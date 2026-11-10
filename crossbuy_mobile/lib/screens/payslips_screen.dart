import 'package:flutter/material.dart';

import '../models/ess.dart';
import '../services/api_service.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';

/// MY PAYSLIPS — the list (amounts hidden until asked for) and one payslip in full, every earning and deduction, so the
/// lines reconcile to the net. The PDF itself prints from Report Studio (Hr.Payslip), never from the phone's HTML.
class PayslipsScreen extends StatefulWidget {
  const PayslipsScreen({super.key});
  @override
  State<PayslipsScreen> createState() => _PayslipsScreenState();
}

class _PayslipsScreenState extends State<PayslipsScreen> {
  final _future = ApiService.instance.getPayslips();
  bool _reveal = false;

  @override
  Widget build(BuildContext context) => PageFrame(
        title: L(context, 'قسائمي', 'My payslips'),
        actions: [IconButton(onPressed: () => setState(() => _reveal = !_reveal), icon: Icon(_reveal ? Icons.visibility_off_outlined : Icons.visibility_outlined))],
        child: FutureBuilder<(String, List<PayslipSummary>)>(
          future: _future,
          builder: (context, s) {
            if (s.hasError) return Center(child: Text(L(context, 'تعذّر تحميل القسائم', 'Could not load payslips')));
            if (!s.hasData) return const Center(child: CircularProgressIndicator());
            final (cur, list) = s.data!;
            if (list.isEmpty) return Padding(padding: const EdgeInsets.all(16), child: SectionCard(child: EmptyNote(icon: Icons.receipt_long_outlined, title: L(context, 'لا قسائم بعد', 'No payslips yet'), text: L(context, 'تظهر هنا بعد ترحيل رواتب الشهر.', "They appear here once the month's payroll is posted."))));
            return ListView(padding: const EdgeInsets.all(16), children: [
              SectionCard(
                padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
                child: Column(children: [
                  for (final p in list)
                    RowItem(
                      leading: const IconTile(Icons.account_balance_wallet_outlined),
                      title: '${monthName(context, p.month)} ${p.year}',
                      subtitle: '${L(context, 'الصافي ', 'Net ')}${_reveal ? '${money(p.net, cur)} $cur' : '••••••'}',
                      trailing: Icon(isAr(context) ? Icons.chevron_left : Icons.chevron_right, color: Brand.muted),
                      onTap: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => PayslipDetailScreen(id: p.id))),
                    ),
                ]),
              ),
            ]);
          },
        ),
      );
}

class PayslipDetailScreen extends StatelessWidget {
  final int id;
  const PayslipDetailScreen({super.key, required this.id});

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<(String, PayslipDetail)>(
      future: ApiService.instance.getPayslip(id),
      builder: (context, s) {
        if (s.hasError) return PageFrame(title: L(context, 'القسيمة', 'Payslip'), child: Center(child: Text(L(context, 'تعذّر تحميل القسيمة', 'Could not load the payslip'))));
        if (!s.hasData) return PageFrame(title: L(context, 'القسيمة', 'Payslip'), child: const Center(child: CircularProgressIndicator()));
        final (cur, p) = s.data!;
        String m(num v) => '${money(v, cur)} $cur';
        Widget kv(String ar, String en, num v, {String? note}) => Padding(
              padding: const EdgeInsets.symmetric(vertical: 8),
              child: Row(children: [
                Expanded(child: Text('${L(context, ar, en)}${note == null ? '' : ' ($note)'}', style: const TextStyle(color: Brand.muted))),
                Text(m(v), style: const TextStyle(fontWeight: FontWeight.w700)),
              ]),
            );
        return PageFrame(
          title: '${L(context, 'قسيمة ', 'Payslip · ')}${monthName(context, p.month)} ${p.year}',
          child: ListView(padding: const EdgeInsets.all(16), children: [
            SectionCard(
              child: Column(children: [
                Text(L(context, 'صافي الراتب', 'Net pay'), style: const TextStyle(color: Brand.muted)),
                const SizedBox(height: 4),
                Text(m(p.net), style: const TextStyle(fontSize: 30, fontWeight: FontWeight.w800, color: Brand.deep)),
                Text(p.slipNo, style: const TextStyle(fontSize: 12, color: Brand.muted)),
              ]),
            ),
            SectionCard(
              title: L(context, 'الاستحقاقات', 'Earnings'),
              child: Column(children: [
                kv('الأساسي', 'Basic', p.basic),
                kv('البدلات', 'Allowances', p.allowances),
                kv('العمل الإضافي', 'Overtime', p.overtime),
                const Divider(),
                kv('الإجمالي', 'Total', p.earnings),
              ]),
            ),
            SectionCard(
              title: L(context, 'الاستقطاعات', 'Deductions'),
              child: Column(children: [
                kv('التأمينات الاجتماعية', 'Social insurance', p.socialInsurance),
                if (p.tax != 0) kv('ضريبة الدخل', 'Income tax', p.tax),
                kv('التأخير', 'Late', p.late, note: p.lateMinutes > 0 ? '${p.lateMinutes} ${L(context, 'د', 'min')}' : null),
                kv('الغياب', 'Absence', p.absence, note: p.absentDays > 0 ? '${p.absentDays} ${L(context, 'يوم', 'd')}' : null),
                if (p.loan != 0) kv('قسط قرض / سلفة', 'Loan / advance', p.loan),
                if (p.partPaidLeave != 0) kv('إجازة بأجر جزئي', 'Part-paid leave', p.partPaidLeave),
                const Divider(),
                kv('الإجمالي', 'Total', p.deductions),
              ]),
            ),
            Text('${L(context, 'حصة صاحب العمل في التأمينات: ', 'Employer social insurance: ')}${m(p.employerInsurance)}', style: const TextStyle(fontSize: 12, color: Brand.muted)),
          ]),
        );
      },
    );
  }
}
