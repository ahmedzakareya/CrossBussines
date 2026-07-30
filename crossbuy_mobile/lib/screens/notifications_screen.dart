import 'package:flutter/material.dart';
import 'package:intl/intl.dart' hide TextDirection;
import 'package:provider/provider.dart';

import '../l10n/app_localizations.dart';
import '../models/app_notification.dart';
import '../providers/notification_provider.dart';
import '../services/app_nav.dart';
import '../widgets/app_ui.dart';

class NotificationsScreen extends StatefulWidget {
  const NotificationsScreen({super.key});
  @override
  State<NotificationsScreen> createState() => _NotificationsScreenState();
}

class _NotificationsScreenState extends State<NotificationsScreen> {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      context.read<NotificationProvider>().load();
    });
  }

  IconData _icon(String? type) {
    switch (type) {
      case 'leave_approved':
        return Icons.check_circle;
      case 'leave_rejected':
        return Icons.cancel;
      case 'leave_submitted':
        return Icons.event_note;
      default:
        return Icons.notifications;
    }
  }

  Color _color(String? type) {
    switch (type) {
      case 'leave_approved':
        return kGreen;
      case 'leave_rejected':
        return const Color(0xFFD9214E);
      default:
        return kPrimary;
    }
  }

  @override
  Widget build(BuildContext context) {
    final t = AppLocalizations.of(context);
    final ar = t.isAr;
    final df = DateFormat('yyyy/MM/dd  HH:mm');
    final prov = context.watch<NotificationProvider>();
    final items = prov.items;

    return Container(
      color: kBg,
      child: Column(
        children: [
          AppHeader(
            title: t.t('notifications'),
            bottom: prov.unread > 0
                ? Align(
                    alignment: AlignmentDirectional.centerEnd,
                    child: TextButton.icon(
                      onPressed: () => context.read<NotificationProvider>().markAllRead(),
                      icon: const Icon(Icons.done_all, color: Colors.white, size: 18),
                      label: Text(ar ? 'تعليم الكل كمقروء' : 'Mark all read',
                          style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w600)),
                    ),
                  )
                : null,
          ),
          Expanded(
            child: RefreshIndicator(
              onRefresh: () => context.read<NotificationProvider>().load(),
              child: items.isEmpty
                  ? ListView(children: [
                      const SizedBox(height: 120),
                      const Icon(Icons.notifications_none, size: 60, color: Color(0xFFB5B9C5)),
                      const SizedBox(height: 12),
                      Center(child: Text(ar ? 'لا توجد إشعارات' : 'No notifications',
                          style: const TextStyle(color: kMuted, fontSize: 14))),
                    ])
                  : ListView.separated(
                      padding: const EdgeInsets.fromLTRB(14, 16, 14, 24),
                      itemCount: items.length,
                      separatorBuilder: (_, __) => const SizedBox(height: 10),
                      itemBuilder: (_, i) => _tile(items[i], ar, df),
                    ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _tile(AppNotification n, bool ar, DateFormat df) {
    final title = (ar ? n.titleAr : n.titleEn) ?? n.titleEn ?? n.titleAr ?? '';
    final body = (ar ? n.bodyAr : n.bodyEn) ?? n.bodyEn ?? n.bodyAr ?? '';
    final col = _color(n.type);
    return InkWell(
      onTap: () => AppNav.routeForNotification(n.type),
      borderRadius: BorderRadius.circular(16),
      child: Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: n.isRead ? Colors.white : const Color(0xFFEAF3FF),
        borderRadius: BorderRadius.circular(16),
        boxShadow: [
          BoxShadow(color: Colors.black.withValues(alpha: 0.04), blurRadius: 10, offset: const Offset(0, 3)),
        ],
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          IconChip(_icon(n.type), color: col, size: 42),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(title, style: const TextStyle(fontSize: 14, fontWeight: FontWeight.bold, color: kInk)),
                if (body.isNotEmpty) ...[
                  const SizedBox(height: 3),
                  Text(body, style: const TextStyle(fontSize: 12.5, color: Color(0xFF5E6278))),
                ],
                if (n.createdAt != null) ...[
                  const SizedBox(height: 6),
                  Text(df.format(n.createdAt!.toLocal()), style: const TextStyle(fontSize: 10.5, color: kMuted)),
                ],
              ],
            ),
          ),
          if (!n.isRead)
            Container(width: 9, height: 9, margin: const EdgeInsets.only(top: 4),
                decoration: const BoxDecoration(color: kPrimary, shape: BoxShape.circle)),
        ],
      ),
      ),
    );
  }
}
