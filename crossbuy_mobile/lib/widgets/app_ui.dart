import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../l10n/app_localizations.dart';
import '../providers/auth_provider.dart';
import '../providers/locale_provider.dart';
import '../providers/notification_provider.dart';
import '../screens/login_screen.dart';
import '../services/app_nav.dart';

/// Shared visual identity, mirrored from the My Profile screen so every
/// screen in the app speaks the same design language.

const kPrimary = Color(0xFF1B84FF);
const kInk = Color(0xFF181C32);
const kMuted = Color(0xFF99A1B7);
const kGreen = Color(0xFF17C653);
const kBg = Color(0xFFEEF2F8);
const kChipBg = Color(0xFFF5F7FB);

/// The signature gradient header from My Profile (navy gradient + city skyline),
/// reused across screens so the whole app shares one identity.
/// Carries the language toggle and logout, plus an optional [bottom] widget.
class AppHeader extends StatelessWidget {
  final String title;
  final Widget? bottom;
  const AppHeader({required this.title, this.bottom, super.key});

  @override
  Widget build(BuildContext context) {
    final t = AppLocalizations.of(context);
    return Container(
      decoration: const BoxDecoration(
        gradient: LinearGradient(
          begin: Alignment.topRight, end: Alignment.bottomLeft,
          colors: [Color(0xFF0A1B40), Color(0xFF123569), Color(0xFF1C4A8E)],
        ),
      ),
      child: Stack(children: [
        Positioned.fill(child: CustomPaint(painter: SkylinePainter())),
        SafeArea(
          bottom: false,
          child: Padding(
            padding: EdgeInsets.fromLTRB(14, 8, 14, bottom == null ? 16 : 12),
            child: Column(children: [
              Row(children: [
                _langPill(context, t),
                Expanded(
                  child: Center(
                    child: Text(title,
                        maxLines: 1, overflow: TextOverflow.ellipsis,
                        style: const TextStyle(color: Colors.white, fontSize: 18, fontWeight: FontWeight.bold)),
                  ),
                ),
                _circle(Icons.logout, () async {
                  final nav = Navigator.of(context);
                  final notif = context.read<NotificationProvider>();
                  final auth = context.read<AuthProvider>();
                  await notif.reset();
                  await auth.logout();
                  AppNav.resetTabs();
                  nav.pushReplacement(
                      MaterialPageRoute(builder: (_) => const LoginScreen()));
                }),
              ]),
              if (bottom != null) ...[const SizedBox(height: 14), bottom!],
            ]),
          ),
        ),
      ]),
    );
  }

  Widget _langPill(BuildContext context, AppLocalizations t) => InkWell(
        onTap: () => context.read<LocaleProvider>().toggle(),
        borderRadius: BorderRadius.circular(20),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
          decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.16), borderRadius: BorderRadius.circular(20)),
          child: Row(mainAxisSize: MainAxisSize.min, children: [
            const Icon(Icons.language, color: Colors.white, size: 16),
            const SizedBox(width: 6),
            Text(t.t('language'), style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w600, fontSize: 13)),
            const SizedBox(width: 2),
            const Icon(Icons.keyboard_arrow_down, color: Colors.white, size: 17),
          ]),
        ),
      );

  Widget _circle(IconData ic, VoidCallback onTap) => InkWell(
        onTap: onTap, borderRadius: BorderRadius.circular(30),
        child: Container(width: 40, height: 40,
            decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.16), shape: BoxShape.circle),
            child: Icon(ic, color: Colors.white, size: 20)),
      );
}

/// Faint city-skyline silhouette painted inside the gradient header
/// (mirrors the My Profile header skyline).
class SkylinePainter extends CustomPainter {
  @override
  void paint(Canvas canvas, Size size) {
    final p = Paint()..color = Colors.white.withValues(alpha: 0.05);
    final base = size.height - 2;
    final heights = [38, 64, 50, 80, 58, 96, 46, 72, 60, 88, 42, 70, 54, 84, 48];
    final w = size.width / heights.length;
    for (var i = 0; i < heights.length; i++) {
      final h = heights[i].toDouble();
      canvas.drawRRect(
        RRect.fromRectAndCorners(
          Rect.fromLTWH(i * w + 2, base - h, w - 4, h),
          topLeft: const Radius.circular(2), topRight: const Radius.circular(2),
        ),
        p,
      );
    }
    final tp = Paint()..color = Colors.white.withValues(alpha: 0.07);
    final tx = size.width * 0.52;
    canvas.drawRect(Rect.fromLTWH(tx, base - 130, 5, 130), tp);
    canvas.drawCircle(Offset(tx + 2.5, base - 128), 15, tp);
    canvas.drawCircle(Offset(tx + 2.5, base - 92), 9, tp);
    final tx2 = size.width * 0.44;
    canvas.drawRect(Rect.fromLTWH(tx2, base - 100, 4, 100), tp);
    canvas.drawCircle(Offset(tx2 + 2, base - 98), 10, tp);
  }

  @override
  bool shouldRepaint(covariant CustomPainter oldDelegate) => false;
}

/// White rounded card with a header (title + icon chip + accent underline)
/// and an optional centered footer — identical to the profile's section card.
class SectionCard extends StatelessWidget {
  final IconData icon;
  final String title;
  final String? footer;
  final VoidCallback? onFooterTap;
  final Widget? trailing; // e.g. a count badge, shown next to the title
  final List<Widget> children;
  const SectionCard({
    super.key,
    required this.icon,
    required this.title,
    this.footer,
    this.onFooterTap,
    this.trailing,
    required this.children,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: Colors.white,
        borderRadius: BorderRadius.circular(18),
        boxShadow: [
          BoxShadow(color: Colors.black.withValues(alpha: 0.04), blurRadius: 12, offset: const Offset(0, 4)),
        ],
      ),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Row(mainAxisAlignment: MainAxisAlignment.end, children: [
          if (trailing != null) ...[trailing!, const SizedBox(width: 8)],
          Flexible(
            child: Text(title,
                textAlign: TextAlign.end,
                style: const TextStyle(fontSize: 14, fontWeight: FontWeight.bold, color: kInk)),
          ),
          const SizedBox(width: 8),
          Container(
            width: 34, height: 34,
            decoration: BoxDecoration(color: const Color(0xFFEFF5FF), borderRadius: BorderRadius.circular(9)),
            child: Icon(icon, color: kPrimary, size: 18),
          ),
        ]),
        Container(
          height: 2, width: 36,
          margin: const EdgeInsetsDirectional.only(top: 4, bottom: 6, end: 42),
          color: kPrimary,
        ),
        ...children,
        if (footer != null) ...[
          const Divider(height: 18),
          InkWell(
            onTap: onFooterTap,
            child: Center(child: Text('$footer  ⌄', style: const TextStyle(color: kPrimary, fontSize: 12.5, fontWeight: FontWeight.bold))),
          ),
        ],
      ]),
    );
  }
}

/// Small rounded square holding a tinted icon (matches profile's `_chip`).
class IconChip extends StatelessWidget {
  final IconData icon;
  final Color color;
  final double size;
  const IconChip(this.icon, {this.color = kPrimary, this.size = 34, super.key});

  @override
  Widget build(BuildContext context) => Container(
        width: size, height: size,
        decoration: BoxDecoration(color: color.withValues(alpha: 0.14), borderRadius: BorderRadius.circular(9)),
        child: Icon(icon, color: color, size: size * 0.5),
      );
}

/// A soft info row: grey chip background + white icon chip + label/value.
/// Mirrors the profile's `_InfoLine`.
class InfoRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final String value;
  final Color iconColor;
  final Color? valueColor;
  const InfoRow(this.icon, this.label, this.value,
      {this.iconColor = kPrimary, this.valueColor, super.key});

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.only(bottom: 8),
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 8),
      decoration: BoxDecoration(color: kChipBg, borderRadius: BorderRadius.circular(12)),
      child: Row(children: [
        Container(
          width: 34, height: 34,
          decoration: BoxDecoration(
            color: Colors.white, borderRadius: BorderRadius.circular(9),
            boxShadow: [BoxShadow(color: Colors.black.withValues(alpha: 0.04), blurRadius: 4)],
          ),
          child: Icon(icon, color: iconColor, size: 16),
        ),
        const SizedBox(width: 9),
        Expanded(
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(label, style: const TextStyle(fontSize: 10, color: kMuted)),
            const SizedBox(height: 1),
            Text(value,
                style: TextStyle(fontSize: 12.5, fontWeight: FontWeight.w700, color: valueColor ?? kInk),
                maxLines: 2, overflow: TextOverflow.ellipsis),
          ]),
        ),
      ]),
    );
  }
}

/// Pill count badge (matches profile/structure count badge).
class CountBadge extends StatelessWidget {
  final int count;
  const CountBadge(this.count, {super.key});
  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 2),
        decoration: BoxDecoration(color: const Color(0xFFE9F3FF), borderRadius: BorderRadius.circular(20)),
        child: Text('$count',
            style: const TextStyle(color: kPrimary, fontSize: 12, fontWeight: FontWeight.bold)),
      );
}
