# Worker-Pool Lobby Channel (#1771)

**Status:** implemented in den-channels

## Overview

The `#worker-pool` lobby is a system channel (`slug='worker-pool'`, `kind='system'`) that serves
as a visible home lane for spawned-coder orchestration. It tracks worker-pool member presence
and lifecycle status, showing which workers are available (idle), leased, draining, released,
quarantined, or offline.

### Service boundary rule

- **Channels** owns the lobby presence table (`worker_pool_lobby_presence`) and projection.
- **Core** remains the authoritative source for availability, lease, cleanup, release, and
  quarantine decisions. Channels lobby status is an *observability and wake-support* projection only.
- Assignment/checkpoint/transcript work stays in the target project channel, not in `#worker-pool`.
- Return-to-available (back to `idle`) requires an explicit Core release acknowledgment.
  Local cleanup alone must not mark a worker as available.

## Channel definition

| Field | Value |
|-------|-------|
| `slug` | `worker-pool` |
| `display_name` | `#worker-pool` |
| `kind` | `system` |
| `visibility` | `normal` |
| `settings_json` | `{"systemManaged":true,"channelRole":"worker_pool_lobby","description":"..."}` |

The channel is seeded at database migration v3 and on every startup via
`ChannelsDatabaseInitializer.EnsureWorkerPoolLobbySeedAsync`.

## Presence table

**Table:** `worker_pool_lobby_presence`

| Column | Type | Description |
|--------|------|-------------|
| `id` | INTEGER PK | Auto-increment |
| `channel_id` | INTEGER FK → channels | Lobby channel ID |
| `member_identity` | TEXT | Shared identity (e.g. `spawned-coder`). Multiple workers may share. |
| `agent_instance_id` | TEXT? | Concrete instance ID for same-profile distinction |
| `pool_member_id` | TEXT? | Pool member ID from Core |
| `concrete_identity` | TEXT (NOT NULL DEFAULT '') | Deterministic uniqueness key: pool_member_id when present, else agent_instance_id, else '' |
| `profile` | TEXT? | Hermes profile name (e.g. `spawned-coder`, `spawned-reviewer`) |
| `role` | TEXT? | Worker role (e.g. `coder`, `reviewer`, `validator`) |
| `status` | TEXT | One of: `idle`, `leased`, `draining`, `released`, `quarantined`, `offline` |
| `current_assignment_id` | TEXT? | Current Core assignment ID when leased |
| `current_task_id` | TEXT? | Current Den task ID |
| `current_project_id` | TEXT? | Current Den project ID |
| `last_activity_at` | TEXT? | ISO 8601 timestamp of last activity |
| `release_acknowledged` | INTEGER (0/1) | Core release acknowledgment gate |
| `created_at` | TEXT | Row creation timestamp |
| `updated_at` | TEXT | Row last-update timestamp |

**Unique constraint:** `(channel_id, member_identity, concrete_identity)` — multiple workers may share the same `member_identity` (e.g. `spawned-coder`) as long as they have distinct `concrete_identity` values. `concrete_identity` is computed as: `pool_member_id` when present, else `agent_instance_id` when present, else `''` (empty string fallback for non-pool callers, preserving backward compatibility with the old `UNIQUE(channel_id, member_identity)` contract).

## Status lifecycle

```
idle ──→ leased ──→ draining ──→ released ──→ (ack) ──→ idle
  │                                                    ↑
  └──→ quarantined ──→ (Core intervention) ────────────┘
  │
  └──→ offline
```

### Release acknowledgment gate

A worker in `released` status cannot transition back to `idle` (available) until
`release_acknowledged = 1`. The acknowledgment is set by calling:

```
POST /api/worker-pool/lobby/presence/{memberIdentity}/acknowledge-release
```

This enforces the rule: *"Return as available MUST require Core release acknowledgement;
local cleanup alone must not mark available."*

When a presence record is first set to `released`, `release_acknowledged` is automatically
reset to `0`. The Core is expected to call the acknowledgment endpoint after verifying
the release is complete.

## API endpoints

### PUT /api/worker-pool/lobby

Ensure the `#worker-pool` channel exists (idempotent).

**Response:** `200 OK` with `ChannelDto`

### PUT /api/worker-pool/lobby/presence

Create or update a worker's lobby presence record.

**Request body:** `UpsertWorkerPoolLobbyPresenceRequest`
```json
{
  "memberIdentity": "spawned-coder",
  "agentInstanceId": "inst-abc123",
  "poolMemberId": "pool-42",
  "profile": "spawned-coder",
  "role": "coder",
  "status": "leased",
  "currentAssignmentId": "assign-001",
  "currentTaskId": "1771",
  "currentProjectId": "den-channels",
  "lastActivityAt": "2026-05-29T10:00:00Z"
}
```

**Response:** `200 OK` with `WorkerPoolLobbyPresenceDto`

### POST /api/worker-pool/lobby/presence/{memberIdentity}/acknowledge-release

Acknowledge Core release for a worker in `released` status, enabling return to `idle`.

**Optional query params:** `agentInstanceId` and `poolMemberId` identify the concrete worker when
multiple workers share the same `memberIdentity`. Without them, matches the worker whose
`concrete_identity` is `''` (computed from NULL pool_member_id and agent_instance_id).

**Response:** `200 OK` with `WorkerPoolLobbyPresenceDto`, or `404 Not Found` if no
released presence matches the given identity and concrete params.

### GET /api/worker-pool/lobby/presence

List all workers in the lobby with their presence, grouped by role/profile.

**Response:** `200 OK` with `WorkerPoolLobbyOverviewResponse`
```json
{
  "lobbySlug": "worker-pool",
  "lobbyDisplayName": "#worker-pool",
  "lobbyChannelId": 1,
  "totalMembers": 4,
  "availableCount": 3,
  "byRole": [
    {
      "role": "coder",
      "profile": "spawned-coder",
      "count": 2,
      "members": [ ... ]
    },
    {
      "role": "reviewer",
      "profile": "spawned-reviewer",
      "count": 1,
      "members": [ ... ]
    }
  ],
  "members": [ ... ]
}
```

## Concrete instance/member IDs and uniqueness

Each presence record carries `agent_instance_id` and `pool_member_id` to distinguish
multiple concrete agent instances sharing the same Hermes profile (e.g., two `spawned-coder`
workers with different instance IDs). The `concrete_identity` column provides deterministic
uniqueness: it is computed as `pool_member_id` when present, else `agent_instance_id` when
present, else `''`. The `UNIQUE(channel_id, member_identity, concrete_identity)` constraint
allows multiple workers with the same `member_identity` (and same `profile`/`role`) to coexist
in the lobby, each occupying a distinct row.

**Important:** Do NOT create duplicate Hermes profiles to distinguish workers. The
`member_identity` (and `profile`/`role`) may be identical across workers. Concrete identity
(`pool_member_id` or `agent_instance_id`) is the discriminator.

The acknowledge-release endpoint accepts optional `agentInstanceId` and `poolMemberId` query
params to target a specific concrete worker when multiple exist with the same `memberIdentity`.
Without these params, it matches workers whose `concrete_identity` is `''` (both concrete
IDs were NULL), preserving backward compatibility with non-pool callers.

## Surfaces and assignment traces

The lobby overview `GET /api/worker-pool/lobby/presence` returns `byRole` groups that
allow UIs to:
- Open assignment traces for workers (using `currentAssignmentId`)
- Group available workers by role/profile for task assignment

The assignment trace surfaces at `/api/assignments/{assignmentId}/transcript` (channel-level)
and `/api/gateway/assignments/{assignmentId}/trace` (gateway-level) remain unchanged.
Lobby activity carries trace context through the presence record's `currentAssignmentId`,
`currentTaskId`, and `currentProjectId`.

## Notes for Core integration (#1767, #1739)

- Core must call `PUT /api/worker-pool/lobby/presence` to register workers and update their
  lifecycle status (idle → leased → draining → released).
- Core must call `POST /api/worker-pool/lobby/presence/{memberIdentity}/acknowledge-release`
  after verifying cleanup is complete to permit return-to-available. When multiple workers share
  the same `memberIdentity`, pass `?agentInstanceId=...&poolMemberId=...` to identify the
  concrete worker.
- The lobby presence is a Channels-side observability projection; Core remains authoritative
  for all lease, cleanup, quarantine, and release decisions.
- Channel visibility in `#worker-pool` is a *wake-support indicator* — a worker showing `idle`
  in the lobby is available for assignment by the orchestrator.
