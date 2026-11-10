import 'package:flutter/material.dart';

import '../models/inventory_item.dart';
import '../models/rep.dart';
import '../services/api_service.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';

/// SALES-REP MODE — the prototype's "mode-rep": find the customer, see what they owe and how much credit is left, take
/// an order (priced by the server for that customer) and collect a payment, from the same visit.
class RepModeScreen extends StatefulWidget {
  const RepModeScreen({super.key});
  @override
  State<RepModeScreen> createState() => _RepModeScreenState();
}

class _RepModeScreenState extends State<RepModeScreen> {
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
    return PageFrame(
      title: L(context, 'وضع المندوب', 'Sales-rep mode'),
      child: Column(children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 12, 16, 6),
          child: TextField(
            controller: _q, textInputAction: TextInputAction.search, onSubmitted: (_) => _load(reset: true),
            decoration: InputDecoration(prefixIcon: const Icon(Icons.search), hintText: L(context, 'ابحث عن عميل بالاسم أو الهاتف', 'Search a customer by name or phone')),
          ),
        ),
        Expanded(
          child: _failed && _rows.isEmpty
              ? ListView(padding: const EdgeInsets.all(16), children: [SectionCard(child: EmptyNote(icon: Icons.lock_outline, title: L(context, 'تعذّر التحميل', 'Could not load'), text: L(context, 'لا تملك عرض العملاء أو أن الاتصال انقطع.', 'You may not view customers, or the connection dropped.')))])
              : ListView(padding: const EdgeInsets.fromLTRB(16, 6, 16, 16), children: [
                  if (_rows.isEmpty && !_loading) SectionCard(child: EmptyNote(icon: Icons.storefront_outlined, title: L(context, 'لا عملاء', 'No customers'), text: L(context, 'غيّر كلمة البحث.', 'Try another search.'))),
                  if (_rows.isNotEmpty)
                    SectionCard(
                      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
                      child: Column(children: [
                        for (final c in _rows)
                          RowItem(
                            leading: const IconTile(Icons.storefront_outlined),
                            title: c.name,
                            subtitle: balanceText(context, c.outstanding, _cur),
                            trailing: c.headroom != null && c.headroom! <= 0 ? Pill(L(context, 'تجاوز الحد', 'Over limit'), fg: Brand.bad, bg: Brand.badTint) : Icon(isAr(context) ? Icons.chevron_left : Icons.chevron_right, color: Brand.muted),
                            onTap: () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => CustomerVisitScreen(id: c.id))),
                          ),
                      ]),
                    ),
                  if (_rows.length < _total)
                    TextButton(onPressed: _loading ? null : _load, child: Text('${L(context, 'المزيد', 'More')} (${_rows.length}/$_total)')),
                  if (_loading) const Padding(padding: EdgeInsets.all(16), child: Center(child: CircularProgressIndicator())),
                ]),
        ),
      ]),
    );
  }
}

/// A negative outstanding is money the customer has in credit (paid ahead / returns), not a debt.
String balanceText(BuildContext context, double v, String cur) => v < 0
    ? '${L(context, 'رصيد دائن له ', 'In credit ')}${money(-v, cur)} $cur'
    : v == 0
        ? L(context, 'لا مستحقات', 'Nothing owed')
        : '${L(context, 'المستحق ', 'Owes ')}${money(v, cur)} $cur';

/// The visit: the customer card with what they owe, and the two things a rep does there.
class CustomerVisitScreen extends StatefulWidget {
  final int id;
  const CustomerVisitScreen({super.key, required this.id});
  @override
  State<CustomerVisitScreen> createState() => _CustomerVisitScreenState();
}

class _CustomerVisitScreenState extends State<CustomerVisitScreen> {
  late Future<(RepCustomer, String, bool, bool)> _f = ApiService.instance.getRepCustomer(widget.id);

  void _reload() => setState(() => _f = ApiService.instance.getRepCustomer(widget.id));

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<(RepCustomer, String, bool, bool)>(
      future: _f,
      builder: (context, s) {
        if (s.hasError) return PageFrame(title: L(context, 'العميل', 'Customer'), child: Center(child: Text(L(context, 'تعذّر تحميل العميل', 'Could not load the customer'))));
        if (!s.hasData) return PageFrame(title: L(context, 'العميل', 'Customer'), child: const Center(child: CircularProgressIndicator()));
        final (c, cur, canOrder, canCollect) = s.data!;
        String m(double v) => '${money(v, cur)} $cur';
        Widget kpi(String ar, String en, String v, {Color? color}) => Expanded(
              child: Container(
                margin: const EdgeInsets.all(4), padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(color: Colors.white, borderRadius: BorderRadius.circular(16)),
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text(L(context, ar, en), style: const TextStyle(fontSize: 11.5, color: Brand.muted)),
                  FittedBox(fit: BoxFit.scaleDown, child: Text(v, style: TextStyle(fontSize: 17, fontWeight: FontWeight.w700, color: color ?? Brand.ink))),
                ]),
              ),
            );
        return PageFrame(
          title: c.name,
          child: ListView(padding: const EdgeInsets.all(12), children: [
            Row(children: [
              c.outstanding < 0
                  ? kpi('رصيد دائن له', 'In credit', m(-c.outstanding), color: Brand.ok)
                  : kpi('المستحق', 'Outstanding', m(c.outstanding), color: c.outstanding > 0 ? Brand.bad : Brand.ok),
              kpi('المتاح من الحد', 'Credit left', c.headroom == null ? L(context, 'بلا حد', 'No limit') : m(c.headroom!), color: (c.headroom ?? 1) <= 0 ? Brand.bad : null),
            ]),
            if (c.phone != null || c.address != null)
              SectionCard(
                padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
                child: Column(children: [
                  if (c.phone != null) RowItem(leading: const IconTile(Icons.phone_outlined), title: c.phone!),
                  if (c.address != null) RowItem(leading: const IconTile(Icons.place_outlined), title: c.address!),
                ]),
              ),
            SectionCard(
              child: Row(children: [
                Expanded(
                  child: FilledButton.icon(
                    onPressed: canOrder ? () async { await Navigator.of(context).push(MaterialPageRoute(builder: (_) => RepOrderScreen(customer: c, currency: cur))); _reload(); } : null,
                    icon: const Icon(Icons.shopping_basket_outlined), label: Text(L(context, 'طلبية', 'Order')),
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: OutlinedButton.icon(
                    onPressed: canCollect ? () async { await Navigator.of(context).push(MaterialPageRoute(builder: (_) => RepCollectScreen(customer: c, currency: cur))); _reload(); } : null,
                    icon: const Icon(Icons.payments_outlined), label: Text(L(context, 'تحصيل', 'Collect')),
                  ),
                ),
              ]),
            ),
            if (!canOrder || !canCollect)
              Text(
                [if (!canOrder) L(context, 'أخذ الطلبيات لمدير المخزون أو أمين المخزن.', 'Orders are for an inventory manager or warehouse keeper.'), if (!canCollect) L(context, 'التحصيل لمن يملك صلاحية الدفع في الحسابات.', 'Collections need the accounting pay permission.')].join(' '),
                style: const TextStyle(fontSize: 12, color: Brand.muted),
              ),
          ]),
        );
      },
    );
  }
}

/// The order: the rep picks items and quantities; every price comes from the server for THIS customer.
class RepOrderScreen extends StatefulWidget {
  final RepCustomer customer;
  final String currency;
  const RepOrderScreen({super.key, required this.customer, required this.currency});
  @override
  State<RepOrderScreen> createState() => _RepOrderScreenState();
}

class _RepOrderScreenState extends State<RepOrderScreen> {
  final Map<int, double> _qty = {};
  final Map<int, String> _names = {};
  final _q = TextEditingController();
  List<InventoryItem> _found = [];
  OrderPreview? _preview;
  bool _busy = false;

  String m(double v) => '${money(v, widget.currency)} ${widget.currency}';

  Future<void> _search(String q) async {
    if (q.trim().isEmpty) return setState(() => _found = []);
    final r = await ApiService.instance.getInventoryItems(q: q.trim()).catchError((_) => <InventoryItem>[]);
    if (!mounted) return;
    final exact = r.where((i) => i.code == q.trim() || i.barcode == q.trim()).toList();
    if (exact.length == 1) { _add(exact.first); _q.clear(); return setState(() => _found = []); }
    setState(() => _found = r.take(8).toList());
  }

  void _add(InventoryItem it) { _names[it.id] = it.name; _set(it.id, (_qty[it.id] ?? 0) + 1); }

  Future<void> _set(int id, double q) async {
    setState(() { q <= 0 ? _qty.remove(id) : _qty[id] = q; });
    if (_qty.isEmpty) return setState(() => _preview = null);
    final p = await ApiService.instance.previewOrder(widget.customer.id, Map.of(_qty));
    if (mounted) setState(() => _preview = p);
  }

  Future<void> _save() async {
    setState(() => _busy = true);
    final (ok, msg, warn) = await ApiService.instance.saveOrder(widget.customer.id, _qty, notes: 'mobile');
    if (!mounted) return;
    setState(() => _busy = false);
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(ok ? '${L(context, 'حُفظت الطلبية ', 'Order saved ')}${msg ?? ''}${warn == null ? '' : ' — $warn'}' : (msg ?? L(context, 'تعذّر حفظ الطلبية', 'The order could not be saved')))));
    if (ok) Navigator.of(context).pop();
  }

  @override
  Widget build(BuildContext context) {
    final p = _preview;
    return PageFrame(
      title: '${L(context, 'طلبية — ', 'Order — ')}${widget.customer.name}',
      child: Column(children: [
        Expanded(
          child: ListView(padding: const EdgeInsets.all(16), children: [
            SectionCard(
              child: Column(children: [
                TextField(
                  controller: _q, textInputAction: TextInputAction.search, onSubmitted: _search,
                  decoration: InputDecoration(prefixIcon: const Icon(Icons.qr_code_scanner), hintText: L(context, 'امسح أو ابحث عن صنف ثم Enter', 'Scan or search an item, then Enter')),
                ),
                for (final it in _found)
                  RowItem(leading: const IconTile(Icons.add, size: 36), title: it.name, subtitle: it.code, onTap: () { _add(it); setState(() { _found = []; _q.clear(); }); }),
              ]),
            ),
            if (p != null && p.lines.isNotEmpty)
              SectionCard(
                title: L(context, 'الأصناف (بسعر العميل)', 'Items (customer price)'),
                padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
                child: Column(children: [
                  for (final l in p.lines)
                    RowItem(
                      leading: const IconTile(Icons.inventory_2_outlined, size: 36),
                      title: l.name,
                      subtitle: '${money(l.unitPrice, widget.currency)} × ${l.qty == l.qty.roundToDouble() ? l.qty.toInt() : l.qty}${l.discount > 0 ? ' − ${money(l.discount, widget.currency)}' : ''}${l.taxRate > 0 ? ' + ${l.taxRate.toStringAsFixed(0)}%' : ''}',
                      trailing: Row(mainAxisSize: MainAxisSize.min, children: [
                        IconButton(visualDensity: VisualDensity.compact, onPressed: () => _set(l.itemId, (_qty[l.itemId] ?? 0) - 1), icon: const Icon(Icons.remove_circle_outline)),
                        IconButton(visualDensity: VisualDensity.compact, onPressed: () => _set(l.itemId, (_qty[l.itemId] ?? 0) + 1), icon: const Icon(Icons.add_circle_outline)),
                      ]),
                    ),
                ]),
              ),
            if (p != null && !p.ok) _Banner(text: p.message ?? L(context, 'تعذّر التسعير', 'Pricing failed'), bad: true),
            if (p != null && p.warning != null) _Banner(text: p.warning!, bad: false),
            if (p != null && p.overLimit)
              _Banner(bad: true, text: L(context, 'المستحق + هذه الطلبية يتجاوز الحد الائتماني؛ ستُرفض الفاتورة عند الإصدار ما لم يُسدَّد.', 'Outstanding + this order exceeds the credit limit; invoicing will be refused unless it is paid down.')),
          ]),
        ),
        SafeArea(
          top: false,
          child: Padding(
            padding: const EdgeInsets.fromLTRB(16, 0, 16, 10),
            child: Row(children: [
              Expanded(child: Text(p == null ? '' : '${L(context, 'الإجمالي ', 'Total ')}${m(p.total)}', style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16, color: Brand.deep))),
              FilledButton.icon(onPressed: _busy || p == null || !p.ok ? null : _save, icon: const Icon(Icons.check), label: Text(L(context, 'حفظ الطلبية', 'Save order'))),
            ]),
          ),
        ),
      ]),
    );
  }
}

class _Banner extends StatelessWidget {
  final String text;
  final bool bad;
  const _Banner({required this.text, required this.bad});
  @override
  Widget build(BuildContext context) => Container(
        margin: const EdgeInsets.only(bottom: 12), padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(color: bad ? Brand.badTint : Brand.tint2, borderRadius: BorderRadius.circular(12), border: Border.all(color: bad ? const Color(0xFFFCA5A5) : const Color(0xFFA8C5FF))),
        child: Text(text, style: TextStyle(fontSize: 12.5, color: bad ? Brand.bad : Brand.deep)),
      );
}

/// The collection: amount, method and the cash / bank account, posted as a receipt voucher.
class RepCollectScreen extends StatefulWidget {
  final RepCustomer customer;
  final String currency;
  const RepCollectScreen({super.key, required this.customer, required this.currency});
  @override
  State<RepCollectScreen> createState() => _RepCollectScreenState();
}

class _RepCollectScreenState extends State<RepCollectScreen> {
  late final Future<List<CashAccount>> _accounts = ApiService.instance.getCashAccounts();
  late final _amount = TextEditingController(text: widget.customer.outstanding > 0 ? money(widget.customer.outstanding, widget.currency).replaceAll(',', '') : '');
  String _method = 'Cash';
  int? _account;
  bool _busy = false;

  Future<void> _send() async {
    final a = double.tryParse(_amount.text.trim());
    if (a == null || a <= 0 || _account == null) return;
    setState(() => _busy = true);
    final (ok, err) = await ApiService.instance.collect(widget.customer.id, a, _method, _account!);
    if (!mounted) return;
    setState(() => _busy = false);
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(ok ? L(context, 'صدر سند القبض', 'Receipt issued') : (err ?? L(context, 'تعذّر إصدار السند', 'The receipt could not be issued')))));
    if (ok) Navigator.of(context).pop();
  }

  @override
  Widget build(BuildContext context) {
    return PageFrame(
      title: '${L(context, 'تحصيل — ', 'Collect — ')}${widget.customer.name}',
      child: FutureBuilder<List<CashAccount>>(
        future: _accounts,
        builder: (context, s) {
          if (s.hasError) return Center(child: Text(L(context, 'لا تملك صلاحية التحصيل', 'You may not collect')));
          if (!s.hasData) return const Center(child: CircularProgressIndicator());
          final accs = s.data!;
          _account ??= accs.isEmpty ? null : accs.first.id;
          return ListView(padding: const EdgeInsets.all(16), children: [
            SectionCard(
              child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                Text(balanceText(context, widget.customer.outstanding, widget.currency), style: const TextStyle(color: Brand.muted)),
                const SizedBox(height: 12),
                TextField(controller: _amount, keyboardType: const TextInputType.numberWithOptions(decimal: true), decoration: InputDecoration(labelText: L(context, 'المبلغ', 'Amount'), suffixText: widget.currency)),
                const SizedBox(height: 12),
                SegmentedButton<String>(
                  segments: [
                    ButtonSegment(value: 'Cash', icon: const Icon(Icons.payments_outlined), label: Text(L(context, 'نقد', 'Cash'))),
                    ButtonSegment(value: 'Bank', icon: const Icon(Icons.account_balance_outlined), label: Text(L(context, 'بنك', 'Bank'))),
                  ],
                  selected: {_method},
                  onSelectionChanged: (v) => setState(() => _method = v.first),
                ),
                const SizedBox(height: 12),
                DropdownButtonFormField<int>(
                  initialValue: _account, isExpanded: true,
                  decoration: InputDecoration(labelText: L(context, 'إلى حساب', 'Into account')),
                  items: [for (final a in accs) DropdownMenuItem(value: a.id, child: Text('${a.name} · ${a.code}', overflow: TextOverflow.ellipsis))],
                  onChanged: (v) => setState(() => _account = v),
                ),
                const SizedBox(height: 14),
                FilledButton.icon(onPressed: _busy || _account == null ? null : _send, icon: const Icon(Icons.receipt_long_outlined), label: Text(L(context, 'إصدار سند قبض', 'Issue receipt'))),
              ]),
            ),
          ]);
        },
      ),
    );
  }
}
