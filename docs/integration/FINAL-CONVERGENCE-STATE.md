# CrossBusiness — Final Convergence State

**Integration authority:** TAB-1
**Date:** 2026-09-01
**Starting master HEAD:** `e1eb499`
**Final canonical HEAD:** see §11 (recorded after the gates, from the pristine re-verification)

---

## 1. Repository graph, as measured

Nothing below is taken from a tab's report. Every ancestry claim is `git merge-base --is-ancestor`.

```
branch            master
worktrees         11 (CrossBuy, _ar, _land, wt-blue, wt-conv2, wt-doc3, wt-doc4,
                      wt-onb, wt-portal, wt-roster, wt-sec, wt-tab5-pim)
index             clean
shared tree       251 tracked-modified, 381 untracked, 3 stashes  (all foreign, all preserved)
```

Commits reachable from `master` at the start, in order: `e1eb499`, `662b834`, `3e2ecc3`,
`a9fa399`, `f5f2a72`, `d688886`, `81ec8fa`, `d05d687`, `dd112cc`, `578c471`, `1f7145c`,
`77a148e`, …

**Commits NOT reachable from master at the start** — the real candidate set:

| Commit | Subject | Merge base | Verdict |
|---|---|---|---|
| `a2b9e86` | Documents: expiry / renewal (Batch 4) | `662b834` | **integrated** |
| `cf9276e` | Portal: external customer boundary | `d05d687` | **integrated** |
| `68d4338` | Central Documents Batch 3 | `d05d687` | **already present** — see §2 |
| `20b24a4`, `baf10e0` | Brand: Facebook Blue palette + SVG assets | `a9fa399` | **not landed** — see §10 |
| `ebbbbf5`, `44a93fe` | stash commits on the brand branch | — | not candidates |

`e1eb499` was reported as a candidate by the Accounting batch. It **is** master HEAD: already
landed, nothing to integrate.

---

## 2. Candidate integration table

| TAB | Batch | Candidate | Merge base | In master before? | How landed | Shared files | Handoffs | Decision |
|---|---|---|---|---|---|---|---|---|
| TAB-3 | Central Documents 4 | `a2b9e86` | `662b834` | No | cherry-pick → `52bf5dd` | none | 3 (§4) | **Integrated**, 8 files byte-identical |
| TAB-3 | Central Documents 3 | `68d4338` | `d05d687` | **Semantically yes** | cherry-picked earlier as `a9fa399` | — | — | **Already present** |
| TAB-1 | HR bootstrap hardening | patch | — | No | `git apply` → `0cfe7dd` | none | closes the §4A weakness | **Integrated** |
| TAB-1 | Doc lifecycle read + UI | §4A/§4B | — | No | new → `8b69918` | SHF-29, SHF-30 | 4A, 4B closed | **Integrated** |
| TAB-3 | Client Portal | `cf9276e` | `d05d687` | No | cherry-pick → `0d9a5ff` | Program.cs, CrossDbContext.cs (auto-merged) | reconciliation | **Integrated** |
| TAB-6 | Accounting Period Close | `e1eb499` | — | **Yes** | — | — | §6 carried | **Already present** |
| — | Brand Facebook Blue | `20b24a4` | `a9fa399` | No | — | 3 open handoff patches | 3 | **Not landed** (§10) |

**Ancestry proof for “already present”.** `68d4338` is not an ancestor of master, because it was
cherry-picked onto a newer base as `a9fa399` — a different hash for identical content. All five of
its files were compared blob-for-blob at the time and were identical. It is present semantically and
provably, not by resemblance.

---

## 3. Foreign-work preservation

Captured **before** any change: byte copies of all 26 candidate-overlapping files, the tracked diff
(16,295 lines), the full `git status`, and a `git hash-object` of every captured file.

* Integration was performed in a **dedicated clean worktree** (`wt-conv2`, branched from master).
  The shared tree at `C:/CrossBuy/CrossBuy` was **never written to** during this batch.
* Only 2 of the 26 overlapping files were dirty in the shared tree (`Program.cs`,
  `CrossDbContext.cs`) — both carrying another tab's Construction/Platform-Kernel WIP.
* After integration, all **9 captured files hash bit-identically** to their pre-integration state.
* All **3 stashes intact**. No `reset --hard`, no `clean`, no stash drop, no broad checkout.

One file changed in the shared tree during the batch — `Views/Shared/_LayoutEmbed.cshtml` — by
another tab. It is not in the candidate set, was never touched here, and is preserved as it stands.

---

## 4. Architecture convergence

**Central Documents (§4).** One canonical evaluation path, `EvaluateAt(doc, type, today)`. NULL
lead-time means the platform default, not "never warn". Validity is a projection of it. Renewal is
a new version of the same document; each version keeps its own instrument identity. Task idempotency
is instrument-aware: `DocExpiry:{documentId}:{yyyyMMdd}`. No `DocumentReminder`, no `DocumentTask`,
no sixth Attention source.

**§4A — resolved by reuse, not by widening.** TAB-3 wrote `LifecycleAsync` /
`LifecycleForEntityAsync` and correctly refused to land them: `IPlatformDocumentService` is a
declared authority and `DocumentAuthorityMemberTests` fails reflectively on an uncovered member.
`ListForEntityAsync` already gates the entity once and re-asks per document, so its row now carries
the lifecycle. **The interface still has exactly 11 members.** The guarantee was not spent.

**§4B — one governed screen.** `GET /Documents/workspace/{entityType}/{entityId}`. It compares no
dates; state, days remaining and warning window all arrive computed. A restricted row is *absent*
rather than hidden. Bytes only through the authorized route — no StorageKey, no static path.
Route names an entity type, never a module.

**§4C — blocked, and recorded.** `CrossBuy/BL/Reporting/**` is **TAB-2's**. Datasets not written.

**Accounting (§5), re-proved on the integrated tree:** 2 `JournalEntries.Add` sites, both in the GL
writer; 0 raw-SQL inserts; 4 posting entry points through 1 guarded method; GL and stock guards both
on `BlocksPosting`; `period-close` = chief‖acct, `period-reopen` = chief, both NeverBootstrapOpen;
`ReasonMaxLength` 500; `ResolveCompensatingPostingDateAsync` intact.

**Communication (§8) / Tasks-Workspace (§9) / AI (§7):** verification results and the items that
could **not** be proven are in `FINAL-OPEN-HANDOFFS.md`. Nothing was claimed closed on a summary.

---

## 5. Security proof — measured, not copied

```
mutating            439   (unchanged)
attributeProtected  156   (unchanged)
inBodyProtected     189   (unchanged)
gaps                 94   (unchanged — gap-set delta: ZERO, id for id)
controllers          52 → 53   (ClientPortalController, read-only: five GETs, no mutating action)

156 + 189 + 94 = 439

CBA001 0 · CBA002 0 emitted (configured `suggestion`) · CBA003 0
CBA004 0 · CBA005 0 · CBA006 0
```

The entire convergence introduced **no mutating endpoint and no authorization gap**. The one frozen
number that moved is a count of controllers, and the controller that moved it writes nothing. No
protected-category reclassification occurred.

---

## 6. Company-isolation verification (§10)

New surfaces: the documents workspace authorizes per entity *and* per document; the Client Portal
scopes twice (company **and** customer) with `PortalContext` structurally unable to become a
`BusinessContext`. Both proven behaviourally, including mutation M8.

Pre-existing and **not introduced here**: 13 files still carry a live company-1 constant. None was
touched by this convergence — see `FINAL-OPEN-HANDOFFS.md` for the exact list. §10 forbids widening
this into a rewrite, so they are reported, not repaired.

---

## 7. Ownership proof (§11)

Canonical generator only; regenerated twice per change; **byte-identical** each time; no duplicate
path rules (30 shared entries, 0 duplicate paths). `-Strict` passes per half:

* TAB-1 — 12 owned + 4 shared, 0 unclaimed, 0 denied
* TAB-3 — 7 owned + 1 shared, 0 unclaimed, 0 denied (documents) and 7 owned (portal)

Ownership delta: TAB-3 gains `Views/Documents/**` and the whole Client Portal surface (both were
unclaimed, which is why they could not land under `-Strict`); TAB-1 gains
`DocumentLifecycleUiTests.cs`; **SHF-30** names the one file with two owners —
`PlatformDocumentService.cs`, whose behaviour is TAB-3's and whose *public member set* is a security
contract.

---

## 8. Schema / DDL parity (§13)

| Slice | Parity | Executed |
|---|---|---|
| `documents_004_expiry.sql` | `DocumentNumber nvarchar(200)` ↔ `HasMaxLength(200)`; `IssueDate`/`ExpiryDate datetime2` ↔ `DateTime?` on **PlatformDocumentVersions**; `ExpiryWarningDays int` ↔ `int?` on **PlatformDocumentTypes** | **No** |
| `comm_portal_001_identity.sql` | authored with the portal candidate | **No** |
| `accounting_period_control.sql` | proven last batch, unchanged | **No** |

**No backfill.** `documents_004_expiry.sql` contains zero `UPDATE` and zero `INSERT`: historical
`PlatformDocumentVersion` rows are *not* given the current document's instrument identity, which
would falsify what those versions actually were.

```
DDL executed      0
CrossBuyDev writes 0
production writes  0
```

---

## 9. Mutation results (§15)

Every mutation applied to the integrated HEAD and reverted immediately afterwards.

| # | Mutation | Killed by |
|---|---|---|
| 1 | GL period guard → literal `"Closed"` | `PeriodPostingChokepointTests.Every_public_posting_entry_point_funnels_through_the_one_guarded_method`, `AccountingPeriodCloseTests.The_gl_writer_asks_the_shared_predicate_rather_than_a_literal` |
| 2 | Stock period guard → literal `"Closed"` | `PeriodPostingChokepointTests.A_period_that_blocks_posting_blocks_a_stock_movement_too(SoftClosed)` (behavioural), `…The_stock_guard_asks_the_canonical_predicate_rather_than_a_literal` |
| 3 | `SetStatusAsync` back on the **interface** | **compilation fails** — `ScriptedPeriods` no longer implements `IFiscalPeriodService` |
| 4 | `SetStatusAsync` back on the **concrete class only** | `PeriodPostingChokepointTests.The_CONCRETE_period_service_exposes_no_status_mutator_either` |
| 5 | UI computes its own expiry | `DocumentLifecycleUiTests.The_view_compares_no_dates_of_its_own`, `…The_view_renders_the_state_the_service_named` |
| 6 | New uncovered public member on the declared authority | `DocumentAuthorityMemberTests.Every_member_of_the_declared_authority_is_covered_by_this_file`, `DocumentLifecycleUiTests.The_declared_authority_did_NOT_grow_to_serve_the_screen` |
| 7 | Expiry idempotency keyed by document id only | `PlatformDocumentTests.A_renewed_document_may_warn_again_on_its_NEW_expiry_date` |
| 8 | Per-document authorization removed from `ListForEntityAsync` | `DocumentLifecycleUiTests.A_row_the_caller_may_not_see_is_ABSENT_rather_than_rendered_as_hidden`, `PlatformDocumentTests.Confidentiality_is_carried_to_the_resolver_and_can_refuse_a_single_document` |

Working tree verified identical to HEAD after the last revert.

---

## 10. What was deliberately NOT landed

**Brand / Facebook Blue** (`20b24a4`, `baf10e0`) is a **live branch, not a completed candidate**: it
has its own worktree, its own stash of in-flight work, and three unresolved ownership handoff
patches (`_blue-B-TAB2-reporting`, `_blue-C-TAB6-workspace`, `_blue-D-SHARED-integration-owner`).
Landing a branch whose owner is still working on it, and whose handoffs are open, is not integration.

---

## 11. Build / test gates and final HEAD

Recorded from a **pristine worktree created from the final candidate HEAD** — see the closing
section of the batch report for the exact figures.
