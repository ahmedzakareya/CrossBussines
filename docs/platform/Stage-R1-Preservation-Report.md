# Stage R1 — Preservation Report

**30 files · 0 restore mismatches.**

Archive: `scratchpad/r1final/r1-final.tar.gz`
SHA-256: `f4778590767b88fa6393c8616e8d76a16546515567f685f44e59eab96cdd22b1`

The hash is recorded **here and in `r1final/archive-hash.txt`, both outside the archive** — stamping it into a file the archive contains would change the archive and invalidate the hash.

---

## 1. Verification

| Check | Result |
|---|---|
| Files preserved | **30** |
| Manifest rows (`Stage-R1-Disk-State-Manifest.csv`) | 29 + the manifest itself |
| Restore-verify — extract, hash every file, compare to disk | **0 mismatches / 30 files** |
| Restore directory removed afterwards | Yes |
| Isolation harness removed | Yes (`scratchpad/r1verify`) |
| Registries regenerate byte-identical | **Yes** |
| Prior archives overwritten | **None** — 11 archives now preserved |

## 2. Contents

| Group | Files |
|---|---|
| Governance registries (generated) | `tab-ownership.json`, `shared-files.json`, `adr-registry.json`, `sql-slices.json`, `manifest-facts.json` |
| Governance tools | `integration-gate.ps1`, `build-gate.ps1`, `check-file-ownership.ps1`, `validate-sql-governance.ps1`, `apply-sql-slices.ps1`, `setup-worktrees.ps1` |
| Governance documentation | `governance/README.md`, `governance/branch-protection.md` |
| CI | `.github/workflows/integration-gate.yml` |
| ADRs | `ADR-038`, `ADR-039` |
| R1 documents | Reporting-Authorization-Surface-Evidence, Ownership-Reconciliation, Registry-Reconciliation, Integration-Baseline, Owner-Actions, Final-Delivery-Report, Disk-State-Manifest, this report |
| Analyzer changes | `AuthorizationSurface.cs`, `ReportingAuthoritySurfaceTests.cs`, `Surface.cs` |
| Canonical dataset generator | `roadmap_v2_data.py`, `roadmap_v2_data2.py`, `generate_roadmap_v2.py` |

The generator is included because the registries are **generated artefacts** — preserving the JSON without its source would preserve an output nobody could reproduce.

## 3. Scope — what was deliberately excluded

No file owned by another tab is in the archive. `CrossBuy/BL/Reporting/**`, `CrossBuy/BL/Workspace/**`, `CrossBuy/BL/Communication/**`, `CrossBuy/BL/Construction/**` and the Platform Kernel were **read** during R1 and **not modified**, so they are not R1 artefacts.

`CrossBuy/deploy/sql/manifest.json` was **regenerated** by R1 but is a TAB-0 shared artefact rather than an R1 deliverable; its regeneration is recorded in the registry reconciliation instead of archived here.

## 4. Environment at preservation time

0 probe databases · `CrossBuyDB2` present and untouched · no SQL executed against it · no file moved or deleted in either SQL tree · nothing committed or pushed.

## 5. All archives intact

| Hash | Archive |
|---|---|
| `f47785907 67b88fa` | `r1final/r1-final.tar.gz` — **this increment** |
| `76a927f25cb7eb73` | `r1/r1-governance.tar.gz` |
| `2eaf90f22af5df20` | `roadmapv2-approved/roadmap-v2-approved.tar.gz` |
| `89eb7b5fc6060dec` | `roadmapv2/roadmap-v2.tar.gz` |
| `d3dc7369210b71c5` | `b6final/b6-final.tar.gz` |
| `c838003b1992c4b8` | `stage2a-phase5/…-b6-unverified.tar.gz` |
| `2b13f7b0d72958cd` | `stage2a-phase4/…` |
| `b66aff2463666404` | `stage2a-phase3/…` |
| `aa1ce5f1258bfb22` | `stage2a-phase2/…` |
| `5919d9b3f4bfcd10` | `stage2a/stage2a-shared-edits.tar.gz` |
| `2e0cfd5586795873` | `stage2a/stage2a-verified.tar.gz` |

**11 archives, none overwritten.** This increment wrote to a new path.
