# Command lifecycle

Every viewer command has a `commandId` so feedback belongs to the exact click rather than whichever command of the same type completed last.

The lifecycle is:

1. `sent` — the viewer socket accepted the local send. No game state is changed optimistically.
2. `accepted` — the relay routed the command to the connected host and returns `command_status` with the same `commandId`.
3. `applied` — the game executed the command and returns `action_result` with `ok: true`, `phase: "applied"`, and the same `commandId`.
4. `failed` — either the relay could not reach a host or the game rejected execution. The failure carries the same `commandId`.

The viewer keeps an ID-keyed pending ledger and a five-second result timeout. A timeout clears pending presentation without changing the last authoritative pawn or economy payload. Older relays and hosts remain compatible because action-based result matching is retained as a fallback when `commandId` is absent.

Context-menu requests retain relay coalescing. If a queued request is replaced by a newer target, the superseded command receives a correlated failure instead of disappearing silently.

The release gate proves the full path with the production C# `RelayClient`, `JsonHelper`, and `StateProtocol`, plus browser assertions for the visible accepted and applied states.
