using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H97: allergen immunotherapy from the allergy visit: start a course, give the dose the schedule allows
	// (its 30-minute observation), record its reaction, resume or stop a held course.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalImmunotherapyService? _immunoSvc;
		private HospitalImmunotherapyService ImmunoSvc => _immunoSvc ??= ActivatorUtilities.CreateInstance<HospitalImmunotherapyService>(HttpContext.RequestServices);

		public sealed class HospitalImmunoStartInput { public int PatientId { get; set; } public string? Extract { get; set; } public string? ExtractEn { get; set; } }
		public sealed class HospitalImmunoDecideInput { public bool Resume { get; set; } public string? Note { get; set; } }

		[HttpPost("Immuno/Start"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> ImmunoStart([FromBody] HospitalImmunoStartInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ImmunoSvc.StartAsync(ctx, input.PatientId, input.Extract, input.ExtractEn, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Immuno/Courses/{id:int}/Give"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> ImmunoGive(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ImmunoSvc.GiveAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Immuno/Doses/{id:int}/Reaction"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> ImmunoReaction(int id, [FromBody] HospitalImmunoReactionInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ImmunoSvc.ReactionAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Immuno/Courses/{id:int}/Decide"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> ImmunoDecide(int id, [FromBody] HospitalImmunoDecideInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ImmunoSvc.DecideAsync(ctx, id, input.Resume, input.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
