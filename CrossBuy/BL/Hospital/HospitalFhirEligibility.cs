using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalEligibilityAnswer(bool? InForce, string? Detail, bool Error);
	public sealed record HospitalPreauthAnswer(string Request, bool? Approved, string? ApprovalRef, string? Reason, bool Error);

	// ==========================================================================================
	// HOSPITAL H242 — the FHIR R4 messages of eligibility and pre-authorisation, free of the database.
	//   · CoverageEligibilityRequest: purpose "validation" + "benefits", the patient and the coverage contained.
	//   · A pre-authorisation is a Claim with use "preauthorization", id "preauth-{id}", one item for the service.
	//   · CoverageEligibilityResponse: outcome "error" → error; insurance[0].inforce → in force or not; the disposition
	//     and any excluded benefit become the detail.
	//   · ClaimResponse to a pre-authorisation: outcome "error" → error; preAuthRef → approved; an item adjudication with
	//     a benefit → approved; a reason without a benefit → rejected with it; "queued"/"partial" with neither → pending.
	// ==========================================================================================
	public static class HospitalFhirEligibility
	{
		private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
		private static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		private static string En(string? en, string ar) => string.IsNullOrWhiteSpace(en) ? ar : en;
		private static JsonObject Concept(string system, string code, string? display = null)
		{
			var c = new JsonObject { ["system"] = system, ["code"] = code };
			if (!string.IsNullOrWhiteSpace(display)) c["display"] = display;
			return new JsonObject { ["coding"] = new JsonArray(c) };
		}
		private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

		public static string PreauthRef(int id) => "preauth-" + id.ToString(CultureInfo.InvariantCulture);
		public static int? PreauthIdOf(string? request)
		{
			if (string.IsNullOrWhiteSpace(request)) return null;
			int i = request.LastIndexOf("preauth-", StringComparison.OrdinalIgnoreCase);
			return i >= 0 && int.TryParse(request.AsSpan(i + 8), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
		}

		private static (JsonObject Patient, JsonObject Coverage) Contained(HospitalPatient p, HospitalPatientPolicy policy, HospitalPayer payer)
		{
			var patient = new JsonObject
			{
				["resourceType"] = "Patient", ["id"] = "patient",
				["identifier"] = new JsonArray(new JsonObject { ["system"] = HospitalFhirClaims.Sys + "mrn", ["value"] = p.Mrn }),
				["name"] = new JsonArray(new JsonObject { ["text"] = En(p.NameEn, p.Name) }),
				["gender"] = p.Sex == HospitalPatientSex.Female ? "female" : p.Sex == HospitalPatientSex.Male ? "male" : "unknown",
			};
			if (!string.IsNullOrWhiteSpace(p.NationalId)) patient["identifier"]!.AsArray().Add(new JsonObject { ["system"] = "urn:crossbusiness:national-id", ["value"] = p.NationalId });
			if (p.BirthDate is DateTime bd) patient["birthDate"] = Day(bd);
			var coverage = new JsonObject
			{
				["resourceType"] = "Coverage", ["id"] = "coverage", ["status"] = "active", ["subscriberId"] = policy.PolicyNo,
				["beneficiary"] = new JsonObject { ["reference"] = "#patient" },
				["payor"] = new JsonArray(new JsonObject { ["identifier"] = new JsonObject { ["system"] = HospitalFhirClaims.Sys + "payer", ["value"] = payer.Code }, ["display"] = En(payer.NameEn, payer.Name) }),
			};
			return (patient, coverage);
		}

		private static JsonObject Insurer(HospitalPayer payer) => new() { ["identifier"] = new JsonObject { ["system"] = HospitalFhirClaims.Sys + "payer", ["value"] = payer.Code }, ["display"] = En(payer.NameEn, payer.Name) };
		private static JsonObject Provider(int companyId) => new() { ["identifier"] = new JsonObject { ["system"] = "urn:crossbusiness:company", ["value"] = companyId.ToString(CultureInfo.InvariantCulture) } };

		public static string EligibilityRequest(int messageId, HospitalPatient p, HospitalPatientPolicy policy, HospitalPayer payer, int companyId, DateTime nowUtc)
		{
			var (patient, coverage) = Contained(p, policy, payer);
			var r = new JsonObject
			{
				["resourceType"] = "CoverageEligibilityRequest", ["id"] = "eligibility-" + messageId.ToString(CultureInfo.InvariantCulture),
				["contained"] = new JsonArray(patient, coverage), ["status"] = "active",
				["purpose"] = new JsonArray("validation", "benefits"),
				["patient"] = new JsonObject { ["reference"] = "#patient" },
				["servicedDate"] = Day(nowUtc), ["created"] = Day(nowUtc),
				["insurer"] = Insurer(payer), ["provider"] = Provider(companyId),
				["insurance"] = new JsonArray(new JsonObject { ["focal"] = true, ["coverage"] = new JsonObject { ["reference"] = "#coverage" } }),
			};
			return r.ToJsonString(Pretty);
		}

		public static string PreauthClaim(HospitalPreauth a, HospitalPatient p, HospitalPatientPolicy policy, HospitalPayer payer, string serviceCode, string serviceName, string currency, int companyId)
		{
			var (patient, coverage) = Contained(p, policy, payer);
			var money = new JsonObject { ["value"] = a.Amount, ["currency"] = currency };
			var r = new JsonObject
			{
				["resourceType"] = "Claim", ["id"] = PreauthRef(a.ID),
				["contained"] = new JsonArray(patient, coverage),
				["identifier"] = new JsonArray(new JsonObject { ["system"] = HospitalFhirClaims.Sys + "preauth", ["value"] = a.RequestNo }),
				["status"] = "active", ["use"] = "preauthorization",
				["type"] = Concept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
				["patient"] = new JsonObject { ["reference"] = "#patient" }, ["created"] = Day(a.RequestedAt),
				["insurer"] = Insurer(payer), ["provider"] = Provider(companyId),
				["priority"] = Concept("http://terminology.hl7.org/CodeSystem/processpriority", "normal"),
				["insurance"] = new JsonArray(new JsonObject { ["sequence"] = 1, ["focal"] = true, ["coverage"] = new JsonObject { ["reference"] = "#coverage" } }),
				["item"] = new JsonArray(new JsonObject { ["sequence"] = 1, ["productOrService"] = Concept(HospitalFhirClaims.Sys + "service", serviceCode, serviceName), ["net"] = money }),
				["total"] = money.DeepClone(),
			};
			return r.ToJsonString(Pretty);
		}

		private static JsonObject? Resource(string? raw, string type, out string? refusal)
		{
			refusal = null;
			if (string.IsNullOrWhiteSpace(raw)) { refusal = "The file is empty"; return null; }
			JsonNode? root;
			try { root = JsonNode.Parse(raw); } catch (JsonException) { refusal = "This is not a FHIR JSON file"; return null; }
			if (root is not JsonObject o) { refusal = "This is not a FHIR JSON file"; return null; }
			if (Str(o["resourceType"]) == type) return o;
			if (Str(o["resourceType"]) == "Bundle")
				foreach (var e in o["entry"] as JsonArray ?? new JsonArray())
					if (e?["resource"] is JsonObject r && Str(r["resourceType"]) == type) return r;
			refusal = type == "ClaimResponse" ? "The file holds no claim responses (ClaimResponse)" : "The file holds no eligibility answer (CoverageEligibilityResponse)";
			return null;
		}

		public static (HospitalEligibilityAnswer? Answer, string? Refusal) ParseEligibility(string? raw)
		{
			var r = Resource(raw, "CoverageEligibilityResponse", out var why);
			if (r == null) return (null, why);
			string? disposition = Str(r["disposition"]);
			if (Str(r["outcome"]) == "error")
				return (new HospitalEligibilityAnswer(null, disposition ?? Str(r["error"]?[0]?["code"]?["coding"]?[0]?["code"]) ?? "error", true), null);
			bool? inforce = r["insurance"]?[0]?["inforce"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
			var excluded = new List<string>();
			foreach (var it in r["insurance"]?[0]?["item"] as JsonArray ?? new JsonArray())
				if (it?["excluded"] is JsonValue ev && ev.TryGetValue<bool>(out var ex) && ex)
					excluded.Add(Str(it["productOrService"]?["coding"]?[0]?["display"]) ?? Str(it["productOrService"]?["coding"]?[0]?["code"]) ?? Str(it["name"]) ?? "?");
			var detail = string.Join(" · ", new[] { disposition, excluded.Count > 0 ? "Excluded: " + string.Join(", ", excluded) : null }.Where(x => x != null));
			return (new HospitalEligibilityAnswer(inforce, detail.Length == 0 ? null : detail, false), null);
		}

		public static (HospitalPreauthAnswer? Answer, string? Refusal) ParsePreauth(string? raw)
		{
			var r = Resource(raw, "ClaimResponse", out var why);
			if (r == null) return (null, why);
			string request = Str(r["request"]?["reference"]) ?? Str(r["request"]?["identifier"]?["value"]) ?? "";
			string? disposition = Str(r["disposition"]);
			if (Str(r["outcome"]) == "error") return (new HospitalPreauthAnswer(request, null, null, disposition ?? "error", true), null);
			string? preAuthRef = Str(r["preAuthRef"]);
			decimal? benefit = null; string? reason = null;
			foreach (var it in r["item"] as JsonArray ?? new JsonArray())
				foreach (var a in it?["adjudication"] as JsonArray ?? new JsonArray())
				{
					if (Str(a?["category"]?["coding"]?[0]?["code"]) == "benefit" && a?["amount"]?["value"] is JsonNode v && decimal.TryParse(v.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) benefit = d;
					reason ??= Str(a?["reason"]?["coding"]?[0]?["display"]) ?? Str(a?["reason"]?["coding"]?[0]?["code"]) ?? Str(a?["reason"]?["text"]);
				}
			if (preAuthRef != null || benefit is > 0) return (new HospitalPreauthAnswer(request, true, preAuthRef ?? request, null, false), null);
			if (reason != null || benefit == 0) return (new HospitalPreauthAnswer(request, false, null, reason ?? disposition ?? "Rejected by the payer", false), null);
			return (new HospitalPreauthAnswer(request, null, null, disposition, false), null);   // queued: still pending
		}

		/// The gateway address is an https URL (a plain http one only to this machine, for a test exchange).
		public static string? GatewayRefusal(string? baseUrl, string? keyHeader, string? apiKey)
		{
			if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)))
				return "Enter the gateway's https address";
			if (!string.IsNullOrWhiteSpace(apiKey) && string.IsNullOrWhiteSpace(keyHeader)) return "Name the header that carries the key";
			if (keyHeader is string h && (h.Trim().Length > 60 || h.Trim().Contains(' '))) return "The header name has no spaces";
			return null;
		}
	}
}
