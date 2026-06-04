# Den Channels

Standalone .NET service for Den channel data and channel-facing backend APIs.

**Primary frontend**: Den Web at [http://192.168.1.10:18080/](http://192.168.1.10:18080/) (`den-web` repo).
The embedded Channel Chat ClientApp was retired in task #1708. All frontend product
work now lives in `den-web`. This repo owns channel backend APIs, membership,
messages, and activity state — not the public SPA.

Den Channels owns channel-specific, high-volume conversational/activity data (channels, messages, memberships, reactions, read cursors, mirror summaries, and future wake-policy inputs). Den core/den-mcp remains the source of truth for canonical workflow data such as projects, tasks, task messages, reviews, worker runs, documents, and identity/auth contracts.

## Current slice

This repo currently contains the service skeleton for Den task #1320:

- `src/DenChannels.Service` — ASP.NET Core service.
- `tests/DenChannels.Service.Tests` — smoke tests using `WebApplicationFactory`.
- `/health/live` — process liveness check.
- `/health/ready` — configuration readiness check.
- `/api/channels` — create/list channels.
- `/api/channels/{id}` — get a channel.
- `/api/projects/{projectId}/default-channel` — idempotently ensure the safe-slug default project channel.
- `/api/channels/{channelId}/messages` — post/list channel messages with source pointers and cursor params.
- `/api/channels/{channelId}/memberships` — minimal membership upsert.
- `/api/channel-messages/{messageId}/reactions` — idempotent reaction add.
- `/api/channels/{channelId}/activity-events` — append/query non-waking agent/tool-call breadcrumbs.
- `/api/channel-activity-events` and `/api/channel-activity-events/status` — Channels-owned Gateway-shaped breadcrumb compatibility writer plus recent failure diagnostics; new callers should prefer the per-channel route.
- `/api/gateway/memberships?channelId={id}|projectId={projectId}` — Gateway-facing participant/wake-policy snapshot for channel routing.
- `/api/gateway/test-wakes` — controlled synthetic wake-event recorder for an active agent membership; it records Channels-owned evidence only and returns Gateway message/events URLs for downstream delivery/claim/complete/fail follow-up.
- `/api/gateway/direct-agent-messages` — active-member direct-agent wake/message recorder with optional bounded Gateway claim/ack wait (`waitFor`, `timeoutMs`) and explicit delivery status handles. See `docs/direct-agent-delivery-status.md`.

The Den Web channel chat panel (in the `den-web` repo) exposes the same boundary:
it lists project/space channels, shows channel participants/active agent bindings, lets a tester
join an agent membership with a bounded wake policy, posts direct agent-targeted channel messages,
and records low-risk test wakes through the Gateway API. Channels stores the message/membership
rows; Gateway/bridge consumers remain responsible for real transport, delivery state, claims,
completions, failures, and suppression decisions.

- `/api/project-channel-sync/projects/{projectId}` — ensure one project default channel from Den core/stub metadata.
- `/api/project-channel-sync` — backfill default channels from Den core/stub project list or explicit project payload.
- `/api/mirror-summaries/ingest` — ingest explicit Den event payloads into idempotent channel mirror summaries.

Den core outbox polling still depends on den-mcp task #1341; until then mirror ingestion accepts explicit event payloads and keeps source pointers/deep links instead of copying canonical Den records.

## Configuration

Configuration lives under the `DenChannels` section.

```json
{
  "DenChannels": {
    "Database": {
      "Path": "data/den-channels.db",
      "ApplyMigrationsOnStartup": true
    },
    "DenCore": {
      "BaseUrl": "http://127.0.0.1:5199",
      "UseStubProjectMetadata": true,
      "StubProjects": []
    },
    "ServiceAuth": {
      "ServiceToken": null
    }
  }
}
```

Environment variable equivalents use double underscores, for example:

```bash
DenChannels__Database__Path=/var/lib/den-channels/den-channels.db
DenChannels__Database__ApplyMigrationsOnStartup=true
DenChannels__DenCore__BaseUrl=http://127.0.0.1:5199
DenChannels__DenCore__UseStubProjectMetadata=true
DenChannels__ServiceAuth__ServiceToken=...
```

`UseStubProjectMetadata=true` is intentional for the first standalone slices while Den core/den-mcp integration contracts are requested and implemented separately. The initial Den core integration request is tracked in den-mcp task #1341.

## Build and test

```bash
dotnet restore DenChannels.slnx
dotnet build DenChannels.slnx
dotnet test DenChannels.slnx
```

## Deploy live on den-srv

Use the deploy helper from this repo root:

```bash
scripts/deploy-live-server.sh --remote
```

The script publishes `DenChannels.Service` for `linux-x64`, uploads to `den-srv`,
swaps `/data/services/den-channels/app` atomically through `/data/services/den-channels/app.new`,
restarts `den-channels.service`, and smoke-tests the backend on its internal URL:

- `/health/live`
- `/health/ready`
- `/` (moved-page referencing Den Web)
- `/den-core-api/api/projects`
- `/api/not-a-route` as non-HTML 404

The default `SMOKE_BASE_URL=http://127.0.0.1:18081` targets the den-channels backend
directly on the loopback interface. For smoke through the den-web reverse proxy,
set `SMOKE_BASE_URL=http://192.168.1.10:18080`. Den Web (den-web.service) owns the
public 0.0.0.0:18080 listener; this script does not rebuild or deploy the frontend.

Useful overrides/options:

```bash
SSH_TARGET=den-srv scripts/deploy-live-server.sh --remote
scripts/deploy-live-server.sh --dry-run
scripts/deploy-live-server.sh --skip-restart
scripts/deploy-live-server.sh --skip-smoke
```

Live defaults are `/data/services/den-channels`, `den-channels.service`, and
`SMOKE_BASE_URL=http://127.0.0.1:18081` (pointing at the backend, not the public URL).

## Run locally

```bash
dotnet run --project src/DenChannels.Service
```

Then check:

```bash
curl http://127.0.0.1:5000/health/live
curl http://127.0.0.1:5000/health/ready
```

If Kestrel chooses a different development URL, use the URL printed by `dotnet run`.

## Boundary rules

- Do not depend on being inside the `den-mcp` repo.
- Do not write channel rows into the Den core database.
- Consume Den core through explicit HTTP/event contracts.
- When a new Den core capability is needed, create a task in the `den-mcp` Den project instead of editing that repo from here.
