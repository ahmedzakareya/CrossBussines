using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H64 — restricted files and break-glass, with no database: who opens a restricted file and on what ground,
	// the emergency reason, the review by someone else (an unjustified one says why), and the tables against their slice.
	// ==========================================================================================
	public class HospitalBreakGlassTests
	{
		[Fact]
		public void Who_opens_a_restricted_file()
		{
			Assert.Equal(HospitalFileAccess.Open, HospitalRestrictedRules.Decide(false, false, false, false));
			Assert.Equal(HospitalFileAccess.Privileged, HospitalRestrictedRules.Decide(true, true, false, false));
			Assert.Equal(HospitalFileAccess.CareTeam, HospitalRestrictedRules.Decide(true, false, true, false));
			Assert.Equal(HospitalFileAccess.BreakGlass, HospitalRestrictedRules.Decide(true, false, false, true));
			Assert.Equal(HospitalFileAccess.Denied, HospitalRestrictedRules.Decide(true, false, false, false));
			Assert.False(HospitalFileAccess.Allows(HospitalFileAccess.Denied));
			Assert.True(HospitalFileAccess.Allows(HospitalFileAccess.BreakGlass));
		}

		[Fact]
		public void Breaking_the_glass_needs_a_real_reason_and_a_reason_to_break_it()
		{
			Assert.Equal("This file is not restricted", HospitalRestrictedRules.BreakRefusal(HospitalFileAccess.Open, "the patient collapsed"));
			Assert.Equal("You may already open this file", HospitalRestrictedRules.BreakRefusal(HospitalFileAccess.CareTeam, "the patient collapsed"));
			Assert.Equal("Write the emergency that needs this file (at least {0} characters)", HospitalRestrictedRules.BreakRefusal(HospitalFileAccess.Denied, "urgent"));
			Assert.Null(HospitalRestrictedRules.BreakRefusal(HospitalFileAccess.Denied, "arrived unconscious, allergies needed"));
		}

		[Fact]
		public void Someone_else_reviews_and_an_unjustified_opening_says_why()
		{
			var g = new HospitalBreakGlass { EmployeeId = 7 };
			Assert.Equal("Someone else reviews your own opening", HospitalRestrictedRules.ReviewRefusal(g, 7, HospitalBreakGlassOutcome.Justified, null));
			Assert.Equal("Choose whether it was justified", HospitalRestrictedRules.ReviewRefusal(g, 9, "Maybe", null));
			Assert.Equal("Write why it was not justified", HospitalRestrictedRules.ReviewRefusal(g, 9, HospitalBreakGlassOutcome.Unjustified, " "));
			Assert.Null(HospitalRestrictedRules.ReviewRefusal(g, 9, HospitalBreakGlassOutcome.Justified, null));
			g.ReviewedAt = DateTime.UtcNow;
			Assert.Equal("This opening was already reviewed", HospitalRestrictedRules.ReviewRefusal(g, 9, HospitalBreakGlassOutcome.Justified, null));
		}

		[Fact]
		public void The_tables_match_their_slice_and_move_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h64_break_glass.sql");
			var m = HospitalSliceParity.Model(HospitalRestrictedAccessModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalRestrictedFile), "HospitalRestrictedFiles");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalRestrictedFile), "HospitalRestrictedFiles");
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalBreakGlass), "HospitalBreakGlass");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalBreakGlass), "HospitalBreakGlass");
			Assert.Contains("CREATE UNIQUE INDEX UX_HospitalRestrictedFiles_Live ON dbo.HospitalRestrictedFiles (CompanyId, PatientId) WHERE LiftedAt IS NULL;", s);
			Assert.Contains("ReviewedByEmployeeId <> EmployeeId", s);
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalRestrictedFile));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalBreakGlass));
		}
	}
}
