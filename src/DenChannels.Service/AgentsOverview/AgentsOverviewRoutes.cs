namespace DenChannels.Service.AgentsOverview;

public static class AgentsOverviewRoutes
{
    public static RouteGroupBuilder MapAgentsOverviewRoutes(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        // =========================================================================
        // GET /api/agents/overview
        // Composed read-only list of agents with memberships, Gateway bindings,
        // and recent activity.
        // =========================================================================
        api.MapGet("/agents/overview", async (
            AgentsOverviewService service,
            string? projectId,
            string? channelId,
            string? scope,
            string? agentIdentity,
            int? activityLimit,
            bool? includeLeft,
            CancellationToken cancellationToken) =>
        {
            long? parsedChannelId = null;
            if (long.TryParse(channelId, out var cid))
                parsedChannelId = cid;

            var response = await service.GetOverviewAsync(
                projectId, parsedChannelId, scope, agentIdentity,
                activityLimit ?? 3, includeLeft ?? false,
                cancellationToken);

            return Results.Ok(response);
        });

        // =========================================================================
        // GET /api/agents/{agentIdentity}/overview
        // Detail view for a single agent with full membership, binding, delivery,
        // activity, and task association data.
        // =========================================================================
        api.MapGet("/agents/{agentIdentity}/overview", async (
            AgentsOverviewService service,
            string agentIdentity,
            string? projectId,
            string? channelId,
            int? activityLimit,
            int? deliveryLimit,
            CancellationToken cancellationToken) =>
        {
            // URL-decode the identity in case it contains encoded characters
            var decodedIdentity = Uri.UnescapeDataString(agentIdentity);

            long? parsedChannelId = null;
            if (long.TryParse(channelId, out var cid))
                parsedChannelId = cid;

            var response = await service.GetAgentDetailAsync(
                decodedIdentity, projectId, parsedChannelId,
                activityLimit ?? 50, deliveryLimit ?? 50,
                cancellationToken);

            return Results.Ok(response);
        });

        return api;
    }
}
