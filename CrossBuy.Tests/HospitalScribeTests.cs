using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H254 — ambient documentation, the drafting half. What must never happen: a spoken vital sign read into the
	// wrong field, or an impossible one read at all; Arabic-Indic digits missed; a plan sentence left in history; an
	// empty transcript producing text; a model naming a table its slice does not create.
	// ==========================================================================================
	public class HospitalScribeTests
	{
		[Fact]
		public void An_arabic_visit_is_drafted_into_soap_and_vitals()
		{
			var d = HospitalScribeRules.Draft("المريض يشكو من ألم في البطن منذ يومين. لا يوجد قيء. بالفحص البطن لين مع ألم خفيف في الجهة اليمنى. الضغط ١٣٠ على ٨٥ والنبض 92 والحرارة 38.2. الخطة: سنطلب صورة دم كاملة وأشعة صوتية.");
			Assert.Contains("يشكو من ألم في البطن", d.ChiefComplaint);
			Assert.Contains("لا يوجد قيء", d.History);
			Assert.Contains("البطن لين", d.Examination);
			Assert.Contains("سنطلب", d.Plan);
			Assert.Equal((130, 85, 92, 38.2m), (d.BpSystolic!.Value, d.BpDiastolic!.Value, d.HeartRate!.Value, d.TemperatureC!.Value));
		}

		[Fact]
		public void An_english_visit_is_drafted_too()
		{
			var d = HospitalScribeRules.Draft("Patient complains of cough for a week. Non-smoker. On examination chest is clear. Saturation 97, pulse 88. Plan: prescribe salbutamol and follow up in one week.");
			Assert.StartsWith("Patient complains of cough", d.ChiefComplaint);
			Assert.StartsWith("Non-smoker", d.History);
			Assert.Contains("chest is clear", d.Examination);
			Assert.Contains("salbutamol", d.Plan);
			Assert.Equal(97, d.SpO2); Assert.Equal(88, d.HeartRate);
			Assert.Null(d.BpSystolic);
		}

		[Fact]
		public void A_sentence_of_readings_is_examination_not_history()
		{
			// the live microphone test's transcript: the readings sentence had landed in the history
			var d = HospitalScribeRules.Draft("The patient complains of a dry cough and fever for three days. On examination the chest is clear and the throat is red. Blood pressure is 128 over 82, pulse 92, temperature 38.4 degrees. Plan, paracetamol, plenty of fluids, and review in three days if the fever continues.");
			Assert.DoesNotContain("Blood pressure", d.History);
			Assert.Contains("Blood pressure is 128 over 82", d.Examination);
			Assert.Equal((128, 82, 92, 38.4m), (d.BpSystolic!.Value, d.BpDiastolic!.Value, d.HeartRate!.Value, d.TemperatureC!.Value));
			var a = HospitalScribeRules.Draft("يشكو من صداع. الضغط ١٤٠ على ٩٠. لا يدخن.");
			Assert.Contains("الضغط 140 على 90", a.Examination); Assert.Equal("لا يدخن", a.History);
			// a complaint that names a reading stays the complaint
			Assert.StartsWith("يشكو من حرارة 39", HospitalScribeRules.Draft("يشكو من حرارة 39 منذ أمس.").ChiefComplaint);
		}

		[Fact]
		public void Impossible_readings_and_empty_words_give_nothing()
		{
			var d = HospitalScribeRules.Draft("الحرارة 98 والنبض 400");
			Assert.Null(d.TemperatureC); Assert.Null(d.HeartRate);
			var e = HospitalScribeRules.Draft("  ");
			Assert.Null(e.ChiefComplaint); Assert.Equal("", e.History); Assert.Equal("", e.Plan);
			Assert.Equal("120/80", HospitalScribeRules.LatinDigits("١٢٠/٨٠"));
		}

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
		{
			var model = HospitalSliceParity.Model(HospitalScribeModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h254_scribe.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalScribeLog), "HospitalScribeLogs");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalScribeLog), "HospitalScribeLogs");
		}
	}
}
