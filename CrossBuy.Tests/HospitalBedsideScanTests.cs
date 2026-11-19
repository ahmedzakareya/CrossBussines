using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H205 — bedside barcode verification. What must never happen: a dose given to the wrong patient, the wrong
	// medicine, an expired or recalled pack given, a code that cannot be read taken as a match, or a dose given without a
	// scan and without the reason — and the compliance figure must count an override against the ward.
	// ==========================================================================================
	public class HospitalBedsideScanTests
	{
		private static readonly DateTime Now = new(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx() => new() { CompanyId = 1, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private static string Gtin(string first13)
		{
			for (int c = 0; c <= 9; c++) if (HospitalGs1.CheckDigitOk(first13 + c)) return first13 + c;
			throw new InvalidOperationException();
		}
		private static readonly string Para = Gtin("0628701000100"), Amox = Gtin("0628701000200"), Other = Gtin("0628701000300");

		[Fact]
		public void The_wristband_is_the_file_number_and_compliance_counts_overrides_against_the_ward()
		{
			Assert.True(HospitalBedsideScanRules.WristbandMatches(" mrn-100008 ", "MRN-100008"));
			Assert.False(HospitalBedsideScanRules.WristbandMatches("MRN-100023", "MRN-100008"));
			Assert.False(HospitalBedsideScanRules.WristbandMatches("", "MRN-100008"));
			Assert.True(HospitalBedsideScanRules.Scanned("MRN-1", Para));
			Assert.False(HospitalBedsideScanRules.Scanned("MRN-1", " "));
			Assert.Equal(95.0, HospitalBedsideScanRules.Percent(95, 5));
			Assert.Equal(66.7, HospitalBedsideScanRules.Percent(2, 1));
			Assert.Null(HospitalBedsideScanRules.Percent(0, 0));
		}

		[Fact]
		public async Task Each_wrong_scan_stops_the_dose_and_the_right_one_passes()
		{
			using var host = new PlatformTestHost();
			host.Seed.ItemCategories.Add(new ItemCategory { ID = 61, CompanyID = 1, Code = "MED", Name = "أدوية", NameEn = "Medicines" });
			host.Seed.Items.Add(new Item { ID = 71, CompanyID = 1, ItemCode = "MED-PARA", Barcode = Para[1..], Name = "MED-PARA", NameEn = "MED-PARA", ItemCategoryId = 61, BaseUoMId = 1, IsActive = true, CreatedAt = Now });
			host.Seed.Items.Add(new Item { ID = 72, CompanyID = 1, ItemCode = "MED-AMOX", Barcode = "B72", Name = "MED-AMOX", NameEn = "MED-AMOX", ItemCategoryId = 61, BaseUoMId = 1, IsActive = true, CreatedAt = Now });
			await host.Seed.SaveChangesAsync();
			host.Db.ItemBarcodes.Add(new ItemBarcode { ItemId = 72, Barcode = Amox });   // a second barcode on the item
			await host.Db.SaveChangesAsync();
			var para = new HospitalMedOrder { ID = 1, PatientId = 8, ItemId = 71, Drug = "Paracetamol" };
			var amox = new HospitalMedOrder { ID = 2, PatientId = 8, ItemId = 72, Drug = "Amoxicillin" };
			var free = new HospitalMedOrder { ID = 3, PatientId = 8, ItemId = null, Drug = "Something typed" };
			async Task<string?> Check(HospitalMedOrder o, string wrist, string pack) => (await HospitalBedsideScanCheck.MedicationAsync(host.Db, Ctx(), o, "MRN-100008", wrist, pack, Now, default)).Refusal;

			Assert.Null(await Check(para, "MRN-100008", "(01)" + Para + "(17)271231(10)P1"));
			Assert.Null(await Check(para, "MRN-100008", Para[1..]));                                  // the plain retail barcode
			Assert.Null(await Check(amox, "MRN-100008", Amox));                                       // through the item's second barcode
			Assert.Equal(HospitalBedsideScanRules.WrongPatient, await Check(para, "MRN-100023", Para));
			Assert.Equal(HospitalBedsideScanRules.WrongMedicine, await Check(para, "MRN-100008", Amox));
			Assert.Equal(HospitalBedsideScanRules.WrongMedicine, await Check(para, "MRN-100008", Other));
			Assert.Equal(HospitalBedsideScanRules.Unreadable, await Check(para, "MRN-100008", "6287010001"));   // ten digits: no GTIN
			Assert.Equal(HospitalBedsideScanRules.Expired, await Check(para, "MRN-100008", "(01)" + Para + "(17)251031"));
			Assert.Equal(HospitalBedsideScanRules.NoItem, await Check(free, "MRN-100008", Para));
			host.Db.Add(new HospitalLotRecall { CompanyId = 1, RecallNo = "RC-1", ItemId = 71, BatchNo = "P1", RecallClass = HospitalLotRecallClass.I, Reason = "contamination", QuarantineWarehouseId = 5, OpenedAt = Now });
			await host.Db.SaveChangesAsync();
			Assert.Equal(HospitalBedsideScanRules.Recalled, await Check(para, "MRN-100008", "(01)" + Para + "(17)271231(10)P1"));
			Assert.Null(await Check(para, "MRN-100008", "(01)" + Para + "(17)271231(10)P2"));   // another batch is fine
		}

		[Fact]
		public void A_specimen_is_collected_only_with_the_patients_band_and_the_orders_label()
		{
			Assert.Null(HospitalBedsideScanRules.SpecimenRefusal("MRN-100008", "LAB-000016", "MRN-100008", " lab-000016"));
			Assert.Equal(HospitalBedsideScanRules.WrongPatient, HospitalBedsideScanRules.SpecimenRefusal("MRN-100008", "LAB-000016", "MRN-100023", "LAB-000016"));
			Assert.Equal(HospitalBedsideScanRules.WrongTube, HospitalBedsideScanRules.SpecimenRefusal("MRN-100008", "LAB-000016", "MRN-100008", "LAB-000017"));
			Assert.False(HospitalBedsideScanRules.Same("", "LAB-1"));
			// the label's QR carries the accession number it prints
			var qr = CrossBuy.BL.Reporting.HospitalDeskLayouts.SpecimenLabel().Bands.SelectMany(b => b.Elements).Single(e => e.Kind == CrossBuy.BL.Reporting.ReportElementKind.QrCode);
			Assert.Equal("DocumentNo", qr.FieldKey);
		}

		[Fact]
		public void A_bag_is_hung_only_on_its_patient_and_a_baby_handed_only_to_its_mother()
		{
			Assert.Null(HospitalBedsideScanRules.BloodRefusal("MRN-100008", "BB-2026-0041", "MRN-100008", "bb-2026-0041"));
			Assert.Equal(HospitalBedsideScanRules.WrongPatient, HospitalBedsideScanRules.BloodRefusal("MRN-100008", "BB-2026-0041", "MRN-100023", "BB-2026-0041"));
			Assert.Equal(HospitalBedsideScanRules.WrongBag, HospitalBedsideScanRules.BloodRefusal("MRN-100008", "BB-2026-0041", "MRN-100008", "BB-2026-0042"));
			Assert.Null(HospitalBedsideScanRules.BabyRefusal("MRN-100008", "MRN-100031", "MRN-100008", "MRN-100031"));
			Assert.Equal(HospitalBedsideScanRules.WrongPatient, HospitalBedsideScanRules.BabyRefusal("MRN-100008", "MRN-100031", "MRN-100009", "MRN-100031"));
			Assert.Equal(HospitalBedsideScanRules.WrongBaby, HospitalBedsideScanRules.BabyRefusal("MRN-100008", "MRN-100031", "MRN-100008", "MRN-100032"));
		}

		[Fact]
		public void The_table_matches_its_slice()
		{
			var model = HospitalSliceParity.Model(HospitalBedsideScanModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h205_bedside_scans.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalBedsideScan), "HospitalBedsideScans");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalBedsideScan), "HospitalBedsideScans");
			foreach (var x in HospitalBedsideScanKind.All.Concat(HospitalBedsideScanOutcome.All)) Assert.Contains("'" + x + "'", s);
		}
	}
}
