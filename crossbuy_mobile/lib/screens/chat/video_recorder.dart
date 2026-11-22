import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'package:camera/camera.dart';
import 'package:flutter/material.dart';
import 'package:video_player/video_player.dart';

import '../../services/api_service.dart';
import '../../theme/brand.dart';
import '../../widgets/ui.dart';

/// A VIDEO MESSAGE recorded in the app (the web has had it since round 8): the front camera and the microphone, at
/// most three minutes, WATCHED BEFORE IT IS SENT — record, stop, see it, then send it or record again. A still picture
/// is taken just before the recording starts and sent beside it, so the message shows a picture, not black.
///
/// Recorded with the official camera plugin (CameraX, MP4 with sound). flutter_webrtc's file recorder was tried first:
/// it takes its sound from a live call's audio engine only, so outside a call it wrote empty files.
/// Returns true when a video was sent.
Future<bool?> showVideoRecorder(BuildContext context, int conversationId) =>
    Navigator.of(context).push<bool>(MaterialPageRoute(fullscreenDialog: true, builder: (_) => _VideoRecorder(conversationId: conversationId)));

class _VideoRecorder extends StatefulWidget {
  final int conversationId;
  const _VideoRecorder({required this.conversationId});
  @override
  State<_VideoRecorder> createState() => _VideoRecorderState();
}

class _VideoRecorderState extends State<_VideoRecorder> {
  static const _max = 180;
  CameraController? _cam;
  String? _path;
  Uint8List? _poster;
  VideoPlayerController? _preview;
  Timer? _tick;
  int _secs = 0;
  bool _recording = false, _sending = false, _failed = false, _busy = false;

  @override
  void initState() {
    super.initState();
    _open();
  }

  Future<void> _open() async {
    try {
      final all = await availableCameras();
      if (all.isEmpty) throw StateError('no camera');
      final front = all.firstWhere((c) => c.lensDirection == CameraLensDirection.front, orElse: () => all.first);
      final cam = CameraController(front, ResolutionPreset.medium, enableAudio: true);
      await cam.initialize();
      if (!mounted) { await cam.dispose(); return; }
      setState(() => _cam = cam);
    } catch (_) {
      if (mounted) setState(() => _failed = true);
    }
  }

  Future<void> _start() async {
    final cam = _cam; if (cam == null || _recording || _busy) return;
    _busy = true;
    await _clearPreview();
    // the still picture for the message's poster, taken just before recording (best effort: none → the dark tile)
    try { final pic = await cam.takePicture(); _poster = await pic.readAsBytes(); try { File(pic.path).deleteSync(); } catch (_) {} } catch (_) { _poster = null; }
    try {
      await cam.startVideoRecording();
    } catch (_) {
      _busy = false;
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(L(context, 'تعذّر بدء التسجيل', 'The recording could not start'))));
      return;
    }
    _busy = false; _secs = 0;
    setState(() => _recording = true);
    _tick = Timer.periodic(const Duration(seconds: 1), (_) {
      if (!mounted) return;
      setState(() => _secs++);
      if (_secs >= _max) _stop();
    });
  }

  Future<void> _stop() async {
    final cam = _cam; if (cam == null || !_recording || _busy) return;
    _busy = true;
    _tick?.cancel();
    XFile? f;
    try { f = await cam.stopVideoRecording(); } catch (_) {}
    _busy = false;
    setState(() => _recording = false);
    if (f == null) return;
    _path = f.path;
    final v = VideoPlayerController.file(File(f.path));
    v.addListener(() { if (mounted) setState(() {}); });
    _preview = v;
    try { await v.initialize(); } catch (_) {}
    if (mounted) setState(() {});
  }

  Future<void> _clearPreview() async {
    final v = _preview; _preview = null;
    await v?.dispose();
    final p = _path; _path = null;
    if (p != null) { try { File(p).deleteSync(); } catch (_) {} }
  }

  Future<void> _send() async {
    final p = _path; if (p == null || _sending) return;
    setState(() => _sending = true);
    await _preview?.pause();
    final ok = await ApiService.instance.chatSendVideo(widget.conversationId, p, poster: _poster);
    if (!mounted) return;
    setState(() => _sending = false);
    if (ok) { Navigator.of(context).pop(true); return; }
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(L(context, 'لم يُرسَل الفيديو', 'The video was not sent'))));
  }

  @override
  void dispose() {
    _tick?.cancel();
    final cam = _cam;
    if (cam != null) {
      if (_recording) { cam.stopVideoRecording().then((f) { try { File(f.path).deleteSync(); } catch (_) {} }).catchError((_) {}); }
      cam.dispose();
    }
    _preview?.dispose();
    final p = _path;
    if (p != null) { try { File(p).deleteSync(); } catch (_) {} }
    super.dispose();
  }

  String _mmss(int s) => '${s ~/ 60}:${(s % 60).toString().padLeft(2, '0')}';

  @override
  Widget build(BuildContext context) {
    final v = _preview;
    final showing = v != null && v.value.isInitialized;
    final cam = _cam;
    return Scaffold(
      backgroundColor: Colors.black,
      appBar: AppBar(backgroundColor: Colors.black, foregroundColor: Colors.white, title: Text(L(context, 'رسالة فيديو', 'Video message'), style: const TextStyle(fontSize: 15))),
      body: SafeArea(
        child: Column(children: [
          Expanded(
            child: Center(
              child: _failed
                  ? Padding(padding: const EdgeInsets.all(24), child: Text(L(context, 'تعذّر فتح الكاميرا', 'The camera could not be opened'), style: const TextStyle(color: Colors.white70)))
                  : showing
                      ? GestureDetector(
                          onTap: () => v.value.isPlaying ? v.pause() : v.play(),
                          child: AspectRatio(aspectRatio: v.value.aspectRatio, child: Stack(alignment: Alignment.center, children: [
                            VideoPlayer(v),
                            if (!v.value.isPlaying) Container(width: 58, height: 58, decoration: const BoxDecoration(color: Colors.black38, shape: BoxShape.circle), child: const Icon(Lu.play, color: Colors.white, size: 28)),
                          ])),
                        )
                      : cam == null || !cam.value.isInitialized
                          ? const CircularProgressIndicator(color: Colors.white)
                          : CameraPreview(cam),
            ),
          ),
          Container(
            color: const Color(0xFF0C1837),
            padding: const EdgeInsets.fromLTRB(18, 14, 18, 18),
            child: Row(children: [
              Icon(Lu.circle, size: 10, color: _recording ? const Color(0xFFD23C3C) : Colors.white38),
              const SizedBox(width: 8),
              // a duration reads left to right in either language
              Text('${_mmss(_secs)}  /  ${_mmss(_max)}', textDirection: TextDirection.ltr,
                  style: const TextStyle(color: Colors.white, fontSize: 13, fontFeatures: [FontFeature.tabularFigures()])),
              const Spacer(),
              if (showing && !_recording) ...[
                TextButton.icon(onPressed: _sending ? null : _start, style: TextButton.styleFrom(foregroundColor: Colors.white),
                    icon: const Icon(Lu.rotateCcw, size: 16), label: Text(L(context, 'إعادة التسجيل', 'Record again'))),
                const SizedBox(width: 8),
                FilledButton.icon(onPressed: _sending ? null : _send, style: FilledButton.styleFrom(backgroundColor: Brand.blue),
                    icon: _sending ? const SizedBox(width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white)) : const Icon(Lu.sendHorizontal, size: 16),
                    label: Text(L(context, 'إرسال', 'Send'))),
              ] else
                GestureDetector(
                  onTap: cam == null ? null : (_recording ? _stop : _start),
                  child: Container(
                    width: 60, height: 60, alignment: Alignment.center,
                    decoration: BoxDecoration(shape: BoxShape.circle, border: Border.all(color: Colors.white, width: 3)),
                    child: Container(
                      width: _recording ? 22 : 44, height: _recording ? 22 : 44,
                      decoration: BoxDecoration(color: const Color(0xFFD23C3C), borderRadius: BorderRadius.circular(_recording ? 5 : 22)),
                    ),
                  ),
                ),
            ]),
          ),
        ]),
      ),
    );
  }
}
