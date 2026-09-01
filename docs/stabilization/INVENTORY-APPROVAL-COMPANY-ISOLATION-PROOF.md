# Inventory Approval — Company Isolation Proof

**Implementation provenance:** not TAB-1's. Written by another tab, headed *"STAGE 1 BATCH A"* in
its own source, and uncommitted in the shared working tree until `123a898`. All three files landed
byte-identical. TAB-1's contribution is the integration, the provenance record and the proof of the
write half.

## The design, and why the two directions differ

| Direction | Company source | Failure mode it prevents |
|---|---|---|
| reads · `RequiresApprovalAsync` · `SubmitAsync` | `BusinessContext`, via `GetCurrentAsync` which **throws** when unresolved | an approval filed against, or a threshold read from, a company nobody named |
| `ApproveAsync` · `RejectAsync` · all four replays | the **approval row's own `CompanyID`** | replaying a payload captured against company A's warehouses, vendors and items inside company B |

The withdrawn stabilization fix resolved *everything* from the request and would have done exactly
what the second row prevents.

## The 14 invariants

| # | Invariant | Proof |
|---|---|---|
| 1 | new submission belongs to the resolved company | `A_new_submission_belongs_to_the_resolved_company_and_never_to_company_one` |
| 2 | unknown company cannot create an approval | `An_unresolved_company_cannot_submit_and_writes_no_row` (throws; no row) |
| 3 | inbox reads cannot leak another company's approvals | foreign suite, 16 tests |
| 4 | detail reads cannot cross companies | scoped lookup + `An_approver_in_another_company_cannot_replay…` |
| 5 | approve uses the persisted `CompanyID` | `A_replayed_command_executes_under_the_approval_rows_company` |
| 6 | reject is company-scoped | same scoped lookup, asserted by the foreign suite |
| 7–10 | PO / Transfer / Count / Write-Off replay under the approval's company | one `[InlineData]` case each, observing the **company argument** passed to `IProcurementService` / `IStockService` |
| 11 | side effects stay in the approval's company | the argument *is* the side effect's company; the recording stubs throw on any other call |
| 12 | a company-B approver cannot execute company A's payload | `An_approver_in_another_company_cannot_replay_company_As_payload_at_all` × 4 — nothing executed, row still `Pending` |
| 13 | missing company context fails closed | throws `BusinessContextUnresolvedException`; nothing written, nothing replayed |
| 14 | no `?? 1` or company constant | `The_service_holds_no_company_constant_and_no_company_one_fallback` |

## The measured correction

Swapping `ap.CompanyID` for the resolved company in the four replay calls is **behaviourally
equivalent** — every behavioural test still passes; only the source assertion catches it.

That is a fact about the code, not a gap: `ApproveAsync` loads the row scoped to the approver's own
company, so the two values are provably equal by the time the replay runs.

**The active barrier is the scoped lookup.** Loosening `a.ID == id && a.CompanyID == companyId` to
`a.ID == id` fails four tests. Reading the company from the row is defence in depth: it keeps the
replay correct by construction if that barrier is ever weakened. The suite records which rule is
which, so nobody mistakes the second for the first.

## Coverage

13 new tests, all green. The foreign suite's 16 read tests and 6 aggregator tests continue to pass.
