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
        Assert.Contains("channel_read_cursors", tables);
        Assert.Contains("schema_migrations", tables);

        var messageColumns = await ListColumnsAsync(connection, "channel_messages");
        Assert.Contains("source_kind", messageColumns);
        Assert.Contains("source_id", messageColumns);
        Assert.Contains("source_project_id", messageColumns);
        Assert.Contains("deep_link", messageColumns);
        Assert.Contains("metadata_json", messageColumns);
        Assert.Contains("dedupe_key", messageColumns);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_IsIdempotent()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();

        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        Assert.Equal(1, await CountRowsAsync(connection, "schema_migrations"));
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
