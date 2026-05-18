using DenChannels.Service.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace DenChannels.Service.Channels;

public sealed class ChannelsRepository
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
                summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, dedupe_key)
            VALUES (
                $channelId, $senderType, $senderIdentity, $body, $messageKind, $sourceKind, $sourceId, $sourceProjectId,
                $summary, $deepLink, $threadRootMessageId, $replyToMessageId, $metadataJson, $dedupeKey)
            RETURNING id, channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id, source_project_id,
                summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, dedupe_key, created_at, edited_at, deleted_at;
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
        command.Parameters.AddWithValue("$dedupeKey", (object?)request.DedupeKey ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadMessage(reader);
    }

    public async Task<IReadOnlyList<ChannelMessageDto>> ListMessagesAsync(long channelId, long? afterId = null,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = afterId is null
            ? """
              SELECT id, channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id, source_project_id,
                  summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, dedupe_key, created_at, edited_at, deleted_at
              FROM (
                  SELECT id, channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id, source_project_id,
                      summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, dedupe_key, created_at, edited_at, deleted_at
                  FROM channel_messages
                  WHERE channel_id = $channelId
                    AND deleted_at IS NULL
                  ORDER BY id DESC
                  LIMIT $limit
              ) AS latest_messages
              ORDER BY id ASC;
              """
            : """
              SELECT id, channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id, source_project_id,
                  summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, dedupe_key, created_at, edited_at, deleted_at
              FROM channel_messages
              WHERE channel_id = $channelId
                AND id > $afterId
                AND deleted_at IS NULL
              ORDER BY id ASC
              LIMIT $limit;
              """;
        command.Parameters.AddWithValue("$channelId", channelId);
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
                summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, dedupe_key, created_at, edited_at, deleted_at
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
                summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, dedupe_key, created_at, edited_at, deleted_at
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

    public async Task<IReadOnlyList<ChannelMembershipDto>> ListMembershipsAsync(long channelId, int limit = 200,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json, created_at, updated_at
            FROM channel_memberships
            WHERE channel_id = $channelId
            ORDER BY id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$limit", limit);
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
                summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, dedupe_key, created_at, edited_at, deleted_at
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
        command.CommandText = """
            INSERT INTO channel_memberships(
                channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json)
            VALUES ($channelId, $memberType, $memberIdentity, $membershipStatus, $wakePolicy, $canSend, $canReact, $canInvite,
                $cooldownSeconds, $maxAutoRepliesPerWindow, $settingsJson)
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
                updated_at = datetime('now')
            RETURNING id, channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json, created_at, updated_at;
            """;
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadMembership(reader);
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

    private static string DefaultMessageKind(string senderType) => senderType == "agent" ? "agent_text" : "human_text";

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
        GetNullableString(reader, 9),
        GetNullableString(reader, 10),
        GetNullableInt64(reader, 11),
        GetNullableInt64(reader, 12),
        GetNullableString(reader, 13),
        GetNullableString(reader, 14),
        reader.GetString(15),
        GetNullableString(reader, 16),
        GetNullableString(reader, 17));

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
        reader.GetString(12),
        reader.GetString(13));

    private static ChannelReactionDto ReadReaction(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5));

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
