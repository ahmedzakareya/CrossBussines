import 'package:flutter/material.dart';

import '../l10n/app_localizations.dart';
import '../models/dashboard.dart';
import '../services/api_service.dart';
import '../widgets/app_ui.dart';
import '../widgets/charts.dart';

class StatisticsScreen extends StatefulWidget {
  const StatisticsScreen({super.key});
  @override
  State<StatisticsScreen> createState() => _StatisticsScreenState();
}

class _StatisticsScreenState extends State<StatisticsScreen> {
  late Future<DashboardData> _future;
  static const _monthsAr = ['', 'يناير', 'فبراير', 'مارس', 'أبريل', 'مايو', 'يونيو', 'يوليو', 'أغسطس', 'سبتمبر', 'أكتوبر', 'نوفمبر', 'ديسمبر'];
  static const _monthsEn = ['', 'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

  @override
  void initState() {
    super.initState();
    _future = ApiService.instance.getDashboard();
  }

  @override
  Widget build(BuildContext context) {
    final t = AppLocalizations.of(context);
    final ar = t.isAr;
    return Scaffold(
      backgroundColor: kBg,
      body: Column(
        children: [
          _header(context, ar),
          Expanded(
            child: FutureBuilder<DashboardData>(
              future: _future,
              builder: (context, snap) {
                if (snap.connectionState == ConnectionState.waiting) {
                  return const Center(child: CircularProgressIndicator());
                }
                if (!snap.hasData) {
                  return Center(child: Text(t.t('noData')));
                }
                final d = snap.data!;
                return ListView(
                  padding: const EdgeInsets.fromLTRB(14, 16, 14, 24),
                  children: [
                    _overview(ar, d),
                    const SizedBox(height: 16),
                    _distribution(ar, d),
                    const SizedBox(height: 16),
                    _trend(ar, d),
                    const SizedBox(height: 16),
                    _comparison(ar, d),
                    const SizedBox(height: 16),
                    _topTypes(ar, d),
                  ],
                );
              },
            ),
          ),
        ],
      ),
    );
  }

  Widget _header(BuildContext c, bool ar) => Container(
        decoration: const BoxDecoration(
          gradient: LinearGradient(begin: Alignment.topRight, end: Alignment.bottomLeft, colors: [Color(0xFF0A1B40), Color(0xFF1C4A8E)]),
        ),
        child: SafeArea(
          bottom: false,
          child: Padding(
            padding: const EdgeInsets.fromLTRB(14, 10, 14, 14),
            child: Row(children: [
              InkWell(
                onTap: () => Navigator.of(c).maybePop(),
                borderRadius: BorderRadius.circular(30),
                child: Container(width: 40, height: 40,
                    decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.16), shape: BoxShape.circle),
                    child: Icon(ar ? Icons.arrow_forward : Icons.arrow_back, color: Colors.white, size: 20)),
              ),
              Expanded(child: Center(child: Text(ar ? 'الإحصائيات' : 'Statistics',
                  style: const TextStyle(color: Colors.white, fontSize: 18, fontWeight: FontWeight.bold)))),
              const SizedBox(width: 40),
            ]),
          ),
        ),
      );

  Widget _overview(bool ar, DashboardData d) {
    final cells = [
      [ar ? 'إجمالي الطلبات' : 'Total', d.total, kPrimary],
      [ar ? 'معتمدة' : 'Approved', d.approved, kGreen],
      [ar ? 'معلقة' : 'Pending', d.pending, const Color(0xFFF6C000)],
      [ar ? 'مرفوضة' : 'Rejected', d.rejected, const Color(0xFFD9214E)],
    ];
    return SectionCard(
      icon: Icons.insights,
      title: ar ? 'نظرة عامة' : 'Overview',
      children: [
        GridView.count(
          crossAxisCount: 2, shrinkWrap: true, physics: const NeverScrollableScrollPhysics(),
          mainAxisSpacing: 10, crossAxisSpacing: 10, childAspectRatio: 2.4,
          children: cells.map((x) {
            final col = x[2] as Color;
            return Container(
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
              decoration: BoxDecoration(color: col.withValues(alpha: 0.08), borderRadius: BorderRadius.circular(12)),
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisAlignment: MainAxisAlignment.center, children: [
                Text(x[0] as String, style: TextStyle(fontSize: 11.5, color: col, fontWeight: FontWeight.w600)),
                const SizedBox(height: 2),
                Text('${x[1]}', style: TextStyle(fontSize: 22, fontWeight: FontWeight.bold, color: col)),
              ]),
            );
          }).toList(),
        ),
      ],
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
                      Expanded(child: Text((ar ? d.byType[i].nameAr : d.byType[i].nameEn) ?? '-', maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12.5, color: kInk))),
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
        Padding(
          padding: const EdgeInsets.only(top: 6),
          child: AreaLineChart(values: values, bottomLabels: labels, color: kPrimary, showAxis: true, height: 170),
        ),
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
          Icon(up ? Icons.arrow_upward : Icons.arrow_downward, color: up ? kGreen : const Color(0xFFD9214E), size: 16),
          Text(' ${d.pctChange.abs()}% ', style: TextStyle(color: up ? kGreen : const Color(0xFFD9214E), fontWeight: FontWeight.bold)),
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
    final maxCount = d.byType.isEmpty ? 1 : d.byType.map((t) => t.count).reduce((a, b) => a > b ? a : b);
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
                  Expanded(child: Text((ar ? d.byType[i].nameAr : d.byType[i].nameEn) ?? '-', style: const TextStyle(fontSize: 12.5, color: kInk, fontWeight: FontWeight.w600))),
                  Text('${d.byType[i].count} (${d.byType[i].pct}%)', style: const TextStyle(fontSize: 11.5, color: kMuted, fontWeight: FontWeight.bold)),
                ]),
                const SizedBox(height: 5),
                ClipRRect(
                  borderRadius: BorderRadius.circular(5),
                  child: LinearProgressIndicator(
                    value: maxCount == 0 ? 0 : d.byType[i].count / maxCount,
                    minHeight: 7,
                    backgroundColor: kChartColors[i % kChartColors.length].withValues(alpha: 0.14),
                    valueColor: AlwaysStoppedAnimation(kChartColors[i % kChartColors.length]),
                  ),
                ),
              ]),
            ),
      ],
    );
  }
}
