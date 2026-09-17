using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037 §Dataset) — the dataset registry.
    //
    // Deliberately the same shape and the same failure behaviour as IReportDataSourceRegistry: validate in the
    // constructor, refuse duplicates, throw a named exception on an unknown code. One mental model for "how the
    // platform finds a pluggable thing" is worth more than a marginally cleverer second one.
    //
    // WHAT IS REGISTERED HERE. Nothing, by this class — and that is still the design. Every dataset is
    // contributed by its own module as an IReportDatasetDefinition, and the registry only collects, validates
    // and refuses duplicates. The platform does not know Accounting from Inventory, which is why adding a
    // module's datasets has never required editing this file.
    //
    // (This paragraph used to say the registry "ships empty". That was true of the increment that wrote it
    // and has not been true since the module datasets landed: ReportingRegistration now contributes the
    // Accounting, Inventory, CRM, Roster, Admin, document and register datasets. The distinction the old
    // wording lost is the one that matters — the registry holds no datasets of its OWN, which is not the
    // same as holding none.)
    //
    // An empty registry would still not be a broken one: ListForStudioAsync returns nothing and the Studio
    // shows nothing, which is the honest state for a deployment that has wired no modules.
    // ============================================================================================
    public sealed class ReportDatasetRegistry : IReportDatasetRegistry
    {
        private readonly Dictionary<string, IReportDatasetDefinition> _byCode;
        private readonly IReportPermissionEvaluator _permissions;

        public ReportDatasetRegistry(
            IEnumerable<IReportDatasetDefinition> datasets,
            IReportPermissionEvaluator permissions)
        {
            _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
            _byCode = new Dictionary<string, IReportDatasetDefinition>(StringComparer.Ordinal);

            var errors = new List<string>();

            foreach (var dataset in datasets)
            {
                if (string.IsNullOrWhiteSpace(dataset.DatasetCode))
                {
                    errors.Add($"Dataset '{dataset.GetType().Name}' has an empty DatasetCode.");
                    continue;
                }

                // Last-registration-wins would let one module silently hijack another's dataset — and with it
                // that dataset's permission key. Two claimants is a wiring error, and it fails at graph
                // construction where the author can see it.
                if (_byCode.TryGetValue(dataset.DatasetCode, out var existing))
                {
                    errors.Add($"Duplicate dataset code '{dataset.DatasetCode}': claimed by both " +
                               $"'{existing.GetType().Name}' and '{dataset.GetType().Name}'.");
                    continue;
                }

                // Validation at CONSTRUCTION, not at first use. Every rule in the validator is one that would
                // otherwise surface on a user's screen as a message about an internal key.
                errors.AddRange(ReportDatasetValidator.Validate(dataset));
                _byCode[dataset.DatasetCode] = dataset;
            }

            if (errors.Count > 0)
                throw new ReportingException(
                    "Report dataset definitions are invalid:" + Environment.NewLine +
                    string.Join(Environment.NewLine, errors.Select(e => "  * " + e)));
        }

        public IReadOnlyCollection<string> RegisteredCodes => _byCode.Keys;

        public IReportDatasetDefinition Resolve(string datasetCode) =>
            _byCode.TryGetValue(datasetCode ?? "", out var dataset)
                ? dataset
                : throw new ReportDatasetNotRegisteredException(datasetCode);

        public bool TryResolve(string? datasetCode, out IReportDatasetDefinition? dataset)
        {
            dataset = null;
            if (string.IsNullOrEmpty(datasetCode)) return false;
            if (!_byCode.TryGetValue(datasetCode, out var found)) return false;
            dataset = found;
            return true;
        }

        // What this caller may build a Studio report over.
        //
        // Three filters, and the ORDER is the cheap-to-expensive one: the two in-memory flags are checked before
        // the permission evaluator, so a deprecated or non-Studio dataset costs no authorization call.
        //
        // A DEPRECATED dataset is excluded from the builder while remaining resolvable by Resolve() — that
        // combination is the whole point of deprecation: saved reports keep working, new ones cannot be created.
        public async Task<IReadOnlyList<IReportDatasetDefinition>> ListForStudioAsync(
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            // Fail closed on an unresolved tenant, matching the platform rule that an unresolved company scope
            // reads no company-scoped data.
            if (context.CompanyId <= 0) return Array.Empty<IReportDatasetDefinition>();

            var results = new List<IReportDatasetDefinition>();

            foreach (var dataset in _byCode.Values.OrderBy(d => d.DatasetCode, StringComparer.Ordinal))
            {
                if (!dataset.AvailableInStudio) continue;
                if (dataset is ReportDatasetDefinition concrete && concrete.IsDeprecated) continue;

                if (await _permissions.HasPermissionAsync(dataset.RequiredPermissionKey, context, cancellationToken))
                    results.Add(dataset);
            }

            return results;
        }
    }
}
