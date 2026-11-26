-- HM-12 — RETAIL PROMOTIONS for the hypermarket lane. Idempotent, additive, no data migration.
--
-- WHY A TABLE AND NOT A PRICE LIST: a promotion is a RULE over a basket, not a price on an item.
-- "Buy 2 get the cheapest free" cannot be expressed as a unit price, and the moment it is tried the
-- rule gets duplicated into every screen that prices anything. One rule store, one engine.
--
-- WHAT THE ENGINE WRITES: PosOrderLines.DiscountAmount, which already exists and is already carried
-- through RecomputeAsync into SubTotal, TaxTotal, GrandTotal, the sales invoice, AR and COGS. So no
-- posting path changes - the discount reaches the ledger by the route a manual discount already took.
--
-- NOTHING HERE IS SEEDED ON. A branch with no rows behaves exactly as it does today.

IF OBJECT_ID('PosPromotions','U') IS NULL
CREATE TABLE PosPromotions (
    ID              INT IDENTITY(1,1) PRIMARY KEY,
    CompanyId       INT            NOT NULL,
    BranchId        INT            NULL,          -- NULL = every branch of the company
    Code            NVARCHAR(40)   NOT NULL,      -- the internal tag reports filter on
    NameAr          NVARCHAR(200)  NOT NULL,
    NameEn          NVARCHAR(200)  NOT NULL,
    -- PercentOff | AmountOff | BuyXGetY | ThresholdAmount
    PromoType       NVARCHAR(24)   NOT NULL,
    TriggerQty      DECIMAL(18,3)  NOT NULL CONSTRAINT DF_PosPromotions_TriggerQty DEFAULT(0),
    RewardQty       DECIMAL(18,3)  NOT NULL CONSTRAINT DF_PosPromotions_RewardQty  DEFAULT(0),
    DiscountPct     DECIMAL(9,4)   NOT NULL CONSTRAINT DF_PosPromotions_Pct        DEFAULT(0),
    DiscountAmount  DECIMAL(18,3)  NOT NULL CONSTRAINT DF_PosPromotions_Amt        DEFAULT(0),
    MinBasket       DECIMAL(18,3)  NOT NULL CONSTRAINT DF_PosPromotions_MinBasket  DEFAULT(0),
    -- lower runs first. A FIXED order is what makes two overlapping offers reproducible.
    Priority        INT            NOT NULL CONSTRAINT DF_PosPromotions_Priority   DEFAULT(100),
    -- the stacking control: once an exclusive rule applies to a line, no further rule may touch it.
    IsExclusive     BIT            NOT NULL CONSTRAINT DF_PosPromotions_Excl       DEFAULT(0),
    -- ceiling as a percentage of the line/basket it discounts. 0 = no ceiling.
    MaxDiscountPct  DECIMAL(9,4)   NOT NULL CONSTRAINT DF_PosPromotions_MaxPct     DEFAULT(0),
    StartsAt        DATETIME2      NULL,
    EndsAt          DATETIME2      NULL,
    IsActive        BIT            NOT NULL CONSTRAINT DF_PosPromotions_Active     DEFAULT(1),
    CreatedAt       DATETIME2      NOT NULL CONSTRAINT DF_PosPromotions_Created    DEFAULT(SYSUTCDATETIME())
);
GO

-- one code per company: the tag a sales report groups by must not be ambiguous.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_PosPromotions_Code' AND object_id=OBJECT_ID('PosPromotions'))
    CREATE UNIQUE INDEX UX_PosPromotions_Code ON PosPromotions (CompanyId, Code);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_PosPromotions_Active' AND object_id=OBJECT_ID('PosPromotions'))
    CREATE INDEX IX_PosPromotions_Active ON PosPromotions (CompanyId, IsActive, Priority);
GO

-- The item membership. Role separates the two halves of a mix-and-match: an item that is a Trigger is
-- what the customer must buy; a Reward is what gets discounted. An item may be both (classic BOGO on
-- one product), and a promotion with no rows at all is BASKET-wide.
IF OBJECT_ID('PosPromotionItems','U') IS NULL
CREATE TABLE PosPromotionItems (
    ID           INT IDENTITY(1,1) PRIMARY KEY,
    PromotionId  INT          NOT NULL,
    ItemId       INT          NULL,        -- one of ItemId / CategoryId is set
    CategoryId   INT          NULL,
    Role         NVARCHAR(10) NOT NULL CONSTRAINT DF_PosPromotionItems_Role DEFAULT('Both')  -- Trigger | Reward | Both
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_PosPromotionItems_Promo' AND object_id=OBJECT_ID('PosPromotionItems'))
    CREATE INDEX IX_PosPromotionItems_Promo ON PosPromotionItems (PromotionId);
GO

-- WHAT ACTUALLY FIRED, per sold line. Written at settlement, never derived later: a promotion edited
-- or ended tomorrow must not change what yesterday's receipt says was given away.
IF OBJECT_ID('PosOrderLinePromotions','U') IS NULL
CREATE TABLE PosOrderLinePromotions (
    ID            INT IDENTITY(1,1) PRIMARY KEY,
    CompanyId     INT           NOT NULL,
    OrderId       INT           NOT NULL,
    OrderLineId   INT           NOT NULL,
    PromotionId   INT           NOT NULL,
    PromoCode     NVARCHAR(40)  NOT NULL,     -- COPIED, not joined: the rule may be renamed later
    PromoName     NVARCHAR(200) NOT NULL,
    Amount        DECIMAL(18,3) NOT NULL,     -- the discount this rule contributed to this line
    AppliedAt     DATETIME2     NOT NULL CONSTRAINT DF_PosOrderLinePromotions_At DEFAULT(SYSUTCDATETIME())
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_PosOrderLinePromotions_Order' AND object_id=OBJECT_ID('PosOrderLinePromotions'))
    CREATE INDEX IX_PosOrderLinePromotions_Order ON PosOrderLinePromotions (CompanyId, OrderId);
GO
