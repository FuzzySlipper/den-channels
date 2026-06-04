using DenChannels.Service.AgentsOverview;
using DenChannels.Service.Channels;

namespace DenChannels.Service.AgentsOverview;

/// <summary>
/// Composes read-only Agents Overview data from Channels repository memberships,
/// WorkerPool state, and activity events.
/// Gateway binding/delivery tracking has been removed — the overview only
/// shows Channels memberships + WorkerPool state + activity events.
/// </summary>
public sealed class AgentsOverviewService
{
    private readonly ChannelsRepository _repository;
    private readonly IWorkerPoolStateClient _workerPoolClient;
    private readonly ILogger<AgentsOverviewService> _logger;

    public AgentsOverviewService(ChannelsRepository repository,
        IWorkerPoolStateClient workerPoolClient,
        ILogger<AgentsOverviewService> logger)
    {
        _repository = repository;
        _workerPoolClient = workerPoolClient;
        _logger = logger;
    }

    /// <summary>
    /// Produce the list overview for GET /api/agents/overview.
    /// </summary>
    public async Task<AgentsOverviewResponse> GetOverviewAsync(
        string? projectId = null, long? channelId = null, string? scope = null,
        string? agentIdentity = null, int activityLimit = 3, bool includeLeft = false,
        CancellationToken cancellationToken = default)
    {
        // scope=all => no project filter; scope=project (default) => use projectId.
        var effectiveProjectId = string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase) ? null : projectId;

        // 1. Fetch Worker-pool state (best-effort, always fetched independently)
        WorkerPoolStateDto? workerPoolState = null;
        SourceServiceStatusDto? workerPoolHealth = null;
        workerPoolState = await _workerPoolClient.FetchWorkersAsync(effectiveProjectId, agentIdentity, cancellationToken);
        workerPoolHealth = workerPoolState is not null
            ? new SourceServiceStatusDto("available")
            : new SourceServiceStatusDto("unavailable", "Core worker-pool endpoint did not respond. Workers shown without pool assignment state.");

        var channelsHealth = new SourceServiceStatusDto("available");

        // 2. Channel scope has already been resolved above for Channels queries.

        // 3. Fetch channels (if channelId specified, filter to that channel)
        var channels = await _repository.ListChannelsForOverviewAsync(effectiveProjectId, channelId, cancellationToken);

        // If a specific channelId was provided, verify it exists
        if (channelId.HasValue && channels.Count == 0)
        {
            channelsHealth = new SourceServiceStatusDto("unavailable", $"Channel {channelId} not found.");
        }

        // 4. Fetch memberships across all matching channels
        var allMemberships = await _repository.ListMembershipsForOverviewAsync(
            effectiveProjectId, channelId, agentIdentity, includeLeft, cancellationToken);

        // 5. Fetch recent activity
        var allActivity = await _repository.ListRecentActivityForOverviewAsync(
            effectiveProjectId, channelId, agentIdentity, activityLimit, cancellationToken);

        // 6. Build channel lookup
        var channelLookup = channels.ToDictionary(c => c.Id);

        // 7. Group memberships by agent identity
        var membershipByAgent = allMemberships
            .GroupBy(m => m.MemberIdentity)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 8. Group activity by agent identity
        var activityByAgent = allActivity
            .GroupBy(a => a.AgentIdentity)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 9. Build Worker-pool member lookup
        var workerPoolMembers = (workerPoolState?.Members ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.MemberIdentity))
            .ToList();
        var workerPoolByIdentity = workerPoolMembers
            .GroupBy(m => m.MemberIdentity!)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // 10. Collect all unique agent identities
        var allAgentIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in allMemberships)
            allAgentIdentities.Add(m.MemberIdentity);
        foreach (var a in allActivity)
            allAgentIdentities.Add(a.AgentIdentity);
        foreach (var w in workerPoolMembers)
        {
            if (!string.IsNullOrWhiteSpace(w.MemberIdentity))
                allAgentIdentities.Add(w.MemberIdentity);
        }

        // 11. Build overview items
        var items = new List<AgentOverviewItem>();
        foreach (var identity in allAgentIdentities.OrderBy(id => id))
        {
            var memberships = membershipByAgent.TryGetValue(identity, out var mList) ? mList : [];
            var activityEvents = activityByAgent.TryGetValue(identity, out var aList) ? aList : [];

            var flags = new List<string>();
            if (memberships.Count == 0) flags.Add("missing_membership");
            if (activityEvents.Count > 0 && memberships.Count == 0) flags.Add("activity_without_membership");

            // Determine operator status from most recent membership
            var latestMembership = memberships.MaxBy(m => m.UpdatedAt);
            var operatorStatus = latestMembership?.MembershipStatus ?? "unknown";

            // Derive live work state from activity events only
            var (activityWorkState, activitySeverity) = DeriveWorkStateAndSeverity(activityEvents);
            var workState = activityWorkState ?? "idle";
            var severity = DeriveSeverity(workState, activitySeverity);

            // Build membership overview DTOs
            var membershipOverviews = memberships
                .Select(m =>
                {
                    channelLookup.TryGetValue(m.ChannelId, out var ch);
                    return new ChannelMembershipOverviewDto(
                        m.ChannelId,
                        ch?.Slug ?? $"channel-{m.ChannelId}",
                        ch?.DisplayName ?? $"Channel {m.ChannelId}",
                        ch?.Kind ?? "unknown",
                        ch?.ProjectId,
                        m.MembershipStatus,
                        m.WakePolicy,
                        m.CanSend,
                        SafeSettingsLabel(m.SettingsJson),
                        m.MembershipPurpose);
                })
                .ToList();

            // Build recent activity DTOs
            var recentActivityDtos = activityEvents
                .Take(activityLimit)
                .Select(a => new ActivityEventOverviewDto(
                    a.Id, a.ChannelId, a.ProjectId, a.AgentIdentity,
                    a.DeliveryRequestId, a.HermesSessionKey, a.DisplayBlockId,
                    a.WorkerRunId, a.WorkerRole, a.AgentInstanceId, a.PoolMemberId, a.TaskId, a.EventType,
                    a.Status, a.DeliveryStage, a.Terminal, a.Title,
                    a.Summary, a.CreatedAt, a.UpdatedAt))
                .ToList();

            var summaries = new AgentSummaryDto(
                memberships.Count,
                memberships.Count(m => m.MembershipStatus == "active"),
                0, // ActiveDeliveryCount — no Gateway binding tracking
                activityEvents.Count,
                activityEvents.Count > 0 ? activityEvents.Max(a => a.CreatedAt) : null,
                severity);

            // Build links
            var encodedIdentity = Uri.EscapeDataString(identity);
            var links = new AgentLinksDto(
                $"/api/agents/overview?agentIdentity={encodedIdentity}",
                $"/api/gateway/memberships?memberIdentity={encodedIdentity}",
                null, // Bindings link — no Gateway
                activityEvents.Count > 0 ? $"/api/channels?agentIdentity={encodedIdentity}&activity=true" : null);

            // Compose worker-pool state (if available)
            WorkerPoolMemberDto? workerPoolStateDto = null;
            WorkerPoolAssignmentDto? workerPoolAssignmentDto = null;
            AssignmentTraceHandlesDto? traceHandles = null;
            if (workerPoolByIdentity.TryGetValue(identity, out var workerPoolMember))
            {
                (workerPoolStateDto, workerPoolAssignmentDto, traceHandles) = ComposeWorkerPoolProjection(
                    workerPoolMember,
                    memberships,
                    channelId,
                    flags);
            }
            else if (workerPoolState is not null)
            {
                flags.Add("worker_pool_orphaned");
            }

            // Build child runs for this agent identity from WorkerPool members (#1806)
            var childRuns = BuildChildRunStates(workerPoolMembers, identity);
            var childRunCount = childRuns.Count;

            items.Add(new AgentOverviewItem(
                identity, operatorStatus, workState, severity, summaries,
                flags, links,
                membershipOverviews.Count > 0 ? membershipOverviews : null,
                Bindings: null,
                DeliverySummaries: null,
                recentActivityDtos.Count > 0 ? recentActivityDtos : null,
                WorkerPoolState: workerPoolStateDto,
                CurrentAssignment: workerPoolAssignmentDto,
                AssignmentTrace: traceHandles,
                ChildRuns: childRuns.Count > 0 ? childRuns : null,
                ChildRunCount: childRunCount));
        }

        var sourceHealth = new SourceHealthDto(channelsHealth, workerPoolHealth);

        return new AgentsOverviewResponse(items, items.Count, sourceHealth);
    }

    /// <summary>
    /// Produce the detail view for GET /api/agents/{agentIdentity}/overview.
    /// </summary>
    public async Task<AgentDetailResponse> GetAgentDetailAsync(
        string agentIdentity, string? projectId = null, long? channelId = null,
        int activityLimit = 50, int deliveryLimit = 50,
        CancellationToken cancellationToken = default)
    {
        var channelsHealth = new SourceServiceStatusDto("available");

        // 1. Fetch Worker-pool state (best-effort)
        WorkerPoolStateDto? workerPoolState = null;
        SourceServiceStatusDto? workerPoolHealth = null;
        workerPoolState = await _workerPoolClient.FetchWorkersAsync(projectId, agentIdentity, cancellationToken);
        workerPoolHealth = workerPoolState is not null
            ? new SourceServiceStatusDto("available")
            : new SourceServiceStatusDto("unavailable", "Core worker-pool endpoint did not respond.");

        // 2. Fetch memberships for this agent
        var memberships = await _repository.ListMembershipsForOverviewAsync(
            projectId, channelId, agentIdentity, true, cancellationToken);

        // 3. Fetch channels for membership resolution
        var channels = await _repository.ListChannelsForOverviewAsync(projectId, channelId, cancellationToken);
        var channelLookup = channels.ToDictionary(c => c.Id);

        // 4. Fetch activity events for this agent
        var activityEvents = await _repository.ListRecentActivityForDetailAsync(
            agentIdentity, projectId, channelId, activityLimit, cancellationToken);

        // 5. Fetch task associations
        var taskEvents = await _repository.ListTaskActivityForDetailAsync(
            agentIdentity, projectId, channelId, cancellationToken);

        // 6. Build flags
        var flags = new List<string>();
        if (memberships.Count == 0) flags.Add("missing_membership");
        if (activityEvents.Count > 0 && memberships.Count == 0) flags.Add("activity_without_membership");

        // 7. Build membership overviews
        var membershipOverviews = memberships
            .Select(m =>
            {
                channelLookup.TryGetValue(m.ChannelId, out var ch);
                return new ChannelMembershipOverviewDto(
                    m.ChannelId,
                    ch?.Slug ?? $"channel-{m.ChannelId}",
                    ch?.DisplayName ?? $"Channel {m.ChannelId}",
                    ch?.Kind ?? "unknown",
                    ch?.ProjectId,
                    m.MembershipStatus,
                    m.WakePolicy,
                    m.CanSend,
                    SafeSettingsLabel(m.SettingsJson),
                    m.MembershipPurpose);
            })
            .ToList();

        // 8. Build activity event DTOs
        var activityDtos = activityEvents
            .Select(a => new ActivityEventOverviewDto(
                a.Id, a.ChannelId, a.ProjectId, a.AgentIdentity,
                a.DeliveryRequestId, a.HermesSessionKey, a.DisplayBlockId,
                a.WorkerRunId, a.WorkerRole, a.AgentInstanceId, a.PoolMemberId, a.TaskId, a.EventType,
                a.Status, a.DeliveryStage, a.Terminal, a.Title,
                a.Summary, a.CreatedAt, a.UpdatedAt))
            .ToList();

        // 9. Build task associations from both dedicated task events and recent activity
        //     that carries task/run context
        var activityWithTaskContext = activityEvents
            .Where(a => a.TaskId.HasValue)
            .ToList();
        var allTaskEvents = taskEvents
            .Concat(activityWithTaskContext)
            .GroupBy(a => a.Id) // dedupe by activity event ID
            .Select(g => g.First())
            .ToList();
        var taskAssociations = allTaskEvents
            .GroupBy(a => (a.TaskId, a.ProjectId))
            .Select(g =>
            {
                var latest = g.MaxBy(a => a.CreatedAt);
                var terminalCount = g.Count(a => a.Terminal);
                var status = terminalCount == g.Count() && g.Count() > 0 ? "completed" : "in_progress";
                return new TaskAssociationDto(
                    g.Key.TaskId,
                    g.Key.ProjectId,
                    latest?.Title,
                    status,
                    g.Count(),
                    latest?.CreatedAt);
            })
            .OrderByDescending(t => t.LatestActivityAt)
            .ToList();

        // 10. Build summary from activity evidence only
        var (activityDetailWorkState, activityDetailSeverity) = DeriveWorkStateAndSeverity(activityEvents);
        var detailWorkState = activityDetailWorkState ?? "idle";
        var detailSeverity = DeriveSeverity(detailWorkState, activityDetailSeverity);

        var summary = new AgentSummaryDto(
            memberships.Count,
            memberships.Count(m => m.MembershipStatus == "active"),
            0, // ActiveDeliveryCount — no Gateway binding tracking
            activityEvents.Count,
            activityEvents.Count > 0 ? activityEvents.Max(a => a.CreatedAt) : null,
            detailSeverity);

        var sourceHealth = new SourceHealthDto(channelsHealth, workerPoolHealth);

        // Compose worker-pool state for this agent
        WorkerPoolMemberDto? detailWorkerPoolState = null;
        WorkerPoolAssignmentDto? detailWorkerPoolAssignment = null;
        AssignmentTraceHandlesDto? detailTraceHandles = null;
        if (workerPoolState?.Members is not null)
        {
            var workerPoolMember = workerPoolState.Members
                .FirstOrDefault(m => string.Equals(m.MemberIdentity, agentIdentity, StringComparison.OrdinalIgnoreCase));

            if (workerPoolMember is not null)
            {
                (detailWorkerPoolState, detailWorkerPoolAssignment, detailTraceHandles) = ComposeWorkerPoolProjection(
                    workerPoolMember,
                    memberships,
                    channelId,
                    flags);
            }
            else
            {
                flags.Add("worker_pool_orphaned");
            }
        }

        // Build child runs for agent detail (#1806)
        var detailChildRuns = BuildChildRunStates(workerPoolState?.Members ?? [], agentIdentity);
        var detailChildRunCount = detailChildRuns.Count;

        return new AgentDetailResponse(
            agentIdentity,
            membershipOverviews.Count > 0 ? membershipOverviews : null,
            Bindings: null,
            CurrentDeliveries: null,
            RecentDeliveries: null,
            activityDtos.Count > 0 ? activityDtos : null,
            taskAssociations.Count > 0 ? taskAssociations : null,
            summary,
            flags,
            sourceHealth,
            WorkerPoolState: detailWorkerPoolState,
            CurrentAssignment: detailWorkerPoolAssignment,
            AssignmentTrace: detailTraceHandles,
            ChildRuns: detailChildRuns.Count > 0 ? detailChildRuns : null,
            ChildRunCount: detailChildRunCount);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static (WorkerPoolMemberDto Member, WorkerPoolAssignmentDto? Assignment, AssignmentTraceHandlesDto? Trace)
        ComposeWorkerPoolProjection(
            WorkerPoolMemberStateDto workerPoolMember,
            IReadOnlyList<ChannelMembershipDto> memberships,
            long? channelId,
            List<string> flags)
    {
        var currentAssignment = workerPoolMember.CurrentAssignment;
        var workerPoolAssignmentDto = currentAssignment is not null
            ? new WorkerPoolAssignmentDto(
                currentAssignment.AssignmentId,
                currentAssignment.TaskId,
                currentAssignment.ProjectId,
                currentAssignment.LeaseOwner,
                currentAssignment.LeaseExpiresAt,
                currentAssignment.Phase,
                currentAssignment.CheckpointType,
                currentAssignment.CheckpointHandle,
                currentAssignment.LastCheckpointAt)
            : null;

        var workerPoolStateDto = new WorkerPoolMemberDto(
            workerPoolMember.MemberIdentity,
            workerPoolMember.Role,
            workerPoolMember.ToolProfile,
            workerPoolMember.AgentInstanceId,
            workerPoolMember.PoolMemberId,
            workerPoolMember.RunId,
            workerPoolMember.Availability,
            workerPoolMember.LastActivityAt,
            workerPoolAssignmentDto,
            workerPoolMember.Flags);

        AddWorkerPoolFlags(workerPoolMember, flags);

        var traceChannelId = channelId ?? memberships.FirstOrDefault()?.ChannelId;
        var traceHandles = new AssignmentTraceHandlesDto(
            currentAssignment?.AssignmentId,
            traceChannelId,
            RepresentativeMessageId: null,
            ActivityHandle: currentAssignment?.AssignmentId is not null
                ? $"/api/assignments/{currentAssignment.AssignmentId}/transcript"
                : null,
            DeliveryHandle: null); // No Gateway delivery tracking

        return (workerPoolStateDto, workerPoolAssignmentDto, traceHandles);
    }

    private static void AddWorkerPoolFlags(
        WorkerPoolMemberStateDto workerPoolMember,
        List<string> flags)
    {
        var availability = workerPoolMember.Availability ?? string.Empty;
        if (string.Equals(availability, "leased", StringComparison.OrdinalIgnoreCase))
            AddFlag(flags, "worker_pool_leased");
        else if (string.Equals(availability, "quarantined", StringComparison.OrdinalIgnoreCase))
            AddFlag(flags, "worker_pool_quarantined");
        else if (string.Equals(availability, "draining", StringComparison.OrdinalIgnoreCase))
            AddFlag(flags, "worker_pool_draining");
        else if (string.Equals(availability, "offline", StringComparison.OrdinalIgnoreCase))
            AddFlag(flags, "worker_pool_offline");

        if (workerPoolMember.Flags is not null)
        {
            foreach (var flag in workerPoolMember.Flags)
                AddFlag(flags, flag);
        }

        if (workerPoolMember.CurrentAssignment?.Phase is not null &&
            string.Equals(workerPoolMember.CurrentAssignment.Phase, "cleanup_pending", StringComparison.OrdinalIgnoreCase))
        {
            AddFlag(flags, "cleanup_pending");
        }
    }

    private static void AddFlag(List<string> flags, string flag)
    {
        if (!flags.Contains(flag, StringComparer.OrdinalIgnoreCase))
            flags.Add(flag);
    }

    /// <summary>
    /// Derive workState and severity from activity events.
    /// - workState: "idle" if no events, "active" if any non-terminal events, "completed" if all terminal.
    /// - delivered and completed are distinct: "delivered" requires terminal+completed status.
    /// </summary>
    private static (string? WorkState, string? Severity) DeriveWorkStateAndSeverity(
        IReadOnlyList<ChannelActivityEventDto> activityEvents)
    {
        if (activityEvents.Count == 0)
            return ("idle", null);

        var hasActive = activityEvents.Any(a => !a.Terminal);
        var allTerminal = activityEvents.All(a => a.Terminal);
        var hasFailed = activityEvents.Any(a => a.Status == "failed");
        var hasCompleted = activityEvents.Any(a => a.Status == "completed" && a.Terminal);

        string workState = hasActive ? "active" : (allTerminal && hasCompleted ? "completed" : "idle");
        string? severity = hasFailed ? "error" : (hasActive ? "info" : (hasCompleted ? "success" : null));

        return (workState, severity);
    }

    private static string? DeriveSeverity(string? workState, string? fallbackSeverity) => workState switch
    {
        "failed" or "stuck" => "error",
        "pending" or "delivering" or "delivered_waiting_completion" or "acknowledged" or "active" => "info",
        "completed" => "success",
        _ => fallbackSeverity
    };

    private static string? SafeSettingsLabel(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

            var parts = new List<string>();
            AddAllowedSettingsPart(document.RootElement, parts, "profile");
            AddAllowedSettingsPart(document.RootElement, parts, "profileName");
            AddAllowedSettingsPart(document.RootElement, parts, "profile_id");
            AddAllowedSettingsPart(document.RootElement, parts, "binding");
            AddAllowedSettingsPart(document.RootElement, parts, "bindingName");
            return parts.Count == 0 ? null : string.Join(" · ", parts.Distinct());
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static void AddAllowedSettingsPart(System.Text.Json.JsonElement root, ICollection<string> parts, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String) return;
        var text = value.GetString()?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
            parts.Add($"{propertyName}: {text}");
    }

    // =========================================================================
    // Shared-profile pool child-run state builder (#1806)
    // =========================================================================

    /// <summary>
    /// Build child-run states from WorkerPool members sharing the same agent identity
    /// and ToolProfile (profile identity). Each member = one child-run slot.
    /// Status derived from availability + current assignment phase.
    /// </summary>
    public static List<ChildRunStateDto> BuildChildRunStates(
        IReadOnlyList<WorkerPoolMemberStateDto> members,
        string? agentIdentity)
    {
        // Group members by agent identity (MemberIdentity is the pool member/slot id)
        var relevant = agentIdentity is not null
            ? members.Where(m => string.Equals(m.MemberIdentity, agentIdentity, StringComparison.OrdinalIgnoreCase)).ToList()
            : members.ToList();

        return relevant.Select(m =>
        {
            var flags = new List<string>();
            var status = m.Availability.ToLowerInvariant() switch
            {
                "leased" => "busy",
                "quarantined" => "quarantined",
                "offline" => "stale",
                "available" => "available",
                var other => other
            };

            if (m.CurrentAssignment is { Phase: "cleanup_pending" })
                flags.Add("cleanup_pending");
            if (m.Flags?.Contains("core_busy_without_assignment") == true)
                flags.Add("busy_without_assignment");
            if (m.Flags?.Contains("core_offboarded") == true)
                flags.Add("core_offboarded");

            var supervisorTarget = m.MemberIdentity; // supervisor profile identity

            return new ChildRunStateDto(
                AgentInstanceId: m.AgentInstanceId,
                RunId: m.RunId ?? m.CurrentAssignment?.RunId,
                WorkerRunId: m.RunId,
                AssignmentId: m.CurrentAssignment?.AssignmentId,
                PoolMemberId: m.PoolMemberId ?? m.MemberIdentity,
                ProfileIdentity: m.ToolProfile,
                Status: status,
                LastActivityAt: m.LastActivityAt,
                Flags: flags.Count > 0 ? flags : null)
            {
                SupervisorDeliveryTarget = supervisorTarget,
            };
        }).ToList();
    }
}
