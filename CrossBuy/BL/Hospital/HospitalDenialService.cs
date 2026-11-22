using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed class HospitalDenialInput
	{
		public string? RootCause { get; set; }
		public string? Status { get; set; }
		public string? Note { get; set; }
		public DateTime? DueDate { get; set; }
		/// Take the item: the caller becomes its owner.
		public bool Take { get; set; }
	}

	public sealed record HospitalDenialRow(HospitalDenialWork Work, HospitalClaimLine Line, string BatchNo, int BatchId, string Payer, string PayerEn,
		string PatientName, string? PatientNameEn, string? Photo, HospitalFace? Owner, bool Overdue, bool Paid);

	public sealed record HospitalDenialQueue(bool Ready, IReadOnlyList<HospitalDenialRow> Rows, int Total, int Open, int Overdue, decimal OpenAmount,
		IReadOnlyList<HospitalDenialCauseStat> ByCause, IReadOnlyList<(string Payer, string PayerEn, int Count, decimal Amount)> ByPayer);

	// ==========================================================================================
	// HOSPITAL H240 — the denial work-queue. Every rejected claim line gets its work item when the queue is read (once:
	// the line is unique); a line resubmitted from the claim batch closes its item as resubmitted. The biller (`bill`)
	// reads and works the queue. Nothing here touches the claim's money or the ledger.
	// ==========================================================================================
	public sealed class HospitalDenialService
	{
		public const string NoTables = "The denial work-queue is not installed yet (hospital_h240_denials.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalDenialService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static DateTime Ward(DateTime utc) => HospitalNursingService.UtcToWard(utc);

		/// Opens an item for each rejected line without one, and closes as resubmitted the items whose line went out again.
		private async Task SyncAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct)
		{
			var missing = await (from l in _db.Set<HospitalClaimLine>().AsNoTracking()
								 join c in _db.Set<HospitalClaim>().AsNoTracking() on l.ClaimId equals c.ID
								 where l.CompanyId == ctx.CompanyId && (l.Status == HospitalClaimLineStatus.Rejected || l.Status == HospitalClaimLineStatus.Resubmitted)
									&& !_db.Set<HospitalDenialWork>().Any(w => w.CompanyId == ctx.CompanyId && w.ClaimLineId == l.ID)
								 select new { l.ID, c.PatientId, l.MissingPreauth, l.RejectCode, l.RejectReason, l.DecidedAt, l.Status }).Take(500).ToListAsync(ct);
			foreach (var m in missing)
				_db.Add(new HospitalDenialWork
				{
					CompanyId = ctx.CompanyId, ClaimLineId = m.ID, PatientId = m.PatientId, RootCause = HospitalDenialRules.SuggestCause(m.MissingPreauth, m.RejectCode, m.RejectReason),
					DueDate = HospitalDenialRules.DueDate(Ward(m.DecidedAt ?? nowUtc), null),
					Status = m.Status == HospitalClaimLineStatus.Resubmitted ? HospitalDenialStatus.Resubmitted : HospitalDenialStatus.Open, CreatedAt = nowUtc, UpdatedAt = nowUtc,
				});
			if (missing.Count > 0)
				try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException) { _db.ChangeTracker.Clear(); }   // a parallel reader opened them first
			await (from w in _db.Set<HospitalDenialWork>()
				   where w.CompanyId == ctx.CompanyId && (w.Status == HospitalDenialStatus.Open || w.Status == HospitalDenialStatus.Working)
					  && _db.Set<HospitalClaimLine>().Any(l => l.ID == w.ClaimLineId && l.Status == HospitalClaimLineStatus.Resubmitted)
				   select w).ExecuteUpdateAsync(u => u.SetProperty(w => w.Status, HospitalDenialStatus.Resubmitted).SetProperty(w => w.UpdatedAt, nowUtc), ct);
		}

		public async Task<HospitalDenialQueue?> QueueAsync(BusinessContext ctx, string? q, string? view, string? cause, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Bill, null, ct)) return null;
			try { await SyncAsync(ctx, nowUtc, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208)
			{
				return new HospitalDenialQueue(false, Array.Empty<HospitalDenialRow>(), 0, 0, 0, 0, Array.Empty<HospitalDenialCauseStat>(), Array.Empty<(string, string, int, decimal)>());
			}
			var today = Ward(nowUtc).Date;
			var all = await (from w in _db.Set<HospitalDenialWork>().AsNoTracking()
							 join l in _db.Set<HospitalClaimLine>().AsNoTracking() on w.ClaimLineId equals l.ID
							 join c in _db.Set<HospitalClaim>().AsNoTracking() on l.ClaimId equals c.ID
							 join b in _db.Set<HospitalClaimBatch>().AsNoTracking() on c.BatchId equals b.ID
							 join py in _db.Set<HospitalPayer>().AsNoTracking() on b.PayerId equals py.ID
							 join p in _db.HospitalPatients.AsNoTracking() on c.PatientId equals p.ID
							 where w.CompanyId == ctx.CompanyId
							 select new { w, l, b.BatchNo, BatchId = b.ID, Payer = py.Name, PayerEn = py.NameEn, p.Name, p.NameEn, p.PhotoPath, p.Mrn }).ToListAsync(ct);
			// a resubmitted line was paid when its resubmission was accepted
			var lineIds = all.Select(a => (int?)a.l.ID).ToList();
			var paid = (await _db.Set<HospitalClaimLine>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.ResubmitsLineId != null && lineIds.Contains(x.ResubmitsLineId) && x.Status == HospitalClaimLineStatus.Accepted)
				.Select(x => x.ResubmitsLineId!.Value).ToListAsync(ct)).ToHashSet();

			var active = all.Where(a => HospitalDenialStatus.Active(a.w.Status)).ToList();
			var byCause = HospitalDenialRules.ByCause(all.Select(a => (a.w.RootCause, a.l.InsurerAmount, a.w.Status, paid.Contains(a.l.ID))));
			var byPayer = active.GroupBy(a => (a.Payer, a.PayerEn)).Select(g => (g.Key.Payer, g.Key.PayerEn, g.Count(), g.Sum(a => a.l.InsurerAmount))).OrderByDescending(x => x.Item4).Take(8).ToList();

			var list = all.AsEnumerable();
			list = view switch
			{
				"overdue" => list.Where(a => HospitalDenialRules.Overdue(a.w.Status, a.w.DueDate, today)),
				"closed" => list.Where(a => !HospitalDenialStatus.Active(a.w.Status)),
				"mine" => list.Where(a => HospitalDenialStatus.Active(a.w.Status) && a.w.OwnerEmployeeId == ctx.EmployeeId),
				_ => list.Where(a => HospitalDenialStatus.Active(a.w.Status)),
			};
			if (HospitalDenialCause.All.Contains(cause ?? "")) list = list.Where(a => a.w.RootCause == cause);
			if (!string.IsNullOrWhiteSpace(q))
			{
				var t = q.Trim();
				list = list.Where(a => a.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || (a.NameEn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || (a.Mrn ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
					|| a.l.Code.Contains(t, StringComparison.OrdinalIgnoreCase) || a.BatchNo.Contains(t, StringComparison.OrdinalIgnoreCase) || (a.l.RejectCode ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
					|| a.Payer.Contains(t, StringComparison.OrdinalIgnoreCase) || a.PayerEn.Contains(t, StringComparison.OrdinalIgnoreCase));
			}
			var ordered = list.OrderBy(a => HospitalDenialStatus.Active(a.w.Status) ? 0 : 1).ThenBy(a => a.w.DueDate).ThenByDescending(a => a.l.InsurerAmount).ToList();
			var pageRows = ordered.Skip((Math.Max(1, page) - 1) * size).Take(size).ToList();
			var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, pageRows.Select(a => a.w.OwnerEmployeeId), ct);
			var rows = pageRows.Select(a => new HospitalDenialRow(a.w, a.l, a.BatchNo, a.BatchId, a.Payer, a.PayerEn, a.Name, a.NameEn, a.PhotoPath,
				a.w.OwnerEmployeeId is int o ? faces.GetValueOrDefault(o) : null, HospitalDenialRules.Overdue(a.w.Status, a.w.DueDate, today), paid.Contains(a.l.ID))).ToList();
			return new HospitalDenialQueue(true, rows, ordered.Count, active.Count, active.Count(a => HospitalDenialRules.Overdue(a.w.Status, a.w.DueDate, today)),
				active.Sum(a => a.l.InsurerAmount), byCause, byPayer);
		}

		public async Task<HospitalResult> UpdateAsync(BusinessContext ctx, int id, HospitalDenialInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Bill, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var w = await _db.Set<HospitalDenialWork>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
				if (w == null) return HospitalResult.Fail("not_found");
				if (HospitalDenialRules.UpdateRefusal(w.Status, i.RootCause, i.Status, i.Note) is string why) return HospitalResult.Fail(why);
				if (i.DueDate is DateTime d && (d.Date < w.CreatedAt.Date.AddDays(-1) || d.Date > Ward(nowUtc).Date.AddDays(365))) return HospitalResult.Fail("The deadline is within the next year");
				if (i.RootCause != null) w.RootCause = i.RootCause;
				if (i.Status != null) w.Status = i.Status;
				if (i.DueDate is DateTime dd) w.DueDate = dd.Date;
				if (i.Take) w.OwnerEmployeeId = employeeId;
				if (i.Note != null) w.Note = string.IsNullOrWhiteSpace(i.Note) ? null : i.Note.Trim();
				if (w.Status == HospitalDenialStatus.Open && (i.Take || i.Note != null)) w.Status = HospitalDenialStatus.Working;
				w.UpdatedAt = nowUtc; w.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(w.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}
	}
}
