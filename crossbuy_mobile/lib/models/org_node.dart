class OrgNode {
  final int id;
  final int? parentId;
  final String? nameAr;
  final String? nameEn;
  final int? type;
  final int? objectId;
  final int? sort;

  OrgNode({
    required this.id,
    this.parentId,
    this.nameAr,
    this.nameEn,
    this.type,
    this.objectId,
    this.sort,
  });

  factory OrgNode.fromJson(Map<String, dynamic> j) => OrgNode(
        id: j['id'] ?? 0,
        parentId: j['parentId'],
        nameAr: j['nameAr'],
        nameEn: j['nameEn'],
        type: j['type'],
        objectId: j['objectId'],
        sort: j['sort'],
      );
}
