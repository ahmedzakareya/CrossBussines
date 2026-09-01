# CrossBusiness Platform — Roadmap v2 — 11 Risk Register

**24 risks. 12 High.**

Machine-readable: `roadmap-v2-risks.csv`.

---

## 1. Risks discovered by this reassessment

Three were not in any prior report and came from reading the tree rather than the reports:

* **RSK-01** — two `deploy/sql` trees exist. 57 slices in `deploy/sql`, 60 in
  `CrossBuy/deploy/sql`, only 4 filenames in both, and **all 4 differ**. Zero identical.
* **RSK-02** — `platform_schema_history.sql`, the table that is supposed to record what has
  been applied, is itself unapplied. The deployment tracker is untracked.
* **RSK-13** — Tasks and Calendar have **live registered hosted services** and no tab owner.

## 2. Register

| RiskId | Area | Risk | Severity | Likelihood | Impact | Mitigation | Status |
|---|---|---|---|---|---|---|---|
| RSK-01 | Deployment | Two SQL trees with divergent same-name slices | High | Certain | 'Apply pos_setup.sql' is ambiguous - the manifest states this explicitly. | D-38 names a canonical root; D-39 reconciles each pair deliberately in R1. No file moved in this increment. | Open - governed by R1 |
| RSK-02 | Deployment | No authoritative APPLIED record | High | Certain | No reliable answer to 'which slices are applied to this database'. | D-40: PlatformSchemaHistory as the applied registry, first slice in every environment. | Open - governed by R1 |
| RSK-03 | Security | 143 unprotected mutating actions | High | Certain | Unauthorized mutation across 143 actions. | Burn down in R6 and R8; analyzer keeps it shrink-only. | Open |
| RSK-04 | Security | CRM bootstrap-open remains | High | Certain | Full CRM access and unrestricted visibility on any unconfigured company. | Convert in R6 once D-03 and D-04 are decided. | Open |
| RSK-05 | Process | Shared-tree build breaks | High | Likely | Acceptance runs on stale binaries; false green results. | R1 execution model; no tab may leave the integration branch unbuildable. | Open - governed by R1 |
| RSK-06 | Process | Uncommitted parallel work | High | Certain | Every tab's base shifts invisibly. | Commit all four tabs in R1. | Open - governed by R1 |
| RSK-07 | Reporting | Foundation with no consumer | Medium | Certain | Investment does not convert to value; drift from real data shapes. | R2 activation then R4 pilot dataset (D-35 approved). | Open - governed by R2/R4 |
| RSK-08 | Communication | Foundation not activated | Medium | Certain | A complete platform nobody can use. | R2 activation behind D-05/D-06/D-07. | Open - governed by R2 |
| RSK-09 | Communication | Two comment models coexist | Medium | Certain | Divergent comment history; migration cost grows. | DocComments migration in R2 with before/after counts. | Open - governed by R2 |
| RSK-10 | Communication | Two notification paths | Medium | Likely | Duplicate or divergent notifications. | Reconcile during R2 activation - one fact, one notification. | Open - governed by R2 |
| RSK-11 | Construction | Services registered against tables that do not exist | High | Certain | A construction code path can fail at runtime on a missing table. | Apply DDL in the D-19 environment during R2. | Open - governed by R2 |
| RSK-12 | Construction | Legacy concurrency gap | Medium | Likely | Lost updates on concurrent commercial edits. | Adopt the C1 rowversion pattern in R7 (D-26). | Open |
| RSK-13 | Platform | Unowned modules with live workers | Medium | Certain | Changes land with no owner and no review. | RESOLVED by D-32 and D-33; assignment to a tab is an R1 action. | Mitigated - ownership decided |
| RSK-14 | Platform | Kernel is a hard runtime dependency | High | Certain | A missing table fails a real sale or any correction. | Every platform slice applied and verified before each phase. | Mitigated by process |
| RSK-15 | Platform | Background paths and BusinessContext | Medium | Possible | A future background document writer fails in production. | Covered by the DI wiring test; re-checked on every new hosted service. | Open |
| RSK-16 | Security | PlatformOps via accounting fallback | Low | Rare | Platform operations granted through an accounting role. | Closed - Accounting.manage is Never-bootstrap-open, asserted directly. | Closed |
| RSK-17 | Quality | Stale-build false green | High | Likely | Evidence that proves nothing. | Capture the build error count explicitly; never --no-build for mutation evidence. | Mitigated by rule |
| RSK-18 | Product | Three platforms with no user reach | Medium | Certain | Stakeholders may believe features exist. | Catalog separates CurrentStatus from ProductionActivation and UIStatus; R2-R4 convert them. | Mitigated by disclosure |
| RSK-19 | Security | AI over ungoverned data | Medium | Possible | AI surfaces data a user may not access. | Route AI through the reporting dataset layer (D-27). | Open |
| RSK-20 | Data | Master data has no owner | Medium | Certain | Duplicate and divergent master records. | Per-entity ownership register in R6 (D-31). | Open |
| RSK-21 | Deployment | An unreviewed script is in the tree | High | Certain | A re-run could mutate data unguarded, or a deployment could include a script judged unsafe. | Resolve or formally exclude both before any environment promotion (R1). | Open |
| RSK-22 | Deployment | Provisioning does not use the slices at all | High | Certain | The slice registry describes a build path that is not the one actually used; a fresh environment may not match any manifest state. | R1 must reconcile the restore-based path with the slice registry and decide which is authoritative for new environments. | Open |
| RSK-23 | Product | Workspace could become a permission bypass | High | Possible | One aggregated query could return data the user cannot see in the owning module. | D-41: Workspace adds NO permissions; every read delegates to the owning module's access service. Per-module delegation tests are an R3 gate. | Open - designed against |
| RSK-24 | Product | Brand divergence between spec and code | Medium | Certain | New and legacy screens look like two products. | D-37 rollout: blue on new screens first, legacy migrated through an approved visual rollout. | Open |

## 3. Evidence

| RiskId | Evidence | Owner |
|---|---|---|
| RSK-01 | deploy/sql (57) and CrossBuy/deploy/sql (60); 4 filenames in both, all 4 differ, 0 identical. manifest.json already records all 4 with identical=false and distinctHashes=2. | Integration Owner |
| RSK-02 | platform_schema_history.sql is authored but applied nowhere. manifest.json answers the AUTHORED side (115 scripts, SHA-256, apply rank) but has no database-side counterpart. | Integration Owner |
| RSK-03 | Analyzer baseline, shrink-only, enforced at build. | First Tab |
| RSK-04 | CrmAccessService.cs:59 returns true for every action; :98 returns null (unrestricted). | First Tab |
| RSK-05 | Five cross-tab breaks from three tabs during Stage 2A. | All tabs |
| RSK-06 | git ls-files returns empty for the entire Platform tree. | All tabs |
| RSK-07 | 221/221 tests, 12 tables, zero production datasets, zero screens. | Second Tab |
| RSK-08 | 274/274 tests; CommunicationPlatformRegistration not called in Program.cs; no hosted worker. | Third Tab |
| RSK-09 | Legacy DocCommentService plus the Comm platform. | Third Tab |
| RSK-10 | Legacy NotifyAsync plus the projection consumer; a path bypassing the outbox was observed. | Third Tab |
| RSK-11 | C1 services are in Program.cs; construction_c1 DDL is applied nowhere. | Fourth Tab |
| RSK-12 | Five legacy base tables lack concurrency control. | Fourth Tab |
| RSK-13 | Tasks has 2 registered hosted services and no owner; Calendar has a live service and no owner. | Owner |
| RSK-14 | RecordAsync has no swallowing catch; ReverseAsync depends on BusinessEvents. | Integration Owner |
| RSK-15 | The kernel requires a signed-in context; a new background writer would throw. | Integration Owner |
| RSK-16 | PlatformOpsAttribute falls back to Accounting.manage. | First Tab |
| RSK-17 | Mutation H first passed against a stale assembly under --no-build. | All tabs |
| RSK-18 | Reporting, Communication and Construction C1: 533 tests, 34 tables, 0 screens. | Owner |
| RSK-19 | AI services exist with no AI-specific authorization; no search exists. | Owner |
| RSK-20 | No MDM; Employee, Customer, Item and Supplier shared informally. | Owner |
| RSK-21 | manifest.json classifies hm16_rename_grni.sql as 'review' - a mutating batch with no re-run guard that nobody has read - and script.sql as 'not-deployable'. | Integration Owner |
| RSK-22 | deploy/README.md states the authoritative way to stand up the schema is BACKUP/RESTORE of CrossBuyDB2, because EF migrations are broken and ~90 ad-hoc scripts in C:/temp built the schema - a third source outside the repository. | Integration Owner |
| RSK-23 | A unified surface aggregating cross-module data is the classic place for an authorization shortcut. | To be assigned |
| RSK-24 | D-36 makes CrossBusiness Blue canonical while crossbuy-brand.css globally overrides to green and gold across 327 views. | Owner |
