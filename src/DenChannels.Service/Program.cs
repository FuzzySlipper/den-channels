using DenChannels.Service;
using DenChannels.Service.ActiveWorkRouting;
using DenChannels.Service.AgentsOverview;
using DenChannels.Service.Channels;
using DenChannels.Service.Configuration;
using DenChannels.Service.Data;
using DenChannels.Service.DenCore;
using DenChannels.Service.DirectAgentEvents;
using DenChannels.Service.Gateway;
using DenChannels.Service.Mirrors;
using DenChannels.Service.Presence;
using DenChannels.Service.Subscriptions;
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
builder.Services.AddSingleton<ChannelRepository>();
builder.Services.AddSingleton<MembershipRepository>();
builder.Services.AddSingleton<WorkerPoolMembershipRepository>();
builder.Services.AddSingleton<DirectConversationRepository>();
builder.Services.AddSingleton<ChannelProjectLinkRepository>();
builder.Services.AddSingleton<ChannelOverviewRepository>();
builder.Services.AddSingleton<SubscriptionRepository>();
builder.Services.AddHttpClient<IDenCoreProjectClient, DenCoreProjectClient>();
builder.Services.AddSingleton<ProjectChannelSyncService>();
builder.Services.AddSingleton<MirrorSummaryIngestionService>();
builder.Services.AddHttpClient<IWorkerPoolStateClient, WorkerPoolStateClient>(client =>
{
    // Base address is set per-request in the client; this just registers DI
});
builder.Services.AddSingleton<AgentsOverviewService>();
builder.Services.AddSingleton<ActiveWorkRoutingService>();
builder.Services.AddSingleton<ChannelActivityEventRoutingService>();
builder.Services.AddSingleton<PresenceProjectionService>();

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
app.MapSubscriptionRoutes();
app.MapProjectChannelSyncRoutes();
app.MapMirrorSummaryRoutes();
app.MapDenCoreApiProxy();
app.MapDirectAgentEventRoutes();
app.MapDirectConversationRoutes();
app.MapGatewayRoutes();
app.MapAgentsOverviewRoutes();
app.MapActiveWorkRoutingRoutes();
app.MapAgentWorkLifecycleRoutes();
app.MapPresenceRoutes();

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
        : Results.Content(DefaultMovedPageHtml(), "text/html; charset=utf-8");
});

app.Run();

static string DefaultMovedPageHtml() => """
    <!doctype html>
    <html lang="en">
      <head>
        <meta charset="UTF-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1.0" />
        <title>Den Channels (Backend API)</title>
        <style>
          body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 640px; margin: 48px auto; padding: 0 16px; line-height: 1.6; color: #222; }
          h1 { font-size: 1.5rem; border-bottom: 2px solid #ddd; padding-bottom: 8px; }
          a { color: #2563eb; text-decoration: none; }
          a:hover { text-decoration: underline; }
          code { background: #f1f5f9; padding: 2px 6px; border-radius: 4px; font-size: 0.9em; }
          ul { padding-left: 20px; }
          .note { background: #fef9c3; border-left: 4px solid #eab308; padding: 8px 16px; border-radius: 4px; margin: 16px 0; }
        </style>
      </head>
      <body>
        <h1>Den Channels &mdash; Backend API Service</h1>
        <p>
          This server hosts the Den Channels backend API. The primary Den Web
          frontend lives elsewhere.
        </p>
        <p>
          <strong>&rarr; <a href="http://192.168.1.10:18080/">Go to Den Web</a></strong>
        </p>
        <div class="note">
          The embedded Channel Chat SPA was retired in task #1708.
          All frontend product work now routes to the <code>den-web</code> repo.
        </div>
        <h2>API endpoints</h2>
        <ul>
          <li><code><a href="/health/live">/health/live</a></code> &mdash; process liveness</li>
          <li><code><a href="/health/ready">/health/ready</a></code> &mdash; readiness check</li>
          <li><code><a href="/api/service-info">/api/service-info</a></code> &mdash; service metadata</li>
          <li><code>/api/channels</code> &mdash; channel CRUD</li>
          <li><code>/api/channels/{id}/messages</code> &mdash; channel messages</li>
        </ul>
        <p>
          See <code>README.md</code> for the full API reference.
        </p>
      </body>
    </html>
    """;

public partial class Program;

internal sealed record HealthResponse(
    string Service,
    string Status,
    IReadOnlyDictionary<string, string> Checks);
