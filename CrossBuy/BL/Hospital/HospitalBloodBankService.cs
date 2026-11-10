using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H41 — the blood bank. The rules are HospitalBloodRules' (no database).
	//   · Units (`lab`): received by bag number, group, product and expiry; an available bag past its expiry is not
	//     on the shelf, and a bag may be discarded with its reason.
	//   · Requests: the doctor asks (`consult`) for a product and a number of units for a patient whose blood group is
	//     on file; the laboratory (`lab`) starts the cross-match and rules it — compatible reserves the units
	//     (compatible by group, the patient's own group first, nearest expiry first) in the same save, incompatible
	//     records why — then issues them. Cancelling (`consult` or `lab`) puts reserved bags back on the shelf.
	//   · The shelf: whole blood and packed cells available per group, low below the safety level.
	// Nothing reaches a bill here yet.
	// ==========================================================================================
	public sealed record HospitalBloodRequestRow(HospitalTransfusionRequest Row, string Patient, string PatientEn, string Mrn, string? Photo, string? BloodGroup,
		string? Place, string? PlaceEn, IReadOnlyList<string> UnitNos);

	public sealed record HospitalBloodBoard(IReadOnlyDictionary<string, int> Shelf, IReadOnlyList<string> Low, IReadOnlyList<HospitalBloodRequestRow> Requests,
		IReadOnlyList<HospitalBloodUnit> Units, int Expired);

	public static class HospitalBloodRules
	{
		/// Below this many whole-blood / packed-cell units a group is low (the prototype's safety level).
		public const int SafetyLevel = 5;

		private static (string Abo, bool Pos) Split(string g) => (g.TrimEnd('+', '-'), g.EndsWith('+'));

		/// Red cells: the donor's cells carry no antigen the recipient lacks (ABO), and Rh-negative patients get Rh-negative.
		public static bool RedCellsFit(string recipient, string donor)
		{
			var (ra, rp) = Split(recipient); var (da, dp) = Split(donor);
			bool abo = da == "O" || da == ra || ra == "AB";
			return abo && (rp || !dp);
		}

		/// Plasma (and platelets): the donor's plasma carries no antibody against the recipient's cells; Rh does not matter.
		public static bool PlasmaFits(string recipient, string donor)
		{
			var (ra, _) = Split(recipient); var (da, _) = Split(donor);
			return da == "AB" || da == ra || ra == "O";
		}

		/// Whether a bag of `product` from `donor` may go to `recipient`. Whole blood carries both, so its group is identical.
		public static bool Fits(string product, string recipient, string donor) => product switch
		{
			HospitalBloodProduct.WholeBlood => recipient == donor,
			HospitalBloodProduct.PackedCells => RedCellsFit(recipient, donor),
			_ => PlasmaFits(recipient, donor),
		};

		public static string? RequestRefusal(string product, int units, string? patientGroup)
		{
			if (!HospitalBloodProduct.All.Contains(product)) return "Choose the blood product";
			if (units < 1 || units > 10) return "Between 1 and 10 units are requested at a time";
			if (!HospitalBloodGroups.IsKnown(patientGroup)) return "Record the patient's blood group on the file first";
			return null;
		}

		public static string? ReceiveRefusal(IReadOnlyList<string> unitNos, string group, string product, DateTime expiresOn, DateTime today)
		{
			if (unitNos.Count == 0) return "Enter at least one bag number";
			if (unitNos.Any(n => n.Length > 40)) return "A bag number is up to 40 characters";
			if (unitNos.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) return "A bag number is entered twice";
			if (!HospitalBloodGroups.IsKnown(group)) return "Choose the blood group";
			if (!HospitalBloodProduct.All.Contains(product)) return "Choose the blood product";
			if (expiresOn.Date < today.Date) return "This bag has already expired";
			return null;
		}

		/// Where a request may go from where it is.
		public static bool CanMove(string from, string to) => (from, to) switch
		{
			(HospitalTransfusionStatus.Requested, HospitalTransfusionStatus.CrossMatching) => true,
			(HospitalTransfusionStatus.CrossMatching, HospitalTransfusionStatus.Compatible) => true,
			(HospitalTransfusionStatus.CrossMatching, HospitalTransfusionStatus.Incompatible) => true,
			(HospitalTransfusionStatus.Compatible, HospitalTransfusionStatus.Issued) => true,
			(HospitalTransfusionStatus.Requested or HospitalTransfusionStatus.CrossMatching or HospitalTransfusionStatus.Compatible, HospitalTransfusionStatus.Cancelled) => true,
			_ => false,
		};

		/// The bags a compatible cross-match reserves: available, unexpired, the product asked, fitting the patient —
		/// the patient's own group first, then the nearest expiry. Null when the shelf holds fewer than asked.
		public static IReadOnlyList<HospitalBloodUnit>? Pick(IEnumerable<HospitalBloodUnit> shelf, string product, string recipient, int units, DateTime today)
		{
			var fit = shelf.Where(u => u.Status == HospitalBloodUnitStatus.Available && u.Product == product && u.ExpiresOn.Date >= today.Date && Fits(product, recipient, u.BloodGroup))
				.OrderBy(u => u.BloodGroup == recipient ? 0 : 1).ThenBy(u => u.ExpiresOn).ThenBy(u => u.ID).Take(units).ToList();
			return fit.Count < units ? null : fit;
		}

		/// The shelf the prototype counts: whole blood and packed cells, available and unexpired, per group.
		public static IReadOnlyDictionary<string, int> Shelf(IEnumerable<HospitalBloodUnit> units, DateTime today)
		{
			var red = units.Where(u => u.Status == HospitalBloodUnitStatus.Available && u.ExpiresOn.Date >= today.Date
				&& (u.Product == HospitalBloodProduct.WholeBlood || u.Product == HospitalBloodProduct.PackedCells)).ToList();
			return HospitalBloodGroups.All.ToDictionary(g => g, g => red.Count(u => u.BloodGroup == g));
		}
	}

	public sealed class HospitalBloodBankService
	{
		public const string NoTables = "The blood bank tables are not created yet (hospital_h41_blood_bank.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;

		private readonly HospitalBillingService? _billing;
		public HospitalBloodBankService(CrossDbContext db, IHospitalAccessService access, HospitalBillingService? billing = null) { _db = db; _access = access; _billing = billing; }

		// ---------------- H51: what a product is charged as ----------------

		/// Product → the catalogue service it is charged as (empty when none is named, or the H51 table is missing).
		public async Task<IReadOnlyDictionary<string, int>> ProductServicesAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try { return await _db.Set<HospitalBloodProductService>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId).ToDictionaryAsync(x => x.Product, x => x.ServiceId, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return new Dictionary<string, int>(); }
		}

		/// Names (or clears, with null) the service each product is charged as. The billing settings (`manage`).
		public async Task<HospitalResult> SaveProductServicesAsync(BusinessContext ctx, IReadOnlyDictionary<string, int?> map, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (map.Keys.Any(k => !HospitalBloodProduct.All.Contains(k))) return HospitalResult.Fail("not_found");
			var ids = map.Values.Where(v => v != null).Select(v => v!.Value).Distinct().ToList();
			if (await _db.HospitalServices.CountAsync(s => s.CompanyId == ctx.CompanyId && ids.Contains(s.ID), ct) != ids.Count) return HospitalResult.Fail("not_found");
			try
			{
				var rows = await _db.Set<HospitalBloodProductService>().Where(x => x.CompanyId == ctx.CompanyId).ToListAsync(ct);
				foreach (var (product, serviceId) in map)
				{
					var row = rows.FirstOrDefault(x => x.Product == product);
					if (serviceId == null) { if (row != null) _db.Remove(row); continue; }
					if (row == null) { row = new HospitalBloodProductService { CompanyId = ctx.CompanyId, Product = product }; _db.Add(row); }
					row.ServiceId = serviceId.Value; row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				}
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(0);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalBloodUnit> Units(BusinessContext ctx) => _db.Set<HospitalBloodUnit>().Where(u => u.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalTransfusionRequest> Requests(BusinessContext ctx) => _db.Set<HospitalTransfusionRequest>().Where(r => r.CompanyId == ctx.CompanyId);
		private static DateTime Today(DateTime nowUtc) => HospitalNursingService.UtcToWard(nowUtc).Date;

		public async Task<(HospitalBloodBoard Board, bool Ready)> BoardAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			var today = Today(nowUtc);
			try
			{
				var onShelf = await Units(ctx).AsNoTracking().Where(u => u.Status == HospitalBloodUnitStatus.Available || u.Status == HospitalBloodUnitStatus.Reserved).OrderBy(u => u.ExpiresOn).ToListAsync(ct);
				var shelf = HospitalBloodRules.Shelf(onShelf, today);
				var low = shelf.Where(s => s.Value < HospitalBloodRules.SafetyLevel).Select(s => s.Key).ToList();
				// the open requests, and those closed in the last two days
				var since = nowUtc.AddDays(-2);
				var reqs = await Requests(ctx).AsNoTracking().Where(r => r.Status == HospitalTransfusionStatus.Requested || r.Status == HospitalTransfusionStatus.CrossMatching || r.Status == HospitalTransfusionStatus.Compatible
					|| r.RequestedAt >= since || r.IssuedAt >= since).OrderByDescending(r => r.ID).Take(50).ToListAsync(ct);
				var rids = reqs.Select(r => r.ID).ToList();
				var bags = await Units(ctx).AsNoTracking().Where(u => u.RequestId != null && rids.Contains(u.RequestId.Value)).Select(u => new { u.RequestId, u.UnitNo }).ToListAsync(ct);
				var pids = reqs.Select(r => r.PatientId).Distinct().ToList();
				var pats = await _db.HospitalPatients.AsNoTracking().Where(p => pids.Contains(p.ID) && p.CompanyId == ctx.CompanyId)
					.Select(p => new { p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath, p.BloodGroup }).ToDictionaryAsync(p => p.ID, ct);
				// where each patient lies now: the ward of their open bed stay (an ICU room says ICU)
				var places = await (from s in _db.HospitalBedStays.AsNoTracking()
									join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
									join w in _db.HospitalWards.AsNoTracking() on r.WardId equals w.ID into ws
									from w in ws.DefaultIfEmpty()
									where s.CompanyId == ctx.CompanyId && s.EndedAt == null && pids.Contains(s.PatientId)
									select new { s.PatientId, r.Kind, Ward = w == null ? null : w.Name, WardEn = w == null ? null : w.NameEn }).ToListAsync(ct);
				var rows = reqs.Where(r => pats.ContainsKey(r.PatientId)).Select(r =>
				{
					var p = pats[r.PatientId]; var pl = places.FirstOrDefault(x => x.PatientId == r.PatientId);
					string? place = pl == null ? null : pl.Kind == HospitalRoomKinds.Icu ? "ICU" : pl.Ward, placeEn = pl == null ? null : pl.Kind == HospitalRoomKinds.Icu ? "ICU" : pl.WardEn ?? pl.Ward;
					return new HospitalBloodRequestRow(r, p.Name, p.NameEn, p.Mrn, p.PhotoPath, p.BloodGroup, place, placeEn, bags.Where(b => b.RequestId == r.ID).Select(b => b.UnitNo).OrderBy(x => x).ToList());
				}).ToList();
				var expired = onShelf.Count(u => u.Status == HospitalBloodUnitStatus.Available && u.ExpiresOn.Date < today);
				return (new HospitalBloodBoard(shelf, low, rows, onShelf, expired), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
			{
				return (new HospitalBloodBoard(HospitalBloodGroups.All.ToDictionary(g => g, _ => 0), Array.Empty<string>(), Array.Empty<HospitalBloodRequestRow>(), Array.Empty<HospitalBloodUnit>(), 0), false);
			}
		}

		public async Task<HospitalResult> ReceiveAsync(BusinessContext ctx, IReadOnlyList<string> unitNos, string group, string product, DateTime expiresOn, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Lab, ct)) return HospitalResult.Fail("forbidden");
			var nos = unitNos.Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
			if (HospitalBloodRules.ReceiveRefusal(nos, group, product, expiresOn, Today(nowUtc)) is string why) return HospitalResult.Fail(why);
			try
			{
				var taken = await Units(ctx).Where(u => nos.Contains(u.UnitNo)).Select(u => u.UnitNo).ToListAsync(ct);
				if (taken.Count > 0) return HospitalResult.Fail("This bag number is already received: {0}", string.Join(", ", taken));
				foreach (var n in nos)
					_db.Add(new HospitalBloodUnit { CompanyId = ctx.CompanyId, UnitNo = n, BloodGroup = group, Product = product, ExpiresOn = expiresOn.Date, Status = HospitalBloodUnitStatus.Available,
						Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(300, note.Trim().Length)], ReceivedAt = nowUtc, ReceivedByEmployeeId = employeeId });
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(nos.Count);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}

		public async Task<HospitalResult> DiscardAsync(BusinessContext ctx, int unitId, string? reason, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Lab, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(reason)) return HospitalResult.Fail("Write why the bag is discarded");
			try
			{
				var u = await Units(ctx).FirstOrDefaultAsync(x => x.ID == unitId, ct);
				if (u == null) return HospitalResult.Fail("not_found");
				if (u.Status != HospitalBloodUnitStatus.Available) return HospitalResult.Fail("Only a bag on the shelf is discarded");
				u.Status = HospitalBloodUnitStatus.Discarded; u.DiscardedAt = nowUtc; u.Note = reason.Trim()[..Math.Min(300, reason.Trim().Length)];
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(u.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> RequestAsync(BusinessContext ctx, int patientId, string product, int units, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == patientId && x.CompanyId == ctx.CompanyId).Select(x => new { x.ID, x.BloodGroup }).FirstOrDefaultAsync(ct);
			if (p == null) return HospitalResult.Fail("not_found");
			if (HospitalBloodRules.RequestRefusal(product, units, p.BloodGroup) is string why) return HospitalResult.Fail(why);
			try
			{
				var r = new HospitalTransfusionRequest { CompanyId = ctx.CompanyId, PatientId = p.ID, Product = product, Units = units, Status = HospitalTransfusionStatus.Requested,
					Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(500, note.Trim().Length)], RequestedAt = nowUtc, RequestedByEmployeeId = employeeId };
				_db.Add(r); await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(r.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// Moves a request on: cross-match started, compatible (reserves the bags), incompatible, issued, cancelled.
		public async Task<HospitalResult> MoveAsync(BusinessContext ctx, int requestId, string to, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			bool lab = await Can(ctx, HospitalActions.Lab, ct);
			if (!lab && !(to == HospitalTransfusionStatus.Cancelled && await Can(ctx, HospitalActions.Consult, ct))) return HospitalResult.Fail("forbidden");
			try
			{
				var r = await Requests(ctx).FirstOrDefaultAsync(x => x.ID == requestId, ct);
				if (r == null) return HospitalResult.Fail("not_found");
				if (!HospitalBloodRules.CanMove(r.Status, to)) return HospitalResult.Fail("This request cannot move to that step from where it is");
				if ((to == HospitalTransfusionStatus.Incompatible || to == HospitalTransfusionStatus.Cancelled) && string.IsNullOrWhiteSpace(reason)) return HospitalResult.Fail("Write the reason");
				var held = await Units(ctx).Where(u => u.RequestId == r.ID && u.Status == HospitalBloodUnitStatus.Reserved).ToListAsync(ct);
				switch (to)
				{
					case HospitalTransfusionStatus.Compatible:
						var group = await _db.HospitalPatients.Where(p => p.ID == r.PatientId).Select(p => p.BloodGroup).FirstAsync(ct);
						if (!HospitalBloodGroups.IsKnown(group)) return HospitalResult.Fail("Record the patient's blood group on the file first");
						var shelf = await Units(ctx).AsNoTracking().Where(u => u.Status == HospitalBloodUnitStatus.Available && u.Product == r.Product).ToListAsync(ct);
						var pick = HospitalBloodRules.Pick(shelf, r.Product, group!, r.Units, Today(nowUtc));
						if (pick == null) return HospitalResult.Fail("Not enough compatible bags on the shelf");
						// each bag is taken only if it is still on the shelf: two cross-matches at once never share a bag
						await using (var tx = await ScopedTx.BeginOrJoinAsync(_db))
						{
							foreach (var u in pick)
								if (await Units(ctx).Where(x => x.ID == u.ID && x.Status == HospitalBloodUnitStatus.Available)
									.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, HospitalBloodUnitStatus.Reserved).SetProperty(x => x.RequestId, (int?)r.ID), ct) != 1)
								{ await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
							r.MatchedAt = nowUtc; r.MatchedByEmployeeId = employeeId; r.Status = to;
							await _db.SaveChangesAsync(ct);
							await tx.CommitAsync();
						}
						return HospitalResult.Success(r.ID);
					case HospitalTransfusionStatus.Incompatible:
						r.MatchedAt = nowUtc; r.MatchedByEmployeeId = employeeId; r.Reason = reason!.Trim()[..Math.Min(300, reason.Trim().Length)];
						break;
					case HospitalTransfusionStatus.Issued:
						foreach (var u in held) { u.Status = HospitalBloodUnitStatus.Issued; u.IssuedAt = nowUtc; }
						r.IssuedAt = nowUtc; r.IssuedByEmployeeId = employeeId;
						// H51: the bags issued, on the patient's bill, in the same save as the issue
						await using (var tx = await ScopedTx.BeginOrJoinAsync(_db))
						{
							if (r.BillLineId == null && held.Count > 0 && _billing != null && (await ProductServicesAsync(ctx, ct)).TryGetValue(r.Product, out var serviceId))
							{
								var now = HospitalNursingService.UtcToWard(nowUtc);
								var bill = await _billing.BillForWardChargeAsync(ctx, r.PatientId, employeeId, now, ct);
								var line = await _billing.AddServiceChargeAsync(ctx, bill, serviceId, employeeId, now, ct, held.Count);
								await _db.SaveChangesAsync(ct);
								r.BillLineId = line?.ID;
							}
							r.Status = to;
							await _db.SaveChangesAsync(ct);
							await tx.CommitAsync();
						}
						return HospitalResult.Success(r.ID);
					case HospitalTransfusionStatus.Cancelled:
						foreach (var u in held) { u.Status = HospitalBloodUnitStatus.Available; u.RequestId = null; }
						r.Reason = reason!.Trim()[..Math.Min(300, reason.Trim().Length)];
						break;
				}
				r.Status = to;
				await _db.SaveChangesAsync(ct);   // the request and its bags in one save
				return HospitalResult.Success(r.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}
	}
}
