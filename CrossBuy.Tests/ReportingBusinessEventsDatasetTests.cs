using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // Reporting Platform — R1 ACTIVATION: the Business Event log dataset.
    //
    // The dataset reads `BusinessEvents`, whose rows carry a VISIBILITY (ADR-004). The kernel's own reader
    // applies four filters before returning a row. A report over the same table is a plausible way around all
    // four, so most of this file is about proving it is not:
    //
    //   * company isolation, on the row
    //   * branch isolation, only when BOTH sides have a branch
    //   * visibility tiering, with the own-actor exception the kernel uses
    //   * the per-entity View check replaced by a stricter, deliberately narrow report permission
    //
    // The remainder covers activation mechanics: parameters, the row cap, truncation, and the registry wiring.
    // ============================================================================================
    public class ReportingBusinessEventsDatasetTests
    {
        private const int Company = 1;
        private const int OtherCompany = 2;
        private const int Me = 7;
        private const int SomebodyElse = 9;

        private static ReportRequest Request(Dictionary<string, string?>? parameters = null) => new()
        {
            ReportCode = BusinessEventsReportCodes.ReportCode,
            Format = ReportOutputFormat.Html,
            Parameters = parameters ?? new Dictionary<string, string?>
            {
                ["From"] = "2020-01-01",
                ["To"] = "2030-12-31",
            },
        };

        // A host wired with the Business Event report + data source, and a permission evaluator holding exactly
        // the keys the test names.
        private static ReportingTestHost Host(params string[] heldKeys)
        {
            var host = new ReportingTestHost(companyId: Company, employeeId: Me, roles: "Auditor");

            // The role map is the shipped evaluator's mechanism: map each requested key to the role the context
            // actually holds. A key left unmapped stays denied, which is the fail-closed default under test.
            foreach (var key in heldKeys) host.PermissionOptions.RoleMap[key] = new[] { "Auditor" };
            return host;
        }

        private static void SeedEvent(ReportingTestHost host, string visibility, int? actor = Me,
            string entityType = "SalesInvoice", int entityId = 1001, int company = Company,
            int? branch = null, string? payload = null, DateTime? at = null)
        {
            host.Db.BusinessEvents.Add(new BusinessEvent
            {
                EventUid = Guid.NewGuid(),
                CompanyID = company,
                BranchID = branch,
                EntityType = entityType,
                EntityId = entityId,
                EventType = entityType + ".Created",
                ActorEmployeeId = actor,
                Payload = payload,
                PayloadVersion = 1,
                Visibility = visibility,
                CreatedAt = at ?? ReportingTestHost.FixedNow.AddMinutes(-5),
            });
            host.Db.SaveChanges();
        }

        private static BusinessEventsReportDataSource Source(ReportingTestHost host) =>
            new(host.Db, host.PermissionEvaluator);

        private static ReportEngine EngineFor(ReportingTestHost host) => host.EngineWith(Source(host));

        // ========================================================================================
        // The report is gated, and the gate is fail-closed
        // ========================================================================================

        // The keys are UNMAPPED by default. Registering the dataset must therefore grant nobody anything — the
        // intended starting state for an audit log.
        [Fact]
        public async Task With_no_permission_mapped_the_report_is_denied()
        {
            using var host = Host();                       // maps nothing
            SeedEvent(host, BusinessEventVisibility.Internal);

            var result = await EngineFor(host).GenerateAsync(Request(), host.Ctx);

            Assert.True(result.IsDenied);
            Assert.Null(result.Artifact);
        }

        [Fact]
        public async Task With_the_view_permission_the_report_runs_and_returns_internal_rows()
        {
            using var host = Host(BusinessEventsReportPermissions.View);
            SeedEvent(host, BusinessEventVisibility.Internal);

            var result = await EngineFor(host).GenerateAsync(Request(), host.Ctx);

            Assert.True(result.IsSuccess);
            Assert.Equal(1, result.Run!.RowCount);
        }

        // ========================================================================================
        // VISIBILITY TIERING — the heart of this dataset
        // ========================================================================================

        // A plain auditor sees Internal only. Confidential, and somebody else's Restricted, are absent — not
        // blanked, absent: the row never leaves the database.
        [Fact]
        public async Task A_plain_auditor_sees_internal_only()
        {
            using var host = Host(BusinessEventsReportPermissions.View);

            SeedEvent(host, BusinessEventVisibility.Internal, entityId: 1);
            SeedEvent(host, BusinessEventVisibility.Confidential, entityId: 2);
            SeedEvent(host, BusinessEventVisibility.Restricted, actor: SomebodyElse, entityId: 3);
            SeedEvent(host, BusinessEventVisibility.System, actor: null, entityId: 4);

            var result = await EngineFor(host).GenerateAsync(Request(), host.Ctx);

            Assert.True(result.IsSuccess);
            Assert.Equal(1, result.Run!.RowCount);
        }

        [Fact]
        public async Task The_confidential_key_adds_confidential_rows_and_nothing_else()
        {
            using var host = Host(BusinessEventsReportPermissions.View,
                                  BusinessEventsReportPermissions.Confidential);

            SeedEvent(host, BusinessEventVisibility.Internal, entityId: 1);
            SeedEvent(host, BusinessEventVisibility.Confidential, entityId: 2);
            SeedEvent(host, BusinessEventVisibility.Restricted, actor: SomebodyElse, entityId: 3);
            SeedEvent(host, BusinessEventVisibility.System, actor: null, entityId: 4);

            var result = await EngineFor(host).GenerateAsync(Request(), host.Ctx);

            Assert.Equal(2, result.Run!.RowCount);      // Internal + Confidential; NOT Restricted or System
        }

        // System rides with Restricted, exactly as the kernel's timeline gates it — machine bookkeeping is never
        // shown below the manager tier.
        [Fact]
        public async Task The_restricted_key_adds_restricted_and_system_rows()
        {
            using var host = Host(BusinessEventsReportPermissions.View,
                                  BusinessEventsReportPermissions.Confidential,
                                  BusinessEventsReportPermissions.Restricted);

            SeedEvent(host, BusinessEventVisibility.Internal, entityId: 1);
            SeedEvent(host, BusinessEventVisibility.Confidential, entityId: 2);
            SeedEvent(host, BusinessEventVisibility.Restricted, actor: SomebodyElse, entityId: 3);
            SeedEvent(host, BusinessEventVisibility.System, actor: null, entityId: 4);

            var result = await EngineFor(host).GenerateAsync(Request(), host.Ctx);

            Assert.Equal(4, result.Run!.RowCount);
        }

        // THE OWN-ACTOR EXCEPTION. An ordinary caller sees their OWN restricted rows and nobody else's. This is
        // the two-step the kernel uses — Restricted passes the SQL filter so own rows are fetched, then a
        // row-level pass drops the rest. Collapsing it shows every viewer every restricted event.
        [Fact]
        public async Task An_ordinary_caller_sees_their_own_restricted_row_but_not_someone_elses()
        {
            using var host = Host(BusinessEventsReportPermissions.View);

            SeedEvent(host, BusinessEventVisibility.Restricted, actor: Me, entityId: 1);
            SeedEvent(host, BusinessEventVisibility.Restricted, actor: SomebodyElse, entityId: 2);

            var result = await EngineFor(host).GenerateAsync(Request(), host.Ctx);

            Assert.Equal(1, result.Run!.RowCount);      // mine only
        }

        // A System row has no actor, so the own-actor exception can never reach it.
        [Fact]
        public async Task A_system_row_is_never_reachable_by_the_own_actor_exception()
        {
            using var host = Host(BusinessEventsReportPermissions.View);
            SeedEvent(host, BusinessEventVisibility.System, actor: null, entityId: 1);

            var result = await EngineFor(host).GenerateAsync(Request(), host.Ctx);

            Assert.Equal(0, result.Run!.RowCount);
        }

        // ========================================================================================
        // The Payload column is separately gated
        // ========================================================================================

        // A payload is a summary by contract, but a summary of a sales invoice still carries its total — so the
        // SHAPE of activity and the AMOUNTS are separate grants.
        [Fact]
        public void The_payload_field_is_confidential_and_names_its_own_permission_key()
        {
            var payload = BusinessEventsDataset.Definition().FindField("Payload");

            Assert.NotNull(payload);
            Assert.Equal(ReportFieldSensitivity.Confidential, payload!.Sensitivity);
            Assert.Equal(BusinessEventsReportPermissions.Confidential, payload.RequiredPermissionKey);

            // Not filterable: a free-text search over a JSON blob is a table scan wearing a filter's clothes.
            Assert.False(payload.Filterable);
            Assert.False(payload.Sortable);
        }

        [Fact]
        public void A_plain_auditor_cannot_see_the_payload_field_at_all()
        {
            var dataset = BusinessEventsDataset.Definition();
            var held = new HashSet<string>(StringComparer.Ordinal) { BusinessEventsReportPermissions.View };

            Assert.DoesNotContain(dataset.VisibleFields(held), f => f.Key == "Payload");

            held.Add(BusinessEventsReportPermissions.Confidential);
            Assert.Contains(dataset.VisibleFields(held), f => f.Key == "Payload");
        }

        // The dedup key is an idempotency token with no business meaning, and exposing it would let a reader
        // infer a producer's keying scheme. Never-sensitivity means nobody, including an administrator.
        [Fact]
        public void The_dedup_key_is_never_visible_to_anyone()
        {
            var dataset = BusinessEventsDataset.Definition();
            var everything = new HashSet<string>(StringComparer.Ordinal)
            {
                BusinessEventsReportPermissions.View,
                BusinessEventsReportPermissions.Confidential,
                BusinessEventsReportPermissions.Restricted,
                ReportPermissions.Administer,
            };

            Assert.DoesNotContain(dataset.VisibleFields(everything), f => f.Key == "DedupKey");

            // And the projected column is Internal, so no renderer or exporter can emit it either.
            Assert.True(dataset.FindField("DedupKey")!.ToColumn().Internal);
        }

        // ========================================================================================
        // Company and branch isolation
        // ========================================================================================
        [Fact]
        public async Task Another_companys_events_are_never_returned()
        {
            using var host = Host(BusinessEventsReportPermissions.View,
                                  BusinessEventsReportPermissions.Restricted);

            SeedEvent(host, BusinessEventVisibility.Internal, entityId: 1, company: Company);

            // The other company's row is written through a context SCOPED TO THAT COMPANY, not through the
            // company-1 host. Writing it through `host.Db` does not produce the row this test needs: the
            // CompanyWriteGuardInterceptor is attached to that context, so a foreign-company row either is
            // refused or does not land as company 2 — and the test would then be asserting against a row that
            // is not actually another tenant's. This is the same arrangement the Communication isolation tests
            // use, and the reason PlatformTestHost grew a per-company Request() helper.
            var otherHolder = new CompanyScopeHolder();
            otherHolder.Set(OtherCompany, null);
            using (var otherDb = host.NewContext(otherHolder))
            {
                otherDb.BusinessEvents.Add(new BusinessEvent
                {
                    EventUid = Guid.NewGuid(),
                    CompanyID = OtherCompany,
                    EntityType = "SalesInvoice", EntityId = 2,
                    EventType = "SalesInvoice.Created",
                    ActorEmployeeId = Me,
                    Visibility = BusinessEventVisibility.Internal,
                    PayloadVersion = 1,
                    CreatedAt = ReportingTestHost.FixedNow.AddMinutes(-5),
                });
                await otherDb.SaveChangesAsync();
            }

            // NOT asserted here: a count through `host.Db`. That context is scoped to company 1 and carries the
            // write guard, so it is not a neutral observer of another tenant's rows — counting through it would
            // be measuring the isolation with the very thing under test.
            //
            // Seeding the foreign row through `host.Db` (the first version of this test) does not work either:
            // the CompanyWriteGuardInterceptor coerces it into company 1, so the report then legitimately
            // returned BOTH rows and the test failed against correct code.
            var result = await EngineFor(host).GenerateAsync(Request(), host.Ctx);

            Assert.Equal(1, result.Run!.RowCount);
        }

        // Branch filtering applies only when BOTH sides have a branch. A branch user must still see events that
        // carry no branch (a SalesInvoice has no BranchID column at all), or their history silently loses rows.
        [Fact]
        public async Task A_branch_user_still_sees_events_that_carry_no_branch()
        {
            using var host = Host(BusinessEventsReportPermissions.View);

            SeedEvent(host, BusinessEventVisibility.Internal, entityId: 1, branch: null);
            SeedEvent(host, BusinessEventVisibility.Internal, entityId: 2, branch: 5);
            SeedEvent(host, BusinessEventVisibility.Internal, entityId: 3, branch: 6);   // another branch

            // The branch lives on the CONTEXT, so the test builds one rather than changing the shared host's
            // constructor signature for a single case.
            var branchScoped = new BusinessContext
            {
                CompanyId = Company, BranchId = 5, EmployeeId = Me, UserId = "u7",
                Roles = new[] { "Auditor" }, Source = BusinessContextSource.Test,
            };

            var result = await EngineFor(host).GenerateAsync(Request(), branchScoped);

            Assert.Equal(2, result.Run!.RowCount);      // unstamped + own branch, not the other branch
        }

        // The other direction of the same rule: a caller with NO branch (head office) must not lose
        // branch-stamped history. Filtering unconditionally hides rows in both directions, which is why the
        // filter is applied only when both sides carry a branch.
        [Fact]
        public async Task A_head_office_caller_with_no_branch_sees_branch_stamped_events()
        {
            using var host = Host(BusinessEventsReportPermissions.View);

            SeedEvent(host, BusinessEventVisibility.Internal, entityId: 1, branch: null);
            SeedEvent(host, BusinessEventVisibility.Internal, entityId: 2, branch: 5);
            SeedEvent(host, BusinessEventVisibility.Internal, entityId: 3, branch: 6);

            var result = await EngineFor(host).GenerateAsync(Request(), host.Ctx);   // no BranchId

            Assert.Equal(3, result.Run!.RowCount);
        }

        // Fail closed. The platform rule is that an unresolved company scope reads no company-scoped data — and
        // an event log is emphatically company-scoped.
        [Fact]
        public async Task An_unresolved_company_reads_nothing()
        {
            using var host = Host(BusinessEventsReportPermissions.View);
            SeedEvent(host, BusinessEventVisibility.Internal);

            var unresolved = new BusinessContext
            {
                CompanyId = 0, EmployeeId = Me, UserId = "u7",
                Roles = new[] { "Auditor" }, Source = BusinessContextSource.Test,
            };

            var result = await EngineFor(host).GenerateAsync(Request(), unresolved);

            Assert.False(result.IsSuccess);
        }

        // ========================================================================================
        // Parameters
        // ========================================================================================

        // The upper bound is INCLUSIVE. Every date range a user types means both endpoints, and a half-open
        // range silently drops the last day — the single most common off-by-one in a reporting product.
        [Fact]
        public async Task The_date_range_includes_both_endpoints()
        {
            using var host = Host(BusinessEventsReportPermissions.View);

            var day = new DateTime(2026, 5, 14);
            SeedEvent(host, BusinessEventVisibility.Internal, entityId: 1, at: day.AddHours(0));    // 00:00
            SeedEvent(host, BusinessEventVisibility.Internal, entityId: 2, at: day.AddHours(23.9)); // 23:54
            SeedEvent(host, BusinessEventVisibility.Internal, entityId: 3, at: day.AddDays(1));     // next day

            var result = await EngineFor(host).GenerateAsync(Request(new Dictionary<string, string?>
            {
                ["From"] = "2026-05-14",
                ["To"] = "2026-05-14",
            }), host.Ctx);

            Assert.Equal(2, result.Run!.RowCount);      // both rows ON the day, not the next one
        }

        [Fact]
        public async Task The_entity_type_parameter_accepts_multiple_values()
        {
            using var host = Host(BusinessEventsReportPermissions.View);

            SeedEvent(host, BusinessEventVisibility.Internal, entityType: "SalesInvoice", entityId: 1);
            SeedEvent(host, BusinessEventVisibility.Internal, entityType: "PurchaseInvoice", entityId: 2);
            SeedEvent(host, BusinessEventVisibility.Internal, entityType: "Customer", entityId: 3);

            var result = await EngineFor(host).GenerateAsync(Request(new Dictionary<string, string?>
            {
                ["From"] = "2020-01-01", ["To"] = "2030-12-31",
                ["EntityType"] = "SalesInvoice,PurchaseInvoice",
            }), host.Ctx);

            Assert.Equal(2, result.Run!.RowCount);
        }

        [Fact]
        public async Task Filtering_to_one_record_returns_only_that_records_history()
        {
            using var host = Host(BusinessEventsReportPermissions.View);

            SeedEvent(host, BusinessEventVisibility.Internal, entityId: 1001);
            SeedEvent(host, BusinessEventVisibility.Internal, entityId: 1001);
            SeedEvent(host, BusinessEventVisibility.Internal, entityId: 2002);

            var result = await EngineFor(host).GenerateAsync(Request(new Dictionary<string, string?>
            {
                ["From"] = "2020-01-01", ["To"] = "2030-12-31", ["EntityId"] = "1001",
            }), host.Ctx);

            Assert.Equal(2, result.Run!.RowCount);
        }

        [Fact]
        public async Task Filtering_by_actor_returns_only_that_actors_events()
        {
            using var host = Host(BusinessEventsReportPermissions.View);

            SeedEvent(host, BusinessEventVisibility.Internal, actor: Me, entityId: 1);
            SeedEvent(host, BusinessEventVisibility.Internal, actor: SomebodyElse, entityId: 2);

            var result = await EngineFor(host).GenerateAsync(Request(new Dictionary<string, string?>
            {
                ["From"] = "2020-01-01", ["To"] = "2030-12-31",
                ["ActorEmployeeId"] = SomebodyElse.ToString(),
            }), host.Ctx);

            Assert.Equal(1, result.Run!.RowCount);
        }

        // A null actor is SYSTEM, not blank. A blank cell reads as missing data; "System" is the fact.
        [Fact]
        public async Task A_system_event_renders_its_actor_as_System_rather_than_blank()
        {
            using var host = Host(BusinessEventsReportPermissions.View,
                                  BusinessEventsReportPermissions.Restricted);
            SeedEvent(host, BusinessEventVisibility.System, actor: null, entityId: 1);

            var result = await EngineFor(host).GenerateAsync(Request(), host.Ctx);

            Assert.True(result.IsSuccess);
            var html = System.Text.Encoding.UTF8.GetString(result.Artifact!.Content);
            Assert.Contains("System", html);
        }

        // ========================================================================================
        // Row cap and truncation
        // ========================================================================================

        // Truncation is DECLARED, never silent. The source fetches cap+1 so it can tell "exactly N exist" from
        // "more than N exist" instead of guessing.
        [Fact]
        public async Task Exceeding_the_row_cap_truncates_and_says_so()
        {
            using var host = Host(BusinessEventsReportPermissions.View);

            for (int i = 1; i <= 6; i++)
                SeedEvent(host, BusinessEventVisibility.Internal, entityId: i);

            var request = new ReportRequest
            {
                ReportCode = BusinessEventsReportCodes.ReportCode,
                Format = ReportOutputFormat.Html,
                MaxRows = 3,                       // a caller may LOWER the cap
                Parameters = new Dictionary<string, string?> { ["From"] = "2020-01-01", ["To"] = "2030-12-31" },
            };

            var result = await EngineFor(host).GenerateAsync(request, host.Ctx);

            Assert.True(result.IsSuccess);
            Assert.Equal(3, result.Run!.RowCount);
            Assert.True(result.Run!.Truncated);
        }

        // At exactly the cap nothing is truncated — the cap+1 fetch must not report truncation off by one.
        [Fact]
        public async Task Exactly_the_row_cap_is_not_reported_as_truncated()
        {
            using var host = Host(BusinessEventsReportPermissions.View);

            for (int i = 1; i <= 3; i++)
                SeedEvent(host, BusinessEventVisibility.Internal, entityId: i);

            var request = new ReportRequest
            {
                ReportCode = BusinessEventsReportCodes.ReportCode,
                Format = ReportOutputFormat.Html,
                MaxRows = 3,
                Parameters = new Dictionary<string, string?> { ["From"] = "2020-01-01", ["To"] = "2030-12-31" },
            };

            var result = await EngineFor(host).GenerateAsync(request, host.Ctx);

            Assert.Equal(3, result.Run!.RowCount);
            Assert.False(result.Run!.Truncated);
        }

        // ========================================================================================
        // Output formats — activation means HTML, CSV and Excel all work end to end
        // ========================================================================================
        [Theory]
        [InlineData(ReportOutputFormat.Html)]
        [InlineData(ReportOutputFormat.Csv)]
        [InlineData(ReportOutputFormat.Xlsx)]
        public async Task The_log_is_producible_in_every_activated_format(ReportOutputFormat format)
        {
            using var host = Host(BusinessEventsReportPermissions.View);
            SeedEvent(host, BusinessEventVisibility.Internal);

            var result = await EngineFor(host).GenerateAsync(new ReportRequest
            {
                ReportCode = BusinessEventsReportCodes.ReportCode,
                Format = format,
                Parameters = new Dictionary<string, string?> { ["From"] = "2020-01-01", ["To"] = "2030-12-31" },
            }, host.Ctx);

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Artifact);
            Assert.NotEmpty(result.Artifact!.Content);
        }

        // A CSV of this dataset can carry payload text, which is why the dataset requires the export right.
        // Whatever the export policy, a Never field must never reach the file.
        [Fact]
        public async Task A_csv_export_never_contains_the_dedup_key_column()
        {
            using var host = Host(BusinessEventsReportPermissions.View,
                                  BusinessEventsReportPermissions.Confidential,
                                  BusinessEventsReportPermissions.Restricted);
            SeedEvent(host, BusinessEventVisibility.Internal, payload: "{\"total\":42}");

            var result = await EngineFor(host).GenerateAsync(new ReportRequest
            {
                ReportCode = BusinessEventsReportCodes.ReportCode,
                Format = ReportOutputFormat.Csv,
                Parameters = new Dictionary<string, string?> { ["From"] = "2020-01-01", ["To"] = "2030-12-31" },
            }, host.Ctx);

            var csv = System.Text.Encoding.UTF8.GetString(result.Artifact!.Content);
            Assert.DoesNotContain("DedupKey", csv);
        }

        // ========================================================================================
        // The dataset itself
        // ========================================================================================
        [Fact]
        public void The_dataset_validates_clean()
        {
            Assert.Empty(ReportDatasetValidator.Validate(BusinessEventsDataset.Definition()));
        }

        [Fact]
        public void The_dataset_is_available_to_the_studio_but_not_as_a_dashboard_widget()
        {
            var dataset = BusinessEventsDataset.Definition();

            Assert.True(dataset.AvailableInStudio);

            // A widget refreshes unattended on a shared screen. An audit log is the last thing that belongs on one.
            Assert.False(dataset.AvailableAsWidget);
        }

        [Fact]
        public void The_dataset_requires_the_export_permission_because_a_field_is_confidential()
        {
            var dataset = BusinessEventsDataset.Definition();

            Assert.Equal(ReportDatasetExportPolicy.RequirePermissionForDataExport, dataset.ExportPolicy);
            Assert.Contains(dataset.Fields, f => f.Sensitivity == ReportFieldSensitivity.Confidential);
        }

        // Truncate rather than fail: an investigative log is useful partially, and says so. A trial balance is
        // not — which is why the policy is per dataset.
        [Fact]
        public void The_dataset_truncates_rather_than_failing_at_the_cap()
        {
            Assert.Equal(ReportRowCapPolicy.TruncateAndDeclare, BusinessEventsDataset.Definition().RowCapPolicy);
        }

        [Fact]
        public void The_report_projects_its_columns_from_the_dataset_so_the_two_cannot_disagree()
        {
            var dataset = BusinessEventsDataset.Definition();
            var report = BusinessEventsDataset.Report();

            Assert.Equal(dataset.Fields.Count, report.Columns.Count);
            foreach (var field in dataset.Fields)
            {
                var column = report.FindColumn(field.Key);
                Assert.NotNull(column);
                Assert.Equal(field.Type, column!.Type);
                Assert.Equal(field.Sensitivity == ReportFieldSensitivity.Never, column.Internal);
            }
        }

        [Fact]
        public void The_calculated_action_field_declares_what_it_reads_and_is_not_pushed_down()
        {
            var action = BusinessEventsDataset.Definition().FindField("Action");

            Assert.NotNull(action);
            Assert.True(action!.IsCalculated);
            Assert.Contains("EventType", action.DependsOn);
            Assert.Contains("EntityType", action.DependsOn);
            Assert.False(action.Filterable);
            Assert.False(action.Sortable);
        }

        [Fact]
        public void The_drill_through_maps_from_fields_that_exist()
        {
            var dataset = BusinessEventsDataset.Definition();
            var target = Assert.Single(dataset.DrillThroughTargets);

            foreach (var sourceField in target.ParameterMap.Values)
                Assert.NotNull(dataset.FindField(sourceField));

            Assert.Equal(BusinessEventsReportCodes.ReportCode, target.TargetReportCode);
        }
    }
}
