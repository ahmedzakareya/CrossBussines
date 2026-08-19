using System.Text.RegularExpressions;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — THE CATALOG.
    //
    // The single source of truth for "which reports exist and what shape are they". Same role, and the same
    // frozen-vocabulary discipline, as IEntityRegistry has for entity codes: platform code may not accept a
    // free-text report code, and a code that is not registered is an exception rather than an empty result.
    //
    // WHY CODE-FIRST AND NOT A TABLE. A definition carries the report's DataSourceKey and PermissionKey. If
    // definitions lived in the database, then editing a row would repoint a report at a different data source or
    // a weaker permission — a privilege escalation performed with an UPDATE statement. Templates are the
    // database-backed, user-editable half, and they can only ever choose among what a definition declares.
    // ============================================================================================
    public interface IReportCatalog
    {
        // Every registered definition, ordered by module, then category, then SortOrder, then code.
        IReadOnlyList<ReportDefinition> GetDefinitions();

        // Throws ReportNotRegisteredException for an unknown code — an unregistered code is a wiring bug.
        ReportDefinition GetDefinition(string reportCode);

        bool TryGetDefinition(string? reportCode, out ReportDefinition? definition);

        bool IsRegistered(string? reportCode);

        // Filtered browse for the report picker. Every argument is optional; `search` matches code, Arabic and
        // English title, case-insensitively.
        IReadOnlyList<ReportDefinition> Query(string? module = null, string? categoryKey = null,
            string? tag = null, string? search = null);

        // Distinct category keys present in the catalog, in first-seen order. Used to materialise the platform
        // ReportCategories rows without a hand-maintained seed list.
        IReadOnlyList<string> GetCategoryKeys();

        IReadOnlyList<string> GetModules();
    }

    // ============================================================================================
    // THE EXTENSION POINT.
    //
    // A module contributes its reports by registering one of these. That is the whole integration surface: a
    // module never touches the catalog, the engine, or any renderer.
    //
    // CONTRACT — a provider must be PURE:
    //   * no DbContext, no IHttpContextAccessor, no request state, no I/O;
    //   * the same definitions on every call, for the life of the process.
    // Because of that it is registered as a SINGLETON, and so is the catalog over it. This is deliberate and is
    // the one safe shape for a singleton in this codebase: it injects nothing scoped, so it cannot reproduce the
    // captured-scoped-service defect CLAUDE.md records (a scoped service captured by a process-lifetime object
    // serves every later request from the first request's state).
    //
    // A report whose AVAILABILITY depends on data (a capability flag, a tenant setting) is still declared here
    // and filtered at AUTHORIZATION time by IReportPermissionEvaluator, which is scoped and may read anything.
    // ============================================================================================
    public interface IReportDefinitionProvider
    {
        // Stable owner name for diagnostics ("Accounting", "Platform"). Appears in the duplicate-code error so a
        // clash names both providers.
        string ProviderName { get; }

        IEnumerable<ReportDefinition> GetDefinitions();
    }

    public class ReportCatalog : IReportCatalog
    {
        // A dotted code, "<Module>.<Report>", ASCII, PascalCase segments. Validated so the code stays usable as
        // a URL segment, a file-name fragment and a persisted key without escaping.
        private static readonly Regex CodePattern =
            new(@"^[A-Z][A-Za-z0-9]{1,39}(\.[A-Z][A-Za-z0-9]{1,39}){1,3}$", RegexOptions.Compiled);

        private readonly Dictionary<string, ReportDefinition> _byCode;
        private readonly List<ReportDefinition> _ordered;

        public ReportCatalog(IEnumerable<IReportDefinitionProvider> providers)
        {
            // Validation happens ONCE, in the constructor, so a bad definition fails at DI-graph construction
            // (ValidateOnBuild) rather than the first time a user opens the report browser. The kernel learned
            // this the hard way: "112 green tests coexisted with an application that could not boot."
            var byCode = new Dictionary<string, ReportDefinition>(StringComparer.Ordinal);
            var owner = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var provider in providers)
            {
                var providerName = string.IsNullOrWhiteSpace(provider.ProviderName)
                    ? provider.GetType().Name
                    : provider.ProviderName;

                foreach (var definition in provider.GetDefinitions())
                {
                    Validate(definition, providerName);

                    if (byCode.TryGetValue(definition.Code, out _))
                        throw new ReportingException(
                            $"Duplicate report code '{definition.Code}': declared by both " +
                            $"'{owner[definition.Code]}' and '{providerName}'. Report codes are a frozen, " +
                            "globally unique vocabulary.");

                    byCode[definition.Code] = definition;
                    owner[definition.Code] = providerName;
                }
            }

            _byCode = byCode;
            _ordered = byCode.Values
                .OrderBy(d => d.Module, StringComparer.Ordinal)
                .ThenBy(d => d.CategoryKey ?? "￿", StringComparer.Ordinal)
                .ThenBy(d => d.SortOrder)
                .ThenBy(d => d.Code, StringComparer.Ordinal)
                .ToList();
        }

        public IReadOnlyList<ReportDefinition> GetDefinitions() => _ordered;

        public ReportDefinition GetDefinition(string reportCode) =>
            _byCode.TryGetValue(reportCode ?? "", out var definition)
                ? definition
                : throw new ReportNotRegisteredException(reportCode);

        public bool TryGetDefinition(string? reportCode, out ReportDefinition? definition)
        {
            definition = null;
            if (string.IsNullOrEmpty(reportCode)) return false;
            if (!_byCode.TryGetValue(reportCode, out var found)) return false;
            definition = found;
            return true;
        }

        public bool IsRegistered(string? reportCode) =>
            !string.IsNullOrEmpty(reportCode) && _byCode.ContainsKey(reportCode);

        public IReadOnlyList<ReportDefinition> Query(string? module = null, string? categoryKey = null,
            string? tag = null, string? search = null)
        {
            IEnumerable<ReportDefinition> q = _ordered;

            if (!string.IsNullOrWhiteSpace(module))
                q = q.Where(d => string.Equals(d.Module, module, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(categoryKey))
                q = q.Where(d => string.Equals(d.CategoryKey, categoryKey, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(tag))
                q = q.Where(d => d.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                q = q.Where(d =>
                    d.Code.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                    d.TitleAr.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                    d.TitleEn.Contains(s, StringComparison.OrdinalIgnoreCase));
            }

            return q.ToList();
        }

        public IReadOnlyList<string> GetCategoryKeys() => _ordered
            .Select(d => d.CategoryKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        public IReadOnlyList<string> GetModules() => _ordered
            .Select(d => d.Module)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // ------------------------------------------------------------------------------------------------
        // Definition validation. Everything checked here is something that would otherwise fail deep inside a
        // render, where the error is unrecognisable.
        // ------------------------------------------------------------------------------------------------
        private static void Validate(ReportDefinition d, string providerName)
        {
            string Where(string what) => $"Report definition '{d.Code}' from provider '{providerName}': {what}";

            if (string.IsNullOrWhiteSpace(d.Code) || !CodePattern.IsMatch(d.Code))
                throw new ReportingException(Where(
                    "Code must be a dotted PascalCase code such as 'Accounting.TrialBalance'."));

            if (string.IsNullOrWhiteSpace(d.Module))
                throw new ReportingException(Where("Module is required."));

            if (string.IsNullOrWhiteSpace(d.TitleAr) || string.IsNullOrWhiteSpace(d.TitleEn))
                throw new ReportingException(Where("both TitleAr and TitleEn are required (every user-facing " +
                                                   "string in this platform is bilingual)."));

            if (string.IsNullOrWhiteSpace(d.DataSourceKey))
                throw new ReportingException(Where("DataSourceKey is required."));

            // The single most important check in this method. A definition with no permission key would be a
            // report anybody can run, and it must not be possible to reach that state by omission.
            if (string.IsNullOrWhiteSpace(d.PermissionKey))
                throw new ReportingException(Where(
                    "PermissionKey is required. Use ReportPermissions.Public explicitly if the report is " +
                    "genuinely unrestricted — omission must never mean unrestricted."));

            if (d.Columns.Count == 0)
                throw new ReportingException(Where("at least one column is required."));

            var columnKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in d.Columns)
            {
                if (string.IsNullOrWhiteSpace(c.Key))
                    throw new ReportingException(Where("a column has an empty Key."));
                if (!columnKeys.Add(c.Key))
                    throw new ReportingException(Where($"duplicate column key '{c.Key}'."));
                if (string.IsNullOrWhiteSpace(c.TitleAr) || string.IsNullOrWhiteSpace(c.TitleEn))
                    throw new ReportingException(Where($"column '{c.Key}' needs both TitleAr and TitleEn."));
                if (c.Type == ReportFieldType.EntityRef && string.IsNullOrWhiteSpace(c.LookupEntityCode))
                    throw new ReportingException(Where(
                        $"column '{c.Key}' is an EntityRef but names no LookupEntityCode."));
                if (c.Aggregate != ReportAggregate.None && !c.IsNumeric
                    && c.Aggregate is not (ReportAggregate.Count or ReportAggregate.CountDistinct
                        or ReportAggregate.Min or ReportAggregate.Max))
                    throw new ReportingException(Where(
                        $"column '{c.Key}' is not numeric, so aggregate '{c.Aggregate}' is undefined for it."));
            }

            var parameterKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in d.Parameters)
            {
                if (string.IsNullOrWhiteSpace(p.Key))
                    throw new ReportingException(Where("a parameter has an empty Key."));
                if (!parameterKeys.Add(p.Key))
                    throw new ReportingException(Where($"duplicate parameter key '{p.Key}'."));
                if (string.IsNullOrWhiteSpace(p.TitleAr) || string.IsNullOrWhiteSpace(p.TitleEn))
                    throw new ReportingException(Where($"parameter '{p.Key}' needs both TitleAr and TitleEn."));
                if (p.Type == ReportFieldType.EntityRef && string.IsNullOrWhiteSpace(p.LookupEntityCode))
                    throw new ReportingException(Where(
                        $"parameter '{p.Key}' is an EntityRef but names no LookupEntityCode."));

                // A system-supplied parameter is filled by the engine, so "required" is either automatic (the
                // engine always supplies it) or unsatisfiable (it does not). Either way the flag is a lie.
                if (p.SystemSupplied && p.Required)
                    throw new ReportingException(Where(
                        $"parameter '{p.Key}' is both SystemSupplied and Required. A system-supplied parameter " +
                        "is filled by the engine; marking it Required tells the user to provide something they " +
                        "cannot."));
            }

            // Default query intent must reference declared, permitted columns — checked here so a definition
            // cannot ship a default sort on a column that does not exist.
            foreach (var s in d.DefaultSorts)
                RequireColumn(d, s.Field, "DefaultSorts", providerName, mustBe: c => c.Sortable, what: "sortable");

            foreach (var g in d.DefaultGroupings)
                RequireColumn(d, g.Field, "DefaultGroupings", providerName, mustBe: c => c.Groupable, what: "groupable");

            foreach (var f in d.DefaultFilters)
                RequireColumn(d, f.Field, "DefaultFilters", providerName, mustBe: c => c.Filterable, what: "filterable");

            if (d.Capabilities.MaxRows < 0 || d.Capabilities.PreviewRows < 0)
                throw new ReportingException(Where("row limits may not be negative."));

            if (!d.Capabilities.SupportsFormat(d.DefaultFormat))
                throw new ReportingException(Where(
                    $"DefaultFormat '{d.DefaultFormat}' is not in Capabilities.Formats."));
        }

        private static void RequireColumn(ReportDefinition d, string field, string where, string providerName,
            Func<ReportColumn, bool> mustBe, string what)
        {
            var column = d.FindColumn(field);
            if (column == null)
                throw new ReportingException(
                    $"Report definition '{d.Code}' from provider '{providerName}': {where} references column " +
                    $"'{field}', which the definition does not declare.");
            if (column.Internal)
                throw new ReportingException(
                    $"Report definition '{d.Code}' from provider '{providerName}': {where} references internal " +
                    $"column '{field}'.");
            if (!mustBe(column))
                throw new ReportingException(
                    $"Report definition '{d.Code}' from provider '{providerName}': {where} references column " +
                    $"'{field}', which is not marked {what}.");
        }
    }
}