using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H259 — STRUCTURED RADIOLOGY REPORTING and RADIOLOGIST PEER REVIEW.
	//   · A report TEMPLATE (per modality, or any) lists its sections in Arabic and English and which are required.
	//     Applying it to a study writes the section headings into the report and records that the study uses it; the
	//     report is then not signed while a required section is empty.
	//   · PEER REVIEW (RADPEER): a second radiologist scores a signed report — 1 agree; 2 a discrepancy not
	//     ordinarily expected to be caught; 3 one that should be caught most of the time — and marks whether it is
	//     clinically significant. Nobody reviews their own report, and each reviewer scores a study once.
	// deploy/sql/hospital_h259_rad_reporting.sql builds the tables.
	// ==========================================================================================
	public class HospitalRadTemplate
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		/// Null: any modality.
		public string? Modality { get; set; }
		/// One section per line: "Heading|HeadingEn|1" (1 = required).
		public string Sections { get; set; } = "";
		public bool Active { get; set; } = true;
		public DateTime UpdatedAt { get; set; }
	}

	public class HospitalRadTemplateUse
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int OrderId { get; set; }
		public int TemplateId { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public class HospitalRadPeerReview
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int OrderId { get; set; }
		public int RadiologistEmployeeId { get; set; }
		public int ReviewerEmployeeId { get; set; }
		/// RADPEER 1, 2 or 3.
		public int Score { get; set; }
		public bool Significant { get; set; }
		public string? Note { get; set; }
		public DateTime At { get; set; }
	}

	public static class HospitalRadReportingModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalRadTemplate>(e =>
			{
				e.ToTable("HospitalRadTemplates");
				e.Property(x => x.Code).HasMaxLength(20); e.Property(x => x.Name).HasMaxLength(100); e.Property(x => x.NameEn).HasMaxLength(100);
				e.Property(x => x.Modality).HasMaxLength(4); e.Property(x => x.Sections).HasMaxLength(2000);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalRadTemplateUse>(e =>
			{
				e.ToTable("HospitalRadTemplateUses");
				e.HasIndex(x => new { x.CompanyId, x.OrderId }).IsUnique();
			});
			b.Entity<HospitalRadPeerReview>(e =>
			{
				e.ToTable("HospitalRadPeerReviews");
				e.Property(x => x.Note).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.OrderId, x.ReviewerEmployeeId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.RadiologistEmployeeId, x.At });
			});
		}
	}
}
