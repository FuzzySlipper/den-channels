namespace DenChannels.Service.Mirrors;

public static class MirrorSummaryRoutes
{
    public static RouteGroupBuilder MapMirrorSummaryRoutes(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/mirror-summaries");

        group.MapPost("/ingest", async (MirrorSummaryIngestionService ingestion, MirrorEventIngestRequest request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await ingestion.IngestAsync(request.Events, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return group;
    }
}
