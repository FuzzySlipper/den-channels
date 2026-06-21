namespace DenChannels.Service;

internal static class LegacyRouteTombstone
{
    internal static IResult Gone(string replacement)
    {
        return Results.Json(new
        {
            code = "route_gone",
            message = "This legacy den-channels route has been retired. Use the successor route instead.",
            replacement
        }, statusCode: StatusCodes.Status410Gone);
    }
}
