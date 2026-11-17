using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static CrossBuy.Tests.HospitalPartialDispenseTests;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H150: GS1 DataMatrix at the pharmacy counter. What must never happen:
	//   · a code misread (a wrong check digit, an identifier the reader does not know) taken as some medicine;
	//   · a pack of another medicine, an expired pack, a recalled batch, a batch the store does not hold, or a pack already
	//     scanned (its serial) counted for this prescription; or more packs counted than the line still owes;
	//   · a scanned pack given from a batch other than its own.
	// ==========================================================================================
	public class HospitalDispenseScanTests
	{
		private const char Gs = HospitalGs1.Gs;

		/// A valid GTIN-14 for 13 leading digits.
		private static string Gtin(string first13)
		{
			for (int c = 0; c <= 9; c++) if (HospitalGs1.CheckDigitOk(first13 + c)) return first13 + c;
			throw new InvalidOperationException();
		}

		private static readonly string Para = Gtin("0628701000100"), Amox = Gtin("0628701000200");

		[Fact]
		public void The_reader_takes_the_scanner_string_the_typed_string_and_a_retail_barcode()
		{
			var raw = HospitalGs1.Parse("]d201" + Para + "17271231" + "10PARA-26" + Gs + "21SN0001");
			Assert.Equal(new HospitalGs1Code(Para, new DateTime(2027, 12, 31), "PARA-26", "SN0001"), raw);
			Assert.Equal(raw, HospitalGs1.Parse("(01)" + Para + "(17)271231(10)PARA-26(21)SN0001"));
			Assert.Equal(raw, HospitalGs1.Parse("01" + Para + "21SN0001" + Gs + "17271231" + "10PARA-26"));   // any order
			Assert.Equal(new HospitalGs1Code(Para, null, null, null), HospitalGs1.Parse(Para[1..]));          // an EAN-13 alone
			Assert.Equal(new DateTime(2027, 2, 28), HospitalGs1.Parse("01" + Para + "17270200")!.Expiry);      // day 00 = the month's last
		}

		[Theory]
		[InlineData("")]
		[InlineData("0106287010001009")]           // a wrong check digit
		[InlineData("hello")]
		[InlineData("17271231")]                   // no GTIN
		public void A_code_that_cannot_be_read_is_refused_not_guessed(string raw)
		{
			Assert.Null(HospitalGs1.Parse(raw));
			Assert.Null(HospitalGs1.Parse("01" + Para + "9912345"));   // an identifier the reader does not know
			Assert.Null(HospitalGs1.Parse("01" + Para + "17271340"));  // month 13
		}

		[Fact]
		public void The_scanned_packs_leave_from_their_batch_and_the_rest_nearest_expiry_first()
		{
			Assert.Equal(new (string?, decimal)[] { ("B1", 2m), ("B2", 1m), (null, 2m) }, HospitalScanRules.Segments(true, new[] { "B1", "B2", "B1" }, 5).ToArray());
			Assert.Equal(new (string?, decimal)[] { (null, 3m) }, HospitalScanRules.Segments(false, new[] { "B1" }, 3).ToArray());   // not tracked by batch
			Assert.Equal(new (string?, decimal)[] { ("B1", 1m) }, HospitalScanRules.Segments(true, new[] { "B1", "B1" }, 1).ToArray());
		}

		private static async Task<HospitalDispenseScanService> Ready(World w, bool tracked)
		{
			foreach (var (id, gtin) in new[] { (71, Para), (72, Amox) })
			{
				var it = await w.Host.Db.Items.FirstAsync(i => i.ID == id);
				it.Barcode = gtin[1..]; it.TrackExpiry = tracked;
			}
			await w.Host.Db.SaveChangesAsync();
			return new HospitalDispenseScanService(w.Host.Db, w.Access, w.Pharmacy);
		}

		private static async Task Holds(World w, int itemId, string batch, decimal qty)
		{
			var b = new StockBatch { CompanyID = 1, ItemId = itemId, BatchNo = batch, ExpiryDate = new DateTime(2027, 12, 31), CreatedAt = Now };
			w.Host.Db.StockBatches.Add(b); await w.Host.Db.SaveChangesAsync();
			w.Host.Db.StockMovements.Add(new StockMovement { CompanyID = 1, MovementDate = Now, ItemId = itemId, WarehouseId = 5, Direction = 1, QtyBase = qty, UnitCost = 2m, TotalCost = 2m * qty, BatchId = b.ID, SourceType = "Opening", SourceId = 1, CreatedAt = Now });
			await w.Host.Db.SaveChangesAsync();
		}

		[Fact]
		public async Task Each_wrong_pack_is_refused_and_nothing_is_counted()
		{
			var (w, rx, para, amox) = await Prescribed();
			using var _h = w.Host;
			var svc = await Ready(w, tracked: true);
			await Holds(w, para, "P-OK", 20);
			string Code(string gtin, string batch, string serial, string exp = "271231") => "01" + gtin + "17" + exp + "10" + batch + Gs + "21" + serial;

			Assert.Equal("This is not a GS1 code this counter can read", (await svc.ScanAsync(Ctx(), rx.ID, "rubbish", 7, Now)).Error);
			Assert.Equal("This pack is not a medicine of this prescription", (await svc.ScanAsync(Ctx(), rx.ID, Code(Gtin("0628701000300"), "X", "1"), 7, Now)).Error);
			Assert.Equal("This pack expired on {0}: it cannot be given", (await svc.ScanAsync(Ctx(), rx.ID, Code(Para, "P-OK", "1", "251031"), 7, Now)).Error);
			Assert.Equal("The pharmacy store holds no more of batch {0}: check the pack", (await svc.ScanAsync(Ctx(), rx.ID, Code(Para, "P-NONE", "1"), 7, Now)).Error);
			w.Host.Db.Add(new HospitalLotRecall { CompanyId = 1, RecallNo = "RC-1", ItemId = para, BatchNo = "P-OK", RecallClass = HospitalLotRecallClass.I, Reason = "contamination", QuarantineWarehouseId = 5, OpenedAt = Now });
			await w.Host.Db.SaveChangesAsync();
			Assert.Equal("Batch {0} is under recall: put the pack aside", (await svc.ScanAsync(Ctx(), rx.ID, Code(Para, "P-OK", "1"), 7, Now)).Error);
			Assert.Equal("not_found", (await svc.ScanAsync(Ctx(7, 2), rx.ID, Code(Para, "P-OK", "1"), 7, Now)).Error);
			Assert.Empty(await w.Host.Db.Set<HospitalDispenseScan>().ToListAsync());
		}

		[Fact]
		public async Task A_pack_is_counted_once_and_never_past_what_the_line_owes()
		{
			var (w, rx, para, amox) = await Prescribed();   // 10 paracetamol, 4 amoxicillin
			using var _h = w.Host;
			var svc = await Ready(w, tracked: false);
			for (int n = 1; n <= 4; n++) Assert.True((await svc.ScanAsync(Ctx(), rx.ID, "(01)" + Amox + "(17)271231(10)A1(21)S" + n, 7, Now)).Ok);
			Assert.Equal("This pack (serial {0}) was already scanned", (await svc.ScanAsync(Ctx(), rx.ID, "(01)" + Amox + "(21)S1", 7, Now)).Error);
			Assert.Equal("All of this medicine is already scanned", (await svc.ScanAsync(Ctx(), rx.ID, "(01)" + Amox + "(21)S5", 7, Now)).Error);
			var lines = await Lines(w, rx.ID);
			Assert.Equal(4, (await svc.PendingAsync(Ctx(), rx.ID))[lines[amox].ID].Count);
			Assert.True((await svc.ClearAsync(Ctx(), rx.ID, lines[amox].ID)).Ok);
			Assert.Empty(await svc.PendingAsync(Ctx(), rx.ID));
		}

		[Fact]
		public async Task Dispensing_gives_a_scanned_pack_from_its_own_batch_and_marks_it_given()
		{
			var (w, rx, para, _) = await Prescribed();
			using var _h = w.Host;
			var svc = await Ready(w, tracked: true);
			await Holds(w, para, "P-LATE", 20);
			var lines = await Lines(w, rx.ID);
			Assert.True((await svc.ScanAsync(Ctx(), rx.ID, "(01)" + Para + "(17)271231(10)P-LATE(21)X1", 7, Now)).Ok);
			Assert.True((await svc.ScanAsync(Ctx(), rx.ID, "(01)" + Para + "(17)271231(10)P-LATE(21)X2", 7, Now)).Ok);
			int asked = w.Stock.Asked.Count;
			Assert.True((await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now, default, new Dictionary<int, decimal> { [lines[para].ID] = 3 })).Ok);
			var outs = w.Stock.Asked.Skip(asked).Where(m => m.ItemId == para).Select(m => (m.BatchNo, m.Qty)).ToArray();
			Assert.Equal(new (string?, decimal)[] { ("P-LATE", 2m), (null, 1m) }, outs);   // the two scanned from their batch, the third by expiry
			var scans = await w.Host.Db.Set<HospitalDispenseScan>().AsNoTracking().ToListAsync();
			Assert.All(scans, s => Assert.NotNull(s.FillId));
			Assert.Empty(await svc.PendingAsync(Ctx(), rx.ID));
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPharmacyModel.Configure);

		[Fact]
		public void The_scans_table_matches_its_slice()
		{
			var slice = HospitalSliceParity.Slice("hospital_h150_dispense_scans.sql");
			HospitalSliceParity.NamesItsTable(Model(), slice, typeof(HospitalDispenseScan), "HospitalDispenseScans");
			HospitalSliceParity.ColumnsMatch(Model(), slice, typeof(HospitalDispenseScan), "HospitalDispenseScans");
		}
	}
}
