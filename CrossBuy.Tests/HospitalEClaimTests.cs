using System.Text.Json.Nodes;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H200 — electronic claims (FHIR R4). What must never happen: an answer recorded on another claim or another
	// line, a payer paying more than was claimed taken as paid, a rejection without a reason recorded, or a file we
	// could not read silently accepted.
	// ==========================================================================================
	public class HospitalEClaimTests
	{
		private static HospitalEClaimClaimData Data()
		{
			var claim = new HospitalClaim { ID = 41, BatchId = 7 };
			var lines = new List<HospitalClaimLine>
			{
				new() { ID = 302, ClaimId = 41, Code = "LAB-CBC", Description = "صورة دم", DescriptionEn = "CBC", Category = "Lab", Amount = 10m, InsurerAmount = 8m },
				new() { ID = 301, ClaimId = 41, Code = "CONS", Description = "كشف", DescriptionEn = "", Category = "Consultation", Amount = 20m, InsurerAmount = 16m, Justification = "Seen twice" },
			};
			var patient = new HospitalPatient { Mrn = "MRN-100025", Name = "مريض", NameEn = "Test Patient", Sex = HospitalPatientSex.Female, BirthDate = new DateTime(1980, 2, 3), NationalId = "280020300000" };
			var policy = new HospitalPatientPolicy { PolicyNo = "POL-9", MemberClass = "A", ValidFrom = new DateTime(2026, 1, 1) };
			var dx = new List<HospitalEncounterDiagnosis>
			{
				new() { Code = "E11.9", NameEn = "Type 2 diabetes", IsPrimary = false, SortOrder = 1 },
				new() { Code = "I10", NameEn = "Hypertension", IsPrimary = true, SortOrder = 2 },
			};
			return new HospitalEClaimClaimData(claim, "BILL-000123", new DateTime(2026, 11, 18), false, patient, policy, lines.OrderBy(l => l.ID).ToList(), dx, null, new[] { "APR-77" });
		}

		[Fact]
		public void A_claim_carries_the_patient_policy_diagnoses_and_one_item_per_line()
		{
			var payer = new HospitalPayer { Code = "MOH", Name = "وزارة", NameEn = "Ministry" };
			var c = HospitalFhirClaims.Claim(Data(), payer, "KWD", 1);
			Assert.Equal(("Claim", "claim-41", "professional"), (c["resourceType"]!.GetValue<string>(), c["id"]!.GetValue<string>(), c["type"]!["coding"]![0]!["code"]!.GetValue<string>()));
			Assert.Equal("BILL-000123", c["identifier"]![0]!["value"]!.GetValue<string>());
			var pat = c["contained"]![0]!;
			Assert.Equal(("female", "1980-02-03", "MRN-100025"), (pat["gender"]!.GetValue<string>(), pat["birthDate"]!.GetValue<string>(), pat["identifier"]![0]!["value"]!.GetValue<string>()));
			Assert.Equal("POL-9", c["contained"]![1]!["subscriberId"]!.GetValue<string>());
			// the primary diagnosis comes first, marked principal
			Assert.Equal(("I10", "principal"), (c["diagnosis"]![0]!["diagnosisCodeableConcept"]!["coding"]![0]!["code"]!.GetValue<string>(), c["diagnosis"]![0]!["type"]![0]!["coding"]![0]!["code"]!.GetValue<string>()));
			Assert.Equal("APR-77", c["insurance"]![0]!["preAuthRef"]![0]!.GetValue<string>());
			// items in line-id order: sequence 1 is line 301; each names its line; net = the payer's share
			var items = c["item"]!.AsArray();
			Assert.Equal(2, items.Count);
			Assert.Equal((1, 301, 16m, "CONS"), (items[0]!["sequence"]!.GetValue<int>(), items[0]!["extension"]![0]!["valueInteger"]!.GetValue<int>(), items[0]!["net"]!["value"]!.GetValue<decimal>(), items[0]!["productOrService"]!["coding"]![0]!["code"]!.GetValue<string>()));
			Assert.Equal("كشف", items[0]!["productOrService"]!["coding"]![0]!["display"]!.GetValue<string>());   // no English: the Arabic
			Assert.Equal(24m, c["total"]!["value"]!.GetValue<decimal>());
			Assert.Equal("KWD", c["total"]!["currency"]!.GetValue<string>());
			var bundle = JsonNode.Parse(HospitalFhirClaims.Bundle("CLB-1", new[] { c }, new DateTime(2026, 11, 18, 10, 0, 0)))!;
			Assert.Equal(("Bundle", "collection", "Claim/claim-41"), (bundle["resourceType"]!.GetValue<string>(), bundle["type"]!.GetValue<string>(), bundle["entry"]![0]!["fullUrl"]!.GetValue<string>()));
		}

		private const string Answer = """
		{ "resourceType": "Bundle", "type": "collection", "entry": [ { "resource": {
		  "resourceType": "ClaimResponse", "outcome": "complete", "request": { "reference": "Claim/claim-41" },
		  "item": [
		    { "itemSequence": 1, "adjudication": [ { "category": { "coding": [ { "code": "submitted" } ] }, "amount": { "value": 16 } },
		                                           { "category": { "coding": [ { "code": "benefit" } ] }, "amount": { "value": 12.5 } } ] },
		    { "itemSequence": 2, "adjudication": [ { "category": { "coding": [ { "code": "benefit" } ] }, "amount": { "value": 0 },
		                                             "reason": { "coding": [ { "code": "MN-1", "display": "Not medically necessary" } ] } } ] }
		  ] } } ] }
		""";

		[Fact]
		public void An_answer_file_is_read_item_by_item()
		{
			var (a, why) = HospitalFhirClaims.ParseResponses(Answer);
			Assert.Null(why);
			var r = Assert.Single(a!);
			Assert.Equal(41, HospitalFhirClaims.ClaimIdOf(r.Request));
			Assert.Equal(new HospitalFhirAdjudication(1, 12.5m, null, null), r.Items[0]);
			Assert.Equal(new HospitalFhirAdjudication(2, 0m, "MN-1", "Not medically necessary"), r.Items[1]);
			Assert.Equal("The file is empty", HospitalFhirClaims.ParseResponses(" ").Refusal);
			Assert.Equal("This is not a FHIR JSON file", HospitalFhirClaims.ParseResponses("MSH|^~\\&|").Refusal);
			Assert.Equal("The file holds no claim responses (ClaimResponse)", HospitalFhirClaims.ParseResponses("{\"resourceType\":\"Claim\"}").Refusal);
			Assert.Null(HospitalFhirClaims.ClaimIdOf("Claim/41"));
			Assert.Equal(7, HospitalFhirClaims.ClaimIdOf("#claim-7"));
		}

		[Fact]
		public void Each_item_accepts_rejects_or_is_left_with_its_reason()
		{
			Assert.Equal(new HospitalEClaimDecision(true, true, null, null, null, null), HospitalFhirClaims.Decide(new(1, 16m, null, null), 16m));        // in full
			Assert.Equal(new HospitalEClaimDecision(true, true, 12.5m, null, null, null), HospitalFhirClaims.Decide(new(1, 12.5m, null, null), 16m));    // part
			Assert.Equal(HospitalEClaimSkip.OverClaimed, HospitalFhirClaims.Decide(new(1, 16.001m, null, null), 16m).Skip);
			Assert.Equal(new HospitalEClaimDecision(true, false, null, "MN-1", "Not medically necessary", null), HospitalFhirClaims.Decide(new(2, 0m, "MN-1", "Not medically necessary"), 16m));
			Assert.Equal(new HospitalEClaimDecision(true, false, null, "-", "Duplicate", null), HospitalFhirClaims.Decide(new(2, null, null, "Duplicate"), 16m));
			Assert.Equal(HospitalEClaimSkip.NoReason, HospitalFhirClaims.Decide(new(2, 0m, null, null), 16m).Skip);
			Assert.Equal(HospitalEClaimSkip.NoDecision, HospitalFhirClaims.Decide(new(2, null, null, null), 16m).Skip);
		}

		[Fact]
		public void The_table_matches_its_slice()
		{
			var model = HospitalSliceParity.Model(HospitalClaimExchangeModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h200_eclaims.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalClaimExchange), "HospitalClaimExchanges");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalClaimExchange), "HospitalClaimExchanges");
			foreach (var d in HospitalClaimExchangeDirection.All) Assert.Contains("'" + d + "'", s);
		}
	}
}
