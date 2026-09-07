# Stage — Reporting: ADR Renumbering Evidence (ADR-030 → ADR-037)

**Date:** 2026-08-06
**Owner decision applied:** the Reporting ADR moves; Communication keeps ADR-030…036.

---

## 1. The collision

Two documents occupied `ADR-030` in the shared `docs/platform` folder:

```
docs/platform/ADR-030-Communication-Platform-Architecture.md   (third tab)
docs/platform/ADR-030-Reporting-Platform-Architecture.md       (second tab — this one)
```

Both tabs independently took the next free number after ADR-029, in the same folder, without coordination.

**Resolution (owner):** Reporting moves to **ADR-037**. Communication's contiguous block ADR-030…036 stays.
The asymmetry is deliberate and cheap: Communication is a seven-ADR block cross-referenced from CPS-001 and from
each of its siblings (moving it means edits in ~10 documents), while Reporting is one document with a handful of
inbound references.

---

## 2. What was changed

### 2.1 The document
```
git mv  docs/platform/ADR-030-Reporting-Platform-Architecture.md
     →  docs/platform/ADR-037-Reporting-Platform-Architecture.md
```
Its title line became `# ADR-037 — Reporting Platform Architecture`, and a **`Renumbered:`** header line was added
recording the previous number, the reason, and a pointer to this file. That line is the one intentional remaining
mention of the old number (§4).

### 2.2 Reporting-owned files rewritten — 41 files, 63 occurrences

| Area | Files | Occurrences |
|---|---|---|
| `CrossBuy/BL/Reporting/*.cs` | 26 | 30 |
| `CrossBuy/Models/Context/Reporting/*.cs` | 2 | 2 |
| `CrossBuy.Tests/Reporting*.cs` | 9 | 10 |
| `CrossBuy/deploy/sql/reporting_platform.sql` | 1 | 1 |
| `docs/platform/RPS-001-Reporting-Platform-Specification.md` | 1 | 14 |
| **Shared files — Reporting lines only** | 2 | 3 |
| **Total** | **41** | **60** |

Plus 1 in-document occurrence inside the renamed ADR itself = **61 rewritten**.

### 2.3 The two shared files — surgical, line by line

`CrossBuy/Program.cs` and `CrossBuy/Models/Context/CrossDbContext.cs` each contain references belonging to **both**
tabs. The rewrite was performed line by line with a Communication guard, not by whole-file replacement:

| File | Line | Owner | Action |
|---|---|---|---|
| `Program.cs` | 2 | Reporting | `using CrossBuy.BL.Reporting;   // ADR-030:` → `ADR-037:` |
| `Program.cs` | 172 | Reporting | `// ---- Reporting Platform (ADR-030) ----` → `(ADR-037)` |
| `CrossDbContext.cs` | 429 | Reporting | `// ---- Reporting Platform (ADR-030) ----` → `(ADR-037)` |
| `CrossDbContext.cs` | **227** | **Communication** | **UNTOUCHED** |

---

## 3. Communication was not modified — measured, not asserted

`ADR-030` occurrences across all Communication-owned files, counted before and after:

```
BEFORE : 36
AFTER  : 36        ✅ unchanged
```

Scope of the count: `CrossBuy/BL/Communication/`, `CrossBuy/Models/Communication/`,
`CrossBuy/Models/Context/Communication/`, `communication_platform_slice_001.sql`,
`ADR-030-Communication-Platform-Architecture.md`, `ADR-031`…`ADR-036`, `CPS-001`, and the Communication delivery
report.

Additionally, `CrossDbContext.cs:227` (the Communication mapping line) was verified present and unchanged, and the
Communication test suite was re-run afterwards: **250 / 250 passing**.

---

## 4. Stale-reference sweep

Every `ADR-030` occurrence remaining in the repository, classified by owner:

| Class | Count | Verdict |
|---|---|---|
| Communication-owned files | 36 | correct — Communication owns ADR-030 |
| Communication line inside a shared file (`CrossDbContext.cs:227`) | 1 | correct |
| Intentional historical mention in `ADR-037-Reporting-Platform-Architecture.md:4` | 1 | correct — documents the renumbering |
| **Stale Reporting references** | **0** | ✅ **GATE PASSED** |

Method: a per-file walk over every `.cs`/`.md`/`.sql`/`.csv`/`.json` in the tree (excluding `bin`/`obj`/`.git`),
classifying each hit by owning file rather than by a text pattern — a regex over the whole repo would have
mis-filed the shared files, which is precisely where the two tabs' references sit side by side.

The single mention in ADR-037 line 4 is a **historical note**, not a reference: it records what the document used
to be called. It does not point at a document expecting to find it.

---

## 5. Link resolution

Every `ADR-###-*.md`, `RPS-001*.md` and `CPS-001*.md` filename referenced anywhere in the tree was checked
against the files that actually exist in `docs/platform`:

```
unresolved document links: 0        ✅
```

That covers `RPS-001`'s companion pointer, which now correctly reads
`ADR-037-Reporting-Platform-Architecture.md`.

---

## 6. Post-change verification

| Check | Result |
|---|---|
| Solution build, Debug, no exclusions | **0 errors** (2026-08-06 04:55Z) |
| Solution build, TestRun, no exclusions | **0 errors** |
| Reporting tests | **179 / 179 passing** |
| Communication tests (third tab, unaffected) | **250 / 250 passing** |

All 61 rewritten occurrences are inside comments, a doc-comment header or markdown prose — no identifier, symbol,
string literal or test assertion changed. No Reporting test asserts a document filename, which was checked before
the rewrite.

---

## 7. Gate criteria

| # | Criterion | Status |
|---|---|---|
| 1 | Reporting ADR renumbered 030 → 037 | ✅ |
| 2 | Every Reporting-owned reference updated | ✅ 41 files, 61 occurrences |
| 3 | Communication ADR-030…036 untouched | ✅ 36 = 36, verified by count and by re-running their suite |
| — | Zero stale Reporting references | ✅ |
| — | All document links resolve | ✅ 0 unresolved |
