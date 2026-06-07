using DenChannels.Service.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace DenChannels.Service.Channels;

public sealed class ChannelOverviewRepository : ChannelsRepositoryBase
{
    public ChannelOverviewRepository(IOptions<DenChannelsOptions> options) : base(options)
    {
    }

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
}
