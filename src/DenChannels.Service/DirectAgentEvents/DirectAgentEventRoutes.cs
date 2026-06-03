using System.Text.Json;
using DenChannels.Service.Channels;

using static DenChannels.Service.EventRecordingStatus;
using static DenChannels.Service.SourceKind;
using static DenChannels.Service.MessageKind;
using CS = DenChannels.Service.ClaimStatus;
using CompS = DenChannels.Service.CompletionStatus;
using SupS = DenChannels.Service.SuppressionStatus;

namespace DenChannels.Service.DirectAgentEvents;

public static class DirectAgentEventRoutes
{
    public static RouteGroupBuilder MapDirectAgentEventRoutes(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/direct-agent-events");

        // -----------------------------------------------------------------------
        // POST /api/direct-agent-events
        // Channels-owned, fully async direct-agent event creation.
        // Returns immediately with durable evidence. No Gateway dependency.
        // -----------------------------------------------------------------------
        group.MapPost("/", async (
            ChannelsRepository repository,
            RecordDirectAgentEventRequest request,
            CancellationToken cancellationToken) =>
        {
            if (request.ChannelId is null && string.IsNullOrWhiteSpace(request.ProjectId))
                return Results.BadRequest(new DirectAgentEventErrorDto("missing_parameter",
                    "Provide channelId or projectId."));

            if (string.IsNullOrWhiteSpace(request.MemberIdentity))
                return Results.BadRequest(new DirectAgentEventErrorDto("missing_member_identity",
                    "Provide memberIdentity for the target agent binding."));

            if (string.IsNullOrWhiteSpace(request.SenderIdentity))
                return Results.BadRequest(new DirectAgentEventErrorDto("missing_sender_identity",
                    "Provide senderIdentity for the direct message request."));

            if (string.IsNullOrWhiteSpace(request.Body))
                return Results.BadRequest(new DirectAgentEventErrorDto("missing_body",
                    "Provide body for the direct message request."));

            var channel = await ResolveChannelAsync(repository, request.ChannelId, request.ProjectId, cancellationToken);
            if (channel is null)
                return Results.NotFound(new DirectAgentEventErrorDto("channel_not_found",
                    request.ChannelId is not null
                        ? $"Channel {request.ChannelId} not found."
                        : $"No default channel found for project '{request.ProjectId}'."));

            var member = await FindActiveAgentMemberAsync(repository, channel.Id, request.MemberIdentity, cancellationToken);
            if (member is null)
                return Results.NotFound(new DirectAgentEventErrorDto("member_not_active_agent",
                    $"Active agent member '{request.MemberIdentity}' is not joined to channel {channel.Id}."));

            var requestId = $"direct-agent-message:{channel.Id}:{Uri.EscapeDataString(member.MemberIdentity)}:{Guid.NewGuid():N}";
            var resolvedSourceProjectId = request.SourceProjectId ?? channel.ProjectId;

            // Build metadata payload with delivery tracking fields
            var metadataPayload = new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["targetMemberIdentity"] = member.MemberIdentity,
                ["targetMemberType"] = member.MemberType,
                ["wakePolicy"] = member.WakePolicy,
                ["deliveryMode"] = "direct_agent_message",
                ["deliveryStatus"] = "recorded_pending_claim",
                ["claimStatus"] = CS.Unclaimed,
                ["completionStatus"] = CompS.Pending,
                ["suppressionStatus"] = SupS.NotSuppressed,
                ["evidence"] = new { gatewayEventsUrl = $"/api/gateway/events?channelId={channel.Id}&afterId=0&limit=50" }
            };
            if (request.SourceProjectId is not null)
                metadataPayload["sourceProjectId"] = request.SourceProjectId;
            if (request.TargetProjectId is not null)
                metadataPayload["targetProjectId"] = request.TargetProjectId;
            if (request.TargetTaskId is not null)
                metadataPayload["targetTaskId"] = request.TargetTaskId;
            if (request.AssignmentId is not null)
                metadataPayload["assignmentId"] = request.AssignmentId;
            if (request.WorkerRunId is not null)
                metadataPayload["workerRunId"] = request.WorkerRunId;
            if (request.WorkerRole is not null)
                metadataPayload["workerRole"] = request.WorkerRole;
            if (request.ProfileIdentity is not null)
                metadataPayload["profileIdentity"] = request.ProfileIdentity;
            if (request.PoolMemberId is not null)
                metadataPayload["poolMemberId"] = request.PoolMemberId;
            if (request.AgentInstanceId is not null)
                metadataPayload["agentInstanceId"] = request.AgentInstanceId;
            if (request.SessionOwnerId is not null)
                metadataPayload["sessionOwnerId"] = request.SessionOwnerId;
            if (request.SessionId is not null)
                metadataPayload["sessionId"] = request.SessionId;
            var metadataJson = JsonSerializer.Serialize(metadataPayload);

            // Post the durable wake_event message
            var msg = await repository.PostMessageAsync(channel.Id, new PostChannelMessageRequest(
                SenderType: "user",
                SenderIdentity: request.SenderIdentity.Trim(),
                Body: request.Body.Trim(),
                MessageKind: HumanText,
                SourceKind: WakeEvent,
                SourceId: requestId,
                SourceProjectId: resolvedSourceProjectId,
                TargetProjectId: request.TargetProjectId,
                TargetTaskId: request.TargetTaskId,
                WorkerRunId: request.WorkerRunId,
                WorkerRole: request.WorkerRole,
                ProfileIdentity: request.ProfileIdentity,
                PoolMemberId: request.PoolMemberId,
                AgentInstanceId: request.AgentInstanceId,
                SessionOwnerId: request.SessionOwnerId,
                SessionId: request.SessionId,
                Summary: $"Direct agent request to {member.MemberIdentity}: recorded, pending claim/completion",
                DeepLink: null,
                ThreadRootMessageId: null,
                ReplyToMessageId: null,
                MetadataJson: metadataJson,
                DeliveryRequestId: null,
                DedupeKey: null,
                AssignmentId: request.AssignmentId,
                CheckpointType: request.CheckpointType,
                CheckpointHandle: request.CheckpointHandle), cancellationToken);


            var eventUrl = $"/api/direct-agent-events/{msg.Id}";
            var eventsUrl = $"/api/gateway/events?channelId={channel.Id}&afterId={Math.Max(0, msg.Id - 1)}&limit=10";

            return Results.Created(eventUrl, new DirectAgentEventDto(
                Status: Recorded,
                EventId: msg.Id,
                ChannelId: channel.Id,
                RequestId: requestId,
                MemberIdentity: member.MemberIdentity,
                WakePolicy: member.WakePolicy,
                SourceProjectId: resolvedSourceProjectId,
                TargetProjectId: request.TargetProjectId,
                TargetTaskId: request.TargetTaskId,
                AssignmentId: request.AssignmentId,
                WorkerRunId: request.WorkerRunId,
                WorkerRole: request.WorkerRole,
                ProfileIdentity: request.ProfileIdentity,
                PoolMemberId: request.PoolMemberId,
                AgentInstanceId: request.AgentInstanceId,
                SessionOwnerId: request.SessionOwnerId,
                SessionId: request.SessionId,
                EventUrl: eventUrl,
                EventsUrl: eventsUrl,
                EvidenceSummary: $"Direct agent wake_event recorded as event {msg.Id}. Gateway/den-host consumers may claim this event. Readback: GET {eventUrl}"));
        });

        // -----------------------------------------------------------------------
        // GET /api/direct-agent-events/{eventId}
        // Readback endpoint for a single recorded direct-agent event.
        // Works without Gateway; uses only Channels data.
        // -----------------------------------------------------------------------
        group.MapGet("/{eventId:long}", async (
            ChannelsRepository repository,
            long eventId,
            CancellationToken cancellationToken) =>
        {
            var msg = await repository.GetMessageAsync(eventId, cancellationToken);
            if (msg is null)
                return Results.NotFound(new DirectAgentEventErrorDto("event_not_found",
                    $"Direct-agent event {eventId} not found."));

            // Only surface wake_event messages through this endpoint
            if (!string.Equals(msg.SourceKind, WakeEvent, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new DirectAgentEventErrorDto("not_a_direct_agent_event",
                    $"Message {eventId} is not a direct-agent event."));

            // Extract direct-agent tracking fields from metadata if present.
            var (deliveryStatus, claimStatus, completionStatus, wakePolicy) = ExtractDirectAgentMetadata(msg.MetadataJson);

            // Resolve member identity from sourceId pattern: direct-agent-message:{channelId}:{memberIdentity}:{guid}
            var memberIdentity = ExtractMemberIdentity(msg.SourceId);

            return Results.Ok(new DirectAgentEventReadbackDto(
                EventId: msg.Id,
                ChannelId: msg.ChannelId,
                RequestId: msg.SourceId ?? string.Empty,
                MessageKind: msg.MessageKind,
                SenderType: msg.SenderType,
                SenderIdentity: msg.SenderIdentity,
                MemberIdentity: memberIdentity ?? string.Empty,
                WakePolicy: wakePolicy ?? string.Empty,
                SourceKind: msg.SourceKind,
                SourceProjectId: msg.SourceProjectId,
                TargetProjectId: msg.TargetProjectId,
                TargetTaskId: msg.TargetTaskId,
                AssignmentId: msg.AssignmentId,
                WorkerRunId: msg.WorkerRunId,
                WorkerRole: msg.WorkerRole,
                ProfileIdentity: msg.ProfileIdentity,
                PoolMemberId: msg.PoolMemberId,
                AgentInstanceId: msg.AgentInstanceId,
                SessionOwnerId: msg.SessionOwnerId,
                SessionId: msg.SessionId,
                Summary: msg.Summary,
                Body: msg.Body,
                DeliveryStatus: deliveryStatus,
                ClaimStatus: claimStatus,
                CompletionStatus: completionStatus,
                CreatedAt: msg.CreatedAt));
        });

        return group;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task<ChannelDto?> ResolveChannelAsync(ChannelsRepository repository, long? channelId,
        string? projectId, CancellationToken cancellationToken)
    {
        if (channelId is not null)
            return await repository.GetChannelAsync(channelId.Value, cancellationToken);

        var channels = await repository.ListChannelsAsync(projectId, "project_default", 1, cancellationToken);
        return channels.Count > 0 ? channels[0] : null;
    }

    private static async Task<ChannelMembershipDto?> FindActiveAgentMemberAsync(ChannelsRepository repository,
        long channelId, string memberIdentity, CancellationToken cancellationToken)
    {
        var members = await repository.ListMembershipsAsync(channelId, 200, cancellationToken);
        return members.FirstOrDefault(m =>
            string.Equals(m.MemberIdentity, memberIdentity.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.MemberType, "agent", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.MembershipStatus, "active", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extract direct-agent tracking fields from the wake_event metadata JSON.
    /// Returns defaults if metadata is missing or malformed.
    /// </summary>
    private static (string? deliveryStatus, string? claimStatus, string? completionStatus, string? wakePolicy)
        ExtractDirectAgentMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return (null, null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            return (
                TryGetString(root, "deliveryStatus"),
                TryGetString(root, "claimStatus"),
                TryGetString(root, "completionStatus"),
                TryGetString(root, "wakePolicy"));
        }
        catch (JsonException)
        {
            return (null, null, null, null);
        }
    }

    /// <summary>
    /// Extract member identity from the sourceId pattern: direct-agent-message:{channelId}:{memberIdentity}:{guid}
    /// </summary>
    private static string? ExtractMemberIdentity(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || !sourceId.StartsWith("direct-agent-message:", StringComparison.Ordinal))
            return null;

        var parts = sourceId.Split(':');
        if (parts.Length >= 3)
            return Uri.UnescapeDataString(parts[2]);

        return null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private sealed record DirectAgentEventErrorDto(string Code, string Detail);
}
