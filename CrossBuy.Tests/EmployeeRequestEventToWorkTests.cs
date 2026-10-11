using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Orchestration;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // ================================================================================================
    // HR EMPLOYEE SERVICE REQUEST — EVENT TO WORK, PHASE A (the platform half).
    //
    // THE CLAIM. The platform can now name an employee request, record the four facts about it, and turn
    // an approval level into exactly one Task for the manager the domain already chose — with the tenant
    // taken from ONE place, nothing about the request disclosed, and a replayed event producing nothing
    // new.
    //
    // WHAT IS DELIBERATELY NOT HERE. There is no production producer: EmployeeRequestService is untouched
    // by this slice and emits no event at all. So these tests record the canonical facts THEMSELVES,
    // through the real BusinessEventService against the real EntityRegistry — which is not a shortcut but
    // the point of Phase A: RecordAsync resolves the entity code through the registry and THROWS on an
    // unknown one, so a suite that could not record an EmployeeRequest event would be proving that the
    // registration does not work. One test records against an unregistered HR code to show that refusal
    // is real and that these events pass for a reason.
    //
    // COMPANIES ARE 41 AND 77, NEVER 1 — company 1 is the value every historical tenancy bug defaulted to.
    //
    // WHY THE RESOLVER TESTS ARE NOT VACUOUS. EmployeeRequest carries NO global query filter:
    // CompanyQueryFilters covers fourteen entities and this is not one of them. So a foreign-company row
    // really is visible to the DbContext, and the arm's own predicate is the only thing refusing it.
    // ================================================================================================
    public class EmployeeRequestEventToWorkTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;

        private const int Requester = 10;
        private const int Manager1 = 20;   // level 1 — the direct manager
        private const int Manager2 = 30;   // level 2 — above them, the unit head
        private const int Outsider = 40;   // same company, nowhere in the chain

        // ==========================================================================================
        // A — ENTITY REGISTRATION. Code, definition and resolve arm: the three that ship together.
        // ==========================================================================================

        [Fact]
        public void A01_the_canonical_entity_code_exists_and_reads_as_the_clr_type()
        {
            Assert.Equal("EmployeeRequest", EntityRegistry.EmployeeRequest);
            Assert.Equal(nameof(EmployeeRequest), EntityRegistry.EmployeeRequest);
        }

        [Fact]
        public void A02_get_definition_succeeds_rather_than_throwing_on_an_unknown_code()
        {
            using var host = new PlatformTestHost(CompanyA);

            var def = host.Registry().GetDefinition(EntityRegistry.EmployeeRequest);

            Assert.Equal(EntityRegistry.EmployeeRequest, def.Code);
            Assert.True(host.Registry().IsValid(EntityRegistry.EmployeeRequest));
        }

        /// Hr, not None. ScopeNone routes to DefaultPermissionAdapter, which grants View to any
        /// authenticated caller in a matching company — and a letter request can be a salary certificate.
        [Fact]
        public void A03_permission_scope_is_hr_so_the_platform_asks_hr_and_not_authentication()
        {
            using var host = new PlatformTestHost(CompanyA);

            var def = host.Registry().GetDefinition(EntityRegistry.EmployeeRequest);

            Assert.Equal(EntityRegistry.ScopeHr, def.PermissionScope);
            Assert.NotEqual(EntityRegistry.ScopeNone, def.PermissionScope);
            Assert.Contains(EntityRegistry.ScopeHr, EntityRegistry.PermissionScopes);
        }

        [Fact]
        public void A04_module_is_hr()
        {
            using var host = new PlatformTestHost(CompanyA);
            Assert.Equal("Hr", host.Registry().GetDefinition(EntityRegistry.EmployeeRequest).Module);
        }

        /// A capability declares what the PLATFORM has WIRED. The screen existing is not the same thing,
        /// and every one of these being false is what keeps an employee request out of places — the
        /// record picker above all — where anyone could browse who is asking for what.
        [Fact]
        public void A05_every_capability_flag_is_false_because_nothing_beyond_resolution_is_wired()
        {
            using var host = new PlatformTestHost(CompanyA);
            var def = host.Registry().GetDefinition(EntityRegistry.EmployeeRequest);

            Assert.False(def.SupportsSearch);
            Assert.False(def.SupportsTimeline);
            Assert.False(def.SupportsComments);
            Assert.False(def.SupportsFiles);
            Assert.False(def.SupportsFollowers);
            Assert.False(def.ListedInRecordPicker);
        }

        /// The same control-record shape LeaveRequest has, asserted against LeaveRequest itself rather
        /// than against six copied literals — so the two cannot drift apart unnoticed.
        [Fact]
        public void A06_the_capability_shape_is_the_leave_request_control_record_shape()
        {
            using var host = new PlatformTestHost(CompanyA);
            var reg = host.Registry();
            var mine = reg.GetDefinition(EntityRegistry.EmployeeRequest);
            var leave = reg.GetDefinition(EntityRegistry.LeaveRequest);

            Assert.Equal(leave.Module, mine.Module);
            Assert.Equal(leave.PermissionScope, mine.PermissionScope);
            Assert.Equal(leave.SupportsSearch, mine.SupportsSearch);
            Assert.Equal(leave.SupportsTimeline, mine.SupportsTimeline);
            Assert.Equal(leave.SupportsComments, mine.SupportsComments);
            Assert.Equal(leave.SupportsFiles, mine.SupportsFiles);
            Assert.Equal(leave.SupportsFollowers, mine.SupportsFollowers);
            Assert.Equal(leave.ListedInRecordPicker, mine.ListedInRecordPicker);
        }

        /// Search is declared false, so SearchAsync returns nothing — and no search arm was written for
        /// an entity that does not declare search, because dead code in a switch is a future reader
        /// believing a path exists.
        [Fact]
        public async Task A07_search_returns_nothing_because_the_entity_does_not_declare_search()
        {
            using var host = new PlatformTestHost(CompanyA);
            Seed(host, Row(1, CompanyA));

            var rows = await host.Registry().SearchAsync(
                EntityRegistry.EmployeeRequest, null, PlatformTestHost.DefaultContext(CompanyA));

            Assert.Empty(rows);
        }

        [Fact]
        public async Task A08_resolve_finds_a_request_in_the_callers_own_company()
        {
            using var host = new PlatformTestHost(CompanyA);
            Seed(host, Row(7, CompanyA));

            var r = await host.Registry().ResolveAsync(
                EntityRegistry.EmployeeRequest, 7, PlatformTestHost.DefaultContext(CompanyA));

            Assert.True(r.Found);
            Assert.Equal("#7", r.Label);
            Assert.Equal("/People/Requests", r.Url);
            Assert.Equal(EntityRegistry.EmployeeRequest, r.EntityCode);
            Assert.Equal(7, r.EntityId);
        }

        [Fact]
        public async Task A09_resolve_refuses_a_request_that_belongs_to_another_company()
        {
            using var host = new PlatformTestHost(CompanyA);
            Seed(host, Row(7, CompanyB));

            // the row really is there and really is visible to this context — the refusal is the arm's
            // own predicate, not a query filter doing the work for it
            Assert.True(await host.Db.EmployeeRequests.AsNoTracking().AnyAsync(r => r.ID == 7));

            var r = await host.Registry().ResolveAsync(
                EntityRegistry.EmployeeRequest, 7, PlatformTestHost.DefaultContext(CompanyA));

            Assert.False(r.Found);
            Assert.Null(r.Url);
        }

        /// The whole point of failing closed: a foreign row and a row that never existed must be the
        /// SAME answer, field for field. Anything else is a cross-tenant existence oracle.
        [Fact]
        public async Task A10_a_foreign_request_and_a_missing_request_are_indistinguishable()
        {
            using var host = new PlatformTestHost(CompanyA);
            Seed(host, Row(7, CompanyB));
            var ctx = PlatformTestHost.DefaultContext(CompanyA);

            var foreign = await host.Registry().ResolveAsync(EntityRegistry.EmployeeRequest, 7, ctx);
            var missing = await host.Registry().ResolveAsync(EntityRegistry.EmployeeRequest, 7777, ctx);

            Assert.Equal(missing.Found, foreign.Found);
            Assert.Equal(missing.Url, foreign.Url);
            Assert.Equal(missing.EntityCode, foreign.EntityCode);
            // the labels differ only by the id the CALLER supplied, which the caller already knew
            Assert.Equal("#" + 7, foreign.Label);
            Assert.Equal("#" + 7777, missing.Label);
        }

        /// TENANCY IS THE ROW'S OWN COLUMN. Both halves of this are needed: a request whose REQUESTER
        /// sits in another company still resolves for its own company, and a request BELONGING to another
        /// company does not resolve even when its requester is one of ours. A resolver that reached
        /// tenancy through the Employee join — the shape LeaveRequest is forced into — fails both.
        [Fact]
        public async Task A11_tenancy_is_the_requests_own_company_column_and_not_the_requesters()
        {
            using var host = new PlatformTestHost(CompanyA);
            host.Seed.Employee.Add(Emp(Requester, CompanyB));       // requester lives in the OTHER company
            Seed(host, Row(1, CompanyA, employeeId: Requester));    // but the request is ours
            Seed(host, Row(2, CompanyB, employeeId: Manager1));     // and this one is not, though Manager1 is
            host.Seed.Employee.Add(Emp(Manager1, CompanyA));
            await host.Seed.SaveChangesAsync();

            var ctx = PlatformTestHost.DefaultContext(CompanyA);
            Assert.True((await host.Registry().ResolveAsync(EntityRegistry.EmployeeRequest, 1, ctx)).Found);
            Assert.False((await host.Registry().ResolveAsync(EntityRegistry.EmployeeRequest, 2, ctx)).Found);
        }

        /// A label is rendered wherever a link to this record appears. RequestType would be the worst
        /// thing to put there: "Letter" plus a salary certificate is a fact about pay.
        [Fact]
        public async Task A12_the_label_is_the_id_and_discloses_nothing_about_the_request()
        {
            using var host = new PlatformTestHost(CompanyA);
            Seed(host, Row(9, CompanyA, requestType: "Letter", letterType: "Salary",
                addressee: "بنك الرياض", reason: "قرض عقاري"));

            var r = await host.Registry().ResolveAsync(
                EntityRegistry.EmployeeRequest, 9, PlatformTestHost.DefaultContext(CompanyA));

            Assert.Equal("#9", r.Label);
            foreach (var secret in new[] { "Letter", "Salary", "بنك الرياض", "قرض عقاري", "emp10" })
            {
                Assert.DoesNotContain(secret, r.Label ?? "");
                Assert.DoesNotContain(secret, r.Url ?? "");
            }
        }

        /// Resolution answers whether the record EXISTS in this company — never whether it is still
        /// actionable, and never who may act on it. A decided request still resolves, or a link from a
        /// completed task would go dead the moment the request was answered.
        [Fact]
        public async Task A13_resolution_carries_no_status_filter_and_no_authorization_decision()
        {
            using var host = new PlatformTestHost(CompanyA);
            Seed(host, Row(4, CompanyA, status: 2));   // rejected

            var r = await host.Registry().ResolveAsync(
                EntityRegistry.EmployeeRequest, 4, PlatformTestHost.DefaultContext(CompanyA));

            Assert.True(r.Found);
        }

        /// A list-only route: the template carries no {id}, so BuildUrl returns it unchanged. Stated as a
        /// test because it is a real navigation limitation, not an oversight — see the report.
        [Fact]
        public void A14_the_route_is_the_existing_list_screen_and_no_new_endpoint_was_invented()
        {
            using var host = new PlatformTestHost(CompanyA);
            var def = host.Registry().GetDefinition(EntityRegistry.EmployeeRequest);

            Assert.Equal("/People/Requests", def.RouteTemplate);
            Assert.DoesNotContain("{id}", def.RouteTemplate);
            Assert.Equal("/People/Requests", host.Registry().BuildUrl(EntityRegistry.EmployeeRequest, 12));
        }

        /// Registering this entity must not have moved any other one.
        [Fact]
        public void A15_no_other_entity_definition_changed()
        {
            using var host = new PlatformTestHost(CompanyA);
            var reg = host.Registry();

            var leave = reg.GetDefinition(EntityRegistry.LeaveRequest);
            Assert.Equal("/People/Leaves", leave.RouteTemplate);
            Assert.Equal(EntityRegistry.ScopeHr, leave.PermissionScope);
            Assert.False(leave.ListedInRecordPicker);

            var billing = reg.GetDefinition(EntityRegistry.ProjectBilling);
            Assert.Equal(EntityRegistry.ScopeProjects, billing.PermissionScope);

            var invoice = reg.GetDefinition(EntityRegistry.SalesInvoice);
            Assert.Equal(EntityRegistry.ScopeAccounting, invoice.PermissionScope);
            Assert.True(invoice.SupportsTimeline);
        }

        /// Registering the request did NOT register its steps. Each entity is its own platform decision.
        [Fact]
        public void A16_the_approval_step_is_still_not_an_entity()
        {
            using var host = new PlatformTestHost(CompanyA);
            Assert.False(host.Registry().IsValid("EmployeeRequestStep"));
        }

        // ==========================================================================================
        // B — EVENT VOCABULARY. Validated against the real registry definition, never a loose string.
        // ==========================================================================================

        [Fact]
        public void B01_four_constants_exist_they_are_distinct_and_each_carries_the_entity_prefix()
        {
            var all = new[]
            {
                EmployeeRequestEvents.Submitted, EmployeeRequestEvents.Advanced,
                EmployeeRequestEvents.Approved, EmployeeRequestEvents.Rejected,
            };

            Assert.Equal(4, all.Distinct(StringComparer.Ordinal).Count());
            foreach (var t in all)
                Assert.StartsWith(EntityRegistry.EmployeeRequest + ".", t, StringComparison.Ordinal);
        }

        [Fact]
        public void B02_every_constant_validates_against_the_real_registry_definition()
        {
            using var host = new PlatformTestHost(CompanyA);
            var code = host.Registry().GetDefinition(EntityRegistry.EmployeeRequest).Code;

            foreach (var t in AllEvents())
            {
                Assert.True(BusinessEventTypes.TryValidate(t, code, out var error), $"{t}: {error}");
                Assert.Equal(EntityRegistry.EmployeeRequest, BusinessEventTypes.Build(code, BusinessEventTypes.ActionOf(t)).Split('.')[0]);
            }
        }

        /// The prefix must MATCH the entity it is recorded against. This is the guarantee that a leave
        /// fact cannot be filed under an employee request, or the two families would share a log.
        [Fact]
        public void B03_a_constant_recorded_against_the_wrong_entity_fails_validation()
        {
            foreach (var t in AllEvents())
                Assert.False(BusinessEventTypes.TryValidate(t, EntityRegistry.LeaveRequest, out _));

            Assert.False(BusinessEventTypes.TryValidate(
                LeaveRequestEvents.Submitted, EntityRegistry.EmployeeRequest, out _));
        }

        [Fact]
        public void B04_malformed_employee_request_event_types_are_refused()
        {
            const string code = EntityRegistry.EmployeeRequest;

            Assert.False(BusinessEventTypes.TryValidate("EmployeeRequestSubmitted", code, out _));   // fused
            Assert.False(BusinessEventTypes.TryValidate("employee_request_submitted", code, out _)); // snake
            Assert.False(BusinessEventTypes.TryValidate("EmployeeRequest.submitted", code, out _));  // camel action
            Assert.False(BusinessEventTypes.TryValidate("EmployeeRequest.Sub.Mitted", code, out _)); // two dots
            Assert.False(BusinessEventTypes.TryValidate("EmployeeRequest.", code, out _));           // no action
        }

        /// THE VOCABULARY IS CLOSED. No Changed, Cancelled, Withdrawn or Reopened — the domain has no
        /// such transition, and a constant is a claim that the platform can record a real fact.
        [Fact]
        public void B05_no_transition_the_domain_does_not_have_was_invented()
        {
            var names = typeof(EmployeeRequestEvents)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral).Select(f => f.Name).ToList();

            Assert.Equal(4, names.Count);
            foreach (var absent in new[] { "Changed", "Cancelled", "Withdrawn", "Reopened", "Returned", "Escalated" })
                Assert.DoesNotContain(absent, names);
        }

        /// THE STRING IS THE CONTRACT, AND NOT THE CONSTANT NAME. Every other test here reaches the
        /// vocabulary through the constants, so renaming a value consistently would go unnoticed - and a
        /// stored row carries the LITERAL, so a consumer deployed against the old spelling would simply
        /// stop matching, silently and for ever. The four values are pinned here for that reason.
        [Fact]
        public void B07_the_four_event_type_strings_are_pinned_to_their_exact_values()
        {
            Assert.Equal("EmployeeRequest.Submitted", EmployeeRequestEvents.Submitted);
            Assert.Equal("EmployeeRequest.Advanced", EmployeeRequestEvents.Advanced);
            Assert.Equal("EmployeeRequest.Approved", EmployeeRequestEvents.Approved);
            Assert.Equal("EmployeeRequest.Rejected", EmployeeRequestEvents.Rejected);

            // and the action half is the leave family word for word, which is the parity the vocabulary
            // claims out loud rather than merely happening to have
            var pairs = new[]
            {
                (EmployeeRequestEvents.Submitted, LeaveRequestEvents.Submitted),
                (EmployeeRequestEvents.Advanced, LeaveRequestEvents.Advanced),
                (EmployeeRequestEvents.Approved, LeaveRequestEvents.Approved),
                (EmployeeRequestEvents.Rejected, LeaveRequestEvents.Rejected),
            };
            foreach (var (mine, leave) in pairs)
                Assert.Equal(BusinessEventTypes.ActionOf(leave), BusinessEventTypes.ActionOf(mine));
        }

        /// The registration is what makes these recordable AT ALL — proven by recording one, and by
        /// showing that an unregistered HR code is still refused by the same call.
        [Fact]
        public async Task B06_the_registration_is_what_lets_the_platform_record_the_fact()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(5);

            await rig.RecordAsync(5, EmployeeRequestEvents.Submitted, Payload(5, level: 1, approver: Manager1));

            var stored = Assert.Single(rig.Events_());
            Assert.Equal(EntityRegistry.EmployeeRequest, stored.EntityType);
            Assert.Equal(EmployeeRequestEvents.Submitted, stored.EventType);
            Assert.Equal(CompanyA, stored.CompanyID);
            Assert.Equal(EmployeeRequestEventPayload.Version, stored.PayloadVersion);

            await Assert.ThrowsAnyAsync<Exception>(() => rig.RecordRawAsync(
                "EmployeeRequestStep", 5, "EmployeeRequestStep.Submitted", null));
        }

        // ==========================================================================================
        // C — PAYLOAD V1. What it carries, and the two things it must never carry.
        // ==========================================================================================

        [Fact]
        public void C01_the_payload_carries_exactly_the_v1_fields()
        {
            var names = PayloadProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

            Assert.Equal(new[]
            {
                "ApproverEmployeeId", "DecidedByEmployeeId", "EmployeeRequestId", "Level",
                "NextApproverEmployeeId", "NextLevel", "RequestType", "RequesterEmployeeId",
            }, names);
            Assert.Equal(1, EmployeeRequestEventPayload.Version);
        }

        // ------------------------------------------------------------------------------------------
        // THE NO-COMPANYID RATCHET. Reflection over the type, so it holds against a property, a field,
        // an auto-property or a differently-cased spelling — and it fires the moment somebody
        // reintroduces the duplicate tenant authority this slice deliberately removed.
        // ------------------------------------------------------------------------------------------
        [Fact]
        public void C02_the_payload_has_no_company_identity_of_any_spelling()
        {
            var members = typeof(EmployeeRequestEventPayload)
                .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Select(m => m.Name).ToList();

            Assert.NotEmpty(members);
            foreach (var name in members)
            {
                Assert.False(string.Equals(name, "CompanyId", StringComparison.OrdinalIgnoreCase),
                    $"{name}: the payload must carry no company — tenant authority is envelope.Context.CompanyId.");
                Assert.False(string.Equals(name, "TenantId", StringComparison.OrdinalIgnoreCase), name);
                Assert.False(string.Equals(name, "BranchId", StringComparison.OrdinalIgnoreCase), name);
            }
        }

        /// The same refusal at the wire, because the projection reads JSON and not the CLR type.
        [Fact]
        public void C03_the_serialized_payload_has_no_company_key()
        {
            var json = ToCanonicalJson(Payload(3, level: 1, approver: Manager1));

            foreach (var key in json.AsObject().Select(kv => kv.Key))
                Assert.False(key.Contains("company", StringComparison.OrdinalIgnoreCase), key);
        }

        [Fact]
        public void C04_the_payload_carries_no_decision_note_attachment_or_free_text()
        {
            var names = PayloadProperties().Select(p => p.Name).ToList();

            foreach (var forbidden in new[]
            {
                "DecisionNote", "Reason", "LetterType", "Addressee", "PermissionDate", "FromTime",
                "ToTime", "Attachments", "FilePath", "Salary", "NationalId", "CivilId", "Note",
            })
                Assert.DoesNotContain(forbidden, names);

            // and nothing that is not an int, an int? or the one routing discriminator
            foreach (var p in PayloadProperties())
                Assert.True(p.PropertyType == typeof(int) || p.PropertyType == typeof(int?)
                            || (p.PropertyType == typeof(string) && p.Name == "RequestType"),
                    $"{p.Name} is {p.PropertyType.Name}");
        }

        /// Round-tripped through the canonical event mechanism — serialized by BusinessEventService and
        /// parsed back by BuildEnvelope — rather than through a JsonSerializer call this test chose.
        [Fact]
        public async Task C05_the_payload_survives_the_canonical_event_json_round_trip()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(6);
            var sent = Payload(6, level: 2, approver: Manager2, next: 3, nextApprover: Outsider,
                decidedBy: Manager1, requestType: "Permission");

            await rig.RecordAsync(6, EmployeeRequestEvents.Advanced, sent);

            var envelope = rig.Events.BuildEnvelope(Assert.Single(rig.Events_()));
            var back = envelope.Payload.Deserialize<EmployeeRequestEventPayload>(Web)!;

            Assert.Equal(sent.EmployeeRequestId, back.EmployeeRequestId);
            Assert.Equal(sent.RequesterEmployeeId, back.RequesterEmployeeId);
            Assert.Equal(sent.RequestType, back.RequestType);
            Assert.Equal(sent.Level, back.Level);
            Assert.Equal(sent.ApproverEmployeeId, back.ApproverEmployeeId);
            Assert.Equal(sent.NextLevel, back.NextLevel);
            Assert.Equal(sent.NextApproverEmployeeId, back.NextApproverEmployeeId);
            Assert.Equal(sent.DecidedByEmployeeId, back.DecidedByEmployeeId);

            // and the envelope — not the payload — is where the company comes from
            Assert.Equal(CompanyA, envelope.Context.CompanyId);
            Assert.Null(envelope.Payload!["companyId"]);
        }

        // ==========================================================================================
        // D — SUBMITTED. One level owed, one task, and nothing invented.
        // ==========================================================================================

        [Fact]
        public async Task D01_a_submitted_fact_becomes_exactly_one_task_for_the_named_manager()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(11);
            await rig.RecordAsync(11, EmployeeRequestEvents.Submitted, Payload(11, level: 1, approver: Manager1));

            await rig.DispatchAllAsync();

            var task = Assert.Single(rig.Tasks());
            Assert.Equal(CompanyA, task.CompanyId);
            Assert.Equal(Manager1, task.AssigneeEmployeeId);
            Assert.NotEqual(Requester, task.AssigneeEmployeeId);
            Assert.Equal(EntityRegistry.EmployeeRequest, task.EntityType);
            Assert.Equal(11, task.EntityId);
            Assert.Equal(TaskStatuses.New, task.Status);
            Assert.Equal(0, task.CreatedByEmployeeId);      // the system, not an impersonated person
            Assert.Null(task.DueDate);                      // no invented SLA
            Assert.Null(task.Description);
            Assert.Equal(EmployeeRequestApprovalRule.KeyFor(11, 1), Assert.Single(rig.Ledger()).RuleKey);
        }

        /// The entity code on the task is the REGISTRY CONSTANT, which is what makes the link resolvable
        /// and the permission question answerable. A task carrying an unregistered string would open a
        /// dead link for exactly the people entitled to it.
        [Fact]
        public async Task D02_the_task_entity_type_resolves_through_the_registry()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(12);
            await rig.RecordAsync(12, EmployeeRequestEvents.Submitted, Payload(12, level: 1, approver: Manager1));
            await rig.DispatchAllAsync();

            var task = Assert.Single(rig.Tasks());
            var resolved = await rig.Host.Registry().ResolveAsync(
                task.EntityType!, task.EntityId!.Value, PlatformTestHost.DefaultContext(CompanyA));

            Assert.True(resolved.Found);
            Assert.Equal("#12", resolved.Label);
        }

        [Fact]
        public async Task D03_the_rule_key_names_the_request_and_the_level_and_ends_in_a_colon()
        {
            string key = EmployeeRequestApprovalRule.KeyFor(5, 1);

            Assert.Contains(EntityRegistry.EmployeeRequest, key);
            Assert.EndsWith(":5:level:1:", key, StringComparison.Ordinal);
            Assert.NotEqual(EmployeeRequestApprovalRule.KeyFor(5, 1), EmployeeRequestApprovalRule.KeyFor(5, 11));
            Assert.NotEqual(EmployeeRequestApprovalRule.KeyFor(5, 1), EmployeeRequestApprovalRule.KeyFor(50, 1));
            // and it cannot collide with the leave family, which uses the same (id, level) shape
            Assert.NotEqual(LeaveApprovalRule.KeyFor(5, 1), EmployeeRequestApprovalRule.KeyFor(5, 1));

            // the trailing colon is what stops level 1 completing level 11 by prefix
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(5);
            await rig.RecordAsync(5, EmployeeRequestEvents.Submitted, Payload(5, level: 11, approver: Manager1));
            await rig.DispatchAllAsync();
            await rig.DispatchOneAsync(Env(EmployeeRequestEvents.Approved, 5, CompanyA, Payload(5, level: 1)));

            Assert.Equal(TaskStatuses.New, Assert.Single(rig.Tasks()).Status);   // level 11 survived
        }

        // ==========================================================================================
        // E / F — THE TWO REFUSALS.
        // ==========================================================================================

        /// LEVEL 0 IS THE EMPTY-CHAIN AUTO-APPROVAL. Nobody was ever asked, so there is no work: a task
        /// raised only to be completed in the same breath is a record of work nobody did.
        [Fact]
        public async Task E01_level_zero_creates_no_task_at_all()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(20);

            await rig.RecordAsync(20, EmployeeRequestEvents.Submitted, Payload(20, level: 0, approver: null));
            await rig.RecordAsync(20, EmployeeRequestEvents.Approved, Payload(20, level: 0, decidedBy: Requester));
            await rig.DispatchAllAsync();

            Assert.Empty(rig.Tasks());
            Assert.Empty(rig.Ledger());
            Assert.Equal(2, rig.Events_().Count);   // both facts really happened and were recorded
        }

        [Fact]
        public async Task E02_a_negative_level_is_refused_the_same_way()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(21);
            await rig.DispatchOneAsync(Env(EmployeeRequestEvents.Submitted, 21, CompanyA, Payload(21, level: -1, approver: Manager1)));
            Assert.Empty(rig.Tasks());
        }

        /// E01 COULD HAVE PASSED FOR THE WRONG REASON, and a surviving mutant said so: its empty-chain
        /// payload names nobody, so the refusal there might have come from the missing approver rather
        /// than from the level guard. This is the same fact with an approver PRESENT, which only the
        /// level guard can refuse. Without it a phantom task appears at a level nobody was ever asked at.
        [Fact]
        public async Task E03_level_zero_is_refused_by_the_level_guard_and_not_by_a_missing_approver()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(25);

            await rig.DispatchOneAsync(Env(EmployeeRequestEvents.Submitted, 25, CompanyA,
                Payload(25, level: 0, approver: Manager1)));

            Assert.Empty(rig.Tasks());
            Assert.Empty(rig.Ledger());
        }

        /// Work that would let somebody approve their own request is not created. Defence in depth and
        /// NOT the primary control — this decides no product policy about self-approval.
        [Fact]
        public async Task F01_a_level_whose_approver_is_the_requester_creates_no_task()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(22);

            await rig.RecordAsync(22, EmployeeRequestEvents.Submitted, Payload(22, level: 1, approver: Requester));
            await rig.DispatchAllAsync();

            Assert.Empty(rig.Tasks());
        }

        [Fact]
        public async Task F02_an_advance_onto_the_requester_creates_no_next_task_but_still_closes_the_old_one()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(23);
            await rig.RecordAsync(23, EmployeeRequestEvents.Submitted, Payload(23, level: 1, approver: Manager1));
            await rig.DispatchAllAsync();

            await rig.DispatchOneAsync(Env(EmployeeRequestEvents.Advanced, 23, CompanyA,
                Payload(23, level: 1, approver: Manager1, next: 2, nextApprover: Requester, decidedBy: Manager1)));

            var task = Assert.Single(rig.Tasks());
            Assert.Equal(TaskStatuses.Done, task.Status);
        }

        [Fact]
        public async Task F03_a_level_with_no_named_approver_creates_no_task()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(24);
            await rig.DispatchOneAsync(Env(EmployeeRequestEvents.Submitted, 24, CompanyA, Payload(24, level: 1, approver: null)));
            Assert.Empty(rig.Tasks());
        }

        // ==========================================================================================
        // G — ADVANCED. The one fact with two projections.
        // ==========================================================================================

        [Fact]
        public async Task G01_an_advance_closes_the_level_that_decided_and_opens_the_one_that_now_owes()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(30);
            await rig.RecordAsync(30, EmployeeRequestEvents.Submitted, Payload(30, level: 1, approver: Manager1));
            await rig.DispatchAllAsync();
            Assert.Single(rig.Tasks());

            await rig.DispatchOneAsync(Env(EmployeeRequestEvents.Advanced, 30, CompanyA,
                Payload(30, level: 1, approver: Manager1, next: 2, nextApprover: Manager2, decidedBy: Manager1)));

            var tasks = rig.Tasks();
            Assert.Equal(2, tasks.Count);

            var closed = tasks.Single(t => t.AssigneeEmployeeId == Manager1);
            var opened = tasks.Single(t => t.AssigneeEmployeeId == Manager2);
            Assert.Equal(TaskStatuses.Done, closed.Status);
            Assert.Equal(TaskStatuses.New, opened.Status);
            Assert.Equal(EntityRegistry.EmployeeRequest, opened.EntityType);
            Assert.Equal(30, opened.EntityId);

            Assert.Contains(rig.Ledger(), l => l.RuleKey == EmployeeRequestApprovalRule.KeyFor(30, 1));
            Assert.Contains(rig.Ledger(), l => l.RuleKey == EmployeeRequestApprovalRule.KeyFor(30, 2));
        }

        /// The next approver comes from the event and from nowhere else — no hierarchy is re-walked, no
        /// manager re-resolved, no session read. Proven by naming somebody the org tree would never pick.
        [Fact]
        public async Task G02_the_next_approver_is_the_one_the_event_names()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(31);
            await rig.RecordAsync(31, EmployeeRequestEvents.Submitted, Payload(31, level: 1, approver: Manager1));
            await rig.DispatchAllAsync();

            await rig.DispatchOneAsync(Env(EmployeeRequestEvents.Advanced, 31, CompanyA,
                Payload(31, level: 1, approver: Manager1, next: 7, nextApprover: Outsider, decidedBy: Manager1)));

            var opened = rig.Tasks().Single(t => t.Status == TaskStatuses.New);
            Assert.Equal(Outsider, opened.AssigneeEmployeeId);
            Assert.Contains(rig.Ledger(), l => l.RuleKey == EmployeeRequestApprovalRule.KeyFor(31, 7));
        }

        [Fact]
        public async Task G03_an_advance_with_no_next_level_opens_nothing_and_still_closes_the_old_level()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(32);
            await rig.RecordAsync(32, EmployeeRequestEvents.Submitted, Payload(32, level: 1, approver: Manager1));
            await rig.DispatchAllAsync();

            await rig.DispatchOneAsync(Env(EmployeeRequestEvents.Advanced, 32, CompanyA,
                Payload(32, level: 1, approver: Manager1, next: 0, decidedBy: Manager1)));

            Assert.Equal(TaskStatuses.Done, Assert.Single(rig.Tasks()).Status);
        }

        /// A three-level climb walked end to end: the chain is not two special cases.
        [Fact]
        public async Task G04_a_three_level_climb_leaves_one_task_per_level_each_closed_when_decided()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(33);
            await rig.RecordAsync(33, EmployeeRequestEvents.Submitted, Payload(33, level: 1, approver: Manager1));
            await rig.DispatchAllAsync();

            await rig.DispatchOneAsync(Env(EmployeeRequestEvents.Advanced, 33, CompanyA,
                Payload(33, level: 1, approver: Manager1, next: 2, nextApprover: Manager2, decidedBy: Manager1)));
            await rig.DispatchOneAsync(Env(EmployeeRequestEvents.Advanced, 33, CompanyA,
                Payload(33, level: 2, approver: Manager2, next: 3, nextApprover: Outsider, decidedBy: Manager2)));
            await rig.DispatchOneAsync(Env(EmployeeRequestEvents.Approved, 33, CompanyA,
                Payload(33, level: 3, approver: Outsider, decidedBy: Outsider)));

            var tasks = rig.Tasks();
            Assert.Equal(3, tasks.Count);
            Assert.All(tasks, t => Assert.Equal(TaskStatuses.Done, t.Status));
            Assert.Equal(new[] { Manager1, Manager2, Outsider }, tasks.Select(t => t.AssigneeEmployeeId).ToArray());
        }

        // ==========================================================================================
        // H — THE TERMINAL FACTS. Opposite outcomes, identical WORK.
        // ==========================================================================================

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task H01_a_terminal_decision_closes_the_level_and_creates_nothing(bool approved)
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(40);
            await rig.RecordAsync(40, EmployeeRequestEvents.Submitted, Payload(40, level: 1, approver: Manager1));
            await rig.DispatchAllAsync();
            int before = rig.Tasks().Count;

            await rig.DispatchOneAsync(Env(
                approved ? EmployeeRequestEvents.Approved : EmployeeRequestEvents.Rejected,
                40, CompanyA, Payload(40, level: 1, approver: Manager1, decidedBy: Manager1)));

            var task = Assert.Single(rig.Tasks());
            Assert.Equal(before, rig.Tasks().Count);
            Assert.Equal(TaskStatuses.Done, task.Status);   // Done for both — never a Cancelled state
        }

        /// A rejection ends the WHOLE request, so nothing is opened above it even when the payload
        /// happens to carry next fields.
        [Fact]
        public async Task H02_a_rejection_opens_no_next_level()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(41);
            await rig.RecordAsync(41, EmployeeRequestEvents.Submitted, Payload(41, level: 1, approver: Manager1));
            await rig.DispatchAllAsync();

            await rig.DispatchOneAsync(Env(EmployeeRequestEvents.Rejected, 41, CompanyA,
                Payload(41, level: 1, approver: Manager1, next: 2, nextApprover: Manager2, decidedBy: Manager1)));

            Assert.Single(rig.Tasks());
            Assert.Equal(TaskStatuses.Done, rig.Tasks()[0].Status);
        }

        /// A terminal fact with nothing projected behind it — replayed from before the rule existed, or
        /// delivered out of order — must not CREATE work. It is inert, not an error.
        [Fact]
        public async Task H03_a_terminal_fact_with_no_open_task_creates_none()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(42);

            await rig.DispatchOneAsync(Env(EmployeeRequestEvents.Approved, 42, CompanyA,
                Payload(42, level: 1, approver: Manager1, decidedBy: Manager1)));

            Assert.Empty(rig.Tasks());
            Assert.Empty(rig.Ledger());
        }

        // ==========================================================================================
        // I — WHAT THE RULE MUST NOT ANSWER.
        // ==========================================================================================

        [Fact]
        public void I01_an_unknown_employee_request_event_is_inert_rather_than_an_error()
        {
            var rule = new EmployeeRequestApprovalRule();
            var close = new EmployeeRequestApprovalAdvanceCloseRule();

            foreach (var unknown in new[]
            {
                "EmployeeRequest.Cancelled", "EmployeeRequest.Withdrawn", "EmployeeRequest.Changed",
                "EmployeeRequest.Reopened",
            })
            {
                var env = Env(unknown, 1, CompanyA, Payload(1, level: 1, approver: Manager1));
                Assert.Equal(TaskProjectionAction.Ignore, rule.Decide(env).Action);
                Assert.Equal(TaskProjectionAction.Ignore, close.Decide(env).Action);
            }
        }

        /// EXACT ENTITY CODE. Another entity growing its own Approved must not be answered here.
        [Fact]
        public void I02_another_entity_is_never_answered_even_with_an_identical_action()
        {
            var rule = new EmployeeRequestApprovalRule();
            var close = new EmployeeRequestApprovalAdvanceCloseRule();

            foreach (var code in new[]
            {
                EntityRegistry.LeaveRequest, EntityRegistry.InventoryApproval,
                EntityRegistry.ProjectBilling, "EmployeeRequestStep", "EmployeeRequest2",
            })
            {
                var submitted = new BusinessEventEnvelope
                {
                    EventUid = Guid.NewGuid(),
                    EventType = code + ".Submitted",
                    Entity = new BusinessEventEntity { Code = code, Id = 1 },
                    Actor = new BusinessEventActor { EmployeeId = Manager1 },
                    Context = new BusinessEventContext { CompanyId = CompanyA },
                    PayloadVersion = 1,
                    Visibility = BusinessEventVisibility.Internal,
                    OccurredAt = DateTime.UtcNow,
                    Payload = ToCanonicalJson(Payload(1, level: 1, approver: Manager1)),
                };
                Assert.Equal(TaskProjectionAction.Ignore, rule.Decide(submitted).Action);
                Assert.Equal(TaskProjectionAction.Ignore, close.Decide(submitted).Action);
            }
        }

        [Fact]
        public void I03_a_missing_or_malformed_payload_degrades_to_ignore_and_never_throws()
        {
            var rule = new EmployeeRequestApprovalRule();

            Assert.Equal(TaskProjectionAction.Ignore,
                rule.Decide(EnvRaw(EmployeeRequestEvents.Submitted, 1, CompanyA, null)).Action);
            Assert.Equal(TaskProjectionAction.Ignore,
                rule.Decide(EnvRaw(EmployeeRequestEvents.Submitted, 1, CompanyA, JsonNode.Parse("{}"))).Action);
            Assert.Equal(TaskProjectionAction.Ignore, rule.Decide(EnvRaw(
                EmployeeRequestEvents.Submitted, 1, CompanyA,
                JsonNode.Parse("{\"level\":\"one\",\"approverEmployeeId\":\"x\"}"))).Action);
        }

        [Fact]
        public void I04_an_entity_id_of_zero_is_no_identity_and_no_work()
        {
            var env = Env(EmployeeRequestEvents.Submitted, 0, CompanyA, Payload(0, level: 1, approver: Manager1));
            Assert.Equal(TaskProjectionAction.Ignore, new EmployeeRequestApprovalRule().Decide(env).Action);
            Assert.Equal(TaskProjectionAction.Ignore, new EmployeeRequestApprovalAdvanceCloseRule().Decide(env).Action);
        }

        // ==========================================================================================
        // J — REPLAY. A redelivered dispatch row is the common path, not a failure.
        // ==========================================================================================

        [Fact]
        public async Task J01_a_replayed_submission_produces_one_task_in_total()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(50);
            await rig.RecordAsync(50, EmployeeRequestEvents.Submitted, Payload(50, level: 1, approver: Manager1));

            await rig.DispatchAllAsync();
            await rig.DispatchAllAsync();
            await rig.DispatchAllAsync();

            Assert.Single(rig.Tasks());
            Assert.Single(rig.Ledger());
        }

        [Fact]
        public async Task J02_a_replayed_advance_produces_one_next_task_in_total()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(51);
            await rig.RecordAsync(51, EmployeeRequestEvents.Submitted, Payload(51, level: 1, approver: Manager1));
            await rig.DispatchAllAsync();

            var advance = Env(EmployeeRequestEvents.Advanced, 51, CompanyA,
                Payload(51, level: 1, approver: Manager1, next: 2, nextApprover: Manager2, decidedBy: Manager1));
            await rig.DispatchOneAsync(advance);
            await rig.DispatchOneAsync(advance);

            Assert.Equal(2, rig.Tasks().Count);
            Assert.Equal(2, rig.Ledger().Count);
            Assert.Equal(TaskStatuses.New, rig.Tasks().Single(t => t.AssigneeEmployeeId == Manager2).Status);
        }

        /// A completed level replayed is a no-op: it does not resurrect the task, does not create a
        /// second one, and does not move it out of Done.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task J03_a_replayed_terminal_decision_has_no_second_effect(bool approved)
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(52);
            await rig.RecordAsync(52, EmployeeRequestEvents.Submitted, Payload(52, level: 1, approver: Manager1));
            await rig.DispatchAllAsync();

            var terminal = Env(approved ? EmployeeRequestEvents.Approved : EmployeeRequestEvents.Rejected,
                52, CompanyA, Payload(52, level: 1, approver: Manager1, decidedBy: Manager1));
            await rig.DispatchOneAsync(terminal);
            await rig.DispatchOneAsync(terminal);
            await rig.DispatchOneAsync(terminal);

            var task = Assert.Single(rig.Tasks());
            Assert.Equal(TaskStatuses.Done, task.Status);
            Assert.Single(rig.Ledger());
        }

        /// The producer's own dedup key is Phase B's business, but the platform already refuses a second
        /// recording under one key — so a resubmitted fact cannot become a second event either.
        [Fact]
        public async Task J04_the_event_log_dedups_a_repeated_fact_under_one_key()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(53);
            var p = Payload(53, level: 1, approver: Manager1);

            long first = await rig.RecordAsync(53, EmployeeRequestEvents.Submitted, p, dedupKey: "employee-request:53:level:1:Submitted");
            long again = await rig.RecordAsync(53, EmployeeRequestEvents.Submitted, p, dedupKey: "employee-request:53:level:1:Submitted");

            Assert.Equal(first, again);
            Assert.Single(rig.Events_());
        }

        // ==========================================================================================
        // K — TENANT AUTHORITY. One answer, from the envelope.
        // ==========================================================================================

        [Fact]
        public void K01_the_task_company_is_the_envelope_context_company()
        {
            var decision = new EmployeeRequestApprovalRule().Decide(
                Env(EmployeeRequestEvents.Submitted, 60, CompanyB, Payload(60, level: 1, approver: Manager1)));

            Assert.Equal(TaskProjectionAction.Create, decision.Action);
            Assert.Equal(CompanyB, decision.Intent!.CompanyId);
            Assert.NotEqual(1, decision.Intent.CompanyId);
            Assert.NotEqual(CompanyA, decision.Intent.CompanyId);
        }

        /// The same fact, the same payload, a different envelope company — and the intent follows the
        /// envelope. A rule that had derived the company from anything else could not do this.
        [Fact]
        public void K02_the_same_payload_under_two_companies_yields_two_companies()
        {
            var rule = new EmployeeRequestApprovalRule();
            var p = Payload(61, level: 1, approver: Manager1);

            Assert.Equal(CompanyA, rule.Decide(Env(EmployeeRequestEvents.Submitted, 61, CompanyA, p)).Intent!.CompanyId);
            Assert.Equal(CompanyB, rule.Decide(Env(EmployeeRequestEvents.Submitted, 61, CompanyB, p)).Intent!.CompanyId);
        }

        /// The ledger lookup behind Complete is company-scoped, so a fact arriving under another company
        /// cannot close this company's work — the closest thing this slice has to a cross-tenant attack.
        [Fact]
        public async Task K03_another_companys_event_cannot_complete_this_companys_task()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(62);
            await rig.RecordAsync(62, EmployeeRequestEvents.Submitted, Payload(62, level: 1, approver: Manager1));
            await rig.DispatchAllAsync();
            Assert.Equal(TaskStatuses.New, Assert.Single(rig.Tasks()).Status);

            await rig.DispatchOneAsync(Env(EmployeeRequestEvents.Approved, 62, CompanyB,
                Payload(62, level: 1, approver: Manager1, decidedBy: Manager1)));

            Assert.Equal(TaskStatuses.New, Assert.Single(rig.Tasks()).Status);   // untouched
        }

        /// And it cannot open work here either: the intent would name company B, which the orchestration
        /// service refuses against a company-A ledger — nothing is created under either company.
        [Fact]
        public async Task K04_another_companys_submission_creates_no_task_here()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(63);

            await rig.DispatchOneAsync(Env(EmployeeRequestEvents.Submitted, 63, CompanyB,
                Payload(63, level: 1, approver: Manager1)));

            Assert.Empty(rig.Tasks());
        }

        // ==========================================================================================
        // L — PRIVACY. The title is the same sentence whatever the request is about.
        // ==========================================================================================

        [Theory]
        [InlineData("Letter")]
        [InlineData("Permission")]
        [InlineData("Salary")]
        public async Task L01_the_task_title_is_identical_whatever_the_request_type(string requestType)
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(70);
            await rig.RecordAsync(70, EmployeeRequestEvents.Submitted,
                Payload(70, level: 1, approver: Manager1, requestType: requestType));

            await rig.DispatchAllAsync();

            var task = Assert.Single(rig.Tasks());
            Assert.Equal("Employee request approval", task.TitleEn);
            Assert.Equal("طلب موظف بانتظار اعتمادك", task.Title);
            Assert.Null(task.Description);
            Assert.DoesNotContain(requestType, task.Title ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(requestType, task.TitleEn ?? "", StringComparison.OrdinalIgnoreCase);
        }

        /// The stronger form of the same claim: two DIFFERENT request types produce byte-identical task
        /// text. A title that merely omitted one value could still leak another.
        [Fact]
        public async Task L02_two_different_request_types_produce_byte_identical_task_text()
        {
            using var a = new Rig(CompanyA);
            using var b = new Rig(CompanyA);
            await a.SeedRequestAsync(71);
            await b.SeedRequestAsync(71);

            await a.RecordAsync(71, EmployeeRequestEvents.Submitted, Payload(71, level: 1, approver: Manager1, requestType: "Letter"));
            await b.RecordAsync(71, EmployeeRequestEvents.Submitted, Payload(71, level: 1, approver: Manager1, requestType: "Salary"));
            await a.DispatchAllAsync();
            await b.DispatchAllAsync();

            var x = Assert.Single(a.Tasks());
            var y = Assert.Single(b.Tasks());
            Assert.Equal(x.Title, y.Title);
            Assert.Equal(x.TitleEn, y.TitleEn);
            Assert.Equal(x.Description, y.Description);
            Assert.Null(x.Description);
        }

        /// Nothing about the person or the request reaches the task, including through the row the
        /// request was seeded with.
        [Fact]
        public async Task L03_the_task_discloses_nothing_from_the_request_row()
        {
            using var rig = new Rig(CompanyA);
            await rig.SeedRequestAsync(72, letterType: "Salary", addressee: "بنك الرياض", reason: "قرض عقاري");
            await rig.RecordAsync(72, EmployeeRequestEvents.Submitted,
                Payload(72, level: 1, approver: Manager1, requestType: "Letter"));
            await rig.DispatchAllAsync();

            var task = Assert.Single(rig.Tasks());
            foreach (var secret in new[] { "Salary", "بنك الرياض", "قرض عقاري", "emp10", "Letter" })
            {
                Assert.DoesNotContain(secret, task.Title ?? "");
                Assert.DoesNotContain(secret, task.TitleEn ?? "");
                Assert.DoesNotContain(secret, task.Description ?? "");
            }
        }

        // ==========================================================================================
        // M — STRUCTURAL RATCHETS. Each one guards a property no behavioural test can see.
        // ==========================================================================================

        /// M — THE CONSTANT, NOT A LITERAL. The behavioural tests cannot tell these apart: a quoted
        /// "EmployeeRequest" produces exactly the same string today. What it would NOT do is follow the
        /// registry if the code were ever renamed — the rule would silently answer nothing for ever.
        [Fact]
        public void M01_the_rule_matches_the_registry_constant_and_never_a_string_literal()
        {
            string source = SourceOf("CrossBuy", "BL", "Platform", "Orchestration", "EmployeeRequestApprovalRule.cs");

            Assert.Contains("EntityRegistry.EmployeeRequest", source, StringComparison.Ordinal);
            Assert.DoesNotContain("\"EmployeeRequest\"", StripComments(source), StringComparison.Ordinal);
            Assert.DoesNotContain("\"EmployeeRequest.", StripComments(source), StringComparison.Ordinal);
        }

        /// M — NO HARDCODED TENANT. The literal company 1 is the value every historical tenancy defect in
        /// this repository defaulted to. Comments are stripped: the prose around this code legitimately
        /// discusses company 1, and an assertion a comment can break is an assertion nobody trusts.
        [Fact]
        public void M02_no_phase_a_file_hardcodes_a_company()
        {
            var files = new[]
            {
                SourceOf("CrossBuy", "BL", "Platform", "Orchestration", "EmployeeRequestApprovalRule.cs"),
                SourceOf("CrossBuy", "Models", "Platform", "EmployeeRequestEventPayload.cs"),
                EmployeeRequestRegionOf(SourceOf("CrossBuy", "BL", "Platform", "EntityRegistry.cs")),
            };

            foreach (var raw in files)
            {
                var code = StripComments(raw);
                Assert.NotEmpty(code.Trim());
                foreach (var bad in new[]
                {
                    "CompanyId = 1", "CompanyID = 1", "CompanyId=1", "CompanyID=1",
                    "CompanyId == 1", "CompanyID == 1", "companyId = 1", "companyId: 1",
                })
                    Assert.DoesNotContain(bad, code, StringComparison.Ordinal);
            }
        }

        /// M — THE RESOLVER READS THE REQUEST'S OWN COLUMN. A11 proves the behaviour; this proves the
        /// SHAPE, because a future arm could reach the same answer through a join that quietly makes
        /// tenancy derived again. Scoped to the EmployeeRequest arm only — the LeaveRequest arm above it
        /// legitimately joins Employee and must keep doing so.
        [Fact]
        public void M03_the_resolver_arm_filters_directly_and_joins_nothing()
        {
            string arm = EmployeeRequestRegionOf(SourceOf("CrossBuy", "BL", "Platform", "EntityRegistry.cs"), armOnly: true);
            var code = StripComments(arm);

            Assert.Contains("r.CompanyID == companyId", code, StringComparison.Ordinal);
            Assert.Contains("_db.EmployeeRequests", code, StringComparison.Ordinal);
            Assert.DoesNotContain("_db.Employee.Any", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Join", code, StringComparison.Ordinal);
            // the label is the id and nothing else
            Assert.Contains("\"#\" + r.ID", code, StringComparison.Ordinal);
            foreach (var field in new[] { "RequestType", "LetterType", "Addressee", "Reason", "DecisionNote" })
                Assert.DoesNotContain(field, code, StringComparison.Ordinal);
        }

        /// M — NO NOTIFICATION DUPLICATION. The mapper answers only the two invoice facts and returns an
        /// empty array for everything else, so adding this event family cannot produce a second
        /// notification beside the direct ones EmployeeRequestService already sends.
        [Fact]
        public void M04_an_employee_request_event_maps_to_no_notification_command()
        {
            foreach (var type in AllEvents())
            {
                var commands = new BusinessEventNotificationMapper().Map(
                    Env(type, 80, CompanyA, Payload(80, level: 1, approver: Manager1)));
                Assert.Empty(commands);
            }
        }

        /// M — THE PAIR IS REGISTERED, BOTH HALVES OF IT. One without the other is the failure that looks
        /// like success: advances would open the next level and never close the last, or close and never
        /// open, and only a multi-level test would notice.
        [Fact]
        public void M05_both_orchestration_rules_are_registered_exactly_once()
        {
            var program = StripComments(SourceOf("CrossBuy", "Program.cs"));

            foreach (var rule in new[] { "EmployeeRequestApprovalRule", "EmployeeRequestApprovalAdvanceCloseRule" })
            {
                var line = $"ITaskOrchestrationRule, CrossBuy.BL.Platform.Orchestration.{rule}>();";
                Assert.Equal(1, Occurrences(program, line));
            }
        }

        /// M — THE PRODUCER IS HERE NOW, AND IT IS FAIL-CLOSED. This test used to assert the exact
        /// opposite: that EmployeeRequestService referenced no recorder, no event vocabulary and no
        /// payload. That was the honest state of Phase A, and its comment said that the day somebody
        /// wired production it must fail and the report has to say so. Phase B is that day, so the
        /// marker is RETIRED rather than deleted - it turns around and ratchets the new truth, which is
        /// the only reading under which the original promise was kept.
        ///
        /// IT LANDS IN THE SAME COMMIT AS THE PRODUCER, and it has to. Against a tree without the
        /// producer this test fails; against a tree with it the OLD marker fails. Either ordering of two
        /// separate commits puts a red test on a lineage many worktrees build from, so the ratchet and
        /// the capability it describes move together - the rule SHF-86 already established for the
        /// vocabulary, applied to the same class of structural marker.
        ///
        /// WHAT IS ACTUALLY GUARANTEED, and why each line is here rather than a general 'it works':
        /// a recorder that were optional, nullable or defaulted would let this service commit an
        /// authoritative transition and publish nothing, with no test able to tell - which is the
        /// RSK-25 shape this platform has already paid for once. One mandatory constructor is what
        /// makes a recorder-less service UNCONSTRUCTIBLE rather than merely refused at runtime.
        [Fact]
        public void M06_production_employee_request_events_have_one_authoritative_fail_closed_producer()
        {
            var service = StripComments(SourceOf("CrossBuy", "BL", "EmployeeRequestService.cs"));

            // the producer exists and speaks the canonical vocabulary
            Assert.Contains("IBusinessEventService", service, StringComparison.Ordinal);
            Assert.Contains("EmployeeRequestEvents", service, StringComparison.Ordinal);
            Assert.Contains("EmployeeRequestEventPayload", service, StringComparison.Ordinal);

            // EXACTLY ONE constructor, so there is no recorder-less way to build the service at all
            Assert.Equal(1, Occurrences(service, "public EmployeeRequestService("));

            // and the recorder is mandatory: no nullable field, no optional parameter, no guarded
            // call that would let a transition commit while publishing nothing
            Assert.DoesNotContain("IBusinessEventService?", service, StringComparison.Ordinal);
            Assert.DoesNotContain("IBusinessEventService events = null", service, StringComparison.Ordinal);
            Assert.DoesNotContain("_events?", service, StringComparison.Ordinal);
            Assert.DoesNotContain("_events != null", service, StringComparison.Ordinal);
            Assert.DoesNotContain("_events is null", service, StringComparison.Ordinal);

            // THE AUTHORITY IS THIS SERVICE ALONE. The orchestration rule consumes these events and
            // must never record one: a rule that produced what it consumes is a loop, and a second
            // producer anywhere is a second answer waiting to disagree with the first.
            var rule = StripComments(SourceOf(
                "CrossBuy", "BL", "Platform", "Orchestration", "EmployeeRequestApprovalRule.cs"));
            Assert.Equal(0, Occurrences(rule, "RecordAsync"));
            Assert.Equal(0, Occurrences(rule, "IBusinessEventService"));
        }

        // ==========================================================================================
        // helpers
        // ==========================================================================================

        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        private static string[] AllEvents() => new[]
        {
            EmployeeRequestEvents.Submitted, EmployeeRequestEvents.Advanced,
            EmployeeRequestEvents.Approved, EmployeeRequestEvents.Rejected,
        };

        private static PropertyInfo[] PayloadProperties() =>
            typeof(EmployeeRequestEventPayload).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        private static EmployeeRequestEventPayload Payload(
            int requestId, int level = 0, int? approver = null, int? next = null,
            int? nextApprover = null, int? decidedBy = null, string? requestType = "Letter") => new()
            {
                EmployeeRequestId = requestId,
                RequesterEmployeeId = Requester,
                RequestType = requestType,
                Level = level,
                ApproverEmployeeId = approver,
                NextLevel = next,
                NextApproverEmployeeId = nextApprover,
                DecidedByEmployeeId = decidedBy,
            };

        private static JsonNode ToCanonicalJson(object payload) =>
            JsonNode.Parse(JsonSerializer.Serialize(payload, Web))!;

        private static BusinessEventEnvelope Env(string eventType, int requestId, int companyId, object? payload)
            => EnvRaw(eventType, requestId, companyId, payload == null ? null : ToCanonicalJson(payload));

        private static BusinessEventEnvelope EnvRaw(string eventType, int requestId, int companyId, JsonNode? payload)
            => new()
            {
                EventUid = Guid.NewGuid(),
                EventType = eventType,
                Entity = new BusinessEventEntity { Code = EntityRegistry.EmployeeRequest, Id = requestId },
                Actor = new BusinessEventActor { EmployeeId = Manager1 },
                Context = new BusinessEventContext { CompanyId = companyId },
                PayloadVersion = EmployeeRequestEventPayload.Version,
                Visibility = BusinessEventVisibility.Internal,
                OccurredAt = DateTime.UtcNow,
                Payload = payload,
            };

        private static EmployeeRequest Row(
            int id, int companyId, int employeeId = Requester, string requestType = "Letter",
            string? letterType = null, string? addressee = null, string? reason = null, int status = 0) => new()
            {
                ID = id,
                CompanyID = companyId,
                EmployeeID = employeeId,
                RequestType = requestType,
                LetterType = letterType,
                Addressee = addressee,
                Reason = reason,
                Status = status,
                CurrentLevel = 1,
                CreatedAt = DateTime.UtcNow,
            };

        private static void Seed(PlatformTestHost host, params EmployeeRequest[] rows)
        {
            host.Seed.EmployeeRequests.AddRange(rows);
            host.Seed.SaveChanges();
        }

        private static Employee Emp(int id, int companyId) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
            Email = $"e{id}-{companyId}@example.invalid", ProfileImage = "-", Gender = "M",
            MaritalStatus = "S", UserId = $"user-{id}-{companyId}",
        };

        // ---- source reading, for the structural ratchets ------------------------------------------

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        /// FAILS LOUDLY WHEN IT CANNOT READ. A structural guard that silently reads an empty string
        /// passes for ever while proving nothing — the one failure mode this kind of test actually has.
        private static string SourceOf(params string[] parts)
        {
            var path = Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), path);
            var text = File.ReadAllText(path);
            Assert.NotEmpty(text.Trim());
            return text;
        }

        /// The EmployeeRequest pieces of the registry, isolated so a ratchet about this entity cannot be
        /// satisfied — or broken — by something another entity does.
        private static string EmployeeRegionOfGuard(string region)
        {
            Assert.NotEmpty(region.Trim());
            return region;
        }

        private static string EmployeeRequestRegionOf(string source, bool armOnly = false)
        {
            if (armOnly)
            {
                int start = source.IndexOf("case EmployeeRequest:", StringComparison.Ordinal);
                Assert.True(start > 0, "the EmployeeRequest resolve arm was not found");
                int end = source.IndexOf("break;", start, StringComparison.Ordinal);
                Assert.True(end > start, "the EmployeeRequest resolve arm has no break");
                return EmployeeRegionOfGuard(source.Substring(start, end - start));
            }

            // every line that mentions the entity, plus the arm, is enough surface for the company ratchet
            var lines = source.Split('\n')
                .Where(l => l.Contains("EmployeeRequest", StringComparison.Ordinal));
            return EmployeeRegionOfGuard(string.Join("\n", lines));
        }

        private static string StripComments(string source) =>
            string.Join("\n", source.Split('\n').Select(l =>
            {
                int i = l.IndexOf("//", StringComparison.Ordinal);
                return i >= 0 ? l.Substring(0, i) : l;
            }));

        private static int Occurrences(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        // ---- the rig: every piece production, and no producer because there is not one yet ---------

        private sealed class Rig : IDisposable
        {
            public PlatformTestHost Host { get; }
            public int CompanyId { get; }
            public IBusinessEventService Events { get; }
            public TaskOrchestrationConsumer Consumer { get; }

            public Rig(int companyId)
            {
                CompanyId = companyId;
                Host = new PlatformTestHost(companyId: companyId);
                Events = Host.Events(context: PlatformTestHost.DefaultContext(companyId, Manager1));

                var tasks = new TaskService(
                    Host.Db, new TaskCalendarEventPublisher(Events),
                    new TaskNotificationService(Host.Db, new UatRecordingNotificationService(Host.Db)));

                Consumer = new TaskOrchestrationConsumer(new TaskOrchestrationService(
                    Host.Db,
                    new ITaskOrchestrationRule[]
                    {
                        new EmployeeRequestApprovalRule(),
                        new EmployeeRequestApprovalAdvanceCloseRule(),
                    },
                    tasks,
                    new TaskWorkPolicyResolver(Host.Db, null)));
            }

            /// The request row and the people in its chain. The orchestration service checks a
            /// source-named assignee against the Employee table before routing work to them, so the
            /// chain has to exist for a task to reach anybody.
            public async Task SeedRequestAsync(
                int id, string? letterType = null, string? addressee = null, string? reason = null)
            {
                foreach (var who in new[] { Requester, Manager1, Manager2, Outsider })
                    Host.Seed.Employee.Add(Emp(who, CompanyId));
                Host.Seed.EmployeeRequests.Add(Row(id, CompanyId, letterType: letterType,
                    addressee: addressee, reason: reason));
                await Host.Seed.SaveChangesAsync();
            }

            /// ADR-001: an event is recorded INSIDE the business transaction, never after it. There is no
            /// production producer yet, so the test supplies the transaction the producer will.
            public async Task<long> RecordAsync(
                int requestId, string eventType, EmployeeRequestEventPayload payload, string? dedupKey = null)
                => await RecordRawAsync(EntityRegistry.EmployeeRequest, requestId, eventType, payload, dedupKey);

            public async Task<long> RecordRawAsync(
                string entityCode, int entityId, string eventType, object? payload, string? dedupKey = null)
            {
                await using var tx = await ScopedTx.BeginOrJoinAsync(Host.Db);
                long id = await Events.RecordAsync(new BusinessEventRecord
                {
                    EntityCode = entityCode,
                    EntityId = entityId,
                    EventType = eventType,
                    Payload = payload,
                    PayloadVersion = EmployeeRequestEventPayload.Version,
                    DedupKey = dedupKey,
                });
                await tx.CommitAsync();
                return id;
            }

            public List<BusinessEvent> Events_() => Host.Seed.BusinessEvents
                .AsNoTracking().Where(e => e.CompanyID == CompanyId)
                .OrderBy(e => e.EventId).ToList();

            /// EXACTLY what BusinessEventDispatchWorker does with a committed row.
            public async Task DispatchAllAsync()
            {
                foreach (var stored in Events_())
                    await Consumer.HandleAsync(Events.BuildEnvelope(stored));
            }

            /// A fact the producer will one day record, delivered straight to the consumer. Used where
            /// the test needs an envelope this company could not legitimately WRITE — another company's
            /// event, or a malformed payload — which is exactly what a hostile or stale delivery is.
            public Task DispatchOneAsync(BusinessEventEnvelope envelope) => Consumer.HandleAsync(envelope);

            public List<TaskItem> Tasks() => Host.Seed.TaskItems
                .AsNoTracking().Where(t => t.CompanyId == CompanyId).OrderBy(t => t.ID).ToList();

            public List<TaskAutoLog> Ledger() => Host.Seed.TaskAutoLogs
                .AsNoTracking().Where(l => l.CompanyId == CompanyId).OrderBy(l => l.ID).ToList();

            public void Dispose() => Host.Dispose();
        }
    }
}
