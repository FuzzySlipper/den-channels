# Worker-pool channel discovery lifecycle

Den role workers use Channels membership state to move between the neutral worker pool and project-specific task work without hard-coding every target project channel in the Hermes adapter.

## Actors and channels

- `#worker-pool` is the neutral control/lobby channel for idle worker profiles. Its current live channel id is `604`.
- Target project channels, such as `agora-os` or `pi-crew`, receive temporary worker memberships while a worker is assigned to a task in that project.
- Worker profiles such as `spawned-coder`, `spawned-reviewer`, and `spawned-validator` poll direct-agent events for every active agent membership returned for their member identity.

## Membership purposes

Channel memberships use `membershipPurpose` to describe why the agent is present:

- `worker_pool_control` — the worker is idle or reachable through the neutral worker pool. This is the home/control membership and should remain active while the worker is part of the pool.
- `target_work` — the worker is temporarily resident in a project channel for an assigned task. Assignment code creates or refreshes this membership before waking the worker for task work.
- other or blank purposes — ordinary agent/channel membership. Runtime polling may include these only when explicitly configured or needed for compatibility.

## Discovery endpoint

Workers discover pollable channels with:

```http
GET /api/channel-memberships?memberIdentity=<agent>&membershipPurpose=<optional>&includeLeft=false
```

The endpoint returns only memberships for the requested `memberIdentity`, with channel metadata and sanitized membership labels. Runtime adapters should treat active memberships as the source of truth for the set of channels to poll.

Useful filters:

- `membershipPurpose=worker_pool_control` to find pool-control memberships.
- `membershipPurpose=target_work` to find active target-work project memberships.
- `projectId=<project>` or `channelId=<id>` for bounded diagnostics.
- `includeLeft=true` only for audit/debug views; runtime polling should normally exclude left memberships.
- `limit=<n>` to cap diagnostics or adapter polling discovery.

## Runtime lifecycle

1. **Idle** — the worker has an active `worker_pool_control` membership in `#worker-pool` and polls that channel for direct-agent wake events.
2. **Assignment prepare** — the orchestrator/assignment path adds or refreshes a `target_work` membership for the worker in the task's target project channel.
3. **Discovery** — the worker adapter calls `/api/channel-memberships?memberIdentity=<agent>` and resolves the active channel ids for both `worker_pool_control` and `target_work` memberships.
4. **Wake and poll** — the assignment wake is sent to the target project channel (or to `#worker-pool` as a controlled fallback). The worker polls all discovered active channels and claims the direct-agent event with the task/run/assignment metadata.
5. **Completion cleanup** — on terminal completion, failure, or abort, the orchestrator/assignment cleanup removes or marks the `target_work` membership left and restores the worker to neutral pool-control residency.
6. **Return to idle** — the next discovery pass no longer includes the old target project channel, so the worker resumes polling only its active control/assigned channels.

## Adapter override

Hermes Den Channels adapters still support explicit `poll_channel_ids` as an emergency override and bootstrap escape hatch. When configured, explicit `poll_channel_ids` take precedence over dynamic discovery. Use the override only for bounded repair or during deployment gaps; dynamic membership discovery is the preferred steady-state path.

## Operational checks

A healthy worker-pool discovery setup should satisfy these checks:

```bash
curl 'http://<channels-host>/api/channel-memberships?memberIdentity=spawned-coder'
curl 'http://<channels-host>/api/channel-memberships?memberIdentity=spawned-coder&membershipPurpose=target_work'
```

Expected results:

- idle workers show an active `worker_pool_control` membership for `#worker-pool`;
- assigned workers additionally show one or more active `target_work` memberships;
- completed workers no longer show stale `target_work` memberships for old tasks;
- response labels do not expose secrets from membership settings.

If the endpoint returns `404`, the Channels build serving the request has not been deployed with member-identity discovery yet. If it returns an empty list for an assigned worker, inspect the assignment/residency preparation path before changing adapter polling configuration.
