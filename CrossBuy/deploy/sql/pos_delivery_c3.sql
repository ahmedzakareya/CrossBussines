-- POS-C3: delivery lifecycle status on the order (operational; null → OutForDelivery → Delivered).
IF COL_LENGTH('PosOrders','DeliveryStatus') IS NULL ALTER TABLE PosOrders ADD DeliveryStatus NVARCHAR(30) NULL;
GO
