# Stage-Construction-12 — Business Events (proposed)

> **Generated file — do not edit by hand.**
> Source of truth: `docs/construction/_generator/generate_construction_catalogs.py`.
> Regenerate with `python docs/construction/_generator/generate_construction_catalogs.py`.
> The paired CSV in this folder is emitted from the same dataset in the same run.

**Nothing in this document is raised in this increment.** These are *proposals* that follow the platform kernel's
mandatory contract, so that adopting them later is a wiring change and not a redesign.

## Standing constraint

The hyper track's standing decision — do not raise platform business events until the kernel work is committed in
git — applies here too, and for the same reason: `IBusinessEventService.RecordAsync` must be called inside the
caller's ambient transaction immediately before commit **with no swallowing catch**, so raising an event makes the
kernel a hard runtime dependency of the raising path. A construction certificate that cannot post because the event
table changed is a worse outcome than a certificate with no timeline entry.

Therefore: **Construction defines events and payloads (this document). It does not raise them, and it does not write
consumers** — consumers listed below are *requests* to the third tab, not work items for this tab.

## Contract every proposed event already obeys

- Name is `<EntityCode>.<Action>`, action PascalCase, and the entity code must first be registered in
  `BL/Platform/EntityRegistry.cs` (today the only construction-adjacent code is `Project`, and it is registered with
  `SupportsTimeline = false, SupportsComments = false, SupportsFiles = false` at line 219 — i.e. **not onboarded**).
- Every event carries CompanyID and ProjectId, because no construction fact is meaningful without both.
- `SensitiveFields` names what must be withheld from a general timeline: contract values, rates and margins are
  commercial data, and a timeline is read by more people than a certificate is.
- A `DedupKey` is set on every event whose producing action can be retried (certificate submit/approve/post).
- The action must be a real transition — no `Updated` events.

## Proposed catalogue

| EventName | Version | Aggregate | EntityId | Company | Project | Actor | Payload | SensitiveFields | ProposedConsumers |
|---|---|---|---|---|---|---|---|---|---|
| ConstructionProject.Created | v1 | ConstructionProject | ProjectId | required | required | creator | code, name, type, site, contract currency | contract value | Timeline, Notification |
| WbsNode.Created | v1 | WbsNode | WbsNodeId | required | required | creator | code, name, parent, path, method | - | Timeline |
| Boq.Approved | v1 | BoqRevision | BoqRevisionId | required | required | approver | revision no, kind, source, line count, total value | total value, rates | Timeline, Notification, Cost control |
| ProjectBudget.Approved | v1 | ProjectBudget | BudgetId | required | required | approver | version, total by cost code | amounts | Timeline, Notification |
| ClientContract.Approved | v1 | ClientContract | ContractId | required | required | approver | contract no, customer, currency, dates | value, terms | Timeline, Notification |
| Subcontract.Approved | v1 | Subcontract | SubcontractId | required | required | approver | vendor, scope summary, dates | value, rates | Timeline, Notification |
| ProgressCertificate.Submitted | v1 | Certificate | CertificateId | required | required | submitter | certificate no, period, cumulative qty | values | Timeline, Notification, Approval inbox |
| ProgressCertificate.Approved | v1 | Certificate | CertificateId | required | required | approver | certificate no, net payable | values, deductions | Timeline, Notification |
| ProgressCertificate.Posted | v1 | Certificate | CertificateId | required | required | poster | invoice id, receipt ids | values | Timeline, Notification, Cash flow |
| Variation.Raised | v1 | Variation | VariationId | required | required | originator | vo no, reason, scope summary | cost impact | Timeline, Notification |
| Variation.Approved | v1 | Variation | VariationId | required | required | approver | vo no, boq revision id, time impact days | cost impact | Timeline, Notification, Cost control |
| Claim.Raised | v1 | Claim | ClaimId | required | required | originator | claim type, cause, responsible party | entitlement value | Timeline, Notification |
| Delay.Recorded | v1 | DelayEvent | DelayEventId | required | required | recorder | date, cause, affected wbs, classification | cost impact | Timeline, Notification |
| MaterialRequest.Raised | v1 | MaterialRequest | MaterialRequestId | required | required | requester | site, wbs, items, quantities | - | Timeline, Notification |
| MaterialRequest.DeliveredToSite | v1 | MaterialRequest | MaterialRequestId | required | required | storekeeper | site, warehouse, quantities, movement ids | unit cost | Timeline, Cost control |
| DailySiteReport.Submitted | v1 | DailySiteReport | DailySiteReportId | required | required | submitter | date, site, manpower total, equipment total, quantities | cost rates | Timeline, Notification |
| Rfi.Raised | v1 | Rfi | RfiId | required | required | originator | rfi no, discipline, due date | - | Timeline, Notification |
| Inspection.Approved | v1 | InspectionRequest | InspectionRequestId | required | required | inspector | inspection no, wbs, result | - | Timeline, Notification |
| DrawingRevision.Issued | v1 | DocumentRevision | DocumentRevisionId | required | required | document controller | document no, revision, discipline, supersedes | - | Timeline, Notification, Site warning |
| ConstructionProject.HandoverCompleted | v1 | ConstructionProject | ProjectId | required | required | project manager | handover date, as-built set, DLP end | retention balance | Timeline, Notification, Retention |

## Onboarding prerequisites (owned elsewhere)

1. Register the construction entity codes in `EntityRegistry` (kernel change — coordinate).
2. Turn on `SupportsTimeline`/`SupportsComments`/`SupportsFiles` per entity, and set a real `PermissionScope`
   (today `Project` is `ScopeNone`).
3. Only then add `RecordAsync` calls at the transitions above, inside the existing transaction.
