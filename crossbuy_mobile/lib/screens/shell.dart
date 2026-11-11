import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../providers/notification_provider.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../widgets/ui.dart';
import 'apps_tab.dart';
import 'home_tab.dart';
import 'inbox_tab.dart';
import 'me_tab.dart';

/// The shell of the OWNER'S REFERENCE DESIGN: the navy top over every tab, and its four tabs —
/// الرئيسية · الموافقات · مساحات العمل · حسابي — with the gold bar over the chosen one. The bell opens the
/// notifications; work modes and detail screens are pushed over it.
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
      _loadSession();
    });
  }

  // the company row of the top band comes from the signed-in employee
  Future<void> _loadSession() async {
    try {
      final me = await ApiService.instance.getProfile();
      if (!mounted) return;
      final ar = isAr(context);
      setState(() {
        ExecSession.company = (ar ? me.companyAr : (me.companyEn ?? me.companyAr)) ?? '';
        final branch = (ar ? me.branchAr : (me.branchEn ?? me.branchAr)) ?? '';
        final dept = (ar ? me.departmentAr : (me.departmentEn ?? me.departmentAr)) ?? '';
        ExecSession.branch = [branch, dept].where((x) => x.isNotEmpty).join(' · ');
      });
    } catch (_) {}
  }

  void _onTab() {
    if (mounted && AppNav.tabIndex.value != _index) setState(() => _index = AppNav.tabIndex.value);
  }

  @override
  void dispose() {
    AppNav.tabIndex.removeListener(_onTab);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    ExecSession.unread = context.watch<NotificationProvider>().unread > 0;
    const pages = [HomeTab(), InboxTab(), AppsTab(), MeTab()];
    return ExecFrame(
      onBell: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => const NotificationsPage())),
      bottomBar: ExecNav(
        index: _index,
        onTap: (i) => AppNav.tabIndex.value = i,
        items: [
          (Lu.house, L(context, 'الرئيسية', 'Home')),
          (Lu.layers2, L(context, 'الموافقات', 'Approvals')),
          (Lu.grid2x2, L(context, 'مساحات العمل', 'Workspaces')),
          (Lu.circleUserRound, L(context, 'حسابي', 'Account')),
        ],
      ),
      child: IndexedStack(index: _index, children: pages),
    );
  }
}
