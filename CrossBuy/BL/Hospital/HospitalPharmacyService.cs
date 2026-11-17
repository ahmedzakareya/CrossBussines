using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H10 — the pharmacy, dispensing from the inventory.
	//
	//   · Every signed visit with a prescription becomes one dispense on the queue (RX-000001), its lines
	//     copied from the prescription — an inventory item the doctor picked, or free text not stocked.
	//   · The pharmacist reviews it (`pharmacy`): the screen shows what the review must weigh — an allergy
	//     recorded on the file that a drug's name mentions, the same item twice, a drug not stocked, stock
	//     that does not cover the quantity, a batch near expiry.
	//   · Dispensing is ALL OR NOTHING, in one transaction: every stocked line leaves the pharmacy store
	//     through the inventory's single stock writer (IStockService.PostMovementAsync — nearest expiry first,
	//     expired batches refused, the cost posted Dr the category's cost of goods / Cr its inventory), and
	//     becomes a charge on the visit's bill (or the patient's pharmacy bill when the visit's is closed) at
	//     the patient's price-list price. A free-text line is not charged — the patient buys it outside.
	//   · No stock rule lives here; the movements carry SourceType "HospitalDispense" so they are found again.
	// ==========================================================================================

	/// H136: Priority is the effective one (HospitalRxPriority.Effective); ReviewedAt stops the review clock.
	/// H145: partial dispensing — what a line still owes, what may leave now, and the prescription's status after.
	public static class HospitalPartialDispenseRules
	{
		public static decimal Owed(HospitalDispenseLine l) => Math.Max(0, l.Quantity - (l.DispensedQty ?? 0));

		/// The quantity to give now on each stocked line: what the pharmacist asked (null = all that is owed). Refused when a
		/// line asks for less than nothing or more than it owes, or when nothing at all would leave.
		public static (IReadOnlyDictionary<int, decimal>? Now, string? Error) Plan(IEnumerable<HospitalDispenseLine> stocked, IReadOnlyDictionary<int, decimal>? asked)
		{
			var now = new Dictionary<int, decimal>();
			foreach (var l in stocked)
			{
				decimal owed = Owed(l);
				decimal q = asked != null && asked.TryGetValue(l.ID, out var a) ? Math.Round(a, 4) : owed;
				if (q < 0) return (null, "A quantity cannot be negative");
				if (q > owed) return (null, "{0}: more than the prescription still owes ({1})");
				if (q > 0) now[l.ID] = q;
			}
			return now.Count == 0 ? (null, "Nothing to dispense: enter a quantity") : (now, null);
		}

		/// Dispensed when nothing is owed on any stocked line; Partial otherwise.
		public static string StatusAfter(IEnumerable<HospitalDispenseLine> stocked) => stocked.All(l => Owed(l) == 0) ? HospitalDispenseStatus.Dispensed : HospitalDispenseStatus.Partial;
	}

	public sealed record HospitalRxRow(int Id, string RxNo, int PatientId, string PatientName, string PatientNameEn, string Mrn, string? PatientPhoto,
		string Source, int Items, string? Doctor, string? DoctorEn, string? DoctorPhoto, string Status, DateTime PrescribedAt, bool HasWarning,
		string Priority = HospitalRxPriority.Routine, DateTime? ReviewedAt = null);

	/// H133: Master is the line's drug record (null when the item has none); MatchText the words its allergies and interactions
/// were matched in (the prescribed name and the generic).
public sealed record HospitalRxLineView(HospitalDispenseLine Line, string? ItemCode, decimal? OnHand, string? NextBatch, DateTime? NextExpiry, decimal? Price, IReadOnlyList<string> Warnings,
		HospitalDrug? Master = null, string? MatchText = null, string? DrugEn = null, string? InstructionsEn = null)
	{
		/// The drug and its directions in the screen's language: the prescription wrote both (H33); the dispense line kept one.
		public string Drug => DisplayName.Of(Line.Drug, DrugEn);
		public string? Instructions => string.IsNullOrWhiteSpace(Line.Instructions) && string.IsNullOrWhiteSpace(InstructionsEn) ? null : DisplayName.Of(Line.Instructions ?? "", InstructionsEn);
	}

	public sealed record HospitalRxView(HospitalDispense Dispense, HospitalPatient Patient, IReadOnlyList<HospitalRxLineView> Lines, IReadOnlyDictionary<int, string> People,
		string? WarehouseName, string? PriceListName, string? CurrencyLabel, decimal Total, string? BillNo = null, bool OnStay = false);

	/// H136: Late = waiting for the review past its priority's target; the month's interventions and how many the prescribers accepted.
	public sealed record HospitalPharmacyStats(int Waiting, int? AvgDispenseMinutes, int NearExpiry, int BelowReorder, int NearExpiryDays,
		int Late = 0, int Interventions = 0, int? AcceptedPct = null);

	public sealed class HospitalPharmacyService
	{
		public const string MovementSource = "HospitalDispense";

		/// H121: a request with no visit behind it (a chemotherapy cycle's) is charged like a ward charge — on the stay's
		/// bill while the patient is admitted, beside the cycle's own administration charge. A visit's request stays on the
		/// visit's bill.
		public static bool BillsLikeAWardCharge(HospitalDispense d) => d.EncounterId == null;
		public const string WarnAllergy = "allergy", WarnDuplicate = "duplicate", WarnNotStocked = "not-stocked", WarnShort = "short", WarnNearExpiry = "near-expiry";
		/// H73: the line interacts with another line or with a medicine the patient is still given in hospital.
		public const string WarnInteraction = "interaction";
		/// H133, from the drug master: a high-alert medicine (a second check before it leaves), one confused with another
		/// name, a restricted or non-formulary one, one withdrawn from the formulary (refused at dispensing).
		public const string WarnHighAlert = "high-alert", WarnLasa = "lasa", WarnRestricted = "restricted", WarnNonFormulary = "non-formulary", WarnUnavailable = "unavailable";

		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IStockService _stock;
		private readonly HospitalBillingService _billing;

		public HospitalPharmacyService(CrossDbContext db, IHospitalAccessService access, IStockService stock, HospitalBillingService billing)
		{ _db = db; _access = access; _stock = stock; _billing = billing; }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);

		// ====================================================================================
		// THE RULES (pure)
		// ====================================================================================

		/// The recorded allergies (one per line, or separated by commas) that a drug's name mentions.
		public static IReadOnlyList<string> AllergiesIn(string drug, string? allergies)
		{
			if (string.IsNullOrWhiteSpace(allergies) || string.IsNullOrWhiteSpace(drug)) return Array.Empty<string>();
			return allergies.Split(new[] { '\n', ',', '،', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(a => a.Split('(')[0].Trim())
				.Where(a => a.Length >= 3 && drug.Contains(a, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
		}

		// ====================================================================================
		// SETTINGS
		// ====================================================================================

		public async Task<HospitalPharmacySettings> SettingsAsync(BusinessContext ctx, CancellationToken ct = default)
			=> await _db.HospitalPharmacySettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == ctx.CompanyId, ct) ?? new HospitalPharmacySettings { CompanyId = ctx.CompanyId };

		public async Task<IReadOnlyList<(int Id, string Code, string Name, string? NameEn)>> WarehousesAsync(BusinessContext ctx, CancellationToken ct = default)
			=> (await _db.Warehouses.AsNoTracking().Where(w => w.CompanyID == ctx.CompanyId && w.IsActive).OrderBy(w => w.Code)
				.Select(w => new { w.ID, w.Code, w.Name, w.NameEn }).ToListAsync(ct)).Select(w => (w.ID, w.Code, w.Name, w.NameEn)).ToList();

		public async Task<HospitalResult> SaveSettingsAsync(BusinessContext ctx, int? warehouseId, int nearExpiryDays, CancellationToken ct = default, int? quarantineWarehouseId = null)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (nearExpiryDays is < 1 or > 365) return HospitalResult.Fail("Near expiry is 1 to 365 days");
			if (warehouseId is int w && !await _db.Warehouses.AnyAsync(x => x.ID == w && x.CompanyID == ctx.CompanyId && x.IsActive, ct)) return HospitalResult.Fail("not_found");
			// H134: the quarantine store holds what must not be dispensed, so it is never the store that dispenses
			if (quarantineWarehouseId is int q && q == warehouseId) return HospitalResult.Fail("The quarantine store must be another store than the pharmacy's");
			if (quarantineWarehouseId is int q2 && !await _db.Warehouses.AnyAsync(x => x.ID == q2 && x.CompanyID == ctx.CompanyId && x.IsActive, ct)) return HospitalResult.Fail("not_found");
			var s = await _db.HospitalPharmacySettings.FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId, ct);
			if (s == null) { s = new HospitalPharmacySettings { CompanyId = ctx.CompanyId }; _db.HospitalPharmacySettings.Add(s); }
			s.WarehouseId = warehouseId; s.NearExpiryDays = nearExpiryDays; s.QuarantineWarehouseId = quarantineWarehouseId;
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(s.ID);
		}

		// ====================================================================================
		// THE QUEUE
		// ====================================================================================

		/// Creates the dispense of every signed visit with a prescription that has none yet. Idempotent.
		public async Task<int> SyncAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var pending = await _db.HospitalEncounters.AsNoTracking()
				.Where(e => e.CompanyId == ctx.CompanyId && e.Status == HospitalEncounterStatus.Signed
					&& _db.HospitalPrescriptionLines.Any(r => r.CompanyId == ctx.CompanyId && r.EncounterId == e.ID)
					&& !_db.HospitalDispenses.Any(d => d.CompanyId == ctx.CompanyId && d.EncounterId == e.ID))
				.OrderBy(e => e.ID).ToListAsync(ct);
			if (pending.Count == 0) return 0;
			var ids = pending.Select(e => e.ID).ToList();
			var rx = await _db.HospitalPrescriptionLines.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && ids.Contains(r.EncounterId)).OrderBy(r => r.SortOrder).ToListAsync(ct);
			var nos = await _db.HospitalDispenses.Where(d => d.CompanyId == ctx.CompanyId && d.RxNo.StartsWith("RX-")).Select(d => d.RxNo).ToListAsync(ct);
			int n = 0;
			foreach (var no in nos) if (int.TryParse(no.AsSpan(3), out var v) && v > n) n = v;
			try
			{
				await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
				foreach (var e in pending)
				{
					var d = new HospitalDispense
					{
						CompanyId = ctx.CompanyId, RxNo = "RX-" + (++n).ToString("000000"), PatientId = e.PatientId, EncounterId = e.ID, Source = HospitalDispenseSource.Outpatient,
						PrescribedByEmployeeId = e.SignedByEmployeeId, PractitionerId = e.PractitionerId, PrescribedAt = e.SignedAt ?? DateTime.UtcNow,
					};
					_db.HospitalDispenses.Add(d);
					await _db.SaveChangesAsync(ct);
					int order = 0;
					foreach (var r in rx.Where(r => r.EncounterId == e.ID))
						_db.HospitalDispenseLines.Add(new HospitalDispenseLine
						{
							CompanyId = ctx.CompanyId, DispenseId = d.ID, PrescriptionLineId = r.ID, ItemId = r.ItemId, Drug = r.Drug, Instructions = r.Instructions,
							Quantity = Math.Max(1, r.Quantity), SortOrder = ++order,
						});
					await _db.SaveChangesAsync(ct);
				}
				await tx.CommitAsync();
			}
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return 0; }   // another request created them first
			return pending.Count;
		}

		public async Task<IReadOnlyList<HospitalRxRow>> QueueAsync(BusinessContext ctx, string? status, CancellationToken ct = default)
		{
			var q = from d in _db.HospitalDispenses.AsNoTracking()
					join p in _db.HospitalPatients.AsNoTracking() on d.PatientId equals p.ID
					join r0 in _db.HospitalPractitioners.AsNoTracking() on d.PractitionerId equals (int?)r0.ID into rs
					from r in rs.DefaultIfEmpty()
					where d.CompanyId == ctx.CompanyId && p.CompanyId == ctx.CompanyId
					select new { d, p, r };
			if (status != null) q = q.Where(x => x.d.Status == status);
			else q = q.Where(x => x.d.Status == HospitalDispenseStatus.New || x.d.Status == HospitalDispenseStatus.Verified || x.d.Status == HospitalDispenseStatus.Partial
				|| (x.d.Status == HospitalDispenseStatus.Dispensed && x.d.DispensedAt >= DateTime.UtcNow.AddDays(-1)));
			bool Open(HospitalDispense d) => d.Status == HospitalDispenseStatus.New || d.Status == HospitalDispenseStatus.Verified || d.Status == HospitalDispenseStatus.Partial;
			// H136: what still needs the pharmacy comes first, the most urgent priority first, then the oldest
			var rows = (await q.OrderBy(x => x.d.PrescribedAt).Take(300).ToListAsync(ct))
				.OrderBy(x => Open(x.d) ? 0 : 1).ThenBy(x => HospitalRxPriority.Rank(HospitalRxPriority.Effective(x.d.Priority, x.d.Source))).ThenBy(x => x.d.PrescribedAt).ToList();
			var dids = rows.Select(x => x.d.ID).ToList();
			var lines = await _db.HospitalDispenseLines.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && dids.Contains(l.DispenseId)).ToListAsync(ct);
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, rows.Where(x => x.r?.EmployeeId != null).Select(x => x.r!.EmployeeId!.Value).Distinct(), ct);
			return rows.Select(x =>
			{
				var ls = lines.Where(l => l.DispenseId == x.d.ID).ToList();
				bool warn = ls.Any(l => AllergiesIn(l.Drug, x.p.Allergies).Count > 0) || ls.Where(l => l.ItemId != null).GroupBy(l => l.ItemId).Any(g => g.Count() > 1);
				return new HospitalRxRow(x.d.ID, x.d.RxNo, x.p.ID, x.p.Name, x.p.NameEn, x.p.Mrn, x.p.PhotoPath, x.d.Source, ls.Count, x.r?.Name, x.r?.NameEn,
					x.r?.EmployeeId is int de ? photos.GetValueOrDefault(de) : null, x.d.Status, x.d.PrescribedAt, warn,
					HospitalRxPriority.Effective(x.d.Priority, x.d.Source), x.d.VerifiedAt ?? x.d.RejectedAt);
			}).ToList();
		}

		/// On-hand in the warehouse per batch of these items (from the movements — the inventory keeps no per-batch balance).
		public async Task<List<(int ItemId, string BatchNo, DateTime? Expiry, decimal Qty)>> BatchesAsync(BusinessContext ctx, int warehouseId, IReadOnlyCollection<int> itemIds, CancellationToken ct)
		{
			var sums = await _db.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == ctx.CompanyId && m.WarehouseId == warehouseId && m.BatchId != null && itemIds.Contains(m.ItemId))
				.GroupBy(m => new { m.ItemId, m.BatchId }).Select(g => new { g.Key.ItemId, g.Key.BatchId, Qty = g.Sum(m => m.Direction * m.QtyBase) })
				.Where(x => x.Qty > 0).ToListAsync(ct);
			var bids = sums.Select(s => s.BatchId!.Value).ToList();
			var batches = await _db.StockBatches.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && bids.Contains(b.ID)).ToDictionaryAsync(b => b.ID, ct);
			return sums.Where(s => batches.ContainsKey(s.BatchId!.Value))
				.Select(s => (s.ItemId, batches[s.BatchId!.Value].BatchNo, batches[s.BatchId!.Value].ExpiryDate, s.Qty)).ToList();
		}

		public async Task<HospitalRxView?> RxAsync(BusinessContext ctx, int id, DateTime nowUtc, CancellationToken ct = default)
		{
			var d = await _db.HospitalDispenses.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
			if (d == null) return null;
			var p = await _db.HospitalPatients.AsNoTracking().FirstAsync(x => x.ID == d.PatientId && x.CompanyId == ctx.CompanyId, ct);
			var lines = await _db.HospitalDispenseLines.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.DispenseId == d.ID).OrderBy(l => l.SortOrder).ToListAsync(ct);
			var settings = await SettingsAsync(ctx, ct);
			int? wh = d.WarehouseId ?? settings.WarehouseId;
			var itemIds = lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).Distinct().ToList();
			var items = await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && itemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, ct);
			var balances = wh == null ? new Dictionary<int, decimal>() : await _db.StockBalances.AsNoTracking()
				.Where(b => b.CompanyID == ctx.CompanyId && b.WarehouseId == wh && itemIds.Contains(b.ItemId)).ToDictionaryAsync(b => b.ItemId, b => b.QtyOnHand, ct);
			var batches = wh == null ? new() : await BatchesAsync(ctx, wh.Value, itemIds, ct);
			var bill = d.BillId != null ? await _db.HospitalBills.AsNoTracking().FirstOrDefaultAsync(b => b.ID == d.BillId && b.CompanyId == ctx.CompanyId, ct)
				: d.EncounterId != null ? await _db.HospitalBills.AsNoTracking().FirstOrDefaultAsync(b => b.CompanyId == ctx.CompanyId && b.EncounterId == d.EncounterId, ct)
				: await _billing.PeekWardChargeBillAsync(ctx, d.PatientId, ct);   // H121: the bill it will go on, priced by that bill's list
			var listId = bill?.PriceListId;
			var near = nowUtc.Date.AddDays(settings.NearExpiryDays);
			var writtenIds = lines.Where(x => x.PrescriptionLineId != null).Select(x => x.PrescriptionLineId!.Value).ToList();   // the prescription's own lines: both languages
			var written = await _db.HospitalPrescriptionLines.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && writtenIds.Contains(x.ID))
				.Select(x => new { x.ID, x.DrugEn, x.InstructionsEn }).ToDictionaryAsync(x => x.ID, ct);
			var masters = await HospitalDrugMasterService.LookupAsync(_db, ctx.CompanyId, itemIds, ct);
			HospitalDrug? Master(HospitalDispenseLine x) => x.ItemId is int mi ? masters.GetValueOrDefault(mi) : null;
			string Words(HospitalDispenseLine x) => HospitalDrugRules.MatchText(x.Drug, Master(x));
			var (kbPairs, _) = await HospitalDrugSafetyService.KbAsync(_db, ctx.CompanyId, ct);
			var running = kbPairs.Count == 0 ? new List<string>() : await (from o in _db.Set<HospitalMedOrder>().AsNoTracking()
				join a in _db.HospitalAdmissions.AsNoTracking() on o.AdmissionId equals a.ID
				where o.CompanyId == ctx.CompanyId && o.PatientId == d.PatientId && o.Status == HospitalMedOrderStatus.Active && a.Status == HospitalAdmissionStatus.Active
				select o.Drug).ToListAsync(ct);
			var views = new List<HospitalRxLineView>();
			foreach (var l in lines)
			{
				var w = new List<string>();
				var m = Master(l); var words = Words(l);
				if (AllergiesIn(words, p.Allergies).Count > 0) w.Add(WarnAllergy);
				if (l.ItemId != null && lines.Count(x => x.ItemId == l.ItemId) > 1) w.Add(WarnDuplicate);
				if (kbPairs.Count > 0 && HospitalDrugSafetyRules.Interactions(words, lines.Where(x => x.ID != l.ID).Select(Words).Concat(running), kbPairs).Count > 0) w.Add(WarnInteraction);
				if (m?.FormularyStatus == HospitalFormularyStatus.Unavailable) w.Add(WarnUnavailable);
				if (m?.FormularyStatus == HospitalFormularyStatus.NonFormulary) w.Add(WarnNonFormulary);
				if (m?.FormularyStatus == HospitalFormularyStatus.Restricted) w.Add(WarnRestricted);
				if (m?.HighAlert == true) w.Add(WarnHighAlert);
				if (m?.LasaWith != null) w.Add(WarnLasa);
				decimal? onHand = null; string? nextBatch = null; DateTime? nextExpiry = null; decimal? price = l.UnitPrice;
				if (l.ItemId is not int iid) w.Add(WarnNotStocked);
				else
				{
					onHand = balances.GetValueOrDefault(iid);
					var b = batches.Where(x => x.ItemId == iid && (x.Expiry == null || x.Expiry >= nowUtc.Date)).OrderBy(x => x.Expiry == null).ThenBy(x => x.Expiry).ThenBy(x => x.BatchNo).FirstOrDefault();
					if (b.BatchNo != null) { nextBatch = b.BatchNo; nextExpiry = b.Expiry; }
					if (d.Status != HospitalDispenseStatus.Dispensed && onHand < l.Quantity) w.Add(WarnShort);
					var exp = d.Status == HospitalDispenseStatus.Dispensed ? l.NearestExpiry : nextExpiry;
					if (exp != null && exp <= near) w.Add(WarnNearExpiry);
					price ??= (await _billing.QuoteItemForBillAsync(ctx, listId, iid, ct))?.Price;
				}
				var pl = l.PrescriptionLineId is int plid ? written.GetValueOrDefault(plid) : null;
				views.Add(new HospitalRxLineView(l, l.ItemId is int i2 && items.TryGetValue(i2, out var it) ? it.ItemCode : null, onHand, nextBatch, nextExpiry, price, w, m, words, pl?.DrugEn, pl?.InstructionsEn));
			}
			var empIds = new[] { d.PrescribedByEmployeeId, d.VerifiedByEmployeeId, d.DispensedByEmployeeId, d.RejectedByEmployeeId }.Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
			var whName = wh == null ? null : await _db.Warehouses.AsNoTracking().Where(x => x.ID == wh && x.CompanyID == ctx.CompanyId).Select(x => x.Code + " · " + (DisplayName.IsArabic ? x.Name : x.NameEn ?? x.Name)).FirstOrDefaultAsync(ct);
			var listName = listId == null ? null : await _db.HospitalPriceLists.AsNoTracking().Where(x => x.ID == listId && x.CompanyId == ctx.CompanyId).Select(x => DisplayName.IsArabic ? x.Name : x.NameEn).FirstOrDefaultAsync(ct);
			var total = views.Where(v => v.Line.ItemId != null).Sum(v => (v.Price ?? 0) * (v.Line.DispensedQty ?? v.Line.Quantity));
			return new HospitalRxView(d, p, views, people, whName, listName, await _billing.CurrencyLabelAsync(ctx, bill, ct), total, bill?.BillNo, bill?.AdmissionId != null);
		}

		public async Task<HospitalPharmacyStats> StatsAsync(BusinessContext ctx, DateTime dayStartUtc, DateTime nowUtc, CancellationToken ct = default)
		{
			var settings = await SettingsAsync(ctx, ct);
			var all = _db.HospitalDispenses.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId);
			var open = await all.Where(d => d.Status == HospitalDispenseStatus.New || d.Status == HospitalDispenseStatus.Verified)
				.Select(d => new { d.Status, d.Priority, d.Source, d.PrescribedAt }).ToListAsync(ct);
			int waiting = open.Count;
			// H136: still waiting for the review past its priority's target
			int late = open.Count(d => d.Status == HospitalDispenseStatus.New && HospitalReviewRules.IsLate(HospitalRxPriority.Effective(d.Priority, d.Source), d.PrescribedAt, null, nowUtc));
			var (interventions, acceptedPct) = await HospitalPharmacyReviewService.MonthAsync(_db, ctx.CompanyId, nowUtc, ct);
			var done = await all.Where(d => d.DispensedAt >= dayStartUtc).Select(d => new { d.PrescribedAt, d.DispensedAt }).ToListAsync(ct);
			int? avg = done.Count == 0 ? null : (int)Math.Round(done.Average(x => (x.DispensedAt!.Value - x.PrescribedAt).TotalMinutes));
			int nearCount = 0, below = 0;
			if (settings.WarehouseId is int wh)
			{
				var meds = await MedicineItemIdsAsync(ctx, ct);
				var near = nowUtc.Date.AddDays(settings.NearExpiryDays);
				nearCount = (await BatchesAsync(ctx, wh, meds, ct)).Count(b => b.Expiry != null && b.Expiry <= near);
				var settingsRows = await _db.ItemWarehouseSettings.AsNoTracking().Where(s => s.WarehouseId == wh && meds.Contains(s.ItemId) && (s.ReorderPoint != null || s.MinQty != null)).ToListAsync(ct);
				var bal = await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && b.WarehouseId == wh && meds.Contains(b.ItemId)).ToDictionaryAsync(b => b.ItemId, b => b.QtyOnHand, ct);
				below = settingsRows.Count(s => bal.GetValueOrDefault(s.ItemId) <= (s.ReorderPoint ?? s.MinQty ?? 0));
			}
			return new HospitalPharmacyStats(waiting, avg, nearCount, below, settings.NearExpiryDays, late, interventions, acceptedPct);
		}

		/// The items the hospital dispenses: those in the inventory categories it maps (medicines and consumables).
		public async Task<List<int>> MedicineItemIdsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var cats = await _db.HospitalItemCategories.AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId).Select(c => c.ItemCategoryId).ToListAsync(ct);
			return await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && i.IsActive && cats.Contains(i.ItemCategoryId)).Select(i => i.ID).ToListAsync(ct);
		}

		/// What a doctor can prescribe from stock: the mapped items, with their selling price.
		public async Task<IReadOnlyList<(int Id, string Code, string Name, string NameEn, decimal? Price)>> FormularyAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var ids = await MedicineItemIdsAsync(ctx, ct);
			return (await _db.Items.AsNoTracking().Where(i => ids.Contains(i.ID)).OrderBy(i => i.Name).Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn, i.SalesPrice }).ToListAsync(ct))
				.Select(i => (i.ID, i.ItemCode, i.Name, i.NameEn ?? i.Name, i.SalesPrice)).ToList();
		}

		// ====================================================================================
		// REVIEW AND DISPENSE
		// ====================================================================================

		public async Task<HospitalResult> VerifyAsync(BusinessContext ctx, int id, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Pharmacy, ct)) return HospitalResult.Fail("forbidden");
			var d = await _db.HospitalDispenses.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
			if (d == null) return HospitalResult.Fail("not_found");
			if (d.Status != HospitalDispenseStatus.New) return HospitalResult.Fail("This prescription was already reviewed");
			d.Status = HospitalDispenseStatus.Verified; d.VerifiedAt = nowUtc; d.VerifiedByEmployeeId = employeeId;
			d.PharmacistNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(400, note.Trim().Length)];
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(d.ID);
		}

		/// Dispenses a reviewed prescription: each stocked line all it still owes, or (H145) the quantity the pharmacist gives
		/// now when the store is short; the rest stays owed (Partial) and a later dispense gives it. Everything asked for now
		/// leaves, or nothing does. Only what left is charged.
		public async Task<HospitalResult> DispenseAsync(BusinessContext ctx, int id, int? employeeId, string? userId, DateTime nowUtc, CancellationToken ct = default,
			IReadOnlyDictionary<int, decimal>? quantities = null)
		{
			if (!await Can(ctx, HospitalActions.Pharmacy, ct)) return HospitalResult.Fail("forbidden");
			var d = await _db.HospitalDispenses.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
			if (d == null) return HospitalResult.Fail("not_found");
			if (d.Status == HospitalDispenseStatus.Dispensed) return HospitalResult.Fail("This prescription was already dispensed");
			if (!HospitalDispenseStatus.Dispensable(d.Status)) return HospitalResult.Fail("Review the prescription before dispensing");
			var settings = await SettingsAsync(ctx, ct);
			if (settings.WarehouseId is not int wh) return HospitalResult.Fail("Choose the pharmacy store in the pharmacy settings");
			var lines = await _db.HospitalDispenseLines.Where(l => l.CompanyId == ctx.CompanyId && l.DispenseId == d.ID).OrderBy(l => l.SortOrder).ToListAsync(ct);
			var stocked = lines.Where(l => l.ItemId != null).ToList();
			var (plan, planError) = stocked.Count == 0 ? (new Dictionary<int, decimal>(), null) : HospitalPartialDispenseRules.Plan(stocked, quantities);
			if (planError != null)
			{
				var over = stocked.FirstOrDefault(l => quantities != null && quantities.TryGetValue(l.ID, out var a) && a > HospitalPartialDispenseRules.Owed(l));
				return over == null ? HospitalResult.Fail(planError) : HospitalResult.Fail(planError, over.Drug, HospitalPartialDispenseRules.Owed(over).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
			}
			var itemIds = stocked.Select(l => l.ItemId!.Value).Distinct().ToList();
			var items = await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && itemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, ct);
			if (items.Count != itemIds.Count) return HospitalResult.Fail("not_found");
			// H133: a medicine the formulary withdrew (recall, shortage) does not leave the store
			var withdrawn = (await HospitalDrugMasterService.LookupAsync(_db, ctx.CompanyId, itemIds, ct)).Values.FirstOrDefault(x => x.FormularyStatus == HospitalFormularyStatus.Unavailable);
			if (withdrawn != null && items.TryGetValue(withdrawn.ItemId, out var wi))
				return HospitalResult.Fail("{0} is withdrawn from the formulary and cannot be dispensed", DisplayName.Of(wi.Name, wi.NameEn ?? wi.Name));
			// H152: a restricted or non-formulary medicine leaves only with the committee's approval for its line
			var givingNow = stocked.Where(l => plan!.TryGetValue(l.ID, out var g) && g > 0).ToList();
			var unapproved = await HospitalFormularyGovernanceService.NeedsApprovalAsync(_db, ctx, d.ID, givingNow, ct);
			if (unapproved.Count > 0 && items.TryGetValue(unapproved[0].ItemId!.Value, out var ui))
				return HospitalResult.Fail("{0} needs the formulary committee's approval for this patient", DisplayName.Of(ui.Name, ui.NameEn ?? ui.Name));
			// H153: a chemotherapy dose given by injection leaves only once its preparation is checked and released
			// (a line the store does not stock still has to be prepared and checked: nothing leaves, but the request completes)
			var notReleased = await HospitalCompoundingService.NotReleasedAsync(_db, ctx, d, lines, givingNow.Concat(lines.Where(l => l.ItemId == null)), nowUtc, ct);
			if (notReleased.Count > 0) return HospitalResult.Fail("{0} must be compounded and checked before it leaves", notReleased[0].Drug);
			var catIds = items.Values.Select(i => i.ItemCategoryId).Distinct().ToList();
			var cogs = await _db.ItemCategories.AsNoTracking().Where(c => catIds.Contains(c.ID)).ToDictionaryAsync(c => c.ID, c => c.CogsAccountId, ct);
			if (items.Values.FirstOrDefault(i => cogs.GetValueOrDefault(i.ItemCategoryId) == null) is { } noCogs)
				return HospitalResult.Fail("Map the cost-of-goods account of {0}'s inventory category first", DisplayName.Of(noCogs.Name, noCogs.NameEn ?? noCogs.Name));

			// H150: the packs scanned at the counter and not yet given leave FROM THEIR OWN BATCH (scan order); the rest of a
			// line's quantity is picked nearest-expiry-first as before
			List<HospitalDispenseScan> scanned;
			try { scanned = await _db.Set<HospitalDispenseScan>().Where(s => s.CompanyId == ctx.CompanyId && s.DispenseId == d.ID && s.FillId == null).OrderBy(s => s.ID).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { scanned = new(); }
			var taken = new List<(HospitalDispenseScan Scan, HospitalDispenseFill Fill)>();

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			try
			{
				var bill = stocked.Count == 0 ? null : BillsLikeAWardCharge(d) ? await _billing.BillForWardChargeAsync(ctx, d.PatientId, employeeId, nowUtc, ct)
					: await _billing.BillForDispenseAsync(ctx, d.PatientId, d.EncounterId, employeeId, nowUtc, ct);
				foreach (var l in stocked)
				{
					if (!plan!.TryGetValue(l.ID, out var give)) continue;   // nothing for this line now
					var it = items[l.ItemId!.Value];
					// this fill's movements are the ones after the line's last
					long before = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == ctx.CompanyId && m.SourceType == MovementSource && m.SourceId == d.ID && m.SourceLineId == l.ID)
						.Select(m => (long?)m.ID).MaxAsync(ct) ?? 0;
					var packs = scanned.Where(s => s.LineId == l.ID).Take((int)Math.Floor(give)).ToList();
					var parts = HospitalScanRules.Segments(it.TrackExpiry, packs.Select(p => p.BatchNo), give);
					foreach (var (batchNo, partQty) in parts)
					{
						var (ok, error, _) = await _stock.PostMovementAsync(ctx.CompanyId, new MovementRequest
						{
							Date = nowUtc, ItemId = it.ID, WarehouseId = wh, Direction = -1, Qty = partQty, BatchNo = batchNo,
							SourceType = MovementSource, SourceId = d.ID, SourceLineId = l.ID, PostToGl = true, CounterAccountOverride = cogs[it.ItemCategoryId],
							Notes = "Pharmacy dispense " + d.RxNo,
						}, userId);
						if (!ok) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("{0}: {1}", DisplayName.Of(it.Name, it.NameEn ?? it.Name), error ?? ""); }
					}
					// What actually left: one movement per batch when the inventory split it nearest-expiry-first.
					var all = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == ctx.CompanyId && m.SourceType == MovementSource && m.SourceId == d.ID && m.SourceLineId == l.ID).ToListAsync(ct);
					var moved = all.Where(m => m.ID > before).ToList();
					var bids = all.Where(m => m.BatchId != null).Select(m => m.BatchId!.Value).Distinct().ToList();
					var batches = await _db.StockBatches.AsNoTracking().Where(b => bids.Contains(b.ID)).ToDictionaryAsync(b => b.ID, ct);
					string? Names(IEnumerable<CrossBuy.Models.Context.Inventory.StockMovement> ms) { var s = string.Join(", ", ms.Where(m => m.BatchId != null && batches.ContainsKey(m.BatchId.Value)).Select(m => batches[m.BatchId!.Value].BatchNo).Distinct()); return s.Length == 0 ? null : s[..Math.Min(400, s.Length)]; }
					decimal qty = all.Sum(m => m.QtyBase), cost = all.Sum(m => m.TotalCost), nowQty = moved.Sum(m => m.QtyBase), nowCost = moved.Sum(m => m.TotalCost);
					var charge = await _billing.AddItemChargeAsync(ctx, bill!, it.ID, it.ItemCode, it.Name, it.NameEn ?? it.Name, give, employeeId, nowUtc, ct);
					await _db.SaveChangesAsync(ct);
					l.DispensedQty = (l.DispensedQty ?? 0) + give; l.UnitPrice = charge.UnitPrice; l.UnitCost = qty == 0 ? 0 : Math.Round(cost / qty, 4);
					l.Batches = Names(all);
					l.NearestExpiry = all.Where(m => m.BatchId != null && batches.ContainsKey(m.BatchId.Value)).Select(m => batches[m.BatchId!.Value].ExpiryDate).Where(x => x != null).Min();
					l.BillLineId ??= charge.ID;
					var fill = new HospitalDispenseFill { CompanyId = ctx.CompanyId, DispenseId = d.ID, LineId = l.ID, Quantity = give, UnitCost = nowQty == 0 ? 0 : Math.Round(nowCost / nowQty, 4),
						UnitPrice = charge.UnitPrice, Batches = Names(moved), BillLineId = charge.ID, At = nowUtc, ByEmployeeId = employeeId };
					_db.Add(fill);
					taken.AddRange(packs.Select(p => (p, fill)));
				}
				d.Status = HospitalPartialDispenseRules.StatusAfter(stocked); d.DispensedAt = nowUtc; d.DispensedByEmployeeId = employeeId; d.WarehouseId = wh; d.BillId ??= bill?.ID;
				await _db.SaveChangesAsync(ct);
				if (taken.Count > 0) { foreach (var (scan, fill) in taken) scan.FillId = fill.ID; await _db.SaveChangesAsync(ct); }   // H150: the packs this fill gave
				await tx.CommitAsync();
				return HospitalResult.Success(d.ID);
			}
			catch
			{
				await tx.RollbackAsync(); _db.ChangeTracker.Clear();
				throw;
			}
		}
	}
}
