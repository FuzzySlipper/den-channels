# Direct Agent DM Transcript API Contract

> **den-channels** backend slice for `den-web` integration  
> **Branch:** `task/2003-direct-agent-dm-transcripts` | **Last updated:** 2026-06-06 (R2 fix)

## Core invariant (read this first)

A direct-agent DM transcript is a **read model** over canonical channel/conversation message history. It is NOT delivery truth.

- Do **NOT** derive Hermes session keys, worker/session bindings, or delivery claims from `direct_conversation_id`.
- New sending goes through the Conversation and Delivery successors. Legacy `source_kind = wake_event` rows remain readable as transcript evidence.
- Agent responses are linked into transcripts through successor conversation evidence; the legacy `link-message` write route is archived in production under task #3029.

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

Retired in production as of task #3029 when `DenChannels:LegacyDisplayHistory:TombstoneArchivedRoutes=true`. New agent-response transcript evidence should come through the Conversation successor; legacy direct-conversation rows remain available for readback/export.

Historical behavior: this was the explicit trust boundary between DM transcript and agent response linking. It did not derive session identity from `direct_conversation_id` and did not validate metadata field names on the linked message.

### `PUT /api/direct-conversations/{id}/read-cursor` / `GET .../read-cursor`

Manage per-reader unread state for the DM sidebar.

## Trust boundary

The legacy `link-message` endpoint used to be the explicit trust boundary between DM transcript and agent response. In production it is now a retired write surface under the display/history tombstone switch; keep the old semantics only as historical context for preserved transcript rows and dev/offline tests. Successor Conversation evidence owns new transcript linkage.

## Schema

Migration v6: `direct_conversations`, `direct_conversation_entries`, `direct_conversation_read_cursors` — all `IF NOT EXISTS` idempotent.
