using DenChannels.Service.AgentsOverview;

namespace DenChannels.Service.Gateway;

using DS = DenChannels.Service.DeliveryStatus;
using CS = DenChannels.Service.ClaimStatus;
using CompS = DenChannels.Service.CompletionStatus;
using SupS = DenChannels.Service.SuppressionStatus;
using WFT = DenChannels.Service.WaitForTarget;

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
            DeliveryStatus: DS.RecordedNotClaimedYet,
            ClaimStatus: CS.Unclaimed,
            CompletionStatus: CompS.Pending,
            SuppressionStatus: SupS.NotSuppressed,
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
        var suppressionStatus = status == "suppressed" ? SupS.Suppressed : SupS.NotSuppressed;
        var claimStatus = status switch
        {
            "delivering" or "delivered" or "acknowledged" or "completed" => CS.Claimed,
            "failed" or "expired" or "suppressed" when delivery.AttemptCount > 0 || delivery.LastAttempt is not null => CS.Claimed,
            _ => CS.Unclaimed
        };
        var completionStatus = status switch
        {
            "completed" => CompS.Completed,
            "failed" => CompS.Failed,
            "expired" => CompS.Expired,
            "suppressed" => CompS.Suppressed,
            _ => CompS.Pending
        };
        var deliveryStatus = status switch
        {
            "pending" => DS.Enqueued,
            "delivering" => DS.Claimed,
            "delivered" => DS.Received,
            "acknowledged" => DS.Acknowledged,
            "completed" => DS.Completed,
            "failed" => DS.Failed,
            "expired" => DS.Expired,
            "suppressed" => DS.Suppressed,
            _ => string.IsNullOrEmpty(status) ? DS.RecordedNotClaimedYet : status
        };

        var evidence = deliveryStatus switch
        {
            DS.Enqueued => "Gateway delivery request exists but has not been claimed by the target runtime yet.",
            DS.Claimed => "Gateway delivery request was claimed by the target runtime/adapter.",
            DS.Received => "Target runtime/adapter reported the delivery as delivered/received.",
            DS.Acknowledged => "Target runtime/session acknowledged accepting the delivery.",
            DS.Completed => "Target runtime reported final delivery completion.",
            DS.Suppressed => "Gateway recorded the direct-agent delivery as suppressed.",
            DS.Failed or DS.Expired => "Gateway recorded a terminal delivery failure/expiry.",
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
            WFT.None => true,
            WFT.Claim => observation.ClaimStatus == CS.Claimed || IsTerminal(observation.CompletionStatus) || observation.DeliveryStatus is DS.Claimed or DS.Received or DS.Acknowledged or DS.Completed,
            WFT.Ack => observation.DeliveryStatus is DS.Acknowledged or DS.Completed || IsTerminal(observation.CompletionStatus),
            WFT.Completion => IsTerminal(observation.CompletionStatus),
            _ => false
        };
    }

    public static string NormalizeWaitFor(string? waitFor)
    {
        if (string.IsNullOrWhiteSpace(waitFor))
            return WFT.None;
        return waitFor.Trim().ToLowerInvariant() switch
        {
            "none" => WFT.None,
            "no_wait" => WFT.None,
            "enqueue" => WFT.None,
            "claim" => WFT.Claim,
            "received" => WFT.Claim,
            "ack" => WFT.Ack,
            "acknowledged" => WFT.Ack,
            "complete" => WFT.Completion,
            "completed" => WFT.Completion,
            "completion" => WFT.Completion,
            _ => WFT.Claim
        };
    }

    private static bool IsTerminal(string status) => status is CompS.Completed or CompS.Failed or CompS.Expired or CompS.Suppressed;
}
