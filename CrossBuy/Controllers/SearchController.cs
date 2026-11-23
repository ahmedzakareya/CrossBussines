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

        // GET /Search/Results?q=term — the SHELL only. The rows arrive from ResultsData.
        //
        // It still accepts q so a link, a bookmark or Enter in the header box lands on a page that
        // is already showing that search rather than an empty one.
        [HttpGet]
        public IActionResult Results(string? q) => View(model: q ?? string.Empty);

        // GET /Search/ResultsData?q=&page=&pageSize= — the rows, as the other list screens do it.
        //
        // WHY THIS EXISTS: the results page used to be served whole, so changing the search meant a
        // full navigation — the shell, the menu and the scripts all fetched again to change a table.
        // Every other list in the product (cbServerList, ninety-six screens) loads its shell once and
        // then fetches rows, and this is now one of them: same element ids, same X-Total/X-Page/
        // X-Pages headers, same partial-of-rows answer.
        //
        // The groups are flattened here rather than in the view, because the page is a table and the
        // type became a column. perType is the registry's own cap, so this asks for everything the
        // query is willing to return and pages over it, instead of inventing a second limit.
        [HttpGet]
        public async Task<IActionResult> ResultsData(string? q, int page = 1, int pageSize = 25, CancellationToken ct = default)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            var res = await _search.SearchAsync(ctx, q, perType: EntityRegistry.SearchTake, cancellationToken: ct);

            var all = res.Groups
                .SelectMany(g => g.Hits.Select(h => new GlobalSearchRow(
                    g.Code, g.LabelAr, g.LabelEn, g.Icon, h.Id, h.Label, h.Url)))
                .ToList();

            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 200) pageSize = 25;
            int total = all.Count;
            int pages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
            if (page > pages) page = pages;

            // Said on the page, because every type is capped twice and a count that looks complete
            // when it is not is worse than one that admits its own ceiling.
            // WHETHER A SEARCH WAS ACTUALLY MADE, decided here rather than guessed at in the browser.
            // An empty table means two different things — "nothing was asked" when the page was just
            // opened, and "nothing matched" after a search — and telling someone their search failed
            // before they made one is the worse of the two mistakes. The page cannot tell them apart
            // reliably from the tag input, whose value is Tagify's own encoding, so the server says.
            // A flag, not the term: a header carrying an Arabic query would not survive the trip.
            Response.Headers["X-Asked"] = (q ?? "").Trim().Length >= 2 ? "1" : "0";
            Response.Headers["X-Truncated"] = res.Groups.Any(g => g.Truncated) ? "1" : "0";
            Response.Headers["X-Types"] = res.TypesSearched.ToString(CultureInfo.InvariantCulture);
            Response.Headers["X-Total"] = total.ToString(CultureInfo.InvariantCulture);
            Response.Headers["X-Page"] = page.ToString(CultureInfo.InvariantCulture);
            Response.Headers["X-Pages"] = pages.ToString(CultureInfo.InvariantCulture);

            return PartialView("_ResultRows", all.Skip((page - 1) * pageSize).Take(pageSize).ToList());
        }
    }
}
