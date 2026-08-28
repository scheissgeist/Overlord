# Hosting Overlord — a guide for streamers

You do **not** need to be a developer to run Overlord. Pick the path that matches
what you want to do.

| I want to… | Path | Cost | Setup time |
|---|---|---|---|
| Play with friends, no stream | **Path A — Friends mode** | Free | ~2 minutes |
| Play online or stream | **Path B — Use the Overlord relay** | Free | ~1 minute |
| Run a relay for myself (and maybe others) | **Path C — Your own relay** | Free tier is enough | ~20 minutes |

**Path B is the default and it is the easy one.** You do not need an account, a
server, a Twitch application, or a secret. The official Overlord relay address is
already filled in; press one button and the mod creates your host room and copies
the link to share. Several games can use the relay without interfering with each
other. Replace the address only if you want to use a compatible relay you or a
friend operates.

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

## Path B — Use the Overlord relay

1. Subscribe to **Harmony**, then **Overlord**. Enable both.
2. Open the in-game **Overlord** tab and choose **People anywhere / my stream**.
3. The official address `https://overlord-relay.fly.dev` is already filled in. Press **Set me up**.
4. The line under it turns green: *You're set up. Send people this link:* followed
   by your own link, like `https://overlord-relay.fly.dev/g/kjkksh92`. It is copied automatically.
5. If setup happened inside a running save, return to the main menu and load the save again. Anyone you send that link to sees **your**
   game, not the relay owner's.

The middle box next to **Set me up** names your game in the relay's list (optional). The
right box is for an invite code, only if you were given one.

### Using another relay
Replace the official address with the compatible relay address you were given, then
press **Set me up**. An invite code is only required when that relay's operator set one.

**What you never do:** invent a secret, copy one between two places, set an
environment variable, or deploy anything. The relay hands your game a key and the
mod stores it. The key is never shown, because there is no reason for you to see it.

### If setup fails
| It says | What to do |
|---|---|
| `This relay does not accept other hosts` | Whoever runs it needs to set `OPEN_HOSTING=1`. Ask them. |
| `That invite code is not right` | They gave you a code and it did not match, or you left it blank. |
| `This relay is full` | It is set to allow a limited number of games at once. Try later. |
| `That host name does not resolve` | Typo in the address. |
| `Cannot reach that relay` | It may be asleep on a free tier — press **Set me up** again. |

### Leaving
**Start over** in Mod Settings forgets the room and the key. Your old link stops
working; press **Set me up** again to get a new one.

### During a stream

The in-game **Overlord** tab is the authoritative host console. It can assign
viewers, change permissions, broadcast messages, run votes, create VOD markers,
and start a community goal. A goal advances by one only after the game accepts a
meaningful viewer action; refreshes and relay reconnects cannot invent progress.

If you operate a custom relay, its operator console at
`https://YOUR-RELAY/admin` exposes the same live exceptions and stream tools. Its
password is that relay's `HOST_SECRET`; shared official-relay hosts use the
in-game console and are not given operator access. Do not share an admin address
or credential with viewers. VOD markers record relay UTC plus the RimWorld tick,
date, and colony name, and remain available after a relay restart. They are edit
markers, not Twitch-created clips.

---

## Path C — Your own relay (for Twitch viewers)

The official Overlord relay already sits between RimWorld and remote viewers. Run
your own only when you want control over capacity, access, login configuration, or
infrastructure. A custom relay can also host friends — see **Letting other people host** below.

**You host your own, on your own account.** That keeps the bill yours (usually
$0 on a free tier) and means nobody else's traffic touches it.

### What you'll need
- A free hosting account — [Render](https://render.com), [Fly.io](https://fly.io) or [Railway](https://railway.app)
- A free [Twitch developer application](https://dev.twitch.tv/console/apps) — **or skip it**, see below
- About 20 minutes, once

### Skipping the Twitch app on your first run
Registering a Twitch application is the longest step and you do not need it to try
this. Set **`ALLOW_GUEST_LOGIN=1`** on the relay and leave `TWITCH_CLIENT_ID`
empty: viewers then open your relay URL, type a name, and join — no login at all,
and unlike friends mode it works over the internet with no tunnel.

The tradeoff is real: **anyone with the URL can join as any name.** Use it to test,
or with a group you trust. When you fill in `TWITCH_CLIENT_ID` later, guest login
switches off by itself — the relay ignores it whenever Twitch is configured, so the
two can never both be live and nobody can type a real viewer's name.

### Step 1 — Get a host secret
In **Mod Settings → Overlord**, click **Generate** next to Host secret, then
**Show**, and copy the value. This is the password your game uses to claim the
host slot. Keep it private — anyone with it can kick you off your own relay.

### Step 2 — Register a Twitch application
Skip this entirely if you are using guest login (above) — set `ALLOW_GUEST_LOGIN=1`
in step 3 instead and come back here when you want real Twitch names.

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
| Viewers can't log in | Neither `TWITCH_CLIENT_ID` nor `ALLOW_GUEST_LOGIN` is set on the relay, or the Twitch redirect URL doesn't exactly match the address viewers open. **Test connection** in Mod Settings names which one. |
| The name box didn't appear | `ALLOW_GUEST_LOGIN` is ignored while `TWITCH_CLIENT_ID` is set. Clear the client id to use guest login. |
| Another streamer kicked you off | You are both using the same self-host credential. Return to the default setup path and press **Set me up** to get a room of your own. |

## Letting other people host on your relay

Set **`OPEN_HOSTING=1`** on the relay. Anyone with the address can then press **Set me up**
in their own Mod Settings and get a room of their own; several games run side by
side and viewers pick which one to watch from the front page.

Guard rails, all environment variables on the relay:

| Variable | Default | What it does |
|---|---|---|
| `OPEN_HOSTING` | off | Must be `1` before anyone else can host. |
| `HOST_INVITE_CODE` | none | If set, a joiner must type this code. Opens the relay to friends without opening it to the internet. |
| `MAX_ROOMS` | `8` | How many games at once. |
| `MAX_VIEWERS` | `50` | Viewers **per game**. |
| `MAX_TOTAL_VIEWERS` | `100` | Viewers across the whole relay. This is the one that bounds your bill. |

**You pay for their viewers.** Each viewer receives a live map image several times a
second, so bandwidth scales with games × viewers. `MAX_TOTAL_VIEWERS` is the real
ceiling; the other two shape how it is divided.

A game that registers and never hosts is dropped after 10 minutes, so abandoned
attempts do not use up your room slots.

## Costs

Friends mode and the official Overlord relay are free for hosts.

For a custom relay, Fly.io and Railway both have free allowances that comfortably fit a
small relay; the main variable is bandwidth, since each viewer receives a live
map image several times a second. If you run big viewer counts, lower **Frame
interval** and **Image size** in mod settings to cut it.
