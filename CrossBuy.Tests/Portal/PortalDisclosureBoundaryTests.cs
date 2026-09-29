using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using CrossBuy.BL.Portal;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context.Portal;
using Xunit;

namespace CrossBuy.Tests.Portal
{
    // =============================================================================================
    // WHAT THE PORTAL DOES NOT CARRY.
    //
    // The other portal test files assert that the right rows reach the right customer. This one
    // asserts about ABSENCES, which are the failures nobody notices: an internal comment thread that
    // starts rendering on a customer screen, an internal document that becomes downloadable, a staff
    // task whose title tells a client what the team thinks of their project. None of those would fail
    // an isolation test - they would be perfectly correctly scoped to the right customer, and still
    // be a disclosure.
    //
    // TWO OF THESE ARE SOURCE-SHAPED, AND THAT IS A WEAKNESS WORTH NAMING. A test that reads a file
    // and asserts a word is absent fails OPEN: it passes on the day somebody renames the thing it
    // was looking for. So each one is paired with a positive control that proves the scan is looking
    // where it thinks it is, and each was verified by MUTATION - the forbidden read was added to
    // PortalDataService and the test failed - rather than by having been green from birth.
    // =============================================================================================
    public class PortalDisclosureBoundaryTests
    {
        private static string RepoRoot()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
            if (!string.IsNullOrWhiteSpace(fromEnvironment)
                && File.Exists(Path.Combine(fromEnvironment, "CrossBuy.sln")))
            {
                return fromEnvironment;
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string ServiceSource()
            => File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "BL", "Portal", "PortalDataService.cs"));

        /// The reads the service actually performs, as `_db.<Something>` occurrences. This is the
        /// positive control the absence assertions rest on: if the scan stops matching real reads,
        /// the list empties and every "does not contain" below would pass vacuously.
        private static string[] DbSetsRead()
            => Regex.Matches(ServiceSource(), @"_db\.(\w+)")
                    .Select(m => m.Groups[1].Value)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToArray();

        [Fact]
        public void The_scan_finds_the_reads_the_portal_really_does_make()
        {
            // THE CONTROL. Every absence assertion in this file is only as good as this list, so it is
            // asserted first and by name: these four are the whole of the portal's read surface.
            var read = DbSetsRead();

            Assert.Contains("Quotations", read);
            Assert.Contains("SalesInvoices", read);
            Assert.Contains("Projects", read);
            Assert.Contains("Customers", read);
        }

        // ---- 10. communication ------------------------------------------------------------------

        [Fact]
        public void Public_is_the_only_communication_tier_that_may_leave_staff()
        {
            // Stated by the vocabulary itself, and asserted here because the portal's whole
            // communication position depends on it: everything that is not Public is internal
            // conversation, and there is no tier between "the customer may read this" and "they
            // may not".
            Assert.Equal("Public", CommVisibility.Public);
            Assert.Equal("Internal", CommVisibility.Internal);
            Assert.Equal("Confidential", CommVisibility.Confidential);
            Assert.Equal("Restricted", CommVisibility.Restricted);
        }

        [Fact]
        public void The_portal_reads_no_communication_table_at_all()
        {
            // THE STRONGEST FORM THIS CAN TAKE TODAY. There is no customer-visible conversation
            // surface in the portal, so the correct assertion is not "it filters to Public" but "it
            // does not read the conversation store". A filter can be got wrong; a read that does not
            // exist cannot.
            //
            // When a portal conversation is eventually built, THIS TEST IS THE THING THAT MUST CHANGE
            // FIRST, and changing it is the moment somebody has to write the Public-only predicate and
            // prove it with rows rather than with source.
            var read = DbSetsRead();
            Assert.DoesNotContain(read, n => n.StartsWith("Comm", StringComparison.Ordinal));

            // And the DTOs carry nothing shaped like a message, so a thread cannot arrive by being
            // projected onto an existing screen either.
            foreach (var dto in new[] { typeof(PortalQuotation), typeof(PortalProject), typeof(PortalInvoice) })
            {
                var fields = dto.GetProperties().Select(p => p.Name).ToList();
                foreach (var conversational in new[] { "Comments", "Thread", "Messages", "Notes", "Visibility" })
                {
                    Assert.DoesNotContain(conversational, fields);
                }
            }
        }

        // ---- 11. documents ----------------------------------------------------------------------

        [Fact]
        public void The_portal_exposes_no_document_surface_because_no_visibility_contract_exists()
        {
            // ViewDocuments IS IN THE VOCABULARY AND GRANTS NOTHING, on purpose. The Central Document
            // platform has no notion of "a customer may see this file": its tiers are staff tiers, and
            // there is no column that says a document was produced FOR the client. Shipping a document
            // list scoped only by customer id would therefore publish internal attachments that merely
            // happen to hang off the customer's records.
            //
            // So the capability is reserved and the surface is absent. That is a deliberate gap, and
            // it is recorded here rather than in a comment nobody reads.
            Assert.DoesNotContain(PortalCapabilities.ViewDocuments, PortalCapabilities.Default);

            var members = typeof(IPortalDataService)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => m.Name)
                .ToList();
            Assert.DoesNotContain(members, m => m.Contains("Document", StringComparison.Ordinal));

            var read = DbSetsRead();
            Assert.DoesNotContain(read, n => n.StartsWith("Doc", StringComparison.Ordinal));
        }

        // ---- 13. tasks --------------------------------------------------------------------------

        [Fact]
        public void The_portal_exposes_no_task_surface_and_borrows_no_employee_task_authority()
        {
            // A task is a STAFF artefact. Its title, its assignee and its due date are how a team talks
            // to itself about a customer's work, and the product has no customer-visible task concept
            // to show instead. Reusing the employee access service to answer "which tasks may this
            // portal user see" would have been the fastest way to give a customer an employee's answer.
            var members = typeof(IPortalDataService)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => m.Name)
                .ToList();
            Assert.DoesNotContain(members, m => m.Contains("Task", StringComparison.Ordinal));

            var source = ServiceSource();
            Assert.DoesNotContain("TasksAccessService", source, StringComparison.Ordinal);
            Assert.DoesNotContain("_db.Tasks", source, StringComparison.Ordinal);
        }
    }
}
