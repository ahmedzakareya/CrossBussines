import 'package:flutter/material.dart';

import '../models/finance_summary.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';

/// FINANCE (the owner's reference): cash and banks on the financial card with the year's revenue, receivables and
/// payables in its footer; the year's result as a data list; the latest posted entries. Figures come from the
/// accounting summary of the signed-in user's company, in that company's currency.
class FinanceScreen extends StatefulWidget {
  const FinanceScreen({super.key});
  @override
  State<FinanceScreen> createState() => _FinanceScreenState();
}

class _FinanceScreenState extends State<FinanceScreen> {
  late Future<FinanceSummary> _future = ApiService.instance.getFinanceSummary();

  @override
  void initState() {
    super.initState();
    AppNav.dataChanged.addListener(_reload);
  }

  @override
  void dispose() {
    AppNav.dataChanged.removeListener(_reload);
    super.dispose();
  }

  void _reload() {
    if (mounted) setState(() => _future = ApiService.instance.getFinanceSummary());
  }

  @override
  Widget build(BuildContext context) {
    return ExecFrame(
      child: FutureBuilder<FinanceSummary>(
        future: _future,
        builder: (context, s) {
          final head = [
            if (Navigator.of(context).canPop()) BackLink(L(context, 'مساحات العمل', 'Workspaces')),
            PageHead(L(context, 'المالية', 'Finance'), eyebrow: L(context, 'السيولة والحسابات', 'Cash and accounts')),
          ];
          if (s.hasError) {
            return ExecBody(children: [...head, EmptyNote(icon: Lu.lock, title: L(context, 'تعذّر تحميل المالية', 'Could not load finance'), text: L(context, 'لا تملك عرض الحسابات، أو أن الاتصال انقطع.', 'You may not view the accounts, or the connection dropped.'))]);
          }
          if (!s.hasData) return const Center(child: CircularProgressIndicator());
          final f = s.data!;
          final cur = f.currency;
          final unit = curLabel(cur, isAr(context));
          String m(double v) => v < 0 ? '(${money(-v, cur)})' : money(v, cur);
          final profit = f.netResult >= 0;
          return ExecBody(
            onRefresh: () async => _reload(),
            children: [
              ...head,
              FinancialCard(
                label: L(context, 'النقدية والبنوك', 'Cash & banks'),
                icon: Lu.wallet,
                amount: Amount(m(f.cash), unit),
                note: f.asOf == null ? '' : '${L(context, 'حتى ', 'As of ')}${dayMonth(context, f.asOf!)} ${f.asOf!.year}',
                footer: [
                  (L(context, 'إيرادات السنة', 'Revenue (year)'), whole(f.revenue), ''),
                  (L(context, 'العملاء', 'Receivables'), whole(f.arTotal), ''),
                  (L(context, 'الموردون', 'Payables'), whole(f.apTotal), ''),
                ],
              ),
              SectionHeading(L(context, 'نتيجة السنة', "The year's result")),
              DataList(children: [
                KV(L(context, 'الإيرادات', 'Revenue'), '${m(f.revenue)} $unit'),
                KV(L(context, 'المصروفات', 'Expenses'), '${m(f.expense)} $unit'),
                KV(profit ? L(context, 'صافي الربح', 'Net profit') : L(context, 'صافي الخسارة', 'Net loss'), '${m(f.netResult)} $unit', total: true),
              ]),
              SectionHeading(L(context, 'الشركاء', 'Partners')),
              DataList(children: [
                KV(L(context, 'العملاء', 'Customers'), '${f.customers}'),
                KV(L(context, 'الموردون', 'Vendors'), '${f.vendors}'),
              ]),
              SectionHeading(L(context, 'آخر القيود', 'Latest entries'), count: f.recent.length),
              if (f.recent.isEmpty)
                EmptyNote(icon: Lu.bookOpen, title: L(context, 'لا قيود مرحّلة', 'No posted entries'), text: L(context, 'تظهر هنا آخر القيود المرحّلة في دفتر الأستاذ.', 'The latest posted ledger entries show here.'))
              else
                DataList(children: [
                  for (final e in f.recent)
                    ItemRow(
                      leading: const IconBox(icon: Lu.bookOpen),
                      title: (e.description ?? '').trim().isNotEmpty ? e.description!.trim() : (e.entryNo ?? '—'),
                      subtitle: [e.entryNo ?? '', if (e.entryDate != null) dayMonth(context, e.entryDate!)].where((x) => x.isNotEmpty).join(' · '),
                      end: Text(money(e.amount, cur), textDirection: TextDirection.ltr, style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: Brand.inkC)),
                    ),
                ]),
            ],
          );
        },
      ),
    );
  }
}
