using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H180 — the dental chart on a past date, and the teeth an image shows. What must never happen:
	//   · the past chart showing work as done before it was done, or work planned after that date;
	//   · an image linked to a number that is not a tooth.
	// ==========================================================================================
	public class HospitalDentalHistoryTests
	{
		private static readonly DateTime Day = new(2026, 10, 1, 12, 0, 0);

		[Fact]
		public void A_mark_completed_later_shows_as_it_was()
		{
			var marks = new[]
			{
				new HospitalDentalFinding { ID = 1, Tooth = 36, Status = HospitalDentalMark.Completed, PlanLineId = 9, RecordedAt = Day.AddDays(-10), CompletedAt = Day.AddDays(5) },
				new HospitalDentalFinding { ID = 2, Tooth = 11, Status = HospitalDentalMark.Completed, RecordedAt = Day.AddDays(-10), CompletedAt = Day.AddDays(-1) },
				new HospitalDentalFinding { ID = 3, Tooth = 46, Status = HospitalDentalMark.Condition, RecordedAt = Day.AddDays(-3) },
			};
			var then = HospitalDentalRules.AsOf(marks, Day);
			Assert.Equal(HospitalDentalMark.Planned, then.Single(m => m.ID == 1).Status);    // done after the date: still planned then
			Assert.Null(then.Single(m => m.ID == 1).CompletedAt);
			Assert.Equal(HospitalDentalMark.Completed, then.Single(m => m.ID == 2).Status);  // done before it
			Assert.Equal(HospitalDentalMark.Condition, then.Single(m => m.ID == 3).Status);
		}

		[Fact]
		public void Plan_lines_as_they_stood()
		{
			var lines = new[]
			{
				new HospitalDentalPlanLine { ID = 1, Status = HospitalDentalLineStatus.Completed, CreatedAt = Day.AddDays(-5), CompletedAt = Day.AddDays(2) },
				new HospitalDentalPlanLine { ID = 2, Status = HospitalDentalLineStatus.Planned, CreatedAt = Day.AddDays(3) },
				new HospitalDentalPlanLine { ID = 3, Status = HospitalDentalLineStatus.Completed, CreatedAt = Day.AddDays(-5), CompletedAt = Day.AddDays(-1) },
			};
			var then = HospitalDentalRules.LinesAsOf(lines, Day);
			Assert.Equal(new[] { 1, 3 }, then.Select(l => l.ID));   // line 2 did not exist yet
			Assert.Equal(HospitalDentalLineStatus.Planned, then.Single(l => l.ID == 1).Status);
			Assert.Equal(HospitalDentalLineStatus.Completed, then.Single(l => l.ID == 3).Status);
		}

		[Theory]
		[InlineData("16, 17,16", "16,17", null)]
		[InlineData("17 ، 55", "17,55", null)]
		[InlineData(" ", null, null)]
		[InlineData("19", null, "Write the teeth as FDI numbers, e.g. 16, 17")]
		[InlineData("abc", null, "Write the teeth as FDI numbers, e.g. 16, 17")]
		public void Teeth_are_fdi_numbers(string text, string? teeth, string? error) =>
			Assert.Equal((teeth, error), HospitalDentalRules.ParseTeeth(text));

		[Fact]
		public void The_column_is_in_its_slice()
		{
			var s = HospitalSliceParity.Slice("hospital_h180_dental_image_teeth.sql");
			Assert.Contains("ADD Teeth nvarchar(60) NULL", s);
			Assert.Equal(typeof(string), typeof(HospitalRadOrder).GetProperty("Teeth")!.PropertyType);
		}
	}
}
