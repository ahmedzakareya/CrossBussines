using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H266 — the patient's assistant. What must never happen: an emergency answered with anything but the
	// emergency number; a clinical question answered instead of sent to the clinic; a guess on an unknown question; an
	// unreleased result shown; clitics or punctuation hiding a word; "latest" read as "test".
	// ==========================================================================================
	public class HospitalPortalAssistantTests
	{
		private static string I(string q) => HospitalPortalAssistantRules.Intent(q);

		[Theory]
		[InlineData("عندي ألم في الصدر الآن", HospitalPortalAssistantIntent.Emergency)]
		[InlineData("I can't breathe", HospitalPortalAssistantIntent.Emergency)]
		[InlineData("هل يجب أن أوقف الدواء؟", HospitalPortalAssistantIntent.Clinical)]
		[InlineData("is it normal to feel dizzy after the medicine?", HospitalPortalAssistantIntent.Clinical)]
		[InlineData("متى موعدي القادم؟", HospitalPortalAssistantIntent.NextVisit)]
		[InlineData("وموعدي مع الدكتور متى", HospitalPortalAssistantIntent.NextVisit)]
		[InlineData("When is my next appointment?", HospitalPortalAssistantIntent.NextVisit)]
		[InlineData("آخر نتائجي", HospitalPortalAssistantIntent.Results)]
		[InlineData("بنتيجتي التحليل", HospitalPortalAssistantIntent.Results)]
		[InlineData("My latest results", HospitalPortalAssistantIntent.Results)]
		[InlineData("كم المبلغ المستحق علي؟", HospitalPortalAssistantIntent.Bills)]
		[InlineData("how much do i owe", HospitalPortalAssistantIntent.Bills)]
		[InlineData("my latest bill", HospitalPortalAssistantIntent.Bills)]
		[InlineData("أدويتي", HospitalPortalAssistantIntent.Medicines)]
		[InlineData("I need a refill", HospitalPortalAssistantIntent.Medicines)]
		[InlineData("أريد أن أحجز موعد جديد", HospitalPortalAssistantIntent.Book)]
		[InlineData("ما هو الطقس اليوم", HospitalPortalAssistantIntent.Unknown)]
		[InlineData("", HospitalPortalAssistantIntent.Unknown)]
		public void Each_question_goes_to_one_intent(string q, string intent) => Assert.Equal(intent, I(q));

		private static readonly HospitalPortalPerson Me = new(25, "سامي", "Sami", "MRN-25", null, "Self");
		private static readonly DateTime Now = new(2026, 11, 23, 9, 0, 0);

		[Fact]
		public void An_emergency_gives_the_number_and_nothing_else()
		{
			var a = HospitalPortalAssistantRules.Answer(HospitalPortalAssistantIntent.Emergency, [], [], [], [], true, Now);
			Assert.Single(a.Lines); Assert.Contains(HospitalPortalAssistantRules.EmergencyNumber, a.Lines[0].Args);
			Assert.Null(a.Link);
		}

		[Fact]
		public void Only_released_results_are_named()
		{
			HospitalPortalResult R(string name, bool released, bool abnormal) => new("Lab", 1, Me, "LAB-1", name, name, Now.AddDays(-1), null, released, null, Array.Empty<HospitalLabResult>(), null, null, abnormal);
			var a = HospitalPortalAssistantRules.Answer(HospitalPortalAssistantIntent.Results, [], [R("Lipid profile", true, true), R("Kidney function", false, false)], [], [], false, Now);
			Assert.Equal(2, a.Lines.Count);
			Assert.Contains("Lipid profile", a.Lines[0].Args);
			Assert.DoesNotContain(a.Lines, l => l.Args.Contains("Kidney function"));
			Assert.Equal(1, a.Lines[1].Args[0]);   // one is with the doctor
		}

		[Fact]
		public void The_next_appointment_and_nothing_past()
		{
			HospitalPortalVisit V(int days, string status) => new(days + 100, Me, Now.Date.AddDays(days), 600, "FollowUp", status, 11, "د. أحمد", "Dr Ahmed", null, null, null, "الباطنة", "Medicine", false);
			var a = HospitalPortalAssistantRules.Answer(HospitalPortalAssistantIntent.NextVisit, [V(-3, "Booked"), V(2, "Booked"), V(5, "Cancelled")], [], [], [], false, Now);
			Assert.Single(a.Lines);
			Assert.Contains("2026-11-25", a.Lines[0].Args);
			Assert.Equal("Your appointments", HospitalPortalAssistantRules.Answer(HospitalPortalAssistantIntent.NextVisit, [], [], [], [], false, Now).TitleKey);
		}

		[Fact]
		public void Unknown_is_refused_with_what_it_can_do()
		{
			var a = HospitalPortalAssistantRules.Answer(HospitalPortalAssistantIntent.Unknown, [], [], [], [], true, Now);
			Assert.Equal("Messages", a.Link);
			Assert.StartsWith("I can tell you", a.Lines[0].Key);
		}
	}
}
