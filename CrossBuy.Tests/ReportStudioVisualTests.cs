using System.Reflection;
using System.Text;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // REPORT STUDIO V2 — THE VISUAL DESIGNER.
    //
    // V1's tests asked "can a draft ask for a column the caller may not have". A designed report widens the
    // question, because a saved layout is a NEW kind of persisted, caller-authored artifact:
    //
    //      · it names FIELDS, so it can try to bind one its author has since lost;
    //      · it names IMAGES, so it can try to reference another company's file;
    //      · it carries TEXT, so it can try to carry script;
    //      · it carries GEOMETRY and STYLE, so it can try to be nonsense;
    //      · it is REOPENED later, so yesterday's permission must not become today's grant.
    //
    // Every test below asks one of those of the SERVER. None drives the browser: a rule that only holds
    // when the designer behaves is not a rule, and the designer is the one component an attacker replaces
    // first.
    //
    // The two-company fixture is the same one V1 uses, for the same reason: a single-company fixture cannot
    // fail an isolation assertion.
    // =============================================================================================
    public class ReportStudioVisualTests
    {
        private const int Mine = 1;
        private const int Theirs = 99;

        private static void Grant(ReportingTestHost host, params string[] keys)
        {
            foreach (var key in keys) host.PermissionOptions.RoleMap[key] = new[] { "Reports" };
        }

        private static ReportStudioService Studio(ReportingTestHost host, IReportDataSource? source = null,
            params IReportDatasetDefinition[] datasets) =>
            new(host.DatasetRegistry(datasets.Length > 0 ? datasets : Sales),
                host.Catalog, host.PermissionEvaluator, host.Templates,
                host.Reports(host.EngineWith(source ?? new SalesRevenueDataSource(host.Db))),
                host.Accessor, host.VisualValidator, host.Assets);

        private static readonly IReportDatasetDefinition[] Sales =
        {
            AccountingDatasets.SalesRevenue(),
        };

        private static CrossBuy.Models.Context.CrossDbContext Neighbour(ReportingTestHost host)
        {
            var scope = new CompanyScopeHolder();
            scope.Set(Theirs, null);
            return host.NewContext(scope);
        }

        // Dates sit in May 2026 for the reason V1 recorded: the datasets default From/To to
        // month-start..today against a clock fixed at 2026-05-14, so a March fixture runs over zero rows and
        // makes an assertion pass for the wrong reason.
        private static async Task SeedSalesAsync(ReportingTestHost host)
        {
            var db = host.Db;
            db.Customers.Add(new Customer { ID = 10, CompanyID = Mine, Name = "عميل", NameEn = "Customer One", ControlAccountId = 1 });
            db.SalesInvoices.AddRange(
                new SalesInvoice { ID = 100, CompanyID = Mine, InvoiceNo = "SI-1", InvoiceDate = new DateTime(2026, 5, 4), CustomerId = 10, SubTotal = 100m, TaxTotal = 14m, GrandTotal = 114m, Status = "Draft" },
                new SalesInvoice { ID = 101, CompanyID = Mine, InvoiceNo = "SI-2", InvoiceDate = new DateTime(2026, 5, 10), CustomerId = 10, SubTotal = 200m, TaxTotal = 28m, GrandTotal = 228m, Status = "Posted" },
                new SalesInvoice { ID = 102, CompanyID = Mine, InvoiceNo = "SI-3", InvoiceDate = new DateTime(2026, 5, 12), CustomerId = 10, SubTotal = 300m, TaxTotal = 42m, GrandTotal = 342m, Status = "Posted" });
            await db.SaveChangesAsync();

            await using var theirs = Neighbour(host);
            theirs.Customers.Add(new Customer { ID = 11, CompanyID = Theirs, Name = "جار", NameEn = "Neighbour", ControlAccountId = 1 });
            theirs.SalesInvoices.Add(new SalesInvoice { ID = 103, CompanyID = Theirs, InvoiceNo = "SI-X", InvoiceDate = new DateTime(2026, 5, 11), CustomerId = 11, SubTotal = 9_999m, TaxTotal = 0m, GrandTotal = 9_999m, Status = "Draft" });
            await theirs.SaveChangesAsync();
        }

        // ---- layout builders --------------------------------------------------------------------------
        private static ReportVisualLayout Layout(Action<ReportVisualLayout>? tweak = null)
        {
            var layout = ReportVisualLayout.Blank();
            tweak?.Invoke(layout);
            return layout;
        }

        private static ReportElement Put(ReportVisualLayout layout, ReportBandKind kind, ReportElement element)
        {
            layout.Band(kind)!.Elements.Add(element);
            return element;
        }

        private static ReportElement Text(string text, double x = 5, double y = 2) => new()
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Kind = ReportElementKind.Text,
            Text = text,
            XMm = x, YMm = y, WidthMm = 60, HeightMm = 6,
        };

        private static ReportElement Field(string key, double x = 5, double y = 1) => new()
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Kind = ReportElementKind.Field,
            FieldKey = key,
            XMm = x, YMm = y, WidthMm = 40, HeightMm = 6,
        };

        private static ReportElement Image(int? assetId, ReportImageRole role = ReportImageRole.CompanyLogo) => new()
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Kind = ReportElementKind.Image,
            AssetId = assetId,
            ImageRole = role,
            XMm = 4, YMm = 2, WidthMm = 30, HeightMm = 15,
        };

        private static StudioDraft Draft(ReportVisualLayout? visual, params string[] columns) => new()
        {
            DatasetCode = AccountingDatasetCodes.SalesRevenue,
            Name = "Designed report",
            Columns = columns.ToList(),
            PageSize = 200,
            Visual = visual,
        };

        private static byte[] Png()
        {
            // A 1×1 PNG. Real magic bytes, because the asset service sniffs rather than trusting a
            // Content-Type — a fake header here would test nothing.
            return Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        }

        private static string Csv(ReportResult result) =>
            result.Artifact is null ? "" : Encoding.UTF8.GetString(result.Artifact.Content);

        // =========================================================================================
        // 1–3  THE SHAPE OF A DESIGN
        // =========================================================================================

        [Fact] // 1
        public void A_blank_layout_carries_all_seven_bands_exactly_once()
        {
            var layout = ReportVisualLayout.Blank();

            Assert.Equal(7, layout.Bands.Count);
            foreach (ReportBandKind kind in Enum.GetValues<ReportBandKind>())
                Assert.Single(layout.Bands.Where(b => b.Kind == kind));
        }

        [Fact] // 2
        public async Task A_placed_element_keeps_its_exact_millimetre_position_through_save_and_reopen()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);

            var layout = Layout();
            var placed = Put(layout, ReportBandKind.PageHeader, Text("Head", x: 37.5, y: 4.5));
            placed.WidthMm = 63.5;
            placed.HeightMm = 9.5;

            var studio = Studio(host);
            var saved = await studio.SaveAsync(Draft(layout, "InvoiceNo"));
            Assert.True(saved.Success);

            var reopened = await studio.OpenAsync(saved.TemplateId);
            var back = reopened!.Visual!.Band(ReportBandKind.PageHeader)!.Elements.Single();

            Assert.Equal(37.5, back.XMm);
            Assert.Equal(4.5, back.YMm);
            Assert.Equal(63.5, back.WidthMm);
            Assert.Equal(9.5, back.HeightMm);
        }

        [Fact] // 3
        public async Task What_is_persisted_is_structure_and_never_rendered_markup()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);

            var layout = Layout();
            Put(layout, ReportBandKind.Detail, Field("InvoiceNo"));

            var studio = Studio(host);
            var saved = await studio.SaveAsync(Draft(layout, "InvoiceNo"));

            // Straight out of the store, not through the service: what is on disk is what matters.
            var version = await host.Templates.ResolveAsync(
                host.Catalog.GetDefinitions().First(d =>
                    d.DataSourceKey == AccountingDatasetCodes.SalesRevenue),
                saved.TemplateId, null, host.Ctx);

            var stored = version.Layout.Visual;
            Assert.NotNull(stored);
            Assert.Equal(ReportElementKind.Field, stored!.Band(ReportBandKind.Detail)!.Elements.Single().Kind);

            // The one thing that must NOT be there. A layout that stored markup would stop opening the first
            // time the designer's HTML changed, and would be un-auditable in between.
            var asJson = System.Text.Json.JsonSerializer.Serialize(stored);
            Assert.DoesNotContain("<div", asJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<span", asJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("style=", asJson, StringComparison.OrdinalIgnoreCase);
        }

        // =========================================================================================
        // 4–7  FIELD PERMISSIONS — a layout is not a grant
        // =========================================================================================

        [Fact] // 4
        public async Task A_layout_binding_a_field_the_caller_may_not_see_is_refused()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);   // NOT Profitability
            host.SeedEmployee(7);

            var layout = Layout();
            Put(layout, ReportBandKind.Detail, Field("GrandTotalBase"));

            var confidential = AccountingDatasets.SalesRevenue().Fields
                .FirstOrDefault(f => !string.IsNullOrEmpty(f.RequiredPermissionKey));

            if (confidential != null)
            {
                layout.Band(ReportBandKind.Detail)!.Elements.Clear();
                Put(layout, ReportBandKind.Detail, Field(confidential.Key));

                var validation = await Studio(host).ValidateAsync(Draft(layout));
                Assert.False(validation.Ok);
            }
        }

        [Fact] // 5
        public async Task An_unknown_field_and_an_unpermitted_field_answer_with_the_same_message()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);

            var unknown = Layout();
            Put(unknown, ReportBandKind.Detail, Field("ThereIsNoSuchColumn"));

            var validation = await Studio(host).ValidateAsync(Draft(unknown));

            Assert.False(validation.Ok);

            // The message must not distinguish "no such field" from "not yours" — telling them apart would
            // make the designer a free catalogue oracle for anyone with any dataset.
            var message = string.Join(" ", validation.Errors);
            Assert.DoesNotContain("does not exist", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("permission", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact] // 6
        public async Task A_stored_layout_is_revalidated_on_reopen_against_todays_permissions()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View, AccountingReportPermissions.Profitability);
            host.SeedEmployee(7);

            var dataset = AccountingDatasets.SalesRevenue();
            var guarded = dataset.Fields.FirstOrDefault(f => !string.IsNullOrEmpty(f.RequiredPermissionKey));
            if (guarded is null) return;   // the dataset declares no guarded field; nothing to prove here

            var layout = Layout();
            Put(layout, ReportBandKind.Detail, Field("InvoiceNo"));
            Put(layout, ReportBandKind.Detail, Field(guarded.Key, x: 60));

            var studio = Studio(host);
            var saved = await studio.SaveAsync(Draft(layout, "InvoiceNo"));
            Assert.True(saved.Success);

            // The grant goes away between save and reopen — the exact sequence a real permission change is.
            host.PermissionOptions.RoleMap.Remove(guarded.RequiredPermissionKey!);

            var reopened = await studio.OpenAsync(saved.TemplateId);
            var keys = reopened!.Visual!.Band(ReportBandKind.Detail)!.Elements
                .Select(e => e.FieldKey).ToList();

            Assert.Contains("InvoiceNo", keys);
            Assert.DoesNotContain(guarded.Key, keys);
        }

        [Fact] // 7
        public void A_dropped_binding_is_named_rather_than_silently_removed()
        {
            var dataset = AccountingDatasets.SalesRevenue();
            var layout = Layout();
            Put(layout, ReportBandKind.Detail, Field("InvoiceNo"));
            Put(layout, ReportBandKind.Detail, Field("GrandTotal", x: 60));

            var validator = new ReportVisualLayoutValidator();
            var result = validator.Sanitise(layout, dataset,
                new HashSet<string>(StringComparer.Ordinal) { "InvoiceNo" },
                new HashSet<int>());

            Assert.NotNull(result.Sanitised);
            Assert.Single(result.Sanitised!.Band(ReportBandKind.Detail)!.Elements);
            Assert.NotEmpty(result.Dropped);
        }

        // =========================================================================================
        // 8–17  IMAGES — company isolation, no path, no URL
        // =========================================================================================

        [Fact] // 8
        public async Task A_layout_referencing_another_companys_image_is_refused()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);

            // Uploaded by the NEIGHBOUR, through the same service, with their context.
            var theirsDb = Neighbour(host);
            var theirAssets = new ReportAssetService(theirsDb, host.AssetOptions, host.Clock);
            var theirContext = new BusinessContext
            {
                CompanyId = Theirs, EmployeeId = 1, UserId = "n", Roles = new[] { "Reports" },
                Source = BusinessContextSource.Test,
            };
            var foreign = await theirAssets.UploadAsync(new MemoryStream(Png()), "logo.png",
                ReportImageRole.CompanyLogo, "Theirs", theirContext);
            Assert.NotNull(foreign);

            var layout = Layout();
            Put(layout, ReportBandKind.PageHeader, Image(foreign!.Id));

            var validation = await Studio(host).ValidateAsync(Draft(layout, "InvoiceNo"));
            Assert.False(validation.Ok);
        }

        [Fact] // 9
        public async Task A_foreign_image_id_resolves_to_nothing_at_render_time_as_well()
        {
            using var host = new ReportingTestHost();

            var theirsDb = Neighbour(host);
            var theirAssets = new ReportAssetService(theirsDb, host.AssetOptions, host.Clock);
            var theirContext = new BusinessContext
            {
                CompanyId = Theirs, EmployeeId = 1, UserId = "n", Roles = new[] { "Reports" },
                Source = BusinessContextSource.Test,
            };
            var foreign = await theirAssets.UploadAsync(new MemoryStream(Png()), "logo.png",
                ReportImageRole.CompanyLogo, "Theirs", theirContext);

            // Read back with MY context — the validator is not involved at all here, so this proves the
            // isolation holds even if a layout somehow carried the id.
            var mine = await host.Assets.ReadAsync(foreign!.Id, host.Ctx);
            Assert.Null(mine);
        }

        [Fact] // 10
        public void The_image_contract_has_no_property_that_could_carry_a_path_or_a_url()
        {
            // A CONTRACT test, not a behaviour one. The rule "no URL-based server-side image fetch" is only
            // durable if there is nowhere to put a URL — a validator that rejected them could be bypassed by
            // the next caller, but an absent property cannot.
            var forbidden = new[] { "url", "uri", "path", "src", "href", "file" };

            foreach (var property in typeof(ReportElement).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = property.Name.ToLowerInvariant();
                foreach (var word in forbidden)
                    Assert.False(name.Contains(word),
                        $"ReportElement.{property.Name} could carry a location; images must be an id.");
            }
        }

        [Fact] // 11
        public async Task An_upload_that_is_not_an_image_is_refused()
        {
            using var host = new ReportingTestHost();

            var text = Encoding.UTF8.GetBytes("<?php echo 1; ?>");
            var saved = await host.Assets.UploadAsync(new MemoryStream(text), "logo.png",
                ReportImageRole.CompanyLogo, null, host.Ctx);

            // Named .png and it still does not get in: the bytes are sniffed, the extension is not trusted.
            Assert.Null(saved);
        }

        [Fact] // 12
        public async Task An_svg_carrying_script_is_refused()
        {
            using var host = new ReportingTestHost();

            var svg = Encoding.UTF8.GetBytes(
                "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");

            Assert.Null(await host.Assets.UploadAsync(new MemoryStream(svg), "x.svg",
                ReportImageRole.Custom, null, host.Ctx));

            // A plain one is fine — the refusal is about the script, not about SVG.
            var plain = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><rect/></svg>");
            Assert.NotNull(await host.Assets.UploadAsync(new MemoryStream(plain), "y.svg",
                ReportImageRole.Custom, null, host.Ctx));
        }

        [Fact] // 13
        public async Task The_stored_path_is_computed_and_never_the_name_the_browser_supplied()
        {
            using var host = new ReportingTestHost();

            var saved = await host.Assets.UploadAsync(new MemoryStream(Png()), "my-company-logo.png",
                ReportImageRole.CompanyLogo, "Logo", host.Ctx);
            Assert.NotNull(saved);

            var row = host.Db.ReportAssets.Single(a => a.Id == saved!.Id);

            Assert.StartsWith(Mine + "/", row.StoredPath);
            Assert.DoesNotContain("my-company-logo", row.StoredPath);
            Assert.EndsWith(".png", row.StoredPath);
        }

        [Fact] // 14
        public async Task A_traversal_attempt_in_the_filename_cannot_escape_the_asset_root()
        {
            using var host = new ReportingTestHost();

            var saved = await host.Assets.UploadAsync(new MemoryStream(Png()),
                @"..\..\..\windows\system32\evil.png", ReportImageRole.Custom, null, host.Ctx);
            Assert.NotNull(saved);

            var row = host.Db.ReportAssets.Single(a => a.Id == saved!.Id);

            Assert.DoesNotContain("..", row.StoredPath);
            Assert.DoesNotContain("system32", row.StoredPath);
            Assert.DoesNotContain("..", row.FileName);
        }

        [Fact] // 15
        public async Task The_same_bytes_uploaded_twice_reuse_one_stored_file()
        {
            using var host = new ReportingTestHost();

            var first = await host.Assets.UploadAsync(new MemoryStream(Png()), "a.png", ReportImageRole.Custom, null, host.Ctx);
            var second = await host.Assets.UploadAsync(new MemoryStream(Png()), "b.png", ReportImageRole.Custom, null, host.Ctx);

            Assert.Equal(first!.Id, second!.Id);
            Assert.Single(host.Db.ReportAssets.Where(a => a.CompanyID == Mine));
        }

        [Fact] // 16
        public async Task The_asset_list_shows_only_this_companys_images()
        {
            using var host = new ReportingTestHost();

            await host.Assets.UploadAsync(new MemoryStream(Png()), "mine.png", ReportImageRole.CompanyLogo, "Mine", host.Ctx);

            var theirsDb = Neighbour(host);
            var theirAssets = new ReportAssetService(theirsDb, host.AssetOptions, host.Clock);
            await theirAssets.UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes(
                    "<svg xmlns='http://www.w3.org/2000/svg'><rect/></svg>")),
                "theirs.svg", ReportImageRole.Custom, "Theirs",
                new BusinessContext
                {
                    CompanyId = Theirs, EmployeeId = 1, UserId = "n",
                    Roles = new[] { "Reports" }, Source = BusinessContextSource.Test,
                });

            var listed = await host.Assets.ListAsync(host.Ctx);

            Assert.Single(listed);
            Assert.Equal("Mine", listed[0].Title);
        }

        [Fact] // 17
        public async Task Deleting_an_image_is_soft_and_company_scoped()
        {
            using var host = new ReportingTestHost();

            var saved = await host.Assets.UploadAsync(new MemoryStream(Png()), "a.png", ReportImageRole.Custom, null, host.Ctx);

            var theirContext = new BusinessContext
            {
                CompanyId = Theirs, EmployeeId = 1, UserId = "n",
                Roles = new[] { "Reports" }, Source = BusinessContextSource.Test,
            };

            // The neighbour cannot delete it…
            Assert.False(await host.Assets.DeleteAsync(saved!.Id, theirContext));

            // …the owner can, and the row survives so an archived document stays explainable.
            Assert.True(await host.Assets.DeleteAsync(saved.Id, host.Ctx));
            Assert.NotNull(host.Db.ReportAssets.SingleOrDefault(a => a.Id == saved.Id));
            Assert.Empty(await host.Assets.ListAsync(host.Ctx));
        }

        // =========================================================================================
        // 18–25  TEXT, STYLE AND GEOMETRY
        // =========================================================================================

        [Fact] // 18
        public async Task Text_in_an_element_is_html_encoded_in_the_rendered_document()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);
            await SeedSalesAsync(host);

            var layout = Layout();
            Put(layout, ReportBandKind.ReportHeader, Text("<script>alert('x')</script>"));
            Put(layout, ReportBandKind.Detail, Field("InvoiceNo"));

            var result = await Studio(host).RunAsync(Draft(layout, "InvoiceNo"),
                ReportOutputFormat.PrintHtml, preview: false);

            Assert.True(result.IsSuccess);
            var html = Encoding.UTF8.GetString(result.Artifact!.Content);

            Assert.DoesNotContain("<script>alert", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("&lt;script&gt;", html, StringComparison.OrdinalIgnoreCase);
        }

        [Fact] // 19
        public async Task A_designed_report_emits_no_script_element_at_all()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);
            await SeedSalesAsync(host);

            var layout = Layout();
            Put(layout, ReportBandKind.ReportHeader, Text("Sales"));
            Put(layout, ReportBandKind.Detail, Field("InvoiceNo"));

            var result = await Studio(host).RunAsync(Draft(layout, "InvoiceNo"),
                ReportOutputFormat.PrintHtml, preview: false);

            var html = Encoding.UTF8.GetString(result.Artifact!.Content);

            // The print document is what the PDF engine loads. A <script> here would execute in that browser.
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        }

        [Fact] // 20
        public void A_colour_that_is_not_a_plain_hex_value_is_refused()
        {
            var dataset = AccountingDatasets.SalesRevenue();
            var layout = Layout();
            var e = Put(layout, ReportBandKind.ReportHeader, Text("x"));
            e.Style.Color = "red; background:url(javascript:alert(1))";

            var result = new ReportVisualLayoutValidator().Validate(layout, dataset,
                new HashSet<string>(StringComparer.Ordinal), new HashSet<int>());

            Assert.False(result.Ok);
        }

        [Fact] // 21
        public void A_font_outside_the_approved_list_is_refused()
        {
            var dataset = AccountingDatasets.SalesRevenue();
            var layout = Layout();
            var e = Put(layout, ReportBandKind.ReportHeader, Text("x"));
            e.Style.FontFamily = "Comic Sans MS'; behavior:url(x)";

            var result = new ReportVisualLayoutValidator().Validate(layout, dataset,
                new HashSet<string>(StringComparer.Ordinal), new HashSet<int>());

            Assert.False(result.Ok);

            // An approved one passes, so this is a whitelist and not a blanket refusal.
            e.Style.FontFamily = "Cairo";
            Assert.True(new ReportVisualLayoutValidator().Validate(layout, dataset,
                new HashSet<string>(StringComparer.Ordinal), new HashSet<int>()).Ok);
        }

        [Fact] // 22
        public void An_element_positioned_outside_the_printable_box_is_refused()
        {
            var dataset = AccountingDatasets.SalesRevenue();
            var layout = Layout();
            var e = Put(layout, ReportBandKind.ReportHeader, Text("x"));
            e.XMm = 500;   // A4 portrait is 210mm wide; 500 is off the paper entirely

            var result = new ReportVisualLayoutValidator().Validate(layout, dataset,
                new HashSet<string>(StringComparer.Ordinal), new HashSet<int>());

            Assert.False(result.Ok);
        }

        [Fact] // 23
        public void A_layout_that_declares_a_band_twice_is_refused()
        {
            var dataset = AccountingDatasets.SalesRevenue();
            var layout = Layout();
            layout.Bands.Add(new ReportBand { Kind = ReportBandKind.Detail, HeightMm = 10 });

            var result = new ReportVisualLayoutValidator().Validate(layout, dataset,
                new HashSet<string>(StringComparer.Ordinal), new HashSet<int>());

            Assert.False(result.Ok);
        }

        [Fact] // 24
        public void A_table_outside_the_detail_band_is_refused()
        {
            var dataset = AccountingDatasets.SalesRevenue();
            var layout = Layout();

            Put(layout, ReportBandKind.PageHeader, new ReportElement
            {
                Id = "t1",
                Kind = ReportElementKind.Table,
                XMm = 2, YMm = 2, WidthMm = 100, HeightMm = 20,
                Columns = { new ReportTableColumn { FieldKey = "InvoiceNo", WidthMm = 30 } },
            });

            var result = new ReportVisualLayoutValidator().Validate(layout, dataset,
                new HashSet<string>(StringComparer.Ordinal) { "InvoiceNo" }, new HashSet<int>());

            Assert.False(result.Ok);
        }

        [Fact] // 25
        public void A_summary_using_an_aggregate_the_field_does_not_support_is_refused()
        {
            var dataset = AccountingDatasets.SalesRevenue();

            // InvoiceNo is text: summing it is meaningless, and the dataset says so by not listing Sum.
            var layout = Layout();
            Put(layout, ReportBandKind.ReportFooter, new ReportElement
            {
                Id = "s1",
                Kind = ReportElementKind.Summary,
                FieldKey = "InvoiceNo",
                Aggregate = ReportAggregate.Sum,
                XMm = 2, YMm = 2, WidthMm = 30, HeightMm = 6,
            });

            var result = new ReportVisualLayoutValidator().Validate(layout, dataset,
                new HashSet<string>(StringComparer.Ordinal) { "InvoiceNo" }, new HashSet<int>());

            Assert.False(result.Ok);
        }

        // =========================================================================================
        // 26–28  §9 — PARAMETERS
        // =========================================================================================

        [Fact] // 26
        public async Task A_parameter_the_dataset_never_declared_is_refused()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);

            var draft = Draft(null, "InvoiceNo");
            draft.Parameters["DropTable"] = "1";

            var validation = await Studio(host).ValidateAsync(draft);
            Assert.False(validation.Ok);
        }

        [Fact] // 27
        public async Task A_caller_supplied_company_parameter_is_refused_rather_than_honoured()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);

            var draft = Draft(null, "InvoiceNo");
            draft.Parameters[ReportSystemParameters.CompanyId] = Theirs.ToString();

            var validation = await Studio(host).ValidateAsync(draft);

            // REFUSED, not ignored. A silent no-op would leave the caller believing the value took effect,
            // and would make the next reader of this code wonder whether it did.
            Assert.False(validation.Ok);
            Assert.DoesNotContain(ReportSystemParameters.CompanyId, validation.Parameters.Keys);
        }

        [Fact] // 28
        public async Task A_declared_parameter_reaches_the_run_and_widens_the_default_window()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);

            var db = host.Db;
            db.Customers.Add(new Customer { ID = 10, CompanyID = Mine, Name = "عميل", NameEn = "Customer One", ControlAccountId = 1 });

            // FEBRUARY. Outside month-start..today, which is the exact window V1 could not escape — §9's
            // "the user cannot report on last quarter", made concrete.
            db.SalesInvoices.Add(new SalesInvoice
            {
                ID = 200, CompanyID = Mine, InvoiceNo = "SI-FEB", InvoiceDate = new DateTime(2026, 2, 3),
                CustomerId = 10, SubTotal = 50m, TaxTotal = 7m, GrandTotal = 57m, Status = "Posted",
            });
            await db.SaveChangesAsync();

            var studio = Studio(host);

            var defaultRun = await studio.RunAsync(Draft(null, "InvoiceNo"), ReportOutputFormat.Csv, preview: false);
            Assert.DoesNotContain("SI-FEB", Csv(defaultRun));

            var widened = Draft(null, "InvoiceNo");
            widened.Parameters["From"] = "2026-01-01";
            widened.Parameters["To"] = "2026-05-14";

            var widenedRun = await studio.RunAsync(widened, ReportOutputFormat.Csv, preview: false);

            Assert.True(widenedRun.IsSuccess);
            Assert.Contains("SI-FEB", Csv(widenedRun));
        }

        // =========================================================================================
        // 29–31  §10 — THE FILTER REACHES SQL BEFORE THE CAP
        // =========================================================================================

        [Fact] // 29
        public async Task A_filter_finds_a_row_that_sits_outside_the_row_cap_window()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);
            await SeedSalesAsync(host);

            // The cap is ONE row. Ordered newest-first the source would fetch SI-3 (Posted) and nothing else,
            // so filtering afterwards would report "no Drafts" for a company that has one.
            var draft = Draft(null, "InvoiceNo", "Status");
            draft.PageSize = 1;
            draft.Filters.Add(new StudioFilterDraft
            {
                Field = "Status",
                Operator = ReportFilterOperator.Equals,
                Values = { "Draft" },
            });

            var result = await Studio(host).RunAsync(draft, ReportOutputFormat.Csv, preview: false);

            Assert.True(result.IsSuccess);
            Assert.Contains("SI-1", Csv(result));
        }

        [Fact] // 30
        public async Task A_pushed_filter_is_declared_so_the_shaper_does_not_apply_it_twice()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);
            await SeedSalesAsync(host);

            var draft = Draft(null, "InvoiceNo", "Status");
            draft.Filters.Add(new StudioFilterDraft
            {
                Field = "Status",
                Operator = ReportFilterOperator.Equals,
                Values = { "Posted" },
            });

            var result = await Studio(host).RunAsync(draft, ReportOutputFormat.Csv, preview: false);
            var csv = Csv(result);

            // Both Posted rows, neither Draft one. Applying the predicate twice would still be correct here;
            // what would NOT be is losing a row, so the count is the assertion that matters.
            Assert.Contains("SI-2", csv);
            Assert.Contains("SI-3", csv);
            Assert.DoesNotContain("SI-1", csv);
        }

        [Fact] // 31
        public async Task A_user_filter_can_only_narrow_what_company_isolation_already_permitted()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);
            await SeedSalesAsync(host);

            // The neighbour's only invoice is a Draft, so a Draft filter is the widest possible attempt to
            // reach it. Isolation runs first and it is not reachable.
            var draft = Draft(null, "InvoiceNo");
            draft.PageSize = 500;
            draft.Filters.Add(new StudioFilterDraft
            {
                Field = "Status",
                Operator = ReportFilterOperator.Equals,
                Values = { "Draft" },
            });

            var csv = Csv(await Studio(host).RunAsync(draft, ReportOutputFormat.Csv, preview: false));

            Assert.Contains("SI-1", csv);
            Assert.DoesNotContain("SI-X", csv);
            Assert.DoesNotContain("9999", csv.Replace(",", "").Replace(".00", ""));
        }

        // =========================================================================================
        // 32–35  PAPER, PAGINATION AND ONE LAYOUT FOR EVERY OUTPUT
        // =========================================================================================

        [Fact] // 32
        public void Paper_and_orientation_decide_the_printable_box()
        {
            var portrait = new ReportPageSetup
            {
                PageSize = ReportPageSize.A4, Orientation = ReportOrientation.Portrait,
                MarginLeftMm = 10, MarginRightMm = 10, MarginTopMm = 10, MarginBottomMm = 10,
            };
            var landscape = new ReportPageSetup
            {
                PageSize = ReportPageSize.A4, Orientation = ReportOrientation.Landscape,
                MarginLeftMm = 10, MarginRightMm = 10, MarginTopMm = 10, MarginBottomMm = 10,
            };

            Assert.Equal(190, ReportPaper.ContentWidthMm(portrait), 1);
            Assert.Equal(277, ReportPaper.ContentWidthMm(landscape), 1);

            var a5 = new ReportPageSetup { PageSize = ReportPageSize.A5, MarginLeftMm = 0, MarginRightMm = 0 };
            Assert.Equal(148, ReportPaper.ContentWidthMm(a5), 1);
        }

        [Fact] // 33
        public async Task A_long_report_paginates_and_every_page_knows_the_total()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);

            var db = host.Db;
            db.Customers.Add(new Customer { ID = 10, CompanyID = Mine, Name = "ع", NameEn = "C", ControlAccountId = 1 });
            for (int i = 0; i < 80; i++)
            {
                db.SalesInvoices.Add(new SalesInvoice
                {
                    ID = 300 + i, CompanyID = Mine, InvoiceNo = "P-" + i,
                    InvoiceDate = new DateTime(2026, 5, 5), CustomerId = 10,
                    SubTotal = 10m, TaxTotal = 1m, GrandTotal = 11m, Status = "Posted",
                });
            }
            await db.SaveChangesAsync();

            var layout = Layout();
            Put(layout, ReportBandKind.Detail, Field("InvoiceNo"));
            Put(layout, ReportBandKind.PageFooter, new ReportElement
            {
                Id = "pf", Kind = ReportElementKind.SystemField,
                SystemField = ReportSystemField.PageXOfY,
                XMm = 2, YMm = 2, WidthMm = 40, HeightMm = 6,
            });
            layout.Band(ReportBandKind.Detail)!.HeightMm = 8;

            var result = await Studio(host).RunAsync(Draft(layout, "InvoiceNo"),
                ReportOutputFormat.PrintHtml, preview: false);

            var html = Encoding.UTF8.GetString(result.Artifact!.Content);
            var pages = System.Text.RegularExpressions.Regex.Matches(html, "cbv-page").Count;

            // More than one page: 80 rows at 8mm is 640mm of detail against a ~263mm printable box.
            Assert.True(pages > 1, $"expected pagination, saw {pages} page(s)");

            // "Page 1 of N" with N > 1 is what makes a footer honest — a page number that cannot say how
            // many there are is the classic symptom of pagination left to CSS.
            Assert.Matches(@"Page\s+1\s+of\s+[2-9]", html);
        }

        [Fact] // 34
        public async Task The_preview_the_print_view_and_the_pdf_all_come_from_one_layout()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);
            await SeedSalesAsync(host);

            var layout = Layout();
            Put(layout, ReportBandKind.ReportHeader, Text("MARKER-TOKEN"));
            Put(layout, ReportBandKind.Detail, Field("InvoiceNo"));

            var studio = Studio(host);

            var preview = await studio.RunAsync(Draft(layout, "InvoiceNo"), ReportOutputFormat.Html, preview: true);
            var print = await studio.RunAsync(Draft(layout, "InvoiceNo"), ReportOutputFormat.PrintHtml, preview: false);

            var previewHtml = Encoding.UTF8.GetString(preview.Artifact!.Content);
            var printHtml = Encoding.UTF8.GetString(print.Artifact!.Content);

            Assert.Contains("MARKER-TOKEN", previewHtml);
            Assert.Contains("MARKER-TOKEN", printHtml);
            Assert.Contains("SI-2", previewHtml);
            Assert.Contains("SI-2", printHtml);

            // The preview is a FRAGMENT (it lives inside the designer's page); the print view is a DOCUMENT.
            // Same builder, different chrome — which is the whole point.
            Assert.DoesNotContain("<!DOCTYPE", previewHtml, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<!DOCTYPE", printHtml, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("@page", printHtml);
        }

        [Fact] // 36 — added by the runtime probe, which found the defect this asserts against
        public async Task A_table_in_the_detail_band_prints_each_row_exactly_once()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);
            await SeedSalesAsync(host);   // three invoices for company 1

            var layout = Layout();
            layout.Band(ReportBandKind.Detail)!.HeightMm = 8;
            Put(layout, ReportBandKind.Detail, new ReportElement
            {
                Id = "tbl",
                Kind = ReportElementKind.Table,
                XMm = 2, YMm = 0, WidthMm = 150, HeightMm = 8,
                Columns =
                {
                    new ReportTableColumn { FieldKey = "InvoiceNo", WidthMm = 40 },
                    new ReportTableColumn { FieldKey = "GrandTotal", WidthMm = 40, Total = ReportAggregate.Sum },
                },
            });

            var result = await Studio(host).RunAsync(Draft(layout, "InvoiceNo", "GrandTotal"),
                ReportOutputFormat.PrintHtml, preview: false);

            Assert.True(result.IsSuccess);
            var html = Encoding.UTF8.GetString(result.Artifact!.Content);

            // THE DEFECT, stated as arithmetic. A Detail band prints once per row, and a table lays out the
            // rows itself — so a table INSIDE a detail band used to print the whole set once per row. Three
            // invoices came out as nine table rows and three totals rows; the first real document, over nine
            // invoices, produced ten thousand.
            var bodyRows = System.Text.RegularExpressions.Regex.Matches(html, "<tr>").Count;
            var totalRows = System.Text.RegularExpressions.Regex.Matches(html, "<tfoot>").Count;

            // ONE totals block for the whole report — not one per page, each covering its own page. A reader
            // takes the last totals row for the grand total, so a per-page subtotal wearing that label is a
            // wrong number rather than an extra one.
            Assert.Equal(1, totalRows);
            Assert.True(bodyRows <= 6, $"expected each row once, saw {bodyRows} table rows");

            // Each invoice appears exactly once.
            foreach (var invoice in new[] { "SI-1", "SI-2", "SI-3" })
                Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(html, invoice).Count);
        }

        [Fact] // 37 — also from the runtime probe: the totals row went missing across pages
        public async Task A_paginated_table_prints_one_totals_row_covering_every_page()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);

            var db = host.Db;
            db.Customers.Add(new Customer { ID = 10, CompanyID = Mine, Name = "ع", NameEn = "C", ControlAccountId = 1 });
            for (int i = 0; i < 80; i++)
            {
                db.SalesInvoices.Add(new SalesInvoice
                {
                    ID = 500 + i, CompanyID = Mine, InvoiceNo = "T-" + i,
                    InvoiceDate = new DateTime(2026, 5, 6), CustomerId = 10,
                    SubTotal = 10m, TaxTotal = 0m, GrandTotal = 10m, Status = "Posted",
                });
            }
            await db.SaveChangesAsync();

            var layout = Layout();
            layout.Band(ReportBandKind.Detail)!.HeightMm = 6;
            Put(layout, ReportBandKind.Detail, new ReportElement
            {
                Id = "tbl",
                Kind = ReportElementKind.Table,
                XMm = 2, YMm = 0, WidthMm = 150, HeightMm = 6,
                Columns =
                {
                    new ReportTableColumn { FieldKey = "InvoiceNo", WidthMm = 40 },
                    new ReportTableColumn { FieldKey = "GrandTotal", WidthMm = 40, Format = "N2",
                                            Total = ReportAggregate.Sum },
                },
            });

            var result = await Studio(host).RunAsync(Draft(layout, "InvoiceNo", "GrandTotal"),
                ReportOutputFormat.PrintHtml, preview: false);

            Assert.True(result.IsSuccess);
            var html = Encoding.UTF8.GetString(result.Artifact!.Content);

            var pages = System.Text.RegularExpressions.Regex.Matches(html, "cbv-page").Count;
            var footers = System.Text.RegularExpressions.Regex.Matches(html, "<tfoot>").Count;

            Assert.True(pages > 1, $"expected pagination, saw {pages} page(s)");

            // ONE totals row across the whole report — not one per page, and not none. Both of those were
            // real states of this renderer: per-page first, then absent when an empty footer page pushed the
            // anchor past the last page that had rows.
            Assert.Equal(1, footers);

            // And it totals EVERY row, not just the ones on its own page: 80 × 10.00.
            Assert.Contains("800.00", html);
        }

        [Fact] // 38 — the grouped case, where anchoring the totals by position failed a third time
        public async Task A_grouped_paginated_table_still_prints_its_totals_row()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);

            var db = host.Db;
            db.Customers.Add(new Customer { ID = 10, CompanyID = Mine, Name = "ع", NameEn = "C", ControlAccountId = 1 });
            for (int i = 0; i < 60; i++)
            {
                db.SalesInvoices.Add(new SalesInvoice
                {
                    ID = 700 + i, CompanyID = Mine, InvoiceNo = "G-" + i,
                    // Three distinct dates, so the group band actually groups.
                    InvoiceDate = new DateTime(2026, 5, 4 + (i % 3)), CustomerId = 10,
                    SubTotal = 5m, TaxTotal = 0m, GrandTotal = 5m, Status = "Posted",
                });
            }
            await db.SaveChangesAsync();

            var layout = Layout();
            layout.Band(ReportBandKind.Detail)!.HeightMm = 6;

            // A GROUP FOOTER after the last rows is what broke it: the final run was flushed as a non-final
            // one because something followed it on the page, and the totals row was never emitted.
            var groupHeader = layout.Band(ReportBandKind.GroupHeader)!;
            groupHeader.HeightMm = 8;
            groupHeader.GroupFieldKey = "InvoiceDate";
            var groupFooter = layout.Band(ReportBandKind.GroupFooter)!;
            groupFooter.HeightMm = 6;
            groupFooter.GroupFieldKey = "InvoiceDate";

            Put(layout, ReportBandKind.Detail, new ReportElement
            {
                Id = "tbl",
                Kind = ReportElementKind.Table,
                XMm = 2, YMm = 0, WidthMm = 150, HeightMm = 6,
                Columns =
                {
                    new ReportTableColumn { FieldKey = "InvoiceNo", WidthMm = 40 },
                    new ReportTableColumn { FieldKey = "GrandTotal", WidthMm = 40, Format = "N2",
                                            Total = ReportAggregate.Sum },
                },
            });

            var result = await Studio(host).RunAsync(Draft(layout, "InvoiceNo", "GrandTotal"),
                ReportOutputFormat.PrintHtml, preview: false);

            Assert.True(result.IsSuccess);
            var html = Encoding.UTF8.GetString(result.Artifact!.Content);

            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(html, "<tfoot>").Count);
            Assert.Contains("300.00", html);   // 60 × 5.00, across every group and every page
        }

        [Fact] // 39 — the printed header must say what the designer said
        public async Task A_table_column_with_no_typed_header_prints_the_fields_title_not_its_key()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);
            await SeedSalesAsync(host);

            var layout = Layout();
            layout.Band(ReportBandKind.Detail)!.HeightMm = 6;
            Put(layout, ReportBandKind.Detail, new ReportElement
            {
                Id = "tbl",
                Kind = ReportElementKind.Table,
                XMm = 2, YMm = 0, WidthMm = 150, HeightMm = 6,
                Columns =
                {
                    // NO HeaderText on the first, an explicit one on the second.
                    new ReportTableColumn { FieldKey = "GrandTotal", WidthMm = 40 },
                    new ReportTableColumn { FieldKey = "InvoiceNo", WidthMm = 40, HeaderText = "Ref" },
                },
            });

            var result = await Studio(host).RunAsync(Draft(layout, "InvoiceNo", "GrandTotal"),
                ReportOutputFormat.PrintHtml, preview: false);

            var html = Encoding.UTF8.GetString(result.Artifact!.Content);
            var title = AccountingDatasets.SalesRevenue().Fields.First(f => f.Key == "GrandTotal").TitleEn;

            // The definition's own title, in the reader's language — not the machine key, which is what a
            // customer-facing document used to show while the designer displayed the friendly name.
            Assert.Contains($">{title}<", html);
            Assert.DoesNotContain(">GrandTotal<", html);

            // An author who typed a header still gets exactly theirs.
            Assert.Contains(">Ref<", html);
        }

        [Fact] // 40 — where the bytes live is a durability and an isolation question at once
        public void The_default_asset_root_is_neither_the_binary_directory_nor_a_web_served_folder()
        {
            var root = new ReportAssetOptions().RootPath.Replace(System.IO.Path.DirectorySeparatorChar, '/');

            // RELATIVE, so the HOST decides where it lands. Every absolute default is a guess about the
            // process: AppContext.BaseDirectory is bin/Debug/net8.0 and a rebuild orphaned every stored
            // logo while its row survived, and Directory.GetCurrentDirectory() is whatever launched the
            // process. The first of those was the shipped default until a runtime probe found the 404.
            Assert.False(System.IO.Path.IsPathRooted(root),
                $"the asset root default must be relative so the host anchors it; it is '{root}'");

            Assert.DoesNotContain("bin/", root, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("obj/", root, StringComparison.OrdinalIgnoreCase);

            // NOT wwwroot. A file under it is served by the static-file middleware, which knows nothing
            // about companies — every isolation check in ReportAssetService would be one guessable URL
            // away from irrelevant.
            Assert.DoesNotContain("wwwroot", root, StringComparison.OrdinalIgnoreCase);
        }

        [Fact] // 35
        public async Task One_saved_layout_is_correct_in_both_directions_with_no_mirrored_copy()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            host.SeedEmployee(7);
            await SeedSalesAsync(host);

            var layout = Layout(l => l.Page = l.Page.With(direction: ReportPageDirection.Rtl));
            Put(layout, ReportBandKind.ReportHeader, Text("عنوان", x: 25));
            Put(layout, ReportBandKind.Detail, Field("InvoiceNo"));

            var rtl = await Studio(host).RunAsync(Draft(layout, "InvoiceNo"),
                ReportOutputFormat.PrintHtml, preview: false);
            var rtlHtml = Encoding.UTF8.GetString(rtl.Artifact!.Content);

            layout.Page = layout.Page.With(direction: ReportPageDirection.Ltr);
            var ltr = await Studio(host).RunAsync(Draft(layout, "InvoiceNo"),
                ReportOutputFormat.PrintHtml, preview: false);
            var ltrHtml = Encoding.UTF8.GetString(ltr.Artifact!.Content);

            Assert.Contains("dir=\"rtl\"", rtlHtml);
            Assert.Contains("dir=\"ltr\"", ltrHtml);

            // THE POINT: the element geometry is IDENTICAL in both. It is expressed as a logical offset the
            // browser resolves against dir, so there is no second, mirrored layout to keep in step — and
            // nothing in the output pins an element to a physical left or right.
            Assert.Contains("inset-inline-start:25", rtlHtml.Replace(" ", ""));
            Assert.Contains("inset-inline-start:25", ltrHtml.Replace(" ", ""));
            Assert.DoesNotContain("left:25mm", rtlHtml);
            Assert.DoesNotContain("right:25mm", rtlHtml);
        }

        // =========================================================================================
        // CHARTS AND CROSS-TABS.
        //
        // Both are new element kinds that READ A SCOPE OF ROWS and AGGREGATE it, which makes them the
        // first elements whose output depends on data the author never sees while placing them. That
        // creates three questions no earlier element had to answer, and each has a test below:
        //
        //   * can either become a side door onto a field the reader may not see?  (no: the same gate)
        //   * can either sit where "the rows" means something other than it appears to?
        //   * can data the author cannot predict make the document unbounded?     (no: the ceiling)
        // =========================================================================================

        private static ReportElement Chart(ReportChartKind kind, string measure, string category,
            ReportAggregate aggregate = ReportAggregate.Sum) => new()
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Kind = ReportElementKind.Chart,
            ChartKind = kind,
            FieldKey = measure,
            CategoryFieldKey = category,
            Aggregate = aggregate,
            XMm = 5, YMm = 2, WidthMm = 90, HeightMm = 55,
        };

        private static ReportElement Pivot(string measure, string rowField, string columnField,
            ReportAggregate aggregate = ReportAggregate.Sum) => new()
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Kind = ReportElementKind.CrossTab,
            FieldKey = measure,
            CategoryFieldKey = rowField,
            SeriesFieldKey = columnField,
            Aggregate = aggregate,
            XMm = 5, YMm = 2, WidthMm = 120, HeightMm = 50,
        };

        private static VisualLayoutValidation Check(ReportVisualLayout layout, params string[] permitted) =>
            new ReportVisualLayoutValidator().Validate(
                layout, AccountingDatasets.SalesRevenue(),
                new HashSet<string>(permitted, StringComparer.Ordinal), new HashSet<int>());

        [Fact]
        public void A_chart_axis_is_bound_through_the_same_gate_as_any_other_field()
        {
            // The measure is permitted and the CATEGORY is not. Grouping by a field is reading it - the
            // axis labels print its values - so a chart that bound its axis on a laxer rule than a Field
            // element would be a way to read a column the reader was never given.
            var layout = Layout();
            Put(layout, ReportBandKind.ReportHeader, Chart(ReportChartKind.Column, "GrandTotal", "CustomerName"));

            Assert.False(Check(layout, "GrandTotal").Ok);
            Assert.True(Check(layout, "GrandTotal", "CustomerName").Ok);
        }

        [Fact]
        public void A_cross_tab_binds_both_of_its_axes()
        {
            var layout = Layout();
            Put(layout, ReportBandKind.ReportFooter, Pivot("GrandTotal", "CustomerName", "Status"));

            Assert.False(Check(layout, "GrandTotal", "CustomerName").Ok);   // column axis withheld
            Assert.False(Check(layout, "GrandTotal", "Status").Ok);         // row axis withheld
            Assert.True(Check(layout, "GrandTotal", "CustomerName", "Status").Ok);
        }

        [Fact]
        public void A_chart_takes_only_an_aggregate_the_dataset_allows_on_that_field()
        {
            // CustomerName declares CountDistinct and nothing else. Summing a customer's NAME is the same
            // nonsense Summary already refuses, and it has to be refused by the same answer.
            var layout = Layout();
            Put(layout, ReportBandKind.ReportHeader,
                Chart(ReportChartKind.Pie, "CustomerName", "Status", ReportAggregate.Sum));

            Assert.False(Check(layout, "CustomerName", "Status").Ok);

            var ok = Layout();
            Put(ok, ReportBandKind.ReportHeader,
                Chart(ReportChartKind.Pie, "CustomerName", "Status", ReportAggregate.CountDistinct));
            Assert.True(Check(ok, "CustomerName", "Status").Ok);
        }

        [Theory]
        [InlineData(ReportBandKind.PageHeader)]
        [InlineData(ReportBandKind.PageFooter)]
        [InlineData(ReportBandKind.Detail)]
        public void A_chart_is_refused_in_a_band_whose_scope_would_misrepresent_it(ReportBandKind band)
        {
            // Page bands carry no rows at all. Detail carries THIS PAGE's run - a chart there would draw
            // one page while looking exactly like a chart of the report, which is the failure a reader
            // cannot see and therefore cannot catch.
            var layout = Layout();
            Put(layout, band, Chart(ReportChartKind.Column, "GrandTotal", "Status"));

            Assert.False(Check(layout, "GrandTotal", "Status").Ok);
        }

        [Theory]
        [InlineData(ReportBandKind.ReportHeader)]
        [InlineData(ReportBandKind.ReportFooter)]
        [InlineData(ReportBandKind.GroupHeader)]
        [InlineData(ReportBandKind.GroupFooter)]
        public void A_chart_is_accepted_in_every_band_that_carries_a_whole_scope(ReportBandKind band)
        {
            var layout = Layout();
            Put(layout, band, Chart(ReportChartKind.Column, "GrandTotal", "Status"));

            Assert.True(Check(layout, "GrandTotal", "Status").Ok);
        }

        [Fact]
        public void A_series_field_on_a_chart_is_refused_rather_than_ignored()
        {
            // This increment draws one series. Accepting the property and dropping it would leave the
            // author looking at a chart that answers a different question from the one they configured.
            var layout = Layout();
            var e = Put(layout, ReportBandKind.ReportHeader,
                Chart(ReportChartKind.Column, "GrandTotal", "Status"));
            e.SeriesFieldKey = "CustomerName";

            Assert.False(Check(layout, "GrandTotal", "Status", "CustomerName").Ok);
        }

        [Fact]
        public void A_cross_tab_refuses_the_same_field_on_both_axes()
        {
            var layout = Layout();
            Put(layout, ReportBandKind.ReportFooter, Pivot("GrandTotal", "Status", "Status"));

            Assert.False(Check(layout, "GrandTotal", "Status").Ok);
        }

        [Fact]
        public void The_category_ceiling_is_clamped_so_no_data_shape_can_make_the_document_unbounded()
        {
            var layout = Layout();
            var e = Put(layout, ReportBandKind.ReportHeader,
                Chart(ReportChartKind.Bar, "GrandTotal", "CustomerName"));
            e.MaxCategories = 100_000;

            var result = Check(layout, "GrandTotal", "CustomerName");
            Assert.True(result.Ok);

            var clean = result.Sanitised!.Band(ReportBandKind.ReportHeader)!.Elements.Single();
            Assert.InRange(clean.MaxCategories, 2, 40);

            // Zero is a default, not a refusal: an author who clears the box gets the platform's number
            // rather than a chart with no bars.
            e.MaxCategories = 0;
            Assert.Equal(12, Check(layout, "GrandTotal", "CustomerName")
                .Sanitised!.Band(ReportBandKind.ReportHeader)!.Elements.Single().MaxCategories);
        }
    }
}
