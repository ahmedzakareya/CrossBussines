import 'package:flutter/material.dart';
import 'package:geolocator/geolocator.dart';

import '../models/ess.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';

/// MY ATTENDANCE (the owner's reference): the month's working hours on the financial card with today's punch under the
/// amount and attendance, lateness and overtime in its footer; then the month as a calendar — each day marked by the
/// verdict the server's own summary reached (present, late / absent, leave), rest days and holidays shaded — and
/// "punch now": the phone's position goes to the server, which accepts it only inside the branch office's radius.
class AttendanceScreen extends StatefulWidget {
  const AttendanceScreen({super.key});
  @override
  State<AttendanceScreen> createState() => _AttendanceScreenState();
}

class _AttendanceScreenState extends State<AttendanceScreen> {
  DateTime _month = DateTime(DateTime.now().year, DateTime.now().month);
  late Future<AttendanceMonth> _future = _load();
  bool _punching = false;

  Future<AttendanceMonth> _load() => ApiService.instance.getAttendance(_month.year, _month.month);

  void _shift(int by) => setState(() {
        _month = DateTime(_month.year, _month.month + by);
        _future = _load();
      });

  Color? _dot(String st) => switch (st) {
        'Present' => Brand.support,
        'Late' || 'Absent' => Brand.badC,
        'Leave' => Brand.gold,
        _ => null,
      };

  void _say(String m) => ScaffoldMessenger.of(context)..hideCurrentSnackBar()..showSnackBar(SnackBar(content: Text(m)));

  /// Read the position (asking for permission the first time) and let the server decide.
  Future<void> _punch() async {
    if (_punching) return;
    setState(() => _punching = true);
    try {
      final enabled = await Geolocator.isLocationServiceEnabled();
      if (!mounted) return;
      if (!enabled) return _say(L(context, 'شغّل خدمة الموقع في الجوال ثم أعد المحاولة.', 'Turn on location services, then try again.'));
      var perm = await Geolocator.checkPermission();
      if (perm == LocationPermission.denied) perm = await Geolocator.requestPermission();
      if (!mounted) return;
      if (perm == LocationPermission.denied || perm == LocationPermission.deniedForever) {
        return _say(L(context, 'البصمة من الجوال تحتاج إذن الموقع.', 'Punching from the phone needs the location permission.'));
      }
      final pos = await Geolocator.getCurrentPosition(locationSettings: const LocationSettings(accuracy: LocationAccuracy.high, timeLimit: Duration(seconds: 20)));
      final (ok, kind) = await ApiService.instance.punch(pos.latitude, pos.longitude, pos.accuracy);
      if (!mounted) return;
      if (!ok) return _say(kind ?? L(context, 'تعذّرت البصمة', 'The punch failed'));
      _say(kind == 'out' ? L(context, 'سُجّل انصرافك', 'Check-out recorded') : L(context, 'سُجّل حضورك', 'Check-in recorded'));
      AppNav.notifyDataChanged();
      setState(() => _future = _load());
    } catch (_) {
      if (mounted) _say(L(context, 'تعذّر تحديد موقعك. حاول في مكان مكشوف.', 'Could not get your position. Try somewhere open.'));
    } finally {
      if (mounted) setState(() => _punching = false);
    }
  }

  String _hm(DateTime t) => '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';

  @override
  Widget build(BuildContext context) {
    final ar = isAr(context);
    final monthBar = Row(children: [
      GestureDetector(onTap: () => _shift(-1), child: Padding(padding: const EdgeInsets.all(6), child: Icon(ar ? Lu.chevronRight : Lu.chevronLeft, size: 18, color: Brand.support))),
      Expanded(child: Text('${monthName(context, _month.month)} ${_month.year}', textAlign: TextAlign.center, style: T.h2)),
      GestureDetector(onTap: () => _shift(1), child: Padding(padding: const EdgeInsets.all(6), child: Icon(ar ? Lu.chevronLeft : Lu.chevronRight, size: 18, color: Brand.support))),
    ]);
    return ExecFrame(
      child: FutureBuilder<AttendanceMonth>(
        future: _future,
        builder: (context, s) {
          final head = [BackLink(L(context, 'حسابي', 'Account')), PageHead(L(context, 'حضوري', 'My attendance'), eyebrow: '${monthName(context, _month.month)} ${_month.year}')];
          if (s.hasError) {
            return ExecBody(children: [...head, monthBar, EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر تحميل الحضور', 'Could not load attendance'), text: L(context, 'تحقق من الاتصال ثم أعد المحاولة.', 'Check the connection and try again.'))]);
          }
          if (!s.hasData) return const Center(child: CircularProgressIndicator());
          final a = s.data!;
          final byDay = {for (final d in a.days) d.date.day: d.status};
          final lead = DateTime(a.year, a.month, 1).weekday % 7;   // weeks start on Sunday, as in the Gulf
          final count = DateTime(a.year, a.month + 1, 0).day;
          final heads = ar ? ['أحد', 'إثن', 'ثلا', 'أرب', 'خمي', 'جمع', 'سبت'] : ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
          final today = DateTime.now();
          // the month's working hours, as the reference's card shows them: 94:30
          final worked = a.days.fold<double>(0, (t, d) => t + d.workedHours.toDouble());
          final wh = worked.floor(), wm = ((worked - wh) * 60).round();
          final ot = '${a.overtimeMinutes ~/ 60}:${(a.overtimeMinutes % 60).toString().padLeft(2, '0')}';
          final t = a.days.where((d) => d.date.year == today.year && d.date.month == today.month && d.date.day == today.day).firstOrNull;
          final todayNote = t?.checkIn == null
              ? L(context, 'اليوم: لم تُسجَّل بصمة بعد', 'Today: no punch yet')
              : [
                  L(context, 'اليوم: دخول ${_hm(t!.checkIn!)}', 'Today: in ${_hm(t.checkIn!)}'),
                  if (t.checkOut != null) L(context, 'خروج ${_hm(t.checkOut!)}', 'out ${_hm(t.checkOut!)}'),
                  if (t.source == 'Mobile') L(context, 'داخل نطاق المكتب', 'inside the office range'),
                ].join(' · ');
          Widget legend(Color c, String txt) => Row(mainAxisSize: MainAxisSize.min, children: [
                Container(width: 6, height: 6, decoration: BoxDecoration(color: c, shape: BoxShape.circle)),
                const SizedBox(width: 5),
                Text(txt, style: const TextStyle(fontSize: 9.5, color: Brand.mutedC)),
              ]);
          return ExecBody(
            onRefresh: () async => setState(() => _future = _load()),
            children: [
              ...head,
              FinancialCard(
                label: L(context, 'ساعات العمل هذا الشهر', 'Hours worked this month'),
                icon: Lu.clock3,
                amount: Amount('$wh:${wm.toString().padLeft(2, '0')}',L(context, 'ساعة', 'hours')),
                note: todayNote,
                footer: [
                  (L(context, 'حضور', 'Present'), '${a.presentDays}', L(context, 'أيام', 'days')),
                  (L(context, 'تأخير', 'Late'), '${a.lateCount}', L(context, 'مرة', 'times')),
                  (L(context, 'إضافي', 'Overtime'), ot, ''),
                ],
              ),
              SectionHeading(L(context, 'الشهر', 'The month'), action: _punching ? L(context, 'جارٍ…', 'Working…') : L(context, 'بصمة الآن', 'Punch now'), actionIcon: Lu.fingerprint, onAction: _punching ? null : _punch),
              monthBar,
              const SizedBox(height: 10),
              Container(
                padding: const EdgeInsets.all(14),
                decoration: BoxDecoration(color: Brand.paper, border: Border.all(color: Brand.lineC), borderRadius: BorderRadius.circular(18)),
                child: GridView.count(
                  crossAxisCount: 7, shrinkWrap: true, physics: const NeverScrollableScrollPhysics(), padding: EdgeInsets.zero,
                  mainAxisSpacing: 6, crossAxisSpacing: 6, childAspectRatio: .95,
                  children: [
                    for (final h in heads) Center(child: Text(h, style: const TextStyle(fontSize: 9.5, color: Brand.mutedC))),
                    for (var i = 0; i < lead; i++) const SizedBox.shrink(),
                    for (var d = 1; d <= count; d++)
                      () {
                        final st = byDay[d] ?? '';
                        final off = st == 'Rest' || st == 'Holiday' || st == 'RestDay';
                        final isToday = a.year == today.year && a.month == today.month && d == today.day;
                        final dot = _dot(st);
                        return Container(
                          decoration: BoxDecoration(
                            color: off ? Colors.transparent : Brand.page,
                            borderRadius: BorderRadius.circular(9),
                            border: isToday ? Border.all(color: Brand.blue, width: 1.5) : null,
                          ),
                          child: Stack(alignment: Alignment.center, children: [
                            Text('$d', style: TextStyle(fontSize: 11, color: off ? const Color(0xFFB3BCCB) : Brand.inkC)),
                            if (dot != null) Positioned(bottom: 4, child: Container(width: 4, height: 4, decoration: BoxDecoration(color: dot, shape: BoxShape.circle))),
                          ]),
                        );
                      }(),
                  ],
                ),
              ),
              const SizedBox(height: 10),
              Wrap(alignment: WrapAlignment.center, spacing: 14, children: [
                legend(Brand.support, L(context, 'حضور', 'Present')),
                legend(Brand.badC, L(context, 'تأخير / غياب', 'Late / absent')),
                legend(Brand.gold, L(context, 'إجازة', 'Leave')),
              ]),
            ],
          );
        },
      ),
    );
  }
}
