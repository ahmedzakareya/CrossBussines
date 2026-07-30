import 'package:flutter/foundation.dart' show kIsWeb;

/// Central app configuration.
///
/// ── PRODUCTION ──────────────────────────────────────────────────────────
/// Set [useProduction] = true and put your real HTTPS domain in [prodBaseUrl],
/// then rebuild the app (`flutter build apk --release` / `flutter build web`).
/// The server must be reachable over HTTPS (valid certificate) — SignalR and
/// JWT login both run over the same host.
///
/// ── DEVELOPMENT (useProduction = false) ─────────────────────────────────
///  - Web (Chrome on this PC) -> http://localhost:5000
///  - Android emulator        -> http://10.0.2.2:5000   (10.0.2.2 = host machine)
///  - Real device             -> http://<your-PC-LAN-IP>:5000  (same Wi-Fi)
class AppConfig {
  /// Flip to true for production builds.
  /// Can also be overridden at build time:
  ///   flutter build apk --release --dart-define=PRODUCTION=true
  static const bool useProduction =
      bool.fromEnvironment('PRODUCTION', defaultValue: false);

  /// REAL production host — replace with your domain (keep the https:// scheme,
  /// no trailing slash). Example: 'https://erp.yourcompany.com'
  static const String prodBaseUrl = 'https://YOUR-DOMAIN.com';

  static String get apiBaseUrl {
    if (useProduction) return prodBaseUrl;
    return kIsWeb ? 'http://localhost:5000' : 'http://10.0.2.2:5000';
  }

  /// Where employee profile images are served from (wwwroot).
  static String get mediaBaseUrl => apiBaseUrl;
}
