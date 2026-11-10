using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H80 — CHART COMPLETION (health information management). Phase 2 of the 2026-11-09 gap research: the
	// medical records department checks every discharged stay's chart for what is missing — the discharge summary and its
	// signature, the discharge medication reconciliation, consultations still open, infections never decided, devices never
	// recorded out, care plans left open — and who owes each item. The deficiencies are computed from the chart itself,
	// never typed; the department signs a chart complete only when none is left. A chart still incomplete 14 days after
	// discharge is delinquent. CrossBuy/deploy/sql/hospital_h80_chart_review.sql builds the sign-off table.
	// ==========================================================================================
	public static class HospitalChartDeficiency
	{
		public const string NoSummary = "NoSummary";
		public const string SummaryUnsigned = "SummaryUnsigned";
		public const string MedRecOpen = "MedRecOpen";
		public const string ConsultOpen = "ConsultOpen";
		public const string InfectionUndecided = "InfectionUndecided";
		public const string DeviceInPlace = "DeviceInPlace";
		public const string CarePlanOpen = "CarePlanOpen";
		public static readonly IReadOnlyList<string> All = new[] { NoSummary, SummaryUnsigned, MedRecOpen, ConsultOpen, InfectionUndecided, DeviceInPlace, CarePlanOpen };

		/// Who owes it: the attending doctor, the specialty asked, infection control, or the ward.
		public static string OwnerOf(string d) => d switch
		{
			NoSummary or SummaryUnsigned or MedRecOpen => "Attending",
			ConsultOpen => "Consultant",
			InfectionUndecided => "InfectionControl",
			_ => "Ward",
		};
	}

	/// The records department's sign-off: the chart was checked and found complete.
	public class HospitalChartReview
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public DateTime ReviewedAt { get; set; }
		public int? ReviewedByEmployeeId { get; set; }
		public string? Note { get; set; }
	}

	public static class HospitalChartReviewModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalChartReview>(e =>
			{
				e.ToTable("HospitalChartReviews");   // no DbSet: it names its table
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId }).IsUnique();
			});
		}
	}
}
