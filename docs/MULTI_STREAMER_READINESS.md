# Overlord — what it takes to be usable by other streamers

**Written:** 2026-08-23. Based on reading the actual tree, not memory.

## Verified current state

- Mod code is **already clean of personal values.** Grep across `Source/`, `Defs/`,
  `About/` found no hardcoded relay URL, secret, or channel name. `relayUrl` and
  `hostSecret` are empty-string defaults in `Source/Core/OverlordSettings.cs`.
  The only `broteam` literals are in `relay-server/scripts/*.js` (test fixtures)
  and one `DEFAULT_URL` in `verify-live-release.js` (a dev verify script).
- `docs/SELF_HOST.md` already exists and is placeholder-only.
- The relay is **hard single-tenant by construction**: `server.js:48` is a
  module-level `let hostSocket = null` and `server.js:51` a single global
  `const viewers = new Map()`. One process = one streamer. A second host with
  the same secret evicts the first (`server.js:887`).

So generalization of the *mod* is largely done. The gap is **onboarding**, not
hardcoding.

## The actual blocker: a 5-step setup with two developer accounts

To use Overlord today, a streamer must:

1. Install the mod (easy — Workshop).
2. **Register a Twitch developer application** and copy a Client ID.
3. **Deploy a Node server** (Fly / Docker / Railway) with two env vars.
4. Invent and match a `HOST_SECRET` in two places by hand.
5. Paste the relay URL back into mod settings.

Steps 2–4 are a developer workflow. Most RimWorld streamers will not complete
them. This is the single thing standing between "shipped" and "used by others."

## Ranked work

### 1. In-game setup feedback (highest value / lowest risk)
The settings UI (`Source/Core/OverlordMod.cs:93-118`) takes a relay URL and a
secret as bare text fields and reports **nothing** — no connection status, no
error. Verified: no `IsConnected`/`connectionStatus` reference exists in that
file. A streamer who typos the URL or mismatches the secret sees silence and
concludes the mod is broken.

- Add a status line: Connected / Bad secret / Unreachable / Local mode.
- Add a **"Generate secret"** button (no `Guid.NewGuid` exists in the mod today)
  so the secret is never hand-invented.
- Add a "Test connection" button hitting `/health`, which already reports
  `host`, `twitch`, and `clientBuild` (`server.js:696-705`).

This is pure additive UI, no hot-path cost, and it converts the most common
silent failure into a readable one.

### 2. One-click relay deploy
Templates exist (`fly.toml.example`, `railway.toml`, `Dockerfile`) but each
still requires a CLI and manual secret-setting. Best options, in order:

- **Railway/Render deploy button** in the README — a template URL that prompts
  for the two env vars in a web form. No CLI at all. `railway.toml` is already
  present, so this is mostly a README + template-repo task.
- **Guest mode is already supported** (`server.js:1394` warns and runs without
  `TWITCH_CLIENT_ID`). Worth documenting loudly: a streamer can skip the whole
  Twitch app registration for a first test run, at the cost of unauthenticated
  viewers. That removes step 2 from first-run entirely.

### 3. Hosting — the real decision
There are three models and they are not equally good:

| Model | Cost to Sean | Setup for streamer | Risk |
|---|---|---|---|
| **Self-host only** (today) | none | high — 5 steps, 2 accounts | adoption stays near zero |
| **Sean hosts shared multi-tenant** | server + bandwidth + support + moderation liability | near zero | requires rewriting the relay; Sean becomes an operator on call during others' streams |
| **Deploy-button self-host** | none | low — one form | best ratio |

**Recommendation: deploy-button self-host.** A shared relay would require
turning `hostSocket`/`viewers` into a per-room map keyed by streamer, adding
room routing, quotas, and abuse handling — a real rewrite of a live file — and
it makes Sean responsible for other people's broadcasts going down. The
video path is the expensive part: at the default `mapUpdateInterval=0.10`
each viewer gets ~10 JPEG frames/sec, so bandwidth scales with
streamers × viewers. That is a bad thing to volunteer to pay for.

The deploy button gets ~90% of the adoption benefit for ~5% of the work and
none of the ongoing liability.

## Not worth doing
- Removing `broteam` from `relay-server/scripts/*` — those are test fixtures,
  invisible to users.
- Multi-tenant relay, unless Sean actively wants to run a service.

## Open threads
- None of the above is implemented. This document is analysis only.
- If the deploy-button path is taken, the relay template likely wants its own
  small repo so the button target is stable.
