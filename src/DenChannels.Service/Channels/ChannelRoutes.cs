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
            try
            {
                var message = await repository.PostMessageAsync(channelId, request, cancellationToken);
                return Results.Created($"/api/channels/{channelId}/messages/{message.Id}", message);
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
            string? deliveryRequestId, string? hermesSessionKey, string? displayBlockId, string? workerRunId,
            string? agentInstanceId, long? anchorMessageId, long? taskId, string? assignmentId, long? afterId, int? limit, CancellationToken cancellationToken) => Results.Ok(
                await repository.ListActivityEventsAsync(channelId, deliveryRequestId, hermesSessionKey, displayBlockId,
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

        return api;
    }

    private static bool IsConstraintFailure(SqliteException ex) => ex.SqliteErrorCode == 19;

    private sealed record ProblemDetailsDto(string Code, int SqliteErrorCode, string Detail);
}
