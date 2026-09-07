# Stage-Tasks-Calendar-02 — Test Evidence (Phase 6 + verification)

**74 focused tests, 0 failures. Full application suite: 1472 passed, 0 failed, 183 skipped.**
Every number below was taken after a build that was separately verified at **0 errors**.

---

## 1. Verification run, in the order the owner specified

| # | Step | Result |
|---|---|---|
| 1 | `dotnet build CrossBuy.sln -c Debug` | **0 errors** |
| 2 | `dotnet build CrossBuy.sln -c Release` | **0 errors** |
| 3 | `dotnet build CrossBuy.sln -c TestRun` | **0 errors** |
| 4 | Focused Tasks/Calendar integration tests | **74 passed, 0 failed** |
| 5 | Full application suite | **1472 passed, 0 failed, 183 skipped** (1655 total) |
| 6 | DI + hosted-worker lifetime validation | **59 passed, 0 failed, 1 skipped** |

**Release is genuinely verified.** In the previous increment it could only be reported as "compiles, output copy
blocked by a running process". The owner stopped Visual Studio and IIS Express, and it now builds clean end to end.

A stale-`obj` failure appeared on the first Debug attempt (`MSB3030: staticwebassets.build.endpoints.json` not
found) — a leftover of Visual Studio being stopped mid-build. Resolved with `--no-incremental`; **no source file was
touched** to fix it.

## 2. The 74 focused tests

| File | Tests | Covers |
|---|---|---|
| `TasksCalendarIntegrationTests.cs` | 17 | notifications (11) + overdue sweep (6) |
| `TasksCalendarTimeAndContractTests.cs` | 24 | time model (13) + registry/event contracts (11) |
| `TasksCalendarAgendaTests.cs` | 16 | workspace agenda |
| **`TasksCalendarTransitionAndEventTests.cs`** | **17** | **new: transition wiring (11) + registration (4) + transactional publishing (2)** |

### 2.1 New this increment — transitions (Phase 1)

`Creating_a_task_publishes_created_and_assigned_and_notifies_the_assignee` ·
`Reassigning_publishes_reassigned_and_notifies_both_sides` ·
`Changing_the_due_date_publishes_the_change_with_both_values` ·
`Completing_publishes_status_changed_and_completed_and_notifies_the_creator` ·
`Reopening_publishes_reopened_and_notifies_the_assignee`

**Negative paths — the ones that matter most:**
`A_rejected_save_writes_no_event_and_no_notification` ·
`A_refused_transition_publishes_nothing_and_notifies_nobody` ·
`Setting_the_same_status_again_changes_nothing` ·
`Saving_with_no_change_publishes_no_transition_event_and_notifies_nobody`

### 2.2 New this increment — transactional publishing (Phase 3)

`An_event_and_its_state_change_are_one_commit` ·
**`No_event_survives_a_rolled_back_outer_transaction`** ·
`Every_published_event_carries_company_entity_actor_and_a_utc_instant` ·
`No_task_event_payload_carries_the_description_or_commercial_data`

Both transaction tests use the **real** `BusinessEventService`. A stub would have recorded the call and proved
nothing about the commit.

### 2.3 New this increment — registration (Phase 2)

`Task_and_CalendarEvent_are_registered_with_the_intended_capabilities` ·
`An_unknown_entity_code_is_still_refused` ·
`The_record_picker_contract_is_unchanged_by_this_registration` ·
`A_registered_event_name_validates_and_a_mismatched_one_does_not`

## 3. Phase 6 — test fixtures

`Employee` declares **ten** non-nullable string columns. The TAB 4 seed helpers now populate all ten honestly
(`FirstName`, `LastName`, `FullName`, `Address`, `PhoneNumber`, `Email`, `ProfileImage`, `Gender`, `MaritalStatus`,
`UserId`).

**No database constraint was weakened, no production `Employee` behaviour was changed, and no other tab's test was
modified or deleted.** The fix is confined to the two TAB 4 fixtures (`TcFixture`, `TransitionFixture`).

## 4. One test assumption corrected

`A_refused_transition_publishes_nothing_and_notifies_nobody` originally used `New → Done` as the "refused"
transition. That transition is **allowed** (`New → {InProgress, Done}`), so the test proved nothing. It now uses
`Done → New`, which the map genuinely refuses. A test that cannot fail is not a guard.

## 5. Externally-caused noise, recorded

The shared tree went red **eight times** during this increment, every time from TAB 2 or TAB 3 mid-edit
(`WorkspaceService.cs`, `_LayoutWorkspace.cshtml`, `ReportsCenterApiController.cs`, `ReportsCenterPresenter.cs`,
`ReportingUiSafetyTests.cs`). In every case the count of errors in TAB 4 files was **zero**.

Two Reporting tests were failing mid-increment (`ReportingPilotAndWorkspaceTests`,
`ReportingUiSafetyTests`) — both TAB 2's own, in files they had modified minutes earlier. **They were fixed by TAB 2
before final verification**, so the full-suite gate is met rather than externally blocked.

No result in this document comes from a run whose build was not separately confirmed at 0 errors.
