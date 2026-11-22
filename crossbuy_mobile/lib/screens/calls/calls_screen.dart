import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import 'package:video_player/video_player.dart';

import '../../models/calls.dart';
import '../../providers/calls_provider.dart';
import '../../services/api_service.dart';
import '../../services/app_nav.dart';
import '../../theme/brand.dart';
import '../../widgets/ui.dart';

/// The calls screens — exactly the approved prototype (design-output/executive-prototype):
///   ?page=calls            الأشخاص: the banner, the search, «المتاحون أولاً», each person with voice and video
///   ?page=call-log         السجل: الكل · فائتة · مسجّلة, by day, call back / play
///   ?page=call-group-pick  مكالمة جماعية: who to invite, «بخصوص مريض (اختياري)», start by video or by voice
/// The three tabs switch between them, as in the prototype.
class CallsScreen extends StatefulWidget {
  final int tab;   // 0 people · 1 log · 2 group
  const CallsScreen({super.key, this.tab = 0});
  @override
  State<CallsScreen> createState() => _CallsScreenState();
}

class _CallsScreenState extends State<CallsScreen> {
  late int _tab = widget.tab;
  List<CallPerson>? _people;
  String _q = '';
  Timer? _debounce;
  List<CallLogRow>? _log;
  int _logFilter = 0;   // الكل · فائتة · مسجّلة
  final Set<int> _picked = {};
  CallPatient? _patient;
  bool _failed = false;
  late final CallsProvider _calls;

  @override
  void initState() {
    super.initState();
    _calls = context.read<CallsProvider>();
    _calls.notice.addListener(_onNotice);
    _calls.connect();
    _loadPeople();
    _loadLog();
  }

  @override
  void dispose() {
    _calls.notice.removeListener(_onNotice);
    _debounce?.cancel();
    super.dispose();
  }

  void _onNotice() {
    final v = _calls.notice.value;
    if (v == null || !mounted || _calls.busy) return;
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(v), behavior: SnackBarBehavior.floating));
    _loadLog();
  }

  Future<void> _loadPeople() async {
    try {
      final list = await ApiService.instance.getCallPeople(q: _q);
      if (mounted) setState(() { _people = list; _failed = false; });
    } catch (_) {
      if (mounted) setState(() => _failed = true);
    }
  }

  Future<void> _loadLog() async {
    try {
      final (rows, _) = await ApiService.instance.getCallLog(only: const [null, 'missed', 'recorded'][_logFilter]);
      if (mounted) setState(() => _log = rows);
    } catch (_) {}
  }

  @override
  Widget build(BuildContext context) {
    final p = context.watch<CallsProvider>();
    final me = p.me;
    final head = switch (_tab) {
      1 => [BackLink(L(context, 'مساحات العمل', 'Workspaces')), PageHead(L(context, 'سجل المكالمات', 'Call log'), eyebrow: L(context, 'مكالماتي', 'My calls'))],
      2 => [
          BackLink(L(context, 'الاتصال الداخلي', 'Internal calls'), onTap: () => setState(() => _tab = 0)),
          PageHead(L(context, 'مكالمة جماعية', 'Group call'),
              eyebrow: L(context, 'حتى ${_count(me?.groupMax ?? 4)} معك', 'Up to ${me?.groupMax ?? 4} people with you'),
              sub: L(context, 'اختر من تدعوهم؛ تصل لكلٍّ منهم دعوة يقبلها أو يعتذر عنها', 'Choose who to invite; each gets an invitation to accept or decline')),
        ],
      _ => [BackLink(L(context, 'مساحات العمل', 'Workspaces')), PageHead(L(context, 'الاتصال الداخلي', 'Internal calls'), eyebrow: L(context, 'داخل شبكة المستشفى فقط', 'Inside the hospital network only'))],
    };
    final tabs = ChipTabs(
      items: [L(context, 'الأشخاص', 'People'), L(context, 'السجل', 'Log'), if (me?.group ?? true) L(context, 'مكالمة جماعية', 'Group call')],
      index: _tab,
      onTap: (i) => setState(() => _tab = i),
    );
    final List<Widget> body;
    if (me != null && !me.installed) {
      body = [SoftNote(L(context, 'الاتصال الداخلي غير مفعّل بعد', 'Internal calls are not set up yet'))];
    } else if (_failed) {
      body = [EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر التحميل', 'Could not load'), text: L(context, 'الاتصال الداخلي يعمل على شبكة المستشفى فقط.', 'Internal calls work on the hospital network only.'))];
    } else {
      body = switch (_tab) { 1 => _logTab(context, p), 2 => _groupTab(context, p), _ => _peopleTab(context, p) };
    }
    return ExecFrame(
      bottomBar: const CallsNav(),
      child: ExecBody(onRefresh: () async { await _loadPeople(); await _loadLog(); }, children: [...head, tabs, ...body]),
    );
  }

  String _count(int n) => n == 4 ? 'أربعة أشخاص' : '$n أشخاص';

  // ------------------------------------------------------------------------------------------ الأشخاص
  String _state(CallsProvider p, CallPerson x) => p.presenceOf(x.id, x.state);

  // "phone" (push, 2026-11-22): the app is closed but a call rings it — a hollow brand ring
  Widget _dot(String st) => Container(
        width: 11, height: 11,
        decoration: BoxDecoration(color: st == 'on' ? Brand.blue : st == 'busy' ? Brand.badC : st == 'phone' ? Brand.paper : const Color(0xFFB3BCCB), shape: BoxShape.circle,
            border: Border.all(color: st == 'phone' ? Brand.blue : Brand.paper, width: 2)),
      );

  Widget _face(CallPerson x, String st) => Stack(clipBehavior: Clip.none, children: [
        PersonAvatar(photo: x.photo, name: x.display(context), size: 40, radius: 12),
        PositionedDirectional(bottom: -2, end: -2, child: _dot(st)),
      ]);

  /// .cb — the 34px call buttons (video filled).
  Widget _cb(IconData i, {bool fill = false, bool enabled = true, required VoidCallback onTap}) => GestureDetector(
        onTap: enabled ? onTap : null,
        child: Container(
          width: 34, height: 34, margin: const EdgeInsetsDirectional.only(start: 6),
          decoration: BoxDecoration(
            color: !enabled ? Brand.page : fill ? Brand.blue : Brand.paper, borderRadius: BorderRadius.circular(11),
            border: Border.all(color: fill && enabled ? Brand.blue : Brand.lineC)),
          child: Icon(i, size: 16, color: !enabled ? const Color(0xFFB3BCCB) : fill ? Colors.white : Brand.blue),
        ),
      );

  Widget _person(BuildContext context, CallsProvider p, CallPerson x) {
    final st = _state(p, x);
    final can = (st == 'on' || st == 'phone') && p.connected;
    final label = st == 'on' ? L(context, 'متاح الآن', 'Available now') : st == 'busy' ? L(context, 'في مكالمة', 'In a call')
        : st == 'phone' ? L(context, 'متاح على الهاتف', 'On their phone') : L(context, 'غير متصل', 'Offline');
    return Opacity(
      opacity: st == 'off' ? .62 : 1,
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 11),
        child: Row(children: [
          _face(x, st),
          const SizedBox(width: 10),
          Expanded(
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(x.display(context), maxLines: 1, overflow: TextOverflow.ellipsis, style: T.body),
              const SizedBox(height: 2),
              Text([x.role(context), label].where((s) => s.isNotEmpty).join(' · '), maxLines: 1, overflow: TextOverflow.ellipsis,
                  style: T.small.copyWith(color: st == 'busy' ? Brand.badC : Brand.mutedC)),
            ]),
          ),
          _cb(Lu.phone, enabled: can, onTap: () => p.startCall(x, video: false)),
          if (p.me?.video ?? true) _cb(Lu.video, fill: true, enabled: can, onTap: () => p.startCall(x, video: true)),
        ]),
      ),
    );
  }

  List<Widget> _peopleTab(BuildContext context, CallsProvider p) {
    final list = _people;
    const rank = {'on': 0, 'busy': 1, 'phone': 2, 'off': 3};
    final sorted = list == null ? null : (List.of(list)..sort((a, b) {
        final r = (rank[_state(p, a)] ?? 3).compareTo(rank[_state(p, b)] ?? 3);
        return r != 0 ? r : a.nameEn.toLowerCase().compareTo(b.nameEn.toLowerCase());
      }));
    final online = list?.where((x) => _state(p, x) == 'on').length ?? 0;
    return [
      Container(
        padding: const EdgeInsets.fromLTRB(13, 12, 13, 12),
        margin: const EdgeInsets.only(bottom: 14),
        decoration: BoxDecoration(color: Brand.soft, borderRadius: BorderRadius.circular(14)),
        child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
          const Icon(Lu.shieldCheck, size: 16, color: Brand.blue),
          const SizedBox(width: 10),
          Expanded(child: Text(L(context, 'الصوت والصورة لا يغادران شبكة المستشفى. لا يعمل الاتصال إلا على شبكة المستشفى (Wi-Fi الداخلية أو VPN المستشفى).',
              'Voice and picture never leave the hospital network. Calls work only on the hospital network (internal Wi-Fi or the hospital VPN).'), style: const TextStyle(fontSize: 10, color: Brand.inkC, height: 1.7))),
        ]),
      ),
      Container(
        margin: const EdgeInsets.only(bottom: 12),
        decoration: BoxDecoration(color: Brand.paper, border: Border.all(color: Brand.lineC), borderRadius: BorderRadius.circular(14)),
        child: TextField(
          decoration: InputDecoration(
            prefixIcon: const Icon(Lu.search, size: 15, color: Brand.mutedC),
            hintText: L(context, 'ابحث بالاسم أو القسم أو التخصص', 'Search by name, department or speciality'),
            hintStyle: const TextStyle(fontSize: 11, color: Brand.mutedC),
            border: InputBorder.none, isDense: true, contentPadding: const EdgeInsets.symmetric(vertical: 12),
          ),
          style: const TextStyle(fontSize: 12),
          onChanged: (v) { _q = v; _debounce?.cancel(); _debounce = Timer(const Duration(milliseconds: 350), _loadPeople); },
        ),
      ),
      Padding(
        padding: const EdgeInsets.only(top: 6, bottom: 13),
        child: Row(children: [
          Text(L(context, 'المتاحون أولاً', 'Available first'), style: T.h2),
          Container(
            margin: const EdgeInsetsDirectional.only(start: 6), width: 22, height: 20, alignment: Alignment.center,
            decoration: BoxDecoration(color: Brand.soft, borderRadius: BorderRadius.circular(6)),
            child: Text('$online', style: const TextStyle(fontSize: 10, fontWeight: FontWeight.w600, color: Brand.blue)),
          ),
          const Spacer(),
          Tag.info(p.connected ? L(context, 'يتحدّث تلقائياً', 'Updates live') : L(context, 'جارٍ الاتصال…', 'Connecting…')),
        ]),
      ),
      if (sorted == null) const Padding(padding: EdgeInsets.all(30), child: Center(child: CircularProgressIndicator()))
      else if (sorted.isEmpty) EmptyNote(icon: Lu.users, title: L(context, 'لا أحد', 'Nobody'), text: L(context, 'لا يطابق البحث أحدًا', 'Nobody matches the search'))
      else DataList(children: [for (final x in sorted) _person(context, p, x)]),
    ];
  }

  // ------------------------------------------------------------------------------------------ السجل
  List<Widget> _logTab(BuildContext context, CallsProvider p) {
    final rows = _log;
    final missed = p.me?.missed ?? 0;
    final out = <Widget>[
      ChipTabs(
        items: [L(context, 'الكل', 'All'), L(context, 'فائتة', 'Missed') + (missed > 0 ? ' · $missed' : ''), L(context, 'مسجّلة', 'Recorded')],
        index: _logFilter,
        onTap: (i) { setState(() { _logFilter = i; _log = null; }); _loadLog(); },
      ),
    ];
    if (rows == null) return [...out, const Padding(padding: EdgeInsets.all(30), child: Center(child: CircularProgressIndicator()))];
    if (rows.isEmpty) return [...out, EmptyNote(icon: Lu.history, title: L(context, 'لا مكالمات', 'No calls'), text: L(context, 'تظهر هنا مكالماتك', 'Your calls show here'))];
    // by day: اليوم · أمس · the date
    final now = DateTime.now();
    String day(DateTime d) {
      final a = DateTime(d.year, d.month, d.day), t = DateTime(now.year, now.month, now.day);
      final diff = t.difference(a).inDays;
      return diff == 0 ? L(context, 'اليوم', 'Today') : diff == 1 ? L(context, 'أمس', 'Yesterday') : dayMonth(context, d);
    }
    String? cur;
    var group = <Widget>[];
    void flush() { if (group.isNotEmpty) { out.add(DataList(children: group)); group = []; } }
    for (final r in rows) {
      final d = day(r.startedAt);
      if (d != cur) {
        flush();
        out.add(Padding(padding: EdgeInsets.only(top: cur == null ? 0 : 23, bottom: 13), child: Text(d, style: T.h2)));
        cur = d;
      }
      group.add(_logRow(context, p, r));
    }
    flush();
    return out;
  }

  Widget _logRow(BuildContext context, CallsProvider p, CallLogRow r) {
    final who = r.group ? '${L(context, 'مكالمة جماعية', 'Group call')} · ${r.members.length + 1}' : (r.other?.display(context) ?? '—');
    final dir = r.missedForMe
        ? L(context, 'فائتة', 'Missed')
        : switch (r.status) {
            'Declined' => r.outgoing ? L(context, 'اعتذر', 'Declined') : L(context, 'رفضتها', 'You declined'),
            'Cancelled' => L(context, 'ملغاة', 'Cancelled'),
            'Missed' => L(context, 'لم يُرد عليها', 'Not answered'),
            _ => r.outgoing ? L(context, 'صادرة', 'Outgoing') : L(context, 'واردة', 'Incoming'),
          };
    final t = '${r.startedAt.hour.toString().padLeft(2, '0')}:${r.startedAt.minute.toString().padLeft(2, '0')}';
    final parts = <String>[
      if (r.group) [L(context, 'أنت', 'You'), ...r.members.map((m) => m.display(context))].join(isAr(context) ? ' و' : ', ') else dir,
      if (!r.group) (r.video ? L(context, 'فيديو', 'Video') : L(context, 'صوت', 'Voice')),
      t,
      if ((r.seconds ?? 0) > 0) mmss(r.seconds!),
      if (r.patient != null) L(context, 'بخصوص ${r.patient}', 'about ${r.patientEn ?? r.patient}'),
    ];
    final photo = r.group ? (r.members.isNotEmpty ? r.members.first.photo : null) : r.other?.photo;
    final Widget end = r.recordingId != null
        ? _cb(Lu.play, onTap: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => RecordingScreen(id: r.recordingId!))))
        : (!r.group && r.other != null)
            ? (r.missedForMe ? _cb(Lu.phoneCall, fill: true, onTap: () => p.startCall(r.other!, video: false)) : _cb(Lu.phone, onTap: () => p.startCall(r.other!, video: false)))
            : const SizedBox();
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 11),
      child: Row(children: [
        PersonAvatar(photo: photo, name: who, size: 40, radius: 12),
        const SizedBox(width: 10),
        Expanded(
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              Flexible(child: Text(who, maxLines: 1, overflow: TextOverflow.ellipsis, style: T.body.copyWith(color: r.missedForMe ? Brand.badC : Brand.inkC))),
              if (r.recordingId != null)
                Container(
                  margin: const EdgeInsetsDirectional.only(start: 9),
                  padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                  decoration: BoxDecoration(color: Brand.badSoft, borderRadius: BorderRadius.circular(6)),
                  child: Text(L(context, 'مسجّلة', 'Recorded'), style: const TextStyle(fontSize: 9, fontWeight: FontWeight.w600, color: Brand.badC)),
                ),
            ]),
            const SizedBox(height: 2),
            Text(parts.join(' · '), maxLines: 1, overflow: TextOverflow.ellipsis, style: T.small),
          ]),
        ),
        end,
      ]),
    );
  }

  // ------------------------------------------------------------------------------------------ مكالمة جماعية
  List<Widget> _groupTab(BuildContext context, CallsProvider p) {
    final cap = p.me?.groupMax ?? 4;
    final list = (_people ?? []).where((x) => _state(p, x) != 'off' && _state(p, x) != 'phone').toList()   // a group call does not ring through push yet
      ..sort((a, b) => (_state(p, a) == 'on' ? 0 : 1).compareTo(_state(p, b) == 'on' ? 0 : 1));
    final chosen = (_people ?? []).where((x) => _picked.contains(x.id)).toList();
    Widget box(CallPerson x) {
      final st = _state(p, x);
      final on = _picked.contains(x.id);
      return GestureDetector(
        onTap: st != 'on' ? null : () => setState(() { if (on) { _picked.remove(x.id); } else if (_picked.length < cap - 1) { _picked.add(x.id); } }),
        child: Container(
          width: 34, height: 34,
          decoration: BoxDecoration(
            color: st != 'on' ? Brand.page : on ? Brand.blue : Brand.paper, borderRadius: BorderRadius.circular(11),
            border: Border.all(color: on ? Brand.blue : Brand.lineC)),
          child: st != 'on' ? const Icon(Lu.minus, size: 16, color: Color(0xFFB3BCCB)) : on ? const Icon(Lu.check, size: 16, color: Colors.white) : null,
        ),
      );
    }
    return [
      if (_people == null) const Padding(padding: EdgeInsets.all(30), child: Center(child: CircularProgressIndicator()))
      else if (list.isEmpty) SoftNote(L(context, 'لا أحد متاح الآن', 'Nobody is available now'))
      else DataList(children: [
        for (final x in list)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 11),
            child: Row(children: [
              _face(x, _state(p, x)),
              const SizedBox(width: 10),
              Expanded(
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text(x.display(context), maxLines: 1, overflow: TextOverflow.ellipsis, style: T.body),
                  const SizedBox(height: 2),
                  Text([x.role(context), _state(p, x) == 'on' ? L(context, 'متاح الآن', 'Available now') : L(context, 'في مكالمة', 'In a call')].where((s) => s.isNotEmpty).join(' · '),
                      maxLines: 1, overflow: TextOverflow.ellipsis, style: T.small.copyWith(color: _state(p, x) == 'busy' ? Brand.badC : Brand.mutedC)),
                ]),
              ),
              box(x),
            ]),
          ),
      ]),
      const SizedBox(height: 12),
      FieldBox(
        label: L(context, 'بخصوص مريض (اختياري)', 'About a patient (optional)'),
        onTap: () => _pickPatient(context),
        child: Row(children: [
          Expanded(child: Text(_patient == null ? L(context, 'لا مريض', 'No patient') : '${_patient!.name} · ${_patient!.mrn}', maxLines: 1, overflow: TextOverflow.ellipsis)),
          if (_patient != null) GestureDetector(onTap: () => setState(() => _patient = null), child: const Icon(Lu.x, size: 15, color: Brand.support))
          else const Icon(Lu.chevronDown, size: 15, color: Brand.support),
        ]),
      ),
      Padding(
        padding: const EdgeInsets.only(top: 7),
        child: Row(children: [
          Expanded(
            flex: 3,
            child: _ActionBtn(L(context, 'بدء مكالمة جماعية بالفيديو · ${_picked.length + 1}', 'Start a video group call · ${_picked.length + 1}'), primary: true,
                onTap: _picked.length < 2 ? null : () => p.startGroup(chosen, video: true, patientId: _patient?.id)),
          ),
          const SizedBox(width: 9),
          Expanded(flex: 2, child: _ActionBtn(L(context, 'بالصوت', 'By voice'), onTap: _picked.length < 2 ? null : () => p.startGroup(chosen, video: false, patientId: _patient?.id))),
        ]),
      ),
      Padding(
        padding: const EdgeInsets.only(top: 12),
        child: Text(cap <= 4 ? L(context, 'أكثر من أربعة يحتاج خادم الوسائط الداخلي — حتى اثني عشر شخصاً', 'More than four needs the internal media server — up to twelve')
            : L(context, 'اختر شخصين على الأقل', 'Choose at least two people'), textAlign: TextAlign.center, style: T.small),
      ),
    ];
  }

  Future<void> _pickPatient(BuildContext context) async {
    final ctl = TextEditingController();
    List<CallPatient> found = const [];
    Timer? t;
    final picked = await showModalBottomSheet<CallPatient>(
      context: context,
      isScrollControlled: true,
      backgroundColor: Brand.paper,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(26))),
      builder: (c) => StatefulBuilder(
        builder: (c, set) => Padding(
          padding: EdgeInsets.fromLTRB(20, 16, 20, MediaQuery.of(c).viewInsets.bottom + 20),
          child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            Text(L(c, 'بخصوص مريض', 'About a patient'), style: T.h2),
            const SizedBox(height: 10),
            TextField(
              controller: ctl, autofocus: true,
              decoration: InputDecoration(hintText: L(c, 'الاسم أو الرقم الطبي', 'Name or MRN'), prefixIcon: const Icon(Lu.search, size: 15)),
              onChanged: (v) { t?.cancel(); t = Timer(const Duration(milliseconds: 350), () async { final r = await ApiService.instance.getCallPatients(v.trim()); set(() => found = r); }); },
            ),
            const SizedBox(height: 10),
            if (found.isNotEmpty)
              ConstrainedBox(
                constraints: BoxConstraints(maxHeight: MediaQuery.of(c).size.height * .4),
                child: SingleChildScrollView(
                  child: DataList(children: [
                    for (final x in found) ItemRow(leading: PersonAvatar(photo: x.photo, name: x.name, size: 36, radius: 10), title: x.name, subtitle: x.mrn, onTap: () => Navigator.of(c).pop(x)),
                  ]),
                ),
              ),
          ]),
        ),
      ),
    );
    t?.cancel();
    if (picked != null && mounted) setState(() => _patient = picked);
  }
}

/// .actions button — blue primary, outlined secondary; dimmed while it cannot be used.
class _ActionBtn extends StatelessWidget {
  final String text;
  final bool primary;
  final VoidCallback? onTap;
  const _ActionBtn(this.text, {this.onTap, this.primary = false});
  @override
  Widget build(BuildContext context) => Opacity(
        opacity: onTap == null ? .5 : 1,
        child: GestureDetector(
          onTap: onTap,
          child: Container(
            padding: const EdgeInsets.symmetric(vertical: 13, horizontal: 12),
            alignment: Alignment.center,
            decoration: BoxDecoration(color: primary ? Brand.blue : Brand.paper, border: Border.all(color: primary ? Brand.blue : Brand.lineC), borderRadius: BorderRadius.circular(12)),
            child: Text(text, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12, color: primary ? Colors.white : Brand.inkC)),
          ),
        ),
      );
}

/// ?page=call-rec — the recording: the player, what it holds, who agreed, the patient, when it is deleted, who opened it.
class RecordingScreen extends StatefulWidget {
  final int id;
  const RecordingScreen({super.key, required this.id});
  @override
  State<RecordingScreen> createState() => _RecordingScreenState();
}

class _RecordingScreenState extends State<RecordingScreen> {
  Map<String, dynamic>? _d;
  bool _missing = false;
  VideoPlayerController? _v;
  Map<String, dynamic>? _sum;          // the call's summary (owner 2026-11-19)
  bool _sumStarting = false;
  Timer? _sumTimer;

  @override
  void initState() {
    super.initState();
    _load();
    _loadSummary();
  }

  Future<void> _loadSummary() async {
    _sumTimer?.cancel();
    final s = await ApiService.instance.getCallSummary(widget.id);
    if (!mounted) return;
    setState(() => _sum = s);
    // while it is being made, ask again every few seconds (the work happens on the server, not here)
    if (s?['status'] == 'working') _sumTimer = Timer(const Duration(seconds: 6), _loadSummary);
  }

  Future<void> _startSummary() async {
    setState(() => _sumStarting = true);
    final (ok, s) = await ApiService.instance.startCallSummary(widget.id);
    if (!mounted) return;
    setState(() { _sumStarting = false; if (s != null && s['status'] != 'none') _sum = s; });
    if (!ok) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(L(context, 'الملخص غير متاح الآن — حاول لاحقاً', 'The summary is not available right now — try again later'))));
    if (_sum?['status'] == 'working') _loadSummary();
  }

  /// .csum in the prototype: the overview, key points, decisions, tasks, the full text, and where it came from.
  Widget _summaryCard(BuildContext context) {
    final s = _sum;
    final st = s?['status'] ?? 'none';
    Widget btn(String label, IconData icon) => Align(
          alignment: AlignmentDirectional.centerStart,
          child: FilledButton.icon(
            style: FilledButton.styleFrom(backgroundColor: Brand.navy, padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8), shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10))),
            onPressed: _sumStarting ? null : _startSummary,
            icon: _sumStarting ? const SizedBox(width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 1.6, color: Colors.white)) : Icon(icon, size: 15),
            label: Text(_sumStarting ? L(context, 'جارٍ إرسال التسجيل…', 'Sending the recording…') : label, style: const TextStyle(fontSize: 12)),
          ),
        );
    final List<Widget> body;
    if (st == 'working') {
      final pc = (((s?['progress'] as num?) ?? 0) * 100).round();
      body = [
        Text('${pc < 75 ? L(context, 'جارٍ تحويل المكالمة إلى نص…', 'Turning the call into text…') : L(context, 'جارٍ كتابة الملخص…', 'Writing the summary…')} $pc%', style: T.sub),
        const SizedBox(height: 8),
        ClipRRect(borderRadius: BorderRadius.circular(3), child: LinearProgressIndicator(value: pc.clamp(4, 100) / 100, minHeight: 5, backgroundColor: Brand.lineC, color: Brand.blue)),
        const SizedBox(height: 8),
        Text(L(context, 'يمكنك مغادرة الصفحة؛ سيكون الملخص هنا حين يجهز.', 'You can leave this page; the summary will be here when it is ready.'), style: T.small),
      ];
    } else if (st == 'done') {
      final m = (s?['summary'] as Map?) ?? const {};
      List<String> strs(dynamic x) => (x as List? ?? const []).map((e) => '$e').where((e) => e.trim().isNotEmpty).toList();
      Widget sec(IconData icon, String title, List<Widget> rows) => rows.isEmpty
          ? const SizedBox.shrink()
          : Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Row(children: [Icon(icon, size: 13, color: Brand.blue), const SizedBox(width: 5), Text(title, style: const TextStyle(fontSize: 10.5, fontWeight: FontWeight.w700, color: Brand.blue))]),
                const SizedBox(height: 3),
                ...rows,
              ]),
            );
      Widget li(InlineSpan span) => Padding(padding: const EdgeInsets.only(bottom: 2), child: Text.rich(TextSpan(children: [const TextSpan(text: '•  '), span]), style: const TextStyle(fontSize: 11.5, height: 1.7, color: Brand.inkC)));
      final overview = '${m['overview'] ?? ''}'.trim();
      final transcript = '${s?['transcript'] ?? ''}'.trim();
      body = [
        Text(overview.isEmpty
                ? (m['tooShort'] == true || transcript.isNotEmpty
                    ? L(context, 'لم يُقل في المكالمة ما يكفي لتلخيصها.', 'Too little was said in this call to summarise it.')
                    : L(context, 'لم تُسمع كلمات في هذا التسجيل', 'No words were heard in this recording'))
                : overview,
            style: TextStyle(fontSize: 12, height: 1.8, color: overview.isEmpty ? Brand.mutedC : Brand.inkC)),
        sec(Lu.list, L(context, 'أهم النقاط', 'Key points'), [for (final p in strs(m['points'])) li(TextSpan(text: p))]),
        sec(Lu.circleCheck, L(context, 'القرارات', 'Decisions'), [for (final p in strs(m['decisions'])) li(TextSpan(text: p))]),
        sec(Lu.listTodo, L(context, 'المهام', 'Tasks'), [
          for (final a in (m['actions'] as List? ?? const []).whereType<Map>())
            li(TextSpan(children: [TextSpan(text: '${a['owner'] ?? ''}', style: const TextStyle(fontWeight: FontWeight.w700)), TextSpan(text: ' — ${a['task'] ?? ''}')])),
        ]),
        if (transcript.isNotEmpty)
          Theme(
            data: Theme.of(context).copyWith(dividerColor: Colors.transparent),
            child: ExpansionTile(
              tilePadding: EdgeInsets.zero, childrenPadding: const EdgeInsets.only(bottom: 6), dense: true,
              title: Text(L(context, 'النص الكامل للمكالمة', 'The full text of the call'), style: const TextStyle(fontSize: 11, fontWeight: FontWeight.w600, color: Brand.blue)),
              children: [Text(transcript, style: const TextStyle(fontSize: 11, height: 1.7, color: Brand.mutedC))],
            ),
          ),
        Text(L(context, 'أُعدّ تلقائياً من التسجيل على خوادم الشركة — راجعه قبل الاعتماد عليه.', 'Made automatically from the recording on the company’s servers — check before relying on it.'), style: T.small),
      ];
    } else if (st == 'failed') {
      body = [Text(L(context, 'تعذّر تلخيص هذه المكالمة.', 'This call could not be summarised.'), style: T.sub), const SizedBox(height: 8), btn(L(context, 'إعادة المحاولة', 'Try again'), Lu.rotateCcw)];
    } else {
      body = [
        Text(L(context, 'يُحوَّل التسجيل إلى نص ويُلخَّص على خوادم الشركة نفسها، ويستغرق ذلك بضع دقائق.', 'The recording is turned into text and summarised on the company’s own servers; it takes a few minutes.'), style: T.sub),
        const SizedBox(height: 10),
        btn(L(context, 'تلخيص المكالمة', 'Summarise the call'), Lu.sparkles),
      ];
    }
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.fromLTRB(16, 14, 16, 14),
      decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(18), border: Border.all(color: Brand.lineC)),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: body),
    );
  }

  Future<void> _load() async {
    final d = await ApiService.instance.getCallRecording(widget.id);
    if (!mounted) return;
    if (d == null) { setState(() => _missing = true); return; }
    setState(() => _d = d);
    if (d['status'] == 'Deleted') return;
    final f = await ApiService.instance.mediaFile(ApiService.instance.callRecordingMediaUrl(widget.id, log: true));   // see mediaFile
    if (!mounted || f == null) return;
    final v = VideoPlayerController.file(f);
    _v = v;
    v.addListener(() { if (mounted) setState(() {}); });
    try { await v.initialize(); if (mounted) setState(() {}); } catch (_) {}
  }

  @override
  void dispose() {
    _v?.dispose();
    _sumTimer?.cancel();
    super.dispose();
  }

  String _str(dynamic x) => x?.toString() ?? '';
  String _name(dynamic who) => who is Map ? _str(who['name']) : '—';
  DateTime? _t(dynamic iso) { final s = _str(iso); return s.isEmpty ? null : DateTime.tryParse(s.endsWith('Z') || s.contains('+') ? s : '${s}Z')?.toLocal(); }
  String _hm(DateTime? t) => t == null ? '' : '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';

  @override
  Widget build(BuildContext context) {
    final d = _d;
    final v = _v;
    final head = [BackLink(L(context, 'سجل المكالمات', 'Call log')), PageHead(L(context, 'تسجيل المكالمة', 'Call recording'), eyebrow: d == null ? null : _str(d['folder']))];
    if (_missing) return ExecFrame(bottomBar: const CallsNav(), child: ExecBody(children: [...head, SoftNote(L(context, 'هذا التسجيل غير متاح لك', 'This recording is not available to you'))]));
    if (d == null) return const ExecFrame(bottomBar: CallsNav(), child: Center(child: CircularProgressIndicator()));
    final started = _t(d['startedAt']);
    final size = (((d['size'] as num?) ?? 0) / 1048576).toStringAsFixed(1);
    final consent = d['group'] == true
        ? (d['consents'] as List? ?? []).whereType<Map>().map((c) => _name(c['who'])).join('، ')
        : L(context, 'طلبه ${_name(d['requestedBy'])} · ووافق ${_name(d['agreedBy'])} ${_hm(_t(d['agreedAt']))}',
            'Asked by ${_name(d['requestedBy'])} · agreed by ${_name(d['agreedBy'])} ${_hm(_t(d['agreedAt']))}');
    final pos = v?.value.position ?? Duration.zero, dur = v?.value.duration ?? Duration.zero;
    return ExecFrame(
      bottomBar: const CallsNav(),
      child: ExecBody(children: [
        ...head,
        Container(
          height: 190,
          margin: const EdgeInsets.only(bottom: 12),
          decoration: BoxDecoration(color: const Color(0xFF162238), borderRadius: BorderRadius.circular(18)),
          clipBehavior: Clip.antiAlias,
          child: d['status'] == 'Deleted'
              ? Center(child: Text(L(context, 'حُذف هذا التسجيل عند انتهاء مدة الاحتفاظ به', 'This recording was deleted when its retention ended'), style: const TextStyle(color: Brand.onNavyMuted, fontSize: 11)))
              : Stack(fit: StackFit.expand, children: [
                  if (v != null && v.value.isInitialized) FittedBox(fit: BoxFit.cover, clipBehavior: Clip.hardEdge, child: SizedBox(width: v.value.size.width, height: v.value.size.height, child: VideoPlayer(v)))
                  else const Center(child: CircularProgressIndicator(color: Colors.white)),
                  Positioned(
                    left: 0, right: 0, bottom: 0,
                    child: Container(
                      padding: const EdgeInsets.fromLTRB(14, 18, 14, 10),
                      decoration: const BoxDecoration(gradient: LinearGradient(begin: Alignment.topCenter, end: Alignment.bottomCenter, colors: [Color(0x000C1837), Color(0xDD0C1837)])),
                      child: Directionality(
                        textDirection: TextDirection.ltr,
                        child: Row(children: [
                          GestureDetector(onTap: v == null ? null : () => v.value.isPlaying ? v.pause() : v.play(), child: Icon(v?.value.isPlaying == true ? Lu.pause : Lu.play, size: 18, color: Colors.white)),
                          const SizedBox(width: 10),
                          Expanded(child: v != null && v.value.isInitialized ? VideoProgressIndicator(v, allowScrubbing: true, colors: const VideoProgressColors(playedColor: Brand.gold, bufferedColor: Color(0x55FFFFFF), backgroundColor: Color(0x33FFFFFF))) : const SizedBox()),
                          const SizedBox(width: 10),
                          Text('${mmss(pos.inSeconds)} / ${mmss(dur.inSeconds > 0 ? dur.inSeconds : ((d['seconds'] as num?)?.toInt() ?? 0))}', style: const TextStyle(color: Colors.white, fontSize: 11, fontWeight: FontWeight.w600)),
                        ]),
                      ),
                    ),
                  ),
                ]),
        ),
        DataList(children: [
          _Fact(L(context, 'المكالمة', 'The call'), '${d['outgoing'] == true ? L(context, 'صادرة إلى', 'Outgoing to') : L(context, 'واردة من', 'Incoming from')} ${_name(d['other'])} · ${_hm(started)}'),
          _Fact(L(context, 'التسجيل', 'Recording'), '${_str(d['kind']) == 'video' ? L(context, 'صوت وفيديو', 'Voice and video') : L(context, 'صوت', 'Voice')} · ${mmss((d['seconds'] as num?)?.toInt() ?? 0)} · $size ${L(context, 'ميجابايت', 'MB')}'),
          _Fact(L(context, 'الموافقة', 'Consent'), consent),
          _Fact(L(context, 'المريض', 'Patient'), d['patient'] is Map ? '${_str((d['patient'] as Map)['name'])} · ${_str((d['patient'] as Map)['mrn'])}' : '—'),
          if (d['deleteAfter'] != null) _Fact(L(context, 'يُحذف تلقائياً في', 'Deleted automatically on'), _str(d['deleteAfter']).split('T').first),
        ]),
        if (d['status'] != 'Deleted') ...[
          SectionHeading(L(context, 'ملخص المكالمة', 'Call summary')),
          _summaryCard(context),
        ],
        SectionHeading(L(context, 'من فتح التسجيل', 'Who opened it')),
        DataList(children: [
          for (final w in (d['views'] as List? ?? []).whereType<Map>())
            ItemRow(leading: const IconBox(icon: Lu.eye), title: _name(w['who']), subtitle: '${ymd(_t(w['at']) ?? DateTime.now())} ${_hm(_t(w['at']))}'),
          if ((d['views'] as List? ?? []).isEmpty) Padding(padding: const EdgeInsets.symmetric(vertical: 12), child: Text(L(context, 'لم يفتحه أحد بعد', 'Nobody has opened it yet'), style: T.small)),
        ]),
        Padding(padding: const EdgeInsets.only(top: 12), child: Text(L(context, 'يُسجَّل كل فتح للتسجيل. تشغيل فقط — لا تنزيل على الجوال.', 'Every opening is logged. Play only — no download on the phone.'), textAlign: TextAlign.center, style: T.small)),
      ]),
    );
  }
}

/// The app's four tabs under every calls screen, as in the prototype («مساحات العمل» chosen): a tap goes back to the
/// shell on that tab.
class CallsNav extends StatelessWidget {
  const CallsNav({super.key});
  @override
  Widget build(BuildContext context) => ExecNav(
        index: AppNav.apps,
        onTap: (i) {
          Navigator.of(context).popUntil((r) => r.isFirst);
          AppNav.tabIndex.value = i;
        },
        items: [
          (Lu.house, L(context, 'الرئيسية', 'Home')),
          (Lu.layers2, L(context, 'الموافقات', 'Approvals')),
          (Lu.grid2x2, L(context, 'مساحات العمل', 'Workspaces')),
          (Lu.circleUserRound, L(context, 'حسابي', 'Account')),
        ],
      );
}

/// A fact of the recording (the prototype's .data row): the label on one line, the value taking the rest and wrapping.
class _Fact extends StatelessWidget {
  final String k, v;
  const _Fact(this.k, this.v);
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 14),
        child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(k, style: const TextStyle(fontSize: 11, color: Brand.mutedC)),
          const SizedBox(width: 16),
          Expanded(child: Text(v, textAlign: TextAlign.end, style: const TextStyle(fontSize: 11, fontWeight: FontWeight.w500, color: Brand.inkC))),
        ]),
      );
}
