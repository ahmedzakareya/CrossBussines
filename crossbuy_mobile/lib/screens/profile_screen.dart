import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../models/employee.dart';
import '../models/profile_extras.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';

/// MY PROFILE (the owner's reference): the photo, the name and the post, then the facts as data lists — personal,
/// work — my documents and my latest activity. Read-only: a change to these facts goes through HR, so the screen shows
/// them and lets a phone number or e-mail be copied with a long press.
class ProfileScreen extends StatefulWidget {
  const ProfileScreen({super.key});
  @override
  State<ProfileScreen> createState() => _ProfileScreenState();
}

class _ProfileScreenState extends State<ProfileScreen> {
  late Future<(Employee, List<DocItem>, List<ActivityItem>)> _future = _load();

  Future<(Employee, List<DocItem>, List<ActivityItem>)> _load() async {
    final emp = await ApiService.instance.getProfile();
    final docs = await ApiService.instance.getDocuments().catchError((_) => <DocItem>[]);
    final act = await ApiService.instance.getActivity().catchError((_) => <ActivityItem>[]);
    return (emp, docs, act);
  }

  @override
  void initState() {
    super.initState();
    AppNav.dataChanged.addListener(_reload);
  }

  @override
  void dispose() {
    AppNav.dataChanged.removeListener(_reload);
    super.dispose();
  }

  void _reload() {
    if (mounted) setState(() => _future = _load());
  }

  @override
  Widget build(BuildContext context) {
    return ExecFrame(
      child: FutureBuilder<(Employee, List<DocItem>, List<ActivityItem>)>(
        future: _future,
        builder: (context, s) {
          final back = BackLink(L(context, 'حسابي', 'Account'));
          if (s.hasError) {
            return ExecBody(children: [back, EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر تحميل الملف', 'Could not load the profile'), text: L(context, 'تحقق من الاتصال ثم أعد المحاولة.', 'Check the connection and try again.'))]);
          }
          if (!s.hasData) return const Center(child: CircularProgressIndicator());
          final (e, docs, act) = s.data!;
          final ar = isAr(context);
          String pick(String? a, String? b) {
            final x = a?.trim() ?? '', y = b?.trim() ?? '';
            return ar ? (x.isNotEmpty ? x : y) : (y.isNotEmpty ? y : x);
          }
          final name = pick(e.fullName, e.fullNameEn);
          final job = pick(e.jobTitleAr, e.jobTitleEn);
          final branch = pick(e.branchAr, e.branchEn);
          String v(String? x) => (x ?? '').trim().isEmpty ? '—' : x!.trim();
          Widget copyable(String label, String? value) => GestureDetector(
                onLongPress: (value ?? '').trim().isEmpty
                    ? null
                    : () {
                        Clipboard.setData(ClipboardData(text: value!.trim()));
                        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(L(context, 'نُسخ', 'Copied'))));
                      },
                child: KV(label, v(value)),
              );
          return ExecBody(
            onRefresh: () async => _reload(),
            children: [
              back,
              Padding(
                padding: const EdgeInsets.only(top: 4, bottom: 22),
                child: Column(children: [
                  PersonAvatar(photo: e.profileImage, name: name, size: 68, ring: true),
                  const SizedBox(height: 14),
                  Text(name, textAlign: TextAlign.center, style: T.h1),
                  Text([job, branch].where((x) => x.isNotEmpty).join(' · '), textAlign: TextAlign.center, style: T.sub),
                  const SizedBox(height: 10),
                  e.isActive ? Tag.ok(L(context, 'على رأس العمل', 'Active')) : Tag.bad(L(context, 'غير نشط', 'Inactive')),
                ]),
              ),
              SectionHeading(L(context, 'العمل', 'Work')),
              DataList(children: [
                KV(L(context, 'الرقم الوظيفي', 'Employee no.'), 'EMP-${e.id.toString().padLeft(4, '0')}'),
                KV(L(context, 'المسمى الوظيفي', 'Job title'), v(job)),
                KV(L(context, 'الشركة', 'Company'), v(pick(e.companyAr, e.companyEn))),
                KV(L(context, 'الإدارة', 'Department'), v(pick(e.departmentAr, e.departmentEn))),
                KV(L(context, 'الفرع', 'Branch'), v(branch)),
                KV(L(context, 'نوع التوظيف', 'Employment'), v(e.employmentType)),
                KV(L(context, 'تاريخ الالتحاق', 'Joined'), e.dateOfJoining == null ? '—' : '${dayMonth(context, e.dateOfJoining!)} ${e.dateOfJoining!.year}'),
              ]),
              SectionHeading(L(context, 'البيانات الشخصية', 'Personal')),
              DataList(children: [
                copyable(L(context, 'البريد', 'Email'), e.email),
                copyable(L(context, 'الهاتف', 'Phone'), e.phoneNumber),
                KV(L(context, 'العنوان', 'Address'), v(e.address)),
                KV(L(context, 'الجنس', 'Gender'), v(e.gender)),
                KV(L(context, 'الحالة الاجتماعية', 'Marital status'), v(e.maritalStatus)),
              ]),
              const SizedBox(height: 12),
              OutlinedButton.icon(
                onPressed: () async {
                  final sent = await Navigator.of(context).push<bool>(MaterialPageRoute(builder: (_) => ProfileChangeScreen(current: e)));
                  if (sent == true) _reload();
                },
                icon: const Icon(Lu.userPen, size: 16),
                label: Text(L(context, 'طلب تعديل بياناتي', 'Ask to change my details')),
              ),
              SectionHeading(L(context, 'مستنداتي', 'My documents'), count: docs.length),
              if (docs.isEmpty)
                EmptyNote(icon: Lu.folderOpen, title: L(context, 'لا مستندات', 'No documents'), text: L(context, 'ترفع الموارد البشرية مستنداتك (الهوية، العقد…) فتظهر هنا.', 'HR uploads your documents (ID, contract…) and they show here.'))
              else
                DataList(children: [
                  for (final d in docs)
                    ItemRow(
                      leading: IconBox(icon: (d.path ?? d.name ?? '').toLowerCase().contains('pdf') ? Lu.fileText : Lu.file),
                      title: v(d.name),
                      subtitle: d.uploadedAt == null ? null : '${L(context, 'رُفع في', 'Uploaded')} ${dayMonth(context, d.uploadedAt!.toLocal())} ${d.uploadedAt!.year}',
                    ),
                ]),
              if (act.isNotEmpty) ...[
                SectionHeading(L(context, 'آخر نشاطاتي', 'My latest activity')),
                DataList(children: [
                  for (final a in act.take(5))
                    ItemRow(
                      leading: IconBox(icon: a.type == 'leave_approved' ? Lu.circleCheck : a.type == 'leave_rejected' ? Lu.circleX : Lu.activity, fg: a.type == 'leave_rejected' ? Brand.badC : Brand.support),
                      title: pick(a.titleAr, a.titleEn),
                      subtitle: [pick(a.descAr, a.descEn), if (a.at != null) dayMonth(context, a.at!.toLocal())].where((x) => x.isNotEmpty).join(' · '),
                    ),
                ]),
              ],
            ],
          );
        },
      ),
    );
  }
}

/// ASK TO CHANGE MY DETAILS (HR9): the self-service fields only — phone, address, the emergency contact, the bank and
/// the IBAN. Only what is filled is sent; nothing changes until the company's approval path approves it, and then the
/// change lands with the approval.
class ProfileChangeScreen extends StatefulWidget {
  final Employee current;
  const ProfileChangeScreen({super.key, required this.current});
  @override
  State<ProfileChangeScreen> createState() => _ProfileChangeScreenState();
}

class _ProfileChangeScreenState extends State<ProfileChangeScreen> {
  static const _keys = ['PhoneNumber', 'Address', 'EmergencyContactName', 'EmergencyContactPhone', 'EmergencyContactRelation', 'BankName', 'Iban'];
  final _c = {for (final k in _keys) k: TextEditingController()};
  final _reason = TextEditingController();
  bool _busy = false;

  @override
  void dispose() {
    for (final c in _c.values) {
      c.dispose();
    }
    _reason.dispose();
    super.dispose();
  }

  String _label(BuildContext context, String k) => switch (k) {
        'PhoneNumber' => L(context, 'الهاتف', 'Phone'),
        'Address' => L(context, 'العنوان', 'Address'),
        'EmergencyContactName' => L(context, 'جهة الطوارئ', 'Emergency contact'),
        'EmergencyContactPhone' => L(context, 'هاتف الطوارئ', 'Emergency phone'),
        'EmergencyContactRelation' => L(context, 'صلة القرابة', 'Relation'),
        'BankName' => L(context, 'البنك', 'Bank'),
        _ => 'IBAN',
      };

  String? _now(String k) => switch (k) { 'PhoneNumber' => widget.current.phoneNumber, 'Address' => widget.current.address, _ => null };

  Future<void> _send() async {
    final changes = <String, String>{
      for (final k in _keys)
        if (_c[k]!.text.trim().isNotEmpty) k: _c[k]!.text.trim(),
    };
    if (changes.isEmpty) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(L(context, 'أدخل قيمة جديدة واحدة على الأقل', 'Enter at least one new value'))));
      return;
    }
    setState(() => _busy = true);
    final (ok, err) = await ApiService.instance.createRequest({'requestType': 'ProfileChange', 'changes': changes, 'reason': _reason.text.trim()});
    if (!mounted) return;
    setState(() => _busy = false);
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(ok ? L(context, 'أُرسل طلب التعديل', 'Change request sent') : (err ?? L(context, 'تعذّر إرسال الطلب', 'The request could not be sent')))));
    if (ok) {
      AppNav.notifyDataChanged();
      Navigator.of(context).pop(true);
    }
  }

  @override
  Widget build(BuildContext context) {
    Widget field(String k, {TextInputType? type, bool ltr = false}) => Padding(
          padding: const EdgeInsets.only(bottom: 9),
          child: TextField(
            controller: _c[k],
            keyboardType: type,
            textDirection: ltr ? TextDirection.ltr : null,
            decoration: InputDecoration(labelText: _label(context, k), hintText: (_now(k) ?? '').trim().isEmpty ? null : '${L(context, 'الحالي', 'Now')}: ${_now(k)!.trim()}'),
          ),
        );
    return ExecFrame(
      child: ExecBody(children: [
        BackLink(L(context, 'ملفي الشخصي', 'My profile')),
        PageHead(L(context, 'تعديل بياناتي', 'Change my details'), eyebrow: L(context, 'يُرسل عبر مسار الاعتماد في شركتك', "Sent through your company's approval path")),
        SoftNote(L(context, 'املأ ما تغيّر فقط. لا يتغير شيء قبل الاعتماد، وعند الاعتماد يُطبَّق التعديل مباشرة.', 'Fill in only what changed. Nothing changes before approval; on approval the change takes effect at once.')),
        const SizedBox(height: 12),
        SectionHeading(L(context, 'التواصل', 'Contact')),
        field('PhoneNumber', type: TextInputType.phone, ltr: true),
        field('Address'),
        SectionHeading(L(context, 'جهة الطوارئ', 'Emergency contact')),
        field('EmergencyContactName'),
        field('EmergencyContactPhone', type: TextInputType.phone, ltr: true),
        field('EmergencyContactRelation'),
        SectionHeading(L(context, 'الحساب البنكي', 'Bank account')),
        field('BankName'),
        field('Iban', ltr: true),
        TextField(controller: _reason, maxLines: 2, decoration: InputDecoration(labelText: L(context, 'ملاحظة', 'Note'))),
        const SizedBox(height: 16),
        FilledButton(onPressed: _busy ? null : _send, child: Text(L(context, 'إرسال الطلب', 'Send request'))),
      ]),
    );
  }
}
