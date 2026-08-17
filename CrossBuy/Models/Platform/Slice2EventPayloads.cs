namespace CrossBuy.Models.Platform
{
    // Platform Kernel slice 2 — pilot payloads for Customer, Purchase Invoice and Manufacturing Work Order.
    //
    // Every one of these is a change SUMMARY, never the entity graph. Shared rules (PKS-001 §4.4):
    //   * no lines / components / BOM / routing / GL entries / navigation collections;
    //   * no files, binary data, secrets, passwords, tokens or connection strings;
    //   * no private contact data (phone, email, address) — a name and a code are enough to identify a row,
    //     and everything in a payload is readable by anyone who may read the event;
    //   * changedFields carries field NAMES only, never old/new values, except for the small number of
    //     status/total fields named explicitly below.

    public sealed class CustomerEventPayload
    {
        public const int Version = 1;

        // Tax registration number doubles as the customer's business code in this schema; there is no
        // separate Code column on Customer.
        public string? CustomerCode { get; init; }

        public string? CustomerName { get; init; }

        // Customer.Segment (VIP / Wholesale / Retail ...) — the closest thing to a customer type here.
        public string? CustomerType { get; init; }

        // "Active" / "Inactive", derived from Customer.IsActive. There is no status column.
        public string? InitialStatus { get; init; }
        public string? OldStatus { get; init; }
        public string? NewStatus { get; init; }

        public string[]? ChangedFields { get; init; }
    }

    public sealed class PurchaseInvoiceEventPayload
    {
        public const int Version = 1;

        public string? InvoiceNumber { get; init; }
        public int? SupplierId { get; init; }
        public string? SupplierName { get; init; }
        public DateTime? InvoiceDate { get; init; }

        // Document-currency grand totals. "Before" is null on create.
        public decimal? TotalBefore { get; init; }
        public decimal? TotalAfter { get; init; }

        public string? OldStatus { get; init; }
        public string? NewStatus { get; init; }
        public string[]? ChangedFields { get; init; }
    }

    public sealed class ManufWorkOrderEventPayload
    {
        public const int Version = 1;

        public string? WorkOrderNumber { get; init; }
        public int? ItemId { get; init; }
        public string? ItemName { get; init; }

        public decimal? PlannedQuantity { get; init; }
        public decimal? CompletedQuantityBefore { get; init; }
        public decimal? CompletedQuantityAfter { get; init; }

        public string? OldStatus { get; init; }
        public string? NewStatus { get; init; }

        public DateTime? PlannedStartDate { get; init; }
        public DateTime? PlannedEndDate { get; init; }

        public string[]? ChangedFields { get; init; }
    }
}
