using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H123 — a treatment unit's settings, in one place: the service one completed session is charged as (chosen
	// from the services list, priced by the bill's price list — never a hidden code the price-keeper must know), and, for a
	// unit whose sessions consume a kit (H122), the kit and the store it draws from. The dialysis unit, the oncology day
	// unit and physiotherapy each open the same settings from their own page.
	// ==========================================================================================
	public sealed record HospitalUnitChoice(int Id, string Code, string Name, string NameEn, decimal? Price = null);

	public sealed record HospitalUnitSettingsView(string Unit, bool Ready, bool CanManage, int? ServiceId, int? KitItemId, int? StoreWarehouseId,
		IReadOnlyList<HospitalUnitChoice> Services, IReadOnlyList<HospitalUnitChoice> Kits, IReadOnlyList<HospitalUnitChoice> Stores)
	{
		public bool WithKit => HospitalKitUnits.WithKit.Contains(Unit);
		public HospitalUnitChoice? Service => Services.FirstOrDefault(s => s.Id == ServiceId);
	}

	public sealed class HospitalUnitSettingsInput
	{
		public int? ServiceId { get; set; }
		public int? KitItemId { get; set; }
		public int? WarehouseId { get; set; }
	}

	public sealed class HospitalUnitSettingsService
	{
		public const string NoTables = "The unit settings are not created yet (hospital_h122_unit_kits.sql, hospital_h123_unit_settings.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalUnitSettingsService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;

		/// The unit's row, or null (none saved yet, or the tables are not there).
		public static async Task<HospitalUnitKit?> RowAsync(CrossDbContext db, int companyId, string unit, CancellationToken ct)
		{
			try { return await db.Set<HospitalUnitKit>().AsNoTracking().FirstOrDefaultAsync(k => k.CompanyId == companyId && k.Unit == unit, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}

		/// What the settings modal shows: the current choices and what may be chosen.
		public async Task<HospitalUnitSettingsView> ViewAsync(BusinessContext ctx, string unit, CancellationToken ct = default)
		{
			bool can = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			HospitalUnitKit? row; bool ready = true;
			try { row = await _db.Set<HospitalUnitKit>().AsNoTracking().FirstOrDefaultAsync(k => k.CompanyId == ctx.CompanyId && k.Unit == unit, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { row = null; ready = false; }
			var services = (await _db.HospitalServices.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.IsActive).OrderBy(s => s.Code)
				.Select(s => new { s.ID, s.Code, s.Name, s.NameEn, s.Price }).ToListAsync(ct)).Select(s => new HospitalUnitChoice(s.ID, s.Code, s.Name, s.NameEn, s.Price)).ToList();
			var kits = !HospitalKitUnits.WithKit.Contains(unit) ? new List<HospitalUnitChoice>()
				: (await HospitalKitStock.KitItemsAsync(_db, ctx.CompanyId, ct)).Select(k => new HospitalUnitChoice(k.Id, k.Code, k.Name, k.NameEn)).ToList();
			var stores = !HospitalKitUnits.WithKit.Contains(unit) ? new List<HospitalUnitChoice>()
				: (await _db.Warehouses.AsNoTracking().Where(w => w.CompanyID == ctx.CompanyId && w.IsActive).OrderBy(w => w.Code)
					.Select(w => new { w.ID, w.Code, w.Name, w.NameEn }).ToListAsync(ct)).Select(w => new HospitalUnitChoice(w.ID, w.Code, w.Name, w.NameEn ?? w.Name)).ToList();
			return new HospitalUnitSettingsView(unit, ready, can, row?.SessionServiceId, row?.KitItemId, row?.StoreWarehouseId, services, kits, stores);
		}

		/// Saves the unit's settings (`manage`); a null clears that one. A unit without a kit keeps none.
		public async Task<HospitalResult> SaveAsync(BusinessContext ctx, string unit, HospitalUnitSettingsInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalKitUnits.All.Contains(unit)) return HospitalResult.Fail("not_found");
			bool withKit = HospitalKitUnits.WithKit.Contains(unit);
			int? kit = withKit ? input.KitItemId : null, store = withKit ? input.WarehouseId : null;
			if (input.ServiceId is int s && !await _db.HospitalServices.AnyAsync(x => x.ID == s && x.CompanyId == ctx.CompanyId && x.IsActive, ct)) return HospitalResult.Fail("Choose the session's service from the services list");
			if (kit is int k && !(await HospitalKitStock.KitItemsAsync(_db, ctx.CompanyId, ct)).Any(x => x.Id == k)) return HospitalResult.Fail("Choose a kit item that has a bill of materials");
			if (store is int w && !await _db.Warehouses.AnyAsync(x => x.ID == w && x.CompanyID == ctx.CompanyId && x.IsActive, ct)) return HospitalResult.Fail("not_found");
			try
			{
				var row = await _db.Set<HospitalUnitKit>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.Unit == unit, ct)
					?? _db.Set<HospitalUnitKit>().Add(new HospitalUnitKit { CompanyId = ctx.CompanyId, Unit = unit }).Entity;
				row.SessionServiceId = input.ServiceId; row.KitItemId = kit; row.StoreWarehouseId = store; row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}
	}
}
