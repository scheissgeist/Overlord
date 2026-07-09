# Overlord

Twitch viewers control your RimWorld colonists from a browser.

Assign a colonist to a viewer and they get a live control panel: needs, health, skills, map view, draft/move/work/gear, and (optionally) a Twitch Toolkit store in the same window.

**RimWorld 1.5 / 1.6** · requires **[Harmony](https://github.com/pardeike/HarmonyRimWorld)** · self-hosted relay

There is no shared public server. Each streamer runs their own relay.

---

## How it fits together

```
Viewers (browser + Twitch login)
        │
        ▼
  Your relay server  ◄── RimWorld host (Overlord mod)
        │
        └── optional: Twitch Toolkit / ToolkitUtils for Buy / Story purchases
```

- **Mod** — runs in RimWorld; sends pawn/map state; applies viewer commands.
- **Relay** — Node server (Fly.io, Docker, or local). One host connection per instance.
- **Viewers** — open your relay URL, log in with Twitch, claim or wait for assignment.

Native pawn control (draft, move, work, gear) is Overlord. Toolkit **Buy** still needs Toolkit loaded and its Twitch chat client connected in RimWorld.

---

## Install

### From a GitHub Release

1. Download the latest **Release** zip.
2. Unzip into `RimWorld/Mods/Overlord`.
3. Enable **Harmony**, then **Overlord**.
4. Deploy your own relay — see **[docs/SELF_HOST.md](docs/SELF_HOST.md)**.

### From source

```bat
build.bat
```

Or `dotnet build Overlord.csproj -c Release`. Close RimWorld before replacing the DLL.

Then follow **[docs/SELF_HOST.md](docs/SELF_HOST.md)** for the relay, Twitch app, and mod settings.

---

## Relay (short)

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

Overlord is inspired by **[Puppeteer](https://github.com/pardeike/Puppeteer)** by **Andreas Pardeike (Brrainz)** — the original RimWorld mod that let Twitch viewers control colonists from a browser. Overlord is a new codebase aimed at RimWorld 1.5/1.6 and self-hosted relays; it is not affiliated with or maintained by the Puppeteer authors.

Harmony is by the same author: [HarmonyRimWorld](https://github.com/pardeike/HarmonyRimWorld).

---

## License

MIT — see [LICENSE](LICENSE).
