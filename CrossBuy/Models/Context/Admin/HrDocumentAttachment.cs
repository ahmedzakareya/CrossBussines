namespace CrossBuy.Models.Context.Admin
{
    // One uploaded file linked to an employment contract or an employee document (HR-5).
    // A single record (contract/document) can own many attachments.
    public class HrDocumentAttachment
    {
        public int ID { get; set; }
        public int CompanyID { get; set; }
        public string OwnerKind { get; set; } = "Document";  // Contract | Document
        public int OwnerID { get; set; }                     // EmploymentContract.ID or EmployeeDocument.ID
        public string FilePath { get; set; } = "";           // web path under /uploads/hr-docs
        public string? FileName { get; set; }                // original file name for display
        public DateTime? UploadedAt { get; set; }
    }
}
