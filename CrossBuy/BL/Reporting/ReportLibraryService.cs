using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — THE REPORT LIBRARY: categories, tags, favourites, ownership, sharing.
    //
    // Components 24–28 live in ONE service because they are all the same kind of thing — metadata ABOUT a report
    // rather than part of producing one — and because they share a single rule that must not be restated four
    // times: every row is company-scoped, and every write re-checks access against IReportAuthorizationService.
    //
    // Nothing here can affect what a report READS. A tag, a favourite, a category and even an ownership transfer
    // change navigation and administration only. Sharing is the one exception and it is deliberately one-way:
    // a share may RAISE a caller's access level, never grant access the module permission denied
    // (see ReportAuthorizationService — the ordering rule).
    // ============================================================================================

    public sealed class ReportCategoryNode
    {
        public required int Id { get; init; }
        public required string Key { get; init; }
        public required string Name { get; init; }
        public string? NameEn { get; init; }
        public int? ParentId { get; init; }
        public string? Icon { get; init; }
        public bool IsSystem { get; init; }
        public int SortOrder { get; init; }
        public IReadOnlyList<ReportCategoryNode> Children { get; init; } = Array.Empty<ReportCategoryNode>();

        // Reports in this category that the caller may see.
        public int ReportCount { get; init; }

        public string Label(bool arabic) => arabic ? Name : (NameEn ?? Name);
    }

    public sealed class ReportTagInfo
    {
        public required int Id { get; init; }
        public required string Name { get; init; }
        public string? NameEn { get; init; }
        public string ColorToken { get; init; } = "primary";
        public int UsageCount { get; init; }
    }

    public sealed class ReportFavoriteInfo
    {
        public required int Id { get; init; }
        public required string ReportCode { get; init; }
        public int? TemplateId { get; init; }
        public int SortOrder { get; init; }

        // Resolved from the catalog so a sidebar does not need a second lookup. null when the report code no
        // longer exists in the catalog — a favourite that outlived its report is shown as stale, not hidden,
        // so the user can remove it.
        public ReportDefinition? Definition { get; init; }
        public bool IsStale => Definition == null;
    }

    public sealed class ReportShareInput
    {
        public required string ReportCode { get; init; }
        public int? TemplateId { get; init; }
        public ReportPrincipalType PrincipalType { get; init; }
        public string PrincipalKey { get; init; } = "";
        public ReportAccessLevel AccessLevel { get; init; } = ReportAccessLevel.Run;
        public DateTime? ExpiresAt { get; init; }
    }

    public sealed class ReportShareInfo
    {
        public required int Id { get; init; }
        public required string ReportCode { get; init; }
        public int? TemplateId { get; init; }
        public ReportPrincipalType PrincipalType { get; init; }
        public required string PrincipalKey { get; init; }
        public ReportAccessLevel AccessLevel { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public int? GrantedByEmpId { get; init; }
        public bool IsExpired { get; init; }
    }

    public interface IReportLibraryService
    {
        // ---- categories -------------------------------------------------------------------------------
        Task<IReadOnlyList<ReportCategoryNode>> GetCategoryTreeAsync(BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<int> SaveCategoryAsync(int id, string key, string name, string? nameEn, int? parentId, string? icon,
            int sortOrder, BusinessContext context, CancellationToken cancellationToken = default);

        Task<bool> DeleteCategoryAsync(int id, BusinessContext context, CancellationToken cancellationToken = default);

        // Materialises the catalog's CategoryKeys as platform ReportCategories rows. Idempotent, so it is safe to
        // call at any time; it is how a fresh database gets its category tree without a hand-written seed list.
        Task<int> SyncPlatformCategoriesAsync(CancellationToken cancellationToken = default);

        // ---- tags -------------------------------------------------------------------------------------
        Task<IReadOnlyList<ReportTagInfo>> GetTagsAsync(BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<int> EnsureTagAsync(string name, string? nameEn, string colorToken, BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<bool> TagAsync(string reportCode, int? templateId, int tagId, BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<bool> UntagAsync(string reportCode, int? templateId, int tagId, BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<string>> GetReportCodesByTagAsync(int tagId, BusinessContext context,
            CancellationToken cancellationToken = default);

        // ---- favourites -------------------------------------------------------------------------------
        Task<IReadOnlyList<ReportFavoriteInfo>> GetFavoritesAsync(BusinessContext context,
            CancellationToken cancellationToken = default);

        // Idempotent: favouriting twice is one row. Returns the row id.
        Task<int> AddFavoriteAsync(string reportCode, int? templateId, BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<bool> RemoveFavoriteAsync(string reportCode, int? templateId, BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<bool> ReorderFavoritesAsync(IReadOnlyList<int> orderedIds, BusinessContext context,
            CancellationToken cancellationToken = default);

        // ---- ownership --------------------------------------------------------------------------------
        Task<bool> TransferOwnershipAsync(int templateId, int toEmployeeId, BusinessContext context,
            CancellationToken cancellationToken = default);

        // ---- sharing ----------------------------------------------------------------------------------
        Task<IReadOnlyList<ReportShareInfo>> GetSharesAsync(string reportCode, int? templateId,
            BusinessContext context, CancellationToken cancellationToken = default);

        Task<int> ShareAsync(ReportShareInput input, BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<bool> RevokeShareAsync(int shareId, BusinessContext context,
            CancellationToken cancellationToken = default);
    }

    public class ReportLibraryService : IReportLibraryService
    {
        public const string CodeDenied = "library_denied";
        public const string CodeNotFound = "library_not_found";

        private readonly CrossDbContext _db;
        private readonly IReportCatalog _catalog;
        private readonly IReportAuthorizationService _authorization;
        private readonly IReportClock _clock;

        public ReportLibraryService(CrossDbContext db, IReportCatalog catalog,
            IReportAuthorizationService authorization, IReportClock clock)
        {
            _db = db;
            _catalog = catalog;
            _authorization = authorization;
            _clock = clock;
        }

        // ================================================================================================
        // CATEGORIES
        // ================================================================================================
        public async Task<IReadOnlyList<ReportCategoryNode>> GetCategoryTreeAsync(BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return Array.Empty<ReportCategoryNode>();

            // Platform rows (CompanyID 0) plus the company's own — the same "own company OR platform" rule the
            // template service uses.
            var rows = await _db.ReportCategories.AsNoTracking()
                .Where(c => (c.CompanyID == context.CompanyId || c.CompanyID == 0) && c.DeletedAt == null)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                .ToListAsync(cancellationToken);

            // Counts come from the CATALOG filtered by what the caller may see, not from a stored counter — a
            // stored count would drift the moment a report's permission changed.
            var visible = await _authorization.FilterVisibleAsync(_catalog.GetDefinitions(), context,
                cancellationToken);
            var countsByKey = visible
                .Where(d => !string.IsNullOrWhiteSpace(d.CategoryKey))
                .GroupBy(d => d.CategoryKey!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            ReportCategoryNode Build(ReportCategory row) => new()
            {
                Id = row.Id,
                Key = row.Key,
                Name = row.Name,
                NameEn = row.NameEn,
                ParentId = row.ParentId,
                Icon = row.Icon,
                IsSystem = row.IsSystem,
                SortOrder = row.SortOrder,
                ReportCount = countsByKey.GetValueOrDefault(row.Key),
                Children = rows.Where(c => c.ParentId == row.Id).Select(Build).ToList(),
            };

            return rows.Where(c => c.ParentId == null).Select(Build).ToList();
        }

        public async Task<int> SaveCategoryAsync(int id, string key, string name, string? nameEn, int? parentId,
            string? icon, int sortOrder, BusinessContext context, CancellationToken cancellationToken = default)
        {
            RequireCompany(context);
            await RequireAdministratorAsync(context, cancellationToken);

            var now = _clock.LocalNow;
            ReportCategory row;

            if (id > 0)
            {
                row = await _db.ReportCategories
                          .FirstOrDefaultAsync(c => c.Id == id && c.CompanyID == context.CompanyId
                                                    && c.DeletedAt == null, cancellationToken)
                      ?? throw new ReportingException($"Report category {id} was not found in this company.");

                // A system category came from the catalog and a definition points at its Key. Renaming the label
                // is fine; changing the key would orphan every definition that references it.
                if (row.IsSystem && !string.Equals(row.Key, key, StringComparison.Ordinal))
                    throw new ReportingException(
                        $"Category '{row.Key}' is a platform category; its key may not be changed.");

                row.updatedBy = context.EmployeeId;
                row.UpdatedAt = now;
            }
            else
            {
                row = new ReportCategory
                {
                    CompanyID = context.CompanyId,
                    CreatedBy = context.EmployeeId,
                    CreatedAt = now,
                };
                _db.ReportCategories.Add(row);
            }

            row.Key = string.IsNullOrWhiteSpace(key) ? Slug(name) : key.Trim();
            row.Name = name.Trim();
            row.NameEn = nameEn?.Trim();
            row.ParentId = parentId;
            row.Icon = icon;
            row.SortOrder = sortOrder;

            await _db.SaveChangesAsync(cancellationToken);
            return row.Id;
        }

        public async Task<bool> DeleteCategoryAsync(int id, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            RequireCompany(context);
            await RequireAdministratorAsync(context, cancellationToken);

            var row = await _db.ReportCategories
                .FirstOrDefaultAsync(c => c.Id == id && c.CompanyID == context.CompanyId && c.DeletedAt == null,
                    cancellationToken);
            if (row == null) return false;

            // A platform category is referenced by a code-first definition, which a tenant cannot edit. Deleting
            // it would leave that definition pointing at nothing.
            if (row.IsSystem) return false;

            row.DeletedAt = _clock.LocalNow;
            row.updatedBy = context.EmployeeId;
            row.UpdatedAt = _clock.LocalNow;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        public async Task<int> SyncPlatformCategoriesAsync(CancellationToken cancellationToken = default)
        {
            // Deliberately takes NO BusinessContext: it writes platform rows (CompanyID 0), which belong to no
            // tenant. It is idempotent and additive — it never renames or deletes — so it is safe to run on every
            // start-up or from a maintenance action.
            var existing = await _db.ReportCategories
                .Where(c => c.CompanyID == 0)
                .Select(c => c.Key)
                .ToListAsync(cancellationToken);

            var keys = _catalog.GetCategoryKeys()
                .Where(k => !existing.Contains(k, StringComparer.Ordinal))
                .ToList();

            if (keys.Count == 0) return 0;

            var now = _clock.LocalNow;
            var order = 0;
            foreach (var key in keys)
            {
                // The label is derived from the key, then a deployment or an administrator renames it. Deriving it
                // avoids a second hand-maintained list that could disagree with the catalog.
                _db.ReportCategories.Add(new ReportCategory
                {
                    CompanyID = 0,
                    Key = key,
                    Name = Humanise(key),
                    NameEn = Humanise(key),
                    IsSystem = true,
                    SortOrder = order += 10,
                    CreatedAt = now,
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
            return keys.Count;
        }

        // ================================================================================================
        // TAGS
        // ================================================================================================
        public async Task<IReadOnlyList<ReportTagInfo>> GetTagsAsync(BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return Array.Empty<ReportTagInfo>();

            var tags = await _db.ReportTags.AsNoTracking()
                .Where(t => t.CompanyID == context.CompanyId && t.DeletedAt == null)
                .OrderBy(t => t.Name)
                .ToListAsync(cancellationToken);

            var counts = await _db.ReportTagLinks.AsNoTracking()
                .Where(l => l.CompanyID == context.CompanyId)
                .GroupBy(l => l.TagId)
                .Select(g => new { TagId = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);

            return tags.Select(t => new ReportTagInfo
            {
                Id = t.Id,
                Name = t.Name,
                NameEn = t.NameEn,
                ColorToken = t.ColorToken,
                UsageCount = counts.FirstOrDefault(c => c.TagId == t.Id)?.Count ?? 0,
            }).ToList();
        }

        public async Task<int> EnsureTagAsync(string name, string? nameEn, string colorToken,
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            RequireCompany(context);
            var normalised = (name ?? "").Trim();
            if (normalised.Length == 0) throw new ReportingException("A tag name is required.");

            // Case-insensitive match so "Month-End" and "month-end" are one tag. Without this a tag list becomes
            // a list of near-duplicates within a week.
            var existing = await _db.ReportTags
                .FirstOrDefaultAsync(t => t.CompanyID == context.CompanyId && t.DeletedAt == null
                                          && t.Name.ToLower() == normalised.ToLower(), cancellationToken);
            if (existing != null) return existing.Id;

            var tag = new ReportTag
            {
                CompanyID = context.CompanyId,
                Name = normalised,
                NameEn = nameEn?.Trim(),
                ColorToken = string.IsNullOrWhiteSpace(colorToken) ? "primary" : colorToken,
                CreatedBy = context.EmployeeId,
                CreatedAt = _clock.LocalNow,
            };
            _db.ReportTags.Add(tag);
            await _db.SaveChangesAsync(cancellationToken);
            return tag.Id;
        }

        public async Task<bool> TagAsync(string reportCode, int? templateId, int tagId, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            RequireCompany(context);

            // Tagging is a VIEW-level act — it changes navigation, not data. Requiring Edit would stop a user
            // organising the reports they are allowed to read.
            var definition = _catalog.GetDefinition(reportCode);
            var decision = await _authorization.AuthorizeReportAsync(definition, ReportAccessLevel.View, context,
                cancellationToken);
            if (!decision.Allowed) return false;

            var tagExists = await _db.ReportTags.AsNoTracking()
                .AnyAsync(t => t.Id == tagId && t.CompanyID == context.CompanyId && t.DeletedAt == null,
                    cancellationToken);
            if (!tagExists) return false;

            var already = await _db.ReportTagLinks.AsNoTracking()
                .AnyAsync(l => l.CompanyID == context.CompanyId && l.TagId == tagId
                               && l.ReportCode == reportCode && l.TemplateId == templateId, cancellationToken);
            if (already) return true;

            _db.ReportTagLinks.Add(new ReportTagLink
            {
                CompanyID = context.CompanyId,
                TagId = tagId,
                ReportCode = reportCode,
                TemplateId = templateId,
                CreatedBy = context.EmployeeId,
                CreatedAt = _clock.LocalNow,
            });
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        public async Task<bool> UntagAsync(string reportCode, int? templateId, int tagId, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            RequireCompany(context);

            var link = await _db.ReportTagLinks
                .FirstOrDefaultAsync(l => l.CompanyID == context.CompanyId && l.TagId == tagId
                                          && l.ReportCode == reportCode && l.TemplateId == templateId,
                    cancellationToken);
            if (link == null) return false;

            // A tag link carries no history worth keeping, so it is hard-deleted (unlike a template or an archive
            // entry, both of which are referenced by other rows).
            _db.ReportTagLinks.Remove(link);
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        public async Task<IReadOnlyList<string>> GetReportCodesByTagAsync(int tagId, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return Array.Empty<string>();

            return await _db.ReportTagLinks.AsNoTracking()
                .Where(l => l.CompanyID == context.CompanyId && l.TagId == tagId)
                .Select(l => l.ReportCode)
                .Distinct()
                .ToListAsync(cancellationToken);
        }

        // ================================================================================================
        // FAVOURITES
        // ================================================================================================
        public async Task<IReadOnlyList<ReportFavoriteInfo>> GetFavoritesAsync(BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0 || context.EmployeeId is not > 0)
                return Array.Empty<ReportFavoriteInfo>();

            var rows = await _db.ReportFavorites.AsNoTracking()
                .Where(f => f.CompanyID == context.CompanyId && f.EmployeeId == context.EmployeeId!.Value)
                .OrderBy(f => f.SortOrder).ThenBy(f => f.Id)
                .ToListAsync(cancellationToken);

            var result = new List<ReportFavoriteInfo>();
            foreach (var row in rows)
            {
                _catalog.TryGetDefinition(row.ReportCode, out var definition);

                // A favourite whose permission has since been revoked is dropped from the list — a pinned link the
                // user cannot open is worse than no link. A favourite whose REPORT no longer exists is kept and
                // flagged stale, so they can unpin it.
                if (definition != null)
                {
                    var decision = await _authorization.AuthorizeReportAsync(definition, ReportAccessLevel.View,
                        context, cancellationToken);
                    if (!decision.Allowed) continue;
                }

                result.Add(new ReportFavoriteInfo
                {
                    Id = row.Id,
                    ReportCode = row.ReportCode,
                    TemplateId = row.TemplateId,
                    SortOrder = row.SortOrder,
                    Definition = definition,
                });
            }
            return result;
        }

        public async Task<int> AddFavoriteAsync(string reportCode, int? templateId, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            RequireCompany(context);
            if (context.EmployeeId is not > 0)
                throw new ReportingException("A favourite requires a resolved employee.");

            var definition = _catalog.GetDefinition(reportCode);
            var decision = await _authorization.AuthorizeReportAsync(definition, ReportAccessLevel.View, context,
                cancellationToken);
            if (!decision.Allowed)
                throw new ReportingException($"You may not access report '{reportCode}'.");

            var existing = await _db.ReportFavorites
                .FirstOrDefaultAsync(f => f.CompanyID == context.CompanyId
                                          && f.EmployeeId == context.EmployeeId!.Value
                                          && f.ReportCode == reportCode
                                          && f.TemplateId == templateId, cancellationToken);
            if (existing != null) return existing.Id;

            var maxOrder = await _db.ReportFavorites
                .Where(f => f.CompanyID == context.CompanyId && f.EmployeeId == context.EmployeeId!.Value)
                .Select(f => (int?)f.SortOrder)
                .MaxAsync(cancellationToken) ?? 0;

            var favorite = new ReportFavorite
            {
                CompanyID = context.CompanyId,
                EmployeeId = context.EmployeeId!.Value,
                ReportCode = reportCode,
                TemplateId = templateId,
                SortOrder = maxOrder + 10,
                CreatedBy = context.EmployeeId,
                CreatedAt = _clock.LocalNow,
            };
            _db.ReportFavorites.Add(favorite);
            await _db.SaveChangesAsync(cancellationToken);
            return favorite.Id;
        }

        public async Task<bool> RemoveFavoriteAsync(string reportCode, int? templateId, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0 || context.EmployeeId is not > 0) return false;

            var row = await _db.ReportFavorites
                .FirstOrDefaultAsync(f => f.CompanyID == context.CompanyId
                                          && f.EmployeeId == context.EmployeeId!.Value
                                          && f.ReportCode == reportCode
                                          && f.TemplateId == templateId, cancellationToken);
            if (row == null) return false;

            _db.ReportFavorites.Remove(row);
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        public async Task<bool> ReorderFavoritesAsync(IReadOnlyList<int> orderedIds, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0 || context.EmployeeId is not > 0) return false;

            // Loaded with the ownership filter in the QUERY, not checked afterwards: an id belonging to another
            // employee simply is not in the set, so a crafted list cannot reorder someone else's sidebar.
            var rows = await _db.ReportFavorites
                .Where(f => f.CompanyID == context.CompanyId && f.EmployeeId == context.EmployeeId!.Value
                            && orderedIds.Contains(f.Id))
                .ToListAsync(cancellationToken);

            for (var i = 0; i < orderedIds.Count; i++)
            {
                var row = rows.FirstOrDefault(r => r.Id == orderedIds[i]);
                if (row != null) row.SortOrder = (i + 1) * 10;
            }

            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        // ================================================================================================
        // OWNERSHIP
        // ================================================================================================
        public async Task<bool> TransferOwnershipAsync(int templateId, int toEmployeeId, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            RequireCompany(context);

            var template = await _db.ReportTemplates
                .FirstOrDefaultAsync(t => t.Id == templateId && t.CompanyID == context.CompanyId
                                          && t.DeletedAt == null, cancellationToken);
            if (template == null) return false;

            if (!_catalog.TryGetDefinition(template.ReportCode, out var definition) || definition == null)
                return false;

            // Manage, not Edit: handing a template to someone else is an administrative act, and Edit is only
            // "may change the layout".
            var decision = await _authorization.AuthorizeTemplateAsync(definition, template,
                ReportAccessLevel.Manage, context, cancellationToken);
            if (!decision.Allowed) return false;

            // The new owner must be a real active employee OF THIS COMPANY. Without this check a transfer could
            // park a template on an employee id from another tenant, making it unreachable and unmanageable.
            var validTarget = await _db.Employee.AsNoTracking()
                .AnyAsync(e => e.ID == toEmployeeId && e.EmpCompanyID == context.CompanyId && e.IsActive,
                    cancellationToken);
            if (!validTarget) return false;

            template.OwnerEmpId = toEmployeeId;
            template.updatedBy = context.EmployeeId;
            template.UpdatedAt = _clock.LocalNow;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        // ================================================================================================
        // SHARING
        // ================================================================================================
        public async Task<IReadOnlyList<ReportShareInfo>> GetSharesAsync(string reportCode, int? templateId,
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return Array.Empty<ReportShareInfo>();

            var definition = _catalog.GetDefinition(reportCode);
            var decision = await _authorization.AuthorizeReportAsync(definition, ReportAccessLevel.View, context,
                cancellationToken);
            if (!decision.Allowed) return Array.Empty<ReportShareInfo>();

            var now = _clock.LocalNow;
            var rows = await _db.ReportShares.AsNoTracking()
                .Where(s => s.CompanyID == context.CompanyId && s.DeletedAt == null
                            && s.ReportCode == reportCode
                            && (templateId == null || s.TemplateId == null || s.TemplateId == templateId))
                .ToListAsync(cancellationToken);

            return rows.Select(s => new ReportShareInfo
            {
                Id = s.Id,
                ReportCode = s.ReportCode,
                TemplateId = s.TemplateId,
                PrincipalType = s.PrincipalType,
                PrincipalKey = s.PrincipalKey,
                AccessLevel = s.AccessLevel,
                ExpiresAt = s.ExpiresAt,
                GrantedByEmpId = s.GrantedByEmpId,
                IsExpired = s.ExpiresAt != null && s.ExpiresAt <= now,
            }).ToList();
        }

        public async Task<int> ShareAsync(ReportShareInput input, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            RequireCompany(context);

            var definition = _catalog.GetDefinition(input.ReportCode);

            if (!definition.Capabilities.AllowShare)
                throw new ReportingException($"Report '{definition.Code}' may not be shared.");

            // Only Manage may share. Manage is also the highest level a share can grant, which closes the
            // privilege-escalation loop: someone with Run cannot mint themselves Edit, and someone with Manage
            // cannot mint more than they hold.
            var decision = await _authorization.AuthorizeReportAsync(definition, ReportAccessLevel.Manage, context,
                cancellationToken);
            if (!decision.Allowed)
                throw new ReportingException(decision.Reason ?? "You may not share that report.");

            if (input.AccessLevel > decision.EffectiveLevel)
                throw new ReportingException(
                    $"You hold {decision.EffectiveLevel} on '{definition.Code}' and cannot grant " +
                    $"{input.AccessLevel}.");

            if (input.PrincipalType != ReportPrincipalType.Company && string.IsNullOrWhiteSpace(input.PrincipalKey))
                throw new ReportingException("A share needs a principal.");

            var now = _clock.LocalNow;
            if (input.ExpiresAt is { } expiry && expiry <= now)
                throw new ReportingException("A share cannot expire in the past.");

            var key = input.PrincipalType == ReportPrincipalType.Company ? "" : input.PrincipalKey.Trim();

            // Re-granting updates the existing row instead of stacking a second grant. Two live grants to one
            // principal would make "what does this person have?" a question with two answers.
            var existing = await _db.ReportShares
                .FirstOrDefaultAsync(s => s.CompanyID == context.CompanyId && s.DeletedAt == null
                                          && s.ReportCode == input.ReportCode
                                          && s.TemplateId == input.TemplateId
                                          && s.PrincipalType == input.PrincipalType
                                          && s.PrincipalKey == key, cancellationToken);

            if (existing != null)
            {
                existing.AccessLevel = input.AccessLevel;
                existing.ExpiresAt = input.ExpiresAt;
                existing.GrantedByEmpId = context.EmployeeId;
                existing.updatedBy = context.EmployeeId;
                existing.UpdatedAt = now;
                await _db.SaveChangesAsync(cancellationToken);
                return existing.Id;
            }

            var share = new ReportShare
            {
                CompanyID = context.CompanyId,
                ReportCode = input.ReportCode,
                TemplateId = input.TemplateId,
                PrincipalType = input.PrincipalType,
                PrincipalKey = key,
                AccessLevel = input.AccessLevel,
                ExpiresAt = input.ExpiresAt,
                GrantedByEmpId = context.EmployeeId,
                CreatedBy = context.EmployeeId,
                CreatedAt = now,
            };
            _db.ReportShares.Add(share);
            await _db.SaveChangesAsync(cancellationToken);
            return share.Id;
        }

        public async Task<bool> RevokeShareAsync(int shareId, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            RequireCompany(context);

            var share = await _db.ReportShares
                .FirstOrDefaultAsync(s => s.Id == shareId && s.CompanyID == context.CompanyId
                                          && s.DeletedAt == null, cancellationToken);
            if (share == null) return false;

            if (!_catalog.TryGetDefinition(share.ReportCode, out var definition) || definition == null)
                return false;

            var decision = await _authorization.AuthorizeReportAsync(definition, ReportAccessLevel.Manage, context,
                cancellationToken);
            if (!decision.Allowed) return false;

            // Soft-deleted: who revoked whose access, and when, is exactly the kind of thing an audit asks about.
            share.DeletedAt = _clock.LocalNow;
            share.updatedBy = context.EmployeeId;
            share.UpdatedAt = _clock.LocalNow;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        // ================================================================================================
        private static void RequireCompany(BusinessContext context)
        {
            if (context.CompanyId <= 0)
                throw new ReportingException(
                    "No company is resolved for this request; the report library is company-scoped.");
        }

        private async Task RequireAdministratorAsync(BusinessContext context, CancellationToken cancellationToken)
        {
            if (!await _authorization.IsAdministratorAsync(context, cancellationToken))
                throw new ReportingException("Reporting administration is required for this operation.");
        }

        private static string Slug(string value) => new string(value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');

        private static string Humanise(string key)
        {
            var last = key.Split('.').Last().Replace('-', ' ').Replace('_', ' ').Trim();
            return last.Length == 0 ? key : char.ToUpperInvariant(last[0]) + last[1..];
        }
    }
}