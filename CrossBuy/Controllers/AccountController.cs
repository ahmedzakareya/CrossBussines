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
			ViewBag.EmployeeName = emp.FullName;
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
