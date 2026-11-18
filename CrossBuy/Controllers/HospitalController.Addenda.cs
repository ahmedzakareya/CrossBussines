using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H177: an addendum or a correction on a locked record (a signed visit or drawing, a closed labour). The
	// door is `consult`; the service checks the record is this company's and locked.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalAddendumService? _addendaSvc;
		private HospitalAddendumService AddendaSvc => _addendaSvc ??= ActivatorUtilities.CreateInstance<HospitalAddendumService>(HttpContext.RequestServices);

		[HttpPost("Addenda/{subject}/{id:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult), HospitalIdempotent]
		public async Task<IActionResult> AddAddendum(string subject, int id, [FromBody] HospitalAddendumInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			subject = HospitalAddendumSubject.All.FirstOrDefault(s => string.Equals(s, subject, StringComparison.OrdinalIgnoreCase)) ?? "";
			if (ctx == null || input == null || subject == "") return NotFound(new { ok = false, code = "not_found" });
			return Result(await AddendaSvc.AddAsync(ctx, subject, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
