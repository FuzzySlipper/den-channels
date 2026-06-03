using DenChannels.Service.Channels;
using DenChannels.Service.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenChannels.Service.Tests;

/// <summary>
/// Tests for shared-profile concrete agent-instance support (#1769).
/// Verifies that two spawned-coder instances sharing a profile identity
/// maintain independent activity traces and read cursors.
/// </summary>
public sealed class SharedProfileInstanceTests
{
    /// <summary>
    /// Two same-profile worker instances in the same channel/project maintain
    /// distinct activity traces via agent_instance_id/pool_member_id.
    /// </summary>
    [Fact]
    public async Task TwoSameProfileInstances_MaintainDistinctActivityTraces()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        // Create channel
        await ExecuteAsync(connection, """
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-instance-test', 'Instance Test', 'project_default', 'instance-test');
            """);

        // Instance A posts a message and activity events
        await ExecuteAsync(connection, """
            INSERT INTO channel_messages(
                channel_id, sender_type, sender_identity, body, message_kind,
                agent_instance_id, pool_member_id, assignment_id)
            VALUES (1, 'agent', 'spawned-coder', 'Instance A message', 'agent_text',
                    'inst-a', 'pool-member-a', 'assign-a-001');
            """);

        // Instance A activity events
        await ExecuteAsync(connection, """
            INSERT INTO channel_activity_events(
                channel_id, agent_identity, agent_instance_id, pool_member_id,
                assignment_id, event_type, status, sequence, dedupe_key)
            VALUES (1, 'spawned-coder', 'inst-a', 'pool-member-a',
                    'assign-a-001', 'tool_call_started', 'started', 1, 'inst-a:evt:1');
            """);
        await ExecuteAsync(connection, """
            INSERT INTO channel_activity_events(
                channel_id, agent_identity, agent_instance_id, pool_member_id,
                assignment_id, event_type, status, sequence, dedupe_key)
            VALUES (1, 'spawned-coder', 'inst-a', 'pool-member-a',
                    'assign-a-001', 'tool_call_completed', 'completed', 2, 'inst-a:evt:2');
            """);

        // Instance B posts a message and activity events
        await ExecuteAsync(connection, """
            INSERT INTO channel_messages(
                channel_id, sender_type, sender_identity, body, message_kind,
                agent_instance_id, pool_member_id, assignment_id)
            VALUES (1, 'agent', 'spawned-coder', 'Instance B message', 'agent_text',
                    'inst-b', 'pool-member-b', 'assign-b-001');
            """);

        // Instance B activity events
        await ExecuteAsync(connection, """
            INSERT INTO channel_activity_events(
                channel_id, agent_identity, agent_instance_id, pool_member_id,
                assignment_id, event_type, status, sequence, dedupe_key)
            VALUES (1, 'spawned-coder', 'inst-b', 'pool-member-b',
                    'assign-b-001', 'tool_call_started', 'started', 1, 'inst-b:evt:1');
            """);

        // Verify: Instance A's activity events are distinct
        var instanceAEvents = await QueryAllAsync(connection, """
            SELECT agent_instance_id, pool_member_id, assignment_id, event_type, dedupe_key
            FROM channel_activity_events
            WHERE channel_id = 1
              AND agent_instance_id = 'inst-a'
            ORDER BY sequence ASC;
            """);

        Assert.Equal(2, instanceAEvents.Count);
        Assert.Equal("inst-a", instanceAEvents[0]["agent_instance_id"]);
        Assert.Equal("pool-member-a", instanceAEvents[0]["pool_member_id"]);
        Assert.Equal("assign-a-001", instanceAEvents[0]["assignment_id"]);
        Assert.Equal("tool_call_started", instanceAEvents[0]["event_type"]);
        Assert.Equal("tool_call_completed", instanceAEvents[1]["event_type"]);

        // Verify: Instance B's activity events are distinct
        var instanceBEvents = await QueryAllAsync(connection, """
            SELECT agent_instance_id, pool_member_id, assignment_id, event_type, dedupe_key
            FROM channel_activity_events
            WHERE channel_id = 1
              AND agent_instance_id = 'inst-b'
            ORDER BY sequence ASC;
            """);

        Assert.Single(instanceBEvents);
        Assert.Equal("inst-b", instanceBEvents[0]["agent_instance_id"]);
        Assert.Equal("pool-member-b", instanceBEvents[0]["pool_member_id"]);
        Assert.Equal("assign-b-001", instanceBEvents[0]["assignment_id"]);

        // Verify: No cross-contamination — each instance's events are filtered correctly
        var aFilteredByB = await CountRowsAsync(connection, """
            SELECT 1 FROM channel_activity_events
            WHERE channel_id = 1
              AND agent_instance_id = 'inst-b'
              AND dedupe_key LIKE 'inst-a:%';
            """);
        Assert.Equal(0, aFilteredByB);

        var bFilteredByA = await CountRowsAsync(connection, """
            SELECT 1 FROM channel_activity_events
            WHERE channel_id = 1
              AND agent_instance_id = 'inst-a'
              AND dedupe_key LIKE 'inst-b:%';
            """);
        Assert.Equal(0, bFilteredByA);

        // Verify: Listing activity events with agentInstanceId filter keeps instances separate
        var listA = await QueryAllAsync(connection, """
            SELECT agent_instance_id, pool_member_id
            FROM channel_activity_events
            WHERE channel_id = 1
              AND agent_instance_id = 'inst-a'
            ORDER BY sequence ASC;
            """);
        Assert.All(listA, row => Assert.Equal("inst-a", row["agent_instance_id"]));
        Assert.All(listA, row => Assert.Equal("pool-member-a", row["pool_member_id"]));

        var listB = await QueryAllAsync(connection, """
            SELECT agent_instance_id, pool_member_id
            FROM channel_activity_events
            WHERE channel_id = 1
              AND agent_instance_id = 'inst-b'
            ORDER BY sequence ASC;
            """);
        Assert.All(listB, row => Assert.Equal("inst-b", row["agent_instance_id"]));
        Assert.All(listB, row => Assert.Equal("pool-member-b", row["pool_member_id"]));

        // Verify: profile-scoped (without agent_instance_id filter) returns all events
        var allEvents = await CountRowsAsync(connection, """
            SELECT 1 FROM channel_activity_events
            WHERE channel_id = 1;
            """);
        Assert.Equal(3, allEvents);
    }

    /// <summary>
    /// Two same-profile instances in the same channel maintain independent
    /// read cursor positions via instance_id. One instance's cursor update
    /// does not mask or consume the other's position.
    /// </summary>
    [Fact]
    public async Task TwoSameProfileInstances_MaintainIndependentReadCursors()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        // Create channel and messages
        await ExecuteAsync(connection, """
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-cursor-test', 'Cursor Test', 'project_default', 'cursor-test');
            """);
        for (var i = 1; i <= 5; i++)
        {
            await ExecuteAsync(connection, $"""
                INSERT INTO channel_messages(channel_id, sender_type, sender_identity, body, message_kind)
                VALUES (1, 'system', 'test', 'Message {i}', 'human_text');
                """);
        }

        // Profile-level cursor: spawned-coder has read up to msg 2
        await ExecuteAsync(connection, """
            INSERT INTO channel_read_cursors(channel_id, reader_type, reader_identity, instance_id, last_read_channel_message_id)
            VALUES (1, 'agent', 'spawned-coder', '', 2);
            """);

        // Instance A cursor: read up to msg 3
        await ExecuteAsync(connection, """
            INSERT INTO channel_read_cursors(channel_id, reader_type, reader_identity, instance_id, last_read_channel_message_id)
            VALUES (1, 'agent', 'spawned-coder', 'inst-a', 3);
            """);

        // Instance B cursor: read up to msg 1
        await ExecuteAsync(connection, """
            INSERT INTO channel_read_cursors(channel_id, reader_type, reader_identity, instance_id, last_read_channel_message_id)
            VALUES (1, 'agent', 'spawned-coder', 'inst-b', 1);
            """);

        // Verify: profile-level cursor is unaffected by instance cursors
        var profileCursor = await QuerySingleAsync(connection, """
            SELECT last_read_channel_message_id
            FROM channel_read_cursors
            WHERE channel_id = 1
              AND reader_type = 'agent'
              AND reader_identity = 'spawned-coder'
              AND instance_id = '';
            """);
        Assert.Equal("2", profileCursor["last_read_channel_message_id"]);

        // Verify: Instance A cursor is independent
        var cursorA = await QuerySingleAsync(connection, """
            SELECT last_read_channel_message_id
            FROM channel_read_cursors
            WHERE channel_id = 1
              AND reader_type = 'agent'
              AND reader_identity = 'spawned-coder'
              AND instance_id = 'inst-a';
            """);
        Assert.Equal("3", cursorA["last_read_channel_message_id"]);

        // Verify: Instance B cursor is independent
        var cursorB = await QuerySingleAsync(connection, """
            SELECT last_read_channel_message_id
            FROM channel_read_cursors
            WHERE channel_id = 1
              AND reader_type = 'agent'
              AND reader_identity = 'spawned-coder'
              AND instance_id = 'inst-b';
            """);
        Assert.Equal("1", cursorB["last_read_channel_message_id"]);

        // Verify: total row count
        Assert.Equal(3, await CountRowsAsync(connection, "channel_read_cursors"));

        // Verify: updating Instance A's cursor does not affect Instance B
        await ExecuteAsync(connection, """
            UPDATE channel_read_cursors
            SET last_read_channel_message_id = 5, instance_id = 'inst-a'
            WHERE channel_id = 1
              AND reader_type = 'agent'
              AND reader_identity = 'spawned-coder'
              AND instance_id = 'inst-a';
            """);

        cursorA = await QuerySingleAsync(connection, """
            SELECT last_read_channel_message_id
            FROM channel_read_cursors
            WHERE channel_id = 1
              AND reader_type = 'agent'
              AND reader_identity = 'spawned-coder'
              AND instance_id = 'inst-a';
            """);
        Assert.Equal("5", cursorA["last_read_channel_message_id"]);

        // Instance B still at 1
        cursorB = await QuerySingleAsync(connection, """
            SELECT last_read_channel_message_id
            FROM channel_read_cursors
            WHERE channel_id = 1
              AND reader_type = 'agent'
              AND reader_identity = 'spawned-coder'
              AND instance_id = 'inst-b';
            """);
        Assert.Equal("1", cursorB["last_read_channel_message_id"]);

        // Profile-level still at 2
        profileCursor = await QuerySingleAsync(connection, """
            SELECT last_read_channel_message_id
            FROM channel_read_cursors
            WHERE channel_id = 1
              AND reader_type = 'agent'
              AND reader_identity = 'spawned-coder'
              AND instance_id = '';
            """);
        Assert.Equal("2", profileCursor["last_read_channel_message_id"]);
    }

    /// <summary>
    /// ChannelMessageDto and ChannelActivityEventDto carry concrete identity
    /// fields (AgentInstanceId, PoolMemberId) alongside shared profile identity.
    /// </summary>
    [Fact]
    public async Task InstanceDtoFields_CarryConcreteIdentity()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        await ExecuteAsync(connection, """
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-dto-test', 'DTO Test', 'project_default', 'dto-test');
            INSERT INTO channel_messages(
                channel_id, sender_type, sender_identity, body, message_kind,
                agent_instance_id, pool_member_id, assignment_id)
            VALUES (1, 'agent', 'spawned-coder', 'DTO test message', 'agent_text',
                    'dto-inst-a', 'dto-pool-a', 'dto-assign-1');
            INSERT INTO channel_activity_events(
                channel_id, agent_identity, agent_instance_id, pool_member_id,
                assignment_id, event_type, status, sequence, dedupe_key)
            VALUES (1, 'spawned-coder', 'dto-inst-a', 'dto-pool-a',
                    'dto-assign-1', 'tool_call_started', 'started', 1, 'dto:evt:1');
            """);

        // Read message back via explicit column check
        var message = await QuerySingleAsync(connection, """
            SELECT agent_instance_id, pool_member_id, assignment_id, sender_identity
            FROM channel_messages
            WHERE channel_id = 1
              AND agent_instance_id = 'dto-inst-a';
            """);

        Assert.Equal("dto-inst-a", message["agent_instance_id"]);
        Assert.Equal("dto-pool-a", message["pool_member_id"]);
        Assert.Equal("dto-assign-1", message["assignment_id"]);
        Assert.Equal("spawned-coder", message["sender_identity"]);

        var activity = await QuerySingleAsync(connection, """
            SELECT agent_instance_id, pool_member_id, assignment_id, agent_identity
            FROM channel_activity_events
            WHERE channel_id = 1
              AND agent_instance_id = 'dto-inst-a';
            """);

        Assert.Equal("dto-inst-a", activity["agent_instance_id"]);
        Assert.Equal("dto-pool-a", activity["pool_member_id"]);
        Assert.Equal("dto-assign-1", activity["assignment_id"]);
        Assert.Equal("spawned-coder", activity["agent_identity"]);
    }

    /// <summary>
    /// Two same-profile instances posting messages in the same channel carry
    /// distinct session_owner_id/session_id so runtime sessions stay isolated.
    /// </summary>
    [Fact]
    public async Task TwoSameProfileInstances_DistinctSessionOwnerFields()
    {
        await using var connection = await OpenInMemoryDatabaseAsync();
        await ChannelsDatabaseInitializer.ApplyMigrationsAsync(connection, NullLogger.Instance);

        await ExecuteAsync(connection, """
            INSERT INTO channels(slug, display_name, kind, project_id)
            VALUES ('project-session-owner-test', 'Session Owner Test', 'project_default', 'session-owner-test');
            """);

        // Instance A: runner assignment 141
        await ExecuteAsync(connection, """
            INSERT INTO channel_messages(
                channel_id, sender_type, sender_identity, body, message_kind,
                agent_instance_id, pool_member_id, session_owner_id, session_id, assignment_id)
            VALUES (1, 'agent', 'spawned-coder', 'Instance A message', 'agent_text',
                    'inst-runner-141', 'pool-member-141', 'runner-inst-141', 'session-runner-141', '141');
            """);

        // Instance B: runner assignment 188 — same profile, distinct session
        await ExecuteAsync(connection, """
            INSERT INTO channel_messages(
                channel_id, sender_type, sender_identity, body, message_kind,
                agent_instance_id, pool_member_id, session_owner_id, session_id, assignment_id)
            VALUES (1, 'agent', 'spawned-coder', 'Instance B message', 'agent_text',
                    'inst-runner-188', 'pool-member-188', 'runner-inst-188', 'session-runner-188', '188');
            """);

        // Verify: distinct session_owner_id per instance
        var msgA = await QuerySingleAsync(connection, """
            SELECT agent_instance_id, session_owner_id, session_id, assignment_id, sender_identity
            FROM channel_messages
            WHERE channel_id = 1
              AND agent_instance_id = 'inst-runner-141';
            """);
        Assert.Equal("inst-runner-141", msgA["agent_instance_id"]);
        Assert.Equal("runner-inst-141", msgA["session_owner_id"]);
        Assert.Equal("session-runner-141", msgA["session_id"]);
        Assert.Equal("141", msgA["assignment_id"]);
        Assert.Equal("spawned-coder", msgA["sender_identity"]);

        var msgB = await QuerySingleAsync(connection, """
            SELECT agent_instance_id, session_owner_id, session_id, assignment_id, sender_identity
            FROM channel_messages
            WHERE channel_id = 1
              AND agent_instance_id = 'inst-runner-188';
            """);
        Assert.Equal("inst-runner-188", msgB["agent_instance_id"]);
        Assert.Equal("runner-inst-188", msgB["session_owner_id"]);
        Assert.Equal("session-runner-188", msgB["session_id"]);
        Assert.Equal("188", msgB["assignment_id"]);
        Assert.Equal("spawned-coder", msgB["sender_identity"]);

        // Same profile (sender_identity) but distinct sessions
        Assert.Equal(msgA["sender_identity"], msgB["sender_identity"]);
        Assert.NotEqual(msgA["agent_instance_id"], msgB["agent_instance_id"]);
        Assert.NotEqual(msgA["session_owner_id"], msgB["session_owner_id"]);
        Assert.NotEqual(msgA["session_id"], msgB["session_id"]);

        // Verify total count: 2 messages
        var count = await CountRowsAsync(connection, "channel_messages");
        Assert.Equal(2, count);
    }

    private static async Task<SqliteConnection> OpenInMemoryDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<int> CountRowsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        // Strip trailing semicolons to avoid nesting issues
        var cleanSql = sql.TrimEnd(';', ' ', '\t', '\r', '\n');
        command.CommandText = $"SELECT COUNT(*) FROM ({cleanSql}) AS count_query;";
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
        var hasRow = await reader.ReadAsync();
        Assert.True(hasRow, "Expected at least one row but query returned no rows.");

        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[reader.GetName(i)] = reader.IsDBNull(i) ? "" : reader.GetString(i);
        }
        return row;
    }

    private static async Task<List<Dictionary<string, string>>> QueryAllAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, string>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? "" : reader.GetString(i);
            rows.Add(row);
        }
        return rows;
    }
}
