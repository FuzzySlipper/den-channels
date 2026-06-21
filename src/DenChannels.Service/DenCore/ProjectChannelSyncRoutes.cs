using DenChannels.Service.Configuration;
using Microsoft.Extensions.Options;

namespace DenChannels.Service.DenCore;

public static class ProjectChannelSyncRoutes
{
    public static RouteGroupBuilder MapProjectChannelSyncRoutes(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/project-channel-sync");

        group.MapPut("/projects/{projectId}", async (
            ProjectChannelSyncService sync,
            IOptions<DenChannelsOptions> options,
            string projectId,
            CancellationToken cancellationToken) =>
        {
            if (options.Value.LegacyDisplayHistory.TombstoneArchivedRoutes)
                return LegacyRouteTombstone.Gone("PUT /v1/conversation/projects/{project_id}/default-channel");

            return Results.Ok(await sync.EnsureProjectChannelAsync(projectId, cancellationToken));
        });

        group.MapPost("/", async (
            ProjectChannelSyncService sync,
            IOptions<DenChannelsOptions> options,
            ProjectChannelSyncRequest? request,
            CancellationToken cancellationToken) =>
        {
            if (options.Value.LegacyDisplayHistory.TombstoneArchivedRoutes)
                return LegacyRouteTombstone.Gone("PUT /v1/conversation/projects/{project_id}/default-channel");

            return Results.Ok(await sync.SyncProjectChannelsAsync(request?.Projects, cancellationToken));
        });

        return group;
    }
}
