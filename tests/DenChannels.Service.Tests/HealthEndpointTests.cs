using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DenChannels.Service.Tests;

public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LiveHealth_ReturnsOkServiceStatus()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/live");
        var payload = await response.Content.ReadFromJsonAsync<HealthPayload>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal("den-channels", payload.Service);
        Assert.Equal("ok", payload.Status);
        Assert.Equal("running", payload.Checks["process"]);
    }

    [Fact]
    public async Task ReadyHealth_ReturnsConfiguredDenCoreBoundary()
    {
        using var client = _factory.CreateClient();

        var payload = await client.GetFromJsonAsync<HealthPayload>("/health/ready");

        Assert.NotNull(payload);
        Assert.Equal("ready", payload.Status);
        Assert.EndsWith(".db", payload.Checks["databasePath"]);
        Assert.Equal("http://127.0.0.1:5199", payload.Checks["denCoreBaseUrl"]);
    }

    private sealed record HealthPayload(
        string Service,
        string Status,
        Dictionary<string, string> Checks);
}
