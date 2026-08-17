-- =============================================================================================
-- Platform Kernel slice 2 — kernel expansion (Customer · PurchaseInvoice · ManufWorkOrder)
--                            + the NotificationProjection consumer.
--
-- Additive + idempotent. Safe to run any number of times. NO GL/stock impact, NO data modification.
-- Requires deploy/sql/platform_business_events.sql (slice 1) to have run first.
--
-- WHAT THIS SLICE NEEDS FROM THE DATABASE, and why:
--
--   1. Notifications.EntityType / Notifications.EntityId (new, nullable)
--      A notification could not say WHICH business object it was about. `Type` is a catalog key (a meaning,
--      e.g. 'purchase_invoice') and `RefId` is untyped, so nothing could answer that question reliably.
--      NotificationProjection needs it to resolve the click-through through IEntityRegistry, and it makes
--      notifications addressable the same way BusinessEvents already is.
--
--   2. An index on Notifications (RecipientEmployeeID, DedupKey)
--      NotificationProjection's idempotency check runs once per (event, recipient) on every dispatch. The
--      pre-existing dedup query in NotificationService filters on UNREAD rows only, so it is a noise guard,
--      not idempotency — the consumer does its own unfiltered lookup and that lookup needs an index.
--      NOT unique: the same recipient may legitimately hold rows with a NULL key, and a UNIQUE index would
--      also turn a duplicate-suppression concern into a hard insert failure inside the dispatcher.
--
-- NO change is needed to BusinessEvents or BusinessEventDispatch. Slice 1's schema already carries
-- everything the second consumer needs: dispatch rows are keyed per (EventId, Consumer), the claiming index
-- is filtered on Status <> 'Done', and Attempts/Error/UpdatedAt already express retry state per consumer.
-- Adding NotificationProjection is therefore a CODE change only — which is the point of ADR-003.
-- =============================================================================================

-- ---------------------------------------------------------------------------------------------
-- 1. Notifications: entity addressing (additive, nullable — every existing row stays valid).
-- ---------------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.Notifications', 'EntityType') IS NULL
BEGIN
    ALTER TABLE dbo.Notifications ADD EntityType NVARCHAR(60) NULL;
    PRINT 'ADDED Notifications.EntityType';
END
ELSE PRINT 'Notifications.EntityType EXISTS';
GO

IF COL_LENGTH('dbo.Notifications', 'EntityId') IS NULL
BEGIN
    ALTER TABLE dbo.Notifications ADD EntityId INT NULL;
    PRINT 'ADDED Notifications.EntityId';
END
ELSE PRINT 'Notifications.EntityId EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 2. The idempotency lookup index for NotificationProjection.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Notifications_Recipient_DedupKey' AND object_id = OBJECT_ID('dbo.Notifications'))
BEGIN
    CREATE INDEX IX_Notifications_Recipient_DedupKey
        ON dbo.Notifications (RecipientEmployeeID, DedupKey)
        WHERE DedupKey IS NOT NULL;
    PRINT 'ADDED IX_Notifications_Recipient_DedupKey';
END
ELSE PRINT 'IX_Notifications_Recipient_DedupKey EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- 3. Reading aid for finding a notification by the entity it points at (new columns above).
--    Filtered so it only covers rows that actually carry an entity — i.e. rows written by the kernel.
-- ---------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Notifications_Entity' AND object_id = OBJECT_ID('dbo.Notifications'))
BEGIN
    CREATE INDEX IX_Notifications_Entity
        ON dbo.Notifications (CompanyID, EntityType, EntityId)
        WHERE EntityType IS NOT NULL;
    PRINT 'ADDED IX_Notifications_Entity';
END
ELSE PRINT 'IX_Notifications_Entity EXISTS';
GO

-- ---------------------------------------------------------------------------------------------
-- NOT DONE HERE, deliberately:
--
--   * No backfill of Notifications.EntityType/EntityId for historical rows. Mapping a legacy (Type, RefId)
--     pair onto a canonical entity code is a guess for several types, and the columns are only read for
--     kernel-written rows. An optional, separately-documented script can do it if it is ever wanted.
--
--   * No dispatch rows for the new consumer on events recorded BEFORE it was registered. A consumer starts
--     from the events written after it exists (ADR-003). Back-filling NotificationProjection rows for
--     historical events would deliver a burst of notifications about work that is already finished.
-- ---------------------------------------------------------------------------------------------
