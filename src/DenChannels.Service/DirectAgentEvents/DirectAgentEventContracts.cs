namespace DenChannels.Service.DirectAgentEvents;

/// <summary>
/// Channels-owned request to record a durable direct-agent event.
/// This is the primary public API for direct-agent wake/recording.
/// SourceProjectId/ChannelId are the source/control context (where the interaction happened).
/// Target-work fields (TargetProjectId, TargetTaskId, AssignmentId, WorkerRunId, WorkerRole,
/// ProfileIdentity, PoolMemberId) are workflow attribution for the target work.
/// Session-owner fields (AgentInstanceId, SessionOwnerId, SessionId) identify the target durable
/// agent instance/session so consumers can reuse one active session across channels.
/// All target-work and session-owner fields are optional for backward compatibility.
/// </summary>
public sealed record RecordDirectAgentEventRequest(
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
    string? CheckpointHandle = null);

/// <summary>
/// Result of a Channels-owned direct-agent event recording.
/// Returns immediately with durable evidence; no Gateway spin-wait required.
/// The EventId is the channel_message.id and can be used for readback via
/// GET /api/direct-agent-events/{eventId}.
/// </summary>
public sealed record DirectAgentEventDto(
    string Status,
    long EventId,
    long ChannelId,
    string RequestId,
    string MemberIdentity,
    string WakePolicy,
    string? SourceProjectId,
    string? TargetProjectId,
    int? TargetTaskId,
    int? AssignmentId,
    string? WorkerRunId,
    string? WorkerRole,
    string? ProfileIdentity,
    string? PoolMemberId,
    string? AgentInstanceId,
    string? SessionOwnerId,
    string? SessionId,
    string EventUrl,
    string EventsUrl,
    string EvidenceSummary);

/// <summary>
/// Readback response for a single direct-agent event.
/// Contains the full message/event details for UI/agent consumption
/// without requiring Gateway internals.
/// </summary>
public sealed record DirectAgentEventReadbackDto(
    long EventId,
    long ChannelId,
    string RequestId,
    string MessageKind,
    string SenderType,
    string SenderIdentity,
    string MemberIdentity,
    string WakePolicy,
    string? SourceKind,
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
    string? Summary,
    string Body,
    string? DeliveryStatus,
    string? ClaimStatus,
    string? CompletionStatus,
    string CreatedAt);
