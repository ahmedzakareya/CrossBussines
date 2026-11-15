# Hospital pharmacy programme (from H133)

Owner, 2026-11-15: the H132 stock screen is "very superficial". We are to study the leading systems and build on the
inventory foundation the platform already has.

## What the platform already gives us (reuse, never rebuild)

- **Stock writer and FEFO.** `StockService` is the only stock writer. It issues first-expiry-first-out, blocks expired
  batches, posts moving-average cost and COGS, transfers between stores, and handles counts (batch-aware), write-offs
  with reasons, goods receipts carrying batch/expiry/serial, purchase orders, GeneratePO from reorder, purchase returns,
  bins and valuation.
- **Approvals.** `InventoryApprovalService` covers PO, transfer, count and write-off. `WarehouseType.Quarantine` exists,
  but nothing uses it yet.
- **Hospital pieces already built:**
  - a dispense queue (verify → dispense → bill);
  - drug safety by free-text word;
  - a controlled-drug register for ward stores;
  - med routing with returns;
  - supply units with par levels and requisitions;
  - consignment implants, chemo item links, order sets and med reconciliation.

## Leading systems, condensed (Epic Willow, Oracle Health PharmNet, MEDITECH, TrakCare, Omnicell/Pyxis, ASHP/ISMP, SFDA DTTS, EDA EPTTS)

1. A layered drug master: generic → product, with ATC, form, route, strength and pack→dispense→dose conversions. It
   drives decision support, labels, picking and costing.
2. A pharmacist verification gate with priority (STAT / first dose / routine), timers and a clinical context panel.
3. A perpetual inventory across a location hierarchy (main → satellite → ward/cabinet → crash cart), with the lot on
   every movement.
4. Controlled-drug chain of custody: running balance, authenticated double signature, witnessed waste, blind counts,
   and discrepancies closed by end of shift.
5. GS1 DataMatrix capture — (01) GTIN, (17) expiry, (10) batch, (21) serial — for track-and-trace in Egypt and Saudi
   Arabia.
6. Enforced FEFO, plus a near-expiry workflow (move to a higher-use store, return to supplier, witnessed destruction
   with value).
7. Lot quarantine and recall, traced to the patients who received the lot.
8. Par levels per location, auto refill lists and data-driven par.
9. Returns with inspection and automatic patient credit; charge on dispense or on administration.
10. High-alert / LASA / Tall Man everywhere a name is shown.
11. Compounding records for IV, TPN and chemo.
12. Formulary governance: status, restricted use, non-formulary requests, P&T.
13. A clinical interventions log and surveillance worklists (renal dosing, therapeutic drug monitoring, stewardship).
14. A procurement loop with ABC/VEN-weighted safety stock.
15. KPIs: turnaround, inventory turns / days on hand, expiry loss, stock-outs, controlled-drug discrepancies,
    interventions.

## Units

| Unit | What | Builds on |
|---|---|---|
| H133 | **Drug master / formulary.** Generic (Ar/En), strength, form, route, ATC, therapeutic class, formulary status, high-alert, LASA + Tall Man name, controlled schedule, cold chain, hazardous, VEN. The dispense screen and drug safety read it. | Item, UoMConversion, HospitalControlledItems |
| H134 | **Lot quarantine and recall.** Move the lot to the quarantine store from settings; trace it to patients. | StockService.TransferAsync, WarehouseType.Quarantine, dispense/delivery batches |
| H135 | **Near-expiry workflow:** return to supplier / witnessed destruction with value. | StockWriteOff (Expired), PurchaseReturn |
| H136 | **Verification gate:** priority, timers, clinical panel, reject/modify with reason, interventions log. | HospitalDispense, renal dosing, drug safety |
| H137 | **Pharmacy controlled-drug register** in the main store: running balance, blind count, discrepancies. | HospitalControlledEntries |
| H138 | **Ward cabinet par + refill list + unit-dose cart fill.** | HospitalSupplyUnit/Par/Requisition |
| H139 | **Outpatient returns with credit**, partial dispense with an owed balance. | dispense lines, bill lines |
| H140 | **GS1 DataMatrix scan** at receipt and dispense. | ScanBarcode |
| H141 | **Pharmacy KPIs + ABC/VEN.** | StockMovements, dispenses |
| H142 | **Formulary governance:** non-formulary request, P&T decision, therapeutic interchange. | H133 |
| H143 | **Compounding:** IV / TPN / chemo. | chemo pharmacy |
| H144 | **Pharmacy labels** through Report Studio. | Report Studio datasets |
