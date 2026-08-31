# CrossBusiness Platform — Roadmap v2 — 09 Parallel Execution Model

**The problem this solves: five cross-tab build breaks from three tabs during Stage 2A,
and a whole platform tree that is still uncommitted.**

Machine-readable: `roadmap-v2-tab-ownership.csv`, `roadmap-v2-shared-files.csv`,
`roadmap-v2-adr-register.csv`, `roadmap-v2-sql-slice-register.csv`.

---

## 1. What actually went wrong

| Observation | Consequence |
|---|---|
| Five build breaks from three tabs (Reporting `ReportParameterSet`/`IReportService`, Communication `ICommActorDirectory`, Construction `CrossBuy.BL.Construction`/`BoqLineInput`) | Acceptance blocked repeatedly; one increment had to run on an older binary and declare it |
| `git ls-files` returns **empty** for the whole `Platform/` tree | Every tab's base can shift invisibly; selective commits diff against nothing stable |
| Two `deploy/sql` trees, 4 overlapping files, all 4 different | A deploy can apply the wrong version of a slice |
| A stale assembly produced a passing mutation test | Evidence that proved nothing |

The common cause is not carelessness — it is that **a shared mutable tree has no mechanism
to reject a broken state**. Every model below is judged on whether it does.

## 2. Model comparison

| Model | Isolation | Break containment | Integration cost | Verdict |
|---|---|---|---|---|
| 1. Single shared working tree (today) | None | None — one tab breaks all four | Zero | **Rejected.** This is the observed failure. |
| 2. Sequential tabs | Total | Total | Zero | **Rejected.** Four tabs would run at one quarter speed. |
| 3. Per-tab branch | High | High | Merge conflicts on shared files | **Viable**, but each tab still needs its own checkout to build. |
| 4. Per-tab git worktree | High | High | Same as 3, plus disk | **RECOMMENDED.** Each tab gets a real working directory on its own branch from one clone. |
| 5. Integration branch | N/A | N/A | N/A | **Required alongside 4** — this is where the four branches meet and must stay green. |

## 3. Recommendation — per-tab worktree plus a protected integration branch

Model **4 + 5**. Each tab develops in its own worktree on its own branch; an `integration`
branch is the single place they combine, and it must build.

Worktrees rather than separate clones because the four tabs share one object store and one
SQL slice registry — a second clone would let the registries diverge exactly the way
`deploy/sql` already has.

### The binding rule

> **No tab may leave the shared integration branch unbuildable.**

Operationally: a tab merges to `integration` only after `dotnet build` succeeds in **Debug,
Release and TestRun** in its own worktree, and the full suite is green. A merge that breaks
`integration` is reverted immediately — not fixed forward, because a fix-forward leaves the
other three tabs blocked for the duration.

## 4. Rules

| Rule | Statement |
|---|---|
| Buildability | Debug, Release and TestRun all build before merge. A build error count is captured explicitly; `--no-build` is never the build. |
| Commit | Every tab commits its own files daily. The current state — an entire untracked platform tree — is itself the top process risk (RSK-06). |
| Shared file | Shared files are edited only inside the tab's marked region, or by request to the Integration Owner. resx files get our keys only via git plumbing, never whole-file `git add`. |
| ADR number | Reserve in the ADR registry before writing. ADR-011, 014, 015, 017–021 are free; ADR-038–040 are reserved for R1/R3. |
| SQL slice | Claim the slice name in the SQL registry before authoring. One tree only after R1. |
| Migration ownership | The owning tab authors the slice; the Integration Owner sequences application. Idempotent SQL only — never EF migrations. |
| Program.cs | Integration Owner. Each platform exposes ONE registration extension method (the Reporting tab's `AddCrossBusinessReporting` is the pattern to copy) so the shared file gains one line, not forty. |
| CrossDbContext | Integration Owner. DbSets added in the tab's marked region; never reordered, never reformatted. 245 DbSets make whole-file edits certain conflicts. |
| Integration cadence | Daily merge to `integration` per tab. |
| Full-suite cadence | Full suite on `integration` at least daily, and before any tab starts a new phase. |
| Rollback | Revert the merge commit, not the tab's branch. The tab keeps its history and re-merges when green. |
| Artifact preservation | Every increment archives its own files with SHA-256 per file and a restore-verify requiring 0 mismatches. The archive hash is recorded outside the archive. |
| Stale-build prevention | Delete build outputs before an acceptance build; record the assembly timestamp; never accept a test result from an unverified build. |

## 5. Tab Ownership Register

| TabId | Tab | Scope | Status |
|---|---|---|---|
| TAB-1 | First Tab - Core Security and Platform Governance | Authorization, bootstrap policy, grants, analyzer, security console. | Active - B6 closed |
| TAB-2 | Second Tab - Reporting Platform | Reporting engine, datasets, exporters, Report Center and Studio. | Active - foundation complete, activation in R2 |
| TAB-3 | Third Tab - Communication and Collaboration | Comm platform, threads, mentions, notifications, timeline. | Active - foundation complete, activation in R2 |
| TAB-4 | Fourth Tab - Construction and Contracting | BOQ, subcontracts, commercial revisions, construction C2-C8. | Active - C1 verified, DDL applied nowhere |
| TAB-5 | Tasks and Calendar - UNASSIGNED | Task state and rules; calendar events and unified presentation. | UNASSIGNED - 2 live hosted services with no owner (R1 action) |
| TAB-6 | CrossBusiness Workspace | Workspace shell, My Work, approvals, mentions and activity surfaces (R3). APPOINTED NARROWLY for the read-only cross-silo approval inbox: aggregation, read DTOs, navigation metadata and Workspace consumption. Approval WRITES (approve/reject/post/release/confirm/escalate) stay with the module services. | APPOINTED for read-only approval inbox (owner ruling); wider Workspace R3 scope still unassigned |

### Owned paths

| TabId | OwnedPaths | MayNotTouch |
|---|---|---|
| TAB-1 | CrossBuy.Analyzers/**; CrossBuy.Analyzers.Tests/**; CrossBuy/BL/Platform/Bootstrap*; CrossBuy/BL/Platform/PlatformGrant*; CrossBuy/BL/Platform/Timeline*; CrossBuy/BL/*AccessService.cs; CrossBuy/Models/Platform/Bootstrap*; CrossBuy/Models/Platform/PlatformGrant*; CrossBuy/Models/Context/Platform/BootstrapAccessPolicy.cs; CrossBuy/Models/Context/Platform/PlatformRoleAssignment.cs; CrossBuy/Controllers/Api/PlatformGrantsApiController.cs; engineering/**; docs/platform/Stage-002A-*; docs/roadmap-v2/**; CrossBuy/CrossBuy.csproj; CrossBuy.sln; CrossBuy/Resources/**; CrossBuy/Views/Shared/**; CrossBuy/deploy/**; deploy/**; governance/**; .github/workflows/**; docs/platform/ADR-*; docs/deployment/**; CrossBuy/BL/Platform/**; CrossBuy/Models/Platform/**; CrossBuy/Models/Context/Platform/**; CrossBuy/BL/IntegrityCheckHostedService.cs; CrossBuy/BL/CrmReminderHostedService.cs; CrossBuy.Tests/WriterWorkerGovernanceTests.cs; CrossBuy.Tests/EntityConversationPartialContractTests.cs; CrossBuy.Tests/HrBootstrapPolicyTests.cs; CrossBuy.Tests/DocumentAccessFoundationTests.cs; CrossBuy.Tests/OnboardingLocalizationTests.cs; CrossBuy.Tests/DocumentSubmitContractTests.cs; CrossBuy.Tests/RosterLocalizationTests.cs; docs/handoff/** | Reporting, Communication, Construction trees |
| TAB-2 | CrossBuy/BL/Reporting/**; CrossBuy/Models/Context/Reporting/**; CrossBuy/Controllers/Api/ReportsCenterApiController.cs; CrossBuy/Views/Reports*/**; CrossBuy/deploy/sql/reporting_platform.sql; docs/platform/ADR-037*; CrossBuy/Controllers/Api/EmployeesApiController.cs; CrossBuy/Controllers/Api/HrApiController.cs; CrossBuy/Controllers/Api/LeaveApiController.cs; CrossBuy/Controllers/AdminController.Appraisals.cs; CrossBuy/Controllers/AdminController.Recruitment.cs; CrossBuy/Controllers/AdminController.Training.cs; CrossBuy/Controllers/PeopleController.cs; CrossBuy.Tests/LeaveApproverCompanyBoundaryTests.cs; CrossBuy.Tests/Hr/**; CrossBuy/deploy/sql/hr*.sql; deploy/sql/hr*.sql; CrossBuy/BL/Hr/EmployeeOnboardingService.cs; CrossBuy/Controllers/EmployeeOnboardingController.cs; CrossBuy/Models/Context/Hr/EmployeeOnboarding.cs; CrossBuy/Views/EmployeeOnboarding/**; CrossBuy/BL/Hr/RosterService.cs; CrossBuy/Controllers/RosterController.cs; CrossBuy/Models/Context/Hr/Roster.cs; CrossBuy/Views/Roster/** | Security, Communication, Construction trees |
| TAB-3 | CrossBuy/BL/Communication/**; CrossBuy/BL/Comm/**; CrossBuy/Models/Context/Communication/**; CrossBuy/Models/Context/Comm/**; CrossBuy/Models/Communication/**; CrossBuy/deploy/sql/communication_platform_slice_001.sql; CrossBuy/deploy/sql/comm_*; docs/platform/ADR-03[0-6]*; CrossBuy/BL/ChatService.cs; CrossBuy/Controllers/ChatController.cs; CrossBuy/Hubs/ChatHub.cs; CrossBuy/Views/Chat/**; CrossBuy/Resources/Views/Chat/**; CrossBuy/Models/Context/Chat/**; CrossBuy/deploy/sql/comm_chat_*.sql; CrossBuy/Hubs/ChatHub.cs; CrossBuy/Hubs/NotificationsHub.cs; CrossBuy.Tests/Communication/QuotationConversationTests.cs; CrossBuy/BL/Documents/**; CrossBuy/Models/Context/Documents/**; CrossBuy.Tests/Communication/PlatformDocumentTests.cs; CrossBuy/deploy/sql/documents_*.sql; CrossBuy/Controllers/DocumentsController.cs | Security, Reporting, Construction trees |
| TAB-4 | CrossBuy/BL/Construction/**; CrossBuy/BL/BoqService.cs; CrossBuy/BL/SubcontractBillingService.cs; CrossBuy/BL/VariationOrderService.cs; CrossBuy/Models/Context/Construction/**; deploy/sql/construction_c1_*; docs/construction/** | Security, Reporting, Communication trees |
| TAB-5 | CrossBuy/BL/TaskService.cs; CrossBuy/BL/TasksAccessService.cs; CrossBuy/BL/TaskGeneratorHostedService.cs; CrossBuy/BL/TaskScheduleMatchHostedService.cs; CrossBuy/BL/CalendarService.cs; CrossBuy/Controllers/TasksController.cs; CrossBuy/Controllers/CalendarController.cs; CrossBuy/BL/TasksCalendar/**; CrossBuy/Views/Tasks/**; CrossBuy/Views/Calendar/**; CrossBuy/deploy/sql/tasks_*; CrossBuy/deploy/sql/calendar.sql | Other tabs' trees |
| TAB-6 | CrossBuy/BL/Workspace/**; CrossBuy/Controllers/WorkspaceController.cs; CrossBuy/Views/Workspace/**; CrossBuy/BL/Approvals/**; CrossBuy/Controllers/ApprovalsController.cs; CrossBuy/Views/Approvals/**; CrossBuy/BL/ApprovalInboxDto.cs; CrossBuy/Controllers/ProjectController.cs; CrossBuy/BL/ProjectMembershipService.cs; CrossBuy/Models/Context/Accounting/ProjectMember.cs; CrossBuy/Views/Project/Team.cshtml; CrossBuy.Tests/Projects*Tests.cs; CrossBuy/BL/ProgressBillingService.cs; CrossBuy/Models/Context/Accounting/ProgressBilling.cs; CrossBuy/Views/Project/Billing.cshtml; CrossBuy.Tests/ApprovalInboxAggregatorTests.cs; CrossBuy.Tests/ApprovalCompanyIsolationTests.cs; CrossBuy/BL/ProjectCloseoutService.cs; CrossBuy/Models/Context/Accounting/ProjectCloseout.cs; CrossBuy/Controllers/ProjectCloseoutController.cs; CrossBuy/Views/ProjectCloseout/**; CrossBuy.Tests/ProjectCloseoutTests.cs | Other tabs' trees; approval WRITE paths in module services |

**Two modules have no owner and live hosted services: Tasks and Calendar** (D-32, D-33).
Assign before R5.

## 6. Shared File Register

| FileId | File | Owner | ChangeRule | Reason |
|---|---|---|---|---|
| SHF-01 | CrossBuy/Program.cs | Integration Owner | Append inside the tab's own marked region only; ONE registration extension method per platform; never reorder another tab's block. | Every tab registers services; unmarked edits collide on every change. AddCrossBusinessReporting is the pattern. |
| SHF-02 | CrossBuy/Models/Context/CrossDbContext.cs | Integration Owner | Add DbSets in the tab's marked region; never reorder; never reformat. | 245 DbSets; whole-file edits guarantee conflicts. |
| SHF-03 | CrossBuy/CrossBuy.csproj | Integration Owner | Package and analyzer references by request only. | A reference change alters every tab's build. |
| SHF-04 | CrossBuy.sln | Integration Owner | Project additions by request only. | Project GUIDs conflict destructively. |
| SHF-05 | CrossBuy/Resources/SharedResources*.resx | Integration Owner | Our keys only via git plumbing; never whole-file git add. | Files are reordered by other work; whole-file staging loses keys. |
| SHF-06 | CrossBuy/Views/Shared/_Layout*.cshtml | Integration Owner | Partial insertion by request; the partial must exist or every screen breaks. | Shared layouts already embed cross-tab partials. |
| SHF-07 | CrossBuy/deploy/sql/** (CANONICAL authored root) | Integration Owner | Claim a SliceId in the registry before authoring; regenerate manifest.json via scan-sql-manifest.ps1. | D-38 names this the canonical authored root; the manifest tooling lives beside it. |
| SHF-08 | deploy/** (deployment PACKAGE root) | Integration Owner | Generated artifacts and operational scripts only - NOT an authoring root after D-38. | deploy/sql/fresh is generated by gen_fresh_db.ps1; backup and purge are operational. |
| SHF-09 | CrossBuy/deploy/sql/manifest.json | Integration Owner | Never hand-edit; regenerate with scan-sql-manifest.ps1. | The file says so itself; a hand-edit desynchronises hashes from content. |
| SHF-10 | docs/platform/ADR-*.md | Integration Owner | Reserve the number in the ADR registry before writing. | ADR numbers are sparse; collisions are silent. |
| SHF-11 | CrossBuy/wwwroot/css/crossbuy-brand.css | Integration Owner | FROZEN in this increment. Changes only through the D-37 approved visual rollout. | A global override flip would restyle 327 views with no design review. |
| SHF-12 | CrossBuy/BL/JournalEntryService.cs | Architectural invariant - owner coordination required | No change without pre-coordination with the owner. | The single GL writer; already kernel-wired once without coordination. |
| SHF-13 | CrossBuy/BL/StockService.cs | Architectural invariant - owner coordination required | No change without pre-coordination with the owner. | The single stock writer. |
| SHF-14 | CrossBuy/BL/LeaveWorkflowService.cs | Module workflow - TAB-6 narrow read handoff | TAB-6 may ADD read-only pending-approval readers only. The approver chain, CreateAsync, DecideAsync, approval-step writes, notifications and escalation are module-owned and must not change. | Carries the leave approver company boundary (87ec8fa). A read contract is needed by the cross-silo inbox, but the workflow itself is not TAB-6's to alter. |
| SHF-15 | CrossBuy/BL/EmployeeRequestService.cs | Module workflow - TAB-6 narrow read handoff | TAB-6 may ADD read-only pending-approval readers only. CreateAsync, DecideAsync, workflow transitions, approval writes and notification logic are module-owned and must not change. | Shares the approver chain with leave, including its defect guard. Same narrow read need, same restriction. |
| SHF-16 | governance/registry/** | Integration Owner - approved shared governance infrastructure | Never hand-edit. Change the canonical dataset in docs/roadmap-v2/_generator and regenerate; every edit needs explicit owner approval (-AllowShared) and must preserve machine-enforced ownership semantics. | These registries ARE the ownership rules the checker reads. TAB-1 owns governance/** as integration owner, but shared is evaluated BEFORE ownership, so keeping them shared forces an explicit approval on every edit instead of letting the tab that owns the rules change the rules unremarked. |
| SHF-17 | governance/tools/** | Integration Owner - approved shared governance infrastructure | Changes require explicit owner approval (-AllowShared) and must not weaken the ownership checker or its exit codes. | The enforcement tooling itself. A tab that could silently relax check-file-ownership.ps1 could grant itself any path. |
| SHF-18 | CrossBuy/BL/Platform/BusinessEventService.cs | Architectural invariant - owner coordination required | No change without pre-coordination with the owner. | The single outbox writer; the per-consumer fan-out is decided here, so one edit changes what every consumer ever sees. |
| SHF-19 | CrossBuy/Controllers/AccountingController.cs | Module surface - integration owner unblock only | Company-context remediation of named read paths only. No posting, stock, journal, receivable or payment behaviour may change under this entry. | Invoice read paths still resolve company from a compile-time constant while the product surface above them is company-scoped; the legacy business modules have no tab, so the screens cannot be fixed by their owner. |
| SHF-20 | CrossBuy/Views/Shared/_DocEventTimeline.cshtml | Integration Owner - shared document partial | Shared by three committed document views; changes require owner approval (-AllowShared). | Three committed views include this partial and it was never committed, so a clean checkout throws partial-not-found on both invoice detail screens and the work order screen. |
| SHF-21 | CrossBuy/Views/Shared/_DocTimeline.cshtml | Integration Owner - shared document partial | Shared document comments widget; changes require owner approval (-AllowShared). | The committed Inventory/QuotationDetails view includes this partial and it was never committed, so a clean checkout throws partial-not-found on the quotation screen. |
| SHF-22 | CrossBuy/deploy/sql/project_members.sql | Integration Owner - deployment slice | Schema slice for a table committed product code queries; changes require owner approval (-AllowShared). | ProjectsAccessService queries dbo.ProjectMembers but the DDL that creates it was never committed, so a fresh deployment has no such table. |
| SHF-23 | CrossBuy/Views/Accounting/SalesInvoiceDetail.cshtml | Integration Owner - shared conversation caller | Includes the shared _EntityConversation panel and must name its own two conversation endpoints; the partial resolves none on a caller's behalf. Accounting feature work and Platform contract changes both land here, so changes require owner approval (-AllowShared). | The conversation endpoints used to be hardcoded inside the shared partial as Url.Action of an Accounting action, so any second module including it posted to the Accounting controller. Moving them to the caller binds this view to a shared platform contract that a tab other than the Accounting owner maintains. |
| SHF-24 | CrossBuy/Views/Accounting/PurchaseInvoiceDetail.cshtml | Integration Owner - shared conversation caller | Includes the shared _EntityConversation panel and must name its own two conversation endpoints; the partial resolves none on a caller's behalf. Accounting feature work and Platform contract changes both land here, so changes require owner approval (-AllowShared). | The conversation endpoints used to be hardcoded inside the shared partial as Url.Action of an Accounting action, so any second module including it posted to the Accounting controller. Moving them to the caller binds this view to a shared platform contract that a tab other than the Accounting owner maintains. |
| SHF-25 | CrossBuy/Views/Inventory/QuotationDetails.cshtml | Integration Owner - shared conversation caller | Includes the shared _EntityConversation panel and must name its own two conversation endpoints; the partial resolves none on a caller's behalf. Inventory feature work and Platform contract changes both land here, so changes require owner approval (-AllowShared). | Same contract as SHF-23/24 on the invoice views. This screen was the last one rendering the legacy _DocTimeline widget into DocComments; converging it onto the Communication Platform binds an Inventory view to a shared platform contract that the Inventory owner does not maintain. |
| SHF-26 | CrossBuy/Controllers/InventoryController.cs | Module surface - conversation endpoints only | Communication conversation endpoints for Inventory-owned documents only (QuotationConversation, QuotationConversationAdd) and their shared gate. No stock, pricing, procurement, warehouse, approval or costing behaviour may change under this entry. | The quotation conversation must be authorized by the module that owns the document rather than routed through AccountingController, so the endpoints live here; but the file is the Inventory module surface and is not TAB-3 work. Narrow entry mirroring SHF-19 rather than transferring a 2900-line controller. |
| SHF-27 | CrossBuy/Controllers/AdminController.cs | Integration Owner - mixed HR / organisation administration surface | HR Phase-1 sections ONLY. TAB-2 may modify the holiday, attendance, final-settlement, HR document and contract, leave accrual/encashment/carry-over/provision, leave type and leave policy, salary policy, attendance policy and employee actions (HolidaysList..DeleteHoliday, Attendance..SaveAttendance, FinalSettlement, PostFinalSettlement, HrDocuments..DeleteDocument, DocExpiryAlerts, NotifyExpiring, LeaveAccrual..SaveLeaveTypeEncashable, Polices..GetPolicy, EmployeesList, EmployeeData, GetPolicies, GetEmployeeByUserId, SaveEmployee). It may NOT modify the organisation-administration actions on the same file: JobTitlesList, GetJobTitles, JobTitle, GetBranches, GetCompanies, AdministrativeBodiesCompanyList, AdministrativeBodiesCompany, AdministrativeStructure, GetHierarchicals, AddHierarchicalItem, GetParentInfo, Index. AddHierarchicalItem WRITES a node into dbo.Hierarchicals, which carries no CompanyID and is one tree shared by every company, so it is organisation administration and not HR. Changes require owner approval (-AllowShared). | 1064 lines carrying two different surfaces. Roughly two thirds is HR - holidays, attendance, final settlement, HR documents, contracts, leave accrual and policies, salary and attendance policy, employees - but the rest administers companies, branches, job titles and the administrative hierarchy, which is org administration and not HR. Handing the whole file to the HR implementer as ownership would transfer company and branch administration with it, so the file stays shared and the rule names the exact HR actions instead. The three sibling partials (Appraisals, Recruitment, Training) are 100 percent HR and are narrow TAB-2 ownership rather than shared. |
| SHF-28 | CrossBuy/Controllers/Api/DevSeedController.cs | Integration Owner - shared development seed harness | Seed data for a tab's OWN feature only, appended as its own block. A tab may add the rows its feature needs and adjust the seed calls that construct them; it may not restructure the harness, reorder another tab's block, or change seed data belonging to a module it does not own. Changes require owner approval (-AllowShared). | 15,000 lines that every module extends as it lands, and the evidence is live rather than theoretical: at the time of this entry the file carried 283 uncommitted lines from one tab while another tab's batch was adding 32 more to a different region. Granting it to whichever tab touched it last would hand that tab every other module's seed data; leaving it unclaimed fails -Strict for everyone. Shared with a narrow per-feature rule is what it actually is. |

## 7. ADR Registry

| AdrId | Title | Area | Owner | Status |
|---|---|---|---|---|
| ADR-001 | Transactional Business Events | Platform Kernel | Platform | Accepted |
| ADR-002 | Entity Registry | Platform Kernel | Platform | Accepted |
| ADR-003 | Dispatch State Per Consumer | Platform Kernel | Platform | Accepted |
| ADR-004 | Business Event Visibility | Platform Kernel | Platform | Accepted |
| ADR-005 | Legacy Timeline Adapters | Platform Kernel | Platform | Accepted |
| ADR-006 | Notification Projection | Platform Kernel | Platform | Accepted |
| ADR-007 | SQL Server Dispatch Locking | Platform Kernel | Platform | Accepted |
| ADR-008 | Operator Dispatch Retry | Platform Kernel | Platform | Accepted |
| ADR-009 | Platform Operations Authorization | Core Security | First Tab | Accepted |
| ADR-010 | Session-Free Permission Evaluation | Core Security | First Tab | Accepted |
| ADR-011 | RESERVED - unused | - | Integration Owner | Available |
| ADR-012 | Deployment Manifest and Schema History | Deployment | Integration Owner | Accepted - superseded in part by ADR-039 |
| ADR-013 | Single Worker Process | Platform Kernel | Platform | Accepted |
| ADR-014 | RESERVED - unused | - | Integration Owner | Available |
| ADR-015 | RESERVED - unused | - | Integration Owner | Available |
| ADR-016 | Reproducible Architecture Evidence | Governance | First Tab | Accepted |
| ADR-017 | RESERVED - unused | - | Integration Owner | Available |
| ADR-018 | RESERVED - unused | - | Integration Owner | Available |
| ADR-019 | RESERVED - unused | - | Integration Owner | Available |
| ADR-020 | RESERVED - unused | - | Integration Owner | Available |
| ADR-021 | RESERVED - unused | - | Integration Owner | Available |
| ADR-022 | BusinessContext Resolution Policy | Core Security | First Tab | Accepted |
| ADR-023 | Company Isolation Bypass | Core Security | First Tab | Accepted |
| ADR-024 | Pilot Company Isolation | Core Security | First Tab | Accepted |
| ADR-025 | Accounting API Security Policy | Core Security | First Tab | Accepted |
| ADR-026 | Shared Platform RBAC | Core Security | First Tab | Accepted |
| ADR-027 | HR Permission Model | Core Security | First Tab | Accepted |
| ADR-028 | Projects Permission Model | Core Security | First Tab | Accepted |
| ADR-029 | Tasks and Communication Permission Models | Core Security | First Tab | Accepted |
| ADR-030 | Communication Platform Architecture | Communication | Third Tab | Accepted - RETAINED |
| ADR-031 | Universal Collaboration Surface | Communication | Third Tab | Accepted - RETAINED |
| ADR-032 | Mention Resolution | Communication | Third Tab | Accepted - RETAINED |
| ADR-033 | Communication Visibility and Thread Permissions | Communication | Third Tab | Accepted - RETAINED |
| ADR-034 | Notification Channels, Templates, Preferences | Communication | Third Tab | Accepted - RETAINED |
| ADR-035 | Comment History, Soft Delete, Audit | Communication | Third Tab | Accepted - RETAINED |
| ADR-036 | Timeline Aggregation | Communication | Third Tab | Accepted - RETAINED |
| ADR-037 | Reporting Platform Architecture | Reporting | Second Tab | Accepted - CONFIRMED as the Reporting ADR |
| ADR-038 | RESERVED - Parallel execution model (worktrees + integration branch) | Governance | Integration Owner | Reserved for R1 |
| ADR-039 | RESERVED - Canonical SQL root and PlatformSchemaHistory | Deployment | Integration Owner | Reserved for R1 |
| ADR-040 | RESERVED - Master Data ownership register | Master Data | Integration Owner | Reserved for R6 |
| ADR-041 | RESERVED - ExternalPrincipalContext | Core Security | First Tab | Reserved for R11 - required by D-13 |
| ADR-042 | RESERVED - CrossBusiness Workspace architecture and delegation rule | Product | To be assigned | Reserved for R3 |
| ADR-043 | RESERVED - CrossBusiness Blue visual identity and rollout | Product | Integration Owner | Reserved for R3 - required by D-36/D-37 |
| ADR-044 | RESERVED - Entity registration declarations (privacy, retention, audit) | Communication | Third Tab | Reserved for R2 - required by D-05/D-06/D-07 |

## 8. SQL Slice Registry

**This registry exists because the SQL tree has already split.** RSK-01 is the highest-severity
finding of this reassessment.

| SliceId | Slice | Tree | Owner | AppliedStatus | GatesOurPath | Note |
|---|---|---|---|---|---|---|
| SQL-01 | platform_business_events.sql | CANONICAL (CrossBuy/deploy/sql) | Platform | Applied on acceptance DB | Yes - sale, purchase, reversal | Missing causes SQL-208 on a real sale. |
| SQL-02 | platform_business_events_slice_002.sql | CANONICAL | Platform | Applied on acceptance DB | Yes | Notifications.EntityType/EntityId; missing causes SQL-207. Apply with sqlcmd -I. |
| SQL-03 | platform_role_assignments.sql | CANONICAL | First Tab | Authored | No | Batch A grant storage. |
| SQL-04 | platform_role_assignments_slice_002.sql | CANONICAL | First Tab | Authored | No | Batch A extension. |
| SQL-05 | bootstrap_access_policies.sql | CANONICAL | First Tab | Authored - NOT applied | No | B6 policy store; 6 CHECKs, filtered unique index, drift detection. Applies in R6. |
| SQL-06 | reporting_platform.sql | CANONICAL | Second Tab | A0 - deployed via the pipeline and recorded in PlatformSchemaHistory (probe-verified); NOT applied to CrossBuyDB2 | No | 12 tables. Deployed with apply-sql-slices.ps1 -Slice, which validates a named slice on the same terms as the global pre-flight so an unrelated backlog cannot block a clean one. Applied hash matches the manifest exactly. |
| SQL-07 | communication_platform_slice_001.sql | CANONICAL | Third Tab | Authored - NOT applied | No | 14 tables; from-empty and idempotency proved. Applies in R2. |
| SQL-08 | comm_outbox_slice_003.sql | CANONICAL | Third Tab | Authored | No | Legacy Comm module outbox - distinct from the Communication Platform. |
| SQL-09 | platform_schema_history.sql | CANONICAL | Integration Owner | Authored - NOT applied | No | RSK-02. D-40 makes this the APPLIED registry and the first slice in every environment. |
| SQL-10 | construction_c1_commercial_foundation.sql | PACKAGE (deploy/sql) | Fourth Tab | Authored - NOT applied | No | 8 tables. Applies in R2 in the D-19 environment only. Migrates to canonical root in R1. |
| SQL-11 | construction_c1_contract_mapping_measurement.sql | PACKAGE | Fourth Tab | Authored - NOT applied | No | M3/M9 measurement script. Run in R2 after SQL-10. |
| SQL-12 | pos_hold_recall.sql | BOTH - DIVERGENT | POS owner | Unknown | No | manifest.json: identical=false, distinctHashes=2. Reconcile per D-39. |
| SQL-13 | pos_order_guests.sql | BOTH - DIVERGENT | POS owner | Unknown | No | manifest.json: identical=false, distinctHashes=2. Reconcile per D-39. |
| SQL-14 | pos_quickmenu.sql | BOTH - DIVERGENT | POS owner | Unknown | No | manifest.json: identical=false, distinctHashes=2. Reconcile per D-39. |
| SQL-15 | pos_setup.sql | BOTH - DIVERGENT | POS owner | Unknown | No | manifest.json: identical=false, distinctHashes=2. Reconcile per D-39. |
| SQL-16 | hm16_rename_grni.sql | CANONICAL | Hyper track | Unknown | No | manifest.json idempotency = REVIEW: a mutating batch with no re-run guard that nobody has read. DO NOT DEPLOY until resolved (RSK-21). |
| SQL-17 | script.sql | REPOSITORY ROOT | Unknown | Never | No | manifest.json idempotency = NOT-DEPLOYABLE and excludedFromDeploy. Judged unsafe to re-run. |
| SQL-18 | 00_backup_dev_db.sql | PACKAGE | Integration Owner | Operational | No | excludedFromDeploy - operational, not a schema slice. |
| SQL-19 | 10_purge_for_production.sql | PACKAGE | Integration Owner | Operational | No | excludedFromDeploy - run once after restore. |
| SQL-20 | deploy/sql/fresh/*.sql | PACKAGE - GENERATED | Integration Owner | Generated by gen_fresh_db.ps1 | No | Generated schema build; not hand-authored. Confirms deploy/ is the package root, not an authoring root. |
| SQL-21 | (53 further module slices, package tree only) | PACKAGE | Various | Various | No | HR, CRM, projects, pricing, manufacturing, notifications, announcements, chat, boq, brand. Migrate to canonical root in R1 with hash verification. |
| SQL-22 | (50 further module slices, canonical tree only) | CANONICAL | Various | Various | Some | Hyper (hm*), POS, tasks (7 slices), calendar, library, inventory reconcile. |

## 9. Integration Gate Checklist

A merge to `integration` is accepted only when every line is true:

| # | Gate |
|---|---|
| 1 | Debug build `0 Error(s)` — error count captured, not inferred |
| 2 | Release build `0 Error(s)` |
| 3 | TestRun build `0 Error(s)` |
| 4 | Full suite green, skipped count explained (never claim coverage from a skip) |
| 5 | Analyzer gates: CBA001 = 0, CBA004 = 0, CBA006 = 0 |
| 6 | Authorization debt unchanged or **lower**, reconciled if changed |
| 7 | No suppressions, no baseline additions |
| 8 | Only files in the tab's owned paths are modified |
| 9 | Shared-file edits confined to the tab's marked region |
| 10 | Any new SQL slice is claimed in the registry and idempotent |
| 11 | Any new hosted service is added to the DI wiring test |
| 12 | 0 probe databases, 0 mutation markers left behind |
