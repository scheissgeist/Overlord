# Overlord — Self-host guide

Run your own Overlord stack. This guide uses placeholders only. Replace every `YOUR-*` value with yours. Do not point your game at another streamer’s relay.

---

## What you need

| Piece | Role |
|-------|------|
| RimWorld 1.5 or 1.6 | Host game |
| [Harmony](https://github.com/pardeike/HarmonyRimWorld) | Mod dependency |
| Overlord mod build | From this repo (`build.bat` / Release DLL) |
| Node 18+ (or Docker / Fly) | Relay process |
| Twitch developer application | Viewer login |

Optional: Twitch Toolkit + ToolkitUtils on the host if you want Overlord **Buy** / **Story** purchases.

---

## 1. Install the mod

1. Build or obtain `Overlord.dll` (`build.bat` on Windows copies into Steam Mods when possible).
2. Ensure the mod folder contains at least:
   - `About/`
   - `Assemblies/Overlord.dll`
   - `Defs/` (if present in the repo)
   - `WebUI/` (copy of `relay-server/public/` — `build.bat` does this for embedded/local UI)
3. Enable **Harmony**, then **Overlord**, in the RimWorld mod list.
4. Restart RimWorld after any DLL replace.

---

## 2. Create a Twitch application

1. Open the [Twitch Developer Console](https://dev.twitch.tv/console/apps).
2. Register an application.
3. Set **OAuth Redirect URL** to your relay’s public origin, for example:
   - Local: `http://localhost:8080/`
   - Hosted: `https://YOUR-RELAY-HOST/`
4. Copy the **Client ID** (public). Keep any Client Secret out of the repo and out of the browser; Overlord’s relay uses the Client ID for the implicit browser login flow.

Viewers will log in on *your* relay URL using this app.

---

## 3. Configure and run the relay

Environment variables (set in your shell, Docker, or host platform — never commit them):

| Variable | Required | Meaning |
|----------|----------|---------|
| `HOST_SECRET` | Strongly recommended | Shared secret; must match Overlord mod settings |
| `TWITCH_CLIENT_ID` | Yes for Twitch login | From your Twitch app |
| `PORT` | No | Default `8080` |
| `MAX_VIEWERS` | No | Default `50` |

### Local

```bash
cd relay-server
npm install
export HOST_SECRET='YOUR-LONG-RANDOM-SECRET'    # Windows: set HOST_SECRET=...
export TWITCH_CLIENT_ID='YOUR-TWITCH-CLIENT-ID'
npm start
```

Open `http://localhost:8080/health` — you should see JSON with `"ok": true`.

### Docker

Use the `relay-server/Dockerfile`. Pass the same env vars at runtime. Map the container port to whatever you expose publicly (the app listens on `PORT`, default 8080).

### Fly.io (example pattern)

1. Install `flyctl` and log in.
2. From `relay-server/`, create **your own** app name (do not reuse someone else’s):

   ```bash
   flyctl apps create YOUR-APP-NAME
   ```

3. Set secrets:

   ```bash
   flyctl secrets set HOST_SECRET=YOUR-LONG-RANDOM-SECRET TWITCH_CLIENT_ID=YOUR-TWITCH-CLIENT-ID -a YOUR-APP-NAME
   ```

4. Copy `fly.toml.example` → `fly.toml`, set `app = 'YOUR-APP-NAME'`, then deploy from `relay-server/`:

   ```bash
   flyctl deploy --app YOUR-APP-NAME
   ```

   Keep your real `fly.toml` out of public git (see `docs/PUBLIC_CHECKLIST.md`).

5. Check `https://YOUR-APP-NAME.fly.dev/health`.

One relay instance accepts **one** RimWorld host at a time. A second host with the same secret replaces the first.

---

## 4. Point RimWorld at your relay

Mod Settings → **Overlord**:

1. **Relay Server URL** — your relay base URL as the mod expects (HTTPS/WSS host you deployed). Leave blank only for local embedded mode.
2. **Host secret** — exact same string as `HOST_SECRET`.
3. Load a save / start the game so the host connects.

Confirm in-game that the mod reports a relay connection (Overlord tab / logs). On the relay, `/health` should show `"host": true` while you are connected.

---

## 5. Invite viewers

1. Give them your relay’s **public HTTPS site** (the page that serves the viewer UI), not your host secret.
2. They log in with Twitch.
3. They claim a colonist or wait for you to assign one in the Overlord tab.

Never publish your `HOST_SECRET`. Anyone with it can take the host slot on that relay.

---

## 6. Toolkit Buy (optional)

1. Install and configure Twitch Toolkit (and ToolkitUtils if you use it) on the host.
2. Connect Toolkit’s Twitch chat client in RimWorld.
3. Viewers use Overlord **Buy** / **Story** while assigned (for pawn-targeted SKUs).

If Toolkit chat is offline, Overlord shows purchases as locked.

---

## Troubleshooting

| Symptom | Check |
|---------|--------|
| `/health` has `"host": false` | Mod relay URL, secret mismatch, RimWorld not in a playable state, firewall |
| Viewers can’t log in | `TWITCH_CLIENT_ID`, redirect URI must match the URL they open |
| “Purchases locked” / Toolkit offline | Toolkit chat connected in RimWorld |
| Second streamer kicks you off | Same relay + same secret — use a separate relay instance per streamer |
| Stale viewer UI after deploy | Hard-refresh; `/health` `clientBuild` should match your deploy |

---

## Security checklist

- [ ] Unique `HOST_SECRET` per relay; not reused from examples or other people
- [ ] No `.env` committed; secrets only in the host platform
- [ ] Twitch redirect URI is *your* origin only
- [ ] You are not advertising another operator’s live relay as the default

---

## Probe for interest

A simple public pitch:

> Self-hosted RimWorld mod: Twitch viewers drive colonists from a browser. Repo + setup guide — come by the stream to see it live.

Point at this repository and your VOD/stream. Do not put a shared production relay URL in the README.
