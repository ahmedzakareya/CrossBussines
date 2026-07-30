import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intl/intl.dart' hide TextDirection;
import 'package:provider/provider.dart';

import '../app_config.dart';
import '../l10n/app_localizations.dart';
import '../models/dashboard.dart';
import '../models/employee.dart';
import '../models/profile_extras.dart';
import '../providers/auth_provider.dart';
import '../providers/locale_provider.dart';
import '../services/app_nav.dart';
import '../services/api_service.dart';
import 'login_screen.dart';

const _primary = Color(0xFF1B84FF);
const _ink = Color(0xFF181C32);
const _muted = Color(0xFF99A1B7);
const _green = Color(0xFF17C653);

class ProfileScreen extends StatefulWidget {
  const ProfileScreen({super.key});
  @override
  State<ProfileScreen> createState() => _ProfileScreenState();
}

class _ProfileScreenState extends State<ProfileScreen> {
  late Future<_Bundle> _future;

  @override
  void initState() {
    super.initState();
    _future = _load();
    AppNav.dataChanged.addListener(_reload);
    AppNav.tabIndex.addListener(_onTab);
  }

  void _onTab() {
    if (AppNav.tabIndex.value == 1) _reload();
  }

  @override
  void dispose() {
    AppNav.dataChanged.removeListener(_reload);
    AppNav.tabIndex.removeListener(_onTab);
    super.dispose();
  }

  Future<_Bundle> _load() async {
    final emp = await ApiService.instance.getProfile();
    DashboardData? dash;
    List<ActivityItem> act = const [];
    List<DocItem> docs = const [];
    try { dash = await ApiService.instance.getDashboard(); } catch (_) {}
    try { act = await ApiService.instance.getActivity(); } catch (_) {}
    try { docs = await ApiService.instance.getDocuments(); } catch (_) {}
    return _Bundle(emp, dash, act, docs);
  }

  void _reload() {
    if (mounted) setState(() => _future = _load());
  }

  @override
  Widget build(BuildContext context) {
    final t = AppLocalizations.of(context);
    return Scaffold(
      backgroundColor: const Color(0xFFEEF2F8),
      body: FutureBuilder<_Bundle>(
        future: _future,
        builder: (context, snap) {
          if (snap.connectionState == ConnectionState.waiting) {
            return const Center(child: CircularProgressIndicator());
          }
          if (snap.hasError || !snap.hasData) {
            return Center(
              child: Column(mainAxisSize: MainAxisSize.min, children: [
                const Icon(Icons.cloud_off, size: 56, color: Colors.grey),
                const SizedBox(height: 12),
                Text(t.t('noData')),
                const SizedBox(height: 12),
                FilledButton.tonal(onPressed: _reload, child: Text(t.t('retry'))),
              ]),
            );
          }
          return _Body(bundle: snap.data!);
        },
      ),
    );
  }
}

class _Bundle {
  final Employee emp;
  final DashboardData? dash;
  final List<ActivityItem> activity;
  final List<DocItem> docs;
  _Bundle(this.emp, this.dash, this.activity, this.docs);
}

String _ar(BuildContext c, String a, String b) =>
    AppLocalizations.of(c).isAr ? a : b;

class _Body extends StatelessWidget {
  final _Bundle bundle;
  const _Body({required this.bundle});

  @override
  Widget build(BuildContext context) {
    final e = bundle.emp;
    final isAr = AppLocalizations.of(context).isAr;
    final job = (isAr ? e.jobTitleAr : e.jobTitleEn) ?? e.jobTitleEn ?? e.jobTitleAr;
    final company = (isAr ? e.companyAr : e.companyEn) ?? e.companyEn ?? e.companyAr;
    final dept = (isAr ? e.departmentAr : e.departmentEn) ?? e.departmentEn ?? e.departmentAr;
    final df = DateFormat('yyyy/MM/dd');
    final joining = e.dateOfJoining == null ? '—' : df.format(e.dateOfJoining!);
    final empNo = 'EMP${e.id.toString().padLeft(3, '0')}';

    return CustomScrollView(slivers: [
      SliverToBoxAdapter(
        child: Stack(clipBehavior: Clip.none, children: [
          _Header(employee: e, job: job),
          PositionedDirectional(
            start: 14, end: 14, bottom: -30,
            child: _StatsBar(
              status: e.isActive ? _ar(context, 'نشط', 'Active') : _ar(context, 'غير نشط', 'Inactive'),
              dept: dept ?? '—',
              empNo: empNo,
              joining: joining,
            ),
          ),
        ]),
      ),
      SliverToBoxAdapter(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(14, 44, 14, 18),
          child: Column(children: [
              IntrinsicHeight(
                child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Expanded(child: _personalCard(context, e)),
                  const SizedBox(width: 12),
                  Expanded(child: _workCard(context, e, job, company, dept)),
                ]),
              ),
              const SizedBox(height: 12),
              _activitiesCard(context),
              const SizedBox(height: 12),
              _documentsCard(context),
              const SizedBox(height: 12),
              _quickStats(context),
            ]),
          ),
        ),
    ]);
  }

  IconData _actIcon(String? t) =>
      t == 'leave_approved' ? Icons.check_circle : (t == 'leave_rejected' ? Icons.cancel : Icons.flight_takeoff);
  Color _actColor(String? t) =>
      t == 'leave_approved' ? _green : (t == 'leave_rejected' ? const Color(0xFFD9214E) : _primary);

  Widget _personalCard(BuildContext c, Employee e) => _SectionCard(
        icon: Icons.person_outline,
        title: _ar(c, 'المعلومات الشخصية', 'Personal Info'),
        footer: _ar(c, 'عرض المزيد', 'Show more'),
        children: [
          _InfoLine(Icons.mail_outline, _ar(c, 'البريد الإلكتروني', 'Email'), e.email, copy: true),
          _InfoLine(Icons.phone_outlined, _ar(c, 'الهاتف', 'Phone'), e.phoneNumber, copy: true),
          _InfoLine(Icons.location_on_outlined, _ar(c, 'العنوان', 'Address'), e.address, copy: true),
          _InfoLine(Icons.wc_outlined, _ar(c, 'الجنس', 'Gender'), e.gender),
          _InfoLine(Icons.favorite_outline, _ar(c, 'الحالة الاجتماعية', 'Marital status'), e.maritalStatus),
        ],
      );

  Widget _workCard(BuildContext c, Employee e, String? job, String? company, String? dept) {
    final isAr = AppLocalizations.of(c).isAr;
    final branch = (isAr ? e.branchAr : e.branchEn) ?? e.branchEn ?? e.branchAr;
    return _SectionCard(
      icon: Icons.work_outline,
      title: _ar(c, 'معلومات العمل', 'Work Info'),
      footer: _ar(c, 'عرض المزيد', 'Show more'),
      children: [
        _InfoLine(Icons.bookmark_border, _ar(c, 'المسمى الوظيفي', 'Job title'), job),
        _InfoLine(Icons.apartment_outlined, _ar(c, 'الشركة', 'Company'), company),
        _InfoLine(Icons.account_tree_outlined, _ar(c, 'الإدارة', 'Department'), dept),
        _InfoLine(Icons.location_on_outlined, _ar(c, 'الموقع', 'Location'), branch),
        _InfoLine(Icons.schedule_outlined, _ar(c, 'نوع التوظيف', 'Employment'), e.employmentType),
      ],
    );
  }

  Widget _activitiesCard(BuildContext c) {
    final isAr = AppLocalizations.of(c).isAr;
    final df = DateFormat('MM/dd HH:mm');
    final items = bundle.activity;
    return _SectionCard(
      icon: Icons.trending_up,
      title: _ar(c, 'آخر الأنشطة', 'Recent activity'),
      children: items.isEmpty
          ? [
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 14),
                child: Center(child: Text(_ar(c, 'لا توجد أنشطة بعد', 'No activity yet'), style: const TextStyle(color: _muted, fontSize: 12.5))),
              )
            ]
          : [
              for (var i = 0; i < items.length; i++)
                IntrinsicHeight(
                  child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    _chip(_actIcon(items[i].type), _actColor(items[i].type)),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Padding(
                        padding: const EdgeInsets.only(bottom: 12),
                        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                          Text((isAr ? items[i].titleAr : items[i].titleEn) ?? '', style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.bold, color: _ink)),
                          const SizedBox(height: 2),
                          Text((isAr ? items[i].descAr : items[i].descEn) ?? '', style: const TextStyle(fontSize: 10.5, color: _muted)),
                        ]),
                      ),
                    ),
                    const SizedBox(width: 6),
                    Column(children: [
                      const SizedBox(height: 3),
                      Container(
                        width: 12, height: 12,
                        decoration: BoxDecoration(
                          color: _actColor(items[i].type), shape: BoxShape.circle,
                          border: Border.all(color: Colors.white, width: 2),
                          boxShadow: [BoxShadow(color: _actColor(items[i].type).withValues(alpha: 0.3), blurRadius: 4)],
                        ),
                      ),
                      if (i != items.length - 1) Expanded(child: Container(width: 2, color: const Color(0xFFE7ECF3))),
                    ]),
                    const SizedBox(width: 8),
                    SizedBox(
                      width: 56,
                      child: Padding(
                        padding: const EdgeInsets.only(top: 1),
                        child: Text(items[i].at != null ? df.format(items[i].at!.toLocal()) : '', style: const TextStyle(fontSize: 9, color: _muted)),
                      ),
                    ),
                  ]),
                ),
            ],
    );
  }

  Widget _documentsCard(BuildContext c) {
    final df = DateFormat('yyyy/MM/dd');
    final docs = bundle.docs;
    return _SectionCard(
      icon: Icons.folder_open_outlined,
      title: _ar(c, 'المستندات والمرفقات', 'Documents'),
      children: docs.isEmpty
          ? [
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 14),
                child: Center(child: Text(_ar(c, 'لا توجد مستندات', 'No documents'), style: const TextStyle(color: _muted, fontSize: 12.5))),
              )
            ]
          : docs.map((d) {
              final src = (d.path ?? d.name ?? '').toLowerCase();
              final pdf = src.contains('pdf');
              return Padding(
                padding: const EdgeInsets.symmetric(vertical: 7),
                child: Row(children: [
                  Icon(pdf ? Icons.picture_as_pdf : Icons.insert_drive_file, color: pdf ? const Color(0xFFE0506A) : _green, size: 26),
                  const SizedBox(width: 10),
                  Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text(d.name ?? '-', overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600, color: _ink)),
                    if (d.uploadedAt != null)
                      Text('${_ar(c, 'تم الرفع', 'Uploaded')} ${df.format(d.uploadedAt!.toLocal())}', style: const TextStyle(fontSize: 10, color: _muted)),
                  ])),
                  const Icon(Icons.download_outlined, size: 20, color: _muted),
                ]),
              );
            }).toList(),
    );
  }

  Widget _quickStats(BuildContext c) {
    final d = bundle.dash;
    final totalRemaining = d == null ? 0 : d.balances.fold<int>(0, (s, b) => s + b.remaining);
    final stats = [
      ['$totalRemaining', _ar(c, 'رصيد الإجازات', 'Leave balance'), Icons.event_available, _primary],
      ['${d?.myPending ?? 0}', _ar(c, 'طلبات معلّقة', 'Pending'), Icons.hourglass_top, const Color(0xFFF6A609)],
      ['${d?.myApprovedThisYear ?? 0}', _ar(c, 'معتمَدة', 'Approved'), Icons.check_circle, _green],
      ['${d?.teamSize ?? 0}', _ar(c, 'فريقي', 'Team'), Icons.groups, const Color(0xFF22CCE2)],
    ];
    return Container(
      padding: const EdgeInsets.fromLTRB(10, 10, 10, 10),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(16),
        gradient: const LinearGradient(begin: Alignment.topRight, end: Alignment.bottomLeft, colors: [Color(0xFF12347A), Color(0xFF18356E)]),
      ),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Padding(
          padding: const EdgeInsets.only(bottom: 8, right: 4, left: 4),
          child: Text(_ar(c, 'إحصائيات سريعة', 'Quick stats'), textAlign: TextAlign.start,
              style: const TextStyle(color: Colors.white, fontWeight: FontWeight.bold, fontSize: 13)),
        ),
        IntrinsicHeight(
          child: Row(crossAxisAlignment: CrossAxisAlignment.stretch, children: stats.map((s) => Expanded(
            child: Container(
              margin: const EdgeInsets.symmetric(horizontal: 3),
              padding: const EdgeInsets.symmetric(vertical: 7, horizontal: 3),
              decoration: BoxDecoration(
                color: Colors.white.withValues(alpha: 0.06),
                borderRadius: BorderRadius.circular(12),
                border: Border.all(color: Colors.white.withValues(alpha: 0.10)),
              ),
              child: Row(mainAxisAlignment: MainAxisAlignment.center, children: [
                Container(width: 30, height: 30, decoration: BoxDecoration(color: (s[3] as Color).withValues(alpha: 0.22), borderRadius: BorderRadius.circular(8)), child: Icon(s[2] as IconData, color: s[3] as Color, size: 16)),
                const SizedBox(width: 6),
                Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisAlignment: MainAxisAlignment.center, children: [
                  Text(s[0] as String, style: const TextStyle(color: Colors.white, fontSize: 16, fontWeight: FontWeight.bold)),
                  Text(s[1] as String, maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(color: Color(0xFFB7C4DE), fontSize: 8.5, height: 1.1)),
                ])),
              ]),
            ),
          )).toList()),
        ),
      ]),
    );
  }

  Widget _chip(IconData ic, Color col) => Container(
        width: 34, height: 34,
        decoration: BoxDecoration(color: col.withValues(alpha: 0.14), borderRadius: BorderRadius.circular(9)),
        child: Icon(ic, color: col, size: 17),
      );
}

// ============ Header (matches the target mockup) ============
class _Header extends StatelessWidget {
  final Employee employee;
  final String? job;
  const _Header({required this.employee, this.job});

  @override
  Widget build(BuildContext context) {
    final t = AppLocalizations.of(context);
    final isAr = t.isAr;
    final name = isAr ? (employee.fullName ?? employee.fullNameEn) : (employee.fullNameEn ?? employee.fullName);
    final img = employee.profileImage;
    final url = (img != null && img.isNotEmpty)
        ? (img.startsWith('http') ? img : '${AppConfig.mediaBaseUrl}$img')
        : null;

    return Container(
      padding: const EdgeInsets.only(bottom: 54),
      decoration: const BoxDecoration(
        gradient: LinearGradient(
          begin: Alignment.topRight, end: Alignment.bottomLeft,
          colors: [Color(0xFF0A1B40), Color(0xFF123569), Color(0xFF1C4A8E)],
        ),
      ),
      child: Stack(children: [
        // city skyline silhouette
        Positioned.fill(
          child: CustomPaint(painter: _SkylinePainter()),
        ),
        SafeArea(
          bottom: false,
          child: Padding(
            padding: const EdgeInsets.fromLTRB(14, 6, 14, 0),
            child: Column(children: [
              // top bar
              Row(children: [
                _circle(isAr ? Icons.arrow_forward : Icons.arrow_back, () {}),
                Expanded(child: Center(child: Text(t.t('profile'),
                    style: const TextStyle(color: Colors.white, fontSize: 18, fontWeight: FontWeight.bold)))),
                _circle(Icons.logout, () async {
                  await context.read<AuthProvider>().logout();
                  if (!context.mounted) return;
                  Navigator.of(context).pushReplacement(MaterialPageRoute(builder: (_) => const LoginScreen()));
                }),
              ]),
              const SizedBox(height: 14),
              // main row — forced LTR for precise placement: [controls | avatar | identity]
              Directionality(
                textDirection: TextDirection.ltr,
                child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  // LEFT: language + quick actions
                  Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    _langPill(context, t),
                    const SizedBox(height: 12),
                    _quickActions(context),
                  ]),
                  const SizedBox(width: 34),
                  // CENTER: avatar with badges
                  _avatar(url),
                  const SizedBox(width: 10),
                  // RIGHT: identity
                  Expanded(
                    child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Row(children: [
                        const Icon(Icons.verified, color: Color(0xFF50CD89), size: 17),
                        const SizedBox(width: 5),
                        Expanded(child: Text(name ?? '-', maxLines: 1, overflow: TextOverflow.ellipsis,
                            style: const TextStyle(color: Colors.white, fontSize: 17, fontWeight: FontWeight.bold))),
                      ]),
                      const SizedBox(height: 4),
                      Text(job ?? '—', style: const TextStyle(color: Colors.white, fontSize: 13.5, fontWeight: FontWeight.w600)),
                      const SizedBox(height: 2),
                      Text(
                          (isAr ? (employee.departmentAr ?? employee.departmentEn) : (employee.departmentEn ?? employee.departmentAr)) ?? '',
                          style: TextStyle(color: Colors.white.withValues(alpha: 0.7), fontSize: 11.5)),
                    ]),
                  ),
                ]),
              ),
            ]),
          ),
        ),
      ]),
    );
  }

  Widget _avatar(String? url) => Stack(clipBehavior: Clip.none, children: [
        Container(
          padding: const EdgeInsets.all(3),
          decoration: const BoxDecoration(color: Colors.white, shape: BoxShape.circle),
          child: CircleAvatar(
            radius: 36,
            backgroundColor: const Color(0xFFE9F3FF),
            backgroundImage: url != null ? NetworkImage(url) : null,
            child: url == null ? const Icon(Icons.person, size: 44, color: _primary) : null,
          ),
        ),
        const Positioned(
          bottom: 4, right: 4,
          child: CircleAvatar(radius: 9, backgroundColor: Colors.white,
              child: CircleAvatar(radius: 6.5, backgroundColor: _green)),
        ),
      ]);

  Widget _langPill(BuildContext context, AppLocalizations t) => InkWell(
        onTap: () => context.read<LocaleProvider>().toggle(),
        borderRadius: BorderRadius.circular(20),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
          decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.16), borderRadius: BorderRadius.circular(20)),
          child: Row(mainAxisSize: MainAxisSize.min, children: [
            const Icon(Icons.language, color: Colors.white, size: 16),
            const SizedBox(width: 6),
            Text(t.t('language'), style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w600, fontSize: 13)),
            const SizedBox(width: 2),
            const Icon(Icons.keyboard_arrow_down, color: Colors.white, size: 17),
          ]),
        ),
      );

  Widget _quickActions(BuildContext context) {
    Widget a(IconData ic, String label) => Padding(
          padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            Icon(ic, color: Colors.white, size: 18),
            const SizedBox(height: 4),
            Text(label, style: const TextStyle(color: Colors.white, fontSize: 9.5)),
          ]),
        );
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 8),
      decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.14), borderRadius: BorderRadius.circular(14)),
      child: Row(mainAxisSize: MainAxisSize.min, children: [
        a(Icons.phone, _ar(context, 'اتصال', 'Call')),
        a(Icons.mail, _ar(context, 'بريد', 'Email')),
        a(Icons.location_on, _ar(context, 'الموقع', 'Map')),
      ]),
    );
  }

  Widget _circle(IconData ic, VoidCallback onTap) => InkWell(
        onTap: onTap, borderRadius: BorderRadius.circular(30),
        child: Container(width: 40, height: 40,
            decoration: BoxDecoration(color: Colors.white.withValues(alpha: 0.16), shape: BoxShape.circle),
            child: Icon(ic, color: Colors.white, size: 20)),
      );
}

class _SkylinePainter extends CustomPainter {
  @override
  void paint(Canvas canvas, Size size) {
    final p = Paint()..color = Colors.white.withValues(alpha: 0.05);
    final base = size.height - 6;
    final heights = [38, 64, 50, 80, 58, 96, 46, 72, 60, 88, 42, 70, 54, 84, 48];
    final w = size.width / heights.length;
    for (var i = 0; i < heights.length; i++) {
      final h = heights[i].toDouble();
      canvas.drawRRect(
        RRect.fromRectAndCorners(
          Rect.fromLTWH(i * w + 2, base - h, w - 4, h),
          topLeft: const Radius.circular(2), topRight: const Radius.circular(2),
        ),
        p,
      );
    }
    // Kuwait towers — pole + spheres
    final tp = Paint()..color = Colors.white.withValues(alpha: 0.07);
    final tx = size.width * 0.52;
    canvas.drawRect(Rect.fromLTWH(tx, base - 130, 5, 130), tp);
    canvas.drawCircle(Offset(tx + 2.5, base - 128), 15, tp);
    canvas.drawCircle(Offset(tx + 2.5, base - 92), 9, tp);
    final tx2 = size.width * 0.44;
    canvas.drawRect(Rect.fromLTWH(tx2, base - 100, 4, 100), tp);
    canvas.drawCircle(Offset(tx2 + 2, base - 98), 10, tp);
  }

  @override
  bool shouldRepaint(covariant CustomPainter oldDelegate) => false;
}

// ============ Stats bar ============
class _StatsBar extends StatelessWidget {
  final String status, dept, empNo, joining;
  const _StatsBar({required this.status, required this.dept, required this.empNo, required this.joining});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(vertical: 9, horizontal: 6),
      decoration: BoxDecoration(
        color: Colors.white,
        borderRadius: BorderRadius.circular(16),
        boxShadow: [BoxShadow(color: Colors.black.withValues(alpha: 0.08), blurRadius: 16, offset: const Offset(0, 6))],
      ),
      child: IntrinsicHeight(
        child: Row(children: [
          _cell(Icons.check_circle, _ar(context, 'الحالة', 'Status'), status, _green),
          _divider(),
          _cell(Icons.account_tree, _ar(context, 'الإدارة', 'Dept'), dept, _primary),
          _divider(),
          _cell(Icons.badge, _ar(context, 'رقم الموظف', 'Emp #'), empNo, _primary),
          _divider(),
          _cell(Icons.calendar_month, _ar(context, 'تاريخ الانضمام', 'Joined'), joining, _primary),
        ]),
      ),
    );
  }

  Widget _divider() => const VerticalDivider(width: 1, indent: 4, endIndent: 4, color: Color(0xFFEFF2F5));

  Widget _cell(IconData ic, String label, String value, Color col) => Expanded(
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 4),
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            Icon(ic, color: col, size: 16),
            const SizedBox(height: 4),
            Text(label, style: const TextStyle(fontSize: 9.5, color: _muted), textAlign: TextAlign.center),
            const SizedBox(height: 1),
            Text(value, style: const TextStyle(fontSize: 11, fontWeight: FontWeight.bold, color: _ink), textAlign: TextAlign.center, maxLines: 1, overflow: TextOverflow.ellipsis),
          ]),
        ),
      );
}

class _SectionCard extends StatelessWidget {
  final IconData icon;
  final String title;
  final String? footer;
  final List<Widget> children;
  const _SectionCard({required this.icon, required this.title, this.footer, required this.children});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: Colors.white,
        borderRadius: BorderRadius.circular(18),
        boxShadow: [BoxShadow(color: Colors.black.withValues(alpha: 0.04), blurRadius: 12, offset: const Offset(0, 4))],
      ),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Row(mainAxisAlignment: MainAxisAlignment.end, children: [
          Flexible(child: Text(title, textAlign: TextAlign.end, style: const TextStyle(fontSize: 14, fontWeight: FontWeight.bold, color: _ink))),
          const SizedBox(width: 8),
          Container(width: 34, height: 34, decoration: BoxDecoration(color: const Color(0xFFEFF5FF), borderRadius: BorderRadius.circular(9)), child: Icon(icon, color: _primary, size: 18)),
        ]),
        Container(height: 2, width: 36, margin: const EdgeInsetsDirectional.only(top: 4, bottom: 6, end: 42), color: _primary),
        ...children,
        if (footer != null) ...[
          const Divider(height: 18),
          Center(child: Text('$footer  ⌄', style: const TextStyle(color: _primary, fontSize: 12.5, fontWeight: FontWeight.bold))),
        ],
      ]),
    );
  }
}

class _InfoLine extends StatelessWidget {
  final IconData icon;
  final String label;
  final String? value;
  final bool copy;
  const _InfoLine(this.icon, this.label, this.value, {this.copy = false});

  @override
  Widget build(BuildContext context) {
    final v = (value == null || value!.isEmpty) ? '—' : value!;
    return Container(
      margin: const EdgeInsets.only(bottom: 8),
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 8),
      decoration: BoxDecoration(color: const Color(0xFFF5F7FB), borderRadius: BorderRadius.circular(12)),
      child: Row(children: [
        Container(
          width: 34, height: 34,
          decoration: BoxDecoration(
            color: Colors.white, borderRadius: BorderRadius.circular(9),
            boxShadow: [BoxShadow(color: Colors.black.withValues(alpha: 0.04), blurRadius: 4)],
          ),
          child: Icon(icon, color: _primary, size: 16),
        ),
        const SizedBox(width: 9),
        Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(label, style: const TextStyle(fontSize: 10, color: _muted)),
          const SizedBox(height: 1),
          Text(v, style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w700, color: _ink), maxLines: 2, overflow: TextOverflow.ellipsis),
        ])),
        if (copy) InkWell(onTap: () => Clipboard.setData(ClipboardData(text: v)), child: const Icon(Icons.copy_rounded, size: 14, color: _muted)),
      ]),
    );
  }
}
