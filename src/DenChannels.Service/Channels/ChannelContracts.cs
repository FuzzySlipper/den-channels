namespace DenChannels.Service.Channels;

public sealed record ChannelDto(
    long Id,
    string Slug,
    string DisplayName,
    string Kind,
    string? ProjectId,
    string? SpaceId,
    string CreatedBy,
    string Visibility,
    string? SettingsJson,
    string CreatedAt,
    string UpdatedAt,
    string? ArchivedAt);

public sealed record CreateChannelRequest(
    string Slug,
    string DisplayName,
    string Kind,
    string? ProjectId,
    string? SpaceId,
    string? CreatedBy,
    string? Visibility,
    string? SettingsJson);

public sealed record EnsureProjectDefaultChannelRequest(
    string? DisplayName,
    string? CreatedBy,
    string? SettingsJson);

/// <summary>
/// A channel message with full attribution fields.
/// SourceProjectId is the source/control project (transport attribution).
/// TargetProjectId is the target work project (workflow attribution).
/// These may differ when e.g. a shared worker-control channel delivers work for a different project.
/// </summary>
public sealed record ChannelMessageDto(
    long Id,
    long ChannelId,
    string SenderType,
    string SenderIdentity,
    string Body,
    string MessageKind,
    string? SourceKind,
    string? SourceId,
    string? SourceProjectId,
    string? TargetProjectId,
    long? TargetTaskId,
    string? WorkerRunId,
    string? WorkerRole,
    string? ProfileIdentity,
    string? Summary,
    string? DeepLink,
    long? ThreadRootMessageId,
    long? ReplyToMessageId,
    string? MetadataJson,
    string? DeliveryRequestId,
    string? DedupeKey,
    string? AssignmentId,
    string? CheckpointType,
    string? CheckpointHandle,
    string? AgentInstanceId,
    string? PoolMemberId,
    string CreatedAt,
    string? EditedAt,
    string? DeletedAt);

/// <summary>
/// Request to post a new channel message.
/// SourceProjectId is the source/control project (transport attribution).
/// Target-work fields (TargetProjectId, TargetTaskId, WorkerRunId, WorkerRole, ProfileIdentity)
/// are workflow attribution and must not be inferred only from the channel project.
/// </summary>
public sealed record PostChannelMessageRequest(
    string SenderType,
    string SenderIdentity,
    string Body,
    string? MessageKind,
    string? SourceKind,
    string? SourceId,
    string? SourceProjectId,
    string? TargetProjectId = null,
    long? TargetTaskId = null,
    string? WorkerRunId = null,
    string? WorkerRole = null,
    string? ProfileIdentity = null,
    string? Summary = null,
    string? DeepLink = null,
    long? ThreadRootMessageId = null,
    long? ReplyToMessageId = null,
    string? MetadataJson = null,
    string? DeliveryRequestId = null,
    string? DedupeKey = null,
    string? AssignmentId = null,
    string? CheckpointType = null,
    string? CheckpointHandle = null,
    string? AgentInstanceId = null,
    string? PoolMemberId = null);

public sealed record ChannelMembershipDto(
    long Id,
    long ChannelId,
    string MemberType,
    string MemberIdentity,
    string MembershipStatus,
    string WakePolicy,
    bool CanSend,
    bool CanReact,
    bool CanInvite,
    int CooldownSeconds,
    int MaxAutoRepliesPerWindow,
    string? SettingsJson,
    string? MembershipPurpose,
    string CreatedAt,
    string UpdatedAt);

public sealed record UpsertChannelMembershipRequest(
    string MemberType,
    string MemberIdentity,
    string? MembershipStatus,
    string? WakePolicy,
    bool? CanSend,
    bool? CanReact,
    bool? CanInvite,
    int? CooldownSeconds,
    int? MaxAutoRepliesPerWindow,
    string? SettingsJson,
    string? MembershipPurpose = null);

public sealed record AgentCommonsBrakeRequest(
    string? MembershipStatus,
    string? WakePolicy,
    string? RequestedBy);

public sealed record AgentCommonsBrakeResultDto(
    string Status,
    long ChannelId,
    int UpdatedCount,
    string MembershipStatus,
    string WakePolicy);

public sealed record ChannelReactionDto(
    long Id,
    long ChannelMessageId,
    string ReactorType,
    string ReactorIdentity,
    string ReactionKey,
    string CreatedAt);

public sealed record ChannelReactionSummaryDto(
    long ChannelMessageId,
    string ReactionKey,
    int Count,
    IReadOnlyList<string> Reactors);

public sealed record AddChannelReactionRequest(
    string ReactorType,
    string ReactorIdentity,
    string ReactionKey);

public sealed record ChannelActivityEventDto(
    long Id,
    long ChannelId,
    string? ProjectId,
    string AgentIdentity,
    string? DeliveryRequestId,
    string? HermesSessionKey,
    string? DisplayBlockId,
    string? ParentHermesSessionKey,
    string? ParentAgentIdentity,
    string? WorkerRunId,
    string? WorkerRole,
    string? AgentInstanceId,
    string? PoolMemberId,
    long? TaskId,
    long? ThreadId,
    long? AnchorMessageId,
    string? AssignmentId,
    string? CheckpointType,
    string? CheckpointHandle,
    string EventType,
    string Status,
    string DeliveryStage,
    bool Terminal,
    long Sequence,
    long UpdateVersion,
    string? Title,
    string? Summary,
    string? PreviewJson,
    string? MetadataJson,
    string? DedupeKey,
    long? FinalChannelMessageId,
    string CreatedAt,
    string UpdatedAt);

public sealed record AppendChannelActivityEventRequest(
    string? ProjectId,
    string AgentIdentity,
    string? DeliveryRequestId,
    string? HermesSessionKey,
    string? DisplayBlockId,
    string? ParentHermesSessionKey,
    string? ParentAgentIdentity,
    string? WorkerRunId,
    string? WorkerRole,
    string? AgentInstanceId,
    string? PoolMemberId,
    long? TaskId,
    long? ThreadId,
    long? AnchorMessageId,
    string? AssignmentId,
    string? CheckpointType,
    string? CheckpointHandle,
    string EventType,
    string? Status,
    string? DeliveryStage,
    bool? Terminal,
    long? Sequence,
    string? Title,
    string? Summary,
    string? PreviewJson,
    string? MetadataJson,
    string? DedupeKey,
    long? FinalChannelMessageId);

public sealed record UpdateChannelActivityEventRequest(
    string? Status,
    string? DeliveryStage,
    bool? Terminal,
    string? Title,
    string? Summary,
    string? PreviewJson,
    string? MetadataJson,
    long? FinalChannelMessageId);

/// <summary>
/// Assignment-scoped transcript response: visible messages + non-waking activity/checkpoint events.
/// Consumer: Den Web #1729.
/// Field order matches JSON serialization for simple response shape.
/// </summary>
public sealed record AssignmentTranscriptResponse(
    string AssignmentId,
    IReadOnlyList<ChannelMessageDto> Messages,
    IReadOnlyList<ChannelActivityEventDto> ActivityEvents);

/// <summary>
/// Channel read cursor DTO tracking which concrete reader has read up to which message.
/// InstanceId enables multiple concrete agent instances sharing a profile identity
/// to maintain independent read positions.
/// </summary>
public sealed record ChannelReadCursorDto(
    long Id,
    long ChannelId,
    string ReaderType,
    string ReaderIdentity,
    string? InstanceId,
    long? LastReadChannelMessageId,
    string LastReadAt,
    string CreatedAt,
    string UpdatedAt);

/// <summary>
/// Request to upsert a channel read cursor with optional instance-level scoping.
/// </summary>
public sealed record UpsertChannelReadCursorRequest(
    string ReaderType,
    string ReaderIdentity,
    string? InstanceId,
    long? LastReadChannelMessageId);

// =========================================================================
// Worker-pool lobby presence DTOs (task #1771)
// =========================================================================

/// <summary>
/// Visible presence record for a worker-pool member in the #worker-pool lobby.
/// Each record corresponds to an active member who has joined the lobby.
/// Idle = available for assignment; other statuses indicate lease lifecycle.
/// </summary>
public sealed record WorkerPoolLobbyPresenceDto(
    long Id,
    long ChannelId,
    string MemberIdentity,
    string? AgentInstanceId,
    string? PoolMemberId,
    string? Profile,
    string? Role,
    string Status,
    string? CurrentAssignmentId,
    string? CurrentTaskId,
    string? CurrentProjectId,
    string? LastActivityAt,
    string CreatedAt,
    string UpdatedAt);

/// <summary>
/// Request to join or update presence in the #worker-pool lobby.
/// Status transitions: idle -> leased -> draining -> released -> idle
/// Quarantined/offline are terminal statuses requiring Core intervention.
/// </summary>
public sealed record UpsertWorkerPoolLobbyPresenceRequest(
    string MemberIdentity,
    string? AgentInstanceId,
    string? PoolMemberId,
    string? Profile,
    string? Role,
    string? Status,
    string? CurrentAssignmentId,
    string? CurrentTaskId,
    string? CurrentProjectId,
    string? LastActivityAt);

/// <summary>
/// Response payload for the worker-pool lobby overview.
/// Groups available (idle) workers by role/profile for easy scheduling.
/// </summary>
public sealed record WorkerPoolLobbyOverviewResponse(
    string LobbySlug,
    string LobbyDisplayName,
    long LobbyChannelId,
    int TotalMembers,
    int AvailableCount,
    IReadOnlyList<WorkerPoolPresenceByRoleGroup> ByRole,
    IReadOnlyList<WorkerPoolLobbyPresenceDto> Members);

/// <summary>
/// Group of available (idle-status) workers sharing a role/profile.
/// </summary>
public sealed record WorkerPoolPresenceByRoleGroup(
    string? Role,
    string? Profile,
    int Count,
    IReadOnlyList<WorkerPoolLobbyPresenceDto> Members);

/// <summary>
/// Body for a worker-pool lobby status message that carries trace context.
/// Used when posting a lobby status update that includes assignment trace IDs.
/// </summary>
public sealed record PostWorkerPoolLobbyStatusRequest(
    string SenderIdentity,
    string Body,
    string? AgentInstanceId,
    string? PoolMemberId,
    string? Profile,
    string? Role,
    string? Status,
    string? CurrentAssignmentId,
    string? CurrentTaskId,
    string? CurrentProjectId);
