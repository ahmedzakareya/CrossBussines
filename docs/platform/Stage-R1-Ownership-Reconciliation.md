# Stage R1 — Ownership Reconciliation

**Eight tabs registered. 392 changed production files swept; 280 now claimed, 112 residual long-tail.**

Machine-readable: `governance/registry/tab-ownership.json`, `governance/registry/shared-files.json`.
Enforced by: `governance/tools/check-file-ownership.ps1`.

---

## 1. Method

Assignment is by **architectural ownership**, not filename prefix. Two examples where prefix would have been wrong:

* `CrossBuy/BL/Comm/**` looks like Communication Platform. It is the **legacy Comm module** — active, with a registered dispatcher. Both go to TAB-3 because TAB-3 owns the migration between them, but they are recorded as distinct systems, never merged in status.
* `CrossBuy/BL/Platform/**` contains both the parallel Platform Kernel *and* TAB-1's `Bootstrap*` / `PlatformGrant*` files. The kernel belongs to the Integration Owner; the security files stay with TAB-1. The registry expresses both, and the tool resolves the requesting tab's own globs first, so the more specific claim wins for its owner.

## 2. Tab register

| Tab | Scope | Status |
|---|---|---|
| **TAB-0** Integration Owner | Shared files, Platform Kernel, SQL roots, governance, CI, ADRs, hubs | **TO BE APPOINTED** |
| **TAB-1** Core Security | Analyzer, bootstrap policy, grants, access services, roadmap docs | Active |
| **TAB-2** Reporting | Reporting engine, Report Center API and views, reporting slice, ADR-037 | Active |
| **TAB-3** Communication | Comm platform, legacy Comm module, comm models and slices, ADR-030–036 | Active |
| **TAB-4** Construction | BOQ, subcontracts, commercial revisions, construction slices | Active |
| **TAB-5** Tasks and Calendar | Task state and rules, calendar events, `BL/TasksCalendar/**`, task/calendar views | **UNASSIGNED** — 2 live hosted services |
| **TAB-6** CrossBusiness Workspace | Workspace shell, controller, views | **UNASSIGNED** — R3 code exists in the tree |
| **TAB-7** Legacy Business Modules | Accounting, Inventory, Purchasing, POS, Manufacturing, Projects, HR, CRM, Support | **UNASSIGNED** — carries most of the 143 debt |

**Three tabs are unassigned and two of them have live code.** TAB-5 runs `TaskGeneratorHostedService` and `TaskScheduleMatchHostedService` in production wiring. TAB-6 appeared in the tree during R1. Registering them as UNASSIGNED is deliberate: an unclaimed path is **invisible** to the ownership gate, which is exactly how unowned code lands. A named-but-unassigned tab is visible and blocks.

## 3. Exclusive paths

| Tab | Exclusive paths |
|---|---|
| TAB-1 | `CrossBuy.Analyzers/**`, `CrossBuy.Analyzers.Tests/**`, `CrossBuy/BL/Platform/Bootstrap*`, `CrossBuy/BL/Platform/PlatformGrant*`, `CrossBuy/BL/*AccessService.cs`, `CrossBuy/Models/Platform/Bootstrap*`, `CrossBuy/Models/Platform/PlatformGrant*`, `CrossBuy/Controllers/Api/PlatformGrantsApiController.cs`, `engineering/**`, `docs/roadmap-v2/**` |
| TAB-2 | `CrossBuy/BL/Reporting/**`, `CrossBuy/Models/Context/Reporting/**`, `CrossBuy/Controllers/Api/ReportsCenterApiController.cs`, `CrossBuy/Views/Reports*/**` |
| TAB-3 | `CrossBuy/BL/Communication/**`, `CrossBuy/BL/Comm/**`, `CrossBuy/Models/Context/Communication/**`, `CrossBuy/Models/Communication/**` |
| TAB-4 | `CrossBuy/BL/Construction/**`, `BoqService.cs`, `SubcontractBillingService.cs`, `VariationOrderService.cs`, `CrossBuy/Models/Context/Construction/**` |
| TAB-5 | `CrossBuy/BL/TasksCalendar/**`, `TaskService.cs`, `TasksAccessService.cs`, the two hosted services, `CalendarService.cs`, task/calendar controllers and views |
| TAB-6 | `CrossBuy/BL/Workspace/**`, `WorkspaceController.cs`, `CrossBuy/Views/Workspace/**` |
| TAB-0 | `CrossBuy/BL/Platform/**` (kernel), `CrossBuy/Models/Platform/**`, `CrossBuy/Hubs/**`, `governance/**`, `.github/workflows/**`, `docs/deployment/**`, `docs/platform/ADR-*` |

## 4. Shared paths

| File | Primary owner | Secondary | Integration rule | Prohibited |
|---|---|---|---|---|
| `CrossBuy/Program.cs` | TAB-0 | every tab appends | **ONE registration extension method per platform**, inside the tab's marked region. `AddCrossBusinessReporting` is the pattern to copy. | Reordering another tab's block; adding a 40-line inline block |
| `CrossBuy/Models/Context/CrossDbContext.cs` | TAB-0 | every tab adds DbSets | DbSets added in the tab's marked region only | **Whole-file rewrite, reorder or reformat** — 245 DbSets make this a certain conflict |
| `CrossBuy/CrossBuy.csproj` | TAB-0 | by request | Package/analyzer references by request only | Unilateral reference changes |
| `CrossBuy.sln` | TAB-0 | by request | Project additions by request | Unilateral edits — GUID conflicts are destructive |
| `CrossBuy/Resources/SharedResources*.resx` | TAB-0 | every tab | **Our keys only, via git plumbing** | **Whole-file `git add`** — the files get reordered and staging loses keys |
| `CrossBuy/Views/Shared/_Layout*.cshtml` | TAB-0 | every tab | Partial insertion by request; the partial must exist or every screen breaks | Removing another tab's partial |
| `CrossBuy/deploy/sql/**` | TAB-0 | slice authors | Claim a `SliceId` before authoring; regenerate the manifest | Hand-editing `manifest.json` |
| `CrossBuy/deploy/sql/manifest.json` | TAB-0 | — | Regenerate with `scan-sql-manifest.ps1` | **Any hand edit** — it desynchronises hashes from content |
| `CrossBuy/wwwroot/css/crossbuy-brand.css` | TAB-0 | — | **FROZEN** — changes only through the D-37 approved rollout | Global override flip; it would restyle 327 views with no design review |
| `docs/platform/ADR-*.md` | TAB-0 | authors | Reserve the number in the registry first | Reusing a number |
| `CrossBuy/BL/JournalEntryService.cs` | **Architectural invariant** | — | No change without owner pre-coordination | Any unilateral edit — it is the single GL writer |
| `CrossBuy/BL/StockService.cs` | **Architectural invariant** | — | No change without owner pre-coordination | Any unilateral edit — it is the single stock writer |

## 5. Communication contracts consumed by Workspace

TAB-6's `WorkspaceService` consumes `CommActorDto` and `CommMentionHistoryItemDto` from TAB-3. Those DTOs are **TAB-3's**, and the current build failures (`does not contain a definition for 'DisplayName'` / `'MentionedAt'`) are contract drift across that boundary.

**Rule:** a consumer never edits the producer's DTO. TAB-6 requests the shape from TAB-3; TAB-3 changes it in its own tree and merges first. This is exactly the cross-tab coupling the integration branch exists to sequence.

## 6. Sweep result

| Measure | Value |
|---|---|
| Changed production files swept | 392 |
| Claimed | **280** |
| Residual unclaimed | 112 |
| Largest unclaimed cluster | 3 files |

Residual is a long tail of individual views and build artefacts (`obj/`), none exceeding three files per directory. Reported rather than force-claimed: assigning them by directory guesswork would put a name against code nobody has actually agreed to own, which is the failure this register exists to remove.

## 7. Enforcement proved

| Case | Result |
|---|---|
| TAB-1 touching `BL/Reporting/ReportEngine.cs` | **DENIED**, attributed to TAB-2 |
| TAB-2 touching its own `ReportEngine.cs` | PASS |
| TAB-1 touching `BL/Workspace/WorkspaceService.cs` | **DENIED**, attributed to TAB-6 |
| TAB-1 touching shared `Program.cs` | **FAIL** without `-AllowShared`, citing SHF-01 and its rule |
| TAB-1 touching its own files | PASS |

## 8. Defect fixed during reconciliation

Ownership globs were **not repo-relative** — `BL/*AccessService.cs` can never match `CrossBuy/BL/AccountingAccessService.cs`. Every genuinely-owned file was falling through as unclaimed, so the gate would have passed almost anything. All eight tabs were normalised, and annotated globs (`governance/** (R1 tools)`) are now stripped at generation, because a rule that matches nothing is a rule that is not enforced.
