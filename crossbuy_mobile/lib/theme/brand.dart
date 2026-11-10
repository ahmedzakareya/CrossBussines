import 'package:flutter/material.dart';

/// The CrossBusiness identity on a phone (owner-approved prototype, design-output/mobile-prototype).
/// Tokens follow crossbuy-brand.css. No amber anywhere: attention is red, information is brand blue, success green.
class Brand {
  static const brand = Color(0xFF3A5795);
  static const deep = Color(0xFF1F2D4D);
  static const tint = Color(0xFFE7F0FF);
  static const tint2 = Color(0xFFF3F7FF);
  static const blue300 = Color(0xFF7DA9FF);
  static const ink = Color(0xFF0F172A);
  static const ink2 = Color(0xFF334155);
  static const muted = Color(0xFF64748B);
  static const line = Color(0xFFE2E8F0);
  static const bg = Color(0xFFF4F6FB);
  static const ok = Color(0xFF15803D);
  static const okTint = Color(0xFFDCFCE7);
  static const bad = Color(0xFFDC2626);
  static const badTint = Color(0xFFFEE2E2);
  static const violet = Color(0xFF7C3AED);
  static const violetTint = Color(0xFFEDE9FE);

  static const heroGradient = LinearGradient(
    begin: AlignmentDirectional.topStart,
    end: AlignmentDirectional.bottomEnd,
    colors: [deep, brand],
  );

  static ThemeData theme() {
    final scheme = ColorScheme.fromSeed(seedColor: brand, primary: brand, secondary: deep, error: bad, surface: Colors.white);
    return ThemeData(
      useMaterial3: true,
      colorScheme: scheme,
      scaffoldBackgroundColor: bg,
      fontFamily: 'Cairo',
      appBarTheme: const AppBarTheme(
        backgroundColor: Colors.white, foregroundColor: ink, elevation: 0, centerTitle: false, surfaceTintColor: Colors.white,
        titleTextStyle: TextStyle(fontFamily: 'Cairo', fontSize: 18, fontWeight: FontWeight.w700, color: ink),
      ),
      cardTheme: CardThemeData(
        color: Colors.white, elevation: 0, margin: EdgeInsets.zero,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(18)),
      ),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          backgroundColor: brand, minimumSize: const Size(0, 48),
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14)),
          textStyle: const TextStyle(fontFamily: 'Cairo', fontWeight: FontWeight.w700, fontSize: 15),
        ),
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true, fillColor: Colors.white,
        border: OutlineInputBorder(borderRadius: BorderRadius.circular(14), borderSide: const BorderSide(color: line, width: 1.5)),
        enabledBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(14), borderSide: const BorderSide(color: line, width: 1.5)),
        focusedBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(14), borderSide: const BorderSide(color: blue300, width: 1.5)),
      ),
      navigationBarTheme: NavigationBarThemeData(
        backgroundColor: Colors.white, indicatorColor: tint, elevation: 0,
        labelTextStyle: WidgetStateProperty.resolveWith((s) => TextStyle(
            fontFamily: 'Cairo', fontSize: 11.5, fontWeight: FontWeight.w600, color: s.contains(WidgetState.selected) ? brand : muted)),
      ),
    );
  }
}

/// Money in the pack's currency. Three decimals for the three-decimal currencies (KWD, BHD, OMR, JOD).
String money(num v, String currency) {
  final dec = const {'KWD', 'BHD', 'OMR', 'JOD'}.contains(currency) ? 3 : 2;
  final s = v.toStringAsFixed(dec);
  final parts = s.split('.');
  final whole = parts[0].replaceAllMapped(RegExp(r'\B(?=(\d{3})+(?!\d))'), (m) => ',');
  return parts.length > 1 ? '$whole.${parts[1]}' : whole;
}
