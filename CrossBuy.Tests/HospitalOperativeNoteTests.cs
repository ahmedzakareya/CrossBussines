using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H260 — the operative note. What must never happen: a note signed with a required part empty; zero blood
	// loss read as "not written"; anyone but the case's surgeon signing; a signed note edited; a note written before the
	// operation started; a model naming a table its slice does not create.
	// ==========================================================================================
	public class HospitalOperativeNoteTests
	{
		private static HospitalOperativeNote Full() => new()
		{
			PostOpDiagnosis = "Acute appendicitis", ProcedurePerformed = "Laparoscopic appendicectomy", Findings = "Inflamed, non-perforated appendix",
			Technique = "Three-port laparoscopy; base ligated with endoloops", BloodLossMl = 0, Complications = "None", PostOpPlan = "Oral fluids tonight; home tomorrow",
		};

		[Fact]
		public void A_whole_note_misses_nothing_and_zero_blood_loss_is_an_answer()
		{
			Assert.Empty(HospitalOperativeNoteRules.Missing(Full()));
			var n = Full(); n.BloodLossMl = null; n.Complications = " ";
			Assert.Equal(new[] { "Estimated blood loss", "Complications" }, HospitalOperativeNoteRules.Missing(n));
			Assert.Equal(7, HospitalOperativeNoteRules.Missing(new HospitalOperativeNote()).Count);
			Assert.Equal(HospitalOperativeNoteRules.Required.Count, HospitalOperativeNoteRules.Missing(new HospitalOperativeNote()).Count);
		}

		[Fact]
		public void Only_the_cases_surgeon_signs_a_complete_unsigned_note()
		{
			Assert.Null(HospitalOperativeNoteRules.SignRefusal(Full(), 40, 40));
			Assert.NotNull(HospitalOperativeNoteRules.SignRefusal(Full(), 40, 41));
			Assert.NotNull(HospitalOperativeNoteRules.SignRefusal(Full(), null, 40));
			var half = Full(); half.Technique = null;
			Assert.Equal("Complete the operative note before signing: {0}", HospitalOperativeNoteRules.SignRefusal(half, 40, 40));
			var done = Full(); done.SignedAt = DateTime.UtcNow;
			Assert.Equal("The operative note is signed and locked", HospitalOperativeNoteRules.SignRefusal(done, 40, 40));
		}

		[Fact]
		public void A_note_is_written_during_or_after_the_operation_and_never_after_signing()
		{
			Assert.Null(HospitalOperativeNoteRules.EditRefusal(HospitalSurgeryStatus.InProgress, null, 150));
			Assert.Null(HospitalOperativeNoteRules.EditRefusal(HospitalSurgeryStatus.Done, null, null));
			Assert.NotNull(HospitalOperativeNoteRules.EditRefusal(HospitalSurgeryStatus.Planned, null, null));
			Assert.NotNull(HospitalOperativeNoteRules.EditRefusal(HospitalSurgeryStatus.Cancelled, null, null));
			Assert.NotNull(HospitalOperativeNoteRules.EditRefusal(HospitalSurgeryStatus.Done, DateTime.UtcNow, null));
			Assert.NotNull(HospitalOperativeNoteRules.EditRefusal(HospitalSurgeryStatus.InProgress, null, -5));
		}

		[Fact]
		public void Overdue_after_24_hours_unsigned()
		{
			var now = new DateTime(2026, 11, 23, 12, 0, 0);
			Assert.True(HospitalOperativeNoteRules.Overdue(now.AddHours(-25), null, now));
			Assert.False(HospitalOperativeNoteRules.Overdue(now.AddHours(-23), null, now));
			Assert.False(HospitalOperativeNoteRules.Overdue(now.AddHours(-30), now, now));
			Assert.False(HospitalOperativeNoteRules.Overdue(null, null, now));
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalOperativeNoteModel.Configure);
		private static string Slice() => HospitalSliceParity.Slice("hospital_h260_operative_notes.sql");

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), typeof(HospitalOperativeNote), "HospitalOperativeNotes");
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), typeof(HospitalOperativeNote), "HospitalOperativeNotes");
		}
	}
}
