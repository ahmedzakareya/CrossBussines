# ADR-027 — HR permission model

**Status:** Accepted (Stage 1 Batch C) · **Date:** 2026-08-04 · **Depends on:** ADR-026 (shared RBAC).

---

## 1. The starting position

`AdminController` (+3 partials, 42 mutating actions) and `PeopleController` (5) carried **ZERO authorization**: no
permission attribute, no `IsInRole`, no in-body access check. The only guard was the global
`SessionValidationMiddleware` — i.e. *authentication*. **Any signed-in employee could reach employee
administration, salary policies, payroll paths and appraisals.**

There was also **no HR role anywhere** — not in a table, not in code. So the roles named in the brief (HR officer,
HR supervisor, payroll officer) did not exist and could not be preserved; they are created here.

## 2. Vocabulary — eleven actions, each backed by a real operation

`read` · `employee-view` · `employee-manage` · `attendance-manage` · `leave-manage` · `leave-approve` ·
`payroll-view` · `payroll-manage` · `organization-manage` · `performance-manage` · `confidential-view`

Derived from the 47 mutating actions actually present: employees, attendance, leave (types/policies/encashment/
provision), employee requests, payroll/payslips, holidays, salary and attendance policies, policy assignments, job
titles, hierarchicals, appraisals, training, recruitment, employee documents, contracts.

## 3. Roles — four, not an org chart

| Role | Grants |
|---|---|
| `HrManager` | everything except payroll money |
| `HrOfficer` | day-to-day employee / attendance / leave administration |
| `PayrollOfficer` | payroll and salary policy |
| `HrViewer` | read-only across the company |

**`HrManager` does NOT get `payroll-manage`.** Money is a separate right, held by `PayrollOfficer`. That is the one
mapping most likely to be "simplified" later, so it is stated and tested.

## 4. Record-level rules — only what the data supports

1. **Self-access** for `read` / `employee-view`, with no HR role at all. It is evaluated before roles.
2. **NOT self-served:** `confidential-view`, `payroll-view`, `performance-manage`. "It is my own salary" is not the
   same claim as "I may see salary data", and a disciplinary record about someone is not theirs to read.
3. **`leave-approve` is not a role grant at all** — no HR role confers it. The caller must be a manager of the
   SUBJECT through the company-intersected hierarchy, which is the relationship `LeaveWorkflowService` already
   builds its approver chain from. A manager cannot approve their own leave.
4. **The subject's company comes from the `Employee` ROW.** A posted employee id is only ever a lookup key, and a
   non-existent id answers **identically** to another company's, so employees cannot be enumerated by probing.
5. **An inactive employee remains administrable** — a leaver's record must stay correctable. Recorded and tested so
   it cannot drift into an accidental deny.

## 5. Bootstrap-open, and its two deliberate exceptions

Until a company assigns its first `Hr` role, the module answers as it does today. **`confidential-view` and
`payroll-manage` are NEVER bootstrap-open**: those tiers were effectively unreachable before this batch, and opening
them by default would be a new exposure created by the batch meant to close one.

## 6. Not implemented

The Human Capital Platform, Employee/Manager Workspaces, Self-Service and People Intelligence. `AccessScope`
(`ResolveEmployeeScopeAsync`) is the seam they will use. **`AdminController`'s 42 actions are NOT remediated** —
Batch D.

## 7. Proof endpoint

`PeopleController.DecideLeave` — leave approval, the one pre-existing record-level rule. Before: any signed-in
employee could post a decision on any leave request. After: the approver relationship is required.

## 8. Tests

In `BatchCAccessServiceTests`: self-access, confidential-not-self-served, another employee denied,
payroll/confidential require the stronger role, cross-company employee refused, absent-vs-other-company
indistinguishable, inactive still administrable, manager approves a report but not self, an HR role does **not** grant
approval, and the cross-company hierarchy exclusion.