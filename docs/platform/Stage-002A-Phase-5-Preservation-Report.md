# CrossBusiness Platform — Stage 2A · Final Preservation Report

**84 First-Tab files · restore-verified · 0 mismatches.**

Archive: `scratchpad/b6final/b6-final.tar.gz`
SHA-256: `d3dc7369210b71c5c051c4bb80336a7497064e47eb80676a562944d3eee601d7`

The hash is recorded **here and in `b6final/archive-hash.txt`, both outside the archive** — stamping it into a file
the archive contains would change the archive and invalidate the hash.

---

## 1. Verification

| Check | Result |
|---|---|
| Files preserved | **84** |
| Manifest rows (`Stage-002A-Phase-5-Disk-State-Manifest.csv`) | **84**, each with SHA-256 and byte size |
| Restore-verify — extract, hash every file, compare to disk | **0 mismatches / 84 files** |
| Restore directory removed afterwards | **yes** |
| Prior archives overwritten | **none** |

## 2. Prior archives — all intact at their recorded hashes

| Phase | Hash | Path |
|---|---|---|
| 1 | `2e0cfd5586795873` | `stage2a/stage2a-verified.tar.gz` |
| 2 | `aa1ce5f1258bfb22` | `stage2a-phase2/stage2a-phase2.tar.gz` |
| 3 | `b66aff2463666404` | `stage2a-phase3/stage2a-phase3.tar.gz` |
| 4 | `2b13f7b0d72958cd` | `stage2a-phase4/stage2a-phase4.tar.gz` |
| 5 (B6, unverified) | `c838003b1992c4b8` | `stage2a-phase5/stage2a-phase5-b6-unverified.tar.gz` |
| — (shared edits) | `5919d9b3f4bfcd10` | `stage2a/stage2a-shared-edits.tar.gz` |

Each was re-hashed this increment. This archive was written to a **new path**; none of the above was touched.

## 3. Scope — how ownership was decided, and a correction

The archive contains **only files this programme produced or modified**: the Roslyn analyzer and its tests (Batch
00-A), the grant writer and its API controller (Batch A), the bootstrap policy contracts / entity / reader / seeder
and its SQL slice (Batch B), the three converted B6 sites plus `CrmAccessService.cs` as evidence of **non**-change,
this tab's tests and evidence infrastructure, the two `engineering/*.json` files, and this tab's documents.

**A first attempt globbed `BL/Platform/*.cs` and `Models/Platform/*.cs` wholesale and swept in 50 files belonging to
the parallel Platform Kernel** — `BusinessEvent*`, `Timeline*`, `NotificationProjectionConsumer`,
`CompanyScopeMiddleware`, `CompanyQueryFilters`, `EntityRegistry` and the legacy timeline adapters. CLAUDE.md
attributes several of these to the parallel team by name. The archive was rebuilt from an **explicit allow-list**
instead.

Nothing was at risk — archiving reads files and does not modify them — but an archive labelled "First-Tab-owned" that
silently contains another team's kernel misdescribes what it holds, and would be misleading if it were ever used to
restore.

Ownership could not be settled from git: **nothing under `Platform/` is tracked**, so every file there is uncommitted
and `git ls-files` returns empty for the whole directory. The allow-list is therefore derived from what this
programme built, not inferred from the tree.

Files owned by the Reporting, Communication and Construction tabs were **not modified and are not in the archive**.

## 4. Environment at preservation time

0 probe databases · 0 mutation markers · `CrossBuyDB2` present and untouched · no SQL executed against it.
