using System.Net.Http.Json;
using DenChannels.Service.ActiveWorkRouting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenChannels.Service.Tests;

/// <summary>
/// Tests for active-work continuation routing (task #1873).
/// Covers: Runner crosses projects; worker activity uses Bridge/runtime control
/// metadata; Patch asks from target project channel; routing selects existing
/// active actor rather than a different same-profile session; no active route
/// returns an explicit result.
/// </summary>
public sealed class ActiveWorkRoutingTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"den-channels-awr-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ActiveWorkRoutingTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenChannels:Database:Path"] = _databasePath,
                    ["DenChannels:Database:ApplyMigrationsOnStartup"] = "true",
                    ["DenChannels:Gateway:Disabled"] = "true",
                    ["DenChannels:WorkerPool:Disabled"] = "true"
                });
            }));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { File.Delete(_databasePath); } catch { }
    }

    // =========================================================================
    // Helper methods
    // =========================================================================

    private async Task<ChannelPayload> EnsureChannelAsync(string projectId, string? slug = null)
    {
        var response = await _client.PutAsJsonAsync($"/api/projects/{projectId}/default-channel", new
        {
            displayName = projectId,
            createdBy = "test"
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChannelPayload>())!;
    }

    private async Task<ChannelPayload> CreateChannelAsync(string slug, string displayName, string? projectId = null)
    {
        var request = new
        {
            slug,
            displayName,
            kind = "project_default",
            projectId
        };
        var response = await _client.PostAsJsonAsync("/api/channels", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChannelPayload>())!;
    }

    private async Task PostMessageAsync(long channelId, object message)
    {
        var response = await _client.PostAsJsonAsync($"/api/channels/{channelId}/messages", message);
        response.EnsureSuccessStatusCode();
    }

    private async Task PostActivityEventAsync(long channelId, object request)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/channels/{channelId}/activity-events", request);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Helper: post a worker message with full target-work fields.</summary>
    private async Task PostWorkerMessageAsync(
        long channelId,
        string targetProjectId,
        long? targetTaskId = null,
        string? assignmentId = null,
        string? workerRunId = null,
        string? workerRole = null,
        string? profileIdentity = null,
        string? agentInstanceId = null,
        string? poolMemberId = null,
        string? sessionOwnerId = null,
        string? sessionId = null,
        string? sourceProjectId = null)
    {
        await PostMessageAsync(channelId, new
        {
            senderType = "agent",
            senderIdentity = profileIdentity ?? "spawned-coder",
            body = $"Work message for {targetProjectId}",
            messageKind = "agent_text",
            targetProjectId,
            targetTaskId,
            assignmentId,
            workerRunId,
            workerRole,
            profileIdentity,
            agentInstanceId,
            poolMemberId,
            sessionOwnerId,
            sessionId,
            sourceProjectId
        });
    }

    // =========================================================================
    // Test: No active route returns explicit result
    // =========================================================================

    [Fact]
    public async Task Resolve_NoMatchingWork_ReturnsExplicitNoActiveRoute()
    {
        // Setup: create a channel but no work messages
        await EnsureChannelAsync("proj-no-work");

        var response = await _client.PostAsJsonAsync("/api/active-work/resolve",
            new ResolveActiveWorkRouteRequest(
                TargetProjectId: "proj-no-work",
                TargetTaskId: 9999));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActiveWorkRouteResponsePayload>();

        Assert.NotNull(result);
        Assert.Equal("no_active_route", result.RouteStatus);
        Assert.Null(result.Route);
        Assert.NotNull(result.Reason);
        Assert.Contains("proj-no-work", result.Reason);
        Assert.NotNull(result.Evidence);
        Assert.Equal(3, result.Evidence.Sources.Count);
    }

    // =========================================================================
    // Test: Route resolves by target project/task to correct worker session
    // =========================================================================

    [Fact]
    public async Task Resolve_ByTargetProjectAndTask_ReturnsActiveWorkerRoute()
    {
        // Setup: worker-pool control channel (different project from target work)
        var controlChannel = await EnsureChannelAsync("den-hermes-bridge");

        // Target project channel
        var targetChannel = await EnsureChannelAsync("den-channels");

        // Worker posts activity in the target project channel with target-work fields
        await PostWorkerMessageAsync(
            channelId: targetChannel.Id,
            targetProjectId: "den-channels",
            targetTaskId: 1873,
            assignmentId: "150",
            workerRunId: "dc-1873-run-001",
            workerRole: "coder",
            profileIdentity: "spawned-coder",
            agentInstanceId: "inst-coder-01",
            poolMemberId: "pool-coder-02",
            sessionOwnerId: "spawned-coder:inst-coder-01",
            sessionId: "sess-001",
            sourceProjectId: "den-hermes-bridge");

        // Resolve: should find the active work by target project/task
        var response = await _client.PostAsJsonAsync("/api/active-work/resolve",
            new ResolveActiveWorkRouteRequest(
                TargetProjectId: "den-channels",
                TargetTaskId: 1873));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActiveWorkRouteResponsePayload>();

        Assert.NotNull(result);
        Assert.Equal("routed", result.RouteStatus);
        Assert.NotNull(result.Route);

        // Verify route points to the correct worker instance
        Assert.Equal("den-channels", result.Route.TargetProjectId);
        Assert.Equal(1873, result.Route.TargetTaskId);
        Assert.Equal("150", result.Route.AssignmentId);
        Assert.Equal("dc-1873-run-001", result.Route.WorkerRunId);
        Assert.Equal("coder", result.Route.WorkerRole);
        Assert.Equal("inst-coder-01", result.Route.AgentInstanceId);
        Assert.Equal("spawned-coder", result.Route.ProfileIdentity);
        Assert.Equal("pool-coder-02", result.Route.PoolMemberId);
        Assert.Equal("spawned-coder:inst-coder-01", result.Route.SessionOwnerId);
        Assert.Equal("sess-001", result.Route.SessionId);

        // Source channel should be the target project channel (where work is visible)
        Assert.Equal(targetChannel.Id, result.Route.SourceChannelId);

        // Handles should be populated
        Assert.NotNull(result.Route.Handles);
        Assert.Contains("/api/assignments/150/transcript", result.Route.Handles.TranscriptUrl);
        Assert.NotNull(result.Route.Handles.AgentDetailUrl);
    }

    // =========================================================================
    // Test: Runner crosses projects - work is for different project than control channel
    // =========================================================================

    [Fact]
    public async Task Resolve_CrossProjectWork_RoutesToTargetNotControlProject()
    {
        // Control channel in den-hermes-bridge
        var controlChannel = await EnsureChannelAsync("den-hermes-bridge");

        // Worker posts message in control channel with target_project_id = different project
        await PostWorkerMessageAsync(
            channelId: controlChannel.Id,
            targetProjectId: "den-gateway",
            targetTaskId: 555,
            assignmentId: "99",
            workerRunId: "dg-555-run-001",
            workerRole: "coder",
            profileIdentity: "pool-coder-01",
            agentInstanceId: "inst-pool-coder-01",
            sessionOwnerId: "pool-coder-01:inst-pool-coder-01",
            sessionId: "sess-cross-001",
            sourceProjectId: "den-hermes-bridge");

        // Resolve by target project (not the source/control project)
        var response = await _client.PostAsJsonAsync("/api/active-work/resolve",
            new ResolveActiveWorkRouteRequest(
                TargetProjectId: "den-gateway",
                TargetTaskId: 555));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActiveWorkRouteResponsePayload>();

        Assert.NotNull(result);
        Assert.Equal("routed", result.RouteStatus);
        Assert.NotNull(result.Route);
        Assert.Equal("den-gateway", result.Route.TargetProjectId);
        Assert.Equal(555, result.Route.TargetTaskId);
        Assert.Equal("99", result.Route.AssignmentId);

        // Source control project should reflect the control channel project, not the target
        Assert.Equal("den-hermes-bridge", result.Route.SourceControlProjectId);
    }

    // =========================================================================
    // Test: Same profile, different instances - routes to correct instance
    // =========================================================================

    [Fact]
    public async Task Resolve_SameProfileDifferentInstances_RoutesToCorrectInstance()
    {
        var channel = await EnsureChannelAsync("proj-instance-disambig");

        // Instance A working on task 100
        await PostWorkerMessageAsync(
            channelId: channel.Id,
            targetProjectId: "proj-instance-disambig",
            targetTaskId: 100,
            assignmentId: "assign-A",
            workerRunId: "run-A-001",
            workerRole: "coder",
            profileIdentity: "spawned-coder",
            agentInstanceId: "inst-A",
            poolMemberId: "pool-A",
            sessionOwnerId: "spawned-coder:inst-A",
            sessionId: "sess-A");

        // Instance B working on task 200
        await PostWorkerMessageAsync(
            channelId: channel.Id,
            targetProjectId: "proj-instance-disambig",
            targetTaskId: 200,
            assignmentId: "assign-B",
            workerRunId: "run-B-001",
            workerRole: "coder",
            profileIdentity: "spawned-coder",
            agentInstanceId: "inst-B",
            poolMemberId: "pool-B",
            sessionOwnerId: "spawned-coder:inst-B",
            sessionId: "sess-B");

        // Resolve for task 100 -> should get instance A
        var responseA = await _client.PostAsJsonAsync("/api/active-work/resolve",
            new ResolveActiveWorkRouteRequest(
                TargetProjectId: "proj-instance-disambig",
                TargetTaskId: 100));

        responseA.EnsureSuccessStatusCode();
        var resultA = await responseA.Content.ReadFromJsonAsync<ActiveWorkRouteResponsePayload>();

        Assert.NotNull(resultA);
        Assert.Equal("routed", resultA.RouteStatus);
        Assert.Equal("inst-A", resultA.Route!.AgentInstanceId);
        Assert.Equal("assign-A", resultA.Route.AssignmentId);
        Assert.Equal("spawned-coder:inst-A", resultA.Route.SessionOwnerId);

        // Resolve for task 200 -> should get instance B
        var responseB = await _client.PostAsJsonAsync("/api/active-work/resolve",
            new ResolveActiveWorkRouteRequest(
                TargetProjectId: "proj-instance-disambig",
                TargetTaskId: 200));

        responseB.EnsureSuccessStatusCode();
        var resultB = await responseB.Content.ReadFromJsonAsync<ActiveWorkRouteResponsePayload>();

        Assert.NotNull(resultB);
        Assert.Equal("routed", resultB.RouteStatus);
        Assert.Equal("inst-B", resultB.Route!.AgentInstanceId);
        Assert.Equal("assign-B", resultB.Route.AssignmentId);
    }

    // =========================================================================
    // Test: Resolve by assignment ID directly
    // =========================================================================

    [Fact]
    public async Task Resolve_ByAssignmentId_ReturnsMatchingRoute()
    {
        var channel = await EnsureChannelAsync("proj-assign-resolve");

        await PostWorkerMessageAsync(
            channelId: channel.Id,
            targetProjectId: "proj-assign-resolve",
            targetTaskId: 42,
            assignmentId: "assign-42-special",
            workerRunId: "run-42",
            workerRole: "reviewer",
            profileIdentity: "pool-reviewer-01",
            agentInstanceId: "inst-reviewer-01",
            sessionOwnerId: "pool-reviewer-01:inst-reviewer-01",
            sessionId: "sess-review-42");

        // Resolve directly by assignment ID
        var response = await _client.PostAsJsonAsync("/api/active-work/resolve",
            new ResolveActiveWorkRouteRequest(AssignmentId: "assign-42-special"));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActiveWorkRouteResponsePayload>();

        Assert.NotNull(result);
        Assert.Equal("routed", result.RouteStatus);
        Assert.Equal("assign-42-special", result.Route!.AssignmentId);
        Assert.Equal("reviewer", result.Route.WorkerRole);
    }

    // =========================================================================
    // Test: Activity events supplement route with instance identity
    // =========================================================================

    [Fact]
    public async Task Resolve_ActivityEventsSupplementRoute_WithInstanceFields()
    {
        var channel = await EnsureChannelAsync("proj-activity-supplement");

        // Post activity event with instance identity but no message
        await PostActivityEventAsync(channel.Id, new
        {
            projectId = "proj-activity-supplement",
            agentIdentity = "spawned-coder",
            workerRunId = "awr-run-001",
            workerRole = "coder",
            agentInstanceId = "inst-awr-01",
            poolMemberId = "pool-awr-01",
            assignmentId = "assign-awr-001",
            taskId = 300,
            eventType = "tool_call_started",
            status = "started",
            deliveryStage = "tool",
            terminal = false,
            sequence = 1
        });

        // Resolve should find the route from activity events
        var response = await _client.PostAsJsonAsync("/api/active-work/resolve",
            new ResolveActiveWorkRouteRequest(
                TargetProjectId: "proj-activity-supplement",
                TargetTaskId: 300));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActiveWorkRouteResponsePayload>();

        Assert.NotNull(result);
        Assert.Equal("routed", result.RouteStatus);
        Assert.NotNull(result.Route);
        Assert.Equal("inst-awr-01", result.Route.AgentInstanceId);
        Assert.Equal("assign-awr-001", result.Route.AssignmentId);
        Assert.Equal("spawned-coder", result.Route.ProfileIdentity);
    }

    // =========================================================================
    // Test: Messages and activity events merge correctly
    // =========================================================================

    [Fact]
    public async Task Resolve_MessagesAndActivityMerge_CombinesSessionAndInstance()
    {
        var channel = await EnsureChannelAsync("proj-merge-test");

        // Message provides session identity but no agent instance
        await PostWorkerMessageAsync(
            channelId: channel.Id,
            targetProjectId: "proj-merge-test",
            targetTaskId: 400,
            assignmentId: "assign-merge-400",
            workerRunId: "run-merge-400",
            profileIdentity: "spawned-coder",
            sessionOwnerId: "spawned-coder:inst-merge",
            sessionId: "sess-merge");

        // Activity event provides agent instance but no session
        await PostActivityEventAsync(channel.Id, new
        {
            projectId = "proj-merge-test",
            agentIdentity = "spawned-coder",
            workerRunId = "run-merge-400",
            workerRole = "coder",
            agentInstanceId = "inst-merge",
            poolMemberId = "pool-merge",
            assignmentId = "assign-merge-400",
            taskId = 400,
            eventType = "tool_call_completed",
            status = "completed",
            deliveryStage = "tool",
            terminal = true,
            sequence = 1
        });

        var response = await _client.PostAsJsonAsync("/api/active-work/resolve",
            new ResolveActiveWorkRouteRequest(
                TargetProjectId: "proj-merge-test",
                TargetTaskId: 400));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActiveWorkRouteResponsePayload>();

        Assert.NotNull(result);
        Assert.Equal("routed", result.RouteStatus);
        Assert.NotNull(result.Route);
        // Merged: session from message, instance from activity
        Assert.Equal("spawned-coder:inst-merge", result.Route.SessionOwnerId);
        Assert.Equal("sess-merge", result.Route.SessionId);
        Assert.Equal("inst-merge", result.Route.AgentInstanceId);
    }

    // =========================================================================
    // Test: Patch asks from target project channel resolve to active worker
    // =========================================================================

    [Fact]
    public async Task Resolve_PatchAsksFromTargetProject_ResolvesToActiveWorker()
    {
        // Scenario: Patch asks a question in the den-channels project channel.
        // A spawned-coder worker is actively working on den-channels task 1873.
        // The question should resolve to the active worker session, not a random
        // same-profile lane.

        var targetChannel = await EnsureChannelAsync("den-channels");

        // Active worker session
        await PostWorkerMessageAsync(
            channelId: targetChannel.Id,
            targetProjectId: "den-channels",
            targetTaskId: 1873,
            assignmentId: "150",
            workerRunId: "dc-1873-run-active",
            workerRole: "coder",
            profileIdentity: "spawned-coder",
            agentInstanceId: "inst-active-coder",
            sessionOwnerId: "spawned-coder:inst-active-coder",
            sessionId: "sess-active-1873");

        // Another same-profile session doing unrelated work on a different task
        await PostWorkerMessageAsync(
            channelId: targetChannel.Id,
            targetProjectId: "den-channels",
            targetTaskId: 999,
            assignmentId: "999-assign",
            workerRunId: "dc-999-run-other",
            workerRole: "coder",
            profileIdentity: "spawned-coder",
            agentInstanceId: "inst-other-coder",
            sessionOwnerId: "spawned-coder:inst-other-coder",
            sessionId: "sess-other-999");

        // Patch asks about task 1873
        var response = await _client.PostAsJsonAsync("/api/active-work/resolve",
            new ResolveActiveWorkRouteRequest(
                TargetProjectId: "den-channels",
                TargetTaskId: 1873));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActiveWorkRouteResponsePayload>();

        Assert.NotNull(result);
        Assert.Equal("routed", result.RouteStatus);
        Assert.NotNull(result.Route);

        // Should route to the ACTIVE worker for task 1873, not the other session
        Assert.Equal("inst-active-coder", result.Route.AgentInstanceId);
        Assert.Equal("150", result.Route.AssignmentId);
        Assert.Equal("dc-1873-run-active", result.Route.WorkerRunId);
        Assert.Equal("spawned-coder:inst-active-coder", result.Route.SessionOwnerId);
    }

    // =========================================================================
    // Test: List routes endpoint
    // =========================================================================

    [Fact]
    public async Task ListRoutes_ByProject_ReturnsAllActiveRoutes()
    {
        var channel = await EnsureChannelAsync("proj-list-test");

        // Two active tasks
        await PostWorkerMessageAsync(
            channelId: channel.Id,
            targetProjectId: "proj-list-test",
            targetTaskId: 10,
            assignmentId: "assign-list-10",
            workerRunId: "run-list-10",
            profileIdentity: "coder-A",
            agentInstanceId: "inst-list-A",
            sessionOwnerId: "coder-A:inst-list-A",
            sessionId: "sess-list-10");

        await PostWorkerMessageAsync(
            channelId: channel.Id,
            targetProjectId: "proj-list-test",
            targetTaskId: 20,
            assignmentId: "assign-list-20",
            workerRunId: "run-list-20",
            profileIdentity: "coder-B",
            agentInstanceId: "inst-list-B",
            sessionOwnerId: "coder-B:inst-list-B",
            sessionId: "sess-list-20");

        var response = await _client.GetAsync("/api/active-work/routes?targetProjectId=proj-list-test");

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ListActiveWorkRoutesResponsePayload>();

        Assert.NotNull(result);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.Routes.Count);

        var taskIds = result.Routes.Select(r => r.TargetTaskId).ToList();
        Assert.Contains(10L, taskIds);
        Assert.Contains(20L, taskIds);
    }

    // =========================================================================
    // Test: List routes filters stale by default
    // =========================================================================

    [Fact]
    public async Task ListRoutes_DefaultExcludesStale_WhenIncludeStaleFalse()
    {
        var channel = await EnsureChannelAsync("proj-stale-filter");

        // Post a message (will have a recent timestamp, so not stale)
        await PostWorkerMessageAsync(
            channelId: channel.Id,
            targetProjectId: "proj-stale-filter",
            targetTaskId: 555,
            assignmentId: "assign-fresh",
            workerRunId: "run-fresh",
            profileIdentity: "coder-fresh",
            agentInstanceId: "inst-fresh",
            sessionOwnerId: "coder-fresh:inst-fresh",
            sessionId: "sess-fresh");

        var response = await _client.GetAsync("/api/active-work/routes?targetProjectId=proj-stale-filter");

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ListActiveWorkRoutesResponsePayload>();

        Assert.NotNull(result);
        // Fresh route should be included (timestamp is recent)
        Assert.True(result.TotalCount >= 1);
        Assert.All(result.Routes, r => Assert.False(r.IsStale));
    }

    // =========================================================================
    // Test: Resolve with no filters returns no_active_route
    // =========================================================================

    [Fact]
    public async Task Resolve_NoFiltersNoData_ReturnsNoActiveRoute()
    {
        var response = await _client.PostAsJsonAsync("/api/active-work/resolve",
            new ResolveActiveWorkRouteRequest());

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActiveWorkRouteResponsePayload>();

        Assert.NotNull(result);
        Assert.Equal("no_active_route", result.RouteStatus);
        Assert.Null(result.Route);
    }

    // =========================================================================
    // Test: Evidence fields are populated correctly
    // =========================================================================

    [Fact]
    public async Task Resolve_EvidenceFields_ArePopulated()
    {
        var channel = await EnsureChannelAsync("proj-evidence");

        await PostWorkerMessageAsync(
            channelId: channel.Id,
            targetProjectId: "proj-evidence",
            targetTaskId: 777,
            assignmentId: "assign-evidence-777",
            workerRunId: "run-evidence",
            profileIdentity: "coder-evidence",
            agentInstanceId: "inst-evidence");

        var response = await _client.PostAsJsonAsync("/api/active-work/resolve",
            new ResolveActiveWorkRouteRequest(
                TargetProjectId: "proj-evidence",
                TargetTaskId: 777));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActiveWorkRouteResponsePayload>();

        Assert.NotNull(result);
        Assert.Equal("routed", result.RouteStatus);
        Assert.NotNull(result.Evidence);
        Assert.Equal(3, result.Evidence.Sources.Count);

        // Check source names
        var sourceNames = result.Evidence.Sources.Select(s => s.Source).ToList();
        Assert.Contains("channel_messages", sourceNames);
        Assert.Contains("activity_events", sourceNames);
        Assert.Contains("worker_pool", sourceNames);

        // Channel messages should have found records
        var msgSource = result.Evidence.Sources.First(s => s.Source == "channel_messages");
        Assert.True(msgSource.Available);
        Assert.True(msgSource.RecordsExamined > 0);

        // ResolvedAt should be a valid timestamp
        Assert.NotNull(result.Evidence.ResolvedAt);
        Assert.True(DateTime.TryParse(result.Evidence.ResolvedAt, out _));
    }

    // =========================================================================
    // Test: Route handles are correct
    // =========================================================================

    [Fact]
    public async Task Resolve_Handles_IncludesTranscriptAndAgentUrls()
    {
        var channel = await EnsureChannelAsync("proj-handles");

        await PostWorkerMessageAsync(
            channelId: channel.Id,
            targetProjectId: "proj-handles",
            targetTaskId: 888,
            assignmentId: "assign-handles",
            workerRunId: "run-handles",
            profileIdentity: "coder-handles",
            agentInstanceId: "inst-handles",
            sessionOwnerId: "coder-handles:inst-handles",
            sessionId: "sess-handles");

        // Also post with delivery request ID
        await PostMessageAsync(channel.Id, new
        {
            senderType = "agent",
            senderIdentity = "coder-handles",
            body = "Delivery message",
            messageKind = "agent_text",
            targetProjectId = "proj-handles",
            targetTaskId = 888,
            assignmentId = "assign-handles",
            workerRunId = "run-handles",
            profileIdentity = "coder-handles",
            agentInstanceId = "inst-handles",
            deliveryRequestId = "delivery-handles-001"
        });

        var response = await _client.PostAsJsonAsync("/api/active-work/resolve",
            new ResolveActiveWorkRouteRequest(
                TargetProjectId: "proj-handles",
                TargetTaskId: 888));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActiveWorkRouteResponsePayload>();

        Assert.NotNull(result);
        Assert.Equal("routed", result.RouteStatus);
        Assert.NotNull(result.Route!.Handles);
        Assert.Equal("/api/assignments/assign-handles/transcript", result.Route.Handles.TranscriptUrl);
        Assert.Contains("/api/gateway/assignments/assign-handles/trace", result.Route.Handles.TraceUrl);
        Assert.Equal("delivery-handles-001", result.Route.Handles.DeliveryHandle);
        Assert.Contains("/api/agents/coder-handles/overview", result.Route.Handles.AgentDetailUrl);
    }

    // =========================================================================
    // Test: Allowed actions populated correctly
    // =========================================================================

    [Fact]
    public async Task Resolve_WithSessionOwner_IncludesAllActions()
    {
        var channel = await EnsureChannelAsync("proj-actions");

        await PostWorkerMessageAsync(
            channelId: channel.Id,
            targetProjectId: "proj-actions",
            targetTaskId: 600,
            assignmentId: "assign-actions",
            workerRunId: "run-actions",
            profileIdentity: "coder-actions",
            agentInstanceId: "inst-actions",
            sessionOwnerId: "coder-actions:inst-actions",
            sessionId: "sess-actions");

        var response = await _client.PostAsJsonAsync("/api/active-work/resolve",
            new ResolveActiveWorkRouteRequest(
                TargetProjectId: "proj-actions",
                TargetTaskId: 600));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActiveWorkRouteResponsePayload>();

        Assert.NotNull(result);
        Assert.Equal("routed", result.RouteStatus);
        Assert.NotNull(result.Route);

        Assert.Contains("ask", result.Route.AllowedActions);
        Assert.Contains("continue", result.Route.AllowedActions);
        Assert.Contains("reset", result.Route.AllowedActions);
        Assert.Contains("view_transcript", result.Route.AllowedActions);
    }

    [Fact]
    public async Task Resolve_WithoutSessionOwner_LimitedActions()
    {
        var channel = await EnsureChannelAsync("proj-limited-actions");

        // Post a message with no session owner and no agent instance
        await PostMessageAsync(channel.Id, new
        {
            senderType = "agent",
            senderIdentity = "coder-basic",
            body = "Basic message",
            messageKind = "agent_text",
            targetProjectId = "proj-limited-actions",
            targetTaskId = 700,
            assignmentId = "assign-basic",
            workerRunId = "run-basic",
            profileIdentity = "coder-basic"
            // No sessionOwnerId, no agentInstanceId
        });

        var response = await _client.PostAsJsonAsync("/api/active-work/resolve",
            new ResolveActiveWorkRouteRequest(
                TargetProjectId: "proj-limited-actions",
                TargetTaskId: 700));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActiveWorkRouteResponsePayload>();

        Assert.NotNull(result);
        Assert.Equal("routed", result.RouteStatus);
        Assert.NotNull(result.Route);

        // Without session owner/instance, only limited actions should be allowed
        Assert.Contains("ask", result.Route.AllowedActions);
        Assert.Contains("view_transcript", result.Route.AllowedActions);
        Assert.DoesNotContain("continue", result.Route.AllowedActions);
        Assert.DoesNotContain("reset", result.Route.AllowedActions);
    }

    // =========================================================================
    // Test: Worker message with direct-agent-event-like fields creates route-visible work
    // =========================================================================

    [Fact]
    public async Task Resolve_WorkerMessageInControlChannel_CreatesRouteVisibleWork()
    {
        // Scenario: A worker control channel in worker-control-bridge, but the message
        // carries target-project fields pointing to the actual work project.
        var controlChannel = await EnsureChannelAsync("awr-worker-bridge");

        // Post a message via the channel messages API with target-work routing fields
        await PostMessageAsync(controlChannel.Id, new
        {
            senderType = "user",
            senderIdentity = "patch",
            body = "Continue working on this task",
            messageKind = "human_text",
            sourceProjectId = "awr-worker-bridge",
            targetProjectId = "awr-target-proj",
            targetTaskId = 1900,
            assignmentId = "assign-dae-1900",
            workerRunId = "dae-run-1900",
            workerRole = "coder",
            profileIdentity = "spawned-coder",
            agentInstanceId = "inst-dae-01",
            poolMemberId = "pool-dae-01",
            sessionOwnerId = "spawned-coder:inst-dae-01",
            sessionId = "sess-dae-1900"
        });

        var response = await _client.PostAsJsonAsync("/api/active-work/resolve",
            new ResolveActiveWorkRouteRequest(
                TargetProjectId: "awr-target-proj",
                TargetTaskId: 1900));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ActiveWorkRouteResponsePayload>();

        Assert.NotNull(result);
        Assert.Equal("routed", result.RouteStatus);
        Assert.NotNull(result.Route);
        Assert.Equal("awr-target-proj", result.Route.TargetProjectId);
        Assert.Equal(1900, result.Route.TargetTaskId);
        Assert.Equal("assign-dae-1900", result.Route.AssignmentId);
        Assert.Equal("inst-dae-01", result.Route.AgentInstanceId);
        Assert.Equal("awr-worker-bridge", result.Route.SourceControlProjectId);
    }

    // =========================================================================
    // JSON payload types for deserialization
    // =========================================================================

    private sealed record ActiveWorkRouteResponsePayload(
        string RouteStatus,
        string Reason,
        ActiveWorkRoutePayload? Route = null,
        ActiveWorkRouteEvidencePayload? Evidence = null);

    private sealed record ActiveWorkRoutePayload(
        string? TargetProjectId,
        long? TargetTaskId,
        string? AssignmentId,
        string? WorkerRunId,
        string? WorkerRole,
        string? AgentInstanceId,
        string? ProfileIdentity,
        string? PoolMemberId,
        string? SessionOwnerId,
        string? SessionId,
        long? SourceChannelId,
        string? SourceControlProjectId,
        string? LastActivityAt,
        string? AssignmentPhase,
        bool IsStale,
        List<string> AllowedActions,
        ActiveWorkRouteHandlesPayload? Handles);

    private sealed record ActiveWorkRouteHandlesPayload(
        string? TranscriptUrl,
        string? TraceUrl,
        string? DeliveryHandle,
        string? AgentDetailUrl);

    private sealed record ActiveWorkRouteEvidencePayload(
        List<ActiveWorkRouteSourceEvidencePayload> Sources,
        int CandidatesConsidered,
        string ResolvedAt);

    private sealed record ActiveWorkRouteSourceEvidencePayload(
        string Source,
        bool Available,
        int RecordsExamined,
        string? Detail);

    private sealed record ListActiveWorkRoutesResponsePayload(
        List<ActiveWorkRoutePayload> Routes,
        int TotalCount,
        ActiveWorkRouteEvidencePayload Evidence);

    private sealed record ChannelPayload(long Id, string Slug, string Kind, string? ProjectId);
}
