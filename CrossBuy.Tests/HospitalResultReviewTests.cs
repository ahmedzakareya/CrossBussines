using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H178 — the result follow-up inbox. What must never happen:
	//   · a finished result with no owner to answer for it, or one closed without saying what was done;
	//   · a critical result closed with "no action";
	//   · someone other than the owner (or the lead) closing or moving it;
	//   · a late order going unnoticed.
	// ==========================================================================================
	public class HospitalResultReviewTests
	{
		private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int employeeId) => new() { CompanyId = 1, EmployeeId = employeeId, UserId = "u" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		[Fact]
		public void Severity_time_to_review_and_turnaround()
		{
			Assert.Equal(HospitalResultReviewRules.Critical, HospitalResultReviewRules.LabSeverity(false, new[] { "N", "HH" }));
			Assert.Equal(HospitalResultReviewRules.Abnormal, HospitalResultReviewRules.LabSeverity(false, new[] { "N", "L" }));
			Assert.Equal(HospitalResultReviewRules.Normal, HospitalResultReviewRules.LabSeverity(false, new[] { "N" }));
			Assert.Equal(HospitalResultReviewRules.Abnormal, HospitalResultReviewRules.RadSeverity(null));   // an uncategorised report is not "normal"
			Assert.Equal(TimeSpan.FromHours(24), HospitalResultReviewRules.ReviewWithin(HospitalResultReviewRules.Critical));
			Assert.Equal(TimeSpan.FromHours(2), HospitalResultReviewRules.Turnaround(HospitalResultKind.Lab, HospitalLabPriority.Stat));
			Assert.Equal(TimeSpan.FromDays(7), HospitalResultReviewRules.Turnaround(HospitalResultKind.Rad, HospitalLabPriority.Routine));
		}

		[Fact]
		public void A_review_says_what_was_done()
		{
			Assert.Equal("Choose what was done about the result", HospitalResultReviewRules.ReviewRefusal(HospitalResultReviewRules.Normal, new HospitalResultReviewInput()));
			Assert.Null(HospitalResultReviewRules.ReviewRefusal(HospitalResultReviewRules.Normal, new HospitalResultReviewInput { Action = HospitalResultAction.NoAction }));
			Assert.Equal("A critical result needs more than \"no action\": say what was done", HospitalResultReviewRules.ReviewRefusal(HospitalResultReviewRules.Critical, new HospitalResultReviewInput { Action = HospitalResultAction.NoAction }));
			Assert.Equal("Write a note on what was done", HospitalResultReviewRules.ReviewRefusal(HospitalResultReviewRules.Abnormal, new HospitalResultReviewInput { Action = HospitalResultAction.ChangedTreatment }));
		}

		[Fact]
		public async Task A_result_stays_with_its_owner_until_reviewed_and_can_be_handed_over()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var svc = new HospitalResultReviewService(host.Db, access);
			var p = (await new HospitalPatientService(host.Db, access).SaveAsync(Ctx(7), new HospitalPatientInput { Name = "م سلمى", NameEn = "Salma", Sex = "F" }, 7, DateTime.UtcNow)).Id!.Value;
			host.Db.Add(new HospitalPractitioner { CompanyId = 1, EmployeeId = 9, Name = "د منى", NameEn = "Dr Mona", IsActive = true });
			var o = new HospitalLabOrder { CompanyId = 1, AccessionNo = "L-1", PatientId = p, ServiceId = 1, Code = "K", Name = "بوتاسيوم", NameEn = "Potassium", OrderedByEmployeeId = 7,
				Status = HospitalLabStatus.Verified, OrderedAt = Now.AddDays(-5), VerifiedAt = Now.AddDays(-4) };
			var late = new HospitalLabOrder { CompanyId = 1, AccessionNo = "L-2", PatientId = p, ServiceId = 1, Code = "CBC", Name = "صورة دم", NameEn = "CBC", OrderedByEmployeeId = 7,
				Status = HospitalLabStatus.Collected, Priority = HospitalLabPriority.Stat, OrderedAt = Now.AddHours(-3) };
			host.Db.AddRange(o, late); await host.Db.SaveChangesAsync();
			host.Db.Add(new HospitalLabResult { CompanyId = 1, LabOrderId = o.ID, AnalyteId = 1, Name = "ك", NameEn = "K", Value = 5.9m, Flag = HospitalLabFlag.High });
			await host.Db.SaveChangesAsync();

			var item = Assert.Single(await svc.ToReviewAsync(Ctx(7), 7, Now));
			Assert.Equal((HospitalResultReviewRules.Abnormal, true), (item.Severity, item.Overdue));   // 4 days > 72 h
			Assert.Equal(late.ID, Assert.Single(await svc.AwaitingAsync(Ctx(7), 7, Now)).OrderId);       // STAT, 3 h > 2 h
			Assert.Equal("Only the result's owner or the clinical lead reviews it", (await svc.ReviewAsync(Ctx(9), o.ID, new HospitalResultReviewInput { Action = HospitalResultAction.NoAction }, null, Now)).Error);   // no person (bootstrap-open makes every test user the lead)
			Assert.Equal("Say why it is handed over", (await svc.HandOverAsync(Ctx(7), o.ID, new HospitalResultHandInput { ToEmployeeId = 9 }, 7, Now)).Error);
			Assert.True((await svc.HandOverAsync(Ctx(7), o.ID, new HospitalResultHandInput { ToEmployeeId = 9, Reason = "on leave" }, 7, Now)).Ok);
			Assert.Empty(await svc.ToReviewAsync(Ctx(7), 7, Now));
			Assert.Single(await svc.ToReviewAsync(Ctx(9), 9, Now));
			Assert.True((await svc.ReviewAsync(Ctx(9), o.ID, new HospitalResultReviewInput { Action = HospitalResultAction.ChangedTreatment, Note = "stopped spironolactone" }, 9, Now)).Ok);
			Assert.Empty(await svc.ToReviewAsync(Ctx(9), null, Now));
			Assert.Equal("This result is already reviewed", (await svc.ReviewAsync(Ctx(9), o.ID, new HospitalResultReviewInput { Action = HospitalResultAction.NoAction }, 9, Now)).Error);
			var row = await host.Db.Set<HospitalResultReview>().AsNoTracking().SingleAsync();
			Assert.Equal((9, 7, "on leave"), (row.ReviewedByEmployeeId!.Value, row.HandedByEmployeeId!.Value, row.HandReason));
		}

		[Fact]
		public void The_table_matches_its_slice_and_moves_in_a_merge()
		{
			var model = HospitalSliceParity.Model(HospitalResultReviewModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h178_result_reviews.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalResultReview), "HospitalResultReviews");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalResultReview), "HospitalResultReviews");
			foreach (var x in HospitalResultKind.All.Concat(HospitalResultAction.All)) Assert.Contains("'" + x + "'", s);
			Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == typeof(HospitalResultReview));
		}
	}
}
