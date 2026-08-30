using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	// HR-10 — training courses + enrollments (HR record only). Employee view lives in PeopleController (ESS).
	// ────────────────────────────────────────────────────────────────────────────────────────────
	// TRAINING — five baseline AuthorizationGap entries closed, and the company-1 literal removed.
	//
	// Enrollment records carry scores and certificates against named employees, and every write here was
	// open to any signed-in user while addressing company 1. HrActions.PerformanceManage is the existing
	// vocabulary entry for "appraisals, training, recruitment".
	//
	// EnrollEmployees needed the tenant fix more than the gate: it takes an ARRAY of employee ids, so a
	// caller could enroll another company's employees onto company 1's course. A PermissionTarget cannot
	// express an array, so the ids are filtered against the resolved company here, before the service.
	// ────────────────────────────────────────────────────────────────────────────────────────────
	public partial class AdminController
	{
		private CrossBuy.BL.ITrainingService TrnSvc => (HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.ITrainingService)) as CrossBuy.BL.ITrainingService)!;

		[HttpGet]
		public async Task<IActionResult> TrainingCourses(string? q)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(Index));

			ViewBag.Q = q;
			return View(await TrnSvc.GetCoursesAsync(gate.CompanyId, q));
		}

		[HttpGet]
		public async Task<IActionResult> TrainingCourseEditor(int? id)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(Index));

			var model = (id.HasValue && id.Value > 0 ? await TrnSvc.GetCourseAsync(gate.CompanyId, id.Value) : null)
				?? new TrainingCourse { IsActive = true };
			return View(model);
		}

		[HttpPost]
		public async Task<IActionResult> SaveTrainingCourse(TrainingCourse model)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(TrainingCourses));

			// Overwritten from the gate, never taken from the posted model — a bound CompanyID on a
			// [FromForm] entity is a caller-supplied tenant by another name.
			model.CompanyID = gate.CompanyId;
			var (ok, err, id) = await TrnSvc.SaveCourseAsync(model);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Training course saved"].Value : err;
			return ok ? RedirectToAction(nameof(CourseEnrollments), new { courseId = id }) : RedirectToAction(nameof(TrainingCourseEditor), new { id = model.ID });
		}

		[HttpPost]
		public async Task<IActionResult> DeleteTrainingCourse(int id)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(TrainingCourses));

			await TrnSvc.DeleteCourseAsync(gate.CompanyId, id);
			TempData["HrMsg"] = L["Course deleted"].Value;
			return RedirectToAction(nameof(TrainingCourses));
		}

		[HttpGet]
		public async Task<IActionResult> CourseEnrollments(int courseId)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(TrainingCourses));

			var course = await TrnSvc.GetCourseAsync(gate.CompanyId, courseId);
			if (course == null) { TempData["HrErr"] = L["Course not found"].Value; return RedirectToAction(nameof(TrainingCourses)); }
			var enr = await TrnSvc.GetEnrollmentsAsync(gate.CompanyId, courseId);
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var empIds = enr.Select(e => e.EmployeeID).Distinct().ToList();
			ViewBag.Names = (await Db.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID) && e.EmpCompanyID == gate.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync())
				.ToDictionary(e => e.ID, e => !isAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName);
			ViewBag.Employees = (await Db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == gate.CompanyId && e.IsActive)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync())
				.Select(e => new CrossBuy.ViewModel.EmployeeViewModel { ID = e.ID, FullName = !isAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName })
				.OrderBy(e => e.FullName).ToList();
			ViewBag.Course = course;
			return View(enr);
		}

		[HttpPost]
		public async Task<IActionResult> EnrollEmployees(int courseId, int[] employeeIds)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(CourseEnrollments), new { courseId });

			// THE POSTED IDS ARE FILTERED, not trusted. An id that is not an employee of the resolved
			// company is dropped before the service is called, so a crafted form cannot enroll another
			// tenant's staff onto this company's course.
			var posted = employeeIds ?? Array.Empty<int>();
			var mine = posted.Length == 0
				? Array.Empty<int>()
				: await Db.Employee.AsNoTracking()
					.Where(e => posted.Contains(e.ID) && e.EmpCompanyID == gate.CompanyId)
					.Select(e => e.ID).ToArrayAsync();

			var (ok, err) = await TrnSvc.EnrollAsync(gate.CompanyId, courseId, mine);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Employees enrolled"].Value : err;
			return RedirectToAction(nameof(CourseEnrollments), new { courseId });
		}

		[HttpPost]
		public async Task<IActionResult> SetEnrollmentStatus(int id, int courseId, string status, decimal? score, string? certificate)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(CourseEnrollments), new { courseId });

			var (ok, err) = await TrnSvc.SetEnrollmentStatusAsync(gate.CompanyId, id, status, score, certificate);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Enrollment status updated"].Value : err;
			return RedirectToAction(nameof(CourseEnrollments), new { courseId });
		}

		[HttpPost]
		public async Task<IActionResult> RemoveEnrollment(int id, int courseId)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(CourseEnrollments), new { courseId });

			await TrnSvc.RemoveEnrollmentAsync(gate.CompanyId, id);
			TempData["HrMsg"] = L["Enrollment removed"].Value;
			return RedirectToAction(nameof(CourseEnrollments), new { courseId });
		}
	}
}
