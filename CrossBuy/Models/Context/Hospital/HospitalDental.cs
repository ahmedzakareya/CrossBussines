using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL DENTISTRY (unit H15) — the first specialty pack.
	//
	// What a dental practice changes against the general clinic:
	//   · the record is a TOOTH CHART: FDI / ISO 3950 numbering (11–48, primary 51–85), five surfaces per tooth
	//     (M, O or I on the front teeth, D, B, L) and whole-tooth work (crown, root canal, implant, extraction),
	//     each marked as a condition, planned, completed here or existing from elsewhere — the charting convention
	//     of the commercial systems (red, red hatch, blue, green);
	//   · a PERIODONTAL CHART: six sites per tooth with probing depth, recession and bleeding, plus mobility and
	//     furcation; an exam is kept whole, the chart shows the latest;
	//   · the bill is per TOOTH and SURFACE: a dental procedure is a hospital service with a dental profile (its
	//     category, how many surfaces a multi-surface code bills, the whole-tooth work it does, pre-authorisation,
	//     lab work); work is planned in PHASES and presented as a quotation the patient accepts phase by phase;
	//   · a payer's dental benefit: the cover per category and a yearly dental limit.
	// CrossBuy/deploy/sql/hospital_h15_dental.sql builds the tables (prepared, not applied).
	// ==========================================================================================

	/// The procedure categories a dental benefit table is written in.
	public static class HospitalDentalCategory
	{
		public const string Diagnostic = "Diagnostic";
		public const string Preventive = "Preventive";
		public const string Restorative = "Restorative";
		public const string Endodontic = "Endodontic";
		public const string Prosthetic = "Prosthetic";
		public const string Surgery = "Surgery";
		public const string Implant = "Implant";
		public const string Cosmetic = "Cosmetic";
		public static readonly IReadOnlyList<string> All = new[] { Diagnostic, Preventive, Restorative, Endodontic, Prosthetic, Surgery, Implant, Cosmetic };
	}

	/// Work on the whole tooth rather than on its surfaces, and the tooth's own states.
	public static class HospitalToothWork
	{
		public const string Crown = "Crown";
		public const string RootCanal = "RootCanal";
		public const string Implant = "Implant";
		public const string Extraction = "Extraction";
		public const string Missing = "Missing";
		public const string Impacted = "Impacted";
		/// What a procedure may do to a tooth.
		public static readonly IReadOnlyList<string> Procedures = new[] { Crown, RootCanal, Implant, Extraction };
		public static readonly IReadOnlyList<string> All = new[] { Crown, RootCanal, Implant, Extraction, Missing, Impacted };
		public static bool IsState(string? w) => w == Missing || w == Impacted;
	}

	/// The four marks of the chart.
	public static class HospitalDentalMark
	{
		public const string Condition = "Condition";
		public const string Planned = "Planned";
		public const string Completed = "Completed";
		public const string Existing = "Existing";
		public static readonly IReadOnlyList<string> All = new[] { Condition, Planned, Completed, Existing };
	}

	public static class HospitalDentition
	{
		public const string Permanent = "Permanent";
		public const string Mixed = "Mixed";
		public const string Primary = "Primary";
		public static readonly IReadOnlyList<string> All = new[] { Permanent, Mixed, Primary };
	}

	public static class HospitalDentalDecision
	{
		public const string Pending = "Pending";
		public const string Accepted = "Accepted";
		public const string Declined = "Declined";
		public static readonly IReadOnlyList<string> All = new[] { Pending, Accepted, Declined };
	}

	public static class HospitalDentalLineStatus
	{
		public const string Planned = "Planned";
		public const string Completed = "Completed";
		public const string Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Planned, Completed, Cancelled };
	}

	/// The dental profile of a hospital service: the service keeps its code, names and price; this says how the
	/// chart and the plan treat it.
	public class HospitalDentalProcedure
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ServiceId { get; set; }
		public string Category { get; set; } = HospitalDentalCategory.Restorative;
		/// How many surfaces the code bills (a multi-surface code: 1, 2, 3…); null when it is not a surface procedure.
		public int? Surfaces { get; set; }
		/// The whole-tooth work it does (crown, root canal, implant, extraction), or null.
		public string? ToothWork { get; set; }
		/// Done on one tooth (a periapical X-ray) even when it marks nothing on the chart.
		public bool NeedsTooth { get; set; }
		public bool NeedsPreauth { get; set; }
		public bool NeedsLab { get; set; }
		/// Never covered by a dental benefit (implants, whitening at most facilities).
		public bool NotCovered { get; set; }
		/// The chair time it books.
		public int Minutes { get; set; } = 30;
	}

	/// A payer's dental benefit: the cover per category (percent of the fee) and the yearly dental limit.
	public class HospitalDentalBenefit
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PayerId { get; set; }
		public decimal YearlyLimit { get; set; }
		public decimal DiagnosticPct { get; set; }
		public decimal PreventivePct { get; set; }
		public decimal RestorativePct { get; set; }
		public decimal EndodonticPct { get; set; }
		public decimal ProstheticPct { get; set; }
		public decimal SurgeryPct { get; set; }
		public decimal ImplantPct { get; set; }
		public decimal CosmeticPct { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	/// The patient's chart header: the dentition the chart is drawn in (null = by age).
	public class HospitalDentalChart
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public string? Dentition { get; set; }
		public DateTime UpdatedAt { get; set; }
	}

	/// One mark on the chart: on surfaces of a tooth, or on the whole tooth (work, or its state). A mark made in
	/// error is voided, never deleted; a planned mark belongs to its plan line and completes with it.
	public class HospitalDentalFinding
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public int Tooth { get; set; }
		/// "MOD", in chart order; null for a whole-tooth mark.
		public string? Surfaces { get; set; }
		/// HospitalToothWork; null for a surface mark.
		public string? Work { get; set; }
		public string Status { get; set; } = HospitalDentalMark.Condition;
		public int? ServiceId { get; set; }
		public int? PlanLineId { get; set; }
		public int? PractitionerId { get; set; }
		public string? Note { get; set; }
		/// H33: the note's English twin.
		public string? NoteEn { get; set; }
		public DateTime RecordedAt { get; set; }
		public int? RecordedByEmployeeId { get; set; }
		public DateTime? CompletedAt { get; set; }
		public DateTime? VoidedAt { get; set; }
		public int? VoidedByEmployeeId { get; set; }
	}

	public class HospitalDentalPlan
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string PlanNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? PractitionerId { get; set; }
		/// The currency the fees are in: the patient's price list's when the plan was opened (null = the facility's).
		public int? CurrencyId { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public class HospitalDentalPlanPhase
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PlanId { get; set; }
		public int Seq { get; set; }
		public string Title { get; set; } = "";
		public string TitleEn { get; set; } = "";
		public string Decision { get; set; } = HospitalDentalDecision.Pending;
		/// Who accepted or declined it on the patient's side (the patient, or the guardian).
		public string? Signatory { get; set; }
		public DateTime? DecidedAt { get; set; }
		public int? DecidedByEmployeeId { get; set; }
	}

	public class HospitalDentalPlanLine
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PlanId { get; set; }
		public int PhaseId { get; set; }
		public int? Tooth { get; set; }
		public string? Surfaces { get; set; }
		public int ServiceId { get; set; }
		/// Who does it when it is not the plan's dentist (the surgeon, the hygienist).
		public int? ProviderId { get; set; }
		/// The fee quoted when the line was added, in the plan's currency.
		public decimal Fee { get; set; }
		public string Status { get; set; } = HospitalDentalLineStatus.Planned;
		public int? PreauthId { get; set; }
		public DateTime CreatedAt { get; set; }
		public DateTime? CompletedAt { get; set; }
		public int? CompletedByEmployeeId { get; set; }
		/// H53: the bill line the completed work was charged as (its accepted fee).
		public int? BillLineId { get; set; }
	}

	public class HospitalPerioExam
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public DateTime ExamDate { get; set; }
		public int? PractitionerId { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	/// One tooth of a periodontal exam. The six sites are stored buccal DB·B·MB then lingual DL·L·ML, as
	/// comma-separated millimetres (recession may be negative: gingival overgrowth); bleeding as six 0/1 digits.
	public class HospitalPerioTooth
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ExamId { get; set; }
		public int Tooth { get; set; }
		public string Depths { get; set; } = "";
		public string Recession { get; set; } = "";
		public string Bleeding { get; set; } = "000000";
		/// 0–3.
		public int? Mobility { get; set; }
		/// 1–3 (I–III), multi-rooted teeth only.
		public int? Furcation { get; set; }
	}

	public static class HospitalDentalModel
	{
		public static void Configure(ModelBuilder b)
		{
			// no DbSet for any of them: each names its table, or EF would look for the singular name
			b.Entity<HospitalDentalProcedure>(e =>
			{
				e.ToTable("HospitalDentalProcedures");
				e.Property(x => x.Surfaces).HasColumnType("tinyint").HasConversion<byte?>();
				e.Property(x => x.Minutes).HasColumnType("smallint").HasConversion<short>();
				e.HasIndex(x => new { x.CompanyId, x.ServiceId }).IsUnique();
			});
			b.Entity<HospitalDentalBenefit>(e =>
			{
				e.ToTable("HospitalDentalBenefits");
				e.Property(x => x.YearlyLimit).HasPrecision(18, 3);
				foreach (var p in new[] { nameof(HospitalDentalBenefit.DiagnosticPct), nameof(HospitalDentalBenefit.PreventivePct), nameof(HospitalDentalBenefit.RestorativePct),
					nameof(HospitalDentalBenefit.EndodonticPct), nameof(HospitalDentalBenefit.ProstheticPct), nameof(HospitalDentalBenefit.SurgeryPct),
					nameof(HospitalDentalBenefit.ImplantPct), nameof(HospitalDentalBenefit.CosmeticPct) })
					e.Property<decimal>(p).HasPrecision(5, 2);
				e.HasIndex(x => new { x.CompanyId, x.PayerId }).IsUnique();
			});
			b.Entity<HospitalDentalChart>(e =>
			{
				e.ToTable("HospitalDentalCharts");
				e.HasIndex(x => new { x.CompanyId, x.PatientId }).IsUnique();
			});
			b.Entity<HospitalDentalFinding>(e =>
			{
				e.ToTable("HospitalDentalFindings");
				e.Property(x => x.Tooth).HasColumnType("tinyint").HasConversion<byte>();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
				// a plan line has one mark on the chart
				e.HasIndex(x => x.PlanLineId, "UX_HospitalDentalFindings_PlanLine").IsUnique().HasFilter("[PlanLineId] IS NOT NULL");
			});
			b.Entity<HospitalDentalPlan>(e =>
			{
				e.ToTable("HospitalDentalPlans");
				e.HasIndex(x => new { x.CompanyId, x.PlanNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalDentalPlanPhase>(e =>
			{
				e.ToTable("HospitalDentalPlanPhases");
				e.Property(x => x.Seq).HasColumnType("tinyint").HasConversion<byte>();
				e.HasIndex(x => new { x.PlanId, x.Seq }).IsUnique();
			});
			b.Entity<HospitalDentalPlanLine>(e =>
			{
				e.ToTable("HospitalDentalPlanLines");
				e.Property(x => x.Tooth).HasColumnType("tinyint").HasConversion<byte?>();
				e.Property(x => x.Fee).HasPrecision(18, 3);
				e.HasIndex(x => new { x.CompanyId, x.PlanId });
			});
			b.Entity<HospitalPerioExam>(e =>
			{
				e.ToTable("HospitalPerioExams");
				e.Property(x => x.ExamDate).HasColumnType("date");
				e.HasIndex(x => new { x.CompanyId, x.PatientId, x.ExamDate });
			});
			b.Entity<HospitalPerioTooth>(e =>
			{
				e.ToTable("HospitalPerioTeeth");
				e.Property(x => x.Tooth).HasColumnType("tinyint").HasConversion<byte>();
				e.Property(x => x.Mobility).HasColumnType("tinyint").HasConversion<byte?>();
				e.Property(x => x.Furcation).HasColumnType("tinyint").HasConversion<byte?>();
				e.HasIndex(x => new { x.ExamId, x.Tooth }).IsUnique();
			});
		}
	}
}
