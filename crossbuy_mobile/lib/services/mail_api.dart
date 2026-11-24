import 'package:dio/dio.dart';

import 'api_service.dart';

/// البريد — /api/mail (owner, 2026-11-24, approving the prototype pages mail · mail-read · mail-compose). The same
/// mailbox the browser has: my copies only; colleagues' mail delivered inside, mail from outside through the company's
/// own account; the assistant on the company's AI service.
class MailApi {
  MailApi._();
  static final MailApi instance = MailApi._();
  Dio get _dio => ApiService.instance.http;

  Future<Map<String, dynamic>?> counts() async {
    try { return (await _dio.get('/api/mail/counts')).data as Map<String, dynamic>; } on DioException { return null; }
  }

  Future<List<MailRow>> list(String folder, {String? q}) async {
    final d = (await _dio.get('/api/mail/list', queryParameters: {'folder': folder, if (q != null && q.isNotEmpty) 'q': q})).data;
    return (d as List).map((e) => MailRow.fromJson(e as Map<String, dynamic>)).toList();
  }

  Future<Map<String, dynamic>?> message(int id) async {
    try { return (await _dio.get('/api/mail/message', queryParameters: {'id': id})).data as Map<String, dynamic>; } on DioException { return null; }
  }

  Future<Map<String, dynamic>?> compose(int parentId, String kind) async {
    try { return (await _dio.get('/api/mail/compose', queryParameters: {'parentId': parentId, 'kind': kind})).data as Map<String, dynamic>; } on DioException { return null; }
  }

  Future<Map<String, dynamic>?> send({required String to, String? cc, required String subject, required String text, int? parentId, String? kind,
      List<int> libraryIds = const [], List<(String path, String name)> files = const []}) async {
    final form = FormData.fromMap({
      'to': to, if (cc != null && cc.isNotEmpty) 'cc': cc, 'subject': subject, 'text': text,
      if (parentId != null) 'parentId': parentId, if (kind != null) 'kind': kind,
      if (libraryIds.isNotEmpty) 'libraryIds': libraryIds.join(','),
    });
    for (final f in files) {
      form.files.add(MapEntry('files', await MultipartFile.fromFile(f.$1, filename: f.$2)));
    }
    try {
      return (await _dio.post('/api/mail/send', data: form, options: Options(sendTimeout: const Duration(minutes: 3), receiveTimeout: const Duration(minutes: 2)))).data as Map<String, dynamic>;
    } on DioException catch (e) {
      final d = e.response?.data;
      return d is Map<String, dynamic> ? d : {'ok': false};
    }
  }

  Future<bool> delete(List<int> ids, {bool permanent = false}) async {
    try { return (await _dio.post('/api/mail/delete', queryParameters: {'ids': ids.join(','), 'permanent': permanent})).data['ok'] == true; } on DioException { return false; }
  }

  Future<bool> restore(int id) async {
    try { return (await _dio.post('/api/mail/restore', queryParameters: {'id': id})).data['ok'] == true; } on DioException { return false; }
  }

  Future<bool> star(int id) async {
    try { return (await _dio.post('/api/mail/star', queryParameters: {'id': id})).data['ok'] == true; } on DioException { return false; }
  }

  /// Starts the assistant (summary · reply · translate) and waits for its answer; null when it could not answer.
  Future<Map<String, dynamic>?> assist(int id, String mode, {String? instruction}) async {
    try {
      final s = (await _dio.post('/api/mail/assist', queryParameters: {'id': id, 'mode': mode, if (instruction != null && instruction.isNotEmpty) 'instruction': instruction})).data as Map;
      final job = s['job'] as String?;
      if (s['ok'] != true || job == null) return null;
      for (var i = 0; i < 150; i++) {
        await Future.delayed(const Duration(seconds: 2));
        final r = (await _dio.get('/api/mail/assist/status', queryParameters: {'job': job})).data as Map;
        if (r['status'] == 'working') continue;
        return r['status'] == 'done' && r['result'] is Map ? Map<String, dynamic>.from(r['result'] as Map) : null;
      }
    } on DioException { /* the service is away: say so */ }
    return null;
  }

  Future<List<Map<String, dynamic>>> contacts(String q) async {
    try { return ((await _dio.get('/api/mail/contacts', queryParameters: {'q': q})).data as List).cast<Map<String, dynamic>>(); } on DioException { return const []; }
  }

  Future<Map<String, dynamic>?> library({int? folderId}) async {
    try { return (await _dio.get('/api/mail/library', queryParameters: {if (folderId != null) 'folderId': folderId})).data as Map<String, dynamic>; } on DioException { return null; }
  }
}

class MailRow {
  final int id;
  final String subject, name;
  final String? preview, photo, box;
  final DateTime? at;
  final bool unread, starred, hasAttachment, external, invite;
  MailRow({required this.id, required this.subject, required this.name, this.preview, this.photo, this.box, this.at,
      this.unread = false, this.starred = false, this.hasAttachment = false, this.external = false, this.invite = false});
  factory MailRow.fromJson(Map<String, dynamic> j) => MailRow(
        id: (j['id'] as num).toInt(), subject: j['subject'] as String? ?? '', name: j['name'] as String? ?? '',
        preview: j['preview'] as String?, photo: j['photo'] as String?, box: j['box'] as String?,
        at: mailTime(j['at']),
        unread: j['unread'] == true, starred: j['starred'] == true, hasAttachment: j['hasAttachment'] == true,
        external: j['external'] == true, invite: j['invite'] == true,
      );
}

/// The server stores UTC and sends it without a zone; read it as UTC, show it local.
DateTime? mailTime(Object? v) {
  if (v == null) return null;
  var t = '$v';
  if (!RegExp(r'(Z|[+-]\d\d:?\d\d)$').hasMatch(t)) t = '${t}Z';
  return DateTime.tryParse(t)?.toLocal();
}
