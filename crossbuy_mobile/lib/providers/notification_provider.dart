import 'dart:async';
import 'package:flutter/foundation.dart';
import 'package:signalr_netcore/signalr_client.dart';

import '../app_config.dart';
import '../models/app_notification.dart';
import '../services/api_service.dart';
import '../widgets/in_app_toast.dart';

/// Loads notifications from the API and keeps them live via the SignalR hub,
/// with a polling fallback (every 15s) so it works even across server instances
/// or if the realtime socket drops.
class NotificationProvider extends ChangeNotifier {
  final List<AppNotification> _items = [];
  int _unread = 0;
  int _maxId = 0;
  HubConnection? _hub;
  Timer? _poll;

  List<AppNotification> get items => List.unmodifiable(_items);
  int get unread => _unread;

  /// Initial load (no toast for existing items).
  Future<void> load() async {
    await _refresh(toastNew: false);
    _poll ??= Timer.periodic(const Duration(seconds: 15), (_) => _refresh(toastNew: true));
  }

  Future<void> _refresh({required bool toastNew}) async {
    try {
      final (u, list) = await ApiService.instance.getNotifications();
      if (toastNew) {
        // newest-first; anything above the last seen id is new → pop a toast
        for (final n in list.where((e) => e.id > _maxId)) {
          showInAppToast(n);
        }
      }
      _items
        ..clear()
        ..addAll(list);
      _unread = u;
      if (list.isNotEmpty && list.first.id > _maxId) _maxId = list.first.id;
      notifyListeners();
    } catch (_) {/* offline / not logged in */}
  }

  Future<void> connect() async {
    if (_hub != null) return;
    try {
      final hub = HubConnectionBuilder()
          .withUrl(
            '${AppConfig.apiBaseUrl}/hubs/notifications',
            options: HttpConnectionOptions(
              accessTokenFactory: () async => ApiService.instance.token ?? '',
            ),
          )
          .withAutomaticReconnect()
          .build();

      hub.on('notification', (args) {
        if (args != null && args.isNotEmpty && args[0] is Map) {
          final n = AppNotification.fromJson(Map<String, dynamic>.from(args[0] as Map));
          if (_items.any((e) => e.id == n.id)) return; // dedupe vs polling
          _items.insert(0, n);
          _unread++;
          if (n.id > _maxId) _maxId = n.id;
          notifyListeners();
          showInAppToast(n);
        }
      });

      await hub.start();
      _hub = hub;
    } catch (_) {/* hub unavailable — polling still covers it */}
  }

  Future<void> markAllRead() async {
    await ApiService.instance.markAllNotificationsRead();
    for (final n in _items) {
      n.isRead = true;
    }
    _unread = 0;
    notifyListeners();
  }

  Future<void> shutdown() async {
    _poll?.cancel();
    _poll = null;
    await _hub?.stop();
    _hub = null;
  }

  /// Called on logout: drop the hub + polling + clear state for the next user.
  Future<void> reset() async {
    await shutdown();
    _items.clear();
    _unread = 0;
    _maxId = 0;
    notifyListeners();
  }
}
