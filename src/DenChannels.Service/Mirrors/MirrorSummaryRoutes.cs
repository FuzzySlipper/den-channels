using DenChannels.Service.Configuration;
using Microsoft.Extensions.Options;

namespace DenChannels.Service.Mirrors;

public static class MirrorSummaryRoutes
{
    public static RouteGroupBuilder MapMirrorSummaryRoutes(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/mirror-summaries");

        group.MapPost("/ingest", async (
            MirrorSummaryIngestionService ingestion,
            IOptions<DenChannelsOptions> options,
            MirrorEventIngestRequest request,
            CancellationToken cancellationToken) =>
        {
            if (options.Value.LegacyDisplayHistory.TombstoneArchivedRoutes)
                return LegacyRouteTombstone.Gone("Timeline/Observation successor ingestion; legacy mirror summaries are archived");

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
