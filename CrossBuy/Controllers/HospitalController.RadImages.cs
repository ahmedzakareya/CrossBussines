using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H31, an imaging order's pictures. Every rule is HospitalRadImageService's; the bytes come from the
	// private document storage through here only (no static URL). Built from the request's services like H11–H30.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalRadImageService? _radImageSvc;
		private HospitalRadImageService RadImageSvc => _radImageSvc ??= ActivatorUtilities.CreateInstance<HospitalRadImageService>(HttpContext.RequestServices);

		/// `read` at the door; the service asks `rad` or `rad-report` (the radiographer or the radiologist).
		[HttpPost("Radiology/{id:int}/Images"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		[RequestSizeLimit(80_000_000)]
		public async Task<IActionResult> RadImageUpload(int id, List<IFormFile>? files, string? caption, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			if (files == null || files.Count == 0) return Json(new { ok = false, error = _t["Choose the images to attach"].Value });
			int added = 0;
			foreach (var f in files)
			{
				await using var s = f.OpenReadStream();
				var r = await RadImageSvc.UploadAsync(ctx, id, s, f.Length, f.FileName, caption, ctx.EmployeeId, DateTime.UtcNow, ct);
				if (!r.Ok) return added == 0 ? Result(r) : Json(new { ok = false, error = _t[r.Error!, r.Args ?? Array.Empty<object>()].Value, added });
				added++;
			}
			return Json(new { ok = true, id, added });
		}

		[HttpPost("Radiology/Images/{id:int}/Remove"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RadImageRemove(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RadImageSvc.RemoveAsync(ctx, id, ct));
		}

		[HttpGet("Radiology/Images/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RadImage(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var img = await RadImageSvc.OpenAsync(ctx, id, ct);
			if (img is not { } x) return NotFound();
			Response.Headers["Cache-Control"] = "private, max-age=600";
			return File(x.Content, x.Type);
		}
	}
}
