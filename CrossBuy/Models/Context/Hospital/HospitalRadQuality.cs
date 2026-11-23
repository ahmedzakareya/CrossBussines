using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H243 — radiology beyond the free-text report:
	//   · The STRUCTURED CATEGORY of a study by the published ACR systems (BI-RADS, LI-RADS, Lung-RADS, TI-RADS, PI-RADS),
	//     with the follow-up the category calls for. A FOLLOW-UP is tracked until it is done (the later study that answers
	//     it), or closed with a reason — so a recommended scan is never silently lost.
	//   · The RADIATION DOSE of an exam (CT: CTDIvol and DLP; radiography/fluoroscopy: DAP), its effective-dose estimate
	//     (DLP × the region's conversion factor) and the patient's cumulative dose. Doses above the facility's reference
	//     level are flagged.
	// deploy/sql/hospital_h243_rad_quality.sql builds the tables.
	// ==========================================================================================
	public static class HospitalRadFollowUpStatus
	{
		public const string Open = "Open", Done = "Done", Closed = "Closed";
		public static readonly IReadOnlyList<string> All = new[] { Open, Done, Closed };
	}

	public class HospitalRadFollowUp
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int OrderId { get; set; }
		public int PatientId { get; set; }
		public string System { get; set; } = "";
		public string Category { get; set; } = "";
		public string? Recommendation { get; set; }
		/// Null when the category asks for no follow-up (the category is still recorded).
		public DateTime? DueDate { get; set; }
		public string Status { get; set; } = HospitalRadFollowUpStatus.Open;
		public int? AnsweredByOrderId { get; set; }
		public string? Note { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? ClosedAt { get; set; }
	}

	public class HospitalRadDose
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int OrderId { get; set; }
		public int PatientId { get; set; }
		public string Region { get; set; } = "";
		public decimal? CtdiVol { get; set; }
		public decimal? Dlp { get; set; }
		public decimal? Dap { get; set; }
		public decimal? EffectiveMsv { get; set; }
		public bool AboveReference { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalRadQualityModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalRadFollowUp>(e =>
			{
				e.ToTable("HospitalRadFollowUps");
				e.Property(x => x.System).HasMaxLength(12); e.Property(x => x.Category).HasMaxLength(10); e.Property(x => x.Recommendation).HasMaxLength(300);
				e.Property(x => x.DueDate).HasColumnType("date"); e.Property(x => x.Status).HasMaxLength(8); e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.OrderId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Status, x.DueDate });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalRadDose>(e =>
			{
				e.ToTable("HospitalRadDoses");
				e.Property(x => x.Region).HasMaxLength(15);
				e.Property(x => x.CtdiVol).HasPrecision(9, 2); e.Property(x => x.Dlp).HasPrecision(9, 1); e.Property(x => x.Dap).HasPrecision(9, 2); e.Property(x => x.EffectiveMsv).HasPrecision(7, 2);
				e.HasIndex(x => new { x.CompanyId, x.OrderId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
