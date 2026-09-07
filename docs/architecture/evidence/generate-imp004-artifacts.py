"""IMP-004 Master Data source artifacts, generated from one structured source.

Facts traced through production readers/writers, plus the accepted Hypermarket assessment
(Stage-002-Hypermarket-Current-State-Assessment.md) which is authoritative for retail behaviour.

Measured reader/writer counts:
  ItemBarcode          BL=2 ctrl=5 views=4 writes=10      Item.Barcode field refs=72  ItemBarcode table refs=43
  ItemImage            BL=2 ctrl=2 views=1 writes=2
  ItemComponent        BL=6 ctrl=4 views=1 writes=20      (load-bearing, RISK-033)
  UoMConversion        BL=4 ctrl=3 views=0 writes=6
  UnitOfMeasure        BL=1 ctrl=1 views=11 writes=0
  ItemCategory         BL=8 ctrl=4 views=7 writes=0
  ItemWarehouseSetting BL=4 ctrl=2 views=0 writes=7
  PriceList            BL=4 ctrl=4 views=7 writes=14
  ScaleCode refs=39    IsWeighted refs=31

Hypermarket rules that constrain any redesign:
  * Item.Barcode is the PRIMARY and unique; ItemBarcode(Barcode, UoMId) is secondary/per-unit.
  * Multi-barcode is gated by the BarcodeMulti capability; an AMBIGUOUS barcode is EXPLICITLY REJECTED.
  * UoMConversion(ItemId, From, To, Factor): a unit with NO conversion is REJECTED at add-line, NEVER factor-1.
  * IsWeighted + ScaleCode + ScaleBarcodeParser (GS1 mod-10, config-driven length) exist in the ENGINE
    but NO UI writes either field (Hypermarket finding F4).
"""
import csv, os, collections

EV = os.path.join("docs", "architecture", "evidence")

# ---------------------------------------------------------------- source inventory
F = ["source_id","entity_or_table","purpose","readers","writers","screens","apis","tests",
     "company_scope","branch_scope","lifecycle","source_of_truth_status","duplicated_fields",
     "historical_dependency","migration_suitability","security_behavior","classification","risks"]

def r(*a):
    assert len(a) == len(F), f"{a[0]}: {len(a)} of {len(F)}"
    return a

SRC = [
r("MD-001","Item","the master record - 39 properties covering identity units costing tracking pricing and storefront",
  "every module: StockService PricingService PosOrderService ManufService ProcurementService SellingService",
  "InventoryController ItemService DevSeed","ItemForm and inventory lists","InventoryApiController","inventory and POS tests",
  "CompanyID direct","none","IsActive only - no lifecycle states","AUTHORITATIVE for operational identity",
  "Barcode duplicates ItemBarcode; ImagePath duplicates ItemImage; 5 Store* fields are channel presentation",
  "stock cost layers and movements reference ItemId forever","Extend - never replace",
  "InvPerm read/doc/manage; 267 hardcoded company refs in InventoryController","Authoritative",
  "RISK-003 company constants; RISK-018 four overlapping type signals"),
r("MD-002","Item.Barcode (column)","the PRIMARY barcode - unique per company",
  "PosOrderService barcode scan; InventoryController lookups; label printing","InventoryController ItemService",
  "ItemForm barcode field","InventoryApiController lookup","POS scan tests",
  "unique within CompanyID","none","lives with the Item","LEGACY COMPATIBILITY - remains the primary designation",
  "same fact as an ItemBarcode row","72 field references across BL and Controllers",
  "KEEP - do not delete in Phase 0","read on the scan path","Legacy Compatibility",
  "RISK-009 two sources for one fact"),
r("MD-003","ItemBarcode (table)","SECONDARY and per-UoM barcodes - Barcode plus UoMId",
  "PosOrderService (UoMId drives the sold unit); InventoryController","InventoryController; DevSeed",
  "barcode management views (4)","InventoryApiController","hyper scan tests",
  "via parent Item CompanyID","none","rows added and removed freely",
  "AUTHORITATIVE for multi-barcode and per-UoM resolution",
  "Barcode value also on Item","43 table references; UoMId selects the SOLD UNIT",
  "TARGET for consolidation","gated by the BarcodeMulti capability; AMBIGUOUS barcode is EXPLICITLY REJECTED",
  "Authoritative","RISK-046 a barcode migration that changes the selected UoM would change sale quantity"),
r("MD-004","Item.ImagePath (column)","single legacy image path",
  "storefront and item lists","InventoryController ItemService","ItemForm","none","none",
  "with the Item","none","with the Item","LEGACY COMPATIBILITY",
  "same fact as an ItemImage row","legacy screens read it directly",
  "KEEP - do not delete in Phase 0","static path - not an authorization model","Legacy Compatibility",
  "RISK-010 two sources; RISK-012 static paths are not a security model"),
r("MD-005","ItemImage (table)","image collection for an item",
  "item detail and storefront","InventoryController","1 view","none","none",
  "via parent Item","none","rows added and removed","AUTHORITATIVE for the gallery once a primary is designated",
  "ImagePath duplicates the primary","low","TARGET for consolidation",
  "must inherit authorization from the parent Item via 2B attachments","Authoritative",
  "RISK-012 file authorization"),
r("MD-006","Item Store* fields (StoreOldPrice StoreBadge StoreRating StoreVendor StoreHoverImage)",
  "storefront presentation on the operational master record",
  "StoreController and storefront views","InventoryController ItemService","store views","store APIs","none",
  "with the Item","none","with the Item","CHANNEL-SPECIFIC - wrong owner",
  "presentation concerns inside operational master data","storefront rendering depends on them",
  "Separate into Commerce Presentation - no data movement in Phase 0",
  "no channel-level authorization exists","Channel-Specific",
  "RISK-011 presentation coupled to operational master data"),
r("MD-007","ItemComponent","composite composition - parent to component with SortOrder",
  "StockService:319 and :1189 kit explosion; PricingService:311 manufactured classification; PosOrderService:302 and :1746 sale-time explosion; ManufService; PosSetupService",
  "InventoryController; DevSeed","composite designer view","InventoryApiController","composite and POS tests",
  "CompanyID on the row","none","rows added and removed; NO revision history",
  "AUTHORITATIVE and LOAD-BEARING","CompositeType and ProductionMethod overlap with ItemType",
  "historical costs depend on explosion behaviour at the time of sale",
  "Extend with typing and revisions - NEVER reclassify historical rows",
  "InvPerm manage","Authoritative",
  "RISK-033 load-bearing in the two-writer core; RISK-047 no revision history so historical explosions cannot be reproduced"),
r("MD-008","Item.CompositeType","kit or bundle or sales-BOM discriminator",
  "StockService PosOrderService PricingService","InventoryController","composite designer","none","composite tests",
  "with the Item","none","with the Item","AMBIGUOUS - overlaps ItemType IsComposite ProductionMethod",
  "four overlapping type signals","behaviour differs per value historically",
  "Clarify semantics without changing stored values","InvPerm manage","Ambiguous","RISK-018 terminology"),
r("MD-009","Item.ProductionMethod","how a manufactured item is produced",
  "ManufService PosOrderService","InventoryController","manufacturing views","none","manuf tests",
  "with the Item","none","with the Item","AMBIGUOUS - overlaps CompositeType",
  "overlaps composite typing","RecipeAtSale backflush depends on it",
  "Clarify - meaning narrows to manufactured items only","InvPerm doc","Ambiguous","RISK-018"),
r("MD-010","UnitOfMeasure","unit definitions",
  "StockService ItemService PosOrderService IntegrityCheckService","none in production - reference data",
  "11 views","InventoryApiController","conversion tests",
  "company or global reference","none","stable reference data","AUTHORITATIVE",
  "none","every historical quantity is expressed in a unit","Preserve - extend with Unit Sets",
  "InvPerm manage","Authoritative","none"),
r("MD-011","UoMConversion","per-item conversion - ItemId From To Factor",
  "StockService:236; PosOrderService:547; ItemService; IntegrityCheckService (validates them); InventoryApiController",
  "InventoryController; DevSeed","conversion designer","InventoryApiController","conversion and POS tests",
  "via parent Item","none","rows added and removed; NO validity dates",
  "AUTHORITATIVE","none",
  "HIGH - a unit with NO conversion is REJECTED at add-line and NEVER defaulted to factor 1",
  "Extend with Unit Sets and validity - factors must not change",
  "InvPerm manage","Authoritative",
  "RISK-048 a conversion redesign that introduces a factor-1 fallback would silently change sale quantities"),
r("MD-012","Item.IsWeighted","weighted item flag",
  "PosOrderService; ScaleBarcodeParser; hyper checkout","NO UI WRITES ANYWHERE (Hypermarket finding F4)",
  "none - no admin screen","none","hyper scale tests",
  "with the Item","branch scale settings interact","with the Item","AUTHORITATIVE but unmanageable",
  "none","weighted sale quantity derives from it","New capability required - an admin screen",
  "no screen means no authorization surface","New Capability Required",
  "RISK-049 weighted and scale fields have no admin screen so they are DB or DevOnly-only today"),
r("MD-013","Item.ScaleCode","scale PLU code with filtered uniqueness",
  "ScaleBarcodeParser; PosOrderService scan path","NO UI WRITES ANYWHERE (F4)","none","none","hyper scale tests",
  "filtered uniqueness per company","branch scale config","with the Item",
  "AUTHORITATIVE but unmanageable","none",
  "scale barcode resolution depends on exact interpretation","New capability required - preserve filtered uniqueness EXACTLY",
  "no screen","New Capability Required","RISK-049; filtered uniqueness must be preserved byte-for-byte"),
r("MD-014","ItemCategory","classification tree",
  "8 BL services; 4 controllers","none measured in production writers","7 views","none","category tests",
  "CompanyID","none","tree with parents","AUTHORITATIVE","none","reporting groups by category",
  "Extend - add groups families brands","InvPerm manage","Authoritative","none"),
r("MD-015","ItemWarehouseSetting","per-item per-warehouse rules",
  "StockService; ProcurementService; 4 BL","InventoryController","none","none","stock tests",
  "via Item and Warehouse","warehouse implies branch","rows per pair","AUTHORITATIVE",
  "none","reorder and replenishment behaviour","Preserve","InvPerm manage","Authoritative","none"),
r("MD-016","PriceList and PriceListLine","price lists",
  "PricingService; SellingService; PosOrderService","InventoryController; PricingService","7 views","none","pricing tests",
  "CompanyID","branch or channel selection","effective dating exists","AUTHORITATIVE for price",
  "Item.SalesPrice is a separate default","historical pricing affects invoices",
  "Preserve - Master Data must not change pricing results","AccPerm post for price changes","Authoritative",
  "RISK-050 a Master Data change that alters price resolution would change invoice totals"),
r("MD-017","StockBatch and StockSerial","batch and serial instances",
  "StockService; FEFO allocation","StockService only","inventory views","none","FEFO tests",
  "via stock","warehouse and bin","per receipt","AUTHORITATIVE - owned by the stock writer",
  "TrackBatch and TrackExpiry and TrackSerial flags live on Item",
  "FEFO allocation order depends on them","Do not touch - stock writer owns these",
  "StockService is the only writer","Derived","none - out of Master Data scope"),
r("MD-018","BinLocation and BinStock","rack-level stock",
  "StockService movement contracts (BinLocationId SourceBinLocationId); ProcurementService; SellingService; WarehouseService",
  "StockService; WarehouseService","5 views","none","bin tests",
  "via Warehouse","branch via warehouse","operational","AUTHORITATIVE - operational not dormant",
  "none","bin balances are real","Out of IMP-004 scope - Stage 4 WMS extends it",
  "InvPerm doc and manage","Authoritative","RISK-019 foundation underused"),
]

# ---------------------------------------------------------------- barcode matrix
BF = ["case_id","barcode_kind","current_source","current_behavior","future_authority","primary_designation",
      "uniqueness","uom_binding","hypermarket_dependency","coexistence_read","coexistence_write",
      "divergence_detection","migration_step","rollback","must_not_change"]
BC = [
("BC-01","primary item barcode","Item.Barcode column","unique per company; scanned first",
 "ItemBarcode row flagged IsPrimary","IsPrimary flag on the row","unique per company",
 "base UoM implied","primary scan path","read ItemBarcode primary, fall back to Item.Barcode",
 "write both during coexistence","nightly compare of column vs primary row",
 "seed a primary ItemBarcode row from Item.Barcode","stop reading the primary row; column still populated",
 "the scanned item resolution must not change"),
("BC-02","secondary or per-UoM barcode","ItemBarcode(Barcode, UoMId)","UoMId SELECTS THE SOLD UNIT",
 "ItemBarcode - unchanged","not primary","unique per company across all barcodes",
 "UoMId is authoritative and drives quantity","CRITICAL - hyper checkout depends on it",
 "ItemBarcode only","ItemBarcode only","n/a - already authoritative","none needed",
 "n/a","ItemBarcode.UoMId semantics must remain IDENTICAL - a changed UoM changes sale quantity"),
("BC-03","ambiguous barcode","ItemBarcode multiple matches","EXPLICITLY REJECTED - never guesses",
 "unchanged","n/a","n/a","n/a","gated by the BarcodeMulti capability",
 "unchanged","unchanged","n/a","none","n/a",
 "the explicit rejection must be preserved - a redesign must not silently pick one match"),
("BC-04","scale or weighted barcode","Item.ScaleCode plus ScaleBarcodeParser","GS1 mod-10, config-driven length; prefix routed BEFORE lookup",
 "unchanged - parser is authoritative","n/a","ScaleCode filtered uniqueness per company",
 "weight or price embedded, not a UoM row","CRITICAL - fresh food checkout",
 "unchanged","unchanged","n/a","add an admin screen only - no parser change","n/a",
 "scale prefix routing order, mod-10 check, and embedded quantity interpretation must not change"),
("BC-05","supplier barcode","NOT MODELLED","no attribution of who issued a barcode",
 "ItemBarcode plus a Source column","not primary","unique per company",
 "optional UoMId","none today","n/a","new rows only","n/a","additive column",
 "drop the column","must not affect existing barcode resolution"),
("BC-06","packaging barcode","NOT MODELLED","no packaging concept exists",
 "ItemPackage barcode","not primary","unique per company","package UoM","none today",
 "n/a","new rows only","n/a","additive with packaging","drop","must not affect existing resolution"),
("BC-07","GS1 structured barcode","partially - the scale parser is GS1 mod-10","no general GS1 AI parsing",
 "a GS1 parser alongside the scale parser","n/a","n/a","AI-derived","scale path already GS1",
 "n/a","n/a","n/a","additive","drop","the existing scale parser must remain the scale path"),
]

# ---------------------------------------------------------------- image matrix
IF = ["case_id","media_kind","current_source","future_authority","primary_rule","authorization",
      "lifecycle","channel_behavior","coexistence_read","coexistence_write","migration_step","must_not_change"]
IM = [
("IM-01","primary item image","Item.ImagePath","ItemImage row flagged IsPrimary",
 "exactly one primary per item","inherit from parent Item via 2B attachments",
 "orphan cleanup on item retirement","channel may override","ItemImage primary then ImagePath fallback",
 "write both during coexistence","seed a primary ItemImage from ImagePath",
 "existing storefront and list rendering must be pixel-identical"),
("IM-02","gallery images","ItemImage","ItemImage - unchanged","non-primary rows ordered",
 "inherit from parent","cleanup with the item","channel may select a subset",
 "ItemImage","ItemImage","none needed","existing galleries must render unchanged"),
("IM-03","storefront hover image","Item.StoreHoverImage","Commerce Presentation media",
 "channel-scoped","channel-level authorization (none exists today)","with the presentation row",
 "per channel","Store* field then presentation","write both","additive presentation row",
 "storefront rendering must be unchanged"),
("IM-04","documents on an item","NOT MODELLED","2B unified attachments",
 "n/a","inherit from parent Item - authorizing endpoint, never a guessable static path",
 "retention policy","not channel-specific","n/a","new only","additive with 2B",
 "no existing behaviour to preserve"),
]

def emit(name, fields, rows):
    p = os.path.join(EV, name)
    with open(p, "w", encoding="utf-8", newline="") as fh:
        w = csv.writer(fh, quoting=csv.QUOTE_ALL)
        w.writerow(fields)
        for row in rows:
            assert len(row) == len(fields), f"{name}: {row[0]} has {len(row)} of {len(fields)}"
            w.writerow(row)
    got = list(csv.DictReader(open(p, encoding="utf-8")))
    ids = [x[fields[0]] for x in got]
    assert len(set(ids)) == len(ids), f"{name}: duplicate ids"
    print(f"{name}: {len(got)} rows | unique ids: True | empty: {sum(1 for x in got for k in fields if not x[k])}")
    return got

s = emit("Stage-002-Master-Data-Source-Inventory.csv", F, SRC)
emit("Stage-002-Barcode-Source-Matrix.csv", BF, BC)
emit("Stage-002-Image-and-Media-Source-Matrix.csv", IF, IM)
print()
print("classification:", dict(collections.Counter(x["classification"] for x in s)))
