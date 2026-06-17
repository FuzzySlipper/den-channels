using DenChannels.Service.Configuration;
using DenChannels.Service.DirectAgentEvents;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace DenChannels.Service.Channels;

public sealed partial class ChannelsRepository
{
    private readonly IOptions<DenChannelsOptions> _options;

    public ChannelsRepository(IOptions<DenChannelsOptions> options)
    {
        _options = options;
    }

    public async Task<ChannelDto> CreateChannelAsync(CreateChannelRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channels(slug, display_name, kind, project_id, space_id, created_by, visibility, settings_json)
            VALUES ($slug, $displayName, $kind, $projectId, $spaceId, $createdBy, $visibility, $settingsJson)
            RETURNING id, slug, display_name, kind, project_id, space_id, created_by, visibility, settings_json, created_at, updated_at, archived_at;
            """;
        AddChannelParameters(command, request);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadChannel(reader);
    }

    public async Task<ChannelDto> EnsureProjectDefaultChannelAsync(string projectId,
        EnsureProjectDefaultChannelRequest? request = null, CancellationToken cancellationToken = default)
    {
        var slug = $"project-{projectId}";
        var displayName = request?.DisplayName ?? projectId;
        var createdBy = request?.CreatedBy ?? "system";

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channels(slug, display_name, kind, project_id, created_by, visibility, settings_json)
            VALUES ($slug, $displayName, 'project_default', $projectId, $createdBy, 'normal', $settingsJson)
            ON CONFLICT(project_id) WHERE project_id IS NOT NULL AND kind = 'project_default'
            DO UPDATE SET
                display_name = excluded.display_name,
                settings_json = COALESCE(excluded.settings_json, channels.settings_json),
                updated_at = datetime('now')
            RETURNING id, slug, display_name, kind, project_id, space_id, created_by, visibility, settings_json, created_at, updated_at, archived_at;
            """;
        command.Parameters.AddWithValue("$slug", slug);
        command.Parameters.AddWithValue("$displayName", displayName);
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$createdBy", createdBy);
        command.Parameters.AddWithValue("$settingsJson", (object?)request?.SettingsJson ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadChannel(reader);
    }

    public async Task<ChannelDto> EnsureAgentCommonsChannelAsync(CancellationToken cancellationToken = default)
    {
        const string settingsJson = "{\"systemManaged\":true,\"channelRole\":\"agent_commons\",\"defaultWakePolicy\":\"mentions_only\"}";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channels(slug, display_name, kind, created_by, visibility, settings_json)
            VALUES ('agent-commons', 'Agent Commons', 'system', 'system', 'normal', $settingsJson)
            ON CONFLICT(slug) DO UPDATE SET
                display_name = 'Agent Commons',
                kind = 'system',
                visibility = 'normal',
                settings_json = COALESCE(channels.settings_json, excluded.settings_json),
                updated_at = datetime('now')
            RETURNING id, slug, display_name, kind, project_id, space_id, created_by, visibility, settings_json, created_at, updated_at, archived_at;
            """;
        command.Parameters.AddWithValue("$settingsJson", settingsJson);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadChannel(reader);
    }

    public async Task<IReadOnlyList<ChannelDto>> ListChannelsAsync(string? projectId = null, string? kind = null,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, slug, display_name, kind, project_id, space_id, created_by, visibility, settings_json, created_at, updated_at, archived_at
            FROM channels
            WHERE ($projectId IS NULL OR project_id = $projectId)
              AND ($kind IS NULL OR kind = $kind)
            ORDER BY updated_at DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$projectId", (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<ChannelDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadChannel(reader));
        return rows;
    }

    public async Task<ChannelDto?> GetChannelAsync(long channelId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, slug, display_name, kind, project_id, space_id, created_by, visibility, settings_json, created_at, updated_at, archived_at
            FROM channels
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", channelId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadChannel(reader) : null;
    }

    public async Task<ChannelMessageDto> PostMessageAsync(long channelId, PostChannelMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channel_messages(
                channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id, source_project_id,
                target_project_id, target_task_id, worker_run_id, worker_role, profile_identity,
                summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, delivery_request_id, dedupe_key,
                assignment_id, checkpoint_type, checkpoint_handle,
                agent_instance_id, pool_member_id,
                session_owner_id, session_id)
            VALUES (
                $channelId, $senderType, $senderIdentity, $body, $messageKind, $sourceKind, $sourceId, $sourceProjectId,
                $targetProjectId, $targetTaskId, $workerRunId, $workerRole, $profileIdentity,
                $summary, $deepLink, $threadRootMessageId, $replyToMessageId, $metadataJson, $deliveryRequestId, $dedupeKey,
                $assignmentId, $checkpointType, $checkpointHandle,
                $agentInstanceId, $poolMemberId,
                $sessionOwnerId, $sessionId)
            RETURNING id, channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id, source_project_id,
                summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, delivery_request_id, dedupe_key,
                assignment_id, checkpoint_type, checkpoint_handle,
                agent_instance_id, pool_member_id,
                session_owner_id, session_id,
                target_project_id, target_task_id, worker_run_id, worker_role, profile_identity,
                created_at, edited_at, deleted_at;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$senderType", request.SenderType);
        command.Parameters.AddWithValue("$senderIdentity", request.SenderIdentity);
        command.Parameters.AddWithValue("$body", request.Body);
        command.Parameters.AddWithValue("$messageKind", request.MessageKind ?? DefaultMessageKind(request.SenderType));
        command.Parameters.AddWithValue("$sourceKind", (object?)request.SourceKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceId", (object?)request.SourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceProjectId", (object?)request.SourceProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$summary", (object?)request.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$deepLink", (object?)request.DeepLink ?? DBNull.Value);
        command.Parameters.AddWithValue("$threadRootMessageId", (object?)request.ThreadRootMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$replyToMessageId", (object?)request.ReplyToMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadataJson", (object?)request.MetadataJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$deliveryRequestId", (object?)DeriveMessageDeliveryRequestId(request) ?? DBNull.Value);
        command.Parameters.AddWithValue("$dedupeKey", (object?)request.DedupeKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$assignmentId", (object?)request.AssignmentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$checkpointType", (object?)request.CheckpointType ?? DBNull.Value);
        command.Parameters.AddWithValue("$checkpointHandle", (object?)request.CheckpointHandle ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentInstanceId", (object?)request.AgentInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$poolMemberId", (object?)request.PoolMemberId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionOwnerId", (object?)request.SessionOwnerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionId", (object?)request.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$targetProjectId", (object?)request.TargetProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$targetTaskId", (object?)request.TargetTaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$workerRunId", (object?)request.WorkerRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$workerRole", (object?)request.WorkerRole ?? DBNull.Value);
        command.Parameters.AddWithValue("$profileIdentity", (object?)request.ProfileIdentity ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadMessage(reader);
    }

    public async Task<IReadOnlyList<ChannelMessageDto>> ListMessagesAsync(long channelId, long? afterId = null,
        string? assignmentId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // Build SQL with optional assignment filter
        var selectColumns = """"
            SELECT id, channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id, source_project_id,
                summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, delivery_request_id, dedupe_key,
                assignment_id, checkpoint_type, checkpoint_handle, agent_instance_id, pool_member_id,
                session_owner_id, session_id,
                target_project_id, target_task_id, worker_run_id, worker_role, profile_identity, created_at, edited_at, deleted_at
            """";

        if (afterId is null)
        {
            command.CommandText = $"""
                {selectColumns}
                FROM (
                    {selectColumns}
                    FROM channel_messages
                    WHERE channel_id = $channelId
                      AND deleted_at IS NULL
                      AND ($assignmentId IS NULL OR assignment_id = $assignmentId)
                    ORDER BY id DESC
                    LIMIT $limit
                ) AS latest_messages
                ORDER BY id ASC;
                """;
        }
        else
        {
            command.CommandText = $"""
                {selectColumns}
                FROM channel_messages
                WHERE channel_id = $channelId
                  AND id > $afterId
                  AND deleted_at IS NULL
                  AND ($assignmentId IS NULL OR assignment_id = $assignmentId)
                ORDER BY id ASC
                LIMIT $limit;
                """;
        }

        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$assignmentId", (object?)assignmentId ?? DBNull.Value);
        if (afterId is not null)
            command.Parameters.AddWithValue("$afterId", afterId.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<ChannelMessageDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadMessage(reader));
        return rows;
    }

    public async Task<ChannelMessageDto?> GetMessageAsync(long messageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id, source_project_id,
                summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, delivery_request_id, dedupe_key,
                assignment_id, checkpoint_type, checkpoint_handle, agent_instance_id, pool_member_id,
                session_owner_id, session_id,
                target_project_id, target_task_id, worker_run_id, worker_role, profile_identity, created_at, edited_at, deleted_at
            FROM channel_messages
            WHERE id = $messageId
              AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMessage(reader) : null;
    }

    public async Task<IReadOnlyList<ChannelMessageDto>> ListMessagesBySourceAsync(string sourceKind, string sourceId,
        string? sourceProjectId = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id, source_project_id,
                summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, delivery_request_id, dedupe_key,
                assignment_id, checkpoint_type, checkpoint_handle, agent_instance_id, pool_member_id,
                session_owner_id, session_id,
                target_project_id, target_task_id, worker_run_id, worker_role, profile_identity, created_at, edited_at, deleted_at
            FROM channel_messages
            WHERE source_kind = $sourceKind
              AND source_id = $sourceId
              AND ($sourceProjectId IS NULL OR source_project_id = $sourceProjectId)
              AND deleted_at IS NULL
            ORDER BY id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$sourceKind", sourceKind);
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$sourceProjectId", (object?)sourceProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<ChannelMessageDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadMessage(reader));
        return rows;
    }

    /// <summary>
    /// Search channel messages across all channels using FTS5 full-text search.
    /// Supports filters for channel, sender, project, time range, and message kind.
    /// Results ordered by FTS5 relevance (rank) or creation recency.
    /// </summary>
    public async Task<SearchMessagesResponse> SearchMessagesAsync(
        string? query = null,
        long? channelId = null,
        string? senderIdentity = null,
        string? projectId = null,
        bool nonProjectOnly = false,
        string? messageKind = null,
        DateTime? createdAfter = null,
        DateTime? createdBefore = null,
        string? orderBy = null,
        int offset = 0,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);
        var useRelevance = !string.Equals(orderBy, "recency", StringComparison.OrdinalIgnoreCase);
        var hasFtsQuery = !string.IsNullOrWhiteSpace(query);
        var safeQuery = hasFtsQuery ? SanitizeFts5Query(query!) : null;

        await using var connection = await OpenConnectionAsync(cancellationToken);

        // Build the search query
        var fromClause = hasFtsQuery
            ? "FROM channel_messages_fts JOIN channel_messages ON channel_messages.id = channel_messages_fts.rowid"
            : "FROM channel_messages";
        var joinChannels = " JOIN channels ON channels.id = channel_messages.channel_id";

        var conditions = new List<string>();
        var cmd = connection.CreateCommand();

        conditions.Add("channel_messages.deleted_at IS NULL");

        if (hasFtsQuery)
        {
            conditions.Add("channel_messages_fts MATCH $query");
            cmd.Parameters.AddWithValue("$query", safeQuery);
        }

        if (channelId.HasValue)
        {
            conditions.Add("channel_messages.channel_id = $channelId");
            cmd.Parameters.AddWithValue("$channelId", channelId.Value);
        }

        if (!string.IsNullOrWhiteSpace(senderIdentity))
        {
            conditions.Add("channel_messages.sender_identity = $senderIdentity");
            cmd.Parameters.AddWithValue("$senderIdentity", senderIdentity.Trim());
        }

        if (nonProjectOnly)
        {
            conditions.Add("channels.project_id IS NULL");
        }
        else if (!string.IsNullOrWhiteSpace(projectId))
        {
            conditions.Add("channels.project_id = $projectId");
            cmd.Parameters.AddWithValue("$projectId", projectId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(messageKind))
        {
            conditions.Add("channel_messages.message_kind = $messageKind");
            cmd.Parameters.AddWithValue("$messageKind", messageKind.Trim());
        }

        if (createdAfter.HasValue)
        {
            conditions.Add("channel_messages.created_at >= $createdAfter");
            cmd.Parameters.AddWithValue("$createdAfter", createdAfter.Value.ToString("o"));
        }

        if (createdBefore.HasValue)
        {
            conditions.Add("channel_messages.created_at <= $createdBefore");
            cmd.Parameters.AddWithValue("$createdBefore", createdBefore.Value.ToString("o"));
        }

        var whereClause = string.Join(" AND ", conditions);

        // Count total matching rows
        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) {fromClause} {joinChannels} WHERE {whereClause};";
        foreach (SqliteParameter p in cmd.Parameters)
            countCmd.Parameters.AddWithValue(p.ParameterName, p.Value);
        var totalCount = (long)(await countCmd.ExecuteScalarAsync(cancellationToken))!;

        // Build the main query with ordering and pagination
        const string messageColumns = """
            channel_messages.id,
            channel_messages.channel_id,
            channels.slug,
            channels.display_name,
            channels.project_id,
            channel_messages.sender_type,
            channel_messages.sender_identity,
            channel_messages.body,
            channel_messages.message_kind,
            channel_messages.source_kind,
            channel_messages.source_id,
            channel_messages.source_project_id,
            channel_messages.target_project_id,
            channel_messages.target_task_id,
            channel_messages.worker_run_id,
            channel_messages.worker_role,
            channel_messages.profile_identity,
            channel_messages.summary,
            channel_messages.deep_link,
            channel_messages.thread_root_message_id,
            channel_messages.reply_to_message_id,
            channel_messages.metadata_json,
            channel_messages.created_at,
            channel_messages.edited_at,
            channel_messages.deleted_at
            """;

        var orderClause = useRelevance && hasFtsQuery
            ? "ORDER BY rank"
            : "ORDER BY channel_messages.created_at DESC, channel_messages.id DESC";

        cmd.CommandText = $"""
            SELECT {messageColumns}
            {fromClause}
            {joinChannels}
            WHERE {whereClause}
            {orderClause}
            LIMIT $limit OFFSET $offset;
            """;

        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var items = new List<SearchableChannelMessageDto>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(ReadSearchableMessage(reader));

        return new SearchMessagesResponse(
            Items: items,
            TotalCount: (int)totalCount,
            Offset: offset,
            Limit: limit,
            Query: hasFtsQuery ? query : null);
    }

    /// <summary>
    /// Sanitize user input for safe FTS5 MATCH usage. Removes query-breaking syntax
    /// while preserving meaningful search terms. Escapes double-quote characters.
    /// </summary>
    internal static string SanitizeFts5Query(string rawQuery)
    {
        // Strip FTS5 special characters that aren't part of a user search
        var cleaned = System.Text.RegularExpressions.Regex.Replace(rawQuery, @"[*^]", "");
        // Escape double-quotes (used for phrase queries in FTS5)
        cleaned = cleaned.Replace("\"", "\"\"");
        // Trim whitespace and ensure non-empty
        var trimmed = cleaned.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "\"\"" : trimmed;
    }

    private static SearchableChannelMessageDto ReadSearchableMessage(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        GetNullableString(reader, 9),
        GetNullableString(reader, 10),
        GetNullableString(reader, 11),
        GetNullableString(reader, 12),
        GetNullableInt64(reader, 13),
        GetNullableString(reader, 14),
        GetNullableString(reader, 15),
        GetNullableString(reader, 16),
        GetNullableString(reader, 17),
        GetNullableString(reader, 18),
        GetNullableInt64(reader, 19),
        GetNullableInt64(reader, 20),
        GetNullableString(reader, 21),
        reader.GetString(22),
        GetNullableString(reader, 23),
        GetNullableString(reader, 24));

    public async Task<IReadOnlyList<ChannelMembershipDto>> ListMembershipsAsync(long channelId, int limit = 200,
        CancellationToken cancellationToken = default, bool includeLeft = true, int? leftGraceMinutes = null)
    {
        limit = Math.Clamp(limit, 1, 500);
        var clampedLeftGraceMinutes = leftGraceMinutes.HasValue ? Math.Clamp(leftGraceMinutes.Value, 0, 10080) : (int?)null;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """"
            SELECT id, channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json, membership_purpose, created_at, updated_at
            FROM channel_memberships
            WHERE channel_id = $channelId
              AND (
                    membership_status != 'left'
                    OR ($includeLeft = 1 AND $leftGraceMinutes IS NULL)
                    OR ($includeLeft = 1 AND $leftGraceMinutes IS NOT NULL AND updated_at >= datetime('now', '-' || $leftGraceMinutes || ' minutes'))
                  )
            ORDER BY id ASC
            LIMIT $limit;
            """";
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$includeLeft", includeLeft ? 1 : 0);
        command.Parameters.AddWithValue("$leftGraceMinutes", (object?)clampedLeftGraceMinutes ?? DBNull.Value);
        var rows = new List<ChannelMembershipDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadMembership(reader));
        return rows;
    }

    public async Task<ChannelMessageDto?> GetMessageByDedupeKeyAsync(long channelId, string dedupeKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id, source_project_id,
                summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, delivery_request_id, dedupe_key,
                assignment_id, checkpoint_type, checkpoint_handle, agent_instance_id, pool_member_id,
                session_owner_id, session_id,
                target_project_id, target_task_id, worker_run_id, worker_role, profile_identity, created_at, edited_at, deleted_at
            FROM channel_messages
            WHERE channel_id = $channelId
              AND dedupe_key = $dedupeKey
              AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$dedupeKey", dedupeKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMessage(reader) : null;
    }

    public async Task<ChannelMembershipDto> UpsertMembershipAsync(long channelId, UpsertChannelMembershipRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """"
            INSERT INTO channel_memberships(
                channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json, membership_purpose)
            VALUES ($channelId, $memberType, $memberIdentity, $membershipStatus, $wakePolicy, $canSend, $canReact, $canInvite,
                $cooldownSeconds, $maxAutoRepliesPerWindow, $settingsJson, $membershipPurpose)
            ON CONFLICT(channel_id, member_type, member_identity)
            DO UPDATE SET
                membership_status = excluded.membership_status,
                wake_policy = excluded.wake_policy,
                can_send = excluded.can_send,
                can_react = excluded.can_react,
                can_invite = excluded.can_invite,
                cooldown_seconds = excluded.cooldown_seconds,
                max_auto_replies_per_window = excluded.max_auto_replies_per_window,
                settings_json = COALESCE(excluded.settings_json, channel_memberships.settings_json),
                membership_purpose = CASE
                    WHEN $membershipPurpose IS NOT NULL THEN $membershipPurpose
                    ELSE channel_memberships.membership_purpose
                END,
                updated_at = datetime('now')
            RETURNING id, channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json, membership_purpose, created_at, updated_at;
            """";
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$memberType", request.MemberType);
        command.Parameters.AddWithValue("$memberIdentity", request.MemberIdentity);
        command.Parameters.AddWithValue("$membershipStatus", request.MembershipStatus ?? "active");
        command.Parameters.AddWithValue("$wakePolicy", request.WakePolicy ?? "mentions_only");
        command.Parameters.AddWithValue("$canSend", request.CanSend ?? true);
        command.Parameters.AddWithValue("$canReact", request.CanReact ?? true);
        command.Parameters.AddWithValue("$canInvite", request.CanInvite ?? false);
        command.Parameters.AddWithValue("$cooldownSeconds", request.CooldownSeconds ?? 60);
        command.Parameters.AddWithValue("$maxAutoRepliesPerWindow", request.MaxAutoRepliesPerWindow ?? 1);
        command.Parameters.AddWithValue("$settingsJson", (object?)request.SettingsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$membershipPurpose", (object?)request.MembershipPurpose ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var membership = ReadMembership(reader);
        await reader.DisposeAsync();
        if (ShouldAutoEnsureAgentCommonsMembership(channelId, request, membership))
        {
            await EnsureAgentCommonsMembershipAsync(membership.MemberIdentity, null, cancellationToken);
        }
        return membership;
    }

    private static bool ShouldAutoEnsureAgentCommonsMembership(long sourceChannelId, UpsertChannelMembershipRequest request, ChannelMembershipDto membership)
    {
        if (!string.Equals(request.MemberType, "agent", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(membership.MembershipStatus, "active", StringComparison.OrdinalIgnoreCase)) return false;
        return sourceChannelId != membership.ChannelId || !string.Equals(membership.MemberIdentity, "", StringComparison.Ordinal);
    }

    public async Task<ChannelMembershipDto> EnsureAgentCommonsMembershipAsync(string agentIdentity, string? sourceSettingsJson = null,
        CancellationToken cancellationToken = default)
    {
        var commons = await EnsureAgentCommonsChannelAsync(cancellationToken);
        const string defaultSettingsJson = "{\"systemManaged\":true,\"source\":\"agent-commons-auto-membership\"}";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """"
            INSERT INTO channel_memberships(
                channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json, membership_purpose)
            VALUES ($channelId, 'agent', $agentIdentity, 'active', 'mentions_only', 1, 1, 0, 60, 1, $settingsJson, 'agent_commons')
            ON CONFLICT(channel_id, member_type, member_identity)
            DO UPDATE SET
                membership_status = CASE
                    WHEN channel_memberships.membership_status IN ('muted', 'left', 'banned') THEN channel_memberships.membership_status
                    WHEN channel_memberships.settings_json LIKE '%"systemManaged":true%' THEN 'active'
                    ELSE channel_memberships.membership_status
                END,
                wake_policy = CASE
                    WHEN channel_memberships.wake_policy = 'never' THEN channel_memberships.wake_policy
                    WHEN channel_memberships.membership_status IN ('muted', 'left', 'banned') THEN channel_memberships.wake_policy
                    WHEN channel_memberships.settings_json LIKE '%"systemManaged":true%' THEN 'mentions_only'
                    ELSE channel_memberships.wake_policy
                END,
                can_send = 1,
                can_react = 1,
                membership_purpose = 'agent_commons',
                updated_at = datetime('now')
            RETURNING id, channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json, membership_purpose, created_at, updated_at;
            """";
        command.Parameters.AddWithValue("$channelId", commons.Id);
        command.Parameters.AddWithValue("$agentIdentity", agentIdentity.Trim());
        command.Parameters.AddWithValue("$settingsJson", string.IsNullOrWhiteSpace(sourceSettingsJson) ? defaultSettingsJson : sourceSettingsJson);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadMembership(reader);
    }

    public async Task<AgentCommonsBrakeResultDto> ApplyAgentCommonsBrakeAsync(string membershipStatus = "muted", string wakePolicy = "never",
        CancellationToken cancellationToken = default)
    {
        var commons = await EnsureAgentCommonsChannelAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE channel_memberships
            SET membership_status = $membershipStatus,
                wake_policy = $wakePolicy,
                updated_at = datetime('now')
            WHERE channel_id = $channelId
              AND member_type = 'agent'
              AND membership_status = 'active';
            """;
        command.Parameters.AddWithValue("$channelId", commons.Id);
        command.Parameters.AddWithValue("$membershipStatus", membershipStatus);
        command.Parameters.AddWithValue("$wakePolicy", wakePolicy);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        return new AgentCommonsBrakeResultDto("applied", commons.Id, updated, membershipStatus, wakePolicy);
    }

    public async Task<IReadOnlyList<ChannelReactionSummaryDto>> ListReactionSummariesAsync(long channelId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.channel_message_id, r.reaction_key, r.reactor_type, r.reactor_identity
            FROM channel_reactions r
            JOIN channel_messages m ON m.id = r.channel_message_id
            WHERE m.channel_id = $channelId
            ORDER BY r.channel_message_id, r.reaction_key, r.created_at, r.id;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var grouped = new Dictionary<(long MessageId, string ReactionKey), List<string>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (reader.GetInt64(0), reader.GetString(1));
            if (!grouped.TryGetValue(key, out var reactors))
            {
                reactors = [];
                grouped[key] = reactors;
            }
            reactors.Add($"{reader.GetString(2)}:{reader.GetString(3)}");
        }
        return grouped
            .Select(item => new ChannelReactionSummaryDto(
                item.Key.MessageId,
                item.Key.ReactionKey,
                item.Value.Count,
                item.Value))
            .ToList();
    }

    public async Task<ChannelReactionDto> AddReactionAsync(long messageId, AddChannelReactionRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channel_reactions(channel_message_id, reactor_type, reactor_identity, reaction_key)
            VALUES ($messageId, $reactorType, $reactorIdentity, $reactionKey)
            ON CONFLICT(channel_message_id, reactor_type, reactor_identity, reaction_key) DO NOTHING;

            SELECT id, channel_message_id, reactor_type, reactor_identity, reaction_key, created_at
            FROM channel_reactions
            WHERE channel_message_id = $messageId
              AND reactor_type = $reactorType
              AND reactor_identity = $reactorIdentity
              AND reaction_key = $reactionKey;
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        command.Parameters.AddWithValue("$reactorType", request.ReactorType);
        command.Parameters.AddWithValue("$reactorIdentity", request.ReactorIdentity);
        command.Parameters.AddWithValue("$reactionKey", request.ReactionKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (!reader.HasRows && await reader.NextResultAsync(cancellationToken))
        {
        }
        await reader.ReadAsync(cancellationToken);
        return ReadReaction(reader);
    }

    public async Task<ChannelActivityEventDto> AppendActivityEventAsync(long channelId,
        AppendChannelActivityEventRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channel_activity_events(
                channel_id, project_id, agent_identity, delivery_request_id, session_key,
                display_block_id, parent_session_key, parent_agent_identity, worker_run_id, worker_role,
                agent_instance_id, pool_member_id,
                task_id, thread_id, anchor_message_id,
                assignment_id, checkpoint_type, checkpoint_handle,
                event_type, status, delivery_stage, terminal, sequence,
                title, summary, preview_json, metadata_json, dedupe_key, final_channel_message_id)
            VALUES (
                $channelId, $projectId, $agentIdentity, $deliveryRequestId, $sessionKey,
                $displayBlockId, $parentSessionKey, $parentAgentIdentity, $workerRunId, $workerRole,
                $agentInstanceId, $poolMemberId,
                $taskId, $threadId, $anchorMessageId,
                $assignmentId, $checkpointType, $checkpointHandle,
                $eventType, $status, $deliveryStage, $terminal, $sequence,
                $title, $summary, $previewJson, $metadataJson, $dedupeKey, $finalChannelMessageId)
            ON CONFLICT(channel_id, dedupe_key) WHERE dedupe_key IS NOT NULL DO UPDATE SET
                project_id = COALESCE(excluded.project_id, channel_activity_events.project_id),
                agent_identity = excluded.agent_identity,
                delivery_request_id = COALESCE(excluded.delivery_request_id, channel_activity_events.delivery_request_id),
                session_key = COALESCE(excluded.session_key, channel_activity_events.session_key),
                display_block_id = COALESCE(excluded.display_block_id, channel_activity_events.display_block_id),
                parent_session_key = COALESCE(excluded.parent_session_key, channel_activity_events.parent_session_key),
                parent_agent_identity = COALESCE(excluded.parent_agent_identity, channel_activity_events.parent_agent_identity),
                worker_run_id = COALESCE(excluded.worker_run_id, channel_activity_events.worker_run_id),
                worker_role = COALESCE(excluded.worker_role, channel_activity_events.worker_role),
                task_id = COALESCE(excluded.task_id, channel_activity_events.task_id),
                thread_id = COALESCE(excluded.thread_id, channel_activity_events.thread_id),
                anchor_message_id = COALESCE(excluded.anchor_message_id, channel_activity_events.anchor_message_id),
                assignment_id = COALESCE(excluded.assignment_id, channel_activity_events.assignment_id),
                checkpoint_type = COALESCE(excluded.checkpoint_type, channel_activity_events.checkpoint_type),
                checkpoint_handle = COALESCE(excluded.checkpoint_handle, channel_activity_events.checkpoint_handle),
                event_type = excluded.event_type,
                status = excluded.status,
                delivery_stage = excluded.delivery_stage,
                terminal = excluded.terminal,
                sequence = excluded.sequence,
                title = COALESCE(excluded.title, channel_activity_events.title),
                summary = COALESCE(excluded.summary, channel_activity_events.summary),
                preview_json = COALESCE(excluded.preview_json, channel_activity_events.preview_json),
                metadata_json = COALESCE(excluded.metadata_json, channel_activity_events.metadata_json),
                final_channel_message_id = COALESCE(excluded.final_channel_message_id, channel_activity_events.final_channel_message_id),
                update_version = channel_activity_events.update_version + 1,
                updated_at = datetime('now')
            RETURNING id, channel_id, project_id, agent_identity, delivery_request_id, session_key,
                display_block_id, parent_session_key, parent_agent_identity, worker_run_id, worker_role,
                agent_instance_id, pool_member_id,
                task_id, thread_id, anchor_message_id,
                assignment_id, checkpoint_type, checkpoint_handle,
                event_type, status, delivery_stage, terminal, sequence,
                update_version, title, summary, preview_json, metadata_json, dedupe_key, final_channel_message_id,
                created_at, updated_at;
            """;
        AddActivityParameters(command, channelId, request);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadActivityEvent(reader);
    }

    public async Task<ChannelActivityEventDto?> UpdateActivityEventAsync(long activityEventId,
        UpdateChannelActivityEventRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE channel_activity_events
            SET status = COALESCE($status, status),
                delivery_stage = COALESCE($deliveryStage, delivery_stage),
                terminal = COALESCE($terminal, terminal),
                title = COALESCE($title, title),
                summary = COALESCE($summary, summary),
                preview_json = COALESCE($previewJson, preview_json),
                metadata_json = COALESCE($metadataJson, metadata_json),
                final_channel_message_id = COALESCE($finalChannelMessageId, final_channel_message_id),
                update_version = update_version + 1,
                updated_at = datetime('now')
            WHERE id = $id
            RETURNING id, channel_id, project_id, agent_identity, delivery_request_id, session_key,
                display_block_id, parent_session_key, parent_agent_identity, worker_run_id, worker_role,
                agent_instance_id, pool_member_id,
                task_id, thread_id, anchor_message_id,
                assignment_id, checkpoint_type, checkpoint_handle,
                event_type, status, delivery_stage, terminal, sequence,
                update_version, title, summary, preview_json, metadata_json, dedupe_key, final_channel_message_id,
                created_at, updated_at;
            """;
        command.Parameters.AddWithValue("$id", activityEventId);
        command.Parameters.AddWithValue("$status", (object?)request.Status ?? DBNull.Value);
        command.Parameters.AddWithValue("$deliveryStage", NormalizeDeliveryStage(request.DeliveryStage));
        command.Parameters.AddWithValue("$terminal", request.Terminal.HasValue ? request.Terminal.Value ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$title", NormalizeActivityText(request.Title, 200));
        command.Parameters.AddWithValue("$summary", NormalizeActivityText(request.Summary, 1000));
        command.Parameters.AddWithValue("$previewJson", NormalizeActivityText(request.PreviewJson, 4000));
        command.Parameters.AddWithValue("$metadataJson", NormalizeActivityText(request.MetadataJson, 4000));
        command.Parameters.AddWithValue("$finalChannelMessageId", (object?)request.FinalChannelMessageId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadActivityEvent(reader) : null;
    }

    public async Task<IReadOnlyList<ChannelActivityEventDto>> ListActivityEventsAsync(long channelId,
        string? deliveryRequestId = null, string? sessionKey = null, string? displayBlockId = null,
        string? workerRunId = null, string? agentInstanceId = null, long? anchorMessageId = null, long? taskId = null,
        string? assignmentId = null, long? afterId = null,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var hasScopedFilter = deliveryRequestId is not null
                              || sessionKey is not null
                              || displayBlockId is not null
                              || workerRunId is not null
                              || agentInstanceId is not null
                              || anchorMessageId is not null
                              || taskId is not null
                              || assignmentId is not null;
        var useLatestChannelWindow = !hasScopedFilter && afterId is null;
        var filteredOrderBy = afterId is null ? "sequence ASC, id ASC" : "id ASC";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        const string selectColumns = """
            id, channel_id, project_id, agent_identity, delivery_request_id, session_key,
                display_block_id, parent_session_key, parent_agent_identity, worker_run_id, worker_role,
                agent_instance_id, pool_member_id,
                task_id, thread_id, anchor_message_id,
                assignment_id, checkpoint_type, checkpoint_handle,
                event_type, status, delivery_stage, terminal, sequence,
                update_version, title, summary, preview_json, metadata_json, dedupe_key, final_channel_message_id,
                created_at, updated_at
            """;
        command.CommandText = useLatestChannelWindow
            ? $""""
              SELECT {selectColumns}
              FROM (
                  SELECT {selectColumns}
                  FROM channel_activity_events
                  WHERE channel_id = $channelId
                  ORDER BY id DESC
                  LIMIT $limit
              ) recent_activity_events
              ORDER BY id ASC;
              """"
            : $""""
              SELECT {selectColumns}
              FROM channel_activity_events
              WHERE channel_id = $channelId
                AND ($deliveryRequestId IS NULL OR delivery_request_id = $deliveryRequestId)
                AND ($sessionKey IS NULL OR session_key = $sessionKey)
                AND ($displayBlockId IS NULL OR display_block_id = $displayBlockId)
                AND ($workerRunId IS NULL OR worker_run_id = $workerRunId)
                AND ($agentInstanceId IS NULL OR agent_instance_id = $agentInstanceId)
                AND ($anchorMessageId IS NULL OR anchor_message_id = $anchorMessageId)
                AND ($taskId IS NULL OR task_id = $taskId)
                AND ($assignmentId IS NULL OR assignment_id = $assignmentId)
                AND ($afterId IS NULL OR id > $afterId)
              ORDER BY {filteredOrderBy}
              LIMIT $limit;
              """";
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$deliveryRequestId", (object?)deliveryRequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionKey", (object?)sessionKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$displayBlockId", (object?)displayBlockId ?? DBNull.Value);
        command.Parameters.AddWithValue("$workerRunId", (object?)workerRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentInstanceId", (object?)agentInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$anchorMessageId", (object?)anchorMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$taskId", (object?)taskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$assignmentId", (object?)assignmentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$afterId", (object?)afterId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<ChannelActivityEventDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadActivityEvent(reader));
        return rows;
    }

    public async Task<IReadOnlyList<ChannelActivityEventDto>> ListActivityEventsAfterIdAsync(long channelId,
        long afterId = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        afterId = Math.Max(0, afterId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, channel_id, project_id, agent_identity, delivery_request_id, session_key,
                display_block_id, parent_session_key, parent_agent_identity, worker_run_id, worker_role,
                agent_instance_id, pool_member_id,
                task_id, thread_id, anchor_message_id,
                assignment_id, checkpoint_type, checkpoint_handle,
                event_type, status, delivery_stage, terminal, sequence,
                update_version, title, summary, preview_json, metadata_json, dedupe_key, final_channel_message_id,
                created_at, updated_at
            FROM channel_activity_events
            WHERE channel_id = $channelId
              AND id > $afterId
            ORDER BY id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$afterId", afterId);
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<ChannelActivityEventDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadActivityEvent(reader));
        return rows;
    }

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

    private static void AddChannelParameters(SqliteCommand command, CreateChannelRequest request)
    {
        command.Parameters.AddWithValue("$slug", request.Slug);
        command.Parameters.AddWithValue("$displayName", request.DisplayName);
        command.Parameters.AddWithValue("$kind", request.Kind);
        command.Parameters.AddWithValue("$projectId", (object?)request.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$spaceId", (object?)request.SpaceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdBy", request.CreatedBy ?? "system");
        command.Parameters.AddWithValue("$visibility", request.Visibility ?? "normal");
        command.Parameters.AddWithValue("$settingsJson", (object?)request.SettingsJson ?? DBNull.Value);
    }

    private static void AddActivityParameters(SqliteCommand command, long channelId, AppendChannelActivityEventRequest request)
    {
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$projectId", (object?)request.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentIdentity", request.AgentIdentity);
        command.Parameters.AddWithValue("$deliveryRequestId", (object?)request.DeliveryRequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionKey", (object?)request.SessionKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$displayBlockId", (object?)request.DisplayBlockId ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentSessionKey", (object?)request.ParentSessionKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentAgentIdentity", (object?)request.ParentAgentIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$workerRunId", (object?)request.WorkerRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$workerRole", (object?)request.WorkerRole ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentInstanceId", (object?)request.AgentInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$poolMemberId", (object?)request.PoolMemberId ?? DBNull.Value);
        command.Parameters.AddWithValue("$taskId", (object?)request.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$threadId", (object?)request.ThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$anchorMessageId", (object?)request.AnchorMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$assignmentId", (object?)request.AssignmentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$checkpointType", (object?)request.CheckpointType ?? DBNull.Value);
        command.Parameters.AddWithValue("$checkpointHandle", (object?)request.CheckpointHandle ?? DBNull.Value);
        command.Parameters.AddWithValue("$eventType", request.EventType);
        command.Parameters.AddWithValue("$status", request.Status ?? "completed");
        var deliveryStage = NormalizeDeliveryStage(request.DeliveryStage);
        command.Parameters.AddWithValue("$deliveryStage", deliveryStage is DBNull ? "progress" : deliveryStage);
        command.Parameters.AddWithValue("$terminal", request.Terminal == true ? 1 : 0);
        command.Parameters.AddWithValue("$sequence", request.Sequence ?? 0);
        command.Parameters.AddWithValue("$title", NormalizeActivityText(request.Title, 200));
        command.Parameters.AddWithValue("$summary", NormalizeActivityText(request.Summary, 1000));
        command.Parameters.AddWithValue("$previewJson", NormalizeActivityText(request.PreviewJson, 4000));
        command.Parameters.AddWithValue("$metadataJson", NormalizeActivityText(request.MetadataJson, 4000));
        command.Parameters.AddWithValue("$dedupeKey", (object?)request.DedupeKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$finalChannelMessageId", (object?)request.FinalChannelMessageId ?? DBNull.Value);
    }

    private static object NormalizeDeliveryStage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DBNull.Value;
        return NormalizeActivityText(value, 80);
    }

    private static string DefaultMessageKind(string senderType) => senderType == "agent" ? "agent_text" : "human_text";

    private static string? DeriveMessageDeliveryRequestId(PostChannelMessageRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.DeliveryRequestId))
            return request.DeliveryRequestId;

        if (string.Equals(request.SourceKind, "gateway_delivery", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(request.SourceId))
        {
            return request.SourceId;
        }

        return null;
    }

    private static object NormalizeActivityText(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return DBNull.Value;

        var redacted = SecretLikeValueRegex().Replace(value, match => $"{match.Groups[1].Value}\"[REDACTED]\"");
        return redacted.Length <= maxLength ? redacted : redacted[..maxLength] + "…";
    }

    [GeneratedRegex("((?:\\\"?(?:api[_-]?key|access[_-]?token|refresh[_-]?token|auth(?:orization)?|token|password|secret)\\\"?\\s*[:=]\\s*))(\\\"[^\\\"]*\\\"|[^,}\\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretLikeValueRegex();

    private static ChannelDto ReadChannel(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        GetNullableString(reader, 4),
        GetNullableString(reader, 5),
        reader.GetString(6),
        reader.GetString(7),
        GetNullableString(reader, 8),
        reader.GetString(9),
        reader.GetString(10),
        GetNullableString(reader, 11));

    private static ChannelMessageDto ReadMessage(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        GetNullableString(reader, 6),
        GetNullableString(reader, 7),
        GetNullableString(reader, 8),
        GetNullableString(reader, 23),  // target_project_id
        GetNullableInt64(reader, 24),   // target_task_id
        GetNullableString(reader, 25),  // worker_run_id
        GetNullableString(reader, 26),  // worker_role
        GetNullableString(reader, 27),  // profile_identity
        GetNullableString(reader, 9),   // summary
        GetNullableString(reader, 10),  // deep_link
        GetNullableInt64(reader, 11),   // thread_root_message_id
        GetNullableInt64(reader, 12),   // reply_to_message_id
        GetNullableString(reader, 13),  // metadata_json
        GetNullableString(reader, 14),  // delivery_request_id
        GetNullableString(reader, 15),  // dedupe_key
        GetNullableString(reader, 16),  // assignment_id
        GetNullableString(reader, 17),  // checkpoint_type
        GetNullableString(reader, 18),  // checkpoint_handle
        GetNullableString(reader, 19),  // agent_instance_id
        GetNullableString(reader, 20),  // pool_member_id
        GetNullableString(reader, 21),  // session_owner_id
        GetNullableString(reader, 22),  // session_id
        reader.GetString(28),           // created_at
        GetNullableString(reader, 29),  // edited_at
        GetNullableString(reader, 30)); // deleted_at

    private static ChannelMembershipDto ReadMembership(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetBoolean(6),
        reader.GetBoolean(7),
        reader.GetBoolean(8),
        reader.GetInt32(9),
        reader.GetInt32(10),
        GetNullableString(reader, 11),
        GetNullableString(reader, 12),
        reader.GetString(13),
        reader.GetString(14));

    private static ChannelReactionDto ReadReaction(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5));

    private static ChannelActivityEventDto ReadActivityEvent(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        GetNullableString(reader, 2),
        reader.GetString(3),
        GetNullableString(reader, 4),
        GetNullableString(reader, 5),
        GetNullableString(reader, 6),
        GetNullableString(reader, 7),
        GetNullableString(reader, 8),
        GetNullableString(reader, 9),
        GetNullableString(reader, 10),
        GetNullableString(reader, 11),
        GetNullableString(reader, 12),
        GetNullableInt64(reader, 13),
        GetNullableInt64(reader, 14),
        GetNullableInt64(reader, 15),
        GetNullableString(reader, 16),
        GetNullableString(reader, 17),
        GetNullableString(reader, 18),
        reader.GetString(19),
        reader.GetString(20),
        reader.GetString(21),
        reader.GetBoolean(22),
        reader.GetInt64(23),
        reader.GetInt64(24),
        GetNullableString(reader, 25),
        GetNullableString(reader, 26),
        GetNullableString(reader, 27),
        GetNullableString(reader, 28),
        GetNullableString(reader, 29),
        GetNullableInt64(reader, 30),
        reader.GetString(31),
        reader.GetString(32));

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    // =========================================================================
    // Channel-project link operations (task #1874)
    // =========================================================================

    /// <summary>
    /// Get all project links for a given channel.
    /// </summary>
    public async Task<IReadOnlyList<ChannelProjectLinkDto>> GetChannelProjectLinksAsync(
        long channelId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, channel_id, project_id, relation_kind, is_primary, settings_json, created_at
            FROM channel_project_links
            WHERE channel_id = $channelId
            ORDER BY is_primary DESC, project_id ASC;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        var rows = new List<ChannelProjectLinkDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadProjectLink(reader));
        return rows;
    }

    /// <summary>
    /// Get all channels linked to a given project.
    /// </summary>
    public async Task<IReadOnlyList<ChannelDto>> GetLinkedChannelsForProjectAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.slug, c.display_name, c.kind, c.project_id, c.space_id,
                   c.created_by, c.visibility, c.settings_json, c.created_at, c.updated_at, c.archived_at
            FROM channels c
            JOIN channel_project_links cpl ON cpl.channel_id = c.id
            WHERE cpl.project_id = $projectId
            ORDER BY cpl.is_primary DESC, c.id ASC;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        var rows = new List<ChannelDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadChannel(reader));
        return rows;
    }

    /// <summary>
    /// Upsert a channel-project link. Creates the link or updates the relation_kind,
    /// is_primary, and settings_json if it already exists.
    /// </summary>
    public async Task<ChannelProjectLinkDto> UpsertChannelProjectLinkAsync(
        UpsertChannelProjectLinkRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channel_project_links(channel_id, project_id, relation_kind, is_primary, settings_json)
            VALUES ($channelId, $projectId, $relationKind, $isPrimary, $settingsJson)
            ON CONFLICT(channel_id, project_id)
            DO UPDATE SET
                relation_kind = CASE
                    WHEN $relationKind IS NOT NULL THEN $relationKind
                    ELSE channel_project_links.relation_kind
                END,
                is_primary = CASE
                    WHEN $isPrimary IS NOT NULL THEN $isPrimary
                    ELSE channel_project_links.is_primary
                END,
                settings_json = COALESCE($settingsJson, channel_project_links.settings_json)
            RETURNING id, channel_id, project_id, relation_kind, is_primary, settings_json, created_at;
            """;
        command.Parameters.AddWithValue("$channelId", request.ChannelId);
        command.Parameters.AddWithValue("$projectId", request.ProjectId);
        command.Parameters.AddWithValue("$relationKind", (object?)request.RelationKind ?? "linked");
        command.Parameters.AddWithValue("$isPrimary", request.IsPrimary ?? false ? 1 : 0);
        command.Parameters.AddWithValue("$settingsJson", (object?)request.SettingsJson ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadProjectLink(reader);
    }

    /// <summary>
    /// Remove a channel-project link.
    /// </summary>
    public async Task RemoveChannelProjectLinkAsync(
        long channelId, string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM channel_project_links
            WHERE channel_id = $channelId
              AND project_id = $projectId;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$projectId", projectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ChannelProjectLinkDto ReadProjectLink(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetBoolean(4),
        GetNullableString(reader, 5),
        reader.GetString(6));

    // =========================================================================
    // Agents Overview read queries
    // =========================================================================

    /// <summary>
    /// List channels with optional project/channel filter. Returns channels with their membership counts.
    /// </summary>
    public async Task<IReadOnlyList<ChannelDto>> ListChannelsForOverviewAsync(
        string? projectId = null, long? channelId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, slug, display_name, kind, project_id, space_id, created_by, visibility, settings_json, created_at, updated_at, archived_at
            FROM channels
            WHERE ($projectId IS NULL OR project_id = $projectId)
              AND ($channelId IS NULL OR id = $channelId)
            ORDER BY updated_at DESC, id DESC;
            """;
        command.Parameters.AddWithValue("$projectId", (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$channelId", (object?)channelId ?? DBNull.Value);
        var rows = new List<ChannelDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadChannel(reader));
        return rows;
    }

    /// <summary>
    /// List memberships across channels, optionally filtered by member identity and project scope.
    /// </summary>
    public async Task<IReadOnlyList<ChannelMembershipDto>> ListMembershipsForOverviewAsync(
        string? projectId = null, long? channelId = null, string? agentIdentity = null, bool includeLeft = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """"
            SELECT m.id, m.channel_id, m.member_type, m.member_identity, m.membership_status, m.wake_policy,
                   m.can_send, m.can_react, m.can_invite, m.cooldown_seconds, m.max_auto_replies_per_window,
                   m.settings_json, m.membership_purpose, m.created_at, m.updated_at
            FROM channel_memberships m
            JOIN channels c ON c.id = m.channel_id
            WHERE m.member_type = 'agent'
              AND ($projectId IS NULL OR c.project_id = $projectId)
              AND ($channelId IS NULL OR m.channel_id = $channelId)
              AND ($agentIdentity IS NULL OR m.member_identity = $agentIdentity)
              AND ($includeLeft = 1 OR m.membership_status != 'left')
            ORDER BY m.member_identity, m.channel_id;
            """";
        command.Parameters.AddWithValue("$projectId", (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$channelId", (object?)channelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentIdentity", (object?)agentIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$includeLeft", includeLeft ? 1 : 0);
        var rows = new List<ChannelMembershipDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadMembership(reader));
        return rows;
    }

    /// <summary>
    /// List channel memberships for one member identity across channels, including channel metadata.
    /// Used by direct-event pollers to discover worker_pool_control and target_work channels by default.
    /// Long-lived runtime agents can opt into ordinary null-purpose memberships with includeOrdinaryMemberships.
    /// </summary>
    public async Task<IReadOnlyList<ChannelMembershipDiscoveryRowDto>> ListMembershipsByMemberIdentityAsync(
        string memberIdentity, string? membershipPurpose = null, string? projectId = null, long? channelId = null,
        bool includeLeft = false, bool includeOrdinaryMemberships = false, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var normalizedMemberIdentity = memberIdentity.Trim();
        var normalizedPurpose = string.IsNullOrWhiteSpace(membershipPurpose) ? null : membershipPurpose.Trim();
        var clampedLimit = Math.Clamp(limit, 1, 500);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """"
            SELECT c.id, c.slug, c.kind, c.project_id,
                   m.id, m.channel_id, m.member_type, m.member_identity, m.membership_status, m.wake_policy,
                   m.can_send, m.can_react, m.can_invite, m.cooldown_seconds, m.max_auto_replies_per_window,
                   m.settings_json, m.membership_purpose, m.created_at, m.updated_at
            FROM channel_memberships m
            JOIN channels c ON c.id = m.channel_id
            WHERE m.member_identity = $memberIdentity
              AND ($projectId IS NULL OR c.project_id = $projectId)
              AND ($channelId IS NULL OR m.channel_id = $channelId)
              AND (
                    $membershipPurpose IS NOT NULL
                    OR m.membership_purpose IN ('worker_pool_control', 'target_work')
                    OR ($includeOrdinaryMemberships = 1 AND (m.membership_purpose IS NULL OR trim(m.membership_purpose) = ''))
                  )
              AND ($membershipPurpose IS NULL OR m.membership_purpose = $membershipPurpose)
              AND ($includeLeft = 1 OR m.membership_status != 'left')
            ORDER BY CASE m.membership_purpose
                       WHEN 'worker_pool_control' THEN 0
                       WHEN 'target_work' THEN 1
                       ELSE 2
                     END,
                     c.id ASC
            LIMIT $limit;
            """";
        command.Parameters.AddWithValue("$memberIdentity", normalizedMemberIdentity);
        command.Parameters.AddWithValue("$membershipPurpose", (object?)normalizedPurpose ?? DBNull.Value);
        command.Parameters.AddWithValue("$projectId", (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$channelId", (object?)channelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$includeLeft", includeLeft ? 1 : 0);
        command.Parameters.AddWithValue("$includeOrdinaryMemberships", includeOrdinaryMemberships ? 1 : 0);
        command.Parameters.AddWithValue("$limit", clampedLimit);

        var rows = new List<ChannelMembershipDiscoveryRowDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var membership = new ChannelMembershipDto(
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetBoolean(10),
                reader.GetBoolean(11),
                reader.GetBoolean(12),
                reader.GetInt32(13),
                reader.GetInt32(14),
                GetNullableString(reader, 15),
                GetNullableString(reader, 16),
                reader.GetString(17),
                reader.GetString(18));
            rows.Add(new ChannelMembershipDiscoveryRowDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                GetNullableString(reader, 3),
                membership));
        }
        return rows;
    }

    /// <summary>
    /// List recent activity events across multi-channel scope, optionally filtered.
    /// Returns most recent events bounded by the per-agent limit.
    /// </summary>
    public async Task<IReadOnlyList<ChannelActivityEventDto>> ListRecentActivityForOverviewAsync(
        string? projectId = null, long? channelId = null, string? agentIdentity = null,
        int perAgentLimit = 3, CancellationToken cancellationToken = default)
    {
        var clampedLimit = Math.Clamp(perAgentLimit, 1, 100);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.channel_id, a.project_id, a.agent_identity, a.delivery_request_id, a.session_key,
                   a.display_block_id, a.parent_session_key, a.parent_agent_identity, a.worker_run_id, a.worker_role,
                   a.agent_instance_id, a.pool_member_id,
                   a.task_id, a.thread_id, a.anchor_message_id,
                   a.assignment_id, a.checkpoint_type, a.checkpoint_handle,
                   a.event_type, a.status, a.delivery_stage, a.terminal,
                   a.sequence, a.update_version, a.title, a.summary, a.preview_json, a.metadata_json, a.dedupe_key,
                   a.final_channel_message_id, a.created_at, a.updated_at
            FROM channel_activity_events a
            JOIN channels c ON c.id = a.channel_id
            WHERE ($projectId IS NULL OR c.project_id = $projectId)
              AND ($channelId IS NULL OR a.channel_id = $channelId)
              AND ($agentIdentity IS NULL OR a.agent_identity = $agentIdentity)
            ORDER BY a.agent_identity, a.created_at DESC, a.id DESC;
            """;
        command.Parameters.AddWithValue("$projectId", (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$channelId", (object?)channelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentIdentity", (object?)agentIdentity ?? DBNull.Value);
        var rows = new List<ChannelActivityEventDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadActivityEvent(reader));

        // Group by agent identity and apply per-agent limit
        return rows
            .GroupBy(a => a.AgentIdentity)
            .SelectMany(g => g.Take(clampedLimit))
            .ToList();
    }

    /// <summary>
    /// List recent activity events for a single agent detail view with per-agent pagination.
    /// </summary>
    public async Task<IReadOnlyList<ChannelActivityEventDto>> ListRecentActivityForDetailAsync(
        string agentIdentity, string? projectId = null, long? channelId = null,
        int activityLimit = 50, CancellationToken cancellationToken = default)
    {
        var clampedLimit = Math.Clamp(activityLimit, 1, 200);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.channel_id, a.project_id, a.agent_identity, a.delivery_request_id, a.session_key,
                   a.display_block_id, a.parent_session_key, a.parent_agent_identity, a.worker_run_id, a.worker_role,
                   a.agent_instance_id, a.pool_member_id,
                   a.task_id, a.thread_id, a.anchor_message_id,
                   a.assignment_id, a.checkpoint_type, a.checkpoint_handle,
                   a.event_type, a.status, a.delivery_stage, a.terminal,
                   a.sequence, a.update_version, a.title, a.summary, a.preview_json, a.metadata_json, a.dedupe_key,
                   a.final_channel_message_id, a.created_at, a.updated_at
            FROM channel_activity_events a
            JOIN channels c ON c.id = a.channel_id
            WHERE a.agent_identity = $agentIdentity
              AND ($projectId IS NULL OR c.project_id = $projectId)
              AND ($channelId IS NULL OR a.channel_id = $channelId)
            ORDER BY a.created_at DESC, a.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$agentIdentity", agentIdentity);
        command.Parameters.AddWithValue("$projectId", (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$channelId", (object?)channelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", clampedLimit);
        var rows = new List<ChannelActivityEventDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadActivityEvent(reader));
        return rows;
    }

    /// <summary>
    /// List distinct task IDs associated with activity events for a given agent.
    /// </summary>
    public async Task<IReadOnlyList<ChannelActivityEventDto>> ListTaskActivityForDetailAsync(
        string agentIdentity, string? projectId = null, long? channelId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.channel_id, a.project_id, a.agent_identity, a.delivery_request_id, a.session_key,
                   a.display_block_id, a.parent_session_key, a.parent_agent_identity, a.worker_run_id, a.worker_role,
                   a.agent_instance_id, a.pool_member_id,
                   a.task_id, a.thread_id, a.anchor_message_id,
                   a.assignment_id, a.checkpoint_type, a.checkpoint_handle,
                   a.event_type, a.status, a.delivery_stage, a.terminal,
                   a.sequence, a.update_version, a.title, a.summary, a.preview_json, a.metadata_json, a.dedupe_key,
                   a.final_channel_message_id, a.created_at, a.updated_at
            FROM channel_activity_events a
            JOIN channels c ON c.id = a.channel_id
            WHERE a.agent_identity = $agentIdentity
              AND a.task_id IS NOT NULL
              AND ($projectId IS NULL OR c.project_id = $projectId)
              AND ($channelId IS NULL OR a.channel_id = $channelId)
            ORDER BY a.created_at DESC, a.id DESC;
            """;
        command.Parameters.AddWithValue("$agentIdentity", agentIdentity);
        command.Parameters.AddWithValue("$projectId", (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$channelId", (object?)channelId ?? DBNull.Value);
        var rows = new List<ChannelActivityEventDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadActivityEvent(reader));
        return rows;
    }

    // =========================================================================
    // Read cursor operations (task #1769 shared-profile instance support)
    // =========================================================================

    /// <summary>
    /// Get a single read cursor by channel + reader identity. If instanceId is provided,
    /// returns the instance-scoped cursor; otherwise returns the profile-scoped cursor
    /// (instance_id = '').
    /// </summary>
    public async Task<ChannelReadCursorDto?> GetReadCursorAsync(long channelId, string readerType, string readerIdentity,
        string? instanceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Normalize: null/empty instanceId matches profile-level cursor (instance_id = '')
        var instanceFilter = string.IsNullOrEmpty(instanceId) ? "" : instanceId;
        command.CommandText = """
            SELECT id, channel_id, reader_type, reader_identity, instance_id,
                   last_read_channel_message_id, last_read_at, created_at, updated_at
            FROM channel_read_cursors
            WHERE channel_id = $channelId
              AND reader_type = $readerType
              AND reader_identity = $readerIdentity
              AND instance_id = $instanceId;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$readerType", readerType);
        command.Parameters.AddWithValue("$readerIdentity", readerIdentity);
        command.Parameters.AddWithValue("$instanceId", instanceFilter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadReadCursor(reader) : null;
    }

    /// <summary>
    /// Upsert a read cursor for profile-level or instance-level scoping.
    /// Two instances sharing the same profile identity maintain independent read positions.
    /// Profile-level cursors use instance_id = '' for proper SQLite UNIQUE enforcement.
    /// </summary>
    public async Task<ChannelReadCursorDto> UpsertReadCursorAsync(long channelId, UpsertChannelReadCursorRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Normalize: null/empty instanceId -> '' (profile-level cursor with proper uniqueness)
        var normalizedInstanceId = string.IsNullOrEmpty(request.InstanceId) ? "" : request.InstanceId;
        command.CommandText = """
            INSERT INTO channel_read_cursors(channel_id, reader_type, reader_identity, instance_id, last_read_channel_message_id)
            VALUES ($channelId, $readerType, $readerIdentity, $instanceId, $lastReadMessageId)
            ON CONFLICT(channel_id, reader_type, reader_identity, instance_id)
            DO UPDATE SET
                last_read_channel_message_id = COALESCE($lastReadMessageId, channel_read_cursors.last_read_channel_message_id),
                last_read_at = datetime('now'),
                updated_at = datetime('now')
            RETURNING id, channel_id, reader_type, reader_identity, instance_id,
                last_read_channel_message_id, last_read_at, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$readerType", request.ReaderType);
        command.Parameters.AddWithValue("$readerIdentity", request.ReaderIdentity);
        command.Parameters.AddWithValue("$instanceId", normalizedInstanceId);
        command.Parameters.AddWithValue("$lastReadMessageId", (object?)request.LastReadChannelMessageId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadReadCursor(reader);
    }

    /// <summary>
    /// List read cursors for a channel, optionally filtered by reader identity or instance.
    /// Profile-level cursors use instance_id = '' internally.
    /// When instanceId is null/omitted, returns ALL cursors (both profile and instance).
    /// When instanceId is provided, filters to that specific instance ('' for profile-level).
    /// </summary>
    public async Task<IReadOnlyList<ChannelReadCursorDto>> ListReadCursorsAsync(long channelId,
        string? readerType = null, string? readerIdentity = null, string? instanceId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, channel_id, reader_type, reader_identity, instance_id,
                   last_read_channel_message_id, last_read_at, created_at, updated_at
            FROM channel_read_cursors
            WHERE channel_id = $channelId
              AND ($readerType IS NULL OR reader_type = $readerType)
              AND ($readerIdentity IS NULL OR reader_identity = $readerIdentity)
              AND ($instanceId IS NULL OR instance_id = $instanceId)
            ORDER BY reader_type, reader_identity, instance_id, id ASC;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$readerType", (object?)readerType ?? DBNull.Value);
        command.Parameters.AddWithValue("$readerIdentity", (object?)readerIdentity ?? DBNull.Value);
        // When instanceId is explicitly provided (including ''), normalize and filter;
        // when null, the $instanceId IS NULL condition matches all rows.
        command.Parameters.AddWithValue("$instanceId", instanceId is null ? DBNull.Value : (object)(string.IsNullOrEmpty(instanceId) ? "" : instanceId));
        var rows = new List<ChannelReadCursorDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadReadCursor(reader));
        return rows;
    }

    private static ChannelReadCursorDto ReadReadCursor(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        // Convert '' back to null in DTO for API backward compatibility
        // (profile-level cursor shows as null InstanceId externally)
        NormalizeReadCursorInstanceId(reader, 4),
        GetNullableInt64(reader, 5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8));

    private static string? NormalizeReadCursorInstanceId(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetString(ordinal);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    // =========================================================================
    // Worker-pool lobby operations (task #1771)
    // =========================================================================

    /// <summary>
    /// Ensure the #worker-pool lobby channel exists (slug='worker-pool', kind='system').
    /// Returns the channel DTO. Idempotent — uses ON CONFLICT on slug.
    /// </summary>
    public async Task<ChannelDto> EnsureWorkerPoolLobbyChannelAsync(CancellationToken cancellationToken = default)
    {
        const string settingsJson = "{\"systemManaged\":true,\"channelRole\":\"worker_pool_lobby\",\"description\":\"Worker-pool lobby: visible home lane for worker-pool orchestration. Idle = available. Status transitions: idle → leased → draining → released → idle.\"}";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """"
            INSERT INTO channels(slug, display_name, kind, created_by, visibility, settings_json)
            VALUES ('worker-pool', '#worker-pool', 'system', 'system', 'normal', $settingsJson)
            ON CONFLICT(slug) DO UPDATE SET
                display_name = '#worker-pool',
                kind = 'system',
                visibility = 'normal',
                settings_json = COALESCE(channels.settings_json, excluded.settings_json),
                updated_at = datetime('now')
            RETURNING id, slug, display_name, kind, project_id, space_id, created_by, visibility, settings_json, created_at, updated_at, archived_at;
            """";
        command.Parameters.AddWithValue("$settingsJson", settingsJson);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadChannel(reader);
    }

    /// <summary>
    /// Ensure the #service-work global service-activity channel exists.
    /// Returns the channel DTO. Idempotent — uses ON CONFLICT on slug.
    /// The channel has no project_id (global scope), uses kind='system' with
    /// channelRole='service_work' in settings_json for UI identification.
    /// </summary>
    public async Task<ChannelDto> EnsureServiceWorkChannelAsync(CancellationToken cancellationToken = default)
    {
        const string settingsJson = "{\"systemManaged\":true,\"channelRole\":\"service_work\",\"channelScope\":\"global\",\"description\":\"Global service-work channel for background agent activity, service automation, watcher output, maintenance events, deployment/status pings, and non-project-specific operational messages.\"}";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channels(slug, display_name, kind, created_by, visibility, settings_json)
            VALUES ('service-work', '#service-work', 'system', 'system', 'normal', $settingsJson)
            ON CONFLICT(slug) DO UPDATE SET
                display_name = '#service-work',
                kind = 'system',
                visibility = 'normal',
                settings_json = COALESCE(channels.settings_json, excluded.settings_json),
                updated_at = datetime('now')
            RETURNING id, slug, display_name, kind, project_id, space_id, created_by, visibility, settings_json, created_at, updated_at, archived_at;
            """;
        command.Parameters.AddWithValue("$settingsJson", settingsJson);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadChannel(reader);
    }

    /// <summary>
    /// Upsert a worker-pool member's presence record in the lobby.
    /// Uses ON CONFLICT(channel_id, member_identity) to create or update.
    /// The release_acknowledged flag gates the transition from 'released' to 'idle':
    /// - Setting status='released' sets release_acknowledged=0
    /// - A separate AcknowledgeWorkerPoolReleaseAsync call sets release_acknowledged=1
    /// - Only when status='released' AND release_acknowledged=1 can the worker
    ///   transition back to 'idle' (available)
    /// </summary>
    public async Task<WorkerPoolLobbyPresenceDto> UpsertWorkerPoolLobbyPresenceAsync(
        long channelId, UpsertWorkerPoolLobbyPresenceRequest request,
        CancellationToken cancellationToken = default)
    {
        var lobbyChannelId = channelId;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // Determine if release-acknowledged gate applies.
        // When transitioning from 'released' to 'idle', require release_acknowledged=1.
        // For other transitions or new inserts, derive default ack state.
        string requestedStatus = request.Status ?? "idle";

        command.CommandText = """"
            INSERT INTO worker_pool_lobby_presence(
                channel_id, member_identity, agent_instance_id, pool_member_id,
                concrete_identity,
                profile, role, status, current_assignment_id, current_task_id,
                current_project_id, last_activity_at, release_acknowledged)
            VALUES (
                $channelId, $memberIdentity, $agentInstanceId, $poolMemberId,
                COALESCE($poolMemberId, $agentInstanceId, ''),
                $profile, $role, $status, $currentAssignmentId, $currentTaskId,
                $currentProjectId, $lastActivityAt,
                CASE WHEN $status = 'idle' AND EXISTS(
                    SELECT 1 FROM worker_pool_lobby_presence
                    WHERE channel_id = $channelId
                      AND member_identity = $memberIdentity
                      AND concrete_identity = COALESCE($poolMemberId, $agentInstanceId, '')
                      AND status = 'released'
                      AND release_acknowledged = 0
                ) THEN 0 ELSE 1 END)
            ON CONFLICT(channel_id, member_identity, concrete_identity) DO UPDATE SET
                agent_instance_id = COALESCE($agentInstanceId, worker_pool_lobby_presence.agent_instance_id),
                pool_member_id = COALESCE($poolMemberId, worker_pool_lobby_presence.pool_member_id),
                profile = COALESCE($profile, worker_pool_lobby_presence.profile),
                role = COALESCE($role, worker_pool_lobby_presence.role),
                status = CASE
                    -- Gate: released->idle requires release_acknowledged
                    WHEN $status = 'idle' AND worker_pool_lobby_presence.status = 'released'
                         AND worker_pool_lobby_presence.release_acknowledged = 0
                    THEN worker_pool_lobby_presence.status
                    WHEN $status = 'released'
                    THEN 'released'
                    ELSE COALESCE($status, worker_pool_lobby_presence.status)
                END,
                current_assignment_id = $currentAssignmentId,
                current_task_id = $currentTaskId,
                current_project_id = $currentProjectId,
                last_activity_at = COALESCE($lastActivityAt, worker_pool_lobby_presence.last_activity_at),
                -- Reset release_acknowledged when transitioning to 'released'
                release_acknowledged = CASE
                    WHEN $status = 'released' THEN 0
                    ELSE worker_pool_lobby_presence.release_acknowledged
                END,
                updated_at = datetime('now')
            RETURNING id, channel_id, member_identity, agent_instance_id, pool_member_id,
                profile, role, status, current_assignment_id, current_task_id,
                current_project_id, last_activity_at, created_at, updated_at;
            """";
        command.Parameters.AddWithValue("$channelId", lobbyChannelId);
        command.Parameters.AddWithValue("$memberIdentity", request.MemberIdentity);
        command.Parameters.AddWithValue("$agentInstanceId", (object?)request.AgentInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$poolMemberId", (object?)request.PoolMemberId ?? DBNull.Value);
        command.Parameters.AddWithValue("$profile", (object?)request.Profile ?? DBNull.Value);
        command.Parameters.AddWithValue("$role", (object?)request.Role ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", requestedStatus);
        command.Parameters.AddWithValue("$currentAssignmentId", (object?)request.CurrentAssignmentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$currentTaskId", (object?)request.CurrentTaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$currentProjectId", (object?)request.CurrentProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastActivityAt", (object?)request.LastActivityAt ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadWorkerPoolLobbyPresence(reader);
    }

    /// <summary>
    /// Acknowledge Core release for a worker in 'released' status, permitting
    /// the transition back to 'idle' (available). This is the release gate:
    /// Core must explicitly acknowledge before a worker is shown as available.
    /// </summary>
    public async Task<WorkerPoolLobbyPresenceDto?> AcknowledgeWorkerPoolReleaseAsync(
        long channelId, string memberIdentity,
        string? agentInstanceId = null, string? poolMemberId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """"
            UPDATE worker_pool_lobby_presence
            SET release_acknowledged = 1,
                updated_at = datetime('now')
            WHERE channel_id = $channelId
              AND member_identity = $memberIdentity
              AND ($agentInstanceId IS NULL AND $poolMemberId IS NULL
                   OR concrete_identity = COALESCE($poolMemberId, $agentInstanceId, ''))
              AND status = 'released'
            RETURNING id, channel_id, member_identity, agent_instance_id, pool_member_id,
                profile, role, status, current_assignment_id, current_task_id,
                current_project_id, last_activity_at, created_at, updated_at;
            """";
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$memberIdentity", memberIdentity);
        command.Parameters.AddWithValue("$agentInstanceId", (object?)agentInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$poolMemberId", (object?)poolMemberId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadWorkerPoolLobbyPresence(reader) : null;
    }

    /// <summary>
    /// Release a child-run lobby presence. Transitions status to 'released'.
    /// Channels-only — does not claim to release Core capacity or Gateway delivery.
    /// Only releases non-terminal statuses (idle, leased, busy).
    /// </summary>
    public async Task<WorkerPoolLobbyPresenceDto?> ReleaseChildRunPresenceAsync(
        long channelId, string memberIdentity,
        string? agentInstanceId = null, string? poolMemberId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE worker_pool_lobby_presence
            SET status = 'released',
                updated_at = datetime('now')
            WHERE channel_id = $channelId
              AND member_identity = $memberIdentity
              AND status IN ('idle', 'leased', 'busy')
              AND ($agentInstanceId IS NULL AND $poolMemberId IS NULL
                   OR concrete_identity = COALESCE($poolMemberId, $agentInstanceId, ''))
            RETURNING id, channel_id, member_identity, agent_instance_id, pool_member_id,
                profile, role, status, current_assignment_id, current_task_id,
                current_project_id, last_activity_at, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$memberIdentity", memberIdentity);
        command.Parameters.AddWithValue("$agentInstanceId", (object?)agentInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$poolMemberId", (object?)poolMemberId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadWorkerPoolLobbyPresence(reader) : null;
    }

    /// <summary>
    /// List all worker-pool lobby presence records for a given lobby channel.
    /// Returns the full presence list for projection into the overview response.
    /// </summary>
    public async Task<IReadOnlyList<WorkerPoolLobbyPresenceDto>> ListWorkerPoolLobbyPresenceAsync(
        long channelId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """"
            SELECT id, channel_id, member_identity, agent_instance_id, pool_member_id,
                   profile, role, status, current_assignment_id, current_task_id,
                   current_project_id, last_activity_at, created_at, updated_at
            FROM worker_pool_lobby_presence
            WHERE channel_id = $channelId
            ORDER BY status ASC, role ASC, profile ASC, member_identity ASC, concrete_identity ASC;
            """";
        command.Parameters.AddWithValue("$channelId", channelId);
        var rows = new List<WorkerPoolLobbyPresenceDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadWorkerPoolLobbyPresence(reader));
        return rows;
    }

    private static WorkerPoolLobbyPresenceDto ReadWorkerPoolLobbyPresence(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        GetNullableString(reader, 3),
        GetNullableString(reader, 4),
        GetNullableString(reader, 5),
        GetNullableString(reader, 6),
        reader.GetString(7),
        GetNullableString(reader, 8),
        GetNullableString(reader, 9),
        GetNullableString(reader, 10),
        GetNullableString(reader, 11),
        reader.GetString(12),
        reader.GetString(13));

    // =========================================================================
    // Worker-pool membership lifecycle (task #1880)
    // =========================================================================

    /// <summary>
    /// Ensure the worker has an active membership in the #worker-pool control channel
    /// with purpose 'worker_pool_control'. This is the idle home for pool workers.
    /// Idempotent — reactivates 'left' memberships back to 'active'.
    /// Does NOT touch the lobby presence table (that's handled separately via lobby endpoints).
    /// </summary>
    public async Task<ChannelMembershipDto> EnsureWorkerPoolControlMembershipAsync(
        string agentIdentity, CancellationToken cancellationToken = default)
    {
        var lobby = await EnsureWorkerPoolLobbyChannelAsync(cancellationToken);
        const string settingsJson = "{\"systemManaged\":true,\"source\":\"worker-pool-control-auto-membership\"}";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """"
            INSERT INTO channel_memberships(
                channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json, membership_purpose)
            VALUES ($channelId, 'agent', $agentIdentity, 'active', 'mentions_only', 1, 1, 0, 60, 1, $settingsJson, 'worker_pool_control')
            ON CONFLICT(channel_id, member_type, member_identity)
            DO UPDATE SET
                membership_status = CASE
                    WHEN channel_memberships.membership_status IN ('muted', 'banned') THEN channel_memberships.membership_status
                    ELSE 'active'
                END,
                wake_policy = 'mentions_only',
                can_send = 1,
                can_react = 1,
                membership_purpose = 'worker_pool_control',
                updated_at = datetime('now')
            RETURNING id, channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json, membership_purpose, created_at, updated_at;
            """";
        command.Parameters.AddWithValue("$channelId", lobby.Id);
        command.Parameters.AddWithValue("$agentIdentity", agentIdentity.Trim());
        command.Parameters.AddWithValue("$settingsJson", settingsJson);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadMembership(reader);
    }

    /// <summary>
    /// Release a worker's target-work membership in a project channel.
    /// Sets membership_status to 'left' when the worker is released from an assignment.
    /// Only affects memberships with purpose 'target_work' (or legacy null) that are 'active'.
    /// Does NOT touch worker_pool_control or agent_commons memberships.
    /// Returns the updated membership or null if none found.
    /// </summary>
    public async Task<ChannelMembershipDto?> ReleaseTargetWorkMembershipAsync(
        long channelId, string agentIdentity, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """"
            UPDATE channel_memberships
            SET membership_status = 'left',
                updated_at = datetime('now')
            WHERE channel_id = $channelId
              AND member_type = 'agent'
              AND member_identity = $agentIdentity
              AND membership_status = 'active'
              AND (membership_purpose = 'target_work' OR membership_purpose IS NULL)
              AND (membership_purpose IS NULL OR membership_purpose != 'worker_pool_control')
              AND (membership_purpose IS NULL OR membership_purpose != 'agent_commons')
            RETURNING id, channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json, membership_purpose, created_at, updated_at;
            """";
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$agentIdentity", agentIdentity.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMembership(reader) : null;
    }

    // =========================================================================
    // Active-work continuation routing queries (task #1873)
    // =========================================================================

    /// <summary>
    /// Find channel messages carrying target-work fields that match the given
    /// target project/task/assignment/run. Used by active-work continuation routing
    /// to locate the source channel and session owner for active work.
    /// Results are ordered by most recent first.
    /// </summary>
    public async Task<IReadOnlyList<ChannelMessageDto>> FindActiveWorkMessagesAsync(
        string? targetProjectId = null,
        long? targetTaskId = null,
        string? assignmentId = null,
        string? workerRunId = null,
        string? profileIdentity = null,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var conditions = new List<string>
        {
            "m.target_project_id IS NOT NULL"
        };

        if (!string.IsNullOrWhiteSpace(targetProjectId))
            conditions.Add("m.target_project_id = $targetProjectId");
        if (targetTaskId.HasValue)
            conditions.Add("m.target_task_id = $targetTaskId");
        if (!string.IsNullOrWhiteSpace(assignmentId))
            conditions.Add("m.assignment_id = $assignmentId");
        if (!string.IsNullOrWhiteSpace(workerRunId))
            conditions.Add("m.worker_run_id = $workerRunId");
        if (!string.IsNullOrWhiteSpace(profileIdentity))
            conditions.Add("m.profile_identity = $profileIdentity");

        var whereClause = string.Join(" AND ", conditions);
        command.CommandText = $"""
            SELECT m.id, m.channel_id, m.sender_type, m.sender_identity, m.body, m.message_kind, m.source_kind, m.source_id, m.source_project_id,
                m.summary, m.deep_link, m.thread_root_message_id, m.reply_to_message_id, m.metadata_json, m.delivery_request_id, m.dedupe_key,
                m.assignment_id, m.checkpoint_type, m.checkpoint_handle, m.agent_instance_id, m.pool_member_id,
                m.session_owner_id, m.session_id,
                m.target_project_id, m.target_task_id, m.worker_run_id, m.worker_role, m.profile_identity, m.created_at, m.edited_at, m.deleted_at
            FROM channel_messages m
            WHERE {whereClause}
              AND m.deleted_at IS NULL
            ORDER BY m.created_at DESC, m.id DESC
            LIMIT $limit;
            """;

        if (!string.IsNullOrWhiteSpace(targetProjectId))
            command.Parameters.AddWithValue("$targetProjectId", targetProjectId);
        if (targetTaskId.HasValue)
            command.Parameters.AddWithValue("$targetTaskId", targetTaskId.Value);
        if (!string.IsNullOrWhiteSpace(assignmentId))
            command.Parameters.AddWithValue("$assignmentId", assignmentId);
        if (!string.IsNullOrWhiteSpace(workerRunId))
            command.Parameters.AddWithValue("$workerRunId", workerRunId);
        if (!string.IsNullOrWhiteSpace(profileIdentity))
            command.Parameters.AddWithValue("$profileIdentity", profileIdentity);
        command.Parameters.AddWithValue("$limit", limit);

        var rows = new List<ChannelMessageDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadMessage(reader));
        return rows;
    }

    /// <summary>
    /// Find activity events carrying target-work fields (project_id, task_id,
    /// assignment_id, worker_run_id) that match the given filters.
    /// Ordered by most recent first.
    /// </summary>
    public async Task<IReadOnlyList<ChannelActivityEventDto>> FindActiveWorkActivityEventsAsync(
        string? targetProjectId = null,
        long? targetTaskId = null,
        string? assignmentId = null,
        string? workerRunId = null,
        string? profileIdentity = null,
        bool nonTerminalOnly = false,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(targetProjectId))
            conditions.Add("a.project_id = $targetProjectId");
        if (targetTaskId.HasValue)
            conditions.Add("a.task_id = $targetTaskId");
        if (!string.IsNullOrWhiteSpace(assignmentId))
            conditions.Add("a.assignment_id = $assignmentId");
        if (!string.IsNullOrWhiteSpace(workerRunId))
            conditions.Add("a.worker_run_id = $workerRunId");
        // profileIdentity on activity events is matched via agent_identity
        if (!string.IsNullOrWhiteSpace(profileIdentity))
            conditions.Add("a.agent_identity = $profileIdentity");
        if (nonTerminalOnly)
            conditions.Add("a.terminal = 0");

        var whereClause = conditions.Count > 0
            ? string.Join(" AND ", conditions)
            : "1=1";

        command.CommandText = $"""
            SELECT a.id, a.channel_id, a.project_id, a.agent_identity, a.delivery_request_id, a.session_key,
                   a.display_block_id, a.parent_session_key, a.parent_agent_identity, a.worker_run_id, a.worker_role,
                   a.agent_instance_id, a.pool_member_id,
                   a.task_id, a.thread_id, a.anchor_message_id,
                   a.assignment_id, a.checkpoint_type, a.checkpoint_handle,
                   a.event_type, a.status, a.delivery_stage, a.terminal,
                   a.sequence, a.update_version, a.title, a.summary, a.preview_json, a.metadata_json, a.dedupe_key,
                   a.final_channel_message_id, a.created_at, a.updated_at
            FROM channel_activity_events a
            WHERE {whereClause}
            ORDER BY a.created_at DESC, a.id DESC
            LIMIT $limit;
            """;

        if (!string.IsNullOrWhiteSpace(targetProjectId))
            command.Parameters.AddWithValue("$targetProjectId", targetProjectId);
        if (targetTaskId.HasValue)
            command.Parameters.AddWithValue("$targetTaskId", targetTaskId.Value);
        if (!string.IsNullOrWhiteSpace(assignmentId))
            command.Parameters.AddWithValue("$assignmentId", assignmentId);
        if (!string.IsNullOrWhiteSpace(workerRunId))
            command.Parameters.AddWithValue("$workerRunId", workerRunId);
        if (!string.IsNullOrWhiteSpace(profileIdentity))
            command.Parameters.AddWithValue("$profileIdentity", profileIdentity);
        command.Parameters.AddWithValue("$limit", limit);

        var rows = new List<ChannelActivityEventDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadActivityEvent(reader));
        return rows;
    }

    // =========================================================================
    // Direct conversation (DM transcript) methods — migration v6
    // =========================================================================

    public async Task<DirectConversationDto> GetOrCreateConversationAsync(CreateDirectConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO direct_conversations(human_identity, agent_identity, scope_project_id, display_title)
            VALUES ($humanIdentity, $agentIdentity, $scopeProjectId, $displayTitle)
            ON CONFLICT(human_identity, agent_identity) DO UPDATE SET
                display_title = COALESCE(excluded.display_title, direct_conversations.display_title),
                scope_project_id = COALESCE(excluded.scope_project_id, direct_conversations.scope_project_id),
                updated_at = datetime('now')
            RETURNING id, human_identity, agent_identity, scope_project_id, display_title,
                is_archived, is_muted, settings_json,
                last_entry_at, last_entry_preview, last_entry_sender,
                created_at, updated_at,
                0;
            """;
        command.Parameters.AddWithValue("$humanIdentity", request.HumanIdentity.Trim());
        command.Parameters.AddWithValue("$agentIdentity", request.AgentIdentity.Trim());
        command.Parameters.AddWithValue("$scopeProjectId", (object?)request.ScopeProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$displayTitle", (object?)request.DisplayTitle ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadConversation(reader);
    }

    public async Task<IReadOnlyList<DirectConversationDto>> ListConversationsAsync(string humanIdentity,
        int limit = 50, long? afterId = null, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = afterId is null
            ? """
                SELECT dc.id, dc.human_identity, dc.agent_identity, dc.scope_project_id, dc.display_title,
                    dc.is_archived, dc.is_muted, dc.settings_json,
                    dc.last_entry_at, dc.last_entry_preview, dc.last_entry_sender,
                    dc.created_at, dc.updated_at,
                    (SELECT COUNT(*) FROM direct_conversation_entries dce
                     WHERE dce.conversation_id = dc.id
                       AND (rc.last_read_entry_id IS NULL OR dce.id > rc.last_read_entry_id)) AS unread_count
                FROM direct_conversations dc
                LEFT JOIN direct_conversation_read_cursors rc
                    ON rc.conversation_id = dc.id AND rc.reader_identity = $readerIdentity
                WHERE dc.human_identity = $humanIdentity
                ORDER BY dc.last_entry_at DESC, dc.id DESC
                LIMIT $limit;
                """
            : """
                SELECT dc.id, dc.human_identity, dc.agent_identity, dc.scope_project_id, dc.display_title,
                    dc.is_archived, dc.is_muted, dc.settings_json,
                    dc.last_entry_at, dc.last_entry_preview, dc.last_entry_sender,
                    dc.created_at, dc.updated_at,
                    (SELECT COUNT(*) FROM direct_conversation_entries dce
                     WHERE dce.conversation_id = dc.id
                       AND (rc.last_read_entry_id IS NULL OR dce.id > rc.last_read_entry_id)) AS unread_count
                FROM direct_conversations dc
                LEFT JOIN direct_conversation_read_cursors rc
                    ON rc.conversation_id = dc.id AND rc.reader_identity = $readerIdentity
                WHERE dc.human_identity = $humanIdentity
                  AND dc.id < $afterId
                ORDER BY dc.last_entry_at DESC, dc.id DESC
                LIMIT $limit;
                """;
        command.Parameters.AddWithValue("$humanIdentity", humanIdentity.Trim());
        command.Parameters.AddWithValue("$readerIdentity", humanIdentity.Trim());
        command.Parameters.AddWithValue("$limit", limit);
        if (afterId is not null)
            command.Parameters.AddWithValue("$afterId", afterId.Value);
        var rows = new List<DirectConversationDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadConversation(reader));
        return rows;
    }

    public async Task<DirectConversationDto?> GetConversationAsync(long conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, human_identity, agent_identity, scope_project_id, display_title,
                is_archived, is_muted, settings_json,
                last_entry_at, last_entry_preview, last_entry_sender,
                created_at, updated_at,
                0
            FROM direct_conversations
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadConversation(reader) : null;
    }

    public async Task<DirectConversationEntryDto> AddConversationEntryAsync(long conversationId,
        long channelMessageId, string direction, string senderIdentity, string recipientIdentity,
        long? sourceChannelId = null, string? sourceProjectId = null, long? sourceTaskId = null,
        string? sourceSessionOwnerId = null, string? sourceWorkerRunId = null,
        string? bodyPreview = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO direct_conversation_entries(
                conversation_id, channel_message_id, direction, sender_identity, recipient_identity,
                source_channel_id, source_project_id, source_task_id,
                source_session_owner_id, source_worker_run_id, body_preview)
            VALUES ($conversationId, $channelMessageId, $direction, $senderIdentity, $recipientIdentity,
                $sourceChannelId, $sourceProjectId, $sourceTaskId,
                $sourceSessionOwnerId, $sourceWorkerRunId, $bodyPreview)
            RETURNING id, conversation_id, channel_message_id, direction, sender_identity, recipient_identity,
                source_channel_id, source_project_id, source_task_id,
                source_session_owner_id, source_worker_run_id, body_preview, created_at;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$channelMessageId", channelMessageId);
        command.Parameters.AddWithValue("$direction", direction);
        command.Parameters.AddWithValue("$senderIdentity", senderIdentity.Trim());
        command.Parameters.AddWithValue("$recipientIdentity", recipientIdentity.Trim());
        command.Parameters.AddWithValue("$sourceChannelId", (object?)sourceChannelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceProjectId", (object?)sourceProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceTaskId", (object?)sourceTaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceSessionOwnerId", (object?)sourceSessionOwnerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceWorkerRunId", (object?)sourceWorkerRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$bodyPreview", (object?)bodyPreview ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var entry = ReadConversationEntry(reader);

        // Update conversation last-entry projection
        await using var updateCmd = connection.CreateCommand();
        updateCmd.CommandText = """
            UPDATE direct_conversations
            SET last_entry_at = $entryAt,
                last_entry_preview = $preview,
                last_entry_sender = $sender,
                updated_at = datetime('now')
            WHERE id = $conversationId;
            """;
        updateCmd.Parameters.AddWithValue("$entryAt", entry.CreatedAt);
        updateCmd.Parameters.AddWithValue("$preview", Truncate(bodyPreview ?? "", 200));
        updateCmd.Parameters.AddWithValue("$sender", senderIdentity.Trim());
        updateCmd.Parameters.AddWithValue("$conversationId", conversationId);
        await updateCmd.ExecuteNonQueryAsync(cancellationToken);

        return entry;
    }

    public async Task<IReadOnlyList<DirectConversationEntryDto>> ListConversationEntriesAsync(
        long conversationId, int limit = 50, long? afterId = null, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = afterId is null
            ? """
                SELECT id, conversation_id, channel_message_id, direction, sender_identity, recipient_identity,
                    source_channel_id, source_project_id, source_task_id,
                    source_session_owner_id, source_worker_run_id, body_preview, created_at
                FROM direct_conversation_entries
                WHERE conversation_id = $conversationId
                ORDER BY id DESC
                LIMIT $limit;
                """
            : """
                SELECT id, conversation_id, channel_message_id, direction, sender_identity, recipient_identity,
                    source_channel_id, source_project_id, source_task_id,
                    source_session_owner_id, source_worker_run_id, body_preview, created_at
                FROM direct_conversation_entries
                WHERE conversation_id = $conversationId
                  AND id < $afterId
                ORDER BY id DESC
                LIMIT $limit;
                """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$limit", limit);
        if (afterId is not null)
            command.Parameters.AddWithValue("$afterId", afterId.Value);
        var rows = new List<DirectConversationEntryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadConversationEntry(reader));
        return rows;
    }

    public async Task<DirectConversationReadCursorDto> UpsertReadCursorAsync(long conversationId,
        UpsertDirectReadCursorRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO direct_conversation_read_cursors(conversation_id, reader_identity, last_read_entry_id)
            VALUES ($conversationId, $readerIdentity, $lastReadEntryId)
            ON CONFLICT(conversation_id, reader_identity) DO UPDATE SET
                last_read_entry_id = COALESCE(excluded.last_read_entry_id, direct_conversation_read_cursors.last_read_entry_id),
                last_read_at = datetime('now'),
                updated_at = datetime('now')
            RETURNING id, conversation_id, reader_identity, last_read_entry_id, last_read_at, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$readerIdentity", request.ReaderIdentity.Trim());
        command.Parameters.AddWithValue("$lastReadEntryId", (object?)request.LastReadEntryId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadDirectConversationReadCursor(reader);
    }

    public async Task<DirectConversationReadCursorDto?> GetReadCursorAsync(long conversationId,
        string readerIdentity, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, conversation_id, reader_identity, last_read_entry_id, last_read_at, created_at, updated_at
            FROM direct_conversation_read_cursors
            WHERE conversation_id = $conversationId AND reader_identity = $readerIdentity;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$readerIdentity", readerIdentity.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDirectConversationReadCursor(reader) : null;
    }

    public async Task<long> GetUnreadEntryCountAsync(long conversationId, long? lastReadEntryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (lastReadEntryId is null)
        {
            command.CommandText = """
                SELECT COUNT(*) FROM direct_conversation_entries
                WHERE conversation_id = $conversationId;
                """;
        }
        else
        {
            command.CommandText = """
                SELECT COUNT(*) FROM direct_conversation_entries
                WHERE conversation_id = $conversationId AND id > $lastReadEntryId;
                """;
            command.Parameters.AddWithValue("$lastReadEntryId", lastReadEntryId.Value);
        }
        command.Parameters.AddWithValue("$conversationId", conversationId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long count ? count : 0;
    }

    // ── Agent response transcript linking ────────────────────────────────

    public async Task<DirectConversationEntryDto> LinkMessageToConversationAsync(long conversationId,
        long channelMessageId, string direction, string senderIdentity, string recipientIdentity,
        string? bodyPreview = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        // Read canonical channel_message source fields for source badge attribution
        long? sourceChannelId = null;
        string? sourceProjectId = null;
        long? sourceTaskId = null;
        string? sourceSessionOwnerId = null;
        string? sourceWorkerRunId = null;

        await using (var readCmd = connection.CreateCommand())
        {
            readCmd.CommandText = """
                SELECT channel_id, source_project_id, target_project_id, target_task_id,
                       session_owner_id, worker_run_id
                FROM channel_messages WHERE id = $id;
                """;
            readCmd.Parameters.AddWithValue("$id", channelMessageId);
            await using var readReader = await readCmd.ExecuteReaderAsync(cancellationToken);
            if (await readReader.ReadAsync(cancellationToken))
            {
                sourceChannelId = readReader.IsDBNull(0) ? null : readReader.GetInt64(0);
                sourceProjectId = readReader.IsDBNull(1) ? null : readReader.GetString(1)
                    ?? (readReader.IsDBNull(2) ? null : readReader.GetString(2));
                sourceTaskId = readReader.IsDBNull(3) ? null : readReader.GetInt64(3);
                sourceSessionOwnerId = readReader.IsDBNull(4) ? null : readReader.GetString(4);
                sourceWorkerRunId = readReader.IsDBNull(5) ? null : readReader.GetString(5);
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO direct_conversation_entries(
                conversation_id, channel_message_id, direction, sender_identity, recipient_identity,
                source_channel_id, source_project_id, source_task_id,
                source_session_owner_id, source_worker_run_id, body_preview)
            VALUES ($conversationId, $channelMessageId, $direction, $senderIdentity, $recipientIdentity,
                $sourceChannelId, $sourceProjectId, $sourceTaskId,
                $sourceSessionOwnerId, $sourceWorkerRunId, $bodyPreview)
            RETURNING id, conversation_id, channel_message_id, direction, sender_identity, recipient_identity,
                source_channel_id, source_project_id, source_task_id,
                source_session_owner_id, source_worker_run_id, body_preview, created_at;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$channelMessageId", channelMessageId);
        command.Parameters.AddWithValue("$direction", direction);
        command.Parameters.AddWithValue("$senderIdentity", senderIdentity.Trim());
        command.Parameters.AddWithValue("$recipientIdentity", recipientIdentity.Trim());
        command.Parameters.AddWithValue("$sourceChannelId", (object?)sourceChannelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceProjectId", (object?)sourceProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceTaskId", (object?)sourceTaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceSessionOwnerId", (object?)sourceSessionOwnerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceWorkerRunId", (object?)sourceWorkerRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$bodyPreview", (object?)bodyPreview ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var entry = ReadConversationEntry(reader);

        // Update conversation last-entry projection
        await using var updateCmd = connection.CreateCommand();
        updateCmd.CommandText = """
            UPDATE direct_conversations
            SET last_entry_at = $entryAt,
                last_entry_preview = $preview,
                last_entry_sender = $sender,
                updated_at = datetime('now')
            WHERE id = $conversationId;
            """;
        updateCmd.Parameters.AddWithValue("$entryAt", entry.CreatedAt);
        updateCmd.Parameters.AddWithValue("$preview", Truncate(bodyPreview ?? "", 200));
        updateCmd.Parameters.AddWithValue("$sender", senderIdentity.Trim());
        updateCmd.Parameters.AddWithValue("$conversationId", conversationId);
        await updateCmd.ExecuteNonQueryAsync(cancellationToken);

        return entry;
    }

    // ── Private row readers ─────────────────────────────────────────────

    private static DirectConversationDto ReadConversation(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        HumanIdentity: reader.GetString(1),
        AgentIdentity: reader.GetString(2),
        ScopeProjectId: reader.IsDBNull(3) ? null : reader.GetString(3),
        DisplayTitle: reader.IsDBNull(4) ? null : reader.GetString(4),
        IsArchived: reader.GetInt64(5) != 0,
        IsMuted: reader.GetInt64(6) != 0,
        SettingsJson: reader.IsDBNull(7) ? null : reader.GetString(7),
        LastEntryAt: reader.IsDBNull(8) ? null : reader.GetString(8),
        LastEntryPreview: reader.IsDBNull(9) ? null : reader.GetString(9),
        LastEntrySender: reader.IsDBNull(10) ? null : reader.GetString(10),
        CreatedAt: reader.GetString(11),
        UpdatedAt: reader.GetString(12),
        UnreadCount: reader.IsDBNull(13) ? 0 : reader.GetInt64(13));

    private static DirectConversationEntryDto ReadConversationEntry(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        ConversationId: reader.GetInt64(1),
        ChannelMessageId: reader.GetInt64(2),
        Direction: reader.GetString(3),
        SenderIdentity: reader.GetString(4),
        RecipientIdentity: reader.GetString(5),
        SourceChannelId: reader.IsDBNull(6) ? null : reader.GetInt64(6),
        SourceProjectId: reader.IsDBNull(7) ? null : reader.GetString(7),
        SourceTaskId: reader.IsDBNull(8) ? null : reader.GetInt64(8),
        SourceSessionOwnerId: reader.IsDBNull(9) ? null : reader.GetString(9),
        SourceWorkerRunId: reader.IsDBNull(10) ? null : reader.GetString(10),
        BodyPreview: reader.IsDBNull(11) ? null : reader.GetString(11),
        CreatedAt: reader.GetString(12));

    private static DirectConversationReadCursorDto ReadDirectConversationReadCursor(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        ConversationId: reader.GetInt64(1),
        ReaderIdentity: reader.GetString(2),
        LastReadEntryId: reader.IsDBNull(3) ? null : reader.GetInt64(3),
        LastReadAt: reader.GetString(4),
        CreatedAt: reader.GetString(5),
        UpdatedAt: reader.GetString(6));

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
}
