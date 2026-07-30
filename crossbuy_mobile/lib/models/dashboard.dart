// People dashboard: leave balances per type + self-service stats.
class LeaveBalance {
  final int leaveTypeId;
  final String? nameAr;
  final String? nameEn;
  final int entitlement;
  final int used;
  final int remaining;

  LeaveBalance({
    required this.leaveTypeId,
    this.nameAr,
    this.nameEn,
    required this.entitlement,
    required this.used,
    required this.remaining,
  });

  factory LeaveBalance.fromJson(Map<String, dynamic> j) => LeaveBalance(
        leaveTypeId: j['leaveTypeId'] ?? 0,
        nameAr: j['nameAr'],
        nameEn: j['nameEn'],
        entitlement: j['entitlement'] ?? 0,
        used: j['used'] ?? 0,
        remaining: j['remaining'] ?? 0,
      );
}

class TypeSlice {
  final int leaveTypeId;
  final String? nameAr;
  final String? nameEn;
  final int count;
  final int pct;
  TypeSlice({required this.leaveTypeId, this.nameAr, this.nameEn, required this.count, required this.pct});
  factory TypeSlice.fromJson(Map<String, dynamic> j) => TypeSlice(
        leaveTypeId: j['leaveTypeId'] ?? 0,
        nameAr: j['nameAr'],
        nameEn: j['nameEn'],
        count: j['count'] ?? 0,
        pct: j['pct'] ?? 0,
      );
}

class MonthPoint {
  final int year;
  final int month;
  final int count;
  MonthPoint({required this.year, required this.month, required this.count});
  factory MonthPoint.fromJson(Map<String, dynamic> j) =>
      MonthPoint(year: j['year'] ?? 0, month: j['month'] ?? 0, count: j['count'] ?? 0);
}

class DashboardData {
  final bool hasPolicy;
  final String? policyAr;
  final String? policyEn;
  final int myPending;
  final int myApprovedThisYear;
  final int pendingApprovals;
  final int teamSize;
  final List<LeaveBalance> balances;
  final int total;
  final int approved;
  final int pending;
  final int rejected;
  final int thisMonth;
  final int lastMonth;
  final int pctChange;
  final List<TypeSlice> byType;
  final List<MonthPoint> monthly;

  DashboardData({
    required this.hasPolicy,
    this.policyAr,
    this.policyEn,
    required this.myPending,
    required this.myApprovedThisYear,
    required this.pendingApprovals,
    required this.teamSize,
    required this.balances,
    this.total = 0,
    this.approved = 0,
    this.pending = 0,
    this.rejected = 0,
    this.thisMonth = 0,
    this.lastMonth = 0,
    this.pctChange = 0,
    this.byType = const [],
    this.monthly = const [],
  });

  factory DashboardData.fromJson(Map<String, dynamic> j) => DashboardData(
        hasPolicy: j['hasPolicy'] ?? false,
        policyAr: j['policyAr'],
        policyEn: j['policyEn'],
        myPending: j['myPending'] ?? 0,
        myApprovedThisYear: j['myApprovedThisYear'] ?? 0,
        pendingApprovals: j['pendingApprovals'] ?? 0,
        teamSize: j['teamSize'] ?? 0,
        balances: (j['balances'] as List? ?? [])
            .map((e) => LeaveBalance.fromJson(e as Map<String, dynamic>))
            .toList(),
        total: j['total'] ?? 0,
        approved: j['approved'] ?? 0,
        pending: j['pending'] ?? 0,
        rejected: j['rejected'] ?? 0,
        thisMonth: j['thisMonth'] ?? 0,
        lastMonth: j['lastMonth'] ?? 0,
        pctChange: j['pctChange'] ?? 0,
        byType: (j['byType'] as List? ?? []).map((e) => TypeSlice.fromJson(e as Map<String, dynamic>)).toList(),
        monthly: (j['monthly'] as List? ?? []).map((e) => MonthPoint.fromJson(e as Map<String, dynamic>)).toList(),
      );
}
