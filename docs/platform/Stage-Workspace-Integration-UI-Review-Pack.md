# Stage-Workspace-Integration — UI Review Pack (Phase 7)

**Product:** CrossBusiness Workspace · **Tab:** TAB 3
**Purpose:** everything a reviewer needs to open, look at and judge the Workspace — without reading code.

---

## 1. Exact routes to review

Sign in first: the shell is `[SessionValidation]`-guarded and an unauthenticated hit is bounced by the shared
filter.

| # | Screen | URL | What to look at |
| --- | --- | --- | --- |
| 1 | Dashboard | `/Workspace` | metrics row, Quick Actions, My Work, Agenda, Recent Activity, and the right-hand attention rail |
| 2 | Agenda | `/Workspace/Agenda` | day grouping, all-day vs timed rows, overdue/completed treatment |
| 3 | Agenda — today | `/Workspace/Agenda?days=1` | range filter as a link |
| 4 | Agenda — month | `/Workspace/Agenda?days=30` | sticky day headers while scrolling |
| 5 | Reports | `/Workspace/Reports` | kind headings, stale chip |
| 6 | Notifications | `/Workspace/Notifications` | unread tint + "new" chip |
| 7 | Notifications — unread | `/Workspace/Notifications?unreadOnly=true` | filter as a link, URL-driven |
| 8 | **Mentions** | `/Workspace/Mentions` | **the not-activated state** — the most important panel to review |

### 1.1 In-page anchors

`/Workspace/Index#my-work` · `#agenda` · `#reports` · `#favorites` · `#activity` · `#quick-actions`

---

## 2. Capture instructions

No screenshots are embedded here: this tab has no browser and fabricating an image of a screen it never
rendered would be worse than none. Reproduce them like this.

### 2.1 Browser

1. Run the app (`dotnet run`, or F5 in Visual Studio).
2. Sign in as an employee **with a resolved company** — the shell shows a "Session not resolved" prompt
   otherwise, and that is itself worth one capture.
3. Visit each URL in §1.
4. For each, capture **three widths**: `1440`, `992`, `390` (see §6).
5. Repeat in **Arabic** and **English** — the language switch also flips direction.

### 2.2 DevTools full-page capture (Chrome/Edge)

```
F12 → Ctrl+Shift+P → "Capture full size screenshot"
```

Device-toolbar widths: 1440 · 1200 · 992 · 768 · 390.

### 2.3 Dark theme

The Workspace follows Metronic's own switch. Toggle it, or force it:

```js
document.documentElement.setAttribute('data-bs-theme', 'dark');
```

Grounds lift and the action blue lightens to `#4b86e8` to stay legible — both worth one capture.

### 2.4 Forcing each panel state

The five states are the substance of this review, and most are hard to reach with real data:

| State | How to reach it |
| --- | --- |
| **Mentions — not activated** | default today; just open `/Workspace/Mentions` |
| Notifications — empty | sign in as an employee with no notifications |
| Notifications — data | any employee with rows in `Notifications` |
| Agenda — empty | pick a date range with no tasks or events |
| Agenda — partial | temporarily throw inside one of TAB 4's agenda sources |
| Reports — unavailable | comment out `AddCrossBusinessReporting(...)` in `Program.cs` locally |
| Access denied | sign in with a session that has no employee id |

---

## 3. New views and components

### 3.1 Views added this increment

| View | Purpose |
| --- | --- |
| `Views/Workspace/Agenda.cshtml` | full agenda screen |
| `Views/Workspace/Reports.cshtml` | report shortcuts, grouped by kind |

### 3.2 Views changed this increment

| View | Change |
| --- | --- |
| `Views/Workspace/Index.cshtml` | Agenda + Reports panels; capabilities published to the shell; unresolved-session prompt |
| `Views/Shared/_LayoutWorkspace.cshtml` | capability-aware navigation |
| `Views/Shared/_WorkspacePanelState.cshtml` | rewritten: 2 states → **5** |
| `Views/Workspace/Notifications.cshtml` · `Mentions.cshtml` | unchanged markup; now receive richer states |

### 3.3 CSS components (`crossbusiness-workspace.css`, all scoped under `.cbw`)

| Class | Component |
| --- | --- |
| `.cbw-sidebar` `.cbw-brand` `.cbw-nav-*` | rail: ground, brand mark, sections, links, badges |
| `.cbw-header` `.cbw-search` `.cbw-iconbtn` `.cbw-dot` | header, search, icon actions, count dot |
| `.cbw-card` `.cbw-card-head/-body/-foot` | panel container |
| `.cbw-metric` (+ `.is-ok/.is-warn/.is-critical`) | metric tile with state stripe |
| `.cbw-list` `.cbw-row` `.cbw-row-icon/-main/-title/-meta/-time` | list rows |
| `.cbw-chip` (+ tones) | status chips |
| `.cbw-actions` `.cbw-action` | quick actions grid |
| **`.cbw-state`** + `-unavailable` `-denied` `-failure` `-partial` | **the five states — new** |
| `.cbw-empty` | quiet empty state |
| **`.cbw-agenda-day` `.cbw-agenda-time` `.is-completed`** | **agenda — new** |
| `.cbw-grid.is-2col/.is-3col` `.cbw-stack` | layout |

---

## 4. Unavailable panels and why

What a reviewer will actually see **in the current environment**:

| Panel | State today | Why |
| --- | --- | --- |
| **Mentions** | **Unavailable** | `AddCommunicationPlatform` is not registered. Activation is the Communication tab's gated decision, not a side effect of this screen |
| Agenda | depends | needs TAB 4's `IWorkspaceAgendaService` (registered) **and** an employee id |
| My Work | depends | needs `ITaskService` **and** an employee id |

Everything else renders with data when data exists.

**Mentions is the only panel that is unavailable by design today.**

### 4.1 Reports is now fed by two sources — check for this

The Reporting tab registered its own `ReportingWorkspaceSource` during this increment, so `/Workspace/Reports`
now shows **Favourite, Recent, Saved and Shortcut** rather than two kinds. A reviewer should confirm all four
headings can appear, and that a **partial** banner (not an error) is what shows if one source fails.

---

## 5. Responsive breakpoints

| Width | Behaviour |
| --- | --- |
| **≥ 1400** | dashboard rail beside a 1.65fr work column; 3-column grids where used |
| **1200–1399** | same two-column dashboard; metric tiles reflow |
| **992–1199** | sidebar still fixed; work column narrows |
| **< 992 (lg)** | **sidebar becomes an off-canvas drawer**; dashboard collapses to one column |
| **< 768 (md)** | header search hidden; `.cbw-main` padding drops to `1rem` |
| **< 576 (sm)** | identity text hidden, avatar kept |

Metric tiles (`minmax(11rem, 1fr)`), quick actions (`minmax(9.5rem, 1fr)`) and report/agenda rows are
`auto-fit` grids or flex rows — they reflow **without** breakpoints.

Drawer direction follows `dir`: `start` in LTR, `end` in RTL.

---

## 6. UI review checklist

### Layout & responsive
- [ ] 1440 / 992 / 390 — no horizontal page scroll at any width
- [ ] Sidebar becomes a drawer below 992 and the toggle opens it
- [ ] Long titles ellipsize instead of pushing a row wide
- [ ] Metric tiles reflow without a broken row

### RTL / localization
- [ ] Arabic: `dir="rtl"`, sidebar on the right, drawer opens from the right
- [ ] English: mirrored, and **one stylesheet** serves both
- [ ] No untranslated string on any screen
- [ ] Numerals align in metric tiles (`tabular-nums`)

### The five states — the core of this review
- [ ] `/Workspace/Mentions` shows **Unavailable**, not "no mentions"
- [ ] The unavailable message names `AddCommunicationPlatform` **and** the SQL slice
- [ ] Empty and unavailable are **visibly different** (grey vs accent stripe)
- [ ] Access-denied is visibly different from unavailable (neutral vs accent)
- [ ] A partial banner appears **above** rows, never instead of them

### Navigation
- [ ] Rail shows only what this deployment can serve
- [ ] Mentions entry is **absent** while Communication is off, yet `/Workspace/Mentions` still opens
- [ ] Active item is highlighted; fragment entries never highlight
- [ ] No Security Console / Report Studio / CRM / Construction / AI / Mobile anywhere

### Agenda
- [ ] Day headers read **Today** / **Tomorrow**, then dates
- [ ] All-day rows say "All day"; timed rows show `HH:mm`
- [ ] Overdue chip red; completed struck through
- [ ] Type and source-module chips present on every row
- [ ] Clicking a row lands on the owning module's screen

### Theme
- [ ] Dark mode legible; action blue lightens
- [ ] Workspace is **blue**; Accounting/Inventory/POS remain **green** (deliberate — see R1–R3 notes §3)

### Accessibility
- [ ] Keyboard focus visible on every link, button and input
- [ ] Icon-only controls have `aria-label`
- [ ] `prefers-reduced-motion` honoured
- [ ] Headings descend in order (`h1` → `h2`)

---

## 7. Out of scope for this review

No legacy Accounting, Inventory or POS screen was touched, and none should be reviewed here. The only shared
files this increment modified are `Program.cs` (one registration line, added in the previous increment) — no
shared layout, no shared stylesheet, no shared route.
