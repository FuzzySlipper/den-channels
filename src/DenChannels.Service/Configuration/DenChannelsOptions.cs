namespace DenChannels.Service.Configuration;

public sealed class DenChannelsOptions
{
    public const string SectionName = "DenChannels";

    public DatabaseOptions Database { get; init; } = new();

    public DenCoreOptions DenCore { get; init; } = new();

    public ServiceAuthOptions ServiceAuth { get; init; } = new();
}

public sealed class DatabaseOptions
{
    /// <summary>
    /// SQLite database file path used by the standalone channels service.
    /// Future schema/migration work will own this database instead of writing channel rows into den-mcp.
    /// </summary>
    public string Path { get; init; } = "data/den-channels.db";

    /// <summary>
    /// Apply the owned schema migrations when the service starts.
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; init; } = true;
}

public sealed class DenCoreOptions
{
    /// <summary>
    /// Base URL for the Den core/den-mcp HTTP API used for project/source metadata.
    /// This is an explicit service boundary; do not reference den-mcp internals from this repo.
    /// </summary>
    public string BaseUrl { get; init; } = "http://127.0.0.1:5199";

    /// <summary>
    /// Enables stubbed project metadata while the Den core contract task is pending.
    /// </summary>
    public bool UseStubProjectMetadata { get; init; } = true;

    /// <summary>
    /// Project metadata used by project-channel sync while Den core integration is stubbed.
    /// </summary>
    public List<DenCoreStubProjectOptions> StubProjects { get; init; } = [];
}

public sealed class DenCoreStubProjectOptions
{
    public string Id { get; init; } = string.Empty;

    public string? Name { get; init; }
}

public sealed class ServiceAuthOptions
{
    /// <summary>
    /// Placeholder for future service-to-service authentication with Den core.
    /// Prefer environment/user-secrets for the value; do not commit real tokens.
    /// </summary>
    public string? ServiceToken { get; init; }
}
