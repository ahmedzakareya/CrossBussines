import 'package:flutter/material.dart';

import '../theme/brand.dart';
import '../widgets/ui.dart';
import 'attendance_screen.dart';
import 'calls/calls_screen.dart';
import 'chat/chats_screen.dart';
import 'finance_screen.dart';
import 'inventory_screen.dart';
import 'leave_screen.dart';
import 'payslips_screen.dart';
import 'rep_mode.dart';
import 'requests_screen.dart';
import 'store_mode.dart';
import 'structure_screen.dart';

/// WORKSPACES (the reference's "مساحات العمل"): every area as an outlined card in a two-column grid — icon, name and
/// what it holds — then the two work modes on the soft colour. A module the user may not use answers inside with the
/// server's own refusal.
class AppsTab extends StatelessWidget {
  const AppsTab({super.key});

  @override
  Widget build(BuildContext context) {
    void open(Widget page) => Navigator.of(context).push(MaterialPageRoute(builder: (_) => page));
    final modules = <(IconData, String, String, String, String, Widget Function(BuildContext))>[
      (Lu.wallet, 'المالية', 'Finance', 'السيولة والحسابات', 'Cash and accounts', (c) => const FinanceScreen()),
      (Lu.package, 'المخزون', 'Inventory', 'الأصناف والمستودعات', 'Items and warehouses', (c) => const InventoryScreen()),
      (Lu.calendarDays, 'الإجازات', 'Leave', 'الأرصدة والطلبات', 'Balances and requests', (c) => const LeaveScreen()),
      (Lu.clock3, 'الحضور', 'Attendance', 'البصمة والساعات', 'Punches and hours', (c) => const AttendanceScreen()),
      (Lu.receiptText, 'قسائمي', 'Payslips', 'الرواتب الشهرية', 'Monthly pay', (c) => const PayslipsScreen()),
      (Lu.fileText, 'طلباتي', 'My requests', 'خطابات · سلف · أذونات', 'Letters · advances · permissions', (c) => const RequestsScreen()),
      (Lu.messageCircle, 'المحادثات', 'Chats', 'مجموعات وقنوات', 'Groups and channels', (c) => const ChatsScreen()),
      (Lu.phoneCall, 'الاتصال الداخلي', 'Internal calls', 'صوت وفيديو داخل الشبكة', 'Voice and video on the network', (c) => const CallsScreen()),
      (Lu.history, 'سجل المكالمات', 'Call log', 'الفائتة والمسجّلة', 'Missed and recorded', (c) => const CallsScreen(tab: 1)),
      (Lu.usersRound, 'فريق العمل', 'My team', 'الموظفون والهيكل', 'People and structure', (c) => const StructureScreen()),
    ];
    final modes = <(IconData, String, String, String, String, Widget Function(BuildContext))>[
      (Lu.scanBarcode, 'وضع أمين المخزن', 'Storekeeper mode', 'جرد بالمسح · تحويل', 'Scan counts · transfers', (c) => const StoreModeScreen()),
      (Lu.route, 'وضع المندوب', 'Sales-rep mode', 'زيارات · طلبية · تحصيل', 'Visits · orders · collections', (c) => const RepModeScreen()),
    ];
    Widget card((IconData, String, String, String, String, Widget Function(BuildContext)) m, {bool mode = false}) => Material(
          color: mode ? Brand.soft : Brand.paper,
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(18), side: BorderSide(color: mode ? Colors.transparent : Brand.lineC)),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: () => open(Builder(builder: m.$6)),
            child: Padding(
              padding: const EdgeInsets.fromLTRB(15, 20, 15, 18),
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Icon(m.$1, size: 21, color: mode ? Brand.blue : Brand.support),
                const SizedBox(height: 11),
                Text(L(context, m.$2, m.$3), style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w500, color: Brand.inkC)),
                const SizedBox(height: 4),
                Text(L(context, m.$4, m.$5), maxLines: 1, overflow: TextOverflow.ellipsis, style: T.small),
              ]),
            ),
          ),
        );
    Widget grid(List<(IconData, String, String, String, String, Widget Function(BuildContext))> items, {bool mode = false}) => GridView.count(
          crossAxisCount: 2, shrinkWrap: true, physics: const NeverScrollableScrollPhysics(), padding: EdgeInsets.zero,
          mainAxisSpacing: 11, crossAxisSpacing: 11, childAspectRatio: 1.32,
          children: [for (final m in items) card(m, mode: mode)],
        );
    return ExecBody(children: [
      PageHead(L(context, 'مساحات العمل', 'Workspaces'), eyebrow: L(context, 'إدارة مترابطة لأعمالك', 'Your business, connected'), sub: L(context, 'كل أدواتك، تحت هوية واحدة', 'All your tools, under one identity')),
      grid(modules),
      SectionHeading(L(context, 'أوضاع العمل', 'Work modes')),
      grid(modes, mode: true),
    ]);
  }
}
