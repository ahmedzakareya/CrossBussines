# 08 — Inventory UX

Inventory is a high-volume operational estate (78 views in `Views/Inventory`) with collection, transaction, lookup, stock, and reporting patterns. Its UX prioritizes correctness, scanning, units, location, and traceability.

## Contract

Collections show item identity, SKU/barcode, unit, location, available/on-hand distinctions, status, and only decision-relevant columns. Transactions show document context, source/destination, lines, totals, validation, posting state, and audit history. Lookup controls use Select2/Metronic patterns; tree structures use the existing jsTree integration where hierarchy is necessary.

Draft, validating, posted, partially fulfilled, reversed, locked, and failed are distinct domain states in addition to universal data states. Posting/destructive actions name the document and cannot depend on color. Barcode workflows preserve focus, announce accepted/rejected scans, and prevent duplicate submission.

Desktop tables may be compact; mobile uses prioritized cards or horizontal scroll only where column relationships must remain. Numeric codes/quantities remain readable in RTL. Touch targets remain 44px on field devices. Use server paging, debounced lookup, line virtualization where supported, and optimistic UI only for reversible non-financial changes. Future work should consolidate repeated form/table patterns without altering inventory rules.
