using System.Text.Json.Serialization;

namespace DenChannels.Service.ActiveWorkRouting;

// =========================================================================
// Active-work continuation routing contracts (task #1873)
// =========================================================================
//
// Design intent: route Channels questions and continuation actions to
// the active work session, not to a random same-profile lane.
//
// The source/control channel is context/metadata. The concrete agent
// instance / assignment run owns the active session. Target work is
// resolved by explicit target project/task/assignment/run fields, not
// by channel.project_id.
//
// See also:
//   - _global/agent-session-boundary-policy
//   - docs/durable-gateway-sessions-spec.md §0
//   - docs/assignment-trace-aggregate-1737.md

// =========================================================================
// Request DTOs
// =========================================================================

/// <summary>
/// Request to resolve the active work continuation target for a given
/// target project/task/assignment/run. Callers supply whatever identity
/// fields they have; the routing service returns the best-matching
/// active work route or an explicit no-route result.
/// </summary>
public sealed record ResolveActiveWorkRouteRequest(
    /// <summary>Target project where the work is happening.</summary>
    string? TargetProjectId = null,

    /// <summary>Target task within the project.</summary>
    long? TargetTaskId = null,

    /// <summary>Specific assignment run ID.</summary>
    string? AssignmentId = null,

    /// <summary>Specific worker run ID.</summary>
    string? WorkerRunId = null,

    /// <summary>Agent profile identity filter (optional).</summary>
    string? ProfileIdentity = null,

    /// <summary>Source channel context (metadata only, never session owner).</summary>
    string? SourceChannelId = null,

    /// <summary>Source/control project context (metadata only).</summary>
    string? SourceProjectId = null);

// =========================================================================
// Response DTOs
// =========================================================================

/// <summary>
/// Result of active-work continuation route resolution. Always returns
/// 200 with an explicit RouteStatus rather than 404, so callers can
/// distinguish "no active route found" from service errors.
/// </summary>
public sealed record ActiveWorkRouteResponse(
    /// <summary>
    /// Resolution status: "routed" = found an active work target;
    /// "no_active_route" = no matching active work found;
    /// "stale" = found a match but it appears stale/inactive.
    /// </summary>
    string RouteStatus,

    /// <summary>Summary of why this status was returned.</summary>
    string Reason,

    /// <summary>Resolved route when RouteStatus is "routed" or "stale". Null when "no_active_route".</summary>
    ActiveWorkRouteDto? Route = null,

    /// <summary>Evidence supporting the resolution (source traces consulted).</summary>
    ActiveWorkRouteEvidenceDto? Evidence = null);

/// <summary>
/// A resolved active work route with enough identity/handle information
/// for a caller (Runner, Patch, Den Web) to direct a continuation action
/// (question, tool-limit extension, reset) to the correct agent instance
/// and session.
/// </summary>
public sealed record ActiveWorkRouteDto(
    /// <summary>Target project where the work is happening.</summary>
    string? TargetProjectId,

    /// <summary>Target task within the project.</summary>
    long? TargetTaskId,

    /// <summary>Assignment ID for the active work.</summary>
    string? AssignmentId,

    /// <summary>Worker run ID.</summary>
    string? WorkerRunId,

    /// <summary>Worker role (coder, reviewer, etc.).</summary>
    string? WorkerRole,

    /// <summary>
    /// Concrete agent instance identity. When multiple instances share a
    /// profile identity (e.g. coder), this disambiguates which
    /// concrete worker to route to.
    /// </summary>
    string? AgentInstanceId,

    /// <summary>Profile identity (may be shared by multiple instances).</summary>
    string? ProfileIdentity,

    /// <summary>Pool member ID for worker-pool members.</summary>
    string? PoolMemberId,

    /// <summary>
    /// Session owner identity. Identifies the agent session that owns
    /// active work, independent of the source channel.
    /// </summary>
    string? SessionOwnerId,

    /// <summary>Session ID for the active session.</summary>
    string? SessionId,

    /// <summary>Source/control channel where the work is visible.</summary>
    long? SourceChannelId,

    /// <summary>Source/control project for the channel.</summary>
    string? SourceControlProjectId,

    /// <summary>Timestamp of the last observed activity for this route.</summary>
    string? LastActivityAt,

    /// <summary>Phase of the current assignment (running, blocked, etc.).</summary>
    string? AssignmentPhase,

    /// <summary>Whether this route appears stale based on activity age.</summary>
    bool IsStale,

    /// <summary>
    /// Allowed continuation actions for this route. Callers should only
    /// attempt actions listed here.
    /// </summary>
    IReadOnlyList<string> AllowedActions,

    /// <summary>Handles for drill-down (trace, transcript, delivery).</summary>
    ActiveWorkRouteHandlesDto? Handles);

/// <summary>
/// Handles for drill-down from a resolved active work route.
/// </summary>
public sealed record ActiveWorkRouteHandlesDto(
    /// <summary>Assignment transcript URL.</summary>
    string? TranscriptUrl,

    /// <summary>Assignment trace URL (combined Core + Gateway evidence).</summary>
    string? TraceUrl,

    /// <summary>Delivery request handle (if Gateway delivery exists).</summary>
    string? DeliveryHandle,

    /// <summary>Agent detail URL.</summary>
    string? AgentDetailUrl);

/// <summary>
/// Evidence supporting the active work route resolution. Provides
/// transparency about which sources were consulted and what was found.
/// </summary>
public sealed record ActiveWorkRouteEvidenceDto(
    /// <summary>Sources that were consulted during resolution.</summary>
    IReadOnlyList<ActiveWorkRouteSourceEvidenceDto> Sources,

    /// <summary>Candidate routes considered before selection.</summary>
    int CandidatesConsidered,

    /// <summary>Timestamp of the resolution.</summary>
    string ResolvedAt);

/// <summary>
/// Evidence from a single source consulted during route resolution.
/// </summary>
public sealed record ActiveWorkRouteSourceEvidenceDto(
    /// <summary>Source name (e.g. "channel_messages", "activity_events", "worker_pool").</summary>
    string Source,

    /// <summary>Whether this source was available during resolution.</summary>
    bool Available,

    /// <summary>Number of records examined from this source.</summary>
    int RecordsExamined,

    /// <summary>Optional detail about what was found or why it was unavailable.</summary>
    string? Detail);

// =========================================================================
// Constants
// =========================================================================

/// <summary>
/// Well-known route status values.
/// </summary>
public static class ActiveWorkRouteStatus
{
    /// <summary>Found an active work target.</summary>
    public const string Routed = "routed";

    /// <summary>No matching active work found.</summary>
    public const string NoActiveRoute = "no_active_route";

    /// <summary>Found a match but it appears stale/inactive.</summary>
    public const string Stale = "stale";
}

/// <summary>
/// Well-known continuation action values.
/// </summary>
public static class ActiveWorkContinuationAction
{
    /// <summary>Ask a question in the active session's context.</summary>
    public const string Ask = "ask";

    /// <summary>Continue/extend a tool-limited session.</summary>
    public const string Continue = "continue";

    /// <summary>Reset the active session.</summary>
    public const string Reset = "reset";

    /// <summary>View the active session transcript.</summary>
    public const string ViewTranscript = "view_transcript";
}

// =========================================================================
// List/overview DTOs
// =========================================================================

/// <summary>
/// Request to list active work routes. Supports filtering by project,
/// task, agent identity, or assignment status.
/// </summary>
public sealed record ListActiveWorkRoutesRequest(
    string? TargetProjectId = null,
    long? TargetTaskId = null,
    string? ProfileIdentity = null,
    string? AssignmentId = null,
    bool IncludeStale = false,
    int Limit = 50);

/// <summary>
/// Response listing active work routes matching the filter criteria.
/// </summary>
public sealed record ListActiveWorkRoutesResponse(
    IReadOnlyList<ActiveWorkRouteDto> Routes,
    int TotalCount,
    ActiveWorkRouteEvidenceDto Evidence);
