using System.Net;
using System.Net.Http.Json;
using DenChannels.Service.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenChannels.Service.Tests;

/// <summary>
/// Tests for the Channels-owned /api/direct-agent-events endpoint group (task #1902).
/// Verifies durable event creation/readback works without any Gateway dependency.
/// </summary>
public sealed class DirectAgentEventTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"den-channels-dae-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public DirectAgentEventTests()
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
    // POST /api/direct-agent-events — basic recording
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PostDirectAgentEvent_ReturnsRecordedWithEventId()
    {
        var channel = await EnsureDefaultChannelAsync("dae-test-proj-1");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "test-runner",
            wakePolicy = "direct_questions_only"
        });

        using var response = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            channelId = channel.Id,
            memberIdentity = "test-runner",
            senderIdentity = "operator",
            body = "Run the integration tests."
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<DirectAgentEventPayload>();
        Assert.NotNull(payload);
        Assert.Equal("recorded", payload.Status);
        Assert.True(payload.EventId > 0);
        Assert.Equal(channel.Id, payload.ChannelId);
        Assert.Equal("test-runner", payload.MemberIdentity);
        Assert.Equal("subscription", payload.WakePolicy);
        Assert.Equal("recorded_no_subscriber", payload.DeliveryStatus);
        Assert.Equal("no_subscriber", payload.ClaimStatus);
        Assert.Equal("pending", payload.CompletionStatus);
        Assert.Equal(0, payload.ActiveSubscriptionCount);
        Assert.StartsWith($"direct-agent-message:{channel.Id}:test-runner:", payload.RequestId);
        Assert.Contains($"/api/direct-agent-events/{payload.EventId}", payload.EventUrl);
        Assert.Contains($"/api/direct-agent-events?channelId={channel.Id}", payload.EventsUrl);
        Assert.Contains("recorded", payload.EvidenceSummary);
    }

    [Fact]
    public async Task PostDirectAgentEvent_WithCoordinationMetadata_SurfacesReadbackFields()
    {
        var channel = await EnsureDefaultChannelAsync("dae-coordination-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "coordination-agent",
            wakePolicy = "all_messages_except_self"
        });

        using var response = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            channelId = channel.Id,
            memberIdentity = "coordination-agent",
            senderIdentity = "orchestrator",
            body = "Run a coordination call.",
            metadataJson = "{\"coordinationCallId\":\"coord-2106\",\"requestKind\":\"tool_call\",\"resultDestinationJson\":\"{\\\"projectId\\\":\\\"den-channels\\\"}\"}"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<DirectAgentEventPayload>();
        Assert.NotNull(payload);
        Assert.Equal("coord-2106", payload.CoordinationCallId);
        Assert.Equal("tool_call", payload.RequestKind);
        Assert.Equal("{\"projectId\":\"den-channels\"}", payload.ResultDestinationJson);
    }

    // -------------------------------------------------------------------------
    // No Gateway dependency — the endpoint works without Gateway being available
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PostDirectAgentEvent_WorksWhenGatewayIsStopped()
    {
        // The /api/direct-agent-events endpoint does not use GatewayStateClient
        // at all. It records the durable message and returns immediately.
        var channel = await EnsureDefaultChannelAsync("dae-no-gw-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "solo-runner",
            wakePolicy = "all_messages_except_self"
        });

        using var response = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            channelId = channel.Id,
            memberIdentity = "solo-runner",
            senderIdentity = "orchestrator",
            body = "No Gateway needed."
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<DirectAgentEventPayload>();
        Assert.NotNull(payload);
        Assert.Equal("recorded", payload.Status);
        Assert.True(payload.EventId > 0);
    }

    // -------------------------------------------------------------------------
    // GET /api/direct-agent-events/{eventId} — readback
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetDirectAgentEvent_ReturnsFullReadback()
    {
        var channel = await EnsureDefaultChannelAsync("dae-readback-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "readback-agent",
            wakePolicy = "direct_questions_only"
        });

        // Record event
        using var postResponse = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            channelId = channel.Id,
            memberIdentity = "readback-agent",
            senderIdentity = "operator",
            body = "Readback test body.",
            sourceProjectId = "dae-readback-proj",
            targetProjectId = "target-project",
            targetTaskId = 1902,
            assignmentId = "143",
            workerRunId = "dc-1902-run",
            workerRole = "spawned-coder",
            profileIdentity = "den-hermes-coder",
            poolMemberId = "pool-143",
            agentInstanceId = "inst-143",
            sessionOwnerId = "runner-143",
            sessionId = "session-143"
        });
        var postPayload = await postResponse.Content.ReadFromJsonAsync<DirectAgentEventPayload>();
        Assert.NotNull(postPayload);

        // Readback
        var readback = await _client.GetFromJsonAsync<DirectAgentEventReadbackPayload>(
            $"/api/direct-agent-events/{postPayload.EventId}");

        Assert.NotNull(readback);
        Assert.Equal(postPayload.EventId, readback.EventId);
        Assert.Equal(channel.Id, readback.ChannelId);
        Assert.Equal("readback-agent", readback.MemberIdentity);
        Assert.Equal("subscription", readback.WakePolicy);
        Assert.Equal("wake_event", readback.SourceKind);

        Assert.Equal("target-project", readback.TargetProjectId);
        Assert.Equal(1902, readback.TargetTaskId);
        Assert.Equal("143", readback.AssignmentId);
        Assert.Equal("dc-1902-run", readback.WorkerRunId);
        Assert.Equal("spawned-coder", readback.WorkerRole);
        Assert.Equal("den-hermes-coder", readback.ProfileIdentity);
        Assert.Equal("pool-143", readback.PoolMemberId);
        Assert.Equal("inst-143", readback.AgentInstanceId);
        Assert.Equal("runner-143", readback.SessionOwnerId);
        Assert.Equal("session-143", readback.SessionId);
        Assert.Equal("Readback test body.", readback.Body);
        Assert.Equal("user", readback.SenderType);
        Assert.Equal("operator", readback.SenderIdentity);
        Assert.Equal("recorded_no_subscriber", readback.DeliveryStatus);
        Assert.Equal("no_subscriber", readback.ClaimStatus);
        Assert.Equal("pending", readback.CompletionStatus);
        Assert.Equal(0, readback.ActiveSubscriptionCount);
        Assert.Empty(readback.SubscriptionStatuses);
        Assert.NotEmpty(readback.CreatedAt);
    }

    [Fact]
    public async Task GetDirectAgentEvent_NotFound_Returns404()
    {
        using var response = await _client.GetAsync("/api/direct-agent-events/99999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetDirectAgentEvent_NonWakeEvent_Returns404()
    {
        var channel = await EnsureDefaultChannelAsync("dae-non-wake-proj");
        // Post a regular message (not a wake_event)
        var msg = await PostMessageAsync(channel.Id, new
        {
            senderType = "user",
            senderIdentity = "operator",
            body = "Regular message, not a wake event",
            messageKind = "human_text"
        });

        using var response = await _client.GetAsync($"/api/direct-agent-events/{msg.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Multiple source rooms with same durable agent
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PostDirectAgentEvent_MultipleSourceRooms_SameAgent()
    {
        // Two source channels targeting the same agent identity
        var channelA = await EnsureDefaultChannelAsync("dae-room-a");
        await UpsertMembershipAsync(channelA.Id, new
        {
            memberType = "agent",
            memberIdentity = "shared-runner",
            wakePolicy = "all_messages_except_self"
        });

        var channelB = await EnsureDefaultChannelAsync("dae-room-b");
        await UpsertMembershipAsync(channelB.Id, new
        {
            memberType = "agent",
            memberIdentity = "shared-runner",
            wakePolicy = "all_messages_except_self"
        });

        // Same agent instance/session across both channels
        const string sharedInstanceId = "inst-shared-001";
        const string sharedSessionOwnerId = "runner-profile";
        const string sharedSessionId = "session-abc";

        // Post from room A
        using var responseA = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            channelId = channelA.Id,
            memberIdentity = "shared-runner",
            senderIdentity = "operator",
            body = "Message from room A",
            sourceProjectId = "dae-room-a",
            agentInstanceId = sharedInstanceId,
            sessionOwnerId = sharedSessionOwnerId,
            sessionId = sharedSessionId,
            workerRunId = "run-a-001",
            workerRole = "spawned-coder"
        });
        Assert.Equal(HttpStatusCode.Created, responseA.StatusCode);
        var payloadA = await responseA.Content.ReadFromJsonAsync<DirectAgentEventPayload>();
        Assert.NotNull(payloadA);
        Assert.Equal(sharedInstanceId, payloadA.AgentInstanceId);
        Assert.Equal(sharedSessionOwnerId, payloadA.SessionOwnerId);
        Assert.Equal(sharedSessionId, payloadA.SessionId);

        // Post from room B
        using var responseB = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            channelId = channelB.Id,
            memberIdentity = "shared-runner",
            senderIdentity = "operator",
            body = "Message from room B",
            sourceProjectId = "dae-room-b",
            agentInstanceId = sharedInstanceId,
            sessionOwnerId = sharedSessionOwnerId,
            sessionId = sharedSessionId,
            workerRunId = "run-b-002",
            workerRole = "spawned-coder"
        });
        Assert.Equal(HttpStatusCode.Created, responseB.StatusCode);
        var payloadB = await responseB.Content.ReadFromJsonAsync<DirectAgentEventPayload>();
        Assert.NotNull(payloadB);
        Assert.Equal(sharedInstanceId, payloadB.AgentInstanceId);
        Assert.Equal(sharedSessionOwnerId, payloadB.SessionOwnerId);
        Assert.Equal(sharedSessionId, payloadB.SessionId);

        // Different channels/source projects
        Assert.NotEqual(payloadA.ChannelId, payloadB.ChannelId);
        Assert.Equal("dae-room-a", payloadA.SourceProjectId);
        Assert.Equal("dae-room-b", payloadB.SourceProjectId);

        // Same agent instance identity
        Assert.Equal(payloadA.AgentInstanceId, payloadB.AgentInstanceId);
        Assert.Equal(payloadA.SessionOwnerId, payloadB.SessionOwnerId);
        Assert.Equal(payloadA.SessionId, payloadB.SessionId);

        // Readback verifies both events are retrievable
        var readbackA = await _client.GetFromJsonAsync<DirectAgentEventReadbackPayload>(
            $"/api/direct-agent-events/{payloadA.EventId}");
        Assert.NotNull(readbackA);
        Assert.Equal("Message from room A", readbackA.Body);
        Assert.Equal(sharedInstanceId, readbackA.AgentInstanceId);

        var readbackB = await _client.GetFromJsonAsync<DirectAgentEventReadbackPayload>(
            $"/api/direct-agent-events/{payloadB.EventId}");
        Assert.NotNull(readbackB);
        Assert.Equal("Message from room B", readbackB.Body);
        Assert.Equal(sharedInstanceId, readbackB.AgentInstanceId);
    }

    // -------------------------------------------------------------------------
    // Two shared-profile worker pool members (distinct instances)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PostDirectAgentEvent_TwoSharedProfileWorkers_DistinctInstances()
    {
        var channel = await EnsureDefaultChannelAsync("dae-shared-profile-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "spawned-coder",
            wakePolicy = "all_messages_except_self"
        });

        // Worker A: assignment 201
        using var responseA = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            channelId = channel.Id,
            memberIdentity = "spawned-coder",
            senderIdentity = "orch-runner",
            body = "Assignment 201: implement task #1902",
            sourceProjectId = "dae-shared-profile-proj",
            profileIdentity = "den-hermes-coder",
            agentInstanceId = "inst-201",
            poolMemberId = "pool-201",
            sessionOwnerId = "runner-201",
            sessionId = "session-201",
            workerRunId = "dc-1902-run-201",
            workerRole = "spawned-coder",
            assignmentId = "201"
        });
        Assert.Equal(HttpStatusCode.Created, responseA.StatusCode);
        var payloadA = await responseA.Content.ReadFromJsonAsync<DirectAgentEventPayload>();
        Assert.NotNull(payloadA);
        Assert.Equal("inst-201", payloadA.AgentInstanceId);
        Assert.Equal("pool-201", payloadA.PoolMemberId);
        Assert.Equal("runner-201", payloadA.SessionOwnerId);
        Assert.Equal("session-201", payloadA.SessionId);

        // Worker B: assignment 202 — same profile, distinct instance
        using var responseB = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            channelId = channel.Id,
            memberIdentity = "spawned-coder",
            senderIdentity = "orch-runner",
            body = "Assignment 202: implement task #1903",
            sourceProjectId = "dae-shared-profile-proj",
            profileIdentity = "den-hermes-coder",
            agentInstanceId = "inst-202",
            poolMemberId = "pool-202",
            sessionOwnerId = "runner-202",
            sessionId = "session-202",
            workerRunId = "dc-1903-run-202",
            workerRole = "spawned-coder",
            assignmentId = "202"
        });
        Assert.Equal(HttpStatusCode.Created, responseB.StatusCode);
        var payloadB = await responseB.Content.ReadFromJsonAsync<DirectAgentEventPayload>();
        Assert.NotNull(payloadB);
        Assert.Equal("inst-202", payloadB.AgentInstanceId);
        Assert.Equal("pool-202", payloadB.PoolMemberId);
        Assert.Equal("runner-202", payloadB.SessionOwnerId);
        Assert.Equal("session-202", payloadB.SessionId);

        // Same profile identity but distinct instance/session/pool
        Assert.Equal(payloadA.ProfileIdentity, payloadB.ProfileIdentity);
        Assert.NotEqual(payloadA.AgentInstanceId, payloadB.AgentInstanceId);
        Assert.NotEqual(payloadA.PoolMemberId, payloadB.PoolMemberId);
        Assert.NotEqual(payloadA.SessionOwnerId, payloadB.SessionOwnerId);
        Assert.NotEqual(payloadA.SessionId, payloadB.SessionId);
        Assert.NotEqual(payloadA.WorkerRunId, payloadB.WorkerRunId);
        Assert.NotEqual(payloadA.AssignmentId, payloadB.AssignmentId);

        // Readback verifies both are durable and distinct
        var readbackA = await _client.GetFromJsonAsync<DirectAgentEventReadbackPayload>(
            $"/api/direct-agent-events/{payloadA.EventId}");
        Assert.NotNull(readbackA);
        Assert.Equal("inst-201", readbackA.AgentInstanceId);
        Assert.Equal("pool-201", readbackA.PoolMemberId);
        Assert.Equal("runner-201", readbackA.SessionOwnerId);

        var readbackB = await _client.GetFromJsonAsync<DirectAgentEventReadbackPayload>(
            $"/api/direct-agent-events/{payloadB.EventId}");
        Assert.NotNull(readbackB);
        Assert.Equal("inst-202", readbackB.AgentInstanceId);
        Assert.Equal("pool-202", readbackB.PoolMemberId);
        Assert.Equal("runner-202", readbackB.SessionOwnerId);
    }

    [Theory]
    [InlineData("idle", "recorded_pending_claim", "unclaimed", "pending")]
    [InlineData("busy", "claimed", "claimed", "pending")]
    [InlineData("degraded", "recorded_unreachable_subscription", "subscription_unreachable", "failed")]
    public async Task DirectAgentEvent_ReadbackUsesChannelSubscriptionState(
        string subscriptionStatus,
        string expectedDeliveryStatus,
        string expectedClaimStatus,
        string expectedCompletionStatus)
    {
        var channel = await EnsureDefaultChannelAsync($"dae-subscription-state-{subscriptionStatus}");
        await UpsertSubscriptionAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "subscribed-agent",
            subscriptionIdentity = $"subscribed-agent:{subscriptionStatus}",
            subscriptionPurpose = "target_work",
            subscriptionStatus
        });

        using var postResponse = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            channelId = channel.Id,
            memberIdentity = "subscribed-agent",
            senderIdentity = "operator",
            body = $"Subscription state {subscriptionStatus}."
        });
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);
        var postPayload = await postResponse.Content.ReadFromJsonAsync<DirectAgentEventPayload>();
        Assert.NotNull(postPayload);
        Assert.Equal(expectedDeliveryStatus, postPayload.DeliveryStatus);
        Assert.Equal(expectedClaimStatus, postPayload.ClaimStatus);
        Assert.Equal(expectedCompletionStatus, postPayload.CompletionStatus);
        Assert.Equal(1, postPayload.ActiveSubscriptionCount);
        Assert.Contains(subscriptionStatus, postPayload.SubscriptionStatuses ?? Array.Empty<string>());

        var readback = await _client.GetFromJsonAsync<DirectAgentEventReadbackPayload>(
            $"/api/direct-agent-events/{postPayload.EventId}");
        Assert.NotNull(readback);
        Assert.Equal(expectedDeliveryStatus, readback.DeliveryStatus);
        Assert.Equal(expectedClaimStatus, readback.ClaimStatus);
        Assert.Equal(expectedCompletionStatus, readback.CompletionStatus);
        Assert.Equal(1, readback.ActiveSubscriptionCount);
        Assert.Contains(subscriptionStatus, readback.SubscriptionStatuses);
    }

    // -------------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PostDirectAgentEvent_MissingChannelAndProject_Returns400()
    {
        using var response = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            memberIdentity = "test-agent",
            senderIdentity = "operator",
            body = "No channel specified"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostDirectAgentEvent_MissingMemberIdentity_Returns400()
    {
        var channel = await EnsureDefaultChannelAsync("dae-validation-1");
        using var response = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            channelId = channel.Id,
            senderIdentity = "operator",
            body = "No member identity"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostDirectAgentEvent_MissingBody_Returns400()
    {
        var channel = await EnsureDefaultChannelAsync("dae-validation-2");
        using var response = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            channelId = channel.Id,
            memberIdentity = "test-agent",
            senderIdentity = "operator"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostDirectAgentEvent_NoSubscription_RecordsNoSubscriberReadback()
    {
        var channel = await EnsureDefaultChannelAsync("dae-inactive-member");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "inactive-agent",
            wakePolicy = "direct_questions_only",
            membershipStatus = "muted"
        });

        using var response = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            channelId = channel.Id,
            memberIdentity = "inactive-agent",
            senderIdentity = "operator",
            body = "Target is muted."
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<DirectAgentEventPayload>();
        Assert.NotNull(payload);
        Assert.Equal("recorded_no_subscriber", payload.DeliveryStatus);
        Assert.Equal("no_subscriber", payload.ClaimStatus);
        Assert.Equal("pending", payload.CompletionStatus);
        Assert.Equal(0, payload.ActiveSubscriptionCount);
    }

    // -------------------------------------------------------------------------
    // Target-work attribution preserved (#1887 fields)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PostDirectAgentEvent_PreservesAllAttributionFields()
    {
        var channel = await EnsureDefaultChannelAsync("dae-attribution-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "attribution-worker",
            wakePolicy = "direct_questions_only"
        });

        using var response = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            channelId = channel.Id,
            memberIdentity = "attribution-worker",
            senderIdentity = "orch-runner",
            body = "Full attribution test.",
            sourceProjectId = "den-channels",
            targetProjectId = "goblinbench",
            targetTaskId = 1902,
            assignmentId = "143",
            workerRunId = "dc-1902-run",
            workerRole = "spawned-coder",
            profileIdentity = "den-hermes-coder",
            poolMemberId = "pool-143",
            agentInstanceId = "inst-143",
            sessionOwnerId = "runner-143",
            sessionId = "session-143"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<DirectAgentEventPayload>();
        Assert.NotNull(payload);

        // Source context preserved
        Assert.Equal("den-channels", payload.SourceProjectId);

        // Target-work attribution preserved
        Assert.Equal("goblinbench", payload.TargetProjectId);
        Assert.Equal(1902, payload.TargetTaskId);
        Assert.Equal(143, payload.AssignmentId);
        Assert.Equal("dc-1902-run", payload.WorkerRunId);
        Assert.Equal("spawned-coder", payload.WorkerRole);
        Assert.Equal("den-hermes-coder", payload.ProfileIdentity);
        Assert.Equal("pool-143", payload.PoolMemberId);

        // Session-owner fields preserved (#1887)
        Assert.Equal("inst-143", payload.AgentInstanceId);
        Assert.Equal("runner-143", payload.SessionOwnerId);
        Assert.Equal("session-143", payload.SessionId);

        // Also stored durably on the message (verify via gateway message readback)
        var message = await _client.GetFromJsonAsync<GatewayMessageDto>(
            $"/api/gateway/messages/{payload.EventId}");
        Assert.NotNull(message);
        Assert.Equal("den-channels", message.SourceProjectId);
        Assert.Equal("goblinbench", message.TargetProjectId);
        Assert.Equal(1902, message.TargetTaskId);
        Assert.Equal("143", message.AssignmentId);
        Assert.Equal("dc-1902-run", message.WorkerRunId);
        Assert.Equal("spawned-coder", message.WorkerRole);
        Assert.Equal("den-hermes-coder", message.ProfileIdentity);
        Assert.Equal("pool-143", message.PoolMemberId);
        Assert.Equal("inst-143", message.AgentInstanceId);
        Assert.Equal("runner-143", message.SessionOwnerId);
        Assert.Equal("session-143", message.SessionId);
    }

    // -------------------------------------------------------------------------
    // Gateway compatibility alias returns 410 Gone (retired task #2022)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GatewayDirectAgentMessages_Returns410Gone_Tombstone()
    {
        // Task #2022: Gateway compatibility alias retired. The canonical route is
        // POST /api/direct-agent-events.
        var channel = await EnsureDefaultChannelAsync("dae-gw-alias-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "alias-agent",
            wakePolicy = "direct_questions_only"
        });

        using var response = await _client.PostAsJsonAsync("/api/gateway/direct-agent-messages", new
        {
            channelId = channel.Id,
            memberIdentity = "alias-agent",
            senderIdentity = "operator",
            body = "Gateway alias test (should be gone)"
        });

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("route_gone", raw);
        Assert.Contains("direct-agent-events", raw);
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

    private async Task UpsertSubscriptionAsync(long channelId, object request)
    {
        using var response = await _client.PutAsJsonAsync($"/api/channels/{channelId}/subscriptions", request);
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

    private sealed record DirectAgentEventPayload(
        string Status,
        long EventId,
        long ChannelId,
        string RequestId,
        string MemberIdentity,
        string WakePolicy,
        string? SourceProjectId,
        string? TargetProjectId,
        int? TargetTaskId,
        int? AssignmentId,
        string? WorkerRunId,
        string? WorkerRole,
        string? ProfileIdentity,
        string? PoolMemberId,
        string? AgentInstanceId,
        string? SessionOwnerId,
        string? SessionId,
        string EventUrl,
        string EventsUrl,
        string EvidenceSummary,
        string? DeliveryStatus,
        string? ClaimStatus,
        string? CompletionStatus,
        int ActiveSubscriptionCount,
        IReadOnlyList<string>? SubscriptionStatuses,
        IReadOnlyList<string>? SubscriptionIdentities,
        string? CoordinationCallId,
        string? RequestKind,
        string? ResultDestinationJson);

    private sealed record DirectAgentEventReadbackPayload(
        long EventId,
        long ChannelId,
        string RequestId,
        string MessageKind,
        string SenderType,
        string SenderIdentity,
        string MemberIdentity,
        string WakePolicy,
        string? SourceKind,
        string? SourceProjectId,
        string? TargetProjectId,
        long? TargetTaskId,
        string? AssignmentId,
        string? WorkerRunId,
        string? WorkerRole,
        string? ProfileIdentity,
        string? PoolMemberId,
        string? AgentInstanceId,
        string? SessionOwnerId,
        string? SessionId,
        string? Summary,
        string Body,
        string? DeliveryStatus,
        string? ClaimStatus,
        string? CompletionStatus,
        int ActiveSubscriptionCount,
        IReadOnlyList<string> SubscriptionStatuses,
        IReadOnlyList<string> SubscriptionIdentities,
        string CreatedAt);

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
        string? AgentInstanceId,
        string? SessionOwnerId,
        string? SessionId,
        long? DeliveryRequestId,
        long? AttemptId,
        string? GatewayDeliveryState,
        string? GatewayAttemptStatus,
        bool TimedOut,
        bool GatewayUnavailable,
        string GatewayMessageUrl,
        string GatewayEventsUrl,
        string EvidenceSummary);
}
