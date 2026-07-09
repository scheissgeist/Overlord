# Overlord

Twitch viewers control your RimWorld colonists from a browser.

Assign a colonist to a viewer and they get a live control panel: needs, health, skills, map view, draft/move/work/gear, and (optionally) a Twitch Toolkit store inside the same window.

**RimWorld 1.5 / 1.6** · requires **Harmony** · self-hosted relay · mobile-friendly UI

This repository is meant for streamers who want to run the same stack themselves. There is no shared public server in this guide — you deploy your own relay.

---

## How it fits together

```
Viewers (browser + Twitch login)
        │
        ▼
  Your relay server  ◄── RimWorld host (Overlord mod)
        │
        └── optional: Twitch Toolkit / ToolkitUtils on the host for Buy / Story purchases
```

- **Mod** — runs inside RimWorld; sends pawn/map state; applies viewer commands.
- **Relay** — small Node server (Fly.io, Docker, or local). One host connection per relay instance.
- **Viewers** — open *your* relay URL, log in with Twitch, claim or wait for assignment.

Native pawn control (draft, move, work, gear) is Overlord. Toolkit **Buy** still needs Toolkit loaded and its Twitch chat client connected in RimWorld.

---

## Quick start (streamer)

### Option A — GitHub Release (mod zip)

1. Download the latest **Release** zip from this repo’s Releases page.
2. Unzip into `RimWorld/Mods/Overlord` (folder should contain `About/`, `Assemblies/`, etc.).
3. Enable **Harmony**, then **Overlord**.
4. Still deploy **your own relay** — the zip is not a hosted service. See **[docs/SELF_HOST.md](docs/SELF_HOST.md)**.

### Option B — Build from source

1. Run `build.bat` (or `dotnet build Overlord.csproj -c Release`) and install into Mods.
2. Deploy the relay from [`relay-server/`](relay-server/) — **[Self-host guide](docs/SELF_HOST.md)**.

### Then connect

1. Create a Twitch developer application; set the redirect URI to your relay origin.
2. In RimWorld → Options → Mod Settings → **Overlord**:
   - Relay Server URL = your relay base (e.g. `https://YOUR-APP.fly.dev` — the mod normalizes to WebSocket)
   - Host secret = the same value as `HOST_SECRET` on the relay
3. Load a colony, confirm the mod connects, then send viewers your relay’s public HTTPS URL.

Full steps: **[docs/SELF_HOST.md](docs/SELF_HOST.md)**.  
Public publish / scrub checklist: **[docs/PUBLIC_CHECKLIST.md](docs/PUBLIC_CHECKLIST.md)**.

---

## Build the mod (developers)

Windows (typical Steam install):

```bat
build.bat
```

Or:

```bat
dotnet build Overlord.csproj -c Release
```

Output: `Assemblies\Overlord.dll`. `build.bat` also copies `About/`, `Assemblies/`, `Defs/`, and the viewer UI into your RimWorld Mods folder when it finds Steam’s Mods path.

Close RimWorld before overwriting the installed DLL.

---

## Relay (developers)

```bat
cd relay-server
npm install
set HOST_SECRET=choose-a-long-random-string
set TWITCH_CLIENT_ID=your-twitch-client-id
npm start
```

See [`docs/SELF_HOST.md`](docs/SELF_HOST.md) for Fly.io / Docker and required environment variables.

---

## Optional: Twitch Toolkit

If you use [Twitch Toolkit](https://github.com/hodlhodl224/TwitchToolkit) / ToolkitUtils:

- Overlord **Buy** / **Story** purchases go through the Toolkit store bridge.
- Toolkit’s Twitch chat client must be connected in-game or purchases stay locked.
- Pawn-targeted store SKUs (e.g. healme, traits) sync onto the **Overlord-assigned** colonist when purchased from Overlord.

Chat-only Toolkit aliases (`!mypawn*`, etc.) are still Toolkit’s own binding — prefer the Overlord UI for assigned viewers.

---

## GitHub Releases

Tagged Releases are the public **mod package** (versioned zip). Source stays in the repo for people who self-host the relay.

```powershell
.\scripts\pack-release.ps1 -Version 0.1.0
```

Upload `dist/Overlord-0.1.0.zip` on the Release. Do not attach secrets, `.env`, or a personal `fly.toml`.

## Sharing / interest

Point people at this repo + Releases, and invite them to your stream/YouTube demo. Do not publish a shared production relay URL as the default.

---

## License

Add a license before you make the repo public (MIT is a common choice for self-hosted tools). Until then, treat the code as all rights reserved by the author listed in `About/About.xml`.

---

## Status

Personal / early public preview. Expect sharp edges; read the self-host guide before going live on stream.
