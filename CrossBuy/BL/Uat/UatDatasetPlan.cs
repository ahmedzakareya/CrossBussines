namespace CrossBuy.BL.Uat
{
	// ==========================================================================================
	// UAT DATASET — THE CONTENT
	//
	// Separated from the seeder so the seeder reads as MECHANISM and this file reads as MEANING. The
	// brief's rule is "no lorem ipsum and no random rows to inflate a count", which is a rule about this
	// file: every title here is a thing somebody in a hypermarket / accounting business actually does, and
	// the Arabic is real Arabic rather than transliteration.
	//
	// BILINGUAL BY CONSTRUCTION. Rows come in three deliberate shapes, because all three exist in the
	// product and each breaks a different piece of layout:
	//   * Arabic title + English twin      — the normal case, and the only one that proves the twin is used
	//   * Arabic title, NO English twin    — the fallback path (the UI must show the Arabic)
	//   * English-only title               — an LTR string inside an RTL grid
	//
	// THE LONG ROWS ARE NOT PADDING. One deliberately long title, description, checklist line, calendar
	// subject and layout name each exist so wrapping, truncation and overflow can be inspected on a real
	// screen. They are long but VALID — no absurd unbounded value, because a 10,000-character title tests
	// the database, not the layout.
	// ==========================================================================================
	internal static class UatContent
	{
		/// (Arabic title, English twin or null, business area, description or null).
		/// The area becomes the TaskItem.Category chip suffix, so the board shows a meaningful label AND
		/// the row stays traceable to this run.
		internal static readonly (string Ar, string? En, string Area, string? Desc)[] TaskScenarios =
		{
			("مراجعة فاتورة مشتريات وربطها بأمر الشراء", "Purchase invoice review and PO match", "المشتريات",
				"مطابقة الكميات والأسعار بين الفاتورة وأمر الشراء وإشعار الاستلام قبل الاعتماد."),
			("جولة تفتيش على فرع الرياض", "Site inspection — Riyadh branch", "التشغيل",
				"فحص التبريد والنظافة وصلاحية الأصناف في الأرفف الأمامية."),
			("متابعة عميل متأخر في السداد", "Follow up an overdue customer", "المبيعات",
				"الاتصال بالعميل والاتفاق على جدول سداد وتوثيقه في ملف العميل."),
			("جرد ومطابقة مخزون الثلاجات", "Stock reconciliation — chillers", "المخزون",
				"جرد فعلي ومقارنته برصيد النظام مع تفسير أي فرق."),
			("اعتماد طلب شراء مواد تغليف", "Approve packaging purchase requisition", "المشتريات", null),
			("الإقفال الشهري للحسابات", "Monthly accounts closing", "المالية",
				"مراجعة القيود المعلّقة وتسوية الحسابات الوسيطة قبل إقفال الفترة."),
			("مراجعة عقد مورّد قبل التجديد", "Contract review before renewal", "المالية",
				"مراجعة الشروط الجزائية ومدة السداد ونِسَب الخصم."),
			("تنسيق تسليم طلبات الجملة", "Wholesale delivery coordination", "التشغيل", null),
			("تجهيز أوراق موظف جديد", "New employee onboarding paperwork", "الموارد البشرية",
				"العقد، التأمين، بطاقة الدخول، وحساب النظام."),
			("صيانة دورية لمكيّفات المستودع", "Preventive maintenance — warehouse AC", "الصيانة", null),
			("إعداد تقرير المبيعات الأسبوعي", "Weekly sales report preparation", "المبيعات", null),
			("متابعة إدارية لخطة الربع", "Management follow-up — quarterly plan", "الإدارة", null),
			("تسوية حساب مورّد الطاقة", "Settle the utilities supplier account", "المالية", null),
			("مراجعة أسعار البيع للأصناف الموسمية", "Selling price review — seasonal items", "المبيعات",
				"مراجعة هامش الربح بعد تغيّر سعر التوريد."),
			("تدقيق قيود اليومية للأسبوع الماضي", "Journal entry audit — last week", "المالية", null),
			("تجهيز طلب فرع جدة", "Fulfil the Jeddah branch order", "المخزون", null),
			("فحص جودة دفعة إنتاج", "Production batch quality check", "الجودة", null),
			("تحديث بيانات العملاء الناقصة", "Complete missing customer data", "المبيعات", null),
			("مطابقة الصندوق مع نظام الكاشير", "Reconcile the till against POS", "التشغيل", null),
			("مراجعة مرتجعات المبيعات", "Sales returns review", "المبيعات", null),
			("إعداد كشف رواتب الشهر", "Prepare the monthly payroll run", "الموارد البشرية", null),
			("تدريب الكاشير على الأصناف الجديدة", "Train cashiers on the new items", "الموارد البشرية", null),
			("متابعة طلب صيانة عاجل", "Chase an urgent maintenance request", "الصيانة", null),
			("مراجعة حد الائتمان لعميل الجملة", "Review the wholesale credit limit", "المالية", null),

			// English-only rows: an LTR title inside an RTL grid.
			("Reconcile supplier statement for July", null, "المالية", null),
			("Prepare VAT return working papers", null, "المالية",
				"Collect the input/output tax summary and reconcile it to the general ledger before filing."),
			("Cycle count — dry goods aisle", null, "المخزون", null),
			("Chase pending goods receipt notes", null, "المشتريات", null),

			// Arabic with NO English twin: the twin-fallback path.
			("متابعة شكوى عميل على جودة صنف", null, "الجودة", null),
			("ترتيب أرشيف الفواتير الورقية", null, "Administration", null),
			("مراجعة استهلاك الأصول الثابتة", null, "المالية", null),
			("تحديث قائمة الأسعار للفرع الجديد", null, "المبيعات", null),
		};

		/// One deliberately long title. Valid, not absurd — long enough to wrap on a board card and to be
		/// truncated in a table cell, which is exactly what needs inspecting.
		internal const string LongTaskTitle =
			"مراجعة شاملة لمطابقة فواتير المشتريات مع أوامر الشراء وإشعارات الاستلام لجميع الفروع خلال الربع " +
			"الحالي وإعداد مذكّرة بالفروقات المتكرّرة ومقترح لتعديل إجراءات الاستلام";

		internal const string LongTaskTitleEn =
			"End-to-end three-way match review across every branch for the current quarter, including a memo on " +
			"the recurring variances and a proposal for changing the goods-receipt procedure";

		internal const string LongDescription =
			"الغرض من هذه المهمة هو الوصول إلى سبب الفروقات المتكرّرة بين الكميات المستلمة والكميات المفوترة. " +
			"تشمل الخطوات: سحب تقرير المطابقة الثلاثية لكل فرع، فصل الفروقات الناتجة عن الوحدات عن الفروقات " +
			"الناتجة عن التسعير، مراجعة عيّنة من إشعارات الاستلام الورقية مقابل النظام، ثم صياغة مذكّرة تتضمّن " +
			"التوصيات وتقدير الأثر المالي. النتيجة المتوقّعة: قائمة أسباب مرتّبة بالأثر، ومقترح إجرائي واحد قابل للتنفيذ.";

		/// Checklist lines. A mix of Arabic, English and one long line.
		internal static readonly string[] ChecklistPool =
		{
			"سحب تقرير المطابقة من النظام",
			"مقارنة الكميات مع إشعار الاستلام",
			"التحقّق من سعر الوحدة مقابل أمر الشراء",
			"توثيق الفروقات في ورقة العمل",
			"إشعار المورّد بالفروقات",
			"الحصول على اعتماد المدير المالي",
			"Pull the supporting documents",
			"Cross-check the tax treatment",
			"Update the tracking sheet",
			"Send the summary to the requester",
			"إغلاق البند وتحديث نسبة الإنجاز",
			"مراجعة تاريخ الصلاحية للأصناف المستلمة",
		};

		internal const string LongChecklistLine =
			"مراجعة كل سطر من سطور الفاتورة مقابل أمر الشراء وإشعار الاستلام، وتسجيل أي فرق في الكمية أو السعر " +
			"في ورقة العمل مع بيان السبب والمستند المرجعي، ثم اعتماد السطر أو تحويله إلى بند مفتوح للمتابعة";

		/// (Arabic name, English name, Arabic description, English description). Each template's items are
		/// generated from ItemsFor below.
		///
		/// DescEn was added because the screen showed an English name with an Arabic description under it:
		/// the tuple carried one description and the entity had nowhere to put a second one.
		internal static readonly (string Ar, string En, string Desc, string DescEn)[] Templates =
		{
			("إقفال شهري", "Monthly closing", "الخطوات القياسية لإقفال الفترة المحاسبية.", "The standard steps for closing an accounting period."),
			("تعيين موظف جديد", "New employee onboarding", "من قبول العرض حتى أول يوم عمل.", "From accepting the offer to the first day at work."),
			("افتتاح فرع جديد", "New branch opening", "التجهيزات والتراخيص وربط النظام.", "Fit-out, licences and connecting the system."),
			("جرد نصف سنوي", "Half-year stocktake", "التحضير، الجرد، المطابقة، الاعتماد.", "Preparation, counting, reconciliation, approval."),
			("مراجعة عقد مورّد", "Supplier contract review", "المراجعة القانونية والتجارية قبل التوقيع.", "Legal and commercial review before signing."),
			("إطلاق صنف جديد", "New item launch", "التسعير، الباركود، الرفوف، الحملة.", "Pricing, barcode, shelving, campaign."),
			("تدقيق داخلي ربع سنوي", "Quarterly internal audit", "نطاق التدقيق والاختبارات والتقرير.", "Audit scope, testing and the report."),
			("صيانة وقائية للمعدات", "Equipment preventive maintenance", "الجدول والفحوصات وقطع الغيار.", "Schedule, inspections and spare parts."),
			("إعداد كشف الرواتب", "Payroll preparation", "الحضور، الإضافي، الخصومات، الاعتماد.", "Attendance, overtime, deductions, approval."),
			("معالجة مرتجع مبيعات", "Sales return handling", "الاستلام، الفحص، الإشعار الدائن.", "Receipt, inspection, credit note."),
		};

		/// Template line generator: (title, titleEn, dueOffsetDays, priority, checklist lines).
		internal static (string Ar, string En, int Offset, string Priority, string? Checklist)[] ItemsFor(int templateIndex)
			=> templateIndex switch
			{
				0 => new[]
				{
					("تجميد إدخال القيود", "Freeze journal entry", 0, "High", "إبلاغ الفروع\nإغلاق صلاحية الإدخال"),
					("مراجعة الحسابات الوسيطة", "Review clearing accounts", 2, "High", "الصندوق\nالبنك\nالسلف"),
					("تسوية المخزون", "Inventory adjustment", 3, "Normal", null),
					("اعتماد الإقفال", "Approve the close", 5, "Urgent", "مراجعة ميزان المراجعة\nتوقيع المدير المالي"),
				},
				1 => new[]
				{
					("تجهيز العقد", "Prepare the contract", 0, "High", "المسمّى الوظيفي\nالراتب\nمدة التجربة"),
					("فتح ملف التأمين", "Open the insurance file", 1, "Normal", null),
					("إصدار بطاقة الدخول", "Issue the access badge", 2, "Normal", null),
					("إنشاء حساب النظام", "Create the system account", 2, "High", "الصلاحيات\nالفرع\nالقسم"),
					("جلسة تعريفية", "Induction session", 3, "Low", null),
				},
				2 => new[]
				{
					("استخراج الترخيص البلدي", "Municipal licence", 0, "Urgent", null),
					("تجهيز الرفوف والتبريد", "Fit shelving and chillers", 7, "High", "المخطط\nالتركيب\nالفحص"),
					("ربط الكاشير بالنظام", "Connect the POS", 12, "High", null),
					("توظيف الفريق", "Hire the team", 14, "Normal", null),
					("جرد افتتاحي", "Opening stock count", 20, "Normal", null),
				},
				3 => new[]
				{
					("تجميد الحركة", "Freeze movement", 0, "High", null),
					("طباعة أوراق الجرد", "Print count sheets", 0, "Normal", null),
					("الجرد الفعلي", "Physical count", 1, "Urgent", "الممر أ\nالممر ب\nالمستودع"),
					("مطابقة الفروقات", "Reconcile variances", 2, "High", null),
					("اعتماد التسويات", "Approve adjustments", 3, "High", null),
				},
				4 => new[]
				{
					("المراجعة التجارية", "Commercial review", 0, "Normal", "الأسعار\nالخصومات\nمدة السداد"),
					("المراجعة القانونية", "Legal review", 3, "High", null),
					("التوقيع والأرشفة", "Sign and file", 6, "Normal", null),
				},
				5 => new[]
				{
					("تحديد السعر وهامش الربح", "Set price and margin", 0, "High", null),
					("إصدار الباركود", "Generate the barcode", 1, "Normal", null),
					("تخصيص مساحة الرف", "Allocate shelf space", 2, "Normal", null),
					("حملة إطلاق", "Launch campaign", 4, "Low", "التصميم\nالطباعة\nالتوزيع"),
				},
				6 => new[]
				{
					("تحديد نطاق التدقيق", "Define the audit scope", 0, "Normal", null),
					("تنفيذ الاختبارات", "Perform the tests", 5, "High", "العيّنة\nالأدلة\nالملاحظات"),
					("مناقشة الملاحظات", "Discuss the findings", 10, "Normal", null),
					("إصدار التقرير", "Issue the report", 12, "High", null),
				},
				7 => new[]
				{
					("مراجعة جدول الصيانة", "Review the maintenance schedule", 0, "Normal", null),
					("فحص المعدات", "Inspect the equipment", 2, "High", "التبريد\nالمولّد\nالرافعة"),
					("طلب قطع الغيار", "Order spare parts", 4, "Normal", null),
				},
				8 => new[]
				{
					("تجميع الحضور", "Collect attendance", 0, "High", null),
					("حساب الإضافي والخصومات", "Calculate overtime and deductions", 2, "High", null),
					("مراجعة الكشف", "Review the register", 3, "Urgent", "المقارنة بالشهر السابق\nالتحقّق من الجديد"),
					("اعتماد الصرف", "Approve payment", 4, "Urgent", null),
				},
				_ => new[]
				{
					("استلام المرتجع وفحصه", "Receive and inspect the return", 0, "High", "الحالة\nالكمية\nالسبب"),
					("إصدار إشعار دائن", "Issue the credit note", 1, "High", null),
					("إعادة الصنف للمخزون أو الإتلاف", "Restock or write off", 2, "Normal", null),
				},
			};

		/// (Arabic subject, English subject, location, scope, minutes). Times are assigned by the seeder.
		internal static readonly (string Ar, string? En, string? Location, string Scope, int Minutes)[] EventScenarios =
		{
			("اجتماع مراجعة المبيعات الأسبوعي", "Weekly sales review", "قاعة الاجتماعات أ", "Company", 60),
			("لقاء الفريق الصباحي", "Daily stand-up", "قاعة التدريب", "Company", 15),
			("مقابلة مرشّح لوظيفة كاشير", "Cashier candidate interview", "غرفة المقابلات", "Personal", 45),
			("زيارة مورّد الألبان", "Dairy supplier visit", "المستودع الرئيسي", "Personal", 90),
			("اجتماع اعتماد الميزانية", "Budget approval meeting", "قاعة المجلس", "Company", 120),
			("تدريب على نظام الكاشير الجديد", "New POS training", "قاعة التدريب", "Company", 180),
			("جلسة مطابقة حسابات المورّدين", "Supplier reconciliation session", "قاعة الاجتماعات ب", "Personal", 60),
			("متابعة خطة الصيانة", "Maintenance plan follow-up", "غرفة الموقع", "Personal", 30),
			("Quarterly stock review", null, "Board Room", "Company", 120),
			("Credit committee", null, "Board Room", "Company", 60),
			("زيارة فرع جدة", "Jeddah branch visit", null, "Personal", 240),
			("اجتماع لجنة الجودة", "Quality committee", "قاعة الاجتماعات أ", "Company", 60),
			("مراجعة عقد الإيجار", "Lease contract review", "غرفة المقابلات", "Personal", 45),
			("عرض تقديمي للإدارة", "Management presentation", "قاعة المجلس", "Company", 75),
			("تسليم دفعة جملة", "Wholesale batch handover", "المستودع الرئيسي", "Personal", 120),
			("اجتماع تخطيط الربع القادم", "Next-quarter planning", "قاعة الاجتماعات ب", "Company", 150),
		};

		internal const string LongEventSubject =
			"اجتماع موسّع لمراجعة نتائج الربع الحالي ومناقشة خطة التوسّع في الفروع الجديدة وأثرها على مستويات " +
			"المخزون والتدفّق النقدي مع رؤساء الأقسام";

		/// (Arabic name, English name, kind, capacity). The kinds come from CalendarResource.Kind's own
		/// vocabulary (Room | Vehicle | Equipment | Other) — nothing invented.
		internal static readonly (string Ar, string En, string Kind, int? Capacity)[] Resources =
		{
			("قاعة الاجتماعات أ", "Meeting Room A", "Room", 8),
			("قاعة الاجتماعات ب", "Meeting Room B", "Room", 6),
			("قاعة المجلس", "Board Room", "Room", 16),
			("قاعة التدريب", "Training Room", "Room", 24),
			("غرفة المقابلات", "Interview Room", "Room", 4),
			("غرفة الموقع", "Site Room", "Room", 5),
			("قاعة المؤتمرات", "Conference Room", "Room", 40),
			("سيارة الشركة المشتركة", "Shared Vehicle", "Vehicle", 4),
			("شاحنة التوصيل", "Delivery Van", "Vehicle", 2),
			("جهاز العرض المحمول", "Portable Projector", "Equipment", null),
		};

		/// (Arabic title, English title, Arabic body, English body, type, category, priority).
		/// `type` values are taken from the notification types ALREADY in use in this database — no new
		/// catalogue key is invented, because an unknown key has no icon and no click-through.
		internal static readonly (string TAr, string TEn, string BAr, string BEn, string Type, string Category, string Priority)[]
			Notifications =
		{
			("فاتورة مبيعات جديدة", "New sales invoice", "تم إصدار فاتورة مبيعات بحاجة إلى مراجعتك.",
				"A sales invoice was issued and needs your review.", "sales_invoice", "Sales", "Normal"),
			("فاتورة مشتريات بانتظار الاعتماد", "Purchase invoice awaiting approval",
				"فاتورة مشتريات بقيمة تتجاوز حدّ الاعتماد الخاص بك.",
				"A purchase invoice exceeds your approval limit.", "purchase_invoice", "Accounting", "High"),
			("إشعار استلام بضاعة", "Goods receipt posted", "تم ترحيل إشعار استلام بضاعة إلى المخزون.",
				"A goods receipt was posted to inventory.", "goods_receipt", "Inventory", "Normal"),
			("طلب موظف بحاجة إلى موافقة", "Employee request needs approval",
				"طلب إجازة بانتظار موافقتك.", "A leave request is waiting for your approval.",
				"emp_request_submitted", "HR", "Normal"),
			("تجاوز حد الائتمان", "Credit limit exceeded", "عميل تجاوز حدّ الائتمان المسموح؛ تم إيقاف البيع الآجل.",
				"A customer exceeded their credit limit; credit sales are blocked.", "credit_block", "Sales", "Critical"),
			("تذكير متابعة عميل", "Customer follow-up reminder", "موعد متابعة عميل اليوم.",
				"A customer follow-up is due today.", "crm-reminder", "CRM", "Normal"),
			("فرصة بيع مكسوبة", "Opportunity won", "تم إغلاق فرصة بيع بنجاح.",
				"An opportunity was closed as won.", "opportunity_won", "CRM", "Normal"),
			("إعلان إداري", "Announcement", "إعلان جديد من الإدارة بخصوص مواعيد الإقفال الشهري.",
				"A new management announcement about the monthly closing dates.", "announcement", "Governance", "Normal"),
			("تم ترحيل التوصيل", "Delivery posted", "تم ترحيل أمر توصيل إلى العميل.",
				"A delivery order was posted to the customer.", "delivery_posted", "Sales", "Normal"),
			("أمر بيع جديد", "New sales order", "أمر بيع جديد بحاجة إلى تجهيز.",
				"A new sales order needs fulfilment.", "sales_order", "Sales", "Normal"),
		};

		internal const string LongNotificationBody =
			"تنبيه: أظهرت مطابقة المخزون فرقًا متكرّرًا في ممر البضائع الجافة على مدى ثلاثة أسابيع متتالية. " +
			"يُرجى مراجعة إشعارات الاستلام الخاصة بالمورّد المعني، والتحقّق من وحدات القياس المستخدمة عند الإدخال، " +
			"ثم تسجيل النتيجة في ورقة عمل الفروقات قبل نهاية الأسبوع الحالي حتى نتمكّن من إغلاق الفترة في موعدها.";

		/// Saved report layout names. One long name on purpose — the layout picker is a dropdown, and a long
		/// option is what makes a dropdown overflow.
		internal static readonly (string Ar, string En, string Report)[] Layouts =
		{
			("سجل الأحداث — عرض المدقّق", "Event log — auditor view", "Platform.BusinessEventLog"),
			("سجل الأحداث — الفواتير فقط", "Event log — invoices only", "Platform.BusinessEventLog"),
			("سجل الأحداث — آخر ٧ أيام", "Event log — last 7 days", "Platform.BusinessEventLog"),
			("سجل الأحداث — المهام", "Event log — tasks", "Platform.BusinessEventLog"),
			("دليل التقارير — مختصر", "Report catalogue — compact", "Platform.ReportCatalog"),
			("دليل التقارير — كامل", "Report catalogue — full", "Platform.ReportCatalog"),
			("سجل التشغيل — المرفوض", "Run history — denied", "Platform.ReportRunHistory"),
			("سجل التشغيل — المعاينات", "Run history — previews", "Platform.ReportRunHistory"),
		};

		internal const string LongLayoutName =
			"سجل الأحداث — الأحداث الداخلية للفواتير وأوامر التشغيل مرتّبة بالأحدث مع إخفاء أعمدة الحمولة " +
			"والمُنفِّذ لعرضه على الشاشة الكبيرة في غرفة العمليات";
	}
}
