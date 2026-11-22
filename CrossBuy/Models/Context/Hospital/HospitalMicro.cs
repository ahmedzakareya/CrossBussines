using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H230 — MICROBIOLOGY. A lab test marked as a culture (HospitalLabTest.IsCulture) is not resulted by its
	// analyte grid: once the specimen is received it gets a CULTURE record — the source, the Gram stain, preliminary
	// reports (no growth at 24/48 h, growth seen) and, when there is growth, ISOLATES (an organism from the dictionary
	// and its quantity), each with its SUSCEPTIBILITY results (S / I / R, optional MIC). The panel of antibiotics is set
	// per organism group, in two tiers: tier 2 is reported only when the isolate resists a tier-1 agent (cascade /
	// selective reporting). Completing the culture writes a readable summary into the order's one result row — so the
	// culture reaches every place a result already goes — and the order is verified by a second person as any test.
	// Multidrug-resistant organisms (MRSA, VRE, ESBL, CRE, CRAB, CRPA) are flagged from the results, and a verified one
	// opens a suspected MDRO case for infection control when the patient is admitted.
	// deploy/sql/hospital_h230_microbiology.sql builds the tables.
	// ==========================================================================================

	public static class HospitalMicroGroup
	{
		public const string Staphylococci = "Staphylococci";
		public const string Streptococci = "Streptococci";
		public const string Enterococci = "Enterococci";
		public const string Enterobacterales = "Enterobacterales";
		public const string Pseudomonas = "Pseudomonas";
		public const string Acinetobacter = "Acinetobacter";
		public const string Fungi = "Fungi";
		public const string Other = "Other";
		public static readonly IReadOnlyList<string> All = new[] { Staphylococci, Streptococci, Enterococci, Enterobacterales, Pseudomonas, Acinetobacter, Fungi, Other };
	}

	public static class HospitalMicroStatus
	{
		/// Received and on the plates; nothing reported yet.
		public const string Incubating = "Incubating";
		/// A preliminary report went out (Gram stain, no growth so far, growth seen).
		public const string Preliminary = "Preliminary";
		/// Completed by the bench: the summary is written and the order waits for the second person.
		public const string Completed = "Completed";
		/// Verified with the order; locked.
		public const string Final = "Final";
		public static readonly IReadOnlyList<string> All = new[] { Incubating, Preliminary, Completed, Final };
	}

	public static class HospitalMicroGrowth
	{
		public const string Pending = "Pending";
		public const string NoGrowth = "NoGrowth";
		public const string Growth = "Growth";
		/// Mixed growth of doubtful significance — recollection advised; no susceptibility.
		public const string Contaminated = "Contaminated";
		public static readonly IReadOnlyList<string> All = new[] { Pending, NoGrowth, Growth, Contaminated };
	}

	public static class HospitalMicroSir
	{
		public const string Susceptible = "S";
		/// EUCAST 2019+: "susceptible, increased exposure".
		public const string Intermediate = "I";
		public const string Resistant = "R";
		public static readonly IReadOnlyList<string> All = new[] { Susceptible, Intermediate, Resistant };
	}

	public static class HospitalMdro
	{
		public const string Mrsa = "MRSA";
		public const string Vre = "VRE";
		public const string Esbl = "ESBL";
		public const string Cre = "CRE";
		public const string Crab = "CRAB";
		public const string Crpa = "CRPA";
		public static readonly IReadOnlyList<string> All = new[] { Mrsa, Vre, Esbl, Cre, Crab, Crpa };
	}

	public class HospitalMicroOrganism
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string Group { get; set; } = HospitalMicroGroup.Other;
		/// Pos / Neg / NA (fungi).
		public string Gram { get; set; } = "NA";
		/// "Staphylococcus aureus" is the one MRSA is defined on; this flag marks it (and only it) in its group.
		public bool IsAureus { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public class HospitalMicroAntibiotic
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		/// Pharmacological class (Penicillins, Cephalosporins, Carbapenems, Glycopeptides, ...).
		public string Class { get; set; } = "";
		public bool IsActive { get; set; } = true;
	}

	/// One antibiotic in an organism group's panel, in its reporting tier.
	public class HospitalMicroPanelItem
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Group { get; set; } = HospitalMicroGroup.Other;
		public int AntibioticId { get; set; }
		/// 1 = always reported; 2 = reported only when the isolate resists a tier-1 agent.
		public int Tier { get; set; } = 1;
		public int SortOrder { get; set; }
	}

	public class HospitalMicroCulture
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int LabOrderId { get; set; }
		public int PatientId { get; set; }
		/// Blood, Urine, Sputum, Wound, CSF, Stool, Swab, Other.
		public string Source { get; set; } = "Other";
		public string? SourceDetail { get; set; }
		public string Status { get; set; } = HospitalMicroStatus.Incubating;
		public string Growth { get; set; } = HospitalMicroGrowth.Pending;
		public string? GramStain { get; set; }
		/// The last preliminary report's text, as it was released.
		public string? Preliminary { get; set; }
		public DateTime? PreliminaryAt { get; set; }
		public int? PreliminaryByEmployeeId { get; set; }
		public string? Comment { get; set; }
		/// MDRO codes found on completion, comma-joined (empty: none).
		public string? Mdro { get; set; }
		public DateTime StartedAt { get; set; }
		public int? StartedByEmployeeId { get; set; }
		public DateTime? CompletedAt { get; set; }
		public int? CompletedByEmployeeId { get; set; }
		public DateTime? FinalAt { get; set; }
		public int? InfectionCaseId { get; set; }
	}

	public class HospitalMicroIsolate
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int CultureId { get; set; }
		public int PatientId { get; set; }
		public int No { get; set; }
		public int OrganismId { get; set; }
		/// e.g. "&gt;100,000 CFU/mL", "heavy growth", "1 of 2 bottles".
		public string? Quantity { get; set; }
		/// MDRO code for this isolate (empty: none), computed on every save of its results.
		public string? Mdro { get; set; }
		public string? Comment { get; set; }
	}

	public class HospitalMicroSusceptibility
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int IsolateId { get; set; }
		public int AntibioticId { get; set; }
		public string Result { get; set; } = HospitalMicroSir.Susceptible;
		public decimal? Mic { get; set; }
		/// Set on completion: whether this line is on the clinician's report (tier 1, or tier 2 released by cascade).
		public bool Reported { get; set; } = true;
	}

	public static class HospitalMicroModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalMicroOrganism>(e =>
			{
				e.ToTable("HospitalMicroOrganisms");
				e.Property(x => x.Code).HasMaxLength(20); e.Property(x => x.Name).HasMaxLength(120); e.Property(x => x.NameEn).HasMaxLength(120);
				e.Property(x => x.Group).HasMaxLength(20).HasColumnName("OrganismGroup"); e.Property(x => x.Gram).HasMaxLength(3);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalMicroAntibiotic>(e =>
			{
				e.ToTable("HospitalMicroAntibiotics");
				e.Property(x => x.Code).HasMaxLength(20); e.Property(x => x.Name).HasMaxLength(120); e.Property(x => x.NameEn).HasMaxLength(120);
				e.Property(x => x.Class).HasMaxLength(40);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalMicroPanelItem>(e =>
			{
				e.ToTable("HospitalMicroPanelItems");
				e.Property(x => x.Group).HasMaxLength(20).HasColumnName("OrganismGroup");
				e.HasIndex(x => new { x.CompanyId, x.Group, x.AntibioticId }).IsUnique();
			});
			b.Entity<HospitalMicroCulture>(e =>
			{
				e.ToTable("HospitalMicroCultures");
				e.Property(x => x.Source).HasMaxLength(20); e.Property(x => x.SourceDetail).HasMaxLength(120);
				e.Property(x => x.Status).HasMaxLength(12); e.Property(x => x.Growth).HasMaxLength(12);
				e.Property(x => x.GramStain).HasMaxLength(300); e.Property(x => x.Preliminary).HasMaxLength(500);
				e.Property(x => x.Comment).HasMaxLength(1000); e.Property(x => x.Mdro).HasMaxLength(60);
				e.HasIndex(x => new { x.CompanyId, x.LabOrderId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalMicroIsolate>(e =>
			{
				e.ToTable("HospitalMicroIsolates");
				e.Property(x => x.Quantity).HasMaxLength(60); e.Property(x => x.Mdro).HasMaxLength(10); e.Property(x => x.Comment).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.CultureId, x.No }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalMicroSusceptibility>(e =>
			{
				e.ToTable("HospitalMicroSusceptibilities");
				e.Property(x => x.Result).HasMaxLength(1);
				e.Property(x => x.Mic).HasPrecision(9, 3);
				e.HasIndex(x => new { x.CompanyId, x.IsolateId, x.AntibioticId }).IsUnique();
			});
		}
	}
}
