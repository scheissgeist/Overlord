# Steam Workshop

**Live item:** https://steamcommunity.com/sharedfiles/filedetails/?id=3760983440  
**PublishedFileId:** `3760983440` (stored in `About/PublishedFileId.txt`)  
**Visibility:** Public (as of 2026-07-09)

## Required Items

Add **only** [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) (`2009463077`).

Do **not** add Twitch Toolkit / ToolkitUtils as required (optional Buy/Story bridge).  
Do **not** add Puppeteer.  
Leave Required DLC empty unless a hard DLC dependency appears later.

The self-hosted relay cannot be a Steam Required Item — keep that clear in the description.

## Description / links

1. Paste BBCode from [`STEAM_WORKSHOP_DESCRIPTION.txt`](STEAM_WORKSHOP_DESCRIPTION.txt) into **Edit title & description** if the page still shows only the short About.xml text.
2. **Edit Links** → GitHub: https://github.com/scheissgeist/Overlord
3. Optional gallery image: host UI shot (`docs/images/overlord-host-ui.png`)

## Cover / Preview

- Package thumbnail: `About/Preview.png` (must be **under 1 MB**; currently panel-style branded cover)
- After changing Preview locally, either:
  - RimWorld → Mods (dev mode) → Overlord → **Upload on Steam**, or
  - Workshop → **Add/edit images & videos** and set the main image

## Updating the mod later

1. Rebuild / sync into `RimWorld/Mods/Overlord` (`build.bat`).
2. Keep `About/PublishedFileId.txt` = `3760983440`.
3. RimWorld → Mods (dev mode) → **Upload on Steam** again.

## Do not upload

- `.git`, `Source/`, private docs, `AGENTS.md`, personal `fly.toml`, secrets
