-- ================================================================================================
-- HM-26 — COUPONS (code-entered basket discounts with redemption limits)
--
-- WHY A NEW TABLE RATHER THAN COLUMNS ON Promotion:
--   Promotion is the PER-LINE pricing rule engine. It is applied automatically by
--   IPricingService.GetPriceAsync and folded into the unit price. A coupon is a different animal:
--   it is ENTERED by the cashier, it applies to the BASKET, and it is LIMITED by how many times it
--   may be used. Bolting a "do not auto-apply" flag and a use counter onto Promotion would make
--   every promotion query carry coupon concerns, and a coupon with no use counter is worse than no
--   coupon at all. The promotion engine is untouched by this slice.
--
-- THE MODEL IS THE INDUSTRY STANDARD ONE (Commerce Layer, Salesforce Data Cloud, and the common
-- coupon system designs all agree on its shape):
--   * a coupon row carrying a total UsageLimit and a running UsageCount
--   * a PerCustomerLimit enforced by COUNTING REDEMPTION ROWS, not by a second counter that can
--     drift from them
--   * a redemption table, one row per use, which is the audit trail and the per-customer evidence
--   * the check-and-increment done as ONE conditional UPDATE so two tills cannot both spend the
--     last use of a coupon
--
-- RE-RUN SAFE: every batch is guarded by OBJECT_ID / COL_LENGTH.
-- ================================================================================================

IF OBJECT_ID('PosCoupons','U') IS NULL
BEGIN
    CREATE TABLE PosCoupons (
        ID               INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PosCoupons PRIMARY KEY,
        CompanyID        INT            NOT NULL,
        Code             NVARCHAR(40)   NOT NULL,          -- what the cashier types; compared case-insensitively
        Name             NVARCHAR(200)  NOT NULL,
        NameEn           NVARCHAR(200)  NULL,
        DiscountType     NVARCHAR(20)   NOT NULL,          -- Percent | Amount
        Value            DECIMAL(19,4)  NOT NULL,          -- percent (0..100) or a fixed amount
        CurrencyId       INT            NULL,              -- Amount only; null = branch document currency
        MinBasket        DECIMAL(19,4)  NULL,              -- subtotal the basket must reach to qualify
        MaxDiscount      DECIMAL(19,4)  NULL,              -- cap on a Percent coupon; null = uncapped
        ValidFrom        DATETIME2      NULL,
        ValidTo          DATETIME2      NULL,
        -- NULL means UNLIMITED in both of these. Zero would mean "may never be used", which is a
        -- different statement, and collapsing the two would make an unlimited coupon unusable.
        UsageLimit       INT            NULL,              -- total redemptions allowed across everyone
        UsageCount       INT            NOT NULL CONSTRAINT DF_PosCoupons_UsageCount DEFAULT(0),
        PerCustomerLimit INT            NULL,              -- redemptions allowed per identified customer
        RequiresCustomer BIT            NOT NULL CONSTRAINT DF_PosCoupons_ReqCust DEFAULT(0),
        IsActive         BIT            NOT NULL CONSTRAINT DF_PosCoupons_IsActive DEFAULT(1),
        CreatedAt        DATETIME2      NULL,
        CreatedBy        INT            NULL
    );
END
GO

-- One code per company. The filtered-free unique index is what makes "enter a code" unambiguous.
IF OBJECT_ID('PosCoupons','U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PosCoupons_Company_Code' AND object_id = OBJECT_ID('PosCoupons','U'))
    CREATE UNIQUE INDEX UX_PosCoupons_Company_Code ON PosCoupons (CompanyID, Code);
GO

IF OBJECT_ID('PosCouponRedemptions','U') IS NULL
BEGIN
    CREATE TABLE PosCouponRedemptions (
        ID                   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PosCouponRedemptions PRIMARY KEY,
        CompanyID            INT           NOT NULL,
        CouponId             INT           NOT NULL,
        OrderId              INT           NOT NULL,
        InvoiceId            INT           NULL,          -- filled at settlement; null while the basket is open
        CustomerId           INT           NULL,          -- null = walk-in; a per-customer limit cannot apply
        DiscountAmount       DECIMAL(19,4) NOT NULL,
        RedeemedAt           DATETIME2     NOT NULL,
        RedeemedByEmployeeId INT           NULL
    );
END
GO

-- ONE COUPON PER ORDER, enforced by the database rather than by remembering to check. Two coupons
-- on one basket is a policy question nobody has answered, and the safe default is that it cannot
-- happen by accident.
IF OBJECT_ID('PosCouponRedemptions','U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PosCouponRedemptions_Order' AND object_id = OBJECT_ID('PosCouponRedemptions','U'))
    CREATE UNIQUE INDEX UX_PosCouponRedemptions_Order ON PosCouponRedemptions (OrderId);
GO

-- The per-customer limit is answered by counting rows on this index, so it is the one that matters.
IF OBJECT_ID('PosCouponRedemptions','U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PosCouponRedemptions_Coupon_Customer' AND object_id = OBJECT_ID('PosCouponRedemptions','U'))
    CREATE INDEX IX_PosCouponRedemptions_Coupon_Customer ON PosCouponRedemptions (CouponId, CustomerId);
GO

-- Which coupon is on the basket right now. Nullable: almost no basket has one.
IF OBJECT_ID('PosOrders','U') IS NOT NULL AND COL_LENGTH('PosOrders','CouponId') IS NULL
    ALTER TABLE PosOrders ADD CouponId INT NULL;
GO

-- THE COUPON'S SHARE OF EACH LINE, kept SEPARATE from DiscountAmount on purpose.
--
-- DiscountAmount already carries whatever the price list and the promotion engine decided. If the
-- coupon were added into it, removing a mis-keyed coupon would have no way to tell its own
-- contribution from the promotional one, and the line would be restored to the wrong price.
--
-- Keeping it in its own column also means RecomputeAsync needs one extra subtrahend and NOTHING
-- else: tax is computed from LineTotal AFTER discounts, so the basket's tax falls out correctly
-- without touching the tax path at all.
IF OBJECT_ID('PosOrderLines','U') IS NOT NULL AND COL_LENGTH('PosOrderLines','CouponDiscount') IS NULL
    ALTER TABLE PosOrderLines ADD CouponDiscount DECIMAL(19,4) NOT NULL CONSTRAINT DF_PosOrderLines_CouponDiscount DEFAULT(0);
GO
