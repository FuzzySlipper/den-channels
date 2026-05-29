using DenChannels.Service.AgentsOverview;

namespace DenChannels.Service.Gateway;

/// <summary>
/// Bounded observation of Gateway-owned delivery state for a Channels direct-agent message.
/// This is intentionally an observation, not final task completion truth.
/// </summary>
public sealed record DirectAgentDeliveryObservation(
    string DeliveryStatus,
    string ClaimStatus,
    string CompletionStatus,
    string SuppressionStatus,
    long? DeliveryRequestId = null,
    long? AttemptId = null,
    string? GatewayDeliveryState = null,
    string? GatewayAttemptStatus = null,
    string? EvidenceSummary = null,
    bool TimedOut = false,
    bool GatewayUnavailable = false)
{
    public static DirectAgentDeliveryObservation RecordedPending(string? evidenceSummary = null, bool gatewayUnavailable = false) =>
        new(
            DeliveryStatus: "recorded_but_not_claimed_yet",
            ClaimStatus: "unclaimed",
            CompletionStatus: "pending",
            SuppressionStatus: "not_suppressed",
            EvidenceSummary: evidenceSummary ?? "Direct agent wake_event recorded; no Gateway delivery request/claim evidence observed yet.",
            GatewayUnavailable: gatewayUnavailable);

    public static DirectAgentDeliveryObservation Timeout(string? evidenceSummary = null) =>
        RecordedPending(evidenceSummary ?? "Direct agent wake_event recorded; timed out waiting for Gateway claim evidence.") with { TimedOut = true };
}

public static class GatewayDirectAgentDeliveryStatus
{
    public static DirectAgentDeliveryObservation FromGatewayState(GatewayStateDto? state, string requestId)
    {
        if (state is null)
        {
            return DirectAgentDeliveryObservation.RecordedPending(
                "Direct agent wake_event recorded; Gateway status projection unavailable, so claim/completion could not be verified.",
                gatewayUnavailable: true);
        }

        var delivery = state.Agents
            .SelectMany(agent => (agent.CurrentDeliveries ?? []).Concat(agent.RecentDeliveries ?? []))
            .Where(delivery => string.Equals(delivery.SourceId, requestId, StringComparison.Ordinal))
            .OrderByDescending(delivery => delivery.UpdatedAt ?? delivery.CreatedAt ?? string.Empty, StringComparer.Ordinal)
            .FirstOrDefault();

        if (delivery is null)
        {
            return DirectAgentDeliveryObservation.RecordedPending();
        }

        var status = (delivery.Status ?? string.Empty).Trim().ToLowerInvariant();
        var suppressionStatus = status == "suppressed" ? "suppressed" : "not_suppressed";
        var claimStatus = status switch
        {
            "delivering" or "delivered" or "acknowledged" or "completed" => "claimed",
            "failed" or "expired" or "suppressed" when delivery.AttemptCount > 0 || delivery.LastAttempt is not null => "claimed",
            _ => "unclaimed"
        };
        var completionStatus = status switch
        {
            "completed" => "completed",
            "failed" => "failed",
            "expired" => "expired",
            "suppressed" => "suppressed",
            _ => "pending"
        };
        var deliveryStatus = status switch
        {
            "pending" => "enqueued",
            "delivering" => "claimed",
            "delivered" => "received",
            "acknowledged" => "acknowledged",
            "completed" => "completed",
            "failed" => "failed",
            "expired" => "expired",
            "suppressed" => "suppressed",
            _ => string.IsNullOrEmpty(status) ? "recorded_but_not_claimed_yet" : status
        };

        var evidence = deliveryStatus switch
        {
            "enqueued" => "Gateway delivery request exists but has not been claimed by the target runtime yet.",
            "claimed" => "Gateway delivery request was claimed by the target runtime/adapter.",
            "received" => "Target runtime/adapter reported the delivery as delivered/received.",
            "acknowledged" => "Target runtime/session acknowledged accepting the delivery.",
            "completed" => "Target runtime reported final delivery completion.",
            "suppressed" => "Gateway recorded the direct-agent delivery as suppressed.",
            "failed" or "expired" => "Gateway recorded a terminal delivery failure/expiry.",
            _ => "Gateway delivery state observed."
        };

        return new DirectAgentDeliveryObservation(
            DeliveryStatus: deliveryStatus,
            ClaimStatus: claimStatus,
            CompletionStatus: completionStatus,
            SuppressionStatus: suppressionStatus,
            DeliveryRequestId: delivery.DeliveryRequestId,
            AttemptId: delivery.LastAttempt?.AttemptId,
            GatewayDeliveryState: delivery.Status,
            GatewayAttemptStatus: delivery.LastAttempt?.Status,
            EvidenceSummary: evidence);
    }

    public static bool MeetsWaitTarget(DirectAgentDeliveryObservation observation, string waitFor)
    {
        var target = NormalizeWaitFor(waitFor);
        return target switch
        {
            "none" => true,
            "claim" => observation.ClaimStatus == "claimed" || IsTerminal(observation.CompletionStatus) || observation.DeliveryStatus is "claimed" or "received" or "acknowledged" or "completed",
            "ack" => observation.DeliveryStatus is "acknowledged" or "completed" || observation.CompletionStatus == "completed",
            "completion" => IsTerminal(observation.CompletionStatus),
            _ => false
        };
    }

    public static string NormalizeWaitFor(string? waitFor)
    {
        if (string.IsNullOrWhiteSpace(waitFor))
            return "claim";
        return waitFor.Trim().ToLowerInvariant() switch
        {
            "none" => "none",
            "no_wait" => "none",
            "enqueue" => "none",
            "claim" => "claim",
            "received" => "claim",
            "ack" => "ack",
            "acknowledged" => "ack",
            "complete" => "completion",
            "completed" => "completion",
            "completion" => "completion",
            _ => "claim"
        };
    }

    private static bool IsTerminal(string status) => status is "completed" or "failed" or "expired" or "suppressed";
}
