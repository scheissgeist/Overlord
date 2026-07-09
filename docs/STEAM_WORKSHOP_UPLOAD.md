# Steam Workshop upload (remaining manual step)

Prep is done in the repo / Steam Mods folder. Steam requires the upload click inside RimWorld.

## Already prepared
- `About/Preview.png` (1280x720, under 1 MB)
- Latest `About/About.xml` (with Puppeteer credit)
- Mod package under Steam Mods: About, Assemblies, Defs, WebUI
- Workshop BBCode: `docs/STEAM_WORKSHOP_DESCRIPTION.txt`

## You do this in RimWorld
1. Own RimWorld on Steam and be logged into Steam.
2. Options → enable **Development mode**.
3. Main menu → **Mods** → select **Overlord**.
4. Click **Upload on Steam**.
5. After success, copy `About/PublishedFileId.txt` from the Steam Mods folder back into `E:\Overlord\About\` (and commit it) so future updates hit the same Workshop item.
6. On the Workshop page, paste the text from `docs/STEAM_WORKSHOP_DESCRIPTION.txt` into the description (Steam uses BBCode).

## Do not upload
- `.git`, `Source/`, `docs/` session logs, `AGENTS.md`, personal `fly.toml`, secrets
