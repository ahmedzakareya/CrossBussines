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
	// HOSPITAL H169: dental sterilisation release. What must never happen:
	//   · a kit used from a cycle nobody released ("not failed" is not "released");
	//   · a release with no physical record of the run, or on a run that did not meet its programme;
	//   · an implant load released before its biological indicator passed;
	//   · a kit from a pack not checked intact;
	//   · a failure found after release that leaves the patients it reached unlisted;
	//   · a recall follow-up closed before the patient was told, or with no outcome.
	// ==========================================================================================
	public class HospitalSterilReleaseTests
	{
		private static BusinessContext Ctx(int employeeId = 7) => new() { CompanyId = 1, EmployeeId = employeeId, UserId = "u" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };
		private static readonly DateTime Now = new(2026, 11, 17, 9, 0, 0);

		private static HospitalSterilCycle C(bool? physical = true, string chem = HospitalIndicator.Pass, string bio = HospitalIndicator.Pending, bool implant = false) =>
			new() { PhysicalOk = physical, Chemical = chem, Biological = bio, ImplantLoad = implant };

		[Fact]
		public void Release_rests_on_the_physical_record_the_chemical_and_for_an_implant_load_the_biological()
		{
			Assert.Equal("Record the physical readings of the run first", HospitalDentalPracticeRules.ReleaseRefusal(C(physical: null)));
			Assert.Equal("This cycle failed its checks: its kits are held", HospitalDentalPracticeRules.ReleaseRefusal(C(physical: false)));
			Assert.Equal("Release the cycle on its chemical indicator first", HospitalDentalPracticeRules.ReleaseRefusal(C(chem: HospitalIndicator.Pending)));
			Assert.Null(HospitalDentalPracticeRules.ReleaseRefusal(C()));   // parametric release of an ordinary load
			Assert.Equal("A load with implantable items waits for its biological indicator", HospitalDentalPracticeRules.ReleaseRefusal(C(implant: true)));
			Assert.Null(HospitalDentalPracticeRules.ReleaseRefusal(C(implant: true, bio: HospitalIndicator.Pass)));
			Assert.Equal("This cycle is already released", HospitalDentalPracticeRules.ReleaseRefusal(new HospitalSterilCycle { PhysicalOk = true, Chemical = HospitalIndicator.Pass, ReleasedAt = Now }));
		}

		[Theory]
		[InlineData(4, 134, 210, true, null)]
		[InlineData(null, 134, null, true, "Enter the exposure time in minutes")]
		[InlineData(4, 90, null, true, "Enter the sterilisation temperature (100–150 °C)")]
		[InlineData(4, 134, 900, true, "The pressure is out of range")]
		[InlineData(4, 134, null, null, "Say whether the run met its programme")]
		public void The_physical_record_is_bounded(int? minutes, int? temp, int? kpa, bool? ok, string? why) =>
			Assert.Equal(why, HospitalDentalPracticeRules.PhysicalRefusal(new HospitalSterilCycle(), minutes, temp, kpa, ok));

		[Fact]
		public void A_kit_needs_a_released_cycle_and_an_intact_pack()
		{
			Assert.Equal("This cycle is not released yet", HospitalDentalPracticeRules.UseRefusal(C()));
			var released = C(); released.ReleasedAt = Now;
			Assert.Null(HospitalDentalPracticeRules.UseRefusal(released));
			Assert.Equal("A pack that is not intact is not used: send it back to be re-sterilised", HospitalDentalPracticeRules.UseRefusal(released, packIntact: false));
			released.RecalledAt = Now;
			Assert.NotNull(HospitalDentalPracticeRules.UseRefusal(released));
		}

		private static async Task<(PlatformTestHost Host, HospitalDentalPracticeService Svc, int Patient)> SetupAsync()
		{
			var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var svc = new HospitalDentalPracticeService(host.Db, access, null!, null!);
			var p = (await new HospitalPatientService(host.Db, access).SaveAsync(Ctx(), new HospitalPatientInput { Name = "م سلمى", NameEn = "Salma", Sex = "F" }, 7, DateTime.UtcNow)).Id!.Value;
			return (host, svc, p);
		}

		[Fact]
		public async Task An_implant_load_is_used_only_after_physical_chemical_biological_and_a_person_releasing_it()
		{
			var (host, svc, p) = await SetupAsync();
			using var _ = host;
			int id = (await svc.CreateCycleAsync(Ctx(), new HospitalSterilCycleInput { Unit = "Autoclave 1", Load = "Implant kit", ImplantLoad = true }, 7, Now, Now)).Id!.Value;
			Assert.Equal("This cycle is not released yet", (await svc.UseKitAsync(Ctx(), id, p, null, true, 7, Now)).Error);
			Assert.Equal("Record the physical readings of the run first", (await svc.ReleaseAsync(Ctx(), id, 7, Now)).Error);
			Assert.True((await svc.RecordPhysicalAsync(Ctx(), id, new HospitalSterilPhysicalInput { ExposureMinutes = 4, TemperatureC = 134, PressureKpa = 210, Ok = true })).Ok);
			Assert.Equal("The physical readings of this cycle are already recorded", (await svc.RecordPhysicalAsync(Ctx(), id, new HospitalSterilPhysicalInput { ExposureMinutes = 4, TemperatureC = 134, Ok = false })).Error);
			Assert.True((await svc.RecordIndicatorAsync(Ctx(), id, "Chemical", HospitalIndicator.Pass, Now)).Ok);
			Assert.Equal("A load with implantable items waits for its biological indicator", (await svc.ReleaseAsync(Ctx(), id, 7, Now)).Error);
			Assert.True((await svc.RecordIndicatorAsync(Ctx(), id, "Biological", HospitalIndicator.Pass, Now)).Ok);
			Assert.True((await svc.ReleaseAsync(Ctx(), id, 9, Now)).Ok);
			Assert.Equal("A pack that is not intact is not used: send it back to be re-sterilised", (await svc.UseKitAsync(Ctx(), id, p, null, false, 7, Now)).Error);
			Assert.True((await svc.UseKitAsync(Ctx(), id, p, null, true, 7, Now)).Ok);
			var c = await host.Db.Set<HospitalSterilCycle>().AsNoTracking().SingleAsync();
			Assert.Equal((9, true, 134m), (c.ReleasedByEmployeeId!.Value, c.ImplantLoad, c.TemperatureC!.Value));
			Assert.True((await host.Db.Set<HospitalSterilUse>().AsNoTracking().SingleAsync()).PackIntact);
		}

		[Fact]
		public async Task A_failure_after_release_recalls_the_cycle_and_lists_every_patient_until_told_and_closed()
		{
			var (host, svc, p) = await SetupAsync();
			using var _ = host;
			int id = (await svc.CreateCycleAsync(Ctx(), new HospitalSterilCycleInput { Unit = "Autoclave 1", Load = "Exam kits" }, 7, Now, Now)).Id!.Value;
			await svc.RecordPhysicalAsync(Ctx(), id, new HospitalSterilPhysicalInput { ExposureMinutes = 4, TemperatureC = 134, Ok = true });
			await svc.RecordIndicatorAsync(Ctx(), id, "Chemical", HospitalIndicator.Pass, Now);
			Assert.True((await svc.ReleaseAsync(Ctx(), id, 7, Now)).Ok);   // parametric release: biological still incubating
			Assert.True((await svc.UseKitAsync(Ctx(), id, p, null, true, 7, Now)).Ok);
			Assert.True((await svc.UseKitAsync(Ctx(), id, p, null, true, 7, Now.AddHours(1))).Ok);

			Assert.True((await svc.RecordIndicatorAsync(Ctx(), id, "Biological", HospitalIndicator.Fail, Now.AddDays(1))).Ok);
			var c = await host.Db.Set<HospitalSterilCycle>().AsNoTracking().SingleAsync();
			Assert.Equal("Biological indicator failed after release", c.RecallReason);
			Assert.NotNull(c.RecalledAt);
			Assert.Equal("This cycle failed its checks or was recalled: its kits are held", (await svc.UseKitAsync(Ctx(), id, p, null, true, 7, Now.AddDays(1))).Error);

			var follow = await svc.SterilRecallsAsync(Ctx(), Now.AddDays(1));
			Assert.Equal(2, follow.Count);   // one per kit used, each with its use
			int r = follow[0].Recall.ID;
			Assert.Equal("Record that the patient was told first", (await svc.SterilRecallCloseAsync(Ctx(), r, "no infection", 7, Now.AddDays(2))).Error);
			Assert.True((await svc.SterilRecallNotifyAsync(Ctx(), r, 7, Now.AddDays(1))).Ok);
			Assert.Equal("Enter the outcome of the follow-up", (await svc.SterilRecallCloseAsync(Ctx(), r, " ", 7, Now.AddDays(2))).Error);
			Assert.True((await svc.SterilRecallCloseAsync(Ctx(), r, "no infection at 14 days", 7, Now.AddDays(2))).Ok);
			Assert.Equal("This follow-up is already closed", (await svc.SterilRecallCloseAsync(Ctx(), r, "again", 7, Now.AddDays(2))).Error);
		}

		[Fact]
		public async Task A_failure_before_release_holds_the_load_and_recalls_no_one()
		{
			var (host, svc, p) = await SetupAsync();
			using var _ = host;
			int id = (await svc.CreateCycleAsync(Ctx(), new HospitalSterilCycleInput { Unit = "Autoclave 1", Load = "Exam kits" }, 7, Now, Now)).Id!.Value;
			Assert.True((await svc.RecordIndicatorAsync(Ctx(), id, "Chemical", HospitalIndicator.Fail, Now)).Ok);
			Assert.Null((await host.Db.Set<HospitalSterilCycle>().AsNoTracking().SingleAsync()).RecalledAt);
			Assert.Empty(await host.Db.Set<HospitalSterilRecall>().AsNoTracking().ToListAsync());
			Assert.Equal("Enter the reason for the recall", (await svc.RecallCycleAsync(Ctx(), id, "", Now)).Error);
			Assert.True((await svc.RecallCycleAsync(Ctx(), id, "Wet packs found", Now)).Ok);   // by hand: held, with its reason
			Assert.Equal("This cycle was recalled", (await svc.RecallCycleAsync(Ctx(), id, "again", Now)).Error);
		}
	}
}
