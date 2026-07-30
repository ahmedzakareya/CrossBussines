import 'package:flutter/material.dart';
import 'package:intl/intl.dart' hide TextDirection;
import 'package:provider/provider.dart';

import '../l10n/app_localizations.dart';
import '../models/leave.dart';
import '../providers/notification_provider.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../widgets/app_ui.dart';

class LeaveScreen extends StatefulWidget {
  const LeaveScreen({super.key});
  @override
  State<LeaveScreen> createState() => _LeaveScreenState();
}

class _LeaveScreenState extends State<LeaveScreen> with SingleTickerProviderStateMixin {
  static const _primary = kPrimary;
  static const _muted = kMuted;
  static const _green = kGreen;
  static const _red = Color(0xFFD9214E);
  static const _amber = Color(0xFFF6C000);

  late Future<List<LeaveRequest>> _mine;
  late Future<List<LeaveRequest>> _pending;
  late final TabController _tab;
  NotificationProvider? _notif;
  int _lastNotifLen = 0;

  @override
  void initState() {
    super.initState();
    _reloadMine();
    _reloadPending();
    _tab = TabController(length: 2, vsync: this, initialIndex: AppNav.leaveSubTab.value);
    AppNav.leaveSubTab.addListener(_syncTab);
    AppNav.tabIndex.addListener(_onTabChanged);
    AppNav.dataChanged.addListener(_onDataChanged);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _notif = context.read<NotificationProvider>();
      _lastNotifLen = _notif!.items.length;
      _notif!.addListener(_onNotif);
    });
  }

  // a new incoming notification likely changed leave data → refresh both lists
  void _onNotif() {
    final len = _notif?.items.length ?? 0;
    if (len > _lastNotifLen && mounted) {
      setState(() {
        _reloadMine();
        _reloadPending();
      });
    }
    _lastNotifLen = len;
  }

  void _syncTab() {
    if (mounted && _tab.index != AppNav.leaveSubTab.value) {
      _tab.animateTo(AppNav.leaveSubTab.value);
    }
  }

  // refresh both lists every time the Leaves tab (index 3) becomes active,
  // so a request created elsewhere (or just now) shows without waiting for a poll
  void _onTabChanged() {
    if (AppNav.tabIndex.value == 3 && mounted) {
      setState(() {
        _reloadMine();
        _reloadPending();
      });
    }
  }

  // any create/approve/reject anywhere → refresh both lists immediately
  void _onDataChanged() {
    if (mounted) {
      setState(() {
        _reloadMine();
        _reloadPending();
      });
    }
  }

  @override
  void dispose() {
    AppNav.leaveSubTab.removeListener(_syncTab);
    AppNav.tabIndex.removeListener(_onTabChanged);
    AppNav.dataChanged.removeListener(_onDataChanged);
    _notif?.removeListener(_onNotif);
    _tab.dispose();
    super.dispose();
  }

  void _reloadMine() => _mine = ApiService.instance.getMyLeaves();
  void _reloadPending() => _pending = ApiService.instance.getPendingApprovals();

  String _nm(bool ar, String? a, String? b) =>
      (ar ? a : b)?.trim().isNotEmpty == true ? (ar ? a : b)! : (b ?? a ?? '-');

  Color _statusColor(int s) => s == 1 ? _green : (s == 2 ? _red : _amber);

  @override
  Widget build(BuildContext context) {
    final t = AppLocalizations.of(context);
    return Column(
      children: [
        AppHeader(title: t.t('leaves')),
        Material(
          color: Colors.white,
          child: TabBar(
            controller: _tab,
            labelColor: _primary,
            unselectedLabelColor: _muted,
            indicatorColor: _primary,
            labelStyle: const TextStyle(fontWeight: FontWeight.bold, fontSize: 14),
            tabs: [
              Tab(text: t.t('myRequests')),
              Tab(text: t.t('forApproval')),
            ],
          ),
        ),
        Expanded(
          child: Container(
            color: kBg,
            child: TabBarView(
              controller: _tab,
              children: [
                _myTab(context, t),
                _approvalTab(context, t),
              ],
            ),
          ),
        ),
      ],
    );
  }

  // ---------------- My requests tab ----------------
  Widget _myTab(BuildContext context, AppLocalizations t) {
    return Stack(
      children: [
        RefreshIndicator(
          onRefresh: () async => setState(_reloadMine),
          child: FutureBuilder<List<LeaveRequest>>(
            future: _mine,
            builder: (context, snap) {
              if (snap.connectionState == ConnectionState.waiting) {
                return const Center(child: CircularProgressIndicator());
              }
              final list = snap.data ?? [];
              if (list.isEmpty) {
                return _emptyList(t.t('noRequests'), Icons.event_busy);
              }
              return ListView.separated(
                padding: const EdgeInsets.fromLTRB(16, 16, 16, 90),
                itemCount: list.length,
                separatorBuilder: (_, __) => const SizedBox(height: 10),
                itemBuilder: (_, i) => _requestCard(context, t, list[i], showActions: false),
              );
            },
          ),
        ),
        PositionedDirectional(
          end: 16,
          bottom: 16,
          child: DecoratedBox(
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(14),
              boxShadow: [
                BoxShadow(color: _primary.withValues(alpha: 0.35), blurRadius: 14, offset: const Offset(0, 6)),
              ],
            ),
            child: FilledButton.icon(
              onPressed: () => _openNewRequest(context, t),
              icon: const Icon(Icons.add, color: Colors.white),
              label: Text(t.t('newLeave'),
                  style: const TextStyle(color: Colors.white, fontWeight: FontWeight.bold, fontSize: 15)),
              style: FilledButton.styleFrom(
                backgroundColor: _primary,
                padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 16),
                shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14)),
              ),
            ),
          ),
        ),
      ],
    );
  }

  // ---------------- Approvals tab ----------------
  Widget _approvalTab(BuildContext context, AppLocalizations t) {
    return RefreshIndicator(
      onRefresh: () async => setState(_reloadPending),
      child: FutureBuilder<List<LeaveRequest>>(
        future: _pending,
        builder: (context, snap) {
          if (snap.connectionState == ConnectionState.waiting) {
            return const Center(child: CircularProgressIndicator());
          }
          final list = snap.data ?? [];
          if (list.isEmpty) {
            return _emptyList(t.t('noApprovals'), Icons.inbox);
          }
          return ListView.separated(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 24),
            itemCount: list.length,
            separatorBuilder: (_, __) => const SizedBox(height: 10),
            itemBuilder: (_, i) => _requestCard(context, t, list[i], showActions: true),
          );
        },
      ),
    );
  }

  Widget _emptyList(String msg, IconData icon) => ListView(
        children: [
          const SizedBox(height: 120),
          Icon(icon, size: 56, color: const Color(0xFFB5B9C5)),
          const SizedBox(height: 12),
          Center(child: Text(msg, style: const TextStyle(color: _muted, fontSize: 14))),
        ],
      );

  Widget _requestCard(BuildContext context, AppLocalizations t, LeaveRequest r,
      {required bool showActions}) {
    final ar = t.isAr;
    final df = DateFormat('yyyy/MM/dd');
    final typeName = _nm(ar, r.leaveTypeAr, r.leaveTypeEn);
    final statusName = _nm(ar, r.statusAr, r.statusEn);
    final dates =
        '${r.startDate != null ? df.format(r.startDate!) : '-'}  →  ${r.endDate != null ? df.format(r.endDate!) : '-'}';

    return SectionCard(
      icon: Icons.beach_access,
      title: typeName,
      trailing: _statusPill(statusName, r.status),
      children: [
        if (showActions && (r.employeeNameAr != null || r.employeeNameEn != null))
          InfoRow(Icons.person_outline, t.t('requestedBy'), _nm(ar, r.employeeNameAr, r.employeeNameEn)),
        InfoRow(Icons.date_range, ar ? 'الفترة' : 'Period', dates),
        InfoRow(Icons.timelapse, ar ? 'المدة' : 'Duration', '${r.days} ${t.t('days')}', iconColor: _primary),
        if (r.reason != null && r.reason!.trim().isNotEmpty)
          InfoRow(Icons.notes_outlined, ar ? 'السبب' : 'Reason', r.reason!.trim()),
        if (r.approvals.isNotEmpty) _approvalChain(ar, r),
        if (showActions) ...[
          const SizedBox(height: 4),
          Row(
            children: [
              // Reject — soft tonal red (matches the chip aesthetic)
              Expanded(
                child: FilledButton.icon(
                  onPressed: () => _decide(context, t, r, false),
                  icon: const Icon(Icons.close, size: 18, color: _red),
                  label: Text(t.t('reject'), style: const TextStyle(color: _red, fontWeight: FontWeight.bold)),
                  style: FilledButton.styleFrom(
                    backgroundColor: _red.withValues(alpha: 0.10),
                    elevation: 0,
                    shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14)),
                    padding: const EdgeInsets.symmetric(vertical: 14),
                  ),
                ),
              ),
              const SizedBox(width: 10),
              // Approve — solid green with soft shadow (mirrors the New Request button)
              Expanded(
                child: DecoratedBox(
                  decoration: BoxDecoration(
                    borderRadius: BorderRadius.circular(14),
                    boxShadow: [
                      BoxShadow(color: _green.withValues(alpha: 0.32), blurRadius: 12, offset: const Offset(0, 5)),
                    ],
                  ),
                  child: FilledButton.icon(
                    onPressed: () => _decide(context, t, r, true),
                    icon: const Icon(Icons.check, size: 18),
                    label: Text(t.t('approve'), style: const TextStyle(fontWeight: FontWeight.bold)),
                    style: FilledButton.styleFrom(
                      backgroundColor: _green,
                      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14)),
                      padding: const EdgeInsets.symmetric(vertical: 14),
                    ),
                  ),
                ),
              ),
            ],
          ),
        ],
      ],
    );
  }

  // multi-level approval chain timeline (level chips with status + approver)
  Widget _approvalChain(bool ar, LeaveRequest r) {
    String nm(ApprovalStep s) =>
        _nm(ar, s.approverAr, s.approverEn);
    return Padding(
      padding: const EdgeInsets.only(top: 6),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Divider(height: 14, color: Color(0xFFE9ECF3)),
          Text(ar ? 'سلسلة الاعتماد' : 'Approval chain',
              style: const TextStyle(color: _muted, fontSize: 11.5, fontWeight: FontWeight.w600)),
          const SizedBox(height: 6),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: r.approvals.map((s) {
              final c = _statusColor(s.status);
              final isCurrent = r.status == 0 && s.level == r.currentLevel;
              return Container(
                padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 5),
                decoration: BoxDecoration(
                  color: c.withValues(alpha: 0.12),
                  borderRadius: BorderRadius.circular(20),
                  border: isCurrent ? Border.all(color: kPrimary, width: 1.4) : null,
                ),
                child: Row(mainAxisSize: MainAxisSize.min, children: [
                  Icon(
                    s.status == 1 ? Icons.check_circle : (s.status == 2 ? Icons.cancel : Icons.schedule),
                    size: 12, color: c,
                  ),
                  const SizedBox(width: 4),
                  Text('${ar ? "م" : "L"}${s.level}: ${nm(s)}',
                      style: TextStyle(color: c, fontSize: 11, fontWeight: FontWeight.w600)),
                ]),
              );
            }).toList(),
          ),
        ],
      ),
    );
  }

  Widget _statusPill(String label, int status) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        decoration: BoxDecoration(
            color: _statusColor(status).withValues(alpha: 0.14),
            borderRadius: BorderRadius.circular(20)),
        child: Row(mainAxisSize: MainAxisSize.min, children: [
          Icon(Icons.circle, color: _statusColor(status), size: 7),
          const SizedBox(width: 5),
          Text(label,
              style: TextStyle(color: _statusColor(status), fontSize: 11.5, fontWeight: FontWeight.bold)),
        ]),
      );

  // ---------------- Approve / reject ----------------
  Future<void> _decide(BuildContext context, AppLocalizations t, LeaveRequest r, bool approve) async {
    final noteCtrl = TextEditingController();
    final ok = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(approve ? t.t('approve') : t.t('reject')),
        content: TextField(
          controller: noteCtrl,
          decoration: InputDecoration(labelText: t.t('decisionNote')),
          maxLines: 2,
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(ctx, false), child: Text(t.t('cancel'))),
          FilledButton(
            onPressed: () => Navigator.pop(ctx, true),
            style: FilledButton.styleFrom(backgroundColor: approve ? _green : _red),
            child: Text(approve ? t.t('approve') : t.t('reject')),
          ),
        ],
      ),
    );
    if (ok != true) return;
    final (done, msg) = await ApiService.instance.decideLeave(r.id, approve, note: noteCtrl.text.trim());
    if (!context.mounted) return;
    if (done) {
      setState(() {
        _reloadPending();
        _reloadMine();
      });
    } else {
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(msg ?? t.t('loginFailed')), backgroundColor: _red));
    }
  }

  // ---------------- New request sheet ----------------
  Future<void> _openNewRequest(BuildContext context, AppLocalizations t) async {
    final created = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.white,
      shape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.vertical(top: Radius.circular(20))),
      builder: (ctx) => _NewLeaveSheet(),
    );
    if (created == true && context.mounted) {
      setState(_reloadMine);
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(t.t('requestSubmitted'))));
    }
  }
}

class _NewLeaveSheet extends StatefulWidget {
  @override
  State<_NewLeaveSheet> createState() => _NewLeaveSheetState();
}

class _NewLeaveSheetState extends State<_NewLeaveSheet> {
  static const _primary = Color(0xFF1B84FF);
  late Future<List<LeaveType>> _types;
  int? _typeId;
  DateTime? _from;
  DateTime? _to;
  final _reason = TextEditingController();
  bool _saving = false;

  // work-day flags indexed by weekday (0=Sunday … 6=Saturday); null = any day allowed
  List<bool>? _workDays;

  @override
  void initState() {
    super.initState();
    _types = ApiService.instance.getLeaveTypes();
    ApiService.instance.getWorkDays().then((v) {
      if (mounted) setState(() => _workDays = v);
    });
  }

  // Dart weekday: Mon=1 … Sun=7 → map to Sun=0 … Sat=6 for the policy flags
  bool _isWorkDay(DateTime d) {
    if (_workDays == null) return true;
    return _workDays![d.weekday % 7];
  }

  // count working days in the selected range (for the hint)
  int _workingDays() {
    if (_from == null || _to == null || _to!.isBefore(_from!)) return 0;
    var n = 0;
    for (var d = _from!; !d.isAfter(_to!); d = d.add(const Duration(days: 1))) {
      if (_isWorkDay(d)) n++;
    }
    return n;
  }

  @override
  void dispose() {
    _reason.dispose();
    super.dispose();
  }

  String _nm(bool ar, String? a, String? b) =>
      (ar ? a : b)?.trim().isNotEmpty == true ? (ar ? a : b)! : (b ?? a ?? '-');

  Future<void> _pickDate(bool isFrom) async {
    final now = DateTime.now();
    var initial = isFrom ? (_from ?? now) : (_to ?? _from ?? now);
    // initialDate must satisfy the predicate — nudge forward to the next work day
    var guard = 0;
    while (!_isWorkDay(initial) && guard++ < 14) {
      initial = initial.add(const Duration(days: 1));
    }
    final picked = await showDatePicker(
      context: context,
      initialDate: initial,
      firstDate: DateTime(now.year - 1),
      lastDate: DateTime(now.year + 2),
      selectableDayPredicate: _isWorkDay,
    );
    if (picked != null) {
      setState(() {
        if (isFrom) {
          _from = picked;
          if (_to != null && _to!.isBefore(picked)) _to = picked;
        } else {
          _to = picked;
        }
      });
    }
  }

  Future<void> _submit(AppLocalizations t) async {
    if (_typeId == null || _from == null || _to == null) return;
    setState(() => _saving = true);
    final (ok, msg) = await ApiService.instance.createLeave(
      leaveTypeId: _typeId!,
      startDate: _from!,
      endDate: _to!,
      reason: _reason.text.trim().isEmpty ? null : _reason.text.trim(),
    );
    if (!mounted) return;
    if (ok) {
      Navigator.pop(context, true);
    } else {
      setState(() => _saving = false);
      ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(msg ?? t.t('loginFailed')), backgroundColor: const Color(0xFFD9214E)));
    }
  }

  // soft, on-brand field decoration (matches the profile's chip aesthetic)
  InputDecoration _dec(String label, IconData icon) => InputDecoration(
        labelText: label,
        labelStyle: const TextStyle(color: kMuted, fontSize: 13),
        floatingLabelStyle: const TextStyle(color: kPrimary, fontWeight: FontWeight.bold),
        filled: true,
        fillColor: kChipBg,
        prefixIcon: Icon(icon, color: kPrimary, size: 20),
        contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 16),
        border: OutlineInputBorder(borderRadius: BorderRadius.circular(12), borderSide: BorderSide.none),
        enabledBorder: OutlineInputBorder(borderRadius: BorderRadius.circular(12), borderSide: BorderSide.none),
        focusedBorder: OutlineInputBorder(
            borderRadius: BorderRadius.circular(12), borderSide: const BorderSide(color: kPrimary, width: 1.4)),
      );

  @override
  Widget build(BuildContext context) {
    final t = AppLocalizations.of(context);
    final ar = t.isAr;
    final df = DateFormat('yyyy/MM/dd');
    return Padding(
      padding: EdgeInsets.only(
        left: 20, right: 20, top: 16,
        bottom: MediaQuery.of(context).viewInsets.bottom + 20,
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Center(
            child: Container(
              width: 44, height: 4,
              decoration: BoxDecoration(
                  color: const Color(0xFFE4E7EE), borderRadius: BorderRadius.circular(4)),
            ),
          ),
          const SizedBox(height: 16),
          // header — matches the SectionCard header style
          Row(mainAxisAlignment: MainAxisAlignment.end, children: [
            Flexible(
              child: Text(t.t('newLeave'),
                  textAlign: TextAlign.end,
                  style: const TextStyle(fontSize: 16, fontWeight: FontWeight.bold, color: kInk)),
            ),
            const SizedBox(width: 8),
            const IconChip(Icons.beach_access, size: 34),
          ]),
          Container(
            height: 2, width: 36,
            margin: const EdgeInsetsDirectional.only(top: 4, bottom: 14, end: 42),
            color: kPrimary,
          ),
          // leave type
          FutureBuilder<List<LeaveType>>(
            future: _types,
            builder: (context, snap) {
              final types = snap.data ?? [];
              return DropdownButtonFormField<int>(
                initialValue: _typeId,
                isExpanded: true,
                decoration: _dec(t.t('leaveType'), Icons.category_outlined),
                hint: Text(t.t('selectLeaveType'), style: const TextStyle(color: kMuted)),
                items: types
                    .map((e) => DropdownMenuItem(value: e.id, child: Text(_nm(ar, e.nameAr, e.nameEn))))
                    .toList(),
                onChanged: (v) => setState(() => _typeId = v),
              );
            },
          ),
          const SizedBox(height: 14),
          Row(
            children: [
              Expanded(child: _dateField(t.t('fromDate'), _from == null ? null : df.format(_from!), () => _pickDate(true))),
              const SizedBox(width: 12),
              Expanded(child: _dateField(t.t('toDate'), _to == null ? null : df.format(_to!), () => _pickDate(false))),
            ],
          ),
          if (_from != null && _to != null) ...[
            const SizedBox(height: 8),
            Row(children: [
              const Icon(Icons.info_outline, size: 15, color: kPrimary),
              const SizedBox(width: 6),
              Flexible(
                child: Text(
                  ar
                      ? 'أيام الراحة لا تُحتسب — أيام العمل: ${_workingDays()} يوم'
                      : 'Rest days not counted — working days: ${_workingDays()}',
                  style: const TextStyle(color: kMuted, fontSize: 12),
                ),
              ),
            ]),
          ],
          const SizedBox(height: 14),
          TextField(
            controller: _reason,
            maxLines: 2,
            decoration: _dec(t.t('reason'), Icons.notes_outlined),
          ),
          const SizedBox(height: 22),
          SizedBox(
            width: double.infinity,
            height: 52,
            child: FilledButton(
              onPressed: (_typeId != null && _from != null && _to != null && !_saving)
                  ? () => _submit(t)
                  : null,
              style: FilledButton.styleFrom(
                backgroundColor: _primary,
                shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
              ),
              child: _saving
                  ? const SizedBox(height: 22, width: 22, child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
                  : Text(t.t('submit'), style: const TextStyle(fontSize: 16, fontWeight: FontWeight.bold)),
            ),
          ),
        ],
      ),
    );
  }

  Widget _dateField(String label, String? value, VoidCallback onTap) => InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: InputDecorator(
          decoration: _dec(label, Icons.calendar_today_outlined),
          child: Text(value ?? '—',
              style: TextStyle(
                  color: value == null ? kMuted : kInk,
                  fontWeight: value == null ? FontWeight.normal : FontWeight.w600)),
        ),
      );
}
