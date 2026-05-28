# ClientApp Retirement (task #1708)

The embedded Den Web Vite ClientApp (`src/DenChannels.Service/ClientApp`) was
retired in task #1708 and removed from this repo. Den Web (`den-web` repo) now
owns the canonical primary SPA at [http://192.168.1.10:18080/](http://192.168.1.10:18080/).

## What changed

- **`src/DenChannels.Service/ClientApp/`** — deleted. The Vite TypeScript SPA,
  React components, Vitest tests, ESLint config, and npm dependencies are gone.
- **`src/DenChannels.Service/wwwroot/`** — now serves only a minimal static
  moved-page (`index.html`) that links to Den Web. No bundled JS/CSS assets.
- **`DenChannels.Service.csproj`** — no longer has a `BuildDenWebClient` target.
  `dotnet publish` does not run npm or require `ClientApp/node_modules`.
- **`scripts/deploy-live-server.sh`** — no `preflight_workspace` check for
  `node_modules` ownership; no SPA build step; no `.html` publish artifact
  requirement. Smoke checks verify backend endpoints, not built assets.
- **`docs/ui-branch-hygiene.md`** — superseded by this doc. Frontend/hygiene
  conventions now live in the `den-web` repo.

## Where frontend work goes

All frontend product work now routes to the `den-web` repository:

- **Repo**: `den-web`
- **Live URL**: [http://192.168.1.10:18080/](http://192.168.1.10:18080/)
- **Service**: `den-web.service` (binds `0.0.0.0:18080`)
- **Build**: Vite + React + TypeScript (same stack, own repo)

## What stays in den-channels

This repo remains focused on:

- Backend APIs (ASP.NET Core, port 127.0.0.1:18081 behind the den-web reverse proxy)
- Channel CRUD, messages, memberships, reactions, read cursors
- Gateway membership/wake-policy endpoints
- Mirror summary ingestion
- Project-channel sync
- Agents overview API
- Den Core API proxy routes

## Reading the old doc

The original `docs/ui-branch-hygiene.md` file was about preventing disruption
while the SPA lived inside this repo. That concern no longer applies. If you
find references to it in git history, treat them as pre-retirement context.
