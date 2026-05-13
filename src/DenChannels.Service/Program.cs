using DenChannels.Service;
using DenChannels.Service.Channels;
using DenChannels.Service.Configuration;
using DenChannels.Service.Data;
using DenChannels.Service.DenCore;
using DenChannels.Service.Gateway;
using DenChannels.Service.Mirrors;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<DenChannelsOptions>()
    .Bind(builder.Configuration.GetSection(DenChannelsOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Database.Path),
        "DenChannels:Database:Path must be configured.")
    .Validate(options => Uri.TryCreate(options.DenCore.BaseUrl, UriKind.Absolute, out _),
        "DenChannels:DenCore:BaseUrl must be an absolute URI.")
    .ValidateOnStart();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSingleton<ChannelsDatabaseInitializer>();
builder.Services.AddSingleton<ChannelsRepository>();
builder.Services.AddHttpClient<IDenCoreProjectClient, DenCoreProjectClient>();
builder.Services.AddSingleton<ProjectChannelSyncService>();
builder.Services.AddSingleton<MirrorSummaryIngestionService>();

var app = builder.Build();

// Den Web: static operator UI now lives with den-channels instead of the MCP adapter.
// If wwwroot/index.html is present, / serves the SPA. In source-only test runs the
// service metadata endpoint below still answers /.
app.UseDefaultFiles();
app.UseStaticFiles();

var serviceOptions = app.Services.GetRequiredService<IOptions<DenChannelsOptions>>();
if (serviceOptions.Value.Database.ApplyMigrationsOnStartup)
{
    await app.Services.GetRequiredService<ChannelsDatabaseInitializer>().InitializeAsync();
}

app.MapGet("/api/service-info", () => Results.Ok(new
{
    service = "den-channels",
    description = "Standalone Den Channels service",
    docs = "/health/live"
}));

app.MapGet("/health/live", () => Results.Ok(new HealthResponse(
    Service: "den-channels",
    Status: "ok",
    Checks: new Dictionary<string, string>
    {
        ["process"] = "running"
    })));

app.MapGet("/health/ready", (IOptions<DenChannelsOptions> options) =>
{
    var checks = new Dictionary<string, string>
    {
        ["configuration"] = "ok",
        ["databasePath"] = options.Value.Database.Path,
        ["denCoreBaseUrl"] = options.Value.DenCore.BaseUrl
    };

    return Results.Ok(new HealthResponse(
        Service: "den-channels",
        Status: "ready",
        Checks: checks));
});

app.MapChannelRoutes();
app.MapProjectChannelSyncRoutes();
app.MapMirrorSummaryRoutes();
app.MapDenCoreApiProxy();
app.MapGatewayRoutes();

// Keep API misses machine-readable. The SPA fallback is only for browser routes.
app.MapFallback((HttpContext context, IWebHostEnvironment environment) =>
{
    if (context.Request.Path.StartsWithSegments("/api") ||
        context.Request.Path.StartsWithSegments("/health") ||
        context.Request.Path.StartsWithSegments("/den-core-api"))
    {
        return Results.NotFound(new { error = "not_found", path = context.Request.Path.Value });
    }

    var index = environment.WebRootFileProvider.GetFileInfo("index.html");
    return index.Exists
        ? Results.File(index.CreateReadStream(), "text/html; charset=utf-8")
        : Results.NotFound(new { error = "web_frontend_not_built" });
});

app.Run();

public partial class Program;

internal sealed record HealthResponse(
    string Service,
    string Status,
    IReadOnlyDictionary<string, string> Checks);
