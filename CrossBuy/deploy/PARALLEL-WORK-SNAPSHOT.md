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
