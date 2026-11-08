using CrossBuy.BL.Hr;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// An employee's photo, read from the Central Document Platform (HrFileStore). Employee.ProfileImage holds this
	// route's URL, so every avatar in the product keeps rendering from the column it always read.
	//
	// WHO MAY SEE IT: any signed-in member of the employee's OWN company — colleagues see each other's faces in chat,
	// mentions and lists. The company is the reader's resolved BusinessContext, never the URL; a foreign or missing
	// employee and an employee without a platform photo all answer the same bare 404. The query string (d, v) only
	// varies the URL when the photo changes, so a cached old picture is never shown; it selects nothing.
	// ==========================================================================================
	[SessionValidation]
	public class EmployeePhotoController : Controller
	{
		[HttpGet("People/Photo/{employeeId:int}")]
		public async Task<IActionResult> Photo(int employeeId, CancellationToken ct = default)
		{
			var contexts = HttpContext.RequestServices.GetRequiredService<IBusinessContextAccessor>();
			var ctx = await contexts.TryGetCurrentAsync();
			if (ctx == null || ctx.CompanyId <= 0 || employeeId <= 0) return NotFound();
			var store = ActivatorUtilities.CreateInstance<HrFileStore>(HttpContext.RequestServices);
			var photo = await store.OpenPhotoAsync(ctx.CompanyId, employeeId, ct);
			if (photo is not { } p) return NotFound();
			Response.Headers.CacheControl = "private, max-age=86400";
			return File(p.Content, p.ContentType);
		}

		// An HR contract/document scan. The same gate the documents screen applies (module-level employee-view in the
		// reader's company); a foreign, missing or non-HR document answers the same bare 404.
		[HttpGet("People/HrFile/{documentId:long}")]
		public async Task<IActionResult> HrFile(long documentId, CancellationToken ct = default)
		{
			var contexts = HttpContext.RequestServices.GetRequiredService<IBusinessContextAccessor>();
			var ctx = await contexts.TryGetCurrentAsync();
			if (ctx == null || ctx.CompanyId <= 0 || documentId <= 0) return NotFound();
			var hr = HttpContext.RequestServices.GetRequiredService<CrossBuy.BL.IHrAccessService>();
			if (!await hr.CanAsync(ctx, CrossBuy.BL.HrActions.EmployeeView, null)) return NotFound();
			var store = ActivatorUtilities.CreateInstance<HrFileStore>(HttpContext.RequestServices);
			var f = await store.OpenScanAsync(ctx.CompanyId, documentId, ct);
			if (f is not { } x) return NotFound();
			Response.Headers.CacheControl = "private, no-store";
			return Deliver(x.Content, x.ContentType, x.FileName);
		}

		// An applicant's photo or document. The recruitment screens' own gate (module-level performance-manage in the
		// reader's company); a foreign, missing or non-applicant document answers the same bare 404. Images are shown
		// inline (the photo), other files download.
		[HttpGet("People/ApplicantFile/{documentId:long}")]
		public async Task<IActionResult> ApplicantFile(long documentId, CancellationToken ct = default)
		{
			var contexts = HttpContext.RequestServices.GetRequiredService<IBusinessContextAccessor>();
			var ctx = await contexts.TryGetCurrentAsync();
			if (ctx == null || ctx.CompanyId <= 0 || documentId <= 0) return NotFound();
			var hr = HttpContext.RequestServices.GetRequiredService<CrossBuy.BL.IHrAccessService>();
			if (!await hr.CanAsync(ctx, CrossBuy.BL.HrActions.PerformanceManage, null)) return NotFound();
			var store = ActivatorUtilities.CreateInstance<HrFileStore>(HttpContext.RequestServices);
			var f = await store.OpenApplicantFileAsync(ctx.CompanyId, documentId, ct);
			if (f is not { } x) return NotFound();
			Response.Headers.CacheControl = "private, no-store";
			return Deliver(x.Content, x.ContentType, x.FileName);
		}

		// HR3: a leave request's medical certificate. Readable by the REQUESTER, by anyone in the request's stored approval
		// chain (the people asked to decide on it), and by HR through employee-view on the requester. Company from the
		// reader's context; everything else answers the same bare 404.
		[HttpGet("People/LeaveFile/{documentId:long}")]
		public async Task<IActionResult> LeaveFile(long documentId, CancellationToken ct = default)
		{
			var contexts = HttpContext.RequestServices.GetRequiredService<IBusinessContextAccessor>();
			var ctx = await contexts.TryGetCurrentAsync();
			if (ctx == null || ctx.CompanyId <= 0 || documentId <= 0 || ctx.EmployeeId is not int me) return NotFound();
			var store = ActivatorUtilities.CreateInstance<HrFileStore>(HttpContext.RequestServices);
			if (await store.LeaveRequestOfCertificateAsync(ctx.CompanyId, documentId, ct) is not int requestId) return NotFound();
			var db = HttpContext.RequestServices.GetRequiredService<CrossBuy.Models.Context.CrossDbContext>();
			var requester = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
				db.LeaveRequests.Where(r => r.ID == requestId).Select(r => (int?)r.EmployeeID), ct);
			if (requester is not int subject) return NotFound();
			bool may = subject == me
				|| await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.LeaveApprovalSteps, s => s.LeaveRequestID == requestId && s.ApproverEmployeeID == me, ct)
				|| await HttpContext.RequestServices.GetRequiredService<CrossBuy.BL.IHrAccessService>()
					.CanAsync(ctx, CrossBuy.BL.HrActions.EmployeeView, CrossBuy.Models.Platform.PermissionTarget.ForSubjectEmployee(subject, ctx.CompanyId));
			if (!may) return NotFound();
			var f = await store.OpenCurrentBytesAsync(ctx.CompanyId, documentId, ct);
			if (f is not { } x) return NotFound();
			Response.Headers.CacheControl = "private, no-store";
			return Deliver(x.Content, x.ContentType, x.FileName);
		}

		// Images and PDFs are shown INSIDE the page (the viewers embed them); anything else downloads under its name.
		private IActionResult Deliver(Stream content, string contentType, string fileName)
		{
			bool inline = contentType.StartsWith("image/", StringComparison.Ordinal) || contentType == "application/pdf";
			if (!inline) return File(content, contentType, fileName);
			var cd = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline");
			cd.SetHttpFileName(fileName);
			Response.Headers.ContentDisposition = cd.ToString();
			return File(content, contentType);
		}
	}
}
