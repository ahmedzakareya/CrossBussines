using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace CrossBuy.Analyzers.Tests;

/// <summary>
/// The reconciliation: does the analyzer reproduce the accepted Stage 1 baseline from the real application?
///
/// This is the test that matters. Thirty pattern tests prove the analyzer agrees with itself; only this one ties
/// it to the application and to the frozen numbers 388 / 157 / 88 / 143. If it fails, the instruction is explicit —
/// do not change the baseline, produce a reconciliation report.
/// </summary>
public class ReconciliationTests
{
    // Stage 1 measured 388 / 157 / 88 / 143 across 39 controllers, and the analyzer reproduced that exactly, with
    // zero classification disagreements over all 388 endpoints.
    //
    // Stage 2A BATCH A then added production code: PlatformGrantsApiController.Create, .Revoke and .UpdateValidity,
    // on one new controller. All three are authorized in-body through IPlatformGrantWriter, so they land in the
    // in-body category and NOT in the debt.
    //
    // NEW-MACHINE RECONCILIATION (TAB-1, 2026-08-12) brings these totals up to the modernization work that had
    // accumulated in the working tree without ever being recorded here: +19 mutating and +19 in-body protected,
    // +0 attribute-protected, +0 debt. The +19 are the seven activated Reporting write endpoints
    // (ReportsCenterWriteApiController, via IReportAuthorizationService), eleven Tasks endpoints (the Task
    // Ecosystem checklist/dependency/template actions plus BoardMove and TaskCommentAdd, via ITasksAccessService)
    // and CalendarController.SaveSchedule (via ICalendarAccessService).
    //
    // THE FIGURE THAT MATTERS DID NOT MOVE: debt is still 143, and the gap set still matches
    // authorization-baseline.json id for id (asserted below). 410 = 157 + 110 + 143. A grown total with unchanged
    // debt is what "added protected endpoints" looks like; had the debt moved, this would be a finding instead of
    // an update. engineering/authorization-baseline.json records the same delta in frozenBaselineHistory.
    // REPORT STUDIO V1 (TAB-2, 2026-08-25): +5 mutating and +5 in-body protected, +0 attribute-protected,
    // +0 debt. The five are ReportStudioApiController's endpoints — Fields, Validate, Preview, Save and
    // Export — every one of which authorizes in-body through IReportAuthorizationService before it acts.
    // CBA001 fired on all four POSTs when the controller first landed and was fixed by adding that gate, not
    // by a baseline entry.
    //
    // THE FIGURE THAT MATTERS DID NOT MOVE: debt is still 143 and the gap set still matches
    // authorization-baseline.json id for id. 415 = 157 + 115 + 143 — a grown total with unchanged debt, which
    // is what "added protected endpoints" looks like, exactly as the note above records for the previous
    // increment. Had the debt moved this would be a finding instead of an update.
    // ---- Insight -> Task action endpoint (TAB-4, insight action workflows) ----
    //
    // ONE new controller, InsightActionsController, with ONE mutating action: CreateTask. The three
    // insight screens needed a single governed place to turn a finding into a follow-up task, and one
    // shared endpoint is what stopped that becoming three copies of the same authorization code.
    //
    // IT AUTHORIZES IN BODY, and on BOTH sides: the source module (inventory or CRM read) AND task
    // creation, plus a resolved company, a source row that must exist inside it, and an assignee that
    // must be an active employee of it. CBA001 does not fire, so this is an in-body-protected endpoint
    // rather than a baseline entry.
    //
    // THE FIGURE THAT MATTERS DID NOT MOVE: debt is still 143 and the gap set still matches
    // authorization-baseline.json id for id. 416 = 157 + 116 + 143 - a grown total with unchanged debt,
    // which is what "added a protected endpoint" looks like, exactly as the notes above record for the
    // previous two increments. Had the debt moved this would be a finding instead of an update.
    // ---- Report Studio V2 asset, print and PDF endpoints (TAB-2, reporting) ----
    //
    // FOUR new mutating actions, all on the existing ReportStudioApiController, so the controller count
    // does not move: UploadAsset, DeleteAsset, Print and Pdf. Assets and export were the two things the
    // designer could not do without leaving the page.
    //
    // ALL FOUR AUTHORIZE IN BODY, through IReportAuthorizationService.AuthorizeReportAsync, and each
    // refuses with NotFound rather than Forbid so the endpoints cannot be used to enumerate a catalogue
    // the caller may not see. Print and Pdf authorize the DRAFT's dataset; the two asset actions require
    // authorship, which is satisfied only if the caller may run at least one dataset. A resolved company
    // is required first in every case. CBA001 does not fire on any of them, so these are in-body
    // protected endpoints and NOT baseline entries.
    //
    // THE FIGURE THAT MATTERS DID NOT MOVE: debt is still 143 and the gap set still matches
    // authorization-baseline.json id for id. 420 = 157 + 120 + 143 - a grown total with unchanged debt,
    // which is what "added four protected endpoints" looks like. Had the debt moved, or had the four
    // arrived without an in-body check, this would be a finding rather than a count update.
    // ----------------------------------------------------------------------------------------------
    // Projects security + membership foundation (TAB-6, 2026-08-26).
    //
    // THE FIGURE THAT MATTERS FINALLY MOVED, AND IT MOVED DOWN: debt 143 -> 136. Every increment above
    // this one recorded a grown total with UNCHANGED debt; this is the first that pays some back.
    //
    // SEVEN ENTRIES WERE REMOVED, not reclassified. ProjectController.SaveProject, DeleteProject,
    // SaveProgress, ConfirmProgress, DeleteProgress, SaveActivityType and DeleteActivityType carried no
    // authorization at all - they were Stage 1 debt, frozen since Batch-00. They now authorize in body
    // through IProjectsAccessService.CanAsync before they act, so CBA004 FIRED on all seven and was
    // resolved by REMOVING their baseline entries, which is the only direction this list may move.
    //
    // THREE NEW MUTATING ACTIONS ARRIVED PROTECTED: AddMember, UpdateMember and EndMember, the writer for
    // dbo.ProjectMembers - a table that until this batch had two readers and no writer, so the record-level
    // project access model could not be populated at all. Each resolves the company server-side and asks
    // ProjectsAccessService for 'manage' on the target project before it writes; CBA001 fires on none of
    // them. So mutating grows 420 -> 423 and in-body protected grows 120 -> 130 (+7 closed, +3 new).
    //
    // 423 = 157 + 130 + 136. Attribute-protected did not move.
    // ----------------------------------------------------------------------------------------------
    // ----------------------------------------------------------------------------------------------
    // 423 -> 424 (Quotation business conversation, 2026-08-29). ONE new POST on the EXISTING
    // InventoryController: QuotationConversationAdd, the write half of the quotation discussion that
    // moved off the legacy DocComments store onto the Communication Platform.
    //
    // It arrives PROTECTED, which is why the debt figure below does not move. The endpoint resolves the
    // company server-side, requires a BusinessContext carrying a real employee, and asks
    // InventoryAccessService for the "doc" action on PermissionTarget.ForEntity(Quotation, id) before it
    // writes - "doc" and not "read", because adding to a record’s discussion changes that record’s
    // history. CBA001 does not fire on it and no baseline entry was added.
    //
    // Its companion GET, QuotationConversation, is not a mutating endpoint and moves none of these
    // figures. Attribute-protected does not move either: the endpoint authorizes in body, so the growth
    // is entirely in-body, 130 -> 131.
    //
    // 424 = 157 + 131 + 136. The debt is UNCHANGED at 136 and the gap set is identical id for id.
    // ----------------------------------------------------------------------------------------------
    // ----------------------------------------------------------------------------------------------
    // THE DEBT FIGURE MOVED DOWN AGAIN, AND FURTHER THAN IT EVER HAS: 136 -> 94 (HR authorization
    // foundation, 2026-08-30). Forty-two live HR mutations that carried NO authorization at all now
    // authorize in body through IHrAccessService.CanAsync before they act, so CBA004 fired on all
    // forty-two and was resolved by REMOVING their baseline entries - the only direction this list may
    // move. Nothing was added, nothing was edited, nothing was suppressed.
    //
    // Mutating does not move: no endpoint arrived or left. Attribute-protected does not move either -
    // every one of the forty-two authorizes in BODY, so the whole gain is in-body, 131 -> 173.
    //
    // 424 = 157 + 173 + 94.
    //
    // The 94 that remain are NOT HR debt. Three HR-registry endpoints are deliberately still listed -
    // AddHierarchicalItem, AdministrativeBodiesCompany and JobTitle - because they administer companies,
    // branches, job titles and the shared administrative hierarchy rather than HR, and belong to another
    // owner. The other 91 are pre-existing debt in other modules.
    // ----------------------------------------------------------------------------------------------
    // ----------------------------------------------------------------------------------------------
    // 424 -> 426 (project billing separation of duties, 2026-08-30). TWO new POSTs on the EXISTING
    // ProjectController: SubmitBilling and ReturnBilling, the two transitions the billing lifecycle was
    // missing once the single `billing` right was split into billing-prepare / billing-approve /
    // billing-post so that separation of duties could be expressed at all.
    //
    // THE DEBT FIGURE DOES NOT MOVE, AND NEITHER DOES THE ENTRY LIST. Both endpoints arrive protected -
    // IProjectsAccessService.CanAsync on a ForProject target before they act - so they were never in the
    // baseline and there was nothing stale to remove. The integrated candidate produced ZERO CBA004,
    // which is what distinguishes this increment from the HR one above it: that removed 42 entries,
    // this removes none. Attribute-protected does not move either, because both authorize in body.
    //
    // 426 = 157 + 175 + 94.
    // ----------------------------------------------------------------------------------------------
    private const int FrozenMutating = 426;
    private const int FrozenAttributeProtected = 157;
    private const int FrozenInBodyProtected = 175;

    /// <summary>The enforced invariant. This one may only ever shrink - and here it did.</summary>
    private const int FrozenGaps = 94;

    // Controllers carrying at least one endpoint the analyzer models. Grew 40 -> 44 with the platform
    // modernization controllers that arrived with the same increment (Reporting, Workspace, Calendar and the
    // Business Event Monitor surfaces). Descriptive, like the three totals above - not an invariant.
    //
    // 44 -> 45 (UAT dataset owner, 2026-08-12): UatSeedController, the [DevOnly] UAT dataset harness on
    // api/uat. It is a CONTROLLER-COUNT-ONLY delta and the reason is mechanical: every action on it is
    // [HttpGet] (preflight, prime, seed, counts, cleanup), so the analyzer models the controller but finds
    // NO MUTATING ENDPOINT on it. Verified from the analyzer's own evidence file rather than asserted -
    // docs/architecture/evidence/Roslyn-Authorization-Inventory.csv holds 410 rows and ZERO of them name
    // UatSeed, which is the same 410 FrozenMutating asserts.
    //
    // SO NONE OF THE FOUR FIGURES THAT MATTER MOVED: mutating 410, attribute-protected 157, in-body 110 and
    // debt 143 are all unchanged, 157 + 110 + 143 = 410 still closes, and no entry in
    // engineering/authorization-baseline.json was added, removed or edited. No CBA diagnostic was suppressed,
    // CBA001 was not weakened, and no security attribute was removed to reach this number.
    //
    // The controller is not a hole in the coverage metric either: it is 404 outside Development
    // ([CrossBuy.Models.DevOnly]), key-gated, and its own guards refuse to write unless the live connection
    // resolves to CrossBuyDev. A GET-only dev harness has nothing for the mutating inventory to measure.
    // +1 for ReportStudioApiController (Report Studio V1). Its five endpoints all authorize in-body, so the
    // controller count and the descriptive totals move together while the debt figure does not.
    private const int FrozenControllers = 47;

    private static readonly Lazy<AuthorizationInventoryResult> Inventory = new(() =>
        AuthorizationInventory.Build(RealSourceCompilation.Value, CancellationToken.None));

    private static AuthorizationInventoryResult Result => Inventory.Value;

    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_application_sources_compile_well_enough_for_semantic_analysis()
    {
        // A semantic result is only as good as the binding behind it. An unbound access-service call cannot be
        // credited, so compilation errors would systematically UNDERSTATE coverage and overstate debt — and would
        // do it silently. The count is asserted, and the first few errors are printed, so a regression here is
        // legible instead of appearing as a mysterious count drift.
        var errors = RealSourceCompilation.Value.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(errors.Count == 0,
            $"the from-source compilation reported {errors.Count} errors; semantic authorization resolution " +
            "cannot be trusted until they are zero:\n  " +
            string.Join("\n  ", errors.Take(15).Select(e => e.ToString())));
    }

    [Fact]
    public void The_analyzer_reproduces_the_frozen_mutating_endpoint_count()
    {
        Assert.Equal(FrozenMutating, Result.MutatingCount);
    }

    [Fact]
    public void The_analyzer_reproduces_the_frozen_attribute_protected_count()
    {
        Assert.Equal(FrozenAttributeProtected, Result.AttributeProtectedCount);
    }

    [Fact]
    public void The_analyzer_reproduces_the_frozen_in_body_protected_count()
    {
        Assert.Equal(FrozenInBodyProtected, Result.InBodyProtectedCount);
    }

    [Fact]
    public void The_analyzer_reproduces_the_frozen_authorization_debt()
    {
        Assert.Equal(FrozenGaps, Result.GapCount);
    }

    [Fact]
    public void The_three_categories_sum_to_the_mutating_total_with_nothing_uncounted()
    {
        // 157 + 88 + 143 = 388. An endpoint that fell out of all three categories would be invisible in every
        // published number, which is how a measurement defect survives a review.
        Assert.Equal(
            Result.MutatingCount,
            Result.AttributeProtectedCount + Result.InBodyProtectedCount + Result.GapCount);
    }

    [Fact]
    public void The_analyzer_reproduces_the_frozen_controller_count()
    {
        Assert.Equal(FrozenControllers, Result.Controllers.Count);
    }

    // -----------------------------------------------------------------------------------------------------
    // false credits
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void No_endpoint_is_credited_by_something_that_is_not_a_permission()
    {
        // False credits = 0, asserted structurally rather than counted by eye. Every credited endpoint must name
        // either a permission attribute from the declared set, or an authority member.
        var falseCredits = Result.Endpoints
            .Where(e => e.IsProtected)
            .Where(e => e.IsAttributeProtected
                ? e.PermissionAttributes.Count == 0 ||
                  e.PermissionAttributes.Any(a => !AuthorizationSurface.PermissionAttributes.Contains(a))
                : string.IsNullOrEmpty(e.InBodyEvidence))
            .Select(e => e.Id + " (" + e.Authorization + ")")
            .ToList();

        Assert.True(falseCredits.Count == 0, "credited with no named authority:\n  " + string.Join("\n  ", falseCredits));
    }

    [Fact]
    public void The_lane_guard_credits_nothing_on_its_own()
    {
        var laneOnly = Result.Endpoints
            .Where(e => e.HasLaneGuard && e.Authorization == AuthorizationKind.InheritedAttribute)
            .Where(e => e.PermissionAttributes.Contains("PosLaneActivityGuard"))
            .Select(e => e.Id)
            .ToList();

        Assert.Empty(laneOnly);
    }

    [Fact]
    public void The_environment_gate_credits_nothing_on_its_own()
    {
        var devOnly = Result.Endpoints
            .Where(e => e.HasEnvironmentGate && e.Authorization == AuthorizationKind.None)
            .ToList();

        // DevOnly endpoints, if any are mutating, must land in the debt category, not in a protected one.
        Assert.All(Result.Endpoints.Where(e => e.HasEnvironmentGate),
            e => Assert.True(
                e.Authorization != AuthorizationKind.InheritedAttribute ||
                !e.PermissionAttributes.Contains("DevOnly"),
                e.Id + " was credited by DevOnly"));

        _ = devOnly;   // the assertion above is the contract; this keeps the queried set visible for debugging
    }

    // -----------------------------------------------------------------------------------------------------
    // agreement with the frozen baseline FILE, id by id
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_analyzer_gap_set_matches_the_frozen_baseline_file_id_for_id()
    {
        var baseline = BaselineDocument.TryParse(File.ReadAllText(BaselinePath));
        Assert.NotNull(baseline);

        var analyzerGaps = Result.Endpoints.Where(e => !e.IsProtected).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var baselineIds = baseline!.Entries.Keys.ToHashSet(StringComparer.Ordinal);

        var onlyInBaseline = baselineIds.Except(analyzerGaps).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var onlyInAnalyzer = analyzerGaps.Except(baselineIds).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(onlyInBaseline.Count == 0 && onlyInAnalyzer.Count == 0,
            "the analyzer and the frozen baseline must describe the SAME endpoints, not merely the same count.\n" +
            "  stale baseline entries (would be CBA004): " + string.Join(", ", onlyInBaseline) + "\n" +
            "  unlisted analyzer gaps (would be CBA001): " + string.Join(", ", onlyInAnalyzer));
    }

    [Fact]
    public void The_baseline_files_own_frozen_header_matches_what_the_analyzer_measures()
    {
        var baseline = BaselineDocument.TryParse(File.ReadAllText(BaselinePath))!;

        Assert.Equal(baseline.FrozenMutating, Result.MutatingCount);
        Assert.Equal(baseline.FrozenAttributeProtected, Result.AttributeProtectedCount);
        Assert.Equal(baseline.FrozenInBodyProtected, Result.InBodyProtectedCount);
        Assert.Equal(baseline.FrozenGaps, Result.GapCount);
        Assert.Equal(baseline.DeclaredCount, baseline.Entries.Count);
    }

    [Fact]
    public void Running_the_real_analyzer_over_the_real_sources_reports_no_new_debt_and_no_stale_entry()
    {
        // End to end: the analyzer, the real compilation, the real baseline as an AdditionalFile. On a clean tree
        // CBA001 and CBA004 must both be empty — that is what "the guardrail is ready to enforce" means.
        var diagnostics = AnalyzerHarness.Run(RealSourceCompilation.Value, File.ReadAllText(BaselinePath));

        var newDebt = diagnostics.OfId("CBA001").Select(d => d.GetMessage()).ToList();
        var stale = diagnostics.OfId("CBA004").Select(d => d.GetMessage()).ToList();

        Assert.True(newDebt.Count == 0, "CBA001 fired on a clean tree:\n  " + string.Join("\n  ", newDebt));
        Assert.True(stale.Count == 0, "CBA004 fired on a clean tree:\n  " + string.Join("\n  ", stale));
    }

    // -----------------------------------------------------------------------------------------------------
    // evidence artifact
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_reconciliation_writes_its_own_evidence_so_the_report_quotes_a_file_not_a_memory()
    {
        // Stage 1 lost a batch to documents quoting one scan while the evidence CSVs came from another. The
        // analyzer's inventory is therefore written from the SAME run that asserts the counts above.

        var csv = new StringBuilder();
        csv.AppendLine("controller,action,http_method,is_api,authorization,permission_attributes,in_body_authority," +
                       "in_body_chain,authentication_only,antiforgery,lane_guard,environment_gate,anonymous," +
                       "unsupported_helpers,file,line");

        foreach (var endpoint in Result.Endpoints
                     .OrderBy(e => e.Controller, StringComparer.Ordinal)
                     .ThenBy(e => e.Action, StringComparer.Ordinal))
        {
            var relative = endpoint.FilePath.StartsWith(RealSourceCompilation.RepoRoot, StringComparison.OrdinalIgnoreCase)
                ? endpoint.FilePath[(RealSourceCompilation.RepoRoot.Length + 1)..].Replace('\\', '/')
                : endpoint.FilePath.Replace('\\', '/');

            csv.AppendLine(string.Join(",", new[]
            {
                Q(endpoint.Controller), Q(endpoint.Action), Q(endpoint.HttpMethods), Q(endpoint.IsApi.ToString()),
                Q(endpoint.Authorization.ToString()), Q(string.Join(";", endpoint.PermissionAttributes)),
                Q(endpoint.InBodyEvidence ?? ""), Q(endpoint.InBodyChain ?? ""),
                Q(endpoint.HasAuthenticationOnly.ToString()), Q(endpoint.HasAntiForgery.ToString()),
                Q(endpoint.HasLaneGuard.ToString()), Q(endpoint.HasEnvironmentGate.ToString()),
                Q(endpoint.IsAnonymousDeclared.ToString()),
                Q(string.Join(";", endpoint.UnsupportedAuthorizationHelpers)),
                Q(relative), Q(endpoint.Line.ToString()),
            }));
        }

        var path = EvidencePath("Roslyn-Authorization-Inventory.csv");
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));

        Assert.True(File.Exists(path));
        Assert.Equal(FrozenMutating + 1, File.ReadAllLines(path).Length);   // header + one row per endpoint
    }

    [Fact]
    public void The_diagnostic_profile_is_stable_across_two_runs_and_is_recorded()
    {
        // "Analyzer diagnostics stable" from the brief, taken literally: the same compilation analyzed twice must
        // produce the identical diagnostic set. Concurrent execution is enabled, and an analyzer whose output
        // depends on scheduling would make every future count a coin toss.
        var first = AnalyzerHarness.Run(RealSourceCompilation.Value, File.ReadAllText(BaselinePath));
        var second = AnalyzerHarness.Run(RealSourceCompilation.Value, File.ReadAllText(BaselinePath));

        string Key(Diagnostic d) => d.Id + "|" + d.GetMessage() + "|" + d.Location.GetLineSpan();

        Assert.Equal(
            first.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            second.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList());

        // Recorded to a file so the delivery report quotes a generated artifact rather than a remembered number.
        var summary = first
            .GroupBy(d => d.Id)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key + "," + g.Count())
            .ToList();

        var path = EvidencePath("Roslyn-Analyzer-Diagnostic-Profile.csv");

        File.WriteAllText(
            path,
            "diagnostic,count\n" + string.Join("\n", summary) + "\n" +
            $"TOTAL,{first.Length}\n",
            new UTF8Encoding(true));

        // CBA001 and CBA004 must be absent on a clean tree; the rest are informational findings on existing debt.
        Assert.DoesNotContain("CBA001", first.Select(d => d.Id));
        Assert.DoesNotContain("CBA004", first.Select(d => d.Id));
    }

    [Fact]
    public void The_analyzer_build_cost_is_measured_not_estimated()
    {
        // The build-integration plan has to state an expected build impact. Guessing it would be the kind of number
        // that gets quoted back later, so it is measured: a plain compile of the application, then the same
        // compilation WITH the analyzer. The delta is what wiring the analyzer into CrossBuy.csproj costs.
        //
        // Recorded, never asserted — a wall-clock assertion is a flaky test, and a flaky guardrail gets disabled.
        var compilation = RealSourceCompilation.Value;

        var withoutWatch = System.Diagnostics.Stopwatch.StartNew();
        var baselineDiagnostics = compilation.GetDiagnostics().Length;
        withoutWatch.Stop();

        var withWatch = System.Diagnostics.Stopwatch.StartNew();
        var analyzerDiagnostics = AnalyzerHarness.Run(compilation, File.ReadAllText(BaselinePath)).Length;
        withWatch.Stop();

        var path = EvidencePath("Roslyn-Analyzer-Build-Impact.csv");

        File.WriteAllText(path,
            "measurement,value\n" +
            $"syntax_trees,{compilation.SyntaxTrees.Count()}\n" +
            $"metadata_references,{compilation.References.Count()}\n" +
            $"compile_only_ms,{withoutWatch.ElapsedMilliseconds}\n" +
            $"compile_plus_analyzer_ms,{withWatch.ElapsedMilliseconds}\n" +
            $"compiler_diagnostics,{baselineDiagnostics}\n" +
            $"analyzer_diagnostics,{analyzerDiagnostics}\n",
            new UTF8Encoding(true));

        Assert.True(File.Exists(path));
    }

    private static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    // EVERY evidence writer creates its own destination. Only ONE of the three used to, so on a fresh
    // clone the other two threw DirectoryNotFoundException depending on the order xUnit happened to
    // pick — an ordering dependency, not flakiness, and it made a green 16/16 depend on luck. Creating
    // the directory here means no test relies on another having run first.
    private static string EvidencePath(string fileName)
    {
        var directory = Path.Combine(RealSourceCompilation.RepoRoot, "docs", "architecture", "evidence");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, fileName);
    }

    private static string BaselinePath =>
        Path.Combine(RealSourceCompilation.RepoRoot, "engineering", "authorization-baseline.json");
}
