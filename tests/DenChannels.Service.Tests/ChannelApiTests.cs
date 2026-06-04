using System.Net;
using System.Net.Http.Json;
using DenChannels.Service.Gateway;
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
        Assert.Null(posted.DeliveryRequestId);

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
    public async Task GatewaySystemMessage_PopulatesDeliveryRequestIdFromRequestAndFallbacks()
    {
        using var client = _factory.CreateClient();
        var channel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels"
        });

        var explicitDelivery = await PostJsonAsync<GatewayMessageDto>(client, "/api/gateway/system-messages", new
        {
            channelId = channel.Id,
            body = "Gateway delivered reply",
            sourceKind = "gateway_delivery",
            sourceId = "source-44",
            deliveryRequestId = "delivery-44",
            dedupeKey = "gateway-delivery:44"
        });
        Assert.Equal("delivery-44", explicitDelivery.DeliveryRequestId);

        var fallbackDelivery = await PostJsonAsync<GatewayMessageDto>(client, "/api/gateway/system-messages", new
        {
            channelId = channel.Id,
            body = "Gateway delivered reply fallback",
            sourceKind = "gateway_delivery",
            sourceId = "source-45",
            dedupeKey = "gateway-delivery:45"
        });
        Assert.Equal("source-45", fallbackDelivery.DeliveryRequestId);

        var messages = await client.GetFromJsonAsync<List<MessagePayload>>($"/api/channels/{channel.Id}/messages?afterId=0&limit=10");
        Assert.NotNull(messages);
        Assert.Contains(messages, message => message.DeliveryRequestId == "delivery-44");
        Assert.Contains(messages, message => message.DeliveryRequestId == "source-45");
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
    public async Task ChannelMemberships_ByMemberIdentityDiscoversActiveWorkerChannelsAndSanitizesSettings()
    {
        using var client = _factory.CreateClient();
        var workerPool = await PutJsonAsync<ChannelPayload>(client, "/api/worker-pool/lobby", new { });
        var projectChannel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/agora-os/default-channel", new
        {
            displayName = "Agora OS"
        });
        var otherChannel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/pi-crew/default-channel", new
        {
            displayName = "Pi Crew"
        });

        await PutJsonAsync<MembershipPayload>(client, $"/api/channels/{workerPool.Id}/memberships", new
        {
            memberType = "agent",
            memberIdentity = "spawned-coder",
            wakePolicy = "mentions_only",
            membershipPurpose = "worker_pool_control",
            settingsJson = "{\"profile\":\"spawned-coder\",\"apiKey\":\"sk-secret\"}"
        });
        await PutJsonAsync<MembershipPayload>(client, $"/api/channels/{projectChannel.Id}/memberships", new
        {
            memberType = "agent",
            memberIdentity = "spawned-coder",
            wakePolicy = "mentions_only",
            membershipPurpose = "target_work",
            settingsJson = "{\"bindingName\":\"task-1945\",\"token\":\"secret\"}"
        });
        await PutJsonAsync<MembershipPayload>(client, $"/api/channels/{otherChannel.Id}/memberships", new
        {
            memberType = "agent",
            memberIdentity = "spawned-coder",
            membershipStatus = "left",
            wakePolicy = "never",
            membershipPurpose = "target_work"
        });
        await PutJsonAsync<MembershipPayload>(client, $"/api/channels/{projectChannel.Id}/memberships", new
        {
            memberType = "agent",
            memberIdentity = "spawned-reviewer",
            wakePolicy = "mentions_only",
            membershipPurpose = "target_work"
        });

        using var missingResponse = await client.GetAsync("/api/channel-memberships");
        Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);

        var discovered = await client.GetFromJsonAsync<ChannelMembershipDiscoveryPayload>(
            "/api/channel-memberships?memberIdentity=spawned-coder");
        Assert.NotNull(discovered);
        Assert.Equal("spawned-coder", discovered.MemberIdentity);
        Assert.Equal(2, discovered.Memberships.Count);
        Assert.Equal(workerPool.Id, discovered.Memberships[0].ChannelId);
        Assert.Equal("worker-pool", discovered.Memberships[0].ChannelSlug);
        Assert.Equal("system", discovered.Memberships[0].ChannelKind);
        Assert.Null(discovered.Memberships[0].ProjectId);
        Assert.Equal("worker_pool_control", discovered.Memberships[0].MembershipPurpose);
        Assert.Equal("profile: spawned-coder", discovered.Memberships[0].SettingsLabel);
        Assert.Equal(projectChannel.Id, discovered.Memberships[1].ChannelId);
        Assert.Equal("target_work", discovered.Memberships[1].MembershipPurpose);
        Assert.Equal("binding: task-1945", discovered.Memberships[1].SettingsLabel);
        Assert.DoesNotContain(discovered.Memberships, m => m.ChannelId == otherChannel.Id);
        Assert.DoesNotContain(discovered.Memberships, m => m.MemberIdentity == "spawned-reviewer");
        Assert.DoesNotContain(discovered.Memberships, m => m.MembershipPurpose == "agent_commons");

        var commonsOnly = await client.GetFromJsonAsync<ChannelMembershipDiscoveryPayload>(
            "/api/channel-memberships?memberIdentity=spawned-coder&membershipPurpose=agent_commons");
        Assert.NotNull(commonsOnly);
        var commonsMembership = Assert.Single(commonsOnly.Memberships);
        Assert.Equal("agent_commons", commonsMembership.MembershipPurpose);
        Assert.Equal("agent-commons", commonsMembership.ChannelSlug);

        var targetOnly = await client.GetFromJsonAsync<ChannelMembershipDiscoveryPayload>(
            "/api/channel-memberships?memberIdentity=spawned-coder&membershipPurpose=target_work");
        Assert.NotNull(targetOnly);
        var targetMembership = Assert.Single(targetOnly.Memberships);
        Assert.Equal(projectChannel.Id, targetMembership.ChannelId);
        Assert.Equal("agora-os", targetMembership.ProjectId);

        var includeLeft = await client.GetFromJsonAsync<ChannelMembershipDiscoveryPayload>(
            "/api/channel-memberships?memberIdentity=spawned-coder&includeLeft=true");
        Assert.NotNull(includeLeft);
        var leftMembership = Assert.Single(includeLeft.Memberships, m => m.ChannelId == otherChannel.Id);
        Assert.Equal("left", leftMembership.MembershipStatus);
        Assert.NotNull(leftMembership.LeftAt);
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
    public async Task AgentCommons_AutoEnsureDoesNotOverrideMutedNeverBrake()
    {
        using var client = _factory.CreateClient();
        var commons = await PutJsonAsync<ChannelPayload>(client, "/api/agent-commons", new { });
        await PutJsonAsync<MembershipPayload>(client, $"/api/agent-commons/memberships/den-mcp-runner", new { });
        var braked = await PutJsonAsync<MembershipPayload>(client, $"/api/channels/{commons.Id}/memberships", new
        {
            memberType = "agent",
            memberIdentity = "den-mcp-runner",
            membershipStatus = "muted",
            wakePolicy = "never"
        });
        Assert.Equal("muted", braked.MembershipStatus);
        Assert.Equal("never", braked.WakePolicy);

        var ensuredAgain = await PutJsonAsync<MembershipPayload>(client, "/api/agent-commons/memberships/den-mcp-runner", new { });

        Assert.Equal("muted", ensuredAgain.MembershipStatus);
        Assert.Equal("never", ensuredAgain.WakePolicy);
    }

    [Fact]
    public async Task AgentCommons_BrakeMutesAllActiveAgentMemberships()
    {
        using var client = _factory.CreateClient();
        await PutJsonAsync<MembershipPayload>(client, "/api/agent-commons/memberships/den-mcp-runner", new { });
        await PutJsonAsync<MembershipPayload>(client, "/api/agent-commons/memberships/reviewer", new { });

        var brake = await PostJsonAsync<AgentCommonsBrakePayload>(client, "/api/agent-commons/brake", new
        {
            membershipStatus = "muted",
            wakePolicy = "never",
            requestedBy = "test"
        });

        Assert.Equal("applied", brake.Status);
        Assert.True(brake.UpdatedCount >= 2);
        var memberships = await client.GetFromJsonAsync<GatewayMembershipsPayload>(
            $"/api/gateway/memberships?channelId={brake.ChannelId}");
        Assert.NotNull(memberships);
        Assert.Contains(memberships.Members, member => member.MemberIdentity == "den-mcp-runner" && member.MembershipStatus == "muted" && member.WakePolicy == "never");
        Assert.Contains(memberships.Members, member => member.MemberIdentity == "reviewer" && member.MembershipStatus == "muted" && member.WakePolicy == "never");
    }

    [Fact]
    public async Task ActivityEventEndpoints_AppendUpdateQueryWithoutCreatingMessages()
    {
        using var client = _factory.CreateClient();
        var channel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels"
        });
        var parentMessage = await PostJsonAsync<GatewayMessageDto>(client, "/api/gateway/system-messages", new
        {
            channelId = channel.Id,
            body = "Parent #1567 final delivery block",
            sourceKind = "gateway_delivery",
            sourceId = "parent-1567-source",
            deliveryRequestId = "parent-1567",
            dedupeKey = "gateway-delivery:parent-1567"
        });
        Assert.Equal("parent-1567", parentMessage.DeliveryRequestId);

        var coderStarted = await PostJsonAsync<ActivityEventPayload>(client, $"/api/channels/{channel.Id}/activity-events", new
        {
            projectId = "den-channels",
            agentIdentity = "den-mcp-runner",
            deliveryRequestId = "coder-1567",
            hermesSessionKey = "den-channels:1567:coder",
            displayBlockId = "parent-1567",
            parentHermesSessionKey = "den-channels:1567:parent",
            parentAgentIdentity = "den-mcp-runner",
            workerRunId = "coder-1567",
            workerRole = "coder",
            taskId = 1567,
            threadId = 6445,
            anchorMessageId = parentMessage.Id,
            eventType = "tool_call_started",
            status = "started",
            deliveryStage = "tool",
            terminal = false,
            sequence = 1,
            title = "terminal",
            summary = "dotnet test",
            previewJson = "{\"command\":\"dotnet test\",\"apiKey\":\"***\"}",
            metadataJson = "{\"authorization\":\"Bearer very-secret-token\"}",
            dedupeKey = "activity:coder-1567:1"
        });
        Assert.Equal("started", coderStarted.Status);
        Assert.Equal(parentMessage.Id, coderStarted.AnchorMessageId);
        Assert.Equal("parent-1567", coderStarted.DisplayBlockId);
        Assert.Equal("den-channels:1567:parent", coderStarted.ParentHermesSessionKey);
        Assert.Equal("den-mcp-runner", coderStarted.ParentAgentIdentity);
        Assert.Equal("coder-1567", coderStarted.WorkerRunId);
        Assert.Equal("coder", coderStarted.WorkerRole);
        Assert.Equal("tool", coderStarted.DeliveryStage);
        Assert.False(coderStarted.Terminal);
        Assert.DoesNotContain("***", coderStarted.PreviewJson);
        Assert.DoesNotContain("very-secret-token", coderStarted.MetadataJson);
        Assert.Contains("[REDACTED]", coderStarted.PreviewJson);

        var duplicate = await PostJsonAsync<ActivityEventPayload>(client, $"/api/channels/{channel.Id}/activity-events", new
        {
            projectId = "den-channels",
            agentIdentity = "den-mcp-runner",
            deliveryRequestId = "coder-1567",
            hermesSessionKey = "den-channels:1567:coder",
            eventType = "tool_call_completed",
            status = "completed",
            deliveryStage = "tool",
            terminal = false,
            sequence = 1,
            summary = "dotnet test passed",
            dedupeKey = "activity:coder-1567:1"
        });
        Assert.Equal(coderStarted.Id, duplicate.Id);
        Assert.Equal("completed", duplicate.Status);
        Assert.Equal("parent-1567", duplicate.DisplayBlockId);
        Assert.Equal("coder-1567", duplicate.WorkerRunId);
        Assert.True(duplicate.UpdateVersion > coderStarted.UpdateVersion);

        var reviewerStarted = await PostJsonAsync<ActivityEventPayload>(client, $"/api/channels/{channel.Id}/activity-events", new
        {
            projectId = "den-channels",
            agentIdentity = "den-mcp-runner-reviewer",
            deliveryRequestId = "reviewer-1567",
            hermesSessionKey = "den-channels:1567:reviewer",
            displayBlockId = "parent-1567",
            parentHermesSessionKey = "den-channels:1567:parent",
            parentAgentIdentity = "den-mcp-runner",
            workerRunId = "reviewer-1567",
            workerRole = "reviewer",
            taskId = 1567,
            eventType = "tool_call_started",
            status = "started",
            sequence = 1,
            summary = "reviewer checks grouping",
            dedupeKey = "activity:reviewer-1567:1"
        });
        Assert.NotEqual(coderStarted.Id, reviewerStarted.Id);

        var otherTask = await PostJsonAsync<ActivityEventPayload>(client, $"/api/channels/{channel.Id}/activity-events", new
        {
            projectId = "den-channels",
            agentIdentity = "den-mcp-runner",
            deliveryRequestId = "delivery-1529",
            hermesSessionKey = "den-channels:1529",
            displayBlockId = "block-1529",
            workerRunId = "worker-run-1529",
            taskId = 1529,
            eventType = "tool_call_started",
            status = "started",
            deliveryStage = "tool",
            terminal = false,
            sequence = 2,
            summary = "other task should not leak into task filter",
            dedupeKey = "activity:delivery-1529:1"
        });
        Assert.NotEqual(coderStarted.Id, otherTask.Id);

        var byTask = await client.GetFromJsonAsync<List<ActivityEventPayload>>(
            $"/api/channels/{channel.Id}/activity-events?taskId=1567");
        Assert.NotNull(byTask);
        Assert.Equal(new[] { coderStarted.Id, reviewerStarted.Id }, byTask.Select(activity => activity.Id).Order().ToArray());

        var updated = await PatchJsonAsync<ActivityEventPayload>(client, $"/api/channel-activity-events/{coderStarted.Id}", new
        {
            status = "failed",
            deliveryStage = "failure",
            terminal = true,
            summary = "dotnet test failed before fix"
        });
        Assert.Equal("failed", updated.Status);
        Assert.Equal("failure", updated.DeliveryStage);
        Assert.True(updated.Terminal);
        Assert.Contains("failed", updated.Summary);

        var byDelivery = await client.GetFromJsonAsync<List<ActivityEventPayload>>(
            $"/api/channels/{channel.Id}/activity-events?deliveryRequestId=coder-1567");
        Assert.NotNull(byDelivery);
        var activityEvent = Assert.Single(byDelivery);
        Assert.Equal(coderStarted.Id, activityEvent.Id);
        Assert.Equal("den-channels:1567:coder", activityEvent.HermesSessionKey);

        var displayBlockJson = await client.GetStringAsync($"/api/channels/{channel.Id}/activity-events?displayBlockId=parent-1567");
        Assert.Contains("\"displayBlockId\":\"parent-1567\"", displayBlockJson);
        Assert.Contains("\"workerRunId\":\"coder-1567\"", displayBlockJson);
        Assert.Contains("\"workerRunId\":\"reviewer-1567\"", displayBlockJson);
        Assert.DoesNotContain("displayDeliveryRequestId", displayBlockJson);
        var byDisplayBlock = await client.GetFromJsonAsync<List<ActivityEventPayload>>(
            $"/api/channels/{channel.Id}/activity-events?displayBlockId=parent-1567");
        Assert.NotNull(byDisplayBlock);
        Assert.Equal(new[] { coderStarted.Id, reviewerStarted.Id }, byDisplayBlock.Select(activity => activity.Id).Order().ToArray());

        var byWorkerRun = await client.GetFromJsonAsync<List<ActivityEventPayload>>(
            $"/api/channels/{channel.Id}/activity-events?workerRunId=coder-1567");
        Assert.NotNull(byWorkerRun);
        Assert.Equal(coderStarted.Id, Assert.Single(byWorkerRun).Id);

        var messagesJson = await client.GetStringAsync($"/api/channels/{channel.Id}/messages?afterId=0&limit=10");
        Assert.Contains("\"deliveryRequestId\":\"parent-1567\"", messagesJson);
        Assert.DoesNotContain("displayDeliveryRequestId", messagesJson);
        var messages = await client.GetFromJsonAsync<List<MessagePayload>>($"/api/channels/{channel.Id}/messages?afterId=0&limit=10");
        Assert.NotNull(messages);
        var onlyMessage = Assert.Single(messages);
        Assert.Equal(parentMessage.Id, onlyMessage.Id);
        Assert.Equal("parent-1567", onlyMessage.DeliveryRequestId);
    }

    [Fact]
    public async Task ActivityEventEndpoints_DefaultListReturnsLatestWindowChronologically()
    {
        using var client = _factory.CreateClient();
        var channel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/activity-window/default-channel", new
        {
            displayName = "Activity window"
        });

        var created = new List<ActivityEventPayload>();
        for (var i = 1; i <= 130; i++)
        {
            var activity = await PostJsonAsync<ActivityEventPayload>(client, $"/api/channels/{channel.Id}/activity-events", new
            {
                projectId = "activity-window",
                agentIdentity = "den-mcp-runner",
                deliveryRequestId = $"delivery-{i:D3}",
                hermesSessionKey = $"activity-window:{i:D3}",
                displayBlockId = i <= 3 ? "old-display-block" : null,
                workerRunId = i <= 3 ? "old-worker-run" : null,
                eventType = "tool_call_started",
                status = "started",
                deliveryStage = "tool",
                terminal = false,
                sequence = 1,
                title = $"tool-{i:D3}",
                summary = $"activity event {i:D3}",
                dedupeKey = $"activity-window:{i:D3}"
            });
            created.Add(activity);
        }

        var defaultWindow = await client.GetFromJsonAsync<List<ActivityEventPayload>>(
            $"/api/channels/{channel.Id}/activity-events?limit=120");

        Assert.NotNull(defaultWindow);
        Assert.Equal(120, defaultWindow.Count);
        Assert.Equal(created.Skip(10).Select(activity => activity.Id).ToArray(), defaultWindow.Select(activity => activity.Id).ToArray());
        Assert.DoesNotContain(defaultWindow, activity => activity.Id == created[0].Id);
        Assert.Contains(defaultWindow, activity => activity.Id == created[^1].Id);

        var scopedDisplayBlock = await client.GetFromJsonAsync<List<ActivityEventPayload>>(
            $"/api/channels/{channel.Id}/activity-events?displayBlockId=old-display-block&limit=120");
        Assert.NotNull(scopedDisplayBlock);
        Assert.Equal(created.Take(3).Select(activity => activity.Id).ToArray(), scopedDisplayBlock.Select(activity => activity.Id).ToArray());
    }

    [Fact]
    public async Task GatewayActivityEndpoint_RecordsNonWakingProgressWithoutCreatingMessages()
    {
        using var client = _factory.CreateClient();
        var channel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels"
        });

        var recorded = await PostJsonAsync<ChannelActivityRouteResultPayload>(client,
            $"/api/gateway/channel-activity-events?channelId={channel.Id}", new
            {
                projectId = "den-channels",
                agentIdentity = "sysadmin",
                deliveryRequestId = "delivery-1546",
                eventType = "lifecycle_status",
                status = "interim",
                deliveryStage = "assistant_interim",
                terminal = false,
                sequence = 1,
                summary = "I will inspect task context before the final answer",
                dedupeKey = "activity:delivery-1546:interim:1"
            });

        Assert.Equal("recorded", recorded.Status);
        Assert.True(recorded.Recorded);
        Assert.Equal(recorded.ActivityEventId, recorded.ActivityEvent?.Id.ToString());
        Assert.NotNull(recorded.ActivityEvent);
        Assert.Equal("assistant_interim", recorded.ActivityEvent.DeliveryStage);
        Assert.False(recorded.ActivityEvent.Terminal);
        Assert.Equal("delivery-1546", recorded.ActivityEvent.DeliveryRequestId);

        var messages = await client.GetFromJsonAsync<List<MessagePayload>>($"/api/channels/{channel.Id}/messages?afterId=0&limit=10");
        Assert.NotNull(messages);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task ChannelActivityEventsGatewayShapeRoute_DefaultsAndPreservesFieldsWithoutCreatingMessages()
    {
        using var client = _factory.CreateClient();
        var channel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels"
        });
        var parentMessage = await PostJsonAsync<GatewayMessageDto>(client, "/api/gateway/system-messages", new
        {
            channelId = channel.Id,
            body = "Parent #1944 display block",
            sourceKind = "gateway_delivery",
            sourceId = "parent-1944-source",
            deliveryRequestId = "parent-1944",
            dedupeKey = "gateway-delivery:parent-1944"
        });

        var recorded = await PostJsonAsync<ChannelActivityRouteResultPayload>(client,
            "/api/channel-activity-events", new
            {
                channelId = channel.Id.ToString(),
                projectId = "den-channels",
                agentIdentity = "den-coder-profile",
                deliveryRequestId = "coder-1944",
                displayBlockId = "parent-1944",
                hermesSessionKey = "session-coder-1944",
                parentHermesSessionKey = "session-parent-1944",
                parentAgentIdentity = "den-mcp-planner",
                workerRunId = "coder-1944",
                workerRole = "coder",
                taskId = 1944,
                threadId = 10691,
                anchorMessageId = parentMessage.Id,
                eventType = "",
                status = "",
                sequence = 7,
                title = "terminal",
                summary = "dotnet test breadcrumbs",
                previewJson = "{\"command\":\"dotnet test\"}",
                metadataJson = "{\"phase\":\"coder\"}",
                dedupeKey = "activity:coder-1944:7"
            });

        Assert.Equal("recorded", recorded.Status);
        Assert.True(recorded.Recorded);
        Assert.NotNull(recorded.ActivityEvent);
        Assert.Equal(channel.Id, recorded.ActivityEvent.ChannelId);
        Assert.Equal("den-coder-profile", recorded.ActivityEvent.AgentIdentity);
        Assert.Equal("coder-1944", recorded.ActivityEvent.DeliveryRequestId);
        Assert.Equal("parent-1944", recorded.ActivityEvent.DisplayBlockId);
        Assert.Equal("session-coder-1944", recorded.ActivityEvent.HermesSessionKey);
        Assert.Equal("session-parent-1944", recorded.ActivityEvent.ParentHermesSessionKey);
        Assert.Equal("den-mcp-planner", recorded.ActivityEvent.ParentAgentIdentity);
        Assert.Equal("coder-1944", recorded.ActivityEvent.WorkerRunId);
        Assert.Equal("coder", recorded.ActivityEvent.WorkerRole);
        Assert.Equal(parentMessage.Id, recorded.ActivityEvent.AnchorMessageId);
        Assert.Equal("lifecycle_status", recorded.ActivityEvent.EventType);
        Assert.Equal("interim", recorded.ActivityEvent.Status);
        Assert.Equal(7, recorded.ActivityEvent.Sequence);
        Assert.Contains("dotnet test", recorded.ActivityEvent.Summary);

        var duplicate = await PostJsonAsync<ChannelActivityRouteResultPayload>(client,
            "/api/channel-activity-events", new
            {
                channelId = channel.Id,
                projectId = "den-channels",
                agentIdentity = "den-coder-profile",
                deliveryRequestId = "coder-1944",
                eventType = "tool_call_completed",
                status = "completed",
                sequence = 7,
                summary = "same dedupe key completed",
                dedupeKey = "activity:coder-1944:7"
            });

        Assert.Equal(recorded.ActivityEvent.Id, duplicate.ActivityEvent?.Id);
        Assert.Equal("completed", duplicate.ActivityEvent?.Status);
        Assert.Equal("parent-1944", duplicate.ActivityEvent?.DisplayBlockId);
        Assert.True(duplicate.ActivityEvent?.UpdateVersion > recorded.ActivityEvent.UpdateVersion);

        var messages = await client.GetFromJsonAsync<List<MessagePayload>>($"/api/channels/{channel.Id}/messages?afterId=0&limit=10");
        Assert.NotNull(messages);
        var onlyMessage = Assert.Single(messages);
        Assert.Equal(parentMessage.Id, onlyMessage.Id);
        Assert.Equal("parent-1944", onlyMessage.DeliveryRequestId);
    }

    [Fact]
    public async Task ChannelActivityEventsGatewayShapeRoute_RejectsMissingRequiredFields()
    {
        using var client = _factory.CreateClient();

        using var missingChannel = await client.PostAsJsonAsync("/api/channel-activity-events", new
        {
            agentIdentity = "den-mcp-planner"
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingChannel.StatusCode);
        var missingChannelResult = await missingChannel.Content.ReadFromJsonAsync<ChannelActivityRouteResultPayload>();
        Assert.NotNull(missingChannelResult);
        Assert.Equal("rejected", missingChannelResult.Status);
        Assert.False(missingChannelResult.Recorded);
        Assert.Equal("missing_channel_id", missingChannelResult.ErrorCode);

        using var missingAgent = await client.PostAsJsonAsync("/api/channel-activity-events", new
        {
            channelId = "42"
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingAgent.StatusCode);
        var missingAgentResult = await missingAgent.Content.ReadFromJsonAsync<ChannelActivityRouteResultPayload>();
        Assert.NotNull(missingAgentResult);
        Assert.Equal("rejected", missingAgentResult.Status);
        Assert.False(missingAgentResult.Recorded);
        Assert.Equal("missing_agent_identity", missingAgentResult.ErrorCode);
    }

    [Fact]
    public async Task ChannelActivityEventsStatus_ReportsRecentSoftWriteFailures()
    {
        using var client = _factory.CreateClient();

        var degraded = await PostJsonAsync<ChannelActivityRouteResultPayload>(client,
            "/api/channel-activity-events", new
            {
                channelId = "999999",
                projectId = "den-channels",
                agentIdentity = "den-mcp-planner",
                deliveryRequestId = "delivery-1944",
                displayBlockId = "display-1944",
                workerRunId = "worker-1944",
                eventType = "tool_call_failed"
            });

        Assert.Equal("degraded", degraded.Status);
        Assert.False(degraded.Recorded);
        Assert.Equal("activity_record_failed", degraded.ErrorCode);

        var status = await client.GetFromJsonAsync<ChannelActivityRouterStatusPayload>("/api/channel-activity-events/status");
        Assert.NotNull(status);
        var failure = Assert.Single(status.RecentFailures);
        Assert.Equal("999999", failure.ChannelId);
        Assert.Equal("den-channels", failure.ProjectId);
        Assert.Equal("den-mcp-planner", failure.AgentIdentity);
        Assert.Equal("delivery-1944", failure.DeliveryRequestId);
        Assert.Equal("display-1944", failure.DisplayBlockId);
        Assert.Equal("worker-1944", failure.WorkerRunId);
        Assert.Equal("activity_record_failed", failure.ErrorCode);
    }

    [Fact]
    public async Task AssignmentMessages_StoreAndReadAssignmentMetadata()
    {
        using var client = _factory.CreateClient();
        var channel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels"
        });

        var posted = await PostJsonAsync<MessagePayload>(client, $"/api/channels/{channel.Id}/messages", new
        {
            senderType = "system",
            senderIdentity = "den-gateway",
            body = "Checkpoint transcript: Assignment #asn-42 created.",
            messageKind = "system_event",
            sourceKind = "gateway_delivery",
            sourceId = "assign-42",
            sourceProjectId = "den-channels",
            assignmentId = "asn-42",
            checkpointType = "assignment_created",
            checkpointHandle = "chk://den-core/assignment/asn-42/1",
            dedupeKey = $"assign-msg:{Guid.NewGuid():N}"
        });
        Assert.NotNull(posted);

        // Read back by assignmentId filter
        var byAssignment = await client.GetFromJsonAsync<List<MessagePayload>>(
            $"/api/channels/{channel.Id}/messages?assignmentId=asn-42&limit=50");
        Assert.NotNull(byAssignment);
        var found = Assert.Single(byAssignment);
        Assert.Equal(posted.Id, found.Id);
    }

    [Fact]
    public async Task AssignmentActivityEvents_StoreAndReadAssignmentMetadata()
    {
        using var client = _factory.CreateClient();
        var channel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels"
        });

        var posted = await PostJsonAsync<ActivityEventPayload>(client, $"/api/channels/{channel.Id}/activity-events", new
        {
            projectId = "den-channels",
            agentIdentity = "den-mcp-runner",
            deliveryRequestId = "delivery-asn-42",
            hermesSessionKey = "session-asn-42",
            workerRunId = "run-asn-42",
            workerRole = "coder",
            taskId = 1724L,
            eventType = "lifecycle_status",
            status = "interim",
            deliveryStage = "assignment_checkpoint",
            terminal = false,
            sequence = 1,
            assignmentId = "asn-42",
            checkpointType = "checkpoint_saved",
            checkpointHandle = "chk://den-core/assignment/asn-42/2",
            title = "Checkpoint saved",
            summary = "Assignment asn-42 checkpoint saved",
            dedupeKey = $"assign-activity:{Guid.NewGuid():N}"
        });
        Assert.NotNull(posted);

        // Read back by assignmentId filter
        var byAssignment = await client.GetFromJsonAsync<List<ActivityEventPayload>>(
            $"/api/channels/{channel.Id}/activity-events?assignmentId=asn-42");
        Assert.NotNull(byAssignment);
        var found = Assert.Single(byAssignment);
        Assert.Equal(posted.Id, found.Id);
    }

    [Fact]
    public async Task AssignmentMessages_FilterIsServerSideBeforePagination()
    {
        using var client = _factory.CreateClient();
        var channel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels"
        });

        // Post 10 messages with different assignment IDs
        for (var i = 1; i <= 5; i++)
        {
            await PostJsonAsync<MessagePayload>(client, $"/api/channels/{channel.Id}/messages", new
            {
                senderType = "system",
                senderIdentity = "den-gateway",
                body = $"Checkpoint {i} for asn-a",
                assignmentId = "asn-a",
                checkpointType = "checkpoint",
                dedupeKey = $"assign-filter-a-{i}:{Guid.NewGuid():N}"
            });
        }
        for (var i = 1; i <= 5; i++)
        {
            await PostJsonAsync<MessagePayload>(client, $"/api/channels/{channel.Id}/messages", new
            {
                senderType = "system",
                senderIdentity = "den-gateway",
                body = $"Checkpoint {i} for asn-b",
                assignmentId = "asn-b",
                checkpointType = "checkpoint",
                dedupeKey = $"assign-filter-b-{i}:{Guid.NewGuid():N}"
            });
        }

        // Filter by assignmentId=asn-a with limit=2 should return 2 of asn-a's messages, not 2 of asn-b
        var filtered = await client.GetFromJsonAsync<List<MessagePayload>>(
            $"/api/channels/{channel.Id}/messages?assignmentId=asn-a&limit=2");
        Assert.NotNull(filtered);
        Assert.Equal(2, filtered.Count);
        foreach (var msg in filtered)
        {
            Assert.Equal("asn-a", msg.AssignmentId);
        }
    }

    [Fact]
    public async Task AssignmentActivityEvents_FilterIsServerSideBeforePagination()
    {
        using var client = _factory.CreateClient();
        var channel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels"
        });

        for (var i = 1; i <= 5; i++)
        {
            await PostJsonAsync<ActivityEventPayload>(client, $"/api/channels/{channel.Id}/activity-events", new
            {
                projectId = "den-channels",
                agentIdentity = "den-mcp-runner",
                deliveryRequestId = $"delivery-a-{i}",
                eventType = "lifecycle_status",
                status = "interim",
                terminal = false,
                sequence = i,
                assignmentId = "asn-active-a",
                dedupeKey = $"assign-filter-activity-a-{i}:{Guid.NewGuid():N}"
            });
        }
        for (var i = 1; i <= 5; i++)
        {
            await PostJsonAsync<ActivityEventPayload>(client, $"/api/channels/{channel.Id}/activity-events", new
            {
                projectId = "den-channels",
                agentIdentity = "den-mcp-runner",
                deliveryRequestId = $"delivery-b-{i}",
                eventType = "lifecycle_status",
                status = "interim",
                terminal = false,
                sequence = i,
                assignmentId = "asn-active-b",
                dedupeKey = $"assign-filter-activity-b-{i}:{Guid.NewGuid():N}"
            });
        }

        var filtered = await client.GetFromJsonAsync<List<ActivityEventPayload>>(
            $"/api/channels/{channel.Id}/activity-events?assignmentId=asn-active-a&limit=2");
        Assert.NotNull(filtered);
        Assert.Equal(2, filtered.Count);
        foreach (var ev in filtered)
        {
            Assert.Equal("asn-active-a", ev.AssignmentId);
        }
    }

    [Fact]
    public async Task AssignmentActivityEvents_AreNonWakingAndDoNotAffectCursors()
    {
        using var client = _factory.CreateClient();
        var channel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels"
        });

        // Post a visible message first (waking content)
        var msg = await PostJsonAsync<MessagePayload>(client, $"/api/channels/{channel.Id}/messages", new
        {
            senderType = "user",
            senderIdentity = "patch",
            body = "Wake message",
            dedupeKey = $"wake-msg:{Guid.NewGuid():N}"
        });
        Assert.NotNull(msg);

        // Post a non-waking assignment activity event
        var activity = await PostJsonAsync<ActivityEventPayload>(client, $"/api/channels/{channel.Id}/activity-events", new
        {
            projectId = "den-channels",
            agentIdentity = "den-mcp-runner",
            deliveryRequestId = "delivery-asn-nw",
            eventType = "lifecycle_status",
            status = "interim",
            terminal = false,
            sequence = 1,
            assignmentId = "asn-nw",
            dedupeKey = $"assign-nw:{Guid.NewGuid():N}"
        });
        Assert.NotNull(activity);

        // Verify no new messages were created (only the wake message exists)
        var messagesAfterActivity = await client.GetFromJsonAsync<List<MessagePayload>>(
            $"/api/channels/{channel.Id}/messages?afterId=0&limit=10");
        Assert.NotNull(messagesAfterActivity);
        Assert.Single(messagesAfterActivity);  // Only the wake message

        // Verify activity events list includes the assignment event
        var activities = await client.GetFromJsonAsync<List<ActivityEventPayload>>(
            $"/api/channels/{channel.Id}/activity-events?assignmentId=asn-nw");
        Assert.NotNull(activities);
        Assert.NotEmpty(activities);
    }

    [Fact]
    public async Task AssignmentTranscript_ReadbackByAssignmentId_ReturnsMessagesAndActivity()
    {
        using var client = _factory.CreateClient();
        var channel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-channels/default-channel", new
        {
            displayName = "Den Channels"
        });

        // Post a message with assignment metadata
        var msg = await PostJsonAsync<MessagePayload>(client, $"/api/channels/{channel.Id}/messages", new
        {
            senderType = "system",
            senderIdentity = "den-gateway",
            body = "Assignment transcript message",
            assignmentId = "asn-transcript-1",
            checkpointType = "assignment_created",
            checkpointHandle = "chk://den-core/assignment/asn-transcript-1/1",
            dedupeKey = $"assign-transcript-msg:{Guid.NewGuid():N}"
        });

        // Post activity events with the same assignmentId
        var activity = await PostJsonAsync<ActivityEventPayload>(client, $"/api/channels/{channel.Id}/activity-events", new
        {
            projectId = "den-channels",
            agentIdentity = "den-mcp-runner",
            deliveryRequestId = "delivery-transcript",
            eventType = "lifecycle_status",
            status = "completed",
            terminal = true,
            sequence = 1,
            assignmentId = "asn-transcript-1",
            checkpointType = "checkpoint_applied",
            checkpointHandle = "chk://den-core/assignment/asn-transcript-1/2",
            summary = "Checkpoint applied",
            dedupeKey = $"assign-transcript-activity:{Guid.NewGuid():N}"
        });

        // Use the assignment-scoped read API to get transcript
        var transcript = await client.GetFromJsonAsync<AssignmentTranscriptPayload>(
            $"/api/assignments/asn-transcript-1/transcript?channelId={channel.Id}");
        Assert.NotNull(transcript);
        Assert.Equal("asn-transcript-1", transcript.AssignmentId);
        Assert.NotEmpty(transcript.Messages);
        Assert.NotEmpty(transcript.ActivityEvents);
        Assert.Contains(transcript.Messages, m => m.Id == msg.Id);
        Assert.Contains(transcript.ActivityEvents, e => e.Id == activity.Id);
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
        string? DeliveryRequestId, string? DedupeKey, string? AssignmentId = null, string? CheckpointType = null, string? CheckpointHandle = null);

    private sealed record MembershipPayload(long Id, long ChannelId, string MemberType, string MemberIdentity,
        string MembershipStatus, string WakePolicy);

    private sealed record ChannelMembershipDiscoveryPayload(
        string MemberIdentity,
        List<ChannelMembershipDiscoveryItemPayload> Memberships);

    private sealed record ChannelMembershipDiscoveryItemPayload(
        long ChannelId,
        string ChannelSlug,
        string ChannelKind,
        string? ProjectId,
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
        string? SettingsLabel,
        string? MembershipPurpose,
        string CreatedAt,
        string UpdatedAt,
        string? LeftAt);

    private sealed record AgentCommonsBrakePayload(string Status, long ChannelId, int UpdatedCount, string MembershipStatus, string WakePolicy);

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

    private sealed record ChannelActivityRouteResultPayload(
        string Status,
        bool Recorded,
        string? ActivityEventId,
        string? ErrorCode,
        string? Message,
        ActivityEventPayload? ActivityEvent);

    private sealed record ChannelActivityRouterStatusPayload(IReadOnlyList<ChannelActivityDiagnosticPayload> RecentFailures);

    private sealed record ChannelActivityDiagnosticPayload(
        string ChannelId,
        string? ProjectId,
        string? AgentIdentity,
        string? DeliveryRequestId,
        string? DisplayBlockId,
        string? WorkerRunId,
        string ErrorCode,
        string Message);

    private sealed record ActivityEventPayload(long Id, long ChannelId, string? ProjectId, string AgentIdentity,
        string? DeliveryRequestId, string? HermesSessionKey, string? DisplayBlockId, string? ParentHermesSessionKey,
        string? ParentAgentIdentity, string? WorkerRunId, string? WorkerRole, long? AnchorMessageId, string EventType, string Status,
        string DeliveryStage, bool Terminal, long Sequence, long UpdateVersion, string? Summary, string? PreviewJson,
        string? MetadataJson, long? FinalChannelMessageId, string? AssignmentId = null, string? CheckpointType = null, string? CheckpointHandle = null);

    private sealed record AssignmentTranscriptPayload(
        string AssignmentId,
        IReadOnlyList<MessagePayload> Messages,
        IReadOnlyList<ActivityEventPayload> ActivityEvents);
}
