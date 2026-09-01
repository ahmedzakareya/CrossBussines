# PlatformPermissionProvider — Mutation #3, Closed

## Why it survived, established by reading the code rather than guessing

`context.CompanyId` appears exactly **twice** in `PlatformPermissionProvider`:

1. **line 104** — inside a deny *message* string. Mutating it changes prose.
2. **line 126** — the **cache key** `(entityCode, entityId, context.CompanyId)`.

The company gate itself never reads the property. It calls
`_registry.ResolveAsync(code, id, CONTEXT)`, handing the whole context down, so isolation is decided
by the registry's own company-scoped query. **The mutation was invisible because neither read is the
gate** — one is decoration, the other a memo key. That is why 81 tests missed it.

## But the memo key is a real security surface

The provider is `Scoped` and memoises *"does (type, id) exist in company C"* for the life of that
scope. Drop the company from the key and two companies asking about the same entity id share one
entry: the first answer is served to the second caller. A company-A row resolves `Found = true`, and
company B — asking about an id it must not be able to see — is handed that cached `true` and walks
through the company gate.

`NotificationProjectionConsumer` is exactly the shape that reaches it: one entity, many recipients,
one scope.

## The coverage

Six tests, driving **two companies through one provider instance** — the only way the key is
observable:

* `One_providers_cache_does_not_serve_company_As_answer_to_company_B` — the decisive case
* `The_order_of_the_two_questions_does_not_change_either_answer` — a stale-cache bug is directional
* `Repeating_the_same_question_is_memoised_without_changing_the_answer` — the memo still works
* `Neither_company_can_reach_the_others_record_through_the_shared_memo`
* `The_gate_reads_the_SUPPLIED_context_and_not_an_ambient_one`
* `An_entity_id_that_exists_nowhere_refuses_exactly_like_a_foreign_one`

**Result:** the mutation now fails **3 of 6**. Closed behaviourally, not by source text.

## Two corrections made while writing them

**`Employee` was the wrong entity type.** `EntityRegistry.ResolveAsync` applies **no company filter**
to `Employee` — a declared deviation, commented *"TM-2 behaviour"*. The consequence is worth stating
plainly: **the platform company gate is a no-op for the Employee entity type**, because
`BelongsToCompanyAsync` consults `ResolveAsync(...).Found`. It is another tab's declared choice in a
file this batch does not own, so it is **reported, not changed**, and carried as new debt.

**The fourth test asserts less than first written.** `PlatformTestHost` holds one company scope and
the DbContext's global filters answer to that holder, so a second live company scope is not
representable in the fixture. Asserting it would have been testing the fixture.
