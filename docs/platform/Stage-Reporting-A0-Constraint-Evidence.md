# A0 — 5 · Constraint evidence

**Result: PASS.** Seven CHECK constraints present; each proven to reject; the legal boundary value proven to
be accepted; uniqueness and its filter proven.

---

## 1. Presence

`Each_vocabulary_check_constraint_is_present` — a `[Theory]` with one case per constraint, each querying
`sys.check_constraints` on a freshly built probe database.

| Table | Constraint |
|---|---|
| ReportTemplates | `CK_ReportTemplates_Scope` |
| ReportShares | `CK_ReportShares_PrincipalType` |
| ReportShares | `CK_ReportShares_AccessLevel` |
| ReportRuns | `CK_ReportRuns_Kind` |
| ReportRuns | `CK_ReportRuns_Status` |
| ReportSchedules | `CK_ReportSchedules_Frequency` |
| ReportDeliveryAttempts | `CK_ReportDeliveryAttempts_Status` |

## 2. Rejection — the part that matters

`Out_of_range_vocabulary_values_are_rejected_by_the_database` inserts a value **exactly one past the top** of
each enum and requires the insert to fail.

Asserting that a constraint *exists* proves a row in a catalog view. It does not prove the predicate is the
right way round. So each attempt asserts **SQL error 547** specifically — "conflicted with the CHECK
constraint". A NOT NULL violation or a missing column would also throw, and would let the test pass for the
wrong reason while proving nothing.

| Attempt | Expected |
|---|---|
| `ReportTemplates.Scope = 4` | 547 |
| `ReportShares.PrincipalType = 4` | 547 |
| `ReportShares.AccessLevel = 5` | 547 |
| `ReportRuns.Kind = 3` | 547 |
| `ReportRuns.Status = 4` | 547 |
| `ReportSchedules.Frequency = 5` | 547 |
| `ReportDeliveryAttempts.Status = 4` | 547 |

## 3. And the legal value is accepted

The same test then inserts `Scope = 3` (Personal) and requires success.

Without this, an off-by-one in the predicate — `BETWEEN 0 AND 2` — would pass every rejection assertion
while making every real Personal template unsavable. Rejection tests alone cannot see that.

## 4. Uniqueness, and its filter

`A_duplicate_live_share_grant_is_rejected`:

1. Insert a share → succeeds.
2. Insert the identical `(CompanyID, ReportCode, TemplateId, PrincipalType, PrincipalKey)` → **2601/2627**.
   Two live grants would make "what does this person have?" a question with two answers.
3. Soft-delete it, then re-grant → **succeeds**, because `UX_ReportShares_Unique` is filtered on
   `DeletedAt IS NULL`. A grant that could never be reinstated would make revocation permanent by accident,
   and that is a real support incident rather than a hypothetical.

## 5. Cross-company relationships

Not DB-enforceable here, and deliberately so: reporting tables carry `CompanyID` but hold **no FK to
Employees, Companies or Branches**, matching the rest of the platform — an append-only audit table with a
hard FK to mutable master data blocks tenant purges. The three FKs that do exist
(version → template, tag link → tag, recipient → schedule) are within one company's own rows, so a
cross-company parent is unreachable through them.

Company isolation is enforced above the schema, by `BusinessContext` and `CompanyWriteGuardInterceptor`, and
is covered by the existing Reporting suite. This document does not claim the database enforces it.
