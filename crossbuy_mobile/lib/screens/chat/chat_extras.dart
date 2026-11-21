import 'dart:async';

import 'package:flutter/material.dart';

import '../../models/chat.dart';
import '../../services/api_service.dart';
import '../../services/chat_hub.dart';
import '../../theme/brand.dart';
import '../../widgets/ui.dart';
import '../calls/calls_screen.dart';

/// COMMUNICATION v2 IN THE APP (owner 2026-11-19: "finish everything, without stopping"): what the browser's chat does
/// beyond plain messages — a thread, a poll, a message sent later, forwarding, my status and do-not-disturb, channels,
/// saved messages, and «ما فاتك». Every sheet speaks to /api/chat/*, which asks the chat's own authority; nothing here
/// decides who may do what. Drawn in the app's own language (Brand, the reference's sheets and rows).

/// «عضو واحد · عضوان · ٣–١٠ أعضاء · ١١ عضواً» — the Arabic count agrees with its number.
String membersLabel(BuildContext context, int n) => L(context,
    n == 1 ? 'عضو واحد' : n == 2 ? 'عضوان' : (n >= 3 && n <= 10) ? '$n أعضاء' : '$n عضواً', n == 1 ? '1 member' : '$n members');

Future<R?> _sheet<R>(BuildContext context, Widget child, {double height = .78}) => showModalBottomSheet<R>(
      context: context,
      isScrollControlled: true,
      backgroundColor: Brand.page,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(22))),
      builder: (c) => SizedBox(
        height: MediaQuery.of(c).size.height * height,
        child: Padding(padding: EdgeInsets.only(bottom: MediaQuery.of(c).viewInsets.bottom), child: child),
      ),
    );

Widget _title(String t, {String? sub}) => Padding(
      padding: const EdgeInsets.fromLTRB(20, 18, 20, 10),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Text(t, style: T.h2),
        if (sub != null) Padding(padding: const EdgeInsets.only(top: 3), child: Text(sub, style: T.small)),
      ]),
    );

InputDecoration _field(String hint) => InputDecoration(
      hintText: hint, hintStyle: const TextStyle(fontSize: 12, color: Brand.mutedC), filled: true, fillColor: Brand.paper, isDense: true,
      contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      border: OutlineInputBorder(borderRadius: BorderRadius.circular(14), borderSide: const BorderSide(color: Brand.lineC)),
      enabledBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(14), borderSide: const BorderSide(color: Brand.lineC)),
      focusedBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(14), borderSide: const BorderSide(color: Brand.blue)),
    );

Widget _primary(String label, VoidCallback? onTap, {IconData? icon, bool busy = false}) => SizedBox(
      width: double.infinity,
      child: FilledButton.icon(
        style: FilledButton.styleFrom(backgroundColor: Brand.navy, padding: const EdgeInsets.symmetric(vertical: 13), shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14))),
        onPressed: busy ? null : onTap,
        icon: busy ? const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white)) : Icon(icon ?? Lu.check, size: 16),
        label: Text(label, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
      ),
    );

void _toast(BuildContext context, String text) =>
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(text), behavior: SnackBarBehavior.floating));

String _hm(DateTime d) => '${d.hour.toString().padLeft(2, '0')}:${d.minute.toString().padLeft(2, '0')}';
DateTime? _utc(Object? v) {
  if (v == null) return null;
  var s = '$v';
  if (!s.endsWith('Z') && !RegExp(r'[+-]\d\d:\d\d$').hasMatch(s)) s += 'Z';
  return DateTime.tryParse(s)?.toLocal();
}

// ================================================================================================ the thread
/// A message's replies, and a box to answer in the thread (the conversation's stream stays clean).
Future<void> showThreadSheet(BuildContext context, int conversationId, ChatMsg root) =>
    _sheet(context, _ThreadSheet(conversationId: conversationId, root: root), height: .85);

class _ThreadSheet extends StatefulWidget {
  final int conversationId;
  final ChatMsg root;
  const _ThreadSheet({required this.conversationId, required this.root});
  @override
  State<_ThreadSheet> createState() => _ThreadSheetState();
}

class _ThreadSheetState extends State<_ThreadSheet> {
  List<ChatMsg>? _rows;
  final _box = TextEditingController();
  bool _sending = false;

  @override
  void initState() {
    super.initState();
    _load();
    ChatHub.instance.events.addListener(_onEvent);
  }

  @override
  void dispose() {
    ChatHub.instance.events.removeListener(_onEvent);
    _box.dispose();
    super.dispose();
  }

  void _onEvent() {
    final e = ChatHub.instance.events.value;
    if (e != null && e.name == 'thread' && e.conversationId == widget.conversationId && (e.data['rootId'] as num?)?.toInt() == widget.root.id) _load();
  }

  Future<void> _load() async {
    final r = await ApiService.instance.chatThread(widget.conversationId, widget.root.id);
    if (mounted) setState(() => _rows = r.where((m) => m.id != widget.root.id).toList());
  }

  Future<void> _send() async {
    final t = _box.text.trim(); if (t.isEmpty || _sending) return;
    setState(() => _sending = true);
    final m = await ApiService.instance.chatThreadReply(widget.conversationId, widget.root.id, t);
    if (!mounted) return;
    setState(() { _sending = false; if (m != null) { _box.clear(); _rows = [...?_rows, m]; } });
    if (m == null) _toast(context, L(context, 'لم يُرسل الرد', 'The reply was not sent'));
  }

  Widget _msg(ChatMsg m, {bool root = false}) => Container(
        margin: const EdgeInsets.only(bottom: 8),
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(color: root ? Brand.soft : Brand.paper, borderRadius: BorderRadius.circular(14), border: Border.all(color: Brand.lineC)),
        child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
          PersonAvatar(photo: m.senderAvatar, name: m.senderName, size: 30),
          const SizedBox(width: 10),
          Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              Expanded(child: Text(m.senderName, style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: Brand.inkC))),
              Text(_hm(m.createdAt), style: T.small),
            ]),
            const SizedBox(height: 3),
            Text(m.deleted ? L(context, 'حُذفت هذه الرسالة', 'This message was deleted') : (m.text.isEmpty ? (m.attachmentType == 'audio' ? L(context, 'رسالة صوتية', 'Voice message') : m.attachmentType == 'image' ? L(context, 'صورة', 'Picture') : (m.attachmentName ?? '')) : m.text),
                style: const TextStyle(fontSize: 12, height: 1.6, color: Brand.inkC)),
          ])),
        ]),
      );

  @override
  Widget build(BuildContext context) => Column(children: [
        _title(L(context, 'السلسلة', 'Thread'), sub: L(context, 'الردود هنا لا تزدحم بها المحادثة', 'Replies here keep the conversation clear')),
        Expanded(
          child: ListView(padding: const EdgeInsets.symmetric(horizontal: 16), children: [
            _msg(widget.root, root: true),
            if (_rows == null) const Padding(padding: EdgeInsets.all(20), child: Center(child: CircularProgressIndicator()))
            else if (_rows!.isEmpty) Padding(padding: const EdgeInsets.all(16), child: Text(L(context, 'لا ردود بعد — ابدأ السلسلة', 'No replies yet — start the thread'), textAlign: TextAlign.center, style: T.sub))
            else ...[for (final m in _rows!) _msg(m)],
          ]),
        ),
        Padding(
          padding: const EdgeInsets.fromLTRB(12, 6, 12, 14),
          child: Row(children: [
            Expanded(child: TextField(controller: _box, minLines: 1, maxLines: 4, style: const TextStyle(fontSize: 12.5), decoration: _field(L(context, 'رد في السلسلة…', 'Reply in the thread…')))),
            const SizedBox(width: 8),
            IconButton.filled(style: IconButton.styleFrom(backgroundColor: Brand.navy), onPressed: _sending ? null : _send, icon: const Icon(Lu.sendHorizontal, size: 18)),
          ]),
        ),
      ]);

}

// ================================================================================================ the assistant
/// «المساعد»: a summary of the conversation, or a question about it — answered from it alone, on the company's own
/// servers; every answer names the messages it rests on, and nothing is made up.
Future<void> showAssistantSheet(BuildContext context, int conversationId, {void Function(int messageId)? onGoTo}) =>
    _sheet(context, _AssistantSheet(conversationId: conversationId, onGoTo: onGoTo), height: .85);

class _AssistantSheet extends StatefulWidget {
  final int conversationId;
  final void Function(int messageId)? onGoTo;
  const _AssistantSheet({required this.conversationId, this.onGoTo});
  @override
  State<_AssistantSheet> createState() => _AssistantSheetState();
}

class _AssistantSheetState extends State<_AssistantSheet> {
  final _q = TextEditingController();
  bool _busy = false;
  String? _mode, _error;
  Map<String, dynamic>? _result;
  Timer? _poll;

  @override
  void dispose() { _poll?.cancel(); _q.dispose(); super.dispose(); }

  Future<void> _run(String mode) async {
    final q = _q.text.trim();
    if (mode == 'ask' && q.isEmpty) return;
    setState(() { _busy = true; _mode = mode; _result = null; _error = null; });
    final job = await ApiService.instance.chatAssist(widget.conversationId, mode, question: mode == 'ask' ? q : null);
    if (!mounted) return;
    if (job == null) { setState(() { _busy = false; _error = L(context, 'المساعد غير متاح الآن — حاول لاحقاً', 'The assistant is not available right now — try again later'); }); return; }
    void tick() {
      _poll = Timer(const Duration(seconds: 3), () async {
        final s = await ApiService.instance.chatAssistStatus(job);
        if (!mounted) return;
        if (s == null || s['status'] == 'working') { tick(); return; }
        setState(() {
          _busy = false;
          if (s['status'] == 'done' && s['result'] is Map) { _result = (s['result'] as Map).cast<String, dynamic>(); }
          else { _error = L(context, 'المساعد غير متاح الآن — حاول لاحقاً', 'The assistant is not available right now — try again later'); }
        });
      });
    }
    tick();
  }

  List<String> _strs(dynamic x) => (x as List? ?? const []).map((e) => '$e').where((e) => e.trim().isNotEmpty).toList();

  Widget _sec(IconData icon, String title, List<Widget> rows) => rows.isEmpty
      ? const SizedBox.shrink()
      : Padding(
          padding: const EdgeInsets.only(top: 10),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [Icon(icon, size: 13, color: Brand.blue), const SizedBox(width: 5), Text(title, style: const TextStyle(fontSize: 11, fontWeight: FontWeight.w700, color: Brand.blue))]),
            const SizedBox(height: 4),
            ...rows,
          ]),
        );
  Widget _li(Widget child) => Padding(padding: const EdgeInsets.only(bottom: 3), child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [const Text('•  ', style: TextStyle(fontSize: 12)), Expanded(child: child)]));

  Widget _resultView(BuildContext context) {
    final r = _result!;
    final cut = r['cut'] == true ? Padding(padding: const EdgeInsets.only(top: 10), child: Text(L(context, 'قُرئ الجزء الأحدث فقط من محادثة طويلة.', 'Only the newest part of a long conversation was read.'), style: T.small)) : const SizedBox.shrink();
    if (_mode == 'ask') {
      if (r['found'] != true) return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [Text(L(context, 'لا توجد إجابة عن هذا في المحادثة.', 'There is no answer to this in the conversation.'), style: T.sub), cut]);
      final ids = (r['messageIds'] as List? ?? const []).map((e) => (e as num).toInt()).toList();
      return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Text('${r['answer'] ?? ''}', style: const TextStyle(fontSize: 13, height: 1.8, color: Brand.inkC)),
        const SizedBox(height: 8),
        Wrap(spacing: 6, runSpacing: 6, crossAxisAlignment: WrapCrossAlignment.center, children: [
          Text(L(context, 'من:', 'From:'), style: T.small),
          for (final id in ids)
            ActionChip(visualDensity: VisualDensity.compact, backgroundColor: Brand.soft, side: BorderSide.none, label: Text('#$id', style: const TextStyle(fontSize: 11, color: Brand.blue)),
                onPressed: () { Navigator.of(context).pop(); widget.onGoTo?.call(id); }),
        ]),
        cut,
      ]);
    }
    if (r['tooShort'] == true || ('${r['overview'] ?? ''}'.trim().isEmpty && _strs(r['points']).isEmpty)) {
      return Text(L(context, 'المكتوب هنا أقل من أن يُلخَّص.', 'Too little was written here to summarise.'), style: T.sub);
    }
    const body = TextStyle(fontSize: 12, height: 1.7, color: Brand.inkC);
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      Text('${r['overview'] ?? ''}', style: const TextStyle(fontSize: 13, height: 1.8, color: Brand.inkC)),
      _sec(Lu.list, L(context, 'أهم النقاط', 'Key points'), [for (final p in _strs(r['points'])) _li(Text(p, style: body))]),
      _sec(Lu.circleCheck, L(context, 'القرارات', 'Decisions'), [for (final p in _strs(r['decisions'])) _li(Text(p, style: body))]),
      _sec(Lu.listTodo, L(context, 'المهام', 'Tasks'), [
        for (final a in (r['actions'] as List? ?? const []).whereType<Map>())
          _li(Text.rich(TextSpan(children: [TextSpan(text: '${a['owner'] ?? ''}', style: const TextStyle(fontWeight: FontWeight.w700)), TextSpan(text: ' — ${a['task'] ?? ''}')]), style: body)),
      ]),
      cut,
    ]);
  }

  @override
  Widget build(BuildContext context) => Column(children: [
        _title(L(context, 'المساعد', 'Assistant'), sub: L(context, 'يُجاب من هذه المحادثة وحدها، على خوادم الشركة نفسها.', 'Answered from this conversation only, on the company’s own servers.')),
        Expanded(child: ListView(padding: const EdgeInsets.symmetric(horizontal: 16), children: [
          _primary(L(context, 'تلخيص المحادثة', 'Summarise the conversation'), _busy ? null : () => _run('summary'), icon: Lu.sparkles),
          const SizedBox(height: 10),
          Row(children: [
            Expanded(child: TextField(controller: _q, maxLength: 300, style: const TextStyle(fontSize: 12.5), textInputAction: TextInputAction.send, onSubmitted: (_) => _run('ask'),
                decoration: _field(L(context, 'اسأل عن هذه المحادثة…', 'Ask about this conversation…')).copyWith(counterText: ''))),
            const SizedBox(width: 8),
            OutlinedButton(onPressed: _busy ? null : () => _run('ask'), style: OutlinedButton.styleFrom(padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14), shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14))), child: Text(L(context, 'اسأل', 'Ask'))),
          ]),
          const SizedBox(height: 16),
          if (_busy)
            Row(children: [
              const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2)),
              const SizedBox(width: 10),
              Expanded(child: Text(_mode == 'ask' ? L(context, 'جارٍ قراءة المحادثة…', 'Reading the conversation…') : L(context, 'جارٍ تلخيص المحادثة… قد يستغرق ذلك بضع دقائق', 'Summarising the conversation… this can take a few minutes'), style: T.sub)),
            ])
          else if (_error != null) Text(_error!, style: T.sub)
          else if (_result != null) _resultView(context),
        ])),
      ]);
}

// ================================================================================================ a poll
Future<ChatMsg?> showPollComposer(BuildContext context, int conversationId) => _sheet<ChatMsg>(context, _PollSheet(conversationId: conversationId));

class _PollSheet extends StatefulWidget {
  final int conversationId;
  const _PollSheet({required this.conversationId});
  @override
  State<_PollSheet> createState() => _PollSheetState();
}

class _PollSheetState extends State<_PollSheet> {
  final _q = TextEditingController();
  final List<TextEditingController> _opts = [TextEditingController(), TextEditingController()];
  bool _multi = false, _anon = false, _busy = false;

  @override
  void dispose() { _q.dispose(); for (final o in _opts) { o.dispose(); } super.dispose(); }

  Future<void> _go() async {
    final q = _q.text.trim();
    final opts = _opts.map((o) => o.text.trim()).where((o) => o.isNotEmpty).toList();
    if (q.isEmpty || opts.length < 2) { _toast(context, L(context, 'سؤال وخياران على الأقل', 'A question and at least two options')); return; }
    setState(() => _busy = true);
    final m = await ApiService.instance.chatCreatePoll(widget.conversationId, q, opts, multi: _multi, anonymous: _anon);
    if (!mounted) return;
    setState(() => _busy = false);
    if (m == null) { _toast(context, L(context, 'تعذّر إرسال التصويت', 'The poll could not be sent')); return; }
    Navigator.of(context).pop(m);
  }

  @override
  Widget build(BuildContext context) => Column(children: [
        _title(L(context, 'تصويت', 'Poll')),
        Expanded(child: ListView(padding: const EdgeInsets.symmetric(horizontal: 16), children: [
          TextField(controller: _q, style: const TextStyle(fontSize: 13), decoration: _field(L(context, 'السؤال', 'Question'))),
          const SizedBox(height: 10),
          for (var i = 0; i < _opts.length; i++)
            Padding(padding: const EdgeInsets.only(bottom: 8), child: TextField(controller: _opts[i], style: const TextStyle(fontSize: 12.5), decoration: _field(L(context, 'الخيار ${i + 1}', 'Option ${i + 1}')))),
          if (_opts.length < 12)
            Align(alignment: AlignmentDirectional.centerStart, child: TextButton.icon(onPressed: () => setState(() => _opts.add(TextEditingController())), icon: const Icon(Lu.plus, size: 16), label: Text(L(context, 'خيار آخر', 'Another option')))),
          SwitchListTile(contentPadding: EdgeInsets.zero, value: _multi, onChanged: (v) => setState(() => _multi = v), title: Text(L(context, 'أكثر من إجابة', 'More than one answer'), style: const TextStyle(fontSize: 12.5))),
          SwitchListTile(contentPadding: EdgeInsets.zero, value: _anon, onChanged: (v) => setState(() => _anon = v), title: Text(L(context, 'مجهول — لا يرى أحد من صوّت لماذا', 'Anonymous — nobody sees who voted for what'), style: const TextStyle(fontSize: 12.5))),
        ])),
        Padding(padding: const EdgeInsets.fromLTRB(16, 6, 16, 16), child: _primary(L(context, 'إرسال التصويت', 'Send the poll'), _go, icon: Lu.chartBar, busy: _busy)),
      ]);
}

// ================================================================================================ send it later
Future<bool?> showScheduleComposer(BuildContext context, int conversationId, String text) =>
    _sheet<bool>(context, _ScheduleSheet(conversationId: conversationId, text: text), height: .62);

class _ScheduleSheet extends StatefulWidget {
  final int conversationId;
  final String text;
  const _ScheduleSheet({required this.conversationId, required this.text});
  @override
  State<_ScheduleSheet> createState() => _ScheduleSheetState();
}

class _ScheduleSheetState extends State<_ScheduleSheet> {
  late final _body = TextEditingController(text: widget.text);
  DateTime _at = DateTime.now().add(const Duration(hours: 1));
  bool _busy = false;
  List<Map<String, dynamic>>? _list;

  @override
  void initState() { super.initState(); _loadList(); }
  @override
  void dispose() { _body.dispose(); super.dispose(); }

  Future<void> _loadList() async {
    final l = await ApiService.instance.chatScheduled(widget.conversationId);
    if (mounted) setState(() => _list = l);
  }

  Future<void> _pick() async {
    final d = await showDatePicker(context: context, initialDate: _at, firstDate: DateTime.now(), lastDate: DateTime.now().add(const Duration(days: 60)));
    if (d == null || !mounted) return;
    final t = await showTimePicker(context: context, initialTime: TimeOfDay.fromDateTime(_at));
    if (t == null) return;
    setState(() => _at = DateTime(d.year, d.month, d.day, t.hour, t.minute));
  }

  Future<void> _go() async {
    final b = _body.text.trim(); if (b.isEmpty) return;
    if (_at.isBefore(DateTime.now().add(const Duration(minutes: 1)))) { _toast(context, L(context, 'اختر وقتاً بعد دقيقة على الأقل', 'Choose a time at least a minute from now')); return; }
    setState(() => _busy = true);
    final ok = await ApiService.instance.chatSchedule(widget.conversationId, b, _at);
    if (!mounted) return;
    setState(() => _busy = false);
    if (!ok) { _toast(context, L(context, 'تعذّرت الجدولة', 'Could not schedule it')); return; }
    Navigator.of(context).pop(true);
  }

  @override
  Widget build(BuildContext context) => Column(children: [
        _title(L(context, 'جدولة رسالة', 'Schedule a message'), sub: L(context, 'تُرسل وحدها في الوقت الذي تختاره', 'It is sent on its own at the time you choose')),
        Expanded(child: ListView(padding: const EdgeInsets.symmetric(horizontal: 16), children: [
          TextField(controller: _body, minLines: 2, maxLines: 5, style: const TextStyle(fontSize: 13), decoration: _field(L(context, 'الرسالة', 'The message'))),
          const SizedBox(height: 10),
          InkWell(
            onTap: _pick,
            borderRadius: BorderRadius.circular(14),
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 13),
              decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(14), border: Border.all(color: Brand.lineC)),
              child: Row(children: [
                const Icon(Lu.clock, size: 16, color: Brand.blue), const SizedBox(width: 8),
                Expanded(child: Text('${dayMonth(context, _at)} · ${_hm(_at)}', style: const TextStyle(fontSize: 13, color: Brand.inkC))),
                Text(L(context, 'تغيير', 'Change'), style: const TextStyle(fontSize: 11.5, color: Brand.blue)),
              ]),
            ),
          ),
          if ((_list ?? const []).isNotEmpty) ...[
            const SizedBox(height: 14),
            Text(L(context, 'المجدولة في هذه المحادثة', 'Scheduled in this conversation'), style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: Brand.blue)),
            for (final s in _list!)
              ListTile(
                contentPadding: EdgeInsets.zero, dense: true,
                leading: const Icon(Lu.clock, size: 16, color: Brand.mutedC),
                title: Text('${s['body'] ?? ''}', maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12)),
                subtitle: Text(_utc(s['sendAt']) == null ? '' : '${dayMonth(context, _utc(s['sendAt'])!)} · ${_hm(_utc(s['sendAt'])!)}', style: T.small),
                trailing: IconButton(icon: const Icon(Lu.x, size: 16, color: Brand.badC), onPressed: () async {
                  if (await ApiService.instance.chatCancelScheduled((s['id'] as num).toInt())) _loadList();
                }),
              ),
          ],
        ])),
        Padding(padding: const EdgeInsets.fromLTRB(16, 6, 16, 16), child: _primary(L(context, 'جدولة', 'Schedule'), _go, icon: Lu.clock, busy: _busy)),
      ]);
}

// ================================================================================================ forward
Future<void> showForwardPicker(BuildContext context, ChatMsg m) => _sheet(context, _ForwardSheet(message: m));

class _ForwardSheet extends StatefulWidget {
  final ChatMsg message;
  const _ForwardSheet({required this.message});
  @override
  State<_ForwardSheet> createState() => _ForwardSheetState();
}

class _ForwardSheetState extends State<_ForwardSheet> {
  List<ChatConv>? _convs;
  final Set<int> _to = {};
  String _q = '';
  bool _busy = false;

  @override
  void initState() { super.initState(); ApiService.instance.getChats().then((c) { if (mounted) setState(() => _convs = c); }).catchError((_) {}); }

  Future<void> _go() async {
    if (_to.isEmpty) return;
    setState(() => _busy = true);
    final n = await ApiService.instance.chatForward(widget.message.id, _to.toList());
    if (!mounted) return;
    Navigator.of(context).pop();
    _toast(context, n > 0 ? L(context, 'حُوّلت إلى $n', 'Forwarded to $n') : L(context, 'تعذّر التحويل', 'Could not forward'));
  }

  @override
  Widget build(BuildContext context) {
    final list = (_convs ?? const <ChatConv>[]).where((c) => _q.isEmpty || c.title.toLowerCase().contains(_q.toLowerCase())).toList();
    return Column(children: [
      _title(L(context, 'تحويل', 'Forward'), sub: L(context, 'اختر محادثة أو أكثر', 'Choose one or more conversations')),
      Padding(padding: const EdgeInsets.symmetric(horizontal: 16), child: TextField(onChanged: (v) => setState(() => _q = v.trim()), style: const TextStyle(fontSize: 12.5), decoration: _field(L(context, 'ابحث', 'Search')))),
      Expanded(
        child: _convs == null
            ? const Center(child: CircularProgressIndicator())
            : ListView(padding: const EdgeInsets.symmetric(horizontal: 8), children: [
                for (final c in list)
                  CheckboxListTile(
                    value: _to.contains(c.id), activeColor: Brand.navy,
                    onChanged: (v) => setState(() => v == true ? _to.add(c.id) : _to.remove(c.id)),
                    secondary: PersonAvatar(photo: c.avatar, name: c.title, size: 34),
                    title: Text(c.title, style: const TextStyle(fontSize: 12.5)),
                  ),
              ]),
      ),
      Padding(padding: const EdgeInsets.fromLTRB(16, 6, 16, 16), child: _primary(L(context, 'تحويل', 'Forward') + (_to.isEmpty ? '' : ' (${_to.length})'), _to.isEmpty ? null : _go, icon: Lu.forward, busy: _busy)),
    ]);
  }
}

// ================================================================================================ my status
Future<bool?> showStatusSheet(BuildContext context) => _sheet<bool>(context, const _StatusSheet(), height: .8);

class _StatusSheet extends StatefulWidget {
  const _StatusSheet();
  @override
  State<_StatusSheet> createState() => _StatusSheetState();
}

class _StatusSheetState extends State<_StatusSheet> {
  final _text = TextEditingController();
  String _emoji = '💬';
  int _clear = 0;          // 0 never · 1 hour · 2 today · 3 this week
  bool _dnd = false;
  int _dndFor = 1;         // 0 1h · 1 until tomorrow · 2 until I turn it off
  bool _busy = false;

  static const _presets = [('📅', 'في اجتماع', 'In a meeting'), ('🚗', 'في الطريق', 'On the way'), ('🏠', 'أعمل من المنزل', 'Working from home'), ('🤒', 'في إجازة مرضية', 'Off sick'), ('🌴', 'في إجازة', 'On leave'), ('🎯', 'تركيز', 'Focusing')];

  @override
  void initState() {
    super.initState();
    ApiService.instance.chatStatuses(const []).then((d) {
      if (!mounted || d == null) return;
      final me = (d['me'] as num?)?.toInt();
      final s = (d['statuses'] as Map?)?['$me'];
      if (s is Map) setState(() { _emoji = '${s['emoji'] ?? _emoji}'; _text.text = '${s['text'] ?? ''}'; _dnd = s['dnd'] == true; });
    });
  }

  @override
  void dispose() { _text.dispose(); super.dispose(); }

  DateTime? _until(int k) {
    final now = DateTime.now();
    return switch (k) { 1 => now.add(const Duration(hours: 1)), 2 => DateTime(now.year, now.month, now.day, 23, 59), 3 => now.add(const Duration(days: 7)), _ => null };
  }

  Future<void> _save({bool clear = false}) async {
    setState(() => _busy = true);
    final now = DateTime.now();
    final ok = clear
        ? await ApiService.instance.chatSetStatus()
        : await ApiService.instance.chatSetStatus(
            emoji: _text.text.trim().isEmpty ? null : _emoji, text: _text.text.trim().isEmpty ? null : _text.text.trim(), until: _until(_clear),
            dnd: _dnd, dndUntil: !_dnd ? null : _dndFor == 0 ? now.add(const Duration(hours: 1)) : _dndFor == 1 ? DateTime(now.year, now.month, now.day + 1, 8) : null);
    if (!mounted) return;
    setState(() => _busy = false);
    if (!ok) { _toast(context, L(context, 'تعذّر الحفظ', 'Could not save')); return; }
    Navigator.of(context).pop(true);
  }

  Widget _chips(List<String> items, int index, ValueChanged<int> on) => Wrap(spacing: 6, runSpacing: 6, children: [
        for (var i = 0; i < items.length; i++)
          ChoiceChip(label: Text(items[i], style: TextStyle(fontSize: 11, color: i == index ? Colors.white : Brand.inkC)), selected: i == index, selectedColor: Brand.navy, backgroundColor: Brand.paper,
              side: const BorderSide(color: Brand.lineC), showCheckmark: false, onSelected: (_) => on(i)),
      ]);

  @override
  Widget build(BuildContext context) => Column(children: [
        _title(L(context, 'حالتك', 'Your status'), sub: L(context, 'يراها زملاؤك بجانب اسمك', 'Colleagues see it next to your name')),
        Expanded(child: ListView(padding: const EdgeInsets.symmetric(horizontal: 16), children: [
          Row(children: [
            Container(width: 46, height: 46, alignment: Alignment.center, decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(14), border: Border.all(color: Brand.lineC)), child: Text(_emoji, style: const TextStyle(fontSize: 20))),
            const SizedBox(width: 8),
            Expanded(child: TextField(controller: _text, style: const TextStyle(fontSize: 13), decoration: _field(L(context, 'ما حالتك؟', 'What is your status?')))),
          ]),
          const SizedBox(height: 10),
          Wrap(spacing: 6, runSpacing: 6, children: [
            for (final p in _presets)
              ActionChip(backgroundColor: Brand.paper, side: const BorderSide(color: Brand.lineC), label: Text('${p.$1}  ${L(context, p.$2, p.$3)}', style: const TextStyle(fontSize: 11)),
                  onPressed: () => setState(() { _emoji = p.$1; _text.text = L(context, p.$2, p.$3); })),
          ]),
          const SizedBox(height: 14),
          Text(L(context, 'تُمسح بعد', 'Clear after'), style: T.label),
          const SizedBox(height: 6),
          _chips([L(context, 'أبداً', 'Never'), L(context, 'ساعة', 'An hour'), L(context, 'اليوم', 'Today'), L(context, 'هذا الأسبوع', 'This week')], _clear, (i) => setState(() => _clear = i)),
          const SizedBox(height: 10),
          SwitchListTile(contentPadding: EdgeInsets.zero, value: _dnd, activeThumbColor: Brand.navy, onChanged: (v) => setState(() => _dnd = v),
              title: Text(L(context, 'عدم الإزعاج', 'Do not disturb'), style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
              subtitle: Text(L(context, 'لا رنين ولا تنبيهات؛ يُبلَّغ المتصل بذلك', 'No ringing, no notices; a caller is told'), style: T.small)),
          if (_dnd) _chips([L(context, 'ساعة', 'An hour'), L(context, 'حتى الغد', 'Until tomorrow'), L(context, 'حتى أوقفه', 'Until I turn it off')], _dndFor, (i) => setState(() => _dndFor = i)),
        ])),
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 6, 16, 16),
          child: Row(children: [
            Expanded(child: OutlinedButton(onPressed: _busy ? null : () => _save(clear: true), style: OutlinedButton.styleFrom(padding: const EdgeInsets.symmetric(vertical: 13), shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14))), child: Text(L(context, 'مسح', 'Clear')))),
            const SizedBox(width: 10),
            Expanded(flex: 2, child: _primary(L(context, 'حفظ', 'Save'), _save, busy: _busy)),
          ]),
        ),
      ]);
}

// ================================================================================================ channels
class ChannelsScreen extends StatefulWidget {
  final void Function(int id, String title) onOpen;
  const ChannelsScreen({super.key, required this.onOpen});
  @override
  State<ChannelsScreen> createState() => _ChannelsScreenState();
}

class _ChannelsScreenState extends State<ChannelsScreen> {
  List<Map<String, dynamic>>? _rows;
  Timer? _debounce;
  String _q = '';

  @override
  void initState() { super.initState(); _load(); }
  @override
  void dispose() { _debounce?.cancel(); super.dispose(); }

  Future<void> _load() async {
    final r = await ApiService.instance.chatChannels(q: _q);
    if (mounted) setState(() => _rows = r);
  }

  Future<void> _create() async {
    final title = TextEditingController(), desc = TextEditingController();
    final ok = await showDialog<bool>(context: context, builder: (c) => AlertDialog(
          title: Text(L(context, 'قناة جديدة', 'New channel'), style: T.h2),
          content: Column(mainAxisSize: MainAxisSize.min, children: [
            TextField(controller: title, decoration: _field(L(context, 'مثلاً: فريق-المبيعات', 'e.g. sales-team'))),
            const SizedBox(height: 8),
            TextField(controller: desc, decoration: _field(L(context, 'الوصف (اختياري)', 'Description (optional)'))),
            const SizedBox(height: 8),
            Text(L(context, 'القناة عامة: يجدها أي موظف في الشركة وينضم إليها', 'A channel is public: anyone in the company can find it and join'), style: T.small),
          ]),
          actions: [
            TextButton(onPressed: () => Navigator.pop(c, false), child: Text(L(context, 'إلغاء', 'Cancel'))),
            FilledButton(style: FilledButton.styleFrom(backgroundColor: Brand.navy), onPressed: () => Navigator.pop(c, true), child: Text(L(context, 'إنشاء', 'Create'))),
          ]));
    if (ok != true || title.text.trim().isEmpty) return;
    final id = await ApiService.instance.chatCreateChannel(title.text.trim(), description: desc.text.trim());
    if (!mounted) return;
    if (id == null) { _toast(context, L(context, 'تعذّر إنشاء القناة', 'The channel could not be created')); return; }
    widget.onOpen(id, title.text.trim());
  }

  @override
  Widget build(BuildContext context) => ExecFrame(
        bottomBar: const CallsNav(),
        child: ExecBody(onRefresh: _load, children: [
          BackLink(L(context, 'المحادثات', 'Chats')),
          PageHead(L(context, 'القنوات', 'Channels'), eyebrow: L(context, 'عامة داخل الشركة', 'Public inside the company')),
          TextField(
            onChanged: (v) { _q = v.trim(); _debounce?.cancel(); _debounce = Timer(const Duration(milliseconds: 300), _load); },
            style: const TextStyle(fontSize: 12.5), decoration: _field(L(context, 'ابحث عن قناة', 'Find a channel'))),
          const SizedBox(height: 14),
          if (_rows == null) const Padding(padding: EdgeInsets.all(30), child: Center(child: CircularProgressIndicator()))
          else if (_rows!.isEmpty) EmptyNote(icon: Lu.hash, title: L(context, 'لا قنوات بعد', 'No channels yet'), text: L(context, 'أنشئ أول قناة', 'Create the first one'))
          else Container(
            decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(18), border: Border.all(color: Brand.lineC)),
            child: Column(children: [
              for (final r in _rows!)
                ListTile(
                  leading: Container(width: 40, height: 40, alignment: Alignment.center, decoration: const BoxDecoration(color: Brand.soft, shape: BoxShape.circle), child: const Text('#', style: TextStyle(fontWeight: FontWeight.w700, color: Brand.blue))),
                  title: Text('${r['title'] ?? ''}', style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
                  subtitle: Text([if ((r['description'] ?? '').toString().isNotEmpty) '${r['description']}', membersLabel(context, (r['members'] as num?)?.toInt() ?? 0)].join(' · '), maxLines: 2, overflow: TextOverflow.ellipsis, style: T.small),
                  trailing: r['joined'] == true
                      ? TextButton(onPressed: () => widget.onOpen((r['id'] as num).toInt(), '${r['title']}'), child: Text(L(context, 'فتح', 'Open')))
                      : FilledButton(style: FilledButton.styleFrom(backgroundColor: Brand.navy, visualDensity: VisualDensity.compact), onPressed: () async {
                          if (await ApiService.instance.chatJoinChannel((r['id'] as num).toInt())) widget.onOpen((r['id'] as num).toInt(), '${r['title']}');
                        }, child: Text(L(context, 'انضمام', 'Join'))),
                ),
            ]),
          ),
          const SizedBox(height: 14),
          _primary(L(context, 'قناة جديدة', 'New channel'), _create, icon: Lu.plus),
        ]),
      );
}

// ================================================================================================ saved messages
class SavedScreen extends StatefulWidget {
  final void Function(int conversationId, String title, int messageId) onOpen;
  const SavedScreen({super.key, required this.onOpen});
  @override
  State<SavedScreen> createState() => _SavedScreenState();
}

class _SavedScreenState extends State<SavedScreen> {
  Map<String, dynamic>? _d;
  @override
  void initState() { super.initState(); _load(); }
  Future<void> _load() async { final d = await ApiService.instance.chatSaved(); if (mounted) setState(() => _d = d ?? const {}); }

  Widget _row(Map r, {bool reminder = false}) {
    final at = _utc(reminder ? r['remindAt'] : r['at']);
    return ListTile(
      onTap: () => widget.onOpen((r['conversationId'] as num).toInt(), '${r['conversation'] ?? ''}', (r['id'] as num).toInt()),
      leading: PersonAvatar(photo: r['avatar'] as String?, name: '${r['sender'] ?? ''}', size: 38),
      title: Text('${r['body'] ?? L(context, 'حُذفت هذه الرسالة', 'This message was deleted')}', maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12.5)),
      subtitle: Text([r['sender'], r['conversation'], if (at != null) '${reminder ? '⏰ ' : ''}${dayMonth(context, at)} ${_hm(at)}'].where((x) => x != null && '$x'.isNotEmpty).join(' · '), style: T.small),
      trailing: reminder ? const Icon(Lu.bellRing, size: 16, color: Brand.blue) : IconButton(icon: const Icon(Lu.bookmarkMinus, size: 18, color: Brand.mutedC), onPressed: () async {
        if (await ApiService.instance.chatSave((r['id'] as num).toInt(), false)) _load();
      }),
    );
  }

  @override
  Widget build(BuildContext context) {
    final saved = ((_d?['saved'] as List?) ?? const []).whereType<Map>().toList();
    final rem = ((_d?['reminders'] as List?) ?? const []).whereType<Map>().toList();
    return ExecFrame(
      bottomBar: const CallsNav(),
      child: ExecBody(onRefresh: _load, children: [
        BackLink(L(context, 'المحادثات', 'Chats')),
        PageHead(L(context, 'المحفوظات والتذكيرات', 'Saved and reminders'), eyebrow: L(context, 'لك وحدك', 'Only you see this')),
        if (_d == null) const Padding(padding: EdgeInsets.all(30), child: Center(child: CircularProgressIndicator()))
        else if (saved.isEmpty && rem.isEmpty) EmptyNote(icon: Lu.bookmark, title: L(context, 'لا شيء محفوظ بعد', 'Nothing saved yet'), text: L(context, 'اضغط مطولاً على رسالة ثم «حفظ»', 'Long-press a message, then “Save”'))
        else ...[
          if (rem.isNotEmpty) ...[SectionHeading(L(context, 'التذكيرات', 'Reminders')), Container(decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(18), border: Border.all(color: Brand.lineC)), child: Column(children: [for (final r in rem) _row(r, reminder: true)]))],
          if (saved.isNotEmpty) ...[SectionHeading(L(context, 'الرسائل المحفوظة', 'Saved messages')), Container(decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(18), border: Border.all(color: Brand.lineC)), child: Column(children: [for (final r in saved) _row(r)]))],
        ],
      ]),
    );
  }
}

// ================================================================================================ what I missed
/// «ما فاتك»: mentions of me, questions, decisions, files and links among the messages I had not read — gathered by
/// the server without a language model.
Future<void> showCatchUp(BuildContext context, Map<String, dynamic> d) => _sheet(context, _CatchUp(d: d), height: .7);

class _CatchUp extends StatelessWidget {
  final Map<String, dynamic> d;
  const _CatchUp({required this.d});
  @override
  Widget build(BuildContext context) {
    List<Map> rows(String k) => ((d[k] as List?) ?? const []).whereType<Map>().toList();
    Widget sec(IconData i, String t, String k) {
      final r = rows(k);
      if (r.isEmpty) return const SizedBox.shrink();
      return Padding(
        padding: const EdgeInsets.only(bottom: 12),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Row(children: [Icon(i, size: 14, color: Brand.blue), const SizedBox(width: 6), Text(t, style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: Brand.blue))]),
          const SizedBox(height: 4),
          for (final m in r) Padding(padding: const EdgeInsets.only(bottom: 4), child: Text.rich(TextSpan(children: [TextSpan(text: '${m['sender'] ?? ''}: ', style: const TextStyle(fontWeight: FontWeight.w700)), TextSpan(text: '${m['body'] ?? ''}')]), style: const TextStyle(fontSize: 12, height: 1.6, color: Brand.inkC))),
        ]),
      );
    }
    final people = ((d['people'] as List?) ?? const []).whereType<Map>().map((p) => '${p['name']} (${p['count']})').join('، ');
    return Column(children: [
      _title(L(context, 'ما فاتك', 'What you missed'), sub: L(context, '${d['total'] ?? 0} رسالة غير مقروءة · $people', '${d['total'] ?? 0} unread messages · $people')),
      Expanded(child: ListView(padding: const EdgeInsets.symmetric(horizontal: 18), children: [
        sec(Lu.atSign, L(context, 'إشارات إليك', 'Mentions of you'), 'mentions'),
        sec(Lu.circleHelp, L(context, 'أسئلة', 'Questions'), 'questions'),
        sec(Lu.circleCheck, L(context, 'قرارات', 'Decisions'), 'decisions'),
        sec(Lu.paperclip, L(context, 'ملفات', 'Files'), 'files'),
        sec(Lu.link, L(context, 'روابط', 'Links'), 'links'),
      ])),
    ]);
  }
}
