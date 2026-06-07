namespace DenChannels.Service.Presence;

/// <summary>
/// Individual presence entry for a member in a channel.
/// Projection over membership + subscriptions + lifecycle/Core evidence.
/// Presence updates must not create ordinary channel_messages.
/// </summary>
public sealed record PresenceEntryDto(
    long ChannelId,
    string MemberType,
    string MemberIdentity,
    string MembershipStatus,
    string WakePolicy,
    string? ProfileIdentity,
    string? MemberRole,
    int SubscriptionCount,
    int ActiveSubscriptionCount,
    IReadOnlyList<string> SubscriptionStatuses,
    string? LastSeenAt,
    string? LastClaimedAt,
    string? TargetProjectId,
    long? TargetTaskId,
    string? AssignmentId,
    string? WorkerRunId,
    string? WorkerRole,
    string PresenceStatus,
    string SourceSummary);

/// <summary>
/// Response for GET /api/channels/{channelId}/presence.
/// Returns a bounded list of members with subscription reachability summary.
/// </summary>
public sealed record ChannelPresenceResponse(
    long ChannelId,
    string ChannelSlug,
    IReadOnlyList<PresenceEntryDto> Members);
