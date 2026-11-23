-- Email — a mailbox per employee (owner, 2026-11-23: "the real mail as it is, nothing external; carry on with the plan").
-- Idempotent; safe to re-run.
--
-- Until now CommMessages was ONE mailbox for the whole company: every employee who opened /Comm saw every message
-- anybody had sent. A message stays one row; who sees it is now a row of its own:
--   CommMailboxItems : one per (message, employee, box). Box 'Sent' for the sender, 'Inbox' for every recipient who
--                      is an employee of the same company (delivered inside the system, never through SMTP).
--                      Read, starred and deleted are per person: one reader deleting a message does not take it from
--                      the others.
--   CommMessages.ThreadId : the first message of a conversation (a reply carries its parent's thread).
IF OBJECT_ID('dbo.CommMailboxItems') IS NULL
CREATE TABLE dbo.CommMailboxItems (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CommMailboxItems PRIMARY KEY,
    CompanyID int NOT NULL,
    MessageId int NOT NULL,
    EmployeeId int NOT NULL,
    Box nvarchar(10) NOT NULL,                 -- Inbox | Sent
    Address nvarchar(260) NULL,                -- the address it was delivered to (Inbox); null for Sent
    ReadAt datetime2 NULL,
    Starred bit NOT NULL CONSTRAINT DF_CommMailboxItems_Starred DEFAULT 0,
    DeletedAt datetime2 NULL,
    CreatedAt datetime2 NOT NULL);
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommMailboxItems_Box')
    ALTER TABLE dbo.CommMailboxItems ADD CONSTRAINT CK_CommMailboxItems_Box CHECK (Box IN (N'Inbox', N'Sent'));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommMailboxItems_Message_Employee_Box')
    CREATE UNIQUE INDEX UX_CommMailboxItems_Message_Employee_Box ON dbo.CommMailboxItems (MessageId, EmployeeId, Box);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommMailboxItems_Owner')
    CREATE INDEX IX_CommMailboxItems_Owner ON dbo.CommMailboxItems (CompanyID, EmployeeId, Box, DeletedAt) INCLUDE (MessageId, ReadAt, Starred);
GO
IF COL_LENGTH('dbo.CommMessages', 'ThreadId') IS NULL
    ALTER TABLE dbo.CommMessages ADD ThreadId int NULL;
GO

-- ---- the mail that already exists ----------------------------------------------------------------------------
-- Every message a person sent goes into that person's Sent, keeping its star and its deleted state.
INSERT INTO dbo.CommMailboxItems (CompanyID, MessageId, EmployeeId, Box, Address, ReadAt, Starred, DeletedAt, CreatedAt)
SELECT m.CompanyID, m.Id, m.CreatedBy, N'Sent', NULL, COALESCE(m.CreatedAt, SYSUTCDATETIME()), m.Starred, m.DeletedAt,
       COALESCE(m.CreatedAt, SYSUTCDATETIME())
FROM dbo.CommMessages m
WHERE m.CreatedBy IS NOT NULL AND m.CreatedBy > 0
  AND EXISTS (SELECT 1 FROM dbo.Employee e WHERE e.ID = m.CreatedBy AND e.EmpCompanyID = m.CompanyID)
  AND NOT EXISTS (SELECT 1 FROM dbo.CommMailboxItems i WHERE i.MessageId = m.Id AND i.EmployeeId = m.CreatedBy AND i.Box = N'Sent');

-- A message already sent to a colleague's address lands in that colleague's Inbox, as read (it is history, not news).
;WITH addr AS (
    SELECT m.Id AS MessageId, m.CompanyID, COALESCE(m.CreatedAt, SYSUTCDATETIME()) AS At,
           LTRIM(RTRIM(s.value)) AS Address
    FROM dbo.CommMessages m
    CROSS APPLY STRING_SPLIT(REPLACE(m.ToAddress + N',' + COALESCE(m.Cc, N''), N';', N','), N',') s
    WHERE m.Status = N'Sent' AND LTRIM(RTRIM(s.value)) <> N''
), hit AS (
    SELECT a.MessageId, a.CompanyID, a.At, MIN(a.Address) AS Address, e.ID AS EmployeeId
    FROM addr a
    JOIN dbo.Employee e ON e.EmpCompanyID = a.CompanyID AND e.IsActive = 1
                       AND e.Email IS NOT NULL AND LOWER(LTRIM(RTRIM(e.Email))) = LOWER(a.Address)
    GROUP BY a.MessageId, a.CompanyID, a.At, e.ID
)
INSERT INTO dbo.CommMailboxItems (CompanyID, MessageId, EmployeeId, Box, Address, ReadAt, Starred, DeletedAt, CreatedAt)
SELECT h.CompanyID, h.MessageId, h.EmployeeId, N'Inbox', h.Address, h.At, 0, NULL, h.At
FROM hit h
WHERE NOT EXISTS (SELECT 1 FROM dbo.CommMailboxItems i WHERE i.MessageId = h.MessageId AND i.EmployeeId = h.EmployeeId AND i.Box = N'Inbox');

-- Threads for the existing mail: a reply belongs to its parent's thread (walked to the root).
;WITH t AS (
    SELECT Id, Id AS Root FROM dbo.CommMessages WHERE ParentId IS NULL
    UNION ALL
    SELECT c.Id, t.Root FROM dbo.CommMessages c JOIN t ON c.ParentId = t.Id
)
UPDATE m SET ThreadId = t.Root
FROM dbo.CommMessages m JOIN t ON t.Id = m.Id
WHERE m.ThreadId IS NULL
OPTION (MAXRECURSION 200);
UPDATE dbo.CommMessages SET ThreadId = Id WHERE ThreadId IS NULL;
GO
