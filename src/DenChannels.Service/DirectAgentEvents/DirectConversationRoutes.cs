using DenChannels.Service.Channels;
using DenChannels.Service.Configuration;
using Microsoft.Extensions.Options;

namespace DenChannels.Service.DirectAgentEvents;

public static class DirectConversationRoutes
{
    public static RouteGroupBuilder MapDirectConversationRoutes(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/direct-conversations");

        // ---------------------------------------------------------------
        // GET /api/direct-conversations?humanIdentity=xxx
        // List conversations for a human reader
        // ---------------------------------------------------------------
        group.MapGet("/", async (
            ChannelsRepository repository,
            string humanIdentity,
            int? limit,
            long? afterId,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(humanIdentity))
                return Results.BadRequest(new DirectConversationErrorDto("missing_human_identity",
                    "Provide humanIdentity."));

            var pageSize = Math.Clamp(limit ?? 50, 1, 200);
            var conversations = await repository.ListConversationsAsync(
                humanIdentity.Trim(), pageSize + 1, afterId, cancellationToken);

            var hasMore = conversations.Count > pageSize;
            var items = hasMore ? conversations.Take(pageSize).ToList() : conversations.ToList();
            long? nextCursor = hasMore ? items[^1].Id : null;

            return Results.Ok(new DirectConversationListResponse(items, nextCursor, hasMore));
        });

        // ---------------------------------------------------------------
        // POST /api/direct-conversations
        // Get or create a conversation for humanIdentity + agentIdentity
        // ---------------------------------------------------------------
        group.MapPost("/", async (
            ChannelsRepository repository,
            CreateDirectConversationRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.HumanIdentity))
                return Results.BadRequest(new DirectConversationErrorDto("missing_human_identity",
                    "Provide humanIdentity."));
            if (string.IsNullOrWhiteSpace(request.AgentIdentity))
                return Results.BadRequest(new DirectConversationErrorDto("missing_agent_identity",
                    "Provide agentIdentity."));

            var conversation = await repository.GetOrCreateConversationAsync(request, cancellationToken);
            return Results.Ok(conversation);
        });

        // ---------------------------------------------------------------
        // GET /api/direct-conversations/{conversationId}
        // ---------------------------------------------------------------
        group.MapGet("/{conversationId:long}", async (
            ChannelsRepository repository,
            long conversationId,
            CancellationToken cancellationToken) =>
        {
            var conversation = await repository.GetConversationAsync(conversationId, cancellationToken);
            return conversation is not null
                ? Results.Ok(conversation)
                : Results.NotFound(new DirectConversationErrorDto("conversation_not_found",
                    $"Conversation {conversationId} not found."));
        });

        // ---------------------------------------------------------------
        // GET /api/direct-conversations/{conversationId}/entries
        // List transcript entries with pagination
        // ---------------------------------------------------------------
        group.MapGet("/{conversationId:long}/entries", async (
            ChannelsRepository repository,
            long conversationId,
            int? limit,
            long? afterId,
            CancellationToken cancellationToken) =>
        {
            var conversation = await repository.GetConversationAsync(conversationId, cancellationToken);
            if (conversation is null)
                return Results.NotFound(new DirectConversationErrorDto("conversation_not_found",
                    $"Conversation {conversationId} not found."));

            var pageSize = Math.Clamp(limit ?? 50, 1, 200);
            var entries = await repository.ListConversationEntriesAsync(
                conversationId, pageSize + 1, afterId, cancellationToken);

            var hasMore = entries.Count > pageSize;
            var items = hasMore ? entries.Take(pageSize).ToList() : entries.ToList();
            long? nextCursor = hasMore ? items[^1].Id : null;

            return Results.Ok(new DirectConversationEntryListResponse(items, nextCursor, hasMore));
        });

        // ---------------------------------------------------------------
        // POST /api/direct-conversations/{conversationId}/send — RETIRED (task #3025)
        // DM-shaped executable wake production now goes through the Conversation
        // and Delivery successors; this API remains for legacy readback only.
        // ---------------------------------------------------------------
        group.MapPost("/{conversationId:long}/send", () =>
            DirectAgentEventShared.RetiredWakeWriteTombstone("POST /api/direct-conversations/{conversationId}/send"));

        // ---------------------------------------------------------------
        // PUT /api/direct-conversations/{conversationId}/read-cursor
        // Update the read cursor for a conversation
        // ---------------------------------------------------------------
        group.MapPut("/{conversationId:long}/read-cursor", async (
            ChannelsRepository repository,
            long conversationId,
            UpsertDirectReadCursorRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.ReaderIdentity))
                return Results.BadRequest(new DirectConversationErrorDto("missing_reader_identity",
                    "Provide readerIdentity."));

            var conversation = await repository.GetConversationAsync(conversationId, cancellationToken);
            if (conversation is null)
                return Results.NotFound(new DirectConversationErrorDto("conversation_not_found",
                    $"Conversation {conversationId} not found."));

            var cursor = await repository.UpsertReadCursorAsync(conversationId, request, cancellationToken);
            return Results.Ok(cursor);
        });

        // ---------------------------------------------------------------
        // GET /api/direct-conversations/{conversationId}/read-cursor?readerIdentity=xxx
        // ---------------------------------------------------------------
        group.MapGet("/{conversationId:long}/read-cursor", async (
            ChannelsRepository repository,
            long conversationId,
            string readerIdentity,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(readerIdentity))
                return Results.BadRequest(new DirectConversationErrorDto("missing_reader_identity",
                    "Provide readerIdentity."));

            var cursor = await repository.GetReadCursorAsync(conversationId, readerIdentity.Trim(), cancellationToken);
            if (cursor is null)
                return Results.Ok(new { conversationId, readerIdentity, lastReadEntryId = (long?)null, hasUnread = false });

            var unread = await repository.GetUnreadEntryCountAsync(conversationId, cursor.LastReadEntryId, cancellationToken);
            return Results.Ok(new { conversationId, readerIdentity, cursor.LastReadEntryId, unreadCount = unread, hasUnread = unread > 0 });
        });

        // ---------------------------------------------------------------
        // POST /api/direct-conversations/{conversationId}/link-message
        // Link an existing canonical channel_message into the DM transcript.
        // Used by the host/bridge to link agent responses carrying
        // directConversationId metadata. Does NOT derive session identity
        // from direct_conversation_id.
        // ---------------------------------------------------------------
        group.MapPost("/{conversationId:long}/link-message", async (
            ChannelsRepository repository,
            IOptions<DenChannelsOptions> options,
            long conversationId,
            LinkDirectMessageRequest request,
            CancellationToken cancellationToken) =>
        {
            if (options.Value.LegacyDisplayHistory.TombstoneArchivedRoutes)
                return LegacyRouteTombstone.Gone("Conversation/Delivery successors; direct-conversation history is preserved for readback");

            var conversation = await repository.GetConversationAsync(conversationId, cancellationToken);
            if (conversation is null)
                return Results.NotFound(new DirectConversationErrorDto("conversation_not_found",
                    $"Conversation {conversationId} not found."));

            if (request.ChannelMessageId <= 0)
                return Results.BadRequest(new DirectConversationErrorDto("invalid_channel_message_id",
                    "Provide a valid channelMessageId."));
            if (string.IsNullOrWhiteSpace(request.SenderIdentity))
                return Results.BadRequest(new DirectConversationErrorDto("missing_sender_identity",
                    "Provide senderIdentity."));
            if (string.IsNullOrWhiteSpace(request.RecipientIdentity))
                return Results.BadRequest(new DirectConversationErrorDto("missing_recipient_identity",
                    "Provide recipientIdentity."));
            if (request.Direction is not "agent_to_human" and not "human_to_agent" and not "system_note")
                return Results.BadRequest(new DirectConversationErrorDto("invalid_direction",
                    "Direction must be agent_to_human, human_to_agent, or system_note."));

            var entry = await repository.LinkMessageToConversationAsync(
                conversationId, request.ChannelMessageId, request.Direction,
                request.SenderIdentity, request.RecipientIdentity,
                request.BodyPreview, cancellationToken);

            return Results.Created($"/api/direct-conversations/{conversationId}/entries/{entry.Id}", entry);
        });

        return group;
    }
}
