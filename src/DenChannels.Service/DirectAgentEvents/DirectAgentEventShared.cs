using System.Text.Json;
using DenChannels.Service.Channels;

namespace DenChannels.Service.DirectAgentEvents;

/// <summary>
/// Shared internal helpers for direct-agent event routes.
/// Used by direct-agent readback and legacy evidence helpers. The direct-agent
/// wake write routes are retired; new executable wakes belong to Delivery.
/// </summary>
internal static class DirectAgentEventShared
{
    internal const string DeliveryIntentReplacement = "POST /v1/delivery/intents";

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

    internal static IResult RetiredWakeWriteTombstone(string retiredRoute)
    {
        return Results.Json(new
        {
            code = "route_gone",
            message = "This legacy direct-agent wake write route has been retired. Use Delivery for executable wake intents and Conversation for human-facing transcript evidence.",
            retiredRoute,
            replacement = DeliveryIntentReplacement,
            legacyReadback = "GET /api/direct-agent-events/{eventId}"
        }, statusCode: StatusCodes.Status410Gone);
    }
}
