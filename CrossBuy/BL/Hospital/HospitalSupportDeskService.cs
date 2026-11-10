using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H67 — the support desk (housekeeping, porters, laundry). Anyone who reads the hospital asks for a job and
	// any member of staff takes one; the taker closes it; the requester or the administrator cancels it. A bed turned to
	// Cleaning by a discharge or a transfer opens its terminal-cleaning job (OpenTurnoverAsync, inside the admission's own
	// save), and closing that job turns the bed back to Vacant.
	// ==========================================================================================

	public sealed class HospitalSupportJobInput
	{
		public string? Kind { get; set; } public int? RoomId { get; set; } public int? BedId { get; set; } public int? PatientId { get; set; }
		public string? FromPlace { get; set; } public string? ToPlace { get; set; } public int? Bags { get; set; } public string? Details { get; set; } public bool Urgent { get; set; }
	}

	public sealed record HospitalSupportJobRow(HospitalSupportJob Job, string? Room, string? Bed, string? PatientName, string? PatientNameEn, string? Requester, string? RequesterEn,
		string? Taker, string? TakerEn, string? TakerPhoto);

	public static class HospitalSupportDeskRules
	{
		public static string? AskRefusal(HospitalSupportJobInput i)
		{
			if (!HospitalSupportKind.All.Contains(i.Kind ?? "")) return "Choose the kind of job";
			if ((i.FromPlace?.Length ?? 0) > 150 || (i.ToPlace?.Length ?? 0) > 150 || (i.Details?.Length ?? 0) > 500) return "A text is too long";
			return i.Kind switch
			{
				HospitalSupportKind.Cleaning => i.RoomId is > 0 || i.BedId is > 0 || !string.IsNullOrWhiteSpace(i.FromPlace) ? null : "Choose the room or bed to clean",
				HospitalSupportKind.Porter => string.IsNullOrWhiteSpace(i.FromPlace) || string.IsNullOrWhiteSpace(i.ToPlace) ? "Write where from and where to" : null,
				_ => i.Bags is not > 0 ? "How many bags?" : i.RoomId is > 0 || !string.IsNullOrWhiteSpace(i.FromPlace) ? null : "Choose where to collect from",
			};
		}

		/// Why the job cannot take this step (null = it can). `canCancel` = the requester or the administrator.
		public static string? MoveRefusal(HospitalSupportJob j, string to, int me, bool canCancel, string? reason)
		{
			switch (j.Status, to)
			{
				case (HospitalSupportStatus.Open, HospitalSupportStatus.Taken): return null;
				case (HospitalSupportStatus.Taken, HospitalSupportStatus.Done): return j.TakenByEmployeeId == me ? null : "Only who took the job closes it";
				case (HospitalSupportStatus.Open or HospitalSupportStatus.Taken, HospitalSupportStatus.Cancelled):
					if (!canCancel) return "forbidden";
					return string.IsNullOrWhiteSpace(reason) ? "Write why it is cancelled" : null;
				default: return "This job cannot take that step from where it is";
			}
		}

		/// Minutes from the request until the job was taken (null while nobody has).
		public static int? ResponseMinutes(HospitalSupportJob j) => j.TakenAt is DateTime t ? (int)Math.Round((t - j.RequestedAt).TotalMinutes) : null;
	}

	public sealed class HospitalSupportDeskService
	{
		public const string NoTables = "The support desk is not installed yet (hospital_h67_support_desk.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalSupportDeskService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private IQueryable<HospitalSupportJob> Jobs(BusinessContext ctx) => _db.Set<HospitalSupportJob>().Where(j => j.CompanyId == ctx.CompanyId);
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		/// A bed left in Cleaning opens its terminal-cleaning job — ADDED to the caller's context, saved with its change.
		/// Quietly nothing when the desk is not installed (a discharge never fails for it) or the bed already has one open.
		public static async Task OpenTurnoverAsync(CrossDbContext db, BusinessContext ctx, HospitalBed bed, int? employeeId, DateTime nowUtc, CancellationToken ct)
		{
			try
			{
				var q = db.Set<HospitalSupportJob>().Where(j => j.CompanyId == ctx.CompanyId);
				if (await q.AnyAsync(j => j.BedId == bed.ID && j.Kind == HospitalSupportKind.Cleaning && (j.Status == HospitalSupportStatus.Open || j.Status == HospitalSupportStatus.Taken), ct)) return;
				var nos = await q.Select(j => j.JobNo).ToListAsync(ct);
				db.Add(new HospitalSupportJob
				{
					CompanyId = ctx.CompanyId, JobNo = HospitalDentalPracticeRules.NextNo(nos, "SJ-", 6), Kind = HospitalSupportKind.Cleaning, Source = HospitalSupportSource.BedTurnover,
					RoomId = bed.RoomId, BedId = bed.ID, Details = "Terminal cleaning after the patient left", RequestedAt = nowUtc, RequestedByEmployeeId = employeeId,
				});
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
		}

		public async Task<HospitalResult> AskAsync(BusinessContext ctx, HospitalSupportJobInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Read, null, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			if (HospitalSupportDeskRules.AskRefusal(input) is string why) return HospitalResult.Fail(why);
			if (input.RoomId is int r && !await _db.HospitalRooms.AnyAsync(x => x.ID == r && x.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			int? roomId = input.RoomId;
			if (input.BedId is int b)
			{
				// a bed belongs to one room, so a job asked for a bed carries that room — the desk shows "Room 105 · Bed 105-A"
				var bedRoom = await _db.HospitalBeds.Where(x => x.ID == b && x.CompanyId == ctx.CompanyId).Select(x => (int?)x.RoomId).FirstOrDefaultAsync(ct);
				if (bedRoom == null) return HospitalResult.Fail("not_found");
				roomId = bedRoom;
			}
			if (input.PatientId is int p && !await _db.HospitalPatients.AnyAsync(x => x.ID == p && x.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			try
			{
				for (int attempt = 0; attempt < 3; attempt++)
				{
					var nos = await Jobs(ctx).Select(j => j.JobNo).ToListAsync(ct);
					var row = new HospitalSupportJob
					{
						CompanyId = ctx.CompanyId, JobNo = HospitalDentalPracticeRules.NextNo(nos, "SJ-", 6), Kind = input.Kind!, RoomId = roomId, BedId = input.BedId,
						PatientId = input.Kind == HospitalSupportKind.Porter ? input.PatientId : null, FromPlace = Clip(input.FromPlace, 150), ToPlace = input.Kind == HospitalSupportKind.Porter ? Clip(input.ToPlace, 150) : null,
						Bags = input.Kind == HospitalSupportKind.Laundry ? input.Bags : null, Details = Clip(input.Details, 500), Urgent = input.Urgent, RequestedAt = nowUtc, RequestedByEmployeeId = me,
					};
					_db.Add(row);
					try { await _db.SaveChangesAsync(ct); return HospitalResult.Success(row.ID); }
					catch (DbUpdateException) { _db.ChangeTracker.Clear(); }
				}
				return HospitalResult.Fail(HospitalNursingService.ConcurrentChange);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> MoveAsync(BusinessContext ctx, int id, string to, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Read, null, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			HospitalSupportJob? j;
			try { j = await Jobs(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
			if (j == null) return HospitalResult.Fail("not_found");
			bool canCancel = j.RequestedByEmployeeId == me || await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			if (HospitalSupportDeskRules.MoveRefusal(j, to, me, canCancel, note) is string why) return HospitalResult.Fail(why);
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var q = Jobs(ctx).Where(x => x.ID == id && x.Status == j.Status);   // nothing moves a job twice
			int n = to switch
			{
				HospitalSupportStatus.Taken => await q.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, to).SetProperty(x => x.TakenByEmployeeId, me).SetProperty(x => x.TakenAt, nowUtc), ct),
				HospitalSupportStatus.Done => await q.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, to).SetProperty(x => x.DoneAt, nowUtc).SetProperty(x => x.DoneNote, Clip(note, 300)), ct),
				_ => await q.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, HospitalSupportStatus.Cancelled).SetProperty(x => x.CancelReason, Clip(note, 300)), ct),
			};
			if (n != 1) { await tx.RollbackAsync(); return HospitalResult.Fail("Someone else took this job a moment ago"); }
			// a bed's cleaning done: the bed is ready again (only from Cleaning — never over a patient)
			if (to == HospitalSupportStatus.Done && j.Kind == HospitalSupportKind.Cleaning && j.BedId is int bed)
				await _db.HospitalBeds.Where(b => b.ID == bed && b.CompanyId == ctx.CompanyId && b.Status == HospitalBedStatus.Cleaning)
					.ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, HospitalBedStatus.Vacant).SetProperty(b => b.UpdatedAt, DateTime.UtcNow).SetProperty(b => b.UpdatedByEmployeeId, me), ct);
			await tx.CommitAsync();
			return HospitalResult.Success(id);
		}

		public async Task<(IReadOnlyList<HospitalSupportJobRow> Rows, int Total)> ListAsync(BusinessContext ctx, string? kind, string? state, string? q, int page, int pageSize, CancellationToken ct = default)
		{
			var raw = await Jobs(ctx).AsNoTracking().Where(j => kind == null || j.Kind == kind).ToListAsync(ct);
			var roomIds = raw.Where(j => j.RoomId != null).Select(j => j.RoomId!.Value).Distinct().ToList();
			var rooms = await _db.HospitalRooms.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && roomIds.Contains(r.ID)).ToDictionaryAsync(r => r.ID, r => r.Number, ct);
			var bedIds = raw.Where(j => j.BedId != null).Select(j => j.BedId!.Value).Distinct().ToList();
			var beds = await _db.HospitalBeds.AsNoTracking().Where(b => b.CompanyId == ctx.CompanyId && bedIds.Contains(b.ID)).ToDictionaryAsync(b => b.ID, b => b.Code, ct);
			var pids = raw.Where(j => j.PatientId != null).Select(j => j.PatientId!.Value).Distinct().ToList();
			var patients = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && pids.Contains(p.ID)).ToDictionaryAsync(p => p.ID, p => new { p.Name, p.NameEn }, ct);
			var terms = (q ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
			var hit = raw.Where(j => state switch { "active" => HospitalSupportStatus.Active(j.Status), "closed" => !HospitalSupportStatus.Active(j.Status), _ => true })
				.Where(j => terms.All(t => j.JobNo.Contains(t, StringComparison.OrdinalIgnoreCase) || (j.FromPlace ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
					|| (j.ToPlace ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || (j.Details ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
					|| (j.RoomId is int r && (rooms.GetValueOrDefault(r) ?? "").Contains(t, StringComparison.OrdinalIgnoreCase))
					|| (j.BedId is int b && (beds.GetValueOrDefault(b) ?? "").Contains(t, StringComparison.OrdinalIgnoreCase))
					|| (j.PatientId is int p && patients.TryGetValue(p, out var pp) && (pp.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || pp.NameEn.Contains(t, StringComparison.OrdinalIgnoreCase)))))
				.OrderBy(j => HospitalSupportStatus.Active(j.Status) ? 0 : 1).ThenByDescending(j => HospitalSupportStatus.Active(j.Status) && j.Urgent)
				.ThenBy(j => HospitalSupportStatus.Active(j.Status) ? j.RequestedAt : DateTime.MaxValue).ThenByDescending(j => j.DoneAt ?? j.RequestedAt).ToList();
			var pageRows = hit.Skip((page - 1) * pageSize).Take(pageSize).ToList();
			var eids = pageRows.SelectMany(j => new[] { j.RequestedByEmployeeId, j.TakenByEmployeeId }).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, ct);
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, eids, ct);
			string? N(int? id, bool en) => id is int i && people.TryGetValue(i, out var e) ? (en ? e.FullNameEn ?? e.FullName : e.FullName) : null;
			return (pageRows.Select(j => new HospitalSupportJobRow(j, j.RoomId is int r ? rooms.GetValueOrDefault(r) : null, j.BedId is int b ? beds.GetValueOrDefault(b) : null,
				j.PatientId is int p && patients.TryGetValue(p, out var pp) ? pp.Name : null, j.PatientId is int p2 && patients.TryGetValue(p2, out var pp2) ? pp2.NameEn : null,
				N(j.RequestedByEmployeeId, false), N(j.RequestedByEmployeeId, true), N(j.TakenByEmployeeId, false), N(j.TakenByEmployeeId, true),
				j.TakenByEmployeeId is int tk ? photos.GetValueOrDefault(tk) : null)).ToList(), hit.Count);
		}

		/// The desk's figures: what is open per kind, the beds waiting to be cleaned, the average response today.
		public async Task<(Dictionary<string, int> OpenByKind, int BedsToClean, int? AvgResponseToday, int DoneToday, bool Ready)> FiguresAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var open = await Jobs(ctx).AsNoTracking().Where(j => j.Status == HospitalSupportStatus.Open || j.Status == HospitalSupportStatus.Taken).GroupBy(j => j.Kind).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.N, ct);
				int beds = await _db.HospitalBeds.CountAsync(b => b.CompanyId == ctx.CompanyId && b.IsActive && b.Status == HospitalBedStatus.Cleaning, ct);
				var dayStart = nowUtc.Date;
				var today = await Jobs(ctx).AsNoTracking().Where(j => j.TakenAt != null && j.TakenAt >= dayStart).ToListAsync(ct);
				int? avg = today.Count == 0 ? null : (int)Math.Round(today.Average(j => HospitalSupportDeskRules.ResponseMinutes(j)!.Value));
				int done = await Jobs(ctx).CountAsync(j => j.Status == HospitalSupportStatus.Done && j.DoneAt >= dayStart, ct);
				return (open, beds, avg, done, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (new(), 0, null, 0, false); }
		}
	}
}
