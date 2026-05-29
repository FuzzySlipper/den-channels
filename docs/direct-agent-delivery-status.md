# Direct-agent delivery status contract

Den Channels records direct-agent messages as `wake_event` channel messages, but Den Gateway owns runtime delivery truth. The `POST /api/gateway/direct-agent-messages` response therefore returns both the Channels recording handle and a bounded observation of Gateway state.

## Request fields

Existing callers may continue sending:

```json
{
  "channelId": 16,
  "memberIdentity": "voxelforge-runner",
  "senderIdentity": "voxelforge-planner",
  "body": "Please pick up task #1720."
}
```

Optional backwards-compatible acknowledgement controls:

- `waitFor`: `none`, `claim`, `ack`, or `completion`; default `claim`.
- `timeoutMs`: bounded wait in milliseconds; clamped to `0..5000`; default `1500`.

The wait is best-effort and bounded. It never proves task completion, and it never blocks indefinitely.

## Response handles

The response includes stable handles for follow-up diagnostics:

- `messageId` / `gatewayMessageUrl`: the Channels wake-event message that was recorded.
- `requestId`: stable source id shaped `direct-agent-message:{channelId}:{memberIdentity}:{timestamp}`.
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
