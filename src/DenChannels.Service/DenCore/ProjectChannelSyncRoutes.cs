namespace DenChannels.Service.DenCore;

public static class ProjectChannelSyncRoutes
{
    public static RouteGroupBuilder MapProjectChannelSyncRoutes(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/project-channel-sync");

        group.MapPut("/projects/{projectId}", async (ProjectChannelSyncService sync, string projectId,
            CancellationToken cancellationToken) => Results.Ok(await sync.EnsureProjectChannelAsync(projectId, cancellationToken)));

        group.MapPost("/", async (ProjectChannelSyncService sync, ProjectChannelSyncRequest? request,
            CancellationToken cancellationToken) => Results.Ok(await sync.SyncProjectChannelsAsync(request?.Projects, cancellationToken)));

        return group;
    }
}
