# Agent activity events (#1526)

Den Channels persists agent/tool-call breadcrumbs as **activity events**, not as conversation messages.

Planning anchor: Den task #1525 and document `agent-activity-event-model-planning-note`.
Cross-profile display-block extension: Den docs `cross-profile-activity-display-blocks` and
ADR `adr-activity-render-block-correlation`.

## Ownership

- `den-channels` owns persistence, request validation/defaulting, HTTP API contracts,
  and recent write-failure diagnostics for activity events.
- The primary write API is Channels-native. Gateway-shaped routes are compatibility
  aliases only while older Hermes/Gateway-era callers migrate.
- `den-hermes-bridge` summarizes Hermes tool calls into bounded activity events and
  should post them to Channels, not to `den-gateway`.
- `den-desktop` / Den Web render activity breadcrumbs from the Channels read APIs.

## Schema

Activity records live in `channel_activity_events` with these durable associations:

- `channel_id` and optional `project_id`
- `agent_identity`
- optional `delivery_request_id`
- optional `hermes_session_key`
- optional `display_block_id` — render-block grouping key for the visible parent operation block;
  v1 values are sourced from the parent orchestrator delivery id, but the field is deliberately
  a render-block id, not a Gateway delivery foreign key
- optional parent context: `parent_hermes_session_key`, `parent_agent_identity`
- optional spawned-worker context: `worker_run_id`, `worker_role`
- optional Den `task_id` / `thread_id`
- optional `anchor_message_id`
- `event_type`, `status`, `delivery_stage`, `terminal`, `sequence`, `update_version`
- bounded `title`, `summary`, `preview_json`, `metadata_json`
- optional per-channel `dedupe_key`
- optional `final_channel_message_id` linking terminal progress to the real visible final reply

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

Append/upsert an activity event through the canonical Channels route:

```http
POST /api/channels/{channelId}/activity-events
```

Gateway-shaped compatibility write surface (task #1944) for older callers that still
send `channelId` in the JSON body:

```http
POST /api/channel-activity-events
GET /api/channel-activity-events/status
```

The compatibility writer rejects missing `channelId` / `agentIdentity`, defaults blank
`eventType` to `lifecycle_status`, defaults blank `status` to `interim`, and returns
soft `degraded` results plus recent-failure diagnostics when persistence fails. It is
still non-waking observability; it does not create messages or delivery requests.

Gateway-prefixed compatibility aliases are available only for migration/readback and
should not be the target for new callers:

```http
POST /api/gateway/channel-activity-events?channelId=...
GET /api/gateway/channel-activity-events/status
```

Remove the Gateway-prefixed alias once Hermes/bridge callers no longer emit to the old
Gateway-shaped base path.

Use `terminal=false` and a non-final `deliveryStage` such as `assistant_interim`, `tool`, `status`, or `compression` for pre-tool text/status/progress. Reserve `terminal=true` for explicit terminal outcome breadcrumbs; the actual human-visible assistant answer remains a normal `channel_message` row with the `gateway-delivery:<delivery_id>:final` dedupe key.

Task #1555 naming alignment: docs and adapters may call this surface `channel_activity_event` or `delivery_activity_event`, but must not call it a channel message. Final visible delivery replies should be named `gateway_delivery_final_message` (or equivalent wording) when referring to the terminal transcript artifact.

Query channel activity events:

```http
GET /api/channels/{channelId}/activity-events?deliveryRequestId=...&hermesSessionKey=...&anchorMessageId=...&afterId=...&limit=...
```

The list route also supports server-side display-block filters:

```http
GET /api/channels/{channelId}/activity-events?displayBlockId=...&workerRunId=...
```

Update an activity event:

```http
PATCH /api/channel-activity-events/{activityEventId}
```

The append route is idempotent when `dedupeKey` is supplied: uniqueness is scoped to `(channel_id, dedupe_key)`, and a repeated append updates the existing activity row and increments `update_version`.

Repeated appends preserve known display/parent/worker correlation fields unless the retry supplies a replacement value.

## #1567 fake E2E coverage note

The storage/listing and render-model fake E2E tests use aligned fixture ids:

- parent final message `deliveryRequestId`: `parent-1567`
- coder worker `deliveryRequestId` / `workerRunId`: `coder-1567`
- reviewer worker `deliveryRequestId` / `workerRunId`: `reviewer-1567`

The invariant is that stored and accepted payload fields remain camelCase (`displayBlockId`,
`workerRunId`, `deliveryRequestId`) and render grouping only uses
`activity.displayBlockId == message.deliveryRequestId` for child activity under a visible parent
block. There is no `displayDeliveryRequestId` field or fallback dependency.

Focused validation commands:

```bash
dotnet test DenChannels.slnx --filter "FullyQualifiedName~ChannelApiTests&FullyQualifiedName~Activity"
dotnet test DenChannels.slnx --filter ChannelApiTests
```

## Message-to-delivery linkage

Channel messages now expose first-class `delivery_request_id` / `deliveryRequestId` for Gateway-delivered
messages. Activity renderers should match cross-profile child activity by exact key equality:

```text
activity.displayBlockId == message.deliveryRequestId
```

Older metadata/dedupe-key parsing remains a compatibility fallback for pre-migration rows only.

## Non-wake invariant

Activity events are observability, not conversation:

- They are not inserted into `channel_messages`.
- They must not feed Gateway wake-policy evaluation.
- They must not advance read cursors or create unread conversation turns.
- They must not create direct-agent messages or membership notifications.
- They must not terminalize delivery requests or consume final reply dedupe handles.

Downstream Gateway/Hermes/Desktop work should depend on this contract instead of inventing a message-model shortcut.
