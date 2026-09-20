# 02 — Product and business identity

What the product calls itself, what it promises, and where those two things are said
inconsistently.

---

## 1. The wordmark

**CrossBuy.** One word, two capitals, no space. It is the wordmark on every logo asset, the
suffix of most page titles, the JWT issuer (`Jwt:Issuer = "CrossBuy"`), the sender name on
outbound mail (`Smtp:FromName = "CrossBuy"`), the database name (`CrossBuyDB2` on the production
server, `CrossBuyDev` locally) and the solution name.

There is no registered legal entity, company address, VAT number or copyright holder anywhere in
the configuration or the source. **This product does not state who publishes it.** For a business
system sold to companies that is a gap worth closing deliberately rather than by accident.

## 2. The positioning line, in three languages

Held as resource strings in `Resources/SharedResources.*.resx` and rendered on the sign-in screen.
All four lines are translated in all three cultures — this is the most carefully localised copy in
the product.

| Key | English | العربية | Français |
|---|---|---|---|
| `BrandTagline` | THE CROSS-BUSINESS PLATFORM | المنصّة التي تربط أعمالك | LA PLATEFORME INTER-MÉTIERS |
| `BrandHeadlineLead` | One Platform. | .منصّة واحدة | Une plateforme. |
| `BrandHeadlineAccent` | Endless Possibilities. | .إمكانات بلا حدود | Des possibilités infinies. |
| `BrandSubLine1` | Connect your people, processes and data. | .اربط فريقك وإجراءاتك وبياناتك | Reliez vos équipes, vos processus et vos données. |
| `BrandSubLine2` | Build a smarter tomorrow. | .وابنِ غدًا أكثر ذكاءً | Construisez un avenir plus intelligent. |
| `BrandFootline` | BUSINESS WITHOUT BOUNDARIES | أعمال بلا حدود | DES AFFAIRES SANS FRONTIÈRES |

**The Arabic is not a translation of the English.** `THE CROSS-BUSINESS PLATFORM` became
«المنصّة التي تربط أعمالك» — *"the platform that connects your businesses"*. The English names a
category; the Arabic makes a claim about what it does for you. The French took a third route
(`INTER-MÉTIERS`, "cross-trade"). Whether that divergence is intentional positioning or drift is a
question for the owner, but it is deliberate work, not machine output.

> **Evidence:** repository. The six keys above read from all four `SharedResources` files. The
> English line was confirmed at runtime on the sign-in screen (screenshot `01-login.png`).

## 3. What the sign-in screen promises

Measured from the running page (`screenshots/01-login.png`, body text captured in
`capture-log.json`):

> THE CROSS-BUSINESS PLATFORM
> **One Platform. Endless Possibilities.**
> Connect your people, processes and data. Build a smarter tomorrow.
>
> `Accounting · Inventory · CRM · POS · Manufacturing · Projects · HR · Reports`
>
> BUSINESS WITHOUT BOUNDARIES

Eight named capabilities. The sign-in screen is the product's only pitch, and it is an accurate
one — every chip corresponds to a module that exists and is reachable (document 03).

## 4. The first thing a user sees after signing in

**Runtime.** Signing in as `admin` lands on **`/Portal/Choose`**, not on a dashboard.

`Portal/Choose.cshtml` is a destination chooser: *"Choose the portal that fits your needs today."*
It offers six primary portals, six secondary systems, and a hub link:

| Portal | Goes to | Described as |
|---|---|---|
| Administrative system | `Service/Index` | Companies, employees, roles & data |
| Inventory | `Inventory/Index` | Items, stock & movements |
| Employees portal | `People/Dashboard` | Profile, leaves, payslips & alerts |
| Accounting | `Accounting/Index` | Accounts, journals & reports |
| Reports & Analytics | `Accounting/Executive` | Comprehensive reports & analytics for better decisions |
| Technical support | `Crm/Tickets` | Reach our support & assistance team |

with Manufacturing, CRM, Tasks, Calendar, Projects & Contracting and Hypermarket as secondary
tiles. Manufacturing is the only one the screen labels as incomplete: *"Work orders & production
tracking **(WIP)**"*.

**This is the product's own self-description, written for its own users.** It is a better
statement of what CrossBuy is than any summary: *an administrative and financial platform with an
inventory spine, a people portal, and a support desk — plus five more systems.*

---

## 5. The naming problem, measured

The wordmark is consistent. **The product name in the browser tab is not.** Twenty screens were
loaded and their `<title>` recorded. Seven distinct product names appeared.

| What the tab says | Screens observed | Source |
|---|---|---|
| `Inventory System - CrossBuy` | Workspace, Inventory ×2, Tasks, Reports Center, Report Studio, Calendar | `_LayoutInventory` → `PageTitle` resource |
| `Accounting System - CrossBuy` | Accounting, Trial Balance, Chart of Accounts, CRM, CRM Pipeline | `_LayoutAccounting` → `PageTitle` resource |
| `CrossBuy Admin` | HR, Projects, Restaurant, Hyper, Chat | `_LayoutBackend` → `PageTitle` resource |
| `Warehouse System - CrossBuy` | Manufacturing dashboard | `_LayoutManufacturing`, hardcoded |
| `Sign in — CrossBuy` | Sign-in | `Account/Login` |
| `Choose destination - CrossBuy` | Portal chooser | `Portal/Choose` |
| `CrossBuy` | — | `_LayoutEmbed`, hardcoded |
| `· CrossBuy POS` | — | `_LayoutPos`, `_LayoutPosApp` |
| `· CrossBuy Hyper` | — | `_LayoutHyperPos` |

### The cause is structural, not cosmetic

Three of the eleven layouts set the title to `@Localizer["PageTitle"]` — **a constant string held
in that layout's own resource file.** It does not read `ViewData["Title"]`, so the screen cannot
name itself:

```razor
_LayoutInventory.cshtml    <title>@Localizer["PageTitle"]</title>
_LayoutAccounting.cshtml   <title>@Localizer["PageTitle"]</title>
_LayoutBackend.cshtml      <title>@Localizer["PageTitle"]</title>
```

```
Views/Shared/_LayoutInventory.en.resx   PageTitle = "Inventory System - CrossBuy"
Views/Shared/_LayoutAccounting.en.resx  PageTitle = "Accounting System - CrossBuy"
Views/Shared/_LayoutBackend.en.resx     PageTitle = "CrossBuy Admin"
```

Every screen that borrows a layout inherits that layout's identity. Traced:

| Screen | Borrows | So the tab says |
|---|---|---|
| `Workspace/Index` | `_LayoutInventory` | Inventory System |
| `Tasks/Index` | `_LayoutInventory` | Inventory System |
| `Reports/Index`, `Reports/Studio` | `_LayoutInventory` | Inventory System |
| `Calendar/Index` | `_LayoutInventory` | Inventory System |
| `Crm/Index`, `Crm/Pipeline` | `_LayoutAccounting` | Accounting System |
| `Chat`, `Project/Dashboard`, `Pos/Dashboard`, `Hyper/Dashboard` | `_LayoutBackend` | CrossBuy Admin |

The Workspace — the platform's own cross-module home, the first item in the Platform menu —
identifies itself to the browser, the bookmark bar and the window switcher as *Inventory System*.
So does the Reports Center. So does the Calendar.

The `PageTitle` strings are correctly translated into all three cultures, which is the tell: the
titles were localised with care and never audited for whether they were *true*.

### A note on "CrossBusiness"

The string `CrossBusiness` appears in 22 source files. It is the name used throughout the
engineering comments for the platform layer — *"CrossBusiness Platform"*, *"CrossBusiness
Workspace"*, `AddCrossBusinessReporting()`, `AddCrossBusinessWorkspace()`, and the export engine
names `CrossBusiness.Csv`, `CrossBusiness.Xlsx(ClosedXML)`, `CrossBusiness.Html`,
`CrossBusiness.Pdf(…)`.

Exactly one *user-visible* surface carries it:

```razor
_LayoutWorkspace.cshtml   <title>… · CrossBusiness</title>
```

**That layout is referenced by zero views.** It does not ship. `CrossBusiness` therefore never
reaches a browser tab today — but it is the name the platform calls itself internally, and it is
one merge away from the UI. Worth an explicit decision rather than leaving two names alive.

---

## 6. Who the product is for

Inferred from what it actually contains, not from marketing copy:

- **Multi-company, multi-branch, multi-warehouse.** Company and branch are first-class throughout;
  the platform enforces a company boundary in data access rather than trusting the caller.
- **Arabic-first, English and French supported.** Arabic has the most complete resource coverage
  (308 translation units vs. 293 French, 289 English) and the product renders right-to-left with a
  dedicated RTL stylesheet bundle. See 10.
- **Regionally specific.** Egyptian e-invoicing (`Accounting/EtaStatus`), VAT returns, payroll tax
  and social insurance, leave encashment and end-of-service settlement.
- **Several industries from one codebase.** General trade, manufacturing, restaurants
  (`PosApp` — cashier, kitchen display, delivery board), supermarket/hypermarket (`Hyper`), and
  construction & contracting (`Project` — advances, retention, subcontractor retention, variation
  orders).

That last point is the real positioning. «المنصّة التي تربط أعمالك» is not decoration: the product
genuinely runs a restaurant, a factory and a contracting firm out of one chart of accounts. That
is the differentiator, and the sign-in screen's eight chips undersell it — *Restaurant*,
*Hypermarket* and *Construction* are not among them.
