namespace CrossBuy.Models.Menu
{
	public class MenuItem
	{
		public string LabelAr { get; set; } = "";
		public string LabelEn { get; set; } = "";
		public string Action { get; set; } = "";
		public string Controller { get; set; } = "";
		public string? Perm { get; set; }   // optional permission hook (e.g. "manage" → inventory managers only)
		// extra actions (same Controller) that should ALSO highlight this item — e.g. a Reports hub whose
		// drill-down screens (ValuationReport, ReorderReport…) are their own actions but belong to this link.
		public List<string> Aliases { get; set; } = new();
		// Route values for screens that are the SAME action with a different argument — a specific report
		// (Reports/Viewer/Platform.BusinessEventLog) or a filtered view the user thinks of as its own screen
		// (Reports?favorites=true). The URL is built through Url.Action so the value is escaped and the route
		// decides whether it lands in the path or the query — never a string concatenated in Razor, which is
		// how a link survives a routing change on paper and breaks in the browser.
		//
		// An item WITHOUT route values behaves exactly as before: same href, same active-state rule. Only an
		// item that declares them takes the exact-match path (see _MainMenu.cshtml).
		public Dictionary<string, string>? RouteValues { get; set; }
		// A not-yet-built placeholder that navigates to a fallback screen (e.g. the module dashboard) but must NEVER
		// show as the active/selected item — otherwise several placeholders sharing that fallback URL all light up at once.
		public bool Soon { get; set; }
	}

	public class MenuCategory
	{
		public string LabelAr { get; set; } = "";
		public string LabelEn { get; set; } = "";
		public string Icon { get; set; } = "ki-outline ki-element-11";
		public List<MenuItem> Items { get; set; } = new();
	}

	// Per-module navigation. Each module keeps its OWN menu (no cross-module mixing) — every category below
	// contains only links belonging to that module. The shared _MainMenu.cshtml partial renders whichever
	// module's category list a layout passes it. Add a module's links here once.
	public static class MainMenu
	{
		// ===== Platform surfaces (cross-module) =====
		//
		// These are NOT module screens: the Workspace and the Reports Center span every module, and the
		// Business Event Monitor is a platform operations tool. They therefore cannot live inside any one
		// module's menu without either being duplicated across all of them or being unreachable from most.
		//
		// _MainMenu.cshtml prepends this ONE category ahead of whichever module menu a layout passes, so the
		// links exist exactly once in the codebase and appear on every screen that renders the sidebar.
		//
		// Business event monitor was MOVED here from Admin() rather than copied - it was previously reachable
		// only from the backend layout, and leaving it in both places would duplicate the entry.
		public static List<MenuCategory> Platform() => new()
		{
			new() { LabelAr = "المنصّة", LabelEn = "Platform", Icon = "ki-outline ki-abstract-26", Items = new()
			{
				new() { LabelAr = "مساحة العمل", LabelEn = "Workspace", Action = "Index", Controller = "Workspace" },
				// The four Workspace surfaces are now menu entries in their own right rather than aliases of
				// the dashboard. They were DELIBERATELY removed from the Aliases list above: an alias makes the
				// PARENT light up on a drill-down, so leaving them there while also listing them here would
				// highlight two items at once on the same URL.
				//
				// Same model, same builder, same styling as every other item - only Action differs, which is
				// how Inventory lists its own screens.
				new() { LabelAr = "الأجندة", LabelEn = "Agenda", Action = "Agenda", Controller = "Workspace" },
				new() { LabelAr = "الإشعارات", LabelEn = "Notifications", Action = "Notifications", Controller = "Workspace" },
				new() { LabelAr = "الإشارات إليّ", LabelEn = "Mentions", Action = "Mentions", Controller = "Workspace" },
				new() { LabelAr = "تقاريري", LabelEn = "My reports", Action = "Reports", Controller = "Workspace" },
				// Perm "platform-ops" is evaluated in _MainMenu.cshtml with the SAME predicate the
				// [PlatformOps] filter uses, so the link is hidden exactly when the screen would refuse.
				new() { LabelAr = "مراقب أحداث المنصّة", LabelEn = "Business event monitor", Action = "Index",
					Controller = "BusinessEventMonitor", Perm = "platform-ops" },
			}},

			// ===== Reporting =====
			//
			// A SECOND CATEGORY, not a third menu level. The product's sidebar is two levels deep everywhere
			// (category -> items) and Inventory - the visual authority - groups its own screens the same way.
			// Nesting "Reporting" under "Platform" would have introduced a level that exists nowhere else.
			//
			// Only screens that EXIST are listed. Saved Reports and Report History are panels inside the
			// Reports Center, not screens, so they get no entry: a menu row that scrolls a panel into view is
			// a promise the click cannot keep.
			new() { LabelAr = "التقارير", LabelEn = "Reporting", Icon = "ki-outline ki-chart-simple", Items = new()
			{
				// The hub. Its alias covers every OTHER report opened from a card - the drill-down pattern
				// Inventory already uses for its own reports.
				new() { LabelAr = "مركز التقارير", LabelEn = "Reports center", Action = "Index", Controller = "Reports",
                    Aliases = new() { "Viewer", "Datasets" } },
				// A specific report, addressed by its registered code (BusinessEventsDataset.ReportCode). It is
				// the same Viewer action as the alias above - the route VALUE is what makes it a different
				// screen to the user, and what makes only this row light up when it is open.
				// Perm "report" asks the Reporting module whether THIS user can open THIS report, with the
				// same fail-closed rule the Viewer applies. Without it the row would be permanently dead:
				// the report's permission key is unmapped by design until an owner grants it, and the Viewer
				// answers 404 rather than reveal that the report exists.
				new() { LabelAr = "تقرير أحداث المنصّة", LabelEn = "Business event report", Action = "Viewer",
					Controller = "Reports", Perm = "report",
					RouteValues = new() { ["id"] = "Platform.BusinessEventLog" } },
				// Reports/Index already accepts `favorites`. This is that filter as its own entry, because the
				// owner asks for "my favourites" as a destination, not as a checkbox to remember to tick.
				new() { LabelAr = "التقارير المفضّلة", LabelEn = "Favourite reports", Action = "Index",
					Controller = "Reports", RouteValues = new() { ["favorites"] = "true" } },
				// The builder. A REAL screen of its own — not a tab, panel or dialog — so it earns an entry
				// under the same rule the rows above follow. No Perm hook: the Studio itself is open to any
				// signed-in user and renders an explicit "no data sets are available to you" state when their
				// permissions yield nothing, which is more useful than a row that vanishes without saying why.
				new() { LabelAr = "استوديو التقارير", LabelEn = "Report Studio", Action = "Studio",
					Controller = "Reports" },
			}},
		};

		// ===== Inventory & Supply (InventoryController only) =====
		public static List<MenuCategory> Inventory() => new()
		{
			new() { LabelAr = "عام", LabelEn = "General", Icon = "ki-outline ki-element-11", Items = new()
			{
				new() { LabelAr = "لوحة المخزون", LabelEn = "Dashboard", Action = "Index", Controller = "Inventory" },
			}},
			new() { LabelAr = "البيانات الأساسية", LabelEn = "Master data", Icon = "ki-outline ki-abstract-26", Items = new()
			{
				new() { LabelAr = "الأصناف", LabelEn = "Items", Action = "Items", Controller = "Inventory" },
				new() { LabelAr = "التصنيفات", LabelEn = "Categories", Action = "Categories", Controller = "Inventory" },
				new() { LabelAr = "وحدات القياس", LabelEn = "Units", Action = "Units", Controller = "Inventory" },
				new() { LabelAr = "المخازن", LabelEn = "Warehouses", Action = "Warehouses", Controller = "Inventory" },
				new() { LabelAr = "سيكشنات / رفوف المخزن", LabelEn = "Sections / racks", Action = "WarehouseSections", Controller = "Inventory" },
				new() { LabelAr = "مواقع الأصناف", LabelEn = "Item locations", Action = "ItemLocations", Controller = "Inventory" },
			}},
			new() { LabelAr = "الحركات والأرصدة", LabelEn = "Movements & balances", Icon = "ki-outline ki-arrow-two-diagonals", Items = new()
			{
				new() { LabelAr = "حركات المخزون", LabelEn = "Stock movements", Action = "StockMovements", Controller = "Inventory" },
				new() { LabelAr = "أرصدة المخزون", LabelEn = "Stock balances", Action = "StockBalances", Controller = "Inventory" },
				new() { LabelAr = "جرد الرفوف", LabelEn = "Rack stock", Action = "RackBalances", Controller = "Inventory" },
				new() { LabelAr = "الدفعات والصلاحية", LabelEn = "Batches & expiry", Action = "Batches", Controller = "Inventory" },
				new() { LabelAr = "الأرقام التسلسلية", LabelEn = "Serials", Action = "Serials", Controller = "Inventory" },
			}},
			new() { LabelAr = "العمليات", LabelEn = "Operations", Icon = "ki-outline ki-handcart", Items = new()
			{
				new() { LabelAr = "التجميع", LabelEn = "Assembly", Action = "NewAssembly", Controller = "Inventory" },
				new() { LabelAr = "التحويلات بين المخازن", LabelEn = "Transfers", Action = "StockTransfers", Controller = "Inventory" },
				new() { LabelAr = "الجرد والتسويات", LabelEn = "Stock counts", Action = "StockCounts", Controller = "Inventory" },
				new() { LabelAr = "الإعدام والتلف", LabelEn = "Write-offs", Action = "WriteOffs", Controller = "Inventory" },
				new() { LabelAr = "التكاليف الإضافية", LabelEn = "Landed costs", Action = "LandedCosts", Controller = "Inventory" },
				new() { LabelAr = "رسملة أصل من المخزون", LabelEn = "Capitalize asset", Action = "CapitalizeAsset", Controller = "Inventory" },
			}},
			new() { LabelAr = "المشتريات والمبيعات", LabelEn = "Purchasing & sales", Icon = "ki-outline ki-purchase", Items = new()
			{
				new() { LabelAr = "أوامر الشراء", LabelEn = "Purchase orders", Action = "PurchaseOrders", Controller = "Inventory" },
				new() { LabelAr = "أذون الاستلام", LabelEn = "Goods receipts", Action = "GoodsReceipts", Controller = "Inventory" },
				new() { LabelAr = "فواتير المشتريات", LabelEn = "Purchase invoices", Action = "PurchaseInvoices", Controller = "Accounting" },
				new() { LabelAr = "تخطيط النواقص", LabelEn = "Replenishment", Action = "Planning", Controller = "Inventory" },
				new() { LabelAr = "قوائم الأسعار", LabelEn = "Price lists", Action = "PriceLists", Controller = "Inventory" },
				new() { LabelAr = "العروض والخصومات", LabelEn = "Promotions", Action = "Promotions", Controller = "Inventory" },
					new() { LabelAr = "عروض الأسعار", LabelEn = "Quotations", Action = "Quotations", Controller = "Inventory" },
				new() { LabelAr = "أوامر البيع", LabelEn = "Sales orders", Action = "SalesOrders", Controller = "Inventory" },
				new() { LabelAr = "أذون الصرف", LabelEn = "Deliveries", Action = "Deliveries", Controller = "Inventory" },
				new() { LabelAr = "فواتير المبيعات", LabelEn = "Sales invoices", Action = "SalesInvoices", Controller = "Accounting" },
				// field sales (2026-11-18): the reps' routes and the day's visits — the web side of the mobile "today's visits"
				new() { LabelAr = "مسارات المندوبين", LabelEn = "Sales-rep routes", Action = "Routes", Controller = "SalesRep", Perm = "manage", Aliases = new() { "RouteEditor" } },
				new() { LabelAr = "زيارات المندوبين", LabelEn = "Rep visits", Action = "Visits", Controller = "SalesRep", Perm = "manage" },
			}},
			new() { LabelAr = "الحوكمة والإعداد", LabelEn = "Governance & setup", Icon = "ki-outline ki-shield-tick", Items = new()
			{
				new() { LabelAr = "الأرصدة الافتتاحية", LabelEn = "Opening balances", Action = "OpeningBalances", Controller = "Inventory" },
				new() { LabelAr = "تسوية السلامة", LabelEn = "Integrity check", Action = "IntegrityReconciliation", Controller = "Inventory" },
				// A DIAGNOSTIC NOBODY CAN NAVIGATE TO IS THE PROBLEM IT WAS BUILT TO SOLVE. The
				// refused-question screen exists so the product reports its own gaps instead of the
				// owner finding them one screenshot at a time; reachable only by typing a URL, it
				// would have stayed exactly as invisible as the JSON endpoint it replaced.
				// Same "manage" permission the screen itself enforces, beside the other diagnostics.
				new() { LabelAr = "أسئلة بلا إجابة", LabelEn = "Unanswered questions", Action = "AskGaps", Controller = "Inventory", Perm = "manage" },
				new() { LabelAr = "الموافقات", LabelEn = "Approvals", Action = "Approvals", Controller = "Inventory", Perm = "manage" },
				new() { LabelAr = "صلاحيات المخزون", LabelEn = "Inventory roles", Action = "InventoryRoles", Controller = "Inventory", Perm = "manage" },
				new() { LabelAr = "إعدادات المخزون", LabelEn = "Inventory settings", Action = "Settings", Controller = "Inventory", Perm = "manage" },
			}},
			new() { LabelAr = "التقارير", LabelEn = "Reports", Icon = "ki-outline ki-chart-simple", Items = new()
			{
				new() { LabelAr = "تقارير المخزون", LabelEn = "Inventory reports", Action = "Reports", Controller = "Inventory", Aliases = new() { "ValuationReport", "StagnantReport", "ReorderReport", "ExpiryAlerts" } },
			}},
			new() { LabelAr = "مشتريات المشاريع", LabelEn = "Project purchasing", Icon = "ki-outline ki-handcart", Items = new()
			{
				new() { LabelAr = "مسودة شراء مشروع", LabelEn = "New project purchase draft", Action = "Projects", Controller = "Project", Perm = "construction-purchase", RouteValues = ConstructionRoute("purchase") },
				new() { LabelAr = "توزيع تكلفة شراء المشروع", LabelEn = "Project purchase allocation", Action = "PurchaseOrders", Controller = "Inventory", Perm = "construction-purchase", RouteValues = ConstructionRoute("allocation") },
				new() { LabelAr = "اعتماد شراء المشروع", LabelEn = "Project purchase approval", Action = "PurchaseOrders", Controller = "Inventory", Perm = "construction-purchase", RouteValues = ConstructionRoute("approval") },
				new() { LabelAr = "فواتير شراء المشروع الجزئية", LabelEn = "Partial project purchase invoices", Action = "PurchaseOrders", Controller = "Inventory", Perm = "construction-purchase", RouteValues = ConstructionRoute("partial-invoice") },
			}},
		};

		// ===== Manufacturing (Module 4) — standalone system =====
		public static List<MenuCategory> Manufacturing() => new()
		{
			new() { LabelAr = "عام", LabelEn = "General", Icon = "ki-outline ki-element-11", Items = new()
			{
				new() { LabelAr = "لوحة التصنيع", LabelEn = "Manufacturing dashboard", Action = "ManufDashboard", Controller = "Inventory" },
			}},
			new() { LabelAr = "الإنتاج", LabelEn = "Production", Icon = "ki-outline ki-gear", Items = new()
			{
				new() { LabelAr = "أوامر التشغيل", LabelEn = "Work orders", Action = "WorkOrders", Controller = "Inventory" },
				new() { LabelAr = "أمر تشغيل جديد", LabelEn = "New work order", Action = "NewWorkOrder", Controller = "Inventory" },
				new() { LabelAr = "تخطيط الإنتاج (MRP)", LabelEn = "Production planning", Action = "ProductionPlanning", Controller = "Inventory" },
				new() { LabelAr = "التجميع الفوري", LabelEn = "Quick assembly", Action = "NewAssembly", Controller = "Inventory" },
			}},
			new() { LabelAr = "البيانات الأساسية", LabelEn = "Master data", Icon = "ki-outline ki-abstract-26", Items = new()
			{
				new() { LabelAr = "الأصناف وقوائم المواد", LabelEn = "Items & BOMs", Action = "Items", Controller = "Inventory" },
				new() { LabelAr = "مراكز العمل", LabelEn = "Work centers", Action = "WorkCenters", Controller = "Inventory" },
				new() { LabelAr = "وحدات القياس", LabelEn = "Units", Action = "Units", Controller = "Inventory" },
			}},
			new() { LabelAr = "التقارير", LabelEn = "Reports", Icon = "ki-outline ki-chart-simple", Items = new()
			{
				new() { LabelAr = "تقارير التصنيع", LabelEn = "Manufacturing reports", Action = "ManufReports", Controller = "Inventory" },
			}},
		};

		// ===== Restaurant / POS (PosController setup + PosAppController operations) =====
		// The POS environment's own sidebar — cashier/kitchen operations + floor/menu + setup. NO accounting links.
		public static List<MenuCategory> Restaurant() => new()
		{
			new() { LabelAr = "عام", LabelEn = "General", Icon = "ki-outline ki-element-11", Items = new()
			{
				new() { LabelAr = "لوحة المطعم", LabelEn = "Restaurant dashboard", Action = "Dashboard", Controller = "Pos" },
			}},
			new() { LabelAr = "التشغيل", LabelEn = "Operations", Icon = "ki-outline ki-handcart", Items = new()
			{
				new() { LabelAr = "شاشة الكاشير", LabelEn = "Cashier terminal", Action = "Start", Controller = "PosApp" },
				new() { LabelAr = "شاشة المطبخ", LabelEn = "Kitchen display", Action = "Kitchen", Controller = "PosApp" },
				new() { LabelAr = "لوحة التوصيل", LabelEn = "Delivery board", Action = "Delivery", Controller = "PosApp" },
			}},
			new() { LabelAr = "القاعة والقائمة", LabelEn = "Floor & menu", Icon = "ki-outline ki-geolocation", Items = new()
			{
				new() { LabelAr = "تأسيس منصة العمليات", LabelEn = "Operations setup", Action = "Setup", Controller = "Pos" },
				new() { LabelAr = "الصالات والمطابخ", LabelEn = "Areas & stations", Action = "Areas", Controller = "Pos" },
				new() { LabelAr = "مخطط الطاولات", LabelEn = "Floor plan", Action = "FloorPlan", Controller = "Pos" },
				new() { LabelAr = "الحجوزات", LabelEn = "Reservations", Action = "Reservations", Controller = "Pos" },
				new() { LabelAr = "الأصناف السريعة", LabelEn = "Quick items", Action = "QuickMenu", Controller = "Pos" },
				new() { LabelAr = "الإضافات (Modifiers)", LabelEn = "Modifiers", Action = "Modifiers", Controller = "Pos" },
				new() { LabelAr = "معاينة الكاشير", LabelEn = "Cashier preview", Action = "Preview", Controller = "Pos" },
			}},
			new() { LabelAr = "الإعداد", LabelEn = "Setup", Icon = "ki-outline ki-setting-2", Items = new()
			{
				new() { LabelAr = "أجهزة الكاشير", LabelEn = "Cashier terminals", Action = "Terminals", Controller = "Pos" },
				new() { LabelAr = "سائقو التوصيل", LabelEn = "Delivery drivers", Action = "Drivers", Controller = "Pos" },
				new() { LabelAr = "طرق الدفع", LabelEn = "Payment methods", Action = "PaymentMethods", Controller = "Pos" },
				new() { LabelAr = "صلاحيات الكاشير", LabelEn = "Cashier roles", Action = "CashierRoles", Controller = "Pos" },
				new() { LabelAr = "مناطق ورسوم التوصيل", LabelEn = "Delivery zones & fees", Action = "DeliveryZones", Controller = "Pos" },
				new() { LabelAr = "مصادر توفير الأصناف", LabelEn = "Item sourcing", Action = "ItemSourcing", Controller = "Pos" },
				new() { LabelAr = "نظرة عامة: مصادر التوفير", LabelEn = "Sourcing overview", Action = "SourcingOverview", Controller = "Pos" },
				new() { LabelAr = "مراجعة تعارضات المزامنة", LabelEn = "Sync conflict review", Action = "SyncConflicts", Controller = "Pos" },
			}},
		};

		// ===== Accounting & Finance (AccountingController only) =====
		public static List<MenuCategory> Accounting() => new()
		{
			new() { LabelAr = "عام", LabelEn = "General", Icon = "ki-outline ki-bank", Items = new()
			{
				new() { LabelAr = "اللوحة التنفيذية", LabelEn = "Executive dashboard", Action = "Executive", Controller = "Accounting" },
				new() { LabelAr = "لوحة المحاسبة", LabelEn = "Dashboard", Action = "Index", Controller = "Accounting" },
				new() { LabelAr = "شجرة الحسابات", LabelEn = "Chart of accounts", Action = "ChartOfAccounts", Controller = "Accounting" },
				new() { LabelAr = "القيود اليومية", LabelEn = "Journals", Action = "Journals", Controller = "Accounting" },
				new() { LabelAr = "مراكز التكلفة", LabelEn = "Cost centers", Action = "CostCenters", Controller = "Accounting" },
				new() { LabelAr = "الفترات المالية", LabelEn = "Fiscal periods", Action = "Periods", Controller = "Accounting" },
				new() { LabelAr = "رؤى ذكية (AI)", LabelEn = "AI insights", Action = "AiInsights", Controller = "Accounting" },
			}},
			new() { LabelAr = "العملات وأسعار الصرف", LabelEn = "Currencies & FX", Icon = "ki-outline ki-dollar", Items = new()
			{
				new() { LabelAr = "العملات", LabelEn = "Currencies", Action = "Currencies", Controller = "Currency", Perm = "acc-manage" },
				new() { LabelAr = "أسعار الصرف", LabelEn = "Exchange rates", Action = "ExchangeRates", Controller = "Currency" },
				new() { LabelAr = "إعداد العملة الوظيفية", LabelEn = "Functional currency setup", Action = "Setup", Controller = "Currency", Perm = "acc-manage" },
				new() { LabelAr = "إعادة تقييم العملات", LabelEn = "FX revaluation", Action = "Revaluation", Controller = "Currency", Perm = "acc-manage" },
			}},
			new() { LabelAr = "العملاء", LabelEn = "Receivables", Icon = "ki-outline ki-handcart", Items = new()
			{
				new() { LabelAr = "العملاء", LabelEn = "Customers", Action = "Customers", Controller = "Accounting" },
				new() { LabelAr = "فواتير المبيعات", LabelEn = "Sales invoices", Action = "SalesInvoices", Controller = "Accounting" },
				new() { LabelAr = "سندات القبض", LabelEn = "Receipts", Action = "Receipts", Controller = "Accounting" },
				// a customer's cheque is a receipt on 1106 until the bank clears it (2026-11-19)
				new() { LabelAr = "شيكات تحت التحصيل", LabelEn = "Cheques under collection", Action = "Index", Controller = "Cheques" },
				new() { LabelAr = "مرتجعات البيع (إشعار دائن)", LabelEn = "Sales returns", Action = "SalesReturns", Controller = "Accounting" },
				new() { LabelAr = "كشف حساب عميل", LabelEn = "Customer statement", Action = "CustomerStatement", Controller = "Accounting" },
				new() { LabelAr = "أعمار ديون العملاء", LabelEn = "AR aging", Action = "ArAging", Controller = "Accounting" },
					new() { LabelAr = "تحليلات العملاء", LabelEn = "Customer analytics", Action = "CustomerAnalytics", Controller = "Accounting" },
				}},
				new() { LabelAr = "إدارة العلاقات (CRM)", LabelEn = "CRM", Icon = "ki-outline ki-people", Items = new()
				{
					// CRM C9: a rep's work for today in one ordered list, from the assistant's rules.
					new() { LabelAr = "يومي", LabelEn = "My day", Action = "MyDay", Controller = "Crm" },
					new() { LabelAr = "لوحة CRM", LabelEn = "CRM dashboard", Action = "Index", Controller = "Crm" },
					new() { LabelAr = "الحسابات", LabelEn = "Accounts", Action = "Accounts", Controller = "Crm" },
					// CRM C1: the people behind the accounts, each with their communication consent.
					new() { LabelAr = "جهات الاتصال", LabelEn = "Contacts", Action = "Contacts", Controller = "Crm" },
					new() { LabelAr = "العملاء المحتملون", LabelEn = "Leads", Action = "Leads", Controller = "Crm" },
					// CRM C2: leads that arrive by themselves from the company's website.
					new() { LabelAr = "نماذج الموقع", LabelEn = "Website forms", Action = "WebForms", Controller = "Crm" },
					new() { LabelAr = "الفرص البيعية", LabelEn = "Opportunities", Action = "Opportunities", Controller = "Crm" },
					new() { LabelAr = "خط الأنابيب (Kanban)", LabelEn = "Pipeline (Kanban)", Action = "Pipeline", Controller = "Crm" },
					// CRM C3: discounts above someone's limit wait here for a person whose own limit covers them.
					new() { LabelAr = "موافقات الخصم", LabelEn = "Discount approvals", Action = "DiscountApprovals", Controller = "Crm" },
					// Rules-based review list. Placed straight after the pipeline because it answers the
					// question a salesperson asks while looking at one: which of these needs me today?
					new() { LabelAr = "تحليلات الفرص", LabelEn = "Opportunity Insights", Action = "OpportunityInsights", Controller = "Crm" },
					// Account-level companion to the line above: that one asks which DEALS need attention,
					// this one asks which RELATIONSHIPS are weakening. Adjacent because a manager moves
					// between the two questions constantly.
					new() { LabelAr = "تحليلات الحسابات", LabelEn = "Account Insights", Action = "AccountInsights", Controller = "Crm" },
					new() { LabelAr = "إعداد خطوط الأنابيب", LabelEn = "Pipeline setup", Action = "Pipelines", Controller = "Crm", Perm = "crm-manage" },
					// CRM C3: what each stage requires, and the discount each role may give alone.
					new() { LabelAr = "قواعد الصفقات", LabelEn = "Deal rules", Action = "DealRules", Controller = "Crm", Perm = "crm-manage" },
					new() { LabelAr = "الأنشطة والمهام", LabelEn = "Activities", Action = "Activities", Controller = "Crm" },
					// CRM C6: timed follow-up steps that stop when the person answers, and each rep's public booking page.
					new() { LabelAr = "تسلسلات المتابعة", LabelEn = "Follow-up sequences", Action = "Sequences", Controller = "Crm" },
					new() { LabelAr = "رابط حجز مواعيدي", LabelEn = "My booking link", Action = "Booking", Controller = "Crm" },
					new() { LabelAr = "الحملات التسويقية", LabelEn = "Campaigns", Action = "Campaigns", Controller = "Crm" },
					new() { LabelAr = "القوائم التسويقية", LabelEn = "Marketing lists", Action = "MarketingLists", Controller = "Crm" },
					// CRM C7: audiences that keep themselves current, and the drawn paths their new members walk.
					new() { LabelAr = "الشرائح", LabelEn = "Segments", Action = "Segments", Controller = "Crm" },
					new() { LabelAr = "رحلات التسويق", LabelEn = "Marketing journeys", Action = "Journeys", Controller = "Crm" },
					// CRM C4: website chat, WhatsApp and e-mail conversations in one place, each matched to the person it came from.
					new() { LabelAr = "صندوق المحادثات الموحد", LabelEn = "Unified inbox", Action = "Inbox", Controller = "Crm" },
					new() { LabelAr = "القنوات", LabelEn = "Conversation channels", Action = "Channels", Controller = "Crm", Perm = "crm-manage" },
					// CRM C8: the desk at a glance (SLA, first reply, satisfaction) and the answers it shares.
					new() { LabelAr = "مكتب الخدمة", LabelEn = "Service desk", Action = "ServiceDesk", Controller = "Crm" },
					new() { LabelAr = "التذاكر", LabelEn = "Tickets", Action = "Tickets", Controller = "Crm" },
					new() { LabelAr = "قاعدة المعرفة", LabelEn = "Knowledge base", Action = "KnowledgeBase", Controller = "Crm" },
					// The calls themselves. Sits next to Tickets because an agent moves between the two all
					// day: a call becomes a ticket, a ticket prompts a call back.
					new() { LabelAr = "مركز الاتصال", LabelEn = "Call centre", Action = "Calls", Controller = "Crm" },
					new() { LabelAr = "سياسات SLA", LabelEn = "SLA policies", Action = "SlaPolicies", Controller = "Crm", Perm = "crm-manage" },
					new() { LabelAr = "التنبؤ بالمبيعات", LabelEn = "Forecast", Action = "Forecast", Controller = "Crm" },
					// CRM C5: the target each person is measured against, month by month.
					new() { LabelAr = "أهداف المبيعات", LabelEn = "Sales targets", Action = "Quotas", Controller = "Crm" },
					new() { LabelAr = "تقارير 360°", LabelEn = "360° reports", Action = "Reports", Controller = "Crm" },
					new() { LabelAr = "تقييم العملاء والأتمتة", LabelEn = "Scoring & automation", Action = "ScoringRules", Controller = "Crm", Perm = "crm-manage" },
					new() { LabelAr = "الحقول المخصّصة", LabelEn = "Custom fields", Action = "CustomFields", Controller = "Crm", Perm = "crm-manage" },
					// Beside the custom fields deliberately: both are the VOCABULARY a CRM record is described
					// with, both are manage-only, and both are things you set up once and rarely revisit.
					new() { LabelAr = "الوسوم", LabelEn = "Tags", Action = "Tags", Controller = "Crm", Perm = "crm-manage" },
					// CRM C1: one customer record — possible duplicates, merge, and the consent gaps.
					new() { LabelAr = "جودة البيانات والتكرار", LabelEn = "Data quality & duplicates", Action = "Duplicates", Controller = "Crm" },
					// CRM C2: who takes a new lead — territories, their reps, and the order the rules follow.
					new() { LabelAr = "التوزيع والمناطق", LabelEn = "Assignment & territories", Action = "Assignment", Controller = "Crm" },
					new() { LabelAr = "قواعد الأتمتة", LabelEn = "Automation rules", Action = "AutomationRules", Controller = "Crm", Perm = "crm-manage" },
					new() { LabelAr = "صلاحيات CRM", LabelEn = "CRM roles", Action = "CrmRoles", Controller = "Crm", Perm = "crm-manage" },
			}},
			new() { LabelAr = "الموردون", LabelEn = "Payables", Icon = "ki-outline ki-purchase", Items = new()
			{
				new() { LabelAr = "الموردون", LabelEn = "Vendors", Action = "Vendors", Controller = "Accounting" },
				new() { LabelAr = "فواتير المشتريات", LabelEn = "Purchase invoices", Action = "PurchaseInvoices", Controller = "Accounting" },
				new() { LabelAr = "سندات الدفع", LabelEn = "Payments", Action = "Payments", Controller = "Accounting" },
				new() { LabelAr = "مرتجعات الشراء (إشعار مدين)", LabelEn = "Purchase returns", Action = "PurchaseReturns", Controller = "Accounting" },
				new() { LabelAr = "كشف حساب مورد", LabelEn = "Vendor statement", Action = "VendorStatement", Controller = "Accounting" },
				new() { LabelAr = "أعمار ديون الموردين", LabelEn = "AP aging", Action = "ApAging", Controller = "Accounting" },
			}},
			new() { LabelAr = "البنوك والنقدية", LabelEn = "Banks & cash", Icon = "ki-outline ki-dollar", Items = new()
			{
				new() { LabelAr = "البنوك", LabelEn = "Bank accounts", Action = "BankAccounts", Controller = "Accounting" },
				new() { LabelAr = "الخزائن", LabelEn = "Cash boxes", Action = "CashBoxes", Controller = "Accounting" },
				new() { LabelAr = "تحويل نقدي", LabelEn = "Cash transfer", Action = "Transfer", Controller = "Accounting" },
				new() { LabelAr = "تسوية بنكية", LabelEn = "Bank reconcile", Action = "Reconcile", Controller = "Accounting" },
			}},
			new() { LabelAr = "الرواتب", LabelEn = "Payroll", Icon = "ki-outline ki-people", Items = new()
			{
				new() { LabelAr = "قيد الرواتب", LabelEn = "Payroll run", Action = "Payroll", Controller = "Accounting" },
				new() { LabelAr = "قسائم الرواتب", LabelEn = "Payslips", Action = "Payslips", Controller = "Accounting" },
				new() { LabelAr = "صرف الرواتب والتوريد", LabelEn = "Disbursement & remittance", Action = "PayrollDisbursement", Controller = "Accounting" },
				new() { LabelAr = "ضريبة الرواتب والتأمينات", LabelEn = "Payroll tax & insurance", Action = "PayrollTaxSettings", Controller = "Accounting", Perm = "acc-manage" },
			}},
			new() { LabelAr = "الضرائب", LabelEn = "Tax", Icon = "ki-outline ki-percentage", Items = new()
			{
				new() { LabelAr = "أكواد الضريبة", LabelEn = "Tax codes", Action = "TaxCodes", Controller = "Accounting" },
				new() { LabelAr = "إقرارات القيمة المضافة", LabelEn = "VAT returns", Action = "VatReturns", Controller = "Accounting" },
				new() { LabelAr = "حالة الفاتورة الإلكترونية", LabelEn = "ETA status", Action = "EtaStatus", Controller = "Accounting" },
			}},
			new() { LabelAr = "الأصول الثابتة", LabelEn = "Fixed assets", Icon = "ki-outline ki-some-files", Items = new()
			{
				new() { LabelAr = "الأصول الثابتة", LabelEn = "Fixed assets", Action = "FixedAssets", Controller = "Accounting" },
				new() { LabelAr = "فئات الأصول", LabelEn = "Asset categories", Action = "AssetCategories", Controller = "Accounting" },
				new() { LabelAr = "تشغيل الإهلاك", LabelEn = "Depreciation runs", Action = "DepreciationRuns", Controller = "Accounting" },
					new() { LabelAr = "صيانة مستحقّة", LabelEn = "Maintenance due", Action = "MaintenanceDue", Controller = "Accounting" },
			}},
			new() { LabelAr = "التقارير المالية", LabelEn = "Financial reports", Icon = "ki-outline ki-chart-simple", Items = new()
			{
				new() { LabelAr = "ميزان المراجعة", LabelEn = "Trial balance", Action = "TrialBalance", Controller = "Accounting" },
				new() { LabelAr = "الميزانية العمومية", LabelEn = "Balance sheet", Action = "BalanceSheet", Controller = "Accounting" },
				new() { LabelAr = "قائمة الدخل", LabelEn = "Income statement", Action = "IncomeStatement", Controller = "Accounting" },
				new() { LabelAr = "التدفقات النقدية", LabelEn = "Cash flow", Action = "CashFlow", Controller = "Accounting" },
			}},
			new() { LabelAr = "الإقفال", LabelEn = "Closing", Icon = "ki-outline ki-lock-2", Items = new()
			{
				new() { LabelAr = "إقفال السنة المالية", LabelEn = "Year-end close", Action = "YearEndClose", Controller = "Accounting" },
			}},
			new() { LabelAr = "الإعدادات والصلاحيات", LabelEn = "Setup & roles", Icon = "ki-outline ki-shield-tick", Items = new()
			{
				new() { LabelAr = "أدوار المحاسبة", LabelEn = "Accounting roles", Action = "AccountingRoles", Controller = "Accounting", Perm = "acc-manage" },
			}},
		};

		// ===== Tasks & Timesheet (TasksController) — TM-1: tasks only; timesheet/reports arrive in later phases =====
		public static List<MenuCategory> Tasks() => new()
		{
			new() { LabelAr = "المهام", LabelEn = "Tasks", Icon = "ki-outline ki-abstract-26", Items = new()
			{
                new() { LabelAr = "مهامي", LabelEn = "My tasks", Action = "Index", Controller = "Tasks", Aliases = new() { "Summary" } },
				new() { LabelAr = "كل المهام", LabelEn = "All tasks", Action = "All", Controller = "Tasks" },
				new() { LabelAr = "اللوحة", LabelEn = "Board", Action = "Board", Controller = "Tasks", Aliases = new() { "Detail" } },
			}},
			new() { LabelAr = "التقارير", LabelEn = "Reports", Icon = "ki-outline ki-chart-simple", Items = new()
			{
				new() { LabelAr = "لوحة التقارير", LabelEn = "Reports dashboard", Action = "Reports", Controller = "Tasks" },
				new() { LabelAr = "تقرير الساعات", LabelEn = "Hours report", Action = "HoursReport", Controller = "Tasks" },
			}},
			new() { LabelAr = "الإعداد", LabelEn = "Setup", Icon = "ki-outline ki-gear", Items = new()
			{
				new() { LabelAr = "قواعد التوليد التلقائي", LabelEn = "Auto-task rules", Action = "AutoRules", Controller = "Tasks" },
					new() { LabelAr = "مطابقات مقترحة", LabelEn = "Match suggestions", Action = "MatchSuggestions", Controller = "Tasks" },
					new() { LabelAr = "قوالب المهام", LabelEn = "Task templates", Action = "Templates", Controller = "Tasks" },
			}},
		};

		// ===== Calendar =====
		//
		// Calendar previously had NO menu of its own — it was a single link in Admin > General, so the two
		// scheduling screens rendered _LayoutInventory's DEFAULT sidebar (Inventory's own menu). That is the
		// defect this fixes: a Calendar screen showing the Inventory menu is not a navigation preference,
		// it is the wrong menu.
		//
		// Agenda is deliberately ABSENT here: MainMenu.Platform() already lists Workspace > Agenda and is
		// prepended to every sidebar, so adding it would show the same destination twice on this one screen.
		public static List<MenuCategory> Calendar() => new()
		{
			new() { LabelAr = "التقويم", LabelEn = "Calendar", Icon = "ki-outline ki-calendar", Items = new()
			{
				new() { LabelAr = "التقويم", LabelEn = "Calendar", Action = "Index", Controller = "Calendar" },
				new() { LabelAr = "الجدول الزمني", LabelEn = "Timeline", Action = "Timeline", Controller = "Calendar" },
				new() { LabelAr = "عرض الموارد", LabelEn = "Resource view", Action = "ResourceView", Controller = "Calendar" },
			}},
		};

		// ===== Hypermarket / Retail (HyperController + HyperPosController) =====
		//
		// THIS MENU IS THE PLAN. The hypermarket is being built as a prototype FIRST and the prototype is
		// the blueprint, so the navigation shows the WHOLE shape of the module from day one - not only the
		// parts that happen to work today. A reader must be able to see what a hypermarket is going to be
		// without opening a roadmap document.
		//
		// HONESTY IS ENFORCED BY `Soon`, not by omission. A built item links to its real screen. A planned
		// item carries Soon = true and lands on the Plan screen, which states what that screen will do,
		// what it depends on and what is already underneath it. Nothing here is a dead link, and nothing
		// pretends to work. The day a screen ships, its item loses Soon and gains its own Action - one
		// edit, in one place.
		//
		// Same chrome as Hospital deliberately: _LayoutBackend renders this through the shared _MainMenu
		// partial, so the retail pack inherits the back-office shell instead of copying a sixth layout.
		public static List<MenuCategory> Hyper() => new()
		{
			new() { LabelAr = "لوحة الهايبر", LabelEn = "Hypermarket", Icon = "ki-outline ki-element-11", Items = new()
			{
				new() { LabelAr = "لوحة التحكم", LabelEn = "Dashboard", Action = "Dashboard", Controller = "Hyper" },
				new() { LabelAr = "خطة البناء", LabelEn = "Build plan", Action = "Plan", Controller = "Hyper" },
			}},

			new() { LabelAr = "الصندوق", LabelEn = "Checkout", Icon = "ki-outline ki-basket", Items = new()
			{
				new() { LabelAr = "ممرّ الكاشير", LabelEn = "Cashier lane", Action = "Lane", Controller = "HyperPos" },
				new() { LabelAr = "بدء وردية", LabelEn = "Open a shift", Action = "Start", Controller = "HyperPos" },
				new() { LabelAr = "الإيصالات", LabelEn = "Receipts", Action = "Receipts", Controller = "HyperPos" },
				new() { LabelAr = "فحص السعر", LabelEn = "Price check", Action = "PriceCheck", Controller = "HyperPos" },
				// REMOVED: this pointed at HyperPos.Receipts, the same destination as the entry above. Returns are
				// performed FROM the receipts screen (ReverseSale is [HttpPost("receipt/void")], not a page), so a
				// second label for the same page promised a Returns screen that does not exist. Restore this the
				// day HyperPos gains a real returns page - with that page's own action, not this one.
				new() { LabelAr = "درج النقد", LabelEn = "Cash drawer", Action = "CashDrawer", Controller = "Hyper" },
				// REMOVED: pointed at HyperPos.Lane, so it landed on the till lane rather than the page it names.
				// Repointing it at PriceCheck only moved the problem - "Price check" above is already that page,
				// and a PLU lookup IS a price check. One destination, one entry.
			}},

			new() { LabelAr = "العروض والولاء", LabelEn = "Promotions & loyalty", Icon = "ki-outline ki-discount", Items = new()
			{
				new() { LabelAr = "العروض", LabelEn = "Promotions", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "promotions" } },
				new() { LabelAr = "الكوبونات", LabelEn = "Coupons", Action = "Coupons", Controller = "Hyper" },
				new() { LabelAr = "برنامج الولاء", LabelEn = "Loyalty programme", Action = "LoyaltySetup", Controller = "Hyper" },
				// REMOVED: pointed at Hyper.LoyaltySetup, the same page as "Loyalty programme" above it.
				// Redemption is NOT BUILT - it needs schema and, first, a business number nobody has decided:
				// what one point is worth in currency. Restore it when the page exists, with its own action.
			}},

			new() { LabelAr = "الأصناف والتسعير", LabelEn = "Items & pricing", Icon = "ki-outline ki-tag", Items = new()
			{
				new() { LabelAr = "هوامش الأصناف", LabelEn = "Item margins", Action = "ItemMargin", Controller = "Hyper" },
				// REMOVED: pointed at Hyper.ScaleSetup, which "Scale setup" in this same menu already names.
				// The view titles itself "إعداد الموازين" / "Scale setup" - it configures scales, it is not a
				// weighed-items list, so this label promised a screen that does not exist.
				new() { LabelAr = "الباركودات", LabelEn = "Barcodes", Action = "Barcodes", Controller = "Hyper" },
				new() { LabelAr = "قوائم الأسعار", LabelEn = "Price lists", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "pricelists" } },
				new() { LabelAr = "تغيير سعر جماعي", LabelEn = "Bulk price change", Action = "BulkPrice", Controller = "Hyper" },
				new() { LabelAr = "ملصقات الرفوف", LabelEn = "Shelf labels", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "shelflabels" } },
			}},

			new() { LabelAr = "المخزون", LabelEn = "Stock", Icon = "ki-outline ki-package", Items = new()
			{
				new() { LabelAr = "أرصدة الفرع", LabelEn = "Branch stock", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "stock" } },
				new() { LabelAr = "الجرد", LabelEn = "Stock count", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "count" } },
				new() { LabelAr = "التحويلات بين الفروع", LabelEn = "Branch transfers", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "transfers" } },
				new() { LabelAr = "الهالك والتلف", LabelEn = "Waste & write-off", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "waste" } },
				new() { LabelAr = "تتبّع الصلاحية", LabelEn = "Expiry control", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "expiry" } },
				new() { LabelAr = "إعادة الطلب", LabelEn = "Replenishment", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "replenish" } },
			}},

			new() { LabelAr = "المشتريات والموردون", LabelEn = "Purchasing & suppliers", Icon = "ki-outline ki-truck", Items = new()
			{
				new() { LabelAr = "أوامر الشراء", LabelEn = "Purchase orders", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "po" } },
				new() { LabelAr = "استلام البضاعة", LabelEn = "Goods receipt", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "grn" } },
				new() { LabelAr = "الموردون", LabelEn = "Suppliers", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "suppliers" } },
				new() { LabelAr = "مرتجع المورد", LabelEn = "Supplier returns", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "supplierreturn" } },
			}},

			new() { LabelAr = "العملاء", LabelEn = "Customers", Icon = "ki-outline ki-profile-user", Items = new()
			{
				new() { LabelAr = "عميل الصندوق", LabelEn = "Till customer", Action = "Customer", Controller = "HyperPos" },
				new() { LabelAr = "سجل العملاء", LabelEn = "Customer registry", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "customers" } },
			}},

			new() { LabelAr = "التقارير", LabelEn = "Reports", Icon = "ki-outline ki-chart-simple", Items = new()
			{
				new() { LabelAr = "المبيعات", LabelEn = "Sales", Action = "Sales", Controller = "Hyper" },
				new() { LabelAr = "تقرير الوردية Z", LabelEn = "Z report", Action = "ZReport", Controller = "Hyper" },
				new() { LabelAr = "الأكثر مبيعاً", LabelEn = "Top sellers", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "topsellers" } },
				new() { LabelAr = "تحليل السلة", LabelEn = "Basket analysis", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "basket" } },
				new() { LabelAr = "الفاقد والانكماش", LabelEn = "Shrinkage", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "shrinkage" } },
			}},

			new() { LabelAr = "الإعدادات", LabelEn = "Setup", Icon = "ki-outline ki-setting-2", Items = new()
			{
				new() { LabelAr = "الفروع ونقاط البيع", LabelEn = "Branches & terminals", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "terminals" } },
				new() { LabelAr = "قدرات الفرع", LabelEn = "Branch capabilities", Action = "CapabilitySetup", Controller = "Hyper" },
				// HM-PROMO: the engine had no way in until this screen existed.
				new() { LabelAr = "عروض نقاط البيع", LabelEn = "Till promotions", Action = "Promotions", Controller = "Hyper" },
				new() { LabelAr = "طرق الدفع", LabelEn = "Payment methods", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "tenders" } },
				new() { LabelAr = "إعداد الموازين", LabelEn = "Scale setup", Action = "ScaleSetup", Controller = "Hyper" },
				new() { LabelAr = "الضرائب والفوترة", LabelEn = "Tax & invoicing", Action = "TaxSetup", Controller = "Hyper" },
				new() { LabelAr = "صلاحيات الكاشير", LabelEn = "Cashier roles", Action = "Plan", Controller = "Hyper", Soon = true, RouteValues = new() { ["key"] = "roles" } },
			}},
		};

		// ===== Hospital (HospitalController — industry pack H1: the building as the admin draws it) =====
		public static List<MenuCategory> Hospital() => new()
		{
			// The prototype's groups: the front desk first, then inpatient.
			new() { LabelAr = "لوحة المستشفى", LabelEn = "Hospital dashboard", Icon = "ki-outline ki-element-11", Items = new()
			{
				new() { LabelAr = "لوحة التحكم", LabelEn = "Hospital overview", Action = "Dashboard", Controller = "Hospital" },
				new() { LabelAr = "خريطة المستشفى", LabelEn = "Hospital map", Action = "Map", Controller = "Hospital" },
				new() { LabelAr = "تقارير المستشفى", LabelEn = "Hospital reports", Action = "Reports", Controller = "Hospital" },
			}},
			new() { LabelAr = "المرضى والاستقبال", LabelEn = "Patients & front desk", Icon = "ki-outline ki-profile-user", Items = new()
			{
				new() { LabelAr = "مكتب الاستقبال", LabelEn = "Front desk", Action = "FrontDesk", Controller = "Hospital" },   // H139
				new() { LabelAr = "الوارد من الاستقبال", LabelEn = "Incoming from reception", Action = "Handoffs", Controller = "Hospital" },   // H158
				new() { LabelAr = "نواقص التسجيل", LabelEn = "Check-in gaps", Action = "CheckinGaps", Controller = "Hospital" },   // H159
				new() { LabelAr = "تغييرات العيادات الطارئة", LabelEn = "Clinic disruptions", Action = "ClinicDisruptions", Controller = "Hospital" },   // H161
				new() { LabelAr = "سجل المرضى", LabelEn = "Patient registry", Action = "Patients", Controller = "Hospital" },
				new() { LabelAr = "تسجيل مريض", LabelEn = "Register patient", Action = "RegisterPatient", Controller = "Hospital" },
				new() { LabelAr = "التسجيل الذاتي", LabelEn = "Self-intake", Action = "Intake", Controller = "Hospital" },   // H91
				new() { LabelAr = "المرضى المجهولون", LabelEn = "Unidentified patients", Action = "Unidentified", Controller = "Hospital" },   // H92
				new() { LabelAr = "الملفات المكررة", LabelEn = "Duplicate files", Action = "Duplicates", Controller = "Hospital" },
				new() { LabelAr = "المواليد والوفيات", LabelEn = "Births & deaths", Action = "Vital", Controller = "Hospital" },
				new() { LabelAr = "نماذج الموافقات", LabelEn = "Consent forms", Action = "ConsentForms", Controller = "Hospital" },
				new() { LabelAr = "المواعيد", LabelEn = "Appointments", Action = "Appointments", Controller = "Hospital" },
				new() { LabelAr = "طلبات بوابة المريض", LabelEn = "Portal requests", Action = "PortalRequests", Controller = "Hospital" },
				new() { LabelAr = "قائمة الانتظار لموعد أقرب", LabelEn = "Earlier-slot waitlist", Action = "PortalWaitlist", Controller = "Hospital" },
				new() { LabelAr = "رسائل SMS للمرضى", LabelEn = "Patient SMS", Action = "Sms", Controller = "Hospital" },
			}},
			new() { LabelAr = "العيادات", LabelEn = "Clinics", Icon = "ki-outline ki-briefcase", Items = new()
			{
				new() { LabelAr = "لوحة العيادات", LabelEn = "Clinics board", Action = "Clinics", Controller = "Hospital" },
				new() { LabelAr = "شاشة صالة الانتظار", LabelEn = "Waiting-room display", Action = "QueueDisplay", Controller = "Hospital" },   // H83
				new() { LabelAr = "كشك تسجيل الوصول", LabelEn = "Check-in kiosk", Action = "QueueKiosk", Controller = "Hospital" },   // H83
				new() { LabelAr = "التطعيمات", LabelEn = "Immunizations", Action = "Vaccines", Controller = "Hospital" },   // H84
				new() { LabelAr = "متابعة الحمل", LabelEn = "Antenatal care", Action = "Antenatal", Controller = "Hospital" },   // H85
				new() { LabelAr = "غرفة الولادة", LabelEn = "Labour ward", Action = "LabourWard", Controller = "Hospital" },   // H86
				new() { LabelAr = "وحدة الغسيل الكلوي", LabelEn = "Dialysis unit", Action = "Dialysis", Controller = "Hospital" },   // H87
				new() { LabelAr = "الأورام", LabelEn = "Oncology", Action = "Oncology", Controller = "Hospital" },   // H88
				new() { LabelAr = "العلاج الطبيعي", LabelEn = "Physiotherapy", Action = "Rehab", Controller = "Hospital" },   // H89
				new() { LabelAr = "المشرحة", LabelEn = "Mortuary", Action = "Morgue", Controller = "Hospital" },   // H90
				new() { LabelAr = "قائمة الطبيب", LabelEn = "Doctor worklist", Action = "Worklist", Controller = "Hospital" },
				new() { LabelAr = "التحويلات", LabelEn = "Referrals", Action = "Referrals", Controller = "Hospital" },   // H203
				new() { LabelAr = "طلبات تجديد الأدوية", LabelEn = "Refill requests", Action = "PortalRefills", Controller = "Hospital" },
			}},
			// H93 — the specialty clinics, built on one dictionary and clinician-built templates (the approved prototype)
			new() { LabelAr = "العيادات التخصصية", LabelEn = "Specialty clinics", Icon = "ki-outline ki-abstract-26", Items = new()
			{
				new() { LabelAr = "قاموس القياسات", LabelEn = "Concept dictionary", Action = "Concepts", Controller = "Hospital" },   // H93
				new() { LabelAr = "مصمم النماذج", LabelEn = "Template designer", Action = "Templates", Controller = "Hospital" },   // H93
				new() { LabelAr = "الباطنة", LabelEn = "Internal medicine", Action = "SpecialtyClinic", Controller = "Hospital", RouteValues = new() { ["code"] = "IM" } },   // H94
				new() { LabelAr = "الأطفال", LabelEn = "Paediatrics", Action = "SpecialtyClinic", Controller = "Hospital", RouteValues = new() { ["code"] = "PED" } },   // H95
				new() { LabelAr = "العيون", LabelEn = "Ophthalmology", Action = "SpecialtyClinic", Controller = "Hospital", RouteValues = new() { ["code"] = "OPH" } },   // H96
				new() { LabelAr = "الحساسية", LabelEn = "Allergy", Action = "SpecialtyClinic", Controller = "Hospital", RouteValues = new() { ["code"] = "ALG" } },   // H97
				new() { LabelAr = "باقي التخصصات", LabelEn = "Other specialties", Action = "SpecialtyCatalog", Controller = "Hospital" },   // H98
			}},
			// H99 — the clinical canvas (the approved prototype): every drawing a clinic makes on paper, on the visit
			new() { LabelAr = "اللوحة الإكلينيكية", LabelEn = "Clinical canvas", Icon = "ki-outline ki-pencil", Items = new()
			{
				new() { LabelAr = "المحرك والطبقات", LabelEn = "Engine & layers", Action = "CanvasHome", Controller = "Hospital" },   // H99
				new() { LabelAr = "خريطة الجسم + القلم", LabelEn = "Body map + pen", Action = "CanvasHome", Controller = "Hospital", RouteValues = new() { ["kind"] = "Body" } },   // H99
				new() { LabelAr = "قاع العين", LabelEn = "Fundus", Action = "CanvasHome", Controller = "Hospital", RouteValues = new() { ["kind"] = "Fundus" } },   // H99
				new() { LabelAr = "خريطة الحروق", LabelEn = "Burn chart", Action = "CanvasHome", Controller = "Hospital", RouteValues = new() { ["kind"] = "Burn" } },   // H100
				new() { LabelAr = "مخطط السمع", LabelEn = "Audiogram", Action = "CanvasHome", Controller = "Hospital", RouteValues = new() { ["kind"] = "Audio" } },   // H101
				new() { LabelAr = "مفاصل DAS28", LabelEn = "DAS28 joints", Action = "CanvasHome", Controller = "Hospital", RouteValues = new() { ["kind"] = "Joints" } },   // H102
				new() { LabelAr = "شجرة العائلة", LabelEn = "Pedigree", Action = "CanvasHome", Controller = "Hospital", RouteValues = new() { ["kind"] = "Pedigree" } },   // H103
			}},
			new() { LabelAr = "الطوارئ", LabelEn = "Emergency", Icon = "ki-outline ki-pulse", Items = new()
			{
				new() { LabelAr = "لوحة الطوارئ", LabelEn = "Emergency board", Action = "Emergency", Controller = "Hospital" },
			}},
			// H15 — the first specialty pack (there is no ki-tooth in this Keenicons build)
			new() { LabelAr = "الأسنان", LabelEn = "Dentistry", Icon = "ki-outline ki-emoji-happy", Items = new()
			{
				new() { LabelAr = "جدول الكراسي", LabelEn = "Chair schedule", Action = "Dental", Controller = "Hospital" },
				new() { LabelAr = "مخطط الأسنان واللثة", LabelEn = "Dental & perio chart", Action = "DentalChart", Controller = "Hospital" },
				new() { LabelAr = "خطط العلاج", LabelEn = "Treatment plans", Action = "DentalPlans", Controller = "Hospital" },
				new() { LabelAr = "معمل التركيبات", LabelEn = "Dental lab cases", Action = "DentalLab", Controller = "Hospital" },
				new() { LabelAr = "الاستدعاء والتعقيم", LabelEn = "Recall & sterilisation", Action = "DentalRecall", Controller = "Hospital" },
				new() { LabelAr = "تقويم الأسنان", LabelEn = "Orthodontics", Action = "DentalOrtho", Controller = "Hospital" },
				new() { LabelAr = "زراعة الأسنان", LabelEn = "Dental implants", Action = "DentalImplants", Controller = "Hospital" },
			}},
			new() { LabelAr = "التنويم", LabelEn = "Inpatient", Icon = "ki-outline ki-home-3", Items = new()
			{
				new() { LabelAr = "الأسرة والغرف", LabelEn = "Beds & rooms", Action = "Index", Controller = "Hospital" },
				new() { LabelAr = "محطة التمريض", LabelEn = "Nursing station", Action = "Nursing", Controller = "Hospital" },
				new() { LabelAr = "أجهزة المراقبة", LabelEn = "Bedside monitors", Action = "DeviceVitals", Controller = "Hospital" },   // H201
				new() { LabelAr = "المسح عند السرير", LabelEn = "Bedside scanning", Action = "BedsideScans", Controller = "Hospital" },   // H205
				new() { LabelAr = "نسخ وقت التوقف", LabelEn = "Downtime copies", Action = "Downtime", Controller = "Hospital" },   // H211
				new() { LabelAr = "الرصد السريري", LabelEn = "Clinical surveillance", Action = "Surveillance", Controller = "Hospital" },   // H212
				new() { LabelAr = "التقييد والعزل الانفرادي", LabelEn = "Restraint and seclusion", Action = "Restraints", Controller = "Hospital" },   // H213
				new() { LabelAr = "العناية المركزة", LabelEn = "Intensive care", Action = "Icu", Controller = "Hospital" },
				new() { LabelAr = "مكافحة العدوى", LabelEn = "Infection control", Action = "Infection", Controller = "Hospital" },   // H76
				new() { LabelAr = "الأمراض واجبة الإبلاغ", LabelEn = "Notifiable diseases", Action = "Notifiable", Controller = "Hospital" },   // H209
				new() { LabelAr = "الاستشارات", LabelEn = "Consultations", Action = "Consults", Controller = "Hospital" },   // H77
				new() { LabelAr = "السجلات الطبية", LabelEn = "Medical records", Action = "Records", Controller = "Hospital" },   // H80
				new() { LabelAr = "مؤشرات الجودة", LabelEn = "Quality indicators", Action = "Quality", Controller = "Hospital" },   // H81
				new() { LabelAr = "جدول العمليات", LabelEn = "Theatre schedule", Action = "Theatre", Controller = "Hospital" },
				new() { LabelAr = "أنواع الغرف", LabelEn = "Room types", Action = "RoomTypes", Controller = "Hospital" },
				new() { LabelAr = "مصمم الأدوار والغرف", LabelEn = "Floor & room designer", Action = "Designer", Controller = "Hospital" },
			}},
			new() { LabelAr = "التشخيص", LabelEn = "Diagnostics", Icon = "ki-outline ki-flask", Items = new()
			{
				new() { LabelAr = "المختبر", LabelEn = "Laboratory", Action = "Lab", Controller = "Hospital" },
				new() { LabelAr = "التحاليل ومؤشراتها", LabelEn = "Lab tests & analytes", Action = "LabTests", Controller = "Hospital" },
				new() { LabelAr = "نتائج أجهزة المختبر", LabelEn = "Analyser results", Action = "LabDevices", Controller = "Hospital" },   // H198
				new() { LabelAr = "علم الأمراض", LabelEn = "Pathology", Action = "Pathology", Controller = "Hospital" },   // H216
				new() { LabelAr = "المناظير", LabelEn = "Endoscopy", Action = "Endoscopy", Controller = "Hospital" },   // H217
				new() { LabelAr = "تخطيط القلب والصدى", LabelEn = "Cardiology", Action = "Cardiology", Controller = "Hospital" },   // H218
				new() { LabelAr = "واجهات الأجهزة", LabelEn = "Machine interfaces", Action = "Interfaces", Controller = "Hospital" },   // H215
				new() { LabelAr = "الأشعة", LabelEn = "Radiology", Action = "Radiology", Controller = "Hospital" },
				new() { LabelAr = "الفحوصات والأجهزة", LabelEn = "Exams & rooms", Action = "RadiologySetup", Controller = "Hospital" },
				new() { LabelAr = "بنك الدم", LabelEn = "Blood bank", Action = "BloodBank", Controller = "Hospital" },
				new() { LabelAr = "متابعة النتائج", LabelEn = "Result follow-up", Action = "Results", Controller = "Hospital" },   // H178
			}},
			new() { LabelAr = "الصيدلية", LabelEn = "Pharmacy", Icon = "ki-outline ki-capsule", Items = new()
			{
				new() { LabelAr = "قائمة الصرف", LabelEn = "Dispensing queue", Action = "Pharmacy", Controller = "Hospital" },
				new() { LabelAr = "مخزون الصيدلية", LabelEn = "Pharmacy stock", Action = "PharmacyStock", Controller = "Hospital" },   // H132
				new() { LabelAr = "مؤشرات الصيدلية", LabelEn = "Pharmacy indicators", Action = "PharmacyIndicators", Controller = "Hospital" },   // H151
				new() { LabelAr = "دليل الأدوية", LabelEn = "Drug formulary", Action = "Formulary", Controller = "Hospital" },   // H133
				new() { LabelAr = "طلبات دليل الأدوية", LabelEn = "Formulary requests", Action = "FormularyRequests", Controller = "Hospital" },   // H152
				new() { LabelAr = "سحب التشغيلات", LabelEn = "Batch recalls", Action = "Recalls", Controller = "Hospital" },   // H134
				new() { LabelAr = "الصلاحية والإتلاف", LabelEn = "Expiry and disposal", Action = "Disposals", Controller = "Hospital" },   // H135
				new() { LabelAr = "سجل المخدرات", LabelEn = "Controlled-drug register", Action = "ControlledRegister", Controller = "Hospital" },   // H137
				new() { LabelAr = "التحضير المعقَّم", LabelEn = "Compounding", Action = "Compounding", Controller = "Hospital" },   // H153
				new() { LabelAr = "تجهيز عربة الجرعات", LabelEn = "Cart fill", Action = "CartFill", Controller = "Hospital" },   // H138
				new() { LabelAr = "توجيه الأدوية", LabelEn = "Medication routing", Action = "MedRouting", Controller = "Hospital" },
				new() { LabelAr = "تداخلات الأدوية والجرعات", LabelEn = "Drug interactions & doses", Action = "DrugSafety", Controller = "Hospital" },
				new() { LabelAr = "مجموعات الأوامر", LabelEn = "Order sets", Action = "OrderSets", Controller = "Hospital" },
			}},
			new() { LabelAr = "الخدمات المساندة", LabelEn = "Support services", Icon = "ki-outline ki-shop", Items = new()
			{
				new() { LabelAr = "المطبخ والكافيتيريا", LabelEn = "Kitchen & cafeteria", Action = "Kitchen", Controller = "Hospital" },
				new() { LabelAr = "التغذية العلاجية", LabelEn = "Clinical dietary", Action = "Dietary", Controller = "Hospital" },
				new() { LabelAr = "التعقيم المركزي", LabelEn = "Sterile supply", Action = "Cssd", Controller = "Hospital" },
				new() { LabelAr = "أوامر عمل الأجهزة", LabelEn = "Equipment work orders", Action = "WorkOrders", Controller = "Hospital" },
				new() { LabelAr = "النظافة والنقل والمغسلة", LabelEn = "Housekeeping, porters & laundry", Action = "SupportDesk", Controller = "Hospital" },
			}},
			new() { LabelAr = "الإمداد والجودة", LabelEn = "Supply & quality", Icon = "ki-outline ki-shield-search", Items = new()
			{
				new() { LabelAr = "طلبات الأقسام", LabelEn = "Ward requisitions", Action = "Supply", Controller = "Hospital" },
				new() { LabelAr = "الوحدات ومن يطلب", LabelEn = "Units & who asks", Action = "SupplyUnits", Controller = "Hospital" },
				new() { LabelAr = "بلاغات الحوادث", LabelEn = "Incident reports", Action = "Incidents", Controller = "Hospital" },
				new() { LabelAr = "الوصول الطارئ للملفات", LabelEn = "Emergency file access", Action = "BreakGlassLog", Controller = "Hospital" },
				new() { LabelAr = "استبيانات رضا المرضى", LabelEn = "Satisfaction surveys", Action = "Surveys", Controller = "Hospital" },
				new() { LabelAr = "القواعد السريرية المعتمدة", LabelEn = "Clinical rules", Action = "ClinicalRules", Controller = "Hospital" },   // H171
			}},
			new() { LabelAr = "الأطباء والعاملون", LabelEn = "Doctors & staff", Icon = "ki-outline ki-people", Items = new()
			{
				new() { LabelAr = "الممارسون الصحيون", LabelEn = "Practitioners", Action = "Practitioners", Controller = "Hospital" },
				new() { LabelAr = "كشوف الأتعاب", LabelEn = "Fee statements", Action = "Fees", Controller = "Hospital" },
				new() { LabelAr = "المناوبات عند الطلب", LabelEn = "On-call", Action = "OnCall", Controller = "Hospital" },
				new() { LabelAr = "أدوار العاملين", LabelEn = "Staff roles", Action = "StaffRoles", Controller = "Hospital" },   // H194
			}},
			new() { LabelAr = "التواصل", LabelEn = "Communication", Icon = "ki-outline ki-call", Items = new()   // H176
			{
				new() { LabelAr = "الاتصال الداخلي", LabelEn = "Internal calls", Action = "Calls", Controller = "Hospital" },
				new() { LabelAr = "إعدادات الاتصال", LabelEn = "Call settings", Action = "CallSettings", Controller = "Hospital" },
			}},
			new() { LabelAr = "المالية والتأمين", LabelEn = "Finance & insurance", Icon = "ki-outline ki-bill", Items = new()
			{
				new() { LabelAr = "الفوترة والخزينة", LabelEn = "Billing & cashier", Action = "Bills", Controller = "Hospital" },
				new() { LabelAr = "إغلاق الخزينة", LabelEn = "Cashier closing", Action = "Cashier", Controller = "Hospital" },
				new() { LabelAr = "الحالات الخيرية", LabelEn = "Charity cases", Action = "Charity", Controller = "Hospital" },
				new() { LabelAr = "الخدمات والأصناف", LabelEn = "Services & items", Action = "Catalog", Controller = "Hospital" },
				new() { LabelAr = "قوائم الأسعار", LabelEn = "Price lists", Action = "PriceLists", Controller = "Hospital" },
				new() { LabelAr = "التقديرات والتأمين المقدم", LabelEn = "Estimates & deposits", Action = "Estimates", Controller = "Hospital" },
				new() { LabelAr = "الموافقات والمطالبات", LabelEn = "Approvals & claims", Action = "Insurance", Controller = "Hospital" },
				new() { LabelAr = "شركات التأمين والعقود", LabelEn = "Payers & contracts", Action = "Payers", Controller = "Hospital" },
				new() { LabelAr = "إعدادات الفوترة", LabelEn = "Billing settings", Action = "BillingSettings", Controller = "Hospital" },
				new() { LabelAr = "بوابة المريض", LabelEn = "Patient portal", Action = "PortalSettings", Controller = "Hospital" },
				new() { LabelAr = "حسابات بوابة المريض", LabelEn = "Patient portal accounts", Action = "PortalAccounts", Controller = "Hospital" },
			}},
		};

		// ===== Projects & Contracting (ProjectController — its OWN system, not under Accounting) =====
		private static Dictionary<string, string> ConstructionRoute(string destination) => new()
		{
			["construction"] = destination,
			["culture"] = System.Globalization.CultureInfo.CurrentCulture.Name,
			["ui-culture"] = System.Globalization.CultureInfo.CurrentUICulture.Name
		};
		public static List<MenuCategory> Projects() => new()
		{
			new() { LabelAr = "المشاريع والمقاولات", LabelEn = "Projects & Contracting", Icon = "ki-outline ki-briefcase", Items = new()
			{
				new() { LabelAr = "لوحة المشاريع", LabelEn = "Projects dashboard", Action = "Dashboard", Controller = "Project" },
				new() { LabelAr = "المشاريع", LabelEn = "Projects", Action = "Projects", Controller = "Project" },
				new() { LabelAr = "استلام دفعة مقدمة", LabelEn = "Receive advance", Action = "Advance", Controller = "Project" },
				new() { LabelAr = "رد المحتجز", LabelEn = "Release retention", Action = "RetentionRelease", Controller = "Project" },
				new() { LabelAr = "رد محتجز الباطن", LabelEn = "Release subcontractor retention", Action = "SubRetentionRelease", Controller = "Project" },
				new() { LabelAr = "أنواع نشاط المشاريع", LabelEn = "Project activity types", Action = "ActivityTypes", Controller = "Project" },
				new() { LabelAr = "ربحية المشاريع", LabelEn = "Project profitability", Action = "Profitability", Controller = "Project" },
			}},
			new() { LabelAr = "الإنشاءات وميزانيات المشاريع", LabelEn = "Construction & project budgets", Icon = "ki-outline ki-element-11", Items = new()
			{
				new() { LabelAr = "تقارير الموقع اليومية", LabelEn = "Daily site reports", Action = "Projects", Controller = "Project", Perm = "projects-sitereport-read", RouteValues = ConstructionRoute("daily-report") },
				new() { LabelAr = "هيكل المشروع", LabelEn = "Project structure", Action = "Projects", Controller = "Project", Perm = "projects-budget-view", RouteValues = ConstructionRoute("structure") },
				new() { LabelAr = "إصدارات الميزانية", LabelEn = "Budget versions", Action = "Projects", Controller = "Project", Perm = "projects-budget-view", RouteValues = ConstructionRoute("budget") },
				new() { LabelAr = "استثناءات الميزانية", LabelEn = "Budget exceptions", Action = "Projects", Controller = "Project", Perm = "projects-budget-manage", RouteValues = ConstructionRoute("exceptions") },
                new() { LabelAr = "إعادة اعتماد المشتريات", LabelEn = "Purchase budget readmission", Action = "Projects", Controller = "Project", Perm = "projects-budget-manage", RouteValues = ConstructionRoute("rebase") },
                new() { LabelAr = "توزيع التكاليف الفعلية", LabelEn = "Allocate actual costs", Action = "Projects", Controller = "Project", Perm = "projects-budget-manage", RouteValues = ConstructionRoute("actual-cost") },
                new() { LabelAr = "ربط استهلاك المواد", LabelEn = "Material consumption linkage", Action = "Projects", Controller = "Project", Perm = "projects-budget-manage", RouteValues = ConstructionRoute("consumption") },
                new() { LabelAr = "فحص إمكانية نقل الميزانية", LabelEn = "Budget transfer feasibility", Action = "Projects", Controller = "Project", Perm = "projects-budget-manage", RouteValues = ConstructionRoute("transfer-preview") },
                new() { LabelAr = "توقّع تكلفة المشروع", LabelEn = "Project cost forecast", Action = "Projects", Controller = "Project", Perm = "projects-budget-view", RouteValues = ConstructionRoute("cost-forecast") },
                new() { LabelAr = "سياسات الإنشاءات للشركة", LabelEn = "Company construction policies", Action = "Index", Controller = "ConstructionCompanyPolicy", Perm = "projects-manage" },
				new() { LabelAr = "مسودة شراء مشروع", LabelEn = "New project purchase draft", Action = "Projects", Controller = "Project", Perm = "construction-purchase", RouteValues = ConstructionRoute("purchase") },
				new() { LabelAr = "توزيع تكلفة شراء المشروع", LabelEn = "Project purchase allocation", Action = "PurchaseOrders", Controller = "Inventory", Perm = "construction-purchase", RouteValues = ConstructionRoute("allocation") },
				new() { LabelAr = "اعتماد شراء المشروع", LabelEn = "Project purchase approval", Action = "PurchaseOrders", Controller = "Inventory", Perm = "construction-purchase", RouteValues = ConstructionRoute("approval") },
				new() { LabelAr = "فواتير شراء المشروع الجزئية", LabelEn = "Partial project purchase invoices", Action = "PurchaseOrders", Controller = "Inventory", Perm = "construction-purchase", RouteValues = ConstructionRoute("partial-invoice") },
			}},
		};

		// ===== Administration & HR (Admin + Service controllers — the back-office setup area) =====
		public static List<MenuCategory> Admin() => new()
		{
			new() { LabelAr = "عام", LabelEn = "General", Icon = "ki-outline ki-element-11", Items = new()
			{
				new() { LabelAr = "لوحة الموارد البشرية", LabelEn = "HR Dashboard", Action = "Index", Controller = "Admin" },
				// REACHABLE WITHOUT KNOWING IT IS THERE. The results page could only be opened by typing
				// in the header box and pressing Enter, or through the dropdown's "all results" link —
				// so a person who had not already started a search had no way to find it, and nothing
				// in the product said it existed. It sits beside the other cross-cutting tools.
				new() { LabelAr = "البحث الشامل", LabelEn = "Global search", Action = "Results", Controller = "Search" },
				new() { LabelAr = "المحادثات", LabelEn = "Chat", Action = "Index", Controller = "Chat" },
				new() { LabelAr = "الإشعارات", LabelEn = "Notifications", Action = "Index", Controller = "Notifications" },
				new() { LabelAr = "موافقاتي", LabelEn = "My approvals", Action = "Index", Controller = "Approvals" },
					new() { LabelAr = "البريد", LabelEn = "Email", Action = "Index", Controller = "Comm" },
					new() { LabelAr = "التقويم", LabelEn = "Calendar", Action = "Index", Controller = "Calendar" },
					new() { LabelAr = "إدارة الملفات", LabelEn = "File Manager", Action = "Index", Controller = "FileManager" },
					new() { LabelAr = "الإعلانات", LabelEn = "Announcements", Action = "Index", Controller = "Announcements" },
			}},
			new() { LabelAr = "الموظفون والهيكل", LabelEn = "Employees & structure", Icon = "ki-outline ki-people", Items = new()
			{
				new() { LabelAr = "الموظفون", LabelEn = "Employees", Action = "EmployeesList", Controller = "Admin" },
				new() { LabelAr = "الهيكل التنظيمي", LabelEn = "Org structure", Action = "AdministrativeStructure", Controller = "Admin" },
				new() { LabelAr = "المسميات الوظيفية", LabelEn = "Job titles", Action = "JobTitlesList", Controller = "Admin" },
				new() { LabelAr = "الجهات الإدارية", LabelEn = "Administrative bodies", Action = "AdministrativeBodiesCompanyList", Controller = "Admin" },
				new() { LabelAr = "طلبات التوظيف", LabelEn = "Job applications", Action = "Applications", Controller = "Admin" },
				new() { LabelAr = "مستندات وعقود الموظفين", LabelEn = "Contracts & documents", Action = "HrDocuments", Controller = "Admin" },
				new() { LabelAr = "تنبيهات انتهاء المستندات", LabelEn = "Expiry alerts", Action = "DocExpiryAlerts", Controller = "Admin" },
				new() { LabelAr = "أنواع المستندات المطلوبة", LabelEn = "Required documents", Action = "RequiredDocTypesList", Controller = "Admin" },
				new() { LabelAr = "إنهاء الخدمة والتسوية النهائية", LabelEn = "Termination & settlement", Action = "FinalSettlement", Controller = "Admin" },
			}},
			new() { LabelAr = "الإجازات والحضور", LabelEn = "Leave & attendance", Icon = "ki-outline ki-calendar-tick", Items = new()
			{
				new() { LabelAr = "الإجازات", LabelEn = "Leave", Action = "Leave", Controller = "Admin" },   // HR leave overview (prototype design, 2026-11-08)
				new() { LabelAr = "حزمة قواعد الدولة", LabelEn = "Country rule pack", Action = "HrRules", Controller = "Admin" },   // every legal figure as a setting (2026-11-08)
				new() { LabelAr = "مسارات الموافقة", LabelEn = "Approval paths", Action = "ApprovalPaths", Controller = "Admin" },   // approval paths you design, not code (2026-11-09)
				new() { LabelAr = "القروض والسلف", LabelEn = "Loans & advances", Action = "Loans", Controller = "Admin" },   // HR4: loans with the legal deduction cap (2026-11-08)
				new() { LabelAr = "اعتماد العمل الإضافي", LabelEn = "Overtime approval", Action = "Overtime", Controller = "Admin" },   // HR5: overtime approved before pay, within the yearly cap (2026-11-08)
				new() { LabelAr = "جدول ساعات العمل", LabelEn = "Working-hours schedule", Action = "WorkingHours", Controller = "Admin" },   // the Ashal schedule (Kuwait Resolution 15/2025), 2026-11-09
				new() { LabelAr = "مسير الرواتب", LabelEn = "Payroll run", Action = "PayrollRun", Controller = "Admin" },   // HR6: the payroll register in the HR design (2026-11-08)
				new() { LabelAr = "ملف تحويل الرواتب للبنك", LabelEn = "Bank salary file", Action = "BankFile", Controller = "Admin" },   // HR8: wage protection, its format a setting (2026-11-09)
				new() { LabelAr = "بيانات البنك من Excel", LabelEn = "Bank details from Excel", Action = "BankDetails", Controller = "Admin" },   // civil ID / IBAN / bank in bulk (2026-11-09)
				new() { LabelAr = "كشف التأمينات الاجتماعية", LabelEn = "Social insurance statement", Action = "SocialInsurance", Controller = "Admin" },   // HR6: per employee, per rule-pack line (2026-11-08)
				new() { LabelAr = "ملف التأمينات الاجتماعية", LabelEn = "Social insurance file", Action = "InsuranceFile", Controller = "Admin" },   // the monthly contributions file, its shape a setting (2026-11-09)
				new() { LabelAr = "مخصص مكافأة نهاية الخدمة", LabelEn = "Gratuity provision", Action = "GratuityProvision", Controller = "Admin" },   // the end-of-service provision, booked monthly (2026-11-08)
				new() { LabelAr = "أنواع الإجازات", LabelEn = "Leave types", Action = "LeaveTypesList", Controller = "Admin" },
				new() { LabelAr = "السياسات", LabelEn = "Policies", Action = "Polices", Controller = "Admin" },
				new() { LabelAr = "العطلات الرسمية", LabelEn = "Official holidays", Action = "HolidaysList", Controller = "Admin" },
				new() { LabelAr = "الحضور والانصراف", LabelEn = "Attendance", Action = "Attendance", Controller = "Admin" },
				new() { LabelAr = "مواقع البصمة", LabelEn = "Punch locations", Action = "PunchLocations", Controller = "Admin" },   // where a phone punch is accepted (2026-11-19)
				new() { LabelAr = "بدل الإجازات ومخصصها", LabelEn = "Encashment & provision", Action = "LeaveAccrual", Controller = "Admin" },
				new() { LabelAr = "ترحيل رصيد الإجازات", LabelEn = "Leave carry-over", Action = "LeaveCarryOver", Controller = "Admin" },
					new() { LabelAr = "تقييم الأداء", LabelEn = "Performance appraisal", Action = "Appraisals", Controller = "Admin" },
					new() { LabelAr = "التدريب", LabelEn = "Training", Action = "TrainingCourses", Controller = "Admin" },
			}},
			new() { LabelAr = "إعدادات النظام", LabelEn = "System setup", Icon = "ki-outline ki-setting-2", Items = new()
			{
				new() { LabelAr = "الشركات", LabelEn = "Companies", Action = "CompaniesList", Controller = "Service" },
				new() { LabelAr = "الفروع", LabelEn = "Branches", Action = "BranchesList", Controller = "Service" },
				new() { LabelAr = "العلامات التجارية", LabelEn = "Brands", Action = "Brands", Controller = "Brand" },
					// Business event monitor MOVED to MainMenu.Platform() so it is reachable from every layout,
					// not only the backend one, and so it appears exactly once. Do not re-add it here.
				// POS-A2: restaurant setup moved OUT to its own "Restaurant" system (MainMenu.Restaurant()); no longer scattered here.
			}},
		};
	}
}
