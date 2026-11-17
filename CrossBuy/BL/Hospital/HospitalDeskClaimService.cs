using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H164 — ownership on the desk's queues (see HospitalDeskClaims.cs):
	//   · TAKE an item (it becomes mine, even from a colleague — the desk hands work over aloud) or RELEASE it (mine only).
	//   · A queue is read as MINE, UNASSIGNED or ALL, with its counts: total, mine, unassigned, overdue.
	//   · OVERDUE is the queue's own clock: a portal request waiting over 4 hours, a waitlist offer expiring within the
	//     hour, a self-intake form over 30 minutes (10 when urgent), a pre-authorisation over a day, an unidentified
	//     patient over a day. A pair of look-alike files is backlog, never overdue.
	// ==========================================================================================

	public sealed record HospitalQueueCounts(int Total, int Mine, int Unassigned, int Overdue);
	public sealed record HospitalQueueItem(HospitalDeskWorkItem Item, int? OwnerId, string? Owner, bool Overdue);

	public static class HospitalDeskQueueRules
	{
		public static readonly IReadOnlyList<string> Owned = new[] { "portal", "waitlist", "intake", "preauth", "unidentified", "dup" };

		public static bool Overdue(HospitalDeskWorkItem w, DateTime nowUtc) => w.Kind switch
		{
			"portal" => w.At is DateTime a && (nowUtc - a).TotalHours > 4,
			"waitlist" => w.Due is DateTime d && (d - nowUtc).TotalMinutes < 60,
			"intake" => w.At is DateTime a && (nowUtc - a).TotalMinutes > (w.Urgent ? 10 : 30),
			"preauth" => w.At is DateTime a && (nowUtc - a).TotalHours > 24,
			"unidentified" => w.At is DateTime a && (nowUtc - a).TotalHours > 24,
			_ => false,
		};
	}

	public sealed class HospitalDeskClaimService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalDeskClaimService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number == 208;
		private IQueryable<HospitalDeskClaim> Rows(BusinessContext ctx) => _db.Set<HospitalDeskClaim>().Where(c => c.CompanyId == ctx.CompanyId);

		public async Task<HospitalResult> TakeAsync(BusinessContext ctx, string? kind, string? key, int employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalDeskQueueRules.Owned.Contains(kind ?? "") || string.IsNullOrWhiteSpace(key) || key.Length > 40) return HospitalResult.Fail("not_found");
			var row = await Rows(ctx).FirstOrDefaultAsync(c => c.Kind == kind && c.ItemKey == key, ct);
			if (row?.OwnerEmployeeId == employeeId) return HospitalResult.Fail("This item is yours already");
			if (row == null) { row = new HospitalDeskClaim { CompanyId = ctx.CompanyId, Kind = kind!, ItemKey = key }; _db.Add(row); }
			row.OwnerEmployeeId = employeeId; row.ClaimedAt = nowUtc; row.ReleasedAt = null;
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail("Someone took it a moment ago: refresh the list"); }
			return HospitalResult.Success(row.ID);
		}

		public async Task<HospitalResult> ReleaseAsync(BusinessContext ctx, string? kind, string? key, int employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			var row = await Rows(ctx).FirstOrDefaultAsync(c => c.Kind == kind && c.ItemKey == key, ct);
			if (row?.OwnerEmployeeId != employeeId) return HospitalResult.Fail("Only the one who took an item releases it");
			row.OwnerEmployeeId = null; row.ReleasedAt = nowUtc;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		/// The queue as the desk reads it: every item with its owner and lateness, the counts, and the `whose` slice.
		public async Task<(IReadOnlyList<HospitalQueueItem> Items, HospitalQueueCounts Counts)> QueueAsync(BusinessContext ctx, IReadOnlyList<HospitalDeskWorkItem> all, string? whose, int? me, DateTime nowUtc, int take, CancellationToken ct = default)
		{
			var kinds = all.Select(w => w.Kind).Distinct().ToList(); var keys = all.Where(w => w.Key != null).Select(w => w.Key!).ToList();
			var owners = new Dictionary<(string, string), int>();
			try
			{
				owners = (await Rows(ctx).AsNoTracking().Where(c => kinds.Contains(c.Kind) && keys.Contains(c.ItemKey) && c.OwnerEmployeeId != null).Select(c => new { c.Kind, c.ItemKey, c.OwnerEmployeeId }).ToListAsync(ct))
					.ToDictionary(c => (c.Kind, c.ItemKey), c => c.OwnerEmployeeId!.Value);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			var ids = owners.Values.Distinct().ToList();
			var names = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId && ids.Contains(e.ID)).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName, e.FullNameEn), ct);
			var items = all.Select(w =>
			{
				int? o = w.Key != null && owners.TryGetValue((w.Kind, w.Key), out var x) ? x : null;
				return new HospitalQueueItem(w, o, o is int oid ? names.GetValueOrDefault(oid) : null, HospitalDeskQueueRules.Overdue(w, nowUtc));
			}).ToList();
			var counts = new HospitalQueueCounts(items.Count, items.Count(i => me != null && i.OwnerId == me), items.Count(i => i.OwnerId == null), items.Count(i => i.Overdue));
			IEnumerable<HospitalQueueItem> slice = whose switch
			{
				"mine" => items.Where(i => me != null && i.OwnerId == me),
				"unassigned" => items.Where(i => i.OwnerId == null),
				_ => items,
			};
			return (slice.OrderByDescending(i => i.Overdue).Take(take).ToList(), counts);
		}
	}
}
