using DenChannels.Service.Configuration;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace DenChannels.Service.Channels;

public abstract partial class ChannelsRepositoryBase
{
    private readonly IOptions<DenChannelsOptions> _options;

    protected ChannelsRepositoryBase(IOptions<DenChannelsOptions> options)
    {
        _options = options;
    }

    protected async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
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

    protected static void AddChannelParameters(SqliteCommand command, CreateChannelRequest request)
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

    protected static void AddActivityParameters(SqliteCommand command, long channelId, AppendChannelActivityEventRequest request)
    {
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$projectId", (object?)request.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentIdentity", request.AgentIdentity);
        command.Parameters.AddWithValue("$deliveryRequestId", (object?)request.DeliveryRequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionKey", (object?)request.SessionKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$displayBlockId", (object?)request.DisplayBlockId ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentSessionKey", (object?)request.ParentSessionKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentAgentIdentity", (object?)request.ParentAgentIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$workerRunId", (object?)request.WorkerRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$workerRole", (object?)request.WorkerRole ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentInstanceId", (object?)request.AgentInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$poolMemberId", (object?)request.PoolMemberId ?? DBNull.Value);
        command.Parameters.AddWithValue("$taskId", (object?)request.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$threadId", (object?)request.ThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$anchorMessageId", (object?)request.AnchorMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$assignmentId", (object?)request.AssignmentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$checkpointType", (object?)request.CheckpointType ?? DBNull.Value);
        command.Parameters.AddWithValue("$checkpointHandle", (object?)request.CheckpointHandle ?? DBNull.Value);
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

    protected static object NormalizeDeliveryStage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DBNull.Value;
        return NormalizeActivityText(value, 80);
    }

    protected static string DefaultMessageKind(string senderType) => senderType == "agent" ? "agent_text" : "human_text";

    protected static string? DeriveMessageDeliveryRequestId(PostChannelMessageRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.DeliveryRequestId))
            return request.DeliveryRequestId;

        // gateway_delivery source_kind is retired (v8). No implicit derivation from source_kind.
        return null;
    }

    protected static object NormalizeActivityText(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return DBNull.Value;

        var redacted = SecretLikeValueRegex().Replace(value, match => $"{match.Groups[1].Value}\"[REDACTED]\"");
        return redacted.Length <= maxLength ? redacted : redacted[..maxLength] + "…";
    }

    [GeneratedRegex("((?:\\\"?(?:api[_-]?key|access[_-]?token|refresh[_-]?token|auth(?:orization)?|token|password|secret)\\\"?\\s*[:=]\\s*))(\\\"[^\\\"]*\\\"|[^,}\\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    protected static partial Regex SecretLikeValueRegex();

    protected static ChannelDto ReadChannel(SqliteDataReader reader) => new(
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

    protected static ChannelMessageDto ReadMessage(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        GetNullableString(reader, 6),
        GetNullableString(reader, 7),
        GetNullableString(reader, 8),
        GetNullableString(reader, 23),  // target_project_id
        GetNullableInt64(reader, 24),   // target_task_id
        GetNullableString(reader, 25),  // worker_run_id
        GetNullableString(reader, 26),  // worker_role
        GetNullableString(reader, 27),  // profile_identity
        GetNullableString(reader, 9),   // summary
        GetNullableString(reader, 10),  // deep_link
        GetNullableInt64(reader, 11),   // thread_root_message_id
        GetNullableInt64(reader, 12),   // reply_to_message_id
        GetNullableString(reader, 13),  // metadata_json
        GetNullableString(reader, 14),  // delivery_request_id
        GetNullableString(reader, 15),  // dedupe_key
        GetNullableString(reader, 16),  // assignment_id
        GetNullableString(reader, 17),  // checkpoint_type
        GetNullableString(reader, 18),  // checkpoint_handle
        GetNullableString(reader, 19),  // agent_instance_id
        GetNullableString(reader, 20),  // pool_member_id
        GetNullableString(reader, 21),  // session_owner_id
        GetNullableString(reader, 22),  // session_id
        reader.GetString(28),           // created_at
        GetNullableString(reader, 29),  // edited_at
        GetNullableString(reader, 30)); // deleted_at

    protected static ChannelMembershipDto ReadMembership(SqliteDataReader reader) => new(
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
        GetNullableString(reader, 12),
        GetNullableString(reader, 13),
        GetNullableString(reader, 14),
        GetNullableString(reader, 15),
        reader.GetString(16),
        reader.GetString(17));

    protected static ChannelReactionDto ReadReaction(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5));

    protected static ChannelActivityEventDto ReadActivityEvent(SqliteDataReader reader) => new(
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
        GetNullableString(reader, 11),
        GetNullableString(reader, 12),
        GetNullableInt64(reader, 13),
        GetNullableInt64(reader, 14),
        GetNullableInt64(reader, 15),
        GetNullableString(reader, 16),
        GetNullableString(reader, 17),
        GetNullableString(reader, 18),
        reader.GetString(19),
        reader.GetString(20),
        reader.GetString(21),
        reader.GetBoolean(22),
        reader.GetInt64(23),
        reader.GetInt64(24),
        GetNullableString(reader, 25),
        GetNullableString(reader, 26),
        GetNullableString(reader, 27),
        GetNullableString(reader, 28),
        GetNullableString(reader, 29),
        GetNullableInt64(reader, 30),
        reader.GetString(31),
        reader.GetString(32));

    protected static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    protected static long? GetNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    // =========================================================================
    // Channel-project link operations (task #1874)
    // =========================================================================

    /// <summary>
    /// Get all project links for a given channel.
    /// </summary>

    protected static ChannelProjectLinkDto ReadProjectLink(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetBoolean(4),
        GetNullableString(reader, 5),
        reader.GetString(6));

    // =========================================================================
    // Agents Overview read queries
    // =========================================================================


    protected static ChannelReadCursorDto ReadReadCursor(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        // Convert '' back to null in DTO for API backward compatibility
        // (profile-level cursor shows as null InstanceId externally)
        NormalizeReadCursorInstanceId(reader, 4),
        GetNullableInt64(reader, 5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8));

    protected static string? NormalizeReadCursorInstanceId(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetString(ordinal);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    // =========================================================================
    // Worker-pool lobby operations (task #1771)
    // =========================================================================

    /// <summary>
    /// Ensure the #worker-pool lobby channel exists (slug='worker-pool', kind='system').
    /// Returns the channel DTO. Idempotent — uses ON CONFLICT on slug.
    /// </summary>

    protected static WorkerPoolLobbyPresenceDto ReadWorkerPoolLobbyPresence(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        GetNullableString(reader, 3),
        GetNullableString(reader, 4),
        GetNullableString(reader, 5),
        GetNullableString(reader, 6),
        reader.GetString(7),
        GetNullableString(reader, 8),
        GetNullableString(reader, 9),
        GetNullableString(reader, 10),
        GetNullableString(reader, 11),
        reader.GetString(12),
        reader.GetString(13));

    // =========================================================================
    // Worker-pool membership lifecycle (task #1880)
    // =========================================================================

}
