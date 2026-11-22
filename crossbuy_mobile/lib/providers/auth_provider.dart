import 'package:flutter/material.dart';

import '../services/api_service.dart';
import '../services/push_service.dart';

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
    if (_api.isLoggedIn) PushService.register();   // a token may have rotated while the app was closed
  }

  Future<bool> login(String username, String password) async {
    _loading = true;
    notifyListeners();
    final ok = await _api.login(username, password);
    _loading = false;
    notifyListeners();
    if (ok) PushService.register();
    return ok;
  }

  Future<void> logout() async {
    await PushService.unregister();   // while still signed in: the server must know whose phone stops listening
    await _api.logout();
    notifyListeners();
  }
}
