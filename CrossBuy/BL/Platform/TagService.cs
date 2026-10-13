using System.Globalization;
using System.Text.RegularExpressions;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform
{
    // =============================================================================================
    // THE PLATFORM TAG SERVICE — one vocabulary, many entity kinds.
    //
    // Task Management is its first caller and is not mentioned anywhere in it. That is the point: the
    // requirement is that CRM reuse this rather than grow CrmTag beside it, so the only thing that
    // makes a tag "a task tag" is the EntityType on its assignment.
    //
    // WHAT THIS SERVICE OWNS, and what it refuses to let a caller own:
    //   * NORMALIZATION. One function, used by writes and by lookups, so the database's unique index
    //     and the application can never disagree about whether two names collide.
    //   * THE THREE-WAY COMPANY CHECK on assignment. The tag's company, the entity's company and the
    //     caller's company must all agree. A controller cannot be trusted to remember that.
    //   * DELETE-vs-ARCHIVE. An unused tag is a typo and may go; a used one is history and is
    //     archived. Callers do not get to choose.
    //
    // WHAT IT DELIBERATELY DOES NOT DO:
    //   * decide who may manage definitions — that is the permission layer's job, checked by the
    //     controller before it ever reaches here;
    //   * emit Business Events. Tag maintenance is master-data housekeeping, not a business fact
    //     other modules react to, and the platform's own rule is that a declared event with no
    //     consumer is dead dispatch rows. BaseEntity's audit stamps carry who and when, which is the
    //     existing convention for this class of data. If a tag ever needs to drive workflow, that is
    //     a declared event with a transactional producer, not a side effect added here.
    // =============================================================================================
    public interface ITagService
    {
        Task<IReadOnlyList<Tag>> GetTagsAsync(int companyId, bool includeArchived = false, CancellationToken ct = default);

        Task<(bool ok, string? error, Tag? tag)> CreateTagAsync(int companyId, string name, int? actorEmployeeId, CancellationToken ct = default);
        Task<(bool ok, string? error)> RenameTagAsync(int companyId, int tagId, string newName, int? actorEmployeeId, CancellationToken ct = default);

        /// Archives a tag that is in use; deletes one that is not. The outcome says which happened.
        Task<(bool ok, string? error, bool archived)> RetireTagAsync(int companyId, int tagId, int? actorEmployeeId, CancellationToken ct = default);

        Task<(bool ok, string? error)> AssignAsync(int companyId, int tagId, string entityType, int entityId, int? actorEmployeeId, CancellationToken ct = default);
        Task<(bool ok, string? error)> RemoveAsync(int companyId, int tagId, string entityType, int entityId, CancellationToken ct = default);

        /// Replaces an entity's whole tag set — what a Create/Edit screen posts.
        Task<(bool ok, string? error)> SetEntityTagsAsync(int companyId, string entityType, int entityId, IEnumerable<int> tagIds, int? actorEmployeeId, CancellationToken ct = default);

        Task<IReadOnlyList<Tag>> GetEntityTagsAsync(int companyId, string entityType, int entityId, CancellationToken ct = default);

        /// The tags of MANY entities in one query. The list screens use this instead of asking per
        /// row, which is the N+1 this wave was told to avoid.
        Task<IReadOnlyDictionary<int, List<Tag>>> GetTagsForEntitiesAsync(int companyId, string entityType, IReadOnlyCollection<int> entityIds, CancellationToken ct = default);
    }

    public class TagService : ITagService
    {
        public const int MaxNameLength = 60;

        /// The refusal for an entity type the platform does not tag. Named because it is the ONLY way
        /// a caller - or a test - can tell WHICH of the two independent guards refused: this one, or the
        /// resolver switch below whose default arm returns false. Mutation found that difference: with
        /// the type gate deleted, an unregistered type was still refused, by the resolver, with a
        /// different message. Two guards is the right design; being unable to tell them apart is not.
        public const string UnsupportedEntityTypeError = "نوع الكيان غير مدعوم للوسوم";

        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly CrossDbContext _db;
        public TagService(CrossDbContext db) { _db = db; }

        // -----------------------------------------------------------------------------------------
        // NORMALIZATION — the one definition, used by every write and every duplicate check.
        //
        // Trim, collapse inner whitespace, uppercase invariantly. So "Urgent", " urgent " and
        // "URGENT" are one tag inside one company, which is what the requirement asks for.
        //
        // ToUpperInvariant, NOT ToUpper: a culture-sensitive fold would make collision depend on the
        // server's locale, and the Turkish dotted-i is the classic way that becomes a bug nobody can
        // reproduce. Arabic is unaffected by case folding, so an Arabic tag normalizes to its own
        // trimmed, whitespace-collapsed form — which is still exactly what is needed to catch
        // "عاجل" typed with a stray trailing space.
        // -----------------------------------------------------------------------------------------
        public static string Normalize(string? name)
            => name == null ? "" : Whitespace.Replace(name.Trim(), " ").ToUpperInvariant();

        public async Task<IReadOnlyList<Tag>> GetTagsAsync(int companyId, bool includeArchived = false, CancellationToken ct = default)
        {
            var q = _db.Tags.AsNoTracking().Where(t => t.CompanyID == companyId);
            if (!includeArchived) q = q.Where(t => t.IsActive);
            return await q.OrderBy(t => t.Name).ToListAsync(ct);
        }

        public async Task<(bool ok, string? error, Tag? tag)> CreateTagAsync(
            int companyId, string name, int? actorEmployeeId, CancellationToken ct = default)
        {
            var (valid, error, normalized, trimmed) = Validate(name);
            if (!valid) return (false, error, null);

            // A duplicate name REVIVES an archived tag rather than failing or creating a twin. The
            // alternative is a dead end: the user is told "already exists" about something they
            // cannot see, and the unique index makes a twin impossible anyway.
            var existing = await _db.Tags
                .FirstOrDefaultAsync(t => t.CompanyID == companyId && t.NormalizedName == normalized, ct);
            if (existing != null)
            {
                if (existing.IsActive) return (false, "الوسم موجود بالفعل", null);
                existing.IsActive = true;
                existing.Name = trimmed;
                existing.updatedBy = actorEmployeeId;
                existing.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                return (true, null, existing);
            }

            var tag = new Tag
            {
                CompanyID = companyId,
                Name = trimmed,
                NormalizedName = normalized,
                IsActive = true,
                CreatedBy = actorEmployeeId,
                CreatedAt = DateTime.UtcNow,
            };
            _db.Tags.Add(tag);
            await _db.SaveChangesAsync(ct);
            return (true, null, tag);
        }

        public async Task<(bool ok, string? error)> RenameTagAsync(
            int companyId, int tagId, string newName, int? actorEmployeeId, CancellationToken ct = default)
        {
            var (valid, error, normalized, trimmed) = Validate(newName);
            if (!valid) return (false, error);

            var tag = await _db.Tags.FirstOrDefaultAsync(t => t.ID == tagId && t.CompanyID == companyId, ct);
            if (tag == null) return (false, "الوسم غير موجود");

            // A rename that only changes case or spacing keeps the same normalized key and is allowed
            // — the user is fixing the display form, not colliding with anything.
            if (!string.Equals(tag.NormalizedName, normalized, StringComparison.Ordinal))
            {
                bool clash = await _db.Tags.AsNoTracking()
                    .AnyAsync(t => t.CompanyID == companyId && t.ID != tagId && t.NormalizedName == normalized, ct);
                if (clash) return (false, "يوجد وسم آخر بنفس الاسم");
            }

            tag.Name = trimmed;
            tag.NormalizedName = normalized;
            tag.updatedBy = actorEmployeeId;
            tag.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return (true, null);
        }

        public async Task<(bool ok, string? error, bool archived)> RetireTagAsync(
            int companyId, int tagId, int? actorEmployeeId, CancellationToken ct = default)
        {
            var tag = await _db.Tags.FirstOrDefaultAsync(t => t.ID == tagId && t.CompanyID == companyId, ct);
            if (tag == null) return (false, "الوسم غير موجود", false);

            // USED means history. Archiving keeps every assignment answerable; deleting would either
            // orphan those rows or rewrite what somebody recorded.
            bool inUse = await _db.TagAssignments.AsNoTracking()
                .AnyAsync(a => a.CompanyID == companyId && a.TagId == tagId, ct);

            if (inUse)
            {
                if (!tag.IsActive) return (true, null, true);   // already archived; idempotent
                tag.IsActive = false;
                tag.updatedBy = actorEmployeeId;
                tag.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                return (true, null, true);
            }

            _db.Tags.Remove(tag);
            await _db.SaveChangesAsync(ct);
            return (true, null, false);
        }

        public async Task<(bool ok, string? error)> AssignAsync(
            int companyId, int tagId, string entityType, int entityId, int? actorEmployeeId, CancellationToken ct = default)
        {
            var (ok, error) = await ValidateAssignmentAsync(companyId, tagId, entityType, entityId, ct);
            if (!ok) return (false, error);

            bool already = await _db.TagAssignments.AsNoTracking().AnyAsync(
                a => a.CompanyID == companyId && a.TagId == tagId
                  && a.EntityType == entityType && a.EntityId == entityId, ct);
            if (already) return (true, null);   // IDEMPOTENT: the desired state already holds

            _db.TagAssignments.Add(new TagAssignment
            {
                CompanyID = companyId,
                TagId = tagId,
                EntityType = entityType,
                EntityId = entityId,
                CreatedBy = actorEmployeeId,
                CreatedAt = DateTime.UtcNow,
            });

            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // Lost the race on UX_TagAssignments_Unique. The desired state holds either way, so
                // this is a success and not an error the user should ever see.
                return (true, null);
            }
            return (true, null);
        }

        public async Task<(bool ok, string? error)> RemoveAsync(
            int companyId, int tagId, string entityType, int entityId, CancellationToken ct = default)
        {
            var row = await _db.TagAssignments.FirstOrDefaultAsync(
                a => a.CompanyID == companyId && a.TagId == tagId
                  && a.EntityType == entityType && a.EntityId == entityId, ct);
            if (row == null) return (true, null);   // idempotent

            _db.TagAssignments.Remove(row);
            await _db.SaveChangesAsync(ct);
            return (true, null);
        }

        public async Task<(bool ok, string? error)> SetEntityTagsAsync(
            int companyId, string entityType, int entityId, IEnumerable<int> tagIds,
            int? actorEmployeeId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(entityType)) return (false, "نوع الكيان مطلوب");
            if (entityId <= 0) return (false, "الكيان غير صالح");
            if (!SupportedEntityTypes.Contains(entityType))
                return (false, UnsupportedEntityTypeError);
            // The same three-way check the single-assign path makes. A replace-the-whole-set call is
            // not a lesser operation and does not get a lesser guard.
            if (!await EntityBelongsToCompanyAsync(companyId, entityType, entityId, ct))
                return (false, "الكيان غير موجود");

            var wanted = (tagIds ?? Array.Empty<int>()).Where(id => id > 0).Distinct().ToList();

            // EVERY requested tag is validated against this company before ANYTHING is written. A
            // partially-applied set would leave the entity tagged with whichever ids happened to be
            // legitimate, which is worse than refusing.
            if (wanted.Count > 0)
            {
                int mine = await _db.Tags.AsNoTracking()
                    .CountAsync(t => t.CompanyID == companyId && wanted.Contains(t.ID), ct);
                if (mine != wanted.Count) return (false, "أحد الوسوم غير موجود في هذه الشركة");
            }

            var current = await _db.TagAssignments
                .Where(a => a.CompanyID == companyId && a.EntityType == entityType && a.EntityId == entityId)
                .ToListAsync(ct);

            foreach (var gone in current.Where(a => !wanted.Contains(a.TagId)))
                _db.TagAssignments.Remove(gone);

            var have = current.Select(a => a.TagId).ToHashSet();
            foreach (var add in wanted.Where(id => !have.Contains(id)))
                _db.TagAssignments.Add(new TagAssignment
                {
                    CompanyID = companyId,
                    TagId = add,
                    EntityType = entityType,
                    EntityId = entityId,
                    CreatedBy = actorEmployeeId,
                    CreatedAt = DateTime.UtcNow,
                });

            await _db.SaveChangesAsync(ct);
            return (true, null);
        }

        public async Task<IReadOnlyList<Tag>> GetEntityTagsAsync(
            int companyId, string entityType, int entityId, CancellationToken ct = default)
            => await (from a in _db.TagAssignments.AsNoTracking()
                      join t in _db.Tags.AsNoTracking() on a.TagId equals t.ID
                      where a.CompanyID == companyId && a.EntityType == entityType && a.EntityId == entityId
                      orderby t.Name
                      select t).ToListAsync(ct);

        public async Task<IReadOnlyDictionary<int, List<Tag>>> GetTagsForEntitiesAsync(
            int companyId, string entityType, IReadOnlyCollection<int> entityIds, CancellationToken ct = default)
        {
            if (entityIds == null || entityIds.Count == 0)
                return new Dictionary<int, List<Tag>>();

            // ONE query for the whole page. Asking per row is the N+1 a task list makes expensive.
            var rows = await (from a in _db.TagAssignments.AsNoTracking()
                              join t in _db.Tags.AsNoTracking() on a.TagId equals t.ID
                              where a.CompanyID == companyId && a.EntityType == entityType
                                    && entityIds.Contains(a.EntityId)
                              orderby t.Name
                              select new { a.EntityId, Tag = t }).ToListAsync(ct);

            return rows.GroupBy(r => r.EntityId)
                       .ToDictionary(g => g.Key, g => g.Select(r => r.Tag).ToList());
        }

        // -----------------------------------------------------------------------------------------
        private static (bool ok, string? error, string normalized, string trimmed) Validate(string? name)
        {
            var trimmed = Whitespace.Replace((name ?? "").Trim(), " ");
            if (trimmed.Length == 0) return (false, "اسم الوسم مطلوب", "", "");
            if (trimmed.Length > MaxNameLength)
                return (false, $"اسم الوسم أطول من {MaxNameLength} حرفًا", "", "");
            return (true, null, Normalize(trimmed), trimmed);
        }

        /// THE THREE-WAY COMPANY CHECK. The tag's company, the entity's company and the caller's
        /// company must all agree, and a foreign id gets the same refusal as a missing one so an id
        /// here cannot become a tenant probe.
        private async Task<(bool ok, string? error)> ValidateAssignmentAsync(
            int companyId, int tagId, string entityType, int entityId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(entityType)) return (false, "نوع الكيان مطلوب");
            if (entityId <= 0) return (false, "الكيان غير صالح");

            if (!SupportedEntityTypes.Contains(entityType))
                return (false, UnsupportedEntityTypeError);

            var tag = await _db.Tags.AsNoTracking()
                .FirstOrDefaultAsync(t => t.ID == tagId && t.CompanyID == companyId, ct);
            if (tag == null) return (false, "الوسم غير موجود");
            if (!tag.IsActive) return (false, "الوسم مؤرشف");

            // THE ENTITY'S OWN COMPANY, and this is the check the platform's other guards cannot make
            // for us. The global filter and CompanyWriteGuardInterceptor both look at the ASSIGNMENT
            // row, which would claim company 41 quite happily while pointing at company 77's task.
            // Only reading the target tells us whether the three companies agree.
            if (!await EntityBelongsToCompanyAsync(companyId, entityType, entityId, ct))
                return (false, "الكيان غير موجود");

            return (true, null);
        }

        /// The entity kinds tags may be attached to TODAY. An unknown type is REFUSED rather than
        /// accepted as free text - otherwise a request could invent an entity kind whose company
        /// nothing verifies.
        ///
        /// CRM WAVE 1 IS THE REUSE THIS DESIGN WAS FOR, and it cost exactly what was predicted: three
        /// codes here, three company resolvers below, and no schema change at all. No CrmTag table, no
        /// LeadTag table, no second vocabulary - company 41 curates ONE list of names and a task and a
        /// lead draw from it.
        ///
        /// RECOGNITION IS NOT AN ASSIGNMENT. Adding a code here makes the platform able to accept a CRM
        /// tag; it writes none, exposes no CRM tag control, and changes no CRM screen. The CRM increment
        /// that calls SetEntityTagsAsync is a separate piece of work with a separate grant.
        private static readonly HashSet<string> SupportedEntityTypes =
            new(StringComparer.Ordinal)
            {
                EntityRegistry.Task,
                EntityRegistry.Lead,
                EntityRegistry.Opportunity,
                EntityRegistry.CrmAccount,
            };

        private Task<bool> EntityBelongsToCompanyAsync(
            int companyId, string entityType, int entityId, CancellationToken ct)
            => entityType switch
            {
                EntityRegistry.Task =>
                    _db.TaskItems.AsNoTracking().AnyAsync(t => t.ID == entityId && t.CompanyId == companyId, ct),

                // CRM Wave 1. Each reads the TARGET row and compares ITS company - which is the check
                // neither the global query filter nor the write guard can make for us, because both look
                // at the assignment row and it would happily claim company 41 while pointing at company
                // 77. Only reading the target tells us whether the three companies agree.
                EntityRegistry.Lead =>
                    _db.Leads.AsNoTracking().AnyAsync(l => l.ID == entityId && l.CompanyID == companyId, ct),
                EntityRegistry.Opportunity =>
                    _db.Opportunities.AsNoTracking().AnyAsync(o => o.ID == entityId && o.CompanyID == companyId, ct),
                EntityRegistry.CrmAccount =>
                    _db.CrmAccounts.AsNoTracking().AnyAsync(a => a.ID == entityId && a.CompanyID == companyId, ct),
                // Unreachable while SupportedEntityTypes gates the caller, and deliberately false
                // rather than true: a new entity type must be added in BOTH places to work at all.
                _ => Task.FromResult(false),
            };
    }
}
