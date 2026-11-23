using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalBlockInput { public int? Id { get; set; } public int RoomId { get; set; } public int Weekday { get; set; } public int StartMinute { get; set; } public int EndMinute { get; set; } public int? SurgeonId { get; set; } public string? Label { get; set; } public bool IsActive { get; set; } = true; }
	public sealed class HospitalCardInput
	{
		public int SurgeonId { get; set; } public int ServiceId { get; set; }
		public string? Position { get; set; } public string? SkinPrep { get; set; } public string? Gloves { get; set; } public string? Instruments { get; set; }
		public string? Equipment { get; set; } public string? Sutures { get; set; } public string? Notes { get; set; }
	}

	public sealed record HospitalBlockRow(HospitalTheatreBlock Block, HospitalTheatreRoom? Room, HospitalPortalDoctorRef? Surgeon, HospitalBlockUse Use);
	public sealed record HospitalCardRow(HospitalPreferenceCard Card, HospitalPortalDoctorRef? Surgeon, string Procedure, string ProcedureEn, string Code);
	public sealed record HospitalBlocksPage(bool Ready, IReadOnlyList<HospitalBlockRow> Blocks, decimal? Overall, IReadOnlyList<HospitalCardRow> Cards, int CardTotal,
		IReadOnlyList<HospitalTheatreRoom> Rooms, IReadOnlyList<HospitalPortalDoctorRef> Surgeons, DateTime From, DateTime To);

	// ==========================================================================================
	// HOSPITAL H244 — block time and preference cards. Anyone in the hospital reads them; the theatre manager
	// (`manage`) keeps the blocks; a surgeon (`consult`) or the manager keeps the cards.
	// ==========================================================================================
	public sealed class HospitalTheatreBlockService
	{
		public const string NoTables = "Block time and preference cards are not installed yet (hospital_h244_theatre_blocks.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalTheatreService _theatre;
		public HospitalTheatreBlockService(CrossDbContext db, IHospitalAccessService access, HospitalTheatreService theatre) { _db = db; _access = access; _theatre = theatre; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;

		public async Task<HospitalBlocksPage> PageAsync(BusinessContext ctx, DateTime today, int weeks, string? q, int page, int size, CancellationToken ct = default)
		{
			var rooms = await _theatre.TheatresAsync(ctx, ct);
			var team = (await _theatre.TeamAsync(ctx, ct)).Select(t => t.Doctor).ToList();
			var byId = team.ToDictionary(t => t.Id);
			DateTime to = today.Date.AddDays(-1), from = to.AddDays(-7 * Math.Clamp(weeks, 1, 26) + 1);
			try
			{
				var blocks = await _db.Set<HospitalTheatreBlock>().AsNoTracking().Where(b => b.CompanyId == ctx.CompanyId).OrderBy(b => b.Weekday).ThenBy(b => b.StartMinute).ToListAsync(ct);
				var raw = await _db.Set<HospitalSurgery>().AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.Status != HospitalSurgeryStatus.Cancelled && s.CaseDate >= from && s.CaseDate <= to)
					.Select(s => new { s.RoomId, s.CaseDate, s.StartMinute, s.DurationMinutes, s.SurgeonId, s.Status, s.StartedAt, s.EndedAt }).ToListAsync(ct);
				var cases = raw.Select(s =>
				{
					if (s.Status == HospitalSurgeryStatus.Done && s.StartedAt is DateTime a && s.EndedAt is DateTime e)
					{
						var st = HospitalNursingService.UtcToWard(a);
						return new HospitalBlockCase(s.RoomId, s.CaseDate, st.Hour * 60 + st.Minute, Math.Max(0, (int)(e - a).TotalMinutes), s.SurgeonId);
					}
					return new HospitalBlockCase(s.RoomId, s.CaseDate, s.StartMinute, s.DurationMinutes, s.SurgeonId);
				}).ToList();
				var rows = blocks.Select(b => new HospitalBlockRow(b, rooms.FirstOrDefault(r => r.Id == b.RoomId), b.SurgeonId is int sid ? byId.GetValueOrDefault(sid) : null,
					HospitalTheatreBlockRules.Use(b, cases, from, to))).ToList();
				var act = rows.Where(r => r.Block.IsActive).ToList();
				int bm = act.Sum(r => r.Use.BlockMinutes);
				decimal? overall = bm == 0 ? null : Math.Round(act.Sum(r => r.Use.UsedMinutes) * 100m / bm, 0, MidpointRounding.AwayFromZero);

				var cq = from c in _db.Set<HospitalPreferenceCard>().AsNoTracking()
						 join s in _db.HospitalServices.AsNoTracking() on c.ServiceId equals s.ID
						 where c.CompanyId == ctx.CompanyId
						 select new { c, s.Name, s.NameEn, s.Code };
				var all = await cq.ToListAsync(ct);
				IEnumerable<HospitalCardRow> cards = all.Select(x => new HospitalCardRow(x.c, byId.GetValueOrDefault(x.c.SurgeonId), x.Name, x.NameEn, x.Code));
				if (!string.IsNullOrWhiteSpace(q))
				{
					var t = q.Trim();
					cards = cards.Where(c => c.Procedure.Contains(t, StringComparison.OrdinalIgnoreCase) || c.ProcedureEn.Contains(t, StringComparison.OrdinalIgnoreCase) || c.Code.Contains(t, StringComparison.OrdinalIgnoreCase)
						|| (c.Surgeon?.Name ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || (c.Surgeon?.NameEn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase));
				}
				var list = cards.OrderBy(c => c.Surgeon?.Name).ThenBy(c => c.Code).ToList();
				return new HospitalBlocksPage(true, rows, overall, list.Skip((Math.Max(1, page) - 1) * size).Take(size).ToList(), list.Count, rooms, team, from, to);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex))
			{
				return new HospitalBlocksPage(false, Array.Empty<HospitalBlockRow>(), null, Array.Empty<HospitalCardRow>(), 0, rooms, team, from, to);
			}
		}

		/// The booked surgeon's card for the procedure, for the case card. Null when none (or not installed).
		public async Task<HospitalPreferenceCard?> CardForAsync(BusinessContext ctx, int surgeonId, int serviceId, CancellationToken ct = default)
		{
			try { return await _db.Set<HospitalPreferenceCard>().AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == ctx.CompanyId && c.SurgeonId == surgeonId && c.ServiceId == serviceId, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}

		public async Task<HospitalResult> SaveBlockAsync(BusinessContext ctx, HospitalBlockInput i, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			if ((await _theatre.TheatresAsync(ctx, ct)).All(r => r.Id != i.RoomId)) return HospitalResult.Fail("not_found");
			if (i.SurgeonId is int sid && !await _db.HospitalPractitioners.AnyAsync(p => p.ID == sid && p.CompanyId == ctx.CompanyId && p.IsActive, ct)) return HospitalResult.Fail("not_found");
			try
			{
				var same = await _db.Set<HospitalTheatreBlock>().AsNoTracking().Where(b => b.CompanyId == ctx.CompanyId && b.RoomId == i.RoomId && b.Weekday == i.Weekday).ToListAsync(ct);
				if (i.IsActive && HospitalTheatreBlockRules.BlockRefusal(i.Weekday, i.StartMinute, i.EndMinute, i.SurgeonId, i.Label, same, i.Id) is string why) return HospitalResult.Fail(why);
				HospitalTheatreBlock? b = i.Id is int id ? await _db.Set<HospitalTheatreBlock>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct) : null;
				if (i.Id != null && b == null) return HospitalResult.Fail("not_found");
				if (b == null) _db.Add(b = new HospitalTheatreBlock { CompanyId = ctx.CompanyId });
				b.RoomId = i.RoomId; b.Weekday = i.Weekday; b.StartMinute = i.StartMinute; b.EndMinute = i.EndMinute; b.SurgeonId = i.SurgeonId;
				b.Label = string.IsNullOrWhiteSpace(i.Label) ? null : i.Label.Trim(); b.IsActive = i.IsActive; b.UpdatedAt = nowUtc;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(b.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> SaveCardAsync(BusinessContext ctx, HospitalCardInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct) && !await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.HospitalPractitioners.AnyAsync(p => p.ID == i.SurgeonId && p.CompanyId == ctx.CompanyId, ct)
				|| !await _db.HospitalServices.AnyAsync(s => s.ID == i.ServiceId && s.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (HospitalTheatreBlockRules.CardRefusal(i.Position, i.SkinPrep, i.Gloves, i.Instruments, i.Equipment, i.Sutures, i.Notes) is string why) return HospitalResult.Fail(why);
			static string? C(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(n, s.Trim().Length)];
			try
			{
				var c = await _db.Set<HospitalPreferenceCard>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.SurgeonId == i.SurgeonId && x.ServiceId == i.ServiceId, ct);
				if (c == null) _db.Add(c = new HospitalPreferenceCard { CompanyId = ctx.CompanyId, SurgeonId = i.SurgeonId, ServiceId = i.ServiceId });
				c.Position = C(i.Position, 200); c.SkinPrep = C(i.SkinPrep, 200); c.Gloves = C(i.Gloves, 100); c.Instruments = C(i.Instruments, 1000);
				c.Equipment = C(i.Equipment, 500); c.Sutures = C(i.Sutures, 500); c.Notes = C(i.Notes, 1000); c.UpdatedAt = nowUtc; c.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}
	}
}
