import 'package:flutter/foundation.dart' show kIsWeb;

/// Central app configuration.
///
/// ── PRODUCTION ──────────────────────────────────────────────────────────
/// Build with the real HTTPS host:
///   flutter build apk --release --dart-define=API_BASE=https://erp.yourcompany.com
/// The server must be reachable over HTTPS (valid certificate) — SignalR and JWT login both run over the same host.
///
/// ── DEVELOPMENT ─────────────────────────────────────────────────────────
///  - API_BASE given            -> that host (e.g. --dart-define=API_BASE=https://localhost:7183)
///  - Web (Chrome on this PC)   -> http://localhost:5000
///  - Android emulator          -> http://10.0.2.2:5000   (10.0.2.2 = host machine)
///  - Real device               -> pass API_BASE=http://<your-PC-LAN-IP>:5000  (same Wi-Fi)
class AppConfig {
  /// The API host, set at build time. Empty means "development default".
  static const String apiBase = String.fromEnvironment('API_BASE', defaultValue: '');

  static String get apiBaseUrl {
    if (apiBase.isNotEmpty) return apiBase;
    return kIsWeb ? 'http://localhost:5000' : 'http://10.0.2.2:5000';
  }

  /// Where employee profile images are served from (wwwroot).
  static String get mediaBaseUrl => apiBaseUrl;
}
