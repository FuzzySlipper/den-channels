using DenChannels.Service;
using DenChannels.Service.AgentsOverview;
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
builder.Services.AddHttpClient<GatewayStateClient>(client =>
{
    // Base address is set per-request in the client; this just registers DI
});
builder.Services.AddHttpClient<IWorkerPoolStateClient, WorkerPoolStateClient>(client =>
{
    // Base address is set per-request in the client; this just registers DI
});
builder.Services.AddSingleton<AgentsOverviewService>();

var app = builder.Build();

// den-channels no longer owns the SPA. Static wwwroot files serve a minimal
// moved-page referencing Den Web at http://192.168.1.10:18080.
// API misses return JSON; public root paths serve the static page.
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
app.MapAgentsOverviewRoutes();

// API misses: machine-readable JSON. Root/public paths: serve the moved-page.
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
        : Results.NotFound(new { error = "static_page_not_found" });
});

app.Run();

public partial class Program;

internal sealed record HealthResponse(
    string Service,
    string Status,
    IReadOnlyDictionary<string, string> Checks);
