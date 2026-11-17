using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H152 — formulary governance (see HospitalFormularyRequests.cs).
	//   · ASK (pharmacy): for a prescription line whose medicine the formulary restricts or leaves out, with a clinical
	//     justification; one open or approved request per line.
	//   · DECIDE (manage — the committee — never the person who asked): approve, or refuse with a note and optionally a
	//     formulary alternative (the therapeutic interchange the prescriber is asked to switch to).
	//   · THE GATE: dispensing refuses a restricted or non-formulary line without an approved request for that line
	//     (HospitalPharmacyService.DispenseAsync asks NeedsApprovalAsync).
	// ==========================================================================================

	public static class HospitalFormularyRules
	{
		public static bool Governed(string? formularyStatus) => formularyStatus is HospitalFormularyStatus.Restricted or HospitalFormularyStatus.NonFormulary;

		public static string? RefuseAsk(string? justification) =>
			string.IsNullOrWhiteSpace(justification) || justification.Trim().Length < 10 ? "Write the clinical reason the patient needs this medicine" : null;

		public static string? RefuseDecision(bool approve, string? note, int? requester, int? decider)
		{
			if (requester != null && requester == decider) return "The person who asked cannot decide the request";
			if (!approve && string.IsNullOrWhiteSpace(note)) return "Write why the committee refuses it";
			return null;
		}
	}

	public sealed record HospitalFormularyRequestRow(HospitalFormularyRequest R, string PatientName, string? PatientNameEn, string Mrn, string ItemCode, string ItemName, string? ItemNameEn,
		string? RxNo, string? AlternativeName, string? RequestedBy, string? DecidedBy);
	public sealed record HospitalFormularyRequestStats(bool Ready, int Pending, int Approved30, int Refused30);

	public sealed class HospitalFormularyGovernanceService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalFormularyGovernanceService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private IQueryable<HospitalFormularyRequest> Requests(BusinessContext ctx) => _db.Set<HospitalFormularyRequest>().Where(r => r.CompanyId == ctx.CompanyId);
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number == 208;

		/// The requests of one dispense, by line (the latest per line), for the dispense card.
		public async Task<Dictionary<int, HospitalFormularyRequest>> ForDispenseAsync(BusinessContext ctx, int dispenseId, CancellationToken ct = default)
		{
			try { return (await Requests(ctx).AsNoTracking().Where(r => r.DispenseId == dispenseId).OrderBy(r => r.ID).ToListAsync(ct)).GroupBy(r => r.LineId).ToDictionary(g => g.Key, g => g.Last()); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new(); }
		}

		/// The lines of a dispense that the formulary governs and that have no approved request: they may not leave.
		public static async Task<List<HospitalDispenseLine>> NeedsApprovalAsync(CrossDbContext db, BusinessContext ctx, int dispenseId, IEnumerable<HospitalDispenseLine> lines, CancellationToken ct)
		{
			var stocked = lines.Where(l => l.ItemId != null).ToList();
			if (stocked.Count == 0) return new();
			var masters = await HospitalDrugMasterService.LookupAsync(db, ctx.CompanyId, stocked.Select(l => l.ItemId!.Value).Distinct().ToList(), ct);
			var governed = stocked.Where(l => HospitalFormularyRules.Governed(masters.GetValueOrDefault(l.ItemId!.Value)?.FormularyStatus)).ToList();
			if (governed.Count == 0) return governed;
			HashSet<int> approved;
			try { approved = (await db.Set<HospitalFormularyRequest>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.DispenseId == dispenseId && r.Status == HospitalFormularyRequestStatus.Approved).Select(r => r.LineId).ToListAsync(ct)).ToHashSet(); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { approved = new(); }
			return governed.Where(l => !approved.Contains(l.ID)).ToList();
		}

		public async Task<HospitalResult> AskAsync(BusinessContext ctx, int dispenseId, int lineId, string? justification, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return HospitalResult.Fail("forbidden");
			var why = HospitalFormularyRules.RefuseAsk(justification);
			if (why != null) return HospitalResult.Fail(why);
			var d = await _db.HospitalDispenses.AsNoTracking().FirstOrDefaultAsync(x => x.ID == dispenseId && x.CompanyId == ctx.CompanyId, ct);
			var l = d == null ? null : await _db.HospitalDispenseLines.AsNoTracking().FirstOrDefaultAsync(x => x.ID == lineId && x.DispenseId == d.ID && x.CompanyId == ctx.CompanyId, ct);
			if (d == null || l?.ItemId is not int itemId) return HospitalResult.Fail("not_found");
			var m = (await HospitalDrugMasterService.LookupAsync(_db, ctx.CompanyId, new[] { itemId }, ct)).GetValueOrDefault(itemId);
			if (!HospitalFormularyRules.Governed(m?.FormularyStatus)) return HospitalResult.Fail("This medicine is on the formulary: no approval is needed");
			if (await Requests(ctx).AnyAsync(r => r.LineId == l.ID && r.DispenseId == d.ID && r.Status != HospitalFormularyRequestStatus.Refused, ct))
				return HospitalResult.Fail("This medicine already has a request for this prescription");
			var issued = await Requests(ctx).Select(r => r.RequestNo).ToListAsync(ct);
			int max = 0; foreach (var n in issued) if (n.StartsWith("FR-") && int.TryParse(n.AsSpan(3), out var v) && v > max) max = v;
			var row = new HospitalFormularyRequest
			{
				CompanyId = ctx.CompanyId, RequestNo = "FR-" + (max + 1).ToString("000000"), PatientId = d.PatientId, DispenseId = d.ID, LineId = l.ID, ItemId = itemId,
				FormularyStatus = m!.FormularyStatus, Justification = justification!.Trim()[..Math.Min(500, justification.Trim().Length)], RequestedAt = nowUtc, RequestedByEmployeeId = employeeId,
			};
			_db.Add(row);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		public async Task<HospitalResult> DecideAsync(BusinessContext ctx, int id, bool approve, string? note, int? alternativeItemId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			var r = await Requests(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (r.Status != HospitalFormularyRequestStatus.Pending) return HospitalResult.Fail("This request was already decided");
			var why = HospitalFormularyRules.RefuseDecision(approve, note, r.RequestedByEmployeeId, employeeId);
			if (why != null) return HospitalResult.Fail(why);
			if (!approve && alternativeItemId is int alt)
			{
				var am = (await HospitalDrugMasterService.LookupAsync(_db, ctx.CompanyId, new[] { alt }, ct)).GetValueOrDefault(alt);
				if (am == null || am.FormularyStatus != HospitalFormularyStatus.Formulary || alt == r.ItemId) return HospitalResult.Fail("The alternative must be a formulary medicine");
			}
			r.Status = approve ? HospitalFormularyRequestStatus.Approved : HospitalFormularyRequestStatus.Refused;
			r.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(500, note.Trim().Length)];
			r.AlternativeItemId = approve ? null : alternativeItemId;
			r.DecidedAt = nowUtc; r.DecidedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(r.ID);
		}

		public async Task<HospitalFormularyRequestStats> StatsAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var since = nowUtc.AddDays(-30);
				var q = Requests(ctx).AsNoTracking();
				return new HospitalFormularyRequestStats(true, await q.CountAsync(r => r.Status == HospitalFormularyRequestStatus.Pending, ct),
					await q.CountAsync(r => r.Status == HospitalFormularyRequestStatus.Approved && r.DecidedAt >= since, ct),
					await q.CountAsync(r => r.Status == HospitalFormularyRequestStatus.Refused && r.DecidedAt >= since, ct));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalFormularyRequestStats(false, 0, 0, 0); }
		}

		public async Task<(IReadOnlyList<HospitalFormularyRequestRow> Rows, int Total)> ListAsync(BusinessContext ctx, string? q, string? status, int page, int size, CancellationToken ct = default)
		{
			var rows = from r in Requests(ctx).AsNoTracking()
					   join p in _db.HospitalPatients.AsNoTracking() on r.PatientId equals p.ID
					   join i in _db.Items.AsNoTracking() on r.ItemId equals i.ID
					   join d in _db.HospitalDispenses.AsNoTracking() on r.DispenseId equals d.ID
					   where p.CompanyId == ctx.CompanyId && i.CompanyID == ctx.CompanyId
					   select new { r, p.Name, p.NameEn, p.Mrn, i.ItemCode, ItemName = i.Name, ItemNameEn = i.NameEn, d.RxNo };
			if (HospitalFormularyRequestStatus.All.Contains(status ?? "")) rows = rows.Where(x => x.r.Status == status);
			if (!string.IsNullOrWhiteSpace(q))
			{
				var s = q.Trim();
				rows = rows.Where(x => x.r.RequestNo.Contains(s) || x.Name.Contains(s) || (x.NameEn ?? "").Contains(s) || x.Mrn.Contains(s) || x.ItemCode.Contains(s) || x.ItemName.Contains(s) || x.RxNo.Contains(s));
			}
			int total = await rows.CountAsync(ct);
			// the ones waiting first, then the newest
			var page1 = await rows.OrderBy(x => x.r.Status == HospitalFormularyRequestStatus.Pending ? 0 : 1).ThenByDescending(x => x.r.ID).Skip((page - 1) * size).Take(size).ToListAsync(ct);
			var altIds = page1.Where(x => x.r.AlternativeItemId != null).Select(x => x.r.AlternativeItemId!.Value).Distinct().ToList();
			var alts = await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && altIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => DisplayName.Of(i.Name, i.NameEn), ct);
			var empIds = page1.SelectMany(x => new[] { x.r.RequestedByEmployeeId, x.r.DecidedByEmployeeId }).Where(e => e != null).Select(e => e!.Value).Distinct().ToList();
			var emps = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId && empIds.Contains(e.ID)).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName, e.FullNameEn), ct);
			return (page1.Select(x => new HospitalFormularyRequestRow(x.r, x.Name, x.NameEn, x.Mrn, x.ItemCode, x.ItemName, x.ItemNameEn, x.RxNo,
				x.r.AlternativeItemId is int a ? alts.GetValueOrDefault(a) : null,
				x.r.RequestedByEmployeeId is int rq ? emps.GetValueOrDefault(rq) : null, x.r.DecidedByEmployeeId is int dc ? emps.GetValueOrDefault(dc) : null)).ToList(), total);
		}

		/// The formulary medicines a refusal may name as the alternative (search box of the decision dialog).
		public async Task<List<(int Id, string Name)>> AlternativesAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var ids = await _db.Set<HospitalDrug>().AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && m.FormularyStatus == HospitalFormularyStatus.Formulary).Select(m => m.ItemId).ToListAsync(ct);
			return (await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && ids.Contains(i.ID)).OrderBy(i => i.ItemCode).Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn }).ToListAsync(ct))
				.Select(i => (i.ID, i.ItemCode + " · " + DisplayName.Of(i.Name, i.NameEn))).ToList();
		}
	}
}
