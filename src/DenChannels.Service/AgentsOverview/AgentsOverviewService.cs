using DenChannels.Service.AgentsOverview;
using DenChannels.Service.Channels;
using DenChannels.Service.Gateway;

namespace DenChannels.Service.AgentsOverview;

/// <summary>
/// Composes read-only Agents Overview data from Channels repository memberships,
/// Gateway state projection, and activity events.
/// </summary>
public sealed class AgentsOverviewService
{
    private readonly ChannelsRepository _repository;
    private readonly GatewayStateClient _gatewayClient;
    private readonly IWorkerPoolStateClient _workerPoolClient;
    private readonly ILogger<AgentsOverviewService> _logger;

    public AgentsOverviewService(ChannelsRepository repository, GatewayStateClient gatewayClient,
        IWorkerPoolStateClient workerPoolClient,
        ILogger<AgentsOverviewService> logger)
    {
        _repository = repository;
        _gatewayClient = gatewayClient;
        _workerPoolClient = workerPoolClient;
        _logger = logger;
    }

    /// <summary>
    /// Produce the list overview for GET /api/agents/overview.
    /// </summary>
    public async Task<AgentsOverviewResponse> GetOverviewAsync(
        string? projectId = null, long? channelId = null, string? scope = null,
        string? agentIdentity = null, int activityLimit = 3, bool includeLeft = false,
        bool includeGateway = true, CancellationToken cancellationToken = default)
    {
        // scope=all => no project filter; scope=project (default) => use projectId.
        var effectiveProjectId = string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase) ? null : projectId;

        // 1. Fetch Gateway state (best-effort)
        GatewayStateDto? gatewayState = null;
        SourceServiceStatusDto? gatewayHealth = null;
        if (includeGateway)
        {
            gatewayState = await _gatewayClient.FetchGatewayStateAsync(effectiveProjectId, agentIdentity, cancellationToken);
            gatewayHealth = gatewayState is not null
                ? new SourceServiceStatusDto("available")
                : new SourceServiceStatusDto("unavailable", "Gateway state endpoint did not respond. Only Channels data is available.");
        }

        // 1b. Fetch Worker-pool state (best-effort, always fetched independently)
        WorkerPoolStateDto? workerPoolState = null;
        SourceServiceStatusDto? workerPoolHealth = null;
        workerPoolState = await _workerPoolClient.FetchWorkersAsync(effectiveProjectId, agentIdentity, cancellationToken);
        workerPoolHealth = workerPoolState is not null
            ? new SourceServiceStatusDto("available")
            : new SourceServiceStatusDto("unavailable", "Core worker-pool endpoint did not respond. Workers shown without pool assignment state.");

        var channelsHealth = new SourceServiceStatusDto("available");

        // 2. Channel scope has already been resolved above for both Gateway and Channels queries.

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

        // 9. Build Gateway agent lookup. Gateway can pre-filter project/agent, but
        // Channels owns channel scope, so keep channel-scoped Gateway-only rows out
        // unless the agent also appears in scoped Channels data or a Gateway delivery
        // explicitly targets the requested channel.
        var scopedChannelsAgentIdentities = allMemberships.Select(m => m.MemberIdentity)
            .Concat(allActivity.Select(a => a.AgentIdentity))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gatewayAgents = (gatewayState?.Agents ?? [])
            .Where(g => GatewayAgentMatchesScope(g, effectiveProjectId, channelId, agentIdentity, scopedChannelsAgentIdentities))
            .ToList();
        var gatewayByIdentity = gatewayAgents
            .Where(a => !string.IsNullOrWhiteSpace(a.AgentIdentity))
            .GroupBy(a => a.AgentIdentity!)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 9b. Build Worker-pool member lookup
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
        foreach (var g in gatewayAgents)
        {
            if (!string.IsNullOrWhiteSpace(g.AgentIdentity))
                allAgentIdentities.Add(g.AgentIdentity);
        }
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
            var gatewayEntries = gatewayByIdentity.TryGetValue(identity, out var gList) ? gList : [];
            var activityEvents = activityByAgent.TryGetValue(identity, out var aList) ? aList : [];

            var flags = new List<string>();
            if (memberships.Count == 0) flags.Add("missing_membership");
            if (gatewayEntries.Count == 0 && includeGateway) flags.Add("missing_binding");
            if (gatewayState is null && includeGateway) flags.Add("gateway_unavailable");
            if (activityEvents.Count > 0 && memberships.Count == 0) flags.Add("activity_without_membership");

            // Determine operator status from most recent membership
            var latestMembership = memberships.MaxBy(m => m.UpdatedAt);
            var operatorStatus = latestMembership?.MembershipStatus ?? "unknown";

            // Combine Gateway delivery evidence and activity events to derive live work state.
            // Recent non-terminal activity overrides stale Gateway stuck state.
            // Stale delivery debt remains visible via flags and counts.
            var scopedDeliveries = gatewayEntries
                .SelectMany(g => ScopedGatewayDeliveries(g, effectiveProjectId, channelId))
                .ToList();
            var (activityWorkState, activitySeverity) = DeriveWorkStateAndSeverity(activityEvents);

            // Use combined derivation when Gateway data exists; fall back to activity-only
            string workState;
            string? severity;
            if (scopedDeliveries.Count > 0)
            {
                (workState, severity) = DeriveWorkStateFromGatewayAndActivity(scopedDeliveries, activityEvents);
            }
            else
            {
                workState = activityWorkState ?? "idle";
                severity = DeriveSeverity(workState, activitySeverity);
            }

            // Detect stale gateway delivery debt
            var hasStaleDebt = HasStaleGatewayDebt(scopedDeliveries, activityEvents);
            var staleDebtCount = CountStaleGatewayDebt(scopedDeliveries, activityEvents);
            if (hasStaleDebt) flags.Add("stale_delivery_debt");

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

            // Build binding overview DTOs
            var bindingOverviews = gatewayEntries
                .Select(g => new GatewayBindingOverviewDto(
                    g.AgentKey,
                    g.Role,
                    g.BindingFreshness,
                    ScopedGatewayDeliveryState(g, effectiveProjectId, channelId),
                    ScopedGatewayDeliveryCounts(g, effectiveProjectId, channelId),
                    g.AdapterInstances))
                .ToList();

            // Build delivery summaries from Gateway. Keep delivered and completed distinct:
            // delivered is non-terminal/current; completed is terminal/recent.
            // Mark deliveries as stale when they have no recent activity.
            var liveDeliveryIds = FindLiveDeliveryIds(activityEvents);
            var deliverySummaries = gatewayEntries
                .SelectMany(g => ScopedGatewayDeliveries(g, effectiveProjectId, channelId))
                .Select(d =>
                {
                    var baseDto = ToDeliveryOverview(d);
                    var isStale = !d.Terminal && !liveDeliveryIds.Contains(d.RequestId ?? string.Empty);
                    return new DeliveryOverviewDto(
                        DeliveryRequestId: baseDto.DeliveryRequestId,
                        State: baseDto.State,
                        Status: baseDto.Status,
                        Terminal: baseDto.Terminal,
                        CreatedAt: baseDto.CreatedAt,
                        UpdatedAt: baseDto.UpdatedAt,
                        Summary: baseDto.Summary,
                        IsStale: isStale);
                })
                // Sort: live deliveries first, then stale ones
                .OrderBy(d => d.IsStale)
                .ThenByDescending(d => d.CreatedAt)
                .ToList();

            // Fallback: if Gateway has summary-level but no detail-level deliveries
            if (deliverySummaries.Count == 0)
            {
                deliverySummaries = gatewayEntries
                    .Where(g => g.DeliverySummary is not null)
                    .Select(g => new DeliveryOverviewDto(
                        null,
                        g.DeliverySummary!.State,
                        null,
                        false,
                        null,
                        null,
                        null))
                    .ToList();
            }

            // If no Gateway delivery summaries, derive from activity events
            if (deliverySummaries.Count == 0)
            {
                var terminalActivities = activityEvents.Where(a => a.Terminal).ToList();
                if (terminalActivities.Count > 0)
                {
                    var latestTerminal = terminalActivities.MaxBy(a => a.UpdatedAt);
                    deliverySummaries.Add(new DeliveryOverviewDto(
                        latestTerminal?.DeliveryRequestId,
                        latestTerminal?.Terminal == true ? "completed" : "active",
                        latestTerminal?.Status,
                        latestTerminal?.Terminal ?? false,
                        latestTerminal?.CreatedAt,
                        latestTerminal?.UpdatedAt,
                        latestTerminal?.Summary));
                }
            }

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
                gatewayEntries.Sum(g => ActiveGatewayDeliveryCount(g, effectiveProjectId, channelId)),
                activityEvents.Count,
                activityEvents.Count > 0 ? activityEvents.Max(a => a.CreatedAt) : null,
                severity,
                staleDebtCount);

            // Build links
            var encodedIdentity = Uri.EscapeDataString(identity);
            var links = new AgentLinksDto(
                $"/api/agents/overview?agentIdentity={encodedIdentity}",
                $"/api/gateway/memberships?memberIdentity={encodedIdentity}",
                bindingOverviews.Count > 0 ? $"/api/gateway/agent-overview/gateway-state" : null,
                activityEvents.Count > 0 ? $"/api/channels?agentIdentity={encodedIdentity}&activity=true" : null);

            // Compose worker-pool state (if available)
            WorkerPoolMemberDto? workerPoolStateDto = null;
            WorkerPoolAssignmentDto? workerPoolAssignmentDto = null;
            AssignmentTraceHandlesDto? traceHandles = null;
            if (workerPoolByIdentity.TryGetValue(identity, out var workerPoolMember))
            {
                (workerPoolStateDto, workerPoolAssignmentDto, traceHandles) = ComposeWorkerPoolProjection(
                    workerPoolMember,
                    scopedDeliveries,
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
                bindingOverviews.Count > 0 ? bindingOverviews : null,
                deliverySummaries.Count > 0 ? deliverySummaries : null,
                recentActivityDtos.Count > 0 ? recentActivityDtos : null,
                WorkerPoolState: workerPoolStateDto,
                CurrentAssignment: workerPoolAssignmentDto,
                AssignmentTrace: traceHandles,
                ChildRuns: childRuns.Count > 0 ? childRuns : null,
                ChildRunCount: childRunCount));
        }

        var sourceHealth = new SourceHealthDto(channelsHealth, gatewayHealth, workerPoolHealth);

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
        // 1. Fetch Gateway state (best-effort)
        GatewayStateDto? gatewayState = null;
        SourceServiceStatusDto? gatewayHealth = null;
        gatewayState = await _gatewayClient.FetchGatewayStateAsync(projectId, agentIdentity, cancellationToken);
        gatewayHealth = gatewayState is not null
            ? new SourceServiceStatusDto("available")
            : new SourceServiceStatusDto("unavailable", "Gateway state endpoint did not respond.");

        var channelsHealth = new SourceServiceStatusDto("available");

        // 1b. Fetch Worker-pool state (best-effort)
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

        // 6. Build Gateway entries
        var scopedDetailAgentIdentities = memberships.Select(m => m.MemberIdentity)
            .Concat(activityEvents.Select(a => a.AgentIdentity))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gatewayEntries = gatewayState?.Agents
            .Where(a => string.Equals(a.AgentIdentity, agentIdentity, StringComparison.OrdinalIgnoreCase))
            .Where(a => GatewayAgentMatchesScope(a, projectId, channelId, agentIdentity, scopedDetailAgentIdentities))
            .ToList() ?? [];

        // 7. Build flags
        var flags = new List<string>();
        if (memberships.Count == 0) flags.Add("missing_membership");
        if (gatewayEntries.Count == 0) flags.Add("missing_binding");
        if (gatewayState is null) flags.Add("gateway_unavailable");
        if (activityEvents.Count > 0 && memberships.Count == 0) flags.Add("activity_without_membership");

        // 8. Build membership overviews
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

        // 9. Build binding overviews from Gateway
        var bindingOverviews = gatewayEntries
            .Select(g => new GatewayBindingOverviewDto(
                g.AgentKey,
                g.Role,
                g.BindingFreshness,
                ScopedGatewayDeliveryState(g, projectId, channelId),
                ScopedGatewayDeliveryCounts(g, projectId, channelId),
                g.AdapterInstances))
            .ToList();

        // 10. Build current/recent deliveries from Gateway, marked with staleness
        var liveDeliveryIdsDetail = FindLiveDeliveryIds(activityEvents);
        var currentDeliveries = gatewayEntries
            .Where(g => g.CurrentDeliveries is not null)
            .SelectMany(g => g.CurrentDeliveries!.Where(d => GatewayDeliveryMatchesScope(d, projectId, channelId)))
            .Take(deliveryLimit)
            .Select(d =>
            {
                var baseDetail = ToDeliveryOverview(d);
                var isStale = !d.Terminal && !liveDeliveryIdsDetail.Contains(d.RequestId ?? string.Empty);
                return new DeliveryOverviewDto(
                    DeliveryRequestId: baseDetail.DeliveryRequestId,
                    State: baseDetail.State,
                    Status: baseDetail.Status,
                    Terminal: baseDetail.Terminal,
                    CreatedAt: baseDetail.CreatedAt,
                    UpdatedAt: baseDetail.UpdatedAt,
                    Summary: baseDetail.Summary,
                    IsStale: isStale);
            })
            .OrderBy(d => d.IsStale)
            .ThenByDescending(d => d.CreatedAt)
            .ToList();

        var recentDeliveries = gatewayEntries
            .Where(g => g.RecentDeliveries is not null)
            .SelectMany(g => g.RecentDeliveries!.Where(d => GatewayDeliveryMatchesScope(d, projectId, channelId)))
            .Take(deliveryLimit)
            .Select(ToDeliveryOverview)
            .ToList();

        // 11. Build activity event DTOs
        var activityDtos = activityEvents
            .Select(a => new ActivityEventOverviewDto(
                a.Id, a.ChannelId, a.ProjectId, a.AgentIdentity,
                a.DeliveryRequestId, a.HermesSessionKey, a.DisplayBlockId,
                a.WorkerRunId, a.WorkerRole, a.AgentInstanceId, a.PoolMemberId, a.TaskId, a.EventType,
                a.Status, a.DeliveryStage, a.Terminal, a.Title,
                a.Summary, a.CreatedAt, a.UpdatedAt))
            .ToList();

        // 12. Build task associations from both dedicated task events and recent activity
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

        // 13. Build summary - combine Gateway and activity evidence
        var scopedDetailDeliveries = gatewayEntries
            .SelectMany(g => ScopedGatewayDeliveries(g, projectId, channelId))
            .ToList();
        var (activityDetailWorkState, activityDetailSeverity) = DeriveWorkStateAndSeverity(activityEvents);

        string detailWorkState;
        string? detailSeverity;
        if (scopedDetailDeliveries.Count > 0)
        {
            (detailWorkState, detailSeverity) = DeriveWorkStateFromGatewayAndActivity(scopedDetailDeliveries, activityEvents);
        }
        else
        {
            detailWorkState = activityDetailWorkState ?? "idle";
            detailSeverity = DeriveSeverity(detailWorkState, activityDetailSeverity);
        }

        var staleDebtCountDetail = CountStaleGatewayDebt(scopedDetailDeliveries, activityEvents);
        var hasStaleDebtDetail = HasStaleGatewayDebt(scopedDetailDeliveries, activityEvents);
        if (hasStaleDebtDetail) flags.Add("stale_delivery_debt");

        var summary = new AgentSummaryDto(
            memberships.Count,
            memberships.Count(m => m.MembershipStatus == "active"),
            gatewayEntries.Sum(g => ActiveGatewayDeliveryCount(g, projectId, channelId)),
            activityEvents.Count,
            activityEvents.Count > 0 ? activityEvents.Max(a => a.CreatedAt) : null,
            detailSeverity,
            staleDebtCountDetail);

        var sourceHealth = new SourceHealthDto(channelsHealth, gatewayHealth, workerPoolHealth);

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
                    scopedDetailDeliveries,
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
            bindingOverviews.Count > 0 ? bindingOverviews : null,
            currentDeliveries.Count > 0 ? currentDeliveries : null,
            recentDeliveries.Count > 0 ? recentDeliveries : null,
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
            IReadOnlyList<GatewayDeliveryDto> scopedDeliveries,
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

        AddWorkerPoolFlags(workerPoolMember, scopedDeliveries, flags);

        var traceChannelId = channelId ?? memberships.FirstOrDefault()?.ChannelId;
        var traceHandles = new AssignmentTraceHandlesDto(
            currentAssignment?.AssignmentId,
            traceChannelId,
            RepresentativeMessageId: null,
            ActivityHandle: currentAssignment?.AssignmentId is not null
                ? $"/api/assignments/{currentAssignment.AssignmentId}/transcript"
                : null,
            DeliveryHandle: scopedDeliveries.FirstOrDefault(d => !d.Terminal)?.DeliveryId);

        return (workerPoolStateDto, workerPoolAssignmentDto, traceHandles);
    }

    private static void AddWorkerPoolFlags(
        WorkerPoolMemberStateDto workerPoolMember,
        IReadOnlyList<GatewayDeliveryDto> scopedDeliveries,
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

        // Source disagreement is a visible mismatch between Core lease state and
        // Gateway delivery evidence; absence of Gateway delivery evidence alone is
        // not enough to call disagreement.
        if (string.Equals(availability, "leased", StringComparison.OrdinalIgnoreCase) &&
            scopedDeliveries.Count > 0 &&
            scopedDeliveries.All(d => d.Terminal))
        {
            AddFlag(flags, "source_disagreement");
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
    /// Find delivery request IDs that have recent non-terminal activity events,
    /// indicating the delivery is live/current rather than stale debt.
    /// </summary>
    private static HashSet<string> FindLiveDeliveryIds(
        IReadOnlyList<ChannelActivityEventDto> activityEvents)
    {
        return activityEvents
            .Where(a => !string.IsNullOrWhiteSpace(a.DeliveryRequestId))
            .Where(a => !a.Terminal || a.EventType != "noop")
            .Select(a => a.DeliveryRequestId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Combine Gateway delivery evidence and Channels activity events to derive
    /// the live agent work state. Activity events indicate current liveness;
    /// old Gateway stuck deliveries without recent activity are stale debt.
    /// </summary>
    private static (string WorkState, string? Severity) DeriveWorkStateFromGatewayAndActivity(
        IReadOnlyList<GatewayDeliveryDto>? gatewayDeliveries,
        IReadOnlyList<ChannelActivityEventDto> activityEvents)
    {
        var deliveries = (gatewayDeliveries ?? []).ToList();

        // Check activity first: any non-terminal activity means agent is actively working
        var hasNonTerminalActivity = activityEvents.Any(a => !a.Terminal);
        if (hasNonTerminalActivity)
            return ("active", "info");

        // Check if any Gateway delivery with "stuck" flag has NO recent activity → stale debt
        var gatewayHasStuck = deliveries.Any(d => d.Flags?.Contains("stuck") == true);
        var gatewayHasActiveNonTerminal = deliveries.Any(d => !d.Terminal && d.Status != "completed" && d.Status != "expired");

        // If all activity is terminal and no stuck Gateway deliveries → derive from activity
        if (!gatewayHasStuck && !gatewayHasActiveNonTerminal)
        {
            var (activityState, activitySeverity) = DeriveWorkStateAndSeverity(activityEvents);
            return (activityState ?? "idle", activitySeverity);
        }

        // If there are stuck deliveries but all recent activity is terminal,
        // the stuck state from Gateway is the real state
        if (gatewayHasStuck)
            return ("stuck", "error");

        // Remaining active Gateway deliveries without activity
        if (gatewayHasActiveNonTerminal)
            return ("delivering", "info");

        // Fallback to activity-derived state
        var (fallbackState, fallbackSeverity) = DeriveWorkStateAndSeverity(activityEvents);
        return (fallbackState ?? "idle", fallbackSeverity);
    }

    /// <summary>
    /// Determine if there are stale Gateway delivery debt entries that should be flagged.
    /// An entry is stale if it is non-terminal or stuck, and has no recent associated activity.
    /// </summary>
    private static bool HasStaleGatewayDebt(
        IReadOnlyList<GatewayDeliveryDto>? gatewayDeliveries,
        IReadOnlyList<ChannelActivityEventDto> activityEvents)
    {
        if (gatewayDeliveries is null || gatewayDeliveries.Count == 0)
            return false;

        var liveDeliveryIds = FindLiveDeliveryIds(activityEvents);

        return gatewayDeliveries.Any(d =>
            (!d.Terminal || d.Flags?.Contains("stuck") == true) &&
            !liveDeliveryIds.Contains(d.RequestId ?? string.Empty));
    }

    /// <summary>
    /// Count stale Gateway delivery debt entries.
    /// </summary>
    private static int CountStaleGatewayDebt(
        IReadOnlyList<GatewayDeliveryDto>? gatewayDeliveries,
        IReadOnlyList<ChannelActivityEventDto> activityEvents)
    {
        if (gatewayDeliveries is null || gatewayDeliveries.Count == 0)
            return 0;

        var liveDeliveryIds = FindLiveDeliveryIds(activityEvents);

        return gatewayDeliveries.Count(d =>
            (!d.Terminal || d.Flags?.Contains("stuck") == true) &&
            !liveDeliveryIds.Contains(d.RequestId ?? string.Empty));
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

    public static bool GatewayAgentMatchesScope(
        GatewayAgentDto agent,
        string? projectId,
        long? channelId,
        string? agentIdentity,
        IReadOnlySet<string>? scopedChannelsAgentIdentities = null)
    {
        if (!string.IsNullOrWhiteSpace(agentIdentity) &&
            !string.Equals(agent.AgentIdentity, agentIdentity, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(projectId) &&
            !string.Equals(agent.ProjectId, projectId, StringComparison.OrdinalIgnoreCase) &&
            !ScopedGatewayDeliveries(agent, projectId, null).Any())
            return false;

        if (!channelId.HasValue)
            return true;

        if (!string.IsNullOrWhiteSpace(agent.AgentIdentity) &&
            scopedChannelsAgentIdentities?.Contains(agent.AgentIdentity) == true)
            return true;

        return ScopedGatewayDeliveries(agent, projectId, channelId).Any();
    }

    private static IEnumerable<GatewayDeliveryDto> ScopedGatewayDeliveries(
        GatewayAgentDto agent, string? projectId, long? channelId) =>
        (agent.CurrentDeliveries ?? [])
        .Concat(agent.RecentDeliveries ?? [])
        .Where(d => GatewayDeliveryMatchesScope(d, projectId, channelId));

    private static bool GatewayDeliveryMatchesScope(GatewayDeliveryDto delivery, string? projectId, long? channelId)
    {
        if (!string.IsNullOrWhiteSpace(projectId) &&
            !string.Equals(delivery.ProjectId, projectId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(delivery.SourceProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            return false;

        return !channelId.HasValue || string.Equals(delivery.ChannelId, channelId.Value.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static int ActiveGatewayDeliveryCount(GatewayAgentDto agent, string? projectId, long? channelId)
    {
        if (string.IsNullOrWhiteSpace(projectId) && !channelId.HasValue && agent.DeliverySummary is not null)
            return agent.DeliverySummary.Counts.Active;

        return ScopedGatewayDeliveries(agent, projectId, channelId).Count(d => !d.Terminal);
    }

    public static GatewayDeliveryCountsDto? ScopedGatewayDeliveryCounts(GatewayAgentDto agent, string? projectId, long? channelId)
    {
        if (string.IsNullOrWhiteSpace(projectId) && !channelId.HasValue && agent.DeliverySummary is not null)
            return agent.DeliverySummary.Counts;

        var deliveries = ScopedGatewayDeliveries(agent, projectId, channelId).ToList();
        if (deliveries.Count == 0)
            return null;

        return new GatewayDeliveryCountsDto(
            Active: deliveries.Count(d => !d.Terminal),
            Completed: deliveries.Count(d => d.Status == "completed"),
            Failed: deliveries.Count(d => d.Status == "failed"),
            Suppressed: deliveries.Count(d => d.Status == "suppressed"),
            Total: deliveries.Count);
    }

    public static string? ScopedGatewayDeliveryState(GatewayAgentDto agent, string? projectId, long? channelId)
    {
        if (string.IsNullOrWhiteSpace(projectId) && !channelId.HasValue)
            return agent.DeliverySummary?.State;

        var deliveries = ScopedGatewayDeliveries(agent, projectId, channelId).ToList();
        if (deliveries.Count == 0)
            return null;
        if (deliveries.Any(d => d.Status == "failed"))
            return "failed";
        if (deliveries.Any(d => d.Flags?.Contains("stuck") == true))
            return "stuck";
        if (deliveries.Any(d => !d.Terminal))
            return deliveries.First(d => !d.Terminal).Status;
        if (deliveries.Any(d => d.Status == "completed"))
            return "completed";
        return deliveries[0].Status;
    }

    private static DeliveryOverviewDto ToDeliveryOverview(GatewayDeliveryDto delivery) => new(
        delivery.RequestId,
        delivery.Status,
        delivery.Status,
        delivery.Terminal,
        delivery.CreatedAt,
        delivery.UpdatedAt,
        delivery.Summary);

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
    // Test-accessible wrappers. These expose internal helpers for unit tests.
    // =========================================================================

    public static HashSet<string> FindLiveDeliveryIdsForTest(IReadOnlyList<ChannelActivityEventDto> activityEvents)
        => FindLiveDeliveryIds(activityEvents);

    public static (string WorkState, string? Severity) DeriveWorkStateFromGatewayForTest(
        IReadOnlyList<GatewayDeliveryDto>? gatewayDeliveries,
        IReadOnlyList<ChannelActivityEventDto> activityEvents)
        => DeriveWorkStateFromGatewayAndActivity(gatewayDeliveries, activityEvents);

    public static bool HasStaleGatewayDebtForTest(
        IReadOnlyList<GatewayDeliveryDto>? gatewayDeliveries,
        IReadOnlyList<ChannelActivityEventDto> activityEvents)
        => HasStaleGatewayDebt(gatewayDeliveries, activityEvents);

    public static int CountStaleGatewayDebtForTest(
        IReadOnlyList<GatewayDeliveryDto>? gatewayDeliveries,
        IReadOnlyList<ChannelActivityEventDto> activityEvents)
        => CountStaleGatewayDebt(gatewayDeliveries, activityEvents);

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
