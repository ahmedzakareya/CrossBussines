import 'package:flutter/material.dart';

import '../models/dashboard.dart';
import '../models/employee.dart';
import '../models/ess.dart';
import '../models/finance_summary.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';
import 'leave_screen.dart';
import 'payslips_screen.dart';
import 'requests_screen.dart';
import 'attendance_screen.dart';

/// HOME — "what needs me now" (owner-approved prototype). The cards follow what the signed-in user can actually see:
/// approvals appear when something waits for them, the business KPIs when the server grants the financial summary,
/// leave balances and the last payslip for everyone. No role is guessed on the phone; the server's answers decide.
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
  bool _revealPay = false;

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
    // each card is independent: one failing endpoint never blanks the whole Home
    await Future.wait([
      api.getProfile().then<void>((v) { d.me = v; }).catchError((_) {}),
      api.getDashboard().then<void>((v) { d.dash = v; }).catchError((_) {}),
      api.getApprovals().then((v) { d.currency = v.$1; d.approvals = v.$2; }).catchError((_) {}),
      api.getPayslips().then((v) { if (d.currency.isEmpty) d.currency = v.$1; d.payslips = v.$2; }).catchError((_) {}),
      api.tryFinanceSummary().then((v) => d.finance = v),
    ]);
    return d;
  }

  Future<void> _decide(ApprovalItem a, bool approve) async {
    final (ok, err) = await ApiService.instance.decide(a, approve);
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(
      content: Text(ok ? (approve ? L(context, 'تم الاعتماد وأُبلغ صاحب الطلب', 'Approved — the requester is notified') : L(context, 'رُفض الطلب وأُبلغ صاحبه', 'Rejected — the requester is notified'))
          : (err ?? L(context, 'تعذّر تنفيذ القرار', 'The decision could not be saved'))),
    ));
    if (ok) _reload();
  }

  void _open(Widget page) => Navigator.of(context).push(MaterialPageRoute(builder: (_) => page));

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<_HomeData>(
      future: _future,
      builder: (context, snap) {
        final d = snap.data;
        if (d == null) return const Center(child: CircularProgressIndicator());
        final ar = isAr(context);
        final name = (ar ? d.me?.fullName : (d.me?.fullNameEn ?? d.me?.fullName)) ?? '';
        final now = DateTime.now();
        return RefreshIndicator(
          onRefresh: () async => _reload(),
          child: ListView(padding: EdgeInsets.zero, children: [
            HeroBand(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Row(children: [
                  PersonAvatar(photo: d.me?.profileImage, name: name, size: 46),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Text(L(context, 'صباح الخير', 'Good morning'), style: const TextStyle(fontSize: 13, color: Colors.white70)),
                      Text(name, style: const TextStyle(fontSize: 19, fontWeight: FontWeight.w700, color: Colors.white)),
                    ]),
                  ),
                  IconButton(onPressed: () => AppNav.goInbox(filter: 2), icon: const Icon(Icons.notifications_none, color: Colors.white)),
                ]),
                const SizedBox(height: 12),
                Text('${now.day} ${monthName(context, now.month)} ${now.year}', style: const TextStyle(fontSize: 12.5, color: Colors.white70)),
              ]),
            ),
            Transform.translate(
              offset: const Offset(0, -44),
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 16),
                child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                  _quickActions(context),
                  if (d.finance != null) _kpis(context, d),
                  if (d.approvals.isNotEmpty) _approvals(context, d),
                  if (d.dash != null && d.dash!.balances.isNotEmpty) _balances(context, d.dash!),
                  if (d.payslips.isNotEmpty) _payslip(context, d),
                ]),
              ),
            ),
          ]),
        );
      },
    );
  }

  Widget _quickActions(BuildContext context) {
    final acts = <(IconData, String, String, Widget)>[
      (Icons.event_available_outlined, 'طلب إجازة', 'Leave', PageFrame(title: L(context, 'الإجازات', 'Leave'), child: const LeaveScreen())),
      (Icons.schedule_outlined, 'حضوري', 'Attendance', const AttendanceScreen()),
      (Icons.account_balance_wallet_outlined, 'قسائمي', 'Payslips', const PayslipsScreen()),
      (Icons.description_outlined, 'خطاب / سلفة', 'Letter / advance', const NewRequestScreen()),
    ];
    return SectionCard(
      child: Row(children: [
        for (final a in acts)
          Expanded(
            child: InkWell(
              borderRadius: BorderRadius.circular(14),
              onTap: () => _open(a.$4),
              child: Column(children: [
                IconTile(a.$1, size: 52),
                const SizedBox(height: 6),
                Text(L(context, a.$2, a.$3), textAlign: TextAlign.center, style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600, color: Brand.ink2)),
              ]),
            ),
          ),
      ]),
    );
  }

  Widget _kpis(BuildContext context, _HomeData d) {
    final f = d.finance!;
    final cur = d.currency.isEmpty ? '' : ' ${d.currency}';
    Widget tile(String ar, String en, double v, {bool bad = false}) => Expanded(
          child: Container(
            margin: const EdgeInsets.all(4),
            padding: const EdgeInsets.all(14),
            decoration: BoxDecoration(color: Colors.white, borderRadius: BorderRadius.circular(16)),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(L(context, ar, en), style: const TextStyle(fontSize: 11.5, color: Brand.muted)),
              const SizedBox(height: 4),
              Text('${money(v, d.currency)}$cur', style: TextStyle(fontSize: 17, fontWeight: FontWeight.w700, color: bad ? Brand.bad : Brand.ink)),
            ]),
          ),
        );
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Padding(padding: const EdgeInsets.fromLTRB(4, 4, 4, 6), child: Text(L(context, 'أعمالك', 'Your business'), style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: Brand.muted))),
        Row(children: [tile('النقدية', 'Cash', f.cash), tile('إيرادات السنة', 'Revenue YTD', f.revenue)]),
        Row(children: [tile('مستحقات العملاء', 'Receivables', f.arTotal), tile('مستحقات الموردين', 'Payables', f.apTotal, bad: true)]),
      ]),
    );
  }

  Widget _approvals(BuildContext context, _HomeData d) {
    final ar = isAr(context);
    return SectionCard(
      title: '${L(context, 'بانتظار اعتمادك', 'Waiting for you')} ${d.approvals.length}',
      action: TextButton(onPressed: () => AppNav.goInbox(filter: 1), child: Text(L(context, 'الكل', 'All'))),
      child: Column(children: [
        for (final a in d.approvals.take(3))
          Container(
            margin: const EdgeInsets.only(bottom: 10),
            padding: const EdgeInsets.all(12),
            decoration: BoxDecoration(border: Border.all(color: Brand.line), borderRadius: BorderRadius.circular(16)),
            child: Column(children: [
              Row(children: [
                PersonAvatar(photo: null, name: a.requester(ar), size: 34),
                const SizedBox(width: 10),
                Expanded(
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text(approvalTitle(context, a), style: const TextStyle(fontWeight: FontWeight.w600)),
                    Text('${a.requester(ar)} · ${approvalSub(context, a, d.currency)}', maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12, color: Brand.muted)),
                  ]),
                ),
              ]),
              const SizedBox(height: 10),
              Row(children: [
                Expanded(child: OutlinedButton(onPressed: () => _decide(a, false), style: OutlinedButton.styleFrom(foregroundColor: Brand.bad, side: const BorderSide(color: Color(0xFFFECACA))), child: Text(L(context, 'رفض', 'Reject')))),
                const SizedBox(width: 8),
                Expanded(child: FilledButton(onPressed: () => _decide(a, true), style: FilledButton.styleFrom(backgroundColor: Brand.ok, minimumSize: const Size(0, 40)), child: Text(L(context, 'اعتماد', 'Approve')))),
              ]),
            ]),
          ),
      ]),
    );
  }

  Widget _balances(BuildContext context, DashboardData dash) {
    final ar = isAr(context);
    return SectionCard(
      title: L(context, 'أرصدة إجازاتي', 'My leave balances'),
      child: SizedBox(
        height: 104,
        child: ListView(scrollDirection: Axis.horizontal, children: [
          for (final b in dash.balances)
            Container(
              width: 120, margin: const EdgeInsetsDirectional.only(end: 10), padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(color: Brand.tint2, borderRadius: BorderRadius.circular(14)),
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text((ar ? b.nameAr : (b.nameEn ?? b.nameAr)) ?? '', maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 11.5, color: Brand.muted)),
                Text('${b.remaining}', style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w700, color: Brand.deep)),
                Text('${L(context, 'من ', 'of ')}${b.entitlement}', style: const TextStyle(fontSize: 11.5, color: Brand.muted)),
                const Spacer(),
                ClipRRect(
                  borderRadius: BorderRadius.circular(99),
                  child: LinearProgressIndicator(value: b.entitlement == 0 ? 0 : (b.remaining / b.entitlement).clamp(0, 1).toDouble(), minHeight: 6, backgroundColor: Brand.line, color: Brand.brand),
                ),
              ]),
            ),
        ]),
      ),
    );
  }

  Widget _payslip(BuildContext context, _HomeData d) {
    final p = d.payslips.first;
    return SectionCard(
      onTap: () => _open(PayslipDetailScreen(id: p.id)),
      child: Row(children: [
        const IconTile(Icons.account_balance_wallet_outlined),
        const SizedBox(width: 12),
        Expanded(
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text('${L(context, 'قسيمة ', 'Payslip · ')}${monthName(context, p.month)} ${p.year}', style: const TextStyle(fontWeight: FontWeight.w600)),
            Text('${L(context, 'الصافي ', 'Net ')}${_revealPay ? '${money(p.net, d.currency)} ${d.currency}' : '••••••'}', style: const TextStyle(fontSize: 12.5, color: Brand.muted)),
          ]),
        ),
        // the amount stays hidden until asked for (shoulder-surfing, screen sharing)
        IconButton(onPressed: () => setState(() => _revealPay = !_revealPay), icon: Icon(_revealPay ? Icons.visibility_off_outlined : Icons.visibility_outlined, color: Brand.brand)),
      ]),
    );
  }
}

/// Title and subtitle of an approvals-inbox item, shared by Home and Inbox.
String approvalTitle(BuildContext context, ApprovalItem a) {
  final ar = isAr(context);
  if (a.kind == 'leave') return '${L(context, 'إجازة', 'Leave')} ${(ar ? a.typeAr : a.type) ?? ''}'.trim();
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

String approvalSub(BuildContext context, ApprovalItem a, String currency) {
  if (a.kind == 'leave' && a.from != null) {
    final days = a.days ?? 0;
    return '${ymd(a.from!)}${a.to != null && a.to != a.from ? ' → ${ymd(a.to!)}' : ''} · $days ${L(context, 'يوم', days == 1 ? 'day' : 'days')}';
  }
  if (a.amount != null) return '${money(a.amount!, currency)} $currency';
  return a.createdAt != null ? ymd(a.createdAt!) : '';
}
