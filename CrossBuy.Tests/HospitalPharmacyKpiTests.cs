using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static CrossBuy.Tests.HospitalPartialDispenseTests;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H151: the pharmacy's indicators and its ABC/VEN matrix. What must never happen:
	//   · a medicine ranked A that is not among those making the first 80% of the value (or one consumed nothing not C);
	//   · a vital medicine ever put in a category below I;
	//   · consumption counting what came back, or the matrix counting a medicine twice or losing one;
	//   · another company's dispensing in this company's figures.
	// ==========================================================================================
	public class HospitalPharmacyKpiTests
	{
		[Fact]
		public void Abc_ranks_by_the_value_before_each_medicine()
		{
			var r = HospitalAbcVenRules.Classify(new[] { (1, 700m), (2, 150m), (3, 100m), (4, 50m), (5, 0m) });
			// before: 1 → 0%, 2 → 70%, 3 → 85%, 4 → 95%; 5 consumed nothing
			Assert.Equal(new[] { "A", "A", "B", "C", "C" }, new[] { 1, 2, 3, 4, 5 }.Select(i => r[i].Abc).ToArray());
			var s = HospitalAbcVenRules.Classify(new[] { (1, 500m), (2, 250m), (3, 150m), (4, 60m), (5, 40m) });
			// before: 1 → 0%, 2 → 50%, 3 → 75%, 4 → 90%, 5 → 96%
			Assert.Equal(new[] { "A", "A", "A", "B", "C" }, new[] { 1, 2, 3, 4, 5 }.Select(i => s[i].Abc).ToArray());
			Assert.Equal(50m, s[1].SharePct);
			Assert.All(HospitalAbcVenRules.Classify(new[] { (1, 0m), (2, 0m) }).Values, x => Assert.Equal("C", x.Abc));   // nothing consumed
		}

		[Theory]
		[InlineData("A", "N", "I")]
		[InlineData("C", "V", "I")]
		[InlineData("B", "V", "I")]
		[InlineData("B", "E", "II")]
		[InlineData("C", "E", "II")]
		[InlineData("C", "N", "III")]
		[InlineData("C", null, "II")]   // unclassified counts as essential
		public void The_who_matrix_never_lets_a_vital_medicine_below_category_one(string abc, string? ven, string category)
			=> Assert.Equal(category, HospitalAbcVenRules.Category(abc, ven));

		[Fact]
		public void Percentiles_and_cover_are_plain_arithmetic()
		{
			Assert.Equal(30, HospitalAbcVenRules.Percentile(new double[] { 10, 20, 30, 40, 50 }, 50));
			Assert.Equal(50, HospitalAbcVenRules.Percentile(new double[] { 10, 20, 30, 40, 50 }, 90));
			Assert.Null(HospitalAbcVenRules.Percentile(Array.Empty<double>(), 50));
			Assert.Equal(60, HospitalAbcVenRules.DaysOfCover(200m, 100m, 30));   // 100 over 30 days → 3.33 a day → 60 days
			Assert.Null(HospitalAbcVenRules.DaysOfCover(200m, 0m, 30));
		}

		[Fact]
		public async Task The_figures_come_from_what_was_dispensed_less_what_came_back()
		{
			var (w, rx, para, amox) = await Prescribed();   // 10 paracetamol, 4 amoxicillin; the test store costs 2.000 a unit
			using var _h = w.Host;
			w.Host.Seed.Add(new HospitalDrug { CompanyId = 1, ItemId = amox, GenericName = "أموكسيسيلين", GenericNameEn = "amoxicillin", Ven = HospitalVen.Vital, UpdatedAt = Now });
			await w.Host.Seed.SaveChangesAsync();
			var lines = await Lines(w, rx.ID);
			Assert.True((await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now)).Ok);
			var back = new HospitalDispenseReturnService(w.Host.Db, w.Access, w.StockService, w.Billing, new NoLedgerHere());
			Assert.True((await back.ReturnAsync(Ctx(), rx.ID, lines[para].ID, 2, "sealed", 7, "u7", Now)).Ok);

			var svc = new HospitalPharmacyKpiService(w.Host.Db, w.Pharmacy);
			var k = await svc.KpisAsync(Ctx(), 30, Now.AddHours(1));
			Assert.Equal((1, 24m), (k.Dispensed, k.Consumption));   // (10 + 4 − 2) × 2.000
			Assert.Equal((1, 6m), (k.Returns, k.ReturnsValue));      // the return credits the bill: 2 × 3.000
			Assert.Equal(k.Matrix.Sum(c => c.Items), (await svc.RowsAsync(Ctx(), null, null, null, 1, 100, Now.AddHours(1))).Total);   // every medicine once
			var (rows, total) = await svc.RowsAsync(Ctx(), null, null, null, 1, 100, Now.AddHours(1));
			var a = rows.Single(r => r.ItemId == para);
			Assert.Equal(("A", 16m), (a.Abc, a.Value));   // 8 × 2.000 of 24: the first two thirds of the value
			var v = rows.Single(r => r.ItemId == amox);
			Assert.Equal(("V", "I"), (v.Ven, v.Category));
			Assert.Equal(1, (await svc.RowsAsync(Ctx(), "AMOX", null, null, 1, 100, Now.AddHours(1))).Total);
			Assert.Equal(1, (await svc.RowsAsync(Ctx(), null, "AE", null, 1, 100, Now.AddHours(1))).Total);   // paracetamol: A, unclassified → E
			Assert.Equal(0, (await svc.KpisAsync(Ctx(7, 2), 30, Now.AddHours(1))).Dispensed);
		}

		private sealed class NoLedgerHere : IHospitalLedger
		{
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(CrossBuy.Models.Platform.BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
				=> Task.FromResult<(bool, string?, int?)>((true, null, 1));
		}
	}
}
