using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H173 — KDIGO chronicity and the eGFR window. What must never happen:
	//   · one abnormal value called chronic kidney disease (KDIGO: the abnormality lasts more than three months);
	//   · a normal cell (G1–G2 with A1) treated as disease;
	//   · a dosing decision on an eGFR older than the window the clinical lead approved.
	// ==========================================================================================
	public class HospitalKdigoChronicityTests
	{
		private static readonly DateTime At = new(2026, 11, 17);

		[Theory]
		[InlineData("G3a", "A1", true)]
		[InlineData("G2", "A2", true)]
		[InlineData("G2", "A1", false)]
		[InlineData("G1", "A1", false)]
		public void A_cell_is_a_disease_marker_below_60_or_with_albuminuria(string g, string a, bool abnormal) =>
			Assert.Equal(abnormal, HospitalClinicalCalc.KdigoAbnormal(g, a));

		[Fact]
		public void Confirmed_only_by_an_abnormal_stage_ninety_days_before()
		{
			Assert.False(HospitalClinicalCalc.KdigoConfirmed(At, Array.Empty<(string, string, DateTime)>()));
			Assert.False(HospitalClinicalCalc.KdigoConfirmed(At, new[] { ("G3a", "A1", At.AddDays(-60)) }));   // too recent
			Assert.False(HospitalClinicalCalc.KdigoConfirmed(At, new[] { ("G2", "A1", At.AddDays(-200)) }));   // normal then
			Assert.True(HospitalClinicalCalc.KdigoConfirmed(At, new[] { ("G3b", "A1", At.AddDays(-95)) }));
		}

		[Fact]
		public async Task Dosing_uses_an_egfr_only_within_the_approved_window()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var ctx = new BusinessContext { CompanyId = 1, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };
			var p = (await new HospitalPatientService(host.Db, access).SaveAsync(ctx, new HospitalPatientInput { Name = "م سلمى", NameEn = "Salma", Sex = "F" }, 7, DateTime.UtcNow)).Id!.Value;
			var c = new HospitalConcept { CompanyId = 1, Code = "98979-8", CodeSystem = "LOINC", Name = "eGFR", NameEn = "eGFR", ValueType = HospitalConceptType.Number, IsActive = true };
			host.Db.Add(c); await host.Db.SaveChangesAsync();
			host.Db.Add(new HospitalClinicalObservation { CompanyId = 1, PatientId = p, ConceptId = c.ID, Value = 47, ObservedAt = At.AddDays(-120) });
			await host.Db.SaveChangesAsync();
			Assert.Null(await HospitalRenalRules.EgfrAsync(host.Db, 1, p, At, default));   // 120 days: older than the published 90
			var svc = new HospitalClinicalRuleService(host.Db, access);
			var def = HospitalClinicalRuleCatalog.Of(HospitalClinicalRuleCatalog.EgfrDosing)!;
			Assert.True((await svc.ApproveAsync(ctx, new HospitalRuleApproveInput { Code = def.Code, Version = def.Version, Values = new() { ["recencyDays"] = 180 } }, 7, DateTime.UtcNow)).Ok);
			Assert.Equal(47, (await HospitalRenalRules.EgfrAsync(host.Db, 1, p, At, default))!.Value);   // the clinic approved 180 days
		}
	}
}
