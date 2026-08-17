namespace CrossBuy.Models.Platform
{
    // Platform Kernel slice 3 (Stage 0) — payload for JournalEntry.Reversed (PayloadVersion 1).
    //
    // Identity and linkage only. Deliberately EXCLUDED, because a journal entry is the most sensitive object in
    // the system and this payload is readable by anyone who may read the event:
    //   * journal lines (account ids, per-line debits/credits, cost centres, projects, employees);
    //   * account names or codes;
    //   * any employee data beyond the actor id the event header already carries;
    //   * credentials or connection data.
    //
    // OriginalAmount is the entry TOTAL (Σ debits) only — enough to recognise the entry in a timeline without
    // disclosing its composition. That single figure is why the event is recorded Confidential rather than
    // Internal (ADR-004): it is a monetary fact, so it sits behind the accounting "post" right.
    public sealed class JournalEntryEventPayload
    {
        public const int Version = 1;

        public int OriginalJournalEntryId { get; init; }
        public string? OriginalJournalNumber { get; init; }

        public int ReversingJournalEntryId { get; init; }
        public string? ReversingJournalNumber { get; init; }

        // What produced the original entry — e.g. "SalesInvoice", "PurchaseInvoice", "WorkOrder", "Payroll".
        // Free text in the schema (52 observed values); carried as-is so the timeline can name the source.
        public string? OriginalSourceType { get; init; }
        public int? OriginalSourceId { get; init; }

        public string? ReversalReason { get; init; }

        // Σ debits of the original entry, in the entry's currency.
        public decimal? OriginalAmount { get; init; }

        public DateTime? ReversedAt { get; init; }
    }
}
