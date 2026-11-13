using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H60 — informed CONSENT with an electronic signature (the prototype's "medical records: consents &
	// e-signature"). Two tables:
	//   · HospitalConsentForms — the hospital's own consent wording, per kind (procedure, anaesthesia, transfusion…),
	//     in Arabic and English, kept by the administrator. Changing the wording raises its version.
	//   · HospitalConsents — one signed decision: the patient (or their representative) GIVES or REFUSES consent to
	//     what a clinician explained, before a witness, signing on the screen. The text they read is frozen into the
	//     row (later edits to the form never change it); the signature is a picture kept on the document platform
	//     (owner rule: every file in the File Manager) with its SHA-256 fingerprint. It is never edited or deleted:
	//     a consent is WITHDRAWN with a reason.
	// CrossBuy/deploy/sql/hospital_h60_consents.sql builds the tables.
	// ==========================================================================================
	public static class HospitalConsentKind
	{
		public const string General = "General";
		public const string Procedure = "Procedure";
		public const string Anaesthesia = "Anaesthesia";
		public const string Transfusion = "Transfusion";
		public const string Contrast = "Contrast";
		public const string Photography = "Photography";
		public const string AgainstAdvice = "AgainstAdvice";
		public static readonly IReadOnlyList<string> All = new[] { General, Procedure, Anaesthesia, Transfusion, Contrast, Photography, AgainstAdvice };
		/// These name what is done: the consent says which procedure.
		public static bool NamesAProcedure(string kind) => kind is Procedure or Anaesthesia or Transfusion or Contrast;
	}

	public static class HospitalConsentDecision
	{
		public const string Given = "Given";
		public const string Refused = "Refused";
		public static readonly IReadOnlyList<string> All = new[] { Given, Refused };
	}

	/// H113: what a photography consent allows, each wider than the one before (the GMC's three uses of a clinical
	/// photograph): the patient's own record; also teaching inside the hospital; also publication.
	public static class HospitalConsentPhotoScope
	{
		public const string Record = "Record";
		public const string Teaching = "Teaching";
		public const string Publication = "Publication";
		public static readonly IReadOnlyList<string> All = new[] { Record, Teaching, Publication };
		/// Whether a consent of scope `given` allows the use `need`.
		public static bool Covers(string? given, string need) => Rank(given) >= 0 && Rank(need) >= 0 && Rank(given) >= Rank(need);
		private static int Rank(string? s) => s switch { Record => 0, Teaching => 1, Publication => 2, _ => -1 };
	}

	public static class HospitalConsentSigner
	{
		public const string Patient = "Patient";
		public const string Representative = "Representative";
		public static readonly IReadOnlyList<string> All = new[] { Patient, Representative };
	}

	/// Why someone else signs for the patient.
	public static class HospitalConsentProxyReason
	{
		public const string Minor = "Minor";
		public const string Unconscious = "Unconscious";
		public const string NoCapacity = "NoCapacity";
		public const string Other = "Other";
		public static readonly IReadOnlyList<string> All = new[] { Minor, Unconscious, NoCapacity, Other };
	}

	public class HospitalConsentForm
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Kind { get; set; } = HospitalConsentKind.General;
		public string Title { get; set; } = "";
		public string TitleEn { get; set; } = "";
		public string Body { get; set; } = "";
		public string BodyEn { get; set; } = "";
		/// Raised each time the wording changes; a signed consent records the version it was signed on.
		public int Version { get; set; } = 1;
		public bool IsActive { get; set; } = true;
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public class HospitalConsent
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string ConsentNo { get; set; } = "";
		public int PatientId { get; set; }
		public int FormId { get; set; }
		public int FormVersion { get; set; }
		public string Kind { get; set; } = HospitalConsentKind.General;
		/// The form's title and wording as the signer read them, in the language they read ("ar" / "en").
		public string Title { get; set; } = "";
		public string Text { get; set; } = "";
		public string Language { get; set; } = "ar";
		public string? ProcedureName { get; set; }
		/// The risks and alternatives explained for this patient, beyond the form's wording.
		public string? Risks { get; set; }
		public int? SurgeryId { get; set; }
		public int? AdmissionId { get; set; }
		public string Decision { get; set; } = HospitalConsentDecision.Given;
		public string SignerKind { get; set; } = HospitalConsentSigner.Patient;
		public string SignerName { get; set; } = "";
		public string? SignerRelation { get; set; }
		public string? ProxyReason { get; set; }
		public string? Interpreter { get; set; }
		/// The clinician who explained it (the signed-in employee) and their practitioner record when they have one.
		public int ExplainedByEmployeeId { get; set; }
		public int? PractitionerId { get; set; }
		public int WitnessEmployeeId { get; set; }
		public DateTime SignedAt { get; set; }
		/// The signature picture on the document platform (null only for the instant between the row and the upload).
		public long? SignatureDocumentId { get; set; }
		public string? SignatureSha256 { get; set; }
		public DateTime? WithdrawnAt { get; set; }
		public int? WithdrawnByEmployeeId { get; set; }
		public string? WithdrawReason { get; set; }
		/// H113: a photography consent's scope (HospitalConsentPhotoScope); null for every other kind and for a refusal.
		public string? PhotoScope { get; set; }
	}

	public static class HospitalConsentModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalConsentForm>(e =>
			{
				e.ToTable("HospitalConsentForms");   // no DbSet: each names its table
				e.Property(x => x.Code).HasMaxLength(30);
				e.Property(x => x.Kind).HasMaxLength(20);
				e.Property(x => x.Title).HasMaxLength(200);
				e.Property(x => x.TitleEn).HasMaxLength(200);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalConsent>(e =>
			{
				e.ToTable("HospitalConsents");
				e.Property(x => x.ConsentNo).HasMaxLength(20);
				e.Property(x => x.Kind).HasMaxLength(20);
				e.Property(x => x.Title).HasMaxLength(200);
				e.Property(x => x.Language).HasMaxLength(2);
				e.Property(x => x.ProcedureName).HasMaxLength(300);
				e.Property(x => x.Risks).HasMaxLength(1000);
				e.Property(x => x.PhotoScope).HasMaxLength(12);
				e.Property(x => x.Decision).HasMaxLength(10);
				e.Property(x => x.SignerKind).HasMaxLength(20);
				e.Property(x => x.SignerName).HasMaxLength(200);
				e.Property(x => x.SignerRelation).HasMaxLength(60);
				e.Property(x => x.ProxyReason).HasMaxLength(20);
				e.Property(x => x.Interpreter).HasMaxLength(200);
				e.Property(x => x.SignatureSha256).HasMaxLength(64);
				e.Property(x => x.WithdrawReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.ConsentNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
				e.HasIndex(x => new { x.CompanyId, x.SurgeryId });
			});
		}
	}
}
