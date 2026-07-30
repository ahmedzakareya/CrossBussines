import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../l10n/app_localizations.dart';
import '../models/dashboard.dart';
import '../providers/notification_provider.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../widgets/app_ui.dart';
import '../widgets/charts.dart';

class DashboardScreen extends StatefulWidget {
  const DashboardScreen({super.key});
  @override
  State<DashboardScreen> createState() => _DashboardScreenState();
}

class _DashboardScreenState extends State<DashboardScreen> {
  late Future<DashboardData> _future;
  NotificationProvider? _notif;
  int _lastNotifLen = 0;

  static const _monthsAr = ['', 'يناير', 'فبراير', 'مارس', 'أبريل', 'مايو', 'يونيو', 'يوليو', 'أغسطس', 'سبتمبر', 'أكتوبر', 'نوفمبر', 'ديسمبر'];
  static const _monthsEn = ['', 'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  static const _red = Color(0xFFD9214E);
  static const _amber = Color(0xFFF6C000);

  @override
  void initState() {
    super.initState();
    _future = ApiService.instance.getDashboard();
    AppNav.dataChanged.addListener(_reload);   // any create/approve/reject
    AppNav.tabIndex.addListener(_onTab);        // returning to the Home tab
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _notif = context.read<NotificationProvider>();
      _lastNotifLen = _notif!.items.length;
      _notif!.addListener(_onNotif);
    });
  }

  void _reload() {
    if (mounted) setState(() => _future = ApiService.instance.getDashboard());
  }

  void _onTab() {
    if (AppNav.tabIndex.value == 0) _reload();
  }

  void _onNotif() {
    final len = _notif?.items.length ?? 0;
    if (len > _lastNotifLen) _reload();
    _lastNotifLen = len;
  }

  @override
  void dispose() {
    AppNav.dataChanged.removeListener(_reload);
    AppNav.tabIndex.removeListener(_onTab);
    _notif?.removeListener(_onNotif);
    super.dispose();
  }

  String _nm(bool ar, String? a, String? b) =>
      (ar ? a : b)?.trim().isNotEmpty == true ? (ar ? a : b)! : (b ?? a ?? '-');

  @override
  Widget build(BuildContext context) {
    final t = AppLocalizations.of(context);
    return Container(
      color: kBg,
      child: Column(
        children: [
          AppHeader(title: t.t('dashboard')),
          Expanded(
            child: FutureBuilder<DashboardData>(
              future: _future,
              builder: (context, snap) {
                if (snap.connectionState == ConnectionState.waiting) {
                  return const Center(child: CircularProgressIndicator());
                }
                if (snap.hasError || !snap.hasData) {
                  return Center(
                    child: Column(mainAxisSize: MainAxisSize.min, children: [
                      const Icon(Icons.cloud_off, size: 54, color: kMuted),
                      const SizedBox(height: 10),
                      Text(t.t('noData')),
                      const SizedBox(height: 10),
                      FilledButton.tonal(onPressed: () => setState(() => _future = ApiService.instance.getDashboard()), child: Text(t.t('retry'))),
                    ]),
                  );
                }
                return RefreshIndicator(
                  onRefresh: () async => setState(() => _future = ApiService.instance.getDashboard()),
                  child: ListView(
                    padding: const EdgeInsets.fromLTRB(14, 16, 14, 24),
                    children: _body(context, t, snap.data!),
                  ),
                );
              },
            ),
          ),
        ],
      ),
    );
  }

  List<Widget> _body(BuildContext c, AppLocalizations t, DashboardData d) {
    final ar = t.isAr;
    return [
      _periodChip(ar),
      const SizedBox(height: 14),
      _overview(ar, d),
      const SizedBox(height: 16),
      _balances(ar, t, d),
      const SizedBox(height: 16),
      _distribution(ar, d),
      const SizedBox(height: 16),
      _trend(ar, d),
      const SizedBox(height: 16),
      _comparison(ar, d),
      const SizedBox(height: 16),
      _topTypes(ar, d),
      const SizedBox(height: 16),
      SizedBox(
        width: double.infinity,
        child: FilledButton.icon(
          onPressed: () => AppNav.goLeaves(sub: 0),
          icon: const Icon(Icons.add),
          label: Text(t.t('newLeave')),
          style: FilledButton.styleFrom(
            backgroundColor: kPrimary,
            padding: const EdgeInsets.symmetric(vertical: 14),
            shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
          ),
        ),
      ),
    ];
  }

  Widget _periodChip(bool ar) => Align(
        alignment: AlignmentDirectional.centerStart,
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 9),
          decoration: BoxDecoration(
            color: Colors.white,
            borderRadius: BorderRadius.circular(12),
            boxShadow: [BoxShadow(color: Colors.black.withValues(alpha: 0.04), blurRadius: 8)],
          ),
          child: Row(mainAxisSize: MainAxisSize.min, children: [
            const Icon(Icons.calendar_month, size: 16, color: kMuted),
            const SizedBox(width: 8),
            Text(ar ? 'هذا الشهر' : 'This month', style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600, color: kInk)),
            const SizedBox(width: 6),
            const Icon(Icons.keyboard_arrow_down, size: 18, color: kMuted),
          ]),
        ),
      );

  Widget _overview(bool ar, DashboardData d) {
    final cells = [
      [ar ? 'إجمالي الطلبات' : 'Total', d.total, kPrimary],
      [ar ? 'معتمدة' : 'Approved', d.approved, kGreen],
      [ar ? 'معلقة' : 'Pending', d.pending, _amber],
      [ar ? 'مرفوضة' : 'Rejected', d.rejected, _red],
    ];
    return SectionCard(
      icon: Icons.insights,
      title: ar ? 'نظرة عامة' : 'Overview',
      children: [
        GridView.count(
          crossAxisCount: 2, shrinkWrap: true, physics: const NeverScrollableScrollPhysics(),
          mainAxisSpacing: 10, crossAxisSpacing: 10, childAspectRatio: 2.3,
          children: cells.map((x) {
            final col = x[2] as Color;
            return Container(
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
              decoration: BoxDecoration(color: col.withValues(alpha: 0.08), borderRadius: BorderRadius.circular(12)),
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisAlignment: MainAxisAlignment.center, children: [
                Text(x[0] as String, style: TextStyle(fontSize: 11.5, color: col, fontWeight: FontWeight.w600)),
                const SizedBox(height: 3),
                Text('${x[1]}', style: TextStyle(fontSize: 23, fontWeight: FontWeight.bold, color: col)),
              ]),
            );
          }).toList(),
        ),
      ],
    );
  }

  Widget _balances(bool ar, AppLocalizations t, DashboardData d) => SectionCard(
        icon: Icons.beach_access,
        title: t.t('leaveBalances'),
        children: [
          if (!d.hasPolicy)
            Padding(padding: const EdgeInsets.symmetric(vertical: 16),
                child: Center(child: Text(ar ? 'لم يتم تسكينك على لائحة إجازات بعد' : 'You are not assigned to a leave policy yet', textAlign: TextAlign.center, style: const TextStyle(color: kMuted))))
          else if (d.balances.isEmpty)
            Padding(padding: const EdgeInsets.symmetric(vertical: 16), child: Center(child: Text(ar ? 'لا توجد أنواع إجازات' : 'No leave types', style: const TextStyle(color: kMuted))))
          else
            ...List.generate(d.balances.length, (i) => _balanceRow(ar, d.balances[i], kChartColors[i % kChartColors.length])),
        ],
      );

  Widget _balanceRow(bool ar, LeaveBalance b, Color col) {
    final pct = b.entitlement > 0 ? (b.remaining / b.entitlement).clamp(0.0, 1.0) : 0.0;
    return Container(
      margin: const EdgeInsets.only(bottom: 10),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(color: kChipBg, borderRadius: BorderRadius.circular(12)),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Row(children: [
          IconChip(Icons.event_available, color: col, size: 34),
          const SizedBox(width: 10),
          Expanded(child: Text(_nm(ar, b.nameAr, b.nameEn), style: const TextStyle(fontSize: 14, fontWeight: FontWeight.bold, color: kInk))),
          RichText(text: TextSpan(children: [
            TextSpan(text: '${b.remaining}', style: TextStyle(fontSize: 18, fontWeight: FontWeight.bold, color: col)),
            TextSpan(text: ar ? ' / ${b.entitlement} يوم' : ' / ${b.entitlement}d', style: const TextStyle(fontSize: 11.5, color: kMuted)),
          ])),
        ]),
        const SizedBox(height: 9),
        ClipRRect(borderRadius: BorderRadius.circular(6),
            child: LinearProgressIndicator(value: pct, minHeight: 7, backgroundColor: col.withValues(alpha: 0.14), valueColor: AlwaysStoppedAnimation(col))),
        const SizedBox(height: 4),
        Text(ar ? 'المستهلك: ${b.used}' : 'Used: ${b.used}', style: const TextStyle(fontSize: 10.5, color: kMuted)),
      ]),
    );
  }

  Widget _distribution(bool ar, DashboardData d) {
    final slices = [for (var i = 0; i < d.byType.length; i++) DonutSlice(d.byType[i].count.toDouble(), kChartColors[i % kChartColors.length])];
    return SectionCard(
      icon: Icons.donut_large,
      title: ar ? 'توزيع الطلبات' : 'Requests distribution',
      children: [
        d.total == 0
            ? Padding(padding: const EdgeInsets.symmetric(vertical: 16), child: Center(child: Text(ar ? 'لا توجد بيانات' : 'No data', style: const TextStyle(color: kMuted))))
            : Row(children: [
                DonutChart(slices: slices, centerTop: '${d.total}', centerBottom: ar ? 'الطلبات' : 'requests', size: 140),
                const SizedBox(width: 10),
                Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  for (var i = 0; i < d.byType.length; i++)
                    Padding(padding: const EdgeInsets.symmetric(vertical: 6), child: Row(children: [
                      Container(width: 11, height: 11, decoration: BoxDecoration(color: kChartColors[i % kChartColors.length], shape: BoxShape.circle)),
                      const SizedBox(width: 8),
                      Expanded(child: Text(_nm(ar, d.byType[i].nameAr, d.byType[i].nameEn), maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12.5, color: kInk))),
                      Text('${d.byType[i].pct}% (${d.byType[i].count})', style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.bold, color: kMuted)),
                    ])),
                ])),
              ]),
      ],
    );
  }

  Widget _trend(bool ar, DashboardData d) {
    final values = d.monthly.map((m) => m.count.toDouble()).toList();
    final labels = d.monthly.map((m) => (ar ? _monthsAr : _monthsEn)[m.month]).toList();
    return SectionCard(
      icon: Icons.show_chart,
      title: ar ? 'اتجاه الطلبات' : 'Requests trend',
      children: [
        Padding(padding: const EdgeInsets.only(top: 6),
            child: AreaLineChart(values: values, bottomLabels: labels, color: kPrimary, showAxis: true, height: 170)),
      ],
    );
  }

  Widget _comparison(bool ar, DashboardData d) {
    final up = d.pctChange >= 0;
    return SectionCard(
      icon: Icons.compare_arrows,
      title: ar ? 'مقارنة الفترات' : 'Period comparison',
      children: [
        Row(children: [
          Expanded(child: _cmpCell(ar ? 'الشهر الماضي' : 'Last month', d.lastMonth, kMuted)),
          const SizedBox(width: 10),
          Expanded(child: _cmpCell(ar ? 'هذا الشهر' : 'This month', d.thisMonth, kPrimary)),
        ]),
        const SizedBox(height: 10),
        Center(child: Row(mainAxisSize: MainAxisSize.min, children: [
          Icon(up ? Icons.arrow_upward : Icons.arrow_downward, color: up ? kGreen : _red, size: 16),
          Text(' ${d.pctChange.abs()}% ', style: TextStyle(color: up ? kGreen : _red, fontWeight: FontWeight.bold)),
          Text(ar ? 'تغيّر في الطلبات' : 'change in requests', style: const TextStyle(color: kMuted, fontSize: 12)),
        ])),
      ],
    );
  }

  Widget _cmpCell(String label, int value, Color col) => Container(
        padding: const EdgeInsets.symmetric(vertical: 14),
        decoration: BoxDecoration(color: col.withValues(alpha: 0.08), borderRadius: BorderRadius.circular(12)),
        child: Column(children: [
          Text(label, style: const TextStyle(fontSize: 11.5, color: kMuted)),
          const SizedBox(height: 4),
          Text('$value', style: TextStyle(fontSize: 24, fontWeight: FontWeight.bold, color: col)),
        ]),
      );

  Widget _topTypes(bool ar, DashboardData d) {
    final maxCount = d.byType.isEmpty ? 1 : d.byType.map((e) => e.count).reduce((a, b) => a > b ? a : b);
    return SectionCard(
      icon: Icons.leaderboard,
      title: ar ? 'أكثر أنواع الإجازات' : 'Top leave types',
      children: [
        if (d.byType.isEmpty)
          Padding(padding: const EdgeInsets.symmetric(vertical: 16), child: Center(child: Text(ar ? 'لا توجد بيانات' : 'No data', style: const TextStyle(color: kMuted))))
        else
          for (var i = 0; i < d.byType.length; i++)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 7),
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Row(children: [
                  Expanded(child: Text(_nm(ar, d.byType[i].nameAr, d.byType[i].nameEn), style: const TextStyle(fontSize: 12.5, color: kInk, fontWeight: FontWeight.w600))),
                  Text('${d.byType[i].count} (${d.byType[i].pct}%)', style: const TextStyle(fontSize: 11.5, color: kMuted, fontWeight: FontWeight.bold)),
                ]),
                const SizedBox(height: 5),
                ClipRRect(borderRadius: BorderRadius.circular(5),
                    child: LinearProgressIndicator(value: maxCount == 0 ? 0 : d.byType[i].count / maxCount, minHeight: 7,
                        backgroundColor: kChartColors[i % kChartColors.length].withValues(alpha: 0.14), valueColor: AlwaysStoppedAnimation(kChartColors[i % kChartColors.length]))),
              ]),
            ),
      ],
    );
  }
}
