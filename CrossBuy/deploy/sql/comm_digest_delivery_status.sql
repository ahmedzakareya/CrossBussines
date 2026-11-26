-- =============================================================================================
-- Communication — widen CK_CommNotificationDeliveries_Status to admit 'Deferred'.
--
-- WHAT THIS UNBLOCKS. CPS-001 §9 records gap G5: Digest behaves as Immediate for InApp and is
-- EXCLUDED for every other channel, because no batching worker exists. CommPreferenceResolver
-- says so in one line — DigestNote() returns "preference is Digest and no digest batching worker
-- exists for this channel yet (ADR-034 §5)" — and the channel is dropped at queue time.
--
-- WHY A STATUS AND NOT A TABLE. A deferred delivery is the SAME row as an immediate one: same
-- notification, same channel, same dedup key, same recipient. Only its readiness differs. A
-- second table would duplicate every column and then need reconciling with the first.
--
-- AND WHY NO NEW COLUMN. The digest window is derived from CreatedAt, which this table already
-- carries (BaseEntity: CreatedBy/CreatedAt/updatedBy/UpdatedAt are columns 12-15). A worker
-- flushing "everything deferred for this recipient older than N minutes" needs nothing else.
--
-- THIS IS THE ONE SLICE IN THE COMMUNICATION SET THAT IS NOT PURELY ADDITIVE. It drops and
-- recreates an existing CHECK constraint. Three properties make that safe, and each is asserted
-- by the guard rather than assumed:
--
--   1. IT ONLY WIDENS. The new predicate is the old five values PLUS 'Deferred'. A widening CHECK
--      can never invalidate a row that already satisfied the narrower one, so there is no
--      validation scan risk and no possibility of failing on existing data.
--   2. IT IS ORDER-SAFE IN BOTH DIRECTIONS. Code that predates the digest worker never writes
--      'Deferred', so applying this slice early is inert. Code that writes 'Deferred' against an
--      un-widened constraint fails loudly at the INSERT rather than silently storing a value the
--      claim predicate would misread — which is why this is SQL BEFORE CODE, like every other
--      slice here.
--   3. THE DROP WINDOW TOUCHES NO ROWS. Between DROP and ADD the table is unconstrained on Status
--      for the length of one DDL pair inside a single batch. No DML runs in this script.
--
-- WHAT IT DOES NOT DO: it ships no digest worker, changes no preference resolution and alters no
-- claim predicate. Applying it alone changes NOTHING observable — see the footer.
--
-- DEPENDENCIES: communication_platform_slice_001.sql (CommNotificationDeliveries and its
-- CK_CommNotificationDeliveries_Status must already exist).
-- =============================================================================================

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------------------------------------
-- 1. Refuse to run against a database that has not had the base slice applied.
--
-- Stated as a hard stop rather than a silent skip: a CHECK this script cannot find is a database
-- in a state nobody intended, and widening a constraint that does not exist would leave Status
-- unconstrained for good.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommNotificationDeliveries', 'U') IS NULL
BEGIN
    RAISERROR('CommNotificationDeliveries does not exist. Apply communication_platform_slice_001.sql first.', 16, 1);
END
GO

-- ---------------------------------------------------------------------------------------------
-- 2. Drop the narrow constraint ONLY when it is genuinely narrow.
--
-- The definition test is what makes this idempotent. A re-run against an already-widened database
-- matches nothing, drops nothing and recreates nothing — the constraint is left exactly as it is,
-- rather than being dropped and rebuilt on every deployment.
-- ---------------------------------------------------------------------------------------------
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = 'CK_CommNotificationDeliveries_Status'
      AND parent_object_id = OBJECT_ID('dbo.CommNotificationDeliveries')
      AND definition NOT LIKE '%Deferred%')
BEGIN
    ALTER TABLE dbo.CommNotificationDeliveries DROP CONSTRAINT CK_CommNotificationDeliveries_Status;
    PRINT 'DROPPED CK_CommNotificationDeliveries_Status (narrow)';
END
ELSE PRINT 'CK_CommNotificationDeliveries_Status already admits Deferred, or is absent';
GO

-- ---------------------------------------------------------------------------------------------
-- 3. Recreate it with the sixth value.
--
-- WITH CHECK is implicit and intended: SQL Server validates existing rows against the new
-- predicate. That validation cannot fail here, because every stored value is one of the original
-- five and all five remain in the list. A constraint added WITH NOCHECK would be untrusted and
-- would stop the optimiser using it — the opposite of what a vocabulary constraint is for.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = 'CK_CommNotificationDeliveries_Status'
      AND parent_object_id = OBJECT_ID('dbo.CommNotificationDeliveries'))
BEGIN
    ALTER TABLE dbo.CommNotificationDeliveries ADD CONSTRAINT CK_CommNotificationDeliveries_Status
        CHECK ([Status] IN ('Pending','Claimed','Sent','Failed','Skipped','Deferred'));
    PRINT 'ADDED CK_CommNotificationDeliveries_Status (with Deferred)';
END
ELSE PRINT 'CK_CommNotificationDeliveries_Status EXISTS';
GO

-- =============================================================================================
-- WHY NO INDEX CHANGE IS NEEDED, which is the part most likely to be assumed wrong.
--
-- IX_CommNotificationDeliveries_Claim is filtered "WHERE [Status] <> 'Sent' AND [Status] <>
-- 'Skipped'". 'Deferred' is neither, so deferred rows are INSIDE that index already — which is
-- what a digest worker wants, because it scans for them by (Status, CreatedAt).
--
-- And the normal dispatcher will not pick them up. Both claim stores enumerate the statuses they
-- claim rather than excluding the ones they do not:
--
--     EfCommDeliveryClaimStore        Status == Pending || Failed || (Claimed && stale)
--     SqlServerCommDeliveryClaimStore [Status] = @Pending OR @Failed OR (@Claimed AND stale)
--
-- A status absent from both lists is therefore invisible to immediate dispatch by construction,
-- not by a filter somebody has to remember to add. That is the property that makes 'Deferred'
-- safe to introduce before the worker exists.
--
-- STILL REQUIRED AFTER THIS SLICE, for G5 to close:
--   1. CommDeliveryStatus.Deferred in the code vocabulary (+ IsValid/Values), and the schema
--      parity test updated to expect six values in this CHECK.
--   2. CommPreferenceResolver stops EXCLUDING Digest channels and marks them deferred instead,
--      so DigestNote() no longer reports "no batching worker exists".
--   3. A digest worker that claims Deferred rows by (CompanyID, recipient, CreatedAt window),
--      renders ONE summary per recipient per channel, and marks the whole claimed set Sent.
--      It must register through the SAME opt-in path as CommNotificationHostedService — an
--      explicit flag plus a positive company allow-list — because
--      CommunicationDiWiringTests.Registers_no_hosted_service is a hard invariant: this platform
--      adds NO hosted service by default.
-- =============================================================================================
