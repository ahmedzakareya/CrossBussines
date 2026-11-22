import 'dart:io';

import 'package:dio/dio.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../app_config.dart';
import 'app_nav.dart';
import '../models/app_notification.dart';
import '../models/calls.dart';
import '../models/chat.dart';
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

  /// A punch from the phone: the server checks the position against the branch office. (ok, kind "in"/"out" or error)
  Future<(bool, String?)> punch(double latitude, double longitude, double accuracy) async {
    try {
      final res = await _dio.post('/api/ess/punch', data: {'latitude': latitude, 'longitude': longitude, 'accuracy': accuracy});
      AppNav.notifyDataChanged();
      return (true, res.data?['kind']?.toString());
    } on DioException catch (e) {
      final d = e.response?.data;
      return (false, d is Map ? d['message'] as String? : null);
    }
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

  /// "Return for review": the request goes back to its requester to amend and send again; the note says what.
  Future<(bool, String?)> returnForReview(ApprovalItem item, String note) async {
    try {
      final res = await _dio.post('/api/ess/approvals/${item.kind}/${item.id}/return', data: {'note': note});
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

  /// The call's summary as it stands (status none | working | done | failed); null = not open to me.
  Future<Map<String, dynamic>?> getCallSummary(int id) async {
    try {
      final res = await _dio.get('/api/calls/recording/$id/summary');
      return (res.data['summary'] as Map?)?.cast<String, dynamic>();
    } on DioException {
      return null;
    }
  }

  /// Start the call's summary (made in the background on the company's servers). (ok, summary)
  Future<(bool, Map<String, dynamic>?)> startCallSummary(int id) async {
    try {
      final res = await _dio.post('/api/calls/recording/$id/summary', options: Options(sendTimeout: const Duration(minutes: 2), receiveTimeout: const Duration(minutes: 2)));
      return (res.data['ok'] == true, (res.data['summary'] as Map?)?.cast<String, dynamic>());
    } on DioException {
      return (false, null);
    }
  }

  /// The recording's address (log=1 the first time: the opening is recorded) and the header that opens it.
  String callRecordingMediaUrl(int id, {bool log = false}) => '${AppConfig.apiBaseUrl}/api/calls/recording/$id/media${log ? '?log=1' : ''}';
  Map<String, String> get authHeaders => {if (_token != null) 'Authorization': 'Bearer $_token'};

  /// Push (2026-11-22): this phone's FCM token, registered after signing in and withdrawn before signing out.
  Future<bool> pushRegister(String token, String platform) async {
    try {
      final d = (await _dio.post('/api/chat/push/register', data: FormData.fromMap({'token': token, 'platform': platform}))).data as Map;
      return d['ok'] == true;
    } on DioException {
      return false;
    }
  }
  Future<void> pushUnregister(String token) async {
    try { await _dio.post('/api/chat/push/unregister', data: FormData.fromMap({'token': token})); } on DioException { /* signing out anyway */ }
  }

  /// A voice or video message, or a call recording, as a local file for the native player. The player (ExoPlayer)
  /// fetches on its own TLS stack, which does not know the development certificate that this client is told to trust
  /// (dev_certificate.dart), so it failed with "trust anchor not found". Downloaded once into the app's cache, with the
  /// sign-in header; null when it cannot be fetched.
  Future<File?> mediaFile(String url) async {
    try {
      final ext = RegExp(r'\.(webm|mp4|m4a|aac|ogg|mp3|wav)(?=$|[?#])', caseSensitive: false).firstMatch(url)?.group(1)?.toLowerCase() ?? 'mp4';
      final f = File('${Directory.systemTemp.path}/cb-media-${url.hashCode.toUnsigned(32).toRadixString(16)}.$ext');
      if (await f.exists() && await f.length() > 0) return f;
      final part = File('${f.path}.part');
      await _dio.download(url, part.path, options: Options(headers: authHeaders, receiveTimeout: const Duration(minutes: 5)));
      return await part.rename(f.path);
    } on DioException {
      return null;
    } on FileSystemException {
      return null;
    }
  }
  // ---------------------------------------------------------------- Communication v2: the chat (api/chat)
  List<Map<String, dynamic>> _rows(Object? d) => [for (final e in (d as List? ?? const [])) (e as Map).cast<String, dynamic>()];

  Future<List<ChatConv>> getChats() async => _rows((await _dio.get('/api/chat/conversations')).data).map(ChatConv.fromJson).toList();

  Future<List<ChatMsg>> getChatMessages(int c, {int? before}) async =>
      _rows((await _dio.get('/api/chat/messages', queryParameters: {'c': c, if (before != null) 'before': before})).data).map(ChatMsg.fromJson).toList();

  Future<ChatHeader?> getChatHeader(int c) async {
    try {
      return ChatHeader.fromJson((await _dio.get('/api/chat/header', queryParameters: {'c': c})).data as Map<String, dynamic>);
    } on DioException {
      return null;
    }
  }

  Future<Map<int, ChatMeta>> getChatMeta(int c, List<int> ids) async {
    if (ids.isEmpty) return {};
    try {
      final rows = _rows((await _dio.get('/api/chat/meta', queryParameters: {'c': c, 'ids': ids.join(',')})).data);
      return {for (final r in rows) (r['id'] as num).toInt(): ChatMeta.fromJson(r)};
    } on DioException {
      return {};
    }
  }

  Future<List<ChatPinned>> getChatPinned(int c) async {
    try {
      return _rows((await _dio.get('/api/chat/pinned', queryParameters: {'c': c})).data).map(chatPinnedFromJson).toList();
    } on DioException {
      return const [];
    }
  }

  /// Text, or a file (a picture, a document, a voice message). Returns the message, or the refusal's code.
  Future<(ChatMsg?, String?)> sendChat(int c, {String? body, int? replyToId, String? filePath, String? fileName}) async {
    try {
      final form = FormData.fromMap({
        'c': c,
        if (body != null && body.isNotEmpty) 'body': body,
        if (replyToId != null) 'replyToId': replyToId,
        if (filePath != null) 'file': await MultipartFile.fromFile(filePath, filename: fileName),
      });
      final res = await _dio.post('/api/chat/send', data: form, options: Options(contentType: 'multipart/form-data', sendTimeout: const Duration(minutes: 2)));
      return (ChatMsg.fromJson(res.data as Map<String, dynamic>), null);
    } on DioException catch (e) {
      final d = e.response?.data;
      return (null, d is Map ? (d['code'] as String? ?? 'refused') : 'network');
    }
  }

  Future<void> readChat(int c) async {
    try { await _dio.post('/api/chat/read', queryParameters: {'c': c}); } on DioException {/* the next read marks it */}
  }

  Future<bool> reactChat(int id, String emoji) async {
    try { await _dio.post('/api/chat/react', queryParameters: {'id': id, 'emoji': emoji}); return true; } on DioException { return false; }
  }

  /// The words of a voice message (transcribed once on the server). (status, text); status "none" = not available now.
  Future<(String, String?)> transcribeChat(int id) async {
    try {
      final d = (await _dio.post('/api/chat/transcribe', queryParameters: {'id': id}, options: Options(receiveTimeout: const Duration(minutes: 3)))).data as Map;
      return ('${d['status'] ?? 'none'}', d['text'] as String?);
    } on DioException {
      return ('none', null);
    }
  }

  Future<bool> voteChat(int id, List<int> options) async {
    try { return (await _dio.post('/api/chat/vote', queryParameters: {'id': id, 'options': options.join(',')})).data['ok'] == true; } on DioException { return false; }
  }

  Future<int?> chatWith(int otherId) async {
    try { return ((await _dio.post('/api/chat/direct', queryParameters: {'otherId': otherId})).data['id'] as num?)?.toInt(); } on DioException { return null; }
  }

  Future<List<ChatPerson>> getChatPeople({String? q}) async =>
      _rows((await _dio.get('/api/chat/directory', queryParameters: {if (q != null && q.isNotEmpty) 'q': q})).data).map(chatPersonFromJson).toList();

  Future<List<ChatHit>> findChat(String q) async {
    return _rows((await _dio.get('/api/chat/find', queryParameters: {'q': q})).data).map(chatHitFromJson).toList();
  }

  /// (my employee id, unread) — the badge.
  Future<(int, int)> chatPulse() async {
    try {
      final d = (await _dio.get('/api/chat/pulse')).data as Map;
      return ((d['me'] as num?)?.toInt() ?? 0, (d['unread'] as num?)?.toInt() ?? 0);
    } on DioException {
      return (0, 0);
    }
  }

  // ---- Communication v2 in the app: threads, pin, save, forward, polls, scheduled, status, channels, catch-up ----
  Future<Map<String, dynamic>?> _getMap(String path, [Map<String, dynamic>? q]) async {
    try { final d = (await _dio.get(path, queryParameters: q)).data; return d is Map ? d.cast<String, dynamic>() : null; } on DioException { return null; }
  }
  Future<List<Map<String, dynamic>>> _getList(String path, [Map<String, dynamic>? q]) async {
    try { return _rows((await _dio.get(path, queryParameters: q)).data); } on DioException { return const []; }
  }
  Future<Map<String, dynamic>?> _post(String path, {Map<String, dynamic>? q, Map<String, dynamic>? form}) async {
    try {
      final d = (await _dio.post(path, queryParameters: q, data: form == null ? null : FormData.fromMap(form))).data;
      return d is Map ? d.cast<String, dynamic>() : <String, dynamic>{'ok': true};
    } on DioException {
      return null;
    }
  }

  Future<List<ChatMsg>> chatThread(int c, int root) async => (await _getList('/api/chat/thread', {'c': c, 'root': root})).map(ChatMsg.fromJson).toList();
  Future<ChatMsg?> chatThreadReply(int c, int root, String body) async {
    final d = await _post('/api/chat/thread', form: {'c': c, 'root': root, 'body': body});
    return d == null || d['id'] == null ? null : ChatMsg.fromJson(d);
  }
  Future<bool> chatPin(int id, bool on) async => (await _post('/api/chat/pin', q: {'id': id, 'on': on}))?['ok'] == true;
  Future<bool> chatSave(int id, bool on) async => (await _post('/api/chat/save', q: {'id': id, 'on': on}))?['ok'] == true;
  Future<Map<String, dynamic>?> chatSaved() => _getMap('/api/chat/saved');
  Future<int> chatForward(int id, List<int> to, {String? note}) async =>
      ((await _post('/api/chat/forward', form: {'id': id, 'to': to.join(','), if (note != null && note.isNotEmpty) 'note': note}))?['sent'] as num?)?.toInt() ?? 0;
  Future<ChatMsg?> chatCreatePoll(int c, String question, List<String> options, {bool multi = false, bool anonymous = false}) async {
    final d = await _post('/api/chat/poll', form: {'c': c, 'question': question, 'options': options.join('\n'), 'multi': multi, 'anonymous': anonymous});
    return d == null || d['id'] == null ? null : ChatMsg.fromJson(d);
  }
  Future<bool> chatSchedule(int c, String body, DateTime at) async =>
      (await _post('/api/chat/schedule', form: {'c': c, 'body': body, 'sendAt': at.toUtc().toIso8601String()}))?['ok'] == true;
  Future<List<Map<String, dynamic>>> chatScheduled(int c) => _getList('/api/chat/scheduled', {'c': c});
  Future<bool> chatCancelScheduled(int id) async => (await _post('/api/chat/scheduled/cancel', q: {'id': id}))?['ok'] == true;
  Future<Map<String, dynamic>?> chatStatuses(List<int> ids) => _getMap('/api/chat/statuses', {'ids': ids.join(',')});
  Future<bool> chatSetStatus({String? emoji, String? text, DateTime? until, bool dnd = false, DateTime? dndUntil}) async =>
      (await _post('/api/chat/status', form: {
        if (emoji != null) 'emoji': emoji, if (text != null) 'text': text, if (until != null) 'until': until.toUtc().toIso8601String(),
        'dnd': dnd, if (dndUntil != null) 'dndUntil': dndUntil.toUtc().toIso8601String(),
      }))?['ok'] == true;
  Future<List<Map<String, dynamic>>> chatChannels({String? q}) => _getList('/api/chat/channels', {if (q != null && q.isNotEmpty) 'q': q});
  Future<int?> chatCreateChannel(String title, {String? description}) async =>
      ((await _post('/api/chat/channels', form: {'title': title, if (description != null && description.isNotEmpty) 'description': description}))?['id'] as num?)?.toInt();
  Future<bool> chatJoinChannel(int c) async => (await _post('/api/chat/channels/join', q: {'c': c}))?['ok'] == true;
  Future<Map<String, dynamic>?> chatCatchUp(int c, int since) => _getMap('/api/chat/catchup', {'c': c, 'since': since});

  /// The assistant (owner 2026-11-19): start a summary or a question about the conversation → the job's id, or null.
  Future<String?> chatAssist(int c, String mode, {String? question}) async {
    final d = await _post('/api/chat/assist', form: {'c': c, 'mode': mode, if (question != null) 'question': question});
    return d?['ok'] == true ? d!['job'] as String? : null;
  }
  /// A video message recorded in the app, with its first picture beside it when there is one.
  Future<bool> chatSendVideo(int conversationId, String path, {List<int>? poster}) async {
    try {
      final form = FormData.fromMap({
        'c': conversationId,
        'file': await MultipartFile.fromFile(path, filename: 'video.mp4', contentType: DioMediaType('video', 'mp4')),
        if (poster != null && poster.isNotEmpty) 'poster': poster.first == 0x89   // PNG's signature; anything else is the camera's JPEG
            ? MultipartFile.fromBytes(poster, filename: 'poster.png', contentType: DioMediaType('image', 'png'))
            : MultipartFile.fromBytes(poster, filename: 'poster.jpg', contentType: DioMediaType('image', 'jpeg')),
      });
      final r = await _dio.post('/api/chat/video', data: form, options: Options(sendTimeout: const Duration(minutes: 5), receiveTimeout: const Duration(minutes: 2)));
      return r.statusCode == 200;
    } on DioException {
      return false;
    }
  }

  /// A task from a message (the assistant's "make it a task"): assigned to me unless someone else is named. (ok, error)
  Future<(bool, String?)> chatMakeTask(int messageId, String title, {int? assignee}) async {
    try {
      final d = (await _dio.post('/api/chat/task', data: FormData.fromMap({'id': messageId, 'title': title, if (assignee != null) 'assignee': assignee}))).data as Map;
      return (d['ok'] == true, d['error'] as String?);
    } on DioException {
      return (false, null);
    }
  }

  /// The job as it stands: {status: working|done|failed, result}.
  Future<Map<String, dynamic>?> chatAssistStatus(String job) => _getMap('/api/chat/assist', {'job': job});
  /// One message in the other language (made once on the server and kept). (text, lang) or null.
  Future<(String, String)?> chatTranslate(int id) async {
    try {
      final d = (await _dio.post('/api/chat/translate', queryParameters: {'id': id})).data as Map;
      if (d['ok'] == true) return ('${d['text']}', '${d['lang']}');
      final job = d['job'];
      if (d['code'] != 'working' || job is! String) return null;
      // made in its turn (a summary may be using the model): follow the job, at most ten minutes
      for (var i = 0; i < 200; i++) {
        await Future.delayed(const Duration(seconds: 3));
        final st = await chatAssistStatus(job);
        if (st == null || st['status'] == 'working') continue;
        final r = st['result'];
        return st['status'] == 'done' && r is Map && r['text'] != null ? ('${r['text']}', '${r['lang']}') : null;
      }
      return null;
    } on DioException {
      return null;
    }
  }

  String chatFileUrl(int messageId) => '${AppConfig.apiBaseUrl}/api/chat/file?messageId=$messageId';
}
