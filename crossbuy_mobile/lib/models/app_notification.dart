// In-app notification (bilingual) pushed via SignalR / fetched from the API.
class AppNotification {
  final int id;
  final String? titleAr;
  final String? titleEn;
  final String? bodyAr;
  final String? bodyEn;
  final String? type;
  final int? refId;
  bool isRead;
  final DateTime? createdAt;

  AppNotification({
    required this.id,
    this.titleAr,
    this.titleEn,
    this.bodyAr,
    this.bodyEn,
    this.type,
    this.refId,
    this.isRead = false,
    this.createdAt,
  });

  static DateTime? _d(dynamic v) => v == null ? null : DateTime.tryParse(v.toString());

  factory AppNotification.fromJson(Map<String, dynamic> j) => AppNotification(
        id: j['id'] ?? 0,
        titleAr: j['titleAr'],
        titleEn: j['titleEn'],
        bodyAr: j['bodyAr'],
        bodyEn: j['bodyEn'],
        type: j['type'],
        refId: j['refId'],
        isRead: j['isRead'] ?? false,
        createdAt: _d(j['createdAt']),
      );
}
