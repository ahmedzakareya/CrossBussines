# Stage-Communication — Preservation Report

**Platform:** CrossBusiness Communication & Collaboration Platform
**Owner tab:** THIRD TAB (Communication & Collaboration)
**Produced:** 2026-08-06
**Archive:** `engineering/preservation/Stage-Communication-Preservation-20260806.zip`
**Restore test:** ✅ **PASS — 0 mismatches, 0 missing**

---

## 1. Purpose

The shared tree is being edited concurrently by at least four tabs, and during this gate alone a partial landing
from one tab broke the shared test project and two files from another appeared and were corrected mid-write
(Integration Gate Report §3.4). Preservation exists so this platform's state at gate time is **recoverable and
verifiable independently of that churn**.

---

## 2. What is preserved

**59 files. Communication-owned assets only.** No file belonging to another tab is listed or archived — that
boundary is deliberate: an archive that swept in another tab's in-flight work would preserve a broken state and
imply ownership this tab does not have.

| Category | Files | Lines | Contents |
| --- | --- | --- | --- |
| `business-logic` | 21 | 7,241 | `CrossBuy/BL/Communication/*.cs` |
| `ef-entities` | 6 | 905 | `CrossBuy/Models/Context/Communication/*.cs` |
| `contracts` | 8 | 1,753 | `CrossBuy/Models/Communication/*.cs` |
| `tests` | 9 | 4,634 | `CrossBuy.Tests/Communication/*.cs` |
| `deploy-sql` | 1 | 1,034 | `communication_platform_slice_001.sql` |
| `documentation` | 14 | 3,072 | ADR-030…036, CPS-001, delivery report, 5 stage documents |
| **Total** | **59** | **18,639** | 993,584 bytes |

### 2.1 The stated inventory, verified

The brief described the delivered platform as *"35 source files, approximately 9,900 lines"*. Measured:

```
21 (BL) + 6 (entities) + 8 (contracts) = 35 source files
7,241   +   905         +   1,753      =  9,899 lines
```

**Both figures confirmed exactly.** Test files (9) and documentation (14) are counted separately, as the brief did.

---

## 3. The manifest

`docs/platform/Stage-Communication-Disk-State-Manifest.csv` — one row per file:

```csv
category,path,state,bytes,lines,sha256
business-logic,CrossBuy/BL/Communication/CommAccessPolicy.cs,present,23371,486,<sha256>
...
```

`state` is recorded per file rather than assumed. All 59 rows are `present`; a `MISSING` row would be reported
rather than silently omitted, because a manifest that only lists what it found cannot detect a loss.

The manifest is generated from disk, not hand-maintained — a hand-maintained inventory is a second source of truth
that drifts.

---

## 4. The archive

| Property | Value |
| --- | --- |
| Path | `engineering/preservation/Stage-Communication-Preservation-20260806.zip` |
| Entries | 60 (59 files + the manifest itself) |
| Size | 290,617 bytes |
| SHA-256 | `cf612ab713a1e96502c618771bdc3290bc116b764dffd00e3b13d0c246195964` |
| Paths | repository-relative, so it restores into a clean checkout unambiguously |

**The manifest is inside the archive.** An archive whose checksums live only outside it cannot be verified once
separated from its report.

---

## 5. Restore test

```
files checked : 59
missing       : 0
MISMATCHES    : 0
RESULT        : PASS
```

**Method, and why it is this method:**

1. Extract the archive to a **throwaway** directory.
2. Re-hash every extracted file with SHA-256.
3. Compare against the manifest hash recorded at capture time.
4. Delete the throwaway directory.

Restoring **over the working tree** would prove nothing (the files are already there) and could destroy another
tab's concurrent edits. Extraction to scratch is the only form of this test that is both meaningful and safe.

A restore is verified when **every** file's content hash matches — not when the file count matches. A count check
would pass on a truncated or corrupted file.

---

## 6. The seven required documents

| # | Document | Status |
| --- | --- | --- |
| 1 | `Stage-Communication-Integration-Gate-Report.md` | ✅ |
| 2 | `Stage-Communication-SQL-From-Empty-Evidence.md` | ✅ |
| 3 | `Stage-Communication-Entity-Registry-Contract.md` | ✅ |
| 4 | `Stage-Communication-Collaboration-Roadmap.md` | ✅ |
| 5 | `Stage-Communication-DocComments-Migration-Plan.md` | ✅ |
| 6 | `Stage-Communication-Disk-State-Manifest.csv` | ✅ 59 rows |
| 7 | `Stage-Communication-Preservation-Report.md` | ✅ this document |

All seven are inside the archive except this report, which is written after the archive is sealed — its own hash
would otherwise be unknowable at capture time. That is a property of the ordering, and it is stated rather than
hidden: **verify this report against the git history, not against the archive.**

---

## 7. Reproduction

```python
# Regenerate the manifest from disk, then verify the archive against it.
import csv, hashlib, io, os, zipfile, tempfile, shutil

rows = [r for r in csv.DictReader(io.open(MANIFEST, encoding="utf-8"))
        if r["state"] == "present"]

tmp = tempfile.mkdtemp()
zipfile.ZipFile(ARCHIVE).extractall(tmp)
bad = [r["path"] for r in rows
       if hashlib.sha256(open(os.path.join(tmp, r["path"]), "rb").read()).hexdigest() != r["sha256"]]
shutil.rmtree(tmp)
assert not bad, bad          # 0 mismatches
```

---

## 8. What preservation does NOT cover

Stated so the archive is not over-trusted:

* **It is not a deployment.** The 14 tables exist in no database; `AddCommunicationPlatform` is called nowhere.
* **It is not a git commit.** Files remain untracked in this working copy. The archive is a **content** snapshot;
  it does not confer version-control history, and the owner still needs to decide when this work is committed —
  under CLAUDE.md's selective-commit rule, and noting that no shared file was modified by this tab.
* **It does not include another tab's state.** If the Construction partial landing (Gate Report §3) is repaired
  by its owner, that repair is not in this archive and does not need to be.
* **It does not freeze dependencies.** It preserves this platform's own source, tests, schema and documentation —
  not the platform kernel it consumes.

---

## 9. Integrity summary

| Check | Result |
| --- | --- |
| Manifest rows | 59 |
| Files present | 59 / 59 |
| Files missing | 0 |
| Archive entries | 60 |
| Restore mismatches | **0** |
| Source-file count vs stated (35) | ✅ exact |
| Source lines vs stated (~9,900) | ✅ 9,899 |
| Communication tests at capture | **273 / 273** @ 08:22:05 |
| Scratch artifacts removed | ✅ restore dir and SQL scratch database both dropped |
| CrossBuyDB2 | ✅ untouched (1157 objects before and after) |
