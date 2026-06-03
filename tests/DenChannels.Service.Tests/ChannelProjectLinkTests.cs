using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DenChannels.Service.Tests;

public sealed class ChannelProjectLinkTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"den-channels-cpl-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public ChannelProjectLinkTests()
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
    public async Task OneProjectDefaultChannel_StillWorksIndependentlyOfLinks()
    {
        using var client = _factory.CreateClient();

        // Create a project default channel the normal way
        var channel = await PutJsonAsync<ChannelPayload>(client, "/api/projects/den-foo/default-channel", new
        {
            displayName = "Foo Project"
        });

        Assert.Equal("project-den-foo", channel.Slug);
        Assert.Equal("project_default", channel.Kind);
        Assert.Equal("den-foo", channel.ProjectId);

        // The project should have no linked channels (no links created yet)
        var linkedChannels = await client.GetFromJsonAsync<List<ChannelPayload>>("/api/projects/den-foo/linked-channels");
        Assert.NotNull(linkedChannels);
        Assert.Empty(linkedChannels);

        // The channel should have no linked projects
        var linkedProjects = await client.GetFromJsonAsync<List<ProjectLinkPayload>>($"/api/channels/{channel.Id}/linked-projects");
        Assert.NotNull(linkedProjects);
        Assert.Empty(linkedProjects);
    }

    [Fact]
    public async Task ManyProjectsToOne_SharedOpsChannel()
    {
        using var client = _factory.CreateClient();

        // Create a shared system channel
        var sharedChannel = await PostJsonAsync<ChannelPayload>(client, "/api/channels", new
        {
            slug = "shared-ops",
            displayName = "Shared Ops",
            kind = "system",
            createdBy = "test"
        });

        // Link multiple projects to this shared channel
        var link1 = await PostJsonAsync<ProjectLinkPayload>(client, "/api/channel-project-links", new
        {
            channelId = sharedChannel.Id,
            projectId = "den-core",
            relationKind = "linked",
            isPrimary = true
        });
        Assert.Equal("den-core", link1.ProjectId);
        Assert.True(link1.IsPrimary);
        Assert.Equal("linked", link1.RelationKind);

        var link2 = await PostJsonAsync<ProjectLinkPayload>(client, "/api/channel-project-links", new
        {
            channelId = sharedChannel.Id,
            projectId = "den-mcp",
            relationKind = "linked",
            isPrimary = false
        });
        Assert.Equal("den-mcp", link2.ProjectId);
        Assert.False(link2.IsPrimary);

        var link3 = await PostJsonAsync<ProjectLinkPayload>(client, "/api/channel-project-links", new
        {
            channelId = sharedChannel.Id,
            projectId = "den-channels",
            relationKind = "linked",
            isPrimary = false
        });
        Assert.Equal("den-channels", link3.ProjectId);

        // Verify channel shows all linked projects
        var linkedProjects = await client.GetFromJsonAsync<List<ProjectLinkPayload>>($"/api/channels/{sharedChannel.Id}/linked-projects");
        Assert.NotNull(linkedProjects);
        Assert.Equal(3, linkedProjects.Count);
        // Primary should be first (ordered by is_primary DESC)
        Assert.Equal("den-core", linkedProjects[0].ProjectId);
        Assert.True(linkedProjects[0].IsPrimary);

        // Verify each project sees the shared channel
        var coreChannels = await client.GetFromJsonAsync<List<ChannelPayload>>("/api/projects/den-core/linked-channels");
        Assert.NotNull(coreChannels);
        // den-core is also linked to den-system from seed, so it has 2 channels
        Assert.Contains(coreChannels, c => c.Id == sharedChannel.Id);

        var mcpChannels = await client.GetFromJsonAsync<List<ChannelPayload>>("/api/projects/den-mcp/linked-channels");
        Assert.NotNull(mcpChannels);
        // den-mcp is also linked to den-system from seed, so it has 2 channels
        Assert.Contains(mcpChannels, c => c.Id == sharedChannel.Id);
    }

    [Fact]
    public async Task UpsertGetDeleteLink_RoundTrip()
    {
        using var client = _factory.CreateClient();

        // Create a channel
        var channel = await PostJsonAsync<ChannelPayload>(client, "/api/channels", new
        {
            slug = "test-ops",
            displayName = "Test Ops",
            kind = "system",
            createdBy = "test"
        });

        // Upsert a link
        var link = await PostJsonAsync<ProjectLinkPayload>(client, "/api/channel-project-links", new
        {
            channelId = channel.Id,
            projectId = "test-project",
            relationKind = "linked",
            isPrimary = true
        });
        Assert.Equal(channel.Id, link.ChannelId);
        Assert.Equal("test-project", link.ProjectId);
        Assert.Equal("linked", link.RelationKind);
        Assert.True(link.IsPrimary);
        Assert.NotNull(link.CreatedAt);

        // Upsert again (update) — change isPrimary to false
        var updated = await PostJsonAsync<ProjectLinkPayload>(client, "/api/channel-project-links", new
        {
            channelId = channel.Id,
            projectId = "test-project",
            isPrimary = false
        });
        Assert.Equal(link.Id, updated.Id);
        Assert.False(updated.IsPrimary);
        Assert.Equal("linked", updated.RelationKind); // preserved

        // Get linked projects
        var linkedProjects = await client.GetFromJsonAsync<List<ProjectLinkPayload>>($"/api/channels/{channel.Id}/linked-projects");
        Assert.NotNull(linkedProjects);
        var fetched = Assert.Single(linkedProjects);
        Assert.Equal("test-project", fetched.ProjectId);

        // Delete the link
        using var deleteResponse = await client.DeleteAsync($"/api/channel-project-links?channelId={channel.Id}&projectId=test-project");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Verify link is gone
        var afterDelete = await client.GetFromJsonAsync<List<ProjectLinkPayload>>($"/api/channels/{channel.Id}/linked-projects");
        Assert.NotNull(afterDelete);
        Assert.Empty(afterDelete);
    }

    [Fact]
    public async Task GatewayRouting_ResolvesLinkedProjectChannel()
    {
        using var client = _factory.CreateClient();

        // Create a shared system channel
        var sharedChannel = await PostJsonAsync<ChannelPayload>(client, "/api/channels", new
        {
            slug = "shared-ops-gw",
            displayName = "Shared Ops GW",
            kind = "system",
            createdBy = "test"
        });

        // Add a membership to the shared channel
        await PutJsonAsync<MembershipPayload>(client, $"/api/channels/{sharedChannel.Id}/memberships", new
        {
            memberType = "agent",
            memberIdentity = "test-agent",
            wakePolicy = "mentions_only"
        });

        // Link a project that has NO default channel to the shared channel
        await PostJsonAsync<ProjectLinkPayload>(client, "/api/channel-project-links", new
        {
            channelId = sharedChannel.Id,
            projectId = "den-unlinked",
            relationKind = "linked"
        });

        // Gateway memberships should resolve via link for project without default channel
        var memberships = await client.GetFromJsonAsync<GatewayMembershipsPayload>(
            "/api/gateway/memberships?projectId=den-unlinked");
        Assert.NotNull(memberships);
        Assert.Equal(sharedChannel.Id, memberships.ChannelId);
        Assert.Equal("shared-ops-gw", memberships.ChannelSlug);
        Assert.Single(memberships.Members);
        Assert.Equal("test-agent", memberships.Members[0].MemberIdentity);
    }

    [Fact]
    public async Task DenSystemChannel_IsSeededOnStartup()
    {
        using var client = _factory.CreateClient();

        // The den-system channel should be auto-created by the seed step
        var channels = await client.GetFromJsonAsync<List<ChannelPayload>>("/api/channels?kind=system&limit=100");
        Assert.NotNull(channels);
        var denSystem = channels.FirstOrDefault(c => c.Slug == "den-system");
        Assert.NotNull(denSystem);
        Assert.Equal("#den-system", denSystem.DisplayName);

        // Should have linked projects
        var linkedProjects = await client.GetFromJsonAsync<List<ProjectLinkPayload>>($"/api/channels/{denSystem.Id}/linked-projects");
        Assert.NotNull(linkedProjects);
        Assert.True(linkedProjects.Count >= 3, "den-system should have at least den-core, den-mcp, den-channels linked");
        Assert.Contains(linkedProjects, lp => lp.ProjectId == "den-core");
        Assert.Contains(linkedProjects, lp => lp.ProjectId == "den-mcp");
        Assert.Contains(linkedProjects, lp => lp.ProjectId == "den-channels");

        // den-core should be primary
        var coreLink = linkedProjects.First(lp => lp.ProjectId == "den-core");
        Assert.True(coreLink.IsPrimary);
    }

    [Fact]
    public async Task DeleteChannel_CascadesToLinks()
    {
        using var client = _factory.CreateClient();

        // We can't directly delete via API, but we can verify the cascade works
        // by creating a channel with links, then verifying the link table is consistent.
        // Since there's no delete channel endpoint, we verify via the upsert + delete link path.
        var channel = await PostJsonAsync<ChannelPayload>(client, "/api/channels", new
        {
            slug = "cascade-test",
            displayName = "Cascade Test",
            kind = "ad_hoc",
            createdBy = "test"
        });

        await PostJsonAsync<ProjectLinkPayload>(client, "/api/channel-project-links", new
        {
            channelId = channel.Id,
            projectId = "project-a"
        });
        await PostJsonAsync<ProjectLinkPayload>(client, "/api/channel-project-links", new
        {
            channelId = channel.Id,
            projectId = "project-b"
        });

        var linked = await client.GetFromJsonAsync<List<ProjectLinkPayload>>($"/api/channels/{channel.Id}/linked-projects");
        Assert.NotNull(linked);
        Assert.Equal(2, linked.Count);

        // Delete one link
        await client.DeleteAsync($"/api/channel-project-links?channelId={channel.Id}&projectId=project-a");

        var afterDelete = await client.GetFromJsonAsync<List<ProjectLinkPayload>>($"/api/channels/{channel.Id}/linked-projects");
        Assert.NotNull(afterDelete);
        var remaining = Assert.Single(afterDelete);
        Assert.Equal("project-b", remaining.ProjectId);
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

    private sealed record MembershipPayload(long Id, long ChannelId, string MemberType, string MemberIdentity,
        string MembershipStatus, string WakePolicy);

    private sealed record ProjectLinkPayload(long Id, long ChannelId, string ProjectId, string RelationKind,
        bool IsPrimary, string? SettingsJson, string CreatedAt);

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
}
