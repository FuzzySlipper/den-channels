using System.Text.Json;
using DenChannels.Service.AgentsOverview;
using DenChannels.Service.Configuration;
using Microsoft.Extensions.Options;

namespace DenChannels.Service.Gateway;

/// <summary>
/// HTTP client for the external Gateway service (/api/agent-overview/gateway-state).
/// Graceful degradation: all failures produce null rather than throwing.
/// </summary>
public sealed class GatewayStateClient
{
    private readonly HttpClient _httpClient;
    private readonly GatewayOptions _options;
    private readonly ILogger<GatewayStateClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public GatewayStateClient(HttpClient httpClient, IOptions<DenChannelsOptions> options,
        ILogger<GatewayStateClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value.Gateway;
        _logger = logger;
    }

    /// <summary>
    /// Fetch Gateway state projection. Returns null on any failure (network, timeout, bad response).
    /// Caller treats null as "Gateway unavailable" and returns Channels-only data.
    /// </summary>
    public async Task<GatewayStateDto?> FetchGatewayStateAsync(
        string? projectId = null, string? agentIdentity = null, CancellationToken cancellationToken = default)
    {
        if (_options.Disabled)
        {
            _logger.LogDebug("Gateway is disabled via configuration; skipping fetch.");
            return null;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var response = await _httpClient.GetAsync(BuildGatewayStatePath(projectId, agentIdentity), cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gateway state endpoint returned {StatusCode}", response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cts.Token);
            var state = JsonSerializer.Deserialize<GatewayStateDto>(content, JsonOptions);
            return state;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Gateway state request timed out after {Timeout}s.", _options.TimeoutSeconds);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Gateway state request failed (HTTP transport error).");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Gateway state response could not be deserialized.");
            return null;
        }
    }

    public async Task<DirectAgentDeliveryObservation> WaitForDirectAgentDeliveryStatusAsync(
        string? projectId,
        string memberIdentity,
        string requestId,
        string? waitFor,
        int? timeoutMs,
        CancellationToken cancellationToken = default)
    {
        var target = GatewayDirectAgentDeliveryStatus.NormalizeWaitFor(waitFor);
        if (target == "none")
        {
            return DirectAgentDeliveryObservation.RecordedPending(
                "Direct agent wake_event recorded; caller requested no Gateway claim wait.");
        }

        var boundedTimeoutMs = Math.Clamp(timeoutMs ?? 1500, 0, 5000);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(boundedTimeoutMs);
        DirectAgentDeliveryObservation latest = DirectAgentDeliveryObservation.RecordedPending();

        do
        {
            var state = await FetchGatewayStateAsync(projectId, memberIdentity, cancellationToken);
            latest = GatewayDirectAgentDeliveryStatus.FromGatewayState(state, requestId);
            if (latest.GatewayUnavailable || GatewayDirectAgentDeliveryStatus.MeetsWaitTarget(latest, target))
                return latest;

            if (boundedTimeoutMs == 0)
                break;

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            var delay = remaining < TimeSpan.FromMilliseconds(150) ? remaining : TimeSpan.FromMilliseconds(150);
            await Task.Delay(delay, cancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return latest with
        {
            TimedOut = true,
            EvidenceSummary = $"Direct agent wake_event recorded; timed out waiting for Gateway {target} evidence."
        };
    }

    private string BuildGatewayStatePath(string? projectId, string? agentIdentity)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectId))
            query.Add($"projectId={Uri.EscapeDataString(projectId)}");
        if (!string.IsNullOrWhiteSpace(agentIdentity))
            query.Add($"agentIdentity={Uri.EscapeDataString(agentIdentity)}");

        var path = $"{_options.BaseUrl.TrimEnd('/')}/api/agent-overview/gateway-state";
        return query.Count == 0 ? path : $"{path}?{string.Join('&', query)}";
    }
}
