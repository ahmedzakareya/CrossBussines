import 'dart:async';
import 'dart:io';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import 'package:record/record.dart';
import 'package:video_player/video_player.dart';

import '../../models/calls.dart';
import '../../models/chat.dart';
import '../../providers/calls_provider.dart';
import '../../services/api_service.dart';
import '../../theme/brand.dart';
import '../../widgets/ui.dart';
import '../../services/chat_hub.dart';
import '../calls/calls_screen.dart';
import 'chat_extras.dart';

/// A conversation — exactly the approved prototype (design-output/executive-prototype, ?page=chat): the head (back,
/// face, name and presence, voice and video), the pinned line, the thread by day (others white on the left of their
/// face, mine navy; mentions as chips; a thread's reply count; a poll with its bars), then the composer (+, the box,
/// attach, the microphone and send). Long-press a message to react or reply. The same rules as the browser: the
/// server refuses what the company's data-loss policy blocks, and says why.
class ChatScreen extends StatefulWidget {
  final int id;
  final String? title;
  final int? otherId;
  final int? focusMessageId;
  /// How many were unread when it was opened: enough of them offers «ما فاتك».
  final int unread;
  const ChatScreen({super.key, required this.id, this.title, this.otherId, this.focusMessageId, this.unread = 0});
  @override
  State<ChatScreen> createState() => _ChatScreenState();
}

class _ChatScreenState extends State<ChatScreen> {
  final _api = ApiService.instance;
  final _box = TextEditingController();
  final _scroll = ScrollController();
  final _rec = AudioRecorder();
  List<ChatMsg> _msgs = [];
  ChatHeader? _head;
  List<ChatPinned> _pinned = const [];
  ChatMsg? _replyTo;
  bool _loading = true, _older = true, _sending = false, _recording = false;
  DateTime? _recStart;
  Timer? _tick, _recTick;

  @override
  void initState() {
    super.initState();
    _box.addListener(() => setState(() {}));
    _scroll.addListener(() { if (_scroll.position.pixels >= _scroll.position.maxScrollExtent - 60) _loadOlder(); });
    _first();
    // live over the chat hub; the poll stays as the fallback (every 4 s while the hub is down, every 20 s while it is up)
    _tick = Timer.periodic(const Duration(seconds: 4), (_) { if (!ChatHub.instance.connected || (_ticks++ % 5 == 0)) _refresh(); });
    ChatHub.instance.join(widget.id);
    ChatHub.instance.events.addListener(_onHub);
    // the call buttons in the head need the calls hub: connect it here (it used to connect only on the calls screen,
    // so a call from a conversation was refused silently) and show its notices in this screen
    _calls = context.read<CallsProvider>();
    _calls.connect();
    _calls.notice.addListener(_onCallNotice);
    _box.addListener(_onTyping);
  }

  int _ticks = 0;
  /// Translations asked for here: message → its text in the other language (null while it is being made).
  final Map<int, String?> _translations = {};

  /// From the assistant's sources: a message already loaded is scrolled to; an older one is said to be further up.
  void _goToMessage(int id) {
    final i = _msgs.indexWhere((m) => m.id == id);
    if (i < 0) { ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(L(context, 'الرسالة أقدم من المعروض — مرّر للأعلى', 'That message is further up — scroll up')))); return; }
    // the list is reversed: the newest is at offset 0; a rough position is enough to bring it into view
    final fromEnd = _msgs.length - 1 - i;
    if (_scroll.hasClients) _scroll.animateTo((fromEnd * 90.0).clamp(0, _scroll.position.maxScrollExtent), duration: const Duration(milliseconds: 300), curve: Curves.easeOut);
  }
  late final CallsProvider _calls;

  void _onCallNotice() {
    final v = _calls.notice.value;
    if (v == null || !mounted || _calls.busy) return;
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(v), behavior: SnackBarBehavior.floating));
  }
  Timer? _hubDebounce, _typingOff;
  DateTime _lastTypingSent = DateTime(2000);
  String? _typingName;
  List<ChatMember> _mentionHits = const [];
  int? _catchUpSince;

  void _onHub() {
    final e = ChatHub.instance.events.value;
    if (e == null || e.conversationId != widget.id) return;
    if (e.name == 'typing') {
      final who = (e.data['employeeId'] as num?)?.toInt();
      final m = _head?.members.where((x) => x.id == who).toList();
      setState(() => _typingName = (m != null && m.isNotEmpty) ? m.first.name : L(context, 'أحدهم', 'Someone'));
      _typingOff?.cancel();
      _typingOff = Timer(const Duration(seconds: 4), () { if (mounted) setState(() => _typingName = null); });
      return;
    }
    if (e.name == 'pinned') { _api.getChatPinned(widget.id).then((p) { if (mounted) setState(() => _pinned = p); }); }
    if (e.name == 'message') { _typingOff?.cancel(); _typingName = null; }
    // several events arrive together (a message, then the read mark): one refresh for all of them
    _hubDebounce?.cancel();
    _hubDebounce = Timer(const Duration(milliseconds: 250), _refresh);
  }

  /// "is typing" to the others, at most every three seconds; and the @ suggestions.
  void _onTyping() {
    final t = _box.text;
    if (t.trim().isNotEmpty && DateTime.now().difference(_lastTypingSent).inSeconds >= 3) {
      _lastTypingSent = DateTime.now();
      ChatHub.instance.typing(widget.id);
    }
    final at = RegExp(r'(?:^|\s)@([^\s@]{0,20})$').firstMatch(t);
    final hits = at == null
        ? const <ChatMember>[]
        : (_head?.members ?? const <ChatMember>[]).where((m) {
            final q = at.group(1)!.toLowerCase();
            return q.isEmpty || m.name.toLowerCase().contains(q) || (m.altName ?? '').toLowerCase().contains(q);
          }).take(5).toList();
    if (hits.length != _mentionHits.length || !hits.every(_mentionHits.contains)) setState(() => _mentionHits = hits);
  }

  void _pickMention(ChatMember m) {
    final t = _box.text;
    final i = t.lastIndexOf('@');
    _box.text = '${t.substring(0, i)}@${m.name} ';
    _box.selection = TextSelection.collapsed(offset: _box.text.length);
    setState(() => _mentionHits = const []);
  }

  @override
  void dispose() {
    _tick?.cancel();
    _hubDebounce?.cancel();
    _typingOff?.cancel();
    ChatHub.instance.events.removeListener(_onHub);
    _calls.notice.removeListener(_onCallNotice);
    ChatHub.instance.leave(widget.id);
    _recTick?.cancel();
    _rec.dispose();
    _draftPlayer?.dispose();
    _box.dispose();
    _scroll.dispose();
    super.dispose();
  }

  Future<void> _first() async {
    final r = await Future.wait([_api.getChatHeader(widget.id), _api.getChatMessages(widget.id).catchError((_) => <ChatMsg>[]), _api.getChatPinned(widget.id)]);
    if (!mounted) return;
    setState(() {
      _head = r[0] as ChatHeader?;
      _msgs = r[1] as List<ChatMsg>;
      _pinned = r[2] as List<ChatPinned>;
      _loading = false;
      _older = _msgs.length >= 30;
      // «ما فاتك» when enough had piled up: from the last message I had read
      if (widget.unread >= 5 && _msgs.length > widget.unread) _catchUpSince = _msgs[_msgs.length - widget.unread - 1].id;
    });
    _meta(_msgs);
    _api.readChat(widget.id);
  }

  Future<void> _openCatchUp() async {
    final since = _catchUpSince; if (since == null) return;
    final d = await _api.chatCatchUp(widget.id, since);
    if (!mounted) return;
    setState(() => _catchUpSince = null);
    if (d != null) await showCatchUp(context, d);
  }

  Future<void> _showPinned() async {
    await showModalBottomSheet<void>(context: context, backgroundColor: Brand.paper,
        shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(22))),
        builder: (c) => SafeArea(child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
              Padding(padding: const EdgeInsets.fromLTRB(20, 18, 20, 6), child: Text(L(context, 'الرسائل المثبّتة', 'Pinned messages'), style: T.h2)),
              ConstrainedBox(
                constraints: BoxConstraints(maxHeight: MediaQuery.of(c).size.height * .5),
                child: ListView(shrinkWrap: true, children: [
                  for (final p in _pinned)
                    ListTile(leading: const Icon(Lu.pin, size: 18, color: Brand.blue), title: Text(p.body, maxLines: 3, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12.5)),
                        subtitle: p.sender == null ? null : Text(p.sender!, style: T.small),
                        trailing: IconButton(icon: const Icon(Lu.pinOff, size: 16, color: Brand.mutedC), onPressed: () async { Navigator.pop(c); if (await _api.chatPin(p.id, false)) { final l = await _api.getChatPinned(widget.id); if (mounted) setState(() => _pinned = l); } })),
                ]),
              ),
            ])));
  }

  /// The newest thirty again: new messages join, edits/reactions/deletions replace, the read marks move.
  Future<void> _refresh() async {
    try {
      final latest = await _api.getChatMessages(widget.id);
      final head = await _api.getChatHeader(widget.id);
      if (!mounted) return;
      final byId = {for (final m in _msgs) m.id: m};
      var added = false;
      for (final m in latest) {
        m.meta = byId[m.id]?.meta;
        if (!byId.containsKey(m.id)) added = true;
        byId[m.id] = m;
      }
      setState(() { _msgs = byId.values.toList()..sort((a, b) => a.id.compareTo(b.id)); if (head != null) _head = head; });
      _meta(latest);
      if (added) _api.readChat(widget.id);
    } catch (_) {/* the next tick */}
  }

  Future<void> _loadOlder() async {
    if (!_older || _loading || _msgs.isEmpty) return;
    _loading = true;
    try {
      final more = await _api.getChatMessages(widget.id, before: _msgs.first.id);
      if (!mounted) return;
      setState(() { _msgs = [...more, ..._msgs]; _older = more.length >= 30; });
      _meta(more);
    } catch (_) {}
    _loading = false;
  }

  Future<void> _meta(List<ChatMsg> list) async {
    final meta = await _api.getChatMeta(widget.id, [for (final m in list) m.id]);
    if (!mounted || meta.isEmpty) return;
    setState(() { for (final m in _msgs) { if (meta.containsKey(m.id)) m.meta = meta[m.id]; } });
  }

  void _refused(String? code) {
    final text = switch (code) {
      'too_large' => L(context, 'الملف أكبر من 50 ميغابايت', 'The file is larger than 50 MB'),
      'attachment_type' => L(context, 'نوع الملف غير مسموح', 'This type of file is not allowed'),
      'network' => L(context, 'تعذّر الإرسال — تحقّق من الاتصال', 'Could not send — check the connection'),
      _ => L(context, 'لم تُرسَل الرسالة: قد تحتوي على بيانات حساسة تمنعها سياسة الشركة', 'Not sent: it may hold sensitive data the company’s policy blocks'),
    };
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(text), behavior: SnackBarBehavior.floating));
  }

  Future<void> _send({String? filePath, String? fileName}) async {
    final text = _box.text.trim();
    if (_sending || (text.isEmpty && filePath == null)) return;
    setState(() => _sending = true);
    final (m, code) = await _api.sendChat(widget.id, body: filePath == null ? text : null, replyToId: _replyTo?.id, filePath: filePath, fileName: fileName);
    if (!mounted) return;
    setState(() {
      _sending = false;
      if (m != null) {
        if (filePath == null) _box.clear();
        _replyTo = null;
        if (!_msgs.any((x) => x.id == m.id)) _msgs = [..._msgs, m];
      }
    });
    if (m == null) _refused(code);
    if (_scroll.hasClients) _scroll.animateTo(0, duration: const Duration(milliseconds: 200), curve: Curves.easeOut);
  }

  Future<void> _attach({bool picture = false}) async {
    final files = await FilePicker.pickFiles(type: picture ? FileType.image : FileType.any);
    if (files.isEmpty || files.first.path == null) return;
    await _send(filePath: files.first.path, fileName: files.first.name);
  }

  Future<void> _plus() async {
    final pick = await showModalBottomSheet<int>(context: context, backgroundColor: Brand.paper,
        shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(22))),
        builder: (c) => SafeArea(child: Column(mainAxisSize: MainAxisSize.min, children: [
              ListTile(leading: const Icon(Lu.image, color: Brand.blue), title: Text(L(context, 'صورة', 'Picture')), onTap: () => Navigator.pop(c, 1)),
              ListTile(leading: const Icon(Lu.paperclip, color: Brand.blue), title: Text(L(context, 'ملف', 'File')), onTap: () => Navigator.pop(c, 2)),
              ListTile(leading: const Icon(Lu.chartBar, color: Brand.blue), title: Text(L(context, 'تصويت', 'Poll')), onTap: () => Navigator.pop(c, 3)),
              ListTile(leading: const Icon(Lu.clock, color: Brand.blue), title: Text(L(context, 'جدولة رسالة', 'Schedule a message')), onTap: () => Navigator.pop(c, 4)),
            ])));
    if (pick == 1) _attach(picture: true);
    if (pick == 2) _attach();
    if (pick == 3 && mounted) { final m = await showPollComposer(context, widget.id); if (m != null) _refresh(); }
    if (pick == 4 && mounted) {
      final ok = await showScheduleComposer(context, widget.id, _box.text.trim());
      if (ok == true && mounted) { _box.clear(); ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(L(context, 'جُدولت الرسالة', 'The message is scheduled')))); }
    }
  }

  // A voice message is LISTENED TO BEFORE IT IS SENT (owner 2026-11-19): the microphone stops the recording, the
  // preview plays it, and only «send» sends it; the bin deletes it.
  String? _draftPath;
  int _draftSecs = 0;
  VideoPlayerController? _draftPlayer;

  Future<void> _voice() async {
    if (_recording) {
      _recTick?.cancel();
      final path = await _rec.stop();
      final ms = _recStart == null ? 0 : DateTime.now().difference(_recStart!).inMilliseconds;
      setState(() => _recording = false);
      if (path == null) return;
      if (ms < 700) { try { File(path).deleteSync(); } catch (_) {} return; }   // a tap, not a message
      final player = VideoPlayerController.file(File(path));
      player.addListener(() { if (mounted) setState(() {}); });
      setState(() { _draftPath = path; _draftSecs = (ms / 1000).round(); _draftPlayer = player; });
      try { await player.initialize(); } catch (_) {/* still sendable; only the preview is silent */}
      return;
    }
    if (_draftPath != null) return;   // the one waiting for review is sent or deleted first
    if (!await _rec.hasPermission()) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(L(context, 'اسمح بالميكروفون لتسجيل رسالة صوتية', 'Allow the microphone to record a voice message'))));
      return;
    }
    final path = '${Directory.systemTemp.path}/cb-voice-${DateTime.now().millisecondsSinceEpoch}.m4a';
    await _rec.start(const RecordConfig(encoder: AudioEncoder.aacLc, bitRate: 64000), path: path);
    _recStart = DateTime.now();
    _recTick = Timer.periodic(const Duration(seconds: 1), (_) { if (mounted) setState(() {}); });
    setState(() => _recording = true);
  }

  void _dropDraft({bool delete = true}) {
    final p = _draftPath, pl = _draftPlayer;
    setState(() { _draftPath = null; _draftPlayer = null; });
    pl?.dispose();
    if (delete && p != null) { try { File(p).deleteSync(); } catch (_) {} }
  }

  Future<void> _sendDraft() async {
    final p = _draftPath; if (p == null) return;
    _dropDraft(delete: false);
    await _send(filePath: p, fileName: 'voice-${DateTime.now().millisecondsSinceEpoch}.m4a');
  }

  Future<void> _cancelVoice() async {
    _recTick?.cancel();
    final p = await _rec.stop();
    if (p != null) { try { File(p).deleteSync(); } catch (_) {} }
    if (mounted) setState(() => _recording = false);
  }

  Future<void> _longPress(ChatMsg m) async {
    if (m.deleted) return;
    const emojis = ['👍', '❤️', '😂', '😮', '😢', '🙏'];
    final r = await showModalBottomSheet<String>(context: context, backgroundColor: Brand.paper,
        shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(22))),
        builder: (c) => SafeArea(child: Column(mainAxisSize: MainAxisSize.min, children: [
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 16, 16, 6),
                child: Row(mainAxisAlignment: MainAxisAlignment.spaceAround, children: [
                  for (final e in emojis) GestureDetector(onTap: () => Navigator.pop(c, e), child: Text(e, style: const TextStyle(fontSize: 26))),
                ]),
              ),
              ListTile(leading: const Icon(Lu.reply, color: Brand.blue), title: Text(L(context, 'رد', 'Reply')), onTap: () => Navigator.pop(c, '·reply')),
              ListTile(leading: const Icon(Lu.messagesSquare, color: Brand.blue), title: Text(L(context, 'رد في سلسلة', 'Reply in a thread')), onTap: () => Navigator.pop(c, '·thread')),
              ListTile(leading: Icon(m.meta?.pinned == true ? Lu.pinOff : Lu.pin, color: Brand.blue), title: Text(m.meta?.pinned == true ? L(context, 'إلغاء التثبيت', 'Unpin') : L(context, 'تثبيت في المحادثة', 'Pin in the conversation')), onTap: () => Navigator.pop(c, '·pin')),
              ListTile(leading: Icon(m.meta?.saved == true ? Lu.bookmarkMinus : Lu.bookmark, color: Brand.blue), title: Text(m.meta?.saved == true ? L(context, 'إزالة من المحفوظات', 'Remove from saved') : L(context, 'حفظ', 'Save')), onTap: () => Navigator.pop(c, '·save')),
              ListTile(leading: const Icon(Lu.forward, color: Brand.blue), title: Text(L(context, 'تحويل', 'Forward')), onTap: () => Navigator.pop(c, '·forward')),
              if (m.text.isNotEmpty) ListTile(leading: const Icon(Lu.languages, color: Brand.blue), title: Text(L(context, 'ترجمة', 'Translate')), onTap: () => Navigator.pop(c, '·translate')),
            ])));
    if (r == null || !mounted) return;
    if (r == '·reply') { setState(() => _replyTo = m); return; }
    if (r == '·thread') { await showThreadSheet(context, widget.id, m); _meta([m]); return; }
    if (r == '·forward') { await showForwardPicker(context, m); return; }
    if (r == '·translate') {
      setState(() => _translations[m.id] = null);           // «جارٍ الترجمة…» until it arrives
      final t = await _api.chatTranslate(m.id);
      if (!mounted) return;
      setState(() { if (t == null) { _translations.remove(m.id); } else { _translations[m.id] = t.$1; } });
      if (t == null) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(L(context, 'المساعد غير متاح الآن — حاول لاحقاً', 'The assistant is not available right now — try again later'))));
      return;
    }
    if (r == '·pin') {
      if (await _api.chatPin(m.id, m.meta?.pinned != true)) { _meta([m]); final p = await _api.getChatPinned(widget.id); if (mounted) setState(() => _pinned = p); }
      return;
    }
    if (r == '·save') {
      final on = m.meta?.saved != true;
      if (await _api.chatSave(m.id, on)) { _meta([m]); if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(on ? L(context, 'حُفظت', 'Saved') : L(context, 'أُزيلت من المحفوظات', 'Removed from saved')))); }
      return;
    }
    if (await _api.reactChat(m.id, r)) _refresh();
  }

  // ------------------------------------------------------------------------------------------ calls from the head
  List<CallPerson> _others() {
    final h = _head;
    if (h == null) return const [];
    final mine = context.read<CallsProvider>().myId;
    if (h.members.isEmpty) {
      // a direct conversation's header names the other person by its title only (members is sent empty)
      final other = widget.otherId ?? h.memberIds.firstWhere((id) => id != mine, orElse: () => 0);
      return other == 0 ? const [] : [CallPerson(id: other, name: h.title, nameEn: h.title, photo: h.avatar, state: h.online ? 'on' : 'off')];
    }
    return [for (final m in h.members) if (m.id != mine) CallPerson(id: m.id, name: m.name, nameEn: m.altName ?? m.name, photo: m.avatar, state: m.online ? 'on' : 'off')];
  }

  Future<void> _call({required bool video}) async {
    final p = context.read<CallsProvider>();
    if (!p.connected) { await p.connect(); if (!p.connected) await Future.delayed(const Duration(seconds: 2)); }
    final h = _head;
    if (h == null) return;
    if (!h.isGroup) {
      final other = widget.otherId;
      final o = _others().where((x) => other == null || x.id == other).toList();
      if (o.isNotEmpty) { p.startCall(o.first, video: video); } else if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(L(context, 'لا يمكن الاتصال من هذه المحادثة الآن', 'A call cannot be made from this conversation now'))));
      }
    } else {
      final o = _others();
      if (o.length >= 2) {
        p.startGroup(o.take((p.me?.groupMax ?? 4)).toList(), video: video);
      } else if (o.length == 1) {
        p.startCall(o.first, video: video);
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final calls = context.watch<CallsProvider>();
    final canCall = calls.me?.installed == true;
    final h = _head;
    final title = h?.title ?? widget.title ?? '';
    final sub = h == null ? '' : h.isGroup ? membersLabel(context, h.memberCount) : h.online ? L(context, 'متاح الآن', 'Available now') : L(context, 'غير متصل', 'Offline');
    return ExecFrame(
      bottomBar: const CallsNav(),
      child: Column(children: [
        // the head
        Container(
          padding: const EdgeInsets.fromLTRB(14, 10, 14, 10),
          decoration: const BoxDecoration(color: Brand.paper, border: Border(bottom: BorderSide(color: Brand.lineC))),
          child: Row(children: [
            GestureDetector(onTap: () => Navigator.of(context).pop(), child: Icon(Directionality.of(context) == TextDirection.rtl ? Lu.arrowRight : Lu.arrowLeft, size: 20, color: Brand.inkC)),
            const SizedBox(width: 10),
            h != null && h.isGroup && h.avatar == null
                ? Container(width: 38, height: 38, alignment: Alignment.center, decoration: const BoxDecoration(color: Brand.soft, shape: BoxShape.circle), child: const Icon(Lu.usersRound, size: 18, color: Brand.blue))
                : PersonAvatar(photo: h?.avatar, name: title, size: 38),
            const SizedBox(width: 10),
            Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(title, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600, color: Brand.inkC)),
              Text(sub, style: TextStyle(fontSize: 9.5, color: h?.online == true || h?.isGroup == true ? Brand.blue : Brand.mutedC)),
            ])),
            _headBtn(Lu.sparkles, onTap: () => showAssistantSheet(context, widget.id, onGoTo: _goToMessage)),
            if (canCall) ...[
              _headBtn(Lu.phone, onTap: () => _call(video: false)),
              if (calls.me?.video ?? true) _headBtn(Lu.video, fill: true, onTap: () => _call(video: true)),
            ],
          ]),
        ),
        if (_catchUpSince != null)
          InkWell(
            onTap: _openCatchUp,
            child: Container(
              width: double.infinity,
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
              color: Brand.soft,
              child: Row(children: [
                const Icon(Lu.sparkles, size: 14, color: Brand.blue), const SizedBox(width: 6),
                Expanded(child: Text(L(context, 'ما فاتك · ${widget.unread} رسالة', 'What you missed · ${widget.unread} messages'), style: const TextStyle(fontSize: 11, fontWeight: FontWeight.w600, color: Brand.blue))),
                GestureDetector(onTap: () => setState(() => _catchUpSince = null), child: const Icon(Lu.x, size: 14, color: Brand.mutedC)),
              ]),
            ),
          ),
        if (_pinned.isNotEmpty)
          GestureDetector(onTap: _showPinned, child: Container(
            width: double.infinity,
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 6),
            decoration: const BoxDecoration(color: Color(0xFFFAFBFE), border: Border(bottom: BorderSide(color: Brand.lineC))),
            child: Row(children: [
              const Icon(Lu.pin, size: 12, color: Brand.blue),
              const SizedBox(width: 6),
              Expanded(child: Text(_pinned.first.body, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 9.5, color: Brand.mutedC))),
              if (_pinned.length > 1) Text(' +${_pinned.length - 1}', style: const TextStyle(fontSize: 9.5, color: Brand.blue)),
            ]),
          )),
        Expanded(
          child: _loading && _msgs.isEmpty
              ? const Center(child: CircularProgressIndicator())
              : _msgs.isEmpty
                  ? EmptyNote(icon: Lu.messageCircle, title: L(context, 'لا رسائل بعد', 'No messages yet'), text: L(context, 'اكتب أول رسالة.', 'Write the first message.'))
                  : _thread(context),
        ),
        _composer(context),
      ]),
    );
  }

  Widget _headBtn(IconData i, {bool fill = false, required VoidCallback onTap}) => GestureDetector(
        onTap: onTap,
        child: Container(
          width: 34, height: 34, margin: const EdgeInsetsDirectional.only(start: 6), alignment: Alignment.center,
          decoration: BoxDecoration(color: fill ? Brand.navy : Brand.paper, borderRadius: BorderRadius.circular(11), border: Border.all(color: fill ? Brand.navy : Brand.lineC)),
          child: Icon(i, size: 16, color: fill ? Colors.white : Brand.inkC),
        ),
      );

  Widget _thread(BuildContext context) {
    final items = <Widget>[];
    DateTime? lastDay;
    for (final m in _msgs) {
      final d = DateTime(m.createdAt.year, m.createdAt.month, m.createdAt.day);
      if (lastDay == null || d != lastDay) { items.add(_day(context, d)); lastDay = d; }
      items.add(_message(context, m));
    }
    // reversed: the newest at the bottom, and the list starts there
    return ListView(controller: _scroll, reverse: true, padding: const EdgeInsets.fromLTRB(12, 10, 12, 10), children: items.reversed.toList());
  }

  Widget _day(BuildContext context, DateTime d) {
    final now = DateTime.now(), today = DateTime(now.year, now.month, now.day);
    final label = d == today ? L(context, 'اليوم', 'Today') : today.difference(d).inDays == 1 ? L(context, 'أمس', 'Yesterday') : dayMonth(context, d);
    return Padding(
      padding: const EdgeInsets.fromLTRB(0, 4, 0, 10),
      child: Row(children: [
        const Expanded(child: Divider(color: Brand.lineC)),
        Padding(padding: const EdgeInsets.symmetric(horizontal: 8), child: Text(label, style: const TextStyle(fontSize: 9, fontWeight: FontWeight.w600, color: Brand.mutedC))),
        const Expanded(child: Divider(color: Brand.lineC)),
      ]),
    );
  }

  /// Mentions of a member (in either language) as chips, *bold* as bold; the rest as typed.
  List<InlineSpan> _spans(String text, bool mine) {
    final names = <String>{for (final m in _head?.members ?? const <ChatMember>[]) ...[m.name, if (m.altName != null) m.altName!]}.where((n) => n.isNotEmpty).toList()
      ..sort((a, b) => b.length.compareTo(a.length));
    // the composer's block marks, as the browser draws them: a list item as a bullet, a quote as a bar, no fences
    text = text.replaceAll('```', '').replaceAllMapped(RegExp(r'(^|\n)- '), (x) => '${x[1]}• ').replaceAllMapped(RegExp(r'(^|\n)> '), (x) => '${x[1]}│ ');
    final parts = [if (names.isNotEmpty) '@(?:${names.map(RegExp.escape).join('|')})', r'\*[^*\n]+\*', r'`[^`\n]+`', r'(?<!\w)_[^_\n]+_(?!\w)', r'~[^~\n]+~'];
    final re = RegExp(parts.join('|'));
    final out = <InlineSpan>[];
    var i = 0;
    for (final x in re.allMatches(text)) {
      if (x.start > i) out.add(TextSpan(text: text.substring(i, x.start)));
      final t = x.group(0)!;
      if (t.startsWith('@')) {
        out.add(WidgetSpan(alignment: PlaceholderAlignment.middle, child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 1),
          decoration: BoxDecoration(color: mine ? Colors.white.withValues(alpha: .2) : Brand.soft, borderRadius: BorderRadius.circular(6)),
          child: Text(t, style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600, color: mine ? Colors.white : Brand.navy)),
        )));
      } else if (t.startsWith('`')) {
        out.add(WidgetSpan(alignment: PlaceholderAlignment.middle, child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 1),
          decoration: BoxDecoration(color: mine ? Colors.white.withValues(alpha: .15) : Brand.soft, borderRadius: BorderRadius.circular(5)),
          child: Text(t.substring(1, t.length - 1), textDirection: TextDirection.ltr, style: TextStyle(fontFamily: 'monospace', fontSize: 10.5, color: mine ? Colors.white : Brand.navy)),
        )));
      } else {
        final inner = t.substring(1, t.length - 1);
        out.add(TextSpan(text: inner, style: t.startsWith('*') ? const TextStyle(fontWeight: FontWeight.w700) : t.startsWith('_') ? const TextStyle(fontStyle: FontStyle.italic) : const TextStyle(decoration: TextDecoration.lineThrough)));
      }
      i = x.end;
    }
    if (i < text.length) out.add(TextSpan(text: text.substring(i)));
    return out;
  }

  Widget _message(BuildContext context, ChatMsg m) {
    final mine = m.mine;
    final fg = mine ? Colors.white : Brand.inkC;
    final muted = mine ? const Color(0xFFC9D4EA) : Brand.mutedC;
    final read = mine && (_head?.isGroup == true ? (_head!.reads.entries.where((e) => e.key != m.senderId).any((e) => e.value >= m.id)) : (_head?.otherReadMessageId ?? 0) >= m.id);
    final meta = m.meta;
    final hhmm = '${m.createdAt.hour.toString().padLeft(2, '0')}:${m.createdAt.minute.toString().padLeft(2, '0')}';
    final content = <Widget>[];
    if (_head?.isGroup == true && !mine) content.add(Text(m.senderName, style: const TextStyle(fontSize: 9.5, fontWeight: FontWeight.w600, color: Brand.blue)));
    if (meta?.forwarded == true) {
      content.add(Row(mainAxisSize: MainAxisSize.min, children: [Icon(Lu.forward, size: 11, color: muted), const SizedBox(width: 3),
        Text(meta!.forwardedFrom == null ? L(context, 'محوّلة', 'Forwarded') : L(context, 'محوّلة من ${meta.forwardedFrom}', 'Forwarded from ${meta.forwardedFrom}'), style: TextStyle(fontSize: 9, color: muted))]));
    }
    if (m.replyToId != null) {
      content.add(Container(
        margin: const EdgeInsets.only(bottom: 4),
        padding: const EdgeInsets.fromLTRB(8, 4, 8, 4),
        decoration: BoxDecoration(color: mine ? Colors.white.withValues(alpha: .12) : Brand.soft, borderRadius: BorderRadius.circular(8),
            border: BorderDirectional(start: BorderSide(color: mine ? Colors.white : Brand.blue, width: 2))),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(m.replyToSender ?? '', style: TextStyle(fontSize: 9.5, fontWeight: FontWeight.w600, color: mine ? Colors.white : Brand.blue)),
          Text(m.replyToBody ?? '', maxLines: 2, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 10, color: muted)),
        ]),
      ));
    }
    if (m.deleted) {
      content.add(Text(L(context, 'حُذفت هذه الرسالة', 'This message was deleted'), style: TextStyle(fontSize: 11, fontStyle: FontStyle.italic, color: muted)));
    } else {
      final poll = meta?.poll;
      if (poll != null) {
        content.add(_poll(context, m, poll));
      } else {
        if (m.attachmentPath != null) content.add(_attachment(context, m, mine));
        if (m.text.isNotEmpty) content.add(Text.rich(TextSpan(children: _spans(m.text, mine)), style: TextStyle(fontSize: 11.5, height: 1.6, color: fg)));
        if (_translations.containsKey(m.id)) {
          final tr = _translations[m.id];
          content.add(Container(
            margin: const EdgeInsets.only(top: 5),
            padding: const EdgeInsets.only(top: 5),
            decoration: BoxDecoration(border: Border(top: BorderSide(color: muted.withValues(alpha: .5)))),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Row(mainAxisSize: MainAxisSize.min, children: [Icon(Lu.languages, size: 11, color: muted), const SizedBox(width: 4), Text(tr == null ? L(context, 'جارٍ الترجمة…', 'Translating…') : L(context, 'الترجمة', 'Translation'), style: TextStyle(fontSize: 9.5, color: muted))]),
              if (tr != null) Text(tr, style: TextStyle(fontSize: 11.5, height: 1.6, color: fg)),
            ]),
          ));
        }
      }
    }
    content.add(Row(mainAxisSize: MainAxisSize.min, children: [
      if (meta?.pinned == true) Padding(padding: const EdgeInsetsDirectional.only(end: 3), child: Icon(Lu.pin, size: 10, color: muted)),
      if (m.editedAt != null) Text(L(context, 'معدّلة · ', 'edited · '), style: TextStyle(fontSize: 8.5, color: muted)),
      Text(hhmm, style: TextStyle(fontSize: 8.5, color: muted)),
      if (mine) Padding(padding: const EdgeInsetsDirectional.only(start: 3), child: Icon(read ? Lu.checkCheck : Lu.check, size: 11, color: muted)),
    ]));
    if ((meta?.threadCount ?? 0) > 0) {
      content.add(GestureDetector(
        onTap: () => showThreadSheet(context, widget.id, m).then((_) => _meta([m])),
        child: Container(
        margin: const EdgeInsets.only(top: 4),
        padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
        decoration: BoxDecoration(color: mine ? Colors.white.withValues(alpha: .15) : Brand.soft, borderRadius: BorderRadius.circular(8)),
        child: Row(mainAxisSize: MainAxisSize.min, children: [
          Icon(Lu.messagesSquare, size: 12, color: mine ? Colors.white : Brand.blue), const SizedBox(width: 4),
          Text(meta!.threadCount == 1 ? L(context, 'رد واحد', '1 reply') : meta.threadCount == 2 ? L(context, 'ردّان', '2 replies') : L(context, '${meta.threadCount} ردود', '${meta.threadCount} replies'), style: TextStyle(fontSize: 9.5, color: mine ? Colors.white : Brand.blue)),
        ]),
      )));
    }
    final bubble = Container(
      constraints: BoxConstraints(maxWidth: MediaQuery.of(context).size.width * .76),
      padding: const EdgeInsets.fromLTRB(10, 7, 10, 5),
      decoration: BoxDecoration(
        color: mine ? Brand.navy : Brand.paper,
        borderRadius: BorderRadiusDirectional.only(
          topStart: const Radius.circular(16), topEnd: const Radius.circular(16),
          bottomStart: Radius.circular(mine ? 16 : 5), bottomEnd: Radius.circular(mine ? 5 : 16),
        ),
        boxShadow: const [BoxShadow(color: Color(0x0F1F2D4D), blurRadius: 1, offset: Offset(0, 1))],
      ),
      child: Column(crossAxisAlignment: mine ? CrossAxisAlignment.end : CrossAxisAlignment.start, children: content),
    );
    final reactions = m.reactions.isEmpty
        ? null
        : Wrap(spacing: 4, children: [
            for (final r in m.reactions)
              GestureDetector(
                onTap: () async { if (await _api.reactChat(m.id, r.emoji)) _refresh(); },
                child: Container(
                  padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 1),
                  decoration: BoxDecoration(color: r.mine ? Brand.soft : Brand.paper, borderRadius: BorderRadius.circular(10), border: Border.all(color: r.mine ? Brand.blue : Brand.lineC)),
                  child: Text('${r.emoji} ${r.count}', style: const TextStyle(fontSize: 10)),
                ),
              ),
          ]);
    return Padding(
      padding: const EdgeInsets.only(bottom: 7),
      child: GestureDetector(
        onLongPress: () => _longPress(m),
        child: Row(
          mainAxisAlignment: mine ? MainAxisAlignment.end : MainAxisAlignment.start,
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            if (!mine) ...[PersonAvatar(photo: m.senderAvatar, name: m.senderName, size: 24), const SizedBox(width: 6)],
            Flexible(child: Column(crossAxisAlignment: mine ? CrossAxisAlignment.end : CrossAxisAlignment.start, children: [
              bubble,
              if (reactions != null) Padding(padding: const EdgeInsets.only(top: 3), child: reactions),
            ])),
          ],
        ),
      ),
    );
  }

  Widget _poll(BuildContext context, ChatMsg m, ChatPoll p) {
    final total = p.options.fold<int>(0, (a, o) => a + o.votes);
    return Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      Text(p.question, style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: m.mine ? Colors.white : Brand.inkC)),
      for (final o in p.options)
        GestureDetector(
          onTap: p.closed
              ? null
              : () async {
                  final mineNow = [for (final x in p.options) if (x.mine) x.id];
                  final pick = p.multi ? (mineNow.contains(o.id) ? (mineNow..remove(o.id)) : [...mineNow, o.id]) : (o.mine ? <int>[] : [o.id]);
                  if (await _api.voteChat(m.id, pick)) _meta([m]);
                },
          child: Container(
            margin: const EdgeInsets.only(top: 4),
            clipBehavior: Clip.antiAlias,
            decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(9), border: Border.all(color: o.mine ? Brand.blue : Brand.lineC)),
            child: Stack(children: [
              Positioned.fill(child: FractionallySizedBox(alignment: AlignmentDirectional.centerStart, widthFactor: total == 0 ? 0 : o.votes / total, child: Container(color: Brand.soft))),
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 5),
                child: Row(children: [
                  if (o.mine) const Padding(padding: EdgeInsetsDirectional.only(end: 4), child: Icon(Lu.check, size: 12, color: Brand.blue)),
                  Expanded(child: Text(o.text, style: const TextStyle(fontSize: 10.5, color: Brand.inkC))),
                  Text('${o.votes}', style: const TextStyle(fontSize: 10.5, fontWeight: FontWeight.w700, color: Brand.inkC)),
                ]),
              ),
            ]),
          ),
        ),
      if (p.closed) Padding(padding: const EdgeInsets.only(top: 4), child: Text(L(context, 'أُغلق التصويت', 'Voting closed'), style: TextStyle(fontSize: 9, color: m.mine ? Colors.white70 : Brand.mutedC))),
    ]);
  }

  Widget _attachment(BuildContext context, ChatMsg m, bool mine) {
    final url = _api.chatFileUrl(m.id);
    switch (m.attachmentType) {
      case 'image':
        return GestureDetector(
          onTap: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => Scaffold(backgroundColor: Colors.black, appBar: AppBar(backgroundColor: Colors.black, foregroundColor: Colors.white),
              body: Center(child: InteractiveViewer(child: Image.network(url, headers: _api.authHeaders)))))),
          child: Padding(
            padding: const EdgeInsets.only(bottom: 4),
            child: ClipRRect(borderRadius: BorderRadius.circular(10), child: Image.network(url, headers: _api.authHeaders, width: 200, fit: BoxFit.cover,
                errorBuilder: (_, __, ___) => const SizedBox(width: 200, height: 80, child: Icon(Lu.imageOff, color: Brand.mutedC)))),
          ),
        );
      case 'video':
        return GestureDetector(
          onTap: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => _VideoScreen(url: url))),
          child: Container(
            width: 200, height: 120, margin: const EdgeInsets.only(bottom: 4), alignment: Alignment.center,
            decoration: BoxDecoration(color: const Color(0xFF0C1837), borderRadius: BorderRadius.circular(12)),
            child: Column(mainAxisSize: MainAxisSize.min, children: [
              Container(width: 44, height: 44, alignment: Alignment.center, decoration: const BoxDecoration(color: Colors.white24, shape: BoxShape.circle), child: const Icon(Lu.play, color: Colors.white, size: 22)),
              const SizedBox(height: 6),
              Text(L(context, 'رسالة فيديو', 'Video message'), style: const TextStyle(fontSize: 10.5, color: Colors.white70)),
            ]),
          ),
        );
      case 'audio':
        return Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisSize: MainAxisSize.min, children: [
          _VoicePlayer(url: url, mine: mine),
          _transcript(context, m, mine),
        ]);
      default:
        return Container(
          margin: const EdgeInsets.only(bottom: 4),
          padding: const EdgeInsets.all(8),
          decoration: BoxDecoration(color: mine ? Colors.white.withValues(alpha: .12) : Brand.soft, borderRadius: BorderRadius.circular(10)),
          child: Row(mainAxisSize: MainAxisSize.min, children: [
            Icon(Lu.fileText, size: 18, color: mine ? Colors.white : Brand.blue),
            const SizedBox(width: 6),
            Flexible(child: Text(m.attachmentName ?? L(context, 'ملف', 'File'), maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 11, color: mine ? Colors.white : Brand.inkC))),
          ]),
        );
    }
  }

  // ------------------------------------------------------------------------------------------ speech to text
  final Set<int> _transcribing = {};

  Future<void> _transcribe(ChatMsg m) async {
    setState(() => _transcribing.add(m.id));
    final (status, text) = await _api.transcribeChat(m.id);
    if (!mounted) return;
    setState(() {
      _transcribing.remove(m.id);
      if (status == 'done' || status == 'too_long') {
        m.meta ??= ChatMeta();
        m.meta!.transcriptStatus = status;
        m.meta!.transcriptText = text;
      }
    });
    if (status != 'done' && status != 'too_long') {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(L(context, 'النص غير متاح الآن — حاول لاحقاً', 'The text is not available right now — try again later'))));
    }
  }

  /// Under a voice message: «عرض النص», then the words (transcribed once on the server, for everyone).
  Widget _transcript(BuildContext context, ChatMsg m, bool mine) {
    final fg = mine ? Colors.white : Brand.navy;
    final st = m.meta?.transcriptStatus;
    if (st == 'done') {
      final t = (m.meta!.transcriptText ?? '').trim();
      return Container(
        margin: const EdgeInsets.only(top: 6),
        padding: const EdgeInsetsDirectional.only(start: 8),
        constraints: const BoxConstraints(maxWidth: 240),
        decoration: BoxDecoration(border: BorderDirectional(start: BorderSide(color: fg.withValues(alpha: .6), width: 2))),
        child: Text(t.isEmpty ? L(context, 'لم تُسمع كلمات في هذا التسجيل', 'No words were heard in this recording') : t,
            style: TextStyle(fontSize: 11.5, height: 1.6, color: mine ? Colors.white : Brand.inkC, fontStyle: t.isEmpty ? FontStyle.italic : FontStyle.normal)),
      );
    }
    if (st == 'too_long') {
      return Padding(padding: const EdgeInsets.only(top: 6), child: Text(L(context, 'أطول من أن تُحوَّل إلى نص (دقيقة ونصف على الأكثر)', 'Too long to turn into text (a minute and a half at most)'), style: TextStyle(fontSize: 10.5, color: mine ? Colors.white70 : Brand.mutedC)));
    }
    final busy = _transcribing.contains(m.id);
    return GestureDetector(
      onTap: busy ? null : () => _transcribe(m),
      child: Container(
        margin: const EdgeInsets.only(top: 6),
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
        decoration: BoxDecoration(color: mine ? Colors.white.withValues(alpha: .16) : Brand.soft, borderRadius: BorderRadius.circular(8)),
        child: Row(mainAxisSize: MainAxisSize.min, children: [
          busy ? SizedBox(width: 12, height: 12, child: CircularProgressIndicator(strokeWidth: 1.5, color: fg)) : Icon(Lu.captions, size: 13, color: fg),
          const SizedBox(width: 5),
          Text(busy ? L(context, 'جارٍ تحويلها إلى نص…', 'Turning it into text…') : L(context, 'عرض النص', 'Show the text'),
              style: TextStyle(fontSize: 10.5, fontWeight: FontWeight.w600, color: fg)),
        ]),
      ),
    );
  }

  Widget _composer(BuildContext context) {
    final typing = _box.text.trim().isNotEmpty;
    final secs = _recStart == null ? 0 : DateTime.now().difference(_recStart!).inSeconds;
    return Container(
      color: Brand.page,
      padding: const EdgeInsets.fromLTRB(10, 6, 10, 10),
      child: Column(mainAxisSize: MainAxisSize.min, children: [
        if (_typingName != null)
          Padding(padding: const EdgeInsets.only(bottom: 4), child: Align(alignment: AlignmentDirectional.centerStart,
              child: Text(L(context, '$_typingName يكتب…', '$_typingName is typing…'), style: const TextStyle(fontSize: 10.5, color: Brand.blue, fontStyle: FontStyle.italic)))),
        if (_mentionHits.isNotEmpty)
          Container(
            margin: const EdgeInsets.only(bottom: 6),
            decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(12), border: Border.all(color: Brand.lineC)),
            child: Column(children: [
              for (final m in _mentionHits)
                InkWell(onTap: () => _pickMention(m), child: Padding(padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7), child: Row(children: [
                  PersonAvatar(photo: m.avatar, name: m.name, size: 26), const SizedBox(width: 8),
                  Expanded(child: Text(m.name, style: const TextStyle(fontSize: 12))),
                ]))),
            ]),
          ),
        if (_replyTo != null)
          Container(
            margin: const EdgeInsets.only(bottom: 6),
            padding: const EdgeInsets.fromLTRB(10, 6, 6, 6),
            decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(10), border: const BorderDirectional(start: BorderSide(color: Brand.blue, width: 3))),
            child: Row(children: [
              Expanded(child: Text('${L(context, 'رد على', 'Replying to')} ${_replyTo!.senderName}: ${_replyTo!.text}', maxLines: 1, overflow: TextOverflow.ellipsis, style: T.small)),
              GestureDetector(onTap: () => setState(() => _replyTo = null), child: const Icon(Lu.x, size: 16, color: Brand.mutedC)),
            ]),
          ),
        Row(children: [
          Expanded(
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 8),
              decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(14), border: Border.all(color: _recording ? Brand.badC : _draftPath != null ? Brand.blue : Brand.lineC)),
              child: _draftPath != null
                  ? _draftRow(context)
                  : _recording
                  ? SizedBox(height: 42, child: Row(children: [
                      const Icon(Lu.mic, size: 17, color: Brand.badC),
                      const SizedBox(width: 6),
                      Text('${L(context, 'جارٍ التسجيل', 'Recording')} · ${secs ~/ 60}:${(secs % 60).toString().padLeft(2, '0')}', style: const TextStyle(fontSize: 11, color: Brand.badC)),
                      const Spacer(),
                      GestureDetector(onTap: _cancelVoice, child: Text(L(context, 'إلغاء', 'Cancel'), style: const TextStyle(fontSize: 11, color: Brand.mutedC))),
                    ]))
                  : Row(children: [
                      GestureDetector(onTap: _plus, child: const Icon(Lu.plus, size: 18, color: Brand.mutedC)),
                      const SizedBox(width: 6),
                      Expanded(child: TextField(
                        controller: _box, minLines: 1, maxLines: 5, textInputAction: TextInputAction.newline,
                        style: const TextStyle(fontSize: 12),
                        decoration: InputDecoration(hintText: L(context, 'اكتب رسالة', 'Write a message'), hintStyle: const TextStyle(fontSize: 11.5, color: Brand.mutedC),
                            border: InputBorder.none, enabledBorder: InputBorder.none, focusedBorder: InputBorder.none, filled: false, isDense: true, contentPadding: const EdgeInsets.symmetric(vertical: 12)),
                      )),
                      GestureDetector(onTap: () => _attach(), child: const Icon(Lu.paperclip, size: 17, color: Brand.mutedC)),
                    ]),
            ),
          ),
          const SizedBox(width: 6),
          if (_draftPath != null)
            _sendBtn(Lu.sendHorizontal, onTap: _sendDraft)
          else if (!typing)
            _sendBtn(_recording ? Lu.square : Lu.mic, soft: !_recording, onTap: _voice)
          else
            _sendBtn(Lu.sendHorizontal, onTap: () => _send()),
        ]),
      ]),
    );
  }

  /// The recording waiting for review: delete · play/pause · progress · length.
  Widget _draftRow(BuildContext context) {
    final v = _draftPlayer?.value;
    final total = (v != null && v.isInitialized && v.duration > Duration.zero) ? v.duration : Duration(seconds: _draftSecs);
    final pos = v?.position ?? Duration.zero;
    final playing = v?.isPlaying ?? false;
    String t(Duration d) => '${d.inMinutes}:${(d.inSeconds % 60).toString().padLeft(2, '0')}';
    return SizedBox(height: 42, child: Row(children: [
      GestureDetector(onTap: () => _dropDraft(), child: const Padding(padding: EdgeInsets.all(4), child: Icon(Lu.trash2, size: 18, color: Brand.badC))),
      const SizedBox(width: 6),
      GestureDetector(
        onTap: () async {
          final p = _draftPlayer; if (p == null || !p.value.isInitialized) return;
          if (p.value.isPlaying) { await p.pause(); } else { if (p.value.position >= p.value.duration) await p.seekTo(Duration.zero); await p.play(); }
        },
        child: Container(width: 30, height: 30, alignment: Alignment.center, decoration: const BoxDecoration(color: Brand.navy, shape: BoxShape.circle),
            child: Icon(playing ? Lu.pause : Lu.play, size: 14, color: Colors.white)),
      ),
      const SizedBox(width: 8),
      Expanded(child: ClipRRect(borderRadius: BorderRadius.circular(2), child: LinearProgressIndicator(
          value: total.inMilliseconds == 0 ? 0 : (pos.inMilliseconds / total.inMilliseconds).clamp(0.0, 1.0), minHeight: 4,
          backgroundColor: Brand.lineC, color: Brand.blue))),
      const SizedBox(width: 8),
      Text(playing || pos > Duration.zero ? '${t(pos)} / ${t(total)}' : t(total), style: const TextStyle(fontSize: 10.5, color: Brand.mutedC)),
    ]));
  }

  Widget _sendBtn(IconData i, {bool soft = false, required VoidCallback onTap}) => GestureDetector(
        onTap: _sending ? null : onTap,
        child: Container(
          width: 42, height: 42, alignment: Alignment.center,
          decoration: BoxDecoration(color: soft ? Brand.soft : Brand.navy, borderRadius: BorderRadius.circular(12)),
          child: _sending && !soft
              ? const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
              : Icon(i, size: 17, color: soft ? Brand.navy : Colors.white),
        ),
      );
}

/// A video message, full screen (tap the picture to pause or play).
class _VideoScreen extends StatefulWidget {
  final String url;
  const _VideoScreen({required this.url});
  @override
  State<_VideoScreen> createState() => _VideoScreenState();
}

class _VideoScreenState extends State<_VideoScreen> {
  VideoPlayerController? _v;
  bool _failed = false;
  VideoPlayerController get _c => _v!;
  @override
  void initState() {
    super.initState();
    _open();
  }
  Future<void> _open() async {
    final f = await ApiService.instance.mediaFile(widget.url);   // see mediaFile: the player cannot fetch it itself
    if (!mounted) return;
    if (f == null) { setState(() => _failed = true); return; }
    final c = VideoPlayerController.file(f);
    _v = c;
    c.addListener(() { if (mounted) setState(() {}); });
    try { await c.initialize(); if (mounted) { setState(() {}); await c.play(); } } catch (_) { if (mounted) setState(() => _failed = true); }
  }
  @override
  void dispose() { _v?.dispose(); super.dispose(); }
  @override
  Widget build(BuildContext context) => Scaffold(
        backgroundColor: Colors.black,
        appBar: AppBar(backgroundColor: Colors.black, foregroundColor: Colors.white, title: Text(L(context, 'رسالة فيديو', 'Video message'), style: const TextStyle(fontSize: 14))),
        body: Center(
          child: _failed
              ? Text(L(context, 'تعذّر تشغيل الفيديو', 'The video could not be played'), style: const TextStyle(color: Colors.white70))
              : _v == null || !_c.value.isInitialized
                  ? const CircularProgressIndicator(color: Colors.white)
                  : GestureDetector(
                      onTap: () => _c.value.isPlaying ? _c.pause() : _c.play(),
                      child: AspectRatio(aspectRatio: _c.value.aspectRatio, child: Stack(alignment: Alignment.bottomCenter, children: [
                        VideoPlayer(_c),
                        VideoProgressIndicator(_c, allowScrubbing: true, colors: const VideoProgressColors(playedColor: Brand.blue, bufferedColor: Colors.white38, backgroundColor: Colors.white12)),
                      ])),
                    ),
        ),
      );
}

/// A voice message: play/pause, the progress, the length.
class _VoicePlayer extends StatefulWidget {
  final String url;
  final bool mine;
  const _VoicePlayer({required this.url, required this.mine});
  @override
  State<_VoicePlayer> createState() => _VoicePlayerState();
}

class _VoicePlayerState extends State<_VoicePlayer> {
  VideoPlayerController? _c;
  bool _failed = false;

  Future<void> _toggle() async {
    if (_c == null) {
      final f = await ApiService.instance.mediaFile(widget.url);   // see mediaFile: the player cannot fetch it itself
      if (f == null) { if (mounted) setState(() => _failed = true); return; }
      final c = VideoPlayerController.file(f);
      _c = c;
      c.addListener(() { if (mounted) setState(() {}); });
      try { await c.initialize(); } catch (_) { if (mounted) setState(() => _failed = true); return; }
    }
    final c = _c!;
    if (c.value.isPlaying) {
      await c.pause();
    } else {
      if (c.value.position >= c.value.duration) await c.seekTo(Duration.zero);
      await c.play();
    }
  }

  @override
  void dispose() { _c?.dispose(); super.dispose(); }

  @override
  Widget build(BuildContext context) {
    final v = _c?.value;
    final fg = widget.mine ? Colors.white : Brand.navy;
    final dur = v?.duration ?? Duration.zero, pos = v?.position ?? Duration.zero;
    String t(Duration d) => '${d.inMinutes}:${(d.inSeconds % 60).toString().padLeft(2, '0')}';
    return SizedBox(
      width: 190,
      child: Row(children: [
        GestureDetector(
          onTap: _failed ? null : _toggle,
          child: Container(width: 30, height: 30, alignment: Alignment.center,
              decoration: BoxDecoration(color: widget.mine ? Colors.white.withValues(alpha: .18) : Brand.soft, shape: BoxShape.circle),
              child: Icon(_failed ? Lu.circleAlert : (v?.isPlaying ?? false) ? Lu.pause : Lu.play, size: 15, color: fg)),
        ),
        const SizedBox(width: 8),
        Expanded(child: ClipRRect(borderRadius: BorderRadius.circular(2), child: LinearProgressIndicator(
          value: dur.inMilliseconds == 0 ? 0 : pos.inMilliseconds / dur.inMilliseconds, minHeight: 3,
          backgroundColor: widget.mine ? Colors.white24 : Brand.lineC, color: fg))),
        const SizedBox(width: 8),
        Text(dur == Duration.zero ? '·:··' : t((v?.isPlaying ?? false) ? pos : dur), style: TextStyle(fontSize: 9.5, color: widget.mine ? Colors.white70 : Brand.mutedC)),
      ]),
    );
  }
}
