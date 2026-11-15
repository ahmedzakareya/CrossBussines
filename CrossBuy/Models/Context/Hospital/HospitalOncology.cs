using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H88 — ONCOLOGY: chemotherapy (phase 4, after dialysis).
	//   * A REGIMEN is the hospital's own protocol library: a name, the days between cycles, the most cycles, and its
	//     drugs, each with a dose on a basis (mg/m², mg/kg, a flat mg, or a carboplatin AUC), a route, the days of the
	//     cycle it is given on, and — for the anthracyclines and their kind — a lifetime limit in mg/m². The deploy ships
	//     NO clinical content: the pharmacy and the oncologists enter their protocols (as with H73's drug-safety table).
	//   * A COURSE is a patient on a regimen: the diagnosis, the stage, the intent, the height and the weight at the start.
	//   * A CYCLE is given or held. When given it freezes everything it was given on: the weight, the body surface area
	//     (Mosteller), the counts that cleared it (neutrophils, platelets, creatinine), the dose reduction, the toxicity
	//     seen since the last cycle, an override reason when a check was overruled, and each drug's computed dose.
	// CrossBuy/deploy/sql/hospital_h88_oncology.sql builds the tables.
	// ==========================================================================================
	public static class HospitalChemoBasis
	{
		public const string PerM2 = "MgM2";
		public const string PerKg = "MgKg";
		public const string Flat = "Mg";
		public const string Auc = "AUC";
		public static readonly IReadOnlyList<string> All = new[] { PerM2, PerKg, Flat, Auc };
	}

	public static class HospitalChemoRoute
	{
		public const string IV = "IV";
		public const string Oral = "PO";
		public const string SC = "SC";
		public const string IM = "IM";
		public static readonly IReadOnlyList<string> All = new[] { IV, Oral, SC, IM };
	}

	public static class HospitalChemoIntent
	{
		public const string Curative = "Curative";
		public const string Adjuvant = "Adjuvant";
		public const string Neoadjuvant = "Neoadjuvant";
		public const string Palliative = "Palliative";
		public static readonly IReadOnlyList<string> All = new[] { Curative, Adjuvant, Neoadjuvant, Palliative };
	}

	public static class HospitalChemoCourseStatus
	{
		public const string Active = "Active";
		public const string Completed = "Completed";
		public const string Stopped = "Stopped";
	}

	public static class HospitalChemoCycleStatus
	{
		public const string Given = "Given";
		public const string Held = "Held";
	}

	public class HospitalChemoRegimen
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int CycleDays { get; set; }
		public int MaxCycles { get; set; }
		public string? Note { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
	}

	public class HospitalChemoRegimenDrug
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int RegimenId { get; set; }
		public int SortOrder { get; set; }
		public string DrugName { get; set; } = "";
		public decimal Dose { get; set; }
		public string Basis { get; set; } = HospitalChemoBasis.PerM2;
		public string Route { get; set; } = HospitalChemoRoute.IV;
		/// The days of the cycle it is given on, "1" or "1,8".
		public string Days { get; set; } = "1";
		/// The lifetime limit, in mg/m² (doxorubicin 450, epirubicin 900, bleomycin is in units: entered as written).
		public decimal? LifetimeMgM2 { get; set; }
	}

	public class HospitalChemoCourse
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string CourseNo { get; set; } = "";
		public int PatientId { get; set; }
		public int RegimenId { get; set; }
		public string Diagnosis { get; set; } = "";
		public string? Stage { get; set; }
		public string Intent { get; set; } = HospitalChemoIntent.Curative;
		public decimal HeightCm { get; set; }
		public decimal WeightKg { get; set; }
		public DateTime StartDate { get; set; }
		public string Status { get; set; } = HospitalChemoCourseStatus.Active;
		public string? EndReason { get; set; }
		public DateTime? EndedAt { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public class HospitalChemoCycle
	{
		public int ID { get; set; }
		/// H119: the bill line this session was charged on (null = not charged: not done, or no price yet).
		public int? BillLineId { get; set; }
		/// H130: when this session's kit left its unit's store (null until it has — never twice).
		public DateTime? KitIssuedAt { get; set; }
		public int CompanyId { get; set; }
		public int CourseId { get; set; }
		/// H120: the pharmacy request that carries this cycle's drugs (stock and bill follow the pharmacy's own dispensing).
		public int? DispenseId { get; set; }
		public int PatientId { get; set; }
		public int CycleNo { get; set; }
		public string Status { get; set; } = HospitalChemoCycleStatus.Given;
		/// The day it was given, or the day it was held on.
		public DateTime CycleDate { get; set; }
		/// Held: the day it is put off to.
		public DateTime? HeldUntil { get; set; }
		public decimal? WeightKg { get; set; }
		public decimal? BsaM2 { get; set; }
		/// Neutrophils, ×10⁹/L.
		public decimal? Anc { get; set; }
		/// Platelets, ×10⁹/L.
		public int? Platelets { get; set; }
		/// Serum creatinine, mg/dL.
		public decimal? Creatinine { get; set; }
		public int DoseReductionPct { get; set; }
		/// The worst toxicity grade (CTCAE 0–4) seen since the previous cycle.
		public int? ToxicityGrade { get; set; }
		public string? ToxicityNote { get; set; }
		public string? OverrideReason { get; set; }
		public string? Reason { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public class HospitalChemoDose
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int CycleId { get; set; }
		public int PatientId { get; set; }
		public string DrugName { get; set; } = "";
		public decimal Dose { get; set; }
		public string Basis { get; set; } = HospitalChemoBasis.PerM2;
		public string Route { get; set; } = HospitalChemoRoute.IV;
		public string Days { get; set; } = "1";
		/// The milligrams per administration, after the reduction.
		public decimal DoseMg { get; set; }
		/// This cycle's mg/m² toward the lifetime limit (every day of the cycle counted).
		public decimal? CumulativeMgM2 { get; set; }
	}

	public static class HospitalOncologyModel
	{
		public static void Configure(ModelBuilder b)
		{
			HospitalChemoDrugItemModel.Configure(b);   // H120: the pharmacy's stock link for each drug
			b.Entity<HospitalChemoRegimen>(e =>
			{
				e.ToTable("HospitalChemoRegimens");   // no DbSet: each names its table
				e.Property(x => x.Code).HasMaxLength(30);
				e.Property(x => x.Name).HasMaxLength(150);
				e.Property(x => x.NameEn).HasMaxLength(150);
				e.Property(x => x.Note).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalChemoRegimenDrug>(e =>
			{
				e.ToTable("HospitalChemoRegimenDrugs");
				e.Property(x => x.DrugName).HasMaxLength(100);
				e.Property(x => x.Dose).HasPrecision(9, 2);
				e.Property(x => x.Basis).HasMaxLength(4);
				e.Property(x => x.Route).HasMaxLength(3);
				e.Property(x => x.Days).HasMaxLength(30);
				e.Property(x => x.LifetimeMgM2).HasPrecision(9, 1);
				e.HasIndex(x => new { x.CompanyId, x.RegimenId });
			});
			b.Entity<HospitalChemoCourse>(e =>
			{
				e.ToTable("HospitalChemoCourses");
				e.Property(x => x.CourseNo).HasMaxLength(20);
				e.Property(x => x.Diagnosis).HasMaxLength(200);
				e.Property(x => x.Stage).HasMaxLength(30);
				e.Property(x => x.Intent).HasMaxLength(12);
				e.Property(x => x.HeightCm).HasPrecision(5, 1);
				e.Property(x => x.WeightKg).HasPrecision(5, 1);
				e.Property(x => x.StartDate).HasColumnType("date");
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.EndReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.CourseNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalChemoCycle>(e =>
			{
				e.ToTable("HospitalChemoCycles");
				e.Property(x => x.Status).HasMaxLength(5);
				e.Property(x => x.CycleDate).HasColumnType("date");
				e.Property(x => x.HeldUntil).HasColumnType("date");
				e.Property(x => x.WeightKg).HasPrecision(5, 1);
				e.Property(x => x.BsaM2).HasPrecision(4, 2);
				e.Property(x => x.Anc).HasPrecision(5, 2);
				e.Property(x => x.Creatinine).HasPrecision(5, 2);
				e.Property(x => x.ToxicityNote).HasMaxLength(300);
				e.Property(x => x.OverrideReason).HasMaxLength(300);
				e.Property(x => x.Reason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.CourseId, x.CycleNo });
			});
			b.Entity<HospitalChemoDose>(e =>
			{
				e.ToTable("HospitalChemoDoses");
				e.Property(x => x.DrugName).HasMaxLength(100);
				e.Property(x => x.Dose).HasPrecision(9, 2);
				e.Property(x => x.Basis).HasMaxLength(4);
				e.Property(x => x.Route).HasMaxLength(3);
				e.Property(x => x.Days).HasMaxLength(30);
				e.Property(x => x.DoseMg).HasPrecision(9, 1);
				e.Property(x => x.CumulativeMgM2).HasPrecision(9, 1);
				e.HasIndex(x => new { x.CompanyId, x.CycleId });
			});
		}
	}
}
