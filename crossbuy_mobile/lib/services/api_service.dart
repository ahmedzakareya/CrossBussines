import 'package:dio/dio.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../app_config.dart';
import 'app_nav.dart';
import '../models/app_notification.dart';
import '../models/calls.dart';
import '../models/dashboard.dart';
import '../models/employee.dart';
import '../models/rep.dart';
import '../models/store.dart';
import '../models/ess.dart';
import '../models/finance_summary.dart';
import '../models/inventory_item.dart';
import '../models/leave.dart';
import '../models/org_node.dart';
import '../models/org_position.dart';
import '../models/profile_extras.dart';

class StructureResult {
  final int? myNodeId;
  final List<OrgNode> nodes;
  StructureResult(this.myNodeId, this.nodes);
}

/// Thin wrapper over the CrossBuy REST API with token persistence.
class ApiService {
  ApiService._() {
    _dio = Dio(BaseOptions(
      baseUrl: AppConfig.apiBaseUrl,
      connectTimeout: const Duration(seconds: 20),
      receiveTimeout: const Duration(seconds: 20),
      headers: {'Content-Type': 'application/json'},
    ));
    _dio.interceptors.add(InterceptorsWrapper(
      onRequest: (options, handler) {
        if (_token != null) {
          options.headers['Authorization'] = 'Bearer $_token';
        }
        // names (items, warehouses, units) come back in the app's language
        options.headers['Accept-Language'] = language;
        handler.next(options);
      },
      // An expired or revoked token used to leave the app stuck on empty screens. A 401 now signs the user out and
      // the app returns to the login screen (onUnauthorized is wired by AuthProvider).
      onError: (e, handler) async {
        if (e.response?.statusCode == 401 && _token != null && !e.requestOptions.path.endsWith('/api/auth/login')) {
          await logout();
          onUnauthorized?.call();
        }
        handler.next(e);
      },
    ));
  }

  static final ApiService instance = ApiService._();

  late final Dio _dio;
  String? _token;
  static const _tokenKey = 'auth_token';

  String? get token => _token;

  /// The app's language, sent as Accept-Language (set by LocaleProvider).
  String language = 'ar';

  /// Called after a 401 has signed the user out.
  void Function()? onUnauthorized;
  bool get isLoggedIn => _token != null;

  Future<void> loadToken() async {
    final prefs = await SharedPreferences.getInstance();
    _token = prefs.getString(_tokenKey);
  }

  Future<void> _saveToken(String token) async {
    _token = token;
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_tokenKey, token);
  }

  Future<void> logout() async {
    _token = null;
    final prefs = await SharedPreferences.getInstance();
    await prefs.remove(_tokenKey);
  }

  /// Returns true on success. Throws nothing the UI can't handle.
  Future<bool> login(String username, String password) async {
    try {
      final res = await _dio.post('/api/auth/login', data: {
        'userName': username,
        'password': password,
      });
      final token = res.data?['token'];
      if (token is String && token.isNotEmpty) {
        await _saveToken(token);
        return true;
      }
      return false;
    } on DioException {
      return false;
    }
  }

  Future<Employee> getProfile() async {
    final res = await _dio.get('/api/me/profile');
    return Employee.fromJson(res.data['data'] as Map<String, dynamic>);
  }

  Future<OrgPosition> getPosition() async {
    final res = await _dio.get('/api/me/position');
    return OrgPosition.fromJson(res.data as Map<String, dynamic>);
  }

  Future<DashboardData> getDashboard() async {
    final res = await _dio.get('/api/me/dashboard');
    return DashboardData.fromJson(res.data['data'] as Map<String, dynamic>);
  }

  Future<List<ActivityItem>> getActivity() async {
    final res = await _dio.get('/api/me/activity');
    return (res.data['data'] as List? ?? [])
        .map((e) => ActivityItem.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<List<DocItem>> getDocuments() async {
    final res = await _dio.get('/api/me/documents');
    return (res.data['data'] as List? ?? [])
        .map((e) => DocItem.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<StructureResult> getStructure() async {
    final res = await _dio.get('/api/me/structure');
    final list = (res.data['nodes'] as List? ?? [])
        .map((e) => OrgNode.fromJson(e as Map<String, dynamic>))
        .toList();
    return StructureResult(res.data['myNodeId'], list);
  }

  // ---- Accounting / Finance ----

  Future<FinanceSummary> getFinanceSummary() async {
    // No companyId: the server resolves the signed-in user's company (a hardcoded company is forbidden).
    final res = await _dio.get('/api/acc/summary');
    return FinanceSummary.fromJson(res.data['data'] as Map<String, dynamic>);
  }

  // ---- Inventory ----

  Future<List<InventoryItem>> getInventoryItems({String? q}) async {
    final res = await _dio.get('/api/inv/items', queryParameters: {if (q != null && q.isNotEmpty) 'q': q});
    return (res.data as List? ?? [])
        .map((e) => InventoryItem.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<ScanResult> scanInventory(String barcode) async {
    final res = await _dio.get('/api/inv/scan', queryParameters: {'barcode': barcode});
    return ScanResult.fromJson(res.data as Map<String, dynamic>);
  }

  // ---- Leave module ----

  Future<List<LeaveType>> getLeaveTypes() async {
    final res = await _dio.get('/api/leave/types');
    return (res.data['data'] as List? ?? [])
        .map((e) => LeaveType.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  /// Work-day flags indexed by weekday (0=Sunday … 6=Saturday).
  /// Returns null when there is no restriction (any day allowed).
  Future<List<bool>?> getWorkDays() async {
    try {
      final res = await _dio.get('/api/leave/workdays');
      if (res.data?['restricted'] != true) return null;
      final days = (res.data?['days'] as List?) ?? [];
      return days.map((e) => e == true).toList();
    } on DioException {
      return null;
    }
  }

  Future<List<LeaveRequest>> getMyLeaves() async {
    final res = await _dio.get('/api/leave/my');
    return (res.data['data'] as List? ?? [])
        .map((e) => LeaveRequest.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  /// The request before it is sent: working days, balance after, and who approves it (null when it cannot be read).
  Future<LeavePreview?> previewLeave(int typeId, DateTime start, DateTime end) async {
    try {
      final res = await _dio.get('/api/leave/preview', queryParameters: {'typeId': typeId, 'start': start.toIso8601String(), 'end': end.toIso8601String()});
      return LeavePreview.fromJson(res.data['data'] as Map<String, dynamic>);
    } on DioException {
      return null;
    }
  }

  /// Colleagues who may cover my leave (active, my company, my department first).
  Future<List<Colleague>> getColleagues({String? q}) async {
    final res = await _dio.get('/api/leave/colleagues', queryParameters: {if (q != null && q.isNotEmpty) 'q': q});
    return (res.data['data'] as List? ?? []).map((e) => Colleague.fromJson(e as Map<String, dynamic>)).toList();
  }

  Future<List<LeaveRequest>> getPendingApprovals() async {
    final res = await _dio.get('/api/leave/pending');
    return (res.data['data'] as List? ?? [])
        .map((e) => LeaveRequest.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  /// Returns (ok, errorMessage). errorMessage is set from the server on failure.
  Future<(bool, String?)> createLeave({
    required int leaveTypeId,
    required DateTime startDate,
    required DateTime endDate,
    String? reason,
    int? substituteEmployeeId,
  }) async {
    try {
      final res = await _dio.post('/api/leave', data: {
        'leaveTypeID': leaveTypeId,
        'startDate': startDate.toIso8601String(),
        'endDate': endDate.toIso8601String(),
        'reason': reason,
        if (substituteEmployeeId != null) 'substituteEmployeeID': substituteEmployeeId,
      });
      final ok = res.data?['success'] == true;
      if (ok) AppNav.notifyDataChanged();
      return (ok, null);
    } on DioException catch (e) {
      final msg = e.response?.data is Map ? e.response?.data['message'] as String? : null;
      return (false, msg);
    }
  }

  /// approve = true to approve, false to reject. Returns (ok, errorMessage).
  Future<(bool, String?)> decideLeave(int id, bool approve, {String? note}) async {
    try {
      final res = await _dio.post('/api/leave/$id/decision', data: {
        'approve': approve,
        'note': note,
      });
      final ok = res.data?['success'] == true;
      if (ok) AppNav.notifyDataChanged();
      return (ok, null);
    } on DioException catch (e) {
      final msg = e.response?.data is Map ? e.response?.data['message'] as String? : null;
      return (false, msg);
    }
  }

  // ---- Employee self-service (/api/ess) ----

  List<Map<String, dynamic>> _list(dynamic data) => ((data?['data'] as List?) ?? []).map((e) => Map<String, dynamic>.from(e as Map)).toList();

  /// (currency, my posted payslips newest first)
  Future<(String, List<PayslipSummary>)> getPayslips() async {
    final res = await _dio.get('/api/ess/payslips');
    return ((res.data['currency'] as String?) ?? '', _list(res.data).map(PayslipSummary.fromJson).toList());
  }

  Future<(String, PayslipDetail)> getPayslip(int id) async {
    final res = await _dio.get('/api/ess/payslips/$id');
    return ((res.data['currency'] as String?) ?? '', PayslipDetail.fromJson(Map<String, dynamic>.from(res.data['data'])));
  }

  Future<AttendanceMonth> getAttendance(int year, int month) async {
    final res = await _dio.get('/api/ess/attendance', queryParameters: {'year': year, 'month': month});
    return AttendanceMonth.fromJson(Map<String, dynamic>.from(res.data));
  }

  /// (currency, my requests) — the currency is the company's, for the amount of an advance
  Future<(String, List<MyRequest>)> getMyRequests() async {
    final res = await _dio.get('/api/ess/requests');
    return ((res.data['currency'] as String?) ?? '', _list(res.data).map(MyRequest.fromJson).toList());
  }

  Future<(bool, String?)> createRequest(Map<String, dynamic> body) async {
    try {
      final res = await _dio.post('/api/ess/requests', data: body);
      final ok = res.data?['success'] == true;
      if (ok) AppNav.notifyDataChanged();
      return (ok, null);
    } on DioException catch (e) {
      return (false, e.response?.data is Map ? e.response?.data['message'] as String? : null);
    }
  }

  /// (currency, everything whose current approval step is mine: leave AND employee requests)
  Future<(String, List<ApprovalItem>)> getApprovals() async {
    final res = await _dio.get('/api/ess/approvals');
    return ((res.data['currency'] as String?) ?? '', _list(res.data).map(ApprovalItem.fromJson).toList());
  }

  /// Decide an inbox item: a leave goes to /api/leave, an employee request to /api/ess.
  Future<(bool, String?)> decide(ApprovalItem item, bool approve, {String? note}) async {
    if (item.kind == 'leave') return decideLeave(item.id, approve, note: note);
    try {
      final res = await _dio.post('/api/ess/requests/${item.id}/decision', data: {'approve': approve, 'note': note});
      final ok = res.data?['success'] == true;
      if (ok) AppNav.notifyDataChanged();
      return (ok, null);
    } on DioException catch (e) {
      return (false, e.response?.data is Map ? e.response?.data['message'] as String? : null);
    }
  }

  /// Is the signed-in user allowed the company's financial summary? (drives the owner's KPI cards)
  Future<FinanceSummary?> tryFinanceSummary() async {
    try {
      return await getFinanceSummary();
    } on DioException {
      return null;
    }
  }



  // ---- Notifications ----

  /// Returns (unreadCount, list).
  // ---------------- sales-rep mode (/api/rep, receipts via /api/acc/ar/receipts) ----------------

  /// (total, currency, one page of 20 customers with outstanding / headroom)
  Future<(int, String, List<RepCustomer>)> getRepCustomers({String? q, int page = 1}) async {
    final res = await _dio.get('/api/rep/customers', queryParameters: {if (q != null && q.isNotEmpty) 'q': q, 'page': page});
    return ((res.data['total'] as int?) ?? 0, (res.data['currency'] as String?) ?? '', _list(res.data).map(RepCustomer.fromJson).toList());
  }

  /// (the card, currency, may I take an order, may I collect)
  Future<(RepCustomer, String, bool, bool, List<OpenInvoice>)> getRepCustomer(int id) async {
    final res = await _dio.get('/api/rep/customers/$id');
    return (
      RepCustomer.fromJson(res.data['data'] as Map<String, dynamic>),
      (res.data['currency'] as String?) ?? '',
      res.data['canOrder'] == true,
      res.data['canCollect'] == true,
      [for (final i in (res.data['openInvoices'] as List? ?? [])) OpenInvoice.fromJson(i as Map<String, dynamic>)],
    );
  }

  /// The rep's day: route, target, achievement and the visits (generated from the route on first read).
  Future<RepDay> getRepToday() async {
    final res = await _dio.get('/api/rep/today');
    return RepDay.fromJson(res.data as Map<String, dynamic>);
  }

  /// Mark the arrival at a visit (the first arrival is kept). Returns the arrival time, or null.
  Future<DateTime?> arriveVisit(int visitId) async {
    try {
      final res = await _dio.post('/api/rep/visits/$visitId/arrive');
      return utcFromServer(res.data?['arrivedAt']);
    } on DioException {
      return null;
    }
  }

  /// Record a collection made at a visit (the receipt itself is posted through the accounting API).
  Future<void> visitCollected(int visitId, double amount) async {
    try {
      await _dio.post('/api/rep/visits/$visitId/collected', data: {'amount': amount});
      AppNav.notifyDataChanged();
    } on DioException {
      // the receipt is posted; only the visit's tally is missing — the day reloads from the server anyway
    }
  }

  /// End a visit (done, with an optional note) or skip it with the reason. Returns the server's refusal, or null.
  Future<String?> closeVisit(int visitId, {required bool skipped, String? outcome}) async {
    try {
      await _dio.post('/api/rep/visits/$visitId/close', data: {'skipped': skipped, 'outcome': outcome});
      AppNav.notifyDataChanged();
      return null;
    } on DioException catch (e) {
      final d = e.response?.data;
      return d is Map ? d['message'] as String? ?? 'error' : 'error';
    }
  }

  Map<String, dynamic> _order(int customerId, Map<int, double> qty, String? notes, {int? visitId}) => {
        'customerId': customerId, 'notes': notes, if (visitId != null) 'visitId': visitId,
        'lines': [for (final e in qty.entries) {'itemId': e.key, 'qty': e.value}],
      };

  /// The server prices every line for this customer; the phone never sends a price.
  Future<OrderPreview> previewOrder(int customerId, Map<int, double> qty) async {
    try {
      final res = await _dio.post('/api/rep/order/preview', data: _order(customerId, qty, null));
      return OrderPreview.fromJson(res.data as Map<String, dynamic>);
    } on DioException catch (e) {
      final d = e.response?.data;
      return OrderPreview(ok: false, overLimit: false, message: d is Map ? d['message'] as String? : null, lines: const [], total: 0, outstanding: 0);
    }
  }

  /// (ok, order number or the refusal, a margin/discount warning)
  Future<(bool, String?, String?)> saveOrder(int customerId, Map<int, double> qty, {String? notes, int? visitId}) async {
    try {
      final res = await _dio.post('/api/rep/order', data: _order(customerId, qty, notes, visitId: visitId));
      AppNav.notifyDataChanged();
      return (true, res.data['orderNo'] as String?, res.data['warning'] as String?);
    } on DioException catch (e) {
      final d = e.response?.data;
      return (false, d is Map ? d['message'] as String? : null, null);
    }
  }

  Future<List<CashAccount>> getCashAccounts() async {
    final res = await _dio.get('/api/rep/cash-accounts');
    return _list(res.data).map(CashAccount.fromJson).toList();
  }

  /// A receipt voucher through the accounting API (permission "pay").
  Future<(bool, String?)> collect(int customerId, double amount, String method, int cashAccountId) async {
    try {
      await _dio.post('/api/acc/ar/receipts', data: {
        'customerId': customerId, 'receiptDate': ymdIso(DateTime.now()), 'amount': amount, 'method': method, 'cashAccountId': cashAccountId,
      });
      AppNav.notifyDataChanged();
      return (true, null);
    } on DioException catch (e) {
      final d = e.response?.data;
      return (false, d is Map ? d['message'] as String? : null);
    }
  }

  /// A customer's cheque: a receipt on cheques under collection (1106) with the cheque beside it. (ok, receiptNo or error)
  Future<(bool, String?)> receiveCheque(int customerId, double amount, String chequeNo, DateTime dueDate, String? bank, {int? visitId}) async {
    try {
      final res = await _dio.post('/api/rep/cheque', data: {
        'customerId': customerId, 'amount': amount, 'chequeNo': chequeNo, 'dueDate': ymdIso(dueDate), 'bank': bank,
        if (visitId != null) 'visitId': visitId,
      });
      AppNav.notifyDataChanged();
      return (true, res.data?['receiptNo']?.toString());
    } on DioException catch (e) {
      final d = e.response?.data;
      return (false, d is Map ? d['message'] as String? : null);
    }
  }

  static String ymdIso(DateTime d) => '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

  // ---------------- storekeeper mode (/api/store) ----------------

  /// (warehouses I may post stock documents in, every active warehouse as a transfer target)
  Future<(List<StoreWarehouse>, List<StoreWarehouse>)> getStoreWarehouses() async {
    final res = await _dio.get('/api/store/warehouses');
    List<StoreWarehouse> parse(dynamic l) => (l as List? ?? []).map((e) => StoreWarehouse.fromJson(e as Map<String, dynamic>)).toList();
    return (parse(res.data['data']), parse(res.data['targets']));
  }

  /// The blind count sheet: the items held in a warehouse, with no quantities.
  Future<List<CountSheetItem>> getCountSheet(int warehouseId) async {
    final res = await _dio.get('/api/store/count-sheet', queryParameters: {'warehouseId': warehouseId});
    return _list(res.data).map(CountSheetItem.fromJson).toList();
  }

  /// Posts a count (or sends it for approval above the company threshold). The result reveals the variance.
  Future<StoreResult> postCount(int warehouseId, Map<int, double> counted, {String? notes}) =>
      _storePost('/api/store/count', {
        'warehouseId': warehouseId, 'notes': notes,
        'lines': [for (final e in counted.entries) {'itemId': e.key, 'countedQty': e.value}],
      });

  /// Approved purchase orders awaiting goods for a warehouse; null = not allowed to receive (no "purchase" permission).
  Future<List<OpenPurchaseOrder>?> getOpenOrders(int warehouseId) async {
    try {
      final res = await _dio.get('/api/store/receipts', queryParameters: {'warehouseId': warehouseId});
      return _list(res.data).map(OpenPurchaseOrder.fromJson).toList();
    } on DioException catch (e) {
      if (e.response?.statusCode == 403) return null;
      rethrow;
    }
  }

  /// The order's lines still awaiting goods: (orderNo, vendor, lines).
  Future<(String, String, List<ReceiptLine>)> getReceiptSheet(int poId, int warehouseId) async {
    final res = await _dio.get('/api/store/receipts/$poId', queryParameters: {'warehouseId': warehouseId});
    final d = res.data['data'] as Map<String, dynamic>;
    return (d['orderNo']?.toString() ?? '', d['vendor']?.toString() ?? '', [for (final l in (d['lines'] as List? ?? [])) ReceiptLine.fromJson(l as Map<String, dynamic>)]);
  }

  /// Receive the scanned quantities against the order's lines. Returns (ok, receiptNo or the server's refusal).
  Future<(bool, String?)> postReceipt(int poId, int warehouseId, Map<int, double> qtyByLine) async {
    try {
      final res = await _dio.post('/api/store/receipts', data: {
        'poId': poId, 'warehouseId': warehouseId, 'notes': 'mobile',
        'lines': [for (final e in qtyByLine.entries) {'lineId': e.key, 'qty': e.value}],
      });
      return (res.data?['success'] == true, res.data?['receiptNo']?.toString());
    } on DioException catch (e) {
      return (false, e.response?.data is Map ? e.response?.data['message'] as String? : null);
    }
  }

  Future<StoreResult> postTransfer(int fromId, int toId, Map<int, double> qty, {String? notes}) =>
      _storePost('/api/store/transfer', {
        'fromWarehouseId': fromId, 'toWarehouseId': toId, 'notes': notes,
        'lines': [for (final e in qty.entries) {'itemId': e.key, 'qty': e.value}],
      });

  Future<StoreResult> _storePost(String path, Map<String, dynamic> body) async {
    try {
      final res = await _dio.post(path, data: body);
      return StoreResult.fromJson(res.data as Map<String, dynamic>);
    } on DioException catch (e) {
      final d = e.response?.data;
      return StoreResult(ok: false, message: d is Map ? d['message'] as String? : null);
    }
  }

  Future<(int, List<AppNotification>)> getNotifications() async {
    final res = await _dio.get('/api/notifications');
    final list = (res.data['data'] as List? ?? [])
        .map((e) => AppNotification.fromJson(e as Map<String, dynamic>))
        .toList();
    return (res.data['unread'] as int? ?? 0, list);
  }

  Future<void> markAllNotificationsRead() async {
    try {
      await _dio.post('/api/notifications/read-all');
    } on DioException {/* ignore */}
  }

  // ---------------------------------------------------------------- H176 internal calls (api/calls)
  /// What this person can do with calls here; null when the hospital module or the calls are not available to them.
  Future<CallsMe?> getCallsMe() async {
    try {
      final res = await _dio.get('/api/calls/me');
      return CallsMe.fromJson(res.data as Map<String, dynamic>);
    } on DioException {
      return null;
    }
  }

  /// The directory: available first, then in a call, then offline.
  Future<List<CallPerson>> getCallPeople({String? q}) async {
    final res = await _dio.get('/api/calls/people', queryParameters: {if (q != null && q.isNotEmpty) 'q': q});
    return (res.data['people'] as List? ?? []).map((e) => CallPerson.fromJson((e as Map).cast<String, dynamic>())).toList();
  }

  /// The hospital's STUN/TURN for the next call (a relay password valid 12 hours).
  Future<List<Map<String, dynamic>>> getCallIce() async {
    try {
      final res = await _dio.get('/api/calls/ice');
      return (res.data['iceServers'] as List? ?? []).map((e) => (e as Map).cast<String, dynamic>()).toList();
    } on DioException {
      return const [];
    }
  }

  /// My call log, newest first. only = null | 'missed' | 'recorded'.
  Future<(List<CallLogRow>, int)> getCallLog({int page = 1, String? only}) async {
    final res = await _dio.get('/api/calls/log', queryParameters: {'page': page, if (only != null) 'only': only});
    final rows = (res.data['rows'] as List? ?? []).map((e) => CallLogRow.fromJson((e as Map).cast<String, dynamic>())).toList();
    return (rows, (res.data['total'] as num?)?.toInt() ?? rows.length);
  }

  /// The patient a group call is about (name or MRN, at least two letters).
  Future<List<CallPatient>> getCallPatients(String q) async {
    try {
      final res = await _dio.get('/api/calls/patients', queryParameters: {'q': q});
      return (res.data['patients'] as List? ?? []).map((e) => CallPatient.fromJson(e)).whereType<CallPatient>().toList();
    } on DioException {
      return const [];
    }
  }

  /// A note written during the call (only a person in that call).
  Future<bool> postCallNote(int callId, String note) async {
    try {
      final res = await _dio.post('/api/calls/$callId/note', data: {'note': note});
      return res.data?['ok'] == true;
    } on DioException {
      return false;
    }
  }

  /// The player's facts; null when it does not exist or is not mine to see.
  Future<Map<String, dynamic>?> getCallRecording(int id) async {
    try {
      final res = await _dio.get('/api/calls/recording/$id');
      return (res.data['recording'] as Map?)?.cast<String, dynamic>();
    } on DioException {
      return null;
    }
  }

  /// The recording's address (log=1 the first time: the opening is recorded) and the header that opens it.
  String callRecordingMediaUrl(int id, {bool log = false}) => '${AppConfig.apiBaseUrl}/api/calls/recording/$id/media${log ? '?log=1' : ''}';
  Map<String, String> get authHeaders => {if (_token != null) 'Authorization': 'Bearer $_token'};
}
