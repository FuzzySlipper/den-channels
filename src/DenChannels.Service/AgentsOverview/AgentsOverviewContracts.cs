using System.Text.Json.Serialization;

namespace DenChannels.Service.AgentsOverview;

// =========================================================================
// Gateway state projection (consumed from external Gateway service #1693)
// =========================================================================

public sealed record GatewayStateDto(
    string GeneratedAt,
    string Service,
    GatewayBindingHealthDto? BindingHealth,
    IReadOnlyList<GatewayAgentDto> Agents);

public sealed record GatewayBindingHealthDto(
    string Status,
    int TotalCount,
    int FreshCount,
    int StaleCount,
    string? Reason);

public sealed record GatewayAgentDto(
    string AgentKey,
    string? ProjectId,
    string? AgentIdentity,
    string? Role,
    string? BindingFreshness,
    IReadOnlyList<GatewayAdapterInstanceDto>? AdapterInstances,
    GatewayDeliverySummaryDto? DeliverySummary,
    IReadOnlyList<GatewayDeliveryDto>? CurrentDeliveries,
    IReadOnlyList<GatewayDeliveryDto>? RecentDeliveries,
    IReadOnlyList<string>? Flags);

public sealed record GatewayAdapterInstanceDto(
    string? AdapterKind,
    string? AdapterInstanceId,
    string Status,
    string? LastSeenAt,
    string? ExpiresAt,
    bool IsStale,
    string? StalenessReason,
    IReadOnlyDictionary<string, string>? Metadata)
{
    public string AdapterKey => AdapterInstanceId ?? AdapterKind ?? "unknown";
    public string? LastHeartbeat => LastSeenAt;
}

public sealed record GatewayDeliverySummaryDto(
    string State,
    int PendingCount,
    int DeliveringCount,
    int DeliveredNotCompletedCount,
    int CompletedRecentCount,
    int FailedRecentCount,
    int SuppressedRecentCount,
    int StuckCount,
    int Total)
{
    public GatewayDeliveryCountsDto Counts => new(
        Active: PendingCount + DeliveringCount + DeliveredNotCompletedCount + StuckCount,
        Completed: CompletedRecentCount,
        Failed: FailedRecentCount,
        Suppressed: SuppressedRecentCount,
        Total: Total);
}

public sealed record GatewayDeliveryCountsDto(
    int Active,
    int Completed,
    int Failed,
    int Suppressed,
    int Total);

public sealed record GatewayDeliveryDto(
    long? DeliveryRequestId,
    string Status,
    string? DeliveryMode,
    string? TargetType,
    string? TargetIdentity,
    string? ProjectId,
    long? TaskId,
    string? ChannelId,
    string? SourceKind,
    string? SourceId,
    string? SourceProjectId,
    string? ContextSummary,
    string? ContextLink,
    int AttemptCount,
    string? LeaseExpiresAt,
    string? NextAttemptAt,
    string? ExpiresAt,
    string? CreatedAt,
    string? UpdatedAt,
    GatewayDeliveryAttemptDto? LastAttempt,
    IReadOnlyList<string>? Flags)
{
    public string DeliveryId => DeliveryRequestId?.ToString() ?? string.Empty;
    public string? RequestId => DeliveryRequestId?.ToString();
    public string State => Status;
    public bool Terminal => Status is "completed" or "failed" or "expired" or "suppressed";
    public string? Summary => ContextSummary;
}

public sealed record GatewayDeliveryAttemptDto(
    long AttemptId,
    int AttemptNumber,
    long? AdapterBindingId,
    string Status,
    string? AckKind,
    string? ExternalMessageId,
    string? SessionId,
    string? ObservedAt,
    string? ErrorCode,
    string? ErrorMessage);

// =========================================================================
// Agents Overview API request/response DTOs
// =========================================================================

public sealed record AgentsOverviewRequest(
    string? ProjectId,
    string? ChannelId,
    [property: JsonPropertyName("scope")] string? Scope,
    string? AgentIdentity,
    int ActivityLimit = 3,
    bool IncludeLeft = false,
    bool IncludeGateway = true);

public sealed record AgentsOverviewResponse(
    IReadOnlyList<AgentOverviewItem> Agents,
    int TotalCount,
    SourceHealthDto SourceHealth);

public sealed record SourceHealthDto(
    SourceServiceStatusDto? Channels,
    SourceServiceStatusDto? Gateway,
    SourceServiceStatusDto? WorkerPool = null);

public sealed record SourceServiceStatusDto(
    string Status,
    string? Warning = null);

public sealed record AgentOverviewItem(
    string AgentIdentity,
    string? OperatorStatus,
    string? WorkState,
    string? Severity,
    AgentSummaryDto? Summary,
    IReadOnlyList<string> Flags,
    AgentLinksDto? Links,
    IReadOnlyList<ChannelMembershipOverviewDto>? Memberships,
    IReadOnlyList<GatewayBindingOverviewDto>? Bindings,
    IReadOnlyList<DeliveryOverviewDto>? DeliverySummaries,
    IReadOnlyList<ActivityEventOverviewDto>? RecentActivity,
    WorkerPoolMemberDto? WorkerPoolState = null,
    WorkerPoolAssignmentDto? CurrentAssignment = null,
    AssignmentTraceHandlesDto? AssignmentTrace = null,
    [property: JsonPropertyName("childRuns")] IReadOnlyList<ChildRunStateDto>? ChildRuns = null,
    [property: JsonPropertyName("childRunCount")] int ChildRunCount = 0);

public sealed record AgentSummaryDto(
    int ChannelCount,
    int ActiveMembershipCount,
    int ActiveDeliveryCount,
    int RecentActivityCount,
    string? LatestActivityAt,
    string? HighestSeverity,
    int StaleDeliveryCount = 0);

public sealed record AgentLinksDto(
    string? Self,
    string? Memberships,
    string? Bindings,
    string? Activity);

public sealed record ChannelMembershipOverviewDto(
    long ChannelId,
    string ChannelSlug,
    string ChannelDisplayName,
    string ChannelKind,
    string? ProjectId,
    string MembershipStatus,
    string WakePolicy,
    bool CanSend,
    string? SettingsLabel);

public sealed record GatewayBindingOverviewDto(
    string? AgentKey,
    string? Role,
    string? BindingFreshness,
    string? DeliveryState,
    GatewayDeliveryCountsDto? DeliveryCounts,
    IReadOnlyList<GatewayAdapterInstanceDto>? AdapterInstances);

public sealed record DeliveryOverviewDto(
    string? DeliveryRequestId,
    string? State,
    string? Status,
    bool Terminal,
    string? CreatedAt,
    string? UpdatedAt,
    string? Summary,
    bool IsStale = false);

public sealed record ActivityEventOverviewDto(
    long Id,
    long ChannelId,
    string? ProjectId,
    string AgentIdentity,
    string? DeliveryRequestId,
    string? HermesSessionKey,
    string? DisplayBlockId,
    string? WorkerRunId,
    string? WorkerRole,
    string? AgentInstanceId,
    string? PoolMemberId,
    long? TaskId,
    string EventType,
    string Status,
    string DeliveryStage,
    bool Terminal,
    string? Title,
    string? Summary,
    string CreatedAt,
    string UpdatedAt);

// =========================================================================
// Single-agent detail endpoint DTOs
// =========================================================================

public sealed record AgentDetailResponse(
    string AgentIdentity,
    IReadOnlyList<ChannelMembershipOverviewDto>? Memberships,
    IReadOnlyList<GatewayBindingOverviewDto>? Bindings,
    IReadOnlyList<DeliveryOverviewDto>? CurrentDeliveries,
    IReadOnlyList<DeliveryOverviewDto>? RecentDeliveries,
    IReadOnlyList<ActivityEventOverviewDto>? ActivityEvents,
    IReadOnlyList<TaskAssociationDto>? TaskAssociations,
    AgentSummaryDto? Summary,
    IReadOnlyList<string> Flags,
    SourceHealthDto SourceHealth,
    WorkerPoolMemberDto? WorkerPoolState = null,
    WorkerPoolAssignmentDto? CurrentAssignment = null,
    AssignmentTraceHandlesDto? AssignmentTrace = null,
    [property: JsonPropertyName("childRuns")] IReadOnlyList<ChildRunStateDto>? ChildRuns = null,
    [property: JsonPropertyName("childRunCount")] int ChildRunCount = 0);

public sealed record TaskAssociationDto(
    long? TaskId,
    string? ProjectId,
    string? Title,
    string? Status,
    int ActivityCount,
    string? LatestActivityAt);

// =========================================================================
// Worker-pool state projection (consumed from Core worker-pool API #1722)
// =========================================================================

/// <summary>
/// Core worker-pool member canonical state. This is the authoritative source
/// for availability, lease state, and quarantine. Read-only projection only.
/// </summary>
public sealed record WorkerPoolMemberDto(
    string? MemberIdentity,
    string? Role,
    string? ToolProfile,
    string? AgentInstanceId,
    string? PoolMemberId,
    string? RunId,
    string? Availability,
    string? LastActivityAt,
    WorkerPoolAssignmentDto? CurrentAssignment,
    IReadOnlyList<string>? Flags);

/// <summary>
/// Current assignment on a worker-pool member. Derived from Core lease/checkpoint state.
/// Phase reflects: waiting / running / blocked / completed / cleanup_pending.
/// </summary>
public sealed record WorkerPoolAssignmentDto(
    string? AssignmentId,
    string? TaskId,
    string? ProjectId,
    string? LeaseOwner,
    string? LeaseExpiresAt,
    string? Phase,
    string? CheckpointType,
    string? CheckpointHandle,
    string? LastCheckpointAt);

/// <summary>
/// Trace handles for Den Web #1729 linking the overview row/detail to
/// the assignment transcript, channel activity, and delivery evidence.
/// </summary>
public sealed record AssignmentTraceHandlesDto(
    string? AssignmentId,
    long? ChannelId,
    string? RepresentativeMessageId,
    string? ActivityHandle,
    string? DeliveryHandle);

// =========================================================================
// Shared-profile pool child-run state projection (#1806)
// =========================================================================

/// <summary>
/// Per-child-run visibility for shared-profile worker pools. Each active
/// child run under a profile identity (e.g., spawned-coder) gets a concrete
/// ChildRunStateDto with routing handles for supervisor-routed delivery.
/// </summary>
public sealed record ChildRunStateDto(
    [property: JsonPropertyName("agentInstanceId")] string? AgentInstanceId,
    [property: JsonPropertyName("runId")] string? RunId,
    [property: JsonPropertyName("workerRunId")] string? WorkerRunId,
    [property: JsonPropertyName("assignmentId")] string? AssignmentId,
    [property: JsonPropertyName("poolMemberId")] string? PoolMemberId,
    [property: JsonPropertyName("profileIdentity")] string? ProfileIdentity,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("lastActivityAt")] string? LastActivityAt,
    [property: JsonPropertyName("flags")] IReadOnlyList<string>? Flags)
{
    /// <summary>
    /// Supervisor-routed delivery target. The supervisor profile identity
    /// (e.g. pool-coder-01) receives deliveries and dispatches to the
    /// correct child using AgentInstanceId + RunId + AssignmentId.
    /// Direct child routing via Channels membership is deferred to
    /// Bridge/Channels follow-up work.
    /// </summary>
    [JsonPropertyName("supervisorDeliveryTarget")]
    public string? SupervisorDeliveryTarget { get; init; }

    /// <summary>
    /// Child identity metadata for supervisor routing disambiguation.
    /// </summary>
    [JsonPropertyName("childIdentityMetadata")]
    public IReadOnlyDictionary<string, string?> ChildIdentityMetadata => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["agentInstanceId"] = AgentInstanceId,
        ["runId"] = RunId,
        ["assignmentId"] = AssignmentId,
        ["poolMemberId"] = PoolMemberId,
        ["profileIdentity"] = ProfileIdentity,
    };
}
