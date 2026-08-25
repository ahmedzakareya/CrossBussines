using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =============================================================================================
    // BUSINESS ENTITY CONVERSATIONS — sales invoices and purchase invoices.
    //
    // The product claim under test: a conversation on an invoice is the SAME conversation machinery
    // Tasks already uses, reached through CommEntityRef, and there is no invoice-specific comment
    // store anywhere. So these tests do two different jobs, and both are needed:
    //
    //   * BEHAVIOUR, against the real platform services — the thread is created, the comment is
    //     stored, and a second company reading the same entity id sees nothing.
    //   * STRUCTURE, against the controller and view source — the ORDER of the checks, and the
    //     absence of a second store. Order cannot be observed from a return value: a controller that
    //     asks the platform first and the permission second returns the same JSON on the happy path
    //     and leaks existence on the unhappy one. That is exactly the bug worth a test.
    //
    // Every behavioural assertion RE-READS from a fresh DbContext where it matters, per CLAUDE.md
    // ("Prove from a new context"), rather than trusting the entity that wrote it.
    // =============================================================================================
    public sealed class BusinessEntityConversationTests : IDisposable
    {
        private readonly CommunicationTestHost _host = new();

        public void Dispose() => _host.Dispose();

        // -----------------------------------------------------------------------------------------
        // 1 — the capability, on both invoice families
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_sales_invoice_conversation_is_stored_and_read_through_the_platform()
        {
            var ctx = CommunicationTestHost.Context();
            var entity = new CommEntityRef(EntityRegistry.SalesInvoice, 5501);

            var thread = await _host.Threads().GetOrCreateAsync(ctx, new CommThreadRequest { Entity = entity });
            var added = await _host.Comments().AddAsync(ctx, _host.CommentOn(entity, "Check the freight line."));

            Assert.True(thread.Id > 0);
            Assert.True(added.CommentId > 0);

            // Re-read from a FRESH context: the row is in the platform's own tables, not in memory.
            using var fresh = _host.NewContext();
            var stored = await fresh.Set<CrossBuy.Models.Context.Communication.CommComment>()
                .AsNoTracking().FirstOrDefaultAsync(c => c.Id == added.CommentId);

            Assert.NotNull(stored);
            Assert.Equal(CommunicationTestHost.CompanyId, stored!.CompanyID);
        }

        // The SAME code path with a different registry code. If this needed its own service, its own
        // table or its own controller action body, the entity-reference architecture would not be
        // doing its job — that is the actual claim here, not "purchase invoices also work".
        [Fact]
        public async Task A_purchase_invoice_conversation_uses_the_same_path_with_only_the_code_changed()
        {
            var ctx = CommunicationTestHost.Context();
            var sales = new CommEntityRef(EntityRegistry.SalesInvoice, 7001);
            var purchase = new CommEntityRef(EntityRegistry.PurchaseInvoice, 7001);

            var salesThread = await _host.Threads().GetOrCreateAsync(ctx, new CommThreadRequest { Entity = sales });
            var purchaseThread = await _host.Threads().GetOrCreateAsync(ctx, new CommThreadRequest { Entity = purchase });

            await _host.Comments().AddAsync(ctx, _host.CommentOn(purchase, "Vendor disputes the tax."));

            // Same id, different family — two separate conversations, never merged. An implementation
            // that keyed on the id alone would hand a purchase invoice the sales invoice's thread.
            Assert.NotEqual(salesThread.Id, purchaseThread.Id);

            var page = await _host.Comments().ListAsync(ctx, purchaseThread.Id);
            Assert.Single(page.Items);
            Assert.Equal("Vendor disputes the tax.", page.Items[0].Body);

            var salesPage = await _host.Comments().ListAsync(ctx, salesThread.Id);
            Assert.Empty(salesPage.Items);
        }

        [Fact]
        public async Task Both_invoice_families_are_permitted_by_the_entity_surface()
        {
            foreach (var code in new[] { EntityRegistry.SalesInvoice, EntityRegistry.PurchaseInvoice })
            {
                var decision = await _host.Surface().EvaluateAsync(
                    new CommEntityRef(code, 1), CrossBuy.BL.Communication.CommCapabilities.Comments);

                Assert.True(decision.Allowed, code + " is not permitted to carry comments: " + decision.Reason);
            }
        }

        // -----------------------------------------------------------------------------------------
        // 2 — company isolation, which is the whole security claim
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task Another_companys_caller_cannot_read_the_conversation_on_the_same_entity_id()
        {
            var mine = CommunicationTestHost.Context();
            var entity = new CommEntityRef(EntityRegistry.SalesInvoice, 9100);

            var thread = await _host.Threads().GetOrCreateAsync(mine, new CommThreadRequest { Entity = entity });
            await _host.Comments().AddAsync(mine, _host.CommentOn(entity, "Margin looks wrong on line 3."));

            // Same entity code, same entity id, different company. Invoice ids are per-company, so this
            // is the realistic attack: guess an id you already know exists in your own company.
            var theirs = CommunicationTestHost.Context(
                employeeId: CommunicationTestHost.OtherCompanyEmployee,
                companyId: CommunicationTestHost.OtherCompanyId);

            var theirThread = await _host.Threads().GetOrCreateAsync(theirs, new CommThreadRequest { Entity = entity });

            // They get their OWN empty thread, never the other company's.
            Assert.NotEqual(thread.Id, theirThread.Id);

            var theirPage = await _host.Comments().ListAsync(theirs, theirThread.Id);
            Assert.Empty(theirPage.Items);
        }

        // The body is the thing that must not travel. Asserted explicitly rather than inferred from an
        // empty list, because "empty" and "empty of THIS text" fail differently when a filter regresses.
        [Fact]
        public async Task The_comment_body_never_reaches_another_company()
        {
            const string secret = "Confidential: renegotiate at 12% before quarter end.";
            var mine = CommunicationTestHost.Context();
            var entity = new CommEntityRef(EntityRegistry.SalesInvoice, 9200);

            await _host.Comments().AddAsync(mine, _host.CommentOn(entity, secret));

            var theirs = CommunicationTestHost.Context(
                employeeId: CommunicationTestHost.OtherCompanyEmployee,
                companyId: CommunicationTestHost.OtherCompanyId);

            var theirThread = await _host.Threads().GetOrCreateAsync(theirs, new CommThreadRequest { Entity = entity });
            var theirPage = await _host.Comments().ListAsync(theirs, theirThread.Id);

            Assert.DoesNotContain(theirPage.Items, c => c.Body != null && c.Body.Contains("12%", StringComparison.Ordinal));
            Assert.DoesNotContain(theirPage.Items, c => c.Body == secret);
        }

        [Fact]
        public async Task A_mention_on_an_invoice_stays_inside_the_callers_company()
        {
            var mine = CommunicationTestHost.Context();
            var entity = new CommEntityRef(EntityRegistry.SalesInvoice, 9300);

            await _host.Comments().AddAsync(mine, _host.CommentOn(
                entity, "@[employee:" + CommunicationTestHost.Colleague + "] please confirm the discount."));

            var theirs = CommunicationTestHost.Context(
                employeeId: CommunicationTestHost.OtherCompanyEmployee,
                companyId: CommunicationTestHost.OtherCompanyId);

            var history = await _host.Mentions().GetHistoryAsync(theirs, new CommPageRequest { PageSize = 50 });

            // A mention raised in company 1 must not appear in company 2's mention history at all.
            Assert.Empty(history.Items);

            using var fresh = _host.NewContext();
            var rows = await fresh.Set<CrossBuy.Models.Context.Communication.CommMention>()
                .AsNoTracking().ToListAsync();
            Assert.All(rows, m => Assert.Equal(CommunicationTestHost.CompanyId, m.CompanyID));
        }

        // -----------------------------------------------------------------------------------------
        // 3 — STRUCTURE: order of checks, honest failure, and one store
        // -----------------------------------------------------------------------------------------

        // ORDER IS THE PROPERTY. The permission gate must be consulted before the platform is even
        // asked whether it exists, or the endpoint answers "503 not deployed" to a caller who should
        // have got "no such invoice" — and that difference tells them the invoice is real.
        [Fact]
        public void The_permission_gate_runs_before_the_platform_is_consulted()
        {
            var source = ControllerSource();

            foreach (var action in new[] { "InvoiceConversation", "InvoiceConversationAdd" })
            {
                var body = ActionBody(source, action);

                int gate = body.IndexOf("ConversationGateAsync(", StringComparison.Ordinal);
                int platform = body.IndexOf("TryConversation()", StringComparison.Ordinal);

                Assert.True(gate >= 0, action + " does not call the permission gate at all.");
                Assert.True(platform >= 0, action + " does not ask whether Communication is present.");
                Assert.True(gate < platform,
                    action + " consults the Communication platform BEFORE the permission gate, so an " +
                    "unauthorized caller can tell a real record from a missing one by the status code.");
            }
        }

        // The gate must resolve the company and check the ROW, and must never reach for the
        // compile-time company constant that the rest of this controller still carries.
        [Fact]
        public void The_gate_resolves_the_company_and_verifies_the_row_rather_than_trusting_a_constant()
        {
            var gate = ActionBody(ControllerSource(), "ConversationGateAsync");

            Assert.Contains("_company.ResolveAsync()", gate);
            Assert.Contains("CanAsync(ctx, \"read\"", gate);
            Assert.Contains("CompanyID == ctx.CompanyId", gate);
            Assert.DoesNotContain("DefaultCompanyId", gate);
        }

        // Both refusals must be the SAME result, or the endpoint is an existence oracle.
        [Fact]
        public void An_unauthorized_caller_and_a_missing_record_get_the_identical_refusal()
        {
            var source = ControllerSource();

            foreach (var action in new[] { "InvoiceConversation", "InvoiceConversationAdd" })
            {
                var body = ActionBody(source, action);
                Assert.Contains("if (!gate.Ok) return NotFound(", body);
                Assert.DoesNotContain("Forbid()", body);   // a 403 here would separate the two cases
            }
        }

        [Fact]
        public void A_missing_platform_fails_honestly_with_a_machine_code_rather_than_an_empty_list()
        {
            var source = ControllerSource();

            // Treated as absent unless the WHOLE set resolves: a half-present platform that can list
            // but not add is a worse answer than an honest refusal.
            Assert.Contains("threads is null || comments is null || surface is null ? null", source);
            Assert.Contains("Status503ServiceUnavailable", source);
            Assert.Contains("communication_unavailable", source);

            foreach (var action in new[] { "InvoiceConversation", "InvoiceConversationAdd" })
            {
                Assert.Contains("if (comm == null) return ConversationUnavailable();", ActionBody(source, action));
            }
        }

        // ZERO DUPLICATE STORAGE. The screens must not carry two comment widgets writing two stores.
        [Fact]
        public void The_invoice_screens_carry_one_conversation_panel_and_no_legacy_comment_widget()
        {
            foreach (var view in new[] { "SalesInvoiceDetail", "PurchaseInvoiceDetail" })
            {
                var raw = File.ReadAllText(Path.Combine(
                    RepoRoot(), "CrossBuy", "Views", "Accounting", view + ".cshtml"));

                // MARKUP ONLY. The first version of this test searched the raw file and failed on the
                // replacement's own explanatory comment, which names the widget it removed. A guard that
                // cannot tell an include from a sentence about an include is the wrong guard: strip the
                // Razor comments, then assert. Prose stays free to explain; the assertion stays literal.
                var source = StripRazorComments(raw);

                Assert.Contains("_EntityConversation", source);
                Assert.Contains("ViewBag.ConversationEntityCode", source);

                // _DocTimeline posts to /Comments/* and writes the legacy DocComments table. Two widgets
                // on one screen means a conversation half in each store.
                Assert.DoesNotContain("_DocTimeline", source);
                Assert.DoesNotContain("ViewBag.CommentEntityType", source);
            }
        }

        // And the conversation controller must not have grown a store of its own.
        [Fact]
        public void No_invoice_comment_table_or_DbSet_was_introduced()
        {
            var source = ControllerSource();

            foreach (var forbidden in new[] { "DocComments", "InvoiceComment", "SalesInvoiceComment" })
            {
                Assert.DoesNotContain(forbidden, source);
            }

            var contextSource = File.ReadAllText(Path.Combine(
                RepoRoot(), "CrossBuy", "Models", "Context", "CrossDbContext.cs"));
            Assert.DoesNotContain("InvoiceComment", contextSource);
        }

        // The family list is a closed map, so a caller cannot name an arbitrary entity code and have it
        // forwarded to the platform.
        [Fact]
        public void Only_the_two_declared_families_are_reachable_from_the_query_string()
        {
            var source = ControllerSource();
            var gate = ActionBody(source, "ConversationGateAsync");

            Assert.Contains("ConversationFamilies.TryGetValue(entity, out var code)", gate);
            Assert.Contains("EntityRegistry.SalesInvoice", source);
            Assert.Contains("EntityRegistry.PurchaseInvoice", source);
        }

        // -----------------------------------------------------------------------------------------
        // helpers
        // -----------------------------------------------------------------------------------------

        /// Removes Razor server comments (@* … *@), including multi-line ones, so an assertion about
        /// rendered markup cannot be satisfied — or broken — by prose.
        private static string StripRazorComments(string source)
        {
            var sb = new System.Text.StringBuilder(source.Length);
            int i = 0;
            while (i < source.Length)
            {
                int open = source.IndexOf("@*", i, StringComparison.Ordinal);
                if (open < 0) { sb.Append(source, i, source.Length - i); break; }

                sb.Append(source, i, open - i);
                int close = source.IndexOf("*@", open + 2, StringComparison.Ordinal);
                if (close < 0) { break; }        // unterminated comment: nothing after it is markup
                i = close + 2;
            }
            return sb.ToString();
        }

        private static string ControllerSource() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "AccountingController.cs"));

        /// Everything from a member's signature to the start of the next member at the same indent.
        /// Crude, and sufficient: every assertion here is about ORDER or PRESENCE inside one body.
        private static string ActionBody(string source, string member)
        {
            int start = source.IndexOf(" " + member + "(", StringComparison.Ordinal);
            Assert.True(start >= 0, "member " + member + " not found in AccountingController.cs");

            int next = source.IndexOf("\n\t\tprivate ", start + 1, StringComparison.Ordinal);
            int nextPublic = source.IndexOf("\n\t\tpublic ", start + 1, StringComparison.Ordinal);
            if (nextPublic >= 0 && (next < 0 || nextPublic < next)) { next = nextPublic; }

            return next < 0 ? source[start..] : source[start..next];
        }

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
    }
}
