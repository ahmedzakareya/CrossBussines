# Stage-Construction-CR01 — BOQ Stable Identity: Evidence

**Severity:** Critical · **Status:** remediated and mutation-proved in C1

---

## 1. The defect, exactly

`BL/BoqService.ReplaceAllAsync` — the only path the BOQ editor used — ran:

```csharp
var existing = await _db.BoqItems.Where(b => b.CompanyID == companyId && b.ProjectId == projectId).ToListAsync();
if (existing.Count > 0) { _db.BoqItems.RemoveRange(existing); await _db.SaveChangesAsync(); }   // DELETE EVERY LINE
…
foreach (var r in rows) { var e = new BoqItem { … }; _db.BoqItems.Add(e); await _db.SaveChangesAsync(); }  // NEW IDS
```

Meanwhile `BL/ProgressBillingService.PreviouslyBilledAsync` keys previously-billed value **by `BoqItemId`**:

```csharp
where h.CompanyID == companyId && h.ProjectId == projectId && h.Status == "Posted" && h.ID != exceptBillingId
select new { l.BoqItemId, l.PeriodValue }
…
return rows.GroupBy(x => x.BoqItemId?.ToString() ?? "null") …
```

**The consequence.** One BOQ re-save gave every line a new identity. The next certificate looked up
previously-billed value by the *new* id, found nothing, and billed work that had already been certified and posted
**a second time**. Nothing warned: from the database's point of view the old ids simply no longer existed.

Two further exposures in the same class:

- `DeleteItemAsync` checked only for **child items** — a certified line could be deleted outright.
- A contractual quantity or rate could be changed on any line at any time, with no revision and no audit.

## 2. Why it was reachable

`Controllers/ProjectController.SaveBoq` posts the whole editor to `ReplaceAllAsync` and is gated on
`ProjectsActions.BudgetManage` — a right a commercial user legitimately holds. So this was not a theoretical path;
it was the normal way to edit a BOQ.

## 3. The remediation

`ReplaceAllAsync` no longer deletes anything. It maps the posted rows onto existing lines (by id where the caller
supplies one, else by code, else by description) and delegates to the new **`SaveLinesAsync`**, which performs a
*difference*:

| Case | Behaviour |
|---|---|
| line identified by the caller | **updated in place — the id survives** |
| line omitted, not referenced | **retired**: `BoqLineStates.Status = 'Retired'` with `RetiredAt/By/Reason`. The `BoqItems` row stays. |
| line omitted, but referenced | **refused**, naming the line and what holds it |
| line new | inserted; exactly one new id |
| reorder | `SortOrder`/`ParentId` move; identity does not |
| contractual quantity/rate changed on a committed line | **refused** — requires a commercial revision or approved variation (CR-03) |
| duplicate code | refused before anything is written |
| id from another company/project | refused with the same message as "not found", so ids cannot be probed |

"Referenced" is computed by `ReferencesAsync` and names the holder: an approved/posted client certificate, a
confirmed measurement, a posted material issue, an active subcontract scope, or an approved variation.

**Nothing in `BoqService` deletes a `BoqItem` any more**, except `DeleteItemAsync` for a line that is provably
unreferenced — a check that class did not previously have.

All validation happens **before** the first write, so a refusal leaves the database untouched — which is what makes
"nothing was written" assertable rather than hoped for.

## 4. Tests (all in `CrossBuy.Tests/ConstructionC1BoqIdentityTests.cs`)

| # | Test | Asserts |
|---|---|---|
| 1 | `Replace_preserves_unchanged_line_ids` | three ids identical after a re-save, read from a new context |
| 2 | `Adding_a_line_creates_exactly_one_new_id` | `added == 1`, `retired == 0`, existing ids untouched |
| 3 | `Removing_an_unreferenced_line_retires_it_and_keeps_the_row` | row still exists; state `Retired`; reason stored; gone from the active BOQ |
| 4 | `Removing_a_certified_line_is_refused` | refusal names the line; state still `Active`; row still present |
| 5 | **`Previously_billed_work_cannot_become_billable_again_after_a_boq_edit`** | the **money** test — see §5 |
| 6 | `Reorder_does_not_change_identity` | `added == 0`, `retired == 0`, sort order changed, ids unchanged |
| 7 | `Duplicate_line_codes_are_rejected` | refused, and **zero** rows written |
| 8 | `A_line_from_another_company_cannot_be_updated_through_this_project` | refused; the foreign line is byte-for-byte untouched |
| 9 | `Changing_a_contractual_quantity_on_a_certified_line_requires_a_revision` | refused; rate unchanged |
| 10 | `Line_changes_are_audited_with_actor_reason_and_one_correlation_id` | actor, reason, one correlation id per save |

## 5. The money test, and why it is written the way it is

The naive assertion — "the certificate line still points at id X" — **passes even with the defect reinstated**,
because the certificate row is never touched by a BOQ re-save. It is the *BOQ side* that moves.

So the test asserts from the perspective of the query that actually loses the money:

```csharp
var idsAfter = await f.ActiveLineIdsByCodeAsync(project.ID);
Assert.Equal(ids["2"], idsAfter["2"]);                                   // identity survived
decimal billed = await f.PreviouslyBilledForAsync(project.ID, idsAfter["2"]);
Assert.Equal(1200m, billed);                                             // the NEXT certificate will see it
```

`PreviouslyBilledForAsync` is a deliberate copy of `ProgressBillingService.PreviouslyBilledAsync` — the real query.
If BOQ identity moves, the current line's id finds **0** previously billed, and the work is billable again.

## 6. Mutation proof C-01

The pre-C1 behaviour was reinstated inside `SaveLinesAsync`:

```csharp
_db.BoqItems.RemoveRange(existing);
_db.BoqLineStates.RemoveRange(states);
await _db.SaveChangesAsync(ct);
existingById.Clear(); stateByItem.Clear(); removed.Clear();
foreach (var mutantRow in input) mutantRow.Id = 0;
```

| Stage | Result |
|---|---|
| baseline | passed = 1, failed = 0 |
| **mutated** | **passed = 0, failed = 1** |
| restored (SHA-256 compared) | identical ✔ |
| after restore | passed = 1, failed = 0 |

Recorded in `mutation-proof-results.json`.

## 7. Residual items

| Item | Owner | Note |
|---|---|---|
| `BoqLineStates` backfill for existing rows | owner | Commented out in the DDL pending the mapping measurement. Not a correctness prerequisite: a missing state row is created on the next save. |
| `ClientContractId` on `BoqLineStates` | owner | Nullable until the D-01 backfill runs. |
| Folding the satellite into `BoqItems` | owner | A later consolidation once an ALTER can be scheduled. |
