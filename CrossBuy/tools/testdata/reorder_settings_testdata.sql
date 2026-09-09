-- =================================================================================================
-- Reorder policy for the Reorder settings screen (Inventory/ReorderSettings).
--
-- WHAT THE SCREEN SHOWS, read from the action rather than guessed:
--
--   InventoryController.ReorderSettings(int? warehouseId)
--       if (warehouseId == null) -> no rows at all ("Select a warehouse to list items.")
--       else  items.Where(i => i.ItemType == "Stockable")
--             left-joined onto ItemWarehouseSettings for THAT warehouse
--             ReorderPoint = s?.ReorderPoint ?? 0, MinQty = ..., MaxQty = ...
--
-- So the grid lists every stockable item whether it has a policy or not, and a missing policy is
-- drawn as three zeros. Measured before writing anything: 202 stockable items, 3 warehouses, 61
-- ItemWarehouseSettings rows in total and only 12 of them carrying a reorder point - which is why
-- the screen reads as a wall of zeros.
--
-- ------------------------------------------------------------------------------------------------
-- THE POLICY IS DERIVED, NOT INVENTED. Three tiers, and the tier decides the shape of the numbers:
--
--   TIER 1  an item the warehouse HOLDS (a StockBalances row with quantity on hand)
--           The point sits BELOW the quantity on hand, so the item reads as healthy. This is
--           deliberate: setting a point above on-hand for all 62 stocked rows would put every one
--           of them on the Planning screen as a shortage, and a warehouse where nothing is in
--           stock is not a state worth looking at. Planning keeps the shortages it already had.
--
--   TIER 1b an item the warehouse holds at ZERO OR NEGATIVE quantity (17 such rows)
--           These get a normal small policy, and they WILL appear on Planning - correctly, because
--           an item with a reorder policy and no stock is a real stockout. That is the one case
--           where this script grows the Planning list, and the summary reports the new count.
--
--   TIER 2  a stockable item with NO balance row at the busiest warehouse (154 items)
--           A main warehouse plans its whole catalogue, so these get a modest catalogue policy.
--           They CANNOT reach the Planning screen: that action iterates StockService
--           .GetBalancesAsync, which reads StockBalances only, so an item with no balance row is
--           never considered. Branch warehouses get policy only for what they actually carry - a
--           branch plans its own shelves, not the catalogue.
--
-- Min <= ReorderPoint <= Max holds on every row written, because the screen's own arithmetic is
-- "suggested = max - on hand" and a max under the point produces a suggestion of zero, i.e. a row
-- that is below its reorder point and still proposes ordering nothing.
--
-- The ratios vary by row number rather than being one constant, so the grid reads as a set of
-- decisions instead of a fill-down.
--
-- ------------------------------------------------------------------------------------------------
-- ADDITIVE. It writes a policy only where there is none: every UPDATE is guarded on the column
-- being NULL or zero, so a point somebody actually set is left exactly as it is - including the 12
-- rows that currently drive the Planning screen. Re-running it changes nothing further.
--
-- SafetyStock and LeadTimeDays are filled too. Nothing in the application reads either column
-- today (they exist only on the entity, Models/Context/Inventory/Inventory.cs:182-183), so this
-- changes no screen - but a reorder policy without a lead time is not a policy, and the columns
-- are there to hold one.
--
--   sqlcmd -S localhost -d CrossBuyDev -E -C -f 65001 -i tools/testdata/reorder_settings_testdata.sql
-- =================================================================================================
-- sqlcmd -i runs with QUOTED_IDENTIFIER OFF and these tables carry filtered indexes, so without
-- this line every INSERT is rejected. It must come first in the batch.
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @co int = 1;

-- The warehouse that plans the catalogue is the one holding the most stock - derived from the data,
-- not from an ID or from the word "MAIN" in a code, either of which is a different machine's truth.
DECLARE @mainWh int;
SELECT TOP 1 @mainWh = b.WarehouseId
FROM StockBalances b
JOIN Warehouses w ON w.ID = b.WarehouseId AND w.CompanyID = @co
WHERE b.CompanyID = @co AND b.QtyOnHand > 0
GROUP BY b.WarehouseId
ORDER BY COUNT(*) DESC, SUM(b.QtyOnHand) DESC;

IF @mainWh IS NULL
BEGIN
    RAISERROR('No warehouse holds any stock; there is nothing to derive a policy from.', 16, 1);
    RETURN;
END

-- Rows created by this run are identified by ID, captured before the first insert, because this
-- table has no column a marker could live in. The teardown at the bottom uses it.
DECLARE @idBefore int = ISNULL((SELECT MAX(ID) FROM ItemWarehouseSettings), 0);

-- The whole policy is computed once, into one table, so the INSERT and the UPDATE below cannot
-- disagree about what a row's numbers should be.
DECLARE @policy TABLE (
    ItemId int, WarehouseId int, Tier varchar(8),
    ReorderPoint decimal(18,4), MinQty decimal(18,4), MaxQty decimal(18,4),
    SafetyStock decimal(18,4), LeadTimeDays int,
    PRIMARY KEY (ItemId, WarehouseId));

-- ---- tiers 1 and 1b: what each warehouse actually holds -----------------------------------------
INSERT INTO @policy (ItemId, WarehouseId, Tier, ReorderPoint, MinQty, MaxQty, SafetyStock, LeadTimeDays)
SELECT ItemId, WarehouseId, Tier, rp,
       -- the floor of the ladder: half the point, and never above it
       CASE WHEN FLOOR(rp * 0.5) < 1 THEN 1 ELSE FLOOR(rp * 0.5) END,
       mx, CASE WHEN FLOOR(rp * 0.4) < 1 THEN 1 ELSE FLOOR(rp * 0.4) END, leadDays
FROM (
    SELECT s.ItemId, s.WarehouseId, s.Tier, s.leadDays,
           s.rp,
           -- the ceiling must clear the point, or "suggested = max - on hand" yields nothing to order
           CASE WHEN s.mx <= s.rp THEN CEILING(s.rp * 1.5) ELSE s.mx END AS mx
    FROM (
        SELECT b.ItemId, b.WarehouseId,
               CASE WHEN b.QtyOnHand > 0 THEN 'T1' ELSE 'T1b' END AS Tier,
               CASE WHEN b.QtyOnHand > 0
                    -- BELOW on hand: 25% to 40% of it, so the item reads as healthy
                    THEN FLOOR(b.QtyOnHand * (0.25 + (rn % 4) * 0.05))
                    -- out of stock: an ordinary small point, which Planning will show as a stockout
                    ELSE 10 + (rn % 5) * 5 END AS rp,
               CASE WHEN b.QtyOnHand > 0
                    THEN CEILING(b.QtyOnHand * (1.35 + (rn % 3) * 0.15))
                    ELSE (10 + (rn % 5) * 5) * 3 END AS mx,
               3 + (rn % 7) * 3 AS leadDays
        FROM (SELECT b.*, ROW_NUMBER() OVER (PARTITION BY b.WarehouseId ORDER BY b.QtyOnHand DESC, b.ItemId) AS rn
              FROM StockBalances b WHERE b.CompanyID = @co) b
        JOIN Items i      ON i.ID = b.ItemId      AND i.CompanyID = @co AND i.ItemType = 'Stockable'
        JOIN Warehouses w ON w.ID = b.WarehouseId AND w.CompanyID = @co
    ) s
    -- A holding of one or two units does not need a planning ladder. Rounding one up to a point of
    -- 1 would put the item on the shortage list purely as an artefact of the arithmetic.
    WHERE s.rp >= 1
) x;

-- ---- tier 2: the rest of the catalogue, at the busiest warehouse only ----------------------------
INSERT INTO @policy (ItemId, WarehouseId, Tier, ReorderPoint, MinQty, MaxQty, SafetyStock, LeadTimeDays)
SELECT ItemId, @mainWh, 'T2', rp,
       CASE WHEN FLOOR(rp * 0.5) < 1 THEN 1 ELSE FLOOR(rp * 0.5) END,
       rp * 3,
       CASE WHEN FLOOR(rp * 0.4) < 1 THEN 1 ELSE FLOOR(rp * 0.4) END,
       3 + (rn % 7) * 3
FROM (
    SELECT i.ID AS ItemId,
           ROW_NUMBER() OVER (ORDER BY i.ItemCode) AS rn,
           8 + (ROW_NUMBER() OVER (ORDER BY i.ItemCode) % 6) * 4 AS rp
    FROM Items i
    WHERE i.CompanyID = @co AND i.ItemType = 'Stockable' AND i.IsActive = 1
      AND NOT EXISTS (SELECT 1 FROM StockBalances b
                      WHERE b.CompanyID = @co AND b.WarehouseId = @mainWh AND b.ItemId = i.ID)
) y
WHERE NOT EXISTS (SELECT 1 FROM @policy p WHERE p.ItemId = y.ItemId AND p.WarehouseId = @mainWh);

BEGIN TRAN;

-- FILL ONLY WHAT IS EMPTY. A point somebody already set is a decision, not a gap - so each column
-- is guarded on its own, and the 12 rows currently driving the Planning screen survive untouched.
UPDATE s
SET ReorderPoint = CASE WHEN ISNULL(s.ReorderPoint, 0) <= 0 THEN p.ReorderPoint ELSE s.ReorderPoint END,
    MinQty       = CASE WHEN ISNULL(s.MinQty, 0)       <= 0 THEN p.MinQty       ELSE s.MinQty       END,
    MaxQty       = CASE WHEN ISNULL(s.MaxQty, 0)       <= 0 THEN p.MaxQty       ELSE s.MaxQty       END,
    SafetyStock  = CASE WHEN ISNULL(s.SafetyStock, 0)  <= 0 THEN p.SafetyStock  ELSE s.SafetyStock  END,
    LeadTimeDays = CASE WHEN ISNULL(s.LeadTimeDays, 0) <= 0 THEN p.LeadTimeDays ELSE s.LeadTimeDays END
FROM ItemWarehouseSettings s
JOIN @policy p ON p.ItemId = s.ItemId AND p.WarehouseId = s.WarehouseId
WHERE ISNULL(s.ReorderPoint, 0) <= 0 OR ISNULL(s.MinQty, 0) <= 0 OR ISNULL(s.MaxQty, 0) <= 0
   OR ISNULL(s.SafetyStock, 0) <= 0 OR ISNULL(s.LeadTimeDays, 0) <= 0;
DECLARE @filled int = @@ROWCOUNT;

INSERT INTO ItemWarehouseSettings (ItemId, WarehouseId, ReorderPoint, MinQty, MaxQty, SafetyStock, LeadTimeDays)
SELECT p.ItemId, p.WarehouseId, p.ReorderPoint, p.MinQty, p.MaxQty, p.SafetyStock, p.LeadTimeDays
FROM @policy p
WHERE NOT EXISTS (SELECT 1 FROM ItemWarehouseSettings s
                  WHERE s.ItemId = p.ItemId AND s.WarehouseId = p.WarehouseId);
DECLARE @created int = @@ROWCOUNT;

COMMIT;

-- ------------------------------------------------------------------------------------------------
-- The summary answers the two questions this script can be wrong about: how many rows the settings
-- grid now shows filled, and whether it flooded the Planning screen. The Planning count runs that
-- action's OWN three conditions, so it is the number of rows the page will draw.
-- ------------------------------------------------------------------------------------------------
DECLARE @planning int;
SELECT @planning = COUNT(*)
FROM StockBalances b
JOIN ItemWarehouseSettings s ON s.ItemId = b.ItemId AND s.WarehouseId = b.WarehouseId
WHERE b.CompanyID = @co
  AND ISNULL(s.ReorderPoint, 0) > 0
  AND b.QtyOnHand <= s.ReorderPoint
  AND (CASE WHEN ISNULL(s.MaxQty, 0) > s.ReorderPoint THEN s.MaxQty ELSE s.ReorderPoint END) - b.QtyOnHand > 0;

SELECT N'main warehouse: ' + (SELECT Code FROM Warehouses WHERE ID = @mainWh)
     + N'  |  settings created: ' + CAST(@created AS varchar)
     + N', filled: ' + CAST(@filled AS varchar)
     + N'  |  rows Planning will show: ' + CAST(@planning AS varchar)
     + N'  |  IDs created above: ' + CAST(@idBefore AS varchar) AS result;

SELECT Tier, COUNT(*) AS rows_planned, MIN(ReorderPoint) AS rp_min, MAX(ReorderPoint) AS rp_max
FROM @policy GROUP BY Tier ORDER BY Tier;

SELECT w.Code AS warehouse,
       (SELECT COUNT(*) FROM Items i WHERE i.CompanyID = @co AND i.ItemType = 'Stockable') AS grid_rows,
       (SELECT COUNT(*) FROM ItemWarehouseSettings s
        JOIN Items i ON i.ID = s.ItemId AND i.CompanyID = @co AND i.ItemType = 'Stockable'
        WHERE s.WarehouseId = w.ID AND ISNULL(s.ReorderPoint, 0) > 0) AS with_policy
FROM Warehouses w WHERE w.CompanyID = @co ORDER BY w.ID;

-- =================================================================================================
-- TEARDOWN
--
-- Rows this run CREATED - exact, because the ID watermark above is printed by the summary:
--
--   DELETE FROM ItemWarehouseSettings WHERE ID > <IDs created above>;
--
-- Rows it FILLED cannot be told apart from a policy entered by a person afterwards, since the table
-- has no column to mark. Clear them only if nothing real has been entered since:
--
--   UPDATE ItemWarehouseSettings SET ReorderPoint=NULL, MinQty=NULL, MaxQty=NULL,
--                                    SafetyStock=NULL, LeadTimeDays=NULL
--   WHERE ID <= <IDs created above>;
-- =================================================================================================
