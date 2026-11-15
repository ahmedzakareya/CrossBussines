using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H21 — medication routing: an inpatient order (H14) carried to the bedside in six stamped steps.
	//
	//   · Raised by the pharmacy (`pharmacy`) for an active order: priority, how it travels, how many units.
	//   · Verified, prepared and dispatched by the pharmacy; received and administered by the ward (`admit`).
	//   · Preparing issues the stock from the pharmacy store (the warehouse of H10's settings) through the
	//     inventory's single stock writer (IStockService — nearest expiry first, the cost posted to the category's
	//     cost of goods). From the ward cabinet the ward's receipt issues it instead (prepare and dispatch are not
	//     needed). A drug the pharmacy does not stock is tracked, never issued nor charged.
	//   · Administering needs the wristband scan, writes the dose through H14's MAR (RecordAsync — its rules, its
	//     witness, its one-answer-per-dose index) and adds the charge to the STAY's bill at the patient's price.
	//   · Every step is conditional on the stage it starts from, in one transaction: two hands cannot both do it.
	//   · H131: cancelling a dose the store issued but nobody gave returns it to the store it left, movement by movement
	//     (the same batch, quantity and cost, the cost back off cost of goods), in the cancel's own transaction.
	//   · Not here yet:
	//     and the ward cabinet as a store of its own (it issues from the pharmacy store).
	// ==========================================================================================

	public sealed class HospitalMedDeliveryInput
	{
		public int MedOrderId { get; set; }
		public string Priority { get; set; } = HospitalMedPriority.Routine;
		public string? Via { get; set; }
		public decimal Quantity { get; set; }
	}

	public sealed class HospitalMedStepInput
	{
		public byte Stage { get; set; }
		public int? WitnessEmployeeId { get; set; }
		public string? Wristband { get; set; }
	}

	public sealed record HospitalMedPerson(int Id, string Name, string NameEn, string? Photo);

	public sealed record HospitalMedStep(byte Stage, DateTime? At, HospitalMedPerson? By, bool Skipped);

	public sealed record HospitalMedDeliveryRow(HospitalMedDelivery Delivery, string Drug, string Dose, string Route, bool Controlled, bool Stocked,
		int PatientId, string PatientName, string PatientNameEn, string Mrn, string? PatientPhoto, string? Bed, IReadOnlyList<HospitalMedStep> Steps, bool Late);

	public sealed record HospitalMedOrderChoice(int OrderId, string Drug, string Dose, string Route, bool Controlled, bool Stocked, string PatientName, string PatientNameEn, string Mrn, string? Photo, string? Bed);

	public sealed record HospitalMedBoard(IReadOnlyList<HospitalMedDeliveryRow> Rows, int[] StageCounts, int Active, int? StatTurnaround, int? RoutineTurnaround, int Late,
		IReadOnlyList<HospitalMedOrderChoice> Orders);

	public sealed class HospitalMedRoutingService
	{
		public const string MovementSource = "HospitalMedDelivery";
		/// H131: the stock that came back from a cancelled delivery (found again by the delivery).
		public const string ReturnSource = "HospitalMedReturn";

		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IStockService _stock;
		private readonly HospitalBillingService _billing;

		public HospitalMedRoutingService(CrossDbContext db, IHospitalAccessService access, IStockService stock, HospitalBillingService billing)
		{ _db = db; _access = access; _stock = stock; _billing = billing; }

		public const string ConcurrentChange = HospitalInsuranceService.ConcurrentChange;
		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalMedDelivery> Deliveries(BusinessContext ctx) => _db.Set<HospitalMedDelivery>().Where(d => d.CompanyId == ctx.CompanyId);

		/// The bed each active admission lies in now (its open bed stay).
		private async Task<Dictionary<int, string>> BedsAsync(BusinessContext ctx, IEnumerable<int> admissions, CancellationToken ct)
		{
			var ids = admissions.Distinct().ToList();
			return (await (from s in _db.HospitalBedStays.AsNoTracking() join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
						   where s.CompanyId == ctx.CompanyId && s.EndedAt == null && ids.Contains(s.AdmissionId) select new { s.AdmissionId, b.Code }).ToListAsync(ct))
				.GroupBy(x => x.AdmissionId).ToDictionary(g => g.Key, g => g.First().Code);
		}

		public async Task<HospitalMedBoard> BoardAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			var dayStart = HospitalNursingService.WardToUtc(HospitalNursingService.UtcToWard(nowUtc).Date);
			var rows = await Deliveries(ctx).AsNoTracking()
				.Where(d => d.CancelledAt == null && (d.Stage < HospitalMedStage.Administered || d.AdministeredAt >= dayStart))
				.OrderByDescending(d => d.Priority == HospitalMedPriority.Stat).ThenBy(d => d.Stage == HospitalMedStage.Administered).ThenBy(d => d.PrescribedAt).ToListAsync(ct);
			var orderIds = rows.Select(r => r.MedOrderId).ToList();
			var active = await _db.Set<HospitalMedOrder>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.Status == HospitalMedOrderStatus.Active).ToListAsync(ct);
			var open = await Deliveries(ctx).AsNoTracking().Where(d => d.CancelledAt == null && d.Stage < HospitalMedStage.Administered).Select(d => d.MedOrderId).ToListAsync(ct);
			var choices = active.Where(o => !open.Contains(o.ID)).ToList();
			var allOrders = orderIds.Concat(choices.Select(o => o.ID)).Distinct().ToList();
			var orders = await _db.Set<HospitalMedOrder>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && allOrders.Contains(o.ID)).ToDictionaryAsync(o => o.ID, ct);
			// only the orders of patients still admitted can be raised
			var admitted = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.Status == HospitalAdmissionStatus.Active).Select(a => a.ID).ToListAsync(ct);
			choices = choices.Where(o => admitted.Contains(o.AdmissionId)).ToList();
			var patientIds = orders.Values.Select(o => o.PatientId).Distinct().ToList();
			var patients = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && patientIds.Contains(p.ID))
				.ToDictionaryAsync(p => p.ID, p => new { p.Name, p.NameEn, p.Mrn, p.PhotoPath }, ct);
			var beds = await BedsAsync(ctx, orders.Values.Select(o => o.AdmissionId), ct);
			var people = await PeopleAsync(ctx, rows.SelectMany(r => new[] { r.PrescribedByEmployeeId, r.VerifiedByEmployeeId, r.PreparedByEmployeeId, r.DispatchedByEmployeeId, r.ReceivedByEmployeeId, r.AdministeredByEmployeeId }), ct);

			var list = rows.Select(r =>
			{
				var o = orders[r.MedOrderId]; var p = patients[r.PatientId];
				var at = new DateTime?[] { r.PrescribedAt, r.VerifiedAt, r.PreparedAt, r.DispatchedAt, r.ReceivedAt, r.AdministeredAt };
				var by = new int?[] { r.PrescribedByEmployeeId, r.VerifiedByEmployeeId, r.PreparedByEmployeeId, r.DispatchedByEmployeeId, r.ReceivedByEmployeeId, r.AdministeredByEmployeeId };
				var steps = Enumerable.Range(0, 6).Select(i => new HospitalMedStep((byte)i, at[i], by[i] is int e ? people.GetValueOrDefault(e) : null, HospitalMedRoutingRules.Skips(r.Via, (byte)i))).ToList();
				return new HospitalMedDeliveryRow(r, o.Drug, o.Dose, o.Route, o.Controlled, o.ItemId != null, r.PatientId, p.Name, p.NameEn, p.Mrn, p.PhotoPath, beds.GetValueOrDefault(r.AdmissionId), steps,
					HospitalMedRoutingRules.IsLate(r.Priority, r.PrescribedAt, r.AdministeredAt, r.CancelledAt, nowUtc));
			}).ToList();
			var counts = new int[6];
			foreach (var r in rows) counts[r.Stage]++;
			var given = rows.Where(r => r.AdministeredAt != null).ToList();
			int? Avg(Func<HospitalMedDelivery, bool> f) => HospitalMedRoutingRules.Turnaround(given.Where(f).Select(r => (r.PrescribedAt, r.AdministeredAt!.Value)));
			var orderChoices = choices.Where(o => patients.ContainsKey(o.PatientId)).Select(o =>
			{
				var p = patients[o.PatientId];
				return new HospitalMedOrderChoice(o.ID, o.Drug, o.Dose, o.Route, o.Controlled, o.ItemId != null, p.Name, p.NameEn, p.Mrn, p.PhotoPath, beds.GetValueOrDefault(o.AdmissionId));
			}).ToList();
			return new HospitalMedBoard(list, counts, rows.Count(r => r.Stage < HospitalMedStage.Administered), Avg(r => r.Priority == HospitalMedPriority.Stat),
				Avg(r => r.Priority == HospitalMedPriority.Routine), list.Count(r => r.Late), orderChoices);
		}

		private async Task<Dictionary<int, HospitalMedPerson>> PeopleAsync(BusinessContext ctx, IEnumerable<int?> ids, CancellationToken ct)
		{
			var list = ids.Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
			var rows = await _db.Employee.AsNoTracking().Where(e => list.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, rows.Select(e => e.ID), ct);
			return rows.ToDictionary(e => e.ID, e => new HospitalMedPerson(e.ID, e.FullName ?? "", e.FullNameEn ?? e.FullName ?? "", photos.GetValueOrDefault(e.ID)));
		}

		/// Raises a delivery for an active order of an admitted patient (one open delivery per order).
		public async Task<HospitalResult> CreateAsync(BusinessContext ctx, HospitalMedDeliveryInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Pharmacy, ct)) return HospitalResult.Fail("forbidden");
			var o = await _db.Set<HospitalMedOrder>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.MedOrderId && x.CompanyId == ctx.CompanyId, ct);
			if (o == null) return HospitalResult.Fail("not_found");
			if (o.Status != HospitalMedOrderStatus.Active) return HospitalResult.Fail("This order is stopped");
			if (!await _db.HospitalAdmissions.AnyAsync(a => a.ID == o.AdmissionId && a.CompanyId == ctx.CompanyId && a.Status == HospitalAdmissionStatus.Active, ct))
				return HospitalResult.Fail("The patient is no longer admitted");
			string via = input.Via ?? HospitalMedRoutingRules.SuggestedVia(input.Priority, o.Controlled);
			var refusal = HospitalMedRoutingRules.CreateRefusal(input.Priority, via, o.Controlled, input.Quantity, o.ItemId != null);
			if (refusal != null) return HospitalResult.Fail(refusal);
			if (await Deliveries(ctx).AnyAsync(d => d.MedOrderId == o.ID && d.CancelledAt == null && d.Stage < HospitalMedStage.Administered, ct))
				return HospitalResult.Fail("This order already has a delivery on its way");
			var nos = await Deliveries(ctx).Select(d => d.DeliveryNo).ToListAsync(ct);
			var d = new HospitalMedDelivery
			{
				CompanyId = ctx.CompanyId, DeliveryNo = HospitalDentalPracticeRules.NextNo(nos, "MR-", 6), MedOrderId = o.ID, AdmissionId = o.AdmissionId, PatientId = o.PatientId,
				Priority = input.Priority, Via = via, Quantity = o.ItemId == null ? 0 : input.Quantity, Stage = HospitalMedStage.Prescribed,
				PrescribedAt = nowUtc, PrescribedByEmployeeId = o.OrderedByEmployeeId ?? employeeId,
			};
			_db.Add(d);
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(ConcurrentChange); }
			return HospitalResult.Success(d.ID);
		}

		/// Does the next step of a delivery.
		public async Task<HospitalResult> StepAsync(BusinessContext ctx, int id, HospitalMedStepInput input, int? employeeId, string? userId, DateTime nowUtc, CancellationToken ct = default)
		{
			var d = await Deliveries(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (d == null) return HospitalResult.Fail("not_found");
			byte next = input.Stage;
			bool ward = next is HospitalMedStage.Received or HospitalMedStage.Administered;
			if (!await Can(ctx, ward ? HospitalActions.Admit : HospitalActions.Pharmacy, ct)) return HospitalResult.Fail("forbidden");
			var o = await _db.Set<HospitalMedOrder>().AsNoTracking().FirstAsync(x => x.ID == d.MedOrderId && x.CompanyId == ctx.CompanyId, ct);
			var refusal = HospitalMedRoutingRules.StepRefusal(d.Stage, next, d.Via, d.CancelledAt != null, o.Controlled, employeeId, input.WitnessEmployeeId);
			if (refusal != null) return HospitalResult.Fail(refusal);
			if (input.WitnessEmployeeId is int w && !await _db.Employee.AnyAsync(e => e.ID == w && e.EmpCompanyID == ctx.CompanyId && e.IsActive, ct)) return HospitalResult.Fail("not_found");
			if (ward && !await _db.HospitalAdmissions.AnyAsync(a => a.ID == d.AdmissionId && a.CompanyId == ctx.CompanyId && a.Status == HospitalAdmissionStatus.Active, ct))
				return HospitalResult.Fail("The patient is no longer admitted");
			if (next == HospitalMedStage.Administered)
			{
				var mrn = await _db.HospitalPatients.AsNoTracking().Where(p => p.ID == d.PatientId && p.CompanyId == ctx.CompanyId).Select(p => p.Mrn).FirstAsync(ct);
				if (!HospitalMedRoutingRules.WristbandMatches(input.Wristband, mrn)) return HospitalResult.Fail("The wristband does not match this patient");
			}
			// the stock leaves when it is prepared, or, from the ward cabinet, when the ward receives it
			bool issue = o.ItemId != null && d.Quantity > 0 && (next == HospitalMedStage.Prepared || (next == HospitalMedStage.Received && d.Via == HospitalMedVia.Cabinet));

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			try
			{
				// taken first, conditionally: only one hand does a step
				int n = await Deliveries(ctx).Where(x => x.ID == id && x.Stage == d.Stage && x.CancelledAt == null).ExecuteUpdateAsync(u => u.SetProperty(x => x.Stage, next), ct);
				if (n != 1) { await tx.RollbackAsync(); return HospitalResult.Fail(ConcurrentChange); }
				var row = await Deliveries(ctx).FirstAsync(x => x.ID == id, ct);
				switch (next)
				{
					case HospitalMedStage.Verified: row.VerifiedAt = nowUtc; row.VerifiedByEmployeeId = employeeId; break;
					case HospitalMedStage.Prepared: row.PreparedAt = nowUtc; row.PreparedByEmployeeId = employeeId; break;
					case HospitalMedStage.Dispatched: row.DispatchedAt = nowUtc; row.DispatchedByEmployeeId = employeeId; break;
					case HospitalMedStage.Received: row.ReceivedAt = nowUtc; row.ReceivedByEmployeeId = employeeId; row.ReceivedWitnessEmployeeId = input.WitnessEmployeeId; break;
					case HospitalMedStage.Administered: row.AdministeredAt = nowUtc; row.AdministeredByEmployeeId = employeeId; break;
				}
				if (issue)
				{
					var why = await IssueAsync(ctx, row, o.ItemId!.Value, userId, nowUtc, ct);
					if (why != null) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return why; }
				}
				if (next == HospitalMedStage.Administered)
				{
					var why = await AdministerAsync(ctx, row, o, input.WitnessEmployeeId, employeeId, nowUtc, ct);
					if (why != null) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return why; }
				}
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync();
				return HospitalResult.Success(id);
			}
			catch
			{
				await tx.RollbackAsync(); _db.ChangeTracker.Clear();
				throw;
			}
		}

		/// The stock leaves the pharmacy store through the inventory's one writer, nearest expiry first.
		private async Task<HospitalResult?> IssueAsync(BusinessContext ctx, HospitalMedDelivery row, int itemId, string? userId, DateTime nowUtc, CancellationToken ct)
		{
			var wh = await _db.Set<HospitalPharmacySettings>().AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId).Select(s => s.WarehouseId).FirstOrDefaultAsync(ct);
			if (wh is not int store) return HospitalResult.Fail("Choose the pharmacy store in the pharmacy settings");
			var it = await _db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == itemId && i.CompanyID == ctx.CompanyId, ct);
			if (it == null) return HospitalResult.Fail("not_found");
			var cogs = await _db.ItemCategories.AsNoTracking().Where(c => c.ID == it.ItemCategoryId).Select(c => c.CogsAccountId).FirstOrDefaultAsync(ct);
			if (cogs == null) return HospitalResult.Fail("Map the cost-of-goods account of {0}'s inventory category first", DisplayName.Of(it.Name, it.NameEn ?? it.Name));
			var (ok, error, _) = await _stock.PostMovementAsync(ctx.CompanyId, new MovementRequest
			{
				Date = nowUtc, ItemId = it.ID, WarehouseId = store, Direction = -1, Qty = row.Quantity,
				SourceType = MovementSource, SourceId = row.ID, PostToGl = true, CounterAccountOverride = cogs, Notes = "Medication delivery " + row.DeliveryNo,
			}, userId);
			if (!ok) return HospitalResult.Fail("{0}: {1}", DisplayName.Of(it.Name, it.NameEn ?? it.Name), error ?? "");
			var moved = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == ctx.CompanyId && m.SourceType == MovementSource && m.SourceId == row.ID).ToListAsync(ct);
			var bids = moved.Where(m => m.BatchId != null).Select(m => m.BatchId!.Value).ToList();
			var batches = string.Join(", ", await _db.StockBatches.AsNoTracking().Where(b => bids.Contains(b.ID)).Select(b => b.BatchNo).ToListAsync(ct));
			decimal qty = moved.Sum(m => m.QtyBase), cost = moved.Sum(m => m.TotalCost);
			row.WarehouseId = store; row.UnitCost = qty == 0 ? 0 : Math.Round(cost / qty, 4);
			row.Batches = batches.Length == 0 ? null : batches[..Math.Min(400, batches.Length)];
			return null;
		}

		/// The dose through H14's MAR, and the charge on the stay's bill.
		private async Task<HospitalResult?> AdministerAsync(BusinessContext ctx, HospitalMedDelivery row, HospitalMedOrder o, int? witness, int? employeeId, DateTime nowUtc, CancellationToken ct)
		{
			DateTime? due = null;
			if (!o.AsNeeded)
			{
				var hours = HospitalNursingRules.ParseHours(o.Hours) ?? Array.Empty<int>();
				var today = HospitalNursingRules.DosesOn(HospitalNursingService.UtcToWard(nowUtc).Date, hours, o.StartAt, o.StoppedAt, HospitalNursingService.WardToUtc);
				var recorded = await _db.Set<HospitalMarEntry>().AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && m.OrderId == o.ID && m.DueAt != null).Select(m => m.DueAt!.Value).ToListAsync(ct);
				due = HospitalMedRoutingRules.DoseToAnswer(today, recorded, nowUtc);
				if (due == null) return HospitalResult.Fail("No dose of this order is due now");
			}
			var mar = await new HospitalNursingService(_db, _access).RecordAsync(ctx, new HospitalMarInput { OrderId = o.ID, DueAt = due, Status = HospitalMarStatus.Given, WitnessEmployeeId = witness }, employeeId, nowUtc, ct);
			if (!mar.Ok) return mar;
			row.MarEntryId = mar.Id;
			if (o.ItemId is int itemId && row.Quantity > 0)
			{
				var billRes = await _billing.EnsureAdmissionBillAsync(ctx, row.AdmissionId, employeeId, nowUtc, ct);
				if (!billRes.Ok) return billRes;
				var bill = await _db.HospitalBills.FirstAsync(b => b.ID == billRes.Id && b.CompanyId == ctx.CompanyId, ct);
				if (bill.Status != HospitalBillStatus.Open) return HospitalResult.Fail("The stay's bill is closed");
				var it = await _db.Items.AsNoTracking().FirstAsync(i => i.ID == itemId && i.CompanyID == ctx.CompanyId, ct);
				var charge = await _billing.AddItemChargeAsync(ctx, bill, it.ID, it.ItemCode, it.Name, it.NameEn ?? it.Name, row.Quantity, employeeId, nowUtc, ct);
				await _db.SaveChangesAsync(ct);
				row.BillLineId = charge.ID;
			}
			return null;
		}

		/// Cancels a delivery not yet given. If the store already issued it (H131), the dose returns to the store it left in the
		/// same transaction — every movement replayed in: its batch, its quantity, its cost, the cost back off cost of goods —
		/// or the cancel does not happen.
		public async Task<HospitalResult> CancelAsync(BusinessContext ctx, int id, string? reason, int? employeeId, string? userId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Pharmacy, ct)) return HospitalResult.Fail("forbidden");
			var d = await Deliveries(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (d == null) return HospitalResult.Fail("not_found");
			if (HospitalMedRoutingRules.CancelRefusal(d.Stage, d.CancelledAt != null) is string why) return HospitalResult.Fail(why);
			if (string.IsNullOrWhiteSpace(reason)) return HospitalResult.Fail("Write why it is cancelled");
			string r = reason.Trim()[..Math.Min(300, reason.Trim().Length)];
			bool returns = HospitalMedRoutingRules.Returns(d.WarehouseId);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			try
			{
				int n = await Deliveries(ctx).Where(x => x.ID == id && x.CancelledAt == null && x.Stage < HospitalMedStage.Administered && (x.WarehouseId != null) == returns).ExecuteUpdateAsync(u => u
					.SetProperty(x => x.CancelledAt, nowUtc).SetProperty(x => x.CancelledByEmployeeId, employeeId).SetProperty(x => x.CancelReason, r)
					.SetProperty(x => x.ReturnedAt, returns ? nowUtc : (DateTime?)null), ct);
				if (n != 1) { await tx.RollbackAsync(); return HospitalResult.Fail(ConcurrentChange); }
				if (returns && await ReturnAsync(ctx, d, userId, nowUtc, ct) is { } refused) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return refused; }
				await tx.CommitAsync();
				return HospitalResult.Success(id);
			}
			catch { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); throw; }
		}

		/// H131: the issued dose goes back into the store it left — each outgoing movement replayed in, with its own batch
		/// (and expiry), quantity and unit cost; the cost-of-goods account it was charged to is the counter, so the value
		/// returns exactly. Null when everything came back.
		private async Task<HospitalResult?> ReturnAsync(BusinessContext ctx, HospitalMedDelivery d, string? userId, DateTime nowUtc, CancellationToken ct)
		{
			var outs = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == ctx.CompanyId && m.SourceType == MovementSource && m.SourceId == d.ID && m.Direction == -1).OrderBy(m => m.ID).ToListAsync(ct);
			if (outs.Count == 0) return null;   // nothing actually left (an unstocked drug): nothing to bring back
			var itemIds = outs.Select(m => m.ItemId).Distinct().ToList();
			var items = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID) && i.CompanyID == ctx.CompanyId).ToDictionaryAsync(i => i.ID, ct);
			var catIds = items.Values.Select(i => i.ItemCategoryId).Distinct().ToList();
			var cogs = await _db.ItemCategories.AsNoTracking().Where(c => catIds.Contains(c.ID)).ToDictionaryAsync(c => c.ID, c => c.CogsAccountId, ct);
			var bids = outs.Where(m => m.BatchId != null).Select(m => m.BatchId!.Value).ToList();
			var batches = await _db.StockBatches.AsNoTracking().Where(b => bids.Contains(b.ID)).ToDictionaryAsync(b => b.ID, ct);
			foreach (var m in outs)
			{
				var it = items[m.ItemId];
				var b = m.BatchId is int bid && batches.TryGetValue(bid, out var bb) ? bb : null;
				var (ok, error, _) = await _stock.PostMovementAsync(ctx.CompanyId, new MovementRequest
				{
					Date = nowUtc, ItemId = m.ItemId, WarehouseId = m.WarehouseId, Direction = 1, Qty = m.QtyBase, UnitCostInBase = m.UnitCost,
					BatchNo = b?.BatchNo, Expiry = b?.ExpiryDate, AllowExpired = true,
					SourceType = ReturnSource, SourceId = d.ID, SourceLineId = m.ID, PostToGl = true, CounterAccountOverride = cogs.GetValueOrDefault(it.ItemCategoryId),
					Notes = "Medication returned " + d.DeliveryNo,
				}, userId);
				if (!ok) return HospitalResult.Fail("{0}: {1}", DisplayName.Of(it.Name, it.NameEn ?? it.Name), error ?? "");
			}
			return null;
		}
	}
}
