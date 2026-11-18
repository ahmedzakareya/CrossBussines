import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../models/employee.dart';
import '../providers/auth_provider.dart';
import '../providers/locale_provider.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../widgets/ui.dart';
import 'attendance_screen.dart';
import 'leave_screen.dart';
import 'payslips_screen.dart';
import 'profile_screen.dart';
import 'requests_screen.dart';

/// ACCOUNT (the reference's "حسابي"): the photo, the name and the post; the facts as a data list; my services; the
/// language; sign-out.
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
    return FutureBuilder<Employee?>(
      future: _me,
      builder: (context, s) {
        final me = s.data;
        final ar = isAr(context);
        String pick(String? a, String? e) => ((ar ? a : (e ?? a)) ?? '').trim();
        final name = pick(me?.fullName, me?.fullNameEn);
        final job = pick(me?.jobTitleAr, me?.jobTitleEn);
        final branch = pick(me?.branchAr, me?.branchEn);
        return ExecBody(children: [
          Padding(
            padding: const EdgeInsets.only(top: 10, bottom: 25),
            child: Column(children: [
              PersonAvatar(photo: me?.profileImage, name: name, size: 68, ring: true),
              const SizedBox(height: 14),
              Text(name, textAlign: TextAlign.center, style: T.h1),
              Text([job, branch].where((x) => x.isNotEmpty).join(' · '), textAlign: TextAlign.center, style: T.sub),
            ]),
          ),
          DataList(children: [
            KV(L(context, 'الشركة', 'Company'), pick(me?.companyAr, me?.companyEn)),
            KV(L(context, 'الإدارة', 'Department'), pick(me?.departmentAr, me?.departmentEn)),
            KV(L(context, 'البريد', 'Email'), me?.email ?? ''),
            KV(L(context, 'اللغة', 'Language'), '', trailing: GestureDetector(
              onTap: () => context.read<LocaleProvider>().toggle(),
              child: Row(children: [
                Text(ar ? 'العربية' : 'English', style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w500)),
                const SizedBox(width: 6),
                Text(ar ? 'English ↔' : '↔ العربية', style: const TextStyle(fontSize: 10.5, color: Color(0xFF3A5795))),
              ]),
            )),
          ]),
          SectionHeading(L(context, 'خدماتي', 'My services')),
          DataList(children: [
            ItemRow(leading: const IconBox(icon: Lu.badge), title: L(context, 'ملفي الشخصي', 'My profile'), subtitle: L(context, 'البيانات · الطوارئ · المستندات', 'Details · emergency · documents'), onTap: () => _open(const ProfileScreen())),
            ItemRow(leading: const IconBox(icon: Lu.receiptText), title: L(context, 'قسائمي', 'My payslips'), subtitle: L(context, 'الرواتب الشهرية', 'Monthly pay'), onTap: () => _open(const PayslipsScreen())),
            ItemRow(leading: const IconBox(icon: Lu.clock3), title: L(context, 'حضوري', 'My attendance'), subtitle: L(context, 'الشهر والبصمات', 'The month and punches'), onTap: () => _open(const AttendanceScreen())),
            ItemRow(leading: const IconBox(icon: Lu.calendarDays), title: L(context, 'إجازاتي', 'My leave'), subtitle: L(context, 'الأرصدة والطلبات', 'Balances and requests'), onTap: () => _open(const LeaveScreen())),
            ItemRow(leading: const IconBox(icon: Lu.fileText), title: L(context, 'طلباتي', 'My requests'), subtitle: L(context, 'خطابات · سلف · أذونات', 'Letters · advances · permissions'), onTap: () => _open(const RequestsScreen())),
          ]),
          const SizedBox(height: 18),
          OutlinedButton.icon(
            onPressed: () async {
              AppNav.resetTabs();
              await context.read<AuthProvider>().logout();
            },
            icon: const Icon(Lu.logOut, size: 16),
            label: Text(L(context, 'تسجيل الخروج', 'Sign out')),
          ),
        ]);
      },
    );
  }
}
