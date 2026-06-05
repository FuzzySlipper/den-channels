using System.Globalization;
using System.Text.Json;
using DenChannels.Service.AgentWorkLifecycle;
using DenChannels.Service.Channels;
using DenChannels.Service.DirectAgentEvents;

namespace DenChannels.Service;

/// <summary>
/// Channels-owned agent work lifecycle observability routes.
///
/// Architecture boundary:
///   - These endpoints are non-waking observability ONLY.
///   - They write to channel_activity_events with event_type="agent_work_lifecycle".
///   - They NEVER create channel_messages, wake agents, advance read cursors,
///     or imply delivery completion.
///   - den-host is the expected runtime telemetry producer.
///
/// Current-work projection (#1977): composes agent activity from three
/// Channels-owned evidence sources, not just lifecycle events. During
/// producer migration, the projection degrades gracefully from canonical
/// lifecycle events to general activity events and direct-agent records.
/// </summary>
public static class AgentWorkLifecycleRoutes
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static RouteGroupBuilder MapAgentWorkLifecycleRoutes(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/agent-work");

        // ── Write ──────────────────────────────────────────────────────────
        group.MapPost("/lifecycle-events", WriteLifecycleEventAsync);

        // ── Query ──────────────────────────────────────────────────────────
        group.MapGet("/events", QueryLifecycleEventsAsync);

        // ── Projection ─────────────────────────────────────────────────────
        group.MapGet("/current", GetCurrentWorkProjectionAsync);

        return group;
    }

    // ════════════════════════════════════════════════════════════════════════
    // POST /api/agent-work/lifecycle-events
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Record a machine-written agent-work lifecycle event. This is the
    /// canonical non-waking observability write path for den-host/Hermes
    /// harness modules and Core orchestration paths.
    ///
    /// Non-waking guarantee: this endpoint appends to channel_activity_events
    /// with event_type="agent_work_lifecycle". It does NOT create channel
    /// messages, wake agents, advance read cursors, or route through Gateway
    /// delivery. It is observability only.
    /// </summary>
    private static async Task<IResult> WriteLifecycleEventAsync(
        AgentWorkLifecycleWriteRequest request,
        ChannelsRepository repository,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // Validate event type
        if (string.IsNullOrWhiteSpace(request.EventType)
            || !LifecycleEventType.All.Contains(request.EventType))
        {
            return Results.BadRequest(new
            {
                error = "invalid_event_type",
                message = $"Event type must be one of: {string.Join(", ", LifecycleEventType.All)}",
                provided = request.EventType,
            });
        }

        if (string.IsNullOrWhiteSpace(request.AgentIdentity))
        {
            return Results.BadRequest(new { error = "missing_agent_identity", message = "agentIdentity is required." });
        }

        try
        {
            var appendRequest = new AppendChannelActivityEventRequest(
                ProjectId: request.ProjectId,
                AgentIdentity: request.AgentIdentity,
                DeliveryRequestId: request.DeliveryRequestId,
                SessionKey: request.SessionId,
                DisplayBlockId: request.DisplayBlockId,
                ParentSessionKey: request.ParentSessionId,
                ParentAgentIdentity: request.ParentAgentIdentity,
                WorkerRunId: request.WorkerRunId,
                WorkerRole: request.WorkerRole,
                AgentInstanceId: request.AgentInstanceId,
                PoolMemberId: request.PoolMemberId,
                TaskId: request.TaskId,
                ThreadId: request.ThreadId,
                AnchorMessageId: request.AnchorMessageId,
                AssignmentId: request.AssignmentId,
                CheckpointType: null,
                CheckpointHandle: null,
                EventType: "agent_work_lifecycle",
                Status: DetermineLifecycleStatus(request.EventType),
                DeliveryStage: "observability",
                Terminal: IsTerminalEvent(request.EventType),
                Sequence: null,
                Title: request.Title,
                Summary: request.Summary,
                PreviewJson: null,
                MetadataJson: BuildLifecycleMetadata(request),
                DedupeKey: request.DedupeKey,
                FinalChannelMessageId: null);

            var activityEvent = await repository.AppendActivityEventAsync(request.ChannelId, appendRequest, cancellationToken);

            var response = ToLifecycleDto(activityEvent);

            return Results.Created($"/api/agent-work/events/{activityEvent.Id}", response);
        }
        catch (Exception ex)
        {
            var logger = loggerFactory.CreateLogger("AgentWorkLifecycle");
            logger.LogError(ex, "Lifecycle event write failed for agent {AgentIdentity}, eventType {EventType}",
                request.AgentIdentity, request.EventType);
            return Results.Problem(
                detail: "Failed to record lifecycle event.",
                statusCode: 500);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // GET /api/agent-work/events
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Query lifecycle events with optional filters. Returns events
    /// ordered by id descending (newest first).
    /// </summary>
    private static async Task<IResult> QueryLifecycleEventsAsync(
        ChannelsRepository repository,
        long? channelId,
        string? projectId,
        long? taskId,
        string? agentIdentity,
        string? workerRunId,
        string? assignmentId,
        string? sessionId,
        string? eventType,
        long? afterId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1 || limit > 200) limit = 50;

        try
        {
            // Use the existing activity events query with the lifecycle
            // filter. We query channel_activity_events where event_type
            // is 'agent_work_lifecycle' and apply the requested filters.
            // For simplicity, we use the channel-scoped activity event
            // query when channelId is provided, or return empty when not.
            if (channelId is not long cid)
            {
                return Results.BadRequest(new { error = "missing_channel_id", message = "channelId query parameter is required." });
            }

            var events = await repository.ListActivityEventsAsync(
                cid,
                deliveryRequestId: null,
                sessionKey: sessionId,
                displayBlockId: null,
                workerRunId: workerRunId,
                agentInstanceId: null,
                anchorMessageId: null,
                taskId: taskId,
                assignmentId: assignmentId,
                afterId: afterId,
                limit: limit,
                cancellationToken);

            // Filter to lifecycle events only (the existing query returns
            // all activity events; we narrow in-memory for now — a future
            // optimization can push the filter to SQL).
            var lifecycleEvents = events
                .Where(e => string.Equals(e.EventType, "agent_work_lifecycle", StringComparison.OrdinalIgnoreCase))
                .Select(ToLifecycleDto)
                .Where(e => string.IsNullOrWhiteSpace(projectId)
                    || string.Equals(e.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
                .Where(e => string.IsNullOrWhiteSpace(agentIdentity)
                    || string.Equals(e.AgentIdentity, agentIdentity, StringComparison.OrdinalIgnoreCase))
                .Where(e => !taskId.HasValue || e.TaskId == taskId)
                .Where(e => string.IsNullOrWhiteSpace(eventType)
                    || string.Equals(e.EventType, eventType, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return Results.Ok(new
            {
                items = lifecycleEvents,
                count = lifecycleEvents.Count,
                channelId = cid,
                filters = new { agentIdentity, taskId, workerRunId, assignmentId, sessionId, eventType },
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: 500);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // GET /api/agent-work/current
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Bounded current-work projection. Composes agent activity from three
    /// Channels-owned evidence sources, not just lifecycle events.
    ///
    /// During producer migration (#1956 → #1977), the projection degrades
    /// gracefully: canonical lifecycle events are preferred, with fallback
    /// to general activity events and direct-agent wake records.
    ///
    /// Evidence sources:
    ///   1. agent_work_lifecycle events (canonical target)
    ///   2. General channel_activity_events (tool_call_started, etc.)
    ///   3. Direct-agent wake_event messages (channel_messages with
    ///      source_kind=wake_event)
    ///
    /// This is the endpoint that answers: "what is each agent/worker doing
    /// right now, when was it last seen, and what evidence backs that state?"
    /// </summary>
    private static async Task<IResult> GetCurrentWorkProjectionAsync(
        ChannelsRepository repository,
        long? channelId,
        CancellationToken cancellationToken)
    {
        if (channelId is not long cid)
        {
            return Results.BadRequest(new { error = "missing_channel_id", message = "channelId query parameter is required." });
        }

        try
        {
            // ── Source 1: lifecycle events (canonical) ──────────────────
            var allActivityEvents = await repository.ListActivityEventsAsync(
                cid,
                deliveryRequestId: null, sessionKey: null, displayBlockId: null,
                workerRunId: null, agentInstanceId: null, anchorMessageId: null,
                taskId: null, assignmentId: null, afterId: null,
                limit: 200, cancellationToken);

            var lifecycleEvents = allActivityEvents
                .Where(e => string.Equals(e.EventType, "agent_work_lifecycle", StringComparison.OrdinalIgnoreCase))
                .Select(ToLifecycleDto)
                .ToList();

            // ── Source 2: general activity events (non-lifecycle) ───────
            var generalActivityEvents = allActivityEvents
                .Where(e => !string.Equals(e.EventType, "agent_work_lifecycle", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // ── Source 3: direct-agent wake messages ────────────────────
            var channelMessages = await repository.ListMessagesAsync(
                cid, afterId: null, assignmentId: null, limit: 200, cancellationToken);
            var directAgentWakeMessages = channelMessages
                .Where(m => string.Equals(m.SourceKind, "wake_event", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // ── Compose unified evidence map ────────────────────────────
            var evidenceMap = new Dictionary<string, AgentEvidenceBucket>(StringComparer.OrdinalIgnoreCase);

            // Canonical lifecycle events (highest authority)
            foreach (var le in lifecycleEvents)
            {
                var key = le.AgentIdentity;
                if (!evidenceMap.ContainsKey(key))
                    evidenceMap[key] = new AgentEvidenceBucket(key);
                var bucket = evidenceMap[key];
                bucket.LifecycleEvents.Add(le);
                bucket.HasLifecycle = true;
            }

            // General activity events (fill gaps)
            foreach (var ae in generalActivityEvents)
            {
                var key = ae.AgentIdentity;
                if (!evidenceMap.ContainsKey(key))
                    evidenceMap[key] = new AgentEvidenceBucket(key);
                var bucket = evidenceMap[key];
                bucket.ActivityEvents.Add(ae);
                bucket.HasActivity = true;
            }

            // Direct-agent wake messages (fill gaps)
            foreach (var dw in directAgentWakeMessages)
            {
                // Use the target member identity from the SourceId pattern,
                // fall back to SenderIdentity
                var memberIdentity = DirectAgentEventShared.ExtractMemberIdentity(dw.SourceId)
                                     ?? dw.SenderIdentity;
                var key = memberIdentity;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    if (!evidenceMap.ContainsKey(key))
                        evidenceMap[key] = new AgentEvidenceBucket(key);
                    var bucket = evidenceMap[key];
                    bucket.DirectAgentEvents.Add(dw);
                    bucket.HasDirectAgent = true;
                }
            }

            // ── Build projection items ──────────────────────────────────
            var projectionItems = evidenceMap
                .Select(kvp => BuildProjectionItem(kvp.Value, cid))
                .Where(item => item is not null)
                .Select(item => item!)
                .OrderBy(i => i.AgentIdentity)
                .ToList();

            // ── Staleness diagnostics ───────────────────────────────────
            var staleItems = projectionItems
                .Where(i => i.StalenessDiagnostic is not null)
                .ToList();

            // ── Migration note ──────────────────────────────────────────
            var lifecycleCount = lifecycleEvents.Count;
            var nonLifecycleAgents = projectionItems.Count(i =>
                i.EvidenceProvenance.All(p => p != EvidenceProvenance.LifecycleEvent));
            string? migrationNote = lifecycleCount == 0
                ? "No agent_work_lifecycle events present. Projection composed from general activity events and/or direct-agent wake records. The #1956 lifecycle event contract is the canonical target for den-host/Core/Hermes producers."
                : nonLifecycleAgents > 0
                    ? $"{nonLifecycleAgents} agent(s) projected from non-lifecycle evidence only. Canonical agent_work_lifecycle event coverage is partial. Producers should migrate to the #1956 lifecycle contract."
                    : null;

            var response = new CurrentWorkProjectionResponse(
                Items: projectionItems,
                TotalCount: projectionItems.Count,
                GeneratedAt: DateTimeOffset.UtcNow.ToString("O"),
                StalenessSummary: new StalenessSummaryDto(
                    TotalTracked: projectionItems.Count,
                    Stale: staleItems.Count,
                    StaleDiagnostics: staleItems
                        .Select(i => $"{i.AgentIdentity}: {i.StalenessDiagnostic}")
                        .ToList()),
                MigrationNote: migrationNote);

            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: 500);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Intermediate evidence bucket for composing agent activity across sources.
    /// </summary>
    private sealed class AgentEvidenceBucket
    {
        public string AgentIdentity { get; }
        public List<AgentWorkLifecycleEventDto> LifecycleEvents { get; } = new();
        public List<ChannelActivityEventDto> ActivityEvents { get; } = new();
        public List<ChannelMessageDto> DirectAgentEvents { get; } = new();
        public bool HasLifecycle { get; set; }
        public bool HasActivity { get; set; }
        public bool HasDirectAgent { get; set; }

        public AgentEvidenceBucket(string agentIdentity)
        {
            AgentIdentity = agentIdentity;
        }
    }

    /// <summary>
    /// Build a projection item from an evidence bucket. Prefers lifecycle
    /// events when available; falls back to activity events, then direct-agent.
    /// </summary>
    private static CurrentWorkProjectionItem? BuildProjectionItem(AgentEvidenceBucket bucket, long cid)
    {
        var provenance = new List<string>();
        var evidenceLinks = new List<string>();

        // ── Determine primary evidence source ───────────────────────────
        if (bucket.HasLifecycle)
        {
            provenance.Add(EvidenceProvenance.LifecycleEvent);
            var latest = bucket.LifecycleEvents.OrderByDescending(e => e.Id).First();
            evidenceLinks.Add($"/api/agent-work/events?channelId={cid}&agentIdentity={Uri.EscapeDataString(latest.AgentIdentity)}&limit=1");
            evidenceLinks.Add($"/api/channels/{cid}/activity-events?limit=10");

            var currentWorkState = latest.Terminal
                ? CurrentWorkState.TerminalLifecycle
                : CurrentWorkState.LifecycleEventPresent;

            return new CurrentWorkProjectionItem(
                AgentIdentity: latest.AgentIdentity,
                WorkerRunId: latest.WorkerRunId,
                ProjectId: latest.ProjectId,
                TaskId: latest.TaskId,
                AssignmentId: latest.AssignmentId,
                WorkerRole: latest.WorkerRole,
                ProfileIdentity: latest.ProfileIdentity,
                PoolMemberId: latest.PoolMemberId,
                AgentInstanceId: latest.AgentInstanceId,
                State: DetermineProjectedState(latest),
                StateReason: latest.Summary,
                LastActivityAt: latest.UpdatedAt,
                StalenessDeadline: null,
                LastActivityEventId: latest.Id,
                EvidenceLink: $"/api/agent-work/events?channelId={cid}&agentIdentity={Uri.EscapeDataString(latest.AgentIdentity)}&limit=1",
                EvidenceProvenance: provenance,
                EvidenceLinks: evidenceLinks,
                SessionId: latest.SessionId,
                DeliveryRequestId: latest.DeliveryRequestId,
                DirectAgentEventId: latest.DirectAgentEventId,
                HostId: latest.HostId,
                ProcessId: latest.ProcessId,
                CurrentWorkState: currentWorkState,
                StalenessDiagnostic: GetStalenessDiagnostic(latest),
                Flags: BuildFlags(latest));
        }

        // ── Has both activity AND direct-agent, but no lifecycle ───────
        if (bucket.HasActivity && bucket.HasDirectAgent)
        {
            provenance.Add(EvidenceProvenance.ActivityEvent);
            provenance.Add(EvidenceProvenance.DirectAgentEvent);

            var latestActivity = bucket.ActivityEvents.OrderByDescending(e => e.Id).First();
            var latestDirectAgent = bucket.DirectAgentEvents.OrderByDescending(e => e.Id).First();

            evidenceLinks.Add($"/api/channels/{cid}/activity-events?limit=10");
            evidenceLinks.Add($"/api/direct-agent-events?channelId={cid}&limit=10");

            // Use the latest timestamp across both sources
            var latestAt = LatestTimestamp(latestActivity.UpdatedAt, latestDirectAgent.CreatedAt);

            return new CurrentWorkProjectionItem(
                AgentIdentity: bucket.AgentIdentity,
                WorkerRunId: latestActivity.WorkerRunId ?? latestDirectAgent.WorkerRunId,
                ProjectId: latestActivity.ProjectId ?? latestDirectAgent.TargetProjectId ?? latestDirectAgent.SourceProjectId,
                TaskId: latestActivity.TaskId ?? latestDirectAgent.TargetTaskId,
                AssignmentId: latestActivity.AssignmentId ?? latestDirectAgent.AssignmentId,
                WorkerRole: latestActivity.WorkerRole ?? latestDirectAgent.WorkerRole,
                ProfileIdentity: null,
                PoolMemberId: latestActivity.PoolMemberId ?? latestDirectAgent.PoolMemberId,
                AgentInstanceId: latestActivity.AgentInstanceId ?? latestDirectAgent.AgentInstanceId,
                State: "running",
                StateReason: $"Activity seen ({latestActivity.EventType}) + direct-agent wake; no lifecycle event",
                LastActivityAt: latestAt,
                StalenessDeadline: null,
                LastActivityEventId: latestActivity.Id,
                EvidenceLink: $"/api/channels/{cid}/activity-events?limit=10",
                EvidenceProvenance: provenance,
                EvidenceLinks: evidenceLinks,
                SessionId: latestActivity.SessionKey ?? latestDirectAgent.SessionId,
                DeliveryRequestId: latestActivity.DeliveryRequestId ?? latestDirectAgent.DeliveryRequestId,
                DirectAgentEventId: latestDirectAgent.SourceId,
                HostId: null,
                ProcessId: null,
                CurrentWorkState: CurrentWorkState.DeliveredNoLifecycle,
                StalenessDiagnostic: GetTimestampDiagnostic(latestAt, bucket.AgentIdentity),
                Flags: new List<string> { "multi_source", "no_lifecycle" });
        }

        // ── Activity only, no lifecycle ─────────────────────────────────
        if (bucket.HasActivity)
        {
            provenance.Add(EvidenceProvenance.ActivityEvent);
            var latest = bucket.ActivityEvents.OrderByDescending(e => e.Id).First();
            evidenceLinks.Add($"/api/channels/{cid}/activity-events?limit=10");

            return new CurrentWorkProjectionItem(
                AgentIdentity: bucket.AgentIdentity,
                WorkerRunId: latest.WorkerRunId,
                ProjectId: latest.ProjectId,
                TaskId: latest.TaskId,
                AssignmentId: latest.AssignmentId,
                WorkerRole: latest.WorkerRole,
                ProfileIdentity: null,
                PoolMemberId: latest.PoolMemberId,
                AgentInstanceId: latest.AgentInstanceId,
                State: "running",
                StateReason: $"Activity event ({latest.EventType}) without lifecycle event",
                LastActivityAt: latest.UpdatedAt,
                StalenessDeadline: null,
                LastActivityEventId: latest.Id,
                EvidenceLink: $"/api/channels/{cid}/activity-events?limit=10",
                EvidenceProvenance: provenance,
                EvidenceLinks: evidenceLinks,
                SessionId: latest.SessionKey,
                DeliveryRequestId: latest.DeliveryRequestId,
                DirectAgentEventId: null,
                HostId: null,
                ProcessId: null,
                CurrentWorkState: CurrentWorkState.ActivityNoLifecycle,
                StalenessDiagnostic: GetTimestampDiagnostic(latest.UpdatedAt, bucket.AgentIdentity),
                Flags: new List<string> { "no_lifecycle" });
        }

        // ── Direct-agent only ───────────────────────────────────────────
        if (bucket.HasDirectAgent)
        {
            provenance.Add(EvidenceProvenance.DirectAgentEvent);
            var latest = bucket.DirectAgentEvents.OrderByDescending(e => e.Id).First();
            evidenceLinks.Add($"/api/direct-agent-events/{latest.Id}");
            evidenceLinks.Add($"/api/direct-agent-events?channelId={cid}&limit=10");

            return new CurrentWorkProjectionItem(
                AgentIdentity: bucket.AgentIdentity,
                WorkerRunId: latest.WorkerRunId,
                ProjectId: latest.TargetProjectId ?? latest.SourceProjectId,
                TaskId: latest.TargetTaskId,
                AssignmentId: latest.AssignmentId,
                WorkerRole: latest.WorkerRole,
                ProfileIdentity: latest.ProfileIdentity,
                PoolMemberId: latest.PoolMemberId,
                AgentInstanceId: latest.AgentInstanceId,
                State: "running",
                StateReason: "Direct-agent wake recorded; pending delivery or lifecycle event",
                LastActivityAt: latest.CreatedAt,
                StalenessDeadline: null,
                LastActivityEventId: null,
                EvidenceLink: $"/api/direct-agent-events/{latest.Id}",
                EvidenceProvenance: provenance,
                EvidenceLinks: evidenceLinks,
                SessionId: latest.SessionId,
                DeliveryRequestId: latest.DeliveryRequestId,
                DirectAgentEventId: latest.SourceId,
                HostId: null,
                ProcessId: null,
                CurrentWorkState: CurrentWorkState.RecordedOnly,
                StalenessDiagnostic: GetTimestampDiagnostic(latest.CreatedAt, bucket.AgentIdentity),
                Flags: new List<string> { "direct_agent_only", "no_lifecycle" });
        }

        return null;
    }

    private static string? LatestTimestamp(string? a, string? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return string.Compare(a, b, StringComparison.Ordinal) >= 0 ? a : b;
    }

    private static string? GetTimestampDiagnostic(string? timestamp, string agentIdentity)
    {
        if (string.IsNullOrWhiteSpace(timestamp)) return "missing_timestamp";

        var parsed = DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var dt) ? dt : (DateTimeOffset?)null;

        if (parsed is null) return "missing_timestamp";

        var age = DateTimeOffset.UtcNow - parsed.Value;

        if (age.TotalMinutes > 30) return $"stale (last seen {age.TotalMinutes:F0}m ago)";
        if (age.TotalMinutes > 10) return $"possibly_stale (last seen {age.TotalMinutes:F0}m ago)";
        return null;
    }

    private static AgentWorkLifecycleEventDto ToLifecycleDto(ChannelActivityEventDto e)
    {
        var metadata = ParseMetadata(e.MetadataJson);
        var lifecycleType = MetadataString(metadata, "event_type") ?? e.EventType;

        return new AgentWorkLifecycleEventDto(
            Id: e.Id,
            ChannelId: e.ChannelId,
            AgentIdentity: e.AgentIdentity,
            EventType: lifecycleType,
            Status: e.Status,
            Terminal: e.Terminal,
            CreatedAt: e.CreatedAt,
            UpdatedAt: e.UpdatedAt,
            ProjectId: e.ProjectId,
            TaskId: e.TaskId,
            ThreadId: e.ThreadId,
            AnchorMessageId: e.AnchorMessageId,
            ProfileIdentity: MetadataString(metadata, "profile_identity"),
            AgentInstanceId: e.AgentInstanceId,
            WorkerIdentity: MetadataString(metadata, "worker_identity"),
            WorkerRole: e.WorkerRole,
            PoolMemberId: e.PoolMemberId,
            AssignmentId: e.AssignmentId,
            WorkerRunId: e.WorkerRunId,
            LeaseId: MetadataString(metadata, "lease_id"),
            SessionId: e.SessionKey,
            ParentSessionId: e.ParentSessionKey ?? MetadataString(metadata, "parent_session_id"),
            DeliveryRequestId: e.DeliveryRequestId,
            SourceMessageId: MetadataString(metadata, "source_message_id"),
            DirectAgentEventId: MetadataString(metadata, "direct_agent_event_id"),
            HostId: MetadataString(metadata, "host_id"),
            ProcessId: MetadataInt(metadata, "process_id"),
            Workdir: MetadataString(metadata, "workdir"),
            Branch: MetadataString(metadata, "branch"),
            Commit: MetadataString(metadata, "commit"),
            ReviewRoundId: MetadataLong(metadata, "review_round_id"),
            DisplayBlockId: e.DisplayBlockId,
            ParentAgentIdentity: e.ParentAgentIdentity,
            Title: e.Title,
            Summary: MetadataString(metadata, "state_reason") ?? e.Summary);
    }

    private static Dictionary<string, JsonElement> ParseMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? MetadataString(Dictionary<string, JsonElement> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static long? MetadataLong(Dictionary<string, JsonElement> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)) return n;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static int? MetadataInt(Dictionary<string, JsonElement> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static string DetermineLifecycleStatus(string eventType) => eventType switch
    {
        LifecycleEventType.RequestRecorded => "started",
        LifecycleEventType.DeliveryAttempted => "interim",
        LifecycleEventType.RuntimeReceived => "started",
        LifecycleEventType.RequestClaimed => "started",
        LifecycleEventType.AgentTurnStarted => "started",
        LifecycleEventType.TaskSelected => "interim",
        LifecycleEventType.AssignmentCreated => "started",
        LifecycleEventType.WorkerSpawnRequested => "started",
        LifecycleEventType.WorkerProcessStarted => "started",
        LifecycleEventType.Heartbeat => "interim",
        LifecycleEventType.CheckpointSeen => "interim",
        LifecycleEventType.Blocked => "blocked",
        LifecycleEventType.Completed => "completed",
        LifecycleEventType.Failed => "failed",
        LifecycleEventType.TimedOut => "failed",
        LifecycleEventType.CleanupStarted => "interim",
        LifecycleEventType.CleanupCompleted => "completed",
        LifecycleEventType.CapacityReleased => "completed",
        _ => "interim",
    };

    private static bool IsTerminalEvent(string eventType) => eventType switch
    {
        LifecycleEventType.Completed => true,
        LifecycleEventType.Failed => true,
        LifecycleEventType.TimedOut => true,
        LifecycleEventType.Blocked => true,
        LifecycleEventType.CleanupCompleted => true,
        LifecycleEventType.CapacityReleased => true,
        _ => false,
    };

    private static string? BuildLifecycleMetadata(AgentWorkLifecycleWriteRequest r)
    {
        // Pack all correlation fields into metadata_json so they survive
        // the activity event append (which only stores a subset as
        // first-class columns). This is the durable record for
        // host/runtime correlation fields not in the core activity schema.
        var extensions = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        void AddIf(string key, object? value)
        {
            if (value is not null) extensions[key] = value;
        }

        AddIf("event_type", r.EventType);
        AddIf("profile_identity", r.ProfileIdentity);
        AddIf("worker_identity", r.WorkerIdentity);
        AddIf("lease_id", r.LeaseId);
        AddIf("parent_session_id", r.ParentSessionId);
        AddIf("source_message_id", r.SourceMessageId);
        AddIf("direct_agent_event_id", r.DirectAgentEventId);
        AddIf("host_id", r.HostId);
        AddIf("process_id", r.ProcessId);
        AddIf("workdir", r.Workdir);
        AddIf("branch", r.Branch);
        AddIf("commit", r.Commit);
        AddIf("review_round_id", r.ReviewRoundId);
        AddIf("parent_agent_identity", r.ParentAgentIdentity);
        AddIf("last_activity_at", r.LastActivityAt);
        AddIf("staleness_deadline", r.StalenessDeadline);
        AddIf("state_reason", r.StateReason);

        return extensions.Count > 0
            ? JsonSerializer.Serialize(extensions, JsonOpts)
            : null;
    }

    private static string DetermineProjectedState(AgentWorkLifecycleEventDto e) => e.Status switch
    {
        "started" when !e.Terminal => "running",
        "interim" when !e.Terminal => "running",
        "blocked" => "blocked",
        "completed" => "completed",
        "failed" => "failed",
        _ when e.Terminal => "completed",
        _ => "unknown",
    };

    private static string? GetStalenessDiagnostic(AgentWorkLifecycleEventDto e)
    {
        if (e.Terminal) return null; // Terminal events are not stale

        var updatedAt = DateTimeOffset.TryParse(e.UpdatedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var dt) ? dt : (DateTimeOffset?)null;

        if (updatedAt is null) return "missing_timestamp";

        var age = DateTimeOffset.UtcNow - updatedAt.Value;

        if (age.TotalMinutes > 30) return $"stale (last seen {age.TotalMinutes:F0}m ago)";
        if (age.TotalMinutes > 10) return $"possibly_stale (last seen {age.TotalMinutes:F0}m ago)";
        return null;
    }

    private static IReadOnlyList<string> BuildFlags(AgentWorkLifecycleEventDto e)
    {
        var flags = new List<string>();
        if (e.Terminal) flags.Add("terminal");
        if (string.IsNullOrWhiteSpace(e.WorkerRunId)) flags.Add("no_worker_run");
        if (string.IsNullOrWhiteSpace(e.AssignmentId)) flags.Add("no_assignment");
        return flags;
    }
}
