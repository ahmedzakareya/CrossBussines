using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H43 — clinical dietary. The rules are HospitalDietaryRules' (no database).
	//   · Who is on the ward: an open bed stay in one of its rooms (H3), with the allergy on the file.
	//   · The diet (`admit` or `consult`): one open order per admission; a change closes the old one and reaches the
	//     trays already sent and not yet served (nil by mouth holds them, a diet again releases them).
	//   · A round (`admit`): sending it orders a tray for every ward patient who has none for that round — held for a
	//     patient nil by mouth — with the diet of the moment; serving it marks the sent trays served.
	//   · A patient with no diet ordered eats the regular diet.
	// ==========================================================================================
	public sealed record HospitalDietRow(int AdmissionId, int PatientId, string Name, string NameEn, string Mrn, string? Photo, string Bed,
		string? Allergies, string Diet, bool Ordered, HospitalMealOrder? Meal);

	public sealed record HospitalDietBoard(HospitalWardOption Ward, DateTime Day, string Round, IReadOnlyList<HospitalDietRow> Rows);

	public static class HospitalDietaryRules
	{
		/// The round a ward is working on at this time of day (the kitchen's: breakfast 06:30, lunch 12:00, snack 15:30, dinner 18:00).
		public static string RoundAt(DateTime wardNow) => wardNow.Hour switch
		{
			< 10 => HospitalMealRound.Breakfast,
			< 15 => HospitalMealRound.Lunch,
			< 17 => HospitalMealRound.Snack,
			_ => HospitalMealRound.Dinner,
		};

		/// A tray's status when it is sent with this diet.
		public static string StatusFor(string diet) => diet == HospitalDiet.Npo ? HospitalMealStatus.Held : HospitalMealStatus.Sent;

		/// A sent tray after the diet changes: the new status, or null when it is already served and stays as it was.
		public static string? AfterDietChange(string status, string newDiet) => status == HospitalMealStatus.Served ? null : StatusFor(newDiet);

		/// The diet a patient eats: the open order's, else regular.
		public static string Effective(string? ordered) => ordered ?? HospitalDiet.Regular;
	}

	public sealed class HospitalDietaryService
	{
		public const string NoTables = "The dietary tables are not created yet (hospital_h43_dietary.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;

		public HospitalDietaryService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalDietOrder> Diets(BusinessContext ctx) => _db.Set<HospitalDietOrder>().Where(d => d.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalMealOrder> Meals(BusinessContext ctx) => _db.Set<HospitalMealOrder>().Where(m => m.CompanyId == ctx.CompanyId);
		public static DateTime Today(DateTime nowUtc) => HospitalNursingService.UtcToWard(nowUtc).Date;

		private sealed record Here(int AdmissionId, int PatientId, string Name, string NameEn, string Mrn, string? Photo, string? Allergies, string Bed);

		private Task<List<Here>> OnWardAsync(BusinessContext ctx, int wardId, CancellationToken ct)
			=> (from s in _db.HospitalBedStays.AsNoTracking()
				join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
				join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
				join a in _db.HospitalAdmissions.AsNoTracking() on s.AdmissionId equals a.ID
				join p in _db.HospitalPatients.AsNoTracking() on s.PatientId equals p.ID
				where s.CompanyId == ctx.CompanyId && s.EndedAt == null && r.WardId == wardId && a.Status == HospitalAdmissionStatus.Active && p.CompanyId == ctx.CompanyId
				orderby b.Code
				select new Here(s.AdmissionId, p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath, p.Allergies, b.Code)).ToListAsync(ct);

		public async Task<(HospitalDietBoard? Board, bool Ready)> BoardAsync(BusinessContext ctx, int wardId, string? round, DateTime nowUtc, CancellationToken ct = default)
		{
			var ward = await _db.HospitalWards.AsNoTracking().Where(w => w.ID == wardId && w.CompanyId == ctx.CompanyId).Select(w => new HospitalWardOption(w.ID, w.Code, w.Name, w.NameEn)).FirstOrDefaultAsync(ct);
			if (ward == null) return (null, true);
			var day = Today(nowUtc);
			var r = HospitalMealRound.All.Contains(round ?? "") ? round! : HospitalDietaryRules.RoundAt(HospitalNursingService.UtcToWard(nowUtc));
			var here = await OnWardAsync(ctx, wardId, ct);
			try
			{
				var ids = here.Select(h => h.AdmissionId).ToList();
				var diets = await Diets(ctx).AsNoTracking().Where(d => ids.Contains(d.AdmissionId) && d.EndedAt == null).ToDictionaryAsync(d => d.AdmissionId, d => d.Diet, ct);
				var meals = await Meals(ctx).AsNoTracking().Where(m => ids.Contains(m.AdmissionId) && m.Day == day && m.Round == r).ToDictionaryAsync(m => m.AdmissionId, ct);
				var rows = here.Select(h => new HospitalDietRow(h.AdmissionId, h.PatientId, h.Name, h.NameEn, h.Mrn, h.Photo, h.Bed, h.Allergies,
					HospitalDietaryRules.Effective(diets.GetValueOrDefault(h.AdmissionId)), diets.ContainsKey(h.AdmissionId), meals.GetValueOrDefault(h.AdmissionId))).ToList();
				return (new HospitalDietBoard(ward, day, r, rows), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
			{
				return (new HospitalDietBoard(ward, day, r, here.Select(h => new HospitalDietRow(h.AdmissionId, h.PatientId, h.Name, h.NameEn, h.Mrn, h.Photo, h.Bed, h.Allergies, HospitalDiet.Regular, false, null)).ToList()), false);
			}
		}

		/// Today's patient trays by diet, every ward (the kitchen's "Patient meals by diet").
		public async Task<IReadOnlyDictionary<string, int>> DayByDietAsync(BusinessContext ctx, DateTime day, CancellationToken ct = default)
		{
			try
			{
				var rows = await Meals(ctx).AsNoTracking().Where(m => m.Day == day.Date).GroupBy(m => m.Diet).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
				return HospitalDiet.All.ToDictionary(d => d, d => rows.FirstOrDefault(x => x.Key == d)?.N ?? 0);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return HospitalDiet.All.ToDictionary(d => d, _ => 0); }
		}

		/// Today's trays per round, every ward: how many were sent (held ones excluded) and served.
		public async Task<IReadOnlyDictionary<string, (int Sent, int Served)>> DayByRoundAsync(BusinessContext ctx, DateTime day, CancellationToken ct = default)
		{
			try
			{
				var rows = await Meals(ctx).AsNoTracking().Where(m => m.Day == day.Date && m.Status != HospitalMealStatus.Held).GroupBy(m => new { m.Round, m.Status }).Select(g => new { g.Key.Round, g.Key.Status, N = g.Count() }).ToListAsync(ct);
				return HospitalMealRound.All.ToDictionary(r => r, r => (rows.Where(x => x.Round == r).Sum(x => x.N), rows.Where(x => x.Round == r && x.Status == HospitalMealStatus.Served).Sum(x => x.N)));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return HospitalMealRound.All.ToDictionary(r => r, _ => (0, 0)); }
		}

		public async Task<HospitalResult> SetDietAsync(BusinessContext ctx, int admissionId, string diet, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct) && !await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalDiet.All.Contains(diet)) return HospitalResult.Fail("Choose the diet");
			var a = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(x => x.ID == admissionId && x.CompanyId == ctx.CompanyId && x.Status == HospitalAdmissionStatus.Active, ct);
			if (a == null) return HospitalResult.Fail("This patient is not admitted");
			try
			{
				await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
				var open = await Diets(ctx).FirstOrDefaultAsync(d => d.AdmissionId == a.ID && d.EndedAt == null, ct);
				if (open?.Diet == diet && (note ?? "") == (open.Note ?? "")) return HospitalResult.Success(open.ID);
				if (open != null) { open.EndedAt = nowUtc; await _db.SaveChangesAsync(ct); }
				var d = new HospitalDietOrder { CompanyId = ctx.CompanyId, AdmissionId = a.ID, PatientId = a.PatientId, Diet = diet,
					Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(300, note.Trim().Length)], StartedAt = nowUtc, OrderedByEmployeeId = employeeId };
				_db.Add(d);
				// the trays already on their way today follow the new diet (a served one stays as it was)
				foreach (var m in await Meals(ctx).Where(m => m.AdmissionId == a.ID && m.Day == Today(nowUtc) && m.Status != HospitalMealStatus.Served).ToListAsync(ct))
					if (HospitalDietaryRules.AfterDietChange(m.Status, diet) is string st) { m.Diet = diet; m.Status = st; }
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync();
				return HospitalResult.Success(d.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}

		/// Sends a round of a ward to the kitchen: a tray for every patient who has none for it yet.
		public async Task<HospitalResult> SendRoundAsync(BusinessContext ctx, int wardId, string round, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalMealRound.All.Contains(round)) return HospitalResult.Fail("Choose the round");
			if (!await _db.HospitalWards.AnyAsync(w => w.ID == wardId && w.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var day = Today(nowUtc);
			try
			{
				var here = await OnWardAsync(ctx, wardId, ct);
				var ids = here.Select(h => h.AdmissionId).ToList();
				var sent = await Meals(ctx).Where(m => ids.Contains(m.AdmissionId) && m.Day == day && m.Round == round).Select(m => m.AdmissionId).ToListAsync(ct);
				var diets = await Diets(ctx).AsNoTracking().Where(d => ids.Contains(d.AdmissionId) && d.EndedAt == null).ToDictionaryAsync(d => d.AdmissionId, d => d.Diet, ct);
				var todo = here.Where(h => !sent.Contains(h.AdmissionId)).ToList();
				if (todo.Count == 0) return HospitalResult.Fail("Every patient's tray for this round is already sent");
				foreach (var h in todo)
				{
					var diet = HospitalDietaryRules.Effective(diets.GetValueOrDefault(h.AdmissionId));
					_db.Add(new HospitalMealOrder { CompanyId = ctx.CompanyId, AdmissionId = h.AdmissionId, PatientId = h.PatientId, WardId = wardId, Day = day, Round = round,
						Diet = diet, Status = HospitalDietaryRules.StatusFor(diet), SentAt = nowUtc, SentByEmployeeId = employeeId });
				}
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(todo.Count);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}

		/// The kitchen delivered a ward's round: its sent trays are served.
		public async Task<HospitalResult> ServeRoundAsync(BusinessContext ctx, int wardId, string round, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			var day = Today(nowUtc);
			try
			{
				var n = await Meals(ctx).Where(m => m.WardId == wardId && m.Day == day && m.Round == round && m.Status == HospitalMealStatus.Sent)
					.ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, HospitalMealStatus.Served).SetProperty(m => m.ServedAt, (DateTime?)nowUtc), ct);
				return n == 0 ? HospitalResult.Fail("No sent tray of this round waits to be served") : HospitalResult.Success(n);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return HospitalResult.Fail(NoTables); }
		}
	}
}
