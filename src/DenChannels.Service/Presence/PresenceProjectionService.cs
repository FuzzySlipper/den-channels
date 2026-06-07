using DenChannels.Service.Channels;
using DenChannels.Service.Subscriptions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using DenChannels.Service.Configuration;

namespace DenChannels.Service.Presence;

/// <summary>
/// PresenceProjectionService: build a presence read model over
/// memberships + subscriptions + lifecycle/Core evidence.
/// </summary>
public sealed class PresenceProjectionService
{
    private readonly IOptions<DenChannelsOptions> _options;

    public PresenceProjectionService(IOptions<DenChannelsOptions> options)
    {
        _options = options;
    }

    public async Task<ChannelPresenceResponse> GetChannelPresenceAsync(
        long channelId, CancellationToken cancellationToken = default)
    {
        var path = _options.Value.Database.Path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(cancellationToken);
        await using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA foreign_keys = ON;";
        await pragmaCmd.ExecuteNonQueryAsync(cancellationToken);

        // Resolve channel slug
        string channelSlug;
        await using (var slugCmd = connection.CreateCommand())
        {
            slugCmd.CommandText = "SELECT slug FROM channels WHERE id = $channelId;";
            slugCmd.Parameters.AddWithValue("$channelId", channelId);
            var result = await slugCmd.ExecuteScalarAsync(cancellationToken);
            if (result is null)
                return new ChannelPresenceResponse(channelId, $"channel-{channelId}", []);
            channelSlug = (string)result;
        }

        // Project presence: join memberships with their latest subscription state
        var members = new List<PresenceEntryDto>();
        await using var projCmd = connection.CreateCommand();
        projCmd.CommandText = """
            SELECT
                m.channel_id,
                m.member_type,
                m.member_identity,
                m.membership_status,
                m.wake_policy,
                m.profile_identity,
                m.member_role,
                COALESCE(s.sub_count, 0) AS subscription_count,
                COALESCE(s.active_sub_count, 0) AS active_subscription_count,
                s.subscription_statuses,
                s.last_seen_at,
                s.last_claimed_at,
                s.target_project_id,
                s.target_task_id,
                s.assignment_id,
                s.worker_run_id,
                s.worker_role
            FROM channel_memberships m
            LEFT JOIN (
                SELECT
                    channel_id,
                    member_identity,
                    COUNT(*) AS sub_count,
                    SUM(CASE WHEN subscription_status NOT IN ('left','released','quarantined') THEN 1 ELSE 0 END) AS active_sub_count,
                    GROUP_CONCAT(DISTINCT subscription_status) AS subscription_statuses,
                    MAX(last_seen_at) AS last_seen_at,
                    MAX(last_claimed_at) AS last_claimed_at,
                    MAX(target_project_id) AS target_project_id,
                    MAX(target_task_id) AS target_task_id,
                    MAX(assignment_id) AS assignment_id,
                    MAX(worker_run_id) AS worker_run_id,
                    MAX(worker_role) AS worker_role
                FROM channel_subscriptions
                GROUP BY channel_id, member_identity
            ) s ON s.channel_id = m.channel_id AND s.member_identity = m.member_identity
            WHERE m.channel_id = $channelId
            ORDER BY m.membership_status, m.member_type, m.member_identity;
            """;
        projCmd.Parameters.AddWithValue("$channelId", channelId);
        await using var reader = await projCmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var subStatusesStr = reader.IsDBNull(9) ? "" : reader.GetString(9);
            var subStatuses = string.IsNullOrWhiteSpace(subStatusesStr)
                ? Array.Empty<string>()
                : subStatusesStr.Split(',', StringSplitOptions.RemoveEmptyEntries);

            var presenceStatus = DerivePresenceStatus(
                reader.GetString(3),  // membership_status
                reader.GetInt32(8),   // active_subscription_count
                subStatuses);

            members.Add(new PresenceEntryDto(
                ChannelId: reader.GetInt64(0),
                MemberType: reader.GetString(1),
                MemberIdentity: reader.GetString(2),
                MembershipStatus: reader.GetString(3),
                WakePolicy: reader.GetString(4),
                ProfileIdentity: reader.IsDBNull(5) ? null : reader.GetString(5),
                MemberRole: reader.IsDBNull(6) ? null : reader.GetString(6),
                SubscriptionCount: reader.GetInt32(7),
                ActiveSubscriptionCount: reader.GetInt32(8),
                SubscriptionStatuses: subStatuses,
                LastSeenAt: reader.IsDBNull(10) ? null : reader.GetString(10),
                LastClaimedAt: reader.IsDBNull(11) ? null : reader.GetString(11),
                TargetProjectId: reader.IsDBNull(12) ? null : reader.GetString(12),
                TargetTaskId: reader.IsDBNull(13) ? null : reader.GetInt64(13),
                AssignmentId: reader.IsDBNull(14) ? null : reader.GetString(14),
                WorkerRunId: reader.IsDBNull(15) ? null : reader.GetString(15),
                WorkerRole: reader.IsDBNull(16) ? null : reader.GetString(16),
                PresenceStatus: presenceStatus,
                SourceSummary: BuildSourceSummary(reader.GetString(3), reader.GetInt32(7), reader.GetInt32(8), subStatuses)));
        }

        return new ChannelPresenceResponse(channelId, channelSlug, members);
    }

    private static string DerivePresenceStatus(
        string membershipStatus, int activeCount, IReadOnlyList<string> statuses)
    {
        if (membershipStatus is "left" or "banned")
            return membershipStatus;

        if (activeCount == 0)
            return "no_subscription";

        if (statuses.Any(s => string.Equals(s, "busy", StringComparison.OrdinalIgnoreCase)))
            return "busy";

        if (statuses.All(s => s is "degraded" or "offline" or "needs_rebind"))
            return "degraded";

        if (statuses.Contains("idle", StringComparer.OrdinalIgnoreCase))
            return "idle";

        return activeCount > 0 ? "active" : membershipStatus;
    }

    private static string BuildSourceSummary(
        string membershipStatus, int subCount, int activeCount, IReadOnlyList<string> statuses)
    {
        var parts = new List<string> { $"membership:{membershipStatus}" };
        if (subCount > 0)
        {
            parts.Add($"subscriptions:{subCount}");
            parts.Add($"activeSubscriptions:{activeCount}");
        }
        else
        {
            parts.Add("subscriptions:none");
        }
        return string.Join(";", parts);
    }
}
