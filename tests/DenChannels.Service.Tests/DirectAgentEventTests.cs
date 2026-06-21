using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenChannels.Service.Tests;

/// <summary>
/// Tests for the legacy /api/direct-agent-events readback endpoint group.
/// The write route is retired; historical wake_event evidence remains readable.
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

    [Fact]
    public async Task PostDirectAgentEvent_Returns410Gone_Tombstone()
    {
        using var response = await _client.PostAsJsonAsync("/api/direct-agent-events", new
        {
            projectId = "dae-retired-proj",
            memberIdentity = "test-runner",
            senderIdentity = "operator",
            body = "Run the integration tests."
        });

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("route_gone", raw);
        Assert.Contains("POST /v1/delivery/intents", raw);
        Assert.Contains("GET /api/direct-agent-events/{eventId}", raw);
    }

    [Fact]
    public async Task GetDirectAgentEvent_ReturnsFullReadback()
    {
        var channel = await EnsureDefaultChannelAsync("dae-readback-proj");
        var message = await SeedWakeEventAsync(channel.Id, "readback-agent", new
        {
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
            sessionId = "session-143",
            wakePolicy = "direct_questions_only",
            deliveryStatus = "recorded_pending_claim"
        });

        var readback = await _client.GetFromJsonAsync<DirectAgentEventReadbackPayload>(
            $"/api/direct-agent-events/{message.Id}");

        Assert.NotNull(readback);
        Assert.Equal(message.Id, readback.EventId);
        Assert.Equal(channel.Id, readback.ChannelId);
        Assert.Equal("readback-agent", readback.MemberIdentity);
        Assert.Equal("direct_questions_only", readback.WakePolicy);
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
        Assert.Equal("recorded_pending_claim", readback.DeliveryStatus);
        Assert.Equal("unclaimed", readback.ClaimStatus);
        Assert.Equal("pending", readback.CompletionStatus);
        Assert.NotEmpty(readback.CreatedAt);
    }

    [Fact]
    public async Task ListDirectAgentEvents_ReturnsHistoricalWakeEvents()
    {
        var channel = await EnsureDefaultChannelAsync("dae-list-proj");
        var message = await SeedWakeEventAsync(channel.Id, "list-agent", new
        {
            senderIdentity = "operator",
            body = "List readback body.",
            sourceProjectId = "dae-list-proj",
            wakePolicy = "all_messages_except_self",
            deliveryStatus = "recorded_pending_subscription"
        });

        var listed = await _client.GetFromJsonAsync<DirectAgentEventListPayload>(
            $"/api/direct-agent-events?channelId={channel.Id}&afterId=0&limit=10");

        Assert.NotNull(listed);
        var item = Assert.Single(listed.Items);
        Assert.Equal(message.Id, item.Id);
        Assert.Equal(channel.Id, item.ChannelId);
        Assert.Equal("wake_event", item.SourceKind);
        Assert.Equal("List readback body.", item.Body);
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

    [Fact]
    public async Task GatewayDirectAgentMessages_Returns410Gone_DeliveryTombstone()
    {
        using var response = await _client.PostAsJsonAsync("/api/gateway/direct-agent-messages", new
        {
            projectId = "dae-gw-alias-proj",
            memberIdentity = "alias-agent",
            senderIdentity = "operator",
            body = "Gateway alias test should be gone."
        });

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("route_gone", raw);
        Assert.Contains("POST /v1/delivery/intents", raw);
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

    private async Task<MessageStub> SeedWakeEventAsync(long channelId, string memberIdentity, dynamic values)
    {
        string requestId = $"direct-agent-message:{channelId}:{Uri.EscapeDataString(memberIdentity)}:{Guid.NewGuid():N}";
        string? sourceProjectId = values.sourceProjectId;
        string? targetProjectId = HasMember(values, "targetProjectId") ? values.targetProjectId : null;
        long? targetTaskId = HasMember(values, "targetTaskId") ? Convert.ToInt64(values.targetTaskId) : null;
        string? assignmentId = HasMember(values, "assignmentId") ? values.assignmentId : null;
        string? workerRunId = HasMember(values, "workerRunId") ? values.workerRunId : null;
        string? workerRole = HasMember(values, "workerRole") ? values.workerRole : null;
        string? profileIdentity = HasMember(values, "profileIdentity") ? values.profileIdentity : null;
        string? poolMemberId = HasMember(values, "poolMemberId") ? values.poolMemberId : null;
        string? agentInstanceId = HasMember(values, "agentInstanceId") ? values.agentInstanceId : null;
        string? sessionOwnerId = HasMember(values, "sessionOwnerId") ? values.sessionOwnerId : null;
        string? sessionId = HasMember(values, "sessionId") ? values.sessionId : null;
        string wakePolicy = values.wakePolicy;
        string deliveryStatus = values.deliveryStatus;

        var metadataJson = JsonSerializer.Serialize(new
        {
            requestId,
            targetMemberIdentity = memberIdentity,
            targetMemberType = "agent",
            wakePolicy,
            deliveryMode = "direct_agent_message",
            deliveryStatus,
            claimStatus = "unclaimed",
            completionStatus = "pending",
            suppressionStatus = "not_suppressed",
            evidence = new { gatewayEventsUrl = $"/api/direct-agent-events?channelId={channelId}&afterId=0&limit=50" },
            sourceProjectId,
            targetProjectId,
            targetTaskId,
            assignmentId,
            workerRunId,
            workerRole,
            profileIdentity,
            poolMemberId,
            agentInstanceId,
            sessionOwnerId,
            sessionId
        });

        return await PostMessageAsync(channelId, new
        {
            senderType = "user",
            senderIdentity = values.senderIdentity,
            body = values.body,
            messageKind = "human_text",
            sourceKind = "wake_event",
            sourceId = requestId,
            sourceProjectId,
            targetProjectId,
            targetTaskId,
            assignmentId,
            workerRunId,
            workerRole,
            profileIdentity,
            poolMemberId,
            agentInstanceId,
            sessionOwnerId,
            sessionId,
            summary = $"Direct agent request to {memberIdentity}: recorded, pending claim/completion",
            metadataJson
        });
    }

    private static bool HasMember(object value, string memberName) =>
        value.GetType().GetProperty(memberName) is not null;

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

    private sealed record ChannelStub(long Id, string Slug, string Kind, string? ProjectId);
    private sealed record MessageStub(long Id, long ChannelId, string Body);
    private sealed record DirectAgentEventListPayload(
        IReadOnlyList<DirectAgentEventListItemPayload> Items,
        long? NextAfterId,
        bool HasMore);
    private sealed record DirectAgentEventListItemPayload(long Id, long ChannelId, string? SourceKind, string Body);
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
}
