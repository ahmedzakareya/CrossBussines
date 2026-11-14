using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H120 — a chemotherapy cycle's drugs go to the pharmacy, with no database: a drug is one key however it is
	// written; the units to prepare round UP (a vial is never shared); the line the pharmacist reads; the link's
	// refusals; the table against its slice.
	// ==========================================================================================
	public class HospitalChemoPharmacyTests
	{
		[Fact]
		public void A_drug_is_one_key_however_it_is_written()
		{
			Assert.Equal("DOXORUBICIN", HospitalChemoPharmacyRules.Key("  doxorubicin "));
			Assert.Equal("CYCLOPHOSPHAMIDE IV", HospitalChemoPharmacyRules.Key("Cyclophosphamide   iv"));
			Assert.Equal("", HospitalChemoPharmacyRules.Key(null));
		}

		[Fact]
		public void Units_round_up_a_vial_is_never_shared()
		{
			Assert.Equal(2m, HospitalChemoPharmacyRules.Units(88.3m, 50m));     // 88.3 mg from 50 mg vials: two
			Assert.Equal(1m, HospitalChemoPharmacyRules.Units(50m, 50m));       // exactly one
			Assert.Equal(1m, HospitalChemoPharmacyRules.Units(10m, 50m));       // a part vial is a whole vial
			Assert.Equal(1m, HospitalChemoPharmacyRules.Units(883.2m, 1000m));
			Assert.Equal(1m, HospitalChemoPharmacyRules.Units(5m, 0m));          // no strength: one, never a division by zero
			Assert.Equal(2m, HospitalChemoPharmacyRules.Units(100.0000001m, 50m * 1.0000001m));   // floating noise does not add a vial
		}

		[Fact]
		public void The_line_says_the_drug_the_dose_the_route_and_the_days()
		{
			Assert.Equal("Doxorubicin 88.3 mg IV · 1", HospitalChemoPharmacyRules.Line(" Doxorubicin ", 88.34m, "IV", "1"));
		}

		[Fact]
		public void A_link_must_name_a_medicine_and_its_strength()
		{
			Assert.Equal("Choose the drug", HospitalChemoPharmacyRules.LinkRefusal(new() { Drug = " ", ItemId = 1, MgPerUnit = 50 }, true));
			Assert.Equal("Choose a medicine the pharmacy stocks", HospitalChemoPharmacyRules.LinkRefusal(new() { Drug = "Doxorubicin", ItemId = 1, MgPerUnit = 50 }, false));
			Assert.Equal("Write how many mg one unit holds", HospitalChemoPharmacyRules.LinkRefusal(new() { Drug = "Doxorubicin", ItemId = 1, MgPerUnit = 0 }, true));
			Assert.Null(HospitalChemoPharmacyRules.LinkRefusal(new() { Drug = "Doxorubicin", ItemId = 1, MgPerUnit = 50 }, true));
			Assert.Null(HospitalChemoPharmacyRules.LinkRefusal(new() { Drug = "Doxorubicin", ItemId = null }, false));   // unlinking
		}

		[Fact]
		public void The_table_matches_its_slice()
		{
			var s = HospitalSliceParity.Slice("hospital_h120_chemo_pharmacy.sql");
			var m = HospitalSliceParity.Model(HospitalOncologyModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalChemoDrugItem), "HospitalChemoDrugItems");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalChemoDrugItem), "HospitalChemoDrugItems");
			Assert.Contains("ALTER TABLE dbo.HospitalChemoCycles ADD DispenseId int NULL", s);
			Assert.True(HospitalDispenseSource.Oncology.Length <= 12);   // the request's source column is nvarchar(12)
			// found live: the pharmacy's own slice limits the source — the new one must be allowed there, or a cycle fails to give
			foreach (var src in new[] { HospitalDispenseSource.Outpatient, HospitalDispenseSource.Inpatient, HospitalDispenseSource.Emergency, HospitalDispenseSource.Oncology })
				Assert.Contains("'" + src + "'", s[s.IndexOf("CK_HospitalDispenses_Source CHECK", StringComparison.Ordinal)..]);
		}
	}
}
