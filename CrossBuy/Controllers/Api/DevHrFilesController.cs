using CrossBuy.BL.Hr;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	// ==========================================================================================
	// DEVELOPMENT ONLY — moves the HR files that still sit under wwwroot/uploads onto the Central Document Platform
	// (owner rule 2026-11-08: every document and picture lives in the File Manager), through the SAME HrFileStore a
	// new upload uses, so the platform authorises every move (employee-manage on each employee).
	//
	//  * employee photos   Employee.ProfileImage  "/uploads/..."            -> /People/Photo/{id}?d=&v=
	//  * scans             HrDocumentAttachments.FilePath "/uploads/..."    -> /People/HrFile/{documentId}
	//
	// Each file is moved, its row re-pointed, and only THEN is the old file deleted (no second copy stays behind);
	// a failed move leaves the old file and the old path exactly as they were. Theme avatars under /Backend-assets are
	// not uploads and are left alone. dryRun=true reports what would move and touches nothing. [DevOnly]: 404 elsewhere.
	// ==========================================================================================
	[ApiController]
	[Route("api/dev/hr-files")]
	[CrossBuy.Models.DevOnly]
	public sealed class DevHrFilesController : ControllerBase
	{
		private readonly CrossDbContext _db;
		private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _contexts;
		private readonly CrossBuy.BL.IHrAccessService _hr;
		private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _env;

		public DevHrFilesController(CrossDbContext db, CrossBuy.BL.Platform.IBusinessContextAccessor contexts, CrossBuy.BL.IHrAccessService hr, Microsoft.AspNetCore.Hosting.IWebHostEnvironment env)
		{ _db = db; _contexts = contexts; _hr = hr; _env = env; }

		private string? LocalFile(string? webPath)
		{
			if (string.IsNullOrWhiteSpace(webPath)) return null;
			var root = Path.GetFullPath(_env.WebRootPath);
			var full = Path.GetFullPath(Path.Combine(root, webPath.Replace('\\', '/').TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
			return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(full) ? full : null;
		}

		// POST /api/dev/hr-files/migrate?dryRun=true — signed in, with the antiforgery token, and HR employee-manage in
		// the caller's own company (the same authority every upload it performs needs). No shared secret.
		[HttpPost("migrate")]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> Migrate(bool dryRun = true, CancellationToken ct = default)
		{
			var ctx = await _contexts.TryGetCurrentAsync(ct);
			if (ctx is not { CompanyId: > 0 }) return NotFound();
			if (!await _hr.CanAsync(ctx, CrossBuy.BL.HrActions.EmployeeManage, null, ct)) return NotFound();
			var store = ActivatorUtilities.CreateInstance<HrFileStore>(HttpContext.RequestServices);
			var report = new List<object>();
			int moved = 0;

			// ---- employee photos ----
			var people = await _db.Employee.Where(e => e.EmpCompanyID == ctx.CompanyId && e.ProfileImage != null && e.ProfileImage.Replace("\\", "/").Contains("uploads/")).ToListAsync(ct);
			foreach (var e in people)
			{
				var full = LocalFile(e.ProfileImage);
				if (full == null) { report.Add(new { kind = "photo", id = e.ID, ok = false, why = "file missing", path = e.ProfileImage }); continue; }
				if (dryRun) { report.Add(new { kind = "photo", id = e.ID, ok = true, path = e.ProfileImage, dryRun }); continue; }
				string? url, err;
				await using (var s = System.IO.File.OpenRead(full)) (url, err) = await store.StorePhotoAsync(ctx.CompanyId, e.ID, s, s.Length, ct);
				if (url == null) { report.Add(new { kind = "photo", id = e.ID, ok = false, why = err }); continue; }
				e.ProfileImage = url;
				await _db.SaveChangesAsync(ct);
				System.IO.File.Delete(full);
				moved++; report.Add(new { kind = "photo", id = e.ID, ok = true, url });
			}

			// ---- contract / document scans ----
			var atts = await _db.HrDocumentAttachments.Where(a => a.CompanyID == ctx.CompanyId && a.FilePath != null && a.FilePath.Replace("\\", "/").Contains("uploads/")).ToListAsync(ct);
			foreach (var a in atts)
			{
				int employeeId = a.OwnerKind == "Contract"
					? await _db.EmploymentContracts.AsNoTracking().Where(c => c.ID == a.OwnerID && c.CompanyID == ctx.CompanyId).Select(c => c.EmployeeID).FirstOrDefaultAsync(ct)
					: await _db.EmployeeDocuments.AsNoTracking().Where(d => d.ID == a.OwnerID && d.CompanyID == ctx.CompanyId).Select(d => d.EmployeeID).FirstOrDefaultAsync(ct);
				var full = LocalFile(a.FilePath);
				if (employeeId <= 0 || full == null) { report.Add(new { kind = "scan", id = a.ID, ok = false, why = employeeId <= 0 ? "owner missing" : "file missing", path = a.FilePath }); continue; }
				if (dryRun) { report.Add(new { kind = "scan", id = a.ID, ok = true, path = a.FilePath, employeeId, dryRun }); continue; }
				string? url, err;
				await using (var s = System.IO.File.OpenRead(full))
					(url, err) = await store.StoreScanAsync(employeeId, a.FileName ?? Path.GetFileName(full), s, s.Length, a.OwnerKind == "Contract" ? "Employment contract scan (moved)" : "Employee document scan (moved)", ct);
				if (url == null) { report.Add(new { kind = "scan", id = a.ID, ok = false, why = err }); continue; }
				// The legacy single-file column on the contract/document can name the SAME file: re-point it too.
				var oldPath = a.FilePath;
				a.FilePath = url;
				foreach (var c in await _db.EmploymentContracts.Where(c => c.CompanyID == ctx.CompanyId && c.FilePath == oldPath).ToListAsync(ct)) c.FilePath = url;
				foreach (var d in await _db.EmployeeDocuments.Where(d => d.CompanyID == ctx.CompanyId && d.FilePath == oldPath).ToListAsync(ct)) d.FilePath = url;
				await _db.SaveChangesAsync(ct);
				System.IO.File.Delete(full);
				moved++; report.Add(new { kind = "scan", id = a.ID, ok = true, url });
			}

			// ---- applicants: the photo, then the documents (several rows may name ONE file; it goes after the last) ----
			var apps = await _db.JobApplications.Where(a => a.CompanyID == ctx.CompanyId && a.PhotoPath != null && a.PhotoPath.Replace("\\", "/").Contains("uploads/")).ToListAsync(ct);
			foreach (var a in apps)
			{
				var full = LocalFile(a.PhotoPath);
				if (full == null) { report.Add(new { kind = "applicant-photo", id = a.ID, ok = false, why = "file missing", path = a.PhotoPath }); continue; }
				if (dryRun) { report.Add(new { kind = "applicant-photo", id = a.ID, ok = true, path = a.PhotoPath, dryRun }); continue; }
				string? url, err;
				await using (var s = System.IO.File.OpenRead(full)) (url, err) = await store.StoreApplicantFileAsync(a.ID, true, Path.GetFileName(full), s, s.Length, ct);
				if (url == null) { report.Add(new { kind = "applicant-photo", id = a.ID, ok = false, why = err }); continue; }
				a.PhotoPath = url;
				await _db.SaveChangesAsync(ct);
				System.IO.File.Delete(full);
				moved++; report.Add(new { kind = "applicant-photo", id = a.ID, ok = true, url });
			}
			var appDocs = await _db.ApplicationDocuments.Include(d => d.Application)
				.Where(d => d.Application!.CompanyID == ctx.CompanyId && d.FilePath != null && d.FilePath.Replace("\\", "/").Contains("uploads/")).ToListAsync(ct);
			foreach (var group in appDocs.GroupBy(d => d.FilePath))
			{
				var full = LocalFile(group.Key);
				bool allMoved = full != null;
				foreach (var d in group)
				{
					if (full == null) { report.Add(new { kind = "applicant-doc", id = d.ID, ok = false, why = "file missing", path = d.FilePath }); continue; }
					if (dryRun) { report.Add(new { kind = "applicant-doc", id = d.ID, ok = true, path = d.FilePath, dryRun }); continue; }
					string? url, err;
					await using (var s = System.IO.File.OpenRead(full)) (url, err) = await store.StoreApplicantFileAsync(d.ApplicationID, false, d.FileName ?? Path.GetFileName(full), s, s.Length, ct);
					if (url == null) { allMoved = false; report.Add(new { kind = "applicant-doc", id = d.ID, ok = false, why = err }); continue; }
					d.FilePath = url;
					await _db.SaveChangesAsync(ct);
					moved++; report.Add(new { kind = "applicant-doc", id = d.ID, ok = true, url });
				}
				if (!dryRun && allMoved && full != null) System.IO.File.Delete(full);
			}

			return Ok(new { dryRun, moved, of = people.Count + atts.Count + apps.Count + appDocs.Count, report });
		}
	}
}
