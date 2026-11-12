using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H94 — THE SPECIALTY VISIT (the approved prototype screen sc-im). One visit, one screen beside the
	// consultation: the clinic's templates filled as one form; the figures on top and their trends — read only from the
	// observation rows; the care gaps and alerts; the KDIGO kidney stage; and the order sets the visit's diagnoses
	// propose. It has ONE signature with the consultation: signing either signs both — the forms are checked first, so a
	// visit is never signed while one of its forms could not be.
	//   * a follow-up opens with the templates the patient was seen on before; any published template of the clinic
	//     (or of every clinic) can be added;
	//   * "last lab" fields are filled from the last verified result (and say so); a measured value is NEVER carried
	//     from an earlier visit — its last value is shown beside the field, not put in it;
	//   * order-set lines join the consultation's draft through the consultation's own rules: tests and imaging as
	//     orders, medicines as prescription lines, tasks as lines of the plan.
	// ==========================================================================================
	public sealed record HospitalVisitForm(HospitalClinicalTemplate Template, HospitalClinicalTemplateVersion Version, HospitalClinicalForm Form,
		IReadOnlyList<HospitalTemplateFieldView> Fields, IReadOnlyDictionary<string, string> Answers);
	public sealed record HospitalVisitHeadline(HospitalConcept Concept, HospitalClinicalTemplateField Field, HospitalObsPoint? Last, HospitalObsPoint? Previous, IReadOnlyList<HospitalObsPoint> Points);
	public sealed record HospitalVisitKdigo(string G, string A, string Risk, DateTime At);
	public sealed record HospitalVisitOrderSet(HospitalOrderSet Set, IReadOnlyList<HospitalOrderSetItem> Items);
	public sealed record HospitalVisitDone(HospitalConcept Concept, DateTime At, string? Value);
	public sealed record HospitalVisitModel(HospitalEncounterView Visit, HospitalPatient Patient, HospitalPractitioner? Doctor, HospitalDepartment? Clinic,
		IReadOnlyList<HospitalVisitForm> Forms, IReadOnlyList<HospitalClinicalTemplate> Offer, IReadOnlyDictionary<int, HospitalObsPoint> LastByConcept,
		IReadOnlyDictionary<int, HospitalPrefill> Prefill, IReadOnlyList<HospitalVisitHeadline> Headlines, IReadOnlyList<HospitalCareItem> Care,
		IReadOnlyList<HospitalVisitDone> Done, HospitalVisitKdigo? Kdigo, IReadOnlyList<HospitalVisitOrderSet> OrderSets, IReadOnlyDictionary<int, int> GapServices, bool CanEdit, IReadOnlyDictionary<int, HospitalService> Services);

	/// One visit on a specialty clinic's list.
	public sealed record HospitalClinicVisitRow(int EncounterId, DateTime StartedAt, string Status, int PatientId, string Name, string NameEn, string Mrn, string? Photo, string Sex,
		DateTime? BirthDate, string Doctor, string DoctorEn, int Forms, int SignedForms, string? Complaint, string? DoctorPhoto = null);

	public static class HospitalVisitRules
	{
		/// The KDIGO cell's advice (the prototype's sentence for "high"; KDIGO 2024 monitoring for the others).
		public static string AdviceKey(string risk) => "kdigo:" + risk;

		/// A value written on a published form falls outside its concept's normal range.
		public static bool OutOfRange(HospitalConcept c, string? raw)
		{
			if (string.IsNullOrWhiteSpace(raw)) return false;
			var (v, v2, _, err) = HospitalFormRules.Parse(c, raw);
			return err == null && v != null && HospitalObsFlag.Abnormal(HospitalConceptRules.Flag(c, v, v2));
		}

		/// The consultation as it stands, as the input its own save takes — so an order set or a gap's order joins the
		/// draft through the consultation's rules, never around them.
		public static HospitalEncounterInput InputOf(HospitalEncounterView v) => new()
		{
			ChiefComplaint = v.Encounter.ChiefComplaint, History = v.Encounter.History, Examination = v.Encounter.Examination, PlanNote = v.Encounter.PlanNote,
			BpSystolic = v.Encounter.BpSystolic, BpDiastolic = v.Encounter.BpDiastolic, HeartRate = v.Encounter.HeartRate, TemperatureC = v.Encounter.TemperatureC,
			SpO2 = v.Encounter.SpO2, WeightKg = v.Encounter.WeightKg, HeightCm = v.Encounter.HeightCm, FollowUpDays = v.Encounter.FollowUpDays, ReferToDepartmentId = v.Encounter.ReferToDepartmentId,
			Diagnoses = v.Diagnoses.Select(d => new HospitalDiagnosisInput { Code = d.Code, Primary = d.IsPrimary }).ToList(),
			Prescription = v.Prescription.Select(r => new HospitalPrescriptionInput { Drug = r.Drug, ItemId = r.ItemId, Instructions = r.Instructions, InstructionsEn = r.InstructionsEn, Quantity = r.Quantity }).ToList(),
			OrderServiceIds = (v.Orders ?? Array.Empty<HospitalEncounterOrder>()).Select(o => o.ServiceId).ToList(),
		};

		/// Adds the chosen order-set lines to the input: a test or an exam as an order (once), a medicine as a
		/// prescription line (once per drug), a task as a line of the plan. Returns how many were added.
		public static int Apply(HospitalEncounterInput input, IEnumerable<HospitalOrderSetItem> items)
		{
			int n = 0;
			foreach (var it in items)
			{
				if (it.Kind is HospitalOrderSetItemKind.Lab or HospitalOrderSetItemKind.Imaging)
				{
					if (it.ServiceId is int s && !input.OrderServiceIds.Contains(s)) { input.OrderServiceIds.Add(s); n++; }
				}
				else if (it.Kind == HospitalOrderSetItemKind.Medication)
				{
					var drug = (it.Drug ?? "").Trim();
					if (drug.Length == 0 || input.Prescription.Any(r => string.Equals(r.Drug.Trim(), drug, StringComparison.OrdinalIgnoreCase))) continue;
					var how = string.Join(" ", new[] { it.Dose, it.Route, it.Hours, it.AsNeeded ? "PRN" : null, it.Note }.Where(x => !string.IsNullOrWhiteSpace(x)));
					input.Prescription.Add(new HospitalPrescriptionInput { Drug = drug, Instructions = how.Length == 0 ? null : how, Quantity = 1 }); n++;
				}
				else
				{
					var line = "• " + ((it.TitleAr ?? it.TitleEn ?? "").Trim());
					if (line.Length > 2 && !(input.PlanNote ?? "").Contains(line, StringComparison.Ordinal)) { input.PlanNote = string.IsNullOrWhiteSpace(input.PlanNote) ? line : input.PlanNote.TrimEnd() + "\n" + line; n++; }
				}
			}
			return n;
		}

		/// The diagnoses to add to the patient's long-term problems: the visit's, not already listed (one per line).
		public static string? WithProblems(string? current, IEnumerable<string> names)
		{
			var lines = (current ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
			foreach (var n in names.Select(x => x.Trim()).Where(x => x.Length > 0))
				if (!lines.Contains(n, StringComparer.OrdinalIgnoreCase)) lines.Add(n);
			var s = string.Join("\n", lines);
			return s.Length == 0 ? null : s;
		}
	}

	public sealed class HospitalSpecialtyVisitService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalSpecialtyVisitService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private IQueryable<T> Q<T>(BusinessContext ctx) where T : class => _db.Set<T>().Where(x => EF.Property<int>(x, "CompanyId") == ctx.CompanyId);
		private HospitalClinicalFormService Forms => new(_db, _access);
		private HospitalClinicalContentService Content => new(_db, _access);
		private HospitalCanvasService Canvas => new(_db, _access);   // H99: the visit's drawings share its signature

		private async Task<int?> ClinicOfAsync(BusinessContext ctx, HospitalEncounter e, CancellationToken ct) =>
			await Q<HospitalPractitioner>(ctx).Where(p => p.ID == e.PractitionerId).Select(p => p.DepartmentId).FirstOrDefaultAsync(ct);

		/// The published templates this visit can use: the clinic's and every clinic's.
		private async Task<List<(HospitalClinicalTemplate T, HospitalClinicalTemplateVersion V)>> UsableAsync(BusinessContext ctx, int? clinic, CancellationToken ct) =>
			(await (from t in Q<HospitalClinicalTemplate>(ctx)
					join v in Q<HospitalClinicalTemplateVersion>(ctx) on t.ID equals v.TemplateId
					where t.IsActive && v.Status == HospitalTemplateStatus.Published && (t.DepartmentId == null || t.DepartmentId == clinic)
					orderby t.ID
					select new { t, v }).ToListAsync(ct)).Select(x => (x.t, x.v)).ToList();

		public async Task<HospitalVisitModel?> VisitAsync(BusinessContext ctx, HospitalConsultationService consult, int encounterId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var visit = await consult.GetAsync(ctx, encounterId, ct);
			if (visit == null) return null;
			var e = visit.Encounter;
			var patient = await Q<HospitalPatient>(ctx).AsNoTracking().FirstAsync(p => p.ID == e.PatientId, ct);
			var doctor = await Q<HospitalPractitioner>(ctx).AsNoTracking().FirstOrDefaultAsync(p => p.ID == e.PractitionerId, ct);
			var clinic = doctor?.DepartmentId is int d ? await Q<HospitalDepartment>(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == d, ct) : null;
			bool canEdit = e.Status == HospitalEncounterStatus.Draft && await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			var usable = await UsableAsync(ctx, clinic?.ID, ct);

			// A follow-up starts with the templates the patient was seen on before (their current published version).
			var forms = await Q<HospitalClinicalForm>(ctx).Where(f => f.EncounterId == e.ID).OrderBy(f => f.ID).ToListAsync(ct);
			if (forms.Count == 0 && canEdit)
			{
				var before = await Q<HospitalClinicalForm>(ctx).Where(f => f.PatientId == e.PatientId && f.Status == HospitalFormStatus.Signed && f.EncounterId != e.ID)
					.Join(Q<HospitalClinicalTemplateVersion>(ctx), f => f.TemplateVersionId, v => v.ID, (f, v) => v.TemplateId).Distinct().ToListAsync(ct);
				foreach (var (t, v) in usable.Where(u => before.Contains(u.T.ID)))
					await Forms.SaveAsync(ctx, e.ID, v.ID, new Dictionary<string, string>(), employeeId, nowUtc, ct);
				forms = await Q<HospitalClinicalForm>(ctx).Where(f => f.EncounterId == e.ID).OrderBy(f => f.ID).ToListAsync(ct);
			}
			var versions = await Q<HospitalClinicalTemplateVersion>(ctx).Where(v => forms.Select(f => f.TemplateVersionId).Contains(v.ID)).ToDictionaryAsync(v => v.ID, ct);
			var templates = await Q<HospitalClinicalTemplate>(ctx).Where(t => versions.Values.Select(v => v.TemplateId).Contains(t.ID)).ToDictionaryAsync(t => t.ID, ct);
			var concepts = await Q<HospitalConcept>(ctx).ToListAsync(ct);
			var vf = new List<HospitalVisitForm>();
			foreach (var f in forms)
			{
				var v = versions[f.TemplateVersionId];
				vf.Add(new HospitalVisitForm(templates[v.TemplateId], v, f, await Content.FieldViewsAsync(ctx, v.ID, concepts, ct), HospitalFormRules.Answers(f.AnswersJson)));
			}
			var offer = usable.Where(u => !vf.Any(x => x.Template.ID == u.T.ID)).Select(u => u.T).ToList();

			// the last value of every concept on the screen, and what the lab gives the "last lab" fields
			var cids = vf.SelectMany(x => x.Fields).Select(f => f.Concept.ID).Distinct().ToList();
			var obs = await Q<HospitalClinicalObservation>(ctx).Where(o => o.PatientId == e.PatientId && cids.Contains(o.ConceptId)).OrderBy(o => o.ObservedAt).ThenBy(o => o.ID).ToListAsync(ct);
			HospitalObsPoint P(HospitalClinicalObservation o) => new(o.ObservedAt, o.Value, o.Value2, o.ValueCode, o.Side, o.Flag, o.Unit);
			var last = obs.Where(o => o.Side == null).GroupBy(o => o.ConceptId).ToDictionary(g => g.Key, g => P(g.Last()));
			var prefill = new Dictionary<int, HospitalPrefill>();
			foreach (var x in vf.Where(x => x.Form.Status == HospitalFormStatus.Draft))
				foreach (var p in await Forms.PrefillAsync(ctx, e.PatientId, x.Version.ID, nowUtc, ct)) prefill[p.FieldId] = p;

			// the figures on top and their trends: the fields marked to head the visit
			var heads = new List<HospitalVisitHeadline>();
			foreach (var fv in vf.SelectMany(x => x.Fields).Where(f => f.Field.ShowTrend).GroupBy(f => f.Concept.ID).Select(g => g.First()).Take(3))
			{
				// a side concept (an eye's pressure) trends each side; the others their single value
				var pts = obs.Where(o => o.ConceptId == fv.Concept.ID && (fv.Concept.HasLaterality || o.Side == null)).Select(P).ToList();
				heads.Add(new HospitalVisitHeadline(fv.Concept, fv.Field, pts.LastOrDefault(), pts.Count > 1 ? pts[^2] : null, pts));
			}

			var care = await Forms.CareAsync(ctx, e.PatientId, nowUtc, ct, vf.Select(x => x.Template.ID).ToList());
			// care fields that are up to date ("done") — the reassuring line under the gaps
			var careFields = vf.SelectMany(x => x.Fields).Where(f => f.Field.CareGapDays != null).ToList();
			var done = new List<HospitalVisitDone>();
			foreach (var f in careFields.GroupBy(f => f.Concept.ID).Select(g => g.First()))
			{
				var lastAny = await Q<HospitalClinicalObservation>(ctx).Where(o => o.PatientId == e.PatientId && o.ConceptId == f.Concept.ID).OrderByDescending(o => o.ObservedAt).FirstOrDefaultAsync(ct);
				if (lastAny != null && lastAny.ObservedAt >= nowUtc.AddDays(-f.Field.CareGapDays!.Value))
					done.Add(new HospitalVisitDone(f.Concept, lastAny.ObservedAt, lastAny.Value is decimal dv ? HospitalConceptRules.Num(dv, f.Concept.Decimals) + (lastAny.Value2 is decimal d2 ? "/" + HospitalConceptRules.Num(d2, f.Concept.Decimals) : "") : lastAny.ValueCode));
			}
			// a gap on a lab-linked concept can be ordered from the card
			var gapServices = new Dictionary<int, int>();
			foreach (var g in care.Where(c => c.Kind == "Gap" && c.Concept.LabAnalyteCode != null))
			{
				var svc = await Q<HospitalLabAnalyte>(ctx).Where(a => a.Code == g.Concept.LabAnalyteCode).Select(a => (int?)a.ServiceId).FirstOrDefaultAsync(ct);
				if (svc is int s) gapServices[g.Concept.ID] = s;
			}

			// the kidney stage: the newest KDIGO category recorded on any template of the screen
			HospitalVisitKdigo? kd = null;
			var kdc = vf.SelectMany(x => x.Fields).FirstOrDefault(f => f.Concept.Calculator == HospitalConceptCalculator.Kdigo)?.Concept;
			if (kdc != null)
			{
				var k = obs.LastOrDefault(o => o.ConceptId == kdc.ID && o.ValueCode != null);
				var parts = k?.ValueCode?.Split(' ');
				if (k != null && parts!.Length == 2 && HospitalClinicalCalc.KdigoRisk(parts[0], parts[1]) is string risk) kd = new HospitalVisitKdigo(parts[0], parts[1], risk, k.ObservedAt);
			}

			// order sets the visit's diagnoses propose
			var codes = visit.Diagnoses.Select(x => x.Code).ToList();
			var sets = await Q<HospitalOrderSet>(ctx).Where(s => s.IsActive && s.DiagnosisCodes != null).ToListAsync(ct);
			var proposed = sets.Where(s => HospitalOrderSetRules.Proposes(s.DiagnosisCodes, codes)).ToList();
			var items = await Q<HospitalOrderSetItem>(ctx).Where(i => proposed.Select(s => s.ID).Contains(i.SetId)).OrderBy(i => i.SortOrder).ThenBy(i => i.ID).ToListAsync(ct);
			var osets = proposed.Select(s => new HospitalVisitOrderSet(s, items.Where(i => i.SetId == s.ID).ToList())).ToList();

			// a test or an exam line is named by its catalogue service
			var sids = items.Where(i => i.ServiceId != null).Select(i => i.ServiceId!.Value).Distinct().ToList();
			var services = await Q<HospitalService>(ctx).AsNoTracking().Where(s => sids.Contains(s.ID)).ToDictionaryAsync(s => s.ID, ct);
			return new HospitalVisitModel(visit, patient, doctor, clinic, vf, offer, last, prefill, heads, care, done, kd, osets, gapServices, canEdit, services);
		}

		/// A specialty clinic's visits (the clinic by its department code): today's, the open ones, or all; searched by the
		/// patient's name or file number; newest first.
		public async Task<(IReadOnlyList<HospitalClinicVisitRow> Rows, int Total, HospitalDepartment? Clinic)> ClinicVisitsAsync(BusinessContext ctx, string code, string? q, string? scope,
			int page, int pageSize, DateTime nowUtc, CancellationToken ct = default)
		{
			var clinic = await Q<HospitalDepartment>(ctx).AsNoTracking().FirstOrDefaultAsync(d => d.Code == code && d.IsActive, ct);
			if (clinic == null) return (Array.Empty<HospitalClinicVisitRow>(), 0, null);
			var src = from e in Q<HospitalEncounter>(ctx)
					  join d in Q<HospitalPractitioner>(ctx) on e.PractitionerId equals d.ID
					  join p in Q<HospitalPatient>(ctx) on e.PatientId equals p.ID
					  where d.DepartmentId == clinic.ID
					  select new { e, d, p };
			var dayStart = HospitalNursingService.UtcToWard(nowUtc).Date.ToUniversalTime();
			if (scope == "open") src = src.Where(x => x.e.Status == HospitalEncounterStatus.Draft);
			else if (scope != "all") src = src.Where(x => x.e.StartedAt >= dayStart);
			if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim(); src = src.Where(x => x.p.Name.Contains(t) || x.p.NameEn.Contains(t) || x.p.Mrn.Contains(t)); }
			int total = await src.CountAsync(ct);
			var rows = await src.OrderByDescending(x => x.e.StartedAt).ThenByDescending(x => x.e.ID).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
			var ids = rows.Select(r => r.e.ID).ToList();
			var forms = new Dictionary<int, (int All, int Signed)>();
			try
			{
				forms = (await Q<HospitalClinicalForm>(ctx).Where(f => ids.Contains(f.EncounterId)).Select(f => new { f.EncounterId, f.Status }).ToListAsync(ct))
					.GroupBy(f => f.EncounterId).ToDictionary(g => g.Key, g => (g.Count(), g.Count(f => f.Status == HospitalFormStatus.Signed)));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			// the doctor is shown by face: a practitioner linked to an employee carries that employee's photo
			var faces = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, rows.Where(r => r.d.EmployeeId != null).Select(r => r.d.EmployeeId!.Value), ct);
			return (rows.Select(r => new HospitalClinicVisitRow(r.e.ID, r.e.StartedAt, r.e.Status, r.p.ID, r.p.Name, r.p.NameEn, r.p.Mrn, r.p.PhotoPath, r.p.Sex, r.p.BirthDate,
				r.d.Name, r.d.NameEn, forms.TryGetValue(r.e.ID, out var c) ? c.All : 0, forms.TryGetValue(r.e.ID, out var c2) ? c2.Signed : 0, r.e.ChiefComplaint,
				r.d.EmployeeId is int de ? faces.GetValueOrDefault(de) : null)).ToList(), total, clinic);
		}

		public async Task<HospitalResult> AddTemplateAsync(BusinessContext ctx, int encounterId, int templateId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var e = await Q<HospitalEncounter>(ctx).FirstOrDefaultAsync(x => x.ID == encounterId, ct);
				if (e == null) return HospitalResult.Fail("not_found");
				var clinic = await ClinicOfAsync(ctx, e, ct);
				var u = (await UsableAsync(ctx, clinic, ct)).FirstOrDefault(x => x.T.ID == templateId);
				if (u.T == null) return HospitalResult.Fail("This template is not published for this clinic");
				return await Forms.SaveAsync(ctx, encounterId, u.V.ID, new Dictionary<string, string>(), employeeId, nowUtc, ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(HospitalClinicalContentService.NoTables); }
		}

		/// Saves every form's answers (formId → answers). Returns the visit id.
		public async Task<HospitalResult> SaveAsync(BusinessContext ctx, int encounterId, IReadOnlyDictionary<int, Dictionary<string, string>> answers, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var forms = await Q<HospitalClinicalForm>(ctx).Where(f => f.EncounterId == encounterId).ToListAsync(ct);
			foreach (var (formId, a) in answers)
			{
				var f = forms.FirstOrDefault(x => x.ID == formId);
				if (f == null) return HospitalResult.Fail("not_found");
				var r = await Forms.SaveAsync(ctx, encounterId, f.TemplateVersionId, a, employeeId, nowUtc, ct);
				if (!r.Ok) return r;
			}
			return HospitalResult.Success(encounterId);
		}

		/// The ONE signature: every draft form must read cleanly; then the consultation (its own rules: complaint,
		/// diagnoses, the bill); then each form, its values becoming observations. Input null = the consultation as saved.
		public async Task<HospitalResult> SignAsync(BusinessContext ctx, HospitalConsultationService consult, int encounterId, HospitalEncounterInput? input, bool en, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var check = await CheckFormsAsync(ctx, encounterId, en, ct);
			if (!check.Ok) return check;
			if (input == null)
			{
				var v = await consult.GetAsync(ctx, encounterId, ct);
				if (v == null) return HospitalResult.Fail("not_found");
				input = HospitalVisitRules.InputOf(v);
			}
			var signed = await consult.SignAsync(ctx, encounterId, input, employeeId, nowUtc, ct);
			if (!signed.Ok) return signed;
			return await SignFormsAsync(ctx, encounterId, en, employeeId, nowUtc, ct);
		}

		/// Refuses with the first form field that would stop the forms' signature.
		public async Task<HospitalResult> CheckFormsAsync(BusinessContext ctx, int encounterId, bool en, CancellationToken ct = default)
		{
			try
			{
				foreach (var f in await Q<HospitalClinicalForm>(ctx).Where(x => x.EncounterId == encounterId && x.Status == HospitalFormStatus.Draft).ToListAsync(ct))
				{
					var (_, err, args) = HospitalFormRules.Read(await Content.FieldViewsAsync(ctx, f.TemplateVersionId, null, ct), HospitalFormRules.Answers(f.AnswersJson), en);
					if (err != null) return HospitalResult.Fail(err, args!);
				}
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			var drawn = await Canvas.CheckAsync(ctx, encounterId, ct);
			if (!drawn.Ok) return drawn;
			return HospitalResult.Success(encounterId);
		}

		public async Task<HospitalResult> SignFormsAsync(BusinessContext ctx, int encounterId, bool en, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				foreach (var f in await Q<HospitalClinicalForm>(ctx).Where(x => x.EncounterId == encounterId && x.Status == HospitalFormStatus.Draft).Select(x => x.ID).ToListAsync(ct))
				{
					var r = await Forms.SignAsync(ctx, f, en, employeeId, nowUtc, ct);
					if (!r.Ok) return r;
				}
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			await Canvas.SignAsync(ctx, encounterId, employeeId, nowUtc, ct);
			return HospitalResult.Success(encounterId);
		}

		/// The ticked lines of an order set proposed by the visit's diagnoses join the consultation's draft. Id = lines added.
		public async Task<HospitalResult> ApplyOrderSetAsync(BusinessContext ctx, HospitalConsultationService consult, int encounterId, int setId, IReadOnlyList<int> itemIds, int? employeeId, CancellationToken ct = default)
		{
			var v = await consult.GetAsync(ctx, encounterId, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			var set = await Q<HospitalOrderSet>(ctx).FirstOrDefaultAsync(s => s.ID == setId && s.IsActive, ct);
			if (set == null || !HospitalOrderSetRules.Proposes(set.DiagnosisCodes, v.Diagnoses.Select(d => d.Code))) return HospitalResult.Fail("This order set is not proposed by this visit's diagnoses");
			var items = await Q<HospitalOrderSetItem>(ctx).Where(i => i.SetId == setId && itemIds.Contains(i.ID)).ToListAsync(ct);
			if (items.Count == 0) return HospitalResult.Fail("Tick at least one line");
			var input = HospitalVisitRules.InputOf(v);
			int n = HospitalVisitRules.Apply(input, items);
			if (n == 0) return HospitalResult.Fail("Everything ticked is already on the visit");
			var r = await consult.SaveDraftAsync(ctx, encounterId, input, employeeId, ct);
			return r.Ok ? HospitalResult.Success(n) : r;
		}

		/// A care gap's test ordered from the card, through the consultation's own save.
		public async Task<HospitalResult> OrderServiceAsync(BusinessContext ctx, HospitalConsultationService consult, int encounterId, int serviceId, int? employeeId, CancellationToken ct = default)
		{
			var v = await consult.GetAsync(ctx, encounterId, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			var input = HospitalVisitRules.InputOf(v);
			if (input.OrderServiceIds.Contains(serviceId)) return HospitalResult.Fail("This test is already ordered on this visit");
			input.OrderServiceIds.Add(serviceId);
			return await consult.SaveDraftAsync(ctx, encounterId, input, employeeId, ct);
		}

		/// The visit's diagnoses join the patient's long-term problems (no repeats).
		public async Task<HospitalResult> AddProblemsAsync(BusinessContext ctx, HospitalConsultationService consult, int encounterId, bool en, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			var v = await consult.GetAsync(ctx, encounterId, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			if (v.Diagnoses.Count == 0) return HospitalResult.Fail("Add a diagnosis to the visit first");
			var p = await Q<HospitalPatient>(ctx).FirstAsync(x => x.ID == v.Encounter.PatientId, ct);
			var next = HospitalVisitRules.WithProblems(p.ChronicConditions, v.Diagnoses.Select(d => d.Code + " " + d.Name));
			if (next == p.ChronicConditions) return HospitalResult.Fail("These diagnoses are already on the problem list");
			if ((next ?? "").Length > 2000) return HospitalResult.Fail("The problem list is full");
			p.ChronicConditions = next;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(p.ID);
		}
	}
}
