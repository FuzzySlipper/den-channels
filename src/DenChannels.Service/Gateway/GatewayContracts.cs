using DenChannels.Service.Channels;

namespace DenChannels.Service.Gateway;

using DS = DenChannels.Service.DeliveryStatus;
using CS = DenChannels.Service.ClaimStatus;
using CompS = DenChannels.Service.CompletionStatus;
using SupS = DenChannels.Service.SuppressionStatus;

/// <summary>
/// Bounded observation of delivery state for a Channels direct-agent message.
/// This is intentionally an observation, not final task completion truth.
/// </summary>
public sealed record DirectAgentDeliveryObservation(
    string DeliveryStatus,
    string ClaimStatus,
    string CompletionStatus,
    string SuppressionStatus,
    long? DeliveryRequestId = null,
    long? AttemptId = null,
    string? GatewayDeliveryState = null,
    string? GatewayAttemptStatus = null,
    string? EvidenceSummary = null,
    bool TimedOut = false,
    bool GatewayUnavailable = false)
{
    public static DirectAgentDeliveryObservation RecordedPending(string? evidenceSummary = null, bool gatewayUnavailable = false) =>
        new(
            DeliveryStatus: DS.RecordedNotClaimedYet,
            ClaimStatus: CS.Unclaimed,
            CompletionStatus: CompS.Pending,
            SuppressionStatus: SupS.NotSuppressed,
            EvidenceSummary: evidenceSummary ?? "Direct agent wake_event recorded; no delivery request/claim evidence observed yet.",
            GatewayUnavailable: gatewayUnavailable);

    public static DirectAgentDeliveryObservation Timeout(string? evidenceSummary = null) =>
        RecordedPending(evidenceSummary ?? "Direct agent wake_event recorded; timed out waiting for delivery claim evidence.") with { TimedOut = true };
}

/// <summary>Machine-readable Gateway dependency probe response.</summary>
public sealed record GatewayHealthDto(
    string Service,
    string Status,
    string[] Endpoints);

/// <summary>Channel membership/wake-policy snapshot for Gateway routing decisions.</summary>
public sealed record GatewayMembershipsDto(
    long ChannelId,
    string ChannelSlug,
    string ChannelKind,
    string? ProjectId,
    IReadOnlyList<GatewayMemberDto> Members);

/// <summary>Single channel member with bounded wake-policy fields.</summary>
public sealed record GatewayMemberDto(
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

/// <summary>Controlled low-risk wake probe request recorded through Gateway-visible channel state.</summary>
public sealed record PostGatewayTestWakeRequest(
    long? ChannelId,
    string? ProjectId,
    string MemberIdentity,
    string? RequestedBy,
    string? Note);

/// <summary>Result of a controlled wake probe recording with Gateway/Core evidence links.</summary>
public sealed record GatewayTestWakeDto(
    string Status,
    string MemberIdentity,
    string WakePolicy,
    long MessageId,
    long ChannelId,
    string GatewayMessageUrl,
    string GatewayEventsUrl,
    string EvidenceSummary);

/// <summary>
/// Request to record a direct targeted message as a Gateway-visible wake request.
/// SourceProjectId/ChannelId are the source/control context (where the interaction happened) —
/// these identify the transport origin and must never be treated as the agent session owner.
/// Target-work fields (TargetProjectId, TargetTaskId, AssignmentId, WorkerRunId, WorkerRole, ProfileIdentity, PoolMemberId)
/// are workflow attribution and must not be inferred only from the channel or source project.
/// Session-owner fields (AgentInstanceId, SessionOwnerId, SessionId) identify the target durable
/// agent instance/session so Bridge can reuse one active session across channels for that instance.
/// All target-work and session-owner fields are optional for backward compatibility.
/// </summary>
public sealed record PostGatewayDirectAgentMessageRequest(
    long? ChannelId,
    string? ProjectId,
    string MemberIdentity,
    string SenderIdentity,
    string Body,
    string? SourceProjectId = null,
    string? TargetProjectId = null,
    int? TargetTaskId = null,
    string? AssignmentId = null,
    string? WorkerRunId = null,
    string? WorkerRole = null,
    string? ProfileIdentity = null,
    string? PoolMemberId = null,
    string? AgentInstanceId = null,
    string? SessionOwnerId = null,
    string? SessionId = null,
    string? CheckpointType = null,
    string? CheckpointHandle = null,
    string? WaitFor = null,
    int? TimeoutMs = null);

/// <summary>
/// Result of a direct targeted message request with delivery/evidence status links.
/// SourceProjectId/ChannelId are the source/control context (where the interaction happened).
/// Target-work fields (TargetProjectId, TargetTaskId, WorkerRunId, WorkerRole, ProfileIdentity, PoolMemberId)
/// are workflow attribution and may differ from the transport/control project.
/// Session-owner fields (AgentInstanceId, SessionOwnerId, SessionId) identify the target durable
/// agent instance/session for session reuse across channels.
/// All target-work and session-owner fields are nullable for backward compatibility.
/// </summary>
public sealed record GatewayDirectAgentMessageDto(
    string Status,
    string DeliveryStatus,
    string ClaimStatus,
    string CompletionStatus,
    string SuppressionStatus,
    string MemberIdentity,
    string WakePolicy,
    long MessageId,
    long ChannelId,
    string RequestId,
    string? SourceProjectId,
    string? TargetProjectId,
    long? TargetTaskId,
    string? AssignmentId,
    string? WorkerRunId,
    string? WorkerRole,
    string? ProfileIdentity,
    string? PoolMemberId,
    string? AgentInstanceId,
    string? SessionOwnerId,
    string? SessionId,
    long? DeliveryRequestId,
    long? AttemptId,
    string? GatewayDeliveryState,
    string? GatewayAttemptStatus,
    bool TimedOut,
    bool GatewayUnavailable,
    string GatewayMessageUrl,
    string GatewayEventsUrl,
    string EvidenceSummary);

/// <summary>
/// Single channel message suitable for Gateway routing/simulation decisions.
/// SourceProjectId/ChannelId are the source/control context (transport attribution).
/// Target-work fields (TargetProjectId, TargetTaskId, WorkerRunId, WorkerRole, ProfileIdentity, PoolMemberId)
/// are workflow attribution for the target project work, not inferred from the channel project.
/// Session-owner fields (AgentInstanceId, SessionOwnerId, SessionId) identify the target durable
/// agent instance/session for session reuse across channels.
/// All target-work and session-owner fields are nullable for backward compatibility.
/// </summary>
public sealed record GatewayMessageDto(
    long Id,
    long ChannelId,
    string MessageKind,
    string SenderType,
    string SenderIdentity,
    string? SourceKind,
    string? SourceId,
    string? SourceProjectId,
    string? TargetProjectId,
    long? TargetTaskId,
    string? AssignmentId,
    string? WorkerRunId,
    string? WorkerRole,
    string? ProfileIdentity,
    string? PoolMemberId,
    string? AgentInstanceId,
    string? SessionOwnerId,
    string? SessionId,
    string? DeliveryRequestId,
    string? DedupeKey,
    string? DeepLink,
    string? Summary,
    string Body,
    string CreatedAt);

/// <summary>Cursor-paged event subscription response for Gateway consumption.</summary>
public sealed record GatewayEventsDto(
    IReadOnlyList<GatewayEventItemDto> Items,
    long? NextAfterId,
    bool HasMore);

/// <summary>
/// Single event item within the Gateway events cursor response.
/// SourceProjectId/ChannelId are the source/control context (transport attribution).
/// Target-work fields (TargetProjectId, TargetTaskId, WorkerRunId, WorkerRole, ProfileIdentity, PoolMemberId)
/// are workflow attribution, surfaced as structured fields rather than only body-embedded metadata.
/// Session-owner fields (AgentInstanceId, SessionOwnerId, SessionId) identify the target durable
/// agent instance/session for session reuse across channels.
/// All target-work and session-owner fields are nullable for backward compatibility.
/// </summary>
public sealed record GatewayEventItemDto(
    long Id,
    long ChannelId,
    string MessageKind,
    string SenderType,
    string SenderIdentity,
    string? SourceKind,
    string? SourceId,
    string? SourceProjectId,
    string? TargetProjectId,
    long? TargetTaskId,
    string? AssignmentId,
    string? WorkerRunId,
    string? WorkerRole,
    string? ProfileIdentity,
    string? PoolMemberId,
    string? AgentInstanceId,
    string? SessionOwnerId,
    string? SessionId,
    string? DeliveryRequestId,
    string? DedupeKey,
    string? DeepLink,
    string? Summary,
    string Body,
    string CreatedAt);

/// <summary>
/// Compatibility request for Gateway-generated channel messages.
/// With sourceKind=gateway_delivery and a final gateway-delivery dedupe key this is the gateway_delivery_final_message surface;
/// interim delivery progress must use channel activity events instead.
/// </summary>
public sealed record PostGatewaySystemMessageRequest(
    long? ChannelId,
    string? ProjectId,
    string? SenderIdentity,
    string? MessageKind,
    string Body,
    string? SourceKind,
    string? SourceId,
    string? SourceProjectId,
    string? Summary,
    string? DeepLink,
    string? MetadataJson,
    string? DeliveryRequestId,
    string? DedupeKey);

// =========================================================================
// Assignment trace aggregate (task #1737)
// =========================================================================

/// <summary>
/// Source availability signals for assignment trace sections.
/// </summary>
public static class TraceSourceAvailability
{
    public const string Available = "available";
    public const string CoreUnavailable = "core_unavailable";
    public const string GatewayUnavailable = "gateway_unavailable";
    public const string NoAssignmentMessages = "no_assignment_messages";
    public const string NoActivityEvents = "no_activity_events";
    public const string DeliveryMissing = "delivery_missing";
    public const string Pending = "pending";

    public static readonly string[] All =
    [
        Available, CoreUnavailable, GatewayUnavailable,
        NoAssignmentMessages, NoActivityEvents, DeliveryMissing, Pending
    ];
}

/// <summary>
/// Aggregated assignment trace response composed from Core worker-pool state,
/// Channels messages, and Gateway evidence (task #1737).
/// Den Web consumer: https://github.com/nousresearch/den-web/blob/main/src/api/gateway/types.ts
/// ChannelMessages and ActivityEvents are now typed DTOs (v0 contract hardening, task #1848).
/// </summary>
public sealed record AssignmentTraceResponse(
    string AssignmentId,
    string? ProjectId,
    string? ProjectName,
    long? TaskId,
    string? TaskTitle,
    string? AgentIdentity,
    string? WorkerRunId,
    string? WorkerRole,
    string CoreAvailability,
    string GatewayAvailability,
    string MessagesAvailability,
    string ActivityAvailability,
    AssignmentCoreStateDto? CoreState,
    AssignmentGatewayEvidenceDto? GatewayEvidence,
    IReadOnlyList<GatewayEventItemDto> ChannelMessages,
    IReadOnlyList<ChannelActivityEventDto> ActivityEvents,
    string? Summary);

/// <summary>Core worker-pool assignment state projected for trace display.</summary>
public sealed record AssignmentCoreStateDto(
    string? Phase,
    string? AssignedAt,
    string? AssignedAgent,
    string? LeaseAcquiredAt,
    string? LeaseExpiresAt,
    IReadOnlyList<AssignmentCheckpointDto>? Checkpoints,
    string? FinalStatus,
    string? FinalStatusAt,
    string? CleanupState,
    string? CleanupTriggeredAt,
    string? CleanupCompletedAt,
    string? ReleaseState,
    bool Quarantined,
    string? QuarantinedAt);

/// <summary>A single checkpoint in the assignment lifecycle.</summary>
public sealed record AssignmentCheckpointDto(
    int Sequence,
    string? CheckpointRequestAt,
    string? CheckpointResponseAt,
    string? Status,
    string? SnapshotPreview,
    string? Error);

/// <summary>Gateway delivery evidence for the assignment trace.</summary>
public sealed record AssignmentGatewayEvidenceDto(
    string? DeliveryRequestId,
    string? DeliveryStatus,
    string? ClaimStatus,
    string? CompletionStatus,
    string? SuppressionStatus,
    string? RequestedAt,
    string? DeliveredAt,
    string? ClaimedAt,
    string? CompletedAt,
    string? EvidenceSummary,
    string? GatewayMessageUrl,
    string? GatewayEventsUrl);
