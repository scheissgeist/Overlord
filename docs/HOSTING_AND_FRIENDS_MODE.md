# Overlord — repo exposure, friends mode, and who pays for hosting

**Written:** 2026-08-23. Every claim below was verified against the tree or a
live probe this session. Supersedes the "guest mode" claim in
MULTI_STREAMER_READINESS.md, which was wrong — see correction below.

---

## 1. The repo is ALREADY public

`origin` = https://github.com/scheissgeist/Overlord, `origin/master` exists with
111 files. This is not a "before we go live" question; it is an audit of what is
already out there.

### Verified clean
`.gitignore` is doing its job. Confirmed NOT in `origin/master`:
`AGENTS.md`, `docs/SESSION_LOG_*`, `docs/HANDOFF_*`, `docs/TASK_*`,
`docs/AI_CHANNEL.md`, `relay-server/fly.toml`, any `.env`.
`git ls-files` on the private-doc patterns returns only `fly.toml.example`.

Greps for `client_secret`, `oauth:<token>`, `api_key=`, `password=` over the
published tree: **no hits.** No credential is exposed.

### The one real finding: your live relay hostname is published

`relay-server/scripts/verify-live-release.js:8`

```js
const DEFAULT_URL = 'https://overlord-relay.fly.dev/';
```

Verified this is yours (`relay-server/fly.toml` local: `app = 'overlord-relay'`)
and that it is live — `curl https://overlord-relay.fly.dev/health` returned
`{"ok":true,...,"host":false,"twitch":true}`, uptime ~1.1M s.

This is **not** a credential leak. `HOST_SECRET` still gates the host slot
(`server.js:878`), the admin API fails closed (`server.js:1233-1241`), and
`MAX_VIEWERS` caps viewers. The risk is narrower but real:

- It is a public pointer at a machine **you pay for**. Anyone can open the
  viewer UI and sit on a session slot, or point traffic at it.
- If a streamer copies that URL into their mod settings (the obvious mistake —
  it is the only concrete URL in the repo), they land on **your relay and your
  bandwidth bill**, and they will silently fail to get a host slot without your
  secret.

**Fix:** make the default a placeholder and require the URL as an argument.
Low risk — it is a dev verify script, not shipped code.

### Cosmetic, leave alone
`BroTeamPill` in `About.xml`/`LICENSE`/Harmony ID (that is authorship),
`broteampill@gmail.com` in README (that is your published contact channel), and
`broteam` in `relay-server/scripts/*` test fixtures (invisible to users).

---

## 2. CKchaos's question: friends without Twitch — ALREADY BUILT

> "is there a way to use this for just friends without using twitch? because i
> wanna just play it with friends without needing to be streaming"

**Yes. It ships today and needs no code.** This is the mod's *local mode*, and
the whole path is verified present:

- Leaving **Relay Server URL blank** in mod settings starts the embedded server
  instead of a relay (`Source/Core/OverlordGameComponent.cs:98-114`), on
  `localPort` (default **8421**).
- The embedded server injects `data-embedded-mode="true"` into the served page
  (`Source/Networking/EmbeddedWebServer.cs:349-369`).
- The viewer UI reads that flag (`public/app.js:14`) and switches the login
  screen from the Twitch button to a **plain username box**
  (`app.js:1407-1413`, `index.html:28-29` `#local-login`), storing a local
  session instead of an OAuth one.
- It binds `http://+:{port}/` (all interfaces) and falls back to
  `localhost`-only if not elevated (`EmbeddedWebServer.cs:78-88`).

So friends open `http://<your-LAN-IP>:8421`, type any name, and claim a
colonist. No Twitch app, no relay, no streaming, no `HOST_SECRET`.

**The two caveats that decide whether it works for CKchaos:**
1. **LAN vs internet.** All-interfaces binding covers same-network friends. For
   remote friends he needs a port-forward or a tunnel (Tailscale / `cloudflared`
   / ngrok). That is the only genuine friction.
2. **The `http://+:` prefix usually needs admin** on Windows, or it silently
   degrades to localhost-only — the log line says which one happened. This is
   the likely "it doesn't work" report.

### CORRECTION to the previous session's answer
I previously said guest mode was reachable by omitting `TWITCH_CLIENT_ID` on the
relay. **That is wrong.** On the relay path, no client ID means `/auth/twitch`
returns 503 (`server.js:721-723`) and the UI shows "Twitch auth is not
configured" (`app.js:1452-1456`) — there is no anonymous relay login. The
`console.warn` at `server.js:1394` that says "running in guest mode" is a
**misleading log message**, not a working feature. The real no-Twitch path is
embedded/local mode, which is a different code path entirely. Worth fixing that
warning string so it stops implying a mode that does not exist.

**Action: this is a documentation gap, not a feature gap.** Neither `README.md`
nor `docs/SELF_HOST.md` explains local/friends mode as a first-class way to
play. Answer CKchaos directly, and add a "Play with friends (no Twitch, no
stream)" section.

---

## 3. Steam Workshop — how is it doing for others?

**Unknown, and I am not going to guess.** Nothing in this repo records installs,
ratings, or comments. `data/workshop_stats.csv` is gitignored and I have not
read it. The screenshot in this conversation is the only user feedback I have
seen, and one comment is not a usage signal.

To actually answer: open the Workshop page (id `3760983440`) for subscriber
count, favorites, and the comment thread. That is the authority; this repo is not.

---

## 4. How others host it — and how you never pay for it

The relay is **hard single-tenant by construction**: `server.js:48` is a
module-level `let hostSocket = null` and `server.js:51` a single global
`const viewers = new Map()`. One process serves exactly one streamer; a second
host with the same secret evicts the first (`server.js:887`).

That constraint is the answer to the billing question. There are three models:

| Model | Who pays | Setup for them | Verdict |
|---|---|---|---|
| **Local/embedded** | nobody | blank URL + port-forward | **already works** — best for friends |
| **Deploy-button self-host** | them, own account | one web form | **best for streamers** |
| **You host shared** | **you**, forever | none | **do not** |

### Why not to host a shared relay
1. It does not exist — one global `hostSocket` means a rewrite into per-room
   routing, quotas, and abuse handling, on a live file.
2. Video dominates the cost. At default `mapUpdateInterval=0.10`
   (`OverlordSettings.cs`), each viewer pulls **~10 JPEG frames/sec**, so
   bandwidth scales with streamers × viewers. That is an unbounded bill you
   would be volunteering for.
3. You become the on-call operator for other people's broadcasts, plus whatever
   their viewers do through your box.

### The rule that keeps you off the hook
**Every streamer deploys under their own account.** The deploy-button flow
(Railway/Render template + the existing `railway.toml` / `Dockerfile`) makes
them enter their own credentials, so the bill is theirs by construction — you
cannot pay for it even by accident. Fly's free allowance covers a small relay,
so for most streamers this is $0 anyway.

Concretely, three things:
1. Placeholder the `DEFAULT_URL` in `verify-live-release.js` so your relay stops
   being the one copyable URL in the repo.
2. Add a deploy button to `README.md` pointing at a template, not at your app.
3. State it plainly in `README.md` / `SELF_HOST.md`: *there is no shared public
   server; run your own, or play locally with friends.* `SELF_HOST.md` already
   says a version of this — the README should too, up top.

---

## Open threads
- Nothing here is implemented. Analysis and audit only.
- Workshop reception genuinely unmeasured (§3).
- The `server.js:1394` "guest mode" warning is misleading and should be reworded.
