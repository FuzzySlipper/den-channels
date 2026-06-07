using System.Text.Json;
using DenChannels.Service.Channels;
using DenChannels.Service.Subscriptions;

using static DenChannels.Service.MessageKind;
using static DenChannels.Service.SourceKind;
using CS = DenChannels.Service.ClaimStatus;
using CompS = DenChannels.Service.CompletionStatus;
using SupS = DenChannels.Service.SuppressionStatus;

namespace DenChannels.Service.DirectAgentEvents;

/// <summary>
/// Shared internal helpers for direct-agent event routes.
/// Used by DirectAgentEventRoutes (canonical). The legacy GatewayRoutes
/// compatibility aliases were retired (task #2022); this shared code
/// remains in use by the canonical route only.
/// </summary>
internal static class DirectAgentEventShared
{
    // ── Channel resolution ─────────────────────────────────────────────

    /// <summary>
    /// Resolve a channel from channelId (direct) or projectId (default channel lookup
    /// with linked-channels fallback).
    /// </summary>
    internal static async Task<ChannelDto?> ResolveChannelAsync(
        ChannelRepository repository,
        ChannelProjectLinkRepository projectLinks,
        long? channelId,
        string? projectId,
        CancellationToken cancellationToken)
    {
        if (channelId is not null)
            return await repository.GetChannelAsync(channelId.Value, cancellationToken);

        var channels = await repository.ListChannelsAsync(projectId, "project_default", 1, cancellationToken);
        if (channels.Count > 0)
            return channels[0];

        // Fallback: check channel-project links for shared operations channels
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            var linkedChannels = await projectLinks.GetLinkedChannelsForProjectAsync(projectId, cancellationToken);
            if (linkedChannels.Count > 0)
                return linkedChannels[0];
        }

        return null;
    }

    // ── Subscription lookup ─────────────────────────────────────────────

    internal sealed record DirectAgentSubscriptionState(
        string DeliveryStatus,
        string ClaimStatus,
        string CompletionStatus,
        int ActiveSubscriptionCount,
        IReadOnlyList<string> SubscriptionStatuses,
        IReadOnlyList<string> SubscriptionIdentities);

    internal static async Task<DirectAgentSubscriptionState> ResolveSubscriptionStateAsync(
        SubscriptionRepository subscriptionRepo,
        long channelId,
        string memberIdentity,
        CancellationToken cancellationToken)
    {
        var subscriptions = await subscriptionRepo.ListSubscriptionsByMemberAsync(
            memberIdentity, subscriptionPurpose: null, projectId: null, channelId: channelId,
            limit: 100, cancellationToken);

        var statuses = subscriptions
            .Select(s => s.SubscriptionStatus)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var identities = subscriptions
            .Select(s => s.SubscriptionIdentity)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (subscriptions.Count == 0)
        {
            return new DirectAgentSubscriptionState(
                "recorded_no_subscriber", "no_subscriber", CompS.Pending,
                0, statuses, identities);
        }

        if (statuses.Any(s => string.Equals(s, "busy", StringComparison.OrdinalIgnoreCase)))
        {
            return new DirectAgentSubscriptionState(
                "claimed", CS.Claimed, CompS.Pending,
                subscriptions.Count, statuses, identities);
        }

        if (statuses.All(s => s is "degraded" or "offline" or "needs_rebind"))
        {
            return new DirectAgentSubscriptionState(
                "recorded_unreachable_subscription", "subscription_unreachable", CompS.Failed,
                subscriptions.Count, statuses, identities);
        }

        return new DirectAgentSubscriptionState(
            "recorded_pending_claim", CS.Unclaimed, CompS.Pending,
            subscriptions.Count, statuses, identities);
    }

    // ── Metadata payload construction ──────────────────────────────────

    /// <summary>
    /// Build the shared metadata dictionary for a direct-agent wake event,
    /// including target-work attribution and session-owner fields.
    /// </summary>
    internal static Dictionary<string, object?> BuildWakeMetadata(
        string requestId,
        string targetMemberIdentity,
        string targetMemberType,
        string wakePolicy,
        string? resolvedSourceProjectId,
        string? sourceProjectId,
        string? targetProjectId,
        long? targetTaskId,
        string? assignmentId,
        string? workerRunId,
        string? workerRole,
        string? profileIdentity,
        string? poolMemberId,
        string? agentInstanceId,
        string? sessionOwnerId,
        string? sessionId,
        string gatewayEventsUrl,
        DirectAgentSubscriptionState subscriptionState)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["requestId"] = requestId,
            ["targetMemberIdentity"] = targetMemberIdentity,
            ["targetMemberType"] = targetMemberType,
            ["wakePolicy"] = wakePolicy,
            ["deliveryMode"] = "direct_agent_message",
            ["deliveryStatus"] = subscriptionState.DeliveryStatus,
            ["claimStatus"] = subscriptionState.ClaimStatus,
            ["completionStatus"] = subscriptionState.CompletionStatus,
            ["activeSubscriptionCount"] = subscriptionState.ActiveSubscriptionCount,
            ["subscriptionStatuses"] = subscriptionState.SubscriptionStatuses,
            ["subscriptionIdentities"] = subscriptionState.SubscriptionIdentities,
            ["suppressionStatus"] = SupS.NotSuppressed,
            ["evidence"] = new
            {
                gatewayEventsUrl,
                subscriptionSource = "channel_subscriptions",
                subscriptionCursorSource = "channel_subscription_cursors"
            }
        };

        if (sourceProjectId is not null)
            metadata["sourceProjectId"] = sourceProjectId;
        if (targetProjectId is not null)
            metadata["targetProjectId"] = targetProjectId;
        if (targetTaskId is not null)
            metadata["targetTaskId"] = targetTaskId;
        if (assignmentId is not null)
            metadata["assignmentId"] = assignmentId;
        if (workerRunId is not null)
            metadata["workerRunId"] = workerRunId;
        if (workerRole is not null)
            metadata["workerRole"] = workerRole;
        if (profileIdentity is not null)
            metadata["profileIdentity"] = profileIdentity;
        if (poolMemberId is not null)
            metadata["poolMemberId"] = poolMemberId;
        if (agentInstanceId is not null)
            metadata["agentInstanceId"] = agentInstanceId;
        if (sessionOwnerId is not null)
            metadata["sessionOwnerId"] = sessionOwnerId;
        if (sessionId is not null)
            metadata["sessionId"] = sessionId;

        return metadata;
    }

    // ── Message posting ────────────────────────────────────────────────

    /// <summary>
    /// Post the durable wake_event message to the channel.
    /// Returns the created ChannelMessageDto.
    /// </summary>
    internal static async Task<ChannelMessageDto> PostWakeMessageAsync(
        ChannelRepository repository,
        long channelId,
        string senderIdentity,
        string body,
        string requestId,
        string? resolvedSourceProjectId,
        string? targetProjectId,
        long? targetTaskId,
        string? workerRunId,
        string? workerRole,
        string? profileIdentity,
        string? poolMemberId,
        string? agentInstanceId,
        string? sessionOwnerId,
        string? sessionId,
        string? assignmentId,
        string? checkpointType,
        string? checkpointHandle,
        string memberIdentity,
        string metadataJson,
        CancellationToken cancellationToken)
    {
        return await repository.PostMessageAsync(channelId, new PostChannelMessageRequest(
            SenderType: "user",
            SenderIdentity: senderIdentity.Trim(),
            Body: body.Trim(),
            MessageKind: HumanText,
            SourceKind: WakeEvent,
            SourceId: requestId,
            SourceProjectId: resolvedSourceProjectId,
            TargetProjectId: targetProjectId,
            TargetTaskId: targetTaskId,
            WorkerRunId: workerRunId,
            WorkerRole: workerRole,
            ProfileIdentity: profileIdentity,
            PoolMemberId: poolMemberId,
            AgentInstanceId: agentInstanceId,
            SessionOwnerId: sessionOwnerId,
            SessionId: sessionId,
            Summary: $"Direct agent request to {memberIdentity}: recorded, pending claim/completion",
            DeepLink: null,
            ThreadRootMessageId: null,
            ReplyToMessageId: null,
            MetadataJson: metadataJson,
            DeliveryRequestId: null,
            DedupeKey: null,
            AssignmentId: assignmentId,
            CheckpointType: checkpointType,
            CheckpointHandle: checkpointHandle), cancellationToken);
    }

    // ── JSON helpers ───────────────────────────────────────────────────

    internal static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>
    /// Extract direct-agent tracking fields from the wake_event metadata JSON.
    /// Returns defaults if metadata is missing or malformed.
    /// </summary>
    internal static (string? deliveryStatus, string? claimStatus, string? completionStatus, string? wakePolicy)
        ExtractDirectAgentMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return (null, null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            return (
                TryGetString(root, "deliveryStatus"),
                TryGetString(root, "claimStatus"),
                TryGetString(root, "completionStatus"),
                TryGetString(root, "wakePolicy"));
        }
        catch (JsonException)
        {
            return (null, null, null, null);
        }
    }

    /// <summary>
    /// Extract member identity from the sourceId pattern: direct-agent-message:{channelId}:{memberIdentity}:{guid}
    /// </summary>
    internal static string? ExtractMemberIdentity(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || !sourceId.StartsWith("direct-agent-message:", StringComparison.Ordinal))
            return null;

        var parts = sourceId.Split(':');
        if (parts.Length >= 3)
            return Uri.UnescapeDataString(parts[2]);

        return null;
    }
}
