using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H165 — the desk's shift handover (see HospitalDeskHandovers.cs).
	//   · WHAT I HOLD: the queue items I took (H164), the check-in gaps I own (H159), how many of them are overdue, and the
	//     handoffs I sent that are still open (H158, for the note: they stay mine on record).
	//   · CASH FIRST: an open cashier shift of mine must be closed (counted) at the cashier before the handover; the last
	//     shift I closed today is copied in — expected, counted, the difference.
	//   · HAND OVER: to a colleague of the company (never myself); my items and gaps become theirs in one transaction.
	//   · ACKNOWLEDGE: only the receiver, once; until then the desk shows it to them.
	// ==========================================================================================

	public sealed record HospitalHoldings(int QueueItems, int Gaps, int Overdue, int OpenHandoffs, HospitalCashierShift? OpenShift, HospitalCashierShift? ClosedShift);
	public sealed record HospitalHandoverRow(HospitalDeskHandover H, string? From, string? To);

	public sealed class HospitalDeskHandoverService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalDeskHandoverService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number == 208;
		private static async Task<T> Safe<T>(Func<Task<T>> f, T fallback) { try { return await f(); } catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return fallback; } }

		/// Everything the employee holds at the desk now, and their cash shift of today.
		public async Task<HospitalHoldings> HoldingsAsync(BusinessContext ctx, int me, DateTime dayStartUtc, DateTime todayLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			int cid = ctx.CompanyId;
			int queue = await Safe(() => _db.Set<HospitalDeskClaim>().CountAsync(c => c.CompanyId == cid && c.OwnerEmployeeId == me, ct), 0);
			var gaps = await Safe(() => _db.Set<HospitalCheckinGap>().AsNoTracking().Where(g => g.CompanyId == cid && g.OwnerEmployeeId == me && g.Status == HospitalCheckinGapStatus.Open).Select(g => g.DueDate).ToListAsync(ct), new List<DateTime>());
			int handoffs = await Safe(() => _db.Set<HospitalDeskHandoff>().CountAsync(h => h.CompanyId == cid && h.SentByEmployeeId == me && (h.Status == HospitalHandoffStatus.Sent || h.Status == HospitalHandoffStatus.Accepted), ct), 0);
			var open = await _db.HospitalCashierShifts.AsNoTracking().Where(s => s.CompanyId == cid && s.CashierEmployeeId == me && s.Status == HospitalShiftStatus.Open).OrderByDescending(s => s.ID).FirstOrDefaultAsync(ct);
			var closed = await _db.HospitalCashierShifts.AsNoTracking().Where(s => s.CompanyId == cid && s.CashierEmployeeId == me && s.Status == HospitalShiftStatus.Closed && s.ClosedAt >= dayStartUtc).OrderByDescending(s => s.ClosedAt).FirstOrDefaultAsync(ct);
			return new HospitalHoldings(queue, gaps.Count, gaps.Count(d => d.Date < todayLocal.Date), handoffs, open, closed);
		}

		public async Task<HospitalResult> HandOverAsync(BusinessContext ctx, int me, int? to, string? note, DateTime dayStartUtc, DateTime todayLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			if (to is not int target) return HospitalResult.Fail("Choose the colleague who takes over");
			if (target == me) return HospitalResult.Fail("Hand over to a colleague, not to yourself");
			if (!await _db.Employee.AnyAsync(e => e.ID == target && e.EmpCompanyID == ctx.CompanyId && e.IsActive, ct)) return HospitalResult.Fail("Choose someone from this company");
			var h = await HoldingsAsync(ctx, me, dayStartUtc, todayLocal, nowUtc, ct);
			if (h.OpenShift != null) return HospitalResult.Fail("Close your cash shift at the cashier first: count the drawer, then hand over");
			if (h.QueueItems + h.Gaps + h.OpenHandoffs == 0 && h.ClosedShift == null && string.IsNullOrWhiteSpace(note)) return HospitalResult.Fail("Nothing to hand over: write a note if there is something to say");
			var nos = await Safe(() => _db.Set<HospitalDeskHandover>().Where(x => x.CompanyId == ctx.CompanyId).Select(x => x.HandoverNo).ToListAsync(ct), new List<string>());
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			await Safe(() => _db.Set<HospitalDeskClaim>().Where(c => c.CompanyId == ctx.CompanyId && c.OwnerEmployeeId == me)
				.ExecuteUpdateAsync(u => u.SetProperty(c => c.OwnerEmployeeId, target).SetProperty(c => c.ClaimedAt, nowUtc), ct), 0);
			await Safe(() => _db.Set<HospitalCheckinGap>().Where(g => g.CompanyId == ctx.CompanyId && g.OwnerEmployeeId == me && g.Status == HospitalCheckinGapStatus.Open)
				.ExecuteUpdateAsync(u => u.SetProperty(g => g.OwnerEmployeeId, target), ct), 0);
			var row = new HospitalDeskHandover
			{
				CompanyId = ctx.CompanyId, HandoverNo = HospitalDentalPracticeRules.NextNo(nos, "HV-", 6), FromEmployeeId = me, ToEmployeeId = target, At = nowUtc,
				Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(500, note.Trim().Length)],
				QueueItems = h.QueueItems, Gaps = h.Gaps, Overdue = h.Overdue, OpenHandoffs = h.OpenHandoffs,
				ShiftId = h.ClosedShift?.ID, CashExpected = h.ClosedShift?.ExpectedCash, CashCounted = h.ClosedShift?.CountedCash, CashVariance = h.ClosedShift?.Variance,
			};
			_db.Add(row);
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync();
			return HospitalResult.Success(row.ID);
		}

		public async Task<HospitalResult> AcknowledgeAsync(BusinessContext ctx, int id, int me, DateTime nowUtc, CancellationToken ct = default)
		{
			var h = await _db.Set<HospitalDeskHandover>().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
			if (h == null) return HospitalResult.Fail("not_found");
			if (h.ToEmployeeId != me) return HospitalResult.Fail("Only the colleague who takes over acknowledges");
			if (h.AcknowledgedAt != null) return HospitalResult.Fail("This handover is acknowledged already");
			h.AcknowledgedAt = nowUtc;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(h.ID);
		}

		/// Handovers waiting for my acknowledgement, and the latest ones I gave or received.
		public async Task<(IReadOnlyList<HospitalHandoverRow> Pending, IReadOnlyList<HospitalHandoverRow> Recent)> ForMeAsync(BusinessContext ctx, int me, CancellationToken ct = default)
		{
			var rows = await Safe(() => _db.Set<HospitalDeskHandover>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && (x.ToEmployeeId == me || x.FromEmployeeId == me))
				.OrderByDescending(x => x.ID).Take(20).ToListAsync(ct), new List<HospitalDeskHandover>());
			var ids = rows.SelectMany(r => new[] { r.FromEmployeeId, r.ToEmployeeId }).Distinct().ToList();
			var names = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId && ids.Contains(e.ID)).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName, e.FullNameEn), ct);
			var all = rows.Select(r => new HospitalHandoverRow(r, names.GetValueOrDefault(r.FromEmployeeId), names.GetValueOrDefault(r.ToEmployeeId))).ToList();
			return (all.Where(r => r.H.ToEmployeeId == me && r.H.AcknowledgedAt == null).ToList(), all.Take(5).ToList());
		}
	}
}
