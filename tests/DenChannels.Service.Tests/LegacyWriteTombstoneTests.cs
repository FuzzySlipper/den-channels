using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenChannels.Service.Tests;

public sealed class LegacyWriteTombstoneTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"den-channels-legacy-tombstone-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public LegacyWriteTombstoneTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenChannels:Database:Path"] = _databasePath,
                    ["DenChannels:Database:ApplyMigrationsOnStartup"] = "true",
                    ["DenChannels:LegacyWrites:TombstoneChannelMessages"] = "true",
                    ["DenChannels:LegacyWrites:TombstoneGatewaySystemMessages"] = "true",
                    ["DenChannels:LegacyObservation:TombstoneRoutes"] = "true",
                    ["DenChannels:LegacyRuntimeControl:TombstoneUnusedRoutes"] = "true"
                });
            }));
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task PostChannelMessage_Returns410Gone_WhenLegacyWritesAreTombstoned()
    {
        using var response = await _client.PostAsJsonAsync("/api/channels/42/messages", new
        {
            senderType = "agent",
            senderIdentity = "legacy-test",
            body = "should not write",
            messageKind = "agent_text",
            sourceKind = "legacy_write_tombstone_test",
            dedupeKey = "legacy-tombstone-channel-message"
        });

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        await AssertTombstone(response, "POST /v1/conversation/channels/{channel_id}/messages");
    }

    [Fact]
    public async Task GatewaySystemMessage_Returns410Gone_WhenLegacyWritesAreTombstoned()
    {
        using var response = await _client.PostAsJsonAsync("/api/gateway/system-messages", new
        {
            projectId = "den-channels",
            body = "should not write",
            messageKind = "system_event",
            dedupeKey = "legacy-tombstone-gateway-system-message"
        });

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        await AssertTombstone(response, "POST /v1/conversation/channels/{channel_id}/messages");
    }

    [Fact]
    public async Task GatewayHealth_DoesNotAdvertiseSystemMessageWrite_WhenTombstoned()
    {
        var payload = await _client.GetFromJsonAsync<GatewayHealthPayload>("/api/gateway/health");

        Assert.NotNull(payload);
        Assert.DoesNotContain(payload.Endpoints, endpoint => endpoint.Contains("/api/gateway/system-messages"));
        Assert.Contains(payload.Endpoints, endpoint => endpoint.Contains("/api/gateway/messages"));
    }

    [Theory]
    [InlineData("POST", "/api/channels/42/activity-events", "POST /v1/observation/activity-events")]
    [InlineData("GET", "/api/channels/42/activity-events?limit=1", "GET /v1/observation/activity-events")]
    [InlineData("PATCH", "/api/channel-activity-events/123", "POST /v1/observation/activity-events")]
    [InlineData("POST", "/api/channel-activity-events", "POST /v1/observation/activity-events")]
    [InlineData("GET", "/api/channel-activity-events/status", "GET /v1/observation/activity-events/status")]
    [InlineData("POST", "/api/agent-work/lifecycle-events", "POST /v1/observation/lifecycle-events")]
    [InlineData("GET", "/api/agent-work/events?channelId=42", "GET /v1/observation/activity-events")]
    [InlineData("GET", "/api/agent-work/current?channelId=42", "GET /v1/observation/active-work")]
    [InlineData("GET", "/api/agents/overview", "GET /v1/observation/agents/overview")]
    [InlineData("GET", "/api/agents/den-mcp-runner/overview", "GET /v1/observation/agents/{id}/overview")]
    [InlineData("GET", "/api/assignments/asn-1/trace?projectId=den-services", "GET /v1/observation/assignments/{id}/trace")]
    [InlineData("GET", "/api/gateway/assignments/asn-1/trace?projectId=den-services", "GET /v1/observation/assignments/{id}/trace")]
    [InlineData("GET", "/api/assignments/asn-1/transcript?channelId=42", "GET /v1/observation/assignments/{id}/transcript")]
    public async Task LegacyObservationRoutes_Return410Gone_WhenTombstoned(string method, string path, string replacement)
    {
        using var response = await SendAsync(method, path);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        await AssertTombstone(response, replacement);
    }

    [Theory]
    [InlineData("PUT", "/api/agent-commons/memberships/legacy-worker", "PUT /api/channels/{channelId}/memberships")]
    [InlineData("POST", "/api/agent-commons/brake", "Den Core/Runtime agent-control policy")]
    [InlineData("PUT", "/api/worker-pool/lobby", "Den Core/Runtime worker-pool member registration")]
    [InlineData("PUT", "/api/worker-pool/lobby/presence", "Den Core/Runtime worker-pool member heartbeat")]
    [InlineData("POST", "/api/worker-pool/lobby/presence/legacy-worker/acknowledge-release", "Den Core/Runtime worker-pool assignment release")]
    [InlineData("GET", "/api/worker-pool/lobby/presence/by-instance?agentInstanceId=inst-1", "Den Core/Runtime child-run projection")]
    [InlineData("POST", "/api/worker-pool/lobby/presence/release-child-run?memberIdentity=legacy-worker", "Den Core/Runtime worker-pool assignment release")]
    [InlineData("GET", "/api/agents/legacy-worker/child-runs", "Den Core/Runtime child-run projection")]
    public async Task UnusedRuntimeControlRoutes_Return410Gone_WhenTombstoned(
        string method,
        string path,
        string replacement)
    {
        using var response = await SendAsync(method, path);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        await AssertTombstone(response, replacement);
    }

    [Theory]
    [InlineData("PUT", "/api/agent-commons")]
    [InlineData("GET", "/api/worker-pool/lobby/presence")]
    [InlineData("PUT", "/api/worker-pool/control/membership?agentIdentity=legacy-worker")]
    [InlineData("GET", "/api/channel-subscriptions?memberIdentity=legacy-worker")]
    [InlineData("GET", "/api/active-work/routes?targetProjectId=den-channels")]
    public async Task LiveRuntimeCompatibilityRoutes_RemainAvailable_WhenUnusedRuntimeControlIsTombstoned(
        string method,
        string path)
    {
        using var response = await SendAsync(method, path);

        Assert.NotEqual(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task GatewayHealth_DoesNotAdvertiseObservationRoutes_WhenTombstoned()
    {
        var payload = await _client.GetFromJsonAsync<GatewayHealthPayload>("/api/gateway/health");

        Assert.NotNull(payload);
        Assert.DoesNotContain(payload.Endpoints, endpoint => endpoint.Contains("/api/gateway/assignments"));
        Assert.DoesNotContain(payload.Endpoints, endpoint => endpoint.Contains("/api/channel-activity-events"));
        Assert.DoesNotContain(payload.Endpoints, endpoint => endpoint.Contains("/activity-events"));
    }

    private async Task<HttpResponseMessage> SendAsync(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PATCH" or "PUT")
        {
            request.Content = JsonContent.Create(new
            {
                channelId = 42,
                agentIdentity = "legacy-observation-test",
                eventType = "heartbeat",
                status = "interim",
                title = "retired legacy observation route",
                summary = "should not write",
                memberIdentity = "legacy-worker",
                subscriptionIdentity = "member:legacy-worker:ordinary_channel"
            });
        }

        return await _client.SendAsync(request);
    }

    private static async Task AssertTombstone(HttpResponseMessage response, string expectedReplacement)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.NotEmpty(raw);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal("route_gone", root.GetProperty("code").GetString());
        Assert.Contains("retired", root.GetProperty("message").GetString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedReplacement, root.GetProperty("replacement").GetString());
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
    }

    private sealed record GatewayHealthPayload(string Service, string Status, string[] Endpoints);
}
