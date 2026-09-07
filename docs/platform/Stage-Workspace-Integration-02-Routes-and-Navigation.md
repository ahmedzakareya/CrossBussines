# Stage-Workspace-Integration — 02 · Routes and Navigation (Phase 2)

**Product:** CrossBusiness Workspace · **Tab:** TAB 3 · **Source:** `BL/Workspace/WorkspaceNavigation.cs`

---

## 1. The eight required sections

All eight are present. They are grouped into four rail sections because eight flat links is a list, not a
structure — and the grouping encodes something true: what is *mine*, what *needs me*, what I *reach for*, and
where I *leave to*.

| Required | Rail section | Destination |
| --- | --- | --- |
| **Home** | Workspace | `/Workspace` |
| **My Work** | Workspace | `/Workspace/Index#my-work` |
| **Agenda** | Workspace | `/Workspace/Agenda` |
| **Notifications** | Needs attention | `/Workspace/Notifications` |
| **Mentions** | Needs attention | `/Workspace/Mentions` |
| **Reports** | Quick access | `/Workspace/Reports` |
| **Favourites** | Quick access | `/Workspace/Index#favorites` |
| **Recent Activity** | Quick access | `/Workspace/Index#activity` |

A fifth section, **Modules**, links out to Accounting, Inventory and Tasks. Each target performs its own
authorization on arrival, so the Workspace neither duplicates nor pre-empts that decision.

---

## 2. Capability-aware, not permission-guessing

Navigation is built **from** `WorkspaceCapabilities`, resolved per request:

```csharp
Tasks         = ITaskService resolvable        && caller has an employee id
Calendar      = ICalendarService resolvable    && caller has an employee id
Agenda        = Tasks || Calendar                        // at least one contributing source
Reporting     = any IWorkspaceReportSource reports IsAvailable
Communication = ICommMentionService resolvable           // i.e. the platform is activated
Notifications = notification source available  && caller has an employee id
```

**Why capability and not a permission check.** Evaluating a module's permission here would be a second,
divergent copy of that module's rule — and the two would disagree the first time either changed. The Workspace
asks only whether the **contract resolves** and whether the **caller can be identified**; the module's own
screen still authorizes on arrival. Tasks additionally requires an employee id because every task query is
scoped to a person, and a session without one cannot show My Work at all.

### 2.1 What is hidden, and when

| Entry | Hidden when |
| --- | --- |
| My Work | `!Tasks` |
| Agenda | `!Agenda` (neither Tasks nor Calendar registered) |
| Notifications | `!Notifications` |
| **Mentions** | `!Communication` — i.e. whenever the platform is not activated |
| Reports | `!Reporting` |
| "Needs attention" section | when it would contain no items |

Home, Favourites and Recent Activity are always present: they are served by the Workspace itself.

---

## 3. Hidden ≠ empty — the distinction the brief asks for

These are three different things and the product expresses them three different ways:

| Situation | Expression | Why |
| --- | --- | --- |
| **This deployment cannot do it** | the nav entry is **not rendered** | a link that always leads to "not available" trains people to ignore the rail |
| **It can, but there is nothing** | entry shown, panel shows a quiet grey **empty state** | normal and unremarkable |
| **On the roadmap, not built** | dimmed, **non-clickable** (`Soon`) | hiding makes the roadmap invisible; linking makes a dead end |

A hidden entry and an empty panel are therefore never confusable, which is exactly the requirement.

---

## 4. Future modules are never active links

The six out-of-scope products — **Security Console, Report Studio, CRM, Construction, AI, Mobile** — appear
**nowhere**: not as links, and not even dimmed. They are out of this product's scope rather than merely
unbuilt, and showing them would imply a commitment this tab has not been asked to make.

The `Soon` flag exists in the model (`WorkspaceNavItem.Soon`, rendered `opacity: .45; pointer-events: none`)
and is currently used by nothing. It is kept because the *next* increment will need it.

---

## 5. Active-state resolution

An entry is active when its controller **and** action match the current route **and** it has no fragment.
Fragment entries never light up — they scroll within a page that is already active, and marking them active
would light two entries at once.

---

## 6. Responsive

| Breakpoint | Behaviour |
| --- | --- |
| `< lg` (992 px) | Sidebar is an **off-canvas drawer** (Metronic `data-kt-drawer`), opened by the header button. Drawer direction follows `dir` — `start` in LTR, `end` in RTL |
| `≥ lg` | Sidebar always visible, fixed |
| `< md` (768 px) | Header search hidden; icon actions and identity remain |
| `< sm` (576 px) | Identity text hidden, avatar retained |

The drawer is **Metronic's own mechanism**, not a second implementation — the framework's JavaScript already
handles focus, overlay and escape.

---

## 7. CrossBusiness Blue consistency

Every rail token comes from the scoped design system:

* sidebar ground `--cbw-blue-900` `#0b1f3d`
* active item: `rgba(31,95,208,.22)` fill + `--cbw-blue-500` inline-start border
* count badge `--cbw-blue-600`; attention badge `--cbw-critical` (notifications, mentions)
* section headings `rgba(255,255,255,.42)`, uppercase, letter-spaced

**Scoped under `.cbw`.** `crossbuy-brand.css` (the app-wide ledger-green identity) is untouched, so every
existing module screen keeps the green it has today. See the R1–R3 delivery notes §3 for the open
blue-product-wide decision.

---

## 8. Localization

Every label is an Ar/En pair on `WorkspaceNavItem` / `WorkspaceNavSection`, resolved through
`Label(bool arabic)` from `CultureInfo.CurrentUICulture`. Arabic is **first** in each pair because it is the
product's primary language. Direction, `lang` and `dir` all derive from the same culture, and the stylesheet
uses logical properties so one sheet is correct in both directions.
