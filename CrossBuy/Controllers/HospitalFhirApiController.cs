using System.Text.Json.Nodes;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H248 — the FHIR R4 clinical read API (HospitalFhirApiService). No session and no cookie: a system presents
	// its ClinicalRead interface key in the X-Interface-Key header (or Authorization: Bearer <key>); it reads as the
	// employee the interface acts for, with that person's rights checked on each call.
	[Route("api/hospital/fhir/{code}/r4")]
	[IgnoreAntiforgeryToken]
	public class HospitalFhirApiController : Controller
	{
		private HospitalFhirApiService Api => ActivatorUtilities.CreateInstance<HospitalFhirApiService>(HttpContext.RequestServices);

		private string? Key()
		{
			var k = Request.Headers["X-Interface-Key"].FirstOrDefault();
			if (!string.IsNullOrWhiteSpace(k)) return k;
			var auth = Request.Headers.Authorization.FirstOrDefault();
			return auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
		}

		private IActionResult Fhir(JsonObject o, int status = 200) => new ContentResult { Content = o.ToJsonString(), ContentType = "application/fhir+json; charset=utf-8", StatusCode = status };

		private async Task<BusinessContext?> OpenAsync(string code, CancellationToken ct)
		{
			var open = await HospitalInterfaceInbound.OpenAsync(HttpContext.RequestServices, code, Key(), new[] { HospitalInterfaceKind.ClinicalRead }, ct);
			if (open == null) return null;
			return await Api.MayReadAsync(open.Value.Ctx, ct) ? open.Value.Ctx : null;
		}

		private const string Why =
			"Another clinical system has no login: it presents its ClinicalRead interface key (X-Interface-Key or a Bearer token; random, stored " +
			"only as a SHA-256 hash, shown once). The key opens one interface of one company and the record is read AS the employee the interface " +
			"acts for — clinical-read checked on every call, a restricted file closed unless that person may open it without breaking the glass. " +
			"It reads only; a wrong, inactive or other-kind key gets a bare 401.";

		[AllowAnonymous, AnonymousByDesign(Why)]
		[HttpGet("metadata")]
		public IActionResult Metadata(string code) => Fhir(new JsonObject
		{
			["resourceType"] = "CapabilityStatement", ["status"] = "active", ["kind"] = "instance", ["fhirVersion"] = "4.0.1", ["format"] = new JsonArray("json"),
			["rest"] = new JsonArray(new JsonObject
			{
				["mode"] = "server",
				["resource"] = new JsonArray(new[] { "Patient", "Encounter", "Observation", "DiagnosticReport", "AllergyIntolerance", "MedicationRequest" }
					.Select(t => (JsonNode)new JsonObject { ["type"] = t, ["interaction"] = new JsonArray(new JsonObject { ["code"] = t == "Patient" ? "read" : "search-type" }) }).ToArray()),
			}),
		});

		[AllowAnonymous, AnonymousByDesign(Why)]
		[HttpGet("Patient/{id:int}")]
		public async Task<IActionResult> Patient(string code, int id, CancellationToken ct = default)
		{
			var ctx = await OpenAsync(code, ct);
			if (ctx == null) return Unauthorized();
			var p = await Api.PatientAsync(ctx, id, ct);
			return p == null ? Fhir(HospitalFhirResources.Outcome("not-found", "No such patient"), 404) : Fhir(HospitalFhirResources.Patient(p));
		}

		[AllowAnonymous, AnonymousByDesign(Why)]
		[HttpGet("Patient")]
		public async Task<IActionResult> PatientSearch(string code, string? identifier, CancellationToken ct = default)
		{
			var ctx = await OpenAsync(code, ct);
			if (ctx == null) return Unauthorized();
			var list = await Api.FindPatientsAsync(ctx, identifier, ct);
			return Fhir(HospitalFhirResources.Bundle(list.Select(HospitalFhirResources.Patient), Request.Path + Request.QueryString));
		}

		[AllowAnonymous, AnonymousByDesign(Why)]
		[HttpGet("{type:regex(^(Encounter|Observation|DiagnosticReport|AllergyIntolerance|MedicationRequest)$)}")]
		public async Task<IActionResult> Search(string code, string type, string? patient, CancellationToken ct = default)
		{
			var ctx = await OpenAsync(code, ct);
			if (ctx == null) return Unauthorized();
			var pid = (patient ?? "").Split('/').Last();
			if (!int.TryParse(pid, out var id)) return Fhir(HospitalFhirResources.Outcome("required", "Search by patient (?patient=Patient/{id})"), 400);
			var p = await Api.PatientAsync(ctx, id, ct);
			if (p == null) return Fhir(HospitalFhirResources.Bundle(Array.Empty<JsonObject>(), Request.Path + Request.QueryString));
			IEnumerable<JsonObject> rows = type switch
			{
				"Encounter" => await Api.EncountersAsync(ctx, id, ct),
				"Observation" => await Api.ObservationsAsync(ctx, id, ct),
				"DiagnosticReport" => await Api.ReportsAsync(ctx, id, ct),
				"AllergyIntolerance" => HospitalFhirResources.Allergies(p),
				_ => await Api.MedicationRequestsAsync(ctx, id, ct),
			};
			return Fhir(HospitalFhirResources.Bundle(rows, Request.Path + Request.QueryString));
		}
	}
}
