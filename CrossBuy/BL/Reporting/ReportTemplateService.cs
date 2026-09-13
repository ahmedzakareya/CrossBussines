using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — TEMPLATES, VERSIONING, AND THE FOUR SCOPES.
    //
    // Components 29–32 (Personal / Team / Company / Platform templates) are ONE mechanism, not four features. A
    // template carries a Scope, and resolution walks the scopes in precedence order:
    //
    //      Personal  ─┐  most specific — one person's own layout
    //      Team       │
    //      Company    │
    //      Platform  ─┘  least specific — what the product shipped
    //      (definition defaults, when no template exists at all)
    //
    // Building them as four tables or four services would have produced four copies of "find the default,
    // check access, load the version, fall back" — and they would have diverged. One table, one precedence walk.
    //
    // VERSIONING is immutability, not history-for-its-own-sake: an archived PDF records (TemplateId, VersionNo),
    // so that version must never change or the artifact stops being reproducible. Editing appends a version;
    // rolling back MOVES THE POINTER and appends nothing. Same instinct as our "reverse, never delete" rule for
    // the ledger — the record of what happened stays whole.
    // ============================================================================================

    // Where a resolved layout came from. Returned rather than inferred, because "why am I seeing this layout?" is
    // the first question a user asks when a report looks different from a colleague's.
    public enum ReportTemplateSource
    {
        // No template at all — the definition's own defaults.
        DefinitionDefault = 0,

        // The caller named a specific template id.
        Explicit = 1,

        // Resolved by precedence.
        Personal = 2,
        Team = 3,
        Company = 4,
        Platform = 5,
    }

    public sealed class ReportTemplateResolution
    {
        public ReportTemplate? Template { get; init; }
        public int? VersionNo { get; init; }
        public required ReportLayout Layout { get; init; }
        public ReportTemplateSource Source { get; init; }
        public IReadOnlyList<ReportDiagnostic> Diagnostics { get; init; } = Array.Empty<ReportDiagnostic>();

        public static ReportTemplateResolution FromDefinition(ReportDefinition definition,
            IReadOnlyList<ReportDiagnostic>? diagnostics = null) => new()
            {
                Source = ReportTemplateSource.DefinitionDefault,
                Diagnostics = diagnostics ?? Array.Empty<ReportDiagnostic>(),
                Layout = new ReportLayout
                {
                    VisibleColumns = definition.DefaultVisibleColumns.Select(c => c.Key).ToList(),
                    Filters = definition.DefaultFilters,
                    Sorts = definition.DefaultSorts,
                    Groupings = definition.DefaultGroupings,
                    PageSetup = definition.DefaultPageSetup,
                },
            };
    }

    public sealed class ReportTemplateSummary
    {
        public required int Id { get; init; }
        public required string ReportCode { get; init; }
        public required string Name { get; init; }
        public string? NameEn { get; init; }
        public ReportTemplateScope Scope { get; init; }
        public int? OwnerEmpId { get; init; }
        public int? TeamId { get; init; }
        public int CurrentVersionNo { get; init; }
        public bool IsDefault { get; init; }
        public int? CategoryId { get; init; }
        public DateTime? UpdatedAt { get; init; }

        // What the caller may do with it — filled from IReportAuthorizationService so a UI never has to guess.
        public ReportAccessLevel AccessLevel { get; init; }
    }

    public sealed class ReportTemplateVersionSummary
    {
        public required int VersionNo { get; init; }
        public bool IsPublished { get; init; }
        public bool IsCurrent { get; init; }
        public string? ChangeNote { get; init; }
        public DateTime? PublishedAt { get; init; }
        public int? PublishedBy { get; init; }
        public required string ContentHash { get; init; }
    }

    public sealed class ReportTemplateInput
    {
        // 0 = create.
        public int Id { get; init; }
        public required string ReportCode { get; init; }
        public required string Name { get; init; }
        public string? NameEn { get; init; }
        public ReportTemplateScope Scope { get; init; } = ReportTemplateScope.Personal;
        public int? TeamId { get; init; }
        public int? CategoryId { get; init; }
        public bool IsDefault { get; init; }
        public required ReportLayout Layout { get; init; }
        public string? ChangeNote { get; init; }
    }

    public sealed class ReportTemplateSaveResult
    {
        public bool Success { get; init; }
        public int TemplateId { get; init; }
        public int VersionNo { get; init; }

        // true = the layout was byte-identical to the current version, so no new version was created. Reported
        // rather than hidden: a UI that says "saved as v9" when nothing changed teaches users to distrust it.
        public bool Unchanged { get; init; }

        public IReadOnlyList<ReportDiagnostic> Diagnostics { get; init; } = Array.Empty<ReportDiagnostic>();

        public static ReportTemplateSaveResult Fail(params ReportDiagnostic[] diagnostics) =>
            new() { Success = false, Diagnostics = diagnostics };
    }

    public interface IReportTemplateService
    {
        // The precedence walk. Never throws for a missing template — an absent or invisible template degrades to
        // the next scope with a diagnostic, because a stale bookmark must not break a report.
        Task<ReportTemplateResolution> ResolveAsync(ReportDefinition definition, int? templateId, int? versionNo,
            BusinessContext context, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ReportTemplateSummary>> ListAsync(string reportCode, BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<ReportTemplateSaveResult> SaveAsync(ReportTemplateInput input, BusinessContext context,
            CancellationToken cancellationToken = default);

        // Copy a template (typically a Platform one) into a scope the caller may edit. This is what "edit a
        // platform template" actually means.
        Task<ReportTemplateSaveResult> ForkAsync(int templateId, ReportTemplateScope targetScope, string name,
            BusinessContext context, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ReportTemplateVersionSummary>> ListVersionsAsync(int templateId, BusinessContext context,
            CancellationToken cancellationToken = default);

        // Moves CurrentVersionNo to an existing published version. Appends nothing — history is not rewritten.
        Task<ReportTemplateSaveResult> RollbackAsync(int templateId, int toVersionNo, BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<bool> SetDefaultAsync(int templateId, BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<bool> DeleteAsync(int templateId, BusinessContext context,
            CancellationToken cancellationToken = default);
    }

    public class ReportTemplateService : IReportTemplateService
    {
        public const string CodeTemplateNotFound = "template_not_found";
        public const string CodeTemplateInvalidLayout = "template_layout_invalid";
        public const string CodeTemplateNameRequired = "template_name_required";
        public const string CodeTemplateScopeInvalid = "template_scope_invalid";
        public const string CodeTemplateVersionNotFound = "template_version_not_found";
        public const string CodeTemplateDenied = "template_denied";
        public const string CodeLayoutCorrupt = "template_layout_corrupt";

        private readonly CrossDbContext _db;
        private readonly IReportCatalog _catalog;
        private readonly IReportAuthorizationService _authorization;
        private readonly IReportTeamResolver _teams;
        private readonly IReportClock _clock;

        public ReportTemplateService(CrossDbContext db, IReportCatalog catalog,
            IReportAuthorizationService authorization, IReportTeamResolver teams, IReportClock clock)
        {
            _db = db;
            _catalog = catalog;
            _authorization = authorization;
            _teams = teams;
            _clock = clock;
        }

        // ------------------------------------------------------------------------------------------------
        // RESOLUTION
        // ------------------------------------------------------------------------------------------------
        public async Task<ReportTemplateResolution> ResolveAsync(ReportDefinition definition, int? templateId,
            int? versionNo, BusinessContext context, CancellationToken cancellationToken = default)
        {
            var diagnostics = new List<ReportDiagnostic>();

            // ---- explicit id -----------------------------------------------------------------------------
            if (templateId is > 0)
            {
                var template = await VisibleTemplates(definition.Code, context)
                    .FirstOrDefaultAsync(t => t.Id == templateId.Value, cancellationToken);

                if (template == null)
                {
                    diagnostics.Add(ReportDiagnostic.Warning(CodeTemplateNotFound,
                        $"Template {templateId} was not found for report '{definition.Code}'; the report's " +
                        "default layout was used instead."));
                    return await ResolveByPrecedenceAsync(definition, context, diagnostics, cancellationToken);
                }

                var decision = await _authorization.AuthorizeTemplateAsync(definition, template,
                    ReportAccessLevel.Run, context, cancellationToken);

                if (!decision.Allowed)
                {
                    // Degraded, not refused: the REPORT was already authorized by the engine before we got here.
                    // Losing access to one layout must not lose access to the report.
                    diagnostics.Add(ReportDiagnostic.Warning(CodeTemplateDenied,
                        $"Template {templateId} is not available to you ({decision.ReasonCode}); the report's " +
                        "default layout was used instead."));
                    return await ResolveByPrecedenceAsync(definition, context, diagnostics, cancellationToken);
                }

                return await LoadAsync(definition, template, versionNo, ReportTemplateSource.Explicit,
                    diagnostics, cancellationToken);
            }

            return await ResolveByPrecedenceAsync(definition, context, diagnostics, cancellationToken);
        }

        private async Task<ReportTemplateResolution> ResolveByPrecedenceAsync(ReportDefinition definition,
            BusinessContext context, List<ReportDiagnostic> diagnostics, CancellationToken cancellationToken)
        {
            // One query for every candidate, then the walk in memory. Four scope-ordered queries would be four
            // round trips for what is a handful of rows.
            var candidates = await VisibleTemplates(definition.Code, context)
                .Select(t => new
                {
                    t.Id, t.Scope, t.OwnerEmpId, t.TeamId, t.IsDefault, t.CurrentVersionNo, t.UpdatedAt, t.CreatedAt,
                })
                .ToListAsync(cancellationToken);

            if (candidates.Count == 0) return ReportTemplateResolution.FromDefinition(definition, diagnostics);

            var teamIds = candidates.Any(c => c.Scope == ReportTemplateScope.Team)
                ? await _teams.GetTeamIdsAsync(context, cancellationToken)
                : (IReadOnlyList<int>)Array.Empty<int>();

            // Precedence order is the enum's descending numeric order — see ReportTemplateScope, where the values
            // ARE the precedence.
            foreach (var scope in new[]
                     {
                         ReportTemplateScope.Personal, ReportTemplateScope.Team,
                         ReportTemplateScope.Company, ReportTemplateScope.Platform,
                     })
            {
                if (!definition.Capabilities.SupportsScope(scope)) continue;

                var inScope = candidates.Where(c => c.Scope == scope);

                inScope = scope switch
                {
                    ReportTemplateScope.Personal => inScope.Where(c =>
                        c.OwnerEmpId != null && c.OwnerEmpId == context.EmployeeId),
                    ReportTemplateScope.Team => inScope.Where(c =>
                        c.TeamId != null && teamIds.Contains(c.TeamId.Value)),
                    _ => inScope,
                };

                // Within a scope: the one marked default, else the most recently touched. Deterministic, so two
                // requests never resolve different layouts.
                var chosen = inScope
                    .OrderByDescending(c => c.IsDefault)
                    .ThenByDescending(c => c.UpdatedAt ?? c.CreatedAt ?? DateTime.MinValue)
                    .ThenByDescending(c => c.Id)
                    .FirstOrDefault();

                if (chosen == null) continue;

                var template = await _db.ReportTemplates.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == chosen.Id, cancellationToken);
                if (template == null) continue;

                var source = scope switch
                {
                    ReportTemplateScope.Personal => ReportTemplateSource.Personal,
                    ReportTemplateScope.Team => ReportTemplateSource.Team,
                    ReportTemplateScope.Company => ReportTemplateSource.Company,
                    _ => ReportTemplateSource.Platform,
                };

                return await LoadAsync(definition, template, versionNo: null, source, diagnostics,
                    cancellationToken);
            }

            return ReportTemplateResolution.FromDefinition(definition, diagnostics);
        }

        private async Task<ReportTemplateResolution> LoadAsync(ReportDefinition definition, ReportTemplate template,
            int? versionNo, ReportTemplateSource source, List<ReportDiagnostic> diagnostics,
            CancellationToken cancellationToken)
        {
            var wanted = versionNo ?? template.CurrentVersionNo;

            var version = await _db.ReportTemplateVersions.AsNoTracking()
                .FirstOrDefaultAsync(v => v.TemplateId == template.Id && v.VersionNo == wanted, cancellationToken);

            if (version == null)
            {
                diagnostics.Add(ReportDiagnostic.Warning(CodeTemplateVersionNotFound,
                    $"Template {template.Id} has no version {wanted}; the report's default layout was used."));
                return ReportTemplateResolution.FromDefinition(definition, diagnostics);
            }

            var layout = ReportLayoutJson.Deserialize(version.LayoutJson);
            if (layout == null)
            {
                // Corrupt JSON degrades to the definition defaults rather than throwing. A single bad row must not
                // take a report offline, and the warning names the row so it can be fixed.
                diagnostics.Add(ReportDiagnostic.Warning(CodeLayoutCorrupt,
                    $"Template {template.Id} version {wanted} has an unreadable layout; the report's default " +
                    "layout was used."));
                return ReportTemplateResolution.FromDefinition(definition, diagnostics);
            }

            return new ReportTemplateResolution
            {
                Template = template,
                VersionNo = wanted,
                Layout = layout,
                Source = source,
                Diagnostics = diagnostics,
            };
        }

        // ------------------------------------------------------------------------------------------------
        // LISTING
        // ------------------------------------------------------------------------------------------------
        public async Task<IReadOnlyList<ReportTemplateSummary>> ListAsync(string reportCode,
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            if (!_catalog.TryGetDefinition(reportCode, out var definition) || definition == null)
                throw new ReportNotRegisteredException(reportCode);

            var rows = await VisibleTemplates(reportCode, context).ToListAsync(cancellationToken);
            var result = new List<ReportTemplateSummary>();

            foreach (var template in rows)
            {
                var decision = await _authorization.AuthorizeTemplateAsync(definition, template,
                    ReportAccessLevel.View, context, cancellationToken);
                if (!decision.Allowed) continue;

                result.Add(new ReportTemplateSummary
                {
                    Id = template.Id,
                    ReportCode = template.ReportCode,
                    Name = template.Name,
                    NameEn = template.NameEn,
                    Scope = template.Scope,
                    OwnerEmpId = template.OwnerEmpId,
                    TeamId = template.TeamId,
                    CurrentVersionNo = template.CurrentVersionNo,
                    IsDefault = template.IsDefault,
                    CategoryId = template.CategoryId,
                    UpdatedAt = template.UpdatedAt ?? template.CreatedAt,
                    AccessLevel = decision.EffectiveLevel,
                });
            }

            return result
                .OrderByDescending(t => (int)t.Scope)
                .ThenByDescending(t => t.IsDefault)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
        }

        public async Task<IReadOnlyList<ReportTemplateVersionSummary>> ListVersionsAsync(int templateId,
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            var (template, definition, decision) = await LoadForAccessAsync(templateId, ReportAccessLevel.View,
                context, cancellationToken);
            if (template == null || !decision.Allowed) return Array.Empty<ReportTemplateVersionSummary>();

            var versions = await _db.ReportTemplateVersions.AsNoTracking()
                .Where(v => v.TemplateId == templateId)
                .OrderByDescending(v => v.VersionNo)
                .ToListAsync(cancellationToken);

            return versions.Select(v => new ReportTemplateVersionSummary
            {
                VersionNo = v.VersionNo,
                IsPublished = v.IsPublished,
                IsCurrent = v.VersionNo == template.CurrentVersionNo,
                ChangeNote = v.ChangeNote,
                PublishedAt = v.PublishedAt,
                PublishedBy = v.PublishedBy,
                ContentHash = v.ContentHash,
            }).ToList();
        }

        // ------------------------------------------------------------------------------------------------
        // SAVE (create / new version)
        // ------------------------------------------------------------------------------------------------
        public async Task<ReportTemplateSaveResult> SaveAsync(ReportTemplateInput input, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0)
                return ReportTemplateSaveResult.Fail(ReportDiagnostic.Error(
                    ReportAuthorizationService.CodeCompanyUnresolved,
                    "No company is resolved for this request."));

            if (!_catalog.TryGetDefinition(input.ReportCode, out var definition) || definition == null)
                throw new ReportNotRegisteredException(input.ReportCode);

            if (string.IsNullOrWhiteSpace(input.Name))
                return ReportTemplateSaveResult.Fail(ReportDiagnostic.Error(CodeTemplateNameRequired,
                    "A template name is required.", nameof(input.Name)));

            // A tenant may never create a Platform template. Platform content arrives by deployment; allowing a
            // tenant to write one would let one company publish a layout every other company resolves.
            if (input.Scope == ReportTemplateScope.Platform)
                return ReportTemplateSaveResult.Fail(ReportDiagnostic.Error(CodeTemplateScopeInvalid,
                    "Platform templates are created by deployment, not by a tenant."));

            if (!definition.Capabilities.SupportsScope(input.Scope))
                return ReportTemplateSaveResult.Fail(ReportDiagnostic.Error(CodeTemplateScopeInvalid,
                    $"Report '{definition.Code}' does not permit {input.Scope} templates."));

            if (input.Scope == ReportTemplateScope.Team && input.TeamId is not > 0)
                return ReportTemplateSaveResult.Fail(ReportDiagnostic.Error(CodeTemplateScopeInvalid,
                    "A team template must name a team."));

            if (input.Scope == ReportTemplateScope.Personal && context.EmployeeId is not > 0)
                return ReportTemplateSaveResult.Fail(ReportDiagnostic.Error(CodeTemplateScopeInvalid,
                    "A personal template requires a resolved employee."));

            // The layout is validated against the DEFINITION before it is stored. Storing an invalid layout would
            // move the failure to render time, where it looks like a report bug rather than a save mistake.
            var layoutDiagnostics = ValidateLayout(definition, input.Layout);
            if (layoutDiagnostics.Any(d => d.Severity == ReportDiagnosticSeverity.Error))
                return new ReportTemplateSaveResult { Success = false, Diagnostics = layoutDiagnostics };

            var now = _clock.LocalNow;
            var json = ReportLayoutJson.Serialize(input.Layout);
            var hash = Hash(json);

            ReportTemplate template;

            if (input.Id > 0)
            {
                var (existing, _, decision) = await LoadForAccessAsync(input.Id, ReportAccessLevel.Edit, context,
                    cancellationToken);
                if (existing == null)
                    return ReportTemplateSaveResult.Fail(ReportDiagnostic.Error(CodeTemplateNotFound,
                        $"Template {input.Id} was not found."));
                if (!decision.Allowed)
                    return ReportTemplateSaveResult.Fail(ReportDiagnostic.Error(
                        decision.ReasonCode ?? CodeTemplateDenied,
                        decision.Reason ?? "You may not edit that template."));

                // Tracked instance, not the AsNoTracking one used for the access check.
                template = await _db.ReportTemplates.FirstAsync(t => t.Id == input.Id, cancellationToken);
                template.Name = input.Name.Trim();
                template.NameEn = input.NameEn?.Trim();
                template.CategoryId = input.CategoryId;

                // PUBLISHING AN EXISTING LAYOUT. Scope used to be set only when CREATING, so "promote my
                // draft to the company standard" could only be done by forking a copy and leaving two
                // templates where the author wanted one.
                //
                // Gated on MANAGE, which the access check above has already required for Edit — and
                // Manage is what an owner and an administrator hold, not a Run-only viewer. Platform is
                // refused further up for everyone, so this cannot publish to other tenants.
                if (input.Scope != template.Scope)
                {
                    if (decision.EffectiveLevel < ReportAccessLevel.Manage)
                        return ReportTemplateSaveResult.Fail(ReportDiagnostic.Error(CodeTemplateDenied,
                            "Changing who a template is for needs Manage on it."));

                    template.Scope = input.Scope;

                    // A team template must name its team; every other scope must NOT keep a stale one.
                    template.TeamId = input.Scope == ReportTemplateScope.Team ? input.TeamId : null;

                    // The default is per (company, report, scope, owner). A template that moves scope
                    // would otherwise arrive as a second default in its new home.
                    template.IsDefault = false;
                }
                template.updatedBy = context.EmployeeId;
                template.UpdatedAt = now;

                var current = await _db.ReportTemplateVersions.AsNoTracking()
                    .Where(v => v.TemplateId == template.Id && v.VersionNo == template.CurrentVersionNo)
                    .Select(v => v.ContentHash)
                    .FirstOrDefaultAsync(cancellationToken);

                if (string.Equals(current, hash, StringComparison.Ordinal))
                {
                    // Identical layout → no new version. Idempotent save: a UI that autosaves would otherwise
                    // grow a version per keystroke and make the history useless.
                    await _db.SaveChangesAsync(cancellationToken);
                    if (input.IsDefault) await ApplyDefaultAsync(template, context, cancellationToken);
                    return new ReportTemplateSaveResult
                    {
                        Success = true,
                        TemplateId = template.Id,
                        VersionNo = template.CurrentVersionNo,
                        Unchanged = true,
                        Diagnostics = layoutDiagnostics,
                    };
                }
            }
            else
            {
                template = new ReportTemplate
                {
                    CompanyID = context.CompanyId,
                    ReportCode = definition.Code,
                    Name = input.Name.Trim(),
                    NameEn = input.NameEn?.Trim(),
                    Scope = input.Scope,
                    OwnerEmpId = context.EmployeeId,
                    TeamId = input.Scope == ReportTemplateScope.Team ? input.TeamId : null,
                    CategoryId = input.CategoryId,
                    CurrentVersionNo = 0,
                    CreatedBy = context.EmployeeId,
                    CreatedAt = now,
                };
                _db.ReportTemplates.Add(template);
                await _db.SaveChangesAsync(cancellationToken);
            }

            // Next version number from the table, not from a counter. Concurrency note: two simultaneous saves of
            // the SAME template could both compute the same next number; the (TemplateId, VersionNo) unique index
            // in deploy/sql/reporting_platform.sql makes the loser fail rather than overwrite. A template is
            // edited by one person at a time, so a retry loop would be machinery for a case that does not occur —
            // and a lost version is far better than a silently overwritten one.
            var nextVersion = await _db.ReportTemplateVersions
                .Where(v => v.TemplateId == template.Id)
                .Select(v => (int?)v.VersionNo)
                .MaxAsync(cancellationToken) ?? 0;
            nextVersion += 1;

            _db.ReportTemplateVersions.Add(new ReportTemplateVersion
            {
                CompanyID = template.CompanyID,
                TemplateId = template.Id,
                VersionNo = nextVersion,
                LayoutJson = json,
                ContentHash = hash,
                ChangeNote = input.ChangeNote,
                IsPublished = true,
                PublishedAt = now,
                PublishedBy = context.EmployeeId,
                CreatedBy = context.EmployeeId,
                CreatedAt = now,
            });

            template.CurrentVersionNo = nextVersion;
            template.UpdatedAt = now;
            template.updatedBy = context.EmployeeId;

            await _db.SaveChangesAsync(cancellationToken);

            if (input.IsDefault) await ApplyDefaultAsync(template, context, cancellationToken);

            return new ReportTemplateSaveResult
            {
                Success = true,
                TemplateId = template.Id,
                VersionNo = nextVersion,
                Diagnostics = layoutDiagnostics,
            };
        }

        // ------------------------------------------------------------------------------------------------
        // FORK — what "edit a platform template" really is
        // ------------------------------------------------------------------------------------------------
        public async Task<ReportTemplateSaveResult> ForkAsync(int templateId, ReportTemplateScope targetScope,
            string name, BusinessContext context, CancellationToken cancellationToken = default)
        {
            var (source, definition, decision) = await LoadForAccessAsync(templateId, ReportAccessLevel.View,
                context, cancellationToken);

            if (source == null || definition == null)
                return ReportTemplateSaveResult.Fail(ReportDiagnostic.Error(CodeTemplateNotFound,
                    $"Template {templateId} was not found."));
            if (!decision.Allowed)
                return ReportTemplateSaveResult.Fail(ReportDiagnostic.Error(
                    decision.ReasonCode ?? CodeTemplateDenied,
                    decision.Reason ?? "You may not read that template."));

            var version = await _db.ReportTemplateVersions.AsNoTracking()
                .FirstOrDefaultAsync(v => v.TemplateId == source.Id && v.VersionNo == source.CurrentVersionNo,
                    cancellationToken);

            var layout = version == null ? null : ReportLayoutJson.Deserialize(version.LayoutJson);
            if (layout == null)
                return ReportTemplateSaveResult.Fail(ReportDiagnostic.Error(CodeLayoutCorrupt,
                    $"Template {templateId} has no readable current layout to fork."));

            // A fork is an ordinary create — so it goes through SaveAsync and gets every validation, scope rule
            // and version-1 publish that a hand-made template gets. A fork that took a shortcut would be the one
            // path that could store an unvalidated layout.
            var teamIds = targetScope == ReportTemplateScope.Team
                ? await _teams.GetTeamIdsAsync(context, cancellationToken)
                : (IReadOnlyList<int>)Array.Empty<int>();

            return await SaveAsync(new ReportTemplateInput
            {
                ReportCode = source.ReportCode,
                Name = string.IsNullOrWhiteSpace(name) ? source.Name : name.Trim(),
                NameEn = source.NameEn,
                Scope = targetScope,
                TeamId = targetScope == ReportTemplateScope.Team ? teamIds.FirstOrDefault() : null,
                CategoryId = source.CategoryId,
                Layout = layout,
                ChangeNote = $"Forked from template {source.Id} v{source.CurrentVersionNo}",
            }, context, cancellationToken);
        }

        // ------------------------------------------------------------------------------------------------
        // ROLLBACK — move the pointer, append nothing
        // ------------------------------------------------------------------------------------------------
        public async Task<ReportTemplateSaveResult> RollbackAsync(int templateId, int toVersionNo,
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            var (existing, _, decision) = await LoadForAccessAsync(templateId, ReportAccessLevel.Edit, context,
                cancellationToken);

            if (existing == null)
                return ReportTemplateSaveResult.Fail(ReportDiagnostic.Error(CodeTemplateNotFound,
                    $"Template {templateId} was not found."));
            if (!decision.Allowed)
                return ReportTemplateSaveResult.Fail(ReportDiagnostic.Error(
                    decision.ReasonCode ?? CodeTemplateDenied,
                    decision.Reason ?? "You may not edit that template."));

            var target = await _db.ReportTemplateVersions.AsNoTracking()
                .FirstOrDefaultAsync(v => v.TemplateId == templateId && v.VersionNo == toVersionNo
                                          && v.IsPublished, cancellationToken);
            if (target == null)
                return ReportTemplateSaveResult.Fail(ReportDiagnostic.Error(CodeTemplateVersionNotFound,
                    $"Template {templateId} has no published version {toVersionNo}."));

            var template = await _db.ReportTemplates.FirstAsync(t => t.Id == templateId, cancellationToken);
            template.CurrentVersionNo = toVersionNo;
            template.UpdatedAt = _clock.LocalNow;
            template.updatedBy = context.EmployeeId;
            await _db.SaveChangesAsync(cancellationToken);

            return new ReportTemplateSaveResult
            {
                Success = true,
                TemplateId = templateId,
                VersionNo = toVersionNo,
                Unchanged = true,   // no new version was created — that is the point
            };
        }

        public async Task<bool> SetDefaultAsync(int templateId, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            var (existing, _, decision) = await LoadForAccessAsync(templateId, ReportAccessLevel.Edit, context,
                cancellationToken);
            if (existing == null || !decision.Allowed) return false;

            var template = await _db.ReportTemplates.FirstAsync(t => t.Id == templateId, cancellationToken);
            await ApplyDefaultAsync(template, context, cancellationToken);
            return true;
        }

        public async Task<bool> DeleteAsync(int templateId, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            var (existing, _, decision) = await LoadForAccessAsync(templateId, ReportAccessLevel.Manage, context,
                cancellationToken);
            if (existing == null || !decision.Allowed) return false;

            var template = await _db.ReportTemplates.FirstAsync(t => t.Id == templateId, cancellationToken);

            // Soft delete — the feature-module convention, and here it is also a correctness requirement: an
            // archived artifact references (TemplateId, VersionNo), so hard-deleting a template would orphan
            // archive rows and make an archived document unexplainable.
            template.DeletedAt = _clock.LocalNow;
            template.updatedBy = context.EmployeeId;
            template.UpdatedAt = _clock.LocalNow;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        // ------------------------------------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------------------------------------

        // The ONE company-scoped template query. Every read goes through it, so the "own company OR platform"
        // rule exists in exactly one place. CompanyID == 0 is the platform row — see ReportTemplate.CompanyID.
        private IQueryable<ReportTemplate> VisibleTemplates(string reportCode, BusinessContext context) =>
            _db.ReportTemplates.AsNoTracking()
                .Where(t => t.ReportCode == reportCode
                            && t.DeletedAt == null
                            && (t.CompanyID == context.CompanyId
                                || (t.CompanyID == 0 && t.Scope == ReportTemplateScope.Platform)));

        private async Task<(ReportTemplate? Template, ReportDefinition? Definition, ReportAccessDecision Decision)>
            LoadForAccessAsync(int templateId, ReportAccessLevel required, BusinessContext context,
                CancellationToken cancellationToken)
        {
            var template = await _db.ReportTemplates.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == templateId && t.DeletedAt == null, cancellationToken);

            if (template == null)
                return (null, null, ReportAccessDecision.Deny(CodeTemplateNotFound, "Template not found."));

            if (!_catalog.TryGetDefinition(template.ReportCode, out var definition) || definition == null)
                return (template, null, ReportAccessDecision.Deny(CodeTemplateNotFound,
                    $"Template {templateId} refers to unregistered report '{template.ReportCode}'."));

            var decision = await _authorization.AuthorizeTemplateAsync(definition, template, required, context,
                cancellationToken);
            return (template, definition, decision);
        }

        // "At most one default per group" is enforced here rather than by a unique index: the group differs per
        // scope (company+report+scope, plus owner for Personal and team for Team), which would need one filtered
        // index per scope shape. One service owns the invariant instead.
        private async Task ApplyDefaultAsync(ReportTemplate template, BusinessContext context,
            CancellationToken cancellationToken)
        {
            var siblings = await _db.ReportTemplates
                .Where(t => t.CompanyID == template.CompanyID
                            && t.ReportCode == template.ReportCode
                            && t.Scope == template.Scope
                            && t.DeletedAt == null
                            && (template.Scope != ReportTemplateScope.Personal || t.OwnerEmpId == template.OwnerEmpId)
                            && (template.Scope != ReportTemplateScope.Team || t.TeamId == template.TeamId))
                .ToListAsync(cancellationToken);

            foreach (var sibling in siblings)
                sibling.IsDefault = sibling.Id == template.Id;

            template.IsDefault = true;
            template.UpdatedAt = _clock.LocalNow;
            template.updatedBy = context.EmployeeId;
            await _db.SaveChangesAsync(cancellationToken);
        }

        // Layout validation. Column/sort/grouping problems are WARNINGS (cosmetic, and a definition may have
        // dropped a column since); a filter problem is an ERROR, for the same reason the shaper treats it as one —
        // an unusable filter would widen the result set.
        private static List<ReportDiagnostic> ValidateLayout(ReportDefinition definition, ReportLayout layout)
        {
            var diagnostics = new List<ReportDiagnostic>();

            foreach (var key in layout.VisibleColumns)
            {
                var column = definition.FindColumn(key);
                if (column == null)
                    diagnostics.Add(ReportDiagnostic.Warning(CodeTemplateInvalidLayout,
                        $"Column '{key}' is not declared by report '{definition.Code}'.", key));
                else if (column.Internal)
                    diagnostics.Add(ReportDiagnostic.Warning(CodeTemplateInvalidLayout,
                        $"Column '{key}' is internal and cannot be shown.", key));
            }

            foreach (var filter in layout.Filters)
            {
                var column = definition.FindColumn(filter.Field);
                if (column == null || column.Internal || !column.Filterable)
                    diagnostics.Add(ReportDiagnostic.Error(CodeTemplateInvalidLayout,
                        $"Filter field '{filter.Field}' is not a filterable column of report " +
                        $"'{definition.Code}'.", filter.Field));
            }

            foreach (var sort in layout.Sorts)
                if (definition.FindColumn(sort.Field) is null or { Sortable: false } or { Internal: true })
                    diagnostics.Add(ReportDiagnostic.Warning(CodeTemplateInvalidLayout,
                        $"Sort field '{sort.Field}' is not a sortable column.", sort.Field));

            foreach (var grouping in layout.Groupings)
                if (definition.FindColumn(grouping.Field) is null or { Groupable: false } or { Internal: true })
                    diagnostics.Add(ReportDiagnostic.Warning(CodeTemplateInvalidLayout,
                        $"Grouping field '{grouping.Field}' is not a groupable column.", grouping.Field));

            foreach (var key in layout.Parameters.Keys)
            {
                var parameter = definition.FindParameter(key);
                if (parameter == null)
                    diagnostics.Add(ReportDiagnostic.Warning(CodeTemplateInvalidLayout,
                        $"Parameter '{key}' is not declared by report '{definition.Code}'.", key));
                else if (parameter.SystemSupplied)
                    // A template that could bake CompanyId would be a stored cross-tenant read. Rejected outright.
                    diagnostics.Add(ReportDiagnostic.Error(CodeTemplateInvalidLayout,
                        $"Parameter '{key}' is supplied by the platform and may not be stored in a template.",
                        key));
            }

            return diagnostics;
        }

        private static string Hash(string json) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    // Layout (de)serialization in one place, with ONE options instance.
    //
    // Shared static options matter for more than tidiness: JsonSerializerOptions caches its reflection metadata,
    // and constructing a fresh instance per call is a documented System.Text.Json performance trap. It also
    // guarantantees that what SaveAsync writes is exactly what ResolveAsync reads — a second options object with
    // different naming would silently produce layouts that round-trip to empty.
    public static class ReportLayoutJson
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            // PascalCase (the default) — these rows are read by humans during support, and matching the C#
            // property names makes a stored layout self-describing.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        public static string Serialize(ReportLayout layout) => JsonSerializer.Serialize(layout, Options);

        // null = unreadable. Callers degrade to the definition defaults rather than failing the report.
        public static ReportLayout? Deserialize(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonSerializer.Deserialize<ReportLayout>(json, Options);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}