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
	// HOSPITAL H179 — the signed dental session and the phase's informed consent. What must never happen:
	//   · treatment beyond diagnosis and prevention done under the price acceptance alone, with no informed consent;
	//   · completed work that no signed session records, or one session recording another patient's work;
	//   · a session without the anaesthetic and the instructions given.
	// ==========================================================================================
	public class HospitalDentalSessionTests
	{
		private static BusinessContext Ctx() => new() { CompanyId = 1, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		[Fact]
		public void The_rules()
		{
			Assert.False(HospitalDentalSessionRules.NeedsConsent(HospitalDentalCategory.Diagnostic));
			Assert.False(HospitalDentalSessionRules.NeedsConsent(HospitalDentalCategory.Preventive));
			Assert.True(HospitalDentalSessionRules.NeedsConsent(HospitalDentalCategory.Endodontic));
			var ok = new HospitalDentalSessionInput { LineIds = new() { 1 }, Anaesthetic = "none", PostOpInstructions = "no food 1 h" };
			Assert.Null(HospitalDentalSessionRules.Refusal(ok, 1));
			Assert.Equal("Choose the work done in this session", HospitalDentalSessionRules.Refusal(new HospitalDentalSessionInput(), 0));
			Assert.Equal("Only this patient's completed work not yet in a session is recorded", HospitalDentalSessionRules.Refusal(ok, 0));
			Assert.Equal("Write the anaesthetic given, or \"none\"", HospitalDentalSessionRules.Refusal(new HospitalDentalSessionInput { LineIds = new() { 1 }, PostOpInstructions = "x" }, 1));
			Assert.Equal("Write the instructions given to the patient", HospitalDentalSessionRules.Refusal(new HospitalDentalSessionInput { LineIds = new() { 1 }, Anaesthetic = "none" }, 1));
		}

		[Fact]
		public async Task Completed_work_is_recorded_once_in_a_signed_session_of_its_own_patient()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var svc = new HospitalDentalSessionService(host.Db, access);
			var pats = new HospitalPatientService(host.Db, access);
			int p1 = (await pats.SaveAsync(Ctx(), new HospitalPatientInput { Name = "م سلمى", NameEn = "Salma", Sex = "F" }, 7, DateTime.UtcNow)).Id!.Value;
			int p2 = (await pats.SaveAsync(Ctx(), new HospitalPatientInput { Name = "م عمر", NameEn = "Omar", Sex = "M" }, 7, DateTime.UtcNow)).Id!.Value;
			var svcRow = new HospitalService { CompanyId = 1, Code = "RCT", Name = "علاج عصب", NameEn = "Root canal", IsActive = true };
			host.Db.Add(svcRow); await host.Db.SaveChangesAsync();
			var plan1 = new HospitalDentalPlan { CompanyId = 1, PlanNo = "TP-1", PatientId = p1, CreatedAt = DateTime.UtcNow };
			var plan2 = new HospitalDentalPlan { CompanyId = 1, PlanNo = "TP-2", PatientId = p2, CreatedAt = DateTime.UtcNow };
			host.Db.AddRange(plan1, plan2); await host.Db.SaveChangesAsync();
			var l1 = new HospitalDentalPlanLine { CompanyId = 1, PlanId = plan1.ID, PhaseId = 1, Tooth = 36, ServiceId = svcRow.ID, Status = HospitalDentalLineStatus.Completed, CompletedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow };
			var l2 = new HospitalDentalPlanLine { CompanyId = 1, PlanId = plan2.ID, PhaseId = 2, Tooth = 11, ServiceId = svcRow.ID, Status = HospitalDentalLineStatus.Completed, CompletedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow };
			host.Db.AddRange(l1, l2); await host.Db.SaveChangesAsync();

			Assert.Equal(l1.ID, Assert.Single(await svc.UnrecordedAsync(Ctx(), p1)).LineId);
			var input = new HospitalDentalSessionInput { LineIds = new() { l1.ID, l2.ID }, Anaesthetic = "lidocaine 2%", AnaestheticAmount = "1.8 mL", PostOpInstructions = "analgesic as needed" };
			Assert.Equal("Only this patient's completed work not yet in a session is recorded", (await svc.SignAsync(Ctx(), p1, input, 7, DateTime.UtcNow)).Error);   // another patient's line
			input.LineIds = new() { l1.ID };
			Assert.True((await svc.SignAsync(Ctx(), p1, input, 7, DateTime.UtcNow)).Ok);
			Assert.Empty(await svc.UnrecordedAsync(Ctx(), p1));
			var s = Assert.Single(await svc.SessionsAsync(Ctx(), p1));
			Assert.Equal(("DS-000001", 7, 36), (s.Session.SessionNo, s.Session.SignedByEmployeeId, s.Lines.Single().Tooth!.Value));
			Assert.Equal("Only this patient's completed work not yet in a session is recorded", (await svc.SignAsync(Ctx(), p1, input, 7, DateTime.UtcNow)).Error);   // never twice
		}

		[Fact]
		public void A_phase_is_covered_only_by_a_given_signed_procedure_consent_not_withdrawn()
		{
			var all = new[]
			{
				new HospitalConsent { CompanyId = 1, DentalPhaseId = 5, Kind = HospitalConsentKind.Procedure, Decision = HospitalConsentDecision.Given, SignatureDocumentId = 1 },
				new HospitalConsent { CompanyId = 1, DentalPhaseId = 6, Kind = HospitalConsentKind.Procedure, Decision = HospitalConsentDecision.Given, SignatureDocumentId = 1, WithdrawnAt = DateTime.UtcNow },
				new HospitalConsent { CompanyId = 1, DentalPhaseId = 7, Kind = HospitalConsentKind.General, Decision = HospitalConsentDecision.Given, SignatureDocumentId = 1 },
				new HospitalConsent { CompanyId = 1, DentalPhaseId = 8, Kind = HospitalConsentKind.Procedure, Decision = HospitalConsentDecision.Refused, SignatureDocumentId = 1 },
			}.AsQueryable();
			Assert.True(HospitalDentalSessionRules.CoveringPhase(all, 1, 5).Any());
			Assert.False(HospitalDentalSessionRules.CoveringPhase(all, 1, 6).Any());   // withdrawn
			Assert.False(HospitalDentalSessionRules.CoveringPhase(all, 1, 7).Any());   // not a procedure consent
			Assert.False(HospitalDentalSessionRules.CoveringPhase(all, 1, 8).Any());   // refused
			Assert.False(HospitalDentalSessionRules.CoveringPhase(all, 2, 5).Any());   // another company
		}

		[Fact]
		public void The_table_matches_its_slice_and_moves_in_a_merge()
		{
			var model = HospitalSliceParity.Model(HospitalDentalSessionModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h179_dental_sessions.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalDentalSession), "HospitalDentalSessions");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalDentalSession), "HospitalDentalSessions");
			Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == typeof(HospitalDentalSession));
		}
	}
}
