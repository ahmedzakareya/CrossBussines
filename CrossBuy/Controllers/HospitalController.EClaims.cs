using System.Text;
using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H200 — electronic claims: a submitted batch's file for the payer (FHIR R4 Claim bundle), the payer's answer
	// file loaded (FHIR R4 ClaimResponse), and every file kept on the batch. All `bill` (the claims desk's work).
	public partial class HospitalController
	{
		public sealed class HospitalEClaimInput { public string? Raw { get; set; } }

		private HospitalEClaimService? _eclaimSvc;
		private HospitalEClaimService EClaimSvc => _eclaimSvc ??= ActivatorUtilities.CreateInstance<HospitalEClaimService>(HttpContext.RequestServices, InsuranceSvc);

		[HttpGet("Insurance/Batch/{id:int}/Fhir"), HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> ClaimBatchFhir(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var (r, name, json) = await EClaimSvc.ExportAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			return File(new UTF8Encoding(false).GetBytes(json!), "application/fhir+json", name);
		}

		/// H210: the batch as the payer's claim sheet (CSV with its UTF-8 mark: a spreadsheet opens Arabic as Arabic).
		[HttpGet("Insurance/Batch/{id:int}/Sheet"), HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> ClaimBatchSheet(int id, string? format, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			if (string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
			{   // H223: a real Excel workbook
				var (rx, nx, bytes) = await EClaimSvc.ExportSheetXlsxAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct);
				if (!rx.Ok) return Result(rx);
				return File(bytes!, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", nx);
			}
			var (r, name, csv) = await EClaimSvc.ExportSheetAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			return File(new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(csv!)).ToArray(), "text/csv", name);
		}

		[HttpGet("Insurance/Payers/{id:int}/Sheet"), HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> PayerClaimSheet(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var s = await EClaimSvc.SheetAsync(ctx, id, ct);
			if (s == null) return NotFound(new { ok = false, code = "not_found" });
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var others = (await EClaimSvc.PayersWithSheetsAsync(ctx, ct)).Where(p => p.Id != id).Select(p => new { id = p.Id, name = ar ? p.Name : (string.IsNullOrWhiteSpace(p.NameEn) ? p.Name : p.NameEn) });
			return Json(new { ok = true, saved = s.Saved, delimiter = s.Delimiter, dateFormat = s.DateFormat, columns = s.Columns.Select(c => new { key = c.Key, header = c.Header, value = c.Value }),   // H229: a fixed column's value
				fields = HospitalClaimSheets.Fields.Select(f => new { key = f.Key, name = ar ? f.Ar : f.En, en = f.En }), others });
		}

		/// H229: the headings of the payer's own template (Excel or CSV), in its order, each with the field it most likely
		/// means. Nothing is saved: the administrator reviews the columns and saves them as usual.
		[HttpPost("Insurance/Payers/{id:int}/Sheet/Template"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		[RequestSizeLimit(3_000_000)]
		public async Task<IActionResult> PayerClaimSheetTemplate(int id, IFormFile? file, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || await EClaimSvc.SheetAsync(ctx, id, ct) == null) return NotFound(new { ok = false, code = "not_found" });
			if (file == null || file.Length == 0) return Json(new { ok = false, error = _t["Choose the payer's template file"].Value });
			var ext = Path.GetExtension(file.FileName ?? "").ToLowerInvariant();
			IReadOnlyList<string> headers; string? delimiter = null;
			try
			{
				await using var st = file.OpenReadStream();
				if (ext == ".xlsx") { using var ms = new MemoryStream(); await st.CopyToAsync(ms, ct); ms.Position = 0; headers = HospitalClaimSheets.XlsxHeaders(ms); }
				else if (ext is ".csv" or ".txt") { using var rd = new StreamReader(st, Encoding.UTF8, true); var (h, d) = HospitalClaimSheets.CsvHeaders(await rd.ReadToEndAsync(ct)); headers = h; delimiter = d; }
				else return Json(new { ok = false, error = _t["The template must be an Excel (.xlsx) or CSV file"].Value });
			}
			catch (Exception ex) when (ex is not OperationCanceledException)   // a damaged or foreign file: say so, never a 500
			{ return Json(new { ok = false, error = _t["The template could not be read"].Value }); }
			var cols = HospitalClaimSheets.Map(headers);
			if (cols.Count == 0) return Json(new { ok = false, error = _t["No headings were found in the template"].Value });
			return Json(new { ok = true, delimiter, columns = cols.Select(c => new { header = c.Header, key = c.Key }) });
		}

		[HttpPost("Insurance/Payers/{id:int}/Sheet/CopyFrom/{from:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> PayerClaimSheetCopy(int id, int from, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await EClaimSvc.CopySheetAsync(ctx, id, from, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Insurance/Payers/{id:int}/Sheet"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> PayerClaimSheetSave(int id, [FromBody] HospitalClaimSheetInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await EClaimSvc.SaveSheetAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Insurance/Batch/{id:int}/Response"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		[RequestSizeLimit(6_000_000)]
		public async Task<IActionResult> ClaimBatchResponse(int id, [FromBody] HospitalEClaimInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var (r, imp) = await EClaimSvc.ImportAsync(ctx, id, input.Raw, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			return Json(new { ok = true, applied = imp!.Applied, skipped = imp.Skipped, detail = imp.Detail });
		}

		[HttpGet("Insurance/Exchange/{id:int}"), HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> ClaimExchangeFile(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var x = await EClaimSvc.ExchangeAsync(ctx, id, ct);
			if (x == null) return NotFound();
			string name = (x.Direction == HospitalClaimExchangeDirection.Sent ? "claims-" : "answer-") + x.BatchId + "-" + x.ID;
			if (x.Format == HospitalClaimSheets.Format)   // H210: a claim sheet comes back as the sheet it was
				return File(new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(x.Raw)).ToArray(), "text/csv", name + ".csv");
			return File(new UTF8Encoding(false).GetBytes(x.Raw), "application/fhir+json", name + ".fhir.json");
		}
	}
}
