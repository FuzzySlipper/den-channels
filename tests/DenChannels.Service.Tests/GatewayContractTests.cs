using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenChannels.Service.Tests;

/// <summary>
/// Tests for the /api/gateway endpoint group (task #1351).
/// </summary>
public sealed class GatewayContractTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"den-channels-gw-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public GatewayContractTests()
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
        _client = _factory.CreateClient();
    }

    // -------------------------------------------------------------------------
    // Health probe
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GatewayHealth_ReturnsReadyProbe()
    {
        using var response = await _client.GetAsync("/api/gateway/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GatewayHealthPayload>();
        Assert.NotNull(payload);
        Assert.Equal("den-channels", payload.Service);
        Assert.Equal("ready", payload.Status);
        Assert.NotNull(payload.Endpoints);
        Assert.NotEmpty(payload.Endpoints);
        // Should advertise the gateway membership and message endpoints
        Assert.Contains(payload.Endpoints, e => e.Contains("memberships"));
        Assert.Contains(payload.Endpoints, e => e.Contains("messages"));
    }

    // -------------------------------------------------------------------------
    // Membership lookup
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GatewayMemberships_ByChannelId_ReturnsMembersWithWakePolicy()
    {
        // Arrange: create a channel and add a membership
        var channel = await EnsureDefaultChannelAsync("gw-test-proj-1");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "den-gateway",
            wakePolicy = "all_messages_except_self",
            cooldownSeconds = 30,
            maxAutoRepliesPerWindow = 5
        });

        // Act
        var payload = await _client.GetFromJsonAsync<GatewayMembershipsPayload>(
            $"/api/gateway/memberships?channelId={channel.Id}");

        Assert.NotNull(payload);
        Assert.Equal(channel.Id, payload.ChannelId);
        Assert.NotNull(payload.Members);
        var member = Assert.Single(payload.Members);
        Assert.Equal("agent", member.MemberType);
        Assert.Equal("den-gateway", member.MemberIdentity);
        Assert.Equal("all_messages_except_self", member.WakePolicy);
        Assert.Equal(30, member.CooldownSeconds);
        Assert.Equal(5, member.MaxAutoRepliesPerWindow);
        Assert.Equal("active", member.MembershipStatus);
    }

    [Fact]
    public async Task GatewayMemberships_ByProjectId_ResolvesDefaultChannel()
    {
        var channel = await EnsureDefaultChannelAsync("gw-test-proj-2");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "den-pi",
            wakePolicy = "mentions_only"
        });

        var payload = await _client.GetFromJsonAsync<GatewayMembershipsPayload>(
            "/api/gateway/memberships?projectId=gw-test-proj-2");

        Assert.NotNull(payload);
        Assert.Equal(channel.Id, payload.ChannelId);
        Assert.NotEmpty(payload.Members);
    }

    [Fact]
    public async Task GatewayMemberships_MissingParams_Returns400()
    {
        using var response = await _client.GetAsync("/api/gateway/memberships");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GatewayMemberships_SettingsJson_IsBounded()
    {
        // Settings JSON must be capped to a reasonable preview length
        var channel = await EnsureDefaultChannelAsync("gw-test-proj-settings");
        var longSettings = "{\"key\":\"" + new string('x', 2000) + "\"}";
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "den-gateway",
            wakePolicy = "never",
            settingsJson = longSettings
        });

        var payload = await _client.GetFromJsonAsync<GatewayMembershipsPayload>(
            $"/api/gateway/memberships?channelId={channel.Id}");

        Assert.NotNull(payload);
        var member = Assert.Single(payload.Members);
        // Settings preview should be bounded (max ~500 chars)
        Assert.NotNull(member.SettingsJsonPreview);
        Assert.True(member.SettingsJsonPreview!.Length <= 512,
            $"SettingsJsonPreview length {member.SettingsJsonPreview.Length} exceeds 512");
    }

    // -------------------------------------------------------------------------
    // Message lookup
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GatewayMessages_ById_ReturnsMessageWithAllFields()
    {
        var channel = await EnsureDefaultChannelAsync("gw-test-msg-proj");
        var posted = await PostMessageAsync(channel.Id, new
        {
            senderType = "system",
            senderIdentity = "den-gateway",
            body = "Gateway test message",
            messageKind = "system_event",
            sourceKind = "task_message",
            sourceId = "9900",
            sourceProjectId = "gw-test-msg-proj",
            summary = "Gateway sent event",
            deepLink = "den://project/gw-test-msg-proj/task/99",
            dedupeKey = "gw-test:9900"
        });

        var payload = await _client.GetFromJsonAsync<GatewayMessagePayload>(
            $"/api/gateway/messages/{posted.Id}");

        Assert.NotNull(payload);
        Assert.Equal(posted.Id, payload.Id);
        Assert.Equal(channel.Id, payload.ChannelId);
        Assert.Equal("system_event", payload.MessageKind);
        Assert.Equal("system", payload.SenderType);
        Assert.Equal("den-gateway", payload.SenderIdentity);
        Assert.Equal("task_message", payload.SourceKind);
        Assert.Equal("9900", payload.SourceId);
        Assert.Equal("gw-test-msg-proj", payload.SourceProjectId);
        Assert.Equal("gw-test:9900", payload.DedupeKey);
        Assert.Equal("den://project/gw-test-msg-proj/task/99", payload.DeepLink);
        Assert.Equal("Gateway sent event", payload.Summary);
        Assert.Equal("Gateway test message", payload.Body);
        Assert.NotEmpty(payload.CreatedAt);
    }

    [Fact]
    public async Task GatewayMessages_ById_NotFound_Returns404()
    {
        using var response = await _client.GetAsync("/api/gateway/messages/99999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Source pointer lookup
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GatewaySources_BySourcePointer_ReturnsMatchingMessages()
    {
        var channel = await EnsureDefaultChannelAsync("gw-test-src-proj");
        await PostMessageAsync(channel.Id, new
        {
            senderType = "system",
            senderIdentity = "den-gateway",
            body = "Source pointer test 1",
            messageKind = "mirror_summary",
            sourceKind = "review_round",
            sourceId = "77",
            sourceProjectId = "gw-test-src-proj",
            dedupeKey = "rr:77:a"
        });
        await PostMessageAsync(channel.Id, new
        {
            senderType = "system",
            senderIdentity = "den-gateway",
            body = "Source pointer test 2",
            messageKind = "mirror_summary",
            sourceKind = "review_round",
            sourceId = "77",
            sourceProjectId = "gw-test-src-proj",
            dedupeKey = "rr:77:b"
        });
        // Different source — should not appear
        await PostMessageAsync(channel.Id, new
        {
            senderType = "system",
            senderIdentity = "den-gateway",
            body = "Different source",
            messageKind = "mirror_summary",
            sourceKind = "review_round",
            sourceId = "88",
            dedupeKey = "rr:88:a"
        });

        var messages = await _client.GetFromJsonAsync<List<GatewayMessagePayload>>(
            "/api/gateway/sources/review_round/77?sourceProjectId=gw-test-src-proj");

        Assert.NotNull(messages);
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Equal("review_round", m.SourceKind));
        Assert.All(messages, m => Assert.Equal("77", m.SourceId));
    }

    [Fact]
    public async Task GatewaySources_WithoutSourceProjectId_MatchesAcrossProjects()
    {
        // Without sourceProjectId, should match by sourceKind + sourceId only
        var channel = await EnsureDefaultChannelAsync("gw-src-multi-proj");
        await PostMessageAsync(channel.Id, new
        {
            senderType = "system",
            senderIdentity = "den-gateway",
            body = "Cross-project source",
            messageKind = "system_event",
            sourceKind = "worker_run",
            sourceId = "run-999",
            dedupeKey = "wr:run-999"
        });

        var messages = await _client.GetFromJsonAsync<List<GatewayMessagePayload>>(
            "/api/gateway/sources/worker_run/run-999");

        Assert.NotNull(messages);
        Assert.NotEmpty(messages);
        Assert.All(messages, m => Assert.Equal("worker_run", m.SourceKind));
        Assert.All(messages, m => Assert.Equal("run-999", m.SourceId));
    }

    // -------------------------------------------------------------------------
    // Events cursor
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GatewayEvents_ByChannelId_ReturnsCursorResponse()
    {
        var channel = await EnsureDefaultChannelAsync("gw-events-proj-1");
        for (var i = 1; i <= 5; i++)
        {
            await PostMessageAsync(channel.Id, new
            {
                senderType = "system",
                senderIdentity = "den-gateway",
                body = $"event {i}",
                messageKind = "system_event"
            });
        }

        var response = await _client.GetFromJsonAsync<GatewayEventsPayload>(
            $"/api/gateway/events?channelId={channel.Id}&limit=3");

        Assert.NotNull(response);
        Assert.Equal(3, response.Items.Count);
        Assert.True(response.HasMore);
        Assert.NotNull(response.NextAfterId);
        // Should be in ascending order
        for (var i = 1; i < response.Items.Count; i++)
            Assert.True(response.Items[i].Id > response.Items[i - 1].Id);
    }

    [Fact]
    public async Task GatewayEvents_AfterIdCursor_ReturnsNextPage()
    {
        var channel = await EnsureDefaultChannelAsync("gw-events-proj-2");
        for (var i = 1; i <= 6; i++)
        {
            await PostMessageAsync(channel.Id, new
            {
                senderType = "system",
                senderIdentity = "den-gateway",
                body = $"page event {i}",
                messageKind = "system_event"
            });
        }

        var page1 = await _client.GetFromJsonAsync<GatewayEventsPayload>(
            $"/api/gateway/events?channelId={channel.Id}&limit=4");
        Assert.NotNull(page1);
        Assert.Equal(4, page1.Items.Count);
        Assert.True(page1.HasMore);

        var page2 = await _client.GetFromJsonAsync<GatewayEventsPayload>(
            $"/api/gateway/events?channelId={channel.Id}&afterId={page1.NextAfterId}&limit=4");
        Assert.NotNull(page2);
        Assert.Equal(2, page2.Items.Count);
        Assert.False(page2.HasMore);
        Assert.Null(page2.NextAfterId);
    }

    [Fact]
    public async Task GatewayEvents_ByProjectId_ResolvesDefaultChannel()
    {
        var channel = await EnsureDefaultChannelAsync("gw-events-proj-3");
        await PostMessageAsync(channel.Id, new
        {
            senderType = "system",
            senderIdentity = "den-gateway",
            body = "project event",
            messageKind = "system_event"
        });

        var response = await _client.GetFromJsonAsync<GatewayEventsPayload>(
            "/api/gateway/events?projectId=gw-events-proj-3&limit=10");

        Assert.NotNull(response);
        Assert.NotEmpty(response.Items);
    }

    [Fact]
    public async Task GatewayEvents_MissingParams_Returns400()
    {
        using var response = await _client.GetAsync("/api/gateway/events");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GatewayEvents_ItemsContainExpectedFields()
    {
        var channel = await EnsureDefaultChannelAsync("gw-events-fields");
        await PostMessageAsync(channel.Id, new
        {
            senderType = "agent",
            senderIdentity = "den-pi",
            body = "event with fields",
            messageKind = "agent_text",
            sourceKind = "task_message",
            sourceId = "123",
            dedupeKey = "evt:123"
        });

        var response = await _client.GetFromJsonAsync<GatewayEventsPayload>(
            $"/api/gateway/events?channelId={channel.Id}&limit=10");

        Assert.NotNull(response);
        var item = Assert.Single(response.Items);
        Assert.Equal("agent_text", item.MessageKind);
        Assert.Equal("agent", item.SenderType);
        Assert.Equal("den-pi", item.SenderIdentity);
        Assert.Equal("task_message", item.SourceKind);
        Assert.Equal("123", item.SourceId);
        Assert.Equal("evt:123", item.DedupeKey);
        Assert.NotEmpty(item.CreatedAt);
    }

    // -------------------------------------------------------------------------
    // System message post (Gateway-generated)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GatewaySystemMessages_Post_IdempotentByDedupeKey()
    {
        var channel = await EnsureDefaultChannelAsync("gw-sys-msg-proj-1");

        using var response1 = await _client.PostAsJsonAsync("/api/gateway/system-messages", new
        {
            channelId = channel.Id,
            body = "System sentinel",
            messageKind = "system_event",
            dedupeKey = "gw-sys:sentinel:1"
        });
        Assert.Equal(HttpStatusCode.Created, response1.StatusCode);
        var created = await response1.Content.ReadFromJsonAsync<GatewayMessagePayload>();
        Assert.NotNull(created);

        // Re-post same dedupeKey — should return existing message (200 OK, not 409 Conflict)
        using var response2 = await _client.PostAsJsonAsync("/api/gateway/system-messages", new
        {
            channelId = channel.Id,
            body = "Different body",
            messageKind = "system_event",
            dedupeKey = "gw-sys:sentinel:1"
        });
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        var existing = await response2.Content.ReadFromJsonAsync<GatewayMessagePayload>();
        Assert.NotNull(existing);
        Assert.Equal(created.Id, existing.Id);
        // Body should be from original post, not re-posted body
        Assert.Equal("System sentinel", existing.Body);
    }

    [Fact]
    public async Task GatewaySystemMessages_Post_DefaultsToSystemEventAndDenGatewaySender()
    {
        var channel = await EnsureDefaultChannelAsync("gw-sys-msg-proj-2");

        using var response = await _client.PostAsJsonAsync("/api/gateway/system-messages", new
        {
            channelId = channel.Id,
            body = "Minimal gateway post"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GatewayMessagePayload>();
        Assert.NotNull(payload);
        Assert.Equal("system_event", payload.MessageKind);
        Assert.Equal("den-gateway", payload.SenderIdentity);
        Assert.Equal("system", payload.SenderType);
    }

    [Fact]
    public async Task GatewaySystemMessages_Post_RoutesToProjectDefaultChannel()
    {
        await EnsureDefaultChannelAsync("gw-sys-route-proj");

        using var response = await _client.PostAsJsonAsync("/api/gateway/system-messages", new
        {
            projectId = "gw-sys-route-proj",
            body = "Routed to project default channel",
            messageKind = "mirror_summary",
            dedupeKey = "gw-route:proj:1"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GatewayMessagePayload>();
        Assert.NotNull(payload);
        Assert.True(payload.ChannelId > 0);
    }

    [Fact]
    public async Task GatewaySystemMessages_Post_SupportsSourcePointerAndDeepLink()
    {
        var channel = await EnsureDefaultChannelAsync("gw-sys-src-proj");

        using var response = await _client.PostAsJsonAsync("/api/gateway/system-messages", new
        {
            channelId = channel.Id,
            body = "Delivery sentinel summary",
            messageKind = "mirror_summary",
            sourceKind = "worker_run",
            sourceId = "run-42",
            sourceProjectId = "gw-sys-src-proj",
            deepLink = "den://run/42",
            summary = "Worker run completed",
            metadataJson = "{\"run_id\":\"run-42\"}",
            dedupeKey = "gw-run:42"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GatewayMessagePayload>();
        Assert.NotNull(payload);
        Assert.Equal("mirror_summary", payload.MessageKind);
        Assert.Equal("worker_run", payload.SourceKind);
        Assert.Equal("run-42", payload.SourceId);
        Assert.Equal("den://run/42", payload.DeepLink);
        Assert.Equal("Worker run completed", payload.Summary);
        Assert.Equal("gw-run:42", payload.DedupeKey);
    }

    [Fact]
    public async Task GatewaySystemMessages_Post_InvalidMessageKind_Returns400()
    {
        var channel = await EnsureDefaultChannelAsync("gw-sys-invalid-proj");

        using var response = await _client.PostAsJsonAsync("/api/gateway/system-messages", new
        {
            channelId = channel.Id,
            body = "Bad kind",
            messageKind = "not_a_valid_kind"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GatewaySystemMessages_Post_MissingChannelAndProject_Returns400()
    {
        using var response = await _client.PostAsJsonAsync("/api/gateway/system-messages", new
        {
            body = "No channel or project"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GatewayTestWakes_Post_RecordsWakeEventForActiveAgentMembership()
    {
        var channel = await EnsureDefaultChannelAsync("gw-test-wake-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "hermes-coder",
            wakePolicy = "direct_questions_only",
            settingsJson = "{\"profile\":\"den-hermes-coder\",\"transportPreview\":\"redacted-by-test\"}"
        });

        using var response = await _client.PostAsJsonAsync("/api/gateway/test-wakes", new
        {
            channelId = channel.Id,
            memberIdentity = "hermes-coder",
            requestedBy = "tester"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GatewayTestWakePayload>();
        Assert.NotNull(payload);
        Assert.Equal("recorded", payload.Status);
        Assert.Equal("hermes-coder", payload.MemberIdentity);
        Assert.Equal("direct_questions_only", payload.WakePolicy);
        Assert.Equal(channel.Id, payload.ChannelId);
        Assert.Contains($"/api/gateway/messages/{payload.MessageId}", payload.GatewayMessageUrl);

        var message = await _client.GetFromJsonAsync<GatewayMessagePayload>(payload.GatewayMessageUrl);
        Assert.NotNull(message);
        Assert.Equal("wake_event", message.SourceKind);
        Assert.Contains("Controlled test wake", message.Body);
        Assert.DoesNotContain("redacted-by-test", message.Body);
    }

    // -------------------------------------------------------------------------
    // Existing APIs still work
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExistingChannelApi_StillWorksAfterGatewayRoutes()
    {
        using var createResponse = await _client.PostAsJsonAsync("/api/channels", new
        {
            slug = $"gw-regression-{Guid.NewGuid():N}",
            displayName = "Regression Channel",
            kind = "ad_hoc",
            createdBy = "test"
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<ChannelStub> EnsureDefaultChannelAsync(string projectId)
    {
        var response = await _client.PutAsJsonAsync($"/api/projects/{projectId}/default-channel", new
        {
            displayName = projectId,
            createdBy = "test"
        });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<ChannelStub>();
        Assert.NotNull(payload);
        return payload;
    }

    private async Task UpsertMembershipAsync(long channelId, object request)
    {
        using var response = await _client.PutAsJsonAsync($"/api/channels/{channelId}/memberships", request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<MessageStub> PostMessageAsync(long channelId, object request)
    {
        using var response = await _client.PostAsJsonAsync($"/api/channels/{channelId}/messages", request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<MessageStub>();
        Assert.NotNull(payload);
        return payload;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
    }

    // ---- Local payload records ----

    private sealed record ChannelStub(long Id, string Slug, string Kind, string? ProjectId);
    private sealed record MessageStub(long Id, long ChannelId, string Body);

    private sealed record GatewayHealthPayload(string Service, string Status, string[] Endpoints);

    private sealed record GatewayMembershipsPayload(
        long ChannelId,
        string ChannelSlug,
        string ChannelKind,
        string? ProjectId,
        List<GatewayMemberPayload> Members);

    private sealed record GatewayMemberPayload(
        long Id,
        string MemberType,
        string MemberIdentity,
        string MembershipStatus,
        string WakePolicy,
        bool CanSend,
        int CooldownSeconds,
        int MaxAutoRepliesPerWindow,
        string? SettingsJsonPreview);

    private sealed record GatewayMessagePayload(
        long Id,
        long ChannelId,
        string MessageKind,
        string SenderType,
        string SenderIdentity,
        string? SourceKind,
        string? SourceId,
        string? SourceProjectId,
        string? DedupeKey,
        string? DeepLink,
        string? Summary,
        string Body,
        string CreatedAt);

    private sealed record GatewayTestWakePayload(
        string Status,
        string MemberIdentity,
        string WakePolicy,
        long MessageId,
        long ChannelId,
        string GatewayMessageUrl,
        string GatewayEventsUrl,
        string EvidenceSummary);

    private sealed record GatewayEventsPayload(
        List<GatewayEventItemPayload> Items,
        long? NextAfterId,
        bool HasMore);

    private sealed record GatewayEventItemPayload(
        long Id,
        long ChannelId,
        string MessageKind,
        string SenderType,
        string SenderIdentity,
        string? SourceKind,
        string? SourceId,
        string? SourceProjectId,
        string? DedupeKey,
        string? DeepLink,
        string? Summary,
        string Body,
        string CreatedAt);
}
