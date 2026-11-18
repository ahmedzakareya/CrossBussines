using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H93 — THE CLINICAL CONTENT PLATFORM the specialty clinics are built on (the approved prototype,
	// screens sc-concepts and sc-designer). The pattern of Epic SmartData + SmartForms + flowsheets, Oracle Health
	// PowerForms + DTAs, openEHR archetypes + templates and HL7 FHIR SDC:
	//   * HospitalConcepts — the CONCEPT DICTIONARY: every measurable thing defined once (an international code, Arabic
	//     and English, type, unit, normal and critical limits, whether it has a side, the lab analyte it is read from).
	//   * HospitalClinicalTemplates / …Versions / …Fields — a specialty TEMPLATE built by clinicians from the dictionary,
	//     VERSIONED: only a draft is edited; publishing freezes it, and a visit keeps the version it was taken on.
	//   * HospitalClinicalForms — one filled template on one visit (the answers, kept with their version).
	//   * HospitalClinicalObservations — at sign-off every value becomes ONE ROW (patient, concept, value, unit, side,
	//     time, flag). Trends, alerts, care gaps and reports read only these rows, never a form's answers.
	// CrossBuy/deploy/sql/hospital_h93_clinical_content.sql builds the tables.
	// ==========================================================================================
	public static class HospitalConceptType
	{
		public const string Number = "Number";
		/// Two numbers written "a/b" (blood pressure).
		public const string Panel = "Panel";
		/// Computed from other concepts by a calculator (eGFR, BMI, KDIGO stage).
		public const string Calculated = "Calculated";
		/// The sum of the form's items (PHQ-9, ACT).
		public const string Score = "Score";
		/// One of a fixed list of answers.
		public const string Choice = "Choice";
		public static readonly IReadOnlyList<string> All = new[] { Number, Panel, Calculated, Score, Choice };
	}

	public static class HospitalCodeSystem
	{
		public const string Loinc = "LOINC";
		public const string Snomed = "SNOMED";
		public const string Local = "Local";
		public static readonly IReadOnlyList<string> All = new[] { Loinc, Snomed, Local };
	}

	public static class HospitalConceptCalculator
	{
		/// kg / m² from the form's weight and height.
		public const string Bmi = "Bmi";
		/// CKD-EPI 2021 (race-free) from the creatinine, the patient's age and sex.
		public const string EgfrCkdEpi2021 = "EgfrCkdEpi2021";
		/// KDIGO 2024: the G category from eGFR × the A category from the urine ACR.
		public const string Kdigo = "Kdigo";
		/// The sum of the listed concepts (a Score's items).
		public const string Sum = "Sum";
		/// H97: the specific-IgE class (ImmunoCAP 0–6) from kU/L.
		public const string IgeClass = "IgeClass";
		/// H188: the corrected QT (ms) from the QT (ms) and the heart rate (bpm) — Bazett (QT/√RR) or Fridericia (QT/∛RR).
		public const string QtcBazett = "QtcBazett";
		public const string QtcFridericia = "QtcFridericia";
		/// H188: ACR TI-RADS 2017 — the level from the points, and what the nodule's size calls for (FNA, follow-up, none).
		public const string TiRads = "TiRads";
		/// H188: the Psoriasis Area and Severity Index (0–72) from the four regions' area and erythema, induration, scaling.
		public const string Pasi = "Pasi";
		/// H188: the WHO bone density class from the T-score.
		public const string TScoreClass = "TScoreClass";
		public static readonly IReadOnlyList<string> All = new[] { Bmi, EgfrCkdEpi2021, Kdigo, Sum, IgeClass, QtcBazett, QtcFridericia, TiRads, Pasi, TScoreClass };

		/// H188: how many codes each calculator reads, in order (Sum: at least one).
		public static int InputsNeeded(string? calculator) => calculator switch
		{
			Bmi or Kdigo or QtcBazett or QtcFridericia or TiRads => 2,
			EgfrCkdEpi2021 or IgeClass or TScoreClass => 1,
			Pasi => 16,
			Sum => 1,
			_ => 0,
		};
	}

	public static class HospitalTemplateStatus
	{
		public const string Draft = "Draft";
		public const string Published = "Published";
		/// A published version replaced by a newer one; visits taken on it still render with it.
		public const string Retired = "Retired";
		public static readonly IReadOnlyList<string> All = new[] { Draft, Published, Retired };
	}

	public static class HospitalValueSource
	{
		public const string Manual = "Manual";
		/// The last verified lab result of the concept's analyte within the look-back days; typed when there is none.
		public const string LastLab = "LastLab";
		public static readonly IReadOnlyList<string> All = new[] { Manual, LastLab };
	}

	public static class HospitalFormStatus
	{
		public const string Draft = "Draft";
		public const string Signed = "Signed";
		public static readonly IReadOnlyList<string> All = new[] { Draft, Signed };
	}

	public static class HospitalObsFlag
	{
		public const string Normal = "N";
		public const string Low = "L";
		public const string High = "H";
		public const string CriticalLow = "LL";
		public const string CriticalHigh = "HH";
		/// H94: nothing to compare against — the concept has no limits (or there is no number). Never shown as "normal".
		public const string Unknown = "U";
		public static readonly IReadOnlyList<string> All = new[] { Normal, Low, High, CriticalLow, CriticalHigh, Unknown };
		public static bool Abnormal(string? f) => f is Low or High or CriticalLow or CriticalHigh;
	}

	public static class HospitalObsSource
	{
		public const string Form = "Form";
		public const string Lab = "Lab";
		/// H110: a canvas calculator's result (burn %, DAS28, hearing average), written when the visit is signed.
		public const string Canvas = "Canvas";
		/// H112: reported by the patient (their pain on the tablet) — never mixed with what a clinician measured.
		public const string Patient = "Patient";
		public static readonly IReadOnlyList<string> All = new[] { Form, Lab, Canvas, Patient };
	}

	public class HospitalConcept
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string CodeSystem { get; set; } = HospitalCodeSystem.Loinc;
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string ValueType { get; set; } = HospitalConceptType.Number;
		public string? Unit { get; set; }
		public int Decimals { get; set; } = 1;
		public decimal? NormalLow { get; set; }
		public decimal? NormalHigh { get; set; }
		/// A panel's second number (the diastolic).
		public decimal? NormalHigh2 { get; set; }
		public decimal? CriticalLow { get; set; }
		public decimal? CriticalHigh { get; set; }
		public decimal? CriticalHigh2 { get; set; }
		/// Shown instead of the computed range when the limits are not numbers ("by age", "± 2 SD").
		public string? NormalText { get; set; }
		public string? NormalTextEn { get; set; }
		public string? CriticalText { get; set; }
		public string? CriticalTextEn { get; set; }
		/// A choice's answers, one per line: code|Arabic|English.
		public string? Choices { get; set; }
		public string? Calculator { get; set; }
		/// The calculator's inputs: concept codes, comma-separated.
		public string? CalcInputs { get; set; }
		/// Recorded per side (right / left): an eye, an ear, a limb.
		public bool HasLaterality { get; set; }
		/// The lab analyte code a "last lab" field is read from (HospitalLabAnalytes.Code).
		public string? LabAnalyteCode { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public class HospitalClinicalTemplate
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		/// The clinic it belongs to; null = every clinic.
		public int? DepartmentId { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public class HospitalClinicalTemplateVersion
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int TemplateId { get; set; }
		public int Version { get; set; }
		public string Status { get; set; } = HospitalTemplateStatus.Draft;
		public string? ChangeNote { get; set; }
		/// H94: the change note's English twin.
		public string? ChangeNoteEn { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? PublishedAt { get; set; }
		public int? PublishedByEmployeeId { get; set; }
	}

	public class HospitalClinicalTemplateField
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int VersionId { get; set; }
		public int SortOrder { get; set; }
		public string Section { get; set; } = "";
		public string SectionEn { get; set; } = "";
		public int ConceptId { get; set; }
		/// The question as the form asks it; null = the concept's name.
		public string? Label { get; set; }
		public string? LabelEn { get; set; }
		public bool Required { get; set; }
		public string ValueSource { get; set; } = HospitalValueSource.Manual;
		public int? LookbackDays { get; set; }
		/// Shown only when this other field of the SAME version holds a value above ShowWhenAbove.
		public int? ShowWhenFieldId { get; set; }
		public decimal? ShowWhenAbove { get; set; }
		public string? Note { get; set; }
		public string? NoteEn { get; set; }
		/// Alert when the value is at or above / at or below these.
		public decimal? AlertAbove { get; set; }
		public decimal? AlertBelow { get; set; }
		public string? AlertText { get; set; }
		public string? AlertTextEn { get; set; }
		/// A care gap when the patient has no observation of the concept within this many days.
		public int? CareGapDays { get; set; }
		/// H94: the field heads the visit — a figure on top and a trend tab.
		public bool ShowTrend { get; set; }
		/// H106: when the alert fires, the department's on-call doctor is notified at once …
		public bool AlertNotify { get; set; }
		/// … and this template opens on the visit (C-SSRS after PHQ-9 item 9).
		public int? AlertTemplateId { get; set; }
		/// H116: also alert when the value is outside THIS patient's normal range (H115: by sex, age, pregnancy) — always
		/// (Any) or only while she is pregnant (Pregnant: "TSH out of range in pregnancy"). Null = the fixed lines only.
		public string? AlertRange { get; set; }
	}

	public static class HospitalAlertRange
	{
		public const string Any = "Any";
		public const string Pregnant = "Pregnant";
		public static readonly IReadOnlyList<string> All = new[] { Any, Pregnant };
	}

	public class HospitalClinicalForm
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public int EncounterId { get; set; }
		public int TemplateVersionId { get; set; }
		public string Status { get; set; } = HospitalFormStatus.Draft;
		/// {"<fieldId>": "8.3", "<fieldId>.R": "0.2", "<fieldId>.L": "0.3"} — values as written.
		public string AnswersJson { get; set; } = "{}";
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public DateTime? SignedAt { get; set; }
		public int? SignedByEmployeeId { get; set; }
	}

	public class HospitalClinicalObservation
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public int ConceptId { get; set; }
		public int? EncounterId { get; set; }
		public int? FormId { get; set; }
		public decimal? Value { get; set; }
		/// A panel's second number.
		public decimal? Value2 { get; set; }
		/// A choice's answer code, or a calculator's category (KDIGO "G3a A2").
		public string? ValueCode { get; set; }
		/// R or L for a concept with a side.
		public string? Side { get; set; }
		public string? Unit { get; set; }
		public string Flag { get; set; } = HospitalObsFlag.Normal;
		public DateTime ObservedAt { get; set; }
		public string Source { get; set; } = HospitalObsSource.Form;
		public DateTime RecordedAt { get; set; }
		public int? RecordedByEmployeeId { get; set; }
	}

	public static class HospitalClinicalContentModel
	{
		public static void Configure(ModelBuilder b)
		{
			HospitalConceptRangeModel.Configure(b);   // H115: ranges by who the patient is
			b.Entity<HospitalConcept>(e =>
			{
				e.ToTable("HospitalConcepts");   // no DbSet: each names its table
				e.Property(x => x.Code).HasMaxLength(30);
				e.Property(x => x.CodeSystem).HasMaxLength(10);
				e.Property(x => x.Name).HasMaxLength(150);
				e.Property(x => x.NameEn).HasMaxLength(150);
				e.Property(x => x.ValueType).HasMaxLength(12);
				e.Property(x => x.Unit).HasMaxLength(30);
				foreach (var p in new[] { "NormalLow", "NormalHigh", "NormalHigh2", "CriticalLow", "CriticalHigh", "CriticalHigh2" }) e.Property<decimal?>(p).HasPrecision(18, 4);
				e.Property(x => x.NormalText).HasMaxLength(60);
				e.Property(x => x.NormalTextEn).HasMaxLength(60);
				e.Property(x => x.CriticalText).HasMaxLength(60);
				e.Property(x => x.CriticalTextEn).HasMaxLength(60);
				e.Property(x => x.Choices).HasMaxLength(2000);
				e.Property(x => x.Calculator).HasMaxLength(20);
				e.Property(x => x.CalcInputs).HasMaxLength(200);
				e.Property(x => x.LabAnalyteCode).HasMaxLength(20);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalClinicalTemplate>(e =>
			{
				e.ToTable("HospitalClinicalTemplates");
				e.Property(x => x.Code).HasMaxLength(20);
				e.Property(x => x.Name).HasMaxLength(150);
				e.Property(x => x.NameEn).HasMaxLength(150);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalClinicalTemplateVersion>(e =>
			{
				e.ToTable("HospitalClinicalTemplateVersions");
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.ChangeNote).HasMaxLength(300);
				e.Property(x => x.ChangeNoteEn).HasMaxLength(300);
				e.HasIndex(x => new { x.TemplateId, x.Version }).IsUnique();
			});
			b.Entity<HospitalClinicalTemplateField>(e =>
			{
				e.ToTable("HospitalClinicalTemplateFields");
				e.Property(x => x.Section).HasMaxLength(100);
				e.Property(x => x.SectionEn).HasMaxLength(100);
				e.Property(x => x.Label).HasMaxLength(200);
				e.Property(x => x.LabelEn).HasMaxLength(200);
				e.Property(x => x.ValueSource).HasMaxLength(10);
				e.Property(x => x.ShowWhenAbove).HasPrecision(18, 4);
				e.Property(x => x.Note).HasMaxLength(300);
				e.Property(x => x.NoteEn).HasMaxLength(300);
				e.Property(x => x.AlertAbove).HasPrecision(18, 4);
				e.Property(x => x.AlertBelow).HasPrecision(18, 4);
				e.Property(x => x.AlertRange).HasMaxLength(8);
				e.Property(x => x.AlertText).HasMaxLength(150);
				e.Property(x => x.AlertTextEn).HasMaxLength(150);
				e.HasIndex(x => new { x.VersionId, x.SortOrder });
			});
			b.Entity<HospitalClinicalForm>(e =>
			{
				e.ToTable("HospitalClinicalForms");
				e.Property(x => x.Status).HasMaxLength(10);
				e.HasIndex(x => new { x.CompanyId, x.EncounterId });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalClinicalObservation>(e =>
			{
				e.ToTable("HospitalClinicalObservations");
				e.Property(x => x.Value).HasPrecision(18, 4);
				e.Property(x => x.Value2).HasPrecision(18, 4);
				e.Property(x => x.ValueCode).HasMaxLength(40);
				e.Property(x => x.Side).HasMaxLength(1);
				e.Property(x => x.Unit).HasMaxLength(30);
				e.Property(x => x.Flag).HasMaxLength(2);
				e.Property(x => x.Source).HasMaxLength(10);   // H112: 'Patient' is seven letters
				e.HasIndex(x => new { x.CompanyId, x.PatientId, x.ConceptId, x.ObservedAt });
				e.HasIndex(x => new { x.CompanyId, x.FormId });
			});
		}
	}
}
