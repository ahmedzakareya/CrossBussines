-- ============================================================================
-- CrossBuy — POS / Restaurant schema, FULL build order (idempotent).
-- Run against a fresh DB to create every POS object. All scripts are IF-guarded,
-- so re-running is safe. Execute from this folder:  sqlcmd -S . -d <DB> -E -i pos_schema_master.sql
-- (assumes the core accounting/inventory schema — Accounts, Items, Branches,
--  Warehouses, JournalEntries, SalesInvoices/Receipts — already exists).
-- Order = base tables first, then add-column / child scripts.
-- ============================================================================
:on error exit

-- 1) base tables ------------------------------------------------------------
:r pos_order.sql              -- PosOrders, PosOrderLines, PosPayments
:r pos_setup.sql              -- ActivityPresets(+Caps), BranchCapabilities, BranchPosSettings, DiningAreas, KitchenStations, RestaurantTables
:r pos_quickmenu.sql          -- PosMenuGroups, PosQuickItems, Items.QuickCode
:r pos_terminals.sql          -- PosTerminals, PosShifts + PosOrders.(TerminalId/ShiftId/ReceiptNo) + terminal receipt cols
:r pos_payment_roles.sql      -- BranchPaymentMethods, BranchUserRoles
:r pos_modifiers.sql          -- ModifierGroups, ModifierOptions, ItemModifierGroups
:r pos_reservations_b1.sql    -- Reservations
:r pos_modifiers_at_sale.sql  -- PosOrderLineModifiers
:r pos_branch_item_sourcing_bis1.sql  -- BranchItemSourcings

-- 2) add-column / feature scripts (need the base tables above) --------------
:r pos_dining_area_nameen.sql -- DiningAreas.NameEn
:r pos_station_nameen.sql     -- KitchenStations.NameEn
:r pos_kds.sql                -- PosOrderLines.KdsStatus
:r pos_kds_stations.sql       -- PosMenuGroups.KitchenStationId + PosOrderLines.StationId
:r pos_send_kitchen.sql       -- PosOrderLines.SentQty + SentAt
:r pos_hold_recall.sql        -- PosOrders.IsHeld + HeldAt
:r pos_merge_tables.sql       -- PosOrders.MergedIntoOrderId
:r pos_order_guests.sql       -- PosOrders.Guests
:r pos_delivery_c1.sql        -- DeliveryZones, CustomerAddresses + PosOrders.Delivery* + BranchPosSettings.Delivery*
:r pos_delivery_c2.sql        -- Drivers + PosOrders.DriverId
:r pos_delivery_c3.sql        -- PosOrders.DeliveryStatus
:r pos_shift_close_rc6a.sql   -- PosShifts.(ClosingFloat/ExpectedCash/CashVariance/…) + account 520111
:r pos_void_rc6c.sql          -- PosPayments.ReceiptId
:r pos_tip_rc5.sql            -- PosOrders.(TipAmount/TipMethod/TipJournalEntryId) + account 210207
