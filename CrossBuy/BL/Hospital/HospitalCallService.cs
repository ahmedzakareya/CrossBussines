using System.Security.Cryptography;
using System.Text;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H176 — internal calls. Design: the approved prototype, #/calls and #/calls-setup.
	//
	// WHAT THE SERVER DOES AND DOES NOT DO. It rings the other person (HospitalCallHub), relays the two browsers'
	// connection offers to each other, mints a short-lived password for the hospital's own relay, and keeps the call's
	// record. The audio and video never pass through it and are never stored.
	//
	// WHO MAY CALL WHOM is a matrix of staff groups, read from the hospital roles each person holds (a linked practitioner
	// is a doctor). Someone with no hospital role is "administration". The matrix is the company's (HospitalCallSettings);
	// the published default lets clinical staff reach each other and keeps the desk away from the benches.
	// ==========================================================================================
	public static class HospitalCallGroups
	{
		public const string Doctors = "doctors";
		public const string Nursing = "nursing";
		public const string Diagnostics = "diagnostics";
		public const string Pharmacy = "pharmacy";
		public const string Reception = "reception";
		public const string Administration = "admin";
		public static readonly IReadOnlyList<string> All = new[] { Doctors, Nursing, Diagnostics, Pharmacy, Reception, Administration };

		/// The groups an employee is in, from their hospital roles; a practitioner record makes a doctor.
		public static IReadOnlyList<string> Of(IEnumerable<string> roles, bool isPractitioner)
		{
			var g = new List<string>();
			var r = new HashSet<string>(roles, StringComparer.Ordinal);
			if (isPractitioner || r.Contains(HospitalRoles.HospitalDoctor)) g.Add(Doctors);
			if (r.Contains(HospitalRoles.HospitalStaff)) g.Add(Nursing);
			if (r.Contains(HospitalRoles.HospitalLabTechnician) || r.Contains(HospitalRoles.HospitalLabSpecialist) || r.Contains(HospitalRoles.HospitalRadiographer) || r.Contains(HospitalRoles.HospitalRadiologist)) g.Add(Diagnostics);
			if (r.Contains(HospitalRoles.HospitalPharmacist)) g.Add(Pharmacy);
			if (r.Contains(HospitalRoles.HospitalReception)) g.Add(Reception);
			if (r.Contains(HospitalRoles.HospitalAdministrator) || g.Count == 0) g.Add(Administration);
			return g;
		}
	}

	public static class HospitalCallRules
	{
		/// The published default: clinical staff reach each other; the desk reaches nursing, itself and administration;
		/// the benches (lab, imaging, pharmacy) do not take calls from the desk.
		public static readonly IReadOnlyList<(string From, string To)> DefaultPolicy = BuildDefault();
		private static IReadOnlyList<(string, string)> BuildDefault()
		{
			var off = new HashSet<(string, string)>
			{
				(HospitalCallGroups.Nursing, HospitalCallGroups.Administration),
				(HospitalCallGroups.Diagnostics, HospitalCallGroups.Reception), (HospitalCallGroups.Diagnostics, HospitalCallGroups.Administration),
				(HospitalCallGroups.Pharmacy, HospitalCallGroups.Reception), (HospitalCallGroups.Pharmacy, HospitalCallGroups.Administration),
				(HospitalCallGroups.Reception, HospitalCallGroups.Doctors), (HospitalCallGroups.Reception, HospitalCallGroups.Diagnostics), (HospitalCallGroups.Reception, HospitalCallGroups.Pharmacy),
				(HospitalCallGroups.Administration, HospitalCallGroups.Diagnostics), (HospitalCallGroups.Administration, HospitalCallGroups.Pharmacy),
			};
			return HospitalCallGroups.All.SelectMany(a => HospitalCallGroups.All.Select(b => (a, b))).Where(p => !off.Contains(p)).ToList();
		}

		public static string FormatPolicy(IEnumerable<(string From, string To)> pairs) =>
			string.Join(",", pairs.Where(p => HospitalCallGroups.All.Contains(p.From) && HospitalCallGroups.All.Contains(p.To)).Distinct().Select(p => p.From + ">" + p.To));

		/// Null or empty = the published default. Unknown groups are dropped.
		public static HashSet<(string From, string To)> ParsePolicy(string? stored)
		{
			if (string.IsNullOrWhiteSpace(stored)) return new HashSet<(string, string)>(DefaultPolicy);
			var set = new HashSet<(string, string)>();
			foreach (var part in stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var ab = part.Split('>');
				if (ab.Length == 2 && HospitalCallGroups.All.Contains(ab[0]) && HospitalCallGroups.All.Contains(ab[1])) set.Add((ab[0], ab[1]));
			}
			return set;
		}

		/// A caller may ring a callee when ANY of the caller's groups may reach ANY of the callee's.
		public static bool Allowed(ISet<(string From, string To)> policy, IEnumerable<string> callerGroups, IEnumerable<string> calleeGroups)
		{
			var to = calleeGroups.ToList();
			return callerGroups.Any(a => to.Any(b => policy.Contains((a, b))));
		}

		/// coturn's "TURN REST API" (use-auth-secret): username = expiry:id, password = base64(HMAC-SHA1(secret, username)).
		/// The browser gets only this pair, valid for `ttl`; the secret stays on the server.
		public static (string Username, string Credential) TurnCredential(string secret, int employeeId, DateTime nowUtc, TimeSpan ttl)
		{
			long expiry = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).Add(ttl)).ToUnixTimeSeconds();
			string user = expiry + ":" + employeeId;
			using var h = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
			return (user, Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(user))));
		}

		/// The relay addresses as written by IT, cleaned: only stun:, turn: and turns: URLs, at most six.
		public static IReadOnlyList<string> ParseUrls(string? s) =>
			(s ?? "").Split(new[] { ',', ';', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Where(u => u.StartsWith("stun:", StringComparison.OrdinalIgnoreCase) || u.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) || u.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))
				.Where(u => u.Length <= 120).Distinct().Take(6).ToList();

		/// The status a call moves to when an action happens, or null when that action is not allowed now.
		public static string? Next(string status, string action, bool byCaller) => (status, action) switch
		{
			(HospitalCallStatus.Ringing, "answer") when !byCaller => HospitalCallStatus.Answered,
			(HospitalCallStatus.Ringing, "decline") when !byCaller => HospitalCallStatus.Declined,
			(HospitalCallStatus.Ringing, "cancel") when byCaller => HospitalCallStatus.Cancelled,
			(HospitalCallStatus.Ringing, "timeout") => HospitalCallStatus.Missed,
			(HospitalCallStatus.Ringing, "drop") => byCaller ? HospitalCallStatus.Cancelled : HospitalCallStatus.Missed,
			(HospitalCallStatus.Answered, "hangup") => HospitalCallStatus.Ended,
			(HospitalCallStatus.Answered, "drop") => HospitalCallStatus.Ended,
			_ => null,
		};

		public static string Duration(int? seconds) => seconds is int s && s >= 0 ? $"{s / 60:00}:{s % 60:00}" : "";
	}

	public sealed record HospitalCallPerson(int EmployeeId, string Name, string NameEn, string? Photo, string? Title, string? TitleEn,
		string? Department, string? DepartmentEn, IReadOnlyList<string> Groups, bool OnCall);

	public sealed record HospitalCallLogRow(HospitalCall Call, bool Outgoing, HospitalCallPerson? Other, string? Patient, string? PatientEn, string? Mrn);

	public sealed record HospitalCallLogPage(IReadOnlyList<HospitalCallLogRow> Rows, int Total, int Page, int PageSize);

	public sealed record HospitalCallStats(int Today, int AverageSeconds, int MissedWaiting);

	public sealed class HospitalCallSettingsInput
	{
		public bool VideoEnabled { get; set; } = true;
		public bool ScreenShareEnabled { get; set; } = true;
		public int RingSeconds { get; set; } = 30;
		public int MaxVideoHeight { get; set; } = 720;
		public string? TurnUrls { get; set; }
		/// Null = keep the stored secret; "" = remove it.
		public string? TurnSecret { get; set; }
		public List<string>? Policy { get; set; }
		public bool RecordingEnabled { get; set; }
		public string? RecordingMode { get; set; }
		public int RecordingDays { get; set; } = 90;
		public List<string>? RecordingGroups { get; set; }
		public bool GroupEnabled { get; set; } = true;
		public int GroupMax { get; set; } = 4;
	}

	public sealed class HospitalCallService
	{
		public const string NoTables = "Internal calls are not installed yet (hospital_h176_calls.sql).";
		private readonly CrossDbContext _db;
		private readonly IPlatformRoleDirectory _roles;
		public HospitalCallService(CrossDbContext db, IPlatformRoleDirectory roles) { _db = db; _roles = roles; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		public async Task<bool> InstalledAsync(CancellationToken ct = default)
		{
			try { await _db.Set<HospitalCallSettings>().AsNoTracking().AnyAsync(ct); return true; }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return false; }
		}

		public async Task<HospitalCallSettings> SettingsAsync(int companyId, CancellationToken ct = default)
		{
			try { return await _db.Set<HospitalCallSettings>().AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId, ct) ?? new HospitalCallSettings { CompanyId = companyId }; }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalCallSettings { CompanyId = companyId }; }
		}

		public async Task<HospitalResult> SaveSettingsAsync(int companyId, HospitalCallSettingsInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (i.RingSeconds is < 10 or > 120) return HospitalResult.Fail("The ring time must be between 10 and 120 seconds");
			if (i.MaxVideoHeight is not (480 or 720 or 1080)) return HospitalResult.Fail("Choose a video quality from the list");
			if (!HospitalCallRecordingRules.RetentionChoices.Contains(i.RecordingDays)) return HospitalResult.Fail("Choose how long recordings are kept from the list");
			if (i.RecordingMode is not (HospitalCallRecordingMode.Voice or HospitalCallRecordingMode.Both)) i.RecordingMode = HospitalCallRecordingMode.Both;
			var urls = HospitalCallRules.ParseUrls(i.TurnUrls);
			if (!string.IsNullOrWhiteSpace(i.TurnUrls) && urls.Count == 0) return HospitalResult.Fail("Write the relay address as turn:host:port or stun:host:port");
			var pairs = (i.Policy ?? new()).Select(p => p.Split('>')).Where(a => a.Length == 2).Select(a => (a[0], a[1]));
			try
			{
				var row = await _db.Set<HospitalCallSettings>().FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
				if (row == null) { row = new HospitalCallSettings { CompanyId = companyId }; _db.Add(row); }
				row.VideoEnabled = i.VideoEnabled; row.ScreenShareEnabled = i.ScreenShareEnabled; row.RingSeconds = i.RingSeconds; row.MaxVideoHeight = i.MaxVideoHeight;
				row.TurnUrls = urls.Count == 0 ? null : string.Join(",", urls);
				if (i.TurnSecret != null) row.TurnSecret = Clip(i.TurnSecret, 200);
				var policy = HospitalCallRules.FormatPolicy(pairs);
				row.Policy = policy.Length == 0 ? "-" : policy;
				row.RecordingEnabled = i.RecordingEnabled; row.RecordingMode = i.RecordingMode!; row.RecordingDays = i.RecordingDays;
				row.RecordingGroups = HospitalCallRecordingRules.FormatGroups(i.RecordingGroups);
				row.GroupEnabled = i.GroupEnabled; row.GroupMax = HospitalCallGroupRules.Cap(i.GroupMax);   // nobody may call: stored explicitly, an empty value would mean the default
				row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		// ---------------------------------------------------------------------------------------
		// PEOPLE
		// ---------------------------------------------------------------------------------------

		/// Every active employee of the company as the directory shows them: face, title, department, groups, on call now.
		public async Task<IReadOnlyList<HospitalCallPerson>> PeopleAsync(int companyId, DateTime nowUtc, IEnumerable<int>? only = null, CancellationToken ct = default)
		{
			var q = _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == companyId && e.IsActive);
			if (only != null) { var ids = only.Distinct().ToList(); q = q.Where(e => ids.Contains(e.ID)); }
			var emps = await q.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.JobTitleID, e.DepartmentID }).ToListAsync(ct);
			if (emps.Count == 0) return Array.Empty<HospitalCallPerson>();
			var eids = emps.Select(e => e.ID).ToList();
			var docs = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == companyId && p.IsActive && p.EmployeeId != null && eids.Contains(p.EmployeeId.Value))
				.Select(p => new { EmployeeId = p.EmployeeId!.Value, p.Name, p.NameEn, p.Title, p.TitleEn, p.DepartmentId }).ToListAsync(ct);
			var docBy = docs.GroupBy(d => d.EmployeeId).ToDictionary(g => g.Key, g => g.First());
			var hdIds = docs.Where(d => d.DepartmentId != null).Select(d => d.DepartmentId!.Value).Distinct().ToList();
			var hdepts = await _db.HospitalDepartments.AsNoTracking().Where(d => d.CompanyId == companyId && hdIds.Contains(d.ID)).ToDictionaryAsync(d => d.ID, ct);
			var jtIds = emps.Select(e => e.JobTitleID).Distinct().ToList();
			var titles = await _db.JobTitles.AsNoTracking().Where(j => jtIds.Contains(j.ID)).Select(j => new { j.ID, j.Title, j.TitleAr }).ToDictionaryAsync(j => j.ID, ct);
			var depIds = emps.Where(e => e.DepartmentID != null).Select(e => e.DepartmentID!.Value).Distinct().ToList();
			var deps = await _db.Hierarchicals.AsNoTracking().Where(h => depIds.Contains(h.H_ID) && (h.CompanyID == null || h.CompanyID == companyId)).Select(h => new { h.H_ID, h.H_Name, h.H_NameEn }).ToDictionaryAsync(h => h.H_ID, ct);
			var ctx = new BusinessContext { CompanyId = companyId };
			var photos = await EmployeePhotos.ResolveAsync(_db, ctx, eids, ct);
			// The hospital roles of these employees, read with the role directory's own rules (company first, the scope, an
			// employee principal, active, inside its validity window) in ONE query for the whole list.
			var grants = await _db.PlatformRoleAssignments.AsNoTracking()
				.Where(r => r.CompanyID == companyId && r.Scope == EntityRegistry.ScopeHospital && r.PrincipalType == CrossBuy.Models.Context.Platform.PlatformPrincipalTypes.Employee
						 && eids.Contains(r.PrincipalId) && r.IsActive && (r.ValidFrom == null || r.ValidFrom <= nowUtc) && (r.ValidTo == null || r.ValidTo >= nowUtc))
				.Select(r => new { r.PrincipalId, r.Role }).ToListAsync(ct);
			var roleBy = grants.GroupBy(g => g.PrincipalId).ToDictionary(g => g.Key, g => g.Select(x => x.Role).Distinct().ToList());
			var onCall = await OnCallNowAsync(companyId, nowUtc, ct);
			return emps.Select(e =>
			{
				docBy.TryGetValue(e.ID, out var d);
				titles.TryGetValue(e.JobTitleID, out var jt);
				string? dep = null, depEn = null;
				if (d?.DepartmentId is int hd && hdepts.TryGetValue(hd, out var hdep)) { dep = hdep.Name; depEn = hdep.NameEn ?? hdep.Name; }
				else if (e.DepartmentID is int di && deps.TryGetValue(di, out var h)) { dep = h.H_Name ?? h.H_NameEn; depEn = h.H_NameEn ?? h.H_Name; }
				return new HospitalCallPerson(e.ID,
					d?.Name ?? e.FullName ?? e.FullNameEn ?? "—", d?.NameEn ?? e.FullNameEn ?? e.FullName ?? "—", photos.GetValueOrDefault(e.ID),
					d?.Title ?? jt?.TitleAr ?? jt?.Title, d?.TitleEn ?? jt?.Title ?? jt?.TitleAr, dep, depEn,
					HospitalCallGroups.Of(roleBy.GetValueOrDefault(e.ID) ?? new List<string>(), d != null), onCall.Contains(e.ID));
			}).OrderBy(p => p.NameEn, StringComparer.OrdinalIgnoreCase).ToList();
		}

		/// The employees on call at this ward moment (first or backup), from the on-call roster (H68).
		private async Task<HashSet<int>> OnCallNowAsync(int companyId, DateTime nowUtc, CancellationToken ct)
		{
			var ward = HospitalNursingService.UtcToWard(nowUtc);
			try
			{
				var docIds = await _db.Set<HospitalOnCallShift>().AsNoTracking()
					.Where(s => s.CompanyId == companyId && s.CancelledAt == null && s.StartsAt <= ward && ward < s.EndsAt).Select(s => s.PractitionerId).Distinct().ToListAsync(ct);
				var eids = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == companyId && docIds.Contains(p.ID) && p.EmployeeId != null).Select(p => p.EmployeeId!.Value).ToListAsync(ct);
				return eids.ToHashSet();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HashSet<int>(); }
		}

		public async Task<bool> MayCallAsync(int companyId, HospitalCallPerson caller, HospitalCallPerson callee, CancellationToken ct = default)
		{
			var s = await SettingsAsync(companyId, ct);
			return HospitalCallRules.Allowed(HospitalCallRules.ParsePolicy(s.Policy), caller.Groups, callee.Groups);
		}

		// ---------------------------------------------------------------------------------------
		// THE CALL'S LIFE (called by the hub)
		// ---------------------------------------------------------------------------------------

		public async Task<HospitalCall> OpenAsync(int companyId, int callerId, int calleeId, bool video, int? patientId, string? reason, string status, string? missedWhy, DateTime nowUtc, CancellationToken ct = default)
		{
			var row = new HospitalCall
			{
				CompanyId = companyId, CallerEmployeeId = callerId, CalleeEmployeeId = calleeId, IsVideo = video, PatientId = patientId, Reason = Clip(reason, 200),
				Status = status, MissedWhy = missedWhy, StartedAt = nowUtc, EndedAt = status == HospitalCallStatus.Missed ? nowUtc : null,
			};
			_db.Add(row);
			await _db.SaveChangesAsync(ct);
			return row;
		}

		/// Moves a call on. Null when the call is not this company's, the employee is not in it, or the action is not allowed now.
		public async Task<HospitalCall?> MoveAsync(int companyId, int callId, int employeeId, string action, string? reply, DateTime nowUtc, CancellationToken ct = default)
		{
			var row = await _db.Set<HospitalCall>().FirstOrDefaultAsync(c => c.ID == callId && c.CompanyId == companyId, ct);
			if (row == null || (row.CallerEmployeeId != employeeId && row.CalleeEmployeeId != employeeId)) return null;
			var next = HospitalCallRules.Next(row.Status, action, row.CallerEmployeeId == employeeId);
			if (next == null) return null;
			row.Status = next;
			if (next == HospitalCallStatus.Answered) row.AnsweredAt = nowUtc;
			else
			{
				row.EndedAt = nowUtc;
				if (row.AnsweredAt is DateTime a) row.DurationSeconds = Math.Max(0, (int)(nowUtc - a).TotalSeconds);
				if (next == HospitalCallStatus.Missed) row.MissedWhy = "no-answer";
				if (next == HospitalCallStatus.Declined) row.Reply = Clip(reply, 120);
			}
			await _db.SaveChangesAsync(ct);
			return row;
		}

		/// Calls still ringing long after their ring time (a browser closed mid-ring, a server restart) become missed.
		public async Task SweepAsync(int companyId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var s = await SettingsAsync(companyId, ct);
				var cutoff = nowUtc.AddSeconds(-(s.RingSeconds + 30));
				var stale = await _db.Set<HospitalCall>().Where(c => c.CompanyId == companyId && c.Status == HospitalCallStatus.Ringing && c.StartedAt < cutoff).ToListAsync(ct);
				foreach (var c in stale) { c.Status = HospitalCallStatus.Missed; c.MissedWhy = "no-answer"; c.EndedAt = c.StartedAt.AddSeconds(s.RingSeconds); }
				// an answered call that lost both sides without a hang-up (a restart) is closed at 4 hours
				var hung = await _db.Set<HospitalCall>().Where(c => c.CompanyId == companyId && c.Status == HospitalCallStatus.Answered && c.AnsweredAt < nowUtc.AddHours(-4)).ToListAsync(ct);
				foreach (var c in hung) { c.Status = HospitalCallStatus.Ended; c.EndedAt = c.AnsweredAt!.Value.AddHours(4); c.DurationSeconds = 4 * 3600; }
				if (stale.Count + hung.Count > 0) await _db.SaveChangesAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
		}

		// ---------------------------------------------------------------------------------------
		// WHAT THE SCREENS READ
		// ---------------------------------------------------------------------------------------

		public async Task<HospitalCallLogPage> LogAsync(int companyId, int employeeId, string? q, int page, int pageSize, DateTime nowUtc, CancellationToken ct = default)
		{
			page = Math.Max(1, page);
			try
			{
				// mine: I called or was called, or I was invited to a group call (H176 group calls)
				var partOf = _db.Set<HospitalCallParticipant>().AsNoTracking().Where(p => p.CompanyId == companyId && p.EmployeeId == employeeId).Select(p => p.CallId);
				var mine = _db.Set<HospitalCall>().AsNoTracking().Where(c => c.CompanyId == companyId && (c.CallerEmployeeId == employeeId || c.CalleeEmployeeId == employeeId || (c.IsGroup && partOf.Contains(c.ID))));
				var term = q?.Trim();
				if (!string.IsNullOrEmpty(term))
				{
					var empHits = _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == companyId && (e.FullName.Contains(term) || (e.FullNameEn != null && e.FullNameEn.Contains(term)))).Select(e => e.ID);
					var patHits = _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == companyId && (p.Name.Contains(term) || p.NameEn.Contains(term) || p.Mrn.Contains(term))).Select(p => (int?)p.ID);
					mine = mine.Where(c => (c.Reason != null && c.Reason.Contains(term)) || (c.Note != null && c.Note.Contains(term))
						|| empHits.Contains(c.CallerEmployeeId == employeeId ? c.CalleeEmployeeId : c.CallerEmployeeId) || patHits.Contains(c.PatientId));
				}
				int total = await mine.CountAsync(ct);
				var calls = await mine.OrderByDescending(c => c.StartedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
				var others = calls.Select(c => c.CallerEmployeeId == employeeId ? c.CalleeEmployeeId : c.CallerEmployeeId).Distinct().ToList();
				var people = (await PeopleAsync(companyId, nowUtc, others, ct)).ToDictionary(p => p.EmployeeId);
				var pids = calls.Where(c => c.PatientId != null).Select(c => c.PatientId!.Value).Distinct().ToList();
				var pats = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == companyId && pids.Contains(p.ID)).Select(p => new { p.ID, p.Name, p.NameEn, p.Mrn }).ToDictionaryAsync(p => p.ID, ct);
				var rows = calls.Select(c =>
				{
					bool outgoing = c.CallerEmployeeId == employeeId;
					var other = people.GetValueOrDefault(outgoing ? c.CalleeEmployeeId : c.CallerEmployeeId);
					var p = c.PatientId is int pid ? pats.GetValueOrDefault(pid) : null;
					return new HospitalCallLogRow(c, outgoing, other, p?.Name, p?.NameEn, p?.Mrn);
				}).ToList();
				return new HospitalCallLogPage(rows, total, page, pageSize);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalCallLogPage(Array.Empty<HospitalCallLogRow>(), 0, 1, pageSize); }
		}

		/// Today's calls of mine (ward day), their average answered length, and missed incoming calls I have not called back.
		public async Task<HospitalCallStats> StatsAsync(int companyId, int employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var wardNow = HospitalNursingService.UtcToWard(nowUtc);
				var dayStartUtc = nowUtc - (wardNow - wardNow.Date);
				var today = await _db.Set<HospitalCall>().AsNoTracking()
					.Where(c => c.CompanyId == companyId && (c.CallerEmployeeId == employeeId || c.CalleeEmployeeId == employeeId) && c.StartedAt >= dayStartUtc)
					.Select(c => new { c.Status, c.DurationSeconds }).ToListAsync(ct);
				var lengths = today.Where(c => c.DurationSeconds != null).Select(c => c.DurationSeconds!.Value).ToList();
				int waiting = await MissedWaitingAsync(companyId, employeeId, ct);
				return new HospitalCallStats(today.Count, lengths.Count == 0 ? 0 : (int)lengths.Average(), waiting);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalCallStats(0, 0, 0); }
		}

		/// Missed calls TO me that I have neither marked seen nor answered by a later call with that person.
		public async Task<int> MissedWaitingAsync(int companyId, int employeeId, CancellationToken ct = default)
		{
			var missed = await _db.Set<HospitalCall>().AsNoTracking()
				.Where(c => c.CompanyId == companyId && c.CalleeEmployeeId == employeeId && c.Status == HospitalCallStatus.Missed && c.SeenAt == null)
				.Select(c => new { c.CallerEmployeeId, c.StartedAt }).ToListAsync(ct);
			if (missed.Count == 0) return 0;
			var callers = missed.Select(m => m.CallerEmployeeId).Distinct().ToList();
			var later = await _db.Set<HospitalCall>().AsNoTracking()
				.Where(c => c.CompanyId == companyId && c.CallerEmployeeId == employeeId && callers.Contains(c.CalleeEmployeeId))
				.GroupBy(c => c.CalleeEmployeeId).Select(g => new { Id = g.Key, Last = g.Max(c => c.StartedAt) }).ToDictionaryAsync(x => x.Id, x => x.Last, ct);
			return missed.Count(m => !later.TryGetValue(m.CallerEmployeeId, out var l) || l < m.StartedAt);
		}

		/// A participant writes the call's note, or links the patient it is about (once, while the call is on or just after).
		public async Task<HospitalResult> NoteAsync(int companyId, int callId, int employeeId, string? note, int? patientId, CancellationToken ct = default)
		{
			try
			{
				var row = await _db.Set<HospitalCall>().FirstOrDefaultAsync(c => c.ID == callId && c.CompanyId == companyId, ct);
				if (row == null || (row.CallerEmployeeId != employeeId && row.CalleeEmployeeId != employeeId)) return HospitalResult.Fail("not_found");
				if (patientId is int pid)
				{
					if (!await _db.HospitalPatients.AnyAsync(p => p.ID == pid && p.CompanyId == companyId, ct)) return HospitalResult.Fail("not_found");
					if (row.PatientId != null && row.PatientId != pid) return HospitalResult.Fail("This call is already linked to a patient");
					row.PatientId = pid;
				}
				if (note != null) { row.Note = Clip(note, 1000); row.NoteByEmployeeId = employeeId; }
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The patient as the call shows them: name, MRN, photo, where they are.
		public async Task<object?> PatientCardAsync(int companyId, int patientId, CancellationToken ct = default)
		{
			var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == patientId && x.CompanyId == companyId)
				.Select(x => new { x.ID, x.Name, x.NameEn, x.Mrn, x.PhotoPath, x.Sex, x.BirthDate }).FirstOrDefaultAsync(ct);
			if (p == null) return null;
			return new { id = p.ID, name = DisplayName.Of(p.Name, p.NameEn), nameEn = p.NameEn, mrn = p.Mrn, photo = p.PhotoPath, initials = HospitalUi.Initials(p.NameEn ?? p.Name), sex = p.Sex, birth = p.BirthDate?.ToString("yyyy-MM-dd") };
		}
	}
}
