import 'package:flutter/widgets.dart';

import '../widgets/ui.dart';

/// HOSPITAL H176 — internal calls, as the app reads them (api/calls and the /hubs/calls messages).

String _s(dynamic v) => v == null ? '' : v.toString();
String? _n(dynamic v) => v == null || v.toString().isEmpty ? null : v.toString();

/// A person a call can be made to (or who calls). The hub's cards carry one name in the connection's language; the
/// API's carry both — `display` picks for the app's language either way.
class CallPerson {
  final int id;
  final String name, nameEn;
  final String? photo, title, titleEn, department, departmentEn;
  final bool onCall;
  String state;   // on | busy | off

  CallPerson({required this.id, required this.name, required this.nameEn, this.photo, this.title, this.titleEn, this.department, this.departmentEn, this.onCall = false, this.state = 'off'});

  factory CallPerson.fromJson(Map<String, dynamic> j) => CallPerson(
        id: (j['id'] as num).toInt(),
        name: _s(j['name']),
        nameEn: _s(j['nameEn'] ?? j['name']),
        photo: _n(j['photo']),
        title: _n(j['title']),
        titleEn: _n(j['titleEn']),
        department: _n(j['department']),
        departmentEn: _n(j['departmentEn']),
        onCall: j['onCall'] == true,
        state: _s(j['state']).isEmpty ? 'off' : _s(j['state']),
      );

  String display(BuildContext c) => isAr(c) ? (name.isNotEmpty ? name : nameEn) : (nameEn.isNotEmpty ? nameEn : name);
  String role(BuildContext c) => [isAr(c) ? (title ?? titleEn) : (titleEn ?? title), isAr(c) ? (department ?? departmentEn) : (departmentEn ?? department)]
      .whereType<String>().where((x) => x.isNotEmpty).join(' · ');
}

/// The patient a call is about (the hub's patient card).
class CallPatient {
  final int? id;
  final String name, mrn;
  final String? photo;
  CallPatient({this.id, required this.name, required this.mrn, this.photo});
  static CallPatient? fromJson(dynamic j) {
    if (j is! Map) return null;
    return CallPatient(id: (j['id'] as num?)?.toInt(), name: _s(j['name']), mrn: _s(j['mrn']), photo: _n(j['photo']));
  }
}

/// What this person can do with calls here (api/calls/me).
class CallsMe {
  final bool installed, video, share, group, recording, mayRecord;
  final int ringSeconds, maxHeight, groupMax, recordingDays, missed;
  final String recKind;
  final CallPerson? me;
  CallsMe({this.installed = false, this.video = true, this.share = true, this.group = false, this.recording = false, this.mayRecord = false, this.ringSeconds = 30,
      this.maxHeight = 720, this.groupMax = 4, this.recordingDays = 90, this.missed = 0, this.recKind = 'voice', this.me});
  factory CallsMe.fromJson(Map<String, dynamic> j) {
    final s = (j['settings'] as Map?)?.cast<String, dynamic>() ?? const {};
    return CallsMe(
      installed: j['installed'] == true,
      video: s['video'] != false, share: s['share'] != false, group: s['group'] == true, recording: s['recording'] == true, mayRecord: s['mayRecord'] == true,
      ringSeconds: (s['ringSeconds'] as num?)?.toInt() ?? 30, maxHeight: (s['maxHeight'] as num?)?.toInt() ?? 720, groupMax: (s['groupMax'] as num?)?.toInt() ?? 4,
      recordingDays: (s['recordingDays'] as num?)?.toInt() ?? 90, recKind: _s(s['recKind']), missed: (j['missed'] as num?)?.toInt() ?? 0,
      me: j['me'] is Map ? CallPerson.fromJson((j['me'] as Map).cast<String, dynamic>()) : null,
    );
  }
}

/// One line of my call log.
class CallLogRow {
  final int id;
  final bool outgoing, video, group, missedForMe;
  final String status;
  final String? missedWhy, reply, note;
  final DateTime startedAt;
  final int? seconds;
  final CallPerson? other;
  final List<CallPerson> members;
  final String? patient, patientEn, mrn;
  final int? recordingId, recordingSeconds;
  final String? recordingKind;

  CallLogRow({required this.id, required this.outgoing, required this.video, required this.group, required this.missedForMe, required this.status, this.missedWhy, this.reply,
      this.note, required this.startedAt, this.seconds, this.other, this.members = const [], this.patient, this.patientEn, this.mrn, this.recordingId, this.recordingSeconds, this.recordingKind});

  factory CallLogRow.fromJson(Map<String, dynamic> j) {
    final p = j['patient'] as Map?;
    final r = j['recording'] as Map?;
    return CallLogRow(
      id: (j['id'] as num).toInt(), outgoing: j['outgoing'] == true, video: j['video'] == true, group: j['group'] == true, missedForMe: j['missedForMe'] == true,
      status: _s(j['status']), missedWhy: _n(j['missedWhy']), reply: _n(j['reply']), note: _n(j['note']),
      startedAt: DateTime.tryParse(_s(j['startedAt']))?.toLocal() ?? DateTime.now(),
      seconds: (j['seconds'] as num?)?.toInt(),
      other: j['other'] is Map ? CallPerson.fromJson((j['other'] as Map).cast<String, dynamic>()) : null,
      members: (j['members'] as List?)?.whereType<Map>().map((m) => CallPerson.fromJson(m.cast<String, dynamic>())).toList() ?? const [],
      patient: p == null ? null : _n(p['name']), patientEn: p == null ? null : _n(p['nameEn']), mrn: p == null ? null : _n(p['mrn']),
      recordingId: (r?['id'] as num?)?.toInt(), recordingSeconds: (r?['seconds'] as num?)?.toInt(), recordingKind: r == null ? null : _n(r['kind']),
    );
  }
}

/// mm:ss (or h:mm:ss), always left to right.
String mmss(int s) {
  final h = s ~/ 3600, m = (s % 3600) ~/ 60, x = s % 60;
  String two(int v) => v.toString().padLeft(2, '0');
  return h > 0 ? '$h:${two(m)}:${two(x)}' : '${two(m)}:${two(x)}';
}
