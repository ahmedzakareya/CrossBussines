# Stage — Reporting: Preservation Report

**Platform:** CrossBusiness Reporting Platform
**Date:** 2026-08-06
**Archive:** `engineering/preservation/Stage-Reporting-Preservation-20260806.zip`
**Manifest:** `docs/platform/Stage-Reporting-Disk-State-Manifest.csv`

---

## 1. What is preserved

The complete Reporting increment, and nothing else: **57 files**.

| Kind | Count | Location |
|---|---|---|
| `source` | 28 | `CrossBuy/BL/Reporting/*.cs` |
| `model` | 8 | `CrossBuy/Models/Context/Reporting/*.cs` |
| `sql` | 1 | `CrossBuy/deploy/sql/reporting_platform.sql` |
| `test` | 11 | `CrossBuy.Tests/Reporting*.cs` |
| `doc` | 9 | `docs/platform/ADR-037-*`, `RPS-001-*`, `Stage-Reporting-*.md` |
| **Total** | **57** | |

Plus `MANIFEST.csv` embedded at the archive root, so the archive is self-verifying without the repository.

Growth this increment: source **26 → 28**, tests **9 → 11**, docs **2 → 9**.

---

## 2. Exclusion guards — enforced, not asserted

The packer refuses to build the archive if any candidate path matches:

| Excluded | Pattern |
|---|---|
| Build output | `/bin/`, `/obj/` |
| Secrets and local config | `appsettings*`, `*.user`, `.env`, `*secret*`, `*password*`, `*connectionstring*` |
| Other tabs' files | `/Communication/`, `/Construction/`, `/Platform/` |

The assertion runs **before** the zip is written, so a violation fails the packaging step rather than producing a
contaminated archive that somebody later has to notice.

**Verified contents:** 0 build artefacts, 0 configuration files, 0 secrets, 0 files owned by the first, third or
fourth tab.

### 2.1 Two shared files are deliberately NOT in the archive

`CrossBuy/Program.cs` and `CrossBuy/Models/Context/CrossDbContext.cs` each received a Reporting **comment-line**
edit during the ADR renumbering (see `Stage-Reporting-ADR-Renumbering-Evidence.md` §2.3). They are excluded
because they are shared files containing three other tabs' work, and archiving them would package that work under
a Reporting label.

Their Reporting-owned lines are recorded in the renumbering evidence instead, which is the right medium for a
three-line change to a file this tab does not own.

---

## 3. Restore verification

Restored into a disposable directory and every file hashed against the manifest.

```
files in manifest        : 57
files restored           : 57
SHA-256 comparisons      : 57
MISMATCHES               : 0        ✅
missing after restore    : 0        ✅
extra in archive         : 0        ✅
```

The disposable restore directory was removed after verification.

Verification compares the **hash of the restored bytes** against the manifest recorded from the working tree —
not the archive's own CRC, which would only prove the zip is internally consistent rather than that it reproduces
what was preserved.

---

## 4. Archive identity

| | |
|---|---|
| Path | `engineering/preservation/Stage-Reporting-Preservation-20260806.zip` |
| Entries | 57 files + `MANIFEST.csv` |
| Compression | Deflate, level 9 |

The archive's own SHA-256 is recorded in §7 below, computed after the final packaging.

---

## 5. Reproducing the verification

```bash
cd <repo root>

# 1. regenerate the manifest from the working tree
python tools/reporting_manifest.py          # or the inline script in this increment's transcript

# 2. repack, with the exclusion guards active
python tools/reporting_pack.py

# 3. restore into a disposable directory and compare every hash
python tools/reporting_verify.py            # expect: MISMATCHES 0
```

The three steps are the same code, run in sequence: build the inventory, guard-and-pack, restore-and-compare. No
step trusts the previous one's output — the verify step re-reads the archive from disk.

---

## 6. What is deliberately NOT preserved

| Not archived | Why |
|---|---|
| `Program.cs`, `CrossDbContext.cs` | shared files; §2.1 |
| The temporary test harness in the session scratchpad | not part of the repository, referenced by nothing; see the Integration Gate Report §3.4 |
| The disposable SQL proof database | dropped; see the SQL evidence §6 |
| Build output, packages, secrets | §2 |

---

## 7. Integrity record

| Field | Value |
|---|---|
| Manifest rows | 57 |
| Restore mismatches | **0** |
| Packaged (UTC) | 2026-08-06 |
| Archive SHA-256 | **not recorded here — see §7.1** |

Any later edit to a Reporting file invalidates both its manifest row and the archive hash. That is the point: the
pair together says *exactly* what was preserved and when.

### 7.1 Why the archive hash lives in a sidecar, not in this file

**This document is inside the archive.** Writing the archive's own SHA-256 into it is circular: stamping the hash
changes the file, which changes the archive, which changes the hash. A first attempt did exactly that and left the
manifest one revision behind this file — caught by re-running the verification rather than by inspection.

The hash therefore lives beside the archive, where nothing it describes can contain it:

```
engineering/preservation/Stage-Reporting-Preservation-20260806.zip.sha256
```

That file holds the digest and the file name in the standard `sha256sum` format, so verification is:

```bash
cd engineering/preservation
sha256sum -c Stage-Reporting-Preservation-20260806.zip.sha256
```

The manifest inside the archive covers every preserved file individually; the sidecar covers the archive as a
whole. Neither can be self-referential.
