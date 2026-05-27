using DenChannels.Service.Configuration;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace DenChannels.Service.Channels;

public sealed partial class ChannelsRepository
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

    public async Task<ChannelDto> EnsureAgentCommonsChannelAsync(CancellationToken cancellationToken = default)
    {
        const string settingsJson = "{\"systemManaged\":true,\"channelRole\":\"agent_commons\",\"defaultWakePolicy\":\"mentions_only\"}";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channels(slug, display_name, kind, created_by, visibility, settings_json)
            VALUES ('agent-commons', 'Agent Commons', 'system', 'system', 'normal', $settingsJson)
            ON CONFLICT(slug) DO UPDATE SET
                display_name = 'Agent Commons',
                kind = 'system',
                visibility = 'normal',
                settings_json = COALESCE(channels.settings_json, excluded.settings_json),
                updated_at = datetime('now')
            RETURNING id, slug, display_name, kind, project_id, space_id, created_by, visibility, settings_json, created_at, updated_at, archived_at;
            """;
        command.Parameters.AddWithValue("$settingsJson", settingsJson);
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
                summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, delivery_request_id, dedupe_key)
            VALUES (
                $channelId, $senderType, $senderIdentity, $body, $messageKind, $sourceKind, $sourceId, $sourceProjectId,
                $summary, $deepLink, $threadRootMessageId, $replyToMessageId, $metadataJson, $deliveryRequestId, $dedupeKey)
            RETURNING id, channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id, source_project_id,
                summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, delivery_request_id, dedupe_key, created_at, edited_at, deleted_at;
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
        command.Parameters.AddWithValue("$deliveryRequestId", (object?)DeriveMessageDeliveryRequestId(request) ?? DBNull.Value);
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
                  summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, delivery_request_id, dedupe_key, created_at, edited_at, deleted_at
              FROM (
                  SELECT id, channel_id, sender_type, sender_identity, body, message_kind, source_kind, source_id, source_project_id,
                      summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, delivery_request_id, dedupe_key, created_at, edited_at, deleted_at
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
                  summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, delivery_request_id, dedupe_key, created_at, edited_at, deleted_at
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
                summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, delivery_request_id, dedupe_key, created_at, edited_at, deleted_at
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
                summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, delivery_request_id, dedupe_key, created_at, edited_at, deleted_at
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
                summary, deep_link, thread_root_message_id, reply_to_message_id, metadata_json, delivery_request_id, dedupe_key, created_at, edited_at, deleted_at
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
        var membership = ReadMembership(reader);
        await reader.DisposeAsync();
        if (ShouldAutoEnsureAgentCommonsMembership(channelId, request, membership))
        {
            await EnsureAgentCommonsMembershipAsync(membership.MemberIdentity, null, cancellationToken);
        }
        return membership;
    }

    private static bool ShouldAutoEnsureAgentCommonsMembership(long sourceChannelId, UpsertChannelMembershipRequest request, ChannelMembershipDto membership)
    {
        if (!string.Equals(request.MemberType, "agent", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(membership.MembershipStatus, "active", StringComparison.OrdinalIgnoreCase)) return false;
        return sourceChannelId != membership.ChannelId || !string.Equals(membership.MemberIdentity, "", StringComparison.Ordinal);
    }

    public async Task<ChannelMembershipDto> EnsureAgentCommonsMembershipAsync(string agentIdentity, string? sourceSettingsJson = null,
        CancellationToken cancellationToken = default)
    {
        var commons = await EnsureAgentCommonsChannelAsync(cancellationToken);
        const string defaultSettingsJson = "{\"systemManaged\":true,\"source\":\"agent-commons-auto-membership\"}";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channel_memberships(
                channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json)
            VALUES ($channelId, 'agent', $agentIdentity, 'active', 'mentions_only', 1, 1, 0, 60, 1, $settingsJson)
            ON CONFLICT(channel_id, member_type, member_identity)
            DO UPDATE SET
                membership_status = CASE
                    WHEN channel_memberships.membership_status IN ('muted', 'left', 'banned') THEN channel_memberships.membership_status
                    WHEN channel_memberships.settings_json LIKE '%"systemManaged":true%' THEN 'active'
                    ELSE channel_memberships.membership_status
                END,
                wake_policy = CASE
                    WHEN channel_memberships.wake_policy = 'never' THEN channel_memberships.wake_policy
                    WHEN channel_memberships.membership_status IN ('muted', 'left', 'banned') THEN channel_memberships.wake_policy
                    WHEN channel_memberships.settings_json LIKE '%"systemManaged":true%' THEN 'mentions_only'
                    ELSE channel_memberships.wake_policy
                END,
                can_send = 1,
                can_react = 1,
                updated_at = datetime('now')
            RETURNING id, channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("$channelId", commons.Id);
        command.Parameters.AddWithValue("$agentIdentity", agentIdentity.Trim());
        command.Parameters.AddWithValue("$settingsJson", string.IsNullOrWhiteSpace(sourceSettingsJson) ? defaultSettingsJson : sourceSettingsJson);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadMembership(reader);
    }

    public async Task<AgentCommonsBrakeResultDto> ApplyAgentCommonsBrakeAsync(string membershipStatus = "muted", string wakePolicy = "never",
        CancellationToken cancellationToken = default)
    {
        var commons = await EnsureAgentCommonsChannelAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE channel_memberships
            SET membership_status = $membershipStatus,
                wake_policy = $wakePolicy,
                updated_at = datetime('now')
            WHERE channel_id = $channelId
              AND member_type = 'agent'
              AND membership_status = 'active';
            """;
        command.Parameters.AddWithValue("$channelId", commons.Id);
        command.Parameters.AddWithValue("$membershipStatus", membershipStatus);
        command.Parameters.AddWithValue("$wakePolicy", wakePolicy);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        return new AgentCommonsBrakeResultDto("applied", commons.Id, updated, membershipStatus, wakePolicy);
    }

    public async Task<IReadOnlyList<ChannelReactionSummaryDto>> ListReactionSummariesAsync(long channelId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.channel_message_id, r.reaction_key, r.reactor_type, r.reactor_identity
            FROM channel_reactions r
            JOIN channel_messages m ON m.id = r.channel_message_id
            WHERE m.channel_id = $channelId
            ORDER BY r.channel_message_id, r.reaction_key, r.created_at, r.id;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var grouped = new Dictionary<(long MessageId, string ReactionKey), List<string>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (reader.GetInt64(0), reader.GetString(1));
            if (!grouped.TryGetValue(key, out var reactors))
            {
                reactors = [];
                grouped[key] = reactors;
            }
            reactors.Add($"{reader.GetString(2)}:{reader.GetString(3)}");
        }
        return grouped
            .Select(item => new ChannelReactionSummaryDto(
                item.Key.MessageId,
                item.Key.ReactionKey,
                item.Value.Count,
                item.Value))
            .ToList();
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

    public async Task<ChannelActivityEventDto> AppendActivityEventAsync(long channelId,
        AppendChannelActivityEventRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channel_activity_events(
                channel_id, project_id, agent_identity, delivery_request_id, hermes_session_key,
                display_block_id, parent_hermes_session_key, parent_agent_identity, worker_run_id, worker_role,
                task_id, thread_id, anchor_message_id, event_type, status, delivery_stage, terminal, sequence,
                title, summary, preview_json, metadata_json, dedupe_key, final_channel_message_id)
            VALUES (
                $channelId, $projectId, $agentIdentity, $deliveryRequestId, $hermesSessionKey,
                $displayBlockId, $parentHermesSessionKey, $parentAgentIdentity, $workerRunId, $workerRole,
                $taskId, $threadId, $anchorMessageId, $eventType, $status, $deliveryStage, $terminal, $sequence,
                $title, $summary, $previewJson, $metadataJson, $dedupeKey, $finalChannelMessageId)
            ON CONFLICT(channel_id, dedupe_key) WHERE dedupe_key IS NOT NULL DO UPDATE SET
                project_id = COALESCE(excluded.project_id, channel_activity_events.project_id),
                agent_identity = excluded.agent_identity,
                delivery_request_id = COALESCE(excluded.delivery_request_id, channel_activity_events.delivery_request_id),
                hermes_session_key = COALESCE(excluded.hermes_session_key, channel_activity_events.hermes_session_key),
                display_block_id = COALESCE(excluded.display_block_id, channel_activity_events.display_block_id),
                parent_hermes_session_key = COALESCE(excluded.parent_hermes_session_key, channel_activity_events.parent_hermes_session_key),
                parent_agent_identity = COALESCE(excluded.parent_agent_identity, channel_activity_events.parent_agent_identity),
                worker_run_id = COALESCE(excluded.worker_run_id, channel_activity_events.worker_run_id),
                worker_role = COALESCE(excluded.worker_role, channel_activity_events.worker_role),
                task_id = COALESCE(excluded.task_id, channel_activity_events.task_id),
                thread_id = COALESCE(excluded.thread_id, channel_activity_events.thread_id),
                anchor_message_id = COALESCE(excluded.anchor_message_id, channel_activity_events.anchor_message_id),
                event_type = excluded.event_type,
                status = excluded.status,
                delivery_stage = excluded.delivery_stage,
                terminal = excluded.terminal,
                sequence = excluded.sequence,
                title = COALESCE(excluded.title, channel_activity_events.title),
                summary = COALESCE(excluded.summary, channel_activity_events.summary),
                preview_json = COALESCE(excluded.preview_json, channel_activity_events.preview_json),
                metadata_json = COALESCE(excluded.metadata_json, channel_activity_events.metadata_json),
                final_channel_message_id = COALESCE(excluded.final_channel_message_id, channel_activity_events.final_channel_message_id),
                update_version = channel_activity_events.update_version + 1,
                updated_at = datetime('now')
            RETURNING id, channel_id, project_id, agent_identity, delivery_request_id, hermes_session_key,
                display_block_id, parent_hermes_session_key, parent_agent_identity, worker_run_id, worker_role,
                task_id, thread_id, anchor_message_id, event_type, status, delivery_stage, terminal, sequence,
                update_version, title, summary, preview_json, metadata_json, dedupe_key, final_channel_message_id,
                created_at, updated_at;
            """;
        AddActivityParameters(command, channelId, request);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadActivityEvent(reader);
    }

    public async Task<ChannelActivityEventDto?> UpdateActivityEventAsync(long activityEventId,
        UpdateChannelActivityEventRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE channel_activity_events
            SET status = COALESCE($status, status),
                delivery_stage = COALESCE($deliveryStage, delivery_stage),
                terminal = COALESCE($terminal, terminal),
                title = COALESCE($title, title),
                summary = COALESCE($summary, summary),
                preview_json = COALESCE($previewJson, preview_json),
                metadata_json = COALESCE($metadataJson, metadata_json),
                final_channel_message_id = COALESCE($finalChannelMessageId, final_channel_message_id),
                update_version = update_version + 1,
                updated_at = datetime('now')
            WHERE id = $id
            RETURNING id, channel_id, project_id, agent_identity, delivery_request_id, hermes_session_key,
                display_block_id, parent_hermes_session_key, parent_agent_identity, worker_run_id, worker_role,
                task_id, thread_id, anchor_message_id, event_type, status, delivery_stage, terminal, sequence,
                update_version, title, summary, preview_json, metadata_json, dedupe_key, final_channel_message_id,
                created_at, updated_at;
            """;
        command.Parameters.AddWithValue("$id", activityEventId);
        command.Parameters.AddWithValue("$status", (object?)request.Status ?? DBNull.Value);
        command.Parameters.AddWithValue("$deliveryStage", NormalizeDeliveryStage(request.DeliveryStage));
        command.Parameters.AddWithValue("$terminal", request.Terminal.HasValue ? request.Terminal.Value ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$title", NormalizeActivityText(request.Title, 200));
        command.Parameters.AddWithValue("$summary", NormalizeActivityText(request.Summary, 1000));
        command.Parameters.AddWithValue("$previewJson", NormalizeActivityText(request.PreviewJson, 4000));
        command.Parameters.AddWithValue("$metadataJson", NormalizeActivityText(request.MetadataJson, 4000));
        command.Parameters.AddWithValue("$finalChannelMessageId", (object?)request.FinalChannelMessageId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadActivityEvent(reader) : null;
    }

    public async Task<IReadOnlyList<ChannelActivityEventDto>> ListActivityEventsAsync(long channelId,
        string? deliveryRequestId = null, string? hermesSessionKey = null, string? displayBlockId = null,
        string? workerRunId = null, long? anchorMessageId = null, long? taskId = null, long? afterId = null,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, channel_id, project_id, agent_identity, delivery_request_id, hermes_session_key,
                display_block_id, parent_hermes_session_key, parent_agent_identity, worker_run_id, worker_role,
                task_id, thread_id, anchor_message_id, event_type, status, delivery_stage, terminal, sequence,
                update_version, title, summary, preview_json, metadata_json, dedupe_key, final_channel_message_id,
                created_at, updated_at
            FROM channel_activity_events
            WHERE channel_id = $channelId
              AND ($deliveryRequestId IS NULL OR delivery_request_id = $deliveryRequestId)
              AND ($hermesSessionKey IS NULL OR hermes_session_key = $hermesSessionKey)
              AND ($displayBlockId IS NULL OR display_block_id = $displayBlockId)
              AND ($workerRunId IS NULL OR worker_run_id = $workerRunId)
              AND ($anchorMessageId IS NULL OR anchor_message_id = $anchorMessageId)
              AND ($taskId IS NULL OR task_id = $taskId)
              AND ($afterId IS NULL OR id > $afterId)
            ORDER BY sequence ASC, id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$deliveryRequestId", (object?)deliveryRequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$hermesSessionKey", (object?)hermesSessionKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$displayBlockId", (object?)displayBlockId ?? DBNull.Value);
        command.Parameters.AddWithValue("$workerRunId", (object?)workerRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$anchorMessageId", (object?)anchorMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$taskId", (object?)taskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$afterId", (object?)afterId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<ChannelActivityEventDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadActivityEvent(reader));
        return rows;
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

    private static void AddActivityParameters(SqliteCommand command, long channelId, AppendChannelActivityEventRequest request)
    {
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$projectId", (object?)request.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentIdentity", request.AgentIdentity);
        command.Parameters.AddWithValue("$deliveryRequestId", (object?)request.DeliveryRequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$hermesSessionKey", (object?)request.HermesSessionKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$displayBlockId", (object?)request.DisplayBlockId ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentHermesSessionKey", (object?)request.ParentHermesSessionKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentAgentIdentity", (object?)request.ParentAgentIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$workerRunId", (object?)request.WorkerRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$workerRole", (object?)request.WorkerRole ?? DBNull.Value);
        command.Parameters.AddWithValue("$taskId", (object?)request.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$threadId", (object?)request.ThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$anchorMessageId", (object?)request.AnchorMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$eventType", request.EventType);
        command.Parameters.AddWithValue("$status", request.Status ?? "completed");
        var deliveryStage = NormalizeDeliveryStage(request.DeliveryStage);
        command.Parameters.AddWithValue("$deliveryStage", deliveryStage is DBNull ? "progress" : deliveryStage);
        command.Parameters.AddWithValue("$terminal", request.Terminal == true ? 1 : 0);
        command.Parameters.AddWithValue("$sequence", request.Sequence ?? 0);
        command.Parameters.AddWithValue("$title", NormalizeActivityText(request.Title, 200));
        command.Parameters.AddWithValue("$summary", NormalizeActivityText(request.Summary, 1000));
        command.Parameters.AddWithValue("$previewJson", NormalizeActivityText(request.PreviewJson, 4000));
        command.Parameters.AddWithValue("$metadataJson", NormalizeActivityText(request.MetadataJson, 4000));
        command.Parameters.AddWithValue("$dedupeKey", (object?)request.DedupeKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$finalChannelMessageId", (object?)request.FinalChannelMessageId ?? DBNull.Value);
    }

    private static object NormalizeDeliveryStage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DBNull.Value;
        return NormalizeActivityText(value, 80);
    }

    private static string DefaultMessageKind(string senderType) => senderType == "agent" ? "agent_text" : "human_text";

    private static string? DeriveMessageDeliveryRequestId(PostChannelMessageRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.DeliveryRequestId))
            return request.DeliveryRequestId;

        if (string.Equals(request.SourceKind, "gateway_delivery", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(request.SourceId))
        {
            return request.SourceId;
        }

        return null;
    }

    private static object NormalizeActivityText(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return DBNull.Value;

        var redacted = SecretLikeValueRegex().Replace(value, match => $"{match.Groups[1].Value}\"[REDACTED]\"");
        return redacted.Length <= maxLength ? redacted : redacted[..maxLength] + "…";
    }

    [GeneratedRegex("((?:\\\"?(?:api[_-]?key|access[_-]?token|refresh[_-]?token|auth(?:orization)?|token|password|secret)\\\"?\\s*[:=]\\s*))(\\\"[^\\\"]*\\\"|[^,}\\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretLikeValueRegex();

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
        GetNullableString(reader, 15),
        reader.GetString(16),
        GetNullableString(reader, 17),
        GetNullableString(reader, 18));

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

    private static ChannelActivityEventDto ReadActivityEvent(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        GetNullableString(reader, 2),
        reader.GetString(3),
        GetNullableString(reader, 4),
        GetNullableString(reader, 5),
        GetNullableString(reader, 6),
        GetNullableString(reader, 7),
        GetNullableString(reader, 8),
        GetNullableString(reader, 9),
        GetNullableString(reader, 10),
        GetNullableInt64(reader, 11),
        GetNullableInt64(reader, 12),
        GetNullableInt64(reader, 13),
        reader.GetString(14),
        reader.GetString(15),
        reader.GetString(16),
        reader.GetBoolean(17),
        reader.GetInt64(18),
        reader.GetInt64(19),
        GetNullableString(reader, 20),
        GetNullableString(reader, 21),
        GetNullableString(reader, 22),
        GetNullableString(reader, 23),
        GetNullableString(reader, 24),
        GetNullableInt64(reader, 25),
        reader.GetString(26),
        reader.GetString(27));

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    // =========================================================================
    // Agents Overview read queries
    // =========================================================================

    /// <summary>
    /// List channels with optional project/channel filter. Returns channels with their membership counts.
    /// </summary>
    public async Task<IReadOnlyList<ChannelDto>> ListChannelsForOverviewAsync(
        string? projectId = null, long? channelId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, slug, display_name, kind, project_id, space_id, created_by, visibility, settings_json, created_at, updated_at, archived_at
            FROM channels
            WHERE ($projectId IS NULL OR project_id = $projectId)
              AND ($channelId IS NULL OR id = $channelId)
            ORDER BY updated_at DESC, id DESC;
            """;
        command.Parameters.AddWithValue("$projectId", (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$channelId", (object?)channelId ?? DBNull.Value);
        var rows = new List<ChannelDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadChannel(reader));
        return rows;
    }

    /// <summary>
    /// List memberships across channels, optionally filtered by member identity and project scope.
    /// </summary>
    public async Task<IReadOnlyList<ChannelMembershipDto>> ListMembershipsForOverviewAsync(
        string? projectId = null, long? channelId = null, string? agentIdentity = null, bool includeLeft = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.id, m.channel_id, m.member_type, m.member_identity, m.membership_status, m.wake_policy,
                   m.can_send, m.can_react, m.can_invite, m.cooldown_seconds, m.max_auto_replies_per_window,
                   m.settings_json, m.created_at, m.updated_at
            FROM channel_memberships m
            JOIN channels c ON c.id = m.channel_id
            WHERE m.member_type = 'agent'
              AND ($projectId IS NULL OR c.project_id = $projectId)
              AND ($channelId IS NULL OR m.channel_id = $channelId)
              AND ($agentIdentity IS NULL OR m.member_identity = $agentIdentity)
              AND ($includeLeft = 1 OR m.membership_status != 'left')
            ORDER BY m.member_identity, m.channel_id;
            """;
        command.Parameters.AddWithValue("$projectId", (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$channelId", (object?)channelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentIdentity", (object?)agentIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$includeLeft", includeLeft ? 1 : 0);
        var rows = new List<ChannelMembershipDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadMembership(reader));
        return rows;
    }

    /// <summary>
    /// List recent activity events across multi-channel scope, optionally filtered.
    /// Returns most recent events bounded by the per-agent limit.
    /// </summary>
    public async Task<IReadOnlyList<ChannelActivityEventDto>> ListRecentActivityForOverviewAsync(
        string? projectId = null, long? channelId = null, string? agentIdentity = null,
        int perAgentLimit = 3, CancellationToken cancellationToken = default)
    {
        var clampedLimit = Math.Clamp(perAgentLimit, 1, 100);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.channel_id, a.project_id, a.agent_identity, a.delivery_request_id, a.hermes_session_key,
                   a.display_block_id, a.parent_hermes_session_key, a.parent_agent_identity, a.worker_run_id, a.worker_role,
                   a.task_id, a.thread_id, a.anchor_message_id, a.event_type, a.status, a.delivery_stage, a.terminal,
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
            SELECT a.id, a.channel_id, a.project_id, a.agent_identity, a.delivery_request_id, a.hermes_session_key,
                   a.display_block_id, a.parent_hermes_session_key, a.parent_agent_identity, a.worker_run_id, a.worker_role,
                   a.task_id, a.thread_id, a.anchor_message_id, a.event_type, a.status, a.delivery_stage, a.terminal,
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
            SELECT a.id, a.channel_id, a.project_id, a.agent_identity, a.delivery_request_id, a.hermes_session_key,
                   a.display_block_id, a.parent_hermes_session_key, a.parent_agent_identity, a.worker_run_id, a.worker_role,
                   a.task_id, a.thread_id, a.anchor_message_id, a.event_type, a.status, a.delivery_stage, a.terminal,
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
}
