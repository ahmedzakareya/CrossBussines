using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	// HR-10 — training courses + enrollments (HR record only). Employee view lives in PeopleController (ESS).
	public partial class AdminController
	{
		private CrossBuy.BL.ITrainingService TrnSvc => (HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.ITrainingService)) as CrossBuy.BL.ITrainingService)!;

		[HttpGet]
		public async Task<IActionResult> TrainingCourses(string? q)
		{
			ViewBag.Q = q;
			return View(await TrnSvc.GetCoursesAsync(HrCompanyId, q));
		}

		[HttpGet]
		public async Task<IActionResult> TrainingCourseEditor(int? id)
		{
			var model = (id.HasValue && id.Value > 0 ? await TrnSvc.GetCourseAsync(HrCompanyId, id.Value) : null)
				?? new TrainingCourse { IsActive = true };
			return View(model);
		}

		[HttpPost]
		public async Task<IActionResult> SaveTrainingCourse(TrainingCourse model)
		{
			model.CompanyID = HrCompanyId;
			var (ok, err, id) = await TrnSvc.SaveCourseAsync(model);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Training course saved"].Value : err;
			return ok ? RedirectToAction(nameof(CourseEnrollments), new { courseId = id }) : RedirectToAction(nameof(TrainingCourseEditor), new { id = model.ID });
		}

		[HttpPost]
		public async Task<IActionResult> DeleteTrainingCourse(int id)
		{
			await TrnSvc.DeleteCourseAsync(HrCompanyId, id);
			TempData["HrMsg"] = L["Course deleted"].Value;
			return RedirectToAction(nameof(TrainingCourses));
		}

		[HttpGet]
		public async Task<IActionResult> CourseEnrollments(int courseId)
		{
			var course = await TrnSvc.GetCourseAsync(HrCompanyId, courseId);
			if (course == null) { TempData["HrErr"] = L["Course not found"].Value; return RedirectToAction(nameof(TrainingCourses)); }
			var enr = await TrnSvc.GetEnrollmentsAsync(HrCompanyId, courseId);
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var empIds = enr.Select(e => e.EmployeeID).Distinct().ToList();
			ViewBag.Names = (await Db.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync())
				.ToDictionary(e => e.ID, e => !isAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName);
			ViewBag.Employees = (await Db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == HrCompanyId && e.IsActive)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync())
				.Select(e => new CrossBuy.ViewModel.EmployeeViewModel { ID = e.ID, FullName = !isAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName })
				.OrderBy(e => e.FullName).ToList();
			ViewBag.Course = course;
			return View(enr);
		}

		[HttpPost]
		public async Task<IActionResult> EnrollEmployees(int courseId, int[] employeeIds)
		{
			var (ok, err) = await TrnSvc.EnrollAsync(HrCompanyId, courseId, employeeIds ?? Array.Empty<int>());
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Employees enrolled"].Value : err;
			return RedirectToAction(nameof(CourseEnrollments), new { courseId });
		}

		[HttpPost]
		public async Task<IActionResult> SetEnrollmentStatus(int id, int courseId, string status, decimal? score, string? certificate)
		{
			var (ok, err) = await TrnSvc.SetEnrollmentStatusAsync(HrCompanyId, id, status, score, certificate);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Enrollment status updated"].Value : err;
			return RedirectToAction(nameof(CourseEnrollments), new { courseId });
		}

		[HttpPost]
		public async Task<IActionResult> RemoveEnrollment(int id, int courseId)
		{
			await TrnSvc.RemoveEnrollmentAsync(HrCompanyId, id);
			TempData["HrMsg"] = L["Enrollment removed"].Value;
			return RedirectToAction(nameof(CourseEnrollments), new { courseId });
		}
	}
}
