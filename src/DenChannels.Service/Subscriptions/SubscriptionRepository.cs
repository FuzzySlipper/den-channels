using DenChannels.Service.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace DenChannels.Service.Subscriptions;

/// <summary>
/// SubscriptionRepository: runtime subscription registration, discovery,
/// subscription cursors, and status updates.
/// Extracted from the omnibus ChannelsRepository for module-boundary clarity.
/// </summary>
public sealed class SubscriptionRepository
{
    private readonly IOptions<DenChannelsOptions> _options;

    public SubscriptionRepository(IOptions<DenChannelsOptions> options)
    {
        _options = options;
    }

    // =========================================================================
    // Connection factory
    // =========================================================================

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var path = _options.Value.Database.Path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    // =========================================================================
    // UpsertSubscriptionAsync
    // =========================================================================

    public async Task<ChannelSubscriptionDto> UpsertSubscriptionAsync(long channelId,
        UpsertChannelSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        // Validate purpose vocabulary
        if (!SubscriptionVocabulary.IsAllowedPurpose(request.SubscriptionPurpose))
            throw new ArgumentException(
                $"Invalid subscription_purpose '{request.SubscriptionPurpose}'. " +
                $"Allowed: {string.Join(", ", SubscriptionVocabulary.AllowedPurposes)}",
                nameof(request.SubscriptionPurpose));

        var initialStatus = request.SubscriptionStatus ?? SubscriptionVocabulary.StatusActive;
        if (!SubscriptionVocabulary.IsAllowedStatus(initialStatus))
            throw new ArgumentException(
                $"Invalid subscription_status '{initialStatus}'. " +
                $"Allowed: {string.Join(", ", SubscriptionVocabulary.AllowedStatuses)}",
                nameof(request.SubscriptionStatus));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channel_subscriptions(
                channel_id, membership_id,
                member_type, member_identity, profile_identity,
                agent_instance_id, pool_member_id,
                subscription_identity, subscription_purpose, subscription_status,
                source_project_id, target_project_id, target_task_id,
                assignment_id, worker_run_id, worker_role,
                session_owner_id, session_id,
                wake_policy_override, settings_json)
            VALUES (
                $channelId, $membershipId,
                $memberType, $memberIdentity, $profileIdentity,
                $agentInstanceId, $poolMemberId,
                $subscriptionIdentity, $subscriptionPurpose, $subscriptionStatus,
                $sourceProjectId, $targetProjectId, $targetTaskId,
                $assignmentId, $workerRunId, $workerRole,
                $sessionOwnerId, $sessionId,
                $wakePolicyOverride, $settingsJson)
            ON CONFLICT(channel_id, subscription_identity)
            DO UPDATE SET
                membership_id = COALESCE(excluded.membership_id, channel_subscriptions.membership_id),
                member_type = excluded.member_type,
                member_identity = excluded.member_identity,
                profile_identity = COALESCE(excluded.profile_identity, channel_subscriptions.profile_identity),
                agent_instance_id = COALESCE(excluded.agent_instance_id, channel_subscriptions.agent_instance_id),
                pool_member_id = COALESCE(excluded.pool_member_id, channel_subscriptions.pool_member_id),
                subscription_purpose = excluded.subscription_purpose,
                subscription_status = excluded.subscription_status,
                source_project_id = COALESCE(excluded.source_project_id, channel_subscriptions.source_project_id),
                target_project_id = COALESCE(excluded.target_project_id, channel_subscriptions.target_project_id),
                target_task_id = COALESCE(excluded.target_task_id, channel_subscriptions.target_task_id),
                assignment_id = COALESCE(excluded.assignment_id, channel_subscriptions.assignment_id),
                worker_run_id = COALESCE(excluded.worker_run_id, channel_subscriptions.worker_run_id),
                worker_role = COALESCE(excluded.worker_role, channel_subscriptions.worker_role),
                session_owner_id = COALESCE(excluded.session_owner_id, channel_subscriptions.session_owner_id),
                session_id = COALESCE(excluded.session_id, channel_subscriptions.session_id),
                wake_policy_override = COALESCE(excluded.wake_policy_override, channel_subscriptions.wake_policy_override),
                settings_json = COALESCE(excluded.settings_json, channel_subscriptions.settings_json),
                last_seen_at = datetime('now'),
                updated_at = datetime('now')
            RETURNING id, channel_id, membership_id,
                member_type, member_identity, profile_identity,
                agent_instance_id, pool_member_id,
                subscription_identity, subscription_purpose, subscription_status,
                source_project_id, target_project_id, target_task_id,
                assignment_id, worker_run_id, worker_role,
                session_owner_id, session_id,
                wake_policy_override, last_seen_at, last_claimed_at,
                degraded_reason, settings_json,
                created_at, updated_at;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$membershipId", (object?)request.MembershipId ?? DBNull.Value);
        command.Parameters.AddWithValue("$memberType", request.MemberType);
        command.Parameters.AddWithValue("$memberIdentity", request.MemberIdentity);
        command.Parameters.AddWithValue("$profileIdentity", (object?)request.ProfileIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentInstanceId", (object?)request.AgentInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$poolMemberId", (object?)request.PoolMemberId ?? DBNull.Value);
        command.Parameters.AddWithValue("$subscriptionIdentity", request.SubscriptionIdentity);
        command.Parameters.AddWithValue("$subscriptionPurpose", request.SubscriptionPurpose);
        command.Parameters.AddWithValue("$subscriptionStatus", initialStatus);
        command.Parameters.AddWithValue("$sourceProjectId", (object?)request.SourceProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$targetProjectId", (object?)request.TargetProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$targetTaskId", (object?)request.TargetTaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$assignmentId", (object?)request.AssignmentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$workerRunId", (object?)request.WorkerRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$workerRole", (object?)request.WorkerRole ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionOwnerId", (object?)request.SessionOwnerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionId", (object?)request.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$wakePolicyOverride", (object?)request.WakePolicyOverride ?? DBNull.Value);
        command.Parameters.AddWithValue("$settingsJson", (object?)request.SettingsJson ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadSubscription(reader);
    }

    // =========================================================================
    // ListSubscriptionsByMemberAsync
    // =========================================================================

    public async Task<IReadOnlyList<ChannelSubscriptionDiscoveryDto>> ListSubscriptionsByMemberAsync(
        string memberIdentity, string? subscriptionPurpose = null, string? projectId = null, long? channelId = null,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cs.id, cs.channel_id, c.slug, c.kind, c.project_id,
                   cs.member_type, cs.member_identity, cs.profile_identity,
                   cs.agent_instance_id, cs.pool_member_id,
                   cs.subscription_identity, cs.subscription_purpose, cs.subscription_status,
                   cs.target_project_id, cs.target_task_id,
                   cs.assignment_id, cs.worker_run_id, cs.worker_role,
                   cs.created_at, cs.updated_at
            FROM channel_subscriptions cs
            JOIN channels c ON c.id = cs.channel_id
            WHERE cs.member_identity = $memberIdentity
              AND cs.subscription_status NOT IN ('left', 'released', 'quarantined')
              AND ($purpose IS NULL OR cs.subscription_purpose = $purpose)
              AND ($projectId IS NULL OR c.project_id = $projectId)
              AND ($channelId IS NULL OR cs.channel_id = $channelId)
            ORDER BY c.id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$memberIdentity", memberIdentity.Trim());
        command.Parameters.AddWithValue("$purpose", (object?)subscriptionPurpose ?? DBNull.Value);
        command.Parameters.AddWithValue("$projectId", (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$channelId", (object?)channelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<ChannelSubscriptionDiscoveryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadSubscriptionDiscovery(reader));
        return rows;
    }

    // =========================================================================
    // UpsertSubscriptionCursorAsync
    // =========================================================================

    public async Task<ChannelSubscriptionCursorDto> UpsertSubscriptionCursorAsync(
        long subscriptionId, UpsertSubscriptionCursorRequest request,
        CancellationToken cancellationToken = default)
    {
        // Validate stream kind vocabulary
        if (!SubscriptionVocabulary.IsAllowedStreamKind(request.StreamKind))
            throw new ArgumentException(
                $"Invalid stream_kind '{request.StreamKind}'. " +
                $"Allowed: {string.Join(", ", SubscriptionVocabulary.AllowedStreamKinds)}",
                nameof(request.StreamKind));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channel_subscription_cursors(subscription_id, stream_kind, last_seen_id, cursor_json)
            VALUES ($subscriptionId, $streamKind, $lastSeenId, $cursorJson)
            ON CONFLICT(subscription_id, stream_kind)
            DO UPDATE SET
                last_seen_id = excluded.last_seen_id,
                cursor_json = COALESCE(excluded.cursor_json, channel_subscription_cursors.cursor_json),
                last_seen_at = datetime('now'),
                updated_at = datetime('now')
            RETURNING id, subscription_id, stream_kind, last_seen_id, last_seen_at, cursor_json, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("$subscriptionId", subscriptionId);
        command.Parameters.AddWithValue("$streamKind", request.StreamKind);
        command.Parameters.AddWithValue("$lastSeenId", request.LastSeenId);
        command.Parameters.AddWithValue("$cursorJson", (object?)request.CursorJson ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadSubscriptionCursor(reader);
    }

    // =========================================================================
    // ListSubscriptionCursorsAsync
    // =========================================================================

    public async Task<IReadOnlyList<ChannelSubscriptionCursorDto>> ListSubscriptionCursorsAsync(
        long subscriptionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, subscription_id, stream_kind, last_seen_id, last_seen_at, cursor_json, created_at, updated_at
            FROM channel_subscription_cursors
            WHERE subscription_id = $subscriptionId
            ORDER BY stream_kind;
            """;
        command.Parameters.AddWithValue("$subscriptionId", subscriptionId);
        var rows = new List<ChannelSubscriptionCursorDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadSubscriptionCursor(reader));
        return rows;
    }

    // ── Private readers ─────────────────────────────────────────────────

    private static ChannelSubscriptionDto ReadSubscription(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), GetNullableInt64(reader, 2),
        reader.GetString(3), reader.GetString(4), GetNullableString(reader, 5),
        GetNullableString(reader, 6), GetNullableString(reader, 7),
        reader.GetString(8), reader.GetString(9), reader.GetString(10),
        GetNullableString(reader, 11), GetNullableString(reader, 12), GetNullableInt64(reader, 13),
        GetNullableString(reader, 14), GetNullableString(reader, 15), GetNullableString(reader, 16),
        GetNullableString(reader, 17), GetNullableString(reader, 18), GetNullableString(reader, 19),
        GetNullableString(reader, 20), GetNullableString(reader, 21), GetNullableString(reader, 22),
        GetNullableString(reader, 23), reader.GetString(24), reader.GetString(25));

    private static ChannelSubscriptionDiscoveryDto ReadSubscriptionDiscovery(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
        GetNullableString(reader, 4), reader.GetString(5), reader.GetString(6),
        GetNullableString(reader, 7), GetNullableString(reader, 8), GetNullableString(reader, 9),
        reader.GetString(10), reader.GetString(11), reader.GetString(12),
        GetNullableString(reader, 13), GetNullableInt64(reader, 14), GetNullableString(reader, 15),
        GetNullableString(reader, 16), GetNullableString(reader, 17),
        reader.GetString(18), reader.GetString(19));

    private static ChannelSubscriptionCursorDto ReadSubscriptionCursor(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
        GetNullableString(reader, 4), GetNullableString(reader, 5),
        reader.GetString(6), reader.GetString(7));

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
