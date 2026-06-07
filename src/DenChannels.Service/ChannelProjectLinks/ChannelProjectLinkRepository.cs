using DenChannels.Service.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

using DenChannels.Service.Channels;

namespace DenChannels.Service.Channels;

public sealed class ChannelProjectLinkRepository : ChannelsRepositoryBase
{
    public ChannelProjectLinkRepository(IOptions<DenChannelsOptions> options) : base(options)
    {
    }

    public async Task<IReadOnlyList<ChannelProjectLinkDto>> GetChannelProjectLinksAsync(
        long channelId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, channel_id, project_id, relation_kind, is_primary, settings_json, created_at
            FROM channel_project_links
            WHERE channel_id = $channelId
            ORDER BY is_primary DESC, project_id ASC;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        var rows = new List<ChannelProjectLinkDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadProjectLink(reader));
        return rows;
    }

    /// <summary>
    /// Get all channels linked to a given project.
    /// </summary>
    public async Task<IReadOnlyList<ChannelDto>> GetLinkedChannelsForProjectAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.slug, c.display_name, c.kind, c.project_id, c.space_id,
                   c.created_by, c.visibility, c.settings_json, c.created_at, c.updated_at, c.archived_at
            FROM channels c
            JOIN channel_project_links cpl ON cpl.channel_id = c.id
            WHERE cpl.project_id = $projectId
            ORDER BY cpl.is_primary DESC, c.id ASC;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        var rows = new List<ChannelDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadChannel(reader));
        return rows;
    }

    /// <summary>
    /// Upsert a channel-project link. Creates the link or updates the relation_kind,
    /// is_primary, and settings_json if it already exists.
    /// </summary>
    public async Task<ChannelProjectLinkDto> UpsertChannelProjectLinkAsync(
        UpsertChannelProjectLinkRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channel_project_links(channel_id, project_id, relation_kind, is_primary, settings_json)
            VALUES ($channelId, $projectId, $relationKind, $isPrimary, $settingsJson)
            ON CONFLICT(channel_id, project_id)
            DO UPDATE SET
                relation_kind = CASE
                    WHEN $relationKind IS NOT NULL THEN $relationKind
                    ELSE channel_project_links.relation_kind
                END,
                is_primary = CASE
                    WHEN $isPrimary IS NOT NULL THEN $isPrimary
                    ELSE channel_project_links.is_primary
                END,
                settings_json = COALESCE($settingsJson, channel_project_links.settings_json)
            RETURNING id, channel_id, project_id, relation_kind, is_primary, settings_json, created_at;
            """;
        command.Parameters.AddWithValue("$channelId", request.ChannelId);
        command.Parameters.AddWithValue("$projectId", request.ProjectId);
        command.Parameters.AddWithValue("$relationKind", (object?)request.RelationKind ?? "linked");
        command.Parameters.AddWithValue("$isPrimary", request.IsPrimary ?? false ? 1 : 0);
        command.Parameters.AddWithValue("$settingsJson", (object?)request.SettingsJson ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadProjectLink(reader);
    }

    /// <summary>
    /// Remove a channel-project link.
    /// </summary>
    public async Task RemoveChannelProjectLinkAsync(
        long channelId, string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM channel_project_links
            WHERE channel_id = $channelId
              AND project_id = $projectId;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$projectId", projectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }


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
}
