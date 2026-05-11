using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenChannels.Service.Tests;

public sealed class ProjectChannelSyncTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"den-channels-sync-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public ProjectChannelSyncTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenChannels:Database:Path"] = _databasePath,
                    ["DenChannels:Database:ApplyMigrationsOnStartup"] = "true",
                    ["DenChannels:DenCore:UseStubProjectMetadata"] = "true",
                    ["DenChannels:DenCore:StubProjects:0:Id"] = "den-channels",
                    ["DenChannels:DenCore:StubProjects:0:Name"] = "Den Channels",
                    ["DenChannels:DenCore:StubProjects:1:Id"] = "den-mcp",
                    ["DenChannels:DenCore:StubProjects:1:Name"] = "Den MCP"
                });
            }));
    }

    [Fact]
    public async Task SyncFromConfiguredStubProjects_BackfillsKnownProjects()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/project-channel-sync", new { });
        response.EnsureSuccessStatusCode();
        var channels = await response.Content.ReadFromJsonAsync<List<ChannelPayload>>();

        Assert.NotNull(channels);
        Assert.Collection(channels.OrderBy(channel => channel.ProjectId),
            channel =>
            {
                Assert.Equal("den-channels", channel.ProjectId);
                Assert.Equal("project-den-channels", channel.Slug);
                Assert.Equal("Den Channels", channel.DisplayName);
            },
            channel =>
            {
                Assert.Equal("den-mcp", channel.ProjectId);
                Assert.Equal("project-den-mcp", channel.Slug);
                Assert.Equal("Den MCP", channel.DisplayName);
            });
    }

    [Fact]
    public async Task EnsureSingleProjectFromStub_IsIdempotentAndMockable()
    {
        using var client = _factory.CreateClient();

        var first = await PutJsonAsync<ChannelPayload>(client, "/api/project-channel-sync/projects/den-channels", new { });
        var second = await PutJsonAsync<ChannelPayload>(client, "/api/project-channel-sync/projects/den-channels", new { });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("project-den-channels", first.Slug);
        Assert.Equal("Den Channels", first.DisplayName);
    }

    [Fact]
    public async Task ExplicitProjectPayloadBackfill_DoesNotRequireDenCoreAvailability()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/project-channel-sync", new
        {
            projects = new[]
            {
                new { id = "custom-project", name = "Custom Project" }
            }
        });
        response.EnsureSuccessStatusCode();
        var channels = await response.Content.ReadFromJsonAsync<List<ChannelPayload>>();

        Assert.NotNull(channels);
        var channel = Assert.Single(channels);
        Assert.Equal("custom-project", channel.ProjectId);
        Assert.Equal("project-custom-project", channel.Slug);
        Assert.Equal("Custom Project", channel.DisplayName);
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
    }

    private static async Task<T> PutJsonAsync<T>(HttpClient client, string url, object request)
    {
        using var response = await client.PutAsJsonAsync(url, request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(payload);
        return payload;
    }

    private sealed record ChannelPayload(long Id, string Slug, string DisplayName, string Kind, string? ProjectId);
}
