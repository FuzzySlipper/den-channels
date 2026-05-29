# Assignment Trace Aggregate (task #1737)

## Route ownership

| Route | Method | Owner | Consumer |
|-------|--------|-------|----------|
| `/api/gateway/assignments/{assignmentId}/trace` | GET | GatewayRoutes (#1737) | Den Web #1729, operators |
| `/api/assignments/{assignmentId}/trace` | GET | ChannelRoutes (alias) | Den Web #1729 (legacy path) |

The canonical implementation lives in `GatewayRoutes.cs` under the `/api/gateway` group.
The alias in `ChannelRoutes.cs` at `/api/assignments/{assignmentId}/trace` exists because
Den Web's gateway client calls that path; both delegates to the same handler method
(`GatewayRoutes.HandleAssignmentTraceAsync`).

## Den Web contract

Consumer: `/home/dev/den-web/src/api/gateway/types.ts` `AssignmentTraceResponse`

Response shape (serialized JSON):

```json
{
  "assignmentId": "string",
  "projectId": "string | null",
  "projectName": "string | null",
  "taskId": "number | null",
  "taskTitle": "string | null",
  "agentIdentity": "string | null",
  "workerRunId": "string | null",
  "workerRole": "string | null",
  "coreAvailability": "'available' | 'core_unavailable'",
  "gatewayAvailability": "'available' | 'no_assignment_messages' | 'delivery_missing'",
  "messagesAvailability": "'available' | 'no_assignment_messages'",
  "activityAvailability": "'available' | 'no_activity_events'",
  "coreState": { "phase", "assignedAgent", "checkpoints", ... } | null,
  "gatewayEvidence": { "deliveryRequestId", "deliveryStatus", ... } | null,
  "channelMessages": [ ... ChannelMessageDto ... ],
  "activityEvents": [ ... ChannelActivityEventDto ... ],
  "summary": "string | null"
}
```

### Source availability values

| Value | Meaning |
|-------|---------|
| `available` | Source responded with data |
| `core_unavailable` | Core worker-pool endpoint unreachable/disabled/timeout |
| `no_assignment_messages` | No channel messages tagged with this assignment ID |
| `no_activity_events` | No activity events tagged with this assignment ID |
| `delivery_missing` | Messages exist but none have `DeliveryRequestId` |

## Service boundaries

- **den-channels** calls Den Core via `IWorkerPoolStateClient` as an upstream evidence
  source. Core state is a read-only projection; no Core ledger state is moved into
  Channels storage.
- Gateway evidence is derived from channel message metadata (the `MetadataJson` field
  on `channel_messages` rows), not by calling the external Gateway service directly.
  The `deliveryRequestId`, `deliveryStatus`, `claimStatus`, `completionStatus`, and
  `suppressionStatus` fields are read from the message's `MetadataJson` when available.
- Messages and activity events are queried from the local SQLite database scoped to
  the resolved channel and filtered by `AssignmentId`.

## Dependencies

- `IWorkerPoolStateClient` (from `AgentsOverview`) — calls Core worker-pool read endpoints:
  `/api/worker-pool/assignments/{id}`, `/api/worker-pool/checkpoints`, and
  `/api/worker-pool/responses/by-run/{runId}` for the assignment trace; overview
  screens continue to use `/api/worker-pool/members` and `/api/worker-pool/assignments`.
  Graceful degradation: returns null on failure.
- `ChannelsRepository` — local SQLite queries for messages and activity events by
  `AssignmentId`.

## Live smoke notes

After deployment by Runner:
1. Ensure a channel message exists with `AssignmentId = '1'` in
   project `den-hermes-bridge` (created by direct-agent wake).
2. Make request: `GET /api/gateway/assignments/1/trace?projectId=den-hermes-bridge`
3. Expected: 200 response with `channelMessages` containing the wake message,
   `coreAvailability` = `available` when Core still has assignment id `1`,
   `coreState.phase`/`finalStatus` matching the Core assignment, checkpoint history
   projected from Core, and `messagesAvailability` = `available`.
4. If Core is down or the assignment has been pruned, the route must still return
   200 with `coreAvailability = core_unavailable` while preserving Channels/Gateway
   message evidence.
