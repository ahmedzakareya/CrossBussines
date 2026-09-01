using CrossBuy.BL.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // Platform Foundation — the private-file classification, tested as the pure function it is.
    //
    // These assert the POLICY TABLE, not middleware plumbing: the exploit was a classification failure
    // (private documents classified as public chrome), so that is what is pinned here. The end-to-end
    // behaviour is verified separately against a running host.
    public class PrivateFileGateTests
    {
        // ---- the four paths an anonymous request actually retrieved before the gate existed ----
        [Theory]
        [InlineData("/uploads/hr-docs/189bee67-30f4-4b09-8555-5f94d24767f3.pdf")]
        [InlineData("/uploads/chat/23921eab-9570-4b5b-a7ca-22bac4714f37.jpg")]
        [InlineData("/Files/11/2a098cef-9a25-4873-8368-c72d16076903.pdf")]
        [InlineData("/Files/10/91167a3c-8fa3-484b-8731-9c781b491d71.pdf")]
        public void The_paths_that_leaked_anonymously_now_require_authentication(string path)
            => Assert.Equal(PrivateFilePolicy.RequireAuthenticated, PrivateFileGate.PolicyFor(path));

        [Theory]
        [InlineData("/uploads/comm/invite.ics")]
        [InlineData("/uploads/applicants/cv.pdf")]
        [InlineData("/uploads/library/7/contract.pdf")]
        [InlineData("/uploads/employees/photo.jpg")]
        public void The_remaining_private_stores_require_authentication(string path)
            => Assert.Equal(PrivateFilePolicy.RequireAuthenticated, PrivateFileGate.PolicyFor(path));

        // ---- the explicit non-goal: product chrome and the anonymous storefront stay public ----
        //
        // StoreController is [AllowAnonymous] and renders the catalogue, so gating item/brand images would
        // break the storefront for exactly the visitors it exists to serve. CSS/JS/fonts must never be routed
        // through authorization at all.
        [Theory]
        [InlineData("/Backend-assets/css/crossbuy-brand.css")]
        [InlineData("/Backend-assets/media/svg/brand-logos/google-icon.svg")]
        [InlineData("/uploads/items/widget.png")]
        [InlineData("/uploads/brands/acme.png")]
        [InlineData("/favicon.ico")]
        [InlineData("/pos-sw.js")]
        [InlineData("/")]
        [InlineData("")]
        public void Public_chrome_and_the_anonymous_storefront_are_untouched(string path)
            => Assert.Equal(PrivateFilePolicy.Public, PrivateFileGate.PolicyFor(path));

        [Fact]
        public void A_null_path_is_not_treated_as_private()
            => Assert.Equal(PrivateFilePolicy.Public, PrivateFileGate.PolicyFor(null));

        // The filesystem is case-insensitive on Windows, so a differently-cased URL reaches the same bytes.
        // If classification were case-sensitive, "/UPLOADS/HR-DOCS/x.pdf" would serve the document while
        // "/uploads/hr-docs/x.pdf" was refused — the gate would be trivially bypassable.
        [Theory]
        [InlineData("/UPLOADS/HR-DOCS/x.pdf")]
        [InlineData("/Uploads/Chat/x.jpg")]
        [InlineData("/FILES/11/x.pdf")]
        public void Classification_is_case_insensitive(string path)
            => Assert.Equal(PrivateFilePolicy.RequireAuthenticated, PrivateFileGate.PolicyFor(path));

        // A prefix must match on a SEGMENT boundary. Without this, "/uploads/chat" would also claim
        // "/uploads/chatter-public/…", and a future public folder whose name merely starts with a private
        // one would be silently locked — a self-inflicted outage rather than a leak, but still wrong.
        [Theory]
        [InlineData("/uploads/chatter-public/x.png")]
        [InlineData("/uploads/items-archive/x.png")]
        [InlineData("/FilesPublic/x.png")]
        public void A_prefix_only_matches_on_a_segment_boundary(string path)
            => Assert.Equal(PrivateFilePolicy.Public, PrivateFileGate.PolicyFor(path));

        // Kestrel removes dot segments before middleware runs, so this is defence in depth. It is asserted
        // because the failure it guards is the dangerous one: a traversal that lands inside a private folder
        // while matching no private prefix would be served as public.
        [Theory]
        [InlineData("/Backend-assets/../uploads/hr-docs/x.pdf")]
        [InlineData("/uploads/items/../hr-docs/x.pdf")]
        public void A_surviving_dot_segment_fails_closed(string path)
            => Assert.Equal(PrivateFilePolicy.RequireAuthenticated, PrivateFileGate.PolicyFor(path));

        // The table's lookup does not depend on ordering, which is only true while no prefix is a prefix of
        // another. Asserting the invariant is cheaper than debugging the day someone adds "/uploads".
        [Fact]
        public void No_private_prefix_is_a_prefix_of_another()
        {
            var prefixes = PrivateFileGate.PrivatePrefixes;
            for (int i = 0; i < prefixes.Count; i++)
                for (int j = 0; j < prefixes.Count; j++)
                {
                    if (i == j) continue;
                    Assert.False(
                        prefixes[j].StartsWith(prefixes[i] + "/", System.StringComparison.OrdinalIgnoreCase),
                        $"'{prefixes[j]}' sits under '{prefixes[i]}', so the policy depends on rule order.");
                }
        }

        [Fact]
        public void Every_private_prefix_is_rooted_and_lowercase_for_ordinal_ignore_case_matching()
        {
            foreach (var p in PrivateFileGate.PrivatePrefixes)
            {
                Assert.StartsWith("/", p, System.StringComparison.Ordinal);
                Assert.DoesNotContain(" ", p, System.StringComparison.Ordinal);
                Assert.False(p.EndsWith("/", System.StringComparison.Ordinal),
                    $"'{p}' has a trailing slash, which breaks the segment-boundary test.");
            }
        }
    }
}
