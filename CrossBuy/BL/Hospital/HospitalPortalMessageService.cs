using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalPortalNewThreadInput { public int PatientId { get; set; } public string? Topic { get; set; } public string? Subject { get; set; } public string? Body { get; set; } }
	public sealed class HospitalPortalReplyInput { public string? Body { get; set; } }

	public sealed record HospitalPortalThreadRow(HospitalPortalThread Thread, string PatientName, string? PatientNameEn, string? Photo, string? Mrn, HospitalFace? Assigned, bool Overdue, int Unread);
	public sealed record HospitalPortalThreadView(HospitalPortalThread Thread, string PatientName, string? PatientNameEn, string? Photo, IReadOnlyList<(HospitalPortalMessage Message, HospitalFace? Staff)> Messages);

	public static class HospitalPortalMessageRules
	{
		/// The clinic answers a patient's message within two days.
		public const int ReplyHours = 48;
		public static bool Overdue(HospitalPortalThread t, DateTime nowUtc) => t.Status == HospitalPortalThreadStatus.Open && t.AwaitingClinic && (nowUtc - t.LastMessageAt).TotalHours > ReplyHours;

		public static string? NewRefusal(string? topic, string? subject, string? body)
		{
			if (!HospitalPortalTopic.All.Contains(topic ?? "")) return "Choose the topic";
			if (string.IsNullOrWhiteSpace(subject) || subject.Trim().Length > 150) return "Write the subject (150 characters at most)";
			return BodyRefusal(body);
		}

		public static string? BodyRefusal(string? body) =>
			string.IsNullOrWhiteSpace(body) || body.Trim().Length > 2000 ? "Write the message (2000 characters at most)" : null;
	}

	// ==========================================================================================
	// HOSPITAL H249 — secure messaging. The patient side is reached only through the portal account (its own people);
	// the clinic side needs clinical read to see and answer. A closed thread takes no new message; the patient starts a
	// new one. Nothing is sent outside the system.
	// ==========================================================================================
	public sealed class HospitalPortalMessageService
	{
		public const string NoTables = "Portal messaging is not installed yet (hospital_h249_portal_messages.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalPortalMessageService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string Clip(string s, int n) => s.Trim()[..Math.Min(n, s.Trim().Length)];

		// ---------------------------------------------------------------- the patient's side

		public async Task<(bool Ready, IReadOnlyList<HospitalPortalThread> Threads, Dictionary<int, int> Unread)> MyThreadsAsync(int companyId, int accountId, CancellationToken ct = default)
		{
			try
			{
				var t = await _db.Set<HospitalPortalThread>().AsNoTracking().Where(x => x.CompanyId == companyId && x.AccountId == accountId).OrderByDescending(x => x.LastMessageAt).Take(100).ToListAsync(ct);
				var ids = t.Select(x => x.ID).ToList();
				var unread = await _db.Set<HospitalPortalMessage>().AsNoTracking().Where(m => m.CompanyId == companyId && ids.Contains(m.ThreadId) && !m.FromPatient && m.ReadAt == null)
					.GroupBy(m => m.ThreadId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
				return (true, t, unread);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (false, Array.Empty<HospitalPortalThread>(), new()); }
		}

		public async Task<HospitalResult> StartAsync(int companyId, int accountId, IReadOnlyCollection<int> myPeople, HospitalPortalNewThreadInput i, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!myPeople.Contains(i.PatientId)) return HospitalResult.Fail("not_found");
			if (HospitalPortalMessageRules.NewRefusal(i.Topic, i.Subject, i.Body) is string why) return HospitalResult.Fail(why);
			try
			{
				var t = new HospitalPortalThread { CompanyId = companyId, AccountId = accountId, PatientId = i.PatientId, Topic = i.Topic!, Subject = Clip(i.Subject!, 150), AwaitingClinic = true, CreatedAt = nowUtc, LastMessageAt = nowUtc };
				_db.Add(t); await _db.SaveChangesAsync(ct);
				_db.Add(new HospitalPortalMessage { CompanyId = companyId, ThreadId = t.ID, FromPatient = true, Body = Clip(i.Body!, 2000), At = nowUtc });
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(t.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		private async Task<HospitalPortalThreadView?> ViewAsync(int companyId, HospitalPortalThread t, CancellationToken ct)
		{
			var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == t.PatientId).Select(x => new { x.Name, x.NameEn, x.PhotoPath }).FirstAsync(ct);
			var msgs = await _db.Set<HospitalPortalMessage>().AsNoTracking().Where(m => m.CompanyId == companyId && m.ThreadId == t.ID).OrderBy(m => m.At).ToListAsync(ct);
			var faces = await HospitalFaces.OfEmployeesAsync(_db, companyId, msgs.Select(m => m.EmployeeId), ct);
			return new HospitalPortalThreadView(t, p.Name, p.NameEn, p.PhotoPath, msgs.Select(m => (m, m.EmployeeId is int e ? faces.GetValueOrDefault(e) : null)).ToList());
		}

		/// The account's thread, marked read on the patient's side.
		public async Task<HospitalPortalThreadView?> MyThreadAsync(int companyId, int accountId, int threadId, DateTime nowUtc, CancellationToken ct = default)
		{
			var t = await _db.Set<HospitalPortalThread>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == threadId && x.CompanyId == companyId && x.AccountId == accountId, ct);
			if (t == null) return null;
			await _db.Set<HospitalPortalMessage>().Where(m => m.CompanyId == companyId && m.ThreadId == t.ID && !m.FromPatient && m.ReadAt == null).ExecuteUpdateAsync(u => u.SetProperty(m => m.ReadAt, nowUtc), ct);
			return await ViewAsync(companyId, t, ct);
		}

		public async Task<HospitalResult> PatientReplyAsync(int companyId, int accountId, int threadId, HospitalPortalReplyInput i, DateTime nowUtc, CancellationToken ct = default)
		{
			if (HospitalPortalMessageRules.BodyRefusal(i.Body) is string why) return HospitalResult.Fail(why);
			var t = await _db.Set<HospitalPortalThread>().FirstOrDefaultAsync(x => x.ID == threadId && x.CompanyId == companyId && x.AccountId == accountId, ct);
			if (t == null) return HospitalResult.Fail("not_found");
			if (t.Status != HospitalPortalThreadStatus.Open) return HospitalResult.Fail("This conversation is closed: start a new message");
			_db.Add(new HospitalPortalMessage { CompanyId = companyId, ThreadId = t.ID, FromPatient = true, Body = Clip(i.Body!, 2000), At = nowUtc });
			t.AwaitingClinic = true; t.LastMessageAt = nowUtc;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(t.ID);
		}

		// ---------------------------------------------------------------- the clinic's side

		public async Task<(bool Ready, IReadOnlyList<HospitalPortalThreadRow> Rows, int Total, int Awaiting, int Overdue)?> InboxAsync(BusinessContext ctx, string? q, string? view, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return null;
			try
			{
				var baseQ = from t in _db.Set<HospitalPortalThread>().AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on t.PatientId equals p.ID
							where t.CompanyId == ctx.CompanyId
							select new { t, p.Name, p.NameEn, p.PhotoPath, p.Mrn };
				var due = nowUtc.AddHours(-HospitalPortalMessageRules.ReplyHours);
				int awaiting = await baseQ.CountAsync(x => x.t.Status == HospitalPortalThreadStatus.Open && x.t.AwaitingClinic, ct);
				int overdue = await baseQ.CountAsync(x => x.t.Status == HospitalPortalThreadStatus.Open && x.t.AwaitingClinic && x.t.LastMessageAt < due, ct);
				var qq = view switch
				{
					"closed" => baseQ.Where(x => x.t.Status == HospitalPortalThreadStatus.Closed),
					"mine" => baseQ.Where(x => x.t.Status == HospitalPortalThreadStatus.Open && x.t.AssignedEmployeeId == ctx.EmployeeId),
					"all" => baseQ.Where(x => x.t.Status == HospitalPortalThreadStatus.Open),
					_ => baseQ.Where(x => x.t.Status == HospitalPortalThreadStatus.Open && x.t.AwaitingClinic),
				};
				if (!string.IsNullOrWhiteSpace(q))
				{
					var s = q.Trim();
					qq = qq.Where(x => x.Name.Contains(s) || (x.NameEn ?? "").Contains(s) || x.Mrn.Contains(s) || x.t.Subject.Contains(s));
				}
				int total = await qq.CountAsync(ct);
				var rows = await qq.OrderBy(x => x.t.AwaitingClinic ? 0 : 1).ThenBy(x => x.t.LastMessageAt).Skip((Math.Max(1, page) - 1) * size).Take(size).ToListAsync(ct);
				var ids = rows.Select(r => r.t.ID).ToList();
				var unread = await _db.Set<HospitalPortalMessage>().AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && ids.Contains(m.ThreadId) && m.FromPatient && m.ReadAt == null)
					.GroupBy(m => m.ThreadId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
				var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, rows.Select(r => r.t.AssignedEmployeeId), ct);
				return (true, rows.Select(r => new HospitalPortalThreadRow(r.t, r.Name, r.NameEn, r.PhotoPath, r.Mrn, r.t.AssignedEmployeeId is int a ? faces.GetValueOrDefault(a) : null,
					HospitalPortalMessageRules.Overdue(r.t, nowUtc), unread.GetValueOrDefault(r.t.ID))).ToList(), total, awaiting, overdue);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (false, Array.Empty<HospitalPortalThreadRow>(), 0, 0, 0); }
		}

		public async Task<HospitalPortalThreadView?> ClinicThreadAsync(BusinessContext ctx, int threadId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return null;
			var t = await _db.Set<HospitalPortalThread>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == threadId && x.CompanyId == ctx.CompanyId, ct);
			if (t == null) return null;
			await _db.Set<HospitalPortalMessage>().Where(m => m.CompanyId == ctx.CompanyId && m.ThreadId == t.ID && m.FromPatient && m.ReadAt == null).ExecuteUpdateAsync(u => u.SetProperty(m => m.ReadAt, nowUtc), ct);
			return await ViewAsync(ctx.CompanyId, t, ct);
		}

		public async Task<HospitalResult> ClinicReplyAsync(BusinessContext ctx, int threadId, HospitalPortalReplyInput i, bool close, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return HospitalResult.Fail("forbidden");
			if (!close && HospitalPortalMessageRules.BodyRefusal(i.Body) is string why) return HospitalResult.Fail(why);
			var t = await _db.Set<HospitalPortalThread>().FirstOrDefaultAsync(x => x.ID == threadId && x.CompanyId == ctx.CompanyId, ct);
			if (t == null) return HospitalResult.Fail("not_found");
			if (t.Status != HospitalPortalThreadStatus.Open) return HospitalResult.Fail("This conversation is closed: start a new message");
			if (!string.IsNullOrWhiteSpace(i.Body))
			{
				if (HospitalPortalMessageRules.BodyRefusal(i.Body) is string why2) return HospitalResult.Fail(why2);
				_db.Add(new HospitalPortalMessage { CompanyId = ctx.CompanyId, ThreadId = t.ID, FromPatient = false, EmployeeId = ctx.EmployeeId, Body = Clip(i.Body!, 2000), At = nowUtc });
				t.LastMessageAt = nowUtc; t.AwaitingClinic = false;
			}
			t.AssignedEmployeeId ??= ctx.EmployeeId;
			if (close) { t.Status = HospitalPortalThreadStatus.Closed; t.ClosedAt = nowUtc; t.AwaitingClinic = false; }
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(t.ID);
		}
	}
}
