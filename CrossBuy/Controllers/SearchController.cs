using System.Globalization;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
    // Global search across the platform's entities. Both endpoints resolve the caller's BusinessContext and
    // delegate to IGlobalSearchService, which company-scopes and permission-gates every type. Suggest feeds the
    // header search box (JSON); Results is the full page. No entity queries live here — only presentation.
    [SessionValidation]
    public class SearchController : Controller
    {
        private readonly IGlobalSearchService _search;
        private readonly IBusinessContextAccessor _contexts;

        public SearchController(IGlobalSearchService search, IBusinessContextAccessor contexts)
        { _search = search; _contexts = contexts; }

        private static bool IsAr => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

        // GET /Search/Suggest?q=term — the header box's dropdown. At most a few hits per type.
        [HttpGet]
        public async Task<IActionResult> Suggest(string? q, CancellationToken ct = default)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            var res = await _search.SearchAsync(ctx, q, perType: 5, cancellationToken: ct);
            bool ar = IsAr;
            return Json(new
            {
                term = res.Term,
                total = res.Total,
                groups = res.Groups.Select(g => new
                {
                    code = g.Code,
                    label = ar ? g.LabelAr : g.LabelEn,
                    icon = g.Icon,
                    hits = g.Hits.Select(h => new { h.Id, h.Label, url = h.Url })
                })
            });
        }

        // GET /Search/Results?q=term — the full results page, more hits per type.
        [HttpGet]
        public async Task<IActionResult> Results(string? q, CancellationToken ct = default)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            var res = await _search.SearchAsync(ctx, q, perType: 12, cancellationToken: ct);
            return View(res);
        }
    }
}
