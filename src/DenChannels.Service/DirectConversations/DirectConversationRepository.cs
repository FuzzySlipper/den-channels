using DenChannels.Service.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

using DenChannels.Service.Channels;
using DenChannels.Service.DirectAgentEvents;

namespace DenChannels.Service.Channels;

public sealed class DirectConversationRepository : ChannelsRepositoryBase
{
    public DirectConversationRepository(IOptions<DenChannelsOptions> options) : base(options)
    {
    }

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
