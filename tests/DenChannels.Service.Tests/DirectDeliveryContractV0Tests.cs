using DenChannels.Service.Channels;
using DenChannels.Service.DirectAgentEvents;
using DenChannels.Service.Gateway;

namespace DenChannels.Service.Tests;

/// <summary>
/// Contract tests for Direct Delivery / Channels Operations Contract v0 (task #1848).
/// Asserts that named constants match their expected string values and that
/// enumerated values are complete and stable.
/// </summary>
public sealed class DirectDeliveryContractV0Tests
{
    // -------------------------------------------------------------------------
    // Delivery status vocabulary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(DeliveryStatus.RecordedNotClaimedYet, "recorded_but_not_claimed_yet")]
    [InlineData(DeliveryStatus.Enqueued, "enqueued")]
    [InlineData(DeliveryStatus.Claimed, "claimed")]
    [InlineData(DeliveryStatus.Received, "received")]
    [InlineData(DeliveryStatus.Acknowledged, "acknowledged")]
    [InlineData(DeliveryStatus.Completed, "completed")]
    [InlineData(DeliveryStatus.Suppressed, "suppressed")]
    [InlineData(DeliveryStatus.Failed, "failed")]
    [InlineData(DeliveryStatus.Expired, "expired")]
    public void DeliveryStatus_Constants_MatchExpectedValues(string constant, string expected)
    {
        Assert.Equal(expected, constant);
    }

    [Fact]
    public void DeliveryStatus_All_ContainsExactlyNineValues()
    {
        Assert.Equal(9, DeliveryStatus.All.Length);
        Assert.All(DeliveryStatus.All, Assert.NotEmpty);
        Assert.Equal(DeliveryStatus.All, DeliveryStatus.All.Distinct().ToArray());
    }

    // -------------------------------------------------------------------------
    // Claim status vocabulary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(ClaimStatus.Unclaimed, "unclaimed")]
    [InlineData(ClaimStatus.Claimed, "claimed")]
    public void ClaimStatus_Constants_MatchExpectedValues(string constant, string expected)
    {
        Assert.Equal(expected, constant);
    }

    [Fact]
    public void ClaimStatus_All_ContainsExactlyTwoValues()
    {
        Assert.Equal(2, ClaimStatus.All.Length);
    }

    // -------------------------------------------------------------------------
    // Completion status vocabulary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(CompletionStatus.Pending, "pending")]
    [InlineData(CompletionStatus.Completed, "completed")]
    [InlineData(CompletionStatus.Failed, "failed")]
    [InlineData(CompletionStatus.Expired, "expired")]
    [InlineData(CompletionStatus.Suppressed, "suppressed")]
    public void CompletionStatus_Constants_MatchExpectedValues(string constant, string expected)
    {
        Assert.Equal(expected, constant);
    }

    [Fact]
    public void CompletionStatus_All_ContainsExactlyFiveValues()
    {
        Assert.Equal(5, CompletionStatus.All.Length);
    }

    // -------------------------------------------------------------------------
    // Suppression status vocabulary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(SuppressionStatus.NotSuppressed, "not_suppressed")]
    [InlineData(SuppressionStatus.Suppressed, "suppressed")]
    public void SuppressionStatus_Constants_MatchExpectedValues(string constant, string expected)
    {
        Assert.Equal(expected, constant);
    }

    // -------------------------------------------------------------------------
    // Wait-for target vocabulary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(WaitForTarget.None, "none")]
    [InlineData(WaitForTarget.Claim, "claim")]
    [InlineData(WaitForTarget.Ack, "ack")]
    [InlineData(WaitForTarget.Completion, "completion")]
    public void WaitForTarget_Constants_MatchExpectedValues(string constant, string expected)
    {
        Assert.Equal(expected, constant);
    }

    // -------------------------------------------------------------------------
    // Message kind vocabulary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(MessageKind.HumanText, "human_text")]
    [InlineData(MessageKind.AgentText, "agent_text")]
    [InlineData(MessageKind.SystemEvent, "system_event")]
    [InlineData(MessageKind.MirrorSummary, "mirror_summary")]
    [InlineData(MessageKind.Command, "command")]
    [InlineData(MessageKind.CommandResult, "command_result")]
    public void MessageKind_Constants_MatchExpectedValues(string constant, string expected)
    {
        Assert.Equal(expected, constant);
    }

    [Fact]
    public void MessageKind_All_ContainsExactlySixValues()
    {
        Assert.Equal(6, MessageKind.All.Length);
    }

    // -------------------------------------------------------------------------
    // Source kind vocabulary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(SourceKind.WakeEvent, "wake_event")]
    [InlineData(SourceKind.GatewayDelivery, "gateway_delivery")]
    [InlineData(SourceKind.TaskMessage, "task_message")]
    [InlineData(SourceKind.AgentStreamEntry, "agent_stream_entry")]
    [InlineData(SourceKind.Notification, "notification")]
    [InlineData(SourceKind.WorkerRun, "worker_run")]
    [InlineData(SourceKind.ReviewRound, "review_round")]
    [InlineData(SourceKind.ReviewFinding, "review_finding")]
    [InlineData(SourceKind.ExternalAdapterMessage, "external_adapter_message")]
    public void SourceKind_Constants_MatchExpectedValues(string constant, string expected)
    {
        Assert.Equal(expected, constant);
    }

    // -------------------------------------------------------------------------
    // Wake policy vocabulary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(WakePolicy.AllMessages, "all_messages")]
    [InlineData(WakePolicy.AllMessagesExceptSelf, "all_messages_except_self")]
    [InlineData(WakePolicy.AllHumanMessages, "all_human_messages")]
    [InlineData(WakePolicy.DirectQuestionsOnly, "direct_questions_only")]
    [InlineData(WakePolicy.MentionsOnly, "mentions_only")]
    [InlineData(WakePolicy.Never, "never")]
    public void WakePolicy_Constants_MatchExpectedValues(string constant, string expected)
    {
        Assert.Equal(expected, constant);
    }

    [Fact]
    public void WakePolicy_All_ContainsExactlySixValues()
    {
        Assert.Equal(6, WakePolicy.All.Length);
    }

    // -------------------------------------------------------------------------
    // Member type vocabulary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(MemberType.Agent, "agent")]
    [InlineData(MemberType.User, "user")]
    [InlineData(MemberType.System, "system")]
    public void MemberType_Constants_MatchExpectedValues(string constant, string expected)
    {
        Assert.Equal(expected, constant);
    }

    // -------------------------------------------------------------------------
    // Membership status vocabulary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(MembershipStatus.Active, "active")]
    [InlineData(MembershipStatus.Muted, "muted")]
    [InlineData(MembershipStatus.Left, "left")]
    public void MembershipStatus_Constants_MatchExpectedValues(string constant, string expected)
    {
        Assert.Equal(expected, constant);
    }

    // -------------------------------------------------------------------------
    // Channel kind vocabulary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(ChannelKind.ProjectDefault, "project_default")]
    [InlineData(ChannelKind.AdHoc, "ad_hoc")]
    [InlineData(ChannelKind.System, "system")]
    [InlineData(ChannelKind.WorkerPoolLobby, "worker_pool_lobby")]
    public void ChannelKind_Constants_MatchExpectedValues(string constant, string expected)
    {
        Assert.Equal(expected, constant);
    }

    // -------------------------------------------------------------------------
    // Sender type vocabulary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(SenderType.User, "user")]
    [InlineData(SenderType.Agent, "agent")]
    [InlineData(SenderType.System, "system")]
    public void SenderType_Constants_MatchExpectedValues(string constant, string expected)
    {
        Assert.Equal(expected, constant);
    }

    // -------------------------------------------------------------------------
    // Worker-pool lobby status vocabulary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(WorkerPoolLobbyStatus.Idle, "idle")]
    [InlineData(WorkerPoolLobbyStatus.Leased, "leased")]
    [InlineData(WorkerPoolLobbyStatus.Draining, "draining")]
    [InlineData(WorkerPoolLobbyStatus.Released, "released")]
    [InlineData(WorkerPoolLobbyStatus.Quarantined, "quarantined")]
    [InlineData(WorkerPoolLobbyStatus.Offline, "offline")]
    public void WorkerPoolLobbyStatus_Constants_MatchExpectedValues(string constant, string expected)
    {
        Assert.Equal(expected, constant);
    }

    // -------------------------------------------------------------------------
    // Event recording status
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(EventRecordingStatus.Recorded, "recorded")]
    public void EventRecordingStatus_Constants_MatchExpectedValues(string constant, string expected)
    {
        Assert.Equal(expected, constant);
    }

    // -------------------------------------------------------------------------
    // Trace source availability
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(TraceSourceAvailability.Available, "available")]
    [InlineData(TraceSourceAvailability.CoreUnavailable, "core_unavailable")]
    [InlineData(TraceSourceAvailability.GatewayUnavailable, "gateway_unavailable")]
    [InlineData(TraceSourceAvailability.NoAssignmentMessages, "no_assignment_messages")]
    [InlineData(TraceSourceAvailability.NoActivityEvents, "no_activity_events")]
    [InlineData(TraceSourceAvailability.DeliveryMissing, "delivery_missing")]
    [InlineData(TraceSourceAvailability.Pending, "pending")]
    public void TraceSourceAvailability_Constants_MatchExpectedValues(string constant, string expected)
    {
        Assert.Equal(expected, constant);
    }

    [Fact]
    public void TraceSourceAvailability_All_ContainsExactlySevenValues()
    {
        Assert.Equal(7, TraceSourceAvailability.All.Length);
    }

    // -------------------------------------------------------------------------
    // DTO field name assertions (cross-boundary field presence)
    // -------------------------------------------------------------------------

    [Fact]
    public void GatewayDirectAgentMessageDto_HasAllTargetWorkFields()
    {
        // Assert that the DTO type exists and has the expected property shapes
        var dto = new GatewayDirectAgentMessageDto(
            Status: "recorded",
            DeliveryStatus: "recorded_but_not_claimed_yet",
            ClaimStatus: "unclaimed",
            CompletionStatus: "pending",
            SuppressionStatus: "not_suppressed",
            MemberIdentity: "test-agent",
            WakePolicy: "direct_questions_only",
            MessageId: 1,
            ChannelId: 1,
            RequestId: "test",
            SourceProjectId: "src-proj",
            TargetProjectId: "target-proj",
            TargetTaskId: 1848,
            AssignmentId: "149",
            WorkerRunId: "dc-1848-run",
            WorkerRole: "coder",
            ProfileIdentity: "den-hermes-coder",
            PoolMemberId: "pool-149",
            AgentInstanceId: "inst-149",
            SessionOwnerId: "runner-149",
            SessionId: "session-149",
            DeliveryRequestId: null,
            AttemptId: null,
            GatewayDeliveryState: null,
            GatewayAttemptStatus: null,
            TimedOut: false,
            GatewayUnavailable: false,
            GatewayMessageUrl: "/api/gateway/messages/1",
            GatewayEventsUrl: "/api/gateway/events",
            EvidenceSummary: "test");

        // Source context fields
        Assert.Equal("src-proj", dto.SourceProjectId);
        Assert.Equal(1, dto.ChannelId);

        // Target-work attribution fields (#1845)
        Assert.Equal("target-proj", dto.TargetProjectId);
        Assert.Equal(1848, dto.TargetTaskId);
        Assert.Equal("149", dto.AssignmentId);
        Assert.Equal("dc-1848-run", dto.WorkerRunId);
        Assert.Equal("coder", dto.WorkerRole);
        Assert.Equal("den-hermes-coder", dto.ProfileIdentity);
        Assert.Equal("pool-149", dto.PoolMemberId);

        // Session-owner fields (#1887)
        Assert.Equal("inst-149", dto.AgentInstanceId);
        Assert.Equal("runner-149", dto.SessionOwnerId);
        Assert.Equal("session-149", dto.SessionId);

        // Delivery/checkpoint visibility
        Assert.Equal("recorded_but_not_claimed_yet", dto.DeliveryStatus);
        Assert.Equal("unclaimed", dto.ClaimStatus);
        Assert.Equal("pending", dto.CompletionStatus);
        Assert.Equal("not_suppressed", dto.SuppressionStatus);
    }

    [Fact]
    public void DirectAgentEventDto_HasAllTargetWorkFields()
    {
        var dto = new DirectAgentEventDto(
            Status: "recorded",
            EventId: 1,
            ChannelId: 1,
            RequestId: "test",
            MemberIdentity: "test-agent",
            WakePolicy: "direct_questions_only",
            SourceProjectId: "src-proj",
            TargetProjectId: "target-proj",
            TargetTaskId: 1848,
            AssignmentId: "149",
            WorkerRunId: "dc-1848-run",
            WorkerRole: "coder",
            ProfileIdentity: "den-hermes-coder",
            PoolMemberId: "pool-149",
            AgentInstanceId: "inst-149",
            SessionOwnerId: "runner-149",
            SessionId: "session-149",
            EventUrl: "/api/direct-agent-events/1",
            EventsUrl: "/api/gateway/events",
            EvidenceSummary: "test");

        // Source context
        Assert.Equal("src-proj", dto.SourceProjectId);

        // Target-work attribution
        Assert.Equal("target-proj", dto.TargetProjectId);
        Assert.Equal(1848, dto.TargetTaskId);
        Assert.Equal("149", dto.AssignmentId);
        Assert.Equal("dc-1848-run", dto.WorkerRunId);
        Assert.Equal("coder", dto.WorkerRole);
        Assert.Equal("den-hermes-coder", dto.ProfileIdentity);
        Assert.Equal("pool-149", dto.PoolMemberId);

        // Session-owner fields
        Assert.Equal("inst-149", dto.AgentInstanceId);
        Assert.Equal("runner-149", dto.SessionOwnerId);
        Assert.Equal("session-149", dto.SessionId);
    }

    [Fact]
    public void AssignmentTraceResponse_HasTypedChannelMessages()
    {
        // Verify the trace response uses typed DTOs, not opaque object
        var response = new AssignmentTraceResponse(
            AssignmentId: "149",
            ProjectId: "den-channels",
            ProjectName: null,
            TaskId: 1848,
            TaskTitle: null,
            AgentIdentity: "test-agent",
            WorkerRunId: "dc-1848-run",
            WorkerRole: "coder",
            CoreAvailability: "available",
            GatewayAvailability: "available",
            MessagesAvailability: "available",
            ActivityAvailability: "available",
            CoreState: null,
            GatewayEvidence: null,
            ChannelMessages: [],
            ActivityEvents: [],
            Summary: "test");

        Assert.IsType<AssignmentTraceResponse>(response);
        Assert.Empty(response.ChannelMessages);
        Assert.Empty(response.ActivityEvents);
        // ChannelMessages is IReadOnlyList<GatewayEventItemDto> not object
        Assert.IsAssignableFrom<IReadOnlyList<GatewayEventItemDto>>(response.ChannelMessages);
        Assert.IsAssignableFrom<IReadOnlyList<ChannelActivityEventDto>>(response.ActivityEvents);
    }

    [Fact]
    public void GatewayEventItemDto_HasAllTargetWorkFields()
    {
        var dto = new GatewayEventItemDto(
            Id: 1,
            ChannelId: 1,
            MessageKind: "human_text",
            SenderType: "user",
            SenderIdentity: "operator",
            SourceKind: "wake_event",
            SourceId: "test",
            SourceProjectId: "src-proj",
            TargetProjectId: "target-proj",
            TargetTaskId: 1848,
            AssignmentId: "149",
            WorkerRunId: "dc-1848-run",
            WorkerRole: "coder",
            ProfileIdentity: "den-hermes-coder",
            PoolMemberId: "pool-149",
            AgentInstanceId: "inst-149",
            SessionOwnerId: "runner-149",
            SessionId: "session-149",
            DeliveryRequestId: null,
            DedupeKey: null,
            DeepLink: null,
            Summary: null,
            Body: "test",
            CreatedAt: "2026-01-01T00:00:00Z");

        Assert.Equal("target-proj", dto.TargetProjectId);
        Assert.Equal(1848, dto.TargetTaskId);
        Assert.Equal("149", dto.AssignmentId);
        Assert.Equal("dc-1848-run", dto.WorkerRunId);
        Assert.Equal("coder", dto.WorkerRole);
        Assert.Equal("den-hermes-coder", dto.ProfileIdentity);
        Assert.Equal("pool-149", dto.PoolMemberId);
        Assert.Equal("inst-149", dto.AgentInstanceId);
        Assert.Equal("runner-149", dto.SessionOwnerId);
        Assert.Equal("session-149", dto.SessionId);
    }
}
