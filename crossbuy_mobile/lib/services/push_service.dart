import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:dio/dio.dart';
import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_local_notifications/flutter_local_notifications.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../app_config.dart';
import '../screens/calls/calls_screen.dart';
import '../screens/chat/chat_screen.dart';
import 'api_service.dart';
import 'app_nav.dart';
import 'dev_certificate.dart';

/// PUSH WITH THE APP CLOSED (owner 2026-11-22: «Google FCM, no content»).
///
/// Google only WAKES the app: the message it carries holds the kind of news ("msg" or "call") and nothing else. The
/// woken app asks our own server what is new (api/chat/push/summary, with the stored sign-in) and draws the notices
/// itself — the conversation's name and an excerpt of the message, a ringing call, a missed call. So no message text
/// passes through Google.
///
/// Firebase is configured from the build (--dart-define FCM_PROJECT_ID / FCM_APP_ID / FCM_API_KEY / FCM_SENDER_ID, the
/// values of the Android app in the Firebase console). Without them push is simply off and nothing else changes.
class PushService {
  PushService._();

  static const _projectId = String.fromEnvironment('FCM_PROJECT_ID');
  static const _appId = String.fromEnvironment('FCM_APP_ID');
  static const _apiKey = String.fromEnvironment('FCM_API_KEY');
  static const _senderId = String.fromEnvironment('FCM_SENDER_ID');
  static bool get configured => _projectId.isNotEmpty && _appId.isNotEmpty && _apiKey.isNotEmpty && _senderId.isNotEmpty;

  static FirebaseOptions get _options => const FirebaseOptions(apiKey: _apiKey, appId: _appId, messagingSenderId: _senderId, projectId: _projectId);

  static final FlutterLocalNotificationsPlugin _local = FlutterLocalNotificationsPlugin();
  static const _msgChannel = AndroidNotificationChannel('cb_messages', 'الرسائل · Messages', description: 'رسائل المحادثات', importance: Importance.high);
  static const _callChannel = AndroidNotificationChannel('cb_calls', 'المكالمات · Calls', description: 'مكالمة واردة أو فائتة', importance: Importance.max);
  static const _callNoticeId = 900000001;
  static bool _ready = false;
  static String? _lastToken;
  static StreamSubscription<String>? _sub;

  /// At start (main): Firebase, the background handler, the notice channels, and what a tap on a notice opens.
  /// DEBUG BUILDS ONLY (--dart-define=PUSH_SELFTEST=true): no Firebase, but after signing in the app runs exactly what a
  /// wake-up runs (fetch the summary, draw the notices) after 15 s — time to put it in the background and look.
  static const _selfTest = bool.fromEnvironment('PUSH_SELFTEST');

  static Future<void> init() async {
    if (kDebugMode && _selfTest && !kIsWeb) { await _initLocal(onTap: _openFromPayload); _ready = true; return; }
    if (!configured || kIsWeb || _ready) return;
    try {
      await Firebase.initializeApp(options: _options);
      FirebaseMessaging.onBackgroundMessage(pushBackgroundHandler);
      await _initLocal(onTap: _openFromPayload);
      // a wake-up while the app is open: the live connection already shows it, except a call that rings before the
      // calls connection is back — the calls provider gets the ring from the hub as soon as it connects
      FirebaseMessaging.onMessage.listen((_) {});
      final launch = await _local.getNotificationAppLaunchDetails();
      final p = launch?.didNotificationLaunchApp == true ? launch?.notificationResponse?.payload : null;
      if (p != null) WidgetsBinding.instance.addPostFrameCallback((_) => Future.delayed(const Duration(milliseconds: 600), () => _openFromPayload(p)));
      _ready = true;
    } catch (e) {
      debugPrint('push: off ($e)');
    }
  }

  static Future<void> _initLocal({void Function(String payload)? onTap}) async {
    await _local.initialize(
      settings: const InitializationSettings(android: AndroidInitializationSettings('@mipmap/ic_launcher')),
      onDidReceiveNotificationResponse: (r) { if (r.payload != null) onTap?.call(r.payload!); },
    );
    final android = _local.resolvePlatformSpecificImplementation<AndroidFlutterLocalNotificationsPlugin>();
    await android?.createNotificationChannel(_msgChannel);
    await android?.createNotificationChannel(_callChannel);
  }

  /// After signing in (and at every start while signed in): ask for permission, register this phone's token.
  static Future<void> register() async {
    if (!_ready) return;
    if (kDebugMode && _selfTest) {
      await _local.resolvePlatformSpecificImplementation<AndroidFlutterLocalNotificationsPlugin>()?.requestNotificationsPermission();
      Timer(const Duration(seconds: 15), () => showSummary('msg'));
      return;
    }
    try {
      final m = FirebaseMessaging.instance;
      await m.requestPermission(alert: true, badge: true, sound: true);
      _sub ??= m.onRegistered.listen(_send);   // the token now and every time it rotates
      await m.register();
    } catch (e) {
      debugPrint('push: not registered ($e)');
    }
  }

  static Future<void> _send(String token) async {
    _lastToken = token;
    await ApiService.instance.pushRegister(token, Platform.isIOS ? 'ios' : 'android');
  }

  /// Before signing out: this phone stops receiving this person's notices.
  static Future<void> unregister() async {
    if (!_ready) return;
    try {
      final t = _lastToken;
      if (t != null) await ApiService.instance.pushUnregister(t);
      await _sub?.cancel(); _sub = null; _lastToken = null;
      await FirebaseMessaging.instance.unregister();
      await _local.cancelAll();
    } catch (_) {}
  }

  /// A tap on a notice: "c:<id>" opens that conversation, "call" opens the calls screen (the ring arrives there).
  static void _openFromPayload(String payload) {
    final nav = AppNav.navigatorKey.currentState;
    if (nav == null || !ApiService.instance.isLoggedIn) return;
    if (payload.startsWith('c:')) {
      final id = int.tryParse(payload.substring(2));
      if (id != null) nav.push(MaterialPageRoute(builder: (_) => ChatScreen(id: id)));
    } else if (payload == 'call' || payload == 'missed') {
      nav.push(MaterialPageRoute(builder: (_) => CallsScreen(tab: payload == 'missed' ? 1 : 0)));
    }
  }

  // ------------------------------------------------------------------------------------------ the woken app
  /// Runs in the background isolate: fetch what is new from our server and draw the notices.
  static Future<void> showSummary(String kind) async {
    final prefs = await SharedPreferences.getInstance();
    final token = prefs.getString('auth_token');
    if (token == null) return;                 // signed out on this phone: nothing to show
    final ar = (prefs.getString('app_locale') ?? 'ar') == 'ar';
    String l(String a, String e) => ar ? a : e;
    final dio = Dio(BaseOptions(baseUrl: AppConfig.apiBaseUrl, connectTimeout: const Duration(seconds: 10), receiveTimeout: const Duration(seconds: 15),
        headers: {'Authorization': 'Bearer $token', 'Accept-Language': ar ? 'ar' : 'en'}));
    Map<String, dynamic> s;
    try {
      final r = await dio.get('/api/chat/push/summary');
      s = (r.data is String ? jsonDecode(r.data as String) : r.data as Map).cast<String, dynamic>();
    } catch (e) {
      // the server cannot be reached (outside the network): say only that something arrived
      await _local.show(id: 1, title: l('رسالة جديدة', 'New message'), body: l('افتح التطبيق لعرضها', 'Open the app to see it'),
          notificationDetails: NotificationDetails(android: AndroidNotificationDetails(_msgChannel.id, _msgChannel.name, importance: Importance.high, priority: Priority.high)),
          payload: kind == 'call' ? 'call' : 'inbox');
      return;
    }

    final call = s['call'] as Map?;
    if (call != null) {
      final video = call['video'] == true;
      final who = '${(ar ? call['fromAr'] : call['from']) ?? call['from'] ?? ''}';
      await _local.show(id: _callNoticeId, title: who,
          body: video ? l('مكالمة فيديو واردة', 'Incoming video call') : l('مكالمة صوتية واردة', 'Incoming voice call'),
          notificationDetails: NotificationDetails(android: AndroidNotificationDetails(_callChannel.id, _callChannel.name,
              importance: Importance.max, priority: Priority.max, category: AndroidNotificationCategory.call, fullScreenIntent: true,
              ongoing: true, autoCancel: true, timeoutAfter: 45000, playSound: true)),
          payload: 'call');
    } else {
      await _local.cancel(id: _callNoticeId);
    }

    for (final m in (s['missed'] as List? ?? const []).whereType<Map>()) {
      final who = '${(ar ? m['fromAr'] : m['from']) ?? ''}';
      await _local.show(id: 800000000 + (m['callId'] as num).toInt(), title: l('مكالمة فائتة', 'Missed call'),
          body: m['video'] == true ? l('مكالمة فيديو من $who', 'Video call from $who') : l('مكالمة صوتية من $who', 'Voice call from $who'),
          notificationDetails: NotificationDetails(android: AndroidNotificationDetails(_callChannel.id, _callChannel.name, importance: Importance.high, priority: Priority.high,
              category: AndroidNotificationCategory.missedCall)),
          payload: 'missed');
    }

    for (final c in (s['messages'] as List? ?? const []).whereType<Map>()) {
      final id = (c['conversationId'] as num).toInt();
      final n = (c['unread'] as num?)?.toInt() ?? 1;
      final preview = '${c['preview'] ?? ''}';
      final body = n > 1 ? '$preview  ·  ${l('$n رسائل', '$n messages')}' : preview;
      await _local.show(id: id, title: '${c['title'] ?? ''}',
          body: body,
          notificationDetails: NotificationDetails(android: AndroidNotificationDetails(_msgChannel.id, _msgChannel.name,
              importance: Importance.high, priority: Priority.high, category: AndroidNotificationCategory.message,
              styleInformation: BigTextStyleInformation(body), groupKey: 'cb.chat')),
          payload: 'c:$id');
    }
  }

  /// In the background isolate the plugins start from nothing.
  static Future<void> ensureForBackground() async {
    await Firebase.initializeApp(options: _options);
    trustDevCertificate();
    await _initLocal();
  }
}

/// Google's wake-up, received while the app is in the background or closed. Top level, as the plugin requires.
@pragma('vm:entry-point')
Future<void> pushBackgroundHandler(RemoteMessage message) async {
  if (message.data['t'] != 'wake') return;
  WidgetsFlutterBinding.ensureInitialized();
  await PushService.ensureForBackground();
  await PushService.showSummary(message.data['k'] == 'call' ? 'call' : 'msg');
}
