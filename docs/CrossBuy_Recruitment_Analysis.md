# CrossBuy — طبقة التوظيف (Recruitment) + قائمة المستندات (Checklist)

> وثيقة تصميم — **لا كود بعد**. تُبنى فوق موديول الموارد البشرية القائم (Admin) دون كسر أي شيء.
> القرارات الأربعة معتمدة من المستخدم: كيان جديد · مسار متوسط · كتالوج عام إلزامي/اختياري · زر «تعيين» يفتح wizard مملوءًا مسبقًا.

## 1) الوضع الحالي (مُتحقَّق منه)
- الموظف يُنشأ مباشرة عبر wizard 4 خطوات (`AdminController.EmployeeData` → `SaveEmployee` → `EmployeeService.SaveEmployeeAsync`). لا مسار توظيف يسبقه.
- `EmployeeRequest` = **خطابات + أذونات ESS فقط** (لا علاقة بالتوظيف) — **لن يُمَسّ**.
- المستندات: `EmployeeDocument` (نوع نصّي حر + انتهاء) + `HrDocumentAttachment` (ملفات) + `HrDocumentService`. **لا كتالوج مطلوب ولا مفهوم موجود/ناقص**.
- **لا يوجد أي مفهوم توظيف/مرشّح إطلاقًا.**
- 🐞 **خلل مكتشف:** `EmployeeService.SaveEmployeeAsync` يُسقط `CountryID` و`DepartmentID` و`EmploymentType` عند الحفظ — يُصلَح ضمن هذا العمل (يحتاجه مسار التعيين).

## 2) نموذج البيانات (إضافي بالكامل — جداول جديدة + أعمدة nullable)

### أ) `JobApplication` (طلب توظيف)
| الحقل | النوع | ملاحظة |
|---|---|---|
| ID, CompanyID | int | |
| ApplicationNo | int | تسلسل للعرض |
| FirstName, LastName | string | |
| FullName, FullNameEn | string? | اسم كامل ثنائي اللغة |
| Email, PhoneNumber, Address | string? | |
| DateOfBirth, Gender, MaritalStatus | | بيانات شخصية |
| CountryID? | int? → CountriesLookup | |
| **JobTitleID?** | int? → JobTitle | الوظيفة المتقدَّم لها |
| BranchID?, EmpCompanyID?, DepartmentID? | int? | الفرع/الشركة/الإدارة (Hierarchical) |
| EmploymentType? | string? | |
| ExpectedSalary? | decimal? | اختياري |
| Source? | string? | مصدر الطلب (اختياري) |
| **Status** | string | `New` → `Review` → `Interview` → `Accepted`/`Rejected` → `Hired` |
| AppliedAt, ReviewedAt?, InterviewAt?, DecisionAt?, HiredAt? | DateTime? | طوابع المراحل |
| DecisionNote? | string? | سبب القبول/الرفض |
| **HiredEmployeeID?** | int? → Employee | يُملأ عند التعيين (ربط الطلب بالموظف) |
| Notes? | string? | |
| CreatedAt/By, UpdatedAt/By | | تدقيق |

### ب) `RequiredDocumentType` (كتالوج أنواع المستندات المطلوبة)
| الحقل | النوع | ملاحظة |
|---|---|---|
| ID, CompanyID | int | |
| Name, NameEn | string | ثنائي اللغة (هوية/مؤهل/شهادة خبرة/تأمين/تصريح عمل...) |
| **IsMandatory** | bool | إلزامي أم اختياري |
| SortOrder, IsActive | | |

### ج) `ApplicationDocument` (مستند مرفق بالطلب — يعكس `EmployeeDocument`)
| الحقل | النوع | ملاحظة |
|---|---|---|
| ID, ApplicationID | int → JobApplication | |
| **RequiredDocumentTypeID?** | int? → RequiredDocumentType | ربط بنوع الكتالوج (أو Other) |
| FilePath, FileName | string | يعيد استخدام `/uploads/hr-docs` |
| DocNumber?, IssueDate?, ExpiryDate? | | نفس حقول EmployeeDocument (لترحيل نظيف) |
| UploadedAt | | |

> **الـ checklist مُشتَقّ (لا جدول حالة منفصل):** لكل `RequiredDocumentType` نشِط → هل للطلب `ApplicationDocument` من نوعه؟ **نعم ✅ موجود / لا ⬜ ناقص**. الإلزامي الناقص يمنع/يحذّر عند التعيين.

## 3) التدفّق
```
[إنشاء طلب] New ──► Review ──► Interview ──► Accepted ──(زر تعيين)──► Hired
                                     └──► Rejected (نهائي)
   │
   └─ رفع المستندات على الطلب → checklist يعرض ✅/⬜ (من الكتالوج)
```
**التعيين (زر «تعيين» يظهر فقط عند `Accepted`):**
1. يفتح wizard الموظف القائم **مملوءًا مسبقًا** من الطلب (اسم/تواصل/وظيفة/فرع/شركة/إدارة/نوع توظيف).
2. HR يراجع/يكمّل ويحفظ → يُنشأ الموظف عبر `EmployeeService.SaveEmployeeAsync` (نفس المسار القائم — لا كاتب جديد).
3. **ترحيل المستندات:** كل `ApplicationDocument` → `EmployeeDocument` (+ نقل الملف)، فتظهر مباشرة في خزنة مستندات الموظف القائمة (`HrDocuments`).
4. تحديث الطلب: `Status='Hired'`, `HiredEmployeeID`, `HiredAt`.
5. استكمال الإجراءات = عقد (`EmploymentContract` القائم) + بقية المستندات في شاشة `HrDocuments` القائمة.

## 4) الشاشات (تُصمَّم بمرجع Metronic أعرضه قبل البناء — القاعدة الثابتة)
1. **قائمة طلبات التوظيف** — جدول/لوحة بحالة + بحث حقيقي + فلتر مرحلة.
2. **بيانات المتقدّم** (إنشاء/تعديل) — نموذج.
3. **تفاصيل الطلب** — بيانات + **checklist مستندات (✅/⬜ مع رفع)** + أزرار المرحلة (تقديم/قبول/رفض) + زر «تعيين» عند القبول.
4. **إعداد: أنواع المستندات المطلوبة** — CRUD للكتالوج.
5. **wizard الموظف القائم** — دعم prefill من الطلب (تعديل بسيط، لا شاشة جديدة).

## 5) الربط بالقائمة
تحت `MainMenu.Admin()` مجموعة «الموظفون والهيكل»: إضافة **«التوظيف / طلبات التوظيف»** + (إعداد) **«أنواع المستندات المطلوبة»**. لا portal جديد — امتداد لموديول HR.

## 6) ضمانات عدم الكسر
- كل شيء **إضافي**: 3 جداول جديدة + عمود `Employee`? (لا حاجة — الربط عكسي عبر `JobApplication.HiredEmployeeID`). SQL idempotent.
- `EmployeeRequest` (خطابات/أذونات ESS) **لا يُمَسّ**.
- الإنشاء الفعلي للموظف يمرّ بـ `EmployeeService` القائم فقط (لا منطق موازٍ).
- المستندات تعيد استخدام مجلد الرفع القائم + تتحوّل لكيانات HR القائمة عند التعيين.
- ترجمات ar/en/fr لكل نص.

## 7) خطة البناء المقترحة (مراحل، كلٌّ: بناء → اختبار → CDP → وقفة)
- **R0** — الكيانات + DbSets + SQL idempotent + كتالوج أنواع المستندات (CRUD) + إصلاح `EmployeeService` الثلاثي. (اختبار r0-test)
- **R1** — قائمة الطلبات + إنشاء/تعديل المتقدّم (بمرجع تصميم).
- **R2** — تفاصيل الطلب + مسار المراحل (تقديم/قبول/رفض) + رفع مستندات + checklist ✅/⬜.
- **R3** — زر «تعيين» → prefill wizard → إنشاء الموظف + ترحيل المستندات + ربط الطلب. (اختبار صارم: طلب→موظف، المستندات ترحّل، الطلب Hired، لا ازدواج.)
- **R4** — تلميع + دليل المستخدم.

---
**وقفة.** راجع الوثيقة — عند «نفّذ R0» أبدأ الكيانات والكتالوج فقط ثم أقف. لن أصمّم أي شاشة قبل عرض مرجع Metronic عليك.
