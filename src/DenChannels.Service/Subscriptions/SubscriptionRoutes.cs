using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace DenChannels.Service.Subscriptions;

/// <summary>
/// SubscriptionRoutes: runtime registration, discovery, poll cursors.
/// Extracted from the omnibus ChannelRoutes for module-boundary clarity.
/// </summary>
public static class SubscriptionRoutes
{
    public static RouteGroupBuilder MapSubscriptionRoutes(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        // -----------------------------------------------------------------------
        // GET /api/channel-subscriptions
        // List subscriptions by member identity with optional purpose/project/channel filters.
        // -----------------------------------------------------------------------
        api.MapGet("/channel-subscriptions", async Task<IResult> (SubscriptionRepository repository,
            string? memberIdentity, string? purpose, string? projectId, long? channelId,
            int? limit, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(memberIdentity))
                return Results.BadRequest(new { code = "missing_parameter", message = "Provide memberIdentity." });

            var rows = await repository.ListSubscriptionsByMemberAsync(
                memberIdentity, purpose, projectId, channelId, limit ?? 100, cancellationToken);
            return Results.Ok(new { memberIdentity = memberIdentity.Trim(), subscriptions = rows });
        });

        // -----------------------------------------------------------------------
        // PUT /api/channels/{channelId}/subscriptions
        // Register or update a channel subscription idempotently.
        // -----------------------------------------------------------------------
        api.MapPut("/channels/{channelId:long}/subscriptions", async (SubscriptionRepository repository, long channelId,
            UpsertChannelSubscriptionRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var subscription = await repository.UpsertSubscriptionAsync(channelId, request, cancellationToken);
                return Results.Ok(subscription);
            }
            catch (SqliteException ex) when (IsConstraintFailure(ex))
            {
                return Results.Conflict(new ProblemDetailsDto("subscription_constraint_failed", ex.SqliteErrorCode, ex.Message));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "invalid_subscription_vocabulary", message = ex.Message });
            }
        });

        // -----------------------------------------------------------------------
        // PUT /api/channel-subscriptions/{subscriptionId}/cursors/{streamKind}
        // Upsert a subscription cursor for a given stream kind.
        // -----------------------------------------------------------------------
        api.MapPut("/channel-subscriptions/{subscriptionId:long}/cursors/{streamKind}", async (
            SubscriptionRepository repository, long subscriptionId, string streamKind,
            UpsertSubscriptionCursorRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var cursorRequest = request with { StreamKind = streamKind };
                var cursor = await repository.UpsertSubscriptionCursorAsync(subscriptionId, cursorRequest, cancellationToken);
                return Results.Ok(cursor);
            }
            catch (SqliteException ex) when (IsConstraintFailure(ex))
            {
                return Results.Conflict(new ProblemDetailsDto("subscription_cursor_constraint_failed", ex.SqliteErrorCode, ex.Message));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "invalid_cursor_vocabulary", message = ex.Message });
            }
        });

        // -----------------------------------------------------------------------
        // GET /api/channel-subscriptions/{subscriptionId}/cursors
        // List all cursors for a subscription.
        // -----------------------------------------------------------------------
        api.MapGet("/channel-subscriptions/{subscriptionId:long}/cursors", async (
            SubscriptionRepository repository, long subscriptionId, CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListSubscriptionCursorsAsync(subscriptionId, cancellationToken)));

        return api;
    }

    private static bool IsConstraintFailure(SqliteException ex) =>
        ex.SqliteErrorCode is 19 or 1555 or 2067 or 787 or 1811 or 275;
}

/// <summary>
/// Minimal problem details DTO matching the ChannelRoutes convention.
/// </summary>
internal sealed record ProblemDetailsDto(string Code, int SqliteErrorCode, string Message);
