using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H39 — the controlled-drugs register. The rules are HospitalControlledRules' (no database). A ward's
	// register is READ from its store: what came in (the requisitions' transfers), what left, the balance; a dose or a
	// waste is WRITTEN with two signatures — the nurse (the session) and a witness who types her own password on the
	// same screen (owner ruling) — and leaves the store through IStockService.PostMovementAsync in the same transaction,
	// so the register and the stock never disagree. A wrong witness password records nothing.
	// ==========================================================================================
	public sealed record HospitalControlledLine(int ItemId, string Code, string Name, string NameEn, decimal Open, decimal In, decimal Given, decimal Wasted, decimal Balance);
	public sealed record HospitalControlledEntryRow(HospitalControlledEntry Entry, string Item, string ItemEn, string? Patient, string? PatientEn, string? Mrn,
		string Nurse, string NurseEn, string? NursePhoto, string Witness, string WitnessEn, string? WitnessPhoto);

	/// Confirms a witness: her own password, checked like a sign-in (no lockout, as the sign-in itself).
	public interface IHospitalWitness { Task<bool> VerifyAsync(int companyId, int employeeId, string password, CancellationToken ct = default); }

	public sealed class HospitalPasswordWitness(CrossDbContext db, UserManager<Users> users) : IHospitalWitness
	{
		public async Task<bool> VerifyAsync(int companyId, int employeeId, string password, CancellationToken ct = default)
		{
			if (string.IsNullOrEmpty(password)) return false;
			var userId = await db.Employee.AsNoTracking().Where(e => e.ID == employeeId && e.EmpCompanyID == companyId && e.IsActive).Select(e => e.UserId).FirstOrDefaultAsync(ct);
			if (string.IsNullOrEmpty(userId)) return false;
			var user = await users.FindByIdAsync(userId);
			return user != null && await users.CheckPasswordAsync(user, password);
		}
	}

	public static class HospitalControlledRules
	{
		public const string MovementSource = "HospitalControlled";

		/// Why this entry cannot be recorded (before the witness is asked), or null.
		public static string? RecordRefusal(string kind, decimal qty, int? patientId, string? note, int? nurse, int witness)
		{
			if (!HospitalControlledKind.All.Contains(kind)) return "Choose a dose or a waste";
			if (qty <= 0) return "A quantity is more than zero";
			if (kind == HospitalControlledKind.Given && patientId == null) return "Choose the patient the dose was given to";
			if (kind == HospitalControlledKind.Wasted && string.IsNullOrWhiteSpace(note)) return "Write why it was wasted";
			if (nurse is not int n || n <= 0) return "Only a signed-in nurse records the register";
			if (witness <= 0) return "Choose the witness";
			if (witness == n) return "The witness is a second person, not the nurse who records";
			return null;
		}

		/// The opening balance of the period: what is there now, less what came in, plus what left since it began.
		public static decimal Open(decimal balance, decimal inSince, decimal outSince) => balance - inSince + outSince;
	}

	public sealed class HospitalControlledService
	{
		public const string NoTables = "The controlled-drugs tables are not created yet (hospital_h39_controlled_drugs.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IStockService _stock;
		private readonly IHospitalWitness _witness;

		private readonly HospitalBillingService? _billing;   // H52: the dose given to a patient goes on their bill
		public HospitalControlledService(CrossDbContext db, IHospitalAccessService access, IStockService stock, IHospitalWitness witness, HospitalBillingService? billing = null)
		{ _db = db; _access = access; _stock = stock; _witness = witness; _billing = billing; }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);

		/// The ward stores a register is kept for: the supply units' own stores.
		public async Task<IReadOnlyList<(int WarehouseId, string Code, string Name, string NameEn)>> WardStoresAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try
			{
				var ids = await _db.Set<HospitalSupplyUnit>().AsNoTracking().Where(u => u.CompanyId == ctx.CompanyId && u.IsActive).Select(u => u.StoreWarehouseId).Distinct().ToListAsync(ct);
				var rows = await _db.Warehouses.AsNoTracking().Where(w => ids.Contains(w.ID) && w.CompanyID == ctx.CompanyId).OrderBy(w => w.Code).Select(w => new { w.ID, w.Code, w.Name, w.NameEn }).ToListAsync(ct);
				return rows.Select(r => (r.ID, r.Code, r.Name, r.NameEn ?? r.Name)).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return Array.Empty<(int, string, string, string)>(); }
		}

		public async Task<IReadOnlyList<(int Id, string Code, string Name, string NameEn)>> ControlledItemsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try
			{
				var ids = _db.Set<HospitalControlledItem>().Where(c => c.CompanyId == ctx.CompanyId).Select(c => c.ItemId);
				var rows = await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && ids.Contains(i.ID)).OrderBy(i => i.ItemCode).Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn }).ToListAsync(ct);
				return rows.Select(r => (r.ID, r.ItemCode, r.Name, r.NameEn ?? r.Name)).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return Array.Empty<(int, string, string, string)>(); }
		}

		/// A ward's register since `fromUtc`: per controlled drug, open / in / given / wasted / balance, and the entries.
		public async Task<(IReadOnlyList<HospitalControlledLine> Lines, IReadOnlyList<HospitalControlledEntryRow> Entries, bool Ready)> RegisterAsync(BusinessContext ctx, int warehouseId, DateTime fromUtc, CancellationToken ct = default)
		{
			try
			{
				var items = await ControlledItemsAsync(ctx, ct);
				var ids = items.Select(i => i.Id).ToList();
				var bal = await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && b.WarehouseId == warehouseId && ids.Contains(b.ItemId))
					.GroupBy(b => b.ItemId).Select(g => new { g.Key, Q = g.Sum(x => x.QtyOnHand) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);
				var moves = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == ctx.CompanyId && m.WarehouseId == warehouseId && ids.Contains(m.ItemId) && m.CreatedAt >= fromUtc)
					.GroupBy(m => new { m.ItemId, m.Direction }).Select(g => new { g.Key.ItemId, g.Key.Direction, Q = g.Sum(x => x.QtyBase) }).ToListAsync(ct);
				var entries = await _db.Set<HospitalControlledEntry>().AsNoTracking().Where(e => e.CompanyId == ctx.CompanyId && e.WarehouseId == warehouseId && e.RecordedAt >= fromUtc)
					.OrderByDescending(e => e.ID).ToListAsync(ct);
				var lines = items.Select(i =>
				{
					decimal inQ = moves.Where(m => m.ItemId == i.Id && m.Direction == 1).Sum(m => m.Q), outQ = moves.Where(m => m.ItemId == i.Id && m.Direction == -1).Sum(m => m.Q);
					decimal b = bal.GetValueOrDefault(i.Id);
					return new HospitalControlledLine(i.Id, i.Code, i.Name, i.NameEn, HospitalControlledRules.Open(b, inQ, outQ), inQ,
						entries.Where(e => e.ItemId == i.Id && e.Kind == HospitalControlledKind.Given).Sum(e => e.Qty),
						entries.Where(e => e.ItemId == i.Id && e.Kind == HospitalControlledKind.Wasted).Sum(e => e.Qty), b);
				}).Where(l => l.Open != 0 || l.In != 0 || l.Balance != 0 || l.Given != 0 || l.Wasted != 0).ToList();
				// the entries, with who signed (their photos) and the patient
				var emp = entries.SelectMany(e => new[] { e.RecordedByEmployeeId, e.WitnessEmployeeId }).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => emp.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, ct);
				var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, emp, ct);
				var pids = entries.Where(e => e.PatientId != null).Select(e => e.PatientId!.Value).Distinct().ToList();
				var pats = await _db.HospitalPatients.AsNoTracking().Where(p => pids.Contains(p.ID) && p.CompanyId == ctx.CompanyId).Select(p => new { p.ID, p.Name, p.NameEn, p.Mrn }).ToDictionaryAsync(p => p.ID, ct);
				var rows = entries.Take(50).Select(e =>
				{
					var it = items.FirstOrDefault(i => i.Id == e.ItemId); var n = people.GetValueOrDefault(e.RecordedByEmployeeId); var w = people.GetValueOrDefault(e.WitnessEmployeeId);
					var p = e.PatientId is int pid ? pats.GetValueOrDefault(pid) : null;
					return new HospitalControlledEntryRow(e, it.Name ?? "", it.NameEn ?? it.Name ?? "", p?.Name, p?.NameEn, p?.Mrn,
						n?.FullName ?? "", n?.FullNameEn ?? n?.FullName ?? "", photos.GetValueOrDefault(e.RecordedByEmployeeId),
						w?.FullName ?? "", w?.FullNameEn ?? w?.FullName ?? "", photos.GetValueOrDefault(e.WitnessEmployeeId));
				}).ToList();
				return (lines, rows, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return (Array.Empty<HospitalControlledLine>(), Array.Empty<HospitalControlledEntryRow>(), false); }
		}

		/// Marks an item controlled or not (`manage`).
		public async Task<HospitalResult> SetControlledAsync(BusinessContext ctx, int itemId, bool on, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.Items.AnyAsync(i => i.ID == itemId && i.CompanyID == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			try
			{
				var row = await _db.Set<HospitalControlledItem>().FirstOrDefaultAsync(c => c.CompanyId == ctx.CompanyId && c.ItemId == itemId, ct);
				if (on && row == null) _db.Add(new HospitalControlledItem { CompanyId = ctx.CompanyId, ItemId = itemId, CreatedAt = nowUtc });
				if (!on && row != null) _db.Remove(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(itemId);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// A dose given or an amount wasted, signed by the nurse (`admit`, the ward) and a witness whose password is
		/// checked here; it leaves the ward store in the same transaction.
		public async Task<HospitalResult> RecordAsync(BusinessContext ctx, int warehouseId, int itemId, string kind, decimal qty, int? patientId, string? note,
			int witnessEmployeeId, string? witnessPassword, string? userId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct) && !await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalControlledRules.RecordRefusal(kind, qty, patientId, note, ctx.EmployeeId, witnessEmployeeId) is string why) return HospitalResult.Fail(why);
			if (!(await WardStoresAsync(ctx, ct)).Any(w => w.WarehouseId == warehouseId)) return HospitalResult.Fail("not_found");
			if (!(await ControlledItemsAsync(ctx, ct)).Any(i => i.Id == itemId)) return HospitalResult.Fail("This item is not a controlled drug");
			if (patientId is int pid && !await _db.HospitalPatients.AnyAsync(p => p.ID == pid && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			// the second signature: the witness's own password, on this screen
			if (!await _witness.VerifyAsync(ctx.CompanyId, witnessEmployeeId, witnessPassword ?? "", ct)) return HospitalResult.Fail("The witness's password is not correct");
			var cat = await _db.Items.AsNoTracking().Where(i => i.ID == itemId && i.CompanyID == ctx.CompanyId).Select(i => i.ItemCategoryId).FirstAsync(ct);
			var accounts = await _db.ItemCategories.AsNoTracking().Where(c => c.ID == cat).Select(c => new { c.CogsAccountId, c.AdjustmentAccountId }).FirstAsync(ct);
			int? counter = kind == HospitalControlledKind.Given ? accounts.CogsAccountId : accounts.AdjustmentAccountId ?? accounts.CogsAccountId;
			if (counter == null) return HospitalResult.Fail("Map the cost-of-goods account of this drug's inventory category first");

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var e = new HospitalControlledEntry { CompanyId = ctx.CompanyId, WarehouseId = warehouseId, ItemId = itemId, Kind = kind, Qty = qty, PatientId = kind == HospitalControlledKind.Given ? patientId : null,
				Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(300, note.Trim().Length)], RecordedByEmployeeId = ctx.EmployeeId!.Value, WitnessEmployeeId = witnessEmployeeId, RecordedAt = nowUtc };
			try { _db.Add(e); await _db.SaveChangesAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			var (ok, error, _) = await _stock.PostMovementAsync(ctx.CompanyId, new MovementRequest
			{
				Date = nowUtc, ItemId = itemId, WarehouseId = warehouseId, Direction = -1, Qty = qty, SourceType = HospitalControlledRules.MovementSource, SourceId = e.ID,
				PostToGl = true, CounterAccountOverride = counter, Notes = "Controlled drug " + kind.ToLowerInvariant(),
			}, userId);
			if (!ok) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("The ward store: {0}", error ?? ""); }
			// H52: a dose given to a patient goes on their bill, priced as the pharmacy prices the item, in the same transaction
			if (kind == HospitalControlledKind.Given && e.PatientId is int charged && _billing != null)
			{
				var now = HospitalNursingService.UtcToWard(nowUtc);
				var item = await _db.Items.AsNoTracking().Where(i => i.ID == itemId && i.CompanyID == ctx.CompanyId).Select(i => new { i.ItemCode, i.Name, i.NameEn }).FirstAsync(ct);
				var bill = await _billing.BillForWardChargeAsync(ctx, charged, ctx.EmployeeId, now, ct);
				var line = await _billing.AddItemChargeAsync(ctx, bill, itemId, item.ItemCode ?? "", item.Name ?? "", item.NameEn ?? item.Name ?? "", qty, ctx.EmployeeId, now, ct);
				await _db.SaveChangesAsync(ct);
				e.BillLineId = line.ID;
				await _db.SaveChangesAsync(ct);
			}
			await tx.CommitAsync();
			return HospitalResult.Success(e.ID);
		}
	}
}
