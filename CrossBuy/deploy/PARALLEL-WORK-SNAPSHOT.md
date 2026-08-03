# Parallel-Work Snapshot — 2026-08-03

A read-only, timestamped photograph of another team's **uncommitted** work sharing this repo, taken before HM-5.
Nothing here was modified. Our last commit at snapshot time: **`43c1637`** (docs: HM-D47/48/49). Their work is entirely
in the working tree (nothing of theirs is committed yet) — so everything we built sits on a base that can shift
invisibly until they commit. See `deploy/AUDIT-DEVIATIONS.md` HM-D44/46 and CLAUDE.md "Shared Platform Rules".

The parallel effort = **Platform Kernel** (enterprise event platform) + feature modules **Comm/Mail · Announcements ·
Calendar · Library · FileManager · DocComments · Chat**, plus a **CrossBuy.Tests** project and a full `docs/` set
(9 platform ADRs + PKS-001 + Slice-002 + 22 architecture docs).

---

## 1. Tracked files they modified (in OUR build paths) — flagged

| File | +add / −del | mtime | In a path we build on? |
|---|---|---|---|
| `BL/ReceivableService.cs` | +185 / −9 | 12:01 | **YES** — kernel `RecordAsync` ×5 (SalesInvoice.Created:404, Customer.*:157/212/244) inside `ScopedTx` before commit |
| `BL/PayableService.cs` | +83 / −9 | 12:02 | **YES** — kernel `RecordAsync` ×2 |
| `BL/ManufService.cs` | +185 / −9 | 12:03 | **YES** — kernel `RecordAsync` ×1 |
| `BL/NotificationService.cs` | +7 / −1 | 11:57 | **YES** — additive: 2 optional params (entityType/entityId). `NotifyAsync` NOT removed |
| `BL/INotificationService.cs` | +5 / −1 | 11:57 | **YES** — same additive signature |
| `BL/NotificationTypes.cs` | +18 / −0 | 11:59 | notification catalog additions |
| `Models/Context/Admin/Notification.cs` | +9 / −0 | 11:57 | +EntityType/EntityId columns |
| `Models/Context/CrossDbContext.cs` | +50 (theirs) | 13:09* | **YES (shared)** — kernel entity mappings + Comm/Calendar/Library/Platform DbSets. *mtime shows my +1 DbSet line too |
| `Program.cs` | +41 (theirs) | 13:14* | **YES (shared)** — kernel DI (17 lines) + `AddHostedService<BusinessEventDispatchWorker>`. *mtime shows my +1 DI line too |

**Our critical writers are UNTOUCHED by them:** `StockService`, `JournalEntryService`, `PosOrderService`,
`PricingService`, `IntegrityCheckService` — no parallel edits. The kernel sits in the **document layer above** our
two writers (JournalEntryService=GL, StockService=stock), which stay ours alone.

### Tracked files they modified (outside our core paths)
`CrossBuy.sln` (+26, test project) · `BL/ChatService.cs` · `BL/TaskLinkResolver.cs` (+39/−88) · `Controllers/ChatController.cs`
· `Hubs/NotificationsHub.cs` (+9) · `Models/Menu/MainMenu.cs` (+4) · `Views/Chat/Index.cshtml` · `Views/Notifications/Index.cshtml`
· `Resources/SharedResources.en.resx` (a NEW explicit -en variant now tracked) + the 3 shared resx (their keys interleave ours).

⚠️ **Runtime coupling into our screens:** `Views/Shared/_Layout{Accounting,Backend,Inventory,Manufacturing}.cshtml`
(+9 each) now `@Html.PartialAsync("_AnnouncementsBanner")`, `"_CalendarReminders")`, and a Comm email button. Our
Inventory/Accounting/Manufacturing pages therefore **depend on those partials existing at render time** — if the
parallel partials are absent from a deploy, our screens throw. The partials are untracked (see §2).
Also timeline includes were added to `Views/Accounting/{CustomerStatement,PurchaseInvoiceDetail,SalesInvoiceDetail,VendorStatement}.cshtml`
and `Views/Inventory/{QuotationDetails,WorkOrderDetails}.cshtml`.

---

## 2. Untracked (new) parallel files — by module

- **Platform Kernel** — `BL/Platform/` (21 files: BusinessEventService, DispatchWorker, EntityRegistry, SqlEventDispatchStore, TimelineProjection*, NotificationProjectionConsumer, BusinessEventNotificationMapper, PlatformPermissionProvider, 4 LegacyTimelineAdapters, …) · `Models/Platform/` · `Models/Context/Platform/BusinessEvent.cs` · `Controllers/PlatformTimelineController.cs` · `Views/Shared/_DocTimeline.cshtml`, `_DocEventTimeline.cshtml` · `docs/platform/` (ADR-001…007, PKS-001, Slice-002) · `docs/architecture/` (22 docs + `evidence/*.csv`).
- **Comm / Mail** — `BL/CommService.cs` · `Controllers/CommController.cs` · `Models/Context/Comm/` · `Views/Comm/` · `Resources/Views/Comm/` · `wwwroot/uploads/comm/`.
- **Announcements** — `BL/AnnouncementService.cs` · `Controllers/AnnouncementsController.cs` · `Views/Announcements/` · `Views/Shared/_AnnouncementsBanner.cshtml` · `Resources/Views/Announcements/`.
- **Calendar** — `BL/CalendarService.cs` · `Controllers/CalendarController.cs` · `Models/Context/Calendar/` · `Views/Calendar/` · `Views/Shared/_CalendarReminders.cshtml` · `Resources/Views/Calendar/`.
- **Library** — `Models/Context/Library/` · `wwwroot/uploads/library/`.
- **FileManager** — `BL/FileManagerService.cs` · `Controllers/FileManagerController.cs` · `Views/FileManager/` · `Resources/Views/FileManager/`.
- **DocComments** — `BL/DocCommentService.cs` · `Controllers/CommentsController.cs`.
- **Tests** — `CrossBuy.Tests/`.
- **wwwroot plugins** — `Backend-assets/plugins/custom/mammoth/`, `.../sheetjs/`.

*(Mine, mixed into the same untracked list — NOT parallel: `BL/ShelfLabelService.cs`, `Models/Context/Inventory/PriceChangeLog.cs`, `Views/Hyper/PriceCheck.cshtml`, `Views/Inventory/BulkPriceChange.cshtml`, `Views/Inventory/ShelfLabels.cshtml`, `deploy/sql/hm4_pricing.sql`.)*

---

## 3. Unpublished SQL scripts (in `deploy/sql`, two locations)

There are **two** deploy/sql folders: repo-root `deploy/sql/` (older + some parallel) and `CrossBuy/deploy/sql/`
(our HM scripts + some parallel). Idempotent, hand-written — **no EF migrations** (the `CrossBuy/Migrations` folder is
the old dead snapshot, tracked, untouched).

| Script | Owner | Deployed to CrossBuyDB2? | Note |
|---|---|---|---|
| `CrossBuy/deploy/sql/platform_business_events.sql` | parallel (slice 1) | **YES — we applied it** | required by the synchronous sale/purchase/manuf path (HM-D44) |
| `CrossBuy/deploy/sql/platform_business_events_slice_002.sql` | parallel (slice 2) | no | **index-only on Notifications — no table, no 208 risk** |
| `CrossBuy/deploy/sql/calendar.sql` | parallel | no | Calendar module tables — 208 only if Calendar endpoints run |
| `CrossBuy/deploy/sql/library.sql` | parallel | no | Library module tables |
| `deploy/sql/announcements_p5.sql` | parallel | no | Announcements module |
| `deploy/sql/comm_mail.sql` | parallel | no | Comm/Mail module |
| `CrossBuy/deploy/sql/hm4_pricing.sql` | **ours** | **YES — we applied it** | `PriceChangeLogs` |

**Next-208 assessment:** after slice-1 (applied), **no synchronous path we build on can 208 on kernel tables**.
The other unpublished scripts back parallel modules whose endpoints we don't call.

---

## 4. Load-bearing facts (proven, for later reference)

- Kernel `RecordAsync` is **inside** the caller's `ScopedTx`, **before** `CommitAsync`, no swallowing try/catch →
  a kernel failure rolls the whole business transaction back (no partial effect; HM-D44 proved it empirically).
- Kernel writes **only** `BusinessEvents` / `BusinessEventDispatch` / `Notifications`; it **reads** financial tables
  `AsNoTracking` and writes **none**. No financial writer outside our two.
- The dispatch worker is a `BackgroundService` that takes `IServiceScopeFactory` and creates a **scope per batch**
  (no request-scoped-context leak).
- Notifications for onboarded facts are now derived from events via `NotificationProjectionConsumer` (post-commit,
  in the worker); the legacy inline `NotifyRoleAsync` for the sale was deleted to avoid double-notifying.
</content>

---

## Update — 2026-08-03 (post-HM-5), material changes since the snapshot above

Two developments reclassify the risk from "new modules alongside us" to "they are editing our code and can break our build":

1. **They now edit an OWNED writer.** `BL/JournalEntryService.cs` (our GL writer) was modified (mtime 14:01:51): constructor gained `IBusinessEventService _events`, and `ReverseAsync` now calls `RecordAsync(JournalEntry.Reversed)` — in-transaction, before commit, no try/catch, **Visibility = Confidential** (their first non-Internal event). The **ledger posting is unchanged** (reversal entry + status flip identical; the event only writes `BusinessEvents`), so GL numbers are ledger-neutral — but `ReverseAsync` now **hard-depends on the kernel** (`EntityRegistry.JournalEntry`, `JournalEntryEvents.Reversed`, `JournalEntryEventPayload`, the `BusinessEvents` table). **This corrects §1's "our critical writers are UNTOUCHED":** `JournalEntryService` (GL) is now kernel-wired; `StockService` (stock) remains clean. See AUDIT HM-D53.

2. **Their WIP broke the shared build.** A rebuild after HM-5 acceptance failed with 2 errors, both in their files: `BL/Platform/IEventDispatchStore.cs:45` (`CS0246: 'BusinessContext' not found`) and `BL/Platform/SqlEventDispatchStore.cs:24` (`CS0535: does not implement RetryAsync`) — a live half-refactor. HM-D44 realised: the base moved under us. See AUDIT HM-D52.

**Verified this snapshot cycle — our other owned files carry ONLY our changes (no parallel bleed):** `PricingService`, `PosOrderService`, `IntegrityCheckService`, `HyperPosController`, `ShelfLabelService`, `ScaleBarcodeParser`, `StockService`. Only `JournalEntryService` (among ours) was touched by them.

**Owner action (raise with them):** (a) commit their work so our base stops shifting; (b) coordinate before editing our writers; (c) fix the two Platform build errors.

---

## Update — 2026-08-03 (pre-HM-16), before we modify PayableService

About to touch **`PayableService.cs`** (our purchase-document writer) for HM-16. Snapshot of the parallel state at this moment:
- `PayableService.cs` (mtime 12:02) is **kernel-wired** — 2× `RecordAsync` (purchase-invoice / return events), in-tx before commit, no swallow. We build our HM-16 change ON TOP of that; our selective commit uses the pre-kernel HEAD version, so their wiring stays uncommitted and ours is isolated.
- `JournalEntryService.cs` still kernel-wired (1× RecordAsync in ReverseAsync — HM-D53). `ReceivableService`/`ManufService` kernel-wired.
- Their earlier build break (IEventDispatchStore/SqlEventDispatchStore RetryAsync) is **fixed** — the tree compiles again. `Program.cs` re-saved 15:44 (they are still actively editing).
- Coordination rule (CLAUDE.md) applies: editing `PayableService` (a document writer) is coordinated; we touch only the purchase-invoice posting, not their kernel lines.

---

## Update — 2026-08-03 (pre-HM-7 batch-1: batch-aware count), before we modify StockService

About to touch **`StockService.cs`** (our stock writer) for HM-7 batch-1 (PostCountAsync + CountLineInput). Precondition
re-checked per the standing rule:
- **writer_coupling on the stock writer is CLEAN.** StockService constructor (line 137) = the six allowed deps only
  {CrossDbContext, IJournalEntryService, IFiscalPeriodService, ICurrencyService, ILogger, ICurrencyRounding}; grep for
  `_events`/`IBusinessEventService`/`RecordAsync`/`IStringLocalizer` = 0; no uncommitted diff vs HEAD. The integrity
  `writer_coupling` actual=1 is the `JournalEntryService:IBusinessEventService` coupling (HM-D53), NOT StockService.
- **Stock-adjacent parallel WIP still active:** `InventoryApprovalService.cs` (mtime 18:57 — used by the count/transfer/
  write-off approval gates in InventoryController) and `ManufService.cs` are being edited by the parallel team. We do NOT
  touch them; our count change is inside StockService + the count DTO/entity/screen only. If a NEW coupling appears on the
  stock writer during the batch, we STOP and report.

---

## Update — 2026-08-03 (pre-HM-7 batch-2: HM-D7 landed-cost lost-update fix), before we modify StockService again

- **writer_coupling on the stock writer is CLEAN** (StockService ctor = six allowed deps; zero kernel wiring). Pre-step OK.
- **NEW: `IntegrityCheckService.cs` is now a PARALLEL-SHARED file.** The parallel team's Stage-1-B5 edit company-scopes the
  `barcode_cross_table_dup` raw SQL (uses `{0}` params + a "Stage 1 Batch B / B5" comment) in the WORKING TREE. It was
  never in our commits (`git log -S` empty); a housekeeping commit accidentally staged it and was amended out. **Going
  forward, commit IntegrityCheckService via HEAD-base plumbing (our lines only) like SharedResources/CrossDbContext.**
- Parallel infra added today: `CompanyScopeMiddleware` + `CompanyQueryFilters` global query filter (HM-D59) — deliberately
  EXCLUDES StockBalance (names our UPDLOCK guard). Does not touch the stock writer; only breaks single-shot curl (cookie jar).
