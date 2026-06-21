using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DenChannels.Service.AgentsOverview;
using DenChannels.Service.Configuration;
using DenChannels.Service.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DenChannels.Service.Tests;

/// <summary>
/// Tests for the /api/gateway endpoint group (task #1351).
/// After task #2022, the Gateway compatibility aliases for direct-agent
/// events, events lists, test-wakes, and channel-activity-events routes
/// return 410 Gone with canonical replacement pointers.
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
        // Must NOT advertise any Gateway compatibility aliases as active green paths
        Assert.DoesNotContain(payload.Endpoints, e => e.Contains("/api/gateway/direct-agent-messages"));
        Assert.DoesNotContain(payload.Endpoints, e => e.Contains("/api/gateway/test-wakes"));
        Assert.DoesNotContain(payload.Endpoints, e => e.Contains("/api/gateway/events"));
        Assert.DoesNotContain(payload.Endpoints, e => e.Contains("/api/gateway/channel-activity-events"));
        // Canonical direct-agent route should be advertised
        Assert.Contains(payload.Endpoints, e => e.Contains("/api/direct-agent-events"));
    }

    // -------------------------------------------------------------------------
    // Membership lookup (keep)
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
    public async Task GatewayMemberships_CanFilterLeftMembersByGracePeriodAndExposeAgeFields()
    {
        var channel = await EnsureDefaultChannelAsync("gw-test-left-grace");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "active-agent",
            wakePolicy = "mentions_only",
            membershipPurpose = "target_work"
        });
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "recent-left-agent",
            membershipStatus = "left",
            wakePolicy = "never",
            membershipPurpose = "target_work"
        });
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "stale-left-agent",
            membershipStatus = "left",
            wakePolicy = "never",
            membershipPurpose = "target_work"
        });
        await SetMembershipUpdatedAtMinutesAgoAsync(channel.Id, "stale-left-agent", 45);

        var defaultPayload = await _client.GetFromJsonAsync<GatewayMembershipsPayload>(
            $"/api/gateway/memberships?channelId={channel.Id}");
        Assert.NotNull(defaultPayload);
        Assert.Equal(3, defaultPayload.Members.Count);

        var gracePayload = await _client.GetFromJsonAsync<GatewayMembershipsPayload>(
            $"/api/gateway/memberships?channelId={channel.Id}&leftGraceMinutes=30");
        Assert.NotNull(gracePayload);
        Assert.DoesNotContain(gracePayload.Members, m => m.MemberIdentity == "stale-left-agent");
        Assert.Contains(gracePayload.Members, m => m.MemberIdentity == "active-agent");
        var recentLeft = Assert.Single(gracePayload.Members, m => m.MemberIdentity == "recent-left-agent");
        Assert.Equal("left", recentLeft.MembershipStatus);
        Assert.Equal("target_work", recentLeft.MembershipPurpose);
        Assert.False(string.IsNullOrWhiteSpace(recentLeft.UpdatedAt));
        Assert.Equal(recentLeft.UpdatedAt, recentLeft.LeftAt);

        var activeOnlyPayload = await _client.GetFromJsonAsync<GatewayMembershipsPayload>(
            $"/api/gateway/memberships?channelId={channel.Id}&includeLeft=false");
        Assert.NotNull(activeOnlyPayload);
        var activeOnly = Assert.Single(activeOnlyPayload.Members);
        Assert.Equal("active-agent", activeOnly.MemberIdentity);
        Assert.Null(activeOnly.LeftAt);
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
    // Message lookup (keep)
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

        var payload = await _client.GetFromJsonAsync<GatewayMessageDto>(
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
    // Source pointer lookup (keep)
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

        var messages = await _client.GetFromJsonAsync<List<GatewayMessageDto>>(
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

        var messages = await _client.GetFromJsonAsync<List<GatewayMessageDto>>(
            "/api/gateway/sources/worker_run/run-999");

        Assert.NotNull(messages);
        Assert.NotEmpty(messages);
        Assert.All(messages, m => Assert.Equal("worker_run", m.SourceKind));
        Assert.All(messages, m => Assert.Equal("run-999", m.SourceId));
    }

    // =========================================================================
    // TOMBSTONED: GET /api/gateway/events
    // Replaced by GET /api/direct-agent-events
    // =========================================================================

    [Fact]
    public async Task GatewayEvents_Returns410Gone_WithReplacementRoute()
    {
        using var response = await _client.GetAsync("/api/gateway/events?channelId=1&limit=3");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var body = await AssertGatewayTombstone(response, "GET /api/direct-agent-events");
    }

    [Fact]
    public async Task GatewayEvents_MissingParams_Returns410Gone()
    {
        using var response = await _client.GetAsync("/api/gateway/events");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var body = await AssertGatewayTombstone(response, "GET /api/direct-agent-events");
    }

    [Fact]
    public async Task GatewayEvents_AfterIdCursor_Returns410Gone()
    {
        using var response = await _client.GetAsync("/api/gateway/events?channelId=1&afterId=5&limit=4");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var body = await AssertGatewayTombstone(response, "GET /api/direct-agent-events");
    }

    [Fact]
    public async Task GatewayEvents_ByProjectId_Returns410Gone()
    {
        using var response = await _client.GetAsync("/api/gateway/events?projectId=any-proj&limit=10");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var body = await AssertGatewayTombstone(response, "GET /api/direct-agent-events");
    }

    // =========================================================================
    // System message post (keep — still live for delivery system)
    // =========================================================================

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
        var created = await response1.Content.ReadFromJsonAsync<GatewayMessageDto>();
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
        var existing = await response2.Content.ReadFromJsonAsync<GatewayMessageDto>();
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
        var payload = await response.Content.ReadFromJsonAsync<GatewayMessageDto>();
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
        var payload = await response.Content.ReadFromJsonAsync<GatewayMessageDto>();
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
        var payload = await response.Content.ReadFromJsonAsync<GatewayMessageDto>();
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

    // =========================================================================
    // TOMBSTONED: POST /api/gateway/test-wakes
    // Replaced by Delivery successor direct-agent intents.
    // =========================================================================

    [Fact]
    public async Task GatewayTestWakes_Post_Returns410Gone_WithReplacementRoute()
    {
        using var response = await _client.PostAsJsonAsync("/api/gateway/test-wakes", new
        {
            channelId = 1,
            memberIdentity = "hermes-coder",
            requestedBy = "tester"
        });
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var body = await AssertGatewayTombstone(response, "POST /v1/delivery/intents");
    }

    // =========================================================================
    // TOMBSTONED: POST /api/gateway/direct-agent-messages
    // Replaced by Delivery successor direct-agent intents.
    // =========================================================================

    [Fact]
    public async Task GatewayDirectAgentMessages_Post_Returns410Gone_WithReplacementRoute()
    {
        using var response = await _client.PostAsJsonAsync("/api/gateway/direct-agent-messages", new
        {
            channelId = 1,
            memberIdentity = "hermes-reviewer",
            senderIdentity = "operator",
            body = "Please review the fix."
        });
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var body = await AssertGatewayTombstone(response, "POST /v1/delivery/intents");
    }

    [Fact]
    public async Task GatewayDirectAgentMessages_Post_AnyPayload_Returns410Gone()
    {
        using var response = await _client.PostAsJsonAsync("/api/gateway/direct-agent-messages", new
        {
            channelId = 1,
            memberIdentity = "spawned-coder",
            senderIdentity = "operator",
            body = "Wake with target-work fields.",
            sourceProjectId = "den-core",
            targetProjectId = "goblinbench",
            targetTaskId = 1845,
            assignmentId = "81",
            workerRunId = "run-001",
            workerRole = "spawned-coder",
            profileIdentity = "den-hermes-coder",
            poolMemberId = "pool-member-81",
            agentInstanceId = "inst-001",
            sessionOwnerId = "runner-001",
            sessionId = "session-abc"
        });
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var body = await AssertGatewayTombstone(response, "POST /v1/delivery/intents");
    }

    // =========================================================================
    // TOMBSTONED: POST /api/gateway/channel-activity-events
    // Replaced by POST /api/channels/{channelId}/activity-events
    // =========================================================================

    [Fact]
    public async Task GatewayChannelActivityEvents_Post_Returns410Gone_WithReplacementRoute()
    {
        using var response = await _client.PostAsJsonAsync("/api/gateway/channel-activity-events", new
        {
            channelId = 1,
            agentIdentity = "test-agent",
            eventType = "lifecycle_status",
            status = "interim"
        });
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var body = await AssertGatewayTombstone(response, "POST /api/channels/{channelId}/activity-events");
    }

    // =========================================================================
    // TOMBSTONED: GET /api/gateway/channel-activity-events/status
    // Replaced by GET /api/channel-activity-events/status
    // =========================================================================

    [Fact]
    public async Task GatewayChannelActivityEventsStatus_Get_Returns410Gone_WithReplacementRoute()
    {
        using var response = await _client.GetAsync("/api/gateway/channel-activity-events/status");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var body = await AssertGatewayTombstone(response, "GET /api/channel-activity-events/status");
    }

    // =========================================================================
    // Existing APIs still work
    // =========================================================================

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

    // =========================================================================
    // Assignment trace aggregate tests (#1737) — keep
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
              "evidence": { "gatewayEventsUrl": "/api/direct-agent-events?channelId={{channel.Id}}&afterId=0&limit=50" }
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
        Assert.Equal($"/api/direct-agent-events?channelId={channel.Id}&afterId=0&limit=50", trace.GatewayEvidence.GatewayEventsUrl);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Asserts a Gateway tombstone 410 response has the expected JSON body
    /// with a canonical route pointer. Returns the parsed JSON body.
    /// </summary>
    private async Task<JsonElement> AssertGatewayTombstone(HttpResponseMessage response, string expectedReplacement)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.NotEmpty(raw);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        // Must have a code field indicating the route is gone
        Assert.True(root.TryGetProperty("code", out var code), "Tombstone response missing 'code' field");
        Assert.Equal("route_gone", code.GetString());

        // Must have a message indicating retirement
        Assert.True(root.TryGetProperty("message", out var message), "Tombstone response missing 'message' field");
        Assert.Contains("retired", message.GetString() ?? "", StringComparison.OrdinalIgnoreCase);

        // Must have a replacement field pointing to the canonical route
        Assert.True(root.TryGetProperty("replacement", out var replacement), "Tombstone response missing 'replacement' field");
        Assert.Equal(expectedReplacement, replacement.GetString());

        return root;
    }

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

    private async Task SetMembershipUpdatedAtMinutesAgoAsync(long channelId, string memberIdentity, int minutesAgo)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE channel_memberships
            SET updated_at = datetime('now', '-' || $minutesAgo || ' minutes')
            WHERE channel_id = $channelId
              AND member_identity = $memberIdentity;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$memberIdentity", memberIdentity);
        command.Parameters.AddWithValue("$minutesAgo", minutesAgo);
        var updated = await command.ExecuteNonQueryAsync();
        Assert.Equal(1, updated);
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
        string? SettingsLabel,
        string? MembershipPurpose,
        string CreatedAt,
        string UpdatedAt,
        string? LeftAt);

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
        string? SessionKey,
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
