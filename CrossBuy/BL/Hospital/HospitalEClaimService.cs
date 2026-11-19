using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H200 — electronic claims (HL7 FHIR R4). A submitted batch goes to the payer as one Bundle of Claim
	// resources, one per claim (its bill): the patient, the policy, the visit's coded diagnoses (ICD-10-CM; a stay's final
	// diagnosis as text), the approvals, and one item per claim line asking the payer's share. The payer answers with
	// ClaimResponse resources; each item's adjudication is recorded through the SAME path as an answer typed by hand
	// (HospitalInsuranceService.RecordResponseAsync — its checks, its ledger postings, the batch's state), so a file can
	// never do what a person could not:
	//   · a benefit equal to the claim accepts the line; a smaller one accepts that amount; nothing paid with a reason
	//     rejects it with the payer's code and reason;
	//   · a benefit above what was claimed, an answer with no amount and no reason, an item or claim not of this batch,
	//     and a line already answered are LEFT as they were and named in the file's detail;
	//   · every file is kept, sent or received, with what it changed — the same answer loaded twice changes nothing.
	// The item's sequence is its line's place in the claim (lines in id order); each item also names its line id.
	// ==========================================================================================
	public static class HospitalEClaimSkip
	{
		public const string NotInBatch = "not a claim of this batch";
		public const string UnknownItem = "no such item";
		public const string NoDecision = "no amount and no reason";
		public const string OverClaimed = "pays more than was claimed";
		public const string NoReason = "rejected without a reason";
		public const string Answered = "already answered";
		public const string PayerError = "the payer could not process it";
		public static readonly IReadOnlyList<string> All = new[] { NotInBatch, UnknownItem, NoDecision, OverClaimed, NoReason, Answered, PayerError };
	}

	public sealed record HospitalFhirAdjudication(int Sequence, decimal? Benefit, string? ReasonCode, string? ReasonText);
	public sealed record HospitalFhirClaimAnswer(string Request, IReadOnlyList<HospitalFhirAdjudication> Items, bool Error, string? ErrorText);
	public sealed record HospitalEClaimDecision(bool Apply, bool Accept, decimal? Amount, string? Code, string? Reason, string? Skip);
	public sealed record HospitalEClaimClaimData(HospitalClaim Claim, string BillNo, DateTime BillDay, bool Stay, HospitalPatient Patient, HospitalPatientPolicy Policy,
		IReadOnlyList<HospitalClaimLine> Lines, IReadOnlyList<HospitalEncounterDiagnosis> Diagnoses, string? FinalDiagnosis, IReadOnlyList<string> ApprovalRefs);
	public sealed record HospitalEClaimImport(int Applied, int Skipped, string? Detail, int ExchangeId);

	public static class HospitalFhirClaims
	{
		public const string Sys = "urn:crossbusiness:hospital:";
		private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

		private static JsonObject Coding(string system, string code, string? display = null)
		{
			var o = new JsonObject { ["system"] = system, ["code"] = code };
			if (!string.IsNullOrWhiteSpace(display)) o["display"] = display;
			return o;
		}
		private static JsonObject Concept(string system, string code, string? display = null) => new() { ["coding"] = new JsonArray(Coding(system, code, display)) };
		private static JsonObject Money(decimal v, string currency) => new() { ["value"] = v, ["currency"] = currency };
		private static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		private static string En(string? en, string ar) => string.IsNullOrWhiteSpace(en) ? ar : en;

		/// The claim's id inside the files: "claim-{id}". The payer's answer names it back in ClaimResponse.request.
		public static string ClaimRef(int claimId) => "claim-" + claimId.ToString(CultureInfo.InvariantCulture);

		public static int? ClaimIdOf(string? request)
		{
			if (string.IsNullOrWhiteSpace(request)) return null;
			int i = request.LastIndexOf("claim-", StringComparison.OrdinalIgnoreCase);
			return i >= 0 && int.TryParse(request.AsSpan(i + 6), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
		}

		public static JsonObject Claim(HospitalEClaimClaimData d, HospitalPayer payer, string currency, int companyId)
		{
			var p = d.Patient;
			var patient = new JsonObject
			{
				["resourceType"] = "Patient", ["id"] = "patient",
				["identifier"] = new JsonArray(new JsonObject { ["system"] = Sys + "mrn", ["value"] = p.Mrn }),
				["name"] = new JsonArray(new JsonObject { ["text"] = En(p.NameEn, p.Name) }),
				["gender"] = p.Sex == HospitalPatientSex.Female ? "female" : p.Sex == HospitalPatientSex.Male ? "male" : "unknown",
			};
			if (!string.IsNullOrWhiteSpace(p.NationalId)) patient["identifier"]!.AsArray().Add(new JsonObject { ["system"] = "urn:crossbusiness:national-id", ["value"] = p.NationalId });
			if (p.BirthDate is DateTime bd) patient["birthDate"] = Day(bd);

			var coverage = new JsonObject
			{
				["resourceType"] = "Coverage", ["id"] = "coverage", ["status"] = "active", ["subscriberId"] = d.Policy.PolicyNo,
				["beneficiary"] = new JsonObject { ["reference"] = "#patient" },
				["payor"] = new JsonArray(new JsonObject { ["identifier"] = new JsonObject { ["system"] = Sys + "payer", ["value"] = payer.Code }, ["display"] = En(payer.NameEn, payer.Name) }),
				["period"] = d.Policy.ValidTo is DateTime to ? new JsonObject { ["start"] = Day(d.Policy.ValidFrom), ["end"] = Day(to) } : new JsonObject { ["start"] = Day(d.Policy.ValidFrom) },
			};
			if (!string.IsNullOrWhiteSpace(d.Policy.MemberClass))
				coverage["class"] = new JsonArray(new JsonObject { ["type"] = Concept("http://terminology.hl7.org/CodeSystem/coverage-class", "class"), ["value"] = d.Policy.MemberClass });

			var dx = new JsonArray();
			int s = 0;
			foreach (var g in d.Diagnoses.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.SortOrder))
				dx.Add(new JsonObject
				{
					["sequence"] = ++s,
					["diagnosisCodeableConcept"] = Concept("http://hl7.org/fhir/sid/icd-10-cm", g.Code, En(g.NameEn, g.Name)),
					["type"] = new JsonArray(Concept("http://terminology.hl7.org/CodeSystem/ex-diagnosistype", g.IsPrimary ? "principal" : "secondary")),
				});
			if (dx.Count == 0 && !string.IsNullOrWhiteSpace(d.FinalDiagnosis))
				dx.Add(new JsonObject { ["sequence"] = 1, ["diagnosisCodeableConcept"] = new JsonObject { ["text"] = d.FinalDiagnosis } });

			var insurance = new JsonObject { ["sequence"] = 1, ["focal"] = true, ["coverage"] = new JsonObject { ["reference"] = "#coverage" } };
			if (d.ApprovalRefs.Count > 0) insurance["preAuthRef"] = new JsonArray(d.ApprovalRefs.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());

			var items = new JsonArray();
			int seq = 0;
			foreach (var l in d.Lines.OrderBy(x => x.ID))
			{
				var ext = new JsonArray(
					new JsonObject { ["url"] = Sys + "claim-line", ["valueInteger"] = l.ID },
					new JsonObject { ["url"] = Sys + "gross", ["valueMoney"] = Money(l.Amount, currency) });
				if (!string.IsNullOrWhiteSpace(l.Justification)) ext.Add(new JsonObject { ["url"] = Sys + "justification", ["valueString"] = l.Justification });
				items.Add(new JsonObject
				{
					["sequence"] = ++seq,
					["extension"] = ext,
					["category"] = Concept(Sys + "category", l.Category),
					["productOrService"] = Concept(Sys + "service", l.Code, En(l.DescriptionEn, l.Description)),
					["servicedDate"] = Day(d.BillDay),
					["net"] = Money(l.InsurerAmount, currency),
				});
			}

			var claim = new JsonObject
			{
				["resourceType"] = "Claim", ["id"] = ClaimRef(d.Claim.ID),
				["contained"] = new JsonArray(patient, coverage),
				["identifier"] = new JsonArray(new JsonObject { ["system"] = Sys + "bill", ["value"] = d.BillNo }),
				["status"] = "active",
				["type"] = Concept("http://terminology.hl7.org/CodeSystem/claim-type", d.Stay ? "institutional" : "professional"),
				["use"] = "claim",
				["patient"] = new JsonObject { ["reference"] = "#patient" },
				["created"] = Day(d.BillDay),
				["insurer"] = new JsonObject { ["identifier"] = new JsonObject { ["system"] = Sys + "payer", ["value"] = payer.Code }, ["display"] = En(payer.NameEn, payer.Name) },
				["provider"] = new JsonObject { ["identifier"] = new JsonObject { ["system"] = "urn:crossbusiness:company", ["value"] = companyId.ToString(CultureInfo.InvariantCulture) } },
				["priority"] = Concept("http://terminology.hl7.org/CodeSystem/processpriority", "normal"),
				["insurance"] = new JsonArray(insurance),
				["item"] = items,
				["total"] = Money(d.Lines.Sum(x => x.InsurerAmount), currency),
			};
			if (dx.Count > 0) claim["diagnosis"] = dx;
			return claim;
		}

		public static string Bundle(string batchNo, IEnumerable<JsonObject> claims, DateTime nowUtc)
		{
			var b = new JsonObject
			{
				["resourceType"] = "Bundle",
				["identifier"] = new JsonObject { ["system"] = Sys + "claim-batch", ["value"] = batchNo },
				["type"] = "collection",
				["timestamp"] = nowUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
				["entry"] = new JsonArray(claims.Select(c => (JsonNode)new JsonObject { ["fullUrl"] = "Claim/" + c["id"]!.GetValue<string>(), ["resource"] = c }).ToArray()),
			};
			return b.ToJsonString(Pretty);
		}

		/// The answers in a file: a Bundle of ClaimResponse resources, or one ClaimResponse. Null + why when unreadable.
		public static (IReadOnlyList<HospitalFhirClaimAnswer>? Answers, string? Refusal) ParseResponses(string? raw)
		{
			if (string.IsNullOrWhiteSpace(raw)) return (null, "The file is empty");
			JsonNode? root;
			try { root = JsonNode.Parse(raw); } catch (JsonException) { return (null, "This is not a FHIR JSON file"); }
			if (root is not JsonObject o) return (null, "This is not a FHIR JSON file");
			var found = new List<JsonObject>();
			string? type = Str(o["resourceType"]);
			if (type == "ClaimResponse") found.Add(o);
			else if (type == "Bundle")
			{
				foreach (var e in o["entry"] as JsonArray ?? new JsonArray())
					if (e?["resource"] is JsonObject r && Str(r["resourceType"]) == "ClaimResponse") found.Add(r);
			}
			else return (null, "The file holds no claim responses (ClaimResponse)");
			if (found.Count == 0) return (null, "The file holds no claim responses (ClaimResponse)");
			var list = new List<HospitalFhirClaimAnswer>();
			foreach (var r in found)
			{
				string request = Str(r["request"]?["reference"]) ?? Str(r["request"]?["identifier"]?["value"]) ?? "";
				bool error = Str(r["outcome"]) == "error";
				string? errText = Str(r["disposition"]) ?? Str(r["error"]?[0]?["code"]?["coding"]?[0]?["code"]);
				var items = new List<HospitalFhirAdjudication>();
				foreach (var it in r["item"] as JsonArray ?? new JsonArray())
				{
					if (it is not JsonObject io || !int.TryParse(io["itemSequence"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq)) continue;
					decimal? benefit = null; string? code = null, text = null;
					foreach (var a in io["adjudication"] as JsonArray ?? new JsonArray())
					{
						if (a is not JsonObject ao) continue;
						string? cat = Str(ao["category"]?["coding"]?[0]?["code"]);
						if (cat == "benefit" && ao["amount"]?["value"] is JsonNode v && decimal.TryParse(v.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var dv)) benefit = dv;
						var rc = ao["reason"]?["coding"]?[0];
						code ??= Str(rc?["code"]);
						text ??= Str(rc?["display"]) ?? Str(ao["reason"]?["text"]);
					}
					items.Add(new HospitalFhirAdjudication(seq, benefit, code, text));
				}
				list.Add(new HospitalFhirClaimAnswer(request, items, error, errText));
			}
			return (list, null);
		}

		private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

		/// What one item's adjudication does to its line (asked = the line's InsurerAmount).
		public static HospitalEClaimDecision Decide(HospitalFhirAdjudication a, decimal asked)
		{
			if (a.Benefit is decimal b && b > 0)
			{
				if (b > asked) return new(false, false, null, null, null, HospitalEClaimSkip.OverClaimed);
				return new(true, true, b == asked ? null : b, null, null, null);
			}
			if (a.Benefit == null && a.ReasonCode == null && a.ReasonText == null) return new(false, false, null, null, null, HospitalEClaimSkip.NoDecision);
			if (a.ReasonCode == null && a.ReasonText == null) return new(false, false, null, null, null, HospitalEClaimSkip.NoReason);
			return new(true, false, null, a.ReasonCode ?? "-", a.ReasonText ?? a.ReasonCode, null);
		}
	}

	public sealed class HospitalEClaimService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalInsuranceService _insurance;
		private readonly HospitalPricingService? _pricing;
		public HospitalEClaimService(CrossDbContext db, IHospitalAccessService access, HospitalInsuranceService insurance, HospitalPricingService? pricing = null)
		{ _db = db; _access = access; _insurance = insurance; _pricing = pricing; }

		private IQueryable<HospitalClaimExchange> Exchanges(BusinessContext ctx) => _db.Set<HospitalClaimExchange>().Where(x => x.CompanyId == ctx.CompanyId);
		private static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

		private async Task<(HospitalClaimBatch? Batch, HospitalPayer? Payer)> BatchAsync(BusinessContext ctx, int batchId, CancellationToken ct)
		{
			var b = await _db.Set<HospitalClaimBatch>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == batchId && x.CompanyId == ctx.CompanyId, ct);
			if (b == null) return (null, null);
			return (b, await _db.Set<HospitalPayer>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == b.PayerId && x.CompanyId == ctx.CompanyId, ct));
		}

		private async Task<List<HospitalEClaimClaimData>> ClaimsAsync(BusinessContext ctx, int batchId, CancellationToken ct)
		{
			int c = ctx.CompanyId;
			var rows = await (from cl in _db.Set<HospitalClaim>().AsNoTracking()
							  join b in _db.HospitalBills.AsNoTracking() on cl.BillId equals b.ID
							  join p in _db.HospitalPatients.AsNoTracking() on cl.PatientId equals p.ID
							  join po in _db.Set<HospitalPatientPolicy>().AsNoTracking() on cl.PolicyId equals po.ID
							  where cl.CompanyId == c && cl.BatchId == batchId && b.CompanyId == c && p.CompanyId == c && po.CompanyId == c
							  orderby b.BillNo
							  select new { cl, b, p, po }).ToListAsync(ct);
			var ids = rows.Select(r => r.cl.ID).ToList();
			var lines = await _db.Set<HospitalClaimLine>().AsNoTracking().Where(l => l.CompanyId == c && ids.Contains(l.ClaimId)).ToListAsync(ct);
			var encIds = rows.Where(r => r.b.EncounterId != null).Select(r => r.b.EncounterId!.Value).ToList();
			var dx = await _db.Set<HospitalEncounterDiagnosis>().AsNoTracking().Where(d => d.CompanyId == c && encIds.Contains(d.EncounterId)).ToListAsync(ct);
			var admIds = rows.Where(r => r.b.AdmissionId != null).Select(r => r.b.AdmissionId!.Value).ToList();
			var finals = admIds.Count == 0 ? new Dictionary<int, string?>()
				: await _db.Set<HospitalDischargeSummary>().AsNoTracking().Where(s => s.CompanyId == c && admIds.Contains(s.AdmissionId)).ToDictionaryAsync(s => s.AdmissionId, s => s.FinalDiagnosis, ct);
			var preIds = lines.Where(l => l.PreauthId != null).Select(l => l.PreauthId!.Value).Distinct().ToList();
			var refs = await _db.Set<HospitalPreauth>().AsNoTracking().Where(x => x.CompanyId == c && preIds.Contains(x.ID))
				.Select(x => new { x.ID, Ref = x.ApprovalRef ?? x.RequestNo }).ToDictionaryAsync(x => x.ID, x => x.Ref, ct);
			return rows.Select(r =>
			{
				var ls = lines.Where(l => l.ClaimId == r.cl.ID).OrderBy(l => l.ID).ToList();
				return new HospitalEClaimClaimData(r.cl, r.b.BillNo, r.b.ClosedAt ?? r.b.OpenedAt, r.b.AdmissionId != null, r.p, r.po, ls,
					r.b.EncounterId is int e ? dx.Where(d => d.EncounterId == e).ToList() : new List<HospitalEncounterDiagnosis>(),
					r.b.AdmissionId is int a && finals.TryGetValue(a, out var f) ? f : null,
					ls.Where(l => l.PreauthId != null && refs.ContainsKey(l.PreauthId.Value)).Select(l => refs[l.PreauthId!.Value]).Distinct().ToList());
			}).ToList();
		}

		/// The batch's claims as a FHIR R4 Bundle (kept as a sent file). Only a submitted batch goes out.
		public async Task<(HospitalResult Result, string? FileName, string? Json)> ExportAsync(BusinessContext ctx, int batchId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Bill, null, ct)) return (HospitalResult.Fail("forbidden"), null, null);
			var (b, payer) = await BatchAsync(ctx, batchId, ct);
			if (b == null || payer == null) return (HospitalResult.Fail("not_found"), null, null);
			if (b.Status == HospitalClaimBatchStatus.Draft) return (HospitalResult.Fail("Submit the batch before sending its file"), null, null);
			var claims = await ClaimsAsync(ctx, batchId, ct);
			string currency = await CurrencyAsync(ctx, payer, ct);
			string json = HospitalFhirClaims.Bundle(b.BatchNo, claims.Select(d => HospitalFhirClaims.Claim(d, payer, currency, ctx.CompanyId)), nowUtc);
			var x = new HospitalClaimExchange { CompanyId = ctx.CompanyId, BatchId = b.ID, Direction = HospitalClaimExchangeDirection.Sent, Raw = json, Applied = claims.Sum(d => d.Lines.Count), At = nowUtc, ByEmployeeId = employeeId };
			_db.Add(x);
			await _db.SaveChangesAsync(ct);
			return (HospitalResult.Success(x.ID), b.BatchNo + ".fhir.json", json);
		}

		// ---- H210: the payer's claim sheet (a TPA's spreadsheet) ----

		/// The payer's sheet, or the standard one while it has none.
		public async Task<HospitalClaimSheetSetup?> SheetAsync(BusinessContext ctx, int payerId, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Bill, null, ct)) return null;
			if (!await _db.Set<HospitalPayer>().AnyAsync(p => p.ID == payerId && p.CompanyId == ctx.CompanyId, ct)) return null;
			var s = await _db.Set<HospitalPayerClaimSheet>().AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.PayerId == payerId, ct);
			if (s == null) return HospitalClaimSheets.Standard();
			var cols = HospitalClaimSheets.Unpack(s.Columns);
			return cols.Count == 0 ? HospitalClaimSheets.Standard() : new HospitalClaimSheetSetup(cols, s.Delimiter, s.DateFormat, true);
		}

		public async Task<HospitalResult> SaveSheetAsync(BusinessContext ctx, int payerId, HospitalClaimSheetInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Bill, null, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.Set<HospitalPayer>().AnyAsync(p => p.ID == payerId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (HospitalClaimSheets.Refusal(input) is string why) return HospitalResult.Fail(why);
			var s = await _db.Set<HospitalPayerClaimSheet>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.PayerId == payerId, ct);
			if (s == null) { s = new HospitalPayerClaimSheet { CompanyId = ctx.CompanyId, PayerId = payerId }; _db.Add(s); }
			s.Columns = HospitalClaimSheets.Pack(input.Columns!.Select(c => c with { Header = c.Header.Trim() }));
			s.Delimiter = input.Delimiter!; s.DateFormat = input.DateFormat!; s.UpdatedAt = nowUtc; s.UpdatedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(s.ID);
		}

		/// The batch's claims as the payer's sheet (kept as a sent file, like the FHIR one). Only a submitted batch goes out.
		public async Task<(HospitalResult Result, string? FileName, string? Csv)> ExportSheetAsync(BusinessContext ctx, int batchId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Bill, null, ct)) return (HospitalResult.Fail("forbidden"), null, null);
			var (b, payer) = await BatchAsync(ctx, batchId, ct);
			if (b == null || payer == null) return (HospitalResult.Fail("not_found"), null, null);
			if (b.Status == HospitalClaimBatchStatus.Draft) return (HospitalResult.Fail("Submit the batch before sending its file"), null, null);
			var setup = (await SheetAsync(ctx, payer.ID, ct))!;
			var claims = await ClaimsAsync(ctx, batchId, ct);
			string currency = await CurrencyAsync(ctx, payer, ct);
			string csv = HospitalClaimSheets.Render(setup, b.BatchNo, claims, currency);
			var x = new HospitalClaimExchange { CompanyId = ctx.CompanyId, BatchId = b.ID, Direction = HospitalClaimExchangeDirection.Sent, Format = HospitalClaimSheets.Format, Raw = csv, Applied = claims.Sum(d => d.Lines.Count), At = nowUtc, ByEmployeeId = employeeId };
			_db.Add(x);
			await _db.SaveChangesAsync(ct);
			return (HospitalResult.Success(x.ID), b.BatchNo + ".csv", csv);
		}

		private async Task<string> CurrencyAsync(BusinessContext ctx, HospitalPayer payer, CancellationToken ct)
		{
			if (_pricing == null) return "KWD";
			int? cur = null;
			if (payer.PriceListId is int lid) cur = await _db.Set<HospitalPriceList>().AsNoTracking().Where(l => l.ID == lid && l.CompanyId == ctx.CompanyId).Select(l => l.CurrencyId).FirstOrDefaultAsync(ct);
			return (await _pricing.CurrencyOfAsync(ctx, cur, ct)).Code;
		}

		/// The payer's answers (FHIR ClaimResponse) recorded line by line through the hand-entry path; the file is kept.
		public async Task<(HospitalResult Result, HospitalEClaimImport? Import)> ImportAsync(BusinessContext ctx, int batchId, string? raw, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Bill, null, ct)) return (HospitalResult.Fail("forbidden"), null);
			var (b, _) = await BatchAsync(ctx, batchId, ct);
			if (b == null) return (HospitalResult.Fail("not_found"), null);
			if (b.Status == HospitalClaimBatchStatus.Draft) return (HospitalResult.Fail("Submit the batch before recording the payer's answer"), null);
			var (answers, why) = HospitalFhirClaims.ParseResponses(raw);
			if (why != null) return (HospitalResult.Fail(why), null);
			if (raw!.Length > 5_000_000) return (HospitalResult.Fail("The file is too large"), null);
			var claims = (await ClaimsAsync(ctx, batchId, ct)).ToDictionary(d => d.Claim.ID);
			var byBill = claims.Values.ToDictionary(d => d.BillNo, d => d, StringComparer.OrdinalIgnoreCase);
			int applied = 0, skipped = 0;
			var notes = new List<string>();
			void Skip(string where, string reason) { skipped++; notes.Add(where + ": " + reason); }
			foreach (var a in answers!)
			{
				HospitalEClaimClaimData? d = HospitalFhirClaims.ClaimIdOf(a.Request) is int cid && claims.TryGetValue(cid, out var dd) ? dd : byBill.GetValueOrDefault(a.Request);
				if (d == null) { Skip(a.Request == "" ? "?" : a.Request, HospitalEClaimSkip.NotInBatch); continue; }
				if (a.Error) { foreach (var _ in d.Lines) skipped++; notes.Add(d.BillNo + ": " + HospitalEClaimSkip.PayerError + (a.ErrorText == null ? "" : " (" + a.ErrorText + ")")); continue; }
				foreach (var it in a.Items)
				{
					string where = d.BillNo + " #" + it.Sequence.ToString(CultureInfo.InvariantCulture);
					if (it.Sequence < 1 || it.Sequence > d.Lines.Count) { Skip(where, HospitalEClaimSkip.UnknownItem); continue; }
					var line = d.Lines[it.Sequence - 1];
					if (line.Status != HospitalClaimLineStatus.Pending) { Skip(where, HospitalEClaimSkip.Answered); continue; }
					var dec = HospitalFhirClaims.Decide(it, line.InsurerAmount);
					if (!dec.Apply) { Skip(where, dec.Skip!); continue; }
					var r = await _insurance.RecordResponseAsync(ctx, line.ID, dec.Accept, dec.Amount, dec.Code, dec.Reason, nowUtc, ct);
					if (r.Ok) applied++;
					else Skip(where, r.Error ?? "?");
				}
			}
			var x = new HospitalClaimExchange
			{
				CompanyId = ctx.CompanyId, BatchId = b.ID, Direction = HospitalClaimExchangeDirection.Received, Raw = raw, Applied = applied, Skipped = skipped,
				Detail = notes.Count == 0 ? null : Clip(string.Join("; ", notes), 1000), At = nowUtc, ByEmployeeId = employeeId,
			};
			_db.Add(x);
			await _db.SaveChangesAsync(ct);
			return (HospitalResult.Success(x.ID), new HospitalEClaimImport(applied, skipped, x.Detail, x.ID));
		}

		/// The batch's files, newest first, with who sent or loaded them. Null when the table is not there yet.
		public async Task<IReadOnlyList<(HospitalClaimExchange X, string? By)>?> ExchangesAsync(BusinessContext ctx, int batchId, CancellationToken ct = default)
		{
			try
			{
				var rows = await (from x in Exchanges(ctx).AsNoTracking()
								  join e0 in _db.Employee.AsNoTracking() on x.ByEmployeeId equals e0.ID into ee
								  from e in ee.DefaultIfEmpty()
								  where x.BatchId == batchId
								  orderby x.At descending, x.ID descending
								  select new { x, e }).ToListAsync(ct);
				return rows.Select(r => (r.x, r.e == null ? null : (string?)EmployeeNames.Of(r.e))).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException) { return null; }
		}

		public async Task<HospitalClaimExchange?> ExchangeAsync(BusinessContext ctx, int id, CancellationToken ct = default) =>
			!await _access.CanAsync(ctx, HospitalActions.Bill, null, ct) ? null : await Exchanges(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
	}
}
