using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CrossBuy.Tests.Portal
{
    // =============================================================================================
    // THE CONVERGED PORTAL SHELL — what the retained Metronic skin must and must not say.
    //
    // THESE ARE SOURCE-SCANNING TESTS AND THAT IS A DELIBERATE, LIMITED CHOICE. They cannot prove a
    // page renders; they prove that specific defects the convergence map MEASURED are not present in
    // the markup. Each one names the defect it closes, because a grep-shaped test fails OPEN and is
    // worth only what its reason is worth.
    //
    // COMMENTS ARE STRIPPED BEFORE ASSERTING. Every file here documents what it deliberately does
    // NOT do, so a comment naming a forbidden thing in order to record its absence would fail an
    // assertion about the CODE. Razor comments and C# line comments both go.
    // =============================================================================================
    public class PortalShellConvergenceTests
    {
        private static string Repo()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "CrossBuy", "Views", "ClientPortal")))
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return dir!;
        }

        private static string PortalViews() => Path.Combine(Repo(), "CrossBuy", "Views", "ClientPortal");
        private static string AccountViews() => Path.Combine(Repo(), "CrossBuy", "Views", "PortalAccount");

        /// Removes Razor comment blocks and C# line comments. Neither reaches a running program, so a
        /// comment that NAMES a forbidden thing in order to record its absence is documentation.
        private static string StripComments(string text)
        {
            text = Regex.Replace(text, "@" + "\\*.*?\\*" + "@", string.Empty, RegexOptions.Singleline);
            var kept = text.Split('\n').Select(line =>
            {
                int i = line.IndexOf("//", StringComparison.Ordinal);
                return i >= 0 ? line.Substring(0, i) : line;
            });
            return string.Join("\n", kept);
        }

        private static string Code(string file) => StripComments(File.ReadAllText(file));

        private static string[] PortalPages() =>
            Directory.GetFiles(PortalViews(), "*.cshtml")
                     .Where(f => !Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal))
                     .ToArray();

        // ---- 27. LOGOUT TARGETS THE PORTAL, NOT THE EMPLOYEE IDENTITY (closes F3) ---------------
        //
        // The measured defect: _PortalHeader linked to Account/Logout, which signs out the INTERNAL
        // employee identity and leaves the portal cookie standing. On a shared machine a customer
        // signing out of the portal would have signed a colleague out of the ERP, and stayed signed
        // in themselves.
        [Fact]
        public void The_portal_header_signs_out_through_PortalAccount_and_never_through_Account()
        {
            var header = Code(Path.Combine(PortalViews(), "_PortalHeader.cshtml"));

            Assert.Contains("\"Logout\", \"PortalAccount\"", header);

            // The internal logout must not be reachable from the portal shell in any spelling.
            Assert.DoesNotContain("asp-action=\"Logout\"", header);
            Assert.DoesNotContain("\"Logout\", \"Account\"", header);
            Assert.DoesNotContain("/Account/Logout", header);
        }

        // Signing out is a MUTATION, so it is a POST with an anti-forgery token. A GET that performs
        // one can be fired by any image tag on any site.
        [Fact]
        public void The_portal_logout_is_a_post_carrying_an_antiforgery_token()
        {
            var header = Code(Path.Combine(PortalViews(), "_PortalHeader.cshtml"));

            var form = Regex.Match(header, "<form[^>]*Logout[^>]*>.*?</form>", RegexOptions.Singleline);
            Assert.True(form.Success, "the portal sign-out is not inside a form");
            Assert.Contains("method=\"post\"", form.Value, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("AntiForgeryToken", form.Value);
        }

        // ---- 28 & 29. UPDATES AND DOCUMENTS ARE IN THE NAVIGATION -------------------------------
        [Fact]
        public void The_portal_navigation_offers_updates_and_documents()
        {
            var header = Code(Path.Combine(PortalViews(), "_PortalHeader.cshtml"));

            Assert.Contains("(\"Updates\"", header);
            Assert.Contains("(\"Documents\"", header);

            // And the four sections that were already there survived the edit.
            foreach (var section in new[] { "Index", "Quotations", "Projects", "Invoices" })
                Assert.Contains($"(\"{section}\"", header);
        }

        // A single conversation is a page WITHIN Updates, so it lights the Updates entry rather than
        // blanking the menu.
        [Fact]
        public void A_conversation_keeps_the_updates_entry_active()
        {
            var header = Code(Path.Combine(PortalViews(), "_PortalHeader.cshtml"));
            Assert.Contains("\"Conversation\"", header);
            Assert.Contains("section = \"Updates\"", header);
        }

        // ---- 30. THE AVATAR COMES FROM THE PROTECTED ENDPOINT -----------------------------------
        //
        // The measured defect: PhotoPath went straight into an <img src>, which made a guessable
        // path the only control, and 404'd for the customer whose photograph it was.
        [Fact]
        public void The_avatar_is_served_by_the_authorizing_endpoint_and_never_by_a_stored_path()
        {
            var header = Code(Path.Combine(PortalViews(), "_PortalHeader.cshtml"));

            Assert.Contains("\"ContactPhoto\", \"ClientPortal\"", header);

            // The stored value is consulted ONLY to ask whether a photograph exists.
            Assert.Contains("IsNullOrWhiteSpace(portal?.PhotoPath)", header);

            // It is never emitted as a url.
            Assert.DoesNotContain("src=\"@portal", header);
            Assert.DoesNotContain("portal!.PhotoPath!;", header);
        }

        // NO PORTAL SURFACE RENDERS A RAW UPLOAD PATH. This is the whole-of-shell version of the
        // assertion above: not one page, partial or account view may point at the upload folder.
        [Fact]
        public void No_portal_view_renders_a_raw_uploads_portal_url()
        {
            foreach (var file in Directory.GetFiles(PortalViews(), "*.cshtml")
                                          .Concat(Directory.GetFiles(AccountViews(), "*.cshtml")))
            {
                var code = Code(file);
                Assert.False(code.Contains("/uploads/portal", StringComparison.OrdinalIgnoreCase),
                    $"{Path.GetFileName(file)} renders a raw /uploads/portal path");
            }
        }

        // ---- THE SHELL IS ONE DESIGN --------------------------------------------------------------
        //
        // The old cbp-* shell is gone. Two portal designs answering the same customer is the "second
        // competing design" the convergence exists to remove, and a dead duplicate shell is how one
        // quietly comes back.
        [Fact]
        public void The_old_portal_layout_is_gone_and_nothing_references_it()
        {
            Assert.False(File.Exists(Path.Combine(PortalViews(), "_Layout.cshtml")),
                "the dead portal shell is still present");

            foreach (var file in Directory.GetFiles(PortalViews(), "*.cshtml")
                                          .Concat(Directory.GetFiles(AccountViews(), "*.cshtml")))
            {
                var code = Code(file);
                Assert.DoesNotContain("Layout = \"_Layout\"", code);
            }
        }

        // Every portal PAGE pins Layout = null and renders the retained header. That is what makes
        // the deletion above safe, and it is asserted rather than assumed.
        [Fact]
        public void Every_portal_page_is_a_whole_metronic_document_using_the_retained_header()
        {
            var pages = PortalPages();
            Assert.True(pages.Length >= 8, $"expected the eight portal pages, found {pages.Length}");

            foreach (var file in pages)
            {
                var code = Code(file);
                Assert.Contains("Layout = null", code);
                Assert.Contains("Html.PartialAsync(\"_PortalHeader\")", code);
                Assert.Contains("/portal-assets/", code);
            }
        }

        // The five retained screens are still here. This is the preservation half of the wave: the
        // programme's functionality arrived, and none of the theme's screens left with it.
        [Fact]
        public void The_retained_metronic_screens_are_all_present()
        {
            foreach (var name in new[] { "Index", "Quotations", "Projects", "Invoices", "NoAccess" })
                Assert.True(File.Exists(Path.Combine(PortalViews(), name + ".cshtml")),
                    $"retained portal screen {name}.cshtml is missing");

            // And the three the convergence brought over.
            foreach (var name in new[] { "Updates", "Conversation", "Documents" })
                Assert.True(File.Exists(Path.Combine(PortalViews(), name + ".cshtml")),
                    $"converged portal screen {name}.cshtml is missing");
        }

        // ---- THE LOGIN SCREEN --------------------------------------------------------------------
        //
        // No company, customer or employee selector, because each would be an authority the server
        // must never take from the browser.
        [Fact]
        public void The_portal_login_posts_only_credentials()
        {
            var login = Code(Path.Combine(AccountViews(), "Login.cshtml"));

            Assert.Contains("name=\"userName\"", login);
            Assert.Contains("name=\"password\"", login);
            Assert.Contains("AntiForgeryToken", login);

            foreach (var forbidden in new[] { "name=\"companyId\"", "name=\"CompanyID\"", "name=\"customerId\"", "name=\"employeeId\"", "name=\"portalUserId\"" })
                Assert.DoesNotContain(forbidden, login, StringComparison.OrdinalIgnoreCase);
        }

        // It is re-skinned onto the RETAINED portal identity, not onto the internal backend shell —
        // a customer must not sign in on one product and land on another.
        [Fact]
        public void The_portal_login_and_denied_pages_wear_the_retained_portal_skin()
        {
            foreach (var name in new[] { "Login.cshtml", "Denied.cshtml" })
            {
                var code = Code(Path.Combine(AccountViews(), name));
                Assert.Contains("/portal-assets/css/style.bundle", code);
                Assert.DoesNotContain("/Backend-assets/css/style.bundle", code);
            }
        }

        // ---- FONTS -------------------------------------------------------------------------------
        //
        // Cairo is served from disk on every portal surface. The internal product already took this
        // decision for itself ("no page load reports a user's IP to a third party"), and the portal
        // is the EXTERNAL surface, so it is where the reasoning applies most rather than least.
        //
        // Inter is deliberately NOT asserted absent: no local copy exists in this repository, and
        // dropping its stylesheet would change the Latin typography of the retained pages rather
        // than merely change where a file comes from. That one is reported, not silently altered.
        [Fact]
        public void Cairo_is_served_locally_on_every_portal_surface()
        {
            foreach (var file in Directory.GetFiles(PortalViews(), "*.cshtml")
                                          .Concat(Directory.GetFiles(AccountViews(), "*.cshtml")))
            {
                var code = Code(file);
                if (!code.Contains("Cairo", StringComparison.Ordinal)) continue;

                Assert.False(Regex.IsMatch(code, @"fonts\.googleapis\.com[^""']*Cairo"),
                    $"{Path.GetFileName(file)} still fetches Cairo from Google Fonts");
                Assert.Contains("/Backend-assets/fonts/cairo/cairo.css", code);
            }
        }

        // Every local font face the sheet declares must actually exist on disk. A @font-face naming a
        // missing file is a silent fallback, which is exactly the regression this file replaced.
        [Fact]
        public void Every_declared_cairo_face_exists_on_disk()
        {
            var dir = Path.Combine(Repo(), "CrossBuy", "wwwroot", "Backend-assets", "fonts", "cairo");
            var css = File.ReadAllText(Path.Combine(dir, "cairo.css"));

            var referenced = Regex.Matches(css, @"url\(""([^""]+)""\)")
                                  .Select(m => m.Groups[1].Value)
                                  .Distinct();

            Assert.NotEmpty(referenced);
            foreach (var r in referenced)
                Assert.True(File.Exists(Path.Combine(dir, r)), $"cairo.css names {r}, which is not on disk");
        }
    }
}
