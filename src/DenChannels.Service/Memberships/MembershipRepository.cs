using DenChannels.Service.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

using DenChannels.Service.Channels;

namespace DenChannels.Service.Channels;

public sealed class MembershipRepository : ChannelsRepositoryBase
{
    public MembershipRepository(IOptions<DenChannelsOptions> options, ChannelRepository channels) : base(options)
    {
        _channels = channels;
    }

    private readonly ChannelRepository _channels;

    public async Task<IReadOnlyList<ChannelMembershipDto>> ListMembershipsAsync(long channelId, int limit = 200,
        CancellationToken cancellationToken = default, bool includeLeft = true, int? leftGraceMinutes = null)
    {
        limit = Math.Clamp(limit, 1, 500);
        var clampedLeftGraceMinutes = leftGraceMinutes.HasValue ? Math.Clamp(leftGraceMinutes.Value, 0, 10080) : (int?)null;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """"
            SELECT id, channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json, membership_purpose, profile_identity, member_role, left_at, created_at, updated_at
            FROM channel_memberships
            WHERE channel_id = $channelId
              AND (
                    membership_status != 'left'
                    OR ($includeLeft = 1 AND $leftGraceMinutes IS NULL)
                    OR ($includeLeft = 1 AND $leftGraceMinutes IS NOT NULL AND updated_at >= datetime('now', '-' || $leftGraceMinutes || ' minutes'))
                  )
            ORDER BY id ASC
            LIMIT $limit;
            """";
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$includeLeft", includeLeft ? 1 : 0);
        command.Parameters.AddWithValue("$leftGraceMinutes", (object?)clampedLeftGraceMinutes ?? DBNull.Value);
        var rows = new List<ChannelMembershipDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadMembership(reader));
        return rows;
    }


    public async Task<ChannelMembershipDto> UpsertMembershipAsync(long channelId, UpsertChannelMembershipRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """"
            INSERT INTO channel_memberships(
                channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json, membership_purpose, profile_identity, member_role)
            VALUES ($channelId, $memberType, $memberIdentity, $membershipStatus, $wakePolicy, $canSend, $canReact, $canInvite,
                $cooldownSeconds, $maxAutoRepliesPerWindow, $settingsJson, $membershipPurpose, $profileIdentity, $memberRole)
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
                membership_purpose = CASE
                    WHEN $membershipPurpose IS NOT NULL THEN $membershipPurpose
                    ELSE channel_memberships.membership_purpose
                END,
                profile_identity = COALESCE(excluded.profile_identity, channel_memberships.profile_identity),
                member_role = COALESCE(excluded.member_role, channel_memberships.member_role),
                left_at = CASE
                    WHEN excluded.membership_status = 'left'
                        AND channel_memberships.left_at IS NULL THEN datetime('now')
                    ELSE channel_memberships.left_at
                END,
                updated_at = datetime('now')
            RETURNING id, channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json, membership_purpose, profile_identity, member_role, left_at, created_at, updated_at;
            """";
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
        command.Parameters.AddWithValue("$membershipPurpose", (object?)request.MembershipPurpose ?? DBNull.Value);
        command.Parameters.AddWithValue("$profileIdentity", (object?)request.ProfileIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$memberRole", (object?)request.MemberRole ?? DBNull.Value);
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
        var commons = await _channels.EnsureAgentCommonsChannelAsync(cancellationToken);
        const string defaultSettingsJson = "{\"systemManaged\":true,\"source\":\"agent-commons-auto-membership\"}";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """"
            INSERT INTO channel_memberships(
                channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json, membership_purpose)
            VALUES ($channelId, 'agent', $agentIdentity, 'active', 'mentions_only', 1, 1, 0, 60, 1, $settingsJson, 'agent_commons')
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
                membership_purpose = 'agent_commons',
                updated_at = datetime('now')
            RETURNING id, channel_id, member_type, member_identity, membership_status, wake_policy, can_send, can_react, can_invite,
                cooldown_seconds, max_auto_replies_per_window, settings_json, membership_purpose, profile_identity, member_role, left_at, created_at, updated_at;
            """";
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
        var commons = await _channels.EnsureAgentCommonsChannelAsync(cancellationToken);
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


    public async Task<IReadOnlyList<ChannelMembershipDto>> ListMembershipsForOverviewAsync(
        string? projectId = null, long? channelId = null, string? agentIdentity = null, bool includeLeft = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """"
            SELECT m.id, m.channel_id, m.member_type, m.member_identity, m.membership_status, m.wake_policy,
                   m.can_send, m.can_react, m.can_invite, m.cooldown_seconds, m.max_auto_replies_per_window,
                   m.settings_json, m.membership_purpose, m.profile_identity, m.member_role, m.left_at, m.created_at, m.updated_at
            FROM channel_memberships m
            JOIN channels c ON c.id = m.channel_id
            WHERE m.member_type = 'agent'
              AND ($projectId IS NULL OR c.project_id = $projectId)
              AND ($channelId IS NULL OR m.channel_id = $channelId)
              AND ($agentIdentity IS NULL OR m.member_identity = $agentIdentity)
              AND ($includeLeft = 1 OR m.membership_status != 'left')
            ORDER BY m.member_identity, m.channel_id;
            """";
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
    /// List channel memberships for one member identity across channels, including channel metadata.
    /// Default discovery returns all active, non-left memberships regardless of membership_purpose.
    /// membership_purpose is a compatibility/opt-in filter; green-path runtime discovery uses
    /// GET /api/channel-subscriptions instead.
    /// </summary>
    public async Task<IReadOnlyList<ChannelMembershipDiscoveryRowDto>> ListMembershipsByMemberIdentityAsync(
        string memberIdentity, string? membershipPurpose = null, string? projectId = null, long? channelId = null,
        bool includeLeft = false, bool includeOrdinaryMemberships = false, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var normalizedMemberIdentity = memberIdentity.Trim();
        var normalizedPurpose = string.IsNullOrWhiteSpace(membershipPurpose) ? null : membershipPurpose.Trim();
        var clampedLimit = Math.Clamp(limit, 1, 500);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """"
            SELECT c.id, c.slug, c.kind, c.project_id,
                   m.id, m.channel_id, m.member_type, m.member_identity, m.membership_status, m.wake_policy,
                   m.can_send, m.can_react, m.can_invite, m.cooldown_seconds, m.max_auto_replies_per_window,
                   m.settings_json, m.membership_purpose, m.profile_identity, m.member_role, m.left_at, m.created_at, m.updated_at
            FROM channel_memberships m
            JOIN channels c ON c.id = m.channel_id
            WHERE m.member_identity = $memberIdentity
              AND ($projectId IS NULL OR c.project_id = $projectId)
              AND ($channelId IS NULL OR m.channel_id = $channelId)
              AND ($membershipPurpose IS NULL OR m.membership_purpose = $membershipPurpose)
              AND ($includeLeft = 1 OR m.membership_status != 'left')
            ORDER BY CASE m.membership_purpose
                       WHEN 'worker_pool_control' THEN 0
                       WHEN 'target_work' THEN 1
                       ELSE 2
                     END,
                     c.id ASC
            LIMIT $limit;
            """";
        command.Parameters.AddWithValue("$memberIdentity", normalizedMemberIdentity);
        command.Parameters.AddWithValue("$membershipPurpose", (object?)normalizedPurpose ?? DBNull.Value);
        command.Parameters.AddWithValue("$projectId", (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$channelId", (object?)channelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$includeLeft", includeLeft ? 1 : 0);
        // includeOrdinaryMemberships is retained as a request compatibility flag only.
        // Since v8 membership discovery returns all non-left memberships by default,
        // it intentionally does not add a SQL predicate or parameter.
        _ = includeOrdinaryMemberships;
        command.Parameters.AddWithValue("$limit", clampedLimit);

        var rows = new List<ChannelMembershipDiscoveryRowDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var membership = new ChannelMembershipDto(
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetBoolean(10),
                reader.GetBoolean(11),
                reader.GetBoolean(12),
                reader.GetInt32(13),
                reader.GetInt32(14),
                GetNullableString(reader, 15),
                GetNullableString(reader, 16),
                GetNullableString(reader, 17),
                GetNullableString(reader, 18),
                GetNullableString(reader, 19),
                reader.GetString(20),
                reader.GetString(21));
            rows.Add(new ChannelMembershipDiscoveryRowDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                GetNullableString(reader, 3),
                membership));
        }
        return rows;
    }
}
