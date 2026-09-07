# ADR-028 — Projects permission model

**Status:** Accepted (Stage 1 Batch C) · **Date:** 2026-08-04 · **Depends on:** ADR-026 (shared RBAC).

---

## 1. The starting position, and the blocker Batch C had to remove

`ProjectController`'s 30 mutating actions carried **ZERO authorization**. Worse, record-level access was **not
derivable**: `Project` carries `ID`, `CompanyID`, `Code`, `Name`, `NameEn`, `IsActive`, dates, `Budget`,
`CustomerId`, `Location`, `ContractValue`, `Status`, `ActivityTypeId`, `CostCenterId`, `AdvancePercent`,
`RetentionPercent` — and **no manager, owner or member of any kind**. A search of the whole model for an
employee↔project relationship returned nothing.

So the brief's roles (project manager, project member, department owner) had no data behind them.
**`ProjectMembers` is that missing relationship**, and it is the only new business table Batch C adds.

## 2. Vocabulary — eight actions

`read` · `create` · `edit` · `manage` · `budget-view` · `budget-manage` · `billing` · `close`

**Dropped from the brief's example list:** `member-manage` (expressed by `manage` plus the per-project `Manager`
role) and `approve` (no project approval exists anywhere in the code).

## 3. Two different things, kept apart

| | Question it answers |
|---|---|
| **Module roles** (`PlatformRoleAssignments`, Scope = `Projects`): `ProjectsAdministrator`, `ProjectsFinance`, `ProjectsViewer` | what may this person do in the Projects module |
| **`ProjectMembers.RoleOnProject`**: `Manager`, `Member`, `Observer` | who is on project 47 |

Conflating them would make leaving a project look like revoking a permission.

## 4. Record-level rules

| Membership | read | edit | manage (this project) | close | budget | billing |
|---|---|---|---|---|---|---|
| `Manager` | ✔ | ✔ | ✔ | ✖ | ✖ | ✖ |
| `Member` | ✔ | ✔ | ✖ | ✖ | ✖ | ✖ |
| `Observer` | ✔ | ✖ | ✖ | ✖ | ✖ | ✖ |

1. **Membership must be active AND not ended** (`IsActive`, `LeftAt`). Both tested.
2. **The PROJECT ROW's company is verified BEFORE the membership row is trusted.** A `ProjectMembers` row carries a
   denormalised `CompanyID`; a wrong or tampered one would otherwise be enough to reach another company's project.
   **This was a real bug in the first implementation, caught by
   `A_project_in_another_company_is_refused_even_with_a_membership_row`, and fixed.**
3. **Membership is never money.** Even a project `Manager` gets no `budget-view`, `budget-manage` or `billing`.
4. **`close` stays administrative** even for a project's manager: it freezes billing and costs.
5. **`billing` delegates to Accounting.** It requires `AccountingAccessService`'s own `post` decision *as well as* a
   projects-side right, through the context-aware `IModuleAccessService` — not the session-based legacy method. This
   is what stops project administration becoming a back door into the ledger. Tested in both directions.
6. A posted `ProjectID` is only a lookup key; absent and other-company answer identically.

## 5. Not implemented

Project Workspace, AI Context, project approval workflow. `ResolveProjectScopeAsync` / `MemberProjectIdsAsync` are
the seam. **`ProjectController`'s other 29 mutating actions are NOT remediated** — Batch D. No membership
administration UI exists yet, so `ProjectMembers` is populated by data, not by a screen.

## 6. Proof endpoint

`ProjectController.Boq(int id)` — a per-project read. Refused and absent answer identically.