using System.Text.Json;
using DenChannels.Service.Channels;

using static DenChannels.Service.EventRecordingStatus;
using static DenChannels.Service.SourceKind;
using static DenChannels.Service.MessageKind;

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

            var channel = await DirectAgentEventShared.ResolveChannelAsync(
                repository, request.ChannelId, request.ProjectId, cancellationToken);
            if (channel is null)
                return Results.NotFound(new DirectAgentEventErrorDto("channel_not_found",
                    request.ChannelId is not null
                        ? $"Channel {request.ChannelId} not found."
                        : $"No default channel found for project '{request.ProjectId}'."));

            var member = await DirectAgentEventShared.FindActiveAgentMemberAsync(
                repository, channel.Id, request.MemberIdentity, cancellationToken);
            if (member is null)
                return Results.NotFound(new DirectAgentEventErrorDto("member_not_active_agent",
                    $"Active agent member '{request.MemberIdentity}' is not joined to channel {channel.Id}."));

            var hasActiveSubscription = await repository.HasActiveSubscriptionAsync(
                channel.Id, member.MemberIdentity, cancellationToken);

            var requestId = $"direct-agent-message:{channel.Id}:{Uri.EscapeDataString(member.MemberIdentity)}:{Guid.NewGuid():N}";
            var resolvedSourceProjectId = request.SourceProjectId ?? channel.ProjectId;
            var gatewayEventsUrl = $"/api/direct-agent-events?channelId={channel.Id}&afterId=0&limit=50";

            var metadataPayload = DirectAgentEventShared.BuildWakeMetadata(
                requestId, member, resolvedSourceProjectId,
                request.SourceProjectId, request.TargetProjectId, request.TargetTaskId,
                request.AssignmentId, request.WorkerRunId, request.WorkerRole,
                request.ProfileIdentity, request.PoolMemberId,
                request.AgentInstanceId, request.SessionOwnerId, request.SessionId,
                gatewayEventsUrl,
                hasActiveSubscription: hasActiveSubscription);

            var metadataJson = JsonSerializer.Serialize(metadataPayload);

            var msg = await DirectAgentEventShared.PostWakeMessageAsync(
                repository, channel.Id,
                request.SenderIdentity, request.Body, requestId,
                resolvedSourceProjectId, request.TargetProjectId, request.TargetTaskId,
                request.WorkerRunId, request.WorkerRole,
                request.ProfileIdentity, request.PoolMemberId,
                request.AgentInstanceId, request.SessionOwnerId, request.SessionId,
                request.AssignmentId, request.CheckpointType, request.CheckpointHandle,
                member.MemberIdentity, metadataJson, cancellationToken);

            var eventUrl = $"/api/direct-agent-events/{msg.Id}";
            var eventsUrl = $"/api/direct-agent-events?channelId={channel.Id}&afterId={Math.Max(0, msg.Id - 1)}&limit=10";

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
