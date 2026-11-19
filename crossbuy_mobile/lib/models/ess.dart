// Employee self-service models for /api/ess (payslips, attendance, requests, the approvals inbox).

num _n(dynamic v) => v is num ? v : num.tryParse('${v ?? 0}') ?? 0;

class PayslipSummary {
  final int id, year, month;
  final num net;
  PayslipSummary(this.id, this.year, this.month, this.net);
  factory PayslipSummary.fromJson(Map<String, dynamic> j) => PayslipSummary(j['id'] ?? 0, j['year'] ?? 0, j['month'] ?? 0, _n(j['net']));
}

class PayslipDetail {
  final int id, year, month, lateMinutes, absentDays;
  final String slipNo;
  final String? bankName, ibanTail;   // where the net is paid
  final num basic, allowances, overtime, socialInsurance, tax, late, absence, loan, partPaidLeave, net, employerInsurance;
  PayslipDetail.fromJson(Map<String, dynamic> j)
      : id = j['id'] ?? 0,
        year = j['year'] ?? 0,
        month = j['month'] ?? 0,
        slipNo = j['slipNo'] ?? '',
        bankName = j['bankName'],
        ibanTail = j['ibanTail'],
        basic = _n(j['earnings']?['basic']),
        allowances = _n(j['earnings']?['allowances']),
        overtime = _n(j['earnings']?['overtime']),
        socialInsurance = _n(j['deductions']?['socialInsurance']),
        tax = _n(j['deductions']?['tax']),
        late = _n(j['deductions']?['late']),
        lateMinutes = (j['deductions']?['lateMinutes'] ?? 0) as int,
        absence = _n(j['deductions']?['absence']),
        absentDays = (j['deductions']?['absentDays'] ?? 0) as int,
        loan = _n(j['deductions']?['loan']),
        partPaidLeave = _n(j['deductions']?['partPaidLeave']),
        net = _n(j['net']),
        employerInsurance = _n(j['employerInsurance']);
  num get earnings => basic + allowances + overtime;
  num get deductions => socialInsurance + tax + late + absence + loan + partPaidLeave;
}

class AttendanceDay {
  final DateTime date;
  final String status; // Present | Late | Absent | Holiday | RestDay | Leave
  final DateTime? checkIn, checkOut;
  final num workedHours;
  final String? source;   // Web | Mobile | Device | Manual
  AttendanceDay.fromJson(Map<String, dynamic> j)
      : date = DateTime.parse(j['date']),
        status = j['status'] ?? '',
        checkIn = j['checkIn'] == null ? null : DateTime.tryParse(j['checkIn']),
        checkOut = j['checkOut'] == null ? null : DateTime.tryParse(j['checkOut']),
        workedHours = _n(j['workedHours']),
        source = j['source']?.toString();
}

class AttendanceMonth {
  final int year, month;
  final int workDays, presentDays, lateCount, overtimeMinutes, leaveDays, absentDays;
  final List<AttendanceDay> days;
  AttendanceMonth.fromJson(Map<String, dynamic> j)
      : year = j['year'] ?? 0,
        month = j['month'] ?? 0,
        workDays = j['summary']?['workDays'] ?? 0,
        presentDays = j['summary']?['presentDays'] ?? 0,
        lateCount = j['summary']?['lateCount'] ?? 0,
        overtimeMinutes = j['summary']?['overtimeMinutes'] ?? 0,
        leaveDays = j['summary']?['leaveDays'] ?? 0,
        absentDays = j['summary']?['absentDays'] ?? 0,
        days = ((j['days'] as List?) ?? []).map((d) => AttendanceDay.fromJson(Map<String, dynamic>.from(d))).toList();
}

class MyRequest {
  final int id, status;
  final String type;
  final String? letterType, addressee;
  final num? amount, instalment;
  final DateTime? createdAt;
  MyRequest.fromJson(Map<String, dynamic> j)
      : id = j['id'] ?? 0,
        status = j['status'] ?? 0,
        type = j['type'] ?? 'Letter',
        letterType = j['letterType'],
        addressee = j['addressee'],
        amount = j['amount'] == null ? null : _n(j['amount']),
        instalment = j['instalment'] == null ? null : _n(j['instalment']),
        createdAt = j['createdAt'] == null ? null : DateTime.tryParse(j['createdAt']);
}

/// One item of the unified approvals inbox: a leave or an employee request whose current step is mine.
class ApprovalItem {
  final String kind; // leave | request
  final int id, requesterId, level;
  final String? type, typeAr, requesterAr, requesterEn, photo;
  final DateTime? from, to, createdAt;
  final int? days;
  final num? amount;
  // the requester's department, the balance now and after, the substitute, colleagues away, the next step and my title
  final String? deptAr, deptEn, substituteAr, substituteEn, nextAr, nextEn, nextTitleAr, nextTitleEn, myTitleAr, myTitleEn;
  final int? remaining, after;
  final int away;
  final List<String> awayAr, awayEn;
  ApprovalItem.fromJson(Map<String, dynamic> j)
      : kind = j['kind'] ?? 'leave',
        id = j['id'] ?? 0,
        requesterId = j['requesterId'] ?? 0,
        level = j['level'] ?? 0,
        type = j['type'],
        typeAr = j['typeAr'],
        requesterAr = j['requesterAr'],
        requesterEn = j['requesterEn'],
        photo = j['photo'],
        from = j['from'] == null ? null : DateTime.tryParse(j['from']),
        to = j['to'] == null ? null : DateTime.tryParse(j['to']),
        createdAt = j['createdAt'] == null ? null : DateTime.tryParse(j['createdAt']),
        days = j['days'],
        amount = j['amount'] == null ? null : _n(j['amount']),
        deptAr = j['deptAr'],
        deptEn = j['deptEn'],
        substituteAr = j['substituteAr'],
        substituteEn = j['substituteEn'],
        nextAr = j['nextAr'],
        nextEn = j['nextEn'],
        nextTitleAr = j['nextTitleAr'],
        nextTitleEn = j['nextTitleEn'],
        myTitleAr = j['myTitleAr'],
        myTitleEn = j['myTitleEn'],
        remaining = j['remaining'],
        after = j['after'],
        away = j['away'] ?? 0,
        awayAr = ((j['awayAr'] as List?) ?? const []).map((e) => '$e').toList(),
        awayEn = ((j['awayEn'] as List?) ?? const []).map((e) => '$e').toList();
  String requester(bool ar) => (ar ? requesterAr : (requesterEn ?? requesterAr)) ?? '';
  String dept(bool ar) => (ar ? deptAr : (deptEn ?? deptAr)) ?? '';
}
