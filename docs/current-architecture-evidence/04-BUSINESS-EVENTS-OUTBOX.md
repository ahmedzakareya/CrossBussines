# 04 — Business Events and the Outbox

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


## Architecture

| Piece | File |
|---|---|
| Event row | `Models/Context/Platform/BusinessEvent.cs:11` |
| Per-consumer dispatch row | `Models/Context/Platform/BusinessEvent.cs:60` — `BusinessEventDispatch` |
| Dispatch status | `Models/Platform/BusinessEventContracts.cs:73` |
| Write path | `BL/Platform/BusinessEventService.cs` |
| Worker | `BL/Platform/BusinessEventDispatchWorker.cs` |
| Consumer contract | `BL/Platform/IBusinessEventConsumer.cs` |
| Monitoring | `BL/Platform/BusinessEventMonitorService.cs` + `Controllers/BusinessEventMonitorController.cs` |

This is a **real transactional outbox with per-consumer dispatch state** — not a fire-and-forget bus.
A dispatch row exists per (event, consumer), so one consumer failing does not lose the event for
another, and retry is per consumer.

`BusinessEvent` is one of the 12 pilot entities carrying a global company query filter
(`CompanyQueryFilters.cs:151`).

## Flow

```
business transaction
      │  (same DbContext, same SaveChanges)
      ▼
BusinessEvent row committed  ─── atomic with the business write
      │
      ▼
BusinessEventDispatch row per registered consumer
      │
      ▼
BusinessEventDispatchWorker  (background, company-scoped)
      │
      ├──► TimelineProjectionConsumer      → timeline entries
      ├──► NotificationProjectionConsumer  → notifications
      ├──► AiProjectionConsumer            → AI projections (out of scope)
      └──► TaskOrchestrationConsumer       → tasks   ** phase3/4 ONLY **
```

## Registered consumers

| Consumer | Registered at | On master? |
|---|---|---|
| `TimelineProjectionConsumer` | `Program.cs:666` | yes |
| `NotificationProjectionConsumer` | `Program.cs:667` | yes |
| `Ai.AiProjectionConsumer` | `Program.cs:669` | yes |
| `Orchestration.TaskOrchestrationConsumer` | `Program.cs:685` | **NO — phase3/4 only** |

`Program.cs:662` notes that `BusinessEventConsumers.Registered` must stay in step with registration,
and `:678` records that adding a consumer starts creating a dispatch row per **new** event only —
existing rows are not backfilled. **Replay of historical events is therefore not automatic.**

`CommEventPublisher` and `CommNotificationDispatcher` reference the consumer contract but are not in
the four registrations above.

## Declared event families — `BL/Platform/BusinessEventTypes.cs`

| Family | Types | Line |
|---|---|---|
| SalesInvoice | Created, Updated, Cancelled | 84 |
| Customer | Created, Updated | 100 |
| PurchaseInvoice | Created, Updated, Cancelled | 117 |
| JournalEntry | Reversed | 149 |
| ManufWorkOrder | Created, Updated, Released, Produced, Completed, Cancelled | 154 |
| Task | Created, Assigned, Reassigned, StatusChanged, DueDateChanged, BecameOverdue, Completed, Reopened, **Cancelled** | 178 |
| CalendarEvent | Created, Updated, Rescheduled, Cancelled, AttendeeAdded, AttendeeRemoved, ReminderTriggered, Started, Completed | 193 |
| Document | (see `BL/Documents/DocumentEvents.cs`) | — |

**8 families, 34 declared types.**

## Producers — files that actually raise events

`AttendanceService`, `Communication/CommCommentService`, `Communication/CommEventPublisher`,
`Communication/CommMentionService`, `Documents/DocumentEvents`, `Documents/DocumentExpiryProjection`,
`Documents/PlatformDocumentService`, `IntegrityCheckService`, `Platform/BusinessContextFactory`,
`Platform/BusinessEventMonitorService`, `Platform/BusinessEventService`, `Platform/PlatformGrantWriter`,
`PosPreparationService`, `ProgressBillingService`, `Reporting/ReportEngine`,
`Reporting/ReportHistoryService`, `TasksCalendar/TaskCalendarEventPublisher`, plus AI files (out of scope).

**Modules that declare event types but where no producer was found in this pack's sweep:** the
Accounting families (SalesInvoice / PurchaseInvoice / Customer / JournalEntry) and ManufWorkOrder
appear as string constants and in contract registries; a producing call site was not located in
`BL/*Service.cs` for them. This is recorded as *not proven*, not as *absent* — see finding **F-07**.

## Bypasses

Cross-module work that does **not** go through the outbox:

- `INotificationService` is injected directly by ~18 services across CRM, HR, Inventory, Accounting,
  Chat, Documents and Announcements. That is a direct call, not an event.
- The generator, document-expiry and orchestration task-creation doors write `TaskItems` directly and
  raise no `Task.*` event (see file 06).
