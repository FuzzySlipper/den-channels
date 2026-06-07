# Direct-agent delivery status contract

**Note (task #1848):** The full Direct Delivery / Channels Operations Contract v0 is frozen at `docs/direct-delivery-contract-v0.md` with machine-readable schema at `docs/schemas/direct-delivery-contract-v0.json`. Vocabulary constants are in `DirectDeliveryContractV0.cs`. This document remains as a focused reference for delivery status semantics.

Den Channels owns direct-agent event creation. The primary recording API is `POST /api/direct-agent-events`, which writes a durable `wake_event` channel message and returns immediately with `{ eventId, status: "recorded" }` plus readback handles. Den Gateway / den-host owns runtime claim, delivery, acknowledgement, and completion truth after that record exists. The older `POST /api/gateway/direct-agent-messages` compatibility alias was retired in task #2022 and now returns 410 Gone pointing to `POST /api/direct-agent-events`.

## Request fields

Primary Channels-owned callers should send:

```json
{
  "channelId": 16,
  "memberIdentity": "voxelforge-runner",
  "senderIdentity": "voxelforge-planner",
  "body": "Please pick up task #1720."
}
```

The Channels-owned route has no `waitFor` control and never depends on Gateway availability. The retired compatibility alias `POST /api/gateway/direct-agent-messages` returned 410 Gone as of task #2022; callers must use `POST /api/direct-agent-events` instead.

## Response handles (canonical POST /api/direct-agent-events)

The response includes stable handles for follow-up diagnostics:

- `eventId` / `eventUrl`: the Channels wake-event message that was recorded and can be read via `GET /api/direct-agent-events/{eventId}`.
- `requestId`: stable source id shaped `direct-agent-message:{channelId}:{memberIdentity}:{guid}`.
- `eventsUrl`: Channels event cursor near the recorded message (pointing at `/api/direct-agent-events`).
- `deliveryRequestId`: Gateway delivery request id when Gateway has created one for this request.
- `attemptId`: latest Gateway delivery attempt id when an adapter/runtime has attempted or claimed delivery.

## Status vocabulary

`deliveryStatus` is the operator-facing summary. The canonical vocabulary is the Direct Delivery / Channels Operations Contract v0 in `docs/direct-delivery-contract-v0.md`; this focused table mirrors its delivery status values for quick operator reference:

| deliveryStatus | Meaning |
| --- | --- |
| `recorded_but_not_claimed_yet` | Channels recorded the wake_event, but no matching delivery/claim evidence was observed. |
| `recorded_no_subscriber` | Channels recorded the wake_event and found no active subscription for the target member. |
| `recorded_pending_claim` | Channels recorded the wake_event and found an active subscription, but no runtime/adapter claim yet. |
| `recorded_unreachable_subscription` | Channels recorded the wake_event, but only unreachable/degraded subscriptions were found. |
| `enqueued` | A delivery request exists, but the target runtime has not claimed it yet. |
| `claimed` | Delivery request was claimed by the target runtime/adapter. |
| `received` | Target adapter/runtime reported the delivery as delivered/received. |
| `acknowledged` | Target runtime/session acknowledged accepting the prompt/wake. |
| `completed` | Target runtime reported final delivery completion. |
| `suppressed` | A suppression decision was recorded. |
| `failed` / `expired` | A terminal failure or expiry was recorded. |

Supplemental fields:

- `claimStatus`: `unclaimed`, `claimed`, `no_subscriber`, or `subscription_unreachable`.
- `completionStatus`: `pending`, `completed`, `failed`, `expired`, or `suppressed`.
- `suppressionStatus`: `not_suppressed` or `suppressed`.
- `gatewayDeliveryState` / `gatewayAttemptStatus`: raw historical/compatibility Gateway state observed, if any.
- `timedOut`: true when the bounded wait ended before the requested target state.
- `gatewayUnavailable`: true when the historical/compatibility Gateway projection could not be read.

## Safety rules

- Do not report `received`, `acknowledged`, or `completed` solely because Channels wrote a wake_event.
- If the response says `recorded_but_not_claimed_yet`, treat it as durable recording evidence only; follow `deliveryRequestId`, `requestId`, `eventUrl`, or `eventsUrl` later.
- A `completed` delivery is only delivery completion. Use task-thread or worker/review packets for task completion truth.
