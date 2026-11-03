using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H12 — estimates and packages.
	//
	//   · Packages (`manage`): a procedure at a fixed price, its main service, its included nights and room class.
	//   · Estimates (`bill`): priced exactly as the patient's bill would be — the patient's H7 list
	//     (HospitalPricingService.ListForPatientAsync / QuoteServiceAsync / QuoteRoomRateAsync), split by the
	//     patient's cover today with H11's one split rule (HospitalEstimateRules.Totals → HospitalInsuranceRules.Split).
	//     A draft can be changed; issuing freezes it and, when the payer's contract asks for approval of what it
	//     contains, files the approval request through H11 (HospitalInsuranceService.RequestPreauthAsync).
	//   · Nothing here moves money: the deposit is stated, collecting it is billing's (not part of H12).
	//     The entities reach the model through HospitalEstimatesModel.Configure (wired in CrossDbContext).
	// ==========================================================================================

	public sealed class HospitalPackageInput
	{
		public int? Id { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public int ServiceId { get; set; }
		public decimal Price { get; set; }
		public int IncludedNights { get; set; }
		public int? RoomTypeId { get; set; }
		public string? Inclusions { get; set; }
		public string? InclusionsEn { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public sealed class HospitalEstimateServiceInput { public int ServiceId { get; set; } public decimal Quantity { get; set; } = 1; }

	public sealed class HospitalEstimateInput
	{
		public int? Id { get; set; }
		public int PatientId { get; set; }
		public int? PackageId { get; set; }
		public int? RoomTypeId { get; set; }
		public int ExpectedNights { get; set; }
		public decimal MarginPct { get; set; } = 20;
		public string? Note { get; set; }
		public List<HospitalEstimateServiceInput> Services { get; set; } = new();
	}

	public sealed record HospitalPackageRow(HospitalPackage Package, string ServiceCode, string Service, string ServiceEn, string? RoomType);

	/// A priced estimate, stored or not: the cover it was priced with and every line with the payer's part.
	public sealed record HospitalEstimateQuote(IReadOnlyList<HospitalEstimateDraftLine> Lines, HospitalEstimateTotals Totals, HospitalPatientPolicy? Policy, HospitalPayer? Payer,
		string? Ineligible, bool NeedsPreauth, string? Currency);

	public sealed record HospitalEstimateRow(HospitalEstimate Estimate, string PatientName, string PatientNameEn, string Mrn, string? Photo, string? Package, string? PackageEn, string? Payer, string? PayerEn);

	public sealed record HospitalEstimateView(HospitalEstimateRow Row, IReadOnlyList<HospitalEstimateLine> Lines, string? PreauthNo, string? PreauthStatus, bool Expired);

	public sealed class HospitalEstimateService
	{
		private const int DefaultDecimals = 3;
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalInsuranceService _insurance;
		private readonly HospitalPricingService? _pricing;

		public HospitalEstimateService(CrossDbContext db, IHospitalAccessService access, HospitalInsuranceService insurance, HospitalPricingService? pricing = null)
		{ _db = db; _access = access; _insurance = insurance; _pricing = pricing; }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalPackage> Packages(BusinessContext ctx) => _db.Set<HospitalPackage>().Where(p => p.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalEstimate> Estimates(BusinessContext ctx) => _db.Set<HospitalEstimate>().Where(e => e.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalEstimateLine> Lines(BusinessContext ctx) => _db.Set<HospitalEstimateLine>().Where(l => l.CompanyId == ctx.CompanyId);
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		// ====================================================================================
		// PACKAGES
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalPackageRow>> PackagesAsync(BusinessContext ctx, bool activeOnly, CancellationToken ct = default)
		{
			var q = from p in Packages(ctx).AsNoTracking()
					join s in _db.HospitalServices.AsNoTracking() on p.ServiceId equals s.ID
					where s.CompanyId == ctx.CompanyId && (!activeOnly || p.IsActive)
					orderby p.Code
					select new { p, s.Code, s.Name, s.NameEn };
			var rows = await q.ToListAsync(ct);
			var types = await _db.HospitalRoomTypes.AsNoTracking().Where(t => t.CompanyId == ctx.CompanyId).Select(t => new { t.ID, t.Name, t.NameEn }).ToListAsync(ct);
			return rows.Select(r =>
			{
				var t = types.FirstOrDefault(x => x.ID == r.p.RoomTypeId);
				return new HospitalPackageRow(r.p, r.Code, r.Name, r.NameEn, t == null ? null : DisplayName.Of(t.Name, t.NameEn ?? t.Name));
			}).ToList();
		}

		public async Task<HospitalResult> SavePackageAsync(BusinessContext ctx, HospitalPackageInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			var code = (input.Code ?? "").Trim().ToUpperInvariant();
			if (code.Length is 0 or > 20) return HospitalResult.Fail("Enter a code of up to 20 characters");
			if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.NameEn)) return HospitalResult.Fail("Enter the name in both languages");
			if (input.Price < 0) return HospitalResult.Fail("A price cannot be negative");
			if (input.IncludedNights is < 0 or > 365) return HospitalResult.Fail("The included nights are 0 to 365");
			if (!await _db.HospitalServices.AnyAsync(s => s.ID == input.ServiceId && s.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (input.RoomTypeId is int rt && !await _db.HospitalRoomTypes.AnyAsync(t => t.ID == rt && t.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			int self = input.Id ?? 0;
			if (await Packages(ctx).AnyAsync(p => p.Code == code && p.ID != self, ct)) return HospitalResult.Fail("Another package uses this code");
			HospitalPackage? p;
			if (input.Id is int id)
			{
				p = await Packages(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (p == null) return HospitalResult.Fail("not_found");
			}
			else { p = new HospitalPackage { CompanyId = ctx.CompanyId, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId }; _db.Set<HospitalPackage>().Add(p); }
			p.Code = code; p.Name = input.Name.Trim(); p.NameEn = input.NameEn.Trim(); p.ServiceId = input.ServiceId; p.Price = input.Price;
			p.IncludedNights = input.IncludedNights; p.RoomTypeId = input.RoomTypeId; p.Inclusions = Clip(input.Inclusions, 1000); p.InclusionsEn = Clip(input.InclusionsEn, 1000); p.IsActive = input.IsActive;
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalInsuranceService.ConcurrentChange); }
			return HospitalResult.Success(p.ID);
		}

		// ====================================================================================
		// PRICING AN ESTIMATE (no write)
		// ====================================================================================

		/// Prices an estimate as the patient's bill would be priced today, split by the patient's cover today.
		/// Returns the refusal instead when something it needs is missing.
		public async Task<(HospitalEstimateQuote? Quote, HospitalResult? Refusal)> QuoteAsync(BusinessContext ctx, HospitalEstimateInput input, DateTime nowUtc, CancellationToken ct = default)
		{
			if (input.ExpectedNights is < 0 or > 365) return (null, HospitalResult.Fail("The expected nights are 0 to 365"));
			if (input.MarginPct is < 0 or > 200) return (null, HospitalResult.Fail("The deposit margin is 0 to 200%"));
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId, ct)) return (null, HospitalResult.Fail("not_found"));
			HospitalPackage? package = null;
			if (input.PackageId is int pid)
			{
				package = await Packages(ctx).AsNoTracking().FirstOrDefaultAsync(p => p.ID == pid && p.IsActive, ct);
				if (package == null) return (null, HospitalResult.Fail("not_found"));
			}
			var serviceIds = input.Services.Select(s => s.ServiceId).Distinct().ToList();
			if (input.Services.Any(s => s.Quantity <= 0)) return (null, HospitalResult.Fail("A quantity must be more than zero"));
			var services = await _db.HospitalServices.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.IsActive && (serviceIds.Contains(s.ID) || (package != null && s.ID == package.ServiceId))).ToListAsync(ct);
			if (serviceIds.Any(id => services.All(s => s.ID != id))) return (null, HospitalResult.Fail("not_found"));
			if (package == null && input.Services.Count == 0) return (null, HospitalResult.Fail("Choose a package or at least one service"));

			// the list the patient's bill would use today, its currency and decimals
			var list = _pricing == null ? null : await _pricing.ListForPatientAsync(ctx, input.PatientId, nowUtc, ct);
			if (package != null && list?.CurrencyId != null) return (null, HospitalResult.Fail("A package is priced in the facility's currency; this patient's price list is in another"));
			var currency = _pricing == null ? null : await _pricing.CurrencyOfAsync(ctx, list?.CurrencyId, ct);
			int decimals = currency?.Decimals ?? DefaultDecimals;

			int? roomTypeId = input.RoomTypeId ?? package?.RoomTypeId;
			HospitalRoomType? room = null;
			if (roomTypeId is int rid)
			{
				room = await _db.HospitalRoomTypes.AsNoTracking().FirstOrDefaultAsync(t => t.ID == rid && t.CompanyId == ctx.CompanyId, ct);
				if (room == null) return (null, HospitalResult.Fail("not_found"));
			}
			else if (input.ExpectedNights > 0) return (null, HospitalResult.Fail("Choose the room class for the stay"));
			decimal nightly = room == null ? 0 : _pricing == null ? room.DailyRate : await _pricing.QuoteRoomRateAsync(ctx, list?.ID, room.DailyRate, ct);

			async Task<(decimal Price, bool Outside)> Quote(HospitalService s)
			{
				if (_pricing == null) return (s.Price, false);
				var q = await _pricing.QuoteServiceAsync(ctx, list?.ID, s, ct);
				return (q.Price, q.OutsideList);
			}
			bool packageCovered = true;
			if (package != null) packageCovered = !(await Quote(services.First(s => s.ID == package.ServiceId))).Outside;
			var extra = new List<HospitalEstimateDraftLine>();
			foreach (var line in input.Services)
			{
				var s = services.First(x => x.ID == line.ServiceId);
				var (price, outside) = await Quote(s);
				extra.Add(new(HospitalEstimateRules.KindService, s.ID, s.Code, s.Category, s.Name, s.NameEn, line.Quantity, price, 0, !outside));
			}
			var lines = HospitalEstimateRules.Lines(package, packageCovered, room?.Code, nightly, input.ExpectedNights, extra, decimals);

			var (policy, payer, why) = await _insurance.EligibilityAsync(ctx, input.PatientId, nowUtc, null, ct);
			bool insured = why == null && payer != null;
			var totals = HospitalEstimateRules.Totals(lines, payer?.CopayPct ?? 0, payer?.CopayCap ?? 0, insured, input.MarginPct, decimals);
			var cats = insured ? HospitalInsuranceRules.PreauthCategories(payer!.PreauthCategories) : new HashSet<string>();
			bool needsPreauth = insured && lines.Any(l => l.Covered && cats.Contains(l.Category));
			return (new HospitalEstimateQuote(lines, totals, insured ? policy : null, insured ? payer : null, why, needsPreauth, currency?.Code), null);
		}

		// ====================================================================================
		// ESTIMATES
		// ====================================================================================

		private async Task<string> NextNoAsync(BusinessContext ctx, CancellationToken ct)
		{
			var nos = await Estimates(ctx).Where(e => e.EstimateNo.StartsWith("EST-")).Select(e => e.EstimateNo).ToListAsync(ct);
			int n = 0;
			foreach (var no in nos) if (int.TryParse(no.AsSpan(4), out var v) && v > n) n = v;
			return "EST-" + (n + 1).ToString("000000");
		}

		/// Saves a draft (new, or the same draft re-priced): the lines are replaced, the cover is today's.
		public async Task<HospitalResult> SaveDraftAsync(BusinessContext ctx, HospitalEstimateInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			var (quote, refusal) = await QuoteAsync(ctx, input, nowUtc, ct);
			if (refusal != null) return refusal;
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			try
			{
				HospitalEstimate? e;
				if (input.Id is int id)
				{
					e = await Estimates(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
					if (e == null) { await tx.RollbackAsync(); return HospitalResult.Fail("not_found"); }
					if (e.Status != HospitalEstimateStatus.Draft) { await tx.RollbackAsync(); return HospitalResult.Fail("An issued estimate cannot change; make a new one"); }
					if (e.PatientId != input.PatientId) { await tx.RollbackAsync(); return HospitalResult.Fail("An estimate stays with its patient"); }
					await Lines(ctx).Where(l => l.EstimateId == e.ID).ExecuteDeleteAsync(ct);
				}
				else
				{
					e = new HospitalEstimate { CompanyId = ctx.CompanyId, EstimateNo = await NextNoAsync(ctx, ct), PatientId = input.PatientId, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId };
					_db.Set<HospitalEstimate>().Add(e);
				}
				var q = quote!;
				e.PackageId = input.PackageId; e.RoomTypeId = input.RoomTypeId; e.ExpectedNights = input.ExpectedNights; e.MarginPct = input.MarginPct; e.Note = Clip(input.Note, 500);
				e.PolicyId = q.Policy?.ID; e.PayerId = q.Payer?.ID;
				e.Gross = q.Totals.Gross; e.PayerShare = q.Totals.PayerShare; e.PatientShare = q.Totals.PatientShare; e.Deposit = q.Totals.Deposit;
				await _db.SaveChangesAsync(ct);
				for (int i = 0; i < q.Lines.Count; i++)
				{
					var l = q.Lines[i];
					_db.Set<HospitalEstimateLine>().Add(new HospitalEstimateLine
					{
						CompanyId = ctx.CompanyId, EstimateId = e.ID, Kind = l.Kind, ServiceId = l.ServiceId, Code = l.Code, Category = l.Category, Description = l.Description, DescriptionEn = l.DescriptionEn,
						Quantity = l.Quantity, UnitPrice = l.UnitPrice, Amount = l.Amount, Covered = q.Payer != null && l.Covered, PayerAmount = q.Totals.PayerPerLine[i],
					});
				}
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync();
				return HospitalResult.Success(e.ID);
			}
			catch (DbUpdateException) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalInsuranceService.ConcurrentChange); }
		}

		/// Issues a draft: it no longer changes, and when the payer's contract asks for approval of what it
		/// contains, the approval request goes to the payer (H11) for the package's procedure, or for the first
		/// service that needs it. A request already waiting for that service is linked, not duplicated.
		public async Task<HospitalResult> IssueAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			var e = await Estimates(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (e == null) return HospitalResult.Fail("not_found");
			if (e.Status != HospitalEstimateStatus.Draft) return HospitalResult.Fail("This estimate was already issued");
			int? preauthId = null;
			if (e.PayerId is int payerId)
			{
				var payer = await _db.Set<HospitalPayer>().AsNoTracking().FirstAsync(p => p.ID == payerId && p.CompanyId == ctx.CompanyId, ct);
				var cats = HospitalInsuranceRules.PreauthCategories(payer.PreauthCategories);
				var needing = await Lines(ctx).AsNoTracking().Where(l => l.EstimateId == e.ID && l.Covered && l.ServiceId != null).OrderBy(l => l.Kind == HospitalEstimateRules.KindPackage ? 0 : 1).ThenBy(l => l.ID).ToListAsync(ct);
				var first = needing.FirstOrDefault(l => cats.Contains(l.Category));
				if (first != null)
				{
					var waiting = await _db.Set<HospitalPreauth>().AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.PatientId == e.PatientId && a.ServiceId == first.ServiceId && a.Status == HospitalPreauthStatus.Pending)
						.Select(a => (int?)a.ID).FirstOrDefaultAsync(ct);
					if (waiting != null) preauthId = waiting;
					else
					{
						var r = await _insurance.RequestPreauthAsync(ctx, e.PatientId, first.ServiceId!.Value, e.PayerShare + e.PatientShare, employeeId, nowUtc, ct);
						if (!r.Ok) return r;   // e.g. the cover ended since the draft: re-price it
						preauthId = r.Id;
					}
				}
			}
			int n = await Estimates(ctx).Where(x => x.ID == id && x.Status == HospitalEstimateStatus.Draft).ExecuteUpdateAsync(u => u
				.SetProperty(x => x.Status, HospitalEstimateStatus.Issued).SetProperty(x => x.IssuedAt, nowUtc).SetProperty(x => x.IssuedByEmployeeId, employeeId)
				.SetProperty(x => x.PreauthId, preauthId), ct);
			return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail("This estimate was already issued");
		}

		public async Task<HospitalResult> CancelAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			if (!await Estimates(ctx).AnyAsync(x => x.ID == id, ct)) return HospitalResult.Fail("not_found");
			int n = await Estimates(ctx).Where(x => x.ID == id && x.Status != HospitalEstimateStatus.Cancelled)
				.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, HospitalEstimateStatus.Cancelled), ct);
			return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail("This estimate is already cancelled");
		}

		/// The estimates as rows, newest first (or the one `id`). Filtered and ordered in SQL on the entity, then
		/// shaped into rows in memory: EF cannot order or filter after a projection into a record.
		private async Task<List<HospitalEstimateRow>> RowsAsync(BusinessContext ctx, int? id, CancellationToken ct)
		{
			var q = from e in Estimates(ctx).AsNoTracking()
					join p in _db.HospitalPatients.AsNoTracking() on e.PatientId equals p.ID
					join k in Packages(ctx).AsNoTracking() on e.PackageId equals k.ID into kk
					from k in kk.DefaultIfEmpty()
					join y in _db.Set<HospitalPayer>().AsNoTracking().Where(y => y.CompanyId == ctx.CompanyId) on e.PayerId equals y.ID into yy
					from y in yy.DefaultIfEmpty()
					where p.CompanyId == ctx.CompanyId && (id == null || e.ID == id)
					orderby e.CreatedAt descending
					select new { e, p.Name, p.NameEn, p.Mrn, p.PhotoPath, Package = k == null ? null : k.Name, PackageEn = k == null ? null : k.NameEn, Payer = y == null ? null : y.Name, PayerEn = y == null ? null : y.NameEn };
			var rows = await q.Take(300).ToListAsync(ct);
			return rows.Select(r => new HospitalEstimateRow(r.e, r.Name, r.NameEn, r.Mrn, r.PhotoPath, r.Package, r.PackageEn, r.Payer, r.PayerEn)).ToList();
		}

		public async Task<IReadOnlyList<HospitalEstimateRow>> EstimatesAsync(BusinessContext ctx, CancellationToken ct = default)
			=> await RowsAsync(ctx, null, ct);

		public async Task<HospitalEstimateView?> EstimateAsync(BusinessContext ctx, int id, DateTime nowUtc, CancellationToken ct = default)
		{
			var row = (await RowsAsync(ctx, id, ct)).FirstOrDefault();
			if (row == null) return null;
			var lines = await Lines(ctx).AsNoTracking().Where(l => l.EstimateId == id).OrderBy(l => l.ID).ToListAsync(ct);
			var pa = row.Estimate.PreauthId is int pid
				? await _db.Set<HospitalPreauth>().AsNoTracking().Where(a => a.ID == pid && a.CompanyId == ctx.CompanyId).Select(a => new { a.RequestNo, a.Status }).FirstOrDefaultAsync(ct) : null;
			bool expired = row.Estimate.IssuedAt is DateTime at && HospitalEstimateRules.IsExpired(at, nowUtc);
			return new HospitalEstimateView(row, lines, pa?.RequestNo, pa?.Status, expired);
		}
	}
}
