using System.Globalization;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static CrossBuy.Tests.HospitalPartialDispenseTests;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H147: the pharmacy's dispensing labels through Report Studio. What must never happen:
	//   · a label for a medicine that has not left the pharmacy, or for one the patient brought all of back;
	//   · a label stating more than is in the patient's hands (what was given less what came back);
	//   · a cold-chain or high-alert medicine labelled without its warning, or any label without "keep from children";
	//   · English words on an Arabic label, or Arabic on an English one, where the prescription has both;
	//   · another company's dispense printed by changing the id in the URL;
	//   · a label whose text is clipped, or that runs onto a second label.
	// ==========================================================================================
	public class HospitalRxLabelTests
	{
		private static ReportDataQuery Query(BusinessContext ctx, int dispenseId, int? lineId = null, string culture = "en")
		{
			var ds = HospitalDocumentDatasets.RxLabel();
			var values = new List<ReportParameterValue> { new() { Key = "DispenseId", Type = ReportFieldType.Integer, Value = dispenseId } };
			if (lineId != null) values.Add(new() { Key = "LineId", Type = ReportFieldType.Integer, Value = lineId.Value });
			return new ReportDataQuery
			{
				Definition = new ReportDefinition
				{
					Code = "test." + ds.DatasetCode, Module = ds.Module, TitleAr = "اختبار", TitleEn = "Test", DataSourceKey = ds.DataSourceKey, PermissionKey = ds.RequiredPermissionKey,
					Columns = ds.Fields.Select(x => new ReportColumn { Key = x.Key, TitleAr = x.TitleAr, TitleEn = x.TitleEn, Type = x.Type }).ToList(),
				},
				Context = ctx,
				Parameters = new ReportParameterSet(values, new Dictionary<string, string?>()),
				Culture = CultureInfo.GetCultureInfo(culture),
			};
		}

		private static async Task Master(World w, int itemId, bool cold = false, bool highAlert = false)
		{
			w.Host.Seed.Add(new HospitalDrug { CompanyId = 1, ItemId = itemId, GenericName = "أموكسيسيلين", GenericNameEn = "amoxicillin", TallMan = "amOXicillin", Strength = 500m, StrengthUnit = "mg",
				Form = HospitalDrugForm.Capsule, ColdChain = cold, HighAlert = highAlert, UpdatedAt = Now });
			await w.Host.Seed.SaveChangesAsync();
		}

		[Fact]
		public async Task Nothing_is_labelled_before_it_leaves_and_each_medicine_given_gets_one_label()
		{
			var (w, rx, para, amox) = await Prescribed();
			using var _h = w.Host;
			var src = new HospitalRxLabelSource(w.Host.Db);
			Assert.Empty((await src.FetchAsync(Query(Ctx(), rx.ID))).Rows);   // verified, not dispensed: no label yet

			var lines = await Lines(w, rx.ID);
			Assert.True((await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now, default, new Dictionary<int, decimal> { [lines[para].ID] = 6, [lines[amox].ID] = 0 })).Ok);
			var partial = Assert.Single((await src.FetchAsync(Query(Ctx(), rx.ID))).Rows);   // only what left: the amoxicillin is still owed
			Assert.Equal(("MED-PARA", "6", rx.RxNo), ((string?)partial["Drug"], (string?)partial["Quantity"], (string?)partial["DocumentNo"]));

			Assert.True((await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now)).Ok);
			var all = (await src.FetchAsync(Query(Ctx(), rx.ID))).Rows;
			Assert.Equal(new[] { ("MED-PARA", "10"), ("MED-AMOX", "4") }, all.Select(r => ((string?)r["Drug"], (string?)r["Quantity"])).ToArray());
			var one = Assert.Single((await src.FetchAsync(Query(Ctx(), rx.ID, lines[amox].ID))).Rows);   // a reprint of one medicine
			Assert.Equal("MED-AMOX", (string?)one["Drug"]);
			Assert.Empty((await src.FetchAsync(Query(Ctx(7, 2), rx.ID))).Rows);   // another company
		}

		[Fact]
		public async Task A_label_states_what_is_in_the_patients_hands_after_a_return()
		{
			var (w, rx, para, amox) = await Prescribed();
			using var _h = w.Host;
			var lines = await Lines(w, rx.ID);
			Assert.True((await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now)).Ok);
			var returns = new HospitalDispenseReturnService(w.Host.Db, w.Access, w.StockService, w.Billing, new NoopLedger());
			Assert.True((await returns.ReturnAsync(Ctx(), rx.ID, lines[para].ID, 3, "sealed", 7, "u7", Now)).Ok);
			Assert.True((await returns.ReturnAsync(Ctx(), rx.ID, lines[amox].ID, 4, "sealed", 7, "u7", Now)).Ok);
			var row = Assert.Single((await new HospitalRxLabelSource(w.Host.Db).FetchAsync(Query(Ctx(), rx.ID))).Rows);
			Assert.Equal(("MED-PARA", "7"), ((string?)row["Drug"], (string?)row["Quantity"]));   // 10 given, 3 back; the amoxicillin all came back
		}

		[Fact]
		public async Task The_drug_master_puts_the_warnings_and_the_strength_on_the_label_in_its_language()
		{
			var (w, rx, _, amox) = await Prescribed();
			using var _h = w.Host;
			await Master(w, amox, cold: true, highAlert: true);
			Assert.True((await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now)).Ok);
			var src = new HospitalRxLabelSource(w.Host.Db);
			var en = (await src.FetchAsync(Query(Ctx(), rx.ID))).Rows;
			var ar = (await src.FetchAsync(Query(Ctx(), rx.ID, culture: "ar"))).Rows;
			var amoxEn = en.Single(r => (string?)r["Drug"] == "MED-AMOX");
			Assert.Equal("amOXicillin · 500 mg · Capsules", (string?)amoxEn["DrugDetail"]);   // Tall Man in English
			Assert.Equal("High-alert medicine · Keep in a fridge, 2–8 °C · Keep out of the reach of children", (string?)amoxEn["Warnings"]);
			var amoxAr = ar.Single(r => (string?)r["Drug"] == "MED-AMOX");
			Assert.Equal("أموكسيسيلين · 500 مجم · كبسولات", (string?)amoxAr["DrugDetail"]);   // no Latin unit on an Arabic label
			Assert.Contains("الثلاجة", (string?)amoxAr["Warnings"]);
			Assert.DoesNotMatch("[A-Za-z]", (string?)amoxAr["Warnings"]!);   // no English words on the Arabic label
			// a medicine with no master record still says to keep it from children
			Assert.All(en, r => Assert.EndsWith("Keep out of the reach of children", (string?)r["Warnings"]));
			Assert.Null(en.Single(r => (string?)r["Drug"] == "MED-PARA")["DrugDetail"]);
		}

		private sealed class NoopLedger : IHospitalLedger
		{
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
				=> Task.FromResult<(bool, string?, int?)>((true, null, 1));
		}

		// ---- the designed label ----

		[Fact]
		public void One_medicine_per_label_and_the_label_never_runs_onto_the_next()
		{
			var layout = HospitalDeskLayouts.RxLabel();
			Assert.Equal((100d, 60d), ReportPaper.SizeOf(ReportPageSize.RxLabel));
			Assert.Equal(ReportPageSize.RxLabel, layout.Page.PageSize);
			var band = Assert.Single(layout.Bands);
			Assert.Equal(ReportBandKind.Detail, band.Kind);   // printed once per row: a label per medicine
			Assert.True(band.HeightMm <= ReportPaper.ContentHeightMm(layout.Page) + 0.001);   // one label fits a page
			Assert.True(band.HeightMm * 2 > ReportPaper.ContentHeightMm(layout.Page));         // and a second never shares it
			var provider = new HospitalReportDefinitionProvider().GetDefinitions().ToDictionary(d => d.Code);
			Assert.NotNull(provider[HospitalDocumentDatasetCodes.RxLabel].ShippedVisual);
			Assert.Equal(ReportPageSize.RxLabel, provider[HospitalDocumentDatasetCodes.RxLabel].DefaultPageSetup.PageSize);
		}

		[Theory]
		[InlineData(HospitalDocumentDatasetCodes.VisitSlip)]
		[InlineData(HospitalDocumentDatasetCodes.Wristband)]
		[InlineData(HospitalDocumentDatasetCodes.DepositReceipt)]
		[InlineData(HospitalDocumentDatasetCodes.RxLabel)]
		[InlineData(HospitalDocumentDatasetCodes.CreditNote)]   // H149
		[InlineData(HospitalDocumentDatasetCodes.CompoundLabel)]   // H153
		[InlineData(HospitalDocumentDatasetCodes.ReferralLetter)]   // H204
		[InlineData(HospitalDocumentDatasetCodes.SpecimenLabel)]   // H206
		[InlineData(HospitalDocumentDatasetCodes.SickLeave)]   // H208
		[InlineData(HospitalDocumentDatasetCodes.DiseaseReport)]   // H209
		[InlineData(HospitalDocumentDatasetCodes.PathReport)]   // H216
		[InlineData(HospitalDocumentDatasetCodes.EndoReport)]   // H219
		[InlineData(HospitalDocumentDatasetCodes.CardioReport)]   // H219
		[InlineData(HospitalDocumentDatasetCodes.PathLabels)]   // H222
		public void No_one_piece_paper_carries_the_running_footer(string code)
		{
			// the PDF engine's footer (the report code and "Page 1 of 1") printed over the foot of the wristband and the label
			var page = HospitalDeskLayouts.For(code)!.Page;
			Assert.False(page.ShowPageNumbers);
			Assert.False(page.ShowFooter);
			Assert.False(page.ShowHeader);
		}
	}
}
