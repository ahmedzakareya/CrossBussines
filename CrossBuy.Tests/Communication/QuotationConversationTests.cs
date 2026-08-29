using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Communication;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context.Communication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =============================================================================================
    // Quotation → Business Conversations.
    //
    // Quotation was the last document in the system still writing to DocComments, reached through
    // _DocTimeline and /Comments/*. This batch gives it conversation endpoints on the module that owns
    // the document — Inventory — reading and writing the SAME CommThreads/CommComments store the invoice
    // screens use. No second table, no quotation-specific comment service, no second notification path.
    //
    // Two kinds of test here, deliberately:
    //
    //   BEHAVIOURAL — run against the REAL CommThreadService/CommCommentService over a real DbContext,
    //   because storage, entity identity and company isolation are properties of the platform and can be
    //   proven by exercising it. These are the ones worth mutating.
    //
    //   STRUCTURAL — read the controller source, because the gate ORDER (company resolved → module
    //   permission → row-in-my-company) and the identical-refusal shape are properties of the code that a
    //   behavioural test through a 22-dependency MVC controller would assert far more weakly than it
    //   appears to. Every structural guard strips comments first, so prose can neither satisfy nor break
    //   one — a trap this suite has fallen into before.
    // =============================================================================================
    public class QuotationConversationTests
    {
        private static CommEntityRef Quotation(int id = 7001) => new(EntityRegistry.Quotation, id);

        // ---- storage: the authoritative store, and only it -------------------------------------
        [Fact]
        public async Task A_quotation_comment_is_stored_in_CommThreads_and_CommComments()
        {
            using var host = new CommunicationTestHost();
            var ctx = CommunicationTestHost.Context();
            var entity = Quotation();

            var thread = await host.Threads().GetOrCreateAsync(ctx, new CommThreadRequest { Entity = entity });
            var added = await host.Comments().AddAsync(ctx, host.CommentOn(entity, "Customer asked for a longer validity."));

            using var fresh = host.NewContext();
            var storedThread = await new CommDb(fresh).Threads.AsNoTracking().SingleAsync(t => t.Id == thread.Id);
            var storedComment = await new CommDb(fresh).Comments.AsNoTracking().SingleAsync(c => c.Id == added.CommentId);

            Assert.Equal(EntityRegistry.Quotation, storedThread.EntityType);
            Assert.Equal(entity.EntityId, storedThread.EntityId);
            Assert.Equal(thread.Id, storedComment.ThreadId);
            Assert.Equal("Customer asked for a longer validity.", storedComment.Body);

            // And nothing reached the legacy store.
            Assert.Equal(0, await fresh.DocComments.CountAsync());
        }

        [Fact]
        public async Task The_thread_carries_the_resolved_company_not_a_default()
        {
            using var host = new CommunicationTestHost();
            var ctx = CommunicationTestHost.Context();

            var thread = await host.Threads().GetOrCreateAsync(ctx, new CommThreadRequest { Entity = Quotation() });

            using var fresh = host.NewContext();
            var stored = await new CommDb(fresh).Threads.AsNoTracking().SingleAsync(t => t.Id == thread.Id);
            Assert.Equal(ctx.CompanyId, stored.CompanyID);
        }

        [Fact]
        public async Task A_quotation_thread_is_distinct_from_an_invoice_thread_with_the_SAME_numeric_id()
        {
            // Entity identity is (EntityType, EntityId, CompanyId). Quotation 7001 and SalesInvoice 7001
            // are different records and must not share a conversation.
            using var host = new CommunicationTestHost();
            var ctx = CommunicationTestHost.Context();
            var quote = Quotation(7001);
            var invoice = new CommEntityRef(EntityRegistry.SalesInvoice, 7001);

            var quoteThread = await host.Threads().GetOrCreateAsync(ctx, new CommThreadRequest { Entity = quote });
            var invoiceThread = await host.Threads().GetOrCreateAsync(ctx, new CommThreadRequest { Entity = invoice });
            await host.Comments().AddAsync(ctx, host.CommentOn(quote, "QUOTE-ONLY-BODY"));

            Assert.NotEqual(quoteThread.Id, invoiceThread.Id);

            var invoicePage = await host.Comments().ListAsync(ctx, invoiceThread.Id);
            Assert.Empty(invoicePage.Items);
            var quotePage = await host.Comments().ListAsync(ctx, quoteThread.Id);
            Assert.Equal("QUOTE-ONLY-BODY", Assert.Single(quotePage.Items).Body);
        }

        [Fact]
        public async Task Asking_twice_for_the_same_quotation_returns_the_same_thread()
        {
            using var host = new CommunicationTestHost();
            var ctx = CommunicationTestHost.Context();

            var first = await host.Threads().GetOrCreateAsync(ctx, new CommThreadRequest { Entity = Quotation() });
            var second = await host.Threads().GetOrCreateAsync(ctx, new CommThreadRequest { Entity = Quotation() });

            Assert.Equal(first.Id, second.Id);
            using var fresh = host.NewContext();
            Assert.Equal(1, await new CommDb(fresh).Threads.CountAsync(t => t.EntityType == EntityRegistry.Quotation));
        }

        // ---- company isolation ------------------------------------------------------------------
        [Fact]
        public async Task Another_company_asking_for_the_same_quotation_gets_a_DIFFERENT_thread_not_the_first()
        {
            // The isolation that matters: company is part of the thread key, so company B naming the same
            // quotation id cannot land on company A's conversation.
            using var host = new CommunicationTestHost();
            var mine = CommunicationTestHost.Context();
            var theirs = CommunicationTestHost.Context(
                employeeId: CommunicationTestHost.OtherCompanyEmployee,
                companyId: CommunicationTestHost.OtherCompanyId);

            var myThread = await host.Threads().GetOrCreateAsync(mine, new CommThreadRequest { Entity = Quotation() });
            await host.Comments().AddAsync(mine, host.CommentOn(Quotation(), "COMPANY-A-SECRET"));

            var theirThread = await host.Threads().GetOrCreateAsync(theirs, new CommThreadRequest { Entity = Quotation() });

            Assert.NotEqual(myThread.Id, theirThread.Id);
            var theirPage = await host.Comments().ListAsync(theirs, theirThread.Id);
            Assert.Empty(theirPage.Items);
        }

        [Fact]
        public async Task Another_company_cannot_read_the_conversation_by_naming_its_thread_id_directly()
        {
            using var host = new CommunicationTestHost();
            var mine = CommunicationTestHost.Context();
            var theirs = CommunicationTestHost.Context(
                employeeId: CommunicationTestHost.OtherCompanyEmployee,
                companyId: CommunicationTestHost.OtherCompanyId);

            var myThread = await host.Threads().GetOrCreateAsync(mine, new CommThreadRequest { Entity = Quotation() });
            await host.Comments().AddAsync(mine, host.CommentOn(Quotation(), "COMPANY-A-SECRET"));

            // Guessing the id is the whole attack; the store must refuse regardless of how it was obtained.
            //
            // The refusal may be an exception OR an empty page depending on the layer that catches it, so
            // the call is captured and the assertion is made OUTSIDE the catch. An earlier version of this
            // test asserted inside a try/catch helper, which swallowed its own Assert and passed happily
            // when the company predicate was removed from the thread read — a mutation caught exactly that.
            var leaked = await CaptureBodiesAsync(() => host.Comments().ListAsync(theirs, myThread.Id));

            Assert.True(leaked is null || leaked.All(b => !b.Contains("COMPANY-A-SECRET")),
                "another company read the conversation body by naming the thread id: "
                + string.Join(" | ", leaked ?? new List<string>()));
        }

        [Fact]
        public async Task Another_company_cannot_add_a_comment_to_the_conversation()
        {
            using var host = new CommunicationTestHost();
            var mine = CommunicationTestHost.Context();
            var theirs = CommunicationTestHost.Context(
                employeeId: CommunicationTestHost.OtherCompanyEmployee,
                companyId: CommunicationTestHost.OtherCompanyId);

            var myThread = await host.Threads().GetOrCreateAsync(mine, new CommThreadRequest { Entity = Quotation() });

            await RunIgnoringRefusalAsync(() => host.Comments().AddAsync(theirs, host.CommentOn(Quotation(), "INTRUSION")));

            using var fresh = host.NewContext();
            var mineAfter = await new CommDb(fresh).Comments.AsNoTracking().Where(c => c.ThreadId == myThread.Id).ToListAsync();
            Assert.DoesNotContain(mineAfter, c => (c.Body ?? "").Contains("INTRUSION"));
        }

        /// Runs a read and returns the bodies it produced, or null when the call was refused by throwing.
        /// It NEVER swallows an assertion: the caller asserts on the returned value, outside any catch.
        private static async Task<List<string>?> CaptureBodiesAsync(
            Func<Task<CrossBuy.Models.Communication.CommPage<CrossBuy.Models.Communication.CommCommentDto>>> act)
        {
            try
            {
                var page = await act();
                return page.Items.Select(c => c.Body ?? string.Empty).ToList();
            }
            catch (Exception)
            {
                return null;   // refused loudly, which is also a pass
            }
        }

        /// Same idea for a write: reports whether it threw, so the caller can assert on the STORE rather
        /// than on the exception. A write that silently no-ops is still a pass; a write that lands is not.
        private static async Task RunIgnoringRefusalAsync(Func<Task> act)
        {
            try { await act(); }
            catch (Exception) { /* refused loudly — the caller still checks the store */ }
        }

        // ---- structural guards on the endpoints --------------------------------------------------
        [Fact]
        public void The_quotation_endpoints_exist_on_Inventory_and_not_on_Accounting()
        {
            var inventory = StripComments(Read("CrossBuy/Controllers/InventoryController.cs"));
            Assert.Contains("QuotationConversation(int id", inventory);
            Assert.Contains("QuotationConversationAdd(int id", inventory);

            // The document belongs to Inventory, so its conversation is authorized by Inventory. Accounting
            // must not have grown a quotation endpoint as a shortcut.
            var accounting = StripComments(Read("CrossBuy/Controllers/AccountingController.cs"));
            Assert.DoesNotContain("Quotation", accounting.Replace("QuotationDetails", ""));
        }

        [Fact]
        public void The_gate_resolves_company_BEFORE_it_reads_any_row()
        {
            var gate = Member(StripComments(Read("CrossBuy/Controllers/InventoryController.cs")),
                "QuotationConversationGateAsync");

            int resolve = gate.IndexOf("_company.ResolveAsync()", StringComparison.Ordinal);
            int context = gate.IndexOf("TryGetCurrentAsync", StringComparison.Ordinal);
            int permission = gate.IndexOf("inventory.CanAsync", StringComparison.Ordinal);
            int row = gate.IndexOf("_context.Quotations", StringComparison.Ordinal);

            Assert.True(resolve > 0 && context > 0 && permission > 0 && row > 0, "every gate step must be present");
            Assert.True(resolve < context, "company must be resolved before the business context is taken");
            Assert.True(context < permission, "the context must exist before permission is asked");
            Assert.True(permission < row, "permission must be decided before the quotation row is touched");
        }

        [Fact]
        public void The_row_lookup_carries_the_company_IN_the_query()
        {
            // Not "load then compare": a foreign row must never be materialised, which is what keeps
            // foreign and absent indistinguishable.
            var gate = Member(StripComments(Read("CrossBuy/Controllers/InventoryController.cs")),
                "QuotationConversationGateAsync");
            Assert.Matches(new Regex(@"q\.ID\s*==\s*id\s*&&\s*q\.CompanyID\s*==\s*ctx\.CompanyId"), gate);
        }

        [Fact]
        public void The_endpoints_use_the_INVENTORY_authority_and_invent_no_comment_permission()
        {
            var src = StripComments(Read("CrossBuy/Controllers/InventoryController.cs"));
            var gate = Member(src, "QuotationConversationGateAsync");

            Assert.Contains("InventoryAccessService", gate);
            Assert.DoesNotContain("AccountingAccessService", gate);

            // read to load, doc to add — both already published by the module.
            Assert.Contains("QuotationConversationGateAsync(id, \"read\"", src);
            Assert.Contains("QuotationConversationGateAsync(id, \"doc\"", src);

            // No bespoke vocabulary such as "comment", "quotation-comment", "conversation-read".
            foreach (var invented in new[] { "\"comment\"", "\"quotation-comment\"", "\"conversation\"", "\"conversation-read\"" })
                Assert.DoesNotContain(invented, gate);
        }

        [Fact]
        public void Read_and_write_refuse_with_the_SAME_shape_so_neither_is_an_existence_oracle()
        {
            var src = StripComments(Read("CrossBuy/Controllers/InventoryController.cs"));
            var read = Member(src, "QuotationConversation(int id");
            var write = Member(src, "QuotationConversationAdd(int id");

            const string refusal = "NotFound(new { ok = false, code = \"not_found\" })";
            Assert.Contains(refusal, read);
            Assert.Contains(refusal, write);

            // Neither refusal mentions the company, the quotation, or why.
            foreach (var body in new[] { read, write })
            {
                Assert.DoesNotContain("another company", body);
                Assert.DoesNotContain("forbidden", body);
                Assert.DoesNotContain("CompanyId =", body);
            }
        }

        [Fact]
        public void An_unresolved_company_or_a_missing_access_service_fails_closed()
        {
            var gate = Member(StripComments(Read("CrossBuy/Controllers/InventoryController.cs")),
                "QuotationConversationGateAsync");

            Assert.Contains("if (!scope.Ok) return new QuotationConversationGate();", gate);
            Assert.Contains("if (inventory == null) return new QuotationConversationGate();", gate);
            Assert.Matches(new Regex(@"ctx\s*==\s*null\s*\|\|\s*ctx\.CompanyId\s*<=\s*0"), gate);
            // Nothing in the gate returns an allowed result on a failure path.
            Assert.DoesNotContain("return new QuotationConversationGate { Ok = true", gate.Substring(0, gate.IndexOf("_context.Quotations", StringComparison.Ordinal)));
        }

        [Fact]
        public void The_endpoints_never_reach_the_legacy_comment_store()
        {
            var src = StripComments(Read("CrossBuy/Controllers/InventoryController.cs"));
            foreach (var member in new[] { "QuotationConversation(int id", "QuotationConversationAdd(int id", "QuotationConversationGateAsync" })
            {
                var body = Member(src, member);
                Assert.DoesNotContain("DocComment", body);
                Assert.DoesNotContain("DocComments", body);
                Assert.DoesNotContain("_DocTimeline", body);
            }
        }

        [Fact]
        public void No_tenant_authority_other_than_the_business_context_is_used()
        {
            var gate = Member(StripComments(Read("CrossBuy/Controllers/InventoryController.cs")),
                "QuotationConversationGateAsync");
            Assert.DoesNotContain("DefaultCompanyId", gate);
            Assert.DoesNotContain("CompanyId = 1", gate);
            Assert.DoesNotContain("CompanyID == 1", gate);
            Assert.DoesNotContain("Session", gate);
            // `co` is the controller's own resolved company; the gate must not lean on it either.
            Assert.DoesNotMatch(new Regex(@"[^a-zA-Z_]co[^a-zA-Z_]"), gate);
        }

        [Fact]
        public void A_commented_out_endpoint_would_not_count_as_present()
        {
            // Proves the guards above read code, not prose — the trap an earlier route test fell into.
            const string sample = "// public async Task<IActionResult> QuotationConversation(int id) { }";
            Assert.DoesNotContain("QuotationConversation(int id", StripComments(sample));
        }


        // ---- UI convergence (the items that were impossible before d867505) --------------------
        //
        // Items 1 and 2 of the brief — the partial naming no module, and the Accounting callers still
        // supplying their own URLs — are TAB-1 EntityConversationPartialContractTests and are run in
        // regression rather than copied here. What follows is the QUOTATION side of the contract, which
        // is this batch to prove.

        [Fact]
        public void QuotationDetails_renders_the_shared_conversation_panel()
        {
            var view = StripRazorComments(Read("CrossBuy/Views/Inventory/QuotationDetails.cshtml"));
            Assert.Contains("Html.PartialAsync(\"_EntityConversation\")", view);
        }

        [Fact]
        public void QuotationDetails_no_longer_renders_the_legacy_DocTimeline_widget()
        {
            // The whole point of the batch: one screen, one store. A stray include would put the
            // quotation back into DocComments alongside the platform thread.
            var view = StripRazorComments(Read("CrossBuy/Views/Inventory/QuotationDetails.cshtml"));
            Assert.DoesNotContain("Html.PartialAsync(\"_DocTimeline\")", view);
            Assert.DoesNotContain("ViewBag.CommentEntityType", view);
            Assert.DoesNotContain("ViewBag.CommentEntityId", view);
        }

        [Fact]
        public void QuotationDetails_names_its_OWN_endpoints_and_not_Accounting_ones()
        {
            var view = StripRazorComments(Read("CrossBuy/Views/Inventory/QuotationDetails.cshtml"));

            Assert.Contains("ViewBag.ConversationListUrl", view);
            Assert.Contains("ViewBag.ConversationAddUrl", view);
            Assert.Contains("Url.Action(\"QuotationConversation\", \"Inventory\")", view);
            Assert.Contains("Url.Action(\"QuotationConversationAdd\", \"Inventory\")", view);

            // The defect the parameterisation existed to prevent.
            Assert.DoesNotContain("\"Accounting\"", view);
            Assert.DoesNotContain("InvoiceConversation", view);
        }

        [Fact]
        public void QuotationDetails_identifies_the_record_by_the_frozen_registry_constant()
        {
            // The retired legacy pair carried the literal string "Quotation". A literal can drift away
            // from the registry in silence; the constant cannot.
            var view = StripRazorComments(Read("CrossBuy/Views/Inventory/QuotationDetails.cshtml"));
            Assert.Contains("ViewBag.ConversationEntityCode = CrossBuy.BL.Platform.EntityRegistry.Quotation", view);
            Assert.Contains("ViewBag.ConversationEntityId = Model.ID", view);
        }

        [Fact]
        public void The_endpoints_the_quotation_view_names_actually_exist_on_the_inventory_controller()
        {
            // A view naming an action that is absent renders a blank data-list and the panel fetches
            // nothing — the failure the URL parameterisation is meant to make impossible.
            var view = StripRazorComments(Read("CrossBuy/Views/Inventory/QuotationDetails.cshtml"));
            var controller = StripComments(Read("CrossBuy/Controllers/InventoryController.cs"));

            foreach (var action in new[] { "QuotationConversation", "QuotationConversationAdd" })
            {
                Assert.Contains("Url.Action(\"" + action + "\", \"Inventory\")", view);
                Assert.Contains("IActionResult> " + action + "(int id", controller);
            }
        }

        [Fact]
        public void The_quotation_write_endpoint_keeps_its_protection()
        {
            var controller = StripComments(Read("CrossBuy/Controllers/InventoryController.cs"));
            var add = Member(controller, "QuotationConversationAdd(int id");
            int decl = controller.IndexOf("QuotationConversationAdd(int id", StringComparison.Ordinal);
            var attributes = controller.Substring(Math.Max(0, decl - 220), Math.Min(220, decl));

            Assert.Contains("[SessionValidation]", attributes);
            Assert.Contains("[HttpPost]", attributes);
            Assert.Contains("[ValidateAntiForgeryToken]", attributes);
            Assert.Contains("QuotationConversationGateAsync(id, \"doc\"", add);
        }

        private static string StripRazorComments(string source)
        {
            source = Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline);
            source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(source, @"(?<![:/])//[^\n]*", " ");
        }

        // ---- helpers ---------------------------------------------------------------------------
        private static string Read(string relative)
        {
            var path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "expected to find " + relative);
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "CrossBuy", "Controllers", "InventoryController.cs"))) return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }
            throw new DirectoryNotFoundException("repo root not found above " + AppContext.BaseDirectory);
        }

        private static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(source, @"//[^\n]*", " ");
        }

        /// The text of one member, from its signature to the start of the next member at the same level.
        /// Crude on purpose: it only has to be tight enough that an assertion about one endpoint cannot be
        /// satisfied by text belonging to another.
        private static string Member(string source, string signatureFragment)
        {
            int start = source.IndexOf(signatureFragment, StringComparison.Ordinal);
            Assert.True(start > 0, "member not found: " + signatureFragment);
            int next = source.IndexOf("\n\t\tpublic ", start + signatureFragment.Length, StringComparison.Ordinal);
            int nextPrivate = source.IndexOf("\n\t\tprivate ", start + signatureFragment.Length, StringComparison.Ordinal);
            if (nextPrivate > 0 && (next < 0 || nextPrivate < next)) next = nextPrivate;
            return next > 0 ? source.Substring(start, next - start) : source.Substring(start);
        }
    }
}
