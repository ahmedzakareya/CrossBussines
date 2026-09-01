using static CrossBuy.Tests.B6TestWiring;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch D1 Wave 1 — CORRECTION-005 (the validated company source) and the two
    // StampInvoiceCustomer endpoints.
    //
    // THE MOST IMPORTANT TEST IN THIS FILE IS THE BOOTSTRAP-OPEN GUARD (§2).
    //
    // `AccountingAccessService` returns true for EVERY action when no AccountingUserRole row exists in the
    // company. So an "authorized" test written against an empty database passes whether or not the endpoint is
    // protected at all — it proves nothing while looking green. Every allow/deny test below therefore seeds a
    // real role row, and §2 exists to prove the trap is real rather than hypothetical, so nobody later "simplifies"
    // the seeding away.
    public class D1Wave1CompanySourceTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;
        private const int ChiefId = 41;        // ChiefAccountant, company 1
        private const int AccountantId = 42;   // Accountant, company 1
        private const int CashierId = 43;      // Cashier, company 1
        private const int OtherCoChief = 45;   // ChiefAccountant, company 2

        private static Employee Emp(int id, int companyId, bool active = true) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, IsActive = active, Address = "-", PhoneNumber = "-",
            Email = $"e{id}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
            UserId = "user-" + id,
        };

        private static BusinessContext Ctx(int employeeId, int companyId) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
            Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
        };

        private static async Task SeedRolesAsync(PlatformTestHost host)
        {
            host.Seed.Employee.AddRange(
                Emp(ChiefId, CompanyOne), Emp(AccountantId, CompanyOne),
                Emp(CashierId, CompanyOne), Emp(OtherCoChief, CompanyTwo));
            host.Seed.AccountingUserRoles.AddRange(
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = ChiefId, Role = "ChiefAccountant" },
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = AccountantId, Role = "Accountant" },
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = CashierId, Role = "Cashier" },
                new AccountingUserRole { CompanyID = CompanyTwo, EmployeeId = OtherCoChief, Role = "ChiefAccountant" });
            await host.Seed.SaveChangesAsync();
        }

        private static AccountingAccessService Accounting(PlatformTestHost host, BusinessContext? ctx = null)
        {
            var http = new Microsoft.AspNetCore.Http.HttpContextAccessor();
            var accessor = ctx == null
                ? (IBusinessContextAccessor)StubContextAccessor.Unresolved()
                : new StubContextAccessor(ctx);
            return new AccountingAccessService(host.Db, http, accessor, Policies(host.Db), Log<AccountingAccessService>());
        }

        private static IRequestCompanyResolver Resolver(BusinessContext? ctx) =>
            new RequestCompanyResolver(
                ctx == null ? StubContextAccessor.Unresolved() : new StubContextAccessor(ctx),
                NullLogger<RequestCompanyResolver>.Instance);

        // ============================================================================================
        // 1. CORRECTION-005 — the resolver itself
        // ============================================================================================

        [Fact]
        public async Task An_unresolved_identity_yields_no_company_and_never_company_one()
        {
            var r = await Resolver(null).ResolveAsync();

            Assert.False(r.Ok);
            Assert.Equal(CompanyResolutionFailure.Unresolved, r.Failure);
            // The whole point of CORRECTION-005: the answer is NOT 1.
            Assert.Equal(0, r.CompanyId);
        }

        [Fact]
        public async Task A_resolved_context_supplies_the_company_the_caller_actually_belongs_to()
        {
            var r = await Resolver(Ctx(OtherCoChief, CompanyTwo)).ResolveAsync();

            Assert.True(r.Ok);
            Assert.Equal(CompanyTwo, r.CompanyId);          // not 1
            Assert.Equal(OtherCoChief, r.EmployeeId);
        }

        [Fact]
        public async Task A_context_carrying_no_company_is_refused_rather_than_defaulted()
        {
            var broken = new BusinessContext
            {
                CompanyId = 0, EmployeeId = 7, UserId = "u", Roles = Array.Empty<string>(),
                CorrelationId = Guid.NewGuid(),
            };

            var r = await Resolver(broken).ResolveAsync();

            Assert.False(r.Ok);
            Assert.Equal(CompanyResolutionFailure.Unresolved, r.Failure);
        }

        [Fact]
        public async Task A_matching_request_supplied_company_is_accepted_for_compatibility()
        {
            var r = await Resolver(Ctx(ChiefId, CompanyOne)).ResolveAsync(requestSuppliedCompanyId: CompanyOne);

            Assert.True(r.Ok);
            Assert.Equal(CompanyOne, r.CompanyId);
        }

        // The rule that separates this from a convenience helper: a mismatch is REFUSED, not corrected. Coercing
        // it to the caller's own company would silently carry out a different operation than the one requested.
        [Fact]
        public async Task A_mismatched_request_supplied_company_is_refused_and_never_coerced()
        {
            var r = await Resolver(Ctx(ChiefId, CompanyOne)).ResolveAsync(requestSuppliedCompanyId: CompanyTwo);

            Assert.False(r.Ok);
            Assert.Equal(CompanyResolutionFailure.CompanyMismatch, r.Failure);
            Assert.NotEqual(CompanyTwo, r.CompanyId);   // not honoured
            Assert.NotEqual(CompanyOne, r.CompanyId);   // and not silently swapped for the caller's own
        }

        [Fact]
        public async Task A_zero_or_negative_request_company_is_ignored_as_absent_not_treated_as_a_mismatch()
        {
            foreach (var supplied in new int?[] { 0, -1, null })
            {
                var r = await Resolver(Ctx(ChiefId, CompanyOne)).ResolveAsync(supplied);
                Assert.True(r.Ok);
                Assert.Equal(CompanyOne, r.CompanyId);
            }
        }

        // ============================================================================================
        // 2. THE BOOTSTRAP-OPEN GUARD — proving an unseeded "authorized" test proves nothing
        // ============================================================================================

        // Required by the Wave 1 brief. This test is EXPECTED to show permission granted: that is the finding.
        // With no AccountingUserRole rows, a Cashier — who must NOT be able to post — is allowed, because the
        // module is bootstrap-open. Any allow-test that skips role seeding is therefore vacuous.
        [Fact]
        // B6 TRANSITION, taken exactly as the previous version asked for. Its message said: "if this ever fails,
        // the guard below is no longer needed and this test should be revisited DELIBERATELY, not deleted." B6
        // made it fail, so it is revisited rather than removed: assertion inverted, test renamed, paired guard
        // below KEPT because it still proves the role path.
        public async Task With_no_role_rows_a_cashier_is_DENIED_posting_because_post_is_never_bootstrap_open()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(CashierId, CompanyOne));
            await host.Seed.SaveChangesAsync();
            // deliberately NO AccountingUserRole rows

            var acc = Accounting(host, Ctx(CashierId, CompanyOne));

            Assert.False(await acc.CanAsync(Ctx(CashierId, CompanyOne), "post"),
                "Accounting.post is Never-Bootstrap-Open: no policy state may open it, so an unconfigured " +
                "company must deny posting to the ledger");
        }

        // The same caller, once ONE role row exists in the company, is denied. This is the pair that makes the
        // suite's allow/deny assertions meaningful.
        [Fact]
        public async Task Once_a_single_role_row_exists_the_cashier_is_denied_posting()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedRolesAsync(host);

            var acc = Accounting(host, Ctx(CashierId, CompanyOne));

            Assert.False(await acc.CanAsync(Ctx(CashierId, CompanyOne), "post"));
        }

        // ============================================================================================
        // 3. THE PERMISSION THE STAMP ENDPOINT NOW REQUIRES — AccPerm("post")
        // ============================================================================================

        [Theory]
        [InlineData(ChiefId, CompanyOne, true)]        // ChiefAccountant may post
        [InlineData(AccountantId, CompanyOne, true)]   // Accountant may post
        [InlineData(CashierId, CompanyOne, false)]     // Cashier may not — this is the case that was open before
        public async Task The_post_right_governs_who_may_stamp_an_invoice_beneficiary(
            int employeeId, int companyId, bool expected)
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedRolesAsync(host);

            var ctx = Ctx(employeeId, companyId);
            Assert.Equal(expected, await Accounting(host, ctx).CanAsync(ctx, "post"));
        }

        // A ChiefAccountant of company 2 holds the strongest accounting right there and none here.
        [Fact]
        public async Task A_chief_accountant_of_another_company_cannot_post_in_this_company()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedRolesAsync(host);

            var asCompanyOne = Ctx(OtherCoChief, CompanyOne);   // claims company 1, holds a company-2 grant
            Assert.False(await Accounting(host, asCompanyOne).CanAsync(asCompanyOne, "post"));

            var asCompanyTwo = Ctx(OtherCoChief, CompanyTwo);   // in their own company they may
            Assert.True(await Accounting(host, asCompanyTwo).CanAsync(asCompanyTwo, "post"));
        }

        [Fact]
        public async Task An_unresolved_identity_is_denied_posting()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedRolesAsync(host);

            // the legacy session-free overload used by AccPerm — no context resolves
            Assert.False(await Accounting(host, ctx: null).CanAsync("post"));
        }

        // ============================================================================================
        // 4. THE STAMP ITSELF — company validation and the statutory policy, end to end on the data
        // ============================================================================================

        private static async Task<SalesInvoice> InvoiceAsync(
            PlatformTestHost host, int companyId, decimal taxTotal = 0m, string? alreadyStamped = null)
        {
            var inv = new SalesInvoice
            {
                CompanyID = companyId, CustomerId = 1, InvoiceDate = DateTime.UtcNow.Date,
                InvoiceNo = "SI-" + Guid.NewGuid().ToString("N")[..8],
                TaxTotal = taxTotal, CustomerNameOverride = alreadyStamped,
            };
            host.Seed.SalesInvoices.Add(inv);
            await host.Seed.SaveChangesAsync();
            return inv;
        }

        // The action loads the invoice by `ID == id && CompanyID == scope.CompanyId`. This proves the predicate
        // an id from another company cannot satisfy — the tampering case.
        [Fact]
        public async Task An_invoice_in_another_company_is_not_reachable_by_id()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var foreign = await InvoiceAsync(host, CompanyTwo);

            var scope = await Resolver(Ctx(ChiefId, CompanyOne)).ResolveAsync();
            Assert.True(scope.Ok);
            Assert.Equal(CompanyOne, scope.CompanyId);

            var found = await host.Db.SalesInvoices
                .FirstOrDefaultAsync(i => i.ID == foreign.ID && i.CompanyID == scope.CompanyId);

            Assert.Null(found);   // answers exactly like a non-existent id — no existence leak

            // and the row is untouched
            var reread = await host.AllCompanies().SalesInvoices.AsNoTracking()
                .FirstAsync(i => i.ID == foreign.ID);
            Assert.Null(reread.CustomerNameOverride);
        }

        // The statutory policy the remediation deliberately did NOT change. Both refusals must survive.
        [Fact]
        public void The_set_once_rule_still_refuses_a_second_stamp()
        {
            var inv = new SalesInvoice { CompanyID = CompanyOne, CustomerNameOverride = "أول مستفيد" };

            var (ok, err) = OfficialInvoiceHelper.StampCustomer(inv, "مستفيد آخر", null, "9");

            Assert.False(ok);
            Assert.NotNull(err);
            Assert.Equal("أول مستفيد", inv.CustomerNameOverride);   // unchanged
        }

        [Fact]
        public void A_taxed_invoice_still_refuses_a_beneficiary_override()
        {
            var inv = new SalesInvoice { CompanyID = CompanyOne, TaxTotal = 15m };

            var (ok, err) = OfficialInvoiceHelper.StampCustomer(inv, "اسم", "123", "9");

            Assert.False(ok);
            Assert.NotNull(err);
            Assert.Null(inv.CustomerNameOverride);
        }

        // The authorized path still works, and records the RESOLVED actor rather than a session-parsed one.
        [Fact]
        public async Task An_authorized_stamp_succeeds_and_records_the_resolved_actor()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedRolesAsync(host);
            var inv = await InvoiceAsync(host, CompanyOne);

            var scope = await Resolver(Ctx(ChiefId, CompanyOne)).ResolveAsync();
            var tracked = await host.Db.SalesInvoices.FirstAsync(i => i.ID == inv.ID && i.CompanyID == scope.CompanyId);

            var (ok, err) = OfficialInvoiceHelper.StampCustomer(tracked, "محمد", "  ", scope.EmployeeId?.ToString());
            Assert.True(ok, err);
            await host.Db.SaveChangesAsync();

            var reread = await host.AllCompanies().SalesInvoices.AsNoTracking().FirstAsync(i => i.ID == inv.ID);
            Assert.Equal("محمد", reread.CustomerNameOverride);
            Assert.Equal(ChiefId.ToString(), reread.CustomerOverrideBy);
            Assert.Null(reread.CustomerTaxNoOverride);      // whitespace-only tax number stays null
            Assert.NotNull(reread.CustomerOverrideAt);
        }

        // A refusal must leave nothing behind — no partial write, and no financial field touched either way.
        [Fact]
        public async Task A_refused_stamp_leaves_the_invoice_and_its_financials_untouched()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var inv = await InvoiceAsync(host, CompanyOne, taxTotal: 15m);
            var taxBefore = inv.TaxTotal;

            var tracked = await host.Db.SalesInvoices.FirstAsync(i => i.ID == inv.ID);
            var (ok, _) = OfficialInvoiceHelper.StampCustomer(tracked, "اسم", "1", ChiefId.ToString());
            Assert.False(ok);
            await host.Db.SaveChangesAsync();   // even if the caller saves anyway, nothing changed

            var reread = await host.AllCompanies().SalesInvoices.AsNoTracking().FirstAsync(i => i.ID == inv.ID);
            Assert.Null(reread.CustomerNameOverride);
            Assert.Null(reread.CustomerTaxNoOverride);
            Assert.Null(reread.CustomerOverrideBy);
            Assert.Null(reread.CustomerOverrideAt);
            Assert.Equal(taxBefore, reread.TaxTotal);   // no calculation touched
        }
    }
}
