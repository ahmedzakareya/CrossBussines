using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Portal;
using CrossBuy.Models.Context.Portal;
using Xunit;

namespace CrossBuy.Tests.Portal
{
    // =============================================================================================
    // GATE A — THE CONTACT-PHOTOGRAPH BOUNDARY.
    //
    // WHAT THESE EXIST TO PROVE. The convergence replaced a static /uploads/portal URL guarded by
    // PrivateFileGate with an authorizing service, because the old arrangement failed in two
    // opposite directions at once: the signed-in CUSTOMER got 404 on their own picture (the gate
    // reads the DEFAULT authentication scheme and the portal has its own), while ANY authenticated
    // employee of ANY company could fetch ANY contact's photograph (the gate enforces no company and
    // no customer). Both directions are asserted below.
    //
    // EVERY REFUSAL HAS AN ALLOW BESIDE IT. A service that returned null to everybody would satisfy
    // every denial in this file, so the two control tests come first and the denials are only
    // meaningful because the controls pass. This programme has shipped an authorization test that
    // passed because no principal existed at all; the controls are what stop that recurring.
    //
    // THE STORE IS REAL. Files are written to a real temp content root and read back, so "the bytes
    // came out" means the locator round-tripped rather than that a stub agreed to return a stream.
    // =============================================================================================
    public class PortalContactPhotoTests
    {
        private const int CompanyA = 1, CompanyB = 2;
        private const int CustomerAcme = 100, CustomerBeta = 200;

        private sealed class Host : IDisposable
        {
            public readonly PlatformTestHost Platform = new(companyId: null);
            public readonly StubPortalContext Contexts = new();
            public readonly TempContentRoot Env = new();

            public CrossBuy.Models.Context.CrossDbContext Db => Platform.Seed;

            /// A portal contact row. The locator is planted separately so a test can give a row a
            /// photograph, no photograph, or a locator that names a file nobody wrote.
            public int SeedContact(int companyId, int customerId, string? photo, bool active = true)
            {
                var row = new PortalUser
                {
                    CompanyID = companyId,
                    CustomerId = customerId,
                    UserId = "u-" + Guid.NewGuid().ToString("n"),
                    ContactName = "Contact",
                    IsActive = active,
                    Capabilities = string.Join(",", PortalCapabilities.Default),
                    PhotoPath = photo,
                    CreatedAt = DateTime.UtcNow,
                };
                Db.Set<PortalUser>().Add(row);
                Db.SaveChanges();
                return row.Id;
            }

            public void ActAs(int portalUserId, int companyId = CompanyA, int customerId = CustomerAcme)
                => Contexts.Current = new PortalContext(
                    companyId, customerId, portalUserId, PortalCapabilities.Default);

            public void ActAsNobody() => Contexts.Current = null;

            public PortalPhotoService Service(bool crmCan = true)
                => PortalPhotoTest.Service(Db, Contexts, Env, crmCan);

            public void Dispose() { Env.Dispose(); Platform.Dispose(); }
        }

        private static async Task<string?> ReadAll(PortalPhotoContent? content)
        {
            if (content == null) return null;
            using var reader = new StreamReader(content.Stream);
            return await reader.ReadToEndAsync();
        }

        // ---- CONTROLS. Everything below is only meaningful because these two pass. --------------

        // 21. A PORTAL USER CAN READ THEIR OWN AUTHORIZED PHOTOGRAPH.
        [Fact]
        public async Task Portal_user_reads_their_own_photo()
        {
            using var h = new Host();
            var locator = h.Env.PlantPhoto(CompanyA, ".jpg", "my-face");
            var id = h.SeedContact(CompanyA, CustomerAcme, locator);
            h.ActAs(id);

            var content = await h.Service().OpenForPortalAsync(id);

            Assert.NotNull(content);
            Assert.Equal("image/jpeg", content!.ContentType);
            Assert.Equal("my-face", await ReadAll(content));
        }

        // CONTROL for the staff side: with the CRM read right and the right company, the bytes come back.
        [Fact]
        public async Task Staff_with_crm_read_in_the_same_company_reads_the_photo()
        {
            using var h = new Host();
            var locator = h.Env.PlantPhoto(CompanyA, ".png", "staff-view");
            var id = h.SeedContact(CompanyA, CustomerAcme, locator);

            var content = await h.Service(crmCan: true).OpenForStaffAsync(id, CompanyA);

            Assert.NotNull(content);
            Assert.Equal("image/png", content!.ContentType);
            Assert.Equal("staff-view", await ReadAll(content));
        }

        // ---- REFUSALS ---------------------------------------------------------------------------

        // 22. ANOTHER PORTAL CONTACT'S PHOTOGRAPH IS REFUSED — even one of the SAME customer.
        //
        // Two people sharing a customer is a commercial fact, not consent to each other's
        // photograph. This is the case a "belongs to my customer" rule would have let through, which
        // is why the service compares against the resolved identity instead.
        [Fact]
        public async Task Portal_user_cannot_read_another_contacts_photo()
        {
            using var h = new Host();
            var mine = h.SeedContact(CompanyA, CustomerAcme, h.Env.PlantPhoto(CompanyA));
            var colleague = h.SeedContact(CompanyA, CustomerAcme, h.Env.PlantPhoto(CompanyA));
            h.ActAs(mine);

            Assert.Null(await h.Service().OpenForPortalAsync(colleague));

            // And the control, in the same test, so a service that refused everything cannot pass it.
            Assert.NotNull(await h.Service().OpenForPortalAsync(mine));
        }

        // A contact of ANOTHER customer is refused too, which is the tenancy case rather than the
        // privacy one.
        [Fact]
        public async Task Portal_user_cannot_read_another_customers_contact_photo()
        {
            using var h = new Host();
            var mine = h.SeedContact(CompanyA, CustomerAcme, h.Env.PlantPhoto(CompanyA));
            var stranger = h.SeedContact(CompanyA, CustomerBeta, h.Env.PlantPhoto(CompanyA));
            h.ActAs(mine);

            Assert.Null(await h.Service().OpenForPortalAsync(stranger));
        }

        // An unresolved portal identity gets nothing. Fail closed.
        [Fact]
        public async Task No_portal_identity_reads_nothing()
        {
            using var h = new Host();
            var id = h.SeedContact(CompanyA, CustomerAcme, h.Env.PlantPhoto(CompanyA));
            h.ActAsNobody();

            Assert.Null(await h.Service().OpenForPortalAsync(id));
        }

        // A deactivated link stops serving the photograph on the very next request.
        [Fact]
        public async Task A_revoked_contact_no_longer_serves_their_photo()
        {
            using var h = new Host();
            var id = h.SeedContact(CompanyA, CustomerAcme, h.Env.PlantPhoto(CompanyA), active: false);
            h.ActAs(id);

            Assert.Null(await h.Service().OpenForPortalAsync(id));
        }

        // 23. A CROSS-COMPANY EMPLOYEE IS REFUSED, holding the CRM read right the whole time.
        //
        // This is the case the old static URL could not express at all: PrivateFileGate asked only
        // "is this request authenticated", and the answer for this caller is yes.
        [Fact]
        public async Task Cross_company_employee_is_refused_even_with_crm_read()
        {
            using var h = new Host();
            var id = h.SeedContact(CompanyA, CustomerAcme, h.Env.PlantPhoto(CompanyA));

            // Same right, same row, different company — and that is the only difference.
            Assert.Null(await h.Service(crmCan: true).OpenForStaffAsync(id, CompanyB));
            Assert.NotNull(await h.Service(crmCan: true).OpenForStaffAsync(id, CompanyA));
        }

        // Without the CRM read right, the same employee in the same company gets nothing.
        [Fact]
        public async Task Staff_without_crm_read_is_refused()
        {
            using var h = new Host();
            var id = h.SeedContact(CompanyA, CustomerAcme, h.Env.PlantPhoto(CompanyA));

            Assert.Null(await h.Service(crmCan: false).OpenForStaffAsync(id, CompanyA));
            Assert.NotNull(await h.Service(crmCan: true).OpenForStaffAsync(id, CompanyA));
        }

        // 24. THE RAW STORAGE PATH CANNOT BE USED TO BYPASS AUTHORIZATION.
        //
        // Two independent facts, both asserted rather than described:
        //   * the store is NOT under wwwroot, so no static route can reach it at all;
        //   * PrivateFileGate still classifies /uploads/portal as private, so anything that ever
        //     writes a portal photograph back under the web root is signed-in-only rather than
        //     anonymous. That entry is a backstop, and this test is what stops it being deleted as
        //     "unused" by someone who has not read why it is there.
        [Fact]
        public void The_photo_store_is_outside_the_web_root_and_the_legacy_prefix_stays_private()
        {
            using var h = new Host();
            var locator = h.Env.PlantPhoto(CompanyA);
            var onDisk = Path.Combine(h.Env.PhotoFolder(CompanyA), locator.Split('/')[1]);

            Assert.True(File.Exists(onDisk));

            // "wwwroot" appears nowhere in the resolved path of a stored photograph.
            Assert.DoesNotContain("wwwroot", onDisk, StringComparison.OrdinalIgnoreCase);

            // And the legacy web prefix is still refused to anonymous callers.
            Assert.Equal(PrivateFilePolicy.RequireAuthenticated, PrivateFileGate.PolicyFor("/uploads/portal/1/x.jpg"));
            Assert.Contains("/uploads/portal", PrivateFileGate.PrivatePrefixes);
        }

        // 25. TRAVERSAL AND OTHER MALFORMED LOCATORS ARE REFUSED.
        //
        // The locator is DATA, and data that decides which file is opened is checked before it is
        // trusted. Each of these is a different way the column could be wrong, and a real file is
        // planted first so a refusal cannot be mistaken for "there was nothing there anyway".
        [Theory]
        [InlineData("../../appsettings.json")]          // traversal
        [InlineData("1/../../appsettings.json")]        // traversal inside a well-formed prefix
        [InlineData("1\\evil.jpg")]                     // backslash separator
        [InlineData("C:/Windows/win.ini")]              // rooted
        [InlineData("/etc/passwd")]                     // rooted, posix shape
        [InlineData("1/2/deep.jpg")]                    // too many segments
        [InlineData("1/notaguid.jpg")]                  // a name the product never generated
        [InlineData("1/0123456789abcdef0123456789abcdef.exe")]   // generated shape, forbidden extension
        [InlineData("1/0123456789ABCDEF0123456789ABCDEF.jpg")]   // upper-case hex is not what we write
        public async Task A_malformed_locator_is_refused(string locator)
        {
            using var h = new Host();
            h.Env.PlantPhoto(CompanyA);   // a real file exists, so this is not a vacuous "nothing there"
            var id = h.SeedContact(CompanyA, CustomerAcme, locator);
            h.ActAs(id);

            Assert.Null(await h.Service().OpenForPortalAsync(id));
        }

        // A locator naming ANOTHER company is refused even though the file would have opened.
        [Fact]
        public async Task A_locator_naming_another_company_is_refused()
        {
            using var h = new Host();
            var foreign = h.Env.PlantPhoto(CompanyB, ".jpg", "other-tenant");
            Assert.True(File.Exists(Path.Combine(h.Env.PhotoFolder(CompanyB), foreign.Split('/')[1])));

            // The row is company A's; the locator says company B.
            var id = h.SeedContact(CompanyA, CustomerAcme, foreign);
            h.ActAs(id);

            Assert.Null(await h.Service().OpenForPortalAsync(id));
        }

        // 26. AN INVALID CONTENT TYPE IS REFUSED AT THE WRITE PATH, and the extension is the
        // server's decision rather than the browser's.
        [Theory]
        [InlineData("text/html")]
        [InlineData("image/svg+xml")]
        [InlineData("application/pdf")]
        [InlineData("")]
        [InlineData(null)]
        public void An_unaccepted_content_type_yields_no_extension(string? contentType)
        {
            using var h = new Host();
            Assert.Null(h.Service().ExtensionFor(contentType));
        }

        [Theory]
        [InlineData("image/jpeg", ".jpg")]
        [InlineData("image/pjpeg", ".jpg")]
        [InlineData("image/png", ".png")]
        [InlineData("image/webp", ".webp")]
        public void The_accepted_types_map_to_server_chosen_extensions(string contentType, string expected)
        {
            using var h = new Host();
            Assert.Equal(expected, h.Service().ExtensionFor(contentType));
        }

        // A row with no photograph is not an error and not a leak — it is simply nothing.
        [Fact]
        public async Task A_contact_with_no_photo_returns_nothing()
        {
            using var h = new Host();
            var id = h.SeedContact(CompanyA, CustomerAcme, photo: null);
            h.ActAs(id);

            Assert.Null(await h.Service().OpenForPortalAsync(id));
        }

        // DELETE REFUSES A LOCATOR THAT IS NOT THIS COMPANY'S. A stored value that chooses which file
        // gets deleted deserves at least as much suspicion as one that chooses which gets read.
        [Fact]
        public void Delete_refuses_another_companys_locator_and_leaves_the_file_alone()
        {
            using var h = new Host();
            var foreign = h.Env.PlantPhoto(CompanyB);
            var onDisk = Path.Combine(h.Env.PhotoFolder(CompanyB), foreign.Split('/')[1]);

            h.Service().Delete(CompanyA, foreign);
            Assert.True(File.Exists(onDisk));

            // The control: asked as its own company, the delete really does happen — so the refusal
            // above is a refusal and not a method that never deletes anything.
            h.Service().Delete(CompanyB, foreign);
            Assert.False(File.Exists(onDisk));
        }

        [Theory]
        [InlineData("../../appsettings.json")]
        [InlineData("1\\evil.jpg")]
        [InlineData("C:/Windows/win.ini")]
        public void Delete_refuses_a_malformed_locator(string locator)
        {
            using var h = new Host();
            var real = h.Env.PlantPhoto(CompanyA);
            var onDisk = Path.Combine(h.Env.PhotoFolder(CompanyA), real.Split('/')[1]);

            h.Service().Delete(CompanyA, locator);

            Assert.True(File.Exists(onDisk));
        }
    }
}
