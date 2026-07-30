import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:provider/provider.dart';

import 'l10n/app_localizations.dart';
import 'providers/auth_provider.dart';
import 'providers/locale_provider.dart';
import 'providers/notification_provider.dart';
import 'screens/home_screen.dart';
import 'screens/login_screen.dart';
import 'services/app_nav.dart';

void main() async {
  WidgetsFlutterBinding.ensureInitialized();

  final locale = LocaleProvider();
  final auth = AuthProvider();
  await Future.wait([locale.load(), auth.bootstrap()]);

  runApp(
    MultiProvider(
      providers: [
        ChangeNotifierProvider.value(value: locale),
        ChangeNotifierProvider.value(value: auth),
        ChangeNotifierProvider(create: (_) => NotificationProvider()),
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

    const seed = Color(0xFF1B84FF); // Metronic primary blue

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
      theme: ThemeData(
        useMaterial3: true,
        colorSchemeSeed: seed,
        scaffoldBackgroundColor: const Color(0xFFF5F8FA),
        appBarTheme: const AppBarTheme(centerTitle: true),
        inputDecorationTheme: const InputDecorationTheme(
          border: OutlineInputBorder(),
          filled: true,
          fillColor: Colors.white,
        ),
      ),
      home: auth.isLoggedIn ? const HomeScreen() : const LoginScreen(),
    );
  }
}
