import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../l10n/app_localizations.dart';
import '../providers/notification_provider.dart';
import '../services/app_nav.dart';
import 'dashboard_screen.dart';
import 'finance_screen.dart';
import 'inventory_screen.dart';
import 'leave_screen.dart';
import 'notifications_screen.dart';
import 'profile_screen.dart';
import 'structure_screen.dart';

class HomeScreen extends StatefulWidget {
  const HomeScreen({super.key});
  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> {
  int _index = 0;

  @override
  void initState() {
    super.initState();
    AppNav.tabIndex.addListener(_onTabRequest);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      final notif = context.read<NotificationProvider>();
      notif.load();
      notif.connect();
    });
  }

  void _onTabRequest() {
    if (mounted && AppNav.tabIndex.value != _index) {
      setState(() => _index = AppNav.tabIndex.value);
    }
  }

  @override
  void dispose() {
    AppNav.tabIndex.removeListener(_onTabRequest);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final t = AppLocalizations.of(context);
    const pages = [
      DashboardScreen(),
      ProfileScreen(),
      StructureScreen(),
      LeaveScreen(),
      FinanceScreen(),
      InventoryScreen(),
      NotificationsScreen(),
    ];

    return Scaffold(
      // every tab carries its own gradient header
      body: IndexedStack(index: _index, children: pages),
      bottomNavigationBar: NavigationBar(
        selectedIndex: _index,
        onDestinationSelected: (i) => AppNav.tabIndex.value = i,
        destinations: [
          NavigationDestination(
              icon: const Icon(Icons.dashboard_outlined),
              selectedIcon: const Icon(Icons.dashboard),
              label: t.t('dashboard')),
          NavigationDestination(
              icon: const Icon(Icons.person_outline),
              selectedIcon: const Icon(Icons.person),
              label: t.t('profile')),
          NavigationDestination(
              icon: const Icon(Icons.account_tree_outlined),
              selectedIcon: const Icon(Icons.account_tree),
              label: t.t('structure')),
          NavigationDestination(
              icon: const Icon(Icons.beach_access_outlined),
              selectedIcon: const Icon(Icons.beach_access),
              label: t.t('leaves')),
          NavigationDestination(
              icon: const Icon(Icons.account_balance_outlined),
              selectedIcon: const Icon(Icons.account_balance),
              label: t.t('finance')),
          NavigationDestination(
              icon: const Icon(Icons.inventory_2_outlined),
              selectedIcon: const Icon(Icons.inventory_2),
              label: t.t('inventory')),
          NavigationDestination(
              icon: _notifIcon(context, Icons.notifications_outlined),
              selectedIcon: _notifIcon(context, Icons.notifications),
              label: t.t('notifications')),
        ],
      ),
    );
  }

  Widget _notifIcon(BuildContext context, IconData icon) {
    final unread = context.watch<NotificationProvider>().unread;
    if (unread <= 0) return Icon(icon);
    return Badge(label: Text(unread > 99 ? '99+' : '$unread'), child: Icon(icon));
  }
}
