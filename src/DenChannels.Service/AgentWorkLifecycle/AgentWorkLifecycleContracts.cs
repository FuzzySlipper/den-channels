using System.Text.Json.Serialization;

namespace DenChannels.Service.AgentWorkLifecycle;

// =========================================================================
// Agent Work Lifecycle Observability Contract
//
// Channels-owned public contract for automatic (non-waking) agent/worker
// lifecycle event observability. This is not model-authored breadcrumbs;
// it is the runtime-evidence foundation that answers "what is each agent/
// worker doing right now?" without relying on voluntary model artifacts.
//
// Architecture boundary:
//   - Channels owns this contract, the write/query/projection APIs, and
//     the non-waking durability invariants.
//   - Core remains authoritative for task/assignment/lease/run truth.
//   - den-host fulfills runtime/process/session telemetry through this
//     contract (Hermes is an adapter detail behind den-host).
//   - Activity events are observability, NOT conversation — they must not
//     create channel_messages, wake agents, advance read cursors, or imply
//     delivery completion.
// =========================================================================

/// <summary>
/// Canonical lifecycle event type vocabulary. Producers must use one of
/// these; consumers may use them for filtering/projection state.
/// </summary>
public static class LifecycleEventType
{
    // ── Request lifecycle ──
    public const string RequestRecorded = "request_recorded";
    public const string DeliveryAttempted = "delivery_attempted";
    public const string RuntimeReceived = "runtime_received";

    // ── Claim / turn lifecycle ──
    public const string RequestClaimed = "request_claimed";
    public const string AgentTurnStarted = "agent_turn_started";
    public const string TaskSelected = "task_selected";

    // ── Assignment / worker lifecycle ──
    public const string AssignmentCreated = "assignment_created";
    public const string WorkerSpawnRequested = "worker_spawn_requested";
    public const string WorkerProcessStarted = "worker_process_started";

    // ── Runtime / progress ──
    public const string Heartbeat = "heartbeat";
    public const string CheckpointSeen = "checkpoint_seen";

    // ── Terminal ──
    public const string Blocked = "blocked";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";

    // ── Cleanup / release ──
    public const string CleanupStarted = "cleanup_started";
    public const string CleanupCompleted = "cleanup_completed";
    public const string CapacityReleased = "capacity_released";

    /// <summary>
    /// All valid lifecycle event types. Used for validation on write.
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        RequestRecorded, DeliveryAttempted, RuntimeReceived,
        RequestClaimed, AgentTurnStarted, TaskSelected,
        AssignmentCreated, WorkerSpawnRequested, WorkerProcessStarted,
        Heartbeat, CheckpointSeen,
        Blocked, Completed, Failed, TimedOut,
        CleanupStarted, CleanupCompleted, CapacityReleased,
    };
}

/// <summary>
/// Machine-write request to record an agent-work lifecycle event.
/// This is the canonical write path for automatic (non-model-authored)
/// lifecycle observability. Callers are typically den-host/Hermes harness
/// modules or Core orchestration paths.
/// </summary>
public sealed record AgentWorkLifecycleWriteRequest(
    // ── Required ──
    [property: JsonPropertyName("channelId")]
    long ChannelId,

    [property: JsonPropertyName("agentIdentity")]
    string AgentIdentity,

    [property: JsonPropertyName("eventType")]
    string EventType,

    // ── Routing / correlation ──
    [property: JsonPropertyName("projectId")]
    string? ProjectId,

    [property: JsonPropertyName("taskId")]
    long? TaskId,

    [property: JsonPropertyName("threadId")]
    long? ThreadId,

    [property: JsonPropertyName("anchorMessageId")]
    long? AnchorMessageId,

    // ── Identity / pool ──
    [property: JsonPropertyName("profileIdentity")]
    string? ProfileIdentity,

    [property: JsonPropertyName("agentInstanceId")]
    string? AgentInstanceId,

    [property: JsonPropertyName("workerIdentity")]
    string? WorkerIdentity,

    [property: JsonPropertyName("workerRole")]
    string? WorkerRole,

    [property: JsonPropertyName("poolMemberId")]
    string? PoolMemberId,

    // ── Assignment / run / session ──
    [property: JsonPropertyName("assignmentId")]
    string? AssignmentId,

    [property: JsonPropertyName("workerRunId")]
    string? WorkerRunId,

    [property: JsonPropertyName("leaseId")]
    string? LeaseId,

    [property: JsonPropertyName("sessionId")]
    string? SessionId,

    [property: JsonPropertyName("parentSessionId")]
    string? ParentSessionId,

    // ── Delivery / source ──
    [property: JsonPropertyName("deliveryRequestId")]
    string? DeliveryRequestId,

    [property: JsonPropertyName("sourceMessageId")]
    string? SourceMessageId,

    [property: JsonPropertyName("directAgentEventId")]
    string? DirectAgentEventId,

    // ── Host / process ──
    [property: JsonPropertyName("hostId")]
    string? HostId,

    [property: JsonPropertyName("processId")]
    int? ProcessId,

    [property: JsonPropertyName("workdir")]
    string? Workdir,

    [property: JsonPropertyName("branch")]
    string? Branch,

    [property: JsonPropertyName("commit")]
    string? Commit,

    // ── Review / handoff ──
    [property: JsonPropertyName("reviewRoundId")]
    long? ReviewRoundId,

    [property: JsonPropertyName("displayBlockId")]
    string? DisplayBlockId,

    [property: JsonPropertyName("parentAgentIdentity")]
    string? ParentAgentIdentity,

    // ── Status / staleness ──
    [property: JsonPropertyName("lastActivityAt")]
    string? LastActivityAt,

    [property: JsonPropertyName("stalenessDeadline")]
    string? StalenessDeadline,

    [property: JsonPropertyName("stateReason")]
    string? StateReason,

    // ── Bounded content ──
    [property: JsonPropertyName("title")]
    string? Title,

    [property: JsonPropertyName("summary")]
    string? Summary,

    [property: JsonPropertyName("metadataJson")]
    string? MetadataJson,

    [property: JsonPropertyName("dedupeKey")]
    string? DedupeKey);

/// <summary>
/// The lifecycle event record as returned to consumers. Mirrors the
/// durable <c>channel_activity_events</c> row with Channel-owned
/// lifecycle-specific readback semantics.
/// </summary>
public sealed record AgentWorkLifecycleEventDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("channelId")] long ChannelId,
    [property: JsonPropertyName("agentIdentity")] string AgentIdentity,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("terminal")] bool Terminal,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt,

    // ── Routing / correlation ──
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("taskId")] long? TaskId,
    [property: JsonPropertyName("threadId")] long? ThreadId,
    [property: JsonPropertyName("anchorMessageId")] long? AnchorMessageId,

    // ── Identity ──
    [property: JsonPropertyName("profileIdentity")] string? ProfileIdentity,
    [property: JsonPropertyName("agentInstanceId")] string? AgentInstanceId,
    [property: JsonPropertyName("workerIdentity")] string? WorkerIdentity,
    [property: JsonPropertyName("workerRole")] string? WorkerRole,
    [property: JsonPropertyName("poolMemberId")] string? PoolMemberId,

    // ── Assignment / run / session ──
    [property: JsonPropertyName("assignmentId")] string? AssignmentId,
    [property: JsonPropertyName("workerRunId")] string? WorkerRunId,
    [property: JsonPropertyName("leaseId")] string? LeaseId,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("parentSessionId")] string? ParentSessionId,

    // ── Delivery ──
    [property: JsonPropertyName("deliveryRequestId")] string? DeliveryRequestId,
    [property: JsonPropertyName("sourceMessageId")] string? SourceMessageId,
    [property: JsonPropertyName("directAgentEventId")] string? DirectAgentEventId,

    // ── Host / process ──
    [property: JsonPropertyName("hostId")] string? HostId,
    [property: JsonPropertyName("processId")] int? ProcessId,
    [property: JsonPropertyName("workdir")] string? Workdir,
    [property: JsonPropertyName("branch")] string? Branch,
    [property: JsonPropertyName("commit")] string? Commit,

    // ── Review ──
    [property: JsonPropertyName("reviewRoundId")] long? ReviewRoundId,
    [property: JsonPropertyName("displayBlockId")] string? DisplayBlockId,
    [property: JsonPropertyName("parentAgentIdentity")] string? ParentAgentIdentity,

    // ── Content ──
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("summary")] string? Summary);

/// <summary>
/// Query filter for lifecycle event readback.
/// </summary>
public sealed record LifecycleEventQuery(
    [property: JsonPropertyName("channelId")]
    long? ChannelId,

    [property: JsonPropertyName("projectId")]
    string? ProjectId,

    [property: JsonPropertyName("taskId")]
    long? TaskId,

    [property: JsonPropertyName("agentIdentity")]
    string? AgentIdentity,

    [property: JsonPropertyName("workerRunId")]
    string? WorkerRunId,

    [property: JsonPropertyName("assignmentId")]
    string? AssignmentId,

    [property: JsonPropertyName("sessionId")]
    string? SessionId,

    [property: JsonPropertyName("eventType")]
    string? EventType,

    [property: JsonPropertyName("afterId")]
    long? AfterId,

    [property: JsonPropertyName("limit")]
    int Limit = 50);

/// <summary>
/// Current-work projection item. One per agent/worker that has recent
/// lifecycle activity. Produced by the current-work projection endpoint.
/// </summary>
public sealed record CurrentWorkProjectionItem(
    [property: JsonPropertyName("agentIdentity")] string AgentIdentity,
    [property: JsonPropertyName("workerRunId")] string? WorkerRunId,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("taskId")] long? TaskId,
    [property: JsonPropertyName("assignmentId")] string? AssignmentId,
    [property: JsonPropertyName("workerRole")] string? WorkerRole,
    [property: JsonPropertyName("profileIdentity")] string? ProfileIdentity,
    [property: JsonPropertyName("poolMemberId")] string? PoolMemberId,
    [property: JsonPropertyName("agentInstanceId")] string? AgentInstanceId,

    // ── Current state ──
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("stateReason")] string? StateReason,

    // ── Timestamps ──
    [property: JsonPropertyName("lastActivityAt")] string? LastActivityAt,
    [property: JsonPropertyName("stalenessDeadline")] string? StalenessDeadline,

    // ── Evidence ──
    [property: JsonPropertyName("lastActivityEventId")] long? LastActivityEventId,
    [property: JsonPropertyName("evidenceLink")] string? EvidenceLink,

    // ── Process ──
    [property: JsonPropertyName("hostId")] string? HostId,
    [property: JsonPropertyName("processId")] int? ProcessId,

    // ── Diagnostics ──
    [property: JsonPropertyName("stalenessDiagnostic")] string? StalenessDiagnostic,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags);

/// <summary>
/// Current-work projection response. Includes a summary of staleness
/// diagnostics across all tracked agents/workers.
/// </summary>
public sealed record CurrentWorkProjectionResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<CurrentWorkProjectionItem> Items,
    [property: JsonPropertyName("totalCount")] int TotalCount,
    [property: JsonPropertyName("generatedAt")] string GeneratedAt,
    [property: JsonPropertyName("stalenessSummary")] StalenessSummaryDto StalenessSummary);

/// <summary>
/// Aggregate staleness diagnostics across the projection.
/// </summary>
public sealed record StalenessSummaryDto(
    [property: JsonPropertyName("totalTracked")] int TotalTracked,
    [property: JsonPropertyName("stale")] int Stale,
    [property: JsonPropertyName("staleDiagnostics")] IReadOnlyList<string> StaleDiagnostics);
