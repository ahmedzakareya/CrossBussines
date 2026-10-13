using System.Text.Json;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel — turns a stored event into the bilingual text a timeline row shows.
    //
    // Two entry points on purpose:
    //   TryPresent  — STRICT. The TimelineProjection consumer uses it so an event that cannot be rendered
    //                 (unregistered event type, payload version from the future) fails its dispatch row and
    //                 becomes visible to an operator, instead of silently reaching a user's screen blank.
    //   Present     — LENIENT. The read path uses it so an unrecognised event still renders something
    //                 truthful (the canonical action name) rather than breaking a document screen.
    //
    // Bilingual literals are inline AR/EN pairs, matching how the notification producers across the BL
    // already pass titles/bodies, rather than adding a resx key per event type.
    public static class TimelineEventPresenter
    {
        private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

        public sealed class Presentation
        {
            public required string TitleAr { get; init; }
            public required string TitleEn { get; init; }
            public string? DescriptionAr { get; init; }
            public string? DescriptionEn { get; init; }
            public required string Icon { get; init; }
            public required string Color { get; init; }
        }

        public static bool TryPresent(string eventType, int payloadVersion, string? payloadJson, out Presentation? presentation, out string? error)
        {
            presentation = null;
            error = null;

            switch (eventType)
            {
                case SalesInvoiceEvents.Created:
                case SalesInvoiceEvents.Updated:
                case SalesInvoiceEvents.Cancelled:
                    if (!TryPayload<SalesInvoiceEventPayload>(eventType, payloadVersion, SalesInvoiceEventPayload.Version, payloadJson, out var payload, out error))
                        return false;
                    presentation = PresentSalesInvoice(eventType, payload);
                    return true;

                // ---- slice 2 ----
                case CustomerEvents.Created:
                case CustomerEvents.Updated:
                    if (!TryPayload<CustomerEventPayload>(eventType, payloadVersion, CustomerEventPayload.Version, payloadJson, out var customer, out error))
                        return false;
                    presentation = PresentCustomer(eventType, customer);
                    return true;

                case PurchaseInvoiceEvents.Created:
                case PurchaseInvoiceEvents.Updated:
                case PurchaseInvoiceEvents.Cancelled:
                    if (!TryPayload<PurchaseInvoiceEventPayload>(eventType, payloadVersion, PurchaseInvoiceEventPayload.Version, payloadJson, out var purchase, out error))
                        return false;
                    presentation = PresentPurchaseInvoice(eventType, purchase);
                    return true;

                case ManufWorkOrderEvents.Created:
                case ManufWorkOrderEvents.Updated:
                case ManufWorkOrderEvents.Released:
                case ManufWorkOrderEvents.Produced:
                case ManufWorkOrderEvents.Completed:
                case ManufWorkOrderEvents.Cancelled:
                    if (!TryPayload<ManufWorkOrderEventPayload>(eventType, payloadVersion, ManufWorkOrderEventPayload.Version, payloadJson, out var workOrder, out error))
                        return false;
                    presentation = PresentManufWorkOrder(eventType, workOrder);
                    return true;

                // ---- slice 3 (Stage 0) ----
                case JournalEntryEvents.Reversed:
                    if (!TryPayload<JournalEntryEventPayload>(eventType, payloadVersion, JournalEntryEventPayload.Version, payloadJson, out var journal, out error))
                        return false;
                    presentation = PresentJournalEntry(eventType, journal);
                    return true;

                // These registered financial families have no versioned display-field
                // contract yet. Show the declared event fact without exposing raw payloads.
                case SalesReturnEvents.Created:
                case SalesReturnEvents.Updated:
                case SalesReturnEvents.Reversed:
                case PurchaseReturnEvents.Created:
                case PurchaseReturnEvents.Updated:
                case PurchaseReturnEvents.Reversed:
                case ReceiptEvents.Created:
                case ReceiptEvents.Allocated:
                case ReceiptEvents.Updated:
                case ReceiptEvents.Reversed:
                case PaymentEvents.Created:
                case PaymentEvents.Allocated:
                case PaymentEvents.Updated:
                case PaymentEvents.Reversed:
                    if (!TryPayload<System.Text.Json.Nodes.JsonObject>(eventType, payloadVersion, 1, payloadJson, out _, out error))
                        return false;
                    presentation = PresentFinancialFact(eventType);
                    return true;

                // ---- Tasks & Calendar ----
                //
                // EntityRegistry has marked Task and CalendarEvent SupportsTimeline = true since they were
                // onboarded, so TimelineProjectionConsumer took the STRICT path for them — and every one
                // failed here with "no timeline presentation registered", 512 dispatch rows stuck at
                // Attempts = 5. The read path masked it: TimelineProjectionService renders through the
                // lenient Present() below, which falls back to the canonical action name, so screens showed
                // something plausible while the durable projection was failing on a loop.
                //
                // The vocabulary is taken from TaskCalendarIntegrationContracts — the full declared set, not
                // only the subset a producer emits today. A contract that is registered but unlisted here is
                // exactly how this defect happened: the next producer to be wired would resume failing
                // silently. Nothing new is invented; every case below names a type that contract declares.
                case TaskEvents.Created:
                case TaskEvents.Assigned:
                case TaskEvents.Reassigned:
                case TaskEvents.StatusChanged:
                case TaskEvents.DueDateChanged:
                case TaskEvents.BecameOverdue:
                case TaskEvents.Completed:
                case TaskEvents.Reopened:
                case TaskEvents.Cancelled:
                case TaskEvents.HoldRequested:
                case TaskEvents.HoldApproved:
                case TaskEvents.HoldRejected:
                case TaskEvents.HoldResumed:
                    if (!TryPayload<TaskEventPayload>(eventType, payloadVersion, TaskEventPayload.Version, payloadJson, out var task, out error))
                        return false;
                    presentation = PresentTask(eventType, task);
                    return true;

                case CalendarEventEvents.Created:
                case CalendarEventEvents.Updated:
                case CalendarEventEvents.Rescheduled:
                case CalendarEventEvents.Cancelled:
                case CalendarEventEvents.AttendeeAdded:
                case CalendarEventEvents.AttendeeRemoved:
                case CalendarEventEvents.ReminderTriggered:
                case CalendarEventEvents.Started:
                case CalendarEventEvents.Completed:
                    if (!TryPayload<CalendarEventPayload>(eventType, payloadVersion, CalendarEventPayload.Version, payloadJson, out var calendar, out error))
                        return false;
                    presentation = PresentCalendarEvent(eventType, calendar);
                    return true;

                // ---- CRM Wave 1 --------------------------------------------------------------------
                //
                // FOURTEEN TYPES, FOUR PAYLOADS. The CRM producers declare five Lead facts, seven
                // Opportunity facts and two CrmAccount facts, and the follow-up pair on BOTH entities
                // carries the SAME CrmFollowUpEventPayload - so it is grouped by payload here rather
                // than by entity, which is what the switch is actually keyed on.
                //
                // THIS ARRIVED WITH THE CAPABILITY FLIP, NOT BEFORE IT AND NOT AFTER. The registry
                // held Lead, Opportunity and CrmAccount at SupportsTimeline = false precisely because
                // these arms did not exist: TimelineProjectionConsumer is strict, so a flag without a
                // presenter turns every CRM event into a dispatch row that fails on every retry -
                // which is the 512-row defect the Tasks and Calendar note above records. The two
                // halves are one change.
                case LeadEvents.Created:
                case LeadEvents.Updated:
                case LeadEvents.Converted:
                    if (!TryPayload<LeadEventPayload>(eventType, payloadVersion, LeadEventPayload.Version, payloadJson, out var lead, out error))
                        return false;
                    presentation = PresentLead(eventType, lead);
                    return true;

                case OpportunityEvents.Created:
                case OpportunityEvents.Updated:
                case OpportunityEvents.StageChanged:
                case OpportunityEvents.Won:
                case OpportunityEvents.Lost:
                    if (!TryPayload<OpportunityEventPayload>(eventType, payloadVersion, OpportunityEventPayload.Version, payloadJson, out var opportunity, out error))
                        return false;
                    presentation = PresentOpportunity(eventType, opportunity);
                    return true;

                case CrmAccountEvents.Created:
                case CrmAccountEvents.Updated:
                    if (!TryPayload<CrmAccountEventPayload>(eventType, payloadVersion, CrmAccountEventPayload.Version, payloadJson, out var account, out error))
                        return false;
                    presentation = PresentCrmAccount(eventType, account);
                    return true;

                // The follow-up pair is declared on BOTH Lead and Opportunity and shares one payload.
                case LeadEvents.FollowUpScheduled:
                case LeadEvents.FollowUpCompleted:
                case OpportunityEvents.FollowUpScheduled:
                case OpportunityEvents.FollowUpCompleted:
                    if (!TryPayload<CrmFollowUpEventPayload>(eventType, payloadVersion, CrmFollowUpEventPayload.Version, payloadJson, out var followUp, out error))
                        return false;
                    presentation = PresentCrmFollowUp(eventType, followUp);
                    return true;

                default:
                    error = $"Event type '{eventType}' has no timeline presentation registered.";
                    return false;
            }
        }

        // Shared strict payload gate: a version from the future, or a body that does not match the version it
        // declares, is an error the consumer must surface rather than something to render around.
        private static bool TryPayload<T>(
            string eventType, int payloadVersion, int knownVersion, string? payloadJson, out T? payload, out string? error)
            where T : class
        {
            payload = null; error = null;
            if (payloadVersion > knownVersion)
            {
                error = $"Event '{eventType}' has payload version {payloadVersion} but this build understands up to " +
                        $"{knownVersion}. The projection needs upgrading before it can render it.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(payloadJson)) return true;
            try { payload = JsonSerializer.Deserialize<T>(payloadJson, PayloadJson); }
            catch (JsonException ex)
            {
                error = $"Event '{eventType}' payload is not a v{payloadVersion} {typeof(T).Name}: {ex.Message}";
                return false;
            }
            return true;
        }

        public static Presentation Present(string eventType, int payloadVersion, string? payloadJson)
        {
            if (TryPresent(eventType, payloadVersion, payloadJson, out var presentation, out _))
                return presentation!;

            // Lenient fallback: show the canonical action so the row is still honest about what happened.
            var action = BusinessEventTypes.ActionOf(eventType);
            return new Presentation
            {
                TitleAr = action, TitleEn = action,
                Icon = "ki-outline ki-information-2", Color = "secondary",
            };
        }

        private static Presentation PresentSalesInvoice(string eventType, SalesInvoiceEventPayload? p)
        {
            string? reference = p?.ReferenceNumber;
            string refAr = string.IsNullOrWhiteSpace(reference) ? "" : $" {reference}";
            string refEn = string.IsNullOrWhiteSpace(reference) ? "" : $" {reference}";

            switch (eventType)
            {
                case SalesInvoiceEvents.Created:
                    return new Presentation
                    {
                        TitleAr = "إصدار فاتورة المبيعات", TitleEn = "Sales invoice issued",
                        DescriptionAr = $"صدرت الفاتورة{refAr}" + Amount(" بإجمالي", p?.TotalAfter),
                        DescriptionEn = $"Invoice{refEn} was issued" + Amount(" totalling", p?.TotalAfter),
                        Icon = "ki-outline ki-bill", Color = "success",
                    };

                case SalesInvoiceEvents.Updated:
                    var changedAr = FieldList(p?.ChangedFields, isArabic: true);
                    var changedEn = FieldList(p?.ChangedFields, isArabic: false);
                    return new Presentation
                    {
                        TitleAr = "تعديل فاتورة المبيعات", TitleEn = "Sales invoice updated",
                        DescriptionAr = $"عُدّلت الفاتورة{refAr}{changedAr}{Delta(p, isArabic: true)}",
                        DescriptionEn = $"Invoice{refEn} was updated{changedEn}{Delta(p, isArabic: false)}",
                        Icon = "ki-outline ki-pencil", Color = "warning",
                    };

                case SalesInvoiceEvents.Cancelled:
                    return new Presentation
                    {
                        TitleAr = "إلغاء فاتورة المبيعات", TitleEn = "Sales invoice cancelled",
                        DescriptionAr = $"أُلغيت الفاتورة{refAr}",
                        DescriptionEn = $"Invoice{refEn} was cancelled",
                        Icon = "ki-outline ki-cross-circle", Color = "danger",
                    };

                default:
                    throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Not a sales-invoice event type.");
            }
        }

        // ---- slice 2 presentations ----

        private static Presentation PresentCustomer(string eventType, CustomerEventPayload? p)
        {
            string name = string.IsNullOrWhiteSpace(p?.CustomerName) ? "" : $" «{p!.CustomerName}»";

            if (eventType == CustomerEvents.Created)
                return new Presentation
                {
                    TitleAr = "إضافة عميل", TitleEn = "Customer added",
                    DescriptionAr = $"أُضيف العميل{name}" + (string.IsNullOrWhiteSpace(p?.CustomerType) ? "" : $" — التصنيف: {p!.CustomerType}"),
                    DescriptionEn = $"Customer{name} was added" + (string.IsNullOrWhiteSpace(p?.CustomerType) ? "" : $" — segment: {p!.CustomerType}"),
                    Icon = "ki-outline ki-profile-circle", Color = "success",
                };

            // Updated — an activation change is the one substantive state flip this entity has, so it is
            // called out instead of being buried in the field list.
            string statusAr = "", statusEn = "";
            if (p?.OldStatus != null && p.NewStatus != null && p.OldStatus != p.NewStatus)
            {
                statusAr = $" — الحالة {p.OldStatus} ← {p.NewStatus}";
                statusEn = $" — status {p.OldStatus} → {p.NewStatus}";
            }
            return new Presentation
            {
                TitleAr = "تعديل بيانات عميل", TitleEn = "Customer updated",
                DescriptionAr = $"عُدّل العميل{name}{FieldList(p?.ChangedFields, isArabic: true)}{statusAr}",
                DescriptionEn = $"Customer{name} was updated{FieldList(p?.ChangedFields, isArabic: false)}{statusEn}",
                Icon = "ki-outline ki-pencil", Color = "warning",
            };
        }

        private static Presentation PresentPurchaseInvoice(string eventType, PurchaseInvoiceEventPayload? p)
        {
            string reference = string.IsNullOrWhiteSpace(p?.InvoiceNumber) ? "" : $" {p!.InvoiceNumber}";
            string supplierAr = string.IsNullOrWhiteSpace(p?.SupplierName) ? "" : $" من المورد {p!.SupplierName}";
            string supplierEn = string.IsNullOrWhiteSpace(p?.SupplierName) ? "" : $" from {p!.SupplierName}";

            switch (eventType)
            {
                case PurchaseInvoiceEvents.Created:
                    return new Presentation
                    {
                        TitleAr = "تسجيل فاتورة مشتريات", TitleEn = "Purchase invoice recorded",
                        DescriptionAr = $"سُجّلت الفاتورة{reference}{supplierAr}" + Amount(" بإجمالي", p?.TotalAfter),
                        DescriptionEn = $"Invoice{reference}{supplierEn} was recorded" + Amount(" totalling", p?.TotalAfter),
                        Icon = "ki-outline ki-bill", Color = "success",
                    };

                case PurchaseInvoiceEvents.Updated:
                    return new Presentation
                    {
                        TitleAr = "تعديل فاتورة مشتريات", TitleEn = "Purchase invoice updated",
                        DescriptionAr = $"عُدّلت الفاتورة{reference}{FieldList(p?.ChangedFields, isArabic: true)}{TotalDelta(p?.TotalBefore, p?.TotalAfter, isArabic: true)}",
                        DescriptionEn = $"Invoice{reference} was updated{FieldList(p?.ChangedFields, isArabic: false)}{TotalDelta(p?.TotalBefore, p?.TotalAfter, isArabic: false)}",
                        Icon = "ki-outline ki-pencil", Color = "warning",
                    };

                case PurchaseInvoiceEvents.Cancelled:
                    return new Presentation
                    {
                        TitleAr = "إلغاء فاتورة مشتريات", TitleEn = "Purchase invoice cancelled",
                        DescriptionAr = $"أُلغيت الفاتورة{reference}",
                        DescriptionEn = $"Invoice{reference} was cancelled",
                        Icon = "ki-outline ki-cross-circle", Color = "danger",
                    };

                default:
                    throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Not a purchase-invoice event type.");
            }
        }

        private static Presentation PresentManufWorkOrder(string eventType, ManufWorkOrderEventPayload? p)
        {
            string reference = string.IsNullOrWhiteSpace(p?.WorkOrderNumber) ? "" : $" {p!.WorkOrderNumber}";
            string itemAr = string.IsNullOrWhiteSpace(p?.ItemName) ? "" : $" — الصنف {p!.ItemName}";
            string itemEn = string.IsNullOrWhiteSpace(p?.ItemName) ? "" : $" — item {p!.ItemName}";

            switch (eventType)
            {
                case ManufWorkOrderEvents.Created:
                    return new Presentation
                    {
                        TitleAr = "إنشاء أمر تشغيل", TitleEn = "Work order created",
                        DescriptionAr = $"أُنشئ أمر التشغيل{reference}{itemAr}" + Quantity(" بكمية مخطّطة", p?.PlannedQuantity),
                        DescriptionEn = $"Work order{reference} was created{itemEn}" + Quantity(" for a planned quantity of", p?.PlannedQuantity),
                        Icon = "ki-outline ki-gear", Color = "primary",
                    };

                case ManufWorkOrderEvents.Updated:
                    return new Presentation
                    {
                        TitleAr = "تعديل أمر تشغيل", TitleEn = "Work order updated",
                        DescriptionAr = $"عُدّل أمر التشغيل{reference}{FieldList(p?.ChangedFields, isArabic: true)}",
                        DescriptionEn = $"Work order{reference} was updated{FieldList(p?.ChangedFields, isArabic: false)}",
                        Icon = "ki-outline ki-pencil", Color = "warning",
                    };

                case ManufWorkOrderEvents.Released:
                    return new Presentation
                    {
                        TitleAr = "إصدار أمر تشغيل", TitleEn = "Work order released",
                        DescriptionAr = $"صدر أمر التشغيل{reference} للإنتاج{StatusDelta(p, isArabic: true)}",
                        DescriptionEn = $"Work order{reference} was released to production{StatusDelta(p, isArabic: false)}",
                        Icon = "ki-outline ki-rocket", Color = "info",
                    };

                case ManufWorkOrderEvents.Produced:
                    return new Presentation
                    {
                        TitleAr = "إنتاج جزئي", TitleEn = "Partial production",
                        DescriptionAr = $"أمر التشغيل{reference}: المنتج {p?.CompletedQuantityBefore:N2} ← {p?.CompletedQuantityAfter:N2}"
                                        + Quantity(" من", p?.PlannedQuantity),
                        DescriptionEn = $"Work order{reference}: produced {p?.CompletedQuantityBefore:N2} → {p?.CompletedQuantityAfter:N2}"
                                        + Quantity(" of", p?.PlannedQuantity),
                        Icon = "ki-outline ki-chart-line-up", Color = "info",
                    };

                case ManufWorkOrderEvents.Completed:
                    return new Presentation
                    {
                        TitleAr = "اكتمال أمر تشغيل", TitleEn = "Work order completed",
                        DescriptionAr = $"اكتمل أمر التشغيل{reference}" + Quantity(" بكمية منتجة", p?.CompletedQuantityAfter),
                        DescriptionEn = $"Work order{reference} completed" + Quantity(" with a produced quantity of", p?.CompletedQuantityAfter),
                        Icon = "ki-outline ki-check-circle", Color = "success",
                    };

                case ManufWorkOrderEvents.Cancelled:
                    return new Presentation
                    {
                        TitleAr = "إلغاء أمر تشغيل", TitleEn = "Work order cancelled",
                        DescriptionAr = $"أُلغي أمر التشغيل{reference}{StatusDelta(p, isArabic: true)}",
                        DescriptionEn = $"Work order{reference} was cancelled{StatusDelta(p, isArabic: false)}",
                        Icon = "ki-outline ki-cross-circle", Color = "danger",
                    };

                default:
                    throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Not a work-order event type.");
            }
        }

        // Slice 3: the reversal presentation names BOTH entries, because "which entry reversed this one?" is the
        // question the audit reader actually has. The amount is shown because the payload already carries it.
        private static Presentation PresentJournalEntry(string eventType, JournalEntryEventPayload? p)
        {
            if (eventType != JournalEntryEvents.Reversed)
                throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Not a journal-entry event type.");

            string original = string.IsNullOrWhiteSpace(p?.OriginalJournalNumber)
                ? (p?.OriginalJournalEntryId > 0 ? "#" + p!.OriginalJournalEntryId : "")
                : p!.OriginalJournalNumber!;
            string mirror = string.IsNullOrWhiteSpace(p?.ReversingJournalNumber)
                ? (p?.ReversingJournalEntryId > 0 ? "#" + p!.ReversingJournalEntryId : "")
                : p!.ReversingJournalNumber!;
            string reasonAr = string.IsNullOrWhiteSpace(p?.ReversalReason) ? "" : $" — السبب: {p!.ReversalReason}";
            string reasonEn = string.IsNullOrWhiteSpace(p?.ReversalReason) ? "" : $" — reason: {p!.ReversalReason}";
            string sourceAr = string.IsNullOrWhiteSpace(p?.OriginalSourceType) ? "" : $" (المصدر: {p!.OriginalSourceType})";
            string sourceEn = string.IsNullOrWhiteSpace(p?.OriginalSourceType) ? "" : $" (source: {p!.OriginalSourceType})";

            return new Presentation
            {
                TitleAr = "عكس قيد يومية", TitleEn = "Journal entry reversed",
                DescriptionAr = $"عُكس القيد {original} بالقيد {mirror}{sourceAr}" + Amount(" بإجمالي", p?.OriginalAmount) + reasonAr,
                DescriptionEn = $"Entry {original} was reversed by {mirror}{sourceEn}" + Amount(" totalling", p?.OriginalAmount) + reasonEn,
                Icon = "ki-outline ki-arrow-two-diagonals", Color = "danger",
            };
        }

        // -----------------------------------------------------------------------------------------------
        // Tasks
        //
        // PAYLOAD SENSITIVITY. The producer already decided what may leave the module — title only, never
        // Description — and this inherits that rather than re-deciding it. Two further omissions are made
        // HERE, deliberately:
        //   * assignee ids are bound but never printed. "Assigned to 4417" tells a reader nothing and adds an
        //     identifier to a row that did not need one; the transition itself is the fact worth showing.
        //   * nothing reads a field the payload does not carry, so a description never implies knowledge the
        //     event does not contain.
        // -----------------------------------------------------------------------------------------------
        private static Presentation PresentFinancialFact(string eventType)
        {
            var (ar, en) = eventType switch
            {
                SalesReturnEvents.Created => ("تسجيل مرتجع مبيعات", "Sales return recorded"),
                SalesReturnEvents.Updated => ("تعديل مرتجع مبيعات", "Sales return updated"),
                SalesReturnEvents.Reversed => ("عكس مرتجع مبيعات", "Sales return reversed"),
                PurchaseReturnEvents.Created => ("تسجيل مرتجع مشتريات", "Purchase return recorded"),
                PurchaseReturnEvents.Updated => ("تعديل مرتجع مشتريات", "Purchase return updated"),
                PurchaseReturnEvents.Reversed => ("عكس مرتجع مشتريات", "Purchase return reversed"),
                ReceiptEvents.Created => ("تسجيل سند قبض", "Receipt recorded"),
                ReceiptEvents.Allocated => ("تخصيص سند قبض", "Receipt allocated"),
                ReceiptEvents.Updated => ("تعديل سند قبض", "Receipt updated"),
                ReceiptEvents.Reversed => ("عكس سند قبض", "Receipt reversed"),
                PaymentEvents.Created => ("تسجيل سند دفع", "Payment recorded"),
                PaymentEvents.Allocated => ("تخصيص سند دفع", "Payment allocated"),
                PaymentEvents.Updated => ("تعديل سند دفع", "Payment updated"),
                PaymentEvents.Reversed => ("عكس سند دفع", "Payment reversed"),
                _ => throw new ArgumentOutOfRangeException(nameof(eventType)),
            };
            return new Presentation
            {
                TitleAr = ar, TitleEn = en, Icon = "ki-outline ki-document",
                Color = BusinessEventTypes.ActionOf(eventType) == "Reversed" ? "danger" : "primary",
            };
        }

        private static Presentation PresentTask(string eventType, TaskEventPayload? p)
        {
            string title = string.IsNullOrWhiteSpace(p?.Title) ? "" : p!.Title!;
            string titleAr = title.Length == 0 ? "" : $": {title}";
            string titleEn = title.Length == 0 ? "" : $": {title}";

            switch (eventType)
            {
                case TaskEvents.HoldRequested:
                case TaskEvents.HoldApproved:
                case TaskEvents.HoldRejected:
                case TaskEvents.HoldResumed:
                    var (holdAr, holdEn) = eventType switch
                    {
                        TaskEvents.HoldRequested => ("طلب تعليق المهمة", "Task hold requested"),
                        TaskEvents.HoldApproved => ("اعتماد تعليق المهمة", "Task hold approved"),
                        TaskEvents.HoldRejected => ("رفض تعليق المهمة", "Task hold rejected"),
                        _ => ("استئناف المهمة", "Task resumed"),
                    };
                    return new Presentation
                    {
                        TitleAr = holdAr, TitleEn = holdEn,
                        Icon = "ki-outline ki-time", Color = "info",
                    };
                case TaskEvents.Created:
                    return new Presentation
                    {
                        TitleAr = "أُنشئت مهمة", TitleEn = "Task created",
                        DescriptionAr = $"مهمة جديدة{titleAr}" + Due(" — تستحق", p?.NewDueAt, arabic: true),
                        DescriptionEn = $"New task{titleEn}" + Due(" — due", p?.NewDueAt, arabic: false),
                        Icon = "ki-outline ki-check-square", Color = "primary",
                    };

                case TaskEvents.Assigned:
                    return new Presentation
                    {
                        TitleAr = "أُسندت المهمة", TitleEn = "Task assigned",
                        DescriptionAr = "أُسندت المهمة إلى موظف" + Due(" — تستحق", p?.NewDueAt, arabic: true),
                        DescriptionEn = "The task was assigned to an employee" + Due(" — due", p?.NewDueAt, arabic: false),
                        Icon = "ki-outline ki-user-tick", Color = "info",
                    };

                case TaskEvents.Reassigned:
                    return new Presentation
                    {
                        TitleAr = "أُعيد إسناد المهمة", TitleEn = "Task reassigned",
                        DescriptionAr = "نُقلت المهمة إلى موظف آخر",
                        DescriptionEn = "The task was moved to a different employee",
                        Icon = "ki-outline ki-arrows-circle", Color = "info",
                    };

                case TaskEvents.StatusChanged:
                    return new Presentation
                    {
                        TitleAr = "تغيّرت حالة المهمة", TitleEn = "Task status changed",
                        DescriptionAr = Transition("الحالة", p?.PreviousStatus, p?.NewStatus),
                        DescriptionEn = Transition("Status", p?.PreviousStatus, p?.NewStatus),
                        Icon = "ki-outline ki-arrow-right-left", Color = "warning",
                    };

                case TaskEvents.DueDateChanged:
                    return new Presentation
                    {
                        TitleAr = "تغيّر تاريخ الاستحقاق", TitleEn = "Due date changed",
                        DescriptionAr = Transition("الاستحقاق", Stamp(p?.PreviousDueAt), Stamp(p?.NewDueAt)),
                        DescriptionEn = Transition("Due", Stamp(p?.PreviousDueAt), Stamp(p?.NewDueAt)),
                        Icon = "ki-outline ki-calendar-edit", Color = "warning",
                    };

                case TaskEvents.BecameOverdue:
                    return new Presentation
                    {
                        TitleAr = "تأخّرت المهمة", TitleEn = "Task became overdue",
                        DescriptionAr = "تجاوزت المهمة تاريخ استحقاقها" + Due(" —", p?.NewDueAt, arabic: true),
                        DescriptionEn = "The task passed its due date" + Due(" —", p?.NewDueAt, arabic: false),
                        Icon = "ki-outline ki-timer", Color = "danger",
                    };

                case TaskEvents.Completed:
                    return new Presentation
                    {
                        TitleAr = "اكتملت المهمة", TitleEn = "Task completed",
                        DescriptionAr = $"أُنجزت المهمة{titleAr}",
                        DescriptionEn = $"The task was completed{titleEn}",
                        Icon = "ki-outline ki-check-circle", Color = "success",
                    };

                case TaskEvents.Reopened:
                    return new Presentation
                    {
                        TitleAr = "أُعيد فتح المهمة", TitleEn = "Task reopened",
                        DescriptionAr = Transition("الحالة", p?.PreviousStatus, p?.NewStatus),
                        DescriptionEn = Transition("Status", p?.PreviousStatus, p?.NewStatus),
                        Icon = "ki-outline ki-arrow-circle-left", Color = "warning",
                    };

                case TaskEvents.Cancelled:
                    return new Presentation
                    {
                        TitleAr = "أُلغيت المهمة", TitleEn = "Task cancelled",
                        DescriptionAr = $"أُلغيت المهمة{titleAr}",
                        DescriptionEn = $"The task was cancelled{titleEn}",
                        Icon = "ki-outline ki-cross-circle", Color = "danger",
                    };

                default:
                    throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Not a task event type.");
            }
        }

        // -----------------------------------------------------------------------------------------------
        // Calendar
        //
        // PAYLOAD SENSITIVITY, two deliberate omissions:
        //   * CancellationReason is bound but NEVER rendered. It is free text a user typed about why a
        //     meeting was called off, it can name people or state a reason that is nobody else's business,
        //     and a timeline row is read by everyone who can see the entity. The FACT of cancellation is the
        //     event; the reason is not needed to understand it. (JournalEntry.Reversed does render its
        //     reason — that field is an accounting-controlled justification on a financial correction, a
        //     different kind of text with a different audience.)
        //   * attendee ids are not printed, for the same reason as Task assignees. AttendeeCount is, because
        //     the producer chose a count precisely so the size of a meeting could be shown without naming
        //     who is in it.
        // -----------------------------------------------------------------------------------------------
        private static Presentation PresentCalendarEvent(string eventType, CalendarEventPayload? p)
        {
            string title = string.IsNullOrWhiteSpace(p?.Title) ? "" : p!.Title!;
            string titleAr = title.Length == 0 ? "" : $": {title}";
            string titleEn = title.Length == 0 ? "" : $": {title}";

            switch (eventType)
            {
                case CalendarEventEvents.Created:
                {
                    string whenAr = p?.IsAllDay == true ? " (طوال اليوم)" : Due(" — يبدأ", p?.StartUtc, arabic: true);
                    string whenEn = p?.IsAllDay == true ? " (all day)" : Due(" — starts", p?.StartUtc, arabic: false);
                    string whoAr = p?.AttendeeCount > 0 ? $" — {p!.AttendeeCount} مدعوّ" : "";
                    string whoEn = p?.AttendeeCount > 0 ? $" — {p!.AttendeeCount} attendee(s)" : "";
                    return new Presentation
                    {
                        TitleAr = "أُنشئ حدث", TitleEn = "Calendar event created",
                        DescriptionAr = $"حدث جديد{titleAr}{whenAr}{whoAr}",
                        DescriptionEn = $"New event{titleEn}{whenEn}{whoEn}",
                        Icon = "ki-outline ki-calendar-add", Color = "info",
                    };
                }

                case CalendarEventEvents.Updated:
                {
                    // Field NAMES only — the producer emits no values, and this shows no more than it is given.
                    string fields = p?.ChangedFields is { Count: > 0 } ? string.Join(", ", p.ChangedFields) : "";
                    return new Presentation
                    {
                        TitleAr = "عُدّل الحدث", TitleEn = "Calendar event updated",
                        DescriptionAr = fields.Length == 0 ? "عُدّل الحدث" : $"تغيّرت الحقول: {fields}",
                        DescriptionEn = fields.Length == 0 ? "The event was updated" : $"Changed fields: {fields}",
                        Icon = "ki-outline ki-calendar-edit", Color = "warning",
                    };
                }

                case CalendarEventEvents.Rescheduled:
                    return new Presentation
                    {
                        TitleAr = "أُعيدت جدولة الحدث", TitleEn = "Calendar event rescheduled",
                        DescriptionAr = Transition("البداية", Stamp(p?.PreviousStartUtc), Stamp(p?.StartUtc)),
                        DescriptionEn = Transition("Start", Stamp(p?.PreviousStartUtc), Stamp(p?.StartUtc)),
                        Icon = "ki-outline ki-time", Color = "warning",
                    };

                case CalendarEventEvents.Cancelled:
                    // Reason deliberately not rendered — see the block comment above.
                    return new Presentation
                    {
                        TitleAr = "أُلغي الحدث", TitleEn = "Calendar event cancelled",
                        DescriptionAr = $"أُلغي الحدث{titleAr}",
                        DescriptionEn = $"The event was cancelled{titleEn}",
                        Icon = "ki-outline ki-calendar-remove", Color = "danger",
                    };

                case CalendarEventEvents.AttendeeAdded:
                    return new Presentation
                    {
                        TitleAr = "أُضيف مدعوّ", TitleEn = "Attendee added",
                        DescriptionAr = "أُضيف مدعوّ إلى الحدث",
                        DescriptionEn = "An attendee was added to the event",
                        Icon = "ki-outline ki-user-tick", Color = "info",
                    };

                case CalendarEventEvents.AttendeeRemoved:
                    return new Presentation
                    {
                        TitleAr = "أُزيل مدعوّ", TitleEn = "Attendee removed",
                        DescriptionAr = "أُزيل مدعوّ من الحدث",
                        DescriptionEn = "An attendee was removed from the event",
                        Icon = "ki-outline ki-user-cross", Color = "warning",
                    };

                case CalendarEventEvents.ReminderTriggered:
                    return new Presentation
                    {
                        TitleAr = "تذكير بالحدث", TitleEn = "Event reminder",
                        DescriptionAr = $"أُرسل تذكير{titleAr}",
                        DescriptionEn = $"A reminder was sent{titleEn}",
                        Icon = "ki-outline ki-notification-bing", Color = "info",
                    };

                case CalendarEventEvents.Started:
                    return new Presentation
                    {
                        TitleAr = "بدأ الحدث", TitleEn = "Calendar event started",
                        DescriptionAr = $"بدأ الحدث{titleAr}",
                        DescriptionEn = $"The event started{titleEn}",
                        Icon = "ki-outline ki-play", Color = "primary",
                    };

                case CalendarEventEvents.Completed:
                    return new Presentation
                    {
                        TitleAr = "انتهى الحدث", TitleEn = "Calendar event completed",
                        DescriptionAr = $"انتهى الحدث{titleAr}",
                        DescriptionEn = $"The event finished{titleEn}",
                        Icon = "ki-outline ki-check-circle", Color = "success",
                    };

                default:
                    throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Not a calendar event type.");
            }
        }

        // "X: a -> b", skipping either side the payload did not carry rather than printing an empty arrow.
        private static string Transition(string label, string? previous, string? current)
        {
            bool hasPrevious = !string.IsNullOrWhiteSpace(previous);
            bool hasCurrent = !string.IsNullOrWhiteSpace(current);
            if (hasPrevious && hasCurrent) return $"{label}: {previous} → {current}";
            if (hasCurrent) return $"{label}: {current}";
            return label;
        }

        private static string Due(string prefix, DateTime? value, bool arabic)
            => value.HasValue ? $"{prefix} {Stamp(value)}" : "";

        // Date only. A timeline row is read in the viewer's own day, and a UTC clock time on it invites the
        // reader to compare it against a local one — the presenter has no time zone to render it in.
        private static string? Stamp(DateTime? value)
            => value?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        private static string Quantity(string prefix, decimal? value)
            => value.HasValue ? $"{prefix} {value.Value:N2}" : "";

        private static string TotalDelta(decimal? before, decimal? after, bool isArabic)
        {
            if (before == null || after == null || before == after) return "";
            return isArabic ? $" — total {before.Value:N2} → {after.Value:N2}"
                            : $" — total {before.Value:N2} → {after.Value:N2}";
        }

        private static string StatusDelta(ManufWorkOrderEventPayload? p, bool isArabic)
        {
            if (p?.OldStatus == null || p.NewStatus == null || p.OldStatus == p.NewStatus) return "";
            return isArabic ? $" — status {p.OldStatus} → {p.NewStatus}"
                            : $" — status {p.OldStatus} → {p.NewStatus}";
        }

        private static string Amount(string prefix, decimal? value)
            => value.HasValue ? $"{prefix} {value.Value:N2}" : "";

        private static string FieldList(string[]? fields, bool isArabic)
        {
            if (fields == null || fields.Length == 0) return "";
            var names = string.Join(isArabic ? "، " : ", ", fields.Select(f => FieldLabel(f, isArabic)));
            return isArabic ? $" — fields: {names}" : $" — fields: {names}";
        }

        // Field names come from the producer as invoice property names; anything unmapped shows as-is.
        private static string FieldLabel(string field, bool isArabic) => field switch
        {
            "CustomerId" => isArabic ? "العميل" : "Customer",
            "InvoiceDate" => isArabic ? "التاريخ" : "Date",
            "CurrencyId" => isArabic ? "العملة" : "Currency",
            "ExchangeRate" => isArabic ? "سعر الصرف" : "Exchange rate",
            "ProjectId" => isArabic ? "المشروع" : "Project",
            "Notes" => isArabic ? "الملاحظات" : "Notes",
            "Lines" => isArabic ? "البنود" : "Lines",
            "GrandTotal" => isArabic ? "الإجمالي" : "Total",
            // ---- slice 2: Customer ----
            "Name" => isArabic ? "الاسم" : "Name",
            "NameEn" => isArabic ? "الاسم بالإنجليزية" : "English name",
            "TaxRegNo" => isArabic ? "الرقم الضريبي" : "Tax registration",
            "CreditLimit" => isArabic ? "حدّ الائتمان" : "Credit limit",
            "PaymentTermsDays" => isArabic ? "مدة السداد" : "Payment terms",
            "Segment" => isArabic ? "التصنيف" : "Segment",
            "IsActive" => isArabic ? "الحالة" : "Status",
            "Address" => isArabic ? "العنوان" : "Address",
            "ShippingAddress" => isArabic ? "عنوان الشحن" : "Shipping address",
            "Phone" => isArabic ? "الهاتف" : "Phone",
            "Email" => isArabic ? "البريد الإلكتروني" : "Email",
            "ContactPerson" => isArabic ? "مسؤول الاتصال" : "Contact person",
            // ---- slice 2: Purchase invoice ----
            "VendorId" => isArabic ? "المورّد" : "Supplier",
            // ---- CRM Wave 1 ----
            "Title" => isArabic ? "العنوان" : "Title",
            "Status" => isArabic ? "الحالة" : "Status",
            "Source" => isArabic ? "المصدر" : "Source",
            "Company" => isArabic ? "الجهة" : "Company",
            "OwnerEmployeeId" => isArabic ? "المسؤول" : "Owner",
            "EstimatedValue" => isArabic ? "القيمة التقديرية" : "Estimated value",
            "CampaignId" => isArabic ? "الحملة" : "Campaign",
            "AccountId" => isArabic ? "الحساب" : "Account",
            "Amount" => isArabic ? "القيمة" : "Amount",
            "ExpectedCloseDate" => isArabic ? "تاريخ الإغلاق المتوقع" : "Expected close date",
            "WinLossReason" => isArabic ? "سبب الربح أو الخسارة" : "Win or loss reason",
            "Industry" => isArabic ? "القطاع" : "Industry",
            // ---- slice 2: Work order ----
            "Qty" => isArabic ? "الكمية" : "Quantity",
            "PlannedStart" => isArabic ? "بداية مخطّطة" : "Planned start",
            "PlannedEnd" => isArabic ? "نهاية مخطّطة" : "Planned end",
            "LaborCost" => isArabic ? "تكلفة العمالة" : "Labour cost",
            "OverheadCost" => isArabic ? "التكاليف غير المباشرة" : "Overhead cost",
            _ => field,
        };

        // ---- CRM Wave 1 ---------------------------------------------------------------------------
        //
        // EVERY LINE BELOW IS DERIVED FROM THE PAYLOAD AND FROM NOTHING ELSE. The presenter is pure:
        // it never touches the database, so a field the producer did not send is simply not shown
        // rather than fetched. That is why each fragment is guarded on its own value instead of being
        // interpolated blindly - an event recorded before a field existed still renders honestly.

        private static Presentation PresentLead(string eventType, LeadEventPayload? p)
        {
            string name = string.IsNullOrWhiteSpace(p?.LeadName) ? "" : $" «{p!.LeadName}»";
            string company = string.IsNullOrWhiteSpace(p?.CompanyName) ? "" : $" — {p!.CompanyName}";

            if (eventType == LeadEvents.Created)
                return new Presentation
                {
                    TitleAr = "عميل محتمل جديد", TitleEn = "Lead created",
                    DescriptionAr = $"أُضيف عميل محتمل{name}{company}" + Money(" — القيمة التقديرية", p?.EstimatedValue),
                    DescriptionEn = $"Lead{name} was added{company}" + Money(" — estimated value", p?.EstimatedValue),
                    Icon = "ki-outline ki-user", Color = "info",
                };

            if (eventType == LeadEvents.Converted)
                return new Presentation
                {
                    TitleAr = "تحويل عميل محتمل", TitleEn = "Lead converted",
                    // The account and opportunity ids are reported only when the producer sent them.
                    DescriptionAr = $"حُوّل العميل المحتمل{name} إلى حساب" + Ref(" — الحساب رقم", p?.CrmAccountId),
                    DescriptionEn = $"Lead{name} was converted into an account" + Ref(" — account", p?.CrmAccountId),
                    Icon = "ki-outline ki-arrow-right", Color = "success",
                };

            // Updated. A status move is the one substantive flip a lead has, so it is called out
            // instead of disappearing into the field list - the same treatment Customer.Updated gets.
            string statusAr = "", statusEn = "";
            if (!string.IsNullOrWhiteSpace(p?.PreviousStatus) && !string.IsNullOrWhiteSpace(p?.Status)
                && !string.Equals(p!.PreviousStatus, p.Status, StringComparison.Ordinal))
            {
                statusAr = $" — الحالة {p.PreviousStatus} ← {p.Status}";
                statusEn = $" — status {p.PreviousStatus} → {p.Status}";
            }
            return new Presentation
            {
                TitleAr = "تعديل عميل محتمل", TitleEn = "Lead updated",
                DescriptionAr = $"عُدّل العميل المحتمل{name}{FieldList(p?.ChangedFields, isArabic: true)}{statusAr}",
                DescriptionEn = $"Lead{name} was updated{FieldList(p?.ChangedFields, isArabic: false)}{statusEn}",
                Icon = "ki-outline ki-pencil", Color = "warning",
            };
        }

        private static Presentation PresentOpportunity(string eventType, OpportunityEventPayload? p)
        {
            string title = string.IsNullOrWhiteSpace(p?.OpportunityTitle) ? "" : $" «{p!.OpportunityTitle}»";
            string amountAr = Money(" — القيمة", p?.Amount);
            string amountEn = Money(" — amount", p?.Amount);

            switch (eventType)
            {
                case OpportunityEvents.Created:
                    return new Presentation
                    {
                        TitleAr = "فرصة بيعية جديدة", TitleEn = "Opportunity created",
                        DescriptionAr = $"أُضيفت فرصة{title}{amountAr}" + Named(" — المرحلة", p?.StageName),
                        DescriptionEn = $"Opportunity{title} was added{amountEn}" + Named(" — stage", p?.StageName),
                        Icon = "ki-outline ki-basket", Color = "primary",
                    };

                case OpportunityEvents.StageChanged:
                    // The move itself is the fact. Both ends are shown when both were sent; a producer
                    // that knew only the destination still renders a true sentence.
                    string moveAr = !string.IsNullOrWhiteSpace(p?.PreviousStageName) && !string.IsNullOrWhiteSpace(p?.StageName)
                        ? $" — {p!.PreviousStageName} ← {p.StageName}" : Named(" — المرحلة", p?.StageName);
                    string moveEn = !string.IsNullOrWhiteSpace(p?.PreviousStageName) && !string.IsNullOrWhiteSpace(p?.StageName)
                        ? $" — {p!.PreviousStageName} → {p.StageName}" : Named(" — stage", p?.StageName);
                    return new Presentation
                    {
                        TitleAr = "تغيير مرحلة الفرصة", TitleEn = "Opportunity stage changed",
                        DescriptionAr = $"انتقلت الفرصة{title}{moveAr}",
                        DescriptionEn = $"Opportunity{title} moved{moveEn}",
                        Icon = "ki-outline ki-arrow-right", Color = "info",
                    };

                case OpportunityEvents.Won:
                    return new Presentation
                    {
                        TitleAr = "ربح الفرصة", TitleEn = "Opportunity won",
                        DescriptionAr = $"رُبحت الفرصة{title}{amountAr}" + Named(" — السبب", p?.WinLossReason),
                        DescriptionEn = $"Opportunity{title} was won{amountEn}" + Named(" — reason", p?.WinLossReason),
                        Icon = "ki-outline ki-check-circle", Color = "success",
                    };

                case OpportunityEvents.Lost:
                    return new Presentation
                    {
                        TitleAr = "خسارة الفرصة", TitleEn = "Opportunity lost",
                        DescriptionAr = $"خُسرت الفرصة{title}{amountAr}" + Named(" — السبب", p?.WinLossReason),
                        DescriptionEn = $"Opportunity{title} was lost{amountEn}" + Named(" — reason", p?.WinLossReason),
                        Icon = "ki-outline ki-cross-circle", Color = "danger",
                    };

                default:   // Updated
                    return new Presentation
                    {
                        TitleAr = "تعديل فرصة بيعية", TitleEn = "Opportunity updated",
                        DescriptionAr = $"عُدّلت الفرصة{title}{FieldList(p?.ChangedFields, isArabic: true)}",
                        DescriptionEn = $"Opportunity{title} was updated{FieldList(p?.ChangedFields, isArabic: false)}",
                        Icon = "ki-outline ki-pencil", Color = "warning",
                    };
            }
        }

        private static Presentation PresentCrmAccount(string eventType, CrmAccountEventPayload? p)
        {
            string name = string.IsNullOrWhiteSpace(p?.AccountName) ? "" : $" «{p!.AccountName}»";

            if (eventType == CrmAccountEvents.Created)
                return new Presentation
                {
                    TitleAr = "حساب عميل جديد", TitleEn = "CRM account created",
                    // ConvertedFromLeadId is the one fact that distinguishes an account born from a
                    // conversion from one typed in directly, and the producer sends it only then.
                    DescriptionAr = $"أُضيف حساب{name}" + Named(" — القطاع", p?.Industry)
                                    + Ref(" — من العميل المحتمل رقم", p?.ConvertedFromLeadId),
                    DescriptionEn = $"Account{name} was added" + Named(" — industry", p?.Industry)
                                    + Ref(" — converted from lead", p?.ConvertedFromLeadId),
                    Icon = "ki-outline ki-profile-circle", Color = "success",
                };

            return new Presentation
            {
                TitleAr = "تعديل حساب عميل", TitleEn = "CRM account updated",
                DescriptionAr = $"عُدّل الحساب{name}{FieldList(p?.ChangedFields, isArabic: true)}",
                DescriptionEn = $"Account{name} was updated{FieldList(p?.ChangedFields, isArabic: false)}",
                Icon = "ki-outline ki-pencil", Color = "warning",
            };
        }

        private static Presentation PresentCrmFollowUp(string eventType, CrmFollowUpEventPayload? p)
        {
            string subject = string.IsNullOrWhiteSpace(p?.Subject) ? "" : $" «{p!.Subject}»";
            // The anchor is named only when the producer sent a name; the code and id alone would be
            // an internal identifier on a user screen.
            string onAr = string.IsNullOrWhiteSpace(p?.EntityName) ? "" : $" — على {p!.EntityName}";
            string onEn = string.IsNullOrWhiteSpace(p?.EntityName) ? "" : $" — on {p!.EntityName}";

            bool completed = eventType == LeadEvents.FollowUpCompleted
                             || eventType == OpportunityEvents.FollowUpCompleted;

            if (completed)
                return new Presentation
                {
                    TitleAr = "إنجاز متابعة", TitleEn = "Follow-up completed",
                    DescriptionAr = $"أُنجزت المتابعة{subject}{onAr}",
                    DescriptionEn = $"Follow-up{subject} was completed{onEn}",
                    Icon = "ki-outline ki-check-square", Color = "success",
                };

            return new Presentation
            {
                TitleAr = "جدولة متابعة", TitleEn = "Follow-up scheduled",
                DescriptionAr = $"جُدولت متابعة{subject}{onAr}" + Due(" — تستحق", p?.DueDate, arabic: true),
                DescriptionEn = $"Follow-up{subject} was scheduled{onEn}" + Due(" — due", p?.DueDate, arabic: false),
                Icon = "ki-outline ki-calendar", Color = "primary",
            };
        }

        // Two small shared fragments. Both return "" for a missing value, which is how the presenter
        // stays honest about a payload that did not carry the field.
        private static string Money(string prefix, decimal? value)
            => value.HasValue ? $"{prefix} {value.Value:N2}" : "";

        private static string Named(string prefix, string? value)
            => string.IsNullOrWhiteSpace(value) ? "" : $"{prefix} {value}";

        private static string Ref(string prefix, int? id)
            => id.HasValue && id.Value > 0 ? $"{prefix} #{id.Value}" : "";

        private static string Delta(SalesInvoiceEventPayload? p, bool isArabic)
        {
            if (p?.TotalBefore == null || p.TotalAfter == null || p.TotalBefore == p.TotalAfter) return "";
            return isArabic
                ? $" — total {p.TotalBefore.Value:N2} → {p.TotalAfter.Value:N2}"
                : $" — total {p.TotalBefore.Value:N2} → {p.TotalAfter.Value:N2}";
        }
    }
}
