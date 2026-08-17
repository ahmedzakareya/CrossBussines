namespace CrossBuy.Models.Platform
{
    // Platform Kernel — the Sales Invoice pilot payload (PayloadVersion 1).
    //
    // A change SUMMARY, never the entity graph: no lines, no customer record, no GL entry, no cost or
    // margin figures. Everything here is already visible to anyone who may open the invoice, which is what
    // lets these events carry Visibility = Internal.
    public sealed class SalesInvoiceEventPayload
    {
        public const int Version = 1;

        // Human-facing document number (InvoiceNo), e.g. "SV-2026-00042".
        public string? ReferenceNumber { get; init; }

        // Names of the header fields an edit actually changed. Field NAMES only — never old/new values,
        // which is what keeps an edit summary free of anything the reader could not already see.
        public string[]? ChangedFields { get; init; }

        public string? OldStatus { get; init; }
        public string? NewStatus { get; init; }

        // Document-currency grand totals. null on create for "before".
        public decimal? TotalBefore { get; init; }
        public decimal? TotalAfter { get; init; }
    }
}