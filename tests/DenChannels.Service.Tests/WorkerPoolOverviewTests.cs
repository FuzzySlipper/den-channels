using System.Net;
using System.Net.Http.Json;
using DenChannels.Service.AgentsOverview;
using DenChannels.Service.Channels;
using DenChannels.Service.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DenChannels.Service.Tests;

/// <summary>
/// Tests for worker-pool assignment state composition in Agents Overview (task #1727).
/// </summary>
public sealed class WorkerPoolOverviewTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"den-channels-wp-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public WorkerPoolOverviewTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenChannels:Database:Path"] = _databasePath,
                    ["DenChannels:Database:ApplyMigrationsOnStartup"] = "true",
                    ["DenChannels:Gateway:Disabled"] = "true",
                    ["DenChannels:WorkerPool:Disabled"] = "true"  // Worker pool unavailable by default
                });
            }));
        _client = _factory.CreateClient();
    }

    // =========================================================================
    // Overview endpoint — worker-pool state projection
    // =========================================================================

    [Fact]
    public async Task WorkerPool_NoData_EmptyList()
    {
        var response = await _client.GetFromJsonAsync<WorkerPoolOverviewResponsePayload>("/api/agents/overview");

        Assert.NotNull(response);
        Assert.Empty(response.Agents);
        Assert.Equal(0, response.TotalCount);
    }

    [Fact]
    public async Task WorkerPool_AvailableWorker_ShowsAvailable()
    {
        var channel = await EnsureDefaultChannelAsync("wp-avail-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "worker-avail",
            wakePolicy = "mentions_only"
        });

        var response = await _client.GetFromJsonAsync<WorkerPoolOverviewResponsePayload>("/api/agents/overview");

        Assert.NotNull(response);
        var agent = Assert.Single(response.Agents);
        Assert.Equal("worker-avail", agent.AgentIdentity);
        // Without worker-pool source, no worker pool state is shown
        // Worker pool source reports unavailable
        Assert.NotNull(response.SourceHealth);
    }

    [Fact]
    public async Task WorkerPool_WorkerPoolUnavailable_DoesNotBreak()
    {
        // Gateway disabled, worker-pool disabled — still works with Channels data only
        var channel = await EnsureDefaultChannelAsync("wp-unavail-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "unavail-worker",
            wakePolicy = "mentions_only"
        });

        var response = await _client.GetFromJsonAsync<WorkerPoolOverviewResponsePayload>("/api/agents/overview");

        Assert.NotNull(response);
        Assert.Equal(1, response.TotalCount);
        var agent = Assert.Single(response.Agents);
        Assert.Equal("unavail-worker", agent.AgentIdentity);
    }

    // =========================================================================
    // Detail endpoint — worker-pool state
    // =========================================================================

    [Fact]
    public async Task WorkerPool_Detail_ShowsMinimalWithoutWorkerPool()
    {
        var channel = await EnsureDefaultChannelAsync("wp-detail-proj");
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity = "detail-worker",
            wakePolicy = "mentions_only"
        });

        var response = await _client.GetFromJsonAsync<WorkerPoolDetailResponsePayload>(
            "/api/agents/detail-worker/overview");

        Assert.NotNull(response);
        Assert.Equal("detail-worker", response.AgentIdentity);
        Assert.NotNull(response.SourceHealth);
    }

    // =========================================================================
    // Composition unit tests — worker-pool DTO construction and flag derivation
    // =========================================================================

    [Fact]
    public void ComposeWorkerPoolState_FromMemberState_ConstructsCorrectDto()
    {
        var memberState = new WorkerPoolMemberStateDto(
            MemberIdentity: "test-worker",
            Role: "coder",
            ToolProfile: "default",
            Availability: "available",
            LastActivityAt: "2026-05-29T10:00:00Z",
            CurrentAssignment: new WorkerPoolMemberAssignmentStateDto(
                AssignmentId: "assign-001",
                TaskId: "1727",
                ProjectId: "den-channels",
                LeaseOwner: "planner-1",
                LeaseExpiresAt: "2026-05-29T11:00:00Z",
                Phase: "running",
                CheckpointType: "checkpoint",
                CheckpointHandle: "ch-001",
                LastCheckpointAt: "2026-05-29T10:30:00Z"),
            Flags: null);

        var assignmentDto = memberState.CurrentAssignment is not null
            ? new WorkerPoolAssignmentDto(
                memberState.CurrentAssignment.AssignmentId,
                memberState.CurrentAssignment.TaskId,
                memberState.CurrentAssignment.ProjectId,
                memberState.CurrentAssignment.LeaseOwner,
                memberState.CurrentAssignment.LeaseExpiresAt,
                memberState.CurrentAssignment.Phase,
                memberState.CurrentAssignment.CheckpointType,
                memberState.CurrentAssignment.CheckpointHandle,
                memberState.CurrentAssignment.LastCheckpointAt)
            : null;

        var memberDto = new WorkerPoolMemberDto(
            memberState.MemberIdentity,
            memberState.Role,
            memberState.ToolProfile,
            memberState.Availability,
            memberState.LastActivityAt,
            assignmentDto,
            memberState.Flags);

        Assert.Equal("test-worker", memberDto.MemberIdentity);
        Assert.Equal("coder", memberDto.Role);
        Assert.Equal("default", memberDto.ToolProfile);
        Assert.Equal("available", memberDto.Availability);
        Assert.NotNull(memberDto.CurrentAssignment);
        Assert.Equal("assign-001", memberDto.CurrentAssignment!.AssignmentId);
        Assert.Equal("running", memberDto.CurrentAssignment.Phase);
        Assert.Equal("ch-001", memberDto.CurrentAssignment.CheckpointHandle);
    }

    [Fact]
    public void ComposeWorkerPoolState_LeasedMember_SetsCorrectFlags()
    {
        var memberState = new WorkerPoolMemberStateDto(
            MemberIdentity: "leased-worker",
            Role: "coder",
            ToolProfile: "default",
            Availability: "leased",
            LastActivityAt: null,
            CurrentAssignment: new WorkerPoolMemberAssignmentStateDto(
                AssignmentId: "assign-002",
                TaskId: "1727",
                ProjectId: "den-channels",
                LeaseOwner: "planner-1",
                LeaseExpiresAt: "2026-05-29T11:00:00Z",
                Phase: "running",
                CheckpointType: null,
                CheckpointHandle: null,
                LastCheckpointAt: null),
            Flags: null);

        var flags = DeriveWorkerPoolFlags(memberState, [], []);
        Assert.Contains("worker_pool_leased", flags);
        Assert.DoesNotContain("worker_pool_quarantined", flags);
        Assert.DoesNotContain("worker_pool_offline", flags);
    }

    [Fact]
    public void ComposeWorkerPoolState_QuarantinedMember_SetsCorrectFlags()
    {
        var memberState = new WorkerPoolMemberStateDto(
            MemberIdentity: "quarantined-worker",
            Role: "coder",
            ToolProfile: "default",
            Availability: "quarantined",
            LastActivityAt: null,
            CurrentAssignment: null,
            Flags: null);

        var flags = DeriveWorkerPoolFlags(memberState, [], []);
        Assert.Contains("worker_pool_quarantined", flags);
        Assert.DoesNotContain("worker_pool_leased", flags);
    }

    [Fact]
    public void ComposeWorkerPoolState_CleanupPendingPhase_SetsCleanupFlag()
    {
        var memberState = new WorkerPoolMemberStateDto(
            MemberIdentity: "cleanup-worker",
            Role: "coder",
            ToolProfile: "default",
            Availability: "leased",
            LastActivityAt: null,
            CurrentAssignment: new WorkerPoolMemberAssignmentStateDto(
                AssignmentId: "assign-003",
                TaskId: "1727",
                ProjectId: "den-channels",
                LeaseOwner: "planner-1",
                LeaseExpiresAt: "2026-05-29T11:00:00Z",
                Phase: "cleanup_pending",
                CheckpointType: null,
                CheckpointHandle: null,
                LastCheckpointAt: "2026-05-29T10:30:00Z"),
            Flags: null);

        var flags = DeriveWorkerPoolFlags(memberState, [], []);
        Assert.Contains("cleanup_pending", flags);
    }

    [Fact]
    public void ComposeWorkerPoolState_SourceDisagreement_LeasedButDeliveriesTerminal()
    {
        var memberState = new WorkerPoolMemberStateDto(
            MemberIdentity: "disagree-worker",
            Role: "coder",
            ToolProfile: "default",
            Availability: "leased",
            LastActivityAt: null,
            CurrentAssignment: new WorkerPoolMemberAssignmentStateDto(
                AssignmentId: "assign-004",
                TaskId: "1727",
                ProjectId: "den-channels",
                LeaseOwner: "planner-1",
                LeaseExpiresAt: "2026-05-29T11:00:00Z",
                Phase: "running",
                CheckpointType: null,
                CheckpointHandle: null,
                LastCheckpointAt: null),
            Flags: null);

        // All terminal deliveries = no non-terminal activity in Gateway = source disagreement
        var allTerminalDeliveries = new List<WorkerPoolDeliveryOverviewPayload>
        {
            new(DeliveryRequestId: "del-1", State: "completed", Status: "completed", Terminal: true, CreatedAt: null, UpdatedAt: null, Summary: null),
        };

        var flags = DeriveWorkerPoolFlags(memberState, allTerminalDeliveries, []);
        Assert.Contains("source_disagreement", flags);
    }

    [Fact]
    public void ComposeWorkerPoolState_TraceHandles_HaveCorrectUrls()
    {
        var memberState = new WorkerPoolMemberStateDto(
            MemberIdentity: "trace-worker",
            Role: "coder",
            ToolProfile: "default",
            Availability: "leased",
            LastActivityAt: null,
            CurrentAssignment: new WorkerPoolMemberAssignmentStateDto(
                AssignmentId: "assign-trace-001",
                TaskId: "1727",
                ProjectId: "den-channels",
                LeaseOwner: "planner-1",
                LeaseExpiresAt: "2026-05-29T11:00:00Z",
                Phase: "running",
                CheckpointType: null,
                CheckpointHandle: null,
                LastCheckpointAt: null),
            Flags: null);

        var assignmentId = memberState.CurrentAssignment?.AssignmentId;
        var activityHandle = assignmentId is not null
            ? $"/api/assignments/{assignmentId}/transcript"
            : null;

        Assert.Equal("/api/assignments/assign-trace-001/transcript", activityHandle);
    }

    [Fact]
    public void ComposeWorkerPoolState_NoAssignment_ReturnsNullAssignmentFields()
    {
        var memberState = new WorkerPoolMemberStateDto(
            MemberIdentity: "idle-worker",
            Role: "coder",
            ToolProfile: "default",
            Availability: "available",
            LastActivityAt: "2026-05-29T09:00:00Z",
            CurrentAssignment: null,
            Flags: null);

        Assert.Null(memberState.CurrentAssignment);

        var flags = DeriveWorkerPoolFlags(memberState, [], []);
        Assert.DoesNotContain(flags, f => f.StartsWith("worker_pool_"));
        Assert.DoesNotContain("cleanup_pending", flags);
    }

    [Fact]
    public void ComposeWorkerPoolState_DrainingMember_SetsDrainingFlag()
    {
        var memberState = new WorkerPoolMemberStateDto(
            MemberIdentity: "draining-worker",
            Role: "coder",
            ToolProfile: "default",
            Availability: "draining",
            LastActivityAt: null,
            CurrentAssignment: null,
            Flags: null);

        var flags = DeriveWorkerPoolFlags(memberState, [], []);
        Assert.Contains("worker_pool_draining", flags);
    }

    [Fact]
    public void ComposeWorkerPoolState_OfflineMember_SetsOfflineFlag()
    {
        var memberState = new WorkerPoolMemberStateDto(
            MemberIdentity: "offline-worker",
            Role: "coder",
            ToolProfile: "default",
            Availability: "offline",
            LastActivityAt: null,
            CurrentAssignment: null,
            Flags: null);

        var flags = DeriveWorkerPoolFlags(memberState, [], []);
        Assert.Contains("worker_pool_offline", flags);
    }

    [Fact]
    public void ComposeWorkerPoolState_SourceDisagreement_NotRaisedForAvailable()
    {
        var memberState = new WorkerPoolMemberStateDto(
            MemberIdentity: "avail-worker",
            Role: "coder",
            ToolProfile: "default",
            Availability: "available",
            LastActivityAt: null,
            CurrentAssignment: null,
            Flags: null);

        var allTerminalDeliveries = new List<WorkerPoolDeliveryOverviewPayload>
        {
            new(DeliveryRequestId: "del-1", State: "completed", Status: "completed", Terminal: true, CreatedAt: null, UpdatedAt: null, Summary: null),
        };

        var flags = DeriveWorkerPoolFlags(memberState, allTerminalDeliveries, []);
        // Not leased, so no source_disagreement
        Assert.DoesNotContain("source_disagreement", flags);
    }

    [Fact]
    public async Task WorkerPoolStateClient_ComposesActualCoreMemberAndAssignmentEndpoints()
    {
        using var httpClient = new HttpClient(new WorkerPoolCoreStubHandler(req =>
        {
            if (req.RequestUri?.PathAndQuery == "/api/worker-pool/members?limit=200&workerIdentity=core-worker")
            {
                return """
                {
                  "members": [
                    {
                      "worker_identity": "core-worker",
                      "display_name": "Core Worker",
                      "status": "busy",
                      "last_heartbeat": "2026-05-29T12:00:00Z",
                      "metadata": "{\"tool_profile\":\"spawned-coder\"}",
                      "created_at": "2026-05-29T11:00:00Z",
                      "updated_at": "2026-05-29T12:00:00Z"
                    }
                  ],
                  "count": 1
                }
                """;
            }

            if (req.RequestUri?.PathAndQuery == "/api/worker-pool/assignments?limit=200&projectId=den-channels&workerIdentity=core-worker")
            {
                return """
                {
                  "assignments": [
                    {
                      "id": 42,
                      "worker_identity": "core-worker",
                      "run_id": "run-42",
                      "project_id": "den-channels",
                      "task_id": 1727,
                      "role": "coder",
                      "assigned_by": "den-mcp-runner",
                      "state": "running",
                      "latest_checkpoint_id": 77,
                      "created_at": "2026-05-29T11:30:00Z",
                      "updated_at": "2026-05-29T12:05:00Z"
                    }
                  ],
                  "count": 1
                }
                """;
            }

            return null;
        }));
        var options = Options.Create(new DenChannelsOptions
        {
            WorkerPool = new WorkerPoolOptions
            {
                Disabled = false,
                BaseUrl = "http://core.invalid",
                TimeoutSeconds = 5
            }
        });
        var client = new WorkerPoolStateClient(httpClient, options, NullLogger<WorkerPoolStateClient>.Instance);

        var state = await client.FetchWorkersAsync("den-channels", "core-worker");

        Assert.NotNull(state);
        var member = Assert.Single(state.Members);
        Assert.Equal("core-worker", member.MemberIdentity);
        Assert.Equal("leased", member.Availability);
        Assert.Equal("coder", member.Role);
        Assert.Equal("spawned-coder", member.ToolProfile);
        Assert.NotNull(member.CurrentAssignment);
        Assert.Equal("42", member.CurrentAssignment!.AssignmentId);
        Assert.Equal("1727", member.CurrentAssignment.TaskId);
        Assert.Equal("running", member.CurrentAssignment.Phase);
        Assert.Equal("/api/worker-pool/checkpoints/77", member.CurrentAssignment.CheckpointHandle);
    }

    [Fact]
    public async Task WorkerPoolStateClient_FetchAssignmentTrace_ComposesAssignmentCheckpointsAndResponses()
    {
        using var httpClient = new HttpClient(new WorkerPoolCoreStubHandler(req =>
        {
            if (req.RequestUri?.PathAndQuery == "/api/worker-pool/assignments/42")
                return """
                {
                  "id": 42,
                  "worker_identity": "core-worker",
                  "run_id": "run-42",
                  "project_id": "den-channels",
                  "task_id": 1737,
                  "role": "coder",
                  "assigned_by": "den-mcp-runner",
                  "state": "completed",
                  "latest_checkpoint_id": 101,
                  "cleanup_evidence": "{\"scratch_cleanup\":true}",
                  "cleanup_recorded_at": "2026-05-29 13:52:46",
                  "acquired_at": "2026-05-29 13:50:00",
                  "released_at": "2026-05-29 13:53:00",
                  "created_at": "2026-05-29T13:50:00Z",
                  "updated_at": "2026-05-29T13:53:00Z"
                }
                """;
            if (req.RequestUri?.PathAndQuery == "/api/worker-pool/checkpoints?assignmentId=42&runId=run-42&limit=200")
                return """
                {
                  "checkpoints": [
                    { "id": 100, "assignment_id": 42, "run_id": "run-42", "checkpoint_type": "progress", "payload": "{\"type\":\"ack\"}", "created_at": "2026-05-29T13:51:00Z" },
                    { "id": 101, "assignment_id": 42, "run_id": "run-42", "checkpoint_type": "completion", "payload": "{\"status\":\"completed\"}", "created_at": "2026-05-29T13:52:00Z" }
                  ],
                  "count": 2
                }
                """;
            if (req.RequestUri?.PathAndQuery == "/api/worker-pool/responses/by-run/run-42?limit=200")
                return """
                {
                  "responses": [
                    { "id": 200, "checkpoint_id": 100, "assignment_id": 42, "run_id": "run-42", "response_type": "ack", "payload": "{\"verdict\":\"approved\"}", "created_at": "2026-05-29T13:51:10Z" }
                  ],
                  "count": 1
                }
                """;
            return null;
        }));
        var options = Options.Create(new DenChannelsOptions
        {
            WorkerPool = new WorkerPoolOptions { Disabled = false, BaseUrl = "http://core.invalid" }
        });
        var client = new WorkerPoolStateClient(httpClient, options, NullLogger<WorkerPoolStateClient>.Instance);

        var trace = await client.FetchAssignmentTraceAsync("42");

        Assert.NotNull(trace);
        Assert.Equal(42, trace.Assignment.Id);
        Assert.Equal("completed", trace.Assignment.State);
        Assert.Equal("run-42", trace.Assignment.RunId);
        Assert.Equal(2, trace.Checkpoints.Count);
        Assert.Equal("progress", trace.Checkpoints[0].CheckpointType);
        Assert.Single(trace.Responses);
        Assert.Equal(100, trace.Responses[0].CheckpointId);
    }

    [Fact]
    public async Task WorkerPoolStateClient_UsesCoreCleanupPendingProjectionForTerminalUnreleasedAssignment()
    {
        using var httpClient = new HttpClient(new WorkerPoolCoreStubHandler(req =>
        {
            if (req.RequestUri?.PathAndQuery == "/api/worker-pool/members?limit=200")
                return "{\"members\":[{\"worker_identity\":\"cleanup-core\",\"status\":\"busy\"}],\"count\":1}";
            if (req.RequestUri?.PathAndQuery == "/api/worker-pool/assignments?limit=200")
                return "{\"assignments\":[{\"id\":43,\"worker_identity\":\"cleanup-core\",\"run_id\":\"run-43\",\"project_id\":\"den-channels\",\"task_id\":1727,\"role\":\"coder\",\"assigned_by\":\"runner\",\"state\":\"completed\"}],\"count\":1}";
            return null;
        }));
        var options = Options.Create(new DenChannelsOptions
        {
            WorkerPool = new WorkerPoolOptions { Disabled = false, BaseUrl = "http://core.invalid" }
        });
        var client = new WorkerPoolStateClient(httpClient, options, NullLogger<WorkerPoolStateClient>.Instance);

        var state = await client.FetchWorkersAsync();

        Assert.NotNull(state);
        var member = Assert.Single(state.Members);
        Assert.Equal("leased", member.Availability);
        Assert.Contains("cleanup_pending", member.Flags ?? []);
        Assert.Equal("cleanup_pending", member.CurrentAssignment?.Phase);
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
    // Worker-pool flag derivation helper (mirrors production logic)
    // =========================================================================

    private static List<string> DeriveWorkerPoolFlags(
        WorkerPoolMemberStateDto memberState,
        IReadOnlyList<WorkerPoolDeliveryOverviewPayload> scopedDeliveries,
        IReadOnlyList<ChannelActivityEventDto> activityEvents)
    {
        var flags = new List<string>();
        var availability = memberState.Availability ?? string.Empty;

        if (string.Equals(availability, "leased", StringComparison.OrdinalIgnoreCase))
            flags.Add("worker_pool_leased");
        else if (string.Equals(availability, "quarantined", StringComparison.OrdinalIgnoreCase))
            flags.Add("worker_pool_quarantined");
        else if (string.Equals(availability, "draining", StringComparison.OrdinalIgnoreCase))
            flags.Add("worker_pool_draining");
        else if (string.Equals(availability, "offline", StringComparison.OrdinalIgnoreCase))
            flags.Add("worker_pool_offline");

        // Source disagreement: Core says leased but all Gateway deliveries are terminal
        if (string.Equals(availability, "leased", StringComparison.OrdinalIgnoreCase) &&
            scopedDeliveries.Count > 0 &&
            scopedDeliveries.All(d => d.Terminal))
        {
            flags.Add("source_disagreement");
        }

        // Cleanup_pending from phase
        if (memberState.CurrentAssignment?.Phase is not null &&
            string.Equals(memberState.CurrentAssignment.Phase, "cleanup_pending", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add("cleanup_pending");
        }

        return flags;
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

    private sealed class WorkerPoolCoreStubHandler(Func<HttpRequestMessage, string?> responseForRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = responseForRequest(request);
            if (body is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    // ---- Local record types ----

    private sealed record ChannelStub(long Id, string Slug, string Kind, string? ProjectId);

    private sealed record WorkerPoolOverviewResponsePayload(
        List<WorkerPoolAgentOverviewItemPayload> Agents,
        int TotalCount,
        WorkerPoolSourceHealthPayload SourceHealth);

    private sealed record WorkerPoolSourceHealthPayload(
        WorkerPoolSourceServiceStatusPayload? Channels,
        WorkerPoolSourceServiceStatusPayload? Gateway,
        WorkerPoolSourceServiceStatusPayload? WorkerPool);

    private sealed record WorkerPoolSourceServiceStatusPayload(
        string Status,
        string? Warning = null);

    private sealed record WorkerPoolAgentOverviewItemPayload(
        string AgentIdentity,
        string? OperatorStatus,
        string? WorkState,
        string? Severity,
        WorkerPoolAgentSummaryPayload? Summary,
        List<string> Flags,
        WorkerPoolAgentLinksPayload? Links,
        List<WorkerPoolChannelMembershipPayload>? Memberships,
        List<WorkerPoolGatewayBindingPayload>? Bindings,
        List<WorkerPoolDeliveryOverviewPayload>? DeliverySummaries,
        List<WorkerPoolActivityEventPayload>? RecentActivity,
        WorkerPoolMemberStatePayload? WorkerPoolState,
        WorkerPoolAssignmentPayload? CurrentAssignment,
        WorkerPoolAssignmentTracePayload? AssignmentTrace);

    private sealed record WorkerPoolAgentSummaryPayload(
        int ChannelCount,
        int ActiveMembershipCount,
        int ActiveDeliveryCount,
        int RecentActivityCount,
        string? LatestActivityAt,
        string? HighestSeverity,
        int StaleDeliveryCount = 0);

    private sealed record WorkerPoolAgentLinksPayload(
        string? Self,
        string? Memberships,
        string? Bindings,
        string? Activity);

    private sealed record WorkerPoolChannelMembershipPayload(
        long ChannelId,
        string ChannelSlug,
        string ChannelDisplayName,
        string ChannelKind,
        string? ProjectId,
        string MembershipStatus,
        string WakePolicy,
        bool CanSend,
        string? SettingsLabel);

    private sealed record WorkerPoolGatewayBindingPayload(
        string? AgentKey,
        string? Role,
        string? BindingFreshness,
        string? DeliveryState,
        WorkerPoolGatewayDeliveryCountsPayload? DeliveryCounts,
        List<WorkerPoolGatewayAdapterInstancePayload>? AdapterInstances);

    private sealed record WorkerPoolGatewayDeliveryCountsPayload(
        int Active,
        int Completed,
        int Failed,
        int Total);

    private sealed record WorkerPoolGatewayAdapterInstancePayload(
        string AdapterKey,
        string Status,
        string? LastHeartbeat);

    private sealed record WorkerPoolDeliveryOverviewPayload(
        string? DeliveryRequestId,
        string? State,
        string? Status,
        bool Terminal,
        string? CreatedAt,
        string? UpdatedAt,
        string? Summary);

    private sealed record WorkerPoolActivityEventPayload(
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

    private sealed record WorkerPoolMemberStatePayload(
        string? MemberIdentity,
        string? Role,
        string? ToolProfile,
        string? Availability,
        string? LastActivityAt);

    private sealed record WorkerPoolAssignmentPayload(
        string? AssignmentId,
        string? TaskId,
        string? ProjectId,
        string? LeaseOwner,
        string? LeaseExpiresAt,
        string? Phase,
        string? CheckpointType,
        string? CheckpointHandle,
        string? LastCheckpointAt);

    private sealed record WorkerPoolAssignmentTracePayload(
        string? AssignmentId,
        long? ChannelId,
        string? RepresentativeMessageId,
        string? ActivityHandle,
        string? DeliveryHandle);

    private sealed record WorkerPoolDetailResponsePayload(
        string AgentIdentity,
        List<WorkerPoolChannelMembershipPayload>? Memberships,
        List<WorkerPoolGatewayBindingPayload>? Bindings,
        List<WorkerPoolDeliveryOverviewPayload>? CurrentDeliveries,
        List<WorkerPoolDeliveryOverviewPayload>? RecentDeliveries,
        List<WorkerPoolActivityEventPayload>? ActivityEvents,
        List<WorkerPoolTaskAssociationPayload>? TaskAssociations,
        WorkerPoolAgentSummaryPayload? Summary,
        List<string> Flags,
        WorkerPoolSourceHealthPayload SourceHealth,
        WorkerPoolMemberStatePayload? WorkerPoolState,
        WorkerPoolAssignmentPayload? CurrentAssignment,
        WorkerPoolAssignmentTracePayload? AssignmentTrace);

    private sealed record WorkerPoolTaskAssociationPayload(
        long? TaskId,
        string? ProjectId,
        string? Title,
        string? Status,
        int ActivityCount,
        string? LatestActivityAt);
}
