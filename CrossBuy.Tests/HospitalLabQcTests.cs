using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H232 — laboratory quality control. What must never happen: a run past 3 SD accepted; a slow drift (2-2s,
	// 4-1s, 10x) missed because each value alone looks fine; a 1-2s warning treated as a rejection; or a patient result
	// entered on a test whose control is out.
	// ==========================================================================================
	public class HospitalLabQcTests
	{
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);

		[Theory]
		[InlineData(0.5, new double[0], "Accepted", "")]
		[InlineData(2.4, new double[0], "Warning", "1-2s")]
		[InlineData(3.2, new double[0], "Rejected", "1-3s")]
		[InlineData(2.3, new[] { 2.1 }, "Rejected", "2-2s")]
		[InlineData(-2.3, new[] { 2.1 }, "Rejected", "R-4s")]
		[InlineData(1.2, new[] { 1.5, 1.1, 1.3 }, "Rejected", "4-1s")]
		[InlineData(0.4, new[] { 0.3, 0.2, 0.9, 0.1, 0.5, 0.6, 0.2, 0.3, 0.4 }, "Rejected", "10x")]
		[InlineData(0.4, new[] { 0.3, 0.2, 0.9, 0.1, -0.5, 0.6, 0.2, 0.3, 0.4 }, "Accepted", "")]
		[InlineData(2.3, new[] { -0.5 }, "Warning", "1-2s")]
		public void The_westgard_rules_judge_each_run(double z, double[] previous, string status, string violations)
		{
			var (s, v) = HospitalQcRules.Judge((decimal)z, previous.Select(x => (decimal)x).ToList());
			Assert.Equal(status, s);
			Assert.Equal(violations, string.Join(",", v));
		}

		[Fact]
		public void A_material_needs_a_lot_a_level_and_a_positive_sd()
		{
			var today = new DateTime(2026, 11, 15);
			Assert.Null(HospitalQcRules.MaterialRefusal("L123", 2, 5.4m, 0.2m, today.AddDays(30), today));
			Assert.Equal("The standard deviation must be above zero", HospitalQcRules.MaterialRefusal("L123", 2, 5.4m, 0m, null, today));
			Assert.Equal("The level is 1, 2 or 3", HospitalQcRules.MaterialRefusal("L123", 4, 5.4m, 0.2m, null, today));
			Assert.Equal("This lot has expired", HospitalQcRules.MaterialRefusal("L123", 1, 5.4m, 0.2m, today.AddDays(-1), today));
			Assert.Equal(2m, HospitalQcRules.Z(5.8m, 5.4m, 0.2m));
		}

		[Fact]
		public async Task A_rejected_control_stops_patient_results_until_an_accepted_run()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var qc = new HospitalLabQcService(host.Db, access); var lab = new HospitalLabService(host.Db, access);
			var ctx = new BusinessContext { CompanyId = 1, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };
			var pid = (await new HospitalPatientService(host.Db, access).SaveAsync(ctx, new HospitalPatientInput { Name = "م ق", NameEn = "P Q", Sex = "M" }, 7, Now)).Id!.Value;
			host.Seed.HospitalServices.Add(new HospitalService { CompanyId = 1, Code = "LAB-K", Name = "بوتاسيوم", NameEn = "Potassium test", Category = HospitalServiceCategories.Lab, IsActive = true });
			await host.Seed.SaveChangesAsync();
			int svc = (await host.Db.HospitalServices.AsNoTracking().SingleAsync(x => x.Code == "LAB-K")).ID;
			host.Seed.HospitalLabAnalytes.Add(new HospitalLabAnalyte { CompanyId = 1, ServiceId = svc, Code = "K", Name = "بوتاسيوم", NameEn = "Potassium", Unit = "mmol/L", RefLow = 3.5m, RefHigh = 5.1m, Decimals = 1 });
			host.Seed.HospitalLabOrders.Add(new HospitalLabOrder { CompanyId = 1, AccessionNo = "LAB-000700", PatientId = pid, ServiceId = svc, Code = "LAB-K", Name = "ب", NameEn = "K", Status = HospitalLabStatus.Received, OrderedAt = Now, ReceivedAt = Now });
			await host.Seed.SaveChangesAsync();
			var k = await host.Db.HospitalLabAnalytes.AsNoTracking().SingleAsync();
			var orderId = (await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync()).ID;

			var mat = await qc.SaveMaterialAsync(ctx, new HospitalQcMaterialInput { AnalyteId = k.ID, Lot = "LOT-7", Level = 2, Mean = 4.5m, Sd = 0.1m }, 7, Now);
			Assert.True(mat.Ok, mat.Error);
			Assert.Equal("This lot and level are already set for this analyte", (await qc.SaveMaterialAsync(ctx, new HospitalQcMaterialInput { AnalyteId = k.ID, Lot = "LOT-7", Level = 2, Mean = 4.5m, Sd = 0.1m }, 7, Now)).Error);

			Assert.Equal(HospitalQcStatus.Accepted, (await qc.RecordRunAsync(ctx, mat.Id!.Value, 4.52m, null, 7, Now)).Status);
			Assert.Empty(await HospitalLabQcService.OutOfControlAsync(host.Db, 1, svc));
			var (r, status, v) = await qc.RecordRunAsync(ctx, mat.Id.Value, 4.9m, "Recalibrated", 7, Now.AddMinutes(5));   // z = +4
			Assert.Equal((HospitalQcStatus.Rejected, "1-3s"), (status, v![0]));
			Assert.Equal(new[] { "Potassium" }, await HospitalLabQcService.OutOfControlAsync(host.Db, 1, svc));
			Assert.Equal("Quality control is out for {0}: run an accepted control before patient results",
				(await lab.SaveResultsAsync(ctx, orderId, new Dictionary<int, string?> { [k.ID] = "4.2" }, null, 7, Now)).Error);

			Assert.Equal(HospitalQcStatus.Accepted, (await qc.RecordRunAsync(ctx, mat.Id.Value, 4.48m, null, 7, Now.AddMinutes(20))).Status);
			Assert.True((await lab.SaveResultsAsync(ctx, orderId, new Dictionary<int, string?> { [k.ID] = "4.2" }, null, 7, Now)).Ok);
			var page = await qc.PageAsync(ctx, "pot", 1, 12);
			var row = Assert.Single(page.Rows);
			Assert.Equal(3, row.Points.Count);
			Assert.Equal(HospitalQcStatus.Accepted, row.LatestStatus);
		}

		[Fact]
		public void The_tables_match_their_slice()
		{
			var model = HospitalSliceParity.Model(HospitalLabQcModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h232_lab_qc.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalLabQcMaterial), "HospitalLabQcMaterials");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalLabQcMaterial), "HospitalLabQcMaterials");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalLabQcRun), "HospitalLabQcRuns");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalLabQcRun), "HospitalLabQcRuns");
			foreach (var x in HospitalQcStatus.All) Assert.Contains("'" + x + "'", s);
		}
	}
}
