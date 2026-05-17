# Den Channels durable Gateway green path runbook

Status: current after Den tasks #1495, #1498, #1499, and #1496.

## Current production path

Den Channels agent conversations use the long-running Hermes Gateway service for the runner profile:

```text
Den Web / Den Desktop
  -> Den Channels API
  -> Den Gateway delivery request
  -> hermes-gateway@den-channels-runner.service
  -> Hermes Gateway den_channels adapter
  -> GatewayRunner / SessionStore
  -> Den Channels visible agent_text reply
```

The durable service is:

```bash
systemctl --user status hermes-gateway@den-channels-runner.service
```

Expected state is `active` and `enabled`.

## Message/reply contract

Visible final replies from the native adapter use:

- `messageKind=agent_text`
- `sourceKind=gateway_delivery`
- `sourceId=<delivery_request_id>`
- `dedupeKey=gateway-delivery:{delivery_request_id}:final`

`external_adapter_message` is only for true external adapter ingress or temporary compatibility. It is not the first-party Den Gateway / Hermes Gateway reply kind.

## Session behavior

- Den Channels project/channel lanes are shared sessions (`group_sessions_per_user=False`).
- A stable lane reuses the same Hermes Gateway session until `/new` is approved for that lane.
- `/new` is lane-scoped: it should rotate only that lane's session id and leave other gateway bindings/sessions intact.

## Legacy path status

The previous heartbeat/one-shot wake path is retired. The installed user units and helper script were archived by task #1496 to:

```text
/home/agents/runtime/legacy-den-channels-cleanup-1496/20260517T075837Z/
```

Archived files:

- `den-gateway-hermes-consumer.service`
- `hermes-den-channels-heartbeat@.service`
- `hermes-den-channels-heartbeat@.timer`
- `den_hermes_profile_binding_heartbeat.py`

Do not enable these as a current Den Channels path. If temporarily restored for historical debugging, label them legacy/test-only, keep them disabled by default, and remove/disable them again before closing the maintenance window.

## Fast verification checklist

1. Check Den Channels readiness:

   ```bash
   curl -fsS http://192.168.1.10:18080/health/ready
   curl -fsS http://192.168.1.10:18080/api/gateway/health
   ```

2. Check the durable gateway service:

   ```bash
   systemctl --user is-active hermes-gateway@den-channels-runner.service
   systemctl --user is-enabled hermes-gateway@den-channels-runner.service
   ```

3. Check there are no installed legacy unit files:

   ```bash
   systemctl --user list-unit-files '*den-gateway-hermes-consumer*' '*hermes-den-channels-heartbeat*' --no-pager
   ```

   Expected result after #1496 cleanup: `0 unit files listed`.

4. Check there is a fresh active Gateway binding for:

   - `adapter_kind=hermes_profile`
   - `adapter_instance_id` ending in `:gateway`
   - `agent_identity=den-channels-runner`
   - `project_id=den-channels`
   - `role=runner`
   - `status=active`

   The running Hermes platform adapter is `den_channels`; the Gateway binding kind remains the Hermes-profile transport kind.

5. Smoke a real Den Channels message and verify:

   - Gateway creates a delivery request.
   - The native adapter claims/completes it.
   - Den Channels shows a visible `agent_text` reply with `sourceKind=gateway_delivery`.
   - Same-lane continuity works on a second message.
   - `/new` rotates only the lane's session id.
