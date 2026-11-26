using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.Controllers;
using CrossBuy.Controllers.Api;
using CrossBuy.Models;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // WAVE 0 — GLOBAL-REMEDIATION-01. Regression cover for the security findings fixed in this wave.
    //
    // THESE ASSERT COMPILED METADATA, NOT SOURCE TEXT. A test that greps for an attribute passes the
    // moment someone writes the string in a comment and keeps passing after the attribute is deleted
    // from the method it was meant to guard. Reflection over the built assembly cannot do either: it
    // sees what the runtime will see, which is the only thing that decides whether a request is
    // refused.
    //
    // WHAT THEY DELIBERATELY DO NOT CLAIM. None of these proves tenant isolation, and none proves the
    // SEC-01 endpoint is safe — AdminController is staged by another TAB this wave and its missing
    // permission and missing antiforgery token are untouched. The service-layer half is covered here;
    // the controller half is recorded as BLOCKED.
    // =============================================================================================
    public class Wave0SecurityRemediationTests
    {
        // -----------------------------------------------------------------------------------------
        // SEC-03 — the three AI analysis endpoints.
        //
        // The arguments matter as much as the attribute's presence, and that is the whole point of
        // this finding: AccountingAccessService answers `read` UNCONDITIONALLY, so a guard carrying
        // "read" would be an attribute that refuses nobody. CustomAttributeData is read rather than
        // GetCustomAttribute because ApiPermAttribute keeps its module and action private — the
        // constructor arguments are the only place the decision is visible.
        // -----------------------------------------------------------------------------------------
        [Theory]
        [InlineData(nameof(AiController.AnomalyJournalScan), ApiPermAttribute.Accounting, AccountingActions.ReportRead)]
        [InlineData(nameof(AiController.ForecastCashflow), ApiPermAttribute.Accounting, AccountingActions.ReportRead)]
        [InlineData(nameof(AiController.InventoryAnalyze), ApiPermAttribute.Inventory, "manage")]
        public void Ai_analysis_endpoints_require_a_real_module_authority(string action, string module, string permission)
        {
            var method = typeof(AiController).GetMethod(action);
            Assert.NotNull(method);

            var attr = CustomAttributeData.GetCustomAttributes(method!)
                .SingleOrDefault(a => a.AttributeType == typeof(ApiPermAttribute));

            Assert.NotNull(attr);
            Assert.Equal(module, (string)attr!.ConstructorArguments[0].Value!);
            Assert.Equal(permission, (string)attr.ConstructorArguments[1].Value!);
        }

        [Fact]
        public void The_accounting_guard_on_those_endpoints_is_not_the_unconditional_read_action()
        {
            // The defect this wave closed was not "no attribute" — it was that the obvious attribute
            // would have changed nothing. `read` is granted to every authenticated employee of a
            // configured company; `report-read` answers to chief, accountant or auditor. If a later
            // change ever relaxes these to "read", that is a silent re-opening and this fails.
            foreach (var action in new[] { nameof(AiController.AnomalyJournalScan), nameof(AiController.ForecastCashflow) })
            {
                var attr = CustomAttributeData.GetCustomAttributes(typeof(AiController).GetMethod(action)!)
                    .Single(a => a.AttributeType == typeof(ApiPermAttribute));

                Assert.NotEqual("read", (string)attr.ConstructorArguments[1].Value!);
            }
        }

        // -----------------------------------------------------------------------------------------
        // SEC-02 — the four state-changing endpoints that carried no antiforgery token.
        //
        // Each is called by a fetch() that now appends __RequestVerificationToken; the attribute and
        // the caller were changed together, because either one alone is broken: the attribute without
        // the token breaks the feature, the token without the attribute protects nothing.
        // -----------------------------------------------------------------------------------------
        public static TheoryData<Type, string> CsrfFixedEndpoints => new()
        {
            { typeof(AnnouncementsController), nameof(AnnouncementsController.Create) },
            { typeof(AnnouncementsController), nameof(AnnouncementsController.Dismiss) },
            { typeof(AnnouncementsController), nameof(AnnouncementsController.Toggle) },
            { typeof(NotificationsController), nameof(NotificationsController.ToggleMute) },
        };

        [Theory]
        [MemberData(nameof(CsrfFixedEndpoints))]
        public void State_changing_endpoints_validate_their_antiforgery_token(Type controller, string action)
        {
            var method = controller.GetMethod(action);
            Assert.NotNull(method);
            Assert.NotNull(method!.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        }

        // -----------------------------------------------------------------------------------------
        // SEC-01 part C — the actor identity, and the refusal when it cannot be resolved.
        //
        // BEHAVIOURAL, NOT STRUCTURAL. The old code read the actor from Session and then dereferenced
        // it without a null check, so a request with no session key threw NullReferenceException
        // AFTER entering the method. Asserting "it no longer mentions Session" would pass on a
        // rewrite that still crashed. This calls the method with a provider that offers no
        // IBusinessContextAccessor and asserts the refusal, which is the behaviour that matters.
        //
        // The fake provider returns null for every service, which is exactly the unresolved case.
        // -----------------------------------------------------------------------------------------
        private sealed class EmptyProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }

        [Fact]
        public async Task Saving_an_org_structure_node_refuses_when_no_business_context_resolves()
        {
            var service = new AdministrativeStructureService(
                context: null!, mapper: null!, serviceProvider: new EmptyProvider(), httpContextAccessor: null!);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => service.Save(new HierarchicalDto { H_Name = "x", H_NameEn = "x" }));
        }

        [Fact]
        public async Task The_refusal_message_names_no_company_role_or_record()
        {
            // AdminController.AddHierarchicalItem echoes ex.Message straight back to the caller, so a
            // message naming a company, a role or whether a parent exists would hand an unauthorized
            // caller exactly what the refusal is meant to withhold.
            var service = new AdministrativeStructureService(null!, null!, new EmptyProvider(), null!);

            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => service.Save(new HierarchicalDto { H_Name = "x", H_NameEn = "x" }));

            foreach (var leak in new[] { "company", "role", "employee", "parent", "session" })
            {
                Assert.DoesNotContain(leak, ex.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
