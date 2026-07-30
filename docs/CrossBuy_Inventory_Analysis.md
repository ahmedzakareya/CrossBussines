# CrossBuy — تحليل معماري متكامل لنظام المخازن وسلسلة الإمداد (Inventory / Warehouse / Supply Chain)

> **الغرض:** تصميم وحدة مخازن كاملة (Full Inventory + Warehouse Management + Procurement
> Workflow) تُبنى **داخل** CrossBuy، تتكامل عضويًا مع: **(أ) شجرة الـ Admin/الفروع**
> (`Hierarchicals`) و**(ب) النظام المحاسبي** (الـ GL ومحرك الترحيل `JournalEntryService`).
> وثيقة تحليل + تصميم (مرجع لتقسيم prompts مرحلية). تُقرأ مع `CrossBuy_Accounting_Handoff.md`.

---

## 0. سياق: قرار "لا مخزون" السابق تم عكسه
المخزون كان خارج النطاق في تحليل المحاسبة، **واتغيّر دلوقتي**. التصميم المحاسبي يستوعب ده:
`SalesInvoiceLines` فيها `ItemDescription` + `RevenueAccountId` بدون ربط مخزون → الإضافة =
**ربط** (نضيف `ItemId` + قيد COGS عبر نفس محرك الترحيل) مش إعادة بناء.

## 1. مبادئ الأساس
- **Perpetual Inventory** (مستمر): كل حركة تحدّث الرصيد + التقييم + قيد GL فورًا. (Periodic مرفوض).
- **التقييم:** Moving Weighted Average افتراضي + **FIFO خيار** (configurable لكل صنف/فئة). LIFO ممنوع (EAS/IFRS). Standard Cost مؤجّل.
- **التقييم النهائي:** Lower of Cost or NRV (هبوط قيمة عند الإقفال).
- **المعادلة:** رصيد أول + وارد − صادر ± تسويات = رصيد آخر. **ثابت حرج:** تقرير تقييم المخزون == رصيد حساب المخزون 1103 في GL.
- ثنائية اللغة AR/EN لكل صنف/مخزن/فئة/وحدة وكل تقرير.

## 2. النطاق والقرارات المعمارية
**داخل النطاق:** أصناف + وحدات + تحويلاتها + باركود + دفعات/صلاحية/سيريال، مخازن متعددة + مواقع، كل الحركات، محرك تقييم (Moving+FIFO)، دورة مشتريات بـ workflow، دورة مبيعات/صرف، جرد، إعادة طلب، تكامل محاسبي كامل.
**خارج النطاق مبدئيًا:** التصنيع الكامل/BOM متعدد المستويات، Standard cost، WMS متقدم، forecasting إحصائي.

**قرارات ملزمة:**
1. Schema بـ SQL يدوي فقط (`inv_phaseN.sql`)، أسماء أعمدة = properties، `DECIMAL(19,4)`، bracket `[LineNo]`.
2. **محرك الترحيل واحد:** المخزون ممنوع يكتب في `JournalEntryLines` مباشرة — كله عبر `JournalEntryService.CreateAndPostAsync` بـ `SourceType`+`SourceId`.
3. **حساب المخزون 1103 = Control:** يرفض اليدوي، يستقبل من حركات المخزون (system) فقط، ورصيده = تقرير التقييم.
4. **المخازن تنتمي للفروع:** `Warehouse.BranchHierarchicalId → Hierarchicals.H_ID` (زي مراكز التكلفة).
5. شركة واحدة الآن (`DefaultCompanyId=1`) + `CompanyID` على كل كيان.
6. **إعادة استخدام:** `DocumentApprovalSteps` (الموافقات)، `Vendors`/`Customers`، فاتورة الشراء/البيع، `NotificationService`+SignalR.
7. Soft-delete + Audit دائمًا؛ الحركة المرحّلة تُعكس ولا تُحذف.

## 3. البيانات الأساسية
- **الأصناف:** `ItemCode`/Name/NameEn/Barcode، `ItemType` (Stockable/NonStockable/Service/Asset — بس Stockable يدخل التقييم/GL)، `ItemCategoryId` (تحمل ربط GL)، وحدات (Base/Purchase/Sales)، `CostingMethod`، `TrackBatch/Expiry/Serial`، `DefaultTaxCodeId`، `ItemEgsCode` (ETA)، صورة.
- **وحدات القياس + تحويلاتها** (التقييم دائمًا بالوحدة الأساسية).
- **الدفعات/الصلاحية** (FEFO، تنبيهات انتهاء، منع صرف المنتهي).
- **الأرقام التسلسلية** للأصناف عالية القيمة.
- **إعدادات صنف×مخزن:** Reorder/Min/Max/Safety/LeadTime/DefaultBin.
- **قوائم أسعار** للبيع (التكلفة من المحرك).

## 4. المخازن والمواقع
- **Warehouse:** كود/اسم/`BranchHierarchicalId`/`KeeperEmployeeId`/`AllowNegativeStock`، نوع (Main/Transit/Quarantine/Scrap/Consignment/Virtual).
- **BinLocations:** شجرة Zone>Aisle>Rack>Shelf>Bin (اختياري للمخازن الصغيرة).
- **المخزون السالب:** ممنوع افتراضيًا (configurable per warehouse).

## 5. حركات المخزون (القلب)
كل حركة = header+lines، حالة Draft→Posted→[Reversed]. الأنواع: GRN، صرف، مرتجع شراء/بيع، تحويل، تسوية، هبوط قيمة، تجميع.
**نمط الترحيل الموحّد (داخل DB transaction واحدة):** تحقّق الكميات/الوحدات/الدفعة → تحقّق الرصيد → حساب التكلفة → تحديث `StockOnHand`+طبقات → كتابة `StockMovements` (كارت الصنف) → حجز رقم → قيد GL عبر `JournalEntryService` → إشعار+audit.

## 6. محرك التكلفة (`IInventoryCostingService`)
- **Moving Average:** عند الاستلام متوسط جديد=(قيمة حالية+قيمة واردة)/(كمية حالية+واردة)؛ الصرف بالمتوسط الحالي. لكل (صنف×مخزن).
- **FIFO:** `CostLayers` (كمية متبقية+تكلفة+تاريخ)، الصرف من أقدم طبقة.
- **Landed Cost** (شحن/جمارك يوزّع على الاستلام) — Phase لاحق.
- **NRV/إعادة تقييم** عند الإقفال.
- **ثابت:** Σ(كمية×تكلفة) == رصيد 1103؛ job تسوية دوري.

## 7. دورة المشتريات
`PR → [موافقة] → RFQ → مقارنة → PO → [موافقة] → GRN [+فحص] → 3-Way Match → فاتورة AP [+دفع]`.
- **PR:** طلب داخلي بموافقة (`DocumentApprovalSteps`).
- **PO:** ملزم للمورد، موافقة فوق حد، حالات Draft→Approved→PartiallyReceived→Received→Closed.
- **GRN:** مقابل PO + tolerance، Quarantine للفحص. قيد: مدين المخزون / دائن **GRNI**.
- **3-Way Match:** PO↔GRN↔فاتورة المورد آليًا؛ فرق فوق tolerance يُحجز. عند الفاتورة: مدين GRNI / دائن الموردون (+ فروق سعر PPV).

## 8. دورة المبيعات
`SO → حجز → Picking → صرف/تسليم → فاتورة AR`.
- SO يحجز الرصيد (Reserved)؛ نقص → Backorder. الصرف (FEFO) ينقص المخزون.
- قيد الصرف: مدين COGS / دائن المخزون. الفاتورة (`SalesInvoices` الموجودة + `ItemId`) = الإيراد منفصل عن التكلفة (مبدأ المقابلة).

## 9. الجرد
- **كامل:** تجميد→عدّ→مقارنة→فروقات→قيد تسوية (بموافقة).
- **دوري (Cycle):** جزء دوريًا مبني على ABC.
- **تسويات/تالف:** بمبرر+موافقة+قيد؛ التالف لمخزن الخردة أو write-off.

## 10. التحويلات + مخزن العبور
`StockTransfer`؛ بين الفروع عبر **Transit**: إرسال (مدين عبور/دائن المصدر)، استلام (مدين الوجهة/دائن عبور). تحويل بين فرعين = حركة بين مركزي تكلفة.

## 11. التخطيط
Reorder Point (اقتراح PR آلي)، ABC، MRP-lite (بدون forecasting)، تقارير الراكد/البطيء.

## 12. التكامل المحاسبي (الأهم)
**حسابات GL جديدة للـ COA seeder:** `1103` مخزون (Control)، `110302` مخزون عبور، `2104xx` GRNI، `5101xx` COGS، `5102xx` فروق جرد، `5103xx` هبوط قيمة، `5104xx` فروق أسعار شراء.
**ربط الفئة بالحسابات** (مثل `PostingRules`): InventoryAccountId/CogsAccountId/AdjustmentAccountId/GrniAccountId.
**القيود التلقائية:**
| الحركة | مدين | دائن |
|---|---|---|
| GRN | 1103 | GRNI 2104 |
| فاتورة مورد (3-way) | GRNI 2104 (+VAT 110401) | الموردون 2101 |
| صرف/بيع | COGS 5101 | 1103 |
| مرتجع مبيعات | 1103 | COGS 5101 |
| مرتجع مشتريات | GRNI/الموردون | 1103 |
| تسوية نقص | فروق 5102 | 1103 |
| تسوية زيادة | 1103 | فروق 5102 |
| تحويل إرسال | عبور 110302 | المصدر 1103 |
| تحويل استلام | الوجهة 1103 | عبور 110302 |
| هبوط قيمة | 5103 | 1103 |
**ثابت:** تقرير التقييم == رصيد 1103 (تقرير/job تسوية إلزامي).

## 13. التكامل مع شجرة الفروع
كل مخزن مربوط بعقدة فرع → مخزون/تكلفة/حركة/ربحية لكل فرع تلقائيًا، يتربط بمركز التكلفة المقابل. صلاحيات أمين المخزن مربوطة بفرعه. التحويل بين فرعين = حركة بين مركزي تكلفة.

## 14. الموافقات
تعميم `DocumentApprovalSteps`: `DocumentType` يشمل PurchaseRequisition/PurchaseOrder/StockAdjustment/StockTransfer/StockWriteOff/StockCount. تسلسلي، أي رفض يوقف، إشعارات، حدود configurable.

## 15. التقارير (bilingual + drill-down)
كارت الصنف (Item Ledger)، رصيد المخزون، تقييم المخزون (==1103)، الأعمار، الصلاحية، إعادة الطلب، فروقات الجرد، معدل الدوران، استثناءات 3-Way، Dashboard (ApexCharts/fl_chart).

## 16. نموذج البيانات (ملخص — التفصيل الكامل في الـ SQL scripts)
ItemCategories (+GL mapping)، UnitsOfMeasure، Items (+UX index Company+Code)، UoMConversions، ItemBarcodes، ItemWarehouseSettings، Warehouses، BinLocations، StockOnHand (Qty/Reserved/AverageCost)، Batches، SerialNumbers، CostLayers (FIFO)، StockMovements (كارت الصنف)، StockDocuments(+Lines)، PurchaseRequisitions(+Lines)، PurchaseOrders(+Lines, ReceivedQty)، SalesOrders(+Lines, DeliveredQty)، StockCounts(+Lines, VarianceQty computed).
**تعديلات على موجود:** `ALTER TABLE SalesInvoiceLines ADD ItemId`، `PurchaseInvoiceLines ADD ItemId, GrnDocumentId`. FKs تُضاف يدويًا بعد تأكيد أنواع الأعمدة.

## 17. الخدمات (BL)
IItemService، IWarehouseService، **IInventoryCostingService** (التقييم)، **IStockTransactionService** (محرّك الحركة الوحيد)، **IInventoryPostingService** (الجسر للمحاسبة)، IBatchSerialService، IProcurementService، ISalesOrderService، IStockCountService، IReplenishmentService، IInventoryReportService.

## 18. API (`api/inv/*`, JWT) — نمط `api/acc/*`
items/categories/uoms، warehouses/bins، stock-on-hand/item-ledger/valuation، receipts/issues/transfers/adjustments(+post/reverse)، pr/po(+approve/receive/3way)، so(+deliver)، counts(+post)، reorder/abc/dead-stock/expiry، **summary** (one-call KPIs للموبايل).

## 19. الويب (Razor+Metronic)
`Views/Inventory/*` + layout `_LayoutInventory` (الموجود). بحث حقيقي + pager، شجرة الفئات/المواقع collapse مع caret أول عمود، Pure Metronic بلا custom CSS، RTL+Cairo، شاشات تفصيل + موافقة، Select2/flatpickr enhancers موروثة.

## 20. الموبايل (Flutter)
باركود (استلام/صرف/جرد بالكاميرا)، Receiving مقابل PO، Picking، Cycle count، اعتماد PR/PO، استعلام رصيد. نمط Provider+Dio+AppNav+i18n + tab مخزون (زي FinanceScreen).

## 21. الأدوار + SoD
WarehouseKeeper (مخزنه/فرعه)، PurchasingOfficer/Manager، InventoryManager، InventoryAuditor (قراءة). SoD: طالب الشراء≠معتمده≠مستلمه؛ الجارد≠معتمد التسوية. مربوطة بالفرع. Audit كامل.

## 22. الترقيم (`NumberSequences`)
ITM/GRN/ISS/TRF/ADJ/PR/PO/SO/CNT/SRT/PRT — صيغة `PREFIX-YYYY-NNNNNN`، الحجز داخل transaction الترحيل.

## 23. خارطة الطريق المرحلية
- **I0** الأساس + البيانات الأساسية (schema + CRUD + seeder + ربط GL للفئة). **وقفة.**
- **I1** محرك الحركة + التقييم (Moving أولًا) + StockOnHand + كارت الصنف + استلام/صرف/تسوية + القيود التلقائية + تقييم + **تسوية 1103**. **(العمود الفقري — وقفة مهمة).**
- **I2** ربط البيع/الشراء الموجودين (`SalesInvoiceLines.ItemId`→COGS، فاتورة الشراء↔الاستلام). **وقفة.**
- **I3** دورة المشتريات (PR→PO+موافقات+GRN+3-way+GRNI).
- **I4** دورة المبيعات (SO+حجز+تجهيز+صرف+backorder).
- **I5** دفعات/صلاحية/سيريال (FEFO+تنبيهات).
- **I6** التحويلات + مخزن العبور.
- **I7** الجرد (كامل+دوري+ABC+تسويات).
- **I8** التخطيط (إعادة طلب+راكد+FIFO لو مطلوب).
- **I9** Landed Cost + NRV (يتكامل مع الإقفال).
- **I10** التقارير + الداشبورد.
- **I11** الموبايل (باركود).
> **التوصية:** I0 → I1 → I2 أول (مخزون شغّال ومربوط محاسبيًا = أكبر slice ذو قيمة).

## 24. المصائد (قيود ملزمة)
1. migrations مكسورة → SQL يدوي، أسماء أعمدة=properties، `[LineNo]`، `SET QUOTED_IDENTIFIER ON`.
2. محرك ترحيل واحد — ممنوع الكتابة في `JournalEntryLines` مباشرة.
3. 1103 = Control (يعتمد flag الـ Control من المحاسبة)؛ رصيده = التقييم.
4. `DECIMAL(19,4)`، التقييم بالوحدة الأساسية داخليًا.
5. **التزامن:** قفل صف الرصيد (`UPDLOCK`) داخل الـ transaction.
6. المخزون السالب ممنوع افتراضيًا.
7. مصائد Razor: `from`/`to`→`fromDate`/`toDate`، نطاق `T()` داخل `@functions`، الـ views precompiled → rebuild+restart.
8. التحقق عبر JSON من API مش `sqlcmd` console (العربي).
9. التحويلات الجارية لازم تمرّ بمخزن العبور.
10. EGP مع أعمدة العملة، شركة واحدة + schema قابل للتوسّع، عزل ETA (`ItemEgsCode` جاهز).

## 25. القرارات المفتوحة (محتاجة تأكيد المستخدم)
1. طريقة التقييم الافتراضية: Moving (توصية) + FIFO خيار؟
2. تتبّع الدفعات/الصلاحية؟ (حرج للأدوية/الأغذية، يخدم ClinicOS).
3. الأرقام التسلسلية؟
4. عمق دورة الشراء: مبسّطة (PO→GRN) أولًا (توصية) أم الكاملة (PR→RFQ→PO→GRN→3-way)؟
5. Landed Cost؟
6. المواقع/الأرفف (Bins)؟
7. التجميع/التصنيع (Kitting/BOM)؟
8. المخزون السالب: منع نهائي (توصية)؟

*وثيقة تحليل — مرجع لتقسيم prompts مرحلية. سكشن 24 + قيود الوثائق السابقة = قيود ملزمة.*
