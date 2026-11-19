import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

import '../models/inventory_item.dart';
import '../models/rep.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';

/// SALES-REP MODE (the owner's reference, "mode-rep"): the navy band becomes the mode bar; the rep finds the
/// customer, sees what they owe and how much credit is left, takes an order (priced by the server for that customer)
/// and collects a payment, from the same visit.
(IconData, String, String) _mode(BuildContext context, [String sub = '']) =>
    (Lu.route, L(context, 'وضع المندوب', 'Sales-rep mode'), sub.isEmpty ? L(context, 'العملاء · الطلبيات · التحصيل', 'Customers · orders · collections') : sub);

String _m(BuildContext context, double v, String cur) => '${money(v, cur)} ${curLabel(cur, isAr(context))}';

/// A negative outstanding is money the customer has in credit (paid ahead / returns), not a debt.
String balanceText(BuildContext context, double v, String cur) => v < 0
    ? '${L(context, 'رصيد دائن له ', 'In credit ')}${_m(context, -v, cur)}'
    : v == 0
        ? L(context, 'لا مستحقات', 'Nothing owed')
        : '${L(context, 'المستحق ', 'Owes ')}${_m(context, v, cur)}';

/// SALES-REP MODE (the owner's reference, "rep"): the mode bar with the route and the rep, and the mode's three tabs —
/// today's visits, the customer at hand, and collecting from them.
class RepModeScreen extends StatefulWidget {
  const RepModeScreen({super.key});
  @override
  State<RepModeScreen> createState() => _RepModeScreenState();
}

class _RepModeScreenState extends State<RepModeScreen> {
  late Future<RepDay> _day = ApiService.instance.getRepToday();
  int _tab = 0;
  int? _visitId;   // the visit the rep opened; otherwise the day's current one

  void _reload() => setState(() => _day = ApiService.instance.getRepToday());

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

  String _dayHead(BuildContext context, RepDay d) {
    final n = DateTime.now();
    final count = d.visits.length;
    final visits = !isAr(context) ? '$count visits' : count == 1 ? 'زيارة واحدة' : count == 2 ? 'زيارتان' : count <= 10 ? '$count زيارات' : '$count زيارة';
    return '${weekdayName(context, n.weekday)} ${dayMonth(context, n)} · $visits';
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<RepDay>(
      future: _day,
      builder: (context, s) {
        final d = s.data;
        final sub = d == null ? '' : [d.routeName ?? L(context, 'بلا مسار اليوم', 'No route today'), d.rep].where((x) => x.isNotEmpty).join(' · ');
        final current = d == null ? null : (d.visits.where((v) => v.id == _visitId).firstOrNull ?? d.current);
        Widget body;
        if (s.hasError) {
          body = ExecBody(children: [EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر تحميل اليوم', 'Could not load the day'), text: L(context, 'تحقق من الاتصال ثم أعد المحاولة.', 'Check the connection and try again.'))]);
        } else if (d == null) {
          body = const Center(child: CircularProgressIndicator());
        } else if (_tab == 0) {
          body = _visits(context, d);
        } else if (current == null) {
          body = ExecBody(children: [
            EmptyNote(icon: Lu.store, title: L(context, 'لا زيارة مفتوحة', 'No open visit'), text: L(context, 'اختر زيارة من «زيارات اليوم»، أو ابحث عن عميل.', 'Pick a visit from "Today\'s visits", or search for a customer.')),
            const SizedBox(height: 12),
            OutlinedButton.icon(onPressed: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => const RepCustomersScreen())), icon: const Icon(Lu.search, size: 16), label: Text(L(context, 'البحث عن عميل', 'Find a customer'))),
          ]);
        } else if (_tab == 1) {
          body = CustomerVisitScreen(key: ValueKey('c${current.id}'), id: current.customerId, visitId: current.id, embedded: true, onChanged: _reload);
        } else {
          body = _CollectTab(key: ValueKey('k${current.id}'), visit: current, onDone: () { setState(() => _tab = 0); _reload(); });
        }
        return ExecFrame(
          mode: _mode(context, sub),
          bottomBar: ExecNav(
            items: [(Lu.route, L(context, 'زيارات اليوم', "Today's visits")), (Lu.store, L(context, 'العميل', 'Customer')), (Lu.handCoins, L(context, 'تحصيل', 'Collect'))],
            index: _tab,
            onTap: (i) => setState(() => _tab = i),
          ),
          child: body,
        );
      },
    );
  }

  Widget _visits(BuildContext context, RepDay d) {
    final cur = d.currency;
    final unit = curLabel(cur, isAr(context));
    String leftText(int n) => !isAr(context) ? '$n left' : n == 0 ? 'لا زيارات متبقية' : n == 1 ? 'بقيت زيارة واحدة' : n == 2 ? 'بقيت زيارتان' : n <= 10 ? 'بقيت $n زيارات' : 'بقيت $n زيارة';
    return ExecBody(
      onRefresh: () async => _reload(),
      children: [
        PageHead(L(context, 'زيارات اليوم', "Today's visits"), eyebrow: _dayHead(context, d)),
        FinancialCard(
          label: L(context, 'هدف اليوم', "Today's target"),
          icon: Lu.target,
          amount: Amount(money(d.achieved, cur), unit),
          note: [
            if (d.target > 0) L(context, 'من ${money(d.target, cur)}', 'of ${money(d.target, cur)}') else L(context, 'لا هدف محدد', 'No target set'),
            leftText(d.left),
          ].join(' · '),
        ),
        SectionHeading(L(context, 'خط السير', 'The route')),
        if (d.visits.isEmpty)
          EmptyNote(icon: Lu.route, title: L(context, 'لا زيارات اليوم', 'No visits today'), text: L(context, 'يرتّب مديرك مسارك من شاشة «مسارات المندوبين»؛ ويمكنك البحث عن أي عميل.', 'Your manager plans your route in "Sales-rep routes"; you can still find any customer.'))
        else
          DataList(children: [
            for (final v in d.visits)
              ItemRow(
                leading: IconBox(icon: v.status == 'Done' ? Lu.circleCheck : v.status == 'Skipped' ? Lu.circleX : Lu.mapPin, fg: v.status == 'Skipped' ? Brand.badC : Brand.support),
                title: v.customer,
                subtitle: [
                  if ((v.address ?? '').isNotEmpty) v.address!,
                  if (v.plannedTime != null) v.plannedTime!,
                  if (v.status == 'Done') L(context, 'تمت', 'done')
                  else if (v.status == 'Skipped') (v.outcome ?? L(context, 'لم تتم', 'skipped'))
                  else if (v.outstanding > 0) L(context, 'مستحق ${money(v.outstanding, cur)}', 'owes ${money(v.outstanding, cur)}'),
                ].join(' · '),
                end: v.status == 'Done'
                    ? Tag.ok(L(context, 'تمت', 'Done'))
                    : v.status == 'Skipped'
                        ? Tag.bad(L(context, 'لم تتم', 'Skipped'))
                        : v.status == 'Arrived' ? Tag.info(L(context, 'هنا الآن', 'Here now')) : null,
                onTap: v.open ? () => setState(() { _visitId = v.id; _tab = 1; }) : null,
              ),
          ]),
        const SizedBox(height: 12),
        TextButton.icon(onPressed: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => const RepCustomersScreen())), icon: const Icon(Lu.search, size: 16), label: Text(L(context, 'البحث عن عميل خارج المسار', 'Find a customer off the route'))),
      ],
    );
  }
}

/// The collect tab: the visit's customer loaded, then the collection screen in place.
class _CollectTab extends StatelessWidget {
  final RepVisit visit;
  final VoidCallback onDone;
  const _CollectTab({super.key, required this.visit, required this.onDone});
  @override
  Widget build(BuildContext context) => FutureBuilder<(RepCustomer, String, bool, bool, List<OpenInvoice>)>(
        future: ApiService.instance.getRepCustomer(visit.customerId),
        builder: (context, s) {
          if (s.hasError) return ExecBody(children: [EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر تحميل العميل', 'Could not load the customer'), text: L(context, 'تحقق من الاتصال ثم أعد المحاولة.', 'Check the connection and try again.'))]);
          if (!s.hasData) return const Center(child: CircularProgressIndicator());
          final (c, cur, _, canCollect, invoices) = s.data!;
          if (!canCollect) return ExecBody(children: [EmptyNote(icon: Lu.lock, title: L(context, 'لا تملك صلاحية التحصيل', 'You may not collect'), text: L(context, 'التحصيل لمن يملك صلاحية الدفع في الحسابات.', 'Collections need the accounting pay permission.'))]);
          return RepCollectScreen(customer: c, currency: cur, invoice: invoices.isEmpty ? null : invoices.first, visitId: visit.id, embedded: true, onDone: onDone);
        },
      );
}

/// Find any customer (off the route): search and paging; opening one shows the card.
class RepCustomersScreen extends StatefulWidget {
  const RepCustomersScreen({super.key});
  @override
  State<RepCustomersScreen> createState() => _RepCustomersScreenState();
}

class _RepCustomersScreenState extends State<RepCustomersScreen> {
  final _q = TextEditingController();
  final List<RepCustomer> _rows = [];
  String _cur = '';
  int _total = 0, _page = 1;
  bool _loading = false, _failed = false;

  @override
  void initState() {
    super.initState();
    _load(reset: true);
  }

  @override
  void dispose() {
    _q.dispose();
    super.dispose();
  }

  Future<void> _load({bool reset = false}) async {
    if (_loading) return;
    setState(() { _loading = true; if (reset) { _page = 1; _rows.clear(); } });
    try {
      final (total, cur, rows) = await ApiService.instance.getRepCustomers(q: _q.text.trim(), page: _page);
      if (!mounted) return;
      setState(() { _total = total; _cur = cur; _rows.addAll(rows); _page++; _failed = false; });
    } catch (_) {
      if (mounted) setState(() => _failed = true);
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return ExecFrame(
      mode: _mode(context),
      child: ExecBody(
        onRefresh: () => _load(reset: true),
        children: [
          BackLink(L(context, 'زيارات اليوم', "Today's visits")),
          PageHead(L(context, 'العملاء', 'Customers'), eyebrow: _total == 0 ? null : L(context, '$_total عميل', '$_total customers')),
          TextField(
            controller: _q, textInputAction: TextInputAction.search, onSubmitted: (_) => _load(reset: true),
            decoration: InputDecoration(prefixIcon: const Icon(Lu.search, size: 17), hintText: L(context, 'ابحث عن عميل بالاسم أو الهاتف', 'Search a customer by name or phone')),
          ),
          const SizedBox(height: 14),
          if (_failed && _rows.isEmpty)
            EmptyNote(icon: Lu.lock, title: L(context, 'تعذّر التحميل', 'Could not load'), text: L(context, 'لا تملك عرض العملاء أو أن الاتصال انقطع.', 'You may not view customers, or the connection dropped.'))
          else if (_rows.isEmpty && !_loading)
            EmptyNote(icon: Lu.store, title: L(context, 'لا عملاء', 'No customers'), text: L(context, 'غيّر كلمة البحث.', 'Try another search.'))
          else if (_rows.isNotEmpty)
            DataList(children: [
              for (final c in _rows)
                ItemRow(
                  leading: const IconBox(icon: Lu.store),
                  title: c.name,
                  subtitle: balanceText(context, c.outstanding, _cur),
                  end: c.headroom != null && c.headroom! <= 0 ? Tag.bad(L(context, 'تجاوز الحد', 'Over limit')) : null,
                  onTap: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => CustomerVisitScreen(id: c.id))),
                ),
            ]),
          if (_rows.length < _total)
            TextButton(onPressed: _loading ? null : _load, child: Text('${L(context, 'المزيد', 'More')} (${_rows.length}/$_total)')),
          if (_loading) const Padding(padding: EdgeInsets.all(16), child: Center(child: CircularProgressIndicator())),
        ],
      ),
    );
  }
}

/// The visit (the reference's "rep-customer"): what the customer owes on the financial card — the oldest overdue invoice
/// under the amount, the credit limit and what is left of it in the footer — then the visit's four actions (order,
/// collect, call, location) and the open invoices; tapping one collects against it.
class CustomerVisitScreen extends StatefulWidget {
  final int id;
  final int? visitId;          // opened from today's visits: the arrival is recorded, orders count toward the target
  final bool embedded;         // shown inside the mode's "customer" tab (no frame, no back link)
  final VoidCallback? onChanged;
  const CustomerVisitScreen({super.key, required this.id, this.visitId, this.embedded = false, this.onChanged});
  @override
  State<CustomerVisitScreen> createState() => _CustomerVisitScreenState();
}

class _CustomerVisitScreenState extends State<CustomerVisitScreen> {
  late Future<(RepCustomer, String, bool, bool, List<OpenInvoice>)> _f = ApiService.instance.getRepCustomer(widget.id);

  DateTime? _arrived;

  void _reload() {
    setState(() => _f = ApiService.instance.getRepCustomer(widget.id));
    widget.onChanged?.call();
  }

  @override
  void initState() {
    super.initState();
    if (widget.visitId != null) {
      ApiService.instance.arriveVisit(widget.visitId!).then((t) { if (mounted && t != null) setState(() => _arrived = t); });
    }
  }

  Widget _frame(BuildContext context, Widget child, [String sub = '']) => widget.embedded ? child : ExecFrame(mode: _mode(context, sub), child: child);

  Future<void> _close(BuildContext context, bool skipped) async {
    final c = TextEditingController();
    final note = await showDialog<String?>(
      context: context,
      builder: (d) => AlertDialog(
        title: Text(skipped ? L(context, 'لم تتم الزيارة', 'Visit not made') : L(context, 'إنهاء الزيارة', 'End the visit'), style: T.h2),
        content: TextField(controller: c, autofocus: true, maxLines: 2, decoration: InputDecoration(labelText: skipped ? L(context, 'السبب', 'Reason') : L(context, 'ملاحظة (اختياري)', 'Note (optional)'))),
        actions: [FilledButton(onPressed: () => Navigator.pop(d, c.text.trim()), child: Text(L(context, 'حفظ', 'Save')))],
      ),
    );
    if (note == null) return;
    final err = await ApiService.instance.closeVisit(widget.visitId!, skipped: skipped, outcome: note.isEmpty ? null : note);
    if (!context.mounted) return;
    if (err != null) return _no(context, skipped && note.isEmpty ? L(context, 'اذكر سبب عدم إتمام الزيارة', 'Say why the visit was not made') : err);
    widget.onChanged?.call();
    if (!widget.embedded) Navigator.of(context).maybePop();
  }

  Future<void> _launch(Uri uri, String failAr, String failEn) async {
    final ok = await launchUrl(uri, mode: LaunchMode.externalApplication).catchError((_) => false);
    if (!ok && mounted) _no(context, L(context, failAr, failEn));
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<(RepCustomer, String, bool, bool, List<OpenInvoice>)>(
      future: _f,
      builder: (context, s) {
        final Widget back = widget.embedded ? const SizedBox.shrink() : BackLink(L(context, 'العملاء', 'Customers'));
        if (s.hasError) return _frame(context, ExecBody(children: [back, EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر تحميل العميل', 'Could not load the customer'), text: L(context, 'تحقق من الاتصال ثم أعد المحاولة.', 'Check the connection and try again.'))]));
        if (!s.hasData) return _frame(context, const Center(child: CircularProgressIndicator()));
        final (c, cur, canOrder, canCollect, invoices) = s.data!;
        final unit = curLabel(cur, isAr(context));
        final credit = c.outstanding < 0;
        final late = invoices.where((i) => i.overdueDays > 0).toList()..sort((a, b) => b.overdueDays - a.overdueDays);
        Future<void> collect([OpenInvoice? inv]) async {
          if (!canCollect) return _no(context, L(context, 'التحصيل لمن يملك صلاحية الدفع في الحسابات.', 'Collections need the accounting pay permission.'));
          await Navigator.of(context).push(MaterialPageRoute(builder: (_) => RepCollectScreen(customer: c, currency: cur, invoice: inv ?? (invoices.isEmpty ? null : invoices.first), visitId: widget.visitId)));
          _reload();
        }

        final arrived = _arrived == null ? '' : L(context, 'وصلت ${_hm(_arrived!)}', 'arrived ${_hm(_arrived!)}');
        return _frame(
          context,
          ExecBody(
            onRefresh: () async => _reload(),
            children: [
              back,
              PageHead(c.name, eyebrow: [if ((c.address ?? '').isNotEmpty) c.address!, if (arrived.isNotEmpty) arrived].join(' · ')),
              FinancialCard(
                label: credit ? L(context, 'رصيد دائن للعميل', 'Customer in credit') : L(context, 'المستحق على العميل', 'Owed by the customer'),
                icon: Lu.store,
                amount: Amount(money(credit ? -c.outstanding : c.outstanding, cur), unit),
                note: c.outstanding == 0
                    ? L(context, 'لا مستحقات', 'Nothing owed')
                    : late.isEmpty
                        ? L(context, 'لا فواتير متأخرة', 'No invoice overdue')
                        : L(context, 'فاتورة متأخرة ${_days(context, late.first.overdueDays)}', 'An invoice ${late.first.overdueDays} days overdue'),
                footer: [
                  (L(context, 'الحد الائتماني', 'Credit limit'), c.creditLimit == null ? L(context, 'بلا حد', 'None') : whole(c.creditLimit!), c.creditLimit == null ? '' : unit),
                  (L(context, 'المتاح', 'Available'), c.headroom == null ? '—' : whole(c.headroom!), c.headroom == null ? '' : unit),
                ],
              ),
              SectionHeading(L(context, 'إجراءات الزيارة', 'Visit actions')),
              Row(children: [
                Expanded(
                  child: QuickTile(
                    icon: Lu.shoppingBasket,
                    label: L(context, 'طلبية', 'Order'),
                    onTap: canOrder ? () async { await Navigator.of(context).push(MaterialPageRoute(builder: (_) => RepOrderScreen(customer: c, currency: cur, visitId: widget.visitId))); _reload(); } : () => _no(context, L(context, 'أخذ الطلبيات لمدير المخزون أو أمين المخزن.', 'Orders are for an inventory manager or warehouse keeper.')),
                  ),
                ),
                Expanded(child: QuickTile(icon: Lu.handCoins, label: L(context, 'تحصيل', 'Collect'), onTap: () => collect())),
                Expanded(
                  child: QuickTile(
                    icon: Lu.phone,
                    label: L(context, 'اتصال', 'Call'),
                    onTap: () => (c.phone ?? '').trim().isEmpty
                        ? _no(context, L(context, 'لا رقم هاتف مسجّل لهذا العميل.', 'No phone number is recorded for this customer.'))
                        : _launch(Uri(scheme: 'tel', path: c.phone!.trim()), 'تعذّر فتح الاتصال', 'Could not open the dialer'),
                  ),
                ),
                Expanded(
                  child: QuickTile(
                    icon: Lu.navigation,
                    label: L(context, 'الموقع', 'Location'),
                    onTap: () => (c.address ?? '').trim().isEmpty
                        ? _no(context, L(context, 'لا عنوان مسجّل لهذا العميل.', 'No address is recorded for this customer.'))
                        : _launch(Uri.https('www.google.com', '/maps/search/', {'api': '1', 'query': c.address!.trim()}), 'تعذّر فتح الخريطة', 'Could not open the map'),
                  ),
                ),
              ]),
              SectionHeading(L(context, 'فواتير مفتوحة', 'Open invoices'), count: invoices.length),
              if (invoices.isEmpty)
                EmptyNote(icon: Lu.fileCheck, title: L(context, 'لا فواتير مفتوحة', 'No open invoices'), text: L(context, 'كل فواتير العميل مسدّدة.', "All the customer's invoices are paid."))
              else
                DataList(children: [
                  for (final i in invoices)
                    ItemRow(
                      leading: IconBox(icon: i.overdueDays > 0 ? Lu.fileWarning : Lu.fileText, fg: i.overdueDays > 0 ? Brand.badC : Brand.support),
                      title: i.invoiceNo,
                      subtitle: i.overdueDays > 0
                          ? L(context, 'متأخرة ${_days(context, i.overdueDays)}', '${i.overdueDays} days overdue')
                          : i.dueDate == null ? '' : L(context, 'تستحق ${dayMonth(context, i.dueDate!)}', 'Due ${dayMonth(context, i.dueDate!)}'),
                      end: _money(i.open, cur),
                      onTap: () => collect(i),
                    ),
                ]),
              if (widget.visitId != null) ...[
                const SizedBox(height: 16),
                Row(children: [
                  Expanded(child: FilledButton(onPressed: () => _close(context, false), child: Text(L(context, 'إنهاء الزيارة', 'End the visit')))),
                  const SizedBox(width: 9),
                  Expanded(child: OutlinedButton(onPressed: () => _close(context, true), child: Text(L(context, 'لم تتم الزيارة', 'Visit not made')))),
                ]),
              ],
            ],
          ),
          c.name,
        );
      },
    );
  }

  String _hm(DateTime t) => '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';

  void _no(BuildContext context, String why) => ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(why)));
}

/// "12 يوماً" / "3 أيام" / "يوم واحد" — the count agrees with the number.
String _days(BuildContext context, int n) => !isAr(context) ? '$n days' : n == 1 ? 'يوماً واحداً' : n == 2 ? 'يومين' : n <= 10 ? '$n أيام' : '$n يوماً';

/// An amount in a list: the whole part in ink, the fraction in the reference's grey.
Widget _money(double v, String cur) {
  final s = money(v, cur);
  final i = s.indexOf('.');
  return Text.rich(
    TextSpan(children: [
      TextSpan(text: i < 0 ? s : s.substring(0, i), style: const TextStyle(fontWeight: FontWeight.w700, color: Brand.inkC)),
      if (i >= 0) TextSpan(text: s.substring(i), style: const TextStyle(fontWeight: FontWeight.w500, color: Brand.fils)),
    ]),
    textDirection: TextDirection.ltr,
    style: const TextStyle(fontSize: 12.5),
  );
}

/// The order: the rep picks items and quantities; every price comes from the server for THIS customer.
class RepOrderScreen extends StatefulWidget {
  final RepCustomer customer;
  final String currency;
  final int? visitId;   // taken at a visit: its value counts toward the day's target (recorded by the server)
  const RepOrderScreen({super.key, required this.customer, required this.currency, this.visitId});
  @override
  State<RepOrderScreen> createState() => _RepOrderScreenState();
}

class _RepOrderScreenState extends State<RepOrderScreen> {
  final Map<int, double> _qty = {};
  final _q = TextEditingController();
  List<InventoryItem> _found = [];
  OrderPreview? _preview;
  bool _busy = false;

  @override
  void dispose() {
    _q.dispose();
    super.dispose();
  }

  Future<void> _search(String q) async {
    if (q.trim().isEmpty) return setState(() => _found = []);
    final r = await ApiService.instance.getInventoryItems(q: q.trim()).catchError((_) => <InventoryItem>[]);
    if (!mounted) return;
    final exact = r.where((i) => i.code == q.trim() || i.barcode == q.trim()).toList();
    if (exact.length == 1) { _add(exact.first); _q.clear(); return setState(() => _found = []); }
    setState(() => _found = r.take(8).toList());
  }

  void _add(InventoryItem it) => _set(it.id, (_qty[it.id] ?? 0) + 1);

  Future<void> _set(int id, double q) async {
    setState(() { q <= 0 ? _qty.remove(id) : _qty[id] = q; });
    if (_qty.isEmpty) return setState(() => _preview = null);
    final p = await ApiService.instance.previewOrder(widget.customer.id, Map.of(_qty));
    if (mounted) setState(() => _preview = p);
  }

  Future<void> _save() async {
    setState(() => _busy = true);
    final (ok, msg, warn) = await ApiService.instance.saveOrder(widget.customer.id, _qty, notes: 'mobile', visitId: widget.visitId);
    if (!mounted) return;
    setState(() => _busy = false);
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(ok ? '${L(context, 'حُفظت الطلبية ', 'Order saved ')}${msg ?? ''}${warn == null ? '' : ' — $warn'}' : (msg ?? L(context, 'تعذّر حفظ الطلبية', 'The order could not be saved')))));
    if (ok) Navigator.of(context).pop();
  }

  String _n(double q) => q == q.roundToDouble() ? q.toInt().toString() : q.toString();

  @override
  Widget build(BuildContext context) {
    final p = _preview;
    final cur = widget.currency;
    return ExecFrame(
      mode: _mode(context, widget.customer.name),
      child: Column(children: [
        Expanded(
          child: ExecBody(children: [
            BackLink(widget.customer.name),
            PageHead(L(context, 'طلبية', 'Order'), eyebrow: L(context, 'بسعر هذا العميل من الخادم', "Priced by the server for this customer")),
            ScanBox(
              text: L(context, 'امسح الصنف — كل مسح يضيف قطعة', 'Scan the item — each scan adds one'),
              hint: L(context, 'أو ابحث بالكود أو الاسم ثم Enter', 'or search by code or name, then Enter'),
              controller: _q, onSubmitted: _search,
            ),
            if (_found.isNotEmpty) ...[
              const SizedBox(height: 8),
              DataList(children: [
                for (final it in _found)
                  ItemRow(leading: const IconBox(icon: Lu.plus), title: it.name, subtitle: it.code, onTap: () { _add(it); setState(() { _found = []; _q.clear(); }); }),
              ]),
            ],
            SectionHeading(L(context, 'الأصناف', 'Items'), count: _qty.length),
            if (p == null || p.lines.isEmpty)
              EmptyNote(icon: Lu.shoppingBasket, title: L(context, 'الطلبية فارغة', 'The order is empty'), text: L(context, 'امسح صنفاً أو ابحث عنه ليُضاف بسعر العميل.', "Scan or search an item; it is added at the customer's price."))
            else
              DataList(children: [
                for (final l in p.lines)
                  ItemRow(
                    leading: const IconBox(icon: Lu.package),
                    title: l.name,
                    // the price formula is left-to-right inside an Arabic line: isolate it (U+2066 … U+2069) so it reads in order
                    subtitle: '\u2066${money(l.unitPrice, cur)} × ${_n(l.qty)}${l.discount > 0 ? ' − ${money(l.discount, cur)}' : ''}${l.taxRate > 0 ? ' + ${l.taxRate.toStringAsFixed(0)}%' : ''}\u2069',
                    end: QtyStepper(value: _n(_qty[l.itemId] ?? l.qty), onMinus: () => _set(l.itemId, (_qty[l.itemId] ?? 0) - 1), onPlus: () => _set(l.itemId, (_qty[l.itemId] ?? 0) + 1)),
                  ),
              ]),
            if (p != null && !p.ok) ...[const SizedBox(height: 10), _Notice(text: p.message ?? L(context, 'تعذّر التسعير', 'Pricing failed'), bad: true)],
            if (p != null && p.warning != null) ...[const SizedBox(height: 10), _Notice(text: p.warning!, bad: false)],
            if (p != null && p.overLimit) ...[
              const SizedBox(height: 10),
              _Notice(bad: true, text: L(context, 'المستحق + هذه الطلبية يتجاوز الحد الائتماني؛ ستُرفض الفاتورة عند الإصدار ما لم يُسدَّد.', 'Outstanding + this order exceeds the credit limit; invoicing will be refused unless it is paid down.')),
            ],
          ]),
        ),
        Container(
          padding: EdgeInsets.fromLTRB(22, 12, 22, MediaQuery.of(context).padding.bottom + 12),
          decoration: const BoxDecoration(color: Brand.paper, border: Border(top: BorderSide(color: Brand.lineC))),
          child: Row(children: [
            Expanded(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisSize: MainAxisSize.min, children: [
                Text(L(context, 'الإجمالي', 'Total'), style: T.small),
                Text(p == null ? '—' : _m(context, p.total, cur), style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w700, color: Brand.inkC)),
              ]),
            ),
            FilledButton.icon(onPressed: _busy || p == null || !p.ok ? null : _save, icon: const Icon(Lu.check, size: 16), label: Text(L(context, 'حفظ الطلبية', 'Save order'))),
          ]),
        ),
      ]),
    );
  }
}

/// A short line from the server in the house treatment: red for a refusal, the soft brand colour otherwise.
class _Notice extends StatelessWidget {
  final String text;
  final bool bad;
  const _Notice({required this.text, required this.bad});
  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.all(13),
        decoration: BoxDecoration(color: bad ? Brand.badSoft : Brand.soft, borderRadius: BorderRadius.circular(13)),
        child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Icon(bad ? Lu.circleAlert : Lu.info, size: 15, color: bad ? Brand.badC : Brand.support),
          const SizedBox(width: 8),
          Expanded(child: Text(text, style: TextStyle(fontSize: 12, color: bad ? Brand.badC : Brand.inkC))),
        ]),
      );
}

/// The collection (the reference's "rep-collect"): what the customer owes as a tag, the amount large in the middle
/// (tap to change it) with the invoice it settles under it, the method, the cash or bank account, then the receipt
/// voucher. The web offers cash and bank only — a cheque needs its own accounting (a cheques-under-collection account),
/// which the system does not have, so it is not offered here either.
class RepCollectScreen extends StatefulWidget {
  final RepCustomer customer;
  final String currency;
  final OpenInvoice? invoice;
  final int? visitId;          // collected at a visit: the visit records the amount and is done
  final bool embedded;         // shown inside the mode's "collect" tab
  final VoidCallback? onDone;
  const RepCollectScreen({super.key, required this.customer, required this.currency, this.invoice, this.visitId, this.embedded = false, this.onDone});
  @override
  State<RepCollectScreen> createState() => _RepCollectScreenState();
}

class _RepCollectScreenState extends State<RepCollectScreen> {
  late final Future<List<CashAccount>> _accounts = ApiService.instance.getCashAccounts();
  late double _amount = widget.invoice?.open ?? (widget.customer.outstanding > 0 ? widget.customer.outstanding : 0);
  String _method = 'Cash';
  int? _account;
  bool _busy = false;

  Future<void> _editAmount() async {
    final c = TextEditingController(text: _amount > 0 ? money(_amount, widget.currency).replaceAll(',', '') : '');
    final v = await showDialog<double?>(
      context: context,
      builder: (d) => AlertDialog(
        title: Text(L(context, 'المبلغ المحصّل', 'Amount collected'), style: T.h2),
        content: TextField(controller: c, autofocus: true, keyboardType: const TextInputType.numberWithOptions(decimal: true), textDirection: TextDirection.ltr, decoration: InputDecoration(suffixText: curLabel(widget.currency, isAr(context)))),
        actions: [FilledButton(onPressed: () => Navigator.pop(d, double.tryParse(c.text.trim())), child: Text(L(context, 'حفظ', 'Save')))],
      ),
    );
    if (v != null && v > 0) setState(() => _amount = v);
  }

  Future<void> _send() async {
    if (_amount <= 0 || _account == null) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(L(context, 'أدخل مبلغاً واختر الحساب', 'Enter an amount and choose the account'))));
      return;
    }
    setState(() => _busy = true);
    final (ok, err) = await ApiService.instance.collect(widget.customer.id, _amount, _method, _account!);
    if (!mounted) return;
    setState(() => _busy = false);
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(ok ? L(context, 'صدر سند القبض', 'Receipt issued') : (err ?? L(context, 'تعذّر إصدار السند', 'The receipt could not be issued')))));
    if (!ok) return;
    if (widget.visitId != null) await ApiService.instance.visitCollected(widget.visitId!, _amount);
    if (!mounted) return;
    widget.onDone?.call();
    if (!widget.embedded) Navigator.of(context).pop();
  }

  @override
  Widget build(BuildContext context) {
    final c = widget.customer;
    final cur = widget.currency;
    final unit = curLabel(cur, isAr(context));
    final body = FutureBuilder<List<CashAccount>>(
        future: _accounts,
        builder: (context, s) {
          final head = [if (!widget.embedded) BackLink(c.name), PageHead(L(context, 'تحصيل', 'Collect'), eyebrow: widget.embedded ? '${L(context, 'سند قبض', 'Receipt voucher')} · ${c.name}' : L(context, 'سند قبض', 'Receipt voucher'))];
          if (s.hasError) return ExecBody(children: [...head, EmptyNote(icon: Lu.lock, title: L(context, 'لا تملك صلاحية التحصيل', 'You may not collect'), text: L(context, 'التحصيل لمن يملك صلاحية الدفع في الحسابات.', 'Collections need the accounting pay permission.'))]);
          if (!s.hasData) return const Center(child: CircularProgressIndicator());
          final accs = s.data!;
          _account ??= accs.isEmpty ? null : accs.first.id;
          final acc = accs.where((a) => a.id == _account).firstOrNull;
          return ExecBody(children: [
            ...head,
            Align(
              alignment: AlignmentDirectional.centerStart,
              child: c.outstanding > 0 ? Tag('${L(context, 'المستحق', 'Due')} ${money(c.outstanding, cur)} $unit') : Tag.info(balanceText(context, c.outstanding, cur)),
            ),
            GestureDetector(
              onTap: _editAmount,
              child: Padding(
                padding: const EdgeInsets.symmetric(vertical: 22),
                child: Column(children: [
                  Amount(money(_amount, cur), unit, size: 42, center: true),
                  const SizedBox(height: 8),
                  Text(widget.invoice?.invoiceNo ?? L(context, 'على حساب العميل', "On the customer's account"), style: T.sub),
                ]),
              ),
            ),
            ChipTabs(items: [L(context, 'نقد', 'Cash'), L(context, 'تحويل بنكي', 'Bank transfer')], index: _method == 'Cash' ? 0 : 1, onTap: (i) => setState(() => _method = i == 0 ? 'Cash' : 'Bank')),
            FieldBox(
              label: L(context, 'إلى حساب', 'Into account'),
              onTap: accs.length < 2
                  ? null
                  : () async {
                      final id = await pickFromSheet<int>(context, title: L(context, 'إلى حساب', 'Into account'), selected: _account, options: [for (final a in accs) (a.id, a.name, a.code)]);
                      if (id != null) setState(() => _account = id);
                    },
              child: Row(children: [
                Expanded(child: Text(acc == null ? L(context, 'لا حساب نقدية متاح', 'No cash account available') : '${acc.name} · ${acc.code}', overflow: TextOverflow.ellipsis)),
                if (accs.length > 1) const Icon(Lu.chevronDown, size: 15, color: Brand.support),
              ]),
            ),
            const SizedBox(height: 14),
            FilledButton(onPressed: _busy || _account == null ? null : _send, child: Text(L(context, 'إصدار سند القبض', 'Issue the receipt'))),
            const SizedBox(height: 10),
            Text(L(context, 'يُرحَّل السند في الحسابات فور إصداره ويُخصم من مستحقات العميل.', 'The voucher posts to the accounts at once and reduces what the customer owes.'), textAlign: TextAlign.center, style: T.small),
          ]);
        },
    );
    return widget.embedded ? body : ExecFrame(mode: _mode(context, c.name), child: body);
  }
}
