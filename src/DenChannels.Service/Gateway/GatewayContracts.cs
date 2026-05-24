namespace DenChannels.Service.Gateway;

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

/// <summary>Request to record a direct targeted message as a Gateway-visible wake request.</summary>
public sealed record PostGatewayDirectAgentMessageRequest(
    long? ChannelId,
    string? ProjectId,
    string MemberIdentity,
    string SenderIdentity,
    string Body);

/// <summary>Result of a direct targeted message request with delivery/evidence status links.</summary>
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
    string GatewayMessageUrl,
    string GatewayEventsUrl,
    string EvidenceSummary);

/// <summary>Single channel message suitable for Gateway routing/simulation decisions.</summary>
public sealed record GatewayMessageDto(
    long Id,
    long ChannelId,
    string MessageKind,
    string SenderType,
    string SenderIdentity,
    string? SourceKind,
    string? SourceId,
    string? SourceProjectId,
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

/// <summary>Single event item within the Gateway events cursor response.</summary>
public sealed record GatewayEventItemDto(
    long Id,
    long ChannelId,
    string MessageKind,
    string SenderType,
    string SenderIdentity,
    string? SourceKind,
    string? SourceId,
    string? SourceProjectId,
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
