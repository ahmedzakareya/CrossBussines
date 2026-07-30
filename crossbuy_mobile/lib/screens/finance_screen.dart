import 'package:flutter/material.dart';

import '../l10n/app_localizations.dart';
import '../models/finance_summary.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../widgets/app_ui.dart';

class FinanceScreen extends StatefulWidget {
  const FinanceScreen({super.key});
  @override
  State<FinanceScreen> createState() => _FinanceScreenState();
}

class _FinanceScreenState extends State<FinanceScreen> {
  late Future<FinanceSummary> _future;

  static const _monthsAr = ['', 'يناير', 'فبراير', 'مارس', 'أبريل', 'مايو', 'يونيو', 'يوليو', 'أغسطس', 'سبتمبر', 'أكتوبر', 'نوفمبر', 'ديسمبر'];
  static const _monthsEn = ['', 'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

  @override
  void initState() {
    super.initState();
    _future = ApiService.instance.getFinanceSummary();
    AppNav.dataChanged.addListener(_reload);
  }

  void _reload() {
    if (mounted) setState(() => _future = ApiService.instance.getFinanceSummary());
  }

  @override
  void dispose() {
    AppNav.dataChanged.removeListener(_reload);
    super.dispose();
  }

  String _money(bool ar, double v) {
    final neg = v < 0;
    final s = v.abs().toStringAsFixed(2).replaceAllMapped(
        RegExp(r'\B(?=(\d{3})+(?!\d))'), (m) => ',');
    return neg ? '($s)' : s;
  }

  String _date(bool ar, DateTime? d) {
    if (d == null) return '-';
    final m = ar ? _monthsAr[d.month] : _monthsEn[d.month];
    return '${d.day} $m ${d.year}';
  }

  @override
  Widget build(BuildContext context) {
    final t = AppLocalizations.of(context);
    final ar = t.isAr;
    return Container(
      color: kBg,
      child: Column(
        children: [
          AppHeader(title: t.t('finance')),
          Expanded(
            child: FutureBuilder<FinanceSummary>(
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
                      FilledButton.tonal(onPressed: _reload, child: Text(t.t('retry'))),
                    ]),
                  );
                }
                final s = snap.data!;
                final profit = s.netResult >= 0;
                return RefreshIndicator(
                  onRefresh: () async => _reload(),
                  child: ListView(
                    padding: const EdgeInsets.fromLTRB(12, 14, 12, 24),
                    children: [
                      Row(children: [
                        Expanded(child: _kpi(t.t('cash'), _money(ar, s.cash), Icons.account_balance_wallet, s.cash >= 0 ? kGreen : _red)),
                        const SizedBox(width: 10),
                        Expanded(child: _kpi(profit ? t.t('netProfit') : t.t('netLoss'), _money(ar, s.netResult), Icons.trending_up, profit ? kGreen : _red)),
                      ]),
                      const SizedBox(height: 10),
                      Row(children: [
                        Expanded(child: _kpi(t.t('receivables'), _money(ar, s.arTotal), Icons.south_west, kPrimary)),
                        const SizedBox(width: 10),
                        Expanded(child: _kpi(t.t('payables'), _money(ar, s.apTotal), Icons.north_east, _amber)),
                      ]),
                      const SizedBox(height: 14),
                      SectionCard(
                        icon: Icons.assessment_outlined,
                        title: t.t('resultThisYear'),
                        children: [
                          _row(t.t('revenue'), _money(ar, s.revenue), kGreen),
                          _row(t.t('expenses'), _money(ar, s.expense), _red),
                          const Divider(height: 18),
                          _row(profit ? t.t('netProfit') : t.t('netLoss'), _money(ar, s.netResult), profit ? kGreen : _red, bold: true),
                        ],
                      ),
                      const SizedBox(height: 14),
                      SectionCard(
                        icon: Icons.groups_outlined,
                        title: t.t('partners'),
                        children: [
                          _row(t.t('customers'), '${s.customers}', kInk),
                          _row(t.t('vendors'), '${s.vendors}', kInk),
                        ],
                      ),
                      const SizedBox(height: 14),
                      SectionCard(
                        icon: Icons.receipt_long_outlined,
                        title: t.t('recentEntries'),
                        children: [
                          if (s.recent.isEmpty)
                            Padding(padding: const EdgeInsets.all(8), child: Text(t.t('noData'), style: const TextStyle(color: kMuted)))
                          else
                            ...s.recent.map((e) => _entry(ar, e)),
                        ],
                      ),
                    ],
                  ),
                );
              },
            ),
          ),
        ],
      ),
    );
  }

  static const _red = Color(0xFFD9214E);
  static const _amber = Color(0xFFF6C000);

  Widget _kpi(String label, String value, IconData icon, Color color) => Container(
        padding: const EdgeInsets.all(14),
        decoration: BoxDecoration(
          color: Colors.white,
          borderRadius: BorderRadius.circular(18),
          boxShadow: [BoxShadow(color: Colors.black.withValues(alpha: 0.04), blurRadius: 12, offset: const Offset(0, 4))],
        ),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Container(
            width: 34, height: 34,
            decoration: BoxDecoration(color: color.withValues(alpha: 0.12), borderRadius: BorderRadius.circular(9)),
            child: Icon(icon, color: color, size: 18),
          ),
          const SizedBox(height: 10),
          Text(value, style: TextStyle(fontSize: 18, fontWeight: FontWeight.bold, color: color)),
          const SizedBox(height: 2),
          Text(label, style: const TextStyle(fontSize: 11, color: kMuted)),
        ]),
      );

  Widget _row(String label, String value, Color color, {bool bold = false}) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 6),
        child: Row(children: [
          Expanded(child: Text(label, style: TextStyle(fontSize: 13, color: kInk, fontWeight: bold ? FontWeight.bold : FontWeight.w500))),
          Text(value, style: TextStyle(fontSize: bold ? 15 : 13.5, fontWeight: FontWeight.bold, color: color)),
        ]),
      );

  Widget _entry(bool ar, RecentEntry e) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 7),
        child: Row(children: [
          Expanded(
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(e.description ?? e.entryNo ?? '-', maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600, color: kInk)),
              const SizedBox(height: 2),
              Text('${e.entryNo ?? ''} · ${_date(ar, e.entryDate)}', style: const TextStyle(fontSize: 10.5, color: kMuted)),
            ]),
          ),
          const SizedBox(width: 8),
          Text(_money(ar, e.amount), style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.bold, color: kInk)),
        ]),
      );
}
