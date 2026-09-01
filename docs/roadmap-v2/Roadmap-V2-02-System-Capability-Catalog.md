# CrossBusiness Platform — Roadmap v2 — 02 System Capability Catalog

**One canonical catalog. Generated from `_generator/roadmap_v2_data.py`; the CSV and this
table come from the same rows, so they cannot disagree.**

Capabilities: **109** across **28** platforms and modules.

Machine-readable: `roadmap-v2-capability-catalog.csv`.

---

## 1. Status vocabulary

Only these values are used. `VerifiedComplete` means evidence exists; it never means
"a user can do this" — that is `ProductionActivation` plus `UIStatus`.

| Status | Meaning |
|---|---|
| VerifiedComplete | Implemented and proved by test or mutation evidence |
| ImplementedNotActivated | Code complete, not reachable in production |
| FoundationComplete | Services and schema complete, no user surface |
| ArchitectureComplete | Design decided, little or no code |
| Partial | Some capability, materially incomplete |
| LegacyExisting | Pre-existing working capability, not re-verified |
| RemediationRequired | Exists but carries a known defect or gap |
| Planned | Agreed, not started |
| BlockedByDecision | Cannot proceed without an owner decision |
| BlockedByData | Cannot proceed without a migration or measurement |
| Deferred | Deliberately postponed |
| NotStarted | No source |

## 2. Distribution

| Status | Capabilities |
|---|---|
| LegacyExisting | 41 |
| VerifiedComplete | 17 |
| NotStarted | 15 |
| FoundationComplete | 11 |
| Partial | 10 |
| BlockedByDecision | 4 |
| Deferred | 4 |
| ImplementedNotActivated | 3 |
| BlockedByData | 2 |
| Planned | 1 |
| RemediationRequired | 1 |

| Platform or module | Capabilities |
|---|---|
| Communication and Collaboration | 10 |
| Platform Framework | 9 |
| Reporting Platform | 9 |
| Accounting | 8 |
| Core Security | 8 |
| HR | 7 |
| Construction and Contracting | 5 |
| Inventory and WMS | 5 |
| POS | 5 |
| Task Management | 5 |
| CRM | 4 |
| Projects | 4 |
| AI Platform | 3 |
| Audit and Compliance | 3 |
| Notifications | 3 |
| Calendar | 2 |
| Customer Portal | 2 |
| Files and Document Management | 2 |
| Identity and Access | 2 |
| Manufacturing | 2 |
| Purchasing | 2 |
| Support and Customer Service | 2 |
| Workflow and Approvals | 2 |
| CrossBusiness Workspace | 1 |
| Integration Hub | 1 |
| Master Data | 1 |
| Mobile and Field Operations | 1 |
| Search | 1 |

## 3. Catalog — summary view

| CapabilityId | PlatformOrModule | Capability | CurrentStatus | ProductionActivation | UIStatus | TestEvidence | RecommendedPhase |
|---|---|---|---|---|---|---|---|
| ACC-01 | Accounting | General ledger and journal entries | VerifiedComplete | Active | Accounting views (66) | Stage 1 + hyper suites | R10 |
| ACC-02 | Accounting | Receivables and sales invoicing | LegacyExisting | Active | Accounting views | Hyper + Stage 1 | R10 |
| ACC-03 | Accounting | Payables and purchase invoicing | LegacyExisting | Active | Accounting views | Hyper + Stage 1 | R10 |
| ACC-04 | Accounting | Fixed assets and depreciation | LegacyExisting | Active | Accounting views | Partial | R10 |
| ACC-05 | Accounting | Fiscal periods and closing | LegacyExisting | Active | Accounting views | Partial | R10 |
| ACC-06 | Accounting | Multi-currency and FX revaluation | LegacyExisting | Active | Currency views (4) | Hyper suite | R10 |
| ACC-07 | Accounting | Financial statements | LegacyExisting | Active | Accounting views | Partial | R4 |
| ACC-08 | Accounting | Accounting API security policy | VerifiedComplete | Active | None | Stage 1 suite | R0 |
| AI-01 | AI Platform | AI service | Partial | Active | None | None identified | R12 |
| AI-02 | AI Platform | AI insights | Partial | Active | None | None identified | R12 |
| AI-03 | AI Platform | AI governance | BlockedByDecision | None | None | None | R12 |
| AUD-01 | Audit and Compliance | Construction audit trail | VerifiedComplete | Code active, DDL not applied | None | 38/38 C1 suite | R7 |
| AUD-02 | Audit and Compliance | Communication audit | FoundationComplete | NOT activated | None | 274/274 | R2 |
| AUD-03 | Audit and Compliance | Platform-wide audit and compliance | NotStarted | None | Not started | None | R10 |
| CAL-01 | Calendar | Calendar service | Partial | Active | 1 view only | None identified | R5 |
| CAL-02 | Calendar | Calendar workspace UI | NotStarted | None | Not started | None | R5 |
| COM-01 | Communication and Collaboration | Threads and comments | FoundationComplete | NOT activated in Program.cs | None | 274/274 communication suite | R2 |
| COM-02 | Communication and Collaboration | Mentions | FoundationComplete | NOT activated | None | 274/274 | R2 |
| COM-03 | Communication and Collaboration | Notification channels, templates, preferences | FoundationComplete | NOT activated | None | 274/274 | R2 |
| COM-04 | Communication and Collaboration | Timeline aggregation | FoundationComplete | NOT activated | None | 274/274 | R2 |
| COM-05 | Communication and Collaboration | CommEntityRef and ICommEntitySurface | FoundationComplete | NOT activated | None | 274/274 | R2 |
| COM-06 | Communication and Collaboration | Task and Support registration | NotStarted | None | None | None | R5 |
| COM-07 | Communication and Collaboration | DocComments migration | BlockedByData | Not executed | None | None | R2 |
| COM-08 | Communication and Collaboration | Production activation | ImplementedNotActivated | NOT called in Program.cs | None | 274/274 in isolation | R2 |
| COM-09 | Communication and Collaboration | Communication UI | NotStarted | None | Not started | None | R3 |
| COM-10 | Communication and Collaboration | Legacy Comm module | LegacyExisting | Active - hosted service registered | Comm views (3) | 13 comm test files | R2 |
| CON-01 | Construction and Contracting | Stable BOQ identity | VerifiedComplete | Code active, DDL NOT applied | None | 38/38 C1 suite | R7 |
| CON-02 | Construction and Contracting | Subcontract certification cap | VerifiedComplete | Code active, DDL NOT applied | None | 38/38 C1 suite | R7 |
| CON-03 | Construction and Contracting | Immutable commercial revisions | VerifiedComplete | Code active, DDL NOT applied | None | 38/38 C1 suite | R7 |
| CON-04 | Construction and Contracting | C1 production rollout | BlockedByData | Not executed | None | Measurement script not run | R2 |
| CON-05 | Construction and Contracting | C2-C8 construction scope | Deferred | None | Not started | None | R7 |
| CRM-01 | CRM | Customer and lead management | LegacyExisting | Active | Crm views (32) | Batch C access tests | R6 |
| CRM-02 | CRM | CRM automation and reminders | LegacyExisting | Active - hosted service registered | Crm views | Partial | R6 |
| CRM-03 | CRM | CRM custom fields | LegacyExisting | Active | Crm views | Partial | R6 |
| CRM-04 | CRM | Visible-owner scope | BlockedByDecision | Legacy behaviour live | None | Non-change asserted | R6 |
| FIL-01 | Files and Document Management | File manager | LegacyExisting | Active | 1 view | None identified | R1 |
| FIL-02 | Files and Document Management | Document control | NotStarted | None | Not started | None | R7 |
| HR-01 | HR | Employee master | LegacyExisting | Active | People (12) + Admin (31) views | Stage 1 partial | R8 |
| HR-02 | HR | Attendance | LegacyExisting | Active | People views | Partial | R8 |
| HR-03 | HR | Leave management | LegacyExisting | Active | People views | Partial | R8 |
| HR-04 | HR | Payroll and final settlement | LegacyExisting | Active | People views | Partial | R8 |
| HR-05 | HR | Appraisals | LegacyExisting | Active | Admin views | Partial | R8 |
| HR-06 | HR | Recruitment | LegacyExisting | Active | Admin views | Partial | R8 |
| HR-07 | HR | Training | LegacyExisting | Active | Admin views | Partial | R8 |
| IDN-01 | Identity and Access | Authentication | LegacyExisting | Active | Account views (5) | Stage 1 suite | R1 |
| IDN-02 | Identity and Access | Platform role directory | VerifiedComplete | Active | None | Batch A suite | R6 |
| INT-01 | Integration Hub | Integration hub | NotStarted | None | Not started | None | R15 |
| INV-01 | Inventory and WMS | Stock ledger | VerifiedComplete | Active | Inventory views (78) | Hyper + Stage 1 | R9 |
| INV-02 | Inventory and WMS | Warehouses, bins and sections | LegacyExisting | Active | Inventory views | Warehouse scope 10/10 | R9 |
| INV-03 | Inventory and WMS | Counts, batches and expiry | LegacyExisting | Active | Inventory views | Hyper suite | R9 |
| INV-04 | Inventory and WMS | Pricing and promotions | LegacyExisting | Active | Inventory views | Hyper suite | R9 |
| INV-05 | Inventory and WMS | Advanced WMS | NotStarted | None | Not started | None | R9 |
| MDM-01 | Master Data | Master data management | NotStarted | None | Not started | None | R6 |
| MFG-01 | Manufacturing | Work orders and BOM | LegacyExisting | Active | Inventory views | Hyper suite | R9 |
| MFG-02 | Manufacturing | Production variance | Partial | Active | None | Partial | R9 |
| MOB-01 | Mobile and Field Operations | Flutter mobile app | Partial | Ships separately | 10 Flutter screens | No mobile test evidence in suite | R14 |
| NOT-01 | Notifications | Notification service | LegacyExisting | Active | 1 view | Kernel suite | R1 |
| NOT-02 | Notifications | Realtime hub | LegacyExisting | Active | Shared layout | None identified | R1 |
| NOT-03 | Notifications | Mutes and preferences | LegacyExisting | Active | None | None identified | R2 |
| PLT-01 | Platform Framework | Business context resolution | VerifiedComplete | Active | None | Stage 1 DI + scope tests | R0 |
| PLT-02 | Platform Framework | Business events and outbox | VerifiedComplete | Active - hosted dispatcher registered | None | Kernel suite | R0 |
| PLT-03 | Platform Framework | Entity registry | VerifiedComplete | Active | None | Kernel suite | R0 |
| PLT-04 | Platform Framework | Notification projection | VerifiedComplete | Active | None | Kernel suite | R0 |
| PLT-05 | Platform Framework | Timeline aggregation | FoundationComplete | Active for onboarded entities | PlatformTimelineController only | Kernel suite | R2 |
| PLT-06 | Platform Framework | Business Event Monitor | VerifiedComplete | Active | 3 views delivered | Kernel suite | R0 |
| PLT-07 | Platform Framework | Company query filters | FoundationComplete | Active | None | Stage 1 isolation tests | R1 |
| PLT-08 | Platform Framework | Deployment manifest and schema history | Partial | Not reliably applied | None | None | R1 |
| PLT-09 | Platform Framework | Feature capabilities | LegacyExisting | Active | None | Hyper track tests | R1 |
| POS-01 | POS | Point of sale core | LegacyExisting | Active | Pos (16) + PosApp (5) views | Hyper suite | R14 |
| POS-02 | POS | Kitchen display system | LegacyExisting | Active | Pos views | Partial | R14 |
| POS-03 | POS | Delivery and reservations | LegacyExisting | Active | Pos views | Partial | R14 |
| POS-04 | POS | Offline sync and conflict handling | Partial | Active | None | Partial | R14 |
| POS-05 | POS | Hypermarket lane | VerifiedComplete | Active | Hyper views (9) | Hyper acceptance suite | R14 |
| PRJ-01 | Projects | Project master and members | LegacyExisting | Active | Project views (16) | Batch C tests | R7 |
| PRJ-02 | Projects | Progress and billing | LegacyExisting | Active | Project views | Partial | R7 |
| PRJ-03 | Projects | Material issue and labour | LegacyExisting | Active | Project views | Partial | R7 |
| PRJ-04 | Projects | Budgets | Partial | Active | Project views | Partial | R7 |
| PRT-01 | Customer Portal | Customer portal | Partial | Active | 1 view | None identified | R11 |
| PRT-02 | Customer Portal | Storefront | LegacyExisting | Active | Store views (2) | None identified | R11 |
| PUR-01 | Purchasing | Procurement and purchase orders | LegacyExisting | Active | Inventory views | Hyper suite | R10 |
| PUR-02 | Purchasing | Three-way match | LegacyExisting | Active | Inventory views | Partial | R10 |
| REP-01 | Reporting Platform | Dataset layer | FoundationComplete | Registered via AddCrossBusinessReporting | None | 221/221 reporting suite | R2 |
| REP-02 | Reporting Platform | Report catalog, templates, history, archive | FoundationComplete | Registered, no user surface | None | 221/221 | R2 |
| REP-03 | Reporting Platform | Exporters HTML, CSV, Excel | FoundationComplete | Registered, no user surface | None | 221/221 | R2 |
| REP-04 | Reporting Platform | PDF runtime | Deferred | Runtime not provisioned | None | Not covered end to end | R4 |
| REP-05 | Reporting Platform | Email delivery | Deferred | Not integrated | None | Not covered | R4 |
| REP-06 | Reporting Platform | Hosted scheduler | Deferred | Not activated | None | Not covered | R4 |
| REP-07 | Reporting Platform | Production module data source | NotStarted | None | None | None | R4 |
| REP-08 | Reporting Platform | Report Studio UI | NotStarted | None | Not started | None | R4 |
| REP-09 | Reporting Platform | Report Center UI | NotStarted | None | Not started | None | R4 |
| SEC-01 | Core Security | Roslyn authorization analyzer | VerifiedComplete | Active - build gate | None | 93/93 analyzer tests | R0 |
| SEC-02 | Core Security | Bootstrap access policy store | ImplementedNotActivated | SQL slice NOT applied to any production DB | None | 32/32 focused B6 | R0 |
| SEC-03 | Core Security | Mechanism A replacement (B6) | VerifiedComplete | Active in code | None | 32/32 + 3/3 diagnosis | R0 |
| SEC-04 | Core Security | CRM bootstrap sites | BlockedByDecision | Legacy behaviour live | None | Non-change asserted by reflection | R6 |
| SEC-05 | Core Security | Platform grant writer | ImplementedNotActivated | API present, no UI, no operator process | None | Batch A suite | R6 |
| SEC-06 | Core Security | Authorization debt backlog | RemediationRequired | Live exposure | N/A | Analyzer baseline, shrink-only | R6 |
| SEC-07 | Core Security | Company isolation bypass | VerifiedComplete | Active | None | Stage 1 suite + enforcement test | R0 |
| SEC-08 | Core Security | Security Console UI | NotStarted | None | Not started | None | R6 |
| SRC-01 | Search | Platform search | NotStarted | None | Not started | None | R12 |
| SUP-01 | Support and Customer Service | Service tickets | LegacyExisting | Active | Service views (5) | None identified | R6 |
| SUP-02 | Support and Customer Service | External principal and customer-visible comments | BlockedByDecision | None | Not started | None | R6 |
| TSK-01 | Task Management | Task core | LegacyExisting | Active | Tasks views (5) | TasksAccessService tests | R5 |
| TSK-02 | Task Management | Timesheet, labour and billing | LegacyExisting | Active | Tasks views | Partial | R5 |
| TSK-03 | Task Management | Task automation and generation | LegacyExisting | Active - hosted service registered | Tasks views | Partial | R5 |
| TSK-04 | Task Management | Scheduled task matching | LegacyExisting | Active - hosted service registered | Tasks views | Partial | R5 |
| TSK-05 | Task Management | Integrated Tasks + Calendar workspace | NotStarted | None | Not started | None | R5 |
| WFL-01 | Workflow and Approvals | Approvals inbox | Partial | Active | 1 view | None identified | R1 |
| WFL-02 | Workflow and Approvals | Generic workflow engine | NotStarted | None | Not started | None | R10 |
| WSP-01 | CrossBusiness Workspace | Unified workspace shell | Planned | None | Not started | None | R3 |

## 4. Catalog — governance view

| CapabilityId | SecurityStatus | MultiTenantStatus | Dependencies | BlockingDecision | RiskIds | SourceEvidence |
|---|---|---|---|---|---|---|
| ACC-01 | Attribute + in-body | Per company | PLT-01 | D-01 read breadth | R-18 | BL/JournalEntryService.cs |
| ACC-02 | Partial - debt present | Per company | ACC-01 | D-01 | R-19 | BL/ReceivableService.cs |
| ACC-03 | Partial - debt present | Per company | ACC-01 | D-01 | R-20 | BL/PayableService.cs |
| ACC-04 | Partial - debt present | Per company | ACC-01 | None | R-21 | BL/FixedAssetService.cs |
| ACC-05 | Partial | Per company | ACC-01,PLT-02 | None | R-22 | BL/ClosingService.cs |
| ACC-06 | Partial | Per company | ACC-01 | None | R-23 | BL/FxRevaluationService.cs; BL/CurrencyRounding.cs |
| ACC-07 | Partial | Per company | ACC-01,REP-01 | D-35 first production dataset | R-24 | BL/FinancialStatementService.cs |
| ACC-08 | Enforcing | Per company | SEC-01 | None | R-25 | BL/AccountingApiAuthorization.cs; ADR-025 |
| AI-01 | No AI-specific authorization | Per company | PLT-01 | D-27 AI governance | R-96 | BL/AiService.cs; Controllers/Api/AiController.cs |
| AI-02 | No AI-specific authorization | Per company | AI-01 | D-27 | R-97 | BL/AiInsightsService.cs |
| AI-03 | Unresolved | Unresolved | AI-01 | D-27 | R-98 | No source |
| AUD-01 | Scoped | Per company | CON-01 | D-19 | R-102 | BL/Construction/ConstructionAuditService.cs |
| AUD-02 | Scoped | Per company | COM-08 | D-07 per-entity audit policy | R-103 | BL/Communication/CommAuditWriter.cs; ADR-035 |
| AUD-03 | None | N/A | AUD-01,AUD-02 | D-06 | R-104 | No source |
| CAL-01 | CalendarController | Per company | PLT-01 | D-25 Calendar ownership | R-65 | BL/CalendarService.cs; Controllers/CalendarController.cs |
| CAL-02 | None | N/A | CAL-01,TSK-05 | D-25 | R-66 | No source |
| COM-01 | CommAccessPolicy proved | Per company | PLT-03 | D-05 privacy ceiling | R-78 | BL/Communication/CommThreadService.cs |
| COM-02 | Permission boundary proved | Per company | COM-01 | D-15 @Role membership resolution | R-79 | BL/Communication/CommMentionService.cs; ADR-032 |
| COM-03 | Applied | Per company | COM-01,NOT-01 | D-05 | R-80 | BL/Communication/CommNotificationService.cs; ADR-034 |
| COM-04 | Applied | Per company | COM-01,PLT-05 | D-12 Task timeline ownership | R-81 | BL/Communication/CommTimelineAggregator.cs; ADR-036 |
| COM-05 | Applied | Per company | PLT-03 | None | R-82 | BL/Communication/CommEntitySurface.cs; ADR-031 |
| COM-06 | N/A | N/A | COM-05,TSK-01,SUP-01 | D-12,D-13 | R-83 | Not registered |
| COM-07 | N/A | Per company | COM-01,FIL-01 | D-07 entity audit policy | R-84 | BL/DocCommentService.cs (legacy) |
| COM-08 | N/A | N/A | COM-01 | D-05,D-06 | R-85 | BL/Communication/CommunicationPlatformRegistration.cs |
| COM-09 | None | N/A | COM-08 | None | R-86 | No source |
| COM-10 | CommunicationAccessService | Per company | None | D-06 retention policy | R-87 | BL/Comm/; Controllers/CommController.cs |
| CON-01 | Project-scoped | Per company | PRJ-01 | D-19 DDL environment | R-48 | BL/BoqService.cs; ConstructionC1BoqIdentityTests |
| CON-02 | Project-scoped | Per company | CON-01 | D-19 | R-49 | BL/Construction/SubcontractScopeService.cs |
| CON-03 | Project-scoped | Per company | CON-01 | D-19 | R-50 | BL/Construction/CommercialRevisionService.cs |
| CON-04 | N/A | Per company | CON-01,CON-02,CON-03 | D-19,D-20,D-21 | R-51 | docs/construction/Stage-Construction-Contract-Mapping-Measurement.md |
| CON-05 | None | N/A | CON-04 | D-24,D-25 | R-52 | docs/construction/Stage-Construction-16-Implementation-Roadmap.md |
| CRM-01 | Bootstrap-open on unconfigured company | Per company | PLT-01 | D-03 | R-33 | BL/CrmService.cs |
| CRM-02 | Bootstrap-open | Per company | CRM-01 | D-03 | R-34 | BL/CrmAutomationService.cs; BL/CrmReminderHostedService.cs |
| CRM-03 | Bootstrap-open | Per company | CRM-01 | D-03 | R-35 | BL/CrmCustomFieldService.cs |
| CRM-04 | Unrestricted when unconfigured | Per company | SEC-04 | D-04 | R-36 | BL/CrmAccessService.cs:98 |
| FIL-01 | No dedicated access service | Per company | PLT-01 | None | R-88 | BL/FileManagerService.cs |
| FIL-02 | None | N/A | FIL-01 | None | R-89 | No source |
| HR-01 | HrAccessService | Per company | PLT-01 | D-26 Master Data ownership | R-53 | BL/EmployeeService.cs |
| HR-02 | HrAccessService | Per company | HR-01 | None | R-54 | BL/AttendanceService.cs |
| HR-03 | HrAccessService | Per company | HR-01 | None | R-55 | BL/LeaveWorkflowService.cs |
| HR-04 | Hr.payroll-manage is Never-bootstrap-open | Per company | HR-01 | None | R-56 | BL/FinalSettlementService.cs |
| HR-05 | HrAccessService | Per company | HR-01 | None | R-57 | BL/AppraisalService.cs |
| HR-06 | HrAccessService | Per company | HR-01 | None | R-58 | BL/RecruitmentService.cs |
| HR-07 | HrAccessService | Per company | HR-01 | None | R-59 | BL/TrainingService.cs |
| IDN-01 | Authentication only | Per company | None | None | R-100 | Controllers/AccountController.cs; BL/TokenService.cs |
| IDN-02 | Core control | Per company | PLT-01 | None | R-101 | BL/Platform/PlatformRoleDirectory.cs; ADR-026 |
| INT-01 | None | N/A | PLT-02 | None | R-99 | No source |
| INV-01 | Attribute + in-body | Per company | PLT-01 | D-02 cost breadth | R-26 | BL/StockService.cs |
| INV-02 | Scope enforced post-B6 | Per company | INV-01,SEC-03 | D-02 | R-27 | BL/WarehouseService.cs |
| INV-03 | Partial | Per company | INV-01 | None | R-28 | BL/ItemService.cs; hm7_count_batch.sql |
| INV-04 | Partial | Per company | INV-01 | None | R-29 | BL/PricingService.cs |
| INV-05 | None | N/A | INV-02 | None | R-30 | No source |
| MDM-01 | None | N/A | HR-01,CRM-01,INV-01 | D-26 Master Data ownership | R-105 | No source |
| MFG-01 | Partial | Per company | INV-01 | None | R-42 | BL/ManufService.cs |
| MFG-02 | Partial | Per company | MFG-01 | None | R-43 | deploy/sql/manuf_variance_520109.sql |
| MOB-01 | Depends on API authorization | Per company | IDN-01 | D-30 mobile/offline policy | R-108 | crossbuy_mobile/lib/screens (10) |
| NOT-01 | Partial | Per company | PLT-01 | None | R-90 | BL/NotificationService.cs |
| NOT-02 | Partial | Per company | NOT-01 | None | R-91 | Hubs/NotificationsHub.cs |
| NOT-03 | Partial | Per user | NOT-01 | D-06 | R-92 | deploy/sql/notification_mutes_p2.sql |
| PLT-01 | Core control | Core control | IDN-01 | None | R-09 | BL/Platform/BusinessContextFactory.cs; ADR-022 |
| PLT-02 | Scoped | Per company | PLT-01 | None | R-10 | BL/Platform/BusinessEventService.cs; ADR-001,003,007 |
| PLT-03 | N/A | N/A | PLT-02 | None | R-11 | BL/Platform/EntityRegistry.cs; ADR-002 |
| PLT-04 | N/A | Per company | PLT-02,NOT-01 | None | R-12 | BL/Platform/NotificationProjectionConsumer.cs; ADR-006 |
| PLT-05 | N/A | Per company | PLT-02,PLT-03 | None | R-13 | BL/Platform/TimelineProjectionService.cs; ADR-036 |
| PLT-06 | PlatformOps gated | Per company | PLT-02 | None | R-14 | Controllers/BusinessEventMonitorController.cs; Views/BusinessEventMonitor (3) |
| PLT-07 | Core control | Core control | PLT-01 | None | R-15 | BL/Platform/CompanyQueryFilters.cs; ADR-024 |
| PLT-08 | N/A | N/A | None | D-38,D-40 canonical root and applied registry | R-16 | deploy/sql/platform_schema_history.sql; ADR-012 |
| PLT-09 | Not an authorization control | Per company | None | None | R-17 | BL capability checks |
| POS-01 | PosAccessService | Per company | INV-01,ACC-02 | None | R-37 | BL/PosOrderService.cs |
| POS-02 | PosAccessService | Per company | POS-01 | None | R-38 | deploy/sql/pos_kds.sql |
| POS-03 | PosAccessService | Per company | POS-01 | None | R-39 | deploy/sql/pos_delivery_c*.sql |
| POS-04 | PosAccessService | Per company | POS-01 | D-27 mobile/offline policy | R-40 | deploy/sql/pos_sync_conflict_9e.sql |
| POS-05 | PosAccessService | Per company | POS-01,INV-01 | None | R-41 | Controllers/HyperController.cs; BL/SellingService.cs |
| PRJ-01 | ProjectsAccessService | Per company | PLT-01 | None | R-44 | BL/ProjectService.cs |
| PRJ-02 | Billing requires Accounting.post | Per company | PRJ-01,ACC-01 | None | R-45 | BL/ProgressBillingService.cs |
| PRJ-03 | Partial | Per company | PRJ-01,INV-01 | None | R-46 | BL/ProjectMaterialIssueService.cs |
| PRJ-04 | Partial | Per company | PRJ-01 | None | R-47 | BL/ProjectBudgetService.cs |
| PRT-01 | Public catalog scope only | Public scope pinned to Store:StoreCompanyId | PLT-01 | D-29 portal isolation | R-106 | Controllers/PortalController.cs; BL/StoreCatalogService.cs |
| PRT-02 | Public catalog scope | Public scope | PRT-01 | D-29 | R-107 | Controllers/StoreController.cs |
| PUR-01 | Single closed action - vocabulary defect | Per company | INV-01,ACC-03 | D-23 purchase action split | R-31 | BL/ProcurementService.cs |
| PUR-02 | Partial | Per company | PUR-01 | None | R-32 | BL/ThreeWayMatchService.cs |
| REP-01 | ReportAuthorizationService | Per company | PLT-01 | D-35 first production dataset | R-69 | BL/Reporting/ReportDatasetRegistry.cs |
| REP-02 | ReportPermissions mapped | Per company | REP-01 | None | R-70 | BL/Reporting/ReportCatalog.cs |
| REP-03 | Authorization applied | Per company | REP-01 | None | R-71 | BL/Reporting/HtmlReportRenderer.cs, CsvReportExporter.cs, ExcelReportExporter.cs |
| REP-04 | N/A | N/A | REP-03 | D-09 PDF runtime owner | R-72 | BL/Reporting/PlaywrightPdfReportRenderer.cs |
| REP-05 | N/A | N/A | REP-02,COM-03 | D-10 email delivery integration | R-73 | BL/Reporting/ReportDelivery.cs |
| REP-06 | N/A | N/A | REP-02 | D-11 scheduler ownership | R-74 | BL/Reporting/ReportScheduleService.cs |
| REP-07 | N/A | N/A | REP-01,ACC-07 | D-35 first production dataset | R-75 | No production binding |
| REP-08 | None | N/A | REP-07 | D-16 Studio sequence | R-76 | No source |
| REP-09 | None | N/A | REP-07 | None | R-77 | No source |
| SEC-01 | Enforcing | N/A | Build integration | None | R-01 | CrossBuy.Analyzers/AuthorizationAnalyzer.cs |
| SEC-02 | Enforcing | CompanyID scoped | SEC-03 | None | R-02 | CrossBuy/deploy/sql/bootstrap_access_policies.sql |
| SEC-03 | Enforcing | Per company | SEC-02 | None | R-03 | BL/AccountingAccessService.cs:60; BL/InventoryAccessService.cs:48,91 |
| SEC-04 | Open on unconfigured company | Per company | D-03,D-04 | D-03 CRM data breadth; D-04 visible-owner scope | R-04 | BL/CrmAccessService.cs:59,98 |
| SEC-05 | Enforcing | Company scoped | PLT-02 | None | R-05 | BL/Platform/PlatformGrantWriter.cs; Controllers/Api/PlatformGrantsApiController.cs |
| SEC-06 | 143 unprotected | Varies | SEC-01 | None | R-06 | engineering/authorization-baseline.json |
| SEC-07 | Enforcing | Core control | PLT-01 | None | R-07 | BL/Platform/CompanyIsolationBypass.cs; ADR-023 |
| SEC-08 | No console | N/A | SEC-02,SEC-05 | None | R-08 | No source |
| SRC-01 | None | N/A | PLT-03 | D-28 Search ownership | R-95 | No source |
| SUP-01 | No dedicated access service | Per company | PLT-01 | D-13 external principal | R-67 | Controllers/ServiceController.cs |
| SUP-02 | Unresolved trust boundary | Per company | COM-06 | D-13,D-14 | R-68 | ADR-033 |
| TSK-01 | TasksAccessService | Per company | PLT-01 | D-25 Task ownership | R-60 | BL/TaskService.cs |
| TSK-02 | TasksAccessService | Per company | TSK-01 | None | R-61 | BL/TimesheetService.cs; BL/TaskBillingService.cs |
| TSK-03 | TasksAccessService | Per company | TSK-01 | None | R-62 | BL/TaskGeneratorHostedService.cs |
| TSK-04 | TasksAccessService | Per company | TSK-03 | None | R-63 | BL/TaskScheduleMatchHostedService.cs |
| TSK-05 | None | N/A | TSK-01,CAL-01,COM-01 | D-25 | R-64 | No source |
| WFL-01 | Partial | Per company | PLT-01 | None | R-93 | BL/InventoryApprovalService.cs; Controllers/ApprovalsController.cs |
| WFL-02 | None | N/A | WFL-01 | None | R-94 | No source |
| WSP-01 | Adds NO permissions - every read delegates to the owning module access service | Per company via delegated services | PLT-05,COM-08,TSK-01,REP-09 | D-41 approved | R-109 | No source - D-41 |

## 5. Catalog — full field set

The complete 22-field record for every capability is in the CSV; it is too wide to render
legibly here. `Description`, `ImplementationLevel`, `APIStatus`, `DatabaseStatus`,
`ReportingStatus`, `CommunicationStatus`, `MobileStatus` and `AIReadiness` appear there.
