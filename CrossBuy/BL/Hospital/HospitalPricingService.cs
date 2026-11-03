using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL PRICING SERVICE — the catalogue and the price lists (unit H7).
	//
	//   · The CATALOGUE is every active service plus every active inventory item whose category is
	//     mapped to the hospital (medicine or consumable). Its MAIN price is the card's selling price,
	//     so a new card is on the main list at once and a changed price is the main price at once.
	//   · A new list (or a new version) copies EVERY catalogue entry at the latest main price through
	//     its discount rule, and every line starts "not updated". A catalogue entry added later is
	//     "new — not added" on every other list until someone adds it.
	//   · A line follows the rule from the CURRENT main price unless it has a fixed price; it is "not
	//     updated" while it awaits review or once the main price moved from the one it was confirmed at.
	//   · A list is approved only with no line left not updated; only an approved list, valid on the
	//     day, prices a bill. A medicine never exceeds its public price.
	// Everything here asks `manage`, except reading.
	// ==========================================================================================

	public sealed class HospitalPriceListInput
	{
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string Kind { get; set; } = HospitalPriceListKinds.Insurer;
		public int? CurrencyId { get; set; }
		public decimal ServicesAdjPct { get; set; }
		public decimal MedicinesAdjPct { get; set; }
		public decimal ConsumablesAdjPct { get; set; }
		public decimal? RoundingStep { get; set; }
		public DateTime ValidFrom { get; set; }
		public DateTime? ValidTo { get; set; }
	}

	public sealed class HospitalPriceLineInput
	{
		public decimal? FixedPrice { get; set; }
		public bool IsExcluded { get; set; }
	}

	/// One catalogue entry. MainPrice is in the facility's currency.
	public sealed record HospitalCatalogEntry(string Group, int? ServiceId, int? ItemId, string Code, string Name, string NameEn, string Category,
		decimal MainPrice, decimal? PublicPrice, DateTime? Since)
	{
		public string Key => ServiceId is int s ? "s" + s : "i" + ItemId;
	}

	public sealed record HospitalPriceCurrency(int? Id, string Code, string? Symbol, int Decimals)
	{
		public string Label(bool arabic) => arabic && !string.IsNullOrWhiteSpace(Symbol) ? Symbol! : Code;
		public string Format(decimal amount) => amount.ToString("N" + Decimals, System.Globalization.CultureInfo.InvariantCulture);
	}

	/// A catalogue entry seen from one list. Price is in the list's currency (null when not covered or not added).
	public sealed record HospitalListLineView(HospitalCatalogEntry Entry, HospitalPriceListLine? Line, decimal? Price, string Source, bool Stale)
	{
		public bool Member => Line != null;
	}

	public sealed record HospitalPriceListView(HospitalPriceList List, HospitalPriceCurrency Currency, HospitalPriceCurrency Facility,
		IReadOnlyList<HospitalListLineView> Lines, IReadOnlyList<HospitalCatalogEntry> Pending, int Patients)
	{
		public int StaleCount => Lines.Count(l => l.Stale);
	}

	public sealed record HospitalPriceListRow(HospitalPriceList List, HospitalPriceCurrency Currency, int Lines, int Stale, int Pending, int Patients);

	/// A price for a bill: in CurrencyId (null = facility), from PriceListId (null = the main list).
	public sealed record HospitalPriceQuote(decimal Price, int? PriceListId, int? CurrencyId, bool OutsideList, string Source);

	public sealed class HospitalPricingService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly ICurrencyService? _currency;

		public HospitalPricingService(CrossDbContext db, IHospitalAccessService access, ICurrencyService? currency = null)
		{ _db = db; _access = access; _currency = currency; }

		private Task<bool> CanManage(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Manage, null, ct);

		// ====================================================================================
		// THE RULE (pure — the tests pin these)
		// ====================================================================================

		public static decimal AdjFor(HospitalPriceList pl, string group) => group switch
		{
			HospitalPriceGroups.Medicine => pl.MedicinesAdjPct,
			HospitalPriceGroups.Consumable => pl.ConsumablesAdjPct,
			_ => pl.ServicesAdjPct,
		};

		/// The main price through a list's rule, in the list's currency: × frozen rate × (1 + adj%),
		/// a service snapped to the list's step, everything to the currency's decimals.
		public static decimal Rule(decimal main, decimal rate, decimal adjPct, int decimals, decimal? step, bool snap)
		{
			var v = main * rate * (1 + adjPct / 100m);
			if (snap && step is decimal s && s > 0) v = Math.Round(v / s, MidpointRounding.AwayFromZero) * s;
			return Math.Round(v, decimals, MidpointRounding.AwayFromZero);
		}

		/// A line's price and where it came from: excluded | override | ceiling | rule (null price when excluded).
		public static (decimal? Price, string Source) Resolve(HospitalPriceList pl, HospitalPriceListLine line, HospitalCatalogEntry e, int decimals)
		{
			if (line.IsExcluded) return (null, "excluded");
			if (line.FixedPrice is decimal f) return (f, "override");
			var v = Rule(e.MainPrice, pl.FrozenRate, AdjFor(pl, e.Group), decimals, pl.RoundingStep, e.Group == HospitalPriceGroups.Service);
			if (e.Group == HospitalPriceGroups.Medicine && e.PublicPrice is decimal pp)
			{
				var cap = Math.Round(pp * pl.FrozenRate, decimals, MidpointRounding.AwayFromZero);
				if (v > cap) return (cap, "ceiling");
			}
			return (v, "rule");
		}

		public static bool IsStale(HospitalPriceListLine line, decimal mainPrice)
			=> line.NeedsReview || line.ConfirmedMainPrice is not decimal c || Math.Round(c, 4) != Math.Round(mainPrice, 4);

		/// The main price in a list's currency, untouched by its rule (what an item outside the list costs).
		public static decimal OutsidePrice(HospitalPriceList pl, decimal main, int decimals) => Math.Round(main * pl.FrozenRate, decimals, MidpointRounding.AwayFromZero);

		// ====================================================================================
		// CURRENCIES
		// ====================================================================================

		public async Task<HospitalPriceCurrency> FacilityCurrencyAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var id = await _db.Companies.AsNoTracking().Where(c => c.CompanyID == ctx.CompanyId).Select(c => c.DefaultCurrencyId).FirstOrDefaultAsync(ct);
			var c = id is int cid ? await _db.Currencies.AsNoTracking().FirstOrDefaultAsync(x => x.ID == cid, ct) : null;
			return c == null ? new HospitalPriceCurrency(null, "", null, 3) : new HospitalPriceCurrency(c.ID, c.Code, c.Symbol, c.DecimalPlaces);
		}

		/// A list's currency (its own, or the facility's when null).
		public async Task<HospitalPriceCurrency> CurrencyOfAsync(BusinessContext ctx, int? currencyId, CancellationToken ct = default)
		{
			if (currencyId is not int id) return await FacilityCurrencyAsync(ctx, ct);
			var c = await _db.Currencies.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			return c == null ? await FacilityCurrencyAsync(ctx, ct) : new HospitalPriceCurrency(c.ID, c.Code, c.Symbol, c.DecimalPlaces);
		}

		public async Task<IReadOnlyList<HospitalPriceCurrency>> CurrenciesAsync(CancellationToken ct = default)
			=> await _db.Currencies.AsNoTracking().OrderBy(c => c.Code).Select(c => new HospitalPriceCurrency(c.ID, c.Code, c.Symbol, c.DecimalPlaces)).ToListAsync(ct);

		/// List-currency units per one facility unit on `day` (1 for the facility's own currency).
		private async Task<(bool Ok, decimal Rate)> RateFromFacilityAsync(BusinessContext ctx, int? currencyId, DateTime day, CancellationToken ct)
		{
			var fac = await FacilityCurrencyAsync(ctx, ct);
			if (currencyId is not int id || id == fac.Id) return (true, 1m);
			if (_currency == null || fac.Id is not int fid) return (false, 0m);
			try
			{
				var (_, listToFacility) = await _currency.ToBaseAsync(1m, id, fid, day, "Standard");
				return listToFacility > 0 ? (true, Math.Round(1m / listToFacility, 8)) : (false, 0m);
			}
			catch (InvalidOperationException) { return (false, 0m); }
		}

		/// Bill currency → facility currency on `day` (1 for the facility's own currency).
		public async Task<(bool Ok, decimal Rate)> RateToFacilityAsync(BusinessContext ctx, int? currencyId, DateTime day, CancellationToken ct = default)
		{
			var fac = await FacilityCurrencyAsync(ctx, ct);
			if (currencyId is not int id || id == fac.Id) return (true, 1m);
			if (_currency == null || fac.Id is not int fid) return (false, 0m);
			try { var (_, r) = await _currency.ToBaseAsync(1m, id, fid, day, "Standard"); return r > 0 ? (true, r) : (false, 0m); }
			catch (InvalidOperationException) { return (false, 0m); }
		}

		// ====================================================================================
		// THE CATALOGUE
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalCatalogEntry>> CatalogAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var list = new List<HospitalCatalogEntry>();
			var services = await _db.HospitalServices.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.IsActive)
				.OrderBy(s => s.Category).ThenBy(s => s.SortOrder).ThenBy(s => s.Code).ToListAsync(ct);
			list.AddRange(services.Select(s => new HospitalCatalogEntry(HospitalPriceGroups.Service, s.ID, null, s.Code, s.Name, s.NameEn, s.Category, s.Price, null, s.CreatedAt)));
			var cats = await _db.HospitalItemCategories.AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId).ToDictionaryAsync(c => c.ItemCategoryId, c => c.Kind, ct);
			if (cats.Count > 0)
			{
				var catIds = cats.Keys.ToList();
				var items = await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && i.IsActive && catIds.Contains(i.ItemCategoryId))
					.OrderBy(i => i.ItemCode).Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn, i.ItemCategoryId, i.SalesPrice, i.CreatedAt }).ToListAsync(ct);
				var ids = items.Select(i => i.ID).ToList();
				var pub = await _db.HospitalItemPublicPrices.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && ids.Contains(p.ItemId)).ToDictionaryAsync(p => p.ItemId, p => p.PublicPrice, ct);
				foreach (var i in items)
				{
					var kind = cats[i.ItemCategoryId];
					list.Add(new HospitalCatalogEntry(kind, null, i.ID, i.ItemCode, i.Name, i.NameEn ?? i.Name, kind, i.SalesPrice ?? 0m,
						kind == HospitalPriceGroups.Medicine && pub.TryGetValue(i.ID, out var p) ? p : null, i.CreatedAt));
				}
			}
			return list;
		}

		public async Task<IReadOnlyList<HospitalItemCategory>> ItemCategoriesAsync(BusinessContext ctx, CancellationToken ct = default)
			=> await _db.HospitalItemCategories.AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId).ToListAsync(ct);

		/// Replaces the category map: inventory category id → Medicine | Consumable.
		public async Task<HospitalResult> SaveItemCategoriesAsync(BusinessContext ctx, IReadOnlyDictionary<int, string> map, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (map.Values.Any(k => !HospitalPriceGroups.ItemKinds.Contains(k))) return HospitalResult.Fail("A category is a medicine or a consumable");
			var ids = map.Keys.ToList();
			var found = await _db.ItemCategories.AsNoTracking().CountAsync(c => c.CompanyID == ctx.CompanyId && ids.Contains(c.ID), ct);
			if (found != ids.Count) return HospitalResult.Fail("not_found");
			var rows = await _db.HospitalItemCategories.Where(c => c.CompanyId == ctx.CompanyId).ToListAsync(ct);
			foreach (var r in rows.Where(r => !map.ContainsKey(r.ItemCategoryId))) _db.HospitalItemCategories.Remove(r);
			foreach (var (cat, kind) in map)
			{
				var r = rows.FirstOrDefault(x => x.ItemCategoryId == cat);
				if (r == null) _db.HospitalItemCategories.Add(new HospitalItemCategory { CompanyId = ctx.CompanyId, ItemCategoryId = cat, Kind = kind });
				else r.Kind = kind;
			}
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success();
		}

		/// A service card's selling price — which IS its main-list price. Every list holding the service shows
		/// its line not updated from now on (its confirmed price no longer matches).
		public async Task<HospitalResult> SaveServicePriceAsync(BusinessContext ctx, int serviceId, decimal price, int? employeeId, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (price < 0) return HospitalResult.Fail("A price cannot be negative");
			var s = await _db.HospitalServices.FirstOrDefaultAsync(x => x.ID == serviceId && x.CompanyId == ctx.CompanyId, ct);
			if (s == null) return HospitalResult.Fail("not_found");
			s.Price = Math.Round(price, 4); s.UpdatedAt = DateTime.UtcNow; s.UpdatedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(s.ID);
		}

		/// The regulator's public price of a medicine (facility currency); null clears it.
		public async Task<HospitalResult> SavePublicPriceAsync(BusinessContext ctx, int itemId, decimal? price, int? employeeId, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (price < 0) return HospitalResult.Fail("A price cannot be negative");
			var entry = (await CatalogAsync(ctx, ct)).FirstOrDefault(e => e.ItemId == itemId);
			if (entry == null) return HospitalResult.Fail("not_found");
			if (entry.Group != HospitalPriceGroups.Medicine) return HospitalResult.Fail("Only a medicine has a public price");
			var row = await _db.HospitalItemPublicPrices.FirstOrDefaultAsync(p => p.CompanyId == ctx.CompanyId && p.ItemId == itemId, ct);
			if (price == null) { if (row != null) _db.HospitalItemPublicPrices.Remove(row); }
			else
			{
				if (row == null) { row = new HospitalItemPublicPrice { CompanyId = ctx.CompanyId, ItemId = itemId }; _db.HospitalItemPublicPrices.Add(row); }
				row.PublicPrice = Math.Round(price.Value, 4); row.UpdatedAt = DateTime.UtcNow; row.UpdatedByEmployeeId = employeeId;
			}
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(itemId);
		}

		// ====================================================================================
		// LISTS
		// ====================================================================================

		private static bool Matches(HospitalPriceListLine l, HospitalCatalogEntry e) => (e.ServiceId != null && l.ServiceId == e.ServiceId) || (e.ItemId != null && l.ItemId == e.ItemId);

		public async Task<IReadOnlyList<HospitalPriceListRow>> ListsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var lists = await _db.HospitalPriceLists.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId)
				.OrderBy(l => l.Status == HospitalPriceListStatus.Archived).ThenBy(l => l.Code).ThenByDescending(l => l.Version).ToListAsync(ct);
			var catalog = await CatalogAsync(ctx, ct);
			var ids = lists.Select(l => l.ID).ToList();
			var lines = await _db.HospitalPriceListLines.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && ids.Contains(l.PriceListId)).ToListAsync(ct);
			var patients = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.PriceListId != null)
				.GroupBy(p => p.PriceListId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
			var rows = new List<HospitalPriceListRow>();
			foreach (var pl in lists)
			{
				var mine = lines.Where(l => l.PriceListId == pl.ID).ToList();
				int stale = 0, pending = 0;
				foreach (var e in catalog)
				{
					var line = mine.FirstOrDefault(l => Matches(l, e));
					if (line == null) pending++;
					else if (IsStale(line, e.MainPrice)) stale++;
				}
				rows.Add(new HospitalPriceListRow(pl, await CurrencyOfAsync(ctx, pl.CurrencyId, ct), mine.Count, stale, pending, patients.GetValueOrDefault(pl.ID)));
			}
			return rows;
		}

		public async Task<HospitalPriceListView?> ListAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			var pl = await _db.HospitalPriceLists.AsNoTracking().FirstOrDefaultAsync(l => l.ID == id && l.CompanyId == ctx.CompanyId, ct);
			if (pl == null) return null;
			var cur = await CurrencyOfAsync(ctx, pl.CurrencyId, ct);
			var catalog = await CatalogAsync(ctx, ct);
			var lines = await _db.HospitalPriceListLines.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.PriceListId == pl.ID).ToListAsync(ct);
			var views = new List<HospitalListLineView>();
			var pending = new List<HospitalCatalogEntry>();
			foreach (var e in catalog)
			{
				var line = lines.FirstOrDefault(l => Matches(l, e));
				if (line == null) { pending.Add(e); continue; }
				var (price, src) = Resolve(pl, line, e, cur.Decimals);
				views.Add(new HospitalListLineView(e, line, price, src, IsStale(line, e.MainPrice)));
			}
			var patients = await _db.HospitalPatients.AsNoTracking().CountAsync(p => p.CompanyId == ctx.CompanyId && p.PriceListId == pl.ID, ct);
			return new HospitalPriceListView(pl, cur, await FacilityCurrencyAsync(ctx, ct), views, pending, patients);
		}

		private async Task<string> NextCodeAsync(BusinessContext ctx, CancellationToken ct)
		{
			var codes = await _db.HospitalPriceLists.Where(l => l.CompanyId == ctx.CompanyId && l.Code.StartsWith("PL-")).Select(l => l.Code).ToListAsync(ct);
			int max = 0;
			foreach (var c in codes) if (int.TryParse(c.AsSpan(3), out var n) && n > max) max = n;
			return "PL-" + (max + 1).ToString("0000");
		}

		private static string? Check(HospitalPriceListInput i)
		{
			if (string.IsNullOrWhiteSpace(i.Name)) return "Enter the Arabic name";
			if (string.IsNullOrWhiteSpace(i.NameEn)) return "Enter the English name";
			if (!HospitalPriceListKinds.IsKnown(i.Kind)) return "Unknown list kind";
			if (i.ServicesAdjPct is < -100 or > 1000 || i.MedicinesAdjPct is < -100 or > 1000 || i.ConsumablesAdjPct is < -100 or > 1000) return "A percentage is between -100 and 1000";
			if (i.RoundingStep is <= 0) return "A rounding step is above zero";
			if (i.ValidFrom == default) return "Enter the start date";
			if (i.ValidTo != null && i.ValidTo < i.ValidFrom) return "The end date is before the start date";
			return null;
		}

		/// Creates a draft list and copies EVERY catalogue entry into it at the latest main price through the
		/// rule; every line starts not updated.
		public async Task<HospitalResult> CreateAsync(BusinessContext ctx, HospitalPriceListInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (Check(input) is string err) return HospitalResult.Fail(err);
			var fac = await FacilityCurrencyAsync(ctx, ct);
			int? curId = input.CurrencyId == fac.Id ? null : input.CurrencyId;
			if (curId is int cid && !await _db.Currencies.AnyAsync(c => c.ID == cid, ct)) return HospitalResult.Fail("not_found");
			var (ok, rate) = await RateFromFacilityAsync(ctx, curId, nowUtc, ct);
			if (!ok) return HospitalResult.Fail("There is no exchange rate for this currency. Enter one in Accounting first");

			await using var tx = await _db.Database.BeginTransactionAsync(ct);
			var pl = new HospitalPriceList
			{
				CompanyId = ctx.CompanyId, Code = await NextCodeAsync(ctx, ct), Name = input.Name.Trim(), NameEn = input.NameEn.Trim(), Kind = input.Kind, CurrencyId = curId, FrozenRate = rate,
				ServicesAdjPct = input.ServicesAdjPct, MedicinesAdjPct = input.MedicinesAdjPct, ConsumablesAdjPct = input.ConsumablesAdjPct, RoundingStep = input.RoundingStep,
				ValidFrom = input.ValidFrom.Date, ValidTo = input.ValidTo?.Date, Version = 1, Status = HospitalPriceListStatus.Draft, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
			};
			_db.HospitalPriceLists.Add(pl);
			await _db.SaveChangesAsync(ct);
			foreach (var e in await CatalogAsync(ctx, ct)) _db.HospitalPriceListLines.Add(NewLine(pl, e, nowUtc));
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync(ct);
			return HospitalResult.Success(pl.ID);
		}

		private static HospitalPriceListLine NewLine(HospitalPriceList pl, HospitalCatalogEntry e, DateTime now, decimal? fixedPrice = null, bool excluded = false) => new()
		{
			CompanyId = pl.CompanyId, PriceListId = pl.ID, ServiceId = e.ServiceId, ItemId = e.ItemId, FixedPrice = fixedPrice, IsExcluded = excluded,
			NeedsReview = true, ConfirmedMainPrice = e.MainPrice, AddedAt = now,
		};

		/// A draft's header and rule. An approved list is changed by copying it as a new version.
		public async Task<HospitalResult> UpdateAsync(BusinessContext ctx, int id, HospitalPriceListInput input, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (Check(input) is string err) return HospitalResult.Fail(err);
			var pl = await _db.HospitalPriceLists.FirstOrDefaultAsync(l => l.ID == id && l.CompanyId == ctx.CompanyId, ct);
			if (pl == null) return HospitalResult.Fail("not_found");
			if (pl.Status != HospitalPriceListStatus.Draft) return HospitalResult.Fail("Only a draft is edited; copy an approved list as a new version");
			bool ruleChanged = pl.ServicesAdjPct != input.ServicesAdjPct || pl.MedicinesAdjPct != input.MedicinesAdjPct || pl.ConsumablesAdjPct != input.ConsumablesAdjPct || pl.RoundingStep != input.RoundingStep;
			pl.Name = input.Name.Trim(); pl.NameEn = input.NameEn.Trim(); pl.Kind = input.Kind; pl.ValidFrom = input.ValidFrom.Date; pl.ValidTo = input.ValidTo?.Date;
			pl.ServicesAdjPct = input.ServicesAdjPct; pl.MedicinesAdjPct = input.MedicinesAdjPct; pl.ConsumablesAdjPct = input.ConsumablesAdjPct; pl.RoundingStep = input.RoundingStep;
			// A new rule changes every rule-priced line: they all go back to review.
			if (ruleChanged)
				await _db.HospitalPriceListLines.Where(l => l.CompanyId == ctx.CompanyId && l.PriceListId == pl.ID && l.FixedPrice == null && !l.IsExcluded)
					.ExecuteUpdateAsync(u => u.SetProperty(l => l.NeedsReview, true), ct);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(pl.ID);
		}

		/// Adds catalogue entries that are not on the list yet (all of them when `keys` is null), through the rule.
		public async Task<HospitalResult> AddLinesAsync(BusinessContext ctx, int id, IReadOnlyCollection<string>? keys, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			var v = await ListAsync(ctx, id, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			if (v.List.Status == HospitalPriceListStatus.Archived) return HospitalResult.Fail("This list is archived");
			var add = v.Pending.Where(e => keys == null || keys.Contains(e.Key)).ToList();
			if (add.Count == 0) return HospitalResult.Fail("Nothing to add");
			foreach (var e in add) _db.HospitalPriceListLines.Add(NewLine(v.List, e, nowUtc));
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(add.Count);
		}

		/// Confirms lines at the current main price (every not-updated line when `lineIds` is null).
		public async Task<HospitalResult> ApproveLinesAsync(BusinessContext ctx, int id, IReadOnlyCollection<int>? lineIds, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			var v = await ListAsync(ctx, id, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			if (v.List.Status == HospitalPriceListStatus.Archived) return HospitalResult.Fail("This list is archived");
			var targets = v.Lines.Where(l => l.Stale && (lineIds == null || lineIds.Contains(l.Line!.ID))).ToList();
			var ids = targets.Select(t => t.Line!.ID).ToList();
			var rows = await _db.HospitalPriceListLines.Where(l => l.CompanyId == ctx.CompanyId && l.PriceListId == id && ids.Contains(l.ID)).ToListAsync(ct);
			foreach (var r in rows)
			{
				var e = targets.First(t => t.Line!.ID == r.ID).Entry;
				r.NeedsReview = false; r.ConfirmedMainPrice = e.MainPrice; r.ReviewedAt = nowUtc; r.ReviewedByEmployeeId = employeeId;
			}
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(rows.Count);
		}

		/// A line's own price or exclusion; saving it also confirms it.
		public async Task<HospitalResult> SaveLineAsync(BusinessContext ctx, int lineId, HospitalPriceLineInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			var line = await _db.HospitalPriceListLines.FirstOrDefaultAsync(l => l.ID == lineId && l.CompanyId == ctx.CompanyId, ct);
			if (line == null) return HospitalResult.Fail("not_found");
			var v = (await ListAsync(ctx, line.PriceListId, ct))!;
			if (v.List.Status == HospitalPriceListStatus.Archived) return HospitalResult.Fail("This list is archived");
			var e = v.Lines.First(l => l.Line!.ID == line.ID).Entry;
			if (input.FixedPrice < 0) return HospitalResult.Fail("A price cannot be negative");
			if (input.FixedPrice is decimal f && e.Group == HospitalPriceGroups.Medicine && e.PublicPrice is decimal pp
				&& f > Math.Round(pp * v.List.FrozenRate, v.Currency.Decimals, MidpointRounding.AwayFromZero))
				return HospitalResult.Fail("Above the medicine's public price ({0})", v.Currency.Format(Math.Round(pp * v.List.FrozenRate, v.Currency.Decimals)));
			line.FixedPrice = input.IsExcluded ? null : (input.FixedPrice is decimal p ? Math.Round(p, v.Currency.Decimals, MidpointRounding.AwayFromZero) : null);
			line.IsExcluded = input.IsExcluded;
			line.NeedsReview = false; line.ConfirmedMainPrice = e.MainPrice; line.ReviewedAt = nowUtc; line.ReviewedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(line.ID);
		}

		/// Approves a draft. Refused while any line is not updated. Approving a new version archives the one it replaces.
		public async Task<HospitalResult> ApproveListAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			var v = await ListAsync(ctx, id, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			if (v.List.Status != HospitalPriceListStatus.Draft) return HospitalResult.Fail("Only a draft is approved");
			if (v.StaleCount > 0) return HospitalResult.Fail("{0} prices are not updated. Review them before approving", v.StaleCount);
			var pl = await _db.HospitalPriceLists.FirstAsync(l => l.ID == id && l.CompanyId == ctx.CompanyId, ct);
			pl.Status = HospitalPriceListStatus.Active; pl.ApprovedAt = nowUtc; pl.ApprovedByEmployeeId = employeeId;
			if (pl.PreviousVersionId is int prev)
			{
				var old = await _db.HospitalPriceLists.FirstOrDefaultAsync(l => l.ID == prev && l.CompanyId == ctx.CompanyId, ct);
				if (old != null)
				{
					old.Status = HospitalPriceListStatus.Archived;
					// Patients on the old version move to the new one.
					await _db.HospitalPatients.Where(p => p.CompanyId == ctx.CompanyId && p.PriceListId == prev).ExecuteUpdateAsync(u => u.SetProperty(p => p.PriceListId, pl.ID), ct);
				}
			}
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(pl.ID);
		}

		public async Task<HospitalResult> ArchiveAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			var pl = await _db.HospitalPriceLists.FirstOrDefaultAsync(l => l.ID == id && l.CompanyId == ctx.CompanyId, ct);
			if (pl == null) return HospitalResult.Fail("not_found");
			if (await _db.HospitalPatients.AnyAsync(p => p.CompanyId == ctx.CompanyId && p.PriceListId == id, ct)) return HospitalResult.Fail("Patients still use this list. Move them first");
			pl.Status = HospitalPriceListStatus.Archived;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(pl.ID);
		}

		/// A new draft version: same code, version + 1, today's frozen rate, every line copied (own prices and
		/// exclusions kept) plus every catalogue entry missing from it — all not updated.
		public async Task<HospitalResult> CopyVersionAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanManage(ctx, ct)) return HospitalResult.Fail("forbidden");
			var src = await _db.HospitalPriceLists.AsNoTracking().FirstOrDefaultAsync(l => l.ID == id && l.CompanyId == ctx.CompanyId, ct);
			if (src == null) return HospitalResult.Fail("not_found");
			if (await _db.HospitalPriceLists.AnyAsync(l => l.CompanyId == ctx.CompanyId && l.Code == src.Code && l.Status == HospitalPriceListStatus.Draft, ct))
				return HospitalResult.Fail("A draft version of this list already exists");
			var (ok, rate) = await RateFromFacilityAsync(ctx, src.CurrencyId, nowUtc, ct);
			if (!ok) return HospitalResult.Fail("There is no exchange rate for this currency. Enter one in Accounting first");
			var version = await _db.HospitalPriceLists.Where(l => l.CompanyId == ctx.CompanyId && l.Code == src.Code).MaxAsync(l => l.Version, ct) + 1;
			await using var tx = await _db.Database.BeginTransactionAsync(ct);
			var pl = new HospitalPriceList
			{
				CompanyId = ctx.CompanyId, Code = src.Code, Name = src.Name, NameEn = src.NameEn, Kind = src.Kind, CurrencyId = src.CurrencyId, FrozenRate = rate,
				ServicesAdjPct = src.ServicesAdjPct, MedicinesAdjPct = src.MedicinesAdjPct, ConsumablesAdjPct = src.ConsumablesAdjPct, RoundingStep = src.RoundingStep,
				ValidFrom = (src.ValidTo ?? nowUtc.Date).AddDays(src.ValidTo == null ? 0 : 1), ValidTo = src.ValidTo == null ? null : src.ValidTo.Value.AddYears(1),
				Version = version, PreviousVersionId = src.ID, Status = HospitalPriceListStatus.Draft, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
			};
			_db.HospitalPriceLists.Add(pl);
			await _db.SaveChangesAsync(ct);
			var old = await _db.HospitalPriceListLines.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.PriceListId == src.ID).ToListAsync(ct);
			foreach (var e in await CatalogAsync(ctx, ct))
			{
				var o = old.FirstOrDefault(l => Matches(l, e));
				_db.HospitalPriceListLines.Add(NewLine(pl, e, nowUtc, o?.FixedPrice, o?.IsExcluded ?? false));
			}
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync(ct);
			return HospitalResult.Success(pl.ID);
		}

		// ====================================================================================
		// PRICING A BILL
		// ====================================================================================

		/// The list a patient's new bill uses on `day`: the patient's list when it is approved and valid that day,
		/// otherwise none (the main list).
		public async Task<HospitalPriceList?> ListForPatientAsync(BusinessContext ctx, int patientId, DateTime day, CancellationToken ct = default)
		{
			var lid = await _db.HospitalPatients.AsNoTracking().Where(p => p.ID == patientId && p.CompanyId == ctx.CompanyId).Select(p => p.PriceListId).FirstOrDefaultAsync(ct);
			if (lid is not int id) return null;
			var d = day.Date;
			return await _db.HospitalPriceLists.AsNoTracking().FirstOrDefaultAsync(l => l.ID == id && l.CompanyId == ctx.CompanyId && l.Status == HospitalPriceListStatus.Active
				&& l.ValidFrom <= d && (l.ValidTo == null || l.ValidTo >= d), ct);
		}

		/// A service's price on a bill priced by `listId` (null = the main list).
		public async Task<HospitalPriceQuote> QuoteServiceAsync(BusinessContext ctx, int? listId, HospitalService s, CancellationToken ct = default)
		{
			if (listId is not int id) return new HospitalPriceQuote(s.Price, null, null, false, "main");
			var pl = await _db.HospitalPriceLists.AsNoTracking().FirstOrDefaultAsync(l => l.ID == id && l.CompanyId == ctx.CompanyId, ct);
			if (pl == null) return new HospitalPriceQuote(s.Price, null, null, false, "main");
			var cur = await CurrencyOfAsync(ctx, pl.CurrencyId, ct);
			var entry = new HospitalCatalogEntry(HospitalPriceGroups.Service, s.ID, null, s.Code, s.Name, s.NameEn, s.Category, s.Price, null, s.CreatedAt);
			var line = await _db.HospitalPriceListLines.AsNoTracking().FirstOrDefaultAsync(l => l.CompanyId == ctx.CompanyId && l.PriceListId == id && l.ServiceId == s.ID, ct);
			if (line != null)
			{
				var (price, src) = Resolve(pl, line, entry, cur.Decimals);
				if (price is decimal p) return new HospitalPriceQuote(p, pl.ID, pl.CurrencyId, false, src);
			}
			// Not on the list, or not covered: the main price in the list's currency, charged to the patient.
			return new HospitalPriceQuote(OutsidePrice(pl, s.Price, cur.Decimals), pl.ID, pl.CurrencyId, true, line == null ? "missing" : "excluded");
		}

		/// An inventory item's price on a bill priced by `listId` (H10, the pharmacy): its catalogue entry (the card's
		/// selling price, capped at a medicine's public price) through the list, like a service. Null when the item
		/// is not in a category the hospital maps.
		public async Task<HospitalPriceQuote?> QuoteItemAsync(BusinessContext ctx, int? listId, int itemId, CancellationToken ct = default)
		{
			var entry = (await CatalogAsync(ctx, ct)).FirstOrDefault(e => e.ItemId == itemId);
			if (entry == null) return null;
			decimal main = entry.PublicPrice is decimal cap && cap < entry.MainPrice ? cap : entry.MainPrice;
			if (listId is not int id) return new HospitalPriceQuote(main, null, null, false, "main");
			var pl = await _db.HospitalPriceLists.AsNoTracking().FirstOrDefaultAsync(l => l.ID == id && l.CompanyId == ctx.CompanyId, ct);
			if (pl == null) return new HospitalPriceQuote(main, null, null, false, "main");
			var cur = await CurrencyOfAsync(ctx, pl.CurrencyId, ct);
			var line = await _db.HospitalPriceListLines.AsNoTracking().FirstOrDefaultAsync(l => l.CompanyId == ctx.CompanyId && l.PriceListId == id && l.ItemId == itemId, ct);
			if (line != null)
			{
				var (price, src) = Resolve(pl, line, entry, cur.Decimals);
				if (price is decimal p) return new HospitalPriceQuote(p, pl.ID, pl.CurrencyId, false, src);
			}
			return new HospitalPriceQuote(OutsidePrice(pl, main, cur.Decimals), pl.ID, pl.CurrencyId, true, line == null ? "missing" : "excluded");
		}

		/// A room's daily rate on a bill priced by `listId`: the rate through the list's services rule.
		public async Task<decimal> QuoteRoomRateAsync(BusinessContext ctx, int? listId, decimal dailyRate, CancellationToken ct = default)
		{
			if (listId is not int id) return dailyRate;
			var pl = await _db.HospitalPriceLists.AsNoTracking().FirstOrDefaultAsync(l => l.ID == id && l.CompanyId == ctx.CompanyId, ct);
			if (pl == null) return dailyRate;
			var cur = await CurrencyOfAsync(ctx, pl.CurrencyId, ct);
			return Rule(dailyRate, pl.FrozenRate, pl.ServicesAdjPct, cur.Decimals, pl.RoundingStep, true);
		}
	}
}
