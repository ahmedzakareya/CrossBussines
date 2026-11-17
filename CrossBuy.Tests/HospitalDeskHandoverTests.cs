using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H165: the desk's shift handover. What must never happen:
	//   · a shift handed over with its cash drawer still open (uncounted);
	//   · work handed to oneself or to someone outside the company;
	//   · a colleague's items moved by my handover, or mine left behind;
	//   · a handover acknowledged by anyone but the receiver, or twice.
	// ==========================================================================================
	public class HospitalDeskHandoverTests
	{
		private static readonly DateTime Today = new(2026, 11, 17);
		private static readonly DateTime Now = new(2026, 11, 17, 14, 0, 0, DateTimeKind.Utc);
		private static readonly DateTime DayStart = new(2026, 11, 16, 21, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int employeeId = 7, int companyId = 1) => new() { CompanyId = companyId, EmployeeId = employeeId, UserId = "u" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private static Employee Emp(int id, int companyId) => new()
		{
			ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id, EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
			Email = $"e{id}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S", UserId = "u" + id,
		};

		private static async Task<(PlatformTestHost Host, HospitalDeskHandoverService Svc)> World()
		{
			var host = new PlatformTestHost();
			host.Seed.Employee.AddRange(Emp(7, 1), Emp(8, 1), Emp(9, 1), Emp(50, 2)); await host.Seed.SaveChangesAsync();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			host.Db.AddRange(
				new HospitalDeskClaim { CompanyId = 1, Kind = "portal", ItemKey = "1", OwnerEmployeeId = 7, ClaimedAt = Now },
				new HospitalDeskClaim { CompanyId = 1, Kind = "intake", ItemKey = "4", OwnerEmployeeId = 7, ClaimedAt = Now },
				new HospitalDeskClaim { CompanyId = 1, Kind = "portal", ItemKey = "2", OwnerEmployeeId = 9, ClaimedAt = Now },   // a colleague's
				new HospitalCheckinGap { CompanyId = 1, PatientId = 1, Item = HospitalCheckinItem.Identity, Reason = "r", OwnerEmployeeId = 7, DueDate = Today.AddDays(-1), Status = HospitalCheckinGapStatus.Open, OpenedAt = Now },
				new HospitalCheckinGap { CompanyId = 1, PatientId = 1, Item = HospitalCheckinItem.Phone, Reason = "r", OwnerEmployeeId = 7, DueDate = Today, Status = HospitalCheckinGapStatus.Done, OpenedAt = Now });
			await host.Db.SaveChangesAsync();
			return (host, new HospitalDeskHandoverService(host.Db, access));
		}

		[Fact]
		public async Task What_I_hold_and_the_drawer_must_be_counted_first()
		{
			var (host, svc) = await World(); using var _h = host;
			var h = await svc.HoldingsAsync(Ctx(), 7, DayStart, Today, Now);
			Assert.Equal((2, 1, 1), (h.QueueItems, h.Gaps, h.Overdue));   // the finished gap is not held; yesterday's due date is late
			host.Db.Add(new HospitalCashierShift { CompanyId = 1, Desk = "OPD", CashierEmployeeId = 7, Status = HospitalShiftStatus.Open, OpenedAt = Now.AddHours(-6) });
			await host.Db.SaveChangesAsync();
			Assert.Equal("Close your cash shift at the cashier first: count the drawer, then hand over", (await svc.HandOverAsync(Ctx(), 7, 8, null, DayStart, Today, Now)).Error);
			Assert.Equal("Hand over to a colleague, not to yourself", (await svc.HandOverAsync(Ctx(), 7, 7, null, DayStart, Today, Now)).Error);
			Assert.Equal("Choose someone from this company", (await svc.HandOverAsync(Ctx(), 7, 50, null, DayStart, Today, Now)).Error);
		}

		[Fact]
		public async Task Handing_over_moves_my_work_only_and_carries_the_counted_cash()
		{
			var (host, svc) = await World(); using var _h = host;
			host.Db.Add(new HospitalCashierShift { CompanyId = 1, Desk = "OPD", CashierEmployeeId = 7, Status = HospitalShiftStatus.Closed, OpenedAt = Now.AddHours(-6), ClosedAt = Now.AddMinutes(-5),
				ExpectedCash = 120.5m, CountedCash = 120m, Variance = -0.5m });
			await host.Db.SaveChangesAsync();
			var r = await svc.HandOverAsync(Ctx(), 7, 8, "two portal requests promised a call back", DayStart, Today, Now);
			Assert.True(r.Ok, r.Error);
			Assert.Equal(new[] { 8, 9, 8 }, await host.Db.Set<HospitalDeskClaim>().AsNoTracking().OrderBy(c => c.ItemKey).Select(c => c.OwnerEmployeeId!.Value).ToArrayAsync());
			Assert.Equal(8, (await host.Db.Set<HospitalCheckinGap>().AsNoTracking().SingleAsync(g => g.Status == HospitalCheckinGapStatus.Open)).OwnerEmployeeId);
			var row = await host.Db.Set<HospitalDeskHandover>().AsNoTracking().SingleAsync();
			Assert.Equal((2, 1, 1, -0.5m, "HV-000001"), (row.QueueItems, row.Gaps, row.Overdue, row.CashVariance!.Value, row.HandoverNo));

			Assert.Single((await svc.ForMeAsync(Ctx(8), 8)).Pending);
			Assert.Equal("Only the colleague who takes over acknowledges", (await svc.AcknowledgeAsync(Ctx(9), row.ID, 9, Now)).Error);
			Assert.True((await svc.AcknowledgeAsync(Ctx(8), row.ID, 8, Now)).Ok);
			Assert.Equal("This handover is acknowledged already", (await svc.AcknowledgeAsync(Ctx(8), row.ID, 8, Now)).Error);
			Assert.Empty((await svc.ForMeAsync(Ctx(8), 8)).Pending);
		}

		[Fact]
		public void The_table_is_read_in_the_shape_its_slice_creates()
		{
			var model = HospitalSliceParity.Model(HospitalPharmacyModel.Configure);
			HospitalSliceParity.NamesItsTable(model, HospitalSliceParity.Slice("hospital_h165_desk_handovers.sql"), typeof(HospitalDeskHandover), "HospitalDeskHandovers");
			HospitalSliceParity.ColumnsMatch(model, HospitalSliceParity.Slice("hospital_h165_desk_handovers.sql"), typeof(HospitalDeskHandover), "HospitalDeskHandovers");
		}
	}
}
