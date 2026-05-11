using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenChannels.Service.Tests;

public sealed class ChannelApiTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"den-channels-api-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public ChannelApiTests()
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
    public async Task CreateListAndGetChannel_Works()
    {
        using var client = _factory.CreateClient();

        using var createResponse = await client.PostAsJsonAsync("/api/channels", new
        {
            slug = "ops-room",
            displayName = "Ops Room",
            kind = "ad_hoc",
            createdBy = "test"
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ChannelPayload>();
        Assert.NotNull(created);
        Assert.Equal("ops-room", created.Slug);

        var listed = await client.GetFromJsonAsync<List<ChannelPayload>>("/api/channels?kind=ad_hoc");
        Assert.NotNull(listed);
        var listedChannel = Assert.Single(listed);
        Assert.Equal(created.Id, listedChannel.Id);

        var fetched = await client.GetFromJsonAsync<ChannelPayload>($"/api/channels/{created.Id}");
        Assert.NotNull(fetched);
        Assert.Equal("Ops Room", fetched.DisplayName);
    }

    [Fact]
    public async Task EnsureProjectDefaultChannel_IsIdempotentAndUsesSafeSlug()
    {
        using var client = _factory.CreateClient();

        var first = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels",
            createdBy = "test"
        });
        var second = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels",
            createdBy = "test"
        });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("project-den-channels", first.Slug);
        Assert.Equal("project_default", first.Kind);
        Assert.Equal("den-channels", first.ProjectId);

        var projectChannels = await client.GetFromJsonAsync<List<ChannelPayload>>("/api/channels?projectId=den-channels&kind=project_default");
        Assert.NotNull(projectChannels);
        Assert.Single(projectChannels);
    }

    [Fact]
    public async Task PostAndListMessages_SupportsSourcePointersAndCursor()
    {
        using var client = _factory.CreateClient();
        var channel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels"
        });

        using var postResponse = await client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages", new
        {
            senderType = "system",
            senderIdentity = "den-router",
            body = "Task #1320 completed. Open task for details.",
            messageKind = "mirror_summary",
            sourceKind = "task_message",
            sourceId = "5680",
            sourceProjectId = "den-channels",
            summary = "Task #1320 completed",
            deepLink = "den://project/den-channels/task/1320",
            metadataJson = "{\"task_id\":1320}",
            dedupeKey = "task-message:5680"
        });
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);
        var posted = await postResponse.Content.ReadFromJsonAsync<MessagePayload>();
        Assert.NotNull(posted);
        Assert.Equal("task_message", posted.SourceKind);
        Assert.Equal("den://project/den-channels/task/1320", posted.DeepLink);

        var messages = await client.GetFromJsonAsync<List<MessagePayload>>($"/api/channels/{channel.Id}/messages?afterId=0&limit=10");
        Assert.NotNull(messages);
        var listedMessage = Assert.Single(messages);
        Assert.Equal(posted.Id, listedMessage.Id);
        Assert.Equal("task-message:5680", listedMessage.DedupeKey);

        using var duplicateResponse = await client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages", new
        {
            senderType = "system",
            senderIdentity = "den-router",
            body = "Duplicate",
            dedupeKey = "task-message:5680"
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
    }

    [Fact]
    public async Task MembershipAndReactionEndpoints_Work()
    {
        using var client = _factory.CreateClient();
        var channel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels"
        });

        var membership = await PutJsonAsync<MembershipPayload>(client, $"/api/channels/{channel.Id}/memberships", new
        {
            memberType = "agent",
            memberIdentity = "den-channels-runner",
            wakePolicy = "mentions_only"
        });
        Assert.Equal("agent", membership.MemberType);
        Assert.Equal("mentions_only", membership.WakePolicy);

        var message = await PostJsonAsync<MessagePayload>(client, $"/api/channels/{channel.Id}/messages", new
        {
            senderType = "user",
            senderIdentity = "patch",
            body = "Thanks"
        });
        var reaction = await PostJsonAsync<ReactionPayload>(client, $"/api/channel-messages/{message.Id}/reactions", new
        {
            reactorType = "agent",
            reactorIdentity = "den-channels-runner",
            reactionKey = "acknowledged"
        });
        Assert.Equal(message.Id, reaction.ChannelMessageId);
        Assert.Equal("acknowledged", reaction.ReactionKey);
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

    private static async Task<T> PostJsonAsync<T>(HttpClient client, string url, object request)
    {
        using var response = await client.PostAsJsonAsync(url, request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(payload);
        return payload;
    }

    private sealed record ChannelPayload(long Id, string Slug, string DisplayName, string Kind, string? ProjectId);

    private sealed record MessagePayload(long Id, long ChannelId, string Body, string? SourceKind, string? DeepLink,
        string? DedupeKey);

    private sealed record MembershipPayload(long Id, long ChannelId, string MemberType, string MemberIdentity,
        string WakePolicy);

    private sealed record ReactionPayload(long Id, long ChannelMessageId, string ReactionKey);
}
