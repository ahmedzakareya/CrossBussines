using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H218 — cardiac studies. What must never happen: a QTc typed instead of calculated, a critical rate,
	// QTc or ejection fraction not flagged, an ECG reported without its rate and rhythm, an echo without its ejection
	// fraction, or a study reported twice.
	// ==========================================================================================
	public class HospitalCardiologyTests
	{
		[Theory]
		[InlineData(400, 60, 400)]    // RR 1 s: QTc = QT
		[InlineData(360, 100, 465)]   // RR 0.6 s
		[InlineData(440, 75, 492)]    // RR 0.8 s
		public void QTc_is_calculated_by_Bazett(int qt, int rate, int qtc) => Assert.Equal(qtc, HospitalCardioRules.Qtc(qt, rate));

		[Fact]
		public void QTc_needs_both_the_QT_and_the_rate() { Assert.Null(HospitalCardioRules.Qtc(null, 70)); Assert.Null(HospitalCardioRules.Qtc(400, null)); }

		[Fact]
		public void Critical_findings_are_flagged_by_the_system()
		{
			Assert.Equal("", HospitalCardioRules.Critical(HospitalCardioKind.Ecg, 72, 430, null));
			Assert.Equal("HR<40", HospitalCardioRules.Critical(HospitalCardioKind.Ecg, 35, 430, null));
			Assert.Equal("HR>150", HospitalCardioRules.Critical(HospitalCardioKind.Ecg, 170, 430, null));
			Assert.Equal("QTc>=500", HospitalCardioRules.Critical(HospitalCardioKind.Ecg, 72, 500, null));
			Assert.Equal("EF<30", HospitalCardioRules.Critical(HospitalCardioKind.Echo, null, null, 25));
			Assert.Equal("", HospitalCardioRules.Critical(HospitalCardioKind.Echo, null, null, 55));
		}

		[Fact]
		public void A_report_has_what_its_study_needs_and_is_signed_once()
		{
			var ecg = new HospitalCardioStudy { Kind = HospitalCardioKind.Ecg, Status = HospitalCardioStatus.Requested };
			Assert.Equal("Write the heart rate", HospitalCardioRules.ReportRefusal(ecg, new() { Interpretation = "x" }));
			Assert.Equal("Write the rhythm", HospitalCardioRules.ReportRefusal(ecg, new() { Interpretation = "x", HeartRate = 70 }));
			Assert.Equal("A measurement is outside what an ECG can show", HospitalCardioRules.ReportRefusal(ecg, new() { Interpretation = "x", HeartRate = 70, Rhythm = "Sinus", QtMs = 900 }));
			Assert.Null(HospitalCardioRules.ReportRefusal(ecg, new() { Interpretation = "Normal sinus rhythm", HeartRate = 70, Rhythm = "Sinus", QtMs = 400 }));
			var echo = new HospitalCardioStudy { Kind = HospitalCardioKind.Echo, Status = HospitalCardioStatus.Requested };
			Assert.Equal("Write the ejection fraction (5 to 90%)", HospitalCardioRules.ReportRefusal(echo, new() { Interpretation = "x" }));
			Assert.Null(HospitalCardioRules.ReportRefusal(echo, new() { Interpretation = "Normal LV function", EfPercent = 60 }));
			echo.Status = HospitalCardioStatus.Reported;
			Assert.Equal("This study is already reported", HospitalCardioRules.ReportRefusal(echo, new() { Interpretation = "x", EfPercent = 60 }));
			Assert.Equal("Write the interpretation", HospitalCardioRules.ReportRefusal(ecg, new() { HeartRate = 70, Rhythm = "Sinus" }));
		}

		[Fact]
		public void The_table_matches_its_slice()
		{
			var model = HospitalSliceParity.Model(HospitalCardiologyModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h218_cardiology.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalCardioStudy), "HospitalCardioStudies");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalCardioStudy), "HospitalCardioStudies");
			foreach (var x in HospitalCardioKind.All.Concat(HospitalCardioStatus.All)) Assert.Contains("'" + x + "'", s);
		}
	}
}
