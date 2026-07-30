-- Guests (headcount) per POS bill/party — drives how many chairs light up on the floor board.
IF COL_LENGTH('PosOrders','Guests') IS NULL
    ALTER TABLE PosOrders ADD Guests INT NOT NULL CONSTRAINT DF_PosOrders_Guests DEFAULT 1;
GO
