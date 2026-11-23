using System.Globalization;
using System.Text.Json.Nodes;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H248 — the FHIR R4 clinical READ API for other systems (a regional record, a specialist system, a data
	// warehouse). A system presents its ClinicalRead interface key; it reads AS the employee the interface acts for:
	// that person's clinical-read right is checked on every call, and a restricted file (H64) stays closed to it unless
	// that person may open it without breaking the glass — a machine never breaks the glass.
	//   Patient (by id, or ?identifier=MRN|national id) · Encounter (the stays) · Observation (verified laboratory
	//   results) · DiagnosticReport (verified laboratory orders and signed imaging reports) · AllergyIntolerance (the
	//   recorded allergies) · MedicationRequest (inpatient medication orders). Searches return a Bundle "searchset".
	// Nothing here writes, except the interface's last-used time.
	// ==========================================================================================
	public static class HospitalFhirResources
	{
		public const string Sys = HospitalFhirClaims.Sys;
		private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
		private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);
		private static JsonObject Ref(string type, int id) => new() { ["reference"] = type + "/" + Id(id) };
		private static JsonObject Cc(string system, string code, string? display) =>
			new() { ["coding"] = new JsonArray(new JsonObject { ["system"] = system, ["code"] = code, ["display"] = display }), ["text"] = display ?? code };

		public static JsonObject Patient(HospitalPatient p)
		{
			var ids = new JsonArray(new JsonObject { ["system"] = Sys + "mrn", ["value"] = p.Mrn });
			if (!string.IsNullOrWhiteSpace(p.NationalId)) ids.Add(new JsonObject { ["system"] = "urn:crossbusiness:national-id", ["value"] = p.NationalId });
			var names = new JsonArray(new JsonObject { ["use"] = "official", ["text"] = string.IsNullOrWhiteSpace(p.NameEn) ? p.Name : p.NameEn });
			if (!string.IsNullOrWhiteSpace(p.Name) && p.Name != p.NameEn) names.Add(new JsonObject { ["use"] = "usual", ["text"] = p.Name, ["extension"] = new JsonArray(new JsonObject { ["url"] = "http://hl7.org/fhir/StructureDefinition/language", ["valueCode"] = "ar" }) });
			var o = new JsonObject
			{
				["resourceType"] = "Patient", ["id"] = Id(p.ID), ["identifier"] = ids, ["name"] = names,
				["gender"] = p.Sex == HospitalPatientSex.Female ? "female" : p.Sex == HospitalPatientSex.Male ? "male" : "unknown",
			};
			if (p.BirthDate is DateTime b) o["birthDate"] = b.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			if (!string.IsNullOrWhiteSpace(p.Phone)) o["telecom"] = new JsonArray(new JsonObject { ["system"] = "phone", ["value"] = p.Phone });
			return o;
		}

		public static JsonObject Encounter(HospitalAdmission a) => new()
		{
			["resourceType"] = "Encounter", ["id"] = Id(a.ID), ["status"] = a.Status == HospitalAdmissionStatus.Active ? "in-progress" : "finished",
			["class"] = new JsonObject { ["system"] = "http://terminology.hl7.org/CodeSystem/v3-ActCode", ["code"] = "IMP", ["display"] = "inpatient encounter" },
			["subject"] = Ref("Patient", a.PatientId),
			["period"] = a.DischargedAt is DateTime d ? new JsonObject { ["start"] = Iso(a.AdmittedAt), ["end"] = Iso(d) } : new JsonObject { ["start"] = Iso(a.AdmittedAt) },
			["reasonCode"] = string.IsNullOrWhiteSpace(a.Diagnosis) ? null : new JsonArray(new JsonObject { ["text"] = a.Diagnosis }),
		};

		public static string? Interpretation(string flag) => flag switch
		{
			HospitalLabFlag.High => "H", HospitalLabFlag.Low => "L", HospitalLabFlag.CriticalHigh => "HH", HospitalLabFlag.CriticalLow => "LL", HospitalLabFlag.Normal => "N", _ => null,
		};

		public static JsonObject Observation(HospitalLabResult r, HospitalLabOrder o, string code)
		{
			var obs = new JsonObject
			{
				["resourceType"] = "Observation", ["id"] = Id(r.ID), ["status"] = "final",
				["category"] = new JsonArray(Cc("http://terminology.hl7.org/CodeSystem/observation-category", "laboratory", "Laboratory")),
				["code"] = Cc(Sys + "analyte", code, string.IsNullOrWhiteSpace(r.NameEn) ? r.Name : r.NameEn),
				["subject"] = Ref("Patient", o.PatientId),
				["effectiveDateTime"] = Iso(o.CollectedAt ?? o.OrderedAt), ["issued"] = Iso(o.VerifiedAt ?? o.OrderedAt),
			};
			if (r.Value is decimal v) obs["valueQuantity"] = new JsonObject { ["value"] = Math.Round(v, r.Decimals), ["unit"] = r.Unit };
			else if (!string.IsNullOrWhiteSpace(r.TextValue)) obs["valueString"] = r.TextValue;
			if (r.RefLow != null || r.RefHigh != null)
			{
				var rr = new JsonObject();
				if (r.RefLow is decimal lo) rr["low"] = new JsonObject { ["value"] = lo, ["unit"] = r.Unit };
				if (r.RefHigh is decimal hi) rr["high"] = new JsonObject { ["value"] = hi, ["unit"] = r.Unit };
				obs["referenceRange"] = new JsonArray(rr);
			}
			if (Interpretation(r.Flag) is string i)
				obs["interpretation"] = new JsonArray(Cc("http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation", i, null));
			return obs;
		}

		public static JsonObject LabReport(HospitalLabOrder o, IEnumerable<int> resultIds) => new()
		{
			["resourceType"] = "DiagnosticReport", ["id"] = "lab-" + Id(o.ID), ["status"] = "final",
			["category"] = new JsonArray(Cc("http://terminology.hl7.org/CodeSystem/v2-0074", "LAB", "Laboratory")),
			["identifier"] = new JsonArray(new JsonObject { ["system"] = Sys + "accession", ["value"] = o.AccessionNo }),
			["code"] = Cc(Sys + "service", o.Code, string.IsNullOrWhiteSpace(o.NameEn) ? o.Name : o.NameEn),
			["subject"] = Ref("Patient", o.PatientId), ["issued"] = Iso(o.VerifiedAt ?? o.OrderedAt),
			["result"] = new JsonArray(resultIds.Select(id => (JsonNode)Ref("Observation", id)).ToArray()),
		};

		public static JsonObject RadReport(HospitalRadOrder o) => new()
		{
			["resourceType"] = "DiagnosticReport", ["id"] = "rad-" + Id(o.ID), ["status"] = "final",
			["category"] = new JsonArray(Cc("http://terminology.hl7.org/CodeSystem/v2-0074", "RAD", "Radiology")),
			["identifier"] = new JsonArray(new JsonObject { ["system"] = Sys + "accession", ["value"] = o.AccessionNo }),
			["code"] = Cc(Sys + "service", o.Code, string.IsNullOrWhiteSpace(o.NameEn) ? o.Name : o.NameEn),
			["subject"] = Ref("Patient", o.PatientId), ["issued"] = Iso(o.SignedAt ?? o.OrderedAt),
			["conclusion"] = o.ImpressionEn ?? o.Impression,
			["presentedForm"] = new JsonArray(new JsonObject { ["contentType"] = "text/plain", ["data"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(((o.FindingsEn ?? o.Findings) ?? "") + "\n\n" + ((o.ImpressionEn ?? o.Impression) ?? ""))) }),
		};

		public static IEnumerable<JsonObject> Allergies(HospitalPatient p) =>
			(p.Allergies ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select((a, i) => new JsonObject
			{
				["resourceType"] = "AllergyIntolerance", ["id"] = Id(p.ID) + "-" + Id(i + 1),
				["clinicalStatus"] = Cc("http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical", "active", "Active"),
				["code"] = new JsonObject { ["text"] = a }, ["patient"] = Ref("Patient", p.ID),
			});

		public static JsonObject MedicationRequest(HospitalMedOrder m) => new()
		{
			["resourceType"] = "MedicationRequest", ["id"] = Id(m.ID), ["status"] = m.Status == HospitalMedOrderStatus.Active ? "active" : "stopped", ["intent"] = "order",
			["medicationCodeableConcept"] = new JsonObject { ["text"] = m.Drug }, ["subject"] = Ref("Patient", m.PatientId), ["encounter"] = Ref("Encounter", m.AdmissionId),
			["authoredOn"] = Iso(m.CreatedAt),
			["dosageInstruction"] = new JsonArray(new JsonObject { ["text"] = m.Dose + " " + m.Route + (m.AsNeeded ? " PRN" : string.IsNullOrWhiteSpace(m.Hours) ? "" : " at " + m.Hours), ["asNeededBoolean"] = m.AsNeeded }),
		};

		public static JsonObject Bundle(IEnumerable<JsonObject> resources, string selfUrl)
		{
			var list = resources.ToList();
			return new JsonObject
			{
				["resourceType"] = "Bundle", ["type"] = "searchset", ["total"] = list.Count,
				["link"] = new JsonArray(new JsonObject { ["relation"] = "self", ["url"] = selfUrl }),
				["entry"] = new JsonArray(list.Select(r => (JsonNode)new JsonObject { ["fullUrl"] = r["resourceType"]!.GetValue<string>() + "/" + r["id"]!.GetValue<string>(), ["resource"] = r }).ToArray()),
			};
		}

		public static JsonObject Outcome(string code, string text) => new()
		{
			["resourceType"] = "OperationOutcome",
			["issue"] = new JsonArray(new JsonObject { ["severity"] = "error", ["code"] = code, ["diagnostics"] = text }),
		};
	}

	public sealed class HospitalFhirApiService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalRestrictedAccessService _restricted;
		public HospitalFhirApiService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; _restricted = new HospitalRestrictedAccessService(db, access); }

		public Task<bool> MayReadAsync(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct);

		/// The patient when this caller may read the file (restricted files open only without breaking the glass).
		public async Task<HospitalPatient?> PatientAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			var p = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
			if (p == null) return null;
			var (access, _) = await _restricted.AccessAsync(ctx, p.ID, ctx.EmployeeId, DateTime.UtcNow, ct);
			return access is HospitalFileAccess.Denied or HospitalFileAccess.BreakGlass ? null : p;
		}

		public async Task<IReadOnlyList<HospitalPatient>> FindPatientsAsync(BusinessContext ctx, string? identifier, CancellationToken ct = default)
		{
			var v = (identifier ?? "").Split('|').Last().Trim();
			if (v.Length == 0) return Array.Empty<HospitalPatient>();
			var ids = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && (p.Mrn == v || p.NationalId == v)).Select(p => p.ID).Take(5).ToListAsync(ct);
			var list = new List<HospitalPatient>();
			foreach (var id in ids) if (await PatientAsync(ctx, id, ct) is HospitalPatient p) list.Add(p);
			return list;
		}

		public async Task<IReadOnlyList<JsonObject>> EncountersAsync(BusinessContext ctx, int patientId, CancellationToken ct = default) =>
			(await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.PatientId == patientId).OrderByDescending(a => a.AdmittedAt).Take(100).ToListAsync(ct))
				.Select(HospitalFhirResources.Encounter).ToList();

		private async Task<(List<HospitalLabOrder> Orders, List<HospitalLabResult> Results, Dictionary<int, string> Codes)> LabAsync(BusinessContext ctx, int patientId, CancellationToken ct)
		{
			var orders = await _db.Set<HospitalLabOrder>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.PatientId == patientId && o.VerifiedAt != null).OrderByDescending(o => o.VerifiedAt).Take(200).ToListAsync(ct);
			var ids = orders.Select(o => o.ID).ToList();
			var results = await _db.Set<HospitalLabResult>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && ids.Contains(r.LabOrderId)).OrderBy(r => r.SortOrder).ToListAsync(ct);
			var aids = results.Select(r => r.AnalyteId).Distinct().ToList();
			var codes = await _db.Set<HospitalLabAnalyte>().AsNoTracking().Where(a => aids.Contains(a.ID)).ToDictionaryAsync(a => a.ID, a => a.Code, ct);
			return (orders, results, codes);
		}

		public async Task<IReadOnlyList<JsonObject>> ObservationsAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
		{
			var (orders, results, codes) = await LabAsync(ctx, patientId, ct);
			var byId = orders.ToDictionary(o => o.ID);
			return results.Select(r => HospitalFhirResources.Observation(r, byId[r.LabOrderId], codes.GetValueOrDefault(r.AnalyteId) ?? r.AnalyteId.ToString(CultureInfo.InvariantCulture))).ToList();
		}

		public async Task<IReadOnlyList<JsonObject>> ReportsAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
		{
			var (orders, results, _) = await LabAsync(ctx, patientId, ct);
			var list = orders.Select(o => HospitalFhirResources.LabReport(o, results.Where(r => r.LabOrderId == o.ID).Select(r => r.ID))).ToList();
			var rad = await _db.HospitalRadOrders.AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.PatientId == patientId && o.Status == HospitalRadStatus.Signed).OrderByDescending(o => o.SignedAt).Take(100).ToListAsync(ct);
			list.AddRange(rad.Select(HospitalFhirResources.RadReport));
			return list;
		}

		public async Task<IReadOnlyList<JsonObject>> MedicationRequestsAsync(BusinessContext ctx, int patientId, CancellationToken ct = default) =>
			(await _db.Set<HospitalMedOrder>().AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && m.PatientId == patientId).OrderByDescending(m => m.CreatedAt).Take(200).ToListAsync(ct))
				.Select(HospitalFhirResources.MedicationRequest).ToList();
	}
}
