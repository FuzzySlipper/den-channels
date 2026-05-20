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
    public void WorkspaceNavigation_DistinguishesAllAggregateFromConcreteGlobalScope()
    {
        var app = ReadClientSource("App.tsx");
        var sidebar = ReadClientSource("components", "ProjectSidebar.tsx");
        var component = ReadClientSource("components", "ChannelChatPanel.tsx");

        Assert.Contains("ALL_SPACES_ID = '_all'", app);
        Assert.Contains("name: 'All spaces'", app);
        Assert.Contains("description: 'Aggregate views across accessible spaces'", app);
        Assert.Contains("GLOBAL_SPACE_ID = '_global'", app);
        Assert.Contains("listDocuments(isAllSpaces ? undefined : effectiveSpaceId)", app);
        Assert.Contains("projectId={!isAggregateSpace && !isGlobal ? effectiveSpaceId : null}", app);

        Assert.Contains("if (space.id === '_all') return 'aggregate view';", sidebar);
        Assert.Contains("if (space.id === '_global') return 'global scope';", sidebar);
        Assert.DoesNotContain("if (space.id === '_global') return 'all spaces';", sidebar);

        Assert.Contains("resolveAgentCommonsChannel", component);
        Assert.Contains("return existing ?? ensureAgentCommonsChannel();", component);
        Assert.Contains("if (!projectId) return [agentCommons];", component);
        Assert.Contains("previousProjectIdRef", component);
        Assert.Contains("pendingProjectDefaultSelectionRef", component);
        Assert.Contains("pendingProjectDefaultSelectionRef.current === projectId", component);
        Assert.Contains("setSelectedChannelId(projectDefaultChannel.id)", component);
        Assert.Contains("preferredDefaultChannel(availableChannels, projectId)?.id", component);
        Assert.DoesNotContain("Select a project to chat", component);
        Assert.DoesNotContain("Select a project space to join its default channel.", component);
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
        Assert.Contains("channel-chat-members-panel", component);
        Assert.Contains("channel-chat-debug-panel", component);
        Assert.Contains("Wake debug", component);
        Assert.Contains("Join agent", component);
        Assert.Contains("Direct message", component);
        Assert.Contains("direct_agent_message", component);
        Assert.Contains("Gateway message evidence", component);
        Assert.Contains("Gateway events evidence", component);
        Assert.Contains("claim {lastDirectResult.claimStatus}", component);
        Assert.Contains("Test wake selected", component);
        Assert.Contains("channel-chat-body-region", css);
        Assert.Contains("grid-template-columns: minmax(0, 4fr) minmax(220px, 1fr);", css);
        Assert.Contains("channel-chat-members-list", css);
        Assert.Contains(".channel-chat-members-panel", css);
        Assert.Contains(".channel-chat-debug-panel", css);
        Assert.Contains("grid-template-rows: minmax(0, 1fr) minmax(150px, 0.72fr);", css);
        Assert.Contains("overflow-y: auto", css);
        Assert.Contains("channel-chat-delivery-status", css);
    }

    [Fact]
    public void ChannelChatPanel_OffersSmallMediumLargeSizeModesForReadableParticipants()
    {
        var app = ReadClientSource("App.tsx");
        var component = ReadClientSource("components", "ChannelChatPanel.tsx");
        var css = ReadClientSource("styles", "index.css");

        Assert.Contains("channelPanelSize", app);
        Assert.Contains("dashboard-channel-size-", app);
        Assert.Contains("panelSize={channelPanelSize}", app);
        Assert.Contains("onPanelSizeChange={setChannelPanelSize}", app);

        Assert.Contains("aria-label=\"Channel panel size\"", component);
        Assert.Contains("channel-chat-size-controls", component);
        Assert.Contains("channel-chat-size-button", component);
        Assert.Contains("Small", component);
        Assert.Contains("Medium", component);
        Assert.Contains("Large", component);

        Assert.Contains(".dashboard-channel-size-small", css);
        Assert.Contains(".dashboard-channel-size-medium", css);
        Assert.Contains(".dashboard-channel-size-large", css);
        Assert.Contains(".channel-chat-panel-size-small .channel-chat-body-region", css);
        Assert.Contains(".channel-chat-panel-size-large .channel-chat-body-region", css);
    }

    [Fact]
    public void ChannelChatPanel_OffersExplicitChannelAndDirectAgentSendModes()
    {
        var component = ReadClientSource("components", "ChannelChatPanel.tsx");

        Assert.Contains("sendMode", component);
        Assert.Contains("channel-chat-send-mode", component);
        Assert.Contains("aria-label=\"Send mode\"", component);
        Assert.Contains("<option value=\"channel\">Channel</option>", component);
        Assert.Contains("<option value=\"direct\">Direct agent</option>", component);
        Assert.Contains("sendMode === 'direct' && selectedTarget", component);
        Assert.Contains("sendMode === 'channel'", component);
    }

    [Fact]
    public void ChannelChatPanel_ShowsWakeProgressAndAutoscrollControls()
    {
        var component = ReadClientSource("components", "ChannelChatPanel.tsx");
        var css = ReadClientSource("styles", "index.css");

        Assert.Contains("channel-chat-auto-scroll", component);
        Assert.Contains("Auto-scroll", component);
        Assert.Contains("scrollIntoView", component);
        Assert.Contains("channel-chat-scroll-anchor", component);

        Assert.Contains("deriveWakeProgress", component);
        Assert.Contains("channel-chat-wake-progress", component);
        Assert.Contains("Agent wake recorded", component);
        Assert.Contains("Agent is preparing a reply", component);
        Assert.Contains("Reply posted", component);
        Assert.Contains(".channel-chat-wake-progress", css);
        Assert.Contains(".channel-chat-auto-scroll", css);
    }

    [Fact]
    public void ChannelChatPanel_ShowsParticipantWorkingStateWhileReplyPending()
    {
        var component = ReadClientSource("components", "ChannelChatPanel.tsx");
        var css = ReadClientSource("styles", "index.css");

        Assert.Contains("deriveParticipantActivity", component);
        Assert.Contains("memberActivityByIdentity", component);
        Assert.Contains("channel-chat-member-working", component);
        Assert.Contains("working", component);
        Assert.Contains("all_human_messages", component);
        Assert.Contains("all_messages_except_self", component);
        Assert.Contains(".channel-chat-member-working", css);
        Assert.Contains(".member-activity-working", css);
    }

    [Fact]
    public void ChannelChatPanel_TreatsGatewayDeliveryAsAgentReplyEvidenceDuringCutover()
    {
        var component = ReadClientSource("components", "ChannelChatPanel.tsx");

        Assert.Contains("candidate.sourceKind === 'gateway_delivery'", component);
        Assert.Contains("candidate.sourceKind === 'external_adapter_message'", component);
    }


    [Fact]
    public void MessagesView_IsFirstClassWorkspaceTabAndSeparatesHumanMessagesFromActivity()
    {
        Assert.True(ClientFileExists("components", "MessagesInbox.tsx"));
        var filterBar = ReadClientSource("components", "FilterBar.tsx");
        var app = ReadClientSource("App.tsx");
        var component = ReadClientSource("components", "MessagesInbox.tsx");
        var css = ReadClientSource("styles", "index.css");

        Assert.Contains("'messages'", filterBar);
        Assert.Contains("onViewModeChange('messages')", filterBar);
        Assert.Contains("Messages", filterBar);
        Assert.Contains("MessagesInbox", app);
        Assert.Contains("viewMode === 'messages'", app);
        Assert.Contains("getMessages", component);
        Assert.Contains("User-directed", component);
        Assert.Contains("project-level and task-attached messages", component);
        Assert.Contains("separate from channel chat and agent activity breadcrumbs", component);
        Assert.Contains("messages-inbox-user-chip", component);
        Assert.Contains("messages-inbox-urgency-high", css);
        Assert.DoesNotContain("listAgentStream", component);
        Assert.DoesNotContain("channel_activity_events", component);
    }

    [Fact]
    public void SessionsView_IsTopLevelWorkspaceTab_AndKeepsCompactChannelChatPanel()
    {
        var app = ReadClientSource("App.tsx");
        var filterBar = ReadClientSource("components", "FilterBar.tsx");

        Assert.Contains("'sessions'", filterBar);
        Assert.Contains("onViewModeChange('sessions')", filterBar);
        Assert.Contains("Sessions", filterBar);

        Assert.Contains("FocusedSessionView", app);
        Assert.Contains("viewMode === 'sessions'", app);
        Assert.Contains("<FocusedSessionView", app);
        Assert.Contains("<ChannelChatPanel", app);
    }

    [Fact]
    public void FocusedSessionView_UsesDurableChannelSessionApiSeamAndAffordances()
    {
        Assert.True(ClientFileExists("components", "FocusedSessionView.tsx"));
        var component = ReadClientSource("components", "FocusedSessionView.tsx");

        Assert.Contains("listChannels", component);
        Assert.Contains("ensureProjectDefaultChannel", component);
        Assert.Contains("listChannelMessages", component);
        Assert.Contains("postChannelMessage", component);
        Assert.Contains("listGatewayMemberships", component);
        Assert.Contains("listDesktopSessionSnapshots", component);
        Assert.Contains("listDesktopSessionEvents", component);

        Assert.Contains("focused-session-view", component);
        Assert.Contains("focused-session-selector", component);
        Assert.Contains("Live sessions", component);
        Assert.Contains("Recent sessions", component);
        Assert.Contains("Connected transcript", component);
        Assert.Contains("Conversation", component);
        Assert.Contains("Workflow evidence", component);
        Assert.Contains("Command/result", component);
        Assert.Contains("Status evidence", component);
        Assert.Contains("Tool evidence", component);
        Assert.Contains("Den context", component);
        Assert.Contains("Posting as", component);
        Assert.Contains("Slash commands", component);
        Assert.Contains("Direct agent target", component);
        Assert.Contains("/new", component);
        Assert.Contains("senderIdentity", component);
        Assert.Contains("messageKind: body.startsWith('/') ? 'command' : 'human_text'", component);
        Assert.Contains("<option value=\"\">Channel lane</option>", component);
        Assert.Contains("targetMemberIdentity && !activeAgentMembers.some", component);
        Assert.Contains("selectedChannelLane", component);
        Assert.Contains("listChannelMessages(activeChannel.id", component);
        Assert.Contains("listGatewayMemberships({ channelId: activeChannel.id })", component);
        Assert.Contains("safeEvidenceLink", component);
        Assert.Contains("/api/gateway/messages/${message.id}", component);
        Assert.DoesNotContain("/api/channels/messages/${message.id}", component);
        Assert.DoesNotContain("messageKind: body.startsWith('/') ? 'slash_command'", component);
        Assert.DoesNotContain("sourceKind: 'focused_session_view'", component);
        Assert.DoesNotContain("postGatewayDirectAgentMessage", component);
        Assert.DoesNotContain("attach", component, StringComparison.OrdinalIgnoreCase);
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
