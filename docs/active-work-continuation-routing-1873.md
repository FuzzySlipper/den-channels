# Active-Work Continuation Routing (task #1873)

## Problem

Den Channels/Runner/worker-pool UX failure: work can be happening for a target
project/task/run while the source/control channel or runtime-control project
points somewhere else (e.g. den-hermes-bridge). Questions or continuation
requests wake a different same-profile Runner/session instead of the actor
currently doing the work. Tool/max-turn continuation also depends on remembering
the original source channel/session.

## Solution

Active-work continuation routing resolves the correct agent instance/session for
a given target project/task/assignment/run, regardless of which source channel
or control project the interaction happened in.

**Key principle**: Source channel/control room is context metadata, not session
owner. The concrete agent instance / assignment run owns the active session.
Target work is resolved by explicit target project/task/assignment/run fields,
never inferred from `channel.project_id`.

See also: `_global/agent-session-boundary-policy`.

## API

### POST /api/active-work/resolve

Resolve the active work continuation target for given target filters.

Request body (`ResolveActiveWorkRouteRequest`):
- `targetProjectId` — target project where work is happening
- `targetTaskId` — target task within the project
- `assignmentId` — specific assignment run ID
- `workerRunId` — specific worker run ID
- `profileIdentity` — agent profile identity filter
- `sourceChannelId` — source channel context (metadata only)
- `sourceProjectId` — source/control project context (metadata only)

Response (`ActiveWorkRouteResponse`):
- `routeStatus`: `"routed"` | `"no_active_route"` | `"stale"`
- `reason`: human-readable explanation
- `route`: resolved route (null when no_active_route)
- `evidence`: sources consulted during resolution

Always returns 200 with explicit route status — callers distinguish "no active
route found" from service errors without inspecting HTTP status codes.

### GET /api/active-work/routes

List active work routes matching filter criteria.

Query parameters: `targetProjectId`, `targetTaskId`, `assignmentId`,
`profileIdentity`, `includeStale`, `limit`.

Returns matching routes ordered by most recent activity.

## Route resolution logic

1. **Channel messages**: Query `channel_messages` with target-work fields
   (`target_project_id`, `target_task_id`, `assignment_id`, `worker_run_id`).
   Messages carry session-owner identity (`session_owner_id`, `session_id`)
   and concrete agent instance (`agent_instance_id`, `pool_member_id`).

2. **Activity events**: Query `channel_activity_events` for matching
   project/task/assignment/run. Activity events may have richer instance
   fields when messages don't.

3. **Worker-pool state**: Best-effort query to Core worker-pool API for
   assignment phase and member state. Graceful degradation when unavailable.

4. **Merge and select**: Build candidate routes from all sources, merge
   by instance/assignment identity, select most recently active candidate.

5. **Staleness**: Routes without recent activity (30 min threshold) are marked
   stale. Callers decide how to handle stale routes.

## Route fields

An `ActiveWorkRouteDto` contains:

**Target work identity**:
- `targetProjectId`, `targetTaskId`, `assignmentId`, `workerRunId`, `workerRole`

**Agent instance identity**:
- `agentInstanceId` — disambiguates when multiple instances share a profile
- `profileIdentity` — may be shared by multiple workers
- `poolMemberId` — worker-pool member ID
- `sessionOwnerId` — session that owns active work (independent of source channel)
- `sessionId` — active session ID

**Source context** (metadata only):
- `sourceChannelId` — channel where work is visible
- `sourceControlProjectId` — control project for the channel

**Activity state**:
- `lastActivityAt`, `assignmentPhase`, `isStale`

**Continuation actions**:
- `allowedActions`: `ask`, `continue`, `reset`, `view_transcript`
- Session-owning routes get all actions; routes without session owner get
  limited actions (ask, view_transcript only)

**Drill-down handles**:
- `handles.transcriptUrl`, `handles.traceUrl`, `handles.deliveryHandle`,
  `handles.agentDetailUrl`

## Evidence

Every resolution includes `ActiveWorkRouteEvidenceDto`:
- `sources`: list of sources consulted (channel_messages, activity_events,
  worker_pool) with availability and record counts
- `candidatesConsidered`: how many candidate routes were evaluated
- `resolvedAt`: timestamp of resolution

This provides full transparency for debugging routing decisions.

## Scenarios covered

### Runner crosses projects
Worker control channel is in `den-hermes-bridge`, but target work is for
`den-gateway`. Resolution by `targetProjectId=den-gateway` finds the active
worker via target-work fields on channel messages, not by channel project.

### Same profile, different instances
Two `spawned-coder` instances working on different tasks. Resolution by
`targetTaskId` returns the correct instance's `agentInstanceId` and
`sessionOwnerId`, not a random same-profile lane.

### Patch asks from target project channel
A human asks a question about task 1873 in the `den-channels` project channel.
Resolution by `targetProjectId=den-channels` and `targetTaskId=1873` returns
the active coder's session, enabling the question to reach the right worker.

### No active route
When no matching work exists, returns `routeStatus=no_active_route` with
evidence about which sources were consulted. No 404, no error — an explicit
result.

## Data sources and dependencies

- **channel_messages**: Local SQLite. Target-work fields (`target_project_id`,
  `target_task_id`, etc.) are set by callers (Runner, Bridge, workers) when
  posting messages.
- **channel_activity_events**: Local SQLite. Worker activity events carry
  `project_id`, `task_id`, `assignment_id`, `worker_run_id`.
- **Worker-pool state**: Core API. Best-effort; graceful degradation to null.

Full live routing depends on callers consistently populating target-work fields
on messages and activity events. The routing service reads but does not write
these fields.

## Relationship to existing components

- **Agents Overview** (`/api/agents/overview`): Shows agent state per
  channel/project. Active-work routing is complementary — it resolves a
  specific continuation target by work identity.
- **Assignment Trace** (`/api/gateway/assignments/{id}/trace`): Provides full
  assignment evidence. Active-work routing links to trace via handles.
- **Direct Agent Events** (`GET /api/direct-agent-events`): Legacy wake-event
  readback with target-work fields. New executable wake creation belongs to
  Delivery; the old Channels POST route is retired.

## Follow-up recommendations

1. **Den Web integration**: Wire the resolve endpoint into the Den Web session
   composer so "ask about task X" resolves to the active worker. Owning project:
   den-web.

2. **Runner continuation**: Wire the resolve endpoint into the Runner's
   continuation path so tool-limit extensions route to the correct session.
   Owning project: den-hermes-bridge.

3. **Gateway session reuse**: Bridge consumers should use `sessionOwnerId` and
   `sessionId` from resolved routes to reuse Gateway sessions across channels.
   Owning project: den-gateway.

4. **Core worker-pool live enrichment**: When the Core worker-pool API is
   available, enrichment with assignment phase and checkpoint state adds
   real-time liveness. The projection already supports this via best-effort
   worker-pool client.

5. **Staleness policy tuning**: The 30-minute staleness threshold is a starting
   point. Production tuning may need per-role thresholds (coder vs reviewer
   vs planner).
