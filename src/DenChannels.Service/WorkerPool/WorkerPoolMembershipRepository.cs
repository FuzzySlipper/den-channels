using DenChannels.Service.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

using DenChannels.Service.Channels;

namespace DenChannels.Service.Channels;

public sealed class WorkerPoolMembershipRepository : ChannelsRepositoryBase
{
    public WorkerPoolMembershipRepository(IOptions<DenChannelsOptions> options) : base(options)
    {
    }

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
                cooldown_seconds, max_auto_replies_per_window, settings_json, membership_purpose, profile_identity, member_role, left_at, created_at, updated_at;
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
                cooldown_seconds, max_auto_replies_per_window, settings_json, membership_purpose, profile_identity, member_role, left_at, created_at, updated_at;
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

}
