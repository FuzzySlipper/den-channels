# Channel event SSE stream (#2146)

Den Channels exposes a channel-scoped Server-Sent Events stream for Den Web live updates:

```http
GET /api/channels/{channelId}/events/stream
Accept: text/event-stream
```

This endpoint is a Channels-owned backend contract. It streams persisted channel data only; it does **not** create chat messages, wake agents, claim direct-agent delivery, advance read cursors, or make transport/runtime-only delivery state authoritative inside Channels.

## Event sources

The stream currently emits two event types:

| SSE event | Source table/API | Payload field | Meaning |
|---|---|---|---|
| `channel_message` | `/api/channels/{channelId}/messages` / `channel_messages` | `message` | New channel message rows, including wake/direct-agent breadcrumbs already represented as channel messages. |
| `channel_activity_event` | `/api/channels/{channelId}/activity-events` / `channel_activity_events` | `activityEvent` | Non-waking activity/lifecycle breadcrumbs and operator-visible progress evidence. |

Wake/direct-agent delivery progress appears in this stream when producers already persist it as channel messages or channel activity/lifecycle events. Runtime-only transport progress that is not yet persisted in Channels is intentionally not synthesized here; route any additional source through the current runtime/host boundary as pass-through data rather than making Channels invent tracking state.

## Stream-open contract event

Each connection starts with a `stream_open` event. It has no cursor `id` and documents the usable contract for the client:

```text
event: stream_open
data: {
  "type": "stream_open",
  "channelId": 123,
  "cursor": {
    "messageId": 0,
    "activityId": 0,
    "sseId": "messages=0;activity=0"
  },
  "supportedEventTypes": ["channel_message", "channel_activity_event"],
  "fallbackPollEndpoints": [
    "/api/channels/123/messages?afterId=0",
    "/api/channels/123/activity-events?afterId=0"
  ],
  "reconnect": {
    "header": "Last-Event-ID",
    "query": "lastEventId",
    "explicitQuery": ["afterMessageId", "afterActivityId"]
  },
  "heartbeatSeconds": 15,
  "notes": "Delivery/wake progress is present only when already persisted as channel messages or channel activity events. Runtime-only transport progress is not synthesized by this Channels stream."
}
```

Den Web should keep its polling-compatible `useLiveData` fallback. If EventSource fails or the stream closes unexpectedly, poll the listed fallback endpoints using the last cursor values.

## Data event envelope

Message events use:

```text
id: messages=42;activity=7
event: channel_message
data: {
  "type": "channel_message",
  "channelId": 123,
  "sourceId": 42,
  "cursor": {
    "messageId": 42,
    "activityId": 7,
    "sseId": "messages=42;activity=7"
  },
  "createdAt": "2026-06-09 12:34:56",
  "message": { ... ChannelMessageDto ... }
}
```

Activity events use:

```text
id: messages=42;activity=8
event: channel_activity_event
data: {
  "type": "channel_activity_event",
  "channelId": 123,
  "sourceId": 8,
  "cursor": {
    "messageId": 42,
    "activityId": 8,
    "sseId": "messages=42;activity=8"
  },
  "createdAt": "2026-06-09 12:35:01",
  "activityEvent": { ... ChannelActivityEventDto ... }
}
```

The SSE `id` is intentionally opaque to clients except for storage/reconnect. It is currently a composite cursor:

```text
messages={last_message_id};activity={last_activity_event_id}
```

## Cursor and reconnect

Supported reconnect mechanisms:

1. Standard EventSource behavior: browser sends `Last-Event-ID` from the last received data event.
2. Query cursor: `?lastEventId=messages=42;activity=8`.
3. Explicit query cursor: `?afterMessageId=42&afterActivityId=8`.
4. Coarse compatibility cursor: `?afterId=42`, which applies the same value to both message and activity cursors.

Precedence is:

1. `lastEventId` / `since` / `Last-Event-ID` establish the initial composite cursor;
2. `afterId` overwrites both cursor fields;
3. `afterMessageId` and `afterActivityId` overwrite their individual fields.

Replay is bounded by `replayLimit` (default `100`, max `500`). Existing polling/list endpoints remain the fallback and should not be removed.

## Heartbeats and operator smoke

Idle connections receive SSE comments:

```text
: keepalive 2026-06-09T12:35:15.0000000+00:00
```

The default heartbeat is 15 seconds. For local/operator smoke or tests, use `once=true` to return one bounded replay window plus a heartbeat and then close:

```bash
curl -N 'http://127.0.0.1:18081/api/channels/604/events/stream?once=true&afterMessageId=0&afterActivityId=0'
```

Useful query parameters:

| Query | Default | Notes |
|---|---:|---|
| `replayLimit` | `100` | Clamped `1..500`; bounds initial replay and each poll batch. |
| `heartbeatSeconds` | `15` | Clamped `1..300`. |
| `pollIntervalMs` | `1000` | Clamped `100..15000`; server-side DB poll interval. |
| `once` | `false` | Test/smoke mode; emits one replay window and closes after a heartbeat. |
