using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenChannels.Service.Tests;

public sealed class ChannelEventStreamTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"den-channels-sse-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public ChannelEventStreamTests()
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
    }

    [Fact]
    public async Task EventStream_OnceMode_ReplaysMessagesAndActivityWithEnvelopeAndCursor()
    {
        using var client = _factory.CreateClient();
        var channel = await CreateChannelAsync(client, "stream-contract");

        await client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages", new
        {
            senderType = "agent",
            senderIdentity = "den-mcp-runner",
            body = "worker posted a channel update",
            messageKind = "agent_text",
            sourceKind = "task_message",
            sourceId = "13480",
            targetProjectId = "den-web",
            targetTaskId = 2140
        });

        await client.PostAsJsonAsync($"/api/channels/{channel.Id}/activity-events", new
        {
            agentIdentity = "den-mcp-runner",
            eventType = "tool_call_completed",
            status = "completed",
            deliveryStage = "observability",
            title = "Tool call completed",
            summary = "A breadcrumb that should stream without becoming chat"
        });

        using var response = await client.GetAsync(
            $"/api/channels/{channel.Id}/events/stream?once=true&afterMessageId=0&afterActivityId=0&heartbeatSeconds=1&pollIntervalMs=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentType);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: stream_open", body);
        Assert.Contains("event: channel_message", body);
        Assert.Contains("event: channel_activity_event", body);
        Assert.Contains("id: messages=", body);
        Assert.Contains(";activity=", body);
        Assert.Contains("\"type\":\"channel_message\"", body);
        Assert.Contains("\"type\":\"channel_activity_event\"", body);
        Assert.Contains("\"message\":", body);
        Assert.Contains("\"activityEvent\":", body);
        Assert.Contains("worker posted a channel update", body);
        Assert.Contains("A breadcrumb that should stream without becoming chat", body);
        Assert.Contains("\"fallbackPollEndpoints\":", body);
        Assert.Contains($"/api/channels/{channel.Id}/messages", body);
        Assert.Contains($"/api/channels/{channel.Id}/activity-events", body);
    }

    [Fact]
    public async Task EventStream_Reconnect_UsesLastEventIdCursorWithoutReplayingOldMessages()
    {
        using var client = _factory.CreateClient();
        var channel = await CreateChannelAsync(client, "stream-reconnect");

        await client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages", new
        {
            senderType = "user",
            senderIdentity = "patch",
            body = "first message before cursor"
        });

        var initialBody = await GetStreamBodyAsync(client,
            $"/api/channels/{channel.Id}/events/stream?once=true&afterMessageId=0&afterActivityId=0&pollIntervalMs=10");
        var cursor = ExtractLastEventId(initialBody);
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        await client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages", new
        {
            senderType = "user",
            senderIdentity = "patch",
            body = "second message after cursor"
        });

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/channels/{channel.Id}/events/stream?once=true&pollIntervalMs=10");
        request.Headers.TryAddWithoutValidation("Last-Event-ID", cursor);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reconnectBody = await response.Content.ReadAsStringAsync();

        Assert.Contains("second message after cursor", reconnectBody);
        Assert.DoesNotContain("first message before cursor", reconnectBody);
    }

    [Fact]
    public async Task EventStream_Reconnect_DoesNotSkipActivityEventsWhenSequenceDiffersFromIdOrder()
    {
        using var client = _factory.CreateClient();
        var channel = await CreateChannelAsync(client, "stream-activity-cursor");

        await client.PostAsJsonAsync($"/api/channels/{channel.Id}/activity-events", new
        {
            agentIdentity = "sequence-agent",
            eventType = "tool_call_started",
            status = "started",
            deliveryStage = "observability",
            sequence = 100,
            summary = "first id high sequence"
        });
        await client.PostAsJsonAsync($"/api/channels/{channel.Id}/activity-events", new
        {
            agentIdentity = "sequence-agent",
            eventType = "tool_call_completed",
            status = "completed",
            deliveryStage = "observability",
            sequence = 0,
            summary = "second id low sequence"
        });

        var firstWindow = await GetStreamBodyAsync(client,
            $"/api/channels/{channel.Id}/events/stream?once=true&afterMessageId=0&afterActivityId=0&replayLimit=1&pollIntervalMs=10");
        Assert.Contains("first id high sequence", firstWindow);
        Assert.DoesNotContain("second id low sequence", firstWindow);
        var cursor = ExtractLastEventId(firstWindow);

        using var reconnectRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/channels/{channel.Id}/events/stream?once=true&replayLimit=1&pollIntervalMs=10");
        reconnectRequest.Headers.TryAddWithoutValidation("Last-Event-ID", cursor);
        using var reconnectResponse = await client.SendAsync(reconnectRequest);
        Assert.Equal(HttpStatusCode.OK, reconnectResponse.StatusCode);
        var secondWindow = await reconnectResponse.Content.ReadAsStringAsync();

        Assert.Contains("second id low sequence", secondWindow);
        Assert.DoesNotContain("first id high sequence", secondWindow);
    }

    [Fact]
    public async Task ActivityEventsPollingCursor_DoesNotSkipWhenSequenceDiffersFromIdOrder()
    {
        using var client = _factory.CreateClient();
        var channel = await CreateChannelAsync(client, "activity-polling-cursor");

        await client.PostAsJsonAsync($"/api/channels/{channel.Id}/activity-events", new
        {
            agentIdentity = "polling-agent",
            eventType = "tool_call_started",
            status = "started",
            deliveryStage = "observability",
            sequence = 100,
            summary = "poll first id high sequence"
        });
        await client.PostAsJsonAsync($"/api/channels/{channel.Id}/activity-events", new
        {
            agentIdentity = "polling-agent",
            eventType = "tool_call_completed",
            status = "completed",
            deliveryStage = "observability",
            sequence = 0,
            summary = "poll second id low sequence"
        });

        var firstPage = await client.GetStringAsync($"/api/channels/{channel.Id}/activity-events?afterId=0&limit=1");
        using var firstDoc = JsonDocument.Parse(firstPage);
        var firstItem = Assert.Single(firstDoc.RootElement.EnumerateArray());
        Assert.Equal("poll first id high sequence", firstItem.GetProperty("summary").GetString());
        var firstId = firstItem.GetProperty("id").GetInt64();

        var secondPage = await client.GetStringAsync($"/api/channels/{channel.Id}/activity-events?afterId={firstId}&limit=1");
        using var secondDoc = JsonDocument.Parse(secondPage);
        var secondItem = Assert.Single(secondDoc.RootElement.EnumerateArray());
        Assert.Equal("poll second id low sequence", secondItem.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task EventStream_ContinuousStream_BoundsInitialHistoricalReplay()
    {
        using var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        var channel = await CreateChannelAsync(client, "stream-bounded-replay");

        await PostMessageAsync(client, channel.Id, "bounded replay first");
        await PostMessageAsync(client, channel.Id, "bounded replay second");

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/channels/{channel.Id}/events/stream?afterMessageId=0&afterActivityId=0&replayLimit=1&heartbeatSeconds=2&pollIntervalMs=100");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        var openBlock = await ReadEventBlockWithTimeoutAsync(reader, TimeSpan.FromSeconds(1));
        Assert.Contains(openBlock, line => line == "event: stream_open");

        var firstReplayBlock = await ReadEventBlockWithTimeoutAsync(reader, TimeSpan.FromSeconds(1));
        Assert.Contains(firstReplayBlock, line => line == "event: channel_message");
        Assert.Contains(firstReplayBlock, line => line.Contains("bounded replay first", StringComparison.Ordinal));
        Assert.DoesNotContain(firstReplayBlock, line => line.Contains("bounded replay second", StringComparison.Ordinal));

        await PostMessageAsync(client, channel.Id, "bounded replay live message");
        var liveBlock = await ReadEventBlockWithTimeoutAsync(reader, TimeSpan.FromSeconds(1));
        Assert.Contains(liveBlock, line => line == "event: channel_message");
        Assert.Contains(liveBlock, line => line.Contains("bounded replay live message", StringComparison.Ordinal));
        Assert.DoesNotContain(liveBlock, line => line.Contains("bounded replay second", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EventStream_IdleContinuousStream_DoesNotHeartbeatBeforeInterval()
    {
        using var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        var channel = await CreateChannelAsync(client, "stream-idle-heartbeat");

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/channels/{channel.Id}/events/stream?heartbeatSeconds=2&pollIntervalMs=100");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        string? line;
        do
        {
            line = await ReadLineWithTimeoutAsync(reader, TimeSpan.FromSeconds(1));
            Assert.NotNull(line);
        } while (line!.Length != 0);

        var earlyHeartbeat = await ReadLineWithTimeoutAsync(reader, TimeSpan.FromMilliseconds(600));
        Assert.Null(earlyHeartbeat);
    }

    [Fact]
    public async Task EventStream_EmptyChannel_WritesHeartbeatAndFallbackContract()
    {
        using var client = _factory.CreateClient();
        var channel = await CreateChannelAsync(client, "stream-heartbeat");

        using var response = await client.GetAsync(
            $"/api/channels/{channel.Id}/events/stream?once=true&afterMessageId=0&afterActivityId=0&heartbeatSeconds=1&pollIntervalMs=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: stream_open", body);
        Assert.Contains(": keepalive", body);
        Assert.Contains("\"supportedEventTypes\":[\"channel_message\",\"channel_activity_event\"]", body);
        Assert.Contains("\"fallbackPollEndpoints\":", body);
        Assert.DoesNotContain("event: channel_message", body);
        Assert.DoesNotContain("event: channel_activity_event", body);
    }

    private static async Task<ChannelPayload> CreateChannelAsync(HttpClient client, string slug)
    {
        using var response = await client.PostAsJsonAsync("/api/channels", new
        {
            slug,
            displayName = slug,
            kind = "ad_hoc",
            createdBy = "test"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var channel = await response.Content.ReadFromJsonAsync<ChannelPayload>();
        Assert.NotNull(channel);
        return channel;
    }

    private static async Task PostMessageAsync(HttpClient client, long channelId, string body)
    {
        using var response = await client.PostAsJsonAsync($"/api/channels/{channelId}/messages", new
        {
            senderType = "user",
            senderIdentity = "patch",
            body
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<string> GetStreamBodyAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static string ExtractLastEventId(string streamBody)
    {
        return streamBody
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("id: ", StringComparison.Ordinal))
            .Select(line => line[4..])
            .LastOrDefault() ?? string.Empty;
    }

    private static async Task<IReadOnlyList<string>> ReadEventBlockWithTimeoutAsync(StreamReader reader, TimeSpan lineTimeout)
    {
        var lines = new List<string>();
        while (true)
        {
            var line = await ReadLineWithTimeoutAsync(reader, lineTimeout);
            Assert.NotNull(line);
            lines.Add(line!);
            if (line!.Length == 0)
                return lines;
        }
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            return await reader.ReadLineAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
    }

    private sealed record ChannelPayload(long Id, string Slug, string DisplayName, string Kind);
}
