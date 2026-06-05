using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DenChannels.Service.Channels;

/// <summary>
/// Channels-owned router for Gateway-shaped activity/breadcrumb writes.
///
/// The canonical write API remains POST /api/channels/{channelId}/activity-events.
/// This service owns the short-lived compatibility shape used by older Gateway/Hermes
/// callers that include channelId in the body or query string, while preserving the
/// old non-waking/soft-failure breadcrumb contract.
/// </summary>
public sealed class ChannelActivityEventRoutingService
{
    private const int MaxDiagnostics = 20;
    private const string DefaultEventType = "lifecycle_status";
    private const string DefaultStatus = "interim";

    private readonly ChannelsRepository _repository;
    private readonly ILogger<ChannelActivityEventRoutingService> _logger;
    private readonly Queue<ChannelActivityDiagnosticDto> _recentDiagnostics = new();
    private readonly object _sync = new();

    public ChannelActivityEventRoutingService(ChannelsRepository repository, ILogger<ChannelActivityEventRoutingService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<ChannelActivityRouteResultDto> RouteAsync(ChannelActivityRouteRequest request,
        string? queryChannelId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channelIdText = FirstNonBlank(queryChannelId, CoerceText(request.ChannelId));
        if (string.IsNullOrWhiteSpace(channelIdText))
        {
            return Rejected("missing_channel_id", "channelId is required.");
        }

        if (!long.TryParse(channelIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var channelId))
        {
            return Rejected("invalid_channel_id", "channelId must be a numeric Channels channel id.");
        }

        var agentIdentity = request.AgentIdentity?.Trim();
        if (string.IsNullOrWhiteSpace(agentIdentity))
        {
            return Rejected("missing_agent_identity", "agentIdentity is required.");
        }

        var appendRequest = new AppendChannelActivityEventRequest(
            ProjectId: request.ProjectId,
            AgentIdentity: agentIdentity,
            DeliveryRequestId: request.DeliveryRequestId,
            SessionKey: request.SessionKey,
            DisplayBlockId: request.DisplayBlockId,
            ParentSessionKey: request.ParentSessionKey,
            ParentAgentIdentity: request.ParentAgentIdentity,
            WorkerRunId: request.WorkerRunId,
            WorkerRole: request.WorkerRole,
            AgentInstanceId: request.AgentInstanceId,
            PoolMemberId: request.PoolMemberId,
            TaskId: request.TaskId,
            ThreadId: request.ThreadId,
            AnchorMessageId: request.AnchorMessageId,
            AssignmentId: request.AssignmentId,
            CheckpointType: request.CheckpointType,
            CheckpointHandle: request.CheckpointHandle,
            EventType: string.IsNullOrWhiteSpace(request.EventType) ? DefaultEventType : request.EventType,
            Status: string.IsNullOrWhiteSpace(request.Status) ? DefaultStatus : request.Status,
            DeliveryStage: request.DeliveryStage,
            Terminal: request.Terminal,
            Sequence: request.Sequence,
            Title: request.Title,
            Summary: request.Summary,
            PreviewJson: request.PreviewJson,
            MetadataJson: request.MetadataJson,
            DedupeKey: request.DedupeKey,
            FinalChannelMessageId: request.FinalChannelMessageId);

        try
        {
            var activityEvent = await _repository.AppendActivityEventAsync(channelId, appendRequest, cancellationToken);
            return new ChannelActivityRouteResultDto(
                Status: "recorded",
                Recorded: true,
                ActivityEventId: activityEvent.Id.ToString(CultureInfo.InvariantCulture),
                ErrorCode: null,
                Message: "Den Channels accepted the activity event.",
                ActivityEvent: activityEvent);
        }
        catch (SqliteException ex)
        {
            var diagnostic = BuildDiagnostic(channelIdText, request, agentIdentity, "activity_record_failed",
                "Den Channels activity event write failed.");
            RecordDiagnostic(diagnostic);
            _logger.LogWarning(ex,
                "Den Channels activity event write failed for channel {ChannelId}, delivery {DeliveryRequestId}, display block {DisplayBlockId}, worker run {WorkerRunId}: {ErrorCode} {Message}",
                diagnostic.ChannelId,
                diagnostic.DeliveryRequestId,
                diagnostic.DisplayBlockId,
                diagnostic.WorkerRunId,
                diagnostic.ErrorCode,
                diagnostic.Message);
            return Degraded(diagnostic);
        }
        catch (InvalidOperationException ex)
        {
            var diagnostic = BuildDiagnostic(channelIdText, request, agentIdentity, "activity_record_exception", ex.Message);
            RecordDiagnostic(diagnostic);
            _logger.LogWarning(ex,
                "Den Channels activity event write threw for channel {ChannelId}, delivery {DeliveryRequestId}, display block {DisplayBlockId}, worker run {WorkerRunId}.",
                diagnostic.ChannelId,
                diagnostic.DeliveryRequestId,
                diagnostic.DisplayBlockId,
                diagnostic.WorkerRunId);
            return Degraded(diagnostic);
        }
    }

    public ChannelActivityRouterStatusDto GetStatus()
    {
        lock (_sync)
        {
            return new ChannelActivityRouterStatusDto(_recentDiagnostics.ToArray());
        }
    }

    private static ChannelActivityRouteResultDto Rejected(string errorCode, string message) => new(
        Status: "rejected",
        Recorded: false,
        ActivityEventId: null,
        ErrorCode: errorCode,
        Message: message,
        ActivityEvent: null);

    private static ChannelActivityRouteResultDto Degraded(ChannelActivityDiagnosticDto diagnostic) => new(
        Status: "degraded",
        Recorded: false,
        ActivityEventId: null,
        ErrorCode: diagnostic.ErrorCode,
        Message: diagnostic.Message,
        ActivityEvent: null);

    private static ChannelActivityDiagnosticDto BuildDiagnostic(string channelId, ChannelActivityRouteRequest request,
        string agentIdentity, string errorCode, string message) => new(
        ObservedAt: DateTimeOffset.UtcNow,
        ChannelId: channelId,
        ProjectId: request.ProjectId,
        AgentIdentity: agentIdentity,
        DeliveryRequestId: request.DeliveryRequestId,
        DisplayBlockId: request.DisplayBlockId,
        WorkerRunId: request.WorkerRunId,
        ErrorCode: errorCode,
        Message: message);

    private void RecordDiagnostic(ChannelActivityDiagnosticDto diagnostic)
    {
        lock (_sync)
        {
            _recentDiagnostics.Enqueue(diagnostic);
            while (_recentDiagnostics.Count > MaxDiagnostics)
            {
                _recentDiagnostics.Dequeue();
            }
        }
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? CoerceText(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                _ => element.ToString()
            };
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}

public sealed record ChannelActivityRouteRequest(
    object? ChannelId,
    string? ProjectId,
    string? AgentIdentity,
    string? DeliveryRequestId,
    string? DisplayBlockId,
    string? SessionKey,
    string? ParentSessionKey,
    string? ParentAgentIdentity,
    string? WorkerRunId,
    string? WorkerRole,
    string? AgentInstanceId,
    string? PoolMemberId,
    long? TaskId,
    long? ThreadId,
    long? AnchorMessageId,
    string? AssignmentId,
    string? CheckpointType,
    string? CheckpointHandle,
    string? EventType,
    string? Status,
    string? DeliveryStage,
    bool? Terminal,
    long? Sequence,
    string? Title,
    string? Summary,
    string? PreviewJson,
    string? MetadataJson,
    string? DedupeKey,
    long? FinalChannelMessageId);

public sealed record ChannelActivityRouteResultDto(
    string Status,
    bool Recorded,
    string? ActivityEventId,
    string? ErrorCode,
    string? Message,
    ChannelActivityEventDto? ActivityEvent);

public sealed record ChannelActivityRouterStatusDto(IReadOnlyList<ChannelActivityDiagnosticDto> RecentFailures);

public sealed record ChannelActivityDiagnosticDto(
    DateTimeOffset ObservedAt,
    string ChannelId,
    string? ProjectId,
    string? AgentIdentity,
    string? DeliveryRequestId,
    string? DisplayBlockId,
    string? WorkerRunId,
    string ErrorCode,
    string Message);
