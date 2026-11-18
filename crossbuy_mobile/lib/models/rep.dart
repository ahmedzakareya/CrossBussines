// Sales-rep mode models for /api/rep (customer card, priced order preview, receipt accounts).

double _d(dynamic v) => (v as num?)?.toDouble() ?? 0;

class RepCustomer {
  final int id;
  final String name;
  final String? phone, address;
  final double? creditLimit, headroom;
  final double outstanding;
  RepCustomer({required this.id, required this.name, this.phone, this.address, this.creditLimit, this.headroom, required this.outstanding});
  factory RepCustomer.fromJson(Map<String, dynamic> j) => RepCustomer(
        id: j['id'] ?? 0,
        name: j['name']?.toString() ?? '',
        phone: j['phone']?.toString(),
        address: j['address']?.toString(),
        creditLimit: (j['creditLimit'] as num?)?.toDouble(),
        headroom: (j['headroom'] as num?)?.toDouble(),
        outstanding: _d(j['outstanding']),
      );
}

/// An invoice still open on the customer's account: what is left on it after receipts are applied oldest first.
class OpenInvoice {
  final int id;
  final String invoiceNo;
  final DateTime? dueDate;
  final double open;
  final int overdueDays;
  OpenInvoice({required this.id, required this.invoiceNo, this.dueDate, required this.open, this.overdueDays = 0});
  factory OpenInvoice.fromJson(Map<String, dynamic> j) => OpenInvoice(
        id: j['id'] ?? 0,
        invoiceNo: j['invoiceNo']?.toString() ?? '',
        dueDate: j['dueDate'] == null ? null : DateTime.tryParse(j['dueDate'].toString()),
        open: _d(j['open']),
        overdueDays: j['overdueDays'] ?? 0,
      );
}

class PricedLine {
  final int itemId;
  final String name, code, priceSource;
  final double qty, unitPrice, discount, taxRate, total;
  PricedLine({required this.itemId, required this.name, required this.code, required this.priceSource, required this.qty, required this.unitPrice, required this.discount, required this.taxRate, required this.total});
  factory PricedLine.fromJson(Map<String, dynamic> j) => PricedLine(
        itemId: j['itemId'] ?? 0,
        name: j['name']?.toString() ?? '',
        code: j['code']?.toString() ?? '',
        priceSource: j['priceSource']?.toString() ?? '',
        qty: _d(j['qty']), unitPrice: _d(j['unitPrice']), discount: _d(j['discount']), taxRate: _d(j['taxRate']), total: _d(j['total']),
      );
}

class OrderPreview {
  final bool ok, overLimit;
  final String? message, warning;
  final List<PricedLine> lines;
  final double total, outstanding;
  final double? creditLimit;
  OrderPreview({required this.ok, required this.overLimit, this.message, this.warning, required this.lines, required this.total, required this.outstanding, this.creditLimit});
  factory OrderPreview.fromJson(Map<String, dynamic> j) => OrderPreview(
        ok: j['success'] == true,
        overLimit: j['overLimit'] == true,
        message: j['message']?.toString(),
        warning: j['warning']?.toString(),
        lines: (j['lines'] as List? ?? []).map((e) => PricedLine.fromJson(e as Map<String, dynamic>)).toList(),
        total: _d(j['total']),
        outstanding: _d(j['outstanding']),
        creditLimit: (j['creditLimit'] as num?)?.toDouble(),
      );
}

class CashAccount {
  final int id;
  final String code, name;
  CashAccount({required this.id, required this.code, required this.name});
  factory CashAccount.fromJson(Map<String, dynamic> j) => CashAccount(id: j['id'] ?? 0, code: j['code']?.toString() ?? '', name: j['name']?.toString() ?? '');
}
