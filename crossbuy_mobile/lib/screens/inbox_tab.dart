import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../models/ess.dart';
import '../providers/notification_provider.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';
import 'home_tab.dart' show approvalTitle, approvalSub;

/// INBOX — one place for everything that needs me: approvals (leave AND employee requests whose current step is mine),
/// updates (notifications) and what I sent. Every swipe-worthy action also has a button (research: never swipe-only).
class InboxTab extends StatefulWidget {
  const InboxTab({super.key});
  @override
  State<InboxTab> createState() => _InboxTabState();
}

class _InboxTabState extends State<InboxTab> {
  late Future<(String, List<ApprovalItem>)> _approvals;
  late Future<List<MyRequest>> _sent;

  @override
  void initState() {
    super.initState();
    _load();
    AppNav.dataChanged.addListener(_reload);
    AppNav.inboxFilter.addListener(_refresh);
  }

  @override
  void dispose() {
    AppNav.dataChanged.removeListener(_reload);
    AppNav.inboxFilter.removeListener(_refresh);
    super.dispose();
  }

  void _load() {
    _approvals = ApiService.instance.getApprovals().catchError((_) => ('', <ApprovalItem>[]));
    _sent = ApiService.instance.getMyRequests().catchError((_) => <MyRequest>[]);
  }

  void _reload() {
    if (mounted) setState(_load);
  }

  void _refresh() {
    if (mounted) setState(() {});
  }

  Future<void> _decide(ApprovalItem a, bool approve) async {
    final (ok, err) = await ApiService.instance.decide(a, approve);
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(
        content: Text(ok ? (approve ? L(context, 'تم الاعتماد', 'Approved') : L(context, 'رُفض الطلب', 'Rejected')) : (err ?? L(context, 'تعذّر تنفيذ القرار', 'The decision could not be saved')))));
    if (ok) _reload();
  }

  @override
  Widget build(BuildContext context) {
    final f = AppNav.inboxFilter.value;
    final filters = [('الكل', 'All'), ('اعتمادات', 'Approvals'), ('تحديثات', 'Updates'), ('طلباتي المرسلة', 'Sent')];
    return Scaffold(
      appBar: AppBar(title: Text(L(context, 'الوارد', 'Inbox')), shape: const Border(bottom: BorderSide(color: Brand.line))),
      body: Column(children: [
        SizedBox(
          height: 56,
          child: ListView(scrollDirection: Axis.horizontal, padding: const EdgeInsets.fromLTRB(16, 12, 16, 4), children: [
            for (var i = 0; i < filters.length; i++)
              Padding(
                padding: const EdgeInsetsDirectional.only(end: 6),
                child: InkWell(
                  borderRadius: BorderRadius.circular(999),
                  onTap: () => AppNav.inboxFilter.value = i,
                  child: Container(
                    padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
                    decoration: BoxDecoration(
                      color: f == i ? Brand.deep : Colors.white,
                      borderRadius: BorderRadius.circular(999),
                      border: Border.all(color: f == i ? Brand.deep : Brand.line),
                    ),
                    child: Text(L(context, filters[i].$1, filters[i].$2), style: TextStyle(color: f == i ? Colors.white : Brand.ink2, fontWeight: FontWeight.w600, fontSize: 13)),
                  ),
                ),
              ),
          ]),
        ),
        Expanded(
          child: RefreshIndicator(
            onRefresh: () async => _reload(),
            child: ListView(padding: const EdgeInsets.all(16), children: [
              if (f == 0 || f == 1) _approvalsBlock(context, showEmpty: f == 1),
              if (f == 0 || f == 2) _updatesBlock(context),
              if (f == 3) _sentBlock(context),
            ]),
          ),
        ),
      ]),
    );
  }

  Widget _approvalsBlock(BuildContext context, {required bool showEmpty}) => FutureBuilder<(String, List<ApprovalItem>)>(
        future: _approvals,
        builder: (context, s) {
          if (!s.hasData) return const Padding(padding: EdgeInsets.all(24), child: Center(child: CircularProgressIndicator()));
          final (cur, list) = s.data!;
          if (list.isEmpty) {
            return showEmpty
                ? SectionCard(child: EmptyNote(title: L(context, 'لا شيء ينتظرك', 'All caught up'), text: L(context, 'سنبلغك حين يصل طلب جديد.', "We'll tell you when something new arrives.")))
                : const SizedBox.shrink();
          }
          final ar = isAr(context);
          return SectionCard(
            title: '${L(context, 'اعتمادات', 'Approvals')} ${list.length}',
            child: Column(children: [
              for (final a in list)
                Padding(
                  padding: const EdgeInsets.only(bottom: 8),
                  child: Column(children: [
                    RowItem(
                      leading: PersonAvatar(name: a.requester(ar), size: 36),
                      title: approvalTitle(context, a),
                      subtitle: '${a.requester(ar)} · ${approvalSub(context, a, cur)}',
                    ),
                    Row(children: [
                      Expanded(child: OutlinedButton(onPressed: () => _decide(a, false), style: OutlinedButton.styleFrom(foregroundColor: Brand.bad, side: const BorderSide(color: Color(0xFFFECACA))), child: Text(L(context, 'رفض', 'Reject')))),
                      const SizedBox(width: 8),
                      Expanded(child: FilledButton(onPressed: () => _decide(a, true), style: FilledButton.styleFrom(backgroundColor: Brand.ok, minimumSize: const Size(0, 40)), child: Text(L(context, 'اعتماد', 'Approve')))),
                    ]),
                    const Divider(height: 18, color: Color(0xFFF1F5F9)),
                  ]),
                ),
            ]),
          );
        },
      );

  Widget _updatesBlock(BuildContext context) {
    final n = context.watch<NotificationProvider>();
    final ar = isAr(context);
    if (n.items.isEmpty) return SectionCard(child: EmptyNote(icon: Icons.notifications_none, title: L(context, 'لا تحديثات', 'No updates'), text: L(context, 'كل شيء منجز.', 'All caught up.')));
    return SectionCard(
      title: L(context, 'التحديثات', 'Updates'),
      action: TextButton(onPressed: () => ApiService.instance.markAllNotificationsRead().then((_) => n.load()), child: Text(L(context, 'تعليم الكل كمقروء', 'Mark all read'))),
      child: Column(children: [
        for (final i in n.items.take(30))
          RowItem(
            leading: const IconTile(Icons.notifications_active_outlined),
            title: (ar ? i.titleAr : (i.titleEn ?? i.titleAr)) ?? '',
            subtitle: (ar ? i.bodyAr : (i.bodyEn ?? i.bodyAr)) ?? '',
            onTap: () => AppNav.routeForNotification(i.type),
          ),
      ]),
    );
  }

  Widget _sentBlock(BuildContext context) => FutureBuilder<List<MyRequest>>(
        future: _sent,
        builder: (context, s) {
          if (!s.hasData) return const Padding(padding: EdgeInsets.all(24), child: Center(child: CircularProgressIndicator()));
          if (s.data!.isEmpty) return SectionCard(child: EmptyNote(icon: Icons.send_outlined, title: L(context, 'لا طلبات', 'No requests'), text: L(context, 'طلباتك المرسلة تظهر هنا.', 'Requests you send appear here.')));
          return SectionCard(
            child: Column(children: [
              for (final r in s.data!)
                RowItem(
                  leading: IconTile(Icons.description_outlined, fg: r.status == 1 ? Brand.ok : Brand.brand, bg: r.status == 1 ? Brand.okTint : Brand.tint2),
                  title: requestTitle(context, r),
                  subtitle: r.createdAt != null ? ymd(r.createdAt!) : null,
                  trailing: requestPill(context, r.status),
                ),
            ]),
          );
        },
      );
}

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
    ? Pill.ok(L(context, 'معتمد', 'Approved'))
    : status == 2
        ? Pill.bad(L(context, 'مرفوض', 'Rejected'))
        : Pill(L(context, 'قيد الاعتماد', 'Pending'));
