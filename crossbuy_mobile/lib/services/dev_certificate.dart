import 'dart:io' show HttpOverrides, HttpClient, SecurityContext, X509Certificate;

import 'package:flutter/foundation.dart';

import '../app_config.dart';

/// DEBUG BUILDS ONLY. The development server (dotnet run, https://localhost:7183) serves its own development
/// certificate, which an Android emulator does not trust. In a debug build — and only for the development hosts
/// (10.0.2.2 = the emulator's host machine, localhost) — that certificate is accepted so the app can reach it.
/// A release build trusts exactly what the device trusts (the hospital's internal CA, installed by MDM).
void trustDevCertificate() {
  if (!kDebugMode || kIsWeb) return;
  HttpOverrides.global = _DevOverrides();
}

class _DevOverrides extends HttpOverrides {
  static const _devHosts = {'10.0.2.2', 'localhost', '127.0.0.1'};
  @override
  HttpClient createHttpClient(SecurityContext? context) => super.createHttpClient(context)
    ..badCertificateCallback = (X509Certificate cert, String host, int port) => _devHosts.contains(host) && AppConfig.apiBaseUrl.contains(host);
}
