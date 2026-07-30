-- RC-3e: KDS station routing (operational only — no accounting). Idempotent.
-- PosMenuGroup.KitchenStationId = the station a tab's items route to (per-branch); NULL → default station.
-- PosOrderLine.StationId = the station that prepares this line, FROZEN at send-to-kitchen.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PosMenuGroups') AND name = 'KitchenStationId')
    ALTER TABLE dbo.PosMenuGroups ADD KitchenStationId INT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PosOrderLines') AND name = 'StationId')
    ALTER TABLE dbo.PosOrderLines ADD StationId INT NULL;
GO
