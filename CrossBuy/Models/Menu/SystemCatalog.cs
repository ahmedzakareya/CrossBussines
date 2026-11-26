namespace CrossBuy.Models.Menu
{
	/// <summary>
	/// THE list of systems — one source for the sidebar's "Systems" quick switch and the opening screen
	/// (/Portal/Choose). A system added here appears in both; nothing else lists systems.
	///
	/// Key: the stable id (the sidebar's active-path rules and the opening screen's pins use it).
	/// Scope: the IModuleAccessService scope whose "read" decides whether the user sees it (null = everyone signed in).
	/// Color: the calm, flat colour of its icon on the opening screen; Dot: its sidebar dot.
	/// Menu: the MainMenu method whose screens the opening screen's search lists (null = none published).
	/// </summary>
	public sealed record SystemEntry(
		string Key, string Domain, string NameAr, string NameEn, string DescAr, string DescEn,
		string Action, string Controller, string Icon, string Color, string Dot, string? Scope,
		bool InSidebar, string? Menu = null, bool IsNew = false, string? SidebarLabel = null)
	{
		/// The SharedResources key the sidebar translates (its historic label where it differs from NameEn).
		public string SidebarKey => SidebarLabel ?? NameEn;
	}

	public static class SystemCatalog
	{
		public const string Operations = "ops", Finance = "fin", People = "people", Customers = "cust", Work = "work", Packs = "pack";

		/// The domains the opening screen groups systems by, in order.
		public static readonly IReadOnlyList<(string Key, string Ar, string En)> Domains = new[]
		{
			(Operations, "العمليات والمخازن", "Operations & stock"),
			(Finance, "المالية", "Finance"),
			(People, "الموظفون والإدارة", "People & administration"),
			(Customers, "العملاء والتواصل", "Customers & communication"),
			(Work, "العمل والإنتاجية", "Work & productivity"),
			(Packs, "حزم القطاعات", "Industry packs"),
		};

		public static readonly IReadOnlyList<SystemEntry> All = new[]
		{
			new SystemEntry("inv", Operations, "المخزون", "Inventory", "الأصناف والمستودعات وحركة المخزون والمشتريات والمبيعات", "Items, warehouses, stock moves, purchasing and sales",
				"Index", "Inventory", "ki-handcart", "#A9824A", "#FBBF24", "Inventory", true, "Inventory"),
			new SystemEntry("manuf", Operations, "التصنيع", "Manufacturing", "أوامر التشغيل ومتابعة الإنتاج وقوائم المكونات", "Work orders, production and bills of materials",
				"ManufDashboard", "Inventory", "ki-setting-3", "#AD7151", "#FDBA74", "Inventory", true, "Manufacturing"),
			new SystemEntry("acc", Finance, "النظام المحاسبي", "Accounting", "الحسابات والقيود والفواتير والتقارير المالية", "Accounts, journals, invoices and financial reports",
				"Index", "Accounting", "ki-bill", "#467DA6", "#38BDF8", "Accounting", true, "Accounting"),
			new SystemEntry("rep", Finance, "التقارير والتحليلات", "Reports & analytics", "لوحة الإدارة التنفيذية ومصمم التقارير والمؤشرات", "Executive dashboard, Report Studio and KPIs",
				"Executive", "Accounting", "ki-chart-line-up", "#4A5878", "#93C5FD", "Accounting", false),
			new SystemEntry("svc", People, "النظام الإداري", "Back-office", "الشركات والموظفون والصلاحيات وإعدادات النظام", "Companies, employees, permissions and settings",
				"Index", "Admin", "ki-setting-2", "#42888C", "#22D3EE", null, true, "Admin"),
			new SystemEntry("ppl", People, "بوابة الموظفين", "Employee portal", "ملفك وطلباتك وإجازاتك وحضورك", "Your profile, requests, leave and attendance",
				"Dashboard", "People", "ki-profile-user", "#56699B", "#A8C5FF", null, true, SidebarLabel: "People"),
			new SystemEntry("crm", Customers, "إدارة العلاقات", "CRM", "العملاء المحتملون والفرص والحملات", "Leads, opportunities and campaigns",
				"Index", "Crm", "ki-people", "#7A68A2", "#D8B4FE", "Crm", true),
			new SystemEntry("sup", Customers, "الدعم الفني", "Support desk", "تذاكر الدعم وتوجيهها للأقسام", "Support tickets, routed to units",
				"Tickets", "Crm", "ki-message-question", "#A65C6E", "#FDA4AF", "Crm", false),
			new SystemEntry("chat", Customers, "مركز التواصل", "Communication Hub", "المحادثات والقنوات والإشعارات", "Conversations, channels and notifications",
				"Index", "Chat", "ki-messages", "#5C66A3", "#818CF8", null, true),
			new SystemEntry("tasks", Work, "المهام", "Tasks", "المهام والتايم شيت والتكلفة والفوترة", "Tasks, timesheets, cost and billing",
				"Index", "Tasks", "ki-check-square", "#6E61A3", "#C4B5FD", "Tasks", true, "Tasks"),
			new SystemEntry("cal", Work, "التقويم", "Calendar", "الجدول الزمني وعرض الموارد", "Timeline and resource view",
				"Index", "Calendar", "ki-calendar", "#91608F", "#F0ABFC", "Calendar", true, "Calendar"),
			new SystemEntry("proj", Work, "المشاريع والمقاولات", "Projects & contracting", "المشاريع والمقايسات والمستخلصات", "Projects, BOQ and progress billing",
				"Dashboard", "Project", "ki-abstract-26", "#438676", "#2DD4BF", "Projects", true, "Projects", SidebarLabel: "Projects & Contracting"),
			new SystemEntry("hosp", Packs, "المستشفى", "Hospital", "المرضى والعيادات والتنويم والفوترة", "Patients, clinics, inpatient and billing",
				"Index", "Hospital", "ki-pulse", "#3A5795", "#7DD3FC", "Hospital", true, "Hospital", IsNew: true),
			new SystemEntry("rest", Packs, "المطعم", "Restaurant", "الطلبات والمطبخ والكاشير", "Orders, kitchen and cashier",
				"Dashboard", "Pos", "ki-coffee", "#A2617B", "#FDA4AF", "Pos", true, "Restaurant"),
			new SystemEntry("hyper", Packs, "هايبر ماركت", "Hypermarket", "ممرات الكاشير والقدرات والورديات", "Checkout lanes, capacity and shifts",
				"Dashboard", "Hyper", "ki-shop", "#8D6079", "#F9A8D4", "Pos", true, "Hyper"),
		};

		/// The sidebar's order, unchanged: the systems it listed before the catalogue existed.
		public static readonly IReadOnlyList<string> SidebarOrder = new[] { "inv", "manuf", "acc", "svc", "ppl", "crm", "rest", "hyper", "tasks", "cal", "proj", "hosp", "chat" };

		public static IEnumerable<SystemEntry> Sidebar => SidebarOrder.Select(k => All.First(s => s.Key == k));

		/// The screens a system publishes, from its MainMenu method (empty when it publishes none).
		public static IEnumerable<MenuItem> ScreensOf(SystemEntry s) => s.Menu switch
		{
			"Inventory" => Flatten(MainMenu.Inventory()),
			"Manufacturing" => Flatten(MainMenu.Manufacturing()),
			"Accounting" => Flatten(MainMenu.Accounting()),
			"Admin" => Flatten(MainMenu.Admin()),
			"Tasks" => Flatten(MainMenu.Tasks()),
			"Calendar" => Flatten(MainMenu.Calendar()),
			"Projects" => Flatten(MainMenu.Projects()),
			"Hospital" => Flatten(MainMenu.Hospital()),
			"Restaurant" => Flatten(MainMenu.Restaurant()),
			"Hyper" => Flatten(MainMenu.Hyper()),
			_ => Array.Empty<MenuItem>(),
		};

		// A placeholder ("soon") is not a screen, and a permission-hooked row is left out rather than shown to
		// someone who would be refused — the search lists only what opens.
		private static IEnumerable<MenuItem> Flatten(List<MenuCategory> cats)
			=> cats.SelectMany(c => c.Items).Where(i => !string.IsNullOrEmpty(i.Action) && !i.Soon && i.Perm == null)
				.GroupBy(i => i.Controller + "/" + i.Action + "?" + string.Join("&", (i.RouteValues ?? new()).Select(kv => kv.Key + "=" + kv.Value))).Select(g => g.First());
	}
}
