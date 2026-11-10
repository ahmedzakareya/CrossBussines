import 'package:flutter/material.dart';

import '../services/api_service.dart';

/// Tracks authentication state and drives navigation between login/home.
class AuthProvider extends ChangeNotifier {
  final ApiService _api = ApiService.instance;

  AuthProvider() {
    // A 401 anywhere (expired / revoked token) signs out inside ApiService; this sends the app back to login.
    _api.onUnauthorized = notifyListeners;
  }

  bool _loading = false;
  bool get loading => _loading;
  bool get isLoggedIn => _api.isLoggedIn;

  Future<void> bootstrap() async {
    await _api.loadToken();
    notifyListeners();
  }

  Future<bool> login(String username, String password) async {
    _loading = true;
    notifyListeners();
    final ok = await _api.login(username, password);
    _loading = false;
    notifyListeners();
    return ok;
  }

  Future<void> logout() async {
    await _api.logout();
    notifyListeners();
  }
}
