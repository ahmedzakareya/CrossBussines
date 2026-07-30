-- POS floor board: manual table status (Available / Reserved / Cleaning / Closed).
-- "Occupied" is NOT stored — it is derived at runtime from an Open order on the table.
-- Idempotent: safe to re-run.
IF COL_LENGTH('RestaurantTables','Status') IS NULL
    ALTER TABLE RestaurantTables
        ADD Status NVARCHAR(20) NOT NULL CONSTRAINT DF_RestaurantTables_Status DEFAULT 'Available';
GO
