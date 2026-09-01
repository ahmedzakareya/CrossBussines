using System.Text;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // Reporting Platform (ADR-037) — THE PIPELINE.
    //
    // These are the tests that justify the façade existing: authorization happens BEFORE any data is read, and
    // history is written on EVERY path — success, failure and denial. A module calling a renderer directly would
    // skip both, which is the whole reason IReportService is the only public door.
    public class ReportingEngineTests
    {
        // Culture is passed EXPLICITLY. Relying on the test runner's ambient CurrentUICulture would make an
        // assertion about Arabic output pass or fail depending on the machine's regional settings.
        private const string Arabic = "ar-KW";

        private static ReportRequest Request(ReportOutputFormat format = ReportOutputFormat.Html) => new()
        {
            ReportCode = TestReportDefinitions.SalesCode,
            Format = format,
            Culture = Arabic,
        };

        // ================================================================================================
        // 1. THE HAPPY PATH, END TO END
        // ================================================================================================

        [Fact]
        public async Task A_report_generates_and_records_a_run()
        {
            using var host = new ReportingTestHost();

            var result = await host.Engine.GenerateAsync(Request(), host.Ctx);

            Assert.True(result.IsSuccess);
            Assert.Equal(ReportRunStatus.Succeeded, result.Status);
            Assert.Equal(6, result.Run!.RowCount);
            Assert.False(result.Run!.Truncated);
            Assert.NotNull(result.Artifact);
            Assert.Contains("تقرير المبيعات", result.Artifact!.AsText());

            // Read back from a NEW query, not from the object that wrote it.
            var run = await host.Db.ReportRuns.AsNoTracking().SingleAsync(r => r.Id == result.Run!.RunId);
            Assert.Equal(TestReportDefinitions.SalesCode, run.ReportCode);
            Assert.Equal(ReportRunStatus.Succeeded, run.Status);
            Assert.Equal("Html", run.Format);
            Assert.Equal(6, run.RowCount);
            Assert.Equal(1, run.CompanyID);
            Assert.Equal(7, run.EmployeeId);
            Assert.NotNull(run.CompletedAt);
        }

        [Fact]
        public async Task Every_registered_format_can_be_produced_through_the_one_call()
        {
            using var host = new ReportingTestHost();

            foreach (var format in new[]
                     {
                         ReportOutputFormat.Html, ReportOutputFormat.PrintHtml,
                         ReportOutputFormat.Csv, ReportOutputFormat.Xlsx,
                     })
            {
                var result = await host.Engine.GenerateAsync(Request(format), host.Ctx);

                Assert.True(result.IsSuccess, $"{format} failed: " +
                    string.Join("; ", result.Diagnostics.Select(d => d.ToString())));
                Assert.Equal(ReportFormats.ContentType(format), result.Artifact!.ContentType);
            }
        }

        // ================================================================================================
        // 2. AUTHORIZATION HAPPENS BEFORE ANY DATA IS READ — AND A DENIAL IS RECORDED
        // ================================================================================================

        [Fact]
        public async Task A_caller_without_the_module_permission_is_denied_and_the_denial_is_recorded()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7, roles: "Warehouse");

            var result = await host.Engine.GenerateAsync(Request(), host.Ctx);

            Assert.True(result.IsDenied);
            Assert.Null(result.Artifact);
            Assert.Contains(result.Diagnostics,
                d => d.Code == ReportAuthorizationService.CodeNoModulePermission);

            // A refused report is exactly the event an auditor asks about. A history containing only successes
            // would be a success log wearing an audit log's name.
            var run = await host.Db.ReportRuns.AsNoTracking().SingleAsync();
            Assert.Equal(ReportRunStatus.Denied, run.Status);
            Assert.Equal(0, run.RowCount);
        }

        [Fact]
        public async Task An_unresolved_company_reads_nothing_rather_than_defaulting_to_a_company()
        {
            using var host = new ReportingTestHost(companyId: 1);

            var unresolved = new BusinessContext
            {
                CompanyId = 0, EmployeeId = 7, Source = BusinessContextSource.Test,
            };

            var result = await host.Engine.GenerateAsync(Request(), unresolved);

            Assert.True(result.IsDenied);
            Assert.Contains(result.Diagnostics, d => d.Code == ReportAuthorizationService.CodeCompanyUnresolved);
        }

        [Fact]
        public async Task An_administrator_passes_the_gate_without_a_mapped_role()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7, roles: "Admin");

            var result = await host.Engine.GenerateAsync(Request(), host.Ctx);

            Assert.True(result.IsSuccess);
        }

        [Fact]
        public async Task An_unmapped_permission_key_is_DENIED_because_omission_must_not_mean_unrestricted()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7, roles: "Reports");
            host.PermissionOptions.RoleMap.Remove(TestReportDefinitions.SalesPermission);

            var result = await host.Engine.GenerateAsync(Request(), host.Ctx);

            // Fail closed. "Forgot to configure it" must never mean "everyone can see it".
            Assert.True(result.IsDenied);
        }

        // ================================================================================================
        // 3. PARAMETERS AND FAILURE PATHS — every failure is recorded once, through one path
        // ================================================================================================

        [Fact]
        public async Task An_invalid_parameter_fails_the_run_and_the_failure_is_recorded_with_its_code()
        {
            using var host = new ReportingTestHost();

            var request = new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Parameters = new Dictionary<string, string?> { ["From"] = "rubbish" },
            };

            var result = await host.Engine.GenerateAsync(request, host.Ctx);

            Assert.Equal(ReportRunStatus.Failed, result.Status);
            Assert.Contains(result.Errors, d => d.Code == ReportParameterBinder.CodeInvalidValue);

            var run = await host.Db.ReportRuns.AsNoTracking().SingleAsync();
            Assert.Equal(ReportRunStatus.Failed, run.Status);
            Assert.Equal(ReportParameterBinder.CodeInvalidValue, run.ErrorCode);
        }

        [Fact]
        public async Task A_format_the_definition_forbids_is_refused()
        {
            using var host = new ReportingTestHost();

            // Test.Sales declares no explicit format list, so every registered format is permitted — but PDF's
            // engine is unbound, so it fails at the OUTPUT stage with an actionable reason rather than silently.
            var result = await host.Engine.GenerateAsync(Request(ReportOutputFormat.Pdf), host.Ctx);

            Assert.Equal(ReportRunStatus.Failed, result.Status);
            Assert.Contains(result.Errors, d => d.Code == ReportEngine.CodeRenderFailed);
            Assert.Contains(result.Errors, d => d.Message.Contains("Playwright"));
        }

        [Fact]
        public async Task A_data_source_that_throws_becomes_a_recorded_failure_not_an_unhandled_exception()
        {
            using var host = new ReportingTestHost();

            var throwing = new ThrowingDataSource(TestReportDefinitions.SalesDataSourceKey);
            var engine = Rebuild(host, new ReportDataSourceRegistry(new IReportDataSource[] { throwing }));

            var result = await engine.GenerateAsync(Request(), host.Ctx);

            Assert.Equal(ReportRunStatus.Failed, result.Status);
            Assert.Contains(result.Errors, d => d.Code == ReportEngine.CodeDataSourceFailed);

            var run = await host.Db.ReportRuns.AsNoTracking().SingleAsync();
            Assert.Equal(ReportEngine.CodeDataSourceFailed, run.ErrorCode);
        }

        [Fact]
        public async Task An_unregistered_report_code_throws_because_it_is_a_wiring_bug()
        {
            using var host = new ReportingTestHost();

            await Assert.ThrowsAsync<ReportNotRegisteredException>(() =>
                host.Engine.GenerateAsync(ReportRequest.For("No.Such"), host.Ctx));
        }

        // ================================================================================================
        // 4. THE ROW CAP CAN ONLY EVER BE LOWERED BY A CALLER
        // ================================================================================================

        [Fact]
        public async Task A_caller_can_lower_the_row_cap_but_not_raise_it()
        {
            using var host = new ReportingTestHost();
            host.EngineOptions.DefaultMaxRows = 3;

            // Lower: honoured.
            var lowered = await host.Engine.GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode, MaxRows = 2,
            }, host.Ctx);
            Assert.Equal(2, lowered.Run!.RowCount);
            Assert.True(lowered.Run!.Truncated);

            // Higher: ignored — the ceiling stays 3. A request that could raise it would make the ceiling advisory.
            var raised = await host.Engine.GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode, MaxRows = 1000,
            }, host.Ctx);
            Assert.Equal(3, raised.Run!.RowCount);
            Assert.True(raised.Run!.Truncated);
        }

        [Fact]
        public async Task A_preview_is_capped_marked_and_recorded_as_a_preview()
        {
            using var host = new ReportingTestHost();

            var result = await host.Engine.GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Kind = ReportRunKind.Preview,
                Culture = Arabic,
            }, host.Ctx);

            // Test.Sales declares PreviewRows = 2.
            Assert.Equal(2, result.Run!.RowCount);
            Assert.True(result.Run!.Truncated);
            Assert.Contains("معاينة", result.Artifact!.AsText());

            var run = await host.Db.ReportRuns.AsNoTracking().SingleAsync();
            Assert.Equal(ReportRunKind.Preview, run.Kind);
        }

        // ================================================================================================
        // 5. ARCHIVING
        // ================================================================================================

        [Fact]
        public async Task Archiving_stores_the_bytes_and_links_them_to_the_run()
        {
            using var host = new ReportingTestHost();

            var result = await host.Engine.GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Format = ReportOutputFormat.Csv,
                Archive = true,
            }, host.Ctx);

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Run!.ArchiveEntryId);

            var entry = await host.Db.ReportArchiveEntries.AsNoTracking()
                .SingleAsync(a => a.Id == result.Run!.ArchiveEntryId);

            Assert.Equal(result.Artifact!.ContentHash, entry.ContentHash);
            Assert.Equal(result.Artifact!.Length, entry.Length);

            // The bytes come back byte-identical through the store.
            var retrieved = await host.Archive.RetrieveAsync(entry.Id, host.Ctx);
            Assert.NotNull(retrieved);
            Assert.Equal(result.Artifact!.Content, retrieved!.Content);
        }

        [Fact]
        public async Task Archiving_the_same_artifact_twice_writes_one_file_and_two_rows()
        {
            using var host = new ReportingTestHost();

            var request = new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Format = ReportOutputFormat.Csv,
                Archive = true,
            };

            var first = await host.Engine.GenerateAsync(request, host.Ctx);
            var second = await host.Engine.GenerateAsync(request, host.Ctx);

            var entries = await host.Db.ReportArchiveEntries.AsNoTracking().ToListAsync();

            // Content-addressed: two rows (an honest record of two requests) sharing one stored path.
            Assert.Equal(2, entries.Count);
            Assert.Single(entries.Select(e => e.StoredPath).Distinct());
            Assert.Equal(first.Artifact!.ContentHash, second.Artifact!.ContentHash);
        }

        [Fact]
        public async Task A_preview_is_never_archived_and_says_why()
        {
            using var host = new ReportingTestHost();

            var result = await host.Engine.GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Kind = ReportRunKind.Preview,
                Archive = true,
            }, host.Ctx);

            Assert.Null(result.Run!.ArchiveEntryId);
            Assert.Contains(result.Diagnostics, d => d.Code == ReportEngine.CodeArchiveRefused);
            Assert.Empty(await host.Db.ReportArchiveEntries.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task An_archive_entry_is_not_readable_once_the_report_permission_is_revoked()
        {
            using var host = new ReportingTestHost();

            var result = await host.Engine.GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode, Format = ReportOutputFormat.Csv, Archive = true,
            }, host.Ctx);

            var entryId = result.Run!.ArchiveEntryId!.Value;
            Assert.NotNull(await host.Archive.RetrieveAsync(entryId, host.Ctx));

            // Permission revoked AFTER archiving. The archive must not become a way to keep reading a report the
            // caller may no longer run.
            host.PermissionOptions.RoleMap.Remove(TestReportDefinitions.SalesPermission);

            Assert.Null(await host.Archive.RetrieveAsync(entryId, host.Ctx));
        }

        // ================================================================================================
        // 6. COMPANY ISOLATION
        // ================================================================================================

        [Fact]
        public async Task History_and_archive_reads_are_confined_to_the_callers_company()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7);

            await host.Engine.GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode, Format = ReportOutputFormat.Csv, Archive = true,
            }, host.Ctx);

            var otherCompany = new BusinessContext
            {
                CompanyId = 2, EmployeeId = 7, Roles = new[] { "Admin" }, Source = BusinessContextSource.Test,
            };

            Assert.Empty(await host.History.QueryAsync(new ReportHistoryQuery(), otherCompany));
            Assert.Empty(await host.Archive.ListAsync(null, otherCompany));

            var mine = await host.Db.ReportArchiveEntries.AsNoTracking().SingleAsync();
            Assert.Null(await host.Archive.RetrieveAsync(mine.Id, otherCompany));
        }

        [Fact]
        public async Task The_company_a_data_source_sees_comes_from_the_context_not_from_a_parameter()
        {
            using var host = new ReportingTestHost(companyId: 3, employeeId: 7);

            int? seenCompany = null;
            var spy = new SpyDataSource(TestReportDefinitions.SalesDataSourceKey,
                query => seenCompany = query.Parameters.CompanyId);

            var engine = Rebuild(host, new ReportDataSourceRegistry(new IReportDataSource[] { spy }));

            await engine.GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode,
                // A hostile caller trying to read another tenant.
                Parameters = new Dictionary<string, string?> { [ReportSystemParameters.CompanyId] = "99" },
            }, host.Ctx);

            Assert.Equal(3, seenCompany);
        }

        // ================================================================================================
        // 7. HISTORY IS OBSERVABILITY, NOT PART OF CORRECTNESS
        // ================================================================================================

        [Fact]
        public async Task A_report_still_renders_when_the_history_table_is_missing()
        {
            using var host = new ReportingTestHost();

            // Simulates deploy/sql/reporting_platform.sql not being applied. The alternative — letting it throw —
            // would turn every working report into a 500 because of an observability table.
            await host.Db.Database.ExecuteSqlRawAsync("DROP TABLE ReportRuns;");

            var result = await host.Engine.GenerateAsync(Request(), host.Ctx);

            Assert.True(result.IsSuccess);
            Assert.Equal(0, result.Run!.RunId);   // 0 = no history line was written
        }

        [Fact]
        public async Task Recorded_parameters_are_the_callers_text_only_never_the_system_keys()
        {
            using var host = new ReportingTestHost();

            var result = await host.Engine.GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Parameters = new Dictionary<string, string?> { ["From"] = "2026-05-01", ["To"] = "2026-05-31" },
            }, host.Ctx);

            var run = await host.Db.ReportRuns.AsNoTracking().SingleAsync(r => r.Id == result.Run!.RunId);

            Assert.Contains("2026-05-01", run.ParametersJson);

            // Storing CompanyId in a history row would make the row look like a caller-CHOSEN company.
            Assert.DoesNotContain(ReportSystemParameters.CompanyId, run.ParametersJson);

            var replayed = await host.History.GetParametersAsync(run.Id, host.Ctx);
            Assert.Equal("2026-05-01", replayed!["From"]);
        }

        [Fact]
        public async Task Ordinary_users_see_only_their_own_history_and_an_administrator_sees_the_company()
        {
            using var host = new ReportingTestHost(companyId: 1, employeeId: 7);

            await host.Engine.GenerateAsync(Request(), host.Ctx);

            var otherEmployee = new BusinessContext
            {
                CompanyId = 1, EmployeeId = 8, Roles = new[] { "Reports" }, Source = BusinessContextSource.Test,
            };
            var admin = new BusinessContext
            {
                CompanyId = 1, EmployeeId = 9, Roles = new[] { "Admin" }, Source = BusinessContextSource.Test,
            };

            // A run row carries the parameters someone used, which can itself be sensitive.
            Assert.Empty(await host.History.QueryAsync(new ReportHistoryQuery(), otherEmployee));
            Assert.Single(await host.History.QueryAsync(new ReportHistoryQuery(), admin));
        }

        // ================================================================================================
        // 8. GROUPING FLOWS THROUGH THE WHOLE PIPELINE
        // ================================================================================================

        [Fact]
        public async Task A_grouping_key_that_is_not_in_the_visible_set_is_still_fetched()
        {
            using var host = new ReportingTestHost();

            var result = await host.Engine.GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode,
                VisibleColumns = new[] { "Item", "Amount" },
                Groupings = new[] { ReportGrouping.By("Branch") },
            }, host.Ctx);

            Assert.True(result.IsSuccess);

            // Grouping on a column that was not fetched would silently band everything into one "(blank)" group.
            var html = result.Artifact!.AsText();
            Assert.Contains("North", html);
            Assert.Contains("South", html);
            Assert.Contains("cbrep-subtotal", html);
        }

        [Fact]
        public async Task The_report_header_prints_its_parameters_so_the_document_can_be_interpreted_later()
        {
            using var host = new ReportingTestHost();

            var result = await host.Engine.GenerateAsync(new ReportRequest
            {
                ReportCode = TestReportDefinitions.SalesCode,
                Parameters = new Dictionary<string, string?> { ["Branch"] = "North" },
                Culture = Arabic,
            }, host.Ctx);

            var html = result.Artifact!.AsText();

            // Closed-option parameters print the LABEL, not the stored code.
            Assert.Contains("cbrep-params", html);
            Assert.Contains("الشمال", html);

            // System-supplied keys are excluded: "CompanyId: 1" on a printed report is noise.
            Assert.DoesNotContain("CompanyId", html);
        }

        // ------------------------------------------------------------------------------------------------
        // Rebuilds the engine with one substituted collaborator. Everything else stays the host's.
        private static ReportEngine Rebuild(ReportingTestHost host, IReportDataSourceRegistry dataSources) =>
            new(host.Catalog, host.Authorization, host.Templates, host.Binder, dataSources, host.Shaper,
                host.Output, host.Archive, host.History, host.Branding, host.EngineOptions, host.Clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ReportEngine>.Instance);

        private sealed class ThrowingDataSource : IReportDataSource
        {
            public ThrowingDataSource(string key) { Key = key; }
            public string Key { get; }
            public Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("the underlying query blew up");
        }

        private sealed class SpyDataSource : IReportDataSource
        {
            private readonly Action<ReportDataQuery> _observe;
            public SpyDataSource(string key, Action<ReportDataQuery> observe) { Key = key; _observe = observe; }
            public string Key { get; }

            public Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
            {
                _observe(query);
                return Task.FromResult(ReportDataSet.Empty(query.Definition.Columns));
            }
        }
    }
}
