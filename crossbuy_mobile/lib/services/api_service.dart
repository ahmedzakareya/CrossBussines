import 'package:dio/dio.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../app_config.dart';
import 'app_nav.dart';
import '../models/app_notification.dart';
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
  }) async {
    try {
      final res = await _dio.post('/api/leave', data: {
        'leaveTypeID': leaveTypeId,
        'startDate': startDate.toIso8601String(),
        'endDate': endDate.toIso8601String(),
        'reason': reason,
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

  Future<List<MyRequest>> getMyRequests() async {
    final res = await _dio.get('/api/ess/requests');
    return _list(res.data).map(MyRequest.fromJson).toList();
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
  Future<(RepCustomer, String, bool, bool)> getRepCustomer(int id) async {
    final res = await _dio.get('/api/rep/customers/$id');
    return (RepCustomer.fromJson(res.data['data'] as Map<String, dynamic>), (res.data['currency'] as String?) ?? '', res.data['canOrder'] == true, res.data['canCollect'] == true);
  }

  Map<String, dynamic> _order(int customerId, Map<int, double> qty, String? notes) => {
        'customerId': customerId, 'notes': notes,
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
  Future<(bool, String?, String?)> saveOrder(int customerId, Map<int, double> qty, {String? notes}) async {
    try {
      final res = await _dio.post('/api/rep/order', data: _order(customerId, qty, notes));
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
}
