using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Admin
{
	// Recruitment layer (R0). Additive HR sub-module: a hiring pipeline that, on acceptance, converts a JobApplication into
	// a real Employee via the EXISTING EmployeeService (no parallel creation path). EmployeeRequest (ESS letters/permissions)
	// is untouched. Documents reuse the existing /uploads/hr-docs vault and transfer to EmployeeDocument on hire.

	// A managed catalog of required document types (global), each mandatory or optional. Drives the per-application checklist.
	public class RequiredDocumentType
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";       // Arabic
		public string? NameEn { get; set; }
		public bool IsMandatory { get; set; } = true;
		public int SortOrder { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
	}

	// A hiring request / job application (المتقدّم). Pipeline: New → Review → Interview → Accepted/Rejected → Hired.
	public class JobApplication
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ApplicationNo { get; set; }        // display sequence per company

		// applicant identity
		public string FirstName { get; set; } = "";
		public string LastName { get; set; } = "";
		public string? FullName { get; set; }         // Arabic full name (falls back to First+Last)
		public string? FullNameEn { get; set; }
		public string? Email { get; set; }
		public string? PhoneNumber { get; set; }
		public string? Address { get; set; }
		public string? PhotoPath { get; set; }        // optional applicant photo (/uploads/applicants)
		public DateTime? DateOfBirth { get; set; }
		public string? Gender { get; set; }
		public string? MaritalStatus { get; set; }
		public int? CountryID { get; set; }

		// position applied for (mirrors the fields the employee wizard needs)
		public int? JobTitleID { get; set; }
		public int? BranchID { get; set; }
		public int? EmpCompanyID { get; set; }
		public int? DepartmentID { get; set; }        // → Hierarchical org node
		public string? EmploymentType { get; set; }
		public decimal? ExpectedSalary { get; set; }
		public string? Source { get; set; }           // how they applied (optional)

		// pipeline
		public string Status { get; set; } = "New";   // New | Review | Interview | Accepted | Rejected | Hired
		public DateTime? AppliedAt { get; set; }
		public DateTime? ReviewedAt { get; set; }
		public DateTime? InterviewAt { get; set; }
		public DateTime? DecisionAt { get; set; }
		public DateTime? HiredAt { get; set; }
		public string? DecisionNote { get; set; }

		public int? HiredEmployeeID { get; set; }      // set on conversion → Employee

		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
		public int? CreatedBy { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedBy { get; set; }

		public List<ApplicationDocument> Documents { get; set; } = new();
	}

	// A document attached to an application. Mirrors EmployeeDocument so it transfers cleanly on hire.
	public class ApplicationDocument
	{
		public int ID { get; set; }
		public int ApplicationID { get; set; }
		public int? RequiredDocumentTypeID { get; set; }   // → catalog (null = ad-hoc/Other)
		public string? DocType { get; set; }               // snapshot label (for carry-over to EmployeeDocument.DocType)
		public string? FilePath { get; set; }
		public string? FileName { get; set; }
		public string? DocNumber { get; set; }
		public DateTime? IssueDate { get; set; }
		public DateTime? ExpiryDate { get; set; }
		public DateTime? UploadedAt { get; set; }

		[ForeignKey(nameof(ApplicationID))]
		public JobApplication? Application { get; set; }
	}
}
