namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // WHAT A DOCUMENT SCREEN NEEDS TO OFFER ITS PRINT LAYOUTS.
    //
    // A voucher has more than one template — the plain one, and the one with the ministry's mark on
    // the right and the company's on the left — and which of them exists is DATA, authored in Report
    // Studio. A screen that hardcodes one of them, or lists them in its own markup, is out of date the
    // first time somebody adds a layout, and there is no build that would catch it.
    //
    // So the screen says only WHICH REPORT and FOR WHAT, and _ReportPrintMenu reads the rest.
    // ============================================================================================
    public sealed class ReportPrintMenuModel
    {
        // The registered report the screen prints through — e.g. Accounting.JournalVoucher.
        public required string ReportCode { get; init; }

        // The parameters that pin the report to THIS document: { ["JournalId"] = 15158 }. Kept as a
        // dictionary rather than a typed id because the next screen to want this menu prints an invoice,
        // and it should not have to change this class to do it.
        public required IReadOnlyDictionary<string, string> Parameters { get; init; }

        // Shown as the dialog's title. The document's own name reads better than the report's: a person
        // pressing Print on entry JV-2026-003615 is looking for that number, not for "Journal voucher".
        public string? DocumentTitle { get; init; }
    }
}
