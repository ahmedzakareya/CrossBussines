# 13 — Communication and Notifications

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


## Is there one notification authority?

**Yes, for in-app notifications.** Exactly one interface and one implementation:

```
CrossBuy/BL/INotificationService.cs
CrossBuy/BL/NotificationService.cs
```

But it is reached two different ways, and that is the real finding:

| Route | Callers | Nature |
|---|---|---|
| **Direct injection** | ~18 services — `CrmService`, `CrmAutomationService`, `CrmReminderHostedService`, `AppraisalService`, `AnnouncementService`, `ChatService`, `DocCommentService`, `EmployeeRequestService`, `HrDocumentService`, `IntegrityCheckService`, `InventoryApprovalService`, `LeaveWorkflowService`, `PayableService`, `ProcurementService`, `ReceivableService`, … | synchronous, in-transaction, no event |
| **Event projection** | `Platform/NotificationProjectionConsumer.cs` | asynchronous, via the outbox |

So the *authority* is single, but the *path* is not: most modules notify by calling the service
directly and never produce an event, while the outbox has a consumer that produces notifications from
events. A notification therefore does not imply an event, and an event does not imply a notification.

`Notification` is a company-filtered pilot entity, with its own policy at
`BL/Platform/NotificationCompanyPolicy.cs` (nullable `CompanyID` for unattributed rows).

## Communication platform

| Piece | File |
|---|---|
| Threads / comments | `Models/Context/Communication/CommThreadEntities.cs`, `CommCommentEntities.cs` |
| Comment service | `BL/Communication/CommCommentService.cs` |
| Mentions | `BL/Communication/CommMentionService.cs` |
| Events | `BL/Communication/CommEventPublisher.cs` |
| Audit | `BL/Communication/CommAuditWriter.cs`, `CommAuditEntry.cs` |
| Body policy | `BL/Communication/CommBodyPolicy.cs` |
| Principals | `BL/Communication/CommPrincipalResolver.cs` |
| **Delivery outbox** | `BL/Communication/CommNotificationDispatcher.cs` |

### The comm outbox is built but unwired

`CommNotificationDispatcher` implements a proper claim-by-status drain. Its header states the rule
verbatim from ADR-003/ADR-007:

> *"CLAIM BY STATUS ONLY. NEVER a cursor, NEVER MAX(Id), NEVER 'Id > lastSeen'."*

and is honest about its limits: the claim is EF-based, relies on a unique `(NotificationId, Channel)`
index plus a stale-claim clock, and *"is correct for a single"* instance. `ICommDeliveryClaimStore`
exists as the seam for a provider-specific implementation.

**No hosted service drives it.** A search of `Program.cs` and the hosted services for
`CommNotificationDispatcher` returns nothing, and its own comment describes the drain as "a CALLABLE
method with the worker-facing shape already correct" pending "production wiring". So the queue exists,
the claim semantics are tested, and nothing empties it in production. Recorded as **F-06**.

`CommThread` is keyed `(CompanyID, EntityType, EntityId, Kind, ThreadKey)`, so **any registered entity
can carry a thread with no schema change** — a cheap integration point that CRM, Projects and Tasks do
not currently use.

`CommThreadEntities.cs:8` records that none of these tables is in the `CompanyQueryFilters` pilot, so
**no global query filter** applies — the predicate is the isolation.

## Channels

| Channel | Evidence |
|---|---|
| In-app notifications | `NotificationService` + SignalR |
| Chat | `BL/ChatService.cs`, `Controllers/ChatController.cs`, 1 view |
| Comments / mentions | `CommCommentService`, `CommMentionService` |
| Email | `IReportMailSender` is registered as `NullReportMailSender` (`ReportingRegistration.cs:95`) — reporting email is a no-op |
| WhatsApp | **not present** |

## F-1

**F1-UNRESOLVED.** No artefact in the repository identifies what "F-1" designates. Not guessed.
