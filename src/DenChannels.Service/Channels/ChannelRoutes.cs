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
            string? deliveryRequestId, string? hermesSessionKey, long? anchorMessageId, long? afterId, int? limit,
            CancellationToken cancellationToken) => Results.Ok(await repository.ListActivityEventsAsync(
                channelId, deliveryRequestId, hermesSessionKey, anchorMessageId, afterId, limit ?? 100, cancellationToken)));

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
