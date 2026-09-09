namespace CrossBuy.Models
{
    // Generic read-only document viewer model — used by every inventory document Details page
    public class DocDetailVm
    {
        public string Title { get; set; } = "";
        public string TitleEn { get; set; } = "";
        public string DocNo { get; set; } = "";
        public string DateStr { get; set; } = "";
        public string Status { get; set; } = "Posted";
        public string BackAction { get; set; } = "Index";          // list action to return to
        public string BackLabel { get; set; } = "";
        public string BackLabelEn { get; set; } = "";
        public int? JournalEntryId { get; set; }                   // when set → link to the GL entry

        // ---- PRINTING -----------------------------------------------------------------------------
        //
        // EIGHT DOCUMENTS render through DocumentDetails.cshtml — purchase and sales orders, receipts,
        // deliveries, transfers, counts, write-offs, landed costs. Putting the print control in that one
        // view means each of them gets it by naming itself here, and none of them grows print markup of
        // its own to drift from the others.
        //
        // A document that has no report yet simply leaves ScreenKey null and renders no control, which
        // is the honest state: a print button that opens nothing is worse than its absence.
        public string? PrintScreenKey { get; set; }
        public int? DocumentId { get; set; }
        public List<DocKv> Header { get; set; } = new();           // header key/value pairs
        public List<DocCol> Columns { get; set; } = new();         // line table columns
        public List<List<string>> Rows { get; set; } = new();      // line rows (cells line up with Columns)
        public List<DocKv> Totals { get; set; } = new();           // footer totals
        public bool Danger { get; set; }                           // tint (e.g. write-off)
    }

    public class DocKv { public string Label = ""; public string LabelEn = ""; public string Value = ""; }
    public class DocCol { public string Label = ""; public string LabelEn = ""; public bool Num; }   // Num = right-aligned numeric
}
