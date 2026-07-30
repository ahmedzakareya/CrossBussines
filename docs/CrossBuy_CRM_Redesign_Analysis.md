# CrossBuy — تحليل وتصميم موديول CRM (عزل + إعادة تصميم عالمي)
> الموديول 3 — **وثيقة تحليل/تصميم فقط، لا كود.** يبني على CRM الحالي (Lead/Opportunity/Activity/Campaign) بدل هدمه، ويرفعه لمستوى عالمي (Salesforce/Dynamics/HubSpot) عبر مراحل قابلة للاعتماد بندًا بندًا.

---

## 0) الملخّص التنفيذي
- **العزل (3.1) منجز هيكليًا تقريبًا:** CRM في namespace خاص (`CrossBuy.Models.Context.Crm`)، وله `CrmController`/`CrmService`/`Views/Crm`/DbSets مستقلة، ويرتبط بالباقي فقط عبر `IReceivableService.CreateCustomerAsync` + مفاتيح `CustomerId`/`QuotationId`/`CampaignId`. المتبقّي من العزل: **حدّ صريح** (`ICrmCustomerLink`) يفصل CRM عن خدمة الذمم، و**صلاحيات CRM** (لا توجد اليوم — CRM مفتوح لأي مستخدم).
- **الموجود:** Leads، Opportunities (مراحل ثابتة + Kanban + إشعار فوز)، Activities (مرنة الربط)، Campaigns (+ROI)، تحويل Lead→Customer، تصدير Excel، إشعارات. **بلا**: مالك للسجل (Owner)، فصل Account/Contact، خطوط أنابيب متعددة، بنود الفرصة (OpportunityProduct)، أعضاء الحملة، قوائم تسويقية، تذاكر/SLA، حقول مخصّصة، أتمتة، تقييم Lead، تنبؤ، توزيع، أو صلاحيات/نطاق بيانات.

---

## 1) الوضع الحالي (Audit موجز)
| العنصر | الموقع | الحالة |
|---|---|---|
| كيانات `Lead/Opportunity/Activity/Campaign` | Models/Context/Crm/Crm.cs | ✅ موجودة، **بلا OwnerEmployeeId** |
| `ICrmService` (بحث/CRUD/تحويل/Pipeline) | BL/CrmService.cs | ✅ يعتمد على `IReceivableService` مباشرة |
| `CrmController` + Views/Crm (لوحة/Leads/Opps/Pipeline/Activities/Campaigns) | Controllers/CrmController.cs | ✅ `[SessionValidation]` فقط — **بلا صلاحيات** |
| الروابط | Opportunity.QuotationId/CustomerId/CampaignId؛ Lead.CustomerId/CampaignId | ✅ مفاتيح، بلا تكامل O2C تلقائي |
| Account/Contact | لا يوجد | ❌ Customer يلعب الدورين؛ `Customer.ContactPerson` نص واحد |
| صلاحيات CRM | لا يوجد `CrmUserRole` | ❌ |

---

## 2) النموذج المستهدف (Target)
### 2.1 السجل الموحّد للطرف (360°) — **محسوم: Account كيان CRM مستقل**
- **`Account`** (شركة/كيان): **كيان CRM مستقل** عن `Customer` المحاسبي. يحمل `OwnerEmployeeId`، الصناعة، الحجم، المصدر، و**`CustomerId int? NULL`** (الرابط).
- **`Contact`** (شخص): ينتمي لـ Account؛ اسم/منصب/هاتف/بريد؛ جهة اتصال رئيسية.
- **`Lead`/`Opportunity` تُنشأ على Account** (لا على Customer) — فلا تتلوّث شجرة العملاء المالية بمحتملين لم يشتروا.
- **عند فوز الفرصة (Won):** يُنشأ/يُربط `Customer` محاسبي تلقائيًا (نفس منطق `ConvertLeadToCustomerAsync` الموجود عبر `ICrmCustomerLink`) ويُضبط `Account.CustomerId` (Account واحد ↔ Customer واحد)، مع مزامنة البيانات الأساسية. **بلا كسر** لأي تكامل قائم مع Customer.

### 2.2 المبيعات
- **`Pipeline` + `PipelineStage`** (خطوط متعددة، احتمالية لكل مرحلة، ترتيب) بدل المراحل الثابتة الحالية.
- **`Opportunity`** + `OwnerEmployeeId` + `PipelineId` + سبب كسب/خسارة + **`OpportunityProduct`** (أصناف باستخدام منتقي الأصناف ومحرّك التسعير من الموديول 2).
- **تكامل O2C:** الفرصة → عرض سعر (موجود) → أمر بيع → فاتورة، وربط `Account→Customer` عند الفوز.

### 2.3 التفاعلات
- **`Activity`** موحّدة (مكالمة/اجتماع/بريد/مهمة/ملاحظة) مرتبطة بأي كيان عبر (EntityType, EntityId) → **Timeline موحّد**، + `OwnerEmployeeId` + تذكير.

### 2.4 التسويق
- **`Campaign`** (موجود) + **`CampaignMember`** (Lead/Contact في حملة) + ROI/attribution محسوب من الفرص الرابحة، + **`MarketingList`/Segment** (قوائم استهداف).

### 2.5 الخدمة
- **`Ticket`** (عميل/أولوية/حالة/مسؤول) + **`SLA`** (زمن استجابة/حل حسب الأولوية + تنبيه تجاوز عبر NotificationService).

### 2.6 المرونة والأتمتة
- **`CustomFieldDefinition` + `CustomFieldValue`** (حقول مخصّصة لأي كيان بلا تغيير سكيمة).
- **`AutomationRule`** (Trigger + Condition + Action): عند (lead جديد/تغيّر مرحلة/تجاوز SLA/مرور وقت) → (تعيين/مهمة/إشعار/تغيير حقل).
- **Lead Scoring** (نقاط حسب مصدر/تفاعل/قيمة/اكتمال)، **Forecasting** (Σ قيمة×احتمالية لكل فترة/مندوب/خط)، **Assignment/Routing** (round-robin / بالمنطقة عبر Hierarchicals / بالحِمل).

### 2.7 الصلاحيات ونطاق البيانات
- **`CrmUserRole`** (SalesRep يرى سجلاته / SalesManager للفريق عبر Hierarchicals / Marketing / CrmViewer قراءة) + فلترة نطاق البيانات في كل استعلام (OwnerEmployeeId).

---

## 3) التكامل والثوابت
- CRM **لا يُرحّل GL** بذاته؛ يقترح/يربط فقط. الفوز يربط Account↔Customer ويبدأ حلقة O2C (المعتمدة).
- حدّ صريح `ICrmCustomerLink` (إنشاء/قراءة Customer) يفصل CRM عن `IReceivableService`.
- إعادة استخدام: NotificationService+SignalR (تنبيهات/SLA)، Hierarchicals (الفرق/التوزيع)، RBAC نمط AccountingUserRole، select2-ajax، Confirm+Toastr، تصدير Excel، محرّك التسعير (بنود الفرصة)، المنيو الموحّد.
- ترحيل بيانات CRM الحالية للنموذج الجديد (Leads/Opps/Activities/Campaigns تبقى وتُثرى بـ Owner/Pipeline افتراضيين).

---

## 4) تقسيم التنفيذ المقترح (مراحل — بند واحد + وقفة لكل)
> CRM ضخم؛ أقترح ترتيبًا يسلّم الأعلى قيمة أولًا ويبني تدريجيًا. كل بند: تحقّق + handoff + وقفة.

- **3-1 العزل + الصلاحيات:** `ICrmCustomerLink` (فصل عن الذمم) + `OwnerEmployeeId` على الكيانات + `CrmUserRole` (الأدوار الأربعة) + نطاق البيانات (المندوب يرى سجلاته) + شاشة إسناد أدوار + Perm بالمنيو. إثبات أن CRM الحالي يعمل.
- **3-2 الطرف 360° (Account + Contact):** كيانا `Account`(+CustomerId) و`Contact`؛ إعادة توجيه Lead/Opportunity لـ `AccountId`؛ الفوز يُنشئ/يربط Customer ويضبط Account.CustomerId؛ شاشتا Accounts/Contacts + Timeline مبدئي؛ ترحيل البيانات الحالية (Customer ↔ Account).
- **3-3 خطوط الأنابيب + بنود الفرصة:** `Pipeline`/`PipelineStage` متعددة قابلة للتهيئة + `OpportunityProduct` (أصناف + تسعير) + سبب كسب/خسارة + تكامل الفرصة↔عرض سعر/أمر بيع.
- **3-4 الأنشطة الموحّدة (Timeline):** ربط Polymorphic (EntityType,EntityId) + مالك + تذكير + Timeline على كل كيان.
- **3-5 التسويق:** `CampaignMember` + ROI/attribution + `MarketingList`/Segment.
- **3-6 الخدمة:** `Ticket` + `SLA` + تنبيه تجاوز.
- **3-7 المرونة والأتمتة:** `CustomFieldDefinition/Value` + `AutomationRule` + Lead Scoring + Forecasting + Assignment/Routing.
- **3-8 التقارير 360° + لوحات:** خط الأنابيب/أداء المندوبين/التنبؤ/ROI الحملات/أداء الدعم + إنفاذ نطاق البيانات في كل مكان.

**قرار #2:** نمضي في كل المراحل (3-1→3-8) بالترتيب؟ أم تختار مجموعة فرعية ذات أولوية الآن (مثلاً 3-1، 3-3، 3-7) ونؤجّل الباقي؟

---

## 5) القرارات — **محسومة** ✅
1. **Account/Contact:** ✅ **Account كيان CRM مستقل** (360°) منفصل عن Customer؛ Leads/Opps على Account؛ الفوز يُنشئ/يربط Customer ويضبط `Account.CustomerId`؛ بلا ازدواج وبلا كسر تكامل.
2. **النطاق/الترتيب:** ✅ **كل المراحل 3-1 → 3-8 بالترتيب.**
3. **نطاق البيانات:** ✅ SalesRep سجلاته فقط (OwnerEmployeeId)، SalesManager فريقه عبر Hierarchicals، CrmViewer قراءة شاملة.
4. **خطوط الأنابيب:** خط افتراضي واحد بالمراحل الحالية + إمكانية إضافة خطوط (في 3-3).

---

## 6) الثوابت/المبادئ المحفوظة
- لا مساس بـ JournalEntryService/StockService؛ CRM طبقة علاقات فوق الحلقة المعتمدة.
- نفس قاعدة البيانات (فصل منطقي زي HR)، تكامل عبر interfaces صريحة.
- كل كيان جديد يحترم RBAC + نطاق البيانات + Confirm+Toastr + المنيو الموحّد، وseed/test endpoints لكل بند.

---
**انتهت الوثيقة — في انتظار قراراتك (§5) قبل كتابة أي كود.**
