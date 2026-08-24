# Hosting Overlord — a guide for streamers

You do **not** need to be a developer to run Overlord. Pick the path that matches
what you want to do.

| I want to… | Path | Cost | Setup time |
|---|---|---|---|
| Play with friends, no stream | **Path A — Friends mode** | Free | ~2 minutes |
| Let Twitch viewers control colonists | **Path B — Your own relay** | Free tier is enough | ~20 minutes |

There is **no shared public server**. Nobody else's relay will work for you, and
you should not point your game at one — it can only serve one streamer at a time.

---

## Path A — Play with friends (no Twitch, no streaming)

The mod can serve the viewer page by itself, straight out of your game. No
server to deploy, no accounts, no secret.

1. Subscribe to **Harmony**, then **Overlord**. Enable both in the mod list.
2. **Mod Settings → Overlord.** Leave **Relay Server URL empty**.
3. Load or start a save.
4. Come back to **Mod Settings → Overlord**. The top of the window now shows a
   green line with the exact link to send, like:
   `Friends mode — ready. Send friends: http://192.168.1.20:8421`
5. Send friends that link. They open it, type any name, and claim a colonist.

### If it says "THIS MACHINE ONLY"
Windows would not let RimWorld listen on your network. **Close RimWorld, right-click
it, and Run as administrator.** The status line turns green.

### Friends who aren't in your house
The link above only works on your own network. For remote friends, either:

- **A tunnel (easiest).** Install [Tailscale](https://tailscale.com/) on your
  machine and theirs — both join a private network and the same link just works.
  Free for personal use.
- **Port forwarding.** Forward your local port (default `8421`) on your router,
  then give friends `http://YOUR-PUBLIC-IP:8421`. Anyone who finds that address
  can open the page, so prefer the tunnel.

---

## Path B — Your own relay (for Twitch viewers)

For a public stream you need a small server ("relay") sitting between RimWorld
and your viewers, because your viewers are not on your home network.

**You host your own, on your own account.** That keeps the bill yours (usually
$0 on a free tier) and means nobody else's traffic touches it.

### What you'll need
- A free hosting account — [Fly.io](https://fly.io) or [Railway](https://railway.app)
- A free [Twitch developer application](https://dev.twitch.tv/console/apps) (for viewer login)
- About 20 minutes, once

### Step 1 — Get a host secret
In **Mod Settings → Overlord**, click **Generate** next to Host secret, then
**Show**, and copy the value. This is the password your game uses to claim the
host slot. Keep it private — anyone with it can kick you off your own relay.

### Step 2 — Register a Twitch application
1. Open the [Twitch Developer Console](https://dev.twitch.tv/console/apps) → **Register Your Application**.
2. **Name:** anything. **Category:** Application Integration.
3. **OAuth Redirect URL:** your relay's address, which you'll have after step 3.
   Put `http://localhost:8080/` for now and correct it afterwards.
4. Copy the **Client ID**. You do not need the Client Secret.

### Step 3 — Deploy the relay
Full commands are in [SELF_HOST.md](SELF_HOST.md). The short version:

1. Download this repo (green **Code → Download ZIP** on GitHub) and unzip it.
2. Install your host's CLI ([flyctl](https://fly.io/docs/flyctl/install/) or
   [Railway CLI](https://docs.railway.app/develop/cli)) and log in.
3. From the `relay-server` folder, create an app under **your own** account and
   set two secrets — `HOST_SECRET` (from step 1) and `TWITCH_CLIENT_ID` (step 2).
4. Deploy. You get a URL like `https://your-app-name.fly.dev`.
5. Go back to your Twitch app and set the **OAuth Redirect URL** to that URL.

### Step 4 — Point RimWorld at it
In **Mod Settings → Overlord**:
- **Relay Server URL:** your `https://your-app-name.fly.dev`
- **Host secret:** the same value you set as `HOST_SECRET`

Load a save, then reopen the settings. The top line should read
**"Relay mode — connected."** in green.

### Step 5 — Invite viewers
Give viewers the relay URL — **never the host secret**. They log in with Twitch
and claim a colonist, or you assign one from the Overlord tab.

---

## Troubleshooting

| The settings screen says | What it means |
|---|---|
| `Relay mode — NOT connected` | URL typo, secret mismatch, or the relay isn't running. Open `https://YOUR-RELAY/health` in a browser — it should say `"ok":true`. |
| `No host secret set` | Click **Generate**, then put the same value on the relay. |
| `Friends mode — THIS MACHINE ONLY` | Run RimWorld as administrator. |
| `Friends mode — not started yet` | Load a save; the server starts with the game. |
| Viewers can't log in | `TWITCH_CLIENT_ID` missing, or the Twitch redirect URL doesn't exactly match the address viewers open. |
| Another streamer kicked you off | You're both on one relay. Each streamer needs their own. |

## Costs

Friends mode is free — it's your own PC.

For a relay, Fly.io and Railway both have free allowances that comfortably fit a
small relay; the main variable is bandwidth, since each viewer receives a live
map image several times a second. If you run big viewer counts, lower **Frame
interval** and **Image size** in mod settings to cut it.
