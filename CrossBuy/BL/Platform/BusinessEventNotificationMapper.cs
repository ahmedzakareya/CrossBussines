using System.Text.Json;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel slice 2 (ADR-006) — decides WHICH notifications an event asks for.
    //
    // It is deliberately a pure function of the envelope: no database, no permissions, no idempotency, no
    // delivery. Those belong to the consumer. That split is what keeps this file readable as the event
    // catalogue grows, and it is why the worker holds no switch statement at all.
    //
    // Returning an empty list is a normal, successful answer: most events are timeline-only.
    public interface IBusinessEventNotificationMapper
    {
        IReadOnlyList<NotificationCommand> Map(BusinessEventEnvelope envelope);
    }

    public class BusinessEventNotificationMapper : IBusinessEventNotificationMapper
    {
        private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

        // The two audiences the existing NotificationService can resolve.
        private const string ScopeAccounting = "acc";
        private const string ScopeInventory = "inv";

        public IReadOnlyList<NotificationCommand> Map(BusinessEventEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);

            return envelope.EventType switch
            {
                // ---- Sales invoice. Replaces the legacy after-commit NotifyRoleAsync that used to sit in
                // ReceivableService.CreateSalesInvoiceAsync — same audience, same catalog type, same wording,
                // so users see no change other than the click-through now resolving through the registry.
                SalesInvoiceEvents.Created => One(envelope, SalesInvoice(envelope)),

                // ---- Purchase invoice. Replaces the legacy call in PayableService.CreatePurchaseInvoiceAsync.
                PurchaseInvoiceEvents.Created => One(envelope, PurchaseInvoice(envelope)),

                // ---- Work order. No legacy notification existed for manufacturing at all, so these are new
                // and go to the inventory audience, which is the RBAC that governs the work-order screens.
                ManufWorkOrderEvents.Released => One(envelope, WorkOrderReleased(envelope)),
                ManufWorkOrderEvents.Completed => One(envelope, WorkOrderCompleted(envelope)),

                // ---- Everything else is timeline-only, ON PURPOSE:
                // Customer.Created / Customer.Updated — customer master edits are routine and high-volume;
                //   notifying accountants on every one would be noise, and no legacy notification existed.
                // SalesInvoice.Updated / PurchaseInvoice.Updated — no legacy notification existed for edits.
                // ManufWorkOrder.Created / .Updated / .Produced / .Cancelled — creation and header edits are
                //   the actor's own routine work; partial production fires many times per order.
                _ => Array.Empty<NotificationCommand>(),
            };
        }

        private static IReadOnlyList<NotificationCommand> One(BusinessEventEnvelope envelope, NotificationCommand? command)
            => command == null ? Array.Empty<NotificationCommand>() : new[] { command };

        private static NotificationCommand? SalesInvoice(BusinessEventEnvelope envelope)
        {
            var p = Payload<SalesInvoiceEventPayload>(envelope);
            var reference = p?.ReferenceNumber ?? ("#" + envelope.Entity.Id);
            var total = p?.TotalAfter;

            return new NotificationCommand
            {
                RecipientScope = ScopeAccounting,
                RecipientRoles = new[] { "ChiefAccountant" },
                CompanyId = envelope.Context.CompanyId,
                BranchId = envelope.Context.BranchId,
                TitleAr = "فاتورة بيع جديدة", TitleEn = "New sales invoice",
                MessageAr = $"صدرت فاتورة بيع {reference}" + Amount("بقيمة", total),
                MessageEn = $"Sales invoice {reference} issued" + Amount("for", total),
                Type = NotificationTypes.SalesInvoice,
                EntityType = EntityRegistry.SalesInvoice, EntityId = envelope.Entity.Id,
                ActorEmployeeId = envelope.Actor.EmployeeId,
                DedupKeyPrefix = DedupPrefix(envelope),
            };
        }

        private static NotificationCommand? PurchaseInvoice(BusinessEventEnvelope envelope)
        {
            var p = Payload<PurchaseInvoiceEventPayload>(envelope);
            var reference = p?.InvoiceNumber ?? ("#" + envelope.Entity.Id);
            var supplierAr = string.IsNullOrWhiteSpace(p?.SupplierName) ? "" : $" من المورد {p!.SupplierName}";
            var supplierEn = string.IsNullOrWhiteSpace(p?.SupplierName) ? "" : $" from {p!.SupplierName}";

            return new NotificationCommand
            {
                RecipientScope = ScopeAccounting,
                RecipientRoles = new[] { "ChiefAccountant" },
                CompanyId = envelope.Context.CompanyId,
                BranchId = envelope.Context.BranchId,
                TitleAr = "فاتورة شراء جديدة", TitleEn = "New purchase invoice",
                MessageAr = $"سُجّلت فاتورة شراء {reference}{supplierAr}" + Amount("بقيمة", p?.TotalAfter),
                MessageEn = $"Purchase invoice {reference}{supplierEn} recorded" + Amount("for", p?.TotalAfter),
                Type = NotificationTypes.PurchaseInvoice,
                EntityType = EntityRegistry.PurchaseInvoice, EntityId = envelope.Entity.Id,
                ActorEmployeeId = envelope.Actor.EmployeeId,
                DedupKeyPrefix = DedupPrefix(envelope),
            };
        }

        private static NotificationCommand? WorkOrderReleased(BusinessEventEnvelope envelope)
        {
            var p = Payload<ManufWorkOrderEventPayload>(envelope);
            var reference = p?.WorkOrderNumber ?? ("#" + envelope.Entity.Id);
            var itemAr = string.IsNullOrWhiteSpace(p?.ItemName) ? "" : $" — {p!.ItemName}";
            var itemEn = string.IsNullOrWhiteSpace(p?.ItemName) ? "" : $" — {p!.ItemName}";

            return new NotificationCommand
            {
                RecipientScope = ScopeInventory,
                RecipientRoles = new[] { "InventoryManager", "WarehouseKeeper" },
                CompanyId = envelope.Context.CompanyId,
                BranchId = envelope.Context.BranchId,
                TitleAr = "إصدار أمر تشغيل", TitleEn = "Work order released",
                MessageAr = $"صدر أمر التشغيل {reference} للإنتاج{itemAr}" + Quantity("بكمية", p?.PlannedQuantity),
                MessageEn = $"Work order {reference} was released to production{itemEn}" + Quantity("for a quantity of", p?.PlannedQuantity),
                Type = NotificationTypes.WorkOrderReleased,
                Category = "Manufacturing",
                EntityType = EntityRegistry.ManufWorkOrder, EntityId = envelope.Entity.Id,
                ActorEmployeeId = envelope.Actor.EmployeeId,
                DedupKeyPrefix = DedupPrefix(envelope),
            };
        }

        private static NotificationCommand? WorkOrderCompleted(BusinessEventEnvelope envelope)
        {
            var p = Payload<ManufWorkOrderEventPayload>(envelope);
            var reference = p?.WorkOrderNumber ?? ("#" + envelope.Entity.Id);

            return new NotificationCommand
            {
                RecipientScope = ScopeInventory,
                RecipientRoles = new[] { "InventoryManager" },
                CompanyId = envelope.Context.CompanyId,
                BranchId = envelope.Context.BranchId,
                TitleAr = "اكتمال أمر تشغيل", TitleEn = "Work order completed",
                MessageAr = $"اكتمل أمر التشغيل {reference}" + Quantity("بكمية منتجة", p?.CompletedQuantityAfter),
                MessageEn = $"Work order {reference} completed" + Quantity("with a produced quantity of", p?.CompletedQuantityAfter),
                Type = NotificationTypes.WorkOrderCompleted,
                Category = "Manufacturing",
                EntityType = EntityRegistry.ManufWorkOrder, EntityId = envelope.Entity.Id,
                ActorEmployeeId = envelope.Actor.EmployeeId,
                DedupKeyPrefix = DedupPrefix(envelope),
            };
        }

        // The event's own identity is the idempotency root: it never changes across dispatch retries, and it
        // is unique per event, so it cannot collide with a different occurrence of the same event type.
        private static string DedupPrefix(BusinessEventEnvelope envelope) => "evt:" + envelope.EventUid.ToString("N");

        private static T? Payload<T>(BusinessEventEnvelope envelope) where T : class
        {
            if (envelope.Payload == null) return null;
            // A payload that will not bind is not a reason to lose the notification: the mapper falls back to
            // the entity id in the message text. Payload SHAPE is validated by the timeline consumer, whose
            // dispatch row is the right place for that failure to surface.
            try { return envelope.Payload.Deserialize<T>(PayloadJson); }
            catch (JsonException) { return null; }
        }

        private static string Amount(string prefix, decimal? value) => value.HasValue ? $" {prefix} {value.Value:N2}" : "";
        private static string Quantity(string prefix, decimal? value) => value.HasValue ? $" {prefix} {value.Value:N2}" : "";
    }
}
