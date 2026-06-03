# Direct-agent delivery status contract

**Note (task #1848):** The full Direct Delivery / Channels Operations Contract v0 is frozen at `docs/direct-delivery-contract-v0.md` with machine-readable schema at `docs/schemas/direct-delivery-contract-v0.json`. Vocabulary constants are in `DirectDeliveryContractV0.cs`. This document remains as a focused reference for delivery status semantics.

Den Channels owns direct-agent event creation. The primary recording API is `POST /api/direct-agent-events`, which writes a durable `wake_event` channel message and returns immediately with `{ eventId, status: "recorded" }` plus readback handles. Den Gateway / den-host owns runtime claim, delivery, acknowledgement, and completion truth after that record exists. The older `POST /api/gateway/direct-agent-messages` route remains a compatibility alias; it now returns immediately by default and only performs the legacy Gateway poll/spin-wait when callers explicitly set `waitFor=claim`, `waitFor=ack`, or `waitFor=completion`.

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

The Channels-owned route has no `waitFor` control and never depends on Gateway availability.

Optional backwards-compatible acknowledgement controls on the compatibility route only (`POST /api/gateway/direct-agent-messages`):

- `waitFor`: `none`, `claim`, `ack`, or `completion`; default `none`.
- `timeoutMs`: bounded wait in milliseconds; clamped to `0..5000`; default `1500`.

The compatibility wait is best-effort and bounded. It never proves task completion, and it never blocks indefinitely.

## Response handles

The response includes stable handles for follow-up diagnostics:

- `eventId` / `eventUrl`: the Channels wake-event message that was recorded and can be read via `GET /api/direct-agent-events/{eventId}`.
- `messageId` / `gatewayMessageUrl`: compatibility names for the same Channels wake-event record when using `/api/gateway/direct-agent-messages`.
- `requestId`: stable source id shaped `direct-agent-message:{channelId}:{memberIdentity}:{guid}`.
- `gatewayEventsUrl`: Channels event cursor near the recorded message.
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
- If the response says `recorded_but_not_claimed_yet`, treat it as durable recording evidence only; follow `deliveryRequestId`, `requestId`, or `gatewayEventsUrl` later.
- A `completed` delivery is only delivery completion. Use task-thread or worker/review packets for task completion truth.
