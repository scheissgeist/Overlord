# Overlord — public GitHub / Releases checklist

Use this before the first public push and before each GitHub Release.

## Repo vs Release

| Surface | What people get |
|---------|-----------------|
| **Public repo** | Source + `README` + `docs/SELF_HOST.md` + relay code. For people who build/deploy themselves. |
| **GitHub Release** | Versioned **mod zip** (and optional notes). For streamers who just drop a folder into Mods. |

Releases do **not** replace the need for each streamer to run **their own relay**. The zip is the RimWorld mod package; the relay is still self-hosted per [SELF_HOST.md](SELF_HOST.md).

## Must not be public

- [ ] `AGENTS.md` (personal health / project registry) — gitignored; remove from git index if still tracked
- [ ] `docs/SESSION_LOG_*.md`, `docs/HANDOFF_*.md`, `docs/TASK_*.md` — private ops notes (live Fly URL, emails, instance IDs)
- [ ] `docs/IMPLEMENTATION_STATUS.md` — internal handoff pointer
- [ ] `relay-server/fly.toml` with a real `app = '…'` — use `fly.toml.example` only
- [ ] Any `.env` / secrets / Twitch client secret
- [ ] Hardcoded personal relay URL in README or SELF_HOST

## First public publish

Local history was rewritten (2026-07-09):

- `master` — single orphan commit `d74cf8c` (public tree only). **Push only this branch.**
- `archive/pre-public-history` — full prior history + private docs. **Local only — never push this branch.**

Steps:

1. Create a new public GitHub repo (empty, no README).
2. `git remote add origin <url>`
3. `git push -u origin master`  (do **not** `git push --all`)
4. Attach the first Release zip from `scripts/pack-release.ps1`.

## Pack a Release zip

```powershell
# From repo root, with RimWorld closed if overwriting Steam Mods
.\scripts\pack-release.ps1 -Version 0.1.0
```

Creates `dist/Overlord-0.1.0.zip` with `About/`, `Assemblies/`, `Defs/`, `WebUI/`.

Then on GitHub: **Releases → Draft a new release → tag `v0.1.0` → upload the zip**.

## Release notes template

```text
Overlord v0.1.0

Mod package for RimWorld 1.5/1.6 (requires Harmony).

Install: unzip into RimWorld/Mods/Overlord
Relay: each streamer deploys their own — see docs/SELF_HOST.md in the repo
This release does not include a shared public server.
```
