using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H162 — insurance for THIS visit. A policy on file is not a decision: the desk verifies the cover for the
	// visit (insured under a named policy, or self-pay), says how it verified it (the card seen, the insurer's portal, a
	// phone call, an approval letter, the patient's own word) with the insurer's reference, and the estimate the
	// patient was told. Each verification is kept; the latest one is the visit's decision.
	// ==========================================================================================
	public static class HospitalCoverageDecision
	{
		public const string Insured = "Insured", SelfPay = "SelfPay";
		public static readonly IReadOnlyList<string> All = new[] { Insured, SelfPay };
	}

	public static class HospitalCoverageSource
	{
		public const string Card = "Card", Portal = "Portal", Phone = "Phone", Letter = "Letter", Patient = "Patient";
		public static readonly IReadOnlyList<string> All = new[] { Card, Portal, Phone, Letter, Patient };
	}

	public class HospitalVisitCoverage
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AppointmentId { get; set; }
		public int PatientId { get; set; }
		public string Decision { get; set; } = HospitalCoverageDecision.Insured;
		public int? PolicyId { get; set; }
		public int? PayerId { get; set; }
		public string Source { get; set; } = HospitalCoverageSource.Card;
		/// The insurer's reference for the check (an eligibility number, a call reference).
		public string? Reference { get; set; }
		public DateTime VerifiedAt { get; set; }
		public int? VerifiedByEmployeeId { get; set; }
		/// What the visit was estimated at, and the patient's share of it, when the desk verified.
		public decimal EstimatedTotal { get; set; }
		public decimal EstimatedShare { get; set; }
		public string? Note { get; set; }
	}

	public static class HospitalVisitCoverageModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalVisitCoverage>(e =>
			{
				e.ToTable("HospitalVisitCoverages");   // no DbSet: it names its table
				e.Property(x => x.Decision).HasMaxLength(10);
				e.Property(x => x.Source).HasMaxLength(10);
				e.Property(x => x.Reference).HasMaxLength(60);
				e.Property(x => x.EstimatedTotal).HasColumnType("decimal(18,3)");
				e.Property(x => x.EstimatedShare).HasColumnType("decimal(18,3)");
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.AppointmentId });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
