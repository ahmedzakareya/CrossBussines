# CrossBusiness Platform — Stage 2A B6 — Test Transition Decisions

Seven tests encoded pre-B6 behaviour and failed once the three sites were converted. **Each was reviewed
individually against `NeverBootstrapOpen.All`, the Compatibility Read Matrix and live source.** None was bulk-rewritten
and none was merely inverted: every still-valid assertion was preserved, and five tests were renamed because their
names asserted behaviour that no longer exists.

**A failing test here is the intended behaviour change surfacing where it should.** Flipping six security assertions
to match new behaviour without recording why is how a real regression gets laundered through a green suite — so each
carries a decision record.

---

## 1 · `Unseeded_accounting_allows_a_cashier_to_post_which_is_why_seeding_is_mandatory`

`D1Wave1GateTests.cs:95`

| | |
|---|---|
**Previous behaviour** | unconfigured company ⇒ a cashier could post to the general ledger |
**New approved behaviour** | **denied** |
**Action** | `Accounting.post` |
**Never entry** | *"Posts to the general ledger. The two-writers rule makes JournalEntryService the only writer; bootstrap must not decide who may invoke it."* |
**Still valid?** | the *scenario* yes, the *assertion* no |
**Renamed?** | **yes** → `Unseeded_accounting_DENIES_a_cashier_posting_because_post_is_never_bootstrap_open` |
**Assertion change** | `Assert.True(... "post")` → `Assert.True(NeverBootstrapOpen.Contains("Accounting","post"))` + `Assert.False(... "post")` |
**Security consequence** | **the exposure this test documented is now closed.** The old name advertised a finding; keeping it would misdescribe the system. |

## 2 · `Unseeded_projects_allows_billing_which_is_why_seeding_is_mandatory`

`D1Wave1GateTests.cs:106`

| | |
|---|---|
**Previous** | unconfigured ⇒ billing allowed with no ledger right at all |
**New** | **denied transitively** |
**Action** | `Projects.billing` → delegates to `Accounting.post` |
**Never entry** | `Projects.billing`, and `Accounting.post` upstream |
**Renamed?** | **yes** → `Unseeded_projects_DENIES_billing_transitively_because_accounting_post_is_never` |
**Assertion change** | `Assert.True(Billing)` → `Assert.False(Billing)`, preceded by an explicit `NeverBootstrapOpen.Contains("Accounting","post")` |
**Security consequence** | proves the **transitive closure** — `ProjectsAccessService`'s rule that *"being an administrator of projects is not a right over the ledger"* now holds on unconfigured companies too. |

## 3 · `With_no_role_rows_even_a_cashier_can_post_which_is_why_every_test_seeds_a_role`

`D1Wave1CompanySourceTests.cs:154`

| | |
|---|---|
**Previous** | unconfigured ⇒ cashier posts |
**New** | **denied** |
**Renamed?** | **yes** → `With_no_role_rows_a_cashier_is_DENIED_posting_because_post_is_never_bootstrap_open` |
**Still valid?** | the paired guard below it (`Once_a_single_role_row_exists_the_cashier_is_denied_posting`) is **kept untouched** — it still proves the role path |

**The previous version asked for exactly this treatment.** Its assertion message read: *"if this ever fails, the guard
below is no longer needed and this test should be revisited **deliberately, not deleted**."* B6 made it fail; it was
revisited, not deleted, and the paired guard was kept.

## 4 · `Bootstrap_open_applies_per_company`

`Stage1PermissionTests.cs:106` — **two assertions, split disposition**

| Assertion | Disposition |
|---|---|
company 1 configured ⇒ roleless user denied `manage` | **PRESERVED UNCHANGED** — company isolation and role preservation |
company 2 unconfigured ⇒ `manage` allowed | **TRANSITIONED** to denied |

`Accounting.manage` is Never because it gates `AssignAccRole`/`RemoveAccRole` **and** is `PlatformOpsAttribute`'s
fallback authority. Renamed → `Per_company_evaluation_holds_and_bootstrap_no_longer_opens_manage`, because
per-company evaluation is still true — what changed is that bootstrap no longer opens `manage` for anyone.

## 5 · `Removing_the_seeded_role_row_changes_the_answer`

`D1Wave1GateTests.cs:119` — **two assertions, split disposition**

| Assertion | Disposition |
|---|---|
configured ⇒ post denied | **PRESERVED UNCHANGED** |
role rows removed ⇒ post allowed again | **TRANSITIONED** to still denied |

**That flip *was* the exposure.** Post is now denied in both states, so the test proves the answer is **stable under
de-configuration** — deleting the role table no longer reopens the ledger. Renamed →
`Removing_the_seeded_role_row_no_longer_reopens_posting`.

## 6 · `Project_billing_requires_the_accounting_modules_permission_too`

`BatchCAccessServiceTests.cs:592` — **two assertions, split disposition**

| Assertion | Disposition |
|---|---|
accounting bootstrap-open ⇒ billing allowed | **TRANSITIONED** to denied |
accounting closed for this caller ⇒ billing refused | **PRESERVED UNCHANGED** |

**Not renamed** — the title is still exactly what the test proves, and B6 *strengthens* it: billing requires the
accounting module's permission, and there is no longer a bootstrap route around it.

## 7 · `Stage1F4DatabaseProofTests.Every_negative_attempt_leaves_zero_business_effect` — `"foreign company"` case

`Stage1F4DatabaseProofTests.cs:432`

**Diagnosed, not assumed.** Failure message: *"company 2 is unconfigured, so bootstrap-open applies there"* — an
**obsolete bootstrap-open assumption**, not a changed denial path, not fixture state, not a real side effect.

| | |
|---|---|
**Previous** | company-2 employee acting in company 2 was **allowed** to post (bootstrap-open there); the negative was "allowed but touched nothing of company 1" |
**New** | **denied outright**, and still touches nothing of company 1 |
**Zero-business-effect proof** | **PRESERVED EXACTLY** — `reachable == 0` and the fingerprint comparison are untouched |
**Not renamed** | the test's subject is unchanged |

The proof is **stronger** than before: the negative moved from *allowed-but-harmless* to *denied-and-harmless*.
Company isolation is still enforced by the row predicate rather than by the role check, exactly as the original
comment explained.

## Summary

| | Count |
|---|---|
Tests reviewed individually | **7** |
Renamed | **5** |
Assertions inverted | **7** |
Still-valid assertions **preserved** | **4** (tests 3 paired guard, 4, 5, 6) |
Bulk rewrites | **0** |
Tests deleted | **0** |

Every transition traces to a specific entry in `NeverBootstrapOpen.All`, and every one records what it now proves
rather than only what it stopped proving.
