using DenChannels.Service.Channels;

using static DenChannels.Service.SourceKind;

namespace DenChannels.Service.DirectAgentEvents;

public static class DirectAgentEventRoutes
{
    public static RouteGroupBuilder MapDirectAgentEventRoutes(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/direct-agent-events");

        // -----------------------------------------------------------------------
        // GET /api/direct-agent-events
        // Events list endpoint — cursor-paged event subscription.
        // Migrated from /api/gateway/events (owned by Gateway compatibility routes).
        // den-host's EventsListPath should reference this path.
        // -----------------------------------------------------------------------
        group.MapGet("/", async (
            ChannelsRepository repository,
            long? channelId,
            string? projectId,
            long? afterId,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            if (channelId is null && string.IsNullOrWhiteSpace(projectId))
                return Results.BadRequest(new DirectAgentEventErrorDto("missing_parameter",
                    "Provide channelId or projectId."));

            var channel = await DirectAgentEventShared.ResolveChannelAsync(
                repository, channelId, projectId, cancellationToken);
            if (channel is null)
                return Results.NotFound(new DirectAgentEventErrorDto("channel_not_found",
                    channelId is not null
                        ? $"Channel {channelId} not found."
                        : $"No default channel found for project '{projectId}'."));

            var pageSize = Math.Clamp(limit ?? 50, 1, 200);
            var fetched = await repository.ListMessagesAsync(
                channel.Id, afterId ?? 0L, null, pageSize + 1, cancellationToken);

            var hasMore = fetched.Count > pageSize;
            var items = hasMore ? fetched.Take(pageSize).ToList() : fetched.ToList();
            long? nextAfterId = hasMore ? items[^1].Id : null;

            var eventItems = items.Select(m => new DirectAgentEventListItemDto(
                m.Id, m.ChannelId, m.MessageKind, m.SenderType, m.SenderIdentity,
                m.SourceKind, m.SourceId, m.SourceProjectId,
                m.TargetProjectId, m.TargetTaskId, m.AssignmentId,
                m.WorkerRunId, m.WorkerRole, m.ProfileIdentity, m.PoolMemberId,
                m.AgentInstanceId, m.SessionOwnerId, m.SessionId,
                m.DeliveryRequestId, m.DedupeKey, m.DeepLink,
                m.Summary, m.Body, m.CreatedAt)).ToList();

            return Results.Ok(new DirectAgentEventListResponse(eventItems, nextAfterId, hasMore));
        });

        // -----------------------------------------------------------------------
        // POST /api/direct-agent-events — RETIRED (task #3025)
        // Executable wake intents now belong to the Delivery successor. This
        // readback service keeps historical wake_event evidence only.
        // -----------------------------------------------------------------------
        group.MapPost("/", () => DirectAgentEventShared.RetiredWakeWriteTombstone("POST /api/direct-agent-events"));

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
            var (deliveryStatus, claimStatus, completionStatus, wakePolicy) =
                DirectAgentEventShared.ExtractDirectAgentMetadata(msg.MetadataJson);

            // Resolve member identity from sourceId pattern: direct-agent-message:{channelId}:{memberIdentity}:{guid}
            var memberIdentity = DirectAgentEventShared.ExtractMemberIdentity(msg.SourceId);

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

    private sealed record DirectAgentEventErrorDto(string Code, string Detail);
}
