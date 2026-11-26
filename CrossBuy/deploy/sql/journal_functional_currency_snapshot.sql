-- Forward-only writer valuation evidence. Never infer historic identity from current company defaults.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.JournalEntries','U') IS NULL OR OBJECT_ID('dbo.Currencies','U') IS NULL
 THROW 51600,'GL and currency schemas must precede functional valuation evidence.',1;
IF COL_LENGTH('dbo.JournalEntries','FunctionalCurrencyId') IS NULL
 ALTER TABLE dbo.JournalEntries ADD FunctionalCurrencyId int NULL;
IF COL_LENGTH('dbo.JournalEntries','FunctionalCurrencyDecimals') IS NULL
 ALTER TABLE dbo.JournalEntries ADD FunctionalCurrencyDecimals int NULL;
GO
IF NOT EXISTS(SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.JournalEntries') AND name='FK_JournalEntries_Currencies_FunctionalCurrencyId')
 ALTER TABLE dbo.JournalEntries WITH CHECK ADD CONSTRAINT FK_JournalEntries_Currencies_FunctionalCurrencyId FOREIGN KEY(FunctionalCurrencyId) REFERENCES dbo.Currencies(ID);
IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.JournalEntries') AND name='CK_JournalEntries_FunctionalEvidence')
 ALTER TABLE dbo.JournalEntries WITH CHECK ADD CONSTRAINT CK_JournalEntries_FunctionalEvidence CHECK(
  (FunctionalCurrencyId IS NULL AND FunctionalCurrencyDecimals IS NULL) OR
  (FunctionalCurrencyId IS NOT NULL AND FunctionalCurrencyId>0 AND FunctionalCurrencyDecimals IS NOT NULL AND FunctionalCurrencyDecimals BETWEEN 0 AND 28));
GO
CREATE OR ALTER TRIGGER dbo.TR_JournalEntries_FunctionalEvidenceImmutable ON dbo.JournalEntries AFTER UPDATE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT i.ID,i.FunctionalCurrencyId,i.FunctionalCurrencyDecimals FROM inserted i JOIN deleted d ON d.ID=i.ID WHERE d.Status IN('Posted','Reversed')
  EXCEPT SELECT d.ID,d.FunctionalCurrencyId,d.FunctionalCurrencyDecimals FROM deleted d WHERE d.Status IN('Posted','Reversed'))
  THROW 51601,'Posted journal valuation identity cannot be changed or backfilled.',1;
END;
GO
COMMIT TRANSACTION;
