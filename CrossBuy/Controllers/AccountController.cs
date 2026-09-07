using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using System.Globalization;
using System.Diagnostics;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using CrossBuy.BL;
using System.Text.Json;
using AutoMapper;

namespace CrossBuy.Controllers
{
    public class AccountController : Controller
    {

		private readonly IMapper _mapper;
		private readonly IUserService _userService;
		private readonly IEmployeeService _employeeService;
		private readonly SignInManager<Users> _signInManager;
		private readonly CrossDbContext _db;

		public AccountController(
			IUserService userService,
			IEmployeeService employeeService,

			SignInManager<Users> signInManager, IMapper mapper, CrossDbContext db)
		{
			_userService = userService;
			_employeeService = employeeService;
			_signInManager = signInManager;
			_mapper = mapper;
			_db = db;
		}

		// current logged-in employee (stored in session at login as EmployeeViewModel)
		private EmployeeViewModel CurrentEmployee()
		{
			var j = HttpContext.Session.GetString("Employee");
			return string.IsNullOrEmpty(j) ? null : JsonSerializer.Deserialize<EmployeeViewModel>(j);
		}
		private static bool IsAr => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
		public IActionResult SetLanguage(string culture, string returnUrl = "/")
		{
			if (string.IsNullOrEmpty(returnUrl) || !Url.IsLocalUrl(returnUrl))
			{
				returnUrl = "/";
			}

			if (!string.IsNullOrEmpty(culture))
			{
				Response.Cookies.Append(
					CookieRequestCultureProvider.DefaultCookieName,
					CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
					new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1) }
				);
			}

			return LocalRedirect(returnUrl);
		}


		[HttpGet]
        public IActionResult Login(string returnUrl = null)
        {
			ViewData["Culture"] = CultureInfo.CurrentCulture.Name;
			// نحفظ الصفحة المطلوبة (returnUrl) فقط إن كانت رابطًا محليًا آمنًا
			ViewData["ReturnUrl"] = (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) ? returnUrl : null;

			return View();
        }



		public void SetEmployeeSession(Employee employee)
		{
			var employeeJson = JsonSerializer.Serialize(employee);
			HttpContext.Session.SetString("Employee", employeeJson);
		}


		



		public EmployeeViewModel GetEmployeeFromSession()
		{
			var employeeJson = HttpContext.Session.GetString("Employee");
			return string.IsNullOrEmpty(employeeJson)
				? null
				: JsonSerializer.Deserialize<EmployeeViewModel>(employeeJson);
		}


		[HttpPost]
		public async Task<IActionResult> Login(LoginViewModel model, string returnUrl = null)

		{
			try
			{
			if (ModelState.IsValid)
			{
				//var password = "Admin@123";
				//var hasher = new PasswordHasher<object>(); 
				//var hashedPassword = hasher.HashPassword(null, password);

				var user = await _userService.FindUserByNameAsync(model.UserName);

				if (user == null)
				{
					return Json(new { success = false, message = @CrossBuy.Resources.SharedResources.InvalidLogin });
				}

				if (!user.IsActive || !user.IsEndUser)
				{
					return Json(new { success = false, message = @CrossBuy.Resources.SharedResources.InvalidLogin });
				}

				var employee = await _employeeService.GetEmployeeByUserIdAsync(user.Id);
				var employeeViewModel = _mapper.Map<EmployeeViewModel>(employee);


				if (employee == null)
				{
					return Json(new { success = false, message = @CrossBuy.Resources.SharedResources.InvalidLogin });
				}

				var result = await _signInManager.PasswordSignInAsync(user, model.Password, model.RememberMe, lockoutOnFailure: false);

				if (result.Succeeded)
				{
					HttpContext.Session.SetString("Employee", JsonSerializer.Serialize(employeeViewModel));

					// إن كان المستخدم قد جاء من صفحة معيّنة (انتهت جلسته) نعيده إليها، وإلا نذهب لصفحة الاختيار
					var redirectUrl = (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
						? returnUrl
						: Url.Action("Choose", "Portal");

					return Json(new { success = true, redirectUrl });
				}
				else if (result.IsLockedOut)
				{
					return Json(new { success = false, message = @CrossBuy.Resources.SharedResources.InvalidLogin });
				}
				else if (result.IsNotAllowed)
				{
					return Json(new { success = false, message = @CrossBuy.Resources.SharedResources.InvalidLogin });
				}
				else
				{
					return Json(new { success = false, message = @CrossBuy.Resources.SharedResources.InvalidLogin });
				}
			}

			var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
			return Json(new { success = false, errors });
			}
			catch (Exception ex)
			{
				// TEMP diagnostic — capture the real login exception (remove after fixing)
				try
				{
					var logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "login-error.log");
					System.IO.File.AppendAllText(logPath, DateTime.Now.ToString("o") + Environment.NewLine + ex + Environment.NewLine + new string('-', 80) + Environment.NewLine);
				}
				catch { /* ignore logging failure */ }
				return Json(new
				{
					success = false,
					message = "LOGIN-ERROR: " + ex.GetType().Name + ": " + ex.Message,
					inner = ex.InnerException?.Message,
					stack = ex.ToString()
				});
			}
		}



		// =========================================================================================
		// SHARED ACCESS-DENIED DESTINATION.
		//
		// ASP.NET Identity's cookie handler redirects an authenticated-but-unauthorised request to
		// AccessDeniedPath, which defaults to "/Account/AccessDenied". That action did not exist, so every
		// Forbid() on a page request became 302 -> 404: the user was told the screen was MISSING when in
		// fact it was FORBIDDEN, and the 403 the authorization system produced was lost on the way.
		//
		// It lives HERE, at the shared authentication level, not in any module: authorization is refused
		// identically for Reporting, Workspace, Tasks, Calendar and every future module, so a per-module
		// page would be the same screen copied N times and would drift.
		//
		// SEMANTICS ARE PRESERVED, not papered over:
		//   * the response carries HTTP 403 - the redirect target reports the SAME outcome the pipeline
		//     decided. Returning 200 here would make an authorization failure indistinguishable from a
		//     successful page load to any caller that reads status codes (tests, monitors, the browser).
		//   * an AJAX/JSON caller gets a JSON 403 instead of an HTML page, matching how PlatformOps/AccPerm/
		//     InvPerm already answer script callers.
		//   * nothing here turns a 403 into a 404. Endpoints that must hide existence keep returning
		//     NotFound() themselves (ReportsController.Viewer does this deliberately, to deny a catalog
		//     enumeration oracle) - that decision stays with the endpoint that owns the secret.
		//
		// NO INFORMATION LEAKAGE: the page names no resource, no permission key, no role and no module, and
		// it deliberately does NOT render the ReturnUrl the cookie handler appends. Telling someone exactly
		// which permission they lack maps the authorization surface for them; echoing an attacker-supplied
		// ReturnUrl into the page is an injection sink. The user is told they lack access and offered a way
		// back - nothing else.
		//
		// [AllowAnonymous] is required, not incidental: without it a denied user is redirected to a page
		// that denies them, which redirects again. That is the loop this endpoint exists to end.
		[Microsoft.AspNetCore.Authorization.AllowAnonymous]
		[HttpGet]
		public IActionResult AccessDenied()
		{
			bool isAjax = Request.Headers["X-Requested-With"] == "XMLHttpRequest"
			              || (Request.Headers["Accept"].ToString()?.Contains("application/json") ?? false);

			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			if (isAjax)
			{
				return StatusCode(StatusCodes.Status403Forbidden, new
				{
					ok = false,
					denied = true,
					code = "access_denied",
					error = arabic
						? "You do not have permission to open this screen."
						: "You do not have permission to open this screen."
				});
			}

			Response.StatusCode = StatusCodes.Status403Forbidden;
			return View();
		}

		//[HttpPost]
		//[ValidateAntiForgeryToken]

		public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
			HttpContext.Session.Clear();

			return RedirectToAction("Login", "Account");
        }

		// ===== User account pages (wired from the profile dropdown) =====

		[SessionValidation][HttpGet]
		public async Task<IActionResult> Profile()
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login");
			ViewBag.JobTitle = emp.JobTitleID.HasValue
				? await _db.JobTitles.Where(j => j.ID == emp.JobTitleID).Select(j => IsAr ? (j.TitleAr ?? j.Title) : (j.Title ?? j.TitleAr)).FirstOrDefaultAsync() : null;
			ViewBag.Branch = emp.BranchID.HasValue
				? await _db.Branches.Where(b => b.ID == emp.BranchID).Select(b => IsAr ? (b.NameAr ?? b.Name) : (b.Name ?? b.NameAr)).FirstOrDefaultAsync() : null;
			return View(emp);
		}

		[SessionValidation][HttpGet]
		public async Task<IActionResult> Statements()
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login");
			var slips = await _db.Payslips.AsNoTracking()
				.Where(p => p.EmployeeID == emp.ID)
				.OrderByDescending(p => p.Year).ThenByDescending(p => p.Month).ToListAsync();
			ViewBag.EmployeeName = CrossBuy.BL.EmployeeNames.Of(emp.FullName, emp.FullNameEn);
			return View(slips);
		}

		[SessionValidation][HttpGet]
		public IActionResult Subscription()
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login");
			return View(emp);
		}

		[SessionValidation][HttpGet]
		public IActionResult Settings()
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login");
			return View(emp);
		}

		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ChangePassword(string currentPassword, string newPassword, string confirmPassword)
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login");
			if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
			{ TempData["PwErr"] = IsAr ? "كلمة المرور الجديدة قصيرة جدًا (6 أحرف على الأقل)" : "New password is too short (min 6)."; return RedirectToAction(nameof(Settings)); }
			if (newPassword != confirmPassword)
			{ TempData["PwErr"] = IsAr ? "تأكيد كلمة المرور غير مطابق" : "Password confirmation does not match."; return RedirectToAction(nameof(Settings)); }
			var user = await _signInManager.UserManager.GetUserAsync(User);
			if (user == null) return RedirectToAction("Login");
			var res = await _signInManager.UserManager.ChangePasswordAsync(user, currentPassword ?? "", newPassword);
			if (res.Succeeded) TempData["PwMsg"] = IsAr ? "تم تغيير كلمة المرور بنجاح" : "Password changed successfully.";
			else TempData["PwErr"] = string.Join(" · ", res.Errors.Select(e => e.Description));
			return RedirectToAction(nameof(Settings));
		}


    }

}
