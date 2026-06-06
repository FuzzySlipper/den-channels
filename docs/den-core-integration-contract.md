# Den Channels ↔ Den Core Integration Contract

Status: draft for standalone service milestone

**Note (task #1848):** The Direct Delivery / Channels Operations Contract v0 is now frozen at `docs/direct-delivery-contract-v0.md` with machine-readable schema at `docs/schemas/direct-delivery-contract-v0.json`. Vocabulary is pinned in `DirectDeliveryContractV0.cs`. Gateway compatibility is transitional; Core remains workflow truth.

Related work:

- den-channels #1323 — define this contract
- den-mcp #1341 — implement/support the Den core side of this contract
- den-channels #1324 — project default channel sync against this contract
- den-channels #1325 — mirror summary ingestion from Den events
- Den docs: `den-channels-standalone-service-direction`, `den-channels-architecture-adr`, `agent-stream-to-den-channels-gateway-mapping`

## Boundary rule

`den-channels` is a standalone service with its own database. It must not read or write the `den-mcp` SQLite database directly and must not depend on `den-mcp` internal C# types.

All coupling to Den core happens through explicit HTTP/event contracts. MCP tools can wrap or administer these surfaces later, but the service-to-service integration should be HTTP/event based.

## Communication-surface naming

This contract uses the task #1555 vocabulary from Core's `docs/communication-api-surface-naming.md` and Den document `den-core/den-communication-surfaces-concept-map`:

- `channel_message` means a visible Den Channels transcript row written through `POST /api/channels/{channelId}/messages` or the Gateway compatibility seam.
- `direct_agent_message` / `direct_agent_event` means the wakeable Channels-owned request written through `POST /api/direct-agent-events`; its backing transcript row has `sourceKind=wake_event`. The legacy alias `POST /api/gateway/direct-agent-messages` was retired in task #2022 and now returns 410 Gone.
- `gateway_delivery_final_message` means the true terminal visible reply for a Gateway delivery; it is a `channel_message` with `sourceKind=gateway_delivery`, a `deliveryRequestId`, and a final dedupe key shaped `gateway-delivery:{delivery_request_id}:final`.
- `channel_activity_event` / `delivery_activity_event` means non-waking progress/activity written primarily through `POST /api/channels/{channelId}/activity-events` or `POST /api/channel-activity-events`. The legacy alias `POST /api/gateway/channel-activity-events` was retired in task #2022 and now returns 410 Gone.
- Core `project_message` / `task_message`, `user_notification`, `agent_stream_entry`, and worker/review packets remain Core-owned records; Channels may mirror or link them, but does not become their source of truth.

New docs should avoid unqualified "message" when the surface is not already obvious from the route/DTO namespace.

## Current `den-channels` side

The standalone service now owns:

- channel storage and migration startup;
- `channels` / `channel_messages` / `channel_memberships` / `channel_reactions` / `channel_read_cursors` tables;
- source-pointer fields on channel messages: `source_kind`, `source_id`, `source_project_id`, `deep_link`, `metadata_json`, `dedupe_key`;
- basic HTTP API:
  - `GET /api/channels`
  - `POST /api/channels`
  - `GET /api/channels/{channelId}`
  - `PUT /api/projects/{projectId}/default-channel`
  - `POST /api/channels/{channelId}/messages`
  - `GET /api/channels/{channelId}/messages?afterId=&limit=`
  - `PUT /api/channels/{channelId}/memberships`
  - `POST /api/channel-messages/{messageId}/reactions`

`DenChannels:DenCore:UseStubProjectMetadata=true` remains supported while the Den core side is incomplete.

## Required Den core surfaces

### 1. Project metadata

Purpose: project default channel sync and display metadata.

Minimum existing surface in `den-mcp`:

- `GET /api/projects/` lists normal project-kind projects.
- `GET /api/projects/{id}?agent=` returns project stats and metadata.

Required stable contract for `den-channels`:

```http
GET /api/projects
GET /api/projects/{projectId}
```

Response fields needed by `den-channels`:

```json
{
  "id": "den-channels",
  "name": "Den Channels",
  "kind": "project",
  "visibility": "normal",
  "description": "...",
  "updated_at": "2026-05-11T00:00:00Z"
}
```

Rules:

- Project ids are opaque strings.
- `den-channels` derives default channel slugs as `project-{project_id}` and stores the Den project id separately.
- Archived/hidden filtering should be explicit if `den-channels` ever needs those projects.

### 2. Source metadata and summary lookup

Purpose: create mirror summaries and clickthroughs without copying canonical payloads into channel scrollback.

Existing partial surfaces:

- Tasks: `GET /api/projects/{projectId}/tasks/{taskId}`.
- Messages: `GET /api/projects/{projectId}/messages/{messageId}` and `GET /api/projects/{projectId}/messages/thread/{threadId}`.
- Agent stream: `GET /api/agent-stream/{entryId}` and filtered list endpoints.
- Review/worker routes exist for detailed workflow APIs, but no single normalized source-summary API exists.

Required new/normalized contract:

```http
GET /api/source-summaries/{sourceKind}/{sourceId}?projectId={projectId}
```

Recommended response:

```json
{
  "source_kind": "task_message",
  "source_id": "5680",
  "source_project_id": "den-channels",
  "title": "Task #1320 completion packet",
  "summary": "Runner completed the standalone service skeleton; tests passed.",
  "deep_link": "den://project/den-channels/message/5680",
  "occurred_at": "2026-05-11T09:30:00Z",
  "actor": "den-channels-runner",
  "severity": "normal",
  "metadata": {
    "task_id": 1320
  }
}
```

Supported `source_kind` values should initially match the channel schema:

- `task_message`
- `agent_stream_entry`
- `notification`
- `worker_run`
- `review_round`
- `review_finding`
- `wake_event`
- `gateway_delivery` for first-party Den Gateway / Hermes Gateway delivery replies
- `external_adapter_message` only for true external adapter ingress or temporary gateway cutover compatibility

Rules:

- The response is a compact display summary, not the canonical record.
- `metadata` must be bounded and safe for channel display/storage.
- Missing/unauthorized sources should return 404/403, not an empty fake summary.

### 3. Deep-link contract/helper

Purpose: keep source links stable across Desktop and service boundaries.

Candidate canonical links:

- `den://project/{project_id}`
- `den://project/{project_id}/task/{task_id}`
- `den://project/{project_id}/message/{message_id}`
- `den://project/{project_id}/thread/{thread_id}`
- `den://worker-run/{run_id}`
- `den://review/{review_round_id}`
- `den://review-finding/{finding_id}`
- `den://channel/{channel_id}` for Den Channels local links

Required Den core support:

- Either document and version this vocabulary in Den core docs, or expose a small helper endpoint that returns a link for a source pointer.
- The source-summary endpoint may satisfy this if every summary includes `deep_link`.

### 4. Significant event outbox/cursor

Purpose: allow `den-channels` to mirror significant Den events into project channels without polling many tables.

Existing partial surfaces:

- `agent_stream_entries` can be filtered and has a realtime stream internally, but it is explicitly an audit/ops feed, not the channel delivery queue.
- Task/message/review/worker changes are accessible through specific APIs but not through one durable mirror-summary outbox.

Required new contract:

```http
GET /api/events/outbox?after={cursor}&limit=100&projectId={optionalProjectId}
```

Recommended response:

```json
{
  "items": [
    {
      "cursor": "000000000123",
      "event_id": "den-event-123",
      "event_type": "task_status_changed",
      "project_id": "den-channels",
      "source_kind": "task",
      "source_id": "1320",
      "occurred_at": "2026-05-11T09:30:00Z",
      "actor": "den-channels-runner",
      "summary_hint": "Task #1320 completed",
      "deep_link": "den://project/den-channels/task/1320",
      "severity": "normal",
      "dedupe_key": "task:1320:status:done"
    }
  ],
  "next_cursor": "000000000124"
}
```

V1 significant event types for channel mirroring:

- task created/blocked/done/review, especially high priority or assigned work;
- review requested / changes requested / approved;
- worker run completed/failed/blocked;
- user-facing notifications;
- agent needs input;
- Den outage pause/resume events.

Rules:

- Outbox cursor must be monotonic and durable.
- `dedupe_key` should be stable so `den-channels.channel_messages.dedupe_key` can keep mirrors idempotent.
- Debug-level subagent events and routine heartbeat churn should not appear unless explicitly requested.

### 5. Service-to-service auth

Purpose: allow `den-channels` to call Den core safely when both run on den-srv.

Current `den-channels` placeholder config:

```json
{
  "DenChannels": {
    "DenCore": {
      "BaseUrl": "http://127.0.0.1:5199",
      "UseStubProjectMetadata": true
    },
    "ServiceAuth": {
      "ServiceToken": null
    }
  }
}
```

Required Den core decision:

- Define how service tokens are issued, configured, rotated, and authorized.
- V1 can be a static bearer token in service config if scoped to loopback/den-srv, but the contract should not bake in unauthenticated cross-service calls.

Recommended header:

```http
Authorization: Bearer <service-token>
```

### 6. Error handling and compatibility

`den-channels` should treat Den core as eventually unavailable and degrade gracefully:

- project sync can stay on stub/project-cache mode if Den core is unavailable;
- mirror ingestion should retry from the last stored cursor;
- channel post/list APIs must keep working for channel-owned data even if Den core is down;
- failures to resolve a source pointer should keep the channel message with a source pointer and a degraded/missing summary, not copy unknown core payloads.

## Missing support tracked in den-mcp

Created den-mcp task #1341: **Support den-channels standalone service integration contract**.

That task covers:

- project metadata contract hardening;
- normalized source summary/deep-link endpoint;
- event/outbox cursor for significant Den events;
- service-to-service auth decision/support.

If implementation needs to split that work, suggested child tasks are:

1. `Expose normalized source summary/deep-link endpoint for Den Channels`.
2. `Publish durable significant-event outbox cursor for Den Channels mirror ingestion`.
3. `Define and enforce service-token auth for Den service-to-service calls`.

## Stub strategy until #1341 lands

`den-channels` can proceed without Den core internals by using:

- `DenChannels:DenCore:UseStubProjectMetadata=true`;
- explicit project ids passed to `PUT /api/projects/{projectId}/default-channel`;
- channel message source pointers supplied by API callers/tests;
- no mirror-ingestion daemon until event outbox exists.

This satisfies the first milestone while preserving the repo/service boundary.
