using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
	// Hospital H10 against a REAL SQL Server (the inventory's stock writer locks rows with SQL Server hints):
	// the slice runs twice; a dispense takes its stock through IStockService nearest expiry first (an expired
	// batch never leaves), books the cost Dr cost-of-goods / Cr inventory, and charges the patient's bill;
	// and a dispense that cannot be covered in full leaves NOTHING behind — no movement, no charge, no state.
	// Skipped without CROSSBUY_TEST_SQL.
	[Collection(SqlServerCollection.Name)]
	public class HospitalPharmacySqlTests : IAsyncLifetime
	{
		private readonly SqlServerFixture _sql;
		private SqlServerFixture.ProbeDatabase? _probe;
		public HospitalPharmacySqlTests(SqlServerFixture sql) { _sql = sql; }

		public async Task InitializeAsync()
		{
			if (!_sql.Available) return;
			_probe = await _sql.CreateProbeDatabaseAsync("HospitalPharmacy");
		}

		public async Task DisposeAsync()
		{
			if (_probe != null) await _sql.DropProbeDatabaseAsync(_probe);
		}

		private static BusinessContext Ctx() => new() { CompanyId = 1, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		/// Records what the stock writer posts to the ledger.
		private sealed class Journals : IJournalEntryService
		{
			public readonly List<JournalEntryInput> Posted = new();
			public Task<(bool ok, string? error, JournalEntry? entry)> CreateAndPostNoTxAsync(JournalEntryInput input, int? userId) { Posted.Add(input); return Task.FromResult<(bool, string?, JournalEntry?)>((true, null, null)); }
			public Task<(bool ok, string? error, JournalEntry? entry)> CreateDraftAsync(JournalEntryInput input, int? userId) => throw new NotSupportedException();
			public Task<(bool ok, string? error)> PostAsync(int entryId, int? userId) => throw new NotSupportedException();
			public Task<(bool ok, string? error, JournalEntry? entry)> CreateAndPostAsync(JournalEntryInput input, int? userId) => throw new NotSupportedException();
			public Task<(bool ok, string? error, int? reversalId)> ReverseAsync(int entryId, int? userId, string? reason) => throw new NotSupportedException();
			public Task<(bool ok, string? error)> UpdateDraftAsync(int entryId, int companyId, JournalEntryInput input, int? userId) => throw new NotSupportedException();
			public Task<(bool ok, string? error)> DeleteDraftAsync(int entryId, int companyId, int? userId) => throw new NotSupportedException();
		}

		private sealed class ThreeDecimals : ICurrencyRounding
		{
			public Task<int> DecimalsAsync(int companyId, int? currencyId, int? branchId = null) => Task.FromResult(3);
			public Task<decimal> RoundAsync(int companyId, decimal value, int? currencyId, int? branchId = null) => Task.FromResult(Math.Round(value, 3, MidpointRounding.AwayFromZero));
		}

		private sealed class NoLedger : IHospitalLedger
		{
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
				=> Task.FromResult<(bool, string?, int?)>((true, null, 1));
		}

		private sealed record Floor(int Para, int Amox, int Store, int Cogs, int Inventory, int Patient);

		/// A KWD company with an open period; two medicines (expiry-tracked) in a mapped category with its
		/// accounts; the pharmacy store holding paracetamol in three batches — expired, near, far — and 2 amoxicillin.
		private async Task<(Floor F, CrossBuy.Models.Context.CrossDbContext Db, IStockService Stock, Journals J)> ArrangeAsync()
		{
			var db = _sql.ContextFor(_probe!, 1);
			var kwd = new Currency { Code = "KWD", Name = "دينار", NameEn = "Dinar", DecimalPlaces = 3 };
			db.Currencies.Add(kwd); await db.SaveChangesAsync();
			var year = new FiscalYear { CompanyID = 1, Name = "2026", StartDate = DateTime.Today.AddDays(-30), EndDate = DateTime.Today.AddDays(30), Status = "Open" };
			db.FiscalYears.Add(year); await db.SaveChangesAsync();
			db.FiscalPeriods.Add(new FiscalPeriod { FiscalYearId = year.ID, PeriodNo = 1, StartDate = year.StartDate, EndDate = year.EndDate, Status = "Open" });
			Account A(string code) => new() { CompanyID = 1, Code = code, Name = code, NameEn = code, AccountTypeId = 1, IsPostable = true, IsActive = true };
			var inv = A("110401"); var cogs = A("510301"); var adj = A("510399");
			db.Accounts.AddRange(inv, cogs, adj); await db.SaveChangesAsync();
			var cat = new ItemCategory { CompanyID = 1, Code = "MED-DRUG", Name = "أدوية", NameEn = "Medicines", InventoryAccountId = inv.ID, CogsAccountId = cogs.ID, AdjustmentAccountId = adj.ID };
			db.ItemCategories.Add(cat); await db.SaveChangesAsync();
			Item Med(string code, decimal price) => new() { CompanyID = 1, ItemCode = code, Barcode = code, Name = code, NameEn = code, ItemCategoryId = cat.ID, ItemType = "Stockable", BaseUoMId = 1, SalesPrice = price, TrackExpiry = true, IsActive = true, CreatedAt = DateTime.UtcNow };
			var para = Med("MED-PARA", 0.75m); var amox = Med("MED-AMOX", 2.1m);
			var store = new Warehouse { CompanyID = 1, Code = "WH-PH", Name = "صيدلية", NameEn = "Pharmacy", IsActive = true };
			db.AddRange(para, amox, store); await db.SaveChangesAsync();
			db.HospitalItemCategories.Add(new HospitalItemCategory { CompanyId = 1, ItemCategoryId = cat.ID, Kind = HospitalPriceGroups.Medicine });
			db.HospitalPharmacySettings.Add(new HospitalPharmacySettings { CompanyId = 1, WarehouseId = store.ID, NearExpiryDays = 60 });
			var patient = new HospitalPatient { CompanyId = 1, Mrn = "MRN-100001", Name = "م", NameEn = "P", Sex = "M", CreatedAt = DateTime.UtcNow };
			db.HospitalPatients.Add(patient); await db.SaveChangesAsync();

			var journals = new Journals();
			IStockService stock = new StockService(db, journals, new FiscalPeriodService(db), new CurrencyService(db), NullLogger<StockService>.Instance, new ThreeDecimals());
			async Task In(int item, string batch, DateTime expiry, decimal qty)
			{
				var (ok, error, _) = await stock.PostMovementAsync(1, new MovementRequest { ItemId = item, WarehouseId = store.ID, Direction = 1, Qty = qty, UnitCostInBase = 0.4m, BatchNo = batch, Expiry = expiry,
					SourceType = "Adjustment", PostToGl = false, AllowExpired = true }, "seed");
				Assert.True(ok, error);
			}
			await In(para.ID, "P-OLD", DateTime.Today.AddDays(-5), 50);   // expired: must never leave
			await In(para.ID, "P-NEAR", DateTime.Today.AddDays(20), 5);
			await In(para.ID, "P-FAR", DateTime.Today.AddDays(300), 10);
			await In(amox.ID, "A-1", DateTime.Today.AddDays(200), 2);
			db.ChangeTracker.Clear();
			return (new Floor(para.ID, amox.ID, store.ID, cogs.ID, inv.ID, patient.ID), db, stock, journals);
		}

		private static HospitalPharmacyService Pharmacy(CrossBuy.Models.Context.CrossDbContext db, IStockService stock)
		{
			var access = new HospitalAccessService(new PlatformRoleDirectory(db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var pricing = new HospitalPricingService(db, access, new CurrencyService(db));
			return new HospitalPharmacyService(db, access, stock, new HospitalBillingService(db, access, new NoLedger(), pricing));
		}

		private static async Task<int> VerifiedRxAsync(CrossBuy.Models.Context.CrossDbContext db, int patient, params (int? Item, string Drug, decimal Qty)[] lines)
		{
			var d = new HospitalDispense { CompanyId = 1, RxNo = "RX-" + Guid.NewGuid().ToString("N")[..6], PatientId = patient, Status = HospitalDispenseStatus.Verified, PrescribedAt = DateTime.UtcNow, VerifiedAt = DateTime.UtcNow };
			db.HospitalDispenses.Add(d); await db.SaveChangesAsync();
			int order = 0;
			foreach (var l in lines) db.HospitalDispenseLines.Add(new HospitalDispenseLine { CompanyId = 1, DispenseId = d.ID, ItemId = l.Item, Drug = l.Drug, Quantity = l.Qty, SortOrder = ++order });
			await db.SaveChangesAsync(); db.ChangeTracker.Clear();
			return d.ID;
		}

		[SkippableFact]
		public async Task The_slice_runs_twice()
		{
			Skip.If(!_sql.Available, _sql.SkipReason);
			await using var c = new SqlConnection(_probe!.ConnectionString);
			await c.OpenAsync();
			// The probe carries the model's schema; the slice must find its own tables and change nothing on a second run.
			await _sql.RunScriptAsync(c, "hospital_h10_pharmacy.sql");
			await _sql.RunScriptAsync(c, "hospital_h10_pharmacy.sql");
			await using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_HospitalBillLines_Source' AND definition LIKE '%pharmacy%'", c);
			Assert.Equal(1, (int)(await cmd.ExecuteScalarAsync())!);
		}

		[SkippableFact]
		public async Task A_dispense_leaves_nearest_expiry_first_books_cost_of_goods_and_charges_the_bill()
		{
			Skip.If(!_sql.Available, _sql.SkipReason);
			var (f, db, stock, journals) = await ArrangeAsync();
			var id = await VerifiedRxAsync(db, f.Patient, (f.Para, "Paracetamol", 8m), (null, "Vitamin D (not stocked)", 1m));
			var r = await Pharmacy(db, stock).DispenseAsync(Ctx(), id, 7, "u7", DateTime.UtcNow);
			Assert.True(r.Ok, r.Error + " " + string.Join(",", r.Args ?? Array.Empty<object>()));

			await using var check = _sql.ContextFor(_probe!, 1);
			var d = await check.HospitalDispenses.AsNoTracking().SingleAsync(x => x.ID == id);
			Assert.Equal((HospitalDispenseStatus.Dispensed, (int?)f.Store), (d.Status, d.WarehouseId));
			var line = await check.HospitalDispenseLines.AsNoTracking().SingleAsync(l => l.DispenseId == id && l.ItemId == f.Para);
			Assert.Equal(("P-NEAR, P-FAR", 8m, 0.75m), (line.Batches, line.DispensedQty!.Value, line.UnitPrice!.Value));   // 5 from the nearer batch, 3 from the next
			Assert.Equal(DateTime.Today.AddDays(20), line.NearestExpiry);
			var moves = await check.StockMovements.AsNoTracking().Where(m => m.SourceType == HospitalPharmacyService.MovementSource && m.SourceId == id).ToListAsync();
			Assert.Equal(8m, moves.Sum(m => m.QtyBase));
			Assert.Equal(7m, (await check.StockBalances.AsNoTracking().SingleAsync(b => b.ItemId == f.Para && b.WarehouseId == f.Store)).QtyOnHand - 50m);   // 65 − 8 = 57, of which 50 expired

			var bill = await check.HospitalBills.AsNoTracking().SingleAsync(b => b.ID == d.BillId);
			var charge = await check.HospitalBillLines.AsNoTracking().SingleAsync(l => l.BillId == bill.ID);   // the free-text drug is not charged
			Assert.Equal(("MED-PARA", 8m, 0.75m, HospitalBillLineSources.Pharmacy), (charge.Code, charge.Quantity, charge.UnitPrice, charge.Source));

			var lines = journals.Posted.SelectMany(j => j.Lines).ToList();
			Assert.Equal(3.2m, lines.Where(l => l.AccountId == f.Cogs).Sum(l => l.Debit));       // 8 × 0.400
			Assert.Equal(3.2m, lines.Where(l => l.AccountId == f.Inventory).Sum(l => l.Credit));
		}

		[SkippableFact]
		public async Task A_dispense_that_cannot_be_covered_in_full_leaves_nothing_behind()
		{
			Skip.If(!_sql.Available, _sql.SkipReason);
			var (f, db, stock, _) = await ArrangeAsync();
			// paracetamol is there, but only 2 amoxicillin for 3
			var id = await VerifiedRxAsync(db, f.Patient, (f.Para, "Paracetamol", 4m), (f.Amox, "Amoxicillin", 3m));
			var r = await Pharmacy(db, stock).DispenseAsync(Ctx(), id, 7, "u7", DateTime.UtcNow);
			Assert.False(r.Ok);

			await using var check = _sql.ContextFor(_probe!, 1);
			Assert.Equal(HospitalDispenseStatus.Verified, (await check.HospitalDispenses.AsNoTracking().SingleAsync(x => x.ID == id)).Status);
			Assert.Empty(await check.StockMovements.AsNoTracking().Where(m => m.SourceType == HospitalPharmacyService.MovementSource).ToListAsync());
			Assert.Equal(65m, (await check.StockBalances.AsNoTracking().SingleAsync(b => b.ItemId == f.Para && b.WarehouseId == f.Store)).QtyOnHand);
			Assert.Empty(await check.HospitalBillLines.AsNoTracking().ToListAsync());
			Assert.Null((await check.HospitalDispenseLines.AsNoTracking().FirstAsync(l => l.DispenseId == id)).DispensedQty);
		}
	}
}
