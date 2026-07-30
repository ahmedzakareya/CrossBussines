// Leave types and leave requests for the self-service leave module.

class LeaveType {
  final int id;
  final String? nameAr;
  final String? nameEn;

  LeaveType({required this.id, this.nameAr, this.nameEn});

  factory LeaveType.fromJson(Map<String, dynamic> j) => LeaveType(
        id: j['id'] ?? 0,
        nameAr: j['nameAr'],
        nameEn: j['nameEn'],
      );
}

class ApprovalStep {
  final int level;
  final int status; // 0 pending, 1 approved, 2 rejected
  final String? statusAr;
  final String? statusEn;
  final String? approverAr;
  final String? approverEn;

  ApprovalStep({
    required this.level,
    required this.status,
    this.statusAr,
    this.statusEn,
    this.approverAr,
    this.approverEn,
  });

  factory ApprovalStep.fromJson(Map<String, dynamic> j) => ApprovalStep(
        level: j['level'] ?? 0,
        status: j['status'] ?? 0,
        statusAr: j['statusAr'],
        statusEn: j['statusEn'],
        approverAr: j['approverAr'],
        approverEn: j['approverEn'],
      );
}

class LeaveRequest {
  final int id;
  final int employeeId;
  final String? employeeNameAr;
  final String? employeeNameEn;
  final int leaveTypeId;
  final String? leaveTypeAr;
  final String? leaveTypeEn;
  final DateTime? startDate;
  final DateTime? endDate;
  final int days;
  final String? reason;
  final int status; // 0 pending, 1 approved, 2 rejected
  final String? statusAr;
  final String? statusEn;
  final String? decisionNote;
  final DateTime? createdAt;
  final int currentLevel;
  final int totalLevels;
  final List<ApprovalStep> approvals;

  LeaveRequest({
    required this.id,
    required this.employeeId,
    this.employeeNameAr,
    this.employeeNameEn,
    required this.leaveTypeId,
    this.leaveTypeAr,
    this.leaveTypeEn,
    this.startDate,
    this.endDate,
    required this.days,
    this.reason,
    required this.status,
    this.statusAr,
    this.statusEn,
    this.decisionNote,
    this.createdAt,
    this.currentLevel = 0,
    this.totalLevels = 0,
    this.approvals = const [],
  });

  static DateTime? _d(dynamic v) => v == null ? null : DateTime.tryParse(v.toString());

  factory LeaveRequest.fromJson(Map<String, dynamic> j) => LeaveRequest(
        id: j['id'] ?? 0,
        employeeId: j['employeeId'] ?? 0,
        employeeNameAr: j['employeeNameAr'],
        employeeNameEn: j['employeeNameEn'],
        leaveTypeId: j['leaveTypeId'] ?? 0,
        leaveTypeAr: j['leaveTypeAr'],
        leaveTypeEn: j['leaveTypeEn'],
        startDate: _d(j['startDate']),
        endDate: _d(j['endDate']),
        days: j['days'] ?? 0,
        reason: j['reason'],
        status: j['status'] ?? 0,
        statusAr: j['statusAr'],
        statusEn: j['statusEn'],
        decisionNote: j['decisionNote'],
        createdAt: _d(j['createdAt']),
        currentLevel: j['currentLevel'] ?? 0,
        totalLevels: j['totalLevels'] ?? 0,
        approvals: (j['approvals'] as List? ?? [])
            .map((e) => ApprovalStep.fromJson(e as Map<String, dynamic>))
            .toList(),
      );
}
