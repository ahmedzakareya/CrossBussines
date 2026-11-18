import 'package:flutter/material.dart';

import '../models/dashboard.dart';
import '../models/leave.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';

/// Arabic counts agree with the number: يوم واحد · يومان · 3 أيام · 11 يوماً.
String dayCount(BuildContext context, int n) {
  if (!isAr(context)) return n == 1 ? '1 day' : '$n days';
  if (n == 1) return 'يوم واحد';
  if (n == 2) return 'يومان';
  if (n >= 3 && n <= 10) return '$n أيام';
  return '$n يوماً';
}

String _dayUnit(BuildContext context, int n) => !isAr(context) ? (n == 1 ? 'day' : 'days') : (n >= 3 && n <= 10 ? 'أيام' : 'يوماً');

String _pick(BuildContext context, String? ar, String? en) {
  final a = ar?.trim() ?? '', e = en?.trim() ?? '';
  return isAr(context) ? (a.isNotEmpty ? a : e) : (e.isNotEmpty ? e : a);
}

String _range(BuildContext context, LeaveRequest r) {
  if (r.startDate == null) return '';
  final s = r.startDate!, e = r.endDate ?? s;
  if (s.year == e.year && s.month == e.month && s.day == e.day) return dayMonth(context, s);
  if (s.year == e.year && s.month == e.month) return '${s.day} – ${dayMonth(context, e)}';
  return '${dayMonth(context, s)} – ${dayMonth(context, e)}';
}

/// Who decided a request, or whose turn it is — the person, never a bare status.
String _who(BuildContext context, LeaveRequest r) {
  final steps = [...r.approvals]..sort((a, b) => a.level.compareTo(b.level));
  ApprovalStep? s;
  if (r.status == 2) {
    s = steps.where((x) => x.status == 2).firstOrNull;
  } else if (r.status == 1) {
    s = steps.where((x) => x.status == 1).lastOrNull;
  } else {
    s = steps.where((x) => x.status == 0).firstOrNull;
  }
  final name = s == null ? '' : _pick(context, s.approverAr, s.approverEn);
  if (name.isEmpty) return '';
  return switch (r.status) {
    1 => L(context, 'اعتمدها $name', 'Approved by $name'),
    2 => L(context, 'رفضها $name', 'Rejected by $name'),
    _ => L(context, 'بانتظار $name', 'Waiting for $name'),
  };
}

IconData _icon(int status) => switch (status) { 1 => Lu.calendarCheck, 2 => Lu.calendarX, _ => Lu.calendarClock };

/// MY LEAVE (the owner's reference): the main balance on the financial card with the other types in its footer, then
/// my requests as request cards. Balances come from the same dashboard service the web's People page uses; requests
/// for my approval live in the inbox, not here.
class LeaveScreen extends StatefulWidget {
  const LeaveScreen({super.key});
  @override
  State<LeaveScreen> createState() => _LeaveScreenState();
}

class _LeaveScreenState extends State<LeaveScreen> {
  late Future<(DashboardData, List<LeaveRequest>)> _future = _load();

  Future<(DashboardData, List<LeaveRequest>)> _load() async {
    final r = await Future.wait([ApiService.instance.getDashboard(), ApiService.instance.getMyLeaves()]);
    return (r[0] as DashboardData, r[1] as List<LeaveRequest>);
  }

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
    if (mounted) setState(() => _future = _load());
  }

  Future<void> _new() async {
    await Navigator.of(context).push(MaterialPageRoute(builder: (_) => const NewLeaveScreen()));
    _reload();
  }

  @override
  Widget build(BuildContext context) {
    return ExecFrame(
      child: FutureBuilder<(DashboardData, List<LeaveRequest>)>(
        future: _future,
        builder: (context, s) {
          final head = [
            if (Navigator.of(context).canPop()) BackLink(L(context, 'رجوع', 'Back')),
            PageHead(L(context, 'الإجازات', 'Leave'), eyebrow: L(context, 'الأرصدة والطلبات', 'Balances and requests')),
          ];
          if (s.hasError) {
            return ExecBody(children: [...head, EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر تحميل الإجازات', 'Could not load leave'), text: L(context, 'تحقق من الاتصال ثم أعد المحاولة.', 'Check the connection and try again.'))]);
          }
          if (!s.hasData) return const Center(child: CircularProgressIndicator());
          final (d, list) = s.data!;
          final b = d.balances;
          return ExecBody(
            onRefresh: () async => _reload(),
            children: [
              ...head,
              // No leave type is marked "the annual one", so no single type is promoted: the card holds the days taken
              // this year across all types, and every type's balance follows as its own line.
              FinancialCard(
                label: L(context, 'أيام الإجازة المأخوذة هذا العام', 'Leave days taken this year'),
                icon: Lu.calendarDays,
                amount: Amount('${b.fold<int>(0, (s, x) => s + x.used)}', _dayUnit(context, b.fold<int>(0, (s, x) => s + x.used))),
                note: d.myPending == 0 ? L(context, 'لا طلبات معلّقة', 'No pending requests') : L(context, '${d.myPending} طلب قيد الاعتماد', '${d.myPending} request(s) awaiting approval'),
              ),
              SectionHeading(L(context, 'أرصدتي', 'My balances')),
              if (b.isEmpty)
                EmptyNote(icon: Lu.calendarDays, title: L(context, 'لا سياسة إجازات مرتبطة بك', 'No leave policy is assigned to you'), text: L(context, 'تربطك الموارد البشرية بسياسة إجازات، فتظهر أرصدتك هنا.', 'HR assigns you a leave policy; your balances then show here.'))
              else
                DataList(children: [
                  for (final x in b)
                    ItemRow(
                      leading: const IconBox(icon: Lu.calendarDays),
                      title: _pick(context, x.nameAr, x.nameEn),
                      subtitle: L(context, 'من ${x.entitlement} · استُخدم ${x.used}', 'of ${x.entitlement} · ${x.used} used'),
                      end: Row(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.baseline, textBaseline: TextBaseline.alphabetic, children: [
                        Text('${x.remaining}', style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w700, color: Brand.inkC)),
                        const SizedBox(width: 4),
                        Text(_dayUnit(context, x.remaining), style: T.small),
                      ]),
                    ),
                ]),
              SectionHeading(L(context, 'طلباتي', 'My requests'), count: list.length, action: L(context, 'طلب جديد', 'New request'), actionIcon: Lu.plus, onAction: _new),
              if (list.isEmpty)
                EmptyNote(icon: Lu.calendarPlus, title: L(context, 'لا طلبات إجازة', 'No leave requests'), text: L(context, 'اطلب إجازة من «طلب جديد» أعلاه.', 'Ask for leave with “New request” above.')),
              for (final r in list)
                RequestCard(
                  leading: IconBox(icon: _icon(r.status)),
                  title: '${_pick(context, r.leaveTypeAr, r.leaveTypeEn)} · ${_range(context, r)}',
                  subtitle: [dayCount(context, r.days), _who(context, r)].where((x) => x.isNotEmpty).join(' · '),
                  tag: _pill(context, r.status),
                  onTap: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => LeaveDetailScreen(request: r))),
                ),
            ],
          );
        },
      ),
    );
  }
}

Widget _pill(BuildContext context, int status) => status == 1
    ? Tag.ok(L(context, 'معتمد', 'Approved'))
    : status == 2
        ? Tag.bad(L(context, 'مرفوض', 'Rejected'))
        : Tag(L(context, 'قيد الاعتماد', 'Pending'));

/// One request: its facts, then the approval chain step by step with the person at each step.
class LeaveDetailScreen extends StatelessWidget {
  final LeaveRequest request;
  const LeaveDetailScreen({super.key, required this.request});

  @override
  Widget build(BuildContext context) {
    final r = request;
    final steps = [...r.approvals]..sort((a, b) => a.level.compareTo(b.level));
    return ExecFrame(
      child: ExecBody(children: [
        BackLink(L(context, 'الإجازات', 'Leave')),
        PageHead(_pick(context, r.leaveTypeAr, r.leaveTypeEn), eyebrow: _range(context, r)),
        Align(alignment: AlignmentDirectional.centerStart, child: _pill(context, r.status)),
        const SizedBox(height: 12),
        DataList(children: [
          KV(L(context, 'من', 'From'), r.startDate == null ? '—' : dayMonth(context, r.startDate!)),
          KV(L(context, 'إلى', 'To'), r.endDate == null ? '—' : dayMonth(context, r.endDate!)),
          KV(L(context, 'أيام العمل', 'Working days'), dayCount(context, r.days)),
          if ((r.reason ?? '').trim().isNotEmpty) KV(L(context, 'السبب', 'Reason'), r.reason!.trim()),
          if (r.createdAt != null) KV(L(context, 'قُدّم في', 'Submitted'), dayMonth(context, r.createdAt!)),
          if ((r.decisionNote ?? '').trim().isNotEmpty) KV(L(context, 'ملاحظة القرار', 'Decision note'), r.decisionNote!.trim()),
        ]),
        if (steps.isNotEmpty) ...[
          SectionHeading(L(context, 'مسار الاعتماد', 'Approval path')),
          DataList(children: [
            for (final st in steps)
              ItemRow(
                leading: IconBox(icon: st.status == 1 ? Lu.circleCheck : st.status == 2 ? Lu.circleX : Lu.clock3, fg: st.status == 2 ? Brand.badC : Brand.support),
                title: _pick(context, st.approverAr, st.approverEn).isEmpty ? L(context, 'المستوى ${st.level}', 'Level ${st.level}') : _pick(context, st.approverAr, st.approverEn),
                subtitle: L(context, 'المستوى ${st.level}', 'Level ${st.level}'),
                end: _pill(context, st.status),
              ),
          ]),
        ],
      ]),
    );
  }
}

/// NEW LEAVE (the owner's reference): the type as chips, from/to, a note, and what the request will cost before it is
/// sent. Days that are not working days under the employee's attendance policy cannot be picked; the server counts the
/// final days (public holidays excluded) and routes the request through the company's approval path.
class NewLeaveScreen extends StatefulWidget {
  const NewLeaveScreen({super.key});
  @override
  State<NewLeaveScreen> createState() => _NewLeaveScreenState();
}

class _NewLeaveScreenState extends State<NewLeaveScreen> {
  List<LeaveType> _types = [];
  List<LeaveBalance> _balances = [];
  List<bool>? _workDays;   // indexed Sunday=0 … Saturday=6; null = any day
  bool _loading = true;
  String? _loadError;
  int? _typeId;
  DateTime? _from, _to;
  final _reason = TextEditingController();
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    _init();
  }

  Future<void> _init() async {
    try {
      final r = await Future.wait([
        ApiService.instance.getLeaveTypes(),
        ApiService.instance.getDashboard().then((d) => d.balances).catchError((_) => <LeaveBalance>[]),
        ApiService.instance.getWorkDays(),
      ]);
      if (!mounted) return;
      setState(() {
        _types = r[0] as List<LeaveType>;
        _balances = r[1] as List<LeaveBalance>;
        _workDays = r[2] as List<bool>?;
        // the type with a balance comes first, the largest entitlement leading — the annual leave in practice
        final order = {for (var i = 0; i < _balances.length; i++) _balances[i].leaveTypeId: i};
        _types.sort((a, b) => (order[a.id] ?? 999).compareTo(order[b.id] ?? 999));
        _typeId = _types.isEmpty ? null : _types.first.id;
        _loading = false;
      });
    } catch (_) {
      if (mounted) setState(() { _loading = false; _loadError = 'x'; });
    }
  }

  @override
  void dispose() {
    _reason.dispose();
    super.dispose();
  }

  bool _isWorkDay(DateTime d) => _workDays == null || _workDays![d.weekday % 7];

  int _working() {
    if (_from == null || _to == null || _to!.isBefore(_from!)) return 0;
    var n = 0;
    for (var d = _from!; !d.isAfter(_to!); d = d.add(const Duration(days: 1))) {
      if (_isWorkDay(d)) n++;
    }
    return n;
  }

  LeaveBalance? get _balance => _balances.where((b) => b.leaveTypeId == _typeId).firstOrNull;

  Future<void> _pickDate(bool isFrom) async {
    final now = DateTime.now();
    var initial = isFrom ? (_from ?? now) : (_to ?? _from ?? now);
    for (var g = 0; g < 14 && !_isWorkDay(initial); g++) {
      initial = initial.add(const Duration(days: 1));
    }
    final picked = await showDatePicker(
      context: context,
      initialDate: initial,
      firstDate: DateTime(now.year - 1),
      lastDate: DateTime(now.year + 2),
      selectableDayPredicate: _isWorkDay,
    );
    if (picked == null) return;
    setState(() {
      if (isFrom) {
        _from = picked;
        if (_to == null || _to!.isBefore(picked)) _to = picked;
      } else {
        _to = picked;
        if (_from == null || _from!.isAfter(picked)) _from = picked;
      }
    });
  }

  Future<void> _send() async {
    if (_typeId == null || _from == null || _to == null) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(L(context, 'اختر النوع والتاريخين أولاً', 'Choose the type and both dates first'))));
      return;
    }
    setState(() => _busy = true);
    final (ok, err) = await ApiService.instance.createLeave(
      leaveTypeId: _typeId!,
      startDate: _from!,
      endDate: _to!,
      reason: _reason.text.trim().isEmpty ? null : _reason.text.trim(),
    );
    if (!mounted) return;
    setState(() => _busy = false);
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(ok ? L(context, 'أُرسل طلب الإجازة', 'Leave request sent') : (err ?? L(context, 'تعذّر إرسال الطلب', 'The request could not be sent')))));
    if (ok) Navigator.of(context).pop(true);
  }

  @override
  Widget build(BuildContext context) {
    final head = [
      BackLink(L(context, 'الإجازات', 'Leave')),
      PageHead(L(context, 'طلب إجازة', 'Request leave'),
          eyebrow: _balance == null ? L(context, 'يُرسل عبر مسار الاعتماد في شركتك', "Sent through your company's approval path") : '${L(context, 'رصيدك من هذا النوع', 'Your balance of this type')} ${dayCount(context, _balance!.remaining)}'),
    ];
    if (_loading) return const ExecFrame(child: Center(child: CircularProgressIndicator()));
    if (_loadError != null || _types.isEmpty) {
      return ExecFrame(
        child: ExecBody(children: [
          ...head,
          EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر تحميل أنواع الإجازات', 'Could not load leave types'), text: L(context, 'تحقق من الاتصال ثم أعد المحاولة.', 'Check the connection and try again.')),
        ]),
      );
    }
    final n = _working();
    final bal = _balance;
    return ExecFrame(
      child: ExecBody(children: [
        ...head,
        ChipTabs(
          items: [for (final t in _types) _pick(context, t.nameAr, t.nameEn)],
          index: _types.indexWhere((t) => t.id == _typeId),
          onTap: (i) => setState(() => _typeId = _types[i].id),
        ),
        Row(children: [
          Expanded(
            child: FieldBox(
              label: L(context, 'من', 'From'),
              onTap: () => _pickDate(true),
              child: Row(children: [Expanded(child: Text(_from == null ? L(context, 'اختر', 'Choose') : dayMonth(context, _from!), style: _from == null ? T.small : null)), const Icon(Lu.calendar, size: 15, color: Brand.support)]),
            ),
          ),
          const SizedBox(width: 9),
          Expanded(
            child: FieldBox(
              label: L(context, 'إلى', 'To'),
              onTap: () => _pickDate(false),
              child: Row(children: [Expanded(child: Text(_to == null ? L(context, 'اختر', 'Choose') : dayMonth(context, _to!), style: _to == null ? T.small : null)), const Icon(Lu.calendar, size: 15, color: Brand.support)]),
            ),
          ),
        ]),
        TextField(controller: _reason, maxLines: 2, decoration: InputDecoration(labelText: L(context, 'ملاحظة', 'Note'))),
        const SizedBox(height: 12),
        if (n > 0)
          DataList(children: [
            KV(L(context, 'أيام العمل المحسوبة', 'Working days counted'), dayCount(context, n)),
            if (bal != null) KV(L(context, 'الرصيد بعد الطلب', 'Balance after the request'), dayCount(context, bal.remaining - n), total: true),
          ]),
        if (n > 0 && bal != null && bal.remaining - n < 0) ...[
          const SizedBox(height: 9),
          SoftNote(L(context, 'الطلب يتجاوز رصيدك؛ قد يرفضه النظام أو يحتاج موافقة خاصة.', 'The request exceeds your balance; the system may refuse it or it may need special approval.')),
        ],
        if (n > 0) ...[
          const SizedBox(height: 9),
          SoftNote(L(context, 'تُستبعد أيام الراحة من العدّ، ويستبعد الخادم العطل الرسمية عند الحفظ.', 'Rest days are left out of the count; the server also leaves out public holidays when it saves.')),
        ],
        const SizedBox(height: 16),
        FilledButton(onPressed: _busy ? null : _send, child: Text(L(context, 'إرسال الطلب', 'Send request'))),
      ]),
    );
  }
}
