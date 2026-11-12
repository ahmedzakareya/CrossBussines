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
				foreach (var f in forms)
				{
					var answers = HospitalFormRules.Answers(f.AnswersJson);
					foreach (var x in fields.Where(x => x.VersionId == f.TemplateVersionId && HospitalFieldAlertRules.Fires(x, answers))) firing.Add((f, x));
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
