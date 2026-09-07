# CrossBusiness Workspace — R1–R3 Delivery Notes

**Product:** CrossBusiness Workspace — the first user-facing product
**Owner tab:** TAB 3
**Verified:** 2026-08-06 11:12 — Debug **0 errors**, Release **0 errors**, **no exclusions**
**Status:** foundation ready for integration — **stopped after R3, as instructed**

---

## 1. What shipped

11 files, 2,309 lines.

| Release | Scope | Files |
| --- | --- | --- |
| **R1** | Shell, navigation, responsive layout, CrossBusiness Blue, Metronic integration | `_LayoutWorkspace.cshtml`, `crossbusiness-workspace.css`, `WorkspaceNavigation.cs`, `WorkspaceContracts.cs`, `WorkspaceRegistration.cs` |
| **R2** | Dashboard, My Work, Favorites, Recent Activity | `WorkspaceService.cs`, `Views/Workspace/Index.cshtml`, `_WorkspacePanelState.cshtml` |
| **R3** | Notifications panel, Mentions, Quick Actions | `Views/Workspace/Notifications.cshtml`, `Views/Workspace/Mentions.cshtml`, `WorkspaceController.cs` |

`Program.cs` gained **one line** (`AddCrossBusinessWorkspace()`), matching the Reporting block's convention — that
file is edited by several tabs at once and a multi-line block collides on every merge.

---

## 2. The defining constraint: the Workspace consumes, it never re-implements

The brief forbids duplicating Task, Calendar or Communication logic. That is enforced structurally rather than by
intention:

| Panel | Source | Registered? |
| --- | --- | --- |
| My Work | `ITaskService.GetTasksAsync` / `GetKpisAsync` | ✅ |
| Metrics | `ITaskService` KPIs | ✅ |
| Notifications | the platform's own `Notifications` table | ✅ (no read service exists — `INotificationService` is a writer only) |
| Mentions | `ICommMentionService.GetHistoryAsync` | ❌ **not registered** — see §4 |
| Favorites | `IReportLibraryService` via `IWorkspaceFavoritesSource` | ✅ |
| Recent Activity | `IWorkspaceActivitySource` | ✅ |

**The Workspace owns no table, no writer and no business rule.** It resolves the caller's `BusinessContext` once,
asks each source, and projects the answers.

### 2.1 Two extension points make this a foundation rather than a fixed page

```csharp
services.AddCrossBusinessWorkspace();
services.AddScoped<IWorkspaceFavoritesSource, MyModuleFavorites>();   // pinned customers, projects, …
services.AddScoped<IWorkspaceActivitySource, MyModuleActivity>();
```

A module contributes rows by registering — it never edits the Workspace. Each source is awaited in its own
`try/catch`, so one module's broken contribution cannot blank the panel for the others.

---

## 3. CrossBusiness Blue — the decision, and its deliberate limit

You asked for **CrossBusiness Blue**. That instruction resolves a contradiction CLAUDE.md had recorded as an open
owner decision (*"A6.2's 'preserve the CrossBuy blue identity' contradicts the code and needs an owner decision"*).

**The conflict is real:** `crossbuy-brand.css` deliberately replaces Metronic's blue primary with ledger green
`#13433a` + gold for the **whole application** — Accounting, Inventory, POS, Manufacturing, People — and is loaded
after the style bundle so it wins the cascade.

**What I did:** the Blue design system is scoped under `.cbw` and does **not** touch `:root`. It is loaded *after*
`crossbuy-brand.css` without overriding it.

**The consequence, stated plainly:** the Workspace is blue; every existing module screen stays green.

That is the conservative reading, and it is reversible in either direction:

* **Blue product-wide** → move the `.cbw` tokens into `:root` in `crossbuy-brand.css`. One file, one reviewed change.
* **Green Workspace** → delete the token block from `crossbusiness-workspace.css`; the shell inherits green.

Rolling blue into `:root` as a side effect of building one screen would have repainted the entire product without
anyone reviewing it. **That is the decision I need from you** — §7, item 1.

The palette is a considered scale rather than Metronic's stock `#009ef7` (a cyan that reads as "template"):
grounds `#0b1f3d`/`#102c55`, action `#1f5fd0`, surfaces `#e6effc`/`#f4f8fe`, attention accent `#e08c2e`, with
semantic colours (`ok #17845c`, `warn #a86a12`, `critical #c22a4d`) kept **separate** from the accent so a status
is never mistaken for a brand flourish.

---

## 4. Every panel has three states, not two

```
has data  ·  EMPTY (the service answered "nothing")  ·  UNAVAILABLE (the service could not answer)
```

Collapsing the last two is how an undeployed service looks like a quiet week. They render differently: empty is
quiet and grey; unavailable is accented and **states the reason**.

**Mentions is currently `UNAVAILABLE` by design.** `AddCommunicationPlatform` is not called in `Program.cs` — that
platform's activation is its own gated decision, made in the previous phase, and a screen must not force it as a
side effect. So `ICommMentionService` is resolved with `GetService` (nullable), not `GetRequiredService`, and the
panel says:

> The Communication Platform is not activated in this environment. Mentions appear once
> `AddCommunicationPlatform` is registered and `communication_platform_slice_001.sql` is applied.

**When Communication is activated, Mentions starts working with no change to the Workspace.**

The dashboard also renders a diagnostics strip listing any dark panels, so an operator sees the degraded state
rather than discovering it from an empty screen.

---

## 5. One thing was withdrawn, and why

R3 originally shipped a **mark-notification-read** POST. The repository's own Roslyn authorization analyzer
rejected it:

```
CBA001: Mutating endpoint 'WorkspaceController.MarkNotificationRead' has no authorization the analyzer
can see and is not listed in authorization-baseline.json. The baseline may only shrink — a new entry is
not an option.
```

The endpoint **was** safe: company *and* recipient were both in the `WHERE` clause, so a crafted id could not
touch another person's row. But that is authorization the analyzer cannot observe, and *"it is safe, trust me"* is
precisely what that rule exists to refuse.

Three options existed:

| Option | Verdict |
| --- | --- |
| Add to `authorization-baseline.json` | **Forbidden** — the baseline may only shrink |
| Borrow an unrelated module's access service (e.g. `ICommunicationAccessService`) to satisfy the analyzer | **Dishonest** — a notification is not a communication record |
| **Withdraw the write** | **Chosen** |

Withdrawing also restored the design's own coherence: the Workspace is documented as a read model that "owns no
writer", and the endpoint contradicted that. Unread is now shown as **state** (a "new" chip), not as an action.

**To restore mark-as-read**, the owner adds a legitimate authority to `AuthorizationSurface.AuthorityTypes` — the
same visible, deliberate process Stage 2A Batch A used when the analyzer blocked its three endpoints. That is
§7 item 2.

---

## 6. Layout, responsive behaviour and RTL

* **Metronic 8.2.3 `app-*` shell**, matching the other backend layouts, so the framework's own drawer/toggle
  JavaScript behaves identically here rather than being re-implemented.
* **Sidebar becomes an off-canvas drawer below `lg`** via `data-kt-drawer` — Metronic's own responsive mechanism.
* **Two-column dashboard** (work column + attention rail) collapses to one below `lg`; metric tiles and quick
  actions are `auto-fit` grids that reflow without breakpoints.
* **RTL from one stylesheet.** Logical properties throughout (`border-inline-start`, `margin-inline-end`,
  `text-align: start`) — the `dir` attribute flips it. Duplicating the sheet per direction is how RTL parity rots.
* **Bilingual at the source.** Every label is an Ar/En pair; Arabic is first in each pair because it is the
  product's primary language.
* Focus-visible outlines, `prefers-reduced-motion`, `aria-label`s on icon-only controls, and wide content that
  scrolls inside its own container so the page body never scrolls sideways.

---

## 7. Decisions I need from you

| # | Decision | Consequence today |
| --- | --- | --- |
| 1 | **Blue product-wide, or Workspace-only?** | Workspace is blue, every other screen is green. Both directions are a one-file change |
| 2 | **Add a Workspace authority to the analyzer surface?** | No mark-as-read; unread is display-only |
| 3 | **Activate the Communication Platform?** | Mentions renders "not activated" with its reason |
| 4 | Is the Workspace the **default landing page** after sign-in? | Not wired — no existing route was changed |
| 5 | Should global search be real? | The header input is presentational; the Workspace owns no search index and building one would duplicate a search capability |

---

## 8. Scope honoured

**Not implemented, as instructed:** Security Console, Report Studio, CRM, Construction, AI, Mobile. None appears
in navigation — not even as "soon", because they are out of scope for this product rather than merely unbuilt.

**Not duplicated:** Task, Calendar and Communication logic. Every fact comes from those services or is reported
unavailable.

**Not changed:** no existing controller, view, layout, route or stylesheet was modified. `crossbuy-brand.css` is
untouched. The only shared-file edit is the single `Program.cs` registration line plus its `using`.

**No hosted service, no new table, no SQL, no migration.**

---

## 9. Build state

| Target | Debug | Release |
| --- | --- | --- |
| `CrossBuy.csproj`, **no exclusions** | ✅ 0 errors | ✅ 0 errors |
| Workspace-attributable warnings | 0 | 0 |

**Note on the shared tree.** Two other tabs were writing during this build. `ReportsCenterApiController.cs`
(Reporting tab, new) had argument-type errors and 8 CBA001 violations of its own at 11:06 and was corrected by its
owner by 11:10. The final figures above are from **11:12 with nothing excluded**. No file belonging to another tab
was edited at any point.
