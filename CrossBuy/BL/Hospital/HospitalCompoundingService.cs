using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H153 — compounding (see HospitalCompoundings.cs).
	//   · WHAT MUST BE COMPOUNDED: a chemotherapy cycle's dose given by injection (its line in the cycle's pharmacy request,
	//     matched to the cycle's dose in order; anything but PO). Any other prescription line may be prepared as an IV
	//     admixture or TPN by choice.
	//   · RECORD (pharmacy): one live record per line (a rejected one is made again); the beyond-use time is the hours the
	//     pharmacist sets, never more than the cap for its storage (room 12 h, fridge 24 h); the drug batch, when written,
	//     must be one the pharmacy store holds for that medicine.
	//   · CHECK (pharmacy, another pharmacist): release, or reject with the reason; a preparation past its beyond-use time
	//     is never released.
	//   · THE GATE: dispensing refuses a must-compound line without a released, still-in-date record.
	// ==========================================================================================

	public sealed class HospitalCompoundInput
	{
		public string? Kind { get; set; }
		public string? Diluent { get; set; }
		public decimal? FinalVolumeMl { get; set; }
		public string? DrugBatch { get; set; }
		public string? DiluentBatch { get; set; }
		public string? Storage { get; set; }
		public int BudHours { get; set; }
		public string? Notes { get; set; }
	}

	public static class HospitalCompoundRules
	{
		/// USP <797> category 1 compounded sterile preparation: 12 h at controlled room temperature, 24 h refrigerated.
		public static int CapHours(string storage) => storage == HospitalCompoundStorage.Fridge ? 24 : 12;

		public static string? RefuseRecord(HospitalCompoundInput i)
		{
			if (!HospitalCompoundStorage.All.Contains(i.Storage ?? "")) return "Choose how the preparation is kept";
			if (i.BudHours <= 0) return "Set the beyond-use time in hours";
			if (i.BudHours > CapHours(i.Storage!)) return "The beyond-use time is longer than its storage allows ({0} h)";
			if (i.FinalVolumeMl is <= 0) return "The final volume must be above zero";
			if (string.IsNullOrWhiteSpace(i.Diluent) && i.Kind != HospitalCompoundKind.TPN) return "Write the diluent";
			return null;
		}

		public static string? RefuseCheck(bool release, string? reason, int? preparer, int? checker, DateTime beyondUseAt, DateTime nowUtc)
		{
			if (preparer != null && preparer == checker) return "The pharmacist who prepared it cannot check it";
			if (!release && string.IsNullOrWhiteSpace(reason)) return "Write why the preparation is rejected";
			if (release && beyondUseAt <= nowUtc) return "The preparation is past its beyond-use time: reject it and make it again";
			return null;
		}

		/// A line's route from the cycle's dose: parenteral (anything but oral) must be compounded.
		public static bool MustCompound(string? route) => route != null && route != HospitalChemoRoute.Oral;
	}

	public sealed record HospitalCompoundTask(int DispenseId, string RxNo, int LineId, string Product, int PatientId, string PatientName, string? PatientNameEn, string Mrn,
		decimal? DoseMg, string? Route, bool Required, HospitalCompounding? Record, string? PreparedBy, string? CheckedBy);
	public sealed record HospitalCompoundStats(bool Ready, int ToMake, int ToCheck, int ReleasedToday);

	public sealed class HospitalCompoundingService
	{
		public static class Filter { public const string ToMake = "make", ToCheck = "check", Released = "released"; }
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalPharmacyService _pharmacy;
		public HospitalCompoundingService(CrossDbContext db, IHospitalAccessService access, HospitalPharmacyService pharmacy) { _db = db; _access = access; _pharmacy = pharmacy; }

		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number == 208;
		private IQueryable<HospitalCompounding> Records(BusinessContext ctx) => _db.Set<HospitalCompounding>().Where(r => r.CompanyId == ctx.CompanyId);

		/// The must-compound lines of a chemotherapy request: line → (dose mg, route), matched to the cycle's doses in order.
		public static async Task<Dictionary<int, (decimal DoseMg, string Route)>> RequiredAsync(CrossDbContext db, BusinessContext ctx, HospitalDispense d, IReadOnlyList<HospitalDispenseLine> lines, CancellationToken ct)
		{
			var result = new Dictionary<int, (decimal, string)>();
			if (d.Source != HospitalDispenseSource.Oncology) return result;
			try
			{
				var cycle = await db.Set<HospitalChemoCycle>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && c.DispenseId == d.ID).Select(c => (int?)c.ID).FirstOrDefaultAsync(ct);
				if (cycle == null) return result;
				var doses = await db.Set<HospitalChemoDose>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.CycleId == cycle).OrderBy(x => x.ID).ToListAsync(ct);
				var ordered = lines.OrderBy(l => l.SortOrder).ThenBy(l => l.ID).ToList();
				for (int i = 0; i < ordered.Count && i < doses.Count; i++)
					if (HospitalCompoundRules.MustCompound(doses[i].Route)) result[ordered[i].ID] = (doses[i].DoseMg, doses[i].Route);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			return result;
		}

		/// The must-compound lines being given now that have no released, in-date record: they may not leave.
		public static async Task<List<HospitalDispenseLine>> NotReleasedAsync(CrossDbContext db, BusinessContext ctx, HospitalDispense d, IReadOnlyList<HospitalDispenseLine> all, IEnumerable<HospitalDispenseLine> givingNow, DateTime nowUtc, CancellationToken ct)
		{
			var required = await RequiredAsync(db, ctx, d, all, ct);
			var need = givingNow.Where(l => required.ContainsKey(l.ID)).ToList();
			if (need.Count == 0) return need;
			HashSet<int> released;
			try { released = (await db.Set<HospitalCompounding>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.DispenseId == d.ID && r.Status == HospitalCompoundStatus.Released && r.BeyondUseAt > nowUtc).Select(r => r.LineId).ToListAsync(ct)).ToHashSet(); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { released = new(); }
			return need.Where(l => !released.Contains(l.ID)).ToList();
		}

		/// The latest preparation of each line of a dispense (for the dispense card).
		public async Task<Dictionary<int, HospitalCompounding>> LatestByLineAsync(BusinessContext ctx, int dispenseId, CancellationToken ct = default)
		{
			try { return (await Records(ctx).AsNoTracking().Where(r => r.DispenseId == dispenseId).OrderBy(r => r.ID).ToListAsync(ct)).GroupBy(r => r.LineId).ToDictionary(g => g.Key, g => g.Last()); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new(); }
		}

		private async Task<List<HospitalCompoundTask>> TasksAsync(BusinessContext ctx, CancellationToken ct)
		{
			var open = await _db.HospitalDispenses.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.Source == HospitalDispenseSource.Oncology
				&& (d.Status == HospitalDispenseStatus.Verified || d.Status == HospitalDispenseStatus.Partial)).ToListAsync(ct);
			List<HospitalCompounding> recs;
			try { recs = await Records(ctx).AsNoTracking().ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new(); }
			var ids = open.Select(d => d.ID).Concat(recs.Select(r => r.DispenseId)).Distinct().ToList();
			var disp = await _db.HospitalDispenses.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && ids.Contains(d.ID)).ToDictionaryAsync(d => d.ID, ct);
			var lines = await _db.HospitalDispenseLines.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && ids.Contains(l.DispenseId)).ToListAsync(ct);
			// the prescription's own words in both languages: the list shows the reader's
			var writtenIds = lines.Where(l => l.PrescriptionLineId != null).Select(l => l.PrescriptionLineId!.Value).ToList();
			var written = await _db.HospitalPrescriptionLines.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && writtenIds.Contains(x.ID)).ToDictionaryAsync(x => x.ID, x => x.DrugEn, ct);
			string Shown(HospitalDispenseLine l) => DisplayName.Of(l.Drug, l.PrescriptionLineId is int pl ? written.GetValueOrDefault(pl) : null);
			var patIds = disp.Values.Select(d => d.PatientId).Distinct().ToList();
			var pats = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && patIds.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
			var empIds = recs.SelectMany(r => new[] { r.PreparedByEmployeeId, r.CheckedByEmployeeId }).Where(e => e != null).Select(e => e!.Value).Distinct().ToList();
			var emps = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId && empIds.Contains(e.ID)).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName, e.FullNameEn), ct);
			var tasks = new List<HospitalCompoundTask>();
			foreach (var d in disp.Values)
			{
				if (!pats.TryGetValue(d.PatientId, out var p)) continue;
				var dl = lines.Where(l => l.DispenseId == d.ID).ToList();
				var required = await RequiredAsync(_db, ctx, d, dl, ct);
				foreach (var l in dl)
				{
					var rec = recs.Where(r => r.DispenseId == d.ID && r.LineId == l.ID).OrderByDescending(r => r.ID).FirstOrDefault();
					bool req = required.TryGetValue(l.ID, out var dose);
					if (!req && rec == null) continue;   // not to be compounded, and not chosen to be
					if (rec == null && !open.Any(o => o.ID == d.ID)) continue;   // a request no longer open with nothing made
					tasks.Add(new HospitalCompoundTask(d.ID, d.RxNo, l.ID, Shown(l), p.ID, p.Name, p.NameEn, p.Mrn, req ? dose.DoseMg : rec?.DoseMg, req ? dose.Route : rec?.Route, req, rec,
						rec?.PreparedByEmployeeId is int pb ? emps.GetValueOrDefault(pb) : null, rec?.CheckedByEmployeeId is int cb ? emps.GetValueOrDefault(cb) : null));
				}
			}
			return tasks;
		}

		private static bool ToMake(HospitalCompoundTask t) => t.Record == null || t.Record.Status == HospitalCompoundStatus.Rejected;

		public async Task<HospitalCompoundStats> StatsAsync(BusinessContext ctx, DateTime dayStartUtc, CancellationToken ct = default)
		{
			try { await Records(ctx).AnyAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new HospitalCompoundStats(false, 0, 0, 0); }
			var t = await TasksAsync(ctx, ct);
			return new HospitalCompoundStats(true, t.Count(ToMake), t.Count(x => x.Record?.Status == HospitalCompoundStatus.Prepared),
				t.Count(x => x.Record?.Status == HospitalCompoundStatus.Released && x.Record.CheckedAt >= dayStartUtc));
		}

		public async Task<(IReadOnlyList<HospitalCompoundTask> Rows, int Total)> ListAsync(BusinessContext ctx, string? q, string? filter, int page, int size, CancellationToken ct = default)
		{
			IEnumerable<HospitalCompoundTask> t = await TasksAsync(ctx, ct);
			if (filter == Filter.ToMake) t = t.Where(ToMake);
			else if (filter == Filter.ToCheck) t = t.Where(x => x.Record?.Status == HospitalCompoundStatus.Prepared);
			else if (filter == Filter.Released) t = t.Where(x => x.Record?.Status == HospitalCompoundStatus.Released);
			if (!string.IsNullOrWhiteSpace(q))
			{
				var s = q.Trim();
				t = t.Where(x => x.RxNo.Contains(s, StringComparison.OrdinalIgnoreCase) || x.Product.Contains(s, StringComparison.OrdinalIgnoreCase) || x.PatientName.Contains(s, StringComparison.OrdinalIgnoreCase)
					|| (x.PatientNameEn ?? "").Contains(s, StringComparison.OrdinalIgnoreCase) || x.Mrn.Contains(s, StringComparison.OrdinalIgnoreCase) || (x.Record?.CompoundNo ?? "").Contains(s, StringComparison.OrdinalIgnoreCase));
			}
			// to make first, then to check, then the rest; the newest request first within each
			var list = t.OrderBy(x => ToMake(x) ? 0 : x.Record!.Status == HospitalCompoundStatus.Prepared ? 1 : 2).ThenByDescending(x => x.DispenseId).ThenBy(x => x.LineId).ToList();
			return (list.Skip((page - 1) * size).Take(size).ToList(), list.Count);
		}

		public async Task<HospitalResult> RecordAsync(BusinessContext ctx, int dispenseId, int lineId, HospitalCompoundInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return HospitalResult.Fail("forbidden");
			var d = await _db.HospitalDispenses.AsNoTracking().FirstOrDefaultAsync(x => x.ID == dispenseId && x.CompanyId == ctx.CompanyId, ct);
			if (d == null) return HospitalResult.Fail("not_found");
			if (!HospitalDispenseStatus.Dispensable(d.Status)) return HospitalResult.Fail("Review the prescription before dispensing");
			var lines = await _db.HospitalDispenseLines.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.DispenseId == d.ID).ToListAsync(ct);
			var l = lines.FirstOrDefault(x => x.ID == lineId);
			if (l == null) return HospitalResult.Fail("not_found");
			var required = await RequiredAsync(_db, ctx, d, lines, ct);
			bool req = required.TryGetValue(l.ID, out var dose);
			input.Kind = req ? HospitalCompoundKind.Chemo : input.Kind;
			if (!HospitalCompoundKind.All.Contains(input.Kind ?? "")) return HospitalResult.Fail("Choose what is prepared");
			var why = HospitalCompoundRules.RefuseRecord(input);
			if (why != null) return HospitalResult.Fail(why, HospitalCompoundRules.CapHours(input.Storage ?? HospitalCompoundStorage.Room));
			if (await Records(ctx).AnyAsync(r => r.DispenseId == d.ID && r.LineId == l.ID && r.Status != HospitalCompoundStatus.Rejected, ct))
				return HospitalResult.Fail("This line already has a preparation");
			var batch = string.IsNullOrWhiteSpace(input.DrugBatch) ? null : input.DrugBatch.Trim();
			if (batch != null && l.ItemId is int itemId)
			{
				var settings = await _pharmacy.SettingsAsync(ctx, ct);
				if (settings.WarehouseId is int wh && !(await _pharmacy.BatchesAsync(ctx, wh, new[] { itemId }, ct)).Any(b => b.BatchNo == batch))
					return HospitalResult.Fail("The pharmacy store holds no batch {0} of this medicine", batch);
			}
			var issued = await Records(ctx).Select(r => r.CompoundNo).ToListAsync(ct);
			int max = 0; foreach (var n in issued) if (n.StartsWith("CMP-") && int.TryParse(n.AsSpan(4), out var v) && v > max) max = v;
			string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(n, s.Trim().Length)];
			var row = new HospitalCompounding
			{
				CompanyId = ctx.CompanyId, CompoundNo = "CMP-" + (max + 1).ToString("000000"), Kind = input.Kind!, PatientId = d.PatientId, DispenseId = d.ID, LineId = l.ID,
				Product = l.Drug[..Math.Min(200, l.Drug.Length)], DoseMg = req ? dose.DoseMg : null, Route = req ? dose.Route : HospitalChemoRoute.IV,
				Diluent = Clip(input.Diluent, 100), FinalVolumeMl = input.FinalVolumeMl, DrugBatch = Clip(batch, 40), DiluentBatch = Clip(input.DiluentBatch, 40),
				Storage = input.Storage!, BeyondUseAt = nowUtc.AddHours(input.BudHours), Notes = Clip(input.Notes, 300), PreparedAt = nowUtc, PreparedByEmployeeId = employeeId,
			};
			_db.Add(row);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		public async Task<HospitalResult> CheckAsync(BusinessContext ctx, int id, bool release, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return HospitalResult.Fail("forbidden");
			var r = await Records(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (r.Status != HospitalCompoundStatus.Prepared) return HospitalResult.Fail("This preparation was already checked");
			var why = HospitalCompoundRules.RefuseCheck(release, reason, r.PreparedByEmployeeId, employeeId, r.BeyondUseAt, nowUtc);
			if (why != null) return HospitalResult.Fail(why);
			r.Status = release ? HospitalCompoundStatus.Released : HospitalCompoundStatus.Rejected;
			r.RejectReason = release ? null : reason!.Trim()[..Math.Min(300, reason.Trim().Length)];
			r.CheckedAt = nowUtc; r.CheckedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(r.ID);
		}
	}
}
