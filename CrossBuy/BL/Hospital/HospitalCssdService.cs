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
	//   · H170 — the release policy of the dental cycles (H169): a cycle ends with its physical record (time, °C, kPa)
	//     and its chemical indicator; its sets wait in QUARANTINE until a person releases the cycle — a load with
	//     implantable items only after its biological indicator passes. A failure found after release (or a recall by
	//     hand) recalls the cycle: its sets still on the shelf go back to be washed, and every theatre case a set
	//     reached is listed for infection control — told, then closed with the outcome. A set is issued only from a
	//     pack checked intact.
	// ==========================================================================================
	public sealed record HospitalSetRow(HospitalInstrumentSet Set, string State, string? CycleNo, string? CaseNo);
	public sealed record HospitalCycleRow(HospitalSterilisationCycle Cycle, IReadOnlyList<string> Sets, string? ReleasedBy = null);
	/// H170: a theatre case a recalled cycle's set reached.
	public sealed record HospitalCssdRecallRow(HospitalCssdRecall Recall, string CycleNo, string? RecallReason, string SetCode, string CaseNo, string PatientName, string PatientNameEn, string Mrn, string? Phone, DateTime IssuedAt);

	/// H170: the end of a cycle — passed (the physical record met, the chemical indicator passed) or failed (why).
	public sealed class HospitalCycleEndRecord
	{
		public bool Passed { get; set; }
		public string? Note { get; set; }
		public int? ExposureMinutes { get; set; }
		public decimal? TemperatureC { get; set; }
		public decimal? PressureKpa { get; set; }
	}
	public sealed record HospitalCssdCase(int Id, string CaseNo, string Name, string NameEn, string Patient, string PatientEn);
	public sealed record HospitalCssdBoard(IReadOnlyList<HospitalSetRow> Sets, IReadOnlyList<HospitalCycleRow> Cycles, IReadOnlyList<HospitalCssdCase> Cases)
	{
		/// H170: the cycles waiting on a person (release) or a result (biological), and the recall follow-up.
		public IReadOnlyList<HospitalCycleRow> Pending { get; init; } = Array.Empty<HospitalCycleRow>();
		public IReadOnlyList<HospitalCssdRecallRow> Recalls { get; init; } = Array.Empty<HospitalCssdRecallRow>();
	}

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
			(HospitalSetStatus.InAutoclave, HospitalSetEventKind.Sterilised) => HospitalSetStatus.Quarantine,   // H170: held until released
			(HospitalSetStatus.InAutoclave, HospitalSetEventKind.CycleFailed) => HospitalSetStatus.Packed,
			(HospitalSetStatus.Quarantine, HospitalSetEventKind.Released) => HospitalSetStatus.Sterile,
			(HospitalSetStatus.Quarantine, HospitalSetEventKind.CycleFailed) => HospitalSetStatus.Packed,         // its biological failed before release
			(HospitalSetStatus.Quarantine, HospitalSetEventKind.Recalled) => HospitalSetStatus.AwaitingWash,
			(HospitalSetStatus.Sterile, HospitalSetEventKind.Recalled) => HospitalSetStatus.AwaitingWash,
			(HospitalSetStatus.Sterile, HospitalSetEventKind.Issued) when sterileUntil?.Date >= today.Date => HospitalSetStatus.InUse,
			(HospitalSetStatus.InUse, HospitalSetEventKind.Returned) => HospitalSetStatus.AwaitingWash,
			(HospitalSetStatus.Sterile, HospitalSetEventKind.Reprocess) => HospitalSetStatus.AwaitingWash,
			_ => null,
		};

		public static DateTime SterileUntil(DateTime today, int shelfLifeDays) => today.Date.AddDays(shelfLifeDays);

		public static string CycleNo(int n) => "CY-" + n.ToString("000000");

		/// H170: a passed end needs the physical record within bounds; a failed one needs its reason.
		public static string? EndRefusal(HospitalCycleEndRecord r) =>
			!r.Passed ? (string.IsNullOrWhiteSpace(r.Note) ? "Write why the cycle failed" : null)
			: r.ExposureMinutes is not (>= 1 and <= HospitalDentalPracticeRules.MaxExposureMinutes) ? "Enter the exposure time in minutes"
			: r.TemperatureC is not (>= 100m and <= 150m) ? "Enter the sterilisation temperature (100–150 °C)"
			: r.PressureKpa is < 0m or > 400m ? "The pressure is out of range" : null;

		public static bool Held(HospitalSterilisationCycle c) =>
			c.RecalledAt != null || c.Result == HospitalCycleResult.Failed || c.Chemical == HospitalIndicator.Fail || c.Biological == HospitalIndicator.Fail;

		/// H170: released by a person once the run passed (physical and chemical); a load with implantable items only
		/// after its biological indicator passed. Never a held or recalled cycle, never twice.
		public static string? ReleaseRefusal(HospitalSterilisationCycle c) =>
			c.RecalledAt != null ? "This cycle was recalled"
			: c.ReleasedAt != null ? "This cycle is already released"
			: c.Result == HospitalCycleResult.Running ? "This cycle is still running"
			: Held(c) ? "This cycle failed its checks: its kits are held"
			: c.Chemical != HospitalIndicator.Pass ? "Release the cycle on its chemical indicator first"
			: c.ImplantLoad && c.Biological != HospitalIndicator.Pass ? "A load with implantable items waits for its biological indicator" : null;

		/// A cycle still waiting on someone: to be released, or its biological result (within the week it incubates).
		public static bool Pending(HospitalSterilisationCycle c, DateTime nowUtc) =>
			c.Result == HospitalCycleResult.Passed && c.RecalledAt == null && c.Biological != HospitalIndicator.Fail
			&& (c.ReleasedAt == null || (c.Biological == HospitalIndicator.Pending && c.EndedAt >= nowUtc.AddDays(-7)));

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
		private void Event(BusinessContext ctx, HospitalInstrumentSet s, string kind, int? employeeId, DateTime nowUtc, int? cycleId = null, int? surgeryId = null, bool? packIntact = null)
			=> _db.Add(new HospitalSetEvent { CompanyId = ctx.CompanyId, SetId = s.ID, Kind = kind, CycleId = cycleId, SurgeryId = surgeryId, At = nowUtc, ByEmployeeId = employeeId, PackIntact = packIntact });

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
				// H170: the cycles still waiting on someone, whatever their age; the release names who released it
				var pend = await Cycles(ctx).AsNoTracking().Where(c => c.Result == HospitalCycleResult.Passed && c.RecalledAt == null && c.Biological != HospitalIndicator.Fail
						&& (c.ReleasedAt == null || (c.Biological == HospitalIndicator.Pending && c.EndedAt >= nowUtc.AddDays(-7)))).OrderBy(c => c.ID).Take(30).ToListAsync(ct);
				var pids = pend.Select(c => c.ID).Except(ids).ToList();
				if (pids.Count > 0)
					loaded.AddRange(await (from e in _db.Set<HospitalSetEvent>().AsNoTracking()
											join s in _db.Set<HospitalInstrumentSet>().AsNoTracking() on e.SetId equals s.ID
											where e.CompanyId == ctx.CompanyId && e.Kind == HospitalSetEventKind.Loaded && e.CycleId != null && pids.Contains(e.CycleId.Value)
											select new { e.CycleId, s.Code }).ToListAsync(ct));
				var emps = cycles.Concat(pend).Where(c => c.ReleasedByEmployeeId != null).Select(c => c.ReleasedByEmployeeId!.Value).Distinct().ToList();
				var names = await _db.Employee.AsNoTracking().Where(e => emps.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
				HospitalCycleRow Row(HospitalSterilisationCycle c) => new(c, loaded.Where(l => l.CycleId == c.ID).Select(l => l.Code).Distinct().OrderBy(x => x).ToList(),
					names.FirstOrDefault(n => n.ID == c.ReleasedByEmployeeId) is { } rb ? DisplayName.Of(rb.FullName, rb.FullNameEn ?? rb.FullName) : null);
				var crows = cycles.Select(Row).ToList();
				return (new HospitalCssdBoard(rows, crows, cases) { Pending = pend.Select(Row).ToList(), Recalls = await RecallsAsync(ctx, nowUtc, ct) }, true);
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
		public async Task<HospitalResult> StepAsync(BusinessContext ctx, int setId, string step, int? surgeryId, int? employeeId, DateTime nowUtc, CancellationToken ct = default, bool packIntact = false)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			if (step is not (HospitalSetEventKind.Washed or HospitalSetEventKind.Issued or HospitalSetEventKind.Returned or HospitalSetEventKind.Reprocess)) return HospitalResult.Fail("not_found");
			if (step == HospitalSetEventKind.Issued && !packIntact) return HospitalResult.Fail("A pack that is not intact is not used: send it back to be re-sterilised");   // H170
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
				Event(ctx, s, step, employeeId, nowUtc, s.CycleId, caseId, step == HospitalSetEventKind.Issued ? true : null);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(s.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// Loads packed sets into a new cycle on an autoclave.
		public async Task<HospitalResult> StartCycleAsync(BusinessContext ctx, IReadOnlyList<int> setIds, string? autoclave, int? employeeId, DateTime nowUtc, CancellationToken ct = default, bool implantLoad = false)
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
					Result = HospitalCycleResult.Running, StartedAt = nowUtc, StartedByEmployeeId = employeeId, ImplantLoad = implantLoad };
				_db.Add(c); await _db.SaveChangesAsync(ct);
				foreach (var s in sets) { s.Status = HospitalSetStatus.InAutoclave; s.CycleId = c.ID; Event(ctx, s, HospitalSetEventKind.Loaded, employeeId, nowUtc, c.ID); }
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync();
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}

		/// Ends a running cycle: passed — its physical record met and its chemical indicator passed; its sets wait in
		/// quarantine for release (H170) — or failed (back to packed, the reason kept).
		public async Task<HospitalResult> EndCycleAsync(BusinessContext ctx, int cycleId, HospitalCycleEndRecord rec, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalCssdRules.EndRefusal(rec) is string why) return HospitalResult.Fail(why);
			bool passed = rec.Passed; string? note = rec.Note;
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
					s.SterileUntil = null;   // dated when released
					Event(ctx, s, step, employeeId, nowUtc, c.ID);
				}
				c.Result = passed ? HospitalCycleResult.Passed : HospitalCycleResult.Failed; c.EndedAt = nowUtc; c.EndedByEmployeeId = employeeId;
				if (passed) { c.ExposureMinutes = rec.ExposureMinutes; c.TemperatureC = rec.TemperatureC; c.PressureKpa = rec.PressureKpa; c.Chemical = HospitalIndicator.Pass; }
				c.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(300, note.Trim().Length)];
				await _db.SaveChangesAsync(ct);   // the cycle and its sets in one save
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// H170: a person releases a passed cycle: its quarantined sets become sterile until today + their shelf life.
		public async Task<HospitalResult> ReleaseAsync(BusinessContext ctx, int cycleId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			var c = await Cycles(ctx).FirstOrDefaultAsync(x => x.ID == cycleId, ct);
			if (c == null) return HospitalResult.Fail("not_found");
			if (HospitalCssdRules.ReleaseRefusal(c) is string why) return HospitalResult.Fail(why);
			var today = Today(nowUtc);
			var sets = await Sets(ctx).Where(s => s.CycleId == c.ID && s.Status == HospitalSetStatus.Quarantine).ToListAsync(ct);
			foreach (var s in sets)
			{
				s.Status = HospitalSetStatus.Sterile; s.SterileUntil = HospitalCssdRules.SterileUntil(today, s.ShelfLifeDays);
				Event(ctx, s, HospitalSetEventKind.Released, employeeId, nowUtc, c.ID);
			}
			c.ReleasedAt = nowUtc; c.ReleasedByEmployeeId = employeeId;
			try { await _db.SaveChangesAsync(ct); }   // the release and its sets in one save
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
			return HospitalResult.Success(c.ID);
		}

		/// H170: the biological indicator's result, once. A failure before release sends the quarantined sets back to be
		/// run again; a failure after release recalls the cycle.
		public async Task<HospitalResult> BiologicalAsync(BusinessContext ctx, int cycleId, string result, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			if (result is not (HospitalIndicator.Pass or HospitalIndicator.Fail)) return HospitalResult.Fail("Record pass or fail");
			var c = await Cycles(ctx).FirstOrDefaultAsync(x => x.ID == cycleId, ct);
			if (c == null) return HospitalResult.Fail("not_found");
			if (c.Result != HospitalCycleResult.Passed) return HospitalResult.Fail("This cycle has not passed");
			if (c.Biological != HospitalIndicator.Pending) return HospitalResult.Fail("This indicator is already recorded");
			await using var tx = _db.Database.IsRelational() ? await ScopedTx.BeginOrJoinAsync(_db) : null;
			c.Biological = result;
			if (result == HospitalIndicator.Fail && c.ReleasedAt == null)
			{
				var today = Today(nowUtc);
				foreach (var s in await Sets(ctx).Where(s => s.CycleId == c.ID && s.Status == HospitalSetStatus.Quarantine).ToListAsync(ct))
				{
					s.Status = HospitalCssdRules.After(s.Status, HospitalSetEventKind.CycleFailed, null, today)!;
					Event(ctx, s, HospitalSetEventKind.CycleFailed, employeeId, nowUtc, c.ID);
				}
			}
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
			if (result == HospitalIndicator.Fail && c.ReleasedAt != null && c.RecalledAt == null)
			{
				var r = await RecallInTxAsync(ctx, c.ID, "Biological indicator failed after release", employeeId, nowUtc, ct);
				if (!r.Ok) return r;
			}
			if (tx != null) await tx.CommitAsync();
			return HospitalResult.Success(c.ID);
		}

		/// H170: a cycle recalled by hand, with its reason.
		public async Task<HospitalResult> RecallAsync(BusinessContext ctx, int cycleId, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(reason)) return HospitalResult.Fail("Enter the reason for the recall");
			var c = await Cycles(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == cycleId, ct);
			if (c == null) return HospitalResult.Fail("not_found");
			if (c.RecalledAt != null) return HospitalResult.Fail("This cycle was recalled");
			if (c.Result == HospitalCycleResult.Running) return HospitalResult.Fail("This cycle is still running");
			await using var tx = _db.Database.IsRelational() ? await ScopedTx.BeginOrJoinAsync(_db) : null;
			var r = await RecallInTxAsync(ctx, c.ID, reason.Trim()[..Math.Min(300, reason.Trim().Length)], employeeId, nowUtc, ct);
			if (r.Ok && tx != null) await tx.CommitAsync();
			return r;
		}

		/// The cycle is recalled; its sets still in quarantine or on the shelf go back to be washed; every case a set of
		/// this cycle was issued to gets a follow-up row (the issue event names the cycle).
		private async Task<HospitalResult> RecallInTxAsync(BusinessContext ctx, int cycleId, string reason, int? employeeId, DateTime nowUtc, CancellationToken ct)
		{
			var c = await Cycles(ctx).FirstAsync(x => x.ID == cycleId, ct);
			if (c.RecalledAt != null) return HospitalResult.Fail("This cycle was recalled");
			c.RecalledAt = nowUtc; c.RecallReason = reason;
			var today = Today(nowUtc);
			foreach (var s in await Sets(ctx).Where(s => s.CycleId == c.ID && (s.Status == HospitalSetStatus.Quarantine || s.Status == HospitalSetStatus.Sterile)).ToListAsync(ct))
			{
				s.Status = HospitalCssdRules.After(s.Status, HospitalSetEventKind.Recalled, s.SterileUntil, today)!; s.SterileUntil = null;
				Event(ctx, s, HospitalSetEventKind.Recalled, employeeId, nowUtc, c.ID);
			}
			var issued = await (from e in _db.Set<HospitalSetEvent>().AsNoTracking()
								join g in _db.Set<HospitalSurgery>().AsNoTracking() on e.SurgeryId equals g.ID
								where e.CompanyId == ctx.CompanyId && e.CycleId == c.ID && e.Kind == HospitalSetEventKind.Issued
								select new { e.ID, e.SetId, g.PatientId, SurgeryId = g.ID }).ToListAsync(ct);
			foreach (var i in issued)
				_db.Add(new HospitalCssdRecall { CompanyId = ctx.CompanyId, CycleId = c.ID, SetId = i.SetId, EventId = i.ID, SurgeryId = i.SurgeryId, PatientId = i.PatientId, CreatedAt = nowUtc });
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
			return HospitalResult.Success(c.ID);
		}

		/// H170: the follow-up — every case still open, and those closed in the last 30 days.
		public async Task<IReadOnlyList<HospitalCssdRecallRow>> RecallsAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			var since = nowUtc.AddDays(-30);
			var rows = await (from r in _db.Set<HospitalCssdRecall>().AsNoTracking()
							  join c in Cycles(ctx).AsNoTracking() on r.CycleId equals c.ID
							  join s in _db.Set<HospitalInstrumentSet>().AsNoTracking() on r.SetId equals s.ID
							  join e in _db.Set<HospitalSetEvent>().AsNoTracking() on r.EventId equals e.ID
							  join g in _db.Set<HospitalSurgery>().AsNoTracking() on r.SurgeryId equals g.ID
							  join p in _db.HospitalPatients.AsNoTracking() on r.PatientId equals p.ID
							  where r.CompanyId == ctx.CompanyId && (r.ClosedAt == null || r.ClosedAt >= since)
							  orderby r.ClosedAt != null, r.CreatedAt descending
							  select new { r, c.CycleNo, c.RecallReason, s.Code, g.CaseNo, p.Name, p.NameEn, p.Mrn, p.Phone, e.At }).Take(200).ToListAsync(ct);
			return rows.Select(x => new HospitalCssdRecallRow(x.r, x.CycleNo, x.RecallReason, x.Code, x.CaseNo, x.Name, x.NameEn ?? x.Name, x.Mrn, x.Phone, x.At)).ToList();
		}

		public async Task<HospitalResult> RecallNotifyAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			int n = await _db.Set<HospitalCssdRecall>().Where(r => r.CompanyId == ctx.CompanyId && r.ID == id && r.NotifiedAt == null && r.ClosedAt == null)
				.ExecuteUpdateAsync(u => u.SetProperty(r => r.NotifiedAt, nowUtc).SetProperty(r => r.NotifiedByEmployeeId, employeeId), ct);
			return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail("This patient is already recorded as told");
		}

		public async Task<HospitalResult> RecallCloseAsync(BusinessContext ctx, int id, string? outcome, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(outcome)) return HospitalResult.Fail("Enter the outcome of the follow-up");
			var r0 = await _db.Set<HospitalCssdRecall>().AsNoTracking().FirstOrDefaultAsync(r => r.CompanyId == ctx.CompanyId && r.ID == id, ct);
			if (r0 == null) return HospitalResult.Fail("not_found");
			if (r0.ClosedAt != null) return HospitalResult.Fail("This follow-up is already closed");
			if (r0.NotifiedAt == null) return HospitalResult.Fail("Record that the patient was told first");
			var o = outcome.Trim()[..Math.Min(300, outcome.Trim().Length)];
			int n = await _db.Set<HospitalCssdRecall>().Where(r => r.CompanyId == ctx.CompanyId && r.ID == id && r.ClosedAt == null && r.NotifiedAt != null)
				.ExecuteUpdateAsync(u => u.SetProperty(r => r.Outcome, o).SetProperty(r => r.ClosedAt, nowUtc).SetProperty(r => r.ClosedByEmployeeId, employeeId), ct);
			return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail(HospitalNursingService.ConcurrentChange);
		}
	}
}
