using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H238 — the intensive-care scores, infusion protocol and device bundles. What must never happen: a GCS
	// outside 3–15 or a part out of its scale; an APACHE II point taken from the wrong band; creatinine not doubled in
	// acute renal failure; a titration past the protocol's range or step, or with no reason; a pump rate without the
	// bag's strength; a bundle called complete with an item missing; a model naming a table its slice does not create.
	// ==========================================================================================
	public class HospitalIcuCareTests
	{
		[Fact]
		public void Glasgow_adds_its_three_parts_and_bands_the_total()
		{
			Assert.Equal(15, HospitalIcuCareRules.Gcs(4, 5, 6));
			Assert.Equal(3, HospitalIcuCareRules.Gcs(1, 1, 1));
			Assert.Equal("Severe", HospitalIcuCareRules.GcsBand(8));
			Assert.Equal("Moderate", HospitalIcuCareRules.GcsBand(9));
			Assert.Equal("Mild", HospitalIcuCareRules.GcsBand(13));
			Assert.NotNull(HospitalIcuCareRules.GcsRefusal(5, 5, 6));
			Assert.NotNull(HospitalIcuCareRules.GcsRefusal(4, 0, 6));
			Assert.NotNull(HospitalIcuCareRules.GcsRefusal(4, 5, 7));
			Assert.Null(HospitalIcuCareRules.GcsRefusal(1, 1, 1));
		}

		[Theory]
		[InlineData(41.0, 4)] [InlineData(39.0, 3)] [InlineData(38.6, 1)] [InlineData(37.0, 0)] [InlineData(35.0, 1)] [InlineData(33.0, 2)] [InlineData(31.0, 3)] [InlineData(29.0, 4)]
		public void Temperature_bands(double t, int pts) => Assert.Equal(pts, HospitalIcuCareRules.TempPoints((decimal)t));

		[Fact]
		public void Each_physiology_variable_takes_its_band()
		{
			Assert.Equal(4, HospitalIcuCareRules.MapPoints(160)); Assert.Equal(2, HospitalIcuCareRules.MapPoints(65)); Assert.Equal(0, HospitalIcuCareRules.MapPoints(85)); Assert.Equal(4, HospitalIcuCareRules.MapPoints(45));
			Assert.Equal(3, HospitalIcuCareRules.HrPoints(150)); Assert.Equal(3, HospitalIcuCareRules.HrPoints(45)); Assert.Equal(0, HospitalIcuCareRules.HrPoints(80));
			Assert.Equal(1, HospitalIcuCareRules.RrPoints(28)); Assert.Equal(2, HospitalIcuCareRules.RrPoints(8)); Assert.Equal(4, HospitalIcuCareRules.RrPoints(5));
			Assert.Equal(3, HospitalIcuCareRules.OxygenPoints(0.6m, 60, 400));   // FiO2 ≥ 0.5 reads the gradient
			Assert.Equal(3, HospitalIcuCareRules.OxygenPoints(0.4m, 58, 400));   // otherwise PaO2
			Assert.Equal(0, HospitalIcuCareRules.OxygenPoints(0.3m, 90, null));
			Assert.Equal(2, HospitalIcuCareRules.PhPoints(7.28m)); Assert.Equal(1, HospitalIcuCareRules.PhPoints(7.55m));
			Assert.Equal(2, HospitalIcuCareRules.NaPoints(125)); Assert.Equal(1, HospitalIcuCareRules.NaPoints(152));
			Assert.Equal(1, HospitalIcuCareRules.KPoints(3.2m)); Assert.Equal(3, HospitalIcuCareRules.KPoints(6.5m));
			Assert.Equal(3, HospitalIcuCareRules.CreatininePoints(2.5m, false));
			Assert.Equal(6, HospitalIcuCareRules.CreatininePoints(2.5m, true));   // doubled in acute renal failure
			Assert.Equal(2, HospitalIcuCareRules.HctPoints(25m)); Assert.Equal(1, HospitalIcuCareRules.WbcPoints(17m));
			Assert.Equal(0, HospitalIcuCareRules.AgePoints(44)); Assert.Equal(5, HospitalIcuCareRules.AgePoints(70)); Assert.Equal(6, HospitalIcuCareRules.AgePoints(80));
			Assert.Equal(5, HospitalIcuCareRules.ChronicPoints(true, "Emergency")); Assert.Equal(2, HospitalIcuCareRules.ChronicPoints(true, "Elective")); Assert.Equal(0, HospitalIcuCareRules.ChronicPoints(false, "Emergency"));
		}

		[Fact]
		public void Apache_ii_adds_physiology_age_and_chronic_health()
		{
			// septic shock: T 39.2 (3), MAP 55 (2), HR 125 (2), RR 30 (1), FiO2 0.4 / PaO2 65 (1), pH 7.28 (2), Na 138 (0),
			// K 4.0 (0), creatinine 2.1 with ARF (6), Hct 32 (0), WBC 22 (2), GCS 13 (2) = 21; age 68 (5); chronic, emergency (5) = 31
			var r = HospitalIcuCareRules.Apache(new HospitalApacheInput
			{
				TempC = 39.2m, Map = 55, HeartRate = 125, RespRate = 30, FiO2 = 0.4m, PaO2 = 65, Ph = 7.28m, Sodium = 138, Potassium = 4.0m,
				Creatinine = 2.1m, AcuteRenalFailure = true, Hematocrit = 32m, Wbc = 22m, Gcs = 13, Age = 68, ChronicHealth = true, Admission = "Emergency",
			});
			Assert.Equal(21, r.Physiology); Assert.Equal(5, r.AgePoints); Assert.Equal(5, r.ChronicPoints); Assert.Equal(31, r.Total);
			Assert.Equal(75, r.ApproxMortalityPct);
			Assert.Equal(0, HospitalIcuCareRules.Apache(new HospitalApacheInput()).Total);   // nothing entered counts as normal
			Assert.Contains("Creatinine=6", HospitalIcuCareRules.Detail(r.Parts));
			Assert.NotNull(HospitalIcuCareRules.ApacheRefusal(new HospitalApacheInput { Gcs = 2 }));
			Assert.NotNull(HospitalIcuCareRules.ApacheRefusal(new HospitalApacheInput { FiO2 = 21m }));
			Assert.NotNull(HospitalIcuCareRules.ApacheRefusal(new HospitalApacheInput { Admission = "Other" }));
		}

		[Fact]
		public void A_titration_stays_inside_the_protocol_and_says_why()
		{
			// noradrenaline 0.05–0.5 mcg/kg/min, steps of 0.05
			Assert.Null(HospitalIcuCareRules.ProtocolRefusal("Noradrenaline", "mcg/kg/min", 0.05m, 0.5m, 0.05m, 0.1m, "MAP ≥ 65"));
			Assert.NotNull(HospitalIcuCareRules.ProtocolRefusal("Noradrenaline", "drops", 0.05m, 0.5m, 0.05m, 0.1m, "MAP ≥ 65"));
			Assert.NotNull(HospitalIcuCareRules.ProtocolRefusal("Noradrenaline", "mcg/kg/min", 0.5m, 0.05m, 0.05m, 0.1m, "MAP ≥ 65"));
			Assert.NotNull(HospitalIcuCareRules.ProtocolRefusal("Noradrenaline", "mcg/kg/min", 0.05m, 0.5m, 0.05m, 0.6m, "MAP ≥ 65"));
			Assert.NotNull(HospitalIcuCareRules.ProtocolRefusal("Noradrenaline", "mcg/kg/min", 0.05m, 0.5m, 0.05m, 0.1m, " "));
			Assert.Null(HospitalIcuCareRules.TitrateRefusal(0.1m, 0.15m, 0.05m, 0.5m, 0.05m, "MAP 58"));
			Assert.Equal("One change is at most the protocol's step", HospitalIcuCareRules.TitrateRefusal(0.1m, 0.2m, 0.05m, 0.5m, 0.05m, "MAP 52"));
			Assert.Equal("The dose is outside the protocol's range", HospitalIcuCareRules.TitrateRefusal(0.5m, 0.55m, 0.05m, 0.5m, 0.05m, "MAP 50"));
			Assert.NotNull(HospitalIcuCareRules.TitrateRefusal(0.1m, 0.15m, 0.05m, 0.5m, 0.05m, null));
			Assert.NotNull(HospitalIcuCareRules.TitrateRefusal(0.1m, 0.1m, 0.05m, 0.5m, 0.05m, "same"));
		}

		[Fact]
		public void The_pump_rate_needs_the_bags_strength()
		{
			// 0.1 mcg/kg/min × 70 kg × 60 = 420 mcg/h; 4 mg in 50 mL = 80 mcg/mL → 5.3 mL/h
			Assert.Equal(5.3m, HospitalIcuCareRules.RateMlH(0.1m, "mcg/kg/min", 80m, 70m));
			Assert.Equal(2m, HospitalIcuCareRules.RateMlH(2m, "units/h", 1m, null));
			Assert.Null(HospitalIcuCareRules.RateMlH(0.1m, "mcg/kg/min", null, 70m));
			Assert.Null(HospitalIcuCareRules.RateMlH(0.1m, "mcg/kg/min", 80m, null));
		}

		[Fact]
		public void A_bundle_is_complete_only_with_every_item_and_device_days_count_the_first()
		{
			var vap = HospitalIcuCareRules.Bundles[HospitalIcuDeviceKind.Ventilator].Select(b => b.Code).ToList();
			Assert.Equal((string.Join(",", vap), true), HospitalIcuCareRules.Check(HospitalIcuDeviceKind.Ventilator, vap.AsEnumerable().Reverse().Append("NOPE")));
			Assert.False(HospitalIcuCareRules.Check(HospitalIcuDeviceKind.Ventilator, vap.Skip(1)).Complete);
			Assert.Equal(("", false), HospitalIcuCareRules.Check(HospitalIcuDeviceKind.CentralLine, null));
			Assert.All(HospitalIcuDeviceKind.All, k => Assert.NotEmpty(HospitalIcuCareRules.Bundles[k]));
			var ins = new DateTime(2026, 11, 20, 23, 0, 0);
			Assert.Equal(3, HospitalIcuCareRules.DeviceDays(ins, null, new DateTime(2026, 11, 22, 1, 0, 0)));
			Assert.Equal(1, HospitalIcuCareRules.DeviceDays(ins, ins.AddMinutes(30), ins.AddDays(5)));
			Assert.NotNull(HospitalIcuCareRules.DeviceRefusal("Drain", ins, ins, null));
			Assert.NotNull(HospitalIcuCareRules.DeviceRefusal(HospitalIcuDeviceKind.Ventilator, ins.AddHours(1), ins, null));
			Assert.Null(HospitalIcuCareRules.DeviceRefusal(HospitalIcuDeviceKind.Ventilator, ins, ins.AddHours(1), "ETT 7.5"));
		}

		// ------------------------------------------------------------------ the model against its slice, no database

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalIcuCareModel.Configure);
		private static string Slice() => HospitalSliceParity.Slice("hospital_h238_icu_care.sql");

		[Theory]
		[InlineData(typeof(HospitalIcuScore), "HospitalIcuScores")]
		[InlineData(typeof(HospitalIcuInfusion), "HospitalIcuInfusions")]
		[InlineData(typeof(HospitalIcuTitration), "HospitalIcuTitrations")]
		[InlineData(typeof(HospitalIcuDevice), "HospitalIcuDevices")]
		[InlineData(typeof(HospitalIcuBundleCheck), "HospitalIcuBundleChecks")]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table)
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);
		}
	}
}
