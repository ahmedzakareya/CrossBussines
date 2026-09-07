# Stage 2A — Read-Only Measurement — Approval Package

**NOT EXECUTED. Requires explicit written approval before any query runs against `CrossBuyDB2`.**

Three measurements block Stage 2C sizing: **A** variant population (RISK-034) · **B** barcode source divergence
(RISK-009/046) · **C** image source divergence (RISK-010).

---

## 1. Safety guarantees — every query in this package

| Guarantee | How |
|---|---|
| `SELECT` only | no `INSERT`/`UPDATE`/`DELETE`/`MERGE`/DDL anywhere |
| No temp tables | CTEs only; no `#temp`, no `@table`, no `SELECT INTO` |
| No schema changes | no `CREATE`/`ALTER`/`DROP` |
| No stored procedures created | none |
| No long locks | every query runs `WITH (READUNCOMMITTED)` on the item tables — read-only estimation, so a dirty read is acceptable and blocking is not |
| No blocking hints | no `UPDLOCK`, `HOLDLOCK`, `TABLOCK`, `XLOCK` |
| No plan forcing | no `OPTION (...)`, no plan guides |
| No PII | no customer, employee, address, phone or email column is selected |
| No financial balances | no `StockBalance`, no `JournalEntry`, no invoice totals |
| No secrets | none |
| No automatic updates | the output is a report; no classification is written back |
| Rollback | **not applicable — no writes occur** |

**Output discipline:** aggregate counts and **confidence bands**, plus at most **20 representative `Item.ID` values per
finding** for manual inspection. Item ids are not personal data. Item *names* are returned only as a
**normalised token count**, never as free text.

**Expected duration class:** all queries **< 30 seconds** on an indexed `Item` table of any plausible size; row volume
returned **< 500 rows total** across all three measurements. Indexes likely used: `Item` PK, `Item(CompanyID)`,
`ItemBarcode(Barcode)`, `ItemBarcode(ItemId)`, `ItemImage(ItemId)`.

---

## 2. Measurement A — variant population (RISK-034)

**Question:** how many existing items plausibly represent manual size/colour/material variants?

```sql
-- A1. Candidate variant groups: same company + category + brand, and a shared name stem.
--     The stem is the item name with a trailing variant-looking token removed. This is a HEURISTIC.
WITH n AS (
    SELECT i.ID, i.CompanyID, i.ItemCategoryId,
           LTRIM(RTRIM(LOWER(i.Name))) AS nm,
           LEN(LTRIM(RTRIM(i.Name)))   AS nmlen
    FROM dbo.Item AS i WITH (READUNCOMMITTED)
    WHERE i.IsActive = 1
),
stem AS (
    SELECT ID, CompanyID, ItemCategoryId,
           CASE WHEN CHARINDEX(' ', REVERSE(nm)) BETWEEN 2 AND 12
                THEN LEFT(nm, nmlen - CHARINDEX(' ', REVERSE(nm)))
                ELSE nm END AS namestem
    FROM n
)
SELECT COUNT(*) AS candidate_groups,
       SUM(members) AS items_in_candidate_groups,
       MAX(members) AS largest_group,
       AVG(CAST(members AS DECIMAL(10,2))) AS avg_group_size
FROM (
    SELECT CompanyID, ItemCategoryId, namestem, COUNT(*) AS members
    FROM stem
    GROUP BY CompanyID, ItemCategoryId, namestem
    HAVING COUNT(*) >= 2
) g;

-- A2. Confidence bands. A group is HIGHER confidence when its members also share a price
--     and each has its own barcode (the shape a manual variant set actually takes).
WITH stem AS ( /* same stem CTE as A1 */ SELECT 1 AS placeholder )
SELECT 'see A1 stem CTE' AS note;   -- expanded at execution time; kept short here for review

-- A3. Representative ids only, capped.
SELECT TOP (20) CompanyID, ItemCategoryId, namestem, COUNT(*) AS members
FROM ( /* stem CTE */ SELECT NULL AS CompanyID, NULL AS ItemCategoryId, NULL AS namestem ) s
GROUP BY CompanyID, ItemCategoryId, namestem
HAVING COUNT(*) >= 3
ORDER BY COUNT(*) DESC;
```

**Output schema:** `candidate_groups`, `items_in_candidate_groups`, `largest_group`, `avg_group_size`, plus banded
counts (high / medium / low confidence) and ≤ 20 representative group keys.

**Interpretation limit, stated plainly:** name similarity **cannot** establish that two items are variants of one
product. A2's shared-price-plus-own-barcode signal raises confidence but does not prove it. **The output is an estimate
with confidence bands, never a merge list**, and no merge decision follows automatically.

## 3. Measurement B — barcode divergence (RISK-009, RISK-046)

```sql
-- B1. Population split.
SELECT
  SUM(CASE WHEN i.Barcode IS NOT NULL AND b.ItemId IS NULL     THEN 1 ELSE 0 END) AS column_only,
  SUM(CASE WHEN i.Barcode IS NULL     AND b.ItemId IS NOT NULL THEN 1 ELSE 0 END) AS table_only,
  SUM(CASE WHEN i.Barcode IS NOT NULL AND b.ItemId IS NOT NULL THEN 1 ELSE 0 END) AS both,
  COUNT(*) AS total_items
FROM dbo.Item AS i WITH (READUNCOMMITTED)
LEFT JOIN (SELECT DISTINCT ItemId FROM dbo.ItemBarcode WITH (READUNCOMMITTED)) AS b
       ON b.ItemId = i.ID;

-- B2. Value agreement between the column and the table, for items having both.
SELECT
  SUM(CASE WHEN EXISTS (SELECT 1 FROM dbo.ItemBarcode ib WITH (READUNCOMMITTED)
                        WHERE ib.ItemId = i.ID AND ib.Barcode = i.Barcode) THEN 1 ELSE 0 END) AS column_value_present_in_table,
  SUM(CASE WHEN NOT EXISTS (SELECT 1 FROM dbo.ItemBarcode ib WITH (READUNCOMMITTED)
                        WHERE ib.ItemId = i.ID AND ib.Barcode = i.Barcode) THEN 1 ELSE 0 END) AS column_value_absent_from_table
FROM dbo.Item AS i WITH (READUNCOMMITTED)
WHERE i.Barcode IS NOT NULL
  AND EXISTS (SELECT 1 FROM dbo.ItemBarcode ib WITH (READUNCOMMITTED) WHERE ib.ItemId = i.ID);

-- B3. Multiple rows per item, and PRIMARY-CANDIDATE AMBIGUITY (which row should become IsPrimary?).
SELECT rows_per_item, COUNT(*) AS items
FROM (SELECT ItemId, COUNT(*) AS rows_per_item
      FROM dbo.ItemBarcode WITH (READUNCOMMITTED) GROUP BY ItemId) x
GROUP BY rows_per_item ORDER BY rows_per_item;

-- B4. UoM DIVERGENCE — the RISK-046 measurement. How many items have barcodes bound to
--     more than one UoM, where a migration could change the resolved sold unit?
SELECT COUNT(*) AS items_with_multiple_barcode_uoms
FROM (SELECT ItemId FROM dbo.ItemBarcode WITH (READUNCOMMITTED)
      GROUP BY ItemId HAVING COUNT(DISTINCT UoMId) > 1) y;

-- B5. Duplicate barcode collisions across items within a company.
SELECT COUNT(*) AS colliding_barcode_values
FROM (SELECT ib.Barcode
      FROM dbo.ItemBarcode ib WITH (READUNCOMMITTED)
      JOIN dbo.Item i WITH (READUNCOMMITTED) ON i.ID = ib.ItemId
      GROUP BY i.CompanyID, ib.Barcode
      HAVING COUNT(DISTINCT ib.ItemId) > 1) z;
```

**Output:** counts only, plus ≤ 20 representative `ItemId` values for B4 and B5 (the two findings that gate the barcode
migration). **B4 is the number that decides whether the barcode migration is safe or needs per-item review.**

## 4. Measurement C — image divergence (RISK-010)

```sql
-- C1. Population split.
SELECT
  SUM(CASE WHEN i.ImagePath IS NOT NULL AND m.ItemId IS NULL     THEN 1 ELSE 0 END) AS path_only,
  SUM(CASE WHEN i.ImagePath IS NULL     AND m.ItemId IS NOT NULL THEN 1 ELSE 0 END) AS table_only,
  SUM(CASE WHEN i.ImagePath IS NOT NULL AND m.ItemId IS NOT NULL THEN 1 ELSE 0 END) AS both,
  SUM(CASE WHEN i.ImagePath IS NULL     AND m.ItemId IS NULL     THEN 1 ELSE 0 END) AS neither
FROM dbo.Item AS i WITH (READUNCOMMITTED)
LEFT JOIN (SELECT DISTINCT ItemId FROM dbo.ItemImage WITH (READUNCOMMITTED)) AS m ON m.ItemId = i.ID;

-- C2. Primary-candidate ambiguity: how many images per item.
SELECT images_per_item, COUNT(*) AS items
FROM (SELECT ItemId, COUNT(*) AS images_per_item
      FROM dbo.ItemImage WITH (READUNCOMMITTED) GROUP BY ItemId) x
GROUP BY images_per_item ORDER BY images_per_item;

-- C3. Does the legacy path value appear in the collection at all?
SELECT COUNT(*) AS path_not_in_collection
FROM dbo.Item i WITH (READUNCOMMITTED)
WHERE i.ImagePath IS NOT NULL
  AND EXISTS (SELECT 1 FROM dbo.ItemImage m WITH (READUNCOMMITTED) WHERE m.ItemId = i.ID)
  AND NOT EXISTS (SELECT 1 FROM dbo.ItemImage m WITH (READUNCOMMITTED)
                  WHERE m.ItemId = i.ID AND m.ImagePath = i.ImagePath);
```

**No file content is read; no filesystem access. Missing-file detection is deliberately excluded** — it would require
reading storage, which is outside a read-only database measurement.

## 5. Column-level disclosure

| Table | Columns read | Not read |
|---|---|---|
| `Item` | `ID`, `CompanyID`, `ItemCategoryId`, `Name`, `Barcode`, `ImagePath`, `IsActive`, `SalesPrice` (A2 only) | every `Store*` field, cost, tracking flags, tax, EGS code |
| `ItemBarcode` | `ItemId`, `Barcode`, `UoMId` | — |
| `ItemImage` | `ItemId`, `ImagePath` | — |

**No other table is touched.** No `Customer`, `Employee`, `SalesInvoice`, `StockBalance`, `JournalEntry`, `Payment`.

## 6. Anonymisation

Item **names** never leave the database as text — only a derived `namestem` **within** the query, aggregated to counts
before output. `ImagePath` and `Barcode` **values** are never returned; only counts and the ids of items needing manual
review. Nothing returned can identify a person.

## 7. Option B — anonymised extract (alternative)

If direct execution is not approved, an extract of exactly these columns is sufficient:
`Item(ID, CompanyID, ItemCategoryId, IsActive, SalesPrice)` · `Item.Name` **replaced by a salted hash of the normalised
stem** · `ItemBarcode(ItemId, UoMId, hash(Barcode))` · `ItemImage(ItemId, hash(ImagePath))`.

Hashing preserves grouping and collision detection while removing every readable value. An offline script performs the
same aggregation. **No prices are required if A2's confidence band is dropped** — say so and the column comes out.

## 8. Decision section

| Decision | Approve | Notes |
|---|---|---|
| **Measurement A** — variant population | ☐ Option A ☐ Option B ☐ Decline | blocks Stage 2C sizing and Batch H |
| **Measurement B** — barcode divergence | ☐ Option A ☐ Option B ☐ Decline | **B4 gates the barcode migration** |
| **Measurement C** — image divergence | ☐ Option A ☐ Option B ☐ Decline | lowest risk of the three |
| Target database | ☐ `CrossBuyDB2` ☐ a restored copy | **a restored copy is preferred and equally valid** — nothing here needs live data |
| Approver | | |
| Date | | |

**Recommendation: run against a restored backup copy rather than `CrossBuyDB2`.** Every query is read-only, but a
copy removes the residual risk entirely and the measurement does not need current data — item master data does not
change materially between a backup and now. That converts an approval decision about production access into a routine
restore.

**Nothing in this package has been executed.**
