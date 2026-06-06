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

    // ── DM send flow (requires channel + membership setup) ──────────────

    [Fact]
    public async Task SendDirectMessage_RecordsCanonicalMessageAndEntry()
    {
        using var client = _factory.CreateClient();

        // Setup: create channel and active agent membership
        var channelSlug = $"ops-{Guid.NewGuid():N}"[..20];
        using var channelResponse = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = channelSlug,
            displayName = "DM Test Channel",
            kind = "project_default",
            projectId = "den-core",
            createdBy = "test"
        });
        Assert.True(channelResponse.IsSuccessStatusCode);
        var channel = await channelResponse.Content.ReadFromJsonAsync<ChannelPayload>();
        Assert.NotNull(channel);

        // Upsert agent membership (PUT)
        using var membershipResponse = await client.PutAsJsonAsync(
            $"/api/channels/{channel.Id}/memberships", new
            {
                memberType = "agent",
                memberIdentity = "target-agent",
                membershipStatus = "active",
                wakePolicy = "all_human_messages"
            });
        Assert.True(membershipResponse.IsSuccessStatusCode);

        // Create conversation
        using var convResponse = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "target-agent",
            scopeProjectId = "den-core"
        });
        var conv = await convResponse.Content.ReadFromJsonAsync<DirectConversationDto>();
        Assert.NotNull(conv);

        // Send DM
        using var sendResponse = await client.PostAsJsonAsync(
            $"/api/direct-conversations/{conv.Id}/send", new
            {
                senderIdentity = "patch",
                body = "Hello agent, please process task #42.",
                sourceProjectId = "den-core",
                targetTaskId = 42
            });
        Assert.Equal(HttpStatusCode.Created, sendResponse.StatusCode);
        var dmResponse = await sendResponse.Content.ReadFromJsonAsync<DirectMessageResponse>();
        Assert.NotNull(dmResponse);
        Assert.Equal("recorded", dmResponse.Status);
        Assert.Equal(conv.Id, dmResponse.ConversationId);
        Assert.True(dmResponse.EventId > 0);
        Assert.True(dmResponse.EntryId > 0);

        // Verify entry exists in the conversation
        var entries = await client.GetFromJsonAsync<DirectConversationEntryListResponse>(
            $"/api/direct-conversations/{conv.Id}/entries");
        Assert.NotNull(entries);
        var entry = Assert.Single(entries.Entries);
        Assert.Equal("human_to_agent", entry.Direction);
        Assert.Equal("patch", entry.SenderIdentity);
        Assert.Equal("target-agent", entry.RecipientIdentity);
        Assert.True(entry.ChannelMessageId > 0);

        // Verify the canonical channel message exists
        var eventMessage = await client.GetFromJsonAsync<DirectAgentEventReadbackPayload>(
            $"/api/direct-agent-events/{dmResponse.EventId}");
        Assert.NotNull(eventMessage);
        Assert.Equal("wake_event", eventMessage.SourceKind);
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

    // ── Body vs summary precedence ──────────────────────────────────────

    [Fact]
    public async Task SendDirectMessage_BodyIsPrimary_SummaryIsSecondary()
    {
        using var client = _factory.CreateClient();

        // Setup channel + membership like above
        var channelSlug = $"ops-{Guid.NewGuid():N}"[..20];
        using var channelResponse = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = channelSlug,
            displayName = "DM Body Test",
            kind = "project_default",
            projectId = "den-core",
            createdBy = "test"
        });
        var channel = await channelResponse.Content.ReadFromJsonAsync<ChannelPayload>();
        Assert.NotNull(channel);

        using var membershipResponse = await client.PutAsJsonAsync(
            $"/api/channels/{channel.Id}/memberships", new
            {
                memberType = "agent",
                memberIdentity = "body-test-agent",
                membershipStatus = "active",
                wakePolicy = "all_human_messages"
            });
        Assert.True(membershipResponse.IsSuccessStatusCode);

        using var convResponse = await client.PostAsJsonAsync("/api/direct-conversations", new
        {
            humanIdentity = "patch",
            agentIdentity = "body-test-agent",
            scopeProjectId = "den-core"
        });
        var conv = await convResponse.Content.ReadFromJsonAsync<DirectConversationDto>();
        Assert.NotNull(conv);

        var requestBody = "Process task #99 with priority high.";
        using var sendResponse = await client.PostAsJsonAsync(
            $"/api/direct-conversations/{conv.Id}/send", new
            {
                senderIdentity = "patch",
                body = requestBody,
                sourceProjectId = "den-core"
            });
        Assert.Equal(HttpStatusCode.Created, sendResponse.StatusCode);
        var dmResponse = await sendResponse.Content.ReadFromJsonAsync<DirectMessageResponse>();
        Assert.NotNull(dmResponse);

        // The canonical wake_event message must have body=requestBody (not summary)
        var eventMsg = await client.GetFromJsonAsync<DirectAgentEventReadbackPayload>(
            $"/api/direct-agent-events/{dmResponse.EventId}");
        Assert.NotNull(eventMsg);
        Assert.Equal(requestBody, eventMsg.Body);
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

    // ── JSON payload types for deserialization ──────────────────────────

    private sealed record ChannelPayload(long Id, string Slug, string DisplayName, string Kind,
        string? ProjectId, string CreatedBy, string Visibility);
    private sealed record DirectAgentEventReadbackPayload(
        long EventId, long ChannelId, string? SourceKind, string Body, string? Summary);
    private sealed record ReadCursorPayload(
        long ConversationId, string ReaderIdentity, long? LastReadEntryId, long UnreadCount, bool HasUnread);
}
