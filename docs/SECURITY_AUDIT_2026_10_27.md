# Security audit — 2026-10-27

Scope: this repository (public), focused on the relay deployed as Fly app
`overlord-relay` (`relay-server/`), plus the full git history of every branch and tag.

Status: **in progress** — findings are added as they are confirmed and fixed.

## Findings

| # | Severity | Finding | Where | Status |
|---|----------|---------|-------|--------|
| 1 | High | Expired viewer session token crashes the relay process | `relay-server/server.js:1182` | Open |
