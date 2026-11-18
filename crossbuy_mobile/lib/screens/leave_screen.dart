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
  final s = switch (r.status) {
    2 => steps.where((x) => x.status == 2).firstOrNull,
    1 => steps.where((x) => x.status == 1).lastOrNull,
    _ => steps.where((x) => x.status == 0).firstOrNull,
  };
  final name = s == null ? '' : _pick(context, s.approverAr, s.approverEn);
  if (name.isEmpty) return '';
  return switch (r.status) {
    1 => L(context, 'اعتمدها $name', 'Approved by $name'),
    2 => L(context, 'رفضها $name', 'Rejected by $name'),
    _ => L(context, 'بانتظار $name', 'Waiting for $name'),
  };
}

IconData _icon(int status) => switch (status) { 1 => Lu.calendarCheck, 2 => Lu.calendarX, _ => Lu.calendarClock };

Widget _pill(BuildContext context, int status) => status == 1
    ? Tag.ok(L(context, 'معتمد', 'Approved'))
    : status == 2
        ? Tag.bad(L(context, 'مرفوض', 'Rejected'))
        : Tag(L(context, 'قيد الاعتماد', 'Pending'));

/// The balances in the reference's order: the annual leave first (the country rule pack says which type it is), then
/// sick and casual leave, then the rest as the server listed them.
List<LeaveBalance> _ordered(List<LeaveBalance> b) {
  int rank(LeaveBalance x) => switch (x.code) { 'annual' => 0, 'sick' => 1, 'casual' => 2, _ => 3 };
  final idx = {for (var i = 0; i < b.length; i++) b[i]: i};
  return [...b]..sort((x, y) => rank(x) != rank(y) ? rank(x) - rank(y) : idx[x]! - idx[y]!);
}

/// MY LEAVE (the owner's reference): the annual balance on the financial card — of how many days, and when it renews
/// — with sick and casual leave in its footer; then my requests as one list, each with its type and dates, the days
/// and the person who decided it, and its status.
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
            if (Navigator.of(context).canPop()) BackLink(L(context, 'مساحات العمل', 'Workspaces')),
            PageHead(L(context, 'الإجازات', 'Leave'), eyebrow: L(context, 'الأرصدة والطلبات', 'Balances and requests')),
          ];
          if (s.hasError) {
            return ExecBody(children: [...head, EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر تحميل الإجازات', 'Could not load leave'), text: L(context, 'تحقق من الاتصال ثم أعد المحاولة.', 'Check the connection and try again.'))]);
          }
          if (!s.hasData) return const Center(child: CircularProgressIndicator());
          final (d, list) = s.data!;
          final b = _ordered(d.balances);
          final main = b.isEmpty ? null : b.first;
          return ExecBody(
            onRefresh: () async => _reload(),
            children: [
              ...head,
              if (main == null)
                EmptyNote(icon: Lu.calendarDays, title: L(context, 'لا سياسة إجازات مرتبطة بك', 'No leave policy is assigned to you'), text: L(context, 'تربطك الموارد البشرية بسياسة إجازات، فتظهر أرصدتك هنا.', 'HR assigns you a leave policy; your balances then show here.'))
              else
                FinancialCard(
                  label: main.code == 'annual' ? L(context, 'رصيد الإجازة السنوية', 'Annual leave balance') : '${L(context, 'رصيد', 'Balance:')} ${_pick(context, main.nameAr, main.nameEn)}',
                  icon: Lu.calendarDays,
                  amount: Amount('${main.remaining}', _dayUnit(context, main.remaining)),
                  note: [
                    L(context, 'من ${dayCount(context, main.entitlement)}', 'of ${dayCount(context, main.entitlement)}'),
                    if (main.accrual == 'monthly') L(context, 'يُستحق شهرياً', 'accrues monthly') else if (main.accrual != null) L(context, 'يتجدد 1 يناير', 'renews 1 January'),
                  ].join(' · '),
                  footer: [for (final x in b.skip(1).take(2)) (_pick(context, x.nameAr, x.nameEn), '${x.remaining}', _dayUnit(context, x.remaining))],
                ),
              SectionHeading(L(context, 'طلباتي', 'My requests'), action: L(context, 'طلب جديد', 'New request'), actionIcon: Lu.plus, onAction: _new),
              if (list.isEmpty)
                EmptyNote(icon: Lu.calendarPlus, title: L(context, 'لا طلبات إجازة', 'No leave requests'), text: L(context, 'اطلب إجازة من «طلب جديد» أعلاه.', 'Ask for leave with “New request” above.'))
              else
                DataList(children: [
                  for (final r in list)
                    ItemRow(
                      leading: IconBox(icon: _icon(r.status)),
                      title: '${_pick(context, r.leaveTypeAr, r.leaveTypeEn)} · ${_range(context, r)}',
                      subtitle: [dayCount(context, r.days), _who(context, r)].where((x) => x.isNotEmpty).join(' · '),
                      end: _pill(context, r.status),
                      onTap: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => LeaveDetailScreen(request: r))),
                    ),
                ]),
            ],
          );
        },
      ),
    );
  }
}

/// One request: its facts, then the approval chain step by step with the person at each step.
class LeaveDetailScreen extends StatelessWidget {
  final LeaveRequest request;
  const LeaveDetailScreen({super.key, required this.request});

  @override
  Widget build(BuildContext context) {
    final r = request;
    final steps = [...r.approvals]..sort((a, b) => a.level.compareTo(b.level));
    final sub = _pick(context, r.substituteAr, r.substituteEn);
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
          if (sub.isNotEmpty) KV(L(context, 'البديل أثناء الغياب', 'Cover during the absence'), sub),
          if ((r.reason ?? '').trim().isNotEmpty) KV(L(context, 'ملاحظة', 'Note'), r.reason!.trim()),
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

/// NEW LEAVE (the owner's reference): the type as chips, from and to, the colleague who covers, a note, then what the
/// request will be — the working days the server counts, the balance after it and who approves it, read from the
/// server before anything is sent — and send / cancel. Rest days cannot be picked.
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
  bool _loadFailed = false;
  int? _typeId;
  DateTime? _from, _to;
  Colleague? _sub;
  LeavePreview? _preview;
  int _previewSeq = 0;
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
        _balances = _ordered(r[1] as List<LeaveBalance>);
        _workDays = r[2] as List<bool>?;
        // the chips follow the balances' order: annual, sick, casual, then the rest
        final order = {for (var i = 0; i < _balances.length; i++) _balances[i].leaveTypeId: i};
        _types.sort((a, b) => (order[a.id] ?? 999).compareTo(order[b.id] ?? 999));
        _typeId = _types.isEmpty ? null : _types.first.id;
        _loading = false;
      });
    } catch (_) {
      if (mounted) setState(() { _loading = false; _loadFailed = true; });
    }
  }

  @override
  void dispose() {
    _reason.dispose();
    super.dispose();
  }

  bool _isWorkDay(DateTime d) => _workDays == null || _workDays![d.weekday % 7];

  LeaveBalance? get _balance => _balances.where((b) => b.leaveTypeId == _typeId).firstOrNull;

  Future<void> _refreshPreview() async {
    if (_typeId == null || _from == null || _to == null) return setState(() => _preview = null);
    final seq = ++_previewSeq;
    final p = await ApiService.instance.previewLeave(_typeId!, _from!, _to!);
    if (mounted && seq == _previewSeq) setState(() => _preview = p);
  }

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
    _refreshPreview();
  }

  Future<void> _pickSubstitute() async {
    List<Colleague> people;
    try {
      people = await ApiService.instance.getColleagues();
    } catch (_) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(L(context, 'تعذّر تحميل الزملاء', 'Could not load colleagues'))));
      return;
    }
    if (!mounted) return;
    final id = await pickFromSheet<int>(
      context,
      title: L(context, 'البديل أثناء الغياب', 'Cover during the absence'),
      selected: _sub?.id ?? 0,
      options: [(0, L(context, 'بدون بديل', 'No cover'), null), for (final c in people) (c.id, _pick(context, c.nameAr, c.nameEn), null)],
    );
    if (id == null) return;
    setState(() => _sub = id == 0 ? null : people.firstWhere((c) => c.id == id));
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
      substituteEmployeeId: _sub?.id,
    );
    if (!mounted) return;
    setState(() => _busy = false);
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(ok ? L(context, 'أُرسل طلب الإجازة', 'Leave request sent') : (err ?? L(context, 'تعذّر إرسال الطلب', 'The request could not be sent')))));
    if (ok) Navigator.of(context).pop(true);
  }

  String _approvers(BuildContext context, LeavePreview p) {
    if (p.chainBroken) return L(context, 'تعذّر تحديد المسار — راجع الموارد البشرية', 'No path — contact HR');
    if (p.autoApproved) return L(context, 'يُعتمد تلقائياً', 'Approved automatically');
    return p.approvers.map((a) => _pick(context, a.$1, a.$2)).where((x) => x.isNotEmpty).join(L(context, ' ثم ', ' then '));
  }

  @override
  Widget build(BuildContext context) {
    final bal = _balance;
    final head = [
      BackLink(L(context, 'الإجازات', 'Leave')),
      PageHead(L(context, 'طلب إجازة', 'Request leave'),
          eyebrow: bal == null
              ? L(context, 'يُرسل عبر مسار الاعتماد في شركتك', "Sent through your company's approval path")
              : bal.code == 'annual'
                  ? L(context, 'رصيدك السنوي ${dayCount(context, bal.remaining)}', 'Your annual balance: ${dayCount(context, bal.remaining)}')
                  : '${L(context, 'رصيدك من', 'Your balance of')} ${_pick(context, bal.nameAr, bal.nameEn)}: ${dayCount(context, bal.remaining)}'),
    ];
    if (_loading) return const ExecFrame(child: Center(child: CircularProgressIndicator()));
    if (_loadFailed || _types.isEmpty) {
      return ExecFrame(
        child: ExecBody(children: [
          ...head,
          EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر تحميل أنواع الإجازات', 'Could not load leave types'), text: L(context, 'تحقق من الاتصال ثم أعد المحاولة.', 'Check the connection and try again.')),
        ]),
      );
    }
    final p = _preview;
    Widget dateField(bool isFrom) {
      final d = isFrom ? _from : _to;
      return FieldBox(
        label: isFrom ? L(context, 'من', 'From') : L(context, 'إلى', 'To'),
        onTap: () => _pickDate(isFrom),
        child: Row(children: [
          Expanded(child: Text(d == null ? L(context, 'اختر', 'Choose') : dayMonth(context, d), style: d == null ? T.small : null)),
          const Icon(Lu.calendar, size: 15, color: Brand.support),
        ]),
      );
    }

    return ExecFrame(
      child: ExecBody(children: [
        ...head,
        ChipTabs(
          items: [for (final t in _types) _pick(context, t.nameAr, t.nameEn)],
          index: _types.indexWhere((t) => t.id == _typeId),
          onTap: (i) {
            setState(() => _typeId = _types[i].id);
            _refreshPreview();
          },
        ),
        Row(children: [Expanded(child: dateField(true)), const SizedBox(width: 9), Expanded(child: dateField(false))]),
        FieldBox(
          label: L(context, 'البديل أثناء الغياب', 'Cover during the absence'),
          onTap: _pickSubstitute,
          child: Row(children: [
            Expanded(child: Text(_sub == null ? L(context, 'بدون بديل', 'No cover') : _pick(context, _sub!.nameAr, _sub!.nameEn), style: _sub == null ? T.small : null)),
            const Icon(Lu.chevronDown, size: 15, color: Brand.support),
          ]),
        ),
        FieldBox(
          label: L(context, 'ملاحظة', 'Note'),
          child: TextField(
            controller: _reason,
            maxLines: 2,
            minLines: 1,
            style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w500, color: Brand.inkC),
            decoration: const InputDecoration(isDense: true, border: InputBorder.none, enabledBorder: InputBorder.none, focusedBorder: InputBorder.none, filled: false, contentPadding: EdgeInsets.zero),
          ),
        ),
        if (p != null)
          DataList(children: [
            KV(L(context, 'أيام العمل المحسوبة', 'Working days counted'), dayCount(context, p.days)),
            if (p.after != null) KV(L(context, 'الرصيد بعد الطلب', 'Balance after the request'), dayCount(context, p.after!)),
            KV(L(context, 'يعتمده', 'Approved by'), _approvers(context, p)),
          ]),
        if (p != null && p.after != null && p.after! < 0) ...[
          const SizedBox(height: 9),
          SoftNote(L(context, 'الطلب يتجاوز رصيدك؛ سيرفضه النظام.', 'The request exceeds your balance; the system will refuse it.')),
        ],
        const SizedBox(height: 14),
        Row(children: [
          Expanded(child: FilledButton(onPressed: _busy ? null : _send, child: Text(L(context, 'إرسال الطلب', 'Send request')))),
          const SizedBox(width: 9),
          Expanded(child: OutlinedButton(onPressed: () => Navigator.of(context).maybePop(), child: Text(L(context, 'إلغاء', 'Cancel')))),
        ]),
      ]),
    );
  }
}
