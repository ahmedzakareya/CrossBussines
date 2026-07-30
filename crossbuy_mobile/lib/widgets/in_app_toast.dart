import 'package:flutter/material.dart';

import '../l10n/app_localizations.dart';
import '../models/app_notification.dart';
import '../services/app_nav.dart';
import 'app_ui.dart';

/// Shows a transient top banner ("heads-up" toast) for an incoming notification,
/// independent of the notifications list. Tapping it routes to the right screen.
void showInAppToast(AppNotification n) {
  final overlay = AppNav.navigatorKey.currentState?.overlay;
  if (overlay == null) return;

  late OverlayEntry entry;
  var removed = false;
  void close() {
    if (removed) return;
    removed = true;
    entry.remove();
  }

  entry = OverlayEntry(
    builder: (ctx) => _ToastBanner(
      n: n,
      onClose: close,
      onTap: () {
        close();
        AppNav.routeForNotification(n.type);
      },
    ),
  );
  overlay.insert(entry);
  Future.delayed(const Duration(seconds: 5), close);
}

class _ToastBanner extends StatefulWidget {
  final AppNotification n;
  final VoidCallback onClose;
  final VoidCallback onTap;
  const _ToastBanner({required this.n, required this.onClose, required this.onTap});

  @override
  State<_ToastBanner> createState() => _ToastBannerState();
}

class _ToastBannerState extends State<_ToastBanner> with SingleTickerProviderStateMixin {
  late final AnimationController _c =
      AnimationController(vsync: this, duration: const Duration(milliseconds: 260))..forward();

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  Color _color(String? t) =>
      t == 'leave_approved' ? kGreen : (t == 'leave_rejected' ? const Color(0xFFD9214E) : kPrimary);
  IconData _icon(String? t) => t == 'leave_approved'
      ? Icons.check_circle
      : (t == 'leave_rejected' ? Icons.cancel : Icons.event_note);

  @override
  Widget build(BuildContext context) {
    final ar = AppLocalizations.of(context).isAr;
    final n = widget.n;
    final title = (ar ? n.titleAr : n.titleEn) ?? n.titleEn ?? n.titleAr ?? '';
    final body = (ar ? n.bodyAr : n.bodyEn) ?? n.bodyEn ?? n.bodyAr ?? '';
    final col = _color(n.type);

    return Positioned(
      top: 0, left: 0, right: 0,
      child: SafeArea(
        bottom: false,
        child: SlideTransition(
          position: Tween(begin: const Offset(0, -1), end: Offset.zero)
              .animate(CurvedAnimation(parent: _c, curve: Curves.easeOut)),
          child: Padding(
            padding: const EdgeInsets.fromLTRB(10, 8, 10, 0),
            child: Material(
              color: Colors.transparent,
              child: InkWell(
                onTap: widget.onTap,
                borderRadius: BorderRadius.circular(14),
                child: Container(
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(
                    color: Colors.white,
                    borderRadius: BorderRadius.circular(14),
                    boxShadow: [
                      BoxShadow(color: Colors.black.withValues(alpha: 0.16), blurRadius: 18, offset: const Offset(0, 6)),
                    ],
                  ),
                  child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Container(
                      width: 38, height: 38,
                      decoration: BoxDecoration(color: col, shape: BoxShape.circle),
                      child: Icon(_icon(n.type), color: Colors.white, size: 20),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                        Text(title, maxLines: 1, overflow: TextOverflow.ellipsis,
                            style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 14, color: kInk)),
                        if (body.isNotEmpty) ...[
                          const SizedBox(height: 2),
                          Text(body, maxLines: 2, overflow: TextOverflow.ellipsis,
                              style: const TextStyle(fontSize: 12.5, color: Color(0xFF5E6278))),
                        ],
                      ]),
                    ),
                    InkWell(
                      onTap: widget.onClose,
                      child: const Padding(padding: EdgeInsets.all(2),
                          child: Icon(Icons.close, size: 18, color: kMuted)),
                    ),
                  ]),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
