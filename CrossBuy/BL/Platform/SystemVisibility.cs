using CrossBuy.Models.Menu;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform
{
    /// <summary>One screen a person may open, with the system it belongs to.</summary>
    /// <remarks>
    /// It carries the ROUTE, not a URL. Building a URL needs <c>Url.Action</c>, which needs an action
    /// context, and this service is deliberately HttpContext-free like the rest of the platform kernel.
    /// So the caller — which is always a controller — builds the link, and the routing decides whether a
    /// value lands in the path or the query. A string concatenated here would survive a routing change
    /// on paper and break in the browser.
    /// </remarks>
    public sealed record ScreenHit(string SystemKey, string SystemAr, string SystemEn, string Icon,
                                   string LabelAr, string LabelEn, string Action, string Controller,
                                   IReadOnlyDictionary<string, string>? RouteValues);

    /// <summary>
    /// Which systems a person may open, and which of their SCREENS match what they typed.
    /// </summary>
    /// <remarks>
    /// The visibility rule used to live inline in <c>PortalController.Choose</c>, which was fine while
    /// the opening screen was the only thing that asked. The header search box now asks the same
    /// question, and a second copy of a permission rule is the kind that drifts silently: the launcher
    /// would hide a system the search box still offered. One rule, two callers.
    /// </remarks>
    public interface ISystemVisibility
    {
        Task<IReadOnlyList<SystemEntry>> VisibleAsync(BusinessContext? context, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ScreenHit>> FindScreensAsync(BusinessContext? context, string? term, int take, CancellationToken cancellationToken = default);
    }

    public sealed class SystemVisibility : ISystemVisibility
    {
        private readonly IEnumerable<IModuleAccessService> _modules;
        private readonly ILogger<SystemVisibility> _log;

        public SystemVisibility(IEnumerable<IModuleAccessService> modules, ILogger<SystemVisibility> log)
        { _modules = modules; _log = log; }

        public async Task<IReadOnlyList<SystemEntry>> VisibleAsync(BusinessContext? context, CancellationToken cancellationToken = default)
        {
            var visible = new List<SystemEntry>();
            foreach (var s in SystemCatalog.All)
            {
                // The Communication Hub carries no module permission: its gate is a resolved context naming
                // both a company and an employee, which is the sidebar's own rule and the controller's.
                if (s.Key == "chat" && (context == null || context.CompanyId <= 0 || context.EmployeeId is not > 0)) continue;
                if (s.Scope == null || context == null) { if (s.Scope == null) visible.Add(s); continue; }
                var module = _modules.FirstOrDefault(m => string.Equals(m.Scope, s.Scope, StringComparison.OrdinalIgnoreCase));
                if (module == null) { visible.Add(s); continue; }   // a module with no access service has never gated its menu
                try { if (!module.Actions.Contains("read") || await module.CanAsync(context, "read", null, cancellationToken)) visible.Add(s); }
                catch (Exception ex) { _log.LogWarning(ex, "System visibility: {Scope} could not answer read", s.Scope); }
            }
            return visible;
        }

        /// <summary>The screens whose own name matches, plus the front door of a system whose name matches.</summary>
        /// <remarks>
        /// A SYSTEM match offers only that system's front door, never all of its screens. The hospital
        /// publishes well over a hundred; letting the word "المستشفى" expand to every one of them would
        /// bury whatever the person was actually looking for under a menu they already have. So the two
        /// matches answer two different questions: the system name answers "take me to Hospital", a
        /// screen name answers "take me to the patients screen".
        ///
        /// Ranking is prefix before contains, because a person typing three letters of a screen's name is
        /// far more often starting it than quoting its middle. Within a rank the catalogue's own order is
        /// kept — the order the sidebar and the opening screen already show.
        /// </remarks>
        public async Task<IReadOnlyList<ScreenHit>> FindScreensAsync(BusinessContext? context, string? term, int take, CancellationToken cancellationToken = default)
        {
            var t = ArabicText.Fold(term);
            if (t.Length < 2 || take <= 0) return Array.Empty<ScreenHit>();

            var visible = await VisibleAsync(context, cancellationToken);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ranked = new List<(int Rank, int Order, ScreenHit Hit)>();
            int order = 0;

            void Offer(SystemEntry s, string ar, string en, string action, string controller, IReadOnlyDictionary<string, string>? rv)
            {
                int rank = Rank(ar, en, t);
                if (rank < 0) return;
                var key = controller + "/" + action + "?" + (rv == null ? "" : string.Join("&", rv.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value)));
                if (!seen.Add(key)) return;
                ranked.Add((rank, order++, new ScreenHit(s.Key, s.NameAr, s.NameEn, s.Icon, ar, en, action, controller, rv)));
            }

            foreach (var s in visible)
            {
                Offer(s, s.NameAr, s.NameEn, s.Action, s.Controller, null);
                foreach (var i in SystemCatalog.ScreensOf(s))
                    Offer(s, i.LabelAr, i.LabelEn, i.Action, i.Controller, i.RouteValues);
            }

            // The platform surfaces — Workspace, the Reports Centre, the agenda — belong to no system and
            // are reachable by every signed-in person, so the catalogue above never lists them. They are
            // exactly the screens someone reaches for by name, which is why leaving them out would make
            // the box look broken for the most ordinary word a person could type.
            if (context is { CompanyId: > 0 })
            {
                var platform = new SystemEntry("platform", SystemCatalog.Work, "المنصّة", "Platform", "", "",
                    "Index", "Workspace", "ki-outline ki-abstract-26", "#4A5878", "#93C5FD", null, false);
                foreach (var c in MainMenu.Platform())
                    foreach (var i in c.Items)
                    {
                        // Same exclusions the catalogue's own Flatten applies: a placeholder is not a screen,
                        // and a permission-hooked row is left out rather than shown to someone who is refused.
                        if (string.IsNullOrEmpty(i.Action) || i.Soon || i.Perm != null) continue;
                        Offer(platform, i.LabelAr, i.LabelEn, i.Action, i.Controller, i.RouteValues);
                    }
            }

            return ranked.OrderBy(x => x.Rank).ThenBy(x => x.Order).Take(take).Select(x => x.Hit).ToList();
        }

        /// 0 = a name starts with the term, 1 = a name contains it, -1 = no match.
        /// <remarks>
        /// THE DEFINITE ARTICLE IS WHY THIS IS NOT A ONE-LINE Contains. Screens are named the way a
        /// menu names them — "فواتير المبيعات", "أوامر التشغيل" — and a person reaches for them the
        /// way speech does, with the article: "الفواتير", "الأوامر". Folding alone matched neither
        /// direction, so the first run of this found records for "الفواتير" and not one screen —
        /// the exact complaint it was written to answer. Both sides are therefore tried with and
        /// without the article, and a WORD of the name counts as well as its start, so "مبيعات"
        /// reaches "فواتير المبيعات" too.
        ///
        /// The article is only dropped from a word long enough to survive it: taking two letters off
        /// a three-letter word leaves a fragment that would match almost anything.
        /// </remarks>
        private static int Rank(string ar, string en, string folded)
        {
            var terms = Variants(folded).ToArray();
            var names = new[] { ArabicText.Fold(ar), ArabicText.Fold(en) };

            foreach (var name in names)
                foreach (var n in Variants(name))
                    foreach (var t in terms)
                        if (n.StartsWith(t, StringComparison.Ordinal)) return 0;

            foreach (var name in names)
            {
                foreach (var t in terms)
                    if (name.Contains(t, StringComparison.Ordinal)) return 1;
                foreach (var w in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    foreach (var wv in Variants(w))
                        foreach (var t in terms)
                            if (wv.StartsWith(t, StringComparison.Ordinal)) return 1;
            }
            return -1;
        }

        private static IEnumerable<string> Variants(string s)
        {
            yield return s;
            if (s.Length > 3 && s.StartsWith("ال", StringComparison.Ordinal)) yield return s[2..];
        }
    }
}
