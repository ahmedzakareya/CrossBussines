import 'package:flutter/material.dart';

/// The CrossBusiness identity on a phone — the OWNER'S REFERENCE DESIGN "CrossBusiness Executive" (2026-11-10,
/// design-output/reference-cairo; extended to every screen in design-output/executive-prototype). Every value here is
/// read from that reference's stylesheet, not chosen: navy header, blue, support blue, a THIN gold accent (the active
/// tab bar, the marker on the main card, status dots — never a button or a notice background), page #F5F7FB, white
/// cards with a 1px line, Cairo only.
class Brand {
  // ---- the reference's tokens (#cb-executive) ----
  static const navy = Color(0xFF1F2D4D);
  static const blue = Color(0xFF3A5795);
  static const support = Color(0xFF637BAD);
  static const gold = Color(0xFFD4AF37);
  static const goldInk = Color(0xFF806619);   // the "awaiting your approval" label
  static const paper = Colors.white;
  static const page = Color(0xFFF5F7FB);
  static const inkC = Color(0xFF1F2D4D);
  static const mutedC = Color(0xFF738098);
  static const lineC = Color(0xFFE8ECF3);
  static const soft = Color(0xFFEDF1F8);
  static const fils = Color(0xFF9AA6BB);     // the fils part of an amount
  static const okC = Color(0xFF1F7A4D);
  static const okSoft = Color(0xFFE8F4EE);
  static const badC = Color(0xFFB4322E);
  static const badSoft = Color(0xFFFBECEC);
  // header (on navy)
  static const onNavy = Color(0xFFE9EEF9);
  static const onNavyMuted = Color(0xFFACBAD3);

  // ---- names the older screens use, mapped onto the reference palette ----
  static const brand = blue;
  static const deep = navy;
  static const tint = soft;
  static const tint2 = Color(0xFFF5F7FB);
  static const blue300 = support;
  static const ink = inkC;
  static const ink2 = Color(0xFF3A4660);
  static const muted = mutedC;
  static const line = lineC;
  static const bg = page;
  static const ok = okC;
  static const okTint = okSoft;
  static const bad = badC;
  static const badTint = badSoft;
  static const violet = Color(0xFF5B4DB1);
  static const violetTint = Color(0xFFEEEBFA);
  static const heroGradient = LinearGradient(colors: [navy, navy]);

  static ThemeData theme() {
    final scheme = ColorScheme.fromSeed(seedColor: blue, primary: blue, secondary: navy, error: badC, surface: paper)
        .copyWith(outline: lineC, outlineVariant: lineC, surfaceContainerHighest: soft);
    final r12 = RoundedRectangleBorder(borderRadius: BorderRadius.circular(12));
    OutlineInputBorder field(Color c) => OutlineInputBorder(borderRadius: BorderRadius.circular(14), borderSide: BorderSide(color: c));
    return ThemeData(
      useMaterial3: true,
      colorScheme: scheme,
      scaffoldBackgroundColor: page,
      fontFamily: 'Cairo',
      splashFactory: InkRipple.splashFactory,
      highlightColor: Colors.transparent,
      hoverColor: Colors.transparent,
      splashColor: soft,
      dividerTheme: const DividerThemeData(color: lineC, thickness: 1, space: 1),
      appBarTheme: const AppBarTheme(
        backgroundColor: navy, foregroundColor: Colors.white, elevation: 0, scrolledUnderElevation: 0, surfaceTintColor: navy,
        titleTextStyle: TextStyle(fontFamily: 'Cairo', fontSize: 16, fontWeight: FontWeight.w600, color: Colors.white),
      ),
      // the reference's primary action: blue, radius 12, 13px padding, 12px label
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          backgroundColor: blue, foregroundColor: Colors.white, disabledBackgroundColor: soft, disabledForegroundColor: mutedC,
          minimumSize: const Size(0, 46), shape: r12, elevation: 0,
          textStyle: const TextStyle(fontFamily: 'Cairo', fontSize: 13, fontWeight: FontWeight.w600),
        ),
      ),
      // ...and its secondary: white with the 1px line
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          foregroundColor: inkC, backgroundColor: paper, side: const BorderSide(color: lineC), minimumSize: const Size(0, 46), shape: r12,
          textStyle: const TextStyle(fontFamily: 'Cairo', fontSize: 13, fontWeight: FontWeight.w500),
        ),
      ),
      textButtonTheme: TextButtonThemeData(style: TextButton.styleFrom(foregroundColor: blue, textStyle: const TextStyle(fontFamily: 'Cairo', fontSize: 11.5))),
      inputDecorationTheme: InputDecorationTheme(
        filled: true, fillColor: paper, isDense: true,
        contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 13),
        hintStyle: const TextStyle(color: mutedC, fontSize: 12.5),
        labelStyle: const TextStyle(color: mutedC, fontSize: 12),
        floatingLabelStyle: const TextStyle(color: blue, fontSize: 12),
        prefixIconColor: support, suffixIconColor: support,
        border: field(lineC), enabledBorder: field(lineC), focusedBorder: field(support), disabledBorder: field(lineC),
      ),
      segmentedButtonTheme: SegmentedButtonThemeData(
        style: SegmentedButton.styleFrom(
          backgroundColor: paper, foregroundColor: mutedC, selectedBackgroundColor: navy, selectedForegroundColor: Colors.white,
          side: const BorderSide(color: lineC), shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
          textStyle: const TextStyle(fontFamily: 'Cairo', fontSize: 11.5),
        ),
      ),
      snackBarTheme: SnackBarThemeData(
        behavior: SnackBarBehavior.floating, backgroundColor: navy, elevation: 0, shape: r12,
        contentTextStyle: const TextStyle(fontFamily: 'Cairo', color: Colors.white, fontSize: 12.5),
      ),
      dialogTheme: DialogThemeData(backgroundColor: paper, surfaceTintColor: paper, shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(22))),
      bottomSheetTheme: const BottomSheetThemeData(backgroundColor: paper, surfaceTintColor: paper, dragHandleColor: lineC,
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(24)))),
      progressIndicatorTheme: const ProgressIndicatorThemeData(color: blue, circularTrackColor: soft),
      badgeTheme: const BadgeThemeData(backgroundColor: gold, textColor: navy),
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

/// The currency as the reference prints it beside a figure (Arabic: د.ك).
String curLabel(String currency, bool ar) {
  if (!ar) return currency;
  return const {'KWD': 'د.ك', 'SAR': 'ر.س', 'AED': 'د.إ', 'EGP': 'ج.م', 'BHD': 'د.ب', 'OMR': 'ر.ع', 'QAR': 'ر.ق', 'JOD': 'د.أ'}[currency] ?? currency;
}
