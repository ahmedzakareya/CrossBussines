# Stage — New Screens: Navigation Evidence

**A "Platform" category, defined once, rendered on every module layout, permission-aware, localized, with no duplicates.**

---

## 1. The problem

Before this increment, **nothing anywhere linked to the new screens**. A grep across all 327 views for a link to `/Workspace` or `/Reports` matched only the Reporting views' own internal links. `_LayoutWorkspace.cshtml` contained **zero** navigation links.

The only way to reach a delivered screen was to type its URL - and the obvious URL returned 404.

## 2. Where the entries live, and why

The menus are strictly per-module (`MainMenu.Inventory()`, `.Accounting()`, `.Tasks()`, `.Admin()`, …), and `_MainMenu.cshtml` renders whichever list a layout passes it.

Workspace, Reports Center and the Business Event Monitor are **not module screens** - they span every module. Putting them in one module's menu would leave them unreachable from the others; putting them in all eight would duplicate them eight times.

So a single `MainMenu.Platform()` category is **prepended once inside `_MainMenu.cshtml`**:

```csharp
var menu = MainMenu.Platform().Concat(Model ?? new List<MenuCategory>()).ToList();
```

One definition in the codebase, present on every layout that renders a sidebar.

## 3. No duplicates

The Business Event Monitor previously sat in `MainMenu.Admin()`, reachable only from the backend layout. It was **moved**, not copied. `Admin()` now carries a comment in its place so it is not re-added.

Verified in the rendered HTML of a module page - each link appears **exactly once**:

```
1  href="/BusinessEventMonitor"
1  href="/Reports"
1  href="/Workspace"
```

A test asserts this permanently: `A_navigation_entry_appears_exactly_once_across_every_menu` sweeps **every** static menu method and fails if any of the three appears twice.

## 4. Permission-aware, using the real predicate

```csharp
bool canPlatformOps = CrossBuy.Models.PlatformOpsAttribute.IsAdmin(Context) || canAccManage;
bool ItemVisible(MenuItem i) => i.Perm switch {
    "manage" => canManage, "acc-manage" => canAccManage, "crm-manage" => canCrmManage,
    "platform-ops" => canPlatformOps, _ => true };
```

This is the **same** predicate `[PlatformOps]` applies - `IsAdmin(http) || IAccountingAccessService.CanAsync("manage")` - not a proxy for it. A nav predicate that drifts from its filter eventually produces both failure modes: links that 403, and hidden screens the user was entitled to.

Workspace and Reports Center carry **no** `Perm`, because their controllers carry only `[SessionValidation]`. A test pins both facts.

## 5. Localized, both directions

Rendered from a live application:

**Arabic** (default): `المنصّة` · `مساحة العمل` · `مركز التقارير` · `مراقب أحداث المنصّة`

**English** (`?culture=en-US&ui-culture=en-US`): `Platform` · `Workspace` · `Reports center` · `Business event monitor`

Three new keys were added to `SharedResources.ar.resx` - `Platform`, `Workspace`, `Reports center`. `Business event monitor` already existed. A test asserts all four resolve, because a missing key silently renders English text inside an Arabic RTL layout, which reads to the owner as a bug rather than as a missing translation.

## 6. Rendered on every layout

Confirmed by fetching two different module pages and grepping the rendered HTML:

| Page | Layout | Platform links present |
|---|---|---|
| `/Inventory/Index` | `_LayoutInventory` | Workspace, Reports, BusinessEventMonitor |
| `/Accounting/Index` | `_LayoutAccounting` | Workspace, Reports, BusinessEventMonitor |

## 7. Nothing unrelated was touched

Existing module report links are intact and unchanged - `/Inventory/Reports` and `/Crm/Reports` still render exactly as before, alongside the new `/Reports` platform link. No other module's navigation was modified, and no future or unfinished screen was added to the menu.

`Soon` placeholders were not used: all three entries are delivered screens, and a test asserts `Soon == false` for each.
