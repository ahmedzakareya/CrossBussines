using System.Globalization;
using System.Text.RegularExpressions;
using CrossBuy.BL.Comm;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H70 — SMS to patients. The events write their message (QueueAppointmentAsync after a booking or a missed
	// appointment, QueueLabReadyAsync after results are verified, SweepRemindersAsync for tomorrow's appointments); staff
	// type one; the administrator edits the words and sends the queue through ISmsSender. With no provider connected the
	// queue is kept and the screen says so. A message that cannot be addressed is written as Skipped with why.
	// ==========================================================================================
	public sealed class HospitalSmsComposeInput
	{
		public int? PatientId { get; set; }
		public string? Phone { get; set; }
		public string? Language { get; set; }
		public string? Body { get; set; }
	}

	public sealed class HospitalSmsTemplateInput
	{
		public string? Kind { get; set; }
		public string? BodyAr { get; set; }
		public string? BodyEn { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public sealed record HospitalSmsRow(HospitalSmsMessage Message, string? PatientName, string? PatientNameEn, string? Mrn);
	public sealed record HospitalSmsTemplateView(string Kind, string BodyAr, string BodyEn, bool IsActive, bool Edited);
	public sealed record HospitalSmsOptOutRow(HospitalSmsOptOut Row, string Name, string NameEn, string Mrn);
	public sealed record HospitalSmsFigures(bool Ready, int Queued, int SentToday, int Failed, int Skipped30, int OptOuts);

	public static class HospitalSmsRules
	{
		public static readonly IReadOnlyList<string> Placeholders = new[] { "patient", "doctor", "date", "time", "test", "hospital" };

		/// The words a kind starts with until the administrator edits them.
		public static (string Ar, string En) DefaultTemplate(string kind) => kind switch
		{
			HospitalSmsKind.Booked => ("عزيزي {patient}، تم حجز موعدك مع {doctor} يوم {date} الساعة {time}. {hospital}",
				"Dear {patient}, your appointment with {doctor} is booked for {date} at {time}. {hospital}"),
			HospitalSmsKind.Reminder => ("تذكير: موعدك غداً {date} الساعة {time} مع {doctor}. للإلغاء اتصل بنا. {hospital}",
				"Reminder: your appointment is tomorrow, {date} at {time}, with {doctor}. Call us to cancel. {hospital}"),
			HospitalSmsKind.NoShow => ("{patient}، فاتك موعدك مع {doctor} يوم {date}. اتصل بنا لحجز موعد جديد. {hospital}",
				"{patient}, you missed your appointment with {doctor} on {date}. Call us to book a new one. {hospital}"),
			HospitalSmsKind.LabReady => ("{patient}، نتائج تحاليلك ({test}) جاهزة. {hospital}",
				"{patient}, your lab results ({test}) are ready. {hospital}"),
			_ => ("", ""),
		};

		private static readonly Regex Slot = new(@"\{([a-z]+)\}", RegexOptions.Compiled);

		/// The template with its values put in; a placeholder with no value is left out.
		public static string Render(string template, IReadOnlyDictionary<string, string?> values) =>
			Regex.Replace(Slot.Replace(template ?? "", m => values.TryGetValue(m.Groups[1].Value, out var v) ? (v ?? "") : ""), @"[ \t]{2,}", " ").Trim();

		/// A phone a message can go to: digits (and a leading +) only, 8 to 15 digits; null otherwise.
		public static string? Phone(string? raw)
		{
			if (string.IsNullOrWhiteSpace(raw)) return null;
			var t = raw.Trim();
			bool plus = t.StartsWith('+');
			var digits = new string(t.Where(char.IsAsciiDigit).ToArray());
			if (digits.Length is < 8 or > 15) return null;
			if (t.Any(c => !(char.IsAsciiDigit(c) || c is ' ' or '-' or '(' or ')' or '+'))) return null;
			return (plus ? "+" : "") + digits;
		}

		/// How many SMS parts a body takes: Arabic (any character outside GSM-7's basic set) is 70 per part (67 when
		/// split); Latin is 160 (153 when split).
		public static int Parts(string body)
		{
			if (string.IsNullOrEmpty(body)) return 0;
			bool unicode = body.Any(c => c > 127);
			int len = body.Length;
			return unicode ? (len <= 70 ? 1 : (int)Math.Ceiling(len / 67.0)) : (len <= 160 ? 1 : (int)Math.Ceiling(len / 153.0));
		}

		public static string? TemplateRefusal(HospitalSmsTemplateInput i)
		{
			if (!HospitalSmsKind.Templated.Contains(i.Kind ?? "")) return "not_found";
			if (string.IsNullOrWhiteSpace(i.BodyAr) || string.IsNullOrWhiteSpace(i.BodyEn)) return "Write the message in Arabic and in English";
			if (i.BodyAr.Trim().Length > 480 || i.BodyEn.Trim().Length > 480) return "A message is at most 480 characters";
			if (UnknownPlaceholder(i) != null) return "Unknown placeholder: {0}";
			return null;
		}

		/// The first placeholder a template uses that is not one of Placeholders, as written ("{clinic}"), or null.
		public static string? UnknownPlaceholder(HospitalSmsTemplateInput i)
		{
			foreach (Match m in Slot.Matches((i.BodyAr ?? "") + " " + (i.BodyEn ?? "")))
				if (!Placeholders.Contains(m.Groups[1].Value)) return m.Value;
			return null;
		}

		public static string? ComposeRefusal(string? body, string? phone)
		{
			if (string.IsNullOrWhiteSpace(body)) return "Write the message";
			if (body.Trim().Length > 640) return "A message is at most 640 characters";
			if (phone == null) return "Write a valid mobile number";
			return null;
		}

		public static bool CanCancel(string status) => status is HospitalSmsStatus.Queued or HospitalSmsStatus.Failed;
		public static string Time(int minute) => $"{minute / 60:00}:{minute % 60:00}";
		public static string Date(DateTime d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
	}

	public sealed class HospitalSmsService
	{
		public const string NoTables = "SMS is not installed yet (hospital_h70_sms.sql).";
		public const string NoProvider = "No SMS provider is connected yet; the messages stay queued";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalSmsService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private IQueryable<HospitalSmsMessage> Messages(BusinessContext ctx) => _db.Set<HospitalSmsMessage>().Where(m => m.CompanyId == ctx.CompanyId);

		// ---------------- writing a message (the events and staff) ----------------

		/// Writes one message for a patient from its kind's template, once per dedup key. Saves on its own and never
		/// throws: an event (a booking, a verified result) must not fail because a message could not be written.
		public static async Task<int?> QueueAsync(CrossDbContext db, int companyId, int patientId, string kind, IDictionary<string, string?> values,
			string sourceType, int sourceId, string dedupKey, int? employeeId, DateTime nowUtc, CancellationToken ct)
		{
			HospitalSmsMessage? row = null;
			try
			{
				var set = db.Set<HospitalSmsMessage>();
				if (await set.AnyAsync(m => m.CompanyId == companyId && m.DedupKey == dedupKey, ct)) return null;
				var p = await db.HospitalPatients.AsNoTracking().Where(x => x.ID == patientId && x.CompanyId == companyId).Select(x => new { x.Name, x.NameEn, x.Phone }).FirstOrDefaultAsync(ct);
				if (p == null) return null;
				var tpl = await db.Set<HospitalSmsTemplate>().AsNoTracking().FirstOrDefaultAsync(t => t.CompanyId == companyId && t.Kind == kind, ct);
				if (tpl is { IsActive: false }) return null;   // the administrator turned this kind off
				var (ar, en) = tpl == null ? HospitalSmsRules.DefaultTemplate(kind) : (tpl.BodyAr, tpl.BodyEn);
				const string lang = "ar";
				var co = await db.Companies.AsNoTracking().Where(c => c.CompanyID == companyId).Select(c => new { c.CompanyName, c.ComoanyNameAr }).FirstOrDefaultAsync(ct);
				var v = new Dictionary<string, string?>(values) { ["patient"] = p.Name, ["hospital"] = co?.ComoanyNameAr ?? co?.CompanyName };
				var phone = HospitalSmsRules.Phone(p.Phone);
				bool optedOut = await db.Set<HospitalSmsOptOut>().AnyAsync(o => o.CompanyId == companyId && o.PatientId == patientId, ct);
				var nos = await set.Where(m => m.CompanyId == companyId).Select(m => m.MessageNo).ToListAsync(ct);
				row = new HospitalSmsMessage
				{
					CompanyId = companyId, MessageNo = HospitalDentalPracticeRules.NextNo(nos, "SMS-", 6), PatientId = patientId, Phone = phone, Kind = kind, Language = lang,
					Body = Clip(HospitalSmsRules.Render(lang == "ar" ? ar : en, v), 640)!, SourceType = sourceType, SourceId = sourceId, DedupKey = dedupKey,
					QueuedAt = nowUtc, QueuedByEmployeeId = employeeId,
					Status = optedOut || phone == null ? HospitalSmsStatus.Skipped : HospitalSmsStatus.Queued,
					Error = optedOut ? "The patient asked for no messages" : phone == null ? "No valid mobile number on the patient's file" : null,
				};
				set.Add(row);
				await db.SaveChangesAsync(ct);
				return row.ID;
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { if (row != null) db.Entry(row).State = EntityState.Detached; return null; }
			catch (DbUpdateException) { if (row != null) db.Entry(row).State = EntityState.Detached; return null; }   // the same event, written at the same moment
		}

		/// An appointment's message (booked, reminder, missed): the doctor, the day and the time put in.
		public static async Task<int?> QueueAppointmentAsync(CrossDbContext db, int companyId, int appointmentId, string kind, int? employeeId, DateTime nowUtc, CancellationToken ct)
		{
			try
			{
				var a = await (from x in db.HospitalAppointments.AsNoTracking()
							   join d in db.HospitalPractitioners.AsNoTracking() on x.PractitionerId equals d.ID
							   where x.ID == appointmentId && x.CompanyId == companyId
							   select new { x.PatientId, x.AppointmentDate, x.StartMinute, d.Name }).FirstOrDefaultAsync(ct);
				if (a == null) return null;
				var v = new Dictionary<string, string?> { ["doctor"] = a.Name, ["date"] = HospitalSmsRules.Date(a.AppointmentDate), ["time"] = HospitalSmsRules.Time(a.StartMinute) };
				return await QueueAsync(db, companyId, a.PatientId, kind, v, "Appointment", appointmentId, $"appt:{appointmentId}:{kind.ToLowerInvariant()}", employeeId, nowUtc, ct);
			}
			catch (Exception) { return null; }
		}

		/// A verified lab order's "results ready".
		public static async Task<int?> QueueLabReadyAsync(CrossDbContext db, int companyId, int labOrderId, int? employeeId, DateTime nowUtc, CancellationToken ct)
		{
			try
			{
				var o = await db.Set<HospitalLabOrder>().AsNoTracking().Where(x => x.ID == labOrderId && x.CompanyId == companyId).Select(x => new { x.PatientId, x.Name }).FirstOrDefaultAsync(ct);
				if (o == null) return null;
				return await QueueAsync(db, companyId, o.PatientId, HospitalSmsKind.LabReady, new Dictionary<string, string?> { ["test"] = o.Name }, "LabOrder", labOrderId, $"lab:{labOrderId}:ready", employeeId, nowUtc, ct);
			}
			catch (Exception) { return null; }
		}

		/// Tomorrow's booked appointments each get their reminder — once, however often this runs (the screens call it).
		public async Task<int> SweepRemindersAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var tomorrow = HospitalNursingService.UtcToWard(nowUtc).Date.AddDays(1);
				var ids = await _db.HospitalAppointments.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.AppointmentDate == tomorrow && a.Status == HospitalAppointmentStatus.Booked).Select(a => a.ID).ToListAsync(ct);
				if (ids.Count == 0) return 0;
				var keys = ids.Select(i => $"appt:{i}:reminder").ToList();
				var have = await Messages(ctx).Where(m => keys.Contains(m.DedupKey)).Select(m => m.DedupKey).ToListAsync(ct);
				int n = 0;
				foreach (var id in ids.Where(i => !have.Contains($"appt:{i}:reminder")))
					if (await QueueAppointmentAsync(_db, ctx.CompanyId, id, HospitalSmsKind.Reminder, null, nowUtc, ct) != null) n++;
				return n;
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return 0; }
		}

		/// A message typed by staff, to a patient (their file's number unless another is written) or to a number.
		public async Task<HospitalResult> ComposeAsync(BusinessContext ctx, HospitalSmsComposeInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Book, null, ct)) return HospitalResult.Fail("forbidden");
			string? phone = HospitalSmsRules.Phone(input.Phone);
			if (input.PatientId is int pid)
			{
				var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == pid && x.CompanyId == ctx.CompanyId).Select(x => new { x.Phone }).FirstOrDefaultAsync(ct);
				if (p == null) return HospitalResult.Fail("not_found");
				phone ??= HospitalSmsRules.Phone(p.Phone);
			}
			if (HospitalSmsRules.ComposeRefusal(input.Body, phone) is string why) return HospitalResult.Fail(why);
			try
			{
				if (input.PatientId is int op && await _db.Set<HospitalSmsOptOut>().AnyAsync(o => o.CompanyId == ctx.CompanyId && o.PatientId == op, ct))
					return HospitalResult.Fail("This patient asked for no messages");
				for (int attempt = 0; attempt < 3; attempt++)
				{
					var nos = await Messages(ctx).Select(m => m.MessageNo).ToListAsync(ct);
					var no = HospitalDentalPracticeRules.NextNo(nos, "SMS-", 6);
					var row = new HospitalSmsMessage
					{
						CompanyId = ctx.CompanyId, MessageNo = no, PatientId = input.PatientId, Phone = phone, Kind = HospitalSmsKind.Custom, Language = input.Language == "en" ? "en" : "ar",
						Body = Clip(input.Body, 640)!, DedupKey = "custom:" + no, Status = HospitalSmsStatus.Queued, QueuedAt = nowUtc, QueuedByEmployeeId = employeeId,
					};
					_db.Add(row);
					try { await _db.SaveChangesAsync(ct); return HospitalResult.Success(row.ID); }
					catch (DbUpdateException) { _db.Entry(row).State = EntityState.Detached; }
				}
				return HospitalResult.Fail("The change could not be saved");
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> CancelAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Book, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var m = await Messages(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (m == null) return HospitalResult.Fail("not_found");
				if (!HospitalSmsRules.CanCancel(m.Status)) return HospitalResult.Fail("Only a message still waiting to go can be cancelled");
				m.Status = HospitalSmsStatus.Cancelled; m.CancelledAt = nowUtc; m.CancelledByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(m.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// Sends the queue (and retries a failed message up to three tries) through the connected provider.
		public async Task<HospitalResult> DispatchAsync(BusinessContext ctx, ISmsSender sender, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			if (!sender.Configured) return HospitalResult.Fail(NoProvider);
			try
			{
				var due = await Messages(ctx).Where(m => m.Status == HospitalSmsStatus.Queued || (m.Status == HospitalSmsStatus.Failed && m.Attempts < 3))
					.OrderBy(m => m.QueuedAt).Take(100).ToListAsync(ct);
				int sent = 0;
				foreach (var m in due)
				{
					m.Attempts++;
					var r = await sender.SendAsync(m.Phone!, m.Body, ct);
					if (r.Ok) { m.Status = HospitalSmsStatus.Sent; m.SentAt = nowUtc; m.ProviderRef = Clip(r.ProviderRef, 100); m.Error = null; sent++; }
					else { m.Status = HospitalSmsStatus.Failed; m.Error = Clip(r.Error ?? "The provider refused the message", 300); }
					await _db.SaveChangesAsync(ct);
				}
				return HospitalResult.Success(sent);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		// ---------------- opt-outs and templates ----------------

		public async Task<HospitalResult> OptOutAsync(BusinessContext ctx, int patientId, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Book, null, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == patientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			try
			{
				var set = _db.Set<HospitalSmsOptOut>();
				if (await set.AnyAsync(o => o.CompanyId == ctx.CompanyId && o.PatientId == patientId, ct)) return HospitalResult.Fail("This patient already asked for no messages");
				var row = new HospitalSmsOptOut { CompanyId = ctx.CompanyId, PatientId = patientId, Reason = Clip(reason, 200), At = nowUtc, ByEmployeeId = employeeId };
				set.Add(row);
				// what is still waiting for this patient is not sent
				foreach (var m in await Messages(ctx).Where(m => m.PatientId == patientId && (m.Status == HospitalSmsStatus.Queued || m.Status == HospitalSmsStatus.Failed)).ToListAsync(ct))
				{ m.Status = HospitalSmsStatus.Cancelled; m.CancelledAt = nowUtc; m.CancelledByEmployeeId = employeeId; }
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> OptInAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Book, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var row = await _db.Set<HospitalSmsOptOut>().FirstOrDefaultAsync(o => o.CompanyId == ctx.CompanyId && o.PatientId == patientId, ct);
				if (row == null) return HospitalResult.Fail("not_found");
				_db.Remove(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(patientId);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> SaveTemplateAsync(BusinessContext ctx, HospitalSmsTemplateInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalSmsRules.TemplateRefusal(input) is string why)
				return why == "Unknown placeholder: {0}" ? HospitalResult.Fail(why, HospitalSmsRules.UnknownPlaceholder(input)!) : HospitalResult.Fail(why);
			try
			{
				var set = _db.Set<HospitalSmsTemplate>();
				var row = await set.FirstOrDefaultAsync(t => t.CompanyId == ctx.CompanyId && t.Kind == input.Kind, ct);
				if (row == null) { row = new HospitalSmsTemplate { CompanyId = ctx.CompanyId, Kind = input.Kind! }; set.Add(row); }
				row.BodyAr = input.BodyAr!.Trim(); row.BodyEn = input.BodyEn!.Trim(); row.IsActive = input.IsActive; row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		// ---------------- reading ----------------

		public async Task<IReadOnlyList<HospitalSmsTemplateView>> TemplatesAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			List<HospitalSmsTemplate> rows;
			try { rows = await _db.Set<HospitalSmsTemplate>().AsNoTracking().Where(t => t.CompanyId == ctx.CompanyId).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { rows = new(); }
			return HospitalSmsKind.Templated.Select(k =>
			{
				var r = rows.FirstOrDefault(x => x.Kind == k); var (ar, en) = HospitalSmsRules.DefaultTemplate(k);
				return new HospitalSmsTemplateView(k, r?.BodyAr ?? ar, r?.BodyEn ?? en, r?.IsActive ?? true, r != null);
			}).ToList();
		}

		public async Task<HospitalSmsFigures> FiguresAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var q = Messages(ctx);
				var dayStart = HospitalNursingService.WardToUtc(HospitalNursingService.UtcToWard(nowUtc).Date);
				var since = nowUtc.AddDays(-30);
				return new(true, await q.CountAsync(m => m.Status == HospitalSmsStatus.Queued, ct), await q.CountAsync(m => m.Status == HospitalSmsStatus.Sent && m.SentAt >= dayStart, ct),
					await q.CountAsync(m => m.Status == HospitalSmsStatus.Failed, ct), await q.CountAsync(m => m.Status == HospitalSmsStatus.Skipped && m.QueuedAt >= since, ct),
					await _db.Set<HospitalSmsOptOut>().CountAsync(o => o.CompanyId == ctx.CompanyId, ct));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new(false, 0, 0, 0, 0, 0); }
		}

		public async Task<(List<HospitalSmsRow> Rows, int Total)> ListAsync(BusinessContext ctx, string? q, string? kind, string? state, int page, int pageSize, CancellationToken ct = default)
		{
			try
			{
				var rows = from m in Messages(ctx).AsNoTracking()
						   join p0 in _db.HospitalPatients.AsNoTracking() on m.PatientId equals p0.ID into pj
						   from p in pj.DefaultIfEmpty()
						   select new { m, Name = p == null ? null : p.Name, NameEn = p == null ? null : p.NameEn, Mrn = p == null ? null : p.Mrn };
				if (!string.IsNullOrEmpty(kind)) rows = rows.Where(x => x.m.Kind == kind);
				if (state == "waiting") rows = rows.Where(x => x.m.Status == HospitalSmsStatus.Queued || x.m.Status == HospitalSmsStatus.Failed);
				else if (!string.IsNullOrEmpty(state)) rows = rows.Where(x => x.m.Status == state);
				foreach (var t in (q ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
				{
					var term = t.Trim();
					rows = rows.Where(x => x.m.MessageNo.Contains(term) || (x.m.Phone != null && x.m.Phone.Contains(term)) || x.m.Body.Contains(term)
						|| (x.Name != null && x.Name.Contains(term)) || (x.NameEn != null && x.NameEn.Contains(term)) || (x.Mrn != null && x.Mrn.Contains(term)));
				}
				int total = await rows.CountAsync(ct);
				var list = await rows.OrderByDescending(x => x.m.QueuedAt).ThenByDescending(x => x.m.ID).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
				return (list.Select(x => new HospitalSmsRow(x.m, x.Name, x.NameEn, x.Mrn)).ToList(), total);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (new(), 0); }
		}

		public async Task<IReadOnlyList<HospitalSmsOptOutRow>> OptOutsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try
			{
				return await (from o in _db.Set<HospitalSmsOptOut>().AsNoTracking()
							  join p in _db.HospitalPatients.AsNoTracking() on o.PatientId equals p.ID
							  where o.CompanyId == ctx.CompanyId
							  orderby o.At descending
							  select new HospitalSmsOptOutRow(o, p.Name, p.NameEn, p.Mrn)).ToListAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return Array.Empty<HospitalSmsOptOutRow>(); }
		}

		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
	}
}
