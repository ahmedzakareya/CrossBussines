using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H249 — portal messaging. What must never happen: a conversation started about someone the account does not
	// hold; an empty or over-long message; a closed conversation shown overdue; an answered one shown overdue; a model
	// naming a table its slice does not create.
	// ==========================================================================================
	public class HospitalPortalMessageTests
	{
		private static readonly DateTime Now = new(2026, 11, 23, 10, 0, 0);

		[Fact]
		public void Only_an_open_unanswered_conversation_older_than_two_days_is_overdue()
		{
			var t = new HospitalPortalThread { Status = HospitalPortalThreadStatus.Open, AwaitingClinic = true, LastMessageAt = Now.AddHours(-49) };
			Assert.True(HospitalPortalMessageRules.Overdue(t, Now));
			Assert.False(HospitalPortalMessageRules.Overdue(new HospitalPortalThread { Status = HospitalPortalThreadStatus.Open, AwaitingClinic = true, LastMessageAt = Now.AddHours(-47) }, Now));
			t.AwaitingClinic = false; Assert.False(HospitalPortalMessageRules.Overdue(t, Now));
			t.AwaitingClinic = true; t.Status = HospitalPortalThreadStatus.Closed; Assert.False(HospitalPortalMessageRules.Overdue(t, Now));
		}

		[Fact]
		public void A_message_has_a_topic_a_subject_and_a_body()
		{
			Assert.Null(HospitalPortalMessageRules.NewRefusal(HospitalPortalTopic.Clinical, "Cough", "Since three days"));
			Assert.NotNull(HospitalPortalMessageRules.NewRefusal("Gossip", "Cough", "x"));
			Assert.NotNull(HospitalPortalMessageRules.NewRefusal(HospitalPortalTopic.Billing, " ", "x"));
			Assert.NotNull(HospitalPortalMessageRules.NewRefusal(HospitalPortalTopic.Billing, "Bill", new string('x', 2001)));
			Assert.NotNull(HospitalPortalMessageRules.BodyRefusal(""));
		}

		[Fact]
		public async Task A_conversation_is_only_about_the_accounts_own_people()
		{
			var svc = new HospitalPortalMessageService(null!, null!);
			var r = await svc.StartAsync(1, 5, new[] { 25, 27 }, new HospitalPortalNewThreadInput { PatientId = 23, Topic = HospitalPortalTopic.Clinical, Subject = "x", Body = "y" }, Now);
			Assert.False(r.Ok); Assert.Equal("not_found", r.Error);
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPortalMessagesModel.Configure);
		private static string Slice() => HospitalSliceParity.Slice("hospital_h249_portal_messages.sql");

		[Theory]
		[InlineData(typeof(HospitalPortalThread), "HospitalPortalThreads")]
		[InlineData(typeof(HospitalPortalMessage), "HospitalPortalMessages")]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table)
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);
		}
	}
}
