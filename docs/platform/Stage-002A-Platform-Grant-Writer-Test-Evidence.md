# Stage 2A — Platform Grant Writer — Test Evidence

**17 / 17 acceptance tests pass** on an isolated SQL Server probe. Registered in
`engineering/required-evidence-manifest.json` (enforced total **121**). **0 skipped.**

---

## 1. The rule this evidence obeys

**No test seeds a grant row.** Every row under test is created by `IPlatformGrantWriter`.

That is not a stylistic preference. A test that inserts the row itself proves the **reader** works and says nothing
about the writer — and that is precisely the gap that let RISK-037 survive an entire stage: four test files wrote to
`PlatformRoleAssignments`, every test passed, and production could not create a single grant.

## 2. Why `payroll-manage` is the proof action

`HrAccessService` declares `payroll-manage` **never bootstrap-open**, and it requires `PayrollOfficer` specifically
(not `HrManager` — money is a separate right).

With a bootstrap-eligible action, revoking the only grant returns the company to bootstrap-open and authorization comes
**back**, so the revocation proof would be ambiguous. With `payroll-manage` the chain is unambiguous:

```
no grant           -> DENIED    (a real deny, not a bootstrap artefact)
writer creates     -> ALLOWED   (role-driven, read by the real HrAccessService)
writer revokes     -> DENIED    (authorization removed)
```

Each stage is re-read from a **fresh** `CrossDbContext`, so nothing is asserted from the entity that wrote it.

## 3. The 17 tests

### The central proof
| Test | Proves |
|---|---|
`An_authorized_administrator_creates_a_grant_that_HR_then_honours_and_revoking_removes_it` | A14.1–A14.7 end to end: create → persisted → real HR honours it → audit fields → `Created` event → revoke → HR denies → row survives → `Revoked` event |
`A_grant_closes_bootstrap_open_for_the_company_and_scope` | the transition itself: an employee holding nothing passes `leave-manage` under bootstrap-open and is **denied** once any grant exists |

### Idempotency and duplicates
| Test | Proves |
|---|---|
`A_retry_with_the_same_idempotency_key_returns_the_original_and_creates_no_second_row` | replay returns the original id; exactly one row |
`The_same_key_with_a_DIFFERENT_payload_is_a_conflict_not_a_silent_replay` | returning the original would falsely confirm a new intent |
`A_duplicate_active_grant_is_refused` | plus **revoke-then-regrant works** — why the index is filtered |

### Authorization and the ceiling (RISK-039)
| Test | Proves |
|---|---|
`An_unauthorized_actor_creates_no_row_and_no_event` | rows **and** events both unchanged |
`A_cross_company_grant_is_denied_without_platform_authority` | company boundary |
`Self_escalation_is_refused_below_platform_authority` | the shortest escalation path is closed |
`A_module_administrator_may_not_confer_a_role_they_do_not_hold` | a grant never exceeds the grantor's own rights |
`Module_level_authority_is_NOT_admissible_under_bootstrap_open` | **A5 rule 6** — the first grant needs a real administrator |
`A_worker_or_system_context_may_not_administer_grants` | administration is an attributable interactive act |

### Validation and validity
| Test | Proves |
|---|---|
`An_inactive_employee_cannot_receive_a_grant` | |
`An_expired_grant_does_not_authorize` | created with a past window; HR still denies |
`A_revoked_grant_cannot_be_brought_back_by_a_validity_change` | no reactivation by side effect; re-revoke is `AlreadyRevoked` |
`An_unknown_role_or_scope_never_creates_a_silent_no_op_row` | unknown role, unknown scope, POS scope, unsupported principal — four refusals, **zero rows** |

### Isolation
| Test | Proves |
|---|---|
`A_foreign_company_grant_is_indistinguishable_from_not_found` | same outcome as a nonexistent id |
`A_branch_scoped_grant_keeps_its_branch_exactly` | never widened to company scope; a foreign branch is refused |

## 4. Wiring: real services, two stubs

Real: `PlatformRoleDirectory`, `HrAccessService`, `OrgHierarchy`, `BusinessEventService`, `EntityRegistry`, the real
`CrossDbContext` on a real SQL Server probe.

Stubbed, both because neither exists outside a request:

* **`IPlatformAdminIdentity`** — stands in for `AspNetUserRoles`. This is the seam that made the ceiling testable at
  all; the writer previously injected `UserManager<Users>`, and a rule nobody can construct is a rule nobody has
  verified.
* **`IBusinessContextAccessor`** — see §5.

## 5. Two DEFECTS this batch introduced, found and fixed

Both were found by reading the SQL companion guards' own output properly rather than trusting a green run.

### 5.1 The acceptance class leaked 36 probe databases

xUnit builds a new instance per test, so 17 tests create 17 probes per run. `DisposeAsync` dropped them — except this
class creates several `CrossDbContext` instances per test and does not dispose them all, so ADO.NET kept **pooled
connections** open against the probe and `DROP DATABASE` did not take effect. Across four runs, 36 probes accumulated.

Fixed by calling `SqlConnection.ClearAllPools()` before the drop. Verified: a full suite run now leaves **0** probes.

### 5.2 Guard 3's real-instance check could never fail — a guard that passed by looking away

Worse than the leak. `Guard3_no_leftover_scratch_database_exists_on_the_real_instance` passed the result of
`ListOwnedProbeDatabasesAsync()` as "accounted for" — and that method returns **every** `CrossBuyProbe_%` database on
the instance. So every leftover was automatically excused and the assertion was vacuous. It reported clean while 36
leaked probes sat on the server.

This is exactly the failure class the guards were built to prevent — a guard satisfied by a declaration rather than by
a fact — committed by the guard itself. Fixed: only the shared fixture's own database is accounted for. The SqlServer
tests share one xUnit collection and therefore run serially, so any probe present while the guard runs **is** a
leftover.

**Both are reported rather than quietly corrected, because the value of a guard is the finding.**

## 6. Two findings about the platform, from writing these tests

**`RecordAsync` resolves a `BusinessContext` unconditionally.** The first stub threw, and every create-path test failed
on it. Supplying `CompanyIdOverride` and `ActorEmployeeIdOverride` does **not** exempt a caller. This is the same hard
coupling recorded as HM-D58 for the sale/purchase path, and it applies to grant administration too: **a grant cannot be
written without a resolvable BusinessContext.** Harmless in production, where administration is always an interactive
request — and a real constraint on any future background or migration writer, which is why it is written down rather
than worked around silently.

**`Companies`, `Branches` and `Employee` need `IDENTITY_INSERT` and have many NOT NULL string columns.** The seed fills
each table in its own session-scoped window and sets still-null non-nullable strings to a placeholder by reflection.
Enumerating those columns in a test would be a list that goes stale the first time a column is added — and the failure
would look like a grant defect.

## 6. Isolation and re-runnability

Own probe database (`CrossBuyProbe_GrantWriter_*`), created in `InitializeAsync`, dropped with confirmed removal in
`DisposeAsync` — the RISK-036 pattern. The shared platform fixture is untouched.

Fixed employee ids (9001–9006) are safe **because** the probe is per-class: repeated runs cannot collide. They are
worth it — a failure that says "employee 9002 was denied" is legible; "employee 41 was denied" is not.

## 7. Gaps, stated

* **Concurrent create/revoke under real parallelism is not tested.** A15 asks for it. The uniqueness race *is* handled
  (the DB arbitrates; the loser reports `Conflict`) and the code path exists, but a two-thread SQL Server test proving
  it is not yet written. This is the one A15 item outstanding.
* `AdministrationDenied` / `ConflictDetected` are asserted only as absence-of-rows, because they are logs rather than
  events — see the delivery report §6.
