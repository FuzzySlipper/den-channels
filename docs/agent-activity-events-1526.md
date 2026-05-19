# Agent activity events (#1526)

Den Channels persists agent/tool-call breadcrumbs as **activity events**, not as conversation messages.

Planning anchor: Den task #1525 and document `agent-activity-event-model-planning-note`.

## Ownership

- `den-channels` owns persistence and HTTP API contracts for activity events.
- `den-gateway` will route non-waking activity writes and maintain delivery/run association.
- `den-hermes-bridge` will summarize Hermes tool calls into bounded activity events.
- `den-desktop` will render activity breadcrumbs from the read API.

## Schema

Activity records live in `channel_activity_events` with these durable associations:

- `channel_id` and optional `project_id`
- `agent_identity`
- optional `delivery_request_id`
- optional `hermes_session_key`
- optional Den `task_id` / `thread_id`
- optional `anchor_message_id`
- `event_type`, `status`, `sequence`, `update_version`
- bounded `title`, `summary`, `preview_json`, `metadata_json`
- optional per-channel `dedupe_key`

Supported initial event types:

- `tool_call_started`
- `tool_call_completed`
- `tool_call_failed`
- `lifecycle_status`
- `aggregation_snapshot`
- `run_summary`

Supported statuses:

- `started`
- `completed`
- `failed`
- `interim`

## API contract

Append/upsert an activity event:

```http
POST /api/channels/{channelId}/activity-events
```

Query channel activity events:

```http
GET /api/channels/{channelId}/activity-events?deliveryRequestId=...&hermesSessionKey=...&anchorMessageId=...&afterId=...&limit=...
```

Update an activity event:

```http
PATCH /api/channel-activity-events/{activityEventId}
```

The append route is idempotent when `dedupeKey` is supplied: uniqueness is scoped to `(channel_id, dedupe_key)`, and a repeated append updates the existing activity row and increments `update_version`.

## Non-wake invariant

Activity events are observability, not conversation:

- They are not inserted into `channel_messages`.
- They must not feed Gateway wake-policy evaluation.
- They must not advance read cursors or create unread conversation turns.
- They must not create direct-agent messages or membership notifications.
- They must not terminalize delivery requests or consume final reply dedupe handles.

Downstream Gateway/Hermes/Desktop work should depend on this contract instead of inventing a message-model shortcut.
