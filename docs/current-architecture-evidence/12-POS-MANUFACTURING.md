# 12 — POS and Manufacturing

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


## POS

| Area | Evidence |
|---|---|
| Controllers | `PosController.cs`, `PosAppController.cs`, `HyperController.cs`, `HyperPosController.cs`, `RestaurantIntelligenceController.cs` |
| Services | `BL/PosSetupService.cs`, `BL/PosPreparationService.cs`, `BL/PosAccessService.cs` |
| Views | POS 16 + Hyper 9 |
| Events | `PosPreparationService.cs` raises business events |
| Access | `PosAccessService` is registered as `IModuleAccessService` (`Program.cs:305`) |

### Company handling — a documented design decision, not a defect

Four POS entry points hold a company literal, and one explains itself:

```
PosSetupService.cs:805   private const int PosCompanyId = 1;
                         // cash accounts / catalog live under company 1 (branches sit under 65–79)
PosAccessService.cs:44   public const int CatalogCompanyId = 1;
HyperController.cs:17    private const int PosCompanyId = 1;
PosAppController.cs:21   private const int PosCompanyId = 1;
HyperPosController.cs:25 private const int PosCompanyId = 1;
```

This is a **catalog-tenancy model**: the POS catalog and cash accounts are deliberately single-company
while branches are separate companies (65–79). Classified **DESIGN-INTENTIONAL** in file 15, on the
strength of the `PosSetupService.cs:805` comment. It is still a constraint a Phase-4 rollout must know
about: POS work items would be created against company 1 regardless of the branch that generated them.

## Manufacturing

Manufacturing has **no controller and no view folder of its own** — it lives inside Inventory
(`Views/Inventory/ManufReports.cshtml`, and manufacturing screens under the Inventory surface).

| Area | Evidence |
|---|---|
| Events | `ManufWorkOrderEvents` — Created, Updated, Released, Produced, Completed, Cancelled (`BusinessEventTypes.cs:154`) |
| Entity code | `ManufWorkOrder` is in `EntityRegistry` and resolvable by `TaskLinkResolver` |
| Services | manufacturing logic sits in `BL/StockService.cs` and related inventory services |

**`ManufWorkOrder` is the most event-complete family in the estate** — six lifecycle events declared,
and the entity is registered and link-resolvable. A producing call site was not located in this pack's
sweep of `BL/*Service.cs`; recorded as *not proven* under **F-07**.

## Cross-module coupling, actual

| From | To | Mechanism | Evidence |
|---|---|---|---|
| POS | Inventory | shared `CrossDbContext` + direct service call | one DbContext for the estate |
| POS | Accounting | cash accounts under company 1 | `PosSetupService.cs:805` |
| Manufacturing | Inventory | shared services (`StockService`) | same assembly, same context |
| Inventory (stock post) | Accounting | **direct call** to `AccountingPeriodStatuses.BlocksPosting` | `StockService.cs:200` |

None of these is event-mediated. All are compile-time couplings inside one assembly over one context.
