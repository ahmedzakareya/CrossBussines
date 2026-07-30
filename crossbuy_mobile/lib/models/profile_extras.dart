// Real activity feed items + employee documents for the profile screen.
class ActivityItem {
  final String? type;
  final String? titleAr;
  final String? titleEn;
  final String? descAr;
  final String? descEn;
  final DateTime? at;

  ActivityItem({this.type, this.titleAr, this.titleEn, this.descAr, this.descEn, this.at});

  factory ActivityItem.fromJson(Map<String, dynamic> j) => ActivityItem(
        type: j['type'],
        titleAr: j['titleAr'],
        titleEn: j['titleEn'],
        descAr: j['descAr'],
        descEn: j['descEn'],
        at: j['at'] == null ? null : DateTime.tryParse(j['at'].toString()),
      );
}

class DocItem {
  final int id;
  final String? name;
  final String? path;
  final DateTime? uploadedAt;

  DocItem({required this.id, this.name, this.path, this.uploadedAt});

  factory DocItem.fromJson(Map<String, dynamic> j) => DocItem(
        id: j['id'] ?? 0,
        name: j['name'],
        path: j['path'],
        uploadedAt: j['uploadedAt'] == null ? null : DateTime.tryParse(j['uploadedAt'].toString()),
      );
}
