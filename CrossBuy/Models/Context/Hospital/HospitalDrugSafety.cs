using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H73 — DRUG INTERACTIONS AND DOSE LIMITS (phase 1 "patient safety" of the 2026-11-09 gap research). The
	// hospital's own knowledge base, kept by the pharmacy: pairs of medicines that interact (contraindicated, major or
	// moderate, the effect said in Arabic and English) and the highest single and daily dose of a medicine by route.
	// A licensed knowledge base (First Databank, Lexicomp…) is an integration and waits for the client; this one is the
	// hospital's and works today. A doctor's order that meets a contraindication, a major interaction or a dose above its
	// limit stops until the doctor writes why it is still right — and every such decision is kept (HospitalAlertOverrides).
	// CrossBuy/deploy/sql/hospital_h73_drug_safety.sql builds the tables.
	// ==========================================================================================
	public static class HospitalInteractionSeverity
	{
		public const string Contraindicated = "Contra";
		public const string Major = "Major";
		public const string Moderate = "Moderate";
		public static readonly IReadOnlyList<string> All = new[] { Contraindicated, Major, Moderate };
		/// Stops an order until a reason is written.
		public static bool Blocks(string s) => s is Contraindicated or Major;
	}

	public static class HospitalDoseUnit
	{
		public const string Mg = "mg";
		public const string Mcg = "mcg";
		public const string G = "g";
		public const string Units = "units";
		public const string Ml = "ml";
		public static readonly IReadOnlyList<string> All = new[] { Mg, Mcg, G, Units, Ml };
	}

	public static class HospitalAlertKind
	{
		public const string Interaction = "Interaction";
		public const string Dose = "Dose";
		public const string Allergy = "Allergy";
	}

	public class HospitalDrugInteraction
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		/// The two medicines, as words a drug's name contains ("warfarin", "aspirin"); stored lower-case, A before B.
		public string DrugA { get; set; } = "";
		public string DrugB { get; set; } = "";
		public string Severity { get; set; } = HospitalInteractionSeverity.Moderate;
		public string EffectAr { get; set; } = "";
		public string EffectEn { get; set; } = "";
		public bool IsActive { get; set; } = true;
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public class HospitalDoseLimit
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		/// The medicine, as a word its name contains; lower-case.
		public string Drug { get; set; } = "";
		/// A route (HospitalMedRoute) or null for any.
		public string? Route { get; set; }
		public string Unit { get; set; } = HospitalDoseUnit.Mg;
		public decimal? MaxSingle { get; set; }
		public decimal? MaxDaily { get; set; }
		public string? Note { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	/// A doctor went ahead past a stopping alert, and why.
	public class HospitalAlertOverride
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public int? AdmissionId { get; set; }
		/// What was ordered ("MedOrder") and its id.
		public string SourceType { get; set; } = "MedOrder";
		public int SourceId { get; set; }
		public string Kind { get; set; } = HospitalAlertKind.Interaction;
		/// The alert as it was shown ("warfarin + aspirin — Major").
		public string Detail { get; set; } = "";
		public string Reason { get; set; } = "";
		public DateTime At { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalDrugSafetyModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDrugInteraction>(e =>
			{
				e.ToTable("HospitalDrugInteractions");   // no DbSet: each names its table
				e.Property(x => x.DrugA).HasMaxLength(80);
				e.Property(x => x.DrugB).HasMaxLength(80);
				e.Property(x => x.Severity).HasMaxLength(10);
				e.Property(x => x.EffectAr).HasMaxLength(400);
				e.Property(x => x.EffectEn).HasMaxLength(400);
				e.HasIndex(x => new { x.CompanyId, x.DrugA, x.DrugB }).IsUnique();
			});
			b.Entity<HospitalDoseLimit>(e =>
			{
				e.ToTable("HospitalDoseLimits");
				e.Property(x => x.Drug).HasMaxLength(80);
				e.Property(x => x.Route).HasMaxLength(10);
				e.Property(x => x.Unit).HasMaxLength(10);
				e.Property(x => x.MaxSingle).HasPrecision(12, 3);
				e.Property(x => x.MaxDaily).HasPrecision(12, 3);
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.Drug });
			});
			b.Entity<HospitalAlertOverride>(e =>
			{
				e.ToTable("HospitalAlertOverrides");
				e.Property(x => x.SourceType).HasMaxLength(20);
				e.Property(x => x.Kind).HasMaxLength(12);
				e.Property(x => x.Detail).HasMaxLength(300);
				e.Property(x => x.Reason).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
