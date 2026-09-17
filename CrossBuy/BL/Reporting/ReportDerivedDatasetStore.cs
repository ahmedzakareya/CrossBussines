using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // =============================================================================================
    // DERIVED DATASETS — storage, and the registry layer that surfaces them.
    //
    // TWO PIECES, AND THE SECOND ONE IS WHERE THE SECURITY ARGUMENT LANDS:
    //
    //   the store     reads and writes a company's saved narrowings
    //   the layer     puts the built definitions beside the code-authored ones, for that company only
    //
    // THE PROPERTY THAT MAKES THE LAYER SAFE WITHOUT A SECOND PERMISSION CHECK.
    //
    // A derived dataset can only be BUILT from its parent, and the layer builds it from the list the
    // inner registry just returned — which is already "the datasets this caller may build a report
    // over", permission-filtered. So a derivation whose parent the caller cannot see has no parent to
    // build from and simply never appears.
    //
    // That is not a shortcut, it is the design's own consequence: a derived dataset grants nothing, so
    // "visible exactly when its parent is visible" is the whole of its access rule. Writing a separate
    // permission evaluation here would create a second answer to a question already answered, and two
    // answers drift.
    //
    // A SPEC IS REBUILT ON EVERY LOAD, NEVER TRUSTED AS STORED. The stored JSON is an author's
    // intention; the definition is what the builder makes of it against TODAY's parent. A parent that
    // has since dropped a field, tightened an aggregate or gained a permission re-decides the
    // derivation on the spot — the same re-validation a saved visual layout gets when it is reopened,
    // and for the same reason: yesterday's permission must not become today's grant.
    // =============================================================================================

    public interface IReportDerivedDatasetStore
    {
        /// This company's live derivations, as stored. Not yet built — the layer does that.
        Task<IReadOnlyList<ReportDatasetSpec>> ListAsync(BusinessContext context,
            CancellationToken cancellationToken = default);

        /// Creates or updates one derivation. Returns the builder's verdict: a spec that does not build
        /// is not stored, so the table cannot hold a row that would be refused on load.
        Task<ReportDerivedDatasetResult> SaveAsync(ReportDerivedDatasetSpec spec, BusinessContext context,
            CancellationToken cancellationToken = default);

        /// Retires a derivation. Soft, because saved Studio reports may still name its code.
        Task<bool> DeleteAsync(string datasetCode, BusinessContext context,
            CancellationToken cancellationToken = default);
    }

    public sealed class ReportDerivedDatasetStore : IReportDerivedDatasetStore
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        private readonly CrossDbContext _db;
        private readonly IReportDatasetRegistry _code;
        private readonly IReportClock _clock;

        public ReportDerivedDatasetStore(CrossDbContext db, IReportDatasetRegistry code, IReportClock clock)
        {
            _db = db;
            _code = code;
            _clock = clock;
        }

        public async Task<IReadOnlyList<ReportDatasetSpec>> ListAsync(BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            // An unresolved tenant reads no company-scoped data. Same rule, same failure direction, as
            // every other read in the platform.
            if (context is null || context.CompanyId <= 0) return Array.Empty<ReportDatasetSpec>();

            return await _db.ReportDatasetSpecs.AsNoTracking()
                .Where(s => s.CompanyID == context.CompanyId && s.IsActive && s.DeletedAt == null)
                .OrderBy(s => s.DatasetCode)
                .ToListAsync(cancellationToken);
        }

        public async Task<ReportDerivedDatasetResult> SaveAsync(ReportDerivedDatasetSpec spec,
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            var refusal = new ReportDerivedDatasetResult();

            if (context is null || context.CompanyId <= 0)
            {
                refusal.Errors.Add("No company scope.");
                return refusal;
            }
            if (spec is null)
            {
                refusal.Errors.Add("No specification was supplied.");
                return refusal;
            }

            // THE PARENT IS RESOLVED THROUGH THE PERMISSION-FILTERED LIST, not by code lookup. An author
            // deriving from a dataset they may not run would otherwise mint themselves a new name for it
            // — the definition would inherit the parent's permission key and still be refused at run
            // time, but the catalogue would have gained an entry describing data they cannot see.
            var visible = await _code.ListForStudioAsync(context, cancellationToken);
            var parent = visible.FirstOrDefault(d =>
                string.Equals(d.DatasetCode, spec.ParentDatasetCode, StringComparison.Ordinal));

            // BUILDING IS VALIDATING. A spec that does not build is never written, so a row in this table
            // is one that passed the rules at least once — and is re-checked on every load besides.
            var built = ReportDerivedDatasetBuilder.Build(spec, parent, _code.RegisteredCodes);
            if (!built.Ok) return built;

            var now = _clock.LocalNow;
            var existing = await _db.ReportDatasetSpecs.FirstOrDefaultAsync(
                s => s.CompanyID == context.CompanyId
                     && s.DatasetCode == built.Definition!.DatasetCode
                     && s.DeletedAt == null, cancellationToken);

            var json = JsonSerializer.Serialize(spec, Json);

            if (existing is null)
            {
                _db.ReportDatasetSpecs.Add(new ReportDatasetSpec
                {
                    CompanyID = context.CompanyId,
                    DatasetCode = built.Definition!.DatasetCode,
                    ParentDatasetCode = spec.ParentDatasetCode,
                    TitleAr = built.Definition.TitleAr,
                    TitleEn = built.Definition.TitleEn,
                    DescriptionAr = built.Definition.DescriptionAr,
                    DescriptionEn = built.Definition.DescriptionEn,
                    SpecJson = json,
                    CreatedBy = context.EmployeeId,
                    CreatedAt = now,
                });
            }
            else
            {
                // THE PARENT IS NOT RE-POINTABLE. Repointing a derivation at another parent would carry
                // every report saved over it to a different permission key without anyone editing those
                // reports. Renaming the target is a new derivation, and deleting this one.
                if (!string.Equals(existing.ParentDatasetCode, spec.ParentDatasetCode, StringComparison.Ordinal))
                {
                    var refuse = new ReportDerivedDatasetResult();
                    refuse.Errors.Add("A derived dataset cannot be moved to another parent.");
                    return refuse;
                }

                existing.TitleAr = built.Definition!.TitleAr;
                existing.TitleEn = built.Definition.TitleEn;
                existing.DescriptionAr = built.Definition.DescriptionAr;
                existing.DescriptionEn = built.Definition.DescriptionEn;
                existing.SpecJson = json;
                existing.UpdatedBy = context.EmployeeId;
                existing.UpdatedAt = now;
            }

            await _db.SaveChangesAsync(cancellationToken);
            return built;
        }

        public async Task<bool> DeleteAsync(string datasetCode, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context is null || context.CompanyId <= 0) return false;

            var row = await _db.ReportDatasetSpecs.FirstOrDefaultAsync(
                s => s.CompanyID == context.CompanyId
                     && s.DatasetCode == datasetCode
                     && s.DeletedAt == null, cancellationToken);

            // A FOREIGN CODE ANSWERS EXACTLY AS A MISSING ONE. The company predicate is on the query, so
            // another tenant's derivation is not "forbidden", it is not there.
            if (row is null) return false;

            row.DeletedAt = _clock.LocalNow;
            row.IsActive = false;
            row.UpdatedBy = context.EmployeeId;
            row.UpdatedAt = _clock.LocalNow;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        internal static ReportDerivedDatasetSpec? Deserialise(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<ReportDerivedDatasetSpec>(json, Json); }
            catch (JsonException) { return null; }   // an unreadable row is skipped, never guessed at
        }
    }

    // =============================================================================================
    // THE LAYER. Decorates the code-authored registry; adds this company's derivations to the list a
    // caller may build over, and changes nothing else.
    //
    // Resolve and TryResolve are NOT extended, and that is correct rather than lazy: both are
    // context-free, and a derived dataset has no meaning without a company — resolving one by code
    // alone would be resolving it for whoever happened to ask. Every caller in the platform reaches
    // datasets through ListForStudioAsync, which carries the context.
    // =============================================================================================
    public sealed class DerivedAwareDatasetRegistry : IReportDatasetRegistry
    {
        private readonly IReportDatasetRegistry _inner;
        private readonly IReportDerivedDatasetStore _store;

        public DerivedAwareDatasetRegistry(IReportDatasetRegistry inner, IReportDerivedDatasetStore store)
        {
            _inner = inner;
            _store = store;
        }

        public IReportDatasetDefinition Resolve(string datasetCode) => _inner.Resolve(datasetCode);

        public bool TryResolve(string? datasetCode, out IReportDatasetDefinition? dataset) =>
            _inner.TryResolve(datasetCode, out dataset);

        public IReadOnlyCollection<string> RegisteredCodes => _inner.RegisteredCodes;

        public async Task<IReadOnlyList<IReportDatasetDefinition>> ListForStudioAsync(
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            var codeAuthored = await _inner.ListForStudioAsync(context, cancellationToken);
            if (context is null || context.CompanyId <= 0) return codeAuthored;

            var specs = await _store.ListAsync(context, cancellationToken);
            if (specs.Count == 0) return codeAuthored;

            var results = new List<IReportDatasetDefinition>(codeAuthored);

            foreach (var row in specs)
            {
                var spec = ReportDerivedDatasetStore.Deserialise(row.SpecJson);
                if (spec is null) continue;

                // The parent is looked up in the PERMITTED list. No parent here means the caller may not
                // see it, and a derivation of something you cannot see is something you cannot see.
                var parent = codeAuthored.FirstOrDefault(d =>
                    string.Equals(d.DatasetCode, row.ParentDatasetCode, StringComparison.Ordinal));
                if (parent is null) continue;

                // REBUILT, NOT TRUSTED. If the parent has changed since the row was written — a field
                // gone, an aggregate tightened — the build fails or narrows, and the stale derivation
                // drops out rather than describing a shape that no longer exists.
                var built = ReportDerivedDatasetBuilder.Build(spec, parent, _inner.RegisteredCodes);
                if (built.Ok && built.Definition is not null && built.Definition.AvailableInStudio)
                    results.Add(built.Definition);
            }

            return results;
        }
    }
}
