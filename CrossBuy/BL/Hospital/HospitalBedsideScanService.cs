using System.Globalization;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H205 — bedside barcode verification (the closed loop's last step). Before a dose is recorded GIVEN the nurse
	// scans the patient's WRISTBAND (its code is the file number, as the wristband prints it) and the medicine's PACK
	// (GS1 DataMatrix or the plain retail barcode). The dose is refused — and the refused scan kept — when:
	//   · the wristband is not this patient's (never says whose it is);
	//   · the pack is not the ordered medicine (matched through the item's barcodes; an order typed as free text has no
	//     item to match and can only be given with a reason);
	//   · the pack has expired, or its batch is under recall.
	// When a scan cannot be made (a torn band, an unreadable pack, an emergency) the dose is given with the REASON written:
	// an Override, which counts against the ward's compliance (HIMSS EMRAM: 95 % of doses scanned).
	// Held and missed doses need no scan: nothing is given.
	// ==========================================================================================
	public sealed record HospitalScanCompliance(int Matched, int Overrides, int Refused, double? Percent);
	public sealed record HospitalBedsideScanRow(HospitalBedsideScan Scan, string Patient, string PatientEn, string Mrn, string? By);

	public static class HospitalBedsideScanRules
	{
		public const double Target = 95;
		public const string WrongPatient = "This wristband is not this patient's: stop and check who the patient is";
		public const string WrongMedicine = "This pack is not the ordered medicine: stop and check the order";
		public const string Unreadable = "The pack's code could not be read";
		public const string Expired = "This pack expired on {0}: it cannot be given";
		public const string Recalled = "Batch {0} is under recall: put the pack aside";
		public const string NoItem = "This order names no medicine of the drug list, so its pack cannot be matched: write why it is given without the scan";
		public const string ScanOrReason = "Scan the patient's wristband and the medicine, or write why they cannot be scanned";

		private static string N(string? s) => (s ?? "").Trim().ToUpperInvariant();

		/// The wristband's code is the file number (its QR prints PatientMrn); a scanner may add spaces or change case.
		public static bool WristbandMatches(string? scanned, string mrn) => N(scanned).Length > 0 && N(scanned) == N(mrn);

		public static bool Scanned(string? wristband, string? pack) => !string.IsNullOrWhiteSpace(wristband) && !string.IsNullOrWhiteSpace(pack);

		/// Matched over everything given (matched + overrides), as a percent; null when nothing was given.
		public static double? Percent(int matched, int overrides) =>
			matched + overrides == 0 ? null : Math.Round(100.0 * matched / (matched + overrides), 1, MidpointRounding.AwayFromZero);
	}

	public static class HospitalBedsideScanCheck
	{
		/// The medication check: null = both right (with the parsed pack), else the refusal key and its values.
		public static async Task<(string? Refusal, object[] Args, HospitalGs1Code? Pack)> MedicationAsync(CrossDbContext db, BusinessContext ctx, HospitalMedOrder order,
			string mrn, string wristband, string pack, DateTime nowUtc, CancellationToken ct)
		{
			if (!HospitalBedsideScanRules.WristbandMatches(wristband, mrn)) return (HospitalBedsideScanRules.WrongPatient, Array.Empty<object>(), null);
			var code = HospitalGs1.Parse(pack);
			if (code == null) return (HospitalBedsideScanRules.Unreadable, Array.Empty<object>(), null);
			if (order.ItemId is not int itemId) return (HospitalBedsideScanRules.NoItem, Array.Empty<object>(), code);
			var forms = HospitalGs1.Forms(code.Gtin);
			bool match = await db.Items.AsNoTracking().AnyAsync(i => i.ID == itemId && i.CompanyID == ctx.CompanyId && i.Barcode != null && forms.Contains(i.Barcode), ct)
				|| await db.ItemBarcodes.AsNoTracking().AnyAsync(b => b.ItemId == itemId && forms.Contains(b.Barcode), ct);
			if (!match) return (HospitalBedsideScanRules.WrongMedicine, Array.Empty<object>(), code);
			if (code.Expiry is DateTime exp && exp.Date < nowUtc.Date) return (HospitalBedsideScanRules.Expired, new object[] { exp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, code);
			if (code.Batch != null)
			{
				bool recalled;
				try { recalled = await db.Set<HospitalLotRecall>().AnyAsync(r => r.CompanyId == ctx.CompanyId && r.ItemId == itemId && r.BatchNo == code.Batch && r.Status == HospitalLotRecallStatus.Quarantined, ct); }
				catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { recalled = false; }
				if (recalled) return (HospitalBedsideScanRules.Recalled, new object[] { code.Batch }, code);
			}
			return (null, Array.Empty<object>(), code);
		}

		public static HospitalBedsideScan Row(BusinessContext ctx, string kind, int patientId, int? admissionId, int? refId, string? wristband, string? item, HospitalGs1Code? code,
			string outcome, string? detail, int? employeeId, DateTime nowUtc) => new()
		{
			CompanyId = ctx.CompanyId, Kind = kind, PatientId = patientId, AdmissionId = admissionId, RefId = refId,
			Wristband = Clip(wristband, 60), Item = Clip(item, 120), Gtin = code?.Gtin, BatchNo = code?.Batch, Expiry = code?.Expiry,
			Outcome = outcome, Detail = Clip(detail, 300), At = nowUtc, ByEmployeeId = employeeId,
		};

		public static string? Clip(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= n ? s.Trim() : s.Trim()[..n];

		/// A refusal kept with its values, so the list says it in the reader's language.
		public static string Pack(string key, object[] args) => args.Length == 0 ? key : key + "\t" + string.Join("\t", args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)));
	}

	public sealed class HospitalBedsideScanService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalBedsideScanService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private IQueryable<HospitalBedsideScan> Scans(BusinessContext ctx) => _db.Set<HospitalBedsideScan>().Where(x => x.CompanyId == ctx.CompanyId);

		/// The scans of a ward (the room the patient lay in at the time) or of all wards, in a period.
		private IQueryable<HospitalBedsideScan> InWard(BusinessContext ctx, int? wardId, DateTime fromUtc, DateTime toUtc)
		{
			var q = Scans(ctx).Where(s => s.At >= fromUtc && s.At < toUtc);
			if (wardId is not int w) return q;
			return from s in q
				   where _db.HospitalBedStays.Any(b => b.CompanyId == ctx.CompanyId && b.AdmissionId == s.AdmissionId && b.StartedAt <= s.At && (b.EndedAt == null || b.EndedAt > s.At)
					   && _db.HospitalRooms.Any(r => r.ID == b.RoomId && r.WardId == w))
				   select s;
		}

		public async Task<HospitalScanCompliance?> ComplianceAsync(BusinessContext ctx, int? wardId, string kind, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
		{
			try
			{
				var counts = await InWard(ctx, wardId, fromUtc, toUtc).Where(s => s.Kind == kind).GroupBy(s => s.Outcome).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
				int M(string o) => counts.FirstOrDefault(c => c.Key == o)?.N ?? 0;
				int matched = M(HospitalBedsideScanOutcome.Matched), over = M(HospitalBedsideScanOutcome.Override);
				return new HospitalScanCompliance(matched, over, M(HospitalBedsideScanOutcome.Refused), HospitalBedsideScanRules.Percent(matched, over));
			}
			catch (Microsoft.Data.SqlClient.SqlException) { return null; }
		}

		public async Task<(IReadOnlyList<HospitalBedsideScanRow> Rows, int Total)> ListAsync(BusinessContext ctx, int? wardId, string? kind, string? outcome, string? q,
			DateTime fromUtc, DateTime toUtc, int page, int size, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return (Array.Empty<HospitalBedsideScanRow>(), 0);
			try
			{
				var query = from s in InWard(ctx, wardId, fromUtc, toUtc).AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on s.PatientId equals p.ID
							join e0 in _db.Employee.AsNoTracking() on s.ByEmployeeId equals e0.ID into ee
							from e in ee.DefaultIfEmpty()
							where p.CompanyId == ctx.CompanyId
							select new { s, p, e };
				if (!string.IsNullOrEmpty(kind)) query = query.Where(x => x.s.Kind == kind);
				if (!string.IsNullOrEmpty(outcome)) query = query.Where(x => x.s.Outcome == outcome);
				if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim(); query = query.Where(x => x.p.Name.Contains(t) || x.p.NameEn.Contains(t) || x.p.Mrn.Contains(t) || (x.s.Item != null && x.s.Item.Contains(t))); }
				int total = await query.CountAsync(ct);
				page = Math.Max(1, page);
				var rows = await query.OrderByDescending(x => x.s.At).ThenByDescending(x => x.s.ID).Skip((page - 1) * size).Take(size).ToListAsync(ct);
				return (rows.Select(x => new HospitalBedsideScanRow(x.s, x.p.Name, x.p.NameEn, x.p.Mrn, x.e == null ? null : EmployeeNames.Of(x.e))).ToList(), total);
			}
			catch (Microsoft.Data.SqlClient.SqlException) { return (Array.Empty<HospitalBedsideScanRow>(), 0); }
		}
	}
}
