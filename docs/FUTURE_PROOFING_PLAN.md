# Overlord Future-Proofing Plan

## Goal

Make Overlord survive RimWorld updates by shrinking the blast radius of upstream API drift.

This does **not** mean "never update the mod again."

It means:

- the browser and relay protocol stay stable
- most gameplay/network code stays version-agnostic
- RimWorld-specific breakage is isolated to a small compatibility surface
- degraded features fail soft instead of taking the whole mod down

## Stable Surfaces

These should be treated as product contracts and changed carefully:

- `Source/Networking/StateProtocol.cs`
- browser message shapes used by `relay-server/public/app.js`
- viewer/session state and permissions
- relay server host/viewer/admin roles
- save data structure for `ViewerSession` and `ViewerManager`

The browser should care about logical data such as:

- `outfitPolicy`
- `foodPolicy`
- `areaRestriction`
- `pawn_state`
- `action_result`
- `map_full`
- `map_delta`

It should not care which RimWorld property or method produced those values.

## Compatibility Boundary

Version-sensitive RimWorld API access belongs in:

- `Source/Compat/RimWorldCompat.cs`

The rest of the codebase should call the facade instead of directly assuming a specific RimWorld API surface for:

- policy setters/getters
- timetable/work operations
- portrait rendering
- colonist bar access
- context menu generation

When a new RimWorld version breaks something, the first question should be:

"Can this be fixed in `RimWorldCompat.cs` without touching the rest of the mod?"

If the answer is no, the compatibility boundary is too thin.

## Feature Tiers

Support should be organized into fail-soft tiers.

### Tier 1: Core Playability

Must keep working first:

- host connection
- viewer connection
- colonist assignment
- state sync
- draft
- move
- attack
- action feedback

### Tier 2: Management Controls

Can degrade without killing the mod:

- work priorities
- schedule edits
- policy changes
- area restrictions
- inventory actions
- context actions

### Tier 3: Extras

Nice-to-have but not allowed to break core control flow:

- portrait rendering
- OBS/admin overlays
- votes
- triggered events
- respawn UX
- cosmetic polish

## Execution Order

### 1. Stabilize the protocol

- Fix naming drift between the C# mod and browser UI.
- Keep backward-compatible handling where cheap.
- Treat the browser protocol as the source of truth for user-facing behavior.

### 2. Expand the compatibility layer

- Move more RimWorld-touching code behind `RimWorldCompat.cs`.
- Remove direct version assumptions from gameplay, UI, and serialization code.
- Prefer one-time reflection and cached delegates over repeated reflection at call sites.

### 3. Add capability reporting

- Expose what the current RimWorld build actually supports.
- Let the browser/admin surfaces hide or disable unavailable features instead of failing noisily.
- Log capability status during initialization for quick upgrade triage.

### 4. Unify browser surfaces

- Reduce divergence between relay mode and embedded/local mode.
- Avoid maintaining two separate UIs with different protocols and behaviors.
- The long-term target is one canonical frontend with transport/auth differences handled at the boundary.

### 5. Build a regression harness

- Keep save files that exercise assignment, death, inventory, hostile targets, portal flow, and viewer management.
- Re-run the same checklist on each RimWorld upgrade.
- Compare protocol payloads before and after upgrades when debugging breakage.

### 6. Define support policy

Recommended support posture:

- current RimWorld release
- previous major/minor release if practical
- fail-soft behavior for unsupported features

## Current Progress

Completed in the current pass:

- introduced `Source/Compat/RimWorldCompat.cs`
- routed policy/state/portrait/colonist-bar/context-menu access through the compat layer
- fixed `command_result` vs `action_result` browser drift
- added admin-only death broadcast support for OBS/admin consumers
- added host capability messages to viewer/admin surfaces
- wired portrait delivery through a dedicated `pawn_portrait` message on explicit state requests
- verified the mod still builds with `dotnet build Overlord.csproj -c Release`

## Next Recommended Work

1. Expand capability-based UI disabling beyond context-menu gating and admin logging.
2. Reduce divergence between embedded mode and relay mode.
3. Decide whether portraits should refresh only on explicit state requests or also on assignment/appearance changes.
4. Build a repeatable regression harness around real save files and protocol snapshots.
