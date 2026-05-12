# Den Channels

Standalone .NET service for Den channel data and channel-facing APIs.

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

The script publishes `DenChannels.Service` for `linux-x64`, builds the bundled Den Web Vite app via the project publish target, uploads to `den-srv`, swaps `/data/services/den-channels/app` atomically through `/data/services/den-channels/app.new`, restarts `den-channels.service`, and smoke-tests:

- `/health/live`
- `/health/ready`
- `/`
- `/den-core-api/api/projects`
- `/api/not-a-route` as non-HTML 404

Useful overrides/options:

```bash
SSH_TARGET=den-srv scripts/deploy-live-server.sh --remote
scripts/deploy-live-server.sh --dry-run
scripts/deploy-live-server.sh --skip-restart
scripts/deploy-live-server.sh --skip-smoke
```

Live defaults are `/data/services/den-channels`, `den-channels.service`, and `http://192.168.1.10:18080`.

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
