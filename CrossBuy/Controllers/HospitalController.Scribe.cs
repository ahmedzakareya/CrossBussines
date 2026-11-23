using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H254 — ambient documentation: the visit's recording in, the transcript and the SOAP draft out.
	public partial class HospitalController
	{
		[HttpPost("Consult/{id:int}/Scribe"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		[RequestSizeLimit(2_000_000)]
		public async Task<IActionResult> ConsultScribe(int id, IFormFile? audio, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			byte[] bytes = Array.Empty<byte>();
			if (audio != null && audio.Length <= HospitalScribeService.MaxAudioBytes)
			{
				using var ms = new MemoryStream();
				await audio.CopyToAsync(ms, ct);
				bytes = ms.ToArray();
			}
			else if (audio != null) return Json(new { ok = false, code = "too_long" });
			var r = await ActivatorUtilities.CreateInstance<HospitalScribeService>(HttpContext.RequestServices).TranscribeAsync(ctx, id, bytes, DateTime.UtcNow, ct);
			if (r.Code == "not_found") return NotFound(new { ok = false, code = "not_found" });
			return Json(new { ok = r.Ok, code = r.Code, transcript = r.Transcript, language = r.Language, draft = r.Draft });
		}
	}
}
