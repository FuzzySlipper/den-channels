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

public sealed record ChannelReactionDto(
    long Id,
    long ChannelMessageId,
    string ReactorType,
    string ReactorIdentity,
    string ReactionKey,
    string CreatedAt);

public sealed record AddChannelReactionRequest(
    string ReactorType,
    string ReactorIdentity,
    string ReactionKey);
