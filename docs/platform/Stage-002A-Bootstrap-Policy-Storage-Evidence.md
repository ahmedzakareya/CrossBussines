# CrossBusiness Platform — Stage 2A Batch B — B2 Bootstrap Policy Storage Evidence

**Complete and evidence-backed.** Real SQL Server only.

| File | SHA-256 (16) |
|---|---|
`CrossBuy/Models/Context/Platform/BootstrapAccessPolicy.cs` | `99c22d9eab59cd98` |
`CrossBuy/deploy/sql/bootstrap_access_policies.sql` | `09471492be657f09` |
`CrossBuy/Models/Platform/BootstrapPolicyContracts.cs` | `9216b19729e7f04c` |
`CrossBuy.Tests/SqlServer/BootstrapPolicyStorageAndReaderTests.cs` | `f4ea49fcaebc629b` |

**44 tests, 0 failed, 0 skipped** (storage + reader share one class).

---

## 1. Purpose

Mechanism A — `if (no role configured) return true;` — is invisible, unauditable and cannot exclude a single action.
This table is the same compatibility made **explicit**: per company, per scope, **per action**. Per-action is the
property Mechanism A structurally cannot have, because it sits before the action switch.

## 2. Entity, DbSet and EF configuration

`BootstrapAccessPolicy` — 20 columns: `ID`, `CompanyID`, `Scope`, `ActionCode`, `State`, `Reason`, `EnabledAt/By`,
`ExpiresAt`, `ReviewedAt/By`, `AcknowledgedAt/By`, `CreatedAt/By`, `UpdatedAt/By`, `SourceSystem`,
`MigrationBatchId`, `IsActive`. `DbSet<BootstrapAccessPolicy> BootstrapAccessPolicies` on `CrossDbContext`, with
lengths mirroring the DDL exactly — a model that disagreed with the script would truncate on one path and not the
other.

`IsEffective(asOfUtc)` and `Validate()` live on the entity. `Validate()` returns **every** problem rather than the
first, because a seed reporting one error at a time turns a multi-row plan into that many round trips.

**Separate from `PlatformRoleAssignments` by design.** A grant says *this employee holds this role*; a policy says
*this company has not configured this scope yet, and here is what that permits until it does*. IMP-001's coexistence
rule is that authoritative sources are never unioned — and a policy row in the grant store could be mistaken for a
grant by the four access services that already read it.

## 3. The six CHECK constraints — each verified by a test that inserts a violating row

| Constraint | Enforces | Test |
|---|---|---|
`CK_..._Company` | `CompanyID > 0` | covered by entity `Validate()` tests |
`CK_..._State` | the six-state vocabulary | `An_unknown_state_is_refused_by_the_DATABASE` |
`CK_..._NotPos` | `Scope <> 'Pos'` | `A_POS_policy_is_refused_by_the_DATABASE` |
`CK_..._TemporaryExpiry` | `State <> 'Temporary' OR ExpiresAt IS NOT NULL` | `A_Temporary_policy_with_no_expiry_is_refused_by_the_DATABASE` |
`CK_..._Dates` | `ExpiresAt >= EnabledAt` when both supplied | `An_expiry_before_the_enable_date_is_refused_by_the_DATABASE` |
`CK_..._Text` | non-empty `Scope`/`ActionCode`/`State` | an empty scope would be a policy matching nothing while *looking* like configuration |

**POS exclusion** is a database constraint, not a convention. `BranchUserRoles` is read before any `BusinessContext`
exists and POS fails closed without a role; a policy row would imply a bootstrap path that does not exist.

**Temporary expiry** is RISK-045 enforced by the engine rather than by remembering to set a field. A time-boxed
exception with no end date is a permanent one wearing a different name.

## 4. The filtered unique active-policy index

```sql
CREATE UNIQUE INDEX UX_BootstrapAccessPolicies_ActivePolicy
    ON dbo.BootstrapAccessPolicies (CompanyID, Scope, ActionCode) WHERE IsActive = 1;
```

**Filtered on purpose.** Superseded history must survive: a full unique key would force deleting the old row to change
a policy, and the old row is the answer to *"why was this open in March"*.

Proven by `Disabling_a_policy_RETAINS_history_and_allows_a_new_active_row` — two rows for the same identity, exactly
one active. And `A_duplicate_ACTIVE_policy_is_refused_by_the_DATABASE` asserts the index name appears in the failure,
so the test cannot pass on some unrelated error.

## 5. Schema-drift detection

The script does not merely check *whether* the index exists — it reconstructs its key from `sys.index_columns` and
**throws** if it is not exactly `CompanyID,Scope,ActionCode`, if it is not both `is_unique` and `has_filter`, or if
fewer than 6 CHECK constraints are present. A duplicate-prevention index whose key has drifted is a **security
property missing**, not a performance detail, so a second deploy fails loudly rather than proceeding.

## 6. Why the 15 Never actions are NOT a CHECK constraint

The one design decision here worth arguing, so it is argued rather than asserted.

A CHECK listing the 15 pairs would be a **second copy** of `NeverBootstrapOpen.All`. Two copies of a security list
drift, and the copy that drifts is the one nobody watches. Worse, drift would fail in the **dangerous** direction: add
a 16th Never action in code without updating the CHECK, and the database accepts a permitting policy for it — leaving a
row in an audit table asserting something no reader honours. A false audit line is worse than no constraint.

So the list lives in **one** place (code) and is enforced in **two**: `Validate()` refuses to construct a permitting
policy for a Never action, and the reader evaluates the classification **before** it queries. The database enforces
what it is good at — uniqueness, vocabulary, date ordering, POS.

## 7. Concurrency

`Concurrent_equivalent_inserts_produce_exactly_one_active_policy` — four threads, `Barrier`-synchronised, each with its
own `DbContext`. Exactly one succeeds; exactly one active row remains. Application-level checking cannot be the
guarantee because all four read "not found".

`A_rolled_back_transaction_leaves_no_partial_row` proves transactional integrity.
`Audit_fields_persist_and_two_companies_stay_independent` proves the same scope+action in two companies is legal and
that `EnabledBy`, `Reason`, `SourceSystem`, `MigrationBatchId` and `CreatedAt` all survive.

## 8. Relationship to the raw-SQL subversion test

`A_hand_inserted_allowing_policy_for_a_Never_action_still_DENIES` inserts a row with raw SQL — bypassing `Validate()` —
claiming `ExplicitlyAllowed` for `Accounting.post`. The row genuinely exists (asserted). The reader denies it and
returns **no policy id**, proving it never consulted the row. That test belongs to B4 but is the reason §6's design is
safe: the code-side list is enforced at decision time, so the absent CHECK costs nothing.

## 9. Mutation proofs B and C

| Mutation | Injected | Result |
|---|---|---|
**B** | `CK_..._TemporaryExpiry` removed from the slice | `A_Temporary_policy_with_no_expiry_is_refused_by_the_DATABASE` **FAILED** |
**C** | `CK_..._NotPos` removed from the slice | `A_POS_policy_is_refused_by_the_DATABASE` **FAILED** |

Both restored; `grep -c MUTATION` → **0**. Both constraints are load-bearing, not decorative.

## 10. Known limitations — stated at full strength

**The slice is proven idempotent against a probe whose table was created from the EF model, not against a completely
empty database created only by the SQL script.** `CreateProbeDatabaseAsync` builds the schema from the model, then the
script runs on top — so what is proven is "the script is safe against an existing compatible object", which is what a
**redeploy** does. The first-deploy path (`CREATE TABLE` executing for real) is exercised by the script's own
`IF OBJECT_ID(...) IS NULL` branch but is **not** covered by a test. Not claimed as stronger.

Also: `CK_..._Company` has no dedicated SQL-level test (covered only through entity validation), and drift detection is
exercised by a passing second run rather than by a deliberately drifted schema.

## 11. Environment

`CrossBuyDB2` — **present and untouched**. No SQL executed against it. All evidence from isolated
`CrossBuyProbe_BootstrapPolicy_*` databases, dropped with confirmed removal. **0 probes remain.**
