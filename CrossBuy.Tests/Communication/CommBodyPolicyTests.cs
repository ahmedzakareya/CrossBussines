using CrossBuy.BL.Communication;
using CrossBuy.Models.Communication;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =============================================================================================
    // Communication Platform — THE BODY POLICY.
    //
    // The most heavily tested class in the platform, because it is a PURE FUNCTION of (body, format, options):
    // no database, no context, no I/O. Everything it decides — what a mention is, what counts as a table, what
    // an excerpt looks like — is decidable from the input alone, so there is no excuse for leaving it unproven.
    //
    // The cases below are grouped by the requirement they come from: "Markdown / Rich Text / Images / Files /
    // Links / Code Blocks / Tables" and the @Employee / @Team / @Department mention grammar.
    // =============================================================================================
    public class CommBodyPolicyTests
    {
        private static ICommBodyPolicy Policy(CommunicationPlatformOptions? options = null)
            => new CommBodyPolicy(Options.Create(options ?? new CommunicationPlatformOptions()));

        // ---------------------------------------------------------------------------------------------
        // Validation
        // ---------------------------------------------------------------------------------------------
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\n\n\t")]
        public void An_empty_body_is_refused_with_body_required(string? body)
        {
            var ex = Assert.Throws<CommValidationException>(() => Policy().Prepare(body, CommBodyFormat.Markdown));
            Assert.Equal(CommValidationException.Codes.BodyRequired, ex.Code);
        }

        [Fact]
        public void An_unknown_format_is_refused_and_html_is_deliberately_unknown()
        {
            // HTML is not "not yet supported" — it is refused by design, because accepting authored HTML means
            // owning a sanitizer forever and a sanitizer bug in a comment body is stored XSS.
            var ex = Assert.Throws<CommValidationException>(() => Policy().Prepare("<b>hi</b>", "Html"));
            Assert.Equal(CommValidationException.Codes.BodyFormatInvalid, ex.Code);
        }

        // THE LIMIT IS BYTES, NOT CHARACTERS, and this is the test that proves it matters. An Arabic body is
        // roughly two UTF-8 bytes per character, so a character cap would silently halve the real limit for
        // Arabic authors — in a product whose primary language is Arabic.
        [Fact]
        public void The_size_limit_counts_utf8_bytes_so_arabic_is_not_silently_halved()
        {
            var options = new CommunicationPlatformOptions { MaxBodyBytes = 100 };

            // 60 Arabic characters = 120 UTF-8 bytes: under a 100-CHARACTER cap, over the 100-BYTE cap.
            var arabic = new string('ن', 60);
            var ex = Assert.Throws<CommValidationException>(() => Policy(options).Prepare(arabic, CommBodyFormat.Markdown));
            Assert.Equal(CommValidationException.Codes.BodyTooLarge, ex.Code);

            // The same character COUNT in ASCII is 60 bytes and is accepted — proving the cap is on bytes.
            var ascii = new string('a', 60);
            var ok = Policy(options).Prepare(ascii, CommBodyFormat.Markdown);
            Assert.Equal(60, ok.Analysis.Bytes);
        }

        // A CRLF body from a Windows client must measure the same as the identical text from a mobile client.
        // A size limit that depends on the client is not a limit.
        [Fact]
        public void Line_endings_are_normalised_before_the_body_is_measured()
        {
            var crlf = Policy().Prepare("line one\r\nline two\r\nline three", CommBodyFormat.Markdown);
            var lf = Policy().Prepare("line one\nline two\nline three", CommBodyFormat.Markdown);

            Assert.Equal(lf.Body, crlf.Body);
            Assert.Equal(lf.Analysis.Bytes, crlf.Analysis.Bytes);
            Assert.DoesNotContain("\r", crlf.Body);
        }

        // ---------------------------------------------------------------------------------------------
        // Mention grammar (module 3)
        // ---------------------------------------------------------------------------------------------
        [Fact]
        public void The_canonical_mention_form_is_parsed_for_all_three_wired_kinds()
        {
            var result = Policy().Prepare(
                "please review @employee:12 with @team:13 and @department:501", CommBodyFormat.Markdown);

            Assert.Equal(3, result.Mentions.Count);
            Assert.Contains(result.Mentions, m => m.TargetKind == CommMentionTargetKind.Employee && m.TargetId == 12);
            Assert.Contains(result.Mentions, m => m.TargetKind == CommMentionTargetKind.Team && m.TargetId == 13);
            Assert.Contains(result.Mentions, m => m.TargetKind == CommMentionTargetKind.Department && m.TargetId == 501);
        }

        // The labelled form exists so an UNRENDERED body still reads as a name rather than as an id.
        [Fact]
        public void The_labelled_mention_form_carries_its_display_label()
        {
            var result = Policy().Prepare("cc @[Ahmed Zakarya](employee:11) please", CommBodyFormat.Markdown);

            var mention = Assert.Single(result.Mentions);
            Assert.Equal(CommMentionTargetKind.Employee, mention.TargetKind);
            Assert.Equal(11, mention.TargetId);
            Assert.Equal("Ahmed Zakarya", mention.Label);
        }

        // The labelled form ends in "employee:11)", which the PLAIN pattern would also match. Counting it twice
        // would be harmless after dedup but would corrupt the count MaxMentionsPerComment checks.
        [Fact]
        public void A_labelled_mention_is_not_also_counted_as_a_plain_mention()
        {
            var result = Policy().Prepare("@[Ahmed](employee:11)", CommBodyFormat.Markdown);

            Assert.Single(result.Mentions);
            Assert.Equal(1, result.Analysis.MentionTokenCount);
        }

        // A labelled mention looks like a markdown link preceded by '@'. It must not inflate the LINK count.
        [Fact]
        public void A_labelled_mention_is_not_counted_as_a_link()
        {
            var result = Policy().Prepare("@[Ahmed](employee:11) see docs", CommBodyFormat.Markdown);

            Assert.Equal(0, result.Analysis.LinkCount);
            Assert.Equal(1, result.Analysis.MentionTokenCount);
        }

        // CODE IS NOT PROSE. Documenting the mention syntax must not notify employee 12. This is the case that
        // makes the feature safe to document inside the product.
        [Fact]
        public void Mentions_inside_a_fenced_code_block_are_not_mentions()
        {
            var body = "Here is how you mention somebody:\n\n```\n@employee:12\n```\n\nThat is the syntax.";

            var result = Policy().Prepare(body, CommBodyFormat.Markdown);

            Assert.Empty(result.Mentions);
            Assert.Equal(1, result.Analysis.CodeBlockCount);
        }

        [Fact]
        public void Mentions_inside_inline_code_are_not_mentions()
        {
            var result = Policy().Prepare("type `@employee:12` to mention", CommBodyFormat.Markdown);
            Assert.Empty(result.Mentions);
        }

        [Fact]
        public void The_same_target_mentioned_twice_is_one_mention()
        {
            var result = Policy().Prepare("@employee:12 and again @employee:12", CommBodyFormat.Markdown);
            Assert.Single(result.Mentions);
        }

        // A malformed token is DROPPED as plain text rather than failing the comment: the author cannot always
        // tell where the token grammar ends and their sentence begins, and losing a whole comment to a typo is
        // the worse outcome.
        [Theory]
        [InlineData("@employee:abc")]
        [InlineData("@employee:0")]
        [InlineData("@empoyee:12")]
        [InlineData("@employee:")]
        [InlineData("email me at ahmed@employee.com")]
        public void A_malformed_or_unknown_token_is_plain_text_not_an_error(string body)
        {
            var result = Policy().Prepare(body, CommBodyFormat.Markdown);
            Assert.Empty(result.Mentions);
        }

        // A role mention PARSES — the grammar knows the word — so that the service layer can refuse it with a
        // specific reason. If the parser dropped it, the author would get silence instead of "role mentions are
        // not wired yet".
        [Fact]
        public void A_role_mention_parses_so_the_service_can_refuse_it_with_a_reason()
        {
            var result = Policy().Prepare("@role:InventoryManager please look", CommBodyFormat.Markdown);

            var mention = Assert.Single(result.Mentions);
            Assert.Equal(CommMentionTargetKind.Role, mention.TargetKind);
            Assert.Equal("InventoryManager", mention.TargetKey);
            Assert.Null(mention.TargetId);
        }

        [Fact]
        public void The_kind_is_case_insensitive_on_input_and_canonical_on_output()
        {
            var result = Policy().Prepare("@Employee:12 @DEPARTMENT:501", CommBodyFormat.Markdown);

            Assert.Contains(result.Mentions, m => m.TargetKind == CommMentionTargetKind.Employee);
            Assert.Contains(result.Mentions, m => m.TargetKind == CommMentionTargetKind.Department);
        }

        [Fact]
        public void Too_many_mentions_is_refused_at_prepare_time()
        {
            var options = new CommunicationPlatformOptions { MaxMentionsPerComment = 2 };
            var body = "@employee:1 @employee:2 @employee:3";

            var ex = Assert.Throws<CommValidationException>(() => Policy(options).Prepare(body, CommBodyFormat.Markdown));
            Assert.Equal(CommValidationException.Codes.TooManyMentions, ex.Code);
        }

        // ---------------------------------------------------------------------------------------------
        // Structural analysis — the "Markdown / Images / Links / Code Blocks / Tables" requirement
        // ---------------------------------------------------------------------------------------------
        [Fact]
        public void A_rich_body_reports_every_structure_it_contains()
        {
            var body = """
                # Heading

                Some **bold** text with a [link](https://example.com/docs) and an
                ![diagram](https://cdn.example.org/d.png).

                - first
                - second

                > a quote

                | col a | col b |
                | ----- | ----- |
                | 1     | 2     |

                ```csharp
                var x = 1;
                ```
                """;

            var analysis = Policy().Prepare(body, CommBodyFormat.Markdown).Analysis;

            Assert.Equal(1, analysis.LinkCount);
            Assert.Equal(1, analysis.ImageCount);
            Assert.Equal(1, analysis.CodeBlockCount);
            Assert.Equal(1, analysis.TableCount);
            Assert.True(analysis.HasHeading);
            Assert.True(analysis.HasList);
            Assert.True(analysis.HasBlockQuote);
        }

        // An image "![x](y)" also matches the LINK pattern. Counting one construct twice would make the
        // metadata wrong in the most common case there is.
        [Fact]
        public void An_image_is_counted_as_an_image_and_not_also_as_a_link()
        {
            var analysis = Policy().Prepare("![alt](https://x.test/a.png)", CommBodyFormat.Markdown).Analysis;

            Assert.Equal(1, analysis.ImageCount);
            Assert.Equal(0, analysis.LinkCount);
        }

        // A pipe in a sentence is not a table. The DELIMITER row is what makes a GFM table, which is why the
        // pattern anchors on it rather than on the presence of '|'.
        [Fact]
        public void A_pipe_in_prose_is_not_a_table()
        {
            var analysis = Policy().Prepare("choose a | b | c from the list", CommBodyFormat.Markdown).Analysis;
            Assert.Equal(0, analysis.TableCount);
        }

        // Link hosts are recorded because a markdown image with a remote src fires a GET — carrying a referrer —
        // the moment anybody opens the record. A deployment that wants to allow-list hosts must be able to find
        // out which ones appear.
        [Fact]
        public void External_link_hosts_are_recorded_and_deduped_and_relative_links_are_ignored()
        {
            var body = "[a](https://example.com/1) [b](https://example.com/2) " +
                       "![c](https://cdn.other.test/x.png) [d](/Accounting/SalesInvoiceDetail?id=5)";

            var analysis = Policy().Prepare(body, CommBodyFormat.Markdown).Analysis;

            Assert.Contains("example.com", analysis.LinkHosts);
            Assert.Contains("cdn.other.test", analysis.LinkHosts);
            Assert.Equal(2, analysis.LinkHosts.Count);   // example.com appears twice, recorded once
        }

        // A markdown EXAMPLE inside a fence is not the comment's own structure. Reporting it as such would make
        // the metadata describe the documentation rather than the comment.
        [Fact]
        public void Structure_inside_a_code_fence_is_not_reported_as_the_bodys_own()
        {
            var body = "```\n# not a heading\n| a | b |\n| - | - |\n[not a link](https://x.test)\n```";

            var analysis = Policy().Prepare(body, CommBodyFormat.Markdown).Analysis;

            Assert.Equal(1, analysis.CodeBlockCount);
            Assert.False(analysis.HasHeading);
            Assert.Equal(0, analysis.TableCount);
            Assert.Equal(0, analysis.LinkCount);
        }

        // PlainText has no structure BY DEFINITION. Reporting regex hits from it would claim structure the
        // renderer will not honour.
        [Fact]
        public void Plain_text_reports_no_structure_but_still_reports_bytes()
        {
            var body = "# not a heading [not a link](https://x.test)";

            var analysis = Policy().Prepare(body, CommBodyFormat.PlainText).Analysis;

            Assert.False(analysis.HasHeading);
            Assert.Equal(0, analysis.LinkCount);
            Assert.True(analysis.Bytes > 0);
        }

        // ---------------------------------------------------------------------------------------------
        // Excerpts — what a mention notification quotes
        // ---------------------------------------------------------------------------------------------
        [Fact]
        public void An_excerpt_strips_markdown_and_collapses_to_one_line()
        {
            var body = "# Title\n\nSome **bold** and a [link](https://x.test) and `code`.";

            var excerpt = Policy().Excerpt(body, CommBodyFormat.Markdown, 200);

            Assert.DoesNotContain("#", excerpt);
            Assert.DoesNotContain("**", excerpt);
            Assert.DoesNotContain("](", excerpt);
            Assert.DoesNotContain("\n", excerpt);
            Assert.Contains("bold", excerpt);
            Assert.Contains("link", excerpt);   // the label survives, the url does not
        }

        // A bare numeric id in a notification tells the reader nothing. The label is what a human recognises.
        [Fact]
        public void An_excerpt_renders_a_labelled_mention_as_its_label_not_its_id()
        {
            var excerpt = Policy().Excerpt("cc @[Ahmed Zakarya](employee:11) please", CommBodyFormat.Markdown, 200);

            Assert.Contains("@Ahmed Zakarya", excerpt);
            Assert.DoesNotContain("employee:11", excerpt);
        }

        [Fact]
        public void A_long_excerpt_is_truncated_on_a_word_boundary_with_an_ellipsis()
        {
            var body = string.Join(" ", Enumerable.Repeat("word", 200));

            var excerpt = Policy().Excerpt(body, CommBodyFormat.Markdown, 40);

            Assert.True(excerpt.Length <= 41);           // 40 plus the ellipsis
            Assert.EndsWith("…", excerpt);
            Assert.DoesNotContain("wor…", excerpt);      // cut on a boundary, not mid-word
        }

        // The word-boundary cut must not swallow almost everything when there are no spaces — a long URL is the
        // realistic case, and a 3-character excerpt would be useless.
        [Fact]
        public void A_body_with_no_spaces_still_yields_a_useful_excerpt()
        {
            var body = new string('x', 300);

            var excerpt = Policy().Excerpt(body, CommBodyFormat.Markdown, 40);

            Assert.True(excerpt.Length >= 30);
            Assert.EndsWith("…", excerpt);
        }

        [Fact]
        public void A_code_block_becomes_a_placeholder_in_an_excerpt()
        {
            var excerpt = Policy().Excerpt("before\n```\nvar x = 1;\n```\nafter", CommBodyFormat.Markdown, 200);

            Assert.Contains("[code]", excerpt);
            Assert.Contains("before", excerpt);
            Assert.Contains("after", excerpt);
        }

        [Fact]
        public void An_image_becomes_its_alt_text_in_an_excerpt()
        {
            var excerpt = Policy().Excerpt("see ![the chart](https://x.test/c.png)", CommBodyFormat.Markdown, 200);

            Assert.Contains("[the chart]", excerpt);
            Assert.DoesNotContain("x.test", excerpt);
        }
    }
}
