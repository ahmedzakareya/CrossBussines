using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037 §Dataset) — THE ADMINISTRATIVE STRUCTURE.
    //
    // Registers dbo.Hierarchicals — the organisation tree behind /Admin/AdministrativeStructure — as
    // ONE dataset, so Report Studio, the schedulers, the exporters and dashboard widgets can all build
    // over it with no new execution path. Same shape as InventoryDatasets: codes, permissions, a
    // definition, a report definition and one IReportDataSource.
    //
    // ------------------------------------------------------------------------------------------------
    // WHY ONE DATASET AND NOT FIVE
    //
    // The screen offers five printed shapes (an outline, a register, cards, a chart, a summary). Those
    // are LAYOUTS, not data: every one of them reads the same rows and differs only in arrangement,
    // which is precisely the split this layer exists to make. Five datasets over one table would be
    // five copies of the same twelve fields, drifting apart. A Studio author picks columns, grouping
    // and page setup per template instead.
    //
    // ------------------------------------------------------------------------------------------------
    // TENANCY — THE WHOLE TREE, and why this dataset is the exception
    //
    // dbo.Hierarchicals CARRIES NO CompanyID. The governance registry records the same fact about the
    // controller actions that write it: "carries no CompanyID and is one tree shared by every company".
    //
    // The first version of this dataset derived a boundary anyway — it entered only at the type-1 node
    // whose H_ObjectID matched the caller's company — because every other dataset here filters on a
    // CompanyID column and copying that felt safe. It was not safe, it was OPAQUE: a user who set the
    // type filter to «All» still saw one company, with nothing on the screen to say a second, invisible
    // filter was in force. An invented boundary that the UI cannot show is worse than no boundary,
    // because the reader cannot tell the difference between "that is all there is" and "that is all you
    // are being shown".
    //
    // The owner's decision is that the report shows what the tree holds, the same as
    // /Admin/AdministrativeStructure does. Stated plainly rather than left to be discovered:
    //
    //   * Rows from EVERY company's subtree are visible to anyone holding
    //     admin.orgstructure.reports.view — on screen and in CSV, Excel, PDF, scheduled deliveries and
    //     dashboard widgets alike. That permission is therefore a cross-company grant; Program.cs maps
    //     it, and narrowing who holds it is the lever.
    //   * Narrowing the DATA is explicit and belongs to the reader: RootUnitId takes any node and the
    //     report covers its subtree. It is a choice, not a fence, and the parameter panel shows it.
    //   * The tree contains duplicate roots (two nodes both carrying H_ObjectID 990771) and several
    //     distinct nodes named «الشركة الإقليمية». Rows are de-duplicated by H_ID on the walk, so a
    //     duplicated root cannot double every descendant.
    //   * If this table ever gains a company column, the gate belongs in the walk below and nowhere
    //     else.
    //
    // ------------------------------------------------------------------------------------------------
    // WHY THE WALK IS IN MEMORY
    //
    // Descendants of a self-referencing table need a recursive CTE, which EF Core cannot express, and
    // there is no company column to pre-filter on. The whole table is the organisation chart — tens to
    // low hundreds of rows, not a transaction log — so it is read once under a cap and walked in
    // memory. The cap is enforced on the READ, so a runaway table cannot pull the process over.
    // ============================================================================================

    public static class OrgStructureReportPermissions
    {
        // Mapped in Program.cs; UNMAPPED MEANS DENIED. Registering this dataset grants nobody anything.
        public const string View = "admin.orgstructure.reports.view";

        // Notes are free text on an organisational unit, which is where a reorganisation-in-progress or a
        // remark about a person ends up. Readable with the structure itself is the wrong default, so the
        // column is gated separately and hidden by default.
        public const string Notes = "admin.orgstructure.reports.notes";
    }

    public static class OrgStructureDatasetCodes
    {
        public const string OrgUnits = "Admin.OrgStructure.Units";

        public const string CategoryKey = "admin.reporting";
    }

    public static class OrgStructureDatasets
    {
        // An organisation chart, not a ledger. If a tree ever exceeds this it is a data-entry accident,
        // and TruncateAndDeclare says so on the page instead of guessing.
        public const int MaxUnitRows = 20_000;

        private static ReportParameterDescriptor CompanyParam() => new()
        {
            Key = ReportSystemParameters.CompanyId, TitleAr = "الشركة", TitleEn = "Company",
            Type = ReportFieldType.Integer, SystemSupplied = true,
        };

        public static ReportDatasetDefinition OrgUnits() => new()
        {
            DatasetCode = OrgStructureDatasetCodes.OrgUnits,
            Module = "Admin",
            TitleAr = "الهيكل الإداري",
            TitleEn = "Administrative structure",
            DescriptionAr = "وحدات الهيكل الإداري — شركات وفروع وجهات إدارية ومسميات وظيفية وموظفون — "
                          + "بالمستوى ومسار التبعية وعدد التابعين. يشمل الشجرة كاملة بكل الشركات؛ "
                          + "استخدم «يبدأ من الوحدة» للاقتصار على فرع منها.",
            DescriptionEn = "The organisational units — companies, branches, administrative entities, positions "
                          + "and people — each with its level, its reporting path and its counts. Covers the "
                          + "WHOLE tree across every company; use \"Start at unit\" to narrow to one branch of it.",
            DataSourceKey = OrgStructureDatasetCodes.OrgUnits,
            RequiredPermissionKey = OrgStructureReportPermissions.View,
            MaxRows = MaxUnitRows,
            RowCapPolicy = ReportRowCapPolicy.TruncateAndDeclare,

            // A gated field exists (Notes), so a spreadsheet of this dataset can carry it — same policy the
            // Inventory cost columns and the Business Event payload apply.
            ExportPolicy = ReportDatasetExportPolicy.RequirePermissionForDataExport,

            Fields = new[]
            {
                new ReportDatasetField
                {
                    // The reporting path — "الشركة العالمية / الفرع الثاني / رئيس مجلس الادارة".
                    //
                    // FIRST FIELD ON PURPOSE. A flat row out of a tree loses the one thing the tree carried,
                    // and a register whose rows are only indented is unusable the moment somebody sorts it.
                    // Grouping on the path also gives a Studio author a hierarchy for free.
                    Key = "Path", TitleAr = "مسار التبعية", TitleEn = "Reporting path",
                    Groupable = true, WidthMm = 70,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "UnitName", TitleAr = "الوحدة", TitleEn = "Unit",
                    Groupable = true, WidthMm = 48,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    // Both names are always carried, whatever the run's culture. The owner's rule is a twin
                    // column for every displayed field; a report exported in Arabic and read in English must
                    // not have lost half its identity on the way out.
                    Key = "UnitNameAr", TitleAr = "الاسم بالعربية", TitleEn = "Name (Arabic)",
                    VisibleByDefault = false, WidthMm = 48,
                },
                new ReportDatasetField
                {
                    Key = "UnitNameEn", TitleAr = "الاسم بالإنجليزية", TitleEn = "Name (English)",
                    VisibleByDefault = false, WidthMm = 48,
                },
                new ReportDatasetField
                {
                    Key = "UnitType", TitleAr = "النوع", TitleEn = "Type",
                    Groupable = true, WidthMm = 28,
                    SupportedAggregates = new[] { ReportAggregate.Count },
                },
                new ReportDatasetField
                {
                    // The numeric type, for a filter that must not depend on a translated label.
                    Key = "UnitTypeId", TitleAr = "رمز النوع", TitleEn = "Type id",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 18,
                },
                new ReportDatasetField
                {
                    Key = "Level", TitleAr = "المستوى", TitleEn = "Level",
                    Type = ReportFieldType.Integer, Align = ReportAlign.End, Groupable = true, WidthMm = 20,
                    SupportedAggregates = new[] { ReportAggregate.Max, ReportAggregate.Min },
                },
                new ReportDatasetField
                {
                    Key = "ParentName", TitleAr = "يتبع", TitleEn = "Reports to",
                    Groupable = true, WidthMm = 44,
                    SupportedAggregates = new[] { ReportAggregate.CountDistinct },
                },
                new ReportDatasetField
                {
                    Key = "ParentId", TitleAr = "رمز الأصل", TitleEn = "Parent id",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 18,
                },
                new ReportDatasetField
                {
                    Key = "DirectChildren", TitleAr = "التابعون المباشرون", TitleEn = "Direct reports",
                    Type = ReportFieldType.Integer, Align = ReportAlign.End, WidthMm = 24,
                    SupportedAggregates = new[] { ReportAggregate.Sum, ReportAggregate.Max, ReportAggregate.Average },
                },
                new ReportDatasetField
                {
                    // Everything beneath the unit, not just its children. A department of depth three reads as
                    // one unit under DirectChildren and as its real size here.
                    Key = "DescendantCount", TitleAr = "إجمالي التابعين", TitleEn = "Total below",
                    Type = ReportFieldType.Integer, Align = ReportAlign.End, WidthMm = 24,
                    // NOT summed: descendants overlap between a parent and its child, so a column total
                    // would count the same unit once per ancestor. Max answers "how big is the largest arm".
                    SupportedAggregates = new[] { ReportAggregate.Max, ReportAggregate.Average },
                },
                new ReportDatasetField
                {
                    Key = "IsActive", TitleAr = "نشطة", TitleEn = "Active",
                    Type = ReportFieldType.Boolean, Groupable = true, WidthMm = 20,
                },
                new ReportDatasetField
                {
                    Key = "Notes", TitleAr = "ملاحظات", TitleEn = "Notes",
                    VisibleByDefault = false, WidthMm = 60,
                    Sensitivity = ReportFieldSensitivity.Confidential,
                    RequiredPermissionKey = OrgStructureReportPermissions.Notes,
                    Groupable = false,
                },
                new ReportDatasetField
                {
                    Key = "UnitId", TitleAr = "رمز الوحدة", TitleEn = "Unit id",
                    Type = ReportFieldType.Integer, Align = ReportAlign.End,
                    VisibleByDefault = false, WidthMm = 18,
                },
                new ReportDatasetField
                {
                    // H_ObjectID — the employee, company or branch this node stands for. A technical join key
                    // with no business meaning on a page, which is what Sensitivity.Never is for: declared on
                    // the DATA, so every report and every export over this dataset inherits the exclusion.
                    Key = "LinkedObjectId", TitleAr = "الكائن المرتبط", TitleEn = "Linked object id",
                    Type = ReportFieldType.Integer,
                    Sensitivity = ReportFieldSensitivity.Never,
                    VisibleByDefault = false, WidthMm = 18,
                },
                new ReportDatasetField
                {
                    Key = "SortOrder", TitleAr = "الترتيب", TitleEn = "Sort",
                    Type = ReportFieldType.Integer, VisibleByDefault = false, WidthMm = 16,
                },
            },

            Parameters = new[]
            {
                new ReportParameterDescriptor
                {
                    Key = "UnitTypeId", TitleAr = "نوع الوحدة", TitleEn = "Unit type",
                    Type = ReportFieldType.Integer,
                    Options = new[]
                    {
                        new ReportParameterOption { Value = "", LabelAr = "الكل", LabelEn = "Any" },
                        new ReportParameterOption { Value = "1", LabelAr = "شركة", LabelEn = "Company" },
                        new ReportParameterOption { Value = "2", LabelAr = "فرع", LabelEn = "Branch" },
                        new ReportParameterOption { Value = "3", LabelAr = "جهة إدارية", LabelEn = "Administrative entity" },
                        new ReportParameterOption { Value = "4", LabelAr = "مسمى وظيفي", LabelEn = "Position" },
                        new ReportParameterOption { Value = "5", LabelAr = "موظف", LabelEn = "Employee" },
                    },
                    DefaultValue = "",
                    HelpTextAr = "يصفّي الصفوف المعروضة فقط — المسار والمستوى يُحسبان من الشجرة كاملة.",
                    HelpTextEn = "Filters the rows shown. Path and level are still computed from the whole tree, "
                               + "so a filtered row keeps its true depth.",
                },
                new ReportParameterDescriptor
                {
                    Key = "RootUnitId", TitleAr = "يبدأ من الوحدة", TitleEn = "Start at unit",
                    Type = ReportFieldType.Integer,
                    HelpTextAr = "اتركه فارغًا للشجرة كاملة بكل الشركات، أو اكتب رمز وحدة للاقتصار على ما تحتها.",
                    HelpTextEn = "Leave empty for the whole tree across every company, or give a unit id to "
                               + "report only what sits under it."
                },
                new ReportParameterDescriptor
                {
                    Key = "IncludeInactive", TitleAr = "تضمين غير النشطة", TitleEn = "Include inactive",
                    Type = ReportFieldType.Boolean, DefaultValue = "false",
                    HelpTextAr = "الوحدات المعطّلة مستبعدة افتراضيًا. تضمينها لا يخفي أنها معطّلة — عمود «نشطة» يقولها.",
                    HelpTextEn = "Disabled units are excluded by default. Including them does not hide the fact: "
                               + "the Active column still says so.",
                },
                new ReportParameterDescriptor
                {
                    Key = "MaxLevel", TitleAr = "حتى المستوى", TitleEn = "Down to level",
                    Type = ReportFieldType.Integer,
                    HelpTextAr = "لطباعة مخطط من مستويين أو ثلاثة بدل الشجرة كاملة.",
                    HelpTextEn = "For a two- or three-level chart instead of the whole tree.",
                },
                CompanyParam(),
            },

            DrillDowns = new[]
            {
                new ReportDatasetDrillDown
                {
                    Key = "by-type", TitleAr = "حسب النوع", TitleEn = "By type",
                    Levels = new[] { "UnitType", "ParentName", "UnitName" },
                },
                new ReportDatasetDrillDown
                {
                    Key = "by-level", TitleAr = "حسب المستوى", TitleEn = "By level",
                    Levels = new[] { "Level", "UnitType", "UnitName" },
                },
            },
        };

        public static IEnumerable<ReportDatasetDefinition> All()
        {
            yield return OrgUnits();
        }
    }

    public sealed class OrgStructureReportDefinitionProvider : IReportDefinitionProvider
    {
        public string ProviderName => "OrgStructure";

        public IEnumerable<ReportDefinition> GetDefinitions()
        {
            var dataset = OrgStructureDatasets.OrgUnits();
            yield return new ReportDefinition
            {
                Code = dataset.DatasetCode,
                Module = dataset.Module,
                TitleAr = dataset.TitleAr,
                TitleEn = dataset.TitleEn,
                DescriptionAr = dataset.DescriptionAr,
                DescriptionEn = dataset.DescriptionEn,
                DataSourceKey = dataset.DataSourceKey,
                PermissionKey = dataset.RequiredPermissionKey,
                CategoryKey = OrgStructureDatasetCodes.CategoryKey,
                Tags = new[] { "admin", "structure", "org" },
                Columns = dataset.Fields.Select(f => f.ToColumn()).ToList(),
                Parameters = dataset.Parameters,
                // SORTED BY THE TREE, not by name. The source returns pre-order (a parent immediately before
                // its children) and SortOrder preserves it, so the default view of a hierarchy reads as one.
                DefaultSorts = new[] { ReportSort.By("SortOrder") },
                DefaultFormat = ReportOutputFormat.Html,
                Capabilities = new ReportCapabilities
                {
                    Formats = new[]
                    {
                        ReportOutputFormat.Html, ReportOutputFormat.PrintHtml,
                        ReportOutputFormat.Csv, ReportOutputFormat.Xlsx, ReportOutputFormat.Pdf,
                    },
                    MaxRows = dataset.MaxRows,
                    PreviewRows = 100,
                    AllowSchedule = true,
                    AllowArchive = true,
                    AllowShare = true,
                },
                Icon = "ki-outline ki-abstract-26",
                Color = "primary",
                SortOrder = 300,
            };
        }
    }

    // ============================================================================================
    // DATA SOURCE
    // ============================================================================================
    public sealed class OrgStructureDataSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public OrgStructureDataSource(CrossDbContext db) { _db = db; }

        public string Key => OrgStructureDatasetCodes.OrgUnits;

        // The five node types, resolved to labels here rather than joined to HierarchicalType: the label has to
        // exist in both languages for a report that may be run in either, and the type table carries one name.
        private static (string Ar, string En) TypeLabel(int? type) => type switch
        {
            1 => ("شركة", "Company"),
            2 => ("فرع", "Branch"),
            3 => ("جهة إدارية", "Administrative entity"),
            4 => ("مسمى وظيفي", "Position"),
            5 => ("موظف", "Employee"),
            _ => ("غير محدد", "Unspecified"),
        };

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var context = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);

            // A RESOLVED BUSINESS CONTEXT IS STILL REQUIRED, but it no longer selects rows: this tree
            // carries no company column and the report covers all of it. The check stays as an
            // authentication sanity gate - an unresolved caller reads nothing anywhere in reporting -
            // and not as the tenant filter it used to be.
            if (context.CompanyId <= 0) return builder.Build(totalRowCount: 0);

            bool arabic = AccountingSourceHelpers.Arabic(query);
            var typeFilter = query.Parameters.GetInt("UnitTypeId");
            var rootParam = query.Parameters.GetInt("RootUnitId");
            var includeInactive = query.Parameters.GetBool("IncludeInactive");
            var maxLevel = query.Parameters.GetInt("MaxLevel");

            int cap = query.MaxRows > 0 ? query.MaxRows : OrgStructureDatasets.MaxUnitRows;

            // ONE read of the tree, capped. There is no company column to push down and descendants need a
            // recursive CTE EF cannot write, so the shape of the table decides the shape of the query: read
            // the chart, walk it here. cap + 1 is the standard "did we hit the ceiling" probe.
            var all = await _db.Hierarchicals.AsNoTracking()
                .OrderBy(h => h.H_Parent).ThenBy(h => h.Sort).ThenBy(h => h.H_ID)
                .Take(cap + 1)
                .Select(h => new
                {
                    h.H_ID, h.H_Name, h.H_NameEn, h.H_Parent, h.H_Notes, h.H_Type, h.H_ObjectID, h.Sort, h.IsActive,
                })
                .ToListAsync(cancellationToken);

            bool truncated = all.Count > cap;
            if (truncated) all = all.Take(cap).ToList();
            if (all.Count == 0) return builder.Build(totalRowCount: 0);

            var byId = all.ToDictionary(h => h.H_ID);
            var childrenOf = all.Where(h => h.H_Parent != null)
                .GroupBy(h => h.H_Parent!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            // ---- SCOPE: THE WHOLE TREE, BY OWNER'S DECISION ----------------------------------------------
            //
            // This dataset first entered the tree only at the type-1 node whose H_ObjectID matched the
            // caller's company, mirroring what every other dataset does with its CompanyID column. It was
            // the conservative reading and it was WRONG FOR THIS TABLE, in a way that showed up the moment
            // the report was used: a user picking «All» on the type filter still saw one company and could
            // not tell why - nothing on the screen said a second, invisible filter was in force.
            //
            // dbo.Hierarchicals has no CompanyID. The governance registry states the consequence plainly:
            // it is "one tree shared by every company". Deriving a boundary from a root node's H_ObjectID
            // invented a tenancy the data does not have, and then hid it. The owner's decision is that the
            // report shows what the tree contains, the same as /Admin/AdministrativeStructure does.
            //
            // WHAT THAT MEANS, said out loud rather than left to be discovered: rows from every company's
            // subtree are visible to anyone holding admin.orgstructure.reports.view, and that includes CSV,
            // Excel, PDF, scheduled deliveries and dashboard widgets. Narrowing is available and explicit -
            // the RootUnitId parameter takes any node - but it is a CHOICE the reader makes, not a fence.
            // If this table ever gains a company column, restore the gate here and nowhere else.
            var roots = all.Where(h => h.H_Parent == null).Select(h => h.H_ID).ToList();

            // A supplied RootUnitId narrows to that node's subtree. There is no boundary left for it to
            // escape, so it is honoured whenever it names a node that exists.
            var walkFrom = rootParam is > 0 && byId.ContainsKey(rootParam.Value)
                ? new List<int> { rootParam.Value }
                : roots;

            // ---- PRE-ORDER WALK -------------------------------------------------------------------------
            // Parents immediately before their children, which is the order a hierarchy has to be read in and
            // what SortOrder below preserves. `seen` guards a cycle in H_Parent: one bad row must not spin.
            var seen = new HashSet<int>();
            var ordered = new List<(int Id, int Level)>();
            void Walk(int id, int level)
            {
                if (!seen.Add(id)) return;
                if (maxLevel is > 0 && level > maxLevel.Value) return;
                ordered.Add((id, level));
                if (!childrenOf.TryGetValue(id, out var kids)) return;
                foreach (var k in kids.OrderBy(k => k.Sort ?? int.MaxValue).ThenBy(k => k.H_ID))
                    Walk(k.H_ID, level + 1);
            }
            foreach (var r in walkFrom) Walk(r, 1);

            // Descendant counts over the WALKED set, so a level cap or a narrowed root is reflected in the
            // number rather than contradicted by it.
            var levelOf = ordered.ToDictionary(o => o.Id, o => o.Level);
            int Descendants(int id)
            {
                if (!childrenOf.TryGetValue(id, out var kids)) return 0;
                int n = 0;
                foreach (var k in kids)
                {
                    if (!levelOf.ContainsKey(k.H_ID)) continue;
                    n += 1 + Descendants(k.H_ID);
                }
                return n;
            }

            string Name(int id) => byId.TryGetValue(id, out var h)
                ? AccountingSourceHelpers.Pick(arabic, h.H_Name, h.H_NameEn)
                : "#" + id;

            // The path is built from the walked ancestors, so it stops at the narrowed root instead of naming
            // a parent the caller was not given.
            string Path(int id)
            {
                var parts = new List<string>();
                int? cur = id, guard = 0;
                while (cur != null && levelOf.ContainsKey(cur.Value) && guard++ < 64)
                {
                    parts.Add(Name(cur.Value));
                    cur = byId.TryGetValue(cur.Value, out var h) ? h.H_Parent : null;
                }
                parts.Reverse();
                return string.Join(" / ", parts);
            }

            int sort = 0;
            int emitted = 0;
            foreach (var (id, level) in ordered)
            {
                var h = byId[id];
                sort++;

                // The row filters are applied AFTER the walk on purpose. Filtering the tree first would
                // reparent the survivors and make Level and Path lie: filter to "Employee" and every person
                // would read as level 1 with no path. This way a filtered row keeps its true depth.
                bool active = h.IsActive ?? true;
                if (!includeInactive && !active) continue;
                if (typeFilter is > 0 && (h.H_Type ?? 0) != typeFilter.Value) continue;

                var label = TypeLabel(h.H_Type);
                builder.AddRow(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Path"] = Path(id),
                    ["UnitName"] = AccountingSourceHelpers.Pick(arabic, h.H_Name, h.H_NameEn),
                    ["UnitNameAr"] = h.H_Name ?? "",
                    ["UnitNameEn"] = h.H_NameEn ?? "",
                    ["UnitType"] = arabic ? label.Ar : label.En,
                    ["UnitTypeId"] = h.H_Type,
                    ["Level"] = level,
                    ["ParentName"] = h.H_Parent != null ? Name(h.H_Parent.Value) : "",
                    ["ParentId"] = h.H_Parent,
                    ["DirectChildren"] = childrenOf.TryGetValue(id, out var kids)
                        ? kids.Count(k => levelOf.ContainsKey(k.H_ID))
                        : 0,
                    ["DescendantCount"] = Descendants(id),
                    ["IsActive"] = active,
                    ["Notes"] = h.H_Notes ?? "",
                    ["UnitId"] = h.H_ID,
                    ["LinkedObjectId"] = h.H_ObjectID,
                    ["SortOrder"] = sort,
                });
                emitted++;
            }

            return builder.Build(truncated, truncated ? null : emitted,
                Array.Empty<ReportFilter>(),
                new[] { ReportSort.By("SortOrder") });
        }
    }
}
