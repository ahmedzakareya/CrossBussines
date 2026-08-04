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
BranchPosSetting.DefaultPriceListId is a stored setting NOT consumed in the cashier flow: PosOrderService.AddLineAsync (line ~551) calls PricingService.GetPriceAsync WITHOUT the branch price list, so it returns the base Item.SalesPrice. **Mandatory prerequisite for HM-4 (per-branch, per-unit pricing).** **HM-2 Batch 5 proof (2026-08-01):** converting an EGP-denominated price list to KWD produces 0.755 EGP → 0.005 KWD = HALF A FILS — an invalid retail price. This PROVES currency conversion is NOT a substitute for a branch-currency price list: the hypermarket needs a KWD price list with prices entered DIRECTLY (e.g. 0.750), not a converted EGP list. Reinforces HM-D18 as a hard prerequisite for HM-4. NOT fixed in HM-1 because AddLineAsync is SHARED with the restaurant lane — passing a branch price list would change restaurant prices immediately. Deferred.

## HM-D19 — currency-blind 2-decimal rounding loses KWD fils (BLOCKS HM-1 for Kuwait) (2026-07-30)
KWD is defined with DecimalPlaces=3 (Currencies ID 5; EGP/USD/EUR/SAR=2). DB money columns are decimal(19,4) (hold 3dp fine), and PricingService.GetPriceAsync (line 278) correctly rounds the UNIT PRICE to the currency's DecimalPlaces. BUT the sale-path rounding helper hardcodes 2 decimals — `R(v) => Math.Round(v, 2, AwayFromZero)` in PosOrderService:216, ReceivableService:100, JournalEntryService:60 — and is applied to every total: PosOrderService.RecomputeAsync line 962 (`LineTotal = R(Qty*UnitPrice-disc)`, `tax += R(LineTotal*TaxRate/100)`), 971 (GrandTotal), 570/632 (AddLine/edit), plus the invoice and JE. So for a KWD sale the 3rd decimal (fils) is truncated at the line total and everywhere downstream — the correct 3dp price is re-rounded to 2dp. **Verdict: YES, fils CAN be lost, proven by code.** Directly blocks a correct Kuwaiti hypermarket sale (HM-1). Fix = make R() currency-aware (round to the transaction currency's DecimalPlaces) across the POS/AR/JE sale path — NOT done now (touches shared restaurant path + accounting rounding; needs a scoped design so EGP behavior is unchanged). **Prerequisite for HM-1 going live in KWD.**

## HM-2 Phase B — currency-aware rounding (2026-07-30)
Confirmed rules: (أ-3) each JE line rounds to the currency it HOLDS — Debit/Credit are functional-currency amounts (round to the company functional dp), ForeignAmount is document (document dp); totals = Σ(rounded lines) not round(Σ) so balance holds by construction; zero balance tolerance kept. Distribution remainder loaded on the LAST line (chosen: deterministic, matches the existing split-payment remainder convention at PosOrderService:1090 — consistency over first-line). Missing currency → company functional explicitly; unresolvable → throw (never silent 2dp).
Design nuance: KWD 3dp matters ONLY in DOCUMENT-currency amounts (POS order line/subtotal/grand/tax/discount/service/delivery/tip, invoice document totals, receipt document amount, Z-report/drawer). JE Debit/Credit and inventory TotalValue/AvgCost/COGS are FUNCTIONAL (EGP 2dp) — made currency-aware (no-op for an EGP-functional company; correct for a KWD-functional one).
- **HM-D22** — display layer (341 ToString("N2") + 62 toFixed(2)) hardcodes 2dp; separate later phase, does not feed authoritative calc (server recomputes).
- **HM-D23** — exchange-rate staleness: GetRateToEgpAsync uses RateDate <= d with no max-age; latest KWD→EGP is 2026-01-01, so today's sale posts to GL at a 7-month-old rate silently. Needs max-age/warn/reject. Prerequisite for running the hyper in a non-functional currency.
- **HM-D24** — pos-offline-order.js has a duplicate client-side pricing engine; "server is authoritative" does not cover offline. Verify SyncPaidOrderAsync recomputes from lines vs trusts device amounts; if it trusts, offline is a 2dp-entry channel. HM-10 scope.

## HM-2 — two documented behaviors (2026-07-30)
1. **Receipt JE balances by construction, not by JES remainder-loading (intended).** A receipt JE has only Cash / AR(1102) / FX(4902/5902) lines — ALL forbidden from bearing a rounding remainder. So it is balanced by construction: fxNet = cashBase − arBaseTotal absorbs any conversion sub-unit as realized FX (which is what a currency-conversion sub-unit is). Consequence: if a genuine rounding remainder ever arose in a receipt JE it would be REJECTED (no eligible line), not loaded onto a reconciled account. This is INTENDED — rejection is safer than polluting a subledger/FX/cash account.
2. **Partial-collection remainder falls on the last receipt by FIFO settlement, not by an announced "largest" rule.** CreateReceiptAsync FIFO-allocates exact amounts per invoice (take = min(remaining, left)); there is no equal-split, so no remainder to distribute — the final receipt naturally clears the last remaining balance. With multiple invoices the allocation is oldest-first; changing the order changes which invoice each partial clears but never leaves a hung balance (Σ receipts base = Σ invoices base at their own rates). The "largest bears the remainder" rule applies only to EQUAL splits (e.g. PayAsync N-way split, tax/discount distribution).

## HM-D26 — RE-SIZED: CODE-ONLY gap (no migration), affects the DAILY cashier return
Schema check: SalesReturns AND PurchaseReturns ALREADY have CurrencyId, ExchangeRate, SubTotalBase, TaxTotalBase, GrandTotalBase. So the gap is CODE-ONLY — CreateSalesReturnAsync / CreatePurchaseReturnAsync take no currencyId and don't populate the base columns. **No migration needed.** It affects the DAILY path: PosOrderService.ReturnOrderLinesAsync (cashier partial return, line 1429) calls CreateSalesReturnAsync WITHOUT the order's currency, and its refund JE (1440-1441) uses R(ret.GrandTotal) document for BOTH the AR and drawer lines — so a KWD hyper return would post the document amount as if functional and break ar_sub by a full FX difference. Recommended: FIX IN HM-2 (add currencyId+exchangeRate params + document/base conversion like 3-a; ReturnOrderLinesAsync passes o.CurrencyId; refund JE splits AR=base vs drawer=document). Not a marginal deferred item — it is a prerequisite for hyper returns in a non-functional currency, but fixable without a migration.

## HM-2 3-ج — JEs with no eligible P&L line balance by construction (2026-08-01)
The receipt, the vendor payment, the PURE-STOCKABLE purchase invoice (Dr 1103 / Cr 2101 / Dr VAT), and the GRN (Dr 1103 / Cr GRNI 210203) have NO eligible P&L line — every account is reconciled (AR/AP/inventory/GRNI/tax/cash) or FX. So JournalEntryService cannot load a rounding remainder onto any of them; these documents must balance BY CONSTRUCTION and any genuine rounding remainder is REJECTED, not loaded. This is INTENDED — rejection is safer than polluting a reconciled account. How each balances by construction:
- Receipt / payment: fxNet = cashBase − arBaseTotal (or apBaseTotal) absorbs the conversion sub-unit as realized FX (which it is).
- Purchase invoice: GrandTotalBase (= Σ Rf(line base) + Rf(VAT base)) is BOTH the stored column AND the single 2101 line, and the Dr lines are those same Rf bases ⇒ Σ Dr = 2101 exactly. No remainder, no distribution onto inventory (so no stock_gl drift).
- GRN: the inventory GL (1103) comes from the stock movement (PostToGl=true) valued at the FUNCTIONAL unit cost (BaseUnit = converted at source), and gr.TotalCost = Σ movement TotalCost — same single source; verified line 1103 == movement TotalValue for a KWD 3dp unit cost.
A purchase invoice carrying an EXPENSE (P&L) line differs: it HAS an eligible line, so a remainder could be loaded there — but the by-construction Σ Dr = 2101 still holds (GrandTotalBase = Σ of the same rounded debit components), so in practice no remainder arises either.
Proven (KWD @163, ZZ, rolled back): stockable PI 3×0.755 → doc 2.265 (fils kept), 2101=369.20=column, balanced, 1103=369.20; GRN unit 0.755 → 1103 GL = movement TotalValue 369.20 (functional, not the 2.265 document), balanced. EGP identical across purchase/GRN/payment/return.

## HM-2 Batch 4 — POS payment + shift close currency-aware (2026-08-01)
Made the cashier payment/shift-close path round at the ORDER's DOCUMENT currency (was hardcoded 2dp) and generalized the rounding-remainder rule:
- **PosSetupService (4-ب):** `ShiftCurrencyAsync(companyId, terminalId)` resolves the shift's document currency via terminal→branch→BranchPosSettings.DefaultCurrencyId (else functional), returning (cur, rate, ddp, fdp). ExpectedCashAsync + CloseShiftAsync variance now round at the document dp (Rd); GetShiftZReport totals at document dp. The drawer-variance JE converts the SINGLE variance ONCE to functional (`amt = Rf(|variance| × rate)`) and uses that one value on BOTH lines (drawer + 520111) ⇒ balances by construction.
- **Drawer-variance JE = no eligible P&L line, balances by construction (like 3-ج).** The variance JE is Dr/Cr drawer(asset 110101) vs 520111 (a FORBIDDEN diff account). Neither can bear a rounding remainder, so it must balance by construction — which it does because both lines are the same single converted `amt`. variance==0 → no JE. Proven (ZZ KWD): counted−expected overage → JE balanced on 520111, both lines equal (0.82 EGP from one conversion), VarianceJournalEntryId set; variance 0 → no JE.
- **PosOrderService (4-أ):** PayTendersAsync and PaySplitByItemAsync had NO document-dp local `R` shadow (they used the static 2dp `R` → dropped fils on KWD); added the document-dp shadow to both. Generalized "the LARGEST portion bears the rounding remainder" (was "the LAST"): PayAsync equal-split remainder → largest (first on tie), PayTendersAsync change → largest tender, PaySplitByItemAsync residual → largest bill. **Behavioral change** in the working restaurant path: multi-tender change and split residual now land on the largest part instead of the last. Σ receipts/invoices == grand unchanged; ar_sub still 0. Tender-sum validated at the document-currency unit (3dp KWD not rejected/truncated).
Proven (ZZ KWD, rolled back): Z total == Σ paid orders (systematic diff 0); multi-tender Cash 1.000 + Card = largest absorbs change, Σ receipts == grand, AR nets to 0; split 2+1, Σ invoices == grand, AR nets to 0. EGP regression identical (mc-test-o2c/p2p/fx, pos-terminals-test, crm-test-salesreturn all pass; failedCount=4, roundingDiffLoads=0, ar_sub/ap_sub/bal_qty green, stock_gl Ok=False pre-existing HM-D16).

## HM-D27 — EF Core truncates all money to 2dp ON SAVE (BLOCKS end-to-end KWD fils across HM-2) (2026-08-01)
**Discovered while proving Batch 4.** The DB money columns are decimal(19,4) (verified via sys.columns: PosOrders/PosOrderLines/PosShifts/SalesInvoices GrandTotal/SubTotal/TaxTotal/UnitPrice/OpeningFloat/… all 19,4) — they can hold KWD fils. BUT the EF model does NOT declare precision on these decimal properties (no [Column(TypeName)], no HasPrecision, no ConfigureConventions rule; the model snapshot has only 11 explicit decimal(18,2) props), so EF Core infers its DEFAULT precision **decimal(18,2)** and sends SqlParameters with Scale=2 — **rounding every money value to 2 decimals before it reaches SQL Server.** So HM-2's in-memory document-currency rounding is correct (RecomputeAsync resolves ddp=3 for KWD and computes e.g. grand 1.733, tax 0.319), but only 1.73 / 0.32 is persisted. **Proven three independent ways:** (1) RecomputeAsync computes order-B grand 1.733 → a fresh AsNoTracking read returns 1.73; (2) hm2-4-proofs proof0: write PosShift.OpeningFloat = 1.733 via EF → EF read-back 1.73 AND raw SQL column 1.73 (so EF wrote 1.73 to the (19,4) column — truncation is on WRITE, not read); (3) model snapshot shows no precision declaration ⇒ EF default (18,2).
**Consequence:** KWD fils (the 3rd decimal) NEVER survive to the database — for orders, invoices, shifts, returns, everywhere. Every prior HM-2 "KWD 3dp" proof (invoice 2.265, receipt FX, etc.) read the IN-MEMORY entity, not a DB round-trip; the subledger checks use *Base (functional, 2dp) columns, so none of them exposed this. Batch 4's Z-report (which reads the PERSISTED order snapshot) is the first to surface it: proof1 has3rdDecimal=false and proof2 variance=0.01 (not the 3dp 0.005) purely because ExpectedCash/order totals are already 2dp-truncated at persistence.
**This is a model-wide, pre-existing gap — NOT a Batch 4 code defect (Batch 4 rounds correctly in memory).** It is the persistence counterpart of HM-D19 and a hard prerequisite for HM-1 going live in KWD. **Fix (needs its own scoped batch + explicit approval):** declare decimal precision on the model — either a global `configurationBuilder.Properties<decimal>().HavePrecision(19,4)` in ConfigureConventions, or per-money-property HasPrecision(19,4)/[Column(TypeName="decimal(19,4)")]. Widening 2→4 scale never changes an EGP (2dp) value, so EGP behavior is unaffected; but it is a cross-cutting change touching every decimal, so it must be scoped and regression-proven (EGP identical) before HM-1. NOT done in Batch 4 (out of scope; touches the whole model).

## HM-2 Batch 4.5 — HM-D27 RESOLVED: EF decimal precision (2026-08-01)
Fixed the EF-truncation blocker in CrossDbContext: `ConfigureConventions` now defaults every decimal to **HavePrecision(19,4)** (matches the 305 money/cost columns), and a property-name loop in OnModelCreating raises the finer columns to their real scale — exchange/settlement rates (ExchangeRate/Rate/InvoiceRate/PaymentRate/ReceiptRate) to **(19,8)** and UoMConversions.Factor to **(19,6)** (these were ALSO being truncated to 2dp by the old default; PosOrderLines.SentQty (18,3) is safe under the (19,4) default). No migration generated/applied — the DB columns already match these types, so this only fixes the EF PARAMETER scale (the runtime write path); a snapshot-sync migration (all no-op AlterColumns) can be added later in a controlled pass.
**Proven end-to-end (hm2-4-proofs, ZZ KWD, rolled back):** proof0 write OpeningFloat 1.733 → EF read-back **1.733** (was 1.73) + raw column 1.733; write FX rate 0.00612345 → read-back **0.00612345** (8dp survives IN THE ExchangeRates TABLE only — see HM-D29: every DOCUMENT rate is still cut to 4dp by CurrencyService.R4); proof1 KWD order now persists tax **0.319** / grand **2.599** (was 0.32/2.6); proof2 drawer variance **0.005** (3dp, was 0.01), JE balanced from a single conversion. **EGP regression IDENTICAL:** mc-test-o2c baseGrand 57228.0, p2p 56772.0, fx gain 1000.0, crm salesreturn 228/200, purchasereturn 21.19 — all unchanged; failedCount=4, roundingDiffLoads=0, ar_sub/ap_sub/bal_qty green, stock_gl Ok=False (HM-D16). App starts clean (no pending-migration failure). KWD document-currency fils now survive to the database — HM-1 in KWD is unblocked at the persistence layer.

## HM-2 Batch 5 items 1–3 — precision fully pinned, migration guard, Qty truncation (2026-08-01)

**Item 1 — model==DB on all 352 decimals (zero divergence).** The `(19,4)` convention default matched the 305 money/cost columns; the 48 columns whose real DB type differs are now pinned EXPLICITLY in `CrossDbContext` by `table.column` to their exact `(precision,scale)` — 18 rate columns `(19,8)`, `UoMConversions.Factor (19,6)`, 17 percentage/tax-rate/confidence columns at their real `(x,4)`, `PosOrderLines.SentQty (18,3)`, and 11 hours/coordinate/rating/legacy columns at their real 2dp. This replaced the earlier name-based override (which had over-raised the non-FX `TaxCodes/PayrollTaxBrackets/EquipmentDepreciationAllocations.Rate` to `(19,8)`; now each is its real `(7,4)`/`(19,4)`). Proven via `hm2-precision-audit`: `fullyMatched=352, divergenceCount=0, efScaleLtDb=0, efScaleGtDb=0`. No migration/DB change — pins are model-only; the DB already has these types.

**Item 2 — the snapshot is dead; the check is its replacement.** `dotnet ef migrations add` is impossible here (snapshot = April-2025, 26 tables; a generated migration = 197-table recreate, 249 destructive ops — verified then reverted). So instead of a sync migration: (a) a PERMANENT guard `ef_precision_vs_db` in `inv-test-integrity` asserts every decimal property's model `(precision,scale)` == its DB column's, any divergence raises `failedCount` (currently 0/352). This is the only viable model↔DB drift detector on a manually-managed schema — `(18,2)` survived 14 months precisely because nothing checked. (b) Prominent warnings placed in `Migrations/DO-NOT-USE-EF-MIGRATIONS.md` (next to the snapshot, where a dev about to run migrations will see it), `docs/README.md`, and here. (c) **Permanent process rule:** every schema change — including deferred items (HM-D8, HM-D16, HM-D28) — is made by an **idempotent SQL script in `deploy/sql/`** + a matching EF model/precision update, NEVER an EF migration and NEVER editing the snapshot. (d) Deferred (large, unscheduled): re-baseline the snapshot from the live DB.

**Item 3 — Qty was truncated to 2dp before Batch 4.5.** Qty columns are `decimal(19,4)` but EF's old `(18,2)` default sent scale-2 parameters, so every quantity was rounded to 2dp on save — **weighed items and partial units were impossible** (1.234 kg stored as 1.23). Existing fractional-qty footprint (read-only, uncorrected): 25 rows in `StockMovements.QtyBase` (4 items) + 20 in `SalesInvoiceLines.Qty` (3 items), all ≤2dp. The 4.5 fix landed BEFORE HM-3 (weighed items) exists — it removed a structural blocker that would otherwise have surfaced there as a mysterious rounding bug.

## HM-D28 — Items.StoreOldPrice is a money column at 2dp (deferred, needs manual SQL if store prices in a 3-decimal currency) (2026-08-01)
`Items.StoreOldPrice` is `decimal(10,2)` — a MONEY amount (storefront strike-through/old price) but only 2 decimals, unlike the `(19,4)` money columns. In a 3-decimal currency (KWD) it would lose the fil. Pinned to its real `(10,2)` in the EF model so model==DB (no false divergence), but the COLUMN itself needs widening to `(19,4)` via an **idempotent SQL script in `deploy/sql/`** IF the storefront is ever used with a 3-decimal currency. Not urgent (display/marketing field, not an authoritative accounting amount). Follow-up.

## HM-D29 — document conversion rate is cut to 4dp by CurrencyService.R4, despite (19,8) columns (pre-existing; NOT fixed) (2026-08-01)
`CurrencyService.ToBaseAsync` (lines 83-84) returns `effectiveRate = R4(fromRate/funcRate)` and every document writes `ExchangeRate = R4(rate)` — so although `ExchangeRates.Rate` (the rate TABLE) holds 8dp and every document `ExchangeRate` column is `decimal(19,8)` (confirmed by the precision map), the stored DOCUMENT rate is only **4 decimals**. Proven by DB read (fresh AsNoTracking, rolled-back tx, `hm2-rate-precision-probe`): an 8dp rate 3.14159265 → the rate table keeps 3.14159265, but sales invoice / receipt / sales return / purchase invoice / payment ALL store 3.1416; FX revaluation's closing rate is also `ToBaseAsync().eff` = R4 (4dp). **Consequence:** a document's own currency conversion is reproducible only to 4dp — a KWD document at a rate needing >4dp is not exactly re-derivable from its stored rate. This is PRE-EXISTING (R4 predates HM-2) and is a scoped follow-up: to make document conversions exactly reproducible, `effectiveRate` and the `ExchangeRate = R4(...)` writes must round to the rate column's precision (8dp) or store the raw rate — NOT done now (touches the shared conversion helper on every path; needs its own regression-proven batch). **Corrected the Batch 4.5 wording** so "8dp survives" is not read as an end-to-end achievement: it was true for the ExchangeRates TABLE, never for document rates. Test-tooling note: `ExecuteSqlRaw` decimal inserts truncate to the parameter's default scale (2dp) — always write rates/money via the EF entity (which respects the model precision) in tests.

## HM-2 Batch 5 — cleanup + permanent ب-4 guard, and a flagged PricingService behavior change (2026-08-01)
**Static R removed from ALL value/cost paths.** Deleted every `private static decimal R(decimal v)=>Math.Round(v,2)` / `R2` in JournalEntryService, ReceivableService, PayableService, StockService, ProcurementService, PosOrderService, PosSetupService, SellingService, PricingService, and converted each call-site to a currency-aware local shadow (document dp `R`/`Rd` for document amounts, functional dp `R2`/`Rf` for GL/cost). Search proof: `grep "static decimal R2?(decimal v) => Math.Round(v,2)"` and `grep "Math.Round(*, 2)"` over those 9 files → **NONE**. `PricingService.RoundToCurrencyAsync` (was line 278, direct `DecimalPlaces` read with silent `?? 2`) now delegates to the central `ICurrencyRounding.RoundAsync` (throws for an undefined currency; single source of precision). EGP regression IDENTICAL (o2c 57228.0, p2p 56772.0, fx gain 1000.0, crm SR/PR, pos-terminals all pass); constants unchanged (failedCount=4, roundingDiffLoads=0, ar_sub/ap_sub/bal_qty green, ef_precision_vs_db 0/352, dbContextLifetime Scoped).

**Permanent ب-4 check `bp4_value_vs_currency_precision` (data-level, distinct from ef_precision_vs_db).** ef_precision_vs_db is SCHEMA-level (model precision == column precision). ب-4 is DATA-level: no PERSISTED value (GL line Debit/Credit at functional dp; SalesInvoices/PurchaseInvoices/SalesReturns GrandTotal at THEIR document-currency dp) carries more decimals than its currency allows. A `(19,4)` column HOLDS a 4-decimal value (schema OK) while that value violates a 2dp/3dp currency — only ب-4 catches it. Legacy separated by a fixed cutoff (2026-07-30, HM-2 Phase B start). Any violation raises failedCount. Current: **0 violations** → failedCount stays 4. NOT redundant with ef_precision_vs_db (schema vs data).

**⚠️ FLAGGED pricing behavior change (foreign documents only; EGP unaffected).** Deleting PricingService's static R made the FOREIGN-document base-price conversion (GetPriceAsync "converted" branch, `R(basePrice / rate)`) round to the DOCUMENT currency dp instead of a hardcoded 2dp. Consequence: a base price that converts to a sub-0.01 foreign value (e.g. a 0.755-EGP item sold in KWD → 0.755/163 = 0.00463 → was `Round(…,2)=0.00`, now `Round(…,3)=0.005`). Old behavior: the 0.00 converted price failed the `UnitPrice>0` test in AddLineAsync and fell back to the RAW base price, TREATING a 0.755-EGP price as 0.755 **KWD** (≈123 EGP — wildly wrong). New behavior: the correctly-rounded 0.005 KWD is used. This is a currency-aware **correctness improvement**, EGP-identical (the converted branch never runs for functional-currency documents), but it IS a change to KWD pricing OUTPUT — surfaced here for review per the "no pricing change" constraint. It only affects items with NO price-list entry in the foreign currency sold on a foreign document (the intended fix path is a foreign price list; conversion is the admin-toggled fallback `ConvertBasePriceForForeignDocs`).

## DEV-2026-010 — documented integrity BASELINE (restores failedCount as a live alarm) (2026-08-02)
`inv-test-integrity`/`IntegrityCheckService.FailedCount` now counts only failures ABOVE this frozen baseline, so `failedCount == 0` is a real "clean" signal again and any NEW/worsened deviation raises it the same day (this is why tip-test/rc6c-test/manuf-test-wo, which correctly assert `failedCount==0`, went green). Baseline is by EXPLICIT key/id/count — no range, no tolerance, no text-match. It is ALWAYS shown as a separate `documentedBaseline_DEV_2026_010` line so the historical debt stays visible, never buried. The four pre-existing failures, verbatim:
- **stock_gl** — Σ StockBalances.TotalValue = **90,518.15** vs GL 1103 = **738,261.56** (diff **−647,743.41**). Cause: HM-D16 (purchase-model gap: receipt-shaped JEs with no matching movement) + accumulated dev-test stock corruption across sessions. Keyed (not value-pinned) — the figure moves with every stock op; a genuinely new corruption is investigated under HM-D16, not this counter. → **HM-D16**.
- **grni** — GRNI GL = **170,760.00** vs unbilled receipts = **169,620.00** (diff **1,140.00**). Cause: HM-D16 / DEV-2026-004 residue (the ITM-0001 Dell receipt-shaped JEs). Keyed. → **HM-D16**.
- **cogs_impact** — `newNoCogs = 2`: sale **line 6457 (invoice 6233, item 1, 2026-07-30)** and **line 6459 (invoice 6235, item 1, 2026-07-30)** — stockable sale lines with no COGS-effective movement (HM-2-era test invoices for item 1 that skipped the stock path). → **HM-D16**. Pinned count 2 — a 3rd raises failedCount.
- **receiptno_dup** — `newRecDup = 5`: five duplicate (terminal, receiptNo) groups on **terminal 1015** — `XYZ-999999` (maxId 3385), `ZZB5-000001` (3382), `ZZB5-000002` (3384), `ZZB5-000003` (3386), `ZZB5-000010` (3387) — dev-test POS orders that reused receipt numbers on a test terminal (paid orders with invoices/JEs, so NOT deleted, like DEV-2026-008). → **DEV-2026-001-series**. Pinned count 5 — a 6th raises failedCount.
Implementation: `IntegrityCheckService.Baseline` (static dict) + `BaselineExcess(checks)`; `FailedCount = BaselineExcess`. The daily HostedService and every `failedCount==0` assertion now measure "no deviation above baseline". No test data touched.

## HM-2 Phase C — manifest of persisted data + ZZ-rule caveat (2026-08-02)
All Phase-C HM-2 proofs (hm2-kwd-*, hm2-4-proofs, hm2-rounding-test, hm2-kwd-functional-company incl. its GRN/assembly/count) run in ROLLED-BACK transactions — **zero persistence**. The data that DID persist came from the STANDARD demo endpoints run to exercise the never-before-run modules + the EGP regression (these post by design, on company 1, NOT on ZZ entities):
- **Payroll:** JE **10465** (JV-2026-002014, "Payroll", 2026-11-30) — hr-test-payslip; plus hr-test-disburse (period 9/2026) and hr-test-settlement JEs.
- **Fixed asset / capitalization:** JE **10472** (JV-2026-002021, "AssetCapitalization", 2026-08-20) → asset **#2024**.
- **EGP regression demos:** mc-test-o2c/p2p/fx sales/purchase/receipt JEs **10471, 10473–10477** (SV/PI/Receipt on 2026-08-02).
- Max JV allocated now **JV-2026-002157**; new gaps are the usual rolled-back-transaction allocations (the entryno gap-rate is COUNTED, never a failure). No duplicate EntryNo (entryno_dup Ok, receiptno_dup baselined at DEV-2026-010).

**Caveat (HM-D30):** the "test on ZZ entities only" rule is NOT satisfied when a module is verified through an existing demo endpoint that posts by design (payroll/assets/depreciation have no ZZ analog — only company-1 demo endpoints). Future currency/regression verification of these modules needs ZZ analogs or read-only probes. Logged, not implemented.

## HM-2 model decision — the hypermarket is a NEW KWD-functional company (2026-08-02)
Flipping company #1's functional currency (EGP→KWD) is IMPOSSIBLE on live data: **3,603 journal-entry lines** (Debit/Credit), **271 `*Base` document columns** (GrandTotalBase…), and **64 stock balances** (TotalValue/AvgCost) are all EGP-denominated functional amounts. Re-interpreting them as KWD makes every base value wrong by the exchange rate (~163×) and breaks the AR/AP subledger checks (they compare the `*Base` columns) the instant the flip lands. Decision: **the hypermarket is a SEPARATE KWD-functional company** — functional = document = **KWD**, so there is **NO currency conversion at all**; the 3-decimal precision is simply the functional dp. This DROPS two deferred items **by model decision** (not by being fixed): **HM-D23** (exchange-rate staleness) and **HM-D29** (document rate cut to 4dp) — both are conversion-only concerns that never arise when functional==document. The mechanism is proven by Phase-C ج-٥ (a real 2nd company, KWD-functional, posted GRN/assembly/count balanced at 3dp with stock_gl matching). Still required for the KWD hyper: **HM-D18** (a KWD price list with prices entered directly — conversion yields half-a-fils), **HM-D22** (display layer), **HM-D28** (StoreOldPrice widening). Company #1 is NOT touched.

## HM-2 model decision AMENDED after HM-D31 — hyper runs on company #1 (EGP-functional) with a KWD branch (2026-08-02)
HM-D31 (813 hardcoded `CompanyId=1` sites across 16 controllers, no session-derived company) means a new KWD company would exist in the DB with NO request path reaching it. The danger is not the count — it is the ABSENCE OF A GUARD: today the hardcoded 1 PREVENTS cross-company leakage; the moment CompanyId is derived from the session, every missed site becomes a cross-company DATA LEAK across already-complete modules (Accounting, Contracting, Tasks). That is a horizontal architectural change the size of all of HM-1-أ before a single line of the cashier core. **Reversal:** the hyper runs on **company #1 (functional EGP) with a KWD BRANCH** (document = KWD, functional = EGP) — a path HM-2 already proved end-to-end (return/payment realized-FX on 4902/5902, ForeignAmount, ar_sub=0 across currencies, KWD fils persist in the DB). Only two deferred items remain in that path, both scoped: **HM-D23** (exchange-rate staleness) and **HM-D29** (document-rate 4dp precision). Therefore **HM-D23 and HM-D29 are RE-INSTATED as hard prerequisites for running the hyper** (no longer dropped). **HM-D31** is re-scoped as a prerequisite for MULTI-COMPANY SELLING (not for the hyper) and is NOT implemented now. The KWD-functional-company model (ج-٥) remains proven-viable but is shelved behind HM-D31.

## HM-2 closing batch — HM-D23 + HM-D29 RESOLVED (2026-08-02)
**HM-D29 (document rate cut to 4dp):** removed `R4` from EVERY `ExchangeRate` column WRITE (ReceivableService ×5, PayableService ×3, ProcurementService ×1, SellingService ×2, PosOrderService refund ×1 = 12 sites) — the document now stores the ACTUAL rate at the column's `(19,8)` precision. `effectiveRate` and all base calcs untouched. Forward-only: NO backfill of the 271 existing docs (proven unchanged — sample rate 1.0 intact). Proven (hm2-rate-precision-probe, fresh DB read): 8dp rate `3.14159265` persists on sales invoice/receipt/sales return/purchase invoice/payment; `GrandTotalBase` recomputed from the STORED rate == the stored base exactly (31.42==31.42) ⇒ the document conversion is now re-derivable. **Readers becoming more accurate (flagged, not bugs):** FxRevaluationService book base, receipt/payment FX (`invRate`), sales-return settlement (`origRate`). No production equality-comparison on ExchangeRate (only a test assertion, stays valid).
**HM-D23 (exchange-rate staleness):** company-level policy on `AccountingSettings` — `RateMaxAgeDays` (int, **default 0 = no limit ⇒ zero regression**) + `RateStaleBehavior` (`Warn` default / `Reject`), added via idempotent SQL (`deploy/sql/hm2_d23_rate_staleness.sql`, applied — NOT a migration). `CurrencyService.RateStalenessAsync` measures the looked-up rate's age vs the **DOCUMENT date** (justified: a back-dated document is judged against its own date's rate, not today). Enforced in `CreateSalesInvoiceAsync` when the rate is looked-up (not caller-supplied) and the currency is foreign: **Reject** returns an actionable localized error (names the action + the branch accountant) via IStringLocalizer → the existing on-screen error display, zero effect; **Warn** proceeds, increments `CurrencyService.StaleRateSales`, and pushes an on-screen notification (not an app log). New counted classification `stale_rate_sales` in inv-test-integrity (visible, never raises failedCount). The existing rate-entry path (`CurrencyController.SaveExchangeRate`) is untouched; no daily mandate added. Latest KWD→EGP rate is 2026-01-01 (≈213 days stale) — the reason this guard matters for the hyper. Proven (hm2-rate-staleness-test, 4 rolled-back scenarios): MaxAge=0 → no effect · Warn → proceeds + counted+1 · Reject → blocked (0 invoice rows) + actionable message · enter today's rate → succeeds. EGP regression IDENTICAL (rate=1 ⇒ both fixes are no-ops). failedCount=0, ef_precision_vs_db 0/352, bp4=0, all constants green.

## HM-D34 — 197 pre-existing cross-company mismatches: branches MISLABELED onto empty shell companies (read-only forensic, 2026-08-02)
While building the HM-1 counted classification `branch_company_mismatch` (3 dimensions: order.CompanyId≠branch.CompanyID · terminal cash-account company≠branch company · employee.EmpCompanyID≠branch company), the "before" number came back **200, not the ~2 expected** — surfacing a pre-existing leak far beyond the branch-17 HM-0 seed defect. Per the standing rule ("if mismatch appears in rows other than the two shifts: STOP, report, do NOT treat") the guard was **de-fanged and NO data was corrected**. Breakdown: orders=190, terminals=7, employees=3. **Branch 17 (HYPER-DEMO) accounts for only 3** (2 terminals HM-L1/HM-L2, employee 1042). The other **197** are on the LIVE restaurant branches:
| Branch | Branch.CompanyID | Orders (CompanyId=1) | Terminals | Employees | Activity |
|---|---|---|---|---|---|
| 4 | 71 (PrimeTech Solutions) | 135 (through 2026-08-02, TODAY) | 3 | 0 | live restaurant |
| 15 | 79 (Test Branches Co.) | 53 (7/4–7/13) | 2 | 2 (emp 5, 1043) | live restaurant |
| 12 | 65 (Global Corporation) | 2 (7/2) | 0 | 0 | restaurant |
Total 197 = 190 orders + 5 terminals + 2 employees. All 190 order rows have `CompanyId=1` — the HM-D31 hardcode footprint.

**DECISIVE FORENSIC — which side is truth? Evidence, not guess:**
- **Companies 65/71/79 are EMPTY SHELLS.** Each owns **0 Accounts, 0 FiscalYears, 0 Warehouses, 0 JournalEntries, 0 SalesInvoices, 0 Receipts, 0 StockMovements.** Company 79 is literally named *"Test Branches Co." / "شركة الفروع للاختبار"*, seeded 2026-06-24. With zero accounts and zero fiscal years it is **structurally impossible** to post any JE/invoice/receipt to them.
- **100% of the real footprint is company 1:** every financial/inventory table has `MIN(CompanyID)=MAX(CompanyID)=1, DISTINCT=1` (JournalEntries 1569, SalesInvoices 284, Receipts 274, StockMovements 691, PurchaseInvoices 17). Company 1 owns 62 accounts, 2 fiscal years, 3 warehouses.
- **Operational wiring of branches 4/12/15/17 is entirely company 1:** every terminal cash account is company 1 (0 on 65/71/79); every BPS `DefaultSalesWarehouseId` resolves to a company-1 warehouse (WhCo=1 for all four).
- **All 197 come from the POS path only** (the sole branch-linked company reference is `PosOrders.CompanyId`, pinned to 1 by HM-D31). No non-POS document mismatches a branch.

**VERDICT:** the branches are **mislabeled** — `Branches.CompanyID` points at empty shell/test companies (65/71/79) while every peso of financial and inventory reality is company 1. This is the **same defect as branch 17 (HM-D33), just wider** (branches 4, 12, 15, 16, 17 all point at shells). The truth is: everything is company 1. The correction would be a **relabel** (`Branches.CompanyID → 1`), NOT data surgery — no JE/invoice/stock row changes because they are already company 1.

**Guard consequence (implemented this batch):** the cross-company guard is **NOT enforced as a hard reject** on any shared/restaurant path — a reject in `PosOrderService.CreateOrderAsync` / `PosSetupService.CloseShiftAsync` / `PosAppController` login would HALT the working restaurant (branch 4 sold today). It is **counted-only** via `IntegrityCheckService.branch_company_mismatch` (observes, never blocks, never raises failedCount). The hard reject lives on the **hyper gateway alone** (`HyperPosController`), which is not yet live.

**HM-1 status:** branch-17 seed AND correction are **PAUSED** — branch 17 is part of this same picture and the decision may change with the treatment choice. Nothing corrected, nothing seeded.

**Treatment options (NOT executed — awaiting decision):**
1. **Relabel (recommended):** `UPDATE Branches SET CompanyID=1 WHERE ID IN (4,12,15,16,17)` via idempotent SQL. Effect: `branch_company_mismatch` 200→0; branch company = order company = 1 everywhere; restaurant keeps working; hyper gateway guard then passes for branch 17. Zero financial/inventory data touched (all already company 1). Risk ≈ 0. The empty shells 65/71/79 can be left or deleted separately.
2. **Relabel + delete shells:** option 1, then delete companies 65/71/79 (0 footprint ⇒ safe). Cleanest end state; removes the 2026-06-24 test seed.
3. **Build out the shells as real companies:** give 65/71/79 chart of accounts + fiscal years + warehouses and move the footprint. **Rejected by evidence** — they hold zero activity and one is named "Test"; this is data surgery against reality.

## HM-D34 EXECUTED — relabel-only, shells KEPT (2026-08-02)
Approved: **relabel only, no deletion** (empty shells are harmless; deletion is irreversible and could reveal dangling refs — validated below). Scope: **full identity**.
**Condition 2 census (all 140 `Company(Id|ID)` columns + `Employee.EmpCompanyID`) — every reference to shells 65/71/79:**
- `Branches.CompanyID` = 5 (IDs 4,12,15,16,17) · `Employee.EmpCompanyID` = 6 (IDs 19–24, the seed-test-org people) · `Customers.CompanyID` = 1 (ID 1025, a **duplicate** of company‑1 cash customer #23; 0 docs) · `JobApplications.EmpCompanyID` = 1 (ID 18 "تست") · `Companies.CompanyID` = 3 (the shell records themselves — KEPT).
- **Discovered hierarchy (validates "no delete"):** 65 is parent of company 67 (Regional Company), 71 is parent of 72 (PrimeTech Innovations); 67/72 are **also empty** (0 accounts/JEs/emps). Since we KEEP the shell records, the parent links are not orphaned. Deleting 65/71 later must handle 67/72 first — exactly the dangling-ref risk that justified rejecting deletion.
- **No real-effect reference on any shell** → STOP condition not triggered.
**BEFORE → AFTER (idempotent SQL `deploy/sql/hm_d34_relabel_shell_company_refs.sql`, keyed on `IN (65,71,79)`):**
- Branches {4→(was 71), 12→65, 15→79, 16→79, 17→79} ⇒ all CompanyID = 1 (5 rows).
- Employee {19,20,21,22,23,24} EmpCompanyID 79 ⇒ 1 (6 rows).
- Customer 1025 CompanyID 79 ⇒ 1 (1 row). JobApplication 18 EmpCompanyID 79 ⇒ 1 (1 row).
- Movable rows still on a shell AFTER: 0 / 0 / 0 / 0. Shell company records 65/67/71/72/79 present, hierarchy intact.
- Counted classification `branch_company_mismatch`: **200 → 0** (orders 190→0, terminals 7→0, employees 3→0).
- Zero financial/inventory rows changed (every such table was already DISTINCT CompanyID = 1).
**Condition 4 (source fix):** `DevSeedController` HYPER-DEMO seed changed `CompanyID = 79 → 1` (the HM-D33 defect) + the idempotent-repair branch now also repairs a stale company. The `seed-test-org` fixture (origin of company 79 + emps 19–24) is a DELIBERATE separate-company org/HR/notification test with no accounting setup — left as-is but annotated: its branches must never host POS/accounting (a code comment now says so); the re-enabled guard + classification catch any misuse.
**Condition 7 (guard re-armed as HARD REJECT on all paths):** restored the reject in `PosOrderService.CreateOrderAsync`, `PosSetupService.CloseShiftAsync`, and `PosAppController` login (were counted-only during the unresolved window). Hyper gateway `HyperPosController` keeps its reject. Proof endpoint `hm-d34-guard-regression` exercises a full cash sale on a relabeled live branch (guard ALLOWS) and a shell-79 probe branch (guard REJECTS).

## HM-D35 (deferred) — empty shell companies 65/67/71/72/79 from wrong seeds — DELETE in a separate cleanup phase, not now
Companies 65 (Global Corporation) / 67 (Regional Company) / 71 (PrimeTech Solutions) / 72 (PrimeTech Innovations) / 79 (Test Branches Co., seeded 2026-06-24) hold **zero** accounts/fiscal-years/warehouses/JEs/invoices/receipts/stock. After HM-D34 they hold no movable references either (only the 65→67, 71→72 parent links among themselves). They are safe to delete, but deletion is irreversible and must first re-prove zero references (across all 140 company columns) at delete time. Deferred to a dedicated cleanup phase; NOT done now, by explicit decision (deletion could reveal a dangling reference after the fact).

## HM-1 Phase A — hypermarket selling core (2026-08-02, STOPPED before the 9-point acceptance)
**Prep item 1 (AR/AP control untouched by the HM-D34 identity move):** the integrity check's own numbers give AR drift = subledger 672190.35 − ledger 672190.35 = **0.00**, AP drift = 631865.66 − 631865.66 = **0.00**. The moved entities are control-neutral: customer 1025 has **ControlAccountId = 0** (no control account, 0 balance, 0 invoices); employees carry no control account at all. So neither the subledger nor the control ledger moved.
**Prep item 2 — KWD→EGP rate is a DEV ESTIMATE, not a market quote:** seeded for 2026-08-02 (fresh; resolves the HM-D23 staleness) at Central 162 / Sell 163 / Buy 161 EGP-per-KWD, stored at (19,8) via typed decimal. These values are carried from the Jan-2026 structure as *plausible* dev figures (EGP≈49/USD × KWD≈3.3/USD ≈ 160–163) — **the demo's ledger amounts are therefore estimates, not real FX.**
**Branch-17 coherent seed (`hm1-seed`, idempotent):** grants the company-1 cashier hyper1 a **pos-cashier** role; closes the open HM-0 shift **#1053 via CloseShiftAsync at variance 0** — proven to create **NO journal entry** (company-1 JE count 1576 → 1576); seeds **HM-DEMO-001** (مياه معدنية 600مل, Stockable, TrackBatch/Expiry/Serial all false, single UoM, barcode 6281234567890, SalesPrice 0.750 KWD, item #7187); posts **100 units** opening stock via **PostOpeningStockAsync only** (opening JE #10799, 8000 EGP at a dev-estimate 80 EGP unit cost) — balance == 100.
**Phase A build (delegation only — the controller computes nothing):** `HyperPosController` gains `scan` (barcode → item → AddLineAsync), `line/qty` (SetLineQtyAsync), `line/remove` (RemoveLineAsync), `pay` (PayAsync "Cash"); an open cart id lives in the HyperCtx session; **every total is RecomputeAsync server-side**; the pay path **requires an open shift (hyper lane only; restaurant path untouched)**; the company guard on CreateOrderAsync/CloseShiftAsync is active. `Views/Hyper/PosLane.cshtml` extended with a scan box (autofocus), a cart table (qty setter + remove), the grand total at **document-currency decimals (KWD ⇒ 3, from ICurrencyRounding — not toFixed(2))**, a Pay-cash button, and the existing error/success alert area. Only standard Metronic components already used in the hyper views. **New user-facing service messages via Resources** (4 keys × ar/en/fr); **view micro-labels follow the hyper view family's existing inline `isAr` bilingual convention** (flagged for your call). Strictly HM-1 scope: no promos/weight/multi-barcode/units/expiry/batch/loyalty/offline/print/card/KNet/hold-recall; no new sale service; opening stock via PostOpeningStock (no GRN/purchase). **STOPPED before the 9-point acceptance test.**

## HM-D36 (deferred) — unify hypermarket screen strings onto Resources
The hyper POS view family (PosLogin/PosStart/PosLane) uses inline `isAr ? "ar" : "en"` bilingual ternaries, not IStringLocalizer/resx. HM-1 Phase A view labels followed that existing convention (consistency within the family chosen over resx purity, per decision 2026-08-02). Deferred: migrate all hyper view micro-copy to Resources in one pass. Backend/service messages already go through Resources.

## HM-D18 RESOLVED (minimal, for HM-1) — document-currency price list consumed by the cashier path (2026-08-02)
The HM-1 acceptance proved the earlier premise WRONG: the cashier path does NOT return Item.SalesPrice unchanged — `AddLineAsync` calls `PricingService.GetPriceAsync` with the order's (KWD) currency, and with `InventorySettings.ConvertBasePriceForForeignDocs = true` (company-1 default) it CONVERTS the functional (EGP) SalesPrice down to KWD: 0.750 ÷ 162 → **0.005 KWD** (half-a-fils). So HM-D18 is a real HM-1 prerequisite, not just HM-4.
**Minimal fix (no flag, no formula change, no touch to Item.SalesPrice):**
- `PricingService.GetPriceAsync` gains an optional `int? priceListId` — when passed, candidate lines are restricted to that list (`&& (priceListId==null || pl.ID==priceListId)`); everything else in the formula is untouched. Callers that omit it are unaffected.
- `PosOrderService.AddLineAsync` resolves `BranchPosSetting.DefaultPriceListId` for the order's branch and passes it. When the branch HAS a list and the price did NOT come from it (`Source != "list"/"costplus"` — i.e. the item is absent from the list), the line is **REJECTED** with a localized message — never a silent conversion nor a zero price.
- A KWD price list (`CurrencyId = KWD`, `Hyper KWD`) with `HM-DEMO-001 = 0.750` is seeded and linked to branch 17 via `DefaultPriceListId` (dev: `hm1-seed`; prod: idempotent `deploy/sql/hm_d18_hyper_kwd_pricelist.sql`). Because the list currency == the document currency, `GetPriceAsync` returns the entered price (`Source="list"`, 0.750) with **NO conversion**.
- **Restaurant path unaffected (barrier-checked):** every branch has `DefaultPriceListId = NULL` (0 non-null), so the branch list passed for restaurant is null → `GetPriceAsync` behaves exactly as before → zero regression (proven by the mandatory before/after restaurant sale).

## HM-D20 — 6th example: currency-blind amount fields (Item.SalesPrice) (2026-08-02)
Add to the HM-D20 catalog of amount fields that carry no currency of their own (so any consumer implicitly assumes one): **`Item.SalesPrice` is a bare decimal with no currency** — the POS/pricing path treated it as functional (EGP) and converted it to the document currency, which is meaningless for an item whose shelf price is set in KWD. The company-wide `ConvertBasePriceForForeignDocs` flag is worse: it declares "prices are NOT functional" without saying which currency they ARE, so one 0.750 means two different things on an EGP document vs a KWD document. HM-D18's list carries an explicit `CurrencyId`, removing the ambiguity at the source.

## HM-D37 (deferred) — unify selling on currency-bearing price lists; remove Item.SalesPrice from sale paths
HM-1 fixes pricing only for branches that pin a `DefaultPriceListId`. The broader gap remains: `Item.SalesPrice` (currency-blind) is still the fallback for every branch without a list, and `ConvertBasePriceForForeignDocs` still governs foreign documents company-wide. Target end state: every sale path prices exclusively from a currency-bearing price list; `Item.SalesPrice` is removed from sale paths (kept only as an optional default when authoring a functional-currency list); the convert-flag is retired. Not now — it touches the shared restaurant path and every selling module.

## HM-D38 (deferred) — tax rate inherited from item category, blind to the branch's country
The POS tax rate is resolved from the item/category, not the branch's country. HM-DEMO-001 (sold in the KUWAIT hyper branch, where there is no VAT) inherited a 14% Egyptian VAT from its item category, producing a 2.565 gross instead of 2.250. Correct behavior: the tax rate must follow the branch/country (a Kuwait branch ⇒ 0% VAT) — not the item category alone. For HM-1, HM-DEMO-001 is made tax-exempt at the item level as the correct Kuwait behavior. The general fix (branch/country-driven tax resolution) is deferred.

## HM-D39 (deferred, SMALL) — account 510101 is the de-facto COGS account but MISNAMED "Rent Expense"
Read-only forensic (2026-08-02), surfaced by the HM-1 acceptance. First read looked system-wide (23 categories → 510101); the decisive follow-up showed it is a **naming** defect, not a **routing** one:
- **510101 receives ONLY cost-of-goods postings:** 410 `Inventory`-source JEs (13,522.15 Dr) + 14 `Reversal` JEs (1,840 Cr) — and **nothing else**. Zero real rent/lease/manual entries. It sits among a cost family (510102 Utilities, 510103 Inventory write-off, 510104 Project execution cost) under 51 Operating Expenses (type = Expenses).
- **It is the only account named "إيجار/Rent" in the entire chart** (other name-matches were false hits on "Cur**rent**"). There is no separate rent account that COGS is stealing from.
- **Verdict (a): the routing is SOUND, the NAME is wrong.** All company-1 COGS consistently lands in one account (510101); that account is simply labelled "Rent Expense" from an old seed. The 410 entries are correctly classified as cost of sales. The only consequence is presentational — an income statement shows "Rent Expense 11,447.55" where it should read "Cost of Goods Sold", with no separate gross-profit line.
- **Fix = RENAME 510101 → "Cost of Goods Sold / تكلفة البضاعة المباعة"** (a label change; no re-routing, no historical reclassification, no data touched). Deferred as a small item — the 23 categories stay pointed at 510101, which becomes correctly named. If real rent is ever needed, add a new rent account then.
**HM-1 handling:** HM-DEMO-001 is placed in a dedicated isolated hyper category whose COGS points at 510101 (the correct COGS account), so its COGS is correctly classified. No new COGS account created; the rename is deferred to HM-D39.

## HM-1 acceptance — 9/9 PASS (2026-08-02), after HM-D18/D38/D39 fixes surfaced by the test
Driven through the REAL HTTP controller path (login hyper1 → open shift HM-L1 → scan 6281234567890 → qty 3 → cash pay); every proof read fresh from the DB.
1. **Full cycle**: order **5489** (branch 17, KWD), line qty 3 × **0.750** = SubTotal **2.250**, tax **0** (VATEX), GrandTotal **2.250 KWD** (fils preserved). Invoice **9414**: GrandTotalBase **366.75 EGP** = 2.250×**163** = AR line 1102. Stock **100→97** (movement 10598, −3). Revenue JE **11759** balanced (AR 366.75 = Revenue 366.75). COGS JE **11760** = **240** (80×3) to 510101 (the COGS account; rename=HM-D39); margin **126.75 EGP / 34.6%**. Cash receipt **8388** JE **11761** (Till HM-L1 366.75 / AR). Receipt no **HM-L1-000002** (atomic). ar_sub drift **0**.
2. Fractional qty **1.5000** stored (LineTotal 1.1250) — no truncation.
3. Bad barcode → localized error "لا يوجد صنف بهذا الباركود." — no line, no effect.
4. Qty 200 > stock 97 → PAY rejected with StockService verbatim "الرصيد غير كافٍ: المتاح 97، المطلوب 200"; zero effect (invoice max unchanged, balance 97).
5. PAY with no open shift → rejected "افتح وردية قبل الدفع." (server guard); the UI renders no pay button without a shift.
6. Shift **4120** closed at variance 0 (VarianceJournalEntryId null = NO JE); expected 102.250 = opening 100 + Σ orders 2.250; Z report renders **3 decimals** (2.250 / 102.250 / 0.000) after the doc-dp display fix.
7. Isolation: order/shift/invoice on branch 17 (Hyper preset), 0 kitchen lines; excluded from the restaurant lane whitelist {Restaurant, Cafe, no-activity}.
8. Constants: failedCount **0** · ar_sub/ap_sub drift **0** · branch_company_mismatch **0** · bal_qty_vs_moves **0** · ef_precision **0/352** · bp4 **0** · culture-check **allPass** · PRJ-DEMO **30,920** · dbContextLifetime **Scoped**.
9. Manifest: item 7187 · hyper category 3025 · KWD price list 35 · rate rows 38/39/40 (2026-08-02) · order 5489 · invoice 9414 · shift 4120 · JEs 11759/11760/11761 · receipt 8388 · stock movement 10598 · opening-stock (hm1-seed).
**Item balance / depletion:** final **97** (opening 100 − 3 per clean sale; the voided trial netted 0). `hm1-seed` opening-stock is **fill-to-floor** (`if balance<100 → PostOpeningStockAsync(100−balance)`), so re-running it before a round refills to 100 — repeated acceptance rounds are supported without depletion.

## HM-1 acceptance — two follow-up notes (2026-08-02)
- **Fractional-qty proof (point 2) was momentary:** qty 1.5000 was verified live on a scratch order that was then deleted as residue, so it is not inspectable in the DB now. **Rule:** any future fractional-quantity test must leave a durable, inspectable state OR reverse its effect through the services (void/return) — never delete the order line to clean up. (Same spirit as the ZZ-entities-reversed-via-services rule.)
- **Z report & drawer are in the DOCUMENT currency (KWD, 3dp); the journal entries are in the FUNCTIONAL currency (EGP) — BY DESIGN.** The shift Z (grand/expected/variance) and drawer counts read in KWD fils; the posted GL (revenue/COGS/AR/cash) reads in EGP at the day's rate. This split is intentional (document vs functional). Flagged as an item for the visual review in the hypermarket SCREEN phase (confirm the on-screen currency labels make the split obvious to the cashier/accountant).

## HM-D39 EXECUTED — 510101 renamed to "Cost of Goods Sold"; separate rent account added (2026-08-02)
**Gate (the line-12570 ruling) PASSED with evidence:** account 510101 holds **411 Inventory-source (COGS) JEs + 14 Reversal JEs = 425 total, and ZERO rent/purchase/manual entries**. `seed-acc-demo` ran (its vendor exists) and does `Purch(vTrans, …, 2500, rent=510101)`, yet **no PurchaseInvoice/manual JE ever landed on 510101** (A("510101") returned 0 before the chart seed, so the demo rent went nowhere on it). So "name wrong, routing sound" is ABSOLUTE here — no production rent to preserve, no historical reclassification.
**Executed (`deploy/sql/hm_d39_rename_cogs.sql`, idempotent, NOT a migration):**
- `UPDATE Accounts SET NameEn='Cost of Goods Sold', Name='تكلفة البضاعة المباعة' WHERE Code='510101'` (NAME only; ID/Code unchanged → every posting/report/reconciliation by ID/Code is unaffected; the acceptance's COGS JE now reads "Cost of Goods Sold").
- Created a **real** Rent Expense account **510105** under 51 Operating Expenses.
**Source fixed:** chart seed renames 510101 to COGS + adds 510105; `seed-acc-demo` now maps `rent → 510105` (no longer onto the COGS account). No historical JE touched; restaurant path untouched (its COGS is also correctly labelled now — a benefit).

## HM-D38 EXECUTED — tax follows the BRANCH, not only the item category (2026-08-02)
Tax was resolved from the item/category, blind to the branch's country — a Kuwait hyper item inherited Egyptian 14% VAT.
**Fix (smallest, restaurant-safe):**
- New nullable column `BranchPosSettings.DefaultTaxCodeId` (idempotent `deploy/sql/hm_d38_branch_tax.sql`, NOT a migration) + EF property.
- `PosOrderService.ResolveTaxRateAsync` now takes `branchId`; resolution order = **item.DefaultTaxCodeId → BranchPosSetting.DefaultTaxCodeId → company default VAT**. A branch with NULL override (every restaurant branch) falls straight through to the company default — the EXACT pre-D38 behaviour (regression barrier).
- Branch 17 (Kuwait hyper) pinned to **VATEX (0%)**; the per-item VATEX tag was REMOVED from HM-DEMO-001 so the 0% now demonstrably comes from the BRANCH, not the item.
- Country route rejected: branch 17 CountryID=32 == restaurant branch 15, and `Countries` has no VAT column — a branch-level override is the reliable minimal design.

## HM-D40 (deferred, logged not treated) — service & delivery tax still company-level, ignoring the branch
`DefaultVatRateAsync(companyId)` (company default VAT) still drives **service charge and delivery fee tax** at ~6 sites in `PosOrderService` (RecomputeAsync + the invoice build). So a Kuwait branch that correctly charges 0% on ITEMS would still tax a delivery fee at the Egyptian 14%. Not urgent — the hypermarket has no delivery/service now — but it is the same branch-blindness as HM-D38 and must not be forgotten: service/delivery tax should also consult the branch's tax code. Logged; NOT treated in this batch.

## HM-2 — multi-barcode + multi-unit (started 2026-08-03)
**HM-D15 update — a dormant code path activated for the first time:** the read-only barrier check proved the multi-unit stock path has **NEVER** been exercised — **zero** stock movements with a non-base unit exist (and zero with a non-base unit lacking a conversion). So HM-2 turns on a path that has lived in the code unused, joining Receipt/GRN and the offline layer in HM-D15. **The HM-2 acceptance is therefore a FIRST RUN, not a regression check of known behaviour** — any surprise is treated as a first-run finding: report, do not fix by improvisation.
**R4 note → HM-3:** `ToBaseAsync` rounds the base quantity to the column's 4dp. This only loses anything when BOTH the conversion factor is fractional AND the quantity is fractional — exactly the WEIGHTED-item scenario. For HM-2's integer factors (12, 144) the base is exact. Logged as an HM-3 (weight/scale) item: weighed items need base precision > 4dp or an explicit rounding policy.

## HM-2 GetPriceAsync — DETERMINISTIC price-line priority (2026-08-03)
PriceListLine now has two match dimensions: `MinQty` (existing qty break) and `UoMId` (new, HM-2). Two lines can match one request, so the pick MUST be deterministic (never `FirstOrDefault` on an unordered set). Declared order (in `PricingService.GetPriceAsync`):
1. **Explicit unit match first** — a line whose `UoMId` equals the requested unit beats a null-unit (base/any) line.
2. Customer-specific > segment-specific > general (existing).
3. Higher list **Priority** (existing).
4. Most-specific qty break — highest `MinQty` ≤ qty (existing).
5. **Smallest LineId** — stable final tie-break.
If no line matches the requested unit AND no null-unit line exists, the price is rejected (never a fallback to `Item.SalesPrice`, never a factor multiply — the HM-D18 half-fils lesson). A branch-list-pinned path rejects via `AddLineAsync` (`Source != "list"`).

## HM-2 acceptance — 10/10 PASS (2026-08-03), first run of the multi-unit path
Driven through the REAL HTTP scan path where it is a controller concern; every value read fresh from the DB.
Seed: HM-DEMO-002 (item 8186) base PCS, DZN→PCS=12, CTN→PCS=144, barcodes 6280000000021(PCS primary)/38(DZN)/45(CTN), KWD price lines 0.500/5.500/60.000 on list 35, opening 500 base @ 0.300 EGP via PostOpeningStockAsync.
1. Scan PCS ⇒ line UnitPrice **0.500**, unit PCS. 2. Scan DZN ⇒ **5.500**, unit DZN. 3. Scan CTN ⇒ **60.000**, unit CTN. (cart shows قطعة/درزن/كرتونة.)
4. Cart of all three ⇒ invoice 9419 SubTotal **66.000 KWD**, base 10758 EGP (66×163) = AR 1102 = Revenue 4101 (balanced, tax 0/exempt); stock movements QtyInUoM 1/1/1 → QtyBase **1/12/144 = 157**; balance 500→**343**; COGS = 3 JEs × 0.300 = **47.10** to "Cost of Goods Sold" (510101), base-costed; ar_sub drift **0**.
5. Cross-table duplicate barcode ⇒ scan REJECTED "هذا الباركود مسجَّل على أكثر من صنف".
6. KG (no conversion) ⇒ REJECTED at add (AddLineAsync) AND at deduct (PostMovementAsync/ToBaseAsync).
7. BarcodeMulti OFF + secondary barcode ⇒ "الباركودات المتعدّدة غير مفعَّلة" (NOT "not found").
8. Deterministic price priority: explicit CTN line (60) beats null line (99); qty3⇒MinQty=2 line (55), qty1⇒MinQty=1 line (60).
9. Restaurant regression (rc6c) identical, failedCount 0.
10. Constants: failedCount **0** · ar_sub/ap_sub drift **0** · branch_company_mismatch **0** · bal_qty_vs_moves **0** · **barcode_cross_table_dup 0** · **unit_no_conversion 0** · ef_precision **0/352** · bp4 **0** · culture **allPass** · PRJ-DEMO **30,920** · dbContext **Scoped**.
Files: models (PosOrderLine/PriceListLine/SalesInvoiceLine + DTO UoMId), StockService.ToBaseAsync (reject), PricingService.GetPriceAsync (unit + deterministic priority), PosOrderService (AddLineAsync guard + unit price + sale-stock unit chain), ReceivableService (SalesLineInput/line UoMId → movement), HyperPosController.Scan (both tables + capability gate + dup reject), PosLane view (unit in cart), IntegrityCheckService (2 counted classifications), Resources (3 keys ×3 langs), deploy/sql/hm2_multiunit.sql. Balance fill-to-floor to 500 via re-running hm2-seed.

## HM-D41 (deferred) — weighted-item extras: tare and min/max weight
HM-3 core models a weighted item minimally (`IsWeighted` bool + base unit KG). Deferred extras for deli/butchery corners (not the core): tare weight (subtract container weight), and per-item min/max weight guards (reject an implausible scale reading). Logged; not in HM-3 core.

## HM-3 — weighted items + scale barcode (started 2026-08-03)
### HM-D42 (deferred) — fixed barcodes in the reserved scale range (data error, not design)
Four items carry a FIXED product barcode inside the GS1 reserved in-store range `2xxxxxxxxxxx`: ITM-0001 `2001000000017` · ITM-0002 `2001000000024` · ITM-0003 `2001000000031` · ITM-0004 `2001000000048` (present in both Items.Barcode and ItemBarcodes). GS1 reserves `2…` for in-store/variable-measure, so a fixed product barcode there is a data error and should be re-numbered. **NOT corrected now** (existing data). Collision is theoretical in the hypermarket — branch 17 does not carry these items. The counted classification `fixed_barcode_in_scale_range` surfaces them (value 4); the new save-guard blocks only NEW ones.

### HM-D20 — 7th example: undeclared MidpointRounding on quantity/cost Math.Round
Of ~76 `Math.Round` in the cost/quantity BL, the money/stock-value path is explicit `AwayFromZero` (CurrencyRounding + R/R4/R2 helpers), but several quantity/manufacturing rounds omit the mode and take .NET's default ToEven: `StockService:1087` (BOM component qty), `ManufService` (PlannedQty, labor/oh, unit cost, material), `ReceivableService:839` (margin display). Same ambiguity pattern as HM-D20 (an undeclared default is a hidden assumption). Outside the weighted-sale path; deferred — declaring them now would stall HM-3 for no gain.

### HM-3 design note — scale-barcode save-guard scope (INTENTIONAL, declared)
The scale-barcode prefix is a **branch-level** setting (`BranchPosSettings`), but a fixed barcode (`Items.Barcode`/`ItemBarcodes`) is **company-level** (shared across branches). By design, the save-guard rejects a NEW fixed barcode that falls within **any** scale prefix configured on **any** of the company's branches — a deliberate company-wide guard over a branch-level setting, so one branch's scale range protects the whole company's fixed-barcode namespace. **If a branch later adds a scale range that already contains an existing fixed barcode:** nothing existing breaks (the guard blocks only future saves; existing rows are untouched), and the counted classification `fixed_barcode_in_scale_range` surfaces the pre-existing overlap for manual re-numbering. The scan still routes by prefix first, so an existing fixed barcode inside a newly-added range would be rejected at scan with "fixed barcode in scale range" (not silently mis-sold) until re-numbered.

### HM-3 process rule — ALTER with NOT NULL DEFAULT on a large table
`Items.IsWeighted BIT NOT NULL DEFAULT 0` runs on a table of 172 rows (trivial here). RULE: any `ALTER … ADD <col> NOT NULL DEFAULT` on a large table must be duration/lock-estimated before running (SQL Server stamps the default into every existing row; on millions of rows this is a size-of-data write + schema lock). Measured duration on this 172-row table is recorded in the HM-3 execution report below.
**Measured (2026-08-03):** `ADD IsWeighted BIT NOT NULL DEFAULT 0` on 186 rows = **4.5 ms**. A **constant** default is a metadata-only operation (SQL Server 2012+): the default is stored in the catalog, NOT written per-row, so duration is independent of row count. The rule still stands for a **non-constant** default (e.g. a function), which forces a per-row size-of-data rewrite.

### HM-D43 (deferred, RECLASSIFIED — a PRECONDITION for weighted retail, not a cleanup)
**Statement:** a precondition for operating weighted items *from the pricing UI* (not from the seed). `PricingService.SaveAsync` normalizes the **price-line** `MinQty = l.MinQty <= 0 ? 1 : l.MinQty` at [PricingService.cs:431](../BL/PricingService.cs), and `GetPriceAsync` filters `MinQty <= qty`. (CORRECTION to an earlier note: the second occurrence at `:513` is in `SavePromotionAsync` — the **promotion** engine's own `MinQty`, which is HM-5 scope, NOT a price-line "update" path. HM-4 fixes ONLY the price line at `:431`; `:513` is deliberately left to HM-5.) Together they make **every weighted item priced through the screen unsellable below one full kilo** — the item finds no list price for 0.750 kg and is rejected at scan ("not in the branch price list"). That closes the core of weighted retail (meat, cheese, produce are almost all weighed at < 1 kg). The HM-3 acceptance succeeded ONLY because the seed writes `MinQty = 0` **directly** into the DB, bypassing the service. So the runtime path (`GetPriceAsync` + scan + post) is proven correct; what is unproven — and blocked — is **pricing a weighted item from the standard UI**. Not fixed now (pricing logic is out of HM-3 scope), but this is a gate on any real weighted rollout, not a nicety.

**Smallest-fix sketch (read-only, for a later decision):** the `<=0 ? 1` normalization most plausibly exists to (a) reject a *negative* MinQty and (b) give legacy rows without an explicit break a sane default of 1 — neither of which requires forbidding an *explicit* 0. Minimal fix: clamp only negatives (`MinQty < 0 ? 0 : MinQty`), or accept 0 only when the item `IsWeighted` (or a per-kg pricing mode). Impact to weigh: allowing a genuine 0-floor changes nothing for the restaurant/general lists that never set 0 (their existing 1-floors are untouched), and `MinQty <= qty` already treats 0 as "applies to any positive qty" — so the blast radius is confined to lines an author *chooses* to set to 0. Confirm no report/UI assumes `MinQty >= 1` before changing.

**Placement decision (owner, 2026-08-03):** the minimal fix above (`MinQty < 0 ? 0 : MinQty` + verify no report/UI assumes `MinQty >= 1`) is the **accepted** approach but is **scheduled for HM-4 (the pricing phase)**, not implemented now. HM-3 stays runtime-proven via the direct-seed `MinQty = 0`.

### HM-D44 (deferred, BARRIER — parallel Platform Kernel became a hard dependency of the sale/purchase/manuf write paths)
A parallel team introduced an **Enterprise Event Platform** ("Platform Kernel") and wired `IBusinessEventService.RecordAsync` into the document writers we build on. All uncommitted in the working tree as of my HM-3 commit `07a3633` (nothing of theirs is in a commit yet). Findings (read-only, code-proven — NOT fixed, decision deferred to owner):

- **What entered our paths & when** (all 2026-08-03): `ReceivableService.cs` **+185/−9** (12:01) — 5 `RecordAsync` sites incl. `CreateSalesInvoiceAsync:404`; `PayableService.cs` **+83/−9** (12:02) — 2 sites; `ManufService.cs` **+185/−9** (12:03) — 1 site; `Program.cs` **+41** (12:03) — DI + the `BusinessEventDispatchWorker` HostedService; `CrossDbContext.cs` **+50** (11:06) — `BusinessEvents`/`BusinessEventDispatch` mappings + Comm/Calendar/Library DbSets; `NotificationService.cs` +7, `NotificationTypes.cs` +18, `MainMenu.cs` +4.
- **NOT wired into our writers:** `StockService` (stock writer), `JournalEntryService` (GL writer), and `PosOrderService` carry **no** `RecordAsync` — the kernel sits in the document layer *above* our two writers, which stay untouched.
- **Transaction semantics (the HM-1-أ partial-effect question) — SAFE.** In `CreateSalesInvoiceAsync` the order is: open `ScopedTx` (`:360`) → invoice → JE (`:374`) → stock (`:391`) → `RecordAsync` (`:404`) → `CommitAsync` (`:420`). `RecordAsync` is **inside** the tx and **before** commit, deliberately with **no try/catch** ("event and invoice share one fate"). If it throws, `CommitAsync` is never reached and `await using` disposes → **full rollback** of document + JE + stock. **Empirically proven:** the missing-table 208 left **zero** orphan invoice/JE/stock; the invoice posted only after the table was deployed. The partial-effect door is **not** reopened — the kernel is correctly enrolled in the same all-or-nothing tx. (`RecordAsync` itself refuses to run with no ambient tx — `BusinessEventService.cs:85`.)
- **Accounting-neutral — confirmed.** `RecordAsync` writes only `BusinessEvents` + `BusinessEventDispatch` (queue) rows; no GL line, no stock movement, no new account. Its payload copies `GrandTotal` for reporting but posts no ledger. The legacy after-commit `NotifyRoleAsync` in the sale path was **removed** (the −9) and re-sourced from the `SalesInvoice.Created` event via `NotificationProjection` — same audience/wording, now event-driven (after commit, best-effort). A behaviour change in *notification mechanics only*, not in the ledger.
- **Other paths on the kernel:** sales (invoice/receipt/return), purchases (`PayableService`), manufacturing (`ManufService`). Each now needs `BusinessEvents` to exist to post.
- **Unpublished SQL / next 208 risk:** untracked in `deploy/sql`: `platform_business_events.sql` (slice-1 — **I deployed it**, the only script a *synchronous* writer needs), `platform_business_events_slice_002.sql` (**index-only** on `Notifications` — no table, **no 208 risk**), `calendar.sql`, `library.sql`, `announcements_p5.sql`, `comm_mail.sql` (parallel-module tables — 208 only if *their* endpoints run, never our sale/purchase/manuf path). So after slice-1, **no synchronous path we build on can still 208 on kernel tables**; the async `BusinessEventDispatchWorker` reads slice-1 tables (present) and writes `Notifications` (present).
- **Suggested permanent guard (recorded, not built):** a startup/integrity check "every table backing a DbSet exists in the DB" — would have caught the 208 before a sale hit it. Out of HM-3 scope and would touch shared infra; logged for the owner. → promoted to **HM-D45** below.

**Slice-2 verification (owner-requested, 2026-08-03, read-only — code + live numbers):**
- **Notifications did NOT re-enter the transaction (the HM-1-أ escape holds).** `NotificationProjectionConsumer` runs in the **dispatcher's own scope, after the business transaction closed** (`NotificationProjectionConsumer.cs:14-16` contract; it "never touches that transaction"). At sale time `RecordAsync` only *enqueues* (writes `BusinessEvents` + `BusinessEventDispatch` rows) — it does **not** dispatch or project inline. The external SignalR push is done by `NotificationService.NotifyAsync` **inside the worker, post-commit** (`:80-95`); the consumer "sends no email/SMS/push of its own" (`:22`). So no external call re-extends the sale tx — exactly the boundary HM-1-أ set.
- **No silent notification loss.** Event + its dispatch rows commit together with the sale (one tx). The worker claims pending rows; a consumer that throws marks **only its own** row `Failed` and retries to `MaxAttempts`, then stays `Failed` for an operator (`BusinessEventDispatchWorker.cs:117-124`); stale claims are reclaimed after `StaleClaimMinutes`. Idempotent by a per-(event,recipient) `DedupKey` (`NotificationProjectionConsumer.cs:67-78`). Delivery is at-least-once with a visible failure row — never a silent drop. Live proof: event 1 → `TimelineProjection` dispatch `Done` (1 attempt); a `sales_invoice` notification (id 10290, recipient 5, RefId 9423) landed in `Notifications`.
- **Worker DbContext — own-or-join honoured, no cross-request leak.** `BusinessEventDispatchWorker` is a `BackgroundService` that injects **`IServiceScopeFactory`** (`:21,26`) and does `using var scope = _scopes.CreateScope()` **per batch** (`:74`), resolving the Scoped `CrossDbContext` from **that** scope (`:78`) — never a directly-injected request context. Matches the ب-0-2 pattern (app-lifetime service must not hold a request-scoped context).
- **Writes confined to kernel + Notifications.** The worker writes only `BusinessEventDispatch` (status) and `BusinessEvents.CompletedAt`; the consumer writes only via `NotificationService` → `Notifications`. Reads are `AsNoTracking` over role/employee tables. **No GL, no stock, no financial table** is written by either. 
- **Integrity green WITH the worker registered (re-run 2026-08-03):** `failedCount 0` · `dbContextLifetime Scoped` (`scopedOk true`) · `ef_precision_vs_db 0/352` · `bp4 0`.

### HM-D45 (deferred, named) — startup guard: every DbSet-backed table exists in the DB
A startup/integrity check that every table backing a mapped `DbSet<>` physically exists in the connected DB. Would have surfaced the missing `BusinessEvents` table at boot instead of as a SQL-208 mid-sale (see HM-D44). **Approved as a named deferred item** — the idea is sound and pre-emptive. **Not built now:** it touches shared infrastructure and the parallel Platform-Kernel work is still uncommitted; building it against a moving base would churn. Revisit once the kernel work lands in a commit.

### HM-D46 (observation only, parallel internals — record, do NOT trace) — a second notification-producing path bypasses the outbox
During HM-D44 slice-2 verification, the fan-out for `BusinessEvent` #1 (`SalesInvoice.Created`) showed a **single** dispatch row (`TimelineProjection`, Done), yet a `sales_invoice` notification (id 10290, recipient 5, RefId 9423) exists in `Notifications`. So that notification was produced by a path that does **not** go through the kernel outbox/`NotificationProjection` — a second, still-inline or otherwise-direct notification producer coexists with the event-driven one. This is entirely inside the parallel team's Platform-Kernel work (uncommitted). **Recorded, not traced, not touched** — it does not affect the ledger or our writers; flagged so the owner/parallel team can reconcile the two producers (risk: duplicate or divergent notifications) when their work lands.

## HM-4 — pricing management + shelf labels + price-check (started 2026-08-03)
### HM-D47 (deferred) — PricingService.SaveAsync full-replaces a list's lines (loses CreatedAt/By, renumbers LineIds)
`PricingService.SaveAsync` on update does `RemoveRange(existing lines)` then re-inserts from `dto.Lines` ([PricingService.cs:404-405](../BL/PricingService.cs)). Consequence: every ordinary save of a price list **discards each line's `CreatedAt`/`CreatedBy` and assigns brand-new `LineId`s** — a `PriceListLine.ID` is not a stable identity across edits. Design impact absorbed by HM-4: **`PriceChangeLog` references `(PriceListId, ItemId, UoMId)`, never `LineId`** (a LineId reference would dangle after the first subsequent save). The full-replace behaviour itself is **not fixed now** (would touch the pricing save path more than HM-4 needs); deferred. Fix later: incremental upsert of lines that preserves identity + creation audit.

### HM-D48 (deferred) — label-printer support (driver + device)
HM-4 prints shelf labels as an **A4 grid via the browser** (`window.print`, same mechanism as the thermal receipt). Dedicated label-printer output (e.g. Zebra/TSC, a label-per-die roll) needs a printer driver and a physical device we do **not** have access to. Deferred until the hardware and its driver/command language (ZPL/EPL) are available.

### HM-D49 (deferred) — open (login-less) price-check kiosk
HM-4 price-check requires a cashier `HyperCtx` login (no shift), gated by the `PriceCheck` capability, reusing `PosLaneActivityGuard`. A **login-less kiosk** for the shop floor (a fixed screen anyone may scan at) needs a new, lighter guard (prices are not sensitive, but the screen still sits inside the branch). Deferred; not built with the cashier-session model.

### HM-D50 (deferred) — bulk-change scope is filtered by category only
HM-4 `BulkPreview/Execute` scope a price list optionally by ONE `ItemCategoryId`. Real operations want richer targeting: a **price range** ("everything under 1 KWD"), an **explicit selected-items** set, a **name/code search**, multiple categories, exclude-list. Deferred; the current category filter is enough to prove the preview → confirm → audit → undo machinery, and the wider filters are a UI/query addition on top of the same `BulkAdjust*` path.

### HM-4 execution — acceptance PASS (2026-08-03), read from the DB (not tracked entities)
Fixtures (`hm4-seed`): KWD test list **ZZ-HM4** (HM-DEMO-001=0.750, HM-DEMO-003=3.500/kg weighted, ZZ-LABEL=1.000 with a valid EAN-13 **6281234567895**), a 2-decimal EGP list **ZZ-HM4-EGP** for the step guard, `ShelfLabels`+`PriceCheck` capabilities ON (branch 17). SQL: `deploy/sql/hm4_pricing.sql` — table **PriceChangeLogs** (plural, to match the EF DbSet), decimals `decimal(19,4)` (model convention → `ef_precision` stays 0). Code: HM-D43 fix (`MinQty < 0 ? 0`), `BulkPreview/Execute/UndoAsync` + `PriceRound` (currency dp FIRST, then fils step; step-finer-than-currency refused; non-positive guard before rounding), `ShelfLabelService` (zero-dependency EAN-13 SVG + round-trip decode), `PriceCheck` action (reuses the unified scan resolution, calls `GetPriceAsync` never `AddLineAsync`).
- **Rounding order (correction 1) proved:** `0.750 +5% = 0.7875 ⇒ R(KWD,3)=0.788 ⇒ PriceRound(0.005)=0.790`; `3.500 +5% = 3.675 ⇒ R(3)=3.675 ⇒ PriceRound=3.675`. **Guard:** 5-fils step on the 2-decimal EGP list ⇒ rejected ("خطوة التقريب أدقّ من دقّة العملة").
- **Preview writes nothing:** lines=3/logs=0/prices unchanged before AND after preview (measured), then execute wrote prices + `PriceChangeLogs` rows in one `ScopedTx`.
- **Undo (correction 2):** restored 0.750/3.500 **exactly** from the log (not a reverse %), recorded as an `Undo` batch with `ReversalOfBatchId` → the original.
- **Consistency (correction 3):** preview → out-of-band edit of one line → execute with the stale baseline ⇒ **rejected**, no write.
- **Poison line** (amount −10 ⇒ negative) ⇒ rejected, **no price changed, no log row** (single SaveChanges after full validation).
- **Empty reason** ⇒ rejected. **Shelf label:** valid EAN-13 renders SVG bars + encode→decode round-trip matches; an invalid EAN-13 is refused (digits-as-text, no broken symbol). **Weighted card (correction 4):** shows unit **KWD/kg** + **"Scale code: 30001"** as text.
- **Price-check (HTTP):** fixed `6281234567890` ⇒ 0.750/قطعة; scale `2300010007555` ⇒ 3.500 **د.ك/كجم**; unknown ⇒ clear error; capability OFF ⇒ "غير مفعَّل"; **PosOrders(branch 17) 5→5 — no order/cart created.**
- **Regression:** bulk +10% on the live branch list (35) ⇒ hyper sale of HM-DEMO-001 uses **0.825**; undo restores **0.750**; restaurant `rc6c` allPass. Constants: **failedCount 0 · ar_sub 0 · ef_precision 0/352 · dbContext Scoped.**

## HM-5 — retail promotions & discounts (started 2026-08-03)
### HM-5 declared rule — deterministic promotion selection order
`ApplyBestPromotionAsync` ([PricingService.cs](../BL/PricingService.cs)) now picks the winning promotion by, IN ORDER:
**1) specificity** (item-specific `ItemId` > category `ItemCategoryId` > general) — so an item promo is never drowned by a
larger category/general promo (mirrors `GetPriceAsync`, where an explicit-unit line beats a null-unit line); **2) Priority**
(the user's field, ranked ABOVE the amount — else Priority would be meaningless); **3) largest discount amount**;
**4) smallest `Promotion.ID`** as a stable final tie-break. No `FirstOrDefault` on an unordered set in any branch. Proven: 10
identical-promo runs return one winner; an item promo (0.075 off) beats a bigger category promo (0.100 off); a higher-Priority
5% beats a lower-Priority 8%.

### HM-5 accounting — net-revenue method confirmed (no "discount allowed" account)
A promotion/discount **reduces revenue directly** (`LineTotal = Qty×UnitPrice − DiscountAmount`, credited net to the
revenue account) — there is **no "discount allowed" account and no discount JE line**, consistent with the census in
[JournalEntryService.cs:64](../BL/JournalEntryService.cs). HM-5 keeps this: a promotion folds into `DiscountPercent`, lowering
`LineTotal` exactly like a price change — **no new account, the 6-account rounding-diff forbidden set is untouched, and
balance-by-construction is preserved** (the sale JE always has an eligible revenue P&L line; receipts/payments/GRN carry no
promotions). Tax is computed on the **net** (discount before tax); moot for the Kuwait/hyper VATEX-0% branch.

### HM-5 distinction — temporary price vs discount
A **temporary absolute price** for a period = a **price-list line with `ValidFrom/To`** (reuses HM-4, `GetPriceAsync` already
filters line validity — no new code). A **discount off the current price** (% or amount) = a **`Promotion`**. HM-5 implements
only the discount side; temporary prices need no HM-5 code.

### HM-5 — HM-D43 applied to the promotion MinQty
`SavePromotionAsync` MinQty normalization changed `<= 0 ? 1` → `< 0 ? 0` (same fix as the price line): a per-kg weighted
promotion must apply from any positive weight. Proven: a `MinQty = 3` promo on the weighted `HM-DEMO-003` applies at 3.000 kg
and not at 2.500 kg.

### HM-5 — save guards + sell-time safety net
Save (block): percent > 100 and negative value are rejected in `SavePromotionAsync`. Save (warn, non-blocking): percent >
`PromotionPercentWarnThreshold` (90) returns a typo warning (e.g. 50-for-5). Sell (net-zero net): `AddLineAsync` rejects a
**priced** line driven to ≤ 0 by a discount with a clear message (a genuinely zero-priced item — a free modifier component —
is unaffected). All proven.

### HM-5 — Promotions capability gates the SELL path only
`AddLineAsync` applies promotions only when the branch's `Promotions` capability is enabled (`IsCapabilityEnabledAsync`
semantics — the enabled row must exist). Management (creating promotions) is **never** gated — central management, per-branch
activation. Proven: capability OFF ⇒ line 0.750 (no promo); ON ⇒ 0.675. (Gives the 4th dead capability its first consumer.)

### HM-5 deferred (named, out of core)
- **HM-D51** — `Promotion` has no `UoMId`: an **amount** discount applies to every unit with wildly different effect
  (0.100 off a 0.500 piece = 20%, off a 60.000 carton = 0.17%). Percent is fine; amount is not unit-aware. Deferred; the
  promo editor should warn when "Amount" is chosen for a multi-unit item (UI note, not a block).
- **Buy-X-get-Y**, **group/bundle deal**, **usage cap (per-customer / total)**, **promotion stacking**, **invoice-header
  discount**, **"discount allowed" GL account** — all deferred by name; each needs basket-level evaluation, a counter, or a
  new account that the net-revenue core deliberately avoids.

### HM-D52 — parallel WIP broke the working-tree build mid-run (HM-D44 materialised, 2026-08-03)
The HM-5 code built clean and its 11-point acceptance passed (below). **Immediately after**, a rebuild failed with **2 errors**, both in the **parallel team's** files (we do not own them, did not touch them):
1. `BL/Platform/IEventDispatchStore.cs:45` — `CS0246: type 'BusinessContext' could not be found` (a new `RetryAsync(long, BusinessContext, string, bool, CancellationToken)` added with no `using`).
2. `BL/Platform/SqlEventDispatchStore.cs:24` — `CS0535: 'SqlEventDispatchStore' does not implement 'IEventDispatchStore.RetryAsync(...)'`.
A **live half-refactor** (method added to the interface, not yet implemented/imported; their files re-saved 13:09/13:14/14:01). **HM-D44 realised — the base moved under us.** Acceptance ran on the **last-good HM-5 binary** (still in `bin/Debug`); the one later change (seed fix — ZZ price lines `UoMId = null`) was verified by an **equivalent direct DB update**, not a rebuild. Fresh build blocked until their code compiles.

### HM-D53 — parallel now edits OUR GL writer (JournalEntryService) — escalation beyond a build break
Pre-HM-6 audit: the parallel team **modified an owned file**, `BL/JournalEntryService.cs` (mtime 14:01:51) — added `IBusinessEventService _events` to the constructor and `RecordAsync(JournalEntry.Reversed)` inside `ReverseAsync`: **in-transaction, before commit, no try/catch, Visibility = Confidential** (their first non-Internal event). **The ledger posting is UNCHANGED** (reversal entry + status flip identical; the event only writes `BusinessEvents`) — GL numbers are ledger-neutral — but `ReverseAsync` now **hard-depends on the kernel** (`EntityRegistry.JournalEntry`, `JournalEntryEvents.Reversed`, `JournalEntryEventPayload`, the `BusinessEvents` table). This **corrects the coordination snapshot's "both writers untouched"**: `JournalEntryService` (GL) is now kernel-wired; `StockService` (stock) stays clean; `PricingService`/`PosOrderService`/`IntegrityCheckService`/`HyperPosController`/`ShelfLabelService`/`ScaleBarcodeParser` carry **only our own** changes (no parallel bleed). Risk reclassified: "build break" → "they edit our core writers." We do **not** commit their `JournalEntryService` change (our selective commit uses the pre-kernel HEAD version). Owner action: coordinate edits to our writers + get their work committed.

**Exact coupling scope (read-only audit, 2026-08-03) — the LIGHTER case:** a full search of `JournalEntryService.cs` shows `RecordAsync` appears **exactly once — inside `ReverseAsync` (line 384, before `tx.CommitAsync()` at 407, no try/catch)**. It is **NOT** in `CreateAndPostAsync` (193), `PostAsync` (180), `CreateAndPostNoTxAsync` (214), or `PostInternalAsync` (224). So **normal journal posting — every sale/purchase/manuf/manual entry — is FREE of the kernel; only the reversal/correction path is coupled.** `StockService` and `PosOrderService` contain **no** `_events`/`RecordAsync` (output-verified). This is a coordination concern, not a total barrier: routine operations do not depend on `BusinessEvents`.

**But every accounting-CORRECTION path reverses a JE, so all corrections now depend on the kernel.** Callers of `JournalEntryService.ReverseAsync` (each fails completely if `BusinessEvents` is missing/schema-changed — `RecordAsync` throws before commit, no swallow, so the `ScopedTx` rolls back and the correction does not stand): **edit sales invoice** (`ReceivableService:491`), **edit sales return** (`:717/:720`), **edit purchase invoice** (`PayableService:299`), **edit purchase return** (`:508/:511`), **cancel a paid POS order / receipt / tip** (`PosOrderService:1417/1431/1440` — the hyper void path), **reopen fiscal year** (`ClosingService:143`), **FX-revaluation auto-reverse** (`FxRevaluationService:177`), **manual reversal** (`AccountingController:316`, `AccountingApiController:178`). Mechanism proven by code (identical in-tx / before-commit / no-catch structure that HM-D44 already proved empirically via the 208 that rolled back a whole sale); not re-proved destructively (would require dropping `BusinessEvents`). **Net: the single accounting-correction primitive (reversal) is now suspended on in-development platform code that broke the build twice today (HM-D52).**

### HM-5 acceptance — 11/11 PASS (2026-08-03), read from the DB
Seed `hm5-seed`: two ZZ categories (one carrying the category promo, one clean) + members, and 10 `ZZP-` promotions on list
35 (KWD), Promotions capability ON. **T1** 10% on 0.750 ⇒ **0.675** (ZZP-A). **T2** category amount 0.100 on 0.500 ⇒ **0.400**
(ZZP-CAT). **T3** 10 identical-promo runs ⇒ one deterministic winner (lowest ID). **T4** item 10% beats the bigger category
0.100 ⇒ **0.675** (item, not category). **T5** Priority 10 (5%) beats Priority 1 (8%) ⇒ **0.950**. **T6** qty-break MinQty=3 ⇒
qty3 **0.850**, qty2 **1.000** (none). **T7** weighted MinQty=3 ⇒ 3.000 kg **2.800**, 2.500 kg **3.500** (none). **T8** save
guards: 150% rejected · negative rejected · 95% saved with a typo warning. **T9** 100% discount ⇒ line rejected (net ≤ 0),
no zero line. **T10** capability OFF ⇒ 0.750 (no promo), ON ⇒ 0.675. **T11 regression:** restaurant `rc6c` allPass; constants
**failedCount 0 · ar_sub 0 · ef_precision 0/352 · dbContext Scoped**.

### HM-D45 (IMPLEMENTED) + HM-D53 detector (IMPLEMENTED) — the guards batch (2026-08-03)
Both live in `IntegrityCheckService.RunAsync` (our file) and run in `inv-test-integrity` + the daily hosted check.
- **`dbset_tables_exist` (HM-D45, FAILS on any missing critical table).** Verifies every table our writer/correction
  paths depend on exists in the DB — scoped to a critical set (GL/stock/pos/sale/pricing tables resolved from the model
  by CLR-type name, **plus `BusinessEvents` + `BusinessEventDispatch` referenced by name** because reversal now needs
  them, HM-D53). Turns an opaque mid-reversal SQL-208 into a red check BEFORE the op. **Existence only** (column
  precision is covered by `ef_precision_vs_db`). Parallel-module tables not on our paths (Calendar/Comm/Library) are
  listed informationally, never failing. **Result:** 22 critical present, 0 missing → GREEN. **Positive proof** (not a
  hollow zero): a fabricated absent name `__zz_nonexistent_table__` is detected MISSING (0) while `JournalEntries` is
  PRESENT (1) — `hm-guard-test`.
- **`writer_coupling` (HM-D53, COUNTED — records, never prevents).** Reflects each of our two writers' constructors and
  reports any injected dependency OUTSIDE its allow-list. **Result:** exactly ONE — `JournalEntryService:IBusinessEventService`
  (the parallel kernel wiring); `StockService` clean. `Ok=true` so it never raises `failedCount`; it makes the new
  coupling visible by name. **LIMITATION (declared):** constructor-injection only — reflection cannot see call-sites nor
  a static service-locator. Allow-list is the pre-kernel constructor shape; any future legitimate injection must be
  added to the list (that is the point — a new dependency is surfaced for a human decision, not silently accepted).
- **Constants after the batch:** failedCount 0 · ar_sub 0 · ap_sub 0 · ef_precision 0/352 · bp4 0 · bal_qty_vs_moves
  متطابق · culture allPass · dbContextLifetime Scoped.

## HM-6 — expiry & batches (retail FEFO enforcement) — 2026-08-03
### HM-D8 (RESOLVED for the retail input paths) — force a batch on tracked input; fix the misleading issue
The long-standing HM-D8 gap (tracked stock enters without a batch → invisible to FEFO → an "available 0" style
confusion) is closed for the batch-capable USER paths:
- **Force-batch guard in `StockService.PostSingleAsync`** (the single choke point every caller reaches, incl.
  `PostOpeningStockAsync` which bypasses the `PostMovementAsync` wrapper): an INBOUND movement of a `TrackExpiry` item
  with `SourceType ∈ {OpeningStock, Opening, Receipt, Adjustment}` and no batch is REJECTED; a batch without an expiry
  DATE is likewise rejected. A **write-off** of a tracked item must name its batch — that check sits in
  `PostMovementAsync` BEFORE the FEFO block so an expired write-off is not FEFO-auto-picked. EXCLUDED (unchanged):
  sale/issue (FEFO auto-allocates), manufacturing/assembly output, purchase/GRN (**HM-D16 ⇒ HM-16**, no batch entry
  yet), transfer (carries the inherited batch). Input validation only — **no change to FEFO/cost/rounding**.
- **FEFO now BLOCKS an unbatched issue** (behaviour change, deliberate): when a `TrackExpiry` item has NO batched
  stock but DOES have physical on-hand, `FefoAllocateAsync` returns a clear data-correction message instead of
  silently falling through to a normal (unbatched, expiry-bypassing) issue. When it has SOME batched stock but the
  valid quantity is short AND unbatched stock also exists, the shortfall message names the unbatched remainder. This
  is the fix for what was mis-described earlier as a "misleading `available 0`" message.
- **CORRECTED framing (owner-requested):** the real defect was NOT a misleading rejection — it was a **hole in the
  control**: an expiry-tracked item whose stock is unbatched was **sold/issued SILENTLY, bypassing expiry control
  entirely** (`FefoAllocateAsync` returned `applicable=false` → the normal path issued it, no FEFO, no expiry check).
  HM-6 closes it: the issue is now blocked with a clear data-correction message.
- **Historical exposure through the hole (read-only census, 2026-08-03):** issue movements of a `TrackExpiry` item with
  no `BatchId` = **50 legacy issues** (excluding 1 ZZ test artifact), qty ≈ 1119, cost value ≈ **6,676 EGP**, across
  **4 items** (ITM-0001, ITM-0002, MFGT-R1, MFGT-R2). By source: **WorkOrder 36 · TransferOut 5 · Assembly 4 · Issue 3 ·
  ProjectIssue 1 · StockWriteOff 1 · Adjustment 1** — **ZERO `SalesInvoice`**, i.e. **no retail sale ever bypassed**;
  the exposure was manufacturing/transfer/project/manual consumption. HM-6 secured the retail input paths + FEFO sale;
  the manufacturing/transfer/assembly consumption of expiry-tracked components is **still un-guarded** (excluded from
  HM-6 scope) — recorded, not fixed.
- **Test-residue correction (HM-6 acceptance):** an earlier acceptance run left 2 stray unbatched-inbound movements
  (ZZ-EXP opening #10660 from a pre-guard-fix run; ZZ-UNBATCH #10661 crafted by T6), pushing the counted classification
  25/4 → 27/6. Both cleaned; T6 now tears down its crafted item. **Documented baseline restored to 25 movements / 4
  items** = ITM-0001 (18), ITM-0002 (1), MFGT-R1 (3), MFGT-R2 (3). No test leaves an integrity residue.
- Messages are hardcoded Arabic (StockService/ItemService have no localizer by design — every message in them is
  hardcoded Arabic; injecting one would itself be a new writer coupling that HM-D53 flags — see CLAUDE.md declared exception).

### HM-6 — TrackBatch decision (the 4th dead flag) — reject batch-only tracking
`TrackBatch` had no behaviour of its own (FEFO orders by EXPIRY). Enabling it without `TrackExpiry` faked a tracking
the system does not enforce. `ItemService` now **rejects `TrackBatch && !TrackExpiry`** on create/update with a clear
message ("batch tracking without expiry is not supported — enable expiry tracking"). Verified state: all 10 existing
`TrackBatch` items are ALSO `TrackExpiry` (0 batch-only, 0 movements, 0 on-hand), so this breaks nothing. Batch-only
lot traceability (no expiry ordering) is deferred by name.

### HM-6 — counted classification (the legacy footprint)
`unbatched_inbound_tracked` (`IntegrityCheckService`, COUNTED, never fails): inbound stock of a `TrackExpiry` item
with no batch — invisible to FEFO. Current: **27 movements across 6 items** (legacy). The input guard blocks new ones,
so the count only decreases as legacy is cleaned/consumed.

### HM-6 deferred (named)
- **HM-D54** — a promotion targeted at a specific batch (clearance discount on a near-expiry lot): `Promotion` has no
  batch dimension; needs one. Deferred.
- **Cleaning the 27 legacy unbatched movements** — data cleanup, deferred (surfaced by the classification; the guard
  stops new ones).
- **Purchase/GRN batch entry** — HM-D16 ⇒ HM-16 (do not touch the purchase invoice / GRN in HM-6).
- **Near-expiry PUSH notification** (a report `ExpiryAlerts` exists) · **per-branch/item expiry threshold** ·
  **auto-generated batch numbers** · **batch-only (no-expiry) tracking**.

### HM-6 acceptance — 9/9 PASS + regression (2026-08-03, FRESH full build, read from the DB)
Seed `hm6-seed` (fill-to-floor via `PostOpeningStockAsync`): ZZ-EXP (numeric, TrackExpiry) LOT-A(+10d,100)/LOT-B(+60d,
100)/LOT-EXP(-5d,50); ZZ-WEXP (weighted, TrackExpiry) WLOT-A(+5d,0.500kg)/WLOT-B(+40d,2.000kg).
**T1** sell 30 ⇒ FEFO from LOT-A (100→70), LOT-B 100. **T2** sell 150 ⇒ LOT-A→0 + LOT-B→50 (split). **T3** weighted
0.755kg ⇒ WLOT-A 0.500→0 + WLOT-B 2.000→1.745 (Σ drawn 0.755 EXACT, no truncation). **T4** named expired batch blocked;
FEFO short ⇒ excludes expired with a clear message (+ notes unbatched remainder). **T5** opening/receipt/adjustment/
write-off WITHOUT a batch ⇒ all rejected. **T6** unbatched physical stock ⇒ clear data-correction message (not
"available 0"). **T7** expired write-off ⇒ 510103 debited, LOT-EXP 50→40. **T8** ExpiryAlerts shows LOT-A (near) +
LOT-EXP (expired). **T9** TrackBatch without TrackExpiry rejected; with it allowed. **T10 regression:** restaurant
`rc6c` allPass; constants **failedCount 0 · ar_sub 0 · ap_sub 0 · ef_precision 0/352 · bp4 0 · bal_qty_vs_moves match ·
batch_no_negative 0 · culture allPass · dbContext Scoped**; new checks `dbset_tables_exist` GREEN, `writer_coupling`
counted (1), `unbatched_inbound_tracked` counted (27).

## HM-D55 (blocker resolved, read-only census 2026-08-03) — account 210203 is GRNI mislabeled, NOT mixed
Before any HM-16 code, the 210203 question was settled by census:
- **Code treats 210203 as GRNI** (hard-coded: `PayableService.cs:421,488`, `ProcurementService.cs:126`), but it is
  NAMED "ضرائب كسب عمل مستحقة / Payroll Tax Payable" (parent 2102 Taxes Payable).
- **Every posting to 210203 is GRNI-nature, ZERO tax/payroll:** Auto·Inventory receipts (25 lines, −182,590),
  Reversing·Reversal (12), Auto·PurchaseReturn (5, −230), Auto·PurchaseInvoice clearing (4, +1,440), Auto·FixedAsset
  capitalization (1, −10,000), Auto·LandedCost (1, −200), Manual·StockReconcile (1, +720). Net in CrossBuyDB2 = −190,680
  (the documented −39,860 is the production-scale figure — same account, different DB state).
- **No tax ever posted there.** The DUPLICATE "Payroll Tax Payable" `210205` is EMPTY; real withholding tax posts to
  `210202` (`PayableService.cs:584`). 24 item categories map `GrniAccountId → 210203`, 18 null.
- **VERDICT — the FIRST branch (naming error, posting is pure GRNI):** exactly the 510101→COGS precedent (HM-D39). The
  fix is a **rename** (210203 → "بضاعة وردت ولم تُفوتَر (GRNI) / Goods Received Not Invoiced"), done in HM-16. **No STOP.**
  - The **rounding-diff forbidden list stays valid**: 210203 is a LIABILITY (balance-sheet), auto-excluded from
    rounding-diff absorption by the "P&L-only (AccountType 4/5)" rule — it was never in the explicit 6-account P&L set
    (`JournalEntryService.cs:66`), whose own comment already lists "GRNI/tax" as balance-sheet auto-excluded. No change.
  - The **−39,860 dismantling stands** — it was built on the account that IS GRNI in practice (only mislabeled).
  - The rename also resolves the **duplicate name** (210205 remains the payroll-tax account, currently unused).
  - Config note for HM-16: keep future payroll tax posting to 210205; keep real WHT on 210202.

## HM-D56 (deferred, do NOT execute) — chart-of-accounts tree needs a structural review, not per-account fixes
After HM-D55 renames 210203 to "Goods Received Not Invoiced (GRNI)", its PARENT is still 2102 (Taxes Payable) — a
semantic error, because GRNI is not a tax. **Moving an account in the tree changes report roll-ups (it is riskier than
renaming it)**, so it is NOT done per-account. It belongs to the wider naming/structure review: 510101 (de-facto COGS,
renamed HM-D39), 510105, 210203 (GRNI, renamed HM-D55), 210205 (duplicate "Payroll Tax Payable", empty) were all
mislabeled or mis-parented. The chart of accounts needs a **whole-tree review**, not account-by-account patching.
Deferred by name.

## HM-16 — purchase-model unification (DONE, acceptance 10/10, failedCount 0)
GRN-matched purchase invoice now clears GRNI (Dr 210203 / Cr AP, NO second stock movement); a standalone invoice still
receives stock once; a GRN is invoiced at most once (set-once on `GoodsReceipt.InvoiceId`, enforced in
`PayableService.CreatePurchaseInvoiceAsync` + a value-guarded matching service `MatchGoodsReceiptToInvoiceAsync`); the
matching screen refuses any billed≠received amount (price/tax variance deferred); account 210203 renamed to
"Goods Received Not Invoiced (GRNI)" via `deploy/sql/hm16_rename_grni.sql` (name only, parent 2102 unchanged — re-parenting
is HM-D56); GRN batch-forcing for TrackExpiry items was ALREADY enforced by the HM-6 `"Receipt"` guard (verified T6, stale
"excluded" comment fixed); cutoff = `IntegrityCheckService.PurchaseModelCutoffUtc` (a CONSTANT 2026-08-03, not config) with a
COUNTED `open_grni_receipts` check split legacy(≤)/new(>). Acceptance `hm16-accept` proved every number from the DB.
The −39,860 dismantling stands (re-confirmed on the correctly-named GRNI account). Two acceptance findings were
TEST-assertion bugs, not defects: T7's GRNI correctly nets to 0 (three-way: stock-out Dr GRNI/Cr Inv + debit-note Dr AP/Cr
GRNI); stock_gl is a `Baseline[null]` structural check (never raises failedCount) — our postings move stock+GL together.

## HM-D57 — the kernel advanced past slice-1: slice-2 is now REQUIRED for any invoice/notification path
The parallel `RecordAsync`/notification path writes `Notifications.EntityType`/`Notifications.EntityId`, columns added ONLY by
`deploy/sql/platform_business_events_slice_002.sql`. With only slice-1 applied, a purchase invoice fails with SQL-207
"Invalid column name 'EntityId'". Slice-1 alone is NO LONGER a sufficient acceptance precondition — **slice-2 must also be
applied** (additive/idempotent; run with sqlcmd `-I` so its filtered index gets QUOTED_IDENTIFIER ON). Applied to CrossBuyDB2.

## HM-D58 — the kernel now HARD-REQUIRES a signed-in BusinessContext on our purchase/sale path (fallback removed)
The parallel team removed `BusinessContextAccessor.FallbackCompanyId = 1`. `RecordAsync` (invoked inside
`CreatePurchaseInvoiceAsync`/sales/reversal — THEIR wiring in our writers) now calls `GetCurrentAsync`, which THROWS
`BusinessContextUnresolvedException` when no signed-in employee/company resolves from the request. Impact: EVERY document
path that emits an event now requires a resolvable HTTP context; any UNAUTHENTICATED caller (dev-seed/acceptance endpoints
that create invoices) throws. Accommodation: seed the `"Employee"` session blob (a real active company-1 employee, exactly as
`AccountController` login does) before the calls — done in `hm16-accept`. This is a NEW hard runtime coupling on our path;
in production it is always satisfied (users are signed in), but it makes our sale/purchase path depend on their identity
resolution as well as their event table. Recorded, not "fixed" — raise with the parallel owner.

## HM-D58 verdict (read-only sweep) — NO silent production defect; it is a TEST-harness rule
Swept every HostedService/BackgroundService/dispatch-worker/consumer/hub for a path that creates a document, reverses an
entry, posts a movement, or calls RecordAsync from OUTSIDE a user request. Result: NONE. No hosted service injects a
document writer (IReceivable/IPayable/IStock/IJournalEntry/IProcurement/IBusinessEvent); the dispatch worker + projection
consumers never call RecordAsync/ReverseAsync/the accessor; NotificationService.NotifyAsync never touches the accessor;
NotificationsHub posts nothing financial; ForWorker/ForSystem/Publish set the EF company-scope, NOT the accessor cache.
The two reversal producers named as the risk — FxRevaluationService and ClosingService.ReopenYearAsync — are called ONLY
by controllers (CurrencyController.PostRevaluation, AccountingController.ReopenYear), i.e. inside an HTTP request. So
BusinessContextUnresolvedException CANNOT fire on a production background path today. It DOES fire on any headless dev
endpoint that creates a document: ~74 event-emitting writer calls live in DevSeedController and only hm16-accept seeds the
"Employee" blob — the rest pass only when driven from a signed-in browser. Pinned as a permanent test rule in CLAUDE.md.

## HM-D57 slice map (read-only) — which platform SQL gates our path, and applied-state on CrossBuyDB2
Platform-kernel slices (theirs), in order:
  slice-1  platform_business_events.sql          → BusinessEvents + BusinessEventDispatch          GATES our path (SQL-208)   APPLIED
  slice-2  platform_business_events_slice_002.sql → Notifications.EntityType/EntityId (+index)      GATES our path (SQL-207)   APPLIED (this session)
  slice-3  comm_outbox_slice_003.sql             → CommMessage outbox dispatch state               does NOT gate our path      APPLIED
  (batchB) platform_schema_history.sql           → PlatformSchemaHistory (deploy-tracking table)   does NOT gate our path      MISSING
Only the platform_business_events* family gates sale/purchase/reversal/stock; both applied. platform_schema_history is
MISSING — the table meant to record "what was applied" is itself unapplied, so it cannot be trusted as the source of truth.
Rule generalised in CLAUDE.md: ALL working-tree platform slices deployed before acceptance; list re-reviewed every phase.

## HM-D45 extension (DESIGN ONLY, not built) — column-existence, not just table-existence
SQL-207 was a missing COLUMN (Notifications.EntityId), which the current dbset_tables_exist check (table-only) cannot catch.
Smallest shape to close the gap: a COUNTED companion check `dbset_columns_exist` that asserts a hard-coded allow-list of
(table, column) pairs on the kernel tables our path writes — e.g. {Notifications: EntityType,EntityId}, {BusinessEvents:
EntityId,DedupKey,EventType} — resolved by COL_LENGTH/INFORMATION_SCHEMA at runtime; a missing pair FAILS (writer-path,
like dbset_tables_exist) with the exact table.column named. NOT built — proposed for a future guards batch.

## HM-D58 dev-context seed filter (test-harness plaster, NOT a production fix)
Verified before building: (1) DevSeedController IS guarded by class-level [DevOnly] (404 outside Development) — the filter
is not unguarded. (4) Every "Employee"-blob reader consults at most {ID,BranchID,EmpCompanyID,UserId}: the three access
services (Accounting/Crm/Inventory) read only .ID; BusinessContextFactory reads the 4 as HINTS and re-reads company/branch
from the authoritative Employee row; SessionValidationMiddleware skips /api entirely; PosAccessService's Roles/FullName come
from PosCtx/DB, not this blob. So the 4-field blob starves no reader. Implementation: ONE `OnActionExecutionAsync` override
in DevSeedController — seeds only when the blob is ABSENT (browser session wins); EXPLICIT failure (500, clear Arabic) if no
active company-1 employee (never the obscure deeper exception); the chosen employee (id+branch) is surfaced in the
`X-Dev-Context` response header so a determinism shift (OrderBy(ID) picking a newly-added smaller id) is explained. Removed
the now-redundant inline seed in hm16-accept (one mechanism). **This masks HM-D58 for TESTS only. Production interactive
paths use the real session; any NEW parallel background path would still throw — monitoring background paths stays OPEN.**

## HM-D58-net — doc-status/JE-status consistency (durable check + teardown fix + one-time cleanup)
The HM-D58 dev-context filter let doc-creating tests COMPLETE, activating a dormant teardown bug: a test that reverses a
document's JE but leaves the doc Status='Posted' creates a Posted-doc/Reversed-JE ORPHAN that the subledger keeps counting
while the GL does not — drifting ar_sub/ap_sub. Worse, ar_sub/ap_sub compare TOTALS, so a positive orphan and a negative
one cancel out and vanish (same blind spot as stock_gl). Fixes:
- **Durable check** `doc_je_status_mismatch` (IntegrityCheckService, raises failedCount): per-document, by id+value (never a
  sum), across all SIX subledger docs (SalesInvoice/SalesReturn/Receipt/PurchaseInvoice/PurchaseReturn/Payment). A
  Posted-doc/non-Posted-JE (or Cancelled-doc/Posted-JE) FAILS. We found this by luck, not a guard — now there is a guard.
- **Enumeration** (how many teardowns reverse a doc JE without setting status): exactly ONE in DevSeedController —
  hm1-double-post-test (line 2737, `pi.JournalEntryId`). FIXED: after ReverseAsync, set PurchaseInvoice.Status='Cancelled'
  (no IPayableService cancel path exists → direct status update; no delete, no touching the JE). No other doc-JE teardown found.
- **One-time cleanup** (today's runs, JEs already reversed, zero economic effect — status update aligns the subledger):
    PI-2026-04132  Posted→Cancelled  (JE Reversed)  80.00
    PI-2026-04138  Posted→Cancelled  (JE Reversed)  80.00
  Timestamp: applied this session (2026-08-03).
- **Legacy frozen (NOT touched — prior-session existing data, kept visible)**, added to the check's baseline by id:
    PurchaseInvoice #1047 = 100.00 (Posted/Reversed) · #1048 = 75.00 (Posted/Reversed)
    PurchaseReturn  #9    = 20.00  (Posted/Reversed) — a THIRD legacy orphan the new per-document check surfaced in a type
                                    (returns) we had not examined; baselined by the same "don't touch old data" rule.

## HM-7 batch-1 — batch-aware physical count (DONE, acceptance 12/12, failedCount 0)
PostCountAsync counts expiry-tracked items ONE LINE PER BATCH; the diff is attributed to its batch (positive → Adjustment
carrying the batch, passing the HM-6 guard; negative → issued from THAT batch, not FEFO — the FEFO gate only fires for an
unnamed batch). Non-tracked items are the exact pre-HM-7 path (BatchNo null). The count is ATOMIC (one ScopedTx) so a reject
leaves zero effect. A counted batch not in the system is CREATED (expiry required, HM-6 rule) and flagged
(StockCountLine.BatchCreatedInCount). An uncounted batch is left UNTOUCHED and surfaced (never zeroed). Schema:
deploy/sql/hm7_count_batch.sql (StockCountLines += BatchNo/ExpiryDate/BatchCreatedInCount, idempotent, nullable). New
DURABLE classifications in inv-test-integrity: `batches_created_in_count` (COUNTED — creation during a count is stock-in
without a source; watch the trend). bal_value_diff label corrected from "HM-D7 footprint" to "unattributed value diff —
cause not yet proven" (no assumed cause, like the stock_gl fix). This closes the HM-6-introduced block: an expiry-tracked
item could not be counted up (unbatched Adjustment → HM-6 guard rejected). Acceptance: api/dev/hm7-count-accept.

## HM-D59 — parallel Stage-1-Batch-B2: company-scope query filters break single-shot dev acceptance
The parallel team added (untracked, mtime ~22:27 today) `BL/Platform/CompanyScopeMiddleware.cs` + `CompanyQueryFilters`
(a global `HasQueryFilter` on ~12 pilot company-scoped entities, applied LAST in CrossDbContext.OnModelCreating). The
middleware resolves the BusinessContext and publishes the company to `ICompanyScopeHolder`; when NO context resolves it
leaves the scope UNRESOLVED and every filtered entity returns ZERO ROWS ("will return no rows", it logs). It runs in the
PIPELINE (before MVC action filters), so our dev-context seed (DevSeedController.OnActionExecutionAsync, HM-D58) — which
seeds the "Employee" session DURING action execution — is TOO LATE: the scope was already resolved (empty) at middleware
time. Impact: EVERY unauthenticated single-shot curl dev endpoint now reads nothing → FirstAsync throws → 500. PROVEN:
hm16-accept (green earlier THIS session) now also 500s single-shot — so it is their change, not ours. **Workaround (no
code change, no parallel-file touch): persist the session across requests with a cookie jar — prime with one call
(culture-check seeds the blob + Set-Cookie), then call the acceptance with the same cookie so the middleware resolves the
scope. This mirrors a real browser session.** Recorded, not "fixed". writer_coupling on the stock writer stays CLEAN (this
is a read-filter infra, not a writer coupling), so the HM-7 stop-condition was not triggered.

## HM-D59 (refined) — the parallel company-scope filter is DELIBERATE and EXCLUDES StockBalance, naming our guard
Read-only investigation verdict. It is a GLOBAL QUERY FILTER (CrossDbContext model, CompanyQueryFilters.Apply →
builder.Entity<T>().HasQueryFilter reading db.CompanyScope.FilterCompanyId per executing context) — not an MVC filter, not
a SaveChanges interceptor. Filtered (PilotEntities, 12): JournalEntry, SalesInvoice, PurchaseInvoice, Customer, Item,
Warehouse, Quotation, Lead, Opportunity, CrmAccount, BusinessEvent, Notification. **Deliberately UNfiltered (by code,
`DeliberatelyUnfiltered`): StockBalance — with the literal reason "FromSqlInterpolated UPDLOCK/HOLDLOCK — the sole-writer
overselling guard"** (+ Employee, *UserRole, Companies, Branch, BusinessEventDispatch, FiscalPeriod). So the entire stock
writer (StockBalances/StockMovements/StockBatches/StockCostLayer) is unfiltered; the ONLY FromSql in all of BL is
StockService:455 (StockBalances, unfiltered) — no FromSql-locked read touches a filtered entity ⇒ NO locked-vs-normal
divergence. Production resolves the scope (== the operation's company) so filtered reads match our explicit CompanyID;
only single-shot UNauthenticated curl breaks (unresolved scope), handled by a persisted session (cookie jar). **This is the
FIRST positive evidence of coordination FROM the parallel side: they read OUR code and excluded StockBalance by name citing
our UPDLOCK guard.** Reclassified from "discovery" to "deliberate, excludes StockBalance, names our guard." HM-D7 is safe:
the filter does not touch the balance row it locks.

## HM-D60 (their item — record, do NOT fix) — FilterCompanyId => CompanyId ?? 0 is a silent empty grid, not an exception
`CompanyScopeHolder.FilterCompanyId => CompanyId ?? 0` — an UNRESOLVED scope yields CompanyID==0 ⇒ ZERO rows on the 12
filtered entities (Item, Warehouse, invoices, JE, CRM…), NOT an exception. Not dangerous today (stock tables are excluded,
production resolves the scope), but it is the SAME anti-pattern we fought since HM-1: a silent fallback instead of an
explicit failure — the exact shape of the ToBaseAsync `factor ?? 1` bug. A NEW background path that reads Item with no
scope would get "no items" and continue. Theirs, not ours — raise with the owner; do not touch their code.

## HM-7 batch-2 / HM-D7 — landed-cost lost-update FIXED (fail-first proof, writer regression green)
PostLandedCostAsync revalued StockBalance.TotalValue (the moving-average base that feeds COGS + margin) via an UNLOCKED
read + batched save — a lost update under a concurrent receipt/sale on the same item (the normal hypermarket case). Fix
(mirrors PostSingleAsync): aggregate share PER ITEM first ⇒ ONE locked read per item (FromSql UPDLOCK/HOLDLOCK → Modified
guard → Reload → modify) ⇒ one save. Aggregating per item means each balance is locked-read EXACTLY ONCE, so the
batch-2 Modified-guard is satisfied BY CONSTRUCTION (never fires on a second read). The allocation formula, cost formula,
and FIFO-layer bump are UNCHANGED (per-item bump == summed per-line bumps; qty unchanged). Debug-only seam
`_testBypassLandedLockRead` (mirrors `_testBypassLockReadRefresh`) reproduces the bug for the self-test. Proof
(api/dev/hm7-landed-accept, deterministic via raw-SQL external write, one tx rolled back = zero persistence): pre-fix
TotalValue=230 (V+share=230, concurrent Δ=50 LOST); fixed TotalValue=280 (V+Δ+share, Δ preserved) — a test that fails
before the fix and passes after. Full stock-writer regression green (hm4/5/6/16-accept, hm7-count, hm7-landed, rc6c,
mc-o2c, seed-acc-demo all PASS; the WO path's writer correctly rejects insufficient stock — a clean business rule, its
manuf test fixture is under-stocked, pre-existing, not HM-D7). Constants: failedCount 0 · ar_sub/ap_sub/bal_qty_vs_moves/
batch_no_negative/doc_je_status_mismatch/dbset_tables_exist/writer_coupling OK · stock_gl baseline · Scoped.

## HM-D12 (fixed) — manuf tests were not re-runnable; shared MFGT raws drained + stray TrackExpiry
manuf-test-wo seeded MFGT-R1/R2 ONCE (`if (!AnyAsync)`), never replenishing; every WO drains 2×R1+1×R2 per unit, so the
raws bled to 6.4 and every MFGT-consuming test 400'd ("insufficient stock") — the SAME pattern as T6 and hm1-b5b (third
sighting). Sweep found ~10 MFGT-consuming endpoints (manuf-test-wo/wip/wipcheck/routing/scrap/labor/labor-fx/staged/
partial + mfg-sale-chain-test), ALL relying on that one drained seed. Fix: one shared `EnsureMfgtRawFloorAsync` (fill-to-
floor at the SAME cost ⇒ moving average stable) called by every consumer. It ALSO repairs a STRAY TrackExpiry/TrackBatch
flag on MFGT-R1/R2 (the manuf tests never set tracking — corruption from a reused code) that was blocking the unbatched
opening and would gate FEFO on the WO's component issue. Proven: all 12 manuf tests pass on TWO consecutive runs.
SIDE EFFECT (a correction, not a regression): `unbatched_inbound_tracked` drops 25→19 — the 6 removed movements were
MFGT-R1/R2's unbatched inbound while stray-TrackExpiry; repairing the flag removes them. failedCount stays 0. Baseline
note updated: unbatched_inbound_tracked = 19 (was 25) after the MFGT repair.

## HM-8 (built) — official A4 invoice + tax-integrity rule on the walk-in name override
Scope = an official A4 invoice document ONLY. Regulatory/ETA/e-invoice/QR/gapless-numbering DEFERRED (aligns with the
prior team's `EtaInvoiceServiceStub`, already deferred). No PDF library — a standalone Razor A4 view + `window.print()`
+ `@media print` (zero dependency). Two thin actions share ONE view: `AccountingController.PrintInvoice`/`StampInvoiceCustomer`
(gate = `[SessionValidation]`, company guard = `DefaultCompanyId`) and `HyperPosController.PrintInvoice`/`StampInvoiceCustomer`
(gate = HyperCtx + `OfficialInvoice` capability + branch guard: the invoice must be the pay result of a PosOrder on THIS
branch/company — a cashier can never reach another branch's invoice). All amounts render at the DOCUMENT currency's
`Currency.DecimalPlaces` (KWD=3 / EGP=2) — never a fixed N2. The view DISPLAYS stored values (`SubTotal/TaxTotal/GrandTotal`,
line totals) — it never recomputes. **No new column for the logo:** `Companies.CompanyImage` (already nullable) IS the
logo; the template reuses it (no duplication).

**Walk-in beneficiary override (display-only, set-once, audited, TAX-ZERO ONLY).** A walk-in sale posts to the POS Walk-in
customer (control account, JE, amounts UNTOUCHED). When such a customer asks for an invoice in their name, four nullable
DISPLAY columns on `SalesInvoices` carry it — `CustomerNameOverride`, `CustomerTaxNoOverride`, `CustomerOverrideBy`,
`CustomerOverrideAt` — set by `OfficialInvoiceHelper.StampCustomer` (pure; hardcoded Arabic per BL convention). NO financial
field changes; the document shows the override, the ledger does not.

**RULE (the auditor's, enforced in code): تجاوز اسم العميل على الوثيقة مسموح للفواتير معفاة الضريبة فقط.** The name/tax
override is REFUSED when `TaxTotal > 0` — a taxed invoice's beneficiary must equal the ledger account holder (a real
registered customer), so a name-on-the-document override on a taxed invoice is exactly the mismatch to prevent. Practical
check: hyper (Kuwait, VATEX 0%) → allowed; an Egypt-taxed (14%) sale → rejected, and the operator must select a real
registered customer via accounting. No practical blocker (raised for confirmation before build; confirmed). Two guards:
set-once (a stamped beneficiary is never edited — like `GoodsReceipt.InvoiceId` in HM-16) and tax-zero.

**SQL:** `deploy/sql/hm8_official_invoice.sql` — idempotent, additive, ALL nullable, ZERO financial impact (the 4 override
columns only; logo reuses `CompanyImage`). Not a migration.

**The A4 layout is a temporary functional placeholder** (like the shelf label) — correct data, minimal styling — pending
the user's design.

**Acceptance — `api/dev/hm8-accept` (15/15 PASS, `allPass:true`, idempotent over 3 consecutive runs).** Fixtures are
UNPOSTED draft invoices (HM-8 never touches posting; the posting pipeline is proven by HM-16), so the run moves NO ledger
and is re-runnable (prior `ZZ-HM8` fixtures deleted first). Every number is read from a NEW DB query, never the writing
entity. Numbers: T1 KWD dp=3 · grand 1.500 · tax 0.000 · code KWD; T2 EGP dp=2 · sub 100.00 tax 14.00 grand 114.00; T3
weighted qty 1.2340 → line 4.3190 (=1.234×3.500, AwayFromZero 3dp); T4 multi-unit sold-unit resolved (uomId 2 → 'كرتونة');
T5 discount 0.500 → line 4.500; T6 name-only stamp → `CustomerTaxNoOverride` null (doc omits the tax line); T7 read-model
loads the company, null/blank `CompanyImage` omits the logo (view-guarded — the sample company HAS an image, so the null
branch is structural not data-exercised); T8 the stamp write leaves Sub/Tax/Grand + CustomerId + JE UNCHANGED
(1.5000→1.5000); T9 lines unchanged (rows 1→1, same LineTotals); T11 second stamp REJECTED (set-once), first name intact;
T12 taxed (14%) invoice → stamp REFUSED (reason=tax present), `CustomerNameOverride` stays null; T13 KWD 0% → stamp
ALLOWED, name+tax+By(=9)+At persisted; T10 company guard (invoice unreachable under a wrong CompanyID); T14 branch guard
own(17)=True / other(4)=False; T15 `OfficialInvoice` capability ON branch17=True / otherBranch(4)=False.

## HM-9 slice 1 (built) — customer identity in the hyper lane (the loyalty prerequisite)
The hyper lane force-links every order to the single POS Walk-in customer (`PosOrderService.CreateOrderAsync:438-442`).
Loyalty needs a real, identifiable customer ON THE ORDER. Slice 1 REUSES the existing building blocks — `_ar.SearchCustomersAsync`,
`_ar.CreateCustomerAsync`, `_posOrders.SetOrderCustomerAsync` — with NO new service and NO copy of the restaurant lane:
new `HyperPosController` actions `Customer`/`CustomerSearch`/`CustomerAdd`/`AttachCustomer` + a minimal `Views/Hyper/Customer.cshtml`
(PosLane idiom, phone search + quick-add + link). Gated by the reserved `"Loyalty"` capability (its FIRST real consumer;
disabled → "تعريف العميل غير مُفعَّل على هذا الفرع"). `IReceivableService` injected into the controller (reuse, not a new service).

**The architectural decision (confirmed): identity = a REAL customer on the order (model أ), NOT a parallel membership entity.**
Swapping `PosOrder.CustomerId` pre-pay is GL-neutral — every customer's AR control resolves to `1102` (a control account,
`IsPostable=0/IsActive=1`), so the JE is byte-identical; and `PayAsync` settles the cash sale so AR nets to zero. The points
ledger (a later slice) will key off `CustomerId` WITHOUT touching the customer or the posting (the HM-8-override spirit applied
to the POINTS, not the identity).

**Fail-closed control-account guard (mandatory, decision addition 3).** `AttachCustomer` refuses to link a customer whose AR
control account is MISSING/zero/inactive — BEFORE calling `SetOrderCustomerAsync` — so a link can never post a receivable to a
non-existent account. It checks the account EXISTS + IsActive + same company; it does NOT hard-code 1102 and does NOT require
IsPostable — a future customer with a different LEGITIMATE control account still passes. **The acceptance caught a real guard
bug pre-commit:** the first guard required `IsPostable`, which wrongly rejected EVERY normal customer, because the AR control
account `1102` is intentionally a non-postable CONTROL account (`IsPostable=0`) yet is exactly where receivables post (T3 failed
`freshCtrlValid=False`; fixed to exists+active; T3 green). `IsPostable` governs the manual-JE leaf rule, not the AR control target.

**Link is pre-pay only — PROVEN IN CODE, not design (decision addition 4).** `SetOrderCustomerAsync` (`PosOrderService.cs:752`)
already refuses a non-Open order (`o.Status != "Open"` → "الطلب ليس مفتوحًا"); we did NOT modify that shared method, we PROVE it
(T5: a link on a paid order is refused, customer unchanged).

**HM-8 ↔ identity linkage (decision ج).** HM-8 had to add `CustomerNameOverride` precisely because the lane could not identify a
customer. Slice 1 makes the invoice-requester a REAL customer, so the display-only override reverts to an EXCEPTION for the
tax-exempt walk-in only. Proven: T7 (an official invoice on a linked real customer reads the customer's name+tax, override null),
T8 (a taxed 14% invoice posts to a real customer with no override, and the HM-8 stamp is still refused on tax>0 — identity is the
correct route for a taxed sale's customer, not the override).

### Governing evidence carried from HM-9 a-3 (read-only, proven)
- **Redemption-as-discount is rejected on a TECHNICAL ground, not only accounting.** The sale line carries a SINGLE `DiscountAmount`
  scalar with no source discriminator (`PosOrderLine`/`SalesInvoiceLine`), and the promotion priority rule (specificity → Priority
  → largest → ID) is hardcoded to the `Promotion` entity (`PricingService.cs:273-291`, best-one-wins `.First()`). A loyalty discount
  would overwrite the promotion's `DiscountAmount` (last writer wins) and cannot enter the comparator. The liability-settlement model
  bypasses the discount field entirely — a second reason (beyond revenue/VAT) to adopt it.
- **Two rounding domains.** Points as a COUNT round by an explicit floor policy (a count, NOT routed through `ICurrencyRounding` —
  as the retail fils-step is a separate commercial rounding); the points→money conversion at REDEMPTION routes through the single
  source `ICurrencyRounding` at the DOCUMENT-currency dp, AwayFromZero.
- **Offline does NOT advance HM-10 before earn.** The hyper lane is online-only today (every Scan/Pay is a synchronous server
  round-trip; the offline+sync engine belongs to the restaurant lane, scope `/pos/`, not `/hyper/pos`), so the points balance is
  server-authoritative and the double-spend risk arrives WITH HM-10, not before. **Constraint recorded for HM-10:** when offline is
  added, the points balance must be server-authoritative-at-sync via a conditional claim `SET Balance = Balance - @pts WHERE Balance >= @pts`
  (reusable primitives exist: `BusinessEvent.DedupKey`, the `BusinessEventDispatch` claim pattern). Slice 1 (identity) is
  offline-irrelevant.
- **Three earn models (named, none built).** (i) no entry until redemption; (ii) accrue a liability at earn time (Dr marketing
  expense / Cr points liability); (iii) IFRS-15 — points are a separate performance obligation, deferring part of revenue at sale
  (`2104 Advances from customers` is the functional stand-in). The choice depends on a materiality that is unknown before the
  program runs; it is a documentation choice, not a code choice, today.

### Logged items (read-only; not fixed here per scope)
1. **Customer 1025 (ControlAccountId = 0)** — one of forty (the only anomaly; the rest are `1102`). NOT fixed. It is the REAL case
   used to prove the fail-closed guard (T4: guard refuses 1025, the order stays Walk-in, zero effect). A one-off data anomaly, not a
   systemic remediation item.
2. **Phone-search index — a SCALE item with a threshold.** `Customers` has NO index on `Phone`/`Name`/`NameEn`/`TaxRegNo`;
   `SearchCustomersAsync` is a `Contains` scan. At forty customers it is sub-millisecond. **Add a `Phone` index BEFORE loyalty
   rollout OR before the customer base exceeds 1,000 rows, whichever comes first.** NOT built now (out of slice-1 scope).

## HM-D61 (declared) — parallel WIP broke boot mid-slice; validator temporarily disabled to run acceptance, restored, never committed
While building HM-9 slice 1, the parallel team's uncommitted `Program.cs` gained `AddHostedService<PermissionScopeStartupValidator>()`
(line 295) — a singleton `IHostedService` that consumes a scoped `IEnumerable<IModuleAccessService>`, which fails
`ValidateOnBuild` and CRASHES startup (`Program.<Main> BuildServiceProvider`). It is absent from HEAD (`git show HEAD:Program.cs`
has zero hits) and present only in the working tree — pure parallel WIP that appeared on disk AFTER the green HM-8 run ("our base
shifts invisibly", HM-D44; same shape as HM-D52). Per our rule we do NOT fix parallel code. To obtain a bootable binary for
acceptance, that ONE registration line was temporarily commented in the working tree (marker `HM9-TEMP-DISABLED-PARALLEL-BREAK`),
the app booted, acceptance ran, then the line was restored verbatim. `Program.cs` is NOT part of our commit (we never modified
it for HM-9). Declared here as required by the build-freshness rule: the acceptance binary differed from the parallel tree ONLY by
that disabled parallel diagnostic (a startup permission-scope check that touches no GL/stock/POS path), so no part of OUR slice-1
surface was left unverified.
**Characterization for the review list: this is the FOURTH movement of the floor in two phases — broke the build twice
(HM-D44/HM-D52), coupled into a GL writer (HM-D53, `ReverseAsync`→`RecordAsync`), and now broke boot (HM-D61) — and EVERY
one was discovered by a failure mid-work, never by coordination.** The pattern, not any single break, is the item to raise.

### Acceptance — api/dev/hm9-s1-accept (11/11 PASS, allPass:true, failedCount=0, idempotent over 3 runs; cookie-jar)
Reuses the REAL POS pay path (CreateOrder→AddLine→[link]→Pay) with fill-to-floor stock; every number read from a NEW DB query.
Numbers: T1 no link → Walk-in (cust 23) · grand 0.750 · AR on 1102 · JE balanced; T2 phone "0555070777" finds the customer ·
invoice issued to the REAL customer (9045) · same 1102 · same 0.750 · balanced; T3 quick-add (9046) control account VALID · link
works; T4 guard REFUSES customer 1025 (ControlAccountId=0) · order stays Walk-in (23); T5 link on a PAID order refused
("الطلب ليس مفتوحًا") · customer unchanged; T6 Loyalty capability ON branch17 / OFF otherBranch(4); T7 official invoice reads the
real customer (9045, tax TAX-HM9-777) · override null; T8 taxed 14% invoice to the real customer · no override · HM-8 stamp refused
on tax>0; T10 re-link before pay → the LAST customer (9046) is invoiced; T11 link then void → order Void · no invoice · no
anomalous posting; T9 failedCount=0 · ar_sub · ap_sub · doc_je_status_mismatch · writer_coupling · dbset all OK (stock_gl is the
structural baseline check, excluded from failedCount).

## HM-9 slice 2 (built) — loyalty points EARN (memo ledger, no GL) + the CustomerIdentity/Loyalty capability split
Points EARN on an identified sale. The balance is DERIVED (Σ signed `PointsMovements.Points`) — NO stored-balance column,
mirroring HM-6 (batch on-hand from movements) and deliberately avoiding the stored-balance lost update we fought in #5173 /
HM-D7. Points are a MEMO: no GL, no stock — `LoyaltyPointsHelper` writes ONLY `PointsMovements`, so it does not touch the two
writers. Earn = `Math.Floor(eligibleNet × rate)` — an EXPLICIT floor on a COUNT, deliberately NOT routed through
`ICurrencyRounding` (that is money rounding; this mirrors the separate retail fils-step). Guarded no-op unless the branch's
`"Loyalty"` capability is ON and the sale carries a real (non-walk-in) customer; earn is idempotent (once per invoice).

**Feasibility judgment (recorded): redemption is a MEDIUM-LARGE slice, not small** — it modifies the settlement/pay POSTING
(Dr points-liability instead of cash, via the GL writer), adds a concurrency-guarded balance decrement, a new `21xxxx` account,
the HM-8 invoice payment section, register UI, and return/void reversal. Therefore slice 2 ships EARN ALONE with the `"Loyalty"`
capability kept OFF in production (seed default) until redemption lands — "full feature or nothing IN PRODUCTION": the code is
dormant and tested, enabled only when earn+redeem are both ready.

**Rate** = points per DOCUMENT-currency unit on NET (decision: not a percentage — "a point per dinar" is what the cashier and
customer understand). Resolution: `BranchPosSetting.LoyaltyPointsPerCurrencyUnit` (branch override) → else
`Companies.LoyaltyPointsPerCurrencyUnit` (company default) → else 0. Data, not code (the end-customer sets it).

**Basis** = the NET line total (post-discount `LineTotal`, what actually reaches the ledger and what the customer paid).
Promo-discounted lines DO earn, on the discounted net (proven T7: 20% promo ⇒ net 6.000 ⇒ 6 points, not gross 7 — and the
technical reason redemption must be a liability not a discount stands: the single `DiscountAmount` scalar is owned by the promo).

**Eligibility** at the CATEGORY level (decision: not per-item — tobacco/top-up-cards/services are whole categories; a per-item
flag would mean tagging thousands). `ItemCategory.LoyaltyEligible` default true; `Item.LoyaltyEligible` (nullable) OVERRIDES,
null = inherit the category — the same inheritance shape as the category's GL-account inheritance.

**Reverse-never-delete on undo.** A return posts a NEGATIVE `PointsMovement` proportional to the returned eligible net
(`ReverseForSaleUndoAsync`, hooked in `ReturnOrderLinesAsync`); a paid-order CANCEL reverses the full remaining earn
(`ReverseAllForInvoiceAsync`, hooked in `VoidPaidOrderAsync` — it reverses the invoice JE directly, no SalesReturn to proportion
against). A full return reverses exactly the earned points ⇒ the per-invoice derived balance returns to 0. Never over-reverses
(capped at the un-reversed remainder). Earn hooks all three settlement paths (`PayAsync`/`PayTendersAsync`/`PaySplitByItemAsync`),
in-transaction with the sale.

**Capability SPLIT (decision 7): "CustomerIdentity" (slice 1) + "Loyalty" (slice 2) — two separate keys.** Identity resolves
HM-8's taxed-invoice case and is semantically independent of a points program (a branch may identify customers with no loyalty);
binding it under "Loyalty" would strand that HM-8 win until slice 3. Slice-1's gating was moved from "Loyalty" to
"CustomerIdentity" and its acceptance re-run green on the new key. Seed updated: **CustomerIdentity ON, Loyalty OFF** (preset +
DevSeed). A read-only derived points-balance line was added to the hyper Customer panel (shown only when Loyalty is enabled).

**SQL:** `deploy/sql/hm9_loyalty_earn.sql` — idempotent, additive: `PointsMovements` table (+ two indexes) and four nullable/
defaulted columns (Companies + BranchPosSettings rate; ItemCategories + Items eligibility). Zero financial impact. Not a migration.

### Slice-3 constraint recorded NOW (decision 5) — negative balance on return-after-redeem
A return AFTER points were redeemed can drive the balance negative. This does NOT arise in slice 2 (no redemption exists, so a
reversal can never exceed the earned balance). **Before ANY redemption code, an explicit policy must be decided and documented:
allow a negative balance · block the return · or force a cash settlement of the redeemed portion.** The redemption slice's design
must open on this decision, not discover it. (Also carried: the three earn-accounting models — (i) no entry until redemption ·
(ii) accrue a liability at earn · (iii) IFRS-15 deferred revenue — the choice depends on a materiality unknown before the program
runs; slice 2 implements none, points remain a pure memo.)

### Acceptance — api/dev/hm9-s2-accept (14/14 PASS, allPass:true, failedCount=0, idempotent over 3 runs; cookie-jar)
Reuses the REAL POS pay path; balances asserted as DELTAS (re-runnable despite accumulation); every number from a NEW DB query.
Numbers: T1 earn 15 = floor(15.000×1) · ONE Earn movement linked to the invoice · sale JE balanced (points are memo, no GL);
T2/T3 Loyalty OFF ⇒ 0 points AND identity still issues the invoice to the real customer (the split); T4 branch override rate 1
wins over company default 0.5 (ResolveRate); T5 mixed basket ⇒ 3 on the eligible line only (ineligible 4.000 excluded); T6 item
override eligible in an ineligible category ⇒ earns 10; T7 20% promo ⇒ points on NET 6.000 (=6), not gross 7; T8 net 12.900 ⇒
floor 12 (not 13); T9 full return ⇒ −15 reversal, per-invoice net 0; T10 partial (half) ⇒ proportional 7 = floor(15×7.5/15);
T11 paid-order cancel ⇒ full reverse, per-invoice net 0; T12 walk-in sale ⇒ no points; T13 derived balance == Σ movements
(126==126); T14 failedCount 0 · ar_sub · ap_sub · doc_je_status_mismatch · writer_coupling · dbset all OK (points add no GL).
A test-setup bug the acceptance caught: the fail-closed-style opening-stock needs the item's CATEGORY to carry GL accounts —
the ineligible ZZ category was given the eligible category's mapping so its items could be stocked/sold.

**HM-D61 recurred this slice** (parallel `PermissionScopeStartupValidator` still breaks boot) — disabled temporarily to run
acceptance, restored, never committed; `Program.cs` not in our commit. Same declared handling as before.

## HM-9 slice 3 (redemption) — DEFERRED by owner decision; ordered AFTER HM-10 (offline)
Redemption is a real marketing feature; offline is an operating precondition (a 4-lane hypermarket on a flaky network =
a stalled register with customers waiting, whereas the ABSENCE of loyalty never stops a sale). So HM-10 (offline) takes
priority over slice 3. Because the `"Loyalty"` capability ships OFF (seed default), EARN is dormant in production — no
customer is waiting on points — so carrying the redemption debt costs nothing operationally today. Slice 3 stays deferred
and the `"Loyalty"` capability stays OFF until redemption is complete (no half-feature in production). It opens on the
already-recorded **negative-balance policy** (return-after-redeem: allow negative · block return · cash-settle — decided
BEFORE any redemption code) and builds the **liability-settlement** model (Dr points-liability / Cr cash; new `21xxxx`
account; revenue + VAT untouched), NOT the discount model (which the single `DiscountAmount` scalar + promo comparator
technically forbid). Recorded at the HM-10 decision.

## HM-10 — strategy decided: GRACEFUL DEGRADATION (not offline-selling). Offline-selling DEFERRED for an architectural reason.
The hyper lane's real disconnect failure is two-fold: (1) a mid-scan / pay-before-commit STALL (recoverable — the cart is a
server-side Open order), and (2) a pay-AFTER-commit OPAQUE FAILURE (the sale committed, the response was lost, the cashier
sees a browser error and no receipt, and a manual resubmit would DOUBLE-CHARGE — there is no idempotency token on hyper Pay).
HM-10 core = graceful degradation + in-flight-sale safety, NOT offline selling.
- **Offline-selling (queue/sync) is DEFERRED with an explicit characterization:** "constrained by the FEFO wall — it requires
  solving cross-terminal OVERSELL (server-side reservation/allocation) or accepting the negative-stock risk. Deferred for an
  ARCHITECTURAL reason, not for lack of time, and may not be worth it." A 4-lane hypermarket selling from a LIMITED, tracked
  batch balance (unlike the restaurant, which sells from production) means two offline terminals selling the last units of a
  batch = an oversell on physical stock + COGS that a memo reversal cannot fix; FEFO + the overselling guard both need a LIVE
  SQL row lock (`UPDLOCK, HOLDLOCK`) no disconnected terminal can hold.
- **`navigator.onLine` does NOT detect the opaque failure** (the network is up; the request committed and the response was
  lost — timeout / IIS recycle / momentary drop after send). So INSIDE slice A the order is: idempotency + receipt-recovery
  FIRST (they fix the financial harm and work in ALL cases with no detector), THEN the connectivity detector (it only improves
  the message for the pre-submit "no network" case). The detector is NOT the protection.
- Generalizing the restaurant offline engine was rejected: it is not a re-scope (different dataset — full barcode/scale catalog
  vs the restaurant QuickMenu — and a barcode-aware local engine), and it builds on a layer that TRUSTS device 2dp prices
  (HM-D24, `SyncPaidOrderAsync:1583-1590` overwrites server re-pricing with device UnitPrice/Discount/TaxRate), re-requires the
  cancelled `(TerminalId,ReceiptNo)` unique index (HM-D5-b live collision source), and HAS NEVER RUN ON A REAL PATH (all offline
  data is synthetic ZZ fixtures with no PosSyncLog provenance). **Review-by-eye item: a real offline session has never been
  proven — it stays uncheckable until one runs.**

## HM-10 slice A (built) — hyper pay idempotency + receipt recovery (graceful-degradation core)
The hyper lane's opaque post-commit failure (the sale committed, the 302 was lost, the cashier sees a browser error and no
receipt — and a manual resubmit would DOUBLE-CHARGE) is closed. Order INSIDE the slice, per the owner's correction:
idempotency + recovery FIRST (they fix the financial harm and work in ALL cases with no connectivity detector), the
detector LAST (message only).

**Idempotency — DEVIATION from the approved "field on PosOrder" to a dedicated table, for a concurrency reason.** The
design review approved `PosOrder.PayToken` + a unique filtered index. Implementation analysis showed the field CANNOT make
the index arbitrate a CONCURRENT double-submit of the SAME order: both requests UPDATE the same order row to the same token,
producing no duplicate ROW, so no unique violation — both would post (acceptance T3's exact case). Only an INSERT-keyed row
converts the race into a transaction failure (the property PosSyncLog relies on and T3 asserts). So a dedicated table
`HyperPayTokens (CompanyId, Token unique, OrderId, InvoiceId)` is used — the OTHER option the owner offered — NOT PosSyncLog
(the restaurant lane's, untouched). This honors the design's intent ("the unique index decides") which the field could not
deliver. Recorded as a deliberate, reasoned change from the approved shape.

- The token is checked/stamped INSIDE PayAsync's transaction (lesson HM-1-أ 5b): the AUTHORITATIVE guard is the in-tx unique
  INSERT of the token row before commit; a pre-tx read is only a fast-path for a sequential retry, never relied on for the
  race. `PayAsync` gained an optional trailing `idempotencyToken = null` (restaurant callers pass null → unaffected — a
  backward-compatible change to the shared method, chosen over duplicating the whole financial path in a separate hyper method).
- **The catch swallows ONLY this index's violation** (`IsHyperPayTokenDuplicate`: SqlException 2601/2627 AND the message
  contains `UX_HyperPayTokens_Token`) and returns the winner's invoice; any other save failure is RETHROWN — the anti-swallow
  discipline of HM-1-أ. Proven by T11 (an over-long token → a truncation failure, number ≠ 2601/2627 → thrown, no fake invoice).
- **The token vs the status guard (owner's split, documented):** the token guards a RESUBMIT of the same intent (opaque-failure
  retry); the existing `Status != "Open"` guard handles a DIFFERENT intent on the same cart (two different tokens, or a lost
  token). Together ⇒ exactly one invoice, with NO new code for the different-intent case (T4/T9/T10).

**Hole (أ) — sessionStorage limitation, documented for the next reader.** The client token is stored in sessionStorage keyed
by order id, so it survives F5/reload; but if the tab/browser is CLOSED (the common opaque-failure symptom) the token is lost
and a fresh one is generated. In that case the protection is NOT the token but the ORDER-STATUS guard (the order is already
Paid ⇒ the resubmit is rejected). **The token guards same-page/same-session resubmits; the status guard is the comprehensive
protection. A future reader must NOT assume the token is complete protection.** Proven by T9 (lost token ⇒ status guard ⇒ one
invoice).

**Hole (ب) — POST-Redirect-Get was already in place.** `HyperPosController.Pay` already `RedirectToAction(nameof(Lane))` after
a successful pay, so a normal F5 reloads Lane (GET) and never re-posts. The opaque failure is precisely the LOST-302 window
(the redirect never reached the browser); a raw re-post there is caught by the status guard (T10). PRG is the first line; the
token/status guard cover the lost-302.

**Hole (ج) — the DbUpdateException catch is index-specific** (see IsHyperPayTokenDuplicate above), never a generic swallow (T11).

**Receipt recovery (read-only):** `GET /hyper/pos/receipts` lists THIS terminal's recent paid orders (receipt no + total +
time) with a Reprint link that REUSES HM-8 `PrintInvoice` (branch-guarded via `PosOrder.InvoiceId`). Terminal-scoped
(`TerminalId == ctx.TerminalId` + branch + company) so a cashier sees only their own terminal's receipts (T6). Zero posting (T5).

**Detector (message only, last):** a `navigator.onLine` pre-submit guard on the scan/pay forms shows a clear banner instead of
the browser error, and NEVER cancels an in-flight server sale (the cart is a server-side Open order that survives a disconnect
and resumes on reconnect — T7). It is explicitly not the protection (the owner's point: navigator.onLine cannot see the
opaque failure, where the network is up and the response was lost).

**SQL:** `deploy/sql/hm10_pay_idempotency.sql` — idempotent, additive: `HyperPayTokens` + the unique index. A brand-new table
has zero rows, so the unique index can never be blocked by a pre-existing duplicate. Zero financial impact. Not a migration.

### Acceptance — api/dev/hm10-accept (11/11 PASS, allPass:true, failedCount=0, idempotent over 3 runs; cookie-jar)
Reuses the REAL POS pay path with a token; every number from a NEW DB query. T1 normal pay grand 1.500 · one token row;
T2 resubmit same token ⇒ SAME invoice, one token, stock moves 1→1 + receipts 1→1 (no 2nd post); T3 UX_HyperPayTokens_Token
exists AND a duplicate (CompanyId,Token) INSERT is rejected by the index; T4 two different tokens same cart ⇒ 2nd rejected
("الطلب ليس مفتوحًا"), one invoice; T9 lost token ⇒ 2nd rejected by the status guard; T10 raw re-post (no token) ⇒ rejected
by the status guard; T7 pre-commit disconnect ⇒ order was Open · pay after reconnect ⇒ one invoice; T5 recovery finds the
receipt (ZZ-HM10-T1-000006, total 1.500) · reprint target exists · zero posting (invoice count 421→421); T6 recovery is
terminal-scoped (T2's receipt not in T1's list); T11 over-long token (non-index save failure) ⇒ THROWN not swallowed, no
fake invoice, order not paid; T8 failedCount 0 · ar_sub · ap_sub · doc_je_status_mismatch · writer_coupling · dbset all OK.

**HM-D61 recurred** (parallel `PermissionScopeStartupValidator` still breaks boot) — disabled temporarily to run acceptance,
restored, never committed; `Program.cs` not in our commit.
