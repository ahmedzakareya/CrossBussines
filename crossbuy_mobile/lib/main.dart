import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:provider/provider.dart';

import 'l10n/app_localizations.dart';
import 'providers/auth_provider.dart';
import 'providers/calls_provider.dart';
import 'providers/locale_provider.dart';
import 'providers/notification_provider.dart';
import 'screens/shell.dart';
import 'screens/login_screen.dart';
import 'services/app_nav.dart';
import 'services/dev_certificate.dart';
import 'theme/brand.dart';

void main() async {
  WidgetsFlutterBinding.ensureInitialized();
  trustDevCertificate();   // debug builds only: the development server's own certificate (10.0.2.2 / localhost)

  final locale = LocaleProvider();
  final auth = AuthProvider();
  await Future.wait([locale.load(), auth.bootstrap()]);
  final calls = CallsProvider()..lang = locale.locale.languageCode;
  locale.addListener(() => calls.lang = locale.locale.languageCode);
  // signed out anywhere (the account tab, a 401): leave any call and close the calls connection
  auth.addListener(() { if (!auth.isLoggedIn) calls.reset(); });

  runApp(
    MultiProvider(
      providers: [
        ChangeNotifierProvider.value(value: locale),
        ChangeNotifierProvider.value(value: auth),
        ChangeNotifierProvider(create: (_) => NotificationProvider()),
        ChangeNotifierProvider.value(value: calls),
      ],
      child: const CrossBuyApp(),
    ),
  );
}

class CrossBuyApp extends StatelessWidget {
  const CrossBuyApp({super.key});

  @override
  Widget build(BuildContext context) {
    final localeProvider = context.watch<LocaleProvider>();
    final auth = context.watch<AuthProvider>();

    return MaterialApp(
      title: 'CrossBuy',
      debugShowCheckedModeBanner: false,
      navigatorKey: AppNav.navigatorKey,
      locale: localeProvider.locale,
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: const [
        AppLocalizations.delegate,
        GlobalMaterialLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
      ],
      theme: Brand.theme(),
      home: auth.isLoggedIn ? const Shell() : const LoginScreen(),
    );
  }
}
