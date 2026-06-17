using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenChannels.Service.Tests;

/// <summary>
/// Tests for the subscription lifecycle (task #2554):
/// - Active agent membership auto-creates ordinary subscription
/// - Re-upsert is idempotent
/// - Deactivating membership releases subscription
/// - Reactivating re-creates subscription
/// - Non-agent memberships create no subscription
/// - Direct-agent readback distinguishes subscription state
/// </summary>
public sealed class SubscriptionLifecycleTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"den-channels-sub-lifecycle-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SubscriptionLifecycleTests()
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
    // Active agent membership creates active ordinary subscription
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AgentMembership_CreatesSubscription()
    {
        var channel = await EnsureDefaultChannelAsync("sub-lifecycle-1");
        var memberIdentity = "sub-agent-1";

        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity,
            wakePolicy = "direct_questions_only"
        });

        // Verify subscription was auto-created
        var subscriptions = await _client.GetFromJsonAsync<SubscriptionDiscoveryResponse>(
            $"/api/channel-subscriptions?memberIdentity={memberIdentity}&includeInactive=true");
        Assert.NotNull(subscriptions);
        Assert.NotEmpty(subscriptions.Subscriptions);

        var sub = subscriptions.Subscriptions[0];
        Assert.Equal(channel.Id, sub.ChannelId);
        Assert.Equal("agent", sub.MemberType);
        Assert.Equal(memberIdentity, sub.MemberIdentity);
        Assert.Equal("active", sub.SubscriptionStatus);
        Assert.Equal("ordinary_channel", sub.SubscriptionPurpose);
        Assert.Equal($"member:{memberIdentity}:ordinary_channel", sub.SubscriptionIdentity);
    }

    // -------------------------------------------------------------------------
    // Idempotent re-upsert does not duplicate subscriptions
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ReUpsertMembership_DoesNotDuplicateSubscription()
    {
        var channel = await EnsureDefaultChannelAsync("sub-lifecycle-2");
        var memberIdentity = "sub-agent-2";

        // First upsert
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity,
            wakePolicy = "direct_questions_only"
        });

        // Second upsert — same agent, same channel
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity,
            wakePolicy = "direct_questions_only"
        });

        // Verify only ONE subscription exists
        var subscriptions = await _client.GetFromJsonAsync<SubscriptionDiscoveryResponse>(
            $"/api/channel-subscriptions?memberIdentity={memberIdentity}&includeInactive=true");
        Assert.NotNull(subscriptions);
        Assert.Single(subscriptions.Subscriptions);
    }

    // -------------------------------------------------------------------------
    // left/muted/banned releases ordinary subscription
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("left")]
    [InlineData("muted")]
    [InlineData("banned")]
    public async Task DeactivatedMembership_ReleasesSubscription(string deactivatedStatus)
    {
        var channel = await EnsureDefaultChannelAsync($"sub-lifecycle-3-{deactivatedStatus}");
        var memberIdentity = $"sub-agent-3-{deactivatedStatus}";

        // Create active membership (auto-creates subscription)
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity,
            wakePolicy = "direct_questions_only"
        });

        // Verify subscription exists
        var beforeSubs = await _client.GetFromJsonAsync<SubscriptionDiscoveryResponse>(
            $"/api/channel-subscriptions?memberIdentity={memberIdentity}&includeInactive=true");
        Assert.NotNull(beforeSubs);
        var beforeSub = Assert.Single(beforeSubs.Subscriptions);
        Assert.Equal("active", beforeSub.SubscriptionStatus);

        // Deactivate membership
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity,
            wakePolicy = "direct_questions_only",
            membershipStatus = deactivatedStatus
        });

        // Verify subscription is now released
        var afterSubs = await _client.GetFromJsonAsync<SubscriptionDiscoveryResponse>(
            $"/api/channel-subscriptions?memberIdentity={memberIdentity}&includeInactive=true");
        Assert.NotNull(afterSubs);
        var afterSub = Assert.Single(afterSubs.Subscriptions);
        Assert.Equal("left", afterSub.SubscriptionStatus);
    }

    // -------------------------------------------------------------------------
    // Reactivating to active reactivates/creates subscription
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ReactivateMembership_CreatesNewSubscription()
    {
        var channel = await EnsureDefaultChannelAsync("sub-lifecycle-4");
        var memberIdentity = "sub-agent-4";

        // Create active membership
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity,
            wakePolicy = "direct_questions_only"
        });

        // Deactivate
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity,
            wakePolicy = "direct_questions_only",
            membershipStatus = "left"
        });

        // Reactivate
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity,
            wakePolicy = "direct_questions_only",
            membershipStatus = "active"
        });

        // Verify a new active subscription exists
        var subscriptions = await _client.GetFromJsonAsync<SubscriptionDiscoveryResponse>(
            $"/api/channel-subscriptions?memberIdentity={memberIdentity}&includeInactive=true");
        Assert.NotNull(subscriptions);
        var activeSubs = subscriptions.Subscriptions
            .Where(s => s.SubscriptionStatus == "active").ToList();
        Assert.Single(activeSubs);
        Assert.Equal("active", activeSubs[0].SubscriptionStatus);
    }

    // -------------------------------------------------------------------------
    // Non-agent membership creates no subscription
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NonAgentMembership_DoesNotCreateSubscription()
    {
        var memberIdentity = "human-user-1";

        // Create an ad_hoc channel without a project
        using var createResponse = await _client.PostAsJsonAsync("/api/channels", new
        {
            slug = "non-agent-channel",
            displayName = "Non-Agent Channel",
            kind = "ad_hoc",
            createdBy = "test"
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var channel = await createResponse.Content.ReadFromJsonAsync<ChannelStub>();
        Assert.NotNull(channel);

        // Upsert a non-agent (user) membership
        using var upsertResponse = await _client.PutAsJsonAsync($"/api/channels/{channel.Id}/memberships", new
        {
            memberType = "user",
            memberIdentity,
            wakePolicy = "all_messages_except_self",
            canSend = true,
            canReact = true,
            maxAutoRepliesPerWindow = 0
        });
        var body = await upsertResponse.Content.ReadAsStringAsync();
        Assert.True(upsertResponse.IsSuccessStatusCode, $"Expected success, got {upsertResponse.StatusCode}: {body}");

        // Verify no subscription was created
        var subscriptions = await _client.GetFromJsonAsync<SubscriptionDiscoveryResponse>(
            $"/api/channel-subscriptions?memberIdentity={memberIdentity}&includeInactive=true");
        Assert.NotNull(subscriptions);
        Assert.Empty(subscriptions.Subscriptions);
    }

    // -------------------------------------------------------------------------
    // Direct-agent event with active subscription stamps recorded_pending_claim
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DirectAgentEvent_WithActiveSubscription_StampsPendingClaim()
    {
        var channel = await EnsureDefaultChannelAsync("sub-lifecycle-6");
        var memberIdentity = "sub-agent-6";

        // Create membership (auto-creates subscription)
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity,
            wakePolicy = "direct_questions_only"
        });

        // Post direct agent event
        using var postResponse = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            channelId = channel.Id,
            memberIdentity,
            senderIdentity = "test-operator",
            body = "Test message with active subscription"
        });
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);
        var postPayload = await postResponse.Content.ReadFromJsonAsync<DirectAgentEventPayload>();
        Assert.NotNull(postPayload);

        // Readback should show recorded_pending_claim
        var readback = await _client.GetFromJsonAsync<DirectAgentEventReadbackPayload>(
            $"/api/direct-agent-events/{postPayload.EventId}");
        Assert.NotNull(readback);
        Assert.Equal("recorded_pending_claim", readback.DeliveryStatus);
    }

    // -------------------------------------------------------------------------
    // Direct-agent event with membership but no active subscription stamps recorded_pending_subscription
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DirectAgentEvent_WithoutActiveSubscription_StampsPendingSubscription()
    {
        var channel = await EnsureDefaultChannelAsync("sub-lifecycle-7");
        var memberIdentity = "sub-agent-7";

        // Create membership (auto-creates subscription)
        await UpsertMembershipAsync(channel.Id, new
        {
            memberType = "agent",
            memberIdentity,
            wakePolicy = "direct_questions_only"
        });

        // Release all subscriptions for this member
        var subscriptions = await _client.GetFromJsonAsync<SubscriptionDiscoveryResponse>(
            $"/api/channel-subscriptions?memberIdentity={memberIdentity}&includeInactive=true");
        Assert.NotNull(subscriptions);
        foreach (var sub in subscriptions.Subscriptions)
        {
            using var deleteResponse = await _client.DeleteAsync($"/api/channel-subscriptions/{sub.Id}");
            deleteResponse.EnsureSuccessStatusCode();
        }

        // Membership still exists but subscription is released
        // Post direct agent event
        using var postResponse = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            channelId = channel.Id,
            memberIdentity,
            senderIdentity = "test-operator",
            body = "Test message without active subscription"
        });
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);
        var postPayload = await postResponse.Content.ReadFromJsonAsync<DirectAgentEventPayload>();
        Assert.NotNull(postPayload);

        // Readback should show recorded_pending_subscription
        var readback = await _client.GetFromJsonAsync<DirectAgentEventReadbackPayload>(
            $"/api/direct-agent-events/{postPayload.EventId}");
        Assert.NotNull(readback);
        Assert.Equal("recorded_pending_subscription", readback.DeliveryStatus);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private async Task UpsertMembershipAsync(long channelId, object request)
    {
        using var response = await _client.PutAsJsonAsync($"/api/channels/{channelId}/memberships", request);
        response.EnsureSuccessStatusCode();
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

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
    }

    // ---- Local payload records ----

    private sealed record ChannelStub(long Id, string Slug, string Kind, string? ProjectId);

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
        string EvidenceSummary);

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
        string CreatedAt);

    private sealed record SubscriptionChannelDto(
        long Id,
        long ChannelId,
        long? MembershipId,
        string MemberType,
        string MemberIdentity,
        string SubscriptionIdentity,
        string SubscriptionPurpose,
        string SubscriptionStatus,
        string? ProfileIdentity,
        string? AgentInstanceId,
        string? PoolMemberId,
        string? SourceProjectId,
        string? TargetProjectId,
        long? TargetTaskId,
        string? AssignmentId,
        string? WorkerRunId,
        string? WorkerRole,
        string? SessionOwnerId,
        string? SessionId,
        string? WakePolicyOverride,
        string? LastSeenAt,
        string? LastClaimedAt,
        string? DegradedReason,
        string? SettingsJson,
        string CreatedAt,
        string UpdatedAt);

    private sealed record SubscriptionDiscoveryResponse(
        string? MemberIdentity,
        string? ProfileIdentity,
        List<SubscriptionChannelDto> Subscriptions);
}
