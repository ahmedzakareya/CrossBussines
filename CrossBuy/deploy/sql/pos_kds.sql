-- RC-3a: KDS line prep state (operational only — no accounting). Idempotent.
-- NULL before a line is sent → "New" on send-to-kitchen → kitchen advances New→Preparing→Ready.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PosOrderLines') AND name = 'KdsStatus')
BEGIN
    ALTER TABLE dbo.PosOrderLines ADD KdsStatus NVARCHAR(20) NULL;
END
GO
