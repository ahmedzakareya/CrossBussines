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
	// HOSPITAL H233 — ranges by age and sex, the delta check, autoverification. What must never happen: a woman's
	// haemoglobin judged on the men's range (or a newborn's on an adult's); a sudden jump from the patient's last value
	// passing unnoticed; an abnormal, critical or jumped value released without a person; an analyte not opted in
	// released automatically.
	// ==========================================================================================
	public class HospitalLabAutoTests
	{
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
		private static readonly HospitalLabAnalyte Hgb = new() { ID = 1, Code = "HGB", RefLow = 12.0m, RefHigh = 17.5m, CriticalLow = 7m, CriticalHigh = 20m };
		private static HospitalLabRefRange R(string? sex, int from, int? to, decimal lo, decimal hi) => new() { AnalyteId = 1, Sex = sex, AgeFromDays = from, AgeToDays = to, RefLow = lo, RefHigh = hi };
		private static readonly HospitalLabRefRange[] Ranges = { R(null, 0, 31, 14m, 24m), R(null, 365, 4380, 11m, 13.5m), R("M", 4380, null, 13.5m, 17.5m), R("F", 4380, null, 12m, 15.5m), R(null, 4380, null, 12.5m, 16m) };

		[Theory]
		[InlineData("F", 40 * 365, 12.0, 15.5)]     // her own sex before "both"
		[InlineData("M", 40 * 365, 13.5, 17.5)]
		[InlineData("M", 10, 14.0, 24.0)]           // a newborn
		[InlineData("F", 5 * 365, 11.0, 13.5)]      // a child
		[InlineData("M", 200, 12.0, 17.5)]          // no band fits: the analyte's own
		public void The_most_specific_range_fits_the_patient(string sex, int ageDays, double lo, double hi)
		{
			var l = HospitalLabAutoRules.Limits(Hgb, Ranges, sex, ageDays);
			Assert.Equal(((decimal)lo, (decimal)hi), (l.RefLow!.Value, l.RefHigh!.Value));
			Assert.Equal(7m, l.CriticalLow);   // a band without critical limits keeps the analyte's
		}

		[Fact]
		public void The_delta_check_and_the_autoverification_rule()
		{
			Assert.True(HospitalLabAutoRules.DeltaFailed(9.0m, 13.0m, 20m));     // −31 %
			Assert.False(HospitalLabAutoRules.DeltaFailed(12.0m, 13.0m, 20m));
			Assert.False(HospitalLabAutoRules.DeltaFailed(9.0m, null, 20m));     // nothing to compare with
			Assert.False(HospitalLabAutoRules.DeltaFailed(9.0m, 13.0m, null));   // not checked
			Assert.True(HospitalLabAutoRules.CanAutoVerify(new[] { (true, "N", false, true), (true, "N", false, true) }));
			Assert.False(HospitalLabAutoRules.CanAutoVerify(new[] { (true, "N", false, true), (false, "N", false, true) }));   // one not opted in
			Assert.False(HospitalLabAutoRules.CanAutoVerify(new[] { (true, "H", false, true) }));
			Assert.False(HospitalLabAutoRules.CanAutoVerify(new[] { (true, "N", true, true) }));
			Assert.Equal("The age band must end after it starts", HospitalLabAutoRules.RangeRefusal(null, 100, 50, 1m, 2m));
			Assert.Equal(3653, HospitalLabService.Days(10m, "years"));
		}

		[Fact]
		public async Task A_normal_result_is_released_alone_and_a_jump_waits_for_a_person()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var lab = new HospitalLabService(host.Db, access);
			var ctx = new BusinessContext { CompanyId = 1, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };
			var pid = (await new HospitalPatientService(host.Db, access).SaveAsync(ctx, new HospitalPatientInput { Name = "م ن", NameEn = "P N", Sex = "F", BirthDate = new DateTime(1986, 1, 1) }, 7, Now)).Id!.Value;
			host.Seed.HospitalServices.Add(new HospitalService { CompanyId = 1, Code = "LAB-HB", Name = "هيموجلوبين", NameEn = "Haemoglobin", Category = HospitalServiceCategories.Lab, IsActive = true });
			await host.Seed.SaveChangesAsync();
			int svc = (await host.Db.HospitalServices.AsNoTracking().SingleAsync(x => x.Code == "LAB-HB")).ID;
			host.Seed.HospitalLabAnalytes.Add(new HospitalLabAnalyte { CompanyId = 1, ServiceId = svc, Code = "HGB", Name = "هيموجلوبين", NameEn = "Haemoglobin", Unit = "g/dL", RefLow = 13.5m, RefHigh = 17.5m, CriticalLow = 7m, CriticalHigh = 20m, Decimals = 1, DeltaPercent = 20m, AutoVerify = true });
			await host.Seed.SaveChangesAsync();
			var a = await host.Db.HospitalLabAnalytes.AsNoTracking().SingleAsync();
			Assert.True((await lab.SaveRangeAsync(ctx, new HospitalLabRangeInput { AnalyteId = a.ID, Sex = "F", AgeFrom = 12, AgeUnit = "years", RefLow = 12.0m, RefHigh = 15.5m })).Ok);
			HospitalLabOrder O(string no) => new() { CompanyId = 1, AccessionNo = no, PatientId = pid, ServiceId = svc, Code = "LAB-HB", Name = "ه", NameEn = "Hb", Status = HospitalLabStatus.Received, OrderedAt = Now, ReceivedAt = Now, CollectedAt = Now };
			host.Seed.HospitalLabOrders.AddRange(O("LAB-000801"), O("LAB-000802"), O("LAB-000803"));
			await host.Seed.SaveChangesAsync();
			var ids = await host.Db.HospitalLabOrders.AsNoTracking().OrderBy(o => o.ID).Select(o => o.ID).ToListAsync();

			// 12.8 is low on the men's range but normal for her: released alone, flag N, her range kept on the result
			Assert.True((await lab.SaveResultsAsync(ctx, ids[0], new Dictionary<int, string?> { [a.ID] = "12.8" }, null, 7, Now)).Ok);
			var o1 = await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync(o => o.ID == ids[0]);
			Assert.Equal((HospitalLabStatus.Verified, true, (int?)null), (o1.Status, o1.AutoVerified, o1.VerifiedByEmployeeId));
			var r1 = await host.Db.HospitalLabResults.AsNoTracking().SingleAsync(r => r.LabOrderId == ids[0]);
			Assert.Equal(("N", 12.0m, 15.5m), (r1.Flag, r1.RefLow, r1.RefHigh));

			// 9.5 a day later: low and a 26 % fall — held for the specialist, with the delta flag
			Assert.True((await lab.SaveResultsAsync(ctx, ids[1], new Dictionary<int, string?> { [a.ID] = "9.5" }, null, 7, Now.AddDays(1))).Ok);
			var o2 = await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync(o => o.ID == ids[1]);
			Assert.Equal((HospitalLabStatus.Resulted, false), (o2.Status, o2.AutoVerified));
			var r2 = await host.Db.HospitalLabResults.AsNoTracking().SingleAsync(r => r.LabOrderId == ids[1]);
			Assert.Equal((true, 12.8m, "L"), (r2.DeltaFailed, r2.DeltaPrevious, r2.Flag));

			// 13.0: in range, but compared with the last VERIFIED value (12.8, not the held 9.5) — no jump: released
			Assert.True((await lab.SaveResultsAsync(ctx, ids[2], new Dictionary<int, string?> { [a.ID] = "13.0" }, null, 7, Now.AddDays(2))).Ok);
			Assert.True((await host.Db.HospitalLabOrders.AsNoTracking().SingleAsync(o => o.ID == ids[2])).AutoVerified);
		}

		[Fact]
		public void The_table_matches_its_slice()
		{
			var model = HospitalSliceParity.Model(HospitalLabRefRangeModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h233_lab_ranges_autoverify.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalLabRefRange), "HospitalLabRefRanges");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalLabRefRange), "HospitalLabRefRanges");
			foreach (var c in new[] { "DeltaPercent", "AutoVerify", "DeltaPrevious", "DeltaFailed", "AutoVerified" }) Assert.Contains(c, s);
		}
	}
}
