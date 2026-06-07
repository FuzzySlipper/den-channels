using DenChannels.Service.AgentsOverview;
using DenChannels.Service.Channels;
using DenChannels.Service.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DenChannels.Service.ActiveWorkRouting;

/// <summary>
/// Resolves active-work continuation routes by target project/task/assignment/run.
/// Composes evidence from Channels messages, activity events, and worker-pool state.
/// Source channel/control room is context only; the concrete agent instance and
/// session own the active work.
/// </summary>
public sealed class ActiveWorkRoutingService
{
    private readonly WorkerPoolMembershipRepository _repository;
    private readonly IWorkerPoolStateClient _workerPoolClient;
    private readonly IOptions<DenChannelsOptions> _options;
    private readonly ILogger<ActiveWorkRoutingService> _logger;

    /// <summary>Maximum age for activity to be considered "fresh" (not stale).</summary>
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(30);

    public ActiveWorkRoutingService(
        WorkerPoolMembershipRepository repository,
        IWorkerPoolStateClient workerPoolClient,
        IOptions<DenChannelsOptions> options,
        ILogger<ActiveWorkRoutingService> logger)
    {
        _repository = repository;
        _workerPoolClient = workerPoolClient;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Resolve the active work route for a given target project/task/assignment/run.
    /// Returns an explicit no-route result when no matching active work is found,
    /// rather than returning an error.
    /// </summary>
    public async Task<ActiveWorkRouteResponse> ResolveRouteAsync(
        ResolveActiveWorkRouteRequest request,
        CancellationToken cancellationToken = default)
    {
        var resolvedAt = DateTime.UtcNow.ToString("O");
        var sources = new List<ActiveWorkRouteSourceEvidenceDto>();
        var candidates = new List<ActiveWorkRouteDto>();

        // --- Phase 1: Consult channel messages with target-work fields ---
        var messages = await _repository.FindActiveWorkMessagesAsync(
            targetProjectId: request.TargetProjectId,
            targetTaskId: request.TargetTaskId,
            assignmentId: request.AssignmentId,
            workerRunId: request.WorkerRunId,
            profileIdentity: request.ProfileIdentity,
            limit: 20,
            cancellationToken: cancellationToken);

        sources.Add(new ActiveWorkRouteSourceEvidenceDto(
            Source: "channel_messages",
            Available: true,
            RecordsExamined: messages.Count,
            Detail: messages.Count > 0
                ? $"Found {messages.Count} message(s) with target-work fields"
                : "No messages with matching target-work fields"));

        // --- Phase 2: Consult activity events ---
        var activityEvents = await _repository.FindActiveWorkActivityEventsAsync(
            targetProjectId: request.TargetProjectId,
            targetTaskId: request.TargetTaskId,
            assignmentId: request.AssignmentId,
            workerRunId: request.WorkerRunId,
            profileIdentity: request.ProfileIdentity,
            nonTerminalOnly: false,
            limit: 50,
            cancellationToken: cancellationToken);

        sources.Add(new ActiveWorkRouteSourceEvidenceDto(
            Source: "activity_events",
            Available: true,
            RecordsExamined: activityEvents.Count,
            Detail: activityEvents.Count > 0
                ? $"Found {activityEvents.Count} activity event(s)"
                : "No matching activity events"));

        // --- Phase 3: Consult worker-pool state (best-effort) ---
        WorkerPoolStateDto? workerPoolState = null;
        if (!_options.Value.WorkerPool.Disabled)
        {
            workerPoolState = await _workerPoolClient.FetchWorkersAsync(
                projectId: request.TargetProjectId,
                agentIdentity: request.ProfileIdentity,
                cancellationToken: cancellationToken);
        }

        sources.Add(new ActiveWorkRouteSourceEvidenceDto(
            Source: "worker_pool",
            Available: workerPoolState is not null,
            RecordsExamined: workerPoolState?.Members.Count ?? 0,
            Detail: workerPoolState is not null
                ? $"Worker-pool returned {workerPoolState.Members.Count} member(s)"
                : _options.Value.WorkerPool.Disabled
                    ? "Worker-pool disabled"
                    : "Worker-pool endpoint unavailable"));

        // --- Phase 4: Build candidate routes from evidence ---

        // 4a. From messages: extract the most recent route per unique session owner
        var messageRoutes = BuildRoutesFromMessages(messages);
        candidates.AddRange(messageRoutes);

        // 4b. From activity events: supplement with session/instance identity
        var activityRoutes = BuildRoutesFromActivity(activityEvents);
        foreach (var ar in activityRoutes)
        {
            // Merge with existing message-based routes if possible
            var existing = candidates.FirstOrDefault(c =>
                RouteIdentityMatches(c, ar));
            if (existing is null)
            {
                candidates.Add(ar);
            }
            else
            {
                // Activity may have richer instance/session fields
                var merged = MergeRoutes(existing, ar);
                var idx = candidates.IndexOf(existing);
                candidates[idx] = merged;
            }
        }

        // 4c. From worker-pool state: add assignment/phase information
        if (workerPoolState is not null)
        {
            EnrichRoutesFromWorkerPool(candidates, workerPoolState);
        }

        // --- Phase 5: Select the best route ---
        if (candidates.Count == 0)
        {
            return new ActiveWorkRouteResponse(
                RouteStatus: ActiveWorkRouteStatus.NoActiveRoute,
                Reason: BuildNoRouteReason(request, sources),
                Route: null,
                Evidence: new ActiveWorkRouteEvidenceDto(
                    Sources: sources,
                    CandidatesConsidered: 0,
                    ResolvedAt: resolvedAt));
        }

        // Select the most recently active candidate
        var selected = candidates
            .OrderByDescending(c => c.LastActivityAt ?? string.Empty)
            .First();

        // Mark staleness
        var isStale = IsRouteStale(selected);
        var routeStatus = isStale
            ? ActiveWorkRouteStatus.Stale
            : ActiveWorkRouteStatus.Routed;

        var reason = isStale
            ? $"Found route for assignment {selected.AssignmentId ?? "unknown"} but last activity was {selected.LastActivityAt}"
            : $"Resolved to active session for assignment {selected.AssignmentId ?? "unknown"}";

        return new ActiveWorkRouteResponse(
            RouteStatus: routeStatus,
            Reason: reason,
            Route: selected,
            Evidence: new ActiveWorkRouteEvidenceDto(
                Sources: sources,
                CandidatesConsidered: candidates.Count,
                ResolvedAt: resolvedAt));
    }

    /// <summary>
    /// List all active work routes matching the given filter criteria.
    /// </summary>
    public async Task<ListActiveWorkRoutesResponse> ListRoutesAsync(
        ListActiveWorkRoutesRequest request,
        CancellationToken cancellationToken = default)
    {
        var resolvedAt = DateTime.UtcNow.ToString("O");
        var sources = new List<ActiveWorkRouteSourceEvidenceDto>();
        var candidates = new List<ActiveWorkRouteDto>();

        // Phase 1: Messages
        var messages = await _repository.FindActiveWorkMessagesAsync(
            targetProjectId: request.TargetProjectId,
            targetTaskId: request.TargetTaskId,
            assignmentId: request.AssignmentId,
            profileIdentity: request.ProfileIdentity,
            limit: 100,
            cancellationToken: cancellationToken);

        sources.Add(new ActiveWorkRouteSourceEvidenceDto(
            Source: "channel_messages",
            Available: true,
            RecordsExamined: messages.Count,
            Detail: null));

        // Phase 2: Activity events
        var activityEvents = await _repository.FindActiveWorkActivityEventsAsync(
            targetProjectId: request.TargetProjectId,
            targetTaskId: request.TargetTaskId,
            assignmentId: request.AssignmentId,
            profileIdentity: request.ProfileIdentity,
            limit: 200,
            cancellationToken: cancellationToken);

        sources.Add(new ActiveWorkRouteSourceEvidenceDto(
            Source: "activity_events",
            Available: true,
            RecordsExamined: activityEvents.Count,
            Detail: null));

        // Phase 3: Worker-pool
        WorkerPoolStateDto? workerPoolState = null;
        if (!_options.Value.WorkerPool.Disabled)
        {
            workerPoolState = await _workerPoolClient.FetchWorkersAsync(
                projectId: request.TargetProjectId,
                agentIdentity: request.ProfileIdentity,
                cancellationToken: cancellationToken);
        }

        sources.Add(new ActiveWorkRouteSourceEvidenceDto(
            Source: "worker_pool",
            Available: workerPoolState is not null,
            RecordsExamined: workerPoolState?.Members.Count ?? 0,
            Detail: null));

        // Build candidates
        var messageRoutes = BuildRoutesFromMessages(messages);
        candidates.AddRange(messageRoutes);

        var activityRoutes = BuildRoutesFromActivity(activityEvents);
        foreach (var ar in activityRoutes)
        {
            var existing = candidates.FirstOrDefault(c => RouteIdentityMatches(c, ar));
            if (existing is null)
            {
                candidates.Add(ar);
            }
            else
            {
                var merged = MergeRoutes(existing, ar);
                var idx = candidates.IndexOf(existing);
                candidates[idx] = merged;
            }
        }

        if (workerPoolState is not null)
        {
            EnrichRoutesFromWorkerPool(candidates, workerPoolState);
        }

        // Filter stale unless requested
        var results = request.IncludeStale
            ? candidates
            : candidates.Where(c => !IsRouteStale(c)).ToList();

        // Apply limit
        results = results
            .OrderByDescending(c => c.LastActivityAt ?? string.Empty)
            .Take(request.Limit)
            .ToList();

        return new ListActiveWorkRoutesResponse(
            Routes: results,
            TotalCount: results.Count,
            Evidence: new ActiveWorkRouteEvidenceDto(
                Sources: sources,
                CandidatesConsidered: candidates.Count,
                ResolvedAt: resolvedAt));
    }

    // =========================================================================
    // Route building helpers
    // =========================================================================

    private List<ActiveWorkRouteDto> BuildRoutesFromMessages(
        IReadOnlyList<ChannelMessageDto> messages)
    {
        var routes = new List<ActiveWorkRouteDto>();
        // Group by unique session identity (agentInstanceId + assignmentId)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var msg in messages)
        {
            var key = $"{msg.AgentInstanceId ?? ""}:{msg.AssignmentId ?? ""}:{msg.SessionOwnerId ?? ""}:{msg.WorkerRunId ?? ""}";
            if (seen.Contains(key)) continue;
            seen.Add(key);

            var allowedActions = new List<string>
            {
                ActiveWorkContinuationAction.Ask,
                ActiveWorkContinuationAction.ViewTranscript,
            };
            // Allow continue/reset if session owner is known
            if (!string.IsNullOrWhiteSpace(msg.SessionOwnerId) || !string.IsNullOrWhiteSpace(msg.AgentInstanceId))
            {
                allowedActions.Add(ActiveWorkContinuationAction.Continue);
                allowedActions.Add(ActiveWorkContinuationAction.Reset);
            }

            routes.Add(new ActiveWorkRouteDto(
                TargetProjectId: msg.TargetProjectId,
                TargetTaskId: msg.TargetTaskId,
                AssignmentId: msg.AssignmentId,
                WorkerRunId: msg.WorkerRunId,
                WorkerRole: msg.WorkerRole,
                AgentInstanceId: msg.AgentInstanceId,
                ProfileIdentity: msg.ProfileIdentity,
                PoolMemberId: msg.PoolMemberId,
                SessionOwnerId: msg.SessionOwnerId,
                SessionId: msg.SessionId,
                SourceChannelId: msg.ChannelId,
                SourceControlProjectId: msg.SourceProjectId,
                LastActivityAt: msg.CreatedAt,
                AssignmentPhase: null,
                IsStale: false,
                AllowedActions: allowedActions,
                Handles: new ActiveWorkRouteHandlesDto(
                    TranscriptUrl: msg.AssignmentId is not null
                        ? $"/api/assignments/{msg.AssignmentId}/transcript"
                        : null,
                    TraceUrl: msg.AssignmentId is not null
                        ? $"/api/gateway/assignments/{msg.AssignmentId}/trace?projectId={msg.TargetProjectId}"
                        : null,
                    DeliveryHandle: msg.DeliveryRequestId,
                    AgentDetailUrl: !string.IsNullOrWhiteSpace(msg.ProfileIdentity)
                        ? $"/api/agents/{Uri.EscapeDataString(msg.ProfileIdentity)}/overview"
                        : null)));
        }

        return routes;
    }

    private List<ActiveWorkRouteDto> BuildRoutesFromActivity(
        IReadOnlyList<ChannelActivityEventDto> events)
    {
        var routes = new List<ActiveWorkRouteDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in events)
        {
            var key = $"{evt.AgentInstanceId ?? ""}:{evt.AssignmentId ?? ""}:{evt.WorkerRunId ?? ""}";
            if (seen.Contains(key)) continue;
            seen.Add(key);

            var allowedActions = new List<string>
            {
                ActiveWorkContinuationAction.Ask,
                ActiveWorkContinuationAction.ViewTranscript,
            };
            if (!string.IsNullOrWhiteSpace(evt.AgentInstanceId))
            {
                allowedActions.Add(ActiveWorkContinuationAction.Continue);
                allowedActions.Add(ActiveWorkContinuationAction.Reset);
            }

            routes.Add(new ActiveWorkRouteDto(
                TargetProjectId: evt.ProjectId,
                TargetTaskId: evt.TaskId,
                AssignmentId: evt.AssignmentId,
                WorkerRunId: evt.WorkerRunId,
                WorkerRole: evt.WorkerRole,
                AgentInstanceId: evt.AgentInstanceId,
                ProfileIdentity: evt.AgentIdentity,
                PoolMemberId: evt.PoolMemberId,
                SessionOwnerId: null,
                SessionId: null,
                SourceChannelId: evt.ChannelId,
                SourceControlProjectId: null,
                LastActivityAt: evt.UpdatedAt,
                AssignmentPhase: null,
                IsStale: false,
                AllowedActions: allowedActions,
                Handles: new ActiveWorkRouteHandlesDto(
                    TranscriptUrl: evt.AssignmentId is not null
                        ? $"/api/assignments/{evt.AssignmentId}/transcript"
                        : null,
                    TraceUrl: evt.AssignmentId is not null
                        ? $"/api/gateway/assignments/{evt.AssignmentId}/trace?projectId={evt.ProjectId}"
                        : null,
                    DeliveryHandle: evt.DeliveryRequestId,
                    AgentDetailUrl: !string.IsNullOrWhiteSpace(evt.AgentIdentity)
                        ? $"/api/agents/{Uri.EscapeDataString(evt.AgentIdentity)}/overview"
                        : null)));
        }

        return routes;
    }

    private static bool RouteIdentityMatches(ActiveWorkRouteDto a, ActiveWorkRouteDto b)
    {
        // Match on the combination that identifies a unique active work session
        var aInstance = a.AgentInstanceId ?? "";
        var bInstance = b.AgentInstanceId ?? "";
        if (!string.IsNullOrEmpty(aInstance) && !string.IsNullOrEmpty(bInstance) &&
            !string.Equals(aInstance, bInstance, StringComparison.OrdinalIgnoreCase))
            return false;

        var aAssignment = a.AssignmentId ?? "";
        var bAssignment = b.AssignmentId ?? "";
        if (!string.IsNullOrEmpty(aAssignment) && !string.IsNullOrEmpty(bAssignment))
            return string.Equals(aAssignment, bAssignment, StringComparison.OrdinalIgnoreCase);

        // Fall back to run ID match
        var aRun = a.WorkerRunId ?? "";
        var bRun = b.WorkerRunId ?? "";
        if (!string.IsNullOrEmpty(aRun) && !string.IsNullOrEmpty(bRun))
            return string.Equals(aRun, bRun, StringComparison.OrdinalIgnoreCase);

        // Fall back to profile + task match
        return string.Equals(a.ProfileIdentity ?? "", b.ProfileIdentity ?? "", StringComparison.OrdinalIgnoreCase)
            && a.TargetTaskId == b.TargetTaskId
            && string.Equals(a.TargetProjectId ?? "", b.TargetProjectId ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private static ActiveWorkRouteDto MergeRoutes(ActiveWorkRouteDto existing, ActiveWorkRouteDto supplement)
    {
        // Merge: prefer existing non-null values, fill gaps from supplement
        var lastActivity = CompareTimestamps(existing.LastActivityAt, supplement.LastActivityAt) >= 0
            ? existing.LastActivityAt
            : supplement.LastActivityAt;

        return new ActiveWorkRouteDto(
            TargetProjectId: existing.TargetProjectId ?? supplement.TargetProjectId,
            TargetTaskId: existing.TargetTaskId ?? supplement.TargetTaskId,
            AssignmentId: existing.AssignmentId ?? supplement.AssignmentId,
            WorkerRunId: existing.WorkerRunId ?? supplement.WorkerRunId,
            WorkerRole: existing.WorkerRole ?? supplement.WorkerRole,
            AgentInstanceId: existing.AgentInstanceId ?? supplement.AgentInstanceId,
            ProfileIdentity: existing.ProfileIdentity ?? supplement.ProfileIdentity,
            PoolMemberId: existing.PoolMemberId ?? supplement.PoolMemberId,
            SessionOwnerId: existing.SessionOwnerId ?? supplement.SessionOwnerId,
            SessionId: existing.SessionId ?? supplement.SessionId,
            SourceChannelId: existing.SourceChannelId ?? supplement.SourceChannelId,
            SourceControlProjectId: existing.SourceControlProjectId ?? supplement.SourceControlProjectId,
            LastActivityAt: lastActivity,
            AssignmentPhase: existing.AssignmentPhase ?? supplement.AssignmentPhase,
            IsStale: existing.IsStale || supplement.IsStale,
            AllowedActions: existing.AllowedActions.Concat(supplement.AllowedActions)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Handles: new ActiveWorkRouteHandlesDto(
                TranscriptUrl: existing.Handles?.TranscriptUrl ?? supplement.Handles?.TranscriptUrl,
                TraceUrl: existing.Handles?.TraceUrl ?? supplement.Handles?.TraceUrl,
                DeliveryHandle: existing.Handles?.DeliveryHandle ?? supplement.Handles?.DeliveryHandle,
                AgentDetailUrl: existing.Handles?.AgentDetailUrl ?? supplement.Handles?.AgentDetailUrl));
    }

    private void EnrichRoutesFromWorkerPool(
        List<ActiveWorkRouteDto> candidates,
        WorkerPoolStateDto workerPoolState)
    {
        foreach (var member in workerPoolState.Members)
        {
            if (member.CurrentAssignment is null) continue;

            // Find matching candidate routes
            foreach (var route in candidates)
            {
                var matchesRunId = !string.IsNullOrWhiteSpace(route.WorkerRunId) &&
                    string.Equals(route.WorkerRunId, member.RunId, StringComparison.OrdinalIgnoreCase);
                var matchesAssignmentId = !string.IsNullOrWhiteSpace(route.AssignmentId) &&
                    string.Equals(route.AssignmentId, member.CurrentAssignment.AssignmentId, StringComparison.OrdinalIgnoreCase);
                var matchesProfile = !string.IsNullOrWhiteSpace(route.ProfileIdentity) &&
                    string.Equals(route.ProfileIdentity, member.MemberIdentity, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(route.TargetProjectId, member.CurrentAssignment.ProjectId, StringComparison.OrdinalIgnoreCase);

                if (matchesRunId || matchesAssignmentId || matchesProfile)
                {
                    // Enrich with assignment phase from worker-pool
                    var updatedRoute = route with
                    {
                        AssignmentPhase = route.AssignmentPhase ?? member.CurrentAssignment.Phase,
                        AgentInstanceId = route.AgentInstanceId ?? member.AgentInstanceId,
                        PoolMemberId = route.PoolMemberId ?? member.PoolMemberId,
                    };
                    var idx = candidates.IndexOf(route);
                    candidates[idx] = updatedRoute;
                }
            }
        }
    }

    private static bool IsRouteStale(ActiveWorkRouteDto route)
    {
        if (string.IsNullOrWhiteSpace(route.LastActivityAt)) return true;
        if (DateTime.TryParse(route.LastActivityAt, out var lastActivity))
        {
            return DateTime.UtcNow - lastActivity > StaleThreshold;
        }
        return true;
    }

    private static int CompareTimestamps(string? a, string? b)
    {
        var hasA = DateTime.TryParse(a, out var dtA);
        var hasB = DateTime.TryParse(b, out var dtB);
        if (hasA && hasB) return dtA.CompareTo(dtB);
        if (hasA) return 1;
        if (hasB) return -1;
        return 0;
    }

    private static string BuildNoRouteReason(
        ResolveActiveWorkRouteRequest request,
        IReadOnlyList<ActiveWorkRouteSourceEvidenceDto> sources)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.TargetProjectId))
            filters.Add($"project={request.TargetProjectId}");
        if (request.TargetTaskId.HasValue)
            filters.Add($"task={request.TargetTaskId}");
        if (!string.IsNullOrWhiteSpace(request.AssignmentId))
            filters.Add($"assignment={request.AssignmentId}");
        if (!string.IsNullOrWhiteSpace(request.WorkerRunId))
            filters.Add($"run={request.WorkerRunId}");
        if (!string.IsNullOrWhiteSpace(request.ProfileIdentity))
            filters.Add($"profile={request.ProfileIdentity}");

        var filterDesc = filters.Count > 0 ? string.Join(", ", filters) : "no filters";
        var sourceSummary = string.Join("; ", sources.Select(s =>
            $"{s.Source}: {(s.Available ? $"{s.RecordsExamined} records" : "unavailable")}"));

        return $"No active route found for [{filterDesc}]. Sources consulted: {sourceSummary}";
    }
}
