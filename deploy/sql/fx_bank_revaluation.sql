-- ============================================================================
-- Multi-Currency — foreign bank-account revaluation. Idempotent.
-- ForeignBalance = the account's period-end balance in ITS OWN currency (from the bank statement);
-- revaluation compares (ForeignBalance × closing rate) to the GL carrying value → 4903/5903 + auto-reversal.
-- ============================================================================
IF COL_LENGTH('BankAccounts','ForeignBalance') IS NULL
    ALTER TABLE BankAccounts ADD ForeignBalance decimal(19,4) NULL;
GO
IF COL_LENGTH('FxRevaluationRuns','TotalBankDiff') IS NULL
    ALTER TABLE FxRevaluationRuns ADD TotalBankDiff decimal(19,4) NOT NULL CONSTRAINT DF_FxRun_BankDiff DEFAULT(0);
GO
