# Overlord release gate

Run from `relay-server`.

- `npm run release:gate` builds the mod, syntax-checks the relay/viewer harness, runs `git diff --check`, starts a fresh local relay, and executes the deterministic viewer smoke at desktop, compact, and narrow browser sizes. It also covers claim/assignment, Armory search and Equip, one reconnect/replay, command availability, and screenshot generation.
- `npm run release:verify` performs a read-only production check. It requires `/health.clientBuild`, the served `app.js` build marker, and `Cache-Control: no-store` to match the local viewer build.
- `npm run release:fly` runs the local gate, synchronizes both installed RimWorld mod copies, performs the Fly rolling deploy, and waits for the production build checks to match.

The gate intentionally uses a generated local host secret and admin-created smoke viewer session. It does not depend on Twitch login, a public queue, a running RimWorld process, or production player data.

Known separate suites remain available as `smoke:tilemap` and `smoke:relay-cache`; they are not silently treated as passing by this viewer-release gate.
