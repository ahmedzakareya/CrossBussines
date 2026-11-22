using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H230 — the culture's bench work on its lab order (`lab`); the second person verifies through the lab's own
	// verify. The standard organisms / antibiotics / panels are added by the administrator (`manage`).
	public partial class HospitalController
	{
		private HospitalMicroService? _microSvc;
		private HospitalMicroService MicroSvc => _microSvc ??= ActivatorUtilities.CreateInstance<HospitalMicroService>(HttpContext.RequestServices);

		public sealed class HospitalMicroStartInput { public string? Source { get; set; } public string? Detail { get; set; } }
		public sealed class HospitalMicroPrelimInput { public string? Growth { get; set; } public string? GramStain { get; set; } public string? Text { get; set; } }
		public sealed class HospitalMicroIsolateInput { public int OrganismId { get; set; } public string? Quantity { get; set; } }
		public sealed class HospitalMicroAstSaveInput { public List<HospitalMicroAstInput>? Lines { get; set; } }
		public sealed class HospitalMicroCompleteInput { public string? Growth { get; set; } public string? Comment { get; set; } }

		[HttpPost("Lab/{id:int}/Micro/Start"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> MicroStart(int id, [FromBody] HospitalMicroStartInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MicroSvc.StartAsync(ctx, id, input.Source, input.Detail, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Lab/{id:int}/Micro/Preliminary"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> MicroPreliminary(int id, [FromBody] HospitalMicroPrelimInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MicroSvc.PreliminaryAsync(ctx, id, input.Growth, input.GramStain, input.Text, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Lab/{id:int}/Micro/Isolates"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> MicroIsolateAdd(int id, [FromBody] HospitalMicroIsolateInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MicroSvc.AddIsolateAsync(ctx, id, input.OrganismId, input.Quantity, ct));
		}

		[HttpPost("Lab/{id:int}/Micro/Isolates/{isolateId:int}/Remove"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> MicroIsolateRemove(int id, int isolateId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MicroSvc.RemoveIsolateAsync(ctx, id, isolateId, ct));
		}

		[HttpPost("Lab/{id:int}/Micro/Isolates/{isolateId:int}/Ast"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> MicroAst(int id, int isolateId, [FromBody] HospitalMicroAstSaveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MicroSvc.SaveAstAsync(ctx, id, isolateId, input.Lines ?? new(), ct));
		}

		[HttpPost("Lab/{id:int}/Micro/Complete"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> MicroComplete(int id, [FromBody] HospitalMicroCompleteInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MicroSvc.CompleteAsync(ctx, id, input.Growth, input.Comment, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		/// H231: the cumulative antibiogram for a period (default: the last twelve months), organism by antibiotic.
		[HttpGet("Lab/Antibiogram"), HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> Antibiogram(DateTime? from, DateTime? to, string? q, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var t = (to ?? DateTime.Today).Date; var f = (from ?? t.AddMonths(-12).AddDays(1)).Date;
			if (f > t) (f, t) = (t, f);
			var cells = await MicroSvc.AntibiogramAsync(ctx, DateTime.SpecifyKind(f, DateTimeKind.Local).ToUniversalTime(), DateTime.SpecifyKind(t.AddDays(1), DateTimeKind.Local).ToUniversalTime(), ct)
				?? Array.Empty<HospitalAbgCell>();
			if (!string.IsNullOrWhiteSpace(q)) cells = cells.Where(c => c.Organism.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
			ViewBag.From = f; ViewBag.To = t; ViewBag.Q = q;
			return View(cells);
		}

		[HttpPost("LabTests/Micro/SeedStandard"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> MicroSeedStandard(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await MicroSvc.SeedStandardAsync(ctx, ct));
		}
	}
}
