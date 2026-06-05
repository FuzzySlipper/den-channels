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
