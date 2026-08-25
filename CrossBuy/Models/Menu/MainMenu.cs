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
					Aliases = new() { "Viewer" } },
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
			}},
			new() { LabelAr = "الحوكمة والإعداد", LabelEn = "Governance & setup", Icon = "ki-outline ki-shield-tick", Items = new()
			{
				new() { LabelAr = "الأرصدة الافتتاحية", LabelEn = "Opening balances", Action = "OpeningBalances", Controller = "Inventory" },
				new() { LabelAr = "تسوية السلامة", LabelEn = "Integrity check", Action = "IntegrityReconciliation", Controller = "Inventory" },
				new() { LabelAr = "الموافقات", LabelEn = "Approvals", Action = "Approvals", Controller = "Inventory", Perm = "manage" },
				new() { LabelAr = "صلاحيات المخزون", LabelEn = "Inventory roles", Action = "InventoryRoles", Controller = "Inventory", Perm = "manage" },
				new() { LabelAr = "إعدادات المخزون", LabelEn = "Inventory settings", Action = "Settings", Controller = "Inventory", Perm = "manage" },
			}},
			new() { LabelAr = "التقارير", LabelEn = "Reports", Icon = "ki-outline ki-chart-simple", Items = new()
			{
				new() { LabelAr = "تقارير المخزون", LabelEn = "Inventory reports", Action = "Reports", Controller = "Inventory", Aliases = new() { "ValuationReport", "StagnantReport", "ReorderReport", "ExpiryAlerts" } },
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

		// ===== Hypermarket (HM-0 scaffold) — its OWN sidebar, separate from Restaurant. =====
		// HM-0 ships only the diagnostic dashboard + a link into the independent lane. Every other item is a
		// PLACEHOLDER for a later HM phase (kept navigable → Dashboard until its screen exists). LabelEn values reuse
		// EXISTING SharedResources keys so the Arabic UI renders correctly (a bare non-key would show English).
		public static List<MenuCategory> Hyper() => new()
		{
			new() { LabelAr = "عام", LabelEn = "General", Icon = "ki-outline ki-element-11", Items = new()
			{
				new() { LabelAr = "لوحة التشخيص", LabelEn = "Dashboard", Action = "Dashboard", Controller = "Hyper" },
			}},
			new() { LabelAr = "التشغيل", LabelEn = "Operations", Icon = "ki-outline ki-handcart", Items = new()
			{
				new() { LabelAr = "شاشة الكاشير", LabelEn = "Cashier terminal", Action = "Login", Controller = "HyperPos" },
			}},
			new() { LabelAr = "التقارير", LabelEn = "Reports", Icon = "ki-outline ki-chart-simple", Items = new()
			{
				new() { LabelAr = "مبيعات الهايبر", LabelEn = "Hyper sales", Action = "Sales", Controller = "Hyper" },
				new() { LabelAr = "هامش الصنف", LabelEn = "Item margin", Action = "ItemMargin", Controller = "Hyper" },
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
				new() { LabelAr = "مرتجعات البيع (إشعار دائن)", LabelEn = "Sales returns", Action = "SalesReturns", Controller = "Accounting" },
				new() { LabelAr = "أعمار ديون العملاء", LabelEn = "AR aging", Action = "ArAging", Controller = "Accounting" },
					new() { LabelAr = "تحليلات العملاء", LabelEn = "Customer analytics", Action = "CustomerAnalytics", Controller = "Accounting" },
				}},
				new() { LabelAr = "إدارة العلاقات (CRM)", LabelEn = "CRM", Icon = "ki-outline ki-people", Items = new()
				{
					new() { LabelAr = "لوحة CRM", LabelEn = "CRM dashboard", Action = "Index", Controller = "Crm" },
					new() { LabelAr = "الحسابات", LabelEn = "Accounts", Action = "Accounts", Controller = "Crm" },
					new() { LabelAr = "العملاء المحتملون", LabelEn = "Leads", Action = "Leads", Controller = "Crm" },
					new() { LabelAr = "الفرص البيعية", LabelEn = "Opportunities", Action = "Opportunities", Controller = "Crm" },
					new() { LabelAr = "خط الأنابيب (Kanban)", LabelEn = "Pipeline (Kanban)", Action = "Pipeline", Controller = "Crm" },
					// Rules-based review list. Placed straight after the pipeline because it answers the
					// question a salesperson asks while looking at one: which of these needs me today?
					new() { LabelAr = "تحليلات الفرص", LabelEn = "Opportunity Insights", Action = "OpportunityInsights", Controller = "Crm" },
					// Account-level companion to the line above: that one asks which DEALS need attention,
					// this one asks which RELATIONSHIPS are weakening. Adjacent because a manager moves
					// between the two questions constantly.
					new() { LabelAr = "تحليلات الحسابات", LabelEn = "Account Insights", Action = "AccountInsights", Controller = "Crm" },
					new() { LabelAr = "إعداد خطوط الأنابيب", LabelEn = "Pipeline setup", Action = "Pipelines", Controller = "Crm", Perm = "crm-manage" },
					new() { LabelAr = "الأنشطة والمهام", LabelEn = "Activities", Action = "Activities", Controller = "Crm" },
					new() { LabelAr = "الحملات التسويقية", LabelEn = "Campaigns", Action = "Campaigns", Controller = "Crm" },
					new() { LabelAr = "القوائم التسويقية", LabelEn = "Marketing lists", Action = "MarketingLists", Controller = "Crm" },
					new() { LabelAr = "التذاكر", LabelEn = "Tickets", Action = "Tickets", Controller = "Crm" },
					new() { LabelAr = "سياسات SLA", LabelEn = "SLA policies", Action = "SlaPolicies", Controller = "Crm", Perm = "crm-manage" },
					new() { LabelAr = "التنبؤ بالمبيعات", LabelEn = "Forecast", Action = "Forecast", Controller = "Crm" },
					new() { LabelAr = "تقارير 360°", LabelEn = "360° reports", Action = "Reports", Controller = "Crm" },
					new() { LabelAr = "تقييم العملاء والأتمتة", LabelEn = "Scoring & automation", Action = "ScoringRules", Controller = "Crm", Perm = "crm-manage" },
					new() { LabelAr = "الحقول المخصّصة", LabelEn = "Custom fields", Action = "CustomFields", Controller = "Crm", Perm = "crm-manage" },
					new() { LabelAr = "قواعد الأتمتة", LabelEn = "Automation rules", Action = "AutomationRules", Controller = "Crm", Perm = "crm-manage" },
					new() { LabelAr = "صلاحيات CRM", LabelEn = "CRM roles", Action = "CrmRoles", Controller = "Crm", Perm = "crm-manage" },
			}},
			new() { LabelAr = "الموردون", LabelEn = "Payables", Icon = "ki-outline ki-purchase", Items = new()
			{
				new() { LabelAr = "الموردون", LabelEn = "Vendors", Action = "Vendors", Controller = "Accounting" },
				new() { LabelAr = "فواتير المشتريات", LabelEn = "Purchase invoices", Action = "PurchaseInvoices", Controller = "Accounting" },
				new() { LabelAr = "سندات الدفع", LabelEn = "Payments", Action = "Payments", Controller = "Accounting" },
				new() { LabelAr = "مرتجعات الشراء (إشعار مدين)", LabelEn = "Purchase returns", Action = "PurchaseReturns", Controller = "Accounting" },
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
				new() { LabelAr = "مهامي", LabelEn = "My tasks", Action = "Index", Controller = "Tasks" },
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

		// ===== Projects & Contracting (ProjectController — its OWN system, not under Accounting) =====
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
		};

		// ===== Administration & HR (Admin + Service controllers — the back-office setup area) =====
		public static List<MenuCategory> Admin() => new()
		{
			new() { LabelAr = "عام", LabelEn = "General", Icon = "ki-outline ki-element-11", Items = new()
			{
				new() { LabelAr = "لوحة الموارد البشرية", LabelEn = "HR Dashboard", Action = "Index", Controller = "Admin" },
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
				new() { LabelAr = "أنواع الإجازات", LabelEn = "Leave types", Action = "LeaveTypesList", Controller = "Admin" },
				new() { LabelAr = "السياسات", LabelEn = "Policies", Action = "Polices", Controller = "Admin" },
				new() { LabelAr = "العطلات الرسمية", LabelEn = "Official holidays", Action = "HolidaysList", Controller = "Admin" },
				new() { LabelAr = "الحضور والانصراف", LabelEn = "Attendance", Action = "Attendance", Controller = "Admin" },
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
