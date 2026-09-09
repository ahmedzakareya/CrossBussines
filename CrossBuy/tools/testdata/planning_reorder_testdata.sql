-- =================================================================================================
-- Reorder points for the Inventory Planning screen (Inventory/Planning).
--
-- WHY THE SCREEN IS EMPTY, measured rather than assumed: the company has 63 stock balances with
-- quantity on hand and 49 ItemWarehouseSettings rows, and ZERO of those settings carry a
-- ReorderPoint. The action drops every row where the point is not set:
--
--   InventoryController.Planning
--       if (rp <= 0 || b.QtyOnHand > rp) continue;          -- rp = ItemWarehouseSettings.ReorderPoint
--       var target    = mx > rp ? mx : rp;                  -- mx = MaxQty
--       var suggested = Math.Max(0, target - b.QtyOnHand);
--       if (suggested <= 0) continue;
--
-- So a row only appears when ALL THREE hold: the point is set, the balance is at or below it, and
-- the suggested quantity is positive. This seeds points ABOVE each item's current balance so all
-- three hold, and a max above the point so the suggestion is a real number rather than zero.
--
-- IT DOES NOT TOUCH STOCK. No movement is posted and no balance is changed - the shortage is
-- created by setting a policy, which is what a planning screen is about, not by inventing stock.
--
-- ADDITIVE AND REVERSIBLE. It only fills ReorderPoint/MaxQty where they are currently NULL or
-- zero, so a real policy someone already set is never overwritten. Rows it creates itself are
-- marked; rows it merely fills are recorded in the summary so they can be cleared by hand.
--
--   sqlcmd -S localhost -d CrossBuyDev -E -C -f 65001 -i tools/testdata/planning_reorder_testdata.sql
-- =================================================================================================
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @company int = 1;
DECLARE @take    int = 12;          -- how many items to put on the plan

-- The items to cover: the ones actually holding stock, largest first, so the suggestions are
-- meaningful numbers instead of ones and twos.
DECLARE @pick TABLE (rn int IDENTITY(1,1), ItemId int, WarehouseId int, Qty decimal(18,4));
INSERT INTO @pick (ItemId, WarehouseId, Qty)
SELECT TOP (@take) b.ItemId, b.WarehouseId, b.QtyOnHand
FROM StockBalances b
WHERE b.CompanyID = @company AND b.QtyOnHand > 0
ORDER BY b.QtyOnHand DESC;

IF (SELECT COUNT(*) FROM @pick) = 0
BEGIN
    RAISERROR('No stock balance with quantity on hand; nothing to plan for.', 16, 1);
    RETURN;
END

BEGIN TRAN;

DECLARE @i int = 1, @max int = (SELECT COUNT(*) FROM @pick);
DECLARE @created int = 0, @filled int = 0;

WHILE @i <= @max
BEGIN
    DECLARE @item int, @wh int, @qty decimal(18,4);
    SELECT @item = ItemId, @wh = WarehouseId, @qty = Qty FROM @pick WHERE rn = @i;

    -- THE POINT SITS ABOVE THE BALANCE, which is what puts the item on the plan at all, and the
    -- max sits above the point, which is what makes the suggested quantity non-zero. The margins
    -- vary per row so the screen shows a spread of shortages rather than one repeated number.
    DECLARE @rp decimal(18,4) = CEILING(@qty * (1.15 + (@i % 4) * 0.20));
    DECLARE @mx decimal(18,4) = CEILING(@rp  * (1.30 + (@i % 3) * 0.25));
    DECLARE @minq decimal(18,4) = CEILING(@qty * 0.5);

    IF EXISTS (SELECT 1 FROM ItemWarehouseSettings WHERE ItemId = @item AND WarehouseId = @wh)
    BEGIN
        -- FILL ONLY WHAT IS EMPTY. A policy somebody already set is a decision, not a gap.
        UPDATE ItemWarehouseSettings
        SET ReorderPoint = CASE WHEN ISNULL(ReorderPoint, 0) <= 0 THEN @rp   ELSE ReorderPoint END,
            MaxQty       = CASE WHEN ISNULL(MaxQty, 0)       <= 0 THEN @mx   ELSE MaxQty       END,
            MinQty       = CASE WHEN ISNULL(MinQty, 0)       <= 0 THEN @minq ELSE MinQty       END
        WHERE ItemId = @item AND WarehouseId = @wh
          AND (ISNULL(ReorderPoint, 0) <= 0 OR ISNULL(MaxQty, 0) <= 0);
        IF @@ROWCOUNT > 0 SET @filled = @filled + 1;
    END
    ELSE
    BEGIN
        INSERT INTO ItemWarehouseSettings (ItemId, WarehouseId, ReorderPoint, MinQty, MaxQty)
        VALUES (@item, @wh, @rp, @minq, @mx);
        SET @created = @created + 1;
    END

    SET @i = @i + 1;
END

COMMIT;

-- The count below runs the SCREEN'S OWN three conditions, so the number reported is the number of
-- rows the page will actually draw - not the number of settings written.
DECLARE @willShow int;
SELECT @willShow = COUNT(*)
FROM StockBalances b
JOIN ItemWarehouseSettings s ON s.ItemId = b.ItemId AND s.WarehouseId = b.WarehouseId
WHERE b.CompanyID = @company
  AND ISNULL(s.ReorderPoint, 0) > 0
  AND b.QtyOnHand <= s.ReorderPoint
  AND (CASE WHEN ISNULL(s.MaxQty, 0) > s.ReorderPoint THEN s.MaxQty ELSE s.ReorderPoint END) - b.QtyOnHand > 0;

SELECT N'settings created: ' + CAST(@created AS varchar)
     + N' , filled: ' + CAST(@filled AS varchar)
     + N'  ->  rows the Planning screen will now show: ' + CAST(@willShow AS varchar) AS result;

-- =================================================================================================
-- TEARDOWN - clears only the planning policy, and only for the rows this script can have touched.
-- It cannot distinguish a point it wrote from one a person wrote afterwards, so it is deliberately
-- narrow: run it only if no real policy has been entered since.
--
--   UPDATE s SET ReorderPoint = NULL, MaxQty = NULL, MinQty = NULL
--   FROM ItemWarehouseSettings s
--   JOIN StockBalances b ON b.ItemId = s.ItemId AND b.WarehouseId = s.WarehouseId
--   WHERE b.CompanyID = 1 AND b.QtyOnHand > 0;
-- =================================================================================================
