using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-030 §4) — batch resolution of employee display identity.
    //
    // WHY A SERVICE FOR THIS
    //
    // A 50-comment page references maybe 8 distinct authors. Resolving a name per row is 50 queries; resolving
    // per PAGE is one. Every read path in this platform therefore collects its employee ids first and resolves
    // them once, which is why every ToDto method here takes a pre-resolved dictionary instead of a DbContext.
    //
    // It returns BOTH languages rather than a culture-resolved string, so one cached result serves an Arabic
    // and an English caller — the same twin-column convention the parallel team's feature modules use, and the
    // reason a DTO from this platform is never culture-poisoned.
    //
    // NO COMPANY FILTER, and that is a considered decision rather than an oversight. Two reasons:
    //   1. It resolves DISPLAY NAMES for ids this platform already authorized as authors/actors on rows the
    //      caller may read. The authorization happened upstream; adding a company filter here would blank the
    //      name of a legitimately visible author whose Employee row sits under a different EmpCompanyID (which
    //      happens in this database — see EntityRegistry's own documented Employee deviation).
    //   2. A name is not company-scoped data in this product: EntityRegistry's employee search and resolve
    //      deliberately carry no company filter either, and diverging here would make the same person render
    //      differently in two parts of one screen.
    // Membership decisions — who is in a team, who may be notified — go through ICommPrincipalResolver, which
    // DOES intersect by company. Naming and authorizing are kept apart on purpose.
    // =============================================================================================
    public interface ICommActorDirectory
    {
        // Resolves a batch of (possibly null, possibly duplicated) employee ids in one query.
        Task<IReadOnlyDictionary<int, CommActorDto>> ResolveAsync(
            IEnumerable<int?> employeeIds, CancellationToken cancellationToken = default);

        Task<IReadOnlyDictionary<int, CommActorDto>> ResolveAsync(
            IEnumerable<int> employeeIds, CancellationToken cancellationToken = default);

        // Never returns null: an id with no Employee row degrades to "#id", exactly as the kernel's registry
        // resolve does for a deleted record. A timeline row must not vanish because its author was purged.
        CommActorDto Get(IReadOnlyDictionary<int, CommActorDto> resolved, int? employeeId);
    }

    public sealed class CommActorDirectory : ICommActorDirectory
    {
        private readonly CrossDbContext _db;

        public CommActorDirectory(CrossDbContext db) => _db = db;

        public Task<IReadOnlyDictionary<int, CommActorDto>> ResolveAsync(
            IEnumerable<int?> employeeIds, CancellationToken cancellationToken = default)
            => ResolveAsync(
                (employeeIds ?? Array.Empty<int?>()).Where(id => id is > 0).Select(id => id!.Value),
                cancellationToken);

        public async Task<IReadOnlyDictionary<int, CommActorDto>> ResolveAsync(
            IEnumerable<int> employeeIds, CancellationToken cancellationToken = default)
        {
            var ids = (employeeIds ?? Array.Empty<int>()).Where(id => id > 0).Distinct().ToList();
            if (ids.Count == 0)
                return new Dictionary<int, CommActorDto>();

            var rows = await _db.Employee.AsNoTracking()
                .Where(e => ids.Contains(e.ID))
                .Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage })
                .ToListAsync(cancellationToken);

            return rows.ToDictionary(
                r => r.ID,
                r => new CommActorDto
                {
                    EmployeeId = r.ID,
                    NameAr = r.FullName,
                    NameEn = string.IsNullOrWhiteSpace(r.FullNameEn) ? r.FullName : r.FullNameEn,
                    AvatarUrl = r.ProfileImage,
                });
        }

        public CommActorDto Get(IReadOnlyDictionary<int, CommActorDto> resolved, int? employeeId)
        {
            int id = employeeId ?? 0;
            if (id > 0 && resolved != null && resolved.TryGetValue(id, out var actor)) return actor;

            // The degraded actor. Names are left null so a caller can tell "unknown" from "named", while
            // CommActorDto.Display still renders "#id" rather than blank.
            return new CommActorDto { EmployeeId = id, NameAr = null, NameEn = null, AvatarUrl = null };
        }
    }
}
