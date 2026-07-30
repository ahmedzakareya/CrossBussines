-- RC-6c: link each POS payment to the AR receipt it created, so a paid order can be cleanly reversed. Idempotent, additive.
IF COL_LENGTH('PosPayments','ReceiptId') IS NULL ALTER TABLE PosPayments ADD ReceiptId INT NULL;
GO
