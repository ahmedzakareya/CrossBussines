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

    /// <param name="OpensList">The Url opens a LIST filtered to this record rather than the record
    /// itself — true for the types that have no detail screen. A caller must SAY so instead of
    /// offering it as the record.</param>
    public sealed record GlobalSearchHit(int Id, string Label, string? Url, bool OpensList = false);
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
                                         int Id, string Label, string? Url, bool OpensList = false);

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

        // ---- ROW-LEVEL VISIBILITY --------------------------------------------------------------
        //
        // The module gate above answers "may you open this module". That is NOT the question a
        // record-level rule asks, and this service used to pass `null` as the target, so every
        // record-level rule in the product sat unasked while the dropdown said "within what you may
        // view". PermissionTarget's own file is the specification of which rules exist, and each of
        // these scopes has one:
        //
        //   Tasks           assignee / creator / hierarchy      (TaskId)
        //   Hr              self and direct reports             (SubjectEmployeeId)
        //   Projects        active membership                   (ProjectId)
        //   Communication   conversation membership             (ConversationId)
        //   Crm             the record owner — the one real record-owner rule in the codebase
        //
        // THE LIST IS AN ALLOW-LIST, NOT A SWEEP, and deliberately so. Accounting, Inventory and CRM
        // module "read" is open to every employee of a configured company BY DESIGN; asking those
        // modules a per-row question changes no answer and costs a call per row. Worse, sending a
        // target to an adapter that does not expect one risks a refusal that silently empties a type
        // from search — and a control that empties the product gets switched off rather than obeyed.
        // So the row question is asked exactly where a row rule is known to exist, and a scope joins
        // this list when its rule is written, not before.
        private static bool HasRowRule(string scope) =>
            scope == EntityRegistry.ScopeTasks ||
            scope == EntityRegistry.ScopeHr ||
            scope == EntityRegistry.ScopeProjects ||
            scope == EntityRegistry.ScopeCommunication ||
            scope == EntityRegistry.ScopeCrm;

        // A TYPE THAT HAS NO ROW RULE TO ASK, AND WHOSE ROWS ARE PEOPLE.
        //
        // The Hospital scope declares no record-level rule anywhere in the permission model — there is
        // no "my patients", no treating-clinician relationship to consult — so the only question the
        // engine can answer about a patient is "may this person open the hospital module". That is far
        // too wide a key for a clinical record: an HR manager holding hospital read could type three
        // letters in the header box and get patients by name and MRN. Observed, not theorised.
        //
        // Closing it here rather than by clearing SupportsSearch, because that flag also feeds the
        // record picker and the task link picker, where a clinician IS choosing a patient on purpose
        // and inside a screen with its own gate. The front desk likewise has its own patient search
        // (HospitalFrontDeskService / SearchPatients) and does not come through here at all — so this
        // costs no clinical workflow, which is exactly why it is safe to fail closed.
        //
        // This is a lock, not a verdict: the moment a treating-relationship rule exists, delete the
        // entry and let HasRowRule ask it like every other scope.
        private static bool NotInTheGlobalBox(string code) => code == EntityRegistry.HospitalPatient;

        // The specific factory where one exists, so the adapter reads the field it actually consults;
        // EntityType/EntityId otherwise, which PermissionTarget carries precisely so an adapter can
        // look the record up — that is how CRM reaches a record's owner from a search hit.
        private static PermissionTarget TargetFor(string code, int entityId, int companyId) => code switch
        {
            EntityRegistry.Task => PermissionTarget.ForTask(entityId, companyId),
            EntityRegistry.Employee => PermissionTarget.ForSubjectEmployee(entityId, companyId),
            EntityRegistry.Project => PermissionTarget.ForProject(entityId, companyId),
            _ => new PermissionTarget { EntityType = code, EntityId = entityId, CompanyId = companyId },
        };

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
                if (NotInTheGlobalBox(def.Code)) continue;
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

                // Ask the RECORD question too, where the scope has a record rule. One extra row is
                // kept beyond the cap purely so "more matched" can be stated as a fact rather than
                // inferred from a pre-filter count that no longer describes what is visible.
                IReadOnlyList<EntitySearchResult> visible;
                if (HasRowRule(scope))
                {
                    var kept = new List<EntitySearchResult>(Math.Min(rows.Count, perType + 1));
                    foreach (var r in rows)
                    {
                        if (kept.Count > perType) break;
                        if (await mod.CanAsync(ctx, ReadAction(scope), TargetFor(def.Code, r.EntityId, ctx.CompanyId), cancellationToken))
                            kept.Add(r);
                    }
                    visible = kept;
                }
                else visible = rows;

                if (visible.Count == 0) continue;   // every match was refused: the type is not shown at all

                var hits = visible.Take(perType)
                    .Select(r => new GlobalSearchHit(r.EntityId, r.Label,
                        r.Url ?? _registry.BuildUrl(def.Code, r.EntityId), r.OpensList))
                    .ToList();
                total += hits.Count;
                groups.Add(new GlobalSearchGroup(def.Code, def.DisplayNameAr, def.DisplayNameEn, def.Icon, def.Color,
                    hits, Truncated: visible.Count > hits.Count));
            }

            return new GlobalSearchResponse(t, total, groups, searched);
        }
    }
}
