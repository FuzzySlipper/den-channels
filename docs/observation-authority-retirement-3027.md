# Observation Authority Retirement 3027

Task #3027 retires den-channels observation, lifecycle, trace, and activity authority after successor parity and caller migration were completed by the linked unblockers.

## Runtime switch

Production config enables:

```json
"LegacyObservation": {
  "TombstoneRoutes": true
}
```

Development config leaves the switch false so historical-read tests and local debugging can still exercise the legacy SQLite projections.

## Retired routes

When `DenChannels:LegacyObservation:TombstoneRoutes=true`, these routes return `410 Gone` with `code=route_gone` and a successor `replacement`:

- `POST /api/channels/{channelId}/activity-events` -> `POST /v1/observation/activity-events`
- `GET /api/channels/{channelId}/activity-events` -> `GET /v1/observation/activity-events`
- `PATCH /api/channel-activity-events/{activityEventId}` -> `POST /v1/observation/activity-events`
- `POST /api/channel-activity-events` -> `POST /v1/observation/activity-events`
- `GET /api/channel-activity-events/status` -> `GET /v1/observation/activity-events/status`
- `POST /api/agent-work/lifecycle-events` -> `POST /v1/observation/lifecycle-events`
- `GET /api/agent-work/events` -> `GET /v1/observation/activity-events`
- `GET /api/agent-work/current` -> `GET /v1/observation/active-work`
- `GET /api/agents/overview` -> `GET /v1/observation/agents/overview`
- `GET /api/agents/{agentIdentity}/overview` -> `GET /v1/observation/agents/{id}/overview`
- `GET /api/assignments/{assignmentId}/trace` -> `GET /v1/observation/assignments/{id}/trace`
- `GET /api/gateway/assignments/{assignmentId}/trace` -> `GET /v1/observation/assignments/{id}/trace`
- `GET /api/assignments/{assignmentId}/transcript` -> `GET /v1/observation/assignments/{id}/transcript`

Gateway health omits the retired observation compatibility entries when the switch is enabled.

## Safety boundary

No `channel_activity_events`, `channel_messages`, or history rows are dropped. This change removes den-channels route authority only. Historical data remains available to migration/import tooling and direct database backups.

Successor ownership:

- Observation owns activity/lifecycle readback and display-only agent/work projections.
- Conversation owns transcript rows.
- Delivery/Runtime/Core own executable assignment and work state.
- Timeline remains a read composition surface.
