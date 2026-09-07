# 08 — Tables

Tables are the default for comparable records, not a universal layout. Use lists/cards for narrative, touch-first, or highly variable content.

## Anatomy

Caption/accessible name, command and filter bar, header, body, optional totals/footer, pagination, result count, loading row, empty row, and error recovery. Every data table sits in `.table-responsive` and scrolls internally on narrow screens.

## Rules

- Text aligns to logical start; numbers and amounts to logical end; status centered only when scanability improves.
- Keep primary identity column visible; on mobile hide secondary columns or switch to an approved stacked-row pattern.
- Row actions live in the final logical column and use an accessible menu.
- Sorting exposes direction in text/ARIA; filtering remains visible and resettable.
- Selection uses checkboxes and a contextual bulk-action bar.
- Server pagination is default for unbounded datasets. Show range and total.
- Sticky headers are allowed only inside a bounded scroll region.
- Financial tables show currency/unit and use tabular numbers; totals have a clear top rule.
- Empty, loading, error, and permission-denied rows are distinct.
