namespace CrossBuy.Models.Context.Reporting
{
    // ============================================================================================
    // A STORED REPORTING IMAGE — company logo, signature, stamp or any permitted picture.
    //
    // WHY A REPORTING TABLE AND NOT AN EXISTING ONE (the §0 justification, approved by the owner):
    //
    //   · Attachments / FileManager belong to another module. Their tenancy and permission semantics are that
    //     module's to change, and a Reporting element that resolved through them would inherit a security
    //     model Reporting does not own — the exact coupling ADR-037 keeps out of the reporting platform.
    //   · ReportArchiveEntries is the closest existing Reporting table and was considered. It fits mechanically
    //     (CompanyID, ContentType, StoredPath, ContentHash, DeletedAt) and badly semantically: RunId,
    //     TemplateId and RetainUntil are meaningless for an asset, and a reader a year from now would have to
    //     be told why an "archived report artifact" row is a company logo.
    //
    // So: one small table, inside the Reporting namespace the slice already owns, carrying only what an asset
    // is. It follows the platform's feature-module conventions exactly — CompanyID on every row, CreatedBy/At,
    // soft-delete DeletedAt.
    //
    // THE BYTES ARE NOT IN THE ROW. StoredPath names a file under the Reporting asset root, which the store
    // owns and the browser never sees. The element in a saved layout references this row's Id and nothing else,
    // which is what makes "no arbitrary path, no arbitrary URL" a property of the CONTRACT rather than a rule
    // somebody has to remember to check.
    // ============================================================================================
    public class ReportAsset
    {
        public int Id { get; set; }

        // The tenant. Set from the resolved BusinessContext at upload; never from a request.
        public int CompanyID { get; set; }

        // 0 Custom · 1 CompanyLogo · 2 Signature · 3 Stamp. A ROLE, not a position — §5 is explicit that a
        // logo may be placed anywhere, so this only lets a template default sensibly and lets an operator find
        // "the company stamp" without opening every image.
        public int Role { get; set; }

        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long Length { get; set; }

        // Relative to the configured asset root. Never absolute, never browser-supplied.
        public string StoredPath { get; set; } = "";

        // SHA-256 of the bytes. Lets a re-upload of the same image reuse one row, and lets support answer
        // "is this the logo we approved" without opening the file.
        public string? ContentHash { get; set; }

        public string? Title { get; set; }

        public DateTime? CreatedAt { get; set; }
        public int? CreatedBy { get; set; }

        // Soft delete, matching the platform's feature-module convention. An asset a layout still references
        // must stay resolvable so an archived document can still be explained.
        public DateTime? DeletedAt { get; set; }
    }
}
