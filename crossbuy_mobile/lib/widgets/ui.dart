import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons_300.dart';

import '../app_config.dart';
import '../l10n/app_localizations.dart';
import '../theme/brand.dart';

export 'package:lucide_icons_flutter/lucide_icons_300.dart';

/// Building blocks of the OWNER'S REFERENCE DESIGN "CrossBusiness Executive" (design-output/reference-cairo,
/// extended in design-output/executive-prototype). Sizes, radii and colours are the reference's own CSS values:
/// navy top band with the brand and the company row, 22px financial card with a gold marker and a soft footer,
/// 18px request cards with a 1px line, 15px quick tiles, data lists, Lucide icons at stroke 1.5 (weight 300), Cairo.

/// Lucide at the reference's stroke (1.5).
typedef Lu = LucideIcons300;

/// Bilingual text inline: L(context, 'عربي', 'English').
String L(BuildContext context, String ar, String en) => AppLocalizations.of(context).isAr ? ar : en;
bool isAr(BuildContext context) => AppLocalizations.of(context).isAr;

String mediaUrl(String? path) =>
    (path == null || path.isEmpty) ? '' : (path.startsWith('http') ? path : '${AppConfig.mediaBaseUrl}$path');

/// "Back" in the reading direction.
IconData backIcon(BuildContext context) => isAr(context) ? Lu.arrowRight : Lu.arrowLeft;
IconData forwardIcon(BuildContext context) => isAr(context) ? Lu.chevronLeft : Lu.chevronRight;

// ------------------------------------------------------------------------------------------------ text styles
class T {
  static const eyebrow = TextStyle(fontSize: 10, color: Brand.mutedC, height: 1.4);
  static const h1 = TextStyle(fontSize: 23, fontWeight: FontWeight.w600, letterSpacing: -.5, height: 1.5, color: Brand.inkC);
  static const h2 = TextStyle(fontSize: 14, fontWeight: FontWeight.w600, color: Brand.inkC);
  static const sub = TextStyle(fontSize: 11, color: Brand.mutedC, height: 1.5);
  static const label = TextStyle(fontSize: 11, color: Brand.mutedC);
  static const small = TextStyle(fontSize: 10, color: Brand.mutedC, height: 1.4);
  static const body = TextStyle(fontSize: 12, fontWeight: FontWeight.w500, color: Brand.inkC, height: 1.4);
}

// ------------------------------------------------------------------------------------------------ people
/// A person's photo, or their initials (one letter for an Arabic name) when there is none.
class PersonAvatar extends StatelessWidget {
  final String? photo;
  final String name;
  final double size;
  final bool ring;
  final double radius;   // 0 = circle
  const PersonAvatar({super.key, this.photo, required this.name, this.size = 40, this.ring = false, this.radius = 0});

  String get _initials {
    final words = name.trim().split(RegExp(r'\s+')).where((x) => x.isNotEmpty).toList();
    if (words.isEmpty) return '';
    final arabic = RegExp(r'[؀-ۿ]').hasMatch(words.first);
    return arabic ? words.take(2).map((x) => x[0]).join(' ') : words.take(2).map((x) => x[0]).join().toUpperCase();
  }

  @override
  Widget build(BuildContext context) {
    final url = mediaUrl(photo);
    final shape = radius == 0 ? BoxShape.circle : BoxShape.rectangle;
    final br = radius == 0 ? null : BorderRadius.circular(radius);
    final fallback = Container(
      width: size, height: size, alignment: Alignment.center,
      decoration: BoxDecoration(color: const Color(0xFFE5EAF4), shape: shape, borderRadius: br),
      child: Text(_initials, style: TextStyle(color: Brand.blue, fontSize: size * .32, fontWeight: FontWeight.w500)),
    );
    Widget img = url.isEmpty
        ? fallback
        : ClipRRect(
            borderRadius: br ?? BorderRadius.circular(size),
            child: Image.network(url, width: size, height: size, fit: BoxFit.cover, errorBuilder: (_, __, ___) => fallback),
          );
    if (!ring) return img;
    return Container(padding: const EdgeInsets.all(3), decoration: const BoxDecoration(color: Brand.paper, shape: BoxShape.circle), child: img);
  }
}

// ------------------------------------------------------------------------------------------------ the navy top
/// The reference's navy band: brand mark + bell, then the company row ("متصل") — or, inside a work mode, the
/// mode strip with its exit button.
class ExecTop extends StatelessWidget {
  final String company, branch;
  final VoidCallback? onBell;
  final bool bellDot;
  final (IconData, String, String)? mode;   // icon, title, subtitle
  final VoidCallback? onExitMode;
  const ExecTop({super.key, this.company = '', this.branch = '', this.onBell, this.bellDot = false, this.mode, this.onExitMode});

  @override
  Widget build(BuildContext context) {
    final top = MediaQuery.of(context).padding.top;
    Widget iconBox(IconData i, {Color bg = const Color(0x0CFFFFFF), Color fg = const Color(0xFFC5D1E7)}) => Container(
          width: 35, height: 38, alignment: Alignment.center,
          decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(9)),
          child: Icon(i, size: 17, color: fg),
        );
    final row = mode == null
        ? Row(children: [
            iconBox(Lu.building2),
            const SizedBox(width: 10),
            Expanded(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(company, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w500, color: Colors.white)),
                const SizedBox(height: 2),
                Text(branch, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 10, color: Brand.onNavyMuted)),
              ]),
            ),
            Row(children: [
              Container(width: 4, height: 4, decoration: const BoxDecoration(color: Brand.gold, shape: BoxShape.circle)),
              const SizedBox(width: 5),
              Text(L(context, 'متصل', 'Online'), style: const TextStyle(fontSize: 9, color: Color(0xFFDBD8BE))),
            ]),
          ])
        : Row(children: [
            iconBox(mode!.$1, bg: const Color(0x1FD4AF37), fg: const Color(0xFFE9D48D)),
            const SizedBox(width: 10),
            Expanded(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(mode!.$2, style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w500, color: Colors.white)),
                const SizedBox(height: 2),
                Text(mode!.$3, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 10, color: Brand.onNavyMuted)),
              ]),
            ),
            GestureDetector(
              onTap: onExitMode ?? () => Navigator.of(context).maybePop(),
              child: Container(
                padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
                decoration: BoxDecoration(color: const Color(0x0BFFFFFF), border: Border.all(color: const Color(0x1AFFFFFF)), borderRadius: BorderRadius.circular(10)),
                child: Row(children: [
                  const Icon(Lu.logOut, size: 13, color: Brand.onNavy),
                  const SizedBox(width: 5),
                  Text(L(context, 'خروج من الوضع', 'Exit mode'), style: const TextStyle(fontSize: 10, color: Brand.onNavy)),
                ]),
              ),
            ),
          ]);
    return Container(
      color: Brand.navy,
      padding: EdgeInsets.fromLTRB(23, top + 8, 23, 20),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Directionality(
          textDirection: TextDirection.ltr,
          child: Padding(
            padding: const EdgeInsets.only(top: 4, bottom: 22),
            child: Row(children: [
              Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                RichText(
                  text: const TextSpan(style: TextStyle(fontFamily: 'Cairo', fontSize: 23, letterSpacing: -1, color: Colors.white, height: 1.1), children: [
                    TextSpan(text: 'Cross', style: TextStyle(fontWeight: FontWeight.w800)),
                    TextSpan(text: 'Business', style: TextStyle(fontWeight: FontWeight.w500, color: Color(0xFFD4DFF5))),
                  ]),
                ),
                const SizedBox(height: 7),
                const Text('BUSINESS, CONNECTED.', style: TextStyle(fontSize: 7, letterSpacing: 2.6, fontWeight: FontWeight.w500, color: Color(0xFFBDC9E0))),
              ]),
              const Spacer(),
              GestureDetector(
                onTap: onBell,
                child: Container(
                  width: 40, height: 40, alignment: Alignment.center,
                  decoration: BoxDecoration(color: const Color(0x0BFFFFFF), border: Border.all(color: const Color(0x1AFFFFFF)), shape: BoxShape.circle),
                  child: Stack(clipBehavior: Clip.none, children: [
                    const Icon(Lu.bell, size: 18, color: Brand.onNavy),
                    if (bellDot) Positioned(top: 0, right: 1, child: Container(width: 5, height: 5, decoration: const BoxDecoration(color: Brand.gold, shape: BoxShape.circle))),
                  ]),
                ),
              ),
            ]),
          ),
        ),
        row,
      ]),
    );
  }
}

/// Company and branch lines for the top band, shared by every screen (set once after sign-in).
class ExecSession {
  static String company = '';
  static String branch = '';
  static bool unread = false;
}

// ------------------------------------------------------------------------------------------------ page parts
/// Eyebrow · title · sub (the reference's page heading).
class PageHead extends StatelessWidget {
  final String? eyebrow, sub;
  final String title;
  const PageHead(this.title, {super.key, this.eyebrow, this.sub});
  @override
  Widget build(BuildContext context) => Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        if (eyebrow != null) Padding(padding: const EdgeInsets.only(bottom: 3), child: Text(eyebrow!, style: T.eyebrow)),
        Text(title, style: T.h1),
        if (sub != null) Padding(padding: const EdgeInsets.only(top: 6, bottom: 20), child: Text(sub!, style: T.sub)) else const SizedBox(height: 14),
      ]);
}

/// "← back to …" as a quiet text button (the reference's .back).
class BackLink extends StatelessWidget {
  final String label;
  final VoidCallback? onTap;
  const BackLink(this.label, {super.key, this.onTap});
  @override
  Widget build(BuildContext context) => GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: onTap ?? () => Navigator.of(context).maybePop(),
        child: Padding(
          padding: const EdgeInsets.only(bottom: 20),
          child: Row(children: [
            Icon(backIcon(context), size: 15, color: Brand.mutedC),
            const SizedBox(width: 7),
            Text(label, style: const TextStyle(fontSize: 11, color: Brand.mutedC)),
          ]),
        ),
      );
}

/// Heading of a section: title, optional count, optional text action with an arrow.
class SectionHeading extends StatelessWidget {
  final String title;
  final int? count;
  final String? action;
  final IconData? actionIcon;
  final VoidCallback? onAction;
  const SectionHeading(this.title, {super.key, this.count, this.action, this.onAction, this.actionIcon});
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(top: 23, bottom: 13),
        child: Row(children: [
          Text(title, style: T.h2),
          if (count != null)
            Container(
              margin: const EdgeInsetsDirectional.only(start: 6), width: 22, height: 20, alignment: Alignment.center,
              decoration: BoxDecoration(color: Brand.soft, borderRadius: BorderRadius.circular(6)),
              child: Text('$count', style: const TextStyle(fontSize: 10, fontWeight: FontWeight.w600, color: Brand.blue)),
            ),
          const Spacer(),
          if (action != null)
            GestureDetector(
              onTap: onAction,
              child: Row(children: [
                Text(action!, style: const TextStyle(fontSize: 10, color: Brand.blue)),
                const SizedBox(width: 5),
                Icon(actionIcon ?? (isAr(context) ? Lu.arrowLeft : Lu.arrowRight), size: 13, color: Brand.blue),
              ]),
            ),
        ]),
      );
}

/// An amount as the reference sets it: the whole part large, the fils lighter, the currency small beside it.
class Amount extends StatelessWidget {
  final String value;   // already formatted ("734,203.090")
  final String unit;
  final double size;
  const Amount(this.value, this.unit, {super.key, this.size = 36});
  @override
  Widget build(BuildContext context) {
    final i = value.indexOf('.');
    final whole = i < 0 ? value : value.substring(0, i);
    final frac = i < 0 ? '' : value.substring(i);
    // the reference lays this row out left-to-right and pushes it to the right edge: "د.ك  734,203.090"
    return Directionality(
      textDirection: TextDirection.ltr,
      child: Row(mainAxisAlignment: MainAxisAlignment.end, crossAxisAlignment: CrossAxisAlignment.baseline, textBaseline: TextBaseline.alphabetic, children: [
        Text(unit, style: const TextStyle(fontSize: 12, color: Brand.mutedC)),
        const SizedBox(width: 7),
        Flexible(
          child: FittedBox(
            fit: BoxFit.scaleDown,
            child: Text.rich(TextSpan(children: [
              TextSpan(text: whole, style: TextStyle(fontSize: size, fontWeight: FontWeight.w700, letterSpacing: -1.8, color: Brand.inkC)),
              if (frac.isNotEmpty) TextSpan(text: frac, style: TextStyle(fontSize: size * .66, fontWeight: FontWeight.w700, letterSpacing: -.5, color: Brand.fils)),
            ])),
          ),
        ),
      ]),
    );
  }
}

/// THE financial card: label + icon, the amount, a note, the gold marker on the reading edge, and an optional soft
/// footer of two or three side figures.
class FinancialCard extends StatelessWidget {
  final String label, note;
  final IconData icon;
  final Widget amount;
  final List<(String, String, String)> footer;   // label, value, unit
  const FinancialCard({super.key, required this.label, required this.icon, required this.amount, this.note = '', this.footer = const []});
  @override
  Widget build(BuildContext context) => Container(
        margin: const EdgeInsets.only(bottom: 2),
        decoration: BoxDecoration(color: Brand.paper, border: Border.all(color: Brand.lineC), borderRadius: BorderRadius.circular(22),
            boxShadow: const [BoxShadow(color: Color(0x081F2D4D), blurRadius: 15, offset: Offset(0, 4))]),
        clipBehavior: Clip.antiAlias,
        child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          Stack(children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 20, 20, 17),
              child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                Row(children: [Expanded(child: Text(label, style: T.label)), Icon(icon, size: 16, color: Brand.support)]),
                const SizedBox(height: 8),
                amount,
                const SizedBox(height: 4),
                if (note.isNotEmpty) Text(note, style: T.small),
              ]),
            ),
            PositionedDirectional(start: 0, top: 23, child: Container(width: 3, height: 32, decoration: BoxDecoration(color: Brand.gold, borderRadius: BorderRadius.circular(3)))),
          ]),
          if (footer.isNotEmpty)
            Container(
              color: Brand.soft,
              padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
              child: IntrinsicHeight(
                child: Row(children: [
                  for (var i = 0; i < footer.length; i++) ...[
                    if (i > 0) ...[const VerticalDivider(color: Brand.lineC, width: 1), const SizedBox(width: 14)],
                    Expanded(
                      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                        Text(footer[i].$1, maxLines: 1, overflow: TextOverflow.ellipsis, style: T.small),
                        const SizedBox(height: 5),
                        Row(crossAxisAlignment: CrossAxisAlignment.baseline, textBaseline: TextBaseline.alphabetic, children: [
                          Flexible(
                            child: FittedBox(
                              fit: BoxFit.scaleDown, alignment: AlignmentDirectional.centerStart,
                              child: Text(footer[i].$2, textDirection: TextDirection.ltr, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w700, color: Brand.inkC)),
                            ),
                          ),
                          if (footer[i].$3.isNotEmpty) ...[const SizedBox(width: 5), Text(footer[i].$3, style: T.small)],
                        ]),
                      ]),
                    ),
                  ],
                ]),
              ),
            ),
        ]),
      );
}

/// A quick-access tile: a 52×48 outlined box with a stroke icon, the label under it.
class QuickTile extends StatelessWidget {
  final IconData icon;
  final String label;
  final VoidCallback onTap;
  const QuickTile({super.key, required this.icon, required this.label, required this.onTap});
  @override
  Widget build(BuildContext context) => GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: onTap,
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          Container(
            width: 52, height: 48, alignment: Alignment.center,
            decoration: BoxDecoration(color: Brand.paper, border: Border.all(color: Brand.lineC), borderRadius: BorderRadius.circular(15)),
            child: Icon(icon, size: 20, color: Brand.blue),
          ),
          const SizedBox(height: 9),
          Text(label, textAlign: TextAlign.center, maxLines: 2, style: const TextStyle(fontSize: 11, color: Brand.inkC, height: 1.3)),
        ]),
      );
}

/// A status label with its dot: gold "awaiting", green done, red refused, blue information.
class Tag extends StatelessWidget {
  final String text;
  final Color color, dot;
  const Tag(this.text, {super.key}) : color = Brand.goldInk, dot = Brand.gold;
  const Tag.ok(this.text, {super.key}) : color = Brand.okC, dot = Brand.okC;
  const Tag.bad(this.text, {super.key}) : color = Brand.badC, dot = Brand.badC;
  const Tag.info(this.text, {super.key}) : color = Brand.blue, dot = Brand.blue;
  @override
  Widget build(BuildContext context) => Row(mainAxisSize: MainAxisSize.min, children: [
        Container(width: 4, height: 4, decoration: BoxDecoration(color: dot, shape: BoxShape.circle)),
        const SizedBox(width: 5),
        Text(text, style: TextStyle(fontSize: 9.5, color: color)),
      ]);
}

/// The soft square that holds an icon (or a person's photo) at the start of a card or row.
class IconBox extends StatelessWidget {
  final IconData? icon;
  final String? photo, name;
  final Color fg, bg;
  const IconBox({super.key, this.icon, this.photo, this.name, this.fg = Brand.support, this.bg = Brand.soft});
  @override
  Widget build(BuildContext context) {
    if (photo != null || (icon == null && name != null)) return PersonAvatar(photo: photo, name: name ?? '', size: 36, radius: 10);
    return Container(width: 34, height: 36, alignment: Alignment.center, decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(10)), child: Icon(icon, size: 17, color: fg));
  }
}

/// The request card: icon or photo, title and who/where, a chevron; under a line, the status and the amount.
class RequestCard extends StatelessWidget {
  final Widget leading;
  final String title, subtitle;
  final Widget tag;
  final String amount, unit;
  final VoidCallback? onTap;
  const RequestCard({super.key, required this.leading, required this.title, required this.subtitle, required this.tag, this.amount = '', this.unit = '', this.onTap});
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(bottom: 9),
        child: Material(
          color: Brand.paper,
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(18), side: const BorderSide(color: Brand.lineC)),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: onTap,
            child: Padding(
              padding: const EdgeInsets.all(15),
              child: Column(children: [
                Row(children: [
                  leading,
                  const SizedBox(width: 10),
                  Expanded(
                    child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Text(title, maxLines: 1, overflow: TextOverflow.ellipsis, style: T.body),
                      const SizedBox(height: 2),
                      Text(subtitle, maxLines: 1, overflow: TextOverflow.ellipsis, style: T.small),
                    ]),
                  ),
                  if (onTap != null) Icon(forwardIcon(context), size: 15, color: Brand.mutedC),
                ]),
                const SizedBox(height: 12),
                const Divider(height: 1),
                const SizedBox(height: 11),
                Row(children: [
                  tag,
                  const Spacer(),
                  if (amount.isNotEmpty)
                    Row(crossAxisAlignment: CrossAxisAlignment.baseline, textBaseline: TextBaseline.alphabetic, children: [
                      Text(amount, textDirection: TextDirection.ltr, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: Brand.inkC)),
                      if (unit.isNotEmpty) ...[const SizedBox(width: 4), Text(unit, style: T.small)],
                    ]),
                ]),
              ]),
            ),
          ),
        ),
      );
}

/// A white list with 1px separators (the reference's .data / .list).
class DataList extends StatelessWidget {
  final List<Widget> children;
  const DataList({super.key, required this.children});
  @override
  Widget build(BuildContext context) => Container(
        decoration: BoxDecoration(color: Brand.paper, border: Border.all(color: Brand.lineC), borderRadius: BorderRadius.circular(18)),
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 3),
        child: Column(children: [
          for (var i = 0; i < children.length; i++) ...[if (i > 0) const Divider(height: 1), children[i]],
        ]),
      );
}

/// A key–value line inside a DataList.
class KV extends StatelessWidget {
  final String k, v;
  final bool total;
  final Widget? trailing;
  const KV(this.k, this.v, {super.key, this.total = false, this.trailing});
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 14),
        child: Row(children: [
          Expanded(child: Text(k, style: TextStyle(fontSize: 11.5, color: total ? Brand.inkC : Brand.mutedC, fontWeight: total ? FontWeight.w700 : FontWeight.w400))),
          trailing ?? Text(v, style: TextStyle(fontSize: 11.5, color: Brand.inkC, fontWeight: total ? FontWeight.w700 : FontWeight.w500)),
        ]),
      );
}

/// A row inside a DataList: icon box, title, subtitle, and an end (text, tag, stepper or chevron).
class ItemRow extends StatelessWidget {
  final Widget leading;
  final String title;
  final String? subtitle;
  final Widget? end;
  final VoidCallback? onTap;
  const ItemRow({super.key, required this.leading, required this.title, this.subtitle, this.end, this.onTap});
  @override
  Widget build(BuildContext context) => InkWell(
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 12),
          child: Row(children: [
            leading,
            const SizedBox(width: 10),
            Expanded(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(title, maxLines: 1, overflow: TextOverflow.ellipsis, style: T.body),
                if (subtitle != null) ...[const SizedBox(height: 2), Text(subtitle!, maxLines: 1, overflow: TextOverflow.ellipsis, style: T.small)],
              ]),
            ),
            if (end != null) ...[const SizedBox(width: 8), end!] else if (onTap != null) Icon(forwardIcon(context), size: 15, color: Brand.mutedC),
          ]),
        ),
      );
}

/// Chips in a row (months, leave types, payment methods): outlined, the chosen one navy.
class ChipTabs extends StatelessWidget {
  final List<String> items;
  final int index;
  final ValueChanged<int>? onTap;
  const ChipTabs({super.key, required this.items, required this.index, this.onTap});
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(bottom: 16),
        child: SingleChildScrollView(
          scrollDirection: Axis.horizontal,
          child: Row(children: [
            for (var i = 0; i < items.length; i++)
              Padding(
                padding: const EdgeInsetsDirectional.only(end: 6),
                child: GestureDetector(
                  onTap: onTap == null ? null : () => onTap!(i),
                  child: Container(
                    padding: const EdgeInsets.symmetric(horizontal: 13, vertical: 8),
                    decoration: BoxDecoration(
                      color: i == index ? Brand.navy : Brand.paper,
                      border: Border.all(color: i == index ? Brand.navy : Brand.lineC),
                      borderRadius: BorderRadius.circular(10),
                    ),
                    child: Text(items[i], style: TextStyle(fontSize: 11, color: i == index ? Colors.white : Brand.mutedC)),
                  ),
                ),
              ),
          ]),
        ),
      );
}

/// A form field shown as the reference draws it: a white 14px box, the label small above the value.
class FieldBox extends StatelessWidget {
  final String label;
  final Widget child;
  final VoidCallback? onTap;
  const FieldBox({super.key, required this.label, required this.child, this.onTap});
  @override
  Widget build(BuildContext context) => GestureDetector(
        onTap: onTap,
        child: Container(
          margin: const EdgeInsets.only(bottom: 9),
          padding: const EdgeInsets.fromLTRB(14, 10, 14, 10),
          decoration: BoxDecoration(color: Brand.paper, border: Border.all(color: Brand.lineC), borderRadius: BorderRadius.circular(14)),
          child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            Text(label, style: T.small),
            const SizedBox(height: 2),
            DefaultTextStyle.merge(style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w500, color: Brand.inkC), child: child),
          ]),
        ),
      );
}

/// A full-width "success / all reviewed" note in the soft colour.
class SoftNote extends StatelessWidget {
  final String text;
  const SoftNote(this.text, {super.key});
  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.all(17),
        decoration: BoxDecoration(color: Brand.soft, borderRadius: BorderRadius.circular(13)),
        child: Text(text, textAlign: TextAlign.center, style: const TextStyle(fontSize: 12, color: Brand.inkC)),
      );
}

/// The reference's four-tab bar: white, a hairline on top, the chosen tab blue with a gold bar above it.
class ExecNav extends StatelessWidget {
  final List<(IconData, String)> items;
  final int index;
  final ValueChanged<int> onTap;
  const ExecNav({super.key, required this.items, required this.index, required this.onTap});
  @override
  Widget build(BuildContext context) => Container(
        decoration: const BoxDecoration(color: Brand.paper, border: Border(top: BorderSide(color: Brand.lineC))),
        padding: EdgeInsets.fromLTRB(12, 0, 12, MediaQuery.of(context).padding.bottom + 8),
        child: Row(children: [
          for (var i = 0; i < items.length; i++)
            Expanded(
              child: GestureDetector(
                behavior: HitTestBehavior.opaque,
                onTap: () => onTap(i),
                child: Column(mainAxisSize: MainAxisSize.min, children: [
                  Container(width: 22, height: 3, decoration: BoxDecoration(color: i == index ? Brand.gold : Colors.transparent, borderRadius: const BorderRadius.vertical(bottom: Radius.circular(3)))),
                  const SizedBox(height: 15),
                  Icon(items[i].$1, size: 20, color: i == index ? Brand.blue : Brand.mutedC),
                  const SizedBox(height: 6),
                  Text(items[i].$2, style: TextStyle(fontSize: 9.5, color: i == index ? Brand.blue : Brand.mutedC, fontWeight: i == index ? FontWeight.w600 : FontWeight.w400)),
                  const SizedBox(height: 6),
                ]),
              ),
            ),
        ]),
      );
}

/// A screen of the app: the navy top, the content, and (in a tab or a mode) the bar.
class ExecFrame extends StatelessWidget {
  final Widget child;
  final Widget? bottomBar;
  final (IconData, String, String)? mode;
  final VoidCallback? onExitMode;
  final VoidCallback? onBell;
  const ExecFrame({super.key, required this.child, this.bottomBar, this.mode, this.onExitMode, this.onBell});
  @override
  Widget build(BuildContext context) => Scaffold(
        backgroundColor: Brand.page,
        body: Column(children: [
          ExecTop(company: ExecSession.company, branch: ExecSession.branch, bellDot: ExecSession.unread, mode: mode, onExitMode: onExitMode, onBell: onBell),
          Expanded(child: child),
        ]),
        bottomNavigationBar: bottomBar,
      );
}

/// Scrollable page content with the reference's padding (23 / 22).
class ExecBody extends StatelessWidget {
  final List<Widget> children;
  final Future<void> Function()? onRefresh;
  const ExecBody({super.key, required this.children, this.onRefresh});
  @override
  Widget build(BuildContext context) {
    final list = ListView(padding: const EdgeInsets.fromLTRB(22, 23, 22, 28), children: children);
    return onRefresh == null ? list : RefreshIndicator(onRefresh: onRefresh!, child: list);
  }
}

// ------------------------------------------------------------------------------------------------ older API, restyled
/// A pushed screen with a title (older screens): the navy top, a back link and the title, then the body.
class PageFrame extends StatelessWidget {
  final String title;
  final Widget child;
  final List<Widget>? actions;
  const PageFrame({super.key, required this.title, required this.child, this.actions});
  @override
  Widget build(BuildContext context) => ExecFrame(
        child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(22, 18, 22, 0),
            child: Row(children: [
              if (Navigator.of(context).canPop())
                GestureDetector(
                  onTap: () => Navigator.of(context).maybePop(),
                  child: Padding(padding: const EdgeInsetsDirectional.only(end: 10), child: Icon(backIcon(context), size: 18, color: Brand.mutedC)),
                ),
              Expanded(child: Text(title, maxLines: 1, overflow: TextOverflow.ellipsis, style: T.h1.copyWith(fontSize: 20))),
              ...?actions,
            ]),
          ),
          Expanded(child: child),
        ]),
      );
}

/// A white card with an optional title (older screens).
class SectionCard extends StatelessWidget {
  final String? title;
  final Widget? action;
  final Widget child;
  final EdgeInsetsGeometry padding;
  final VoidCallback? onTap;
  const SectionCard({super.key, this.title, this.action, required this.child, this.padding = const EdgeInsets.all(16), this.onTap});
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(bottom: 12),
        child: Material(
          color: Brand.paper,
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(18), side: const BorderSide(color: Brand.lineC)),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: onTap,
            child: Padding(
              padding: padding,
              child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                if (title != null)
                  Padding(padding: const EdgeInsets.only(bottom: 10), child: Row(children: [Expanded(child: Text(title!, style: T.h2)), if (action != null) action!])),
                child,
              ]),
            ),
          ),
        ),
      );
}

/// A status label (older screens) — the reference's tag.
class Pill extends StatelessWidget {
  final String text;
  final int kind;   // 0 info, 1 ok, 2 bad, 3 awaiting
  const Pill(this.text, {super.key, Color? fg, Color? bg}) : kind = 0;
  const Pill.ok(this.text, {super.key}) : kind = 1;
  const Pill.bad(this.text, {super.key}) : kind = 2;
  const Pill.violet(this.text, {super.key}) : kind = 3;
  @override
  Widget build(BuildContext context) => switch (kind) { 1 => Tag.ok(text), 2 => Tag.bad(text), 3 => Tag(text), _ => Tag.info(text) };
}

/// A soft square icon (older screens).
class IconTile extends StatelessWidget {
  final IconData icon;
  final Color fg, bg;
  final double size;
  const IconTile(this.icon, {super.key, this.fg = Brand.support, this.bg = Brand.soft, this.size = 36});
  @override
  Widget build(BuildContext context) => Container(
        width: size, height: size, alignment: Alignment.center,
        decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(size * .28)),
        child: Icon(icon, size: size * .47, color: fg),
      );
}

/// A list row (older screens).
class RowItem extends StatelessWidget {
  final Widget leading;
  final String title;
  final String? subtitle;
  final Widget? trailing;
  final VoidCallback? onTap;
  const RowItem({super.key, required this.leading, required this.title, this.subtitle, this.trailing, this.onTap});
  @override
  Widget build(BuildContext context) => ItemRow(leading: leading, title: title, subtitle: subtitle, end: trailing, onTap: onTap);
}

/// The blue band of older screens — now the navy of the reference.
class HeroBand extends StatelessWidget {
  final Widget child;
  final double bottom;
  const HeroBand({super.key, required this.child, this.bottom = 40});
  @override
  Widget build(BuildContext context) => Container(
        width: double.infinity, color: Brand.navy,
        padding: EdgeInsetsDirectional.fromSTEB(22, MediaQuery.of(context).padding.top + 18, 22, bottom),
        child: DefaultTextStyle.merge(style: const TextStyle(color: Colors.white), child: child),
      );
}

/// The empty state.
class EmptyNote extends StatelessWidget {
  final IconData icon;
  final String title, text;
  final Color? color;
  const EmptyNote({super.key, this.icon = LucideIcons300.circleCheck, required this.title, required this.text, this.color});
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 22),
        child: Column(children: [
          Container(
            width: 52, height: 52, alignment: Alignment.center,
            decoration: BoxDecoration(color: Brand.soft, borderRadius: BorderRadius.circular(16)),
            child: Icon(icon, size: 24, color: color ?? Brand.support),
          ),
          const SizedBox(height: 12),
          Text(title, style: T.h2),
          const SizedBox(height: 4),
          Text(text, textAlign: TextAlign.center, style: T.sub),
        ]),
      );
}

/// Month name, localized.
String monthName(BuildContext context, int m) {
  const ar = ['', 'يناير', 'فبراير', 'مارس', 'أبريل', 'مايو', 'يونيو', 'يوليو', 'أغسطس', 'سبتمبر', 'أكتوبر', 'نوفمبر', 'ديسمبر'];
  const en = ['', 'January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
  return isAr(context) ? ar[m] : en[m];
}

String weekdayName(BuildContext context, int wd) {
  const ar = ['', 'الإثنين', 'الثلاثاء', 'الأربعاء', 'الخميس', 'الجمعة', 'السبت', 'الأحد'];
  const en = ['', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
  return isAr(context) ? ar[wd] : en[wd];
}

/// A whole number with thousands separators (the reference's side figures: "8,400").
String whole(num v) => v.round().toString().replaceAllMapped(RegExp(r'\B(?=(\d{3})+(?!\d))'), (m) => ',');

/// "2 نوفمبر" / "2 November"
String dayMonth(BuildContext context, DateTime d) => '${d.day} ${monthName(context, d.month)}';

String ymd(DateTime d) => '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

// ------------------------------------------------------------------------------------------------ work modes
/// The reference's scan box: a dashed-feel card with the barcode ring, a one-line instruction and the code field a
/// handheld scanner types into (it presses Enter after the code).
class ScanBox extends StatelessWidget {
  final String text, hint;
  final TextEditingController controller;
  final FocusNode? focusNode;
  final ValueChanged<String> onSubmitted;
  final ValueChanged<String>? onChanged;
  final bool autofocus;
  const ScanBox({super.key, required this.text, required this.hint, required this.controller, required this.onSubmitted, this.focusNode, this.onChanged, this.autofocus = false});
  @override
  Widget build(BuildContext context) => Container(
        margin: const EdgeInsets.only(bottom: 4),
        padding: const EdgeInsets.fromLTRB(16, 18, 16, 16),
        decoration: BoxDecoration(color: Brand.paper, border: Border.all(color: Brand.lineC), borderRadius: BorderRadius.circular(18)),
        child: Column(children: [
          Container(
            width: 54, height: 54, alignment: Alignment.center,
            decoration: BoxDecoration(color: Brand.soft, shape: BoxShape.circle, border: Border.all(color: const Color(0xFFD5DEEE))),
            child: const Icon(Lu.scanBarcode, size: 22, color: Brand.blue),
          ),
          const SizedBox(height: 10),
          Text(text, textAlign: TextAlign.center, style: T.sub),
          const SizedBox(height: 10),
          TextField(
            controller: controller, focusNode: focusNode, autofocus: autofocus, textInputAction: TextInputAction.done,
            onSubmitted: onSubmitted, onChanged: onChanged,
            textAlign: TextAlign.center,
            decoration: InputDecoration(hintText: hint, isDense: true),
          ),
        ]),
      );
}

/// − n + : the reference's step control for a counted or transferred quantity.
class QtyStepper extends StatelessWidget {
  final String value;
  final VoidCallback? onMinus, onPlus, onTapValue;
  const QtyStepper({super.key, required this.value, this.onMinus, this.onPlus, this.onTapValue});
  @override
  Widget build(BuildContext context) {
    Widget b(IconData i, VoidCallback? f) => GestureDetector(
          onTap: f,
          child: Container(
            width: 28, height: 28, alignment: Alignment.center,
            decoration: BoxDecoration(color: Brand.paper, border: Border.all(color: Brand.lineC), borderRadius: BorderRadius.circular(9)),
            child: Icon(i, size: 14, color: f == null ? Brand.lineC : Brand.support),
          ),
        );
    return Row(mainAxisSize: MainAxisSize.min, children: [
      b(Lu.minus, onMinus),
      GestureDetector(
        onTap: onTapValue,
        child: Container(
          constraints: const BoxConstraints(minWidth: 34), alignment: Alignment.center,
          padding: const EdgeInsets.symmetric(horizontal: 6),
          child: Text(value, textDirection: TextDirection.ltr, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: Brand.inkC)),
        ),
      ),
      b(Lu.plus, onPlus),
    ]);
  }
}

/// A choice from a short list, in a bottom sheet of item rows (the reference's field with a chevron opens this).
Future<V?> pickFromSheet<V>(BuildContext context, {required String title, required List<(V, String, String?)> options, V? selected}) =>
    showModalBottomSheet<V>(
      context: context,
      backgroundColor: Brand.page,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(22))),
      builder: (c) => SafeArea(
        child: ConstrainedBox(
          constraints: BoxConstraints(maxHeight: MediaQuery.of(c).size.height * .7),
          child: ListView(shrinkWrap: true, padding: const EdgeInsets.fromLTRB(22, 18, 22, 18), children: [
            Text(title, style: T.h2),
            const SizedBox(height: 12),
            DataList(children: [
              for (final o in options)
                ItemRow(
                  leading: IconBox(icon: o.$1 == selected ? Lu.circleCheck : Lu.circle),
                  title: o.$2,
                  subtitle: o.$3,
                  onTap: () => Navigator.of(c).pop(o.$1),
                ),
            ]),
          ]),
        ),
      ),
    );
