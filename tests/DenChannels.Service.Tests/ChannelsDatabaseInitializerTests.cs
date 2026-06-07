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
        Assert.Contains("channel_project_links", tables);

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
        Assert.Contains("parent_session_key", activityColumns);
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

        Assert.Equal(8, await CountRowsAsync(connection, "schema_migrations"));
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

        // gateway_delivery is quarantined as historical/tombstone in v8
        // but still accepted by CHECK for backward compatibility
        await ExecuteAsync(connection, """"
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
            """");
        Assert.Equal(1, await CountRowsAsync(connection, "channel_messages"));
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
        await ExecuteAsync(connection, """"
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
            """");

        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        // gateway_delivery still accepted (quarantined, not purged)
        await ExecuteAsync(connection, """"
            INSERT INTO channel_messages(channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id, source_project_id, dedupe_key)
            VALUES (1, 'agent', 'den-channels-runner', 'Gateway delivery reply', 'agent_text', 'gateway_delivery', '44', 'den-channels', 'gateway-delivery:44');
            """");
        Assert.Equal(2, await CountRowsAsync(connection, "channel_messages"));
    }

    [Fact]
    public async Task ActivityEventSchema_IsSeparateFromMessagesAndHasConstraints()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        var columns = await ListColumnsAsync(connection, "channel_activity_events");
        Assert.Contains("delivery_request_id", columns);
        Assert.Contains("session_key", columns);
        Assert.Contains("anchor_message_id", columns);
        Assert.Contains("display_block_id", columns);
        Assert.Contains("parent_session_key", columns);
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
                channel_id, project_id, agent_identity, delivery_request_id, session_key,
                display_block_id, parent_session_key, parent_agent_identity, worker_run_id, worker_role,
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
                session_key TEXT,
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
        Assert.Contains("parent_session_key", columns);
        Assert.Contains("parent_agent_identity", columns);
        Assert.Contains("worker_run_id", columns);
        Assert.Contains("worker_role", columns);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_RebuildsLegacyActivityEventTypeConstraintForAgentWorkLifecycle()
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
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-den-channels', 'Den Channels', 'project_default', 'den-channels');

            CREATE TABLE channel_activity_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                project_id TEXT,
                agent_identity TEXT NOT NULL,
                delivery_request_id TEXT,
                session_key TEXT,
                task_id INTEGER,
                thread_id INTEGER,
                anchor_message_id INTEGER,
                event_type TEXT NOT NULL
                    CHECK (event_type IN ('tool_call_started', 'tool_call_completed', 'tool_call_failed', 'lifecycle_status', 'aggregation_snapshot', 'run_summary')),
                status TEXT NOT NULL DEFAULT 'completed'
                    CHECK (status IN ('started', 'completed', 'failed', 'interim', 'blocked')),
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
            INSERT INTO channel_activity_events(
                channel_id, project_id, agent_identity, event_type, status, summary, dedupe_key)
            VALUES (
                1, 'den-channels', 'legacy-agent', 'lifecycle_status', 'interim', 'legacy compatibility row', 'legacy-row');
            """);

        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        await ExecuteAsync(connection, """
            INSERT INTO channel_activity_events(
                channel_id, project_id, agent_identity, event_type, status, summary, dedupe_key)
            VALUES (
                1, 'den-channels', 'pi-crew-gateway', 'agent_work_lifecycle', 'started', 'canonical lifecycle row', 'canonical-row');
            """);

        var legacy = await QuerySingleAsync(connection, """
            SELECT event_type, summary
            FROM channel_activity_events
            WHERE dedupe_key = 'legacy-row';
            """);
        Assert.Equal("lifecycle_status", legacy["event_type"]);
        Assert.Equal("legacy compatibility row", legacy["summary"]);

        var canonical = await QuerySingleAsync(connection, """
            SELECT event_type, status, summary
            FROM channel_activity_events
            WHERE dedupe_key = 'canonical-row';
            """);
        Assert.Equal("agent_work_lifecycle", canonical["event_type"]);
        Assert.Equal("started", canonical["status"]);
        Assert.Equal("canonical lifecycle row", canonical["summary"]);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_DropsStaleActivityEventTempTableBeforeConstraintRebuild()
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
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-den-channels', 'Den Channels', 'project_default', 'den-channels');

            CREATE TABLE channel_activity_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                project_id TEXT,
                agent_identity TEXT NOT NULL,
                delivery_request_id TEXT,
                session_key TEXT,
                task_id INTEGER,
                thread_id INTEGER,
                anchor_message_id INTEGER,
                event_type TEXT NOT NULL
                    CHECK (event_type IN ('tool_call_started', 'tool_call_completed', 'tool_call_failed', 'lifecycle_status', 'aggregation_snapshot', 'run_summary')),
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
            INSERT INTO channel_activity_events(channel_id, project_id, agent_identity, event_type, status)
            VALUES (1, 'den-channels', 'legacy-agent', 'lifecycle_status', 'interim');

            CREATE TABLE channel_activity_events__new (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                stale_marker TEXT
            );
            """);

        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        await ExecuteAsync(connection, """
            INSERT INTO channel_activity_events(channel_id, project_id, agent_identity, event_type, status)
            VALUES (1, 'den-channels', 'pi-crew-gateway', 'agent_work_lifecycle', 'started');
            """);

        var tables = await ListTablesAsync(connection);
        Assert.DoesNotContain("channel_activity_events__new", tables);

        var indexes = await ListIndexesAsync(connection);
        Assert.Contains("idx_channel_activity_events_channel_created", indexes);
        Assert.Contains("idx_channel_activity_events_agent_instance", indexes);
        Assert.Contains("ux_channel_activity_events_dedupe", indexes);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_RecoversInterruptedActivityEventConstraintRebuildWhenOriginalMissing()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ExecuteAsync(connection, """
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            INSERT INTO schema_migrations(version, name)
            VALUES (7, 'channel_messages_fts5');

            CREATE TABLE channels (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                slug TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                kind TEXT NOT NULL,
                project_id TEXT
            );
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-den-channels', 'Den Channels', 'project_default', 'den-channels');

            CREATE TABLE channel_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                sender_type TEXT NOT NULL,
                sender_identity TEXT NOT NULL,
                body TEXT NOT NULL,
                message_kind TEXT NOT NULL DEFAULT 'human_text',
                source_kind TEXT,
                source_id TEXT,
                source_project_id TEXT,
                target_project_id TEXT,
                target_task_id INTEGER,
                worker_run_id TEXT,
                worker_role TEXT,
                profile_identity TEXT,
                summary TEXT,
                deep_link TEXT,
                thread_root_message_id INTEGER,
                reply_to_message_id INTEGER,
                metadata_json TEXT,
                delivery_request_id TEXT,
                dedupe_key TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                edited_at TEXT,
                deleted_at TEXT,
                assignment_id TEXT,
                checkpoint_type TEXT,
                checkpoint_handle TEXT,
                agent_instance_id TEXT,
                pool_member_id TEXT,
                session_owner_id TEXT,
                session_id TEXT
            );

            CREATE TABLE channel_activity_events__new (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                project_id TEXT,
                agent_identity TEXT NOT NULL,
                delivery_request_id TEXT,
                session_key TEXT,
                hermes_session_key TEXT,
                display_block_id TEXT,
                parent_session_key TEXT,
                parent_hermes_session_key TEXT,
                parent_agent_identity TEXT,
                worker_run_id TEXT,
                worker_role TEXT,
                agent_instance_id TEXT,
                pool_member_id TEXT,
                task_id INTEGER,
                thread_id INTEGER,
                anchor_message_id INTEGER,
                assignment_id TEXT,
                checkpoint_type TEXT,
                checkpoint_handle TEXT,
                event_type TEXT NOT NULL
                    CHECK (event_type IN ('tool_call_started', 'tool_call_completed', 'tool_call_failed', 'lifecycle_status', 'aggregation_snapshot', 'run_summary', 'agent_work_lifecycle')),
                status TEXT NOT NULL DEFAULT 'completed'
                    CHECK (status IN ('started', 'completed', 'failed', 'interim', 'blocked')),
                delivery_stage TEXT NOT NULL DEFAULT 'progress',
                terminal INTEGER NOT NULL DEFAULT 0 CHECK (terminal IN (0, 1)),
                final_channel_message_id INTEGER,
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
            INSERT INTO channel_activity_events__new(
                id, channel_id, project_id, agent_identity, event_type, status, summary, dedupe_key)
            VALUES (
                42, 1, 'den-channels', 'legacy-agent', 'lifecycle_status', 'interim', 'preserved copied row', 'copied-row');
            """);

        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        var tables = await ListTablesAsync(connection);
        Assert.Contains("channel_activity_events", tables);
        Assert.DoesNotContain("channel_activity_events__new", tables);

        var preserved = await QuerySingleAsync(connection, """
            SELECT id, event_type, summary
            FROM channel_activity_events
            WHERE dedupe_key = 'copied-row';
            """);
        Assert.Equal("42", preserved["id"]);
        Assert.Equal("lifecycle_status", preserved["event_type"]);
        Assert.Equal("preserved copied row", preserved["summary"]);

        await ExecuteAsync(connection, """
            INSERT INTO channel_activity_events(channel_id, project_id, agent_identity, event_type, status)
            VALUES (1, 'den-channels', 'pi-crew-gateway', 'agent_work_lifecycle', 'started');
            """);

        var indexes = await ListIndexesAsync(connection);
        Assert.Contains("idx_channel_activity_events_channel_created", indexes);
        Assert.Contains("idx_channel_activity_events_agent_instance", indexes);
        Assert.Contains("ux_channel_activity_events_dedupe", indexes);
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

    // =========================================================================
    // V8 migration tests
    // =========================================================================

    [Fact]
    public async Task V8Migration_CreatesSubscriptionTables()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        var tables = await ListTablesAsync(connection);
        Assert.Contains("channel_subscriptions", tables);
        Assert.Contains("channel_subscription_cursors", tables);
    }

    [Fact]
    public async Task V8Migration_IsIdempotentForSubscriptions()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        // Insert a membership row to trigger backfill
        await ExecuteAsync(connection, """"
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-test', 'Test', 'project_default', 'test');
            INSERT INTO channel_memberships(channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite, cooldown_seconds, max_auto_replies_per_window)
            VALUES (1, 'agent', 'test-agent', 'active', 'mentions_only', 1, 1, 0, 60, 1);
            """");
        var subCount = await CountRowsAsync(connection, "channel_subscriptions");

        // Second migration run should not duplicate subscriptions
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);
        Assert.Equal(subCount, await CountRowsAsync(connection, "channel_subscriptions"));
    }

    [Fact]
    public async Task V8Migration_RemovesHermesColumnsFromActivityEvents()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        var columns = await ListColumnsAsync(connection, "channel_activity_events");
        Assert.DoesNotContain("hermes_session_key", columns);
        Assert.DoesNotContain("parent_hermes_session_key", columns);
        Assert.Contains("session_key", columns);
        Assert.Contains("parent_session_key", columns);
    }

    [Fact]
    public async Task V8Migration_AddsMembershipV8Columns()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        var columns = await ListColumnsAsync(connection, "channel_memberships");
        Assert.Contains("profile_identity", columns);
        Assert.Contains("member_role", columns);
        Assert.Contains("left_at", columns);
    }

    [Fact]
    public async Task V8Migration_PreservesHermesSessionValues()
    {
        // Simulate a pre-v8 DB with hermes_session_key values
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ExecuteAsync(connection, """"
            CREATE TABLE channels (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                slug TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                kind TEXT NOT NULL,
                project_id TEXT
            );
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-den-channels', 'Den Channels', 'project_default', 'den-channels');

            CREATE TABLE channel_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                sender_type TEXT NOT NULL,
                sender_identity TEXT NOT NULL,
                body TEXT NOT NULL,
                message_kind TEXT NOT NULL DEFAULT 'human_text',
                source_kind TEXT,
                source_id TEXT,
                source_project_id TEXT,
                target_project_id TEXT,
                target_task_id INTEGER,
                worker_run_id TEXT,
                worker_role TEXT,
                profile_identity TEXT,
                summary TEXT,
                deep_link TEXT,
                thread_root_message_id INTEGER,
                reply_to_message_id INTEGER,
                metadata_json TEXT,
                delivery_request_id TEXT,
                dedupe_key TEXT,
                assignment_id TEXT,
                checkpoint_type TEXT,
                checkpoint_handle TEXT,
                agent_instance_id TEXT,
                pool_member_id TEXT,
                session_owner_id TEXT,
                session_id TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                edited_at TEXT,
                deleted_at TEXT
            );

            CREATE TABLE channel_activity_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                project_id TEXT,
                agent_identity TEXT NOT NULL,
                delivery_request_id TEXT,
                session_key TEXT,
                hermes_session_key TEXT,
                display_block_id TEXT,
                parent_session_key TEXT,
                parent_hermes_session_key TEXT,
                parent_agent_identity TEXT,
                worker_run_id TEXT,
                worker_role TEXT,
                agent_instance_id TEXT,
                pool_member_id TEXT,
                task_id INTEGER,
                thread_id INTEGER,
                anchor_message_id INTEGER,
                assignment_id TEXT,
                checkpoint_type TEXT,
                checkpoint_handle TEXT,
                event_type TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'completed',
                delivery_stage TEXT NOT NULL DEFAULT 'progress',
                terminal INTEGER NOT NULL DEFAULT 0,
                final_channel_message_id INTEGER,
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

            INSERT INTO channel_activity_events(
                channel_id, project_id, agent_identity,
                hermes_session_key, parent_hermes_session_key,
                event_type, status, dedupe_key)
            VALUES (
                1, 'den-channels', 'test-agent',
                'hermes-session-42', 'hermes-parent-7',
                'tool_call_started', 'started', 'v8-migration-preserve');
            """");

        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        // Hermes columns should be gone
        var columns = await ListColumnsAsync(connection, "channel_activity_events");
        Assert.DoesNotContain("hermes_session_key", columns);
        Assert.DoesNotContain("parent_hermes_session_key", columns);

        // Values should be preserved in session_key / parent_session_key
        var row = await QuerySingleAsync(connection, """"
            SELECT session_key, parent_session_key
            FROM channel_activity_events
            WHERE dedupe_key = 'v8-migration-preserve';
            """");
        Assert.Equal("hermes-session-42", row["session_key"]);
        Assert.Equal("hermes-parent-7", row["parent_session_key"]);
    }

    [Fact]
    public async Task V8Migration_MigratesGatewayDeliveryRowsWhenLegacyFtsUpdateTriggerExists()
    {
        // Live v7 databases already have the channel_messages FTS5 table/triggers.
        // The original trigger shape used the FTS5 'delete' control row on a
        // normal FTS5 table, which raises SQLite "SQL logic error" on UPDATE.
        // V8 bulk-updates gateway_delivery rows, so it must tolerate and repair
        // that legacy trigger before touching channel_messages.
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ExecuteAsync(connection, """"
            CREATE TABLE channels (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                slug TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                kind TEXT NOT NULL,
                project_id TEXT
            );
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-den-channels', 'Den Channels', 'project_default', 'den-channels');

            CREATE TABLE channel_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                sender_type TEXT NOT NULL CHECK (sender_type IN ('user', 'agent', 'system', 'bridge')),
                sender_identity TEXT NOT NULL,
                body TEXT NOT NULL,
                message_kind TEXT NOT NULL DEFAULT 'human_text'
                    CHECK (message_kind IN ('human_text', 'agent_text', 'system_event', 'mirror_summary', 'command', 'command_result')),
                source_kind TEXT
                    CHECK (source_kind IS NULL OR source_kind IN ('task_message', 'agent_stream_entry', 'notification', 'worker_run', 'review_round', 'review_finding', 'wake_event', 'gateway_delivery', 'external_adapter_message')),
                source_id TEXT,
                source_project_id TEXT,
                summary TEXT,
                deep_link TEXT,
                thread_root_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
                reply_to_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
                metadata_json TEXT,
                delivery_request_id TEXT,
                dedupe_key TEXT,
                assignment_id TEXT,
                checkpoint_type TEXT,
                checkpoint_handle TEXT,
                agent_instance_id TEXT,
                pool_member_id TEXT,
                target_project_id TEXT,
                target_task_id INTEGER,
                worker_run_id TEXT,
                worker_role TEXT,
                profile_identity TEXT,
                session_owner_id TEXT,
                session_id TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                edited_at TEXT,
                deleted_at TEXT
            );
            INSERT INTO channel_messages(
                channel_id, sender_type, sender_identity, body, message_kind,
                source_kind, source_id, source_project_id, dedupe_key)
            VALUES (
                1, 'agent', 'legacy-gateway', 'Gateway delivery body', 'agent_text',
                'gateway_delivery', 'legacy-1', 'den-channels', 'legacy-gateway-1');

            CREATE VIRTUAL TABLE channel_messages_fts USING fts5(body);
            INSERT INTO channel_messages_fts(rowid, body)
            SELECT id, body FROM channel_messages;
            CREATE TRIGGER channel_messages_fts_update AFTER UPDATE ON channel_messages
            BEGIN
                INSERT INTO channel_messages_fts(channel_messages_fts, rowid, body) VALUES('delete', old.id, old.body);
                INSERT INTO channel_messages_fts(rowid, body) VALUES (new.id, new.body);
            END;

            CREATE TABLE channel_memberships (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                member_type TEXT NOT NULL,
                member_identity TEXT NOT NULL,
                membership_purpose TEXT,
                membership_status TEXT NOT NULL DEFAULT 'active',
                wake_policy TEXT NOT NULL DEFAULT 'mentions_only',
                can_send INTEGER NOT NULL DEFAULT 1,
                can_react INTEGER NOT NULL DEFAULT 1,
                can_invite INTEGER NOT NULL DEFAULT 0,
                cooldown_seconds INTEGER NOT NULL DEFAULT 60,
                max_auto_replies_per_window INTEGER NOT NULL DEFAULT 1,
                settings_json TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(channel_id, member_type, member_identity)
            );
            INSERT INTO channel_memberships(
                channel_id, member_type, member_identity, membership_purpose,
                membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window)
            VALUES (1, 'agent', 'legacy-gateway', 'target_work', 'active', 'mentions_only', 1, 1, 0, 60, 1);

            CREATE TABLE channel_activity_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                project_id TEXT,
                agent_identity TEXT NOT NULL,
                delivery_request_id TEXT,
                session_key TEXT,
                hermes_session_key TEXT,
                display_block_id TEXT,
                parent_session_key TEXT,
                parent_hermes_session_key TEXT,
                parent_agent_identity TEXT,
                worker_run_id TEXT,
                worker_role TEXT,
                agent_instance_id TEXT,
                pool_member_id TEXT,
                task_id INTEGER,
                thread_id INTEGER,
                anchor_message_id INTEGER,
                assignment_id TEXT,
                checkpoint_type TEXT,
                checkpoint_handle TEXT,
                event_type TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'completed',
                delivery_stage TEXT NOT NULL DEFAULT 'progress',
                terminal INTEGER NOT NULL DEFAULT 0,
                final_channel_message_id INTEGER,
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

            CREATE TABLE channel_project_links (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id),
                project_id TEXT NOT NULL,
                relation_kind TEXT NOT NULL DEFAULT 'linked',
                is_primary INTEGER NOT NULL DEFAULT 0 CHECK (is_primary IN (0, 1)),
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                settings_json TEXT,
                UNIQUE(channel_id, project_id)
            );

            CREATE TABLE channel_reactions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id INTEGER NOT NULL REFERENCES channel_messages(id) ON DELETE CASCADE,
                reactor_type TEXT NOT NULL,
                reactor_identity TEXT NOT NULL,
                reaction_emoji TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(message_id, reactor_type, reactor_identity, reaction_emoji)
            );

            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            INSERT INTO schema_migrations(version, name) VALUES
                (1,'initial'),(2,'read_cursors'),(3,'worker_pool_lobby'),
                (4,'channel_project_links'),(5,'agent_work_lifecycle'),
                (6,'instance_read_cursors'),(7,'channel_messages_fts5');
            """");

        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        var migrated = await QuerySingleAsync(connection, """"
            SELECT source_kind
            FROM channel_messages
            WHERE dedupe_key = 'legacy-gateway-1';
            """");
        Assert.Equal("external_adapter_message", migrated["source_kind"]);

        // The startup repair should also leave FTS triggers safe for future body updates.
        await ExecuteAsync(connection, """"
            UPDATE channel_messages
            SET body = 'Gateway delivery body updated'
            WHERE dedupe_key = 'legacy-gateway-1';
            """");
    }

    [Fact]
    public async Task V8Migration_BackfillsSubscriptionCursors_WithSubscriptionMessagesStreamKind()
    {
        // Simulate a pre-v8 DB with channel_read_cursors that have instance-scoped
        // agent read cursors. The V8 migration PART 5 should backfill them into
        // channel_subscription_cursors with stream_kind='subscription_messages'.
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ExecuteAsync(connection, """"
            CREATE TABLE channels (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                slug TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                kind TEXT NOT NULL,
                project_id TEXT
            );
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-den-channels', 'Den Channels', 'project_default', 'den-channels');

            CREATE TABLE channel_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                sender_type TEXT NOT NULL,
                sender_identity TEXT NOT NULL,
                body TEXT NOT NULL,
                message_kind TEXT NOT NULL DEFAULT 'human_text',
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            INSERT INTO channel_messages(id, channel_id, sender_type, sender_identity, body)
            VALUES (42, 1, 'agent', 'backfill-test-agent', 'test message');

            CREATE TABLE channel_memberships (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                member_type TEXT NOT NULL,
                member_identity TEXT NOT NULL,
                membership_purpose TEXT,
                membership_status TEXT NOT NULL DEFAULT 'active',
                wake_policy TEXT NOT NULL DEFAULT 'mentions_only',
                can_send INTEGER NOT NULL DEFAULT 1,
                can_react INTEGER NOT NULL DEFAULT 1,
                can_invite INTEGER NOT NULL DEFAULT 0,
                cooldown_seconds INTEGER NOT NULL DEFAULT 60,
                max_auto_replies_per_window INTEGER NOT NULL DEFAULT 1,
                settings_json TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(channel_id, member_type, member_identity)
            );
            INSERT INTO channel_memberships(
                channel_id, member_type, member_identity,
                membership_purpose, membership_status, wake_policy,
                can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window)
            VALUES (
                1, 'agent', 'backfill-test-agent',
                'ordinary_channel', 'active', 'mentions_only',
                1, 1, 0, 60, 1
            );

            CREATE TABLE channel_read_cursors (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                reader_type TEXT NOT NULL CHECK (reader_type IN ('user','agent','role','group')),
                reader_identity TEXT NOT NULL,
                instance_id TEXT,
                last_read_channel_message_id INTEGER REFERENCES channel_messages(id) ON DELETE SET NULL,
                last_read_at TEXT NOT NULL DEFAULT (datetime('now')),
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(channel_id, reader_type, reader_identity, instance_id)
            );
            INSERT INTO channel_read_cursors(
                channel_id, reader_type, reader_identity,
                instance_id, last_read_channel_message_id, last_read_at)
            VALUES (
                1, 'agent', 'backfill-test-agent',
                'instance-abc', 42, datetime('now')
            );

            CREATE TABLE channel_activity_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
                project_id TEXT,
                agent_identity TEXT NOT NULL,
                delivery_request_id TEXT,
                session_key TEXT,
                hermes_session_key TEXT,
                display_block_id TEXT,
                parent_session_key TEXT,
                parent_hermes_session_key TEXT,
                parent_agent_identity TEXT,
                worker_run_id TEXT,
                worker_role TEXT,
                agent_instance_id TEXT,
                pool_member_id TEXT,
                task_id INTEGER,
                thread_id INTEGER,
                anchor_message_id INTEGER,
                assignment_id TEXT,
                checkpoint_type TEXT,
                checkpoint_handle TEXT,
                event_type TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'completed',
                delivery_stage TEXT NOT NULL DEFAULT 'progress',
                terminal INTEGER NOT NULL DEFAULT 0,
                final_channel_message_id INTEGER,
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
            INSERT INTO channel_activity_events(
                channel_id, project_id, agent_identity,
                event_type, status, dedupe_key)
            VALUES (
                1, 'den-channels', 'backfill-test-agent',
                'tool_call_started', 'completed', 'v8-cursor-backfill'
            );

            CREATE TABLE channel_project_links (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id),
                project_id TEXT NOT NULL,
                relation_kind TEXT NOT NULL DEFAULT 'linked',
                is_primary INTEGER NOT NULL DEFAULT 0 CHECK (is_primary IN (0, 1)),
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                settings_json TEXT,
                UNIQUE(channel_id, project_id)
            );

            CREATE TABLE channel_reactions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id INTEGER NOT NULL REFERENCES channel_messages(id) ON DELETE CASCADE,
                reactor_type TEXT NOT NULL,
                reactor_identity TEXT NOT NULL,
                reaction_emoji TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(message_id, reactor_type, reactor_identity, reaction_emoji)
            );

            -- schema_migrations must exist so ApplyMigrationsAsync can track v8
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            -- Pretend all prior migrations (1-7) are applied
            INSERT INTO schema_migrations(version, name) VALUES
                (1,'initial'),(2,'read_cursors'),(3,'worker_pool_lobby'),
                (4,'channel_project_links'),(5,'agent_work_lifecycle'),
                (6,'instance_read_cursors'),(7,'delivery_audit');
            """");

        // Run migrations — should run v8 only
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        // The backfill should have created a subscription cursor row
        // with stream_kind = 'subscription_messages'
        var cursorRow = await QuerySingleAsync(connection, """"
            SELECT csc.subscription_id, csc.stream_kind, csc.last_seen_id
            FROM channel_subscription_cursors csc
            JOIN channel_subscriptions cs ON cs.id = csc.subscription_id
            WHERE cs.member_type = 'agent'
              AND cs.member_identity = 'backfill-test-agent';
            """");
        Assert.Equal("subscription_messages", cursorRow["stream_kind"]);
        Assert.Equal("42", cursorRow["last_seen_id"]);
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
