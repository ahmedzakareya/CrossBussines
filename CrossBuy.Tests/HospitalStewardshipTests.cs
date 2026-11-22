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
	// HOSPITAL H235 — antimicrobial stewardship. What must never happen: the first dose of a restricted antibiotic held
	// (sepsis cannot wait); an unapproved restricted one running past 24 h unnoticed; a course running past 72 h with no
	// time-out; a "stop" that leaves the order running; a rejection or a narrowing with no reason; a day counted twice.
	// ==========================================================================================
	public class HospitalStewardshipTests
	{
		private static readonly DateTime T0 = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);

		[Theory]
		[InlineData(true, 10, null, false, HospitalAbxState.AwaitingApproval)]
		[InlineData(true, 30, null, false, HospitalAbxState.ApprovalOverdue)]
		[InlineData(true, 30, HospitalAbxDecision.Rejected, false, HospitalAbxState.RejectedRunning)]
		[InlineData(true, 50, HospitalAbxDecision.Approved, false, HospitalAbxState.TimeOutDue)]
		[InlineData(false, 20, null, false, HospitalAbxState.Early)]
		[InlineData(false, 60, null, false, HospitalAbxState.TimeOutDue)]
		[InlineData(false, 80, null, false, HospitalAbxState.TimeOutOverdue)]
		[InlineData(false, 80, null, true, HospitalAbxState.Reviewed)]
		public void Where_an_antimicrobial_order_stands(bool restricted, int hours, string? approval, bool timedOut, string state) =>
			Assert.Equal(state, HospitalStewardshipRules.State(restricted, T0, T0.AddHours(hours), approval, timedOut));

		[Fact]
		public void A_decision_fits_its_kind_and_a_stop_says_why()
		{
			Assert.Null(HospitalStewardshipRules.ReviewRefusal(HospitalAbxReviewKind.TimeOut, HospitalAbxDecision.Continue, null));
			Assert.Equal("Write the reason", HospitalStewardshipRules.ReviewRefusal(HospitalAbxReviewKind.TimeOut, HospitalAbxDecision.Stop, " "));
			Assert.Equal("Write the reason", HospitalStewardshipRules.ReviewRefusal(HospitalAbxReviewKind.Approval, HospitalAbxDecision.Rejected, null));
			Assert.Equal("Choose the decision", HospitalStewardshipRules.ReviewRefusal(HospitalAbxReviewKind.Approval, HospitalAbxDecision.Stop, "x"));
		}

		[Fact]
		public void Days_of_therapy_count_each_calendar_day_once_inside_the_window()
		{
			var from = new DateTime(2026, 11, 1); var to = new DateTime(2026, 12, 1);
			Assert.Equal(3, HospitalStewardshipRules.DaysOfTherapy(new DateTime(2026, 11, 5, 22, 0, 0), new DateTime(2026, 11, 7, 6, 0, 0), from, to));
			Assert.Equal(2, HospitalStewardshipRules.DaysOfTherapy(new DateTime(2026, 10, 28), new DateTime(2026, 11, 2, 9, 0, 0), from, to));   // starts before the window
			Assert.Equal(30, HospitalStewardshipRules.DaysOfTherapy(new DateTime(2026, 10, 1), null, from, to));                           // still running
			Assert.Equal(0, HospitalStewardshipRules.DaysOfTherapy(new DateTime(2026, 9, 1), new DateTime(2026, 9, 5), from, to));
			Assert.Equal(250m, HospitalStewardshipRules.Per1000(5, 20));
			Assert.Null(HospitalStewardshipRules.Per1000(5, 0));
		}

		[Fact]
		public async Task The_team_approves_a_restricted_agent_and_a_stop_stops_the_order()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var ctx = new BusinessContext { CompanyId = 1, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };
			var now = DateTime.UtcNow;
			var pid = (await new HospitalPatientService(host.Db, access).SaveAsync(ctx, new HospitalPatientInput { Name = "م س", NameEn = "P S", Sex = "M" }, 7, now)).Id!.Value;
			host.Seed.HospitalAdmissions.Add(new HospitalAdmission { CompanyId = 1, PatientId = pid, AdmittedAt = now.AddDays(-4), CreatedAt = now.AddDays(-4) });
			host.Seed.Set<HospitalDrug>().AddRange(
				new HospitalDrug { CompanyId = 1, ItemId = 1, GenericName = "ميروبينيم", GenericNameEn = "Meropenem", IsAntimicrobial = true, FormularyStatus = HospitalFormularyStatus.Restricted, UpdatedAt = now },
				new HospitalDrug { CompanyId = 1, ItemId = 2, GenericName = "سيفترياكسون", GenericNameEn = "Ceftriaxone", IsAntimicrobial = true, UpdatedAt = now },
				new HospitalDrug { CompanyId = 1, ItemId = 3, GenericName = "باراسيتامول", GenericNameEn = "Paracetamol", UpdatedAt = now });
			await host.Seed.SaveChangesAsync();
			var adm = (await host.Db.HospitalAdmissions.AsNoTracking().SingleAsync()).ID;
			HospitalMedOrder O(string drug, int hoursAgo) => new() { CompanyId = 1, AdmissionId = adm, PatientId = pid, Drug = drug, Dose = "1 g", Route = "IV", Status = HospitalMedOrderStatus.Active, StartAt = now.AddHours(-hoursAgo), CreatedAt = now.AddHours(-hoursAgo) };
			host.Seed.Set<HospitalMedOrder>().AddRange(O("Meropenem 1 g", 30), O("Ceftriaxone 2 g", 80), O("Paracetamol 1 g", 80));
			await host.Seed.SaveChangesAsync();
			var orders = await host.Db.Set<HospitalMedOrder>().AsNoTracking().OrderBy(o => o.ID).ToListAsync();
			var svc = new HospitalStewardshipService(host.Db, access);

			var page = (await svc.PageAsync(ctx, null, null, 1, 20, now))!;
			Assert.Equal(2, page.Total);   // paracetamol is not an antimicrobial
			Assert.Equal(HospitalAbxState.ApprovalOverdue, page.Rows.Single(r => r.Order.ID == orders[0].ID).State);
			Assert.Equal(HospitalAbxState.TimeOutOverdue, page.Rows.Single(r => r.Order.ID == orders[1].ID).State);
			Assert.True(page.TotalPer1000 > 0);

			Assert.Equal("Only a restricted antimicrobial needs approval", (await svc.ReviewAsync(ctx, orders[1].ID, HospitalAbxReviewKind.Approval, HospitalAbxDecision.Approved, null, 7, now)).Error);
			Assert.True((await svc.ReviewAsync(ctx, orders[0].ID, HospitalAbxReviewKind.Approval, HospitalAbxDecision.Approved, "ESBL in urine", 7, now)).Ok);
			Assert.True((await svc.ReviewAsync(ctx, orders[1].ID, HospitalAbxReviewKind.TimeOut, HospitalAbxDecision.Stop, "Culture negative", 7, now)).Ok);
			Assert.Equal(HospitalMedOrderStatus.Stopped, (await host.Db.Set<HospitalMedOrder>().AsNoTracking().SingleAsync(o => o.ID == orders[1].ID)).Status);
			Assert.Equal("This medicine is not marked as an antimicrobial", (await svc.ReviewAsync(ctx, orders[2].ID, HospitalAbxReviewKind.TimeOut, HospitalAbxDecision.Continue, null, 7, now)).Error);

			var after = (await svc.PageAsync(ctx, null, null, 1, 20, now))!;
			Assert.Equal(HospitalAbxState.Early, Assert.Single(after.Rows).State);   // approved at 30 h: its time-out is not due yet
		}

		[Fact]
		public void The_table_matches_its_slice_and_follows_a_merged_patient()
		{
			var model = HospitalSliceParity.Model(HospitalStewardshipModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h235_stewardship.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalAbxReview), "HospitalAbxReviews");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalAbxReview), "HospitalAbxReviews");
			foreach (var d in HospitalAbxDecision.All.Concat(HospitalAbxReviewKind.All)) Assert.Contains("'" + d + "'", s);
			Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == typeof(HospitalAbxReview));
		}
	}
}
