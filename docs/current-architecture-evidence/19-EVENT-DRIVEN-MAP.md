# 19 — Event-Driven Map

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


## Producer → Event → Consumer → Side effect

| Producer | Event | Consumers | Side effect |
|---|---|---|---|
| `TaskService.cs:345` | `Task.Created` | Timeline, Notification, AI | timeline entry, notification, AI projection |
| `TaskCalendarEventPublisher:93` | `Task.Assigned` | Timeline, Notification, AI | assignment notice |
| `:101` | `Task.Reassigned` | ↑ | |
| `:109` | `Task.StatusChanged` | ↑ | |
| `:117` | `Task.DueDateChanged` | ↑ | |
| `:126` | `Task.Completed` | ↑ | |
| `:135` | `Task.Reopened` | ↑ | |
| `:145` | `Task.BecameOverdue` | ↑ | |
| `:162` | `Task.Escalated` | ↑ | manager notification |
| — | **`Task.Cancelled`** | — | **declared, never raised** |
| `TaskCalendarEventPublisher:222` | `CalendarEvent.Cancelled` | Timeline, Notification, AI | |
| calendar publisher | `CalendarEvent.Created/Updated/Rescheduled/Started/Completed` | ↑ | |
| `Documents/DocumentEvents.cs`, `PlatformDocumentService.cs` | `Document.*` | Timeline, Notification, AI, **TaskOrchestration** | **→ creates a verification task** |
| `AttendanceService.cs` | attendance events | Timeline, Notification, AI | |
| `ProgressBillingService.cs` | billing events | ↑ | |
| `PosPreparationService.cs` | POS preparation events | ↑ | |
| `Communication/CommEventPublisher.cs` | comm events | ↑ | |
| `IntegrityCheckService.cs` | integrity findings | ↑ | |
| `Reporting/ReportEngine.cs`, `ReportHistoryService.cs` | report run events | ↑ | |
| `Platform/PlatformGrantWriter.cs` | grant events | ↑ | |
| *declared, producer not located* | `SalesInvoice.*`, `PurchaseInvoice.*`, `Customer.*`, `JournalEntry.Reversed`, `ManufWorkOrder.*` | — | see **F-07** |

## The backbone, honestly drawn

```
                     ┌──────────────────────────────────────────┐
                     │  PRODUCERS THAT ACTUALLY RAISE EVENTS    │
                     │  Tasks · Calendar · Documents · Comm     │
                     │  Attendance · ProgressBilling · POSprep  │
                     │  Integrity · Reporting · Grants          │
                     └────────────────────┬─────────────────────┘
                                          │ same transaction
                                          ▼
                                   BusinessEvent
                                          │
                                          ▼
                          BusinessEventDispatch (per consumer)
                                          │
                            BusinessEventDispatchWorker
                                          │
              ┌───────────────┬───────────┴───────────┬────────────────┐
              ▼               ▼                       ▼                ▼
        Timeline       Notification              AI projection   TaskOrchestration
        projection     projection                (out of scope)   ** phase3/4 **
                                                                        │
                                                                        ▼
                                                          DocumentVerificationRule
                                                                        │
                                                                        ▼
                                                                   TaskItem
```

## What is NOT on the backbone

| Not event-driven | Why it matters |
|---|---|
| Accounting invoice/journal lifecycle | families declared, producer not proven — the ledger cannot drive work |
| CRM entirely | no `Crm.*` family exists at all |
| Approvals entirely | no `Approval.*` family exists at all |
| HR leave / requests | notify directly, raise nothing |
| Tasks created by generator, document-expiry, orchestration | write the row, raise nothing |
| Comm delivery | `CommNotificationDispatcher` has no worker |

**The backbone is real but narrow.** Four consumers, one of them unmerged, and one orchestration rule.
The modules with the most business activity — Accounting, CRM, Inventory — contribute least to it.
