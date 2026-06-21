# Direct-agent delivery status contract

**Note (task #1848):** The full Direct Delivery / Channels Operations Contract v0 is frozen at `docs/direct-delivery-contract-v0.md` with machine-readable schema at `docs/schemas/direct-delivery-contract-v0.json`. Vocabulary constants are in `DirectDeliveryContractV0.cs`. This document remains as a focused reference for delivery status semantics.

Den Channels no longer owns new direct-agent executable wake creation. As of task #3025, `POST /api/direct-agent-events`, `POST /api/direct-conversations/{id}/send`, `POST /api/gateway/direct-agent-messages`, and `POST /api/gateway/test-wakes` return 410 Gone pointing to the Delivery successor `POST /v1/delivery/intents`. Den Channels retains legacy `wake_event` readback through `GET /api/direct-agent-events` and `GET /api/direct-agent-events/{eventId}` for historical evidence.

## Retired request fields

Historical Channels-owned callers sent:

```json
{
  "channelId": 16,
  "memberIdentity": "voxelforge-runner",
  "senderIdentity": "voxelforge-planner",
  "body": "Please pick up task #1720."
}
```

The Channels-owned write route is retired. New callers must create executable wakes through Delivery and write human-facing transcript evidence through the Conversation successor.

## Historical response handles

Legacy write responses included stable handles for follow-up diagnostics:

- `eventId` / `eventUrl`: the Channels wake-event message that was recorded and can be read via `GET /api/direct-agent-events/{eventId}`.
- `requestId`: stable source id shaped `direct-agent-message:{channelId}:{memberIdentity}:{guid}`.
- `eventsUrl`: Channels event cursor near the recorded message (pointing at `/api/direct-agent-events`).
- `deliveryRequestId`: Gateway delivery request id when Gateway has created one for this request.
- `attemptId`: latest Gateway delivery attempt id when an adapter/runtime has attempted or claimed delivery.

## Status vocabulary

`deliveryStatus` is the operator-facing summary:

| deliveryStatus | Meaning |
| --- | --- |
| `recorded_but_not_claimed_yet` | Channels recorded the wake_event, but no matching Gateway delivery/claim evidence was observed within the bounded wait, or Gateway was unavailable. |
| `enqueued` | Gateway created a delivery request, but the target runtime has not claimed it yet. |
| `claimed` | Gateway delivery request was claimed by the target runtime/adapter (`delivery_requests.status=delivering`). |
| `received` | Target adapter/runtime reported the delivery as delivered/received. |
| `acknowledged` | Target runtime/session acknowledged accepting the prompt/wake. |
| `completed` | Target runtime reported final delivery completion. |
| `suppressed` | Gateway recorded a suppression decision. |
| `failed` / `expired` | Gateway recorded a terminal failure or expiry. |

Supplemental fields:

- `claimStatus`: `unclaimed` or `claimed`.
- `completionStatus`: `pending`, `completed`, `failed`, `expired`, or `suppressed`.
- `suppressionStatus`: `not_suppressed` or `suppressed`.
- `gatewayDeliveryState` / `gatewayAttemptStatus`: raw Gateway state observed, if any.
- `timedOut`: true when the bounded wait ended before the requested target state.
- `gatewayUnavailable`: true when the Gateway projection could not be read.

## Safety rules

- Do not report `received`, `acknowledged`, or `completed` solely because Channels wrote a wake_event.
- If the response says `recorded_but_not_claimed_yet`, treat it as durable recording evidence only; follow `deliveryRequestId`, `requestId`, `eventUrl`, or `eventsUrl` later.
- A `completed` delivery is only delivery completion. Use task-thread or worker/review packets for task completion truth.
