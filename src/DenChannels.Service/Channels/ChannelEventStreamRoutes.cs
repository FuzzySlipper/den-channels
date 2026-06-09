using System.Globalization;
using System.Text.Json;

namespace DenChannels.Service.Channels;

/// <summary>
/// Channels-owned server-sent event stream for Den Web live channel updates.
///
/// Boundary rules:
/// - This streams Channels-owned message/activity rows only.
/// - It does not create messages, wake agents, claim delivery, or advance read cursors.
/// - Delivery/wake progress appears here only when already persisted as channel messages
///   or non-waking channel activity/lifecycle events.
/// </summary>
public static class ChannelEventStreamRoutes
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapChannelEventStreamRoutes(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/channels/{channelId:long}/events/stream", HandleEventStreamAsync);

        return api;
    }

    private static async Task HandleEventStreamAsync(
        long channelId,
        HttpContext httpContext,
        ChannelsRepository repository,
        bool? once,
        string? lastEventId,
        string? since,
        long? afterId,
        long? afterMessageId,
        long? afterActivityId,
        int? replayLimit,
        int? heartbeatSeconds,
        int? pollIntervalMs,
        CancellationToken cancellationToken)
    {
        var channel = await repository.GetChannelAsync(channelId, cancellationToken);
        if (channel is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                code = "channel_not_found",
                message = $"Channel {channelId} not found."
            }, cancellationToken);
            return;
        }

        var cursor = ResolveCursor(
            lastEventId,
            since,
            httpContext.Request.Headers["Last-Event-ID"].FirstOrDefault(),
            afterId,
            afterMessageId,
            afterActivityId);
        var options = new ChannelEventStreamOptions(
            ReplayLimit: Math.Clamp(replayLimit ?? 100, 1, 500),
            HeartbeatInterval: TimeSpan.FromSeconds(Math.Clamp(heartbeatSeconds ?? 15, 1, 300)),
            PollInterval: TimeSpan.FromMilliseconds(Math.Clamp(pollIntervalMs ?? 1000, 100, 15000)),
            CloseAfterInitialReplay: once == true);

        var connectionStartHighWatermark = await LoadHighWatermarkCursorAsync(repository, channelId, cancellationToken);

        PrepareSseResponse(httpContext.Response);

        await WriteSseEventAsync(
            httpContext.Response,
            eventName: "stream_open",
            id: null,
            data: new
            {
                type = "stream_open",
                channelId,
                cursor = ToCursorDto(cursor),
                supportedEventTypes = new[] { "channel_message", "channel_activity_event" },
                fallbackPollEndpoints = new[]
                {
                    $"/api/channels/{channelId}/messages?afterId={cursor.MessageId}",
                    $"/api/channels/{channelId}/activity-events?afterId={cursor.ActivityId}",
                },
                reconnect = new
                {
                    header = "Last-Event-ID",
                    query = "lastEventId",
                    explicitQuery = new[] { "afterMessageId", "afterActivityId" },
                },
                heartbeatSeconds = (int)options.HeartbeatInterval.TotalSeconds,
                notes = "Delivery/wake progress is present only when already persisted as channel messages or channel activity events. Missing Gateway-owned progress requires a den-gateway follow-up."
            },
            cancellationToken);

        var nextHeartbeatAt = DateTimeOffset.UtcNow.Add(options.HeartbeatInterval);
        var initialReplay = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            var streamEvents = await LoadPendingEventsAsync(repository, channelId, cursor, options.ReplayLimit, cancellationToken);
            foreach (var streamEvent in streamEvents)
            {
                cursor = cursor.Advance(streamEvent);
                await WriteSseEventAsync(
                    httpContext.Response,
                    streamEvent.EventName,
                    cursor.ToSseId(),
                    streamEvent.ToEnvelope(channelId, cursor),
                    cancellationToken);
            }

            if (options.CloseAfterInitialReplay)
            {
                await WriteHeartbeatAsync(httpContext.Response, cancellationToken);
                return;
            }

            if (initialReplay)
            {
                cursor = cursor.CatchUpTo(connectionStartHighWatermark);
                initialReplay = false;
            }

            if (DateTimeOffset.UtcNow >= nextHeartbeatAt)
            {
                await WriteHeartbeatAsync(httpContext.Response, cancellationToken);
                nextHeartbeatAt = DateTimeOffset.UtcNow.Add(options.HeartbeatInterval);
            }

            try
            {
                await Task.Delay(options.PollInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static async Task<ChannelEventCursor> LoadHighWatermarkCursorAsync(
        ChannelsRepository repository,
        long channelId,
        CancellationToken cancellationToken)
    {
        var latestMessages = await repository.ListMessagesAsync(
            channelId,
            afterId: null,
            assignmentId: null,
            limit: 1,
            cancellationToken);
        var latestActivityEvents = await repository.ListActivityEventsAsync(
            channelId,
            afterId: null,
            limit: 1,
            cancellationToken: cancellationToken);

        return new ChannelEventCursor(
            latestMessages.LastOrDefault()?.Id ?? 0,
            latestActivityEvents.LastOrDefault()?.Id ?? 0);
    }

    private static async Task<IReadOnlyList<PendingChannelStreamEvent>> LoadPendingEventsAsync(
        ChannelsRepository repository,
        long channelId,
        ChannelEventCursor cursor,
        int replayLimit,
        CancellationToken cancellationToken)
    {
        var messages = await repository.ListMessagesAsync(
            channelId,
            afterId: cursor.MessageId,
            assignmentId: null,
            limit: replayLimit,
            cancellationToken);
        var activityEvents = await repository.ListActivityEventsAfterIdAsync(
            channelId,
            afterId: cursor.ActivityId,
            limit: replayLimit,
            cancellationToken: cancellationToken);

        return messages
            .Select(PendingChannelStreamEvent.FromMessage)
            .Concat(activityEvents.Select(PendingChannelStreamEvent.FromActivityEvent))
            .OrderBy(e => e.CreatedAt, StringComparer.Ordinal)
            .ThenBy(e => e.SourceRank)
            .ThenBy(e => e.SourceId)
            .Take(replayLimit)
            .ToList();
    }

    private static ChannelEventCursor ResolveCursor(
        string? queryLastEventId,
        string? since,
        string? headerLastEventId,
        long? afterId,
        long? afterMessageId,
        long? afterActivityId)
    {
        var cursor = ChannelEventCursor.Zero;
        var opaqueCursor = FirstNonBlank(queryLastEventId, since, headerLastEventId);
        if (!string.IsNullOrWhiteSpace(opaqueCursor))
            cursor = ChannelEventCursor.Parse(opaqueCursor!);

        if (afterId.HasValue)
            cursor = new ChannelEventCursor(Math.Max(0, afterId.Value), Math.Max(0, afterId.Value));
        if (afterMessageId.HasValue)
            cursor = cursor with { MessageId = Math.Max(0, afterMessageId.Value) };
        if (afterActivityId.HasValue)
            cursor = cursor with { ActivityId = Math.Max(0, afterActivityId.Value) };
        return cursor;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static void PrepareSseResponse(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers.Append("X-Accel-Buffering", "no");
    }

    private static async Task WriteSseEventAsync(
        HttpResponse response,
        string eventName,
        string? id,
        object data,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(id))
            await response.WriteAsync($"id: {id}\n", cancellationToken);
        await response.WriteAsync($"event: {eventName}\n", cancellationToken);

        var json = JsonSerializer.Serialize(data, JsonOptions);
        foreach (var line in json.Split('\n'))
            await response.WriteAsync($"data: {line}\n", cancellationToken);

        await response.WriteAsync("\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static async Task WriteHeartbeatAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        await response.WriteAsync($": keepalive {DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static object ToCursorDto(ChannelEventCursor cursor) => new
    {
        messageId = cursor.MessageId,
        activityId = cursor.ActivityId,
        sseId = cursor.ToSseId(),
    };

    private sealed record ChannelEventStreamOptions(
        int ReplayLimit,
        TimeSpan HeartbeatInterval,
        TimeSpan PollInterval,
        bool CloseAfterInitialReplay);

    private readonly record struct ChannelEventCursor(long MessageId, long ActivityId)
    {
        public static ChannelEventCursor Zero => new(0, 0);

        public static ChannelEventCursor Parse(string value)
        {
            var cursor = Zero;
            foreach (var part in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
                if (pair.Length != 2 || !long.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    continue;

                parsed = Math.Max(0, parsed);
                cursor = pair[0] switch
                {
                    "messages" or "message" or "messageId" => cursor with { MessageId = parsed },
                    "activity" or "activityEvents" or "activityId" => cursor with { ActivityId = parsed },
                    _ => cursor,
                };
            }
            return cursor;
        }

        public string ToSseId() => $"messages={MessageId};activity={ActivityId}";

        public ChannelEventCursor CatchUpTo(ChannelEventCursor highWatermark) => new(
            Math.Max(MessageId, highWatermark.MessageId),
            Math.Max(ActivityId, highWatermark.ActivityId));

        public ChannelEventCursor Advance(PendingChannelStreamEvent streamEvent) => streamEvent.Type switch
        {
            "channel_message" => this with { MessageId = Math.Max(MessageId, streamEvent.SourceId) },
            "channel_activity_event" => this with { ActivityId = Math.Max(ActivityId, streamEvent.SourceId) },
            _ => this,
        };
    }

    private sealed record PendingChannelStreamEvent(
        string EventName,
        string Type,
        int SourceRank,
        long SourceId,
        string CreatedAt,
        ChannelMessageDto? Message,
        ChannelActivityEventDto? ActivityEvent)
    {
        public static PendingChannelStreamEvent FromMessage(ChannelMessageDto message) => new(
            EventName: "channel_message",
            Type: "channel_message",
            SourceRank: 0,
            SourceId: message.Id,
            CreatedAt: message.CreatedAt,
            Message: message,
            ActivityEvent: null);

        public static PendingChannelStreamEvent FromActivityEvent(ChannelActivityEventDto activityEvent) => new(
            EventName: "channel_activity_event",
            Type: "channel_activity_event",
            SourceRank: 1,
            SourceId: activityEvent.Id,
            CreatedAt: activityEvent.CreatedAt,
            Message: null,
            ActivityEvent: activityEvent);

        public object ToEnvelope(long channelId, ChannelEventCursor cursor)
        {
            var baseEnvelope = new
            {
                type = Type,
                channelId,
                sourceId = SourceId,
                cursor = ToCursorDto(cursor),
                createdAt = CreatedAt,
            };

            return Message is not null
                ? new
                {
                    baseEnvelope.type,
                    baseEnvelope.channelId,
                    baseEnvelope.sourceId,
                    baseEnvelope.cursor,
                    baseEnvelope.createdAt,
                    message = Message,
                }
                : new
                {
                    baseEnvelope.type,
                    baseEnvelope.channelId,
                    baseEnvelope.sourceId,
                    baseEnvelope.cursor,
                    baseEnvelope.createdAt,
                    activityEvent = ActivityEvent,
                };
        }
    }
}
