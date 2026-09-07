# CORRECTION-002 — Four evidence-scanner defects, and the numbers they changed

**Status:** Accepted. Issued during Stage 0 (Slice-003), Batch B.
**Severity of the errors:** Medium for the SQL verdicts — they *understated* deployment safety, so they erred toward
caution and cost effort rather than creating exposure. **High for the endpoint counts:** the Stage 1 security backlog
was published **13% smaller than it actually is** (267 against a true 306), because a four-file `partial` controller
was only partly attributed and API controllers were left out of the table.

Supersedes nothing. Extends [CORRECTION-001](CORRECTION-001-Manufacturing-Permission-Finding.md), which recorded
the first two defects; this records the SQL defect and two further ones found while regenerating the evidence.

---

## Why this document exists

The as-built architecture discovery was produced by scanners that were **run and then discarded**. That is the root
problem behind every defect below: an un-kept scanner cannot be reviewed, re-run, diffed, or corrected, so the
numbers it produced had to be taken on trust — and four of them were wrong.

Batch B's fix is not "be more careful". It is that the derivations are now **checked-in, re-runnable scripts**:

| Script | Produces |
|---|---|
`CrossBuy/deploy/scan-sql-manifest.ps1` | `deploy/sql/manifest.json` — the single SQL verdict |
`CrossBuy/deploy/scan-architecture.ps1` | `Endpoint-Inventory.csv`, `Permission-Coverage.csv`, `Controller-Inventory.csv`, `Worker-Inventory.csv`, `SQL-Script-Inventory.csv` |

Both print their headline numbers on every run, so a document can be checked against the tool in one command
instead of being believed.

---

## Defect 3 — SQL idempotency was scanned with a too-narrow regex, on the wrong encoding

**Claimed:** nine `deploy/sql` scripts were "non-idempotent" and unsafe to re-apply.

**Actually true:** **zero** scripts are unsafe to re-apply. All 110 are either guarded, predicate-idempotent, or
change nothing. One (`script.sql`, an unversioned scratch file at the repository root) is genuinely not re-runnable
and is now **excluded from deployment by name**, which is a different statement from "a deployable script is
broken".

Two independent causes:

**(a) The guard patterns were incomplete.** The scan looked for `IF OBJECT_ID(...) IS NULL` and `IF NOT EXISTS (`
and stopped there. It did not recognise:

- `WHERE NOT EXISTS (...)` / `AND NOT EXISTS (...)` on an `INSERT ... SELECT`;
- `IF COL_LENGTH(...) IS NULL` and `COLUMNPROPERTY(...)` for column adds;
- `IF DB_ID(...) IS NOT NULL`;
- `IF @variable IS NOT NULL` — a null-check on a looked-up id, which is how `hm_d38_branch_tax.sql` guards;
- and, most importantly, **predicate-idempotent DML**: `UPDATE t SET c = x WHERE c IS NULL` is perfectly
  re-runnable — the second run matches zero rows — and no textual guard pattern will ever see that.

**(b) Every file was read as UTF-8.** `deploy/sql/fresh/03_seed_config.sql` is **UTF-16LE**. Read as UTF-8 it
becomes mojibake, every keyword scan misses, and the file scanned as if it were empty. The manifest generator now
**detects the encoding** (BOM, plus a NUL-density check for BOM-less UTF-16) and records it per file.

**How this is prevented from recurring.** The generator does not widen its regex until nothing is flagged — that is
how a scanner starts lying. Instead it flags every mutating `GO`-batch with no recognised guard and **refuses to
call it safe**; a human reads it and records a verdict with evidence in the `$reviewLedger`. Anything flagged and
not in the ledger stays `review`, and the report command prints a warning telling you not to deploy it.

Result today: 88 `guarded`, 7 `guarded-by-predicate` (read and judged, with the evidence written down),
14 `no-mutation`, 1 `not-deployable`, **0 unread**.

---

## Defect 4 — actions with inline attributes were skipped entirely

Found while writing `scan-architecture.ps1`, before publishing any number from it.

The action-finding pattern was anchored as `^\s{1,8}public …`. This codebase's dominant controller style is:

```csharp
[HttpGet][InvPerm("read")] public async Task<IActionResult> WorkOrders()
```

— the attributes **inline, before `public`**. Those lines never matched, so **102 actions were absent from the
inventory altogether**. Not mislabelled: absent. Attributes are now gathered from two places and merged — the ones
inline on the declaration line, and the ones walked upward from the lines above it.

This is the same defect family as CORRECTION-001's defect 1 (an attribute-layout assumption), reached from the
other direction: there the walk aborted, here the member was never found.

---

## Defect 5 — only the FIRST class in each file was examined, and `partial` was ignored

Also found before publication, by reconciling the scanner's per-controller counts against an independent crude
count of public action-returning methods.

**(a) First class only — a NEW defect, caught before it published anything.** `AccountingController.cs` and
`CrmController.cs` declare helper types *before* the controller class. A first-match-only class scan found a
non-controller, bailed out, and recorded **zero actions for both files** — 197 real endpoints, including 76
mutating ones, silently missing. The reconciliation caught it immediately (two controllers at zero is not a subtle
signal), and the scanner now selects the class whose name ends in `Controller`, preferring the one matching the file
name. **This defect never reached a published number**; it is recorded because it is the same failure shape as the
others and because the reconciliation step is what caught it.

**(b) `partial` ignored — this one DID affect the original discovery.** `AdminController` is split across four
files, and its class-level `[SessionValidation]` sits on one of them. Read file-by-file, three of the four files'
actions are attributed to a class with no attributes and, worse, most of them are never associated with
`AdminController` at all. Class attributes and action sets are now merged **by controller name across all files** in
a first pass. `AdminController`'s unguarded mutating count moves from 24 to **42** because of this alone.

---

## Corrected measurements

Regenerated by `CrossBuy/deploy/scan-architecture.ps1` on 2026-08-03. The "CORRECTION-001" column is what that
document published after fixing the first two defects; the third column is after fixing defects 4 and 5.

| Metric | Original discovery | CORRECTION-001 | **Corrected (Batch B)** |
|---|---|---|---|
Controller files scanned | 38 | 39 | **42** |
Distinct controller classes | 38 | 39 | **39** |
Controller actions | 1002 | 1012 | **1054** |
Mutating actions (POST/PUT/DELETE/PATCH) | not measured | 362 | **381** |
— with an **action-level** module permission | — | 95 | **75** |
— with a module permission at **any** level (action or class) | — | not measured | **76** |
— **without** a module permission at any level | — | **267** | **305** |
— without an action-level module permission | — | 267 | **306** |
Mutating actions with anti-forgery | — | 317 | **323** |
MVC (non-API) mutating actions without anti-forgery | — | 45 | **37** |
SQL scripts | 107 | 107 | **110** |
— unsafe to re-apply | 9 | 9 | **0** (1 excluded from deploy by name) |
Background workers | 6 | 6 | **6** |
— behind the single-worker-process gate | 0 | 0 | **6** |
— hardcoding `CompanyId = 1` | 4 | 0 | **0** |

Three of those movements need saying plainly rather than being left in a table.

### The Stage 1 security backlog is 306, not 267 — and here is exactly where the 39 came from

| Controller | CORRECTION-001 | Batch B | Δ | Why |
|---|---|---|---|---|
`AdminController` | 24 | **42** | +18 | **Defect 5b.** Split across four `partial` files; only one file's actions were being attributed to it. |
`AccountingApiController` | 0 | **10** | +10 | API controllers were absent from the published backlog table. |
`AiController` | 0 | **4** | +4 | as above |
`LeaveApiController` | 0 | **2** | +2 | as above |
`HrApiController` | 0 | **2** | +2 | as above |
`NotificationsApiController` | 0 | **2** | +2 | as above |
`BusinessEventMonitorController` | 0 | **1** | +1 | **New in Batch B** — see the note below. |
| **Total** | **267** | **306** | **+39** | |

Every other controller is **unchanged**, including `AccountingController` (44) and `CrmController` (32). Defect 5a
made those two read as zero in the *new* scanner and was fixed before publication; it never moved a published
number.

The instruction to keep this as a risk-ranked Stage 1 backlog rather than remediating it in Stage 0 is unchanged.
The backlog is simply bigger than it was reported to be, and 20 of the 39 are API endpoints that a UI-focused scan
never listed.

### `BusinessEventMonitorController.Retry` is in that count, and that is a measurement artefact

Its guard `[PlatformOps]` is applied at the **class** level, so an action-level count records it as unguarded. It is
not: every action on that controller is gated, and `Retry` additionally carries `[ValidateAntiForgeryToken]`. This
is why the corrected table reports **both** bases — action-level (306) and any-level (305) — instead of quietly
picking the one that flatters. For a class-gated controller, action-level counting is the wrong measure; for a
controller where only *some* actions carry `[AccPerm]`, it is the right one. Both numbers are published so neither
can be mistaken for the other.

### "With a module permission" went DOWN, from 95 to 75 — nothing was removed

The earlier figure counted class-level `[Authorize]`/`[SessionValidation]` inheritance as permission coverage. The
merged pass now separates **authentication** (you are logged in) from **module permission** (you may perform this
operation), and counts only the latter, because only the latter is a permission. `[SessionValidation]` on a
controller says nothing about *what* the session may do.

---

## CORRECTION-001 is re-verified, not merely restated

`scan-architecture.ps1` re-checks the withdrawn finding on **every run** and prints the result, naming the nine
actions explicitly:

```
CORRECTION-001 re-check — Manufacturing work-order write actions found: 9 of 9 expected
    CreateWorkOrder          POST   perm=InvPerm("doc")   antiforgery=True
    AddWorkOrderLabor        POST   perm=InvPerm("doc")   antiforgery=True
    RemoveWorkOrderLabor     POST   perm=InvPerm("doc")   antiforgery=True
    SaveWorkOrder            POST   perm=InvPerm("doc")   antiforgery=True
    ReleaseWorkOrder         POST   perm=InvPerm("doc")   antiforgery=True
    CancelWorkOrder          POST   perm=InvPerm("doc")   antiforgery=True
    CompleteWorkOrder        POST   perm=InvPerm("doc")   antiforgery=True
    ProducePartial           POST   perm=InvPerm("doc")   antiforgery=True
    GeneratePlanWorkOrders   POST   perm=InvPerm("doc")   antiforgery=True
```

If any of the nine ever loses a guard, the script prints `CORRECTION-001 IS NO LONGER TRUE` in red. If one is
renamed away, it prints `NOT FOUND as a mutating action` rather than quietly reporting a smaller, still-green set.

**The nine are named, not pattern-matched.** A first attempt filtered on `action -match 'WorkOrder'` and confidently
reported **eight of nine** — `ProducePartial` mutates a work order without the word in its name. A name filter that
silently returns a smaller all-green set is the same failure as the defects above, so the list is explicit and the
expected count is asserted.

Independently, `CrossBuy.Tests/Slice3ManufacturingSecurityTests.cs` (13 tests) asserts the same attributes off
**compiled metadata**, which no source-formatting change can fool.

---

## Documents affected by these corrections

| Document | Change |
|---|---|
`13-Security-Authorization-Isolation.md` | mutating-action counts updated 362 → 381, unguarded 267 → 306; the two missing controllers named |
`17-Test-Quality-Map.md` | test count updated to the Batch B suite (230) |
`18-Deployment-Operations-Map.md` | SQL verdict replaced with the manifest projection (0 unsafe); manifest, schema history and the single-worker control added |
`19-Current-Gaps.md` | Stage 1 backlog restated at 306; the SQL-idempotency gap closed as "never real" |
`20-Architecture-Risks.md` | the SQL re-apply risk withdrawn; multi-process worker risk downgraded (now enforced) |
`21-Modernization-Roadmap.md` | Stage 1 backlog size corrected |
`evidence/Endpoint-Inventory.csv`, `Permission-Coverage.csv`, `Controller-Inventory.csv`, `Worker-Inventory.csv`, `SQL-Script-Inventory.csv` | regenerated by `scan-architecture.ps1` |
`evidence/SQL-Script-Inventory.csv` | now a **projection of `manifest.json`**, so the SQL verdict has one derivation instead of two |

Carried forward unchanged, and deliberately: `Business-Object-Capabilities.csv`,
`Module-Dependency-Matrix.csv`, `Approval-Silo-Matrix.csv`, `Entity-Inventory.csv`, `DbSet-Inventory.csv`,
`Service-Inventory.csv`, `Screen-Inventory.csv`, `Hub-Filter-Middleware-Inventory.csv`,
`Event-Producer-Consumer-Matrix.csv`. These involve judgement (what counts as a capability; which dependency is
real) or were untouched by the five defects. Mechanically re-deriving them would trade reviewed content for fresh
risk — and this document is a record of what fresh scanner risk costs.

---

## Lesson recorded

Three of the five defects share one shape: **a pattern that silently returns a smaller, plausible answer**. The
regex that aborted a walk, the anchor that skipped a line, the class match that gave up on a file, the name filter
that found eight of nine — none of them errored. Each produced a confident, self-consistent, wrong number.

So the standing rule for any measurement that a decision rests on:

1. **Keep the derivation.** A discarded scanner's output is an unverifiable claim.
2. **Reconcile against an independent count** before publishing. Defects 4 and 5 were both caught this way,
   by comparing per-controller totals against a crude grep — not by re-reading the regex.
3. **Assert the expected shape, not just the found set.** "9 of 9 expected" catches what "found 8" hides.
4. **Never widen a detector until it stops complaining.** Flag it, read it, and record the verdict with evidence.