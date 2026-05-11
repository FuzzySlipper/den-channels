using DenChannels.Service.Channels;
using DenChannels.Service.Configuration;
using DenChannels.Service.Data;
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

var app = builder.Build();

var serviceOptions = app.Services.GetRequiredService<IOptions<DenChannelsOptions>>();
if (serviceOptions.Value.Database.ApplyMigrationsOnStartup)
{
    await app.Services.GetRequiredService<ChannelsDatabaseInitializer>().InitializeAsync();
}

app.MapGet("/", () => Results.Ok(new
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

app.Run();

public partial class Program;

internal sealed record HealthResponse(
    string Service,
    string Status,
    IReadOnlyDictionary<string, string> Checks);
