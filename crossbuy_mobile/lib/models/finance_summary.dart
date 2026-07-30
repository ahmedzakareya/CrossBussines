// Finance dashboard summary for the mobile app (read-only).
class RecentEntry {
  final int id;
  final String? entryNo;
  final DateTime? entryDate;
  final String? journalType;
  final String? description;
  final double amount;

  RecentEntry({
    required this.id,
    this.entryNo,
    this.entryDate,
    this.journalType,
    this.description,
    required this.amount,
  });

  factory RecentEntry.fromJson(Map<String, dynamic> j) => RecentEntry(
        id: j['id'] ?? 0,
        entryNo: j['entryNo'],
        entryDate: j['entryDate'] != null ? DateTime.tryParse(j['entryDate'].toString()) : null,
        journalType: j['journalType'],
        description: j['description'],
        amount: (j['amount'] as num?)?.toDouble() ?? 0,
      );
}

class FinanceSummary {
  final DateTime? asOf;
  final double cash;
  final double arTotal;
  final double apTotal;
  final double revenue;
  final double expense;
  final double netResult;
  final int customers;
  final int vendors;
  final List<RecentEntry> recent;

  FinanceSummary({
    this.asOf,
    required this.cash,
    required this.arTotal,
    required this.apTotal,
    required this.revenue,
    required this.expense,
    required this.netResult,
    required this.customers,
    required this.vendors,
    required this.recent,
  });

  factory FinanceSummary.fromJson(Map<String, dynamic> j) => FinanceSummary(
        asOf: j['asOf'] != null ? DateTime.tryParse(j['asOf'].toString()) : null,
        cash: (j['cash'] as num?)?.toDouble() ?? 0,
        arTotal: (j['arTotal'] as num?)?.toDouble() ?? 0,
        apTotal: (j['apTotal'] as num?)?.toDouble() ?? 0,
        revenue: (j['revenue'] as num?)?.toDouble() ?? 0,
        expense: (j['expense'] as num?)?.toDouble() ?? 0,
        netResult: (j['netResult'] as num?)?.toDouble() ?? 0,
        customers: j['customers'] ?? 0,
        vendors: j['vendors'] ?? 0,
        recent: (j['recent'] as List? ?? [])
            .map((e) => RecentEntry.fromJson(e as Map<String, dynamic>))
            .toList(),
      );
}
