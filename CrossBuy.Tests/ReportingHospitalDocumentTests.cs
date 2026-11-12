using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // THE HOSPITAL'S PRINTED PAPERS — the pack that was registered and never tested.
    //
    // HospitalDocumentDatasets/Sources declare four document datasets and one module authority, and
    // AddCrossBusinessReporting registers all of them. Nothing covered any of it, so the authority
    // that decides who may print a patient's bill had no proof at all.
    //
    // NO DATABASE OF ANY KIND. The dataset definitions are pure, and the authority's only dependency
    // is IModuleAccessService, which is stubbed here. Under the standing policy nothing may open a
    // database, and this pack does not need one to be proven.
    //
    // WHAT IS DELIBERATELY NOT CLAIMED: these tests say nothing about whether a bill's ROWS are
    // correctly fetched — that needs the DbContext the policy forbids. They cover the declaration and
    // the authority, which is where a permission defect would live.
    // ============================================================================================
    public class ReportingHospitalDocumentTests
    {
        /// A module access service that is whatever the test needs it to be.
        private sealed class StubModule : IModuleAccessService
        {
            private readonly bool _answer;

            public StubModule(string scope, bool answer = true, params string[] actions)
            {
                Scope = scope;
                _answer = answer;
                Actions = actions.Length > 0 ? actions : new[] { HospitalActions.Read };
            }

            public string Scope { get; }
            public IReadOnlyCollection<string> Actions { get; }
            public int Asked { get; private set; }

            public Task<bool> CanAsync(BusinessContext context, string action,
                PermissionTarget? target = null, CancellationToken cancellationToken = default)
            {
                Asked++;
                return Task.FromResult(_answer);
            }

            // Not part of the authority path. Returning empty rather than throwing keeps a mis-wire
            // visible as a refusal in the assertions above instead of as an exception here.
            public Task<IReadOnlyList<string>> RolesAsync(BusinessContext context,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        private static HospitalReportModuleAuthority Authority(params IModuleAccessService[] modules) =>
            new(modules);

        private static BusinessContext Ctx() => new() { CompanyId = 3, EmployeeId = 9 };

        private static readonly string[] AllCodes =
        {
            HospitalDocumentDatasetCodes.Bill,
            HospitalDocumentDatasetCodes.Prescription,
            HospitalDocumentDatasetCodes.PrescriptionForm,
            HospitalDocumentDatasetCodes.DischargeSummary,   // H55
            HospitalDocumentDatasetCodes.SickLeave,          // H56
            HospitalDocumentDatasetCodes.BirthNotice,        // H58
            HospitalDocumentDatasetCodes.DeathCertificate,   // H59
            HospitalDocumentDatasetCodes.Consent,            // H60
            HospitalDocumentDatasetCodes.PatientStatement,   // H63
            HospitalDocumentDatasetCodes.DentalClaim,
            HospitalDocumentDatasetCodes.GlassesRx,          // H109
            HospitalDocumentDatasetCodes.EyeLetter,          // H109
            HospitalDocumentDatasetCodes.AllergyPlan,        // H109
            HospitalDocumentDatasetCodes.CanvasDrawing,      // H111
        };

        // ---- the datasets are declared, and declared legally ------------------------------------

        // ReportDatasetRegistry validates every definition IN ITS CONSTRUCTOR, so building one over
        // the hospital pack is the whole declaration check: duplicate codes, empty keys, malformed
        // fields and bad operator/aggregate combinations all throw here rather than on a user's click.
        [Fact]
        public void Every_document_dataset_constructs_and_validates()
        {
            var registry = new ReportDatasetRegistry(
                HospitalDocumentDatasets.All().Cast<IReportDatasetDefinition>(),
                new AlwaysAllow());

            foreach (var code in AllCodes)
                Assert.True(registry.TryResolve(code, out _), $"{code} did not resolve.");

            Assert.Equal(AllCodes.Length, registry.RegisteredCodes.Count);
        }

        private sealed class AlwaysAllow : IReportPermissionEvaluator
        {
            public Task<bool> HasPermissionAsync(string permissionKey, BusinessContext context,
                CancellationToken cancellationToken = default) => Task.FromResult(true);
        }

        // ONE KEY FOR ALL FOUR, and it must be the hospital's own. A document that named a weaker key
        // — or the reporting role map's — would let someone print a patient's bill without the
        // hospital module ever being asked.
        [Fact]
        public void Every_hospital_document_requires_the_hospital_key()
        {
            foreach (var dataset in HospitalDocumentDatasets.All())
                Assert.Equal(HospitalReportPermissions.View, dataset.RequiredPermissionKey);
        }

        // The key must sit inside the namespace the authority claims, or the adapter would route it
        // to the role map and the hospital module would never be consulted.
        [Fact]
        public void The_hospital_key_sits_inside_the_claimed_namespace()
        {
            Assert.StartsWith(HospitalReportPermissions.Prefix, HospitalReportPermissions.View,
                StringComparison.Ordinal);
            Assert.Equal(HospitalReportPermissions.Prefix, Authority(new StubModule("Hospital")).KeyPrefix);
        }

        // ---- the authority: who may print a patient's paper -------------------------------------

        // THE POSITIVE CONTROL. Without it every refusal below is satisfied by an authority that is
        // simply broken shut, and a pack that prints nothing would look perfectly secure.
        [Fact]
        public async Task A_hospital_user_the_module_allows_may_print()
        {
            var module = new StubModule(EntityRegistry.ScopeHospital, answer: true);

            var allowed = await Authority(module).EvaluateAsync(
                HospitalReportPermissions.View, Ctx());

            Assert.True(allowed);
            Assert.Equal(1, module.Asked);
        }

        // The module's NO is final. Reporting adds nothing of its own on top.
        [Fact]
        public async Task A_user_the_module_refuses_may_not_print()
        {
            var module = new StubModule(EntityRegistry.ScopeHospital, answer: false);

            Assert.False(await Authority(module).EvaluateAsync(HospitalReportPermissions.View, Ctx()));
        }

        // FAIL CLOSED WITH NO HOSPITAL MODULE AT ALL. A deployment without the hospital registered
        // must refuse rather than fall through to the reporting role map, which would decide a
        // clinical-records question from a list of identity role names.
        [Fact]
        public async Task With_no_hospital_module_registered_it_refuses()
        {
            var authority = Authority();   // nothing registered

            Assert.False(await authority.EvaluateAsync(HospitalReportPermissions.View, Ctx()));
        }

        // IT FINDS THE HOSPITAL BY SCOPE, NOT BY REGISTRATION ORDER. IModuleAccessService has nine
        // implementations and this one is registered by interface, so order would otherwise decide
        // which module answered. Accounting is listed first and last here on purpose.
        [Fact]
        public async Task It_selects_the_hospital_module_among_many_whatever_the_order()
        {
            var accountingFirst = new StubModule("Accounting", answer: false);
            var hospital = new StubModule(EntityRegistry.ScopeHospital, answer: true);
            var accountingLast = new StubModule("Accounting", answer: false);

            var allowed = await Authority(accountingFirst, hospital, accountingLast)
                .EvaluateAsync(HospitalReportPermissions.View, Ctx());

            Assert.True(allowed);
            Assert.Equal(1, hospital.Asked);
            Assert.Equal(0, accountingFirst.Asked);
            Assert.Equal(0, accountingLast.Asked);
        }

        // A DEPLOYMENT WITH EVERY OTHER MODULE BUT NO HOSPITAL REFUSES — it does not settle for the
        // first module it can find. This is the cross-module grant the scope check exists to prevent.
        [Fact]
        public async Task It_refuses_rather_than_asking_some_other_module()
        {
            var others = new IModuleAccessService[]
            {
                new StubModule("Accounting", answer: true),
                new StubModule("Inventory", answer: true),
                new StubModule("Crm", answer: true),
            };

            Assert.False(await Authority(others).EvaluateAsync(HospitalReportPermissions.View, Ctx()));
        }

        // AN UNRECOGNISED KEY INSIDE THE CLAIMED NAMESPACE IS null, WHICH THE ADAPTER READS AS A
        // REFUSAL — never as "not mine, let the role map decide". A future hospital.* key that nobody
        // has implemented must be denied, not quietly delegated to a list of role names.
        [Theory]
        [InlineData("hospital.reports.confidential")]
        [InlineData("hospital.reports.")]
        [InlineData("hospital.anything")]
        public async Task An_unrecognised_hospital_key_is_refused_not_delegated(string key)
        {
            var module = new StubModule(EntityRegistry.ScopeHospital, answer: true);

            var answer = await Authority(module).EvaluateAsync(key, Ctx());

            Assert.Null(answer);
            Assert.Equal(0, module.Asked);
        }

        // The module must PUBLISH the action it is asked about. A hospital service whose vocabulary
        // lacks Read is a mis-wire, and the shared Decide refuses on it rather than asking anyway.
        [Fact]
        public async Task A_module_that_does_not_publish_the_action_is_refused()
        {
            var module = new StubModule(EntityRegistry.ScopeHospital, answer: true, "something-else");

            Assert.False(await Authority(module).EvaluateAsync(HospitalReportPermissions.View, Ctx()));
            Assert.Equal(0, module.Asked);
        }
    }
}
