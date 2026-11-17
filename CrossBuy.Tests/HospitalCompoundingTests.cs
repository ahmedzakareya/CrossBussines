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
	// HOSPITAL H153: compounding. What must never happen:
	//   · a chemotherapy dose by injection leaving the pharmacy without a released, in-date preparation (an oral one is
	//     not held back);
	//   · a beyond-use time longer than its storage allows; the preparer checking their own preparation; a rejection with
	//     no reason; a preparation released after its beyond-use time;
	//   · two live preparations for one line (a rejected one is made again);
	//   · a bag label without its cytotoxic warning, or another company's preparation printed.
	// ==========================================================================================
	public class HospitalCompoundingTests
	{
		private static HospitalCompoundInput Bag(int hours = 10, string storage = HospitalCompoundStorage.Room) =>
			new() { Diluent = "Sodium chloride 0.9%", FinalVolumeMl = 250, Storage = storage, BudHours = hours, Notes = "test" };

		[Fact]
		public void The_rules_cap_the_beyond_use_time_and_keep_the_check_independent()
		{
			Assert.Equal((12, 24), (HospitalCompoundRules.CapHours(HospitalCompoundStorage.Room), HospitalCompoundRules.CapHours(HospitalCompoundStorage.Fridge)));
			Assert.Equal("The beyond-use time is longer than its storage allows ({0} h)", HospitalCompoundRules.RefuseRecord(Bag(13)));
			Assert.Null(HospitalCompoundRules.RefuseRecord(Bag(24, HospitalCompoundStorage.Fridge)));
			Assert.Equal("Set the beyond-use time in hours", HospitalCompoundRules.RefuseRecord(Bag(0)));
			Assert.Equal("Choose how the preparation is kept", HospitalCompoundRules.RefuseRecord(new HospitalCompoundInput { BudHours = 4, Diluent = "x" }));
			Assert.Equal("Write the diluent", HospitalCompoundRules.RefuseRecord(new HospitalCompoundInput { BudHours = 4, Storage = HospitalCompoundStorage.Room, Kind = HospitalCompoundKind.IV }));
			Assert.Null(HospitalCompoundRules.RefuseRecord(new HospitalCompoundInput { BudHours = 4, Storage = HospitalCompoundStorage.Room, Kind = HospitalCompoundKind.TPN }));   // TPN: the bag is the diluent
			Assert.Equal("The final volume must be above zero", HospitalCompoundRules.RefuseRecord(new HospitalCompoundInput { BudHours = 4, Storage = HospitalCompoundStorage.Room, Diluent = "x", FinalVolumeMl = 0 }));
			Assert.Equal("The pharmacist who prepared it cannot check it", HospitalCompoundRules.RefuseCheck(true, null, 7, 7, Now.AddHours(1), Now));
			Assert.Equal("Write why the preparation is rejected", HospitalCompoundRules.RefuseCheck(false, " ", 7, 9, Now.AddHours(1), Now));
			Assert.Equal("The preparation is past its beyond-use time: reject it and make it again", HospitalCompoundRules.RefuseCheck(true, null, 7, 9, Now, Now));
			Assert.True(HospitalCompoundRules.MustCompound(HospitalChemoRoute.IV));
			Assert.False(HospitalCompoundRules.MustCompound(HospitalChemoRoute.Oral));
		}

		/// The prescription turned into a chemotherapy cycle's request: the paracetamol line given IV, the amoxicillin PO.
		private static async Task<(World W, HospitalDispense Rx, int Iv, int Po, HospitalCompoundingService Svc)> Chemo()
		{
			var (w, rx, para, amox) = await Prescribed();
			var d = await w.Host.Db.HospitalDispenses.FirstAsync(x => x.ID == rx.ID);
			d.Source = HospitalDispenseSource.Oncology; await w.Host.Db.SaveChangesAsync();
			var cycle = new HospitalChemoCycle { CompanyId = 1, CourseId = 1, PatientId = d.PatientId, CycleNo = 1, CycleDate = Now.Date, DispenseId = d.ID, CreatedAt = Now };
			w.Host.Db.Add(cycle); await w.Host.Db.SaveChangesAsync();
			w.Host.Db.Add(new HospitalChemoDose { CompanyId = 1, CycleId = cycle.ID, PatientId = d.PatientId, DrugName = "MED-PARA", Dose = 60, DoseMg = 100m, Route = HospitalChemoRoute.IV });
			w.Host.Db.Add(new HospitalChemoDose { CompanyId = 1, CycleId = cycle.ID, PatientId = d.PatientId, DrugName = "MED-AMOX", Dose = 20, DoseMg = 500m, Route = HospitalChemoRoute.Oral });
			await w.Host.Db.SaveChangesAsync();
			var lines = await Lines(w, rx.ID);
			return (w, d, lines[para].ID, lines[amox].ID, new HospitalCompoundingService(w.Host.Db, w.Access, w.Pharmacy));
		}

		[Fact]
		public async Task A_chemotherapy_injection_leaves_only_once_its_preparation_is_released()
		{
			var (w, rx, iv, po, svc) = await Chemo();
			using var _h = w.Host;
			Assert.Equal("{0} must be compounded and checked before it leaves", (await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now)).Error);
			Assert.True((await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now, default, new Dictionary<int, decimal> { [iv] = 0, [po] = 4 })).Ok);   // the oral one is not held back

			Assert.Equal("The beyond-use time is longer than its storage allows ({0} h)", (await svc.RecordAsync(Ctx(), rx.ID, iv, Bag(13), 7, Now)).Error);
			var made = await svc.RecordAsync(Ctx(), rx.ID, iv, Bag(10), 7, Now);
			Assert.True(made.Ok, made.Error);
			var rec = await w.Host.Db.Set<HospitalCompounding>().AsNoTracking().SingleAsync();
			Assert.Equal((HospitalCompoundKind.Chemo, 100m, "IV", "CMP-000001", Now.AddHours(10)), (rec.Kind, rec.DoseMg!.Value, rec.Route, rec.CompoundNo, rec.BeyondUseAt));   // chemo whatever was asked
			Assert.Equal("This line already has a preparation", (await svc.RecordAsync(Ctx(), rx.ID, iv, Bag(10), 7, Now)).Error);
			Assert.Equal("{0} must be compounded and checked before it leaves", (await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now)).Error);   // prepared is not released

			Assert.Equal("The pharmacist who prepared it cannot check it", (await svc.CheckAsync(Ctx(), rec.ID, true, null, 7, Now)).Error);
			Assert.True((await svc.CheckAsync(Ctx(9), rec.ID, true, null, 9, Now.AddHours(1))).Ok);
			Assert.Equal("This preparation was already checked", (await svc.CheckAsync(Ctx(9), rec.ID, false, "late doubt", 9, Now.AddHours(1))).Error);
			Assert.Equal("{0} must be compounded and checked before it leaves", (await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now.AddHours(11))).Error);   // released, but out of date
			Assert.True((await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now.AddHours(2))).Ok);
		}

		[Fact]
		public async Task A_dose_the_store_does_not_stock_still_waits_for_its_released_preparation()
		{
			var (w, rx, iv, po, svc) = await Chemo();
			using var _h = w.Host;
			var line = await w.Host.Db.HospitalDispenseLines.FirstAsync(l => l.ID == iv);
			line.ItemId = null; await w.Host.Db.SaveChangesAsync();   // brought in, or not stocked: nothing leaves, but it is still prepared
			Assert.Equal("{0} must be compounded and checked before it leaves", (await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now)).Error);
			var id = (await svc.RecordAsync(Ctx(), rx.ID, iv, Bag(10), 7, Now)).Id!.Value;
			Assert.True((await svc.CheckAsync(Ctx(9), id, true, null, 9, Now)).Ok);
			Assert.True((await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now)).Ok);
		}

		[Fact]
		public async Task A_rejected_preparation_is_made_again_and_one_past_its_time_is_never_released()
		{
			var (w, rx, iv, _, svc) = await Chemo();
			using var _h = w.Host;
			var first = (await svc.RecordAsync(Ctx(), rx.ID, iv, Bag(2), 7, Now)).Id!.Value;
			Assert.Equal("The preparation is past its beyond-use time: reject it and make it again", (await svc.CheckAsync(Ctx(9), first, true, null, 9, Now.AddHours(3))).Error);
			Assert.Equal("Write why the preparation is rejected", (await svc.CheckAsync(Ctx(9), first, false, null, 9, Now.AddHours(3))).Error);
			Assert.True((await svc.CheckAsync(Ctx(9), first, false, "Past its beyond-use time", 9, Now.AddHours(3))).Ok);
			Assert.True((await svc.RecordAsync(Ctx(), rx.ID, iv, Bag(10, HospitalCompoundStorage.Fridge), 7, Now.AddHours(3))).Ok);   // made again
			var (rows, total) = await svc.ListAsync(Ctx(), null, HospitalCompoundingService.Filter.ToCheck, 1, 25);
			Assert.Equal(1, total);
			Assert.Equal("CMP-000002", rows[0].Record!.CompoundNo);
			Assert.Equal("not_found", (await svc.RecordAsync(Ctx(7, 2), rx.ID, iv, Bag(10), 7, Now)).Error);
		}

		[Fact]
		public async Task The_bag_label_carries_the_beyond_use_time_and_the_cytotoxic_warning()
		{
			var (w, rx, iv, _, svc) = await Chemo();
			using var _h = w.Host;
			var id = (await svc.RecordAsync(Ctx(), rx.ID, iv, Bag(10, HospitalCompoundStorage.Fridge), 7, Now)).Id!.Value;
			var ds = HospitalDocumentDatasets.CompoundLabel();
			ReportDataQuery Q(BusinessContext c, string culture) => new()
			{
				Definition = new ReportDefinition
				{
					Code = "test." + ds.DatasetCode, Module = ds.Module, TitleAr = "اختبار", TitleEn = "Test", DataSourceKey = ds.DataSourceKey, PermissionKey = ds.RequiredPermissionKey,
					Columns = ds.Fields.Select(x => new ReportColumn { Key = x.Key, TitleAr = x.TitleAr, TitleEn = x.TitleEn, Type = x.Type }).ToList(),
				},
				Context = c,
				Parameters = new ReportParameterSet(new[] { new ReportParameterValue { Key = "CompoundingId", Type = ReportFieldType.Integer, Value = id } }, new Dictionary<string, string?>()),
				Culture = CultureInfo.GetCultureInfo(culture),
			};
			var src = new HospitalCompoundLabelSource(w.Host.Db);
			var en = Assert.Single((await src.FetchAsync(Q(Ctx(), "en"))).Rows);
			Assert.Equal(("CMP-000001", "100 mg · IV", "⁨Sodium chloride 0.9%⁩ · ⁦250 mL⁩", "CYTOTOXIC — handle with gloves", "Refrigerate 2–8 °C"),
				((string?)en["DocumentNo"], (string?)en["Dose"], (string?)en["Diluent"], (string?)en["Hazard"], (string?)en["Storage"]));
			Assert.Equal(Now.AddHours(10).ToLocalTime(), (DateTime)en["BeyondUse"]!);
			var ar = Assert.Single((await src.FetchAsync(Q(Ctx(), "ar"))).Rows);
			Assert.DoesNotMatch("[A-Za-z]", (string?)ar["Hazard"]!);
			Assert.Empty((await src.FetchAsync(Q(Ctx(7, 2), "en"))).Rows);
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPharmacyModel.Configure);

		[Fact]
		public void The_compounding_table_matches_its_slice()
		{
			var slice = HospitalSliceParity.Slice("hospital_h153_compounding.sql");
			HospitalSliceParity.NamesItsTable(Model(), slice, typeof(HospitalCompounding), "HospitalCompoundings");
			HospitalSliceParity.ColumnsMatch(Model(), slice, typeof(HospitalCompounding), "HospitalCompoundings");
		}
	}
}
