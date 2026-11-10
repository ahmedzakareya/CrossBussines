using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H67 — the support desk, with no database: what each kind of job needs, the path Open → Taken → Done (or
	// Cancelled), who closes and who cancels, the response minutes, and the table against its slice.
	// ==========================================================================================
	public class HospitalSupportDeskTests
	{
		private static HospitalSupportJob J(string status, int? taker = null, int? requester = 3) => new() { Status = status, TakenByEmployeeId = taker, RequestedByEmployeeId = requester, RequestedAt = new DateTime(2026, 11, 8, 9, 0, 0) };

		[Fact]
		public void Each_kind_names_what_it_needs()
		{
			Assert.Null(HospitalSupportDeskRules.AskRefusal(new HospitalSupportJobInput { Kind = HospitalSupportKind.Cleaning, BedId = 4 }));
			Assert.Equal("Choose the room or bed to clean", HospitalSupportDeskRules.AskRefusal(new HospitalSupportJobInput { Kind = HospitalSupportKind.Cleaning }));
			Assert.Null(HospitalSupportDeskRules.AskRefusal(new HospitalSupportJobInput { Kind = HospitalSupportKind.Porter, FromPlace = "Ward 2", ToPlace = "Radiology" }));
			Assert.Equal("Write where from and where to", HospitalSupportDeskRules.AskRefusal(new HospitalSupportJobInput { Kind = HospitalSupportKind.Porter, FromPlace = "Ward 2" }));
			Assert.Equal("How many bags?", HospitalSupportDeskRules.AskRefusal(new HospitalSupportJobInput { Kind = HospitalSupportKind.Laundry, RoomId = 7 }));
			Assert.Equal("Choose where to collect from", HospitalSupportDeskRules.AskRefusal(new HospitalSupportJobInput { Kind = HospitalSupportKind.Laundry, Bags = 2 }));
			Assert.Equal("Choose the kind of job", HospitalSupportDeskRules.AskRefusal(new HospitalSupportJobInput { Kind = "Catering" }));
		}

		[Fact]
		public void Whoever_took_it_closes_it_and_the_requester_or_administrator_cancels()
		{
			Assert.Null(HospitalSupportDeskRules.MoveRefusal(J(HospitalSupportStatus.Open), HospitalSupportStatus.Taken, 9, false, null));
			Assert.Null(HospitalSupportDeskRules.MoveRefusal(J(HospitalSupportStatus.Taken, 9), HospitalSupportStatus.Done, 9, false, null));
			Assert.Equal("Only who took the job closes it", HospitalSupportDeskRules.MoveRefusal(J(HospitalSupportStatus.Taken, 9), HospitalSupportStatus.Done, 8, true, null));
			Assert.Equal("forbidden", HospitalSupportDeskRules.MoveRefusal(J(HospitalSupportStatus.Open), HospitalSupportStatus.Cancelled, 9, false, "x"));
			Assert.Equal("Write why it is cancelled", HospitalSupportDeskRules.MoveRefusal(J(HospitalSupportStatus.Open), HospitalSupportStatus.Cancelled, 3, true, " "));
			Assert.Equal("This job cannot take that step from where it is", HospitalSupportDeskRules.MoveRefusal(J(HospitalSupportStatus.Open), HospitalSupportStatus.Done, 9, true, null));
			Assert.Equal("This job cannot take that step from where it is", HospitalSupportDeskRules.MoveRefusal(J(HospitalSupportStatus.Done, 9), HospitalSupportStatus.Cancelled, 3, true, "x"));
		}

		[Fact]
		public void The_response_is_counted_from_the_request_to_the_taking()
		{
			var j = J(HospitalSupportStatus.Taken, 9); j.TakenAt = j.RequestedAt.AddMinutes(12.4);
			Assert.Equal(12, HospitalSupportDeskRules.ResponseMinutes(j));
			Assert.Null(HospitalSupportDeskRules.ResponseMinutes(J(HospitalSupportStatus.Open)));
		}

		[Fact]
		public void The_table_matches_its_slice_and_a_porter_job_moves_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h67_support_desk.sql");
			var m = HospitalSliceParity.Model(HospitalSupportJobModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalSupportJob), "HospitalSupportJobs");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalSupportJob), "HospitalSupportJobs");
			foreach (var x in HospitalSupportKind.All.Concat(HospitalSupportStatus.All)) Assert.Contains("'" + x + "'", s);
			Assert.Contains("'BedTurnover'", s);
			Assert.Equal(new[] { "HospitalPatients", "HospitalRooms" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalSupportJob));
		}
	}
}
