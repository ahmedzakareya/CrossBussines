using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL DENTISTRY — the long cases (unit H17): orthodontics and implants.
	//
	//   · An ORTHODONTIC CASE is one contract: the appliance, the planned months, the full price and the monthly
	//     instalment. Each adjustment is logged on the case (never billed as a separate visit), with the aligner
	//     stage for clear aligners and the next visit; what the patient has paid is recorded against it.
	//   · An IMPLANT is registered with its system, size and lot — so any lot can be traced when its maker recalls
	//     it — the day it was placed, its stability (ISQ), and its stage: planned, healing, restored, or failed.
	// CrossBuy/deploy/sql/hospital_h17_dental_cases.sql builds the tables (prepared, not applied).
	// ==========================================================================================

	public static class HospitalOrthoAppliance
	{
		public static readonly IReadOnlyList<string> All = new[] { "FixedMetal", "FixedCeramic", "Lingual", "ClearAligners", "Functional", "Removable", "Retention" };
		public static bool Staged(string? a) => a == "ClearAligners";
	}

	/// H185: the orthodontic diagnosis and its follow-through (Angle's classification of the molar relation; the IOTN dental
	/// health component, grade 1 none to 5 very great need; the retainer each arch wears after the appliance comes off).
	public static class HospitalOrthoAngle
	{
		public static readonly IReadOnlyList<string> All = new[] { "I", "II1", "II2", "III" };
	}

	public static class HospitalOrthoRetainer
	{
		public const string None = "None";
		public static readonly IReadOnlyList<string> All = new[] { "Hawley", "Essix", "Bonded", None };
	}

	public static class HospitalOrthoHygiene
	{
		public const string Poor = "Poor";
		public static readonly IReadOnlyList<string> All = new[] { "Good", "Fair", Poor };
	}

	public static class HospitalOrthoStatus
	{
		public const string Active = "Active";
		public const string Retention = "Retention";
		public const string Completed = "Completed";
		public const string Discontinued = "Discontinued";
		public static readonly IReadOnlyList<string> All = new[] { Active, Retention, Completed, Discontinued };
		public static bool Open(string s) => s is Active or Retention;
	}

	public class HospitalOrthoCase
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string CaseNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? PractitionerId { get; set; }
		public string Appliance { get; set; } = "FixedMetal";
		public DateTime StartDate { get; set; }
		public int PlannedMonths { get; set; }
		/// Clear aligners: how many stages, and the one the patient wears now.
		public int? Stages { get; set; }
		public int? CurrentStage { get; set; }
		/// The contract, in the facility's currency.
		public decimal ContractTotal { get; set; }
		public decimal MonthlyInstalment { get; set; }
		public DateTime? NextVisit { get; set; }
		public string Status { get; set; } = HospitalOrthoStatus.Active;
		/// H185: the diagnosis the treatment rests on (millimetres; a negative crowding is spacing).
		public string? AngleClass { get; set; }
		public decimal? OverjetMm { get; set; }
		public decimal? OverbiteMm { get; set; }
		public decimal? CrowdingUpperMm { get; set; }
		public decimal? CrowdingLowerMm { get; set; }
		public int? Iotn { get; set; }
		/// H185: the day the appliance came off, and the retainer each arch wears from then.
		public DateTime? DebondedOn { get; set; }
		public string? RetainerUpper { get; set; }
		public string? RetainerLower { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	/// An adjustment visit, logged on its case.
	public class HospitalOrthoVisit
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int CaseId { get; set; }
		public DateTime VisitDate { get; set; }
		public int? AlignerStage { get; set; }
		public string? Note { get; set; }
		/// H185: what was fitted and seen at the adjustment.
		public string? ArchwireUpper { get; set; }
		public string? ArchwireLower { get; set; }
		public string? Elastics { get; set; }
		public string? Hygiene { get; set; }
		public int? Breakages { get; set; }
		public int? PractitionerId { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	/// What the patient paid against the contract.
	public class HospitalOrthoPayment
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int CaseId { get; set; }
		public DateTime PaidOn { get; set; }
		public decimal Amount { get; set; }
		public string? Reference { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		/// H167: an instalment is money taken at the cashier — the charge it put on the patient's bill, the receipt that paid
		/// it (in the cashier's shift, posted to the ledger), and how it was paid. Null on payments recorded before H167.
		public string? Method { get; set; }
		public int? BillLineId { get; set; }
		public int? PaymentId { get; set; }
	}

	public static class HospitalImplantStage
	{
		public const string Planned = "Planned";
		public const string Healing = "Healing";
		public const string Restored = "Restored";
		public const string Failed = "Failed";
		public static readonly IReadOnlyList<string> All = new[] { Planned, Healing, Restored, Failed };
	}

	public class HospitalImplant
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string ImplantNo { get; set; } = "";
		public int PatientId { get; set; }
		public int Tooth { get; set; }
		/// "Straumann BLX", "Nobel Biocare N1".
		public string System { get; set; } = "";
		public decimal Diameter { get; set; }
		public decimal Length { get; set; }
		/// The maker's lot, recorded when it is placed.
		public string? Lot { get; set; }
		public DateTime? PlacedOn { get; set; }
		/// Implant stability quotient, 1–100.
		public int? Isq { get; set; }
		public string Stage { get; set; } = HospitalImplantStage.Planned;
		public int? SurgeonId { get; set; }
		public DateTime? RestoredOn { get; set; }
		public DateTime? FailedOn { get; set; }
		public string? FailureReason { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		/// H168: how it was loaded and what was fitted; a restoration below the clinic's protocol carries the dentist's exception.
		public string? LoadingKind { get; set; }
		public string? Restoration { get; set; }
		public string? ExceptionReason { get; set; }
		public int? ExceptionByEmployeeId { get; set; }
	}

	public static class HospitalDentalCasesModel
	{
		public static void Configure(ModelBuilder b)
		{
			// no DbSet for any of them: each names its table, or EF would look for the singular name
			b.Entity<HospitalOrthoCase>(e =>
			{
				e.ToTable("HospitalOrthoCases");
				e.Property(x => x.StartDate).HasColumnType("date");
				e.Property(x => x.NextVisit).HasColumnType("date");
				e.Property(x => x.ContractTotal).HasPrecision(18, 3);
				e.Property(x => x.MonthlyInstalment).HasPrecision(18, 3);
				e.Property(x => x.PlannedMonths).HasColumnType("tinyint").HasConversion<byte>();
				e.Property(x => x.Stages).HasColumnType("tinyint").HasConversion<byte?>();
				e.Property(x => x.CurrentStage).HasColumnType("tinyint").HasConversion<byte?>();
				e.Property(x => x.AngleClass).HasMaxLength(4);   // H185
				e.Property(x => x.OverjetMm).HasPrecision(4, 1);
				e.Property(x => x.OverbiteMm).HasPrecision(4, 1);
				e.Property(x => x.CrowdingUpperMm).HasPrecision(4, 1);
				e.Property(x => x.CrowdingLowerMm).HasPrecision(4, 1);
				e.Property(x => x.Iotn).HasColumnType("tinyint").HasConversion<byte?>();
				e.Property(x => x.DebondedOn).HasColumnType("date");
				e.Property(x => x.RetainerUpper).HasMaxLength(10);
				e.Property(x => x.RetainerLower).HasMaxLength(10);
				e.HasIndex(x => new { x.CompanyId, x.CaseNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalOrthoVisit>(e =>
			{
				e.ToTable("HospitalOrthoVisits");
				e.Property(x => x.VisitDate).HasColumnType("date");
				e.Property(x => x.AlignerStage).HasColumnType("tinyint").HasConversion<byte?>();
				e.Property(x => x.ArchwireUpper).HasMaxLength(40);   // H185
				e.Property(x => x.ArchwireLower).HasMaxLength(40);
				e.Property(x => x.Elastics).HasMaxLength(60);
				e.Property(x => x.Hygiene).HasMaxLength(6);
				e.Property(x => x.Breakages).HasColumnType("tinyint").HasConversion<byte?>();
				e.HasIndex(x => new { x.CompanyId, x.CaseId });
			});
			b.Entity<HospitalOrthoPayment>(e =>
			{
				e.ToTable("HospitalOrthoPayments");
				e.Property(x => x.PaidOn).HasColumnType("date");
				e.Property(x => x.Amount).HasPrecision(18, 3);
				e.Property(x => x.Method).HasMaxLength(20);   // H167
				e.HasIndex(x => new { x.CompanyId, x.CaseId });
			});
			b.Entity<HospitalImplant>(e =>
			{
				e.ToTable("HospitalImplants");
				e.Property(x => x.Tooth).HasColumnType("tinyint").HasConversion<byte>();
				e.Property(x => x.Diameter).HasPrecision(4, 1);
				e.Property(x => x.Length).HasPrecision(4, 1);
				e.Property(x => x.PlacedOn).HasColumnType("date");
				e.Property(x => x.RestoredOn).HasColumnType("date");
				e.Property(x => x.FailedOn).HasColumnType("date");
				e.Property(x => x.Isq).HasColumnType("tinyint").HasConversion<byte?>();
				e.Property(x => x.LoadingKind).HasMaxLength(15);   // H168
				e.Property(x => x.Restoration).HasMaxLength(15);
				e.Property(x => x.ExceptionReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.ImplantNo }).IsUnique();
				// a recall by the maker is answered by lot
				e.HasIndex(x => new { x.CompanyId, x.Lot });
			});
		}
	}
}
