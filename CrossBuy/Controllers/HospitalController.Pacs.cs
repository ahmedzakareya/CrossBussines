using System.Text;
using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H199 — the imaging archive (PACS): its viewer address and order address are kept on the exams & rooms
	// screen (`manage`); an imaging order is handed to the archive's worklist as an HL7 ORM^O01 file (`rad`).
	public partial class HospitalController
	{
		public sealed class HospitalPacsInput { public string? ViewerUrl { get; set; } public string? ReceivingApp { get; set; } }

		private HospitalPacsService? _pacsSvc;
		private HospitalPacsService PacsSvc => _pacsSvc ??= ActivatorUtilities.CreateInstance<HospitalPacsService>(HttpContext.RequestServices);

		[HttpPost("RadiologySetup/Pacs"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> RadiologyPacsSave([FromBody] HospitalPacsInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await PacsSvc.SaveAsync(ctx, input.ViewerUrl, input.ReceivingApp, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpGet("Radiology/{id:int}/Hl7"), HospitalPerm(HospitalActions.Rad)]
		public async Task<IActionResult> RadOrderHl7(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var m = await PacsSvc.OrderMessageAsync(ctx, id, DateTime.UtcNow, ct);
			if (m == null) return NotFound();
			return File(new UTF8Encoding(false).GetBytes(m.Value.Message), "application/hl7-v2", "ORM-" + m.Value.Accession + ".hl7");
		}
	}
}
