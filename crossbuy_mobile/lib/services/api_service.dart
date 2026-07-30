import 'package:dio/dio.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../app_config.dart';
import 'app_nav.dart';
import '../models/app_notification.dart';
import '../models/dashboard.dart';
import '../models/employee.dart';
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
        handler.next(options);
      },
    ));
  }

  static final ApiService instance = ApiService._();

  late final Dio _dio;
  String? _token;
  static const _tokenKey = 'auth_token';

  String? get token => _token;
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

  Future<FinanceSummary> getFinanceSummary({int companyId = 1}) async {
    final res = await _dio.get('/api/acc/summary', queryParameters: {'companyId': companyId});
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

  // ---- Notifications ----

  /// Returns (unreadCount, list).
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
