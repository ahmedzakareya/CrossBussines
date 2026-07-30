import 'package:flutter/material.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// Holds the selected app language and persists it.
class LocaleProvider extends ChangeNotifier {
  Locale _locale = const Locale('ar');
  static const _key = 'app_locale';

  Locale get locale => _locale;

  Future<void> load() async {
    final prefs = await SharedPreferences.getInstance();
    final code = prefs.getString(_key);
    if (code != null) _locale = Locale(code);
  }

  Future<void> setLocale(Locale locale) async {
    _locale = locale;
    notifyListeners();
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_key, locale.languageCode);
  }

  Future<void> toggle() async {
    await setLocale(
        _locale.languageCode == 'ar' ? const Locale('en') : const Locale('ar'));
  }
}
