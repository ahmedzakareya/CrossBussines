# 37 — Component Usage Rules

## Universal component contract

Every component uses its Metronic/Bootstrap primitive and documents: visual anatomy; interaction; default/hover/focus/pressed/selected/disabled/loading/error/success; motion; accessible name/role/state; keyboard; 44px touch behavior (36px compact desktop); RTL logical placement; dark tokens; use and non-use. Disabled controls must not be the only explanation of denied access. Reduced motion applies to all animation.

| Component | Use / behavior | Do not use |
|---|---|---|
| Button | `.btn` hierarchy; one page primary; spinner retains label | link-style navigation disguised as action; icon-only without name |
| Card/widget/panel | Metronic card, header/body/actions; ≤2 nesting | wrap every section; stacked shadows |
| Input/textarea | Bootstrap/Metronic form control, persistent label | placeholder-only label |
| Select/lookup | native for small sets; Select2 for search/remote | huge native list; silent dependency reset |
| Checkbox/radio/switch | explicit label and checked/focus state | switch for irreversible action |
| Table | structured comparison, headers/paging/states | layout table or arbitrary card content |
| Tree | jsTree/accessible tree for hierarchy | tree for flat navigation |
| Tabs | related peer sections, active state, arrow navigation | workflow steps or cross-module nav |
| Timeline | chronological actor/action/object/time | decorative activity without provenance |
| Badge/status chip | short category/status, text + semantic color | interactive control unless implemented as button |
| Alert/toast/notification | severity-appropriate channel; live-region policy | toast for validation or durable critical state |
| Dialog | short blocking decision; focus trap/restore | nested modal or long multi-step form |
| Drawer | contextual detail/filter; logical-edge slide | hide essential page content permanently |
| Kanban | stage-based work with menu/keyboard move | visual-only drag or huge unbounded board |
| Chart | ApexCharts + summary/table alternative | color-only series or unjustified 3D |
| Dashboard widget | one question, scope/time/action/states | miniature full screen or unrelated KPIs |
| Calendar | FullCalendar + form/keyboard equivalence | grid-only mobile schedule |
| Report viewer | shared parameters/provenance/result/drill | module-specific duplicate viewer |
| Business-event card | event type, actor/source, time, entity, status | raw payload/secrets by default |
| Workspace panel | bounded cross-module summary + source link | duplicate authoritative editor/data store |
| Comment/mention/chat | semantic chronology/composer/delivery states | new Communication platform built on legacy assumptions |
| File attachment | filename/type/size/progress/scan/access/retry | icon-only unidentified upload |
| Progress/skeleton/loading | determinate if measurable; stable geometry | infinite global spinner |
| Search/filter | explicit scope/applied state/reset | search icon with hidden semantics |
| Breadcrumb/sidebar/topbar | Metronic navigation, one active path | duplicate competing navigation |
| Icon | KeenIcons primary; label when meaning is unclear | mixed icon families or icon-only critical action |

Usage example: `<button class="btn btn-primary">Save</button>` expresses the primitive; actual implementation must use localized text, authorized rendering, documented loading and result states, and approved tokens. A new component proposal must inventory Metronic alternatives and pass `40-UX-Governance.md`.
