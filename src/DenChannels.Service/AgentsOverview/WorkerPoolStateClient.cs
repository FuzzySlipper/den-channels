using System.Text.Json;
using System.Text.Json.Serialization;
using DenChannels.Service.Configuration;
using Microsoft.Extensions.Options;

namespace DenChannels.Service.AgentsOverview;

// =========================================================================
// Worker-pool state DTOs (projected from Core worker-pool API)
// =========================================================================

/// <summary>
/// Top-level response composed from Core worker-pool member and assignment endpoints.
/// </summary>
public sealed record WorkerPoolStateDto(
    string GeneratedAt,
    string PoolId,
    IReadOnlyList<WorkerPoolMemberStateDto> Members);

/// <summary>
/// Individual worker-pool member state projected from Core.
/// </summary>
public sealed record WorkerPoolMemberStateDto(
    string MemberIdentity,
    string? Role,
    string? ToolProfile,
    string Availability,
    string? LastActivityAt,
    WorkerPoolMemberAssignmentStateDto? CurrentAssignment,
    IReadOnlyList<string>? Flags);

/// <summary>
/// Current assignment on a worker-pool member from Core.
/// </summary>
public sealed record WorkerPoolMemberAssignmentStateDto(
    string AssignmentId,
    string? TaskId,
    string? ProjectId,
    string? LeaseOwner,
    string? LeaseExpiresAt,
    string? Phase,
    string? CheckpointType,
    string? CheckpointHandle,
    string? LastCheckpointAt);

/// <summary>
/// Interface for testability. Implementations must gracefully degrade
/// (return null) when the Core worker-pool endpoint is unavailable.
/// </summary>
public interface IWorkerPoolStateClient
{
    Task<WorkerPoolStateDto?> FetchWorkersAsync(
        string? projectId = null,
        string? agentIdentity = null,
        CancellationToken cancellationToken = default);

    Task<WorkerPoolAssignmentTraceCoreDto?> FetchAssignmentTraceAsync(
        string assignmentId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Full Core worker-pool assignment evidence used by the Den Web assignment trace.
/// </summary>
public sealed record WorkerPoolAssignmentTraceCoreDto(
    WorkerPoolAssignmentDetailDto Assignment,
    IReadOnlyList<WorkerPoolCheckpointDetailDto> Checkpoints,
    IReadOnlyList<WorkerPoolCheckpointResponseDetailDto> Responses);

public sealed record WorkerPoolAssignmentDetailDto(
    int Id,
    string WorkerIdentity,
    string RunId,
    string ProjectId,
    int? TaskId,
    string Role,
    string AssignedBy,
    string State,
    int? LatestCheckpointId,
    string? CleanupEvidence,
    string? CleanupRecordedAt,
    string? AcquiredAt,
    string? ReleasedAt,
    DateTime? CreatedAt,
    DateTime? UpdatedAt);

public sealed record WorkerPoolCheckpointDetailDto(
    int Id,
    int AssignmentId,
    string RunId,
    string CheckpointType,
    string Payload,
    DateTime? CreatedAt);

public sealed record WorkerPoolCheckpointResponseDetailDto(
    int Id,
    int CheckpointId,
    int AssignmentId,
    string RunId,
    string ResponseType,
    string Payload,
    DateTime? CreatedAt);

/// <summary>
/// HTTP client for Core-owned worker-pool state. It uses only read endpoints
/// and composes a Channels-friendly observability projection locally.
/// Graceful degradation: all failures return null rather than throwing.
/// </summary>
public sealed class WorkerPoolStateClient : IWorkerPoolStateClient
{
    private static readonly HashSet<string> ActiveAssignmentStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "ack",
        "running",
        "checkpoint_waiting",
        "blocked"
    };

    private readonly HttpClient _httpClient;
    private readonly WorkerPoolOptions _workerPoolOptions;
    private readonly ILogger<WorkerPoolStateClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public WorkerPoolStateClient(HttpClient httpClient, IOptions<DenChannelsOptions> options,
        ILogger<WorkerPoolStateClient> logger)
    {
        _httpClient = httpClient;
        _workerPoolOptions = options.Value.WorkerPool;
        _logger = logger;
    }

    /// <summary>
    /// Fetch worker-pool state from Core. Returns null on any failure
    /// (network, timeout, bad response, disabled). This method is read-only.
    /// </summary>
    public async Task<WorkerPoolStateDto?> FetchWorkersAsync(
        string? projectId = null,
        string? agentIdentity = null,
        CancellationToken cancellationToken = default)
    {
        if (_workerPoolOptions.Disabled)
        {
            _logger.LogDebug("Worker-pool client is disabled via configuration; skipping fetch.");
            return null;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_workerPoolOptions.TimeoutSeconds));

            _httpClient.BaseAddress ??= new Uri(_workerPoolOptions.BaseUrl);

            var membersResponse = await GetJsonAsync<CoreWorkerPoolMemberListResponse>(
                BuildMembersPath(agentIdentity), cts.Token);
            if (membersResponse is null)
                return null;

            var assignmentsResponse = await GetJsonAsync<CoreWorkerAssignmentListResponse>(
                BuildAssignmentsPath(projectId, agentIdentity), cts.Token);
            if (assignmentsResponse is null)
                return null;

            var assignmentsByWorker = assignmentsResponse.Assignments
                .GroupBy(a => a.WorkerIdentity, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.UpdatedAt ?? a.CreatedAt).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var members = membersResponse.Members
                .Select(member => ToWorkerPoolMemberState(member,
                    assignmentsByWorker.TryGetValue(member.WorkerIdentity, out var assignments) ? assignments : []))
                .ToList();

            return new WorkerPoolStateDto(
                GeneratedAt: DateTimeOffset.UtcNow.ToString("O"),
                PoolId: "default",
                Members: members);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Core worker-pool request timed out after {Timeout}s.", _workerPoolOptions.TimeoutSeconds);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Core worker-pool request failed (HTTP transport error).");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Core worker-pool response could not be deserialized.");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Core worker-pool client is misconfigured.");
            return null;
        }
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Core worker-pool endpoint {Path} returned {StatusCode}", path, response.StatusCode);
            return default;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    /// <summary>
    /// Fetch full Core assignment/checkpoint/response evidence for a single assignment.
    /// Returns null on transport, timeout, disabled client, 404, or malformed JSON.
    /// </summary>
    public async Task<WorkerPoolAssignmentTraceCoreDto?> FetchAssignmentTraceAsync(
        string assignmentId,
        CancellationToken cancellationToken = default)
    {
        if (_workerPoolOptions.Disabled)
        {
            _logger.LogDebug("Worker-pool client is disabled via configuration; skipping assignment trace fetch.");
            return null;
        }

        if (!int.TryParse(assignmentId, out var numericAssignmentId))
            return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_workerPoolOptions.TimeoutSeconds));

            _httpClient.BaseAddress ??= new Uri(_workerPoolOptions.BaseUrl);

            var assignment = await GetJsonAsync<CoreWorkerAssignment>(
                $"/api/worker-pool/assignments/{numericAssignmentId}", cts.Token);
            if (assignment is null)
                return null;

            var checkpointsResponse = await GetJsonAsync<CoreWorkerCheckpointListResponse>(
                $"/api/worker-pool/checkpoints?assignmentId={numericAssignmentId}&runId={Uri.EscapeDataString(assignment.RunId)}&limit=200",
                cts.Token);
            var responsesResponse = await GetJsonAsync<CoreWorkerCheckpointResponseListResponse>(
                $"/api/worker-pool/responses/by-run/{Uri.EscapeDataString(assignment.RunId)}?limit=200",
                cts.Token);

            return new WorkerPoolAssignmentTraceCoreDto(
                ToAssignmentDetail(assignment),
                checkpointsResponse?.Checkpoints.Select(ToCheckpointDetail).ToList() ?? [],
                responsesResponse?.Responses.Select(ToResponseDetail).ToList() ?? []);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Core worker-pool assignment trace request timed out after {Timeout}s.", _workerPoolOptions.TimeoutSeconds);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Core worker-pool assignment trace request failed (HTTP transport error).");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Core worker-pool assignment trace response could not be deserialized.");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Core worker-pool client is misconfigured for assignment trace.");
            return null;
        }
    }

    private static WorkerPoolAssignmentDetailDto ToAssignmentDetail(CoreWorkerAssignment assignment) => new(
        assignment.Id,
        assignment.WorkerIdentity,
        assignment.RunId,
        assignment.ProjectId,
        assignment.TaskId,
        assignment.Role,
        assignment.AssignedBy,
        assignment.State,
        assignment.LatestCheckpointId,
        assignment.CleanupEvidence,
        assignment.CleanupRecordedAt,
        assignment.AcquiredAt,
        assignment.ReleasedAt,
        assignment.CreatedAt,
        assignment.UpdatedAt);

    private static WorkerPoolCheckpointDetailDto ToCheckpointDetail(CoreWorkerCheckpoint checkpoint) => new(
        checkpoint.Id,
        checkpoint.AssignmentId,
        checkpoint.RunId,
        checkpoint.CheckpointType,
        checkpoint.Payload,
        checkpoint.CreatedAt);

    private static WorkerPoolCheckpointResponseDetailDto ToResponseDetail(CoreWorkerCheckpointResponse response) => new(
        response.Id,
        response.CheckpointId,
        response.AssignmentId,
        response.RunId,
        response.ResponseType,
        response.Payload,
        response.CreatedAt);

    private static WorkerPoolMemberStateDto ToWorkerPoolMemberState(
        CoreWorkerPoolMember member,
        IReadOnlyList<CoreWorkerAssignment> assignments)
    {
        var currentAssignment = PickCurrentAssignment(assignments);
        var projectedAvailability = ProjectAvailability(member.Status, currentAssignment);
        var flags = BuildMemberFlags(member, currentAssignment, projectedAvailability);

        return new WorkerPoolMemberStateDto(
            MemberIdentity: member.WorkerIdentity,
            Role: currentAssignment?.Role,
            ToolProfile: TryReadMetadataString(member.Metadata, "tool_profile")
                         ?? TryReadMetadataString(member.Metadata, "profile"),
            Availability: projectedAvailability,
            LastActivityAt: member.LastHeartbeat ?? member.UpdatedAt?.ToString("O"),
            CurrentAssignment: currentAssignment is null ? null : ToAssignmentState(currentAssignment),
            Flags: flags.Count == 0 ? null : flags);
    }

    private static CoreWorkerAssignment? PickCurrentAssignment(IReadOnlyList<CoreWorkerAssignment> assignments)
    {
        var active = assignments
            .Where(a => ActiveAssignmentStates.Contains(a.State))
            .OrderByDescending(a => a.UpdatedAt ?? a.CreatedAt)
            .FirstOrDefault();
        if (active is not null)
            return active;

        return assignments
            .Where(a => a.ReleasedAt is null && a.CleanupEvidence is null)
            .OrderByDescending(a => a.UpdatedAt ?? a.CreatedAt)
            .FirstOrDefault();
    }

    private static string ProjectAvailability(string status, CoreWorkerAssignment? currentAssignment)
    {
        if (currentAssignment is not null && ActiveAssignmentStates.Contains(currentAssignment.State))
            return "leased";

        return status.ToLowerInvariant() switch
        {
            "busy" => "leased",
            "quarantined" => "quarantined",
            "offboarded" => "offline",
            var other => other
        };
    }

    private static List<string> BuildMemberFlags(
        CoreWorkerPoolMember member,
        CoreWorkerAssignment? currentAssignment,
        string availability)
    {
        var flags = new List<string>();
        if (string.Equals(member.Status, "busy", StringComparison.OrdinalIgnoreCase) && currentAssignment is null)
            flags.Add("core_busy_without_assignment");
        if (currentAssignment is not null && !ActiveAssignmentStates.Contains(currentAssignment.State) && currentAssignment.CleanupEvidence is null)
            flags.Add("cleanup_pending");
        if (string.Equals(availability, "offline", StringComparison.OrdinalIgnoreCase))
            flags.Add("core_offboarded");
        return flags;
    }

    private static WorkerPoolMemberAssignmentStateDto ToAssignmentState(CoreWorkerAssignment assignment)
    {
        var cleanupPending = !ActiveAssignmentStates.Contains(assignment.State)
                             && assignment.CleanupEvidence is null
                             && assignment.ReleasedAt is null;
        var phase = cleanupPending ? "cleanup_pending" : assignment.State;
        var checkpointHandle = assignment.LatestCheckpointId.HasValue
            ? $"/api/worker-pool/checkpoints/{assignment.LatestCheckpointId.Value}"
            : null;

        return new WorkerPoolMemberAssignmentStateDto(
            AssignmentId: assignment.Id.ToString(),
            TaskId: assignment.TaskId?.ToString(),
            ProjectId: assignment.ProjectId,
            LeaseOwner: assignment.AssignedBy,
            LeaseExpiresAt: null,
            Phase: phase,
            CheckpointType: assignment.LatestCheckpointId.HasValue ? "latest" : null,
            CheckpointHandle: checkpointHandle,
            LastCheckpointAt: assignment.UpdatedAt?.ToString("O"));
    }

    private static string BuildMembersPath(string? agentIdentity)
    {
        var query = new List<string> { "limit=200" };
        if (!string.IsNullOrWhiteSpace(agentIdentity))
            query.Add($"workerIdentity={Uri.EscapeDataString(agentIdentity)}");
        return $"/api/worker-pool/members?{string.Join('&', query)}";
    }

    private static string BuildAssignmentsPath(string? projectId, string? agentIdentity)
    {
        var query = new List<string> { "limit=200" };
        if (!string.IsNullOrWhiteSpace(projectId))
            query.Add($"projectId={Uri.EscapeDataString(projectId)}");
        if (!string.IsNullOrWhiteSpace(agentIdentity))
            query.Add($"workerIdentity={Uri.EscapeDataString(agentIdentity)}");
        return $"/api/worker-pool/assignments?{string.Join('&', query)}";
    }

    private static string? TryReadMetadataString(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            return doc.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record CoreWorkerPoolMemberListResponse(
        [property: JsonPropertyName("members")] IReadOnlyList<CoreWorkerPoolMember> Members,
        [property: JsonPropertyName("count")] int Count);

    private sealed record CoreWorkerAssignmentListResponse(
        [property: JsonPropertyName("assignments")] IReadOnlyList<CoreWorkerAssignment> Assignments,
        [property: JsonPropertyName("count")] int Count);

    private sealed record CoreWorkerCheckpointListResponse(
        [property: JsonPropertyName("checkpoints")] IReadOnlyList<CoreWorkerCheckpoint> Checkpoints,
        [property: JsonPropertyName("count")] int Count);

    private sealed record CoreWorkerCheckpointResponseListResponse(
        [property: JsonPropertyName("responses")] IReadOnlyList<CoreWorkerCheckpointResponse> Responses,
        [property: JsonPropertyName("count")] int Count);

    private sealed record CoreWorkerPoolMember(
        [property: JsonPropertyName("worker_identity")] string WorkerIdentity,
        [property: JsonPropertyName("display_name")] string? DisplayName,
        [property: JsonPropertyName("capabilities")] string? Capabilities,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("last_heartbeat")] string? LastHeartbeat,
        [property: JsonPropertyName("metadata")] string? Metadata,
        [property: JsonPropertyName("created_at")] DateTime? CreatedAt,
        [property: JsonPropertyName("updated_at")] DateTime? UpdatedAt);

    private sealed record CoreWorkerAssignment(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("worker_identity")] string WorkerIdentity,
        [property: JsonPropertyName("run_id")] string RunId,
        [property: JsonPropertyName("project_id")] string ProjectId,
        [property: JsonPropertyName("task_id")] int? TaskId,
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("assigned_by")] string AssignedBy,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("latest_checkpoint_id")] int? LatestCheckpointId,
        [property: JsonPropertyName("cleanup_evidence")] string? CleanupEvidence,
        [property: JsonPropertyName("cleanup_recorded_at")] string? CleanupRecordedAt,
        [property: JsonPropertyName("acquired_at")] string? AcquiredAt,
        [property: JsonPropertyName("released_at")] string? ReleasedAt,
        [property: JsonPropertyName("created_at")] DateTime? CreatedAt,
        [property: JsonPropertyName("updated_at")] DateTime? UpdatedAt);

    private sealed record CoreWorkerCheckpoint(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("assignment_id")] int AssignmentId,
        [property: JsonPropertyName("run_id")] string RunId,
        [property: JsonPropertyName("checkpoint_type")] string CheckpointType,
        [property: JsonPropertyName("payload")] string Payload,
        [property: JsonPropertyName("created_at")] DateTime? CreatedAt);

    private sealed record CoreWorkerCheckpointResponse(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("checkpoint_id")] int CheckpointId,
        [property: JsonPropertyName("assignment_id")] int AssignmentId,
        [property: JsonPropertyName("run_id")] string RunId,
        [property: JsonPropertyName("response_type")] string ResponseType,
        [property: JsonPropertyName("payload")] string Payload,
        [property: JsonPropertyName("created_at")] DateTime? CreatedAt);
}
