using DenChannels.Service.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace DenChannels.Service.Data;

public sealed class ChannelsDatabaseInitializer
{
    public const int CurrentSchemaVersion = 1;

    private readonly IOptions<DenChannelsOptions> _options;
    private readonly ILogger<ChannelsDatabaseInitializer> _logger;

    public ChannelsDatabaseInitializer(IOptions<DenChannelsOptions> options, ILogger<ChannelsDatabaseInitializer> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var databasePath = _options.Value.Database.Path;
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        await ApplyMigrationsAsync(connection, _logger, cancellationToken);
    }

    public static async Task ApplyMigrationsAsync(SqliteConnection connection, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        await EnsureSchemaMigrationsTableAsync(connection, cancellationToken);

        var currentVersion = await GetCurrentSchemaVersionAsync(connection, cancellationToken);
        await EnsureChannelMessageCompatibilityColumnsAsync(connection, cancellationToken);
        if (currentVersion < 1)
        {
            logger?.LogInformation("Applying Den Channels database migration 1");
            await ExecuteNonQueryAsync(connection, InitialSchemaSql, cancellationToken);
            await SetSchemaVersionAsync(connection, 1, "initial_channel_schema", cancellationToken);
        }

        await EnsureChannelMessageCompatibilityColumnsAsync(connection, cancellationToken);
        await EnsureChannelMessagesSourceKindConstraintAsync(connection, cancellationToken);
        await ExecuteNonQueryAsync(connection, PostCreateIndexesSql, cancellationToken);
    }

    private static async Task EnsureChannelMessagesSourceKindConstraintAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "channel_messages", cancellationToken))
            return;

        var createSql = await GetTableCreateSqlAsync(connection, "channel_messages", cancellationToken);
        if (createSql?.Contains("'gateway_delivery'", StringComparison.OrdinalIgnoreCase) == true)
            return;

        await ExecuteNonQueryAsync(connection, RebuildChannelMessagesWithGatewayDeliverySourceKindSql, cancellationToken);
    }

    private static async Task<string?> GetTableCreateSqlAsync(SqliteConnection connection, string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value as string;
    }

    private static async Task EnsureChannelMessageCompatibilityColumnsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(connection, "channel_messages", "message_kind", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "source_kind", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "source_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "source_project_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "summary", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "deep_link", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "thread_root_message_id", "INTEGER", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "reply_to_message_id", "INTEGER", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "metadata_json", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "dedupe_key", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "created_at", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "edited_at", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "deleted_at", "TEXT", cancellationToken);
    }

    private static async Task EnsureSchemaMigrationsTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """;
        await ExecuteNonQueryAsync(connection, sql, cancellationToken);
    }

    private static async Task<int> GetCurrentSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long version ? (int)version : 0;
    }

    private static async Task SetSchemaVersionAsync(SqliteConnection connection, int version, string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO schema_migrations(version, name)
            VALUES ($version, $name);
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$name", name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string tableName, string columnName,
        string columnDefinition, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, tableName, cancellationToken) ||
            await ColumnExistsAsync(connection, tableName, columnName, cancellationToken))
        {
            return;
        }

        await ExecuteNonQueryAsync(connection,
            $"ALTER TABLE {QuoteIdentifier(tableName)} ADD COLUMN {QuoteIdentifier(columnName)} {columnDefinition};",
            cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string InitialSchemaSql = """
        CREATE TABLE IF NOT EXISTS channels (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            slug          TEXT NOT NULL UNIQUE,
            display_name  TEXT NOT NULL,
            kind          TEXT NOT NULL
                          CHECK (kind IN ('project_default', 'project_activity', 'task_room', 'ad_hoc', 'system', 'dm', 'small_group')),
            project_id    TEXT,
            space_id      TEXT,
            created_by    TEXT NOT NULL DEFAULT 'system',
            visibility    TEXT NOT NULL DEFAULT 'normal'
                          CHECK (visibility IN ('normal', 'private', 'archived')),
            settings_json TEXT,
            created_at    TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at    TEXT NOT NULL DEFAULT (datetime('now')),
            archived_at   TEXT
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_channels_project_default
            ON channels(project_id)
            WHERE project_id IS NOT NULL AND kind = 'project_default';
        CREATE INDEX IF NOT EXISTS idx_channels_project_kind
            ON channels(project_id, kind);

        CREATE TABLE IF NOT EXISTS channel_messages (
            id                     INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id             INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            sender_type            TEXT NOT NULL
                                   CHECK (sender_type IN ('user', 'agent', 'system', 'bridge')),
            sender_identity        TEXT NOT NULL,
            body                   TEXT NOT NULL,
            message_kind           TEXT NOT NULL DEFAULT 'human_text'
                                   CHECK (message_kind IN ('human_text', 'agent_text', 'system_event', 'mirror_summary', 'command', 'command_result')),
            source_kind            TEXT
                                   CHECK (source_kind IS NULL OR source_kind IN ('task_message', 'agent_stream_entry', 'notification', 'worker_run', 'review_round', 'review_finding', 'wake_event', 'gateway_delivery', 'external_adapter_message')),
            source_id              TEXT,
            source_project_id      TEXT,
            summary                TEXT,
            deep_link              TEXT,
            thread_root_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            reply_to_message_id    INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            metadata_json          TEXT,
            dedupe_key             TEXT,
            created_at             TEXT NOT NULL DEFAULT (datetime('now')),
            edited_at              TEXT,
            deleted_at             TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_channel_messages_channel_created
            ON channel_messages(channel_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_channel_messages_source
            ON channel_messages(source_kind, source_id)
            WHERE source_kind IS NOT NULL AND source_id IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_channel_messages_dedupe
            ON channel_messages(channel_id, dedupe_key)
            WHERE dedupe_key IS NOT NULL;

        CREATE TABLE IF NOT EXISTS channel_memberships (
            id                            INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id                    INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            member_type                   TEXT NOT NULL
                                          CHECK (member_type IN ('user', 'agent', 'role', 'group')),
            member_identity               TEXT NOT NULL,
            membership_status             TEXT NOT NULL DEFAULT 'active'
                                          CHECK (membership_status IN ('active', 'muted', 'left', 'banned')),
            wake_policy                   TEXT NOT NULL DEFAULT 'mentions_only'
                                          CHECK (wake_policy IN ('never', 'mentions_only', 'direct_questions_only', 'substantive_digest', 'all_human_messages', 'all_messages_except_self')),
            can_send                      INTEGER NOT NULL DEFAULT 1 CHECK (can_send IN (0, 1)),
            can_react                     INTEGER NOT NULL DEFAULT 1 CHECK (can_react IN (0, 1)),
            can_invite                    INTEGER NOT NULL DEFAULT 0 CHECK (can_invite IN (0, 1)),
            cooldown_seconds              INTEGER NOT NULL DEFAULT 60 CHECK (cooldown_seconds >= 0),
            max_auto_replies_per_window   INTEGER NOT NULL DEFAULT 1 CHECK (max_auto_replies_per_window >= 0),
            settings_json                 TEXT,
            created_at                    TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at                    TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(channel_id, member_type, member_identity)
        );

        CREATE INDEX IF NOT EXISTS idx_channel_memberships_member
            ON channel_memberships(member_type, member_identity, membership_status);

        CREATE TABLE IF NOT EXISTS channel_reactions (
            id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_message_id    INTEGER NOT NULL REFERENCES channel_messages(id) ON DELETE CASCADE,
            reactor_type          TEXT NOT NULL CHECK (reactor_type IN ('user', 'agent', 'system', 'bridge')),
            reactor_identity      TEXT NOT NULL,
            reaction_key          TEXT NOT NULL,
            created_at            TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(channel_message_id, reactor_type, reactor_identity, reaction_key)
        );

        CREATE INDEX IF NOT EXISTS idx_channel_reactions_message
            ON channel_reactions(channel_message_id);

        CREATE TABLE IF NOT EXISTS channel_read_cursors (
            id                         INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id                 INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            reader_type                TEXT NOT NULL CHECK (reader_type IN ('user', 'agent', 'role', 'group')),
            reader_identity            TEXT NOT NULL,
            last_read_channel_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            last_read_at               TEXT NOT NULL DEFAULT (datetime('now')),
            created_at                 TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at                 TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(channel_id, reader_type, reader_identity)
        );
        """;

    private const string PostCreateIndexesSql = """
        CREATE UNIQUE INDEX IF NOT EXISTS ux_channels_project_default
            ON channels(project_id)
            WHERE project_id IS NOT NULL AND kind = 'project_default';
        CREATE INDEX IF NOT EXISTS idx_channels_project_kind
            ON channels(project_id, kind);
        CREATE INDEX IF NOT EXISTS idx_channel_messages_channel_created
            ON channel_messages(channel_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_channel_messages_source
            ON channel_messages(source_kind, source_id)
            WHERE source_kind IS NOT NULL AND source_id IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_channel_messages_dedupe
            ON channel_messages(channel_id, dedupe_key)
            WHERE dedupe_key IS NOT NULL;
        """;

    private const string RebuildChannelMessagesWithGatewayDeliverySourceKindSql = """
        PRAGMA foreign_keys = OFF;
        CREATE TABLE channel_messages__new (
            id                     INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id             INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            sender_type            TEXT NOT NULL
                                   CHECK (sender_type IN ('user', 'agent', 'system', 'bridge')),
            sender_identity        TEXT NOT NULL,
            body                   TEXT NOT NULL,
            message_kind           TEXT NOT NULL DEFAULT 'human_text'
                                   CHECK (message_kind IN ('human_text', 'agent_text', 'system_event', 'mirror_summary', 'command', 'command_result')),
            source_kind            TEXT
                                   CHECK (source_kind IS NULL OR source_kind IN ('task_message', 'agent_stream_entry', 'notification', 'worker_run', 'review_round', 'review_finding', 'wake_event', 'gateway_delivery', 'external_adapter_message')),
            source_id              TEXT,
            source_project_id      TEXT,
            summary                TEXT,
            deep_link              TEXT,
            thread_root_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            reply_to_message_id    INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            metadata_json          TEXT,
            dedupe_key             TEXT,
            created_at             TEXT NOT NULL DEFAULT (datetime('now')),
            edited_at              TEXT,
            deleted_at             TEXT
        );

        INSERT INTO channel_messages__new(
            id, channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id,
            source_project_id, summary, deep_link, thread_root_message_id, reply_to_message_id,
            metadata_json, dedupe_key, created_at, edited_at, deleted_at)
        SELECT
            id,
            channel_id,
            sender_type,
            sender_identity,
            body,
            COALESCE(message_kind, CASE WHEN sender_type = 'agent' THEN 'agent_text' ELSE 'human_text' END),
            source_kind,
            source_id,
            source_project_id,
            summary,
            deep_link,
            thread_root_message_id,
            reply_to_message_id,
            metadata_json,
            dedupe_key,
            COALESCE(created_at, datetime('now')),
            edited_at,
            deleted_at
        FROM channel_messages
        ORDER BY id;

        DROP TABLE channel_messages;
        ALTER TABLE channel_messages__new RENAME TO channel_messages;
        PRAGMA foreign_keys = ON;
        """;
}
