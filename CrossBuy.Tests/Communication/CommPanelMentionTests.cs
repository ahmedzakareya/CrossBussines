using CrossBuy.BL.Communication;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =================================================================================================
    // "MENTIONED YOU" — the reader-facing half of the comment projection.
    //
    // WHAT THIS COVERS WITHOUT A DATABASE: every refusal path of CommPanel.MentionedMeAsync, and the
    // whole of Project's mentionsMe decision. The query itself needs CommMentionRecipients rows and is
    // not covered here; what IS covered is that the helper never reaches the query with a context it
    // cannot trust, and never hands back null for Project to misread.
    //
    // THE RISK THESE TESTS EXIST FOR is not an exception, it is a silence. Project's `mentionedMe`
    // parameter is optional, so a screen that never opts in renders mentionsMe = false on every row —
    // which on a page looks exactly like "nobody mentioned you". Four screens call Project and only
    // one passes a set today. The last two tests pin that down so it stays a known state rather than
    // becoming a bug report about a badge that never appears.
    // =================================================================================================
    public class CommPanelMentionTests
    {
        private static BusinessContext Reader(int companyId = 1, int? employeeId = 5) =>
            new() { CompanyId = companyId, EmployeeId = employeeId, UserId = "u" };

        private static CommCommentDto Comment(long id) => new()
        {
            CommentId = id,
            ThreadId = 1,
            Depth = 0,
            Body = "hello",
            BodyFormat = CommBodyFormat.PlainText,
            Visibility = CommVisibility.Internal,
            Author = new CommActorDto { EmployeeId = 9, NameEn = "Author" },
            RevisionCount = 0,
            IsDeleted = false,
            Mentions = Array.Empty<CommMentionDto>(),
            Attachments = Array.Empty<CommAttachmentDto>(),
            Reactions = Array.Empty<CommReactionSummaryDto>(),
            Capabilities = new CommCommentCapabilities
            {
                CanEdit = false, CanDelete = false, CanRestore = false, CanReact = false, CanReply = false,
            },
        };

        // Project returns anonymous objects for a JSON response, so the only way to read one back is
        // by name — which is also what the view does.
        private static bool MentionsMe(object row) =>
            (bool)row.GetType().GetProperty("mentionsMe")!.GetValue(row)!;

        // -----------------------------------------------------------------------------------------
        // EVERY REFUSAL PATH RETURNS AN EMPTY SET, NEVER NULL, AND NEVER THROWS.
        //
        // Null is not a safe degenerate value here: Project reads null as "this screen did not ask"
        // and an empty set as "asked, and nothing named you". A helper that returned null on a bad
        // context would quietly reclassify a failure as an opt-out.
        //
        // The database argument is null in all of these on purpose. If any guard were missing, the
        // call would reach `new CommDb(db)` and throw — so passing null is what proves the
        // short-circuit happened rather than merely that the answer came back empty.
        // -----------------------------------------------------------------------------------------
        public static TheoryData<string, BusinessContext?, IReadOnlyList<CommCommentDto>?> Degenerate() => new()
        {
            { "no context at all",        null,                        new[] { Comment(1) } },
            { "no employee resolved",     Reader(employeeId: null),    new[] { Comment(1) } },
            { "employee id is zero",      Reader(employeeId: 0),       new[] { Comment(1) } },
            { "no company resolved",      Reader(companyId: 0),        new[] { Comment(1) } },
            { "null page",                Reader(),                    null },
            { "empty page",               Reader(),                    Array.Empty<CommCommentDto>() },
        };

        [Theory]
        [MemberData(nameof(Degenerate))]
        public async Task A_context_or_page_it_cannot_trust_yields_an_empty_set_without_a_query(
            string because, BusinessContext? context, IReadOnlyList<CommCommentDto>? items)
        {
            var result = await CommPanel.MentionedMeAsync(null!, context!, items);

            Assert.NotNull(result);
            Assert.Empty(result);
            Assert.False(string.IsNullOrEmpty(because));
        }

        // -----------------------------------------------------------------------------------------
        // THE PROJECTION ITSELF. A comment id in the set is a mention of the reader; one outside it
        // is not. This is the whole of the decision, and it needs no database.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void Only_the_comments_in_the_set_are_marked_as_mentioning_the_reader()
        {
            var items = new[] { Comment(10), Comment(11), Comment(12) };
            var mentionedMe = new HashSet<long> { 11 };

            var rows = CommPanel.Project(items, arabic: false,
                avatars: new Dictionary<int, string>(), mentionedMe: mentionedMe);

            Assert.Equal(3, rows.Length);
            Assert.False(MentionsMe(rows[0]));
            Assert.True(MentionsMe(rows[1]));
            Assert.False(MentionsMe(rows[2]));
        }

        // -----------------------------------------------------------------------------------------
        // A SCREEN THAT DID NOT OPT IN LOOKS EXACTLY LIKE A SCREEN WITH NO MENTIONS.
        //
        // This is a FINDING pinned as a test, not a behaviour anybody should be pleased with. Passing
        // no set and passing an empty set produce byte-identical output, so the rendered page cannot
        // distinguish "this screen never resolved mentions" from "you were not mentioned". Today
        // Accounting passes a set and Inventory, Hospital and BusinessEventMonitor do not.
        //
        // If that is ever fixed by making the parameter required, this test fails — which is the
        // correct outcome, and the reason it is written as an equality rather than two assertions.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void Not_asking_and_finding_nothing_are_indistinguishable_in_the_output()
        {
            var items = new[] { Comment(10), Comment(11) };
            var avatars = new Dictionary<int, string>();

            var neverAsked = CommPanel.Project(items, arabic: false, avatars: avatars);
            var askedAndEmpty = CommPanel.Project(items, arabic: false, avatars: avatars,
                mentionedMe: new HashSet<long>());

            Assert.All(neverAsked, r => Assert.False(MentionsMe(r)));
            Assert.All(askedAndEmpty, r => Assert.False(MentionsMe(r)));

            // The discriminator: not merely both false, but no observable difference at all.
            Assert.Equal(neverAsked.Length, askedAndEmpty.Length);
            for (int i = 0; i < neverAsked.Length; i++)
                Assert.Equal(MentionsMe(neverAsked[i]), MentionsMe(askedAndEmpty[i]));
        }

        // The mention LABELS stay on the row regardless, because they describe the comment rather
        // than the reader. A screen that has not opted into mentionsMe still renders who was named.
        [Fact]
        public void Mention_labels_are_projected_even_when_the_reader_flag_was_never_resolved()
        {
            var rows = CommPanel.Project(new[] { Comment(10) }, arabic: false,
                avatars: new Dictionary<int, string>());

            var mentions = rows[0].GetType().GetProperty("mentions")!.GetValue(rows[0]);
            Assert.NotNull(mentions);
            Assert.False(MentionsMe(rows[0]));
        }
    }
}
