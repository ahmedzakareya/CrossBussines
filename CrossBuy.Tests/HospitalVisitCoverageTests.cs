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
	// HOSPITAL H162: insurance for this visit. What must never happen:
	//   · a visit read as insured when no policy covers its day, or recorded as insured under one that does not;
	//   · a phone or portal check recorded without the insurer's reference;
	//   · a service the contract needs approval for shown as fine before it is asked and approved;
	//   · the estimate ignoring the copay or its cap.
	// ==========================================================================================
	public class HospitalVisitCoverageTests
	{
		private static readonly DateTime Today = new(2026, 11, 15);
		private static readonly DateTime NowUtc = new(2026, 11, 15, 5, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int companyId = 1) => new() { CompanyId = companyId, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private sealed class NoLedger : IHospitalLedger
		{
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
				=> Task.FromResult<(bool, string?, int?)>((true, null, 1));
		}

		private sealed record World(PlatformTestHost Host, HospitalVisitCoverageService Svc, int Appt, int Policy, int Expired, int Mri);

		private static async Task<World> Make(decimal copayPct = 20, decimal cap = 0)
		{
			var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var op = new HospitalOutpatientService(host.Db, access);
			var fee = new HospitalService { CompanyId = 1, Code = "CONS-GEN", Name = "كشف", NameEn = "Consultation", Category = HospitalServiceCategories.Consultation, Price = 100, IsActive = true };
			var mri = new HospitalService { CompanyId = 1, Code = "MRI", Name = "رنين", NameEn = "MRI", Category = "Radiology", Price = 400, IsActive = true };
			host.Db.AddRange(fee, mri); await host.Db.SaveChangesAsync();
			var dr = (await op.SavePractitionerAsync(Ctx(), new HospitalPractitionerInput { Name = "د. علي", NameEn = "Dr Ali", WorkDays = new() { 0, 1 }, SessionStartMinute = 540, SessionEndMinute = 720, SlotMinutes = 15, Privileges = new() { "opd" } }, 7)).Id!.Value;
			var p = (await new HospitalPatientService(host.Db, access).SaveAsync(Ctx(), new HospitalPatientInput { Name = "م عمر", NameEn = "Omar", Sex = "M" }, 7, NowUtc)).Id!.Value;
			var payer = new HospitalPayer { CompanyId = 1, Code = "INS", Name = "تأمين", NameEn = "Insurer", CopayPct = copayPct, CopayCap = cap, PreauthCategories = "Radiology", ValidFrom = Today.AddYears(-1), IsActive = true };
			host.Db.Add(payer); await host.Db.SaveChangesAsync();
			var good = new HospitalPatientPolicy { CompanyId = 1, PatientId = p, PayerId = payer.ID, PolicyNo = "P-1", ValidFrom = Today.AddMonths(-1), IsActive = true, CreatedAt = NowUtc };
			var old = new HospitalPatientPolicy { CompanyId = 1, PatientId = p, PayerId = payer.ID, PolicyNo = "P-0", ValidFrom = Today.AddYears(-2), ValidTo = Today.AddDays(-1), IsActive = true, CreatedAt = NowUtc };
			host.Db.AddRange(good, old); await host.Db.SaveChangesAsync();
			var a = new HospitalAppointment { CompanyId = 1, PatientId = p, PractitionerId = dr, AppointmentDate = Today, StartMinute = 600, DurationMinutes = 15, VisitType = "New", Status = HospitalAppointmentStatus.Arrived, CreatedAt = NowUtc };
			host.Db.Add(a); await host.Db.SaveChangesAsync();
			var enc = new HospitalEncounter { CompanyId = 1, AppointmentId = a.ID, PatientId = p, PractitionerId = dr, Status = HospitalEncounterStatus.Draft, StartedAt = NowUtc, CreatedAt = NowUtc };
			host.Db.Add(enc); await host.Db.SaveChangesAsync();
			host.Db.Add(new HospitalEncounterOrder { CompanyId = 1, EncounterId = enc.ID, ServiceId = mri.ID, Code = "MRI", Name = "رنين", NameEn = "MRI", Category = "Radiology", Price = 400 });
			await host.Db.SaveChangesAsync();
			var pricing = new HospitalPricingService(host.Db, access);
			return new World(host, new HospitalVisitCoverageService(host.Db, access, pricing, new HospitalBillingService(host.Db, access, new NoLedger(), pricing)), a.ID, good.ID, old.ID, mri.ID);
		}

		[Theory]
		[InlineData("Maybe", HospitalCoverageSource.Card, 1, null, "Choose insured or self-pay")]
		[InlineData(HospitalCoverageDecision.Insured, "Fax", 1, null, "Choose how the cover was verified")]
		[InlineData(HospitalCoverageDecision.Insured, HospitalCoverageSource.Card, null, null, "Choose the policy that covers the visit")]
		[InlineData(HospitalCoverageDecision.Insured, HospitalCoverageSource.Phone, 1, " ", "Write the insurer's reference for the check")]
		[InlineData(HospitalCoverageDecision.SelfPay, HospitalCoverageSource.Patient, null, null, null)]
		public void A_verification_names_the_decision_and_how(string decision, string source, int? policy, string? reference, string? refusal)
			=> Assert.Equal(refusal, HospitalVisitCoverageRules.Refuse(new HospitalVisitCoverageInput { Decision = decision, Source = source, PolicyId = policy, Reference = reference }));

		[Fact]
		public async Task Before_verifying_the_step_is_verify_and_the_estimate_follows_the_copay()
		{
			var w = await Make(); using var _h = w.Host;
			var s = (await w.Svc.SheetAsync(Ctx(), w.Appt, Today, NowUtc))!;
			Assert.Equal(("P-1", "next:verify"), (s.Policy!.PolicyNo, s.NextStep));   // the policy covering the day, not the expired one
			Assert.Single(s.Policies);
			Assert.Equal(2, s.Services.Count);
			Assert.True(s.Services.Single(x => x.Code == "MRI").NeedsApproval);
			Assert.Equal((500m, 100m), (s.EstimatedTotal, s.EstimatedShare));   // 20% of 500
			Assert.Null(s.FinalShare);
		}

		[Fact]
		public async Task Verified_insured_the_approval_is_the_next_step_until_answered_and_a_refusal_is_the_patients()
		{
			var w = await Make(); using var _h = w.Host;
			Assert.Equal("This policy does not cover the visit's day", (await w.Svc.VerifyAsync(Ctx(), w.Appt, new HospitalVisitCoverageInput { Decision = HospitalCoverageDecision.Insured, PolicyId = w.Expired, Source = HospitalCoverageSource.Card }, 7, Today, NowUtc)).Error);
			var ok = await w.Svc.VerifyAsync(Ctx(), w.Appt, new HospitalVisitCoverageInput { Decision = HospitalCoverageDecision.Insured, PolicyId = w.Policy, Source = HospitalCoverageSource.Phone, Reference = "ELG-77" }, 7, Today, NowUtc);
			Assert.True(ok.Ok, ok.Error);
			var s = (await w.Svc.SheetAsync(Ctx(), w.Appt, Today, NowUtc))!;
			Assert.Equal(("next:request", "رنين", "ELG-77"), (s.NextStep, s.NextService, s.Latest!.Reference));
			Assert.Equal((500m, 100m), (s.Latest.EstimatedTotal, s.Latest.EstimatedShare));   // the estimate told is kept

			var pa = new HospitalPreauth { CompanyId = 1, RequestNo = "PA-1", PatientId = s.Appointment.PatientId, PayerId = s.Payer!.ID, PolicyId = w.Policy, ServiceId = w.Mri, Amount = 400, Status = HospitalPreauthStatus.Pending, RequestedAt = NowUtc };
			w.Host.Db.Add(pa); await w.Host.Db.SaveChangesAsync();
			Assert.Equal("next:await", (await w.Svc.SheetAsync(Ctx(), w.Appt, Today, NowUtc))!.NextStep);
			pa.Status = HospitalPreauthStatus.Rejected; await w.Host.Db.SaveChangesAsync();
			var r = (await w.Svc.SheetAsync(Ctx(), w.Appt, Today, NowUtc))!;
			Assert.Equal("next:refused", r.NextStep);
			Assert.Equal(420m, r.EstimatedShare);   // 20% of the consultation + the refused MRI in full
			pa.Status = HospitalPreauthStatus.Approved; await w.Host.Db.SaveChangesAsync();
			Assert.Equal("next:none", (await w.Svc.SheetAsync(Ctx(), w.Appt, Today, NowUtc))!.NextStep);
		}

		[Fact]
		public async Task Self_pay_is_the_whole_visit_and_the_cap_limits_the_insured_share()
		{
			var w = await Make(copayPct: 50, cap: 60); using var _h = w.Host;
			Assert.Equal(60m, (await w.Svc.SheetAsync(Ctx(), w.Appt, Today, NowUtc))!.EstimatedShare);   // 50% would be 250: the cap is 60
			Assert.True((await w.Svc.VerifyAsync(Ctx(), w.Appt, new HospitalVisitCoverageInput { Decision = HospitalCoverageDecision.SelfPay, Source = HospitalCoverageSource.Patient }, 7, Today, NowUtc)).Ok);
			var s = (await w.Svc.SheetAsync(Ctx(), w.Appt, Today, NowUtc))!;
			Assert.Equal((500m, 500m, (HospitalPayer?)null), (s.EstimatedTotal, s.EstimatedShare, s.Payer));
			Assert.False(s.Services.Single(x => x.Code == "MRI").NeedsApproval);   // no contract, no approval
			Assert.Null(await w.Svc.SheetAsync(Ctx(2), w.Appt, Today, NowUtc));
		}

		[Fact]
		public void The_table_is_read_in_the_shape_its_slice_creates()
		{
			var model = HospitalSliceParity.Model(HospitalPharmacyModel.Configure);
			HospitalSliceParity.NamesItsTable(model, HospitalSliceParity.Slice("hospital_h162_visit_coverage.sql"), typeof(HospitalVisitCoverage), "HospitalVisitCoverages");
			HospitalSliceParity.ColumnsMatch(model, HospitalSliceParity.Slice("hospital_h162_visit_coverage.sql"), typeof(HospitalVisitCoverage), "HospitalVisitCoverages");
		}
	}
}
