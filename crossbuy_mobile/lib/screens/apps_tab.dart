import 'package:flutter/material.dart';

import '../theme/brand.dart';
import '../widgets/ui.dart';
import 'attendance_screen.dart';
import 'finance_screen.dart';
import 'inventory_screen.dart';
import 'leave_screen.dart';
import 'payslips_screen.dart';
import 'profile_screen.dart';
import 'requests_screen.dart';
import 'structure_screen.dart';

/// APPS — every CrossBusiness module in one searchable grid (SAP Mobile Start "Apps", the Odoo app switcher), instead
/// of a crowded tab bar. A module the user may not use answers with the server's own refusal inside the screen.
class AppsTab extends StatefulWidget {
  const AppsTab({super.key});
  @override
  State<AppsTab> createState() => _AppsTabState();
}

class _App {
  final IconData icon;
  final String ar, en;
  final Color color;
  final Widget Function(BuildContext) page;
  const _App(this.icon, this.ar, this.en, this.color, this.page);
}

class _AppsTabState extends State<AppsTab> {
  String _q = '';

  List<_App> _apps(BuildContext c) => [
        _App(Icons.event_available_outlined, 'الإجازات', 'Leave', const Color(0xFF2563EB), (c) => PageFrame(title: L(c, 'الإجازات', 'Leave'), child: const LeaveScreen())),
        _App(Icons.schedule_outlined, 'الحضور', 'Attendance', const Color(0xFF0EA5E9), (c) => const AttendanceScreen()),
        _App(Icons.account_balance_wallet_outlined, 'قسائمي', 'Payslips', Brand.brand, (c) => const PayslipsScreen()),
        _App(Icons.description_outlined, 'طلباتي', 'Requests', const Color(0xFF7C3AED), (c) => const RequestsScreen()),
        _App(Icons.account_balance_outlined, 'المالية', 'Finance', const Color(0xFF0F766E), (c) => PageFrame(title: L(c, 'المالية', 'Finance'), child: const FinanceScreen())),
        _App(Icons.inventory_2_outlined, 'المخزون', 'Inventory', Brand.deep, (c) => PageFrame(title: L(c, 'المخزون', 'Inventory'), child: const InventoryScreen())),
        _App(Icons.account_tree_outlined, 'الهيكل', 'Structure', const Color(0xFF9333EA), (c) => PageFrame(title: L(c, 'الهيكل التنظيمي', 'Org structure'), child: const StructureScreen())),
        _App(Icons.badge_outlined, 'ملفي', 'My profile', const Color(0xFF475569), (c) => const ProfileScreen()),
      ];

  @override
  Widget build(BuildContext context) {
    final apps = _apps(context).where((a) => _q.isEmpty || a.ar.contains(_q) || a.en.toLowerCase().contains(_q.toLowerCase())).toList();
    return Scaffold(
      appBar: AppBar(title: Text(L(context, 'التطبيقات', 'Apps')), shape: const Border(bottom: BorderSide(color: Brand.line))),
      body: ListView(padding: const EdgeInsets.all(16), children: [
        TextField(
          onChanged: (v) => setState(() => _q = v.trim()),
          decoration: InputDecoration(prefixIcon: const Icon(Icons.search), hintText: L(context, 'ابحث عن تطبيق…', 'Search an app…')),
        ),
        const SizedBox(height: 14),
        SectionCard(
          child: GridView.count(
            crossAxisCount: 4, shrinkWrap: true, physics: const NeverScrollableScrollPhysics(), childAspectRatio: .74, mainAxisSpacing: 8,
            children: [
              for (final a in apps)
                InkWell(
                  borderRadius: BorderRadius.circular(16),
                  onTap: () => Navigator.of(context).push(MaterialPageRoute(builder: (c) => a.page(c))),
                  child: Column(children: [
                    Container(
                      width: 56, height: 56,
                      decoration: BoxDecoration(color: a.color, borderRadius: BorderRadius.circular(18)),
                      child: Icon(a.icon, color: Colors.white, size: 26),
                    ),
                    const SizedBox(height: 6),
                    Text(L(context, a.ar, a.en), textAlign: TextAlign.center, maxLines: 2, style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600, color: Brand.ink2)),
                  ]),
                ),
            ],
          ),
        ),
      ]),
    );
  }
}
