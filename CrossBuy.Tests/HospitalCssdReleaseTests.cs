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
	// HOSPITAL H170: central sterile supply under the dental release policy (H169). What must never happen:
	//   · a set sterile straight off the autoclave, with nobody releasing its cycle;
	//   · a passed cycle with no physical record of its run;
	//   · an implantable load released before its biological indicator passed;
	//   · a set issued from a pack not checked intact;
	//   · a failure after release that leaves the cases a set reached unlisted, or recalled sets left on the shelf.
	// ==========================================================================================
	public class HospitalCssdReleaseTests
	{
		private static BusinessContext Ctx(int employeeId = 7) => new() { CompanyId = 1, EmployeeId = employeeId, UserId = "u" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };
		private static readonly DateTime Now = new(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc);
		private static readonly HospitalCycleEndRecord Met = new() { Passed = true, ExposureMinutes = 4, TemperatureC = 134, PressureKpa = 210 };

		[Fact]
		public void A_passed_end_needs_its_physical_record_and_a_failed_one_its_reason()
		{
			Assert.Null(HospitalCssdRules.EndRefusal(Met));
			Assert.Equal("Enter the exposure time in minutes", HospitalCssdRules.EndRefusal(new HospitalCycleEndRecord { Passed = true, TemperatureC = 134 }));
			Assert.Equal("Enter the sterilisation temperature (100–150 °C)", HospitalCssdRules.EndRefusal(new HospitalCycleEndRecord { Passed = true, ExposureMinutes = 4 }));
			Assert.Equal("Write why the cycle failed", HospitalCssdRules.EndRefusal(new HospitalCycleEndRecord { Passed = false }));
		}

		[Fact]
		public void Release_follows_the_dental_policy()
		{
			HospitalSterilisationCycle C(bool implant = false, string bio = HospitalIndicator.Pending) => new() { Result = HospitalCycleResult.Passed, Chemical = HospitalIndicator.Pass, Biological = bio, ImplantLoad = implant };
			Assert.Null(HospitalCssdRules.ReleaseRefusal(C()));
			Assert.Equal("A load with implantable items waits for its biological indicator", HospitalCssdRules.ReleaseRefusal(C(implant: true)));
			Assert.Null(HospitalCssdRules.ReleaseRefusal(C(implant: true, bio: HospitalIndicator.Pass)));
			Assert.Equal("This cycle is still running", HospitalCssdRules.ReleaseRefusal(new HospitalSterilisationCycle()));
			Assert.Equal("This cycle failed its checks: its kits are held", HospitalCssdRules.ReleaseRefusal(C(bio: HospitalIndicator.Fail)));
		}

		private static async Task<(PlatformTestHost Host, HospitalCssdService Svc, int Set, int Surgery)> SetupAsync()
		{
			var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var svc = new HospitalCssdService(host.Db, access);
			var p = (await new HospitalPatientService(host.Db, access).SaveAsync(Ctx(), new HospitalPatientInput { Name = "م سلمى", NameEn = "Salma", Sex = "F" }, 7, DateTime.UtcNow)).Id!.Value;
			var g = new HospitalSurgery { CompanyId = 1, CaseNo = "OR-0001", PatientId = p, RoomId = 1, ServiceId = 1, Code = "TKR", Name = "ر", NameEn = "Knee", SurgeonId = 1,
				CaseDate = HospitalNursingService.UtcToWard(Now).Date, StartMinute = 600, DurationMinutes = 90, Status = HospitalSurgeryStatus.Planned, CreatedAt = Now };
			host.Db.Add(g); await host.Db.SaveChangesAsync();
			int set = (await svc.SaveSetAsync(Ctx(), new HospitalSetInput { Code = "ORTHO-1", Name = "طقم عظام", ShelfLifeDays = 30 }, Now)).Id!.Value;
			Assert.True((await svc.StepAsync(Ctx(), set, HospitalSetEventKind.Washed, null, 7, Now)).Ok);
			return (host, svc, set, g.ID);
		}

		private static async Task<HospitalInstrumentSet> SetOf(PlatformTestHost h, int id) => await h.Db.Set<HospitalInstrumentSet>().AsNoTracking().SingleAsync(s => s.ID == id);

		[Fact]
		public async Task An_implant_load_waits_in_quarantine_for_its_biological_and_a_person()
		{
			var (host, svc, set, surgery) = await SetupAsync();
			using var _ = host;
			int cy = (await svc.StartCycleAsync(Ctx(), new[] { set }, "Autoclave 1", 7, Now, default, implantLoad: true)).Id!.Value;
			Assert.Equal("Enter the exposure time in minutes", (await svc.EndCycleAsync(Ctx(), cy, new HospitalCycleEndRecord { Passed = true }, 7, Now)).Error);
			Assert.True((await svc.EndCycleAsync(Ctx(), cy, Met, 7, Now)).Ok);
			var s = await SetOf(host, set);
			Assert.Equal((HospitalSetStatus.Quarantine, (DateTime?)null), (s.Status, s.SterileUntil));
			Assert.Equal("This set cannot take that step from where it is", (await svc.StepAsync(Ctx(), set, HospitalSetEventKind.Issued, surgery, 7, Now, default, packIntact: true)).Error);
			Assert.Equal("A load with implantable items waits for its biological indicator", (await svc.ReleaseAsync(Ctx(), cy, 9, Now)).Error);
			Assert.True((await svc.BiologicalAsync(Ctx(), cy, HospitalIndicator.Pass, 7, Now)).Ok);
			Assert.True((await svc.ReleaseAsync(Ctx(), cy, 9, Now)).Ok);
			s = await SetOf(host, set);
			Assert.Equal(HospitalSetStatus.Sterile, s.Status);
			Assert.NotNull(s.SterileUntil);
			Assert.Equal("A pack that is not intact is not used: send it back to be re-sterilised", (await svc.StepAsync(Ctx(), set, HospitalSetEventKind.Issued, surgery, 7, Now)).Error);
			Assert.True((await svc.StepAsync(Ctx(), set, HospitalSetEventKind.Issued, surgery, 7, Now, default, packIntact: true)).Ok);
			Assert.True((await host.Db.Set<HospitalSetEvent>().AsNoTracking().SingleAsync(e => e.Kind == HospitalSetEventKind.Issued)).PackIntact);
			Assert.Equal(9, (await host.Db.Set<HospitalSterilisationCycle>().AsNoTracking().SingleAsync()).ReleasedByEmployeeId);
		}

		[Fact]
		public async Task A_biological_failure_after_release_recalls_the_shelf_and_lists_the_case()
		{
			var (host, svc, set, surgery) = await SetupAsync();
			using var _ = host;
			int other = (await svc.SaveSetAsync(Ctx(), new HospitalSetInput { Code = "BASIC-1", Name = "طقم أساسي", ShelfLifeDays = 30 }, Now)).Id!.Value;
			await svc.StepAsync(Ctx(), other, HospitalSetEventKind.Washed, null, 7, Now);
			int cy = (await svc.StartCycleAsync(Ctx(), new[] { set, other }, "Autoclave 1", 7, Now)).Id!.Value;
			await svc.EndCycleAsync(Ctx(), cy, Met, 7, Now);
			Assert.True((await svc.ReleaseAsync(Ctx(), cy, 7, Now)).Ok);   // an ordinary load: parametric release
			Assert.True((await svc.StepAsync(Ctx(), set, HospitalSetEventKind.Issued, surgery, 7, Now, default, packIntact: true)).Ok);

			Assert.True((await svc.BiologicalAsync(Ctx(), cy, HospitalIndicator.Fail, 7, Now.AddDays(1))).Ok);
			var c = await host.Db.Set<HospitalSterilisationCycle>().AsNoTracking().SingleAsync();
			Assert.Equal("Biological indicator failed after release", c.RecallReason);
			Assert.Equal(HospitalSetStatus.AwaitingWash, (await SetOf(host, other)).Status);   // still on the shelf: back to be washed
			Assert.Equal(HospitalSetStatus.InUse, (await SetOf(host, set)).Status);            // in theatre: the case is followed up
			var follow = await svc.RecallsAsync(Ctx(), Now.AddDays(1));
			Assert.Equal(("OR-0001", "ORTHO-1"), (follow.Single().CaseNo, follow.Single().SetCode));
			int r = follow.Single().Recall.ID;
			Assert.Equal("Record that the patient was told first", (await svc.RecallCloseAsync(Ctx(), r, "fine", 7, Now.AddDays(2))).Error);
			Assert.True((await svc.RecallNotifyAsync(Ctx(), r, 7, Now.AddDays(1))).Ok);
			Assert.True((await svc.RecallCloseAsync(Ctx(), r, "no infection at 30 days", 7, Now.AddDays(2))).Ok);
		}

		[Fact]
		public async Task A_biological_failure_before_release_sends_the_sets_back_and_recalls_no_one()
		{
			var (host, svc, set, _s) = await SetupAsync();
			using var _ = host;
			int cy = (await svc.StartCycleAsync(Ctx(), new[] { set }, "Autoclave 1", 7, Now)).Id!.Value;
			await svc.EndCycleAsync(Ctx(), cy, Met, 7, Now);
			Assert.True((await svc.BiologicalAsync(Ctx(), cy, HospitalIndicator.Fail, 7, Now)).Ok);
			Assert.Equal(HospitalSetStatus.Packed, (await SetOf(host, set)).Status);
			Assert.Null((await host.Db.Set<HospitalSterilisationCycle>().AsNoTracking().SingleAsync()).RecalledAt);
			Assert.Equal("This cycle failed its checks: its kits are held", (await svc.ReleaseAsync(Ctx(), cy, 7, Now)).Error);
			Assert.Empty(await host.Db.Set<HospitalCssdRecall>().AsNoTracking().ToListAsync());
		}

		[Fact]
		public async Task A_recall_by_hand_needs_a_reason_and_takes_the_shelf_back()
		{
			var (host, svc, set, _s) = await SetupAsync();
			using var _ = host;
			int cy = (await svc.StartCycleAsync(Ctx(), new[] { set }, "Autoclave 1", 7, Now)).Id!.Value;
			await svc.EndCycleAsync(Ctx(), cy, Met, 7, Now);
			await svc.ReleaseAsync(Ctx(), cy, 7, Now);
			Assert.Equal("Enter the reason for the recall", (await svc.RecallAsync(Ctx(), cy, " ", 7, Now)).Error);
			Assert.True((await svc.RecallAsync(Ctx(), cy, "Wet packs found", 7, Now)).Ok);
			Assert.Equal(HospitalSetStatus.AwaitingWash, (await SetOf(host, set)).Status);
			Assert.Equal("This cycle was recalled", (await svc.RecallAsync(Ctx(), cy, "again", 7, Now)).Error);
		}
	}
}
