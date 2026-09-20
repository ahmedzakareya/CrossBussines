# 10 — Localisation and bilingual behaviour

Three languages, two directions. Measured, not assumed.

---

## 1. What is configured

```csharp
// Program.cs
var enCulture = new CultureInfo("en");                 // unchanged (already Latin / ".")
var arCulture = FixNumbers(new CultureInfo("ar"));     // mutable instance — numbers normalized,
                                                       // dates/RTL kept
var frCulture = new CultureInfo("fr");                 // unchanged
options.SupportedCultures   = appCultures;
options.SupportedUICultures = appCultures;
```

Three cultures: **`ar`, `en`, `fr`.** Switched at `/Account/SetLanguage?culture=…&returnUrl=…`,
offered from five layouts.

**The Arabic culture is a mutated instance.** `FixNumbers()` normalises Arabic number formatting
to Latin digits and a `.` decimal separator, while keeping Arabic dates and RTL. This is a
deliberate business decision — an Egyptian or Gulf accountant reads `1,234.56`, not `١٢٣٤٫٥٦` —
and it is the kind of choice that only comes from having shipped to real users.

## 2. The scale of the translation estate

```
891 .resx files over 308 translation units

   Arabic    308 units   ← complete
   French    293 units
   English   289 units
   neutral     1 unit    (SharedResources.resx)
```

`SharedResources` carries 979 neutral / 1,270 Arabic / 993 English / 1,087 French keys. The rest
are **per-screen**: `Resources/Views/<Folder>/<View>.<culture>.resx`.

Per-screen resource files at this density are unusual and are the reason the product translates
as well as it does. A flat listing of `Resources/` finds only four files and badly understates
it — the real estate is 891.

### Culture coverage per unit

| Cultures held | Units |
|---:|---:|
| 4 (incl. neutral) | 1 |
| 3 | 287 |
| 2 | 6 |
| 1 | 14 |

## 3. Arabic is the complete language

**Zero units are missing Arabic.** Every one of the 308 screens has an `.ar.resx`.

That, plus Arabic holding the most `SharedResources` keys (1,270 against English's 993), plus the
RTL stylesheet being a first-class bundle rather than an override — CrossBuy is an **Arabic-first
product with English and French translations**, not an English product that was localised.

### Screens with no English resource — 19

```
Accounting/MatchReceipts          Comm/Compose            Pos/Dashboard
Accounting/SalesInvoicePrint      Comm/Index              Project/Dashboard
Admin/Index      (HR Dashboard)   Comm/View               Shared/_EntityConversation
Admin/_HrDocGallery               FileManager/Index       Shared/_NotificationBell
Announcements/Index               Notifications/Index     Store/Category
Approvals/Index                   Calendar/Index          Store/Product
Chat/Index
```

### Screens with no French resource — 15

```
Accounting/MatchReceipts          Comm/Compose            Project/Dashboard
Accounting/SalesInvoicePrint      Comm/View               Shared/_NotificationBell
Admin/Index                       Notifications/Index     Store/Category
Admin/_HrDocGallery               Pos/Dashboard           Store/Product
Approvals/Index                   Chat/Index              Workspace/Index
```

**Twelve screens are missing both.** They are not marginal: the HR Dashboard, Chat, Notifications,
Approvals, the whole Email module, the POS dashboard, the Projects dashboard, the public store's
product and category pages, and two shared partials that render on *every* screen
(`_NotificationBell`, `_EntityConversation`).

The consequence is not a crash. `IViewLocalizer` falls back to the key, and the keys here are
written as English phrases — so an English user sees correct English by accident, and **a French
user sees English**. The two shared partials mean the notification bell and the comment box are
English on every French screen in the product.

## 4. View text is disciplined; data is not

Measured across all 372 views:

| | Count |
|---|---:|
| Localizer keys written in Arabic (untranslatable by construction) | **0** |
| Views containing hardcoded Arabic text | 10 |
| Hardcoded Arabic occurrences | 25 |

Nine of those ten are legitimate — five layouts carrying «العربية» in the language switcher, plus
two POS sign-in screens and two specialist screens. **This is a well-disciplined view layer.**

The leak is in the **data**, and the product has a rule for it:

> **Every displayed field has a twin column.** Both are captured on input; `DisplayName.Of` picks
> by culture at render.

Measured in `Models/`: **56 twin-column pairs** — `Name`/`NameEn` ×21, `Title`/`TitleEn` ×8,
`Description`/`DescriptionEn` ×7, plus `FullName`, `Subject`, `Label`, `BankName`, `TradeName`,
`NationalityName`, `H_Name`, `ItemDescription` and others.

The rule is real and widely applied. Where it fails, it fails at **input**, not at render: if the
English twin was never typed, an English screen shows the Arabic value. That is what an Arabic
employee name on an English profile page is — a capture gap, not a translation gap. Any screen
that writes a display field must make the twin a first-class input, not an optional extra.

## 5. RTL, verified at runtime

The Arabic pass switched culture and re-measured four screens:

| Screen | `dir` | `lang` | Cairo loaded |
|---|---|---|---|
| `/Inventory/Index` | **rtl** | ar | ✓ |
| `/Accounting/TrialBalance` | **rtl** | ar | ✓ |
| `/Admin/Index` | **rtl** | ar | ✓ |
| `/Account/Login` | **rtl** | ar | ✓ |

Stylesheet served: **`style.bundle.rtl.css`** — the whole bundle swaps, rather than direction
being patched on top. Font families actually loaded: `Cairo`, `Inter`, `keenicons-outline`.

Screenshots: `04-inventory-dash-ar.png`, `07-trial-balance-ar.png`, `11-admin-hr-ar.png`,
`01-login-ar.png`.

> **Worth stating plainly, because it is the easiest thing to get wrong:** the default culture
> served to a fresh session was **English, LTR**. Every screen in the first capture pass reported
> `lang="en" dir="ltr"` without any culture being set. For an Arabic-first product whose most
> complete language is Arabic, the out-of-the-box language being English is a decision that should
> be made deliberately rather than inherited from the framework default.

## 6. Bidi, numbers and time — the rules this product follows

Collected from the codebase's own conventions:

- **Fractions and Latin names inside Arabic text must be bidi-isolated**, or the digits and the
  surrounding words reorder on screen. A ratio like `1.5` inside an Arabic sentence is the classic
  failure.
- **Relative times are assembled, not formatted** — "منذ ٣ أيام" needs different word order from
  "3 days ago", so the phrase is built per language rather than produced by one format string.
- **A timestamp with no zone is UTC.** Rendering it as local time without conversion is how a
  document appears to have been posted tomorrow.
- **Arabic numerals are normalised to Latin digits** by the mutated culture (§1) — so a number is
  the same glyph in all three languages, and only the direction around it changes.

## 7. Findings

| | |
|---|---|
| **Strength** | 891 resource files over 308 screens, with **complete Arabic coverage**. This is a genuinely multilingual product, not an English one with a dictionary. |
| **Strength** | Zero Arabic localizer keys and only 25 hardcoded Arabic literals in 372 views. The view layer is clean. |
| **Strength** | The Arabic culture is mutated for Latin digits — a real-user decision, not a default. |
| **Strength** | RTL verified working, with a dedicated bundle and Cairo confirmed loaded. |
| **Gap** | 19 screens have no English resource, 15 no French, **12 neither** — including two shared partials that appear on every page, so the notification bell and comment box are English throughout the French UI. |
| **Gap** | The twin-column rule is applied in 56 places and enforced nowhere. A missing English twin surfaces as Arabic text on an English screen. |
| **Decision** | The default culture for a new session is English. For an Arabic-first product, that deserves an explicit choice. |
