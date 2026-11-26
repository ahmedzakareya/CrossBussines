using CrossBuy.BL;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Pos;
using CrossBuy.Models.Menu;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// HM-0: the hypermarket BACK-OFFICE at /hyper. Gated by the normal Employee session (SessionValidationMiddleware) —
	// this is the switcher tile target. Its OWN sidebar (MainMenu.Hyper) on the shared back-office shell (_LayoutBackend),
	// exactly the pattern the other systems use. The independent cashier LANE lives separately at /hyper/pos.
	// HM-0 ships ONE screen: a read-only system diagnostic panel. No selling.
	[Route("hyper")]
	public class HyperController : Controller
	{
		// THE COMPANY THIS TILL SELLS FOR, FROM CONFIGURATION. It was `const int PosCompanyId = 1;` - a
		// deployment fact compiled into five separate files, where it could not be changed without a
		// rebuild and the five copies could drift apart in silence. Same treatment the storefront
		// company already had: PosCompanyOptions, bound to the "Pos" section, read here and nowhere else.
		private readonly int PosCompanyId;
		private readonly IPosSetupService _pos;
		private readonly CrossDbContext _db;
		private readonly ICurrencyService _currency;      // HM-10 slice B: today's rate for the INDICATIVE document-currency margin column
		private readonly ICurrencyRounding _rounding;     // HM-10 slice B: document (KWD 3dp) vs functional (EGP 2dp) precision
		public HyperController(IPosSetupService pos, CrossDbContext db, ICurrencyService currency, ICurrencyRounding rounding, Microsoft.Extensions.Options.IOptions<CrossBuy.Models.Platform.PosCompanyOptions> posCompany, CrossBuy.BL.IItemService items, CrossBuy.BL.Platform.IRequestCompanyResolver company)
		{ _pos = pos; _db = db; _currency = currency; _rounding = rounding; PosCompanyId = posCompany.Value.CatalogCompanyId; _posCompany = posCompany.Value; _items = items; _company = company; }

		// HM-20 — WHO DID IT. A cash movement without an author is the one field the question
		// «who took money out of this drawer?» actually needs, and it was being written as null.
		// This is the same resolver the accounting screens use, not a second way of asking.
		private readonly CrossBuy.BL.Platform.IRequestCompanyResolver _company;

		// HM-22: the whole bound options object, because the loyalty screen reports the configured point
		// value and not only the company id.
		private readonly CrossBuy.Models.Platform.PosCompanyOptions _posCompany;

		private readonly CrossBuy.BL.IItemService _items;   // HM-17: the SHARED item search, same one the till's lookup uses

		// HM-0: the hypermarket capability catalog — SEPARATE from the restaurant list (PosController.Capabilities),
		// so the two systems never share a capability editor. KEYS ONLY — no business logic sits behind any of them
		// in HM-0. "Weight" reuses the existing key; the rest are the hypermarket-specific keys.
		public static readonly (string key, string ar, string en)[] Capabilities = new[]
		{
			("Weight",        "Weight",            "Weight"),
			("BarcodeMulti",  "باركود متعدّد",     "Multi-barcode"),
			("ExpiryControl", "ضبط الصلاحية",      "Expiry control"),
			("Promotions",    "العروض",           "Promotions"),
			("CustomerIdentity", "تعريف العميل",   "Customer identity"),   // HM-9 slice 1: link a real customer to the order (identity only)
			// HM-22: redemption HAS now shipped, so this key no longer ships off for want of a spending arm.
			// It still ships off, for a different and smaller reason: spending also needs a configured point
			// value and a "Points" payment method, and a branch that enables this before those two exist is
			// back to accruing a balance nobody can spend. /hyper/setup/loyalty reports all three.
			("Loyalty",       "الولاء",           "Loyalty"),
			("ShelfLabels",   "ملصقات الرفوف",     "Shelf labels"),
			("PriceCheck",    "استعلام السعر",     "Price check"),
			("SuspendResume", "تعليق/استئناف",     "Suspend / resume"),
			("CashDrawer",    "درج النقد",         "Cash drawer"),
		};

		// ============================================================================================
		// THE BUILD PLAN — /hyper/plan
		//
		// The hypermarket is being built PROTOTYPE FIRST, and the prototype is the blueprint. So every
		// screen the module will have appears in the sidebar from day one, and every one that is not
		// built yet lands here instead of nowhere.
		//
		// WHY THIS EXISTS RATHER THAN A ROADMAP DOCUMENT: a document goes stale in a drawer and nobody
		// opens it while looking at the product. This screen is reached by clicking the thing you wanted,
		// states what it will do, and - the part a roadmap never carries - names WHAT IS ALREADY
		// UNDERNEATH IT. Several of these are a screen over an engine that is already written and tested;
		// that is a very different estimate from one that needs a model, and the reader should be able to
		// tell the two apart without asking.
		//
		// NOT A PROMISE OF DATES. There are none here on purpose.
		// ============================================================================================
		// ReuseUrl/ReuseWhere name an EXISTING screen that already does this. A plan item carrying them
		// is not work to be built — it is work already built elsewhere, and the hypermarket's job is to
		// link to it branch-scoped rather than grow a second copy. Finding these was the correction
		// that stopped this module re-implementing Inventory: the warehouse module already has stock
		// counts, transfers, write-offs, batches, replenishment, purchase orders, goods receipts,
		// price lists AND promotions, and Accounting already has vendors, purchase returns and customers.
		public record PlanSpec(string Key, string TitleAr, string TitleEn, string Group,
			string PurposeAr, string Underneath, bool EngineReady, string? ReuseUrl = null, string? ReuseWhere = null);

		public static readonly PlanSpec[] PlanSpecs = new[]
		{
			new PlanSpec("promotions", "العروض", "Promotions", "Promotions",
				"إنشاء وتحرير العروض: نسبة، مبلغ، حد كمية، صنف أو فئة أو عميل، بالأولوية وتواريخ السريان.",
				"موجودة بالكامل في المخزون، والصندوق يطبّقها منذ HM-5 عبر التسعير. بنيتُ محرّكاً ثانياً بالخطأ ثم سحبتُه — لا تُبنَ مرة أخرى.", true, "/Inventory/Promotions", "المخزون"),
			new PlanSpec("pricelists", "قوائم الأسعار", "Price lists", "Items",
				"أسعار لكل فرع أو شريحة عملاء مع تواريخ سريان.",
				"IPricingService موجود ويُستدعى من الصندوق.", false, "/Inventory/PriceLists", "المخزون"),
			new PlanSpec("shelflabels", "ملصقات الرفوف", "Shelf labels", "Items",
				"طباعة ملصقات الرفوف عند تغيّر السعر، بالحجم والكمية الصحيحين.",
				"ShelfLabelService موجود. القدرة معلَنة بلا منفّذ، وللخدمة حدود حجم معروفة.", true, "/Inventory/ShelfLabels", "المخزون"),

			new PlanSpec("stock", "أرصدة الفرع", "Branch stock", "Stock",
				"رصيد كل صنف في الفرع والمستودع، مع الحركة الأخيرة.", "محرّك المخزون ومستودعات الفروع جاهزة.", true, "/Inventory/StockBalances", "المخزون"),
			new PlanSpec("count", "الجرد", "Stock count", "Stock",
				"جرد دوري أو كامل: ورقة جرد، إدخال المعدود، الفروقات، ثم تسوية معتمَدة.", "مستندات الجرد موجودة في المخزون.", true, "/Inventory/StockCounts", "المخزون"),
			new PlanSpec("transfers", "التحويلات بين الفروع", "Branch transfers", "Stock",
				"طلب وتحويل واستلام بين الفروع مع بضاعة في الطريق.", "مستند التحويل موجود في المخزون.", true, "/Inventory/StockTransfers", "المخزون"),
			new PlanSpec("waste", "الهالك والتلف", "Waste & write-off", "Stock",
				"تسجيل التالف والمنتهي بسببه، وأثره على التكلفة — وهو أساس تقرير الفاقد.", "مستند الإعدام موجود.", true, "/Inventory/WriteOffs", "المخزون"),
			new PlanSpec("expiry", "تتبّع الصلاحية", "Expiry control", "Stock",
				"تنبيه قبل انتهاء الصلاحية ومنع بيع المنتهي، بالاعتماد على الدفعات.",
				"النصفان مبنيّان: المنع في StockService — صرف FEFO يستبعد الدفعات المنتهية، ورسالة النقص تذكر الكمية المستبعَدة، "
				+ "وصرف دفعة بعينها يُرفض ما لم يُطلب AllowExpired صراحةً. والتنبيه شاشة قائمة في المخزون تعرض المنتهي "
				+ "والمقترب بالأيام المتبقية. الفجوة الحقيقية تغطية لا ميزة: الحارس مشروط بـ Item.TrackExpiry وهي مفعَّلة على "
				+ "28 صنفاً من 240 — فما لا دفعات له لا يفحصه شيء. ومفتاح القدرة ExpiryControl نفسه لا يقرؤه أحد بعد.",
				true, "/Inventory/ExpiryAlerts", "المخزون"),
			new PlanSpec("replenish", "إعادة الطلب", "Replenishment", "Stock",
				"حد إعادة الطلب واقتراح أمر شراء تلقائي حسب معدّل البيع.", "ReorderPoint موجود وهناك شاشة اقتراحات في المخزون.", true, "/Inventory/Planning", "المخزون"),

			new PlanSpec("po", "أوامر الشراء", "Purchase orders", "Purchasing",
				"أمر شراء للمورد بالكميات والأسعار المتفق عليها ومتابعة المستلَم.", "مستند أمر الشراء موجود في المخزون.", true, "/Inventory/PurchaseOrders", "المخزون"),
			new PlanSpec("grn", "استلام البضاعة", "Goods receipt", "Purchasing",
				"استلام مقابل أمر الشراء، بالدفعات وتواريخ الصلاحية، وفروقات السعر.", "مستند الاستلام موجود.", true, "/Inventory/GoodsReceipts", "المخزون"),
			new PlanSpec("suppliers", "الموردون", "Suppliers", "Purchasing",
				"بيانات المورد، الشروط، الأصناف التي يوردها وتقييم الأداء.", "الموردون والذمم الدائنة موجودة في المحاسبة.", true, "/Accounting/Vendors", "المحاسبة"),
			new PlanSpec("supplierreturn", "مرتجع المورد", "Supplier returns", "Purchasing",
				"إرجاع التالف أو الزائد للمورد بإشعار مدين.", "مرتجع الشراء موجود في المحاسبة.", true, "/Accounting/PurchaseReturns", "المحاسبة"),

			new PlanSpec("customers", "سجل العملاء", "Customer registry", "Customers",
				"عملاء الهايبر وبياناتهم ومشترياتهم ونقاطهم.", "العملاء موجودون في المحاسبة، والربط بالصندوق يعمل (CustomerIdentity).", true, "/Accounting/Customers", "المحاسبة"),

			new PlanSpec("topsellers", "الأكثر مبيعاً", "Top sellers", "Reports",
				"الأصناف الأكثر مبيعاً بالكمية والقيمة والهامش، عبر الفترة والفرع.",
				"مبنيّ: مجموعة بيانات Hyper.TopSellers مسجَّلة في منصّة التقارير، والتكلفة والهامش خلف مفتاح صلاحية منفصل.",
				true, "/Reports", "منصّة التقارير"),
			new PlanSpec("basket", "تحليل السلة", "Basket analysis", "Reports",
				"متوسط قيمة السلة وعدد الأصناف، والأصناف التي تُشترى معاً.",
				"مبنيّ: مجموعة بيانات Hyper.BasketAnalysis مسجَّلة، بحدٍّ أدنى إلزامي لعدد السلال وعمود «أيام ظهور الزوج» "
				+ "يفصل العادة عن يومٍ واحد مزدحم. تنبيه عن البيانات لا عن الشاشة: لا توجد أي بيعة بعد 2026-08-04، "
				+ "و69% من الموجود يأتي من يومين فقط — فأي فترة حديثة تُرجع شاشة فارغة، وهذا نقص بيانات لا عطل.",
				true, "/Reports", "منصّة التقارير"),
			new PlanSpec("shrinkage", "الفاقد والانكماش", "Shrinkage", "Reports",
				"الفرق بين المتوقع والفعلي: الجرد ناقص المبيعات ناقص الهالك.", "يحتاج الجرد والهالك أولاً.", false),

			new PlanSpec("terminals", "الفروع ونقاط البيع", "Branches & terminals", "Setup",
				"تعريف الفروع ونقاط البيع وحسابات النقد والمستودع الافتراضي.", "الكيانات موجودة؛ الإعداد اليوم SQL.", true, "/Pos/Terminals", "نقاط البيع"),
			new PlanSpec("tenders", "طرق الدفع", "Payment methods", "Setup",
				"طرق الدفع لكل فرع وحساب التسوية لكل طريقة.", "BranchPaymentMethods موجود ويستخدمه الدفع المتعدد.", true, "/Pos/PaymentMethods", "نقاط البيع"),
			new PlanSpec("roles", "صلاحيات الكاشير", "Cashier roles", "Setup",
				"من يبيع، من يُلغي، من يُرجع، ومن يفتح الدرج.", "IPosAccessService يفرض الصلاحيات بالفعل.", true, "/Pos/CashierRoles", "نقاط البيع"),
		};

		[HttpGet("plan")]
		public IActionResult Plan(string? key = null)
		{
			ViewBag.SidebarMenu = MainMenu.Hyper();
			ViewBag.PlanKey = key;
			ViewBag.Specs = PlanSpecs;
			return View("~/Views/Hyper/Plan.cshtml");
		}

		// GET /hyper  and  GET /hyper/dashboard — the system diagnostic panel.
		// ============================================================================================
		// BRANCH CAPABILITIES — /hyper/setup/capabilities
		//
		// The hypermarket's capability editor. The RESTAURANT lane has had one since POS setup shipped
		// (Views/Pos/Setup.cshtml posts to PosController.SaveCapabilities); what was missing was the
		// hypermarket's, because the two catalogues are deliberately separate so neither system can edit
		// the other's keys. Until now a hyper capability could only be changed by an UPDATE against
		// BranchCapabilities, which put a shop-floor decision behind database access.
		//
		// The two screens cannot collide: SaveCapabilitiesAsync MERGES the keys it is given and leaves
		// every other row alone, so saving the restaurant's list never clears a hyper key, or the reverse.
		//
		// THE HONEST PART, and the reason this is not a plain checkbox list: four of the ten declared
		// keys have no consumer anywhere in the application (Promotions is NOT one of them: pricing has
		// consumed it since HM-5, through a direct BranchCapabilities query rather than the central gate).
		// Rendering those as switchable would let a
		// manager turn one on, go looking for the feature and find nothing — which is how a panel stops
		// being believed about the other six. They are listed, named RESERVED, and cannot be switched.
		// The day a consumer ships, its key leaves NotBuilt in the same change.
		//
		// NotBuilt is verified, not assumed: each key was searched for as IsCapabilityEnabledAsync("<key>")
		// across *.cs and *.cshtml, and these four returned zero call sites.
		//
		// "CashDrawer" STAYS HERE EVEN THOUGH /hyper/cash NOW EXISTS, and that is not an oversight. The
		// capability means "this till pops a physical drawer", and nothing kicks a drawer yet. The cash
		// screen is about the MONEY in the drawer, is reached from the back office, and deliberately does
		// not consult this key — a safe drop must be recordable on every branch, because the close is wrong
		// without it. So the key keeps its RESERVED badge until a consumer actually reads it.
		// HM-EXP: ExpiryControl left this list when the till began reading it - it now refuses an
		// out-of-date item at the scan and prints the nearest usable date on the basket row.
		public static readonly string[] NotBuilt = { "ShelfLabels", "CashDrawer" };

		// ==================== HM-PROMO: the promotions screen ====================
		//
		// PosPromotionEngine has shipped for a long time and the Promotions capability gates it per branch.
		// Until now there was no way to create a promotion at all - a branch could switch the capability on
		// and nothing would ever happen, which is the same defect as a column no screen can set.
		//
		// WRITES CARRY THE MODULE AUTHORITY; THE SCREEN REFLECTS IT. The POST actions carry PosPerm("manage")
		// and keep it. The GETs ask the same question and render read-only when the answer is no, because a
		// Save button that always refuses is worse than no button.
		[HttpGet("setup/promotions")]
		public async Task<IActionResult> Promotions(int? branchId = null)
		{
			var branches = await _db.Branches.AsNoTracking()
				.Where(b => b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper")
				.OrderBy(b => b.Name).ToListAsync();
			ViewBag.Branches = branches;
			int? bid = branchId ?? branches.FirstOrDefault()?.ID;
			ViewBag.BranchId = bid;
			ViewBag.Rows = await _pos.GetPosPromotionsAsync(PosCompanyId, bid);
			ViewBag.CanManageCaps = await CanManagePosAsync(bid);
			return View("~/Views/Hyper/Promotions.cshtml");
		}

		[HttpGet("setup/promotion")]
		public async Task<IActionResult> PromotionEditor(int? id, int? branchId = null)
		{
			var (promo, targets) = id is > 0
				? await _pos.GetPosPromotionAsync(PosCompanyId, id.Value)
				: (null, new List<CrossBuy.Models.Context.Pos.PosPromotionItem>());
			if (id is > 0 && promo == null) { TempData["HyperErr"] = "promo-not-found"; return RedirectToAction(nameof(Promotions)); }

			ViewBag.Promo = promo;
			ViewBag.Targets = targets;
			ViewBag.BranchId = promo?.BranchId ?? branchId;
			ViewBag.Branches = await _db.Branches.AsNoTracking()
				.Where(b => b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper")
				.OrderBy(b => b.Name).ToListAsync();
			ViewBag.Categories = await _db.ItemCategories.AsNoTracking()
				.Where(c => c.CompanyID == PosCompanyId).OrderBy(c => c.Name).ToListAsync();
			// The names behind the targets already saved, so the editor shows what the row MEANS rather than
			// an id the person would have to look up somewhere else.
			var itemIds = targets.Where(t => t.ItemId != null).Select(t => t.ItemId!.Value).ToList();
			ViewBag.TargetItemNames = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID))
				.ToDictionaryAsync(i => i.ID, i => i.ItemCode + " — " + i.Name);
			ViewBag.CanManageCaps = await CanManagePosAsync(ViewBag.BranchId as int?);
			return View("~/Views/Hyper/PromotionEditor.cshtml");
		}

		[HttpPost("setup/promotion")]
		[ValidateAntiForgeryToken]
		[CrossBuy.Models.PosPerm("manage")]
		public async Task<IActionResult> SavePromotion(int branchId, int? id, string? code, string? nameAr, string? nameEn,
			string? promoType, decimal triggerQty, decimal rewardQty, decimal discountPct, decimal discountAmount,
			decimal minBasket, int priority, bool isExclusive, decimal maxDiscountPct,
			DateTime? startsAt, DateTime? endsAt, bool isActive,
			int[]? tItem, int[]? tCategory, string[]? tRole, bool allBranches = false)
		{
			var dto = new CrossBuy.Models.Context.Pos.PosPromotion
			{
				ID = id ?? 0,
				// "Every branch" is a deliberate choice on the form, not the absence of one: leaving the
				// branch blank by accident would quietly publish a single shop's offer to the whole company.
				BranchId = allBranches ? (int?)null : branchId,
				Code = code ?? "", NameAr = nameAr ?? "", NameEn = nameEn ?? "",
				PromoType = promoType ?? "PercentOff",
				TriggerQty = triggerQty, RewardQty = rewardQty,
				DiscountPct = discountPct, DiscountAmount = discountAmount, MinBasket = minBasket,
				Priority = priority, IsExclusive = isExclusive, MaxDiscountPct = maxDiscountPct,
				StartsAt = startsAt, EndsAt = endsAt, IsActive = isActive,
			};

			var targets = new List<CrossBuy.Models.Context.Pos.PosPromotionItem>();
			int rows = Math.Max(tItem?.Length ?? 0, tCategory?.Length ?? 0);
			for (int i = 0; i < rows; i++)
			{
				int item = (tItem != null && i < tItem.Length) ? tItem[i] : 0;
				int cat = (tCategory != null && i < tCategory.Length) ? tCategory[i] : 0;
				string role = (tRole != null && i < tRole.Length) ? tRole[i] : "Both";
				if (item <= 0 && cat <= 0) continue;
				targets.Add(new CrossBuy.Models.Context.Pos.PosPromotionItem
				{ ItemId = item > 0 ? item : (int?)null, CategoryId = cat > 0 ? cat : (int?)null, Role = role });
			}

			var (ok, err, newId) = await _pos.SavePosPromotionAsync(PosCompanyId, dto, targets);
			if (!ok) { TempData["HyperErr"] = err; return RedirectToAction(nameof(PromotionEditor), new { id = id, branchId }); }
			TempData["HyperMsg"] = "promo-saved";
			return RedirectToAction(nameof(Promotions), new { branchId });
		}

		[HttpPost("setup/promotion/active")]
		[ValidateAntiForgeryToken]
		[CrossBuy.Models.PosPerm("manage")]
		public async Task<IActionResult> SetPromotionActive(int branchId, int id, bool active)
		{
			var (ok, err) = await _pos.SetPosPromotionActiveAsync(PosCompanyId, id, active);
			if (!ok) TempData["HyperErr"] = err; else TempData["HyperMsg"] = "promo-saved";
			return RedirectToAction(nameof(Promotions), new { branchId });
		}

		// The same pair PosPermAttribute consults, so a screen's answer and a POST's answer cannot drift.
		private async Task<bool> CanManagePosAsync(int? branchId)
		{
			var acc = HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IPosAccessService)) as CrossBuy.BL.Platform.IModuleAccessService;
			var ctxs = HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.Platform.IBusinessContextAccessor)) as CrossBuy.BL.Platform.IBusinessContextAccessor;
			if (acc == null || ctxs == null) return false;
			var bc = await ctxs.TryGetCurrentAsync(HttpContext.RequestAborted);
			if (bc == null) return false;
			return await acc.CanAsync(bc, "manage",
				branchId == null ? null : new CrossBuy.Models.Platform.PermissionTarget { BranchId = branchId.Value },
				HttpContext.RequestAborted);
		}

		[HttpGet("setup/capabilities")]
		public async Task<IActionResult> CapabilitySetup(int? branchId = null)
		{
			ViewBag.SidebarMenu = MainMenu.Hyper();
			var branches = await _db.Branches.AsNoTracking()
				.Where(b => b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper").OrderBy(b => b.ID)
				.Select(b => new HyperBranchItem { Id = b.ID, Name = b.Name, NameAr = b.NameAr })
				.ToListAsync();
			ViewBag.HyperBranches = branches;
			var cur = branchId ?? (branches.Count > 0 ? branches[0].Id : (int?)null);
			if (cur == null) { ViewBag.NoBranch = true; return View("~/Views/Hyper/Capabilities.cshtml"); }

			int bid = cur.Value;
			ViewBag.Branch = await _db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == bid);
			var rows = new List<HyperCapRow>();
			foreach (var c in Capabilities)
				rows.Add(new HyperCapRow { Key = c.key, Ar = c.ar, En = c.en, On = await _pos.IsCapabilityEnabledAsync(bid, c.key) });
			ViewBag.Caps = rows;
			ViewBag.HasCapConfig = await _pos.HasCapabilityConfigAsync(bid);
			ViewBag.NotBuilt = NotBuilt;
			// THE SCREEN MUST TELL THE TRUTH ABOUT WHAT THE VIEWER MAY DO. The POST carries
			// [PosPerm("manage")] and keeps it - hiding a control is not a control - but the GET carried
			// nothing, so anyone who could open the page got live checkboxes and a Save button that threw
			// them back to the dashboard. Asking the SAME pair the attribute asks is what keeps the two
			// answers from drifting apart; resolved per request because this is the only action that needs them.
			bool canManage = false;
			var __acc = HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IPosAccessService)) as CrossBuy.BL.Platform.IModuleAccessService;
			var __ctxs = HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.Platform.IBusinessContextAccessor)) as CrossBuy.BL.Platform.IBusinessContextAccessor;
			if (__acc != null && __ctxs != null)
			{
				var __bc = await __ctxs.TryGetCurrentAsync(HttpContext.RequestAborted);
				if (__bc != null)
					canManage = await __acc.CanAsync(__bc, "manage", new CrossBuy.Models.Platform.PermissionTarget { BranchId = bid }, HttpContext.RequestAborted);
			}
			ViewBag.CanManageCaps = canManage;
			return View("~/Views/Hyper/Capabilities.cshtml");
		}

		[HttpPost("setup/capabilities")]
		[ValidateAntiForgeryToken]
		[CrossBuy.Models.PosPerm("manage")]   // CBA001: the module authority, with branch isolation from branchId
		public async Task<IActionResult> SaveCapabilities(int branchId, string[]? on)
		{
			// The branch must be a HYPER branch. Without this check a crafted post could switch a
			// capability on a restaurant branch from the hypermarket's own setup screen, which is exactly
			// the lane separation this module is built around.
			var branch = await _db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == branchId && b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper");
			// The view localises this code: the back-office controller has no IStringLocalizer.
			if (branch == null) { TempData["HyperErr"] = "not-hyper-branch"; return RedirectToAction(nameof(CapabilitySetup)); }

			var chosen = new HashSet<string>(on ?? Array.Empty<string>(), StringComparer.Ordinal);
			var map = new Dictionary<string, bool>();
			foreach (var c in Capabilities)
			{
				// A reserved key keeps whatever it already had: the form never offers it, so a post that
				// names it was not produced by this screen.
				if (NotBuilt.Contains(c.key)) { map[c.key] = await _pos.IsCapabilityEnabledAsync(branchId, c.key); continue; }
				map[c.key] = chosen.Contains(c.key);
			}

			var (ok, err) = await _pos.SaveCapabilitiesAsync(branchId, map);
			if (!ok) TempData["HyperErr"] = err;
			else TempData["HyperMsg"] = "caps-saved";
			return RedirectToAction(nameof(CapabilitySetup), new { branchId });
		}

		// ============================================================================================
		// SCALES AND WEIGHED ITEMS — /hyper/setup/scales
		//
		// The lane has been able to PARSE a scale label since HM-3 and has never had a screen to describe
		// one. Both halves lived only in SQL: the branch's label format, and the per-item flag and code.
		// A shop that bought a scale, or added a line of loose produce, needed database access to sell it.
		//
		// WHY A WRONG FORMAT IS WORSE THAN A MISSING ONE: a mis-set field width does not fail. It reads the
		// wrong digits as the weight and sells 8.5 kg at the price of 0.850 — quietly, every time, until
		// someone reconciles stock. So the format is validated on the way in, against the 13 digits a scale
		// actually prints, and a non-numeric prefix is refused rather than stored.
		[HttpGet("setup/scales")]
		public async Task<IActionResult> ScaleSetup(int? branchId = null)
		{
			ViewBag.SidebarMenu = MainMenu.Hyper();
			var branches = await _db.Branches.AsNoTracking()
				.Where(b => b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper").OrderBy(b => b.ID)
				.Select(b => new HyperBranchItem { Id = b.ID, Name = b.Name, NameAr = b.NameAr })
				.ToListAsync();
			ViewBag.HyperBranches = branches;
			var cur = branchId ?? (branches.Count > 0 ? branches[0].Id : (int?)null);
			if (cur == null) { ViewBag.NoBranch = true; return View("~/Views/Hyper/ScaleSetup.cshtml"); }

			int bid = cur.Value;
			ViewBag.Branch = await _db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == bid);
			ViewBag.Setting = await _pos.GetPosSettingAsync(bid);
			ViewBag.WeightOn = await _pos.IsCapabilityEnabledAsync(bid, "Weight");

			// The company's weighed items, plus the ones that could become weighed. Both lists are needed:
			// the screen's job is as much "which of my items is sold by weight" as it is editing one.
			ViewBag.Weighed = await _db.Items.AsNoTracking()
				.Where(i => i.CompanyID == PosCompanyId && i.IsWeighted)
				.OrderBy(i => i.ScaleCode)
				.Select(i => new HyperWeighedRow { Id = i.ID, Name = i.Name, NameEn = i.NameEn, ScaleCode = i.ScaleCode, IsActive = i.IsActive })
				.ToListAsync();
			return View("~/Views/Hyper/ScaleSetup.cshtml");
		}

		[HttpPost("setup/scales")]
		[ValidateAntiForgeryToken]
		[CrossBuy.Models.PosPerm("manage")]
		public async Task<IActionResult> SaveScaleSetup(int branchId, string? prefix, int itemCodeLength, int valueLength, int valueDecimals)
		{
			var branch = await _db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == branchId && b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper");
			if (branch == null) { TempData["HyperErr"] = "not-hyper-branch"; return RedirectToAction(nameof(ScaleSetup)); }

			// ValueType and CheckAlgo are not offered: the lane implements exactly one of each, and a
			// dropdown with one real option and one that is refused downstream is a trap, not a choice.
			var (ok, err) = await _pos.SaveScaleSettingsAsync(branchId, prefix, itemCodeLength, valueLength, valueDecimals, "Weight", "EanMod10");
			if (!ok) TempData["HyperErr"] = err;
			else TempData["HyperMsg"] = "scale-saved";
			return RedirectToAction(nameof(ScaleSetup), new { branchId });
		}

		[HttpPost("setup/weighed")]
		[ValidateAntiForgeryToken]
		[CrossBuy.Models.PosPerm("manage")]
		public async Task<IActionResult> SaveWeighedItem(int branchId, int itemId, bool isWeighted, int? scaleCode)
		{
			var (ok, err) = await _pos.SaveWeighedItemAsync(PosCompanyId, itemId, isWeighted, scaleCode);
			if (!ok) TempData["HyperErr"] = err;
			else TempData["HyperMsg"] = "weighed-saved";
			return RedirectToAction(nameof(ScaleSetup), new { branchId });
		}

		// ============================================================================================
		// EXTRA BARCODES — /hyper/setup/barcodes
		//
		// One item, several barcodes, each in its own unit: the piece, the dozen, the case. The till has
		// resolved these since HM-2 behind the BarcodeMulti capability, and until now the only thing that
		// ever wrote one was a development seeding endpoint. A shop receiving a new case size had no way
		// to make it scannable.
		//
		// The two refusals this screen gives are the two the TILL already gives at sell time — a duplicate
		// barcode, and one printed inside the scale range. Catching them here means the queue never meets
		// them, which is the whole difference between a data screen and a data-entry hole.
		[HttpGet("setup/barcodes")]
		public async Task<IActionResult> Barcodes(int? branchId = null, string? q = null, int? itemId = null)
		{
			ViewBag.SidebarMenu = MainMenu.Hyper();
			var branches = await _db.Branches.AsNoTracking()
				.Where(b => b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper").OrderBy(b => b.ID)
				.Select(b => new HyperBranchItem { Id = b.ID, Name = b.Name, NameAr = b.NameAr })
				.ToListAsync();
			ViewBag.HyperBranches = branches;
			var cur = branchId ?? (branches.Count > 0 ? branches[0].Id : (int?)null);
			if (cur == null) { ViewBag.NoBranch = true; return View("~/Views/Hyper/Barcodes.cshtml"); }
			int bid = cur.Value;

			ViewBag.Branch = await _db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == bid);
			ViewBag.MultiOn = await _pos.IsCapabilityEnabledAsync(bid, "BarcodeMulti");
			ViewBag.Setting = await _pos.GetPosSettingAsync(bid);
			ViewBag.Q = q;

			if (!string.IsNullOrWhiteSpace(q))
			{
				var (hits, _) = await _items.SearchItemsAsync(PosCompanyId, q!.Trim(), null, "Stockable", true, 1, 12);
				ViewBag.Hits = hits;
			}
			if (itemId is > 0)
			{
				var item = await _db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == itemId && i.CompanyID == PosCompanyId);
				ViewBag.Item = item;
				if (item != null) ViewBag.Codes = await _pos.GetItemBarcodesAsync(PosCompanyId, item.ID);
			}
			ViewBag.Units = await _db.UnitsOfMeasure.AsNoTracking()
				.Where(u => u.CompanyID == PosCompanyId).OrderBy(u => u.Code).ToListAsync();
			return View("~/Views/Hyper/Barcodes.cshtml");
		}

		[HttpPost("setup/barcodes/add")]
		[ValidateAntiForgeryToken]
		[CrossBuy.Models.PosPerm("manage")]
		public async Task<IActionResult> AddBarcode(int branchId, int itemId, string? barcode, int uomId)
		{
			var (ok, err) = await _pos.AddItemBarcodeAsync(PosCompanyId, branchId, itemId, barcode, uomId);
			if (!ok) TempData["HyperErr"] = err;
			else TempData["HyperMsg"] = "barcode-added";
			return RedirectToAction(nameof(Barcodes), new { branchId, itemId });
		}

		[HttpPost("setup/barcodes/delete")]
		[ValidateAntiForgeryToken]
		[CrossBuy.Models.PosPerm("manage")]
		public async Task<IActionResult> DeleteBarcode(int branchId, int itemId, int id)
		{
			var (ok, err) = await _pos.DeleteItemBarcodeAsync(PosCompanyId, id);
			if (!ok) TempData["HyperErr"] = err;
			else TempData["HyperMsg"] = "barcode-removed";
			return RedirectToAction(nameof(Barcodes), new { branchId, itemId });
		}

		// ============================================================================================
		// THE Z REPORT — /hyper/reports/zreport
		//
		// The number this report exists for is the CASH VARIANCE: expected cash against what the cashier
		// actually counted. Everything else on it is the working that produces that one figure, and a
		// manager reading it at close of day is really asking one question — does the drawer agree?
		//
		// The calculation is NOT new. GetShiftZReportAsync has existed since RC-6b and the lane already
		// shows it inline for the current shift. What was missing is the ability to read a shift that has
		// already closed, on a terminal you are not standing at, which is when the question actually gets
		// asked. So this adds a reader, not a second calculation — the figures a manager discusses and the
		// figures the cashier saw are the same figures, from the same method.
		[HttpGet("reports/zreport")]
		public async Task<IActionResult> ZReport(int? branchId = null, int? shiftId = null, string? q = null, int page = 1)
		{
			ViewBag.SidebarMenu = MainMenu.Hyper();
			var branches = await _db.Branches.AsNoTracking()
				.Where(b => b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper").OrderBy(b => b.ID)
				.Select(b => new HyperBranchItem { Id = b.ID, Name = b.Name, NameAr = b.NameAr })
				.ToListAsync();
			ViewBag.HyperBranches = branches;
			var cur = branchId ?? (branches.Count > 0 ? branches[0].Id : (int?)null);
			if (cur == null) { ViewBag.NoBranch = true; return View("~/Views/Hyper/ZReport.cshtml"); }
			int bid = cur.Value;
			ViewBag.Branch = await _db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == bid);

			// Only THIS branch's terminals, so a manager cannot read another store's drawer from here.
			var terminals = await _pos.GetTerminalsAsync(bid);
			var termIds = terminals.Select(t => t.ID).ToList();
			ViewBag.Terminals = terminals;

			// SEARCH AND PAGING, not a Take(25). The cap was not merely a long list: it made every shift
			// past the newest 25 UNREACHABLE, so an older shift's Z report simply could not be opened. A
			// report you cannot reach for last Tuesday is not a report.
			var shiftQuery = _db.PosShifts.AsNoTracking().Where(sh => termIds.Contains(sh.TerminalId));
			q = (q ?? "").Trim();
			if (q.Length > 0)
			{
				// Match the terminal CODE the manager reads on the list, and the shift type. A date is
				// matched as text so "2026/11" narrows to a month without a second control.
				var hit = terminals.Where(t => t.Code != null && t.Code.Contains(q, StringComparison.OrdinalIgnoreCase))
					.Select(t => t.ID).ToList();
				shiftQuery = shiftQuery.Where(sh => hit.Contains(sh.TerminalId) || sh.ShiftType.Contains(q));
			}
			const int shiftPageSize = 15;
			int shiftTotal = await shiftQuery.CountAsync();
			if (page < 1) page = 1;
			int shiftPages = Math.Max(1, (int)Math.Ceiling(shiftTotal / (double)shiftPageSize));
			if (page > shiftPages) page = shiftPages;
			ViewBag.Q = q; ViewBag.Page = page; ViewBag.Pages = shiftPages; ViewBag.Total = shiftTotal;

			var shifts = await shiftQuery
				.OrderByDescending(sh => sh.OpenedAt)
				.Skip((page - 1) * shiftPageSize).Take(shiftPageSize)
				.Select(sh => new HyperShiftRow
				{
					Id = sh.ID, TerminalId = sh.TerminalId, ShiftType = sh.ShiftType, Status = sh.Status,
					OpenedAt = sh.OpenedAt, ClosedAt = sh.ClosedAt, OpeningFloat = sh.OpeningFloat
				})
				.ToListAsync();
			ViewBag.Shifts = shifts;

			if (shiftId is > 0)
			{
				// Re-check the shift belongs to this branch: the list is a convenience, never the authority.
				// Look it up against the BRANCH, not against the current page: a shift reached by link or
				// by a bookmark is legitimate even when it is not in the fifteen rows on screen. The branch
				// check is what keeps the isolation; the page is only what is being displayed.
				var sh = shifts.FirstOrDefault(x => x.Id == shiftId.Value)
					?? await _db.PosShifts.AsNoTracking()
						.Where(x => x.ID == shiftId.Value && termIds.Contains(x.TerminalId))
						.Select(x => new HyperShiftRow
						{
							Id = x.ID, TerminalId = x.TerminalId, ShiftType = x.ShiftType, Status = x.Status,
							OpenedAt = x.OpenedAt, ClosedAt = x.ClosedAt, OpeningFloat = x.OpeningFloat
						}).FirstOrDefaultAsync();
				if (sh != null)
				{
					ViewBag.Z = await _pos.GetShiftZReportAsync(PosCompanyId, sh.TerminalId, sh.Id);
					ViewBag.ShiftId = sh.Id;
				}
				else TempData["HyperErr"] = "shift-not-here";
			}

			int ccyId = await _db.BranchPosSettings.AsNoTracking().Where(x => x.BranchId == bid)
				.Select(x => x.DefaultCurrencyId ?? 0).FirstOrDefaultAsync();
			ViewBag.Dp = await _rounding.DecimalsAsync(PosCompanyId, ccyId == 0 ? (int?)null : ccyId, bid);
			return View("~/Views/Hyper/ZReport.cshtml");
		}

		// ============================================================================================
		// LOYALTY SETUP — /hyper/setup/loyalty
		//
		// Points per unit of currency on the NET value of eligible lines. Earning has worked since HM-9
		// and its configuration has only ever been reachable from SQL or a development endpoint, so a
		// shop could not change its own rate.
		//
		// THE SCREEN SAYS WHAT THE CAPABILITY DOES AND DOES NOT DO. Earning accrues to a ledger; spending
		// those points does not exist yet. That is why the Loyalty capability ships OFF — switching it on
		// today gives customers a balance they cannot spend, which is worse than no programme at all, and
		// a setup screen that hid this would be setting a shop up to promise something it cannot keep.
		[HttpGet("setup/loyalty")]
		public async Task<IActionResult> LoyaltySetup(int? branchId = null, string? catQ = null, int catPage = 1)
		{
			ViewBag.SidebarMenu = MainMenu.Hyper();
			var branches = await _db.Branches.AsNoTracking()
				.Where(b => b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper").OrderBy(b => b.ID)
				.Select(b => new HyperBranchItem { Id = b.ID, Name = b.Name, NameAr = b.NameAr })
				.ToListAsync();
			ViewBag.HyperBranches = branches;
			var cur = branchId ?? (branches.Count > 0 ? branches[0].Id : (int?)null);
			if (cur == null) { ViewBag.NoBranch = true; return View("~/Views/Hyper/LoyaltySetup.cshtml"); }
			int bid = cur.Value;

			ViewBag.Branch = await _db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == bid);
			ViewBag.Setting = await _pos.GetPosSettingAsync(bid);
			ViewBag.LoyaltyOn = await _pos.IsCapabilityEnabledAsync(bid, "Loyalty");

			// HM-22: redemption needs THREE things, and a screen that reports only the capability would send a
			// shop looking for a feature that is switched on and still cannot run. Each is reported separately
			// because each is fixed in a different place by a different person.
			ViewBag.PointValue = _posCompany.LoyaltyPointValue;
			ViewBag.PointsTenderOk = await _db.BranchPaymentMethods.AsNoTracking()
				.AnyAsync(m => m.BranchId == bid && m.IsActive && m.PaymentMethod == "Points" && m.TargetAccountId != null);
			ViewBag.CompanyRate = await _db.Companies.AsNoTracking()
				.Where(c => c.CompanyID == PosCompanyId).Select(c => c.LoyaltyPointsPerCurrencyUnit).FirstOrDefaultAsync();
			// EVERY LIST GETS SEARCH AND PAGING (owner standard). This one shipped as all 49 categories in one
			// unbroken column running off the bottom of the page, which is unusable now and gets worse with
			// every category the shop adds. Paging also bounds the query instead of trusting the count to stay small.
			catQ = (catQ ?? "").Trim();
			var catQuery = _db.ItemCategories.AsNoTracking().Where(c => c.CompanyID == PosCompanyId);
			if (catQ.Length > 0) catQuery = catQuery.Where(c => c.Name.Contains(catQ) || (c.NameEn != null && c.NameEn.Contains(catQ)));
			const int catPageSize = 15;
			int catTotal = await catQuery.CountAsync();
			if (catPage < 1) catPage = 1;
			int catPages = Math.Max(1, (int)Math.Ceiling(catTotal / (double)catPageSize));
			if (catPage > catPages) catPage = catPages;
			ViewBag.Categories = await catQuery.OrderBy(c => c.Name)
				.Skip((catPage - 1) * catPageSize).Take(catPageSize)
				.Select(c => new HyperCatRow { Id = c.ID, Name = c.Name, NameEn = c.NameEn, Eligible = c.LoyaltyEligible })
				.ToListAsync();
			ViewBag.CatQ = catQ; ViewBag.CatPage = catPage; ViewBag.CatPages = catPages; ViewBag.CatTotal = catTotal;
			return View("~/Views/Hyper/LoyaltySetup.cshtml");
		}

		[HttpPost("setup/loyalty")]
		[ValidateAntiForgeryToken]
		[CrossBuy.Models.PosPerm("manage")]
		public async Task<IActionResult> SaveLoyalty(int branchId, decimal? branchRate, decimal? companyRate, bool clearBranch = false)
		{
			var branch = await _db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == branchId && b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper");
			if (branch == null) { TempData["HyperErr"] = "not-hyper-branch"; return RedirectToAction(nameof(LoyaltySetup)); }

			// "Clear" is a distinct action from "set to zero": null inherits the company rate, zero awards
			// nothing. A single number box cannot express both.
			var (ok, err) = await _pos.SaveLoyaltyRateAsync(PosCompanyId, branchId, clearBranch ? null : branchRate, companyRate);
			if (!ok) TempData["HyperErr"] = err;
			else TempData["HyperMsg"] = "loyalty-saved";
			return RedirectToAction(nameof(LoyaltySetup), new { branchId });
		}

		[HttpPost("setup/loyalty/category")]
		[ValidateAntiForgeryToken]
		[CrossBuy.Models.PosPerm("manage")]
		public async Task<IActionResult> SaveCategoryLoyalty(int branchId, int categoryId, bool eligible, string? catQ = null, int catPage = 1)
		{
			var (ok, err) = await _pos.SaveCategoryLoyaltyAsync(PosCompanyId, categoryId, eligible);
			if (!ok) TempData["HyperErr"] = err;
			else TempData["HyperMsg"] = "loyalty-category-saved";
			// Carry the search and the page back, or a manager working through page 3 is thrown to page 1
			// on every single toggle and has to find their place again each time.
			return RedirectToAction(nameof(LoyaltySetup), new { branchId, catQ, catPage });
		}

		// ============================================================================================
		// TAX & INVOICING — /hyper/setup/tax
		//
		// THIS SCREEN WRITES ONE COLUMN, and most of what it shows is somebody else's work made visible.
		//
		// The tax ENGINE is PosOrderService.ResolveTaxRateAsync and it has rated every line since HM-D38:
		// the item's own tax code wins, else the branch's, else the company's default VAT, else zero. The
		// tax CODES are the company's and live in Accounting > Tax codes, which is where they are created
		// and where their rates are edited — this screen links there rather than growing a second editor
		// over the same table.
		//
		// The gap was the MIDDLE STEP. Nothing outside a development endpoint could set a branch's tax code,
		// so a store in a different jurisdiction (a Kuwait branch that should be VATEX at 0%) could only be
		// configured by tagging every one of its items, or by SQL.
		//
		// WHY THE OVERRIDING ITEMS ARE LISTED: an item with its own tax code beats the branch, so a manager
		// who sets this field and expects every line to follow is wrong about any item in that list. Saying
		// so costs one query and prevents the one mistake this screen can cause.
		[HttpGet("setup/tax")]
		public async Task<IActionResult> TaxSetup(int? branchId = null)
		{
			ViewBag.SidebarMenu = MainMenu.Hyper();
			var branches = await _db.Branches.AsNoTracking()
				.Where(b => b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper").OrderBy(b => b.ID)
				.Select(b => new HyperBranchItem { Id = b.ID, Name = b.Name, NameAr = b.NameAr })
				.ToListAsync();
			ViewBag.HyperBranches = branches;
			var cur = branchId ?? (branches.Count > 0 ? branches[0].Id : (int?)null);
			if (cur == null) { ViewBag.NoBranch = true; return View("~/Views/Hyper/TaxSetup.cshtml"); }
			int bid = cur.Value;

			ViewBag.Branch = await _db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == bid);
			ViewBag.Setting = await _pos.GetPosSettingAsync(bid);

			// Only VAT codes: a WHT code is a purchase-side withholding rate and can never rate a sale.
			var codes = await _db.TaxCodes.AsNoTracking()
				.Where(t => t.CompanyID == PosCompanyId && t.Kind == "VAT")
				.OrderByDescending(t => t.IsActive).ThenBy(t => t.Name)
				.ToListAsync();
			ViewBag.TaxCodes = codes;
			ViewBag.CompanyDefault = codes.FirstOrDefault(t => t.IsDefault && t.IsActive);

			// The items that will IGNORE whatever is chosen here, because they carry their own code.
			ViewBag.Overriding = await (from it in _db.Items.AsNoTracking()
										join tc in _db.TaxCodes.AsNoTracking() on it.DefaultTaxCodeId equals (int?)tc.ID
										// BOTH sides are company-scoped. Filtering only the item would let a row that points
										// at another company's tax code render that code's NAME AND RATE on this screen.
										where it.CompanyID == PosCompanyId && tc.CompanyID == PosCompanyId
											&& it.DefaultTaxCodeId != null && it.IsActive
										orderby it.Name
										select new HyperTaxItemRow
										{
											Id = it.ID, Code = it.ItemCode, Name = it.Name, NameEn = it.NameEn,
											TaxName = tc.Name, TaxNameEn = tc.NameEn, Rate = tc.Rate
										}).Take(50).ToListAsync();
			ViewBag.OverridingTotal = await _db.Items.AsNoTracking()
				.CountAsync(it => it.CompanyID == PosCompanyId && it.DefaultTaxCodeId != null && it.IsActive);

			// The official A4 invoice is a CAPABILITY, not a tax setting — but a manager asking "can this
			// branch issue a tax invoice?" asks it here, so the answer and the way to change it belong here.
			ViewBag.OfficialInvoiceOn = await _pos.IsCapabilityEnabledAsync(bid, "OfficialInvoice");
			return View("~/Views/Hyper/TaxSetup.cshtml");
		}

		[HttpPost("setup/tax")]
		[ValidateAntiForgeryToken]
		[CrossBuy.Models.PosPerm("manage")]
		public async Task<IActionResult> SaveBranchTax(int branchId, int? taxCodeId)
		{
			var branch = await _db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == branchId && b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper");
			if (branch == null) { TempData["HyperErr"] = "not-hyper-branch"; return RedirectToAction(nameof(TaxSetup)); }

			var (ok, err) = await _pos.SaveBranchTaxCodeAsync(PosCompanyId, branchId, taxCodeId);
			if (!ok) TempData["HyperErr"] = err;
			else TempData["HyperMsg"] = "tax-saved";
			return RedirectToAction(nameof(TaxSetup), new { branchId });
		}

		// ============================================================================================
		// THE CASH DRAWER — /hyper/cash
		//
		// THIS SCREEN EXISTS BECAUSE THE CLOSE WAS WRONG, not because a screen was missing.
		//
		// Expected cash was opening float + cash taken + cash tips. Nothing subtracted the money a manager
		// physically removes during a shift — the nightly drop to the safe, a supplier paid out of the till,
		// change sent to another lane. So the drawer counted SHORT by exactly the amount that had been safely
		// banked, and the close posted that difference to 520111 as a cash loss. A shop that drops twice a day
		// was manufacturing an expense out of its own prudence, and a real theft of the same size was buried in
		// the noise it created.
		//
		// The movement posts to the LEDGER (SourceType PosShiftCashMovement), not to a POS table, because that
		// is where the money actually went. ExpectedCashAsync reads it back from there, so the Z report, the
		// close and this screen all agree without any of them holding a private copy of the figure.
		[HttpGet("cash")]
		public async Task<IActionResult> CashDrawer(int? branchId = null, int? shiftId = null)
		{
			ViewBag.SidebarMenu = MainMenu.Hyper();
			var branches = await _db.Branches.AsNoTracking()
				.Where(b => b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper").OrderBy(b => b.ID)
				.Select(b => new HyperBranchItem { Id = b.ID, Name = b.Name, NameAr = b.NameAr })
				.ToListAsync();
			ViewBag.HyperBranches = branches;
			var cur = branchId ?? (branches.Count > 0 ? branches[0].Id : (int?)null);
			if (cur == null) { ViewBag.NoBranch = true; return View("~/Views/Hyper/CashDrawer.cshtml"); }
			int bid = cur.Value;
			ViewBag.Branch = await _db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == bid);

			// Only THIS branch's terminals: a manager must not reach another store's drawer from here.
			var terminals = await _pos.GetTerminalsAsync(bid);
			var termIds = terminals.Select(t => t.ID).ToList();
			ViewBag.Terminals = terminals;

			// OPEN shifts only. A closed shift's expectation is already reconciled and its variance posted;
			// offering it here would invite a change to a figure somebody has already signed off.
			var shifts = await _db.PosShifts.AsNoTracking()
				.Where(sh => termIds.Contains(sh.TerminalId) && sh.Status == "Open")
				.OrderByDescending(sh => sh.OpenedAt)
				.Select(sh => new HyperShiftRow
				{
					Id = sh.ID, TerminalId = sh.TerminalId, ShiftType = sh.ShiftType, Status = sh.Status,
					OpenedAt = sh.OpenedAt, ClosedAt = sh.ClosedAt, OpeningFloat = sh.OpeningFloat
				})
				.ToListAsync();
			ViewBag.Shifts = shifts;

			var sel = shiftId is > 0 ? shifts.FirstOrDefault(x => x.Id == shiftId!.Value) : shifts.FirstOrDefault();
			if (shiftId is > 0 && sel == null) TempData["HyperErr"] = "shift-not-open-here";
			if (sel != null)
			{
				ViewBag.Shift = sel;
				ViewBag.Movements = await _pos.GetShiftCashMovementsAsync(PosCompanyId, sel.Id);
				var shift = await _db.PosShifts.AsNoTracking().FirstAsync(x => x.ID == sel.Id);
				ViewBag.Expected = await _pos.ExpectedCashAsync(PosCompanyId, shift);
			}

			// The other side of the entry. Cash, bank and expense leaves are what a drop or a payout actually
			// hits; offering the whole chart would invite posting a drawer drop to revenue.
			ViewBag.Accounts = await _db.Accounts.AsNoTracking()
				.Where(a => a.CompanyID == PosCompanyId && a.IsPostable && a.IsActive
					&& (a.Code.StartsWith("11") || a.Code.StartsWith("5")))
				.OrderBy(a => a.Code).ToListAsync();

			int ccyId = await _db.BranchPosSettings.AsNoTracking().Where(x => x.BranchId == bid)
				.Select(x => x.DefaultCurrencyId ?? 0).FirstOrDefaultAsync();
			ViewBag.Dp = await _rounding.DecimalsAsync(PosCompanyId, ccyId == 0 ? (int?)null : ccyId, bid);
			return View("~/Views/Hyper/CashDrawer.cshtml");
		}

		// Recording a movement MOVES REAL MONEY and posts a journal entry, so it is manager-level, not
		// cashier-level: the person who counts the drawer must not also be the person who decides how much
		// left it unobserved.
		[HttpPost("cash")]
		[ValidateAntiForgeryToken]
		[CrossBuy.Models.PosPerm("manage")]
		public async Task<IActionResult> RecordCashMovement(int branchId, int shiftId, string kind, decimal amount, int counterAccountId, string? note)
		{
			// The shift must be on a terminal of a HYPER branch the caller asked for — re-derived here rather
			// than trusted from the form, which is the only reason the branch is a parameter at all.
			var branch = await _db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == branchId && b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper");
			if (branch == null) { TempData["HyperErr"] = "not-hyper-branch"; return RedirectToAction(nameof(CashDrawer)); }

			var terminals = await _pos.GetTerminalsAsync(branchId);
			var termIds = terminals.Select(t => t.ID).ToList();
			var shift = await _db.PosShifts.AsNoTracking().FirstOrDefaultAsync(x => x.ID == shiftId && termIds.Contains(x.TerminalId));
			if (shift == null) { TempData["HyperErr"] = "shift-not-open-here"; return RedirectToAction(nameof(CashDrawer), new { branchId }); }

			// The journal entry carries the author. A drawer movement is the one place where «who» is the
			// whole point of the record, and it was posting as null.
			var scope = await _company.ResolveAsync();
			var (ok, err, _) = await _pos.RecordShiftCashMovementAsync(PosCompanyId, shift.TerminalId, shiftId,
				kind, amount, counterAccountId, note, DateTime.Today, scope?.EmployeeId);
			if (!ok) TempData["HyperErr"] = err;
			else TempData["HyperMsg"] = "cash-movement-saved";
			return RedirectToAction(nameof(CashDrawer), new { branchId, shiftId });
		}

		// ============================================================================================
		// BULK PRICE CHANGE — /hyper/pricing/bulk
		//
		// THE MOST DESTRUCTIVE ROUTINE ACTION IN THE MODULE, so it is the one screen that refuses to act on
		// the first click. Re-pricing a category overwrites prices in place; there is no previous-price column
		// to roll back to. A misplaced decimal or a wrong sign is therefore not an inconvenience, it is a day
		// of manual repair — and the person who made it will not know until a customer complains.
		//
		// So: GET previews, POST applies, and the POST carries the exact item ids the preview displayed. An
		// item that joined the category between the two is NOT swept into a change nobody looked at.
		//
		// The margin floor is IPricingService's, unchanged and uncopied. When the company's setting is Block,
		// a row under the floor is refused by the WRITER, not merely painted red — a screen that objects and
		// then saves anyway is worse than one that never objected.
		[HttpGet("pricing/bulk")]
		public async Task<IActionResult> BulkPrice(int? categoryId = null, string? q = null, string mode = "Percent", decimal? value = null)
		{
			ViewBag.SidebarMenu = MainMenu.Hyper();
			ViewBag.Categories = await _db.ItemCategories.AsNoTracking()
				.Where(c => c.CompanyID == PosCompanyId).OrderBy(c => c.Name)
				.Select(c => new HyperCatRow { Id = c.ID, Name = c.Name, NameEn = c.NameEn })
				.ToListAsync();
			ViewBag.CategoryId = categoryId; ViewBag.Q = q; ViewBag.Mode = mode; ViewBag.Value = value;

			int ccyId = 0;
			ViewBag.Dp = await _rounding.DecimalsAsync(PosCompanyId, ccyId == 0 ? (int?)null : ccyId);

			// NOTHING IS PREVIEWED UNTIL A CHANGE IS ACTUALLY ASKED FOR. Landing on the screen must not render
			// a table of "new prices" that nobody requested — that is how a preview gets mistaken for a record.
			if (value != null)
			{
				// Refuse an illegal change HERE, with the same rule the apply uses, rather than rendering a
				// table of prices that the button would then reject.
				var bad = CrossBuy.BL.PosSetupService.ValidateBulkChange(mode, value.Value);
				if (bad != null) TempData["HyperErr"] = bad;
				else
				{
					ViewBag.Rows = await _pos.PreviewBulkPriceChangeAsync(PosCompanyId, categoryId, q, mode, value.Value, null);
					ViewBag.Previewed = true;
				}
			}
			return View("~/Views/Hyper/BulkPrice.cshtml");
		}

		[HttpPost("pricing/bulk")]
		[ValidateAntiForgeryToken]
		[CrossBuy.Models.PosPerm("manage")]
		public async Task<IActionResult> ApplyBulkPrice(int? categoryId, string? q, string mode, decimal value, List<string>? seen)
		{
			// Each entry is «itemId:priceTheOperatorSaw», self-describing so a reordered post cannot pair an id
			// with another item's price the way two parallel lists silently would.
			var shown = new Dictionary<int, decimal>();
			foreach (var raw in seen ?? new List<string>())
			{
				var bits = (raw ?? "").Split(':');
				if (bits.Length != 2) continue;
				if (!int.TryParse(bits[0], out var id)) continue;
				if (!decimal.TryParse(bits[1], System.Globalization.NumberStyles.Any,
					System.Globalization.CultureInfo.InvariantCulture, out var pr)) continue;
				shown[id] = pr;
			}

			var (changed, blocked, unchanged, stale, err) = await _pos.ApplyBulkPriceChangeAsync(
				PosCompanyId, categoryId, q, mode, value, null, shown);
			if (err != null) TempData["HyperErr"] = err;
			else
			{
				// The counts go back in the message because "saved" is not an answer here. A manager who asked to
				// re-price 120 items and silently re-priced 93 of them needs to be told about the other 27.
				TempData["HyperMsg"] = $"bulk-price-done|{changed}|{blocked}|{unchanged}|{stale}";
			}
			return RedirectToAction(nameof(BulkPrice), new { categoryId, q });
		}

		// ============================================================================================
		// COUPONS — /hyper/coupons
		//
		// Authoring only. SPENDING a coupon lives in PosCouponService and happens at the till; this screen
		// never applies one and never adjusts UsageCount, because that counter is claimed atomically by the
		// till and a back-office write racing it would be the one bug the atomic claim exists to prevent.
		//
		// WHAT THE LIST IS ACTUALLY FOR: seeing what is left. A coupon campaign is a liability the shop has
		// issued into the world, and the question asked of this screen is «how much of it is still
		// outstanding», not «what did I name it». So remaining uses lead, not the code.
		[HttpGet("coupons")]
		public async Task<IActionResult> Coupons(int? id = null)
		{
			ViewBag.SidebarMenu = MainMenu.Hyper();
			ViewBag.Coupons = await _db.PosCoupons.AsNoTracking()
				.Where(c => c.CompanyID == PosCompanyId)
				.OrderByDescending(c => c.IsActive).ThenBy(c => c.Code)
				.ToListAsync();
			if (id is > 0)
				ViewBag.Edit = await _db.PosCoupons.AsNoTracking().FirstOrDefaultAsync(c => c.ID == id.Value && c.CompanyID == PosCompanyId);

			// Redemptions per coupon, so the list can show what has actually been taken up rather than only
			// the counter. They are two different facts: the counter also counts claims that were removed.
			ViewBag.Redeemed = await _db.PosCouponRedemptions.AsNoTracking()
				.Where(r => r.CompanyID == PosCompanyId)
				.GroupBy(r => r.CouponId)
				.Select(g => new { CouponId = g.Key, Count = g.Count(), Total = g.Sum(x => x.DiscountAmount) })
				.ToDictionaryAsync(x => x.CouponId, x => (x.Count, x.Total));

			int ccy = await _db.BranchPosSettings.AsNoTracking()
				.Where(x => _db.Branches.Any(b => b.ID == x.BranchId && b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper"))
				.Select(x => x.DefaultCurrencyId ?? 0).FirstOrDefaultAsync();
			ViewBag.Dp = await _rounding.DecimalsAsync(PosCompanyId, ccy == 0 ? (int?)null : ccy);
			return View("~/Views/Hyper/Coupons.cshtml");
		}

		[HttpPost("coupons")]
		[ValidateAntiForgeryToken]
		[CrossBuy.Models.PosPerm("manage")]
		public async Task<IActionResult> SaveCoupon(int id, string? code, string? name, string? nameEn,
			string discountType, decimal value, decimal? minBasket, decimal? maxDiscount,
			DateTime? validFrom, DateTime? validTo, int? usageLimit, int? perCustomerLimit,
			bool requiresCustomer = false, bool isActive = true)
		{
			code = (code ?? "").Trim();
			name = (name ?? "").Trim();
			if (code.Length == 0) { TempData["HyperErr"] = "coupon-code-required"; return RedirectToAction(nameof(Coupons)); }
			if (name.Length == 0) name = code;
			if (discountType != "Percent" && discountType != "Amount")
			{ TempData["HyperErr"] = "coupon-type-unknown"; return RedirectToAction(nameof(Coupons)); }
			if (value <= 0) { TempData["HyperErr"] = "coupon-value-positive"; return RedirectToAction(nameof(Coupons)); }
			if (discountType == "Percent" && value > 100)
			{ TempData["HyperErr"] = "coupon-percent-over-100"; return RedirectToAction(nameof(Coupons)); }
			if (validFrom != null && validTo != null && validFrom > validTo)
			{ TempData["HyperErr"] = "coupon-dates-reversed"; return RedirectToAction(nameof(Coupons)); }
			// NULL means unlimited; ZERO would mean "may never be used", which nobody wants to create by
			// typing a 0 into a box meaning «no limit». Refuse rather than guess which they meant.
			if (usageLimit is <= 0) { TempData["HyperErr"] = "coupon-limit-zero"; return RedirectToAction(nameof(Coupons)); }
			if (perCustomerLimit is <= 0) { TempData["HyperErr"] = "coupon-limit-zero"; return RedirectToAction(nameof(Coupons)); }

			bool dup = await _db.PosCoupons.AnyAsync(c => c.CompanyID == PosCompanyId && c.Code == code && c.ID != id);
			if (dup) { TempData["HyperErr"] = "coupon-code-taken"; return RedirectToAction(nameof(Coupons)); }

			var c2 = id > 0
				? await _db.PosCoupons.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == PosCompanyId)
				: new CrossBuy.Models.Context.Pos.PosCoupon { CompanyID = PosCompanyId, CreatedAt = DateTime.UtcNow };
			if (c2 == null) { TempData["HyperErr"] = "coupon-not-found"; return RedirectToAction(nameof(Coupons)); }

			// A limit may never be set BELOW what has already gone out. Doing so would not un-issue those
			// uses; it would only make the remaining figure negative and the screen dishonest.
			if (usageLimit != null && usageLimit.Value < c2.UsageCount)
			{ TempData["HyperErr"] = "coupon-limit-below-used"; return RedirectToAction(nameof(Coupons)); }

			c2.Code = code; c2.Name = name; c2.NameEn = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn!.Trim();
			c2.DiscountType = discountType; c2.Value = value;
			c2.MinBasket = minBasket; c2.MaxDiscount = discountType == "Percent" ? maxDiscount : null;
			c2.ValidFrom = validFrom; c2.ValidTo = validTo;
			c2.UsageLimit = usageLimit; c2.PerCustomerLimit = perCustomerLimit;
			c2.RequiresCustomer = requiresCustomer; c2.IsActive = isActive;
			// UsageCount is NEVER written here. The till owns it.
			if (id <= 0) _db.PosCoupons.Add(c2);
			await _db.SaveChangesAsync();
			TempData["HyperMsg"] = "coupon-saved";
			return RedirectToAction(nameof(Coupons));
		}

		[HttpGet("")]
		[HttpGet("dashboard")]
		public async Task<IActionResult> Dashboard(int? branchId = null)
		{
			ViewBag.SidebarMenu = MainMenu.Hyper();   // _LayoutBackend renders this as the sidebar

			// ISOLATION: only hyper-activity branches ever appear here (a restaurant branch is never listed).
			var hyperBranches = await _db.Branches.AsNoTracking()
				.Where(b => b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper")
				.OrderBy(b => b.ID)
				.Select(b => new HyperBranchItem { Id = b.ID, Name = b.Name, NameAr = b.NameAr })
				.ToListAsync();
			ViewBag.HyperBranches = hyperBranches;

			var cur = branchId ?? (hyperBranches.Count > 0 ? hyperBranches[0].Id : (int?)null);
			if (cur == null) { ViewBag.NoBranch = true; return View(); }
			int bid = cur.Value;

			ViewBag.Branch = await _db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == bid);

			// Capabilities read through the ONE central gate (HM-0's first real consumer) — never a direct EF query.
			var caps = new List<HyperCapRow>();
			foreach (var c in Capabilities)
				caps.Add(new HyperCapRow { Key = c.key, Ar = c.ar, En = c.en, On = await _pos.IsCapabilityEnabledAsync(bid, c.key) });
			ViewBag.Caps = caps;
			// distinguish "no preset applied at all" from "configured but off" (default stays false either way)
			ViewBag.HasCapConfig = await _pos.HasCapabilityConfigAsync(bid);

			ViewBag.Setting = await _pos.GetPosSettingAsync(bid);

			var terminals = await _pos.GetTerminalsAsync(bid);
			ViewBag.Terminals = terminals;
			var open = new Dictionary<int, PosShift?>();
			foreach (var t in terminals) open[t.ID] = await _pos.GetOpenShiftAsync(t.ID);
			ViewBag.OpenShifts = open;

			// ===================== HM-23 — WHAT IS HAPPENING TODAY =====================
			//
			// Everything above this line describes how the branch is CONFIGURED. None of it answers the question
			// a manager actually opens this page with, which is what the shop has done since it opened this
			// morning. A landing screen that only reports its own setup is a screen people stop opening.
			//
			// THIS IS A SUMMARY, NOT A SECOND REPORT. Every figure here links to the screen that owns it —
			// /hyper/sales for the detail, /hyper/cash for the drawer, /hyper/reports/zreport for the shift.
			// Recomputing any of them differently here is how two screens start disagreeing about one day.
			var today = DateTime.Today;
			var termIds = terminals.Select(t => t.ID).ToList();

			// PAID orders only, on THIS branch's terminals, closed today. An open basket is not a sale, and a
			// merged order would otherwise be counted twice.
			var todayOrders = await _db.PosOrders.AsNoTracking()
				.Where(o => o.CompanyId == PosCompanyId && o.BranchId == bid && o.Status == "Paid"
					&& o.MergedIntoOrderId == null
					&& o.ClosedAt != null && o.ClosedAt.Value.Date == today)
				.Select(o => new { o.GrandTotal })
				.ToListAsync();
			ViewBag.TodayCount = todayOrders.Count;
			ViewBag.TodayTotal = todayOrders.Sum(o => o.GrandTotal);

			// The open drawers, each with what it SHOULD hold right now. This is the one number on the page a
			// manager can act on before close rather than after it.
			var drawers = new List<HyperDrawerRow>();
			foreach (var t in terminals)
			{
				var sh = open.TryGetValue(t.ID, out var o2) ? o2 : null;
				if (sh == null) continue;
				var mv = await _pos.GetShiftCashMovementsAsync(PosCompanyId, sh.ID);
				drawers.Add(new HyperDrawerRow
				{
					ShiftId = sh.ID, TerminalCode = t.Code, ShiftType = sh.ShiftType, OpenedAt = sh.OpenedAt,
					OpeningFloat = sh.OpeningFloat,
					ExpectedCash = await _pos.ExpectedCashAsync(PosCompanyId, sh),
					MovedOut = mv.Where(m => m.Kind == "Out").Sum(m => m.Amount),
					MovedIn = mv.Where(m => m.Kind == "In").Sum(m => m.Amount),
					OrphanMovements = mv.Count(m => m.CounterAccountMissing)
				});
			}
			ViewBag.Drawers = drawers;

			int ccy = await _db.BranchPosSettings.AsNoTracking().Where(x => x.BranchId == bid)
				.Select(x => x.DefaultCurrencyId ?? 0).FirstOrDefaultAsync();
			ViewBag.Dp = await _rounding.DecimalsAsync(PosCompanyId, ccy == 0 ? (int?)null : ccy, bid);

			return View();
		}

		// ==================== HM-10 slice B: HYPER SALES REPORTS (read-only, hyper-activity only) ====================
		// All of these filter to Branch.ActivityPresetCode == "Hyper" (never a restaurant order). Read-only: no write, no GL,
		// no stock — the server computes, the view displays. Revenue is DOCUMENT currency (KWD 3dp, what was rung); COGS and
		// the authoritative margin are FUNCTIONAL (EGP 2dp) per the HM-2 boundary; a SECOND indicative margin column is shown
		// in the document currency converted at TODAY's rate, blank if today's rate is missing/stale (HM-D23 — never a bait rate).
		public class ItemMarginRow
		{
			public int ItemId; public string ItemName = ""; public int? CategoryId; public string CategoryName = "";
			public decimal Qty; public decimal RevenueDoc; public decimal Cogs; public decimal MarginFunc;
			public decimal? MarginPct; public decimal? MarginDocIndicative; public string Status = "";   // Costed | ZeroCost | Unposted
		}
		public class CategoryRow { public string CategoryName = ""; public decimal RevenueDoc; public decimal MarginFunc; public bool HasUnposted; }

		// HM-24 — EVERY hyper-branch query in this controller is company-scoped, not only this helper.
		// Thirteen of them were not. Eight were branch switchers, which would have listed another tenant's
		// hypermarket by name; five were the guards that decide whether a WRITE is allowed, and SaveBranchTax
		// checked only the activity preset — so a second company's hyper branch could have had its tax code
		// rewritten from here. Today there is exactly one hyper branch (id 17, company 1), so none of it was
		// reachable and no screen was ever wrong. It would have become reachable the day a second company got
		// one, which is a deployment decision, not a code change — nobody would have connected the two.
		//
		// The cash path was already safe for a different reason: RecordShiftCashMovementAsync hard-rejects a
		// cross-company terminal in the service. Defence in the service is what made this survivable; it is
		// not a reason to leave the controller open.
		private async Task<List<HyperBranchItem>> HyperBranchListAsync() =>
			await _db.Branches.AsNoTracking().Where(b => b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper")
				.OrderBy(b => b.ID).Select(b => new HyperBranchItem { Id = b.ID, Name = b.Name, NameAr = b.NameAr }).ToListAsync();

		private async Task<List<int>> HyperBranchIdsAsync(int? branchId)
		{
			var all = await _db.Branches.AsNoTracking().Where(b => b.CompanyID == PosCompanyId && b.ActivityPresetCode == "Hyper").Select(b => b.ID).ToListAsync();
			return (branchId != null && all.Contains(branchId.Value)) ? new List<int> { branchId.Value } : all;
		}

		private async Task<int> DocDpAsync(List<int> branchIds)
		{
			int? ccy = branchIds.Count > 0 ? await _db.BranchPosSettings.AsNoTracking().Where(s => s.BranchId == branchIds[0]).Select(s => s.DefaultCurrencyId).FirstOrDefaultAsync() : null;
			return await _rounding.DecimalsAsync(PosCompanyId, (ccy == null || ccy == 0) ? (int?)null : ccy);
		}

		// today's EGP-per-KWD rate for the INDICATIVE column — null (blank column) if the rate is MISSING or stale (HM-D23:
		// never a bait rate). A missing rate is checked EXPLICITLY (ToBaseAsync would otherwise throw); staleness is honored
		// only when a max-age is configured.
		private async Task<decimal?> IndicativeEgpPerKwdAsync()
		{
			int funcId = await _currency.GetFunctionalCurrencyIdAsync(PosCompanyId, null);
			int kwdId = await _db.Currencies.AsNoTracking().Where(c => c.Code == "KWD").Select(c => c.ID).FirstOrDefaultAsync();
			if (kwdId == 0 || kwdId == funcId) return null;
			bool rateExists = await _db.ExchangeRates.AsNoTracking().AnyAsync(r => r.CurrencyId == kwdId && r.RateDate <= DateTime.Today);
			if (!rateExists) return null;   // no rate at all ⇒ blank (never fabricate one)
			var (stale, _a, _m, _b) = await _currency.RateStalenessAsync(PosCompanyId, kwdId, DateTime.Today);
			if (stale) return null;         // configured max-age exceeded ⇒ blank
			var (_amt, rate) = await _currency.ToBaseAsync(1m, kwdId, funcId, DateTime.Today, "Sell");
			return rate > 0m ? rate : (decimal?)null;
		}

		private async Task<List<ItemMarginRow>> ComputeItemRowsAsync(List<int> branchIds, DateTime from, DateTime toExcl, decimal? egpPerKwd, int funcDp, int docDp)
		{
			var invIds = await _db.PosOrders.AsNoTracking()
				.Where(o => o.CompanyId == PosCompanyId && branchIds.Contains(o.BranchId) && o.Status == "Paid" && o.InvoiceId != null && o.ClosedAt >= from && o.ClosedAt < toExcl)
				.Select(o => o.InvoiceId!.Value).ToListAsync();
			if (invIds.Count == 0) return new();
			var invRate = await _db.SalesInvoices.AsNoTracking().Where(i => invIds.Contains(i.ID)).Select(i => new { i.ID, i.ExchangeRate }).ToDictionaryAsync(x => x.ID, x => x.ExchangeRate ?? 1m);
			var lines = await _db.SalesInvoiceLines.AsNoTracking().Where(l => invIds.Contains(l.SalesInvoiceId) && l.ItemId != null)
				.Select(l => new { l.ID, l.SalesInvoiceId, ItemId = l.ItemId!.Value, l.Qty, l.LineTotal }).ToListAsync();
			var lineIds = lines.Select(l => l.ID).ToList();
			// per-line COGS signal, reusing the cogs_impact shape: a movement keyed (SourceType=SalesInvoice, SourceLineId).
			var moves = await _db.StockMovements.AsNoTracking()
				.Where(m => m.SourceType == "SalesInvoice" && m.SourceLineId != null && lineIds.Contains(m.SourceLineId.Value))
				.Select(m => new { LineId = m.SourceLineId!.Value, m.TotalCost }).ToListAsync();
			var costByLine = moves.GroupBy(m => m.LineId).ToDictionary(g => g.Key, g => g.Sum(x => x.TotalCost));
			var lineHasMove = new HashSet<int>(moves.Select(m => m.LineId));
			var itemIds = lines.Select(l => l.ItemId).Distinct().ToList();
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var items = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID)).Select(i => new { i.ID, i.Name, i.NameEn, i.ItemCategoryId }).ToListAsync();
			var catIds = items.Select(i => i.ItemCategoryId).Distinct().ToList();
			var cats = await _db.ItemCategories.AsNoTracking().Where(c => catIds.Contains(c.ID)).ToDictionaryAsync(c => c.ID, c => isAr ? c.Name : (c.NameEn ?? c.Name));
			var info = items.ToDictionary(i => i.ID, i => (name: isAr ? i.Name : (i.NameEn ?? i.Name), catId: i.ItemCategoryId, catName: cats.TryGetValue(i.ItemCategoryId, out var cn) ? cn : ""));

			var agg = new Dictionary<int, (decimal qty, decimal revDoc, decimal revFunc, decimal cogs, bool anyUnposted, bool anyCosted)>();
			foreach (var l in lines)
			{
				var rate = invRate.TryGetValue(l.SalesInvoiceId, out var r) ? r : 1m;
				agg.TryGetValue(l.ItemId, out var a);
				a.qty += l.Qty; a.revDoc += l.LineTotal; a.revFunc += l.LineTotal * rate;
				if (!lineHasMove.Contains(l.ID)) a.anyUnposted = true;                       // no COGS movement at all ⇒ un-posted cost
				else { var tc = costByLine.TryGetValue(l.ID, out var c) ? c : 0m; a.cogs += tc; if (tc > 0m) a.anyCosted = true; }   // movement TotalCost=0 ⇒ zero-cost
				agg[l.ItemId] = a;
			}
			decimal Rf(decimal v) => Math.Round(v, funcDp, MidpointRounding.AwayFromZero);
			decimal Rd(decimal v) => Math.Round(v, docDp, MidpointRounding.AwayFromZero);
			var rows = new List<ItemMarginRow>();
			foreach (var kv in agg)
			{
				var a = kv.Value; var it = info.TryGetValue(kv.Key, out var ii) ? ii : (name: "#" + kv.Key, catId: 0, catName: "");
				string status = a.anyUnposted ? "Unposted" : (a.anyCosted ? "Costed" : "ZeroCost");
				var row = new ItemMarginRow { ItemId = kv.Key, ItemName = it.name, CategoryId = it.catId, CategoryName = it.catName, Qty = a.qty, RevenueDoc = Rd(a.revDoc), Cogs = Rf(a.cogs), Status = status };
				if (status == "Unposted") { row.MarginFunc = 0m; row.MarginPct = null; row.MarginDocIndicative = null; }   // margin UNKNOWN — never 0 and never 100
				else
				{
					decimal marginFunc = a.revFunc - a.cogs;
					row.MarginFunc = Rf(marginFunc);
					row.MarginPct = a.revFunc != 0m ? Math.Round(marginFunc / a.revFunc * 100m, 1) : (decimal?)null;
					row.MarginDocIndicative = (egpPerKwd != null && egpPerKwd.Value > 0m) ? Rd(marginFunc / egpPerKwd.Value) : (decimal?)null;
				}
				rows.Add(row);
			}
			return rows.OrderByDescending(r => r.RevenueDoc).ToList();
		}

		// GET /hyper/reports/item-margin — per-item sales + margin for hyper branches, free date range.
		[HttpGet("reports/item-margin")]
		public async Task<IActionResult> ItemMargin(int? branchId = null, DateTime? from = null, DateTime? to = null)
		{
			ViewBag.SidebarMenu = MainMenu.Hyper();
			var f = (from ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)).Date;
			var toDay = (to ?? DateTime.Today).Date;
			var branchIds = await HyperBranchIdsAsync(branchId);
			int funcDp = await _rounding.DecimalsAsync(PosCompanyId, null);
			int docDp = await DocDpAsync(branchIds);
			var egpPerKwd = await IndicativeEgpPerKwdAsync();
			var rows = branchIds.Count == 0 ? new List<ItemMarginRow>() : await ComputeItemRowsAsync(branchIds, f, toDay.AddDays(1), egpPerKwd, funcDp, docDp);
			ViewBag.Rows = rows; ViewBag.From = f; ViewBag.To = toDay; ViewBag.FuncDp = funcDp; ViewBag.DocDp = docDp;
			ViewBag.HasRate = egpPerKwd != null; ViewBag.BranchId = branchId; ViewBag.HyperBranches = await HyperBranchListAsync();
			return View("~/Views/Hyper/HyperItemMargin.cshtml");
		}

		// GET /hyper/reports/sales — hyper sales overview (total, count, top items, by category), free date range. NO ByType.
		[HttpGet("reports/sales")]
		public async Task<IActionResult> Sales(int? branchId = null, DateTime? from = null, DateTime? to = null)
		{
			ViewBag.SidebarMenu = MainMenu.Hyper();
			var f = (from ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)).Date;
			var toDay = (to ?? DateTime.Today).Date; var toExcl = toDay.AddDays(1);
			var branchIds = await HyperBranchIdsAsync(branchId);
			int funcDp = await _rounding.DecimalsAsync(PosCompanyId, null);
			int docDp = await DocDpAsync(branchIds);
			var egpPerKwd = await IndicativeEgpPerKwdAsync();
			var rows = branchIds.Count == 0 ? new List<ItemMarginRow>() : await ComputeItemRowsAsync(branchIds, f, toExcl, egpPerKwd, funcDp, docDp);
			ViewBag.OrderCount = branchIds.Count == 0 ? 0 : await _db.PosOrders.AsNoTracking().CountAsync(o => o.CompanyId == PosCompanyId && branchIds.Contains(o.BranchId) && o.Status == "Paid" && o.InvoiceId != null && o.ClosedAt >= f && o.ClosedAt < toExcl);
			ViewBag.TotalRevenue = rows.Sum(r => r.RevenueDoc);                                    // document currency (KWD)
			ViewBag.TotalMargin = rows.Where(r => r.Status != "Unposted").Sum(r => r.MarginFunc);   // functional (authoritative)
			ViewBag.AnyUnposted = rows.Any(r => r.Status == "Unposted");
			ViewBag.TopItems = rows.Take(10).ToList();
			ViewBag.Categories = rows.GroupBy(r => r.CategoryName).Select(g => new CategoryRow { CategoryName = g.Key, RevenueDoc = g.Sum(x => x.RevenueDoc), MarginFunc = g.Where(x => x.Status != "Unposted").Sum(x => x.MarginFunc), HasUnposted = g.Any(x => x.Status == "Unposted") }).OrderByDescending(c => c.RevenueDoc).ToList();
			ViewBag.From = f; ViewBag.To = toDay; ViewBag.FuncDp = funcDp; ViewBag.DocDp = docDp; ViewBag.BranchId = branchId; ViewBag.HyperBranches = await HyperBranchListAsync();
			return View("~/Views/Hyper/HyperSales.cshtml");
		}

		public class HyperBranchItem { public int Id { get; set; } public string? Name { get; set; } public string? NameAr { get; set; } }
		public class HyperCapRow { public string Key { get; set; } = ""; public string Ar { get; set; } = ""; public string En { get; set; } = ""; public bool On { get; set; } }
		public class HyperWeighedRow { public int Id { get; set; } public string? Name { get; set; } public string? NameEn { get; set; } public int? ScaleCode { get; set; } public bool IsActive { get; set; } }
		public class HyperCatRow { public int Id { get; set; } public string? Name { get; set; } public string? NameEn { get; set; } public bool Eligible { get; set; } }
		public class HyperDrawerRow
		{
			public int ShiftId { get; set; } public string TerminalCode { get; set; } = "";
			public string ShiftType { get; set; } = ""; public DateTime OpenedAt { get; set; }
			public decimal OpeningFloat { get; set; } public decimal ExpectedCash { get; set; }
			public decimal MovedOut { get; set; } public decimal MovedIn { get; set; }
			public int OrphanMovements { get; set; }
		}
		public class HyperTaxItemRow { public int Id { get; set; } public string? Code { get; set; } public string? Name { get; set; } public string? NameEn { get; set; } public string? TaxName { get; set; } public string? TaxNameEn { get; set; } public decimal Rate { get; set; } }
		public class HyperShiftRow { public int Id { get; set; } public int TerminalId { get; set; } public string ShiftType { get; set; } = ""; public string Status { get; set; } = ""; public DateTime OpenedAt { get; set; } public DateTime? ClosedAt { get; set; } public decimal OpeningFloat { get; set; } }
	}
}
