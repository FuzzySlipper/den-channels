using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenChannels.Service.Tests;

/// <summary>
/// Contract tests for the agent-work lifecycle observability API.
/// Verifies non-waking invariants, required correlation fields,
/// and the write/query/projection contract shape.
/// </summary>
public sealed class AgentWorkLifecycleContractTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"den-channels-lifecycle-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public AgentWorkLifecycleContractTests()
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

    [Fact]
    public async Task WriteLifecycleEvent_WithValidData_ReturnsCreated()
    {
        using var client = _factory.CreateClient();

        // Create a channel first
        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "agent-work",
            displayName = "Agent Work",
            kind = "ad_hoc",
            createdBy = "test"
        });
        Assert.Equal(HttpStatusCode.Created, createChannelResp.StatusCode);
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        using var response = await client.PostAsJsonAsync("/api/agent-work/lifecycle-events", new
        {
            channelId = channel!.Id,
            agentIdentity = "spawned-coder",
            eventType = "agent_turn_started",
            projectId = "den-core",
            taskId = 1965,
            workerRunId = "piw_test_run",
            workerRole = "coder",
            title = "Test lifecycle event",
            summary = "Non-waking observability test"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("agent_turn_started", doc.RootElement.GetProperty("eventType").GetString());
    }

    [Fact]
    public async Task WriteLifecycleEvent_WithInvalidEventType_ReturnsBadRequest()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent-work/lifecycle-events", new
        {
            channelId = 1,
            agentIdentity = "spawned-coder",
            eventType = "not_a_real_event_type",
            projectId = "den-core"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WriteLifecycleEvent_MissingAgentIdentity_ReturnsBadRequest()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/agent-work/lifecycle-events", new
        {
            channelId = 1,
            eventType = "request_recorded"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LifecycleEvent_IsNonWaking_DoesNotCreateChannelMessage()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "nowake-test",
            displayName = "NoWake Test",
            kind = "ad_hoc",
            createdBy = "test"
        });
        Assert.Equal(HttpStatusCode.Created, createChannelResp.StatusCode);
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        // Write a lifecycle event
        using var lifecycleResp = await client.PostAsJsonAsync("/api/agent-work/lifecycle-events", new
        {
            channelId = channel!.Id,
            agentIdentity = "test-runner",
            eventType = "request_claimed",
            title = "Non-waking event"
        });
        Assert.Equal(HttpStatusCode.Created, lifecycleResp.StatusCode);

        // Verify no channel_message was created
        var messagesResp = await client.GetAsync($"/api/channels/{channel.Id}/messages?limit=10");
        Assert.Equal(HttpStatusCode.OK, messagesResp.StatusCode);
        var messages = await messagesResp.Content.ReadFromJsonAsync<List<object>>(_jsonOptions);
        Assert.NotNull(messages);
        Assert.Empty(messages);

        // Verify the lifecycle event IS in activity events
        var activityResp = await client.GetAsync($"/api/channels/{channel.Id}/activity-events?limit=10");
        Assert.Equal(HttpStatusCode.OK, activityResp.StatusCode);
        var activityEvents = await activityResp.Content.ReadFromJsonAsync<List<object>>(_jsonOptions);
        Assert.NotNull(activityEvents);
        Assert.NotEmpty(activityEvents);
    }

    [Fact]
    public async Task LifecycleEvent_IsNonWaking_DoesNotAdvanceReadCursors()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "cursor-test",
            displayName = "Cursor Test",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        // Write a lifecycle event
        using var lifecycleResp = await client.PostAsJsonAsync("/api/agent-work/lifecycle-events", new
        {
            channelId = channel!.Id,
            agentIdentity = "test-runner",
            eventType = "heartbeat"
        });
        Assert.Equal(HttpStatusCode.Created, lifecycleResp.StatusCode);

        // Verify the activity event is NOT returned as a channel message
        var messagesResp = await client.GetAsync($"/api/channels/{channel.Id}/messages?limit=10");
        var messages = await messagesResp.Content.ReadFromJsonAsync<List<object>>(_jsonOptions);
        Assert.NotNull(messages);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task QueryLifecycleEvents_FiltersByLifecycleEventType()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "event-type-filter",
            displayName = "Event Type Filter",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        await client.PostAsJsonAsync("/api/agent-work/lifecycle-events", new
        {
            channelId = channel!.Id,
            agentIdentity = "test-runner",
            eventType = "heartbeat",
            workerRunId = "run-heartbeat"
        });
        await client.PostAsJsonAsync("/api/agent-work/lifecycle-events", new
        {
            channelId = channel.Id,
            agentIdentity = "test-runner",
            eventType = "completed",
            workerRunId = "run-completed"
        });

        using var response = await client.GetAsync($"/api/agent-work/events?channelId={channel.Id}&eventType=heartbeat");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("heartbeat", items[0].GetProperty("eventType").GetString());
        Assert.Equal("run-heartbeat", items[0].GetProperty("workerRunId").GetString());
    }

    [Fact]
    public async Task CurrentWorkProjection_WhenEmpty_ReturnsEmptyItems()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "empty-proj",
            displayName = "Empty Projection",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        using var response = await client.GetAsync($"/api/agent-work/current?channelId={channel!.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("items", out var items));
        Assert.Equal(0, items.GetArrayLength());
        Assert.True(doc.RootElement.TryGetProperty("stalenessSummary", out _));
    }

    [Fact]
    public async Task CurrentWorkProjection_WithLifecycleEvents_GroupsByAgent()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "work-proj",
            displayName = "Work Projection",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        // Write events for two agents
        await client.PostAsJsonAsync("/api/agent-work/lifecycle-events", new
        {
            channelId = channel!.Id,
            agentIdentity = "runner-1",
            eventType = "agent_turn_started",
            workerRunId = "run-a",
            title = "Runner 1 turn"
        });
        await client.PostAsJsonAsync("/api/agent-work/lifecycle-events", new
        {
            channelId = channel.Id,
            agentIdentity = "spawned-coder",
            eventType = "worker_process_started",
            workerRunId = "run-b",
            profileIdentity = "spawned-coder",
            hostId = "den-k8",
            processId = 4242,
            title = "Coder spawned"
        });

        using var response = await client.GetAsync($"/api/agent-work/current?channelId={channel.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("items", out var items));
        Assert.True(items.GetArrayLength() >= 2);
        var spawnedCoder = items.EnumerateArray()
            .Single(item => item.GetProperty("agentIdentity").GetString() == "spawned-coder");
        Assert.Equal("spawned-coder", spawnedCoder.GetProperty("profileIdentity").GetString());
        Assert.Equal("den-k8", spawnedCoder.GetProperty("hostId").GetString());
        Assert.Equal(4242, spawnedCoder.GetProperty("processId").GetInt32());
    }


    [Fact]
    public async Task CurrentWorkProjection_UsesProducerStalenessDeadline()
    {
        using var client = _factory.CreateClient();
        var staleDeadline = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "deadline-proj",
            displayName = "Deadline Projection",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        using var lifecycleResp = await client.PostAsJsonAsync("/api/agent-work/lifecycle-events", new
        {
            channelId = channel!.Id,
            agentIdentity = "deadline-runner",
            eventType = "heartbeat",
            workerRunId = "run-deadline",
            stalenessDeadline = staleDeadline,
            summary = "Heartbeat with producer deadline"
        });
        Assert.Equal(HttpStatusCode.Created, lifecycleResp.StatusCode);

        using var response = await client.GetAsync($"/api/agent-work/current?channelId={channel.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var item = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("agentIdentity").GetString() == "deadline-runner");

        Assert.Equal(staleDeadline, item.GetProperty("stalenessDeadline").GetString());
        Assert.Contains("deadline passed", item.GetProperty("stalenessDiagnostic").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("stalenessSummary").GetProperty("stale").GetInt32());
    }

    [Fact]
    public async Task LifecycleEvent_IncludesRequiredCorrelationFields()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "correlation-test",
            displayName = "Correlation Test",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        using var lifecycleResp = await client.PostAsJsonAsync("/api/agent-work/lifecycle-events", new
        {
            channelId = channel!.Id,
            agentIdentity = "test-agent",
            eventType = "assignment_created",
            workerRunId = "piw_corr_test",
            assignmentId = "42",
            workerRole = "coder",
            projectId = "test-project",
            taskId = 100,
            sessionId = "ses-abc",
            hostId = "den-srv",
            processId = 12345
        });
        Assert.Equal(HttpStatusCode.Created, lifecycleResp.StatusCode);

        var body = await lifecycleResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("agentIdentity", out var agentId));
        Assert.Equal("test-agent", agentId.GetString());
        Assert.True(doc.RootElement.TryGetProperty("workerRunId", out var runId));
        Assert.Equal("piw_corr_test", runId.GetString());
        Assert.True(doc.RootElement.TryGetProperty("assignmentId", out var assignId));
        Assert.Equal("42", assignId.GetString());
        Assert.True(doc.RootElement.TryGetProperty("workerRole", out var role));
        Assert.Equal("coder", role.GetString());
        Assert.True(doc.RootElement.TryGetProperty("projectId", out var projId));
        Assert.Equal("test-project", projId.GetString());
        Assert.True(doc.RootElement.TryGetProperty("taskId", out var task));
        Assert.Equal(100, task.GetInt64());
        Assert.True(doc.RootElement.TryGetProperty("sessionId", out var session));
        Assert.Equal("ses-abc", session.GetString());
        Assert.True(doc.RootElement.TryGetProperty("hostId", out var host));
        Assert.Equal("den-srv", host.GetString());
        Assert.True(doc.RootElement.TryGetProperty("processId", out var pid));
        Assert.Equal(12345, pid.GetInt32());
    }

    [Fact]
    public async Task AllLifecycleEventTypes_AreValidatable()
    {
        var validTypes = new[]
        {
            "request_recorded", "delivery_attempted", "runtime_received",
            "request_claimed", "agent_turn_started", "task_selected",
            "assignment_created", "worker_spawn_requested", "worker_process_started",
            "heartbeat", "checkpoint_seen",
            "blocked", "completed", "failed", "timed_out",
            "cleanup_started", "cleanup_completed", "capacity_released"
        };

        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "all-events",
            displayName = "All Events",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        foreach (var eventType in validTypes)
        {
            using var response = await client.PostAsJsonAsync("/api/agent-work/lifecycle-events", new
            {
                channelId = channel!.Id,
                agentIdentity = "test-agent",
                eventType
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // #1977: Multi-source current-work projection tests
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CurrentWorkProjection_WithActivityOnly_NoLifecycleEvents_ReturnsItems()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "activity-only",
            displayName = "Activity Only",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();
        Assert.NotNull(channel);

        // Write general activity events (NOT lifecycle events)
        for (var i = 0; i < 3; i++)
        {
            await client.PostAsJsonAsync($"/api/channels/{channel.Id}/activity-events", new
            {
                agentIdentity = "tool-runner",
                eventType = "tool_call_started",
                workerRunId = $"run-{i}",
                workerRole = "coder",
                title = $"Tool call {i}",
                summary = $"Running tool {i}"
            });
        }

        using var response = await client.GetAsync($"/api/agent-work/current?channelId={channel.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("items", out var items));
        Assert.True(items.GetArrayLength() > 0, "Should have projection items from activity events");

        var first = items[0];
        Assert.Equal("tool-runner", first.GetProperty("agentIdentity").GetString());
        Assert.True(first.TryGetProperty("evidenceProvenance", out var provenance));
        var provList = provenance.EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("activity_event", provList);
        Assert.DoesNotContain("lifecycle_event", provList);

        // Should have evidence links
        Assert.True(first.TryGetProperty("evidenceLinks", out var links));
        Assert.True(links.GetArrayLength() > 0);

        // Should indicate activity_no_lifecycle state
        Assert.True(first.TryGetProperty("currentWorkState", out var cws));
        Assert.Equal("activity_no_lifecycle", cws.GetString());

        // Migration note should be present
        Assert.True(doc.RootElement.TryGetProperty("migrationNote", out var note));
        Assert.NotNull(note.GetString());
    }

    [Fact]
    public async Task CurrentWorkProjection_WithDirectAgentOnly_NoLifecycleEvents_ReturnsItems()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "dae-only",
            displayName = "DAE Only",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();
        Assert.NotNull(channel);

        // Post messages with source_kind = "wake_event" to simulate
        // direct-agent wake events without going through the DAE endpoint
        for (var i = 0; i < 2; i++)
        {
            var reqId = $"direct-agent-message:{channel.Id}:wake-agent:{Guid.NewGuid():N}";
            await client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages", new
            {
                senderType = "user",
                senderIdentity = "operator",
                body = $"Direct agent message {i}",
                messageKind = "human_text",
                sourceKind = "wake_event",
                sourceId = reqId,
                workerRunId = $"dae-run-{i}",
                workerRole = "coder",
                assignmentId = $"assign-{i}",
                sessionOwnerId = $"owner-{i}",
                sessionId = $"ses-{i}"
            });
        }

        using var response = await client.GetAsync($"/api/agent-work/current?channelId={channel.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("items", out var items));
        Assert.True(items.GetArrayLength() > 0, "Should have projection items from direct-agent events");

        var first = items[0];
        Assert.Contains("wake-agent", first.GetProperty("agentIdentity").GetString());

        Assert.True(first.TryGetProperty("evidenceProvenance", out var provenance));
        var provList = provenance.EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("direct_agent_event", provList);
        Assert.DoesNotContain("lifecycle_event", provList);

        // Should have evidence links pointing back to direct-agent-events
        Assert.True(first.TryGetProperty("evidenceLinks", out var links));
        var linkList = links.EnumerateArray().Select(l => l.GetString()).ToList();
        Assert.Contains(linkList, l => l is not null && l.Contains("/api/direct-agent-events", StringComparison.Ordinal));

        // Should indicate recorded_only state
        Assert.True(first.TryGetProperty("currentWorkState", out var cws));
        Assert.Equal("recorded_only_direct_agent", cws.GetString());

        // Should have directAgentEventId
        Assert.True(first.TryGetProperty("directAgentEventId", out var daeId));
        Assert.NotNull(daeId.GetString());
    }

    [Fact]
    public async Task CurrentWorkProjection_MixedEvidence_LifecyclePriority()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "mixed-evidence",
            displayName = "Mixed Evidence",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        // Write general activity event first
        await client.PostAsJsonAsync($"/api/channels/{channel!.Id}/activity-events", new
        {
            agentIdentity = "multi-agent",
            eventType = "tool_call_started",
            workerRunId = "activity-run",
            title = "Tool activity"
        });

        // Write lifecycle event
        await client.PostAsJsonAsync("/api/agent-work/lifecycle-events", new
        {
            channelId = channel.Id,
            agentIdentity = "multi-agent",
            eventType = "agent_turn_started",
            workerRunId = "lifecycle-run",
            title = "Lifecycle turn"
        });

        using var response = await client.GetAsync($"/api/agent-work/current?channelId={channel.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("items", out var items));
        Assert.Equal(1, items.GetArrayLength());

        var first = items[0];
        Assert.Equal("multi-agent", first.GetProperty("agentIdentity").GetString());

        // Lifecycle should take priority - workerRunId should be from lifecycle
        Assert.True(first.TryGetProperty("workerRunId", out var runId));
        Assert.Equal("lifecycle-run", runId.GetString());

        // Should have lifecycle_event provenance
        Assert.True(first.TryGetProperty("evidenceProvenance", out var provenance));
        var provList = provenance.EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("lifecycle_event", provList);

        // Should be lifecycle_event_present state
        Assert.True(first.TryGetProperty("currentWorkState", out var cws));
        Assert.Equal("lifecycle_event_present", cws.GetString());

        // State should be "running" per lifecycle-based projection
        Assert.True(first.TryGetProperty("state", out var state));
        Assert.Equal("running", state.GetString());
    }

    [Fact]
    public async Task CurrentWorkProjection_NonWaking_DoesNotCreateMessages()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "nowake-proj",
            displayName = "NoWake Projection",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        // Create activity events
        await client.PostAsJsonAsync($"/api/channels/{channel!.Id}/activity-events", new
        {
            agentIdentity = "quiet-agent",
            eventType = "tool_call_started",
            title = "No wake test"
        });

        // Call projection endpoint multiple times
        for (var i = 0; i < 3; i++)
        {
            using var projResp = await client.GetAsync($"/api/agent-work/current?channelId={channel.Id}");
            Assert.Equal(HttpStatusCode.OK, projResp.StatusCode);
        }

        // Verify no channel messages were created
        var messagesResp = await client.GetAsync($"/api/channels/{channel.Id}/messages?limit=20");
        var messages = await messagesResp.Content.ReadFromJsonAsync<List<object>>(_jsonOptions);
        Assert.NotNull(messages);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task CurrentWorkProjection_EmptyChannel_ReturnsEmptyWithNote()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "empty-proj-note",
            displayName = "Empty Projection Note",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        using var response = await client.GetAsync($"/api/agent-work/current?channelId={channel!.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("items", out var items));
        Assert.Equal(0, items.GetArrayLength());

        // Empty channel should still have stalenessSummary and migrationNote
        Assert.True(doc.RootElement.TryGetProperty("stalenessSummary", out _));
        Assert.True(doc.RootElement.TryGetProperty("migrationNote", out var note));
        Assert.NotNull(note.GetString());
    }

    [Fact]
    public async Task CurrentWorkProjection_ActivityAndDirectAgent_NoLifecycle_ReturnsMerged()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "activity-dae",
            displayName = "Activity + DAE",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();
        Assert.NotNull(channel);

        // Write general activity event
        await client.PostAsJsonAsync($"/api/channels/{channel.Id}/activity-events", new
        {
            agentIdentity = "hybrid-agent",
            eventType = "tool_call_started",
            workerRunId = "act-run",
            workerRole = "coder"
        });

        // Post direct-agent-style wake_event message
        var reqId = $"direct-agent-message:{channel.Id}:hybrid-agent:{Guid.NewGuid():N}";
        await client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages", new
        {
            senderType = "user",
            senderIdentity = "operator",
            body = "Hybrid test",
            messageKind = "human_text",
            sourceKind = "wake_event",
            sourceId = reqId,
            workerRunId = "dae-run"
        });

        using var response = await client.GetAsync($"/api/agent-work/current?channelId={channel.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("items", out var items));
        Assert.True(items.GetArrayLength() > 0, "Should have projection from activity + DAE");

        var first = items[0];
        Assert.True(first.TryGetProperty("evidenceProvenance", out var provenance));
        var provList = provenance.EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("activity_event", provList);
        Assert.Contains("direct_agent_event", provList);
        Assert.DoesNotContain("lifecycle_event", provList);

        // Should have evidence links for both sources
        Assert.True(first.TryGetProperty("evidenceLinks", out var links));
        var linkList = links.EnumerateArray().Select(l => l.GetString()).ToList();
        Assert.Contains(linkList, l =>
            l is not null &&
            l.Contains("/api/channels/", StringComparison.Ordinal) &&
            l.Contains("activity-events", StringComparison.Ordinal));
        Assert.Contains(linkList, l => l is not null && l.Contains("/api/direct-agent-events", StringComparison.Ordinal));

        // State should indicate delivered_no_lifecycle
        Assert.True(first.TryGetProperty("currentWorkState", out var cws));
        Assert.Equal("delivered_no_lifecycle", cws.GetString());
    }

    [Fact]
    public async Task CurrentWorkProjection_IncludesSessionAndDeliveryFields()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "session-dlv",
            displayName = "Session Delivery",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        // Write lifecycle event with session/delivery fields
        await client.PostAsJsonAsync("/api/agent-work/lifecycle-events", new
        {
            channelId = channel!.Id,
            agentIdentity = "session-agent",
            eventType = "worker_process_started",
            sessionId = "ses-xyz-123",
            deliveryRequestId = "dlv-abc-456"
        });

        using var response = await client.GetAsync($"/api/agent-work/current?channelId={channel.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("items", out var items));
        Assert.Equal(1, items.GetArrayLength());

        var first = items[0];
        Assert.True(first.TryGetProperty("sessionId", out var sessionId));
        Assert.Equal("ses-xyz-123", sessionId.GetString());

        Assert.True(first.TryGetProperty("deliveryRequestId", out var dlvId));
        Assert.Equal("dlv-abc-456", dlvId.GetString());
    }

    [Fact]
    public async Task CurrentWorkProjection_IncludesLifecycleStalenessFields()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "staleness-fields",
            displayName = "Staleness Fields",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        const string lastActivityAt = "2026-06-06T10:00:00.0000000Z";
        const string stalenessDeadline = "2026-06-06T10:15:00.0000000Z";

        await client.PostAsJsonAsync("/api/agent-work/lifecycle-events", new
        {
            channelId = channel!.Id,
            agentIdentity = "stale-agent",
            eventType = "heartbeat",
            lastActivityAt,
            stalenessDeadline,
            stateReason = "heartbeat with explicit deadline"
        });

        using var response = await client.GetAsync($"/api/agent-work/current?channelId={channel.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var first = doc.RootElement.GetProperty("items")[0];
        Assert.Equal(lastActivityAt, first.GetProperty("lastActivityAt").GetString());
        Assert.Equal(stalenessDeadline, first.GetProperty("stalenessDeadline").GetString());
        Assert.Equal("heartbeat with explicit deadline", first.GetProperty("stateReason").GetString());
    }

    // ────────────────────────────────────────────────────────────────────
    // #1977 repair: gateway_delivery projection tests
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CurrentWorkProjection_WithGatewayDeliveryOnly_ReturnsGatewayProvenance()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "gw-only",
            displayName = "Gateway Only",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        // Post a gateway_delivery message directly
        var reqId = $"direct-agent-message:{channel!.Id}:gw-agent:{Guid.NewGuid():N}";
        await client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages", new
        {
            senderType = "user",
            senderIdentity = "operator",
            body = "Gateway delivery test",
            messageKind = "human_text",
            sourceKind = "gateway_delivery",
            sourceId = reqId,
            workerRunId = "gw-run-1",
            workerRole = "coder",
            assignmentId = "gw-assign-1"
        });

        using var response = await client.GetAsync($"/api/agent-work/current?channelId={channel.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("items", out var items));
        Assert.True(items.GetArrayLength() > 0, "Should have projection items from gateway delivery");

        var first = items[0];
        Assert.Contains("gw-agent", first.GetProperty("agentIdentity").GetString());

        Assert.True(first.TryGetProperty("evidenceProvenance", out var provenance));
        var provList = provenance.EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("gateway_delivery", provList);
        Assert.DoesNotContain("lifecycle_event", provList);

        // Should have evidence links
        Assert.True(first.TryGetProperty("evidenceLinks", out var links));
        var linkList = links.EnumerateArray().Select(l => l.GetString()).ToList();
        Assert.Contains(linkList, l => l is not null && l.Contains("/api/direct-agent-events", StringComparison.Ordinal));

        // Should indicate delivered_no_lifecycle state
        Assert.True(first.TryGetProperty("currentWorkState", out var cws));
        Assert.Equal("delivered_no_lifecycle", cws.GetString());

        // Should have gateway_delivery_only flag
        Assert.True(first.TryGetProperty("flags", out var flags));
        var flagList = flags.EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("gateway_delivery_only", flagList);
        Assert.Contains("no_lifecycle", flagList);
    }

    [Fact]
    public async Task CurrentWorkProjection_WakeAndGatewayDelivery_NoLifecycle_ReturnsBothProvenance()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "wake-gw",
            displayName = "Wake + Gateway",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        // Post wake_event message
        var wakeReqId = $"direct-agent-message:{channel!.Id}:dual-agent:{Guid.NewGuid():N}";
        await client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages", new
        {
            senderType = "user",
            senderIdentity = "operator",
            body = "Wake test",
            messageKind = "human_text",
            sourceKind = "wake_event",
            sourceId = wakeReqId,
            workerRunId = "wake-run"
        });

        // Post gateway_delivery message
        var gwReqId = $"direct-agent-message:{channel.Id}:dual-agent:{Guid.NewGuid():N}";
        await client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages", new
        {
            senderType = "user",
            senderIdentity = "operator",
            body = "Gateway delivery test",
            messageKind = "human_text",
            sourceKind = "gateway_delivery",
            sourceId = gwReqId,
            workerRunId = "gw-run"
        });

        using var response = await client.GetAsync($"/api/agent-work/current?channelId={channel.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("items", out var items));
        Assert.Equal(1, items.GetArrayLength());

        var first = items[0];
        Assert.True(first.TryGetProperty("evidenceProvenance", out var provenance));
        var provList = provenance.EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("direct_agent_event", provList);
        Assert.Contains("gateway_delivery", provList);

        Assert.True(first.TryGetProperty("currentWorkState", out var cws));
        Assert.Equal("delivered_no_lifecycle", cws.GetString());
    }

    [Fact]
    public async Task CurrentWorkProjection_LifecycleWithGatewayDelivery_CoPresent()
    {
        using var client = _factory.CreateClient();

        var createChannelResp = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "life-gw",
            displayName = "Lifecycle + Gateway",
            kind = "ad_hoc",
            createdBy = "test"
        });
        var channel = await createChannelResp.Content.ReadFromJsonAsync<ChannelPayload>();

        // Write lifecycle event
        await client.PostAsJsonAsync("/api/agent-work/lifecycle-events", new
        {
            channelId = channel!.Id,
            agentIdentity = "life-gw-agent",
            eventType = "agent_turn_started",
            workerRunId = "life-run"
        });

        // Post gateway_delivery message for same agent
        var gwReqId = $"direct-agent-message:{channel.Id}:life-gw-agent:{Guid.NewGuid():N}";
        await client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages", new
        {
            senderType = "user",
            senderIdentity = "operator",
            body = "Gateway co-present",
            messageKind = "human_text",
            sourceKind = "gateway_delivery",
            sourceId = gwReqId,
            workerRunId = "gw-co-run"
        });

        using var response = await client.GetAsync($"/api/agent-work/current?channelId={channel.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("items", out var items));
        Assert.Equal(1, items.GetArrayLength());

        var first = items[0];
        Assert.True(first.TryGetProperty("evidenceProvenance", out var provenance));
        var provList = provenance.EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("lifecycle_event", provList);
        Assert.Contains("gateway_delivery", provList);

        // Lifecycle takes priority for workerRunId
        Assert.True(first.TryGetProperty("workerRunId", out var runId));
        Assert.Equal("life-run", runId.GetString());

        Assert.True(first.TryGetProperty("currentWorkState", out var cws));
        Assert.Equal("lifecycle_event_present", cws.GetString());

        // Should have a gateway evidence link
        Assert.True(first.TryGetProperty("evidenceLinks", out var links));
        var linkList = links.EnumerateArray().Select(l => l.GetString()).ToList();
        Assert.Contains(linkList, l => l is not null && l.Contains("/api/direct-agent-events/", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try
        {
            _factory.Dispose();
            if (File.Exists(_databasePath)) File.Delete(_databasePath);
        }
        catch
        {
            // best effort cleanup
        }
    }

    // Minimal payload shape for deserialization in tests.
    // Reuse the same shape as ChannelApiTests.
    private sealed record ChannelPayload(long Id, string Slug, string DisplayName, string Kind);
}
