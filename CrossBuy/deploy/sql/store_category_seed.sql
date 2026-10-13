-- E-commerce storefront: fill the empty FEATURED categories with demo products so a category page is never empty.
-- DISPLAY ONLY (Item master rows, no stock/GL/movement -> inventory & GL untouched, inv-test-integrity stays 0).
-- Idempotent: each row inserts only if its ItemCode is missing. Images reuse the Nest theme's product-*.jpg (paths only).
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

INSERT INTO Items (CompanyID, ItemCode, Barcode, Name, NameEn, ItemCategoryId, ItemType, BaseUoMId, SalesPrice, ImagePath, StoreHoverImage, StoreOldPrice, StoreBadge, StoreRating, StoreVendor, ProductionMethod, IsActive, CreatedAt)
SELECT 1, v.code, v.bc, v.nm, v.nm, c.ID, 'Stockable', 1, v.price,
       N'/assets/imgs/shop/product-' + CAST(v.img AS varchar(2)) + N'-1.jpg',
       N'/assets/imgs/shop/product-' + CAST(v.img AS varchar(2)) + N'-2.jpg',
       v.oldp, v.badge, v.rating, v.vendor, 'OrderBased', 1, SYSUTCDATETIME()
FROM (VALUES
  -- Cake & Milk (STORE-C01)
  ('STORE-0011','STORE-BC-0011',N'Fresh Whole Milk 1L',      'STORE-C01',1 ,12.50,14.00,N'Hot' ,4.50,N'NestFood'),
  ('STORE-0012','STORE-BC-0012',N'Chocolate Layer Cake',     'STORE-C01',2 ,34.00,39.00,N'Sale',4.00,N'Bakery Co'),
  ('STORE-0013','STORE-BC-0013',N'Vanilla Cupcakes 6-pack',  'STORE-C01',3 ,18.75,21.00,N'New' ,4.25,N'Bakery Co'),
  -- Coffes & Teas (STORE-C02)
  ('STORE-0014','STORE-BC-0014',N'Arabica Ground Coffee',    'STORE-C02',4 ,45.00,52.00,N'Hot' ,4.75,N'Old El Paso'),
  ('STORE-0015','STORE-BC-0015',N'Green Tea Bags 25',        'STORE-C02',5 ,15.90,18.00,N'Sale',4.10,N'Lipton'),
  ('STORE-0016','STORE-BC-0016',N'Cappuccino Mix 10 Sachets','STORE-C02',6 ,22.40,25.00,N'Best',4.30,N'NestFood'),
  -- Peach (STORE-C03)
  ('STORE-0017','STORE-BC-0017',N'Fresh Yellow Peaches 1kg', 'STORE-C03',7 ,19.50,23.00,N'New' ,4.20,N'FarmFresh'),
  ('STORE-0018','STORE-BC-0018',N'Peach Nectar Juice 1L',    'STORE-C03',8 ,13.20,15.00,N'Sale',3.90,N'NestFood'),
  ('STORE-0019','STORE-BC-0019',N'Canned Peach Slices',      'STORE-C03',9 ,9.80 ,11.00,N'Hot' ,4.00,N'Del Monte'),
  -- Red Apple (STORE-C04)
  ('STORE-0020','STORE-BC-0020',N'Red Delicious Apples 1kg', 'STORE-C04',10,16.00,18.50,N'Hot' ,4.60,N'FarmFresh'),
  ('STORE-0021','STORE-BC-0021',N'Organic Fuji Apples 1kg',  'STORE-C04',11,21.00,24.00,N'Best',4.70,N'FarmFresh'),
  ('STORE-0022','STORE-BC-0022',N'Fresh Apple Juice 1L',     'STORE-C04',12,12.90,14.50,N'Sale',4.10,N'NestFood'),
  -- Strawberry (STORE-C07)
  ('STORE-0023','STORE-BC-0023',N'Fresh Strawberries 500g',  'STORE-C07',13,24.50,28.00,N'New' ,4.55,N'FarmFresh'),
  ('STORE-0024','STORE-BC-0024',N'Strawberry Jam 340g',      'STORE-C07',14,17.30,19.00,N'Sale',4.20,N'Smucker'),
  ('STORE-0025','STORE-BC-0025',N'Strawberry Yogurt 4-pack', 'STORE-C07',15,20.00,22.50,N'Hot' ,4.35,N'Chobani'),
  -- Black plum (STORE-C08)
  ('STORE-0026','STORE-BC-0026',N'Fresh Black Plums 1kg',    'STORE-C08',16,18.90,21.00,N'Hot' ,4.15,N'FarmFresh'),
  ('STORE-0027','STORE-BC-0027',N'Dried Plums 200g',         'STORE-C08',1 ,11.50,13.00,N'New' ,4.05,N'Sunsweet'),
  ('STORE-0028','STORE-BC-0028',N'Plum Juice 1L',            'STORE-C08',2 ,13.75,15.50,N'Sale',3.95,N'NestFood'),
  -- Custard apple (STORE-C09)
  ('STORE-0029','STORE-BC-0029',N'Fresh Custard Apple 1kg',  'STORE-C09',3 ,27.00,31.00,N'Best',4.40,N'FarmFresh'),
  ('STORE-0030','STORE-BC-0030',N'Custard Apple Pulp 500g',  'STORE-C09',4 ,19.60,22.00,N'Sale',4.10,N'NestFood'),
  ('STORE-0031','STORE-BC-0031',N'Sitaphal Ice Cream 1L',    'STORE-C09',5 ,25.40,28.00,N'Hot' ,4.30,N'Kwality'),
  -- Coffe & Tea (STORE-C10)
  ('STORE-0032','STORE-BC-0032',N'Espresso Beans 500g',      'STORE-C10',6 ,49.00,55.00,N'Hot' ,4.80,N'Lavazza'),
  ('STORE-0033','STORE-BC-0033',N'Herbal Tea Assorted 20',   'STORE-C10',7 ,16.80,19.00,N'New' ,4.15,N'Twinings'),
  ('STORE-0034','STORE-BC-0034',N'Iced Coffee Can 250ml',    'STORE-C10',8 ,8.90 ,10.00,N'Sale',4.00,N'NestFood'),
  -- Headphone (STORE-C11)
  ('STORE-0035','STORE-BC-0035',N'Wireless Headphone',       'STORE-C11',9 ,189.00,220.00,N'Best',4.65,N'SoundMax'),
  ('STORE-0036','STORE-BC-0036',N'Gaming Headset Pro',       'STORE-C11',10,249.00,299.00,N'Hot' ,4.70,N'SoundMax'),
  ('STORE-0037','STORE-BC-0037',N'Earbuds Pro',              'STORE-C11',11,159.00,179.00,N'New' ,4.50,N'SoundMax')
) AS v(code, bc, nm, catcode, img, price, oldp, badge, rating, vendor)
JOIN ItemCategories c ON c.Code = v.catcode AND c.CompanyID = 1
WHERE NOT EXISTS (SELECT 1 FROM Items i WHERE i.ItemCode = v.code AND i.CompanyID = 1);

SELECT c.Name AS Category, COUNT(i.ID) AS store_items
FROM ItemCategories c LEFT JOIN Items i ON i.ItemCategoryId=c.ID AND i.ItemCode LIKE 'STORE-%'
WHERE c.StoreIcon IS NOT NULL GROUP BY c.Name ORDER BY c.Name;
