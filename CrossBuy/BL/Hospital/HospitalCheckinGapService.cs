using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H159 — check-in gaps (see HospitalCheckinGaps.cs).
	//   · OPEN: from a step the card shows undone, with a reason, an owner (me, a colleague, or nobody yet) and a due date
	//     (today or later). One open gap per patient and step: a second is refused, the first is the one to follow.
	//   · SELF-CLOSING: identity, mobile, photo and consent close by themselves the moment the file shows them done
	//     (the card and the lists settle them as they read); payer, balance and documents are closed by hand.
	//   · CLOSE / WAIVE: by hand, with a word (a waiver must say why); TAKE: an unassigned gap becomes mine.
	//   · OVERDUE: open past its due date. The lists read mine / unassigned / mine-and-unassigned (the desk) / everyone's.
	// ==========================================================================================

	public sealed class HospitalCheckinGapInput
	{
		public int PatientId { get; set; }
		public string? Item { get; set; }
		public string? Reason { get; set; }
		public int? OwnerEmployeeId { get; set; }
		public DateTime? DueDate { get; set; }
	}

	public static class HospitalCheckinGapRules
	{
		public static string? Refuse(HospitalCheckinGapInput i, DateTime todayLocal)
		{
			if (!HospitalCheckinItem.All.Contains(i.Item ?? "")) return "Choose the missing step";
			if (string.IsNullOrWhiteSpace(i.Reason)) return "Write why it could not be finished at the counter";
			if (i.DueDate is not DateTime d) return "Choose the date it must be finished by";
			if (d.Date < todayLocal.Date) return "The due date cannot be in the past";
			if (d.Date > todayLocal.Date.AddDays(90)) return "The due date must be within 90 days";
			return null;
		}

		public static bool Overdue(HospitalCheckinGap g, DateTime todayLocal) => g.Status == HospitalCheckinGapStatus.Open && g.DueDate.Date < todayLocal.Date;

		/// Whether the file itself shows the step done (the self-closing steps only).
		public static bool DoneOnFile(string item, HospitalPatient p, int consents) => item switch
		{
			HospitalCheckinItem.Identity => !string.IsNullOrWhiteSpace(p.NationalId) || p.PhotoPath != null,
			HospitalCheckinItem.Phone => !string.IsNullOrWhiteSpace(p.Phone),
			HospitalCheckinItem.Photo => p.PhotoPath != null,
			HospitalCheckinItem.Consent => consents > 0 || p.ConsentSigned,
			_ => false,
		};
	}

	public sealed record HospitalCheckinGapRow(HospitalCheckinGap G, string PatientName, string? PatientNameEn, string Mrn, string? Owner, string? OpenedBy, bool Overdue);
	public sealed record HospitalCheckinGapCounts(int Open, int Overdue, int Mine, int Unassigned);
	public sealed record HospitalDeskPerson(int Id, string Name);

	public sealed class HospitalCheckinGapService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalCheckinGapService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private IQueryable<HospitalCheckinGap> Rows(BusinessContext ctx) => _db.Set<HospitalCheckinGap>().Where(g => g.CompanyId == ctx.CompanyId);
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number == 208;
		private static string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(n, s.Trim().Length)];

		/// Who a gap may be given to: the company's active people.
		public async Task<IReadOnlyList<HospitalDeskPerson>> PeopleAsync(BusinessContext ctx, CancellationToken ct = default)
			=> (await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId && e.IsActive).Select(e => new { e.ID, e.FullName, e.FullNameEn }).Take(1000).ToListAsync(ct))
				.Select(e => new HospitalDeskPerson(e.ID, DisplayName.Of(e.FullName, e.FullNameEn))).OrderBy(e => e.Name).ToList();

		public async Task<HospitalResult> OpenAsync(BusinessContext ctx, HospitalCheckinGapInput input, int? employeeId, DateTime nowUtc, DateTime todayLocal, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			var why = HospitalCheckinGapRules.Refuse(input, todayLocal);
			if (why != null) return HospitalResult.Fail(why);
			var p = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.PatientId && x.CompanyId == ctx.CompanyId, ct);
			if (p == null) return HospitalResult.Fail("not_found");
			if (input.OwnerEmployeeId is int o && !await _db.Employee.AnyAsync(e => e.ID == o && e.EmpCompanyID == ctx.CompanyId && e.IsActive, ct)) return HospitalResult.Fail("Choose someone from this company");
			int consents = input.Item == HospitalCheckinItem.Consent ? await _db.Set<HospitalConsent>().CountAsync(c => c.CompanyId == ctx.CompanyId && c.PatientId == p.ID, ct) : 0;
			if (HospitalCheckinGapRules.DoneOnFile(input.Item!, p, consents)) return HospitalResult.Fail("This step is already done on the file");
			if (await Rows(ctx).AnyAsync(g => g.PatientId == p.ID && g.Item == input.Item && g.Status == HospitalCheckinGapStatus.Open, ct))
				return HospitalResult.Fail("This step is already being followed for the patient");
			var row = new HospitalCheckinGap
			{
				CompanyId = ctx.CompanyId, PatientId = p.ID, Item = input.Item!, Reason = Clip(input.Reason, 300)!, OwnerEmployeeId = input.OwnerEmployeeId, DueDate = input.DueDate!.Value.Date,
				Status = HospitalCheckinGapStatus.Open, OpenedAt = nowUtc, OpenedByEmployeeId = employeeId,
			};
			_db.Add(row);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		private async Task<HospitalResult> CloseAsync(BusinessContext ctx, int id, string to, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			var g = await Rows(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (g == null) return HospitalResult.Fail("not_found");
			if (g.Status != HospitalCheckinGapStatus.Open) return HospitalResult.Fail("This follow-up is closed already");
			if (to == HospitalCheckinGapStatus.Waived && string.IsNullOrWhiteSpace(note)) return HospitalResult.Fail("Write why the step is waived");
			g.Status = to; g.ClosedAt = nowUtc; g.ClosedByEmployeeId = employeeId; g.CloseNote = Clip(note, 300);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(g.ID);
		}

		public Task<HospitalResult> DoneAsync(BusinessContext ctx, int id, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default) => CloseAsync(ctx, id, HospitalCheckinGapStatus.Done, note, employeeId, nowUtc, ct);
		public Task<HospitalResult> WaiveAsync(BusinessContext ctx, int id, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default) => CloseAsync(ctx, id, HospitalCheckinGapStatus.Waived, note, employeeId, nowUtc, ct);

		/// An unassigned (or someone else's) open gap becomes the caller's.
		public async Task<HospitalResult> TakeAsync(BusinessContext ctx, int id, int employeeId, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			var g = await Rows(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (g == null) return HospitalResult.Fail("not_found");
			if (g.Status != HospitalCheckinGapStatus.Open) return HospitalResult.Fail("This follow-up is closed already");
			g.OwnerEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(g.ID);
		}

		/// Closes the open self-closing gaps the file now shows done (for these patients, or all open ones).
		public async Task<int> SettleAsync(BusinessContext ctx, IReadOnlyCollection<int>? patientIds, DateTime nowUtc, CancellationToken ct = default)
		{
			List<HospitalCheckinGap> open;
			try
			{
				var q = Rows(ctx).Where(g => g.Status == HospitalCheckinGapStatus.Open && HospitalCheckinItem.SelfClosing.Contains(g.Item));
				if (patientIds != null) q = q.Where(g => patientIds.Contains(g.PatientId));
				open = await q.ToListAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return 0; }
			if (open.Count == 0) return 0;
			var ids = open.Select(g => g.PatientId).Distinct().ToList();
			var pats = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && ids.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
			var consents = await _db.Set<HospitalConsent>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && ids.Contains(c.PatientId)).GroupBy(c => c.PatientId)
				.Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
			int n = 0;
			foreach (var g in open)
			{
				if (!pats.TryGetValue(g.PatientId, out var p) || !HospitalCheckinGapRules.DoneOnFile(g.Item, p, consents.GetValueOrDefault(g.PatientId))) continue;
				g.Status = HospitalCheckinGapStatus.Done; g.ClosedAt = nowUtc; g.CloseNote = "auto"; n++;
			}
			if (n > 0) await _db.SaveChangesAsync(ct);
			return n;
		}

		public async Task<HospitalCheckinGapCounts> CountsAsync(BusinessContext ctx, int? me, DateTime todayLocal, CancellationToken ct = default)
		{
			try
			{
				var open = await Rows(ctx).AsNoTracking().Where(g => g.Status == HospitalCheckinGapStatus.Open).Select(g => new { g.OwnerEmployeeId, g.DueDate }).ToListAsync(ct);
				return new HospitalCheckinGapCounts(open.Count, open.Count(g => g.DueDate.Date < todayLocal.Date), open.Count(g => me != null && g.OwnerEmployeeId == me), open.Count(g => g.OwnerEmployeeId == null));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalCheckinGapCounts(0, 0, 0, 0); }
		}

		/// The open gaps of one patient (the card shows them beside its steps).
		public async Task<IReadOnlyList<HospitalCheckinGap>> ForPatientAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
		{
			try { return await Rows(ctx).AsNoTracking().Where(g => g.PatientId == patientId && g.Status == HospitalCheckinGapStatus.Open).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return Array.Empty<HospitalCheckinGap>(); }
		}

		/// whose: "mine" | "unassigned" | anything else = everyone's; state: "open" (default) | "overdue" | "closed" | "all".
		public async Task<(IReadOnlyList<HospitalCheckinGapRow> Rows, int Total)> ListAsync(BusinessContext ctx, string? q, string? whose, string? item, string? state, int? me, int page, int size, DateTime todayLocal, CancellationToken ct = default)
		{
			IQueryable<HospitalCheckinGap> rows;
			try { rows = Rows(ctx).AsNoTracking(); await rows.AnyAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalCheckinGapRow>(), 0); }
			var today = todayLocal.Date;
			rows = state switch
			{
				"overdue" => rows.Where(g => g.Status == HospitalCheckinGapStatus.Open && g.DueDate < today),
				"closed" => rows.Where(g => g.Status != HospitalCheckinGapStatus.Open),
				"all" => rows,
				_ => rows.Where(g => g.Status == HospitalCheckinGapStatus.Open),
			};
			if (whose == "mine") rows = me == null ? rows.Where(g => false) : rows.Where(g => g.OwnerEmployeeId == me);
			else if (whose == "unassigned") rows = rows.Where(g => g.OwnerEmployeeId == null);
			else if (whose == "desk") rows = rows.Where(g => g.OwnerEmployeeId == null || (me != null && g.OwnerEmployeeId == me));   // the desk tab: mine and nobody's
			if (HospitalCheckinItem.All.Contains(item ?? "")) rows = rows.Where(g => g.Item == item);
			var joined = from g in rows
						 join p in _db.HospitalPatients.AsNoTracking() on g.PatientId equals p.ID
						 select new { g, p.Name, p.NameEn, p.Mrn };
			if (!string.IsNullOrWhiteSpace(q))
			{
				var s = q.Trim();
				joined = joined.Where(x => x.Name.Contains(s) || (x.NameEn ?? "").Contains(s) || x.Mrn.Contains(s) || x.g.Reason.Contains(s));
			}
			int total = await joined.CountAsync(ct);
			var page1 = await joined.OrderBy(x => x.g.Status == HospitalCheckinGapStatus.Open ? 0 : 1).ThenBy(x => x.g.DueDate).ThenBy(x => x.g.ID).Skip((page - 1) * size).Take(size).ToListAsync(ct);
			var emp = page1.SelectMany(x => new[] { x.g.OwnerEmployeeId, x.g.OpenedByEmployeeId }).Where(e => e != null).Select(e => e!.Value).Distinct().ToList();
			var names = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId && emp.Contains(e.ID)).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName, e.FullNameEn), ct);
			return (page1.Select(x => new HospitalCheckinGapRow(x.g, x.Name, x.NameEn, x.Mrn,
				x.g.OwnerEmployeeId is int o ? names.GetValueOrDefault(o) : null, x.g.OpenedByEmployeeId is int b ? names.GetValueOrDefault(b) : null,
				HospitalCheckinGapRules.Overdue(x.g, todayLocal))).ToList(), total);
		}
	}
}
