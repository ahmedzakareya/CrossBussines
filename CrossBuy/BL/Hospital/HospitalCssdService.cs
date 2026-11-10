using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H42 — central sterile supply. The rules are HospitalCssdRules' (no database).
	//   · The sets (`manage`): code, name, shelf life.
	//   · The round (`admit`, the theatre and CSSD staff): wash and pack → load packed sets into a cycle → the cycle
	//     passes (every set in it is sterile until today + its shelf life) or fails (back to packed, the reason kept)
	//     → a sterile, unexpired set is issued to a theatre case that is planned or in progress → returned after the
	//     case, awaiting wash. A sterile set past its date is sent back to be reprocessed.
	//   · Every step is a set event in the same save, so a case's set names the cycle that sterilised it.
	// ==========================================================================================
	public sealed record HospitalSetRow(HospitalInstrumentSet Set, string State, string? CycleNo, string? CaseNo);
	public sealed record HospitalCycleRow(HospitalSterilisationCycle Cycle, IReadOnlyList<string> Sets);
	public sealed record HospitalCssdCase(int Id, string CaseNo, string Name, string NameEn, string Patient, string PatientEn);
	public sealed record HospitalCssdBoard(IReadOnlyList<HospitalSetRow> Sets, IReadOnlyList<HospitalCycleRow> Cycles, IReadOnlyList<HospitalCssdCase> Cases);

	public sealed class HospitalSetInput
	{
		public int? Id { get; set; } public string Code { get; set; } = ""; public string Name { get; set; } = ""; public string? NameEn { get; set; }
		public int ShelfLifeDays { get; set; } = 30; public bool IsActive { get; set; } = true;
	}

	public static class HospitalCssdRules
	{
		/// The state the screen shows: the stored status, or Expired for a sterile set past its date.
		public const string Expired = "Expired";
		public static string State(string status, DateTime? sterileUntil, DateTime today)
			=> status == HospitalSetStatus.Sterile && sterileUntil?.Date < today.Date ? Expired : status;

		/// Where a step takes a set from where it is, or null when the step does not apply.
		public static string? After(string status, string step, DateTime? sterileUntil, DateTime today) => (status, step) switch
		{
			(HospitalSetStatus.AwaitingWash, HospitalSetEventKind.Washed) => HospitalSetStatus.Packed,
			(HospitalSetStatus.Packed, HospitalSetEventKind.Loaded) => HospitalSetStatus.InAutoclave,
			(HospitalSetStatus.InAutoclave, HospitalSetEventKind.Sterilised) => HospitalSetStatus.Sterile,
			(HospitalSetStatus.InAutoclave, HospitalSetEventKind.CycleFailed) => HospitalSetStatus.Packed,
			(HospitalSetStatus.Sterile, HospitalSetEventKind.Issued) when sterileUntil?.Date >= today.Date => HospitalSetStatus.InUse,
			(HospitalSetStatus.InUse, HospitalSetEventKind.Returned) => HospitalSetStatus.AwaitingWash,
			(HospitalSetStatus.Sterile, HospitalSetEventKind.Reprocess) => HospitalSetStatus.AwaitingWash,
			_ => null,
		};

		public static DateTime SterileUntil(DateTime today, int shelfLifeDays) => today.Date.AddDays(shelfLifeDays);

		public static string CycleNo(int n) => "CY-" + n.ToString("000000");

		public static string? SetRefusal(string code, string name, int shelfLifeDays)
		{
			if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 30) return "A set code is 1 to 30 characters";
			if (string.IsNullOrWhiteSpace(name)) return "Write the set's name";
			if (shelfLifeDays < 1 || shelfLifeDays > 365) return "The shelf life is 1 to 365 days";
			return null;
		}
	}

	public sealed class HospitalCssdService
	{
		public const string NoTables = "The sterile supply tables are not created yet (hospital_h42_cssd.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;

		public HospitalCssdService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalInstrumentSet> Sets(BusinessContext ctx) => _db.Set<HospitalInstrumentSet>().Where(s => s.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalSterilisationCycle> Cycles(BusinessContext ctx) => _db.Set<HospitalSterilisationCycle>().Where(c => c.CompanyId == ctx.CompanyId);
		private static DateTime Today(DateTime nowUtc) => HospitalNursingService.UtcToWard(nowUtc).Date;
		private void Event(BusinessContext ctx, HospitalInstrumentSet s, string kind, int? employeeId, DateTime nowUtc, int? cycleId = null, int? surgeryId = null)
			=> _db.Add(new HospitalSetEvent { CompanyId = ctx.CompanyId, SetId = s.ID, Kind = kind, CycleId = cycleId, SurgeryId = surgeryId, At = nowUtc, ByEmployeeId = employeeId });

		public async Task<(HospitalCssdBoard Board, bool Ready)> BoardAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			var today = Today(nowUtc);
			var cases = await (from s in _db.Set<HospitalSurgery>().AsNoTracking()
							   join p in _db.HospitalPatients.AsNoTracking() on s.PatientId equals p.ID
							   where s.CompanyId == ctx.CompanyId && (s.Status == HospitalSurgeryStatus.Planned || s.Status == HospitalSurgeryStatus.InProgress) && s.CaseDate >= today.AddDays(-1) && s.CaseDate <= today.AddDays(1)
							   orderby s.CaseDate, s.StartMinute
							   select new HospitalCssdCase(s.ID, s.CaseNo, s.Name, s.NameEn, p.Name, p.NameEn)).ToListAsync(ct);
			try
			{
				var sets = await Sets(ctx).AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Code).ToListAsync(ct);
				var cycles = await Cycles(ctx).AsNoTracking().Where(c => c.Result == HospitalCycleResult.Running || c.StartedAt >= nowUtc.AddDays(-7)).OrderByDescending(c => c.ID).Take(20).ToListAsync(ct);
				var cids = cycles.Select(c => c.ID).Concat(sets.Where(s => s.CycleId != null).Select(s => s.CycleId!.Value)).Distinct().ToList();
				var cycleNos = await Cycles(ctx).AsNoTracking().Where(c => cids.Contains(c.ID)).ToDictionaryAsync(c => c.ID, c => c.CycleNo, ct);
				var sids = sets.Where(s => s.SurgeryId != null).Select(s => s.SurgeryId!.Value).ToList();
				var caseNos = await _db.Set<HospitalSurgery>().AsNoTracking().Where(s => sids.Contains(s.ID)).ToDictionaryAsync(s => s.ID, s => s.CaseNo, ct);
				// a cycle's sets are the ones its "Loaded" events name
				var ids = cycles.Select(c => c.ID).ToList();
				var loaded = await (from e in _db.Set<HospitalSetEvent>().AsNoTracking()
									join s in _db.Set<HospitalInstrumentSet>().AsNoTracking() on e.SetId equals s.ID
									where e.CompanyId == ctx.CompanyId && e.Kind == HospitalSetEventKind.Loaded && e.CycleId != null && ids.Contains(e.CycleId.Value)
									select new { e.CycleId, s.Code }).ToListAsync(ct);
				var rows = sets.Select(s => new HospitalSetRow(s, HospitalCssdRules.State(s.Status, s.SterileUntil, today),
					s.CycleId is int c ? cycleNos.GetValueOrDefault(c) : null, s.SurgeryId is int x ? caseNos.GetValueOrDefault(x) : null)).ToList();
				var crows = cycles.Select(c => new HospitalCycleRow(c, loaded.Where(l => l.CycleId == c.ID).Select(l => l.Code).Distinct().OrderBy(x => x).ToList())).ToList();
				return (new HospitalCssdBoard(rows, crows, cases), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
			{
				return (new HospitalCssdBoard(Array.Empty<HospitalSetRow>(), Array.Empty<HospitalCycleRow>(), cases), false);
			}
		}

		public async Task<HospitalResult> SaveSetAsync(BusinessContext ctx, HospitalSetInput input, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalCssdRules.SetRefusal(input.Code, input.Name, input.ShelfLifeDays) is string why) return HospitalResult.Fail(why);
			var code = input.Code.Trim().ToUpperInvariant();
			try
			{
				if (await Sets(ctx).AnyAsync(s => s.Code == code && s.ID != (input.Id ?? 0), ct)) return HospitalResult.Fail("That set code is already used");
				var s = input.Id is int id ? await Sets(ctx).FirstOrDefaultAsync(x => x.ID == id, ct) : null;
				if (input.Id != null && s == null) return HospitalResult.Fail("not_found");
				if (s == null) _db.Add(s = new HospitalInstrumentSet { CompanyId = ctx.CompanyId, Status = HospitalSetStatus.AwaitingWash, CreatedAt = nowUtc });
				s.Code = code; s.Name = input.Name.Trim(); s.NameEn = string.IsNullOrWhiteSpace(input.NameEn) ? null : input.NameEn.Trim();
				s.ShelfLifeDays = input.ShelfLifeDays; s.IsActive = input.IsActive;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(s.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// One step on one set: wash, issue to a case, return from it, reprocess an expired one.
		public async Task<HospitalResult> StepAsync(BusinessContext ctx, int setId, string step, int? surgeryId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			if (step is not (HospitalSetEventKind.Washed or HospitalSetEventKind.Issued or HospitalSetEventKind.Returned or HospitalSetEventKind.Reprocess)) return HospitalResult.Fail("not_found");
			try
			{
				var s = await Sets(ctx).FirstOrDefaultAsync(x => x.ID == setId && x.IsActive, ct);
				if (s == null) return HospitalResult.Fail("not_found");
				var today = Today(nowUtc);
				if (step == HospitalSetEventKind.Issued && s.Status == HospitalSetStatus.Sterile && s.SterileUntil?.Date < today) return HospitalResult.Fail("This set's sterility has expired: send it to be reprocessed");
				var to = HospitalCssdRules.After(s.Status, step, s.SterileUntil, today);
				if (to == null) return HospitalResult.Fail("This set cannot take that step from where it is");
				int? caseId = null;
				if (step == HospitalSetEventKind.Issued)
				{
					var c = await _db.Set<HospitalSurgery>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == surgeryId && x.CompanyId == ctx.CompanyId, ct);
					if (c == null) return HospitalResult.Fail("Choose the theatre case");
					if (!HospitalSurgeryStatus.Holds(c.Status)) return HospitalResult.Fail("A set is issued to a planned case or one in progress");
					caseId = c.ID; s.SurgeryId = c.ID;
				}
				if (step == HospitalSetEventKind.Returned) caseId = s.SurgeryId;
				if (to != HospitalSetStatus.InUse) s.SurgeryId = null;
				if (to == HospitalSetStatus.AwaitingWash) s.SterileUntil = null;
				s.Status = to;
				Event(ctx, s, step, employeeId, nowUtc, s.CycleId, caseId);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(s.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// Loads packed sets into a new cycle on an autoclave.
		public async Task<HospitalResult> StartCycleAsync(BusinessContext ctx, IReadOnlyList<int> setIds, string? autoclave, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(autoclave)) return HospitalResult.Fail("Write which autoclave runs the cycle");
			var ids = setIds.Distinct().ToList();
			if (ids.Count == 0) return HospitalResult.Fail("Choose the packed sets to load");
			try
			{
				var sets = await Sets(ctx).Where(s => ids.Contains(s.ID) && s.IsActive).ToListAsync(ct);
				if (sets.Count != ids.Count || sets.Any(s => s.Status != HospitalSetStatus.Packed)) return HospitalResult.Fail("Only washed and packed sets are loaded");
				await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
				int n = await Cycles(ctx).CountAsync(ct) + 1;
				var c = new HospitalSterilisationCycle { CompanyId = ctx.CompanyId, CycleNo = HospitalCssdRules.CycleNo(n), Autoclave = autoclave.Trim()[..Math.Min(60, autoclave.Trim().Length)],
					Result = HospitalCycleResult.Running, StartedAt = nowUtc, StartedByEmployeeId = employeeId };
				_db.Add(c); await _db.SaveChangesAsync(ct);
				foreach (var s in sets) { s.Status = HospitalSetStatus.InAutoclave; s.CycleId = c.ID; Event(ctx, s, HospitalSetEventKind.Loaded, employeeId, nowUtc, c.ID); }
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync();
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}

		/// Ends a running cycle: passed (its sets are sterile until today + their shelf life) or failed (back to packed).
		public async Task<HospitalResult> EndCycleAsync(BusinessContext ctx, int cycleId, bool passed, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			if (!passed && string.IsNullOrWhiteSpace(note)) return HospitalResult.Fail("Write why the cycle failed");
			try
			{
				var c = await Cycles(ctx).FirstOrDefaultAsync(x => x.ID == cycleId, ct);
				if (c == null) return HospitalResult.Fail("not_found");
				if (c.Result != HospitalCycleResult.Running) return HospitalResult.Fail("This cycle has already ended");
				var today = Today(nowUtc);
				var sets = await Sets(ctx).Where(s => s.CycleId == c.ID && s.Status == HospitalSetStatus.InAutoclave).ToListAsync(ct);
				foreach (var s in sets)
				{
					var step = passed ? HospitalSetEventKind.Sterilised : HospitalSetEventKind.CycleFailed;
					s.Status = HospitalCssdRules.After(s.Status, step, null, today)!;
					s.SterileUntil = passed ? HospitalCssdRules.SterileUntil(today, s.ShelfLifeDays) : null;
					Event(ctx, s, step, employeeId, nowUtc, c.ID);
				}
				c.Result = passed ? HospitalCycleResult.Passed : HospitalCycleResult.Failed; c.EndedAt = nowUtc; c.EndedByEmployeeId = employeeId;
				c.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(300, note.Trim().Length)];
				await _db.SaveChangesAsync(ct);   // the cycle and its sets in one save
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}
	}
}
