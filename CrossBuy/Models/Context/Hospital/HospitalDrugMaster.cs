using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H133: the drug master (formulary). Leading hospital pharmacies (Epic Willow, Oracle Health PharmNet,
	// MEDITECH; ASHP / ISMP practice) drive every step from one record per medicine. That record holds:
	//   · the generic name, as a separate field from the brand item;
	//   · strength, form and routes;
	//   · ATC code and therapeutic class;
	//   · formulary status;
	//   · the safety flags: high-alert, look-alike/sound-alike with its Tall Man spelling, controlled schedule,
	//     hazardous/cytotoxic, cold chain;
	//   · the VEN class (vital/essential/non-essential).
	// The record is NOT a second item. It hangs off the inventory item (one row per item, by ItemId), so stock, cost,
	// batches and FEFO stay in the inventory. Only what the pharmacy knows about the medicine lives here.
	// A controlled schedule keeps the H39 controlled-drug flag (HospitalControlledItems) in step: one truth, two readers.
	// CrossBuy/deploy/sql/hospital_h133_drug_master.sql builds the table.
	// ==========================================================================================

	public static class HospitalDrugForm
	{
		public const string Tablet = "Tablet", Capsule = "Capsule", Syrup = "Syrup", Suspension = "Suspension", Solution = "Solution",
			Drops = "Drops", Injection = "Injection", Infusion = "Infusion", Cream = "Cream", Ointment = "Ointment", Gel = "Gel",
			Inhaler = "Inhaler", Nebule = "Nebule", Suppository = "Suppository", Patch = "Patch", Powder = "Powder", Spray = "Spray", Lozenge = "Lozenge";
		public static readonly IReadOnlyList<string> All = new[] { Tablet, Capsule, Syrup, Suspension, Solution, Drops, Injection, Infusion, Cream,
			Ointment, Gel, Inhaler, Nebule, Suppository, Patch, Powder, Spray, Lozenge };
	}

	public static class HospitalStrengthUnit
	{
		public static readonly IReadOnlyList<string> All = new[] { "mg", "mcg", "g", "IU", "mL", "mg/mL", "mcg/mL", "mg/5mL", "%", "mmol", "mEq" };
	}

	/// The routes a medicine may be given by. The first six are the nursing order's routes (HospitalMedRoute); the
	/// pharmacy knows a few more.
	public static class HospitalDrugRoute
	{
		public static readonly IReadOnlyList<string> All = HospitalMedRoute.All.Concat(new[] { "SL", "PR", "OPH", "OTIC", "NAS", "VAG" }).ToArray();
	}

	public static class HospitalFormularyStatus
	{
		/// On the list: any prescriber, stocked.
		public const string Formulary = "Formulary";
		/// On the list for named services or with approval only (e.g. reserve antibiotics).
		public const string Restricted = "Restricted";
		/// Off the list: dispensed only on a justified request.
		public const string NonFormulary = "NonFormulary";
		/// Withdrawn or not available (shortage, recall): not to be dispensed.
		public const string Unavailable = "Unavailable";
		public static readonly IReadOnlyList<string> All = new[] { Formulary, Restricted, NonFormulary, Unavailable };
	}

	/// The controlled schedule, as Egyptian practice registers it: narcotics and psychotropics.
	public static class HospitalControlledSchedule
	{
		public const string Narcotic = "Narcotic", Psychotropic = "Psychotropic";
		public static readonly IReadOnlyList<string> All = new[] { Narcotic, Psychotropic };
	}

	/// VEN: vital (never out of stock), essential, non-essential. It weighs the safety stock the drug committee wants.
	public static class HospitalVen
	{
		public const string Vital = "V", Essential = "E", NonEssential = "N";
		public static readonly IReadOnlyList<string> All = new[] { Vital, Essential, NonEssential };
	}

	public class HospitalDrug
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ItemId { get; set; }
		/// The active ingredient (international non-proprietary name). Arabic and English, like every named field.
		public string GenericName { get; set; } = "";
		public string? GenericNameEn { get; set; }
		/// ISMP Tall Man spelling (hydrALAZINE / hydrOXYzine), shown wherever the name is shown. Same letters as the
		/// generic, only the case differs.
		public string? TallMan { get; set; }
		public decimal? Strength { get; set; }
		public string? StrengthUnit { get; set; }
		public string? Form { get; set; }
		/// Comma-separated HospitalDrugRoute codes.
		public string? Routes { get; set; }
		/// WHO ATC code (A02BC01), at any level from the anatomical group down.
		public string? Atc { get; set; }
		public string? TherapeuticClass { get; set; }
		public string FormularyStatus { get; set; } = HospitalFormularyStatus.Formulary;
		/// Who may prescribe a restricted drug, or why it is off the list.
		public string? RestrictionNote { get; set; }
		public bool HighAlert { get; set; }
		/// The names it is confused with (look-alike / sound-alike), as the pharmacist writes them.
		public string? LasaWith { get; set; }
		public string? ControlledSchedule { get; set; }
		public bool Hazardous { get; set; }
		public bool ColdChain { get; set; }
		public string? Ven { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public static class HospitalDrugModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDrug>(e =>
			{
				e.ToTable("HospitalDrugs");   // no DbSet: it names its table
				e.Property(x => x.GenericName).HasMaxLength(150);
				e.Property(x => x.GenericNameEn).HasMaxLength(150);
				e.Property(x => x.TallMan).HasMaxLength(150);
				e.Property(x => x.Strength).HasPrecision(18, 4);
				e.Property(x => x.StrengthUnit).HasMaxLength(10);
				e.Property(x => x.Form).HasMaxLength(20);
				e.Property(x => x.Routes).HasMaxLength(60);
				e.Property(x => x.Atc).HasMaxLength(10);
				e.Property(x => x.TherapeuticClass).HasMaxLength(120);
				e.Property(x => x.FormularyStatus).HasMaxLength(20);
				e.Property(x => x.RestrictionNote).HasMaxLength(300);
				e.Property(x => x.LasaWith).HasMaxLength(200);
				e.Property(x => x.ControlledSchedule).HasMaxLength(20);
				e.Property(x => x.Ven).HasMaxLength(1);
				e.HasIndex(x => new { x.CompanyId, x.ItemId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.FormularyStatus });
			});
		}
	}
}
