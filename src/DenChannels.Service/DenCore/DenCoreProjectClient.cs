using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DenChannels.Service.Channels;
using DenChannels.Service.Configuration;
using Microsoft.Extensions.Options;

namespace DenChannels.Service.DenCore;

public sealed record DenCoreProjectMetadata(string Id, string Name);

public interface IDenCoreProjectClient
{
    Task<IReadOnlyList<DenCoreProjectMetadata>> ListProjectsAsync(CancellationToken cancellationToken = default);

    Task<DenCoreProjectMetadata?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default);
}

public sealed class DenCoreProjectClient : IDenCoreProjectClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<DenChannelsOptions> _options;

    public DenCoreProjectClient(HttpClient httpClient, IOptions<DenChannelsOptions> options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<IReadOnlyList<DenCoreProjectMetadata>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var denCore = _options.Value.DenCore;
        if (denCore.UseStubProjectMetadata)
            return denCore.StubProjects.Select(project => new DenCoreProjectMetadata(
                    project.Id,
                    string.IsNullOrWhiteSpace(project.Name) ? project.Id : project.Name!))
                .ToList();

        ConfigureClient();
        var projects = await _httpClient.GetFromJsonAsync<List<DenCoreProjectResponse>>("/api/projects/", cancellationToken)
            ?? [];
        return projects.Select(project => new DenCoreProjectMetadata(project.Id, project.Name ?? project.Id)).ToList();
    }

    public async Task<DenCoreProjectMetadata?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var denCore = _options.Value.DenCore;
        if (denCore.UseStubProjectMetadata)
        {
            var stub = denCore.StubProjects.FirstOrDefault(project => string.Equals(project.Id, projectId, StringComparison.Ordinal));
            return stub is null
                ? new DenCoreProjectMetadata(projectId, projectId)
                : new DenCoreProjectMetadata(stub.Id, string.IsNullOrWhiteSpace(stub.Name) ? stub.Id : stub.Name!);
        }

        ConfigureClient();
        var project = await _httpClient.GetFromJsonAsync<DenCoreProjectDetailResponse>($"/api/projects/{Uri.EscapeDataString(projectId)}", cancellationToken);
        return project?.Project is null
            ? null
            : new DenCoreProjectMetadata(project.Project.Id, project.Project.Name ?? project.Project.Id);
    }

    private void ConfigureClient()
    {
        var options = _options.Value;
        _httpClient.BaseAddress ??= new Uri(options.DenCore.BaseUrl);
        if (!string.IsNullOrWhiteSpace(options.ServiceAuth.ServiceToken))
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceAuth.ServiceToken);
    }

    private sealed record DenCoreProjectResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record DenCoreProjectDetailResponse(
        [property: JsonPropertyName("project")] DenCoreProjectResponse? Project);
}

public sealed class ProjectChannelSyncService
{
    private readonly IDenCoreProjectClient _projectClient;
    private readonly ChannelRepository _repository;

    public ProjectChannelSyncService(IDenCoreProjectClient projectClient, ChannelRepository repository)
    {
        _projectClient = projectClient;
        _repository = repository;
    }

    public async Task<ChannelDto> EnsureProjectChannelAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var project = await _projectClient.GetProjectAsync(projectId, cancellationToken)
            ?? new DenCoreProjectMetadata(projectId, projectId);
        return await EnsureProjectChannelAsync(project, cancellationToken);
    }

    public async Task<IReadOnlyList<ChannelDto>> SyncProjectChannelsAsync(IReadOnlyList<DenCoreProjectMetadata>? projects,
        CancellationToken cancellationToken = default)
    {
        var sourceProjects = projects is { Count: > 0 }
            ? projects
            : await _projectClient.ListProjectsAsync(cancellationToken);

        var channels = new List<ChannelDto>();
        foreach (var project in sourceProjects.Where(project => !string.IsNullOrWhiteSpace(project.Id)))
            channels.Add(await EnsureProjectChannelAsync(project, cancellationToken));
        return channels;
    }

    private Task<ChannelDto> EnsureProjectChannelAsync(DenCoreProjectMetadata project, CancellationToken cancellationToken) =>
        _repository.EnsureProjectDefaultChannelAsync(project.Id, new EnsureProjectDefaultChannelRequest(
            DisplayName: project.Name,
            CreatedBy: "project-sync",
            SettingsJson: null), cancellationToken);
}

public sealed record ProjectChannelSyncRequest(IReadOnlyList<DenCoreProjectMetadata>? Projects);
