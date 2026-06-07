// =========================================================================
// Direct Delivery / Channels Operations Contract v0
// Task #1848: Pin the cross-boundary vocabulary as named constants.
// Gateway* DTO names are retained for historical/compatibility reference.
// The Gateway compatibility alias routes were retired in task #2022 (410 Gone).
// Semantics are Direct Delivery / Channels operations, not Gateway-owned.
// =========================================================================

namespace DenChannels.Service;

// -------------------------------------------------------------------------
// Delivery status vocabulary (direct-agent events and delivery observation)
// -------------------------------------------------------------------------

/// <summary>
/// Operator-facing delivery status for a direct-agent event or delivery observation.
/// These values appear in <c>deliveryStatus</c> fields on response DTOs.
/// </summary>
public static class DeliveryStatus
{
    /// <summary>Channels recorded the wake_event, but no matching delivery/claim evidence was observed.</summary>
    public const string RecordedNotClaimedYet = "recorded_but_not_claimed_yet";

    /// <summary>Gateway created a delivery request, not yet claimed by target runtime.</summary>
    public const string Enqueued = "enqueued";

    /// <summary>Delivery request claimed by target runtime/adapter.</summary>
    public const string Claimed = "claimed";

    /// <summary>Target adapter reported the delivery as delivered/received.</summary>
    public const string Received = "received";

    /// <summary>Target runtime/session acknowledged accepting the prompt/wake.</summary>
    public const string Acknowledged = "acknowledged";

    /// <summary>Target runtime reported final delivery completion.</summary>
    public const string Completed = "completed";

    /// <summary>Gateway recorded a suppression decision.</summary>
    public const string Suppressed = "suppressed";

    /// <summary>Gateway recorded a terminal delivery failure.</summary>
    public const string Failed = "failed";

    /// <summary>Gateway recorded a terminal delivery expiry.</summary>
    public const string Expired = "expired";

    /// <summary>All known delivery status values for contract validation.</summary>
    public static readonly string[] All =
    [
        RecordedNotClaimedYet, Enqueued, Claimed, Received,
        Acknowledged, Completed, Suppressed, Failed, Expired
    ];
}

/// <summary>
/// Claim status for a direct-agent delivery observation.
/// Values: <c>unclaimed</c> or <c>claimed</c>.
/// </summary>
public static class ClaimStatus
{
    public const string Unclaimed = "unclaimed";
    public const string Claimed = "claimed";

    public static readonly string[] All = [Unclaimed, Claimed];
}

/// <summary>
/// Completion status for a direct-agent delivery observation.
/// Terminal values: <c>completed</c>, <c>failed</c>, <c>expired</c>, <c>suppressed</c>.
/// Non-terminal: <c>pending</c>.
/// </summary>
public static class CompletionStatus
{
    public const string Pending = "pending";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Expired = "expired";
    public const string Suppressed = "suppressed";

    public static readonly string[] All = [Pending, Completed, Failed, Expired, Suppressed];
}

/// <summary>
/// Suppression status for a direct-agent delivery observation.
/// Values: <c>not_suppressed</c> or <c>suppressed</c>.
/// </summary>
public static class SuppressionStatus
{
    public const string NotSuppressed = "not_suppressed";
    public const string Suppressed = "suppressed";

    public static readonly string[] All = [NotSuppressed, Suppressed];
}

// -------------------------------------------------------------------------
// Wait-for target vocabulary (direct-agent delivery wait controls)
// -------------------------------------------------------------------------

/// <summary>
/// Wait target vocabulary — historical only (task #2022).
/// The Channels-owned <c>/api/direct-agent-events</c> route never uses waitFor.
/// The <c>/api/gateway/direct-agent-messages</c> compatibility alias that used this
/// vocabulary was retired in task #2022 and returns 410 Gone.
/// </summary>
public static class WaitForTarget
{
    public const string None = "none";
    public const string Claim = "claim";
    public const string Ack = "ack";
    public const string Completion = "completion";

    public static readonly string[] All = [None, Claim, Ack, Completion];
}

// -------------------------------------------------------------------------
// Message kind vocabulary (channel_messages.message_kind)
// -------------------------------------------------------------------------

/// <summary>
/// Channel message kinds. Matches the SQLite CHECK constraint on channel_messages.message_kind.
/// </summary>
public static class MessageKind
{
    public const string HumanText = "human_text";
    public const string AgentText = "agent_text";
    public const string SystemEvent = "system_event";
    public const string MirrorSummary = "mirror_summary";
    public const string Command = "command";
    public const string CommandResult = "command_result";

    public static readonly string[] All =
    [
        HumanText, AgentText, SystemEvent, MirrorSummary, Command, CommandResult
    ];
}

// -------------------------------------------------------------------------
// Source kind vocabulary (channel_messages.source_kind)
// -------------------------------------------------------------------------

/// <summary>
/// Source kinds for channel messages, identifying the origin system/event type.
/// Values from the den-core-integration-contract communication-surface naming.
/// </summary>
public static class SourceKind
{
    public const string WakeEvent = "wake_event";
    public const string TaskMessage = "task_message";
    public const string AgentStreamEntry = "agent_stream_entry";
    public const string Notification = "notification";
    public const string WorkerRun = "worker_run";
    public const string ReviewRound = "review_round";
    public const string ReviewFinding = "review_finding";
    public const string ExternalAdapterMessage = "external_adapter_message";

    /// <summary>
    /// Historical/tombstone compatibility value. gateway_delivery rows are migrated
    /// to external_adapter_message; green-path code must not write this value.
    /// All compatibility readback goes through Compatibility/Gateway module.
    /// </summary>
    public const string GatewayDelivery = "gateway_delivery";

    public static readonly string[] All =
    [
        WakeEvent, TaskMessage, AgentStreamEntry,
        Notification, WorkerRun, ReviewRound, ReviewFinding, ExternalAdapterMessage
    ];
}

/// <summary>
/// Historical/tombstone source-kind values preserved for compatibility
/// readback only. Green-path code must not use these directly.
/// </summary>
public static class HistoricalSourceKind
{
    public static readonly string[] All =
    [
        SourceKind.GatewayDelivery
    ];
}

// -------------------------------------------------------------------------
// Wake policy vocabulary (channel_memberships.wake_policy)
// -------------------------------------------------------------------------

/// <summary>
/// Wake policy values controlling when an agent membership generates a wake pulse.
/// </summary>
public static class WakePolicy
{
    public const string AllMessages = "all_messages";
    public const string AllMessagesExceptSelf = "all_messages_except_self";
    public const string AllHumanMessages = "all_human_messages";
    public const string DirectQuestionsOnly = "direct_questions_only";
    public const string MentionsOnly = "mentions_only";
    public const string Never = "never";

    public static readonly string[] All =
    [
        AllMessages, AllMessagesExceptSelf, AllHumanMessages,
        DirectQuestionsOnly, MentionsOnly, Never
    ];
}

// -------------------------------------------------------------------------
// Member type vocabulary (channel_memberships.member_type)
// -------------------------------------------------------------------------

/// <summary>
/// Member type values for channel memberships.
/// </summary>
public static class MemberType
{
    public const string Agent = "agent";
    public const string User = "user";
    public const string System = "system";

    public static readonly string[] All = [Agent, User, System];
}

// -------------------------------------------------------------------------
// Membership status vocabulary (channel_memberships.membership_status)
// -------------------------------------------------------------------------

/// <summary>
/// Membership status values for channel memberships.
/// </summary>
public static class MembershipStatus
{
    public const string Active = "active";
    public const string Muted = "muted";
    public const string Left = "left";

    public static readonly string[] All = [Active, Muted, Left];
}

// -------------------------------------------------------------------------
// Channel kind vocabulary (channels.kind)
// -------------------------------------------------------------------------

/// <summary>
/// Channel kind values identifying the channel type.
/// </summary>
public static class ChannelKind
{
    public const string ProjectDefault = "project_default";
    public const string AdHoc = "ad_hoc";
    public const string System = "system";
    public const string WorkerPoolLobby = "worker_pool_lobby";

    public static readonly string[] All = [ProjectDefault, AdHoc, System, WorkerPoolLobby];
}

// -------------------------------------------------------------------------
// Sender type vocabulary (channel_messages.sender_type)
// -------------------------------------------------------------------------

/// <summary>
/// Sender type values for channel messages.
/// </summary>
public static class SenderType
{
    public const string User = "user";
    public const string Agent = "agent";
    public const string System = "system";

    public static readonly string[] All = [User, Agent, System];
}

// -------------------------------------------------------------------------
// Delivery mode vocabulary (metadata payload)
// -------------------------------------------------------------------------

/// <summary>
/// Delivery mode values that appear in direct-agent event metadata.
/// </summary>
public static class DeliveryMode
{
    public const string DirectAgentMessage = "direct_agent_message";

    public static readonly string[] All = [DirectAgentMessage];
}

// -------------------------------------------------------------------------
// Event recording status (top-level response status)
// -------------------------------------------------------------------------

/// <summary>
/// Top-level status for direct-agent event and wake recording responses.
/// </summary>
public static class EventRecordingStatus
{
    public const string Recorded = "recorded";

    public static readonly string[] All = [Recorded];
}

// -------------------------------------------------------------------------
// Worker-pool lobby presence status vocabulary
// -------------------------------------------------------------------------

/// <summary>
/// Status values for worker-pool lobby presence records.
/// Transitions: idle -> leased -> draining -> released -> idle.
/// Quarantined/offline are terminal statuses requiring Core intervention.
/// </summary>
public static class WorkerPoolLobbyStatus
{
    public const string Idle = "idle";
    public const string Leased = "leased";
    public const string Draining = "draining";
    public const string Released = "released";
    public const string Quarantined = "quarantined";
    public const string Offline = "offline";

    public static readonly string[] All = [Idle, Leased, Draining, Released, Quarantined, Offline];
}
