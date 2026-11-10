import 'package:flutter/material.dart';

import '../app_config.dart';
import '../l10n/app_localizations.dart';
import '../theme/brand.dart';

/// Shared building blocks of the redesigned app (owner-approved prototype design-output/mobile-prototype).

/// Bilingual text inline: L(context, 'عربي', 'English'). New screens use this rather than growing the key map.
String L(BuildContext context, String ar, String en) => AppLocalizations.of(context).isAr ? ar : en;
bool isAr(BuildContext context) => AppLocalizations.of(context).isAr;

String mediaUrl(String? path) =>
    (path == null || path.isEmpty) ? '' : (path.startsWith('http') ? path : '${AppConfig.mediaBaseUrl}$path');

/// A person's photo, or their initials when there is none (or it fails to load).
class PersonAvatar extends StatelessWidget {
  final String? photo;
  final String name;
  final double size;
  const PersonAvatar({super.key, this.photo, required this.name, this.size = 40});

  String get _initials {
    final w = name.trim().split(RegExp(r'\s+')).where((x) => x.isNotEmpty).take(2).map((x) => x[0]).join();
    return w.isEmpty ? '?' : w.toUpperCase();
  }

  @override
  Widget build(BuildContext context) {
    final url = mediaUrl(photo);
    final fallback = Container(
      width: size, height: size, alignment: Alignment.center,
      decoration: const BoxDecoration(color: Brand.tint, shape: BoxShape.circle),
      child: Text(_initials, style: TextStyle(color: Brand.brand, fontWeight: FontWeight.w700, fontSize: size * .34)),
    );
    if (url.isEmpty) return fallback;
    return ClipOval(
      child: Image.network(url, width: size, height: size, fit: BoxFit.cover, errorBuilder: (_, __, ___) => fallback),
    );
  }
}

/// The blue band that opens Home and Me.
class HeroBand extends StatelessWidget {
  final Widget child;
  final double bottom;
  const HeroBand({super.key, required this.child, this.bottom = 64});
  @override
  Widget build(BuildContext context) => Container(
        width: double.infinity,
        decoration: const BoxDecoration(gradient: Brand.heroGradient),
        padding: EdgeInsetsDirectional.fromSTEB(20, MediaQuery.of(context).padding.top + 14, 20, bottom),
        child: DefaultTextStyle.merge(style: const TextStyle(color: Colors.white), child: child),
      );
}

/// A white rounded card with an optional heading row.
class SectionCard extends StatelessWidget {
  final String? title;
  final Widget? action;
  final Widget child;
  final EdgeInsetsGeometry padding;
  final VoidCallback? onTap;
  const SectionCard({super.key, this.title, this.action, required this.child, this.padding = const EdgeInsets.all(16), this.onTap});

  @override
  Widget build(BuildContext context) {
    final body = Padding(
      padding: padding,
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        if (title != null)
          Padding(
            padding: const EdgeInsets.only(bottom: 12),
            child: Row(children: [
              Expanded(child: Text(title!, style: const TextStyle(fontSize: 15.5, fontWeight: FontWeight.w700, color: Brand.ink))),
              if (action != null) action!,
            ]),
          ),
        child,
      ]),
    );
    return Container(
      margin: const EdgeInsets.only(bottom: 14),
      decoration: BoxDecoration(color: Colors.white, borderRadius: BorderRadius.circular(18), boxShadow: const [
        BoxShadow(color: Color(0x0F0F172A), blurRadius: 14, offset: Offset(0, 4)),
      ]),
      child: Material(color: Colors.transparent, child: InkWell(borderRadius: BorderRadius.circular(18), onTap: onTap, child: body)),
    );
  }
}

/// A small status label. Red for attention, green for done, brand blue for information — never amber.
class Pill extends StatelessWidget {
  final String text;
  final Color fg, bg;
  const Pill(this.text, {super.key, this.fg = Brand.brand, this.bg = Brand.tint});
  const Pill.ok(this.text, {super.key}) : fg = Brand.ok, bg = Brand.okTint;
  const Pill.bad(this.text, {super.key}) : fg = Brand.bad, bg = Brand.badTint;
  const Pill.violet(this.text, {super.key}) : fg = Brand.violet, bg = Brand.violetTint;
  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(99)),
        child: Text(text, style: TextStyle(color: fg, fontSize: 11.5, fontWeight: FontWeight.w600)),
      );
}

/// A rounded square icon.
class IconTile extends StatelessWidget {
  final IconData icon;
  final Color fg, bg;
  final double size;
  const IconTile(this.icon, {super.key, this.fg = Brand.brand, this.bg = Brand.tint2, this.size = 42});
  @override
  Widget build(BuildContext context) => Container(
        width: size, height: size,
        decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(size * .32)),
        child: Icon(icon, color: fg, size: size * .5),
      );
}

/// A list row: leading, title, subtitle, trailing.
class RowItem extends StatelessWidget {
  final Widget leading;
  final String title;
  final String? subtitle;
  final Widget? trailing;
  final VoidCallback? onTap;
  const RowItem({super.key, required this.leading, required this.title, this.subtitle, this.trailing, this.onTap});
  @override
  Widget build(BuildContext context) => InkWell(
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 10),
          child: Row(children: [
            leading,
            const SizedBox(width: 12),
            Expanded(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(title, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 14, color: Brand.ink)),
                if (subtitle != null)
                  Text(subtitle!, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12, color: Brand.muted)),
              ]),
            ),
            if (trailing != null) trailing!,
          ]),
        ),
      );
}

/// A page with the light app bar, used for every screen pushed from a tab.
class PageFrame extends StatelessWidget {
  final String title;
  final Widget child;
  final List<Widget>? actions;
  const PageFrame({super.key, required this.title, required this.child, this.actions});
  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: Text(title), actions: actions, shape: const Border(bottom: BorderSide(color: Brand.line))),
        body: child,
      );
}

/// The friendly empty state.
class EmptyNote extends StatelessWidget {
  final IconData icon;
  final String title, text;
  const EmptyNote({super.key, this.icon = Icons.thumb_up_alt_outlined, required this.title, required this.text});
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 24),
        child: Column(children: [
          Icon(icon, size: 36, color: Brand.ok),
          const SizedBox(height: 8),
          Text(title, style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 15.5)),
          const SizedBox(height: 2),
          Text(text, textAlign: TextAlign.center, style: const TextStyle(color: Brand.muted, fontSize: 12.5)),
        ]),
      );
}

/// Month name, localized.
String monthName(BuildContext context, int m) {
  const ar = ['', 'يناير', 'فبراير', 'مارس', 'أبريل', 'مايو', 'يونيو', 'يوليو', 'أغسطس', 'سبتمبر', 'أكتوبر', 'نوفمبر', 'ديسمبر'];
  const en = ['', 'January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
  return isAr(context) ? ar[m] : en[m];
}

String ymd(DateTime d) => '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';
