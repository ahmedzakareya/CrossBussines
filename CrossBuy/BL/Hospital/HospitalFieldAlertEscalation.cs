using System.Globalization;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H106 — A FIELD ALERT THAT CANNOT WAIT (the approved prototype, psychiatry: "PHQ-9 item 9 > 0 — the on-duty
	// doctor is notified now and a C-SSRS suicide-risk assessment opens before the patient leaves").
	// A template field's alert (H98) may now also
	//   * NOTIFY the department's on-call doctor (H68: the first on call, then the backup) — the moment the visit is
	//     saved with the value across the line, ONCE per form and field (a durable key, read or unread);
	//   * OPEN another template on the visit (C-SSRS) — and the visit's one signature (H94) then needs it filled.
	// With nobody on call it says so; it never pretends someone was told.
	// ==========================================================================================
	public sealed record HospitalFieldEscalation(string? NotifiedAr, string? NotifiedEn, bool NobodyOnCall, IReadOnlyList<int> TemplatesAdded);

	public static class HospitalFieldAlertRules
	{
		/// The field's alert fires for these answers (any side): at or above / at or below, as the screen shows it.
		public static bool Fires(HospitalClinicalTemplateField f, IReadOnlyDictionary<string, string> answers)
		{
			if (f.AlertAbove == null && f.AlertBelow == null) return false;
			foreach (var key in new[] { f.ID.ToString(CultureInfo.InvariantCulture), f.ID + ".R", f.ID + ".L" })
			{
				if (!answers.TryGetValue(key, out var raw) || !decimal.TryParse((raw ?? "").Split('/')[0].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) continue;
				if ((f.AlertAbove is decimal a && v >= a) || (f.AlertBelow is decimal b && v <= b)) return true;
			}
			return false;
		}

		public static string Key(int formId, int fieldId) => $"hosp-field-alert:{formId}:{fieldId}";

		/// H116: the fixed lines OR — when the field says so — a value outside this patient's own range (eff: the concept
		/// as it reads for them, H115), always or only while she is pregnant.
		public static bool Fires(HospitalClinicalTemplateField f, IReadOnlyDictionary<string, string> answers, HospitalConcept? eff, HospitalPatientFacts? facts)
		{
			if (Fires(f, answers)) return true;
			return OutsideRange(f, eff, facts, answers.Where(a => a.Key == f.ID.ToString(CultureInfo.InvariantCulture) || a.Key == f.ID + ".R" || a.Key == f.ID + ".L").Select(a => a.Value));
		}

		/// The range half of the rule, for raw answers ("132/85" for a pair).
		public static bool OutsideRange(HospitalClinicalTemplateField f, HospitalConcept? eff, HospitalPatientFacts? facts, IEnumerable<string?> raws)
		{
			if (f.AlertRange == null || eff == null) return false;
			if (f.AlertRange == HospitalAlertRange.Pregnant && facts?.Trimester == null) return false;
			foreach (var raw in raws)
			{
				var parts = (raw ?? "").Split('/');
				if (!decimal.TryParse(parts[0].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) continue;
				decimal? v2 = parts.Length > 1 && decimal.TryParse(parts[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var w) ? w : null;
				if (HospitalObsFlag.Abnormal(HospitalConceptRules.Flag(eff, v, v2))) return true;
			}
			return false;
		}

		/// The same, for a stored reading's flag (it was already judged against the patient's range when it was signed).
		public static bool OutsideRange(HospitalClinicalTemplateField f, string? storedFlag, HospitalPatientFacts? facts) =>
			f.AlertRange != null && (f.AlertRange != HospitalAlertRange.Pregnant || facts?.Trimester != null) && HospitalObsFlag.Abnormal(storedFlag ?? HospitalObsFlag.Unknown);
	}

	public sealed class HospitalFieldAlertEscalation
	{
		public const string NotificationType = "hospital_field_alert";
		private readonly CrossDbContext _db;
		private readonly INotificationService _notify;
		public HospitalFieldAlertEscalation(CrossDbContext db, INotificationService notify) { _db = db; _notify = notify; }
		private IQueryable<T> Q<T>(BusinessContext ctx) where T : class => _db.Set<T>().Where(x => EF.Property<int>(x, "CompanyId") == ctx.CompanyId);

		/// After the visit's forms are saved: the escalating alerts that fire now are sent (once) and their templates opened.
		public async Task<HospitalFieldEscalation> RunAsync(BusinessContext ctx, HospitalSpecialtyVisitService visits, int encounterId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var e = await Q<HospitalEncounter>(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == encounterId, ct);
				if (e == null || e.Status != HospitalEncounterStatus.Draft) return new(null, null, false, Array.Empty<int>());
				var forms = await Q<HospitalClinicalForm>(ctx).AsNoTracking().Where(f => f.EncounterId == encounterId && f.Status == HospitalFormStatus.Draft).ToListAsync(ct);
				var versions = forms.Select(f => f.TemplateVersionId).Distinct().ToList();
				var fields = await Q<HospitalClinicalTemplateField>(ctx).AsNoTracking().Where(f => versions.Contains(f.VersionId) && (f.AlertNotify || f.AlertTemplateId != null)).ToListAsync(ct);
				if (fields.Count == 0) return new(null, null, false, Array.Empty<int>());
				var firing = new List<(HospitalClinicalForm Form, HospitalClinicalTemplateField Field)>();
				// H116: a range alert is judged against this patient's own range today (sex, age, pregnancy)
				Dictionary<int, HospitalConcept> eff = new(); HospitalPatientFacts? facts = null;
				if (fields.Any(x => x.AlertRange != null))
				{
					var patient = await Q<HospitalPatient>(ctx).AsNoTracking().FirstAsync(p => p.ID == e.PatientId, ct);
					var ranges = new HospitalConceptRangeService(_db);
					var cids = fields.Where(x => x.AlertRange != null).Select(x => x.ConceptId).Distinct().ToList();
					eff = (await ranges.ForPatientAsync(ctx, await Q<HospitalConcept>(ctx).AsNoTracking().Where(c => cids.Contains(c.ID)).ToListAsync(ct), patient, nowUtc, ct)).ToDictionary(c => c.ID);
					facts = await ranges.FactsAsync(ctx, patient, nowUtc, ct);
				}
				// H118: a score's alert (compartment syndrome, sudden hearing loss) is judged on the score as the items stand now
				var formFields = await Q<HospitalClinicalTemplateField>(ctx).AsNoTracking().Where(x => versions.Contains(x.VersionId)).ToListAsync(ct);
				var fcids = formFields.Select(x => x.ConceptId).Distinct().ToList();
				var fconcepts = await Q<HospitalConcept>(ctx).AsNoTracking().Where(c => fcids.Contains(c.ID)).ToDictionaryAsync(c => c.ID, ct);
				foreach (var f in forms)
				{
					var answers = HospitalFormRules.Answers(f.AnswersJson);
					var mine = formFields.Where(x => x.VersionId == f.TemplateVersionId).ToList();
					if (mine.Any(x => fconcepts.TryGetValue(x.ConceptId, out var cc) && cc.ValueType == HospitalConceptType.Score && cc.Calculator == HospitalConceptCalculator.Sum))
						answers = HospitalFormRules.WithScores(mine.Where(x => fconcepts.ContainsKey(x.ConceptId)).Select(x => new HospitalTemplateFieldView(x, fconcepts[x.ConceptId], mine.FirstOrDefault(y => y.ID == x.ShowWhenFieldId))).ToList(), answers);
					foreach (var x in fields.Where(x => x.VersionId == f.TemplateVersionId && HospitalFieldAlertRules.Fires(x, answers, eff.GetValueOrDefault(x.ConceptId), facts))) firing.Add((f, x));
				}
				if (firing.Count == 0) return new(null, null, false, Array.Empty<int>());

				// ---- open the templates the alerts call for (the visit's signature then needs them filled)
				var added = new List<int>();
				var onVisit = await (from f in Q<HospitalClinicalForm>(ctx).Where(f => f.EncounterId == encounterId)
									 join v in Q<HospitalClinicalTemplateVersion>(ctx) on f.TemplateVersionId equals v.ID select v.TemplateId).ToListAsync(ct);
				foreach (var tid in firing.Select(x => x.Field.AlertTemplateId).OfType<int>().Distinct().Where(t => !onVisit.Contains(t)))
					if ((await visits.AddTemplateAsync(ctx, encounterId, tid, employeeId, nowUtc, ct)).Ok) added.Add(tid);

				// ---- tell the on-call doctor, once per form and field
				string? toAr = null, toEn = null; bool nobody = false;
				var notify = firing.Where(x => x.Field.AlertNotify).ToList();
				if (notify.Count > 0)
				{
					var doctor = await Q<HospitalPractitioner>(ctx).AsNoTracking().FirstOrDefaultAsync(p => p.ID == e.PractitionerId, ct);
					var patient = await Q<HospitalPatient>(ctx).AsNoTracking().FirstAsync(p => p.ID == e.PatientId, ct);
					HospitalPractitioner? onCall = null;
					if (doctor?.DepartmentId is int dept)
						foreach (var shift in await HospitalOnCallService.OnCallAtAsync(_db, ctx.CompanyId, dept, HospitalNursingService.UtcToWard(nowUtc), ct))
						{
							onCall = await Q<HospitalPractitioner>(ctx).AsNoTracking().FirstOrDefaultAsync(p => p.ID == shift.PractitionerId && p.IsActive && p.EmployeeId != null, ct);
							if (onCall != null) break;
						}
					if (onCall == null) nobody = true;
					else
					{
						toAr = onCall.Name; toEn = onCall.NameEn;
						foreach (var (form, field) in notify)
						{
							var key = HospitalFieldAlertRules.Key(form.ID, field.ID);
							if (await _db.Notifications.AsNoTracking().AnyAsync(n => n.DedupKey == key, ct)) continue;   // durable: sent once, read or not
							await _notify.NotifyAsync(onCall.EmployeeId!.Value,
								"تنبيه عاجل: " + patient.Name, "Urgent alert: " + DisplayName.Of(patient.NameEn, patient.Name),
								(field.AlertText ?? "") + " — " + patient.Mrn, (field.AlertTextEn ?? field.AlertText ?? "") + " — " + patient.Mrn,
								NotificationType, encounterId, $"/Hospital/Consult/{encounterId}/Specialty", ctx.CompanyId, employeeId,
								priority: "Critical", category: "hospital", dedupKey: key, icon: "ki-notification-bing");
						}
					}
				}
				return new(toAr, toEn, nobody, added);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return new(null, null, false, Array.Empty<int>()); }
		}
	}
}
