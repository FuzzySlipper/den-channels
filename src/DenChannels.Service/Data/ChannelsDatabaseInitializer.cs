using DenChannels.Service.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace DenChannels.Service.Data;

public sealed class ChannelsDatabaseInitializer
{
    public const int CurrentSchemaVersion = 8;

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
        await RecoverChannelActivityEventsRebuildIfNeededAsync(connection, logger, cancellationToken);

        var currentVersion = await GetCurrentSchemaVersionAsync(connection, cancellationToken);
        await EnsureChannelsCompatibilityColumnsAsync(connection, cancellationToken);
        await EnsureChannelMessageCompatibilityColumnsAsync(connection, cancellationToken);
        if (currentVersion < 1)
        {
            logger?.LogInformation("Applying Den Channels database migration 1");
            await ExecuteNonQueryAsync(connection, InitialSchemaSql, cancellationToken);
            await SetSchemaVersionAsync(connection, 1, "initial_channel_schema", cancellationToken);
        }

        if (currentVersion < 2)
        {
            logger?.LogInformation("Applying Den Channels database migration 2: shared-profile instance support");
            await ExecuteNonQueryAsync(connection, MigrationV2Sql, cancellationToken);
            await SetSchemaVersionAsync(connection, 2, "shared_profile_instances", cancellationToken);
        }

        if (currentVersion < 3)
        {
            logger?.LogInformation("Applying Den Channels database migration 3: worker-pool lobby presence");
            await ExecuteNonQueryAsync(connection, MigrationV3Sql, cancellationToken);
            await SetSchemaVersionAsync(connection, 3, "worker_pool_lobby_presence", cancellationToken);
        }

        if (currentVersion < 4)
        {
            logger?.LogInformation("Applying Den Channels database migration 4: membership purpose");
            await ExecuteNonQueryAsync(connection, MigrationV4Sql, cancellationToken);
            await SetSchemaVersionAsync(connection, 4, "membership_purpose", cancellationToken);
        }

        if (currentVersion < 5)
        {
            logger?.LogInformation("Applying Den Channels database migration 5: channel-project links");
            await ExecuteNonQueryAsync(connection, MigrationV5Sql, cancellationToken);
            await SetSchemaVersionAsync(connection, 5, "channel_project_links", cancellationToken);
        }

        if (currentVersion < 6)
        {
            logger?.LogInformation("Applying Den Channels database migration 6: direct-agent DM transcripts");
            await ExecuteNonQueryAsync(connection, MigrationV6Sql, cancellationToken);
            await SetSchemaVersionAsync(connection, 6, "direct_agent_dm_transcripts", cancellationToken);
        }

        if (currentVersion < 7)
        {
            logger?.LogInformation("Applying Den Channels database migration 7: FTS5 search index for channel messages");
            await ExecuteNonQueryAsync(connection, MigrationV7Sql, cancellationToken);
            await SetSchemaVersionAsync(connection, 7, "channel_messages_fts5", cancellationToken);
        }

        if (currentVersion < 8)
        {
            // Ensure columns needed for v8 rebuild exist before migration.
            // These are normally added by post-migration Ensure*Compat methods
            // but the v8 rebuild reads from these columns.
            await EnsureColumnAsync(connection, "channel_memberships", "membership_purpose", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_messages", "assignment_id", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_messages", "checkpoint_type", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_messages", "checkpoint_handle", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_messages", "agent_instance_id", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_messages", "pool_member_id", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_messages", "session_owner_id", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_messages", "session_id", "TEXT", cancellationToken);
            // Compatibility columns added by EnsureChannelMessageCompatibilityColumnsAsync,
            // but that runs before v1 creates the table on fresh DBs.
            await EnsureColumnAsync(connection, "channel_messages", "target_project_id", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_messages", "target_task_id", "INTEGER", cancellationToken);
            await EnsureColumnAsync(connection, "channel_messages", "worker_run_id", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_messages", "worker_role", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_messages", "profile_identity", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "agent_instance_id", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "pool_member_id", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "assignment_id", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "checkpoint_type", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "checkpoint_handle", "TEXT", cancellationToken);
            // Activity events compatibility columns needed for v8 COALESCE reads.
            await EnsureColumnAsync(connection, "channel_activity_events", "delivery_stage", "TEXT NOT NULL DEFAULT 'progress'", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "terminal", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "final_channel_message_id", "INTEGER", cancellationToken);
            // Runtime-neutral session columns needed for hermes value migration.
            await EnsureColumnAsync(connection, "channel_activity_events", "session_key", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "parent_session_key", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "display_block_id", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "parent_agent_identity", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "worker_run_id", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "worker_role", "TEXT", cancellationToken);
            // Ensure channel_memberships exists for backfill (may not on very old legacy DBs).
            if (!await TableExistsAsync(connection, "channel_memberships", cancellationToken))
            {
                await ExecuteNonQueryAsync(connection, ChannelMembershipsMinimalSchemaSql, cancellationToken);
            }
            // These tables are needed for v8 migration backfills (may not exist on very old legacy DBs).
            await ExecuteNonQueryAsync(connection, ChannelActivityEventsSchemaSql, cancellationToken);
            // ChannelActivityEventsSchemaSql is CREATE IF NOT EXISTS and very old legacy databases
            // may create or retain a pre-agent-work-lifecycle shape here. Migration v8 reads these
            // columns during the rebuild, so ensure them after the table definitely exists.
            await EnsureColumnAsync(connection, "channel_activity_events", "agent_instance_id", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "pool_member_id", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "assignment_id", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "checkpoint_type", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "checkpoint_handle", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "update_version", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "title", "TEXT", cancellationToken);
            if (!await TableExistsAsync(connection, "channel_read_cursors", cancellationToken))
            {
                await ExecuteNonQueryAsync(connection, MigrationV2Sql, cancellationToken);
            }
            await EnsureColumnAsync(connection, "channel_read_cursors", "instance_id", "TEXT", cancellationToken);
            // Legacy hermes columns needed for COALESCE during rebuild; dropped by migration.
            await EnsureColumnAsync(connection, "channel_activity_events", "hermes_session_key", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "channel_activity_events", "parent_hermes_session_key", "TEXT", cancellationToken);
            logger?.LogInformation("Applying Den Channels database migration 8: membership/subscription split, Hermes column purge, gateway_delivery quarantine");
            await ExecuteNonQueryAsync(connection, MigrationV8Sql, cancellationToken);
            await SetSchemaVersionAsync(connection, 8, "membership_subscription_split", cancellationToken);
        }

        await EnsureChannelsCompatibilityColumnsAsync(connection, cancellationToken);
        await EnsureChannelMessageCompatibilityColumnsAsync(connection, cancellationToken);
        await EnsureChannelMessagesSourceKindConstraintAsync(connection, cancellationToken);
        await EnsureChannelActivityEventsSchemaAsync(connection, cancellationToken);
        await EnsureChannelActivityEventsCompatibilityColumnsAsync(connection, cancellationToken);
        await EnsureAssignmentCompatibilityColumnsAsync(connection, cancellationToken);
        await EnsureSharedProfileInstanceColumnsAsync(connection, cancellationToken);
        await EnsureChannelActivityEventsEventTypeConstraintAsync(connection, logger, cancellationToken);
        await EnsureChannelReadCursorsInstanceConstraintAsync(connection, logger, cancellationToken);
        await EnsureChannelMembershipsMembershipPurposeColumnAsync(connection, cancellationToken);
        await EnsureAgentCommonsSeedAsync(connection, cancellationToken);
        await EnsureWorkerPoolLobbySeedAsync(connection, cancellationToken);
        await EnsureDenSystemChannelSeedAsync(connection, cancellationToken);
        await EnsureWorkerPoolLobbyPresenceConcreteConstraintAsync(connection, logger, cancellationToken);
        await EnsureChannelMessagesFts5Async(connection, cancellationToken);
        await EnsureChannelMembershipsV8ColumnsAsync(connection, cancellationToken);
        await ExecuteNonQueryAsync(connection, PostCreateIndexesSql, cancellationToken);
    }

    private static async Task EnsureChannelMessagesSourceKindConstraintAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "channel_messages", cancellationToken))
            return;

        var createSql = await GetTableCreateSqlAsync(connection, "channel_messages", cancellationToken);
        // gateway_delivery is quarantined as historical/tombstone in v8.
        // Kept in CHECK for backward compatibility but not used in green-path code.
        if (createSql?.Contains("'gateway_delivery'", StringComparison.OrdinalIgnoreCase) == true)
            return;

        await ExecuteNonQueryAsync(connection, RebuildChannelMessagesWithGatewayDeliverySourceKindSql, cancellationToken);
    }

    private static async Task EnsureChannelActivityEventsSchemaAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, ChannelActivityEventsSchemaSql, cancellationToken);
        await EnsureColumnAsync(connection, "channel_activity_events", "display_block_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_activity_events", "session_key", "TEXT", cancellationToken);
        // hermes_session_key / parent_hermes_session_key: REMOVED in v8 migration.
        // These columns are no longer added or maintained; v8 rebuild drops them.
        // Pre-v8 databases will have them dropped by the migration rebuild.
        await EnsureColumnAsync(connection, "channel_activity_events", "parent_session_key", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_activity_events", "parent_agent_identity", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_activity_events", "worker_run_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_activity_events", "worker_role", "TEXT", cancellationToken);
    }


    private static async Task EnsureChannelActivityEventsCompatibilityColumnsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(connection, "channel_activity_events", "delivery_stage", "TEXT NOT NULL DEFAULT 'progress'", cancellationToken);
        await EnsureColumnAsync(connection, "channel_activity_events", "terminal", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "channel_activity_events", "final_channel_message_id", "INTEGER", cancellationToken);
    }

    private static async Task EnsureChannelActivityEventsEventTypeConstraintAsync(SqliteConnection connection,
        ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        await RecoverChannelActivityEventsRebuildIfNeededAsync(connection, logger, cancellationToken);

        if (!await TableExistsAsync(connection, "channel_activity_events", cancellationToken))
            return;

        var createSql = await GetTableCreateSqlAsync(connection, "channel_activity_events", cancellationToken);
        if (createSql?.Contains("'agent_work_lifecycle'", StringComparison.OrdinalIgnoreCase) == true)
            return;

        await RebuildChannelActivityEventsForAgentWorkLifecycleAsync(connection, cancellationToken);
        await ExecuteNonQueryAsync(connection, ChannelActivityEventsIndexesSql, cancellationToken);
        logger?.LogInformation("Rebuilt channel_activity_events table with agent_work_lifecycle event_type support");
    }

    private static async Task RecoverChannelActivityEventsRebuildIfNeededAsync(SqliteConnection connection,
        ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(connection, "channel_activity_events__new", cancellationToken))
            return;

        if (await TableExistsAsync(connection, "channel_activity_events", cancellationToken))
        {
            await ExecuteNonQueryAsync(connection, "DROP TABLE channel_activity_events__new;", cancellationToken);
            logger?.LogWarning("Dropped stale channel_activity_events__new table before lifecycle event_type constraint rebuild");
            return;
        }

        await ExecuteNonQueryAsync(connection,
            "ALTER TABLE channel_activity_events__new RENAME TO channel_activity_events;",
            cancellationToken);
        await ExecuteNonQueryAsync(connection, ChannelActivityEventsIndexesSql, cancellationToken);
        logger?.LogWarning("Recovered channel_activity_events from channel_activity_events__new after interrupted lifecycle event_type constraint rebuild");
    }

    private static async Task RebuildChannelActivityEventsForAgentWorkLifecycleAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = OFF;", cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteNonQueryAsync(connection, RebuildChannelActivityEventsForAgentWorkLifecycleSql, transaction,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        }
    }

    private static async Task EnsureAssignmentCompatibilityColumnsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(connection, "channel_messages", "assignment_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "checkpoint_type", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "checkpoint_handle", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_activity_events", "assignment_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_activity_events", "checkpoint_type", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_activity_events", "checkpoint_handle", "TEXT", cancellationToken);
    }

    private static async Task EnsureSharedProfileInstanceColumnsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(connection, "channel_messages", "agent_instance_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "pool_member_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "session_owner_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "session_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_activity_events", "agent_instance_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_activity_events", "pool_member_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_read_cursors", "instance_id", "TEXT", cancellationToken);

        // Create indexes (safe: CREATE INDEX IF NOT EXISTS will fail only for
        // structural issues; column must exist via EnsureColumnAsync above)
        if (await TableExistsAsync(connection, "channel_activity_events", cancellationToken))
        {
            await ExecuteNonQueryAsync(connection, """
                CREATE INDEX IF NOT EXISTS idx_channel_activity_events_agent_instance
                    ON channel_activity_events(agent_instance_id, channel_id, id)
                    WHERE agent_instance_id IS NOT NULL;
                """, cancellationToken);
        }
        if (await TableExistsAsync(connection, "channel_messages", cancellationToken))
        {
            await ExecuteNonQueryAsync(connection, """
                CREATE INDEX IF NOT EXISTS idx_channel_messages_agent_instance
                    ON channel_messages(agent_instance_id, channel_id, id)
                    WHERE agent_instance_id IS NOT NULL;
                """, cancellationToken);
        }
        if (await TableExistsAsync(connection, "channel_read_cursors", cancellationToken))
        {
            await ExecuteNonQueryAsync(connection, """
                CREATE INDEX IF NOT EXISTS idx_channel_read_cursors_instance
                    ON channel_read_cursors(channel_id, reader_type, reader_identity, instance_id)
                    WHERE instance_id IS NOT NULL;
                """, cancellationToken);
        }
    }

    /// <summary>
    /// Ensures the channel_read_cursors UNIQUE constraint includes instance_id.
    /// For databases created before migration v2, the old constraint
    /// UNIQUE(channel_id, reader_type, reader_identity) prevents per-instance
    /// cursors. This method rebuilds the table with the new constraint and
    /// normalizes any NULL instance_ids to '' for proper SQLite uniqueness.
    /// </summary>
    private static async Task EnsureChannelReadCursorsInstanceConstraintAsync(SqliteConnection connection,
        ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(connection, "channel_read_cursors", cancellationToken))
            return;

        var createSql = await GetTableCreateSqlAsync(connection, "channel_read_cursors", cancellationToken);

        // Check if the new UNIQUE constraint already includes instance_id.
        // SQLite preserves DDL formatting from the original CREATE TABLE, so
        // tolerate both `UNIQUE(...)` and `UNIQUE (...)` spellings.
        if (createSql is not null && Regex.IsMatch(createSql,
                @"UNIQUE\s*\(\s*channel_id\s*,\s*reader_type\s*,\s*reader_identity\s*,\s*instance_id\s*\)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            // Constraint is already correct; just normalize any NULL instance_ids to ''
            await ExecuteNonQueryAsync(connection, """
                UPDATE channel_read_cursors
                SET instance_id = ''
                WHERE instance_id IS NULL;
                """, cancellationToken);
            return;
        }

        // Old constraint detected — rebuild table with instance_id in UNIQUE
        await ExecuteNonQueryAsync(connection, RebuildChannelReadCursorsForInstanceConstraintSql, cancellationToken);
        logger?.LogInformation("Rebuilt channel_read_cursors table with instance-scoped UNIQUE constraint");
    }

    private static async Task EnsureAgentCommonsSeedAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "channels", cancellationToken) ||
            !await TableExistsAsync(connection, "channel_memberships", cancellationToken))
        {
            return;
        }

        await ExecuteNonQueryAsync(connection, AgentCommonsSeedSql, cancellationToken);
    }

    private static async Task EnsureWorkerPoolLobbySeedAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "channels", cancellationToken))
            return;

        await ExecuteNonQueryAsync(connection, WorkerPoolLobbySeedSql, cancellationToken);
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

    private static async Task EnsureChannelsCompatibilityColumnsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(connection, "channels", "space_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channels", "created_by", "TEXT NOT NULL DEFAULT 'system'", cancellationToken);
        await EnsureColumnAsync(connection, "channels", "visibility", "TEXT NOT NULL DEFAULT 'normal'", cancellationToken);
        await EnsureColumnAsync(connection, "channels", "settings_json", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channels", "created_at", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "channels", "updated_at", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "channels", "archived_at", "TEXT", cancellationToken);
    }

    private static async Task EnsureChannelMessageCompatibilityColumnsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(connection, "channel_messages", "message_kind", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "source_kind", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "source_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "source_project_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "target_project_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "target_task_id", "INTEGER", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "worker_run_id", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "worker_role", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "profile_identity", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "summary", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "deep_link", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "thread_root_message_id", "INTEGER", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "reply_to_message_id", "INTEGER", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "metadata_json", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_messages", "delivery_request_id", "TEXT", cancellationToken);
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

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
            delivery_request_id    TEXT,
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
        CREATE INDEX IF NOT EXISTS idx_channel_messages_delivery_request
            ON channel_messages(delivery_request_id)
            WHERE delivery_request_id IS NOT NULL;
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

        CREATE TABLE IF NOT EXISTS channel_activity_events (
            id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id            INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            project_id            TEXT,
            agent_identity        TEXT NOT NULL,
            delivery_request_id   TEXT,
            -- session_key: runtime-neutral session ownership (was hermes_session_key).
            -- parent_session_key: runtime-neutral parent session (was parent_hermes_session_key).
            -- hermes_session_key / parent_hermes_session_key: DEPRECATED legacy columns kept
            -- for DB compatibility only; not used by active green-path queries (task #1967).
            session_key           TEXT,
            hermes_session_key    TEXT,
            display_block_id      TEXT,
            parent_session_key    TEXT,
            parent_hermes_session_key TEXT,
            parent_agent_identity TEXT,
            worker_run_id         TEXT,
            worker_role           TEXT,
            task_id               INTEGER,
            thread_id             INTEGER,
            anchor_message_id     INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            event_type            TEXT NOT NULL
                                  CHECK (event_type IN ('tool_call_started', 'tool_call_completed', 'tool_call_failed', 'lifecycle_status', 'aggregation_snapshot', 'run_summary', 'agent_work_lifecycle')),
            status                TEXT NOT NULL DEFAULT 'completed'
                                  CHECK (status IN ('started', 'completed', 'failed', 'interim', 'blocked')),
            delivery_stage        TEXT NOT NULL DEFAULT 'progress',
            terminal              INTEGER NOT NULL DEFAULT 0 CHECK (terminal IN (0, 1)),
            final_channel_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            sequence              INTEGER NOT NULL DEFAULT 0 CHECK (sequence >= 0),
            update_version        INTEGER NOT NULL DEFAULT 1 CHECK (update_version >= 1),
            title                 TEXT,
            summary               TEXT,
            preview_json          TEXT,
            metadata_json         TEXT,
            dedupe_key            TEXT,
            created_at            TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at            TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_channel_created
            ON channel_activity_events(channel_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_delivery
            ON channel_activity_events(delivery_request_id, sequence, id)
            WHERE delivery_request_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_session
            ON channel_activity_events(session_key, sequence, id)
            WHERE session_key IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_channel_activity_events_dedupe
            ON channel_activity_events(channel_id, dedupe_key)
            WHERE dedupe_key IS NOT NULL;

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

    /// <summary>
    /// Minimal channel_memberships schema for v8 migration backfill resilience.
    /// Used when channel_memberships doesn't exist on very old legacy DBs.
    /// </summary>
    private const string ChannelMembershipsMinimalSchemaSql = """
        CREATE TABLE IF NOT EXISTS channel_memberships (
            id                            INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id                    INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            member_type                   TEXT NOT NULL
                                          CHECK (member_type IN ('user', 'agent', 'role', 'group')),
            member_identity               TEXT NOT NULL,
            membership_status             TEXT NOT NULL DEFAULT 'active'
                                          CHECK (membership_status IN ('active', 'muted', 'left', 'banned')),
            wake_policy                   TEXT NOT NULL DEFAULT 'mentions_only',
            can_send                      INTEGER NOT NULL DEFAULT 1,
            can_react                     INTEGER NOT NULL DEFAULT 1,
            can_invite                    INTEGER NOT NULL DEFAULT 0,
            cooldown_seconds              INTEGER NOT NULL DEFAULT 60,
            max_auto_replies_per_window   INTEGER NOT NULL DEFAULT 1,
            settings_json                 TEXT,
            membership_purpose            TEXT,
            created_at                    TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at                    TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(channel_id, member_type, member_identity)
        );
        """;

    private const string ChannelActivityEventsSchemaSql = """
        CREATE TABLE IF NOT EXISTS channel_activity_events (
            id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id            INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            project_id            TEXT,
            agent_identity        TEXT NOT NULL,
            delivery_request_id   TEXT,
            session_key           TEXT,
            display_block_id      TEXT,
            parent_session_key    TEXT,
            parent_agent_identity TEXT,
            worker_run_id         TEXT,
            worker_role           TEXT,
            task_id               INTEGER,
            thread_id             INTEGER,
            anchor_message_id     INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            event_type            TEXT NOT NULL
                                  CHECK (event_type IN ('tool_call_started', 'tool_call_completed', 'tool_call_failed', 'lifecycle_status', 'aggregation_snapshot', 'run_summary', 'agent_work_lifecycle')),
            status                TEXT NOT NULL DEFAULT 'completed'
                                  CHECK (status IN ('started', 'completed', 'failed', 'interim', 'blocked')),
            delivery_stage        TEXT NOT NULL DEFAULT 'progress',
            terminal              INTEGER NOT NULL DEFAULT 0 CHECK (terminal IN (0, 1)),
            final_channel_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            sequence              INTEGER NOT NULL DEFAULT 0 CHECK (sequence >= 0),
            update_version        INTEGER NOT NULL DEFAULT 1 CHECK (update_version >= 1),
            title                 TEXT,
            summary               TEXT,
            preview_json          TEXT,
            metadata_json         TEXT,
            dedupe_key            TEXT,
            created_at            TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at            TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_channel_created
            ON channel_activity_events(channel_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_delivery
            ON channel_activity_events(delivery_request_id, sequence, id)
            WHERE delivery_request_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_session
            ON channel_activity_events(session_key, sequence, id)
            WHERE session_key IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_channel_activity_events_dedupe
            ON channel_activity_events(channel_id, dedupe_key)
            WHERE dedupe_key IS NOT NULL;
        """;

    private const string ChannelActivityEventsIndexesSql = """
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_channel_created
            ON channel_activity_events(channel_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_delivery
            ON channel_activity_events(delivery_request_id, sequence, id)
            WHERE delivery_request_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_session
            ON channel_activity_events(session_key, sequence, id)
            WHERE session_key IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_channel_activity_events_dedupe
            ON channel_activity_events(channel_id, dedupe_key)
            WHERE dedupe_key IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_display_block
            ON channel_activity_events(display_block_id, sequence, id)
            WHERE display_block_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_worker_run
            ON channel_activity_events(worker_run_id, sequence, id)
            WHERE worker_run_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_assignment
            ON channel_activity_events(assignment_id, channel_id, id)
            WHERE assignment_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_agent_instance
            ON channel_activity_events(agent_instance_id, channel_id, id)
            WHERE agent_instance_id IS NOT NULL;
        """;

    private const string RebuildChannelActivityEventsForAgentWorkLifecycleSql = """
        CREATE TABLE channel_activity_events__new (
            id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id            INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            project_id            TEXT,
            agent_identity        TEXT NOT NULL,
            delivery_request_id   TEXT,
            session_key           TEXT,
            display_block_id      TEXT,
            parent_session_key    TEXT,
            parent_agent_identity TEXT,
            worker_run_id         TEXT,
            worker_role           TEXT,
            agent_instance_id     TEXT,
            pool_member_id        TEXT,
            task_id               INTEGER,
            thread_id             INTEGER,
            anchor_message_id     INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            assignment_id         TEXT,
            checkpoint_type       TEXT,
            checkpoint_handle     TEXT,
            event_type            TEXT NOT NULL
                                  CHECK (event_type IN ('tool_call_started', 'tool_call_completed', 'tool_call_failed', 'lifecycle_status', 'aggregation_snapshot', 'run_summary', 'agent_work_lifecycle')),
            status                TEXT NOT NULL DEFAULT 'completed'
                                  CHECK (status IN ('started', 'completed', 'failed', 'interim', 'blocked')),
            delivery_stage        TEXT NOT NULL DEFAULT 'progress',
            terminal              INTEGER NOT NULL DEFAULT 0 CHECK (terminal IN (0, 1)),
            final_channel_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            sequence              INTEGER NOT NULL DEFAULT 0 CHECK (sequence >= 0),
            update_version        INTEGER NOT NULL DEFAULT 1 CHECK (update_version >= 1),
            title                 TEXT,
            summary               TEXT,
            preview_json          TEXT,
            metadata_json         TEXT,
            dedupe_key            TEXT,
            created_at            TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at            TEXT NOT NULL DEFAULT (datetime('now'))
        );

        INSERT INTO channel_activity_events__new(
            id, channel_id, project_id, agent_identity, delivery_request_id,
            session_key, display_block_id, parent_session_key,
            parent_agent_identity, worker_run_id, worker_role,
            agent_instance_id, pool_member_id, task_id, thread_id, anchor_message_id,
            assignment_id, checkpoint_type, checkpoint_handle, event_type, status,
            delivery_stage, terminal, final_channel_message_id, sequence, update_version,
            title, summary, preview_json, metadata_json, dedupe_key, created_at, updated_at)
        SELECT
            id,
            channel_id,
            project_id,
            agent_identity,
            delivery_request_id,
            COALESCE(session_key, hermes_session_key),
            display_block_id,
            COALESCE(parent_session_key, parent_hermes_session_key),
            parent_agent_identity,
            worker_run_id,
            worker_role,
            agent_instance_id,
            pool_member_id,
            task_id,
            thread_id,
            anchor_message_id,
            assignment_id,
            checkpoint_type,
            checkpoint_handle,
            event_type,
            COALESCE(status, 'completed'),
            COALESCE(delivery_stage, 'progress'),
            COALESCE(terminal, 0),
            final_channel_message_id,
            COALESCE(sequence, 0),
            COALESCE(update_version, 1),
            title,
            summary,
            preview_json,
            metadata_json,
            dedupe_key,
            COALESCE(created_at, datetime('now')),
            COALESCE(updated_at, datetime('now'))
        FROM channel_activity_events
        ORDER BY id;

        DROP TABLE channel_activity_events;
        ALTER TABLE channel_activity_events__new RENAME TO channel_activity_events;
        """;

    private const string AgentCommonsSeedSql = """
        INSERT INTO channels(slug, display_name, kind, created_by, visibility, settings_json)
        VALUES ('agent-commons', 'Agent Commons', 'system', 'system', 'normal', '{"systemManaged":true,"channelRole":"agent_commons","defaultWakePolicy":"mentions_only"}')
        ON CONFLICT(slug) DO UPDATE SET
            display_name = 'Agent Commons',
            kind = 'system',
            visibility = 'normal',
            settings_json = COALESCE(channels.settings_json, excluded.settings_json),
            updated_at = datetime('now');

        INSERT INTO channel_memberships(
            channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
            cooldown_seconds, max_auto_replies_per_window, settings_json, membership_purpose)
        SELECT commons.id, 'agent', agents.member_identity, 'active', 'mentions_only', 1, 1, 0, 60, 1,
            '{"systemManaged":true,"source":"agent-commons-backfill"}', 'agent_commons'
        FROM channels commons
        JOIN (
            SELECT DISTINCT member_identity
            FROM channel_memberships
            WHERE member_type = 'agent'
              AND membership_status = 'active'
              AND member_identity IS NOT NULL
              AND trim(member_identity) <> ''
        ) agents
        WHERE commons.slug = 'agent-commons'
        ON CONFLICT(channel_id, member_type, member_identity) DO UPDATE SET
            membership_status = CASE
                WHEN channel_memberships.settings_json LIKE '%"systemManaged":true%' THEN 'active'
                ELSE channel_memberships.membership_status
            END,
            wake_policy = CASE
                WHEN channel_memberships.settings_json LIKE '%"systemManaged":true%' THEN 'mentions_only'
                ELSE channel_memberships.wake_policy
            END,
            membership_purpose = 'agent_commons',
            updated_at = datetime('now');
        """;

    /// <summary>
    /// Rebuild channel_read_cursors with instance-scoped UNIQUE constraint
    /// and normalize NULL instance_ids to '' for proper SQLite uniqueness.
    /// Preserves existing cursor data.
    /// </summary>
    private const string RebuildChannelReadCursorsForInstanceConstraintSql = """
        PRAGMA foreign_keys = OFF;
        CREATE TABLE channel_read_cursors__new (
            id                         INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id                 INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            reader_type                TEXT NOT NULL CHECK (reader_type IN ('user', 'agent', 'role', 'group')),
            reader_identity            TEXT NOT NULL,
            instance_id                TEXT NOT NULL DEFAULT '',
            last_read_channel_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            last_read_at               TEXT NOT NULL DEFAULT (datetime('now')),
            created_at                 TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at                 TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(channel_id, reader_type, reader_identity, instance_id)
        );

        INSERT INTO channel_read_cursors__new(
            id, channel_id, reader_type, reader_identity, instance_id,
            last_read_channel_message_id, last_read_at, created_at, updated_at)
        SELECT
            id, channel_id, reader_type, reader_identity, COALESCE(instance_id, ''),
            last_read_channel_message_id, last_read_at, created_at, updated_at
        FROM channel_read_cursors
        ORDER BY id;

        DROP TABLE channel_read_cursors;
        ALTER TABLE channel_read_cursors__new RENAME TO channel_read_cursors;
        PRAGMA foreign_keys = ON;
        """;

    private const string PostCreateIndexesSql = """"
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
        CREATE INDEX IF NOT EXISTS idx_channel_messages_delivery_request
            ON channel_messages(delivery_request_id)
            WHERE delivery_request_id IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_channel_messages_dedupe
            ON channel_messages(channel_id, dedupe_key)
            WHERE dedupe_key IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_display_block
            ON channel_activity_events(display_block_id, sequence, id)
            WHERE display_block_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_worker_run
            ON channel_activity_events(worker_run_id, sequence, id)
            WHERE worker_run_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_messages_assignment
            ON channel_messages(assignment_id, channel_id, id)
            WHERE assignment_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_assignment
            ON channel_activity_events(assignment_id, channel_id, id)
            WHERE assignment_id IS NOT NULL;
        """";

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
            target_project_id      TEXT,
            target_task_id         INTEGER,
            worker_run_id          TEXT,
            worker_role            TEXT,
            profile_identity       TEXT,
            summary                TEXT,
            deep_link              TEXT,
            thread_root_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            reply_to_message_id    INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            metadata_json          TEXT,
            delivery_request_id    TEXT,
            dedupe_key             TEXT,
            assignment_id          TEXT,
            checkpoint_type        TEXT,
            checkpoint_handle      TEXT,
            agent_instance_id      TEXT,
            pool_member_id         TEXT,
            session_owner_id       TEXT,
            session_id             TEXT,
            created_at             TEXT NOT NULL DEFAULT (datetime('now')),
            edited_at              TEXT,
            deleted_at             TEXT
        );

        INSERT INTO channel_messages__new(
            id, channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id,
            source_project_id, target_project_id, target_task_id, worker_run_id, worker_role,
            profile_identity, summary, deep_link, thread_root_message_id, reply_to_message_id,
            metadata_json, delivery_request_id, dedupe_key,
            assignment_id, checkpoint_type, checkpoint_handle,
            agent_instance_id, pool_member_id, session_owner_id, session_id,
            created_at, edited_at, deleted_at)
        SELECT
            id,
            channel_id,
            sender_type,
            sender_identity,
            body,
            COALESCE(message_kind, CASE WHEN sender_type = 'agent' THEN 'agent_text' ELSE 'human_text' END),
            CASE WHEN source_kind = 'gateway_delivery' THEN 'external_adapter_message' ELSE source_kind END,
            source_id,
            source_project_id,
            target_project_id,
            target_task_id,
            worker_run_id,
            worker_role,
            profile_identity,
            summary,
            deep_link,
            thread_root_message_id,
            reply_to_message_id,
            metadata_json,
            delivery_request_id,
            dedupe_key,
            assignment_id,
            checkpoint_type,
            checkpoint_handle,
            agent_instance_id,
            pool_member_id,
            session_owner_id,
            session_id,
            COALESCE(created_at, datetime('now')),
            edited_at,
            deleted_at
        FROM channel_messages
        ORDER BY id;

        DROP TABLE channel_messages;
        ALTER TABLE channel_messages__new RENAME TO channel_messages;
        PRAGMA foreign_keys = ON;
        """;

    /// <summary>
    /// Migration v2: Add shared-profile concrete agent-instance support.
    /// Ensures channel_read_cursors table exists with instance_id column
    /// and new UNIQUE constraint for per-instance tracking.
    /// Schema compatibility columns are handled idempotently by
    /// EnsureSharedProfileInstanceColumnsAsync in C#.
    /// Note: This only creates the table if missing. When the table already
    /// exists (from V1 schema), instance_id is added via EnsureColumnAsync.
    /// </summary>
    private const string MigrationV2Sql = """"
        -- Ensure channel_read_cursors table exists with instance_id support
        -- (may not exist in legacy schema v1 databases)
        CREATE TABLE IF NOT EXISTS channel_read_cursors (
            id                         INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id                 INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            reader_type                TEXT NOT NULL CHECK (reader_type IN ('user', 'agent', 'role', 'group')),
            reader_identity            TEXT NOT NULL,
            instance_id                TEXT,
            last_read_channel_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            last_read_at               TEXT NOT NULL DEFAULT (datetime('now')),
            created_at                 TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at                 TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(channel_id, reader_type, reader_identity, instance_id)
        );

        -- Indexes for agent_instance_id lookup (safe: CREATE INDEX IF NOT EXISTS
        -- won't fail if column doesn't exist yet, but better to create from C# code
        -- that runs after EnsureColumnAsync. No index creation here to avoid
        -- 'no such column' errors when the table already exists without instance_id.)
        """";

    /// <summary>
    /// Migration v3: Worker-pool lobby presence table.
    /// Tracks visible lobby status for worker-pool members with
    /// concrete instance/member IDs and lifecycle status transitions.
    /// </summary>
    private const string MigrationV3Sql = """"
        CREATE TABLE IF NOT EXISTS worker_pool_lobby_presence (
            id                      INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id              INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            member_identity         TEXT NOT NULL,
            agent_instance_id       TEXT,
            pool_member_id          TEXT,
            concrete_identity       TEXT NOT NULL DEFAULT '',
            profile                 TEXT,
            role                    TEXT,
            status                  TEXT NOT NULL DEFAULT 'idle'
                                    CHECK (status IN ('idle', 'leased', 'draining', 'released', 'quarantined', 'offline')),
            current_assignment_id   TEXT,
            current_task_id         TEXT,
            current_project_id      TEXT,
            last_activity_at        TEXT,
            release_acknowledged    INTEGER NOT NULL DEFAULT 0 CHECK (release_acknowledged IN (0, 1)),
            created_at              TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at              TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(channel_id, member_identity, concrete_identity)
        );

        CREATE INDEX IF NOT EXISTS idx_wp_lobby_presence_status
            ON worker_pool_lobby_presence(channel_id, status);

        CREATE INDEX IF NOT EXISTS idx_wp_lobby_presence_role_profile
            ON worker_pool_lobby_presence(channel_id, role, profile)
            WHERE status = 'idle';
        """";

    /// <summary>
    /// Migration v4: Add membership_purpose column to channel_memberships.
    /// Distinguishes why a member is in a channel: worker_pool_control (idle pool home),
    /// target_work (active assignment participation), agent_commons (system commons).
    /// NULL = legacy/unspecified purpose.
    /// </summary>
    private const string MigrationV4Sql = """"
        -- membership_purpose column added via C# EnsureColumnAsync (safe for legacy DBs)
        -- See EnsureChannelMembershipsMembershipPurposeColumnAsync
        """";

    /// <summary>
    /// Seed SQL to ensure the #worker-pool lobby channel exists.
    /// Uses slug='worker-pool' with kind='system' for simplicity;
    /// distinct from agent-commons via slug.
    /// </summary>
    private const string WorkerPoolLobbySeedSql = """"
        INSERT INTO channels(slug, display_name, kind, created_by, visibility, settings_json)
        VALUES ('worker-pool', '#worker-pool', 'system', 'system', 'normal',
                '{"systemManaged":true,"channelRole":"worker_pool_lobby","description":"Worker-pool lobby: visible home lane for worker-pool orchestration. Idle = available. Status transitions: idle → leased → draining → released → idle."}')
        ON CONFLICT(slug) DO UPDATE SET
            display_name = '#worker-pool',
            kind = 'system',
            visibility = 'normal',
            settings_json = COALESCE(channels.settings_json, excluded.settings_json),
            updated_at = datetime('now');
        """";

    /// <summary>
    /// Worker-pool lobby presence table schema (for self-contained creation).
    /// </summary>
    private const string WorkerPoolLobbyPresenceSchemaSql = """"
        CREATE TABLE IF NOT EXISTS worker_pool_lobby_presence (
            id                      INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id              INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            member_identity         TEXT NOT NULL,
            agent_instance_id       TEXT,
            pool_member_id          TEXT,
            concrete_identity       TEXT NOT NULL DEFAULT '',
            profile                 TEXT,
            role                    TEXT,
            status                  TEXT NOT NULL DEFAULT 'idle'
                                    CHECK (status IN ('idle', 'leased', 'draining', 'released', 'quarantined', 'offline')),
            current_assignment_id   TEXT,
            current_task_id         TEXT,
            current_project_id      TEXT,
            last_activity_at        TEXT,
            release_acknowledged    INTEGER NOT NULL DEFAULT 0 CHECK (release_acknowledged IN (0, 1)),
            created_at              TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at              TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(channel_id, member_identity, concrete_identity)
        );

        CREATE INDEX IF NOT EXISTS idx_wp_lobby_presence_status
            ON worker_pool_lobby_presence(channel_id, status);

        CREATE INDEX IF NOT EXISTS idx_wp_lobby_presence_role_profile
            ON worker_pool_lobby_presence(channel_id, role, profile)
            WHERE status = 'idle';
        """";

    /// <summary>
    /// Ensures the worker_pool_lobby_presence concrete_identity column exists
    /// and the UNIQUE constraint includes concrete_identity instead of only
    /// (channel_id, member_identity). For databases created before this fix,
    /// the old constraint prevents multiple workers with the same member_identity
    /// from joining the lobby. This method rebuilds the table with the new
    /// constraint and normalizes concrete_identity values.
    /// concrete_identity is computed as: pool_member_id when present, else
    /// agent_instance_id when present, else '' for non-pool fallback.
    /// </summary>
    private static async Task EnsureWorkerPoolLobbyPresenceConcreteConstraintAsync(SqliteConnection connection,
        ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(connection, "worker_pool_lobby_presence", cancellationToken))
            return;

        var createSql = await GetTableCreateSqlAsync(connection, "worker_pool_lobby_presence", cancellationToken);

        // Check if the new UNIQUE constraint already includes concrete_identity.
        // Tolerate both UNIQUE(...) and UNIQUE (...) spellings.
        if (createSql is not null && Regex.IsMatch(createSql,
                @"UNIQUE\s*\(\s*channel_id\s*,\s*member_identity\s*,\s*concrete_identity\s*\)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            // Constraint is already correct; ensure concrete_identity column exists
            await EnsureColumnAsync(connection, "worker_pool_lobby_presence",
                "concrete_identity", "TEXT NOT NULL DEFAULT ''", cancellationToken);
            // Normalize any NULL concrete_identity values (from old data)
            await ExecuteNonQueryAsync(connection, """
                UPDATE worker_pool_lobby_presence
                SET concrete_identity = COALESCE(pool_member_id, agent_instance_id, '')
                WHERE concrete_identity IS NULL OR concrete_identity = '';
                """, cancellationToken);
            return;
        }

        // Old constraint detected — rebuild table with concrete_identity in UNIQUE
        await ExecuteNonQueryAsync(connection, RebuildWorkerPoolLobbyPresenceForConcreteConstraintSql, cancellationToken);
        logger?.LogInformation("Rebuilt worker_pool_lobby_presence table with concrete-identity-scoped UNIQUE constraint");
    }

    /// <summary>
    /// Ensures the membership_purpose column exists on channel_memberships.
    /// Safe for legacy databases where channel_memberships may not exist yet.
    /// Uses EnsureColumnAsync which only adds the column if the table exists.
    /// CHECK constraint is not enforced via this path (SQLite doesn't support
    /// ALTER TABLE ADD COLUMN with CHECK in a backward-compatible way for
    /// existing tables); validation is enforced at the application layer.
    /// </summary>
    private static async Task EnsureChannelMembershipsMembershipPurposeColumnAsync(SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        await EnsureColumnAsync(connection, "channel_memberships", "membership_purpose", "TEXT", cancellationToken);
    }

    /// <summary>
    /// Ensures v8 membership columns (profile_identity, member_role, left_at)
    /// exist on channel_memberships. Safe for pre-v8 databases.
    /// </summary>
    private static async Task EnsureChannelMembershipsV8ColumnsAsync(SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(connection, "channel_memberships", cancellationToken))
            return;

        await EnsureColumnAsync(connection, "channel_memberships", "profile_identity", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_memberships", "member_role", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "channel_memberships", "left_at", "TEXT", cancellationToken);
    }

    /// <summary>
    /// Migration v5: Channel-project links table.
    /// Allows many projects to share one operations channel (e.g., den-system)
    /// while each project retains its own default channel.
    /// </summary>
    private const string MigrationV5Sql = """
        CREATE TABLE IF NOT EXISTS channel_project_links (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id    INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            project_id    TEXT NOT NULL,
            relation_kind TEXT NOT NULL DEFAULT 'linked',
            is_primary    INTEGER NOT NULL DEFAULT 0 CHECK (is_primary IN (0, 1)),
            created_at    TEXT NOT NULL DEFAULT (datetime('now')),
            settings_json TEXT,
            UNIQUE(channel_id, project_id)
        );

        CREATE INDEX IF NOT EXISTS idx_channel_project_links_channel
            ON channel_project_links(channel_id);
        CREATE INDEX IF NOT EXISTS idx_channel_project_links_project
            ON channel_project_links(project_id);
        """;

    /// <summary>
    /// Migration v6: Direct-agent DM transcript read-model tables.
    /// direct_conversations: stable human+agent conversation key with metadata.
    /// direct_conversation_entries: read-model entries linking to canonical channel_messages.
    /// direct_conversation_read_cursors: per-reader unread state for the DM sidebar.
    /// These are a focused readback/index over canonical channel_messages, not a
    /// separate session/delivery lane. Do NOT derive Hermes session keys from
    /// direct_conversation_id.
    /// </summary>
    private const string MigrationV6Sql = """
        CREATE TABLE IF NOT EXISTS direct_conversations (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            human_identity      TEXT NOT NULL,
            agent_identity      TEXT NOT NULL,
            scope_project_id    TEXT,
            display_title       TEXT,
            is_archived         INTEGER NOT NULL DEFAULT 0 CHECK (is_archived IN (0, 1)),
            is_muted            INTEGER NOT NULL DEFAULT 0 CHECK (is_muted IN (0, 1)),
            settings_json       TEXT,
            last_entry_at       TEXT,
            last_entry_preview  TEXT,
            last_entry_sender   TEXT,
            created_at          TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at          TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(human_identity, agent_identity)
        );

        CREATE INDEX IF NOT EXISTS idx_direct_conversations_human
            ON direct_conversations(human_identity, last_entry_at DESC);

        CREATE TABLE IF NOT EXISTS direct_conversation_entries (
            id                      INTEGER PRIMARY KEY AUTOINCREMENT,
            conversation_id         INTEGER NOT NULL REFERENCES direct_conversations(id) ON DELETE CASCADE,
            channel_message_id      INTEGER NOT NULL REFERENCES channel_messages(id) ON DELETE CASCADE,
            direction               TEXT NOT NULL CHECK (direction IN ('human_to_agent', 'agent_to_human', 'system_note')),
            sender_identity         TEXT NOT NULL,
            recipient_identity      TEXT NOT NULL,
            source_channel_id       INTEGER,
            source_project_id       TEXT,
            source_task_id          INTEGER,
            source_session_owner_id TEXT,
            source_worker_run_id    TEXT,
            body_preview            TEXT,
            created_at              TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_direct_conversation_entries_conv
            ON direct_conversation_entries(conversation_id, id);
        CREATE INDEX IF NOT EXISTS idx_direct_conversation_entries_message
            ON direct_conversation_entries(channel_message_id);

        CREATE TABLE IF NOT EXISTS direct_conversation_read_cursors (
            id                      INTEGER PRIMARY KEY AUTOINCREMENT,
            conversation_id         INTEGER NOT NULL REFERENCES direct_conversations(id) ON DELETE CASCADE,
            reader_identity         TEXT NOT NULL,
            last_read_entry_id      INTEGER REFERENCES direct_conversation_entries(id) ON DELETE SET NULL,
            last_read_at            TEXT NOT NULL DEFAULT (datetime('now')),
            created_at              TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at              TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(conversation_id, reader_identity)
        );

        CREATE INDEX IF NOT EXISTS idx_direct_conversation_read_cursors_reader
            ON direct_conversation_read_cursors(reader_identity, conversation_id);
        """;

    /// <summary>
    /// Migration v7: FTS5 full-text search index for channel_messages.body.
    /// Creates a virtual FTS5 table with AFTER INSERT/UPDATE/DELETE triggers
    /// to keep the index in sync with the base table.
    /// Existing non-deleted messages are backfilled on creation.
    /// </summary>
    private const string MigrationV7Sql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS channel_messages_fts USING fts5(
            body
        );

        CREATE TRIGGER IF NOT EXISTS channel_messages_fts_insert AFTER INSERT ON channel_messages
        BEGIN
            INSERT INTO channel_messages_fts(rowid, body) VALUES (new.id, new.body);
        END;

        CREATE TRIGGER IF NOT EXISTS channel_messages_fts_delete AFTER DELETE ON channel_messages
        BEGIN
            INSERT INTO channel_messages_fts(channel_messages_fts, rowid, body) VALUES('delete', old.id, old.body);
        END;

        CREATE TRIGGER IF NOT EXISTS channel_messages_fts_update AFTER UPDATE ON channel_messages
        BEGIN
            INSERT INTO channel_messages_fts(channel_messages_fts, rowid, body) VALUES('delete', old.id, old.body);
            INSERT INTO channel_messages_fts(rowid, body) VALUES (new.id, new.body);
        END;

        -- Backfill existing non-deleted messages into the FTS index
        INSERT INTO channel_messages_fts(rowid, body)
        SELECT id, body FROM channel_messages WHERE deleted_at IS NULL;
        """;

    /// <summary>
    /// Migration v8: membership/subscription split, Hermes column purge,
    /// gateway_delivery quarantine.
    ///
    /// Adds first-class channel_subscriptions and channel_subscription_cursors tables.
    /// Backfills subscriptions from existing membership_purpose membership rows.
    /// Rebuilds channel_activity_events without hermes_session_key / parent_hermes_session_key.
    /// Migrates gateway_delivery source_kind rows to external_adapter_message and removes
    /// gateway_delivery from the green-path CHECK constraint.
    /// </summary>
    private const string MigrationV8Sql = """
        -- =====================================================================
        -- PART 1: Create channel_subscriptions table
        -- =====================================================================
        CREATE TABLE IF NOT EXISTS channel_subscriptions (
            id                      INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id              INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            membership_id           INTEGER REFERENCES channel_memberships(id) ON DELETE SET NULL,

            member_type             TEXT NOT NULL,
            member_identity         TEXT NOT NULL,
            profile_identity        TEXT,
            agent_instance_id       TEXT,
            pool_member_id          TEXT,

            subscription_identity   TEXT NOT NULL,
            subscription_purpose    TEXT NOT NULL,
            subscription_status     TEXT NOT NULL DEFAULT 'active'
                                    CHECK (subscription_status IN ('active','idle','busy','degraded','offline','left','released','quarantined','needs_rebind')),

            source_project_id       TEXT,
            target_project_id       TEXT,
            target_task_id          INTEGER,
            assignment_id           TEXT,
            worker_run_id           TEXT,
            worker_role             TEXT,

            session_owner_id        TEXT,
            session_id              TEXT,

            wake_policy_override    TEXT,
            last_seen_at            TEXT,
            last_claimed_at         TEXT,
            degraded_reason         TEXT,
            settings_json           TEXT,
            created_at              TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at              TEXT NOT NULL DEFAULT (datetime('now')),

            UNIQUE(channel_id, subscription_identity)
        );

        CREATE INDEX IF NOT EXISTS idx_channel_subscriptions_channel
            ON channel_subscriptions(channel_id, subscription_status);
        CREATE INDEX IF NOT EXISTS idx_channel_subscriptions_member
            ON channel_subscriptions(member_type, member_identity, subscription_status);
        CREATE INDEX IF NOT EXISTS idx_channel_subscriptions_instance
            ON channel_subscriptions(agent_instance_id)
            WHERE agent_instance_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_subscriptions_pool
            ON channel_subscriptions(pool_member_id)
            WHERE pool_member_id IS NOT NULL;

        -- =====================================================================
        -- PART 2: Create channel_subscription_cursors table
        -- =====================================================================
        CREATE TABLE IF NOT EXISTS channel_subscription_cursors (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            subscription_id     INTEGER NOT NULL REFERENCES channel_subscriptions(id) ON DELETE CASCADE,
            stream_kind         TEXT NOT NULL
                                CHECK (stream_kind IN ('channel_messages','direct_agent_events','activity_events','agent_work_lifecycle')),
            last_seen_id        INTEGER NOT NULL DEFAULT 0,
            last_seen_at        TEXT,
            cursor_json         TEXT,
            created_at          TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at          TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(subscription_id, stream_kind)
        );

        CREATE INDEX IF NOT EXISTS idx_channel_subscription_cursors_subscription
            ON channel_subscription_cursors(subscription_id);

        -- =====================================================================
        -- PART 3: Add membership v8 columns (profile_identity, member_role, left_at)
        -- =====================================================================
        -- These are additive; ALTER TABLE ADD COLUMN is safe for existing rows.
        -- Use idempotent column checks via the helper pattern.

        -- =====================================================================
        -- PART 4: Backfill subscriptions from existing membership rows
        -- The backfill is idempotent: ON CONFLICT(channel_id, subscription_identity) DO NOTHING.
        -- subscription_identity is derived from member + purpose for backfill safety.
        -- =====================================================================
        INSERT OR IGNORE INTO channel_subscriptions(
            channel_id, membership_id,
            member_type, member_identity, profile_identity,
            subscription_identity, subscription_purpose, subscription_status,
            settings_json)
        SELECT
            m.channel_id,
            m.id,
            m.member_type,
            m.member_identity,
            COALESCE(
                json_extract(m.settings_json, '$.profile'),
                CASE WHEN m.member_type = 'agent' THEN m.member_identity ELSE NULL END
            ),
            CASE
                WHEN m.membership_purpose IS NOT NULL AND m.membership_purpose != ''
                    THEN m.member_type || ':' || m.member_identity || ':' || m.membership_purpose
                ELSE m.member_type || ':' || m.member_identity || ':ordinary_channel'
            END,
            CASE
                WHEN m.membership_purpose IS NULL OR m.membership_purpose = '' THEN 'ordinary_channel'
                ELSE m.membership_purpose
            END,
            CASE m.membership_status
                WHEN 'active' THEN 'active'
                WHEN 'muted' THEN 'idle'
                WHEN 'left' THEN 'left'
                WHEN 'banned' THEN 'quarantined'
                ELSE 'active'
            END,
            m.settings_json
        FROM channel_memberships m
        WHERE m.membership_status = 'active'
          AND NOT EXISTS (
              SELECT 1 FROM channel_subscriptions cs
              WHERE cs.channel_id = m.channel_id
                AND cs.subscription_identity = (
                    CASE
                        WHEN m.membership_purpose IS NOT NULL AND m.membership_purpose != ''
                            THEN m.member_type || ':' || m.member_identity || ':' || m.membership_purpose
                        ELSE m.member_type || ':' || m.member_identity || ':ordinary_channel'
                    END
                )
          );

        -- =====================================================================
        -- PART 5: Backfill subscription cursors from active read cursors
        -- Only for agent instance-scoped cursors (instance_id is non-empty).
        -- Human/UI read cursors stay in channel_read_cursors.
        -- =====================================================================
        INSERT OR IGNORE INTO channel_subscription_cursors(
            subscription_id, stream_kind, last_seen_id, last_seen_at)
        SELECT
            cs.id,
            'channel_messages',
            COALESCE(crc.last_read_channel_message_id, 0),
            crc.last_read_at
        FROM channel_read_cursors crc
        JOIN channel_subscriptions cs
            ON cs.channel_id = crc.channel_id
           AND cs.member_type = crc.reader_type
           AND cs.member_identity = crc.reader_identity
        WHERE crc.instance_id IS NOT NULL
          AND crc.instance_id != ''
          AND crc.last_read_channel_message_id IS NOT NULL
          AND NOT EXISTS (
              SELECT 1 FROM channel_subscription_cursors csc
              WHERE csc.subscription_id = cs.id
                AND csc.stream_kind = 'channel_messages'
          );

        -- =====================================================================
        -- PART 6: Rebuild channel_activity_events without Hermes-named columns
        -- Preserve hermes_session_key values into session_key,
        -- parent_hermes_session_key into parent_session_key,
        -- then drop the legacy columns.
        -- =====================================================================
        -- First, migrate values from hermes_session_key to session_key and
        -- from parent_hermes_session_key to parent_session_key where the
        -- runtime-neutral column is empty.
        UPDATE channel_activity_events
        SET session_key = hermes_session_key
        WHERE (session_key IS NULL OR session_key = '')
          AND hermes_session_key IS NOT NULL
          AND hermes_session_key != '';

        UPDATE channel_activity_events
        SET parent_session_key = parent_hermes_session_key
        WHERE (parent_session_key IS NULL OR parent_session_key = '')
          AND parent_hermes_session_key IS NOT NULL
          AND parent_hermes_session_key != '';

        -- Now rebuild the table without hermes_session_key and parent_hermes_session_key.
        -- Use the same rebuild pattern as prior migrations.
        PRAGMA foreign_keys = OFF;

        CREATE TABLE channel_activity_events__v8 (
            id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id            INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            project_id            TEXT,
            agent_identity        TEXT NOT NULL,
            delivery_request_id   TEXT,
            session_key           TEXT,
            display_block_id      TEXT,
            parent_session_key    TEXT,
            parent_agent_identity TEXT,
            worker_run_id         TEXT,
            worker_role           TEXT,
            agent_instance_id     TEXT,
            pool_member_id        TEXT,
            task_id               INTEGER,
            thread_id             INTEGER,
            anchor_message_id     INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            assignment_id         TEXT,
            checkpoint_type       TEXT,
            checkpoint_handle     TEXT,
            event_type            TEXT NOT NULL
                                  CHECK (event_type IN ('tool_call_started','tool_call_completed','tool_call_failed','lifecycle_status','aggregation_snapshot','run_summary','agent_work_lifecycle')),
            status                TEXT NOT NULL DEFAULT 'completed'
                                  CHECK (status IN ('started','completed','failed','interim','blocked')),
            delivery_stage        TEXT NOT NULL DEFAULT 'progress',
            terminal              INTEGER NOT NULL DEFAULT 0 CHECK (terminal IN (0, 1)),
            final_channel_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
            sequence              INTEGER NOT NULL DEFAULT 0 CHECK (sequence >= 0),
            update_version        INTEGER NOT NULL DEFAULT 1 CHECK (update_version >= 1),
            title                 TEXT,
            summary               TEXT,
            preview_json          TEXT,
            metadata_json         TEXT,
            dedupe_key            TEXT,
            created_at            TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at            TEXT NOT NULL DEFAULT (datetime('now'))
        );

        INSERT INTO channel_activity_events__v8(
            id, channel_id, project_id, agent_identity, delivery_request_id,
            session_key, display_block_id, parent_session_key,
            parent_agent_identity, worker_run_id, worker_role,
            agent_instance_id, pool_member_id, task_id, thread_id, anchor_message_id,
            assignment_id, checkpoint_type, checkpoint_handle, event_type, status,
            delivery_stage, terminal, final_channel_message_id, sequence, update_version,
            title, summary, preview_json, metadata_json, dedupe_key, created_at, updated_at)
        SELECT
            id, channel_id, project_id, agent_identity, delivery_request_id,
            COALESCE(session_key, hermes_session_key),
            display_block_id,
            COALESCE(parent_session_key, parent_hermes_session_key),
            parent_agent_identity, worker_run_id, worker_role,
            agent_instance_id, pool_member_id, task_id, thread_id, anchor_message_id,
            assignment_id, checkpoint_type, checkpoint_handle, event_type,
            COALESCE(status, 'completed'),
            COALESCE(delivery_stage, 'progress'),
            COALESCE(terminal, 0),
            final_channel_message_id,
            COALESCE(sequence, 0),
            COALESCE(update_version, 1),
            title, summary, preview_json, metadata_json, dedupe_key,
            COALESCE(created_at, datetime('now')),
            COALESCE(updated_at, datetime('now'))
        FROM channel_activity_events
        ORDER BY id;

        DROP TABLE channel_activity_events;
        ALTER TABLE channel_activity_events__v8 RENAME TO channel_activity_events;

        -- Recreate indexes on the rebuilt table
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_channel_created
            ON channel_activity_events(channel_id, created_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_delivery
            ON channel_activity_events(delivery_request_id, sequence, id)
            WHERE delivery_request_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_session
            ON channel_activity_events(session_key, sequence, id)
            WHERE session_key IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_channel_activity_events_dedupe
            ON channel_activity_events(channel_id, dedupe_key)
            WHERE dedupe_key IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_display_block
            ON channel_activity_events(display_block_id, sequence, id)
            WHERE display_block_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_worker_run
            ON channel_activity_events(worker_run_id, sequence, id)
            WHERE worker_run_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_assignment
            ON channel_activity_events(assignment_id, channel_id, id)
            WHERE assignment_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_channel_activity_events_agent_instance
            ON channel_activity_events(agent_instance_id, channel_id, id)
            WHERE agent_instance_id IS NOT NULL;

        -- =====================================================================
        -- PART 7: gateway_delivery quarantined as historical/tombstone.
        -- Rows are migrated to external_adapter_message but gateway_delivery
        -- is kept in the CHECK constraint for backward compatibility.
        -- New green-path code must not write gateway_delivery.
        -- =====================================================================
        UPDATE channel_messages
        SET source_kind = 'external_adapter_message'
        WHERE source_kind = 'gateway_delivery';
        """;

    /// <summary>
    /// Ensures the den-system shared operations channel exists and is linked
    /// to Den constellation projects. Idempotent — safe to run on every startup.
    /// </summary>
    private static async Task EnsureDenSystemChannelSeedAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "channels", cancellationToken) ||
            !await TableExistsAsync(connection, "channel_project_links", cancellationToken))
        {
            return;
        }

        await ExecuteNonQueryAsync(connection, DenSystemChannelSeedSql, cancellationToken);
    }

    /// <summary>
    /// Ensures the channel_messages_fts FTS5 virtual table and triggers exist
    /// (idempotent — migration v7). Called on every startup for robustness.
    /// </summary>
    private static async Task EnsureChannelMessagesFts5Async(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "channel_messages", cancellationToken))
            return;

        // Create FTS5 virtual table if missing (idempotent)
        if (!await TableExistsAsync(connection, "channel_messages_fts", cancellationToken))
        {
            await ExecuteNonQueryAsync(connection, MigrationV7Sql, cancellationToken);
        }
    }

    /// <summary>
    /// Seed SQL to create the den-system shared operations channel and link
    /// Den constellation projects to it. The channel uses slug='den-system' with
    /// kind='system'. Each project gets a linked relation; the first project
    /// is marked as primary.
    /// </summary>
    private const string DenSystemChannelSeedSql = """
        INSERT INTO channels(slug, display_name, kind, created_by, visibility, settings_json)
        VALUES ('den-system', '#den-system', 'system', 'system', 'normal',
                '{"systemManaged":true,"channelRole":"den_system_ops","description":"Shared operations channel for Den constellation projects (den-core, den-mcp, den-channels, den-host, etc.)."}')
        ON CONFLICT(slug) DO UPDATE SET
            display_name = '#den-system',
            kind = 'system',
            visibility = 'normal',
            settings_json = COALESCE(channels.settings_json, excluded.settings_json),
            updated_at = datetime('now');

        INSERT INTO channel_project_links(channel_id, project_id, relation_kind, is_primary)
        SELECT c.id, 'den-core', 'linked', 1
        FROM channels c WHERE c.slug = 'den-system'
        ON CONFLICT(channel_id, project_id) DO UPDATE SET
            is_primary = CASE WHEN excluded.is_primary = 1 THEN 1 ELSE channel_project_links.is_primary END;

        INSERT INTO channel_project_links(channel_id, project_id, relation_kind, is_primary)
        SELECT c.id, 'den-mcp', 'linked', 0
        FROM channels c WHERE c.slug = 'den-system'
        ON CONFLICT(channel_id, project_id) DO UPDATE SET
            relation_kind = excluded.relation_kind;

        INSERT INTO channel_project_links(channel_id, project_id, relation_kind, is_primary)
        SELECT c.id, 'den-channels', 'linked', 0
        FROM channels c WHERE c.slug = 'den-system'
        ON CONFLICT(channel_id, project_id) DO UPDATE SET
            relation_kind = excluded.relation_kind;

        INSERT INTO channel_project_links(channel_id, project_id, relation_kind, is_primary)
        SELECT c.id, 'den-host', 'linked', 0
        FROM channels c WHERE c.slug = 'den-system'
        ON CONFLICT(channel_id, project_id) DO UPDATE SET
            relation_kind = excluded.relation_kind;

        INSERT INTO channel_project_links(channel_id, project_id, relation_kind, is_primary)
        SELECT c.id, 'den-gateway', 'linked', 0
        FROM channels c WHERE c.slug = 'den-system'
        ON CONFLICT(channel_id, project_id) DO UPDATE SET
            relation_kind = excluded.relation_kind;

        INSERT INTO channel_project_links(channel_id, project_id, relation_kind, is_primary)
        SELECT c.id, 'den-desktop', 'linked', 0
        FROM channels c WHERE c.slug = 'den-system'
        ON CONFLICT(channel_id, project_id) DO UPDATE SET
            relation_kind = excluded.relation_kind;

        INSERT INTO channel_project_links(channel_id, project_id, relation_kind, is_primary)
        SELECT c.id, 'den-hermes-bridge', 'linked', 0
        FROM channels c WHERE c.slug = 'den-system'
        ON CONFLICT(channel_id, project_id) DO UPDATE SET
            relation_kind = excluded.relation_kind;

        INSERT INTO channel_project_links(channel_id, project_id, relation_kind, is_primary)
        SELECT c.id, 'den-network', 'linked', 0
        FROM channels c WHERE c.slug = 'den-system'
        ON CONFLICT(channel_id, project_id) DO UPDATE SET
            relation_kind = excluded.relation_kind;

        INSERT INTO channel_project_links(channel_id, project_id, relation_kind, is_primary)
        SELECT c.id, 'den-web', 'linked', 0
        FROM channels c WHERE c.slug = 'den-system'
        ON CONFLICT(channel_id, project_id) DO UPDATE SET
            relation_kind = excluded.relation_kind;
        """;

    /// <summary>
    /// Rebuild worker_pool_lobby_presence with concrete-identity-scoped UNIQUE
    /// constraint and normalize concrete_identity values. Preserves existing data.
    /// concrete_identity is computed as: pool_member_id when present, else
    /// agent_instance_id when present, else '' for non-pool callers.
    /// </summary>
    private const string RebuildWorkerPoolLobbyPresenceForConcreteConstraintSql = """
        PRAGMA foreign_keys = OFF;
        CREATE TABLE worker_pool_lobby_presence__new (
            id                      INTEGER PRIMARY KEY AUTOINCREMENT,
            channel_id              INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
            member_identity         TEXT NOT NULL,
            agent_instance_id       TEXT,
            pool_member_id          TEXT,
            concrete_identity       TEXT NOT NULL DEFAULT '',
            profile                 TEXT,
            role                    TEXT,
            status                  TEXT NOT NULL DEFAULT 'idle'
                                    CHECK (status IN ('idle', 'leased', 'draining', 'released', 'quarantined', 'offline')),
            current_assignment_id   TEXT,
            current_task_id         TEXT,
            current_project_id      TEXT,
            last_activity_at        TEXT,
            release_acknowledged    INTEGER NOT NULL DEFAULT 0 CHECK (release_acknowledged IN (0, 1)),
            created_at              TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at              TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(channel_id, member_identity, concrete_identity)
        );

        INSERT INTO worker_pool_lobby_presence__new(
            id, channel_id, member_identity, agent_instance_id, pool_member_id,
            concrete_identity, profile, role, status, current_assignment_id,
            current_task_id, current_project_id, last_activity_at,
            release_acknowledged, created_at, updated_at)
        SELECT
            id, channel_id, member_identity, agent_instance_id, pool_member_id,
            COALESCE(pool_member_id, agent_instance_id, ''),
            profile, role, status, current_assignment_id,
            current_task_id, current_project_id, last_activity_at,
            COALESCE(release_acknowledged, 0), created_at, updated_at
        FROM worker_pool_lobby_presence
        ORDER BY id;

        DROP TABLE worker_pool_lobby_presence;
        ALTER TABLE worker_pool_lobby_presence__new RENAME TO worker_pool_lobby_presence;
        PRAGMA foreign_keys = ON;
        """;
}
