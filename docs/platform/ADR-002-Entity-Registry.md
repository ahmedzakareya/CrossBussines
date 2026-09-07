# ADR-002 — One entity registry, promoted from TaskLinkResolver

**Status:** Accepted. Slice 1 created it; slice 2 was its first real expansion test (one new code, two upgraded,
one new permission scope).

## Context

Platform capabilities key off an entity type: business events, timeline, comments, and later relations, files,
followers, search and AI context. Before this slice, three vocabularies existed and disagreed:

| Source | Style | Example |
|--------|-------|---------|
| `TaskLinkResolver.Types()` | PascalCase, 7 types | `SalesInvoice`, `ManufWorkOrder` |
| `DocComment.EntityType` | unconstrained free text | anything a caller passed |
| `NotificationTypes` | snake_case, ~20 strings | `sales_invoice`, `goods_receipt` |

Unifying them costs a day now. After the event log fills with a forked vocabulary it costs a data migration.

Separately, the platform needed per-entity metadata: bilingual display name, icon, deep link, record search,
capability flags, and which module authorizes the entity. `TaskLinkResolver` (TM-2) already had four of those —
label, icon, deep link, and search — for its own record picker.

## Decision

**Promote `TaskLinkResolver` into `IEntityRegistry` rather than build a parallel component.**

1. `IEntityRegistry` is the single source of truth for entity codes. The vocabulary is the **frozen PascalCase
   set**, compared **ordinally** — `salesinvoice` is not a variant of `SalesInvoice`, it is invalid.
2. `EntityDefinition` carries `Code · DisplayNameAr · DisplayNameEn · Module · Icon · Color · RouteTemplate ·
   SupportsSearch · SupportsTimeline · SupportsComments · SupportsFiles · SupportsFollowers · PermissionScope ·
   ListedInRecordPicker`.
3. The search and resolve queries were moved **verbatim** from `TaskLinkResolver`, so the promoted registry
   returns identical rows to the pre-kernel picker.
4. `TaskLinkResolver` remains as a compatibility wrapper delegating to the registry. Its DTOs and signatures are
   unchanged; `TasksController`, `DevSeedController` and the DI registration were not touched.
5. `GetDefinition` **throws** on an unknown code. An unregistered code is a programming error, not a runtime
   condition to tolerate. `TryGetDefinition`/`IsValid` exist for the edges that legitimately need to test.
6. New platform code must not accept a free-text entity type. `PlatformTimelineController` rejects an
   unregistered code with `400` at the edge.

### Two decisions that needed care

**`ListedInRecordPicker`.** `Supplier` was searchable in `TaskLinkResolver.SearchAsync` (TM-9 scheduled tasks
need it) but absent from `Types()` — and because `ResolveAsync` looked its argument up in `Types()`, `Supplier`
was never resolvable either. That asymmetry is real behaviour that callers may depend on. Rather than fold it
into a semantic flag where it does not belong (`SupportsSearch` is true for `Supplier`; `RouteTemplate` is null
for `PosOrder` too, which *is* in the picker), it is recorded as an explicit compatibility flag that says what
it is.

**Capability flags describe what is wired, not what is conceivable.** Only `SalesInvoice` has
`SupportsTimeline = true` in this slice. Setting it optimistically for every entity would make
`ITimelineProjectionService` answer "no history" for entities that in fact have no timeline wiring at all —
an over-promise indistinguishable from a bug. Flags flip on per entity as each is onboarded.

## Consequences

* One place to add an entity, and every platform capability picks it up.
* The `EntityType` column can be constrained with confidence, because only registry codes reach it.
* Definitions are static and shared; per-entity queries need the request `DbContext`, so the registry is
  `Scoped` like every other BL service.
* **Known deviation:** employee search/resolve is not company-scoped, preserving TM-2 behaviour. Adding the
  filter would silently hide employees from an existing multi-company picker — a behaviour change outside this
  slice's pilot. Commented at both call sites and listed in PKS-001 §11.

## Alternatives rejected

* **A new `IEntityRegistry` alongside `TaskLinkResolver`.** Rejected: two type tables would drift, which is the
  exact problem being solved.
* **Deleting `TaskLinkResolver` and updating its callers.** Rejected: it changes working task-management code
  for no functional gain in a slice whose value is proving the kernel without disruption.
* **An abstract base entity that business POCOs inherit.** Rejected: EF would map it across 217 `DbSet`s, and
  `DocComment` already proves `(EntityType, EntityId)` works without touching a single entity.
* **Database-driven definitions.** Rejected: the vocabulary must be frozen and compile-time referable
  (`EntityRegistry.SalesInvoice`). A config table invites the free text this ADR exists to eliminate.

## Slice 2 addendum — what the first expansion proved

* **Adding an entity is one definition plus two switch arms.** `PurchaseInvoice` needed an `EntityDefinition`,
  a `SearchAsync` case and a `ResolveAsync` case. Timeline, permissions, notifications, URLs and the projection
  picked it up with no further change.
* **The capability flags did their job.** `Customer` and `ManufWorkOrder` already existed with
  `SupportsTimeline = false`; slice 2 *upgraded* them rather than adding duplicates. Turning the flag on broke
  exactly one slice-1 test — the one asserting that an entity without timeline support is refused — which is the
  flag proving it is load-bearing rather than decorative. The assertion moved to `Item`; it was not weakened.
* **No compatibility alias was needed.** Stored `EntityType` data already uses the canonical PascalCase codes,
  so the "add explicit mapping only when legacy values exist" clause did not fire. What the tests now pin is
  that near-misses are *not* codes: `"purchase_invoice"` (the NotificationTypes catalog key), `"WorkOrder"` (the
  journal-entry `SourceType`) and `"PurchaseInvoices"` are all invalid.
* **A new permission scope is cheap.** `Manufacturing` was added as its own scope that delegates to the
  inventory service, so manufacturing policy can diverge later without touching inventory entities.
* **`ListedInRecordPicker` earned its keep a second time.** `PurchaseInvoice` is registered but was never a TM-2
  picker type, so the flag keeps it out and the picker's seven types and their order are provably unchanged.

## Verification

* `GetDefinition_rejects_an_unknown_entity_code`
* `TaskLinkResolver_preserves_its_pre_kernel_behaviour`
* `Every_definition_has_a_permission_scope_and_a_usable_url_contract`
* `Registry_search_and_resolve_are_company_scoped`
* `Event_type_names_must_be_EntityCode_dot_PascalCaseAction`
* slice 2: `Pilot_definitions_are_complete_and_timeline_enabled` ·
  `Pilot_entities_are_not_duplicated_and_scopes_are_the_real_module` ·
  `BuildUrl_returns_a_real_per_record_route` · `Pilot_routes_point_at_the_real_existing_detail_pages` ·
  `Search_and_resolve_work_for_the_new_PurchaseInvoice_code` ·
  `Cross_company_resolve_is_rejected_for_every_pilot_entity` ·
  `Unknown_codes_are_still_rejected_and_no_alias_became_canonical` ·
  `The_record_picker_did_not_change_when_PurchaseInvoice_was_added` ·
  `Slice2_event_names_follow_the_canonical_convention`
