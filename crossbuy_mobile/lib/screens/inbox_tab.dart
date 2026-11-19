import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../models/ess.dart';
import '../providers/notification_provider.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';
import 'home_tab.dart' show approvalCard, approvalTitle, approvalSub;

/// APPROVALS (the reference's "الموافقات" tab): the decision centre — every item whose current approval step is mine,
/// leave and employee requests alike, as request cards; a card opens its detail with the approval timeline and the
/// decision. The bell's notifications are their own page (NotificationsPage).
class InboxTab extends StatefulWidget {
  const InboxTab({super.key});
  @override
  State<InboxTab> createState() => _InboxTabState();
}

class _InboxTabState extends State<InboxTab> {
  late Future<(String, List<ApprovalItem>)> _f = ApiService.instance.getApprovals();

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
    if (mounted) setState(() => _f = ApiService.instance.getApprovals());
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<(String, List<ApprovalItem>)>(
      future: _f,
      builder: (context, s) {
        if (s.hasError) {
          return ExecBody(children: [
            PageHead(L(context, 'الموافقات', 'Approvals'), eyebrow: L(context, 'مركز القرارات', 'Decision centre')),
            EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر التحميل', 'Could not load'), text: L(context, 'تحقق من الاتصال ثم اسحب للتحديث.', 'Check the connection, then pull to refresh.')),
          ]);
        }
        if (!s.hasData) return const Center(child: CircularProgressIndicator());
        final (cur, list) = s.data!;
        return ExecBody(
          onRefresh: () async => _reload(),
          children: [
            PageHead(L(context, 'الموافقات', 'Approvals'), eyebrow: L(context, 'مركز القرارات', 'Decision centre'),
                sub: list.isEmpty ? L(context, 'لا شيء ينتظر مراجعتك', 'Nothing is waiting for you') : L(context, '${list.length} طلبات تنتظر مراجعتك', '${list.length} requests wait for your review')),
            if (list.isEmpty) SoftNote(L(context, 'تمت مراجعة جميع الطلبات', 'Everything has been reviewed')),
            for (final a in list)
              approvalCard(context, a, cur, () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => ApprovalDetailPage(item: a, currency: cur)))),
          ],
        );
      },
    );
  }
}

/// One request in full (the reference's detail page): its number and type, the amount or days with the status,
/// the facts, the approval timeline with the current step, and the decision.
class ApprovalDetailPage extends StatefulWidget {
  final ApprovalItem item;
  final String currency;
  const ApprovalDetailPage({super.key, required this.item, required this.currency});
  @override
  State<ApprovalDetailPage> createState() => _ApprovalDetailPageState();
}

class _ApprovalDetailPageState extends State<ApprovalDetailPage> {
  bool _busy = false;
  String? _done;   // the decision taken here, shown in place of the buttons

  bool _approved = false;

  Future<void> _decide(bool approve) async {
    setState(() => _busy = true);
    final (ok, err) = await ApiService.instance.decide(widget.item, approve);
    if (!mounted) return;
    setState(() {
      _busy = false;
      _approved = ok && approve;
      if (ok) _done = approve ? L(context, 'تم الاعتماد', 'Approved') : L(context, 'تم الرفض', 'Rejected');
    });
    if (!ok) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(err ?? L(context, 'تعذّر تنفيذ القرار', 'The decision could not be saved'))));
    if (ok) AppNav.notifyDataChanged();
  }

  /// "Return for review": ask what should be reviewed (required), then send it back to the requester.
  Future<void> _return() async {
    final ctl = TextEditingController();
    final note = await showModalBottomSheet<String>(
      context: context, isScrollControlled: true, backgroundColor: Brand.page,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(22))),
      builder: (c) => Padding(
        padding: EdgeInsets.fromLTRB(18, 18, 18, 18 + MediaQuery.of(c).viewInsets.bottom),
        child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          Text(L(context, 'إعادة للمراجعة', 'Return for review'), style: T.h2),
          const SizedBox(height: 4),
          Text(L(context, 'يعود الطلب إلى صاحبه ليعدّله ويرسله من جديد.', 'The request goes back to its requester to amend and send again.'), style: T.small),
          const SizedBox(height: 14),
          FieldBox(
            label: L(context, 'ما يجب مراجعته', 'What to review'),
            child: TextField(controller: ctl, autofocus: true, maxLines: 3, minLines: 2, decoration: const InputDecoration.collapsed(hintText: '')),
          ),
          const SizedBox(height: 6),
          FilledButton(
            onPressed: () { final t = ctl.text.trim(); if (t.isNotEmpty) Navigator.of(c).pop(t); },
            child: Text(L(context, 'إعادة الطلب', 'Return the request')),
          ),
        ]),
      ),
    );
    if (note == null || !mounted) return;
    setState(() => _busy = true);
    final (ok, err) = await ApiService.instance.returnForReview(widget.item, note);
    if (!mounted) return;
    setState(() {
      _busy = false;
      if (ok) _done = L(context, 'أُعيد للمراجعة', 'Returned for review');
    });
    if (!ok) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(err ?? L(context, 'تعذّر تنفيذ القرار', 'The decision could not be saved'))));
  }

  /// Arabic counts its days: يوم، يومان، 3–10 أيام، 11 فأكثر يوماً
  String _days(BuildContext context, int n) => isAr(context)
      ? (n == 1 ? 'يوم واحد' : n == 2 ? 'يومان' : (n % 100 >= 3 && n % 100 <= 10) ? '$n أيام' : '$n يوماً')
      : '$n ${n == 1 ? 'day' : 'days'}';

  String _range(BuildContext context, DateTime f, DateTime? t) {
    if (t == null || t == f) return '${dayMonth(context, f)} ${f.year}';
    if (f.year == t.year && f.month == t.month) return '${f.day} – ${dayMonth(context, t)} ${t.year}';
    return '${dayMonth(context, f)} – ${dayMonth(context, t)} ${t.year}';
  }

  @override
  Widget build(BuildContext context) {
    final a = widget.item;
    final ar = isAr(context);
    final isLeave = a.kind == 'leave';
    final value = isLeave ? '${a.days ?? 0}' : (a.amount == null ? '—' : money(a.amount!, widget.currency));
    final unit = isLeave ? L(context, 'أيام', 'days') : curLabel(widget.currency, ar);
    final code = '${isLeave ? 'LV' : 'RQ'}-${a.id.toString().padLeft(4, '0')}';
    Widget step(String label, {bool current = false}) => Column(mainAxisSize: MainAxisSize.min, children: [
          Container(
            width: 7, height: 7,
            decoration: BoxDecoration(color: current ? Brand.gold : Brand.support, shape: BoxShape.circle,
                boxShadow: current ? const [BoxShadow(color: Color(0x1AD4AF37), spreadRadius: 4)] : null),
          ),
          const SizedBox(height: 8),
          Text(label, style: const TextStyle(fontSize: 9.5, color: Brand.mutedC)),
        ]);
    Widget hr() => Expanded(child: Container(height: 1, color: Brand.lineC, margin: const EdgeInsets.only(bottom: 17)));
    return ExecFrame(
      onBell: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => const NotificationsPage())),
      child: ExecBody(children: [
        BackLink(L(context, 'كل الموافقات', 'All approvals')),
        Text([code, if (a.dept(ar).isNotEmpty) a.dept(ar)].join(' / '), style: T.eyebrow),
        const SizedBox(height: 3),
        Text(isLeave ? L(context, 'طلب إجازة', 'Leave request') : approvalTitle(context, a), style: T.h1),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 22),
          child: Column(children: [
            _done == null ? Tag(L(context, 'بانتظار اعتمادك', 'Awaiting your approval')) : (_approved ? Tag.ok(_done!) : Tag.info(_done!)),
            const SizedBox(height: 12),
            Row(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.baseline, textBaseline: TextBaseline.alphabetic, children: [
              Text(value, textDirection: TextDirection.ltr, style: const TextStyle(fontSize: 35, fontWeight: FontWeight.w700, letterSpacing: -1, color: Brand.inkC)),
              const SizedBox(width: 7),
              Text(unit, style: const TextStyle(fontSize: 12, color: Brand.mutedC)),
            ]),
            const SizedBox(height: 8),
            Text('${approvalTitle(context, a)} · ${a.requester(ar)}', style: const TextStyle(fontSize: 12, color: Brand.mutedC)),
          ]),
        ),
        // the reference's facts: for a leave the dates, the balance now and after, the substitute and the team's overlap
        DataList(children: isLeave
            ? [
                if (a.from != null) KV(L(context, 'التاريخ', 'Dates'), _range(context, a.from!, a.to)),
                if (a.remaining != null) KV(L(context, 'الرصيد الآن', 'Balance now'), _days(context, a.remaining!)),
                if (a.after != null) KV(L(context, 'الرصيد بعد الإجازة', 'Balance after the leave'), _days(context, a.after!)),
                KV(L(context, 'البديل أثناء الغياب', 'Cover during the absence'), ((ar ? a.substituteAr : a.substituteEn) ?? '').isEmpty ? L(context, 'لا يوجد', 'None') : (ar ? a.substituteAr! : a.substituteEn!)),
                KV(L(context, 'تعارض مع الفريق', 'Overlap with the team'), a.away == 0
                    ? L(context, 'لا يوجد', 'None')
                    : [...(ar ? a.awayAr : a.awayEn), if (a.away > 3) L(context, '+${a.away - 3} آخرون', '+${a.away - 3} more')].join(L(context, '، ', ', '))),
              ]
            : [
                KV(L(context, 'مقدم الطلب', 'Requested by'), a.requester(ar)),
                if (a.from != null) KV(L(context, 'التاريخ', 'Date'), approvalSub(context, a, widget.currency)),
                if (a.createdAt != null) KV(L(context, 'قُدّم في', 'Submitted on'), ymd(a.createdAt!)),
              ]),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 22, horizontal: 6),
          child: Row(children: [
            step(L(context, 'تقديم الطلب', 'Submitted')),
            hr(),
            step([L(context, 'أنت', 'You'), if (((ar ? a.myTitleAr : a.myTitleEn) ?? '').isNotEmpty) (ar ? a.myTitleAr! : a.myTitleEn!)].join(' · '), current: _done == null),
            hr(),
            // the next step of the stored chain, by title (or name); the final action when mine is the last
            step(((ar ? a.nextTitleAr : a.nextTitleEn) ?? (ar ? a.nextAr : a.nextEn)) ?? L(context, 'الإجراء النهائي', 'Final action'), current: _done != null),
          ]),
        ),
        if (_done != null)
          SoftNote('${_done!} · ${L(context, 'أُبلغ صاحب الطلب', 'the requester is notified')}')
        else
          Row(children: [
            Expanded(flex: 3, child: FilledButton(onPressed: _busy ? null : () => _decide(true), child: Text(L(context, 'اعتماد الطلب', 'Approve')))),
            const SizedBox(width: 9),
            Expanded(flex: 2, child: OutlinedButton(onPressed: _busy ? null : _return, child: Text(L(context, 'إعادة للمراجعة', 'Return for review')))),
          ]),
      ]),
    );
  }
}

/// The bell: the latest updates, newest first; tapping one routes to where it belongs.
class NotificationsPage extends StatelessWidget {
  const NotificationsPage({super.key});
  @override
  Widget build(BuildContext context) {
    final n = context.watch<NotificationProvider>();
    final ar = isAr(context);
    return ExecFrame(
      child: ExecBody(
        onRefresh: () => n.load(),
        children: [
          BackLink(L(context, 'العودة', 'Back')),
          PageHead(L(context, 'الإشعارات', 'Notifications'), eyebrow: L(context, 'آخر التحديثات', 'Latest updates'),
              sub: n.unread > 0 ? L(context, '${n.unread} غير مقروءة', '${n.unread} unread') : null),
          if (n.unread > 0)
            Align(
              alignment: AlignmentDirectional.centerEnd,
              child: TextButton(onPressed: () => ApiService.instance.markAllNotificationsRead().then((_) => n.load()), child: Text(L(context, 'تعليم الكل كمقروء', 'Mark all read'))),
            ),
          if (n.items.isEmpty)
            EmptyNote(icon: Lu.bell, title: L(context, 'لا إشعارات', 'No notifications'), text: L(context, 'كل شيء هادئ.', 'All quiet.'))
          else
            DataList(children: [
              for (final i in n.items.take(40))
                ItemRow(
                  leading: IconBox(icon: i.isRead ? Lu.bell : Lu.bellDot, fg: i.isRead ? Brand.support : Brand.blue),
                  title: ((ar ? i.titleAr : (i.titleEn ?? i.titleAr)) ?? '').trim(),
                  subtitle: ((ar ? i.bodyAr : (i.bodyEn ?? i.bodyAr)) ?? '').trim(),
                  end: i.createdAt == null ? null : Text(ymd(i.createdAt!.toLocal()), style: T.small),
                  onTap: () {
                    Navigator.of(context).maybePop();
                    AppNav.routeForNotification(i.type);
                  },
                ),
            ]),
        ],
      ),
    );
  }
}

/// Title and status of one of MY requests (letters, permissions, advances), shared with the requests screen.
String requestTitle(BuildContext context, MyRequest r) {
  switch (r.type) {
    case 'Advance':
      return L(context, 'سلفة راتب', 'Salary advance');
    case 'Permission':
      return L(context, 'إذن', 'Permission');
    case 'ProfileChange':
      return L(context, 'تعديل بيانات', 'Profile change');
    default:
      return '${L(context, 'خطاب', 'Letter')}${r.addressee != null ? ' — ${r.addressee}' : ''}';
  }
}

Widget requestPill(BuildContext context, int status) => status == 1
    ? Tag.ok(L(context, 'معتمد', 'Approved'))
    : status == 2
        ? Tag.bad(L(context, 'مرفوض', 'Rejected'))
        : status == 3
            ? Tag.info(L(context, 'أُعيد للمراجعة', 'Returned for review'))
            : Tag(L(context, 'قيد الاعتماد', 'Pending'));
