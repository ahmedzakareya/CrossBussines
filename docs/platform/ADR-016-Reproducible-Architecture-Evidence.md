# ADR-016 — Architecture metrics come from checked-in scanners, and a measurement that cannot be re-run is not evidence

**Status:** Accepted, implemented in Stage 0 (Slice-003) Batch B.

## Context

The as-built architecture discovery produced 23 documents and 14 evidence CSVs. It was produced by scanners that were
**run and then discarded**.

Five defects have since been found in those measurements. One of them —
[CORRECTION-001](../architecture/CORRECTION-001-Manufacturing-Permission-Finding.md) — published a **false Critical
security finding** ("work-order writes carry no permission attribute … privilege escalation … the highest-severity
authorization finding in the system") that drove the priority order of the modernization roadmap. All nine of those
actions had carried `[InvPerm("doc")]` and `[ValidateAntiForgeryToken]` the whole time. Another understated the Stage 1
security backlog by 13% (267 against a true 306).

The full list is in [CORRECTION-002](../architecture/CORRECTION-002-Evidence-Scanner-Defects.md). What matters here is
their **shape**, because it is the same shape four times out of five:

> A pattern that silently returns a smaller, plausible, self-consistent answer.

- A regex requiring one attribute per line, which aborted an upward walk instead of erroring.
- An anchor requiring `public` at line start, which skipped 102 actions whose attributes were inline.
- A class match taking the first class in the file, which recorded zero actions for two whole controllers.
- A name filter (`action -match 'WorkOrder'`) that confidently reported **eight of nine** work-order writes, because
  `ProducePartial` mutates a work order without the word in its name.

None of them threw. None produced an obviously wrong number. Each produced a confident, wrong one.

## Decision

**Every architecture metric that a decision rests on is produced by a checked-in, re-runnable script that prints its
headline numbers.**

| Script | Produces |
|---|---|
`CrossBuy/deploy/scan-sql-manifest.ps1` | `deploy/sql/manifest.json` — the single SQL verdict |
`CrossBuy/deploy/scan-architecture.ps1` | `Endpoint-Inventory.csv`, `Permission-Coverage.csv`, `Controller-Inventory.csv`, `Worker-Inventory.csv`, `SQL-Script-Inventory.csv` |

Four rules govern them.

### 1. Keep the derivation

A discarded scanner's output is an unverifiable claim. Checked in, it can be reviewed, diffed, re-run against a later
commit, and **corrected** — which is the one thing the original scans could not be.

### 2. Reconcile against an independent count before publishing

Defects 4 and 5 were both caught this way: by comparing the scanner's per-controller action counts against a crude
`grep` for public action-returning methods, **not** by re-reading the regex. Two controllers at zero is not a subtle
signal; the reconciliation surfaced it in one run.

This is now a step, not a habit. Before any number from these scripts is published, it is reconciled against a
differently-derived count.

### 3. Assert the expected shape, not just the found set

`scan-architecture.ps1` re-verifies CORRECTION-001 on **every run**, against an **explicit list of the nine action
names**, and prints `found: 9 of 9 expected`. If one is renamed away it prints `NOT FOUND as a mutating action`; if one
loses a guard it prints `CORRECTION-001 IS NO LONGER TRUE` in red.

"Found 9 of 9 expected" catches what "found 8" hides. This directly answers the name-filter defect, which was
introduced *while writing this very check*.

### 4. Never widen a detector until it stops complaining

The SQL generator flags every mutating `GO`-batch with no recognised guard and **refuses to call it safe**. A human
reads it and records a verdict with evidence in a review ledger; the ledger can only *downgrade* the alarm for a file
someone actually looked at. Anything flagged and unread stays `review`, and the report command prints "do not deploy".

Widening the regex until the warnings stop is how a scanner starts lying — and it is what produced the "nine
non-idempotent scripts" claim, where the honest answer was "seven of these are predicate-idempotent and no textual
guard will ever see that".

### 5. One derivation per fact

`SQL-Script-Inventory.csv` is now a **projection of `manifest.json`**, not a second scan. Two derivations of the same
fact will disagree, and the disagreement will be discovered by someone who trusts the wrong one.

### 6. Self-checks inside the tooling

The manifest generator throws if duplicate-name grouping collapses to a single group — because that is exactly what it
did once, silently, reporting "1 duplicate name" for 109 scripts when the answer was 4. (Cause: `Group-Object` cannot
read properties off an `OrderedDictionary`; it groups everything under one empty key and does not error. Entries are
now `[pscustomobject]`.) A tool that can fail silently must be made to fail loudly about itself.

### 7. What is NOT regenerated, and why

`Business-Object-Capabilities.csv`, `Module-Dependency-Matrix.csv`, `Approval-Silo-Matrix.csv`, `Entity-Inventory.csv`,
`DbSet-Inventory.csv`, `Service-Inventory.csv`, `Screen-Inventory.csv`, `Hub-Filter-Middleware-Inventory.csv`,
`Event-Producer-Consumer-Matrix.csv` are **carried forward unchanged**.

They involve judgement (what counts as a capability; which dependency is real) or were untouched by the five defects.
Mechanically re-deriving them would trade reviewed content for fresh scanner risk — and this ADR exists because we now
have a measured price for fresh scanner risk.

## Consequences

**Corrected numbers, published with their history.** The corrections table in CORRECTION-002 shows every metric's
original, intermediate and current value, so a reader can see what moved and why rather than encountering a silently
different number.

**Both coverage bases are published.** Mutating actions with no **action-level** module permission: 306. With none at
**any** level (action or class): 305. `BusinessEventMonitorController.Retry` is the difference — it is gated by
`[PlatformOps]` on the class. Publishing one number would mean silently choosing the flattering or the alarming one;
publishing both means neither can be mistaken for the other.

**The scripts must be saved with a UTF-8 BOM.** Windows PowerShell 5.1 reads a BOM-less `.ps1` as ANSI, and the
em-dashes and Arabic strings in these files then break string literals at parse time. Noted here because it is a real
trap for the next person editing them.

**A regression in a corrected fact now fails visibly.** CORRECTION-001 is re-verified on every scan run, and
independently by 13 tests asserting the same attributes off **compiled metadata** — which no source-formatting change
can fool.

## Alternatives rejected

**Trust the original CSVs and correct them by hand.** Rejected: the errors were systematic, not clerical. A hand
correction fixes the instances you notice.

**Roslyn-based analysis instead of text scanning.** Genuinely better, and rejected for this stage on cost: it needs a
compilation of a 218-DbSet project with its full reference set. Text scanning plus reconciliation plus compiled-metadata
tests in the suite covers the same ground for this purpose. Worth revisiting if the metrics ever gate CI.

**A CI gate on the numbers.** Rejected as premature: with 306 unguarded mutating actions, a gate would fail every build
until Stage 1 finishes. The scripts print; humans read; the numbers move in a document with a date on them.

**Deleting the withdrawn findings.** Rejected. A withdrawn finding is struck through and left in place with a pointer
to the correction — the roadmap was reprioritised on the strength of A1/R1, and that reprioritisation is only
comprehensible if the withdrawal is visible.