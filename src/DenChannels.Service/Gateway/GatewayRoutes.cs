using DenChannels.Service.Channels;

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

    /// <summary>Max length for raw settings JSON exposed in bounded Gateway responses.</summary>
    private const int SettingsPreviewMaxLength = 512;

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
                "POST /api/gateway/test-wakes"
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
                m.CooldownSeconds,
                m.MaxAutoRepliesPerWindow,
                BoundSettingsJson(m.SettingsJson))).ToList();

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
                resolvedChannelId, afterId ?? 0L, pageSize + 1, cancellationToken);

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
                m.DedupeKey,
                m.DeepLink,
                m.Summary,
                m.Body,
                m.CreatedAt)).ToList();

            return Results.Ok(new GatewayEventsDto(eventItems, nextAfterId, hasMore));
        });

        // -----------------------------------------------------------------------
        // POST /api/gateway/system-messages
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
                DedupeKey: request.DedupeKey);

            var msg = await repository.PostMessageAsync(resolvedChannelId, postRequest, cancellationToken);
            return Results.Created($"/api/gateway/messages/{msg.Id}", ToGatewayMessageDto(msg));
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
        m.DedupeKey,
        m.DeepLink,
        m.Summary,
        m.Body,
        m.CreatedAt);

    private static string? BoundSettingsJson(string? settingsJson)
    {
        if (settingsJson is null) return null;
        return settingsJson.Length <= SettingsPreviewMaxLength
            ? settingsJson
            : settingsJson[..SettingsPreviewMaxLength];
    }

    private sealed record GatewayErrorDto(string Code, string Detail);
}
