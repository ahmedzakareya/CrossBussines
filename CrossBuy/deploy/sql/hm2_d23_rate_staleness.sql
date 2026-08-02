-- HM-2 HM-D23: exchange-rate staleness policy on AccountingSettings (company level). Idempotent. NOT an EF migration.
-- RateMaxAgeDays: 0 = no limit (default, zero regression). >0 = a rate older than N days (vs the DOCUMENT date) is stale.
-- RateStaleBehavior: 'Warn' (default — sale proceeds + on-screen notice + counted) or 'Reject' (sale blocked).
IF COL_LENGTH('AccountingSettings','RateMaxAgeDays') IS NULL
    ALTER TABLE AccountingSettings ADD RateMaxAgeDays int NOT NULL CONSTRAINT DF_AS_RateMaxAgeDays DEFAULT 0;
IF COL_LENGTH('AccountingSettings','RateStaleBehavior') IS NULL
    ALTER TABLE AccountingSettings ADD RateStaleBehavior nvarchar(10) NOT NULL CONSTRAINT DF_AS_RateStaleBehavior DEFAULT N'Warn';
