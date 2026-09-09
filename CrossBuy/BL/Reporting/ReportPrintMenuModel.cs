namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // WHAT A DOCUMENT SCREEN SAYS TO GET ITS PRINT CONTROL.
    //
    // Only two things: WHICH SCREEN it is, and WHICH DOCUMENT is open. Everything else — which reports
    // that screen prints, what each one calls its id parameter, which layouts exist — is looked up.
    //
    // An earlier version took a report CODE. That bound the screen's MARKUP to one report, so printing
    // a second document from the same screen meant editing a view, building and deploying, for a report
    // the platform already knew how to render. The screen is bound to its reports in
    // ReportScreenBindings now, which is one line per report and no markup at all.
    // ============================================================================================
    public sealed class ReportPrintMenuModel
    {
        // The screen, from ReportScreenKeys — a constant rather than a literal, so a typo is a compile
        // error instead of a print control that silently renders nothing.
        public required string ScreenKey { get; init; }

        // The document on screen, or NULL on a list — which prints a register of the rows it is showing
        // rather than one document. Kept as a STRING because it is going into a query string either way.
        public string? DocumentId { get; init; }

        // Anything else a bound report needs pinned. Empty for the ordinary case; present because a
        // report that takes a date as well as an id should not require a new model to be usable.
        public IReadOnlyDictionary<string, string> ExtraParameters { get; init; }
            = new Dictionary<string, string>(StringComparer.Ordinal);

        // Shown as the dialog's title. The document's own name reads better than the report's: a person
        // pressing Print on entry JV-2026-003615 is looking for that number, not for "Journal voucher".
        public string? DocumentTitle { get; init; }
    }
}
