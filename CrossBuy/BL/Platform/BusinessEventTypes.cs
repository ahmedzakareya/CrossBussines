using System.Text.RegularExpressions;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel (PKS-001 "Event naming rules") — the ONE place that decides whether an event type
    // string is legal.
    //
    // Canonical form: "<EntityCode>.<Action>" where EntityCode is a registered code and Action is
    // PascalCase. Rejected forms — and the reason each is rejected — are the three naming styles that had
    // already appeared across the codebase before the kernel:
    //     "InvoiceCreated"          -> no entity code, no separator
    //     "sales_invoice_created"   -> snake_case (the NotificationTypes style)
    //     "SalesInvoiceCreated"     -> entity code and action fused, unparseable
    public static class BusinessEventTypes
    {
        public const int MaxEventTypeLength = 80;

        // PascalCase action: an initial capital then letters/digits only. No separators, no underscores.
        private static readonly Regex ActionPattern = new(@"^[A-Z][A-Za-z0-9]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string Build(string entityCode, string action) => entityCode + "." + action;

        public static bool TryValidate(string? eventType, string entityCode, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(eventType)) { error = "Event type is required."; return false; }
            if (eventType.Length > MaxEventTypeLength)
            { error = $"Event type '{eventType}' exceeds {MaxEventTypeLength} characters."; return false; }

            int dot = eventType.IndexOf('.');
            if (dot <= 0 || dot == eventType.Length - 1)
            {
                error = $"Event type '{eventType}' must be '<EntityCode>.<Action>' — e.g. '{entityCode}.Created'.";
                return false;
            }

            var prefix = eventType.Substring(0, dot);
            var action = eventType.Substring(dot + 1);

            if (!string.Equals(prefix, entityCode, StringComparison.Ordinal))
            {
                error = $"Event type '{eventType}' is prefixed '{prefix}' but was recorded against entity '{entityCode}'.";
                return false;
            }
            if (action.Contains('.'))
            {
                error = $"Event type '{eventType}' has more than one '.' separator.";
                return false;
            }
            if (!ActionPattern.IsMatch(action))
            {
                error = $"Event action '{action}' must be PascalCase letters/digits — '{eventType}' is not canonical.";
                return false;
            }
            return true;
        }

        public static void Validate(string? eventType, string entityCode)
        {
            if (!TryValidate(eventType, entityCode, out var error))
                throw new BusinessEventContractException(error!);
        }

        // The action half of a canonical event type, used by the timeline presenter.
        public static string ActionOf(string eventType)
        {
            int dot = eventType.IndexOf('.');
            return dot >= 0 && dot < eventType.Length - 1 ? eventType.Substring(dot + 1) : eventType;
        }
    }

    // Canonical Sales Invoice event types — the pilot entity.
    //
    // Only Created and Updated have producers, because those are the only state transitions the domain
    // actually has: ReceivableService.CreateSalesInvoiceAsync writes the invoice already Status="Posted"
    // (there is no separate approval step anywhere in the codebase, so there is no "Approved" transition
    // to record — naming one would put a fact in the audit log that never happened), and
    // EditSalesInvoiceAsync reverses and re-posts in place.
    //
    // Cancelled is DECLARED but has no producer: SalesInvoice.Status documents 'Cancelled' as a legal
    // value, yet no code path sets it today. Declaring it now freezes the name for whichever slice adds
    // the cancel action; nothing emits it in this slice.
    public static class SalesInvoiceEvents
    {
        public const string Created = "SalesInvoice.Created";
        public const string Updated = "SalesInvoice.Updated";
        public const string Cancelled = "SalesInvoice.Cancelled";
    }

    // ---------------------------------------------------------------------------------------------
    // Slice 2 — Customer.
    //
    // ReceivableService has exactly two write paths: CreateCustomerAsync (the quick-add used by the sales
    // document screens) and SaveCustomerAsync (an UPSERT used by the Customers screen). There is no
    // dedicated activate/deactivate operation anywhere — IsActive is just a checkbox on the same edit form —
    // so Customer.StatusChanged is NOT declared. An IsActive flip is reported inside Customer.Updated via
    // changedFields + oldStatus/newStatus, which is what actually happened.
    // ---------------------------------------------------------------------------------------------
    public static class CustomerEvents
    {
        public const string Created = "Customer.Created";
        public const string Updated = "Customer.Updated";
    }

    // ---------------------------------------------------------------------------------------------
    // Slice 2 — Purchase Invoice.
    //
    // Identical lifecycle shape to the sales invoice: CreatePurchaseInvoiceAsync writes the invoice already
    // Status="Posted" (there is no draft-then-post step and no approval anywhere in PayableService), and
    // EditPurchaseInvoiceAsync reverses and re-posts in place.
    //
    // Cancelled is DECLARED for registry/vocabulary consistency but has NO producer: PurchaseInvoice.Status
    // documents 'Cancelled' as a legal value, yet nothing in the codebase sets it. Posted and Approved are
    // NOT declared at all — creation IS the posting, and no approval transition exists to name.
    // ---------------------------------------------------------------------------------------------
    public static class PurchaseInvoiceEvents
    {
        public const string Created = "PurchaseInvoice.Created";
        public const string Updated = "PurchaseInvoice.Updated";
        public const string Cancelled = "PurchaseInvoice.Cancelled";
    }

    // ---------------------------------------------------------------------------------------------
    // Slice 2 — Manufacturing Work Order. The only pilot entity with a real multi-state lifecycle.
    //
    // ManufWorkOrder.Status is documented and enforced as Draft | Released | Completed | Cancelled:
    //   Created    ManufService.CreateAsync                       -> Draft
    //   Updated    ManufService.SaveHeaderAsync                   -> Draft only (rejects post-release edits)
    //   Released   SetStatusAsync("Released") | ReleaseAsync      -> Draft -> Released
    //   Completed  CompleteAsync | ProducePartialAsync(finalize)  -> Released -> Completed
    //   Cancelled  SetStatusAsync("Cancelled") | CancelAsync      -> not-Completed -> Cancelled
    //
    // Started is NOT declared: there is no "Started" status and no operation that would produce one.
    // Progress without finalising is reported as ManufWorkOrder.Produced, which is a real partial-production
    // operation (StockService.ProducePartialAsync) rather than a status transition.
    // ---------------------------------------------------------------------------------------------
    // ---------------------------------------------------------------------------------------------
    // Slice 3 (Stage 0) — Journal entry.
    //
    // ONE event, one real transition. JournalEntryService.ReverseAsync is the only operation that changes a
    // posted entry: it creates a mirror entry (JournalType="Reversing", SourceType="Reversal") and flips the
    // original to Status="Reversed" with ReversedByEntryId set. Nothing else mutates a posted entry.
    //
    // Created/Posted are deliberately NOT declared: CreateAndPostAsync writes the entry already posted, and
    // every posting already leaves the entry row itself as the durable record. Reversal is the transition that
    // previously left no trace anywhere.
    // ---------------------------------------------------------------------------------------------
    public static class JournalEntryEvents
    {
        public const string Reversed = "JournalEntry.Reversed";
    }

    // ---------------------------------------------------------------------------------------------
    // Sales / Purchase Return — the credit note and the debit note.
    //
    // NO PRODUCER EXISTS. EntityRegistry's own note says the panel shows "no activity yet" until
    // ReceivableService/PayableService publish return events, and they never did. These names are consumed
    // TODAY only by the read-time legacy adapters, which reconstruct the history from the return row and its
    // journal entries — so the vocabulary is declared in one place rather than as literals in two adapters.
    //
    // A FUTURE PRODUCER MUST ALSO ADD A PRESENTER CASE in TimelineEventPresenter: a recorded event whose
    // type has no presentation fails the STRICT dispatch path silently, which is exactly the defect the
    // Tasks/Calendar block below documents. Reconstructed items carry their own titles and never reach it.
    //
    // Created IS the posting for both documents — neither service has a draft-then-post step for a return.
    // Reversed names the one transition that changes a posted entry.
    // ---------------------------------------------------------------------------------------------
    public static class SalesReturnEvents
    {
        public const string Created = "SalesReturn.Created";
        public const string Updated = "SalesReturn.Updated";
        public const string Reversed = "SalesReturn.Reversed";
    }

    public static class PurchaseReturnEvents
    {
        public const string Created = "PurchaseReturn.Created";
        public const string Updated = "PurchaseReturn.Updated";
        public const string Reversed = "PurchaseReturn.Reversed";
    }

    // ---------------------------------------------------------------------------------------------
    // Receipt / Payment — the money documents.
    //
    // NO PRODUCER, same as the returns: consumed today only by the read-time adapters. Allocated is the
    // one name here that is not a mirror of the document lifecycle — it is the fact these documents exist
    // for. Cash arriving is bookkeeping; cash SETTLING a named invoice is the business event, and it was
    // previously visible only as a figure inside a settlements table.
    // ---------------------------------------------------------------------------------------------
    public static class ReceiptEvents
    {
        public const string Created = "Receipt.Created";
        public const string Allocated = "Receipt.Allocated";
        public const string Updated = "Receipt.Updated";
        public const string Reversed = "Receipt.Reversed";
    }

    public static class PaymentEvents
    {
        public const string Created = "Payment.Created";
        public const string Allocated = "Payment.Allocated";
        public const string Updated = "Payment.Updated";
        public const string Reversed = "Payment.Reversed";
    }

    public static class ManufWorkOrderEvents
    {
        public const string Created = "ManufWorkOrder.Created";
        public const string Updated = "ManufWorkOrder.Updated";
        public const string Released = "ManufWorkOrder.Released";
        public const string Produced = "ManufWorkOrder.Produced";
        public const string Completed = "ManufWorkOrder.Completed";
        public const string Cancelled = "ManufWorkOrder.Cancelled";
    }

    // ---------------------------------------------------------------------------------------------
    // Tasks & Calendar.
    //
    // These names are NOT new. They are the vocabulary TaskCalendarIntegrationContracts already declares and
    // TaskCalendarEventPublisher already writes — as string literals in both places, which is why the strict
    // timeline presenter had nothing to switch on and answered "no timeline presentation registered" for
    // every one of them. Naming them here gives the presenter (and any future consumer) the same compile-time
    // vocabulary the SalesInvoice and JournalEntry families have always had, so a typo is a build error
    // rather than a dispatch row that fails five times in production.
    //
    // The full DECLARED set is listed, not only the subset with a producer today: an event type that is
    // registered but unlisted is precisely the gap this closes, and it would reopen the moment the next
    // producer was wired.
    // ---------------------------------------------------------------------------------------------
    public static class TaskEvents
    {
        public const string Created = "Task.Created";
        public const string Assigned = "Task.Assigned";
        public const string Reassigned = "Task.Reassigned";
        public const string StatusChanged = "Task.StatusChanged";
        public const string DueDateChanged = "Task.DueDateChanged";
        public const string BecameOverdue = "Task.BecameOverdue";
        public const string Completed = "Task.Completed";
        public const string Reopened = "Task.Reopened";
        public const string Cancelled = "Task.Cancelled";
    }

    // Named CalendarEventEvents because the ENTITY is "CalendarEvent": the doubled word is the entity plus the
    // family suffix every other class here uses, not a typo.
    public static class CalendarEventEvents
    {
        public const string Created = "CalendarEvent.Created";
        public const string Updated = "CalendarEvent.Updated";
        public const string Rescheduled = "CalendarEvent.Rescheduled";
        public const string Cancelled = "CalendarEvent.Cancelled";
        public const string AttendeeAdded = "CalendarEvent.AttendeeAdded";
        public const string AttendeeRemoved = "CalendarEvent.AttendeeRemoved";
        public const string ReminderTriggered = "CalendarEvent.ReminderTriggered";
        public const string Started = "CalendarEvent.Started";
        public const string Completed = "CalendarEvent.Completed";
    }
}