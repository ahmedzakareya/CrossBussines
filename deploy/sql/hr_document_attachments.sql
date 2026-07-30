-- HR-5: multiple file attachments per employment contract / employee document.
-- Idempotent, additive. Safe to re-run.
IF OBJECT_ID('HrDocumentAttachments', 'U') IS NULL
BEGIN
    CREATE TABLE HrDocumentAttachments (
        ID          INT IDENTITY(1,1) PRIMARY KEY,
        CompanyID   INT           NOT NULL,
        OwnerKind   NVARCHAR(20)  NOT NULL,   -- Contract | Document
        OwnerID     INT           NOT NULL,
        FilePath    NVARCHAR(400) NOT NULL,
        FileName    NVARCHAR(260) NULL,
        UploadedAt  DATETIME2     NULL
    );
    CREATE INDEX IX_HrDocAtt_Owner ON HrDocumentAttachments (CompanyID, OwnerKind, OwnerID);
END
GO

-- One-time migration: fold any existing single FilePath on contracts/documents into the new table
-- so records that already had a file keep it as their first attachment. Guarded against double-run.
IF OBJECT_ID('HrDocumentAttachments', 'U') IS NOT NULL
BEGIN
    INSERT INTO HrDocumentAttachments (CompanyID, OwnerKind, OwnerID, FilePath, FileName, UploadedAt)
    SELECT c.CompanyID, 'Contract', c.ID, c.FilePath, NULL, c.CreatedAt
    FROM EmploymentContracts c
    WHERE c.FilePath IS NOT NULL AND LTRIM(RTRIM(c.FilePath)) <> ''
      AND NOT EXISTS (SELECT 1 FROM HrDocumentAttachments a
                      WHERE a.OwnerKind = 'Contract' AND a.OwnerID = c.ID AND a.FilePath = c.FilePath);

    INSERT INTO HrDocumentAttachments (CompanyID, OwnerKind, OwnerID, FilePath, FileName, UploadedAt)
    SELECT d.CompanyID, 'Document', d.ID, d.FilePath, NULL, d.CreatedAt
    FROM EmployeeDocuments d
    WHERE d.FilePath IS NOT NULL AND LTRIM(RTRIM(d.FilePath)) <> ''
      AND NOT EXISTS (SELECT 1 FROM HrDocumentAttachments a
                      WHERE a.OwnerKind = 'Document' AND a.OwnerID = d.ID AND a.FilePath = d.FilePath);
END
GO
