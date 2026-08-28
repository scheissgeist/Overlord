# Overlord

Twitch viewers control your RimWorld colonists from a browser.

Assign a colonist to a viewer and they get a live control panel: needs, health, skills, map view, draft/move/work/gear, and (optionally) a Twitch Toolkit store in the same window.

**RimWorld 1.5 / 1.6** · requires **[Harmony](https://github.com/pardeike/HarmonyRimWorld)**

**You do not need Twitch, and you do not need to be streaming.** Overlord runs in two modes:

- **Local / friends mode** — no Twitch, no relay, no stream. Friends open a link on your network and type any name. See [Play with friends](#play-with-friends-no-twitch-no-streaming) or the full **[Hosting guide](docs/HOSTING_GUIDE.md)**.
- **Online / stream mode** — the official Overlord relay is filled in by default. Press **Set me up** and the mod creates your isolated game room, stores its credential, and copies your viewer link. You can replace the address with any compatible relay or run your own. Start with the **[Hosting guide](docs/HOSTING_GUIDE.md)**.

One relay can carry several games at once. Viewers landing on it see a list of what is live and pick one; a relay with a single game skips the list entirely.

### What ships now

- Persistent viewer identity and history across refreshes, host reconnects, deaths, and reassignment.
- Capability-aware controls: a newer browser hides features an older or limited host cannot perform.
- Tactical map rendering from RimWorld terrain/entity manifests, with host-authoritative state and relay replay repair.
- Streamer operations in-game and at `/admin`: assignments, permissions, pending claims, failures, votes, broadcasts, VOD markers, and community goals.
- Durable VOD/edit markers with relay UTC plus RimWorld tick/date context.
- Save-backed community goals that advance only after successful viewer actions and remain visible through reconnect and death screens.

### Optional: deploy your own relay

[![Deploy to Render](https://render.com/images/deploy-to-render-button.svg)](https://render.com/deploy?repo=https://github.com/scheissgeist/Overlord)

One click, no CLI. Render reads [`render.yaml`](render.yaml) from this repo, which
already points the service at the `relay-server` folder and asks you for the two
values below.

The official Overlord relay is the default. Deploying your own is optional and gives
you control over capacity, access, and login configuration. A custom relay runs under
your own account, so its bill (usually $0 on a free tier) is yours.

You will be asked for two values:

| Variable | Where it comes from |
|---|---|
| `HOST_SECRET` | Mod Settings → Overlord → **Generate**, then copy |
| `TWITCH_CLIENT_ID` | [Twitch Developer Console](https://dev.twitch.tv/console/apps) → your app |

**Trying it without a Twitch app:** leave `TWITCH_CLIENT_ID` empty and set
`ALLOW_GUEST_LOGIN=1`. Viewers then open your relay URL, type a name and join —
no login, and unlike friends mode it reaches people outside your network. Anyone
with the URL can join as any name, so it is for testing or a private group. Filling
in `TWITCH_CLIENT_ID` later turns guest login off automatically; the relay refuses
to run both.

**On Railway instead:** [import this repo from GitHub](https://railway.com/new/github?repo=https://github.com/scheissgeist/Overlord).
Railway only one-clicks templates published to its own marketplace, so there is no
button here — after the import, set **Root Directory** to `relay-server` and add the
two variables above by hand in the service's Variables tab.

**Letting friends host on your custom relay:** set `OPEN_HOSTING=1`. They press **Set me up** in
their own Mod Settings and get a room of their own — you never issue anyone a
credential. `MAX_TOTAL_VIEWERS` (default 100) bounds what it can cost you, since
their viewers use your bandwidth. `HOST_INVITE_CODE` limits it to people you gave a
code to.

Prefer the CLI, or want Fly.io / Docker? See **[docs/SELF_HOST.md](docs/SELF_HOST.md)**.

![Overlord in-game host tab — viewer sessions, assignment board, and permissions](docs/images/overlord-host-ui.png)

*In-game Overlord tab: assign viewers, manage colonists, and set permissions.*

---

## How it fits together

```
Viewers (browser + Twitch login)
        │
        ▼
  Overlord relay     ◄── RimWorld host (Overlord mod)
  (or your own)
        │
        └── optional: Twitch Toolkit / ToolkitUtils for Buy / Story purchases
```

- **Mod** — runs in RimWorld; sends pawn/map state; applies viewer commands.
- **Relay** — the official multi-game Overlord service by default, or a compatible Node server you operate yourself.
- **Viewers** — open your relay URL, log in with Twitch, claim or wait for assignment.

Native pawn control (draft, move, work, gear) is Overlord. Toolkit **Buy** still needs Toolkit loaded and its Twitch chat client connected in RimWorld.

---

## Play with friends (no Twitch, no streaming)

Overlord does not require Twitch or a relay. In **local mode** the mod serves the
viewer UI itself, straight from your game.

1. In **Mod Settings → Overlord**, leave **Relay Server URL blank**.
2. Note the **Local server port** (default `8421`).
3. Start or load a save.
4. Friends open `http://<your-computer's-LAN-IP>:8421` in a browser.
5. They type any name — no Twitch login — and claim a colonist.

That's it. No relay to deploy, no Twitch application, no host secret, and nothing
has to be broadcast anywhere.

**Playing with friends outside your network?** The mod only serves your local
network. To reach remote friends, either forward the port on your router or use a
tunnel (Tailscale, `cloudflared`, ngrok) and hand them that address instead.

**If only your own machine can connect,** RimWorld could not bind all network
interfaces and fell back to localhost-only. Run RimWorld as administrator and
check the log — it states which one happened.

---

## Install

### From Steam Workshop

1. Subscribe: [Overlord on Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3760983440)
2. Also subscribe to **[Harmony](https://steamcommunity.com/workshop/filedetails/?id=2009463077)** (or install Harmony another way).
3. Enable **Harmony**, then **Overlord**, in the RimWorld mod list.
4. For streaming: choose **People anywhere / my stream**, leave the official Overlord address in place, and press **Set me up**. Replace the address only to use another relay. See the **[Hosting guide](docs/HOSTING_GUIDE.md)**. For local friends, you need neither.

### From a GitHub Release

1. Download the latest **Release** zip.
2. Unzip into `RimWorld/Mods/Overlord`.
3. Enable **Harmony**, then **Overlord**.
4. Choose online play to use the prefilled Overlord relay, or replace the address with your own — see the **[Hosting guide](docs/HOSTING_GUIDE.md)**.

### From source

```bat
build.bat
```

Or `dotnet build Overlord.csproj -c Release`. Close RimWorld before replacing the DLL.

The official relay needs no server setup. Follow **[docs/SELF_HOST.md](docs/SELF_HOST.md)** only to operate your own relay.

---

## Optional self-hosted relay (short)

```bat
cd relay-server
npm install
set HOST_SECRET=choose-a-long-random-string
set TWITCH_CLIENT_ID=your-twitch-client-id
npm start
```

Full Fly.io / Docker / troubleshooting: **[docs/SELF_HOST.md](docs/SELF_HOST.md)**.

In RimWorld → Mod Settings → **Overlord**, set:

- **Relay Server URL** — your relay base (e.g. `https://YOUR-APP.fly.dev`)
- **Host secret** — same as `HOST_SECRET`

Send viewers your relay’s public HTTPS URL (never the host secret).

---

## Optional: Twitch Toolkit

If you use Twitch Toolkit / ToolkitUtils:

- Overlord **Buy** / **Story** use the Toolkit store bridge.
- Toolkit’s Twitch chat client must be connected in-game or purchases stay locked.
- Pawn-targeted store SKUs (healme, traits, etc.) apply to the **Overlord-assigned** colonist when bought from Overlord.

---

## Acknowledgements

Overlord is inspired by **[Puppeteer](https://github.com/pardeike/Puppeteer)** by **Andreas Pardeike (Brrainz)** — the original RimWorld mod that let Twitch viewers control colonists from a browser. Overlord is a new codebase aimed at RimWorld 1.5/1.6 with a shared default relay and optional self-hosting; it is not affiliated with or maintained by the Puppeteer authors.

Harmony is by the same author: [HarmonyRimWorld](https://github.com/pardeike/HarmonyRimWorld).

---

## Feedback

Questions, bugs, and ideas: [broteampill@gmail.com](mailto:broteampill@gmail.com) · or open a GitHub issue on this repo.

---

## License

MIT — see [LICENSE](LICENSE).
