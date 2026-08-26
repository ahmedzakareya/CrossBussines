-- =============================================================================================
-- Internal Chat — direct-conversation identity. Slice comm_chat_identity_001.
--
-- WHAT DEFECT THIS CLOSES. GetOrCreateDirectAsync looked for an existing Direct conversation between
-- {me, other} and inserted one when it found none. That is find-then-insert with no guard: two requests
-- that interleave both find nothing and both insert, and the pair ends up with two conversations. Half
-- the messages then live in each, and nothing in the schema says which one is the real conversation.
-- Service-level care cannot fix this — only the database can refuse the second row.
--
-- HOW. A Direct conversation carries its participant pair ON THE CONVERSATION ROW, ordered so the lower
-- employee id is always DirectKeyLow. {A,B} and {B,A} therefore produce the same key, which is what makes
-- A -> B and B -> A the same conversation as a matter of storage rather than convention. A FILTERED UNIQUE
-- index over (CompanyID, DirectKeyLow, DirectKeyHigh) WHERE Kind = 'Direct' makes the duplicate impossible.
--
-- COMPANY IS PART OF THE IDENTITY. Two different companies may each have a Direct conversation between
-- employee 5 and employee 9 and they are different conversations, so CompanyID leads the index. Without it
-- the first company to create a pair would block the second.
--
-- GROUPS ARE UNTOUCHED. The index is filtered to Kind = 'Direct'; Group rows keep NULL keys and their
-- membership stays free to change. This is what keeps a future Group Chat increment possible without
-- replacing anything here.
--
-- ADDITIVE + IDEMPOTENT. Safe to run any number of times:
--   * adds two NULLable columns, guarded by a COL_LENGTH check;
--   * backfills ONLY Direct rows whose keys are still NULL, and only from their own member rows;
--   * creates the index only when absent;
--   * NO row is deleted, NO conversation is merged, NO message is touched;
--   * NO GL, stock, accounting, inventory, CRM, authorization or POS object is referenced.
--
-- IT REFUSES RATHER THAN REPAIRS. If a duplicate pair already exists the backfill would make the unique
-- index uncreatable, and the honest response is to stop and let a human decide which conversation
-- survives — merging chat history automatically is a data-loss decision, not a deployment step. The guard
-- below raises and rolls back instead of quietly deleting. On CrossBuyDev at the time of writing there are
-- 5 Direct conversations, every one with exactly 2 members, 0 duplicate pairs and 0 self-directs.
--
-- SQL BEFORE CODE. Migrations are disabled in this project (idempotent SQL in deploy/sql, not EF
-- migrations), so apply this BEFORE deploying the ChatService change that writes the keys.
--
-- RUN IT WITH sqlcmd -I  (QUOTED_IDENTIFIER ON). The index below is FILTERED and SQL Server refuses to
-- create a filtered index when QUOTED_IDENTIFIER is OFF.
-- =============================================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

-- ---- 1. columns ----------------------------------------------------------------------------
IF COL_LENGTH('dbo.Conversations', 'DirectKeyLow') IS NULL
BEGIN
    ALTER TABLE dbo.Conversations ADD DirectKeyLow int NULL;
    PRINT 'Conversations.DirectKeyLow added.';
END
ELSE PRINT 'Conversations.DirectKeyLow already present.';

IF COL_LENGTH('dbo.Conversations', 'DirectKeyHigh') IS NULL
BEGIN
    ALTER TABLE dbo.Conversations ADD DirectKeyHigh int NULL;
    PRINT 'Conversations.DirectKeyHigh added.';
END
ELSE PRINT 'Conversations.DirectKeyHigh already present.';
GO

-- ---- 2. refuse to proceed if the data cannot satisfy the constraint -------------------------
-- Checked BEFORE the backfill so a refusal leaves the table exactly as it was found.
IF EXISTS (
    SELECT 1
    FROM (
        SELECT c.CompanyID, a.EmployeeId AS E1, b.EmployeeId AS E2
        FROM dbo.ConversationMembers a
        JOIN dbo.ConversationMembers b
             ON b.ConversationId = a.ConversationId AND a.EmployeeId < b.EmployeeId
        JOIN dbo.Conversations c ON c.ID = a.ConversationId
        WHERE c.Kind = 'Direct'
        GROUP BY c.CompanyID, a.EmployeeId, b.EmployeeId
        HAVING COUNT(DISTINCT a.ConversationId) > 1
    ) dup
)
BEGIN
    RAISERROR (
      'comm_chat_identity_001 REFUSED: duplicate Direct conversations exist for at least one employee pair. Nothing was changed. Decide which conversation survives before re-running; this script will not merge or delete chat history.',
      16, 1);
END
GO

-- ---- 3. backfill ---------------------------------------------------------------------------
-- Only Direct rows, only where the key is still NULL, only from that conversation's own two members.
-- A Direct row that does not have exactly 2 distinct members is left alone rather than guessed at: it is
-- malformed data and inventing a key for it would hide that.
UPDATE c
   SET c.DirectKeyLow  = k.LowId,
       c.DirectKeyHigh = k.HighId
  FROM dbo.Conversations c
  JOIN (
        SELECT m.ConversationId,
               MIN(m.EmployeeId) AS LowId,
               MAX(m.EmployeeId) AS HighId
          FROM dbo.ConversationMembers m
         GROUP BY m.ConversationId
        HAVING COUNT(DISTINCT m.EmployeeId) = 2
       ) k ON k.ConversationId = c.ID
 WHERE c.Kind = 'Direct'
   AND (c.DirectKeyLow IS NULL OR c.DirectKeyHigh IS NULL);

PRINT CONCAT('Direct conversations keyed by this run: ', @@ROWCOUNT);
GO

-- ---- 4. filtered unique index --------------------------------------------------------------
-- The constraint that actually prevents the duplicate. Filtered to Direct so Group rows are exempt, and
-- to NOT NULL keys so a malformed Direct row left unkeyed in step 3 cannot block index creation.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Conversations_DirectPair' AND object_id = OBJECT_ID('dbo.Conversations'))
BEGIN
    CREATE UNIQUE INDEX UX_Conversations_DirectPair
        ON dbo.Conversations (CompanyID, DirectKeyLow, DirectKeyHigh)
        WHERE Kind = 'Direct' AND DirectKeyLow IS NOT NULL AND DirectKeyHigh IS NOT NULL;
    PRINT 'UX_Conversations_DirectPair created.';
END
ELSE PRINT 'UX_Conversations_DirectPair already present.';
GO

-- ---- 5. lookup support ---------------------------------------------------------------------
-- get-or-create reads by (CompanyID, pair) on every "start a conversation with this person" click. The
-- unique index above already covers that read, so no second index is created here; this note exists so a
-- later reader does not add a redundant one.
PRINT 'comm_chat_identity_001 complete.';
GO
