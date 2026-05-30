using DenChannels.Service.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenChannels.Service.Tests;

public sealed class ChannelsDatabaseInitializerTests
{
    [Fact]
    public async Task ApplyMigrationsAsync_CreatesChannelTablesAndSourcePointerColumns()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();

        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        var tables = await ListTablesAsync(connection);
        Assert.Contains("channels", tables);
        Assert.Contains("channel_messages", tables);
        Assert.Contains("channel_memberships", tables);
        Assert.Contains("channel_reactions", tables);
        Assert.Contains("channel_activity_events", tables);
        Assert.Contains("channel_read_cursors", tables);
        Assert.Contains("schema_migrations", tables);

        var messageColumns = await ListColumnsAsync(connection, "channel_messages");
        Assert.Contains("source_kind", messageColumns);
        Assert.Contains("source_id", messageColumns);
        Assert.Contains("source_project_id", messageColumns);
        Assert.Contains("deep_link", messageColumns);
        Assert.Contains("metadata_json", messageColumns);
        Assert.Contains("dedupe_key", messageColumns);
        Assert.Contains("delivery_request_id", messageColumns);

        var activityColumns = await ListColumnsAsync(connection, "channel_activity_events");
        Assert.Contains("display_block_id", activityColumns);
        Assert.Contains("parent_hermes_session_key", activityColumns);
        Assert.Contains("parent_agent_identity", activityColumns);
        Assert.Contains("worker_run_id", activityColumns);
        Assert.Contains("worker_role", activityColumns);

        var indexes = await ListIndexesAsync(connection);
        Assert.Contains("idx_channel_messages_delivery_request", indexes);
        Assert.Contains("idx_channel_activity_events_display_block", indexes);
        Assert.Contains("idx_channel_activity_events_worker_run", indexes);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_IsIdempotent()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();

        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        Assert.Equal(2, await CountRowsAsync(connection, "schema_migrations"));
    }

    [Fact]
    public async Task ChannelSchema_RejectsInvalidEnumsAndDuplicateProjectDefaults()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO channels(slug, display_name, kind)
            VALUES ('bad-kind', 'Bad Kind', 'not_a_channel_kind');
            """));

        await ExecuteAsync(connection, """
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-den-channels', 'Den Channels', 'project_default', 'den-channels');
            """);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-den-channels-2', 'Den Channels Duplicate', 'project_default', 'den-channels');
            """));
    }

    [Fact]
    public async Task ChannelMessageSchema_PreservesSourcePointersWithoutCanonicalPayloadCopy()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        await ExecuteAsync(connection, """
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-den-channels', 'Den Channels', 'project_default', 'den-channels');
            INSERT INTO channel_messages(
                channel_id,
                sender_type,
                sender_identity,
                body,
                message_kind,
                source_kind,
                source_id,
                source_project_id,
                summary,
                deep_link,
                metadata_json,
                dedupe_key)
            VALUES (
                1,
                'system',
                'den-router',
                'Task #1320 completed. Open task for details.',
                'mirror_summary',
                'task_message',
                '5680',
                'den-channels',
                'Task #1320 completed',
                'den://project/den-channels/task/1320',
                '{"task_id":1320}',
                'task-message:5680');
            """);

        var row = await QuerySingleAsync(connection, """
            SELECT source_kind, source_id, source_project_id, deep_link, metadata_json
            FROM channel_messages
            WHERE dedupe_key = 'task-message:5680';
            """);

        Assert.Equal("task_message", row["source_kind"]);
        Assert.Equal("5680", row["source_id"]);
        Assert.Equal("den-channels", row["source_project_id"]);
        Assert.Equal("den://project/den-channels/task/1320", row["deep_link"]);
        Assert.Equal("{\"task_id\":1320}", row["metadata_json"]);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO channel_messages(channel_id, sender_type, sender_identity, body, dedupe_key)
            VALUES (1, 'system', 'den-router', 'Duplicate summary', 'task-message:5680');
            """));
    }

    [Fact]
    public async Task ChannelMessageSchema_AcceptsGatewayDeliverySourceKind()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        await ExecuteAsync(connection, """
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-den-channels', 'Den Channels', 'project_default', 'den-channels');
            INSERT INTO channel_messages(
                channel_id,
                sender_type,
                sender_identity,
                body,
                message_kind,
                source_kind,
                source_id,
                source_project_id,
                dedupe_key)
            VALUES (
                1,
                'agent',
                'den-channels-runner',
                'Gateway delivery reply',
                'agent_text',
                'gateway_delivery',
                '44',
                'den-channels',
                'gateway-delivery:44');
            """);

        var row = await QuerySingleAsync(connection, """
            SELECT source_kind, source_id, source_project_id
            FROM channel_messages
            WHERE dedupe_key = 'gateway-delivery:44';
            """);

        Assert.Equal("gateway_delivery", row["source_kind"]);
        Assert.Equal("44", row["source_id"]);
        Assert.Equal("den-channels", row["source_project_id"]);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_AddsSourceProjectIdToLegacyChannelMessagesTable()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ExecuteAsync(connection, """
            CREATE TABLE channel_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL,
                sender_type TEXT NOT NULL,
                sender_identity TEXT NOT NULL,
                body TEXT NOT NULL
            );
            """);

        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        var messageColumns = await ListColumnsAsync(connection, "channel_messages");
        Assert.Contains("source_project_id", messageColumns);
        Assert.Contains("dedupe_key", messageColumns);
        Assert.Contains("delivery_request_id", messageColumns);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_RebuildsLegacySourceKindConstraintForGatewayDelivery()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ExecuteAsync(connection, """
            CREATE TABLE channels (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                slug TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                kind TEXT NOT NULL,
                project_id TEXT
            );
            CREATE TABLE channel_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                sender_type TEXT NOT NULL CHECK (sender_type IN ('user', 'agent', 'system', 'bridge')),
                sender_identity TEXT NOT NULL,
                body TEXT NOT NULL,
                message_kind TEXT NOT NULL DEFAULT 'human_text'
                    CHECK (message_kind IN ('human_text', 'agent_text', 'system_event', 'mirror_summary', 'command', 'command_result')),
                source_kind TEXT
                    CHECK (source_kind IS NULL OR source_kind IN ('task_message', 'agent_stream_entry', 'notification', 'worker_run', 'review_round', 'review_finding', 'wake_event', 'external_adapter_message')),
                source_id TEXT,
                source_project_id TEXT,
                summary TEXT,
                deep_link TEXT,
                thread_root_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
                reply_to_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
                metadata_json TEXT,
                dedupe_key TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                edited_at TEXT,
                deleted_at TEXT
            );
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-den-channels', 'Den Channels', 'project_default', 'den-channels');
            INSERT INTO channel_messages(channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id, source_project_id, dedupe_key)
            VALUES (1, 'system', 'den-router', 'Existing wake', 'system_event', 'wake_event', 'wake-1', 'den-channels', 'wake-1');
            """);

        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        await ExecuteAsync(connection, """
            INSERT INTO channel_messages(channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id, source_project_id, dedupe_key)
            VALUES (1, 'agent', 'den-channels-runner', 'Gateway delivery reply', 'agent_text', 'gateway_delivery', '44', 'den-channels', 'gateway-delivery:44');
            """);
        Assert.Equal(2, await CountRowsAsync(connection, "channel_messages"));
    }

    [Fact]
    public async Task ActivityEventSchema_IsSeparateFromMessagesAndHasConstraints()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        var columns = await ListColumnsAsync(connection, "channel_activity_events");
        Assert.Contains("delivery_request_id", columns);
        Assert.Contains("hermes_session_key", columns);
        Assert.Contains("anchor_message_id", columns);
        Assert.Contains("display_block_id", columns);
        Assert.Contains("parent_hermes_session_key", columns);
        Assert.Contains("parent_agent_identity", columns);
        Assert.Contains("worker_run_id", columns);
        Assert.Contains("worker_role", columns);
        Assert.Contains("preview_json", columns);
        Assert.Contains("dedupe_key", columns);
        Assert.Contains("delivery_stage", columns);
        Assert.Contains("terminal", columns);
        Assert.Contains("final_channel_message_id", columns);

        await ExecuteAsync(connection, """
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-den-channels', 'Den Channels', 'project_default', 'den-channels');
            INSERT INTO channel_activity_events(
                channel_id, project_id, agent_identity, delivery_request_id, hermes_session_key,
                display_block_id, parent_hermes_session_key, parent_agent_identity, worker_run_id, worker_role,
                event_type, status, delivery_stage, terminal, sequence, summary, dedupe_key)
            VALUES (
                1, 'den-channels', 'den-mcp-runner', 'dr-1', 'session-1',
                'block-1', 'parent-session', 'parent-agent', 'worker-1', 'coder',
                'tool_call_started', 'started', 'tool', 0, 1, 'terminal: dotnet test', 'activity:dr-1:1');
            """);

        Assert.Equal(1, await CountRowsAsync(connection, "channel_activity_events"));
        Assert.Equal(0, await CountRowsAsync(connection, "channel_messages"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO channel_activity_events(channel_id, agent_identity, event_type, status)
            VALUES (1, 'den-mcp-runner', 'not_a_real_event', 'started');
            """));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO channel_activity_events(channel_id, agent_identity, event_type, status, sequence)
            VALUES (1, 'den-mcp-runner', 'tool_call_started', 'started', -1);
            """));
    }

    [Fact]
    public async Task ApplyMigrationsAsync_AddsActivityEventStoreToLegacyDatabase()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ExecuteAsync(connection, """
            CREATE TABLE channels (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                slug TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                kind TEXT NOT NULL,
                project_id TEXT
            );
            CREATE TABLE channel_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL,
                sender_type TEXT NOT NULL,
                sender_identity TEXT NOT NULL,
                body TEXT NOT NULL
            );
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            INSERT INTO schema_migrations(version, name) VALUES (1, 'initial_channel_schema');
            """);

        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        var tables = await ListTablesAsync(connection);
        Assert.Contains("channel_activity_events", tables);
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);
        Assert.Contains("channel_activity_events", await ListTablesAsync(connection));
    }

    [Fact]
    public async Task ApplyMigrationsAsync_AddsAssignmentColumnsToChannelMessages()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        var columns = await ListColumnsAsync(connection, "channel_messages");
        Assert.Contains("assignment_id", columns);
        Assert.Contains("checkpoint_type", columns);
        Assert.Contains("checkpoint_handle", columns);

        var indexes = await ListIndexesAsync(connection);
        Assert.Contains("idx_channel_messages_assignment", indexes);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_AddsAssignmentColumnsToActivityEvents()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        var columns = await ListColumnsAsync(connection, "channel_activity_events");
        Assert.Contains("assignment_id", columns);
        Assert.Contains("checkpoint_type", columns);
        Assert.Contains("checkpoint_handle", columns);

        var indexes = await ListIndexesAsync(connection);
        Assert.Contains("idx_channel_activity_events_assignment", indexes);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_AddsAssignmentColumnsToLegacyMessageTable()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ExecuteAsync(connection, """
            CREATE TABLE channel_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL,
                sender_type TEXT NOT NULL,
                sender_identity TEXT NOT NULL,
                body TEXT NOT NULL
            );
            """);

        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        var columns = await ListColumnsAsync(connection, "channel_messages");
        Assert.Contains("assignment_id", columns);
        Assert.Contains("checkpoint_type", columns);
        Assert.Contains("checkpoint_handle", columns);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_AddsAssignmentColumnsToLegacyActivityTable()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ExecuteAsync(connection, """
            CREATE TABLE channels (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                slug TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                kind TEXT NOT NULL,
                project_id TEXT
            );
            CREATE TABLE channel_activity_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL,
                project_id TEXT,
                agent_identity TEXT NOT NULL,
                delivery_request_id TEXT,
                hermes_session_key TEXT,
                task_id INTEGER,
                thread_id INTEGER,
                anchor_message_id INTEGER,
                event_type TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'completed',
                sequence INTEGER NOT NULL DEFAULT 0,
                update_version INTEGER NOT NULL DEFAULT 1,
                title TEXT,
                summary TEXT,
                preview_json TEXT,
                metadata_json TEXT,
                dedupe_key TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """);

        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        var columns = await ListColumnsAsync(connection, "channel_activity_events");
        Assert.Contains("display_block_id", columns);
        Assert.Contains("parent_hermes_session_key", columns);
        Assert.Contains("parent_agent_identity", columns);
        Assert.Contains("worker_run_id", columns);
        Assert.Contains("worker_role", columns);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_RebuildsChannelReadCursorsConstraintForLegacyV1Db()
    {
        // Simulate a V1 database with the old UNIQUE constraint
        // (without instance_id) — the exact structure from InitialSchemaSql
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ExecuteAsync(connection, """
            CREATE TABLE channels (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                slug TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                kind TEXT NOT NULL,
                project_id TEXT
            );
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-test', 'Test Project', 'project_default', 'test-proj');

            CREATE TABLE channel_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                sender_type TEXT NOT NULL,
                sender_identity TEXT NOT NULL,
                body TEXT NOT NULL,
                message_kind TEXT NOT NULL DEFAULT 'human_text',
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            INSERT INTO channel_messages(channel_id, sender_type, sender_identity, body)
            VALUES (1, 'system', 'test', 'test-message-1');
            INSERT INTO channel_messages(channel_id, sender_type, sender_identity, body)
            VALUES (1, 'system', 'test', 'test-message-2');

            CREATE TABLE channel_read_cursors (
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
            -- Existing profile-level cursor
            INSERT INTO channel_read_cursors(channel_id, reader_type, reader_identity, last_read_channel_message_id)
            VALUES (1, 'agent', 'spawned-coder', 1);
            """);

        // Apply migrations (fresh create of schema_migrations table shows version 0)
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        // Verify: can insert two cursors with same (channel, type, identity) but different instance_ids
        await ExecuteAsync(connection, """
            INSERT INTO channel_read_cursors(channel_id, reader_type, reader_identity, instance_id, last_read_channel_message_id)
            VALUES (1, 'agent', 'spawned-coder', 'inst-a', 1);
            """);

        await ExecuteAsync(connection, """
            INSERT INTO channel_read_cursors(channel_id, reader_type, reader_identity, instance_id, last_read_channel_message_id)
            VALUES (1, 'agent', 'spawned-coder', 'inst-b', 2);
            """);

        // Verify: existing profile-level cursor data was preserved (instance_id normalized to '')
        var profileCursor = await QuerySingleAsync(connection, """
            SELECT instance_id, last_read_channel_message_id
            FROM channel_read_cursors
            WHERE channel_id = 1
              AND reader_type = 'agent'
              AND reader_identity = 'spawned-coder'
              AND instance_id = '';
            """);
        Assert.Equal("1", profileCursor["last_read_channel_message_id"]);

        var count = await CountRowsAsync(connection, "channel_read_cursors");
        Assert.Equal(3, count);

        // Verify: the old UNIQUE constraint no longer blocks per-instance cursors
        var instanceA = await QuerySingleAsync(connection, """
            SELECT last_read_channel_message_id
            FROM channel_read_cursors
            WHERE channel_id = 1
              AND reader_type = 'agent'
              AND reader_identity = 'spawned-coder'
              AND instance_id = 'inst-a';
            """);
        Assert.Equal("1", instanceA["last_read_channel_message_id"]);

        var instanceB = await QuerySingleAsync(connection, """
            SELECT last_read_channel_message_id
            FROM channel_read_cursors
            WHERE channel_id = 1
              AND reader_type = 'agent'
              AND reader_identity = 'spawned-coder'
              AND instance_id = 'inst-b';
            """);
        Assert.Equal("2", instanceB["last_read_channel_message_id"]);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_NormalizesNullInstanceIdsDuringRebuild()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        // Simulate a V1 database where channel_read_cursors already exists
        // with the old UNIQUE constraint (before instance_id was added).
        // This is the state of a V1 DB before migration v2 runs.
        await ExecuteAsync(connection, """
            CREATE TABLE channels (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                slug TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                kind TEXT NOT NULL,
                project_id TEXT
            );
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-null-test', 'Null Test', 'project_default', 'null-test');

            CREATE TABLE channel_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                sender_type TEXT NOT NULL,
                sender_identity TEXT NOT NULL,
                body TEXT NOT NULL,
                message_kind TEXT NOT NULL DEFAULT 'human_text',
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            INSERT INTO channel_messages(channel_id, sender_type, sender_identity, body)
            VALUES (1, 'system', 'test', 'msg');

            CREATE TABLE channel_read_cursors (
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
            -- Existing profile-level cursor (NULL instance_id, old constraint)
            INSERT INTO channel_read_cursors(channel_id, reader_type, reader_identity, last_read_channel_message_id)
            VALUES (1, 'agent', 'null-test-agent', 1);
            """);

        // Apply migrations — should rebuild table and normalize NULL to ''
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        var row = await QuerySingleAsync(connection, """
            SELECT instance_id, last_read_channel_message_id
            FROM channel_read_cursors
            WHERE channel_id = 1
              AND reader_type = 'agent'
              AND reader_identity = 'null-test-agent';
            """);
        Assert.Equal("", row["instance_id"]);
        Assert.Equal("1", row["last_read_channel_message_id"]);
    }

    private static async Task<SqliteConnection> OpenInMemoryDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<HashSet<string>> ListTablesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));
        return tables;
    }

    private static async Task<HashSet<string>> ListColumnsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task<HashSet<string>> ListIndexesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";
        await using var reader = await command.ExecuteReaderAsync();
        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
            indexes.Add(reader.GetString(0));
        return indexes;
    }

    private static async Task<int> CountRowsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\";";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Dictionary<string, string>> QuerySingleAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.GetString(i);
        return row;
    }
}
