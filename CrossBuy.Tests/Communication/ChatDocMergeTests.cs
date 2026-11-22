using CrossBuy.BL.Communication;
using Xunit;

namespace CrossBuy.Tests.Communication
{
	// Shared documents in a conversation: two people saved from the same version; the three-way merge keeps both edits,
	// and where both changed the same lines differently it keeps both, marked — nothing anyone wrote is lost.
	public class ChatDocMergeTests
	{
		private static string M(string o, string a, string b) => ChatDocService.Merge3(o, a, b, "A", "B").text;

		[Fact]
		public void Edits_to_different_lines_both_survive()
		{
			var o = "one\ntwo\nthree\nfour";
			var r = ChatDocService.Merge3(o, "ONE\ntwo\nthree\nfour", "one\ntwo\nthree\nFOUR", "A", "B");
			Assert.Equal("ONE\ntwo\nthree\nFOUR", r.text);
			Assert.False(r.conflict);
		}

		[Fact]
		public void An_addition_and_a_deletion_both_apply()
			=> Assert.Equal("one\nnew\ntwo\nthree\nfive", M("one\ntwo\nthree\nfour\nfive", "one\nnew\ntwo\nthree\nfour\nfive", "one\ntwo\nthree\nfive"));

		[Fact]
		public void Lines_added_at_both_ends_both_survive()
			=> Assert.Equal("top\none\ntwo\nbottom", M("one\ntwo", "top\none\ntwo", "one\ntwo\nbottom"));

		[Fact]
		public void The_same_change_on_both_sides_is_kept_once()
		{
			var r = ChatDocService.Merge3("a\nb\nc", "a\nB\nc", "a\nB\nc", "A", "B");
			Assert.Equal("a\nB\nc", r.text);
			Assert.False(r.conflict);
		}

		[Fact]
		public void Different_changes_to_one_line_keep_both_marked()
		{
			var r = ChatDocService.Merge3("a\nb\nc", "a\nmine\nc", "a\ntheirs\nc", "Sara", "Khaled");
			Assert.True(r.conflict);
			Assert.Equal("a\n<<<<<<< Sara\nmine\n=======\ntheirs\n>>>>>>> Khaled\nc", r.text);
		}

		[Fact]
		public void A_neighbouring_line_only_one_changed_stays_out_of_the_conflict()
		{
			var r = ChatDocService.Merge3("a\nb\nc\nd", "a\nb\nc\nmine", "a\nb\nC\ntheirs", "S", "K");
			Assert.True(r.conflict);
			Assert.Equal("a\nb\nC\n<<<<<<< S\nmine\n=======\ntheirs\n>>>>>>> K", r.text);
		}

		[Fact]
		public void One_side_unchanged_takes_the_other_whole()
		{
			Assert.Equal("x\ny", M("a\nb", "a\nb", "x\ny"));
			Assert.Equal("x\ny", M("a\nb", "x\ny", "a\nb"));
			Assert.Equal("", M("a\nb", "", "a\nb"));
		}

		[Fact]
		public void Arabic_lines_merge_like_any_other()
			=> Assert.Equal("البند الأول معدل\nالبند الثاني\nالبند الثالث مضاف", M("البند الأول\nالبند الثاني", "البند الأول معدل\nالبند الثاني", "البند الأول\nالبند الثاني\nالبند الثالث مضاف"));
	}
}
