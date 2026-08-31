# HR Product Batch 2 — Roster & Shift Management: owner handoff

Authored by TAB-2. Everything below is work this batch could not perform because the path belongs to
another owner, or because performing it would have required inventing a decision that is not TAB-2's
to make. Each item says exactly what to do, and none of them is a request to design something.

---

## 1. TAB-1 — the three resource files (mechanical: move three finished files)

`CrossBuy/Resources/**` is TAB-1-owned, so the roster views could not carry their own resources.

The views use the canonical `IViewLocalizer` mechanism — the retired inline `T(ar, en)` deviation was
deliberately **not** reintroduced. The three resx files are **already written, complete and
translated** (73 keys, Arabic and French both real translations rather than English echoes):

```
docs/handoff/roster-resources/Index.ar.resx   ->  CrossBuy/Resources/Views/Roster/Index.ar.resx
docs/handoff/roster-resources/Index.en.resx   ->  CrossBuy/Resources/Views/Roster/Index.en.resx
docs/handoff/roster-resources/Index.fr.resx   ->  CrossBuy/Resources/Views/Roster/Index.fr.resx
```

`CrossBuy.Tests/RosterLocalizationTests.cs` already resolves the **canonical** path first and falls
back to the staged one, so it passes today and begins guarding the real files the moment they move —
no edit to the test is needed.

**Until the move, both roster views render English keys under an Arabic UI.** That is the one known
user-visible gap in this batch, and it closes in a single commit.

> `Views/Roster/Mine.cshtml` shares `Index.*.resx` only if the view-localizer is configured per
> folder. If the convention is strictly per-view, split the same key set into `Mine.*.resx` — every
> key it needs is already present in the staged files.

---

## 2. TAB-1 — `EntityRegistry`, and only then a `Roster.Published` business event

§11 named `Roster.Published` as the one transition worth an event. It is **not raised**, and that is
a blocked item rather than a judgement that it is unwanted.

`CrossBuy/BL/Platform/EntityRegistry.cs` is TAB-1-owned, and the platform contract is explicit that a
producer may only raise an event whose `EntityCode` is a frozen registry code. Raising
`Roster.Published` without the registry entry would fail validation; adding the entry from here would
edit a denied file.

**What TAB-1 would need to add:**

```csharp
public const string Roster = "Roster";   // EntityRegistry codes
```
plus a definition with `PermissionScope = ScopeHr`.

**What TAB-2 would then add** (one call, inside the existing `PublishAsync`, immediately before
`SaveChangesAsync`, no swallowing catch, per ADR-001):

```csharp
await _events.RecordAsync(new BusinessEventRecord {
    EntityCode = EntityRegistry.Roster,
    EntityId   = period.ID,
    EventType  = "Roster.Published",
    DedupKey   = $"roster-published-{period.ID}",
}, ct);
```

`Roster.Published` is the only transition proposed. Assignment edits are deliberately **not** events:
a roster is edited dozens of times while it is drafted, and an event per grid edit is the noise §11
warns against. The evidence trail for published changes is already covered by
`RosterAssignmentRevisions`, which is a better fit — it records *what* changed, not merely *that*
something did.

---

## 3. Calendar owner — a projection needs a source discriminator first

§8 permits projecting a published roster into Calendar **only if it fits the existing architecture
cleanly**. It does not, and the reason is one missing column.

`CalendarEvent` has `Id, CompanyID, Title, Description, Location, AllDay, StartAt, EndAt, Scope,
OwnerEmpId, DeletedAt` — and **no source/origin field**. Consequences of projecting anyway:

- re-publishing a period would either duplicate every event or require deleting by fuzzy title/date
  match, which is a guess;
- a user editing a projected event would silently diverge from the authoritative roster, with no way
  to detect that it had happened;
- removing an assignment would leave an orphan event nothing can find.

**Minimum change that would make a projection safe:** `SourceType nvarchar(50) NULL` +
`SourceId int NULL` on `CalendarEvents`, with a unique index on
`(CompanyID, SourceType, SourceId)` filtered to `SourceType IS NOT NULL`. That gives the projection
an idempotency key, so re-publish becomes an upsert and cleanup becomes a delete-by-source.

Roster stays authoritative either way. No calendar rows are written by this batch.

---

## 4. Reporting — the conflicts dataset, and why it is not here

Two roster datasets ship in this batch, on the existing engine, registered exactly like Accounting,
Inventory and CRM (`roster.schedule`, `roster.planned-vs-actual`). Between them they cover five of
§10's six shapes: scheduled hours, coverage, planned-vs-actual, absence, overtime candidates.

**Conflicts is deliberately not a dataset.** A conflict is not a stored fact — it is what
`RosterService.DetectAsync` computes by comparing an assignment against every other live assignment
and against approved leave. Expressing that as a report query would be a *second implementation of
the same rule*, in SQL, with no test holding the two in agreement; the day they diverged, the screen
and the report would each insist the other was wrong.

**If a conflicts dataset is genuinely wanted**, the safe route is not a parallel query. It is to
persist detection output as a first-class artefact — a `RosterConflictSnapshot` written at publish
time by the same service that detects — and report from that. Then there is still one implementation
of the rule, and the report is reading a recorded decision rather than re-deriving one.

---

## 5. Integration Owner — shared files and the manifest

- `CrossBuy/Program.cs` (SHF-01) — appended in TAB-2's own region only: two `AddScoped`
  registrations, two `IReportDataSource` registrations, and one `MapPermission` line for
  `roster.reports.view`.
- `CrossBuy/Models/Context/CrossDbContext.cs` (SHF-02) — four DbSets appended in TAB-2's region.
- `CrossBuy/BL/Reporting/ReportingRegistration.cs` — TAB-2-owned; dataset + data-source registrations
  added beside the existing modules.
- `CrossBuy/deploy/sql/hr_roster.sql` — new canonical HR slice, **not executed**. Needs manifest
  regeneration by the Integration Owner (generated output is never hand-edited).

---

## 6. Authorization — nothing to reconcile, but worth recording

No new `HrAction` was invented. Roster management reuses `attendance-manage`; own-roster reads reuse
`employee-view`, which `HrAccessService` already answers self-only above the role rules.

**A bootstrap note, stated rather than left to be discovered:** `attendance-manage` *is*
bootstrap-open — only `payroll-manage` and `confidential-view` are permanently excluded. So on an
install with no HR roles configured, roster management is exactly as open as attendance
administration already is. That is inherited posture, not new exposure, and reusing the existing
action is what kept it from becoming a new decision. If an owner wants rostering to be *less* open
than attendance, that is a `NeverBootstrapOpen` entry plus a new action — a TAB-1 change, and a
deliberate one rather than a side effect of this batch.

---

## 7. TAB-1 — authorization baseline descriptive counts (this is what blocks the atomic commit)

`engineering/authorization-baseline.json` is TAB-1-owned. The new `RosterController` adds four
mutating endpoints, so the **descriptive** counts in its frozen header move. These are measurements,
not debt:

| frozenBaseline      | at `dd112cc` | with this batch | delta |
|---------------------|--------------|-----------------|-------|
| `mutating`          | 430          | 434             | +4    |
| `inBodyProtected`   | 179          | 183             | +4    |
| `attributeProtected`| 157          | 157             | 0     |
| `gaps`              | 94           | 94              | **0** |
| controllers         | 48           | 49              | +1    |

**Authorization debt does not increase.** `mutating` and `inBodyProtected` move by the same +4,
because all four new endpoints are gated in-body — every one of them calls `HrAccessService` before
touching the service.

Until this lands, five `CrossBuy.Analyzers.Tests.ReconciliationTests` fail. That was verified by
attribution rather than assumed: with `RosterController.cs` and `Views/Roster/` moved aside, the
suite is 16/16 green with 0 build errors.

**This is the single reason Batch 2 does not commit itself.** Committing TAB-2's files alone would
leave master red until TAB-1's reconciliation arrived. The two changes belong in one commit, exactly
as HR Product Batch 1 landed at `578c471`.
