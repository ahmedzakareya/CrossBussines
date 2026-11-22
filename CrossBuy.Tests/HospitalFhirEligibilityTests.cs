using System.Text.Json.Nodes;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H242 — FHIR eligibility and pre-authorisation. What must never happen: a request without the patient or
	// the policy; a pre-authorisation sent as an ordinary claim; an error answer read as cover; a queued answer read as a
	// decision; an answer for one request applied to another; a gateway over plain http to the outside; a model naming
	// a table its slice does not create.
	// ==========================================================================================
	public class HospitalFhirEligibilityTests
	{
		private static readonly HospitalPatient P = new() { ID = 25, Mrn = "MRN-25", Name = "مريض", NameEn = "Patient", Sex = HospitalPatientSex.Male, BirthDate = new DateTime(1980, 1, 2) };
		private static readonly HospitalPatientPolicy Pol = new() { ID = 3, PolicyNo = "POL-9", ValidFrom = new DateTime(2026, 1, 1) };
		private static readonly HospitalPayer Payer = new() { ID = 2, Code = "GIG", Name = "جهة", NameEn = "GIG" };

		[Fact]
		public void The_eligibility_request_carries_the_patient_and_the_policy()
		{
			var r = JsonNode.Parse(HospitalFhirEligibility.EligibilityRequest(7, P, Pol, Payer, 1, new DateTime(2026, 11, 22)))!;
			Assert.Equal("CoverageEligibilityRequest", r["resourceType"]!.GetValue<string>());
			Assert.Equal("eligibility-7", r["id"]!.GetValue<string>());
			Assert.Equal("POL-9", r["contained"]![1]!["subscriberId"]!.GetValue<string>());
			Assert.Equal("MRN-25", r["contained"]![0]!["identifier"]![0]!["value"]!.GetValue<string>());
			Assert.Equal("GIG", r["insurer"]!["identifier"]!["value"]!.GetValue<string>());
		}

		[Fact]
		public void A_preauthorisation_is_a_claim_used_for_preauthorization()
		{
			var a = new HospitalPreauth { ID = 41, RequestNo = "PA-0041", Amount = 120m, RequestedAt = new DateTime(2026, 11, 20) };
			var r = JsonNode.Parse(HospitalFhirEligibility.PreauthClaim(a, P, Pol, Payer, "MRI-01", "MRI brain", "KWD", 1))!;
			Assert.Equal("Claim", r["resourceType"]!.GetValue<string>());
			Assert.Equal("preauthorization", r["use"]!.GetValue<string>());
			Assert.Equal("preauth-41", r["id"]!.GetValue<string>());
			Assert.Equal(120m, r["total"]!["value"]!.GetValue<decimal>());
			Assert.Equal(41, HospitalFhirEligibility.PreauthIdOf("Claim/preauth-41"));
			Assert.Null(HospitalFhirEligibility.PreauthIdOf("claim-41"));
		}

		[Fact]
		public void An_eligibility_answer_is_in_force_not_in_force_or_an_error()
		{
			var ok = HospitalFhirEligibility.ParseEligibility("""{"resourceType":"CoverageEligibilityResponse","outcome":"complete","disposition":"Active","insurance":[{"inforce":true,"item":[{"excluded":true,"productOrService":{"coding":[{"code":"DEN","display":"Dental"}]}}]}]}""");
			Assert.True(ok.Answer!.InForce); Assert.Contains("Dental", ok.Answer.Detail);
			Assert.False(HospitalFhirEligibility.ParseEligibility("""{"resourceType":"Bundle","entry":[{"resource":{"resourceType":"CoverageEligibilityResponse","insurance":[{"inforce":false}]}}]}""").Answer!.InForce);
			var err = HospitalFhirEligibility.ParseEligibility("""{"resourceType":"CoverageEligibilityResponse","outcome":"error","disposition":"Member not found"}""").Answer!;
			Assert.True(err.Error); Assert.Null(err.InForce);
			Assert.NotNull(HospitalFhirEligibility.ParseEligibility("""{"resourceType":"ClaimResponse"}""").Refusal);
			Assert.NotNull(HospitalFhirEligibility.ParseEligibility("not json").Refusal);
		}

		[Fact]
		public void A_preauthorisation_answer_approves_rejects_or_waits()
		{
			var ap = HospitalFhirEligibility.ParsePreauth("""{"resourceType":"ClaimResponse","request":{"reference":"Claim/preauth-41"},"outcome":"complete","preAuthRef":"AUTH-77"}""").Answer!;
			Assert.True(ap.Approved); Assert.Equal("AUTH-77", ap.ApprovalRef);
			var rj = HospitalFhirEligibility.ParsePreauth("""{"resourceType":"ClaimResponse","request":{"reference":"Claim/preauth-41"},"outcome":"complete","item":[{"itemSequence":1,"adjudication":[{"category":{"coding":[{"code":"benefit"}]},"amount":{"value":0},"reason":{"coding":[{"code":"NMN","display":"Not medically necessary"}]}}]}]}""").Answer!;
			Assert.False(rj.Approved); Assert.Equal("Not medically necessary", rj.Reason);
			var q = HospitalFhirEligibility.ParsePreauth("""{"resourceType":"ClaimResponse","outcome":"queued","disposition":"Under review"}""").Answer!;
			Assert.Null(q.Approved); Assert.False(q.Error);
			Assert.True(HospitalFhirEligibility.ParsePreauth("""{"resourceType":"ClaimResponse","outcome":"error"}""").Answer!.Error);
		}

		[Theory]
		[InlineData("https://fhir.payer.example/r4", "X-Api-Key", "k", null)]
		[InlineData("http://fhir.payer.example/r4", null, null, "Enter the gateway's https address")]
		[InlineData("http://localhost:8080/fhir", null, null, null)]
		[InlineData("not a url", null, null, "Enter the gateway's https address")]
		[InlineData("https://fhir.payer.example", null, "k", "Name the header that carries the key")]
		[InlineData("https://fhir.payer.example", "X Key", "k", "The header name has no spaces")]
		public void A_gateway_is_https_with_a_named_key_header(string url, string? header, string? key, string? refusal) =>
			Assert.Equal(refusal, HospitalFhirEligibility.GatewayRefusal(url, header, key));

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPayerGatewayModel.Configure);
		private static string Slice() => HospitalSliceParity.Slice("hospital_h242_fhir_eligibility.sql");

		[Theory]
		[InlineData(typeof(HospitalPayerGateway), "HospitalPayerGateways")]
		[InlineData(typeof(HospitalFhirMessage), "HospitalFhirMessages")]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table)
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);
		}
	}
}
