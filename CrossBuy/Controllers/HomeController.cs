using CrossBuy.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using System.Diagnostics;
using System.Globalization;

namespace CrossBuy.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly IStringLocalizer<SharedResources> _localizer;
        private readonly CrossBuy.BL.IStoreCatalogService _catalog;   // E-commerce Phase 1 (display only)
        // Stage 1 Batch B / B3: from configuration via the public catalogue scope, not a bare constant. See StoreController.
        private int StoreCompanyId => _isolation.PublicCatalogCompanyId;
        private readonly CrossBuy.BL.Platform.ICompanyIsolationBypass _isolation;

        public HomeController(ILogger<HomeController> logger , IStringLocalizer<SharedResources> localizer, CrossBuy.BL.IStoreCatalogService catalog, CrossBuy.BL.Platform.ICompanyIsolationBypass isolation)
        {
            _logger = logger;
            _localizer = localizer;
            _catalog = catalog;
            _isolation = isolation;
        }

        public IActionResult Index()
        {
            ViewData["Culture"] = CultureInfo.CurrentCulture.Name;

            //var welcomeText = _localizer["WelcomeText"];
            //ViewBag.WelcomeMessage = welcomeText;

            var welcomeMessage = CrossBuy.Resources.SharedResources.WelcomeMessage;
            //ViewBag.WelcomeMessage = welcomeText;
            return View();
        }

        public IActionResult Privacy()
        {
            return View();
        }
        // E-commerce storefront catalog (display only) — reads the real Items + ItemCategories from the DB.
        [AllowAnonymous]   // public storefront — end users browse without the staff/admin login
        public async Task<IActionResult> Store()
        {
            ViewBag.WelcomeMessage = CrossBuy.Resources.SharedResources.WelcomeMessage;
            // Explicit public catalogue scope — pinned to the configured company, read-only, not cross-company.
            using var publicScope = _isolation.BeginPublicCatalogRead("Anonymous storefront: catalogue home.");
            var vm = new CrossBuy.ViewModel.StoreCatalogVM
            {
                Categories = await _catalog.GetCategoriesAsync(StoreCompanyId),
                Products = await _catalog.GetProductsAsync(StoreCompanyId)
            };
            return View(vm);
        }
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }

    }
}
