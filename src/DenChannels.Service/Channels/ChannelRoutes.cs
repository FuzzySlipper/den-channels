using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace DenChannels.Service.Channels;

public static class ChannelRoutes
{
    public static RouteGroupBuilder MapChannelRoutes(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        // -----------------------------------------------------------------------
        // GET /api/assignments/{assignmentId}/trace
        // Alias for Gateway assignment trace. Den Web #1729 calls this path;
        // the canonical implementation lives in GatewayRoutes under /api/gateway.
        // -----------------------------------------------------------------------
        api.MapGet("/assignments/{assignmentId}/trace", async (
            ChannelsRepository repository,
            AgentsOverview.IWorkerPoolStateClient workerPoolClient,
            string assignmentId,
            string? projectId,
            long? channelId,
            CancellationToken cancellationToken) =>
        {
            // Delegate to the same handler
            return await Gateway.GatewayRoutes.HandleAssignmentTraceAsync(
                repository, workerPoolClient, assignmentId, projectId, channelId, cancellationToken);
        });

        api.MapGet("/channels", async (ChannelsRepository repository, string? projectId, string? kind, int? limit,
            CancellationToken cancellationToken) => Results.Ok(await repository.ListChannelsAsync(
                projectId, kind, limit ?? 100, cancellationToken)));

        // -----------------------------------------------------------------------
        // GET /api/channels/search
        // Cross-channel FTS5 search. Read-only; no wake/delivery/claim side effects.
        // Restricted to detective/sysadmin profiles via X-Profile-Identity header.
        // Query params (both camelCase and snake_case aliases supported):
        //   q, channelId/channel_id, senderIdentity/sender_identity,
        //   projectId/project_id, nonProjectOnly/non_project_only,
        //   messageKind/message_kind, createdAfter/created_after,
        //   createdBefore/created_before, orderBy/order_by,
        //   offset, limit
        // -----------------------------------------------------------------------
        api.MapGet("/channels/search", async (
            ChannelsRepository repository,
            HttpContext httpContext,
            string? q,
            long? channelId = null,
            [FromQuery(Name = "channel_id")] long? channelIdSnake = null,
            string? senderIdentity = null,
            [FromQuery(Name = "sender_identity")] string? senderIdentitySnake = null,
            string? projectId = null,
            [FromQuery(Name = "project_id")] string? projectIdSnake = null,
            bool nonProjectOnly = false,
            [FromQuery(Name = "non_project_only")] bool nonProjectOnlySnake = false,
            string? messageKind = null,
            [FromQuery(Name = "message_kind")] string? messageKindSnake = null,
            string? createdAfter = null,
            [FromQuery(Name = "created_after")] string? createdAfterSnake = null,
            string? createdBefore = null,
            [FromQuery(Name = "created_before")] string? createdBeforeSnake = null,
            string? orderBy = null,
            [FromQuery(Name = "order_by")] string? orderBySnake = null,
            int offset = 0,
            int limit = 20,
            CancellationToken cancellationToken = default) =>
        {
            // Merge camelCase + snake_case aliases (snake_case wins if both set)
            channelId ??= channelIdSnake;
            senderIdentity ??= senderIdentitySnake;
            projectId ??= projectIdSnake;
            nonProjectOnly = nonProjectOnly || nonProjectOnlySnake;
            messageKind ??= messageKindSnake;
            createdAfter ??= createdAfterSnake;
            createdBefore ??= createdBeforeSnake;
            orderBy ??= orderBySnake;
            // ── Profile authorization ──────────────────────────────────────
            // Only detective and sysadmin profiles may search across channels.
            // The caller sends X-Profile-Identity header, which the den-channels
            // MCP facade sets from tool_profile during MCP tool invocation.
            var profileIdentity = httpContext.Request.Headers["X-Profile-Identity"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(profileIdentity))
            {
                return Results.Json(new ProblemDetailsDto(
                    "missing_profile_identity",
                    401,
                    "X-Profile-Identity header required. Only detective and sysadmin profiles may search channels."),
                    statusCode: 401);
            }
            if (!string.Equals(profileIdentity, "detective", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(profileIdentity, "sysadmin", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new ProblemDetailsDto(
                    "profile_not_authorized",
                    403,
                    $"Profile '{profileIdentity}' is not authorized to search channels. Only detective and sysadmin profiles have this capability."),
                    statusCode: 403);
            }

            // ── Time bound validation ──────────────────────────────────────
            DateTime? parsedCreatedAfter = null;
            DateTime? parsedCreatedBefore = null;

            if (!string.IsNullOrWhiteSpace(createdAfter))
            {
                if (!DateTime.TryParse(createdAfter.Trim(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var after))
                {
                    return Results.BadRequest(new ProblemDetailsDto(
                        "invalid_created_after",
                        400,
                        $"created_after '{createdAfter}' is not a valid ISO 8601 timestamp."));
                }
                parsedCreatedAfter = after;
            }

            if (!string.IsNullOrWhiteSpace(createdBefore))
            {
                if (!DateTime.TryParse(createdBefore.Trim(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var before))
                {
                    return Results.BadRequest(new ProblemDetailsDto(
                        "invalid_created_before",
                        400,
                        $"created_before '{createdBefore}' is not a valid ISO 8601 timestamp."));
                }
                parsedCreatedBefore = before;
            }

            if (parsedCreatedAfter.HasValue && parsedCreatedBefore.HasValue
                && parsedCreatedAfter.Value > parsedCreatedBefore.Value)
            {
                return Results.BadRequest(new ProblemDetailsDto(
                    "inverted_time_bounds",
                    400,
                    "created_after must not be later than created_before."));
            }

            // ── Standard search criteria guard ────────────────────────────
            if (string.IsNullOrWhiteSpace(q) && channelId is null
                && string.IsNullOrWhiteSpace(senderIdentity)
                && string.IsNullOrWhiteSpace(projectId) && !nonProjectOnly
                && string.IsNullOrWhiteSpace(messageKind)
                && !parsedCreatedAfter.HasValue
                && !parsedCreatedBefore.HasValue)
            {
                return Results.BadRequest(new ProblemDetailsDto(
                    "missing_search_criteria",
                    400,
                    "Provide at least one search criterion (q, channel_id, sender_identity, project_id, non_project_only, message_kind, or time range)."));
            }

            var result = await repository.SearchMessagesAsync(
                q, channelId, senderIdentity, projectId, nonProjectOnly,
                messageKind, parsedCreatedAfter, parsedCreatedBefore, orderBy,
                offset, limit, cancellationToken);
            return Results.Ok(result);
        });

        api.MapPost("/channels", async (ChannelsRepository repository, CreateChannelRequest request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var channel = await repository.CreateChannelAsync(request, cancellationToken);
                return Results.Created($"/api/channels/{channel.Id}", channel);
            }
            catch (SqliteException ex) when (IsConstraintFailure(ex))
            {
                return Results.Conflict(new ProblemDetailsDto("channel_constraint_failed", ex.SqliteErrorCode, ex.Message));
            }
        });

        api.MapGet("/channels/{channelId:long}", async (ChannelsRepository repository, long channelId,
            CancellationToken cancellationToken) =>
        {
            var channel = await repository.GetChannelAsync(channelId, cancellationToken);
            return channel is null ? Results.NotFound() : Results.Ok(channel);
        });

        api.MapPut("/projects/{projectId}/default-channel", async (ChannelsRepository repository, string projectId,
            EnsureProjectDefaultChannelRequest request, CancellationToken cancellationToken) =>
        {
            var channel = await repository.EnsureProjectDefaultChannelAsync(projectId, request, cancellationToken);
            return Results.Ok(channel);
        });

        api.MapPut("/agent-commons", async (ChannelsRepository repository, CancellationToken cancellationToken) =>
        {
            var channel = await repository.EnsureAgentCommonsChannelAsync(cancellationToken);
            return Results.Ok(channel);
        });

        api.MapPut("/agent-commons/memberships/{agentIdentity}", async (ChannelsRepository repository, string agentIdentity,
            CancellationToken cancellationToken) =>
        {
            var membership = await repository.EnsureAgentCommonsMembershipAsync(agentIdentity, null, cancellationToken);
            return Results.Ok(membership);
        });

        api.MapPost("/agent-commons/brake", async (ChannelsRepository repository, AgentCommonsBrakeRequest request,
            CancellationToken cancellationToken) =>
        {
            var membershipStatus = string.IsNullOrWhiteSpace(request.MembershipStatus) ? "muted" : request.MembershipStatus.Trim();
            var wakePolicy = string.IsNullOrWhiteSpace(request.WakePolicy) ? "never" : request.WakePolicy.Trim();
            if (membershipStatus is not ("active" or "muted" or "left" or "banned"))
            {
                return Results.BadRequest(new { code = "invalid_membership_status", message = "membershipStatus must be active, muted, left, or banned." });
            }

            if (wakePolicy is not ("never" or "mentions_only" or "direct_questions_only" or "substantive_digest" or "all_human_messages" or "all_messages_except_self"))
            {
                return Results.BadRequest(new { code = "invalid_wake_policy", message = "wakePolicy is not valid." });
            }

            var result = await repository.ApplyAgentCommonsBrakeAsync(membershipStatus, wakePolicy, cancellationToken);
            return Results.Ok(result);
        });

        api.MapPost("/channels/{channelId:long}/messages", async (ChannelsRepository repository, long channelId,
            PostChannelMessageRequest request, CancellationToken cancellationToken) =>
        {
            // Dedupe keys are caller-provided idempotency keys, not validation failures.
            // Gateway-facing system messages already return the existing row for duplicate
            // dedupe keys; direct channel-message posts need the same behavior so retrying
            // Hermes/Den Channels delivery cannot turn an already-persisted reply into a
            // noisy 409 failure and fallback send loop.
            if (!string.IsNullOrWhiteSpace(request.DedupeKey))
            {
                var existing = await repository.GetMessageByDedupeKeyAsync(channelId, request.DedupeKey, cancellationToken);
                if (existing is not null)
                    return Results.Ok(existing);
            }

            try
            {
                var message = await repository.PostMessageAsync(channelId, request, cancellationToken);
                return Results.Created($"/api/channels/{channelId}/messages/{message.Id}", message);
            }
            catch (SqliteException ex) when (IsConstraintFailure(ex) && !string.IsNullOrWhiteSpace(request.DedupeKey))
            {
                var existing = await repository.GetMessageByDedupeKeyAsync(channelId, request.DedupeKey, cancellationToken);
                if (existing is not null)
                    return Results.Ok(existing);
                return Results.Conflict(new ProblemDetailsDto("message_constraint_failed", ex.SqliteErrorCode, ex.Message));
            }
            catch (SqliteException ex) when (IsConstraintFailure(ex))
            {
                return Results.Conflict(new ProblemDetailsDto("message_constraint_failed", ex.SqliteErrorCode, ex.Message));
            }
        });

        api.MapGet("/channels/{channelId:long}/messages", async (ChannelsRepository repository, long channelId,
            long? afterId, string? assignmentId, int? limit, CancellationToken cancellationToken) => Results.Ok(
                await repository.ListMessagesAsync(channelId, afterId, assignmentId, limit ?? 100, cancellationToken)));

        api.MapGet("/channels/{channelId:long}/reactions", async (ChannelsRepository repository, long channelId,
            CancellationToken cancellationToken) => Results.Ok(
                await repository.ListReactionSummariesAsync(channelId, cancellationToken)));

        api.MapPost("/channels/{channelId:long}/activity-events", async (ChannelsRepository repository, long channelId,
            AppendChannelActivityEventRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var activityEvent = await repository.AppendActivityEventAsync(channelId, request, cancellationToken);
                return Results.Created($"/api/channels/{channelId}/activity-events/{activityEvent.Id}", activityEvent);
            }
            catch (SqliteException ex) when (IsConstraintFailure(ex))
            {
                return Results.Conflict(new ProblemDetailsDto("activity_event_constraint_failed", ex.SqliteErrorCode, ex.Message));
            }
        });

        api.MapGet("/channels/{channelId:long}/activity-events", async (ChannelsRepository repository, long channelId,
            string? deliveryRequestId, string? sessionKey, string? displayBlockId, string? workerRunId,
            string? agentInstanceId, long? anchorMessageId, long? taskId, string? assignmentId, long? afterId, int? limit, CancellationToken cancellationToken) => Results.Ok(
                await repository.ListActivityEventsAsync(channelId, deliveryRequestId, sessionKey, displayBlockId,
                    workerRunId, agentInstanceId, anchorMessageId, taskId, assignmentId, afterId, limit ?? 100, cancellationToken)));

        api.MapPatch("/channel-activity-events/{activityEventId:long}", async (ChannelsRepository repository, long activityEventId,
            UpdateChannelActivityEventRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var activityEvent = await repository.UpdateActivityEventAsync(activityEventId, request, cancellationToken);
                return activityEvent is null ? Results.NotFound() : Results.Ok(activityEvent);
            }
            catch (SqliteException ex) when (IsConstraintFailure(ex))
            {
                return Results.Conflict(new ProblemDetailsDto("activity_event_constraint_failed", ex.SqliteErrorCode, ex.Message));
            }
        });

        // -----------------------------------------------------------------------
        // Channels-owned non-waking activity/breadcrumb route.
        // Canonical Channels ownership is /api/channels/{channelId}/activity-events;
        // this route accepts the old body/query channelId shape while preserving
        // non-waking soft-failure diagnostics for breadcrumb writes.
        // The Gateway-prefixed alias (/api/gateway/channel-activity-events) was
        // retired in task #2022 and returns 410 Gone.
        // -----------------------------------------------------------------------
        api.MapPost("/channel-activity-events", async Task<IResult> (ChannelActivityEventRoutingService activityRouter,
            string? channelId, ChannelActivityRouteRequest request, CancellationToken cancellationToken) =>
        {
            var result = await activityRouter.RouteAsync(request, channelId, cancellationToken);
            return ToActivityRouteHttpResult(result);
        });

        api.MapGet("/channel-activity-events/status", (ChannelActivityEventRoutingService activityRouter) =>
            Results.Ok(activityRouter.GetStatus()));

        // -----------------------------------------------------------------------
        // Read cursor endpoints (task #1769 shared-profile instance support)
        // -----------------------------------------------------------------------

        api.MapGet("/channels/{channelId:long}/read-cursors", async (ChannelsRepository repository, long channelId,
            string? readerType, string? readerIdentity, string? instanceId, CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListReadCursorsAsync(channelId, readerType, readerIdentity, instanceId, cancellationToken)));

        api.MapPut("/channels/{channelId:long}/read-cursors", async (ChannelsRepository repository, long channelId,
            UpsertChannelReadCursorRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var cursor = await repository.UpsertReadCursorAsync(channelId, request, cancellationToken);
                return Results.Ok(cursor);
            }
            catch (SqliteException ex) when (IsConstraintFailure(ex))
            {
                return Results.Conflict(new ProblemDetailsDto("read_cursor_constraint_failed", ex.SqliteErrorCode, ex.Message));
            }
        });

        // -----------------------------------------------------------------------
        // GET /api/assignments/{assignmentId}/transcript
        // Assignment-scoped readback: visible messages + non-waking activity/checkpoint events.
        // Den Web #1729 consumer: given assignmentId, return bounded visible messages
        // plus non-waking activity/checkpoint events with channel/message/delivery handles.
        // -----------------------------------------------------------------------
        api.MapGet("/assignments/{assignmentId}/transcript", async (ChannelsRepository repository,
            string assignmentId, long? channelId, string? projectId, int? messageLimit, int? activityLimit,
            CancellationToken cancellationToken) =>
        {
            if (channelId is null && string.IsNullOrWhiteSpace(projectId))
                return Results.BadRequest(new { code = "missing_parameter", message = "Provide channelId or projectId." });

            long resolvedChannelId;
            if (channelId is not null)
            {
                var channel = await repository.GetChannelAsync(channelId.Value, cancellationToken);
                if (channel is null)
                    return Results.NotFound(new { code = "channel_not_found", message = $"Channel {channelId} not found." });
                resolvedChannelId = channel.Id;
            }
            else
            {
                var channels = await repository.ListChannelsAsync(projectId!, "project_default", 1, cancellationToken);
                if (channels.Count == 0)
                    return Results.NotFound(new { code = "channel_not_found", message = $"No default channel found for project '{projectId}'." });
                resolvedChannelId = channels[0].Id;
            }

            var messageCap = Math.Clamp(messageLimit ?? 100, 1, 500);
            var activityCap = Math.Clamp(activityLimit ?? 100, 1, 500);

            var messages = await repository.ListMessagesAsync(resolvedChannelId, null, assignmentId, messageCap, cancellationToken);
            var activityEvents = await repository.ListActivityEventsAsync(resolvedChannelId,
                assignmentId: assignmentId, limit: activityCap, cancellationToken: cancellationToken);

            return Results.Ok(new AssignmentTranscriptResponse(
                AssignmentId: assignmentId,
                Messages: messages,
                ActivityEvents: activityEvents));
        });

        api.MapPut("/channels/{channelId:long}/memberships", async (ChannelsRepository repository, long channelId,
            UpsertChannelMembershipRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var membership = await repository.UpsertMembershipAsync(channelId, request, cancellationToken);
                return Results.Ok(membership);
            }
            catch (SqliteException ex) when (IsConstraintFailure(ex))
            {
                return Results.Conflict(new ProblemDetailsDto("membership_constraint_failed", ex.SqliteErrorCode, ex.Message));
            }
        });

        api.MapGet("/channel-memberships", async Task<IResult> (ChannelsRepository repository,
            string? memberIdentity, string? membershipPurpose, string? projectId, long? channelId, bool? includeLeft,
            bool? includeOrdinaryMemberships,
            int? limit, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(memberIdentity))
                return Results.BadRequest(new { code = "missing_parameter", message = "Provide memberIdentity." });

            var rows = await repository.ListMembershipsByMemberIdentityAsync(
                memberIdentity,
                membershipPurpose,
                projectId,
                channelId,
                includeLeft ?? false,
                includeOrdinaryMemberships ?? false,
                limit ?? 100,
                cancellationToken);

            return Results.Ok(new ChannelMembershipDiscoveryResponse(
                memberIdentity.Trim(),
                rows.Select(ToChannelMembershipDiscoveryDto).ToList()));
        });

        // -----------------------------------------------------------------------
        // Worker-pool lobby endpoints (task #1771)
        // -----------------------------------------------------------------------

        api.MapPut("/worker-pool/lobby", async (ChannelsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var channel = await repository.EnsureWorkerPoolLobbyChannelAsync(cancellationToken);
            return Results.Ok(channel);
        });

        api.MapPut("/worker-pool/lobby/presence", async (ChannelsRepository repository,
            UpsertWorkerPoolLobbyPresenceRequest request, CancellationToken cancellationToken) =>
        {
            var lobby = await repository.EnsureWorkerPoolLobbyChannelAsync(cancellationToken);
            var presence = await repository.UpsertWorkerPoolLobbyPresenceAsync(lobby.Id, request, cancellationToken);
            return Results.Ok(presence);
        });

        api.MapPost("/worker-pool/lobby/presence/{memberIdentity}/acknowledge-release", async (
            ChannelsRepository repository, string memberIdentity,
            string? agentInstanceId, string? poolMemberId,
            CancellationToken cancellationToken) =>
        {
            var lobby = await repository.EnsureWorkerPoolLobbyChannelAsync(cancellationToken);
            var presence = await repository.AcknowledgeWorkerPoolReleaseAsync(
                lobby.Id, memberIdentity, agentInstanceId, poolMemberId, cancellationToken);
            return presence is null
                ? Results.NotFound(new { code = "no_release_pending", message = $"No released presence found for '{memberIdentity}'." })
                : Results.Ok(presence);
        });

        api.MapGet("/worker-pool/lobby/presence", async (ChannelsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var lobby = await repository.EnsureWorkerPoolLobbyChannelAsync(cancellationToken);
            var members = await repository.ListWorkerPoolLobbyPresenceAsync(lobby.Id, cancellationToken);

            // Build overview response grouped by role/profile for available (idle) workers
            var idleMembers = members.Where(m => string.Equals(m.Status, "idle", StringComparison.OrdinalIgnoreCase)).ToList();
            var byRole = idleMembers
                .GroupBy(m => (m.Role, m.Profile))
                .Select(g => new WorkerPoolPresenceByRoleGroup(
                    g.Key.Role,
                    g.Key.Profile,
                    g.Count(),
                    g.ToList()))
                .OrderByDescending(g => g.Count)
                .ToList();

            return Results.Ok(new WorkerPoolLobbyOverviewResponse(
                LobbySlug: lobby.Slug,
                LobbyDisplayName: lobby.DisplayName,
                LobbyChannelId: lobby.Id,
                TotalMembers: members.Count,
                AvailableCount: idleMembers.Count,
                ByRole: byRole,
                Members: members.ToList()));
        });

        api.MapPost("/channel-messages/{messageId:long}/reactions", async (ChannelsRepository repository, long messageId,
            AddChannelReactionRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var reaction = await repository.AddReactionAsync(messageId, request, cancellationToken);
                return Results.Created($"/api/channel-messages/{messageId}/reactions/{reaction.Id}", reaction);
            }
            catch (SqliteException ex) when (IsConstraintFailure(ex))
            {
                return Results.Conflict(new ProblemDetailsDto("reaction_constraint_failed", ex.SqliteErrorCode, ex.Message));
            }
        });

        // ── Shared-profile pool child-run routing (#1806) ─────────────────

        /// <summary>
        /// List active child-run presences filtered by agent_instance_id.
        /// Returns per-run identities with routing handles for supervisor dispatch.
        /// </summary>
        api.MapGet("/worker-pool/lobby/presence/by-instance", async (
            ChannelsRepository repository,
            string? agentInstanceId,
            CancellationToken cancellationToken) =>
        {
            var lobby = await repository.EnsureWorkerPoolLobbyChannelAsync(cancellationToken);
            var allPresences = await repository.ListWorkerPoolLobbyPresenceAsync(lobby.Id, cancellationToken);

            var filtered = string.IsNullOrWhiteSpace(agentInstanceId)
                ? allPresences
                : allPresences.Where(p =>
                    string.Equals(p.AgentInstanceId, agentInstanceId, StringComparison.OrdinalIgnoreCase)).ToList();

            return Results.Ok(new { presences = filtered, count = filtered.Count });
        });

        /// <summary>
        /// Release a child-run lobby presence. Transitions status to 'released'.
        /// Channels-only — does not claim to release Core capacity or Gateway delivery.
        /// </summary>
        api.MapPost("/worker-pool/lobby/presence/release-child-run", async (
            ChannelsRepository repository,
            string memberIdentity,
            string? agentInstanceId,
            string? poolMemberId,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(memberIdentity))
                return Results.BadRequest(new { error = "memberIdentity is required" });

            var lobby = await repository.EnsureWorkerPoolLobbyChannelAsync(cancellationToken);
            var presence = await repository.ReleaseChildRunPresenceAsync(
                lobby.Id, memberIdentity, agentInstanceId, poolMemberId, cancellationToken);

            return presence is null
                ? Results.NotFound(new { code = "no_child_run_found", message = $"No active child-run presence found for '{memberIdentity}'." })
                : Results.Ok(presence);
        });

        /// <summary>
        /// Resolve child-run identities for a given agent identity.
        /// Returns active child runs with routing handles for supervisor dispatch.
        /// </summary>
        api.MapGet("/agents/{agentIdentity}/child-runs", async (
            ChannelsRepository repository,
            string agentIdentity,
            CancellationToken cancellationToken) =>
        {
            var lobby = await repository.EnsureWorkerPoolLobbyChannelAsync(cancellationToken);
            var allPresences = await repository.ListWorkerPoolLobbyPresenceAsync(lobby.Id, cancellationToken);

            var childRuns = allPresences
                .Where(p => string.Equals(p.MemberIdentity, agentIdentity, StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(p.Status, "released", StringComparison.OrdinalIgnoreCase))
                .Select(p => new
                {
                    p.MemberIdentity,
                    p.Role,
                    p.Profile,
                    p.AgentInstanceId,
                    p.PoolMemberId,
                    p.Status,
                    p.LastActivityAt,
                    supervisorDeliveryTarget = p.MemberIdentity,
                    childIdentityMetadata = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["agentInstanceId"] = p.AgentInstanceId,
                        ["poolMemberId"] = p.PoolMemberId,
                        ["profileIdentity"] = p.Profile,
                    }
                })
                .ToList();

            return Results.Ok(new { childRuns, count = childRuns.Count });
        });

        /// <summary>
        /// Ensure worker-pool control channel membership for an agent.
        /// Idle workers join the #worker-pool control channel with purpose 'worker_pool_control'.
        /// Task #1880.
        /// </summary>
        api.MapPut("/worker-pool/control/membership", async (
            ChannelsRepository repository,
            string agentIdentity,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(agentIdentity))
                return Results.BadRequest(new { error = "agentIdentity is required" });

            try
            {
                var membership = await repository.EnsureWorkerPoolControlMembershipAsync(agentIdentity, cancellationToken);
                return Results.Ok(membership);
            }
            catch (SqliteException ex) when (IsConstraintFailure(ex))
            {
                return Results.Conflict(new ProblemDetailsDto("membership_constraint_failed", ex.SqliteErrorCode, ex.Message));
            }
        });

        /// <summary>
        /// Release a worker's target-work membership in a project channel.
        /// Sets membership_status to 'left' when a worker is released from an assignment.
        /// Task #1880.
        /// </summary>
        api.MapPost("/channels/{channelId:long}/memberships/{agentIdentity}/release-target-work", async (
            ChannelsRepository repository,
            long channelId,
            string agentIdentity,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var membership = await repository.ReleaseTargetWorkMembershipAsync(channelId, agentIdentity, cancellationToken);
                return membership is null
                    ? Results.NotFound(new { code = "no_target_work_membership", message = $"No active target-work membership found for '{agentIdentity}' in channel {channelId}." })
                    : Results.Ok(membership);
            }
            catch (SqliteException ex) when (IsConstraintFailure(ex))
            {
                return Results.Conflict(new ProblemDetailsDto("membership_constraint_failed", ex.SqliteErrorCode, ex.Message));
            }
        });

        // -----------------------------------------------------------------------
        // Channel-project link endpoints (task #1874)
        // -----------------------------------------------------------------------

        api.MapGet("/channels/{channelId:long}/linked-projects", async (
            ChannelsRepository repository, long channelId, CancellationToken cancellationToken) =>
        {
            var channel = await repository.GetChannelAsync(channelId, cancellationToken);
            if (channel is null)
                return Results.NotFound();
            var links = await repository.GetChannelProjectLinksAsync(channelId, cancellationToken);
            return Results.Ok(links);
        });

        api.MapGet("/projects/{projectId}/linked-channels", async (
            ChannelsRepository repository, string projectId, CancellationToken cancellationToken) =>
        {
            var channels = await repository.GetLinkedChannelsForProjectAsync(projectId, cancellationToken);
            return Results.Ok(channels);
        });

        api.MapPost("/channel-project-links", async (
            ChannelsRepository repository, UpsertChannelProjectLinkRequest request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var channel = await repository.GetChannelAsync(request.ChannelId, cancellationToken);
                if (channel is null)
                    return Results.NotFound(new { code = "channel_not_found", message = $"Channel {request.ChannelId} not found." });

                var link = await repository.UpsertChannelProjectLinkAsync(request, cancellationToken);
                return Results.Ok(link);
            }
            catch (SqliteException ex) when (IsConstraintFailure(ex))
            {
                return Results.Conflict(new ProblemDetailsDto("link_constraint_failed", ex.SqliteErrorCode, ex.Message));
            }
        });

        api.MapDelete("/channel-project-links", async (
            ChannelsRepository repository, long channelId, string projectId,
            CancellationToken cancellationToken) =>
        {
            await repository.RemoveChannelProjectLinkAsync(channelId, projectId, cancellationToken);
            return Results.NoContent();
        });

        return api;
    }

    private static IResult ToActivityRouteHttpResult(ChannelActivityRouteResultDto result) =>
        result.Status == "rejected" ? Results.BadRequest(result) : Results.Ok(result);

    private static ChannelMembershipDiscoveryDto ToChannelMembershipDiscoveryDto(ChannelMembershipDiscoveryRowDto row)
    {
        var m = row.Membership;
        return new ChannelMembershipDiscoveryDto(
            row.ChannelId,
            row.ChannelSlug,
            row.ChannelKind,
            row.ProjectId,
            m.Id,
            m.MemberType,
            m.MemberIdentity,
            m.MembershipStatus,
            m.WakePolicy,
            m.CanSend,
            m.CanReact,
            m.CanInvite,
            m.CooldownSeconds,
            m.MaxAutoRepliesPerWindow,
            SafeSettingsLabel(m.SettingsJson),
            m.MembershipPurpose,
            m.CreatedAt,
            m.UpdatedAt,
            string.Equals(m.MembershipStatus, "left", StringComparison.OrdinalIgnoreCase) ? m.UpdatedAt : null);
    }

    private static string? SafeSettingsLabel(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            var parts = new List<string>();
            AddAllowedSettingsPart(document.RootElement, parts, "profile", "profile");
            AddAllowedSettingsPart(document.RootElement, parts, "profileName", "profile");
            AddAllowedSettingsPart(document.RootElement, parts, "profile_id", "profile");
            AddAllowedSettingsPart(document.RootElement, parts, "binding", "binding");
            AddAllowedSettingsPart(document.RootElement, parts, "bindingName", "binding");
            AddAllowedSettingsPart(document.RootElement, parts, "sessionId", "session");
            return parts.Count == 0 ? null : string.Join(" · ", parts.Distinct());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AddAllowedSettingsPart(JsonElement root, ICollection<string> parts, string propertyName, string label)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String) return;
        var text = value.GetString()?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
            parts.Add($"{label}: {text}");
    }

    private static bool IsConstraintFailure(SqliteException ex) => ex.SqliteErrorCode == 19;

    private sealed record ProblemDetailsDto(string Code, int SqliteErrorCode, string Detail);
}
