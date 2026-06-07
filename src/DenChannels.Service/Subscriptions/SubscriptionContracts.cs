namespace DenChannels.Service.Subscriptions;

/// <summary>
/// A channel subscription representing a concrete runtime listening/delivery
/// relationship. Distinct from logical channel membership — multiple subscriptions
/// can exist for one membership (e.g., different agent instances, target-work contexts).
/// </summary>
public sealed record ChannelSubscriptionDto(
    long Id,
    long ChannelId,
    long? MembershipId,
    string MemberType,
    string MemberIdentity,
    string? ProfileIdentity,
    string? AgentInstanceId,
    string? PoolMemberId,
    string SubscriptionIdentity,
    string SubscriptionPurpose,
    string SubscriptionStatus,
    string? SourceProjectId,
    string? TargetProjectId,
    long? TargetTaskId,
    string? AssignmentId,
    string? WorkerRunId,
    string? WorkerRole,
    string? SessionOwnerId,
    string? SessionId,
    string? WakePolicyOverride,
    string? LastSeenAt,
    string? LastClaimedAt,
    string? DegradedReason,
    string? SettingsJson,
    string CreatedAt,
    string UpdatedAt);

/// <summary>
/// Request to upsert (register or update) a channel subscription.
/// subscription_identity must be deterministic and unique per channel.
/// </summary>
public sealed record UpsertChannelSubscriptionRequest(
    string MemberType,
    string MemberIdentity,
    string? ProfileIdentity,
    string? AgentInstanceId,
    string? PoolMemberId,
    string SubscriptionIdentity,
    string SubscriptionPurpose,
    string? SubscriptionStatus,
    long? MembershipId,
    string? SourceProjectId,
    string? TargetProjectId,
    long? TargetTaskId,
    string? AssignmentId,
    string? WorkerRunId,
    string? WorkerRole,
    string? SessionOwnerId,
    string? SessionId,
    string? WakePolicyOverride,
    string? SettingsJson);

/// <summary>
/// Response for listing channel subscriptions with channel context.
/// </summary>
public sealed record ChannelSubscriptionDiscoveryDto(
    long SubscriptionId,
    long ChannelId,
    string ChannelSlug,
    string ChannelKind,
    string? ProjectId,
    string MemberType,
    string MemberIdentity,
    string? ProfileIdentity,
    string? AgentInstanceId,
    string? PoolMemberId,
    string SubscriptionIdentity,
    string SubscriptionPurpose,
    string SubscriptionStatus,
    string? TargetProjectId,
    long? TargetTaskId,
    string? AssignmentId,
    string? WorkerRunId,
    string? WorkerRole,
    string CreatedAt,
    string UpdatedAt);

/// <summary>
/// Subscription cursor: tracks per-subscription, per-stream poll position.
/// Separate from human/UI read cursors (channel_read_cursors).
/// </summary>
public sealed record ChannelSubscriptionCursorDto(
    long Id,
    long SubscriptionId,
    string StreamKind,
    long LastSeenId,
    string? LastSeenAt,
    string? CursorJson,
    string CreatedAt,
    string UpdatedAt);

/// <summary>
/// Request to upsert a subscription cursor for a given stream kind.
/// </summary>
public sealed record UpsertSubscriptionCursorRequest(
    string StreamKind,
    long LastSeenId,
    string? CursorJson);
