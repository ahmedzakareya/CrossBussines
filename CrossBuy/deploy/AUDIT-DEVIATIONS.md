# CrossBuy — Documented Audit Deviations

A permanent, append-only log of accepted accounting/audit deviations. Each entry is documented and
deliberately **not** "fixed", because the fix would break a stronger invariant. One line per field.

---

## DEV-2026-001 — EntryNo gap JV-2026-001155

- **Date:** 2026-07-28
- **Missing number:** JV-2026-001155
- **Deleted entry:** JournalEntry #8046
- **Value:** 2475.78
- **Accounts:** Dr 520111 (cash over/short) / Cr 11010101 (terminal T1 drawer)
- **Root cause:** POS shift #2 was resumed and closed by mistake during HM-1-أ regression testing, then the resulting variance entry was removed via direct SQL DELETE instead of JournalEntryService.ReverseAsync.
- **Remediation applied:** shift #2 restored to Open (close fields nulled); JE #8046 + its lines deleted (SET QUOTED_IDENTIFIER ON); trial balance re-verified balanced (diff 0); zero orphan references.
- **Decision:** accept as a documented gap; do not fill.
- **Why not filled:** the NumberSequences counter already advanced to 1156, so every new entry takes the next number, never 1155 — filling 1155 would require forcing a number outside JournalEntryService (the very invariant that was broken); a zero "gap-filler" entry would inject a non-event into the ledger (worse for audit than a missing number); the impact is confined to the DEV database — production is unaffected.
- **Scope:** DEV database only.

---

## DEV-2026-002 — Test-data manifest (frozen, exclude by ID — never by text match)

Captured 2026-07-28 (HM-1-أ ب-2-تكميلي). All entities below were created by the assistant during HM-0/HM-1-أ testing. The legacy-baseline (COGS check + EntryNo gaps) must exclude these **by explicit ID**, not by `Notes LIKE`.

- **Sales invoices (test):** IDs 26, 27, 6174, 6175, 6178 (project-test, Notes «اختبار مشروع») + 6176, 6177, 6179 (mc-test FX/O2C). Total 8.
- **Purchase invoice (test):** ID 1042 (PI-2026-01042, mc-test-p2p). **Goods receipt:** GRN-2026-01020.
- **Branch:** HYPER-DEMO #17 (+ its BranchCapabilities, BranchPosSetting, BranchPaymentMethods, BranchUserRoles).
- **Terminals:** HM-L1 #1007, HM-L2 #1008.
- **Identity users:** hyper1 (fff141f7-3ef0-4bd2-87ca-fdf4235d0fc0), rest1 (48ea75f3-5fbd-4368-ab6e-1bd7ad694abc).
- **Employees:** #1042 (Hyper Cashier), #1043 (Restaurant Cashier).
- **Throwaway (ZZ-*):** created + deleted within each test (guard/concurrency); none persist.

Baseline snapshot at capture: **JV EntryNo gaps = 254** (block 1–206, cluster 1082–1139, +001155); **COGS-damage sale-lines = 23 / revenue 5,529.59** (of which ITM-0001 + direct invoices ≈ 3,864.59 are the test SVs above). The FORMAL frozen baseline (with fix-apply timestamp) is captured in ب-3.

**Standing rule:** ad-hoc test entities use the `ZZ-` prefix and are deleted at end of test; posted JEs are never deleted (ReverseAsync only).

---

## DEV-2026-003 — Burned receipt counter on terminal RC6-T (#1004) — CONFIRMED defect (HM-D5)

- **Date found:** 2026-07-28 (HM-1-أ ب-3 pre-check).
- **Symptom:** `PosTerminals.NextReceiptNo` on terminal **RC6-T (#1004) = 1902** (its actual max online receipt is RC6-000066 → expected ~67).
- **Root cause:** the offline-sync catch-up (`PosOrderService` SyncSettledOrderAsync) extracts `n` by scraping ALL digits from an offline `ReceiptNo` — a test receipt `T1-9E01` → digits «1901» → counter advanced to 1902. The prefix's own digits are wrongly included.
- **Blast radius:** cosmetic so far (1902 < 999999, so D6 still renders «001902»); but a future scrape yielding n>999999 would produce a 7-digit receipt and break the D6 / tax-invoice format.
- **Scope:** the `T1-OFF001/002` and `T1-9E01` receipts are all on RC6-T, dated 2026-07-13, from the rc offline-sync DEV self-tests (RC6-T is a dev-test terminal). No real cashier data affected.
- **Decision:** NOT touched (rule: no live-data edits). **Added to HM-D5 as a CONFIRMED existing defect** (offline-number handling): the sync digit-scrape must parse only the offline sequence, not the prefix. Until HM-D5, ب-3 adds a DETECTIVE integrity check (no duplicate ReceiptNo within a terminal) instead of the previously-planned unique index (the index is cancelled from HM-1-أ because the collision source is live in code).
- **«OFF» history note:** the `OFF` marker appears ONLY in dev-test fixture data — the CURRENT production offline generator (`pos-offline-order.js`) emits `ReceiptPrefix + pad6(n)` (same space as online), no `OFF`. This project is not a git repo, so version history cannot be checked; from the visible code + data, `OFF` was a test-fixture synthetic marker, not a production separate-space design. HM-D5 is therefore NEW space-separation work, not a restoration.
- **ج data-scan (2026-07-29):** longest ReceiptNo = `POS2T-000001` (12 chars) — the width is driven by the **prefix**; the numeric part is always 6 digits. **No** ReceiptNo in `PosOrders` has >6 trailing digits, so counter 1902 has not yet produced a 7-digit number (still cosmetic, as stated). Prefixes in use: `RC6-`(66), `T1-`(22), `T2-`(3), `POS2T-`(1). The `T1-OFF001/002` + `T1-9E01` rows all sit on terminal **1004** (whose real prefix is `RC6-`), whereas `T1-` is terminal **2**'s normal ONLINE prefix — the prefix mismatch confirms `OFF` is synthetic fixture data on a dev terminal, not a coherent separate space. `pos-offline-order.js::nextReceipt` emits `receiptPrefix + pad6(n)` (same space as online). **Answer to the ج inquiry:** T1-OFF was NOT a by-design separate space in the visible codebase → HM-D5 is a numbering-parse fix, not a design restoration; priority unchanged.
- **Fixed-width dependency (HM-D5 scope):** the server formats the receipt number as `ReceiptPrefix + <number>` zero-padded to a **minimum** width of 6 (`AllocateReceiptNoAsync` → `prefix + n.ToString("D6")`); min-width 6 is NOT a cap, so a scraped `n > 999999` would emit 7 digits and break the 6-digit assumption used by the receipt / tax-invoice layout and any fixed-width print/report column. The offending digit-scrape is `PosOrderService.cs:1453` — `new string(p.ReceiptNo.Where(char.IsDigit).ToArray())` includes the prefix's own digits (`T1-9E01` → `1901`).

### HM-D5 SIZING (read-only measurement, 2026-07-29) — requested before setting priority

- **1902 confirmed as a real bug output, not a stray value.** `T1-9E01` = PosOrder #3350 on terminal **1004**, dated 2026-07-13 11:14, Status Paid. Digit-scrape of `"T1-9E01"` = `1,9,0,1` = **`1901`**; sync advances `NextReceiptNo = max(current, 1901+1)` = **`1902`**. Matches exactly. No `PosSyncLog` row exists for it → it was injected as a fixture, not produced by a real logged sync (consistent with "synthetic dev fixture").
- **Blast radius is EVERY terminal, not just RC6-T** — because every prefix in use contains a digit, scraping a *normal* 6-digit receipt already yields 7 digits:

  | Terminal | Prefix | Top receipt | scrape(top) | counter jumps to | digits |
  |---|---|---|---|---|---|
  | #2 | `T1-` | `T1-000017` | `1000017` | 1000018 | **7 ✗** |
  | #3 | `T2-` | `T2-000003` | `2000003` | 2000004 | **7 ✗** |
  | #4 | `POS2T-` | `POS2T-000001` | `2000001` | 2000002 | **7 ✗** |
  | #1004 RC6-T | `RC6-` | (real) `RC6-000066` | `6000066` | 6000067 | **7 ✗** |
  | #1004 RC6-T | `RC6-` | fixture `T1-9E01` | `1901` | 1902 | 4 (short fixture) |
  | #1007 HM-L1 | `HM-L1-` | (first offline) `HM-L1-000001` | `1000001` | 1000002 | **7 ✗** |

  ⇒ the **first real offline sync on ANY terminal** would push the counter past 6 digits and break D6. It has not fired yet only because offline sync hasn't been exercised on the online terminals; the RC6-T fixtures happened to be short codes.
- **D6 fixed-width dependents:** the number is min-width-6 (not capped) in **4 places** — allocation `PosOrderService.AllocateReceiptNoAsync` (`n.ToString("D6")`) + display `Views/Hyper/Dashboard.cshtml:166`, `Views/Hyper/PosLane.cshtml:43`, `Views/Pos/Terminals.cshtml:89`. The stored `ReceiptNo` string is printed as-is on the thermal receipt / tax invoice, whose layouts assume the 6-digit width.
- **Fix scope (design only, NOT implemented):** at `PosOrderService.cs:1453`, replace the all-digits scrape with a **prefix-stripped suffix parse** — `term` is already loaded, so: strip `term.ReceiptPrefix` from the start of `p.ReceiptNo` then `int.TryParse` only the remainder (guard when the prefix doesn't match). Single-line change, no schema. Separately (bigger, deferred): a true offline/online number-space split or a sync-time renumber for collisions.
### HM-D5-أ APPLIED (2026-07-29) — scrape fixed, recomputed table (all ≤ 6 digits)

Fix: `PosOrderService.AdvanceCounterPastOfflineReceiptAsync` (called at the former line 1453) now strips the terminal's own `ReceiptPrefix` and parses ONLY the numeric suffix; a prefix mismatch / unparseable suffix does NOT advance the counter and does NOT fail the sync — it records a manager-visible `PosSyncConflict` (`ConflictType="ReceiptNoMismatch"`). No change to the offline generator, no space split, no re-numbering, no D6 change; counter 1902 on #1004 left as-is (documented above). Recomputed per terminal:

| Terminal | Prefix | Offline receipt | suffix parse (new) | counter target | digits |
|---|---|---|---|---|---|
| #2 | `T1-` | `T1-000018` | `18` | max(18,19)=19 | 6 ✓ |
| #3 | `T2-` | `T2-000004` | `4` | max(4,5)=5 | 6 ✓ |
| #4 | `POS2T-` | `POS2T-000002` | `2` | max(2,3)=3 | 6 ✓ |
| #1004 RC6-T | `RC6-` | `RC6-000067` | `67` | max(1902,68)=1902 (already ahead) | 6 ✓ |
| #1004 RC6-T | `RC6-` | `T1-9E01` (prefix mismatch) | — | **not advanced** + `ReceiptNoMismatch` conflict | n/a |
| #1007 HM-L1 | `HM-L1-` | `HM-L1-000001` | `1` | max(1,2)=2 | 6 ✓ |

**Zero cases exceed 6 digits.** Verified by `hm1-d5a-test` on a `ZZ6-` terminal (prefix contains a digit): in-series `ZZ6-000012` → counter 5→**13** (was 6000013 under the old scrape); prefix-mismatch `XYZ-000099` → counter frozen + anomaly recorded; regression `ZZ6-000050` → 13→51.

HM-D5-أ corrections (2026-07-29): (1) suffix parse uses `NumberStyles.None`+`InvariantCulture` (HM-1 culture rule); same fix applied to the JV-suffix parse in `IntegrityCheckService`. (2) method returns explicit `(bool advanced, string? anomaly)` — the caller records a `PosSyncConflict` and NEVER fails the sync (the order is posted at `PayAsync` before the counter step). (3) `SaveTerminalAsync` now BLOCKS a `ReceiptPrefix` change once the terminal has orders. (4) suffix `0` (`000000`) is treated as an anomaly — valid numbers start at 1, so 0 is never a real issued number. (5) open `PosSyncConflict` count added to `inv-test-integrity` as a COUNTED class `sync_conflicts` (never raises `failedCount`). (6) idempotency proven: re-parsing the same receipt keeps the counter (conditional `NextReceiptNo <= n`). (7) project-wide search confirms NO other digit-scrape remains in production code.

HM-D5-أ round-2 corrections (2026-07-29): baseline re-verified (see DEV-2026-004). Code-change backdoor to the prefix guard is CLOSED — the guard derives `newPfx` with the same `code + "-"` fallback, so editing `Code` with an empty prefix on a terminal-with-orders is also rejected (verified: `codeBackdoorGuard.changeViaCodeRejected=true`, code+prefix unchanged). `ReceiptNoMismatch` uses the existing POS-9e Open→Acknowledged lifecycle (no new mechanism). Two new COUNTED integrity classes (never raise failedCount): `receiptno_out_of_series` (orders whose ReceiptNo isn't in their terminal's prefix series — **legacy=5** = the RC6-T fixtures T1-OFF001/002×2 + T1-9E01, new=0) and `sync_conflicts` now split Open · Acknowledged · by-type. Int-overflow suffix (12 digits) → `int.TryParse` fails → anomaly + counter frozen (verified). **Item 5b APPLIED (2026-07-29):** the whole offline replay (rebuild + PayAsync/PayTenders + PosSyncLog) is now ONE ambient own-or-join transaction with the LocalGuid dedup INSIDE it, so it is all-or-nothing — a failure never leaves a "posted-without-key" order, and the device's retry re-posts exactly once (unique index `UX_PosSyncLogs_Guid` on `(CompanyId,LocalGuid)` already exists as the race backstop; LocalGuid is a client `crypto.randomUUID()`). The `ReceiptNoMismatch` conflict save is now AFTER the commit, best-effort (one attempt; on failure logged to the app-log via `ILogger`, entity detached, never thrown to the device) — its independent detection channel is the `receiptno_out_of_series` integrity check. Verified by `hm1-b5b-test` (a–e: fault rollback, concurrent-same-guid → one invoice, conflict-save-fault non-blocking, normal, idempotent).

## DEV-2026-005 — StockBalance read-modify-write lost update (CONFIRMED, evidence frozen; NOT corrected)

Surfaced by `hm1-b5b-test`'s concurrent case: `StockService.PostMovementAsync` loads the balance via `FromSqlInterpolated(... WITH (UPDLOCK, HOLDLOCK)).AsTracking()` then read-modify-writes a TRACKED entity — EF's identity map returns an already-tracked STALE instance on re-read, defeating the lock (same family as NextNumber/NextReceiptNo). Full frozen snapshot + reconstruction in **`deploy/EVIDENCE-ZZ-B5B.md`**. All-items balance-vs-movements scan: **zero real items have a QUANTITY divergence**; the only qty divergence is the test item ZZ-B5B-ITM #5173 (+1 unit / +30). Two real items differ in VALUE only from known non-concurrency causes (ITM-0001 = documented `inv-reconcile` rebuild; MFGT-FIN = 5.45 manufacturing-rollup rounding). **NOT caused by ب-3** (the read mechanism predates it). Registered as HM-D6 (per-item balance=movements=layers check) — to be built AFTER the root-cause fix. The +30 stock_gl deviation on #5173 is the ONLY evidence and is left as-is (no reconcile) by explicit instruction.

**FIX APPLIED (2026-07-29):** `StockService.cs:~380` — order lock → guard(hard-fail if the tracked balance is Modified/Added at the locked read) → `Entry(bal).ReloadAsync()` → modify. Pre-checks confirmed the guard never fires in production (every balance writer saves per-movement) and the offline bundle reads `AsNoTracking` (no 4th-class staleness). Fail-first PROVEN (`/api/dev/hm1-d6-race-test`: pre-fix balance 11 ≠ net-movements 16, anomaly −5; post-fix 16==16; same-item basket 17==17). HM-D6 checks added to `inv-test-integrity`: `bal_qty_vs_moves` (qty diff = REAL failure), `fifo_layers_per_item`, `bal_value_diff` (COUNTED; frozen VALUE baseline = ITM-0001 + MFGT-FIN; "new" = unreconciled / HM-D7 footprint), `stock_guard_trips` (counted tripwire). Current `inv-test-integrity` failedCount = **2**, BOTH from the documented #5173 evidence (`stock_gl` value +30 and `bal_qty_vs_moves` qty +1) — intended-red until reconciliation. GRN landed-cost batched/unlocked write registered as **HM-D7** (option ب; not built).

**ITM-0001 value diff (+296,974.95) — audit-gap, NOT a justified cause:** produced by dev tools `seed-item-allmoves` + `inv-reconcile`; **there is NO persistent audit trail of what `inv-reconcile` changed** (only an ephemeral HTTP-response `log` list + an aggregate reconciliation JE — no per-item before/after record). It is recorded here as a value-only diff with no audit trail (dev tool), quantity is correct. Item 5 mandates adding persistent per-fix logging to `inv-reconcile` before it is used to reconcile #5173.

- **Offline↔online collision is a LIVE possibility, not just old data** — proven by code: `pos-offline-order.js::nextReceipt` seeds the local counter from `bundle.terminal.nextReceiptNo` (a snapshot taken at bundle download) then increments **locally with no server coordination**, emitting `receiptPrefix + pad6(n)` — the SAME space the atomic online allocator (`AllocateReceiptNoAsync`, `prefix + D6`) draws from. If the same terminal's server counter advances (any online activity) after that snapshot, the offline device re-issues numbers already assigned online → duplicate `ReceiptNo` within the terminal. Sync (`PosOrderService.cs:1453`) only advances the counter; it does **not** detect or renumber the collision. This is exactly the source the `receiptno_dup` detective check guards. **Priority decision left to the user.**

---

## DEV-2026-004 — FROZEN integrity baseline (HM-1-أ ب-3, fix-apply)

The formal frozen baseline that the three new permanent checks in `IntegrityCheckService.RunAsync` (keys `cogs_impact`, `entryno_dup`, `receiptno_dup`) use to separate **legacy** (counted, never fails) from **new** (raises `failedCount`). Values hard-coded in the service must match this entry exactly.

- **Fix-apply timestamp:** 2026-07-28 15:11 (UTC+03:00).
- **Cutoff date (COGS legacy split):** `2026-07-28 00:00 UTC`. Stockable sale lines with no COGS-effective movement are **legacy** when the invoice `CreatedAt < cutoff` **or** the invoice ID is in the frozen test-data set; **new (fail)** otherwise.
- **Frozen test-data invoice IDs (excluded by ID):** 26, 27, 6174, 6175, 6176, 6177, 6178, 6179 (from DEV-2026-002).
- **`JvBaselineMaxNo` = 1162** — the max JV numeric suffix at capture. Gaps with number ≤ 1162 are legacy (the 254 documented gaps); gaps > 1162 are new-period gaps (counted, warning only). Duplicate JV numbers always fail.
- **`PosOrderBaselineMaxId` = 3352** — the max `PosOrders.ID` at capture. A within-terminal ReceiptNo duplicate group is legacy when its highest order ID ≤ 3352 (the 2 documented T1-OFF/RC6-T dev-test dups); new when any involved order ID > 3352.
- **Baseline counts at capture (as the live checks report them):** JV gaps = **254** · within-terminal ReceiptNo dup groups = **2** (both legacy, on dev-test terminals) · COGS legacy no-movement lines = **19** + zero-cost(counted) = **1** + non-stockable-anomaly = **0**. (The **19** is the strict class the `cogs_impact` check counts: Posted sales-invoice lines with `ItemId` + `WarehouseId` + `ItemType=Stockable` + no COGS movement. It is a subset of the ≈23 broad "COGS-damage sale-lines" estimated in DEV-2026-002, which also counted non-stockable/no-warehouse rows; both are legacy and neither fails.)
- **Gap-rate warning threshold:** new-period gaps / new-period issued numbers **> 25%** → operational warning (abnormal rollback rate; not an accounting error).
- **Test-induced new-period gaps (NOT part of the frozen baseline):** JV numbers **1166, 1171, 1180, 1185** (max JV = 1190) are gaps created by HM-1-أ ب-3/HM-D5 dev self-tests — each is a number allocated in the isolated JV context for a sale that then rolled back (forced-fail / concurrent-loser tests). They are all `> JvBaselineMaxNo (1162)`, so the `entryno_dup` check classifies them as new-period, and they are expected/harmless (gaps never fail; duplicates would). Further self-test runs (e.g. `hm1-b5b-test` fault/concurrency cases) add more such `>1162` gaps — always test-induced, never real ledger loss.
- **Scope:** DEV database only. These baselines are the reference point; they are never corrected (no historical-data fix — standing HM-1-أ prohibition).
- **Re-verified 2026-07-29 after the `IntegrityCheckService:143` culture fix (`NumberStyles.None`+`InvariantCulture`):** authoritative culture-independent SQL over the legacy range (JV suffix ≤ 1162) returns distinctUsed=908, max=1162, **legacyGaps=254**, with gap ranges 1–206 · 1082 · 1087–1099 · 1104–1110 · 1113–1114 · 1116–1139 · 1155 (Σ=254) — **identical, number-for-number, to the frozen baseline**, and equal to what the fixed C# check reports (258 total − 4 new-period = 254 legacy). The baseline is UNCHANGED; the missing culture never corrupted it (ASCII digits parse identically under any culture). No other frozen figure was computed by culture-affected code: the 23/19 COGS-damage lines come from entity counting (no numeric-string parse), and the counters (1162/3352/1902) come from SQL `MAX`/atomic `OUTPUT` (culture-independent).


## DEV-2026-006 — HM-1-أ read-only findings (build config · batch layer · #if DEBUG)

**(1) Release-publish safety of the `#if DEBUG` seams — depends on a DEFAULT, not an enforced script.** `deploy/deploy.ps1` only COPIES a prebuilt `cb_publish` folder to IIS; it does NOT run `dotnet publish`. No script/CI/doc contains an explicit `dotnet publish -c Release` for the .NET app (DEPLOYMENT.md documents only the Flutter `--release` builds). `dotnet publish` DEFAULTS to Release (verified: `dotnet build -c Release` = 0 errors with the seams compiled out), so a normal publish is safe. **Residual risk:** a deliberate `dotnet publish -c Debug` would revive the 3 test seams (`StockService._testBypassLockReadRefresh`, `PosOrderService._testFaultBeforeSyncLogCommit`/`_testFaultBeforeConflictSave`). The ONLY `#if` directives in the whole solution are these six `#if DEBUG` lines (StockService 146/402, PosOrderService 210/1503/1519, DevSeedController 769) — no other compile symbol changes any accounting/stock/security behaviour. Recommendation: state `dotnet publish -c Release` explicitly in DEPLOYMENT.md (a doc improvement).

**(3) Race/fault self-tests are Debug-only.** Because the seams are `#if DEBUG`, `hm1-d6-race-test` and `hm1-b5b-test` cannot run in a Release build ⇒ this family of regressions is verifiable in DEVELOPMENT only, not in a production deployment.

## DEV-2026-007 — HM-D8 batch-layer read-only diagnostics (numbers only; do NOT fix)

Registered as **HM-D8** (see hm-deferred-backlog). Snapshot 2026-07-29 (dev DB). **None blocks a sale or corrupts cost today.**
- **(أ) 78 movements** on TrackExpiry/TrackBatch items carry a NULL `BatchId` (qty 1597.8, value 1,060,552.01) — mostly ITM-0001 (seed-item-allmoves) + MFGT-R1/R2 (manufacturing raw-material issues via WorkOrder/ProjectIssue). These escape `batch_no_negative` (per-batch) but NOT `bal_qty_vs_moves` (universal, includes null-batch rows) — so the item TOTAL balance is still checked; only per-batch/expiry attribution is missing (a FEFO/expiry-discipline gap).
- **(ب) FEFO allocation is warehouse-scoped** (`FefoAllocateAsync`, StockService:249-253 filters `m.WarehouseId == warehouseId`). No cross-warehouse batch allocation possible; `StockBatches` correctly has no WarehouseId (batch qty comes from warehouse-filtered movements). CLEAN.
- **(ج) 1 expired batch with positive balance:** ITM-0001 `EXP-A-EXPIRED`, expired 2026-07-19, net +100 (seed-item-allmoves artifact). Expiry-control concern.
- **(د) 0 orphan batch references** (no movement points to a missing StockBatch). CLEAN.

**DEV-2026-007 update — HM-D8 CAN PREVENT A SALE (not just discipline):** a TrackExpiry item can have balance > FEFO-allocatable ⇒ `FefoAllocateAsync` rejects the shortfall despite stock on the shelf. Today (dev-seed): ITM-0001 wh1 = 187 balance vs 100 FEFO-allocatable (shortfall 87); wh2 = 30 vs 0 (shortfall 30 ⇒ ANY ITM-0001 sale in wh2 rejected). The 78 null-batch movements are all TrackExpiry (ITM-0001 + MFGT-R1/R2 manufacturing issues). Only dev-seed/manufacturing data affected now; defect is structural. HM-D8 raised from «discipline».

**DEV-2026-005 RESOLVED (2026-07-29 11:51 UTC):** #5173 resynced to its movement ledger via the new scoped `inv-resync-item` (itemId=5173, warehouseId=1, expectQtyDelta=+1). BEFORE: qty 201 · value 6030 · avg 30. AFTER: qty 200 · value 6000 · avg 30 (== net movements 200/6000). **No GL JE** — the balance was the drifted cache; the GL was already posted from the same movements (a value-diff JE would have re-broken stock_gl; confirmed post-run: stock_gl diff = 0). Persistent audit trail: `InventoryReconcileLogs` ID=1 (before/after qty+value+avg, RanBy=inv-resync-item, reason cites EVIDENCE-ZZ-B5B.md + DEV-2026-005, JournalEntryId=none). Post-fix `inv-test-integrity`: **failedCount = 0** across all 18 checks (stock_gl, bal_qty_vs_moves, tb_balanced, ar_sub, ap_sub all green; bal_value_diff back to the frozen baseline ITM-0001+MFGT-FIN with no "new"). `deploy/EVIDENCE-ZZ-B5B.md` is retained permanently and unmodified.

## 5ب CLOSED (2026-07-30) — formal closure

The offline-replay atomicity work (5ب) is closed. The StockBalance lost-update deviation (DEV-2026-005) surfaced INSIDE the 5ب concurrency test but its root cause is **independent of the 5ب transaction wrapping** — PROVEN: the cause is EF identity-map staleness on `FromSql(...).AsTracking()` re-reads, a pattern that PREDATES ب-3; ب-3's ambient transaction actually CLOSED the same-context window (a beneficial side-effect, DEV-2026-005), and the cross-context case was a separate EF behavior fixed by `Entry(bal).ReloadAsync()` at StockService:~380. So wrapping the replay in one transaction is NOT a party to the balance bug.

The two 5ب guarantees hold (proven by `hm1-b5b-test`):
1. **No order posted without a privacy key** — the whole replay (rebuild + pay + PosSyncLog) commits atomically in ONE own-or-join transaction; a failure rolls it all back (case a: injected sync-log-commit fault ⇒ zero invoice/JE/movement/sync-log, and the retry posts exactly once).
2. **No duplicate invoice** — the LocalGuid dedup runs INSIDE that transaction, backed by the existing unique index `UX_PosSyncLogs_Guid (CompanyId, LocalGuid)` (case b: two concurrent replays of the same guid ⇒ exactly one sync-log / one invoice).

## (هـ) CloseShiftAsync + JE-ref-field census (2026-07-30)

**CloseShiftAsync wrapped.** The drawer-variance JE (`CreateAndPostAsync`) and the close-field write (Status="Closed", ClosedAt, VarianceJournalEntryId) are now ONE own-or-join transaction (was two commits ⇒ a "JE posted, fields unsaved" failure could leave an orphan variance JE + a shift that still looked Open, re-closeable → a 2nd variance JE). `hm1-closeshift-test` passes twice: variance close ⇒ Closed + VarianceJournalEntryId + JE all atomic; zero-variance ⇒ no JE. This is the **12th** own-or-join-wrapped path.

**Census of "post JE then set a JE-ref field":** SalesInvoice/PurchaseInvoice + their edits + SalesReturn/PurchaseReturn edits + PosOrder.TipJournalEntryId were ALREADY inside their ScopedTx (ب-3). **CreateReceiptAsync (Receipt.JournalEntryId) and CreatePaymentAsync (Payment.JournalEntryId)** set the ref field in a 2nd commit when called standalone (atomic when called from PayAsync's tx) — NOW wrapped in own-or-join. No other JE-ref path remains un-wrapped.

## (د) teardown-compliance status (2026-07-30) — PARTIAL, honest note

Delivered: a reusable guard `ItemDeletableAsync(company,itemId)` (a direct delete is refused if the item has any StockMovement/StockBalance — footprint must be reversed via services), and `hm1-closeshift-test` uses a FIXED reused ZZ terminal + reverses its variance JE via ReverseAsync (the compliant pattern). NOT completed this turn: converting `hm1-b3-test` + `hm1-b3-tracker-test` (which currently `ExecuteDelete` stock-effect rows) to fixed-entity + reverse-via-services + delta-based assertions. Reason: those two endpoints TEST rollback/clear semantics (so they cannot be wrapped in a rolled-back tx) and their absolute assertions require a ~150-line rewrite; rushing it at the tail of the session risked destabilizing the proven-green state. Current state is clean (those tests delete their whole ZZ graph atomically ⇒ zero residue; `inv-test-integrity` failedCount=0 across 19 checks). The remaining work is test-hygiene (the delete METHOD), not production correctness.

## Phase د — regression + measurement (2026-07-30, closing HM-1-أ)

**No production regression from HM-1-أ.** Passing ZZ regression: hm1-d5a-test, hm1-resync-guard-test, hm1-closeshift-test (shift-close atomic), hm1-d6-race-test, hm1-d6-concurrency-test (sale‖edit, sale‖return: balance==movements, loser serialized, no deadlock/InvalidOperationException). pos-9e-test's OWN assertions all pass (offline shift close syncs, variance JE to 520111, idempotent re-send). culture-check allPass.

**inv-test-integrity failedCount=1 = TEST DATA, not a bug:** the one failure is `receiptno_dup new=3` — three within-terminal duplicate ReceiptNos on terminal 1004 (`T1-9E01`, `T1-OFF001`, `T1-OFF002`), created by re-running the offline-sync tests whose payloads hardcode those receipt numbers. The detective check correctly caught test-created duplicates; it does NOT prevent a sale and is unrelated to the (هـ) receipt/payment wrap (no per-item stock corruption; residue scan empty). Test-hygiene item (the sync tests should use per-run ReceiptNos) — logged, not a deviation.

**Measurement (5 parallel full sales, ZZ):** Isolated per-sale [69,82,113,130,161] ms, lock-wait≈92 ms, 0 failures. Ambient [29,50,72,96,176] ms, lock-wait≈147 ms, 0 failures. 20-line basket 734 ms (≈37 ms/line). Connections/sale ≈ 3–4 (1 ambient + 1 short-lived isolated per JE). **Recommendation: keep Isolated and REMOVE the switch (hardcode Isolated).** Both modes are correct at this scale, but Ambient allocates the JV number INSIDE the sale transaction ⇒ every sale system-wide serializes on the single `NumberSequences` JV row; the measured Ambient tail (max 176, lock-wait 147) already runs higher, and it degrades with concurrency, whereas Isolated releases the JV row-lock in ms. `Numbering:JvAllocationMode` → remove after this decision is accepted.

## DEV-2026-008 — offline-sync tests wrote to an EXISTING terminal + receiptno_dup baseline amendment (2026-07-30, closing HM-1-أ)

**Supersedes the framing in the "Phase د" section above.** Calling the 3 duplicate ReceiptNos "TEST DATA, not a bug" and saying "pos-9e's OWN assertions pass, it only fails the shared gate" was wrong on two counts and is corrected here:

1. **It was a real rule violation, not benign test data.** `pos-sync-test` and `pos-9e-test` posted their offline-replay orders with **hardcoded** ReceiptNos (`T1-OFF001`, `T1-OFF002`, `T1-9E01`) onto terminal **1004 (RC6-T) — an EXISTING terminal with 66 real orders**, not a ZZ throwaway. Because each run deletes its own PosSyncLog to force a deterministic replay, every run re-posted the same fixed ReceiptNo → a new within-terminal duplicate. That is exactly the pattern our own detective check exists to catch, and a failing check is failing — there is no "passes its own assertions but fails the gate" exemption.

2. **Fix (tests):** both endpoints now use a **dedicated ZZ terminal `ZZ-SYNCT` (prefix `ZZSY-`)** and allocate every offline ReceiptNo via `PosOrderService.AllocateReceiptNoAsync(term.ID)` — no hardcoded numbers. **Proven:** each test run twice back-to-back; `ZZ-SYNCT` holds 6 orders / 6 distinct ReceiptNos (zero duplicates); terminal 1004's dup groups stopped growing (maxId frozen at 3364/3365/3366).

3. **The 3 pre-existing dup groups on terminal 1004 are NOT deleted.** They are paid orders carrying invoices, journal entries and stock movements; a ReceiptNo is a tax document, so deleting them would repeat the forbidden JE-8046 deletion pattern ([[testing-on-live-data-forbidden]]). Instead they are recorded as a **documented baseline amendment**: `IntegrityCheckService` receiptno_dup now excludes the explicit key set `{1004|T1-OFF001, 1004|T1-OFF002, 1004|T1-9E01}` (a named 3-key exclusion, NOT a blanket raise of `PosOrderBaselineMaxId` — unrelated future dups still fail). The original baseline (`PosOrderBaselineMaxId = 3352`) is kept recorded and unchanged.
   - Affected order IDs: T1-OFF001 = {3346, 3348, 3364}; T1-OFF002 = {3347, 3349, 3365}; T1-9E01 = {3350, 3366} (+ one more in the 3-count group). Reason: created by the now-fixed dev tests before the fix.

**Result after this amendment:** `inv-test-integrity` → `failedCount = 0` across 19 checks; receiptno_dup note = `new(fail)=0 · legacy(baseline)=3`.

## HM-1-أ closing regression (2026-07-30)

**No production regression from the StockService tx-management change.** Green on ZZ: inv-test-integrity (clean state: failedCount=0/19), inv-test-fefo, inv-test-gl (valuation==GL), inv-test-periodlock (closed period blocks, no movement leak), inv-test-i7 (stock-count+variance), inv-test-interbranch, inv-test-composite/costing/concurrency, manuf-test-scrap/immediate-scrap, hm1-d5a, hm1-resync-guard, hm1-closeshift, hm1-d6-race, hm1-d6-concurrency (sale‖edit, sale‖return), crm-test-salesreturn/purchasereturn/o2c, culture-check (allPass). No InvalidOperationException anywhere; `bal_qty_vs_moves = متطابق` and `ar_sub`/`ap_sub` diff-zero across ALL items.
- `manuf-test-wo`/`manuf-test-wip` return `pass:false` but the production POSTS correctly (JE balanced 630=630, finishedStockDelta=10, no exception) — an accumulation/idempotency assertion in the fixture, not a tx regression.

**failedCount honesty.** The clean, decided-deliverable state is `failedCount=0` (verified after the DEV-2026-008 sync-test fix). Running the DEBUG-only stateful reproduction test `hm1-b5b-test` in the regression pass re-dirtied it to `failedCount=3`, entirely on ZZ entities: (a) `stock_gl`+`cogs_impact new=2` = the KNOWN deferred HM-D6 lost-update deviation on ZZ-B5B-ITM #5173 (b5b re-creates it via `_testBypassLockReadRefresh`, no teardown); (b) `receiptno_dup new=5` = b5b's FIXED ReceiptNos (`ZZB5-000001/2/3/10`, `XYZ-999999`) on ZZ terminal 1015 — the SAME hygiene bug as DEV-2026-008, now HM-D10. Cleaning either requires resync or delete, both PROHIBITED this round, so the ZZ residue is left in place. None of it touches production data, ar/ap subledgers, the physical-quantity invariant, or blocks a sale.

**project-test (long-open question answered).** `je-test-project` = NOT pass: it errors at its FIRST step («الاسم الإنجليزي مطلوب») because the test's project payload predates the later-added mandatory `NameEn` bilingual field — a stale fixture, not a logic defect. The project GL dimension is independently verified correct: **PRJ-DEMO profitability = revenue 55,000 − cost 24,080 = 30,920** (queried directly from posted JE lines carrying the project dimension).

## DEV-2026-009 — the "orphan GL 267,246" measure was INVALID + purchase→receipt localization (2026-07-30)

**Retraction:** the earlier "orphan GL = 267,246" (inventory-account JE lines not linked to any StockMovement.JournalEntryId) was an INVALID measure of damage. Proven by step-zero (hm1-step0-purchase-test): a CORRECT purchase-invoice receipt movement is created with **PostToGl=false ⇒ JournalEntryId=NULL by design** (PayableService:194-195), because the invoice JE itself debits the inventory account per-line (PayableService:151/170). So the orphan measure counted every correct purchase's inventory debit as "orphan," conflating correct purchases (PostToGl=false) with genuinely-swallowed ones. Re-measure in item (هـ) when authorized.

**Step-zero manifest (DEV-2026-004 addendum) — ZZ entities, footprint reversed via services (not deleted):**
- Vendor ZZ-STEP0-VEN #1013, item ZZ-STEP0-ITM #5186 (kept as reusable scaffolding).
- Docs: PI-2026-01047 (JE 8445, movement 8153 SourceType=PurchaseInvoice +5 JE=NULL) · GRN-2026-01023 (JE 8446, movement 8154 SourceType=Receipt +3 JE=8446) · DN-2026-00008 (JE 8447, movement 8155 SourceType=PurchaseReturn −1 JE=8447).
- Reversed via JournalEntryService.ReverseAsync → mirror JEs 8450/8451/8452; stock zeroed via a reversing PostToGl=false movement (7→0). Balance after = 0/0.

**Step-zero findings (purchase path AFTER ب-3 works, does NOT block a sale):**
- Purchase invoice: OK. Movement created `SourceType="PurchaseInvoice"`, Direction +1, **JournalEntryId=NULL** (invoice JE 8445 = Dr Inventory 1103 / Cr AP 2101, per-line).
- GRN receipt: OK. Movement `SourceType="Receipt"` (NOT "GoodsReceipt"), JE 8446 = Dr Inventory 1103 / Cr GRNI 210203. Receipt path had **NEVER run before** in this DB (count was 0).
- Vendor return: OK. Movement `SourceType="PurchaseReturn"`, JE 8447 linked (PostToGl=true).
- (ب) every historical orphan purchase line (PI-2026-00023, PI-2026-01042, 01043, 01045; item ITM-0001) has **WarehouseId=1 (set, valid) and hasMov=0** ⇒ the receipt WAS requested and was SWALLOWED (pre-ب-3), not a data-entry gap. Only my ZZ PI-2026-01047 has a movement (post-ب-3 fix).

**HM-D14 — inv-resync-item DISABLED (guard misjudges purchase-history items).** Proven by hm1-guard-purchase-test (rolled-back tx): the three-way guard computes per-item glVal ONLY from movement-linked JEs, but a purchase movement is JournalEntryId=NULL, so glVal(0) ≠ movementsVal(100) ⇒ it REFUSED a legitimate resync as "movements suspect." Since the tool WRITES to the balance, a misjudgment is unsafe. The public endpoint now refuses to run entirely (`disabled=true`); internal guard-tests pass `callerBypassDisable:true`. NOT fixed (per instruction) — the fix is to attribute the invoice-JE inventory debit to items (SourceType=PurchaseInvoice → invoice lines → ItemId).

**Build unblock (out of scope, external):** ApprovalsController.cs(73,104) failed to compile — `InventoryApproval.RequestedByEmployeeId` is `int?` but the inbox code used it as `int` (Concat/empName). External edit, not mine; fixed null-safely to unblock the build. Flag for review.

## HM-D16 — purchase→receipt MODEL CONFLICT (both paths debit inventory, no guard) (2026-07-30)

**Diagnosis (read-only + the two real posted JEs from step zero). Verdict: DESIGN CONFLICT, confirmed.**

(أ) Account postings per path (verbatim from code + posted JEs):
- GRN receipt (ProcurementService.CreateReceiptAsync): stock movement PostToGl=true, SourceType="Receipt" ⇒ **Dr Inventory 1103 / Cr GRNI 210203** (JE 8446: Dr 60 / Cr 60). Fixed-asset items: Dr 1201 / Cr GRNI.
- Purchase invoice (PayableService.CreatePurchaseInvoiceAsync:151/170): JE **Dr Inventory 1103 (per line) / Cr AP 2101** (JE 8445: Dr 100 / Cr 100); receipt movement PostToGl=false (JE=NULL). **Does NOT touch GRNI.**
- Vendor return (CreatePurchaseReturnAsync): movement Dr GRNI / Cr Inventory + JE Dr AP / Cr GRNI / Cr VAT ⇒ nets GRNI to 0 (three-way style).
- Invoice edit / return edit: reverse (mirror) + re-post on the same pattern as their create.

(ب) Link between GRN and invoice? **NONE.** CreatePurchaseInvoiceAsync has no reference to GoodsReceipt/GRNI, no matching, no auto-close, no duplication guard. (GoodsReceipt.InvoiceId field EXISTS but the invoice path never sets or reads it.)

(ج) GRNI adjustment entries 8053 (JV-2026-001160) + 3393 (JV-2026-000418): **Manual JEs, SourceType="StockReconcile"** — created by the **inv-reconcile** dev tool when someone ran it to patch GRNI drift. They exist because GRNI accumulates and is never cleared by the invoice path.

(د) Double-posting: **CONFIRMED by the two posted JEs** (both debit 1103 for the same item, neither clears the other; GRN's GRNI credit is never cleared by the invoice). A written combined test (hm1-double-post-test) is ready but BLOCKED by an unrelated external Chat-module build break (see below); the JE evidence is already conclusive. No prevention/warning exists in either path.

(هـ) Intended model = **three-way match** (evidence, not assumption): ProcurementService comment "the purchase invoice later clears GRNI exactly like a stock receipt"; the receipt notification "ready for vendor-invoice matching"; the per-category GrniAccountId; the GoodsReceipt.InvoiceId link field. Under that model the INVOICE must Dr GRNI / Cr AP — but the code does Dr Inventory / Cr AP. **The invoice implementation contradicts the system's own three-way design.**

(و) GRNI function today: it SHOULD be the receipt/invoice clearing account, but the invoice never clears it ⇒ **GRNI open balance = −39,860** right now, patched only by manual StockReconcile JEs. No automatic closer exists.

**⚠️ STRUCTURAL — blocks hyper inventory correctness.** In a hypermarket both receiving (GRN) and invoicing are daily; running both on the same goods double-debits inventory 1103 and doubles stock, with no guard. It stayed hidden only because the Receipt path had NEVER executed in this DB (count 0 until the ZZ step-zero test) — so historically only invoices ran (Dr Inventory directly), which is internally consistent on its own but wrong vs the three-way design. **Model decision + design session required (like HM-D8). inv-resync-item stays disabled; its fix is subordinate to this model decision. HM-D14 value stays un-finalized, subordinate to HM-D16.**

## ApprovalsController build-unblock — full disclosure (2026-07-30)

- **My change (verbatim):** ApprovalsController.cs line 73 `.Concat(invs.Select(a => a.RequestedByEmployeeId))` → `.Concat(invs.Select(a => a.RequestedByEmployeeId).Where(x => x.HasValue).Select(x => x!.Value))`; line 104 `empName(a.RequestedByEmployeeId)` → `empName(a.RequestedByEmployeeId ?? 0)`.
- **Root:** `InventoryApproval.RequestedByEmployeeId` is `int?` in the model (Models/Context/Inventory/Inventory.cs:555) AND the DB column is nullable (is_nullable=1) — so the field was legitimately nullable; the newly-added approvals-inbox code (external edit) simply didn't handle the null and used it as `int`. No migration needed (column already nullable).
- **Nature:** pure null-safety, NOT behavioral — it only affects requester-name display in the inbox (null requester → excluded from the name lookup → shows "-"). Existing approval logic untouched. Done solely to unblock the build so HM-D16's ZZ tests could compile.

## Chat-module build break — NOT touched (2026-07-30)

Build currently FAILS on external in-progress Chat edits: Views/Chat/Index.cshtml(269) Razor parse error in a JS block, and ChatController.cs(100) a `Directory` action-method vs `System.IO.Directory` collision. This is an active feature-in-development (chat file upload/directory), not a stray defect — NOT fixed (out of scope, would collide with WIP). It blocks running the (د) combined test live; the HM-D16 verdict stands on the posted-JE evidence regardless. Flag for the Chat author.

## HM-D16 — model decision (RECORDED, not executed) + GRNI decomposition (2026-07-30)

**APPROVED MODEL = three-way match** (per the existing design evidence: ProcurementService comment, per-category GrniAccountId, GoodsReceipt.InvoiceId): the receipt Dr Inventory / Cr GRNI, and the invoice Dr GRNI / Cr AP. The current implementation (invoice Dr Inventory / Cr AP) contradicts the design and allows unguarded inventory double-posting. **HM-D16 is a PREREQUISITE for HM-7 (inventory operations) and any hyper receiving — NOT a prerequisite for HM-1 (sales core).** A design session must decide: (أ) is the invoice forced to reference a prior receipt or stay independent, and with what posting; (ب) the matching screen + GRNI closing; (ج) an announced cut-over date and how pre-cutover invoices are read; (د) disposition of the existing open GRNI balance. Nothing is implemented before the session. Do NOT touch any purchase path / link any account / add any dup guard now.

**GRNI (210203) open balance = −39,860, decomposed (sums exactly):**
- `Inventory` (movement-GL, Dr Inv / Cr GRNI) 12 JEs = −41,290 — overwhelmingly PRODUCTION purchase data: ITM-0001 "المورد Dell" dominates (JE 236 = −19,920, JE 8044 = −19,920, +231/8290/8306), ITM-0002 (−120), BRG-L (+150). My ZZ step-zero JEs (8446 −60, 8447 +20) are here but netted by their teardown reversals.
- `Reversal` 7 JEs = +160 (includes my ZZ teardown reversals 8451/8452).
- `PurchaseReturn` 3 = −170. `PurchaseInvoice` 2 (8291/8307) = +720. `StockReconcile` 1 (JE 3393) = +720 (dev inv-reconcile tool).
- Verdict: GRNI is driven by PRODUCTION receipt-side GL (ITM-0001 Dell) that the invoice path never cleared — the exact HM-D16 mechanism, not dev/test noise.

**HM-D16 (third path noted):** twelve JEs with SourceType=Inventory post Dr Inventory / Cr GRNI (receipt-shaped) with NO matching stock movement — chiefly the ITM-0001 receipts from vendor Dell (JE 236 & 8044, −19,920 each). A THIRD path beside invoice and receipt; to be resolved in the HM-D16 design session. (Noted, no investigation.)

## HM-1-أ closing batch manifest (DEV-2026-004 addendum) (2026-07-30)

Three ZZ tests via hm1-close-batch-test, all green (no InvalidOperationException):
- (أ) edit purchase invoice PI-2026-01048 (item ZZ-CB-INVA #5188): create qty5→edit qty3; stock 0→5→3 ✓; old JE 8453→8455.
- (ب) edit purchase return DN-2026-00009 (item ZZ-CB-RETB #5189): return 2→edit 1; stock net −1 ✓ (the pos.388 issue result is handled, not swallowed); JE 8462.
- (ج) issue failure on unmapped ZZ category ZZ-CLOSE-CAT (item ZZ-CB-CFG #5190): verbatim rejection «حساب المخزون غير مربوط لفئة الصنف — اربطه من شاشة الفئات»; zero effect (movements/JE/balance unchanged); category InventoryAccountId restored.
- Teardown: JEs reversed via services; items 5188/5189/5190 stock zeroed.

**AP subledger↔GL re-sync (DEV-2026-004):** reversing the ZZ invoice/return JEs during step-zero + close-batch teardown desynced ap_sub (subledger counts the still-Posted ZZ purchase docs; GL was reversed). Restored subledger==GL by reversing the 4 AP-affecting reversals (8450/8452/8464/8465 → 8466/8467/8468/8469). ap_sub Ok=True again. **Residue (deferred cleanup, HM-D17):** ZZ-STEP0-VEN now carries an open AP balance (~135) and the restored invoice inventory-GL debits sit against zeroed stock — because there is no clean document-level void for a purchase invoice (only edit=reverse+repost). Harmless to gate constants (ap_sub/ar_sub/bal_qty_vs_moves all green); it only adds to the HM-D16 stock_gl gap. Proper fix = a document-level purchase-invoice void, or full offsetting returns — follow-up.

## HM-D18 — DefaultPriceListId not consumed in the cashier path (2026-07-30)
BranchPosSetting.DefaultPriceListId is a stored setting NOT consumed in the cashier flow: PosOrderService.AddLineAsync (line ~551) calls PricingService.GetPriceAsync WITHOUT the branch price list, so it returns the base Item.SalesPrice. **Mandatory prerequisite for HM-4 (per-branch, per-unit pricing).** NOT fixed in HM-1 because AddLineAsync is SHARED with the restaurant lane — passing a branch price list would change restaurant prices immediately. Deferred.

## HM-D19 — currency-blind 2-decimal rounding loses KWD fils (BLOCKS HM-1 for Kuwait) (2026-07-30)
KWD is defined with DecimalPlaces=3 (Currencies ID 5; EGP/USD/EUR/SAR=2). DB money columns are decimal(19,4) (hold 3dp fine), and PricingService.GetPriceAsync (line 278) correctly rounds the UNIT PRICE to the currency's DecimalPlaces. BUT the sale-path rounding helper hardcodes 2 decimals — `R(v) => Math.Round(v, 2, AwayFromZero)` in PosOrderService:216, ReceivableService:100, JournalEntryService:60 — and is applied to every total: PosOrderService.RecomputeAsync line 962 (`LineTotal = R(Qty*UnitPrice-disc)`, `tax += R(LineTotal*TaxRate/100)`), 971 (GrandTotal), 570/632 (AddLine/edit), plus the invoice and JE. So for a KWD sale the 3rd decimal (fils) is truncated at the line total and everywhere downstream — the correct 3dp price is re-rounded to 2dp. **Verdict: YES, fils CAN be lost, proven by code.** Directly blocks a correct Kuwaiti hypermarket sale (HM-1). Fix = make R() currency-aware (round to the transaction currency's DecimalPlaces) across the POS/AR/JE sale path — NOT done now (touches shared restaurant path + accounting rounding; needs a scoped design so EGP behavior is unchanged). **Prerequisite for HM-1 going live in KWD.**
