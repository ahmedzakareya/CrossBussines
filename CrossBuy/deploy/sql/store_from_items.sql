-- E-commerce storefront (DISPLAY ONLY) — catalog sourced from the REAL inventory Items + ItemCategories.
-- Additive & idempotent. Adds nullable storefront-display columns, then seeds demo categories + products.
-- NO stock, NO GL, NO transaction — these are item/category MASTER rows only (creating a master posts nothing).
-- Products = Items with ItemCode 'STORE-%'; featured categories = ItemCategories carrying a StoreIcon. Demo data = temporary.

-- 1) display columns on Item (nullable, additive)
IF COL_LENGTH('Items','StoreOldPrice')   IS NULL ALTER TABLE Items ADD StoreOldPrice DECIMAL(10,2) NULL;
IF COL_LENGTH('Items','StoreBadge')      IS NULL ALTER TABLE Items ADD StoreBadge NVARCHAR(20) NULL;
IF COL_LENGTH('Items','StoreRating')     IS NULL ALTER TABLE Items ADD StoreRating DECIMAL(3,2) NULL;
IF COL_LENGTH('Items','StoreVendor')     IS NULL ALTER TABLE Items ADD StoreVendor NVARCHAR(120) NULL;
IF COL_LENGTH('Items','StoreHoverImage') IS NULL ALTER TABLE Items ADD StoreHoverImage NVARCHAR(300) NULL;
GO
-- 2) storefront columns on ItemCategory (nullable, additive)
IF COL_LENGTH('ItemCategories','StoreIcon')       IS NULL ALTER TABLE ItemCategories ADD StoreIcon NVARCHAR(300) NULL;
IF COL_LENGTH('ItemCategories','StoreItemsCount') IS NULL ALTER TABLE ItemCategories ADD StoreItemsCount INT NULL;
GO

-- 3) seed categories (only if none) — 11 featured (with icon+count) + 5 product-tag categories. GL accounts left null (no txn).
IF NOT EXISTS (SELECT 1 FROM ItemCategories WHERE Code LIKE 'STORE-%')
INSERT INTO ItemCategories (CompanyID, Code, Name, NameEn, Kind, IsActive, CreatedAt, StoreIcon, StoreItemsCount) VALUES
 (1,'STORE-C01',N'Cake & Milk',  N'Cake & Milk',  'Category',1,SYSUTCDATETIME(),N'/assets/imgs/shop/cat-13.png',26),
 (1,'STORE-C02',N'Coffes & Teas',N'Coffes & Teas','Category',1,SYSUTCDATETIME(),N'/assets/imgs/shop/cat-12.png',28),
 (1,'STORE-C03',N'Peach',        N'Peach',        'Category',1,SYSUTCDATETIME(),N'/assets/imgs/shop/cat-11.png',14),
 (1,'STORE-C04',N'Red Apple',    N'Red Apple',    'Category',1,SYSUTCDATETIME(),N'/assets/imgs/shop/cat-9.png', 54),
 (1,'STORE-C05',N'Snack',        N'Snack',        'Category',1,SYSUTCDATETIME(),N'/assets/imgs/shop/cat-3.png', 56),
 (1,'STORE-C06',N'Vegetables',   N'Vegetables',   'Category',1,SYSUTCDATETIME(),N'/assets/imgs/shop/cat-1.png', 72),
 (1,'STORE-C07',N'Strawberry',   N'Strawberry',   'Category',1,SYSUTCDATETIME(),N'/assets/imgs/shop/cat-2.png', 36),
 (1,'STORE-C08',N'Black plum',   N'Black plum',   'Category',1,SYSUTCDATETIME(),N'/assets/imgs/shop/cat-4.png',123),
 (1,'STORE-C09',N'Custard apple',N'Custard apple','Category',1,SYSUTCDATETIME(),N'/assets/imgs/shop/cat-5.png', 34),
 (1,'STORE-C10',N'Coffe & Tea',  N'Coffe & Tea',  'Category',1,SYSUTCDATETIME(),N'/assets/imgs/shop/cat-14.png',89),
 (1,'STORE-C11',N'Headphone',    N'Headphone',    'Category',1,SYSUTCDATETIME(),N'/assets/imgs/shop/cat-15.png',87),
 (1,'STORE-C12',N'Hodo Foods',   N'Hodo Foods',   'Category',1,SYSUTCDATETIME(),NULL,NULL),
 (1,'STORE-C13',N'Pet Foods',    N'Pet Foods',    'Category',1,SYSUTCDATETIME(),NULL,NULL),
 (1,'STORE-C14',N'Meats',        N'Meats',        'Category',1,SYSUTCDATETIME(),NULL,NULL),
 (1,'STORE-C15',N'Cream',        N'Cream',        'Category',1,SYSUTCDATETIME(),NULL,NULL),
 (1,'STORE-C16',N'Coffes',       N'Coffes',       'Category',1,SYSUTCDATETIME(),NULL,NULL);
GO

-- 4) seed products as real Items (only if none). BaseUoMId=1 (an existing UoM). Master only — no stock/GL.
IF NOT EXISTS (SELECT 1 FROM Items WHERE ItemCode LIKE 'STORE-%')
INSERT INTO Items (CompanyID, ItemCode, Barcode, Name, NameEn, ItemCategoryId, ItemType, BaseUoMId, SalesPrice, ImagePath, StoreHoverImage, StoreOldPrice, StoreBadge, StoreRating, StoreVendor, ProductionMethod, IsActive, CreatedAt) VALUES
 (1,'STORE-0001','STORE-BC-0001',N'Seeds of Change Organic Quinoa, Brown, & Red Rice',N'Seeds of Change Organic Quinoa, Brown, & Red Rice',(SELECT ID FROM ItemCategories WHERE Code='STORE-C05'),'Stockable',1,28.85,N'/assets/imgs/shop/product-1-1.jpg',N'/assets/imgs/shop/product-1-2.jpg',32.80,N'Hot',4.50,N'NestFood','OrderBased',1,SYSUTCDATETIME()),
 (1,'STORE-0002','STORE-BC-0002',N'All Natural Italian-Style Chicken Meatballs',N'All Natural Italian-Style Chicken Meatballs',(SELECT ID FROM ItemCategories WHERE Code='STORE-C12'),'Stockable',1,52.85,N'/assets/imgs/shop/product-2-1.jpg',N'/assets/imgs/shop/product-2-2.jpg',55.80,N'Sale',4.00,N'Stouffer','OrderBased',1,SYSUTCDATETIME()),
 (1,'STORE-0003','STORE-BC-0003',N'Angie''s Boomchickapop Sweet & Salty Kettle Corn',N'Angie''s Boomchickapop Sweet & Salty Kettle Corn',(SELECT ID FROM ItemCategories WHERE Code='STORE-C05'),'Stockable',1,48.85,N'/assets/imgs/shop/product-3-1.jpg',N'/assets/imgs/shop/product-3-2.jpg',52.80,N'New',4.25,N'StarKist','OrderBased',1,SYSUTCDATETIME()),
 (1,'STORE-0004','STORE-BC-0004',N'Foster Farms Takeout Crispy Classic Buffalo Wings',N'Foster Farms Takeout Crispy Classic Buffalo Wings',(SELECT ID FROM ItemCategories WHERE Code='STORE-C06'),'Stockable',1,17.85,N'/assets/imgs/shop/product-4-1.jpg',N'/assets/imgs/shop/product-4-2.jpg',19.80,N'Best',4.50,N'NestFood','OrderBased',1,SYSUTCDATETIME()),
 (1,'STORE-0005','STORE-BC-0005',N'Blue Diamond Almonds Lightly Salted Vegetables',N'Blue Diamond Almonds Lightly Salted Vegetables',(SELECT ID FROM ItemCategories WHERE Code='STORE-C13'),'Stockable',1,23.85,N'/assets/imgs/shop/product-5-1.jpg',N'/assets/imgs/shop/product-5-2.jpg',25.80,N'Sale',4.50,N'NestFood','OrderBased',1,SYSUTCDATETIME()),
 (1,'STORE-0006','STORE-BC-0006',N'Chobani Complete Vanilla Greek Yogurt',N'Chobani Complete Vanilla Greek Yogurt',(SELECT ID FROM ItemCategories WHERE Code='STORE-C12'),'Stockable',1,54.85,N'/assets/imgs/shop/product-6-1.jpg',N'/assets/imgs/shop/product-6-2.jpg',55.80,N'Hot',4.50,N'NestFood','OrderBased',1,SYSUTCDATETIME()),
 (1,'STORE-0007','STORE-BC-0007',N'Canada Dry Ginger Ale - 2 L Bottle - 200ml - 400g',N'Canada Dry Ginger Ale - 2 L Bottle - 200ml - 400g',(SELECT ID FROM ItemCategories WHERE Code='STORE-C14'),'Stockable',1,32.85,N'/assets/imgs/shop/product-7-1.jpg',N'/assets/imgs/shop/product-7-2.jpg',33.80,N'Hot',4.50,N'NestFood','OrderBased',1,SYSUTCDATETIME()),
 (1,'STORE-0008','STORE-BC-0008',N'Encore Seafoods Stuffed Alaskan Salmon',N'Encore Seafoods Stuffed Alaskan Salmon',(SELECT ID FROM ItemCategories WHERE Code='STORE-C05'),'Stockable',1,35.85,N'/assets/imgs/shop/product-8-1.jpg',N'/assets/imgs/shop/product-8-2.jpg',37.80,N'Sale',4.50,N'NestFood','OrderBased',1,SYSUTCDATETIME()),
 (1,'STORE-0009','STORE-BC-0009',N'Gorton''s Beer Battered Fish Fillets with soft paper',N'Gorton''s Beer Battered Fish Fillets with soft paper',(SELECT ID FROM ItemCategories WHERE Code='STORE-C16'),'Stockable',1,23.85,N'/assets/imgs/shop/product-9-1.jpg',N'/assets/imgs/shop/product-9-2.jpg',25.80,N'New',4.50,N'NestFood','OrderBased',1,SYSUTCDATETIME()),
 (1,'STORE-0010','STORE-BC-0010',N'Haagen-Dazs Caramel Cone Ice Cream Ketchup',N'Haagen-Dazs Caramel Cone Ice Cream Ketchup',(SELECT ID FROM ItemCategories WHERE Code='STORE-C15'),'Stockable',1,22.85,N'/assets/imgs/shop/product-10-1.jpg',N'/assets/imgs/shop/product-10-2.jpg',24.80,N'Best',2.50,N'NestFood','OrderBased',1,SYSUTCDATETIME());
GO
