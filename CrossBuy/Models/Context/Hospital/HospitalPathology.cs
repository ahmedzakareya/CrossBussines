using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	public static class HospitalPathStatus
	{
		public const string Requested = "Requested";
		public const string Received = "Received";
		public const string Grossed = "Grossed";
		public const string Reported = "Reported";
		public const string Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Requested, Received, Grossed, Reported, Cancelled };
	}

	public static class HospitalPathSource
	{
		public const string Clinic = "Clinic";
		public const string Theatre = "Theatre";
		public const string Endoscopy = "Endoscopy";
		public const string Ward = "Ward";
		public static readonly IReadOnlyList<string> All = new[] { Clinic, Theatre, Endoscopy, Ward };
	}

	// HOSPITAL H216 — an anatomic pathology case: a tissue specimen from the clinic, theatre, endoscopy or a ward, received
	// in the laboratory, described and cut (gross), read under the microscope and signed out by the pathologist. A
	// signed report is never edited: a later finding is an amendment beside it (HospitalPathAmendment).
	public class HospitalPathCase
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string CaseNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? EncounterId { get; set; }
		public string Source { get; set; } = HospitalPathSource.Clinic;
		public int? SourceRefId { get; set; }
		public string Specimen { get; set; } = "";
		public string? Site { get; set; }
		public string? ClinicalHistory { get; set; }
		public int Containers { get; set; } = 1;
		public string Status { get; set; } = HospitalPathStatus.Requested;
		public DateTime RequestedAt { get; set; }
		public int? RequestedByEmployeeId { get; set; }
		public DateTime? ReceivedAt { get; set; }
		public int? ReceivedByEmployeeId { get; set; }
		public string? Gross { get; set; }
		public int? Blocks { get; set; }
		public DateTime? GrossedAt { get; set; }
		public int? GrossedByEmployeeId { get; set; }
		public string? Microscopic { get; set; }
		public string? Diagnosis { get; set; }
		public bool Malignant { get; set; }
		public DateTime? ReportedAt { get; set; }
		public int? ReportedByEmployeeId { get; set; }
		public string? CancelReason { get; set; }
		/// H222: the structured cancer report (synoptic) of a malignant diagnosis.
		public string? TumourType { get; set; }
		public string? Grade { get; set; }
		public decimal? TumourSizeMm { get; set; }
		public string? Margins { get; set; }
		public decimal? MarginMm { get; set; }
		public string? Lvi { get; set; }
		public string? PT { get; set; }
		public string? PN { get; set; }
		public int? NodesExamined { get; set; }
		public int? NodesPositive { get; set; }
	}

	public static class HospitalPathMargins
	{
		public const string Clear = "Clear";
		public const string Close = "Close";
		public const string Involved = "Involved";
		public static readonly IReadOnlyList<string> All = new[] { Clear, Close, Involved };
	}

	public static class HospitalPathLvi
	{
		public const string Absent = "Absent";
		public const string Present = "Present";
		public const string Indeterminate = "Indeterminate";
		public static readonly IReadOnlyList<string> All = new[] { Absent, Present, Indeterminate };
	}

	public class HospitalPathAmendment
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int CaseId { get; set; }
		public string Text { get; set; } = "";
		public string Reason { get; set; } = "";
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalPathologyModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPathCase>(e =>
			{
				e.ToTable("HospitalPathCases");
				e.Property(x => x.CaseNo).HasMaxLength(20);
				e.Property(x => x.Source).HasMaxLength(10);
				e.Property(x => x.Specimen).HasMaxLength(200);
				e.Property(x => x.Site).HasMaxLength(200);
				e.Property(x => x.ClinicalHistory).HasMaxLength(1000);
				e.Property(x => x.Status).HasMaxLength(10).IsConcurrencyToken();   // a step lands only from the state it was read in
				e.Property(x => x.Gross).HasMaxLength(2000);
				e.Property(x => x.Microscopic).HasMaxLength(4000);
				e.Property(x => x.Diagnosis).HasMaxLength(1000);
				e.Property(x => x.CancelReason).HasMaxLength(300);
				e.Property(x => x.TumourType).HasMaxLength(200);   // H222
				e.Property(x => x.Grade).HasMaxLength(20);
				e.Property(x => x.TumourSizeMm).HasPrecision(6, 1);
				e.Property(x => x.Margins).HasMaxLength(10);
				e.Property(x => x.MarginMm).HasPrecision(5, 1);
				e.Property(x => x.Lvi).HasMaxLength(15);
				e.Property(x => x.PT).HasMaxLength(10);
				e.Property(x => x.PN).HasMaxLength(10);
				e.HasIndex(x => new { x.CompanyId, x.CaseNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalPathAmendment>(e =>
			{
				e.ToTable("HospitalPathAmendments");
				e.Property(x => x.Text).HasMaxLength(2000);
				e.Property(x => x.Reason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.CaseId });
			});
		}
	}
}
