namespace DenChannels.Service.Presence;

/// <summary>
/// PresenceRoutes: GET /api/channels/{channelId}/presence
/// Minimal projection over membership + subscription reachability.
/// </summary>
public static class PresenceRoutes
{
    public static RouteGroupBuilder MapPresenceRoutes(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/channels/{channelId:long}/presence", async (
            PresenceProjectionService presence,
            long channelId,
            CancellationToken cancellationToken) =>
        {
            var result = await presence.GetChannelPresenceAsync(channelId, cancellationToken);
            return result.Members.Count == 0 && result.ChannelSlug.StartsWith("channel-")
                ? Results.NotFound(new { code = "channel_not_found", message = $"Channel {channelId} not found." })
                : Results.Ok(result);
        });

        return api;
    }
}
