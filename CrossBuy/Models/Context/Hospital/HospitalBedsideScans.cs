using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	public static class HospitalBedsideScanKind
	{
		public const string Medication = "Medication";
		public const string Specimen = "Specimen";
		public const string Blood = "Blood";
		public const string Baby = "Baby";
		public static readonly IReadOnlyList<string> All = new[] { Medication, Specimen, Blood, Baby };
	}

	public static class HospitalBedsideScanOutcome
	{
		/// Both codes scanned and both right.
		public const string Matched = "Matched";
		/// Done without the scan, with the reason written (a torn band, an unreadable pack) — it counts against compliance.
		public const string Override = "Override";
		/// A scan that stopped the step: the wrong patient, the wrong medicine, an expired or recalled pack.
		public const string Refused = "Refused";
		public static readonly IReadOnlyList<string> All = new[] { Matched, Override, Refused };
	}

	// HOSPITAL H205 — every bedside scan: what was scanned, against which patient and which step (a dose, a specimen,
	// a blood unit, a baby's band), and what came of it — including the scans that STOPPED the step, which are the
	// near-misses the safety committee reads. Compliance (HIMSS: 95 %) is Matched over Matched + Override.
	public class HospitalBedsideScan
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Kind { get; set; } = HospitalBedsideScanKind.Medication;
		public int PatientId { get; set; }
		public int? AdmissionId { get; set; }
		/// The step it guarded: the medication order (Medication), the lab order (Specimen), the blood issue (Blood), the birth (Baby).
		public int? RefId { get; set; }
		/// The record the step became, when it was done (the MAR entry of a dose).
		public int? DoneId { get; set; }
		public string? Wristband { get; set; }
		public string? Item { get; set; }
		public string? Gtin { get; set; }
		public string? BatchNo { get; set; }
		public DateTime? Expiry { get; set; }
		public string Outcome { get; set; } = HospitalBedsideScanOutcome.Matched;
		/// Refused: the refusal key (with its values, tab-separated). Override: the nurse's reason.
		public string? Detail { get; set; }
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalBedsideScanModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalBedsideScan>(e =>
			{
				e.ToTable("HospitalBedsideScans");   // no DbSet: it names its table
				e.Property(x => x.Kind).HasMaxLength(12);
				e.Property(x => x.Wristband).HasMaxLength(60);
				e.Property(x => x.Item).HasMaxLength(120);
				e.Property(x => x.Gtin).HasMaxLength(14);
				e.Property(x => x.BatchNo).HasMaxLength(20);
				e.Property(x => x.Outcome).HasMaxLength(10);
				e.Property(x => x.Detail).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.Kind, x.At });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
