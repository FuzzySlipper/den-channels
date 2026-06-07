using System.Text.Json;
using DenChannels.Service.Channels;
using DenChannels.Service.DenCore;
using Microsoft.Data.Sqlite;

namespace DenChannels.Service.Mirrors;

public sealed record MirrorEventIngestRequest(IReadOnlyList<MirrorEventDto> Events);

public sealed record MirrorEventDto(
    string EventType,
    string ProjectId,
    string SourceKind,
    string SourceId,
    string? SummaryHint,
    string? DeepLink,
    string? Actor,
    string? Severity,
    string? DedupeKey,
    string? StreamKind,
    Dictionary<string, object?>? Metadata);

public sealed record MirrorIngestResult(int Created, int Duplicates, int Suppressed, IReadOnlyList<ChannelMessageDto> Messages);

public sealed class MirrorSummaryIngestionService
{
    private static readonly HashSet<string> SupportedSourceKinds = new(StringComparer.Ordinal)
    {
        "task_message",
        "agent_stream_entry",
        "notification",
        "worker_run",
        "review_round",
        "review_finding",
        "wake_event",
        "gateway_delivery",
        "external_adapter_message"
    };

    private readonly ProjectChannelSyncService _projectChannelSync;
    private readonly ChannelRepository _repository;

    public MirrorSummaryIngestionService(ProjectChannelSyncService projectChannelSync, ChannelRepository repository)
    {
        _projectChannelSync = projectChannelSync;
        _repository = repository;
    }

    public async Task<MirrorIngestResult> IngestAsync(IReadOnlyList<MirrorEventDto> events,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChannelMessageDto>();
        var duplicates = 0;
        var suppressed = 0;

        foreach (var mirrorEvent in events)
        {
            if (ShouldSuppress(mirrorEvent))
            {
                suppressed++;
                continue;
            }

            if (!SupportedSourceKinds.Contains(mirrorEvent.SourceKind))
                throw new InvalidOperationException($"Unsupported mirror source kind '{mirrorEvent.SourceKind}'.");

            var channel = await _projectChannelSync.EnsureProjectChannelAsync(mirrorEvent.ProjectId, cancellationToken);
            var dedupeKey = string.IsNullOrWhiteSpace(mirrorEvent.DedupeKey)
                ? $"{mirrorEvent.ProjectId}:{mirrorEvent.EventType}:{mirrorEvent.SourceKind}:{mirrorEvent.SourceId}"
                : mirrorEvent.DedupeKey!;

            var existing = await _repository.GetMessageByDedupeKeyAsync(channel.Id, dedupeKey, cancellationToken);
            if (existing is not null)
            {
                duplicates++;
                messages.Add(existing);
                continue;
            }

            try
            {
                messages.Add(await _repository.PostMessageAsync(channel.Id, new PostChannelMessageRequest(
                    SenderType: "system",
                    SenderIdentity: "den-mirror",
                    Body: BuildSummary(mirrorEvent),
                    MessageKind: "mirror_summary",
                    SourceKind: mirrorEvent.SourceKind,
                    SourceId: mirrorEvent.SourceId,
                    SourceProjectId: mirrorEvent.ProjectId,
                    Summary: mirrorEvent.SummaryHint,
                    DeepLink: mirrorEvent.DeepLink,
                    ThreadRootMessageId: null,
                    ReplyToMessageId: null,
                    MetadataJson: BuildMetadataJson(mirrorEvent),
                    DeliveryRequestId: null,
                    DedupeKey: dedupeKey), cancellationToken));
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                var raceExisting = await _repository.GetMessageByDedupeKeyAsync(channel.Id, dedupeKey, cancellationToken);
                if (raceExisting is null)
                    throw;
                duplicates++;
                messages.Add(raceExisting);
            }
        }

        return new MirrorIngestResult(messages.Count - duplicates, duplicates, suppressed, messages);
    }

    private static bool ShouldSuppress(MirrorEventDto mirrorEvent) =>
        string.Equals(mirrorEvent.StreamKind, "debug", StringComparison.OrdinalIgnoreCase) ||
        mirrorEvent.EventType.StartsWith("debug_", StringComparison.OrdinalIgnoreCase) ||
        mirrorEvent.EventType.StartsWith("subagent_work_", StringComparison.OrdinalIgnoreCase);

    private static string BuildSummary(MirrorEventDto mirrorEvent)
    {
        if (!string.IsNullOrWhiteSpace(mirrorEvent.SummaryHint))
            return mirrorEvent.SummaryHint!;

        var actor = string.IsNullOrWhiteSpace(mirrorEvent.Actor) ? "Den" : mirrorEvent.Actor;
        return $"{actor} recorded {mirrorEvent.EventType} for {mirrorEvent.SourceKind} {mirrorEvent.SourceId}.";
    }

    private static string BuildMetadataJson(MirrorEventDto mirrorEvent)
    {
        var metadata = mirrorEvent.Metadata is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(mirrorEvent.Metadata);
        metadata["event_type"] = mirrorEvent.EventType;
        if (!string.IsNullOrWhiteSpace(mirrorEvent.Actor))
            metadata["actor"] = mirrorEvent.Actor;
        if (!string.IsNullOrWhiteSpace(mirrorEvent.Severity))
            metadata["severity"] = mirrorEvent.Severity;
        return JsonSerializer.Serialize(metadata);
    }
}
