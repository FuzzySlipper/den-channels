using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenChannels.Service.Tests;

public sealed class MirrorSummaryIngestionTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"den-channels-mirror-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public MirrorSummaryIngestionTests()
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
                    ["DenChannels:DenCore:StubProjects:0:Name"] = "Den Channels"
                });
            }));
    }

    [Fact]
    public async Task Ingest_CreatesMirrorSummaryWithSourcePointerAndDeepLink()
    {
        using var client = _factory.CreateClient();

        var result = await PostIngestAsync(client, new
        {
            events = new[]
            {
                new
                {
                    eventType = "task_done",
                    projectId = "den-channels",
                    sourceKind = "task_message",
                    sourceId = "5680",
                    summaryHint = "Runner finished task #1320: skeleton ready; tests passed.",
                    deepLink = "den://project/den-channels/task/1320",
                    actor = "den-channels-runner",
                    severity = "normal",
                    dedupeKey = "task:1320:done",
                    metadata = new Dictionary<string, object?> { ["task_id"] = 1320 }
                }
            }
        });

        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(0, result.Suppressed);
        var message = Assert.Single(result.Messages);
        Assert.Equal("mirror_summary", message.MessageKind);
        Assert.Equal("task_message", message.SourceKind);
        Assert.Equal("5680", message.SourceId);
        Assert.Equal("den://project/den-channels/task/1320", message.DeepLink);
        Assert.Equal("task:1320:done", message.DedupeKey);

        var messages = await client.GetFromJsonAsync<List<MessagePayload>>($"/api/channels/{message.ChannelId}/messages");
        Assert.NotNull(messages);
        Assert.Single(messages);
    }

    [Fact]
    public async Task Ingest_DedupesRetries()
    {
        using var client = _factory.CreateClient();
        var payload = new
        {
            events = new[]
            {
                new
                {
                    eventType = "review_requested",
                    projectId = "den-channels",
                    sourceKind = "review_round",
                    sourceId = "42",
                    summaryHint = "Review requested for task #1322.",
                    deepLink = "den://review/42",
                    dedupeKey = "review:42:requested"
                }
            }
        };

        var first = await PostIngestAsync(client, payload);
        var second = await PostIngestAsync(client, payload);

        Assert.Equal(1, first.Created);
        Assert.Equal(0, first.Duplicates);
        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.Duplicates);
        Assert.Equal(first.Messages[0].Id, second.Messages[0].Id);
    }

    [Fact]
    public async Task Ingest_SuppressesDebugStreamEvents()
    {
        using var client = _factory.CreateClient();

        var result = await PostIngestAsync(client, new
        {
            events = new[]
            {
                new
                {
                    eventType = "subagent_work_started",
                    streamKind = "debug",
                    projectId = "den-channels",
                    sourceKind = "agent_stream_entry",
                    sourceId = "123",
                    summaryHint = "Noisy debug event",
                    dedupeKey = "debug:123"
                }
            }
        });

        Assert.Equal(0, result.Created);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(1, result.Suppressed);
        Assert.Empty(result.Messages);
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
    }

    private static async Task<MirrorIngestPayload> PostIngestAsync(HttpClient client, object payload)
    {
        using var response = await client.PostAsJsonAsync("/api/mirror-summaries/ingest", payload);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<MirrorIngestPayload>();
        Assert.NotNull(result);
        return result;
    }

    private sealed record MirrorIngestPayload(int Created, int Duplicates, int Suppressed, List<MessagePayload> Messages);

    private sealed record MessagePayload(long Id, long ChannelId, string MessageKind, string? SourceKind, string? SourceId,
        string? DeepLink, string? DedupeKey);
}
