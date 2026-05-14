namespace DenChannels.Service.Tests;

public sealed class DenWebChannelFirstLayoutTests
{
    private static readonly string RepoRoot = LocateRepoRoot();
    private static readonly string ClientSrc = Path.Combine(RepoRoot, "src", "DenChannels.Service", "ClientApp", "src");

    [Fact]
    public void AgentStream_IsTopLevelWorkspaceTab_NotAlwaysVisibleHeaderFeed()
    {
        var app = ReadClientSource("App.tsx");
        var filterBar = ReadClientSource("components", "FilterBar.tsx");

        Assert.Contains("'agent-stream'", filterBar);
        Assert.Contains("onViewModeChange('agent-stream')", filterBar);
        Assert.Contains("Agent Stream", filterBar);

        Assert.Contains("viewMode === 'agent-stream'", app);
        Assert.Contains("<AgentStreamFeed", app);
        Assert.DoesNotContain("panel panel-messages", app);
    }

    [Fact]
    public void Dashboard_ReservesBottomRowForAlwaysVisibleChannelChatPanel()
    {
        var app = ReadClientSource("App.tsx");
        var css = ReadClientSource("styles", "index.css");

        Assert.Contains("<ChannelChatPanel", app);
        Assert.Contains("className=\"dashboard-workspace\"", app);
        Assert.Contains(".channel-chat-panel", css);
        Assert.Contains(".dashboard-workspace", css);
        Assert.DoesNotContain(".panel-messages", css);
    }

    [Fact]
    public void ChannelChatPanel_UsesDenChannelsApiSeam_NotLegacyDispatchOrAgentStreamTransport()
    {
        var client = ReadClientSource("api", "client.ts");
        var component = ReadClientSource("components", "ChannelChatPanel.tsx");

        Assert.Contains("denChannelsApiBase", client);
        Assert.Contains("listChannels", client);
        Assert.Contains("ensureProjectDefaultChannel", client);
        Assert.Contains("listChannelMessages", client);
        Assert.Contains("postChannelMessage", client);
        Assert.Contains("listGatewayMemberships", client);
        Assert.Contains("upsertChannelMembership", client);
        Assert.Contains("postGatewayTestWake", client);

        Assert.Contains("ensureProjectDefaultChannel", component);
        Assert.Contains("listChannelMessages", component);
        Assert.Contains("postChannelMessage", component);
        Assert.Contains("listGatewayMemberships", component);
        Assert.Contains("upsertChannelMembership", component);
        Assert.Contains("postGatewayTestWake", component);
        Assert.Contains("availableChannels.find(candidate => candidate.id === selectedChannelId)", component);
        Assert.DoesNotContain("listAgentStream", component);
        Assert.DoesNotContain("dispatch", component, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChannelChatPanel_UsesOperatorIdentitySeam_NotHardcodedWebUiSender()
    {
        var component = ReadClientSource("components", "ChannelChatPanel.tsx");

        var legacySingleQuotedSender = "senderIdentity: '" + "web-ui'";
        var legacyDoubleQuotedSender = "senderIdentity: \"" + "web-ui\"";

        Assert.Contains("den-channel-sender-identity", component);
        Assert.Contains("channel-chat-identity", component);
        Assert.Contains("senderIdentity", component);
        Assert.DoesNotContain(legacySingleQuotedSender, component);
        Assert.DoesNotContain(legacyDoubleQuotedSender, component);
    }

    [Fact]
    public void ChannelChatPanel_ExposesChannelPickerParticipantsAgentJoinDirectMessagesAndTestWake()
    {
        var component = ReadClientSource("components", "ChannelChatPanel.tsx");
        var css = ReadClientSource("styles", "index.css");

        Assert.Contains("channel-chat-selector", component);
        Assert.Contains("setSelectedChannelId", component);
        Assert.Contains("channel-chat-members", component);
        Assert.Contains("Join agent", component);
        Assert.Contains("Direct message", component);
        Assert.Contains("direct_agent_message", component);
        Assert.Contains("Gateway message evidence", component);
        Assert.Contains("Gateway events evidence", component);
        Assert.Contains("claim {lastDirectResult.claimStatus}", component);
        Assert.Contains("Test wake selected", component);
        Assert.Contains("channel-chat-body-region", css);
        Assert.Contains("channel-chat-members-list", css);
        Assert.Contains("channel-chat-delivery-status", css);
    }

    [Fact]
    public void GatewayMembershipUiTypes_DoNotExposeRawSettingsJsonPreview()
    {
        var component = ReadClientSource("components", "ChannelChatPanel.tsx");
        var types = ReadClientSource("api", "types.ts");

        Assert.DoesNotContain("settingsJsonPreview", component);
        Assert.DoesNotContain("settingsJsonPreview", types);
        Assert.Contains("settingsLabel", component);
        Assert.Contains("settingsLabel", types);
    }

    [Fact]
    public void RetiredMiddleRowFeedCode_IsRemovedAfterChannelFirstRefactor()
    {
        Assert.False(ClientFileExists("components", "AgentBar.tsx"));
        Assert.False(ClientFileExists("components", "SubagentRunPanel.tsx"));
        Assert.False(ClientFileExists("components", "ThoughtFeed.tsx"));
        Assert.False(ClientFileExists("components", "MessageFeed.tsx"));
        Assert.False(ClientFileExists("thoughts.ts"));
        Assert.False(ClientFileExists("hooks", "useEventSourceRefresh.ts"));

        var client = ReadClientSource("api", "client.ts");
        var types = ReadClientSource("api", "types.ts");

        Assert.DoesNotContain("getMessageFeed", client);
        Assert.DoesNotContain("listActiveAgents", client);
        Assert.DoesNotContain("subagentRunEventsUrl", client);
        Assert.DoesNotContain("MessageFeedItem", types);
        Assert.DoesNotContain("AgentSession", types);

        // TaskDetail/SubagentRunDetail still use these for drill-in overlays.
        Assert.Contains("listSubagentRuns", client);
        Assert.Contains("getSubagentRun", client);
    }

    private static string ReadClientSource(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine(new[] { ClientSrc }.Concat(relativeParts).ToArray()));

    private static bool ClientFileExists(params string[] relativeParts) =>
        File.Exists(Path.Combine(new[] { ClientSrc }.Concat(relativeParts).ToArray()));

    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "DenChannels.Service", "ClientApp", "src", "App.tsx");
            if (File.Exists(candidate))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate den-channels repository root from test output directory.");
    }
}
