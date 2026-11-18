import 'package:flutter/material.dart';

import '../models/finance_summary.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';

/// FINANCE (the owner's reference): cash and banks on the financial card — this month's movement under the amount —
/// with the year's revenue, receivables and payables in its footer; then the accounts, each bank and cash account with
/// its posted balance. From the accounting summary of the signed-in user's company, in that company's currency.
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
          final ch = f.cashMonthChange;
          return ExecBody(
            onRefresh: () async => _reload(),
            children: [
              ...head,
              FinancialCard(
                label: L(context, 'النقدية والبنوك', 'Cash & banks'),
                icon: Lu.wallet,
                amount: Amount(m(f.cash), unit),
                note: ch == 0 ? L(context, 'لا حركة هذا الشهر', 'No movement this month') : '\u2066${ch > 0 ? '+' : '−'}${money(ch.abs(), cur)}\u2069 ${L(context, 'هذا الشهر', 'this month')}',
                footer: [
                  (L(context, 'إيرادات السنة', 'Revenue (year)'), whole(f.revenue), ''),
                  (L(context, 'العملاء', 'Customers'), whole(f.arTotal), ''),
                  (L(context, 'الموردون', 'Vendors'), whole(f.apTotal), ''),
                ],
              ),
              SectionHeading(L(context, 'الحسابات', 'Accounts')),
              if (f.accounts.isEmpty)
                EmptyNote(icon: Lu.landmark, title: L(context, 'لا حسابات نقدية', 'No cash accounts'), text: L(context, 'تظهر هنا حسابات البنوك والنقدية بأرصدتها.', 'Bank and cash accounts show here with their balances.'))
              else
                DataList(children: [
                  for (final a in f.accounts)
                    ItemRow(
                      leading: IconBox(icon: a.bank ? Lu.landmark : Lu.banknote),
                      title: a.name,
                      subtitle: a.bank ? (a.accountNo.isEmpty ? L(context, 'حساب بنكي', 'Bank account') : '${L(context, 'حساب جاري', 'Current account')} ···· ${a.accountNo}') : L(context, 'نقدية', 'Cash'),
                      end: Text(m(a.balance), textDirection: TextDirection.ltr, style: TextStyle(fontSize: 12.5, fontWeight: FontWeight.w700, color: a.balance < 0 ? Brand.badC : Brand.inkC)),
                    ),
                ]),
            ],
          );
        },
      ),
    );
  }
}
