using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H137: the controlled-drug register and its blind counts. What must never happen:
	//   · a count with no witness, or the counter witnessing themselves;
	//   · a count of a drug that is not controlled, or in a store outside the register;
	//   · a difference left without a discrepancy;
	//   · a discrepancy closed as a miscount when the recount still differs;
	//   · a surplus written off as a loss;
	//   · a loss closed without the inventory's write-off;
	//   · a discrepancy closed twice.
	// ==========================================================================================
	public class HospitalControlledCountTests
	{
		private const int CompanyOne = 1, Pharmacy = 5, Other = 7, Item = 71, NotControlled = 72, Witness = 20;
		private static readonly DateTime Now = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx() => new() { CompanyId = CompanyOne, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		// ---------- the rules ----------

		[Fact]
		public void A_difference_opens_a_discrepancy_that_is_overdue_after_the_shift()
		{
			Assert.Equal((HospitalCountStatus.Matched, HospitalCountStatus.Open, HospitalCountStatus.Open),
				(HospitalControlledCountRules.StatusOf(0), HospitalControlledCountRules.StatusOf(-1), HospitalControlledCountRules.StatusOf(2)));
			var c = new HospitalControlledCount { Status = HospitalCountStatus.Open, CountedAt = Now };
			Assert.False(HospitalControlledCountRules.IsOverdue(c, Now.AddHours(12)));
			Assert.True(HospitalControlledCountRules.IsOverdue(c, Now.AddHours(12).AddMinutes(1)));
			c.Status = HospitalCountStatus.Resolved;
			Assert.False(HospitalControlledCountRules.IsOverdue(c, Now.AddDays(3)));
		}

		[Fact]
		public void The_ledger_carries_a_running_balance()
			=> Assert.Equal(new[] { 10m, 8m, 9m, 4m }, HospitalControlledCountRules.Running(new (short, decimal)[] { (1, 10), (-1, 2), (1, 1), (-1, 5) }).ToArray());

		[Theory]
		[InlineData(HospitalCountResolution.Miscount, -2, null, "Enter the recount")]
		[InlineData(HospitalCountResolution.Miscount, -2, "9", "The recount differs from the expected quantity — choose another reason")]
		[InlineData(HospitalCountResolution.Miscount, -2, "10", null)]
		[InlineData(HospitalCountResolution.Documentation, -2, null, "Write which movement was not recorded")]
		[InlineData(HospitalCountResolution.Loss, 2, null, "A surplus is not a loss")]
		[InlineData("Theft", -2, null, "Choose the reason")]
		public void A_discrepancy_closes_only_with_a_sound_reason(string resolution, int difference, string? recount, string? error)
		{
			var c = new HospitalControlledCount { Status = HospitalCountStatus.Open, Expected = 10, Counted = 10 + difference, Difference = difference };
			var i = new HospitalCdResolveInput { Resolution = resolution, Recounted = recount == null ? null : decimal.Parse(recount) };
			Assert.Equal(error, HospitalControlledCountRules.ValidateResolution(c, i, 7)?.Error);
		}

		[Theory]
		[InlineData(HospitalControlledRules.MovementSource, "pmv:Controlled")]
		[InlineData("StockWriteOff", "pmv:WriteOff")]
		[InlineData("PurchaseReturn", "pmv:SupplierReturn")]
		[InlineData(HospitalPharmacyService.MovementSource, "pmv:Dispense")]
		[InlineData("Something", "pmv:Other")]
		public void Every_movement_has_one_label_for_both_screens(string source, string key) => Assert.Equal(key, HospitalStockSourceLabels.Key(source));

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPharmacyModel.Configure);

		[Fact]
		public void The_model_names_the_table_its_slice_creates()
			=> HospitalSliceParity.NamesItsTable(Model(), HospitalSliceParity.Slice("hospital_h137_controlled_counts.sql"), typeof(HospitalControlledCount), "HospitalControlledCounts");

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
			=> HospitalSliceParity.ColumnsMatch(Model(), HospitalSliceParity.Slice("hospital_h137_controlled_counts.sql"), typeof(HospitalControlledCount), "HospitalControlledCounts");

		// ---------- the service ----------

		/// The stock writer's write-off: records the loss asked of it.
		public class Stock : System.Reflection.DispatchProxy
		{
			public readonly List<(int Wh, string? Reason, decimal Qty)> WrittenOff = new();
			protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
			{
				if (method?.Name != nameof(IStockService.WriteOffAsync)) throw new NotSupportedException(method?.Name);
				var line = ((List<WriteOffLineInput>)args![5]!)[0];
				WrittenOff.Add(((int)args[1]!, line.Reason, line.Qty));
				return Task.FromResult<(bool, string?, string?, int?, string)>((true, null, "WOF-2026-00099", 99, "SeparateDocument"));
			}
		}

		private sealed class PasswordIsOk : IHospitalWitness
		{
			public Task<bool> VerifyAsync(int companyId, int employeeId, string password, CancellationToken ct = default) => Task.FromResult(password == "ok");
		}

		private sealed class NoLedger : IHospitalLedger
		{
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
				=> Task.FromResult<(bool, string?, int?)>((true, null, 1));
		}

		/// The pharmacy store holds 10 of a controlled drug (and some of a drug that is not controlled).
		private static async Task<(HospitalControlledCountService Svc, Stock Stock)> MakeAsync(PlatformTestHost host)
		{
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var stock = System.Reflection.DispatchProxy.Create<IStockService, Stock>();
			var pharmacy = new HospitalPharmacyService(host.Db, access, stock, new HospitalBillingService(host.Db, access, new NoLedger()));
			host.Seed.ItemCategories.Add(new ItemCategory { ID = 61, CompanyID = CompanyOne, Code = "MED", Name = "أدوية", NameEn = "Medicines" });
			foreach (var id in new[] { Item, NotControlled })
				host.Seed.Items.Add(new Item { ID = id, CompanyID = CompanyOne, ItemCode = "CD-" + id, Barcode = "B" + id, Name = "دواء " + id, NameEn = "Drug " + id, ItemCategoryId = 61, BaseUoMId = 1, IsActive = true, CreatedAt = Now });
			foreach (var (id, code) in new[] { (Pharmacy, "WH-PH"), (Other, "WH-X") })
				host.Seed.Warehouses.Add(new Warehouse { ID = id, CompanyID = CompanyOne, Code = code, Name = code, NameEn = code, IsActive = true });
			host.Seed.HospitalPharmacySettings.Add(new HospitalPharmacySettings { CompanyId = CompanyOne, WarehouseId = Pharmacy, NearExpiryDays = 60 });
			host.Seed.Set<HospitalControlledItem>().Add(new HospitalControlledItem { CompanyId = CompanyOne, ItemId = Item, CreatedAt = Now });
			host.Seed.StockBalances.Add(new StockBalance { CompanyID = CompanyOne, ItemId = Item, WarehouseId = Pharmacy, QtyOnHand = 10, TotalValue = 5 });
			host.Seed.Employee.Add(new CrossBuy.Models.Context.Admin.Employee { ID = Witness, EmpCompanyID = CompanyOne, FullName = "شاهد", FullNameEn = "Witness", IsActive = true,
				FirstName = "W", LastName = "T", Address = "-", PhoneNumber = "-", Email = "w@c1.local", ProfileImage = "-", Gender = "-", MaritalStatus = "-", UserId = "u20" });
			await host.Seed.SaveChangesAsync();
			return (new HospitalControlledCountService(host.Db, access, stock, new PasswordIsOk(), pharmacy), (Stock)(object)stock);
		}

		[Fact]
		public async Task A_blind_count_keeps_the_expected_quantity_and_opens_a_discrepancy_on_a_gap()
		{
			using var host = new PlatformTestHost(); var (svc, _) = await MakeAsync(host);
			Assert.Equal("The witness's password is not correct", (await svc.CountAsync(Ctx(), Pharmacy, Item, 10, Witness, "no", 7, Now)).Error);
			Assert.Equal("The witness must be someone else", (await svc.CountAsync(Ctx(), Pharmacy, Item, 10, 7, "ok", 7, Now)).Error);
			Assert.Equal("not_found", (await svc.CountAsync(Ctx(), Pharmacy, NotControlled, 10, Witness, "ok", 7, Now)).Error);
			Assert.Equal("not_found", (await svc.CountAsync(Ctx(), Other, Item, 10, Witness, "ok", 7, Now)).Error);   // not a register's store

			Assert.True((await svc.CountAsync(Ctx(), Pharmacy, Item, 10, Witness, "ok", 7, Now)).Ok);
			Assert.True((await svc.CountAsync(Ctx(), Pharmacy, Item, 8, Witness, "ok", 7, Now)).Ok);
			var counts = await host.Db.Set<HospitalControlledCount>().AsNoTracking().OrderBy(c => c.ID).ToListAsync();
			Assert.Equal(new[] { ("CDC-000001", HospitalCountStatus.Matched, 0m), ("CDC-000002", HospitalCountStatus.Open, -2m) }, counts.Select(c => (c.CountNo, c.Status, c.Difference)).ToArray());
			Assert.Equal(10m, counts[1].Expected);
			var stats = await svc.StatsAsync(Ctx(), Pharmacy, Now.AddHours(13));
			Assert.Equal((1, 1, 2), (stats.OpenDiscrepancies, stats.Overdue, stats.CountsThisWeek));
		}

		[Fact]
		public async Task A_loss_is_written_off_through_the_inventory_and_closed_once()
		{
			using var host = new PlatformTestHost(); var (svc, stock) = await MakeAsync(host);
			int id = (await svc.CountAsync(Ctx(), Pharmacy, Item, 8, Witness, "ok", 7, Now)).Id!.Value;
			var loss = new HospitalCdResolveInput { Resolution = HospitalCountResolution.Loss, Note = "Two ampoules broken, not recorded", WitnessId = Witness, Password = "no" };
			Assert.Equal("The witness's password is not correct", (await svc.ResolveAsync(Ctx(), id, loss, 7, "u7", Now)).Error);
			Assert.Empty(stock.WrittenOff);
			loss.Password = "ok";
			Assert.True((await svc.ResolveAsync(Ctx(), id, loss, 7, "u7", Now)).Ok);
			Assert.Equal((Pharmacy, (string?)"Lost", 2m), Assert.Single(stock.WrittenOff));
			var c = await host.Db.Set<HospitalControlledCount>().AsNoTracking().SingleAsync();
			Assert.Equal((HospitalCountStatus.Resolved, "WOF-2026-00099", (int?)Witness), (c.Status, c.DocumentNo, c.ResolutionWitnessEmployeeId));
			Assert.Equal("This count has no open discrepancy", (await svc.ResolveAsync(Ctx(), id, new HospitalCdResolveInput { Resolution = HospitalCountResolution.Documentation, Note = "x" }, 7, "u7", Now)).Error);
		}
	}
}
