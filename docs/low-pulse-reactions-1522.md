# Low-pulse Den Channels reactions

Task: #1522

Den Channels reactions are the preferred low-noise acknowledgement surface when a human or agent wants to signal receipt without creating another channel message.

## Guidance for profiles / SOUL

> In Den Channels, if you are woken and have nothing useful to add, prefer reacting to the triggering message (for example ✅ or 👀) instead of posting a new text reply.

Reactions are optional. Do not force every wake to produce a reaction; use them when an acknowledgement is helpful and a text reply would only add pulse/noise.

## No-pulse contract

- Reactions write `channel_reactions` rows only.
- Reactions do **not** insert `channel_messages` rows.
- Gateway channel event polling continues to expose message events only, so reactions do not trigger `all_messages_except_self`, `all_human_messages`, or mention-policy wake fan-out.
- If Gateway observes reactions later, they should use a distinct event kind with default `record_only` semantics.

## UI contract

The Den Channels chat panel displays compact reaction pills below each message and offers quick reactions:

- ✅ acknowledgement / done
- 👀 seen / looking
- 👍 approval
- 🫡 acknowledged / on it
- ❓ question / needs clarification

The quick reaction buttons are intentionally small and inline to preserve scrollback readability.

## Agent adapter contract

The Den-owned Hermes Den Channels adapter exposes a bounded `react_to_message(message_id, reaction_key)` method. It requires an explicit Den Channels message id and reaction key, records the reactor as the agent profile identity, and does not post a text reply.

## Tests

Coverage should include:

- idempotent reaction insert by `(message, reactor_type, reactor_identity, reaction_key)`;
- reaction summary listing for UI display;
- Gateway event regression proving reactions do not create wake-pulse events even with `all_messages_except_self` members;
- Hermes adapter fake test proving an agent reaction posts no channel text reply.
