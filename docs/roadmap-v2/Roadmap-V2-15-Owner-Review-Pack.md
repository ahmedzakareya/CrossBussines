# CrossBusiness Platform — Roadmap v2 — 15 Owner Review Pack

## OWNER-APPROVED EDITION — PLAN FROZEN

**What has been decided, what remains open, and what happens next.**

---

## 1. Read in this order

| # | Document | Why |
|---|---|---|
| 1 | `00-Executive-Summary` | The position in one page |
| 2 | `16-Product-Vision` | **CrossBusiness Workspace** — the first product |
| 3 | `17-SQL-Deployment-Governance` | **R1** — the first executable gate |
| 4 | `07-Implementation-Roadmap` | R0–R15 with gates |
| 5 | `10-Decision-Register` | What is approved and what is open |
| 6 | `01-Verified-Baseline` | Every number, reconciled |
| 7 | `12-Updated-Poster-Specification` | For the later design step |

Reference: capability catalog, module assessments, screen inventory, both architectures, dependency map, risk register, diagrams, integration plan, parallel execution model, and the ten CSVs.

## 2. Decisions recorded as APPROVED

| Decision | Resolution |
|---|---|
| **D-41** | **CrossBusiness Workspace** is the first integrated user-facing product |
| **D-36** | **CrossBusiness Blue** is the canonical platform identity; green and gold stay inside financial visualizations |
| **D-32** | **Tasks** owns task entity, assignment, status, priority, due dates, dependencies, checklists, subtasks, escalation, time tracking and task rules |
| **D-12** | **Communication** owns comments, mentions, attachments, reactions, followers, watchers, unified timeline rendering and notification contracts. Tasks publishes events; it must not keep a second general timeline |
| **D-33** | **Calendar** owns events, recurrence, attendees, availability, reminders, resource booking, schedule views and external calendar contracts. Tasks keeps due dates and scheduling rules; Projects and Construction keep domain schedules |
| **D-35** | First reporting dataset is **Business Events / Platform Operations**, read-only, no sensitive payload by default |
| **D-38** | `CrossBuy/deploy/sql` is the canonical **authored** SQL root; `deploy/` is the deployment **package** root |
| **D-39** | Divergent same-name slices are diffed, read by their owner, and superseded by one reconciled slice |
| **D-40** | `PlatformSchemaHistory` is the **applied** registry; `manifest.json` is the **authored** registry |
| **R1** | Repository, ownership and deployment governance — **not a feature phase** |
| **Execution** | Per-tab worktrees + protected integration branch |
| **ADRs** | Reporting is **ADR-037**; Communication retains **ADR-030–036** |

**Approved architecture direction** (design settled, activation still gated): D-05 privacy ceiling per entity · D-06 retention per entity · D-07 audit per entity · D-13 `ExternalPrincipalContext` — a customer is never an employee context · D-14 `InternalNote` / `CustomerVisibleReply`.

## 3. Decisions that remain OPEN

**Security — four, deliberately not resolved.** Each record now carries exact current behaviour, exposed data, affected users, available dimensions, options, a recommended safe option, compatibility impact, migration implications, tests required, screens affected and the decision owner.

| Decision | Current behaviour | Recommended direction *(not approval)* |
|---|---|---|
| **D-01** Accounting.read | Ledger balances, no branch filter | Branch/company-scoped policy rather than global unfiltered read |
| **D-02** Inventory.read | Quantities **and unit costs**, unfiltered — while B6 now enforces warehouse scope exactly for *use*. Read and scope disagree | Warehouse/branch-scoped visibility **plus a separate stock-cost permission** |
| **D-03** CRM.read | `:59` returns `true` for **every** CRM action on an unconfigured company | Require assigned / team / company to be **explicitly selected** |
| **D-04** CRM owner scope | `:98` returns `null` — unrestricted, returned precisely when the system knows least | Distinguish ConfigurationError from Unconfigured and **fail closed on both**. An unconfigured state must never map silently to unrestricted |

**Also open:** D-19/20/21 Construction environment, M3/M9 and D-08 default · D-37 legacy CSS migration · D-09/10/11 PDF runtime, email delivery, scheduler · D-16/17 reporting vocabulary and Studio sequence · D-23 purchase action split · D-24/25/26 construction sequencing and concurrency · D-27 AI governance · D-28 Search ownership · D-29 portal isolation · D-30 mobile/offline · D-31 Master Data ownership · **D-15 @Role — BLOCKED** until reverse role-membership lookup exists.

## 4. Ownership still to be assigned

Decisions D-32 and D-33 settle the **boundaries**. They do not name the tabs.

| Action | Current state |
|---|---|
| Appoint the **Integration Owner** | `TAB-0` exists in the register, **unappointed** |
| Assign **Tasks and Calendar** to a tab | `TAB-5` exists, **UNASSIGNED** — 2 live hosted services with no owner |
| Assign **Support** to a tab | The only module with no access service at all |

These are R1 actions and cost nothing to decide.

## 5. What changed in this increment

**A correction to the previous assessment.** The earlier report said there is no reliable answer to which slices are applied. That is true of the **applied** side. But `CrossBuy/deploy/sql/manifest.json` — generated by `scan-sql-manifest.ps1` — **already registers all 115 scripts** with SHA-256, apply rank and an idempotency verdict, and already flags the 4 divergent duplicates as ambiguous. Governance now **extends** that registry rather than replacing it; a parallel registry would recreate the exact defect the two SQL trees demonstrate.

**Two further facts surfaced:**

* **`hm16_rename_grni.sql` is classified `review`** — an unguarded, unread, mutating script sitting in the tree. `script.sql` is `not-deployable`. Both must be resolved before any promotion (RSK-21).
* **`deploy/README.md` states provisioning is BACKUP/RESTORE of `CrossBuyDB2`**, not script replay, because EF migrations are broken and ~90 ad-hoc scripts in `C:/temp` built the schema — a third source of truth outside the repository (RSK-22). R1 must reconcile the restore path with the slice path.

**A new risk designed against.** **RSK-23** — a unified cross-module surface is exactly where an authorization shortcut gets introduced. The Workspace therefore adds no permissions and delegates every read, with per-module delegation tests as an R3 gate.

**The generator now validates itself.** Referential integrity runs before any file is written: duplicate ids, field arity, status vocabulary, and every capability→phase, capability→decision, phase→screen, phase→decision and dependency→endpoint reference. It caught two real defects this increment — a capability row carrying 23 values instead of 22 (which silently shifted its phase to a risk id) and a dangling `D-22` reference — and now fails the build rather than shipping a document that points at something which does not exist.

## 6. Approvals requested

| # | Approval |
|---|---|
| 1 | **Roadmap v2 R0–R15 as frozen**, with R1 as deployment governance |
| 2 | **R1 authorized as the next phase** — no feature work until it completes |
| 3 | **Appoint the Integration Owner** |
| 4 | **Assign Tasks and Calendar to a tab** (boundaries already decided) |
| 5 | **Assign Support to a tab** |
| 6 | **D-38 canonical SQL root** and the R1 migration approach (no files moved yet) |
| 7 | Decide the four open security decisions **D-01 – D-04**, or schedule them to R6 entry |
| 8 | **D-19** construction environment, so C1 can be applied and measured in R2 |
| 9 | **Poster specification** as the basis for a later design step |

## 7. Verification you can check yourself

| Claim | How |
|---|---|
| Documentation broke nothing | `dotnet build CrossBuy.sln -c Debug` → 0 errors, 0 warnings |
| Output is deterministic | Run `_generator/generate_roadmap_v2.py` twice; hashes match |
| The dataset is referentially sound | The generator prints `integrity OK` or exits non-zero |
| Markdown and CSV cannot disagree | Both come from `_generator/roadmap_v2_data*.py` |
| Preservation restores | See the final delivery report — 0 mismatches required |

## 8. What this increment did not do

No production source modified · no SQL executed · CrossBuyDB2 untouched · `crossbuy-brand.css`, existing production UI and CLAUDE.md unchanged · no file moved or deleted in either SQL tree · no poster image generated.

Not started: Batch C · Wave 2 · CRM B6 conversion · Security Console · Master Data · Report Studio UI · Communication UI · Construction C2+ · Task Management changes · Calendar changes · production SQL rollout.

## 9. Recommended next step

Approve items 1–6 and 9, and either decide D-01 – D-04 now or schedule them to R6 entry. Then authorize **R1 only**.

R1 is deliberately not a feature phase. Until the repository is committed, owned and deployable — one canonical SQL root, an authoritative applied record, and an integration branch that cannot be left broken — every feature built on it inherits the conditions that produced five build breaks, two divergent SQL trees and an unreviewed mutating script sitting in the tree.
