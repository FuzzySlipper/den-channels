# Direct Agent DM Transcript API Contract

> **den-channels** backend slice for `den-web` integration  
> **Branch:** `task/2003-direct-agent-dm-transcripts` | **Last updated:** 2026-06-06 (R2 fix)

## Core invariant (read this first)

A direct-agent DM transcript is a **read model** over canonical `channel_messages`. It is NOT delivery truth.

- Do **NOT** derive Hermes session keys, worker/session bindings, or delivery claims from `direct_conversation_id`.
- New sending goes through the Conversation and Delivery successors. Legacy `source_kind = wake_event` rows remain readable as transcript evidence.
- Agent responses are linked into transcripts **only** via the explicit `link-message` endpoint — broad identity-pair heuristic capture is **rejected**.

## Endpoints

### `GET /api/direct-conversations?humanIdentity={id}&limit={1-200}&afterId={cursor}`

Returns `DirectConversationListResponse` — sorted by `lastEntryAt DESC, id DESC`.  
Each conversation includes `unreadCount` computed against the reader's read cursor.

### `POST /api/direct-conversations`

Create or retrieve a conversation by `humanIdentity` + `agentIdentity` pair. Idempotent.

### `GET /api/direct-conversations/{id}`

Single conversation details.

### `GET /api/direct-conversations/{id}/entries?limit={1-200}&afterId={cursor}`

Paginated transcript entries. Each entry carries **source badges**:
- `sourceChannelId` — originating channel
- `sourceProjectId` — originating project
- `sourceTaskId` — associated task
- `sourceWorkerRunId` — worker run
- `sourceSessionOwnerId` — session owner

### `POST /api/direct-conversations/{id}/send`

Retired as of task #3025. Returns 410 Gone pointing to `POST /v1/delivery/intents`. New callers should write human-facing transcript evidence through the Conversation successor and create executable wake intents through Delivery.

### `POST /api/direct-conversations/{id}/link-message`

**Trust boundary** for agent response linking. Called by `den-hermes-bridge`/`den-host` after posting an agent response.

- Links an existing `channel_messages` row into the DM transcript
- Source badges (`sourceChannelId`, `sourceProjectId`, `sourceTaskId`, `sourceWorkerRunId`, `sourceSessionOwnerId`) are populated from the canonical message — not from the request body
- Direction: `agent_to_human`, `human_to_agent`, or `system_note`
- No session identity is derived from `direct_conversation_id`

### `PUT /api/direct-conversations/{id}/read-cursor` / `GET .../read-cursor`

Manage per-reader unread state for the DM sidebar.

## Trust boundary

The `link-message` endpoint is the **explicit trust boundary** between DM transcript and agent response. It does NOT validate that the linked message carries `directConversationId`/`inReplyToChannelMessageId` metadata — the caller (`den-hermes-bridge`/`den-host`) is responsible for providing a correct `channelMessageId`. This avoids coupling the Channels read-model to specific metadata field names and keeps the endpoint useful for manual linking, system notes, and other link-back scenarios.

## Schema

Migration v6: `direct_conversations`, `direct_conversation_entries`, `direct_conversation_read_cursors` — all `IF NOT EXISTS` idempotent.
