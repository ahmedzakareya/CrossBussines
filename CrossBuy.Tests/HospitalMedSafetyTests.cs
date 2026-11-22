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
	// HOSPITAL H234 — duplicate therapy, pregnancy / breastfeeding and the dose for the weight. What must never happen:
	// two NSAIDs of one subgroup running unnoticed; a medicine to avoid in pregnancy ordered for a pregnant woman
	// without a stop; a dose ten times the per-kg limit for a small adult passing; or a weight-dosed medicine checked
	// against no weight in silence.
	// ==========================================================================================
	public class HospitalMedSafetyTests
	{
		private static HospitalDrugFacts F(string en, string atc, string? preg = null, string? lact = null) => new("ع " + en, en, atc, preg, lact, false, HospitalFormularyStatus.Formulary);
		private static readonly HospitalDrugFacts[] Kb = { F("Ibuprofen", "M01AE01"), F("Naproxen", "M01AE02"), F("Diclofenac", "M01AB05"), F("Warfarin", "B01AA03", HospitalDrugRisk.Avoid, HospitalDrugRisk.Caution) };

		[Fact]
		public void The_same_therapeutic_subgroup_is_a_duplicate_and_a_neighbour_is_not()
		{
			Assert.Equal(new[] { "Naproxen 500 mg" }, HospitalMedSafetyRules.Duplicates("Ibuprofen 400 mg", new[] { "Naproxen 500 mg", "Diclofenac 50 mg", "Ibuprofen 200 mg" }, Kb));
			Assert.Empty(HospitalMedSafetyRules.Duplicates("Paracetamol 1 g", new[] { "Naproxen 500 mg" }, Kb));   // not in the master: nothing claimed
			Assert.Equal("M01AE", HospitalMedSafetyRules.Atc4("m01ae01"));
			Assert.Null(HospitalMedSafetyRules.Atc4("M01"));
		}

		[Fact]
		public void Breastfeeding_is_six_months_from_a_birth_and_the_weight_sets_the_dose()
		{
			var now = new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
			Assert.True(HospitalMedSafetyRules.Lactating(now.AddDays(-100), now));
			Assert.False(HospitalMedSafetyRules.Lactating(now.AddDays(-200), now));
			Assert.False(HospitalMedSafetyRules.Lactating(null, now));
			Assert.Equal((750m, true), HospitalMedSafetyRules.PerKg(1000m, 15m, 50m)!.Value);
			Assert.Equal((750m, false), HospitalMedSafetyRules.PerKg(500m, 15m, 50m)!.Value);
			Assert.Null(HospitalMedSafetyRules.PerKg(500m, 15m, null));
		}

		[Fact]
		public async Task The_order_check_stops_a_duplicate_and_a_pregnancy_risk_and_says_when_it_lacks_a_weight()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var ctx = new BusinessContext { CompanyId = 1, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };
			var now = DateTime.UtcNow;
			var pid = (await new HospitalPatientService(host.Db, access).SaveAsync(ctx, new HospitalPatientInput { Name = "م ح", NameEn = "P P", Sex = "F" }, 7, now)).Id!.Value;
			host.Seed.HospitalAdmissions.Add(new HospitalAdmission { CompanyId = 1, PatientId = pid, AdmittedAt = now.AddDays(-1), CreatedAt = now.AddDays(-1) });
			host.Seed.Set<HospitalDrug>().AddRange(
				new HospitalDrug { CompanyId = 1, ItemId = 1, GenericName = "إيبوبروفين", GenericNameEn = "Ibuprofen", Atc = "M01AE01", UpdatedAt = now },
				new HospitalDrug { CompanyId = 1, ItemId = 2, GenericName = "نابروكسين", GenericNameEn = "Naproxen", Atc = "M01AE02", UpdatedAt = now },
				new HospitalDrug { CompanyId = 1, ItemId = 3, GenericName = "وارفارين", GenericNameEn = "Warfarin", Atc = "B01AA03", PregnancyRisk = HospitalDrugRisk.Avoid, UpdatedAt = now });
			host.Seed.Set<HospitalPregnancy>().Add(new HospitalPregnancy { CompanyId = 1, PregnancyNo = "PRG-000001", PatientId = pid, Status = HospitalPregnancyStatus.Active });
			host.Seed.Set<HospitalDoseLimit>().Add(new HospitalDoseLimit { CompanyId = 1, Drug = "Vancomycin", Unit = HospitalDoseUnit.Mg, PerKgMg = 15m, EveryHours = 12, MaxSingle = 2000m, UpdatedAt = now });
			await host.Seed.SaveChangesAsync();
			var adm = (await host.Db.HospitalAdmissions.AsNoTracking().SingleAsync()).ID;
			var svc = new HospitalDrugSafetyService(host.Db, access);

			var dup = await svc.CheckOrderAsync(ctx, adm, "Naproxen 500 mg", "500 mg", "PO", "08,20", false, alsoRunning: new[] { "Ibuprofen 400 mg" });
			var d = Assert.Single(dup, a => a.Kind == HospitalAlertKind.Duplicate);
			Assert.True(d.Blocks);

			var preg = await svc.CheckOrderAsync(ctx, adm, "Warfarin 5 mg", "5 mg", "PO", "20", false);
			var p = Assert.Single(preg, a => a.Kind == HospitalAlertKind.Pregnancy);
			Assert.Equal((true, "{0} is to be avoided in pregnancy, and the patient is pregnant"), (p.Blocks, p.Key));

			var vanco = await svc.CheckOrderAsync(ctx, adm, "Vancomycin", "1500 mg", "IV", "08,20", false);
			Assert.Contains(vanco, a => a.Detail == "weight-unknown");   // never a silent skip
		}

		[Fact]
		public void The_slice_widens_the_override_kinds()
		{
			var s = HospitalSliceParity.Slice("hospital_h234_med_safety.sql");
			foreach (var k in new[] { HospitalAlertKind.Duplicate, HospitalAlertKind.Pregnancy, "PregnancyRisk", "LactationRisk", "IsAntimicrobial" }) Assert.Contains(k, s);
		}
	}
}
