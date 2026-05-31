# Channel-located pool children

## Problem

Shared-profile worker pools (e.g., `spawned-coder` with capacity=4) need each active
child run to be visible in Den Channels. Currently, pool members appear as a single
agent identity without per-run disambiguation. Operators and agents cannot see which
child run is active, stale, or crashed.

## Supervisor-routed virtual identity model

Until Bridge/Channels work enables per-child channel membership (tracked separately),
the Channels layer uses a **supervisor-routed virtual identity** model:

- **Channel identity** = supervisor profile identity (e.g., `pool-coder-01`).
  The supervisor profile is the single member in relevant Channels.

- **Per-run attribution** = each channel message, activity event, and checkpoint
  post carries per-run metadata:
  - `agentInstanceId`: Gateway adapter instance for this child run
  - `poolMemberId`: Worker-pool member identity
  - `assignmentId`: Core assignment ID
  - `runId`: Core worker run ID
  - `profileIdentity`: Shared profile identity (`spawned-coder`)

- **Direct-agent routing** delivers to the supervisor profile. The Bridge
  supervisor reads child metadata from the delivery and dispatches to the
  correct child Hermes session.

## Agents Overview child-run visibility

The Agents Overview (`GET /api/agents/overview` and `GET /api/agents/{id}/overview`)
now shows per-child-run state for shared-profile workers:

```json
{
  "agentIdentity": "pool-coder-01",
  "childRunCount": 3,
  "childRuns": [
    {
      "agentInstanceId": "hermes:den-k8:spawned-coder:piw_aaa",
      "runId": "piw_aaa",
      "assignmentId": "98765",
      "poolMemberId": "pool-coder-01",
      "profileIdentity": "spawned-coder",
      "status": "busy",
      "flags": []
    },
    {
      "agentInstanceId": "hermes:den-k8:spawned-coder:piw_bbb",
      "runId": null,
      "status": "available",
      "flags": []
    }
  ]
}
```

Child-run status values:
- **available**: idle, no active assignment
- **busy**: active assignment (leased)
- **quarantined**: worker quarantined
- **stale**: offline/no heartbeat

Supervisor delivery target: The `supervisorDeliveryTarget` field on each child run
identifies the supervisor profile (`pool-coder-01`) that receives deliveries for
dispatch to this child. Direct child routing via Channels membership is deferred.

## Child-run routing endpoints

### GET /api/agents/{agentIdentity}/child-runs

Returns active child-run identities with routing handles for supervisor dispatch.
Excludes released child runs.

### GET /api/worker-pool/lobby/presence/by-instance?agentInstanceId={id}

Lists lobby presence records filtered by `agentInstanceId`. Returns per-run
identities with routing metadata.

### POST /api/worker-pool/lobby/presence/release-child-run

Releases a child-run lobby presence. Channels-only — does NOT claim to release
Core capacity or Gateway delivery.

Request: `memberIdentity` (required), `agentInstanceId`, `poolMemberId`.
Transitions the presence status to `released`.

## Artifact attribution contract

Channel messages posted by child workers must carry per-run identity metadata
so readers can distinguish which child run produced each artifact:

| Message kind | Attribution fields |
|-------------|-------------------|
| checkpoint | agentInstanceId, poolMemberId, assignmentId, runId, checkpointType |
| review_finding | findingKey, reviewRoundId, taskId, assignmentId |
| implementation_packet | assignmentId, runId, workerIdentity |
| completion | assignmentId, runId, status |
| status_update | workerRunId, workerRole, agentInstanceId |

Thread routing: Child work artifacts are posted in the task thread. Observers
can filter by agentInstanceId to see one child's contributions.

## Cleanup semantics

When a child run completes or crashes:
1. Core transitions the assignment to terminal
2. Bridge/Gateway reconciles the child-run binding
3. Channels releases the lobby presence via `release-child-run`

Released presences are excluded from active child-run queries and overview
counts but preserved for audit trail.

## Noise management

- Child work artifacts use task-threaded channels (not project-level noise)
- Message kinds (`checkpoint`, `review_finding`, `implementation_packet`) let
  observers filter to specific artifact types
- Summary-level projections (childRunCount, per-run status) provide at-a-glance
  visibility without requiring message-level inspection

## Deferred work

The following require Bridge/Channels follow-up (tracked separately):

1. **Per-child channel membership** — each child run gets a distinct Den Channels
   identity for direct-agent message routing
2. **Direct child wake** — Gateway delivers directly to child session via
   adapter_instance_id target
3. **Auto-registration** — Bridge registers child Channel membership at process start
