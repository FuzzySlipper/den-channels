using Microsoft.Data.Sqlite;

namespace DenChannels.Service.Channels;

public static class ChannelRoutes
{
    public static RouteGroupBuilder MapChannelRoutes(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

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
            long? afterId, int? limit, CancellationToken cancellationToken) => Results.Ok(
                await repository.ListMessagesAsync(channelId, afterId, limit ?? 100, cancellationToken)));

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
            long? anchorMessageId, long? taskId, long? afterId, int? limit, CancellationToken cancellationToken) => Results.Ok(
                await repository.ListActivityEventsAsync(channelId, deliveryRequestId, hermesSessionKey, displayBlockId,
                    workerRunId, anchorMessageId, taskId, afterId, limit ?? 100, cancellationToken)));

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
