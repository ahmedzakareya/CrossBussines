using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // REPORT STUDIO V1 — THE PRODUCT LAYER, AND ONLY THE PRODUCT LAYER.
    //
    // Everything a builder needs already existed before this file: the dataset catalogue and its
    // permission-filtered ListForStudioAsync, the field sensitivity model, the permission evaluator, an engine
    // that already accepts VisibleColumns/Filters/Sorts/MaxRows, the CSV and XLSX exporters, and — the piece
    // most likely to have been rebuilt by mistake — a SAVED REPORT CONTRACT.
    //
    //     ReportLayout ALREADY persists VisibleColumns, Filters, Sorts, Groupings, Parameters and PageSetup,
    //     and ReportTemplateService.SaveAsync already stores it company-scoped, owned and versioned.
    //
    // So Studio saves a ReportTemplate. It does NOT introduce a second saved-report table, a second save
    // service or a second execution path. A Studio report and a hand-written report are the same object run
    // through the same pipeline, which is the single most important property of this increment: the builder
    // composes what the platform already allows rather than reaching past it.
    //
    // ────────────────────────────────────────────────────────────────────────────────────────────
    // WHAT THIS FILE ADDS, AND WHY IT HAS TO EXIST: THE DRAFT IS UNTRUSTED INPUT.
    //
    // Every other report in the product has a column set an author fixed in code. Studio is the first surface
    // where the CALLER names the columns, the filters and the sorts — so it is the first surface where a
    // request can ask for something it may not have.
    //
    // A discovery that shaped this file: ReportColumn.Internal is enforced by the shaper, but ToColumn() maps
    // only Sensitivity.Never onto it. Confidential and Restricted fields therefore travel as ORDINARY columns
    // at run time, and ReportDatasetDefinition.VisibleFields — the per-caller gate — is called by nothing.
    // That is harmless for a fixed report (its author chose the columns) and NOT harmless for a builder.
    //
    // So this service is the authority on three questions, and every Studio path goes through it:
    //
    //     1. which datasets may this caller build over?      → ListForStudioAsync (already gated)
    //     2. which FIELDS of it may this caller see?         → VisibleFields(resolved permissions)
    //     3. is this draft legal?                            → Validate: every column, filter field and sort
    //                                                          field must be in (2), every operator must be
    //                                                          legal for its field, and anything else is
    //                                                          REFUSED — not silently dropped.
    //
    // REFUSED, NOT DROPPED, is deliberate. Silently dropping a forbidden column would let a caller probe the
    // schema by watching which columns come back, and would let a saved report quietly lose a column its owner
    // believes is there. A refusal says what was wrong with the request without saying what the field contains.
    //
    // NO SQL AND NO EXPRESSION EVER CROSSES THIS BOUNDARY. A filter is (field, operator, values[]) — three
    // pieces of structured data validated against the dataset — and it reaches the source as a ReportFilter
    // object. There is no predicate string, no free-text WHERE and no user expression anywhere in the contract.
    // ============================================================================================

    // ---- what the screen renders ----------------------------------------------------------------
    public sealed class StudioDatasetOption
    {
        public required string DatasetCode { get; init; }
        public required string ReportCode { get; init; }   // the definition the engine actually runs
        public required string Title { get; init; }
        public string? Description { get; init; }
        public required string Module { get; init; }
        public int FieldCount { get; init; }
    }

    public sealed class StudioFieldOption
    {
        public required string Key { get; init; }
        public required string Title { get; init; }
        public ReportFieldType Type { get; init; }
        public bool Filterable { get; init; }
        public bool Sortable { get; init; }
        public bool Groupable { get; init; }
        public bool SelectedByDefault { get; init; }

        // The operators this field legally accepts, already narrowed by the dataset. The screen renders exactly
        // these, so a user cannot pick an operator the validator will refuse — the UI and the gate agree because
        // they read the same source.
        public IReadOnlyList<StudioOperatorOption> Operators { get; init; } = Array.Empty<StudioOperatorOption>();
    }

    public sealed class StudioOperatorOption
    {
        public required ReportFilterOperator Operator { get; init; }
        public required string Label { get; init; }

        // Between and the two null tests need a different input shape: two boxes, or none at all.
        public int ValueCount { get; init; } = 1;
    }

    // ---- §9: THE PARAMETER LIMITATION, CLOSED ---------------------------------------------------
    //
    // V1 shipped a builder that could not set a dataset parameter, so every report ran on the dataset's own
    // defaults — which for the module datasets means month-start..today. §9 records the consequence honestly:
    // "the user cannot report on last quarter". That is not a missing convenience, it is a report tool that
    // cannot answer the most ordinary question asked of one.
    //
    // The fix is NOT a date box bolted onto the screen. It is to surface the parameters the DATASET ALREADY
    // DECLARES — key, type, required, default, closed option set, bounds — and let the platform's own
    // IReportParameterBinder validate the values. So there is no second parameter model, no hardcoded period,
    // and a dataset that later adds a parameter gains an input with no Studio change.
    //
    // SystemSupplied descriptors (CompanyId, EmployeeId, Culture, Now) are ABSENT from this list, exactly as
    // ReportParameterDescriptor's own comment requires: the UI must not render an input for them, and a value
    // supplied for one is ignored rather than honoured. That is what keeps "no browser companyId authority"
    // true at the parameter layer too.
    public sealed class StudioParameterOption
    {
        public required string Key { get; init; }
        public required string Title { get; init; }
        public ReportFieldType Type { get; init; }
        public bool Required { get; init; }
        public bool AllowMultiple { get; init; }
        public string? DefaultValue { get; init; }
        public string? Help { get; init; }
        public string? MinValue { get; init; }
        public string? MaxValue { get; init; }

        // A closed value set, when the descriptor declares one. Rendered as a select, and enforced by the
        // binder regardless of what the browser sends.
        public IReadOnlyList<StudioParameterChoice> Options { get; init; } = Array.Empty<StudioParameterChoice>();
    }

    public sealed class StudioParameterChoice
    {
        public required string Value { get; init; }
        public required string Label { get; init; }
    }

    public sealed class StudioSavedReport
    {
        public required int TemplateId { get; init; }
        public required string Name { get; init; }
        public required string DatasetCode { get; init; }
        public required string ReportCode { get; init; }
        public DateTime? UpdatedAt { get; init; }
        public int VersionNo { get; init; }
    }

    public sealed class ReportStudioModel
    {
        public IReadOnlyList<StudioDatasetOption> Datasets { get; init; } = Array.Empty<StudioDatasetOption>();
        public IReadOnlyList<StudioSavedReport> Saved { get; init; } = Array.Empty<StudioSavedReport>();
        public bool Arabic { get; init; }

        // No dataset the caller may build over. A real, renderable state — not an error and not an empty canvas
        // pretending to work.
        public bool HasNoDatasets => Datasets.Count == 0;
    }

    // ---- what the browser sends back -------------------------------------------------------------
    //
    // Plain data. Note what is ABSENT: no SQL, no expression, no companyId. The tenant is never accepted from
    // a request — it comes from the resolved BusinessContext on the server, every time.
    public sealed class StudioFilterDraft
    {
        public string Field { get; set; } = "";
        public ReportFilterOperator Operator { get; set; } = ReportFilterOperator.Equals;
        public List<string?> Values { get; set; } = new();
    }

    public sealed class StudioSortDraft
    {
        public string Field { get; set; } = "";
        public bool Descending { get; set; }
    }

    public sealed class StudioDraft
    {
        public int TemplateId { get; set; }               // 0 = a new report
        public string DatasetCode { get; set; } = "";

        // BOTH STORED NAMES, not a resolved one. The designer edits the record, so it has to round-trip
        // exactly what the record holds: hand it one display name and the save writes that name back into
        // whichever column it came from, quietly losing the other language.
        public string Name { get; set; } = "";
        public string NameEn { get; set; } = "";
        public List<string> Columns { get; set; } = new();
        public List<StudioFilterDraft> Filters { get; set; } = new();
        public List<StudioSortDraft> Sorts { get; set; } = new();
        public int PageSize { get; set; } = 50;

        // §9. Key → text value, in the same notation the binder already accepts. Text rather than typed so a
        // browser value goes through EXACTLY the binder a default goes through, and cannot bypass its bounds,
        // its option set or its type check by arriving pre-parsed.
        public Dictionary<string, string?> Parameters { get; set; } = new();

        // §14. The positioned design. Null for a V1 column-list report, which is still a legitimate thing to
        // build — the designer is an addition, not a replacement.
        public ReportVisualLayout? Visual { get; set; }

        // THE STORED PAGE, carried separately from Visual — because the save path used to derive the page
        // from Visual alone, and a template whose Visual is null (one designed before the visual designer,
        // or one whose visual failed validation) therefore opened on hardcoded defaults and SAVED them back
        // over the real ones. Paper, orientation, all four margins, RTL and the document font were lost by
        // opening a report and pressing save without touching anything.
        //
        // Nullable on purpose: null means "this draft is new and has no stored page", which is the only case
        // where falling back to a default is correct.
        public ReportPageSetup? PageSetup { get; set; }
    }

    public sealed class StudioValidation
    {
        public bool Ok => Errors.Count == 0;
        public List<string> Errors { get; } = new();

        // Filled only when Ok. These are the platform's own structures, ready for the engine.
        public ReportDefinition? Definition { get; set; }
        public IReportDatasetDefinition? Dataset { get; set; }
        public IReadOnlyList<string> Columns { get; set; } = Array.Empty<string>();
        public IReadOnlyList<ReportFilter> Filters { get; set; } = Array.Empty<ReportFilter>();
        public IReadOnlyList<ReportSort> Sorts { get; set; } = Array.Empty<ReportSort>();
        public int PageSize { get; set; } = 50;

        public IReadOnlyDictionary<string, string?> Parameters { get; set; } = new Dictionary<string, string?>();

        // The layout AFTER validation. On a strict submission this is the layout as supplied (or the whole
        // submission failed); it is never a partially-repaired one, because silently moving somebody's elements
        // is worse than telling them what is wrong.
        public ReportVisualLayout? Visual { get; set; }
    }

    public interface IReportStudioService
    {
        Task<ReportStudioModel> BuildAsync(CancellationToken cancellationToken = default);

        // The fields this caller may use on one dataset. Returns empty when the dataset is not theirs — the
        // same "no such thing, as far as you are concerned" answer the Viewer gives.
        Task<IReadOnlyList<StudioFieldOption>> FieldsAsync(string datasetCode,
            CancellationToken cancellationToken = default);

        // §9. The user-settable parameters this dataset declares. Empty when the dataset is not theirs — the
        // same answer FieldsAsync gives, for the same reason.
        Task<IReadOnlyList<StudioParameterOption>> ParametersAsync(string datasetCode,
            CancellationToken cancellationToken = default);

        // The images this caller may place. Company-scoped by IReportAssetService; a foreign id is not in it.
        Task<IReadOnlyList<ReportAssetSummary>> AssetsAsync(CancellationToken cancellationToken = default);

        // THE GATE. Everything below calls it; nothing bypasses it.
        Task<StudioValidation> ValidateAsync(StudioDraft draft, CancellationToken cancellationToken = default);

        // The report definition behind a dataset the caller may use, or null.
        //
        // Exists so the CONTROLLER can run the platform's own report gate at the boundary before it does
        // anything — CBA001 requires an authorization decision the analyzer can SEE in an endpoint's call
        // graph, and it is right to: a rule found only by descending into a service is a rule nobody declared
        // and any other caller can bypass. The controller therefore calls IReportAuthorizationService itself,
        // and this method is what gives it something to authorize.
        Task<ReportDefinition?> ResolveDefinitionAsync(string datasetCode,
            CancellationToken cancellationToken = default);

        Task<ReportResult> RunAsync(StudioDraft draft, ReportOutputFormat format, bool preview,
            CancellationToken cancellationToken = default);

        Task<ReportTemplateSaveResult> SaveAsync(StudioDraft draft, CancellationToken cancellationToken = default);

        // Reopen: the stored layout, re-validated against what the caller may see TODAY.
        Task<StudioDraft?> OpenAsync(int templateId, CancellationToken cancellationToken = default);
    }

    public sealed class ReportStudioService : IReportStudioService
    {
        // A preview is capped hard. The point of a preview is "is this the report I meant", which 200 rows
        // answers as well as 20 000 and a great deal faster.
        public const int PreviewRows = 200;
        public const int MaxPageSize = 5_000;

        private readonly IReportDatasetRegistry _datasets;
        private readonly IReportCatalog _catalog;
        private readonly IReportPermissionEvaluator _permissions;
        private readonly IReportTemplateService _templates;
        private readonly IReportService _reports;
        private readonly IBusinessContextAccessor _contexts;
        private readonly IReportVisualLayoutValidator _visual;
        private readonly IReportAssetService _assets;

        public ReportStudioService(
            IReportDatasetRegistry datasets,
            IReportCatalog catalog,
            IReportPermissionEvaluator permissions,
            IReportTemplateService templates,
            IReportService reports,
            IBusinessContextAccessor contexts,
            IReportVisualLayoutValidator visual,
            IReportAssetService assets)
        {
            _visual = visual;
            _assets = assets;
            _datasets = datasets;
            _catalog = catalog;
            _permissions = permissions;
            _templates = templates;
            _reports = reports;
            _contexts = contexts;
        }

        private static bool Arabic =>
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

        // ----------------------------------------------------------------------------------------
        // THE DATASET → REPORT LINK.
        //
        // The engine runs a REPORT, not a dataset, and the two are joined by DataSourceKey — the same rule
        // ReportsCenterPresenter already uses. Matching on code would break the platform's own pilot, whose
        // dataset is "Platform.BusinessEvents.Log" while its report is "Platform.BusinessEventLog".
        // ----------------------------------------------------------------------------------------
        // The per-caller field gate. ReportDatasetDefinition.VisibleFields is declared on the CONCRETE type, so a
        // registry that hands back the interface cannot call it — IsFieldVisible is the same rule as a static, and
        // using it keeps ONE definition of "may this caller see this field" rather than a second copy here.
        private static IEnumerable<ReportDatasetField> Visible(
            IReportDatasetDefinition dataset, IReadOnlySet<string> held) =>
            dataset.Fields.Where(f => ReportDatasetDefinition.IsFieldVisible(f, held));

        private ReportDefinition? DefinitionFor(IReportDatasetDefinition dataset) =>
            _catalog.GetDefinitions().FirstOrDefault(d =>
                string.Equals(d.DataSourceKey, dataset.DataSourceKey, StringComparison.Ordinal));

        // The caller's permission set, resolved ONCE per operation. Field visibility is decided against a
        // resolved set rather than an async call per field — VisibleFields is deliberately synchronous.
        private async Task<IReadOnlySet<string>> HeldAsync(IReportDatasetDefinition dataset,
            BusinessContext context, CancellationToken cancellationToken)
        {
            var keys = dataset.Fields
                .Select(f => f.RequiredPermissionKey)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k!)
                .Distinct(StringComparer.Ordinal);

            var held = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in keys)
                if (await _permissions.HasPermissionAsync(key, context, cancellationToken))
                    held.Add(key);

            return held;
        }

        public async Task<ReportStudioModel> BuildAsync(CancellationToken cancellationToken = default)
        {
            var context = await _contexts.TryGetCurrentAsync(cancellationToken);

            // Fail closed and quietly: an unresolved company builds over nothing.
            if (context is not { CompanyId: > 0 }) return new ReportStudioModel { Arabic = Arabic };

            var datasets = await _datasets.ListForStudioAsync(context, cancellationToken);
            var options = new List<StudioDatasetOption>();

            foreach (var dataset in datasets)
            {
                var definition = DefinitionFor(dataset);

                // A dataset with no runnable report is not offered. Showing it would produce a builder whose
                // Run button cannot work.
                if (definition is null) continue;

                var held = await HeldAsync(dataset, context, cancellationToken);

                options.Add(new StudioDatasetOption
                {
                    DatasetCode = dataset.DatasetCode,
                    ReportCode = definition.Code,
                    Title = Arabic ? dataset.TitleAr : DisplayName.Or(dataset.TitleEn, dataset.TitleAr),
                    Description = Arabic ? dataset.DescriptionAr : DisplayName.Or(dataset.DescriptionEn, dataset.DescriptionAr),
                    Module = dataset.Module,
                    FieldCount = Visible(dataset, held).Count(),
                });
            }

            // Saved Studio reports = the caller's own templates on those reports, listed through the template
            // service so its scope and ownership rules decide what is visible.
            var saved = new List<StudioSavedReport>();
            foreach (var option in options)
            {
                foreach (var template in await _templates.ListAsync(option.ReportCode, context, cancellationToken))
                {
                    if (template.Scope is not (ReportTemplateScope.Personal or ReportTemplateScope.Company)) continue;

                    saved.Add(new StudioSavedReport
                    {
                        TemplateId = template.Id,
                        Name = Arabic ? template.Name : (template.NameEn ?? template.Name),
                        DatasetCode = option.DatasetCode,
                        ReportCode = option.ReportCode,
                        UpdatedAt = template.UpdatedAt,
                        VersionNo = template.CurrentVersionNo,
                    });
                }
            }

            return new ReportStudioModel
            {
                Datasets = options,
                Saved = saved.OrderByDescending(s => s.UpdatedAt).ToList(),
                Arabic = Arabic,
            };
        }

        public async Task<ReportDefinition?> ResolveDefinitionAsync(string datasetCode,
            CancellationToken cancellationToken = default)
        {
            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context is not { CompanyId: > 0 }) return null;

            var dataset = await PermittedDatasetAsync(datasetCode, context, cancellationToken);
            return dataset is null ? null : DefinitionFor(dataset);
        }

        public async Task<IReadOnlyList<StudioFieldOption>> FieldsAsync(string datasetCode,
            CancellationToken cancellationToken = default)
        {
            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context is not { CompanyId: > 0 }) return Array.Empty<StudioFieldOption>();

            var dataset = await PermittedDatasetAsync(datasetCode, context, cancellationToken);
            if (dataset is null) return Array.Empty<StudioFieldOption>();

            var held = await HeldAsync(dataset, context, cancellationToken);
            bool arabic = Arabic;

            // VisibleFields is the gate. A Confidential field the caller lacks is ABSENT from this list, not
            // greyed out — a greyed entry discloses that the field exists and what it is called.
            return Visible(dataset, held)
                .Where(f => !f.IsCalculated || f.Filterable || f.Sortable || true)
                .Select(f => new StudioFieldOption
                {
                    Key = f.Key,
                    Title = arabic ? f.TitleAr : DisplayName.Or(f.TitleEn, f.TitleAr),
                    Type = f.Type,
                    Filterable = f.Filterable,
                    Sortable = f.Sortable,
                    Groupable = f.Groupable,
                    SelectedByDefault = f.VisibleByDefault,
                    Operators = f.Filterable ? OperatorsFor(f, arabic) : Array.Empty<StudioOperatorOption>(),
                })
                .ToList();
        }

        public async Task<IReadOnlyList<StudioParameterOption>> ParametersAsync(string datasetCode,
            CancellationToken cancellationToken = default)
        {
            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context is not { CompanyId: > 0 }) return Array.Empty<StudioParameterOption>();

            var dataset = await PermittedDatasetAsync(datasetCode, context, cancellationToken);
            if (dataset is null) return Array.Empty<StudioParameterOption>();

            bool arabic = Arabic;

            return dataset.Parameters
                .Where(pd => !pd.SystemSupplied)   // see StudioParameterOption's header
                .Select(pd => new StudioParameterOption
                {
                    Key = pd.Key,
                    Title = arabic ? pd.TitleAr : DisplayName.Or(pd.TitleEn, pd.TitleAr),
                    Type = pd.Type,
                    Required = pd.Required,
                    AllowMultiple = pd.AllowMultiple,
                    DefaultValue = pd.DefaultValue,
                    Help = arabic ? pd.HelpTextAr : pd.HelpTextEn,
                    MinValue = pd.MinValue,
                    MaxValue = pd.MaxValue,
                    Options = pd.Options
                        .Select(o => new StudioParameterChoice
                        {
                            Value = o.Value,
                            Label = arabic ? o.LabelAr : DisplayName.Or(o.LabelEn, o.LabelAr),
                        })
                        .ToList(),
                })
                .ToList();
        }

        public async Task<IReadOnlyList<ReportAssetSummary>> AssetsAsync(
            CancellationToken cancellationToken = default)
        {
            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context is not { CompanyId: > 0 }) return Array.Empty<ReportAssetSummary>();
            return await _assets.ListAsync(context, cancellationToken);
        }

        // The V1 operator vocabulary, intersected with what the field legally allows. Between/In/StartsWith and
        // the null tests exist in the platform and are deliberately NOT offered yet — V1 ships the five shapes
        // the brief names, and an operator the screen cannot express is an operator the validator would refuse.
        private static readonly ReportFilterOperator[] V1Operators =
        {
            ReportFilterOperator.Equals,
            ReportFilterOperator.NotEquals,
            ReportFilterOperator.Contains,
            ReportFilterOperator.GreaterOrEqual,
            ReportFilterOperator.LessOrEqual,
        };

        private static IReadOnlyList<StudioOperatorOption> OperatorsFor(ReportDatasetField field, bool arabic) =>
            V1Operators
                .Where(op => ReportDatasetOperators.IsAllowed(field, op))
                .Select(op => new StudioOperatorOption { Operator = op, Label = Label(op, arabic) })
                .ToList();

        private static string Label(ReportFilterOperator op, bool arabic) => op switch
        {
            ReportFilterOperator.Equals => arabic ? "يساوي" : "equals",
            ReportFilterOperator.NotEquals => arabic ? "لا يساوي" : "not equals",
            ReportFilterOperator.Contains => arabic ? "يحتوي" : "contains",
            ReportFilterOperator.GreaterOrEqual => arabic ? "أكبر من أو يساوي" : "at least",
            ReportFilterOperator.LessOrEqual => arabic ? "أصغر من أو يساوي" : "at most",
            _ => op.ToString(),
        };

        // A dataset the caller may build over, or null. Goes through ListForStudioAsync rather than Resolve so
        // the permission gate is the SAME one the picker used — resolving directly would answer for a dataset
        // the picker never offered.
        private async Task<IReportDatasetDefinition?> PermittedDatasetAsync(string? datasetCode,
            BusinessContext context, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(datasetCode)) return null;

            var permitted = await _datasets.ListForStudioAsync(context, cancellationToken);
            return permitted.FirstOrDefault(d =>
                string.Equals(d.DatasetCode, datasetCode, StringComparison.Ordinal));
        }

        // ========================================================================================
        // THE GATE
        // ========================================================================================
        public async Task<StudioValidation> ValidateAsync(StudioDraft draft,
            CancellationToken cancellationToken = default)
        {
            var result = new StudioValidation();
            bool arabic = Arabic;

            if (draft is null)
            {
                result.Errors.Add(arabic ? "لا توجد بيانات." : "No draft was supplied.");
                return result;
            }

            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context is not { CompanyId: > 0 })
            {
                result.Errors.Add(arabic ? "لا توجد شركة محدَّدة لهذا الطلب." : "No company is resolved for this request.");
                return result;
            }

            var dataset = await PermittedDatasetAsync(draft.DatasetCode, context, cancellationToken);
            if (dataset is null)
            {
                // Deliberately the same message whether the dataset does not exist or is not theirs. Telling
                // them apart would make this endpoint a catalogue oracle.
                result.Errors.Add(arabic ? "مجموعة البيانات غير متاحة." : "That data set is not available to you.");
                return result;
            }

            var definition = DefinitionFor(dataset);
            if (definition is null)
            {
                result.Errors.Add(arabic ? "لا يوجد تقرير قابل للتشغيل لهذه المجموعة."
                                         : "That data set has no runnable report.");
                return result;
            }

            // THE PERMITTED FIELD SET. Everything below is checked against this and nothing else.
            var held = await HeldAsync(dataset, context, cancellationToken);
            var permitted = Visible(dataset, held).ToDictionary(f => f.Key, StringComparer.Ordinal);

            // ---- columns -------------------------------------------------------------------------
            var columns = new List<string>();
            foreach (var key in draft.Columns ?? new List<string>())
            {
                if (!permitted.TryGetValue(key, out _))
                {
                    // One message for "no such field" and for "not yours" — see above.
                    result.Errors.Add(arabic
                        ? $"Field «{key}» is not available in this data set."
                        : $"Field '{key}' is not available on this data set.");
                    continue;
                }
                if (!columns.Contains(key, StringComparer.Ordinal)) columns.Add(key);
            }

            // A DESIGNED report does not need a column list: its Detail band names the fields it draws, and
            // requiring a redundant second list would make the designer refuse a document it can render
            // perfectly well. A V1 column-list report still needs one.
            if (columns.Count == 0 && draft.Visual is null)
                result.Errors.Add(arabic ? "اختر عمودًا واحدًا على الأقل." : "Choose at least one column.");

            // ---- filters -------------------------------------------------------------------------
            var filters = new List<ReportFilter>();
            foreach (var filter in draft.Filters ?? new List<StudioFilterDraft>())
            {
                if (!permitted.TryGetValue(filter.Field, out var field))
                {
                    result.Errors.Add(arabic
                        ? $"You cannot filter on «{filter.Field}»."
                        : $"Cannot filter on '{filter.Field}'.");
                    continue;
                }

                if (!field.Filterable)
                {
                    result.Errors.Add(arabic
                        ? $"الحقل «{field.TitleAr}» غير قابل للترشيح."
                        : $"Field '{field.TitleEn}' is not filterable.");
                    continue;
                }

                // The operator must be legal for THIS field, per the dataset's own narrowing. A free-text
                // search over a JSON payload, say, is refused because the field declares it so.
                if (!V1Operators.Contains(filter.Operator) || !ReportDatasetOperators.IsAllowed(field, filter.Operator))
                {
                    result.Errors.Add(arabic
                        ? $"المُعامل غير مسموح على «{field.TitleAr}»."
                        : $"That operator is not allowed on '{field.TitleEn}'.");
                    continue;
                }

                var values = (filter.Values ?? new List<string?>())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .ToList();

                if (values.Count == 0)
                {
                    result.Errors.Add(arabic
                        ? $"أدخل قيمة للترشيح على «{field.TitleAr}»."
                        : $"Enter a value to filter '{field.TitleEn}'.");
                    continue;
                }

                // Constructed as a STRUCTURE. The value never becomes part of a predicate string — the shaper
                // and the data sources compare it as data.
                filters.Add(new ReportFilter
                {
                    Field = field.Key,
                    Operator = filter.Operator,
                    Values = new List<string?> { values[0] },
                });
            }

            // ---- sorts ---------------------------------------------------------------------------
            var sorts = new List<ReportSort>();
            foreach (var sort in draft.Sorts ?? new List<StudioSortDraft>())
            {
                if (!permitted.TryGetValue(sort.Field, out var field))
                {
                    result.Errors.Add(arabic
                        ? $"You cannot sort on «{sort.Field}»."
                        : $"Cannot sort on '{sort.Field}'.");
                    continue;
                }
                if (!field.Sortable)
                {
                    result.Errors.Add(arabic
                        ? $"الحقل «{field.TitleAr}» غير قابل للترتيب."
                        : $"Field '{field.TitleEn}' is not sortable.");
                    continue;
                }
                if (sorts.Any(s => string.Equals(s.Field, field.Key, StringComparison.Ordinal))) continue;

                sorts.Add(ReportSort.By(field.Key, sort.Descending));
            }

            // ---- page size -----------------------------------------------------------------------
            var pageSize = draft.PageSize <= 0 ? 50 : Math.Min(draft.PageSize, MaxPageSize);

            // ---- §9 parameters -------------------------------------------------------------------
            //
            // ONLY declared, non-system keys survive. An undeclared key is refused rather than passed on, so a
            // browser cannot smuggle a value into a data source by naming something the dataset never offered;
            // a SystemSupplied key is refused rather than ignored, so an attempt to set CompanyId is a visible
            // error instead of a silent no-op that leaves the caller believing it worked.
            //
            // The VALUES are not parsed here. IReportParameterBinder owns type, bounds and option-set checking
            // and runs inside the engine on every execution — checking them a second time here would be a
            // second, drifting copy of a rule the platform already owns.
            var declared = dataset.Parameters.ToDictionary(pd => pd.Key, StringComparer.Ordinal);
            var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);

            foreach (var (key, value) in draft.Parameters ?? new Dictionary<string, string?>())
            {
                if (string.IsNullOrWhiteSpace(value)) continue;   // "not set" — the descriptor's default applies

                if (!declared.TryGetValue(key, out var descriptor))
                {
                    result.Errors.Add(arabic
                        ? $"Parameter «{key}» is not defined for this data set."
                        : $"Parameter '{key}' is not declared on this data set.");
                    continue;
                }

                if (descriptor.SystemSupplied)
                {
                    result.Errors.Add(arabic
                        ? $"Parameter «{key}» is set by the system and cannot be overridden."
                        : $"Parameter '{key}' is supplied by the system and cannot be set.");
                    continue;
                }

                parameters[key] = value;
            }

            // ---- §14 the visual layout -----------------------------------------------------------
            //
            // STRICT here, because this is a submission: a rejected binding is reported, never quietly removed.
            // The permitted set handed in is the SAME `permitted` dictionary the columns were checked against,
            // which is what makes "a persisted layout can never bypass current field permissions" structural —
            // there is one permitted set per validation and every part of the draft is checked against it.
            ReportVisualLayout? visual = null;
            if (draft.Visual is not null)
            {
                var assetIds = await _assets.PermittedIdsAsync(context, cancellationToken);
                var check = _visual.Validate(draft.Visual, dataset,
                    permitted.Keys.ToHashSet(StringComparer.Ordinal), assetIds);

                if (!check.Ok) result.Errors.AddRange(check.Errors);
                else visual = draft.Visual;
            }

            if (result.Ok)
            {
                result.Definition = definition;
                result.Dataset = dataset;
                result.Columns = columns;
                result.Filters = filters;
                result.Sorts = sorts;
                result.PageSize = pageSize;
                result.Parameters = parameters;
                result.Visual = visual;
            }
            return result;
        }

        // ========================================================================================
        // RUN — preview, full run and export are ONE path with one parameter different.
        //
        // That is the property the brief asks for at §9 ("export uses same saved definition"): there is no
        // separate export builder that could drift from what the preview showed, and no way to export a column
        // the preview would have refused, because both re-validate the same draft.
        // ========================================================================================
        public async Task<ReportResult> RunAsync(StudioDraft draft, ReportOutputFormat format, bool preview,
            CancellationToken cancellationToken = default)
        {
            var validation = await ValidateAsync(draft, cancellationToken);
            if (!validation.Ok)
                return ReportResult.Failed(
                    validation.Definition?.Code ?? draft?.DatasetCode ?? "",
                    format,
                    validation.Errors.Select(e => ReportDiagnostic.Error("studio_invalid_draft", e)).ToList());

            // A FILTERED OR SORTED FIELD MUST BE FETCHED, even if the user did not choose to display it.
            //
            // Found by runtime verification, and it was the worst possible failure mode: a draft that showed
            // InvoiceNo and Total while filtering Status = Posted returned ZERO rows against a company with 146
            // posted invoices. The data source projects only the requested columns, so Status was absent from
            // every row, and the shaper's filter compared against a missing value and matched nothing. No error,
            // no warning — just an empty report that looked like an answer.
            //
            // The platform already solves exactly this for groupings: ReportEngine.ResolveRequestedColumns adds
            // the grouping fields to the fetch set for the same reason. This extends that rule to filters and
            // sorts rather than inventing a new one, and it is done HERE rather than in the engine because the
            // engine's behaviour is correct for every report whose columns an author fixed — Studio is the only
            // surface where the display set and the predicate set are chosen independently.
            //
            // The consequence is deliberate and visible: a field you filter on appears as a column. That is a
            // far better outcome than a silently empty page, and it shows the user what the filter acted on.
            var fetched = validation.Columns.ToList();
            foreach (var key in validation.Filters.Select(f => f.Field)
                                   .Concat(validation.Sorts.Select(s => s.Field)))
            {
                if (!fetched.Contains(key, StringComparer.Ordinal)) fetched.Add(key);
            }

            // A DESIGNED report fetches what its ELEMENTS bind to, not what a column list says. Without this a
            // document whose Detail band draws InvoiceNo and GrandTotal would render two empty boxes, because
            // the shaped view would carry only the V1 column list — the same "field not fetched" defect V1's
            // filter bug was, in a new disguise.
            if (validation.Visual is not null)
            {
                foreach (var key in VisualFieldKeys(validation.Visual))
                    if (!fetched.Contains(key, StringComparer.Ordinal)) fetched.Add(key);
            }

            var request = new ReportRequest
            {
                ReportCode = validation.Definition!.Code,
                Format = format,
                Kind = preview ? ReportRunKind.Preview : ReportRunKind.Full,
                VisibleColumns = fetched,
                Filters = validation.Filters,
                Sorts = validation.Sorts,
                Parameters = validation.Parameters,
                Visual = validation.Visual,
                MaxRows = preview ? PreviewRows : validation.PageSize,
                Archive = false,
            };

            // GenerateAsync authorizes the report again on its own account. Studio's validation is the field
            // gate; this is the report gate, and both run on every single execution — which is what "permissions
            // rechecked at run time" means in practice.
            return preview
                ? await _reports.PreviewAsync(request, cancellationToken)
                : await _reports.GenerateAsync(request, cancellationToken);
        }

        // Every dataset field a layout binds to, from any band: a field element, a summary's source, or a
        // table column. One place, so a new element kind that binds a field is handled by extending this and
        // not by hunting for fetch sites.
        internal static IEnumerable<string> VisualFieldKeys(ReportVisualLayout layout)
        {
            foreach (var band in layout.Bands)
            {
                if (!string.IsNullOrWhiteSpace(band.GroupFieldKey)) yield return band.GroupFieldKey!;

                foreach (var element in band.Elements)
                {
                    if (!string.IsNullOrWhiteSpace(element.FieldKey)) yield return element.FieldKey!;

                    foreach (var column in element.Columns)
                        if (!string.IsNullOrWhiteSpace(column.FieldKey)) yield return column.FieldKey!;
                }
            }
        }

        // ========================================================================================
        // SAVE — a validated draft becomes a ReportTemplate + ReportLayout. No new table, no SQL text.
        // ========================================================================================
        public async Task<ReportTemplateSaveResult> SaveAsync(StudioDraft draft,
            CancellationToken cancellationToken = default)
        {
            var validation = await ValidateAsync(draft, cancellationToken);
            if (!validation.Ok)
                return new ReportTemplateSaveResult
                {
                    Success = false,
                    Diagnostics = validation.Errors
                        .Select(e => ReportDiagnostic.Error("studio_invalid_draft", e)).ToList(),
                };

            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context is not { CompanyId: > 0 })
                return new ReportTemplateSaveResult { Success = false };

            var name = string.IsNullOrWhiteSpace(draft.Name)
                ? (Arabic ? "تقرير بدون اسم" : "Untitled report")
                : draft.Name.Trim();

            // NULL, not a copy of the Arabic name. `NameEn = name` used to run here, which is why the saved
            // layouts on the Viewer and the Reports Center read Arabic while the UI was in English: the
            // English column held an Arabic string, so there was nothing for the presenter to fall back to.
            // Null means "no English name yet", and the presenter's `NameEn ?? Name` then shows the Arabic
            // one - the same visible result, but recoverable the moment someone types an English name.
            var nameEn = string.IsNullOrWhiteSpace(draft.NameEn) ? null : draft.NameEn.Trim();

            // PERSONAL scope: a Studio draft is the author's until somebody promotes it. Promotion to Company is
            // an explicit, separately-authorized act (ReportTemplateService), not a side effect of saving.
            //
            // The company and the owner are set by SaveAsync from the CONTEXT — this input carries neither, and
            // there is no field on it that could.
            return await _templates.SaveAsync(new ReportTemplateInput
            {
                Id = draft.TemplateId,
                ReportCode = validation.Definition!.Code,
                Name = name,
                NameEn = nameEn,
                Scope = ReportTemplateScope.Personal,
                Layout = new ReportLayout
                {
                    VisibleColumns = validation.Columns,
                    Filters = validation.Filters,
                    Sorts = validation.Sorts,
                    Parameters = validation.Parameters,
                    ShowGrandTotals = true,

                    // THE PAGE SETUP WAS NEVER PERSISTED, and everything the designer's page toolbar sets was
                    // quietly lost with it: paper size, orientation, the four margins, RTL — and, once it
                    // existed, the document font. The stored layout showed it plainly: two page objects, the
                    // Visual one carrying what the author chose and the top-level one still at its defaults.
                    //
                    // That matters because the two are read by different code. ReportVisualRenderer uses
                    // Visual.Page, so a positioned report looked right; HtmlReportRenderer and the engine's
                    // own page geometry read ReportLayout.PageSetup, so anything going through them used A4
                    // portrait with default margins however the report was designed.
                    //
                    // One source: the designed page IS the report's page.
                    //
                    // AND NEVER `Default` AS A FALLBACK, which was the second half of the same bug. When a
                    // draft had no Visual, this wrote A4/portrait/18-16-12-12/no-font over whatever the
                    // template held — so opening a report and pressing save silently reset its page. The
                    // fallback is now what the draft CAME IN with; a real default is reached only by a draft
                    // that has no stored page at all, which is what a new report is.
                    PageSetup = validation.Visual?.Page ?? draft.PageSetup ?? ReportPageSetup.Default,

                    // THE NAME TYPED IN THE DESIGNER IS THE DOCUMENT'S TITLE.
                    //
                    // It was stored only as the TEMPLATE's name — a label for the saved-reports list — so
                    // renaming a report in Report Studio changed what the list called it and nothing that
                    // printed. ReportLayout has carried TitleOverride/TitleOverrideEn all along and
                    // ReportEngine.ResolveTitle already prefers it over the definition's title; nothing was
                    // filling it.
                    //
                    // Both languages, separately: the Arabic box titles an Arabic run and the English box an
                    // English one. Writing one into both — which an earlier line in this file did for the
                    // template name — is how a layout ends up with an Arabic string in its English column.
                    TitleOverride = name,
                    TitleOverrideEn = nameEn,

                    // STRUCTURE, not markup. What is stored is bands, elements and millimetres — never the DOM
                    // the designer happened to build, which §14 forbids and which would make the saved report
                    // un-reopenable the first time the designer's HTML changed.
                    Visual = validation.Visual,
                },
                ChangeNote = "Report Studio",
            }, context, cancellationToken);
        }

        // ========================================================================================
        // REOPEN — the stored layout, RE-VALIDATED against today's permissions.
        //
        // A saved report is not a grant. If the author has since lost the profitability right, reopening their
        // own report must not hand the columns back, so the stored layout is run through the same gate a fresh
        // draft is. Columns that no longer clear are dropped HERE (rather than refused) because the alternative
        // is a report its owner can never open again — and dropping is safe in this direction: it removes
        // access, it cannot grant it.
        // ========================================================================================
        public async Task<StudioDraft?> OpenAsync(int templateId, CancellationToken cancellationToken = default)
        {
            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context is not { CompanyId: > 0 } || templateId <= 0) return null;

            var datasets = await _datasets.ListForStudioAsync(context, cancellationToken);

            foreach (var dataset in datasets)
            {
                var definition = DefinitionFor(dataset);
                if (definition is null) continue;

                // ListAsync applies the template's own scope and ownership rules, so a template belonging to
                // another company or another person is simply not in this list.
                var templates = await _templates.ListAsync(definition.Code, context, cancellationToken);
                if (templates.All(t => t.Id != templateId)) continue;

                var resolution = await _templates.ResolveAsync(definition, templateId, null, context,
                    cancellationToken);
                if (resolution.Template is null) continue;

                var held = await HeldAsync(dataset, context, cancellationToken);
                var permitted = Visible(dataset, held)
                    .Select(f => f.Key)
                    .ToHashSet(StringComparer.Ordinal);

                var layout = resolution.Layout;

                return new StudioDraft
                {
                    TemplateId = templateId,
                    DatasetCode = dataset.DatasetCode,

                    // What the template ACTUALLY stores, so the designer opens on the real page and cannot
                    // save a default over it. Not filtered by permissions: a margin is not data.
                    PageSetup = layout.PageSetup,

                    Name = resolution.Template.Name,
                    NameEn = resolution.Template.NameEn ?? "",
                    Columns = layout.VisibleColumns.Where(permitted.Contains).ToList(),
                    Filters = layout.Filters
                        .Where(f => permitted.Contains(f.Field))
                        .Select(f => new StudioFilterDraft
                        {
                            Field = f.Field,
                            Operator = f.Operator,
                            Values = f.Values.ToList(),
                        }).ToList(),
                    Sorts = layout.Sorts
                        .Where(s => permitted.Contains(s.Field))
                        .Select(s => new StudioSortDraft { Field = s.Field, Descending = s.Descending })
                        .ToList(),
                    PageSize = 50,

                    // Only DECLARED, non-system parameters come back. A stored value for a parameter the dataset
                    // has since dropped is discarded rather than replayed into a binder that no longer knows it.
                    Parameters = layout.Parameters
                        .Where(kv => dataset.Parameters.Any(pd =>
                            string.Equals(pd.Key, kv.Key, StringComparison.Ordinal) && !pd.SystemSupplied))
                        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),

                    // LENIENT on reopen, and the asymmetry with Validate is the whole point: refusing the
                    // document because one element binds a field its author has since lost would leave them
                    // unable to open their own report. The illegal bindings are DROPPED — which only ever
                    // removes access — and everything else opens.
                    //
                    // §17 in one line: a saved layout is not a grant. It is re-checked against what this caller
                    // may see TODAY, every single time it is opened.
                    Visual = layout.Visual is null
                        ? null
                        : _visual.Sanitise(layout.Visual, dataset, permitted,
                                           await _assets.PermittedIdsAsync(context, cancellationToken)).Sanitised,
                };
            }

            return null;
        }
    }
}
