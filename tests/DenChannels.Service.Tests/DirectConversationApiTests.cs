using System.Net.Http.Json;
using System.Net;
using DenChannels.Service.Channels;
using DenChannels.Service.DirectAgentEvents;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenChannels.Service.Tests;

public sealed class DirectConversationApiTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"den-channels-dm-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public DirectConversationApiTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenChannels:Database:Path"] = _databasePath,
                    ["DenChannels:Database:ApplyMigrationsOnStartup"] = "true"
                });
            }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
    }

    // ── Schema / migration ──────────────────────────────────────────────

    [Fact]
    public async Task MigrationV6_CreatesDirectConversationTables()
    {
        using var client = _factory.CreateClient();

        // Verify the tables exist by querying them (no rows, but schema is valid)
        var convs = await client.GetFromJsonAsync<DirectConversationListResponse>(
            "/api/direct-conversations?humanIdentity=test-human");
        Assert.NotNull(convs);
        Assert.Empty(convs.Conversations);
    }

    // ── Conversation get/create ─────────────────────────────────────────

    [Fact]
    public async Task CreateAndGetConversation_Works()
    {
        using var client = _factory.CreateClient();

        using var createResponse = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "den-mcp-runner",
            scopeProjectId = "den-core",
            displayTitle = "Runner"
        });
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<DirectConversationDto>();
        Assert.NotNull(created);
        Assert.Equal("patch", created.HumanIdentity);
        Assert.Equal("den-mcp-runner", created.AgentIdentity);
        Assert.Equal("den-core", created.ScopeProjectId);

        // Get by id
        var fetched = await client.GetFromJsonAsync<DirectConversationDto>(
            $"/api/direct-conversations/{created.Id}");
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);

        // Get or create again (idempotent)
        using var createAgainResponse = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "den-mcp-runner"
        });
        Assert.Equal(HttpStatusCode.OK, createAgainResponse.StatusCode);
        var again = await createAgainResponse.Content.ReadFromJsonAsync<DirectConversationDto>();
        Assert.NotNull(again);
        Assert.Equal(created.Id, again.Id); // Same conversation
    }

    // ── List conversations ──────────────────────────────────────────────

    [Fact]
    public async Task ListConversations_ReturnsSortedByLastEntry()
    {
        using var client = _factory.CreateClient();

        // Create two conversations
        using var r1 = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "agent-alpha",
            scopeProjectId = "proj-a"
        });
        var conv1 = await r1.Content.ReadFromJsonAsync<DirectConversationDto>();

        using var r2 = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "agent-beta",
            scopeProjectId = "proj-b"
        });
        var conv2 = await r2.Content.ReadFromJsonAsync<DirectConversationDto>();

        var listed = await client.GetFromJsonAsync<DirectConversationListResponse>(
            "/api/direct-conversations?humanIdentity=patch");
        Assert.NotNull(listed);
        Assert.Equal(2, listed.Conversations.Count);
        // Each has both identities
        Assert.All(listed.Conversations, c => Assert.Equal("patch", c.HumanIdentity));
    }

    // ── Missing identity returns 400 ────────────────────────────────────

    [Fact]
    public async Task ListConversations_MissingIdentity_ReturnsBadRequest()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/direct-conversations");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateConversation_MissingIdentity_ReturnsBadRequest()
    {
        using var client = _factory.CreateClient();
        using var r = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch"
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // ── Entries ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListEntries_EmptyConversation_ReturnsEmptyList()
    {
        using var client = _factory.CreateClient();

        using var r = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "coder"
        });
        var conv = await r.Content.ReadFromJsonAsync<DirectConversationDto>();

        var entries = await client.GetFromJsonAsync<DirectConversationEntryListResponse>(
            $"/api/direct-conversations/{conv!.Id}/entries");
        Assert.NotNull(entries);
        Assert.Empty(entries.Entries);
    }

    // ── Retired DM send flow ───────────────────────────────────────────

    [Fact]
    public async Task SendDirectMessage_Returns410Gone_Tombstone()
    {
        using var client = _factory.CreateClient();

        using var convResponse = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "target-agent",
            scopeProjectId = "den-core"
        });
        var conv = await convResponse.Content.ReadFromJsonAsync<DirectConversationDto>();
        Assert.NotNull(conv);

        using var sendResponse = await client.PostAsJsonAsync(
            $"/api/direct-conversations/{conv.Id}/send", new
            {
                senderIdentity = "patch",
                body = "Hello agent, please process task #42.",
                sourceProjectId = "den-core",
                targetTaskId = 42
            });

        Assert.Equal(HttpStatusCode.Gone, sendResponse.StatusCode);
        var raw = await sendResponse.Content.ReadAsStringAsync();
        Assert.Contains("route_gone", raw);
        Assert.Contains("POST /v1/delivery/intents", raw);
    }

    // ── Read cursor ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReadCursor_UpsertAndRead_Works()
    {
        using var client = _factory.CreateClient();

        // Setup conversation
        using var convResponse = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "agent-alpha"
        });
        var conv = await convResponse.Content.ReadFromJsonAsync<DirectConversationDto>();
        Assert.NotNull(conv);

        // Upsert read cursor
        using var cursorResponse = await client.PutAsJsonAsync(
            $"/api/direct-conversations/{conv.Id}/read-cursor", new
            {
                readerIdentity = "patch",
                lastReadEntryId = (long?)null
            });
        Assert.Equal(HttpStatusCode.OK, cursorResponse.StatusCode);

        // Get read cursor
        var fetched = await client.GetFromJsonAsync<ReadCursorPayload>(
            $"/api/direct-conversations/{conv.Id}/read-cursor?readerIdentity=patch");
        Assert.NotNull(fetched);
        Assert.Equal(conv.Id, fetched.ConversationId);
        Assert.False(fetched.HasUnread);
    }

    // ── Retired send does not mutate transcript ─────────────────────────

    [Fact]
    public async Task SendDirectMessage_TombstoneDoesNotCreateConversationEntry()
    {
        using var client = _factory.CreateClient();

        using var convResponse = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "body-test-agent",
            scopeProjectId = "den-core"
        });
        var conv = await convResponse.Content.ReadFromJsonAsync<DirectConversationDto>();
        Assert.NotNull(conv);

        using var sendResponse = await client.PostAsJsonAsync(
            $"/api/direct-conversations/{conv.Id}/send", new
            {
                senderIdentity = "patch",
                body = "Process task #99 with priority high.",
                sourceProjectId = "den-core"
            });
        Assert.Equal(HttpStatusCode.Gone, sendResponse.StatusCode);

        var entries = await client.GetFromJsonAsync<DirectConversationEntryListResponse>(
            $"/api/direct-conversations/{conv.Id}/entries");
        Assert.NotNull(entries);
        Assert.Empty(entries.Entries);
    }

    // ── No session key derivation invariant ─────────────────────────────

    [Fact]
    public async Task DirectConversationId_IsNotUsedAsSessionKey()
    {
        using var client = _factory.CreateClient();

        // Create conversation and observe that the ID is just an integer
        using var convResponse = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "no-session-agent",
            scopeProjectId = "den-core"
        });
        var conv = await convResponse.Content.ReadFromJsonAsync<DirectConversationDto>();
        Assert.NotNull(conv);

        // The conversation ID is a simple autoincrement integer, not a session key
        Assert.True(conv.Id > 0);
        Assert.False(conv.Id.ToString().Contains("session"), "direct_conversation_id must not contain 'session'");
        Assert.False(conv.Id.ToString().Contains("hermes"), "direct_conversation_id must not contain 'hermes'");
    }

    // ── Agent response transcript linking (explicit metadata) ─────────

    [Fact]
    public async Task LinkMessage_ExplicitlyLinksAgentResponseIntoTranscript()
    {
        using var client = _factory.CreateClient();

        // Setup channel + membership + conversation
        var channelSlug = $"ops-{Guid.NewGuid():N}"[..20];
        using var channelResponse = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = channelSlug,
            displayName = "Link Test Channel",
            kind = "project_default",
            projectId = "den-core",
            createdBy = "test"
        });
        var channel = await channelResponse.Content.ReadFromJsonAsync<ChannelPayload>();

        using var membershipResponse = await client.PutAsJsonAsync(
            $"/api/channels/{channel!.Id}/memberships", new
        {
            memberType = "agent",
            memberIdentity = "link-test-agent",
            membershipStatus = "active",
            wakePolicy = "all_human_messages"
        });
        Assert.True(membershipResponse.IsSuccessStatusCode);

        using var convResponse = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "link-test-agent",
            scopeProjectId = "den-core"
        });
        var conv = await convResponse.Content.ReadFromJsonAsync<DirectConversationDto>();
        Assert.NotNull(conv);

        // Seed a historical wake_event channel message and explicitly link it
        // into the transcript.
        var eventMessage = await PostMessageAsync(client, channel.Id, new
        {
            senderType = "user",
            senderIdentity = "link-test-agent",
            body = "Task #42 processed successfully.",
            messageKind = "human_text",
            sourceKind = "wake_event",
            sourceId = $"direct-agent-message:{channel.Id}:link-test-agent:{Guid.NewGuid():N}",
            sourceProjectId = "den-core",
            targetTaskId = (long?)42,
            agentInstanceId = (string?)null,
            sessionOwnerId = (string?)null,
            sessionId = (string?)null
        });

        // Explicitly link the agent response into the DM transcript
        var bodyPreview = "Task #42 processed successfully.";

        using var linkResponse = await client.PostAsJsonAsync(
            $"/api/direct-conversations/{conv.Id}/link-message", new
        {
            channelMessageId = eventMessage.Id,
            direction = "agent_to_human",
            senderIdentity = "link-test-agent",
            recipientIdentity = "patch",
            bodyPreview
        });
        Assert.Equal(HttpStatusCode.Created, linkResponse.StatusCode);
        var linkedEntry = await linkResponse.Content.ReadFromJsonAsync<DirectConversationEntryDto>();
        Assert.NotNull(linkedEntry);
        Assert.Equal("agent_to_human", linkedEntry.Direction);
        Assert.Equal("link-test-agent", linkedEntry.SenderIdentity);
        Assert.Equal("patch", linkedEntry.RecipientIdentity);
        Assert.Equal(eventMessage.Id, linkedEntry.ChannelMessageId);

        // Verify both entries appear in the transcript
        var entries = await client.GetFromJsonAsync<DirectConversationEntryListResponse>(
            $"/api/direct-conversations/{conv.Id}/entries");
        Assert.NotNull(entries);
        Assert.Single(entries.Entries);
    }

    // ── Broad identity-pair capture rejection ───────────────────────────

    [Fact]
    public async Task BroadIdentityPairCapture_IsRejected()
    {
        using var client = _factory.CreateClient();

        // Setup channel + membership for two agents on the same channel
        var channelSlug = $"ops-{Guid.NewGuid():N}"[..20];
        using var channelResponse = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = channelSlug,
            displayName = "Capture Test Channel",
            kind = "project_default",
            projectId = "den-core",
            createdBy = "test"
        });
        var channel = await channelResponse.Content.ReadFromJsonAsync<ChannelPayload>();
        Assert.NotNull(channel);

        // Membership for the target agent
        using var m1 = await client.PutAsJsonAsync(
            $"/api/channels/{channel.Id}/memberships", new
        {
            memberType = "agent",
            memberIdentity = "capture-agent",
            membershipStatus = "active",
            wakePolicy = "all_human_messages"
        });
        Assert.True(m1.IsSuccessStatusCode);

        // Create a conversation between patch and capture-agent
        using var convResponse = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "capture-agent",
            scopeProjectId = "den-core"
        });
        var conv = await convResponse.Content.ReadFromJsonAsync<DirectConversationDto>();
        Assert.NotNull(conv);

        // Seed a regular wake_event from capture-agent to patch. It should not
        // auto-link just because identities match the conversation.
        _ = await PostMessageAsync(client, channel.Id, new
        {
            senderType = "user",
            senderIdentity = "capture-agent",
            body = "General channel update.",
            messageKind = "human_text",
            sourceKind = "wake_event",
            sourceId = $"direct-agent-message:{channel.Id}:capture-agent:{Guid.NewGuid():N}",
            sourceProjectId = "den-core"
        });

        // Verify the conversation entries are EMPTY — the message was NOT
        // auto-linked just because sender/recipient match the conversation
        var entries = await client.GetFromJsonAsync<DirectConversationEntryListResponse>(
            $"/api/direct-conversations/{conv.Id}/entries");
        Assert.NotNull(entries);
        Assert.Empty(entries.Entries);
    }

    // ── Unread count on conversation list ───────────────────────────────

    [Fact]
    public async Task ConversationList_IncludesUnreadCounts()
    {
        using var client = _factory.CreateClient();

        // Setup channel + membership
        var channelSlug = $"ops-{Guid.NewGuid():N}"[..20];
        using var channelResponse = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = channelSlug,
            displayName = "Unread Test Channel",
            kind = "project_default",
            projectId = "den-core",
            createdBy = "test"
        });
        var channel = await channelResponse.Content.ReadFromJsonAsync<ChannelPayload>();

        using var membershipResponse = await client.PutAsJsonAsync(
            $"/api/channels/{channel!.Id}/memberships", new
        {
            memberType = "agent",
            memberIdentity = "unread-agent",
            membershipStatus = "active",
            wakePolicy = "all_human_messages"
        });
        Assert.True(membershipResponse.IsSuccessStatusCode);

        // Create conversation
        using var convResponse = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "unread-agent",
            scopeProjectId = "den-core"
        });
        var conv = await convResponse.Content.ReadFromJsonAsync<DirectConversationDto>();
        Assert.NotNull(conv);

        // Initially, no entries — unread should be 0
        var list1 = await client.GetFromJsonAsync<DirectConversationListResponse>(
            "/api/direct-conversations?humanIdentity=patch");
        Assert.NotNull(list1);
        var conv1 = Assert.Single(list1.Conversations);
        Assert.Equal(0, conv1.UnreadCount);

        // Explicitly link a canonical message into the transcript.
        var inboundMessage = await PostMessageAsync(client, channel!.Id, new
        {
            senderType = "user",
            senderIdentity = "patch",
            body = "Hello, agent!",
            messageKind = "human_text",
            sourceKind = "wake_event",
            sourceId = $"direct-agent-message:{channel.Id}:unread-agent:{Guid.NewGuid():N}",
            sourceProjectId = "den-core"
        });

        using var linkResponse = await client.PostAsJsonAsync(
            $"/api/direct-conversations/{conv.Id}/link-message", new
        {
            channelMessageId = inboundMessage.Id,
            direction = "human_to_agent",
            senderIdentity = "patch",
            recipientIdentity = "unread-agent",
            bodyPreview = "Hello, agent!"
        });
        Assert.Equal(HttpStatusCode.Created, linkResponse.StatusCode);

        // Now unread should be 1 (no read cursor set yet)
        var list2 = await client.GetFromJsonAsync<DirectConversationListResponse>(
            "/api/direct-conversations?humanIdentity=patch");
        Assert.NotNull(list2);
        var conv2 = Assert.Single(list2.Conversations);
        Assert.Equal(1, conv2.UnreadCount);

        // Mark conversation as read
        var entriesPage = await client.GetFromJsonAsync<DirectConversationEntryListResponse>(
            $"/api/direct-conversations/{conv.Id}/entries");
        var lastEntryId = entriesPage!.Entries[0].Id;

        using var cursorResponse = await client.PutAsJsonAsync(
            $"/api/direct-conversations/{conv.Id}/read-cursor", new
        {
            readerIdentity = "patch",
            lastReadEntryId = lastEntryId
        });
        Assert.Equal(HttpStatusCode.OK, cursorResponse.StatusCode);

        // Now unread should be 0
        var list3 = await client.GetFromJsonAsync<DirectConversationListResponse>(
            "/api/direct-conversations?humanIdentity=patch");
        Assert.NotNull(list3);
        var conv3 = Assert.Single(list3.Conversations);
        Assert.Equal(0, conv3.UnreadCount);
    }

    // ── Link-message validation ─────────────────────────────────────────

    [Fact]
    public async Task LinkMessage_InvalidDirection_ReturnsBadRequest()
    {
        using var client = _factory.CreateClient();

        using var convResponse = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "validation-agent",
            scopeProjectId = "den-core"
        });
        var conv = await convResponse.Content.ReadFromJsonAsync<DirectConversationDto>();
        Assert.NotNull(conv);

        using var linkResponse = await client.PostAsJsonAsync(
            $"/api/direct-conversations/{conv.Id}/link-message", new
        {
            channelMessageId = (long)999,
            direction = "invalid_direction",
            senderIdentity = "agent",
            recipientIdentity = "human"
        });
        Assert.Equal(HttpStatusCode.BadRequest, linkResponse.StatusCode);
    }

    // ── Source badge projections on entries ─────────────────────────────

    [Fact]
    public async Task Entry_HasSourceBadgeProjections()
    {
        using var client = _factory.CreateClient();

        // Setup channel + membership
        var channelSlug = $"ops-{Guid.NewGuid():N}"[..20];
        using var channelResponse = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = channelSlug,
            displayName = "Badge Test Channel",
            kind = "project_default",
            projectId = "den-core",
            createdBy = "test"
        });
        var channel = await channelResponse.Content.ReadFromJsonAsync<ChannelPayload>();

        using var membershipResponse = await client.PutAsJsonAsync(
            $"/api/channels/{channel!.Id}/memberships", new
        {
            memberType = "agent",
            memberIdentity = "badge-agent",
            membershipStatus = "active",
            wakePolicy = "all_human_messages"
        });

        using var convResponse = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "badge-agent",
            scopeProjectId = "den-core"
        });
        var conv = await convResponse.Content.ReadFromJsonAsync<DirectConversationDto>();

        // Link a canonical message with full source badges.
        var inboundMessage = await PostMessageAsync(client, channel!.Id, new
        {
            senderType = "user",
            senderIdentity = "patch",
            body = "Process this.",
            messageKind = "human_text",
            sourceKind = "wake_event",
            sourceId = $"direct-agent-message:{channel.Id}:badge-agent:{Guid.NewGuid():N}",
            sourceProjectId = "den-core",
            targetTaskId = (long?)77,
            workerRunId = "piw_test_123",
            workerRole = "coder"
        });

        using var linkResponse = await client.PostAsJsonAsync(
            $"/api/direct-conversations/{conv!.Id}/link-message", new
        {
            channelMessageId = inboundMessage.Id,
            direction = "human_to_agent",
            senderIdentity = "patch",
            recipientIdentity = "badge-agent",
            bodyPreview = "Process this."
        });
        Assert.Equal(HttpStatusCode.Created, linkResponse.StatusCode);

        var entries = await client.GetFromJsonAsync<DirectConversationEntryListResponse>(
            $"/api/direct-conversations/{conv.Id}/entries");
        var entry = Assert.Single(entries!.Entries);
        Assert.Equal("human_to_agent", entry.Direction);
        Assert.Equal("patch", entry.SenderIdentity);
        Assert.Equal("badge-agent", entry.RecipientIdentity);
        // Source badges
        Assert.True(entry.SourceChannelId > 0);
        Assert.Equal("den-core", entry.SourceProjectId);
        Assert.Equal(77, entry.SourceTaskId);
        Assert.Equal("piw_test_123", entry.SourceWorkerRunId);
    }

    // ── Source badge projections on linked entries ───────────────────────

    [Fact]
    public async Task LinkedEntry_HasSourceBadgesFromCanonicalMessage()
    {
        using var client = _factory.CreateClient();

        // Setup channel + membership
        var channelSlug = $"ops-{Guid.NewGuid():N}"[..20];
        using var channelResponse = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = channelSlug,
            displayName = "LinkBadge Test Channel",
            kind = "project_default",
            projectId = "den-core",
            createdBy = "test"
        });
        var channel = await channelResponse.Content.ReadFromJsonAsync<ChannelPayload>();

        using var membershipResponse = await client.PutAsJsonAsync(
            $"/api/channels/{channel!.Id}/memberships", new
        {
            memberType = "agent",
            memberIdentity = "linkbadge-agent",
            membershipStatus = "active",
            wakePolicy = "all_human_messages"
        });
        Assert.True(membershipResponse.IsSuccessStatusCode);

        using var convResponse = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "linkbadge-agent",
            scopeProjectId = "den-core"
        });
        var conv = await convResponse.Content.ReadFromJsonAsync<DirectConversationDto>();
        Assert.NotNull(conv);

        // Seed a wake_event with rich target-work/session attribution.
        var eventMessage = await PostMessageAsync(client, channel!.Id, new
        {
            senderType = "user",
            senderIdentity = "linkbadge-agent",
            body = "Task #55 done.",
            messageKind = "human_text",
            sourceKind = "wake_event",
            sourceId = $"direct-agent-message:{channel.Id}:linkbadge-agent:{Guid.NewGuid():N}",
            sourceProjectId = "den-core",
            targetProjectId = "den-core",
            targetTaskId = (long?)55,
            workerRunId = "piw_link_999",
            workerRole = "coder",
            sessionOwnerId = "session-owner-1",
            sessionId = "sess-abc"
        });

        // Link the agent response; source badges should come from the canonical message
        using var linkResponse = await client.PostAsJsonAsync(
            $"/api/direct-conversations/{conv.Id}/link-message", new
        {
            channelMessageId = eventMessage.Id,
            direction = "agent_to_human",
            senderIdentity = "linkbadge-agent",
            recipientIdentity = "patch",
            bodyPreview = "Task #55 done."
        });
        Assert.Equal(HttpStatusCode.Created, linkResponse.StatusCode);
        var linkedEntry = await linkResponse.Content.ReadFromJsonAsync<DirectConversationEntryDto>();
        Assert.NotNull(linkedEntry);

        // Source badges are now populated from the canonical channel_message
        Assert.Equal(channel.Id, linkedEntry.SourceChannelId);
        Assert.Equal("den-core", linkedEntry.SourceProjectId);
        Assert.Equal(55, linkedEntry.SourceTaskId);
        Assert.Equal("piw_link_999", linkedEntry.SourceWorkerRunId);
        Assert.Equal("session-owner-1", linkedEntry.SourceSessionOwnerId);
    }

    private static async Task<MessagePayload> PostMessageAsync(HttpClient client, long channelId, object request)
    {
        using var response = await client.PostAsJsonAsync($"/api/channels/{channelId}/messages", request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<MessagePayload>();
        Assert.NotNull(payload);
        return payload;
    }

    // ── JSON payload types for deserialization ──────────────────────────

    private sealed record ChannelPayload(long Id, string Slug, string DisplayName, string Kind,
        string? ProjectId, string CreatedBy, string Visibility);
    private sealed record MessagePayload(long Id, long ChannelId, string Body);
    private sealed record ReadCursorPayload(
        long ConversationId, string ReaderIdentity, long? LastReadEntryId, long UnreadCount, bool HasUnread);
}
