# Stage-Tasks-Calendar-02 — Registry Onboarding (Phase 2)

**Executed.** `Task` and `CalendarEvent` are registered in `CrossBuy/BL/Platform/EntityRegistry.cs`.
TCI-D-01 is closed.

---

## 1. What was added

Two `EntityDefinition` entries, two entity-code constants, and — so the registration is not hollow — **real search
and resolve arms** for both.

| | `Task` | `CalendarEvent` |
|---|---|---|
| Module | Tasks | Calendar |
| RouteTemplate | `/Tasks/Index` | `/Calendar/Index` |
| SupportsSearch | ✔ | ✔ |
| SupportsTimeline | ✔ | ✔ |
| SupportsComments | ✔ | ✔ |
| SupportsFiles | ✔ | ✔ |
| SupportsFollowers | ✔ | ✖ — attendees **are** the follower set |
| **PermissionScope** | **`Tasks`** | **`None`** |
| ListedInRecordPicker | ✖ (§3) | ✖ (§3) |

## 2. The two permission-scope decisions

**`Task` → `ScopeTasks`, not `ScopeNone`.** `TasksAccessService` already exists with eight actions and three roles.
Registering with no scope would make the registry the loosest door into a module that already has a lock — which is
exactly the gap this tab reported against `Project` (registered `ScopeNone` while `ProjectsAccessService` exists).

**`CalendarEvent` → `ScopeNone`, deliberately.** Calendar has **no** access service. Its real rule is record-level —
organiser ∪ company-scope ∪ attendee — and it already lives in `CalendarService.Visible()`. Inventing a module scope
here would create an authorization surface nobody owns and no role fills. The registration says so in its own comment
so the next reader does not "fix" it.

## 3. `ListedInRecordPicker = false` — a deliberate deviation, kept

The earlier contract proposed `true`. It is registered as **false**, and the owner has confirmed it stays false in
this increment.

**Why.** `EntityRegistryTests.TaskLinkResolver_preserves_its_pre_kernel_behaviour` pins the picker to exactly seven
types in order. Adding `Task` there changes a behaviour another tab's test guards. That is a product decision for the
registry owner, not a side effect of onboarding — and nothing else in the registration depends on it.

Asserted by `The_record_picker_contract_is_unchanged_by_this_registration`, which pins the same seven types from
this tab's side, so the deviation cannot be undone by accident.

## 4. Search and resolve — why they were added rather than left to the default

`SearchAsync` has a `_ => new List<(int,string)>()` default and `ResolveAsync` falls through to "not found". So
registering with `SupportsSearch = true` and no arm would have produced an entity that claims to be searchable and
returns nothing, and a link that resolves to `#42` instead of a title.

Both arms filter by company, so a search can never surface another tenant's row.

**One boundary is stated in the code.** The Calendar resolve arm returns a **label** and is company-filtered; it is
not a read authorization. `CalendarService.Visible()` remains the only place that decides who may open an event. That
is why `CalendarEvent` is not in the record picker, and why the agenda redacts rather than relying on this path.

## 5. What the registration buys

`RecordAsync` validates an event name against the registry and **throws on an unknown code**. Before this,
`Task.Created` was unpublishable. After it, every contract in doc 03 is publishable — which is the whole reason
Phase 2 precedes Phase 3.

It also enables timeline, comments, mentions and files for both entities at the platform level; consumers for those
belong to TAB 3 and none were written here.

## 6. Tests

`TasksCalendarRegistrationTests` — 4 tests:

- both entities registered with the intended capabilities and `Task.PermissionScope == "Tasks"`;
- an unknown code still fails closed (`"Taks"`, `"CalendarEvents"` refused);
- the record-picker contract is unchanged;
- a registered event name validates and a mismatched one does not.

**No private second registry was created.** The definitions live in the kernel's own list, and
`TaskCalendarRegistryOnboarding` remains as the documented request that produced them.
