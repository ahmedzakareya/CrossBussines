// Sales-rep mode models for /api/rep (customer card, priced order preview, receipt accounts).

double _d(dynamic v) => (v as num?)?.toDouble() ?? 0;

/// The server stores times in UTC and sends them back without a zone ("2026-11-19T04:43:10"); read such a value as
/// UTC and show it in the phone's own time, so "arrived 07:43" is the time on the rep's watch.
DateTime? utcFromServer(dynamic v) {
  if (v == null) return null;
  final s = v.toString();
  final zoned = s.endsWith('Z') || RegExp(r'[+-]\d\d:\d\d$').hasMatch(s);
  return DateTime.tryParse(zoned ? s : '${s}Z')?.toLocal();
}

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

/// One stop of the rep's day (GET /api/rep/today).
class RepVisit {
  final int id, customerId;
  final String customer, status;   // Planned | Arrived | Done | Skipped
  final String? address, plannedTime, outcome;
  final DateTime? arrivedAt;
  final double orderTotal, collected, outstanding;
  RepVisit({required this.id, required this.customerId, required this.customer, required this.status, this.address, this.plannedTime, this.outcome, this.arrivedAt, this.orderTotal = 0, this.collected = 0, this.outstanding = 0});
  bool get open => status == 'Planned' || status == 'Arrived';
  factory RepVisit.fromJson(Map<String, dynamic> j) => RepVisit(
        id: j['id'] ?? 0, customerId: j['customerId'] ?? 0, customer: j['customer']?.toString() ?? '', status: j['status']?.toString() ?? 'Planned',
        address: j['address']?.toString(), plannedTime: j['plannedTime']?.toString(), outcome: j['outcome']?.toString(),
        arrivedAt: utcFromServer(j['arrivedAt']),
        orderTotal: _d(j['orderTotal']), collected: _d(j['collected']), outstanding: _d(j['outstanding']));
}

/// The rep's day: the route and its target, what the day's orders achieved, and the visits in order.
class RepDay {
  final String currency, rep;
  final String? routeName;
  final double target, achieved, collected;
  final int left;
  final List<RepVisit> visits;
  RepDay({required this.currency, required this.rep, this.routeName, this.target = 0, this.achieved = 0, this.collected = 0, this.left = 0, this.visits = const []});
  factory RepDay.fromJson(Map<String, dynamic> j) => RepDay(
        currency: j['currency']?.toString() ?? '', rep: j['rep']?.toString() ?? '',
        routeName: (j['route'] as Map?)?['name']?.toString(), target: _d((j['route'] as Map?)?['target']),
        achieved: _d(j['achieved']), collected: _d(j['collected']), left: j['left'] ?? 0,
        visits: [for (final v in (j['visits'] as List? ?? [])) RepVisit.fromJson(v as Map<String, dynamic>)]);
  /// The visit the rep is at, or the next one due.
  RepVisit? get current => visits.where((v) => v.status == 'Arrived').firstOrNull ?? visits.where((v) => v.status == 'Planned').firstOrNull;
}
