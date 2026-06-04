using System.Net;
using System.Net.Http.Json;
using DenChannels.Service.AgentsOverview;
using DenChannels.Service.Channels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenChannels.Service.Tests;

/// <summary>
/// Tests for the /api/agents/overview endpoints (task #1694).
/// </summary>
public sealed class AgentsOverviewTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"den-channels-ao-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public AgentsOverviewTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenChannels:Database:Path"] = _databasePath,
                    ["DenChannels:Database:ApplyMigrationsOnStartup"] = "true",
                    ["DenChannels:Gateway:Disabled"] = "true"  // Gateway unavailable by default in tests
                });
            }));
        _client = _factory.CreateClient();
    }

    // =========================================================================
    // Overview endpoint tests
    // =========================================================================

    [Fact]
    public async Task AgentsOverview_NoData_ReturnsEmptyList()
    {
        var response = await _client.GetFromJsonAsync<AgentsOverviewResponsePayload>("/api/agents/overview");

        Assert.NotNull(response);
        Assert.Empty(response.Agents);
        Assert.Equal(0, response.TotalCount);
        Assert.NotNull(response.SourceHealth);
        Assert.Equal("available", response.SourceHealth.Channels?.Status);
    }

    [Fact]
    public async Task AgentsOverview_WithMembership_ReturnsAgentRow()
    {
        var channel = await EnsureDefaultChannelAsync("ao-proj-1");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "agent-alpha",
            wakePolicy = "mentions_only"
        });

        var response = await _client.GetFromJsonAsync<AgentsOverviewResponsePayload>("/api/agents/overview");

        Assert.NotNull(response);
        Assert.Equal(1, response.TotalCount);
        var agent = Assert.Single(response.Agents);
        Assert.Equal("agent-alpha", agent.AgentIdentity);
        Assert.Equal("active", agent.OperatorStatus);
        Assert.Equal("idle", agent.WorkState);
        Assert.NotNull(agent.Memberships);
        // There may be an auto-created Agent Commons membership
        var testMembership = Assert.Single(agent.Memberships, m => m.ProjectId == "ao-proj-1");
        Assert.Equal(channel.Id, testMembership.ChannelId);
        Assert.Equal("mentions_only", testMembership.WakePolicy);
    }

    [Fact]
    public async Task AgentsOverview_ScopeAll_IgnoresProjectFilter()
    {
        var channel1 = await EnsureDefaultChannelAsync("ao-scope-proj-1");
        await UpsertMembershipAsync(channel1.Id, new
        {
            memberType = "agent",
            memberIdentity = "agent-a",
            wakePolicy = "mentions_only"
        });

        var channel2 = await EnsureDefaultChannelAsync("ao-scope-proj-2");
        await UpsertMembershipAsync(channel2.Id, new
        {
            memberType = "agent",
            memberIdentity = "agent-b",
            wakePolicy = "all_human_messages"
        });

        // scope=all with projectId set should return all agents regardless of project
        var response = await _client.GetFromJsonAsync<AgentsOverviewResponsePayload>(
            "/api/agents/overview?scope=all&projectId=ao-scope-proj-1");

        Assert.NotNull(response);
        Assert.Equal(2, response.TotalCount);
    }

    [Fact]
    public async Task AgentsOverview_ScopeProject_FiltersByProject()
    {
        var channel1 = await EnsureDefaultChannelAsync("ao-proj-filter-1");
        await UpsertMembershipAsync(channel1.Id, new
        {
            memberType = "agent",
            memberIdentity = "agent-x",
            wakePolicy = "mentions_only"
        });

        var channel2 = await EnsureDefaultChannelAsync("ao-proj-filter-2");
        await UpsertMembershipAsync(channel2.Id, new
        {
            memberType = "agent",
            memberIdentity = "agent-y",
            wakePolicy = "all_human_messages"
        });

        // scope=project (default) with projectId set filters by project
        var response = await _client.GetFromJsonAsync<AgentsOverviewResponsePayload>(
            "/api/agents/overview?projectId=ao-proj-filter-1&scope=project");

        Assert.NotNull(response);
        Assert.Equal(1, response.TotalCount);
        var agent = Assert.Single(response.Agents);
        Assert.Equal("agent-x", agent.AgentIdentity);
        Assert.NotNull(agent.Memberships);
        // Should only have project default + agent-commons memberships
        Assert.Contains(agent.Memberships, m => m.ProjectId == "ao-proj-filter-1");
    }

    [Fact]
    public async Task AgentsOverview_FilterByAgentIdentity()
    {
        var channel = await EnsureDefaultChannelAsync("ao-identity-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "alpha",
            wakePolicy = "mentions_only"
        });
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "beta",
            wakePolicy = "all_human_messages"
        });

        var response = await _client.GetFromJsonAsync<AgentsOverviewResponsePayload>(
            "/api/agents/overview?agentIdentity=alpha");

        Assert.NotNull(response);
        Assert.Equal(1, response.TotalCount);
        Assert.Equal("alpha", response.Agents[0].AgentIdentity);
    }

    [Fact]
    public async Task AgentsOverview_WithActivityEvents_IncludesRecentActivity()
    {
        var channel = await EnsureDefaultChannelAsync("ao-activity-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "worker-bot",
            wakePolicy = "mentions_only"
        });

        // Create two activity events
        await PostActivityEventAsync(channel.Id, new
        {
            projectId = "ao-activity-proj",
            agentIdentity = "worker-bot",
            deliveryRequestId = "delivery-1",
            hermesSessionKey = "session-1",
            eventType = "tool_call_started",
            status = "started",
            terminal = false,
            sequence = 1,
            title = "Fetching context",
            dedupeKey = $"ao-activity:{Guid.NewGuid():N}"
        });
        await PostActivityEventAsync(channel.Id, new
        {
            projectId = "ao-activity-proj",
            agentIdentity = "worker-bot",
            deliveryRequestId = "delivery-1",
            hermesSessionKey = "session-1",
            eventType = "tool_call_completed",
            status = "completed",
            deliveryStage = "tool",
            terminal = true,
            sequence = 2,
            title = "Context fetched",
            dedupeKey = $"ao-activity:{Guid.NewGuid():N}"
        });

        var response = await _client.GetFromJsonAsync<AgentsOverviewResponsePayload>(
            "/api/agents/overview?activityLimit=5");

        Assert.NotNull(response);
        var agent = Assert.Single(response.Agents);
        Assert.Equal("worker-bot", agent.AgentIdentity);
        Assert.Equal("active", agent.WorkState);
        Assert.NotNull(agent.RecentActivity);
        Assert.Equal(2, agent.RecentActivity.Count);
        Assert.NotNull(agent.Summary);
        Assert.Equal(2, agent.Summary.RecentActivityCount);
        Assert.Equal("info", agent.Severity);
    }

    [Fact]
    public async Task AgentsOverview_ActivityLimit_RespectsBound()
    {
        var channel = await EnsureDefaultChannelAsync("ao-limit-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "noisy-bot",
            wakePolicy = "mentions_only"
        });

        for (var i = 1; i <= 5; i++)
        {
            await PostActivityEventAsync(channel.Id, new
            {
                projectId = "ao-limit-proj",
                agentIdentity = "noisy-bot",
                deliveryRequestId = $"delivery-{i}",
                eventType = "tool_call_completed",
                status = "completed",
                terminal = true,
                sequence = i,
                dedupeKey = $"ao-limit:{Guid.NewGuid():N}"
            });
        }

        // activityLimit=2
        var response = await _client.GetFromJsonAsync<AgentsOverviewResponsePayload>(
            "/api/agents/overview?activityLimit=2");

        Assert.NotNull(response);
        var agent = Assert.Single(response.Agents);
        Assert.NotNull(agent.RecentActivity);
        Assert.True(agent.RecentActivity.Count <= 2);
        // Summary count reflects the limited view after per-agent cap
        Assert.Equal(2, agent.Summary!.RecentActivityCount);
    }

    [Fact]
    public async Task AgentsOverview_DeliveredVsCompleted_DistinctInWorkState()
    {
        var channel = await EnsureDefaultChannelAsync("ao-dc-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "done-bot",
            wakePolicy = "mentions_only"
        });

        // All terminal + completed = "completed" work state
        await PostActivityEventAsync(channel.Id, new
        {
            projectId = "ao-dc-proj",
            agentIdentity = "done-bot",
            deliveryRequestId = "delivery-complete",
            eventType = "tool_call_completed",
            status = "completed",
            deliveryStage = "delivery",
            terminal = true,
            sequence = 1,
            dedupeKey = $"ao-dc:{Guid.NewGuid():N}"
        });

        var response = await _client.GetFromJsonAsync<AgentsOverviewResponsePayload>(
            "/api/agents/overview?activityLimit=5");

        Assert.NotNull(response);
        var agent = Assert.Single(response.Agents);
        Assert.Equal("completed", agent.WorkState);
        Assert.Equal("success", agent.Severity);
    }

    // =========================================================================
    // Detail endpoint tests
    // =========================================================================

    [Fact]
    public async Task AgentDetail_UnknownAgent_ReturnsMinimalResponse()
    {
        var channel = await EnsureDefaultChannelAsync("ao-detail-unknown");
        _ = channel; // ensure at least one channel exists

        var response = await _client.GetFromJsonAsync<AgentDetailResponsePayload>(
            "/api/agents/nonexistent-agent/overview");

        Assert.NotNull(response);
        Assert.Equal("nonexistent-agent", response.AgentIdentity);
        Assert.Null(response.Memberships);
        Assert.Contains("missing_membership", response.Flags);
    }

    [Fact]
    public async Task AgentDetail_WithMembershipAndActivity_ReturnsFullData()
    {
        var channel = await EnsureDefaultChannelAsync("ao-detail-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "detail-bot",
            wakePolicy = "mentions_only",
            canSend = true
        });

        await PostActivityEventAsync(channel.Id, new
        {
            projectId = "ao-detail-proj",
            agentIdentity = "detail-bot",
            deliveryRequestId = "detail-delivery",
            hermesSessionKey = "detail-session",
            workerRunId = "run-1",
            workerRole = "coder",
            taskId = 1694L,
            eventType = "tool_call_completed",
            status = "completed",
            terminal = true,
            sequence = 1,
            title = "Implemented overview API",
            summary = "Completed the agents overview endpoint",
            dedupeKey = $"ao-detail:{Guid.NewGuid():N}"
        });

        var response = await _client.GetFromJsonAsync<AgentDetailResponsePayload>(
            "/api/agents/detail-bot/overview?activityLimit=50&deliveryLimit=50");

        Assert.NotNull(response);
        Assert.Equal("detail-bot", response.AgentIdentity);
        Assert.NotNull(response.Memberships);
        // Find the test channel membership (agent-commons may also be present)
        var testMembership = Assert.Single(response.Memberships, m => m.ProjectId == "ao-detail-proj");
        Assert.Equal(channel.Id, testMembership.ChannelId);
        Assert.Equal("mentions_only", testMembership.WakePolicy);
        Assert.True(testMembership.CanSend);

        Assert.NotNull(response.ActivityEvents);
        var activityEvent = Assert.Single(response.ActivityEvents);
        Assert.Equal("detail-bot", activityEvent.AgentIdentity);
        Assert.Equal("tool_call_completed", activityEvent.EventType);
        Assert.Equal(1694L, activityEvent.TaskId);

        Assert.NotNull(response.TaskAssociations);
        var task = Assert.Single(response.TaskAssociations);
        Assert.Equal(1694L, task.TaskId);
        Assert.Equal("completed", task.Status);
        Assert.Equal(1, task.ActivityCount);

        Assert.NotNull(response.Summary);
        // agent-commons auto-membership adds one more channel
        Assert.True(response.Summary.ChannelCount >= 1);
        Assert.True(response.Summary.ActiveMembershipCount >= 1);
        Assert.Equal(1, response.Summary.RecentActivityCount);
    }

    [Fact]
    public async Task AgentDetail_ActivityAndDeliveryLimits_Respected()
    {
        var channel = await EnsureDefaultChannelAsync("ao-detail-limits");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "limit-bot",
            wakePolicy = "mentions_only"
        });

        for (var i = 1; i <= 10; i++)
        {
            await PostActivityEventAsync(channel.Id, new
            {
                projectId = "ao-detail-limits",
                agentIdentity = "limit-bot",
                deliveryRequestId = $"delivery-{i}",
                eventType = "tool_call_completed",
                status = "completed",
                terminal = true,
                sequence = i,
                dedupeKey = $"ao-detail-limits:{Guid.NewGuid():N}"
            });
        }

        var response = await _client.GetFromJsonAsync<AgentDetailResponsePayload>(
            "/api/agents/limit-bot/overview?activityLimit=3");

        Assert.NotNull(response);
        Assert.NotNull(response.ActivityEvents);
        Assert.True(response.ActivityEvents.Count <= 3);
    }

    [Fact]
    public async Task AgentDetail_ProjectFilter_ScopesMemberships()
    {
        var channel1 = await EnsureDefaultChannelAsync("ao-detail-filter-a");
        await UpsertMembershipAsync(channel1.Id, new
        {
            memberType = "agent",
            memberIdentity = "multi-bot",
            wakePolicy = "mentions_only"
        });

        var channel2 = await EnsureDefaultChannelAsync("ao-detail-filter-b");
        await UpsertMembershipAsync(channel2.Id, new
        {
            memberType = "agent",
            memberIdentity = "multi-bot",
            wakePolicy = "all_human_messages"
        });

        var response = await _client.GetFromJsonAsync<AgentDetailResponsePayload>(
            "/api/agents/multi-bot/overview?projectId=ao-detail-filter-a");

        Assert.NotNull(response);
        Assert.NotNull(response.Memberships);
        Assert.Single(response.Memberships);
        Assert.Equal(channel1.Id, response.Memberships[0].ChannelId);
    }

    // =========================================================================
    // Include left memberships
    // =========================================================================

    [Fact]
    public async Task AgentsOverview_IncludeLeftFalse_ExcludesLeftMembers()
    {
        var channel = await EnsureDefaultChannelAsync("ao-left-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "left-agent",
            membershipStatus = "left",
            wakePolicy = "never"
        });

        // Without includeLeft=true, left members should not appear
        var response = await _client.GetFromJsonAsync<AgentsOverviewResponsePayload>("/api/agents/overview");
        Assert.NotNull(response);
        Assert.Empty(response.Agents);
    }

    [Fact]
    public async Task AgentsOverview_IncludeLeftTrue_IncludesLeftMembers()
    {
        var channel = await EnsureDefaultChannelAsync("ao-left-proj-2");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "left-agent-2",
            membershipStatus = "left",
            wakePolicy = "never"
        });

        var response = await _client.GetFromJsonAsync<AgentsOverviewResponsePayload>(
            "/api/agents/overview?includeLeft=true");
        Assert.NotNull(response);
        Assert.NotEmpty(response.Agents);
        var agent = Assert.Single(response.Agents);
        Assert.Equal("left", agent.OperatorStatus);
    }

    // =========================================================================
    // ChannelId filter
    // =========================================================================

    [Fact]
    public async Task AgentsOverview_ChannelIdFilter_ScopesToChannel()
    {
        var channel1 = await EnsureDefaultChannelAsync("ao-chan-filter-1");
        await UpsertMembershipAsync(channel1.Id, new
        {
            memberType = "agent",
            memberIdentity = "chan-agent",
            wakePolicy = "mentions_only"
        });

        var channel2 = await EnsureDefaultChannelAsync("ao-chan-filter-2");
        await UpsertMembershipAsync(channel2.Id, new
        {
            memberType = "agent",
            memberIdentity = "chan-agent",
            wakePolicy = "all_human_messages"
        });

        // Filter to channel1 only
        var response = await _client.GetFromJsonAsync<AgentsOverviewResponsePayload>(
            $"/api/agents/overview?channelId={channel1.Id}");

        Assert.NotNull(response);
        var agent = Assert.Single(response.Agents);
        Assert.NotNull(agent.Memberships);
        var chanMembership = Assert.Single(agent.Memberships, m => m.ChannelId == channel1.Id);
        Assert.Equal(channel1.Id, chanMembership.ChannelId);
        Assert.DoesNotContain(agent.Memberships, m => m.ChannelId == channel2.Id);
        Assert.All(agent.Memberships, m => Assert.Equal(channel1.Id, m.ChannelId));
    }

    // =========================================================================
    // Activity without membership flag
    // =========================================================================

    [Fact]
    public async Task AgentsOverview_ActivityWithoutMembership_FlagsCorrectly()
    {
        var channel = await EnsureDefaultChannelAsync("ao-orphan-proj");

        // Create activity but no membership for this agent
        await PostActivityEventAsync(channel.Id, new
        {
            projectId = "ao-orphan-proj",
            agentIdentity = "orphan-agent",
            deliveryRequestId = "orphan-delivery",
            eventType = "tool_call_started",
            status = "started",
            terminal = false,
            sequence = 1,
            dedupeKey = $"ao-orphan:{Guid.NewGuid():N}"
        });

        var response = await _client.GetFromJsonAsync<AgentsOverviewResponsePayload>("/api/agents/overview");

        Assert.NotNull(response);
        var agent = Assert.Single(response.Agents);
        Assert.Equal("orphan-agent", agent.AgentIdentity);
        Assert.Contains("missing_membership", agent.Flags);
        Assert.Contains("activity_without_membership", agent.Flags);
    }

    // =========================================================================
    // Sequence and flag completeness checks
    // =========================================================================

    [Fact]
    public async Task AgentsOverview_Flags_AreDeterministic()
    {
        var channel = await EnsureDefaultChannelAsync("ao-flags-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "flags-agent",
            wakePolicy = "mentions_only"
        });

        // Run twice, should produce same flags
        var response1 = await _client.GetFromJsonAsync<AgentsOverviewResponsePayload>("/api/agents/overview");
        var response2 = await _client.GetFromJsonAsync<AgentsOverviewResponsePayload>("/api/agents/overview");

        Assert.NotNull(response1);
        Assert.NotNull(response2);
        Assert.Equal(response1.Agents[0].Flags, response2.Agents[0].Flags);
    }

    // =========================================================================
    // ChannelId filter on detail endpoint
    // =========================================================================

    [Fact]
    public async Task AgentDetail_ChannelIdFilter_ScopesMemberships()
    {
        var channel1 = await EnsureDefaultChannelAsync("ao-detail-chan-1");
        var channel2 = await EnsureDefaultChannelAsync("ao-detail-chan-2");

        await UpsertMembershipAsync(channel1.Id, new
        {
            memberType = "agent",
            memberIdentity = "detail-chan-bot",
            wakePolicy = "mentions_only"
        });
        await UpsertMembershipAsync(channel2.Id, new
        {
            memberType = "agent",
            memberIdentity = "detail-chan-bot",
            wakePolicy = "all_human_messages"
        });

        var response = await _client.GetFromJsonAsync<AgentDetailResponsePayload>(
            $"/api/agents/detail-chan-bot/overview?channelId={channel1.Id}");

        Assert.NotNull(response);
        Assert.NotNull(response.Memberships);
        var chanMembership = Assert.Single(response.Memberships, m => m.ChannelId == channel1.Id);
        Assert.Equal(channel1.Id, chanMembership.ChannelId);
        Assert.DoesNotContain(response.Memberships, m => m.ChannelId == channel2.Id);
        Assert.All(response.Memberships, m => Assert.Equal(channel1.Id, m.ChannelId));
    }

    [Fact]
    public async Task AgentDetail_TaskAssociations_FromRecentActivityWithTaskContext()
    {
        // Test that activity events with taskId/workerRunId populate task associations
        var channel = await EnsureDefaultChannelAsync("ao-task-context-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "task-context-bot",
            wakePolicy = "mentions_only"
        });

        // Create activity event with task context (taskId, workerRunId)
        await PostActivityEventAsync(channel.Id, new
        {
            projectId = "ao-task-context-proj",
            agentIdentity = "task-context-bot",
            deliveryRequestId = "task-delivery-1",
            hermesSessionKey = "task-session",
            workerRunId = "run-1730",
            workerRole = "coder",
            taskId = 1730L,
            eventType = "tool_call_completed",
            status = "completed",
            deliveryStage = "tool",
            terminal = false,
            sequence = 1,
            title = "Working on task 1730",
            summary = "Fixed live vs stale work state",
            dedupeKey = $"ao-task-ctx:{Guid.NewGuid():N}"
        });

        var response = await _client.GetFromJsonAsync<AgentDetailResponsePayload>(
            "/api/agents/task-context-bot/overview?activityLimit=50");

        Assert.NotNull(response);
        Assert.NotNull(response.TaskAssociations);
        var task = Assert.Single(response.TaskAssociations);
        Assert.Equal(1730L, task.TaskId);
        Assert.Equal("ao-task-context-proj", task.ProjectId);
        Assert.Equal("in_progress", task.Status); // non-terminal
        Assert.Equal(1, task.ActivityCount);
        Assert.NotNull(task.Title);
    }

    // =========================================================================
    // Disposal
    // =========================================================================

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

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

    private async Task<ActivityEventStub> PostActivityEventAsync(long channelId, object request)
    {
        using var response = await _client.PostAsJsonAsync($"/api/channels/{channelId}/activity-events", request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<ActivityEventStub>();
        Assert.NotNull(payload);
        return payload;
    }

    private static ChannelActivityEventDto MakeActivityEvent(
        string? deliveryRequestId = null,
        string agentIdentity = "test-agent",
        string eventType = "tool_call_completed",
        string status = "completed",
        string deliveryStage = "tool",
        bool terminal = true,
        long? taskId = null,
        string? workerRunId = null,
        string? hermesSessionKey = null,
        string? title = null) => new(
            Id: 0,
            ChannelId: 0,
            ProjectId: "test-project",
            AgentIdentity: agentIdentity,
            DeliveryRequestId: deliveryRequestId,
            HermesSessionKey: hermesSessionKey,
            DisplayBlockId: null,
            ParentHermesSessionKey: null,
            ParentAgentIdentity: null,
            WorkerRunId: workerRunId,
            WorkerRole: null,
            AgentInstanceId: null,
            PoolMemberId: null,
            TaskId: taskId,
            ThreadId: null,
            AnchorMessageId: null,
            AssignmentId: null,
            CheckpointType: null,
            CheckpointHandle: null,
            EventType: eventType,
            Status: status,
            DeliveryStage: deliveryStage,
            Terminal: terminal,
            Sequence: 1,
            UpdateVersion: 1,
            Title: title,
            Summary: null,
            PreviewJson: null,
            MetadataJson: null,
            DedupeKey: "test-dedupe",
            FinalChannelMessageId: null,
            CreatedAt: "2026-05-29T10:00:00Z",
            UpdatedAt: "2026-05-29T10:00:00Z");

    // ---- Local payload records ----

    private sealed record ChannelStub(long Id, string Slug, string Kind, string? ProjectId);

    private sealed record ActivityEventStub(long Id, long ChannelId, string AgentIdentity, string EventType,
        string Status, string DeliveryStage, bool Terminal);

    private sealed record AgentsOverviewResponsePayload(
        List<AgentOverviewItemPayload> Agents,
        int TotalCount,
        SourceHealthPayload SourceHealth);

    private sealed record SourceHealthPayload(
        SourceServiceStatusPayload? Channels);

    private sealed record SourceServiceStatusPayload(
        string Status,
        string? Warning = null);

    private sealed record AgentOverviewItemPayload(
        string AgentIdentity,
        string? OperatorStatus,
        string? WorkState,
        string? Severity,
        AgentSummaryPayload? Summary,
        List<string> Flags,
        AgentLinksPayload? Links,
        List<ChannelMembershipOverviewPayload>? Memberships,
        List<ActivityEventOverviewPayload>? RecentActivity);

    private sealed record AgentSummaryPayload(
        int ChannelCount,
        int ActiveMembershipCount,
        int ActiveDeliveryCount,
        int RecentActivityCount,
        string? LatestActivityAt,
        string? HighestSeverity);

    private sealed record AgentLinksPayload(
        string? Self,
        string? Memberships,
        string? Activity);

    private sealed record ChannelMembershipOverviewPayload(
        long ChannelId,
        string ChannelSlug,
        string ChannelDisplayName,
        string ChannelKind,
        string? ProjectId,
        string MembershipStatus,
        string WakePolicy,
        bool CanSend,
        string? SettingsLabel);

    private sealed record ActivityEventOverviewPayload(
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
        string EventType,
        string Status,
        string DeliveryStage,
        bool Terminal,
        string? Title,
        string? Summary,
        string CreatedAt,
        string UpdatedAt);

    private sealed record AgentDetailResponsePayload(
        string AgentIdentity,
        List<ChannelMembershipOverviewPayload>? Memberships,
        List<ActivityEventOverviewPayload>? ActivityEvents,
        List<TaskAssociationPayload>? TaskAssociations,
        AgentSummaryPayload? Summary,
        List<string> Flags,
        SourceHealthPayload SourceHealth);

    private sealed record TaskAssociationPayload(
        long? TaskId,
        string? ProjectId,
        string? Title,
        string? Status,
        int ActivityCount,
        string? LatestActivityAt);
}
