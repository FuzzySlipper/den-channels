using System.Text.Json;
using DenChannels.Service.Channels;

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
        ChannelsRepository repository,
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
            var linkedChannels = await repository.GetLinkedChannelsForProjectAsync(projectId, cancellationToken);
            if (linkedChannels.Count > 0)
                return linkedChannels[0];
        }

        return null;
    }

    // ── Member lookup ──────────────────────────────────────────────────

    /// <summary>
    /// Find an active agent member by identity within a channel.
    /// Returns null if no matching active agent membership exists.
    /// </summary>
    internal static async Task<ChannelMembershipDto?> FindActiveAgentMemberAsync(
        ChannelsRepository repository,
        long channelId,
        string memberIdentity,
        CancellationToken cancellationToken)
    {
        var members = await repository.ListMembershipsAsync(channelId, 200, cancellationToken);
        return members.FirstOrDefault(m =>
            string.Equals(m.MemberIdentity, memberIdentity.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.MemberType, "agent", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.MembershipStatus, "active", StringComparison.OrdinalIgnoreCase));
    }

    // ── Metadata payload construction ──────────────────────────────────

    /// <summary>
    /// Build the shared metadata dictionary for a direct-agent wake event,
    /// including target-work attribution and session-owner fields.
    /// </summary>
    internal static Dictionary<string, object?> BuildWakeMetadata(
        string requestId,
        ChannelMembershipDto member,
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
        string gatewayEventsUrl)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["requestId"] = requestId,
            ["targetMemberIdentity"] = member.MemberIdentity,
            ["targetMemberType"] = member.MemberType,
            ["wakePolicy"] = member.WakePolicy,
            ["deliveryMode"] = "direct_agent_message",
            ["deliveryStatus"] = "recorded_pending_claim",
            ["claimStatus"] = CS.Unclaimed,
            ["completionStatus"] = CompS.Pending,
            ["suppressionStatus"] = SupS.NotSuppressed,
            ["evidence"] = new { gatewayEventsUrl }
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
        ChannelsRepository repository,
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
