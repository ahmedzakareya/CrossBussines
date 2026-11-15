using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform.Ai
{
	// =================================================================================================
	// THREE MORE QUESTIONS, CHOSEN BY MEASUREMENT RATHER THAN BY LIST.
	//
	// The standard set an ERP assistant is expected to answer is easy to find and mostly vendor
	// marketing — it says what is sold, not what anyone asks, and it assumes data nobody checked.
	// So every candidate was put to this database first, and the ones below are the ones it can
	// actually support. The ones it cannot are deliberately NOT built: fill rate (2 sales orders),
	// supplier price variance (6 purchase orders), payables ageing (4 payments, 0 allocations),
	// budget variance (no budget table), stockout prediction (28 active days in 312, no lead time),
	// and below-reorder-point (2 items of 231 have one). Each of those would return a confident
	// figure computed from nothing, which is the one failure this service keeps producing.
	// =================================================================================================

	// -------------------------------------------------------------------------------------------
	// "What is not moving?"
	//
	// THE WHOLE VALUE OF THIS ANSWER IS IN A SPLIT NOBODY USUALLY MAKES. 146 stockable items here
	// have never had a single movement and 65 moved and then went quiet. A "dead stock" report that
	// adds them together buries the 65 that are a business signal inside 146 that are a data gap,
	// and the person reading it learns nothing from a list of 211.
	// -------------------------------------------------------------------------------------------
	public sealed class AskDormantItemsHandler : IAskHandler
	{
		public const string IntentId = "inventory.dormant-items";

		private readonly CrossDbContext _db;
		private readonly IInventoryAccessService _inventory;

		public AskDormantItemsHandler(CrossDbContext db, IInventoryAccessService inventory)
		{
			_db = db ?? throw new ArgumentNullException(nameof(db));
			_inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
		}

		public string Intent => IntentId;
		public string Module => EntityRegistry.ScopeInventory;
		public string Action => "read";
		public IReadOnlyCollection<string> Parameters { get; } = Array.Empty<string>();

		public AskText Description => AskText.Of(
			"الأصناف التي لها رصيد ولم تتحرك منذ فترة، في المخازن التي تملك صلاحية عليها",
			"Items holding stock that have not moved for a while, in the warehouses you may see");

		public AskText Example => AskText.Of("إيه الأصناف الراكدة؟", "Which items are not moving?");

		public IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"الاصناف الراكدة", "الأصناف الراكدة", "اصناف راكدة", "راكد", "الراكد",
			"البضاعة الراكدة", "المخزون الراكد", "مخزون ميت", "بضاعة ميتة", "اصناف واقفة",
			"ايه اللي مش بيتحرك", "الاصناف اللي مش بتتباع", "اصناف بطيئة الحركة", "بطيء الحركة",
			"ايه اللي واقف في المخزن", "اللي مش ماشي",
			"dead stock", "slow moving", "slow movers", "dormant items", "stagnant stock",
			"items not moving", "what is not selling", "idle stock", "non moving inventory",
		};

		private const int QuietDays = 90;
		private const int MaxRows = 15;

		public async Task<AskAnswer> AnswerAsync(AskRequest request, AskScope scope, CancellationToken ct = default)
		{
			var isArabic = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var today = DateTime.Today;
			var cutoff = today.AddDays(-QuietDays);

			var balances = await _db.StockBalances.AsNoTracking()
				.Where(b => b.CompanyID == scope.CompanyId && b.QtyOnHand != 0)
				.Select(b => new { b.ItemId, b.WarehouseId, b.QtyOnHand, b.TotalValue, b.LastMovementAt })
				.ToListAsync(ct);

			if (balances.Count == 0)
				return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
					"لا توجد أرصدة مخزون في هذه الشركة.", "There are no stock balances in this company."));

			var warehouseIds = balances.Select(b => b.WarehouseId).Distinct().ToList();
			var visible = new Dictionary<int, bool>();
			foreach (var id in warehouseIds) visible[id] = await _inventory.CanUseWarehouseAsync(id);

			var mine = balances.Where(b => visible.TryGetValue(b.WarehouseId, out var ok) && ok).ToList();
			var withheld = balances.Count - mine.Count;

			if (mine.Count == 0)
				return new AskAnswer
				{
					Answered = false,
					Refusal = AskRefusal.NotPermitted,
					Message = AskText.Of(
						"هناك أرصدة مخزون، لكنها كلها في مخازن ليست ضمن صلاحيتك.",
						"There is stock, but all of it is in warehouses outside your permission."),
					Provenance = Prov(scope, withheld),
				};

			// NEVER MOVED IS A DIFFERENT FACT FROM WENT QUIET, and the two must not share a list.
			// A null LastMovementAt means nothing was ever received or issued against this balance.
			var neverMoved = mine.Where(b => b.LastMovementAt == null).ToList();
			var quiet = mine.Where(b => b.LastMovementAt != null && b.LastMovementAt < cutoff)
				.OrderBy(b => b.LastMovementAt)
				.ToList();

			if (quiet.Count == 0 && neverMoved.Count == 0)
				return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
					$"لا يوجد صنف برصيد توقّف عن الحركة أكثر من {QuietDays} يومًا ضمن صلاحيتك.",
					$"No item holding stock has been still for more than {QuietDays} days within your permission."));

			var ids = quiet.Select(q => q.ItemId).Distinct().Take(MaxRows).ToList();
			var items = (await _db.Items.AsNoTracking()
					.Where(i => i.CompanyID == scope.CompanyId && ids.Contains(i.ID))
					.Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn })
					.ToListAsync(ct))
				.ToDictionary(i => i.ID);

			var inv = System.Globalization.CultureInfo.InvariantCulture;
			var rows = quiet.Where(q => ids.Contains(q.ItemId)).Take(MaxRows).Select(q =>
			{
				items.TryGetValue(q.ItemId, out var it);
				var days = (int)(today - q.LastMovementAt!.Value.Date).TotalDays;
				return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["name"] = it == null ? null : (isArabic ? it.Name : (it.NameEn ?? it.Name)),
					["code"] = it?.ItemCode,
					["warehouse"] = isArabic
						? $"ساكن {days} يومًا · الرصيد {decimal.Round(q.QtyOnHand, 2).ToString(inv)}"
						: $"still {days} days · qty {decimal.Round(q.QtyOnHand, 2).ToString(inv)}",
					["onHand"] = decimal.Round(q.TotalValue, 2),
					["onHandText"] = q.TotalValue.ToString("0.00", inv),
				};
			}).ToList();

			var tiedUp = quiet.Sum(q => q.TotalValue);

			return new AskAnswer
			{
				Answered = rows.Count > 0,
				Refusal = rows.Count > 0 ? AskRefusal.None : AskRefusal.NoData,
				Message = rows.Count > 0 ? null : AskText.Of(
					"كل الأرصدة التي لم تتحرك هي أرصدة لم تُسجَّل عليها حركة قط، وهذه فجوة بيانات لا ركود.",
					"Every still balance is one that never had a movement at all — a data gap, not dormancy."),
				Rows = rows,
				Understood = AskText.Of(
					$"ساكن أكثر من {QuietDays} يومًا، حتى {today:yyyy-MM-dd}. القيمة المحتجزة في هذه الأصناف:"
					+ $" {decimal.Round(tiedUp, 2).ToString(inv)}."
					+ (neverMoved.Count == 0 ? "" :
						$" ويوجد {neverMoved.Count} رصيد آخر لم تُسجَّل عليه أي حركة قط — فجوة بيانات، لا ركود،"
						+ " وهو خارج القائمة أدناه."),
					$"Still for more than {QuietDays} days, as at {today:yyyy-MM-dd}. Value tied up in these:"
					+ $" {decimal.Round(tiedUp, 2).ToString(inv)}."
					+ (neverMoved.Count == 0 ? "" :
						$" A further {neverMoved.Count} balance(s) have never had a movement at all — a data"
						+ " gap rather than dormancy, and outside the list below.")),
				Provenance = Prov(scope, withheld),
			};
		}

		private static AskProvenance Prov(AskScope scope, int withheld) => new()
		{
			CompanyId = scope.CompanyId,
			Sources = new[] { "StockBalances", "Items" },
			Authority = EntityRegistry.ScopeInventory + ":read",
			RowsWithheld = withheld,
			WithheldNote = withheld == 0 ? null : AskText.Of(
				$"استُبعد {withheld} رصيد في مخازن خارج صلاحيتك، فالقائمة والقيمة أعلاه جزئية.",
				$"{withheld} balance(s) in warehouses outside your permission were excluded, so the list"
				+ " and the value above are partial."),
		};
	}

	// -------------------------------------------------------------------------------------------
	// "How long do customers take to pay?"
	//
	// THE AVERAGE IS COMPUTED ON WHAT WAS COLLECTED, WHICH IS THE BIAS. 45 posted invoices here have
	// no receipt against them at all, and an average that silently leaves them out reports the speed
	// of the people who paid as though it were the speed of everybody. The count is stated so the
	// figure can be read for what it is.
	//
	// AND THERE IS NO DUE DATE IN THIS SCHEMA. Not on a sales invoice, not anywhere — so "overdue"
	// cannot be computed at all and is not claimed. What is measured is days from invoice to receipt,
	// which is a different and smaller statement.
	// -------------------------------------------------------------------------------------------
	public sealed class AskCollectionDaysHandler : IAskHandler
	{
		public const string IntentId = "accounting.collection-days";

		private readonly CrossDbContext _db;
		public AskCollectionDaysHandler(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

		public string Intent => IntentId;
		public string Module => EntityRegistry.ScopeAccounting;
		public string Action => "read";
		public IReadOnlyCollection<string> Parameters { get; } = Array.Empty<string>();

		public AskText Description => AskText.Of(
			"متوسط أيام التحصيل من تاريخ الفاتورة حتى الاستلام، وعدد الفواتير التي لم تُحصَّل بعد",
			"Average days from invoice to receipt, and how many invoices have not been collected at all");

		public AskText Example => AskText.Of("العملاء بياخدوا كام يوم عشان يدفعوا؟", "How long do customers take to pay?");

		public IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"ايام التحصيل", "أيام التحصيل", "متوسط التحصيل", "مدة التحصيل", "بياخدوا كام يوم",
			"العملاء بيدفعوا امتى", "سرعة التحصيل", "فترة السداد", "مدة السداد",
			"العملاء بيتأخروا", "التأخير في الدفع", "بيدفعوا بعد كام يوم",
			"dso", "days sales outstanding", "collection days", "how long to get paid",
			"average collection period", "time to collect", "payment speed", "how fast do customers pay",
		};

		public async Task<AskAnswer> AnswerAsync(AskRequest request, AskScope scope, CancellationToken ct = default)
		{
			var isArabic = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			var settled = await (
				from a in _db.ReceiptAllocations.AsNoTracking()
				join r in _db.Receipts.AsNoTracking() on a.ReceiptId equals r.ID
				join s in _db.SalesInvoices.AsNoTracking() on a.SalesInvoiceId equals s.ID
				where a.CompanyID == scope.CompanyId && s.Status == "Posted"
				select new { s.InvoiceDate, r.ReceiptDate, a.ArBase, a.SalesInvoiceId }
			).ToListAsync(ct);

			var uncollected = await _db.SalesInvoices.AsNoTracking()
				.CountAsync(s => s.CompanyID == scope.CompanyId && s.Status == "Posted"
					&& !_db.ReceiptAllocations.Any(a => a.SalesInvoiceId == s.ID), ct);

			if (settled.Count == 0)
				return AskAnswer.Refuse(AskRefusal.NoData, AskText.Of(
					uncollected > 0
						? $"لم يُحصَّل أي من الفواتير المرحَّلة ({uncollected} فاتورة)، فلا يمكن حساب مدة تحصيل."
						: "لا توجد تحصيلات مخصَّصة على فواتير مرحَّلة.",
					uncollected > 0
						? $"None of the {uncollected} posted invoices has been collected, so no collection period can be computed."
						: "There are no receipts allocated to posted invoices."));

			// WEIGHTED BY MONEY, not by invoice. One large slow invoice matters more to the treasury
			// than ten small fast ones, and an unweighted mean hides exactly that.
			var money = settled.Sum(x => x.ArBase);
			var weighted = money == 0
				? 0m
				: settled.Sum(x => (decimal)(x.ReceiptDate.Date - x.InvoiceDate.Date).TotalDays * x.ArBase) / money;

			int InBand(int lo, int hi) =>
				settled.Count(x => (x.ReceiptDate.Date - x.InvoiceDate.Date).TotalDays >= lo
					&& (x.ReceiptDate.Date - x.InvoiceDate.Date).TotalDays <= hi);

			var inv = System.Globalization.CultureInfo.InvariantCulture;
			string Band(string ar, string en, int count) => isArabic ? $"{ar} · {count}" : $"{en} · {count}";

			var rows = new List<IReadOnlyDictionary<string, object?>>
			{
				new Dictionary<string, object?>
				{
					["name"] = isArabic ? "متوسط أيام التحصيل (مرجَّح بالقيمة)" : "Average collection days (value-weighted)",
					["code"] = null,
					["warehouse"] = isArabic ? $"على {settled.Count} تخصيص تحصيل" : $"over {settled.Count} allocations",
					["onHandText"] = decimal.Round(weighted, 2).ToString(inv),
					["onHand"] = decimal.Round(weighted, 2),
				},
				new Dictionary<string, object?>
				{
					["name"] = isArabic ? "نفس اليوم" : "Same day",
					["code"] = Band("فورًا", "immediate", InBand(0, 0)),
					["onHand"] = InBand(0, 0),
				},
				new Dictionary<string, object?>
				{
					["name"] = isArabic ? "خلال أسبوع" : "Within a week",
					["code"] = Band("1-7 يوم", "1-7 days", InBand(1, 7)),
					["onHand"] = InBand(1, 7),
				},
				new Dictionary<string, object?>
				{
					["name"] = isArabic ? "خلال شهر" : "Within a month",
					["code"] = Band("8-30 يوم", "8-30 days", InBand(8, 30)),
					["onHand"] = InBand(8, 30),
				},
				new Dictionary<string, object?>
				{
					["name"] = isArabic ? "أكثر من شهر" : "Over a month",
					["code"] = Band("+30 يوم", "30+ days", InBand(31, int.MaxValue)),
					["onHand"] = InBand(31, int.MaxValue),
				},
			};

			return new AskAnswer
			{
				Answered = true,
				Rows = rows,
				// THE EXCLUSION IS THE HEADLINE, not a footnote. An average over the collected only
				// is the speed of the people who paid, and reads as the speed of everybody.
				Understood = AskText.Of(
					(uncollected > 0
						? $"محسوب على الفواتير التي حُصِّلت فقط. {uncollected} فاتورة مرحَّلة لم يُخصَّص لها أي تحصيل وهي خارج هذا المتوسط. "
						: "محسوب على كل الفواتير المرحَّلة؛ لا توجد فاتورة بلا تحصيل. ")
					+ "ولا يوجد تاريخ استحقاق في المنظومة، فلا يمكن القول إن فاتورة «متأخرة» — المقاس هو أيام من الفاتورة إلى التحصيل.",
					(uncollected > 0
						? $"Computed on collected invoices only. {uncollected} posted invoice(s) have no receipt allocated and are outside this average. "
						: "Computed on all posted invoices; none is uncollected. ")
					+ "There is no due date anywhere in this schema, so nothing can be called overdue — what is measured is days from invoice to receipt."),
				Provenance = new AskProvenance
				{
					CompanyId = scope.CompanyId,
					Sources = new[] { "SalesInvoices", "Receipts", "ReceiptAllocations" },
					Authority = EntityRegistry.ScopeAccounting + ":read",
					RowsWithheld = uncollected,
					WithheldNote = uncollected == 0 ? null : AskText.Of(
						$"{uncollected} فاتورة مرحَّلة بلا أي تحصيل، فهي ليست في المتوسط أعلاه.",
						$"{uncollected} posted invoice(s) have no receipt at all and are not in the average above."),
				},
			};
		}
	}
}
