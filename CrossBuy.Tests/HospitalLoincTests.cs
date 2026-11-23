using System.Text.Json.Nodes;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H261 — LOINC on lab analytes. What must never happen: a mistyped code accepted (its check digit is the
	// guard); a starter code that is itself wrong; the local code lost from what is sent; a model column missing from
	// its slice.
	// ==========================================================================================
	public class HospitalLoincTests
	{
		[Theory]
		[InlineData("2345", 7)]
		[InlineData("718", 7)]
		[InlineData("10839", 9)]
		[InlineData("62292", 8)]
		public void The_check_digit_is_the_mod10_of_the_number(string number, int digit) => Assert.Equal(digit, HospitalLoinc.CheckDigit(number));

		[Fact]
		public void A_mistyped_code_is_refused_and_every_starter_code_is_valid()
		{
			Assert.True(HospitalLoinc.Valid("2345-7"));
			Assert.False(HospitalLoinc.Valid("2345-8"));
			Assert.False(HospitalLoinc.Valid("2345"));
			Assert.False(HospitalLoinc.Valid("23a5-7"));
			Assert.False(HospitalLoinc.Valid("12345678-9"));
			Assert.Null(HospitalLoinc.Refusal(null));
			Assert.Null(HospitalLoinc.Refusal(" "));
			Assert.NotNull(HospitalLoinc.Refusal("718-8"));
			foreach (var (code, loinc) in HospitalLoinc.Starter) Assert.True(HospitalLoinc.Valid(loinc), code + " → " + loinc);
		}

		[Fact]
		public void The_result_message_carries_LOINC_first_and_keeps_the_local_code()
		{
			var p = new HospitalPatient { ID = 25, Mrn = "MRN-25", Name = "مريض", NameEn = "Sara Ali", Sex = HospitalPatientSex.Female };
			var res = new[]
			{
				new HospitalHl7Result("K", "Potassium", "6.2", "NM", "mmol/L", "3.5-5.1", "HH", "2823-3"),
				new HospitalHl7Result("COM", "Comment", "Haemolysed", "ST", null, null, null),
			};
			var seg = HospitalHl7Out.Oru("C1", p, "LAB-1", "BMP", "Basic panel", null, new DateTime(2026, 11, 23, 9, 0, 0), res, null, null, "CB1", new DateTime(2026, 11, 23, 9, 30, 0)).TrimEnd('\r').Split('\r');
			Assert.Equal("OBX|1|NM|2823-3^Potassium^LN^K^Potassium^L||6.2|mmol/L|3.5-5.1|HH|||F", seg[3]);
			Assert.Equal("OBX|2|ST|COM^Comment||Haemolysed||||||F", seg[4]);   // no LOINC: as before
		}

		[Fact]
		public void The_FHIR_observation_codes_LOINC_first()
		{
			var r = new HospitalLabResult { ID = 3, LabOrderId = 1, Name = "البوتاسيوم", NameEn = "Potassium", Value = 4.1m, Decimals = 1, Unit = "mmol/L" };
			var o = new HospitalLabOrder { ID = 1, PatientId = 25, OrderedAt = DateTime.UtcNow, VerifiedAt = DateTime.UtcNow };
			var coding = HospitalFhirResources.Observation(r, o, "K", "2823-3")["code"]!["coding"]!.AsArray();
			Assert.Equal("http://loinc.org", (string?)coding[0]!["system"]);
			Assert.Equal("2823-3", (string?)coding[0]!["code"]);
			Assert.Equal("K", (string?)coding[1]!["code"]);
			Assert.Single(HospitalFhirResources.Observation(r, o, "K")["code"]!["coding"]!.AsArray());
		}

		[Fact]
		public void The_column_is_in_its_slice()
		{
			var s = HospitalSliceParity.Slice("hospital_h261_lab_loinc.sql");
			Assert.Contains("ADD Loinc nvarchar(10) NULL", s);
			Assert.Equal(10, HospitalSliceParity.Model(HospitalLabModel.Configure).FindEntityType(typeof(HospitalLabAnalyte))!.FindProperty("Loinc")!.GetMaxLength());
		}
	}
}
