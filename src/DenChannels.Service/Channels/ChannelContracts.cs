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
    string? Summary,
    string? DeepLink,
    long? ThreadRootMessageId,
    long? ReplyToMessageId,
    string? MetadataJson,
    string? DeliveryRequestId,
    string? DedupeKey,
    string CreatedAt,
    string? EditedAt,
    string? DeletedAt);

public sealed record PostChannelMessageRequest(
    string SenderType,
    string SenderIdentity,
    string Body,
    string? MessageKind,
    string? SourceKind,
    string? SourceId,
    string? SourceProjectId,
    string? Summary,
    string? DeepLink,
    long? ThreadRootMessageId,
    long? ReplyToMessageId,
    string? MetadataJson,
    string? DeliveryRequestId,
    string? DedupeKey);

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
    string? SettingsJson);

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
    long? TaskId,
    long? ThreadId,
    long? AnchorMessageId,
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
    long? TaskId,
    long? ThreadId,
    long? AnchorMessageId,
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
