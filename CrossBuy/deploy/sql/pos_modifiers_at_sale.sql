-- RC-4a: modifiers chosen on an order line (operational recording; backflush at pay). Idempotent, purely additive.
IF OBJECT_ID('PosOrderLineModifiers','U') IS NULL
CREATE TABLE PosOrderLineModifiers (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    OrderLineId INT NOT NULL,
    GroupId INT NOT NULL,
    OptionId INT NOT NULL,
    Name NVARCHAR(200) NOT NULL DEFAULT '',
    LinkedItemId INT NOT NULL,
    QtyDeducted DECIMAL(19,4) NOT NULL DEFAULT 1,
    ExtraPrice DECIMAL(19,4) NOT NULL DEFAULT 0,
    Sort INT NOT NULL DEFAULT 0
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PosOrderLineModifiers_Line' AND object_id = OBJECT_ID('PosOrderLineModifiers'))
    CREATE INDEX IX_PosOrderLineModifiers_Line ON PosOrderLineModifiers (OrderLineId);
GO
