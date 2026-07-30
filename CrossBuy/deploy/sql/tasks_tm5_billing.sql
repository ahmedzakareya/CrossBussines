-- TM-5: task billing fields + per-entry invoiced guard. Additive + idempotent. No new accounting writer.
IF COL_LENGTH('TaskItems','IsBillable') IS NULL ALTER TABLE TaskItems ADD IsBillable BIT NOT NULL DEFAULT 0;
GO
IF COL_LENGTH('TaskItems','BillRate') IS NULL ALTER TABLE TaskItems ADD BillRate DECIMAL(19,4) NULL;
GO
IF COL_LENGTH('TaskItems','CustomerId') IS NULL ALTER TABLE TaskItems ADD CustomerId INT NULL;
GO
IF COL_LENGTH('TimesheetEntries','InvoicedInvoiceId') IS NULL ALTER TABLE TimesheetEntries ADD InvoicedInvoiceId INT NULL;
GO
