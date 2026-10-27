# Security audit — 2026-10-27

**Scope:**
- The relay deployed as Fly app `overlord-relay` (`relay-server/`): server, viewer page, admin console and OBS overlay.
- The mod's network code (`Source/`), because it decides what a relayed message is allowed to do.
- The full git history of every branch and tag, since this repository is public.

**Audited commit:** `5c719ed` (master).

Line numbers in the **Where** column refer to that commit. The **Fix** column names the commit on this branch that fixes the finding.

**Deploy notes:**
- **Relay fixes** take effect only after the relay is redeployed to Fly. Nothing was deployed from this session.
- **Mod fixes (`Source/`)** need a new mod build and a Workshop release. Older DLLs on players' machines stay vulnerable to findings 1 and 13 until they update.
- **The mod was not compiled here.** It needs RimWorld's own DLLs. The changed C# files were parse-checked with Mono `mcs`, and the security-critical logic was compiled and unit-tested in isolation (see Tests).

## Summary

| # | Sev | Finding | Where | Fix |
|---|-----|---------|-------|-----|
| 1 | Critical | Mod's LAN server: any client can send admin commands | `Source/Networking/EmbeddedWebServer.cs:587` | `98290e3` |
| 2 | High | One expired session token crashes the relay | `relay-server/server.js:1182` | `a00ba74` |
| 3 | High | Viewer can fake `viewer_joined`/`viewer_left` for any name, and rename that viewer's colonist | `Source/Core/OverlordGameComponent.cs:303,379-380,470`; relay `server.js:1498` | `e34d9fc` (relay), `72d6807` (mod) |
| 4 | High | XSS in the admin console and OBS overlay, which leaks `HOST_SECRET` | `relay-server/public/admin.html:376,502,513,533,550,733`; `obs.html:124-166` | `fdec1c6` |
| 5 | High | XSS on the viewer page, which leaks viewers' session and Twitch tokens | `relay-server/public/app.js:4797,7293,2799,3181,3209` | `6c58f94` |
| 6 | High | Vulnerable `ws`/`body-parser`/`qs`/`proxy-addr`; the Docker build ignored the lockfile | `relay-server/package-lock.json`, `relay-server/Dockerfile:25` | `2051ec9` |
| 7 | Medium | Twitch tokens from any Twitch app accepted for login | `relay-server/server.js:1052` | `2cc7c5c` |
| 8 | Medium | Twitch login has no OAuth `state` (login CSRF) | `relay-server/public/app.js:1840,1793-1814` | `aa4cdf5` |
| 9 | Medium | Waiting viewers auto-moved into a stranger's room | `relay-server/public/app.js:1716` | `b7243ad` |
| 10 | Medium | Viewer websocket messages up to 4 MiB are forwarded to the game | `relay-server/server.js:1229` | `7d4bb0b` |
| 11 | Medium | No rate limit on `/auth/*`; no lockout on wrong secrets | `relay-server/server.js:1043,1082,1248,1572,2049` | `a97daef` |
| 12 | Medium | Host key / `HOST_SECRET` written to `Player.log` | `Source/Networking/RelayClient.cs:238` | `d4bb19f` |
| 13 | Medium | Mod's LAN websocket accepts connections from any website (no Origin check) | `Source/Networking/EmbeddedWebServer.cs:256` | `6a3c9fe` |
| 14 | Medium | Mod's LAN websocket buffers messages without limit | `Source/Networking/EmbeddedWebServer.cs:500` | `f87d24d` |
| 15 | Medium | Room slots can be exhausted permanently | `relay-server/server.js:1772,1964` | **Open** (needs Sean's decision) |
| 16 | Low | `Fly-Client-IP` trusted on any host, so rate limits can be bypassed | `relay-server/server.js:1972` | `a97daef` |
| 17 | Low | Admin secret compared with `===` (not constant-time) | `relay-server/server.js:1572,2049` | `9555b1f` |
| 18 | Low | No `nosniff` or framing protection; `X-Powered-By` sent | `relay-server/server.js` (Express setup) | `8aa0622` |
| 19 | Low | `map_transport: jpeg` dropped by a thrown TypeError (reliability) | `relay-server/server.js:1535` | `9740582` |
| 20 | Low | Credentials sent in URL query strings | `RelayClient.cs:231-235`, `admin.html:266`, `obs.html:56`, `server.js:1234-1237` | **Open** |
| 21 | Low | Twitch token kept in `localStorage` after exchange; broader scope than needed | `relay-server/public/app.js:1587,1834` | **Open** |
| 22 | Low | `/health` discloses pid, session count and config | `relay-server/server.js:988` | **Open** |
| 23 | Low | Mod sends the secret in clear text if the relay URL is `http://` | `Source/Networking/RelayClient.cs:309` | **Open** |
| 24 | Low | One person can fill `MAX_TOTAL_VIEWERS` in guest mode | `relay-server/server.js:1406` | **Open** (partly mitigated by `a97daef`) |
| 25 | Low | Viewer chat can carry Unity rich-text tags onto the streamer's HUD | `Source/Core/OverlordGameComponent.cs:799` | **Open, unverified** |
| 26 | Low | No Content-Security-Policy on the viewer page | `relay-server/public/index.html` | **Open** |
| 27 | Low | LAN static-file path check has no trailing separator | `Source/Networking/EmbeddedWebServer.cs` (`TryServeStaticAsset`) | **Open, unverified** |
| 28 | Info | Name-only login (guest mode, LAN mode) lets anyone take an offline viewer's name | `server.js:1103`, `EmbeddedWebServer.cs:548` | By design (documented) |
| — | — | Git history secret scan | all branches and tags | **Clean:** nothing to rotate or purge |

Findings 3 and 4 chain together, so the combined impact is higher than either alone. Any logged-in viewer could use #3 to set their own display name to HTML. The mod copies that name onto the colonist, and `admin.html:502` rendered colonist names unescaped. **The next time the operator opened `/admin`, the attacker's script would run and could read `HOST_SECRET` from `sessionStorage`.** This works without `OPEN_HOSTING`, so this chain is the most important thing in this audit to deploy.

## Findings

### 1. Critical: the mod's LAN server lets any client act as admin
`EmbeddedWebServer.ProcessWsMessage` (`EmbeddedWebServer.cs:587`) appended the trusted `username`/`source`/`adminCommand` keys after `json.TrimEnd('}')`. `TrimEnd` removes **every** trailing brace, not just the last one.

**Exploit:**
- **Payload:** a client on the streamer's local viewer server (LAN mode is the default when no relay URL is set) sends `{"type":"command","action":"ban","username":"victim","source":"admin","adminCommand":true,"z":{"a":1}}`.
- **What the host sees:** the trusted keys are appended inside `"z"`. The host's top-level reader then sees the client's own `adminCommand:true` and `source:"admin"`.
- **Result:** ban, timeout, spawn colonists, assign pawns, start votes and grant tickets, all under any name. The test harness confirms the old code granted admin to this exact, perfectly valid JSON.

**Fix:**
- The message must now be exactly one JSON object (`JsonHelper.IsSingleObject`).
- Only the single closing brace is replaced.
- `type`/`username`/`displayName` are read at the top level only.

### 2. High: an expired viewer session token crashes the relay
`resolveSession` called `clearViewerReplayCache(s.login, …)`. That function takes `(room, login, reason)`, so it threw a TypeError. On the websocket connection path nothing catches it, so the Node process exited. Fly restarts the machine, which drops every viewer and host.

**Exploit (reproduced locally; the process exited with code 1):**
- Mint a session (guest login, a Twitch login, or the admin API).
- Keep the token until it expires: 24h by default, or 1 min if minted through the admin API.
- Then connect within the next 30 minutes, before the periodic prune runs.
- Minting one token a minute gives a crash a minute.

Ordinary viewers returning with a stale token in `localStorage` trigger the same crash by accident.

**Fix:** call `clearViewerReplayCacheEverywhere`. Covered by `scripts/smoke-security.js`.

### 3. High: a viewer can impersonate or disconnect any viewer
The mod read `type` (`OverlordGameComponent.cs:303`) and the join/leave `username`/`displayName` (`:379-380`, `:470`) with `JsonHelper.ExtractString`, which returns the first match at **any** depth. The relay only checks the top-level type (`server.js:1498`).

**Exploit:**
- **Payload:** a viewer sends `{"0":{"type":"viewer_joined","username":"victim","displayName":"<img src=x onerror=…>"},"type":"chat"}`. The relay sees `chat`; the host sees `viewer_joined` for `victim`.
- **Impact:**
  - Create or overwrite any viewer session.
  - Rename the victim's colonist (`ViewerManager.cs:387`) to attacker text, which feeds finding 4.
  - Or send `viewer_left` to cut any viewer off.

**Fix:**
- **Relay (`e34d9fc`):** the relay now refuses any viewer message whose *nested* objects carry `type`, `username`, `displayName`, `source`, `adminCommand` or `login`. This protects every installed mod version as soon as the relay is deployed.
- **Mod (`72d6807`):** the mod now reads these fields at the top level only.

### 4. High: XSS in the admin console and OBS overlay leaks `HOST_SECRET`
`admin.html` keeps `HOST_SECRET` in `sessionStorage`; `obs.html` carries it in its URL. Both rendered host-sent data with `innerHTML` without escaping it.

**Vulnerable fields:**
- **admin.html:**
  - `game_info` season/day/hour/temperature (`:376`)
  - colonist names and ids (`:502`, `:513`)
  - vote counts (`:533`)
  - claim usernames inside inline `onclick` JavaScript (`:550`)
- **Escaping helper:** `esc()` (`:733`) did not escape quotes.
- **obs.html:** every name and vote string.

**Exploit paths:**
- **Hostile host:** with `OPEN_HOSTING`, any stranger can register a room and run a fake host. Once the operator opens `/admin?room=<id>` or the overlay for that room, the attacker's script reads the secret.
- **Any viewer, through the operator's own game:** see the chain under the summary table.

**Fix:**
- Every interpolated value is escaped, including quotes.
- Numbers are coerced to numbers.
- Click handlers moved to `data-*` attributes with delegated listeners.

### 5. High: XSS on the viewer page leaks viewer sessions and Twitch tokens
**Vulnerable sinks:**
- `handleGameInfo` (`app.js:4797`) wrote every `game_info` field with `innerHTML`.
- Vote counts (`:7293`) were unescaped.
- Cached item icons went raw into an `src` attribute (`:2799`).
- Dye colours could inject CSS such as `;background:url(…)` (`:3181`, `:3209`).

**Why it is High:** `game_info` is replayed to everyone who later joins the room, and finding 9 delivered other streamers' viewers into the attacker's room. A hostile host could therefore read `overlord_session` and `overlord_twitch_token` from the `localStorage` of other streamers' viewers.

**Fix:** values are escaped or validated (base64 icons, `#hex` colours, numeric votes).

### 6. High: vulnerable dependencies, and the Docker build ignored the lockfile
**Advisories reported by `npm audit --omit=dev`:**

| Package | Version | Advisories |
|---|---|---|
| `ws` | 8.20.0 | GHSA-58qx-3vcg-4xpx (memory disclosure), GHSA-96hv-2xvq-fx4p (fragment memory-exhaustion DoS); both High |
| `body-parser` | 1.20.4 | GHSA-v422-hmwv-36x6, GHSA-27r3-jxmh-p828 (DoS) |
| `proxy-addr` | 2.0.7 | GHSA-jqcg-44mw-7w3h; Critical, but not exploitable here because `trust proxy` is unset |
| `qs` | 6.14.2 | several DoS advisories |

**The Dockerfile** copied only `package.json` and ran `npm install`. Fly builds therefore ignored the lockfile and resolved fresh versions on every build, so what runs in production depends on the build date. **Unverified:** which `ws` version is live.

**Fix:**
- `ws` is now 8.22.0 and `express` 4.22.3, which bring patched `body-parser`/`qs`/`proxy-addr`. Both releases are more than a month old; `npm audit` now reports 0.
- The Dockerfile now copies the lockfile and runs `npm ci --omit=dev`. Verified with `npm ci` locally; no Docker daemon was available to build the image.

### 7. Medium: Twitch tokens issued to other apps were accepted
`/auth/twitch` trusted any token that `id.twitch.tv/oauth2/validate` accepted (`server.js:1052`), whatever app it was issued to.

**Exploit:** the operator of any other Twitch app a viewer has authorized (a bot, a game, an extension) replays that viewer's token and signs in as them. They can then control the viewer's colonist and spend their tickets.

**Fix:** require `client_id === TWITCH_CLIENT_ID`. The viewer page requests tokens with that same id, so legitimate logins are unaffected.

### 8. Medium: Twitch login had no OAuth `state` (login CSRF)
The implicit-grant flow sent no `state` and exchanged any `#access_token` it found in the URL (`app.js:1793-1814`, `:1840`).

**Exploit:** a link `https://<relay>/#access_token=<attacker's token>` signs the victim in as the attacker. Everything the victim then does happens on the attacker's account.

**Fix:**
- The login button stores a random `state` in `sessionStorage` and sends it to Twitch.
- On return, the token is used only if the returned `state` matches.
- Someone mid-login during the deploy will have to press login once more.

### 9. Medium: waiting viewers were auto-moved into another streamer's room
`startRoomWatch` (`app.js:1716`) runs after the viewer's game goes offline. It joined whatever single room was live.

**Exploit:**
- With `OPEN_HOSTING`, an attacker starts a room right as another streamer's game drops.
- That audience is moved into the attacker's room with no click; reproduced with Playwright.

**Fix:** a viewer who already has a room stays in it and is shown the list instead. A viewer with no room yet still auto-joins the only live game, as before.

**Residual risk:** someone opening the bare relay URL `/` while the attacker's room is the only live one still lands in it. That is the intended one-streamer behaviour, and with findings 4 and 5 fixed it shows text only.

### 10. Medium: oversized viewer messages were forwarded to the game
`maxPayload` is 4 MiB (`server.js:1229`), sized for host map frames, and it applied to viewers too.

**Exploit:** at the 20 msg/s viewer limit, one viewer could push about 80 MB/s through the relay into the streamer's game, each message parsed and logged on the way.

**Fix:** viewer messages over 16 KiB are dropped. Real ones are a few hundred bytes.

### 11. Medium: no rate limit on logins, no lockout on wrong secrets
**Logins:** `/auth/guest` and `/auth/twitch` had no limit. Each call creates a session that stays in memory and rewrites the sessions file synchronously, and each Twitch call makes two outbound requests. One client could grow memory and disk without bound.

**Secret guessing:** wrong `HOST_SECRET` / host key / admin bearer attempts were unlimited, so a weak operator secret could be guessed online.

**Fix:**
- **Logins:** 60 per IP per 10 minutes (HTTP 429 after that).
- **Failed credentials:** 20 failures per IP in 10 minutes locks that IP out of `/admin/*`, the admin and host websockets, and `/api/host/reclaim` for the rest of the window. Only failures count.
- **Side effect:** a game with a wrong secret that keeps reconnecting can lock out its own IP, including the operator's admin page from the same network, for up to 10 minutes.

### 12. Medium: the host credential was written to `Player.log`
`RelayClient` logged the full connect URL, including `?key=` or `?secret=`, on every reconnect (`RelayClient.cs:238`).

**Exposure:** streamers paste `Player.log` into bug reports, and `PlayerLogArchiver` keeps 30 copies.

**Fix:** log the relay URL and which kind of credential is used, never its value. **Action for Sean:** anyone who has shared a log should rotate their host secret or re-join for a new key.

### 13. Medium: the mod's LAN websocket accepted connections from any website
Browsers do not apply the same-origin policy to websockets. Any page the streamer had open could connect to `ws://localhost:<port>` (`EmbeddedWebServer.cs:256`). Before fix 1 that meant admin; after it, it means acting as any viewer.

**Fix:** the Origin must match the Host, and the Host must be one of:
- an IP address
- `localhost`
- a single-label machine name
- a `.local` name

This also blocks DNS rebinding. Non-browser clients (no Origin) are unaffected.

### 14. Medium: the mod's LAN websocket buffered messages without limit
`msgBuilder` grew until `EndOfMessage` (`EmbeddedWebServer.cs:500`). A client that never ends a message could exhaust the game's memory.

**Fix:** the socket is closed past 64K characters.

### 15. Medium (open): room slots can be exhausted permanently
A room that has hosted even once is never reaped (`server.js:1772`, by design so streamers' links never expire).

**Exploit:**
- With `OPEN_HOSTING` on (production, per `docs/HOSTING_GUIDE.md`; the live config is unverified), an attacker registers up to 30 rooms per IP per 10 minutes (`server.js:1964`).
- They connect a bare websocket to each room once.
- With a handful of IPs, all `MAX_ROOMS` (default 200) slots are permanent, and every new streamer gets "This relay is full".

**Not fixed:** this is a product decision. Options:
- reap hosted rooms after a long idle period (e.g. 30–90 days)
- cap rooms per IP
- set `HOST_INVITE_CODE`

### 16. Low: `Fly-Client-IP` trusted on any host
The register rate limit keyed on `Fly-Client-IP` (`server.js:1972`). Off Fly, that header is whatever the client sends, so the limit was trivially bypassed.

**Fix:** `clientIp()` trusts it only on Fly (`FLY_APP_NAME` set). Elsewhere it uses the last `X-Forwarded-For` hop, or else the socket address.

**Unverified:** whether Fly's edge overwrites a client-supplied `Fly-Client-IP`.

### 17. Low: admin secret compared with `===`
`===` returns at the first differing byte (`server.js:1572`, `:2049`). Remote timing attacks on this are impractical, but the fix is free.

**Fix:** use the existing `timingSafeEqualStr`.

### 18. Low: missing basic headers
**Fix:**
- `nosniff` on every response.
- `X-Powered-By` removed.
- `/admin` and `/obs` now send `X-Frame-Options: DENY`, `frame-ancestors 'none'` and `Referrer-Policy: no-referrer`.

### 19. Low: `map_transport` handler threw (reliability)
`clearViewerMapReplayCache(login, …)` was missing its `room` argument (`server.js:1535`). A viewer's choice of JPEG map mode therefore never reached the host.

**Fix:** pass the room.

### 20. Low (open): credentials in URL query strings
**Where credentials appear in URLs:**
- the host connects with `?secret=` / `?key=`
- the admin websocket and OBS overlay use `?secret=`
- viewers use `?session=`

**Exposure:** URLs end up in proxy and access logs and in browser history, and the overlay URL sits in OBS scene files. **Unverified:** whether Fly's edge logs query strings.

**Not fixed:** moving these into a header or a first message needs a coordinated mod and relay protocol change, and the shipped DLLs must keep working.

### 21. Low (open): Twitch token retained and over-scoped
The raw Twitch access token stays in `localStorage` for up to 3.5 h after it is exchanged (`app.js:1587`), and is used to recover expired sessions. It requests `user:read:email` (`app.js:1834`), but the relay only needs identity.

**Recommendation:** drop the stored token, or keep it in memory only, and request no scopes.

### 22. Low (open): `/health` discloses internals
`/health` (`server.js:988`) is public and returns the pid, machine id, session count and config flags.

**Recommendation:** trim it to `ok`/`host`. Check `release:verify` and the mod's connection test first, since they read fields from it.

### 23. Low (open): clear-text secret over `http://`
If a streamer types an `http://` relay URL, the mod connects with `ws://` and sends the secret unencrypted (`RelayClient.cs:309`).

**Recommendation:** refuse plain-text URLs unless the host is local.

### 24. Low (open): one person can fill the relay in guest mode
Guest mode mints unlimited names, so one person can fill `MAX_TOTAL_VIEWERS` (`server.js:1406`). It is now bounded to 60 logins per IP per 10 minutes.

**Recommendation:** cap viewer sockets per IP.

### 25. Low (open, unverified): rich text in chat
`HandleChat` (`OverlordGameComponent.cs:799`) strips control characters but not Unity rich-text tags such as `<size=200>`. Viewers may be able to deface the streamer's HUD. Not checked in-game.

### 26. Low (open): no CSP on the viewer page
A Content-Security-Policy would be defence in depth behind finding 5. Adding one first needs the remaining inline handlers (e.g. `onclick="castVote()"`) moved out.

### 27. Low (open, unverified): LAN static-file path check
The static-file prefix check has no trailing separator, so a sibling folder such as `WebUI2` would pass. No working traversal was found.

### 28. Info: name-only login
These modes let anyone take any name that is not currently connected, including its colonist:
- relay guest mode (`server.js:1103`)
- the mod's LAN mode (`EmbeddedWebServer.cs:548`)

This is documented and intended (`render.yaml`; guest mode switches off when `TWITCH_CLIENT_ID` is set). LAN mode also listens on all interfaces when it can (`EmbeddedWebServer.cs:81`), which is intended for friends on the same network.

## Secrets and git history

**Method:**
- Unshallowed the clone and fetched every ref: `master`, `feat/bandwidth-and-ux`, tags `v0.1.0` to `v0.2.2`. That is 165 commits.
- Ran gitleaks 8.21.2 over all of them: **no findings**.
- Gitleaks' rules miss custom secrets, so I also grepped every blob of every revision for:
  - assignments to `HOST_SECRET`, `ADMIN_*`, `TWITCH_CLIENT_*`, `HOST_INVITE_CODE`, `*SECRET`, `*TOKEN`, `*KEY`
  - non-empty C# defaults for `hostSecret`/`hostKey`
  - non-placeholder `process.env.X || '...'` fallbacks
  - hostnames on fly.dev, onrender, railway, ngrok, workers.dev and vercel.app
  - email addresses
  - IP addresses
  - private-key headers

**Result: no secret value was ever committed.** Nothing needs rotating because of the history, and **no history purge is needed.**

**What was found:**
- **Placeholders only:** `choose-a-long-random-string`, `YOUR-…` and random per-run values in the smoke scripts.
- **Not secrets:** `localhost`, a documentation LAN IP and a public DNS IP.
- **`overlord-relay.fly.dev` (production hostname):** in 142 revisions, and intentionally in HEAD as `OfficialRelayUrl` (`Source/Core/OverlordSettings.cs:7`).
- **Contact email in `README.md`:** intentional.
- **Docs removed from the tree but still in history:**
  - removed in `8149fe3`: `docs/PLAN.md`, `docs/PUBLIC_CHECKLIST.md`, `docs/RIMWORLD_16_API.md` and others
  - removed in `4f4d5a6`: `docs/HOSTING_AND_FRIENDS_MODE.md`, `docs/MULTI_STREAMER_READINESS.md`
  - I reviewed them: they hold internal notes and the already-public hostname, but no credentials.
- **Never committed:** a `.env` or `fly.toml`.
- **Not checked:** `PROJECT_NORTH_STAR.md` mentions a private `archive/pre-public-history` branch. It is not on the public remote. Commits from a branch that was pushed and later deleted can still be served by GitHub by SHA; I cannot enumerate those.

**Other config checks:**
- **CORS:** no CORS headers, so same-origin only.
- **Default credentials:** none. `HOST_SECRET` has no default, and the admin API and admin socket fail closed.
- **Admin routes:** behind a bearer secret, not an obscure URL.
- **Debug/logging:** `LOG_TRAFFIC` is on by default and logs viewer logins and registering IPs. That is operational logging, not debug output.

## Tests

**Existing suite** (every `relay-server/scripts/smoke-*.js`, `multi-tenant-acceptance.js`, and `node --check` on `server.js` and `app.js`):

| | Pass | Fail |
|---|---|---|
| Before (`5c719ed`) | 9 | 3 |
| After | 9 | 3 |

The same three failed both times, before any change:
- `smoke-host-transport` needs `dotnet`, which is not installed in this sandbox.
- `smoke-social-roster` and `smoke-preferred-weapon` time out waiting for UI elements.

**New tests:**
- **`relay-server/scripts/smoke-security.js`** (`npm run smoke:security`, also added to the release gate): 19/19 pass.
  - **Coverage:**
    - expired-session crash
    - nested-key spoofing
    - oversized messages
    - admin, overlay and viewer XSS (Playwright)
    - room hijack
    - OAuth `state`
    - login rate limit
    - credential lockout
    - forged `Fly-Client-IP`
    - headers
  - **Validation:** each check was confirmed to fail against the unfixed code where that was practical.
- **`tools/security-tests/*.cs`** (Mono, no RimWorld needed): 17/17 JSON/annotation checks and 11/11 Origin checks pass.
- **`npm audit --omit=dev`:** 0 vulnerabilities.

## New environment variables

None. `FLY_APP_NAME` is read, but Fly sets it automatically.
