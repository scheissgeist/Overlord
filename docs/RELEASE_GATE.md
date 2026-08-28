# Overlord release gate

Run from `relay-server`.

- `npm run release:gate` builds the mod, syntax-checks the relay/viewer harness, runs `git diff --check`, starts fresh local relays, and executes both the deterministic viewer smoke and a clean-profile host-to-viewer transport smoke. The host smoke compiles and runs the production C# `RelayClient`, `JsonHelper`, and `StateProtocol`; proves a new room can carry host capabilities and pawn state to a new viewer; and proves a forged viewer identity is pinned before the real host receives its command. The browser smoke covers desktop, compact, and narrow sizes, claim/assignment, Armory search and Equip, reconnect/replay, command availability, and screenshot generation.
- `npm run release:verify` performs a read-only production check. It requires `/health.clientBuild`, the served `app.js` build marker, and `Cache-Control: no-store` to match the local viewer build.
- `npm run release:fly` runs the local gate, synchronizes both installed RimWorld mod copies, performs the Fly rolling deploy, and waits for the production build checks to match.

The gate intentionally uses generated local credentials and disposable viewer sessions. It does not depend on Twitch login, a public queue, a running RimWorld process, or production player data. RimWorld simulation behavior still requires an in-game check; this gate covers the production mod transport and message serializer without mocking them in JavaScript.

Known separate suites remain available as `smoke:tilemap` and `smoke:relay-cache`; they are not silently treated as passing by this viewer-release gate.
