# CrossBusiness — Current System Architecture Evidence Pack

> **Read-only evidence.** Every claim below cites a file and, where it matters, a line. Nothing in
> this pack was inferred from product expectation. Where the repository does not prove something, it
> says so rather than filling the gap.
>
> Refs analysed: `master` = **60dc115**, `tasks/phase4` = **7c6fcea** (tip of the Task foundation,
> superset of phase1–3). Generated 2026-09-02.


## The one thing to read first

**The entire Task foundation is unmerged.** `tasks/phase4` is 37 commits ahead of `master` and zero
behind. Everything this estate has built for Task orchestration, work policy, the canonical lifecycle
and management queries exists only on that branch. `master` — the branch every other tab integrates
into — has none of it.

That single fact reframes most questions in this pack. "What exists today?" has two answers, and they
differ by the whole of Phases 1–4.

## Files

| File | Covers |
|---|---|
| `00-EXECUTIVE-SUMMARY.md` | headline findings, counts, Phase-4 recommendation |
| `01-SOLUTION-AND-MODULE-MAP.md` | projects, folders, module inventory |
| `02-TASK-MANAGEMENT-CURRENT-STATE.md` | every Task file, and the current flow |
| `03-TASK-LIFECYCLE-ASSIGNMENT-ESCALATION.md` | states, transitions, assignment, escalation, `Task.Cancelled` |
| `04-BUSINESS-EVENTS-OUTBOX.md` | outbox, dispatch, consumers, event inventory |
| `05-TASK-ORCHESTRATION.md` | Phase-3/4 orchestration, rules, idempotency |
| `06-SYSTEM-WORK-CREATION-PATHS.md` | every door that creates a TaskItem |
| `07-ORG-HIERARCHY-HR.md` | Employee, Hierarchical, manager walk, TM-2 |
| `08-WORKSPACE-MYWORK-ATTENTION-CALENDAR.md` | the work surfaces and their sources |
| `09-DOCUMENTS-APPROVALS.md` | document expiry, the three approval mechanisms |
| `10-ACCOUNTING-INVENTORY.md` | ledger, period close, stock, quotations |
| `11-CRM-PROJECTS.md` | CRM state, and the Projects task-concept collision |
| `12-POS-MANUFACTURING.md` | POS, work orders, cross-module coupling |
| `13-COMMUNICATION-NOTIFICATIONS.md` | notification authority, comm outbox, F-1 |
| `14-REPORTING-AND-MANAGEMENT-CONTROL.md` | Report Studio, datasets, the 11 management questions |
| `15-SECURITY-COMPANY-ISOLATION.md` | baseline counts, company literals, BusinessContext |
| `16-BACKGROUND-WORKERS.md` | all 7 workers |
| `17-UI-SCREEN-INVENTORY.md` | 361 views by module |
| `18-DIRECT-COUPLING-MAP.md` | cross-module dependency matrix |
| `19-EVENT-DRIVEN-MAP.md` | producer → event → consumer → side effect |
| `20-FINDINGS-REGISTER.md` | severity-ranked findings |
| `21-PHASE4-PHASE5-DECISION-SUPPORT.md` | Q1–Q10 answered |
| `architecture-catalog.json` | machine-readable catalogue |

## What this pack deliberately does not do

It proposes no implementation, changes no code, and does not rank product priorities. Where a value
looks like policy but is only a default — escalation's 24/24/2 is the clearest case — it is labelled
as a default and attributed to the commit that introduced it, not promoted into a business rule.
