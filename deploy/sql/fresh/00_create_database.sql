-- Run on master. Creates CrossBuyDB2 if it doesn't exist (idempotent).
IF DB_ID('CrossBuyDB2') IS NULL
BEGIN
    CREATE DATABASE CrossBuyDB2;
END
GO
PRINT 'CrossBuyDB2 ready.';
