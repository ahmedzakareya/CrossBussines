using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H136 — the pharmacist's review, as the leading systems run it (Epic Willow's verification queue, ASHP practice).
	//   · PRIORITY: a prescription is STAT, urgent or routine. Each has a target for the pharmacist's review (30 / 60 /
	//     240 minutes from prescribing). The queue works the most urgent first, and a clock shows what is late. Until the
	//     pharmacist sets a priority, an emergency prescription counts as urgent.
	//   · REJECT: the pharmacist may refuse a prescription, with the reason; it never reaches dispensing.
	//   · INTERVENTIONS: every clinical problem the pharmacist raises is logged:
	//       - which medicine, what kind of problem, how serious;
	//       - what was recommended, and whether the prescriber accepted it;
	//       - the minutes it took.
	//     This is the record that proves the pharmacy's clinical value (accepted interventions, time spent).
	// CrossBuy/deploy/sql/hospital_h136_pharmacist_review.sql adds the columns and the table.
	// ==========================================================================================

	public static class HospitalRxPriority
	{
		public const string Stat = "Stat", Urgent = "Urgent", Routine = "Routine";
		public static readonly IReadOnlyList<string> All = new[] { Stat, Urgent, Routine };

		/// The pharmacist's set priority, else what the source implies: an emergency prescription is urgent.
		public static string Effective(string? priority, string source)
			=> All.Contains(priority ?? "") ? priority! : source == HospitalDispenseSource.Emergency ? Urgent : Routine;

		/// Minutes from prescribing to the pharmacist's review.
		public static int TargetMinutes(string priority) => priority switch { Stat => 30, Urgent => 60, _ => 240 };

		public static int Rank(string priority) => priority switch { Stat => 0, Urgent => 1, _ => 2 };
	}

	public static class HospitalInterventionCategory
	{
		public const string Dose = "Dose", Renal = "Renal", Allergy = "Allergy", Interaction = "Interaction", Duplicate = "Duplicate",
			Substitution = "Substitution", NoIndication = "NoIndication", Untreated = "Untreated", Monitoring = "Monitoring", Route = "Route", Other = "Other";
		public static readonly IReadOnlyList<string> All = new[] { Dose, Renal, Allergy, Interaction, Duplicate, Substitution, NoIndication, Untreated, Monitoring, Route, Other };
	}

	public static class HospitalInterventionOutcome
	{
		public const string Pending = "Pending", Accepted = "Accepted", Declined = "Declined";
		public static readonly IReadOnlyList<string> All = new[] { Pending, Accepted, Declined };
	}

	public static class HospitalInterventionSeverity
	{
		public const string Low = "Low", Moderate = "Moderate", High = "High";
		public static readonly IReadOnlyList<string> All = new[] { High, Moderate, Low };
	}

	public class HospitalPharmacyIntervention
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int DispenseId { get; set; }
		public int PatientId { get; set; }
		/// The prescription line it is about (null: the prescription as a whole), and the medicine's name as written.
		public int? LineId { get; set; }
		public string? Drug { get; set; }
		public string Category { get; set; } = HospitalInterventionCategory.Other;
		public string Severity { get; set; } = HospitalInterventionSeverity.Moderate;
		public string Problem { get; set; } = "";
		public string Recommendation { get; set; } = "";
		public string Outcome { get; set; } = HospitalInterventionOutcome.Pending;
		public int? Minutes { get; set; }
		public DateTime RecordedAt { get; set; }
		public int? RecordedByEmployeeId { get; set; }
		public DateTime? OutcomeAt { get; set; }
		public int? OutcomeByEmployeeId { get; set; }
	}

	public static class HospitalPharmacyReviewModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDispense>(e =>
			{
				e.Property(x => x.Priority).HasMaxLength(10);
				e.Property(x => x.RejectReason).HasMaxLength(400);
			});
			b.Entity<HospitalPharmacyIntervention>(e =>
			{
				e.ToTable("HospitalPharmacyInterventions");   // no DbSet: it names its table
				e.Property(x => x.Drug).HasMaxLength(200);
				e.Property(x => x.Category).HasMaxLength(20);
				e.Property(x => x.Severity).HasMaxLength(10);
				e.Property(x => x.Problem).HasMaxLength(400);
				e.Property(x => x.Recommendation).HasMaxLength(400);
				e.Property(x => x.Outcome).HasMaxLength(10);
				e.HasIndex(x => new { x.CompanyId, x.DispenseId });
				e.HasIndex(x => new { x.CompanyId, x.RecordedAt });
			});
		}
	}
}
