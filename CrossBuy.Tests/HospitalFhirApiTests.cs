using System.Text.Json.Nodes;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H248 — the FHIR R4 clinical read API's resources. What must never happen: a patient without the MRN; a
	// stay shown finished while open; a result without its value, range or interpretation; a report without its
	// accession; an allergy list lost line by line; a search bundle whose count disagrees with its entries.
	// ==========================================================================================
	public class HospitalFhirApiTests
	{
		private static readonly HospitalPatient P = new() { ID = 25, Mrn = "MRN-25", Name = "سارة علي", NameEn = "Sara Ali", Sex = HospitalPatientSex.Female, BirthDate = new DateTime(1990, 3, 4), Allergies = "Penicillin\nLatex", Phone = "+96550000000" };

		[Fact]
		public void A_patient_carries_identifiers_both_names_and_demographics()
		{
			var r = HospitalFhirResources.Patient(P);
			Assert.Equal("25", r["id"]!.GetValue<string>());
			Assert.Equal("MRN-25", r["identifier"]![0]!["value"]!.GetValue<string>());
			Assert.Equal("Sara Ali", r["name"]![0]!["text"]!.GetValue<string>());
			Assert.Equal("سارة علي", r["name"]![1]!["text"]!.GetValue<string>());
			Assert.Equal("female", r["gender"]!.GetValue<string>());
			Assert.Equal("1990-03-04", r["birthDate"]!.GetValue<string>());
		}

		[Fact]
		public void A_stay_is_in_progress_until_discharged()
		{
			var a = new HospitalAdmission { ID = 11, PatientId = 25, Status = HospitalAdmissionStatus.Active, AdmittedAt = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc), Diagnosis = "Pneumonia" };
			var e = HospitalFhirResources.Encounter(a);
			Assert.Equal("in-progress", e["status"]!.GetValue<string>());
			Assert.Equal("IMP", e["class"]!["code"]!.GetValue<string>());
			Assert.Equal("2026-11-20T08:00:00Z", e["period"]!["start"]!.GetValue<string>());
			Assert.Null(e["period"]!["end"]);
			a.Status = HospitalAdmissionStatus.Discharged; a.DischargedAt = new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);
			Assert.Equal("finished", HospitalFhirResources.Encounter(a)["status"]!.GetValue<string>());
		}

		[Fact]
		public void A_result_carries_value_range_and_interpretation()
		{
			var o = new HospitalLabOrder { ID = 40, PatientId = 25, AccessionNo = "LAB-000040", Code = "BMP", Name = "لوحة", NameEn = "Basic panel", OrderedAt = new DateTime(2026, 11, 23, 6, 0, 0), CollectedAt = new DateTime(2026, 11, 23, 7, 0, 0), VerifiedAt = new DateTime(2026, 11, 23, 9, 0, 0) };
			var r = new HospitalLabResult { ID = 401, LabOrderId = 40, Name = "بوتاسيوم", NameEn = "Potassium", Unit = "mmol/L", Value = 6.234m, Decimals = 1, RefLow = 3.5m, RefHigh = 5.1m, Flag = HospitalLabFlag.CriticalHigh };
			var obs = HospitalFhirResources.Observation(r, o, "K");
			Assert.Equal(6.2m, obs["valueQuantity"]!["value"]!.GetValue<decimal>());
			Assert.Equal(5.1m, obs["referenceRange"]![0]!["high"]!["value"]!.GetValue<decimal>());
			Assert.Equal("HH", obs["interpretation"]![0]!["coding"]![0]!["code"]!.GetValue<string>());
			Assert.Equal("K", obs["code"]!["coding"]![0]!["code"]!.GetValue<string>());
			var rep = HospitalFhirResources.LabReport(o, new[] { 401 });
			Assert.Equal("LAB-000040", rep["identifier"]![0]!["value"]!.GetValue<string>());
			Assert.Equal("Observation/401", rep["result"]![0]!["reference"]!.GetValue<string>());
		}

		[Fact]
		public void Allergies_are_one_resource_per_line_and_a_bundle_counts_its_entries()
		{
			var al = HospitalFhirResources.Allergies(P).ToList();
			Assert.Equal(2, al.Count);
			Assert.Equal("Latex", al[1]["code"]!["text"]!.GetValue<string>());
			var b = HospitalFhirResources.Bundle(al, "/x");
			Assert.Equal(2, b["total"]!.GetValue<int>());
			Assert.Equal("AllergyIntolerance/25-1", b["entry"]![0]!["fullUrl"]!.GetValue<string>());
			Assert.Equal("searchset", b["type"]!.GetValue<string>());
		}

		[Fact]
		public void A_medication_request_says_its_dose_route_and_status()
		{
			var m = new HospitalMedOrder { ID = 9, PatientId = 25, AdmissionId = 11, Drug = "Ceftriaxone 2 g", Dose = "2 g", Route = HospitalMedRoute.Intravenous, Hours = "08", Status = HospitalMedOrderStatus.Stopped, CreatedAt = new DateTime(2026, 11, 20) };
			var r = HospitalFhirResources.MedicationRequest(m);
			Assert.Equal("stopped", r["status"]!.GetValue<string>());
			Assert.Equal("2 g IV at 08", r["dosageInstruction"]![0]!["text"]!.GetValue<string>());
			Assert.Equal("Encounter/11", r["encounter"]!["reference"]!.GetValue<string>());
		}
	}
}
