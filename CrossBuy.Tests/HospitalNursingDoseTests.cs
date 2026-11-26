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
	// HOSPITAL H271 — a dose outside the order's hours. The owner's walk-through ordered a morning medicine after the
	// morning: its first dose was tomorrow and there was no way to give it now. A scheduled order now gives a dose outside
	// its hours, but only as "given" and only with the reason; an as-needed order still takes no scheduled time.
	// ==========================================================================================
	public class HospitalNursingDoseTests
	{
		private static BusinessContext Ctx() => new() { CompanyId = 1, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		[Fact]
		public async Task A_scheduled_order_gives_a_dose_outside_its_hours_only_with_its_reason()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var now = DateTime.UtcNow;
			var pid = (await new HospitalPatientService(host.Db, access).SaveAsync(Ctx(), new HospitalPatientInput { Name = "م جرعة", NameEn = "Dose Test", Sex = "M" }, 7, now)).Id!.Value;
			host.Seed.HospitalAdmissions.Add(new HospitalAdmission { CompanyId = 1, PatientId = pid, AdmittedAt = now.AddDays(-1), CreatedAt = now.AddDays(-1) });
			await host.Seed.SaveChangesAsync();
			var adm = (await host.Db.HospitalAdmissions.AsNoTracking().SingleAsync()).ID;
			var svc = new HospitalNursingService(host.Db, access);
			var scheduled = await svc.AddOrderAsync(Ctx(), new HospitalMedOrderInput { AdmissionId = adm, Drug = "Omeprazole 20 mg", Dose = "20 mg", Route = HospitalMedRoute.Oral, Hours = "08" }, 7, now);
			var prn = await svc.AddOrderAsync(Ctx(), new HospitalMedOrderInput { AdmissionId = adm, Drug = "Paracetamol 500 mg", Dose = "500 mg", Route = HospitalMedRoute.Oral, AsNeeded = true }, 7, now);
			Assert.True(scheduled.Ok, scheduled.Error); Assert.True(prn.Ok, prn.Error);

			HospitalMarInput Now(int order, string? reason, string status = HospitalMarStatus.Given) => new() { OrderId = order, Status = status, Reason = reason, ScanOverride = "no scanner on the test bench" };
			Assert.Equal("A dose outside the order's hours is given with its reason", (await svc.RecordAsync(Ctx(), Now(scheduled.Id!.Value, null), 7, now)).Error);
			Assert.Equal("A dose outside the order's hours is given with its reason", (await svc.RecordAsync(Ctx(), Now(scheduled.Id!.Value, "patient vomited", HospitalMarStatus.Held), 7, now)).Error);
			var given = await svc.RecordAsync(Ctx(), Now(scheduled.Id!.Value, "first dose now, as the doctor ordered"), 7, now);
			Assert.True(given.Ok, given.Error);
			Assert.True((await svc.RecordAsync(Ctx(), Now(prn.Id!.Value, null), 7, now)).Ok);   // as needed: no reason asked
			Assert.Equal("An as-needed dose has no scheduled time",
				(await svc.RecordAsync(Ctx(), new HospitalMarInput { OrderId = prn.Id!.Value, DueAt = now, Status = HospitalMarStatus.Given, ScanOverride = "x" }, 7, now)).Error);
			Assert.Equal(2, await host.Db.Set<HospitalMarEntry>().CountAsync(m => m.DueAt == null && m.Status == HospitalMarStatus.Given));
		}
	}
}
