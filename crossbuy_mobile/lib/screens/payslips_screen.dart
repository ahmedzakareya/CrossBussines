import 'package:flutter/material.dart';

import '../models/ess.dart';
import '../services/api_service.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';

/// MY PAYSLIPS (the owner's reference): the latest net pay on the financial card, earnings and deductions in its
/// footer, then every earlier month as a row. The PDF itself prints from Report Studio (Hr.Payslip) on the web.
class PayslipsScreen extends StatefulWidget {
  const PayslipsScreen({super.key});
  @override
  State<PayslipsScreen> createState() => _PayslipsScreenState();
}

class _PayslipsScreenState extends State<PayslipsScreen> {
  final _future = ApiService.instance.getPayslips();

  void _open(int id) => Navigator.of(context).push(MaterialPageRoute(builder: (_) => PayslipDetailScreen(id: id)));

  @override
  Widget build(BuildContext context) {
    return ExecFrame(
      child: FutureBuilder<(String, List<PayslipSummary>)>(
        future: _future,
        builder: (context, s) {
          final head = [BackLink(L(context, 'رجوع', 'Back')), PageHead(L(context, 'قسائمي', 'My payslips'), eyebrow: L(context, 'الرواتب الشهرية', 'Monthly pay'))];
          if (s.hasError) {
            return ExecBody(children: [...head, EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر التحميل', 'Could not load'), text: L(context, 'تحقق من الاتصال ثم أعد المحاولة.', 'Check the connection and try again.'))]);
          }
          if (!s.hasData) return const Center(child: CircularProgressIndicator());
          final (cur, list) = s.data!;
          final unit = curLabel(cur, isAr(context));
          if (list.isEmpty) {
            return ExecBody(children: [...head, EmptyNote(icon: Lu.receiptText, title: L(context, 'لا قسائم بعد', 'No payslips yet'), text: L(context, 'تظهر هنا بعد ترحيل رواتب الشهر.', "They appear here once the month's payroll is posted."))]);
          }
          final latest = list.first;
          return ExecBody(children: [
            ...head,
            GestureDetector(
              onTap: () => _open(latest.id),
              child: FinancialCard(
                label: '${L(context, 'صافي راتب ', 'Net pay · ')}${monthName(context, latest.month)} ${latest.year}',
                icon: Lu.receiptText,
                amount: Amount(money(latest.net, cur), unit),
                note: L(context, 'اضغط لعرض التفاصيل', 'Tap for the full payslip'),
              ),
            ),
            if (list.length > 1) ...[
              SectionHeading(L(context, 'الأشهر السابقة', 'Earlier months'), count: list.length - 1),
              DataList(children: [
                for (final p in list.skip(1))
                  ItemRow(
                    leading: const IconBox(icon: Lu.calendarDays),
                    title: '${monthName(context, p.month)} ${p.year}',
                    subtitle: L(context, 'صافي الراتب', 'Net pay'),
                    end: Row(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.baseline, textBaseline: TextBaseline.alphabetic, children: [
                      Text(money(p.net, cur), textDirection: TextDirection.ltr, style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w700, color: Brand.inkC)),
                      const SizedBox(width: 4),
                      Text(unit, style: T.small),
                    ]),
                    onTap: () => _open(p.id),
                  ),
              ]),
            ],
          ]);
        },
      ),
    );
  }
}

/// One payslip in full: the net on the card with earnings / deductions under it, then every line, reconciling to
/// the net.
class PayslipDetailScreen extends StatelessWidget {
  final int id;
  const PayslipDetailScreen({super.key, required this.id});

  @override
  Widget build(BuildContext context) {
    return ExecFrame(
      child: FutureBuilder<(String, PayslipDetail)>(
        future: ApiService.instance.getPayslip(id),
        builder: (context, s) {
          if (s.hasError) {
            return ExecBody(children: [
              BackLink(L(context, 'قسائمي', 'My payslips')),
              EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر تحميل القسيمة', 'Could not load the payslip'), text: L(context, 'تحقق من الاتصال ثم أعد المحاولة.', 'Check the connection and try again.')),
            ]);
          }
          if (!s.hasData) return const Center(child: CircularProgressIndicator());
          final (cur, p) = s.data!;
          final unit = curLabel(cur, isAr(context));
          String m(num v) => money(v, cur);
          String minus(num v) => v == 0 ? m(0) : '− ${m(v)}';
          return ExecBody(children: [
            BackLink(L(context, 'قسائمي', 'My payslips')),
            PageHead(L(context, 'قسيمة الراتب', 'Payslip'), eyebrow: '${monthName(context, p.month)} ${p.year} · ${p.slipNo}'),
            FinancialCard(
              label: L(context, 'صافي الراتب', 'Net pay'),
              icon: Lu.receiptText,
              amount: Amount(m(p.net), unit),
              footer: [
                (L(context, 'الاستحقاقات', 'Earnings'), m(p.earnings), ''),
                (L(context, 'الاستقطاعات', 'Deductions'), m(p.deductions), ''),
              ],
            ),
            SectionHeading(L(context, 'الاستحقاقات', 'Earnings')),
            DataList(children: [
              KV(L(context, 'الراتب الأساسي', 'Basic salary'), m(p.basic)),
              KV(L(context, 'البدلات', 'Allowances'), m(p.allowances)),
              KV(L(context, 'العمل الإضافي', 'Overtime'), m(p.overtime)),
            ]),
            SectionHeading(L(context, 'الاستقطاعات', 'Deductions')),
            DataList(children: [
              KV(L(context, 'التأمينات الاجتماعية', 'Social insurance'), minus(p.socialInsurance)),
              if (p.tax != 0) KV(L(context, 'ضريبة الدخل', 'Income tax'), minus(p.tax)),
              KV('${L(context, 'التأخير', 'Late')}${p.lateMinutes > 0 ? ' · ${p.lateMinutes} ${L(context, 'دقيقة', 'min')}' : ''}', minus(p.late)),
              KV('${L(context, 'الغياب', 'Absence')}${p.absentDays > 0 ? ' · ${p.absentDays} ${L(context, 'يوم', 'd')}' : ''}', minus(p.absence)),
              if (p.loan != 0) KV(L(context, 'قسط قرض / سلفة', 'Loan / advance'), minus(p.loan)),
              if (p.partPaidLeave != 0) KV(L(context, 'إجازة بأجر جزئي', 'Part-paid leave'), minus(p.partPaidLeave)),
              KV(L(context, 'الصافي', 'Net'), '${m(p.net)} $unit', total: true),
            ]),
            Padding(
              padding: const EdgeInsets.only(top: 12),
              child: Text('${L(context, 'حصة صاحب العمل في التأمينات: ', 'Employer social insurance: ')}${m(p.employerInsurance)} $unit', style: T.small),
            ),
          ]);
        },
      ),
    );
  }
}
