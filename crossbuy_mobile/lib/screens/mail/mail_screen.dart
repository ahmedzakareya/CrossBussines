import 'dart:async';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../services/mail_api.dart';
import '../../theme/brand.dart';
import '../../widgets/ui.dart';
import '../calls/calls_screen.dart';
import '../chat/chats_screen.dart';

// =====================================================================================================================
// البريد — exactly the approved prototype (owner 2026-11-24 «تمام موافق»; design-output/executive-prototype,
// ?page=mail · mail-read · mail-compose): the inbox with its four tabs and the person's own address for mail from
// outside; a message with the assistant (summary · a suggested reply · translation), the earlier messages of its
// conversation and its files; the reply box, where a suggested reply arrives as a draft to edit.
// =====================================================================================================================

String _when(BuildContext context, DateTime? d) {
  if (d == null) return '';
  final now = DateTime.now();
  final day = DateTime(d.year, d.month, d.day), today = DateTime(now.year, now.month, now.day);
  if (day == today) return '${d.hour.toString().padLeft(2, '0')}:${d.minute.toString().padLeft(2, '0')}';
  if (today.difference(day).inDays == 1) return L(context, 'أمس', 'Yesterday');
  return '${d.day}/${d.month}';
}

Widget _tag(String text, {bool solid = false}) => Container(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 1),
      decoration: BoxDecoration(color: solid ? Brand.navy : Brand.soft, borderRadius: BorderRadius.circular(5)),
      child: Text(text, style: TextStyle(fontSize: 8.5, fontWeight: FontWeight.w700, color: solid ? Colors.white : Brand.blue)),
    );

Widget _card(List<Widget> rows) => Container(
      padding: const EdgeInsets.symmetric(horizontal: 14),
      decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(18), border: Border.all(color: Brand.lineC)),
      child: Column(children: [for (var i = 0; i < rows.length; i++) ...[if (i > 0) const Divider(height: 1, color: Brand.lineC), rows[i]]]),
    );

class _Btn extends StatelessWidget {
  final IconData icon;
  final String label;
  final bool primary;
  final VoidCallback? onTap;
  const _Btn(this.icon, this.label, {this.primary = false, required this.onTap});
  @override
  Widget build(BuildContext context) => Material(
        color: primary ? Brand.navy : Brand.paper,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14), side: BorderSide(color: primary ? Brand.navy : Brand.lineC)),
        child: InkWell(
          borderRadius: BorderRadius.circular(14),
          onTap: onTap,
          child: Padding(
            padding: const EdgeInsets.symmetric(vertical: 12),
            child: Row(mainAxisAlignment: MainAxisAlignment.center, children: [
              Icon(icon, size: 15, color: primary ? Colors.white : Brand.navy),
              const SizedBox(width: 6),
              Flexible(child: Text(label, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600, color: primary ? Colors.white : Brand.navy))),
            ]),
          ),
        ),
      );
}

// ------------------------------------------------------------------------------------------------ the inbox
class MailScreen extends StatefulWidget {
  const MailScreen({super.key});
  @override
  State<MailScreen> createState() => _MailScreenState();
}

class _MailScreenState extends State<MailScreen> with WidgetsBindingObserver {
  static const _folders = ['Inbox', 'Sent', 'Starred', 'Trash'];
  int _tab = 0;
  String _q = '';
  List<MailRow>? _rows;
  bool _failed = false;
  int _unread = 0;
  String? _address;
  Timer? _poll, _debounce;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _load();
    // live enough: new mail shows within half a minute while the list is open, and at once on returning to it
    _poll = Timer.periodic(const Duration(seconds: 30), (_) => _load(quiet: true));
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState s) { if (s == AppLifecycleState.resumed) _load(quiet: true); }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _poll?.cancel(); _debounce?.cancel();
    super.dispose();
  }

  Future<void> _load({bool quiet = false}) async {
    try {
      final c = await MailApi.instance.counts();
      final rows = await MailApi.instance.list(_folders[_tab], q: _q);
      if (!mounted) return;
      setState(() { _rows = rows; _failed = false; _unread = (c?['unread'] as num?)?.toInt() ?? 0; _address = c?['address'] as String?; });
    } catch (_) {
      if (mounted && !quiet) setState(() => _failed = true);
    }
  }

  Future<void> _open(MailRow r) async {
    await Navigator.of(context).push(MaterialPageRoute(builder: (_) => MailReadScreen(id: r.id)));
    _load(quiet: true);
  }

  Future<void> _new() async {
    await Navigator.of(context).push(MaterialPageRoute(builder: (_) => const MailComposeScreen()));
    _load(quiet: true);
  }

  @override
  Widget build(BuildContext context) {
    final rows = _rows ?? const <MailRow>[];
    return ExecFrame(
      bottomBar: const CallsNav(),
      child: ExecBody(onRefresh: _load, children: [
        BackLink(L(context, 'مساحات العمل', 'Workspaces')),
        PageHead(L(context, 'البريد', 'Mail'), eyebrow: L(context, 'بريدك أنت · داخل الشركة وخارجها', 'Your own mail · inside and outside the company')),
        Container(
          margin: const EdgeInsets.only(bottom: 14),
          padding: const EdgeInsets.symmetric(horizontal: 14),
          decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(14), border: Border.all(color: Brand.lineC)),
          child: Row(children: [
            const Icon(Lu.search, size: 16, color: Brand.mutedC),
            const SizedBox(width: 8),
            Expanded(child: TextField(
              onChanged: (v) { _debounce?.cancel(); _debounce = Timer(const Duration(milliseconds: 400), () { _q = v.trim(); _load(); }); },
              style: const TextStyle(fontSize: 12),
              decoration: InputDecoration(hintText: L(context, 'ابحث في الموضوع والنص والمرسل', 'Search subject, text and sender'), hintStyle: const TextStyle(fontSize: 11.5, color: Brand.mutedC),
                  border: InputBorder.none, enabledBorder: InputBorder.none, focusedBorder: InputBorder.none, filled: false, isDense: true, contentPadding: const EdgeInsets.symmetric(vertical: 13)),
            )),
          ]),
        ),
        ChipTabs(
          items: [_unread > 0 ? L(context, 'الوارد · $_unread', 'Inbox · $_unread') : L(context, 'الوارد', 'Inbox'), L(context, 'المرسل', 'Sent'), L(context, 'المميزة', 'Starred'), L(context, 'المهملات', 'Trash')],
          index: _tab,
          onTap: (i) { setState(() { _tab = i; _rows = null; }); _load(); },
        ),
        if (_failed)
          EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر التحميل', 'Could not load'), text: L(context, 'اسحب للأسفل لإعادة المحاولة.', 'Pull down to try again.'))
        else if (_rows == null)
          const Padding(padding: EdgeInsets.all(30), child: Center(child: CircularProgressIndicator()))
        else if (rows.isEmpty)
          EmptyNote(icon: Lu.inbox, title: _tab == 0 ? L(context, 'صندوق الوارد فارغ', 'Your inbox is empty') : L(context, 'لا رسائل هنا', 'No messages here'),
              text: L(context, 'تصلك هنا رسائل زملائك ومن يراسلك من خارج الشركة.', 'Mail from colleagues and from outside arrives here.'))
        else
          _card([for (final r in rows) _row(context, r)]),
        if (_address != null) _addressCard(context, _address!),
        const SizedBox(height: 4),
        Row(children: [
          Expanded(child: _Btn(Lu.squarePen, L(context, 'رسالة جديدة', 'New message'), primary: true, onTap: _new)),
          const SizedBox(width: 10),
          Expanded(child: _Btn(Lu.messageCircle, L(context, 'المحادثات', 'Chats'), onTap: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => const ChatsScreen())))),
        ]),
      ]),
    );
  }

  Widget _row(BuildContext context, MailRow r) {
    final bold = r.unread ? FontWeight.w700 : FontWeight.w600;
    return InkWell(
      onTap: () => _open(r),
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 11),
        child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
          PersonAvatar(photo: r.photo, name: r.name, size: 40),
          const SizedBox(width: 10),
          Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              Expanded(child: Text(r.name, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12, fontWeight: bold, color: Brand.inkC))),
              if (r.external) Padding(padding: const EdgeInsetsDirectional.only(end: 6), child: _tag(L(context, 'خارجي', 'External'))),
              Text(_when(context, r.at), style: TextStyle(fontSize: 9.5, color: r.unread ? Brand.blue : Brand.mutedC, fontWeight: r.unread ? FontWeight.w700 : FontWeight.w400)),
            ]),
            const SizedBox(height: 2),
            Row(children: [
              if (r.unread) Container(width: 7, height: 7, margin: const EdgeInsetsDirectional.only(end: 6), decoration: const BoxDecoration(color: Brand.blue, shape: BoxShape.circle)),
              Expanded(child: Text(r.subject, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 11, fontWeight: r.unread ? FontWeight.w700 : FontWeight.w400, color: Brand.inkC))),
              if (r.hasAttachment && !r.invite) const Icon(Lu.paperclip, size: 12, color: Brand.mutedC),
              if (r.invite) _tag(L(context, 'دعوة', 'Invite'), solid: true),
            ]),
            if ((r.preview ?? '').isNotEmpty) ...[
              const SizedBox(height: 2),
              Text(r.preview!, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 10, color: Brand.mutedC)),
            ],
          ])),
        ]),
      ),
    );
  }

  Widget _addressCard(BuildContext context, String address) => Container(
        margin: const EdgeInsets.symmetric(vertical: 12),
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
        decoration: BoxDecoration(color: const Color(0xFFF4F7FD), borderRadius: BorderRadius.circular(14), border: Border.all(color: Brand.blue.withValues(alpha: .5))),
        child: Row(children: [
          Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(L(context, 'عنوانك لاستقبال البريد من خارج الشركة', 'Your address for mail from outside'), style: const TextStyle(fontSize: 10.5, fontWeight: FontWeight.w600, color: Brand.inkC)),
            const SizedBox(height: 2),
            Directionality(textDirection: TextDirection.ltr, child: Text(address, style: const TextStyle(fontSize: 9.5, color: Brand.blue))),
          ])),
          const SizedBox(width: 8),
          InkWell(
            borderRadius: BorderRadius.circular(10),
            onTap: () async {
              await Clipboard.setData(ClipboardData(text: address));
              if (context.mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(L(context, 'تم النسخ', 'Copied'))));
            },
            child: Container(width: 32, height: 32, alignment: Alignment.center,
                decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(10), border: Border.all(color: Brand.lineC)),
                child: const Icon(Lu.copy, size: 15, color: Brand.blue)),
          ),
        ]),
      );
}

// ------------------------------------------------------------------------------------------------ one message
class MailReadScreen extends StatefulWidget {
  final int id;
  const MailReadScreen({super.key, required this.id});
  @override
  State<MailReadScreen> createState() => _MailReadScreenState();
}

class _MailReadScreenState extends State<MailReadScreen> {
  Map<String, dynamic>? _m;
  bool _failed = false;
  String? _busy;                       // the assistant's mode being asked
  Map<String, dynamic>? _summary;
  String? _translation;
  bool _aiFailed = false;

  @override
  void initState() { super.initState(); _load(); }

  Future<void> _load() async {
    final m = await MailApi.instance.message(widget.id);
    if (!mounted) return;
    setState(() { _m = m; _failed = m == null; });
  }

  Future<void> _ask(String mode) async {
    if (_busy != null) return;
    String? hint;
    if (mode == 'reply') {
      hint = await _askInstruction();
      if (hint == null) return;           // dismissed
    }
    setState(() { _busy = mode; _aiFailed = false; if (mode == 'summary') _translation = null; if (mode == 'translate') _summary = null; });
    final r = await MailApi.instance.assist(widget.id, mode, instruction: hint);
    if (!mounted) return;
    setState(() { _busy = null; _aiFailed = r == null; if (mode == 'summary') _summary = r; if (mode == 'translate') _translation = r?['text'] as String?; });
    if (mode == 'reply' && r != null && mounted) {
      await Navigator.of(context).push(MaterialPageRoute(builder: (_) => MailComposeScreen(parentId: widget.id, kind: 'Reply', draft: r['text'] as String? ?? '', instruction: hint)));
    }
  }

  Future<String?> _askInstruction() {
    final c = TextEditingController();
    return showModalBottomSheet<String>(
      context: context, isScrollControlled: true, backgroundColor: Brand.paper,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(22))),
      builder: (ctx) => Padding(
        padding: EdgeInsets.fromLTRB(20, 18, 20, MediaQuery.of(ctx).viewInsets.bottom + 20),
        child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(L(ctx, 'اقتراح رد', 'Suggest a reply'), style: T.h2),
          const SizedBox(height: 4),
          Text(L(ctx, 'توجيه للمساعد (اختياري) — مثلاً: اشكرهم واطلب التسليم خلال أسبوعين', 'An instruction (optional) — e.g. thank them and ask for delivery in two weeks'), style: T.small),
          const SizedBox(height: 10),
          TextField(controller: c, maxLength: 300, minLines: 2, maxLines: 4, style: const TextStyle(fontSize: 12)),
          const SizedBox(height: 6),
          _Btn(Lu.sparkles, L(ctx, 'اكتب المسودة', 'Write the draft'), primary: true, onTap: () => Navigator.of(ctx).pop(c.text.trim())),
        ]),
      ),
    );
  }

  Future<void> _reply(String kind) async {
    await Navigator.of(context).push(MaterialPageRoute(builder: (_) => MailComposeScreen(parentId: widget.id, kind: kind)));
  }

  @override
  Widget build(BuildContext context) {
    final m = _m;
    return ExecFrame(
      bottomBar: const CallsNav(),
      child: ExecBody(onRefresh: _load, children: [
        BackLink(L(context, 'البريد', 'Mail')),
        if (_failed)
          EmptyNote(icon: Lu.mailX, title: L(context, 'الرسالة غير متاحة', 'Message not available'), text: L(context, 'ربما حُذفت، أو أنها ليست في بريدك.', 'It may have been deleted, or it is not in your mailbox.'))
        else if (m == null)
          const Padding(padding: EdgeInsets.all(30), child: Center(child: CircularProgressIndicator()))
        else ...[
          Padding(padding: const EdgeInsets.only(bottom: 10), child: Text(m['subject'] as String? ?? '', style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w700, color: Brand.inkC, height: 1.5))),
          SingleChildScrollView(
            scrollDirection: Axis.horizontal,
            child: Row(children: [
              _ai(Lu.sparkles, L(context, 'تلخيص المحادثة', 'Summarize'), 'summary', on: _summary != null),
              const SizedBox(width: 6),
              _ai(Lu.penLine, L(context, 'اقتراح رد', 'Suggest a reply'), 'reply'),
              const SizedBox(width: 6),
              _ai(Lu.languages, L(context, 'ترجمة', 'Translate'), 'translate', on: _translation != null),
            ]),
          ),
          const SizedBox(height: 10),
          if (_busy != null) _panel([Row(children: [
            const SizedBox(width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2)),
            const SizedBox(width: 10),
            Text(L(context, 'المساعد يعمل…', 'The assistant is working…'), style: T.small),
          ])]),
          if (_aiFailed) _panel([Text(L(context, 'تعذّر على المساعد الإجابة الآن. حاول بعد قليل.', 'The assistant could not answer now. Try again in a moment.'), style: const TextStyle(fontSize: 11, color: Brand.badC))]),
          if (_summary != null) _summaryPanel(context, _summary!),
          if (_translation != null) _panel([
            Text(L(context, 'الترجمة', 'Translation'), style: const TextStyle(fontSize: 10.5, fontWeight: FontWeight.w600, color: Brand.blue)),
            const SizedBox(height: 6),
            Text(_translation!, style: const TextStyle(fontSize: 12, color: Brand.inkC, height: 1.7)),
          ]),
          for (final t in (m['thread'] as List? ?? const [])) _earlier(context, Map<String, dynamic>.from(t as Map)),
          _from(context, m),
          Container(
            width: double.infinity,
            margin: const EdgeInsets.only(bottom: 10),
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
            decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(16), border: Border.all(color: Brand.lineC)),
            child: SelectableText(m['text'] as String? ?? '', style: const TextStyle(fontSize: 12, color: Brand.inkC, height: 1.8)),
          ),
          for (final a in (m['attachments'] as List? ?? const [])) _file(context, Map<String, dynamic>.from(a as Map)),
          const SizedBox(height: 4),
          Row(children: [
            Expanded(child: _Btn(Lu.reply, L(context, 'رد', 'Reply'), primary: true, onTap: () => _reply('Reply'))),
            const SizedBox(width: 6),
            Expanded(child: _Btn(Lu.replyAll, L(context, 'رد على الكل', 'Reply all'), onTap: () => _reply('ReplyAll'))),
            const SizedBox(width: 6),
            Expanded(child: _Btn(Lu.forward, L(context, 'تحويل', 'Forward'), onTap: () => _reply('Forward'))),
          ]),
        ],
      ]),
    );
  }

  Widget _ai(IconData icon, String label, String mode, {bool on = false}) => Material(
        color: on ? Brand.navy : Brand.paper,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10), side: BorderSide(color: on ? Brand.navy : Brand.lineC)),
        child: InkWell(
          borderRadius: BorderRadius.circular(10),
          onTap: _busy == null ? () => _ask(mode) : null,
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
            child: Row(children: [
              Icon(icon, size: 13, color: on ? Colors.white : Brand.blue),
              const SizedBox(width: 5),
              Text(label, style: TextStyle(fontSize: 10.5, fontWeight: FontWeight.w600, color: on ? Colors.white : Brand.blue)),
            ]),
          ),
        ),
      );

  Widget _panel(List<Widget> children) => Container(
        width: double.infinity,
        margin: const EdgeInsets.only(bottom: 12),
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
        decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(18), border: Border.all(color: Brand.lineC)),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: children),
      );

  Widget _summaryPanel(BuildContext context, Map<String, dynamic> s) {
    List<String> list(String k) => (s[k] as List? ?? const []).map((e) => e is Map ? '${e['owner'] ?? L(context, 'غير محدد', 'Unassigned')} — ${e['task'] ?? ''}' : '$e').toList();
    final points = list('points'), decisions = list('decisions'), actions = list('actions');
    Widget section(IconData icon, String title, List<String> items) => Padding(
          padding: const EdgeInsets.only(top: 8),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [Icon(icon, size: 13, color: Brand.blue), const SizedBox(width: 5), Text(title, style: const TextStyle(fontSize: 10.5, fontWeight: FontWeight.w600, color: Brand.blue))]),
            for (final i in items) Padding(padding: const EdgeInsetsDirectional.only(start: 8, top: 3), child: Text('•  $i', style: const TextStyle(fontSize: 11, color: Brand.inkC, height: 1.6))),
          ]),
        );
    if (s['tooShort'] == true || ((s['overview'] as String? ?? '').isEmpty && points.isEmpty && decisions.isEmpty && actions.isEmpty)) {
      return _panel([Text(L(context, 'المحادثة أقصر من أن تُلخَّص.', 'This conversation is too short to summarize.'), style: T.small)]);
    }
    return _panel([
      if ((s['overview'] as String? ?? '').isNotEmpty) Text(s['overview'] as String, style: const TextStyle(fontSize: 12, color: Brand.inkC, height: 1.8)),
      if (points.isNotEmpty) section(Lu.list, L(context, 'أهم النقاط', 'Key points'), points),
      if (decisions.isNotEmpty) section(Lu.circleCheck, L(context, 'القرارات', 'Decisions'), decisions),
      if (actions.isNotEmpty) section(Lu.listTodo, L(context, 'المهام', 'Tasks'), actions),
      const SizedBox(height: 8),
      Text(L(context, 'من مساعد الشركة على خوادمها — راجعه قبل الاعتماد عليه.', 'From the company\'s own assistant — check it before relying on it.'), style: const TextStyle(fontSize: 9.5, color: Brand.mutedC)),
    ]);
  }

  Widget _earlier(BuildContext context, Map<String, dynamic> t) => InkWell(
        onTap: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => MailReadScreen(id: (t['id'] as num).toInt()))),
        child: Container(
          margin: const EdgeInsets.only(bottom: 10),
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
          decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(14), border: Border.all(color: Brand.lineC)),
          child: Row(children: [
            PersonAvatar(photo: t['fromPhoto'] as String?, name: t['fromName'] as String? ?? '', size: 26),
            const SizedBox(width: 8),
            Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(t['mine'] == true ? L(context, 'أنت', 'You') : (t['fromName'] as String? ?? ''), maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 10.5, fontWeight: FontWeight.w600, color: Brand.inkC)),
              Text(t['preview'] as String? ?? '', maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 9.5, color: Brand.mutedC)),
            ])),
            Text(_when(context, mailTime(t['at'])), style: const TextStyle(fontSize: 9, color: Brand.mutedC)),
          ]),
        ),
      );

  Widget _from(BuildContext context, Map<String, dynamic> m) => Padding(
        padding: const EdgeInsets.only(bottom: 10),
        child: Row(children: [
          PersonAvatar(photo: m['fromPhoto'] as String?, name: m['fromName'] as String? ?? '', size: 40),
          const SizedBox(width: 10),
          Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              Flexible(child: Text(m['fromName'] as String? ?? '', maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w600, color: Brand.inkC))),
              if (m['external'] == true) Padding(padding: const EdgeInsetsDirectional.only(start: 6), child: _tag(L(context, 'من خارج الشركة', 'From outside'))),
            ]),
            Text('${m['fromAddress'] ?? ''} · ${_when(context, mailTime(m['at']))}', maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 9.5, color: Brand.mutedC)),
          ])),
        ]),
      );

  Widget _file(BuildContext context, Map<String, dynamic> a) {
    final size = (a['size'] as num?)?.toInt() ?? 0;
    final kb = size < 1024 * 1024 ? '${(size / 1024).ceil()} ${L(context, 'كيلوبايت', 'KB')}' : '${(size / 1048576).toStringAsFixed(1)} ${L(context, 'ميجابايت', 'MB')}';
    return InkWell(
      onTap: () => launchUrl(Uri.parse(mediaUrl(a['path'] as String?)), mode: LaunchMode.externalApplication).catchError((_) => false),
      child: Container(
        margin: const EdgeInsets.only(bottom: 10),
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
        decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(12), border: Border.all(color: Brand.lineC)),
        child: Row(children: [
          Container(width: 30, height: 30, alignment: Alignment.center, decoration: BoxDecoration(color: Brand.soft, borderRadius: BorderRadius.circular(9)), child: const Icon(Lu.fileText, size: 15, color: Brand.blue)),
          const SizedBox(width: 8),
          Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(a['name'] as String? ?? '', maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 10.5, fontWeight: FontWeight.w600, color: Brand.inkC)),
            Text(kb, style: const TextStyle(fontSize: 9, color: Brand.mutedC)),
          ])),
        ]),
      ),
    );
  }
}

// ------------------------------------------------------------------------------------------------ the reply box
class MailComposeScreen extends StatefulWidget {
  final int? parentId;
  final String? kind;                  // Reply · ReplyAll · Forward; null = a new message
  final String? draft, instruction;    // a suggested reply, and the instruction it followed
  const MailComposeScreen({super.key, this.parentId, this.kind, this.draft, this.instruction});
  @override
  State<MailComposeScreen> createState() => _MailComposeScreenState();
}

class _MailComposeScreenState extends State<MailComposeScreen> {
  final _to = <String>[], _cc = <String>[];
  final _subject = TextEditingController(), _body = TextEditingController(), _toInput = TextEditingController(), _ccInput = TextEditingController();
  String? _toName, _quote;
  final _files = <(String, String)>[];
  final _library = <(int, String)>[];
  List<Map<String, dynamic>> _suggest = const [];
  bool _forCc = false, _sending = false;
  Timer? _debounce;

  static List<String> _split(String? s) => (s ?? '').split(RegExp('[,;]')).map((x) => x.trim()).where((x) => x.isNotEmpty).toList();

  @override
  void initState() {
    super.initState();
    _body.text = widget.draft ?? '';
    if (widget.parentId != null) _prefill();
  }

  Future<void> _prefill() async {
    final p = await MailApi.instance.compose(widget.parentId!, widget.kind ?? 'Reply');
    if (!mounted || p == null) return;
    setState(() {
      _to.addAll(_split(p['to'] as String?)); _cc.addAll(_split(p['cc'] as String?));
      _subject.text = p['subject'] as String? ?? ''; _quote = p['quote'] as String?; _toName = p['toName'] as String?;
    });
  }

  void _lookup(String q, {required bool cc}) {
    _debounce?.cancel();
    _forCc = cc;
    if (q.trim().length < 2) { setState(() => _suggest = const []); return; }
    _debounce = Timer(const Duration(milliseconds: 300), () async {
      final r = await MailApi.instance.contacts(q.trim());
      if (mounted) setState(() => _suggest = r);
    });
  }

  void _add(String address, {required bool cc}) {
    final a = address.trim();
    if (a.isEmpty || !a.contains('@')) return;
    setState(() { (cc ? _cc : _to).add(a); (cc ? _ccInput : _toInput).clear(); _suggest = const []; });
  }

  Future<void> _pickFiles() async {
    final r = await FilePicker.pickFiles();
    if (r.isEmpty) return;
    setState(() { for (final f in r) { if (f.path != null) _files.add((f.path!, f.name)); } });
  }

  Future<void> _pickLibrary() async {
    final picked = await Navigator.of(context).push<(int, String)>(MaterialPageRoute(builder: (_) => const _LibraryPicker()));
    if (picked != null) setState(() => _library.add(picked));
  }

  Future<void> _send() async {
    // what is still typed in a field counts as an address
    _add(_toInput.text, cc: false); _add(_ccInput.text, cc: true);
    if (_to.isEmpty || _subject.text.trim().isEmpty) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(L(context, 'المستلم والموضوع مطلوبان', 'Recipient and subject are required'))));
      return;
    }
    setState(() => _sending = true);
    final r = await MailApi.instance.send(
      to: _to.join(', '), cc: _cc.join(', '), subject: _subject.text.trim(), text: _body.text,
      parentId: widget.parentId, kind: widget.kind, libraryIds: [for (final l in _library) l.$1], files: _files,
    );
    if (!mounted) return;
    setState(() => _sending = false);
    final ok = r?['ok'] == true;
    final status = r?['status'];
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(!ok
        ? L(context, 'تعذّر الإرسال', 'Could not send') + ((r?['error'] as String?)?.isNotEmpty == true ? ' — ${r!['error']}' : '')
        : status == 'Queued' ? L(context, 'أُضيفت إلى الصادر وستُرسل تلقائياً', 'Added to the outbox; it will be sent') : L(context, 'أُرسلت', 'Sent'))));
    if (ok) Navigator.of(context).pop(true);
  }

  @override
  void dispose() {
    _debounce?.cancel();
    for (final c in [_subject, _body, _toInput, _ccInput]) { c.dispose(); }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final title = switch (widget.kind) { 'Reply' => L(context, 'رد', 'Reply'), 'ReplyAll' => L(context, 'رد على الكل', 'Reply all'), 'Forward' => L(context, 'تحويل', 'Forward'), _ => L(context, 'رسالة جديدة', 'New message') };
    return ExecFrame(
      bottomBar: const CallsNav(),
      child: ExecBody(children: [
        BackLink(widget.parentId != null ? L(context, 'الرسالة', 'The message') : L(context, 'البريد', 'Mail')),
        PageHead(title, eyebrow: _toName != null ? L(context, 'إلى $_toName', 'To $_toName') : null),
        _field(L(context, 'إلى', 'To'), _to, _toInput, cc: false),
        if (_suggest.isNotEmpty && !_forCc) _suggestions(cc: false),
        _field(L(context, 'نسخة', 'Cc'), _cc, _ccInput, cc: true),
        if (_suggest.isNotEmpty && _forCc) _suggestions(cc: true),
        Container(
          decoration: const BoxDecoration(border: Border(bottom: BorderSide(color: Brand.lineC))),
          child: Row(children: [
            SizedBox(width: 48, child: Text(L(context, 'الموضوع', 'Subject'), style: const TextStyle(fontSize: 10, color: Brand.mutedC))),
            Expanded(child: TextField(controller: _subject, style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600),
                decoration: const InputDecoration(border: InputBorder.none, enabledBorder: InputBorder.none, focusedBorder: InputBorder.none, filled: false, isDense: true, contentPadding: EdgeInsets.symmetric(vertical: 12)))),
          ]),
        ),
        if (widget.draft != null)
          Container(
            margin: const EdgeInsets.only(top: 10, bottom: 6),
            padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 6),
            decoration: BoxDecoration(color: const Color(0xFFF4F7FD), borderRadius: BorderRadius.circular(10)),
            child: Row(children: [
              const Icon(Lu.sparkles, size: 13, color: Brand.blue),
              const SizedBox(width: 6),
              Expanded(child: Text(
                  (widget.instruction?.isNotEmpty == true ? L(context, 'مسودة من المساعد بتوجيهك «${widget.instruction}»', 'A draft by the assistant following «${widget.instruction}»') : L(context, 'مسودة من المساعد', 'A draft by the assistant')) +
                      L(context, ' — عدّلها ثم أرسل', ' — edit it, then send'),
                  style: const TextStyle(fontSize: 9.5, color: Brand.blue))),
            ]),
          )
        else
          const SizedBox(height: 10),
        Container(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
          decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(14), border: Border.all(color: Brand.lineC)),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            TextField(controller: _body, minLines: 6, maxLines: 18, style: const TextStyle(fontSize: 12, height: 1.8),
                decoration: InputDecoration(hintText: L(context, 'اكتب رسالتك', 'Write your message'), hintStyle: const TextStyle(fontSize: 11.5, color: Brand.mutedC),
                    border: InputBorder.none, enabledBorder: InputBorder.none, focusedBorder: InputBorder.none, filled: false, isDense: true, contentPadding: const EdgeInsets.symmetric(vertical: 8))),
            if ((_quote ?? '').isNotEmpty)
              Container(
                width: double.infinity,
                margin: const EdgeInsets.only(bottom: 8),
                padding: const EdgeInsets.only(top: 6),
                decoration: const BoxDecoration(border: Border(top: BorderSide(color: Brand.lineC))),
                child: Text('----- ${_subject.text} -----\n${_quote!.length > 600 ? '${_quote!.substring(0, 600)}…' : _quote}', style: const TextStyle(fontSize: 10.5, color: Brand.mutedC, height: 1.6)),
              ),
          ]),
        ),
        if (_files.isNotEmpty || _library.isNotEmpty)
          Padding(
            padding: const EdgeInsets.only(top: 10),
            child: Wrap(spacing: 6, runSpacing: 6, children: [
              for (final f in _files) _attached(f.$2, () => setState(() => _files.remove(f))),
              for (final l in _library) _attached(l.$2, () => setState(() => _library.remove(l))),
            ]),
          ),
        const SizedBox(height: 12),
        Row(children: [
          Expanded(child: _Btn(Lu.paperclip, L(context, 'إرفاق ملف', 'Attach a file'), onTap: _pickFiles)),
          const SizedBox(width: 6),
          Expanded(child: _Btn(Lu.folderOpen, L(context, 'من المكتبة', 'From the library'), onTap: _pickLibrary)),
        ]),
        const SizedBox(height: 10),
        _Btn(Lu.sendHorizontal, _sending ? L(context, 'جارٍ الإرسال…', 'Sending…') : L(context, 'إرسال', 'Send'), primary: true, onTap: _sending ? null : _send),
      ]),
    );
  }

  Widget _field(String label, List<String> list, TextEditingController input, {required bool cc}) => Container(
        decoration: const BoxDecoration(border: Border(bottom: BorderSide(color: Brand.lineC))),
        padding: const EdgeInsets.symmetric(vertical: 4),
        child: Row(children: [
          SizedBox(width: 48, child: Text(label, style: const TextStyle(fontSize: 10, color: Brand.mutedC))),
          Expanded(child: Wrap(spacing: 5, runSpacing: 4, crossAxisAlignment: WrapCrossAlignment.center, children: [
            for (final a in list)
              Container(
                padding: const EdgeInsetsDirectional.only(start: 8, end: 4, top: 2, bottom: 2),
                decoration: BoxDecoration(color: Brand.soft, borderRadius: BorderRadius.circular(14)),
                child: Row(mainAxisSize: MainAxisSize.min, children: [
                  Directionality(textDirection: TextDirection.ltr, child: Text(a, style: const TextStyle(fontSize: 10, color: Brand.inkC))),
                  InkWell(onTap: () => setState(() => list.remove(a)), child: const Padding(padding: EdgeInsets.all(2), child: Icon(Lu.x, size: 12, color: Brand.mutedC))),
                ]),
              ),
            ConstrainedBox(
              constraints: const BoxConstraints(minWidth: 120, maxWidth: 220),
              child: TextField(
                controller: input, keyboardType: TextInputType.emailAddress, style: const TextStyle(fontSize: 11),
                onChanged: (v) => _lookup(v, cc: cc), onSubmitted: (v) => _add(v, cc: cc),
                decoration: InputDecoration(hintText: L(context, 'اسم أو عنوان', 'Name or address'), hintStyle: const TextStyle(fontSize: 10.5, color: Brand.mutedC),
                    border: InputBorder.none, enabledBorder: InputBorder.none, focusedBorder: InputBorder.none, filled: false, isDense: true, contentPadding: const EdgeInsets.symmetric(vertical: 9)),
              ),
            ),
          ])),
        ]),
      );

  Widget _suggestions({required bool cc}) => Container(
        margin: const EdgeInsets.only(top: 4, bottom: 4),
        decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(12), border: Border.all(color: Brand.lineC)),
        child: Column(children: [
          for (final p in _suggest.take(6))
            InkWell(
              onTap: () => _add(p['email'] as String? ?? '', cc: cc),
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
                child: Row(children: [
                  PersonAvatar(photo: p['photo'] as String?, name: p['name'] as String? ?? '', size: 26),
                  const SizedBox(width: 8),
                  Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text(p['name'] as String? ?? '', style: const TextStyle(fontSize: 11, fontWeight: FontWeight.w600, color: Brand.inkC)),
                    Text(p['email'] as String? ?? '', style: const TextStyle(fontSize: 9.5, color: Brand.mutedC)),
                  ])),
                ]),
              ),
            ),
        ]),
      );

  Widget _attached(String name, VoidCallback remove) => Container(
        padding: const EdgeInsetsDirectional.only(start: 8, end: 4, top: 4, bottom: 4),
        decoration: BoxDecoration(color: Brand.soft, borderRadius: BorderRadius.circular(10)),
        child: Row(mainAxisSize: MainAxisSize.min, children: [
          const Icon(Lu.paperclip, size: 12, color: Brand.blue),
          const SizedBox(width: 4),
          ConstrainedBox(constraints: const BoxConstraints(maxWidth: 180), child: Text(name, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 10, color: Brand.inkC))),
          InkWell(onTap: remove, child: const Padding(padding: EdgeInsets.all(2), child: Icon(Lu.x, size: 12, color: Brand.mutedC))),
        ]),
      );
}

// ------------------------------------------------------------------------------------------------ the library
/// «من المكتبة»: the company's document library, folder by folder; a file is attached by reference (no upload).
class _LibraryPicker extends StatefulWidget {
  const _LibraryPicker();
  @override
  State<_LibraryPicker> createState() => _LibraryPickerState();
}

class _LibraryPickerState extends State<_LibraryPicker> {
  int? _folder;
  Map<String, dynamic>? _data;

  @override
  void initState() { super.initState(); _load(); }

  Future<void> _load() async {
    final d = await MailApi.instance.library(folderId: _folder);
    if (mounted) setState(() => _data = d);
  }

  @override
  Widget build(BuildContext context) {
    final items = (_data?['items'] as List? ?? const []).map((e) => Map<String, dynamic>.from(e as Map)).toList();
    return ExecFrame(
      child: ExecBody(children: [
        BackLink(L(context, 'الرسالة', 'The message')),
        PageHead(L(context, 'من المكتبة', 'From the library'), eyebrow: L(context, 'مكتبة مستندات الشركة', 'The company document library')),
        if (_folder != null)
          Padding(padding: const EdgeInsets.only(bottom: 10), child: _Btn(Lu.undo2, L(context, 'المجلد الأعلى', 'Up a folder'), onTap: () { _folder = _data?['parentId'] as int?; _data = null; setState(() {}); _load(); })),
        if (_data == null)
          const Padding(padding: EdgeInsets.all(30), child: Center(child: CircularProgressIndicator()))
        else if (items.isEmpty)
          EmptyNote(icon: Lu.folderOpen, title: L(context, 'لا ملفات هنا', 'No files here'), text: L(context, 'اختر مجلداً آخر.', 'Choose another folder.'))
        else
          _card([
            for (final i in items)
              InkWell(
                onTap: () {
                  if (i['isFolder'] == true) { _folder = (i['id'] as num).toInt(); _data = null; setState(() {}); _load(); }
                  else { Navigator.of(context).pop(((i['id'] as num).toInt(), i['name'] as String? ?? '')); }
                },
                child: Padding(
                  padding: const EdgeInsets.symmetric(vertical: 11),
                  child: Row(children: [
                    Icon(i['isFolder'] == true ? Lu.folder : Lu.fileText, size: 18, color: Brand.blue),
                    const SizedBox(width: 10),
                    Expanded(child: Text(i['name'] as String? ?? '', maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12, color: Brand.inkC))),
                    if (i['isFolder'] != true) Text(i['sizeText'] as String? ?? '', style: T.small),
                  ]),
                ),
              ),
          ]),
      ]),
    );
  }
}
