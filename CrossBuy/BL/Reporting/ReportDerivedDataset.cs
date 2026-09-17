using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Reporting
{
    // =============================================================================================
    // DERIVED DATASETS — a new report shape without a deploy.
    //
    // THE PROBLEM THIS CLOSES. Every dataset is a C# class, so "show the same invoices with these six
    // columns, overdue only, under a different name" was a code change and a release. That is a ceiling
    // on the product, not on the platform: a dataset is already a PROJECTION over a data source, and
    // most of what it carries is presentation rather than reach.
    //
    // THE ONE RULE EVERYTHING ELSE FOLLOWS FROM:
    //
    //      A derived dataset is DERIVED from an existing one. It never DECLARES itself.
    //
    // The author picks a parent and narrows it. Everything security-bearing is inherited; everything
    // authored is presentation or narrowing. That makes the guarantee provable rather than promised:
    // a derived dataset cannot reach a row or a field its parent could not.
    //
    // HOW THAT IS ENFORCED — and it is the same technique ReportElement uses against server-side fetch:
    // THE DANGEROUS PROPERTIES DO NOT EXIST ON THIS CONTRACT. There is no DataSourceKey and no
    // RequiredPermissionKey on a spec, at either level. They are not validated, not defaulted and not
    // overridable, because there is nothing for an author, a request body or a future careless edit to
    // put them in. A rule you cannot express is a rule you cannot get wrong.
    //
    // This is the same asymmetry the engine already states about filters: narrowing is always safe,
    // widening never is.
    // =============================================================================================

    /// One field kept from the parent, optionally re-presented. Absent (null) means "inherit".
    public sealed class ReportDerivedField
    {
        /// Must name a field the PARENT declares. An unknown key is refused, never dropped: silently
        /// ignoring it would leave an author looking at a dataset missing a column they asked for.
        public string Key { get; set; } = "";

        // ---- presentation: free, because none of it reaches data ----------------------------------
        public string? TitleAr { get; set; }
        public string? TitleEn { get; set; }
        public string? Format { get; set; }
        public ReportAlign? Align { get; set; }
        public double? WidthMm { get; set; }
        public bool? VisibleByDefault { get; set; }

        // ---- capability: may only be turned OFF ---------------------------------------------------
        //
        // true where the parent said false would be the one place a projection could widen — it would
        // offer grouping on a column the parent had judged ungroupable.
        public bool? Groupable { get; set; }
        public bool? Sortable { get; set; }
        public bool? Filterable { get; set; }

        /// A SUBSET of the parent's list. See the builder for why an undeclared parent list means the
        /// derived one must stay undeclared too.
        public List<ReportAggregate>? SupportedAggregates { get; set; }

        /// A subset of the parent's operator list, same rule.
        public List<ReportFilterOperator>? SupportedOperators { get; set; }

        // NO Type, NO Sensitivity, NO RequiredPermissionKey, NO Expression, NO DependsOn,
        // NO LookupEntityCode, NO DrillThroughKey. Each is a statement about the DATA rather than about
        // this report, and each is inherited verbatim. Expression is the sharpest of them: it is the
        // platform's only evaluated string, and authoring one here would hand an author the expression
        // surface the whole product refuses.
    }

    /// What an author writes. Deliberately small, and deliberately missing the two keys that would
    /// turn it into an escalation.
    public sealed class ReportDerivedDatasetSpec
    {
        /// The NEW dataset's code. Must not collide with a code-authored one — see the builder.
        public string DatasetCode { get; set; } = "";

        /// The dataset this one narrows. The single source of everything security-bearing.
        public string ParentDatasetCode { get; set; } = "";

        public string TitleAr { get; set; } = "";
        public string TitleEn { get; set; } = "";
        public string? DescriptionAr { get; set; }
        public string? DescriptionEn { get; set; }

        /// The kept fields, in display order. Empty is refused: a dataset with no fields is not a
        /// narrower view of anything, it is a broken one.
        public List<ReportDerivedField> Fields { get; set; } = new();

        /// Baked narrowing — the most useful thing an author can add. "Overdue receivables" is the
        /// receivables parent plus one filter, and a filter can only ever remove rows.
        public List<ReportFilter> FixedFilters { get; set; } = new();

        /// May only LOWER the parent's ceiling.
        public int? MaxRows { get; set; }

        public bool AvailableInStudio { get; set; } = true;
        public bool AvailableAsWidget { get; set; } = true;

        // NO DataSourceKey. NO RequiredPermissionKey. Their absence is the security argument.
    }

    public sealed class ReportDerivedDatasetResult
    {
        public bool Ok => Errors.Count == 0;
        public List<string> Errors { get; } = new();

        /// The built definition, or null when the spec was refused. There is no partial build: a spec
        /// that breaks one rule produces nothing, because "mostly applied" is not a security state.
        public ReportDatasetDefinition? Definition { get; set; }
    }

    // =============================================================================================
    // THE BUILDER, WHICH IS THE VALIDATOR.
    //
    // Building and checking are the same pass on purpose. A separate Validate() that a caller could
    // forget to run before Save() is the shape of every "we validated the wrong copy" bug; here the
    // only way to obtain a definition is to have passed.
    // =============================================================================================
    public static class ReportDerivedDatasetBuilder
    {
        public const int MaxCodeLength = 128;
        public const int MaxFields = 200;
        public const int MaxFixedFilters = 20;

        public static ReportDerivedDatasetResult Build(
            ReportDerivedDatasetSpec spec,
            IReportDatasetDefinition? parent,
            IReadOnlyCollection<string> codeAuthoredCodes)
        {
            var r = new ReportDerivedDatasetResult();
            if (spec is null) { r.Errors.Add("No specification was supplied."); return r; }

            // ---- the parent ----------------------------------------------------------------------
            if (parent is null)
            {
                // ONE MESSAGE for "no such parent" and "not yours". Distinguishing them would turn the
                // authoring screen into a catalogue oracle, which is the rule the layout validator
                // already keeps for fields.
                r.Errors.Add("The parent dataset is not available.");
                return r;
            }

            // IsDeprecated lives on the concrete class, not the interface, so the same judgement is made
            // from the two fields the interface does publish.
            if (!string.IsNullOrWhiteSpace(parent.DeprecationNoticeEn)
                || !string.IsNullOrWhiteSpace(parent.DeprecationNoticeAr))
                r.Errors.Add($"'{parent.DatasetCode}' is deprecated and cannot be derived from.");

            // ---- the new code --------------------------------------------------------------------
            var code = (spec.DatasetCode ?? "").Trim();
            if (code.Length == 0 || code.Length > MaxCodeLength)
                r.Errors.Add("A derived dataset needs a code.");
            else if (!IsCodeShaped(code))
                r.Errors.Add("A dataset code may hold letters, digits, dot, dash and underscore only.");
            else if (codeAuthoredCodes.Contains(code, StringComparer.Ordinal))
                // A DERIVED DATASET MAY NEVER SHADOW A CODE-AUTHORED ONE. Last-registration-wins here
                // would let an author replace a governed dataset — and with it its permission key —
                // by naming theirs the same. The registry refuses duplicates for this reason today.
                r.Errors.Add($"'{code}' is already a platform dataset.");

            // ---- bilingual identity, the platform's standing rule ---------------------------------
            if (string.IsNullOrWhiteSpace(spec.TitleAr) || string.IsNullOrWhiteSpace(spec.TitleEn))
                r.Errors.Add("A derived dataset needs both an Arabic and an English title.");

            // ---- fields ---------------------------------------------------------------------------
            if (spec.Fields is null || spec.Fields.Count == 0)
                r.Errors.Add("A derived dataset needs at least one field.");
            else if (spec.Fields.Count > MaxFields)
                r.Errors.Add($"A derived dataset may keep at most {MaxFields} fields.");

            var fields = new List<ReportDatasetField>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var want in spec.Fields ?? new List<ReportDerivedField>())
            {
                var key = (want.Key ?? "").Trim();
                if (key.Length == 0) { r.Errors.Add("A kept field has no key."); continue; }

                if (!seen.Add(key)) { r.Errors.Add($"Field '{key}' is listed twice."); continue; }

                var from = Field(parent, key);
                if (from is null) { r.Errors.Add($"'{key}' is not a field of '{parent.DatasetCode}'."); continue; }

                if (!NarrowsOnly(want.Groupable, from.Groupable, key, "grouped", r)) continue;
                if (!NarrowsOnly(want.Sortable, from.Sortable, key, "sorted", r)) continue;
                if (!NarrowsOnly(want.Filterable, from.Filterable, key, "filtered", r)) continue;

                var aggregates = Subset(want.SupportedAggregates, from.SupportedAggregates,
                                        key, "aggregate", r);
                if (aggregates is null) continue;

                var operators = Subset(want.SupportedOperators, from.SupportedOperators,
                                       key, "operator", r);
                if (operators is null) continue;

                fields.Add(new ReportDatasetField
                {
                    // ---- inherited verbatim: the whole security surface of a field ----------------
                    Key = from.Key,
                    Type = from.Type,
                    Sensitivity = from.Sensitivity,
                    RequiredPermissionKey = from.RequiredPermissionKey,
                    Expression = from.Expression,
                    DependsOn = from.DependsOn,
                    LookupEntityCode = from.LookupEntityCode,
                    DrillThroughKey = from.DrillThroughKey,

                    // ---- authored, or inherited when the author said nothing ----------------------
                    TitleAr = Blank(want.TitleAr) ?? from.TitleAr,
                    TitleEn = Blank(want.TitleEn) ?? from.TitleEn,
                    Format = Blank(want.Format) ?? from.Format,
                    Align = want.Align ?? from.Align,
                    WidthMm = want.WidthMm is > 0 ? want.WidthMm.Value : from.WidthMm,
                    VisibleByDefault = want.VisibleByDefault ?? from.VisibleByDefault,

                    Groupable = want.Groupable ?? from.Groupable,
                    Sortable = want.Sortable ?? from.Sortable,
                    Filterable = want.Filterable ?? from.Filterable,
                    SupportedAggregates = aggregates,
                    SupportedOperators = operators,
                });
            }

            // ---- fixed filters --------------------------------------------------------------------
            if (spec.FixedFilters is { Count: > MaxFixedFilters })
                r.Errors.Add($"A derived dataset may bake in at most {MaxFixedFilters} filters.");

            foreach (var filter in spec.FixedFilters ?? new List<ReportFilter>())
            {
                // A BAKED FILTER IS CHECKED AGAINST THE PARENT, NOT AGAINST THE KEPT FIELDS. Narrowing
                // on a column the author chose not to display is legitimate and common — "overdue" is
                // a filter on a due date nobody wants printed — and it is still only ever a removal.
                var on = Field(parent, filter.Field);
                if (on is null)
                {
                    r.Errors.Add($"Filter field '{filter.Field}' is not a field of '{parent.DatasetCode}'.");
                    continue;
                }
                if (!on.Filterable)
                    r.Errors.Add($"'{on.TitleEn}' cannot be filtered.");
                else if (on.SupportedOperators.Count > 0 && !on.SupportedOperators.Contains(filter.Operator))
                    r.Errors.Add($"{filter.Operator} is not available on '{on.TitleEn}'.");
            }

            // ---- the row ceiling --------------------------------------------------------------------
            var maxRows = parent.MaxRows;
            if (spec.MaxRows is { } wanted)
            {
                if (wanted <= 0)
                    r.Errors.Add("A row ceiling must be a positive number.");
                else if (parent.MaxRows > 0 && wanted > parent.MaxRows)
                    r.Errors.Add($"The row ceiling may not exceed the parent's ({parent.MaxRows}).");
                else
                    maxRows = wanted;
            }

            if (!r.Ok) return r;

            r.Definition = new ReportDatasetDefinition
            {
                DatasetCode = code,
                Module = parent.Module,
                TitleAr = spec.TitleAr.Trim(),
                TitleEn = spec.TitleEn.Trim(),
                DescriptionAr = Blank(spec.DescriptionAr),
                DescriptionEn = Blank(spec.DescriptionEn),

                // ---- THE TWO INHERITED KEYS. Not read from the spec, because the spec has no such
                // ---- properties. This is the line the whole design exists to make unwritable.
                DataSourceKey = parent.DataSourceKey,
                RequiredPermissionKey = parent.RequiredPermissionKey,

                Fields = fields,
                Parameters = parent.Parameters,
                DrillDowns = parent.DrillDowns,
                DrillThroughTargets = parent.DrillThroughTargets,
                MaxRows = maxRows,
                RowCapPolicy = parent.RowCapPolicy,
                ExportPolicy = parent.ExportPolicy,
                AvailableInStudio = spec.AvailableInStudio,
                AvailableAsWidget = spec.AvailableAsWidget,
            };

            return r;
        }

        // A capability may be turned off, never on. Returns false and records the refusal otherwise.
        private static bool NarrowsOnly(bool? wanted, bool parentAllows, string key, string verb,
            ReportDerivedDatasetResult r)
        {
            if (wanted is not true || parentAllows) return true;
            r.Errors.Add($"'{key}' cannot be {verb} in '{key}'s own dataset, so it cannot be here.");
            return false;
        }

        // A declared list must be a subset of the parent's.
        //
        // AN UNDECLARED PARENT LIST MEANS THE DERIVED ONE STAYS UNDECLARED. An empty list on this
        // platform does not mean "none" — it means "fall back to type compatibility", which is a wider
        // and differently-shaped rule. Letting a derived dataset write a list where the parent had none
        // would be writing a rule the parent never expressed, and SUM on an invoice number is exactly
        // what that produces. Relaxing this needs the type rule restated here, and that is a later
        // increment, not a default.
        private static IReadOnlyList<T>? Subset<T>(List<T>? wanted, IReadOnlyList<T> parent,
            string key, string noun, ReportDerivedDatasetResult r) where T : struct
        {
            if (wanted is null) return parent;

            if (parent.Count == 0)
            {
                if (wanted.Count == 0) return parent;
                r.Errors.Add($"'{key}' declares no {noun} list, so one cannot be narrowed here.");
                return null;
            }

            foreach (var v in wanted)
            {
                if (parent.Contains(v)) continue;
                r.Errors.Add($"{v} is not available on '{key}'.");
                return null;
            }

            return wanted;
        }

        // FindField is on the concrete definition rather than the interface, and this builder takes the
        // interface so a derived dataset can itself be derived from later.
        private static ReportDatasetField? Field(IReportDatasetDefinition d, string? key) =>
            string.IsNullOrWhiteSpace(key)
                ? null
                : d.Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static bool IsCodeShaped(string code) =>
            code.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');
    }
}
