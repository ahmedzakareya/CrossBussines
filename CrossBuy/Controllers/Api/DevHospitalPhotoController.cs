using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	// ==========================================================================================
	// DEVELOPMENT ONLY — moves the hospital patients' photos that still sit under wwwroot/uploads/hospital onto the
	// document platform, through the SAME HospitalPatientPhotoService a new upload uses (the platform authorises it).
	// Each photo is moved, re-pointed, and only THEN is the old file deleted — no second copy stays behind (owner
	// rule), and a failed move leaves the old file and the old path exactly as they were. [DevOnly]: 404 elsewhere.
	// ==========================================================================================
	[ApiController]
	[Route("api/dev/hospital-photos")]
	[AllowAnonymous]
	[CrossBuy.Models.DevOnly]
	[IgnoreAntiforgeryToken]
	public sealed class DevHospitalPhotoController : ControllerBase
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _contexts;
		private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _env;

		public DevHospitalPhotoController(CrossDbContext db, IHospitalAccessService access, CrossBuy.BL.Platform.IBusinessContextAccessor contexts,
			Microsoft.AspNetCore.Hosting.IWebHostEnvironment env)
		{ _db = db; _access = access; _contexts = contexts; _env = env; }

		// POST /api/dev/hospital-photos/migrate?key=seed123
		[HttpPost("migrate")]
		public async Task<IActionResult> Migrate(string key, CancellationToken ct)
		{
			if (key != "seed123") return Unauthorized();
			var ctx = await _contexts.TryGetCurrentAsync(ct);
			if (ctx is not { CompanyId: > 0 }) return NotFound();
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return NotFound();
			var photos = ActivatorUtilities.CreateInstance<HospitalPatientPhotoService>(HttpContext.RequestServices);
			var patients = ActivatorUtilities.CreateInstance<HospitalPatientService>(HttpContext.RequestServices);
			var rows = await _db.HospitalPatients.AsNoTracking()
				.Where(p => p.CompanyId == ctx.CompanyId && p.PhotoPath != null && p.PhotoPath.StartsWith("/uploads/hospital/"))
				.Select(p => new { p.ID, p.Mrn, p.PhotoPath }).ToListAsync(ct);
			var root = Path.GetFullPath(_env.WebRootPath);
			var report = new List<object>();
			foreach (var r in rows)
			{
				var full = Path.GetFullPath(Path.Combine(root, r.PhotoPath!.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
				if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(full))
				{ report.Add(new { r.Mrn, ok = false, why = "file missing" }); continue; }
				string? url, error;
				await using (var s = System.IO.File.OpenRead(full)) (url, error) = await photos.StoreAsync(ctx, r.ID, s, s.Length, ct);
				if (url == null) { report.Add(new { r.Mrn, ok = false, why = error }); continue; }
				var set = await patients.SetPhotoAsync(ctx, r.ID, url, ctx.EmployeeId, ct);
				if (!set.Ok) { report.Add(new { r.Mrn, ok = false, why = set.Error }); continue; }
				System.IO.File.Delete(full);   // moved and re-pointed: the old public copy goes
				report.Add(new { r.Mrn, ok = true, url });
			}
			return Ok(new { moved = report.Count(x => (bool)x.GetType().GetProperty("ok")!.GetValue(x)!), of = rows.Count, report });
		}

		// POST /api/dev/hospital-photos/rad-images?key=seed123 — H35: the imaging pictures attached before H35, onto the
		// platform (HospitalRadImageService.MoveLegacyAsync; the platform authorises `rad` on each order).
		[HttpPost("rad-images")]
		public async Task<IActionResult> MoveRadImages(string key, CancellationToken ct)
		{
			if (key != "seed123") return Unauthorized();
			var ctx = await _contexts.TryGetCurrentAsync(ct);
			if (ctx is not { CompanyId: > 0 }) return NotFound();
			if (!await _access.CanAsync(ctx, HospitalActions.Rad, null, ct)) return NotFound();
			var images = ActivatorUtilities.CreateInstance<HospitalRadImageService>(HttpContext.RequestServices);
			var done = await images.MoveLegacyAsync(ctx, ct);
			return Ok(new { moved = done.Count(d => d.Ok), of = done.Count, report = done.Select(d => new { image = d.ImageId, ok = d.Ok, why = d.Why }) });
		}
	}
}
