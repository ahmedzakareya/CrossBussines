# Stage-Workspace-Integration — Preservation Report

**Product:** CrossBusiness Platform · **Component:** CrossBusiness Workspace · **Tab:** TAB 3
**Date:** 2026-08-06

---

## 1. Why this exists

Four tabs write to one uncommitted working tree. This increment's work is **untracked** (`git status` reports
`?? CrossBuy/BL/Workspace/`, `?? CrossBuy/Views/Workspace/`) — an accidental `git clean`, a branch switch, or
another tab's tidy-up would take it with no recovery. The archive is the recovery, and the restore test is what
makes the archive a fact rather than a hope.

---

## 2. The archive

| | |
| --- | --- |
| Path | `engineering/preservation/Stage-Workspace-Integration-20260806.zip` |
| Size | 74,117 bytes |
| **SHA-256** | `8ea6fc537df90c7f6db122d2eb351cd16fb1df6e54acdd332760f71cc6a91e86` |
| Entries | **23** — 22 preserved files + `MANIFEST.csv` |
| Structure | **repository-relative paths preserved** (`CrossBuy/BL/Workspace/…`, not flattened) |

An earlier build of this archive used a flat file list, which discards directory structure; a restore would
have needed a human to remember where each file belonged. It was rebuilt through a staging tree so the archive
restores to the correct paths on its own. The flat version was discarded and is not the artifact recorded here.

`MANIFEST.csv` is carried **inside** the archive as well as beside it, so the archive can be verified by
someone who has only the archive.

---

## 3. What is preserved — and the one thing that is not

**22 files**, every one owned by this tab: 5 business-logic files, 1 controller, 2 shared views, 5 workspace
views, 1 stylesheet, 8 documents.

**`CrossBuy/Program.cs` is deliberately EXCLUDED.** It is a shared composition root that all four tabs write.
This tab's contribution to it is a single line —
`builder.Services.AddCrossBusinessWorkspace();` — and preserving a 644-line shared file would mean that a
restore could silently revert three other tabs' registrations. **A preservation archive must never be able to
destroy work it does not own.**

It is recorded in the manifest (owner `SHARED-one-line-only`) so its state is *documented*, and excluded from
the archive so it can never be *restored*. If Program.cs is ever lost, re-adding one line is the correct
recovery — not overwriting it from here.

### 3.1 The second exclusion: this report

**This file is also absent from the manifest and the archive, deliberately.** It publishes the archive's
SHA-256, so it cannot be inside the thing it hashes and cannot list its own hash — any value written here would
be wrong the moment the sentence was finished.

The consequence is that the archive covers **22 of the 23 documents-and-code files** this increment produced.
The missing one is this report, which is the *record of* the archive rather than part of the work it preserves,
and which is reconstructible from the delivery report and the manifest. Stated here so a future reader finds a
declared exclusion rather than an unexplained gap.

**Two documented exclusions, then: `Program.cs` (shared, must never be restored) and this report
(self-referential).** Everything else this tab produced is inside the archive and verified.

---

## 4. Restore test

Performed 2026-08-06. Extracted to
`…/scratchpad/ws-restore` — **a scratch directory, never over the working tree.** Restoring over a live tree
would overwrite whatever the other tabs had written since, which is precisely the accident this archive exists
to prevent.

```
RESTORE TEST — 2026-08-06
extracted     : 23 files
files checked : 22
MISMATCHES    : 0
```

Every extracted file's SHA-256 was recomputed and compared against the manifest recorded at archive time.
**Zero mismatches, zero missing files.**

### 4.1 The working tree was independently confirmed unchanged

```
=== live working tree vs manifest ===
live drift: 0
```

Every live file still hashes to its manifest value, so the restore test demonstrably read from scratch and
wrote nothing back. This is the check that distinguishes "the archive is good" from "the archive matches
because I just overwrote the tree with it."

---

## 5. How to restore

```powershell
# 1. Verify the archive is the one this report describes
Get-FileHash engineering\preservation\Stage-Workspace-Integration-20260806.zip -Algorithm SHA256
#   expect 8EA6FC537DF90C7F6DB122D2EB351CD16FB1DF6E54ACDD332760F71CC6A91E86

# 2. ALWAYS extract to scratch first — never straight over a live tree
Expand-Archive -Path engineering\preservation\Stage-Workspace-Integration-20260806.zip `
               -DestinationPath $env:TEMP\ws-restore -Force

# 3. Compare before copying anything back
#    (the archive's own MANIFEST.csv carries every expected hash)

# 4. Copy back ONLY the files that are actually missing or damaged.
#    Program.cs is NOT in the archive by design — re-add its one line by hand:
#        builder.Services.AddCrossBusinessWorkspace();
```

---

## 6. Verification state at preservation time

| Check | Result |
| --- | --- |
| Web project, Debug, **no exclusions** | 0 errors |
| Web project, Release, **no exclusions** | 0 errors |
| Test suite | 1451 passed · **0 failed** · 183 skipped (SQL-gated) |
| Workspace-owned build errors | 0 |
| Live tree vs manifest | 0 drift |
| Restore mismatches | **0** |

The one test-project exclusion (`ReportingUiSafetyTests.cs`, Reporting-owned, does not compile against its own
host) is command-line only and is documented in `…-Delivery-Report.md` §2.2. No repository file was modified to
obtain any of these results.
