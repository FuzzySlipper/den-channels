namespace DenChannels.Service.DirectAgentEvents;

// =========================================================================
// Direct conversation DTOs
// =========================================================================

public sealed record DirectConversationDto(
    long Id,
    string HumanIdentity,
    string AgentIdentity,
    string? ScopeProjectId,
    string? DisplayTitle,
    bool IsArchived,
    bool IsMuted,
    string? SettingsJson,
    string? LastEntryAt,
    string? LastEntryPreview,
    string? LastEntrySender,
    string CreatedAt,
    string UpdatedAt,
    long UnreadCount = 0);

public sealed record DirectConversationListResponse(
    IReadOnlyList<DirectConversationDto> Conversations,
    long? NextCursor,
    bool HasMore);

public sealed record CreateDirectConversationRequest(
    string HumanIdentity,
    string AgentIdentity,
    string? ScopeProjectId = null,
    string? DisplayTitle = null);

public sealed record DirectConversationEntryDto(
    long Id,
    long ConversationId,
    long ChannelMessageId,
    string Direction,
    string SenderIdentity,
    string RecipientIdentity,
    long? SourceChannelId,
    string? SourceProjectId,
    long? SourceTaskId,
    string? SourceSessionOwnerId,
    string? SourceWorkerRunId,
    string? BodyPreview,
    string CreatedAt);

public sealed record DirectConversationEntryListResponse(
    IReadOnlyList<DirectConversationEntryDto> Entries,
    long? NextCursor,
    bool HasMore);

public sealed record SendDirectMessageRequest(
    string SenderIdentity,
    string Body,
    string? SourceProjectId = null,
    long? TargetTaskId = null,
    string? WorkerRunId = null,
    string? WorkerRole = null,
    string? ProfileIdentity = null,
    string? PoolMemberId = null,
    string? AgentInstanceId = null,
    string? SessionOwnerId = null,
    string? SessionId = null);

public sealed record DirectMessageResponse(
    string Status,
    long EventId,
    long ChannelId,
    long ConversationId,
    long EntryId,
    string RequestId,
    string MemberIdentity);

public sealed record DirectConversationReadCursorDto(
    long Id,
    long ConversationId,
    string ReaderIdentity,
    long? LastReadEntryId,
    string LastReadAt,
    string CreatedAt,
    string UpdatedAt);

public sealed record UpsertDirectReadCursorRequest(
    string ReaderIdentity,
    long? LastReadEntryId);

public sealed record LinkDirectMessageRequest(
    long ChannelMessageId,
    string Direction,
    string SenderIdentity,
    string RecipientIdentity,
    string? BodyPreview = null);

public sealed record DirectConversationErrorDto(string Code, string Detail);
