import 'package:flutter/material.dart';

import '../models/ess.dart';
import '../services/api_service.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';

/// MY ATTENDANCE — the month as a calendar coloured by day (present, late/absent, leave, rest), with the summary above.
class AttendanceScreen extends StatefulWidget {
  const AttendanceScreen({super.key});
  @override
  State<AttendanceScreen> createState() => _AttendanceScreenState();
}

class _AttendanceScreenState extends State<AttendanceScreen> {
  DateTime _month = DateTime(DateTime.now().year, DateTime.now().month);
  late Future<AttendanceMonth> _future = _load();

  Future<AttendanceMonth> _load() => ApiService.instance.getAttendance(_month.year, _month.month);

  void _shift(int by) => setState(() {
        _month = DateTime(_month.year, _month.month + by);
        _future = _load();
      });

  Color? _dot(String st) => switch (st) {
        'Present' => Brand.ok,
        'Late' || 'Absent' => Brand.bad,
        'Leave' => Brand.violet,
        _ => null,
      };

  @override
  Widget build(BuildContext context) {
    final ar = isAr(context);
    return PageFrame(
      title: L(context, 'حضوري', 'My attendance'),
      child: FutureBuilder<AttendanceMonth>(
        future: _future,
        builder: (context, s) {
          final head = Row(children: [
            IconButton(onPressed: () => _shift(-1), icon: Icon(ar ? Icons.chevron_right : Icons.chevron_left)),
            Expanded(child: Text('${monthName(context, _month.month)} ${_month.year}', textAlign: TextAlign.center, style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 16))),
            IconButton(onPressed: () => _shift(1), icon: Icon(ar ? Icons.chevron_left : Icons.chevron_right)),
          ]);
          if (s.hasError) return ListView(padding: const EdgeInsets.all(16), children: [head, Text(L(context, 'تعذّر تحميل الحضور', 'Could not load attendance'))]);
          if (!s.hasData) return const Center(child: CircularProgressIndicator());
          final a = s.data!;
          final byDay = {for (final d in a.days) d.date.day: d};
          final first = DateTime(a.year, a.month, 1);
          final lead = first.weekday % 7; // Sunday-first weeks, as in the Gulf
          final count = DateTime(a.year, a.month + 1, 0).day;
          final heads = ar ? ['أحد', 'إثن', 'ثلا', 'أرب', 'خمي', 'جمع', 'سبت'] : ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
          Widget kpi(String arT, String enT, String v, {bool bad = false}) => Expanded(
                child: Container(
                  margin: const EdgeInsets.all(4), padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(color: Colors.white, borderRadius: BorderRadius.circular(16)),
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text(L(context, arT, enT), style: const TextStyle(fontSize: 11.5, color: Brand.muted)),
                    Text(v, style: TextStyle(fontSize: 19, fontWeight: FontWeight.w700, color: bad ? Brand.bad : Brand.ink)),
                  ]),
                ),
              );
          return ListView(padding: const EdgeInsets.all(16), children: [
            head,
            Row(children: [kpi('حاضر', 'Present', '${a.presentDays}/${a.workDays}'), kpi('العمل الإضافي', 'Overtime', '${a.overtimeMinutes ~/ 60}:${(a.overtimeMinutes % 60).toString().padLeft(2, '0')}')]),
            Row(children: [kpi('مرات التأخير', 'Late', '${a.lateCount}', bad: a.lateCount > 0), kpi('الغياب', 'Absent', '${a.absentDays}', bad: a.absentDays > 0)]),
            const SizedBox(height: 10),
            SectionCard(
              child: GridView.count(
                crossAxisCount: 7, shrinkWrap: true, physics: const NeverScrollableScrollPhysics(), mainAxisSpacing: 6, crossAxisSpacing: 6, childAspectRatio: .95,
                children: [
                  for (final h in heads) Center(child: Text(h, style: const TextStyle(fontSize: 10.5, fontWeight: FontWeight.w700, color: Brand.muted))),
                  for (var i = 0; i < lead; i++) const SizedBox.shrink(),
                  for (var d = 1; d <= count; d++)
                    Container(
                      decoration: BoxDecoration(
                        // rest days and holidays are shaded; work days (done or upcoming) are white
                        color: const ['Rest', 'Holiday'].contains(byDay[d]?.status) ? const Color(0xFFEEF2F7) : Colors.white,
                        borderRadius: BorderRadius.circular(11),
                        border: Border.all(color: const Color(0xFFEEF2F7)),
                      ),
                      child: Column(mainAxisAlignment: MainAxisAlignment.center, children: [
                        Text('$d', style: const TextStyle(fontSize: 12.5)),
                        if (byDay[d] != null && _dot(byDay[d]!.status) != null)
                          Container(width: 6, height: 6, margin: const EdgeInsets.only(top: 2), decoration: BoxDecoration(color: _dot(byDay[d]!.status), shape: BoxShape.circle)),
                      ]),
                    ),
                ],
              ),
            ),
            Wrap(spacing: 8, runSpacing: 6, children: [
              Pill.ok(L(context, 'حاضر', 'Present')),
              Pill.bad(L(context, 'تأخير / غياب', 'Late / absent')),
              Pill.violet(L(context, 'إجازة', 'Leave')),
            ]),
          ]);
        },
      ),
    );
  }
}
