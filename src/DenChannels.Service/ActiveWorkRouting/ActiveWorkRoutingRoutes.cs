using DenChannels.Service.ActiveWorkRouting;

namespace DenChannels.Service.ActiveWorkRouting;

public static class ActiveWorkRoutingRoutes
{
    public static RouteGroupBuilder MapActiveWorkRoutingRoutes(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        // =========================================================================
        // POST /api/active-work/resolve
        // Resolve the active work continuation target for given target
        // project/task/assignment/run filters. Always returns 200 with an
        // explicit route status (routed / no_active_route / stale).
        // =========================================================================
        api.MapPost("/active-work/resolve", async (
            ActiveWorkRoutingService service,
            ResolveActiveWorkRouteRequest? request,
            CancellationToken cancellationToken) =>
        {
            request ??= new ResolveActiveWorkRouteRequest();
            var response = await service.ResolveRouteAsync(request, cancellationToken);
            return Results.Ok(response);
        });

        // =========================================================================
        // GET /api/active-work/routes
        // List active work routes matching filter criteria.
        // =========================================================================
        api.MapGet("/active-work/routes", async (
            ActiveWorkRoutingService service,
            string? targetProjectId,
            long? targetTaskId,
            string? assignmentId,
            string? profileIdentity,
            bool? includeStale,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var request = new ListActiveWorkRoutesRequest(
                TargetProjectId: targetProjectId,
                TargetTaskId: targetTaskId,
                AssignmentId: assignmentId,
                ProfileIdentity: profileIdentity,
                IncludeStale: includeStale ?? false,
                Limit: limit ?? 50);

            var response = await service.ListRoutesAsync(request, cancellationToken);
            return Results.Ok(response);
        });

        return api;
    }
}
