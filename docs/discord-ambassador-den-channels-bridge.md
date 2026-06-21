# Discord ambassador bridge into Den Channels

Status: proposed operator pattern, based on live Den Channels API state verified 2026-05-17T08:41:30Z.

Related Den task: #1504.

## Intent

Patch does not need every Hermes profile in Discord. The desired pattern is a small Discord-accessible "ambassador" profile that can be reached from a phone and can answer or act on questions like:

- "What is agent X doing?"
- "Is agent X active or idle?"
- "Send agent X a Den Channels message so it wakes if idle."
- "Check whether there was a visible Den Channels reply."

This should not require sharing one literal Hermes conversation session between Discord and Den Channels. The ambassador can have its own Discord session and cross the boundary explicitly through Den/Channels tools.

## Recommended architecture

```text
Discord mobile/client
  -> Discord bot profile: den-ambassador or similar
  -> Hermes tools:
       - Den MCP for canonical Den Core state
       - Den Channels API/tool for channel memberships, events, and direct-agent messages
  -> Den Channels / Den Gateway durable delivery path
       - target agent's own den_channels session wakes only if it has an active membership/binding
```

Keep these lanes separate:

- Discord is an operator/coordination surface.
- Den Channels is the shared durable agent conversation surface.
- Den Core/den-mcp remains canonical for tasks, task messages, reviews, findings, documents, and worker runs.
- Den Channels remains canonical for channels, channel messages, memberships, wake-policy inputs, and visible `gateway_delivery` replies.

The ambassador should summarize evidence and link/source ids; it should not silently mirror all Discord chatter into Den Channels.

## Existing live Den Channels endpoints the ambassador can use

The live Den Channels service on `http://192.168.1.10:18080` reports health and these relevant endpoints:

- `GET /health/ready`
- `GET /api/gateway/health`
- `GET /api/gateway/memberships?projectId={projectId}`
- `GET /api/gateway/memberships?channelId={channelId}`
- `GET /api/direct-agent-events?channelId={id}&afterId={id}&limit={n}` (replaces retired `GET /api/gateway/events`)
- `GET /api/gateway/messages/{messageId}`
- `GET /api/gateway/sources/{sourceKind}/{sourceId}?sourceProjectId={projectId}`
- `POST /v1/delivery/intents` through Gateway for new direct-agent wake intents; legacy Channels wake writers now return 410 Gone
- `POST /api/gateway/system-messages`

Live readiness verified:

```json
{
  "service": "den-channels",
  "status": "ready",
  "checks": {
    "configuration": "ok",
    "databasePath": "/data/services/den-channels/data/den-channels.db",
    "denCoreBaseUrl": "http://127.0.0.1:5299"
  }
}
```

Current `den-channels` membership snapshot verified at task creation time:

```json
{
  "channelId": 2,
  "channelSlug": "project-den-channels",
  "channelKind": "project_default",
  "projectId": "den-channels",
  "members": [
    {
      "id": 1,
      "memberType": "agent",
      "memberIdentity": "den-channels-runner",
      "membershipStatus": "active",
      "wakePolicy": "all_human_messages",
      "canSend": true,
      "cooldownSeconds": 60,
      "maxAutoRepliesPerWindow": 1,
      "settingsLabel": null
    }
  ]
}
```

## Ambassador capabilities

### 1. Status: "what is agent X doing?"

The ambassador should combine multiple evidence layers, in this order:

1. Den Channels membership:
   - Is the agent a member of the project/channel?
   - Is membership `active`?
   - What is `wakePolicy` and can it send?
2. Den Channels recent events:
   - Recent human/direct messages to the agent.
   - Recent `agent_text` replies with `sourceKind=gateway_delivery`.
   - Recent `wake_event` or direct-agent-message source ids still lacking a visible reply.
3. Den/Gateway state when available:
   - active/degraded bindings for the agent/role/profile;
   - delivery requests/attempts if exposed to the ambassador profile through Den/Gateway tools.
4. Den Core/MCP:
   - assigned/in-progress/review tasks;
   - recent task messages;
   - review findings requiring action;
   - worker runs or completion packets.

The answer should be evidence-labeled, e.g.:

> `den-channels-runner` is joined to `project-den-channels` with `wakePolicy=all_human_messages`. Last channel event for it was direct-agent-message source id `...`; I see a later `gateway_delivery` agent reply at message id N, so the wake appears completed. Den task #X is currently in progress.

If the evidence is missing, say so directly rather than fabricating activity:

> I can see the Den Core task state, but I cannot see a Den Channels membership or recent Gateway reply for that agent, so I cannot prove a Channels wake path exists.

### 2. Send: "message/wake agent X"

If the target agent has an active Den Channels membership for the requested project/channel, the ambassador records a wake via the canonical route (the retired `POST /api/gateway/direct-agent-messages` returns 410 Gone as of task #2022):

```bash
curl -fsS -X POST http://192.168.1.10:18080/api/direct-agent-events \
  -H 'Content-Type: application/json' \
  --data-binary @- <<'JSON'
{
  "projectId": "den-channels",
  "memberIdentity": "den-channels-runner",
  "senderIdentity": "discord-ambassador",
  "body": "Operator request from Discord: please check task #1504 and reply in Den Channels."
}
JSON
```

Expected result shape:

```json
{
  "status": "recorded",
  "eventId": 123,
  "channelId": 2,
  "requestId": "direct-agent-message:2:den-channels-runner:...",
  "memberIdentity": "den-channels-runner",
  "wakePolicy": "all_human_messages",
  "eventUrl": "/api/direct-agent-events/123",
  "eventsUrl": "/api/direct-agent-events?channelId=2&afterId=122&limit=10",
  "evidenceSummary": "..."
}
```

The ambassador should then poll or re-read `eventsUrl` to report whether a visible `agent_text` reply appeared. The wake event itself is Channels-owned evidence; actual wake/claim/completion is still performed by Den Gateway + the target agent's Hermes Gateway session.

### 3. Fall back when the target is not a Channels member

If no active Channels membership exists, the ambassador should not pretend it woke the agent. It can still use Den Core/MCP to leave durable context:

- `mcp_den_send_message` on the relevant task/thread;
- `mcp_den_send_agent_stream_message` to a known agent/role/instance;
- task status/update where appropriate.

The reply should distinguish fallback from wake:

> I posted a Den task message for agent X, but X is not joined to the Den Channels project channel, so this did not create a Channels wake.

## Implementation placement

### Short-term green path

Create a Discord-only Hermes profile such as `den-ambassador` with:

- Discord platform enabled;
- Den MCP server enabled for Den Core state;
- either a small Hermes toolset or MCP facade for Den Channels HTTP calls;
- no Den Channels platform session required unless we later want the ambassador itself to appear in Den Channels.

For a phone-usable ambassador, prefer typed tools over asking the model to run `curl` through a shell. The minimal Den Channels tool surface should be:

- `den_channels_get_memberships(project_id?, channel_id?)`
- `den_channels_get_events(project_id?, channel_id?, after_id=0, limit=20)`
- `den_channels_get_message(message_id)`
- `den_channels_send_direct_agent_event(project_id/channel_id, member_identity, body, sender_identity='discord-ambassador')`

These can live in one of two places:

1. **Hermes Agent custom tool/plugin** for the ambassador profile.
   - Fastest to deploy for one profile.
   - Keeps Den Channels specifics out of Den Core.
2. **den-mcp facade tools that proxy Den Channels APIs.**
   - Better long-term if many agents need the same tool surface.
   - Should remain a facade/proxy; Den Channels continues to own channel data and policy.

Do not add Den Channels DB writes to Den Core/den-mcp.

### Later improvements

- Expose Gateway delivery request/attempt state in the same operator query so ambassador can answer `pending/claimed/delivered/failed` without inferring only from channel events.
- Add an explicit `agent activity projection` endpoint that combines membership, binding freshness, current working/idle, recent messages, and pending deliveries.
- Connect this with task #1450's broader operator status affordance spec.

## Safety and privacy rules

- Only send direct-agent events to active memberships in the requested project/channel.
- Do not infer cross-project targets; require explicit project/channel if ambiguous.
- Keep Den as source of truth; summarize in Discord but write durable operational context back to Den when it matters.
- Do not mirror private Discord content into Den Channels unless the user explicitly asks the ambassador to send that content.
- Preserve source ids/message ids/request ids in replies so actions are auditable.
- Redact secret-like values before sending anything into Discord or Den Channels.

## Minimal smoke test for a deployed ambassador

1. Ask in Discord: `status den-channels-runner in den-channels`.
2. Ambassador reads memberships and recent events, then replies with evidence.
3. Ask in Discord: `message den-channels-runner: please reply with a short ack in Den Channels`.
4. Ambassador creates a Delivery intent and reports the returned intent id and successor readback handles.
5. Verify a later Den Channels event contains an `agent_text` reply with `sourceKind=gateway_delivery`.

Passing this smoke proves the Discord bot can cross into Channels information and initiate a Den Channels wake without sharing the same Hermes conversation session.
