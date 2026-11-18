import 'package:flutter/material.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';
import 'package:provider/provider.dart';

import '../../models/calls.dart';
import '../../providers/calls_provider.dart';
import '../../services/api_service.dart';
import '../../theme/brand.dart';
import '../../widgets/ui.dart';

/// The call itself, full screen and navy — exactly the approved prototype (design-output/executive-prototype):
///   ?page=call-incoming  the ringing card (patient, quick replies, اعتذار · بالصوت · قبول)
///   ?page=call-in        in a call: the other side full, me in the corner, the line «الاتصال جيد · سماعة الأذن …»
///                        and the bar الميكروفون · الكاميرا · قلب الكاميرا · تسجيل · إنهاء
///   ?page=call-rec-ask   I asked to record: «بانتظار موافقة …», the note of what is recorded, «سحب الطلب»
///   ?page=call-live      recording: REC and «إيقاف التسجيل»
///   ?page=call-consent   the other side asked: the consent sheet
///   ?page=call-group     the group grid, «إضافة مشارك · يرن الآن …», الميكروفون · الكاميرا · المشاركون · تسجيل · مغادرة
/// One route for all of them; it closes itself when nothing is left.
class CallStagePage extends StatefulWidget {
  const CallStagePage({super.key});
  @override
  State<CallStagePage> createState() => _CallStagePageState();
}

class _CallStagePageState extends State<CallStagePage> {
  late final CallsProvider _calls;
  String? _toast;

  @override
  void initState() {
    super.initState();
    _calls = context.read<CallsProvider>();
    _calls.notice.addListener(_onNotice);
  }

  void _onNotice() {
    final v = _calls.notice.value;
    if (v == null || !mounted) return;
    setState(() => _toast = v);
    Future.delayed(const Duration(seconds: 4), () { if (mounted && _toast == v) setState(() => _toast = null); });
  }

  @override
  void dispose() {
    _calls.notice.removeListener(_onNotice);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final p = context.watch<CallsProvider>();
    Widget body;
    if (p.ring != null) {
      body = _Ringing(p.ring!);
    } else if (p.group != null) {
      body = _GroupStage(p.group!);
    } else if (p.current != null) {
      body = _OneStage(p.current!);
    } else {
      body = const SizedBox();
    }
    final rec = p.group?.rec ?? p.current?.rec;
    return PopScope(
      canPop: !p.busy,
      child: Scaffold(
        backgroundColor: Brand.navy,
        body: Stack(children: [
          SafeArea(child: body),
          if (rec != null && rec.askingMe) ...[
            const Positioned.fill(child: ColoredBox(color: Color(0x8C0C1837))),
            Align(alignment: Alignment.bottomCenter, child: _ConsentSheet(rec, group: p.group != null)),
          ],
          if (_toast != null)
            Positioned(
              left: 18, right: 18, bottom: 132,
              child: Container(
                padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                decoration: BoxDecoration(color: const Color(0xE60C1837), borderRadius: BorderRadius.circular(12)),
                child: Text(_toast!, textAlign: TextAlign.center, style: const TextStyle(color: Colors.white, fontSize: 12)),
              ),
            ),
        ]),
      ),
    );
  }
}

// ------------------------------------------------------------------------------------------------ the prototype's parts
const _onNavy = Color(0xFFD4DFF5);

/// .round — the big round buttons of the ringing card.
class _Round extends StatelessWidget {
  final IconData icon;
  final String label;
  final Color bg;
  final VoidCallback onTap;
  const _Round(this.icon, this.label, {required this.onTap, this.bg = const Color(0x14FFFFFF)});
  @override
  Widget build(BuildContext context) => GestureDetector(
        onTap: onTap,
        child: Column(mainAxisSize: MainAxisSize.min, children: [
          Container(width: 62, height: 62, decoration: BoxDecoration(color: bg, shape: BoxShape.circle), child: Icon(icon, color: Colors.white, size: 24)),
          const SizedBox(height: 8),
          Text(label, style: const TextStyle(color: _onNavy, fontSize: 10)),
        ]),
      );
}

/// .controls .ctl — one control of the bar: plain, «off» (white), «rec» or «end» (red).
class _Ctl extends StatelessWidget {
  final IconData icon;
  final String label;
  final bool white, red;
  final VoidCallback onTap;
  const _Ctl(this.icon, this.label, {required this.onTap, this.white = false, this.red = false});
  @override
  Widget build(BuildContext context) => Expanded(
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: onTap,
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            Container(
              width: 44, height: 44,
              decoration: BoxDecoration(color: red ? Brand.badC : white ? Colors.white : const Color(0x14FFFFFF), shape: BoxShape.circle),
              child: Icon(icon, size: 19, color: white ? Brand.navy : Colors.white),
            ),
            const SizedBox(height: 6),
            Text(label, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(color: Brand.onNavy, fontSize: 9)),
          ]),
        ),
      );
}

Widget _bar(List<Widget> ctls) => Container(
      margin: const EdgeInsets.only(bottom: 16),
      padding: const EdgeInsets.symmetric(vertical: 12, horizontal: 10),
      decoration: BoxDecoration(color: const Color(0xCC0C1837), borderRadius: BorderRadius.circular(22)),
      child: Row(children: ctls),
    );

/// .chip — the clock, REC, «بانتظار موافقة …».
class _Chip extends StatelessWidget {
  final String text;
  final bool rec, wait;
  const _Chip(this.text, {this.rec = false, this.wait = false});
  @override
  Widget build(BuildContext context) => GestureDetector(
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 5),
          decoration: BoxDecoration(
            color: rec ? const Color(0xCCB4322E) : const Color(0x990C1837),
            borderRadius: BorderRadius.circular(9),
            border: Border.all(color: rec ? Colors.transparent : wait ? const Color(0x66FFFFFF) : const Color(0x1FFFFFFF)),
          ),
          child: Row(mainAxisSize: MainAxisSize.min, children: [
            if (rec) ...[Container(width: 6, height: 6, decoration: const BoxDecoration(color: Colors.white, shape: BoxShape.circle)), const SizedBox(width: 6)],
            Text(text, textDirection: rec || !wait ? TextDirection.ltr : null, style: const TextStyle(color: Colors.white, fontSize: 10, fontWeight: FontWeight.w600)),
          ]),
        ),
      );
}

/// .netnote — one quiet line over the bar.
class _NetNote extends StatelessWidget {
  final IconData icon;
  final String text;
  final VoidCallback? onTap;
  const _NetNote(this.icon, this.text, {this.onTap});
  @override
  Widget build(BuildContext context) => GestureDetector(
        onTap: onTap,
        child: Container(
          margin: const EdgeInsets.only(bottom: 12),
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
          decoration: BoxDecoration(color: const Color(0xB30C1837), borderRadius: BorderRadius.circular(10)),
          child: Row(mainAxisSize: MainAxisSize.min, children: [
            Icon(icon, size: 14, color: _onNavy),
            const SizedBox(width: 6),
            Flexible(child: Text(text, style: const TextStyle(color: _onNavy, fontSize: 10))),
          ]),
        ),
      );
}

// ------------------------------------------------------------------------------------------------ ringing (call-incoming)
class _Ringing extends StatelessWidget {
  final Ring r;
  const _Ringing(this.r);
  @override
  Widget build(BuildContext context) {
    final p = context.read<CallsProvider>();
    final kind = r.group
        ? L(context, 'دعوة إلى مكالمة جماعية', 'Invitation to a group call')
        : r.video ? L(context, 'مكالمة فيديو داخلية واردة', 'Incoming internal video call') : L(context, 'مكالمة صوتية داخلية واردة', 'Incoming internal voice call');
    final hasAbout = r.patient != null || (r.reason?.isNotEmpty ?? false);
    return Padding(
      padding: const EdgeInsets.fromLTRB(22, 18, 22, 20),
      child: Column(children: [
        Row(mainAxisAlignment: MainAxisAlignment.center, children: [
          Text(kind, style: const TextStyle(color: Brand.onNavyMuted, fontSize: 11)),
          const SizedBox(width: 6),
          Icon(r.group ? Lu.usersRound : r.video ? Lu.video : Lu.phone, size: 14, color: Brand.onNavyMuted),
        ]),
        const SizedBox(height: 34),
        Container(
          padding: const EdgeInsets.all(8),
          decoration: BoxDecoration(borderRadius: BorderRadius.circular(42), color: const Color(0x0DFFFFFF)),
          child: Container(
            padding: const EdgeInsets.all(10),
            decoration: BoxDecoration(borderRadius: BorderRadius.circular(38), color: const Color(0x07FFFFFF)),
            child: PersonAvatar(photo: r.caller.photo, name: r.caller.name, size: 112, radius: 34),
          ),
        ),
        const SizedBox(height: 16),
        Text(r.group ? L(context, '${r.caller.name} يدعوك', '${r.caller.name} invites you') : r.caller.name,
            textAlign: TextAlign.center, style: const TextStyle(color: Colors.white, fontSize: 22, fontWeight: FontWeight.w600)),
        const SizedBox(height: 2),
        Text(r.group ? L(context, '${r.members} مشاركين — انضم ${r.joined}', '${r.members} people — ${r.joined} joined') : r.caller.role(context),
            style: const TextStyle(color: Brand.onNavyMuted, fontSize: 11)),
        if (hasAbout)
          Container(
            margin: const EdgeInsets.only(top: 18),
            padding: const EdgeInsets.symmetric(horizontal: 13, vertical: 10),
            decoration: BoxDecoration(color: const Color(0x0DFFFFFF), border: Border.all(color: const Color(0x1AFFFFFF)), borderRadius: BorderRadius.circular(14)),
            child: Row(children: [
              const Icon(Lu.clipboardPlus, size: 16, color: Color(0xFFE9D48D)),
              const SizedBox(width: 10),
              Expanded(
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  if (r.patient != null) Text('${L(context, 'بخصوص', 'About')}: ${r.patient!.name} · ${r.patient!.mrn}', style: const TextStyle(color: Colors.white, fontSize: 12, fontWeight: FontWeight.w500)),
                  if (r.reason?.isNotEmpty ?? false) Text(r.reason!, style: const TextStyle(color: _onNavy, fontSize: 10)),
                ]),
              ),
            ]),
          ),
        if (r.recording)
          Container(
            margin: const EdgeInsets.only(top: 12),
            padding: const EdgeInsets.all(10),
            decoration: BoxDecoration(color: const Color(0x33B4322E), borderRadius: BorderRadius.circular(12)),
            child: Text(L(context, 'هذه المكالمة تُسجَّل بموافقة المشاركين. انضمامك يعني موافقتك على التسجيل.', 'This call is being recorded with everyone\'s consent. Joining means you agree.'),
                style: const TextStyle(color: Colors.white, fontSize: 11)),
          ),
        const Spacer(),
        if (!r.group)
          Padding(
            padding: const EdgeInsets.only(bottom: 18),
            child: Wrap(spacing: 6, runSpacing: 6, alignment: WrapAlignment.center, children: [
              for (final q in [L(context, 'سأتصل بك بعد قليل', 'I will call you back shortly'), L(context, 'في عملية الآن', 'In an operation now'), L(context, 'أرسل لي على الشات', 'Send me a chat message')])
                GestureDetector(
                  onTap: () => p.decline(reply: q),
                  child: Container(
                    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
                    decoration: BoxDecoration(color: const Color(0x0BFFFFFF), border: Border.all(color: const Color(0x22FFFFFF)), borderRadius: BorderRadius.circular(10)),
                    child: Text(q, style: const TextStyle(color: Brand.onNavy, fontSize: 10)),
                  ),
                ),
            ]),
          ),
        Row(mainAxisAlignment: MainAxisAlignment.spaceAround, children: [
          // in reading order (RTL: from the right): decline · voice · accept
          _Round(Lu.phoneOff, L(context, 'اعتذار', 'Decline'), bg: Brand.badC, onTap: () => p.decline()),
          if (r.video) _Round(Lu.phone, L(context, 'بالصوت', 'Voice'), onTap: () => p.accept(video: false)),
          _Round(r.video ? Lu.video : Lu.phone, L(context, 'قبول', 'Accept'), bg: Brand.blue, onTap: () => p.accept(video: r.video)),
        ]),
      ]),
    );
  }
}

// ------------------------------------------------------------------------------------------------ a two-person call
class _OneStage extends StatelessWidget {
  final OneCall c;
  const _OneStage(this.c);
  @override
  Widget build(BuildContext context) {
    final p = context.read<CallsProvider>();
    final seeThem = c.answered && c.remote.cam && c.remoteR.srcObject != null;
    final seeMe = c.cam && c.local != null && c.local!.getVideoTracks().isNotEmpty;
    final reconnecting = c.peerAway || c.net == 'disconnected' || c.net == 'failed';
    final rec = c.rec;
    final asking = rec != null && rec.state == 'asking' && rec.mine;
    final route = c.speaker ? L(context, 'السماعة الخارجية — اضغط لسماعة الأذن', 'Speaker — tap for the earpiece') : L(context, 'سماعة الأذن — اضغط للسماعة الخارجية', 'Earpiece — tap for the speaker');
    final line = !c.answered
        ? L(context, 'جارٍ الاتصال…', 'Calling…')
        : reconnecting ? L(context, 'جارٍ إعادة الاتصال…', 'Reconnecting…') : L(context, 'الاتصال جيد', 'Good connection');
    // the record control: none → ask, asking → withdraw, on → stop (only for someone allowed to ask)
    final Widget? recCtl = rec?.state == 'on'
        ? _Ctl(Lu.circleStop, L(context, 'إيقاف التسجيل', 'Stop recording'), red: true, onTap: p.stopRecording)
        : asking
            ? _Ctl(Lu.circleX, L(context, 'سحب الطلب', 'Withdraw'), white: true, onTap: p.withdrawRecording)
            : (c.canRecord && c.answered && rec == null)
                ? _Ctl(Lu.circleDot, L(context, 'تسجيل', 'Record'), onTap: p.requestRecording)
                : null;
    return Stack(children: [
      Positioned.fill(
        child: seeThem
            ? RTCVideoView(c.remoteR, objectFit: c.remote.share ? RTCVideoViewObjectFit.RTCVideoViewObjectFitContain : RTCVideoViewObjectFit.RTCVideoViewObjectFitCover)
            : Center(
                child: Column(mainAxisSize: MainAxisSize.min, children: [
                  PersonAvatar(photo: c.peer.photo, name: c.peer.name, size: 112, radius: 34),
                  const SizedBox(height: 16),
                  Text(c.peer.name, style: const TextStyle(color: Colors.white, fontSize: 22, fontWeight: FontWeight.w600)),
                  const SizedBox(height: 4),
                  Text(
                    !c.answered ? L(context, 'جارٍ الاتصال…', 'Calling…') : L(context, 'الكاميرا مغلقة لدى الطرف الآخر', 'Their camera is off'),
                    style: const TextStyle(color: Brand.onNavyMuted, fontSize: 11)),
                ]),
              ),
      ),
      if (seeThem) const Positioned.fill(child: IgnorePointer(child: DecoratedBox(decoration: BoxDecoration(gradient: LinearGradient(begin: Alignment.topCenter, end: Alignment.bottomCenter, colors: [Color(0x660C1837), Color(0x000C1837), Color(0x000C1837), Color(0xCC0C1837)], stops: [0, .26, .62, 1])))))
      else if (c.remoteR.srcObject != null) SizedBox(width: 1, height: 1, child: RTCVideoView(c.remoteR)),   // keeps the voice playing on the web build
      Padding(
        padding: const EdgeInsets.fromLTRB(22, 14, 22, 0),
        child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          Row(children: [
            Expanded(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(c.peer.name, style: const TextStyle(color: Colors.white, fontSize: 13, fontWeight: FontWeight.w600)),
                if (c.patient != null) Text('${c.patient!.name} · ${c.patient!.mrn}', style: const TextStyle(color: _onNavy, fontSize: 10)),
              ]),
            ),
            if (rec?.state == 'on') ...[_Chip('REC ${mmss(rec!.secs)}', rec: true), const SizedBox(width: 6)],
            if (asking) ...[_Chip(L(context, 'بانتظار موافقة ${c.peer.name}…', 'Waiting for ${c.peer.name}…'), wait: true), const SizedBox(width: 6)],
            _Chip(mmss(c.secs)),
          ]),
          if (seeMe)
            Align(
              alignment: AlignmentDirectional.centerEnd,
              child: Container(
                margin: const EdgeInsets.only(top: 14),
                width: 92, height: 128,
                decoration: BoxDecoration(color: const Color(0xFF2A3A5C), borderRadius: BorderRadius.circular(16), border: Border.all(color: const Color(0x40FFFFFF), width: 2)),
                clipBehavior: Clip.antiAlias,
                child: RTCVideoView(c.localR, mirror: true, objectFit: RTCVideoViewObjectFit.RTCVideoViewObjectFitCover),
              ),
            ),
          const Spacer(),
          if (asking)
            Container(
              margin: const EdgeInsets.only(bottom: 10),
              padding: const EdgeInsets.fromLTRB(12, 11, 12, 11),
              decoration: BoxDecoration(color: const Color(0xD90C1837), border: Border.all(color: const Color(0x1FFFFFFF)), borderRadius: BorderRadius.circular(14)),
              child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                const Icon(Lu.send, size: 16, color: Colors.white),
                const SizedBox(width: 10),
                Expanded(
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text(L(context, 'أُرسل طلب التسجيل إلى ${c.peer.name}', 'The request to record was sent to ${c.peer.name}'),
                        style: const TextStyle(color: Colors.white, fontSize: 11, fontWeight: FontWeight.w600)),
                    Text(
                      L(context,
                          'لن يبدأ إلا إذا وافق. ما يُسجَّل: ${rec.kind == 'video' ? 'الصوت والصورة من الطرفين' : 'الصوت من الطرفين'}. يُحفظ في مدير الملفات${c.patient != null ? ' وملف المريض' : ''}.',
                          'It starts only if they agree. Recorded: ${rec.kind == 'video' ? 'voice and picture of both sides' : 'the voice of both sides'}. Kept in the File Manager${c.patient != null ? ' and the patient\'s file' : ''}.'),
                      style: const TextStyle(color: _onNavy, fontSize: 10, height: 1.7)),
                  ]),
                ),
              ]),
            ),
          Center(child: _NetNote(reconnecting ? Lu.wifiOff : Lu.wifi, '$line · $route', onTap: p.toggleSpeaker)),
          _bar([
            _Ctl(c.mic ? Lu.mic : Lu.micOff, L(context, 'الميكروفون', 'Microphone'), white: !c.mic, onTap: p.toggleMic),
            _Ctl(c.cam ? Lu.video : Lu.videoOff, L(context, 'الكاميرا', 'Camera'), white: !c.cam, onTap: p.toggleCam),
            _Ctl(Lu.switchCamera, L(context, 'قلب الكاميرا', 'Flip camera'), onTap: p.flipCamera),
            if (recCtl != null) recCtl,
            _Ctl(Lu.phoneOff, L(context, 'إنهاء', 'End'), red: true, onTap: p.hangup),
          ]),
        ]),
      ),
    ]);
  }
}

// ------------------------------------------------------------------------------------------------ the consent sheet (call-consent)
class _ConsentSheet extends StatelessWidget {
  final RecState rec;
  final bool group;
  const _ConsentSheet(this.rec, {required this.group});
  @override
  Widget build(BuildContext context) {
    final p = context.read<CallsProvider>();
    final by = rec.by?.name ?? '';
    final video = rec.kind == 'video';
    Widget fact(IconData i, String t, String s) => ItemRow(leading: IconBox(icon: i), title: t, subtitle: s);
    return Material(
      color: Brand.paper,
      borderRadius: const BorderRadius.vertical(top: Radius.circular(26)),
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(20, 10, 20, 22),
          child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            Center(child: Container(width: 42, height: 4, margin: const EdgeInsets.only(bottom: 14), decoration: BoxDecoration(color: Brand.lineC, borderRadius: BorderRadius.circular(4)))),
            Row(children: [
              const Icon(Lu.circleDot, size: 18, color: Brand.badC),
              const SizedBox(width: 8),
              Expanded(child: Text(L(context, '$by يطلب تسجيل المكالمة', '$by asks to record the call'), style: T.h2.copyWith(fontSize: 15))),
            ]),
            const SizedBox(height: 4),
            Text(group ? L(context, 'لن يبدأ التسجيل إلا إذا وافق كل المشاركين، ويظهر للجميع طوال التسجيل', 'It starts only if everyone agrees; everyone sees it while it runs')
                : L(context, 'لن يبدأ التسجيل إلا إذا وافقت، ويظهر للطرفين طوال التسجيل', 'Nothing is recorded unless you agree; both sides see it while it runs'), style: T.sub),
            const SizedBox(height: 12),
            DataList(children: [
              fact(Lu.monitorPlay, L(context, 'ما يُسجَّل', 'What is recorded'),
                  video ? L(context, 'الصوت والصورة من الطرفين — والشاشة إن عُرضت', 'Voice and picture of both sides — and the screen if shown') : L(context, 'الصوت من الطرفين', 'The voice of both sides')),
              fact(Lu.folderLock, L(context, 'أين يُحفظ', 'Where it is kept'),
                  L(context, 'مدير الملفات · سجل المكالمات', 'The File Manager · the call log') + (rec.patient != null ? L(context, ' وملف المريض', ' and the patient\'s file') : '')),
              fact(Lu.usersRound, L(context, 'من يراه', 'Who sees it'), L(context, 'أنتما فقط ومن تسميه الإدارة، ويُسجَّل كل فتح', 'The two of you and named reviewers; every opening is logged')),
              fact(Lu.timer, L(context, 'مدة الاحتفاظ', 'Kept for'),
                  rec.days > 0 ? L(context, '${rec.days} يوماً ثم يُحذف تلقائياً', '${rec.days} days, then deleted') : L(context, 'دون حذف تلقائي', 'No automatic deletion')),
            ]),
            const SizedBox(height: 14),
            Row(children: [
              Expanded(flex: 3, child: _SheetBtn(L(context, 'أوافق على التسجيل', 'I agree to the recording'), primary: true, onTap: () => p.answerRecording(true))),
              const SizedBox(width: 9),
              Expanded(flex: 2, child: _SheetBtn(L(context, 'لا أوافق', 'I do not agree'), onTap: () => p.answerRecording(false))),
            ]),
          ]),
        ),
      ),
    );
  }
}

/// .actions button — the prototype's blue primary and outlined secondary.
class _SheetBtn extends StatelessWidget {
  final String text;
  final bool primary;
  final VoidCallback onTap;
  const _SheetBtn(this.text, {required this.onTap, this.primary = false});
  @override
  Widget build(BuildContext context) => GestureDetector(
        onTap: onTap,
        child: Container(
          padding: const EdgeInsets.symmetric(vertical: 13, horizontal: 12),
          alignment: Alignment.center,
          decoration: BoxDecoration(color: primary ? Brand.blue : Brand.paper, border: Border.all(color: primary ? Brand.blue : Brand.lineC), borderRadius: BorderRadius.circular(12)),
          child: Text(text, style: TextStyle(fontSize: 12, color: primary ? Colors.white : Brand.inkC)),
        ),
      );
}

// ------------------------------------------------------------------------------------------------ a group call (call-group)
class _GroupStage extends StatelessWidget {
  final GroupCall g;
  const _GroupStage(this.g);
  @override
  Widget build(BuildContext context) {
    final p = context.read<CallsProvider>();
    final inCall = g.members.values.where((x) => x.state == 'joined' || x.state == 'host').toList();
    final ringing = g.members.values.where((x) => x.state == 'invited').toList();
    final iAmHost = g.hostId == p.myId;
    final rec = g.rec;
    final asking = rec != null && rec.state == 'asking' && rec.mine;
    Widget tile(Widget media, String name, {bool mute = false, String? wait, bool speak = false}) => Container(
          decoration: BoxDecoration(color: const Color(0xFF24345A), borderRadius: BorderRadius.circular(18), border: Border.all(color: speak ? Brand.gold : Colors.transparent, width: 2)),
          clipBehavior: Clip.antiAlias,
          child: Stack(fit: StackFit.expand, children: [
            media,
            if (wait != null) PositionedDirectional(top: 8, end: 8, child: _Chip(wait, wait: true)),
            PositionedDirectional(
              bottom: 8, start: 9,
              child: Container(
                padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
                decoration: BoxDecoration(color: const Color(0x990C1837), borderRadius: BorderRadius.circular(7)),
                child: Row(mainAxisSize: MainAxisSize.min, children: [
                  Text(name, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(color: Colors.white, fontSize: 10)),
                  if (mute) ...[const SizedBox(width: 5), const Icon(Lu.micOff, size: 11, color: Colors.white)],
                ]),
              ),
            ),
          ]),
        );
    Widget face(CallPerson c) => Center(
          child: Container(
            width: 58, height: 58, alignment: Alignment.center,
            decoration: const BoxDecoration(color: Brand.blue, shape: BoxShape.circle),
            child: c.photo == null ? Text(c.name.trim().split(RegExp(r'\s+')).take(2).map((w) => w.isEmpty ? '' : w[0]).join(), style: const TextStyle(color: Colors.white, fontSize: 18, fontWeight: FontWeight.w700))
                : PersonAvatar(photo: c.photo, name: c.name, size: 58),
          ),
        );
    final tiles = <Widget>[
      for (final x in inCall)
        tile(
          x.remote.cam && x.r?.srcObject != null
              ? RTCVideoView(x.r!, objectFit: x.remote.share ? RTCVideoViewObjectFit.RTCVideoViewObjectFitContain : RTCVideoViewObjectFit.RTCVideoViewObjectFitCover)
              : Stack(fit: StackFit.expand, children: [face(x.card), if (x.r?.srcObject != null) SizedBox(width: 1, height: 1, child: RTCVideoView(x.r!))]),
          x.card.name,
          mute: !x.remote.mic,
          wait: x.away || x.net == 'disconnected' || x.net == 'failed' ? L(context, 'يُعاد الاتصال…', 'Reconnecting…') : null,
        ),
      tile(g.cam && g.local != null && g.local!.getVideoTracks().isNotEmpty ? RTCVideoView(g.localR, mirror: true, objectFit: RTCVideoViewObjectFit.RTCVideoViewObjectFitCover) : face(p.me?.me ?? CallPerson(id: 0, name: '', nameEn: '')),
          L(context, 'أنت', 'You'), mute: !g.mic),
    ];
    final ringNames = ringing.map((x) => x.card.name).join('، ');
    final addLine = [
      if (iAmHost && inCall.length + ringing.length + 1 < g.cap) L(context, 'إضافة مشارك', 'Add someone'),
      if (ringNames.isNotEmpty) L(context, 'يرن الآن: $ringNames', 'Ringing now: $ringNames'),
    ].join(' · ');
    final Widget? recCtl = rec?.state == 'on'
        ? _Ctl(Lu.circleStop, L(context, 'إيقاف التسجيل', 'Stop recording'), red: true, onTap: p.stopRecording)
        : asking
            ? _Ctl(Lu.circleX, L(context, 'سحب الطلب', 'Withdraw'), white: true, onTap: p.withdrawRecording)
            : (g.canRecord && rec == null) ? _Ctl(Lu.circleDot, L(context, 'تسجيل', 'Record'), onTap: p.requestRecording) : null;
    return Padding(
      padding: const EdgeInsets.fromLTRB(18, 14, 18, 0),
      child: Column(children: [
        Row(children: [
          Expanded(
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text('${L(context, 'مكالمة جماعية', 'Group call')} · ${inCall.length + 1}', style: const TextStyle(color: Colors.white, fontSize: 13, fontWeight: FontWeight.w600)),
              Text([if (g.patient != null) '${g.patient!.name} · ${g.patient!.mrn}', if (iAmHost) L(context, 'أنت المضيف', 'You are the host')].join(' · '),
                  style: const TextStyle(color: _onNavy, fontSize: 10)),
            ]),
          ),
          if (rec?.state == 'on') ...[_Chip('REC ${mmss(rec!.secs)}', rec: true), const SizedBox(width: 6)],
          if (asking) ...[_Chip(L(context, 'بانتظار موافقة ${rec.waiting}…', 'Waiting for ${rec.waiting}…'), wait: true), const SizedBox(width: 6)],
          _Chip(mmss(g.secs)),
        ]),
        const SizedBox(height: 14),
        GridView.count(
          shrinkWrap: true, physics: const NeverScrollableScrollPhysics(),
          crossAxisCount: tiles.length > 4 ? 3 : 2, mainAxisSpacing: 8, crossAxisSpacing: 8, childAspectRatio: .9, children: tiles),
        const Spacer(),
        if (addLine.isNotEmpty) _NetNote(Lu.userPlus, addLine, onTap: iAmHost ? () => _add(context, p, g) : null),
        _bar([
          _Ctl(g.mic ? Lu.mic : Lu.micOff, L(context, 'الميكروفون', 'Microphone'), white: !g.mic, onTap: p.toggleMic),
          _Ctl(g.cam ? Lu.video : Lu.videoOff, L(context, 'الكاميرا', 'Camera'), white: !g.cam, onTap: p.toggleCam),
          _Ctl(Lu.usersRound, L(context, 'المشاركون', 'People'), onTap: () => _people(context, p, g)),
          if (recCtl != null) recCtl,
          _Ctl(Lu.phoneOff, L(context, 'مغادرة', 'Leave'), red: true, onTap: p.leaveGroup),
        ]),
      ]),
    );
  }
}

/// «إضافة مشارك»: the available people not already in the call.
Future<void> _add(BuildContext context, CallsProvider p, GroupCall g) async {
  List<CallPerson> people;
  try { people = await ApiService.instance.getCallPeople(); } catch (_) { return; }
  if (!context.mounted) return;
  final room = g.cap - 1 - g.members.values.where((x) => x.state == 'joined' || x.state == 'invited' || x.state == 'host').length;
  final free = people.where((x) => p.presenceOf(x.id, x.state) == 'on' && !g.members.containsKey(x.id)).toList();
  final picked = <int>{};
  await showModalBottomSheet(
    context: context,
    backgroundColor: Brand.paper,
    isScrollControlled: true,
    shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(26))),
    builder: (c) => StatefulBuilder(
      builder: (c, set) => SafeArea(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(20, 16, 20, 16),
          child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            Text(L(c, 'إضافة مشارك', 'Add someone'), style: T.h2),
            const SizedBox(height: 4),
            Text(L(c, 'المتاحون الآن · يمكن إضافة $room', 'Available now · room for $room'), style: T.small),
            const SizedBox(height: 10),
            if (free.isEmpty) SoftNote(L(c, 'لا أحد متاح الآن', 'Nobody is available now'))
            else ConstrainedBox(
              constraints: BoxConstraints(maxHeight: MediaQuery.of(c).size.height * .5),
              child: SingleChildScrollView(
                child: DataList(children: [
                  for (final x in free)
                    ItemRow(
                      leading: PersonAvatar(photo: x.photo, name: x.display(c), size: 36, radius: 10),
                      title: x.display(c), subtitle: x.role(c),
                      end: Icon(picked.contains(x.id) ? Lu.squareCheck : Lu.square, size: 20, color: picked.contains(x.id) ? Brand.blue : Brand.mutedC),
                      onTap: () => set(() { if (picked.contains(x.id)) { picked.remove(x.id); } else if (picked.length < room) { picked.add(x.id); } }),
                    ),
                ]),
              ),
            ),
            const SizedBox(height: 12),
            _SheetBtn(L(c, 'دعوة ${picked.length}', 'Invite ${picked.length}'), primary: true, onTap: () { Navigator.of(c).pop(); p.addToGroup(free.where((x) => picked.contains(x.id)).toList()); }),
          ]),
        ),
      ),
    ),
  );
}

Future<void> _people(BuildContext context, CallsProvider p, GroupCall g) => showModalBottomSheet(
      context: context,
      backgroundColor: Brand.paper,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(26))),
      builder: (c) {
        final iAmHost = g.hostId == p.myId;
        String state(GMember x) => switch (x.state) {
              'joined' || 'host' => x.card.id == g.hostId ? L(c, 'المضيف', 'Host') : L(c, 'في المكالمة', 'In the call'),
              'invited' => L(c, 'يرن…', 'Ringing…'),
              'declined' => L(c, 'اعتذر', 'Declined'),
              'missed' => L(c, 'لم يرد', 'No answer'),
              'busy' => L(c, 'لم يُتح الوصول', 'Not reachable'),
              'left' => L(c, 'غادر', 'Left'),
              'removed' => L(c, 'أُخرج', 'Removed'),
              _ => x.state,
            };
        return SafeArea(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(20, 16, 20, 16),
            child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
              Text(L(c, 'المشاركون', 'People'), style: T.h2),
              const SizedBox(height: 4),
              Text(L(c, 'حتى ${g.cap} · المضيف يُخرج ويُنهي للجميع', 'Up to ${g.cap} · the host removes and ends for all'), style: T.small),
              const SizedBox(height: 10),
              DataList(children: [
                for (final x in g.members.values)
                  ItemRow(
                    leading: PersonAvatar(photo: x.card.photo, name: x.card.name, size: 36, radius: 10),
                    title: x.card.name,
                    subtitle: state(x),
                    end: iAmHost && (x.state == 'joined' || x.state == 'invited')
                        ? GestureDetector(onTap: () { Navigator.of(c).pop(); p.removeFromGroup(x.card.id); }, child: Text(L(c, 'إخراج', 'Remove'), style: const TextStyle(color: Brand.badC, fontSize: 11)))
                        : null,
                  ),
              ]),
              if (iAmHost) ...[
                const SizedBox(height: 12),
                _SheetBtn(L(c, 'إنهاء المكالمة للجميع', 'End the call for everyone'), onTap: () { Navigator.of(c).pop(); p.endGroupForAll(); }),
              ],
            ]),
          ),
        );
      },
    );
