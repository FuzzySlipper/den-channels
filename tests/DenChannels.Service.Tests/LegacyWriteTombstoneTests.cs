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
                    ["DenChannels:LegacyWrites:TombstoneGatewaySystemMessages"] = "true"
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
