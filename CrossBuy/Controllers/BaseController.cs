using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
    public class BaseController : Controller
    {
        public BaseController()
        {
            ViewData["Culture"] = Thread.CurrentThread.CurrentCulture.TwoLetterISOLanguageName;
        }
        public IActionResult Index()
        {
            return View();
        }
    }
}
