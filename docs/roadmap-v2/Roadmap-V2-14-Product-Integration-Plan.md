# CrossBusiness Platform — Roadmap v2 — 14 Product Integration Plan

**How four separately-built platforms become one product.**

The engineering problem is solved in document 09. This document is the *product* problem: three complete platforms currently reach no user, and two modules run background workers nobody owns.

---

## 1. The integration debt, stated as a number

| Platform | Tests passing | Tables | Screens | Users reached |
|---|---|---|---|---|
| Reporting | 221 | 12 | 0 | **0** |
| Communication | 274 | 14 | 0 | **0** |
| Construction C1 | 38 | 8 (applied nowhere) | 0 | **0** |
| **Total** | **533** | **34** | **0** | **0** |

533 passing tests and 34 tables of capability that no user can reach. Every month this persists, the platforms drift further from the data shapes and workflows they were designed for, because nothing is exercising them against reality.

## 2. Integration sequence

Each step turns one dark platform into a reachable capability. The order is dependency-driven, not political.

### Step 1 — Make the tree deployable (R1)

Nothing else is safe until this is true.

| Action | Outcome |
|---|---|
| Consolidate the two `deploy/sql` trees into one | A deploy applies a known slice version |
| Reconcile the 4 divergent POS slices before deleting either copy | No silent loss |
| Apply and populate `platform_schema_history` | "Which slices are applied here?" gets an answer |
| Commit all four tabs' work | Every tab diffs against a stable base |
| Adopt worktrees + integration branch + the 12-point gate | A broken state is rejected, not discovered |

**Exit:** one SQL tree, one applied-slice record, integration gate enforced, zero build breaks for one full cadence.

### Step 2 — Activate all three foundations (R2)

R2 activates Reporting, Communication AND the Construction C1 schema together, in an approved environment, with **no broad UI**. Communication leads within the phase because every other module consumes it.

1. Decide D-05 (privacy ceiling), D-06 (retention), D-07 (audit policy).
2. Apply `communication_platform_slice_001.sql`.
3. Call `CommunicationPlatformRegistration` in `Program.cs` — one line, following the Reporting tab's `AddCrossBusinessReporting` pattern.
4. Register the platform's hosted worker.
5. Execute the DocComments migration with before/after counts, and retire the second comment model.
6. Reconcile the two notification paths — legacy `NotifyAsync` and the projection consumer — so one fact produces one notification.
7. Deliver the threads panel and mentions centre.

**Exit:** a user comments on an entity in production and the right person is notified, once.

### Step 3 — Give the product a front door (R3)

Deliver the CrossBusiness Workspace shell, My Work, notifications, mentions, approvals, recent activity and the personal dashboard — **owning no data and adding no permissions** (D-41).

**Exit:** a user sees their work across modules in one surface, every item authorized by its owning module.

### Step 4 — Bind Reporting to real data (R4)

1. D-35 is APPROVED: the pilot dataset is **Business Events / Platform Operations**, read-only.
2. Bind it and validate the shape against live data.
3. Deliver the Report Center.
4. D-09 (PDF runtime), D-10 (delivery), D-11 (scheduler) remain OPEN and are not required for the pilot, which ships HTML, CSV and Excel.
5. Report Studio after the Center, not before (D-17).

**Exit:** a real report renders in four formats, on a schedule, delivered.

### Step 5 — Close the security surface (R6)

1. Decide D-01, D-02, D-03, D-04.
2. Convert both CRM sites with the same evidence standard B6 used.
3. Deliver the Security Console so policies and grants stop being raw database rows.
4. Begin the 143-action debt burn-down, analyzer-enforced and shrink-only.

**Exit:** no bootstrap-open site remains; debt is strictly below 143 with reconciliation.

### Step 6 — Construction beyond C1 (R7)

1. The C1 DDL is applied in R2 in the D-19 environment — closing the gap where services are registered against tables that exist nowhere.
2. Run the measurement script; record M3 and M9.
3. Confirm D-08 default mode per company (D-21).
4. Then, and only then, plan C2.

**Exit:** C1 live with measurements recorded.

### Step 7 — Integrate Tasks and Calendar (R5)

**Not greenfield.** Audit the existing controller, 5 views, access service, 7 SQL slices and 2 hosted services first. Then consolidate ownership per D-32/D-33, register Tasks as a Comm entity surface, and surface both in the Workspace. Legacy task activity data is assessed before any migration.

## 3. Ownership actions required now

| Action | Why | Decision |
|---|---|---|
| Appoint an **Integration Owner** | Shared files, slice registry and the integration branch have no owner today | — |
| Assign **Tasks** to a tab | Two live hosted services, no owner. Ownership BOUNDARY decided (D-32); the tab is not yet named | D-32 |
| Assign **Calendar** to a tab | Live service, no owner. Boundary decided (D-33); ships with Tasks | D-33 |
| Assign **Support** to a tab | No access service at all, the only module without one | — |
| Assign **Master Data** ownership | Employee, Customer, Item and Supplier are shared informally | D-31 |

## 4. Product integration principles

1. **A platform is not done when its tests pass — it is done when a module consumes it in production.** The three dark platforms are the evidence for this rule.
2. **One contract per platform.** Modules opt in (`ICommEntitySurface`, dataset registration, `IModuleAccessService`); the platform never enumerates modules.
3. **Activation is a phase, not a footnote.** It gets decisions, a slice application, a Program.cs change, a migration and a UI — which is why R2 exists as a phase rather than a task.
4. **Retire the thing you replaced.** DocComments after Comm; the second notification path after activation; the duplicate SQL tree after consolidation. Two implementations of one concept is how the SQL trees diverged in the first place.
5. **No module ships a private version of a shared capability.** If reporting or comments are needed and the platform is not ready, wait or fix the platform.

## 5. What integration explicitly does not mean

* Not a rewrite of the working legacy modules.
* Not a microservice split.
* Not a UI restyle of specialized operational screens (POS, KDS, hyper lane, mobile attendance, storefront keep their domain UX).
* Not activating anything before its blocking decisions are made.
