using System.Security.Cryptography;
using CrossBuy.BL.Documents;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H60 — informed consent with an electronic signature.
	//   · The FORMS are the hospital's wording (the administrator, `manage`). A standard set can be added in one step.
	//   · A clinician (`consult`) takes a consent on the patient's file: the form, what is done, the risks explained,
	//     who signs (the patient, or a representative with their relation and why), a witness who is not the
	//     clinician, and the decision — given or refused. The signer signs on the screen.
	//   · The signature picture goes to the document platform on the consent (HospitalConsent, files only), never to a
	//     module store; the row keeps its SHA-256. The text read is frozen into the row.
	//   · Never edited or deleted: a consent given is WITHDRAWN with a reason (a clinician or the administrator).
	// ==========================================================================================

	public sealed class HospitalConsentDocumentOwnerResolver(CrossDbContext db) : IDocumentOwnerResolver
	{
		public string EntityType => EntityRegistry.HospitalConsent;
		public Task<int?> OwningCompanyIdAsync(int entityId, CancellationToken cancellationToken = default)
			=> db.Set<HospitalConsent>().AsNoTracking().Where(c => c.ID == entityId && entityId > 0).Select(c => (int?)c.CompanyId).FirstOrDefaultAsync(cancellationToken);
		public PermissionTarget TargetFor(int entityId, int companyId) => PermissionTarget.ForEntity(EntityRegistry.HospitalConsent, entityId);
		public string ModuleActionFor(DocumentAction action) => action switch
		{
			DocumentAction.View or DocumentAction.Download => HospitalActions.Read,
			DocumentAction.Upload => HospitalActions.Consult,
			DocumentAction.Manage => HospitalActions.Manage,
			_ => "hospital-document-unsupported",   // a signature is never replaced or deleted
		};
	}

	public sealed class HospitalConsentFormInput
	{
		public string? Code { get; set; } public string? Kind { get; set; } public string? Title { get; set; } public string? TitleEn { get; set; }
		public string? Body { get; set; } public string? BodyEn { get; set; } public bool IsActive { get; set; } = true;
	}

	public sealed class HospitalConsentInput
	{
		public int? FormId { get; set; } public string? Language { get; set; } public string? ProcedureName { get; set; } public string? Risks { get; set; }
		public int? SurgeryId { get; set; } public int? DentalPhaseId { get; set; } public string? Decision { get; set; } public string? SignerKind { get; set; } public string? SignerName { get; set; }
		public string? SignerRelation { get; set; } public string? ProxyReason { get; set; } public string? Interpreter { get; set; } public int? WitnessEmployeeId { get; set; }
		/// The signature as the pad drew it: a PNG data URL.
		public string? Signature { get; set; }
		/// H113: what the photographs may be used for (a photography consent that is given).
		public string? PhotoScope { get; set; }
	}

	public sealed record HospitalConsentRow(HospitalConsent Consent, string ExplainedBy, string ExplainedByEn, string Witness, string WitnessEn, string? CaseNo, string? WithdrawnBy, string? WithdrawnByEn);
	public sealed record HospitalConsentCase(int Id, string CaseNo, string Name, string NameEn, DateTime CaseDate, int StartMinute);

	public static class HospitalConsentRules
	{
		/// The refusal when a case has no signed procedure consent (owner 2026-11-08: the checklist's "consent signed"
		/// cannot be ticked, and the operation cannot start, without one).
		public const string NoCaseConsent = "Take the patient's signed procedure consent for this operation first";

		/// The consents that cover a theatre case: a procedure consent, given, signed (its signature stored), not withdrawn.
		public static IQueryable<HospitalConsent> CoveringCase(IQueryable<HospitalConsent> all, int companyId, int surgeryId) =>
			all.Where(c => c.CompanyId == companyId && c.SurgeryId == surgeryId && c.Kind == HospitalConsentKind.Procedure
				&& c.Decision == HospitalConsentDecision.Given && c.WithdrawnAt == null && c.SignatureDocumentId != null);

		/// H113: the photography consents that allow a clinical photo of this patient: given, signed, not withdrawn.
		public static IQueryable<HospitalConsent> CoveringPhotos(IQueryable<HospitalConsent> all, int companyId, int patientId) =>
			all.Where(c => c.CompanyId == companyId && c.PatientId == patientId && c.Kind == HospitalConsentKind.Photography
				&& c.Decision == HospitalConsentDecision.Given && c.WithdrawnAt == null && c.SignatureDocumentId != null && c.PhotoScope != null)
			   .OrderByDescending(c => c.SignedAt);
		public const string NoPhotoConsent = "Record the patient's photography consent first";

		public const int MaxSignatureBytes = 512 * 1024, MaxBody = 20000, AdultAge = 18;
		public const string Metadata = "{\"hospital\":\"consent-signature\"}";   // the platform takes a JSON object

		public static string? FormRefusal(HospitalConsentFormInput i)
		{
			var code = i.Code?.Trim() ?? "";
			if (code.Length is 0 or > 30) return "Give the form a code of up to 30 characters";
			if (!HospitalConsentKind.All.Contains(i.Kind ?? "")) return "Choose the kind of consent";
			if (string.IsNullOrWhiteSpace(i.Title) || string.IsNullOrWhiteSpace(i.TitleEn)) return "Give the title in Arabic and English";
			if ((i.Title?.Trim().Length ?? 0) > 200 || (i.TitleEn?.Trim().Length ?? 0) > 200) return "A text is too long";
			if (string.IsNullOrWhiteSpace(i.Body) || string.IsNullOrWhiteSpace(i.BodyEn)) return "Write the wording in Arabic and English";
			if ((i.Body?.Length ?? 0) > MaxBody || (i.BodyEn?.Length ?? 0) > MaxBody) return "A text is too long";
			return null;
		}

		/// Why this consent cannot be taken (null = it can). `age` is the patient's age in years (null = unknown).
		public static string? SignRefusal(HospitalConsentInput i, HospitalConsentForm? form, int? age, bool deceased, int me)
		{
			if (deceased) return "This patient is deceased";
			if (form == null || !form.IsActive) return "Choose the consent form";
			if (i.Language is not ("ar" or "en")) return "Choose the language the signer read";
			if (HospitalConsentKind.NamesAProcedure(form.Kind) && string.IsNullOrWhiteSpace(i.ProcedureName)) return "Write the procedure the consent is for";
			if (!HospitalConsentDecision.All.Contains(i.Decision ?? "")) return "Choose whether consent is given or refused";
			if (!HospitalConsentSigner.All.Contains(i.SignerKind ?? "")) return "Choose who signs";
			if (i.SignerKind == HospitalConsentSigner.Patient && age is < AdultAge) return "A minor's consent is signed by a parent or guardian";
			if (i.SignerKind == HospitalConsentSigner.Representative)
			{
				if (string.IsNullOrWhiteSpace(i.SignerName) || string.IsNullOrWhiteSpace(i.SignerRelation)) return "Give the representative's name and relation to the patient";
				if (!HospitalConsentProxyReason.All.Contains(i.ProxyReason ?? "")) return "Choose why the representative signs";
			}
			if (form.Kind == HospitalConsentKind.Photography && i.Decision == HospitalConsentDecision.Given && !HospitalConsentPhotoScope.All.Contains(i.PhotoScope ?? ""))
				return "Choose what the photographs may be used for";
			if (i.WitnessEmployeeId is not int w || w <= 0) return "Choose the witness";
			if (w == me) return "The witness must be someone other than the clinician who explained";
			if ((i.ProcedureName?.Length ?? 0) > 300 || (i.Risks?.Length ?? 0) > 1000 || (i.SignerName?.Length ?? 0) > 200
				|| (i.SignerRelation?.Length ?? 0) > 60 || (i.Interpreter?.Length ?? 0) > 200) return "A text is too long";
			if (string.IsNullOrWhiteSpace(i.Signature)) return "Sign in the box";
			return null;
		}

		/// The PNG bytes of a "data:image/png;base64,…" URL, or null when it is not one (or too large).
		public static byte[]? SignaturePng(string? dataUrl)
		{
			const string head = "data:image/png;base64,";
			if (dataUrl == null || !dataUrl.StartsWith(head, StringComparison.Ordinal)) return null;
			if ((dataUrl.Length - head.Length) / 4 * 3 > MaxSignatureBytes) return null;
			byte[] bytes;
			try { bytes = Convert.FromBase64String(dataUrl[head.Length..]); } catch (FormatException) { return null; }
			bool png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
			return png && bytes.Length <= MaxSignatureBytes ? bytes : null;
		}

		public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

		/// The wording with this patient's facts written in: {patient} {mrn} {procedure} {doctor} {date}.
		public static string Fill(string body, string patient, string mrn, string? procedure, string doctor, DateTime dateWard) =>
			body.Replace("{patient}", patient).Replace("{mrn}", mrn).Replace("{procedure}", string.IsNullOrWhiteSpace(procedure) ? "—" : procedure.Trim())
				.Replace("{doctor}", doctor).Replace("{date}", dateWard.ToString("yyyy-MM-dd"));

		/// The hospital's starting set: one form per kind, in the plain words a patient reads before signing.
		public static IReadOnlyList<HospitalConsentFormInput> Standard() => new[]
		{
			F("GEN-01", HospitalConsentKind.General, "الموافقة العامة على العلاج", "General consent to treatment",
				"أقر أنا الموقع أدناه بموافقتي على تلقي الرعاية الطبية والتمريضية والفحوصات الروتينية اللازمة في المستشفى للمريض {patient} (رقم الملف {mrn}).\nأُحطت علماً بأن لي الحق في السؤال عن أي إجراء قبل إجرائه، وفي رفضه أو سحب موافقتي في أي وقت، وأن أي إجراء جراحي أو تخدير أو نقل دم يحتاج موافقة مستقلة.\nأوافق على حفظ بياناتي الطبية بسرية واستخدامها لأغراض العلاج فقط.",
				"I, the undersigned, agree to receive the medical and nursing care and the routine investigations needed in this hospital for the patient {patient} (file {mrn}).\nI have been told that I may ask about any procedure before it is done, refuse it, or withdraw my consent at any time, and that any operation, anaesthetic or transfusion needs a separate consent.\nI agree that my medical information is kept confidential and used only for my care."),
			F("PROC-01", HospitalConsentKind.Procedure, "الموافقة على إجراء طبي أو جراحي", "Consent to a medical or surgical procedure",
				"أوافق على إجراء: {procedure} للمريض {patient} (رقم الملف {mrn}) على يد د. {doctor} والفريق الطبي.\nشُرح لي بلغة أفهمها طبيعة الإجراء والغرض منه، وفوائده المتوقعة، ومخاطره ومضاعفاته المحتملة، والبدائل المتاحة بما فيها عدم إجرائه، وأُتيحت لي الفرصة لطرح الأسئلة وأُجيب عنها.\nأوافق على ما قد يلزم من إجراءات إضافية طارئة أثناء العملية لإنقاذ الحياة أو لتفادي ضرر جسيم.",
				"I agree to the procedure: {procedure} for the patient {patient} (file {mrn}), performed by Dr {doctor} and the medical team.\nThe nature and purpose of the procedure, its expected benefits, its risks and possible complications, and the alternatives — including not having it — were explained to me in a language I understand, and my questions were answered.\nI agree to any further procedure that becomes urgently necessary during the operation to save life or prevent serious harm."),
			F("ANES-01", HospitalConsentKind.Anaesthesia, "الموافقة على التخدير", "Consent to anaesthesia",
				"أوافق على التخدير اللازم لإجراء: {procedure} للمريض {patient} (رقم الملف {mrn}).\nشُرح لي نوع التخدير المقترح (عام، نصفي، موضعي أو تهدئة) وبدائله، والمخاطر الشائعة مثل الغثيان والتهاب الحلق، والمخاطر النادرة الجسيمة مثل الحساسية من الأدوية ومضاعفات التنفس والقلب.\nأقررت بصحة ما ذكرته عن حالتي الصحية وأدويتي وحساسيتي وآخر وجبة تناولتها.",
				"I agree to the anaesthetic needed for: {procedure} for the patient {patient} (file {mrn}).\nThe proposed type of anaesthesia (general, regional, local or sedation) and its alternatives were explained to me, with the common risks such as nausea and a sore throat, and the rare serious risks such as drug allergy and breathing or heart complications.\nWhat I have said about my health, my medicines, my allergies and my last meal is true."),
			F("TRANS-01", HospitalConsentKind.Transfusion, "الموافقة على نقل الدم ومشتقاته", "Consent to blood transfusion",
				"أوافق على نقل الدم أو مشتقاته للمريض {patient} (رقم الملف {mrn}) لأجل: {procedure}.\nشُرح لي سبب الحاجة إلى النقل، وأن الدم يُفحص ويُطابق قبل النقل، والمخاطر المحتملة مثل التفاعلات التحسسية والحمى، والمخاطر النادرة لانتقال العدوى، والبدائل المتاحة إن وُجدت.",
				"I agree to the transfusion of blood or blood products to the patient {patient} (file {mrn}) for: {procedure}.\nWhy a transfusion is needed was explained to me, that the blood is tested and cross-matched before it is given, the possible risks such as allergic reactions and fever, the rare risk of infection, and the alternatives where there are any."),
			F("CONT-01", HospitalConsentKind.Contrast, "الموافقة على الصبغة في الأشعة", "Consent to contrast imaging",
				"أوافق على حقن صبغة الأشعة لإجراء: {procedure} للمريض {patient} (رقم الملف {mrn}).\nشُرح لي أن الصبغة قد تسبب إحساساً بالدفء أو طعماً معدنياً، ونادراً حساسية أو تأثيراً على الكلى، وأقررت بما أعرفه عن حساسيتي ووظائف الكلى والحمل.",
				"I agree to the injection of contrast for: {procedure} for the patient {patient} (file {mrn}).\nIt was explained to me that contrast may cause a warm feeling or a metallic taste and, rarely, an allergic reaction or an effect on the kidneys. What I have said about my allergies, my kidney function and pregnancy is true."),
			F("PHOTO-01", HospitalConsentKind.Photography, "الموافقة على التصوير الطبي", "Consent to clinical photography",
				"أوافق على التقاط صور طبية للمريض {patient} (رقم الملف {mrn}) لتوثيق الحالة في الملف الطبي.\nتُحفظ الصور في ملف المريض بسرية، ولا تُستخدم في التعليم أو النشر إلا بموافقة مكتوبة مستقلة، ولي أن أسحب هذه الموافقة في أي وقت.",
				"I agree to clinical photographs of the patient {patient} (file {mrn}) being taken to document the condition in the medical record.\nThe pictures are kept confidential in the patient's file and are not used for teaching or publication without a separate written consent. I may withdraw this consent at any time."),
			F("AMA-01", HospitalConsentKind.AgainstAdvice, "الخروج على مسؤولية المريض", "Leaving against medical advice",
				"أقر أنا الموقع أدناه بأنني أرغب في مغادرة المستشفى بالمريض {patient} (رقم الملف {mrn}) رغم نصيحة الطبيب د. {doctor} بالبقاء للعلاج.\nشُرحت لي المخاطر المحتملة لهذا القرار على صحة المريض بما فيها تدهور الحالة أو الوفاة، وأتحمل كامل المسؤولية عنه، وأعلم أنه يمكنني العودة في أي وقت.",
				"I, the undersigned, wish to leave the hospital with the patient {patient} (file {mrn}) although Dr {doctor} has advised staying for treatment.\nThe possible risks of this decision to the patient's health, including worsening or death, were explained to me. I take full responsibility for it and know that I may come back at any time."),
		};

		private static HospitalConsentFormInput F(string code, string kind, string title, string titleEn, string body, string bodyEn) =>
			new() { Code = code, Kind = kind, Title = title, TitleEn = titleEn, Body = body, BodyEn = bodyEn, IsActive = true };
	}

	public sealed class HospitalConsentService
	{
		public const string NoTables = "Consents are not installed yet (hospital_h60_consents.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IPlatformDocumentService _docs;
		public HospitalConsentService(CrossDbContext db, IHospitalAccessService access, IPlatformDocumentService docs) { _db = db; _access = access; _docs = docs; }
		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalConsent> Consents(BusinessContext ctx) => _db.Set<HospitalConsent>().Where(c => c.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalConsentForm> Forms(BusinessContext ctx) => _db.Set<HospitalConsentForm>().Where(f => f.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private static string PatientEn(HospitalPatient p) => string.IsNullOrWhiteSpace(p.NameEn) ? p.Name : p.NameEn;
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;

		// ---------------- forms ----------------

		/// The forms, by code. `Ready` false = no H60 tables.
		public async Task<(IReadOnlyList<HospitalConsentForm> Forms, bool Ready)> FormsAsync(BusinessContext ctx, bool activeOnly, CancellationToken ct = default)
		{
			try { return (await Forms(ctx).AsNoTracking().Where(f => !activeOnly || f.IsActive).OrderBy(f => f.Code).ToListAsync(ct), true); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalConsentForm>(), false); }
		}

		/// Adds or changes a form. A change of wording raises its version; signed consents keep the text they were signed on.
		public async Task<HospitalResult> SaveFormAsync(BusinessContext ctx, int? id, HospitalConsentFormInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalConsentRules.FormRefusal(input) is string why) return HospitalResult.Fail(why);
			var code = input.Code!.Trim().ToUpperInvariant();
			try
			{
				if (await Forms(ctx).AnyAsync(f => f.Code == code && f.ID != (id ?? 0), ct)) return HospitalResult.Fail("Another form has this code");
				HospitalConsentForm row;
				if (id is int fid)
				{
					var found = await Forms(ctx).FirstOrDefaultAsync(f => f.ID == fid, ct);
					if (found == null) return HospitalResult.Fail("not_found");
					row = found;
					if (row.Body != input.Body!.Trim() || row.BodyEn != input.BodyEn!.Trim() || row.Title != input.Title!.Trim() || row.TitleEn != input.TitleEn!.Trim()) row.Version++;
				}
				else { row = new HospitalConsentForm { CompanyId = ctx.CompanyId }; _db.Add(row); }
				row.Code = code; row.Kind = input.Kind!; row.Title = input.Title!.Trim(); row.TitleEn = input.TitleEn!.Trim();
				row.Body = input.Body!.Trim(); row.BodyEn = input.BodyEn!.Trim(); row.IsActive = input.IsActive; row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail("Another form has this code"); }
		}

		/// Adds the standard forms whose codes the hospital does not have yet. Args = how many were added.
		public async Task<HospitalResult> AddStandardAsync(BusinessContext ctx, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var have = await Forms(ctx).Select(f => f.Code).ToListAsync(ct);
				var add = HospitalConsentRules.Standard().Where(s => !have.Contains(s.Code!)).ToList();
				foreach (var s in add)
					_db.Add(new HospitalConsentForm { CompanyId = ctx.CompanyId, Code = s.Code!, Kind = s.Kind!, Title = s.Title!, TitleEn = s.TitleEn!, Body = s.Body!, BodyEn = s.BodyEn!, IsActive = true, Version = 1, UpdatedAt = nowUtc, UpdatedByEmployeeId = employeeId });
				await _db.SaveChangesAsync(ct);
				return new HospitalResult(true, null, null, new object[] { add.Count });
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		// ---------------- consents ----------------

		/// The patient's consents, newest first (only those whose signature is stored). `Ready` false = no H60 tables.
		public async Task<(IReadOnlyList<HospitalConsentRow> Rows, bool Ready)> ListAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
		{
			try
			{
				var rows = await Consents(ctx).AsNoTracking().Where(c => c.PatientId == patientId && c.SignatureDocumentId != null).OrderByDescending(c => c.ID).ToListAsync(ct);
				var eids = rows.SelectMany(r => new[] { (int?)r.ExplainedByEmployeeId, r.WitnessEmployeeId, r.WithdrawnByEmployeeId }).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, ct);
				var sids = rows.Where(r => r.SurgeryId != null).Select(r => r.SurgeryId!.Value).Distinct().ToList();
				var cases = await _db.Set<HospitalSurgery>().AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && sids.Contains(s.ID)).ToDictionaryAsync(s => s.ID, s => s.CaseNo, ct);
				(string, string) Who(int? id) => id is int i && people.TryGetValue(i, out var p) ? (p.FullName ?? "", p.FullNameEn ?? p.FullName ?? "") : ("", "");
				return (rows.Select(r =>
				{
					var (x, xe) = Who(r.ExplainedByEmployeeId); var (w, we) = Who(r.WitnessEmployeeId); var (d, de) = Who(r.WithdrawnByEmployeeId);
					return new HospitalConsentRow(r, x, xe, w, we, r.SurgeryId is int s ? cases.GetValueOrDefault(s) : null, r.WithdrawnAt == null ? null : d, r.WithdrawnAt == null ? null : de);
				}).ToList(), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalConsentRow>(), false); }
		}

		/// The patient's theatre cases a consent can be for: planned or in progress.
		public Task<List<HospitalConsentCase>> CasesAsync(BusinessContext ctx, int patientId, CancellationToken ct = default) =>
			_db.Set<HospitalSurgery>().AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.PatientId == patientId && (s.Status == HospitalSurgeryStatus.Planned || s.Status == HospitalSurgeryStatus.InProgress))
				.OrderBy(s => s.CaseDate).ThenBy(s => s.StartMinute).Select(s => new HospitalConsentCase(s.ID, s.CaseNo, s.Name, s.NameEn, s.CaseDate, s.StartMinute)).ToListAsync(ct);

		/// Takes a signed consent (or a signed refusal). The row is written, the signature is stored on the document
		/// platform against it, then the row is stamped with the document and its fingerprint; if the signature cannot
		/// be stored the row is removed, so a consent never exists without its signature.
		public async Task<HospitalResult> SignAsync(BusinessContext ctx, int patientId, HospitalConsentInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			var p = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == patientId && x.CompanyId == ctx.CompanyId && x.MergedIntoPatientId == null, ct);
			if (p == null) return HospitalResult.Fail("not_found");
			HospitalConsentForm? form;
			try { form = input.FormId is int fid ? await Forms(ctx).AsNoTracking().FirstOrDefaultAsync(f => f.ID == fid, ct) : null; }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
			int? age = HospitalPatientService.AgeOn(p.BirthDate, nowUtc);
			if (HospitalConsentRules.SignRefusal(input, form, age, p.DeceasedAt != null, me) is string why) return HospitalResult.Fail(why);
			var png = HospitalConsentRules.SignaturePng(input.Signature);
			if (png == null) return HospitalResult.Fail("Sign in the box");
			if (!await _db.Employee.AnyAsync(e => e.ID == input.WitnessEmployeeId && e.EmpCompanyID == ctx.CompanyId && e.IsActive, ct)) return HospitalResult.Fail("Choose the witness");
			if (input.SurgeryId is int sid && !await _db.Set<HospitalSurgery>().AnyAsync(s => s.ID == sid && s.CompanyId == ctx.CompanyId && s.PatientId == patientId
				&& (s.Status == HospitalSurgeryStatus.Planned || s.Status == HospitalSurgeryStatus.InProgress), ct)) return HospitalResult.Fail("not_found");
			if (input.DentalPhaseId != null && form!.Kind != HospitalConsentKind.Procedure) return HospitalResult.Fail("A treatment consent uses a procedure consent form");
			// H179: a dental phase of one of this patient's plans
			if (input.DentalPhaseId is int dph && !await (from ph in _db.Set<HospitalDentalPlanPhase>() join pl in _db.Set<HospitalDentalPlan>() on ph.PlanId equals pl.ID
					where ph.ID == dph && ph.CompanyId == ctx.CompanyId && pl.PatientId == patientId select ph.ID).AnyAsync(ct)) return HospitalResult.Fail("not_found");

			var doctor = await _db.HospitalPractitioners.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.EmployeeId == me && x.IsActive).Select(x => new { x.ID, x.Name, x.NameEn }).FirstOrDefaultAsync(ct);
			var meName = await _db.Employee.AsNoTracking().Where(e => e.ID == me).Select(e => new { e.FullName, e.FullNameEn }).FirstOrDefaultAsync(ct);
			bool en = input.Language == "en";
			string doctorName = en ? (doctor?.NameEn ?? meName?.FullNameEn ?? meName?.FullName ?? "") : (doctor?.Name ?? meName?.FullName ?? "");
			var admission = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.PatientId == patientId && a.DischargedAt == null).Select(a => (int?)a.ID).FirstOrDefaultAsync(ct);
			bool proxy = input.SignerKind == HospitalConsentSigner.Representative;
			var text = HospitalConsentRules.Fill(en ? form!.BodyEn : form!.Body, en ? PatientEn(p) : p.Name, p.Mrn, input.ProcedureName, doctorName, HospitalNursingService.UtcToWard(nowUtc));

			HospitalConsent? row = null;
			try
			{
				for (int attempt = 0; attempt < 3 && row == null; attempt++)
				{
					var nos = await Consents(ctx).Select(c => c.ConsentNo).ToListAsync(ct);
					var r = new HospitalConsent
					{
						CompanyId = ctx.CompanyId, ConsentNo = HospitalDentalPracticeRules.NextNo(nos, "CF-", 6), PatientId = patientId, FormId = form.ID, FormVersion = form.Version, Kind = form.Kind,
						Title = en ? form.TitleEn : form.Title, Text = text, Language = input.Language!, ProcedureName = Clip(input.ProcedureName, 300), Risks = Clip(input.Risks, 1000),
						SurgeryId = input.SurgeryId, DentalPhaseId = input.DentalPhaseId, AdmissionId = admission, Decision = input.Decision!, SignerKind = input.SignerKind!,
						SignerName = proxy ? Clip(input.SignerName, 200)! : (en ? PatientEn(p) : p.Name), SignerRelation = proxy ? Clip(input.SignerRelation, 60) : null,
						ProxyReason = proxy ? input.ProxyReason : null, Interpreter = Clip(input.Interpreter, 200), ExplainedByEmployeeId = me, PractitionerId = doctor?.ID,
						WitnessEmployeeId = input.WitnessEmployeeId!.Value, SignedAt = nowUtc,
						PhotoScope = form.Kind == HospitalConsentKind.Photography && input.Decision == HospitalConsentDecision.Given ? input.PhotoScope : null,
					};
					_db.Add(r);
					try { await _db.SaveChangesAsync(ct); row = r; }
					catch (DbUpdateException) { _db.ChangeTracker.Clear(); }   // the number was taken a moment ago: the next one
				}
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			if (row == null) return HospitalResult.Fail(HospitalNursingService.ConcurrentChange);
			_db.ChangeTracker.Clear();

			var doc = await _docs.UploadAsync(new DocumentUploadRequest
			{
				EntityType = EntityRegistry.HospitalConsent, EntityId = row.ID, FileName = row.ConsentNo + "-signature.png", ContentType = "image/png",
				Metadata = HospitalConsentRules.Metadata, Reason = "Consent signature",
			}, new MemoryStream(png), ct);   // the platform authorises `consult` on this consent and keeps the bytes
			if (!doc.Ok)
			{
				await Consents(ctx).Where(c => c.ID == row.ID && c.SignatureDocumentId == null).ExecuteDeleteAsync(ct);
				return HospitalResult.Fail(doc.ReasonCode is "store_failed" ? "The change could not be saved" : "forbidden");
			}
			var sha = HospitalConsentRules.Sha256(png);
			await Consents(ctx).Where(c => c.ID == row.ID).ExecuteUpdateAsync(u => u.SetProperty(c => c.SignatureDocumentId, doc.DocumentId).SetProperty(c => c.SignatureSha256, sha), ct);
			return HospitalResult.Success(row.ID);
		}

		/// Withdraws a consent that was given (a clinician or the administrator), with the reason. It stays on the file.
		public async Task<HospitalResult> WithdrawAsync(BusinessContext ctx, int id, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct) && !await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(reason)) return HospitalResult.Fail("Write the reason");
			try
			{
				var c = await Consents(ctx).AsNoTracking().Where(x => x.ID == id && x.SignatureDocumentId != null).Select(x => new { x.Decision, x.WithdrawnAt, x.SurgeryId }).FirstOrDefaultAsync(ct);
				if (c == null) return HospitalResult.Fail("not_found");
				if (c.Decision != HospitalConsentDecision.Given) return HospitalResult.Fail("Only a consent given can be withdrawn");
				int n = await Consents(ctx).Where(x => x.ID == id && x.WithdrawnAt == null).ExecuteUpdateAsync(u => u.SetProperty(x => x.WithdrawnAt, nowUtc)
					.SetProperty(x => x.WithdrawnByEmployeeId, employeeId).SetProperty(x => x.WithdrawReason, Clip(reason, 300)), ct);
				if (n != 1) return HospitalResult.Fail("This consent is already withdrawn");
				// the case it covered (still planned) loses its "consent signed" tick unless another consent covers it
				if (c.SurgeryId is int sid && !await HospitalConsentRules.CoveringCase(_db.Set<HospitalConsent>(), ctx.CompanyId, sid).AnyAsync(ct))
					await _db.Set<HospitalSurgeryCheck>().Where(k => k.CompanyId == ctx.CompanyId && k.SurgeryId == sid && k.Item == HospitalSurgeryChecklist.Consent
						&& _db.Set<HospitalSurgery>().Any(s => s.ID == sid && s.CompanyId == ctx.CompanyId && s.Status == HospitalSurgeryStatus.Planned)).ExecuteDeleteAsync(ct);
				return HospitalResult.Success(id);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The signature picture of a consent of this company (the platform authorises the reader on the consent).
		public async Task<DocumentContent?> SignatureAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			try
			{
				var docId = await Consents(ctx).AsNoTracking().Where(c => c.ID == id).Select(c => c.SignatureDocumentId).FirstOrDefaultAsync(ct);
				return docId is long d ? await _docs.OpenCurrentAsync(d, ct) : null;
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}
	}
}
