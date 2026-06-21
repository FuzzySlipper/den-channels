# Direct Delivery / Channels Operations Contract v0

Status: **frozen v0** (task #1848)

This document supersedes the earlier `gateway-contract-drift-analysis` and `channels-operations-hub-interface-review` framing. The v0 contract is a Direct Delivery / Channels operations contract. Gateway compatibility alias routes were retired in task #2022 and now return 410 Gone; Core remains workflow truth.

## Contract owner

Den Channels owns direct-agent event creation, delivery status observation, assignment trace aggregation, and the cross-boundary vocabulary published in this document. Gateway* DTO names are retained only for historical/diagnostic compatibility reference; semantics are Direct Delivery / Channels operations, not Gateway-owned.

## Vocabulary authority

All status/kind/policy fields are pinned as named constants in `DirectDeliveryContractV0.cs` (namespace `DenChannels.Service`). The machine-readable schema is at `docs/schemas/direct-delivery-contract-v0.json`.

### Delivery status (`deliveryStatus`)

| Constant | Value | Meaning |
| --- | --- | --- |
| `RecordedNotClaimedYet` | `recorded_but_not_claimed_yet` | Channels recorded the wake_event, no delivery/claim evidence observed. |
| `Enqueued` | `enqueued` | Gateway created a delivery request, not yet claimed. |
| `Claimed` | `claimed` | Delivery request claimed by target runtime/adapter. |
| `Received` | `received` | Target adapter reported delivery as received. |
| `Acknowledged` | `acknowledged` | Target runtime acknowledged accepting the prompt/wake. |
| `Completed` | `completed` | Target runtime reported final delivery completion. |
| `Suppressed` | `suppressed` | Gateway recorded a suppression decision. |
| `Failed` | `failed` | Gateway recorded a terminal delivery failure. |
| `Expired` | `expired` | Gateway recorded a terminal delivery expiry. |

### Claim status (`claimStatus`)

| Constant | Value |
| --- | --- |
| `Unclaimed` | `unclaimed` |
| `Claimed` | `claimed` |

### Completion status (`completionStatus`)

| Constant | Value | Terminal? |
| --- | --- | --- |
| `Pending` | `pending` | No |
| `Completed` | `completed` | Yes |
| `Failed` | `failed` | Yes |
| `Expired` | `expired` | Yes |
| `Suppressed` | `suppressed` | Yes |

### Suppression status (`suppressionStatus`)

| Constant | Value |
| --- | --- |
| `NotSuppressed` | `not_suppressed` |
| `Suppressed` | `suppressed` |

### Wait-for target (`waitFor`)

Retired as of task #2022, then superseded by the Delivery successor in task #3025. The Gateway compatibility route `POST /api/gateway/direct-agent-messages` now returns 410 Gone pointing to `POST /v1/delivery/intents`. The `waitFor` vocabulary is preserved here for historical reference only.

| Constant | Value |
| --- | --- |
| `None` | `none` |
| `Claim` | `claim` |
| `Ack` | `ack` |
| `Completion` | `completion` |

### Message kind (`messageKind`)

Matches the SQLite CHECK constraint on `channel_messages.message_kind`.

| Constant | Value |
| --- | --- |
| `HumanText` | `human_text` |
| `AgentText` | `agent_text` |
| `SystemEvent` | `system_event` |
| `MirrorSummary` | `mirror_summary` |
| `Command` | `command` |
| `CommandResult` | `command_result` |

### Source kind (`sourceKind`)

| Constant | Value |
| --- | --- |
| `WakeEvent` | `wake_event` |
| `GatewayDelivery` | `gateway_delivery` |
| `TaskMessage` | `task_message` |
| `AgentStreamEntry` | `agent_stream_entry` |
| `Notification` | `notification` |
| `WorkerRun` | `worker_run` |
| `ReviewRound` | `review_round` |
| `ReviewFinding` | `review_finding` |
| `ExternalAdapterMessage` | `external_adapter_message` |

### Wake policy (`wakePolicy`)

| Constant | Value |
| --- | --- |
| `AllMessages` | `all_messages` |
| `AllMessagesExceptSelf` | `all_messages_except_self` |
| `AllHumanMessages` | `all_human_messages` |
| `DirectQuestionsOnly` | `direct_questions_only` |
| `MentionsOnly` | `mentions_only` |
| `Never` | `never` |

### Member type (`memberType`)

| Constant | Value |
| --- | --- |
| `Agent` | `agent` |
| `User` | `user` |
| `System` | `system` |

### Membership status (`membershipStatus`)

| Constant | Value |
| --- | --- |
| `Active` | `active` |
| `Muted` | `muted` |
| `Left` | `left` |

### Channel kind (`channelKind`)

| Constant | Value |
| --- | --- |
| `ProjectDefault` | `project_default` |
| `AdHoc` | `ad_hoc` |
| `System` | `system` |
| `WorkerPoolLobby` | `worker_pool_lobby` |

### Worker-pool lobby status

| Constant | Value |
| --- | --- |
| `Idle` | `idle` |
| `Leased` | `leased` |
| `Draining` | `draining` |
| `Released` | `released` |
| `Quarantined` | `quarantined` |
| `Offline` | `offline` |

### Trace source availability

| Constant | Value |
| --- | --- |
| `Available` | `available` |
| `CoreUnavailable` | `core_unavailable` |
| `GatewayUnavailable` | `gateway_unavailable` |
| `NoAssignmentMessages` | `no_assignment_messages` |
| `NoActivityEvents` | `no_activity_events` |
| `DeliveryMissing` | `delivery_missing` |
| `Pending` | `pending` |

## Field groups

### Source context

Fields identifying where the interaction happened:

- `sourceProjectId` / `channelId` — the source/control context (transport attribution).

### Target-work attribution (#1845)

Fields for the target project work, not inferred from the channel:

- `targetProjectId`
- `targetTaskId`
- `assignmentId`
- `workerRunId`
- `workerRole`
- `profileIdentity`
- `poolMemberId`

### Session-owner fields (#1887)

Fields for the target durable agent instance/session:

- `agentInstanceId`
- `sessionOwnerId`
- `sessionId`

### Delivery/checkpoint visibility

Fields for delivery observation status:

- `deliveryStatus`
- `claimStatus`
- `completionStatus`
- `suppressionStatus`

## Typed DTO boundary (task #1848)

The `AssignmentTraceResponse` now uses typed DTOs:

- `ChannelMessages` → `IReadOnlyList<GatewayEventItemDto>` (was `IReadOnlyList<object>`)
- `ActivityEvents` → `IReadOnlyList<ChannelActivityEventDto>` (was `IReadOnlyList<object>`)

## Boundary decisions

1. **Core is workflow truth.** Channels does not write assignment, lease, run, completion, release, or quarantine state to Core. It observes Core through explicit HTTP/event contracts.
2. **Gateway compatibility aliases retired (task #2022).** `POST /api/gateway/direct-agent-messages`, `POST /api/gateway/test-wakes`, `GET /api/gateway/events`, `POST /api/gateway/channel-activity-events`, and `GET /api/gateway/channel-activity-events/status` return 410 Gone. `Gateway*` DTO names persist in the contract for historical/compatibility reference but are no longer produced by live routes.
3. **`UseStubProjectMetadata` / Core metadata/outbox** remains a separate Channels↔Core integration concern. It does not block Direct Delivery contract hardening.
4. **`sourceProjectId` preserved** for backward compatibility but target-work fields are explicit and must not be inferred from channel/project.
5. **No Hermes/Pi/Codex/OpenCode/Claude Code terms** in public Core/Channels contract names.

## API routes

| Route | Owner | Description |
| --- | --- | --- |
| `POST /api/direct-agent-events` | Channels (retired) | RETIRED (task #3025). Returns 410 Gone pointing to `POST /v1/delivery/intents`. |
| `GET /api/direct-agent-events` | Channels | Cursor-paged legacy direct-agent event readback. |
| `GET /api/direct-agent-events/{eventId}` | Channels | Readback for a single legacy direct-agent event. |
| `POST /api/gateway/direct-agent-messages` | Channels (retired) | RETIRED (task #2022/#3025). Returns 410 Gone pointing to `POST /v1/delivery/intents`. |
| `POST /api/gateway/test-wakes` | Channels (retired) | RETIRED (task #2022/#3025). Returns 410 Gone pointing to `POST /v1/delivery/intents`. |
| `GET /api/gateway/events` | Channels (retired) | RETIRED (task #2022). Returns 410 Gone pointing to `GET /api/direct-agent-events`. |
| `POST /api/gateway/system-messages` | Channels (retired) | RETIRED (task #3026). Returns 410 Gone pointing to `POST /v1/conversation/channels/{channel_id}/messages`. |
| `POST /api/channel-activity-events` | Channels | Non-waking progress/activity writer with Channels-owned validation/defaulting/status diagnostics. Prefer `POST /api/channels/{channelId}/activity-events` for new callers. |
| `POST /api/gateway/channel-activity-events` | Channels (retired) | RETIRED (task #2022). Returns 410 Gone pointing to `POST /api/channels/{channelId}/activity-events`. |
| `GET /api/gateway/channel-activity-events/status` | Channels (retired) | RETIRED (task #2022). Returns 410 Gone pointing to `GET /api/channel-activity-events/status`. |
| `GET /api/gateway/assignments/{assignmentId}/trace` | Channels | Assignment trace aggregate from Core + Channels + Gateway evidence. |
| `GET /api/assignments/{assignmentId}/trace` | Channels | Den Web alias for assignment trace. |
| `GET /api/gateway/messages/{messageId}` | Channels | Single message lookup. |
| `GET /api/gateway/memberships` | Channels | Channel membership/wake-policy snapshot. |

## Safety rules

1. Do not report `received`, `acknowledged`, or `completed` solely because Channels wrote a wake_event.
2. `recorded_but_not_claimed_yet` is durable recording evidence only; follow `deliveryRequestId`, `requestId`, or `eventsUrl` later.
3. `completed` delivery is delivery completion only. Use task-thread or worker/review packets for task completion truth.
4. Bridge/Gateway local state is not canonical for assignments, leases, runs, completion, release, or quarantine.
