using System.Net;
using System.Net.Http.Json;
using DenChannels.Service.Channels;
using DenChannels.Service.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenChannels.Service.Tests;

/// <summary>
/// Tests for #worker-pool lobby channel (task #1771).
/// Covers: lobby seeding, presence lifecycle (idle->leased->draining->released->idle),
/// release acknowledgement gate, concrete instance/member IDs, role/profile grouping,
/// and fake smoke of lease -> project activity -> cleanup -> Core release -> return.
/// </summary>
public sealed class WorkerPoolLobbyTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"den-channels-wpl-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public WorkerPoolLobbyTests()
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

    // =========================================================================
    // Lobby seeding
    // =========================================================================

    [Fact]
    public async Task EnsureWorkerPoolLobby_CreatesLobbyChannel()
    {
        var lobby = await EnsureLobbyChannelAsync();
        Assert.NotNull(lobby);
        Assert.Equal("worker-pool", lobby.Slug);
        Assert.Equal("#worker-pool", lobby.DisplayName);
        Assert.Equal("system", lobby.Kind);
    }

    [Fact]
    public async Task EnsureWorkerPoolLobby_IsIdempotent()
    {
        var first = await EnsureLobbyChannelAsync();
        var second = await EnsureLobbyChannelAsync();
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Slug, second.Slug);
    }

    [Fact]
    public async Task WorkerPoolLobby_ListChannels_IncludesWorkerPool()
    {
        await EnsureLobbyChannelAsync();
        var channels = await _client.GetFromJsonAsync<List<ChannelDto>>("/api/channels");
        Assert.NotNull(channels);
        var lobby = Assert.Single(channels, c => c.Slug == "worker-pool");
        Assert.Equal("#worker-pool", lobby.DisplayName);
    }

    // =========================================================================
    // Presence lifecycle
    // =========================================================================

    [Fact]
    public async Task UpsertPresence_NewMember_DefaultsToIdle()
    {
        var lobby = await EnsureLobbyChannelAsync();
        var presence = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "worker-alpha",
            AgentInstanceId: "inst-001",
            PoolMemberId: "pool-001",
            Profile: "spawned-coder",
            Role: "coder",
            Status: null,
            CurrentAssignmentId: null,
            CurrentTaskId: null,
            CurrentProjectId: null,
            LastActivityAt: null));

        Assert.Equal("worker-alpha", presence.MemberIdentity);
        Assert.Equal("idle", presence.Status);
        Assert.Equal("inst-001", presence.AgentInstanceId);
        Assert.Equal("pool-001", presence.PoolMemberId);
        Assert.Equal("spawned-coder", presence.Profile);
        Assert.Equal("coder", presence.Role);
    }

    [Fact]
    public async Task UpsertPresence_Transition_ToLeased()
    {
        var lobby = await EnsureLobbyChannelAsync();
        var idle = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "worker-beta",
            AgentInstanceId: "inst-002",
            PoolMemberId: null,
            Profile: "spawned-reviewer",
            Role: "reviewer",
            Status: "idle",
            CurrentAssignmentId: null,
            CurrentTaskId: null,
            CurrentProjectId: null,
            LastActivityAt: "2026-05-29T10:00:00Z"));
        Assert.Equal("idle", idle.Status);

        var leased = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "worker-beta",
            AgentInstanceId: "inst-002",
            PoolMemberId: "pool-002",
            Profile: "spawned-reviewer",
            Role: "reviewer",
            Status: "leased",
            CurrentAssignmentId: "assign-001",
            CurrentTaskId: "1771",
            CurrentProjectId: "den-channels",
            LastActivityAt: "2026-05-29T10:05:00Z"));
        Assert.Equal("leased", leased.Status);
        Assert.Equal("assign-001", leased.CurrentAssignmentId);
        Assert.Equal("1771", leased.CurrentTaskId);
        Assert.Equal("den-channels", leased.CurrentProjectId);
    }

    [Fact]
    public async Task UpsertPresence_FullLifecycle_WithReleaseGate()
    {
        var lobby = await EnsureLobbyChannelAsync();

        // 1. idle (join lobby)
        await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "worker-gamma", Status: "idle",
            AgentInstanceId: "inst-003", PoolMemberId: "pool-003",
            Profile: "spawned-coder", Role: "coder",
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: null));

        // 2. leased (assigned)
        var leased = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "worker-gamma", Status: "leased",
            AgentInstanceId: "inst-003", PoolMemberId: "pool-003",
            Profile: "spawned-coder", Role: "coder",
            CurrentAssignmentId: "assign-002", CurrentTaskId: "1772",
            CurrentProjectId: "den-hermes", LastActivityAt: "2026-05-29T11:00:00Z"));
        Assert.Equal("leased", leased.Status);

        // 3. draining (cleanup started)
        var draining = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "worker-gamma", Status: "draining",
            AgentInstanceId: "inst-003", PoolMemberId: "pool-003",
            Profile: "spawned-coder", Role: "coder",
            CurrentAssignmentId: "assign-002", CurrentTaskId: "1772",
            CurrentProjectId: "den-hermes", LastActivityAt: "2026-05-29T12:00:00Z"));
        Assert.Equal("draining", draining.Status);

        // 4. released (cleanup done, waiting for Core release ack)
        var released = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "worker-gamma", Status: "released",
            AgentInstanceId: "inst-003", PoolMemberId: "pool-003",
            Profile: "spawned-coder", Role: "coder",
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: "2026-05-29T12:05:00Z"));
        Assert.Equal("released", released.Status);

        // 5. Attempt to go back to idle WITHOUT Core release ack -> should be BLOCKED
        var blockedIdle = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "worker-gamma", Status: "idle",
            AgentInstanceId: "inst-003", PoolMemberId: "pool-003",
            Profile: "spawned-coder", Role: "coder",
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: "2026-05-29T12:10:00Z"));
        // Should still be 'released' because release_acknowledged=0
        Assert.Equal("released", blockedIdle.Status);

        // 6. Core acknowledges release
        var acked = await AcknowledgeReleaseAsync(lobby.Id, "worker-gamma");
        Assert.NotNull(acked);
        Assert.Equal("released", acked.Status);

        // 7. Now the transition to idle should work
        var available = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "worker-gamma", Status: "idle",
            AgentInstanceId: "inst-003", PoolMemberId: "pool-003",
            Profile: "spawned-coder", Role: "coder",
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: "2026-05-29T12:15:00Z"));
        Assert.Equal("idle", available.Status);
    }

    [Fact]
    public async Task UpsertPresence_QuarantinedStatus_Accepted()
    {
        var lobby = await EnsureLobbyChannelAsync();
        var quarantined = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "worker-delta", Status: "quarantined",
            AgentInstanceId: "inst-004", PoolMemberId: "pool-004",
            Profile: "spawned-coder", Role: "coder",
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: null));
        Assert.Equal("quarantined", quarantined.Status);
    }

    [Fact]
    public async Task UpsertPresence_OfflineStatus_Accepted()
    {
        var lobby = await EnsureLobbyChannelAsync();
        var offline = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "worker-epsilon", Status: "offline",
            AgentInstanceId: null, PoolMemberId: null,
            Profile: "spawned-coder", Role: "coder",
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: null));
        Assert.Equal("offline", offline.Status);
    }

    // =========================================================================
    // Concrete instance/member IDs
    // =========================================================================

    [Fact]
    public async Task UpsertPresence_ConcreteInstanceIds_DistinguishSameProfileWorkers()
    {
        var lobby = await EnsureLobbyChannelAsync();

        // Two workers with same profile but different instances
        var worker1 = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "spawned-coder", Status: "idle",
            AgentInstanceId: "inst-a", PoolMemberId: "pool-a",
            Profile: "spawned-coder", Role: "coder",
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: null));

        var worker2 = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "spawned-coder-2", Status: "idle",
            AgentInstanceId: "inst-b", PoolMemberId: "pool-b",
            Profile: "spawned-coder", Role: "coder",
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: null));

        Assert.Equal("inst-a", worker1.AgentInstanceId);
        Assert.Equal("pool-a", worker1.PoolMemberId);
        Assert.Equal("inst-b", worker2.AgentInstanceId);
        Assert.Equal("pool-b", worker2.PoolMemberId);
        Assert.NotEqual(worker1.Id, worker2.Id);
    }

    // =========================================================================
    // Lobby overview — grouping by role/profile
    // =========================================================================

    [Fact]
    public async Task LobbyOverview_GroupsAvailableWorkersByRoleAndProfile()
    {
        var lobby = await EnsureLobbyChannelAsync();

        // Add several workers with different roles/profiles
        await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "coder-1", Status: "idle",
            AgentInstanceId: "inst-c1", PoolMemberId: "pool-c1",
            Profile: "spawned-coder", Role: "coder",
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: null));

        await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "coder-2", Status: "idle",
            AgentInstanceId: "inst-c2", PoolMemberId: "pool-c2",
            Profile: "spawned-coder", Role: "coder",
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: null));

        await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "reviewer-1", Status: "idle",
            AgentInstanceId: "inst-r1", PoolMemberId: "pool-r1",
            Profile: "spawned-reviewer", Role: "reviewer",
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: null));

        await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "validator-1", Status: "leased",
            AgentInstanceId: "inst-v1", PoolMemberId: "pool-v1",
            Profile: "spawned-validator", Role: "validator",
            CurrentAssignmentId: "assign-003", CurrentTaskId: "1773",
            CurrentProjectId: "den-channels", LastActivityAt: "2026-05-29T13:00:00Z"));

        var overview = await _client.GetFromJsonAsync<WorkerPoolLobbyOverviewResponse>("/api/worker-pool/lobby/presence");
        Assert.NotNull(overview);
        Assert.Equal(4, overview.TotalMembers);
        Assert.Equal(3, overview.AvailableCount);

        // Should have 2 groups: coder/spawned-coder (2), reviewer/spawned-reviewer (1)
        Assert.Equal(2, overview.ByRole.Count);

        var coderGroup = Assert.Single(overview.ByRole, g => g.Role == "coder");
        Assert.Equal("spawned-coder", coderGroup.Profile);
        Assert.Equal(2, coderGroup.Count);

        var reviewerGroup = Assert.Single(overview.ByRole, g => g.Role == "reviewer");
        Assert.Equal(1, reviewerGroup.Count);
    }

    // =========================================================================
    // Fake smoke test: lease -> project activity -> cleanup -> Core release -> return
    // =========================================================================

    [Fact]
    public async Task Smoke_LeaseToProjectActivityToCleanupToReleaseToReturn()
    {
        var lobby = await EnsureLobbyChannelAsync();
        const string memberId = "smoke-worker";
        const string instanceId = "smoke-inst";
        const string poolMemberId = "smoke-pool";
        const string assignmentId = "smoke-assign-001";
        const string taskId = "1771";
        const string projectId = "den-channels";
        const string profile = "spawned-coder";
        const string role = "coder";

        // 1. Worker joins lobby (idle / available)
        await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: memberId, Status: "idle",
            AgentInstanceId: instanceId, PoolMemberId: poolMemberId,
            Profile: profile, Role: role,
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: "2026-05-29T08:00:00Z"));

        // 2. Worker gets leased (assigned to a task in a project channel)
        var leased = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: memberId, Status: "leased",
            AgentInstanceId: instanceId, PoolMemberId: poolMemberId,
            Profile: profile, Role: role,
            CurrentAssignmentId: assignmentId, CurrentTaskId: taskId,
            CurrentProjectId: projectId, LastActivityAt: "2026-05-29T08:05:00Z"));
        Assert.Equal("leased", leased.Status);
        Assert.Equal(assignmentId, leased.CurrentAssignmentId);
        Assert.Equal(taskId, leased.CurrentTaskId);
        Assert.Equal(projectId, leased.CurrentProjectId);

        // Verify lobby overview shows leased member (not in idle count)
        var overviewDuringLease = await _client.GetFromJsonAsync<WorkerPoolLobbyOverviewResponse>("/api/worker-pool/lobby/presence");
        Assert.NotNull(overviewDuringLease);
        Assert.Equal(1, overviewDuringLease.TotalMembers);
        Assert.Equal(0, overviewDuringLease.AvailableCount);

        // 3. Cleanup started (draining)
        var draining = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: memberId, Status: "draining",
            AgentInstanceId: instanceId, PoolMemberId: poolMemberId,
            Profile: profile, Role: role,
            CurrentAssignmentId: assignmentId, CurrentTaskId: taskId,
            CurrentProjectId: projectId, LastActivityAt: "2026-05-29T09:00:00Z"));
        Assert.Equal("draining", draining.Status);

        // 4. Cleanup done but NOT released yet — set to released
        var released = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: memberId, Status: "released",
            AgentInstanceId: instanceId, PoolMemberId: poolMemberId,
            Profile: profile, Role: role,
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: "2026-05-29T09:05:00Z"));
        Assert.Equal("released", released.Status);

        // 5. Attempt immediate return rejected (no Core acknowledgment yet)
        var blockedReturn = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: memberId, Status: "idle",
            AgentInstanceId: instanceId, PoolMemberId: poolMemberId,
            Profile: profile, Role: role,
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: "2026-05-29T09:10:00Z"));
        Assert.Equal("released", blockedReturn.Status);

        // 6. Core acknowledges release
        var ackResult = await AcknowledgeReleaseAsync(lobby.Id, memberId);
        Assert.NotNull(ackResult);

        // 7. Now return to available succeeds
        var available = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: memberId, Status: "idle",
            AgentInstanceId: instanceId, PoolMemberId: poolMemberId,
            Profile: profile, Role: role,
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: "2026-05-29T09:15:00Z"));
        Assert.Equal("idle", available.Status);

        // Verify lobby overview shows worker as available again
        var overviewFinal = await _client.GetFromJsonAsync<WorkerPoolLobbyOverviewResponse>("/api/worker-pool/lobby/presence");
        Assert.NotNull(overviewFinal);
        Assert.Equal(1, overviewFinal.TotalMembers);
        Assert.Equal(1, overviewFinal.AvailableCount);

        var coderGroup = Assert.Single(overviewFinal.ByRole, g => g.Role == "coder");
        Assert.Equal(profile, coderGroup.Profile);
        Assert.Single(coderGroup.Members);
    }

    [Fact]
    public async Task Smoke_AcknowledgeReleaseViaRoute()
    {
        var lobby = await EnsureLobbyChannelAsync();
        const string memberId = "ack-worker";

        // Set up a released member
        await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: memberId, Status: "released",
            AgentInstanceId: "ack-inst", PoolMemberId: null,
            Profile: "spawned-coder", Role: "coder",
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: null));

        // Acknowledge via route
        var response = await _client.PostAsync($"/api/worker-pool/lobby/presence/{memberId}/acknowledge-release", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var acked = await response.Content.ReadFromJsonAsync<WorkerPoolLobbyPresenceDto>();
        Assert.NotNull(acked);
        Assert.Equal("released", acked.Status);

        // Now transition to idle should work
        var idle = await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: memberId, Status: "idle",
            AgentInstanceId: "ack-inst", PoolMemberId: null,
            Profile: "spawned-coder", Role: "coder",
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: null));
        Assert.Equal("idle", idle.Status);
    }

    [Fact]
    public async Task Smoke_AcknowledgeRelease_NotFoundForNonReleasedWorker()
    {
        var lobby = await EnsureLobbyChannelAsync();

        await UpsertPresenceAsync(lobby.Id, new UpsertWorkerPoolLobbyPresenceRequest(
            MemberIdentity: "idle-worker", Status: "idle",
            AgentInstanceId: null, PoolMemberId: null,
            Profile: "spawned-coder", Role: "coder",
            CurrentAssignmentId: null, CurrentTaskId: null,
            CurrentProjectId: null, LastActivityAt: null));

        var response = await _client.PostAsync("/api/worker-pool/lobby/presence/idle-worker/acknowledge-release", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =========================================================================
    // Database initializer seed tests
    // =========================================================================

    [Fact]
    public void ProductionAppSettings_WorkerPoolLobbyConfiguration()
    {
        var appSettingsPath = FindRepositoryFile("src/DenChannels.Service/appsettings.json");
        var config = new ConfigurationBuilder()
            .AddJsonFile(appSettingsPath, optional: false, reloadOnChange: false)
            .Build();
        var options = config.GetSection(DenChannelsOptions.SectionName).Get<DenChannelsOptions>();

        Assert.NotNull(options);
        Assert.False(options.WorkerPool.Disabled);
        Assert.Equal("http://127.0.0.1:5299", options.WorkerPool.BaseUrl);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private async Task<ChannelDto> EnsureLobbyChannelAsync()
    {
        var response = await _client.PutAsync("/api/worker-pool/lobby", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChannelDto>())!;
    }

    private async Task<WorkerPoolLobbyPresenceDto> UpsertPresenceAsync(
        long channelId, UpsertWorkerPoolLobbyPresenceRequest request)
    {
        var response = await _client.PutAsJsonAsync("/api/worker-pool/lobby/presence", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorkerPoolLobbyPresenceDto>())!;
    }

    private async Task<WorkerPoolLobbyPresenceDto?> AcknowledgeReleaseAsync(long channelId, string memberIdentity)
    {
        var response = await _client.PostAsync(
            $"/api/worker-pool/lobby/presence/{memberIdentity}/acknowledge-release", null);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<WorkerPoolLobbyPresenceDto>()
            : null;
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !dir.GetFiles("*.slnx").Any(f => f.Name == "DenChannels.slnx") &&
               !dir.GetFiles("*.sln").Any())
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("Repository root not found.");
        return Path.Combine(dir.FullName, relativePath);
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _client?.Dispose();
        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
    }
}
