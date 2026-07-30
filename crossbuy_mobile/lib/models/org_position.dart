/// The employee's place in the org tree: the chain above them and the people under them.
class OrgNodeRef {
  final int id;
  final String? nameAr;
  final String? nameEn;
  final int? type; // 1 company, 2 branch, 3 admin body, 4 position, 5 employee
  final String? typeNameAr;
  final String? typeNameEn;
  final String? image;

  OrgNodeRef({
    required this.id,
    this.nameAr,
    this.nameEn,
    this.type,
    this.typeNameAr,
    this.typeNameEn,
    this.image,
  });

  factory OrgNodeRef.fromJson(Map<String, dynamic> j) => OrgNodeRef(
        id: j['id'] ?? 0,
        nameAr: j['nameAr'],
        nameEn: j['nameEn'],
        type: j['type'],
        typeNameAr: j['typeNameAr'],
        typeNameEn: j['typeNameEn'],
        image: j['image'],
      );
}

class OrgPerson {
  final int id;
  final String? nameAr;
  final String? nameEn;
  final String? positionAr;
  final String? positionEn;
  final String? image;

  OrgPerson({
    required this.id,
    this.nameAr,
    this.nameEn,
    this.positionAr,
    this.positionEn,
    this.image,
  });

  factory OrgPerson.fromJson(Map<String, dynamic> j) => OrgPerson(
        id: j['id'] ?? 0,
        nameAr: j['nameAr'],
        nameEn: j['nameEn'],
        positionAr: j['positionAr'],
        positionEn: j['positionEn'],
        image: j['image'],
      );
}

class OrgPosition {
  final bool placed;
  final int? meId;
  final String? meNameAr;
  final String? meNameEn;
  final String? positionAr;
  final String? positionEn;
  final String? meImage;
  final List<OrgNodeRef> ancestors;
  final List<OrgPerson> subordinates;

  OrgPosition({
    required this.placed,
    this.meId,
    this.meNameAr,
    this.meNameEn,
    this.positionAr,
    this.positionEn,
    this.meImage,
    this.ancestors = const [],
    this.subordinates = const [],
  });

  factory OrgPosition.fromJson(Map<String, dynamic> j) {
    final me = j['me'] as Map<String, dynamic>?;
    return OrgPosition(
      placed: j['placed'] == true,
      meId: me?['id'],
      meNameAr: me?['nameAr'],
      meNameEn: me?['nameEn'],
      positionAr: me?['positionAr'],
      positionEn: me?['positionEn'],
      meImage: me?['image'],
      ancestors: (j['ancestors'] as List? ?? [])
          .map((e) => OrgNodeRef.fromJson(e as Map<String, dynamic>))
          .toList(),
      subordinates: (j['subordinates'] as List? ?? [])
          .map((e) => OrgPerson.fromJson(e as Map<String, dynamic>))
          .toList(),
    );
  }
}
