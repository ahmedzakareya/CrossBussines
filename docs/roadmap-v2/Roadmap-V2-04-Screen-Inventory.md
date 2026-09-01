# CrossBusiness Platform — Roadmap v2 — 04 Screen Inventory

**What a user can actually open today, and what is only planned.**

Existing screen counts are measured from `CrossBuy/Views/<folder>/*.cshtml` on disk
(327 `.cshtml` files in total, including partials and shared layouts).

Machine-readable: `roadmap-v2-screen-inventory.csv`.

---

## 1. Explicit statements required of this inventory

| Statement | Status |
|---|---|
| Business Event Monitor | **DELIVERED** — 3 views, the one delivered platform operational screen |
| Reporting user interfaces | **NOT DELIVERED** — no Views/Reporting folder exists |
| Communication user interfaces | **NOT DELIVERED** — the 3 Views/Comm files are the *legacy* Comm module, not the Communication Platform |
| Construction C1 | **NO FINAL UI** — C1 delivered services and DDL only |
| Security Console | **NOT STARTED** |
| Report Studio | **NOT STARTED** |
| Task/Calendar integrated workspace | **NOT STARTED** |

## 2. Distribution

| Category | Screens |
|---|---|
| Planned | 21 |
| Existing | 12 |
| ExistingWeak | 8 |
| Delivered | 1 |

`ExistingWeak` means the screen opens but does not carry the capability its module implies.

## 3. Inventory

| ScreenId | Category | OwnerPlatformOrModule | Screen | Status | Purpose | Evidence |
|---|---|---|---|---|---|---|
| SCR-01 | Existing | Accounting | Accounting workspace (66 views) | Existing | Ledger, invoicing, statements, assets, closing. | Views/Accounting (66) |
| SCR-02 | Existing | Inventory and WMS | Inventory workspace (78 views) | Existing | Items, stock, warehouses, counts, pricing, manufacturing, procurement. | Views/Inventory (78) |
| SCR-03 | Existing | CRM | CRM workspace (32 views) | Existing | Accounts, leads, opportunities, custom fields. | Views/Crm (32) |
| SCR-04 | Existing | HR | Admin and People workspaces (43 views) | Existing | Employees, attendance, leave, appraisals, recruitment, training. | Views/Admin (31) + Views/People (12) |
| SCR-05 | Existing | POS | POS and PosApp screens (21 views) | Existing | Order capture, payment, KDS, delivery, shift close. | Views/Pos (16) + Views/PosApp (5) |
| SCR-06 | Existing | POS | Hypermarket lane (9 views) | Existing | Hyper selling lane, loyalty, receipts. | Views/Hyper (9) |
| SCR-07 | Existing | Projects | Project workspace (16 views) | Existing | Projects, progress, billing, material, labour. | Views/Project (16) |
| SCR-08 | Existing | Task Management | Tasks screens (5 views) | ExistingWeak | Task list and detail; not an integrated work surface. | Views/Tasks (5) |
| SCR-09 | Existing | Calendar | Calendar screen (1 view) | ExistingWeak | Single view; no workspace, no cross-module scheduling. | Views/Calendar (1) |
| SCR-10 | Existing | Support and Customer Service | Service screens (5 views) | ExistingWeak | Ticket capture and listing; no SLA, no customer visibility. | Views/Service (5) |
| SCR-11 | Existing | Notifications | Notifications screen (1 view) | ExistingWeak | Basic notification list. | Views/Notifications (1) |
| SCR-12 | Existing | Files and Document Management | File manager (1 view) | ExistingWeak | Flat file browse; no versioning or control. | Views/FileManager (1) |
| SCR-13 | Existing | Workflow and Approvals | Approvals inbox (1 view) | ExistingWeak | Single inbox view; not a general workflow surface. | Views/Approvals (1) |
| SCR-14 | Existing | Customer Portal | Portal and Store (3 views) | ExistingWeak | Public catalog and portal entry. | Views/Portal (1) + Views/Store (2) |
| SCR-15 | Existing | Communication and Collaboration | Legacy Comm screens (3 views) | ExistingWeak | Legacy Comm module UI - NOT the Communication Platform UI. | Views/Comm (3) |
| SCR-16 | PlatformAdministration | Platform Framework | Business Event Monitor (3 views) | Delivered | Inspect business events and dispatch state. The one delivered platform operational screen. | Views/BusinessEventMonitor (3) |
| SCR-17 | Existing | Platform Framework | Platform timeline | Existing | Timeline view over onboarded entities. | Controllers/PlatformTimelineController.cs |
| SCR-18 | Existing | Identity and Access | Account screens (5 views) | Existing | Login, profile, password. | Views/Account (5) |
| SCR-19 | Existing | Accounting | Currency screens (4 views) | Existing | Currency master and revaluation. | Views/Currency (4) |
| SCR-20 | Existing | Platform Framework | Announcements and Brand (3 views) | Existing | Announcements banner and brand configuration. | Views/Announcements (1) + Views/Brand (2) |
| SCR-21 | Mobile | Mobile and Field Operations | Flutter app (10 screens) | Existing | Mobile client shipping separately from the web host. | crossbuy_mobile/lib/screens (10) |
| SCR-30 | Planned | CrossBusiness Workspace | Workspace shell and navigation | Planned | The single front door. Owns no business data; orchestrates approved service contracts. | No source |
| SCR-31 | PlatformAdministration | Core Security | Security Console | Planned | Roles, grants, bootstrap policies, authorization debt burn-down. | No source |
| SCR-32 | PlatformAdministration | Master Data | Master Data Console | Planned | Governed master entities and ownership. | No source |
| SCR-33 | Planned | Reporting Platform | Report Center | Planned | Browse, run, export, schedule and share reports. First dataset: Business Events (D-35). | No source |
| SCR-34 | Planned | Reporting Platform | Report Studio | Planned | Author and edit report definitions and datasets. | No source |
| SCR-35 | Planned | Communication and Collaboration | Threads and comments surface | Planned | Universal comment/thread panel embedded in entity screens. | No source |
| SCR-36 | Planned | Communication and Collaboration | Mentions and notifications centre | Planned | Unified inbox for mentions and notifications; embedded in the workspace. | No source |
| SCR-37 | Planned | Task Management | Unified Task and Calendar views | Planned | Integrated task and calendar presentation. NOT a new module - integration of existing systems. | No source |
| SCR-38 | Planned | Construction and Contracting | Construction workspace | Planned | BOQ, subcontracts, certificates, variations, progress. | No source |
| SCR-39 | Planned | Construction and Contracting | Site operations and DSR | Planned | Daily site report, RFI, document control. | No source |
| SCR-40 | Planned | CRM | CRM and Support workspace | Planned | Unified customer view across CRM and Support. | No source |
| SCR-41 | Planned | Customer Portal | Customer and partner portal | Planned | External self-service portal. | No source |
| SCR-42 | Planned | Search | Search surface | Planned | Cross-module search honouring per-module authorization at query time. | No source |
| SCR-43 | Planned | Platform Framework | Module dashboards | Planned | Role-based operational dashboards inside module workspaces. | Existing dashboard services only |
| SCR-44 | Planned | CrossBusiness Workspace | My Work | Planned | Unified personal work list drawn from Tasks, approvals and mentions. | No source |
| SCR-45 | Planned | CrossBusiness Workspace | Recent Activity and Timeline | Planned | Cross-module activity from the Communication timeline and business events. | No source |
| SCR-46 | Planned | CrossBusiness Workspace | Approvals inbox | Planned | Consolidated approvals across modules; replaces the standalone SCR-13. | No source |
| SCR-47 | Planned | CrossBusiness Workspace | Saved, recent and favourite reports | Planned | Report shortcuts surfaced in the workspace; execution stays in Report Center. | No source |
| SCR-48 | Planned | CrossBusiness Workspace | Quick actions | Planned | Cross-module launch points honouring each module's own permission checks. | No source |
| SCR-49 | Planned | CrossBusiness Workspace | Personal dashboard | Planned | Per-user metrics from approved datasets only. | No source |
| SCR-50 | Planned | CrossBusiness Workspace | Team dashboard | Planned | Team-scoped metrics; scope resolved by module services, never by the workspace. | No source |

## 4. Planned screens — build contract

Every planned screen carries prerequisites, permissions, data sources, workflow, reporting,
communication and mobile relevance:

| ScreenId | Screen | Prerequisites | Permissions | DataSources | Workflow | Reporting | Communication | MobileRelevance |
|---|---|---|---|---|---|---|---|---|
| SCR-30 | Workspace shell and navigation | R1, R2 | Session plus every module's own access service - Workspace adds NO permissions | Cross-module read contracts | None of its own | Embedded | Embedded | High |
| SCR-31 | Security Console | SEC-02, SEC-05 | PlatformOps | Platform tables | Grant and revoke | Debt report | None | Low |
| SCR-32 | Master Data Console | MDM-01 | PlatformOps | Master entities | Stewardship | Planned | None | Low |
| SCR-33 | Report Center | REP-07 | ReportPermissions | Report catalog | Run and schedule | Core | Delivery | Medium |
| SCR-34 | Report Studio | REP-07, SCR-33 | ReportPermissions.Administer | Dataset registry | Authoring | Core | None | Low |
| SCR-35 | Threads and comments surface | COM-08 | CommAccessPolicy | Comm tables | Thread lifecycle | None | Core | High |
| SCR-36 | Mentions and notifications centre | COM-08, NOT-01 | Session | Comm plus Notifications | None | None | Core | High |
| SCR-37 | Unified Task and Calendar views | TSK-01, CAL-01, SCR-30 | TasksAccessService | Task plus Calendar | Task lifecycle | Planned | Mentions | High |
| SCR-38 | Construction workspace | CON-04 | Project-scoped | Construction tables | Certification | Core | Planned | High - site |
| SCR-39 | Site operations and DSR | CON-05 | Project-scoped | Construction tables | Site workflow | Core | Core | Core - offline |
| SCR-40 | CRM and Support workspace | CRM-04, SUP-02 | CrmAccessService post-conversion | Crm plus tickets | Case lifecycle | Planned | Core | High |
| SCR-41 | Customer and partner portal | PRT-01, D-29 | ExternalPrincipalContext - NOT an employee context | Scoped subset | Self-service | Planned | CustomerVisibleReply only | Core |
| SCR-42 | Search surface | SRC-01 | Per-module authorization at query time | Search index | None | None | None | High |
| SCR-43 | Module dashboards | REP-07 | Per-module | Datasets | None | Core | None | Medium |
| SCR-44 | My Work | SCR-30, TSK-01 | Delegated to TasksAccessService and module services | Tasks, approvals, mentions | Read-only aggregation | Saved views | Mentions | High |
| SCR-45 | Recent Activity and Timeline | SCR-30, COM-04, PLT-05 | Delegated per entity | Timeline projection, BusinessEvents | Read-only | None | Core - Communication owns rendering contract | High |
| SCR-46 | Approvals inbox | SCR-30, WFL-01 | Delegated to each module's approval authority | Approval tables | Approve and reject via module services | Planned | Notification target | High |
| SCR-47 | Saved, recent and favourite reports | SCR-30, REP-09 | ReportPermissions | Report catalog, favourites | Run via Report Center | Core | None | Medium |
| SCR-48 | Quick actions | SCR-30 | Delegated - a hidden control is not a control | Module services | Module workflows | None | None | High |
| SCR-49 | Personal dashboard | SCR-30, REP-07 | Dataset-level authorization | Reporting datasets | None | Core | None | Medium |
| SCR-50 | Team dashboard | SCR-49, OrgHierarchy | Delegated scope resolution | Reporting datasets | None | Core | None | Medium |

## 5. Permanent UI rule

**Do not invent the final design.** Before implementing any screen, request the approved
Metronic/theme reference from the owner and follow **CrossBusiness Blue**. Every planned
screen above carries `REQUEST owner Metronic reference before build` in its
`DesignReference` field; that is a gate, not a note.

Existing specialized operational screens (POS, KDS, hypermarket lane, mobile attendance,
storefront) keep their domain UX and are not to be restyled into the admin shell.
