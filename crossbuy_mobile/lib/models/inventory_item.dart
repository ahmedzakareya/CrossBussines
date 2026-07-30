// Inventory models for the mobile app (read-only: item list + barcode scan with on-hand).

class InventoryItem {
  final int id;
  final String code;
  final String? barcode;
  final String name;
  final String? image;
  final String? type;
  final bool isComposite;
  final double salesPrice;
  final double onHand;

  InventoryItem({
    required this.id,
    required this.code,
    this.barcode,
    required this.name,
    this.image,
    this.type,
    this.isComposite = false,
    this.salesPrice = 0,
    this.onHand = 0,
  });

  factory InventoryItem.fromJson(Map<String, dynamic> j) => InventoryItem(
        id: j['id'] ?? 0,
        code: j['code']?.toString() ?? '',
        barcode: j['barcode']?.toString(),
        name: j['name']?.toString() ?? '',
        image: j['image']?.toString(),
        type: j['type']?.toString(),
        isComposite: j['isComposite'] == true,
        salesPrice: (j['salesPrice'] as num?)?.toDouble() ?? 0,
        onHand: (j['onHand'] as num?)?.toDouble() ?? 0,
      );
}

class WarehouseStock {
  final String warehouse;
  final double qty;
  final double avgCost;
  final double value;
  WarehouseStock({required this.warehouse, required this.qty, required this.avgCost, required this.value});
  factory WarehouseStock.fromJson(Map<String, dynamic> j) => WarehouseStock(
        warehouse: j['warehouse']?.toString() ?? '',
        qty: (j['qty'] as num?)?.toDouble() ?? 0,
        avgCost: (j['avgCost'] as num?)?.toDouble() ?? 0,
        value: (j['value'] as num?)?.toDouble() ?? 0,
      );
}

class ScanResult {
  final bool ok;
  final int id;
  final String code;
  final String name;
  final String? image;
  final bool isComposite;
  final String? compositeType;
  final double salesPrice;
  final double totalOnHand;
  final double totalValue;
  final List<WarehouseStock> byWarehouse;
  final List<String> units;
  final List<String> components;

  ScanResult({
    required this.ok,
    this.id = 0,
    this.code = '',
    this.name = '',
    this.image,
    this.isComposite = false,
    this.compositeType,
    this.salesPrice = 0,
    this.totalOnHand = 0,
    this.totalValue = 0,
    this.byWarehouse = const [],
    this.units = const [],
    this.components = const [],
  });

  factory ScanResult.fromJson(Map<String, dynamic> j) => ScanResult(
        ok: j['ok'] == true,
        id: j['id'] ?? 0,
        code: j['code']?.toString() ?? '',
        name: j['name']?.toString() ?? '',
        image: j['image']?.toString(),
        isComposite: j['isComposite'] == true,
        compositeType: j['compositeType']?.toString(),
        salesPrice: (j['salesPrice'] as num?)?.toDouble() ?? 0,
        totalOnHand: (j['totalOnHand'] as num?)?.toDouble() ?? 0,
        totalValue: (j['totalValue'] as num?)?.toDouble() ?? 0,
        byWarehouse: ((j['byWarehouse'] as List?) ?? [])
            .map((e) => WarehouseStock.fromJson(e as Map<String, dynamic>))
            .toList(),
        units: ((j['units'] as List?) ?? []).map((e) => (e['name'] ?? '').toString()).toList(),
        components: ((j['components'] as List?) ?? []).map((e) => (e['name'] ?? '').toString()).toList(),
      );
}
