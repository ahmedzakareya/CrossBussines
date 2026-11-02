using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Crm;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // CRM WAVE 1 — the PLATFORM REGISTRY unblock.
    //
    // WHAT WAS ACTUALLY BLOCKED, and it was one line. BusinessEventService.RecordAsync opens with
    // `_registry.GetDefinition(record.EntityCode)`, which THROWS for a code the registry does not
    // know. CRM has authoritative service writers for Lead, Opportunity and CrmAccount, but the
    // platform had never heard of the codes — so CRM could not publish a single business fact, could
    // not carry a platform tag, and could not appear in a timeline, all for the same reason.
    //
    // WHAT THIS INCREMENT IS NOT. It declares no CRM event, produces none, adds no Task rule and
    // writes no tag assignment. Which CRM facts become canonical events is a business decision that
    // belongs to the CRM increment, and nothing here pre-empts it. The probe event type in R6 is
    // named `PlatformRegistryProbe` precisely so it can never be mistaken for a proposal.
    //
    // COMPANIES ARE 41 AND 77, never 1 — the value every historical default-company bug produced,
    // and the only way to tell "used the caller's company" from "fell back to the default".
    //
    // THE TENANCY TESTS READ THROUGH AN UNFILTERED CONTEXT ON PURPOSE (R8/R9). Lead, Opportunity and
    // CrmAccount are all in CompanyQueryFilters.PilotEntities, so a company-scoped context hides a
    // foreign row before the registry is even consulted — and a test arranged that way passes with a
    // resolver that has NO company predicate at all. Resolving through host.AllCompanies() removes
    // that safety net, so the arm's own `CompanyID == companyId` is the only thing left standing
    // between the caller and another tenant. That is the property under test, so that is the
    // arrangement. Mutating the predicate away kills R8 and R9; with a filtered context it kills
    // nothing.
    // =============================================================================================
    public class CrmPlatformRegistryTests
    {
        private const int CoA = 41;
        private const int CoB = 77;

        // ---- arrangement -------------------------------------------------------------------------

        private static async Task<int> SeedLeadAsync(PlatformTestHost host, int companyId, string name)
        {
            var row = new Lead { CompanyID = companyId, Name = name, Status = "New" };
            host.Seed.Leads.Add(row);
            await host.Seed.SaveChangesAsync();
            return row.ID;
        }

        private static async Task<int> SeedOpportunityAsync(PlatformTestHost host, int companyId, string title)
        {
            var row = new Opportunity { CompanyID = companyId, Title = title, Stage = "Prospecting" };
            host.Seed.Opportunities.Add(row);
            await host.Seed.SaveChangesAsync();
            return row.ID;
        }

        private static async Task<int> SeedAccountAsync(PlatformTestHost host, int companyId, string name)
        {
            var row = new CrmAccount { CompanyID = companyId, Name = name, IsActive = true };
            host.Seed.CrmAccounts.Add(row);
            await host.Seed.SaveChangesAsync();
            return row.ID;
        }

        // ---- R1..R3: the three definitions exist, and say what they should ------------------------

        [Theory]
        [InlineData(EntityRegistry.Lead, "Lead", "/Crm/Leads")]
        [InlineData(EntityRegistry.Opportunity, "Opportunity", "/Crm/Opportunities")]
        [InlineData(EntityRegistry.CrmAccount, "CRM account", "/Crm/AccountEditor?id={id}")]
        public void R1_each_CRM_entity_is_registered_under_the_frozen_naming_convention(
            string code, string displayEn, string route)
        {
            using var host = new PlatformTestHost(CoA);

            Assert.True(host.Registry().TryGetDefinition(code, out var def));

            // Bare PascalCase matching the CLR type — no "CRM." prefix, no "Crm.Account". The convention
            // is not a preference: BusinessEventTypes.Validate requires EventType to be prefixed with
            // exactly this string, so the shape here is the shape every future CRM event must carry.
            Assert.Equal(code, def!.Code);
            Assert.Equal("Crm", def.Module);
            Assert.Equal(EntityRegistry.ScopeCrm, def.PermissionScope);
            Assert.Equal(displayEn, def.DisplayNameEn);
            Assert.False(string.IsNullOrWhiteSpace(def.DisplayNameAr));
            Assert.Equal(route, def.RouteTemplate);
        }

        [Fact]
        public void R2_the_CRM_scope_is_one_a_permission_adapter_actually_answers()
        {
            using var host = new PlatformTestHost(CoA);

            // PermissionScope = ScopeCrm is only meaningful if something is registered to answer it.
            // Projects shipped once with ScopeNone and silently fell through to DefaultPermissionAdapter;
            // this asserts CRM did not repeat that.
            var scopes = host.Registry().GetDefinitions()
                .Where(d => d.Module == "Crm").Select(d => d.PermissionScope).Distinct().ToList();

            Assert.Equal(new[] { EntityRegistry.ScopeCrm }, scopes);
            Assert.Equal(
                EntityRegistry.ScopeCrm,
                new CrossBuy.BL.ModulePermissions.CrmPermissionAdapter(Array.Empty<IModuleAccessService>()).Scope);
        }

        [Fact]
        public void R3_capability_flags_claim_only_what_is_wired()
        {
            using var host = new PlatformTestHost(CoA);
            var registry = host.Registry();

            foreach (var code in new[] { EntityRegistry.Lead, EntityRegistry.Opportunity, EntityRegistry.CrmAccount })
            {
                Assert.True(registry.TryGetDefinition(code, out var def));

                // TIMELINE IS NOW TRUE, and it moved for the only reason the registry rule allows: the
                // capability became real. TimelineEventPresenter learned all fourteen CRM event types in
                // the same commit that flipped this flag, so the strict consumer can render what the
                // producers publish. CrmTimelinePresentationTests holds the standing invariant.
                Assert.True(def!.SupportsTimeline);

                // The rest are still false because they still describe nothing that is wired - CRM has
                // no comment thread, no file attachment and no follower list. The registry rule is
                // unchanged: a flag says what IS wired, not what is planned.
                Assert.False(def.SupportsComments);
                Assert.False(def.SupportsFiles);
                Assert.False(def.SupportsFollowers);

                // Search off, and therefore not in the record picker either — the two must agree, or the
                // picker offers a type whose search returns nothing.
                Assert.False(def.SupportsSearch);
                Assert.False(def.ListedInRecordPicker);
            }
        }

        // ---- R4..R6: the blocker itself -----------------------------------------------------------

        [Theory]
        [InlineData(EntityRegistry.Lead)]
        [InlineData(EntityRegistry.Opportunity)]
        [InlineData(EntityRegistry.CrmAccount)]
        public void R4_GetDefinition_no_longer_throws_for_a_CRM_code(string code)
        {
            using var host = new PlatformTestHost(CoA);

            // THIS IS THE BLOCKER, in one call. Before registration this threw
            // EntityCodeNotRegisteredException, and RecordAsync makes exactly this call on its first line.
            var def = host.Registry().GetDefinition(code);

            Assert.Equal(code, def.Code);
        }

        [Theory]
        [InlineData("CrmLead")]          // the prefixed shape the convention rejects
        [InlineData("Crm.Lead")]         // the dotted shape the convention rejects
        [InlineData("Contact")]          // deliberately NOT registered by this increment
        [InlineData("Campaign")]         // deliberately NOT registered by this increment
        [InlineData("Ticket")]           // deliberately NOT registered by this increment
        [InlineData("")]
        public void R5_an_unregistered_code_still_fails_exactly_as_before(string code)
        {
            using var host = new PlatformTestHost(CoA);
            var registry = host.Registry();

            Assert.False(registry.TryGetDefinition(code, out _));
            Assert.False(registry.IsValid(code));
            Assert.Throws<EntityCodeNotRegisteredException>(() => registry.GetDefinition(code));
        }

        [Fact]
        public async Task R6_the_event_service_accepts_a_CRM_entity_code_it_used_to_reject()
        {
            using var host = new PlatformTestHost(CoA);
            int leadId = await SeedLeadAsync(host, CoA, "شركة النور");

            var req = host.Request(CoA);
            var events = host.Events(req.Db, PlatformTestHost.DefaultContext(CoA));

            // A PROBE, not a proposal. No CRM producer exists and none is added here; which CRM facts
            // become canonical events is the CRM increment's decision. What this asserts is narrower and
            // is the whole point of the increment: the recording path no longer rejects the code.
            long id;
            using (var tx = await req.Db.Database.BeginTransactionAsync())
            {
                id = await events.RecordAsync(new BusinessEventRecord
                {
                    EntityCode = EntityRegistry.Lead,
                    EntityId = leadId,
                    EventType = EntityRegistry.Lead + ".PlatformRegistryProbe",
                });
                await tx.CommitAsync();
            }

            Assert.True(id > 0);

            using var verify = host.NewContext(req.Holder);
            var stored = await verify.BusinessEvents.SingleAsync();
            Assert.Equal(EntityRegistry.Lead, stored.EntityType);
            Assert.Equal(CoA, stored.CompanyID);
        }

        // ---- R7..R9: company resolution, which is the half that carries the tenancy guarantee ------

        [Fact]
        public async Task R7_each_CRM_entity_resolves_to_its_own_record_in_its_own_company()
        {
            using var host = new PlatformTestHost(CoA);
            int leadId = await SeedLeadAsync(host, CoA, "شركة النور");
            int oppId = await SeedOpportunityAsync(host, CoA, "توريد مكاتب");
            int accId = await SeedAccountAsync(host, CoA, "مجموعة الأمل");

            var req = host.Request(CoA);
            var registry = host.Registry(req.Db);
            var ctx = PlatformTestHost.DefaultContext(CoA);

            var lead = await registry.ResolveAsync(EntityRegistry.Lead, leadId, ctx);
            var opp = await registry.ResolveAsync(EntityRegistry.Opportunity, oppId, ctx);
            var acc = await registry.ResolveAsync(EntityRegistry.CrmAccount, accId, ctx);

            Assert.True(lead.Found);
            Assert.Equal("شركة النور", lead.Label);
            Assert.Equal("/Crm/Leads", lead.Url);       // list-only template: no "{id}" to substitute

            Assert.True(opp.Found);
            Assert.Equal("توريد مكاتب", opp.Label);

            Assert.True(acc.Found);
            Assert.Equal("مجموعة الأمل", acc.Label);
            Assert.Equal($"/Crm/AccountEditor?id={accId}", acc.Url);   // the one with a real per-id page
        }

        [Theory]
        [InlineData(EntityRegistry.Lead)]
        [InlineData(EntityRegistry.Opportunity)]
        [InlineData(EntityRegistry.CrmAccount)]
        public async Task R8_another_companys_CRM_record_is_never_resolved(string code)
        {
            using var host = new PlatformTestHost(CoA);

            int foreignId = code switch
            {
                EntityRegistry.Lead => await SeedLeadAsync(host, CoB, "عميل شركة أخرى"),
                EntityRegistry.Opportunity => await SeedOpportunityAsync(host, CoB, "فرصة شركة أخرى"),
                _ => await SeedAccountAsync(host, CoB, "حساب شركة أخرى"),
            };

            // UNFILTERED on purpose — see the class comment. The global query filter is removed so the
            // resolver's own company predicate is the only thing being tested.
            using var unfiltered = host.AllCompanies();
            var registry = host.Registry(unfiltered);

            var result = await registry.ResolveAsync(code, foreignId, PlatformTestHost.DefaultContext(CoA));

            Assert.False(result.Found);
            // It degrades to "#id" with no link — indistinguishable from a deleted record, which is
            // deliberate: telling the two apart would make this a cross-tenant existence probe.
            Assert.Equal("#" + foreignId, result.Label);
            Assert.Null(result.Url);
        }

        [Fact]
        public async Task R9_resolution_follows_the_caller_and_never_falls_back_to_company_one()
        {
            using var host = new PlatformTestHost(CoA);
            int aLead = await SeedLeadAsync(host, CoA, "عميل 41");
            int bLead = await SeedLeadAsync(host, CoB, "عميل 77");

            using var unfiltered = host.AllCompanies();
            var registry = host.Registry(unfiltered);

            // Neither company is 1, and both rows exist. A resolver pinned to a constant, or one reading
            // an ambient session company instead of the passed context, cannot satisfy both directions.
            var asA = await registry.ResolveAsync(EntityRegistry.Lead, aLead, PlatformTestHost.DefaultContext(CoA));
            var asB = await registry.ResolveAsync(EntityRegistry.Lead, bLead, PlatformTestHost.DefaultContext(CoB));
            var crossed = await registry.ResolveAsync(EntityRegistry.Lead, bLead, PlatformTestHost.DefaultContext(CoA));

            Assert.Equal("عميل 41", asA.Label);
            Assert.Equal("عميل 77", asB.Label);
            Assert.False(crossed.Found);
        }

        // ---- R10: nothing else in the frozen vocabulary moved --------------------------------------

        [Fact]
        public void R10_the_existing_vocabulary_is_untouched_and_grew_by_exactly_three()
        {
            using var host = new PlatformTestHost(CoA);
            var codes = host.Registry().GetDefinitions().Select(d => d.Code).ToList();

            var preCrm = new[]
            {
                EntityRegistry.SalesInvoice,
                EntityRegistry.PurchaseOrder,
                EntityRegistry.GoodsReceipt,
                EntityRegistry.SalesOrder,
                EntityRegistry.DeliveryNote,
                EntityRegistry.StockTransfer,
                EntityRegistry.StockCount,
                EntityRegistry.StockWriteOff,
                EntityRegistry.LandedCost,
                EntityRegistry.SalesReturn,
                EntityRegistry.PurchaseReturn,
                EntityRegistry.Receipt,
                EntityRegistry.Payment,
                EntityRegistry.Customer,
                EntityRegistry.PurchaseInvoice,
                EntityRegistry.Quotation,
                EntityRegistry.JournalEntry,
                EntityRegistry.Supplier,
                EntityRegistry.ManufWorkOrder,
                EntityRegistry.PosOrder,
                EntityRegistry.Employee,
                EntityRegistry.Project,
                EntityRegistry.ProjectMilestone,
                EntityRegistry.ProjectBilling,
                EntityRegistry.Item,
                EntityRegistry.Task,
                EntityRegistry.CalendarEvent,
                EntityRegistry.PlatformRoleAssignment,
                EntityRegistry.PlatformEvent,
                EntityRegistry.InventoryApproval,
                EntityRegistry.LeaveRequest,
                EntityRegistry.EmployeeRequest
            };

            Assert.All(preCrm, c => Assert.Contains(c, codes));
            Assert.Equal(preCrm.Length + 3, codes.Count);
            Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        }

        // ---- R11: timeline presentation does not break ---------------------------------------------

        [Fact]
        public async Task R11_a_CRM_event_now_reaches_every_registered_consumer_including_the_timeline()
        {
            using var host = new PlatformTestHost(CoA);
            int leadId = await SeedLeadAsync(host, CoA, "شركة النور");

            var req = host.Request(CoA);
            var events = host.Events(req.Db, PlatformTestHost.DefaultContext(CoA));
            using (var tx = await req.Db.Database.BeginTransactionAsync())
            {
                await events.RecordAsync(new BusinessEventRecord
                {
                    EntityCode = EntityRegistry.Lead,
                    EntityId = leadId,
                    EventType = EntityRegistry.Lead + ".PlatformRegistryProbe",
                });
                await tx.CommitAsync();
            }

            using var verify = host.NewContext(req.Holder);
            var consumers = await verify.BusinessEventDispatches.Select(d => d.Consumer).ToListAsync();

            // THE OTHER SIDE OF THE SAME CAPABILITY RULE. While SupportsTimeline was false this asserted
            // that the timeline consumer got NO dispatch row - which is what kept a flag-without-a-
            // presenter from poisoning the outbox. The presenter now exists and the flag is true, so
            // BusinessEventService.IsEligible admits the timeline consumer and every registered consumer
            // gets a row. The invariant did not weaken; the capability moved, and the assertion follows
            // it in the same direction.
            Assert.Contains(BusinessEventConsumers.TimelineProjection, consumers);
            Assert.Equal(BusinessEventConsumers.Registered.Length, consumers.Count);
        }

        // ---- R12..R14: the reusable Tag platform recognises the same three codes --------------------

        [Theory]
        [InlineData(EntityRegistry.Lead)]
        [InlineData(EntityRegistry.Opportunity)]
        [InlineData(EntityRegistry.CrmAccount)]
        public async Task R12_the_tag_platform_accepts_a_CRM_entity_without_a_second_vocabulary(string code)
        {
            using var host = new PlatformTestHost(CoA);
            int id = code switch
            {
                EntityRegistry.Lead => await SeedLeadAsync(host, CoA, "شركة النور"),
                EntityRegistry.Opportunity => await SeedOpportunityAsync(host, CoA, "توريد مكاتب"),
                _ => await SeedAccountAsync(host, CoA, "مجموعة الأمل"),
            };

            var req = host.Request(CoA);
            var tags = new TagService(req.Db);
            var (created, createError, tag) = await tags.CreateTagAsync(CoA, "عاجل", actorEmployeeId: 5);
            Assert.True(created, createError);

            var (ok, error) = await tags.AssignAsync(CoA, tag!.ID, code, id, actorEmployeeId: 5);

            // ONE company vocabulary, reused. No CrmTag table, no LeadTag table — the same tag row a task
            // uses is the row a lead uses, which is the entire argument for the polymorphic assignment.
            Assert.True(ok, error);
            var onEntity = await tags.GetEntityTagsAsync(CoA, code, id);
            Assert.Equal("عاجل", Assert.Single(onEntity).Name);
        }

        [Fact]
        public async Task R13_a_tag_cannot_be_attached_to_another_companys_CRM_record()
        {
            using var host = new PlatformTestHost(CoA);
            int foreignLead = await SeedLeadAsync(host, CoB, "عميل شركة أخرى");

            var req = host.Request(CoA);
            var tags = new TagService(req.Db);
            var (_, _, tag) = await tags.CreateTagAsync(CoA, "عاجل", 5);

            var (ok, error) = await tags.AssignAsync(CoA, tag!.ID, EntityRegistry.Lead, foreignLead, 5);

            // The three-way check reads the TARGET row's company. Neither the global filter nor the write
            // guard could catch this: both inspect the assignment row, which would claim company 41 quite
            // happily while pointing at company 77's lead.
            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Empty(await tags.GetEntityTagsAsync(CoB, EntityRegistry.Lead, foreignLead));
        }

        [Theory]
        [InlineData(EntityRegistry.Lead)]
        [InlineData(EntityRegistry.Opportunity)]
        [InlineData(EntityRegistry.CrmAccount)]
        public async Task R13b_the_tag_company_check_is_what_refuses_it_not_the_global_filter(string code)
        {
            using var host = new PlatformTestHost(CoA);
            int foreignId = code switch
            {
                EntityRegistry.Lead => await SeedLeadAsync(host, CoB, "عميل شركة أخرى"),
                EntityRegistry.Opportunity => await SeedOpportunityAsync(host, CoB, "فرصة شركة أخرى"),
                _ => await SeedAccountAsync(host, CoB, "حساب شركة أخرى"),
            };

            // R13 is the realistic arrangement and it passes — but it passes for the WRONG REASON, and
            // that was found by mutation rather than by reading: gutting EntityBelongsToCompanyAsync down
            // to `l.ID == entityId` left R13 green, because Lead is a pilot entity and the company-scoped
            // context had already hidden the row. The filter was doing the work the resolver is supposed
            // to do, and a test that cannot tell them apart proves neither.
            //
            // So this one hands TagService an UNFILTERED context. The row is visible, the caller says
            // company 41, the record is company 77, and the three-way check is the only thing left.
            using var unfiltered = host.AllCompanies();
            var tags = new TagService(unfiltered);
            var (_, _, tag) = await tags.CreateTagAsync(CoA, "عاجل", 5);

            var (assigned, assignError) = await tags.AssignAsync(CoA, tag!.ID, code, foreignId, 5);
            Assert.False(assigned);
            Assert.False(string.IsNullOrWhiteSpace(assignError));

            // The replace-the-whole-set path is not a lesser operation and does not get a lesser guard.
            var (replaced, replaceError) = await tags.SetEntityTagsAsync(CoA, code, foreignId, new[] { tag.ID }, 5);
            Assert.False(replaced);
            Assert.False(string.IsNullOrWhiteSpace(replaceError));

            Assert.Empty(await tags.GetEntityTagsAsync(CoB, code, foreignId));
        }

        [Theory]
        [InlineData("Contact")]
        [InlineData("Campaign")]
        [InlineData("Ticket")]
        public async Task R14_the_tag_platform_still_refuses_an_entity_type_this_increment_did_not_register(string code)
        {
            using var host = new PlatformTestHost(CoA);
            var req = host.Request(CoA);
            var tags = new TagService(req.Db);
            var (_, _, tag) = await tags.CreateTagAsync(CoA, "عاجل", 5);

            var (ok, error) = await tags.AssignAsync(CoA, tag!.ID, code, 1, 5);
            var (replaced, replaceError) = await tags.SetEntityTagsAsync(CoA, code, 1, new[] { tag.ID }, 5);

            // Recognition is per code and deliberately not per module: registering three CRM entities did
            // not open the door to every CRM table.
            Assert.False(ok);
            Assert.False(replaced);

            // THE REASON IS ASSERTED, NOT JUST THE REFUSAL, and mutation is why. TagService refuses an
            // unregistered type TWICE - the SupportedEntityTypes gate, and then the resolver switch whose
            // default arm returns false on purpose ("a new entity type must be added in BOTH places to
            // work at all"). Deleting the first gate left an assertion of `!ok` completely green, because
            // the second one still refused. Two independent guards are the right design; a test that
            // cannot tell which one fired is the wrong test, and it would have let the type gate rot away
            // unnoticed. The distinct message is what separates them.
            Assert.Equal(TagService.UnsupportedEntityTypeError, error);
            Assert.Equal(TagService.UnsupportedEntityTypeError, replaceError);
        }
    }
}
