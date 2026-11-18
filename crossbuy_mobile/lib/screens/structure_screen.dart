import 'package:flutter/material.dart';

import '../models/org_position.dart';
import '../services/api_service.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';

/// MY TEAM AND STRUCTURE (the owner's reference): where I sit — the reporting line from the company down to me, each
/// level told apart by its icon and name (one colour, never a colour per level) — and the people who report to me,
/// each with their photo.
class StructureScreen extends StatefulWidget {
  const StructureScreen({super.key});
  @override
  State<StructureScreen> createState() => _StructureScreenState();
}

class _StructureScreenState extends State<StructureScreen> {
  late Future<OrgPosition> _future = ApiService.instance.getPosition();

  void _reload() => setState(() => _future = ApiService.instance.getPosition());

  static IconData _icon(int? type) => switch (type) {
        1 => Lu.building2,
        2 => Lu.store,
        3 => Lu.folderTree,
        4 => Lu.badge,
        _ => Lu.user,
      };

  @override
  Widget build(BuildContext context) {
    String nm(String? a, String? b) {
      final x = a?.trim() ?? '', y = b?.trim() ?? '';
      return isAr(context) ? (x.isNotEmpty ? x : y) : (y.isNotEmpty ? y : x);
    }

    return ExecFrame(
      child: FutureBuilder<OrgPosition>(
        future: _future,
        builder: (context, s) {
          final head = [
            if (Navigator.of(context).canPop()) BackLink(L(context, 'رجوع', 'Back')),
            PageHead(L(context, 'فريق العمل', 'My team'), eyebrow: L(context, 'الهيكل وخط التبعية', 'Structure and reporting line')),
          ];
          if (s.hasError) return ExecBody(children: [...head, EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر تحميل الهيكل', 'Could not load the structure'), text: L(context, 'تحقق من الاتصال ثم أعد المحاولة.', 'Check the connection and try again.'))]);
          if (!s.hasData) return const Center(child: CircularProgressIndicator());
          final p = s.data!;
          if (!p.placed) {
            return ExecBody(children: [...head, EmptyNote(icon: Lu.network, title: L(context, 'لست في الهيكل بعد', 'Not placed in the structure yet'), text: L(context, 'تضعك الموارد البشرية في الهيكل التنظيمي، فيظهر خط تبعيتك وفريقك هنا.', 'HR places you in the org structure; your reporting line and team then show here.'))]);
          }
          final me = nm(p.meNameAr, p.meNameEn);
          return ExecBody(
            onRefresh: () async => _reload(),
            children: [
              ...head,
              Container(
                padding: const EdgeInsets.all(16),
                decoration: BoxDecoration(color: Brand.paper, border: Border.all(color: Brand.lineC), borderRadius: BorderRadius.circular(22)),
                child: Row(children: [
                  PersonAvatar(photo: p.meImage, name: me, size: 52, ring: true),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Text(me, style: T.h2),
                      const SizedBox(height: 2),
                      Text(nm(p.positionAr, p.positionEn), style: T.sub),
                    ]),
                  ),
                  Column(children: [
                    Text('${p.subordinates.length}', style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w700, color: Brand.inkC)),
                    Text(L(context, 'يتبعونني', 'report to me'), style: T.small),
                  ]),
                ]),
              ),
              SectionHeading(L(context, 'خط التبعية', 'Reporting line')),
              DataList(children: [
                for (final n in p.ancestors)
                  ItemRow(
                    leading: n.type == 5 ? PersonAvatar(photo: n.image, name: nm(n.nameAr, n.nameEn), size: 34) : IconBox(icon: _icon(n.type)),
                    title: nm(n.nameAr, n.nameEn),
                    subtitle: nm(n.typeNameAr, n.typeNameEn),
                  ),
                ItemRow(
                  leading: PersonAvatar(photo: p.meImage, name: me, size: 34),
                  title: me,
                  subtitle: nm(p.positionAr, p.positionEn),
                  end: Tag.info(L(context, 'أنت', 'You')),
                ),
              ]),
              SectionHeading(L(context, 'فريقي', 'My team'), count: p.subordinates.length),
              if (p.subordinates.isEmpty)
                EmptyNote(icon: Lu.usersRound, title: L(context, 'لا أحد يتبعك مباشرة', 'No direct reports'), text: L(context, 'من يتبعك في الهيكل يظهر هنا بصورته ومسمّاه.', 'Whoever reports to you in the structure shows here with their photo and post.'))
              else
                DataList(children: [
                  for (final m in p.subordinates)
                    ItemRow(
                      leading: PersonAvatar(photo: m.image, name: nm(m.nameAr, m.nameEn), size: 38),
                      title: nm(m.nameAr, m.nameEn),
                      subtitle: nm(m.positionAr, m.positionEn),
                    ),
                ]),
            ],
          );
        },
      ),
    );
  }
}
