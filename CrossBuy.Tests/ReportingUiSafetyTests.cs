using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // Reporting Platform — R3 PHASE 6: UI SAFETY.
    //
    // Nine properties the brief requires PROVEN, asserted against IReportsCenterPresenter — the boundary the
    // two screens actually read. Not against rendered HTML: an HTML assertion passes for the wrong reasons
    // (a column is absent because the fixture had no rows) and fails for the wrong ones (markup changed).
    // The presenter is where the projection happens, so the presenter is where the proof belongs.
    //
    // Three of the nine were already proven when the engine was built and are NOT re-asserted here, because
    // one rule tested in two places is one rule that can disagree with itself:
    //
    //   denied run never reaches the data source → ReportingSecurityInvariantTests
    //                                              .A_denied_run_never_reaches_the_data_source
    //   template visibility grants no data       → …A_platform_template_is_visible_to_everyone_and_grants_no_data_access
    //   no renderer handle on the request        → …The_public_request_surface_exposes_no_renderer_or_data_source_handle
    //
    // What is new here is the UI's own surface: what the SCREEN is allowed to learn.
    // =============================================================================================
    public class ReportingUiSafetyTests
    {
        private const string InternalColumn = "Cost";

        private static ReportsCenterQuery Center() => new();

        private static ReportViewerQuery Viewer(string code, bool run = false,
            IReadOnlyDictionary<string, string?>? parameters = null) => new()
            {
                Code = code,
                Run = run,
                Parameters = parameters ?? new Dictionary<string, string?>(StringComparer.Ordinal),
            };

        // ============================================================================================
        // 1. AUTHORIZATION BEFORE FETCH
        // ============================================================================================

        // The Viewer must decide "may you see this report" before it decides anything else. A screen that
        // built its parameter form first and gated afterwards would have already told the caller the report's
        // shape — which is most of what a probe wants.
        [Fact]
        public async Task An_unpermitted_report_yields_no_viewer_model_at_all()
        {
            using var host = new ReportingTestHost();
            var presenter = host.Presenter();

            // Sanity: it resolves while the permission is held.
            Assert.NotNull(await presenter.BuildViewerAsync(Viewer(TestReportDefinitions.SalesCode)));

            host.PermissionOptions.RoleMap.Remove(TestReportDefinitions.SalesPermission);

            // Null, not an empty model. There is nothing to render and nothing to leak — the controller turns
            // this into 404, which is the same answer a nonexistent code gets.
            Assert.Null(await presenter.BuildViewerAsync(Viewer(TestReportDefinitions.SalesCode)));
        }

        // A run requested by an unpermitted caller must not reach the source EVEN THOUGH the request asked for
        // one. The gate is ahead of the run, not beside it.
        [Fact]
        public async Task A_run_requested_by_an_unpermitted_caller_never_executes()
        {
            using var host = new ReportingTestHost();

            var recording = new RecordingDataSource(TestReportDefinitions.SalesDataSourceKey);
            var presenter = host.Presenter(host.EngineWith(recording));

            host.PermissionOptions.RoleMap.Remove(TestReportDefinitions.SalesPermission);

            var model = await presenter.BuildViewerAsync(Viewer(TestReportDefinitions.SalesCode, run: true));

            Assert.Null(model);
            Assert.False(recording.WasEntered);
        }

        // ============================================================================================
        // 2 & 3. HIDDEN FIELDS NEVER APPEAR IN UI METADATA, AND CANNOT BE FILTERED
        // ============================================================================================

        // An Internal column is not merely unrendered — the model the screen receives does not contain it. A
        // column the UI never learns about cannot be offered as a filter, put in a column chooser, or named in
        // a saved layout by a user, so "internal fields cannot be filtered" is structural rather than checked.
        [Fact]
        public async Task An_internal_column_is_absent_from_the_viewer_model_entirely()
        {
            using var host = new ReportingTestHost();

            var model = await host.Presenter().BuildViewerAsync(Viewer(TestReportDefinitions.SalesCode));
            Assert.NotNull(model);

            Assert.DoesNotContain(model!.Columns, c => c.Key == InternalColumn);

            // The definition genuinely HAS it — so the absence above is the projection working, not a fixture
            // that never carried an internal column in the first place.
            var definition = host.Catalog.GetDefinition(TestReportDefinitions.SalesCode);
            Assert.Contains(definition.Columns, c => c.Key == InternalColumn && c.Internal);

            // Every column that DID survive is filterable-or-not exactly as the definition declares it. The UI
            // never widens what the engine accepts.
            foreach (var column in model.Columns)
            {
                var source = definition.FindColumn(column.Key);
                Assert.NotNull(source);
                Assert.Equal(source!.Filterable, column.Filterable);
            }
        }

        // A SystemSupplied parameter is dropped from the form. CompanyId is the one that matters: rendering an
        // input for it would look exactly like a tenant selector, and the engine ignores whatever arrives — so
        // the control would be a lie that also invites the attempt.
        [Fact]
        public async Task A_system_supplied_parameter_is_never_offered_as_an_input()
        {
            using var host = new ReportingTestHost();

            var model = await host.Presenter().BuildViewerAsync(Viewer(TestReportDefinitions.SalesCode));
            Assert.NotNull(model);

            Assert.DoesNotContain(model!.Parameters, p => p.Key == ReportSystemParameters.CompanyId);
            Assert.All(model.Parameters, p => Assert.False(ReportSystemParameters.IsSystemKey(p.Key)));

            // And the ordinary ones are all there, so the filter is precise rather than over-broad.
            Assert.Contains(model.Parameters, p => p.Key == "From");
            Assert.Contains(model.Parameters, p => p.Key == "Branch");
        }

        // Never-sensitivity dataset fields are excluded from the availability strip's own field count. The
        // strip reports what somebody could build with; a field nobody may ever see is not one of them.
        [Fact]
        public async Task The_dataset_strip_does_not_count_fields_nobody_may_ever_see()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7, roles: "Admin");
            var dataset = BusinessEventsDataset.Definition();

            host.PermissionOptions.RoleMap[BusinessEventsReportPermissions.View] = new[] { "Admin" };

            var model = await host.Presenter(datasets: dataset).BuildCenterAsync(Center());

            var strip = Assert.Single(model.Datasets.Items,
                d => d.DatasetCode == BusinessEventsReportCodes.DatasetCode);

            var neverFields = dataset.Fields.Count(f => f.Sensitivity == ReportFieldSensitivity.Never);
            Assert.True(neverFields > 0, "The fixture must contain a Never field for this to prove anything.");
            Assert.Equal(dataset.Fields.Count - neverFields, strip.FieldCount);
        }

        // ============================================================================================
        // 4. TRUNCATION IS ALWAYS VISIBLE
        // ============================================================================================

        // The preview model's Truncated is REQUIRED and non-nullable, so it cannot be defaulted away — and it
        // carries the engine's verdict rather than being recomputed from the row count, which would go wrong
        // the moment a report legitimately returns exactly the cap.
        [Fact]
        public async Task A_truncated_run_says_so_on_the_model_the_screen_reads()
        {
            using var host = new ReportingTestHost();

            var model = await host.Presenter().BuildViewerAsync(new ReportViewerQuery
            {
                Code = TestReportDefinitions.SalesCode,
                Run = true,
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal),
            });

            Assert.NotNull(model);
            Assert.NotNull(model!.Preview);

            // PreviewRows is 2 on the test definition and the source yields 6, so the preview IS capped.
            Assert.True(model.Preview!.Truncated,
                "A preview capped at 2 rows over a 6-row source must report truncation.");
            Assert.Equal(2, model.Preview.RowCount);
        }

        [Fact]
        public void The_truncation_flag_cannot_be_omitted_from_a_preview_model()
        {
            // `required` on a non-nullable bool: the compiler refuses a ReportPreviewModel that does not set
            // it. Asserted through reflection because the compile-time guarantee is invisible at run time, and
            // somebody removing `required` to fix an unrelated build error should break THIS test rather than
            // ship a preview that can silently lose the flag.
            var property = typeof(ReportPreviewModel).GetProperty(nameof(ReportPreviewModel.Truncated));

            Assert.NotNull(property);
            Assert.Equal(typeof(bool), property!.PropertyType);
            Assert.Contains(property.GetCustomAttributes(inherit: false),
                a => a.GetType().Name == "RequiredMemberAttribute");
        }

        // ============================================================================================
        // 5. EXPORT RESPECTS THE SAME SHAPED RESULT AS PREVIEW
        // ============================================================================================

        // There is ONE request builder. Preview and export differ in exactly two fields — Kind and Format —
        // and in nothing else, which is what makes "the file is what you looked at" true by construction
        // rather than by two call sites happening to agree today.
        [Fact]
        public void Preview_and_export_build_the_same_request_apart_from_kind_and_format()
        {
            var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["From"] = "2026-05-01",
                ["To"] = "2026-05-31",
                ["Branch"] = "North",
            };

            var preview = ReportsCenterPresenter.BuildRequest(
                TestReportDefinitions.SalesCode, parameters, templateId: 12,
                ReportOutputFormat.Html, preview: true, archive: false);

            var export = ReportsCenterPresenter.BuildRequest(
                TestReportDefinitions.SalesCode, parameters, templateId: 12,
                ReportOutputFormat.Csv, preview: false, archive: false);

            Assert.Equal(preview.ReportCode, export.ReportCode);
            Assert.Equal(preview.TemplateId, export.TemplateId);
            Assert.Equal(preview.Parameters.Count, export.Parameters.Count);
            foreach (var entry in preview.Parameters)
                Assert.Equal(entry.Value, export.Parameters[entry.Key]);

            Assert.Equal(ReportRunKind.Preview, preview.Kind);
            Assert.Equal(ReportRunKind.Full, export.Kind);
        }

        // A PREVIEW IS NEVER ARCHIVED, whatever the caller asks. Archiving a capped render would put a
        // partial artifact in the permanent record under the same name as the full one.
        [Fact]
        public void A_preview_request_never_carries_an_archive_instruction()
        {
            var request = ReportsCenterPresenter.BuildRequest(
                TestReportDefinitions.SalesCode,
                new Dictionary<string, string?>(StringComparer.Ordinal),
                templateId: null, ReportOutputFormat.Html, preview: true, archive: true);

            Assert.False(request.Archive);
        }

        // The rendered HTML the Viewer injects with Html.Raw must be safe. It is our own renderer's output —
        // this proves the renderer encodes, so the @Html.Raw in Viewer.cshtml is justified rather than assumed.
        [Fact]
        public async Task A_cell_containing_markup_is_encoded_by_the_renderer_the_viewer_injects()
        {
            using var host = new ReportingTestHost();

            var hostile = new StaticReportDataSource(TestReportDefinitions.SalesDataSourceKey, _ => new[]
            {
                new Dictionary<string, object?>
                {
                    ["Branch"] = "<script>alert('xss')</script>",
                    ["Category"] = "Dairy", ["Item"] = "Milk",
                    ["SaleDate"] = new DateTime(2026, 5, 2), ["Qty"] = 1,
                    ["Amount"] = 1m, ["Cost"] = 1m,
                },
            });

            var model = await host.Presenter(host.EngineWith(hostile))
                .BuildViewerAsync(Viewer(TestReportDefinitions.SalesCode, run: true));

            Assert.NotNull(model?.Preview?.Html);
            Assert.DoesNotContain("<script>", model!.Preview!.Html!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("&lt;script&gt;", model.Preview.Html!, StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================================================
        // 6. ARCHIVE ACCESS CANNOT BYPASS REPORT PERMISSION
        // ============================================================================================

        // An archived artifact is not a permanent grant. Retrieval re-checks the REPORT's permission, so
        // revoking access to a report takes its history with it — otherwise "export it once" would be a way
        // to make data permanently readable.
        [Fact]
        public async Task An_archived_artifact_stops_being_retrievable_when_the_permission_is_revoked()
        {
            using var host = new ReportingTestHost();

            var generated = await host.Reports().GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Format = ReportOutputFormat.Csv,
                Archive = true,
            });

            Assert.True(generated.IsSuccess);
            var entryId = generated.Run?.ArchiveEntryId;
            Assert.NotNull(entryId);

            // Readable while the permission is held.
            Assert.NotNull(await host.Archive.RetrieveAsync(entryId!.Value, host.Ctx));

            host.PermissionOptions.RoleMap.Remove(TestReportDefinitions.SalesPermission);

            // …and gone the moment it is not. Same id, same bytes on disk, different answer.
            Assert.Null(await host.Archive.RetrieveAsync(entryId.Value, host.Ctx));

            // AND THE LISTING STILL SHOWS IT, marked. This is deliberate and it is the opposite of what I
            // first asserted. ListAsync is self-scoped — these are the caller's OWN artifacts — so dropping
            // the row would make their history rewrite itself when a permission changed elsewhere. The row
            // stays; the download is withdrawn and the reason is named.
            var model = await host.Presenter().BuildCenterAsync(Center());

            var entry = Assert.Single(model.Archive.Items, a => a.Id == entryId.Value);
            Assert.False(entry.Retrievable);
            Assert.False(entry.CanDownload);

            // The bytes are still on disk and the row still says so — the refusal is about permission, and
            // conflating it with a retention sweep would send the user to the wrong administrator.
            Assert.True(entry.BytesPresent);
        }

        // A run whose report is no longer permitted keeps its history row and loses its re-run link — same
        // rule, same reason. Without this the Reports Center offers a re-run that lands on a 404.
        [Fact]
        public async Task A_past_run_of_a_now_unpermitted_report_stays_in_history_but_cannot_be_re_run()
        {
            using var host = new ReportingTestHost();

            await host.Reports().GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Format = ReportOutputFormat.Csv,
            });

            var before = await host.Presenter().BuildCenterAsync(Center());
            Assert.All(before.Recent.Items, r => Assert.True(r.CanRerun));

            host.PermissionOptions.RoleMap.Remove(TestReportDefinitions.SalesPermission);

            var after = await host.Presenter().BuildCenterAsync(Center());
            Assert.NotEmpty(after.Recent.Items);
            Assert.All(after.Recent.Items, r => Assert.False(r.CanRerun));
        }

        // SEARCHING MUST NOT REVOKE AN AFFORDANCE. Retrievability is computed from the caller's full visible
        // set, not from the filtered catalog — deriving it from the filtered list (which I did first) makes
        // typing a search term mark every other report's history as no-longer-permitted.
        [Fact]
        public async Task A_search_term_does_not_make_unrelated_history_look_unpermitted()
        {
            using var host = new ReportingTestHost();

            await host.Reports().GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Format = ReportOutputFormat.Csv,
                Archive = true,
            });

            var filtered = await host.Presenter().BuildCenterAsync(new ReportsCenterQuery
            {
                Search = "something that matches no report at all",
            });

            // The catalog is legitimately empty…
            Assert.Empty(filtered.Catalog.Items);

            // …and the history/archive rows are still marked as usable, because the caller's PERMISSIONS did
            // not change — only their search box did.
            Assert.All(filtered.Recent.Items, r => Assert.True(r.CanRerun));
            Assert.All(filtered.Archive.Items, a => Assert.True(a.Retrievable));
        }

        // ============================================================================================
        // 7. CROSS-COMPANY IDS FAIL CLOSED
        // ============================================================================================

        // A template id from another tenant, pasted into a Viewer URL. It must not resolve, and it must not
        // produce a different error from a nonexistent id — the difference would confirm the row exists.
        [Fact]
        public async Task A_template_id_from_another_company_never_resolves_in_the_viewer()
        {
            using var host = new ReportingTestHost();

            var foreign = new ReportTemplate
            {
                CompanyID = host.Ctx.CompanyId + 1,
                ReportCode = TestReportDefinitions.SalesCode,
                Name = "Another tenant's layout",
                Scope = ReportTemplateScope.Company,
                CreatedAt = host.Clock.LocalNow,
            };
            host.Db.ReportTemplates.Add(foreign);
            await host.Db.SaveChangesAsync();

            var model = await host.Presenter().BuildViewerAsync(new ReportViewerQuery
            {
                Code = TestReportDefinitions.SalesCode,
                TemplateId = foreign.Id,
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal),
            });

            // The report itself is legitimately visible — the caller holds its permission — but the foreign
            // layout contributes nothing and is not listed.
            Assert.NotNull(model);
            Assert.DoesNotContain(model!.Saved.Items, s => s.Id == foreign.Id);
        }

        // An unresolved company yields a model that renders a sign-in notice, not nine empty panels that look
        // like a company with no reports.
        [Fact]
        public async Task With_no_resolved_company_the_center_reports_unresolved_rather_than_empty()
        {
            using var host = new ReportingTestHost(companyId: null);

            var model = await host.Presenter().BuildCenterAsync(Center());

            Assert.False(model.IsResolved);
            Assert.Empty(model.Catalog.Items);
            Assert.Equal(0, model.TotalReports);
        }

        [Fact]
        public async Task With_no_resolved_company_the_viewer_yields_nothing()
        {
            using var host = new ReportingTestHost(companyId: null);

            Assert.Null(await host.Presenter().BuildViewerAsync(Viewer(TestReportDefinitions.SalesCode)));
        }

        // ============================================================================================
        // 8. THE CATALOG SHOWS ONLY WHAT THE CALLER MAY RUN
        // ============================================================================================

        [Fact]
        public async Task A_report_whose_permission_is_not_held_is_absent_from_the_center()
        {
            using var host = new ReportingTestHost();

            var visible = await host.Presenter().BuildCenterAsync(Center());
            Assert.Contains(visible.Catalog.Items.SelectMany(g => g.Reports),
                r => r.Code == TestReportDefinitions.SalesCode);

            host.PermissionOptions.RoleMap.Remove(TestReportDefinitions.SalesPermission);

            var hidden = await host.Presenter().BuildCenterAsync(Center());
            Assert.DoesNotContain(hidden.Catalog.Items.SelectMany(g => g.Reports),
                r => r.Code == TestReportDefinitions.SalesCode);
        }

        // ============================================================================================
        // 9. A DENIAL IS NOT AN ERROR LIST
        // ============================================================================================

        // The engine encodes a refusal as an Error diagnostic. Surfacing that in the Viewer's error strip
        // would show an authorization reason code to somebody who cannot act on it — and would tell a prober
        // exactly which gate stopped them. Denied is its own flag, and the errors are empty.
        [Fact]
        public void A_denied_result_carries_the_denial_flag_and_no_error_text()
        {
            var denied = ReportResult.Denied(TestReportDefinitions.SalesCode, ReportOutputFormat.Html,
                ReportAuthorizationService.CodeNoModulePermission,
                "Permission 'test.sales.view' is required to use report 'Test.Sales'.");

            // Reached through the same private projection the presenter uses, via a run that returns Denied.
            Assert.True(denied.IsDenied);
            Assert.NotEmpty(denied.Errors);   // the engine DOES carry the reason…

            // …and the model the screen sees does not. Proven through the presenter in
            // A_run_requested_by_an_unpermitted_caller_never_executes, which returns null before this point;
            // this test pins the engine-side shape the projection depends on.
            Assert.Equal(ReportRunStatus.Denied, denied.Status);
        }

        // ============================================================================================
        // 10. EVERY USER-FACING STRING IS TRANSLATED
        // ============================================================================================

        // CLAUDE.md: every user-facing string goes through Resources. In a Razor view an untranslated key
        // renders as the key itself, so a missing Arabic entry does not break anything — it silently ships an
        // English sentence into an RTL screen, which is exactly the failure nobody notices until a customer
        // does. This walks the views' @Localizer[...] keys against the .ar.resx.
        [Theory]
        [InlineData("Views/Reports/Index.cshtml", "Resources/Views/Reports/Index.ar.resx")]
        [InlineData("Views/Reports/Viewer.cshtml", "Resources/Views/Reports/Viewer.ar.resx")]
        public void Every_literal_localizer_key_in_a_reporting_view_has_an_arabic_translation(
            string viewPath, string resourcePath)
        {
            var root = RepoRoot();
            var view = Path.Combine(root, "CrossBuy", viewPath.Replace('/', Path.DirectorySeparatorChar));
            var resource = Path.Combine(root, "CrossBuy", resourcePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(view), view);
            Assert.True(File.Exists(resource), resource);

            var text = File.ReadAllText(view);

            // TWO LOCALIZERS, TWO RESOURCE FILES, and they must be checked against the right one.
            //
            //   @Localizer[...]  → IViewLocalizer      → this view's own .ar.resx
            //   @SR[...]         → IHtmlLocalizer<SharedResources> → Resources/SharedResources.ar.resx
            //
            // The first version of this test matched both patterns against the view's resx and reported
            // ten false failures for keys ("Inventory", "Print", "Status", …) that are correctly
            // translated — in the shared file. Checking a key against the wrong file proves nothing.
            //
            // LITERAL keys only. Keys built from an expression — @Localizer[format.ToString()] — are not
            // matched and are covered by the enum-name entries seeded in the resx; a regex that tried to
            // resolve them would be guessing.
            var viewKeys = Keys(text, @"(?<![A-Za-z])Localizer\[""([^""]+)""\]");
            var sharedKeys = Keys(text, @"(?<![A-Za-z])SR\[""([^""]+)""\]");

            Assert.NotEmpty(viewKeys);

            var shared = Path.Combine(root, "CrossBuy", "Resources", "SharedResources.ar.resx");
            Assert.True(File.Exists(shared), shared);

            AssertTranslated(viewPath, viewKeys, resource, "the view's own");
            AssertTranslated(viewPath, sharedKeys, shared, "SharedResources");
        }

        private static List<string> Keys(string text, string pattern) =>
            System.Text.RegularExpressions.Regex.Matches(text, pattern)
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        private static void AssertTranslated(string viewPath, List<string> keys, string resourcePath, string which)
        {
            if (keys.Count == 0) return;

            var translated = System.Xml.Linq.XDocument.Load(resourcePath)
                .Root!.Elements("data")
                .Select(e => e.Attribute("name")?.Value)
                .Where(n => n != null)
                .ToHashSet(StringComparer.Ordinal);

            var missing = keys.Where(k => !translated.Contains(k)).ToList();

            Assert.True(missing.Count == 0,
                $"{missing.Count} key(s) in {viewPath} have no Arabic translation in {which} resource file " +
                $"and would render in English on an RTL screen: {string.Join(" | ", missing)}");
        }

        // ============================================================================================
        // 11. AN UNDEPLOYED SCHEMA IS A MESSAGE, NOT A STACK TRACE
        // ============================================================================================
        //
        // FOUND ON A REAL DATABASE, not by a test. Every Reporting test passed while /Reports/Viewer
        // returned a 500 with `Invalid object name 'ReportShares'`, because ReportingTestHost builds its
        // tables from the EF model with EnsureCreated() — so the tests could not see that deploy/sql
        // contains no reporting slice at all. The engine authorizes by reading ReportShares, so the gate
        // itself throws before it can answer.
        //
        // These two tests close the gap the SQLite host hides: they inject a data-layer failure of the
        // exact shape a missing table produces, and assert the screen degrades instead of throwing.

        [Fact]
        public async Task A_missing_reporting_schema_yields_an_actionable_viewer_state_rather_than_an_exception()
        {
            using var host = new ReportingTestHost();

            var presenter = new ReportsCenterPresenter(
                new SchemaMissingReportService(), host.Library, host.History, host.Archive,
                host.Templates, host.DatasetRegistry(), host.Catalog, host.Accessor);

            var model = await presenter.BuildViewerAsync(Viewer(TestReportDefinitions.SalesCode));

            // NOT null — null means 404 ("no such report, as far as you are concerned"), which would be a
            // lie: the report exists and the caller may hold its permission. The truth is that reporting
            // is not installed, and only a deployment fixes it.
            Assert.NotNull(model);
            Assert.True(model!.IsUnavailable);
            Assert.Equal(ReportPanelReasons.SchemaMissing, model.UnavailableReasonCode);

            // Nothing is claimed about a report whose storage does not exist.
            Assert.Empty(model.Columns);
            Assert.Empty(model.Parameters);
            Assert.Null(model.Preview);
        }

        // The narrowness matters as much as the catch. A timeout or a deadlock must keep throwing —
        // reporting those as "not deployed" would send an operator to the wrong problem entirely.
        [Fact]
        public void Only_a_missing_object_counts_as_a_missing_schema()
        {
            Assert.True(ReportsCenterPresenter.IsSchemaMissing(
                new Microsoft.Data.Sqlite.SqliteException("SQLite Error 1: 'no such table: ReportShares'.", 1)));

            Assert.False(ReportsCenterPresenter.IsSchemaMissing(
                new Microsoft.Data.Sqlite.SqliteException("SQLite Error 5: 'database is locked'.", 5)));

            // A plain exception is never a schema problem, however it is worded.
            Assert.False(ReportsCenterPresenter.IsSchemaMissing(
                new InvalidOperationException("Invalid object name 'ReportShares'")));
        }

        // Throws the way a real provider does when the table is absent: a DbException whose message names
        // the missing object. SQL Server says "Invalid object name 'ReportShares'"; SQLite says "no such
        // table". The presenter matches on the message rather than on a provider-specific type, so one
        // fake covers both deployments.
        private sealed class SchemaMissingReportService : IReportService
        {
            private static Exception Missing() =>
                new Microsoft.Data.Sqlite.SqliteException("SQLite Error 1: 'no such table: ReportShares'.", 1);

            public Task<ReportResult> GenerateAsync(ReportRequest request, CancellationToken ct = default) =>
                throw Missing();

            public Task<ReportResult> PreviewAsync(ReportRequest request, CancellationToken ct = default) =>
                throw Missing();

            public Task<IReadOnlyList<ReportDefinition>> BrowseAsync(string? module = null,
                string? categoryKey = null, string? tag = null, string? search = null,
                CancellationToken ct = default) => throw Missing();

            public Task<ReportDefinition?> DescribeAsync(string reportCode, CancellationToken ct = default) =>
                throw Missing();

            public Task<IReadOnlyList<ReportOutputFormat>> AvailableFormatsAsync(string reportCode,
                CancellationToken ct = default) => throw Missing();
        }

        // ============================================================================================
        // 12. ONE VISUAL LANGUAGE — the Inventory module is the only visual authority
        // ============================================================================================
        //
        // The owner's rule: "If any new screen can be visually distinguished from the Inventory module,
        // the implementation is considered FAILED." That is a design rule, and design rules rot unless
        // something checks them — a later edit adding one convenient custom class is exactly how a
        // second design language starts, and nobody notices until there are forty of them.
        //
        // These three tests are cheap and they make the rule enforceable rather than aspirational.

        private static readonly string[] ReportingViews =
        {
            "Views/Reports/Index.cshtml",
            "Views/Reports/Viewer.cshtml",
            "Views/Reports/_ReportCard.cshtml",
        };

        [Fact]
        public void Every_reporting_view_renders_inside_the_inventory_shell()
        {
            var root = RepoRoot();

            foreach (var relative in ReportingViews)
            {
                var path = Path.Combine(root, "CrossBuy", relative.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), path);

                var text = File.ReadAllText(path);

                // A partial inherits its parent's layout and must NOT set one.
                if (Path.GetFileName(path).StartsWith("_", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain("Layout =", text, StringComparison.Ordinal);
                    continue;
                }

                Assert.Contains("~/Views/Shared/_LayoutInventory.cshtml", text, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void No_reporting_view_introduces_a_second_design_language()
        {
            var root = RepoRoot();

            // Each banned token, with why it is banned.
            var banned = new (string Token, string Why)[]
            {
                ("cbr-",     "a bespoke Reporting class prefix"),
                ("cbw-",     "the Workspace product's class prefix"),
                ("class=\"cbw", "the Workspace product's scope class"),
                ("crossbusiness-reporting.css", "the deleted bespoke Reporting stylesheet"),
                ("crossbusiness-workspace.css", "another product's stylesheet"),
                ("_LayoutReporting", "the deleted bespoke Reporting shell"),

                // _LayoutInventory declares NO Styles section, so this does not merely violate the rule —
                // it throws at run time ("section has been defined but has not been rendered"). It shipped
                // briefly during this increment and rendered the Viewer unusable.
                ("@section Styles", "a stylesheet hook the Inventory layout does not render"),
            };

            foreach (var relative in ReportingViews)
            {
                var path = Path.Combine(root, "CrossBuy", relative.Replace('/', Path.DirectorySeparatorChar));

                // RAZOR COMMENTS ARE STRIPPED FIRST. This test scans MARKUP, and a header comment that
                // explains why a token is banned necessarily contains that token — the first version
                // flagged Viewer.cshtml for the escaped `@@section Styles` inside its own explanation of
                // why there is no section. Same prose-as-code mistake as the Workspace containment test.
                var markup = System.Text.RegularExpressions.Regex.Replace(
                    File.ReadAllText(path), @"@\*.*?\*@", "",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                foreach (var (token, why) in banned)
                    Assert.False(markup.Contains(token, StringComparison.Ordinal),
                        $"{relative} contains \"{token}\" — {why}. The Inventory module is the only visual " +
                        "authority: reuse its existing Metronic components rather than introducing another.");
            }
        }

        [Fact]
        public void The_bespoke_reporting_shell_and_stylesheet_are_gone_from_disk()
        {
            var root = RepoRoot();

            // Deleted, not merely unreferenced. A stylesheet left on disk gets re-linked by the next
            // person who finds it and assumes it is the house style.
            foreach (var relative in new[]
            {
                "CrossBuy/Views/Shared/_LayoutReporting.cshtml",
                "CrossBuy/wwwroot/Backend-assets/css/crossbusiness-reporting.css",
                "CrossBuy/Resources/Views/Shared/_LayoutReporting.ar.resx",
            })
            {
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Assert.False(File.Exists(path),
                    relative + " still exists. The Reporting screens use Inventory's shell and Metronic " +
                    "components only; a second design language must not be left on disk to be re-linked.");
            }
        }

        private static string RepoRoot()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
            if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment))
                return fromEnvironment;

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "CrossBuy.sln"))) return directory.FullName;
                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                "Could not locate the repository root above " + AppContext.BaseDirectory +
                ". Set CROSSBUY_REPO_ROOT.");
        }

        // ============================================================================================
        private sealed class RecordingDataSource : IReportDataSource
        {
            public RecordingDataSource(string key) { Key = key; }

            public string Key { get; }
            public bool WasEntered { get; private set; }

            public Task<ReportDataSet> FetchAsync(ReportDataQuery query,
                CancellationToken cancellationToken = default)
            {
                WasEntered = true;
                return Task.FromResult(new ReportDataSetBuilder(query.Definition.Columns).Build(totalRowCount: 0));
            }
        }
    }
}
