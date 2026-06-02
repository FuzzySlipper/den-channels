using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using DenChannels.Service.AgentsOverview;
using DenChannels.Service.Configuration;
using DenChannels.Service.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
    public async Task GatewayMemberships_SettingsJson_IsSanitizedAllowListLabel()
    {
        var channel = await EnsureDefaultChannelAsync("gw-test-proj-settings");
        const string secret = "sk-sec...leak";
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "den-gateway",
            wakePolicy = "never",
            settingsJson = "{\"profile\":\"den-hermes-coder\",\"bindingName\":\"safe-binding\",\"apiKey\":\"" + secret + "\",\"transportPreview\":\"redacted-by-test\"}"
        });

        using var response = await _client.GetAsync($"/api/gateway/memberships?channelId={channel.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, raw);
        Assert.DoesNotContain("apiKey", raw);
        Assert.DoesNotContain("transportPreview", raw);

        var payload = await response.Content.ReadFromJsonAsync<GatewayMembershipsPayload>();
        Assert.NotNull(payload);
        var member = Assert.Single(payload.Members);
        Assert.Equal("profile: den-hermes-coder · binding: safe-binding", member.SettingsLabel);
        Assert.True(member.CanReact);
        Assert.False(member.CanInvite);
    }

    [Fact]
    public async Task UpsertMembership_NullSettingsPreservesExistingSettingsJsonOnUpdate()
    {
        var channel = await EnsureDefaultChannelAsync("gw-test-proj-settings-preserve");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "den-gateway",
            wakePolicy = "mentions_only",
            canReact = false,
            canInvite = true,
            settingsJson = "{\"profile\":\"den-hermes-coder\",\"bindingName\":\"safe-binding\"}"
        });

        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "den-gateway",
            membershipStatus = "muted",
            wakePolicy = "all_human_messages",
            canSend = true,
            canReact = false,
            canInvite = true,
            cooldownSeconds = 30,
            maxAutoRepliesPerWindow = 2
        });

        var payload = await _client.GetFromJsonAsync<GatewayMembershipsPayload>(
            $"/api/gateway/memberships?channelId={channel.Id}");

        Assert.NotNull(payload);
        var member = Assert.Single(payload.Members);
        Assert.Equal("muted", member.MembershipStatus);
        Assert.Equal("all_human_messages", member.WakePolicy);
        Assert.False(member.CanReact);
        Assert.True(member.CanInvite);
        Assert.Equal(30, member.CooldownSeconds);
        Assert.Equal(2, member.MaxAutoRepliesPerWindow);
        Assert.Equal("profile: den-hermes-coder · binding: safe-binding", member.SettingsLabel);
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
    public async Task GatewayEvents_ReactionsDoNotCreateWakePulseEvents()
    {
        var channel = await EnsureDefaultChannelAsync("gw-events-reactions-no-pulse");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "agent-a",
            wakePolicy = "all_messages_except_self"
        });
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "agent-b",
            wakePolicy = "all_messages_except_self"
        });
        var message = await PostMessageAsync(channel.Id, new
        {
            senderType = "user",
            senderIdentity = "patch",
            body = "please acknowledge without adding noise",
            messageKind = "human_text"
        });

        using var reactionResponse = await _client.PostAsJsonAsync($"/api/channel-messages/{message.Id}/reactions", new
        {
            reactorType = "agent",
            reactorIdentity = "agent-a",
            reactionKey = "✅"
        });
        reactionResponse.EnsureSuccessStatusCode();

        var response = await _client.GetFromJsonAsync<GatewayEventsPayload>(
            $"/api/gateway/events?channelId={channel.Id}&limit=10");

        Assert.NotNull(response);
        var item = Assert.Single(response.Items);
        Assert.Equal(message.Id, item.Id);
        Assert.Equal("human_text", item.MessageKind);
        Assert.Equal("user", item.SenderType);
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

    [Fact]
    public async Task GatewayDirectAgentMessages_Post_ReturnsEvidenceAndPendingStatuses()
    {
        var channel = await EnsureDefaultChannelAsync("gw-direct-agent-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "hermes-reviewer",
            wakePolicy = "direct_questions_only",
            settingsJson = "{\"profile\":\"den-hermes-reviewer\",\"apiKey\":\"must-not-leak\"}"
        });

        using var response = await _client.PostAsJsonAsync("/api/gateway/direct-agent-messages", new
        {
            channelId = channel.Id,
            memberIdentity = "hermes-reviewer",
            senderIdentity = "operator",
            body = "Please review the fix.",
            waitFor = "none"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GatewayDirectAgentMessagePayload>();
        Assert.NotNull(payload);
        Assert.Equal("recorded", payload.Status);
        Assert.Equal("recorded_but_not_claimed_yet", payload.DeliveryStatus);
        Assert.Equal("unclaimed", payload.ClaimStatus);
        Assert.Equal("pending", payload.CompletionStatus);
        Assert.Equal("not_suppressed", payload.SuppressionStatus);
        Assert.False(payload.TimedOut);
        Assert.False(payload.GatewayUnavailable);
        Assert.Equal("hermes-reviewer", payload.MemberIdentity);
        Assert.Equal("direct_questions_only", payload.WakePolicy);
        Assert.Equal(channel.Id, payload.ChannelId);
        Assert.StartsWith($"direct-agent-message:{channel.Id}:hermes-reviewer:", payload.RequestId);
        Assert.DoesNotContain($"direct-agent-message:{channel.Id}:1:", payload.RequestId);
        Assert.Equal($"/api/gateway/messages/{payload.MessageId}", payload.GatewayMessageUrl);
        Assert.Contains($"/api/gateway/events?channelId={channel.Id}", payload.GatewayEventsUrl);
        Assert.Contains("no Gateway claim wait", payload.EvidenceSummary);

        var message = await _client.GetFromJsonAsync<GatewayMessagePayload>(payload.GatewayMessageUrl);
        Assert.NotNull(message);
        Assert.Equal("wake_event", message.SourceKind);
        Assert.Equal(payload.RequestId, message.SourceId);
        Assert.Equal("Please review the fix.", message.Body);
        Assert.Contains("recorded, pending claim/completion", message.Summary);
    }

    [Fact]
    public void GatewayDirectAgentStatus_MapsImmediateClaimEvidence()
    {
        var state = BuildGatewayState("direct-agent-message:16:voxelforge-runner:1", "delivering", 42, 7);

        var observation = GatewayDirectAgentDeliveryStatus.FromGatewayState(state, "direct-agent-message:16:voxelforge-runner:1");

        Assert.Equal("claimed", observation.DeliveryStatus);
        Assert.Equal("claimed", observation.ClaimStatus);
        Assert.Equal("pending", observation.CompletionStatus);
        Assert.Equal(42, observation.DeliveryRequestId);
        Assert.Equal(7, observation.AttemptId);
    }

    [Fact]
    public void GatewayDirectAgentStatus_MapsCompletedAndSuppressedEvidence()
    {
        var completed = GatewayDirectAgentDeliveryStatus.FromGatewayState(
            BuildGatewayState("direct-agent-message:16:voxelforge-runner:2", "completed", 43, 8),
            "direct-agent-message:16:voxelforge-runner:2");
        var suppressed = GatewayDirectAgentDeliveryStatus.FromGatewayState(
            BuildGatewayState("direct-agent-message:16:voxelforge-runner:3", "suppressed", 44, null),
            "direct-agent-message:16:voxelforge-runner:3");

        Assert.Equal("completed", completed.DeliveryStatus);
        Assert.Equal("completed", completed.CompletionStatus);
        Assert.Equal("suppressed", suppressed.DeliveryStatus);
        Assert.Equal("suppressed", suppressed.SuppressionStatus);
        Assert.Equal("suppressed", suppressed.CompletionStatus);
    }

    [Fact]
    public void GatewayDirectAgentStatus_ReportsRecordedButUnclaimedWhenRequestMissing()
    {
        var state = BuildGatewayState("direct-agent-message:16:someone-else:4", "pending", 45, null);

        var observation = GatewayDirectAgentDeliveryStatus.FromGatewayState(state, "direct-agent-message:16:voxelforge-runner:4");

        Assert.Equal("recorded_but_not_claimed_yet", observation.DeliveryStatus);
        Assert.Equal("unclaimed", observation.ClaimStatus);
        Assert.Equal("pending", observation.CompletionStatus);
    }

    [Fact]
    public async Task GatewayDirectAgentStatus_WaitHonorsCallerTimeoutForSlowGateway()
    {
        using var httpClient = new HttpClient(new SlowGatewayHandler(TimeSpan.FromSeconds(5)));
        var options = Options.Create(new DenChannelsOptions
        {
            Gateway = new GatewayOptions
            {
                BaseUrl = "http://gateway.invalid",
                TimeoutSeconds = 5
            }
        });
        var client = new GatewayStateClient(httpClient, options, NullLogger<GatewayStateClient>.Instance);
        var stopwatch = Stopwatch.StartNew();

        var observation = await client.WaitForDirectAgentDeliveryStatusAsync(
            "voxelforge",
            "voxelforge-runner",
            "direct-agent-message:16:voxelforge-runner:5",
            "claim",
            100,
            CancellationToken.None);

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Elapsed {stopwatch.Elapsed} exceeded caller timeout bound.");
        Assert.Equal("recorded_but_not_claimed_yet", observation.DeliveryStatus);
        Assert.True(observation.TimedOut);
    }

    [Fact]
    public async Task GatewayDirectAgentStatus_TriggerPollPostsNonSeedingProjectPoll()
    {
        var handler = new RecordingGatewayHandler();
        using var httpClient = new HttpClient(handler);
        var options = Options.Create(new DenChannelsOptions
        {
            Gateway = new GatewayOptions
            {
                BaseUrl = "http://gateway.invalid",
                TimeoutSeconds = 5
            }
        });
        var client = new GatewayStateClient(httpClient, options, NullLogger<GatewayStateClient>.Instance);

        var observation = await client.TriggerDeliveryLoopPollAsync("goblinbench", limit: 123, CancellationToken.None);

        Assert.True(observation.Triggered);
        Assert.Equal(HttpMethod.Post, handler.LastRequestMethod);
        Assert.Equal("http://gateway.invalid/api/delivery-loop/poll", handler.LastRequestUri?.ToString());
        Assert.Contains("\"source\":\"channels\"", handler.LastRequestBody);
        Assert.Contains("\"projectId\":\"goblinbench\"", handler.LastRequestBody);
        Assert.Contains("\"limit\":123", handler.LastRequestBody);
        Assert.Contains("\"seedCursorAtLatestWhenMissing\":false", handler.LastRequestBody);
    }

    [Fact]
    public void GatewayDirectAgentStatus_TerminalStatesSatisfyAckWait()
    {
        var failed = GatewayDirectAgentDeliveryStatus.FromGatewayState(
            BuildGatewayState("direct-agent-message:16:voxelforge-runner:6", "failed", 46, 10),
            "direct-agent-message:16:voxelforge-runner:6");
        var expired = GatewayDirectAgentDeliveryStatus.FromGatewayState(
            BuildGatewayState("direct-agent-message:16:voxelforge-runner:7", "expired", 47, null),
            "direct-agent-message:16:voxelforge-runner:7");
        var suppressed = GatewayDirectAgentDeliveryStatus.FromGatewayState(
            BuildGatewayState("direct-agent-message:16:voxelforge-runner:8", "suppressed", 48, null),
            "direct-agent-message:16:voxelforge-runner:8");

        Assert.True(GatewayDirectAgentDeliveryStatus.MeetsWaitTarget(failed, "ack"));
        Assert.True(GatewayDirectAgentDeliveryStatus.MeetsWaitTarget(expired, "ack"));
        Assert.True(GatewayDirectAgentDeliveryStatus.MeetsWaitTarget(suppressed, "ack"));
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
        bool CanReact,
        bool CanInvite,
        int CooldownSeconds,
        int MaxAutoRepliesPerWindow,
        string? SettingsLabel);

    private sealed record GatewayDirectAgentMessagePayload(
        string Status,
        string DeliveryStatus,
        string ClaimStatus,
        string CompletionStatus,
        string SuppressionStatus,
        string MemberIdentity,
        string WakePolicy,
        long MessageId,
        long ChannelId,
        string RequestId,
        string? SourceProjectId,
        string? TargetProjectId,
        int? TargetTaskId,
        int? AssignmentId,
        string? WorkerRunId,
        string? WorkerRole,
        string? ProfileIdentity,
        string? PoolMemberId,
        long? DeliveryRequestId,
        long? AttemptId,
        string? GatewayDeliveryState,
        string? GatewayAttemptStatus,
        bool TimedOut,
        bool GatewayUnavailable,
        string GatewayMessageUrl,
        string GatewayEventsUrl,
        string EvidenceSummary);

    [Fact]
    public async Task GatewayDirectAgentMessages_Post_WithSourceProjectId_UsesCallerProjectNotChannel()
    {
        var channel = await EnsureDefaultChannelAsync("gw-da-srcproj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "smoke-reviewer",
            wakePolicy = "direct_questions_only"
        });

        using var response = await _client.PostAsJsonAsync("/api/gateway/direct-agent-messages", new
        {
            channelId = channel.Id,
            memberIdentity = "smoke-reviewer",
            senderIdentity = "operator",
            body = "Review den-core task #1820",
            sourceProjectId = "den-core",
            targetTaskId = 1820,
            assignmentId = "63",
            waitFor = "none"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GatewayDirectAgentMessagePayload>();
        Assert.NotNull(payload);
        Assert.Equal("recorded", payload.Status);
        Assert.Equal("den-core", payload.SourceProjectId);
        Assert.NotEqual("gw-da-srcproj", payload.SourceProjectId);
        Assert.Equal(1820, payload.TargetTaskId);
        Assert.Equal(63, payload.AssignmentId);

        var message = await _client.GetFromJsonAsync<GatewayMessagePayload>(payload.GatewayMessageUrl);
        Assert.NotNull(message);
        Assert.Equal("den-core", message.SourceProjectId);
    }

    [Fact]
    public async Task GatewayDirectAgentMessages_Post_WithoutSourceProjectId_UsesChannelProject()
    {
        var channel = await EnsureDefaultChannelAsync("gw-da-nosrc");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "wake-reviewer",
            wakePolicy = "direct_questions_only"
        });

        using var response = await _client.PostAsJsonAsync("/api/gateway/direct-agent-messages", new
        {
            channelId = channel.Id,
            memberIdentity = "wake-reviewer",
            senderIdentity = "operator",
            body = "Wake reviewer.",
            waitFor = "none"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GatewayDirectAgentMessagePayload>();
        Assert.NotNull(payload);
        Assert.Equal("gw-da-nosrc", payload.SourceProjectId);
    }

    [Fact]
    public async Task GatewayDirectAgentMessages_Post_SharedControlChannel_DifferentTargetProject()
    {
        // Simulate a shared worker-control channel (e.g. den-hermes-bridge) delivering
        // work for a different target project (e.g. goblinbench). The response DTO and
        // stored message must preserve both source/control attribution and target-work fields.
        var channel = await EnsureDefaultChannelAsync("gw-shared-control");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "goblinbench-worker",
            wakePolicy = "direct_questions_only"
        });

        using var response = await _client.PostAsJsonAsync("/api/gateway/direct-agent-messages", new
        {
            channelId = channel.Id,
            memberIdentity = "goblinbench-worker",
            senderIdentity = "den-hermes-bridge",
            body = "Run task #1845 on goblinbench.",
            sourceProjectId = "den-hermes-bridge",       // control/transport project
            targetProjectId = "goblinbench",             // target work project
            targetTaskId = 1845,
            assignmentId = "81",
            workerRunId = "dc-1845-20260602083828-coder",
            workerRole = "spawned-coder",
            profileIdentity = "den-hermes-coder",
            poolMemberId = "pool-member-81",
            waitFor = "none"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GatewayDirectAgentMessagePayload>();
        Assert.NotNull(payload);

        // Source/control project must be preserved from the request, not inferred from channel
        Assert.Equal("den-hermes-bridge", payload.SourceProjectId);
        Assert.NotEqual("gw-shared-control", payload.SourceProjectId);

        // Target-work fields must be surfaced separately from source/control project
        Assert.Equal("goblinbench", payload.TargetProjectId);
        Assert.Equal(1845, payload.TargetTaskId);
        Assert.Equal(81, payload.AssignmentId);
        Assert.Equal("dc-1845-20260602083828-coder", payload.WorkerRunId);
        Assert.Equal("spawned-coder", payload.WorkerRole);
        Assert.Equal("den-hermes-coder", payload.ProfileIdentity);
        Assert.Equal("pool-member-81", payload.PoolMemberId);

        // Verify the stored message preserves both source/control and target-work fields
        var message = await _client.GetFromJsonAsync<GatewayMessagePayload>(payload.GatewayMessageUrl);
        Assert.NotNull(message);
        Assert.Equal("den-hermes-bridge", message.SourceProjectId);
        Assert.Equal("goblinbench", message.TargetProjectId);
        Assert.Equal(1845, message.TargetTaskId);
        Assert.Equal("81", message.AssignmentId);
        Assert.Equal("dc-1845-20260602083828-coder", message.WorkerRunId);
        Assert.Equal("spawned-coder", message.WorkerRole);
        Assert.Equal("den-hermes-coder", message.ProfileIdentity);
        Assert.Equal("pool-member-81", message.PoolMemberId);

        // Events projection should surface target-work fields
        var events = await _client.GetFromJsonAsync<GatewayEventsPayload>(
            $"/api/gateway/events?channelId={channel.Id}&limit=10");
        Assert.NotNull(events);
        Assert.NotEmpty(events.Items);
        var eventItem = events.Items.FirstOrDefault(e => e.SourceKind == "wake_event");
        Assert.NotNull(eventItem);
        Assert.Equal("goblinbench", eventItem.TargetProjectId);
        Assert.Equal(1845, eventItem.TargetTaskId);
        Assert.Equal("81", eventItem.AssignmentId);
        Assert.Equal("dc-1845-20260602083828-coder", eventItem.WorkerRunId);
        Assert.Equal("spawned-coder", eventItem.WorkerRole);
        Assert.Equal("den-hermes-coder", eventItem.ProfileIdentity);
        Assert.Equal("pool-member-81", eventItem.PoolMemberId);
    }

    private static GatewayStateDto BuildGatewayState(string sourceId, string deliveryStatus, long deliveryRequestId, long? attemptId)
    {
        var delivery = new GatewayDeliveryDto(
            DeliveryRequestId: deliveryRequestId,
            Status: deliveryStatus,
            DeliveryMode: "wake",
            TargetType: "agent",
            TargetIdentity: "voxelforge-runner",
            ProjectId: "voxelforge",
            TaskId: null,
            ChannelId: "16",
            SourceKind: "wake_event",
            SourceId: sourceId,
            SourceProjectId: "voxelforge",
            ContextSummary: "direct agent request",
            ContextLink: null,
            AttemptCount: attemptId is null ? 0 : 1,
            LeaseExpiresAt: null,
            NextAttemptAt: null,
            ExpiresAt: null,
            CreatedAt: "2026-05-29T08:00:00Z",
            UpdatedAt: "2026-05-29T08:00:01Z",
            LastAttempt: attemptId is null
                ? null
                : new GatewayDeliveryAttemptDto(attemptId.Value, 1, 9, "claimed", "claim", "external-1", "session-1", "2026-05-29T08:00:01Z", null, null),
            Flags: []);
        var agent = new GatewayAgentDto(
            AgentKey: "voxelforge:voxelforge-runner:runner",
            ProjectId: "voxelforge",
            AgentIdentity: "voxelforge-runner",
            Role: "runner",
            BindingFreshness: "fresh",
            AdapterInstances: [],
            DeliverySummary: new GatewayDeliverySummaryDto("working", 0, deliveryStatus == "delivering" ? 1 : 0, 0, deliveryStatus == "completed" ? 1 : 0, 0, deliveryStatus == "suppressed" ? 1 : 0, 0, 1),
            CurrentDeliveries: delivery.Terminal ? [] : [delivery],
            RecentDeliveries: delivery.Terminal ? [delivery] : [],
            Flags: []);
        return new GatewayStateDto(
            GeneratedAt: "2026-05-29T08:00:01Z",
            Service: "den-gateway",
            BindingHealth: new GatewayBindingHealthDto("available", 1, 1, 0, null),
            Agents: [agent]);
    }

    private sealed record GatewayMessagePayload(
        long Id,
        long ChannelId,
        string MessageKind,
        string SenderType,
        string SenderIdentity,
        string? SourceKind,
        string? SourceId,
        string? SourceProjectId,
        string? TargetProjectId,
        long? TargetTaskId,
        string? AssignmentId,
        string? WorkerRunId,
        string? WorkerRole,
        string? ProfileIdentity,
        string? PoolMemberId,
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
        string? TargetProjectId,
        long? TargetTaskId,
        string? AssignmentId,
        string? WorkerRunId,
        string? WorkerRole,
        string? ProfileIdentity,
        string? PoolMemberId,
        string? DedupeKey,
        string? DeepLink,
        string? Summary,
        string Body,
        string CreatedAt);

    private sealed class RecordingGatewayHandler : HttpMessageHandler
    {
        public HttpMethod? LastRequestMethod { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestMethod = request.Method;
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { status = "completed" })
            };
        }
    }

    private sealed class SlowGatewayHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(BuildGatewayState(
                    "direct-agent-message:16:voxelforge-runner:5",
                    "delivering",
                    45,
                    9))
            };
        }
    }

    // =========================================================================
    // Assignment trace aggregate tests (#1737)
    // =========================================================================

    [Fact]
    public async Task AssignmentTrace_ReturnsMessagesAndActivity_WhenAvailable()
    {
        // Arrange: create a channel and post a message with assignment metadata
        var channel = await EnsureDefaultChannelAsync("trace-test-proj-1");
        var assignmentId = "test-assignment-001";

        // Post a direct-agent-style message with AssignmentId and DeliveryRequestId
        var metadataJson = "{\"deliveryStatus\":\"recorded_pending_claim\",\"claimStatus\":\"unclaimed\",\"completionStatus\":\"pending\",\"suppressionStatus\":\"not_suppressed\"}";
        var msg = await PostMessageAsync(channel.Id, new
        {
            senderType = "user",
            senderIdentity = "test-user",
            body = "Assignment trace test message",
            messageKind = "human_text",
            sourceKind = "wake_event",
            sourceId = $"test-wake:{assignmentId}",
            sourceProjectId = "trace-test-proj-1",
            assignmentId,
            deliveryRequestId = $"delivery:{assignmentId}",
            metadataJson
        });

        // Act
        var trace = await _client.GetFromJsonAsync<AssignmentTracePayload>(
            $"/api/gateway/assignments/{assignmentId}/trace?projectId=trace-test-proj-1");

        // Assert
        Assert.NotNull(trace);
        Assert.Equal(assignmentId, trace.AssignmentId);
        Assert.Equal("trace-test-proj-1", trace.ProjectId);
        Assert.NotEmpty(trace.ChannelMessages);
        Assert.Equal("available", trace.MessagesAvailability);

        // Should have the posted message
        var traceMsg = Assert.Single(trace.ChannelMessages);
        Assert.Equal(msg.Body, traceMsg.Body);

        // Gateway evidence should be extracted from metadata
        Assert.NotNull(trace.GatewayEvidence);
        Assert.Equal($"delivery:{assignmentId}", trace.GatewayEvidence.DeliveryRequestId);
        Assert.Equal("recorded_pending_claim", trace.GatewayEvidence.DeliveryStatus);
        Assert.Equal("unclaimed", trace.GatewayEvidence.ClaimStatus);
        Assert.Equal("pending", trace.GatewayEvidence.CompletionStatus);
        Assert.Contains("delivery:", trace.GatewayEvidence.EvidenceSummary ?? "");

        // Core is unavailable in test environment
        Assert.Equal("core_unavailable", trace.CoreAvailability);
        Assert.Null(trace.CoreState);

        // Summary should be present
        Assert.NotNull(trace.Summary);
        Assert.Contains(assignmentId, trace.Summary);
        Assert.Contains("message(s)", trace.Summary);
    }

    [Fact]
    public async Task AssignmentTrace_ReturnsNoAssignmentMessages_WhenNoMatchingMessages()
    {
        // Arrange
        await EnsureDefaultChannelAsync("trace-test-proj-empty");

        // Act
        var trace = await _client.GetFromJsonAsync<AssignmentTracePayload>(
            "/api/gateway/assignments/nonexistent-42/trace?projectId=trace-test-proj-empty");

        // Assert
        Assert.NotNull(trace);
        Assert.Equal("nonexistent-42", trace.AssignmentId);
        Assert.Equal("no_assignment_messages", trace.MessagesAvailability);
        Assert.Equal("no_activity_events", trace.ActivityAvailability);
        Assert.Empty(trace.ChannelMessages);
        Assert.Empty(trace.ActivityEvents);
        Assert.Null(trace.GatewayEvidence);
        Assert.Equal("no_assignment_messages", trace.GatewayAvailability);
        Assert.NotNull(trace.Summary);
        Assert.Contains("no messages", trace.Summary);
    }

    [Fact]
    public async Task AssignmentTrace_ReturnsBadRequest_WhenMissingProjectIdAndChannelId()
    {
        using var response = await _client.GetAsync("/api/gateway/assignments/test-1/trace");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AssignmentTrace_WorksViaDenWebAliasPath()
    {
        // The Den Web path alias should also work
        var channel = await EnsureDefaultChannelAsync("trace-alias-proj");
        var assignmentId = "alias-test-002";
        await PostMessageAsync(channel.Id, new
        {
            senderType = "user",
            senderIdentity = "test-user",
            body = "Alias path test",
            messageKind = "human_text",
            assignmentId
        });

        var trace = await _client.GetFromJsonAsync<AssignmentTracePayload>(
            $"/api/assignments/{assignmentId}/trace?projectId=trace-alias-proj");

        Assert.NotNull(trace);
        Assert.Equal(assignmentId, trace.AssignmentId);
        Assert.NotEmpty(trace.ChannelMessages);
    }

    [Fact]
    public async Task AssignmentTrace_DeliveryMissing_WhenMessagesExistWithoutDeliveryRequestId()
    {
        var channel = await EnsureDefaultChannelAsync("trace-no-delivery-proj");
        var assignmentId = "no-delivery-assignment";
        await PostMessageAsync(channel.Id, new
        {
            senderType = "user",
            senderIdentity = "test-user",
            body = "Message without delivery tracking",
            messageKind = "human_text",
            assignmentId
            // No deliveryRequestId
        });

        var trace = await _client.GetFromJsonAsync<AssignmentTracePayload>(
            $"/api/gateway/assignments/{assignmentId}/trace?projectId=trace-no-delivery-proj");

        Assert.NotNull(trace);
        Assert.Equal("available", trace.MessagesAvailability);
        Assert.Equal("delivery_missing", trace.GatewayAvailability);
        Assert.Null(trace.GatewayEvidence);
    }

    [Fact]
    public async Task AssignmentTrace_UsesDirectAgentMetadataRequestId_WhenDeliveryRequestIdMissing()
    {
        var channel = await EnsureDefaultChannelAsync("trace-direct-agent-metadata-proj");
        var assignmentId = "direct-agent-metadata-assignment";
        var requestId = $"direct-agent-message:{channel.Id}:worker:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        await PostMessageAsync(channel.Id, new
        {
            senderType = "user",
            senderIdentity = "test-user",
            body = "Direct-agent wake metadata without deliveryRequestId",
            messageKind = "human_text",
            assignmentId,
            metadataJson = $$"""
            {
              "requestId": "{{requestId}}",
              "deliveryStatus": "recorded_pending_claim",
              "claimStatus": "unclaimed",
              "completionStatus": "pending",
              "suppressionStatus": "not_suppressed",
              "evidence": { "gatewayEventsUrl": "/api/gateway/events?channelId={{channel.Id}}&afterId=0&limit=50" }
            }
            """
        });

        var trace = await _client.GetFromJsonAsync<AssignmentTracePayload>(
            $"/api/gateway/assignments/{assignmentId}/trace?projectId=trace-direct-agent-metadata-proj");

        Assert.NotNull(trace);
        Assert.Equal("available", trace.MessagesAvailability);
        Assert.Equal("available", trace.GatewayAvailability);
        Assert.NotNull(trace.GatewayEvidence);
        Assert.Equal(requestId, trace.GatewayEvidence.DeliveryRequestId);
        Assert.Equal("recorded_pending_claim", trace.GatewayEvidence.DeliveryStatus);
        Assert.Equal($"/api/gateway/events?channelId={channel.Id}&afterId=0&limit=50", trace.GatewayEvidence.GatewayEventsUrl);
    }

    // ---- Assignment trace local payload records ----

    private sealed record AssignmentTracePayload(
        string AssignmentId,
        string? ProjectId,
        string? ProjectName,
        long? TaskId,
        string? TaskTitle,
        string? AgentIdentity,
        string? WorkerRunId,
        string? WorkerRole,
        string CoreAvailability,
        string GatewayAvailability,
        string MessagesAvailability,
        string ActivityAvailability,
        AssignmentCoreStatePayload? CoreState,
        AssignmentGatewayEvidencePayload? GatewayEvidence,
        List<MessagePayload> ChannelMessages,
        List<ActivityEventPayload> ActivityEvents,
        string? Summary);

    private sealed record AssignmentCoreStatePayload(
        string? Phase,
        string? AssignedAt,
        string? AssignedAgent,
        string? LeaseAcquiredAt,
        string? LeaseExpiresAt,
        List<AssignmentCheckpointPayload>? Checkpoints,
        string? FinalStatus,
        string? FinalStatusAt,
        string? CleanupState,
        string? CleanupTriggeredAt,
        string? CleanupCompletedAt,
        string? ReleaseState,
        bool Quarantined,
        string? QuarantinedAt);

    private sealed record AssignmentCheckpointPayload(
        int Sequence,
        string? CheckpointRequestAt,
        string? CheckpointResponseAt,
        string? Status,
        string? SnapshotPreview,
        string? Error);

    private sealed record AssignmentGatewayEvidencePayload(
        string? DeliveryRequestId,
        string? DeliveryStatus,
        string? ClaimStatus,
        string? CompletionStatus,
        string? SuppressionStatus,
        string? RequestedAt,
        string? DeliveredAt,
        string? ClaimedAt,
        string? CompletedAt,
        string? EvidenceSummary,
        string? GatewayMessageUrl,
        string? GatewayEventsUrl);

    private sealed record MessagePayload(
        long Id,
        long ChannelId,
        string SenderType,
        string SenderIdentity,
        string Body,
        string MessageKind,
        string? SourceKind,
        string? SourceId,
        string? SourceProjectId,
        string? Summary,
        string? DeepLink,
        string? DeliveryRequestId,
        string? DedupeKey,
        string? AssignmentId,
        string? CheckpointType,
        string? CheckpointHandle,
        string CreatedAt,
        string? EditedAt,
        string? DeletedAt);

    private sealed record ActivityEventPayload(
        long Id,
        long ChannelId,
        string? ProjectId,
        string AgentIdentity,
        string? DeliveryRequestId,
        string? HermesSessionKey,
        string? DisplayBlockId,
        string? WorkerRunId,
        string? WorkerRole,
        long? TaskId,
        string? AssignmentId,
        string? CheckpointType,
        string? CheckpointHandle,
        string EventType,
        string Status,
        string DeliveryStage,
        bool Terminal,
        long Sequence,
        long UpdateVersion,
        string? Title,
        string? Summary,
        string? PreviewJson,
        string? MetadataJson,
        string? DedupeKey,
        long? FinalChannelMessageId,
        string CreatedAt,
        string UpdatedAt);
}
