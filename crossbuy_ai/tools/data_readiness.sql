-- =================================================================================================
-- CAN THIS DATA ANSWER THE QUESTION? — the foundation under every AI capability.
--
-- WHY THIS EXISTS. The assistant was taught to understand a question perfectly and then answered
-- "الصنف موجود ولا يوجد له رصيد مسجَّل" — truthfully, because the item had no stock. It was found
-- from a screenshot. 140 of the 231 stockable items in this catalogue have never had a single
-- movement, so that sentence was the most common answer the assistant could give, and nobody knew.
--
-- A MODEL CANNOT BE BETTER THAN ITS DATA, and the usual failure is not that the data is wrong —
-- it is that it is ABSENT and nothing says so. Every defect found in this service has been one
-- disease: a missing value becoming a confident number. This is the same disease measured one
-- level down, before a model is built on top of it.
--
-- Run:  sqlcmd -S localhost -E -C -d CrossBuyDB2 -i tools/data_readiness.sql
-- Or:   python -I tools/data_readiness.py        (adds thresholds and an exit code)
--
-- READ THE PERCENTAGE, NOT THE COUNT. "1,525 movements" sounds like plenty and says nothing about
-- whether the item somebody asks about is one of the 37% that has any.
-- =================================================================================================
SET NOCOUNT ON;

DECLARE @stockable INT = (SELECT COUNT(*) FROM Items WHERE IsActive = 1 AND ItemType = 'Stockable');
DECLARE @customers INT = (SELECT COUNT(*) FROM Customers);

SELECT capability, requires, have, [of], pct, verdict FROM (

    -- ---- "how much of X do we have?" ---------------------------------------------------------
    SELECT 1 AS ord, 'item-stock' AS capability, 'a stock balance row' AS requires,
           COUNT(DISTINCT b.ItemId) AS have, @stockable AS [of],
           CAST(100.0 * COUNT(DISTINCT b.ItemId) / NULLIF(@stockable, 0) AS DECIMAL(5,1)) AS pct,
           CASE WHEN 100.0 * COUNT(DISTINCT b.ItemId) / NULLIF(@stockable, 0) >= 80 THEN 'ready'
                WHEN 100.0 * COUNT(DISTINCT b.ItemId) / NULLIF(@stockable, 0) >= 40 THEN 'partial'
                ELSE 'NOT READY' END AS verdict
    FROM StockBalances b
    JOIN Items i ON i.ID = b.ItemId AND i.IsActive = 1 AND i.ItemType = 'Stockable'

    -- ---- "what does X cost?" -----------------------------------------------------------------
    UNION ALL
    SELECT 2, 'item-value', 'an average cost greater than zero',
           COUNT(DISTINCT b.ItemId), @stockable,
           CAST(100.0 * COUNT(DISTINCT b.ItemId) / NULLIF(@stockable, 0) AS DECIMAL(5,1)),
           CASE WHEN 100.0 * COUNT(DISTINCT b.ItemId) / NULLIF(@stockable, 0) >= 80 THEN 'ready'
                WHEN 100.0 * COUNT(DISTINCT b.ItemId) / NULLIF(@stockable, 0) >= 40 THEN 'partial'
                ELSE 'NOT READY' END
    FROM StockBalances b
    JOIN Items i ON i.ID = b.ItemId AND i.IsActive = 1 AND i.ItemType = 'Stockable'
    WHERE b.AvgCost > 0

    -- ---- "what happened to X?" ---------------------------------------------------------------
    UNION ALL
    SELECT 3, 'item-movements', 'at least one stock movement',
           COUNT(DISTINCT m.ItemId), @stockable,
           CAST(100.0 * COUNT(DISTINCT m.ItemId) / NULLIF(@stockable, 0) AS DECIMAL(5,1)),
           CASE WHEN 100.0 * COUNT(DISTINCT m.ItemId) / NULLIF(@stockable, 0) >= 80 THEN 'ready'
                WHEN 100.0 * COUNT(DISTINCT m.ItemId) / NULLIF(@stockable, 0) >= 40 THEN 'partial'
                ELSE 'NOT READY' END
    FROM StockMovements m
    JOIN Items i ON i.ID = m.ItemId AND i.IsActive = 1 AND i.ItemType = 'Stockable'

    -- ---- "what should I reorder?" ------------------------------------------------------------
    -- The threshold itself, which the engine cannot infer: a reorder point of zero is unset, not
    -- "reorder at zero", and treating the two alike once produced 42 phantom items below a
    -- threshold none of them had.
    UNION ALL
    SELECT 4, 'reorder', 'a reorder point above zero',
           COUNT(DISTINCT s.ItemId), @stockable,
           CAST(100.0 * COUNT(DISTINCT s.ItemId) / NULLIF(@stockable, 0) AS DECIMAL(5,1)),
           CASE WHEN 100.0 * COUNT(DISTINCT s.ItemId) / NULLIF(@stockable, 0) >= 80 THEN 'ready'
                WHEN 100.0 * COUNT(DISTINCT s.ItemId) / NULLIF(@stockable, 0) >= 40 THEN 'partial'
                ELSE 'NOT READY' END
    FROM ItemWarehouseSettings s
    JOIN Items i ON i.ID = s.ItemId AND i.IsActive = 1 AND i.ItemType = 'Stockable'
    WHERE s.ReorderPoint IS NOT NULL AND s.ReorderPoint > 0

    -- ---- "when will the money arrive?" -------------------------------------------------------
    -- NO SALES INVOICE IN THIS SCHEMA CARRIES A DUE DATE. The cash-flow projection therefore has
    -- to derive one from the customer's payment terms, and that field is null on every customer,
    -- so the horizon is guesswork wearing a date. This is one field on 42 rows — the cheapest
    -- unlock in the whole list.
    UNION ALL
    SELECT 5, 'cashflow-forecast', 'payment terms on the customer',
           COUNT(*), @customers,
           CAST(100.0 * COUNT(*) / NULLIF(@customers, 0) AS DECIMAL(5,1)),
           CASE WHEN 100.0 * COUNT(*) / NULLIF(@customers, 0) >= 80 THEN 'ready'
                WHEN 100.0 * COUNT(*) / NULLIF(@customers, 0) >= 40 THEN 'partial'
                ELSE 'NOT READY' END
    FROM Customers WHERE PaymentTermsDays IS NOT NULL

    -- ---- REVENUE THAT CAN BE ATTRIBUTED TO AN ITEM --------------------------------------------
    -- 93.5% of posted sales revenue sits on lines with a null ItemId, so any per-item sales answer
    -- speaks for about six per cent of the money. The line COUNT hides this completely: only a
    -- quarter of lines are unlinked, but they carry almost all of the value. Measured by money for
    -- exactly that reason — a count would have reported this as 75% ready.
    UNION ALL
    SELECT 8, 'sales-by-item', 'revenue on lines that name an item',
           CAST(SUM(CASE WHEN li.ItemId IS NOT NULL THEN li.LineTotal * ISNULL(s.ExchangeRate,1) ELSE 0 END) AS INT),
           CAST(SUM(li.LineTotal * ISNULL(s.ExchangeRate,1)) AS INT),
           CAST(100.0 * SUM(CASE WHEN li.ItemId IS NOT NULL THEN li.LineTotal * ISNULL(s.ExchangeRate,1) ELSE 0 END)
                / NULLIF(SUM(li.LineTotal * ISNULL(s.ExchangeRate,1)), 0) AS DECIMAL(5,1)),
           CASE WHEN 100.0 * SUM(CASE WHEN li.ItemId IS NOT NULL THEN li.LineTotal * ISNULL(s.ExchangeRate,1) ELSE 0 END)
                     / NULLIF(SUM(li.LineTotal * ISNULL(s.ExchangeRate,1)), 0) >= 80 THEN 'ready'
                WHEN 100.0 * SUM(CASE WHEN li.ItemId IS NOT NULL THEN li.LineTotal * ISNULL(s.ExchangeRate,1) ELSE 0 END)
                     / NULLIF(SUM(li.LineTotal * ISNULL(s.ExchangeRate,1)), 0) >= 40 THEN 'partial'
                ELSE 'NOT READY' END
    FROM SalesInvoiceLines li JOIN SalesInvoices s ON s.ID = li.SalesInvoiceId
    WHERE s.Status = 'Posted'

    -- ---- NAMES THAT SURVIVED THE WRITE -------------------------------------------------------
    -- Not a capability but a precondition for every one of them. Five customers here have their
    -- Arabic name stored as literal "?" characters, which is a non-Unicode write that destroyed
    -- the letters rather than a rendering fault — so no screen, report or assistant can show them,
    -- and the text cannot be recovered from the database. Counted because an answer that names
    -- the wrong-looking customer reads as a bug in the answer.
    UNION ALL
    SELECT 7, 'customer-names', 'an Arabic name that is not question marks',
           COUNT(*), (SELECT COUNT(*) FROM Customers),
           CAST(100.0 * COUNT(*) / NULLIF((SELECT COUNT(*) FROM Customers), 0) AS DECIMAL(5,1)),
           CASE WHEN 100.0 * COUNT(*) / NULLIF((SELECT COUNT(*) FROM Customers), 0) >= 99 THEN 'ready'
                WHEN 100.0 * COUNT(*) / NULLIF((SELECT COUNT(*) FROM Customers), 0) >= 90 THEN 'partial'
                ELSE 'NOT READY' END
    FROM Customers WHERE Name NOT LIKE '%?%'

    -- ---- "is anything odd in the ledger?" ----------------------------------------------------
    -- Measured against a floor rather than a share: anomaly detection needs ENOUGH entries, not a
    -- proportion of anything. 500 is the point below which one unusual week dominates the model.
    UNION ALL
    SELECT 6, 'journal-anomalies', 'at least 500 journal entries',
           COUNT(*), 500,
           CAST(100.0 * COUNT(*) / 500.0 AS DECIMAL(5,1)),
           CASE WHEN COUNT(*) >= 500 THEN 'ready' ELSE 'NOT READY' END
    FROM JournalEntries
) r
ORDER BY ord;
