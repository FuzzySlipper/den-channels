using System.Text.Json;
using DenChannels.Service.Channels;
using DenChannels.Service.Subscriptions;

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
            SubscriptionRepository subscriptionRepo,
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

            var targetMemberIdentity = request.MemberIdentity.Trim();
            var subscriptionState = await DirectAgentEventShared.ResolveSubscriptionStateAsync(
                subscriptionRepo, channel.Id, targetMemberIdentity, cancellationToken);
            const string targetMemberType = "agent";
            const string wakePolicy = "subscription";

            var requestId = $"direct-agent-message:{channel.Id}:{Uri.EscapeDataString(targetMemberIdentity)}:{Guid.NewGuid():N}";
            var resolvedSourceProjectId = request.SourceProjectId ?? channel.ProjectId;
            var gatewayEventsUrl = $"/api/direct-agent-events?channelId={channel.Id}&afterId=0&limit=50";

            var metadataPayload = DirectAgentEventShared.BuildWakeMetadata(
                requestId, targetMemberIdentity, targetMemberType, wakePolicy, resolvedSourceProjectId,
                request.SourceProjectId, request.TargetProjectId, request.TargetTaskId,
                request.AssignmentId, request.WorkerRunId, request.WorkerRole,
                request.ProfileIdentity, request.PoolMemberId,
                request.AgentInstanceId, request.SessionOwnerId, request.SessionId,
                gatewayEventsUrl, subscriptionState);

            // Merge caller-supplied coordination-call metadata under a controlled namespace.
            string? coordinationCallId = null;
            string? requestKind = null;
            string? resultDestinationJson = null;
            if (!string.IsNullOrWhiteSpace(request.MetadataJson))
            {
                try
                {
                    using var callerDoc = JsonDocument.Parse(request.MetadataJson);
                    var callerRoot = callerDoc.RootElement;
                    if (callerRoot.ValueKind != JsonValueKind.Object)
                        return Results.BadRequest(new DirectAgentEventErrorDto("invalid_metadata",
                            "MetadataJson must be a JSON object."));

                    // Merge under controlled namespace to prevent overwriting system keys.
                    metadataPayload["callerMetadata"] = JsonSerializer.Deserialize<JsonElement>(callerRoot.GetRawText());

                    // Extract coordination-call fields if present (they are surfaced in readback).
                    if (callerRoot.TryGetProperty("coordinationCallId", out var ccid) && ccid.ValueKind == JsonValueKind.String)
                        coordinationCallId = ccid.GetString();
                    if (callerRoot.TryGetProperty("requestKind", out var rk) && rk.ValueKind == JsonValueKind.String)
                        requestKind = rk.GetString();
                    if (callerRoot.TryGetProperty("resultDestinationJson", out var rdj) && rdj.ValueKind == JsonValueKind.String)
                        resultDestinationJson = rdj.GetString();
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new DirectAgentEventErrorDto("malformed_metadata",
                        "MetadataJson must be valid JSON."));
                }
            }

            var metadataJson = JsonSerializer.Serialize(metadataPayload);

            var msg = await DirectAgentEventShared.PostWakeMessageAsync(
                repository, channel.Id,
                request.SenderIdentity, request.Body, requestId,
                resolvedSourceProjectId, request.TargetProjectId, request.TargetTaskId,
                request.WorkerRunId, request.WorkerRole,
                request.ProfileIdentity, request.PoolMemberId,
                request.AgentInstanceId, request.SessionOwnerId, request.SessionId,
                request.AssignmentId, request.CheckpointType, request.CheckpointHandle,
                targetMemberIdentity, metadataJson, cancellationToken);

            var eventUrl = $"/api/direct-agent-events/{msg.Id}";
            var eventsUrl = $"/api/direct-agent-events?channelId={channel.Id}&afterId={Math.Max(0, msg.Id - 1)}&limit=10";

            return Results.Created(eventUrl, new DirectAgentEventDto(
                Status: Recorded,
                EventId: msg.Id,
                ChannelId: channel.Id,
                RequestId: requestId,
                MemberIdentity: targetMemberIdentity,
                WakePolicy: wakePolicy,
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
                EvidenceSummary: $"Direct agent wake_event recorded as event {msg.Id}. Subscription readback: {subscriptionState.DeliveryStatus}/{subscriptionState.ClaimStatus}/{subscriptionState.CompletionStatus}. GET {eventUrl}",
                DeliveryStatus: subscriptionState.DeliveryStatus,
                ClaimStatus: subscriptionState.ClaimStatus,
                CompletionStatus: subscriptionState.CompletionStatus,
                ActiveSubscriptionCount: subscriptionState.ActiveSubscriptionCount,
                SubscriptionStatuses: subscriptionState.SubscriptionStatuses,
                SubscriptionIdentities: subscriptionState.SubscriptionIdentities,
                CoordinationCallId: coordinationCallId,
                RequestKind: requestKind,
                ResultDestinationJson: resultDestinationJson));
        });

        // -----------------------------------------------------------------------
        // GET /api/direct-agent-events/{eventId}
        // Readback endpoint for a single recorded direct-agent event.
        // Works without Gateway; uses only Channels data.
        // -----------------------------------------------------------------------
        group.MapGet("/{eventId:long}", async (
            ChannelsRepository repository,
            SubscriptionRepository subscriptionRepo,
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
            var (metadataDeliveryStatus, metadataClaimStatus, metadataCompletionStatus, wakePolicy) =
                DirectAgentEventShared.ExtractDirectAgentMetadata(msg.MetadataJson);

            // Resolve member identity from sourceId pattern: direct-agent-message:{channelId}:{memberIdentity}:{guid}
            var memberIdentity = DirectAgentEventShared.ExtractMemberIdentity(msg.SourceId);
            DirectAgentEventShared.DirectAgentSubscriptionState? subscriptionState = null;
            if (!string.IsNullOrWhiteSpace(memberIdentity))
            {
                subscriptionState = await DirectAgentEventShared.ResolveSubscriptionStateAsync(
                    subscriptionRepo, msg.ChannelId, memberIdentity, cancellationToken);
            }

            var deliveryStatus = subscriptionState?.DeliveryStatus ?? metadataDeliveryStatus;
            var claimStatus = subscriptionState?.ClaimStatus ?? metadataClaimStatus;
            var completionStatus = subscriptionState?.CompletionStatus ?? metadataCompletionStatus;
            var activeSubscriptionCount = subscriptionState?.ActiveSubscriptionCount ?? 0;
            var subscriptionStatuses = subscriptionState?.SubscriptionStatuses ?? Array.Empty<string>();
            var subscriptionIdentities = subscriptionState?.SubscriptionIdentities ?? Array.Empty<string>();

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
                ActiveSubscriptionCount: activeSubscriptionCount,
                SubscriptionStatuses: subscriptionStatuses,
                SubscriptionIdentities: subscriptionIdentities,
                CreatedAt: msg.CreatedAt));
        });

        return group;
    }

    private sealed record DirectAgentEventErrorDto(string Code, string Detail);
}
