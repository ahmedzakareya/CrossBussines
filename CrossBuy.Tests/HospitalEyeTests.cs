using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H96 — the eye clinic, with no database: vision written as Snellen and kept as logMAR, and the glaucoma
	// suspicion rule (pressure, the difference between the eyes, the cup).
	// ==========================================================================================
	public class HospitalEyeTests
	{
		[Fact]
		public void Vision_is_written_as_Snellen_and_kept_as_logMAR()
		{
			Assert.Equal(0.48m, HospitalVision.LogMar("6/18"));
			Assert.Equal(0.30m, HospitalVision.LogMar("20/40"));
			Assert.Equal(0m, HospitalVision.LogMar("6/6"));
			Assert.Equal(-0.10m, HospitalVision.LogMar("6/4.8"));
			Assert.Equal(1.9m, HospitalVision.LogMar("cf"));
			Assert.Equal(2.3m, HospitalVision.LogMar("HM"));
			Assert.Null(HospitalVision.LogMar("good"));
			Assert.Null(HospitalVision.LogMar("6/0"));
			Assert.Equal("6/18", HospitalVision.Snellen6(0.48m));
			Assert.Equal("6/6", HospitalVision.Snellen6(0m));
			Assert.Equal("CF", HospitalVision.Snellen6(1.9m));
			Assert.Equal("6/9", HospitalVision.Snellen6(0.18m));   // a chart line, never "6/9.1"
			Assert.Equal("6/7.5", HospitalVision.Snellen6(0.1m));
			// through the form: a logMAR concept takes Snellen or a number, and refuses anything else
			var va = new HospitalConcept { ValueType = HospitalConceptType.Number, Unit = "logMAR", Decimals = 2 };
			Assert.Equal(0.48m, HospitalFormRules.Parse(va, "6/18").V);
			Assert.Equal(0.2m, HospitalFormRules.Parse(va, "0.2").V);
			Assert.Equal("Write the vision as Snellen (6/12, 20/40) or CF / HM", HospitalFormRules.Parse(va, "good").Error);
			// any other concept still takes numbers only
			Assert.Equal("Write a number", HospitalFormRules.Parse(new HospitalConcept { ValueType = HospitalConceptType.Number, Unit = "mmHg" }, "6/18").Error);
		}

		[Fact]
		public void Glaucoma_is_suspected_by_pressure_asymmetry_or_cup()
		{
			var g = HospitalVisitWidgetRules.Glaucoma(24, 19, 0.6m, 0.4m);   // the prototype's eye
			Assert.True(g.Suspect); Assert.Equal("R", g.Side);
			Assert.Equal(new[] { "iop", "iop-asym", "cup", "cup-asym" }, g.Reasons);
			Assert.False(HospitalVisitWidgetRules.Glaucoma(16, 17, 0.3m, 0.3m).Suspect);
			Assert.Equal(new[] { "iop-asym" }, HospitalVisitWidgetRules.Glaucoma(14, 19, null, null).Reasons);   // 5 apart, both under 21
			Assert.Equal("L", HospitalVisitWidgetRules.Glaucoma(14, 19, null, null).Side);
			Assert.Equal(new[] { "cup", "cup-asym" }, HospitalVisitWidgetRules.Glaucoma(null, null, 0.3m, 0.65m).Reasons);
			Assert.Equal("L", HospitalVisitWidgetRules.Glaucoma(null, null, 0.3m, 0.65m).Side);
		}
	}
}
