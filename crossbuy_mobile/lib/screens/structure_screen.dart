import 'package:flutter/material.dart';

import '../app_config.dart';
import '../l10n/app_localizations.dart';
import '../models/org_position.dart';
import '../services/api_service.dart';
import '../widgets/app_ui.dart';

class StructureScreen extends StatefulWidget {
  const StructureScreen({super.key});

  @override
  State<StructureScreen> createState() => _StructureScreenState();
}

class _StructureScreenState extends State<StructureScreen> {
  late Future<OrgPosition> _future;

  @override
  void initState() {
    super.initState();
    _future = ApiService.instance.getPosition();
  }

  void _reload() => setState(() {
        _future = ApiService.instance.getPosition();
      });

  // palette (shared identity)
  static const _ink = kInk;
  static const _muted = kMuted;
  static const _primary = kPrimary;
  static const _line = Color(0xFFE7ECF3);

  // Metronic-ish color + icon per node type.
  static const _typeColors = {
    1: Color(0xFF1B84FF), // company
    2: Color(0xFF7239EA), // branch
    3: Color(0xFFF6C000), // admin body
    4: Color(0xFF17C653), // position
    5: Color(0xFFFF6F61), // employee
  };
  static const _typeIcons = {
    1: Icons.apartment,
    2: Icons.store_mall_directory,
    3: Icons.folder_special,
    4: Icons.badge,
    5: Icons.person,
  };

  @override
  Widget build(BuildContext context) {
    final t = AppLocalizations.of(context);
    return Container(
      color: kBg,
      child: Column(
        children: [
          AppHeader(title: t.t('structure')),
          Expanded(
            child: FutureBuilder<OrgPosition>(
              future: _future,
              builder: (context, snap) {
                if (snap.connectionState == ConnectionState.waiting) {
                  return const Center(child: CircularProgressIndicator());
                }
                if (snap.hasError || !snap.hasData) {
                  return _centerState(
                      Icons.cloud_off, t.t('noData'), _reload, t.t('retry'));
                }
                final p = snap.data!;
                if (!p.placed) {
                  return _centerState(Icons.account_tree_outlined,
                      t.t('notPlaced'), _reload, t.t('retry'));
                }
                return RefreshIndicator(
                  onRefresh: () async => _reload(),
                  child: ListView(
                    padding: const EdgeInsets.fromLTRB(16, 16, 16, 28),
                    children: [
                      _reportingLineCard(context, p, t),
                      const SizedBox(height: 18),
                      _teamCard(context, p, t),
                      const SizedBox(height: 18),
                      _totalTeamCard(context, p, t),
                    ],
                  ),
                );
              },
            ),
          ),
        ],
      ),
    );
  }

  String _nm(bool ar, String? a, String? b) =>
      (ar ? a : b)?.trim().isNotEmpty == true ? (ar ? a : b)! : (b ?? a ?? '-');

  // ============ Reporting line: one card, vertical timeline + nodes ============
  Widget _reportingLineCard(
      BuildContext context, OrgPosition p, AppLocalizations t) {
    final ar = t.isAr;
    final rows = <Widget>[];

    // total node count = ancestors + me (for first/last flags)
    final total = p.ancestors.length + 1;

    for (var i = 0; i < p.ancestors.length; i++) {
      final n = p.ancestors[i];
      rows.add(_timelineRow(
        first: i == 0,
        last: false,
        isMe: false,
        child: _nodeBody(
          type: n.type ?? 0,
          color: _typeColors[n.type] ?? Colors.grey,
          icon: _typeIcons[n.type] ?? Icons.circle,
          image: n.image,
          label: _nm(ar, n.typeNameAr, n.typeNameEn),
          name: _nm(ar, n.nameAr, n.nameEn),
          showDivider: true, // divider under every ancestor row
        ),
      ));
    }

    // my own highlighted node at the bottom
    rows.add(_timelineRow(
      first: total == 1,
      last: true,
      isMe: true,
      child: _meNode(context, p, t),
    ));

    return SectionCard(
      icon: Icons.account_tree_outlined,
      title: t.t('reportingLine'),
      children: rows,
    );
  }

  // a single timeline row: [dot+line] [node body]
  Widget _timelineRow({
    required bool first,
    required bool last,
    required bool isMe,
    required Widget child,
  }) {
    return IntrinsicHeight(
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // timeline rail
          SizedBox(
            width: 22,
            child: Column(
              children: [
                Expanded(
                  child: Center(
                    child: Container(
                      width: 2,
                      color: first ? Colors.transparent : _line,
                    ),
                  ),
                ),
                Container(
                  width: isMe ? 14 : 11,
                  height: isMe ? 14 : 11,
                  decoration: BoxDecoration(
                    color: isMe ? _primary : Colors.white,
                    shape: BoxShape.circle,
                    border: Border.all(
                        color: isMe ? _primary : const Color(0xFFB8C2D0),
                        width: 2),
                  ),
                ),
                Expanded(
                  child: Center(
                    child: Container(
                      width: 2,
                      color: last ? Colors.transparent : _line,
                    ),
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(width: 12),
          Expanded(child: child),
        ],
      ),
    );
  }

  // ancestor node body (icon/logo + label + name), optional divider beneath
  Widget _nodeBody({
    required int type,
    required Color color,
    required IconData icon,
    String? image,
    required String label,
    required String name,
    required bool showDivider,
  }) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 10),
          child: Row(
            children: [
              _squareNode(image: image, name: name, color: color, icon: icon),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(label,
                        style: const TextStyle(fontSize: 12, color: _muted)),
                    const SizedBox(height: 3),
                    Text(name,
                        style: const TextStyle(
                            fontSize: 16,
                            fontWeight: FontWeight.bold,
                            color: _ink)),
                  ],
                ),
              ),
            ],
          ),
        ),
        if (showDivider) Container(height: 1, color: const Color(0xFFF1F4F9)),
      ],
    );
  }

  // the highlighted "me" node
  Widget _meNode(BuildContext context, OrgPosition p, AppLocalizations t) {
    final ar = t.isAr;
    return Container(
      margin: const EdgeInsets.symmetric(vertical: 10),
      padding: const EdgeInsets.all(10),
      decoration: BoxDecoration(
        color: const Color(0xFFEAF3FF),
        borderRadius: BorderRadius.circular(14),
        border: Border.all(color: _primary, width: 1.4),
      ),
      child: Row(
        children: [
          _circleNode(
              image: p.meImage,
              name: _nm(ar, p.meNameAr, p.meNameEn),
              color: _typeColors[5]!,
              radius: 24),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(_nm(ar, p.positionAr, p.positionEn),
                    style: const TextStyle(
                        fontSize: 12,
                        color: _primary,
                        fontWeight: FontWeight.w600)),
                const SizedBox(height: 3),
                Text(_nm(ar, p.meNameAr, p.meNameEn),
                    style: const TextStyle(
                        fontSize: 16, fontWeight: FontWeight.bold, color: _ink)),
              ],
            ),
          ),
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 5),
            decoration: BoxDecoration(
                color: _primary, borderRadius: BorderRadius.circular(20)),
            child: Text(t.t('you'),
                style: const TextStyle(
                    color: Colors.white,
                    fontSize: 12,
                    fontWeight: FontWeight.bold)),
          ),
        ],
      ),
    );
  }

  // ============ Reporting to me ============
  Widget _teamCard(BuildContext context, OrgPosition p, AppLocalizations t) {
    return SectionCard(
      icon: Icons.groups_outlined,
      title: t.t('myTeam'),
      trailing: p.subordinates.isNotEmpty ? CountBadge(p.subordinates.length) : null,
      children: [
        if (p.subordinates.isEmpty)
          _emptyTeam(context, t)
        else
          ...List.generate(p.subordinates.length, (i) {
            return Padding(
              padding: EdgeInsets.only(
                  bottom: i == p.subordinates.length - 1 ? 0 : 10),
              child: _personTile(context, p.subordinates[i], t),
            );
          }),
      ],
    );
  }

  Widget _personTile(BuildContext context, OrgPerson s, AppLocalizations t) {
    final ar = t.isAr;
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: Colors.white,
        borderRadius: BorderRadius.circular(14),
        border: Border.all(color: const Color(0xFFEEF1F6)),
      ),
      child: Row(
        children: [
          _circleNode(
              image: s.image,
              name: _nm(ar, s.nameAr, s.nameEn),
              color: _typeColors[5]!,
              radius: 22),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(_nm(ar, s.nameAr, s.nameEn),
                    style: const TextStyle(
                        fontSize: 15,
                        fontWeight: FontWeight.bold,
                        color: _ink)),
                const SizedBox(height: 3),
                Text(_nm(ar, s.positionAr, s.positionEn),
                    style: const TextStyle(fontSize: 12.5, color: _muted)),
              ],
            ),
          ),
          Icon(ar ? Icons.chevron_left : Icons.chevron_right,
              color: const Color(0xFFB5B9C5)),
        ],
      ),
    );
  }

  // ============ Total team size ============
  Widget _totalTeamCard(
      BuildContext context, OrgPosition p, AppLocalizations t) {
    final n = p.subordinates.length;
    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        gradient: const LinearGradient(
          begin: Alignment.topRight, end: Alignment.bottomLeft,
          colors: [Color(0xFF12347A), Color(0xFF18356E)],
        ),
        borderRadius: BorderRadius.circular(16),
      ),
      child: Row(
        children: [
          Container(
            width: 46,
            height: 46,
            decoration: BoxDecoration(
                color: Colors.white.withValues(alpha: 0.12),
                borderRadius: BorderRadius.circular(12)),
            child: const Icon(Icons.account_tree, color: Colors.white, size: 24),
          ),
          const SizedBox(width: 14),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(t.t('totalTeamSize'),
                    style: const TextStyle(
                        fontSize: 15.5,
                        fontWeight: FontWeight.bold,
                        color: Colors.white)),
                const SizedBox(height: 3),
                Text(
                    t.isAr
                        ? 'تدير $n من المرؤوسين المباشرين'
                        : 'You manage $n direct report${n == 1 ? '' : 's'}',
                    style: const TextStyle(fontSize: 12.5, color: Color(0xFFB7C4DE))),
              ],
            ),
          ),
          Container(
            width: 50,
            height: 50,
            alignment: Alignment.center,
            decoration: BoxDecoration(
                color: Colors.white.withValues(alpha: 0.14),
                borderRadius: BorderRadius.circular(12)),
            child: Text('$n',
                style: const TextStyle(
                    fontSize: 24, fontWeight: FontWeight.bold, color: Colors.white)),
          ),
        ],
      ),
    );
  }

  // ---- node visuals: real image first, generated initials/icon as fallback ----

  String? _imgUrl(String? raw) {
    if (raw == null || raw.isEmpty) return null;
    return raw.startsWith('http') ? raw : '${AppConfig.mediaBaseUrl}$raw';
  }

  String _initialsOf(String name) {
    final parts =
        name.trim().split(RegExp(r'\s+')).where((w) => w.isNotEmpty).toList();
    if (parts.isEmpty) return '?';
    String head(String w) => w.length >= 2 ? w.substring(0, 2) : w;
    final s = parts.length == 1 ? head(parts[0]) : '${parts[0][0]}${parts[1][0]}';
    return s.toUpperCase();
  }

  Widget _circleNode(
      {String? image,
      required String name,
      required Color color,
      double radius = 21}) {
    final url = _imgUrl(image);
    if (url != null) {
      return CircleAvatar(
        radius: radius,
        backgroundColor: color.withValues(alpha: 0.12),
        backgroundImage: NetworkImage(url),
        onBackgroundImageError: (_, __) {},
      );
    }
    return CircleAvatar(
      radius: radius,
      backgroundColor: color.withValues(alpha: 0.14),
      child: Text(_initialsOf(name),
          style: TextStyle(
              color: color,
              fontWeight: FontWeight.bold,
              fontSize: radius * 0.7)),
    );
  }

  Widget _squareNode(
      {String? image,
      required String name,
      required Color color,
      required IconData icon}) {
    final url = _imgUrl(image);
    if (url != null) {
      return ClipRRect(
        borderRadius: BorderRadius.circular(12),
        child: Image.network(url,
            width: 46,
            height: 46,
            fit: BoxFit.cover,
            errorBuilder: (_, __, ___) => _iconBox(color, icon)),
      );
    }
    return _iconBox(color, icon);
  }

  Widget _iconBox(Color color, IconData icon) => Container(
        width: 46,
        height: 46,
        decoration: BoxDecoration(
            color: color.withValues(alpha: 0.14),
            borderRadius: BorderRadius.circular(12)),
        child: Icon(icon, color: color, size: 24),
      );

  // ---- small shared pieces ----

  Widget _emptyTeam(BuildContext context, AppLocalizations t) => Container(
        padding: const EdgeInsets.symmetric(vertical: 22),
        alignment: Alignment.center,
        decoration: BoxDecoration(
            color: Colors.white,
            borderRadius: BorderRadius.circular(14),
            border: Border.all(color: const Color(0xFFEFF2F5))),
        child: Column(
          children: [
            const Icon(Icons.groups_outlined, color: Color(0xFFB5B9C5), size: 34),
            const SizedBox(height: 8),
            Text(t.t('noSubordinates'),
                style: const TextStyle(color: _muted, fontSize: 13)),
          ],
        ),
      );

  Widget _centerState(
          IconData icon, String msg, VoidCallback onRetry, String retry) =>
      Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 56, color: Colors.grey),
            const SizedBox(height: 12),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 32),
              child: Text(msg, textAlign: TextAlign.center),
            ),
            const SizedBox(height: 12),
            FilledButton.tonal(onPressed: onRetry, child: Text(retry)),
          ],
        ),
      );
}
