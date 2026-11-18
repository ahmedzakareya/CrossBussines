// Storekeeper mode models for /api/store (warehouses, the blind count sheet, the posting result).

class StoreWarehouse {
  final int id;
  final String code, name;
  StoreWarehouse({required this.id, required this.code, required this.name});
  factory StoreWarehouse.fromJson(Map<String, dynamic> j) =>
      StoreWarehouse(id: j['id'] ?? 0, code: j['code']?.toString() ?? '', name: j['name']?.toString() ?? '');
  String get label => name.isEmpty ? code : '$name · $code';
}

class CountSheetItem {
  final int id;
  final String code, name, unit;
  final List<String> barcodes;
  final bool batchTracked;
  CountSheetItem({required this.id, required this.code, required this.name, required this.unit, required this.barcodes, required this.batchTracked});
  factory CountSheetItem.fromJson(Map<String, dynamic> j) => CountSheetItem(
        id: j['id'] ?? 0,
        code: j['code']?.toString() ?? '',
        name: j['name']?.toString() ?? '',
        unit: j['unit']?.toString() ?? '',
        barcodes: (j['barcodes'] as List? ?? []).map((e) => e.toString()).toList(),
        batchTracked: j['batchTracked'] == true,
      );
  bool matches(String q) {
    final t = q.trim().toLowerCase();
    return t.isNotEmpty && (code.toLowerCase() == t || barcodes.any((b) => b.toLowerCase() == t));
  }
}

class VarianceLine {
  final String name;
  final double book, counted, diff, diffValue;
  VarianceLine({required this.name, required this.book, required this.counted, required this.diff, required this.diffValue});
  factory VarianceLine.fromJson(Map<String, dynamic> j) => VarianceLine(
        name: j['name']?.toString() ?? '',
        book: (j['book'] as num?)?.toDouble() ?? 0,
        counted: (j['counted'] as num?)?.toDouble() ?? 0,
        diff: (j['diff'] as num?)?.toDouble() ?? 0,
        diffValue: (j['diffValue'] as num?)?.toDouble() ?? 0,
      );
}

/// What the server did with a count or a transfer: posted (with its number), sent for approval, or refused.
class StoreResult {
  final bool ok, pendingApproval;
  final String? message, docNo;
  final double totalAdjValue;
  final List<VarianceLine> lines;
  StoreResult({required this.ok, this.pendingApproval = false, this.message, this.docNo, this.totalAdjValue = 0, this.lines = const []});
  factory StoreResult.fromJson(Map<String, dynamic> j) => StoreResult(
        ok: j['success'] == true,
        pendingApproval: j['pendingApproval'] == true,
        message: j['message']?.toString(),
        docNo: (j['countNo'] ?? j['transferNo'])?.toString(),
        totalAdjValue: (j['totalAdjValue'] as num?)?.toDouble() ?? 0,
        lines: (j['lines'] as List? ?? []).map((e) => VarianceLine.fromJson(e as Map<String, dynamic>)).toList(),
      );
}

/// An approved purchase order still awaiting goods for a warehouse (GET /api/store/receipts).
class OpenPurchaseOrder {
  final int id, lines;
  final String orderNo, vendor;
  final DateTime? expectedDate;
  final bool partly;
  OpenPurchaseOrder({required this.id, required this.lines, required this.orderNo, required this.vendor, this.expectedDate, this.partly = false});
  factory OpenPurchaseOrder.fromJson(Map<String, dynamic> j) => OpenPurchaseOrder(
        id: j['id'] ?? 0, lines: j['lines'] ?? 0, orderNo: j['orderNo']?.toString() ?? '', vendor: j['vendor']?.toString() ?? '',
        expectedDate: j['expectedDate'] == null ? null : DateTime.tryParse(j['expectedDate'].toString()), partly: j['partly'] == true);
}

/// One order line still awaiting goods; [needsDetails] = tracked by batch / expiry / serial, so it is received on the web.
class ReceiptLine {
  final int lineId, itemId;
  final String code, name;
  final double ordered, received, remaining;
  final List<String> barcodes;
  final bool needsDetails;
  ReceiptLine({required this.lineId, required this.itemId, required this.code, required this.name, required this.ordered, required this.received, required this.remaining, this.barcodes = const [], this.needsDetails = false});
  factory ReceiptLine.fromJson(Map<String, dynamic> j) => ReceiptLine(
        lineId: j['lineId'] ?? 0, itemId: j['itemId'] ?? 0, code: j['code']?.toString() ?? '', name: j['name']?.toString() ?? '',
        ordered: (j['ordered'] as num?)?.toDouble() ?? 0, received: (j['received'] as num?)?.toDouble() ?? 0, remaining: (j['remaining'] as num?)?.toDouble() ?? 0,
        barcodes: [for (final b in (j['barcodes'] as List? ?? [])) b.toString()], needsDetails: j['needsDetails'] == true);
  bool matches(String q) { final t = q.trim().toLowerCase(); return t.isNotEmpty && (code.toLowerCase() == t || barcodes.any((b) => b.toLowerCase() == t)); }
}
