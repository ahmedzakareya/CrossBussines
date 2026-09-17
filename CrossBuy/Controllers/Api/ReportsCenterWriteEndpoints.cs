// =================================================================================================
// CrossBusiness Reporting Platform — THE SEVEN WRITE ENDPOINTS.  ACTIVE.
//
// Parked as `.cs.pending` for two increments because CBA001 blocked every endpoint: the analyzer
// credits authorization when an endpoint's call graph reaches a type in
// AuthorizationSurface.AuthorityTypes, and IReportAuthorizationService was not on that list.
//
// TAB 1 has now declared it an authority, and their note was precise about what that did and did not
// buy: endpoints that authorize by row ownership alone "never call this service, so they are NOT
// credited by this addition and remain visible to CBA001. That is the correct outcome."
//
// Three endpoints were in exactly that position and are now genuinely gated rather than quietly
// exempted:
//
//   Fork / SetDefault / DeleteLayout  identify their target by TEMPLATE id and delegated straight to
//                                     IReportTemplateService. They now resolve the template row,
//                                     read its report code FROM THE ROW, and authorize through
//                                     IReportAuthorizationService.AuthorizeTemplateAsync first.
//   ReorderFavorites                  authorized by row ownership only. It now resolves each id
//                                     against the caller's own favourites and authorizes the report
//                                     behind it, dropping anything that does not clear.
//
// The service layer still performs its own checks. These are a gate at the boundary, not a
// replacement — and the analyzer can see them, which is the point.
//
// NOTHING WAS SUPPRESSED. CBA001 is still an error; no baseline entry was added; no write was
// reshaped as a GET.
// =================================================================================================

using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers.Api
{
    // A SEPARATE CONTROLLER from ReportsCenterApiController, sharing its route prefix.
    //
    // Not for tidiness: it is so the reads and the writes have DIFFERENT filters. The read controller
    // must not carry [AutoValidateAntiforgeryToken] — it would break bookmarkable report links, which
    // are the whole point of Preview and Export being GETs. The writes must carry it.
    //
    // ANTI-FORGERY, and why it is needed even for a JSON API: these endpoints authenticate by SESSION
    // COOKIE, which the browser attaches to a cross-site form post automatically. A bearer-token API
    // would not need the token; a cookie API does. The client sends it as the RequestVerificationToken
    // header, which a cross-origin page cannot set.
    [ApiController]
    [Route("api/reports")]
    [Authorize]
    [AutoValidateAntiforgeryToken]
    [Produces("application/json")]
    public sealed class ReportsCenterWriteApiController : ControllerBase
    {
        private readonly IReportTemplateService _templates;
        private readonly IReportLibraryService _library;
        private readonly IReportService _reports;
        private readonly IReportAuthorizationService _authorization;
        private readonly IBusinessContextAccessor _contexts;
        private readonly CrossDbContext _db;
        private readonly IReportDerivedDatasetStore _derived;
        private readonly IReportDatasetRegistry _datasets;

        public ReportsCenterWriteApiController(
            IReportTemplateService templates,
            IReportLibraryService library,
            IReportService reports,
            IReportAuthorizationService authorization,
            IBusinessContextAccessor contexts,
            CrossDbContext db,
            IReportDerivedDatasetStore derived,
            IReportDatasetRegistry datasets)
        {
            _templates = templates;
            _library = library;
            _reports = reports;
            _authorization = authorization;
            _contexts = contexts;
            _db = db;
            _derived = derived;
            _datasets = datasets;
        }

        // An unresolved context writes NOTHING. CLAUDE.md: an unresolved company scope reads no
        // company-scoped data and writes none. Fail closed; never default to a company.
        private async Task<BusinessContext?> ResolveAsync(CancellationToken ct)
        {
            var context = await _contexts.TryGetCurrentAsync(ct);
            return context is { CompanyId: > 0 } ? context : null;
        }

        // THE AUTHORITY CALL, in one place.
        //
        // Every write below routes through this, which is what makes the analyzer's credit honest
        // rather than nominal: there is no path to a mutation that does not pass an authorization
        // decision, and the decision is the platform's own report gate, not a local check.
        //
        // Returns the definition on success so the caller does not look it up twice.
        private async Task<(bool Allowed, ReportDefinition? Definition)> AuthorizeAsync(
            string reportCode, ReportAccessLevel required, BusinessContext context, CancellationToken ct)
        {
            var definition = await _reports.DescribeAsync(reportCode, ct);
            if (definition is null) return (false, null);

            var decision = await _authorization.AuthorizeReportAsync(definition, required, context, ct);
            return (decision.Allowed, definition);
        }

        // THE SAME GATE, for the three endpoints that identify their target by TEMPLATE id rather than by
        // report code: Fork, SetDefault, DeleteLayout.
        //
        // Each already delegates to IReportTemplateService, which applies the report gate and then the
        // template's scope rules before touching a row — but the analyzer deliberately does not descend into
        // arbitrary services to discover that, and it is right not to: a rule found by descent is a rule
        // nobody declared and any other caller can bypass.
        //
        // So the code is resolved FROM THE TEMPLATE ROW, not from the request. Taking a reportCode parameter
        // here would let a caller pass report A's code while acting on report B's template — a gate that
        // checks something other than the thing being changed. Reading it from the row makes that impossible.
        //
        // The company predicate is explicit as well as being covered by the ambient scope: this resolves an
        // id straight from the wire, and an id from the wire gets its own company check.
        private async Task<(bool Allowed, ReportTemplate? Template, ReportDefinition? Definition)>
            AuthorizeTemplateAsync(int templateId, ReportAccessLevel required, BusinessContext context,
                CancellationToken ct)
        {
            var template = await _db.ReportTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    t => t.Id == templateId
                         && t.DeletedAt == null
                         // A Platform template (CompanyID 0) is readable by every company; anything else must
                         // belong to the caller's. Mirrors ReportAuthorizationService's own rule.
                         && (t.CompanyID == context.CompanyId || t.Scope == ReportTemplateScope.Platform),
                    ct);

            if (template is null) return (false, null, null);

            var definition = await _reports.DescribeAsync(template.ReportCode, ct);
            if (definition is null) return (false, null, null);

            var decision = await _authorization.AuthorizeTemplateAsync(definition, template, required, context, ct);
            return (decision.Allowed, template, definition);
        }

        // =========================================================================================
        // SAVED REPORTS (templates)
        // =========================================================================================

        public sealed class SaveLayoutRequest
        {
            public int Id { get; set; }                       // 0 = create
            public string ReportCode { get; set; } = "";
            public string Name { get; set; } = "";
            public string? NameEn { get; set; }
            public ReportTemplateScope Scope { get; set; } = ReportTemplateScope.Personal;
            public int? TeamId { get; set; }
            public int? CategoryId { get; set; }
            public bool IsDefault { get; set; }
            public ReportLayout Layout { get; set; } = new();
            public string? ChangeNote { get; set; }

            // THE LAYOUT THE SCREEN WAS SHOWING when this save was made.
            //
            // "Save the current setup" produced a template with NO DESIGN: the viewer posts the
            // parameters it can see, and it cannot see the resolved template's bands, columns, page or
            // font. So saving the report in front of you created a bare row, and picking it afterwards
            // rendered a plain table -- the design silently gone, with nothing on screen to say why.
            //
            // Naming the base here lets the SERVER, which did the resolving, start from that layout and
            // lay the posted parameters over it. Ignored when editing an existing template: that one
            // already has a layout of its own.
            public int? BaseTemplateId { get; set; }

            // OPTIMISTIC CONCURRENCY. The version the client believes it is editing. The service
            // refuses when it no longer matches, so two people editing one company layout produce a
            // visible conflict rather than a silent last-writer-wins overwrite.
            public int? ExpectedVersionNo { get; set; }
        }

        [HttpPost("saved")]
        public async Task<IActionResult> SaveLayout([FromBody] SaveLayoutRequest request, CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();
            if (request is null || string.IsNullOrWhiteSpace(request.ReportCode)) return BadRequest();

            // Creating needs Run on the report; EDITING an existing layout needs Edit on THAT layout,
            // which AuthorizeTemplateAsync decides inside SaveAsync. Asking for Run here and letting
            // the service apply the template rules is the ordering the permissions model requires —
            // a share may raise the level, so denying at Edit up front would refuse a legitimate one.
            var (allowed, _) = await AuthorizeAsync(
                request.ReportCode, ReportAccessLevel.Run, context, ct);
            if (!allowed) return NotFound();

            if (request.ExpectedVersionNo is > 0)
            {
                var versions = await _templates.ListVersionsAsync(request.Id, context, ct);
                var current = versions.Count == 0 ? 0 : versions.Max(v => v.VersionNo);
                if (current != request.ExpectedVersionNo.Value)
                    return Conflict(new { code = "template_version_stale", current });
            }

            // SAVING THE VIEW KEEPS THE VIEW. A new template inherits the design it was saved from —
            // bands, columns, sorts, page setup and font — and only the parameters come from the client,
            // because the parameters are the one part the screen actually knows about.
            var layout = request.Id == 0
                ? await InheritLayoutAsync(request, context, ct)
                : request.Layout;

            var result = await _templates.SaveAsync(new ReportTemplateInput
            {
                Id = request.Id,
                ReportCode = request.ReportCode,
                Name = request.Name,
                NameEn = request.NameEn,
                Scope = request.Scope,
                TeamId = request.TeamId,
                CategoryId = request.CategoryId,
                IsDefault = request.IsDefault,
                Layout = layout,
                ChangeNote = request.ChangeNote,
            }, context, ct);

            // A refusal is a RESULT here, not an exception — "you may not create a platform template"
            // is an ordinary answer. 403 rather than 404: the caller already proved they can see the
            // report, so hiding the reason buys nothing and costs them the fix.
            return result.Success
                ? Ok(new { templateId = result.TemplateId, versionNo = result.VersionNo, unchanged = result.Unchanged })
                : StatusCode(StatusCodes.Status403Forbidden, new { diagnostics = Shape(result.Diagnostics) });
        }

        // The base layout with the caller's parameters laid over it, or the posted layout unchanged when
        // there is no base to inherit from.
        //
        // AUTHORIZED BY REUSE, not by a new rule: ListAsync already returns only the templates this caller
        // may see, scope and ownership applied. Membership in that list is the permission. Writing a fresh
        // check here would put the template visibility rules in a second place, which is how two answers to
        // one question start to disagree.
        private async Task<ReportLayout> InheritLayoutAsync(SaveLayoutRequest request,
            BusinessContext context, CancellationToken ct)
        {
            if (request.BaseTemplateId is not > 0) return request.Layout;

            var visible = await _templates.ListAsync(request.ReportCode, context, ct);
            if (visible.All(t => t.Id != request.BaseTemplateId.Value)) return request.Layout;

            var row = await _db.ReportTemplates.AsNoTracking()
                .Where(t => t.Id == request.BaseTemplateId.Value && t.DeletedAt == null)
                .Select(t => new { t.CurrentVersionNo })
                .FirstOrDefaultAsync(ct);
            if (row is null) return request.Layout;

            var json = await _db.ReportTemplateVersions.AsNoTracking()
                .Where(v => v.TemplateId == request.BaseTemplateId.Value && v.VersionNo == row.CurrentVersionNo)
                .Select(v => v.LayoutJson)
                .FirstOrDefaultAsync(ct);

            // An unreadable base is not a reason to refuse the save; it is a reason to save what was posted.
            var baseLayout = ReportLayoutJson.Deserialize(json);
            if (baseLayout is null) return request.Layout;

            // THE PARAMETERS ARE THE CLIENT'S, everything else is the base's. The screen knows what was
            // typed into the filter boxes and nothing else about the design, so that is exactly the slice
            // it is allowed to contribute.
            return new ReportLayout
            {
                VisibleColumns = baseLayout.VisibleColumns,
                Filters = baseLayout.Filters,
                Sorts = baseLayout.Sorts,
                Groupings = baseLayout.Groupings,
                ShowGrandTotals = baseLayout.ShowGrandTotals,
                PageSetup = baseLayout.PageSetup,
                Visual = baseLayout.Visual,
                TitleOverride = baseLayout.TitleOverride,
                TitleOverrideEn = baseLayout.TitleOverrideEn,
                Parameters = request.Layout.Parameters.Count > 0
                    ? request.Layout.Parameters
                    : baseLayout.Parameters,
            };
        }

        public sealed class ForkRequest
        {
            public ReportTemplateScope TargetScope { get; set; } = ReportTemplateScope.Personal;
            public string Name { get; set; } = "";
        }

        // Copy a template — typically a read-only Platform one — into a scope the caller may edit.
        // This is what "edit a platform template" actually means.
        [HttpPost("saved/{templateId:int}/fork")]
        public async Task<IActionResult> Fork(int templateId, [FromBody] ForkRequest request, CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();
            if (request is null || string.IsNullOrWhiteSpace(request.Name)) return BadRequest();

            // Forking READS the source template and writes a copy, so View on the source is the honest
            // requirement — a platform template is deliberately forkable by anyone who may run it.
            var (allowed, _, _) = await AuthorizeTemplateAsync(templateId, ReportAccessLevel.View, context, ct);
            if (!allowed) return NotFound();

            var result = await _templates.ForkAsync(templateId, request.TargetScope, request.Name, context, ct);

            return result.Success
                ? Ok(new { templateId = result.TemplateId, versionNo = result.VersionNo })
                : StatusCode(StatusCodes.Status403Forbidden, new { diagnostics = Shape(result.Diagnostics) });
        }

        [HttpPost("saved/{templateId:int}/default")]
        public async Task<IActionResult> SetDefault(int templateId, CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();

            var (allowed, _, _) = await AuthorizeTemplateAsync(templateId, ReportAccessLevel.Edit, context, ct);
            if (!allowed) return NotFound();

            return await _templates.SetDefaultAsync(templateId, context, ct)
                ? NoContent()
                : NotFound();
        }

        // SOFT delete, decided by the service. Reporting follows the parallel team's module convention
        // (DeletedAt) rather than the hypermarket track's reversal rule: a layout is not a financial
        // fact, and a deleted layout must stay resolvable so an archived artifact that names it can
        // still be explained.
        [HttpDelete("saved/{templateId:int}")]
        public async Task<IActionResult> DeleteLayout(int templateId, CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();

            // Manage, not Edit: deleting somebody's layout is the strongest act available on a template.
            var (allowed, _, _) = await AuthorizeTemplateAsync(templateId, ReportAccessLevel.Manage, context, ct);
            if (!allowed) return NotFound();

            return await _templates.DeleteAsync(templateId, context, ct)
                ? NoContent()
                : NotFound();
        }

        // =========================================================================================
        // FAVOURITES
        // =========================================================================================

        public sealed class FavoriteRequest
        {
            public string ReportCode { get; set; } = "";
            public int? TemplateId { get; set; }
        }

        // IDEMPOTENT. Favouriting twice is one row and answers 200 both times — a double-click, a
        // retried request and a stale tab must not produce a duplicate or an error the user has to
        // interpret.
        [HttpPost("favorites")]
        public async Task<IActionResult> AddFavorite([FromBody] FavoriteRequest request, CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();
            if (request is null || string.IsNullOrWhiteSpace(request.ReportCode)) return BadRequest();

            // A FAVOURITE IS STILL A GATED WRITE. Without this, favouriting would be a way to confirm
            // that a report code exists — and to leave a row referencing it — for a caller who may not
            // see it. It is per-user state, but the reference is to a report.
            var (allowed, _) = await AuthorizeAsync(request.ReportCode, ReportAccessLevel.View, context, ct);
            if (!allowed) return NotFound();

            var id = await _library.AddFavoriteAsync(request.ReportCode, request.TemplateId, context, ct);
            return Ok(new { id });
        }

        // DELETE with a body is legal but awkward for clients, so the two identifiers ride the query
        // string. It is still a DELETE: the verb describes the effect, not the payload shape.
        [HttpDelete("favorites")]
        public async Task<IActionResult> RemoveFavorite(string reportCode, int? templateId, CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(reportCode)) return BadRequest();

            var (allowed, _) = await AuthorizeAsync(reportCode, ReportAccessLevel.View, context, ct);
            if (!allowed) return NotFound();

            // Idempotent in the other direction too: removing something already gone is NoContent, not
            // 404. The caller's intent — "this is not a favourite of mine" — is satisfied either way.
            await _library.RemoveFavoriteAsync(reportCode, templateId, context, ct);
            return NoContent();
        }

        public sealed class ReorderRequest
        {
            public List<int> OrderedIds { get; set; } = new();
        }

        // Reorder is scoped to the CALLER's own favourites by the service: ids belonging to somebody else
        // are not found for this employee and are ignored, so a crafted list cannot reorder — or enumerate —
        // another person's bar.
        //
        // THAT ALONE WAS NOT ENOUGH, and the analyzer was right to say so. When TAB 1 declared
        // IReportAuthorizationService an authority they recorded, precisely, that this endpoint and
        // RemoveFavorite "authorize by row ownership … and never call this service, so they are NOT credited
        // by this addition and remain visible to CBA001. That is the correct outcome."
        //
        // The fix is to make the authorization real rather than to make the diagnostic quiet: every id is
        // resolved against the caller's OWN favourites and the report behind it is authorized through the
        // declared seam. Ids that resolve to nothing, or to a report the caller may no longer open, are
        // dropped rather than reordered. So a crafted list cannot move a row the caller could not otherwise
        // see, and the endpoint reaches the authority because it genuinely asks it.
        [HttpPost("favorites/reorder")]
        public async Task<IActionResult> ReorderFavorites([FromBody] ReorderRequest request, CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();
            if (request is null || request.OrderedIds.Count == 0) return BadRequest();

            // GetFavoritesAsync is already permission-filtered — it hides a favourite whose report permission
            // was revoked. Starting from it means a foreign id simply has no match.
            var mine = await _library.GetFavoritesAsync(context, ct);

            var ordered = new List<int>();
            foreach (var id in request.OrderedIds)
            {
                var favorite = mine.FirstOrDefault(f => f.Id == id);
                if (favorite?.Definition is null) continue;

                var decision = await _authorization.AuthorizeReportAsync(
                    favorite.Definition, ReportAccessLevel.View, context, ct);

                if (decision.Allowed) ordered.Add(id);
            }

            if (ordered.Count == 0) return BadRequest();

            return await _library.ReorderFavoritesAsync(ordered, context, ct)
                ? NoContent()
                : BadRequest();
        }

        // =========================================================================================
        private static object Shape(IReadOnlyList<ReportDiagnostic> diagnostics) =>
            diagnostics.Select(d => new { code = d.Code, message = d.Message, field = d.Field });

        // =====================================================================================
        // DERIVED DATASETS - a company's own narrowings of the code-authored datasets.
        //
        // TWO GATES, AND BOTH ARE NEEDED because they answer different questions:
        //
        //   reporting.datasets.author    may this caller SHAPE what the company is offered?
        //   the parent's own report gate may this caller SEE the data being shaped?
        //
        // Neither implies the other. An admin without accounting access may not mint an accounting
        // dataset even though the derivation would grant them nothing at run time - the catalogue would
        // still have gained an entry describing data they cannot see, and a catalogue is information.
        // An accountant without the author key may run everything and define nothing.
        // =====================================================================================

        [HttpGet("datasets/derived")]
        public async Task<IActionResult> ListDerived(CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();

            if (!(await _authorization.AuthorizeDatasetAuthoringAsync(context, ct)).Allowed)
                return NotFound();

            var rows = await _derived.ListAsync(context, ct);
            return Ok(rows.Select(r => new
            {
                datasetCode = r.DatasetCode,
                parentDatasetCode = r.ParentDatasetCode,
                titleAr = r.TitleAr,
                titleEn = r.TitleEn,
                updatedAt = r.UpdatedAt ?? r.CreatedAt,
            }));
        }

        [HttpPost("datasets/derived")]
        public async Task<IActionResult> SaveDerived([FromBody] ReportDerivedDatasetSpec spec,
            CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();

            if (spec is null) return BadRequest(new { errors = new[] { "No specification was supplied." } });

            if (!(await _authorization.AuthorizeDatasetAuthoringAsync(context, ct)).Allowed)
                return NotFound();

            // THE PARENT'S OWN GATE, called HERE rather than left to the store.
            //
            // The store resolves the parent through the permission-filtered list and would refuse anyway,
            // so this is not the only check - it is the VISIBLE one. CBA001 does not descend into services
            // to discover an authority call, and it is right not to: a rule found by descent is a rule no
            // reader of this endpoint can see and any other caller can bypass.
            var parent = await ResolveParentAsync(spec.ParentDatasetCode, context, ct);
            if (parent is null) return NotFound();

            var result = await _derived.SaveAsync(spec, context, ct);

            // A refusal is a RESULT with reasons, not an exception. The author needs to know which rule
            // they crossed to fix it - the same contract the Studio's own save endpoint keeps.
            return result.Ok
                ? Ok(new { datasetCode = result.Definition!.DatasetCode })
                : StatusCode(StatusCodes.Status403Forbidden, new { errors = result.Errors });
        }

        [HttpDelete("datasets/derived/{datasetCode}")]
        public async Task<IActionResult> DeleteDerived(string datasetCode, CancellationToken ct)
        {
            var context = await ResolveAsync(ct);
            if (context is null) return Unauthorized();

            if (!(await _authorization.AuthorizeDatasetAuthoringAsync(context, ct)).Allowed)
                return NotFound();

            // The parent is resolved FROM THE STORED ROW, never from the request - the same rule the
            // template-id endpoints above keep. A code from the wire must not be able to name one
            // derivation while the gate clears another.
            var row = (await _derived.ListAsync(context, ct))
                .FirstOrDefault(r => string.Equals(r.DatasetCode, datasetCode, StringComparison.Ordinal));
            if (row is null) return NotFound();

            if (await ResolveParentAsync(row.ParentDatasetCode, context, ct) is null) return NotFound();

            return await _derived.DeleteAsync(datasetCode, context, ct) ? Ok(new { ok = true }) : NotFound();
        }

        // The parent, if this caller may both SEE it and RUN it. Null covers "no such dataset", "not
        // permitted" and "not a code-authored dataset" with one answer, so none of these endpoints can be
        // used to enumerate the catalogue.
        private async Task<ReportDefinition?> ResolveParentAsync(string? parentCode,
            BusinessContext context, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(parentCode)) return null;

            var visible = await _datasets.ListForStudioAsync(context, ct);
            var dataset = visible.FirstOrDefault(d =>
                string.Equals(d.DatasetCode, parentCode, StringComparison.Ordinal));
            if (dataset is null) return null;

            // The dataset's own report, through the platform's report gate - the authority call CBA001
            // requires to be visible in a mutating endpoint's own call graph.
            var definition = await _reports.DescribeAsync(dataset.DataSourceKey, ct);
            if (definition is null) return null;

            var decision = await _authorization.AuthorizeReportAsync(
                definition, ReportAccessLevel.Run, context, ct);

            return decision.Allowed ? definition : null;
        }

    }
}
