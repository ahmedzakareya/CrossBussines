using System.Security.Cryptography;
using System.Text;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Crm;
using Microsoft.EntityFrameworkCore;
using S = CrossBuy.BL.CrmIdentity.CrmSequenceRules;
using Keys = CrossBuy.BL.CrmIdentity.CrmDuplicateRules;

namespace CrossBuy.BL.CrmIdentity
{
	// CRM C6 — follow-up sequences and meeting booking.
	//
	// A SEQUENCE is a list of steps on day offsets. ENROLLING a lead or contact starts the clock; the CRM worker (every
	// five minutes, in the company's own scope) first checks whether the person has replied, booked, converted or
	// withdrawn consent — any of which ENDS the walk — and only then runs the steps that are due. A step that cannot run
	// (no e-mail address, no WhatsApp consent) is recorded as skipped with its reason and the walk goes on. E-mail is
	// QUEUED to the outbox (never sent from here), WhatsApp goes through the inbox's own template send (C4), calls and
	// tasks become ordinary CRM activities with a reminder for the owner.
	// BOOKING: each rep has a public page that offers the free times of their own working calendar (attendance policy,
	// public holidays, approved leave, meetings already in the CRM). The anonymous request writes the booking row ONLY;
	// the worker turns it into a meeting activity on the right lead/contact (or a new lead) and tells the rep.

	public sealed record SequenceRow(CrmSequence Seq, int Steps, int Active, int Completed, int Stopped, int Replied, int Booked, PersonRef? Owner);
	public sealed record EnrollmentRow(CrmSequenceEnrollment E, string Name, string? Reach, PersonRef? Owner, CrmSequenceStep? Next, DateTime? NextLocal, CrmSequenceEvent? Last);
	public sealed record Candidate(string Type, int Id, string Name, string? Sub, bool Already);
	public sealed record SequenceFile(CrmSequence Seq, List<CrmSequenceStep> Steps, Dictionary<int, int> RanByStep, Dictionary<string, int> Counts);
	public sealed record BookingLinkView(CrmBookingLink Link, PersonRef Person, List<CrmBooking> Upcoming);
	public sealed record PublicBookingPage(CrmBookingLink Link, string RepName, bool HasPhoto, string Company, List<(DateTime day, List<DateTime> slots)> Days);
	public sealed record RunResult(int Stopped, int Ran, int Bookings);

	public interface ICrmSequenceService
	{
		Task<(List<SequenceRow> rows, int total)> ListAsync(int cid, string? q, int page, int pageSize);
		Task<SequenceFile?> FileAsync(int cid, int id);
		Task<(bool ok, string? error, int id)> SaveAsync(int cid, int? me, int id, string name, string? nameEn, string target, bool isActive, bool skipNonWorkDays, IReadOnlyList<S.Step> steps);
		Task<(List<EnrollmentRow> rows, int total)> EnrollmentsAsync(int cid, HashSet<int>? visible, int seqId, string? status, string? q, int page, int pageSize);
		Task<List<Candidate>> CandidatesAsync(int cid, HashSet<int>? visible, int seqId, string? q);
		Task<(bool ok, string? error, int enrolled, int skipped)> EnrollAsync(int cid, HashSet<int>? visible, int? me, int seqId, IReadOnlyList<int> ids);
		Task<(bool ok, string? error)> StopAsync(int cid, HashSet<int>? visible, int? me, int enrollmentId);
		Task<List<CrmSequenceEvent>> EventsAsync(int cid, int enrollmentId);
		Task<RunResult> RunAsync(int cid, CancellationToken ct = default);
		// booking
		Task<BookingLinkView?> MyLinkAsync(int cid, int? me);
		Task<(bool ok, string? error)> SaveLinkAsync(int cid, int? me, string? title, int duration, int buffer, int daysAhead, int notice, bool isActive, string origin);
		Task<PublicBookingPage?> PublicPageAsync(string key);
		Task<(bool ok, string code)> BookAsync(string key, DateTime startLocal, string? name, string? email, string? phone, string? note);
		Task<string?> CalendarAsync(string token);
		Task<string?> BookingUrlAsync(int cid, int employeeId);
		Task<int> ProcessBookingsAsync(int cid);
	}

	public sealed class CrmSequenceService : ICrmSequenceService
	{
		private readonly CrossDbContext _db;
		private readonly ICrmService _crm;
		private readonly ICommService _mail;
		private readonly INotificationService _notify;
		private readonly IAttendanceService _attendance;
		private readonly IHolidayService _holidays;
		private readonly IServiceProvider _sp;
		public CrmSequenceService(CrossDbContext db, ICrmService crm, ICommService mail, INotificationService notify, IAttendanceService attendance, IHolidayService holidays, IServiceProvider sp)
		{ _db = db; _crm = crm; _mail = mail; _notify = notify; _attendance = attendance; _holidays = holidays; _sp = sp; }

		static bool IsAr => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
		static string N(string n, string? en) => !IsAr && !string.IsNullOrWhiteSpace(en) ? en! : n;
		static string NewKey(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

		// ------------------------------------------------------------------ the owner's working calendar
		sealed record WorkCal(Func<DateTime, bool> IsWorkDay, TimeSpan Start, TimeSpan End);

		/// The rep's own week (their attendance policy; Sunday–Thursday 08:00–17:00 when none is assigned), minus public
		/// holidays and approved leave in the window.
		async Task<WorkCal> CalendarOfAsync(int cid, int? employeeId, DateTime from, DateTime to)
		{
			var policy = employeeId is int e ? await _attendance.PolicyForAsync(e) : null;
			var holidays = await _holidays.HolidayDatesAsync(cid, from.Date, to.Date);
			var leave = employeeId is int le
				? await _db.LeaveRequests.AsNoTracking().Where(r => r.EmployeeID == le && r.Status == 1 && r.StartDate.Date <= to.Date && r.EndDate.Date >= from.Date).Select(r => new { r.StartDate, r.EndDate }).ToListAsync()
				: new();
			bool Week(DateTime d) => policy != null ? AttendanceService.IsWorkDay(policy, d) : d.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday);
			bool Work(DateTime d) => Week(d) && !holidays.Contains(d.Date) && !leave.Any(l => l.StartDate.Date <= d.Date && l.EndDate.Date >= d.Date);
			var start = policy?.WorkStartTime ?? TimeSpan.FromHours(8); var end = policy?.WorkEndTime ?? TimeSpan.FromHours(17);
			if (end <= start) { start = TimeSpan.FromHours(8); end = TimeSpan.FromHours(17); }
			return new WorkCal(Work, start, end);
		}

		async Task<Dictionary<int, PersonRef>> PeopleAsync(int cid, IEnumerable<int?> ids)
		{
			var set = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
			if (set.Count == 0) return new();
			return (await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == cid && set.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync())
				.ToDictionary(e => e.ID, e => new PersonRef(e.ID, N(e.FullName ?? "", e.FullNameEn), !string.IsNullOrWhiteSpace(e.ProfileImage)));
		}

		// ------------------------------------------------------------------ the person on a walk
		sealed record Subject(string Type, int Id, string Name, string? Email, string? Phone, int? Owner, string? Company, string? LeadStatus, int? AccountId);

		async Task<Dictionary<(string, int), Subject>> SubjectsAsync(int cid, IEnumerable<(string type, int id)> keys)
		{
			var list = keys.Distinct().ToList();
			var leadIds = list.Where(k => k.type == "Lead").Select(k => k.id).ToList();
			var contactIds = list.Where(k => k.type == "Contact").Select(k => k.id).ToList();
			var map = new Dictionary<(string, int), Subject>();
			foreach (var l in await _db.Leads.AsNoTracking().Where(l => l.CompanyID == cid && leadIds.Contains(l.ID)).ToListAsync())
				map[("Lead", l.ID)] = new Subject("Lead", l.ID, N(l.Name, l.NameEn), l.Email, l.Phone, l.OwnerEmployeeId, l.Company, l.Status, l.AccountId);
			var contacts = await _db.CrmContacts.AsNoTracking().Where(c => c.CompanyID == cid && contactIds.Contains(c.ID)).ToListAsync();
			var accIds = contacts.Select(c => c.AccountId).Distinct().ToList();
			var accounts = await _db.CrmAccounts.AsNoTracking().Where(a => a.CompanyID == cid && accIds.Contains(a.ID)).ToDictionaryAsync(a => a.ID, a => new { Name = N(a.Name, a.NameEn), a.OwnerEmployeeId });
			foreach (var c in contacts)
			{
				var acc = accounts.GetValueOrDefault(c.AccountId);
				map[("Contact", c.ID)] = new Subject("Contact", c.ID, N(c.Name, c.NameEn), c.Email, c.Phone, c.OwnerEmployeeId ?? acc?.OwnerEmployeeId, acc?.Name, null, c.AccountId);
			}
			return map;
		}

		// ------------------------------------------------------------------ sequences
		public async Task<(List<SequenceRow> rows, int total)> ListAsync(int cid, string? q, int page, int pageSize)
		{
			var query = _db.Set<CrmSequence>().AsNoTracking().Where(s => s.CompanyId == cid);
			foreach (var t in SearchTerms.Parse(q)) { var s = t; query = query.Where(x => x.Name.Contains(s) || (x.NameEn != null && x.NameEn.Contains(s))); }
			var total = await query.CountAsync();
			pageSize = Math.Clamp(pageSize, 10, 100); page = Math.Max(1, page);
			var seqs = await query.OrderByDescending(s => s.IsActive).ThenByDescending(s => s.ID).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			var ids = seqs.Select(s => s.ID).ToList();
			var steps = await _db.Set<CrmSequenceStep>().AsNoTracking().Where(x => x.CompanyId == cid && ids.Contains(x.SequenceId)).GroupBy(x => x.SequenceId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
			var enr = await _db.Set<CrmSequenceEnrollment>().AsNoTracking().Where(x => x.CompanyId == cid && ids.Contains(x.SequenceId))
				.GroupBy(x => new { x.SequenceId, x.Status, x.StopReason }).Select(g => new { g.Key.SequenceId, g.Key.Status, g.Key.StopReason, N = g.Count() }).ToListAsync();
			var people = await PeopleAsync(cid, seqs.Select(s => s.CreatedByEmployeeId));
			int C(int id, Func<string, string?, bool> f) => enr.Where(e => e.SequenceId == id && f(e.Status, e.StopReason)).Sum(e => e.N);
			return (seqs.Select(s => new SequenceRow(s, steps.GetValueOrDefault(s.ID), C(s.ID, (st, _) => st == "Active"), C(s.ID, (st, _) => st == "Completed"), C(s.ID, (st, _) => st == "Stopped"),
				C(s.ID, (_, r) => r == "Replied"), C(s.ID, (_, r) => r == "Booked"), s.CreatedByEmployeeId is int p ? people.GetValueOrDefault(p) : null)).ToList(), total);
		}

		public async Task<SequenceFile?> FileAsync(int cid, int id)
		{
			var s = await _db.Set<CrmSequence>().AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == id);
			if (s == null) return null;
			var steps = await _db.Set<CrmSequenceStep>().AsNoTracking().Where(x => x.CompanyId == cid && x.SequenceId == id).OrderBy(x => x.StepNo).ToListAsync();
			var ran = await (from ev in _db.Set<CrmSequenceEvent>().AsNoTracking()
							 join e in _db.Set<CrmSequenceEnrollment>().AsNoTracking() on ev.EnrollmentId equals e.ID
							 where e.CompanyId == cid && e.SequenceId == id && ev.StepNo != null && (ev.Outcome == "Queued" || ev.Outcome == "Sent" || ev.Outcome == "Task")
							 group ev by ev.StepNo into g select new { Step = g.Key!.Value, N = g.Count() }).ToDictionaryAsync(x => x.Step, x => x.N);
			var counts = await _db.Set<CrmSequenceEnrollment>().AsNoTracking().Where(x => x.CompanyId == cid && x.SequenceId == id)
				.GroupBy(x => x.StopReason ?? x.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
			return new SequenceFile(s, steps, ran, counts);
		}

		public async Task<(bool ok, string? error, int id)> SaveAsync(int cid, int? me, int id, string name, string? nameEn, string target, bool isActive, bool skipNonWorkDays, IReadOnlyList<S.Step> steps)
		{
			name = (name ?? "").Trim();
			if (name.Length < 2) return (false, "The sequence needs a name", 0);
			if (target is not ("Lead" or "Contact")) return (false, "Choose leads or contacts", 0);
			var refusal = S.StepsRefusal(steps);
			if (refusal != null) return (false, refusal, 0);
			if (steps.Any(x => x.Kind == "WhatsApp"))
			{
				// a template must be one the company's WhatsApp channel lists as approved
				var allowed = (await _db.Set<CrmChannel>().AsNoTracking().Where(c => c.CompanyId == cid && c.Kind == "WhatsApp" && c.IsActive).Select(c => c.ConfigJson).ToListAsync())
					.SelectMany(j => (System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(j) ? "{}" : j).RootElement.TryGetProperty("Templates", out var t) ? t.GetString() ?? "" : "")
						.Split(new[] { ',', '،' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToHashSet();
				if (steps.Any(x => x.Kind == "WhatsApp" && !allowed.Contains(x.Template!.Trim()))) return (false, "A WhatsApp step needs an approved template", 0);
			}
			CrmSequence seq;
			await using var tx = await _db.Database.BeginTransactionAsync();
			if (id > 0)
			{
				seq = await _db.Set<CrmSequence>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == id) ?? throw new InvalidOperationException("Not found");
				if (seq.Target != target && await _db.Set<CrmSequenceEnrollment>().AnyAsync(x => x.CompanyId == cid && x.SequenceId == id)) return (false, "People are already enrolled, so the audience cannot change", 0);
				await _db.Set<CrmSequenceStep>().Where(x => x.CompanyId == cid && x.SequenceId == id).ExecuteDeleteAsync();
			}
			else { seq = new CrmSequence { CompanyId = cid, CreatedByEmployeeId = me, CreatedAt = DateTime.UtcNow }; _db.Set<CrmSequence>().Add(seq); }
			seq.Name = name; seq.NameEn = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim(); seq.Target = target; seq.IsActive = isActive; seq.SkipNonWorkDays = skipNonWorkDays;
			await _db.SaveChangesAsync();
			for (var i = 0; i < steps.Count; i++)
			{
				var x = steps[i];
				_db.Set<CrmSequenceStep>().Add(new CrmSequenceStep { CompanyId = cid, SequenceId = seq.ID, StepNo = i + 1, DayOffset = x.DayOffset, Kind = x.Kind,
					Subject = string.IsNullOrWhiteSpace(x.Subject) ? null : x.Subject.Trim(), Body = string.IsNullOrWhiteSpace(x.Body) ? null : x.Body.Trim(),
					Template = x.Kind == "WhatsApp" ? x.Template!.Trim() : null });
			}
			await _db.SaveChangesAsync();
			// running walks keep their place; one pointing past the new last step finishes on the next pass
			await tx.CommitAsync();
			return (true, null, seq.ID);
		}

		public async Task<(List<EnrollmentRow> rows, int total)> EnrollmentsAsync(int cid, HashSet<int>? visible, int seqId, string? status, string? q, int page, int pageSize)
		{
			var query = _db.Set<CrmSequenceEnrollment>().AsNoTracking().Where(x => x.CompanyId == cid && x.SequenceId == seqId);
			if (visible != null) query = query.Where(x => x.OwnerEmployeeId != null && visible.Contains(x.OwnerEmployeeId.Value));
			if (!string.IsNullOrEmpty(status)) query = status is "Active" or "Completed" or "Stopped" ? query.Where(x => x.Status == status) : query.Where(x => x.StopReason == status);
			var all = await query.OrderByDescending(x => x.Status == "Active").ThenBy(x => x.NextDueAt).ThenByDescending(x => x.ID).ToListAsync();
			var subjects = await SubjectsAsync(cid, all.Select(e => (e.SubjectType, e.SubjectId)));
			IEnumerable<CrmSequenceEnrollment> list = all;
			foreach (var t in SearchTerms.Parse(q))
			{
				var s = t;
				list = list.Where(e => subjects.TryGetValue((e.SubjectType, e.SubjectId), out var p) && (p.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
					|| (p.Email ?? "").Contains(s, StringComparison.OrdinalIgnoreCase) || (p.Phone ?? "").Contains(s)));
			}
			var filtered = list.ToList();
			pageSize = Math.Clamp(pageSize, 10, 100); page = Math.Max(1, page);
			var slice = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
			var ids = slice.Select(e => e.ID).ToList();
			var steps = await _db.Set<CrmSequenceStep>().AsNoTracking().Where(x => x.CompanyId == cid && x.SequenceId == seqId).ToDictionaryAsync(x => x.StepNo);
			var last = (await _db.Set<CrmSequenceEvent>().AsNoTracking().Where(x => x.CompanyId == cid && ids.Contains(x.EnrollmentId)).ToListAsync())
				.GroupBy(x => x.EnrollmentId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.ID).First());
			var people = await PeopleAsync(cid, slice.Select(e => e.OwnerEmployeeId));
			return (slice.Select(e =>
			{
				var p = subjects.GetValueOrDefault((e.SubjectType, e.SubjectId));
				return new EnrollmentRow(e, p?.Name ?? ("#" + e.SubjectId), p == null ? null : (p.Phone ?? p.Email), e.OwnerEmployeeId is int o ? people.GetValueOrDefault(o) : null,
					e.NextStepNo is int n ? steps.GetValueOrDefault(n) : null, e.NextDueAt?.ToLocalTime(), last.GetValueOrDefault(e.ID));
			}).ToList(), filtered.Count);
		}

		public async Task<List<Candidate>> CandidatesAsync(int cid, HashSet<int>? visible, int seqId, string? q)
		{
			var seq = await _db.Set<CrmSequence>().AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == seqId);
			if (seq == null) return new();
			var terms = SearchTerms.Parse(q);
			var active = (await _db.Set<CrmSequenceEnrollment>().AsNoTracking().Where(x => x.CompanyId == cid && x.SequenceId == seqId && x.Status == "Active").Select(x => x.SubjectId).ToListAsync()).ToHashSet();
			if (seq.Target == "Lead")
			{
				var lq = _db.Leads.AsNoTracking().Where(l => l.CompanyID == cid && l.Status != "Converted" && l.Status != "Lost");
				if (visible != null) lq = lq.Where(l => l.OwnerEmployeeId != null && visible.Contains(l.OwnerEmployeeId.Value));
				foreach (var t in terms) { var s = t; lq = lq.Where(l => l.Name.Contains(s) || (l.NameEn != null && l.NameEn.Contains(s)) || (l.Phone != null && l.Phone.Contains(s)) || (l.Email != null && l.Email.Contains(s)) || (l.Company != null && l.Company.Contains(s))); }
				return (await lq.OrderByDescending(l => l.ID).Take(30).ToListAsync())
					.Select(l => new Candidate("Lead", l.ID, N(l.Name, l.NameEn), string.Join(" · ", new[] { l.Company, l.Phone ?? l.Email }.Where(x => !string.IsNullOrWhiteSpace(x))), active.Contains(l.ID))).ToList();
			}
			var cq = _db.CrmContacts.AsNoTracking().Where(c => c.CompanyID == cid);
			if (visible != null) cq = cq.Where(c => c.OwnerEmployeeId == null || visible.Contains(c.OwnerEmployeeId.Value));
			foreach (var t in terms) { var s = t; cq = cq.Where(c => c.Name.Contains(s) || (c.NameEn != null && c.NameEn.Contains(s)) || (c.Phone != null && c.Phone.Contains(s)) || (c.Email != null && c.Email.Contains(s))); }
			return (await cq.OrderByDescending(c => c.ID).Take(30).ToListAsync())
				.Select(c => new Candidate("Contact", c.ID, N(c.Name, c.NameEn), c.Phone ?? c.Email, active.Contains(c.ID))).ToList();
		}

		public async Task<(bool ok, string? error, int enrolled, int skipped)> EnrollAsync(int cid, HashSet<int>? visible, int? me, int seqId, IReadOnlyList<int> ids)
		{
			var seq = await _db.Set<CrmSequence>().AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == seqId);
			if (seq == null) return (false, "Record not found", 0, 0);
			if (!seq.IsActive) return (false, "This sequence is paused", 0, 0);
			if (ids.Count == 0) return (false, "Choose at least one person", 0, 0);
			if (ids.Count > 200) return (false, "At most 200 people at a time", 0, 0);
			var first = await _db.Set<CrmSequenceStep>().AsNoTracking().Where(x => x.CompanyId == cid && x.SequenceId == seqId).OrderBy(x => x.StepNo).FirstOrDefaultAsync();
			if (first == null) return (false, "A sequence needs at least one step", 0, 0);
			var subjects = await SubjectsAsync(cid, ids.Distinct().Select(i => (seq.Target, i)));
			var active = (await _db.Set<CrmSequenceEnrollment>().AsNoTracking().Where(x => x.CompanyId == cid && x.SequenceId == seqId && x.Status == "Active" && ids.Contains(x.SubjectId)).Select(x => x.SubjectId).ToListAsync()).ToHashSet();
			int enrolled = 0, skipped = 0;
			var now = DateTime.Now;
			foreach (var id in ids.Distinct())
			{
				if (!subjects.TryGetValue((seq.Target, id), out var p) || active.Contains(id)) { skipped++; continue; }
				var owner = p.Owner ?? me;
				if (visible != null && (owner == null || !visible.Contains(owner.Value))) { skipped++; continue; }
				var cal = await CalendarOfAsync(cid, owner, now, now.AddDays(45));
				var due = S.DueAt(now, first.DayOffset, cal.IsWorkDay, cal.Start, seq.SkipNonWorkDays);
				var e = new CrmSequenceEnrollment { CompanyId = cid, SequenceId = seqId, SubjectType = seq.Target, SubjectId = id, OwnerEmployeeId = owner, EnrolledAt = DateTime.UtcNow,
					EnrolledByEmployeeId = me, Status = "Active", NextStepNo = first.StepNo, NextDueAt = due.ToUniversalTime() };
				_db.Set<CrmSequenceEnrollment>().Add(e);
				await _db.SaveChangesAsync();
				_db.Set<CrmSequenceEvent>().Add(new CrmSequenceEvent { CompanyId = cid, EnrollmentId = e.ID, At = DateTime.UtcNow, Outcome = "Enrolled" });
				await _db.SaveChangesAsync();
				enrolled++;
			}
			return (true, null, enrolled, skipped);
		}

		public async Task<(bool ok, string? error)> StopAsync(int cid, HashSet<int>? visible, int? me, int enrollmentId)
		{
			var e = await _db.Set<CrmSequenceEnrollment>().FirstOrDefaultAsync(x => x.CompanyId == cid && x.ID == enrollmentId);
			if (e == null || (visible != null && (e.OwnerEmployeeId == null || !visible.Contains(e.OwnerEmployeeId.Value)))) return (false, "Record not found");
			if (e.Status != "Active") return (false, "This walk has already ended");
			await EndAsync(e, "Manual", me is int m ? "#" + m : null);
			return (true, null);
		}

		async Task EndAsync(CrmSequenceEnrollment e, string reason, string? detail)
		{
			e.Status = "Stopped"; e.StopReason = reason; e.StoppedAt = DateTime.UtcNow; e.NextStepNo = null; e.NextDueAt = null;
			_db.Set<CrmSequenceEvent>().Add(new CrmSequenceEvent { CompanyId = e.CompanyId, EnrollmentId = e.ID, At = DateTime.UtcNow, Outcome = "Stopped", Detail = reason + (detail != null ? " " + detail : "") });
			await _db.SaveChangesAsync();
		}

		public Task<List<CrmSequenceEvent>> EventsAsync(int cid, int enrollmentId)
			=> _db.Set<CrmSequenceEvent>().AsNoTracking().Where(x => x.CompanyId == cid && x.EnrollmentId == enrollmentId).OrderBy(x => x.ID).ToListAsync();

		// ------------------------------------------------------------------ the worker's pass
		public async Task<RunResult> RunAsync(int cid, CancellationToken ct = default)
		{
			var bookings = await ProcessBookingsAsync(cid);
			var active = await _db.Set<CrmSequenceEnrollment>().Where(x => x.CompanyId == cid && x.Status == "Active").ToListAsync(ct);
			if (active.Count == 0) return new RunResult(0, 0, bookings);
			var seqs = await _db.Set<CrmSequence>().AsNoTracking().Where(x => x.CompanyId == cid).ToDictionaryAsync(x => x.ID, ct);
			var steps = (await _db.Set<CrmSequenceStep>().AsNoTracking().Where(x => x.CompanyId == cid).ToListAsync(ct)).GroupBy(x => x.SequenceId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.StepNo).ToList());
			var subjects = await SubjectsAsync(cid, active.Select(e => (e.SubjectType, e.SubjectId)));
			int stopped = 0, ran = 0;
			foreach (var e in active)
			{
				ct.ThrowIfCancellationRequested();
				try
				{
					var p = subjects.GetValueOrDefault((e.SubjectType, e.SubjectId));
					if (p == null) { await EndAsync(e, "Inactive", "record removed"); stopped++; continue; }
					// 1) has the person answered in some way? then the walk is over
					var reason = await ExitReasonAsync(cid, e, p);
					if (reason != null) { await EndAsync(e, reason, null); stopped++; continue; }
					if (!seqs.TryGetValue(e.SequenceId, out var seq) || !seq.IsActive) continue;   // a paused sequence holds its walks
					if (e.NextDueAt == null || e.NextDueAt > DateTime.UtcNow) continue;
					var list = steps.GetValueOrDefault(e.SequenceId) ?? new();
					var step = list.FirstOrDefault(x => x.StepNo >= (e.NextStepNo ?? int.MaxValue));
					if (step == null) { await CompleteAsync(e); continue; }
					// 2) claim the step: the event row is unique per (walk, step), so a second worker cannot run it again
					var ev = new CrmSequenceEvent { CompanyId = cid, EnrollmentId = e.ID, StepNo = step.StepNo, At = DateTime.UtcNow, Outcome = "Skipped", Detail = "running" };
					_db.Set<CrmSequenceEvent>().Add(ev);
					try { await _db.SaveChangesAsync(ct); }
					catch (DbUpdateException) { _db.Entry(ev).State = EntityState.Detached; continue; }
					var (outcome, detail, refId) = await RunStepAsync(cid, e, p, step);
					ev.Outcome = outcome; ev.Detail = detail?.Length > 300 ? detail[..300] : detail; ev.RefId = refId; ev.At = DateTime.UtcNow;
					ran++;
					// 3) the next step's time, from the enrolment day, in the owner's calendar
					var next = list.FirstOrDefault(x => x.StepNo > step.StepNo);
					if (next == null) { await _db.SaveChangesAsync(ct); await CompleteAsync(e); continue; }
					var enrolledLocal = e.EnrolledAt.ToLocalTime();
					var cal = await CalendarOfAsync(cid, e.OwnerEmployeeId, enrolledLocal, enrolledLocal.AddDays(next.DayOffset + 45));
					var due = S.DueAt(enrolledLocal, next.DayOffset, cal.IsWorkDay, cal.Start, seq.SkipNonWorkDays);
					e.NextStepNo = next.StepNo; e.NextDueAt = due.ToUniversalTime() < DateTime.UtcNow ? DateTime.UtcNow : due.ToUniversalTime();
					await _db.SaveChangesAsync(ct);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					// one person's failure never stops the others; the step stays claimed and says why
					_db.ChangeTracker.Clear();
					_sp.GetService<ILogger<CrmSequenceService>>()?.LogError(ex, "CRM sequence step failed for enrollment {Id}", e.ID);
				}
			}
			return new RunResult(stopped, ran, bookings);
		}

		async Task CompleteAsync(CrmSequenceEnrollment e)
		{
			e.Status = "Completed"; e.NextStepNo = null; e.NextDueAt = null;
			_db.Set<CrmSequenceEvent>().Add(new CrmSequenceEvent { CompanyId = e.CompanyId, EnrollmentId = e.ID, At = DateTime.UtcNow, Outcome = "Completed" });
			await _db.SaveChangesAsync();
		}

		/// Replied (an inbound message on a conversation tied to the person), booked (through any booking link),
		/// converted (the lead became an account, or a deal opened on the contact's account), or a consent withdrawn —
		/// each counted only AFTER the enrolment.
		async Task<string?> ExitReasonAsync(int cid, CrmSequenceEnrollment e, Subject p)
		{
			var since = e.EnrolledAt;
			bool isLead = p.Type == "Lead";
			var convs = _db.Set<CrmConversation>().AsNoTracking().Where(c => c.CompanyId == cid && (isLead ? c.LeadId == p.Id : c.ContactId == p.Id)).Select(c => c.ID);
			var replied = await _db.Set<CrmConversationMessage>().AsNoTracking().AnyAsync(m => m.CompanyId == cid && convs.Contains(m.ConversationId) && m.Direction == "In" && m.At > since);
			var booked = await _db.Set<CrmBooking>().AsNoTracking().AnyAsync(b => b.CompanyId == cid && b.Status == "Booked" && b.CreatedAt > since && (isLead ? b.LeadId == p.Id : b.ContactId == p.Id));
			var converted = isLead
				? p.LeadStatus == "Converted" || await _db.Opportunities.AsNoTracking().AnyAsync(o => o.CompanyID == cid && o.LeadId == p.Id && o.CreatedAt > since)
				: p.AccountId is int acc && await _db.Opportunities.AsNoTracking().AnyAsync(o => o.CompanyID == cid && o.AccountId == acc && o.CreatedAt > since);
			var optOut = await _db.Set<CrmConsent>().AsNoTracking().AnyAsync(c => c.CompanyId == cid && c.SubjectType == p.Type && c.SubjectId == p.Id && c.State == "OptOut" && c.RecordedAt > since);
			if (isLead && p.LeadStatus == "Lost" && !converted) return "Inactive";
			return S.StopReason(converted, booked, replied, optOut);
		}

		async Task<string?> ConsentAsync(int cid, Subject p, string channel)
			=> await _db.Set<CrmConsent>().AsNoTracking().Where(c => c.CompanyId == cid && c.SubjectType == p.Type && c.SubjectId == p.Id && c.Channel == channel)
				.OrderByDescending(c => c.RecordedAt).ThenByDescending(c => c.ID).Select(c => c.State).FirstOrDefaultAsync();

		async Task<(string outcome, string? detail, int? refId)> RunStepAsync(int cid, CrmSequenceEnrollment e, Subject p, CrmSequenceStep step)
		{
			var rep = e.OwnerEmployeeId is int o ? (await PeopleAsync(cid, new int?[] { o })).GetValueOrDefault(o)?.Name : null;
			var values = new Dictionary<string, string?> { ["name"] = p.Name, ["first"] = S.FirstName(p.Name), ["rep"] = rep, ["company"] = p.Company,
				["booking"] = e.OwnerEmployeeId is int bo ? await BookingUrlAsync(cid, bo) : null };
			switch (step.Kind)
			{
				case "Email":
					if (string.IsNullOrWhiteSpace(p.Email)) return ("Skipped", "no e-mail address", null);
					if (await ConsentAsync(cid, p, "Email") == "OptOut") return ("Skipped", "e-mail consent withdrawn", null);
					var (mid, status, err) = await _mail.QueueAsync(cid, e.OwnerEmployeeId ?? 0, p.Email!, null, S.Render(step.Subject, values), S.Render(step.Body, values), null, null, "crm-sequence");
					return err == null ? ("Queued", p.Email, mid) : ("Failed", err, mid);
				case "WhatsApp":
					if (string.IsNullOrWhiteSpace(p.Phone)) return ("Skipped", "no mobile number", null);
					if (await ConsentAsync(cid, p, "WhatsApp") != "OptIn") return ("Skipped", "no recorded WhatsApp consent", null);
					var inbox = ActivatorUtilities.CreateInstance<CrmInboxService>(_sp);
					var (ok, werr, convId) = await inbox.StartTemplateAsync(cid, p.Phone!, p.Name, p.Type == "Contact" ? p.Id : null, p.Type == "Lead" ? p.Id : null, step.Template!, e.OwnerEmployeeId);
					return ok ? ("Sent", step.Template, convId) : ("Failed", werr, convId);
				default:
					var marker = "sequence:" + e.ID + ":" + step.StepNo;
					var dueLocal = DateTime.Now;
					var act = new Activity
					{
						Type = step.Kind == "Call" ? "Call" : "Task", Subject = S.Render(step.Subject, values), Notes = S.Render(step.Body, values), DueDate = dueLocal,
						OwnerEmployeeId = e.OwnerEmployeeId, ReminderAt = DateTime.UtcNow,
						LeadId = p.Type == "Lead" ? p.Id : null, EntityType = p.Type == "Contact" ? "Contact" : null, EntityId = p.Type == "Contact" ? p.Id : null,
					};
					var (aok, aerr) = await _crm.SaveActivityAsync(cid, act, marker);
					if (!aok) return ("Failed", aerr, null);
					var aid = await _db.Activities.AsNoTracking().Where(a => a.CompanyID == cid && a.CreatedBy == marker).OrderByDescending(a => a.ID).Select(a => (int?)a.ID).FirstOrDefaultAsync();
					return ("Task", act.Subject, aid);
			}
		}

		// ------------------------------------------------------------------ booking
		public async Task<string?> BookingUrlAsync(int cid, int employeeId)
		{
			// the address is the one the rep saved the link from: a message written by the worker has no request to ask
			var l = await _db.Set<CrmBookingLink>().AsNoTracking().Where(x => x.CompanyId == cid && x.EmployeeId == employeeId && x.IsActive).Select(x => new { x.PublicKey, x.PublicOrigin }).FirstOrDefaultAsync();
			return l?.PublicOrigin == null ? null : l.PublicOrigin + "/store/meet/" + l.PublicKey;
		}

		public async Task<BookingLinkView?> MyLinkAsync(int cid, int? me)
		{
			if (me is not int m) return null;
			var link = await _db.Set<CrmBookingLink>().AsNoTracking().FirstOrDefaultAsync(l => l.CompanyId == cid && l.EmployeeId == m)
				?? new CrmBookingLink { CompanyId = cid, EmployeeId = m, IsActive = false };
			var person = (await PeopleAsync(cid, new int?[] { m })).GetValueOrDefault(m) ?? new PersonRef(m, "#" + m, false);
			var upcoming = link.ID == 0 ? new() : await _db.Set<CrmBooking>().AsNoTracking().Where(b => b.CompanyId == cid && b.EmployeeId == m && b.EndsAt > DateTime.UtcNow.AddDays(-1))
				.OrderBy(b => b.StartsAt).Take(50).ToListAsync();
			return new BookingLinkView(link, person, upcoming);
		}

		public async Task<(bool ok, string? error)> SaveLinkAsync(int cid, int? me, string? title, int duration, int buffer, int daysAhead, int notice, bool isActive, string origin)
		{
			if (me is not int m) return (false, "Your login is not linked to an employee");
			if (duration is < 15 or > 240 || buffer is < 0 or > 120 || daysAhead is < 1 or > 60 || notice is < 0 or > 168) return (false, "Check the meeting length, the gap, the days ahead and the notice");
			var link = await _db.Set<CrmBookingLink>().FirstOrDefaultAsync(l => l.CompanyId == cid && l.EmployeeId == m);
			if (link == null) _db.Set<CrmBookingLink>().Add(link = new CrmBookingLink { CompanyId = cid, EmployeeId = m, PublicKey = NewKey(12), CalendarToken = NewKey(24) });
			link.Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(); link.DurationMinutes = duration; link.BufferMinutes = buffer; link.DaysAhead = daysAhead; link.MinNoticeHours = notice; link.IsActive = isActive; link.PublicOrigin = origin.Length <= 200 ? origin : null;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		/// The busy spans of one person in a window, local time: meetings and calls in the CRM (as long as this link's
		/// meeting length), and every booking already taken.
		async Task<List<(DateTime start, DateTime end)>> BusyAsync(CrmBookingLink link, DateTime fromLocal, DateTime toLocal)
		{
			var acts = await _db.Activities.AsNoTracking().Where(a => a.CompanyID == link.CompanyId && a.OwnerEmployeeId == link.EmployeeId && !a.Done
				&& (a.Type == "Meeting" || a.Type == "Call") && a.DueDate >= fromLocal && a.DueDate < toLocal).Select(a => a.DueDate!.Value).ToListAsync();
			var fromUtc = fromLocal.ToUniversalTime(); var toUtc = toLocal.ToUniversalTime();
			var books = await _db.Set<CrmBooking>().AsNoTracking().Where(b => b.EmployeeId == link.EmployeeId && b.CompanyId == link.CompanyId && b.Status == "Booked" && b.StartsAt < toUtc && b.EndsAt > fromUtc)
				.Select(b => new { b.StartsAt, b.EndsAt }).ToListAsync();
			return acts.Select(d => (d, d.AddMinutes(link.DurationMinutes)))
				.Concat(books.Select(b => (b.StartsAt.ToLocalTime(), b.EndsAt.ToLocalTime()))).ToList();
		}

		async Task<List<(DateTime day, List<DateTime> slots)>> DaysAsync(CrmBookingLink link)
		{
			var today = DateTime.Today; var last = today.AddDays(link.DaysAhead);
			var cal = await CalendarOfAsync(link.CompanyId, link.EmployeeId, today, last);
			var busy = await BusyAsync(link, today, last.AddDays(1));
			var earliest = DateTime.Now.AddHours(link.MinNoticeHours);
			var days = new List<(DateTime, List<DateTime>)>();
			for (var d = today; d <= last; d = d.AddDays(1))
			{
				if (!cal.IsWorkDay(d)) continue;
				var slots = S.Slots(d, cal.Start, cal.End, link.DurationMinutes, link.BufferMinutes, busy, earliest);
				if (slots.Count > 0) days.Add((d, slots));
			}
			return days;
		}

		public async Task<PublicBookingPage?> PublicPageAsync(string key)
		{
			var link = await _db.Set<CrmBookingLink>().AsNoTracking().FirstOrDefaultAsync(l => l.PublicKey == key && l.IsActive);
			if (link == null) return null;
			var e = await _db.Employee.AsNoTracking().Where(x => x.ID == link.EmployeeId && x.EmpCompanyID == link.CompanyId).Select(x => new { x.FullName, x.FullNameEn, x.ProfileImage }).FirstOrDefaultAsync();
			if (e == null) return null;
			var company = await _db.Companies.AsNoTracking().Where(c => c.CompanyID == link.CompanyId).Select(c => new { c.CompanyName, c.ComoanyNameAr }).FirstOrDefaultAsync();
			return new PublicBookingPage(link, N(e.FullName ?? "", e.FullNameEn), !string.IsNullOrWhiteSpace(e.ProfileImage), company == null ? "" : (IsAr && !string.IsNullOrWhiteSpace(company.ComoanyNameAr) ? company.ComoanyNameAr! : company.CompanyName ?? ""), await DaysAsync(link));
		}

		static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int n, DateTime since)> Rate = new();
		static bool Allow(string k, int max, TimeSpan per)
		{
			var now = DateTime.UtcNow;
			var v = Rate.AddOrUpdate(k, _ => (1, now), (_, o) => now - o.since > per ? (1, now) : (o.n + 1, o.since));
			return v.n <= max;
		}

		/// Anonymous. Writes the booking row only; the slot is re-checked against the calendar now, and the unique index
		/// on (person, start) settles two visitors racing for the same minute.
		public async Task<(bool ok, string code)> BookAsync(string key, DateTime startLocal, string? name, string? email, string? phone, string? note)
		{
			var link = await _db.Set<CrmBookingLink>().AsNoTracking().FirstOrDefaultAsync(l => l.PublicKey == key && l.IsActive);
			if (link == null) return (false, "not_found");
			name = (name ?? "").Trim(); email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(); phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
			if (name.Length < 2 || name.Length > 150) return (false, "name");
			if (email == null && phone == null) return (false, "reach");
			if (email != null && (email.Length > 150 || Keys.EmailKey(email) == null)) return (false, "email");
			if (phone != null && (phone.Length > 50 || Keys.PhoneKey(phone) == null)) return (false, "phone");
			if (!Allow("meet:" + key, 30, TimeSpan.FromHours(1))) return (false, "busy");
			var day = (await DaysAsync(link)).FirstOrDefault(d => d.day == startLocal.Date);
			if (day.slots == null || !day.slots.Contains(startLocal)) return (false, "taken");
			var b = new CrmBooking { CompanyId = link.CompanyId, LinkId = link.ID, EmployeeId = link.EmployeeId, StartsAt = startLocal.ToUniversalTime(), EndsAt = startLocal.AddMinutes(link.DurationMinutes).ToUniversalTime(),
				Name = name, Email = email, Phone = phone, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(1000, note.Trim().Length)], Status = "Booked", CreatedAt = DateTime.UtcNow };
			_db.Set<CrmBooking>().Add(b);
			try { await _db.SaveChangesAsync(); }
			catch (DbUpdateException) { return (false, "taken"); }
			return (true, "ok");
		}

		/// In the company's scope (the worker, or the rep opening their booking screen): a new booking finds its person —
		/// an existing contact, then an open lead, by e-mail or mobile; otherwise it becomes a lead — and lands in the
		/// calendar as a meeting activity, and the rep is told.
		public async Task<int> ProcessBookingsAsync(int cid)
		{
			var fresh = await _db.Set<CrmBooking>().Where(b => b.CompanyId == cid && b.Status == "Booked" && b.ActivityId == null).OrderBy(b => b.ID).Take(50).ToListAsync();
			foreach (var b in fresh)
			{
				var ek = Keys.EmailKey(b.Email); var pk = Keys.PhoneKey(b.Phone);
				var contacts = await _db.CrmContacts.AsNoTracking().Where(c => c.CompanyID == cid && ((b.Email != null && c.Email != null) || (b.Phone != null && c.Phone != null))).Select(c => new { c.ID, c.Email, c.Phone }).ToListAsync();
				var ct = contacts.FirstOrDefault(c => (ek != null && Keys.EmailKey(c.Email) == ek) || (pk != null && Keys.PhoneKey(c.Phone) == pk));
				if (ct != null) b.ContactId = ct.ID;
				else
				{
					var leads = await _db.Leads.AsNoTracking().Where(l => l.CompanyID == cid && l.Status != "Converted" && ((b.Email != null && l.Email != null) || (b.Phone != null && l.Phone != null))).Select(l => new { l.ID, l.Email, l.Phone }).ToListAsync();
					var ld = leads.FirstOrDefault(l => (ek != null && Keys.EmailKey(l.Email) == ek) || (pk != null && Keys.PhoneKey(l.Phone) == pk));
					if (ld != null) b.LeadId = ld.ID;
					else
					{
						var marker = "booking:" + b.ID;
						var (ok, _) = await _crm.SaveLeadAsync(cid, new Lead { Name = b.Name, Phone = b.Phone, Email = b.Email, Source = "Booking link", Status = "New", Notes = b.Note, OwnerEmployeeId = b.EmployeeId }, marker);
						if (ok) b.LeadId = await _db.Leads.AsNoTracking().Where(l => l.CompanyID == cid && l.CreatedBy == marker).OrderByDescending(l => l.ID).Select(l => (int?)l.ID).FirstOrDefaultAsync();
					}
				}
				var amarker = "booking-meeting:" + b.ID;
				var start = b.StartsAt.ToLocalTime();
				var (aok, _) = await _crm.SaveActivityAsync(cid, new Activity
				{
					Type = "Meeting", Subject = (IsAr ? "موعد محجوز: " : "Booked meeting: ") + b.Name, Notes = string.Join("\n", new[] { b.Phone, b.Email, b.Note }.Where(x => !string.IsNullOrWhiteSpace(x))),
					DueDate = start, OwnerEmployeeId = b.EmployeeId, ReminderAt = b.StartsAt.AddMinutes(-30),
					LeadId = b.LeadId, EntityType = b.ContactId != null ? "Contact" : null, EntityId = b.ContactId,
				}, amarker);
				if (!aok) continue;
				b.ActivityId = await _db.Activities.AsNoTracking().Where(a => a.CompanyID == cid && a.CreatedBy == amarker).OrderByDescending(a => a.ID).Select(a => (int?)a.ID).FirstOrDefaultAsync();
				await _db.SaveChangesAsync();
				await _notify.NotifyAsync(b.EmployeeId, "موعد جديد محجوز: " + b.Name + " — " + start.ToString("yyyy-MM-dd HH:mm"), "New booked meeting: " + b.Name + " — " + start.ToString("yyyy-MM-dd HH:mm"),
					b.Note, b.Note, NotificationTypes.CrmAssigned, b.ActivityId, url: "/Crm/Booking", companyId: cid, dedupKey: "crm-booking:" + b.ID);
			}
			return fresh.Count;
		}

		/// The rep's calendar as an iCalendar feed — a URL Outlook, Google or Apple Calendar can subscribe to. The token is
		/// the only key; what it shows is the person's own open CRM meetings, calls and tasks for 60 days, nothing else.
		public async Task<string?> CalendarAsync(string token)
		{
			var link = await _db.Set<CrmBookingLink>().AsNoTracking().FirstOrDefaultAsync(l => l.CalendarToken == token);
			if (link == null) return null;
			var from = DateTime.Today.AddDays(-14); var to = DateTime.Today.AddDays(60);
			var acts = await _db.Activities.AsNoTracking().Where(a => a.CompanyID == link.CompanyId && a.OwnerEmployeeId == link.EmployeeId && !a.Done && a.DueDate >= from && a.DueDate < to)
				.OrderBy(a => a.DueDate).Take(500).Select(a => new { a.ID, a.Type, a.Subject, a.DueDate, a.Notes }).ToListAsync();
			static string Esc(string? s) => (s ?? "").Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");
			static string Utc(DateTime d) => d.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
			var sb = new StringBuilder();
			sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//CrossBusiness//CRM//EN\r\nCALSCALE:GREGORIAN\r\nX-WR-CALNAME:CRM\r\n");
			foreach (var a in acts)
			{
				var start = a.DueDate!.Value; var mins = a.Type == "Meeting" ? link.DurationMinutes : a.Type == "Call" ? 15 : 30;
				sb.Append("BEGIN:VEVENT\r\nUID:crm-activity-").Append(a.ID).Append("@crossbusiness\r\nDTSTAMP:").Append(Utc(DateTime.Now))
				  .Append("\r\nDTSTART:").Append(Utc(start)).Append("\r\nDTEND:").Append(Utc(start.AddMinutes(mins)))
				  .Append("\r\nSUMMARY:").Append(Esc(a.Subject)).Append("\r\nDESCRIPTION:").Append(Esc(a.Notes)).Append("\r\nCATEGORIES:").Append(Esc(a.Type)).Append("\r\nEND:VEVENT\r\n");
			}
			sb.Append("END:VCALENDAR\r\n");
			return sb.ToString();
		}
	}
}
