import 'dart:async';

import 'package:flutter/material.dart';

import '../../models/chat.dart';
import '../../services/api_service.dart';
import '../../theme/brand.dart';
import '../../widgets/ui.dart';
import '../calls/calls_screen.dart';
import 'chat_screen.dart';

/// المحادثات — exactly the approved prototype (design-output/executive-prototype, ?page=chats): the back link, the
/// heading «داخل الشركة فقط», the search over conversations and messages, the four tabs (الكل · غير مقروءة · المجموعات ·
/// المثبّتة), each conversation as a row (face with presence, name, «جارية», time, the last line with its mark, the
/// unread count), then «محادثة جديدة» and «المكالمات».
class ChatsScreen extends StatefulWidget {
  const ChatsScreen({super.key});
  @override
  State<ChatsScreen> createState() => _ChatsScreenState();
}

class _ChatsScreenState extends State<ChatsScreen> {
  List<ChatConv>? _convs;
  List<ChatHit>? _hits;
  int _tab = 0;
  String _q = '';
  bool _failed = false;
  Timer? _poll, _debounce;

  @override
  void initState() {
    super.initState();
    _load();
    _poll = Timer.periodic(const Duration(seconds: 10), (_) => _load());
  }

  @override
  void dispose() {
    _poll?.cancel();
    _debounce?.cancel();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final list = await ApiService.instance.getChats();
      if (mounted) setState(() { _convs = list; _failed = false; });
    } catch (_) {
      if (mounted && _convs == null) setState(() => _failed = true);
    }
  }

  void _search(String q) {
    _q = q.trim();
    _debounce?.cancel();
    if (_q.length < 2) { setState(() => _hits = null); return; }
    _debounce = Timer(const Duration(milliseconds: 350), () async {
      try {
        final h = await ApiService.instance.findChat(_q);
        if (mounted) setState(() => _hits = h);
      } catch (_) {}
    });
  }

  Future<void> _open(int id, {String? title, int? otherId, int? at}) async {
    await Navigator.of(context).push(MaterialPageRoute(builder: (_) => ChatScreen(id: id, title: title, otherId: otherId, focusMessageId: at)));
    _load();
  }

  Future<void> _newChat() async {
    final person = await showModalBottomSheet<ChatPerson>(context: context, isScrollControlled: true, backgroundColor: Brand.page,
        shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(22))), builder: (_) => const _PeoplePicker());
    if (person == null || !mounted) return;
    final id = await ApiService.instance.chatWith(person.id);
    if (id != null && mounted) _open(id, title: person.name, otherId: person.id);
  }

  @override
  Widget build(BuildContext context) {
    final all = _convs ?? const <ChatConv>[];
    final unread = all.where((c) => c.unread > 0).length;
    final q = _q.toLowerCase();
    var shown = switch (_tab) { 1 => all.where((c) => c.unread > 0), 2 => all.where((c) => c.isGroup), 3 => all.where((c) => c.pinned), _ => all }.toList();
    if (q.isNotEmpty) shown = shown.where((c) => c.title.toLowerCase().contains(q)).toList();
    return ExecFrame(
      bottomBar: const CallsNav(),
      child: ExecBody(onRefresh: _load, children: [
        BackLink(L(context, 'مساحات العمل', 'Workspaces')),
        PageHead(L(context, 'المحادثات', 'Chats'), eyebrow: L(context, 'داخل الشركة فقط', 'Inside the company only')),
        _SearchBox(hint: L(context, 'ابحث في المحادثات والرسائل والأشخاص', 'Search chats, messages and people'), onChanged: _search),
        ChipTabs(
          items: [L(context, 'الكل', 'All'), unread > 0 ? L(context, 'غير مقروءة · $unread', 'Unread · $unread') : L(context, 'غير مقروءة', 'Unread'), L(context, 'المجموعات', 'Groups'), L(context, 'المثبّتة', 'Pinned')],
          index: _tab,
          onTap: (i) => setState(() => _tab = i),
        ),
        if (_failed)
          EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر التحميل', 'Could not load'), text: L(context, 'اسحب للأسفل لإعادة المحاولة.', 'Pull down to try again.'))
        else if (_convs == null)
          const Padding(padding: EdgeInsets.all(30), child: Center(child: CircularProgressIndicator()))
        else if (shown.isEmpty && (_hits?.isEmpty ?? true))
          EmptyNote(icon: Lu.messageCircle, title: L(context, 'لا توجد محادثات', 'No conversations'), text: L(context, 'ابدأ محادثة مع زميل من «محادثة جديدة».', 'Start one with a colleague from “New chat”.'))
        else if (shown.isNotEmpty)
          _card([for (final c in shown) _row(context, c)]),
        if (_hits != null && _hits!.isNotEmpty) ...[
          SectionHeading(L(context, 'في الرسائل', 'In messages')),
          _card([for (final h in _hits!) _hit(context, h)]),
        ],
        const SizedBox(height: 14),
        Row(children: [
          Expanded(child: _Btn(Lu.squarePen, L(context, 'محادثة جديدة', 'New chat'), primary: true, onTap: _newChat)),
          const SizedBox(width: 10),
          Expanded(child: _Btn(Lu.phone, L(context, 'المكالمات', 'Calls'), onTap: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => const CallsScreen())))),
        ]),
      ]),
    );
  }

  Widget _card(List<Widget> rows) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 14),
        decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(18), border: Border.all(color: Brand.lineC)),
        child: Column(children: [
          for (var i = 0; i < rows.length; i++) ...[if (i > 0) const Divider(height: 1, color: Brand.lineC), rows[i]],
        ]),
      );

  Widget _face(ChatConv c) => Stack(clipBehavior: Clip.none, children: [
        c.isGroup && (c.avatar == null)
            ? Container(width: 44, height: 44, alignment: Alignment.center, decoration: const BoxDecoration(color: Brand.soft, shape: BoxShape.circle),
                child: c.title.startsWith('#') ? const Text('#', style: TextStyle(fontSize: 17, fontWeight: FontWeight.w700, color: Brand.blue)) : const Icon(Lu.usersRound, size: 20, color: Brand.blue))
            : PersonAvatar(photo: c.avatar, name: c.title, size: 44),
        if (!c.isGroup)
          PositionedDirectional(bottom: 0, end: 0, child: Container(width: 11, height: 11,
              decoration: BoxDecoration(color: c.online ? Brand.blue : const Color(0xFFB3BCCB), shape: BoxShape.circle, border: Border.all(color: Brand.paper, width: 2)))),
      ]);

  Widget _row(BuildContext context, ChatConv c) {
    final p = c.preview ?? '';
    final missed = p.contains('فائتة') || p.toLowerCase().contains('missed');
    final voice = p.contains('صوتية') || p.toLowerCase().contains('voice');
    final lead = missed ? Lu.phoneMissed : voice ? Lu.mic : c.lastMine ? (c.lastSeen ? Lu.checkCheck : Lu.check) : null;
    return InkWell(
      onTap: () => _open(c.id, title: c.title, otherId: c.otherId),
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 11),
        child: Row(children: [
          _face(c),
          const SizedBox(width: 10),
          Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              Expanded(child: Text(c.title, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600, color: Brand.inkC))),
              if (c.pinned) const Padding(padding: EdgeInsetsDirectional.only(end: 4), child: Icon(Lu.pin, size: 11, color: Brand.mutedC)),
              if (c.muted) const Padding(padding: EdgeInsetsDirectional.only(end: 4), child: Icon(Lu.bellOff, size: 11, color: Brand.mutedC)),
              Text(_when(context, c.lastAt), style: TextStyle(fontSize: 9.5, color: c.unread > 0 ? Brand.blue : Brand.mutedC, fontWeight: c.unread > 0 ? FontWeight.w700 : FontWeight.w400)),
            ]),
            const SizedBox(height: 3),
            Row(children: [
              if (lead != null) Padding(padding: const EdgeInsetsDirectional.only(end: 3), child: Icon(lead, size: 12, color: missed ? Brand.badC : c.lastSeen ? Brand.blue : Brand.mutedC)),
              Expanded(child: Text(p, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 10.5, color: missed ? Brand.badC : Brand.mutedC))),
              if (c.unread > 0)
                Container(constraints: const BoxConstraints(minWidth: 18), height: 18, padding: const EdgeInsets.symmetric(horizontal: 5), alignment: Alignment.center,
                    decoration: BoxDecoration(color: c.muted ? Brand.support : Brand.blue, borderRadius: BorderRadius.circular(9)),
                    child: Text('${c.unread}', style: const TextStyle(fontSize: 9.5, color: Colors.white))),
            ]),
          ])),
        ]),
      ),
    );
  }

  Widget _hit(BuildContext context, ChatHit h) => InkWell(
        onTap: () => _open(h.conversationId, title: h.conversationTitle, at: h.messageId),
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 10),
          child: Row(children: [
            const IconBox(icon: Lu.search),
            const SizedBox(width: 10),
            Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text('${h.conversationTitle} · ${h.senderName}', maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600, color: Brand.inkC)),
              const SizedBox(height: 2),
              Text(h.body ?? '', maxLines: 2, overflow: TextOverflow.ellipsis, style: T.small),
            ])),
            Text(_when(context, h.createdAt), style: T.small),
          ]),
        ),
      );
}

/// Today: the time; yesterday: «أمس»; before: day/month.
String _when(BuildContext context, DateTime? d) {
  if (d == null) return '';
  final now = DateTime.now();
  final day = DateTime(d.year, d.month, d.day), today = DateTime(now.year, now.month, now.day);
  if (day == today) return '${d.hour.toString().padLeft(2, '0')}:${d.minute.toString().padLeft(2, '0')}';
  if (today.difference(day).inDays == 1) return L(context, 'أمس', 'Yesterday');
  return '${d.day}/${d.month}';
}

class _SearchBox extends StatelessWidget {
  final String hint;
  final ValueChanged<String> onChanged;
  const _SearchBox({required this.hint, required this.onChanged});
  @override
  Widget build(BuildContext context) => Container(
        margin: const EdgeInsets.only(bottom: 14),
        padding: const EdgeInsets.symmetric(horizontal: 14),
        decoration: BoxDecoration(color: Brand.paper, borderRadius: BorderRadius.circular(14), border: Border.all(color: Brand.lineC)),
        child: Row(children: [
          const Icon(Lu.search, size: 16, color: Brand.mutedC),
          const SizedBox(width: 8),
          Expanded(child: TextField(onChanged: onChanged, style: const TextStyle(fontSize: 12),
              decoration: InputDecoration(hintText: hint, hintStyle: const TextStyle(fontSize: 11.5, color: Brand.mutedC), border: InputBorder.none, enabledBorder: InputBorder.none, focusedBorder: InputBorder.none, filled: false, isDense: true, contentPadding: const EdgeInsets.symmetric(vertical: 13)))),
        ]),
      );
}

class _Btn extends StatelessWidget {
  final IconData icon;
  final String label;
  final bool primary;
  final VoidCallback onTap;
  const _Btn(this.icon, this.label, {this.primary = false, required this.onTap});
  @override
  Widget build(BuildContext context) => Material(
        color: primary ? Brand.navy : Brand.paper,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14), side: BorderSide(color: primary ? Brand.navy : Brand.lineC)),
        child: InkWell(
          borderRadius: BorderRadius.circular(14),
          onTap: onTap,
          child: Padding(
            padding: const EdgeInsets.symmetric(vertical: 13),
            child: Row(mainAxisAlignment: MainAxisAlignment.center, children: [
              Icon(icon, size: 16, color: primary ? Colors.white : Brand.navy),
              const SizedBox(width: 7),
              Text(label, style: TextStyle(fontSize: 12, fontWeight: FontWeight.w600, color: primary ? Colors.white : Brand.navy)),
            ]),
          ),
        ),
      );
}

/// «محادثة جديدة»: everyone a conversation can be started with, searchable, available first.
class _PeoplePicker extends StatefulWidget {
  const _PeoplePicker();
  @override
  State<_PeoplePicker> createState() => _PeoplePickerState();
}

class _PeoplePickerState extends State<_PeoplePicker> {
  List<ChatPerson>? _people;
  Timer? _debounce;
  @override
  void initState() { super.initState(); _load(''); }
  @override
  void dispose() { _debounce?.cancel(); super.dispose(); }
  Future<void> _load(String q) async {
    try {
      final p = await ApiService.instance.getChatPeople(q: q);
      p.sort((a, b) => (b.online ? 1 : 0) - (a.online ? 1 : 0));
      if (mounted) setState(() => _people = p);
    } catch (_) {
      if (mounted) setState(() => _people = const []);
    }
  }

  @override
  Widget build(BuildContext context) => SizedBox(
        height: MediaQuery.of(context).size.height * .78,
        child: Padding(
          padding: EdgeInsets.fromLTRB(20, 18, 20, MediaQuery.of(context).viewInsets.bottom + 10),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(L(context, 'محادثة جديدة', 'New chat'), style: T.h2),
            const SizedBox(height: 12),
            _SearchBox(hint: L(context, 'ابحث عن زميل', 'Find a colleague'), onChanged: (q) { _debounce?.cancel(); _debounce = Timer(const Duration(milliseconds: 300), () => _load(q.trim())); }),
            Expanded(
              child: _people == null
                  ? const Center(child: CircularProgressIndicator())
                  : _people!.isEmpty
                      ? EmptyNote(icon: Lu.usersRound, title: L(context, 'لا نتائج', 'No results'), text: L(context, 'جرّب اسماً آخر.', 'Try another name.'))
                      : ListView.separated(
                          itemCount: _people!.length,
                          separatorBuilder: (_, __) => const Divider(height: 1, color: Brand.lineC),
                          itemBuilder: (_, i) {
                            final x = _people![i];
                            return InkWell(
                              onTap: () => Navigator.of(context).pop(x),
                              child: Padding(
                                padding: const EdgeInsets.symmetric(vertical: 10),
                                child: Row(children: [
                                  Stack(clipBehavior: Clip.none, children: [
                                    PersonAvatar(photo: x.avatar, name: x.name, size: 40),
                                    PositionedDirectional(bottom: 0, end: 0, child: Container(width: 10, height: 10,
                                        decoration: BoxDecoration(color: x.online ? Brand.blue : const Color(0xFFB3BCCB), shape: BoxShape.circle, border: Border.all(color: Brand.page, width: 2)))),
                                  ]),
                                  const SizedBox(width: 10),
                                  Expanded(child: Text(x.name, style: T.body)),
                                  Text(x.online ? L(context, 'متاح', 'Available') : L(context, 'غير متصل', 'Offline'), style: TextStyle(fontSize: 10, color: x.online ? Brand.blue : Brand.mutedC)),
                                ]),
                              ),
                            );
                          },
                        ),
            ),
          ]),
        ),
      );
}
