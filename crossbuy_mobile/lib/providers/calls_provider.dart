import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';
import 'package:signalr_netcore/signalr_client.dart';
import 'package:video_player/video_player.dart';

import '../app_config.dart';
import '../models/calls.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../screens/calls/call_stage.dart';

// ==========================================================================================
// HOSPITAL H176 — internal calls in the app. The SAME hub and the SAME protocol as the browser
// (wwwroot/js/hospital/hospital-calls.js + hospital-calls-group.js), so a phone calls a browser and back:
//   * /hubs/calls rings, passes offer/answer/ICE between the two ends only, and keeps the call's record;
//   * one audio and one video transceiver from the start: camera on/off is replaceTrack, never renegotiation;
//   * the caller sends the offer once answered; in a group the person who JOINS offers to those already there;
//   * a {t:'state'} message carries mic/camera/screen so the other side shows "camera off" or "muted".
// The media goes device to device (or through the hospital's TURN) — never through the app's server.
// After a drop or a server restart the hub reconnects and Resume / GroupResume put this phone back in its call.
// Recording: this phone asks to record, agrees or refuses when asked, shows REC and stops it. A phone cannot mix both
// sides into one file, so the hub has the BROWSER at the other end record a recording this phone asked for (two phones
// cannot record: the hub says so). The request and the consent stay in this person's name.
// ==========================================================================================

class RemoteState {
  bool mic, cam, share;
  RemoteState({this.mic = true, this.cam = false, this.share = false});
}

class RecState {
  final int id;
  String state;   // asking | on
  final String kind;
  final bool mine;
  int secs = 0, waiting;
  final CallPerson? by;
  final CallPatient? patient;
  final int days;
  bool askingMe;   // the consent sheet is showing on this phone
  RecState({required this.id, required this.state, required this.kind, this.mine = false, this.waiting = 0, this.by, this.patient, this.days = 0, this.askingMe = false});
}

/// The ringing screen: a two-person call or a group invitation.
class Ring {
  final int callId;
  final bool video, group, recording, canRecord;
  final CallPerson caller;
  final CallPatient? patient;
  final String? reason;
  final List<CallPerson> others;
  final int joined, members;
  int left;
  Ring({required this.callId, required this.video, required this.caller, this.patient, this.reason, this.group = false, this.others = const [], this.joined = 0, this.members = 0, this.recording = false, this.left = 30, this.canRecord = false});
}

class OneCall {
  final int id;
  final String role;   // caller | callee
  final CallPerson peer;
  final CallPatient? patient;
  final String? reason;
  bool video, answered = false, mic = true, cam, speaker, peerAway = false, canRecord = false;
  final RemoteState remote;
  int secs = 0;
  String net = 'new';
  RTCPeerConnection? pc;
  MediaStream? local, remoteStream;
  RTCRtpTransceiver? audioTx, videoTx;
  final RTCVideoRenderer localR = RTCVideoRenderer(), remoteR = RTCVideoRenderer();
  final List<RTCIceCandidate> cands = [];
  Future<void>? pcReady;
  Future<MediaStream>? media;
  RecState? rec;
  Timer? clock, ringTimer, quality;
  OneCall({required this.id, required this.role, required this.peer, this.patient, this.reason, required this.video})
      : cam = video, speaker = false, remote = RemoteState(cam: video);   // the earpiece first, as in the prototype
}

class GMember {
  final CallPerson card;
  String state;
  RTCPeerConnection? pc;
  MediaStream? stream;
  RTCVideoRenderer? r;
  RTCRtpTransceiver? audioTx, videoTx;
  RemoteState remote = RemoteState();
  final List<RTCIceCandidate> cands = [];
  Future<void>? ready;
  bool away = false;
  String net = 'new';
  GMember(this.card, this.state);
}

class GroupCall {
  final int id;
  bool video, mic = true, cam, speaker = true, canRecord = false;
  int hostId, cap, secs = 0;
  final Map<int, GMember> members = {};
  final CallPatient? patient;
  final String? reason;
  MediaStream? local;
  final RTCVideoRenderer localR = RTCVideoRenderer();
  RecState? rec;
  Timer? clock, ping;
  GroupCall({required this.id, required this.video, required this.hostId, this.patient, this.reason, this.cap = 4}) : cam = video;
}

/// One line of live captions: who spoke and what they said (it fades after a few seconds).
class CaptionLine {
  final String name; final String text; final DateTime at;
  CaptionLine(this.name, this.text) : at = DateTime.now();
}

class CallsProvider extends ChangeNotifier {
  HubConnection? _hub;
  // Live captions (2026-11-22). On for everyone in the call. The app cannot record its own microphone while the call
  // holds it, so it asks the browsers in the call to caption what they hear from it (CaptionRelay: the server lets
  // that through only for someone who asked); the words of everyone come back on the hub's 'caption' event.
  bool captionsOn = false;
  final List<CaptionLine> captions = [];
  bool connected = false;
  CallsMe? me;
  int get myId => me?.me?.id ?? 0;
  final Map<int, String> presence = {};

  Ring? ring;
  OneCall? current;
  GroupCall? group;
  bool _stageOpen = false;

  /// One-line messages for the stage (a toast at the bottom of the call screen or of the app).
  final ValueNotifier<String?> notice = ValueNotifier(null);
  void _say(String text) { notice.value = text; notice.value = null; }   // listeners read the transient value
  String lang = 'ar';
  bool get _ar => lang == 'ar';
  String L(String ar, String en) => _ar ? ar : en;

  List<Map<String, dynamic>>? _ice;
  DateTime? _iceAt;
  VideoPlayerController? _tone;

  bool get busy => ring != null || current != null || group != null;

  // ------------------------------------------------------------------------------------------ connection
  Future<void> connect() async {
    if (_hub != null) { if (!connected) _keepTrying(); return; }
    me = await ApiService.instance.getCallsMe();
    if (me == null || !me!.installed) { notifyListeners(); return; }
    final delays = <int>[0, 1000, 2000, 3000, 4000, ...List.filled(180, 5000)];   // a server restart takes minutes: a quarter of an hour
    final hub = HubConnectionBuilder()
        // client=app: the hub knows a phone cannot record, and has the browser at the other end record what it asks for
        .withUrl('${AppConfig.apiBaseUrl}/hubs/calls?culture=$lang&client=app',
            options: HttpConnectionOptions(accessTokenFactory: () async => ApiService.instance.token ?? ''))
        .withAutomaticReconnect(retryDelays: delays)
        .build();
    hub.on('incoming', (a) => _onIncoming(_m(a)));
    hub.on('answered', (a) => _onAnswered(_m(a)));
    hub.on('ended', (a) => _onEnded(_m(a)));
    hub.on('signal', (a) => _onSignal(_m(a)));
    hub.on('taken', (a) { final m = _m(a); if (ring != null && ring!.callId == m['callId']) _closeRing(); });
    hub.on('caption', (a) => _onCaption(_m(a)));
    hub.on('missed', (a) => _say(L('مكالمة فائتة — تجدها في السجل', 'A missed call — it is in your log')));
    hub.on('presence', (a) { final m = _m(a); final id = (m['employeeId'] as num?)?.toInt(); if (id != null) { presence[id] = '${m['state']}'; notifyListeners(); } });
    hub.on('peeraway', (a) { final c = current; if (c != null && c.id == _m(a)['callId']) { c.peerAway = true; notifyListeners(); } });
    hub.on('peerback', (a) { final c = current; if (c != null && c.id == _m(a)['callId']) { c.peerAway = false; notifyListeners(); } });
    hub.on('recask', (a) => _onRecAsk(_m(a)));
    hub.on('recstart', (a) => _onRecStart(_m(a)));
    hub.on('recclosed', (a) => _onRecClosed(_m(a)));
    hub.on('recstop', (a) => _onRecStop(_m(a)));
    hub.on('recagreed', (a) => _onRecAgreed(_m(a)));
    // group calls
    hub.on('groupincoming', (a) => _onGroupIncoming(_m(a)));
    hub.on('groupjoined', (a) => _onGroupJoined(_m(a)));
    hub.on('groupleft', (a) => _onGroupLeft(_m(a)));
    hub.on('groupstate', (a) { final m = _m(a); final x = _gm(m); if (x != null) { x.state = '${m['state']}'; notifyListeners(); } });
    hub.on('groupmembers', (a) => _onGroupMembers(_m(a)));
    hub.on('grouphost', (a) { final m = _m(a); final g = group; if (g != null && g.id == m['callId']) { g.hostId = (m['hostId'] as num).toInt(); if (g.hostId == myId) _say(L('أصبحتَ مضيف المكالمة', 'You are now the host')); notifyListeners(); } });
    hub.on('groupconvert', (a) => _onGroupConvert(_m(a)));
    hub.on('gsignal', (a) => _onGroupSignal(_m(a)));
    hub.on('groupaway', (a) { final x = _gm(_m(a)); if (x != null) { x.away = true; notifyListeners(); } });
    hub.on('groupback', (a) { final x = _gm(_m(a)); if (x != null) { x.away = false; notifyListeners(); } });
    hub.onreconnected(({connectionId}) { connected = true; _resumeAll(); notifyListeners(); });
    hub.onreconnecting(({error}) { connected = false; notifyListeners(); });
    hub.onclose(({error}) {
      connected = false; notifyListeners();
      _keepTrying();
    });
    _hub = hub;
    try { await hub.start(); connected = true; } catch (_) {}
    notifyListeners();
  }

  Future<void> reset() async {
    await _endEverything();
    try { await _hub?.stop(); } catch (_) {}
    _hub = null; connected = false; me = null; presence.clear();
    notifyListeners();
  }

  static Map<String, dynamic> _m(List<Object?>? a) => (a != null && a.isNotEmpty && a[0] is Map) ? (a[0] as Map).cast<String, dynamic>() : <String, dynamic>{};
  GMember? _gm(Map<String, dynamic> m) { final g = group; if (g == null || g.id != m['callId']) return null; return g.members[(m['employeeId'] as num?)?.toInt()]; }
  // AFTER THE CONNECTION CLOSES, KEEP TRYING. It used to try once, five seconds later: a server restart takes a minute
  // or two, so that one try failed and the app stayed "not connected to the call server" until it was reopened — every
  // call from it refused. Now it tries again, waiting a little longer each time (up to 30 s), until it is back.
  bool _retrying = false;
  void _keepTrying() {
    if (_retrying) return;
    _retrying = true;
    () async {
      var wait = 5;
      while (_hub != null && !connected) {
        await Future.delayed(Duration(seconds: wait));
        try { await _hub?.start(); connected = true; _resumeAll(); notifyListeners(); } catch (_) { wait = wait < 30 ? wait * 2 : 30; }
      }
      _retrying = false;
    }();
  }

  Future<Object?> _invoke(String method, List<Object> args) async { final h = _hub; if (h == null || !connected) throw StateError('not connected'); return h.invoke(method, args: args); }
  static Map<String, dynamic> _r(Object? o) => o is Map ? o.cast<String, dynamic>() : <String, dynamic>{};

  /// back after a drop or a server restart: tell the hub which call this phone is in, it rebuilds it
  void _resumeAll() {
    final c = current;
    if (c != null && c.answered) {
      _invoke('Resume', [c.id]).then((r) { final m = _r(r); if (m['ok'] == true) { c.peerAway = m['peerHere'] != true; notifyListeners(); } }).catchError((_) {});
    }
    final g = group;
    if (g != null) _invoke('GroupResume', [g.id]).then((r) { final m = _r(r); if (m['ok'] == true && m['hostId'] is num) { g.hostId = (m['hostId'] as num).toInt(); notifyListeners(); } }).catchError((_) {});
  }

  String presenceOf(int id, String fallback) => presence[id] ?? fallback;

  // ------------------------------------------------------------------------------------------ the stage route
  void _openStage() {
    if (_stageOpen) { notifyListeners(); return; }
    final nav = AppNav.navigatorKey.currentState;
    if (nav == null) return;
    _stageOpen = true;
    nav.push(PageRouteBuilder(opaque: true, pageBuilder: (_, __, ___) => const CallStagePage(), transitionsBuilder: (_, a, __, child) => FadeTransition(opacity: a, child: child)))
        .then((_) => _stageOpen = false);
    notifyListeners();
  }

  void _closeStageIfIdle() {
    notifyListeners();
    if (!busy && _stageOpen) {
      final nav = AppNav.navigatorKey.currentState;
      if (nav != null && nav.canPop()) nav.pop();
      _stageOpen = false;
    }
  }

  Future<void> _playTone(String asset) async {
    await _stopTone();
    try {
      final t = VideoPlayerController.asset(asset);
      _tone = t;
      await t.initialize();
      await t.setLooping(true);
      if (_tone == t) await t.play();
    } catch (_) {}
  }

  Future<void> _stopTone() async {
    final t = _tone; _tone = null;
    if (t != null) { try { await t.pause(); await t.dispose(); } catch (_) {} }
  }

  // ------------------------------------------------------------------------------------------ media
  Future<List<Map<String, dynamic>>> _iceServers() async {
    if (_ice != null && _iceAt != null && DateTime.now().difference(_iceAt!) < const Duration(hours: 6)) return _ice!;
    _ice = await ApiService.instance.getCallIce(); _iceAt = DateTime.now();
    return _ice!;
  }

  /// The camera and microphone, falling back to the microphone alone, then to the camera alone, then to listening only.
  Future<(MediaStream, bool mic, bool cam)> _getMedia(bool video, int maxHeight) async {
    final v = video ? {'facingMode': 'user', 'height': {'ideal': maxHeight}} : false;
    for (final c in [
      {'audio': true, 'video': v},
      if (video) {'audio': true, 'video': false},
      if (video) {'audio': false, 'video': v},
    ]) {
      try {
        final s = await navigator.mediaDevices.getUserMedia(c);
        return (s, s.getAudioTracks().isNotEmpty, s.getVideoTracks().isNotEmpty);
      } catch (_) {}
    }
    _say(L('تعذّر فتح الميكروفون والكاميرا — تستمع فقط', 'The microphone and camera could not be opened — listening only'));
    return (await createLocalMediaStream('empty'), false, false);
  }

  Map<String, dynamic> _cand(RTCIceCandidate c) => {'candidate': c.candidate, 'sdpMid': c.sdpMid, 'sdpMLineIndex': c.sdpMLineIndex};
  RTCIceCandidate _candOf(dynamic c) {
    final m = (c as Map).cast<String, dynamic>();
    return RTCIceCandidate(m['candidate'] as String?, m['sdpMid'] as String?, (m['sdpMLineIndex'] as num?)?.toInt());
  }
  RTCSessionDescription _sdpOf(dynamic d) { final m = (d as Map).cast<String, dynamic>(); return RTCSessionDescription(m['sdp'] as String?, m['type'] as String?); }

  // ------------------------------------------------------------------------------------------ a two-person call: out
  Future<void> startCall(CallPerson person, {required bool video}) async {
    if (busy) { _say(L('أنت في مكالمة بالفعل', 'You are already in a call')); return; }
    if (!connected) { _say(L('غير متصل بخادم الاتصال — حاول بعد لحظات', 'Not connected to the call server — try again in a moment')); return; }
    try {
      // patient 0 and an empty reason = none (the Dart SignalR client cannot send a JSON null)
      final r = _r(await _invoke('Invite', [person.id, video && (me?.video ?? true), 0, '']));
      if (r['ok'] != true) { _say(errorText('${r['error'] ?? 'failed'}', r['name']?.toString())); return; }
      final c = OneCall(id: (r['callId'] as num).toInt(), role: 'caller', peer: r['callee'] is Map ? CallPerson.fromJson(_r(r['callee'])) : person,
          patient: CallPatient.fromJson(r['patient']), video: r['video'] == true)..canRecord = r['canRecord'] == true;
      await c.localR.initialize(); await c.remoteR.initialize();
      current = c;
      _openStage();
      _playTone('assets/sounds/ringback.wav');
      c.media = _prepareMedia(c);   // the camera while it rings, so answering connects at once
      var left = (r['ringSeconds'] as num?)?.toInt() ?? 30;
      c.ringTimer = Timer.periodic(const Duration(seconds: 1), (t) {
        if (--left > 0) return;
        t.cancel();
        _invoke('Hangup', [c.id, true]).catchError((_) => null);
      });
    } catch (_) {
      _say(L('تعذّر بدء المكالمة', 'The call could not be started'));
    }
  }

  Future<MediaStream> _prepareMedia(OneCall c) async {
    if (c.local != null) return c.local!;
    final (s, mic, cam) = await _getMedia(c.video, me?.maxHeight ?? 720);
    c.local = s; c.mic = mic; c.cam = cam;
    c.localR.srcObject = s;
    if (!kIsWeb) { try { await Helper.setSpeakerphoneOn(c.speaker); } catch (_) {} }
    notifyListeners();
    return s;
  }

  void _onAnswered(Map<String, dynamic> m) {
    final c = current;
    if (c == null || c.id != m['callId'] || c.role != 'caller') return;
    c.ringTimer?.cancel(); _stopTone();
    c.answered = true; c.remote.cam = m['video'] == true;
    _startClock(c);
    c.pcReady = _preparePc(c).then((_) async {
      final pc = c.pc; if (pc == null) return;
      final o = await pc.createOffer({});
      await pc.setLocalDescription(o);
      _send(c, {'t': 'offer', 'sdp': {'type': o.type, 'sdp': o.sdp}});
    });
    notifyListeners();
  }

  // ------------------------------------------------------------------------------------------ a two-person call: in
  void _onIncoming(Map<String, dynamic> m) {
    if (busy) return;   // the hub already refuses a second caller to someone busy
    final caller = CallPerson.fromJson(_r(m['caller']));
    ring = Ring(callId: (m['callId'] as num).toInt(), video: m['video'] == true, caller: caller, patient: CallPatient.fromJson(m['patient']), reason: m['reason']?.toString(),
        left: (m['ringSeconds'] as num?)?.toInt() ?? 30, canRecord: m['canRecord'] == true);
    _ringClock();
    _playTone('assets/sounds/ring.wav');
    _openStage();
  }

  Timer? _ringTimer;
  void _ringClock() {
    _ringTimer?.cancel();
    _ringTimer = Timer.periodic(const Duration(seconds: 1), (t) {
      final r = ring; if (r == null) { t.cancel(); return; }
      r.left--;
      if (!kIsWeb && r.left > 0 && r.left % 2 == 0) HapticFeedback.vibrate();
      if (r.group && r.left <= 0) { final id = r.callId; _closeRing(); _invoke('GroupDecline', [id, true]).catchError((_) => null); _say(L('مكالمة فائتة من ${r.caller.name}', 'Missed call from ${r.caller.name}')); return; }
      if (r.left <= -5) _closeRing();   // the caller's side ends it; this only tidies up if its message was lost
      notifyListeners();
    });
  }

  void _closeRing() { _ringTimer?.cancel(); _ringTimer = null; ring = null; _stopTone(); _closeStageIfIdle(); }

  Future<void> decline({String? reply}) async {
    final r = ring; if (r == null) return;
    _closeRing();
    if (r.group) { _invoke('GroupDecline', [r.callId, false]).catchError((_) => null); return; }
    _invoke('Decline', [r.callId, reply ?? '']).catchError((_) => null);   // '' = no reply
    if (reply != null) _say(L('أُرسل ردّك إلى ${r.caller.name}', 'Your reply was sent to ${r.caller.name}'));
  }

  Future<void> accept({required bool video}) async {
    final r = ring; if (r == null) return;
    _ringTimer?.cancel(); ring = null; _stopTone();
    if (r.group) { await _joinGroup(r, video); return; }
    try {
      final res = _r(await _invoke('Answer', [r.callId, video]));
      if (res['ok'] != true) { _say(L('تعذّر الرد — ربما رُدّ عليها من جهاز آخر', 'Could not answer — perhaps answered elsewhere')); _closeStageIfIdle(); return; }
      final c = OneCall(id: r.callId, role: 'callee', peer: r.caller, patient: r.patient, reason: r.reason, video: video)..answered = true..canRecord = r.canRecord;
      await c.localR.initialize(); await c.remoteR.initialize();
      current = c;
      _startClock(c);
      c.pcReady = _preparePc(c);
      notifyListeners();
    } catch (_) {
      _say(L('تعذّر الرد', 'Could not answer')); _closeStageIfIdle();
    }
  }

  void _startClock(OneCall c) {
    c.clock?.cancel();
    c.clock = Timer.periodic(const Duration(seconds: 1), (_) { c.secs++; if (c.rec?.state == 'on') c.rec!.secs++; notifyListeners(); });
  }

  Future<void> _preparePc(OneCall c) async {
    final servers = await _iceServers();
    final local = await (c.media ??= _prepareMedia(c));
    if (current != c) return;
    final pc = await createPeerConnection({'iceServers': servers, 'sdpSemantics': 'unified-plan'});
    c.pc = pc;
    pc.onIceCandidate = (cand) { if (cand.candidate != null) _send(c, {'t': 'cand', 'c': _cand(cand)}); };
    pc.onTrack = (e) async {
      c.remoteStream ??= e.streams.isNotEmpty ? e.streams.first : await createLocalMediaStream('remote');
      if (e.streams.isEmpty || e.streams.first != c.remoteStream) { try { await c.remoteStream!.addTrack(e.track); } catch (_) {} }
      c.remoteR.srcObject = c.remoteStream;
      notifyListeners();
    };
    pc.onConnectionState = (s) {
      c.net = s.name.replaceFirst('RTCPeerConnectionState', '').toLowerCase();
      if (s == RTCPeerConnectionState.RTCPeerConnectionStateFailed) { _say(L('انقطع الاتصال', 'The connection was lost')); hangup(); }
      notifyListeners();
    };
    if (c.role == 'caller') {
      final a = local.getAudioTracks().isEmpty ? null : local.getAudioTracks().first;
      final v = local.getVideoTracks().isEmpty ? null : local.getVideoTracks().first;
      c.audioTx = await _transceiver(pc, a, RTCRtpMediaType.RTCRtpMediaTypeAudio, local);
      c.videoTx = await _transceiver(pc, v, RTCRtpMediaType.RTCRtpMediaTypeVideo, local);
    }
  }

  /// One audio or video channel from the start, sending what this device has (or nothing yet) and receiving the other side.
  Future<RTCRtpTransceiver> _transceiver(RTCPeerConnection pc, MediaStreamTrack? t, RTCRtpMediaType kind, MediaStream local) {
    final init = RTCRtpTransceiverInit(direction: TransceiverDirection.SendRecv, streams: [local]);
    return t != null ? pc.addTransceiver(track: t, kind: kind, init: init) : pc.addTransceiver(kind: kind, init: init);
  }

  void _send(OneCall c, Map<String, dynamic> o) => _invoke('Signal', [c.id, jsonEncode(o)]).catchError((_) => null);
  void _sendState(OneCall c) => _send(c, {'t': 'state', 'mic': c.mic, 'cam': c.cam, 'share': false});

  Future<void> _onSignal(Map<String, dynamic> m) async {
    final c = current;
    if (c == null || c.id != m['callId']) return;
    Map<String, dynamic> d;
    try { d = (jsonDecode('${m['payload']}') as Map).cast<String, dynamic>(); } catch (_) { return; }
    await (c.pcReady ?? Future.value());
    final pc = c.pc; if (pc == null) return;
    try {
      switch (d['t']) {
        case 'offer':
          await pc.setRemoteDescription(_sdpOf(d['sdp']));
          for (final tx in await pc.getTransceivers()) {
            final k = tx.receiver.track?.kind;
            final local = c.local!;
            if (k == 'audio') { c.audioTx = tx; await tx.setDirection(TransceiverDirection.SendRecv); if (local.getAudioTracks().isNotEmpty) await tx.sender.replaceTrack(local.getAudioTracks().first); }
            if (k == 'video') { c.videoTx = tx; await tx.setDirection(TransceiverDirection.SendRecv); if (local.getVideoTracks().isNotEmpty) await tx.sender.replaceTrack(local.getVideoTracks().first); }
          }
          final ans = await pc.createAnswer({});
          await pc.setLocalDescription(ans);
          _send(c, {'t': 'answer', 'sdp': {'type': ans.type, 'sdp': ans.sdp}});
          _flush(pc, c.cands); _sendState(c);
        case 'answer':
          await pc.setRemoteDescription(_sdpOf(d['sdp']));
          _flush(pc, c.cands); _sendState(c);
        case 'cand':
          final cand = _candOf(d['c']);
          if (await pc.getRemoteDescription() != null) { await pc.addCandidate(cand); } else { c.cands.add(cand); }
        case 'state':
          c.remote..mic = d['mic'] != false..cam = d['cam'] == true..share = d['share'] == true;
          notifyListeners();
        case 'captions':
          _setCaptions(d['on'] == true, tell: false);
      }
    } catch (_) {}
  }

  void _flush(RTCPeerConnection pc, List<RTCIceCandidate> q) { final l = List.of(q); q.clear(); for (final x in l) { pc.addCandidate(x).catchError((_) {}); } }

  void _onEnded(Map<String, dynamic> m) {
    final id = m['callId'];
    final r = ring;
    if (r != null && r.callId == id) {
      _closeRing();
      if (m['status'] == 'Missed' || m['status'] == 'Cancelled') _say(L('مكالمة فائتة من ${r.caller.name}', 'Missed call from ${r.caller.name}'));
      return;
    }
    final g = group;
    if (g != null && g.id == id) {
      _closeGroup();
      _say(m['status'] == 'Removed' ? L('أخرجك المضيف من المكالمة', 'The host removed you from the call') : L('انتهت المكالمة الجماعية', 'The group call ended'));
      return;
    }
    final c = current;
    if (c == null || c.id != id) return;
    if (c.role == 'caller' && !c.answered) {
      if (m['status'] == 'Declined') {
        final reply = m['reply']?.toString();
        _say(L('اعتذر ${c.peer.name}', '${c.peer.name} declined') + (reply != null && reply.isNotEmpty ? ' — «$reply»' : ''));
      } else if (m['status'] == 'Missed') {
        _say(L('لم يتم الرد — سُجّلت مكالمة فائتة', 'No answer — recorded as a missed call'));
      }
    } else {
      _say(L('انتهت المكالمة', 'The call ended'));
    }
    _teardown(c);
  }

  Future<void> hangup() async {
    final c = current;
    if (c == null) return;
    if (connected) { _invoke('Hangup', [c.id, false]).catchError((_) { _teardown(c); return null; }); } else { _teardown(c); }
  }

  // ---------------- live captions ----------------
  int? get _liveCallId => current?.id ?? group?.id;
  /// The caption button: on or off for everyone in the call.
  void toggleCaptions() => _setCaptions(!captionsOn, tell: true);
  void _setCaptions(bool on, {required bool tell}) {
    final id = _liveCallId; if (id == null) return;
    final was = captionsOn; captionsOn = on;
    if (!on) captions.clear();
    final c = current, g = group;
    void all(Map<String, dynamic> o) {
      if (c != null) _send(c, o);
      if (g != null) { for (final k in g.members.keys) { _gsend(g, k, o); } }
    }
    if (tell) all({'t': 'captions', 'on': on});            // the others turn theirs on or off
    if (was != on || tell) {
      _invoke('CaptionRelay', [id, on]).catchError((_) => null);   // my consent to be captioned by a browser
      all({'t': 'captions-relay', 'on': on});               // ...and the request to the browsers in the call
    }
    if (tell || was != on) _say(on ? L('الترجمة النصية الحية مفعّلة لكل من في المكالمة', 'Live captions are on for everyone in the call') : L('أُوقفت الترجمة النصية الحية', 'Live captions are off'));
    notifyListeners();
  }
  void _onCaption(Map<String, dynamic> m) {
    if (m['callId'] != _liveCallId || '${m['text'] ?? ''}'.trim().isEmpty) return;
    if (!captionsOn) { captionsOn = true; }   // someone turned them on and their words arrived first
    final ar = _ar;
    final name = '${(ar ? m['nameAr'] : m['nameEn']) ?? m['nameAr'] ?? m['nameEn'] ?? ''}';
    final line = CaptionLine(name, '${m['text']}'.trim());
    captions.add(line);
    while (captions.length > 3) { captions.removeAt(0); }
    notifyListeners();
    Timer(const Duration(seconds: 7), () { if (captions.remove(line)) notifyListeners(); });
  }
  void _captionsOff() { captionsOn = false; captions.clear(); }

  void _teardown(OneCall c) {
    if (current != c) return;
    current = null;
    _captionsOff();
    c.clock?.cancel(); c.ringTimer?.cancel(); c.quality?.cancel();
    _stopTone();
    _clearBlur();
    try { c.pc?.close(); } catch (_) {}
    for (final t in c.local?.getTracks() ?? <MediaStreamTrack>[]) { t.stop(); }
    c.localR.srcObject = null; c.remoteR.srcObject = null;
    Future.delayed(const Duration(milliseconds: 400), () { c.localR.dispose(); c.remoteR.dispose(); });
    _closeStageIfIdle();
  }

  // ------------------------------------------------------------------------------------------ controls (both kinds)
  void toggleMic() {
    final g = group;
    if (g != null) { g.mic = !g.mic; for (final t in g.local?.getAudioTracks() ?? <MediaStreamTrack>[]) { t.enabled = g.mic; } _groupState(); notifyListeners(); return; }
    final c = current; if (c == null) return;
    final a = c.local?.getAudioTracks() ?? [];
    if (a.isEmpty) { _say(L('لا يوجد ميكروفون متاح', 'No microphone is available')); return; }
    c.mic = !c.mic; for (final t in a) { t.enabled = c.mic; }
    _sendState(c); notifyListeners();
  }

  Future<void> toggleCam() async {
    final g = group;
    if (g != null) {
      final v = g.local?.getVideoTracks() ?? [];
      if (v.isEmpty) { _say(L('لا توجد كاميرا متاحة', 'No camera is available')); return; }
      g.cam = !g.cam; for (final t in v) { t.enabled = g.cam; }
      _groupState(); notifyListeners(); return;
    }
    final c = current; if (c == null) return;
    var v = c.local?.getVideoTracks() ?? [];
    if (v.isEmpty) {
      // a voice call: open the camera now and put it on the video transceiver (no renegotiation)
      try {
        final s = await navigator.mediaDevices.getUserMedia({'video': {'facingMode': 'user', 'height': {'ideal': me?.maxHeight ?? 720}}});
        final t = s.getVideoTracks().first;
        await c.local?.addTrack(t);
        c.localR.srcObject = c.local;
        await c.videoTx?.sender.replaceTrack(t);
        c.cam = true;
      } catch (_) { _say(L('تعذّر فتح الكاميرا', 'The camera could not be opened')); return; }
    } else {
      c.cam = !c.cam; for (final t in v) { t.enabled = c.cam; }
    }
    _sendState(c); notifyListeners();
  }

  // ---- background blur (owner 2026-11-19), Android: MainActivity's "crossbuy/blur" puts BlurProcessor (MediaPipe's
  // selfie model, on the phone) on this call's camera track; the other side receives the blurred picture ----
  static const _blurCh = MethodChannel('crossbuy/blur');
  bool blurOn = false;
  bool get blurSupported => !kIsWeb && defaultTargetPlatform == TargetPlatform.android;

  Future<void> toggleBlur() async {
    final s = group?.local ?? current?.local;
    final v = s?.getVideoTracks() ?? [];
    if (v.isEmpty || !blurSupported) { _say(L('لا توجد كاميرا لتمويه خلفيتها', 'There is no camera to blur')); return; }
    final want = !blurOn;
    bool ok = false;
    try { ok = await _blurCh.invokeMethod<bool>('set', {'trackId': v.first.id, 'on': want}) == true; } catch (_) {}
    if (!ok) { _say(L('تعذّر تشغيل التمويه على هذا الجهاز', 'The blur could not start on this device')); return; }
    blurOn = want;
    _say(want ? L('الخلفية مموّهة', 'The background is blurred') : L('أُلغي تمويه الخلفية', 'The background is no longer blurred'));
    notifyListeners();
  }

  void _clearBlur() {
    if (!blurOn) return;
    blurOn = false;
    _blurCh.invokeMethod('clear').catchError((_) => null);
  }

  Future<void> flipCamera() async {
    final s = group?.local ?? current?.local;
    final v = s?.getVideoTracks() ?? [];
    if (v.isEmpty || kIsWeb) return;
    try { await Helper.switchCamera(v.first); } catch (_) {}
  }

  Future<void> toggleSpeaker() async {
    final g = group; final c = current;
    if (g != null) { g.speaker = !g.speaker; } else if (c != null) { c.speaker = !c.speaker; } else { return; }
    if (!kIsWeb) { try { await Helper.setSpeakerphoneOn(g?.speaker ?? c!.speaker); } catch (_) {} }
    notifyListeners();
  }

  Future<void> saveNote(String text) async {
    final id = current?.id ?? group?.id; if (id == null || text.trim().isEmpty) return;
    final ok = await ApiService.instance.postCallNote(id, text.trim());
    _say(ok ? L('حُفظت الملاحظة في سجل المكالمة', 'The note was saved in the call log') : L('تعذّر حفظ الملاحظة', 'The note could not be saved'));
  }

  // ------------------------------------------------------------------------------------------ recording (consent)
  void _onRecAsk(Map<String, dynamic> m) {
    final isGroup = m['group'] == true;
    if (isGroup ? group?.id != m['callId'] : current?.id != m['callId']) return;
    final rec = RecState(id: (m['recId'] as num).toInt(), state: 'asking', kind: '${m['kind']}', waiting: (m['waiting'] as num?)?.toInt() ?? 0,
        by: m['by'] is Map ? CallPerson.fromJson(_r(m['by'])) : null, patient: CallPatient.fromJson(m['patient']), days: (m['days'] as num?)?.toInt() ?? 0, askingMe: true);
    if (isGroup) { group!.rec = rec; } else { current!.rec = rec; }
    notifyListeners();
  }

  void answerRecording(bool agree) {
    final rec = group?.rec ?? current?.rec; if (rec == null) return;
    rec.askingMe = false;
    _invoke(group != null ? 'GroupRecordAnswer' : 'RecordAnswer', [rec.id, agree]).catchError((_) => null);
    notifyListeners();
  }

  /// This phone asks to record (the prototype's «تسجيل» → «بانتظار موافقة …»). The hub picks the browser that records.
  Future<void> requestRecording() async {
    final g = group; final c = current;
    if ((g ?? c) == null || (g?.rec ?? c?.rec) != null) return;
    final picture = g != null
        ? g.cam || g.members.values.any((x) => x.remote.cam || x.remote.share)
        : (c!.cam || c.remote.cam || c.remote.share);
    try {
      final r = _r(await _invoke(g != null ? 'GroupRecordRequest' : 'RecordRequest', [g?.id ?? c!.id, picture]));
      if (r['ok'] != true) { _say(errorText('${r['error'] ?? 'failed'}', null)); return; }
      final rec = RecState(id: (r['recId'] as num).toInt(), state: 'asking', kind: '${r['kind']}', mine: true, waiting: (r['waiting'] as num?)?.toInt() ?? 1);
      if (g != null) { g.rec = rec; } else { c!.rec = rec; }
      notifyListeners();
    } catch (_) { _say(L('تعذّر طلب التسجيل', 'The recording could not be asked for')); }
  }

  /// «سحب الطلب»: before anyone answered.
  void withdrawRecording() {
    final g = group; final c = current;
    final rec = g?.rec ?? c?.rec; if (rec == null || rec.state != 'asking' || !rec.mine) return;
    _invoke(g != null ? 'GroupRecordStop' : 'RecordWithdraw', [rec.id]).catchError((_) => null);
    if (g != null) { g.rec = null; } else { c!.rec = null; }
    notifyListeners();
  }

  void stopRecording() {
    final rec = group?.rec ?? current?.rec; if (rec == null || rec.state != 'on') return;
    _invoke(group != null ? 'GroupRecordStop' : 'RecordStop', [rec.id]).catchError((_) => null);
  }

  void _onRecStart(Map<String, dynamic> m) {
    final isGroup = m['group'] == true;
    final rec = RecState(id: (m['recId'] as num).toInt(), state: 'on', kind: '${m['kind']}', mine: m['requesterId'] == myId);
    if (isGroup) { if (group?.id != m['callId']) return; group!.rec = rec; } else { if (current?.id != m['callId']) return; current!.rec = rec; }
    _say(L('بدأ التسجيل — يظهر للجميع', 'Recording started — everyone sees it'));
    notifyListeners();
  }

  void _onRecClosed(Map<String, dynamic> m) {
    final g = group; final c = current;
    final holder = g?.rec?.id == m['recId'] ? 'g' : c?.rec?.id == m['recId'] ? 'c' : null;
    if (holder == null) return;
    final wasMine = (holder == 'g' ? g!.rec : c!.rec)?.mine == true;
    if (holder == 'g') { g!.rec = null; } else { c!.rec = null; }
    if (m['refused'] == true) {
      final who = '${m['byName'] ?? (holder == 'c' ? c!.peer.name : '')}';
      _say(wasMine && who.isNotEmpty ? L('رفض $who — تستمر المكالمة دون تسجيل', '$who refused — the call goes on unrecorded') : L('رُفض التسجيل — تستمر المكالمة دون تسجيل', 'Recording refused — the call goes on unrecorded'));
    }
    notifyListeners();
  }

  void _onRecStop(Map<String, dynamic> m) {
    final g = group; final c = current;
    if (g?.rec?.id == m['recId']) { g!.rec = null; } else if (c?.rec?.id == m['recId']) { c!.rec = null; } else { return; }
    _say(L('توقف التسجيل — يُحفظ في مدير الملفات', 'Recording stopped — it is kept in the File Manager'));
    notifyListeners();
  }

  void _onRecAgreed(Map<String, dynamic> m) {
    final r = group?.rec; if (r == null || r.id != m['recId']) return;
    if (r.waiting > 0) r.waiting--;
    notifyListeners();
  }

  // ------------------------------------------------------------------------------------------ group calls (mesh)
  Future<void> startGroup(List<CallPerson> people, {required bool video, int? patientId}) async {
    if (busy) { _say(L('أنت في مكالمة بالفعل', 'You are already in a call')); return; }
    if (!connected) { _say(L('غير متصل بخادم الاتصال', 'Not connected to the call server')); return; }
    try {
      final r = _r(await _invoke('GroupInvite', [people.map((p) => p.id).toList(), video, patientId ?? 0, '', 0]));   // 0 = no patient, not converted from a call
      if (r['ok'] != true) { _say(errorText('${r['error'] ?? 'failed'}', r['name']?.toString())); return; }
      final busyNames = (r['busy'] as List?)?.join('، ');
      if (busyNames != null && busyNames.isNotEmpty) _say(L('لم يُتح الوصول إلى: $busyNames — سُجّلت لهم مكالمة فائتة', 'Could not reach: $busyNames — a missed call was recorded'));
      await _openGroup(r, r['video'] == true);
    } catch (_) {
      _say(L('تعذّر بدء المكالمة الجماعية', 'The group call could not be started'));
    }
  }

  void _onGroupIncoming(Map<String, dynamic> m) {
    if (busy) return;
    final members = (m['members'] as List? ?? []).whereType<Map>().map((x) => x.cast<String, dynamic>()).toList();
    final others = members.where((x) => x['card'] is Map && x['host'] != true && _r(x['card'])['id'] != myId).map((x) => CallPerson.fromJson(_r(x['card']))).toList();
    ring = Ring(callId: (m['callId'] as num).toInt(), video: m['video'] == true, caller: CallPerson.fromJson(_r(m['host'])), patient: CallPatient.fromJson(m['patient']),
        reason: m['reason']?.toString(), group: true, others: others, members: members.length, joined: members.where((x) => x['state'] == 'joined').length,
        recording: m['recording'] == true || m['recording'] is Map, left: (m['ringSeconds'] as num?)?.toInt() ?? 30);   // the ring sends true/false (a rejoin sends the recording's card)
    _ringClock();
    _playTone('assets/sounds/ring.wav');
    _openStage();
  }

  Future<void> _joinGroup(Ring r, bool video) async {
    try {
      final res = _r(await _invoke('GroupJoin', [r.callId, video]));
      if (res['ok'] != true) { _say(errorText('${res['error'] ?? 'failed'}', null)); _closeStageIfIdle(); return; }
      await _openGroup(res, video);
    } catch (_) { _say(L('تعذّر الانضمام', 'Could not join')); _closeStageIfIdle(); }
  }

  void _onGroupConvert(Map<String, dynamic> m) async {
    final c = current; if (c == null || c.id != m['fromCallId']) return;
    final video = c.video;
    // the two-person call becomes a group call: this phone moves into it without ringing again
    current = null;
    c.clock?.cancel(); try { await c.pc?.close(); } catch (_) {}
    final keep = c.local;
    try {
      final r = _r(await _invoke('GroupInfo', [(m['callId'] as num).toInt()]));
      if (r['ok'] == true) { _say(L('أصبحت مكالمة جماعية', 'It became a group call')); await _openGroup(r, video, reuse: keep); }
    } catch (_) {}
  }

  Future<void> _openGroup(Map<String, dynamic> r, bool video, {MediaStream? reuse}) async {
    if (r['sfu'] != null) {
      // the hospital's media server carries this group call; the app does not join through it yet
      _say(L('هذه المكالمة الجماعية تمر عبر خادم الوسائط — انضم إليها من المتصفح', 'This group call goes through the media server — join it from the browser'));
      _invoke('GroupLeave', [(r['callId'] as num).toInt()]).catchError((_) => null);
      _closeStageIfIdle();
      return;
    }
    final host = _r(r['host']);
    final g = GroupCall(id: (r['callId'] as num).toInt(), video: video, hostId: (host['id'] as num?)?.toInt() ?? 0, patient: CallPatient.fromJson(r['patient']),
        reason: r['reason']?.toString(), cap: (r['cap'] as num?)?.toInt() ?? 4)..canRecord = r['canRecord'] == true;
    await g.localR.initialize();
    for (final x in (r['members'] as List? ?? []).whereType<Map>()) {
      final card = x['card'];
      if (card is Map && (card['id'] as num).toInt() != myId) _member(g, CallPerson.fromJson(card.cast<String, dynamic>()), '${x['state']}');
    }
    group = g;
    g.clock = Timer.periodic(const Duration(seconds: 1), (_) { g.secs++; if (g.rec?.state == 'on') g.rec!.secs++; notifyListeners(); });
    g.ping = Timer.periodic(const Duration(seconds: 30), (_) => _invoke('GroupPing', [g.id]).catchError((_) => null));
    _openStage();
    if (reuse != null) {
      g.local = reuse; g.mic = reuse.getAudioTracks().isNotEmpty; g.cam = reuse.getVideoTracks().isNotEmpty && video;
    } else {
      final (s, mic, cam) = await _getMedia(video, me?.maxHeight ?? 720);
      g.local = s; g.mic = mic; g.cam = cam;
    }
    g.localR.srcObject = g.local;
    if (!kIsWeb) { try { await Helper.setSpeakerphoneOn(true); } catch (_) {} }
    if (group != g) return;
    for (final id in (r['connectTo'] as List? ?? []).whereType<num>()) {
      if (g.members.containsKey(id.toInt())) _peer(g, id.toInt(), offer: true);
    }
    final rec = r['recording'];
    if (rec is Map) g.rec = RecState(id: (rec['recId'] as num).toInt(), state: 'on', kind: 'video');
    notifyListeners();
  }

  GMember _member(GroupCall g, CallPerson card, String state) {
    final x = g.members[card.id];
    if (x != null) { x.state = state; return x; }
    return g.members[card.id] = GMember(card, state);
  }

  Future<void> _peer(GroupCall g, int id, {required bool offer}) {
    final x = g.members[id];
    if (x == null) return Future.value();
    if (x.pc != null) return x.ready ?? Future.value();
    final ready = () async {
      final servers = await _iceServers();
      final pc = await createPeerConnection({'iceServers': servers, 'sdpSemantics': 'unified-plan'});
      x.pc = pc;
      x.r ??= RTCVideoRenderer();
      await x.r!.initialize();
      pc.onIceCandidate = (cand) { if (cand.candidate != null) _gsend(g, id, {'t': 'cand', 'c': _cand(cand)}); };
      pc.onTrack = (e) async {
        x.stream ??= e.streams.isNotEmpty ? e.streams.first : await createLocalMediaStream('m$id');
        if (e.streams.isEmpty || e.streams.first != x.stream) { try { await x.stream!.addTrack(e.track); } catch (_) {} }
        x.r!.srcObject = x.stream;
        notifyListeners();
      };
      pc.onConnectionState = (s) { x.net = s.name.replaceFirst('RTCPeerConnectionState', '').toLowerCase(); notifyListeners(); };
      final local = g.local!;
      if (offer) {
        final a = local.getAudioTracks().isEmpty ? null : local.getAudioTracks().first;
        final v = local.getVideoTracks().isEmpty ? null : local.getVideoTracks().first;
        x.audioTx = await _transceiver(pc, a, RTCRtpMediaType.RTCRtpMediaTypeAudio, local);
        x.videoTx = await _transceiver(pc, v, RTCRtpMediaType.RTCRtpMediaTypeVideo, local);
        final o = await pc.createOffer({});
        await pc.setLocalDescription(o);
        _gsend(g, id, {'t': 'offer', 'sdp': {'type': o.type, 'sdp': o.sdp}});
      }
    }();
    x.ready = ready;
    return ready;
  }

  void _gsend(GroupCall g, int to, Map<String, dynamic> o) => _invoke('GroupSignal', [g.id, to, jsonEncode(o)]).catchError((_) => null);
  void _groupState() {
    final g = group; if (g == null) return;
    for (final e in g.members.entries) { if (e.value.pc != null) _gsend(g, e.key, {'t': 'state', 'mic': g.mic, 'cam': g.cam, 'share': false}); }
  }

  Future<void> _onGroupSignal(Map<String, dynamic> m) async {
    final g = group; if (g == null || g.id != m['callId']) return;
    final from = (m['fromId'] as num).toInt();
    Map<String, dynamic> d;
    try { d = (jsonDecode('${m['payload']}') as Map).cast<String, dynamic>(); } catch (_) { return; }
    // a guest who was in the call before me offers with their card
    if (g.members[from] == null && from < 0 && d['t'] == 'offer' && d['card'] is Map) {
      final c = _r(d['card']);
      _member(g, CallPerson.fromJson(_guestTitled({'id': from, 'name': '${c['name'] ?? ''}', 'nameEn': '${c['name'] ?? ''}', 'guest': true})), 'joined');
      notifyListeners();
    }
    final x = g.members[from]; if (x == null) return;
    try {
      if (d['t'] == 'offer') {
        await _peer(g, from, offer: false);
        final pc = x.pc!;
        await pc.setRemoteDescription(_sdpOf(d['sdp']));
        for (final tx in await pc.getTransceivers()) {
          final k = tx.receiver.track?.kind;
          await tx.setDirection(TransceiverDirection.SendRecv);
          final local = g.local!;
          if (k == 'audio') { x.audioTx = tx; if (local.getAudioTracks().isNotEmpty) await tx.sender.replaceTrack(local.getAudioTracks().first); }
          if (k == 'video') { x.videoTx = tx; if (local.getVideoTracks().isNotEmpty) await tx.sender.replaceTrack(local.getVideoTracks().first); }
        }
        final ans = await pc.createAnswer({});
        await pc.setLocalDescription(ans);
        _gsend(g, from, {'t': 'answer', 'sdp': {'type': ans.type, 'sdp': ans.sdp}});
        _flush(pc, x.cands); _groupState();
        return;
      }
      await (x.ready ?? Future.value());
      final pc = x.pc;
      if (d['t'] == 'state') { x.remote..mic = d['mic'] != false..cam = d['cam'] == true..share = d['share'] == true; notifyListeners(); return; }
      if (d['t'] == 'captions') { _setCaptions(d['on'] == true, tell: false); return; }
      if (pc == null) return;
      if (d['t'] == 'answer') { await pc.setRemoteDescription(_sdpOf(d['sdp'])); _flush(pc, x.cands); _groupState(); }
      if (d['t'] == 'cand') { final cand = _candOf(d['c']); if (await pc.getRemoteDescription() != null) { await pc.addCandidate(cand); } else { x.cands.add(cand); } }
    } catch (_) {}
  }

  void _onGroupJoined(Map<String, dynamic> m) {
    final g = group; if (g == null || g.id != m['callId'] || m['card'] is! Map) return;
    _member(g, CallPerson.fromJson(_guestTitled(_r(m['card']))), 'joined');   // they will offer
    notifyListeners();
  }

  /// A guest from outside the company (a negative id, HospitalCallHub.Guests) has no title: they are named as a guest.
  Map<String, dynamic> _guestTitled(Map<String, dynamic> c) =>
      c['guest'] == true ? {...c, 'title': L('ضيف من خارج الشركة', 'Guest from outside the company'), 'titleEn': 'Guest from outside the company'} : c;

  void _onGroupLeft(Map<String, dynamic> m) {
    final x = _gm(m); if (x == null) return;
    x.state = m['removed'] == true ? 'removed' : 'left';
    try { x.pc?.close(); } catch (_) {}
    x.pc = null; x.ready = null; x.stream = null;
    final r = x.r; x.r = null;
    if (r != null) { r.srcObject = null; Future.delayed(const Duration(milliseconds: 300), r.dispose); }
    _say(m['removed'] == true ? L('أُخرج ${x.card.name}', '${x.card.name} was removed') : L('غادر ${x.card.name}', '${x.card.name} left'));
    notifyListeners();
  }

  void _onGroupMembers(Map<String, dynamic> m) {
    final g = group; if (g == null || g.id != m['callId']) return;
    for (final x in (m['members'] as List? ?? []).whereType<Map>()) {
      final card = x['card'];
      if (card is! Map || (card['id'] as num).toInt() == myId) continue;
      final cur = g.members[(card['id'] as num).toInt()];
      if (cur == null || cur.state != 'joined') _member(g, CallPerson.fromJson(card.cast<String, dynamic>()), '${x['state']}');
    }
    notifyListeners();
  }

  Future<void> leaveGroup() async {
    final g = group; if (g == null) return;
    _invoke('GroupLeave', [g.id]).catchError((_) => null);
    _closeGroup();
    _say(L('غادرتَ المكالمة — تستمر لمن بقي فيها', 'You left the call — it goes on for the others'));
  }

  /// «إضافة مشارك» (the host): they ring; a busy or offline one gets a missed call.
  Future<void> addToGroup(List<CallPerson> people) async {
    final g = group; if (g == null || people.isEmpty) return;
    try {
      final r = _r(await _invoke('GroupAdd', [g.id, people.map((p) => p.id).toList()]));
      if (r['ok'] != true) { _say(errorText('${r['error'] ?? 'failed'}', r['name']?.toString())); return; }
      for (final p in people) { if (!g.members.containsKey(p.id)) _member(g, p, 'invited'); }
      notifyListeners();
    } catch (_) { _say(L('تعذّرت الإضافة', 'Could not add')); }
  }

  Future<void> endGroupForAll() async { final g = group; if (g != null) _invoke('GroupEnd', [g.id]).catchError((_) => null); }
  Future<void> removeFromGroup(int employeeId) async { final g = group; if (g != null) _invoke('GroupRemove', [g.id, employeeId]).catchError((_) => null); }

  void _closeGroup() {
    final g = group; if (g == null) return;
    group = null;
    _captionsOff();
    _clearBlur();
    g.clock?.cancel(); g.ping?.cancel();
    for (final x in g.members.values) {
      try { x.pc?.close(); } catch (_) {}
      final r = x.r; if (r != null) { r.srcObject = null; Future.delayed(const Duration(milliseconds: 300), r.dispose); }
    }
    for (final t in g.local?.getTracks() ?? <MediaStreamTrack>[]) { t.stop(); }
    g.localR.srcObject = null;
    Future.delayed(const Duration(milliseconds: 300), g.localR.dispose);
    _closeStageIfIdle();
  }

  Future<void> _endEverything() async {
    if (ring != null) await decline();
    if (current != null) await hangup();
    if (group != null) await leaveGroup();
  }

  // ------------------------------------------------------------------------------------------ the hub's refusals
  String errorText(String key, String? name) {
    const ar = {
      'A group call is up to {0} people with you': 'المكالمة الجماعية حتى {0} أشخاص معك',
      'Choose at least two people for a group call': 'اختر شخصين على الأقل للمكالمة الجماعية',
      'Group calls are switched off (call settings)': 'المكالمات الجماعية متوقفة في إعدادات الاتصال',
      'This call is already being recorded or asked to be': 'هذه المكالمة قيد التسجيل أو هناك طلب تسجيل قائم',
      'You are already in a call': 'أنت في مكالمة بالفعل',
      'You cannot call yourself': 'لا يمكنك الاتصال بنفسك',
      "Your group may not call this person's group (call settings)": 'لا تسمح إعدادات الاتصال لفئتك بالاتصال بفئة هذا الشخص',
      'Recording starts only once the call is answered': 'لا يبدأ التسجيل إلا بعد الرد على المكالمة',
      'You may not ask to record calls (call settings)': 'لا تسمح إعدادات الاتصال لك بطلب تسجيل المكالمات',
      'Recording needs one side on a computer: a phone cannot record the call': 'التسجيل يحتاج أن يكون أحد الطرفين على حاسوب: لا يستطيع الهاتف تسجيل المكالمة',
      '{0} is in another call — it is recorded as a missed call so they call you back': '{0} في مكالمة أخرى — سُجّلت مكالمة فائتة ليعاود الاتصال بك',
      '{0} is not connected — it is recorded as a missed call so they call you back': '{0} غير متصل — سُجّلت مكالمة فائتة ليعاود الاتصال بك',
    };
    if (key == 'failed' || key == 'forbidden' || key == 'not_found') return L('تعذّر ذلك', 'That could not be done');
    final t = _ar ? (ar[key] ?? key) : key;
    return name == null ? t : t.replaceAll('{0}', name);
  }
}
