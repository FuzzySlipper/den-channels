# Den Channels

Standalone .NET service for Den channel data and channel-facing APIs.

Den Channels owns channel-specific, high-volume conversational/activity data (channels, messages, memberships, reactions, read cursors, mirror summaries, and future wake-policy inputs). Den core/den-mcp remains the source of truth for canonical workflow data such as projects, tasks, task messages, reviews, worker runs, documents, and identity/auth contracts.

## Current slice

This repo currently contains the service skeleton for Den task #1320:

- `src/DenChannels.Service` — ASP.NET Core service.
- `tests/DenChannels.Service.Tests` — smoke tests using `WebApplicationFactory`.
- `/health/live` — process liveness check.
- `/health/ready` — configuration readiness check.

The next Den tasks will add repository code and the basic channel/message HTTP API on top of the owned SQLite schema/migrations.

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
      "UseStubProjectMetadata": true
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
