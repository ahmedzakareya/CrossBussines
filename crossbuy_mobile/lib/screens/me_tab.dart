import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../models/employee.dart';
import '../providers/auth_provider.dart';
import '../providers/locale_provider.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';
import 'attendance_screen.dart';
import 'leave_screen.dart';
import 'payslips_screen.dart';
import 'profile_screen.dart';
import 'requests_screen.dart';

/// ME — everything about me in one place (Workday "Menu", Oracle "Me"): profile, payslips, leave, attendance,
/// requests, language and sign-out.
class MeTab extends StatefulWidget {
  const MeTab({super.key});
  @override
  State<MeTab> createState() => _MeTabState();
}

class _MeTabState extends State<MeTab> {
  late final Future<Employee?> _me = ApiService.instance.getProfile().then<Employee?>((v) => v).catchError((_) => null);

  void _open(Widget page) => Navigator.of(context).push(MaterialPageRoute(builder: (_) => page));

  @override
  Widget build(BuildContext context) {
    final rows = <(IconData, String, String, VoidCallback)>[
      (Icons.badge_outlined, 'ملفي الشخصي', 'My profile', () => _open(const ProfileScreen())),
      (Icons.account_balance_wallet_outlined, 'قسائمي', 'My payslips', () => _open(const PayslipsScreen())),
      (Icons.event_available_outlined, 'إجازاتي', 'My leave', () => _open(PageFrame(title: L(context, 'الإجازات', 'Leave'), child: const LeaveScreen()))),
      (Icons.schedule_outlined, 'حضوري', 'My attendance', () => _open(const AttendanceScreen())),
      (Icons.description_outlined, 'طلباتي (خطابات، سلف، أذونات)', 'My requests (letters, advances, permissions)', () => _open(const RequestsScreen())),
      (Icons.translate, 'English', 'العربية', () => context.read<LocaleProvider>().toggle()),
    ];
    return FutureBuilder<Employee?>(
      future: _me,
      builder: (context, s) {
        final me = s.data;
        final ar = isAr(context);
        final name = (ar ? me?.fullName : (me?.fullNameEn ?? me?.fullName)) ?? '';
        final job = (ar ? me?.jobTitleAr : (me?.jobTitleEn ?? me?.jobTitleAr)) ?? '';
        return ListView(padding: EdgeInsets.zero, children: [
          HeroBand(
            bottom: 70,
            child: Column(children: [
              PersonAvatar(photo: me?.profileImage, name: name, size: 72),
              const SizedBox(height: 10),
              Text(name, style: const TextStyle(fontSize: 19, fontWeight: FontWeight.w700, color: Colors.white)),
              Text(job, style: const TextStyle(fontSize: 13, color: Colors.white70)),
            ]),
          ),
          Transform.translate(
            offset: const Offset(0, -50),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16),
              child: Column(children: [
                SectionCard(
                  padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
                  child: Column(children: [
                    for (final r in rows)
                      RowItem(
                        leading: IconTile(r.$1),
                        title: L(context, r.$2, r.$3),
                        trailing: Icon(ar ? Icons.chevron_left : Icons.chevron_right, color: Brand.muted),
                        onTap: r.$4,
                      ),
                  ]),
                ),
                SizedBox(
                  width: double.infinity,
                  child: TextButton.icon(
                    onPressed: () async {
                      AppNav.resetTabs();
                      await context.read<AuthProvider>().logout();
                    },
                    icon: const Icon(Icons.logout, color: Brand.bad),
                    label: Text(L(context, 'تسجيل الخروج', 'Sign out'), style: const TextStyle(color: Brand.bad, fontWeight: FontWeight.w700)),
                  ),
                ),
              ]),
            ),
          ),
        ]);
      },
    );
  }
}
