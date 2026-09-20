# 04 — Complete screen inventory

Every user-facing page in the product, with the exact Arabic and English text a manual must quote.

**Machine-readable: `screen-catalog.json` — 309 entries, one per screen.** This document explains
what is in it, what the numbers mean, and which screens need special handling.

---

## 1. What was inventoried

372 Razor view files, resolved into:

| | Count |
|---|---:|
| **User-facing screens** | **309** |
| Shared partials and view components | 63 |
| Screens with a table | 219 |
| Screens with a form | 190 |
| Screens that mutate through AJAX rather than a form post | 49 |
| Screens containing a modal dialog | 32 |
| Table columns catalogued | 1,399 |
| Form fields catalogued | 1,305 |
| Actions (buttons / button-links) catalogued | 1,475 |
| Modal dialogs | 94 |
| In-page notices (alerts) | 210 |
| Empty / loading states | 24 |

## 2. What each catalogue entry holds

```jsonc
{
  "screen_id": "SCR-INVENTORY-Items",           // stable, unique across all 309
  "module_folder": "Inventory",
  "view_name": "Items",
  "route": "/Inventory/Items",
  "source_view": "CrossBuy/Views/Inventory/Items.cshtml",
  "controller": "Inventory",
  "controller_action": "Items",
  "action_verbs": ["HttpGet"],
  "action_attributes": [],                       // per-action guards
  "class_attributes": ["SessionValidation"],     // controller-wide guards
  "controller_source": "CrossBuy/Controllers/InventoryController.cs:234",
  "rendered_by_actions": null,                   // set when the view name ≠ the action name
  "layout": "~/Views/Shared/_LayoutInventory.cshtml",
  "heading":   { "key": "Items", "ar": "الأصناف", "en": "Items", "fr": "Articles",
                 "ar_source": "shared", "en_source": "shared" },
  "breadcrumb": [ … ],
  "columns":   [ { "label": {ar,en,fr}, "align": "start|end" }, … ],
  "fields":    [ { "id", "control", "type", "label": {ar,en,fr}, "placeholder",
                   "required", "readonly", "disabled", "min", "max", "step",
                   "maxlength", "lookup", "model_binding" }, … ],
  "actions":   [ { "label": {ar,en,fr}, "element", "variant", "href", "id",
                   "opens_modal", "submits", "confirm" }, … ],
  "notices":   [ { "kind": "warning|danger|success|info", "message": {ar,en,fr} }, … ],
  "empty_states": [ … ],
  "modals": ["kt_modal_add_item", …],
  "partials": [ … ], "view_components": [ … ],
  "has_form": true, "is_ajax_driven": true, "lines": 143
}
```

Every bilingual label carries a **`_source`** field, which is the honest part: it says whether the
text came from the screen's own resource file, from the shared resources, from a literal typed in
the view, from an inline culture test, or whether **no translation exists and the key itself is
what the user reads.**

---

## 3. Title provenance — the manual must not trust every label equally

| How the screen heading is produced | Screens |
|---|---:|
| Per-screen resource file (`Resources/Views/<Folder>/<View>.<culture>.resx`) | **209** |
| Shared resources | 27 |
| **No Arabic entry — the key text is shown** | **18** |
| Literal text typed in the view (identical in every language) | 14 |
| Inline culture test — `@(isAr ? "…" : "…")` | 10 |
| **No heading element found at all** | **31** |

Two consequences for the manual writer:

1. **The 18 fallback screens** show English key text to an Arabic user. These are listed in
   `bilingual-glossary.csv` with `translation_status = MISSING arabic`. The Arabic manual must
   either quote what is really on screen (English) or the screen must be fixed first — it cannot
   quietly print an Arabic title the user will not find.
2. **The 31 screens with no heading** need their names taken from the navigation label instead.
   Most are dialogs, print views, or operator screens (POS lane, kitchen) where the whole viewport
   is the interface.

---

## 4. Shell and navigation

Eleven layouts serve the 309 screens:

| Layout | Screens | Sidebar it renders |
|---|---:|---|
| `_LayoutBackend` | 94 | Admin / HR menu |
| `_LayoutAccounting` | 87 | Accounting menu (including all CRM screens) |
| `_LayoutInventory` | 81 | Inventory menu (including Workspace, Tasks, Reports, Calendar) |
| `_LayoutPeople` | 11 | Employee self-service |
| `_LayoutManufacturing` | 9 | Manufacturing menu |
| `_LayoutHyperPos` | 5 | Supermarket lane |
| `_Layout`, `_mainLayout` | 8 | generic |
| `_LayoutPos`, `_LayoutPosApp` | 2 | Restaurant operator |
| *(none declared)* | 12 | print views and full-screen operator surfaces |

> **A manual-relevant warning.** The browser tab title comes from the *layout*, not the screen.
> Three layouts set it to a constant, so `/Workspace`, `/Tasks`, `/Reports` and `/Calendar` all
> display **"Inventory System - CrossBuy"**, and every CRM screen displays **"Accounting System -
> CrossBuy"**. If the manual tells a reader to "look for X in the title bar", it will be wrong.
> Navigate by the page heading and breadcrumb instead.

---

## 5. Screens that need special handling

### 5.1 Eight documents share one screen

`/Inventory/DocumentDetails` is rendered by **eight** different actions:

```
Inventory.CountDetails      Inventory.DeliveryDetails    Inventory.LandedCostDetails
Inventory.PurchaseOrderDetails   Inventory.ReceiptDetails
Inventory.SalesOrderDetails      Inventory.TransferDetails    Inventory.WriteOffDetails
```

Its heading is taken from the model
(`@(isAr ? Model.Title : … Model.TitleEn …)`), so the same screen presents itself as eight
different documents. **Write it once in the manual and cross-reference it eight times** — do not
write eight chapters, and do not claim eight screens.

### 5.2 Views whose file name is not their route

Fifteen screens are opened by an action with a different name. All are resolved in the catalogue's
`rendered_by_actions`:

| View file | Opened by |
|---|---|
| `/Inventory/ItemForm` | `Inventory.CreateItem`, `Inventory.EditItem` |
| `/Inventory/CategoryForm` | `Inventory.CreateCategory`, `Inventory.EditCategory` |
| `/Accounting/PartyStatementPicker` | `Accounting.CustomerStatement`, `Accounting.VendorStatement` |
| `/Accounting/SalesInvoicePrint` | `Accounting.PrintInvoice`, `HyperPos.PrintInvoice` |
| `/Hyper/PosLogin`, `PosStart`, `PosLane`, `Receipts`, `Customer`, `PriceCheck` | `HyperPos.*` |
| `/Hyper/HyperSales`, `HyperItemMargin` | `Hyper.Sales`, `Hyper.ItemMargin` |
| `/ClientPortal/NoAccess` | `ClientPortal.Index` |

**The manual must cite the route a user can reach, not the file name.** Six of these
(`Views/Hyper/Pos*`) are rendered by `HyperPosController` using an absolute view path, so the
folder name is misleading too.

### 5.3 Framework screens, not product screens

`/Shared/Error` and two `/Shared/Default` views are framework surfaces with no route. The error
page is worth one illustration in the troubleshooting chapter; the others are not manual material.

### 5.4 Screens absent from the main menu

Eleven surfaces exist and render but appear in no module menu: the **Client Portal** (5 screens),
the **public Store** (2), the **People portal** (12, reachable from `/Portal/Choose`),
**Restaurant intelligence**, **Insight actions**, **Employee onboarding**, **Documents**, **Roster**
and **Brand**.

The People portal is a deliberate second audience — employees, not staff users. The Client Portal
and Store are for customers. **The manual needs a separate short chapter for each audience**, not
an entry in the staff navigation chapter.

### 5.5 Development-only and unfinished

| Surface | Status |
|---|---|
| `Api/DevSeedController`, `Api/UatSeedController` | **Development only** — seeding endpoints |
| Manufacturing (9 screens) | **Unfinished by the product's own label**: the portal chooser calls it *"Work orders & production tracking (WIP)"* |
| Scheduled report delivery | **Built but disabled** — worker off by design, `NullReportMailSender` registered |
| `_LayoutWorkspace.cshtml` | **Dead** — zero references |
| `Platform.BusinessEventLog` report | **Reachable only after a grant** — its permission key is deliberately unmapped, so the menu row is hidden and the route answers 404 |

None of these should be documented as available features. The Manufacturing chapter in particular
must carry the product's own "WIP" wording rather than describing an MRP the user cannot rely on.

---

## 6. Prerequisites the screens state themselves

210 in-page notices were catalogued, and many are prerequisites rather than errors. They are the
best source for a manual's "before you start" boxes because they are the product's own words. For
example, on `/Inventory/Items`:

> **Add at least one category and one unit before creating items.**
> (`alert-light-warning`, shown when either list is empty; the **Add Product** button is
> simultaneously disabled.)

Each notice in the catalogue carries its `kind` (warning / danger / success / info) and its text in
all three languages, so the manual can reproduce both the wording and the colour treatment.

---

## 7. What this inventory does **not** establish

| | |
|---|---|
| **That any action works** | 1,475 actions were read from markup. **None was clicked.** A button's presence is not proof of its effect. |
| **What a non-administrator sees** | Field- and action-level visibility is often decided server-side by permission. This inventory is the administrator's view of the markup. |
| **Runtime-only elements** | 49 screens build their content with JavaScript. Rows, dynamically added form fields and JS-built dialogs are **under-counted** here — the runtime capture partly compensates (document 08), the source scan cannot. |
| **Screens needing a record id** | Anything routed `/…/{id}` was only reachable where development data supplied one. |
| **Field validation rules** | Only what the markup declares (`required`, `min`, `max`, `maxlength`). Server-side rules live in the business layer — see document 05 and `messages-catalog.csv`. |
