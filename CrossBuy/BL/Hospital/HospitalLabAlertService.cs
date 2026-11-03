using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H8/H9 — telling a doctor about a result: a lab value or an imaging report.
	//
	//   · A CRITICAL result (a lab value LL/HH, or a report signed Critical): the lab or radiology picks the
	//     doctor (the ordering one is offered first, then the department, then anyone with a user). The
	//     doctor sees a blocking alert on EVERY screen of the system — or the moment he signs in — and
	//     closes it only by acknowledging; a lab value must be read back, an imaging report has no number.
	//     Not acknowledged in ReminderMinutes it is reminded; in EscalateMinutes it goes to another doctor
	//     of the department, on duty first. Acknowledging closes every open alert of the order and records
	//     it on the order (the same field the "record the call" fills).
	//   · An ABNORMAL result on an urgent or STAT order (a lab value L/H, or a report signed Abnormal): a
	//     notice to the ordering doctor; it does not stop his work and needs no read-back.
	// One alert names exactly one source: a lab order or an imaging order (HospitalAlertSource).
	// ==========================================================================================

	public sealed record HospitalLabRecipient(HospitalPractitioner Practitioner, string Why, bool OnDuty, bool HasUser);

	public sealed record HospitalLabAlertLine(string Name, string NameEn, string? Unit, string Shown, string Flag, string? Critical, string Range, string? Previous);

	public sealed record HospitalLabAlertView(int Id, string Level, string Reason, int OrderId, string AccessionNo, string Test, string TestEn, string Priority,
		int PatientId, string PatientName, string PatientNameEn, string Mrn, string? Photo, string? Room, string? Bed, string? Value,
		IReadOnlyList<HospitalLabAlertLine> Lines, DateTime SentAt, string? SentBy, string? EscalatedFrom, DateTime? SeenAt, DateTime? RemindedAt,
		string Source = HospitalAlertSource.Lab, string? Impression = null, string? ImpressionCategory = null);

	public sealed record HospitalLabAlertTrail(HospitalLabCriticalAlert Alert, string To, string? ToPhoto, string? SentBy, string? AcknowledgedBy, string? EscalateTo);

	/// The order an alert is about, as the critical-value page shows it — whichever its source.
	public sealed record HospitalCriticalSubject(string Source, int Id, string AccessionNo, string Name, string NameEn, string Priority, HospitalPatient Patient, int? EncounterId,
		bool IsCritical, bool Final, DateTime? DoneAt, int? DoneByEmployeeId, DateTime? AcknowledgedAt, int? AcknowledgedByEmployeeId, string? CriticalNote,
		string? Impression, string? ImpressionCategory, IReadOnlyDictionary<int, string> People);

	public sealed class HospitalLabAlertService
	{
		public const int ReminderMinutes = 5;
		public const int EscalateMinutes = 10;
		/// The order's critical note when the doctor acknowledged on screen: this prefix and the value read back
		/// (nothing after it for an imaging report, which has no number).
		public const string ReadBackNotePrefix = "readback:";

		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalLabService _lab;

		public HospitalLabAlertService(CrossDbContext db, IHospitalAccessService access, HospitalLabService lab) { _db = db; _access = access; _lab = lab; }

		/// A doctor works now when today is one of his days and the clock is inside his session (the clinic
		/// day is the server's wall clock, as for appointments).
		public static bool OnDuty(HospitalPractitioner p, DateTime local)
		{
			int m = local.Hour * 60 + local.Minute;
			return p.IsActive && p.Days().Contains(local.DayOfWeek) && m >= p.SessionStartMinute && m < p.SessionEndMinute;
		}

		/// The value read back: the first critical result as shown, else the first out-of-range one.
		public static string? ReadBackValue(IEnumerable<HospitalLabResult> results)
		{
			var ordered = results.OrderBy(r => r.SortOrder).ToList();
			var r = ordered.FirstOrDefault(x => HospitalLabService.IsCriticalFlag(x.Flag)) ?? ordered.FirstOrDefault(x => x.Flag != HospitalLabFlag.Normal);
			return r == null ? null : r.Value != null ? HospitalUi.LabNum(r.Value, r.Decimals) : r.TextValue;
		}

		/// The read-back matches when it is the same number (2.840 = 2.84) or, for a text value, the same text.
		public static bool ReadBackMatches(string? expected, string? typed)
		{
			if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(typed)) return false;
			var inv = System.Globalization.CultureInfo.InvariantCulture;
			var ns = System.Globalization.NumberStyles.Number;
			if (decimal.TryParse(expected, ns, inv, out var a) && decimal.TryParse(typed.Trim(), ns, inv, out var b)) return a == b;
			return string.Equals(expected.Trim(), typed.Trim(), StringComparison.OrdinalIgnoreCase);
		}

		// ====================================================================================
		// THE SOURCE: a lab order or an imaging order
		// ====================================================================================

		private static bool IsRad(string source) => source == HospitalAlertSource.Radiology;

		private IQueryable<HospitalLabCriticalAlert> AlertsOf(BusinessContext ctx, string source, int id)
			=> IsRad(source) ? _db.HospitalLabCriticalAlerts.Where(a => a.CompanyId == ctx.CompanyId && a.RadOrderId == id)
							 : _db.HospitalLabCriticalAlerts.Where(a => a.CompanyId == ctx.CompanyId && a.LabOrderId == id);

		private IQueryable<HospitalLabCriticalAlert> SiblingsOf(BusinessContext ctx, HospitalLabCriticalAlert a)
			=> a.RadOrderId is int r ? AlertsOf(ctx, HospitalAlertSource.Radiology, r) : AlertsOf(ctx, HospitalAlertSource.Lab, a.LabOrderId ?? 0);

		private static string SourceOf(HospitalLabCriticalAlert a) => a.RadOrderId != null ? HospitalAlertSource.Radiology : HospitalAlertSource.Lab;
		private static int IdOf(HospitalLabCriticalAlert a) => a.RadOrderId ?? a.LabOrderId ?? 0;

		/// The order behind an alert, read the same way whichever its source.
		public async Task<HospitalCriticalSubject?> SubjectAsync(BusinessContext ctx, string source, int id, CancellationToken ct = default)
		{
			HospitalCriticalSubject? s;
			if (IsRad(source))
			{
				var o = await _db.HospitalRadOrders.AsNoTracking().FirstOrDefaultAsync(r => r.ID == id && r.CompanyId == ctx.CompanyId, ct);
				if (o == null) return null;
				s = new HospitalCriticalSubject(source, o.ID, o.AccessionNo, o.Name, o.NameEn, o.Priority, null!, o.EncounterId, o.IsCritical, o.Status == HospitalRadStatus.Signed,
					o.SignedAt ?? o.ReportedAt, o.SignedByEmployeeId ?? o.ReportedByEmployeeId, o.CriticalAcknowledgedAt, o.CriticalAcknowledgedByEmployeeId, o.CriticalNote,
					o.Impression, o.ImpressionCategory, new Dictionary<int, string>());
				s = s with { Patient = await _db.HospitalPatients.AsNoTracking().FirstAsync(p => p.ID == o.PatientId && p.CompanyId == ctx.CompanyId, ct) };
			}
			else
			{
				var o = await _db.HospitalLabOrders.AsNoTracking().FirstOrDefaultAsync(l => l.ID == id && l.CompanyId == ctx.CompanyId, ct);
				if (o == null) return null;
				s = new HospitalCriticalSubject(source, o.ID, o.AccessionNo, o.Name, o.NameEn, o.Priority, null!, o.EncounterId, o.IsCritical, o.Status == HospitalLabStatus.Verified,
					o.VerifiedAt ?? o.ResultedAt, o.VerifiedByEmployeeId ?? o.ResultedByEmployeeId, o.CriticalAcknowledgedAt, o.CriticalAcknowledgedByEmployeeId, o.CriticalNote,
					null, null, new Dictionary<int, string>());
				s = s with { Patient = await _db.HospitalPatients.AsNoTracking().FirstAsync(p => p.ID == o.PatientId && p.CompanyId == ctx.CompanyId, ct) };
			}
			var ids = new[] { s.DoneByEmployeeId, s.AcknowledgedByEmployeeId }.Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
			return s with { People = people };
		}

		private async Task<HospitalPractitioner?> OrderingDoctorAsync(BusinessContext ctx, int? encounterId, CancellationToken ct)
		{
			if (encounterId == null) return null;
			var pid = await _db.HospitalEncounters.AsNoTracking().Where(e => e.ID == encounterId && e.CompanyId == ctx.CompanyId).Select(e => (int?)e.PractitionerId).FirstOrDefaultAsync(ct);
			return pid == null ? null : await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(p => p.ID == pid && p.CompanyId == ctx.CompanyId, ct);
		}

		private async Task<string?> ValueOfAsync(BusinessContext ctx, string source, int id, CancellationToken ct)
			=> IsRad(source) ? null : ReadBackValue(await _db.HospitalLabResults.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.LabOrderId == id).ToListAsync(ct));

		// ====================================================================================
		// THE SENDING SIDE (lab or radiology)
		// ====================================================================================

		/// Whom the department can tell: the ordering doctor, then the doctors of his department, then everyone else.
		public async Task<IReadOnlyList<HospitalLabRecipient>> RecipientsAsync(BusinessContext ctx, string source, int id, DateTime local, CancellationToken ct = default)
		{
			var s = await SubjectAsync(ctx, source, id, ct);
			if (s == null) return Array.Empty<HospitalLabRecipient>();
			var ordering = await OrderingDoctorAsync(ctx, s.EncounterId, ct);
			var all = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.IsActive).OrderBy(p => p.Name).ToListAsync(ct);
			string Why(HospitalPractitioner p) => p.ID == ordering?.ID ? HospitalLabAlertReason.Ordering : ordering?.DepartmentId != null && p.DepartmentId == ordering.DepartmentId ? "Department" : "Other";
			return all.Select(p => new HospitalLabRecipient(p, Why(p), OnDuty(p, local), p.EmployeeId != null))
				.OrderBy(r => r.Why == HospitalLabAlertReason.Ordering ? 0 : r.Why == "Department" ? 1 : 2).ThenByDescending(r => r.OnDuty).ToList();
		}

		/// Sends the critical alert to the chosen doctor. A phone note, when given, also records the call.
		public async Task<HospitalResult> SendAsync(BusinessContext ctx, string source, int id, int practitionerId, bool viaSms, string? phoneNote, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, IsRad(source) ? HospitalActions.Rad : HospitalActions.Lab, null, ct)) return HospitalResult.Fail("forbidden");
			var s = await SubjectAsync(ctx, source, id, ct);
			if (s == null) return HospitalResult.Fail("not_found");
			if (!s.IsCritical) return HospitalResult.Fail("This order has no critical value");
			if (s.AcknowledgedAt != null) return HospitalResult.Fail("The doctor already acknowledged this critical value");
			var doc = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(p => p.ID == practitionerId && p.CompanyId == ctx.CompanyId && p.IsActive, ct);
			if (doc == null) return HospitalResult.Fail("not_found");
			if (doc.EmployeeId is not int to) return HospitalResult.Fail("This doctor has no user in the system, so an alert cannot reach him");
			if (await AlertsOf(ctx, source, id).AnyAsync(a => a.ToEmployeeId == to && a.AcknowledgedAt == null, ct))
				return HospitalResult.Fail("An alert to this doctor is already waiting");
			var ordering = await OrderingDoctorAsync(ctx, s.EncounterId, ct);
			var a = new HospitalLabCriticalAlert
			{
				CompanyId = ctx.CompanyId, LabOrderId = IsRad(source) ? null : id, RadOrderId = IsRad(source) ? id : null, Level = HospitalLabAlertLevel.Critical,
				Reason = doc.ID == ordering?.ID ? HospitalLabAlertReason.Ordering : HospitalLabAlertReason.Chosen,
				ToEmployeeId = to, ToPractitionerId = doc.ID, Value = await ValueOfAsync(ctx, source, id, ct), ViaSms = viaSms, SentAt = nowUtc, SentByEmployeeId = employeeId,
			};
			_db.HospitalLabCriticalAlerts.Add(a);
			await _db.SaveChangesAsync(ct);
			if (!string.IsNullOrWhiteSpace(phoneNote))
			{
				if (!IsRad(source))
				{
					var call = await _lab.AcknowledgeCriticalAsync(ctx, id, phoneNote, employeeId, nowUtc, ct);
					if (!call.Ok) return call;
				}
				else
				{
					var o = await _db.HospitalRadOrders.FirstAsync(r => r.ID == id && r.CompanyId == ctx.CompanyId, ct);
					o.CriticalAcknowledgedAt = nowUtc; o.CriticalAcknowledgedByEmployeeId = employeeId; o.CriticalNote = phoneNote.Trim()[..Math.Min(400, phoneNote.Trim().Length)];
					await _db.SaveChangesAsync(ct);
				}
			}
			return HospitalResult.Success(a.ID);
		}

		/// The department's view of everyone told about an order, oldest first, with whom an escalation would reach.
		public async Task<IReadOnlyList<HospitalLabAlertTrail>> TrailAsync(BusinessContext ctx, string source, int id, DateTime local, CancellationToken ct = default)
		{
			var alerts = await AlertsOf(ctx, source, id).AsNoTracking().OrderBy(a => a.ID).ToListAsync(ct);
			if (alerts.Count == 0) return Array.Empty<HospitalLabAlertTrail>();
			var ids = alerts.SelectMany(a => new int?[] { a.ToEmployeeId, a.SentByEmployeeId, a.AcknowledgedByEmployeeId }).Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
			var photos = await EmployeePhotos.ResolveAsync(_db, ctx, ids, ct);
			string? Name(int? pid) => people.FirstOrDefault(p => p.ID == pid) is { } p ? DisplayName.Of(p.FullName ?? "", p.FullNameEn) : null;
			var list = new List<HospitalLabAlertTrail>();
			foreach (var a in alerts)
			{
				string? next = null;
				if (a.AcknowledgedAt == null && a.EscalatedAt == null && a.Level == HospitalLabAlertLevel.Critical)
					next = (await EscalationTargetAsync(ctx, a, local, ct)) is { } t ? DisplayName.Of(t.Name, t.NameEn) : null;
				list.Add(new HospitalLabAlertTrail(a, Name(a.ToEmployeeId) ?? "—", photos.GetValueOrDefault(a.ToEmployeeId), Name(a.SentByEmployeeId), Name(a.AcknowledgedByEmployeeId), next));
			}
			return list;
		}

		/// Another doctor of the recipient's department with a user, not yet told about this order — on duty first.
		private async Task<HospitalPractitioner?> EscalationTargetAsync(BusinessContext ctx, HospitalLabCriticalAlert a, DateTime local, CancellationToken ct)
		{
			var from = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(p => p.ID == a.ToPractitionerId && p.CompanyId == ctx.CompanyId, ct);
			if (from?.DepartmentId == null) return null;
			var told = await SiblingsOf(ctx, a).Select(x => x.ToEmployeeId).ToListAsync(ct);
			var peers = await _db.HospitalPractitioners.AsNoTracking()
				.Where(p => p.CompanyId == ctx.CompanyId && p.IsActive && p.DepartmentId == from.DepartmentId && p.ID != from.ID && p.EmployeeId != null && !told.Contains(p.EmployeeId!.Value))
				.OrderBy(p => p.ID).ToListAsync(ct);
			return peers.OrderByDescending(p => OnDuty(p, local)).FirstOrDefault();
		}

		/// Reminds and escalates the alerts that waited too long. Idempotent; run on every read.
		public async Task MaintainAsync(BusinessContext ctx, DateTime nowUtc, DateTime local, CancellationToken ct = default)
		{
			var remindBefore = nowUtc.AddMinutes(-ReminderMinutes);
			var escalateBefore = nowUtc.AddMinutes(-EscalateMinutes);
			var due = await _db.HospitalLabCriticalAlerts.Where(a => a.CompanyId == ctx.CompanyId && a.Level == HospitalLabAlertLevel.Critical && a.AcknowledgedAt == null
				&& ((a.RemindedAt == null && a.SentAt <= remindBefore) || (a.EscalatedAt == null && a.SentAt <= escalateBefore))).ToListAsync(ct);
			if (due.Count == 0) return;
			foreach (var a in due)
			{
				if (a.RemindedAt == null && a.SentAt <= remindBefore) a.RemindedAt = nowUtc;
				if (a.EscalatedAt == null && a.SentAt <= escalateBefore)
				{
					a.EscalatedAt = nowUtc;
					if (await EscalationTargetAsync(ctx, a, local, ct) is { } t)
						_db.HospitalLabCriticalAlerts.Add(new HospitalLabCriticalAlert
						{
							CompanyId = a.CompanyId, LabOrderId = a.LabOrderId, RadOrderId = a.RadOrderId, Level = a.Level, Reason = HospitalLabAlertReason.Escalation, ToEmployeeId = t.EmployeeId!.Value,
							ToPractitionerId = t.ID, Value = a.Value, ViaSms = a.ViaSms, SentAt = nowUtc, SentByEmployeeId = a.SentByEmployeeId, EscalatedFromAlertId = a.ID,
						});
				}
			}
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); }
		}

		/// A final urgent/STAT result that is out of range but not critical: a notice to the ordering doctor.
		public async Task<bool> NotifyAbnormalAsync(BusinessContext ctx, string source, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var s = await SubjectAsync(ctx, source, id, ct);
			if (s == null || !s.Final || s.IsCritical || s.Priority == HospitalLabPriority.Routine) return false;
			if (IsRad(source))
			{
				if (s.ImpressionCategory != HospitalRadImpression.Abnormal) return false;
			}
			else if (await _db.HospitalLabResults.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.LabOrderId == id).AllAsync(r => r.Flag == HospitalLabFlag.Normal, ct)) return false;
			var doc = await OrderingDoctorAsync(ctx, s.EncounterId, ct);
			if (doc?.EmployeeId is not int to) return false;
			if (await AlertsOf(ctx, source, id).AnyAsync(a => a.Level == HospitalLabAlertLevel.Abnormal, ct)) return false;
			_db.HospitalLabCriticalAlerts.Add(new HospitalLabCriticalAlert
			{
				CompanyId = ctx.CompanyId, LabOrderId = IsRad(source) ? null : id, RadOrderId = IsRad(source) ? id : null, Level = HospitalLabAlertLevel.Abnormal,
				Reason = HospitalLabAlertReason.Ordering, ToEmployeeId = to, ToPractitionerId = doc.ID, Value = await ValueOfAsync(ctx, source, id, ct), SentAt = nowUtc, SentByEmployeeId = employeeId,
			});
			await _db.SaveChangesAsync(ct);
			return true;
		}

		// ====================================================================================
		// THE DOCTOR SIDE — any screen of the system
		// ====================================================================================

		/// The open alerts addressed to the signed-in employee, critical first. Addressing IS the authority:
		/// nobody sees an alert that is not his.
		public async Task<IReadOnlyList<HospitalLabAlertView>> MineAsync(BusinessContext ctx, DateTime nowUtc, DateTime local, CancellationToken ct = default)
		{
			if (ctx.EmployeeId is not int me) return Array.Empty<HospitalLabAlertView>();
			await MaintainAsync(ctx, nowUtc, local, ct);
			var alerts = await _db.HospitalLabCriticalAlerts.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.ToEmployeeId == me && a.AcknowledgedAt == null)
				.OrderBy(a => a.Level == HospitalLabAlertLevel.Critical ? 0 : 1).ThenBy(a => a.SentAt).Take(10).ToListAsync(ct);
			var views = new List<HospitalLabAlertView>();
			foreach (var a in alerts)
				if (await ViewAsync(ctx, a, ct) is { } v) views.Add(v);
			return views;
		}

		/// What the doctor would see for this order — the department's "preview" uses the same view as the alert.
		public async Task<HospitalLabAlertView?> PreviewAsync(BusinessContext ctx, string source, int id, DateTime nowUtc, CancellationToken ct = default)
			=> await ViewAsync(ctx, new HospitalLabCriticalAlert
			{
				CompanyId = ctx.CompanyId, LabOrderId = IsRad(source) ? null : id, RadOrderId = IsRad(source) ? id : null,
				Value = await ValueOfAsync(ctx, source, id, ct), SentAt = nowUtc, SentByEmployeeId = ctx.EmployeeId,
			}, ct);

		private async Task<HospitalLabAlertView?> ViewAsync(BusinessContext ctx, HospitalLabCriticalAlert a, CancellationToken ct)
		{
			var source = SourceOf(a);
			var s = await SubjectAsync(ctx, source, IdOf(a), ct);
			if (s == null) return null;
			var p = s.Patient;
			var lines = new List<HospitalLabAlertLine>();
			if (!IsRad(source) && await _lab.OrderAsync(ctx, s.Id, ct) is { } v)
				// The critical values first — the first one is the value read back and the one drawn large.
				lines = v.Lines.Where(l => l.Result != null && l.Result.Flag != HospitalLabFlag.Normal)
					.OrderBy(l => HospitalLabService.IsCriticalFlag(l.Result!.Flag) ? 0 : 1).ThenBy(l => l.Result!.SortOrder)
					.Select(l => new HospitalLabAlertLine(l.Analyte.Name, l.Analyte.NameEn, l.Result!.Unit, l.Result.Value != null ? HospitalUi.LabNum(l.Result.Value, l.Result.Decimals) : l.Result.TextValue ?? "",
						l.Result.Flag, l.Analyte.CriticalHigh != null && l.Result.Flag == HospitalLabFlag.CriticalHigh ? "> " + HospitalUi.LabNum(l.Analyte.CriticalHigh, l.Result.Decimals)
							: l.Analyte.CriticalLow != null && l.Result.Flag == HospitalLabFlag.CriticalLow ? "< " + HospitalUi.LabNum(l.Analyte.CriticalLow, l.Result.Decimals) : null,
						HospitalUi.LabRange(l.Result.RefLow, l.Result.RefHigh, l.Result.Decimals), l.Previous != null ? HospitalUi.LabNum(l.Previous, l.Result.Decimals) : null)).ToList();
			var stay = await (from st in _db.HospitalBedStays.AsNoTracking()
							  join r in _db.HospitalRooms.AsNoTracking() on st.RoomId equals r.ID
							  join b in _db.HospitalBeds.AsNoTracking() on st.BedId equals b.ID
							  where st.CompanyId == ctx.CompanyId && st.PatientId == p.ID && st.EndedAt == null
							  select new { r.Number, b.Code }).FirstOrDefaultAsync(ct);
			string? sentBy = a.SentByEmployeeId is int sb ? await _db.Employee.AsNoTracking().Where(e => e.ID == sb && e.EmpCompanyID == ctx.CompanyId).Select(e => DisplayName.Of(e.FullName ?? "", e.FullNameEn)).FirstOrDefaultAsync(ct) : null;
			string? escalatedFrom = null;
			if (a.EscalatedFromAlertId is int fromId)
			{
				var fromEmp = await _db.HospitalLabCriticalAlerts.AsNoTracking().Where(x => x.ID == fromId && x.CompanyId == ctx.CompanyId).Select(x => (int?)x.ToEmployeeId).FirstOrDefaultAsync(ct);
				escalatedFrom = fromEmp == null ? null : await _db.Employee.AsNoTracking().Where(e => e.ID == fromEmp && e.EmpCompanyID == ctx.CompanyId).Select(e => DisplayName.Of(e.FullName ?? "", e.FullNameEn)).FirstOrDefaultAsync(ct);
			}
			return new HospitalLabAlertView(a.ID, a.Level, a.Reason, s.Id, s.AccessionNo, s.Name, s.NameEn, s.Priority, p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath,
				stay?.Number, stay?.Code, a.Value, lines, a.SentAt, sentBy, escalatedFrom, a.SeenAt, a.RemindedAt, source, s.Impression, s.ImpressionCategory);
		}

		public async Task<HospitalResult> SeenAsync(BusinessContext ctx, int id, DateTime nowUtc, CancellationToken ct = default)
		{
			var a = await _db.HospitalLabCriticalAlerts.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId && x.ToEmployeeId == ctx.EmployeeId, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			if (a.SeenAt == null) { a.SeenAt = nowUtc; await _db.SaveChangesAsync(ct); }
			return HospitalResult.Success(a.ID);
		}

		/// The doctor acknowledges. A critical lab value must be read back (an imaging report has no number);
		/// acknowledging closes every open alert of the order and records it on the order.
		public async Task<HospitalResult> AcknowledgeAsync(BusinessContext ctx, int id, string? readBack, DateTime nowUtc, CancellationToken ct = default)
		{
			var a = await _db.HospitalLabCriticalAlerts.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId && x.ToEmployeeId == ctx.EmployeeId, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			if (a.AcknowledgedAt != null) return HospitalResult.Success(a.ID);
			bool critical = a.Level == HospitalLabAlertLevel.Critical;
			bool needsReadBack = critical && a.Value != null;
			if (needsReadBack && !ReadBackMatches(a.Value, readBack)) return HospitalResult.Fail("The value you typed is not the value sent. Read it again");
			var open = await SiblingsOf(ctx, a).Where(x => x.Level == a.Level && x.AcknowledgedAt == null).ToListAsync(ct);
			foreach (var x in open)
			{
				x.AcknowledgedAt = nowUtc; x.AcknowledgedByEmployeeId = ctx.EmployeeId; x.SeenAt ??= nowUtc;
				if (x.ID == a.ID) x.ReadBack = needsReadBack ? readBack!.Trim() : null;
			}
			if (critical)
			{
				var note = ReadBackNotePrefix + (needsReadBack ? readBack!.Trim() : "");   // the views say "acknowledged on screen" in the reader's language
				if (a.RadOrderId is int rid)
				{
					var o = await _db.HospitalRadOrders.FirstAsync(r => r.ID == rid && r.CompanyId == ctx.CompanyId, ct);
					if (o.CriticalAcknowledgedAt == null) { o.CriticalAcknowledgedAt = nowUtc; o.CriticalAcknowledgedByEmployeeId = ctx.EmployeeId; o.CriticalNote = note; }
				}
				else
				{
					var o = await _db.HospitalLabOrders.FirstAsync(l => l.ID == a.LabOrderId && l.CompanyId == ctx.CompanyId, ct);
					if (o.CriticalAcknowledgedAt == null) { o.CriticalAcknowledgedAt = nowUtc; o.CriticalAcknowledgedByEmployeeId = ctx.EmployeeId; o.CriticalNote = note; }
				}
			}
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(a.ID);
		}
	}
}
