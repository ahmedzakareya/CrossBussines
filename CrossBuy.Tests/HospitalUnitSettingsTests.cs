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
	// HOSPITAL H123 — a treatment unit names its session service from the services list. What must never happen: a session
	// charged from a service nobody chose (or one of another company, or switched off); a price-keeper who must know a hidden
	// code; a unit that has no kit carrying one; a service saved over the old code being lost when the unit has none.
	// ==========================================================================================
	public class HospitalUnitSettingsTests
	{
		private const int CompanyOne = 1, CompanyTwo = 2;
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int companyId = CompanyOne) => new()
		{
			CompanyId = companyId, EmployeeId = 7, UserId = "user-7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
		};

		private static HospitalUnitSettingsService Make(PlatformTestHost host) =>
			new(host.Db, new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance));

		private static async Task SeedAsync(PlatformTestHost host)
		{
			host.Seed.HospitalServices.Add(new HospitalService { ID = 301, CompanyId = CompanyOne, Code = "DIAL-01", Name = "جلسة غسيل", NameEn = "Dialysis session", Category = HospitalServiceCategories.Procedure, Price = 120m, IsActive = true, CreatedAt = Now });
			host.Seed.HospitalServices.Add(new HospitalService { ID = 302, CompanyId = CompanyOne, Code = "HD-SESSION", Name = "قديم", NameEn = "Old code", Category = HospitalServiceCategories.Procedure, Price = 90m, IsActive = true, CreatedAt = Now });
			host.Seed.HospitalServices.Add(new HospitalService { ID = 303, CompanyId = CompanyOne, Code = "OFF", Name = "موقوف", NameEn = "Switched off", Category = HospitalServiceCategories.Procedure, Price = 1m, IsActive = false, CreatedAt = Now });
			host.Seed.HospitalServices.Add(new HospitalService { ID = 304, CompanyId = CompanyTwo, Code = "OTHER", Name = "أخرى", NameEn = "Other company", Category = HospitalServiceCategories.Procedure, Price = 1m, IsActive = true, CreatedAt = Now });
			await host.Seed.SaveChangesAsync();
		}

		[Fact]
		public async Task A_session_is_charged_as_the_service_its_unit_chose()
		{
			using var host = new PlatformTestHost(); await SeedAsync(host); var svc = Make(host);
			// before the unit chooses: a service already listed under the old code still counts; physiotherapy has none
			Assert.Equal(302, (await HospitalSessionCharges.ServiceForAsync(host.Db, Ctx(), HospitalSessionKinds.Dialysis, default))!.ID);
			Assert.Null(await HospitalSessionCharges.ServiceForAsync(host.Db, Ctx(), HospitalSessionKinds.Physio, default));

			Assert.True((await svc.SaveAsync(Ctx(), HospitalKitUnits.Dialysis, new HospitalUnitSettingsInput { ServiceId = 301 }, 7, Now)).Ok);
			Assert.Equal(301, (await HospitalSessionCharges.ServiceForAsync(host.Db, Ctx(), HospitalSessionKinds.Dialysis, default))!.ID);   // the choice wins over the code

			var view = await svc.ViewAsync(Ctx(), HospitalKitUnits.Dialysis);
			Assert.Equal((301, "DIAL-01", true), (view.ServiceId!.Value, view.Service!.Code, view.WithKit));
			Assert.DoesNotContain(view.Services, s => s.Id is 303 or 304);   // only this company's services that are on
		}

		[Theory]
		[InlineData(303)]   // switched off
		[InlineData(304)]   // another company's
		[InlineData(999)]   // none
		public async Task Only_this_companys_service_that_is_on_can_be_chosen(int serviceId)
		{
			using var host = new PlatformTestHost(); await SeedAsync(host);
			Assert.Equal("Choose the session's service from the services list", (await Make(host).SaveAsync(Ctx(), HospitalKitUnits.Chemo, new HospitalUnitSettingsInput { ServiceId = serviceId }, 7, Now)).Error);
		}

		[Fact]
		public async Task A_unit_without_a_kit_keeps_none_and_an_unknown_unit_is_refused()
		{
			using var host = new PlatformTestHost(); await SeedAsync(host); var svc = Make(host);
			host.Seed.Warehouses.Add(new CrossBuy.Models.Context.Inventory.Warehouse { ID = 5, CompanyID = CompanyOne, Code = "WH", Name = "م", NameEn = "S", IsActive = true });
			await host.Seed.SaveChangesAsync();
			Assert.True((await svc.SaveAsync(Ctx(), HospitalKitUnits.Physio, new HospitalUnitSettingsInput { ServiceId = 301, WarehouseId = 5 }, 7, Now)).Ok);
			var row = await host.Db.Set<HospitalUnitKit>().AsNoTracking().SingleAsync(k => k.Unit == HospitalKitUnits.Physio);
			Assert.Equal((301, (int?)null, (int?)null), (row.SessionServiceId!.Value, row.KitItemId, row.StoreWarehouseId));
			Assert.Equal("not_found", (await svc.SaveAsync(Ctx(), "Kitchen", new HospitalUnitSettingsInput(), 7, Now)).Error);
			Assert.Empty((await svc.ViewAsync(Ctx(), HospitalKitUnits.Physio)).Stores);
		}

		[Fact]
		public void Every_unit_is_a_session_kind_and_allowed_by_its_slice()
		{
			var s = HospitalSliceParity.Slice("hospital_h123_unit_settings.sql");
			foreach (var u in HospitalKitUnits.All) { Assert.Contains(u, HospitalSessionKinds.All); Assert.Contains("'" + u + "'", s); }
			Assert.Contains("ALTER TABLE dbo.HospitalUnitKits ADD SessionServiceId int NULL", s);
			Assert.Equal(new[] { "HospitalServices" }, HospitalSliceParity.CheckedPrerequisites(s));
		}
	}
}
