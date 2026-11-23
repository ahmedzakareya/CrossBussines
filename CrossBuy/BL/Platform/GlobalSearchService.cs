using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform
{
    // Global cross-entity search (Search module, long the platform's weakest surface). It reuses the kernel the
    // record picker already stands on: IEntityRegistry.SearchAsync company-scopes each type's query, and each
    // module's IModuleAccessService gates whether the caller may see that type at all. So this NEVER widens
    // exposure beyond the existing picker — it adds a permission layer on top and groups the hits by type.
    //
    // WHY THE KERNEL DOC ANTICIPATED THIS: IModuleAccessService's own comment names "Search" as a planned reuser
    // of the one permission engine. This is that reuse — deny-by-default, company from the context, no HttpContext.
    public interface IGlobalSearchService
    {
        Task<GlobalSearchResponse> SearchAsync(BusinessContext? context, string? term, int perType = 6, CancellationToken cancellationToken = default);
    }

    public sealed record GlobalSearchHit(int Id, string Label, string? Url);
    /// <param name="Truncated">
    /// More rows matched this type than are being shown. The page says so instead of letting the
    /// count read as the whole answer: each type is capped twice — EntityRegistry.SearchTake caps
    /// the query at 20, and perType caps what is returned — so a figure here could never be a true
    /// match count, and printing one would be a confident number that happens to be wrong. Saying
    /// "the first twelve" is the most that is known, and it is true.
    /// </param>
    public sealed record GlobalSearchGroup(string Code, string LabelAr, string LabelEn, string Icon, string Color, IReadOnlyList<GlobalSearchHit> Hits, bool Truncated = false);
    /// <param name="TypesSearched">
    /// How many record types were actually looked in — those this caller may read, and that answered.
    /// A result page that says only "1 result" leaves a real doubt unanswered: was there one match,
    /// or did it only look in one place? Only types with hits appear in <c>Groups</c>, so without
    /// this the breadth of the search is invisible exactly when it matters most, on a thin answer.
    /// </param>
    public sealed record GlobalSearchResponse(string Term, int Total, IReadOnlyList<GlobalSearchGroup> Groups, int TypesSearched = 0);

    /// <summary>
    /// One hit, flattened out of its group and carrying its type with it.
    /// </summary>
    /// <remarks>
    /// The results PAGE is a list screen like the ninety-six others in the product: one table, one
    /// search box, a page size and a pager, with rows fetched and injected rather than the page
    /// being reloaded. That shape needs a flat row that knows its own type, because the grouping
    /// moves from the markup into a column.
    /// </remarks>
    public sealed record GlobalSearchRow(string TypeCode, string TypeLabelAr, string TypeLabelEn, string Icon,
                                         int Id, string Label, string? Url);

    public sealed class GlobalSearchService : IGlobalSearchService
    {
        private readonly IEntityRegistry _registry;
        private readonly IReadOnlyDictionary<string, IModuleAccessService> _modules;

        public GlobalSearchService(IEntityRegistry registry, IEnumerable<IModuleAccessService> modules)
        {
            _registry = registry;
            var map = new Dictionary<string, IModuleAccessService>(StringComparer.Ordinal);
            foreach (var m in modules) map[m.Scope] = m;   // one service per scope
            _modules = map;
        }

        // Every module gates a plain "read" except POS, whose basic action is "view".
        private static string ReadAction(string scope) => scope == EntityRegistry.ScopePos ? "view" : "read";

        public async Task<GlobalSearchResponse> SearchAsync(BusinessContext? context, string? term, int perType = 6, CancellationToken cancellationToken = default)
        {
            var t = (term ?? "").Trim();
            var groups = new List<GlobalSearchGroup>();
            // min 2 chars, a resolved company — same floor the shell's search box enforces client-side.
            if (context is not { CompanyId: > 0 } ctx || t.Length < 2) return new GlobalSearchResponse(t, 0, groups);

            // Resolve each scope's visibility ONCE (many entity types share a scope).
            var allowed = new Dictionary<string, bool>(StringComparer.Ordinal);
            int total = 0;
            // Counted where the search actually RAN, so a type refused by permission or without a
            // query is never claimed as somewhere we looked.
            int searched = 0;

            foreach (var def in _registry.GetDefinitions())
            {
                if (!def.SupportsSearch) continue;
                var scope = def.PermissionScope;
                if (string.IsNullOrEmpty(scope) || scope == EntityRegistry.ScopeNone) continue;
                if (!_modules.TryGetValue(scope, out var mod)) continue;   // no gate for this scope → leave it out, never leak

                if (!allowed.TryGetValue(scope, out var ok))
                {
                    ok = await mod.CanAsync(ctx, ReadAction(scope), null, cancellationToken);
                    allowed[scope] = ok;
                }
                if (!ok) continue;

                List<EntitySearchResult> rows;
                try { rows = await _registry.SearchAsync(def.Code, t, ctx, cancellationToken); }
                catch { continue; }   // one type's broken query must never take down the whole search
                searched++;
                if (rows.Count == 0) continue;

                var hits = rows.Take(perType)
                    .Select(r => new GlobalSearchHit(r.EntityId, r.Label, r.Url ?? _registry.BuildUrl(def.Code, r.EntityId)))
                    .ToList();
                total += hits.Count;
                groups.Add(new GlobalSearchGroup(def.Code, def.DisplayNameAr, def.DisplayNameEn, def.Icon, def.Color,
                    hits, Truncated: rows.Count > hits.Count));
            }

            return new GlobalSearchResponse(t, total, groups, searched);
        }
    }
}
