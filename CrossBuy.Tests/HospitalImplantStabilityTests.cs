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
	// HOSPITAL H168: implant stability by protocol. What must never happen:
	//   · one fixed ISQ deciding every loading (there is none in the code; the clinic's approved protocol decides);
	//   · a restoration with no measurement it rests on, or resting on one taken after it;
	//   · immediate loading judged on a measurement other than the one at placement;
	//   · a restoration below the protocol without a dentist's documented exception;
	//   · a protocol with no named source.
	// ==========================================================================================
	public class HospitalImplantStabilityTests
	{
		private static readonly DateTime Today = new(2026, 11, 17);
		private static BusinessContext Ctx(int employeeId = 7) => new() { CompanyId = 1, EmployeeId = employeeId, UserId = "u" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };
		private static HospitalImplantMeasure M(string moment, int? isq, int? torque, int daysAgo, int id = 1) => new() { ID = id, Moment = moment, Isq = isq, TorqueNcm = torque, MeasuredOn = Today.AddDays(-daysAgo) };
		private static readonly HospitalImplantProtocol Iti = new() { MinIsqImmediate = 70, MinIsqEarly = 65, MinIsqConventional = 60, MinTorqueImmediate = 35, Reference = "Clinic SOP v1" };

		[Fact]
		public void The_basis_is_the_right_moment_and_never_after_the_restoration()
		{
			var all = new[] { M(HospitalImplantMoment.Placement, 72, 40, 90, 1), M(HospitalImplantMoment.BeforeLoading, 58, null, 10, 2), M(HospitalImplantMoment.BeforeLoading, 66, null, 2, 3), M(HospitalImplantMoment.BeforeLoading, 80, null, -3, 4) };
			Assert.Equal(1, HospitalImplantStabilityRules.Basis(all, HospitalImplantLoading.Immediate, Today)!.ID);
			Assert.Equal(3, HospitalImplantStabilityRules.Basis(all, HospitalImplantLoading.Conventional, Today)!.ID);   // the latest before loading, not the future one
			Assert.Null(HospitalImplantStabilityRules.Basis(all.Take(1), HospitalImplantLoading.Early, Today));
		}

		[Theory]
		[InlineData(HospitalImplantLoading.Conventional, 62, null, false)]   // 60 for conventional
		[InlineData(HospitalImplantLoading.Early, 62, null, true)]           // 65 for early
		[InlineData(HospitalImplantLoading.Immediate, 75, 30, true)]          // ISQ fine, torque under 35
		[InlineData(HospitalImplantLoading.Immediate, 75, 40, false)]
		[InlineData(HospitalImplantLoading.Immediate, null, 40, true)]         // a threshold with no ISQ measured is not met
		public void The_clinics_protocol_decides_per_loading(string loading, int? isq, int? torque, bool below)
			=> Assert.Equal(below, HospitalImplantStabilityRules.BelowProtocol(Iti, loading, new HospitalImplantMeasure { Isq = isq, TorqueNcm = torque }));

		[Fact]
		public void Without_a_protocol_nothing_is_blocked_but_a_measurement_is_still_required()
		{
			Assert.False(HospitalImplantStabilityRules.BelowProtocol(null, HospitalImplantLoading.Early, new HospitalImplantMeasure { Isq = 40 }));
			Assert.Equal("Record the stability before loading first", HospitalImplantStabilityRules.RefuseRestoration(HospitalImplantLoading.Early, HospitalImplantRestoration.Definitive, null, null, null));
			Assert.Equal("Record the stability at placement first", HospitalImplantStabilityRules.RefuseRestoration(HospitalImplantLoading.Immediate, HospitalImplantRestoration.Provisional, null, null, null));
			Assert.Equal("Choose how the implant is loaded", HospitalImplantStabilityRules.RefuseRestoration(null, HospitalImplantRestoration.Definitive, new HospitalImplantMeasure(), null, null));
			Assert.Equal("Name the guideline or document the protocol follows", HospitalImplantStabilityRules.RefuseProtocol(70, 65, 60, 35, " "));
		}

		[Fact]
		public async Task Below_the_protocol_needs_the_dentists_exception_which_is_kept()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var svc = new HospitalDentalCasesService(host.Db, access, null!);
			var p = (await new HospitalPatientService(host.Db, access).SaveAsync(Ctx(), new HospitalPatientInput { Name = "م عمر", NameEn = "Omar", Sex = "M" }, 7, DateTime.UtcNow)).Id!.Value;
			var imp = new HospitalImplant { CompanyId = 1, ImplantNo = "IM-0001", PatientId = p, Tooth = 36, System = "Test", Diameter = 4, Length = 10, Stage = HospitalImplantStage.Planned, CreatedAt = DateTime.UtcNow };
			host.Db.Add(imp); await host.Db.SaveChangesAsync();
			Assert.True((await svc.MoveImplantAsync(Ctx(), imp.ID, new HospitalImplantMoveInput { To = HospitalImplantStage.Healing, Lot = "L-1", On = Today.AddDays(-100), Isq = 68, TorqueNcm = 40 }, Today, default, 7)).Ok);
			Assert.Equal(HospitalImplantMoment.Placement, (await host.Db.Set<HospitalImplantMeasure>().AsNoTracking().SingleAsync()).Moment);   // the placement reading is kept as a measurement
			Assert.True((await svc.SaveProtocolAsync(Ctx(), new HospitalImplantProtocolInput { MinIsqConventional = 65, Reference = "Clinic SOP v1" }, 7, DateTime.UtcNow)).Ok);
			Assert.True((await svc.AddMeasureAsync(Ctx(), imp.ID, new HospitalImplantMeasureInput { Moment = HospitalImplantMoment.BeforeLoading, Isq = 61, On = Today.AddDays(-1) }, 7, DateTime.UtcNow, Today)).Ok);

			var restore = new HospitalImplantMoveInput { To = HospitalImplantStage.Restored, On = Today, LoadingKind = HospitalImplantLoading.Conventional, Restoration = HospitalImplantRestoration.Definitive };
			Assert.Equal("Below the clinic's protocol for this loading: record why the dentist accepts the exception", (await svc.MoveImplantAsync(Ctx(), imp.ID, restore, Today, default, 7)).Error);
			restore.ExceptionReason = "dense bone, splinted to the neighbouring crown";
			Assert.True((await svc.MoveImplantAsync(Ctx(), imp.ID, restore, Today, default, 7)).Ok);
			var saved = await host.Db.Set<HospitalImplant>().AsNoTracking().SingleAsync();
			Assert.Equal((61, HospitalImplantLoading.Conventional, 7), (saved.Isq!.Value, saved.LoadingKind, saved.ExceptionByEmployeeId!.Value));
			Assert.Equal(7, (await svc.ProtocolAsync(Ctx()))!.ApprovedByEmployeeId);
		}

		[Theory]
		[InlineData(typeof(HospitalImplantMeasure), "HospitalImplantMeasures")]
		[InlineData(typeof(HospitalImplantProtocol), "HospitalImplantProtocols")]
		public void Each_table_is_read_in_the_shape_its_slice_creates(Type entity, string table)
		{
			var model = HospitalSliceParity.Model(HospitalImplantStabilityModel.Configure);
			HospitalSliceParity.NamesItsTable(model, HospitalSliceParity.Slice("hospital_h168_implant_stability.sql"), entity, table);
			HospitalSliceParity.ColumnsMatch(model, HospitalSliceParity.Slice("hospital_h168_implant_stability.sql"), entity, table);
		}
	}
}
