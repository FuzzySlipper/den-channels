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
    public async Task ListMessages_WithoutCursorReturnsLatestWindowInAscendingOrder()
    {
        using var client = _factory.CreateClient();
        var channel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels"
        });

        for (var index = 1; index <= 85; index++)
        {
            await PostJsonAsync<MessagePayload>(client, $"/api/channels/{channel.Id}/messages", new
            {
                senderType = "user",
                senderIdentity = "patch",
                body = $"message {index:000}"
            });
        }

        var messages = await client.GetFromJsonAsync<List<MessagePayload>>($"/api/channels/{channel.Id}/messages?limit=80");
        Assert.NotNull(messages);
        Assert.Equal(80, messages.Count);
        Assert.Equal("message 006", messages[0].Body);
        Assert.Equal("message 085", messages[^1].Body);
        Assert.DoesNotContain(messages, message => message.Body == "message 001");

        var afterCursor = await client.GetFromJsonAsync<List<MessagePayload>>($"/api/channels/{channel.Id}/messages?afterId={messages[^2].Id}&limit=10");
        Assert.NotNull(afterCursor);
        var cursorMessage = Assert.Single(afterCursor);
        Assert.Equal("message 085", cursorMessage.Body);
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
            reactionKey = "✅"
        });
        var duplicate = await PostJsonAsync<ReactionPayload>(client, $"/api/channel-messages/{message.Id}/reactions", new
        {
            reactorType = "agent",
            reactorIdentity = "den-channels-runner",
            reactionKey = "✅"
        });
        await PostJsonAsync<ReactionPayload>(client, $"/api/channel-messages/{message.Id}/reactions", new
        {
            reactorType = "user",
            reactorIdentity = "patch",
            reactionKey = "✅"
        });
        Assert.Equal(message.Id, reaction.ChannelMessageId);
        Assert.Equal("✅", reaction.ReactionKey);
        Assert.Equal(reaction.Id, duplicate.Id);

        var summaries = await client.GetFromJsonAsync<List<ReactionSummaryPayload>>($"/api/channels/{channel.Id}/reactions");
        Assert.NotNull(summaries);
        var summary = Assert.Single(summaries);
        Assert.Equal(message.Id, summary.ChannelMessageId);
        Assert.Equal("✅", summary.ReactionKey);
        Assert.Equal(2, summary.Count);
        Assert.Contains("agent:den-channels-runner", summary.Reactors);
        Assert.Contains("user:patch", summary.Reactors);
    }

    [Fact]
    public async Task AgentCommons_IsVisibleAndAutoIncludesActiveAgentsWithMentionsOnlyWakePolicy()
    {
        using var client = _factory.CreateClient();
        var projectChannel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels"
        });

        var agent = await PutJsonAsync<MembershipPayload>(client, $"/api/channels/{projectChannel.Id}/memberships", new
        {
            memberType = "agent",
            memberIdentity = "den-channels-runner",
            wakePolicy = "all_human_messages"
        });
        Assert.Equal("all_human_messages", agent.WakePolicy);

        var channels = await client.GetFromJsonAsync<List<ChannelPayload>>("/api/channels?limit=100");
        Assert.NotNull(channels);
        var commons = Assert.Single(channels, channel => channel.Slug == "agent-commons");
        Assert.Equal("Agent Commons", commons.DisplayName);
        Assert.Equal("system", commons.Kind);
        Assert.Null(commons.ProjectId);

        var memberships = await client.GetFromJsonAsync<GatewayMembershipsPayload>(
            $"/api/gateway/memberships?channelId={commons.Id}");
        Assert.NotNull(memberships);
        var commonsAgent = Assert.Single(memberships.Members, member => member.MemberIdentity == "den-channels-runner");
        Assert.Equal("agent", commonsAgent.MemberType);
        Assert.Equal("active", commonsAgent.MembershipStatus);
        Assert.Equal("mentions_only", commonsAgent.WakePolicy);
    }

    [Fact]
    public async Task ActivityEventEndpoints_AppendUpdateQueryWithoutCreatingMessages()
    {
        using var client = _factory.CreateClient();
        var channel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels"
        });
        var anchorMessage = await PostJsonAsync<MessagePayload>(client, $"/api/channels/{channel.Id}/messages", new
        {
            senderType = "user",
            senderIdentity = "patch",
            body = "Please work on task 1526"
        });

        var started = await PostJsonAsync<ActivityEventPayload>(client, $"/api/channels/{channel.Id}/activity-events", new
        {
            projectId = "den-channels",
            agentIdentity = "den-mcp-runner",
            deliveryRequestId = "delivery-1526",
            hermesSessionKey = "den-channels:1526",
            taskId = 1526,
            threadId = 6445,
            anchorMessageId = anchorMessage.Id,
            eventType = "tool_call_started",
            status = "started",
            sequence = 1,
            title = "terminal",
            summary = "dotnet test",
            previewJson = "{\"command\":\"dotnet test\",\"apiKey\":\"sk-test-secret\"}",
            metadataJson = "{\"authorization\":\"Bearer very-secret-token\"}",
            dedupeKey = "activity:delivery-1526:1"
        });
        Assert.Equal("started", started.Status);
        Assert.Equal(anchorMessage.Id, started.AnchorMessageId);
        Assert.DoesNotContain("sk-test-secret", started.PreviewJson);
        Assert.DoesNotContain("very-secret-token", started.MetadataJson);
        Assert.Contains("[REDACTED]", started.PreviewJson);

        var duplicate = await PostJsonAsync<ActivityEventPayload>(client, $"/api/channels/{channel.Id}/activity-events", new
        {
            projectId = "den-channels",
            agentIdentity = "den-mcp-runner",
            deliveryRequestId = "delivery-1526",
            hermesSessionKey = "den-channels:1526",
            eventType = "tool_call_completed",
            status = "completed",
            sequence = 1,
            summary = "dotnet test passed",
            dedupeKey = "activity:delivery-1526:1"
        });
        Assert.Equal(started.Id, duplicate.Id);
        Assert.Equal("completed", duplicate.Status);
        Assert.True(duplicate.UpdateVersion > started.UpdateVersion);

        var otherTask = await PostJsonAsync<ActivityEventPayload>(client, $"/api/channels/{channel.Id}/activity-events", new
        {
            projectId = "den-channels",
            agentIdentity = "den-mcp-runner",
            deliveryRequestId = "delivery-1529",
            hermesSessionKey = "den-channels:1529",
            taskId = 1529,
            eventType = "tool_call_started",
            status = "started",
            sequence = 2,
            summary = "other task should not leak into task filter",
            dedupeKey = "activity:delivery-1529:1"
        });
        Assert.NotEqual(started.Id, otherTask.Id);

        var byTask = await client.GetFromJsonAsync<List<ActivityEventPayload>>(
            $"/api/channels/{channel.Id}/activity-events?taskId=1526");
        Assert.NotNull(byTask);
        var taskActivity = Assert.Single(byTask);
        Assert.Equal(started.Id, taskActivity.Id);

        var updated = await PatchJsonAsync<ActivityEventPayload>(client, $"/api/channel-activity-events/{started.Id}", new
        {
            status = "failed",
            summary = "dotnet test failed before fix"
        });
        Assert.Equal("failed", updated.Status);
        Assert.Contains("failed", updated.Summary);

        var byDelivery = await client.GetFromJsonAsync<List<ActivityEventPayload>>(
            $"/api/channels/{channel.Id}/activity-events?deliveryRequestId=delivery-1526");
        Assert.NotNull(byDelivery);
        var activityEvent = Assert.Single(byDelivery);
        Assert.Equal(started.Id, activityEvent.Id);
        Assert.Equal("den-channels:1526", activityEvent.HermesSessionKey);

        var messages = await client.GetFromJsonAsync<List<MessagePayload>>($"/api/channels/{channel.Id}/messages?afterId=0&limit=10");
        Assert.NotNull(messages);
        var onlyMessage = Assert.Single(messages);
        Assert.Equal(anchorMessage.Id, onlyMessage.Id);
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

    private static async Task<T> PatchJsonAsync<T>(HttpClient client, string url, object request)
    {
        using var response = await client.PatchAsJsonAsync(url, request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(payload);
        return payload;
    }

    private sealed record ChannelPayload(long Id, string Slug, string DisplayName, string Kind, string? ProjectId);

    private sealed record MessagePayload(long Id, long ChannelId, string Body, string? SourceKind, string? DeepLink,
        string? DedupeKey);

    private sealed record MembershipPayload(long Id, long ChannelId, string MemberType, string MemberIdentity,
        string MembershipStatus, string WakePolicy);

    private sealed record GatewayMembershipsPayload(
        long ChannelId,
        string ChannelSlug,
        string ChannelKind,
        string? ProjectId,
        List<GatewayMemberPayload> Members);

    private sealed record GatewayMemberPayload(
        long Id,
        string MemberType,
        string MemberIdentity,
        string MembershipStatus,
        string WakePolicy,
        bool CanSend,
        bool CanReact,
        bool CanInvite,
        int CooldownSeconds,
        int MaxAutoRepliesPerWindow,
        string? SettingsLabel);

    private sealed record ReactionPayload(long Id, long ChannelMessageId, string ReactionKey);

    private sealed record ReactionSummaryPayload(long ChannelMessageId, string ReactionKey, int Count,
        string[] Reactors);

    private sealed record ActivityEventPayload(long Id, long ChannelId, string? ProjectId, string AgentIdentity,
        string? DeliveryRequestId, string? HermesSessionKey, long? AnchorMessageId, string EventType, string Status,
        long Sequence, long UpdateVersion, string? Summary, string? PreviewJson, string? MetadataJson);
}
