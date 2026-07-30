-- ============================================================================
-- Pricing 2C — Cost-plus price list lines
-- Idempotent. A price-list line with PricingMode = 'CostPlus' computes the unit price
-- as resolved cost × (1 + MarkupPercent/100) in the functional currency, converted to
-- the document currency and rounded to its decimals (dynamic — moves with the cost).
-- ============================================================================

IF COL_LENGTH('PriceListLines','PricingMode') IS NULL
    ALTER TABLE PriceListLines ADD PricingMode nvarchar(10) NOT NULL CONSTRAINT DF_PriceListLines_PricingMode DEFAULT('Fixed');
GO
IF COL_LENGTH('PriceListLines','MarkupPercent') IS NULL
    ALTER TABLE PriceListLines ADD MarkupPercent decimal(19,4) NOT NULL CONSTRAINT DF_PriceListLines_Markup DEFAULT(0);
GO
