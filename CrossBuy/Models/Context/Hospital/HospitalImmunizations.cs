using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H84 — IMMUNIZATIONS (phase 4: the first specialty module, chosen by the owner 2026-11-10).
	//   * The SCHEDULE is a setting: each row is one dose of one vaccine at an age in days (BCG at birth, the pentavalent
	//     at 60, 120 and 180 days…). A default schedule can be loaded and then changed per country — nothing is hard-coded
	//     in the rules, which only read the rows.
	//   * An IMMUNIZATION is a dose given: what, which dose, when, the lot and its expiry, the site and route, who gave it,
	//     and an adverse event following immunization when there was one.
	// Whether a dose is given, due, overdue or upcoming is computed from the patient's birth date and the schedule.
	// CrossBuy/deploy/sql/hospital_h84_immunizations.sql builds the tables.
	// ==========================================================================================
	public static class HospitalVaccineRoute
	{
		public const string IM = "IM";
		public const string SC = "SC";
		public const string ID = "ID";
		public const string Oral = "Oral";
		public const string Nasal = "Nasal";
		public static readonly IReadOnlyList<string> All = new[] { IM, SC, ID, Oral, Nasal };
	}

	/// H183: the pre-vaccination screening answer (WHO/CDC contraindications and precautions).
	public static class HospitalVaccineScreening
	{
		public const string None = "None";
		public const string Fever = "Fever";                       // moderate or severe acute illness: a precaution — defer unless the clinician records why not
		public const string PriorSevere = "PriorSevere";           // anaphylaxis to a previous dose or a component: a contraindication
		public const string Immunosuppressed = "Immunosuppressed"; // a contraindication to a live vaccine
		public const string Pregnant = "Pregnant";                 // a contraindication to a live vaccine
		public static readonly IReadOnlyList<string> All = new[] { None, Fever, PriorSevere, Immunosuppressed, Pregnant };
	}

	public class HospitalVaccineScheduleItem
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		/// The vaccine's short code ("BCG", "PENTA", "MMR"); the doses of one vaccine share it.
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public int DoseNo { get; set; }
		/// The age at which this dose is given, in days from birth.
		public int AgeDays { get; set; }
		public string Route { get; set; } = HospitalVaccineRoute.IM;
		public bool IsActive { get; set; } = true;
		public int SortOrder { get; set; }
	}

	public class HospitalImmunization
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		/// The schedule dose it fulfils (null for a dose outside the schedule — a travel or catch-up vaccine).
		public int? ScheduleItemId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public int DoseNo { get; set; }
		public DateTime GivenAt { get; set; }
		public string? Lot { get; set; }
		public DateTime? LotExpiry { get; set; }
		public string? Site { get; set; }
		public string Route { get; set; } = HospitalVaccineRoute.IM;
		public int? GivenByEmployeeId { get; set; }
		/// An adverse event following immunization, as observed (fever, local swelling…).
		public string? AdverseEvent { get; set; }
		/// H183: the contraindication screening asked before the dose (HospitalVaccineScreening), and why a precaution was overridden.
		public string? Screening { get; set; }
		public string? ScreenOverride { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	public static class HospitalImmunizationModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalVaccineScheduleItem>(e =>
			{
				e.ToTable("HospitalVaccineScheduleItems");   // no DbSet: each names its table
				e.Property(x => x.Code).HasMaxLength(12);
				e.Property(x => x.Name).HasMaxLength(100);
				e.Property(x => x.NameEn).HasMaxLength(100);
				e.Property(x => x.Route).HasMaxLength(6);
				e.HasIndex(x => new { x.CompanyId, x.Code, x.DoseNo }).IsUnique();
			});
			b.Entity<HospitalImmunization>(e =>
			{
				e.ToTable("HospitalImmunizations");
				e.Property(x => x.Code).HasMaxLength(12);
				e.Property(x => x.Name).HasMaxLength(100);
				e.Property(x => x.NameEn).HasMaxLength(100);
				e.Property(x => x.Lot).HasMaxLength(40);
				e.Property(x => x.LotExpiry).HasColumnType("date");
				e.Property(x => x.Site).HasMaxLength(60);
				e.Property(x => x.Route).HasMaxLength(6);
				e.Property(x => x.AdverseEvent).HasMaxLength(300);
				e.Property(x => x.Screening).HasMaxLength(16);
				e.Property(x => x.ScreenOverride).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
