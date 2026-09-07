# 11 — CRM and Projects

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


## CRM

20 tables, 74 controller actions, 34 views, 1,183-line service. Substantially built.

| Area | Evidence |
|---|---|
| Entities | `Models/Context/Crm/Crm.cs` — Lead, Opportunity, CrmAccount, CrmContact, Activity, Campaign, CampaignMember, CrmMarketingList, CrmListMember, CrmPipeline, CrmPipelineStage, OpportunityProduct, CrmTicket, CrmSlaPolicy, CrmScoringRule, CrmSettings, CrmUserRole |
| Service | `BL/CrmService.cs` |
| Access | `BL/CrmAccessService.cs` — implements `IModuleAccessService`, consumes `IBootstrapAccessPolicyReader` |
| Automation | `BL/CrmAutomationService.cs` |
| Reminders | `BL/CrmReminderHostedService.cs` |
| Datasets | `BL/Reporting/CrmDatasets.cs` — Leads and Opportunities only |

### Concrete gaps (evidence-backed only)

- **No CRM business events.** No `Crm.*`, `Lead.*` or `Opportunity.*` family in `BusinessEventTypes.cs`.
  CRM is therefore invisible to the outbox and cannot drive orchestration.
- **No CRM → Task path.** `CrmService` does not inject `ITaskService`. The only bridge is
  `Controllers/InsightActionsController.cs` (AI Insights — out of scope per brief), which does.
- **No CRM entity codes in `EntityRegistry`.** Module `"Crm"` is registered; `Lead`, `Opportunity`,
  `CrmAccount` are not, so `TaskLinkResolver` cannot resolve a CRM back-link.
- **Notifications are direct calls,** not events — `CrmService`, `CrmAutomationService` and
  `CrmReminderHostedService` all inject `INotificationService`.
- Report datasets cover 2 of ~20 entities.

## Projects

| Area | Evidence |
|---|---|
| Controllers | `ProjectController.cs`, `ProjectCloseoutController.cs` |
| Views | 17 — Projects, Dashboard, Team, Budget, Progress, Billing, Labor, MaterialIssues, Subcontracts, VariationOrders, Boq, Advance, Profitability, RetentionRelease, SubRetentionRelease, EquipmentDepreciation, ActivityTypes |
| Services | `BL/ProgressBillingService.cs` (raises events), plus BoQ / subcontract / variation services owned by TAB-4 |
| Access | `BL/ProjectsAccessService.cs` |

### Does Projects have a competing task concept?

**No.** A search for `ProjectTask`, `ProjectActivity`, `ProjectMilestone`, `ProjectPhase` entities
returns exactly one hit — `Models/Context/Accounting/Dimensions.cs:49` `ProjectActivityType`, which is
an **accounting dimension lookup**, not a work item. There is no project-level task table, no
project-task screen, and no project-task service.

**Consequence:** Projects is the module most safely available for Task rollout — there is nothing to
reconcile or migrate. Its work today is expressed as progress, billing and BoQ lines, none of which
is modelled as assignable work.

### Gaps

- No project Business Events beyond `ProgressBillingService`.
- No Calendar integration (`AgendaItemType` has no Project member).
- No Projects entity code in `EntityRegistry` beyond the module-level `"Project"`.
- `ProjectMembers` write path — reported by TAB-6 as absent; not independently verified in this pack.
