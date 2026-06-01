using System.Text.Json;
using DenChannels.Service.Channels;
using DenChannels.Service.AgentsOverview;

namespace DenChannels.Service.Gateway;

public static class GatewayRoutes
{
    /// <summary>
    /// Valid message kinds that Gateway is allowed to post via system-messages.
    /// Matches the SQLite CHECK constraint on channel_messages.message_kind.
    /// </summary>
    private static readonly HashSet<string> ValidMessageKinds =
    [
        "human_text", "agent_text", "system_event", "mirror_summary", "command", "command_result"
    ];

    public static RouteGroupBuilder MapGatewayRoutes(this IEndpointRouteBuilder endpoints)
    {
        var gw = endpoints.MapGroup("/api/gateway");

        // -----------------------------------------------------------------------
        // GET /api/gateway/health
        // -----------------------------------------------------------------------
        gw.MapGet("/health", () => Results.Ok(new GatewayHealthDto(
            Service: "den-channels",
            Status: "ready",
            Endpoints:
            [
                "GET /api/gateway/health",
                "GET /api/gateway/memberships?channelId={id}",
                "GET /api/gateway/memberships?projectId={projectId}",
                "GET /api/gateway/messages/{messageId}",
                "GET /api/gateway/sources/{sourceKind}/{sourceId}?sourceProjectId={projectId}",
                "GET /api/gateway/events?channelId={id}&afterId={id}&limit={n}",
                "GET /api/gateway/events?projectId={projectId}&afterId={id}&limit={n}",
                "POST /api/gateway/system-messages",
                "POST /api/gateway/channel-activity-events",
                "POST /api/gateway/direct-agent-messages",
                "POST /api/gateway/test-wakes",
                "GET /api/gateway/assignments/{assignmentId}/trace"
            ])));

        // -----------------------------------------------------------------------
        // GET /api/gateway/memberships
        // -----------------------------------------------------------------------
        gw.MapGet("/memberships", async (
            ChannelsRepository repository,
            long? channelId,
            string? projectId,
            CancellationToken cancellationToken) =>
        {
            if (channelId is null && string.IsNullOrWhiteSpace(projectId))
                return Results.BadRequest(new GatewayErrorDto("missing_parameter",
                    "Provide channelId or projectId."));

            ChannelDto? channel;
            if (channelId is not null)
            {
                channel = await repository.GetChannelAsync(channelId.Value, cancellationToken);
                if (channel is null)
                    return Results.NotFound(new GatewayErrorDto("channel_not_found",
                        $"Channel {channelId} not found."));
            }
            else
            {
                var channels = await repository.ListChannelsAsync(projectId, "project_default", 1, cancellationToken);
                channel = channels.Count > 0 ? channels[0] : null;
                if (channel is null)
                    return Results.NotFound(new GatewayErrorDto("channel_not_found",
                        $"No default channel found for project '{projectId}'."));
            }

            var memberships = await repository.ListMembershipsAsync(channel.Id, 200, cancellationToken);
            var members = memberships.Select(m => new GatewayMemberDto(
                m.Id,
                m.MemberType,
                m.MemberIdentity,
                m.MembershipStatus,
                m.WakePolicy,
                m.CanSend,
                m.CanReact,
                m.CanInvite,
                m.CooldownSeconds,
                m.MaxAutoRepliesPerWindow,
                SafeSettingsLabel(m.SettingsJson))).ToList();

            return Results.Ok(new GatewayMembershipsDto(
                channel.Id,
                channel.Slug,
                channel.Kind,
                channel.ProjectId,
                members));
        });

        // -----------------------------------------------------------------------
        // GET /api/gateway/messages/{messageId}
        // -----------------------------------------------------------------------
        gw.MapGet("/messages/{messageId:long}", async (
            ChannelsRepository repository,
            long messageId,
            CancellationToken cancellationToken) =>
        {
            var msg = await repository.GetMessageAsync(messageId, cancellationToken);
            if (msg is null)
                return Results.NotFound(new GatewayErrorDto("message_not_found",
                    $"Message {messageId} not found."));

            return Results.Ok(ToGatewayMessageDto(msg));
        });

        // -----------------------------------------------------------------------
        // GET /api/gateway/sources/{sourceKind}/{sourceId}
        // -----------------------------------------------------------------------
        gw.MapGet("/sources/{sourceKind}/{sourceId}", async (
            ChannelsRepository repository,
            string sourceKind,
            string sourceId,
            string? sourceProjectId,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var messages = await repository.ListMessagesBySourceAsync(
                sourceKind, sourceId, sourceProjectId, limit ?? 50, cancellationToken);
            return Results.Ok(messages.Select(ToGatewayMessageDto).ToList());
        });

        // -----------------------------------------------------------------------
        // GET /api/gateway/events
        // -----------------------------------------------------------------------
        gw.MapGet("/events", async (
            ChannelsRepository repository,
            long? channelId,
            string? projectId,
            long? afterId,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            if (channelId is null && string.IsNullOrWhiteSpace(projectId))
                return Results.BadRequest(new GatewayErrorDto("missing_parameter",
                    "Provide channelId or projectId."));

            long resolvedChannelId;
            if (channelId is not null)
            {
                resolvedChannelId = channelId.Value;
            }
            else
            {
                var channels = await repository.ListChannelsAsync(projectId, "project_default", 1, cancellationToken);
                if (channels.Count == 0)
                    return Results.NotFound(new GatewayErrorDto("channel_not_found",
                        $"No default channel found for project '{projectId}'."));
                resolvedChannelId = channels[0].Id;
            }

            // Fetch one more than requested to determine HasMore.
            // Use afterId ?? 0 so no-cursor requests start from the beginning
            // (ascending cursor mode) rather than the latest-window mode used by
            // the chat UI (which returns the most-recent N messages).
            var pageSize = Math.Clamp(limit ?? 50, 1, 200);
            var fetched = await repository.ListMessagesAsync(
                resolvedChannelId, afterId ?? 0L, null, pageSize + 1, cancellationToken);

            var hasMore = fetched.Count > pageSize;
            var items = hasMore ? fetched.Take(pageSize).ToList() : fetched.ToList();
            long? nextAfterId = hasMore ? items[^1].Id : null;

            var eventItems = items.Select(m => new GatewayEventItemDto(
                m.Id,
                m.ChannelId,
                m.MessageKind,
                m.SenderType,
                m.SenderIdentity,
                m.SourceKind,
                m.SourceId,
                m.SourceProjectId,
                m.DeliveryRequestId,
                m.DedupeKey,
                m.DeepLink,
                m.Summary,
                m.Body,
                m.CreatedAt)).ToList();

            return Results.Ok(new GatewayEventsDto(eventItems, nextAfterId, hasMore));
        });

        // -----------------------------------------------------------------------
        // POST /api/gateway/system-messages
        // Compatibility route for Gateway-generated channel messages. For
        // sourceKind=gateway_delivery + gateway-delivery:{id}:final this writes
        // the gateway_delivery_final_message surface; interim progress belongs
        // in channel-activity-events.
        // -----------------------------------------------------------------------
        gw.MapPost("/system-messages", async (
            ChannelsRepository repository,
            PostGatewaySystemMessageRequest request,
            CancellationToken cancellationToken) =>
        {
            // Validate that channelId or projectId is provided.
            if (request.ChannelId is null && string.IsNullOrWhiteSpace(request.ProjectId))
                return Results.BadRequest(new GatewayErrorDto("missing_parameter",
                    "Provide channelId or projectId."));

            // Validate messageKind if provided.
            var messageKind = request.MessageKind ?? "system_event";
            if (!ValidMessageKinds.Contains(messageKind))
                return Results.BadRequest(new GatewayErrorDto("invalid_message_kind",
                    $"messageKind '{messageKind}' is not valid. Must be one of: {string.Join(", ", ValidMessageKinds)}."));

            // Resolve channel.
            long resolvedChannelId;
            if (request.ChannelId is not null)
            {
                var channel = await repository.GetChannelAsync(request.ChannelId.Value, cancellationToken);
                if (channel is null)
                    return Results.NotFound(new GatewayErrorDto("channel_not_found",
                        $"Channel {request.ChannelId} not found."));
                resolvedChannelId = channel.Id;
            }
            else
            {
                // Route to project default channel; create it if it doesn't exist.
                var existing = await repository.ListChannelsAsync(request.ProjectId, "project_default", 1, cancellationToken);
                ChannelDto defaultChannel;
                if (existing.Count > 0)
                {
                    defaultChannel = existing[0];
                }
                else
                {
                    defaultChannel = await repository.EnsureProjectDefaultChannelAsync(
                        request.ProjectId!, null, cancellationToken);
                }
                resolvedChannelId = defaultChannel.Id;
            }

            // Idempotency: check dedupeKey before inserting.
            if (!string.IsNullOrWhiteSpace(request.DedupeKey))
            {
                var existing = await repository.GetMessageByDedupeKeyAsync(
                    resolvedChannelId, request.DedupeKey, cancellationToken);
                if (existing is not null)
                    return Results.Ok(ToGatewayMessageDto(existing));
            }

            // Post the message.
            var postRequest = new PostChannelMessageRequest(
                SenderType: "system",
                SenderIdentity: request.SenderIdentity ?? "den-gateway",
                Body: request.Body,
                MessageKind: messageKind,
                SourceKind: request.SourceKind,
                SourceId: request.SourceId,
                SourceProjectId: request.SourceProjectId,
                Summary: request.Summary,
                DeepLink: request.DeepLink,
                ThreadRootMessageId: null,
                ReplyToMessageId: null,
                MetadataJson: request.MetadataJson,
                DeliveryRequestId: request.DeliveryRequestId,
                DedupeKey: request.DedupeKey);

            var msg = await repository.PostMessageAsync(resolvedChannelId, postRequest, cancellationToken);
            return Results.Created($"/api/gateway/messages/{msg.Id}", ToGatewayMessageDto(msg));
        });


        // -----------------------------------------------------------------------
        // POST /api/gateway/channel-activity-events
        // Non-waking progress/activity record for Gateway/Hermes delivery runs.
        // -----------------------------------------------------------------------
        gw.MapPost("/channel-activity-events", async (
            ChannelsRepository repository,
            AppendChannelActivityEventRequest request,
            long? channelId,
            string? projectId,
            CancellationToken cancellationToken) =>
        {
            if (channelId is null && string.IsNullOrWhiteSpace(projectId) && string.IsNullOrWhiteSpace(request.ProjectId))
                return Results.BadRequest(new GatewayErrorDto("missing_parameter",
                    "Provide channelId, projectId, or request.projectId."));

            long resolvedChannelId;
            if (channelId is not null)
            {
                var channel = await repository.GetChannelAsync(channelId.Value, cancellationToken);
                if (channel is null)
                    return Results.NotFound(new GatewayErrorDto("channel_not_found",
                        $"Channel {channelId} not found."));
                resolvedChannelId = channel.Id;
            }
            else
            {
                var resolvedProjectId = string.IsNullOrWhiteSpace(projectId) ? request.ProjectId : projectId;
                var existing = await repository.ListChannelsAsync(resolvedProjectId, "project_default", 1, cancellationToken);
                ChannelDto defaultChannel;
                if (existing.Count > 0)
                {
                    defaultChannel = existing[0];
                }
                else
                {
                    defaultChannel = await repository.EnsureProjectDefaultChannelAsync(
                        resolvedProjectId!, null, cancellationToken);
                }
                resolvedChannelId = defaultChannel.Id;
            }

            var activityEvent = await repository.AppendActivityEventAsync(resolvedChannelId, request, cancellationToken);
            return Results.Created($"/api/channels/{resolvedChannelId}/activity-events/{activityEvent.Id}",
                new { status = "recorded", activityEvent });
        });

        // -----------------------------------------------------------------------
        // POST /api/gateway/direct-agent-messages
        // -----------------------------------------------------------------------
        gw.MapPost("/direct-agent-messages", async (
            ChannelsRepository repository,
            GatewayStateClient gatewayStateClient,
            PostGatewayDirectAgentMessageRequest request,
            CancellationToken cancellationToken) =>
        {
            if (request.ChannelId is null && string.IsNullOrWhiteSpace(request.ProjectId))
                return Results.BadRequest(new GatewayErrorDto("missing_parameter",
                    "Provide channelId or projectId."));

            if (string.IsNullOrWhiteSpace(request.MemberIdentity))
                return Results.BadRequest(new GatewayErrorDto("missing_member_identity",
                    "Provide memberIdentity for the target agent binding."));

            if (string.IsNullOrWhiteSpace(request.SenderIdentity))
                return Results.BadRequest(new GatewayErrorDto("missing_sender_identity",
                    "Provide senderIdentity for the direct message request."));

            if (string.IsNullOrWhiteSpace(request.Body))
                return Results.BadRequest(new GatewayErrorDto("missing_body",
                    "Provide body for the direct message request."));

            var channel = await ResolveChannelAsync(repository, request.ChannelId, request.ProjectId, cancellationToken);
            if (channel is null)
                return Results.NotFound(new GatewayErrorDto("channel_not_found",
                    request.ChannelId is not null
                        ? $"Channel {request.ChannelId} not found."
                        : $"No default channel found for project '{request.ProjectId}'."));

            var member = await FindActiveAgentMemberAsync(repository, channel.Id, request.MemberIdentity, cancellationToken);
            if (member is null)
                return Results.NotFound(new GatewayErrorDto("member_not_active_agent",
                    $"Active agent member '{request.MemberIdentity}' is not joined to channel {channel.Id}."));

            var requestId = $"direct-agent-message:{channel.Id}:{Uri.EscapeDataString(member.MemberIdentity)}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            var gatewayEventsUrl = $"/api/gateway/events?channelId={channel.Id}&afterId=0&limit=50";
            var resolvedSourceProjectId = request.SourceProjectId ?? channel.ProjectId;
            var metadataPayload = new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["targetMemberIdentity"] = member.MemberIdentity,
                ["targetMemberType"] = member.MemberType,
                ["wakePolicy"] = member.WakePolicy,
                ["deliveryMode"] = "direct_agent_message",
                ["deliveryStatus"] = "recorded_pending_claim",
                ["claimStatus"] = "unclaimed",
                ["completionStatus"] = "pending",
                ["suppressionStatus"] = "not_suppressed",
                ["evidence"] = new { gatewayEventsUrl }
            };
            if (request.SourceProjectId is not null)
                metadataPayload["sourceProjectId"] = request.SourceProjectId;
            if (request.TargetTaskId is not null)
                metadataPayload["targetTaskId"] = request.TargetTaskId;
            if (request.AssignmentId is not null)
                metadataPayload["assignmentId"] = request.AssignmentId;
            var metadataJson = JsonSerializer.Serialize(metadataPayload);

            var msg = await repository.PostMessageAsync(channel.Id, new PostChannelMessageRequest(
                SenderType: "user",
                SenderIdentity: request.SenderIdentity.Trim(),
                Body: request.Body.Trim(),
                MessageKind: "human_text",
                SourceKind: "wake_event",
                SourceId: requestId,
                SourceProjectId: resolvedSourceProjectId,
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

            var gatewayMessageUrl = $"/api/gateway/messages/{msg.Id}";
            gatewayEventsUrl = $"/api/gateway/events?channelId={channel.Id}&afterId={Math.Max(0, msg.Id - 1)}&limit=10";
            var deliveryProjectId = channel.ProjectId ?? request.ProjectId;
            var pollObservation = await gatewayStateClient.TriggerDeliveryLoopPollAsync(
                deliveryProjectId,
                cancellationToken: cancellationToken);
            var deliveryObservation = await gatewayStateClient.WaitForDirectAgentDeliveryStatusAsync(
                deliveryProjectId,
                member.MemberIdentity,
                requestId,
                request.WaitFor,
                request.TimeoutMs,
                cancellationToken);
            var evidenceSummary = deliveryObservation.EvidenceSummary
                ?? "Direct agent wake_event recorded; Gateway evidence URL exposes delivery request status and follow-up claim/completion/suppression events.";
            if (!pollObservation.Triggered && !string.IsNullOrWhiteSpace(pollObservation.Message))
            {
                evidenceSummary = $"{evidenceSummary} Delivery-loop trigger note: {pollObservation.Message}";
            }

            var parsedAssignmentId = request.AssignmentId is not null && int.TryParse(request.AssignmentId, out var aid) ? aid : (int?)null;
            return Results.Created(gatewayMessageUrl, new GatewayDirectAgentMessageDto(
                Status: "recorded",
                DeliveryStatus: deliveryObservation.DeliveryStatus,
                ClaimStatus: deliveryObservation.ClaimStatus,
                CompletionStatus: deliveryObservation.CompletionStatus,
                SuppressionStatus: deliveryObservation.SuppressionStatus,
                MemberIdentity: member.MemberIdentity,
                WakePolicy: member.WakePolicy,
                MessageId: msg.Id,
                ChannelId: channel.Id,
                RequestId: requestId,
                SourceProjectId: resolvedSourceProjectId,
                TargetTaskId: request.TargetTaskId,
                AssignmentId: parsedAssignmentId,
                DeliveryRequestId: deliveryObservation.DeliveryRequestId,
                AttemptId: deliveryObservation.AttemptId,
                GatewayDeliveryState: deliveryObservation.GatewayDeliveryState,
                GatewayAttemptStatus: deliveryObservation.GatewayAttemptStatus,
                TimedOut: deliveryObservation.TimedOut,
                GatewayUnavailable: deliveryObservation.GatewayUnavailable,
                GatewayMessageUrl: gatewayMessageUrl,
                GatewayEventsUrl: gatewayEventsUrl,
                EvidenceSummary: evidenceSummary));
        });

        // -----------------------------------------------------------------------
        // POST /api/gateway/test-wakes
        // -----------------------------------------------------------------------
        gw.MapPost("/test-wakes", async (
            ChannelsRepository repository,
            PostGatewayTestWakeRequest request,
            CancellationToken cancellationToken) =>
        {
            if (request.ChannelId is null && string.IsNullOrWhiteSpace(request.ProjectId))
                return Results.BadRequest(new GatewayErrorDto("missing_parameter",
                    "Provide channelId or projectId."));

            if (string.IsNullOrWhiteSpace(request.MemberIdentity))
                return Results.BadRequest(new GatewayErrorDto("missing_member_identity",
                    "Provide memberIdentity for the binding to probe."));

            ChannelDto? channel;
            if (request.ChannelId is not null)
            {
                channel = await repository.GetChannelAsync(request.ChannelId.Value, cancellationToken);
                if (channel is null)
                    return Results.NotFound(new GatewayErrorDto("channel_not_found",
                        $"Channel {request.ChannelId} not found."));
            }
            else
            {
                var channels = await repository.ListChannelsAsync(request.ProjectId, "project_default", 1, cancellationToken);
                channel = channels.Count > 0 ? channels[0] : null;
                if (channel is null)
                    return Results.NotFound(new GatewayErrorDto("channel_not_found",
                        $"No default channel found for project '{request.ProjectId}'."));
            }

            var members = await repository.ListMembershipsAsync(channel.Id, 200, cancellationToken);
            var member = members.FirstOrDefault(m =>
                string.Equals(m.MemberIdentity, request.MemberIdentity.Trim(), StringComparison.OrdinalIgnoreCase));
            if (member is null)
                return Results.NotFound(new GatewayErrorDto("member_not_found",
                    $"Member '{request.MemberIdentity}' is not joined to channel {channel.Id}."));

            if (!string.Equals(member.MemberType, "agent", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(member.MembershipStatus, "active", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new GatewayErrorDto("member_not_active_agent",
                    "Only active agent memberships can receive a controlled test wake."));

            var sourceId = $"test-wake:{channel.Id}:{member.Id}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                probe = "channel_agent_test_wake",
                memberIdentity = member.MemberIdentity,
                wakePolicy = member.WakePolicy,
                requestedBy = string.IsNullOrWhiteSpace(request.RequestedBy) ? "den-web" : request.RequestedBy.Trim(),
                note
            });
            var body = $"Controlled test wake recorded for {member.MemberIdentity} ({member.WakePolicy}). Gateway/bridge consumers may claim this wake_event if enabled.";
            var msg = await repository.PostMessageAsync(channel.Id, new PostChannelMessageRequest(
                SenderType: "system",
                SenderIdentity: "den-gateway",
                Body: body,
                MessageKind: "system_event",
                SourceKind: "wake_event",
                SourceId: sourceId,
                SourceProjectId: channel.ProjectId,
                Summary: $"Test wake for {member.MemberIdentity}",
                DeepLink: null,
                ThreadRootMessageId: null,
                ReplyToMessageId: null,
                MetadataJson: metadataJson,
                DeliveryRequestId: null,
                DedupeKey: null), cancellationToken);

            return Results.Created($"/api/gateway/messages/{msg.Id}", new GatewayTestWakeDto(
                Status: "recorded",
                MemberIdentity: member.MemberIdentity,
                WakePolicy: member.WakePolicy,
                MessageId: msg.Id,
                ChannelId: channel.Id,
                GatewayMessageUrl: $"/api/gateway/messages/{msg.Id}",
                GatewayEventsUrl: $"/api/gateway/events?channelId={channel.Id}&afterId={Math.Max(0, msg.Id - 1)}&limit=10",
                EvidenceSummary: "Synthetic wake_event recorded in Den Channels; Gateway/bridge delivery, claim, complete, or fail evidence appears as follow-up channel/gateway events."));
        });

        // -----------------------------------------------------------------------
        // GET /api/gateway/assignments/{assignmentId}/trace
        // Assignment trace aggregate: composes Core worker-pool state, Channels
        // messages/activity, and Gateway delivery evidence for Den Web #1729/#1737.
        // -----------------------------------------------------------------------
        gw.MapGet("/assignments/{assignmentId}/trace", (
            ChannelsRepository repository,
            IWorkerPoolStateClient workerPoolClient,
            string assignmentId,
            string? projectId,
            long? channelId,
            CancellationToken cancellationToken) =>
        {
            return HandleAssignmentTraceAsync(
                repository, workerPoolClient, assignmentId, projectId, channelId, cancellationToken);
        });

        return gw;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GatewayMessageDto ToGatewayMessageDto(ChannelMessageDto m) => new(
        m.Id,
        m.ChannelId,
        m.MessageKind,
        m.SenderType,
        m.SenderIdentity,
        m.SourceKind,
        m.SourceId,
        m.SourceProjectId,
        m.DeliveryRequestId,
        m.DedupeKey,
        m.DeepLink,
        m.Summary,
        m.Body,
        m.CreatedAt);

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

    private static string? SafeSettingsLabel(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            var parts = new List<string>();
            AddAllowedSettingsPart(document.RootElement, parts, "profile", "profile");
            AddAllowedSettingsPart(document.RootElement, parts, "profileName", "profile");
            AddAllowedSettingsPart(document.RootElement, parts, "profile_id", "profile");
            AddAllowedSettingsPart(document.RootElement, parts, "binding", "binding");
            AddAllowedSettingsPart(document.RootElement, parts, "bindingName", "binding");
            AddAllowedSettingsPart(document.RootElement, parts, "sessionId", "session");
            return parts.Count == 0 ? null : string.Join(" · ", parts.Distinct());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AddAllowedSettingsPart(JsonElement root, ICollection<string> parts, string propertyName, string label)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String) return;
        var text = value.GetString()?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
            parts.Add($"{label}: {text}");
    }

    // -------------------------------------------------------------------------
    // Assignment trace helpers
    // -------------------------------------------------------------------------

    private static AssignmentCoreStateDto? ComposeCoreState(WorkerPoolAssignmentTraceCoreDto? coreTrace)
    {
        if (coreTrace is null) return null;

        var assignment = coreTrace.Assignment;
        var responsesByCheckpoint = coreTrace.Responses
            .GroupBy(r => r.CheckpointId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.CreatedAt).FirstOrDefault());

        var checkpoints = coreTrace.Checkpoints
            .OrderBy(c => c.CreatedAt)
            .Select((checkpoint, index) =>
            {
                responsesByCheckpoint.TryGetValue(checkpoint.Id, out var response);
                return new AssignmentCheckpointDto(
                    Sequence: index + 1,
                    CheckpointRequestAt: checkpoint.CreatedAt?.ToString("O"),
                    CheckpointResponseAt: response?.CreatedAt?.ToString("O"),
                    Status: checkpoint.CheckpointType,
                    SnapshotPreview: PreviewPayload(checkpoint.Payload),
                    Error: IsFailureCheckpoint(checkpoint.CheckpointType) ? PreviewPayload(checkpoint.Payload) : null);
            })
            .ToList();

        var isQuarantined = string.Equals(assignment.State, "quarantined", StringComparison.OrdinalIgnoreCase);
        var cleanupRecorded = !string.IsNullOrWhiteSpace(assignment.CleanupEvidence);
        var released = !string.IsNullOrWhiteSpace(assignment.ReleasedAt);
        var terminal = string.Equals(assignment.State, "completed", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(assignment.State, "failed", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(assignment.State, "blocked", StringComparison.OrdinalIgnoreCase)
                       || isQuarantined;

        return new AssignmentCoreStateDto(
            Phase: assignment.State,
            AssignedAt: assignment.CreatedAt?.ToString("O"),
            AssignedAgent: assignment.WorkerIdentity,
            LeaseAcquiredAt: assignment.AcquiredAt,
            LeaseExpiresAt: null,
            Checkpoints: checkpoints.Count > 0 ? checkpoints : null,
            FinalStatus: terminal ? assignment.State : null,
            FinalStatusAt: terminal ? (assignment.ReleasedAt ?? assignment.UpdatedAt?.ToString("O")) : null,
            CleanupState: cleanupRecorded ? "recorded" : null,
            CleanupTriggeredAt: assignment.CleanupRecordedAt,
            CleanupCompletedAt: assignment.CleanupRecordedAt,
            ReleaseState: isQuarantined ? "quarantined" : released ? "released" : null,
            Quarantined: isQuarantined,
            QuarantinedAt: isQuarantined ? (assignment.ReleasedAt ?? assignment.UpdatedAt?.ToString("O")) : null);
    }

    private static bool IsFailureCheckpoint(string checkpointType) =>
        checkpointType.Contains("fail", StringComparison.OrdinalIgnoreCase)
        || checkpointType.Contains("error", StringComparison.OrdinalIgnoreCase);

    private static string? PreviewPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        var normalized = payload.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= 240 ? normalized : normalized[..237] + "...";
    }

    private static AssignmentGatewayEvidenceDto? ExtractGatewayEvidence(
        IReadOnlyList<ChannelMessageDto> messages, long channelId)
    {
        // Direct-agent wake messages may not have a formal delivery_request_id yet,
        // but their metadata contains the Gateway requestId and status fields. Treat
        // that as Gateway evidence so Den Web can distinguish "recorded pending"
        // from true delivery_missing.
        foreach (var message in messages)
        {
            var metadata = TryReadGatewayMetadata(message.MetadataJson);
            var deliveryRequestId = !string.IsNullOrWhiteSpace(message.DeliveryRequestId)
                ? message.DeliveryRequestId
                : metadata.RequestId;
            if (string.IsNullOrWhiteSpace(deliveryRequestId)
                && metadata.DeliveryStatus is null
                && metadata.ClaimStatus is null
                && metadata.CompletionStatus is null
                && metadata.SuppressionStatus is null)
            {
                continue;
            }

            var gatewayMessageUrl = $"/api/gateway/messages/{message.Id}";
            var gatewayEventsUrl = metadata.GatewayEventsUrl
                                   ?? $"/api/gateway/events?channelId={channelId}&afterId={Math.Max(0, message.Id - 1)}&limit=10";

            var evidenceParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(deliveryRequestId)) evidenceParts.Add($"deliveryRequestId: {deliveryRequestId}");
            if (metadata.DeliveryStatus is not null) evidenceParts.Add($"delivery: {metadata.DeliveryStatus}");
            if (metadata.ClaimStatus is not null) evidenceParts.Add($"claim: {metadata.ClaimStatus}");
            if (metadata.CompletionStatus is not null) evidenceParts.Add($"completion: {metadata.CompletionStatus}");
            if (metadata.SuppressionStatus is not null) evidenceParts.Add($"suppression: {metadata.SuppressionStatus}");

            return new AssignmentGatewayEvidenceDto(
                DeliveryRequestId: deliveryRequestId,
                DeliveryStatus: metadata.DeliveryStatus,
                ClaimStatus: metadata.ClaimStatus,
                CompletionStatus: metadata.CompletionStatus,
                SuppressionStatus: metadata.SuppressionStatus,
                RequestedAt: message.CreatedAt,
                DeliveredAt: null,
                ClaimedAt: null,
                CompletedAt: null,
                EvidenceSummary: evidenceParts.Count > 0 ? string.Join(" · ", evidenceParts) : null,
                GatewayMessageUrl: gatewayMessageUrl,
                GatewayEventsUrl: gatewayEventsUrl);
        }

        return null;
    }

    private sealed record GatewayMetadata(
        string? RequestId,
        string? DeliveryStatus,
        string? ClaimStatus,
        string? CompletionStatus,
        string? SuppressionStatus,
        string? GatewayEventsUrl);

    private static GatewayMetadata TryReadGatewayMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return new GatewayMetadata(null, null, null, null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            string? gatewayEventsUrl = null;
            if (root.TryGetProperty("evidence", out var evidence)
                && evidence.ValueKind == JsonValueKind.Object)
            {
                gatewayEventsUrl = TryGetString(evidence, "gatewayEventsUrl");
            }

            return new GatewayMetadata(
                TryGetString(root, "requestId"),
                TryGetString(root, "deliveryStatus"),
                TryGetString(root, "claimStatus"),
                TryGetString(root, "completionStatus"),
                TryGetString(root, "suppressionStatus"),
                gatewayEventsUrl);
        }
        catch (JsonException)
        {
            return new GatewayMetadata(null, null, null, null, null, null);
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private sealed record GatewayErrorDto(string Code, string Detail);

    // -------------------------------------------------------------------------
    // Assignment trace aggregate handler (shared between Gateway and Channel routes)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Handles GET /api/gateway/assignments/{assignmentId}/trace and the
    /// Den Web alias at /api/assignments/{assignmentId}/trace.
    /// Composes Core worker-pool evidence, Channels messages/activity, and
    /// Gateway delivery evidence into a single trace response.
    /// </summary>
    public static async Task<IResult> HandleAssignmentTraceAsync(
        ChannelsRepository repository,
        IWorkerPoolStateClient workerPoolClient,
        string assignmentId,
        string? projectId,
        long? channelId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId) && channelId is null)
            return Results.BadRequest(new GatewayErrorDto("missing_parameter",
                "Provide projectId or channelId."));

        // Resolve channel for scoped database queries.
        var channel = await ResolveChannelAsync(repository, channelId, projectId, cancellationToken);
        if (channel is null)
            return Results.NotFound(new GatewayErrorDto("channel_not_found",
                "No channel found for the given projectId/channelId."));

        var resolvedChannelId = channel.Id;
        var resolvedProjectId = projectId ?? channel.ProjectId ?? string.Empty;

        // -----------------------------------------------------------------------
        // 1. Core worker-pool evidence
        // -----------------------------------------------------------------------
        var coreTrace = await workerPoolClient.FetchAssignmentTraceAsync(assignmentId, cancellationToken);
        var coreStateDto = ComposeCoreState(coreTrace);
        var coreAssignment = coreTrace?.Assignment;
        var coreAvailability = coreTrace is null
            ? TraceSourceAvailability.CoreUnavailable
            : TraceSourceAvailability.Available;

        // -----------------------------------------------------------------------
        // 2. Channel messages tagged with this assignment
        // -----------------------------------------------------------------------
        var channelMessages = await repository.ListMessagesAsync(
            resolvedChannelId, null, assignmentId, 200, cancellationToken);

        string messagesAvailability = channelMessages.Count > 0
            ? TraceSourceAvailability.Available
            : TraceSourceAvailability.NoAssignmentMessages;

        // -----------------------------------------------------------------------
        // 3. Activity events tagged with this assignment
        // -----------------------------------------------------------------------
        var activityEvents = await repository.ListActivityEventsAsync(
            resolvedChannelId, assignmentId: assignmentId, limit: 200, cancellationToken: cancellationToken);

        string activityAvailability = activityEvents.Count > 0
            ? TraceSourceAvailability.Available
            : TraceSourceAvailability.NoActivityEvents;

        // -----------------------------------------------------------------------
        // 4. Gateway delivery evidence from message metadata
        // -----------------------------------------------------------------------
        var gatewayEvidence = ExtractGatewayEvidence(channelMessages, resolvedChannelId);

        string gatewayAvailability = gatewayEvidence is not null
            ? TraceSourceAvailability.Available
            : channelMessages.Count > 0
                ? TraceSourceAvailability.DeliveryMissing
                : TraceSourceAvailability.NoAssignmentMessages;

        // -----------------------------------------------------------------------
        // 5. Summary
        // -----------------------------------------------------------------------
        var summaryParts = new List<string>();
        summaryParts.Add($"Assignment {assignmentId}");

        if (coreAssignment is not null)
            summaryParts.Add($"member: {coreAssignment.WorkerIdentity}");

        if (coreStateDto?.Phase is not null)
            summaryParts.Add($"phase: {coreStateDto.Phase}");

        if (channelMessages.Count > 0)
            summaryParts.Add($"{channelMessages.Count} message(s)");
        else
            summaryParts.Add("no messages");

        if (activityEvents.Count > 0)
            summaryParts.Add($"{activityEvents.Count} activity event(s)");

        var taskId = coreAssignment?.TaskId is not null
            ? coreAssignment.TaskId.Value
            : (long?)null;

        var summary = string.Join(" · ", summaryParts);

        return Results.Ok(new AssignmentTraceResponse(
            AssignmentId: assignmentId,
            ProjectId: coreAssignment?.ProjectId ?? resolvedProjectId,
            ProjectName: null,
            TaskId: taskId,
            TaskTitle: null,
            AgentIdentity: coreAssignment?.WorkerIdentity,
            WorkerRunId: coreAssignment?.RunId,
            WorkerRole: coreAssignment?.Role,
            CoreAvailability: coreAvailability,
            GatewayAvailability: gatewayAvailability,
            MessagesAvailability: messagesAvailability,
            ActivityAvailability: activityAvailability,
            CoreState: coreStateDto,
            GatewayEvidence: gatewayEvidence,
            ChannelMessages: channelMessages.Cast<object>().ToList(),
            ActivityEvents: activityEvents.Cast<object>().ToList(),
            Summary: summary));
    }
}
