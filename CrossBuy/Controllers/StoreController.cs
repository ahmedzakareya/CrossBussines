using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
    // E-commerce storefront (DISPLAY ONLY). Reads storefront Items (STORE-*) from the DB.
    // No cart / stock / GL / transaction — just renders product/category data with the Nest theme design.
    // PUBLIC end-user area: browse without login (own visitor session), decoupled from the staff/admin login.
    [AllowAnonymous]
    public class StoreController : Controller
    {
        private const int StoreCompanyId = 1;
        private readonly CrossBuy.BL.IStoreCatalogService _catalog;
        private readonly CrossBuy.BL.IIdProtector _ids;

        public StoreController(CrossBuy.BL.IStoreCatalogService catalog, CrossBuy.BL.IIdProtector ids)
        {
            _catalog = catalog; _ids = ids;
        }

        // GET /Store/Product/{id} — id is the ENCRYPTED token (never a raw DB id)
        public async Task<IActionResult> Product(string id)
        {
            var realId = _ids.Unprotect(id);
            if (realId == null) return NotFound();
            var vm = await _catalog.GetProductAsync(StoreCompanyId, realId.Value);
            if (vm == null) return NotFound();
            return View(vm);
        }

        // GET /Store/Category/{id} — id is the ENCRYPTED token; products of one featured category (theme shop-grid design)
        public async Task<IActionResult> Category(string id)
        {
            var realId = _ids.Unprotect(id);
            if (realId == null) return NotFound();
            var vm = await _catalog.GetCategoryAsync(StoreCompanyId, realId.Value);
            if (vm == null) return NotFound();
            return View(vm);
        }
    }
}
