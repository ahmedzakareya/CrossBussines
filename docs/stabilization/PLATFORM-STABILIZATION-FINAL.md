# Platform Stabilization — Final Report

**Starting HEAD** `dea7bf3` · **Final HEAD** see §Commits · **Pushed** no

---

## 1. Fixed defects

### `InventoryApprovalService` — found, fixed by its owner, my duplicate withdrawn
A recovered test (`Stage1ContextTests`) found a hardcoded company across **twelve** call sites, five
of them writes, on a production HTTP path: a manager in company 41 who approved a write-off created
it in **company 1's** stock and ledger.

I wrote a fix and then withdrew it. Fast-forwarding revealed the file is dirty in the shared tree
and another tab had already fixed the same defect **better**: they split reads/submissions (resolve
from `BusinessContext`, throw when unresolved) from approve/reject (**use the approval row's own
`CompanyID`**, because the payload was captured under that company's warehouses and vendors). I had
not considered the replay direction; mine would have replayed a payload against the approver's
company. Landing mine would have destroyed theirs and replaced it with the weaker design.

**Net effect of this batch on that defect: the finding and the ownership entry, not the code.**

### Two recovered guards were themselves defective
* `Slice3WorkerCompanyTests` scanned worker source for the constant that was **removed** and matched
  the comment explaining the removal. Comments are stripped now.
* `Correction005StructuralTests` demanded that removals be recorded rather than absorbed — and was
  right: four more constants had gone since its baseline. 12 → 8, with the four named.

Both had the same fault, and it is worth naming: **a guardrail that fails on the documentation of a
fix teaches people to delete the documentation.**

## 2. Recovered tests

**66 suites, 1 200 tests.** Clean-clone execution goes 2 540 → **3 740**, zero failures. Full evidence in `CLEAN-CLONE-TEST-PROOF.md`; per-file classification in
`MISSING-TEST-INVENTORY.md`.

```
Original machine-only test files : 106
  Class A recovered              :  66
  Class B duplicate/superseded   :   0   (measured: no name collision with tracked tests)
  Class C obsolete               :   2
  Class D experimental/local     :   0   (measured: none is scratch or generated)
  Class E owner-decision/blocked :  38
                                  ----
                                   106  ✅
```

Nothing was bent to make it pass, and nothing was committed to inflate a count.

## 3. Rejected and blocked tests

* **Obsolete (2)** — target the superseded `CrossBuy.BL.Comm` outbox. Canonical HEAD uses
  `CommNotificationDispatcher` instead. Resurrecting the old store to make them compile is exactly
  what the brief forbids.
* **Blocked (38)** — 5 need an untracked Construction production slice; 13 need constructor-call
  adaptation after signature drift; 19 compile and run but assert contracts canonical HEAD does not
  meet, most notably `AccessDeniedGuardTests` (needs an untracked view) and
  `CompanyDefaultParameterGuardTests` (needs the untracked `DevSeedFixture.cs`).

## 4. Remaining technical debt

1. **Four UNKNOWN company-1 surfaces**, 228 references between them —
   `AccountingController` (185), `AdminController` (24), `Api/InventoryApiController` (10),
   `CurrencyController` (8). Each needs a per-endpoint reading; deliberately not guessed at.
2. **13 signature-drift suites** — the strongest remaining recovery candidates.
3. **Untracked production code**: `DevSeedFixture.cs`, `Views/Account/AccessDenied.cshtml`,
   `EmployeeNames.cs`, `ModulePermAttributes.cs`, two Workspace views. Canonical HEAD builds without
   them, but two of them are remediations that make the developer tree better than the repository.
4. **One uncovered mutation** — see §7.

## 5. Product decisions required

* Commit or abandon the untracked **Construction** slice (unblocks 5 suites).
* Commit or abandon **`DevSeedFixture.cs`** and the 69 SHF-28 parameter defaults it names.
* **Communication F-1**: name the requirement, or retire the label. See
  `COMMUNICATION-F1-RESOLUTION.md`.

## 6. External blockers

ZDR grant · DPA · required signatures · AccountOwner · LegalEntity — none satisfied, none faked.
Engineering readiness is tracked separately and is green.

## 7. Mutation results

Twelve required mutations, each applied to the stabilization candidate and reverted immediately.

| # | Mutation | Killed by |
|---|---|---|
| 1 | company fallback restored to 1 | proven on the withdrawn fix (3 tests); **the surviving proof belongs to the owning tab's version** |
| 2 | worker company scope removed | **compilation fails** — the runner call is asserted by name |
| 3 | adapter ignores supplied `BusinessContext` | **NOT KILLED** — see below |
| 4 | GL period predicate → literal `"Closed"` | `PeriodPostingChokepointTests`, `AccountingPeriodCloseTests` |
| 5 | stock period predicate → literal | `PeriodPostingChokepointTests` × 2 (incl. the behavioural SoftClosed case) |
| 6 | period setter back on the interface | **compilation fails** |
| 7 | period setter on the concrete class only | `PeriodPostingChokepointTests.The_CONCRETE_period_service…` |
| 8 | uncovered public member on the document authority | `DocumentAuthorityMemberTests`, `DocumentLifecycleUiTests` |
| 9 | expiry idempotency keyed by document id only | `PlatformDocumentTests.A_renewed_document_may_warn_again…` |
| 10 | document UI computes expiry itself | `DocumentLifecycleUiTests` × 2 |
| 11 | per-document authorization removed from the listing | `DocumentLifecycleUiTests.A_row_the_caller_may_not_see…` |
| 12 | Client Portal customer-id substitution | `ClientPortalSecurityTests.Customer_A_never_sees_customer_B…` |

**Mutation 3 was not killed, and that is reported rather than reworded.** Replacing two
`context.CompanyId` reads with `1` inside `PlatformPermissionProvider` left 81 permission and
isolation tests green. Either those call sites are unreached by any test, or the surrounding
assertions do not distinguish the company. It is a real coverage gap in the platform permission
provider and belongs in the next batch — not a reason to claim eleven of twelve as twelve.

## 8. Security and ownership delta

```
mutating 439 · attributeProtected 156 · inBodyProtected 189 · gaps 94 · controllers 53
                                                    — all UNCHANGED, gap-set delta ZERO
CBA001–CBA006 all 0
```

No new gap; no reclassification. Ownership: 66 recovered suites assigned by the surface they
protect (TAB-1 32, TAB-2 14, TAB-5 11, TAB-6 2); **SHF-31** for the two TAB-6 approval suites this
batch had to touch; **SHF-32** for `InventoryApprovalService`, which had no owner at all.
Regenerated twice, byte-identical, `-Strict` passes for all four tabs.

## 9. Database

```
DDL executed        0
CrossBuyDev writes  0
production writes   0
```

Every recovered test was scanned before landing: no production connection string, no Azure host, no
credential, no token, no absolute developer path. The two apparent hits are protective — an
unreachable localhost server proving a gate fails closed, and a test asserting the seeder **refuses**
production-looking database names.

## 10. Foreign work

Integration ran in a dedicated worktree; the shared tree was never written to. 3 stashes intact, 12
worktrees intact, no reset, no destructive clean, no stash dropped.

---

## 11. Next-phase readiness — TASK MANAGEMENT + WORKFLOW + ESCALATION

Inventory only. Nothing below was implemented in this batch.

**Entities / services / controllers present at canonical HEAD**
`BL/TaskService.cs`, `BL/TasksAccessService.cs`, `BL/TaskGeneratorService.cs`,
`BL/TaskGeneratorHostedService.cs`, `BL/TaskScheduleMatcher.cs`, `BL/TaskScheduleMatchHostedService.cs`,
`BL/TaskLinkResolver.cs`, `BL/TaskCostService.cs`, `BL/TasksCalendar/**`,
`BL/TasksCalendar/TaskNotificationService.cs`, `Controllers/TasksController.cs`,
`Controllers/CalendarController.cs`, `Models/Context/Tasks/TaskEcosystem.cs`.

**Coverage that now exists in a clean clone** (recovered this batch, previously machine-only):
`TaskEcosystemTests`, `TasksCalendarAgendaTests`, `TasksCalendarIntegrationTests`,
`TasksCalendarTimeAndContractTests`, `TasksCalendarVisualConformanceTests`,
`TaskCalendarTimelinePresentationTests`, `TasksWithoutCommunicationTests`,
`CalendarAccessServiceTests`, `CalendarConflictTests`, `CalendarSchedulingTests`,
`CalendarDayGridViewModelTests`, `SqlServer/TaskScopeQuerySqlTests`. Already tracked:
`TaskEscalationRuleTests`, `TaskWorkerFoundationTests`, `WorkspaceAttentionTests`,
`WorkspaceMyWorkTests`, `WorkspaceAgendaOverdueTests`.

**MyWork / Attention** — Workspace composes Attention from five existing sources.
`WorkspaceAttentionTests` and `WorkspaceMyWorkTests` hold the composition and the `"mine"` employee
scoping. Document expiry converges through this taxonomy; **no sixth source exists and none may be
added.**

**Routing / auto-rules** — `TaskAutoRule.DefaultAssigneeEmployeeId` is the platform's assignment
authority. Central Documents already routes through it rather than guessing an HR manager.

**Manager hierarchy** — `IOrgHierarchy`, consumed by `HrAccessService` and `CrmAccessService`.

**Escalation today** — `TaskEscalationRuleTests` (24 tests, tracked) covers the rule engine.
`AbandonedClaimRecoveryTests` and `SqlServer/AbandonedClaimReaperConcurrencyTests` (both recovered)
cover claim recovery.

**Integrations already present** — Calendar (`CalendarService`, conflict and scheduling rules);
approvals (`ApprovalInboxService` + the module readers under SHF-14/15); communication and
notification (`ICommNotificationDispatcher`, `TaskNotificationService`); files and comments
(`_EntityConversation`, the Central Document platform); Business Events
(`BusinessEventService`, `EntityRegistry`, dispatch worker).

**Company isolation** — `TasksAccessService`, `WorkerCompanyScope` for the two task hosted services,
and `SqlServer/TaskScopeQuerySqlTests` for the query shape.

**Missing pieces, from evidence rather than ambition**
1. `TasksCalendarTransitionAndEventTests` is **blocked** (E2 signature drift) — the transition and
   event coverage a workflow phase would build on is not yet in a clean clone.
2. No workflow *state machine* exists: transitions live in `TaskService` rather than in a declared
   model.
3. Escalation has rules but no scheduled escalation runner distinct from the generator worker.
4. Mutation 3's coverage gap sits in the permission provider these paths depend on.
