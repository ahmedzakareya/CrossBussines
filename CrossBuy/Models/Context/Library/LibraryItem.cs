namespace CrossBuy.Models.Context.Library
{
    // A single node in the company document library (SharePoint-style). One row is either a FOLDER
    // (IsFolder=true, no file fields) or a FILE (IsFolder=false, StoredPath/ContentType/Size set).
    // Hierarchy: ParentId == null → library root. Company-scoped shared library. Soft-deleted via DeletedAt.
    public class LibraryItem : BaseEntity
    {
        public int Id { get; set; }
        public int CompanyID { get; set; }
        public int? ParentId { get; set; }          // null = root
        public bool IsFolder { get; set; }
        public string Name { get; set; } = "";
        public string? NameEn { get; set; }         // English twin — shown when UI language != Arabic (falls back to Name)
        public string? StoredPath { get; set; }     // file only: web path e.g. /uploads/library/<company>/<guid>.ext
        public string? ContentType { get; set; }    // file only
        public long Size { get; set; }              // file only, bytes
        public int OwnerEmpId { get; set; }         // uploader/creator
        public DateTime? DeletedAt { get; set; }
    }
}
