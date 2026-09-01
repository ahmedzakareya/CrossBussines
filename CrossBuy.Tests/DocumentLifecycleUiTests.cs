using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Documents;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Tests.Communication;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // THE LIFECYCLE READ CONTRACT AND THE SCREEN THAT RENDERS IT.
    //
    // TAB-3 wrote LifecycleAsync / LifecycleForEntityAsync and deliberately did NOT land them,
    // because IPlatformDocumentService is a declared authority: every member is credited by CBA001
    // only because every member was verified to authorize, and DocumentAuthorityMemberTests fails
    // when one appears that its table does not cover. Widening that interface is a governance act.
    //
    // IT TURNED OUT NOT TO NEED ONE. ListForEntityAsync already gates the entity once and re-asks per
    // document, so the row a caller is entitled to see can carry its own lifecycle. The authority
    // surface is untouched, the member table is unchanged, and the screen still gets the canonical
    // answer. Reuse beat widening — which is the order the brief asks for.
    //
    // WHAT THESE TESTS HOLD:
    //
    //   * the listing carries the CANONICAL evaluation, not a re-derivation;
    //   * a restricted row is ABSENT rather than rendered as hidden;
    //   * the view compares no dates — the mutation that matters, because a Razor
    //     `@if (doc.ExpiryDate < DateTime.Now)` would quietly become the system's second date rule
    //     and would get both boundaries wrong;
    //   * the screen exposes no StorageKey and no static file path;
    //   * the authority surface did not grow.
    // =============================================================================================
    public class DocumentLifecycleUiTests
    {
        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
        }

        private static string View() => File.ReadAllText(
            RepoFile("CrossBuy", "Views", "Documents", "Workspace.cshtml"));

        /// The view with its PROSE removed - Razor `@* *@` blocks and C# line comments.
        ///
        /// The assertions below are about what the screen DOES, and this file's own explanations name
        /// the very things they forbid: the comment in the view spells out the
        /// `@if (doc.ExpiryDate < DateTime.Now)` it exists to prevent, and says that no StorageKey is
        /// rendered. Scanning raw text would fail on the warning rather than on the mistake - which is
        /// how a guardrail teaches people to delete the comment instead of keeping the rule.
        private static string ViewCode()
        {
            var src = View();
            src = System.Text.RegularExpressions.Regex.Replace(src, @"@\*.*?\*@", " ",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            src = System.Text.RegularExpressions.Regex.Replace(src, @"^\s*//.*$", " ",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            return src;
        }

        // -----------------------------------------------------------------------------------------
        // THE READ CONTRACT
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void The_declared_authority_did_NOT_grow_to_serve_the_screen()
        {
            // The governance claim, asserted rather than asserted-in-prose. If a future change adds a
            // Lifecycle* member here, DocumentAuthorityMemberTests will demand coverage for it — and
            // this test says plainly that the screen never needed one.
            var members = typeof(IPlatformDocumentService)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => m.Name).ToList();

            Assert.Equal(11, members.Count);
            Assert.DoesNotContain("LifecycleAsync", members);
            Assert.DoesNotContain("LifecycleForEntityAsync", members);
        }

        [Fact]
        public async Task The_listing_carries_the_lifecycle_the_platform_computed()
        {
            using var host = new DocHost();
            var typeId = host.SeedType("PASSPORT", EntityRegistry.Employee);
            var id = Seed(host, typeId, expiry: DateTime.UtcNow.Date.AddDays(5));

            var row = Assert.Single(await host.Service().ListForEntityAsync(EntityRegistry.Employee, Subject));

            Assert.NotNull(row.Lifecycle);

            // Byte for byte the SAME answer the canonical evaluator gives — not a sympathetic copy.
            var doc = host.Platform.Db.Set<PlatformDocument>().Single(d => d.Id == id);
            var type = host.Platform.Db.Set<PlatformDocumentType>().Single(t => t.Id == typeId);
            var canonical = PlatformDocumentService.EvaluateAt(doc, type, DateTime.UtcNow.Date);

            Assert.Equal(canonical.State, row.Lifecycle!.State);
            Assert.Equal(canonical.DaysRemaining, row.Lifecycle.DaysRemaining);
            Assert.Equal(canonical.WarningDays, row.Lifecycle.WarningDays);
        }

        [Fact]
        public async Task A_document_expiring_TODAY_reaches_the_screen_as_still_valid()
        {
            // The first of the two boundaries a hand-written view gets wrong. An expiry date is the last
            // day the document is good for, not the first day it is not.
            using var host = new DocHost();
            var typeId = host.SeedType("PASSPORT", EntityRegistry.Employee);
            Seed(host, typeId, expiry: DateTime.UtcNow.Date);

            var row = Assert.Single(await host.Service().ListForEntityAsync(EntityRegistry.Employee, Subject));
            Assert.True(row.Lifecycle!.CountsAsValid);
        }

        [Fact]
        public async Task A_row_the_caller_may_not_see_is_ABSENT_rather_than_rendered_as_hidden()
        {
            // A screen that received a redacted row would leak the one fact the tier exists to protect:
            // that the document exists at all.
            using var host = new DocHost();
            var typeId = host.SeedType("MEDICAL", EntityRegistry.Employee, conf: DocumentConfidentiality.Confidential);
            Seed(host, typeId, expiry: DateTime.UtcNow.Date.AddDays(30),
                 confidentiality: DocumentConfidentiality.Confidential);

            host.Access.Decide = (_, _, _, _, conf) =>
                conf == DocumentConfidentiality.Confidential
                    ? new DocumentAccessDecision(false, DocumentAccessReasons.ConfidentialityDenied)
                    : new DocumentAccessDecision(true, DocumentAccessReasons.Allowed);

            Assert.Empty(await host.Service().ListForEntityAsync(EntityRegistry.Employee, Subject));
        }

        // -----------------------------------------------------------------------------------------
        // THE SCREEN
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void The_view_compares_no_dates_of_its_own()
        {
            // THE MUTATION THAT MATTERS. A single `doc.ExpiryDate < DateTime.Now` in Razor is a second
            // date rule that disagrees with the platform on both boundaries at once, and it would never
            // show up as a failing behaviour test because no test renders Razor.
            var view = ViewCode();

            foreach (var forbidden in new[]
                     {
                         "DateTime.Now", "DateTime.UtcNow", "DateTime.Today",
                         "ExpiryDate <", "ExpiryDate >", "ExpiryDate.Value <", "ExpiryDate.Value >",
                         "AddDays", "TotalDays", "Subtract(",
                     })
                Assert.False(view.Contains(forbidden, StringComparison.Ordinal),
                    $"the documents workspace computes with '{forbidden}'. The lifecycle state, the days " +
                    "remaining and the type's warning window all arrive already computed by " +
                    "PlatformDocumentService.EvaluateAt; a view that recomputes any of them becomes the " +
                    "system's second date rule.");
        }

        [Fact]
        public void The_view_renders_the_state_the_service_named()
        {
            var view = View();
            Assert.Contains("doc.Lifecycle", view, StringComparison.Ordinal);
            Assert.Contains("DocumentLifecycleState", view, StringComparison.Ordinal);

            // DaysRemaining is PRINTED, never derived.
            Assert.Contains("DaysRemaining", view, StringComparison.Ordinal);
        }

        [Fact]
        public void The_screen_exposes_no_storage_key_and_no_static_file_path()
        {
            var view = ViewCode();

            Assert.DoesNotContain("StorageKey", view, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("App_Data", view, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/uploads/", view, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("wwwroot", view, StringComparison.OrdinalIgnoreCase);

            // Bytes are reachable only through the authorized route.
            Assert.Contains("/Documents/@doc.Id/content", view, StringComparison.Ordinal);
        }

        [Fact]
        public void The_screen_creates_no_second_attention_store()
        {
            // Document follow-up converges through the existing Tasks/Attention taxonomy. A screen that
            // wrote its own reminder rows would be the sixth Attention source the architecture refuses.
            var view = ViewCode();
            foreach (var forbidden in new[] { "DocumentReminder", "DocumentTask", "AttentionSource" })
                Assert.DoesNotContain(forbidden, view, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_workspace_route_names_an_entity_and_never_a_module()
        {
            // SHF-29's rule: the central document surface must never learn a module's vocabulary. A
            // route of /Documents/workspace/Employee/{id} would make the next family need a second one.
            var controller = File.ReadAllText(RepoFile("CrossBuy", "Controllers", "DocumentsController.cs"));
            var i = controller.IndexOf("Workspace", StringComparison.Ordinal);
            Assert.True(i > 0);
            var action = controller[Math.Max(0, i - 200)..Math.Min(controller.Length, i + 900)];

            Assert.Contains("workspace/{entityType}/{entityId:int}", action, StringComparison.Ordinal);
            foreach (var moduleWord in new[] { "Employee", "HrActions", "employee-manage", "Hr." })
                Assert.DoesNotContain(moduleWord, action, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_key_the_screen_asks_for_exists_in_all_three_resource_files()
        {
            var keys = System.Text.RegularExpressions.Regex
                .Matches(View(), "Localizer\\[\"([^\"]+)\"\\]")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            Assert.NotEmpty(keys);

            foreach (var culture in new[] { "ar", "en", "fr" })
            {
                var path = RepoFile("CrossBuy", "Resources", "Views", "Documents", $"Workspace.{culture}.resx");
                Assert.True(File.Exists(path), "missing resource file: " + path);
                var resx = File.ReadAllText(path);
                foreach (var key in keys)
                    Assert.True(resx.Contains("\"" + System.Security.SecurityElement.Escape(key) + "\"", StringComparison.Ordinal),
                        $"{culture}: no resource for \"{key}\" — IViewLocalizer would render the raw English key.");
            }
        }

        // -----------------------------------------------------------------------------------------

        private const int Subject = 500;

        private static long Seed(DocHost host, long typeId, DateTime? expiry,
            string confidentiality = DocumentConfidentiality.Internal)
        {
            var doc = new PlatformDocument
            {
                CompanyID = DocHost.CompanyA,
                EntityType = EntityRegistry.Employee,
                EntityId = Subject,
                DocumentTypeId = typeId,
                Confidentiality = confidentiality,
                Status = "Active",
                ExpiryDate = expiry,
                CreatedBy = DocHost.Alice,
                CreatedAt = DateTime.UtcNow,
            };
            host.Platform.Db.Set<PlatformDocument>().Add(doc);
            host.Platform.Db.SaveChanges();

            var key = StorageKey.New();
            host.Storage.Blobs[key.Value] = new byte[] { 1 };
            var version = new PlatformDocumentVersion
            {
                CompanyID = DocHost.CompanyA, DocumentId = doc.Id, VersionNo = 1,
                StorageKey = key.Value, FileName = "a.pdf", ContentType = "application/pdf",
                SizeBytes = 1, Reason = "seed", UploadedBy = DocHost.Alice, UploadedAt = DateTime.UtcNow,
            };
            host.Platform.Db.Set<PlatformDocumentVersion>().Add(version);
            host.Platform.Db.SaveChanges();

            doc.CurrentVersionId = version.Id;
            host.Platform.Db.SaveChanges();
            return doc.Id;
        }
    }
}
