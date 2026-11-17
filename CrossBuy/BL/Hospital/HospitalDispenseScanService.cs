using System.Globalization;
using System.Text.RegularExpressions;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H150 — GS1 DataMatrix at the pharmacy counter.
	//
	// THE CODE. A medicine pack's 2D code is a GS1 element string: application identifiers (AI) each followed by data —
	//   (01) GTIN, 14 digits · (17) expiry, YYMMDD (DD 00 = the month's last day) · (10) batch, up to 20 · (21) serial, up to 20
	// — the variable-length ones ended by the group separator (ASCII 29, FNC1) unless last. A scanner sends it raw (often
	// behind a "]d2" symbology prefix); a person types it with brackets. Both are read; a plain 8/12/13/14-digit retail
	// barcode is read as a GTIN alone. A GTIN whose check digit is wrong is not a GTIN: the code is refused, not guessed.
	//
	// THE SCAN keeps one row per pack, after refusing: a medicine not in this prescription, an expired pack, a batch under
	// an open recall, a batch the pharmacy store does not hold (or holds fewer of than are already scanned), a serial
	// already scanned anywhere (a pack is dispensed once), and more packs than the line still owes.
	// ==========================================================================================

	public sealed record HospitalGs1Code(string Gtin, DateTime? Expiry, string? Batch, string? Serial);

	public static class HospitalGs1
	{
		public const char Gs = '\u001D';
		private static readonly Dictionary<string, int> Fixed = new() { ["01"] = 14, ["02"] = 14, ["11"] = 6, ["13"] = 6, ["15"] = 6, ["17"] = 6 };
		private static readonly string[] Variable = { "10", "21", "22", "30", "240", "241", "710", "711", "712", "713", "714" };

		public static HospitalGs1Code? Parse(string? raw)
		{
			if (string.IsNullOrWhiteSpace(raw)) return null;
			var s = raw.Trim();
			foreach (var p in new[] { "]d2", "]C1", "]Q3", "]e0" }) if (s.StartsWith(p, StringComparison.Ordinal)) { s = s[p.Length..]; break; }
			s = s.Replace("<GS>", Gs.ToString()).Replace("{GS}", Gs.ToString()).Replace("\\u001d", Gs.ToString(), StringComparison.OrdinalIgnoreCase);
			if (s.Length > 0 && s[0] == Gs) s = s[1..];

			var ais = new Dictionary<string, string>();
			if (Regex.IsMatch(s, @"^\d{8}$|^\d{12,14}$")) ais["01"] = s.PadLeft(14, '0');   // a plain retail barcode
			else if (s.StartsWith('('))
			{
				foreach (Match m in Regex.Matches(s, @"\((\d{2,4})\)([^(" + Gs + "]*)"))
					ais[m.Groups[1].Value] = m.Groups[2].Value.Trim();
			}
			else
			{
				int i = 0;
				while (i < s.Length)
				{
					if (s[i] == Gs) { i++; continue; }
					string? ai = null;
					foreach (var f in Fixed.Keys) if (string.CompareOrdinal(s, i, f, 0, 2) == 0) { ai = f; break; }
					if (ai != null)
					{
						int len = Fixed[ai];
						if (i + 2 + len > s.Length) return null;
						ais[ai] = s.Substring(i + 2, len); i += 2 + len; continue;
					}
					ai = Variable.OrderByDescending(v => v.Length).FirstOrDefault(v => string.CompareOrdinal(s, i, v, 0, v.Length) == 0);
					if (ai == null) return null;   // an identifier this reader does not know: refuse rather than misread
					int start = i + ai.Length, end = s.IndexOf(Gs, start);
					if (end < 0) end = s.Length;
					ais[ai] = s[start..end]; i = end;
				}
			}
			if (!ais.TryGetValue("01", out var gtin) || !Regex.IsMatch(gtin, @"^\d{14}$") || !CheckDigitOk(gtin)) return null;
			DateTime? expiry = null;
			if (ais.TryGetValue("17", out var e)) { expiry = Date(e); if (expiry == null) return null; }
			string? Text(string k, int max) => ais.TryGetValue(k, out var v) && v.Length > 0 ? v[..Math.Min(max, v.Length)] : null;
			return new HospitalGs1Code(gtin, expiry, Text("10", 20), Text("21", 20));
		}

		public static bool CheckDigitOk(string digits)
		{
			int sum = 0;
			for (int k = 0; k < digits.Length - 1; k++)
			{
				int d = digits[digits.Length - 2 - k] - '0';
				sum += k % 2 == 0 ? d * 3 : d;
			}
			return (10 - sum % 10) % 10 == digits[^1] - '0';
		}

		/// YYMMDD; a day of 00 means the month's last day (GS1 General Specifications 3.4.x).
		public static DateTime? Date(string yymmdd)
		{
			if (!Regex.IsMatch(yymmdd, @"^\d{6}$")) return null;
			int y = 2000 + int.Parse(yymmdd[..2], CultureInfo.InvariantCulture), m = int.Parse(yymmdd.Substring(2, 2), CultureInfo.InvariantCulture), d = int.Parse(yymmdd.Substring(4, 2), CultureInfo.InvariantCulture);
			if (m < 1 || m > 12) return null;
			if (d == 0) d = DateTime.DaysInMonth(y, m);
			return d > DateTime.DaysInMonth(y, m) ? null : new DateTime(y, m, d);
		}

		/// The spellings an item's barcode may have for one GTIN: the 14 digits, and the EAN-13 / UPC-12 / EAN-8 it
		/// carries when it begins with zeros.
		public static IReadOnlyList<string> Forms(string gtin14)
		{
			var f = new List<string> { gtin14 };
			if (gtin14.StartsWith('0')) f.Add(gtin14[1..]);
			if (gtin14.StartsWith("00")) f.Add(gtin14[2..]);
			if (gtin14.StartsWith("000000")) f.Add(gtin14[6..]);
			return f;
		}
	}

	public static class HospitalScanRules
	{
		/// How a line's quantity leaves the store: the scanned packs from their own batch (grouped, in scan order), then the
		/// rest nearest-expiry-first (batch null). An item not tracked by batch, or a pack with no batch, joins the rest.
		public static IReadOnlyList<(string? BatchNo, decimal Qty)> Segments(bool tracked, IEnumerable<string?> scannedBatches, decimal give)
		{
			var parts = new List<(string? BatchNo, decimal Qty)>();
			decimal left = give;
			if (tracked)
				foreach (var g in scannedBatches.Where(b => b != null).GroupBy(b => b!))
				{
					var q = Math.Min(left, g.Count()); if (q <= 0) break;
					parts.Add((g.Key, q)); left -= q;
				}
			if (left > 0) parts.Add((null, left));
			return parts;
		}
	}

	public sealed record HospitalScanView(int LineId, int Count, IReadOnlyList<string> Batches);

	public sealed class HospitalDispenseScanService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalPharmacyService _pharmacy;
		public HospitalDispenseScanService(CrossDbContext db, IHospitalAccessService access, HospitalPharmacyService pharmacy) { _db = db; _access = access; _pharmacy = pharmacy; }

		private IQueryable<HospitalDispenseScan> Scans(BusinessContext ctx) => _db.Set<HospitalDispenseScan>().Where(s => s.CompanyId == ctx.CompanyId);

		/// The packs scanned for a dispense and not yet given, per line (empty when the table is not installed).
		public async Task<Dictionary<int, HospitalScanView>> PendingAsync(BusinessContext ctx, int dispenseId, CancellationToken ct = default)
		{
			try
			{
				var rows = await Scans(ctx).AsNoTracking().Where(s => s.DispenseId == dispenseId && s.FillId == null).ToListAsync(ct);
				return rows.GroupBy(r => r.LineId).ToDictionary(g => g.Key, g => new HospitalScanView(g.Key, g.Count(), g.Where(r => r.BatchNo != null).Select(r => r.BatchNo!).Distinct().ToList()));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return new(); }
		}

		public async Task<HospitalResult> ScanAsync(BusinessContext ctx, int dispenseId, string? raw, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return HospitalResult.Fail("forbidden");
			var code = HospitalGs1.Parse(raw);
			if (code == null) return HospitalResult.Fail("This is not a GS1 code this counter can read");
			var d = await _db.HospitalDispenses.AsNoTracking().FirstOrDefaultAsync(x => x.ID == dispenseId && x.CompanyId == ctx.CompanyId, ct);
			if (d == null) return HospitalResult.Fail("not_found");
			if (!HospitalDispenseStatus.Dispensable(d.Status)) return HospitalResult.Fail("Review the prescription before dispensing");

			var lines = await _db.HospitalDispenseLines.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.DispenseId == d.ID && l.ItemId != null).OrderBy(l => l.SortOrder).ToListAsync(ct);
			var itemIds = lines.Select(l => l.ItemId!.Value).Distinct().ToList();
			var forms = HospitalGs1.Forms(code.Gtin);
			var matched = await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && itemIds.Contains(i.ID) && i.Barcode != null && forms.Contains(i.Barcode)).Select(i => i.ID).ToListAsync(ct);
			matched.AddRange(await _db.ItemBarcodes.AsNoTracking().Where(b => itemIds.Contains(b.ItemId) && forms.Contains(b.Barcode)).Select(b => b.ItemId).ToListAsync(ct));
			if (matched.Count == 0) return HospitalResult.Fail("This pack is not a medicine of this prescription");

			// a pack is given once: its serial, anywhere, before anything else
			if (code.Serial != null && await Scans(ctx).AnyAsync(s => s.Gtin == code.Gtin && s.Serial == code.Serial, ct))
				return HospitalResult.Fail("This pack (serial {0}) was already scanned", code.Serial);
			var pending = await Scans(ctx).AsNoTracking().Where(s => s.DispenseId == d.ID && s.FillId == null).ToListAsync(ct);
			var line = lines.FirstOrDefault(l => matched.Contains(l.ItemId!.Value) && HospitalPartialDispenseRules.Owed(l) - pending.Count(p => p.LineId == l.ID) > 0);
			if (line == null) return HospitalResult.Fail("All of this medicine is already scanned");
			var item = await _db.Items.AsNoTracking().FirstAsync(i => i.ID == line.ItemId && i.CompanyID == ctx.CompanyId, ct);
			var name = DisplayName.Of(item.Name, item.NameEn ?? item.Name);

			if (code.Expiry is DateTime exp && exp.Date < nowUtc.Date) return HospitalResult.Fail("This pack expired on {0}: it cannot be given", exp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
			if (code.Batch != null)
			{
				bool recalled;
				try { recalled = await _db.Set<HospitalLotRecall>().AnyAsync(r => r.CompanyId == ctx.CompanyId && r.ItemId == item.ID && r.BatchNo == code.Batch && r.Status == HospitalLotRecallStatus.Quarantined, ct); }
				catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { recalled = false; }
				if (recalled) return HospitalResult.Fail("Batch {0} is under recall: put the pack aside", code.Batch);
				if (item.TrackExpiry)
				{
					var settings = await _pharmacy.SettingsAsync(ctx, ct);
					if (settings.WarehouseId is not int wh) return HospitalResult.Fail("Choose the pharmacy store in the pharmacy settings");
					var held = (await _pharmacy.BatchesAsync(ctx, wh, new[] { item.ID }, ct)).Where(b => b.BatchNo == code.Batch).Sum(b => b.Qty);
					int already = pending.Count(p => p.ItemId == item.ID && p.BatchNo == code.Batch);
					if (held < already + 1) return HospitalResult.Fail("The pharmacy store holds no more of batch {0}: check the pack", code.Batch);
				}
			}

			var row = new HospitalDispenseScan
			{
				CompanyId = ctx.CompanyId, DispenseId = d.ID, LineId = line.ID, ItemId = item.ID, Gtin = code.Gtin, BatchNo = code.Batch, Expiry = code.Expiry, Serial = code.Serial,
				At = nowUtc, ByEmployeeId = employeeId,
			};
			_db.Add(row);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(line.ID);
		}

		/// Takes back a line's scans not yet given (a pack put back on the shelf before dispensing).
		public async Task<HospitalResult> ClearAsync(BusinessContext ctx, int dispenseId, int lineId, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return HospitalResult.Fail("forbidden");
			var rows = await Scans(ctx).Where(s => s.DispenseId == dispenseId && s.LineId == lineId && s.FillId == null).ToListAsync(ct);
			_db.RemoveRange(rows);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(rows.Count);
		}
	}
}
