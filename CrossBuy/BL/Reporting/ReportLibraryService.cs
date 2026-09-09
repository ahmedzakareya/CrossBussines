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

        // Gives every registered report a Platform-scope template built from its own definition, so a report is
        // always rendered THROUGH a template and never from defaults buried in code. Idempotent and additive —
        // see the implementation for why the rule exists and what it makes editable.
        Task<int> SyncPlatformTemplatesAsync(CancellationToken cancellationToken = default);

        // Writes the resolved document face into any LIVE template whose stored layout does not name one, so
        // no report falls back to a font constant in code. Never overwrites a stated face; idempotent.
        Task<int> BackfillTemplateTypographyAsync(CancellationToken cancellationToken = default);

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
        // EVERY REPORT SHIPS WITH A TEMPLATE.
        //
        // THE RULE, by owner's decision: a report renders THROUGH A TEMPLATE, and rendering outside one is
        // not allowed. Before this, a report with no saved layout fell back to
        // ReportTemplateSource.DefinitionDefault — the definition's own columns and a page setup that was a
        // constant in code. That is what made the typeface, the paper and the margins unreachable: the only
        // way to change them was to edit C#, so the person using the product could not.
        //
        // With a Platform template per report the resolution walk ALWAYS finds one, and everything about the
        // document — its font, its title, its paper, its columns, its bands — is data an author can edit in
        // Report Studio or fork into their own scope. Nothing is hardcoded any more because nothing needs to
        // be: the defaults live in a row.
        //
        // BUILT FROM THE DEFINITION, so it is the report as it already looks — not a blank page somebody has
        // to design before the product works. ReportVisualLayout.StarterFor lays out the title, the date and
        // the table; the columns, sorts and page setup come from the definition itself.
        //
        // PLATFORM SCOPE (CompanyID 0, no owner) for the same reason SyncPlatformCategoriesAsync writes
        // platform rows: it belongs to the product, not to a tenant. A tenant cannot create one —
        // ReportTemplateService.SaveAsync refuses Platform scope by design — and does not need to: editing
        // one forks it into their own scope, which is what "edit a platform template" already means here.
        //
        // IDEMPOTENT AND ADDITIVE, exactly like the category sync. It never rewrites or deletes a template,
        // so a deployment that has already been customised is left alone and this is safe on every start-up.
        public async Task<int> SyncPlatformTemplatesAsync(CancellationToken cancellationToken = default)
        {
            var existing = await _db.ReportTemplates
                .Where(t => t.CompanyID == 0 && t.Scope == ReportTemplateScope.Platform && t.DeletedAt == null)
                .Select(t => t.ReportCode)
                .ToListAsync(cancellationToken);

            var missing = _catalog.GetDefinitions()
                .Where(d => !existing.Contains(d.Code, StringComparer.Ordinal))
                .ToList();

            if (missing.Count == 0) return 0;

            var now = _clock.LocalNow;

            foreach (var definition in missing)
            {
                // THE TEMPLATE CARRIES THE TYPEFACE, which is the whole point of seeding one. A stored null
                // would mean "whatever the code says", leaving the document's font a constant no author can
                // see -- so the row names a real face that Studio's picker shows and can change.
                var page = ReportPageSetup.Default.WithTypography(ReportTypography.DefaultDocumentFace, null);

                var layout = new ReportLayout
                {
                    VisibleColumns = definition.Columns
                        .Where(c => c.VisibleByDefault && !c.Internal)
                        .Select(c => c.Key).ToList(),
                    Sorts = definition.DefaultSorts,
                    ShowGrandTotals = true,

                    // The page setup and the visual design come from ONE object, so the table renderer and the
                    // visual renderer cannot disagree about the paper — the drift that left a designed A5
                    // exporting as A4.
                    // THE SAME page object into both, because they are read by different renderers:
                    // HtmlReportRenderer takes ReportLayout.PageSetup and ReportVisualRenderer takes
                    // Visual.Page. Leaving StarterFor to build its own default is how a designed page kept
                    // its paper and lost its font -- the visual renderer never saw the setup beside it.
                    PageSetup = page,
                    Visual = ReportVisualLayout.StarterFor(
                        definition.TitleAr ?? definition.TitleEn ?? definition.Code, definition.Columns, page),

                    // The document's heading follows the template's name from here on, which is why renaming
                    // a report in Studio retitles what prints.
                    TitleOverride = definition.TitleAr,
                    TitleOverrideEn = definition.TitleEn,
                };

                var json = ReportLayoutJson.Serialize(layout);

                var template = new ReportTemplate
                {
                    CompanyID = 0,
                    ReportCode = definition.Code,
                    Name = definition.TitleAr ?? definition.Code,
                    NameEn = definition.TitleEn,
                    Scope = ReportTemplateScope.Platform,
                    OwnerEmpId = null,
                    CurrentVersionNo = 1,
                    IsDefault = true,
                    CreatedAt = now,
                };
                _db.ReportTemplates.Add(template);
                await _db.SaveChangesAsync(cancellationToken);   // the version needs the template's id

                _db.ReportTemplateVersions.Add(new ReportTemplateVersion
                {
                    CompanyID = 0,
                    TemplateId = template.Id,
                    VersionNo = 1,
                    LayoutJson = json,
                    ContentHash = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant(),
                    ChangeNote = "Shipped with the report",

                    // PUBLISHED. A version an author can still edit in place would let the shipped default
                    // change without a new version, and an archived artifact records (TemplateId, VersionNo).
                    IsPublished = true,
                    PublishedAt = now,
                    CreatedAt = now,
                });
                await _db.SaveChangesAsync(cancellationToken);
            }

            return missing.Count;
        }


        // ================================================================================================
        // BACKFILL: A LIVE TEMPLATE MUST NAME ITS TYPEFACE.
        //
        // "كله بيعرض من القالب ممنوع من بره القالب" — everything renders from the template, nothing from
        // outside it. Seeding platform templates satisfied that for NEW rows and left every existing one
        // behind, and those are the ones that actually run: a report resolves Personal before Company before
        // Platform, so a user's own saved template wins and the freshly seeded platform default is never
        // consulted. The measurement said so — an org-structure PDF resolved template 94, whose stored
        // PageSetup had no font at all.
        //
        // WHY A MISSING FONT IS NOT A HARMLESS DEFAULT. The serializer omits nulls, so "no font" is not a
        // stored choice, it is silence — and the renderers answer silence with a constant in
        // ReportTypography. That constant is invisible in Studio, cannot be edited, and is not versioned,
        // which is precisely the "I changed the font and the report did not" complaint. Writing the resolved
        // face into the row makes the decision visible, editable and versioned like the rest of the design.
        //
        // BOTH PAGE OBJECTS, because a layout carries two of them: ReportLayout.PageSetup, which
        // HtmlReportRenderer reads, and Visual.Page, which ReportVisualRenderer reads. Filling one and not
        // the other is the drift that once printed a designed A5 onto A4.
        //
        // CURRENT VERSIONS ONLY. Older versions are history and a rollback should give back what it was.
        // NEVER OVERWRITES a stated face, so an author's own choice is safe; a template that already names
        // one is skipped, which is also what makes this idempotent and safe on every start-up.
        public async Task<int> BackfillTemplateTypographyAsync(CancellationToken cancellationToken = default)
        {
            var face = ReportTypography.DefaultDocumentFace;

            // NO `LayoutJson.Contains("FontFamily")` PRE-FILTER, and that mattered: the question is whether
            // the PAGE names a face, and "FontFamily" also appears on every ELEMENT style. A template with a
            // bold heading in a chosen font therefore looked like it already had a document face and was
            // skipped whole — which is exactly the template this repair exists for. A substring test on a
            // document cannot answer a question about one node inside it.
            //
            // So the shape is decided per row, in memory, by PatchDocumentFace, which returns null when
            // there is nothing to do. The cost is parsing the live templates once per start-up; the cost of
            // the shortcut was not repairing the rows that needed it.
            var rows = await (
                from v in _db.ReportTemplateVersions
                join t in _db.ReportTemplates on v.TemplateId equals t.Id
                where t.DeletedAt == null && t.CurrentVersionNo == v.VersionNo
                select v).ToListAsync(cancellationToken);

            if (rows.Count == 0) return 0;

            var changed = 0;
            foreach (var row in rows)
            {
                // PATCHED AS JSON, not round-tripped through ReportLayout.
                //
                // Deserializing and reserializing would rewrite the whole document through TODAY'S model,
                // and anything the current build does not know about — a property added by a newer version,
                // an element kind since renamed — would be silently dropped from an author's saved design.
                // A repair is not allowed to lose work it did not come to fix. Editing the two nodes leaves
                // every other byte of the layout exactly as its author saved it.
                var json = PatchDocumentFace(row.LayoutJson, face);
                if (json is null) continue;

                row.LayoutJson = json;

                // The hash follows the content it describes. Leaving a stale one would make every later
                // integrity check report a tampered version.
                row.ContentHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
                changed++;
            }

            if (changed > 0) await _db.SaveChangesAsync(cancellationToken);
            return changed;
        }

        // Returns the layout with a document face written into whichever of its two page objects lacks one,
        // or NULL when there is nothing to change — including when the row will not parse. An unreadable
        // layout is left exactly as it is: replacing it with a re-serialized guess would destroy a design
        // this method was never asked to touch.
        private static string? PatchDocumentFace(string? layoutJson, string face)
        {
            if (string.IsNullOrWhiteSpace(layoutJson)) return null;

            System.Text.Json.Nodes.JsonNode? root;
            try
            {
                root = System.Text.Json.Nodes.JsonNode.Parse(layoutJson);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }

            if (root is not System.Text.Json.Nodes.JsonObject obj) return null;

            // BOTH page objects: ReportLayout.PageSetup is what HtmlReportRenderer reads and Visual.Page is
            // what ReportVisualRenderer reads. They are the same shape and they have drifted before.
            var touched = Fill(obj["PageSetup"], face) | Fill(obj["Visual"]?["Page"], face);
            return touched ? root.ToJsonString() : null;

            static bool Fill(System.Text.Json.Nodes.JsonNode? page, string face)
            {
                if (page is not System.Text.Json.Nodes.JsonObject setup) return false;

                // A KEY THAT IS PRESENT AND EMPTY still counts as unstated, because that is what the
                // renderers test: IsNullOrWhiteSpace, not null.
                var current = setup["FontFamily"]?.GetValue<string?>();
                if (!string.IsNullOrWhiteSpace(current)) return false;

                setup["FontFamily"] = face;
                return true;
            }
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