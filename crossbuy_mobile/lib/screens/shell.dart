import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../providers/notification_provider.dart';
import '../services/app_nav.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';
import 'apps_tab.dart';
import 'attendance_screen.dart';
import 'home_tab.dart';
import 'inbox_tab.dart';
import 'leave_screen.dart';
import 'me_tab.dart';
import 'payslips_screen.dart';
import 'requests_screen.dart';

/// The redesigned shell (owner-approved prototype): FOUR tabs — Home, Inbox, Apps, Me — and a centre quick-action
/// button, replacing the 7-tab bar. Every module is reached from Apps; what needs me now is on Home and in Inbox.
class Shell extends StatefulWidget {
  const Shell({super.key});
  @override
  State<Shell> createState() => _ShellState();
}

class _ShellState extends State<Shell> {
  int _index = AppNav.home;

  @override
  void initState() {
    super.initState();
    AppNav.tabIndex.addListener(_onTab);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      final notif = context.read<NotificationProvider>();
      notif.load();
      notif.connect();
    });
  }

  void _onTab() {
    if (mounted && AppNav.tabIndex.value != _index) setState(() => _index = AppNav.tabIndex.value);
  }

  @override
  void dispose() {
    AppNav.tabIndex.removeListener(_onTab);
    super.dispose();
  }

  void _quick() {
    final acts = <(IconData, String, String, Widget Function())>[
      (Icons.event_available_outlined, 'طلب إجازة', 'Request leave', () => PageFrame(title: L(context, 'الإجازات', 'Leave'), child: const LeaveScreen())),
      (Icons.description_outlined, 'خطاب / سلفة', 'Letter / advance', () => const NewRequestScreen()),
      (Icons.account_balance_wallet_outlined, 'قسائمي', 'My payslips', () => const PayslipsScreen()),
      (Icons.schedule_outlined, 'حضوري', 'My attendance', () => const AttendanceScreen()),
    ];
    showModalBottomSheet(
      context: context,
      showDragHandle: true,
      backgroundColor: Colors.white,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(26))),
      builder: (ctx) => Padding(
        padding: const EdgeInsets.fromLTRB(18, 0, 18, 28),
        child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(L(context, 'ماذا تريد أن تفعل؟', 'What would you like to do?'), style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w700)),
          const SizedBox(height: 16),
          GridView.count(
            crossAxisCount: 4, shrinkWrap: true, physics: const NeverScrollableScrollPhysics(), childAspectRatio: .78,
            children: [
              for (final a in acts)
                InkWell(
                  borderRadius: BorderRadius.circular(16),
                  onTap: () {
                    Navigator.pop(ctx);
                    Navigator.of(context).push(MaterialPageRoute(builder: (_) => a.$4()));
                  },
                  child: Column(children: [
                    IconTile(a.$1, size: 52),
                    const SizedBox(height: 6),
                    Text(L(context, a.$2, a.$3), textAlign: TextAlign.center, style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600)),
                  ]),
                ),
            ],
          ),
        ]),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final unread = context.watch<NotificationProvider>().unread;
    const pages = [HomeTab(), InboxTab(), AppsTab(), MeTab()];
    Widget tab(int i, IconData icon, IconData on, String ar, String en, {int badge = 0}) {
      final selected = _index == i;
      return Expanded(
        child: InkWell(
          onTap: () => AppNav.tabIndex.value = i,
          child: Padding(
            padding: const EdgeInsets.only(top: 8, bottom: 6),
            child: Column(mainAxisSize: MainAxisSize.min, children: [
              Badge(isLabelVisible: badge > 0, label: Text('$badge'), backgroundColor: Brand.bad,
                  child: Icon(selected ? on : icon, color: selected ? Brand.brand : const Color(0xFF94A3B8))),
              const SizedBox(height: 3),
              Text(L(context, ar, en), style: TextStyle(fontSize: 11, fontWeight: FontWeight.w600, color: selected ? Brand.brand : const Color(0xFF94A3B8))),
            ]),
          ),
        ),
      );
    }

    return Scaffold(
      body: IndexedStack(index: _index, children: pages),
      floatingActionButton: FloatingActionButton(
        onPressed: _quick,
        backgroundColor: Brand.deep,
        foregroundColor: Colors.white,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(20)),
        child: const Icon(Icons.add, size: 28),
      ),
      floatingActionButtonLocation: FloatingActionButtonLocation.centerDocked,
      bottomNavigationBar: BottomAppBar(
        color: Colors.white, elevation: 8, padding: EdgeInsets.zero, height: 66,
        shape: const CircularNotchedRectangle(), notchMargin: 6,
        child: Row(children: [
          tab(AppNav.home, Icons.home_outlined, Icons.home, 'الرئيسية', 'Home'),
          tab(AppNav.inbox, Icons.inbox_outlined, Icons.inbox, 'الوارد', 'Inbox', badge: unread),
          const SizedBox(width: 64),
          tab(AppNav.apps, Icons.apps_outlined, Icons.apps, 'التطبيقات', 'Apps'),
          tab(AppNav.me, Icons.person_outline, Icons.person, 'أنا', 'Me'),
        ]),
      ),
    );
  }
}
