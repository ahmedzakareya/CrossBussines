# 10 — Bilingual terminology

The vocabulary both manuals must share, and where the two languages do not yet agree.

**Machine-readable: `bilingual-glossary.csv` — 4,333 interface keys, each with Arabic, English,
French, the source of each, and a translation status.**

---

## 1. How the glossary was built

Every localisation key used in any of the 372 views was resolved against the real resource files
in all three cultures, reproducing the runtime fallback rule exactly:

```
1.  Resources/Views/<Folder>/<View>.<culture>.resx     the screen's own file
2.  Resources/SharedResources.<culture>.resx           the shared file
3.  the KEY ITSELF                                     what the user actually reads when neither exists
```

That third rule is why the glossary is trustworthy: it records **what a user sees**, not what a
translator intended. Each row carries `ar_source`, `en_source` and `fr_source` — one of `view`,
`shared`, or `fallback-to-key`.

Columns: `key · arabic · english · french · ar_source · en_source · fr_source ·
translation_status · use_count · first_used_in`.

## 2. The three categories the brief asks for

| Category | Meaning | How to find it in the CSV |
|---|---|---|
| **Exact existing interface translation** | Both languages come from a resource file. Quote verbatim; the user will find these words on screen. | `translation_status = translated in all three` |
| **Missing or inconsistent interface translation** | One culture falls back to the key, so the user reads the other language. | `translation_status` starts with `MISSING` |
| **Proposed editorial translation** | Needed where the interface has no word, or where the interface word is wrong. **None is proposed in this package** — see §6. | — |

## 3. The translation estate

```
891 .resx files over 308 translation units

   Arabic    308 units   ← complete: every screen has one
   French    293 units
   English   289 units
   neutral     1 unit    (SharedResources.resx)
```

`SharedResources` holds 979 neutral / **1,270 Arabic** / 993 English / 1,087 French keys.

**Arabic is the most complete language in this product**, and by a clear margin. Any assumption
that English is the source language and Arabic the translation is backwards here.

### 3.1 Screens with no English resource — 19

```
Accounting/MatchReceipts       Comm/Compose        Pos/Dashboard
Accounting/SalesInvoicePrint   Comm/Index          Project/Dashboard
Admin/Index   (HR Dashboard)   Comm/View           Shared/_EntityConversation
Admin/_HrDocGallery            FileManager/Index   Shared/_NotificationBell
Announcements/Index            Notifications/Index Store/Category
Approvals/Index                Calendar/Index      Store/Product
Chat/Index
```

### 3.2 Screens with no French resource — 15

```
Accounting/MatchReceipts       Comm/Compose        Project/Dashboard
Accounting/SalesInvoicePrint   Comm/View           Shared/_NotificationBell
Admin/Index                    Notifications/Index Store/Category
Admin/_HrDocGallery            Pos/Dashboard       Store/Product
Approvals/Index                Chat/Index          Workspace/Index
```

**Twelve screens are missing both.** Two of them — `_NotificationBell` and `_EntityConversation` —
render on *every* page, so the notification bell and the comment box are English throughout the
French interface.

Because the keys are written as English phrases, the fallback is invisible to an English reader
and obvious to everyone else. **This is the main reason the two manuals cannot yet be written to
identical depth without a decision** (§6).

## 4. Where a screen title has no Arabic

**18 screen headings fall back to the key**, so an Arabic user reads English at the top of the
page. They are in the glossary with `ar_source = fallback-to-key`.

The Arabic manual therefore has a choice on each one:

1. **Quote what is on screen** (English) — accurate, and the reader will find it.
2. **Print an Arabic title the screen does not show** — helpful reading, useless for navigating.
3. **Fix the resource first**, then write the Arabic title.

Option 3 is right, but it is a product change and outside this brief. Until then **option 1** is
the honest default, and the manual should mark such titles so a later pass can sweep them.

## 5. What the view layer gets right

| | Count |
|---|---:|
| Localisation keys resolved | **4,333** |
| Localizer keys written in Arabic (untranslatable by construction) | **0** |
| Views containing hardcoded Arabic text | 10 |
| Hardcoded Arabic occurrences | 25 |

Nine of those ten views are legitimate — five layouts carrying «العربية» in the language switcher
plus two POS sign-in screens. **The view layer is disciplined.** The leaks are elsewhere:

- **Data, not text.** The product's rule is that every displayed field has a twin column
  (`Name`/`NameEn`), both captured on input, resolved at render. **56 twin-column pairs** exist in
  the model — `Name` ×21, `Title` ×8, `Description` ×7, plus `FullName`, `Subject`, `BankName`,
  `TradeName`, `NationalityName`, `ItemDescription` and others. Where the English twin was never
  typed, an English screen shows the Arabic value. That is a **capture** gap, not a translation
  gap, and the manual's data-entry instructions should say so: *fill both name fields.*
- **Business-rule messages.** 80 refusals are thrown as English strings from the business layer
  (§7).

## 6. Why no editorial translations are proposed here

The brief allows a *proposed editorial translation* as a distinct category. **This package
proposes none**, deliberately.

A discovery package that invents Arabic for 18 screen titles and 19 screens' worth of English
hands the manual writer text that does not exist in the product, with nothing to distinguish it
from text that does. Every gap is instead **named, counted and located**, so the decision —
translate the product, or document the gap — is taken by the owner rather than absorbed silently
into a manual.

## 7. Message translation, measured

From `messages-catalog.csv` (223 messages):

| Status | Count |
|---|---:|
| Translated in Arabic and French | **115** |
| Source string is English (business-layer exceptions) | 80 |
| Confirmation prompts — text assembled at runtime, not resolvable from source | 14 (of 23) |
| Missing a translation | 5 |

The 115 are exact and quotable in both manuals. Examples, verbatim:

| English | العربية |
|---|---|
| The fiscal period is closed — posting into it is not allowed | الفترة المالية مقفلة — الترحيل إليها غير مسموح |
| Negative values are not allowed | القيم السالبة غير مسموح بها |
| Currency #{0} is undefined or has no decimal precision — silent 2-decimal rounding is not allowed | العملة رقم {0} غير معرّفة أو بلا دقّة عشرية — لا يُسمح بتقريب صامت |
| Delete message? | حذف الرسالة؟ |
| Branch not found | الفرع غير موجود |

**The 80 business-layer refusals are English source strings.** Where a screen localises them it
does so at the controller. What an Arabic user actually sees for each of them was **not observed**
— it needs the refusal triggered on screen. One of them (`العلامة غير موجودة`) is thrown in
Arabic, which is the mirror-image inconsistency.

## 8. The reporting layer is the exception, and the model

Every report dataset, column and parameter declares both languages in code:

```csharp
TitleAr = "أرصدة المخزون",   TitleEn = "Stock on hand",
Key = "QtyOnHand",  TitleAr = "الكمية",       TitleEn = "Qty on hand",
Key = "AvgCost",    TitleAr = "متوسط التكلفة", TitleEn = "Avg cost",
```

**305 of 305 report columns have both.** No fallback, no gap.

The reports chapter of both manuals can therefore be written to identical depth with no editorial
invention at all — and it is worth saying in the handover that this pattern, not the resx
fallback, is what the rest of the product would need to reach the same standard.

## 9. Core vocabulary — a starting extract

Verified interface translations, high use count, from `bilingual-glossary.csv`:

| English | العربية | Where |
|---|---|---|
| Items | الأصناف | Inventory master data |
| Stock on hand | أرصدة المخزون | Inventory, report |
| Stock movements | حركات المخزون | Inventory, report |
| Warehouse | المخزن | Inventory |
| Item code | كود الصنف | Inventory |
| Qty on hand | الكمية | Report column |
| Avg cost | متوسط التكلفة | Report column |
| Trial balance | ميزان المراجعة | Accounting |
| Chart of accounts | شجرة الحسابات | Accounting |
| Journals | القيود اليومية | Accounting |
| Cost centers | مراكز التكلفة | Accounting |
| Sales invoices | فواتير المبيعات | Accounting |
| Purchase invoices | فواتير المشتريات | Accounting |
| Receipts | سندات القبض | Accounting |
| Payments | سندات الدفع | Accounting |
| Sales returns (credit note) | مرتجعات البيع (إشعار دائن) | Accounting |
| Purchase returns (debit note) | مرتجعات الشراء (إشعار مدين) | Accounting |
| AR aging | أعمار ديون العملاء | Accounting |
| Leads | العملاء المحتملون | CRM |
| Opportunities | الفرص البيعية | CRM |
| Administrative structure | الهيكل الإداري | Admin, report |
| Roster schedule | جدول المناوبات | HR, report |
| Business event log | سجل أحداث الأعمال | Platform, report |
| Report catalogue | دليل التقارير | Platform, report |
| Progress billing | المستخلص | Projects |
| Track batch / expiry / serial | تتبّع الدفعة / الصلاحية / السيريال | Inventory item form |

The full 4,333-row glossary is the deliverable; this table is only an orientation.

## 10. Equivalent coverage — the standing requirement

> **Both manuals must have equivalent functional coverage. Do not silently make the English
> manual shorter.**

The risk here runs the other way. **Arabic is the complete language**, so the pressure will be to
let the *Arabic* manual carry detail the English one omits — or to pad the English with invented
Arabic-to-English translations for the 19 screens that have no English resource.

Neither is acceptable. The rule that follows from the measurements:

- Where **both** resources exist (the large majority), both manuals quote the interface exactly.
- Where **one is missing**, both manuals describe the same function to the same depth, and the
  edition whose interface text is missing **quotes what the screen really shows and flags it**.
- Coverage parity is measured in **functions documented**, never in word count.
