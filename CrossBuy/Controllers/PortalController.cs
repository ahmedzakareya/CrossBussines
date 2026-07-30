using System.Text.Json;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	/// Post-login landing: lets the user choose between the HR back-office
	/// and the People self-service portal.
	public class PortalController : Controller
	{
		public IActionResult Choose()
		{
			var json = HttpContext.Session.GetString("Employee");
			if (json == null) return RedirectToAction("Login", "Account");
			var emp = JsonSerializer.Deserialize<EmployeeViewModel>(json);
			ViewBag.NameAr = emp?.FullName;
			ViewBag.NameEn = emp?.FullNameEn;
			return View();
		}
	}
}
