# Durable Den Channels Gateway Sessions and Focused Session UI Spec

Status: implementation-ready spec for Den task #1494, coordinated with #1480.

Related Den records:

- ADR: `den-channels-durable-gateway-sessions-adr`
- Task #1480: canonical Channels `sourceKind` for Gateway delivery replies
- Task #1494: this spec
- Task #1495: implementation of the durable Gateway session path
- Task #1496: cleanup of test-only heartbeat / one-shot wake scaffolding

## 1. Decision summary

Den Channels should stop treating the one-shot `hermes chat -q` wake consumer as the conversational green path. Durable agent conversations should be hosted by a long-running Hermes Gateway profile process through a native Den Channels platform adapter.

Primary green path:

```text
Den Web/Desktop channel or focused session UI
  -> Den Channels HTTP API
  -> Den Gateway routing / binding / claim state
  -> long-running Hermes Gateway den_channels adapter
  -> GatewayRunner / SessionStore / AIAgent
  -> Den Channels visible replies + status/tool events
```

Den Web / Den Desktop are the product surfaces for this phase. Do not design this phase around TUI attach. Keep boundaries clean enough that future clients could exist, but no TUI attach protocol, live terminal connection, or terminal session injection is part of this implementation.

## 2. Component ownership

### Den Channels

Owns:

- channel, message, membership, reaction, and cursor storage;
- focused session UI route and existing channel panel behavior;
- visible conversation transcript for Den users;
- source-pointer fields on channel messages;
- display of working/busy/replied/session state;
- safe HTTP APIs for channel messages and session-lane discovery.

Den Channels should not own Hermes transcript/session persistence and should not directly mutate Den Gateway delivery internals.

### Den Gateway

Owns:

- adapter bindings and heartbeat snapshots;
- delivery request creation, suppression, priority, claim leases, attempts, completion/failure state;
- routing a channel message/wake request to the target adapter/profile;
- durable audit of delivery lifecycle.

Gateway remains the routing authority. The Hermes adapter should claim or receive deliveries through Gateway contracts rather than polling channel tables directly.

### Hermes Gateway Den Channels adapter

Owns:

- long-running profile process for a specific Hermes profile/agent identity;
- registering/refreshing a `hermes_profile` binding with Den Gateway;
- claiming Den Gateway deliveries for that binding;
- converting claimed deliveries into normal GatewayRunner platform events;
- mapping Den lanes into stable Hermes `SessionSource` / `session_key` values;
- sending final replies and structured status/tool events back to Den Channels/Gateway.

### Hermes GatewayRunner / SessionStore

Owns:

- durable Hermes session identity and transcript persistence;
- slash commands (`/new`, `/reset`, `/model`, `/queue`, etc.) where applicable;
- one active agent turn per session with busy/queue behavior;
- AIAgent cache warmth as an optimization, not as the durable source of truth.

## 3. Native adapter contract

The Den Channels Hermes adapter should be implemented as a first-class Hermes Gateway platform named `den_channels` or equivalent plugin platform. It should run inside the normal long-running service:

```bash
hermes --profile <profile> gateway run
```

For the `den-channels-runner` profile, the intended systemd shape is the normal Hermes gateway template, not a separate one-shot wake service.

### Adapter startup

On startup the adapter must:

1. load its configured Den Gateway URL and Den Channels URL;
2. derive the profile/agent identity/role binding from config;
3. upsert a Den Gateway adapter binding with:
   - `adapter_kind = hermes_profile`
   - `adapter_instance_id = <host>:<profile>:<role>:gateway`
   - `agent_identity`
   - `project_id` when project-scoped, or an explicit multi-project capability when supported
   - `role`
   - `status = active`
   - capabilities including accepted delivery modes, session support, and status event support;
4. begin a claim/long-poll loop or subscribe if Gateway later exposes streaming delivery.

### Delivery claim

For v1, reuse the existing Gateway claim contract:

```http
POST /api/deliveries/claim
```

The adapter claims deliveries for its binding with accepted modes such as:

- `wake`: user/channel message that should enter the Hermes session;
- `notify`: visible status that may be displayed but should not necessarily enter conversational context;
- later: `session_status` / `tool_event` if Gateway separates them.

The adapter must fail closed when binding/project/agent/role data is missing or ambiguous.

### Event conversion

A claimed delivery that represents a user/channel message becomes a normal GatewayRunner event with a Den Channels `SessionSource`:

- `platform`: `den_channels`
- `chat_type`: `channel` for project/channel lanes, `thread` for thread/task lanes when applicable
- `chat_id`: stable Den Channels lane id, not a transient message id
- `chat_name`: channel slug/display name when available
- `thread_id`: thread root message id or task id for scoped lanes
- `user_id`: Den user/sender identity
- `user_name`: display identity if available
- `message_id`: source channel message id

The adapter should pass enough channel/project context for the dynamic session prompt to say where the message came from without embedding secrets or unbounded payloads.

### Reply posting

Final assistant text replies should be posted to Den Channels as `channel_messages` with:

- `senderType = agent`
- `senderIdentity = <agent identity>`
- `messageKind = agent_text`
- `sourceKind = gateway_delivery`
- `sourceId = <delivery_request_id>`
- `sourceProjectId = <project_id>`
- `replyToMessageId = <trigger channel message id>` when available
- `threadRootMessageId = <lane thread root>` when available
- `dedupeKey = channel-message:<trigger_message_id>:agent:<agent_identity>` for direct message/reply flows, or another deterministic delivery-scoped key
- `metadataJson` containing bounded non-secret data:
  - `delivery_request_id`
  - `attempt_id`
  - `adapter_instance_id`
  - `session_key`
  - `session_id`
  - `original_source_kind`
  - `original_source_id`
  - `completion_status`

After posting the visible reply, the adapter should complete or mark delivered in Den Gateway with the resulting channel message id as the external/visible message handle.

### Status/tool events

The adapter should surface long inference/tool windows visibly. V1 can keep the derived UI model, but the durable path should prefer explicit status events over guessing forever.

Recommended status event shape in channel message metadata or a future status endpoint:

- `session_key`
- `session_id`
- `delivery_request_id`
- `state`: `claimed`, `thinking`, `tool_running`, `queued`, `reply_posted`, `failed`
- `tool_name` when safe
- `summary` bounded for display
- timestamps

Debug/noisy tool chunks must not flood the channel transcript by default. The focused session UI may show richer live status than the default channel panel.

## 4. Session lane and key semantics

Every stable Den Channels lane maps to one stable Hermes session key until `/new` resets that lane.

Canonical lane types:

### Project default channel lane

Used for normal project-wide chat.

```text
den_channels:project:<project_id>:channel:<channel_id>
```

Hermes session key should be produced through Gateway `build_session_key` from:

```text
platform=den_channels
chat_type=channel
chat_id=project:<project_id>:channel:<channel_id>
thread_id=null
```

This intentionally gives a shared channel session for the agent/lane, not per-human fragmentation. Configure Den Channels platform defaults to `group_sessions_per_user=false` or implement platform-specific override so a project channel remains a shared planning lane.

### Channel thread lane

Used when replying in a channel thread rooted at a channel message.

```text
den_channels:project:<project_id>:channel:<channel_id>:thread:<thread_root_message_id>
```

Gateway source:

```text
platform=den_channels
chat_type=thread
chat_id=project:<project_id>:channel:<channel_id>
thread_id=thread:<thread_root_message_id>
```

Thread sessions should be shared across participants by default.

### Task room lane

Used for task-specific focused work.

```text
den_channels:project:<project_id>:task:<task_id>
```

This can be represented either as a `task_room` channel with `chat_type=channel`, or as a thread under the project channel with `thread_id=task:<task_id>`. The implementation should pick one representation and keep it stable. Prefer a real `task_room` channel if Den Web/Desktop needs a dedicated focused task surface.

### Direct agent message lane

Direct messages from a channel to a target agent should normally enter the same lane as the originating channel/thread, not create a hidden per-user DM session, unless the UI explicitly starts a private/ad-hoc DM channel.

This keeps “ask the project runner in the project channel” conversationally coherent.

## 5. `/new` reset semantics

`/new` and `/reset` sent in Den Channels should reset only the selected Den Channels lane.

Requirements:

- `/new` in a project default channel resets only that project channel session for that agent profile.
- `/new` in a thread/task lane resets only that thread/task session.
- `/new` must not wipe unrelated Den Channels lanes, Discord sessions, or the whole Hermes profile.
- The adapter should post a visible command result to the same lane, e.g. “Started a new Hermes session for this Den Channels lane.”
- The new session id/key binding should be visible to the focused session UI.

Implementation expectation: use the existing GatewayRunner command/session machinery where possible by feeding the slash command through the normal gateway path. If Den Channels needs a custom header/body, specialize the Den Channels adapter response text, not the reset semantics.

## 6. Focused Den Web/Desktop session UI

Den Web/Desktop is the primary operator surface for durable sessions. A focused route is preferred over cramming all ergonomics into the existing channel panel.

Suggested route:

```text
/sessions
/projects/:projectId/sessions
/channels/:channelId/session
```

The route should expose:

1. **Session selector**
   - live sessions first;
   - recent sessions second;
   - labels include project/channel/task/thread, agent identity, status, last activity, and model/profile when available.

2. **Connected transcript**
   - channel messages for the selected lane;
   - agent replies;
   - command/result messages;
   - status/tool events with noise suppression;
   - clear distinction between conversational messages and mirrored Den workflow events.

3. **Composer**
   - send normal messages;
   - send slash commands including `/new`;
   - support direct-to-agent target selection when multiple agents are in the lane;
   - preserve posting identity.

4. **Status affordances**
   - active / working / queued / failed / stale;
   - claimed/delivered/reply-posted evidence;
   - current tool or “thinking” state during 50-120s model windows;
   - stale binding warnings.

5. **Den context sidebar**
   - current project/task/thread metadata;
   - links to Den task, review, worker records when the lane is attached to them.

The existing `ChannelChatPanel` can remain as the compact project-channel surface, but the focused session route is the green path for extended planning.

## 7. Canonical `sourceKind` decision for #1480

Decision: add and use `gateway_delivery` as the canonical Den Channels `sourceKind` for visible replies produced by Den Gateway / Hermes Gateway delivery handling.

Rationale:

- `external_adapter_message` is too broad and implies traffic from an outside platform adapter such as Discord/Slack/Telegram.
- Durable Den Channels replies are first-party Gateway delivery artifacts tied to Den Gateway `delivery_requests`, not generic external messages.
- A dedicated source kind makes UI evidence, source-summary lookup, dedupe, and auditing clearer.
- The current `external_adapter_message` workaround should remain only as a temporary compatibility bridge until schema/code catches up.

Required follow-up code changes for #1495:

- add `gateway_delivery` to the `channel_messages.source_kind` CHECK constraint/migration path;
- add it to mirror/source-summary supported source kind lists;
- update the Hermes/Den bridge reply envelope from `external_adapter_message` to `gateway_delivery` once live Channels accepts it;
- update UI reply detection to recognize both during cutover, then prefer `gateway_delivery`;
- update docs and tests so `external_adapter_message` is only for true external adapter ingress or temporary compatibility.

## 8. Heartbeat / one-shot wake cleanup plan

The current heartbeat and one-shot wake path are test scaffolding, not protected legacy.

Known pieces to audit/remove or relabel after cutover:

- `hermes-den-channels-heartbeat@.service` style profile heartbeat units;
- `den-gateway-hermes-consumer.service` / `GatewayDeliveryConsumer` one-shot wake claimer;
- `SpawnedHermesProfileWakeTransport` and `/tmp/den-hermes-wakes` envelope flow;
- UI labels/buttons that imply “test wake” is the current production path;
- docs that present `hermes chat -q` wake runs as the green path.

Cutover rule:

- Before #1495 lands, any retained one-shot path must be labeled test-only.
- After the native durable Gateway path is live and smoked, remove or disable the one-shot consumer by default.
- Do not leave two plausible “current” paths without an explicit deprecation/cleanup note.

## 9. Implementation slices for #1495

Recommended sequence:

1. **SourceKind schema/doc slice**
   - add `gateway_delivery` to Channels schema and tests;
   - update UI reply detection to accept `gateway_delivery` plus legacy `external_adapter_message` during cutover.

2. **Hermes platform registration slice**
   - add/enable a `den_channels` Gateway platform/plugin in Hermes;
   - implement config loading and binding heartbeat.

3. **Delivery claim adapter slice**
   - move the claim loop into the long-running Hermes Gateway adapter;
   - claim Gateway deliveries and convert them to GatewayRunner events without spawning `hermes chat -q`.

4. **Session lane slice**
   - implement Den Channels `SessionSource` mapping;
   - verify stable session key reuse across messages;
   - configure shared channel sessions.

5. **Reply/status slice**
   - post visible replies with `sourceKind=gateway_delivery`;
   - record session id/key in metadata;
   - complete/fail Den Gateway delivery attempts deterministically.

6. **Focused UI slice**
   - add focused route/session selector;
   - connect to channel/session state;
   - show working/status/tool states.

7. **Cutover slice**
   - start the durable gateway service for `den-channels-runner`;
   - smoke message -> delivery -> GatewayRunner session -> reply;
   - disable old one-shot consumer if durable smoke passes.

## 10. Validation checklist

### Unit / fake integration

- Channels schema accepts `gateway_delivery` and rejects unknown source kinds.
- Mirror ingestion/source summaries include `gateway_delivery`.
- UI reply detection treats `gateway_delivery` as final reply evidence.
- Adapter maps delivery envelopes to stable `SessionSource` values.
- Two messages in the same lane reuse the same `session_key` and `session_id`.
- `/new` in one lane changes only that lane's session id.
- Busy/queued messages do not start parallel turns for the same session.
- Adapter fails closed for ambiguous binding/profile/project data.

### Live smoke

- A user message in Den Web project channel creates/uses a Gateway delivery.
- The long-running Hermes Gateway adapter claims it.
- Hermes Gateway records/reuses a Den Channels session.
- Den Channels shows a visible agent reply with `sourceKind=gateway_delivery`.
- Focused session UI shows transcript and working/replied state.
- A second message in the same lane has continuity with the first.
- `/new` resets the lane and the next message starts fresh.
- Restarting the Hermes Gateway service preserves durable transcript/session mapping.
- Old one-shot consumer is stopped/disabled or clearly marked test-only.

## 11. Non-goals

- TUI attach or terminal-session sharing.
- Preserving `hermes chat -q` one-shot wakes as an ongoing compatibility path.
- Moving task/review/workflow truth from Den records into chat transcript only.
- Bypassing Den Gateway delivery/routing state by directly polling Channels messages from Hermes.
