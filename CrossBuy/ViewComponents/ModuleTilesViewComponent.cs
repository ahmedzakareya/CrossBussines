using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.ViewComponents
{
    // The eight module tiles on the sign-in page. A view component rather than a block of markup in
    // Login.cshtml because each tile carries a drawn icon, and eight inline SVGs in the middle of a
    // page would bury the layout they belong to.
    public class ModuleTilesViewComponent : ViewComponent
    {
        public IViewComponentResult Invoke() => View(true);
    }
}
