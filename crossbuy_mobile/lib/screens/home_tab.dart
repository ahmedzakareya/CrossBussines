import 'package:flutter/material.dart';

import '../models/dashboard.dart';
import '../models/employee.dart';
import '../models/ess.dart';
import '../models/finance_summary.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';
import 'attendance_screen.dart';
import 'inbox_tab.dart' show ApprovalDetailPage;
import 'leave_screen.dart';
import 'payslips_screen.dart';
import 'structure_screen.dart';

/// HOME — the owner's reference, screen for screen: the date and the greeting with the photo; the financial card
/// (cash & banks with receivables / payables — or, for an employee without the financial summary, the last net pay);
/// quick access; and "needs your attention" — the approvals waiting for me as request cards. What appears is decided
/// by the server's answers, never by a role guessed on the phone.
class HomeTab extends StatefulWidget {
  const HomeTab({super.key});
  @override
  State<HomeTab> createState() => _HomeTabState();
}

class _HomeData {
  Employee? me;
  DashboardData? dash;
  List<ApprovalItem> approvals = [];
  String currency = '';
  List<PayslipSummary> payslips = [];
  FinanceSummary? finance;
}

class _HomeTabState extends State<HomeTab> {
  late Future<_HomeData> _future;

  @override
  void initState() {
    super.initState();
    _future = _load();
    AppNav.dataChanged.addListener(_reload);
  }

  @override
  void dispose() {
    AppNav.dataChanged.removeListener(_reload);
    super.dispose();
  }

  void _reload() {
    if (mounted) setState(() => _future = _load());
  }

  Future<_HomeData> _load() async {
    final api = ApiService.instance;
    final d = _HomeData();
    // each part is independent: one failing endpoint never blanks Home
    await Future.wait([
      api.getProfile().then<void>((v) { d.me = v; }).catchError((_) {}),
      api.getDashboard().then<void>((v) { d.dash = v; }).catchError((_) {}),
      api.getApprovals().then<void>((v) { d.currency = v.$1; d.approvals = v.$2; }).catchError((_) {}),
      api.getPayslips().then<void>((v) { if (d.currency.isEmpty) d.currency = v.$1; d.payslips = v.$2; }).catchError((_) {}),
      api.tryFinanceSummary().then<void>((v) { d.finance = v; }),
    ]);
    return d;
  }

  void _open(Widget page) => Navigator.of(context).push(MaterialPageRoute(builder: (_) => page)).then((_) => _reload());

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<_HomeData>(
      future: _future,
      builder: (context, snap) {
        final d = snap.data;
        if (d == null) return const Center(child: CircularProgressIndicator());
        final ar = isAr(context);
        final first = ((ar ? d.me?.fullName : (d.me?.fullNameEn ?? d.me?.fullName)) ?? '').trim().split(RegExp(r'\s+')).first;
        final now = DateTime.now();
        final greet = now.hour < 12 ? L(context, 'صباح الخير', 'Good morning') : L(context, 'مساء الخير', 'Good evening');
        final cur = curLabel(d.currency, ar);
        return ExecBody(
          onRefresh: () async => _reload(),
          children: [
            Padding(
              padding: const EdgeInsets.only(bottom: 20),
              child: Row(children: [
                Expanded(
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text('${weekdayName(context, now.weekday)}، ${now.day} ${monthName(context, now.month)}', style: T.eyebrow),
                    const SizedBox(height: 3),
                    Text(first.isEmpty ? greet : '$greet، $first', style: T.h1),
                  ]),
                ),
                PersonAvatar(photo: d.me?.profileImage, name: (ar ? d.me?.fullName : d.me?.fullNameEn) ?? '', size: 46, ring: true),
              ]),
            ),
            _financial(context, d, cur),
            SectionHeading(L(context, 'وصول سريع', 'Quick access'), action: L(context, 'كل الأقسام', 'All sections'), onAction: () => AppNav.tabIndex.value = AppNav.apps),
            Row(children: [
              Expanded(child: QuickTile(icon: Lu.calendarPlus, label: L(context, 'طلب إجازة', 'Request leave'), onTap: () => _open(const NewLeaveScreen()))),
              Expanded(child: QuickTile(icon: Lu.fingerprint, label: L(context, 'حضوري', 'Attendance'), onTap: () => _open(const AttendanceScreen()))),
              Expanded(child: QuickTile(icon: Lu.receiptText, label: L(context, 'قسيمتي', 'My payslip'), onTap: () => _open(const PayslipsScreen()))),
              Expanded(child: QuickTile(icon: Lu.usersRound, label: L(context, 'فريق العمل', 'My team'), onTap: () => _open(const StructureScreen()))),
            ]),
            SectionHeading(L(context, 'يحتاج انتباهك', 'Needs your attention'), count: d.approvals.length,
                action: L(context, 'عرض الكل', 'See all'), onAction: () => AppNav.tabIndex.value = AppNav.inbox),
            if (d.approvals.isEmpty) SoftNote(L(context, 'تمت مراجعة جميع الطلبات', 'Everything has been reviewed')),
            for (final a in d.approvals.take(2)) approvalCard(context, a, d.currency, () => _open(ApprovalDetailPage(item: a, currency: d.currency))),
          ],
        );
      },
    );
  }

  Widget _financial(BuildContext context, _HomeData d, String cur) {
    final f = d.finance;
    if (f != null) {
      // the ledger's own currency when the summary gives it; the HR pack's otherwise
      if (f.currency.isNotEmpty) cur = curLabel(f.currency, isAr(context));
      return FinancialCard(
        label: L(context, 'النقدية والبنوك', 'Cash & banks'),
        icon: Lu.wallet,
        amount: Amount(money(f.cash, f.currency.isNotEmpty ? f.currency : d.currency), cur),
        note: '${L(context, 'المركز المالي · ', 'Financial position · ')}${monthName(context, DateTime.now().month)} ${DateTime.now().year}',
        footer: [
          (L(context, 'مستحقات العملاء', 'Receivables'), whole(f.arTotal), cur),
          (L(context, 'مستحقات الموردين', 'Payables'), whole(f.apTotal), cur),
        ],
      );
    }
    if (d.payslips.isNotEmpty) {
      final p = d.payslips.first;
      return GestureDetector(
        onTap: () => _open(PayslipDetailScreen(id: p.id)),
        child: FinancialCard(
          label: L(context, 'صافي الراتب', 'Net pay'),
          icon: Lu.receiptText,
          amount: Amount(money(p.net, d.currency), cur),
          note: '${monthName(context, p.month)} ${p.year}',
        ),
      );
    }
    final b = d.dash?.balances.isNotEmpty == true ? d.dash!.balances.first : null;
    return FinancialCard(
      label: b == null ? L(context, 'اليوم', 'Today') : ((isAr(context) ? b.nameAr : (b.nameEn ?? b.nameAr)) ?? ''),
      icon: Lu.calendarDays,
      amount: b == null
          ? Text('${DateTime.now().day} ${monthName(context, DateTime.now().month)}', textAlign: TextAlign.end, style: T.h1.copyWith(fontSize: 32))
          : Amount('${b.remaining}', L(context, 'يوماً', 'days')),
      note: b == null ? '' : '${L(context, 'من ', 'of ')}${b.entitlement}',
    );
  }
}

/// A waiting approval as the reference's request card (shared by Home and Approvals).
Widget approvalCard(BuildContext context, ApprovalItem a, String currency, VoidCallback onTap) {
  final ar = isAr(context);
  final isLeave = a.kind == 'leave';
  return RequestCard(
    leading: IconBox(photo: a.photo, name: a.requester(ar)),
    title: approvalTitle(context, a),
    subtitle: a.requester(ar),
    tag: Tag(L(context, 'بانتظار اعتمادك', 'Awaiting your approval')),
    amount: isLeave ? '${a.days ?? 0}' : (a.amount == null ? '' : money(a.amount!, currency)),
    unit: isLeave ? L(context, 'أيام', 'days') : (a.amount == null ? '' : curLabel(currency, ar)),
    onTap: onTap,
  );
}

/// Title of an approvals-inbox item, shared by Home and Approvals.
String approvalTitle(BuildContext context, ApprovalItem a) {
  final ar = isAr(context);
  if (a.kind == 'leave') {
    final t = ((ar ? a.typeAr : a.type) ?? '').trim();
    final says = RegExp(r'[إا]جاز|leave', caseSensitive: false).hasMatch(t);
    return t.isEmpty ? L(context, 'إجازة', 'Leave') : (says ? t : '${L(context, 'إجازة', 'Leave')} $t');
  }
  switch (a.type) {
    case 'Advance':
      return L(context, 'سلفة راتب', 'Salary advance');
    case 'Permission':
      return L(context, 'إذن', 'Permission');
    case 'ProfileChange':
      return L(context, 'تعديل بيانات', 'Profile change');
    default:
      return L(context, 'طلب خطاب', 'Letter request');
  }
}

/// The dates (leave) or the amount (requests) of an approvals-inbox item.
String approvalSub(BuildContext context, ApprovalItem a, String currency) {
  if (a.kind == 'leave' && a.from != null) {
    final days = a.days ?? 0;
    // words, not a numeric range: "من 2 نوفمبر إلى 4 نوفمبر" reads the same in both directions
    final range = a.to == null || a.to == a.from ? dayMonth(context, a.from!) : '${L(context, 'من ', 'From ')}${dayMonth(context, a.from!)}${L(context, ' إلى ', ' to ')}${dayMonth(context, a.to!)}';
    return '$range · $days ${L(context, 'يوم', days == 1 ? 'day' : 'days')}';
  }
  if (a.amount != null) return '${money(a.amount!, currency)} ${curLabel(currency, isAr(context))}';
  return a.createdAt != null ? ymd(a.createdAt!) : '';
}
