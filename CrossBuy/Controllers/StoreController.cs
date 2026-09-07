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
        // Stage 1 Batch B / B3 — the bare constant is gone. The public catalogue company now comes from
        // configuration (Store:StoreCompanyId) through ICompanyIsolationBypass.PublicCatalogCompanyId, and every
        // read runs inside an explicit PublicCompanyRead scope.
        //
        // That scope PINS the company and stays fully filtered. It is deliberately NOT the cross-company
        // administrative bypass: an anonymous public page must never be able to hold a right that reads other
        // companies, and PublicCompanyRead.AllowsCrossCompany is false by construction.
        private int StoreCompanyId => _isolation.PublicCatalogCompanyId;
        private readonly CrossBuy.BL.IStoreCatalogService _catalog;
        private readonly CrossBuy.BL.IIdProtector _ids;
        private readonly CrossBuy.BL.Platform.ICompanyIsolationBypass _isolation;

        public StoreController(CrossBuy.BL.IStoreCatalogService catalog, CrossBuy.BL.IIdProtector ids, CrossBuy.BL.Platform.ICompanyIsolationBypass isolation)
        {
            _catalog = catalog; _ids = ids; _isolation = isolation;
        }

        // GET /Store/Product/{id} — id is the ENCRYPTED token (never a raw DB id)
        public async Task<IActionResult> Product(string id)
        {
            var realId = _ids.Unprotect(id);
            if (realId == null) return NotFound();
            using var publicScope = _isolation.BeginPublicCatalogRead("Anonymous storefront: product page.");
			var vm = await _catalog.GetProductAsync(StoreCompanyId, realId.Value);
            if (vm == null) return NotFound();
            return View(vm);
        }

        // GET /Store/Category/{id} — id is the ENCRYPTED token; products of one featured category (theme shop-grid design)
        public async Task<IActionResult> Category(string id)
        {
            var realId = _ids.Unprotect(id);
            if (realId == null) return NotFound();
            using var publicScope = _isolation.BeginPublicCatalogRead("Anonymous storefront: category page.");
			var vm = await _catalog.GetCategoryAsync(StoreCompanyId, realId.Value);
            if (vm == null) return NotFound();
            return View(vm);
        }
    }
}
