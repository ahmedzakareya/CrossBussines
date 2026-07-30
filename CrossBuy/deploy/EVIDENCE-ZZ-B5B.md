# EVIDENCE — StockBalance vs movement-ledger divergence on ZZ-B5B (HM-D5-أ ٥ب investigation)

**Frozen 2026-07-29. READ-ONLY snapshot — nothing corrected/reversed/reconciled. Do NOT run inv-reconcile or touch these rows until the root cause is accepted.**

## 0) The divergence
`inv-test-integrity → stock_gl` FAILED: total stock value 276105.35 vs inventory GL 276075.35 — **diff 30.00**.
30.00 = **1 unit × AvgCost 30.00** on item **ZZ-B5B-ITM (#5173)** in warehouse 1.

## 1) StockBalances row (item 5173)
```
ID=4183 CompanyID=1 ItemId=5173 WarehouseId=1 QtyOnHand=201.0000 TotalValue=6030.0000 AvgCost=30.0000 LastMovementAt=2026-07-29 00:00:00
```

## 2) StockMovements (item 5173, chronological) — net to 200, NOT 201
```
ID    CreatedAt                 Dir Qty  UnitCost TotalCost Source                 SourceId JE
7468  2026-07-29 07:28:59.437    +1 200  30       6000      Opening                  -      8084
7470  2026-07-29 07:29:01.083    -1   1  30         30      SalesInvoice           6189     8089
7471  2026-07-29 07:29:01.253    -1   1  30         30      SalesInvoice           6190     8092
7473  2026-07-29 07:29:01.530    -1   1  30         30      SalesInvoice           6192     8098
7474  2026-07-29 07:29:01.700    -1   1  30         30      SalesInvoice           6193     8101
7475  2026-07-29 07:29:01.813    -1   1  30         30      SalesInvoice           6194     8104
7476  2026-07-29 07:29:02.313    +1   1  30         30      SalesInvoiceReversal   6189     8106
7477  2026-07-29 07:29:02.457    +1   1  30         30      SalesInvoiceReversal   6190     8109
7478  2026-07-29 07:29:02.530    +1   1  30         30      SalesInvoiceReversal   6192     8112
7479  2026-07-29 07:29:02.583    +1   1  30         30      SalesInvoiceReversal   6193     8115
7480  2026-07-29 07:29:02.640    +1   1  30         30      SalesInvoiceReversal   6194     8118
```
- Net: SumIn 205 − SumOut 5 = **NetQty 200**, NetVal **6000**. Movement ledger is INTERNALLY CONSISTENT and equals 200/6000.
- Missing movement IDs **7469, 7472** = two IDENTITY values allocated then ROLLED BACK (the (a)-fault sale + the (b)-concurrent LOSER). Rollback at the movement level worked; the gap is expected.
- 5 orders → exactly 1 reversal movement each (7476–7480). Void path is clean.

## 3) StockCostLayers / StockBatch (item 5173)
EMPTY (LayerQtySum=0, LayerValSum=0). Expected: the item is Average-costing (layers only exist for FIFO). Not a factor.

## 4) The b5b orders — all Voided (reversed via services)
```
3356 Voided ZZB5-000001 inv6189 · 3357 Voided ZZB5-000002 inv6190 · 3359 Voided XYZ-999999 inv6192
3360 Voided ZZB5-000003 inv6193 · 3361 Voided ZZB5-000010 inv6194
```

## 5) All-items balance vs net-movements (production impact check)
```
ItemId ItemCode   Wh QtyOnHand mvQty  qtyDiff TotalValue    mvVal        valDiff
1      ITM-0001    1 187.0000  187.0000  0.0000 229161.0200  -67813.9300  296974.9500
67     MFGT-FIN    2 108.0000  108.0000  0.0000   4373.0500    4367.6000       5.4500
5173   ZZ-B5B-ITM  1 201.0000  200.0000  1.0000    6030.0000    6000.0000      30.0000
```
Disagreeing rows: **3 total, 2 non-ZZ**. BUT the two non-ZZ rows have **qtyDiff = 0** — only VALUE differs, from KNOWN non-concurrency causes:
- **ITM-0001**: value rebuilt by the documented `inv-reconcile` (physical×avg) + `seed-item-allmoves`; qty matches. Not the lost-update bug.
- **MFGT-FIN**: 5.45 value = manufacturing cost-rollup rounding; qty matches. Not the lost-update bug.
- **ZERO real items have a QUANTITY discrepancy.** The lost-update has NOT corrupted production quantities.

## 6) Root-cause verdict — hypothesis (1) lost update, via EF identity-map staleness (NOT our ب-3 tx work)
Balance read (`StockService.cs:379-381`):
```csharp
var bal = (await _context.StockBalances
    .FromSqlInterpolated($"SELECT * FROM StockBalances WITH (UPDLOCK, HOLDLOCK) WHERE ...")
    .AsTracking().ToListAsync()).FirstOrDefault();
bal.QtyOnHand = R4(bal.QtyOnHand - qtyBase);   // read-modify-write on a TRACKED entity
```
EF Core identity map: when this balance row is **already tracked in the context**, `AsTracking()` returns the **existing in-memory instance and does NOT overwrite its values with the freshly-read DB row**. So a context that read the balance in an earlier transaction, then had it modified by ANOTHER context, and re-reads it here, computes from the **stale in-memory value** — the `UPDLOCK` is acquired but its protection is nullified for the re-read.

Reconstruction (matches the +1 exactly):
1. (a)-retry (main ctx): DB bal 200→199; `bal` stays tracked=199 after commit.
2. (b) concurrent (SEPARATE ctx) winner: DB 199→198. Main ctx `bal` still tracked=199 (stale).
3. (c) main ctx: FromSql+AsTracking returns the tracked 199 (ignores DB 198) → 199−1=**198** written (should be 197). **+1 introduced.**
4. (d),(e1): 197,196. Teardown 5 voids: 196+5 = **201**.

- **This is the SAME family as the NextNumber / NextReceiptNo read-then-write bugs** (compute from a stale base). The user predicted it.
- **NOT caused by ب-3**: the balance-read mechanism predates it; our ambient-tx wrapping and `ChangeTracker.Clear()` are not the cause. (In fact `Clear()` on the (a)-fault rollback FORCED a fresh read there — it would PREVENT the staleness if applied after commit too, but commit intentionally does not clear.)

## 7) Production reachability
- The classic 4-lanes-sell-same-item scenario is SAFE: each lane is its own request/context, so its FIRST `FromSql` read is fresh from the DB under the `UPDLOCK`.
- The bug bites only when the SAME context does read-modify-write on a balance across SEPARATE transactions with an external concurrent write in between (what the test's shared main context created). Most current flows are single-transaction, so reachability is LOW but non-zero (future multi-tx same-item flows). It is a real latent fragility, not yet a production corruption.

## 8) Proposed fix (DESIGN ONLY — not implemented, awaiting approval)
Make the locked read return DB-fresh values regardless of the identity map. Options (smallest first):
- After the `FromSql` load, `await _context.Entry(bal).ReloadAsync();` when the entity was already tracked — forces the locked DB values.
- OR load the balance `.AsNoTracking()` under the lock, then attach/update explicitly.
- OR replace read-modify-write with an ATOMIC SQL `UPDATE StockBalances SET QtyOnHand = QtyOnHand ± @q, TotalValue = ... OUTPUT ...` (same remedy as the NextNumber fix) — most robust, no identity-map dependence.
Deferred backlog item: **HM-D6** — a permanent per-item check "StockBalance = net of its movements = Σ of its cost layers/batches", built AFTER the root-cause fix.
