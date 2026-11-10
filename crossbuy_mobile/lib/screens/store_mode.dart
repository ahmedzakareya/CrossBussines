import 'package:flutter/material.dart';

import '../models/inventory_item.dart';
import '../models/store.dart';
import '../services/api_service.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';

/// STOREKEEPER MODE — a full-screen work mode (the prototype's "mode-store"): a blind stock count and a warehouse
/// transfer, each posted through /api/store under the same permission and approval threshold as the web screens.
///
/// Scanning: the code field takes a handheld scanner's keystrokes (it types the code and presses Enter) as well as a
/// typed item code, so a scan of an item on the sheet adds one to its count.
class StoreModeScreen extends StatefulWidget {
  const StoreModeScreen({super.key});
  @override
  State<StoreModeScreen> createState() => _StoreModeScreenState();
}

class _StoreModeScreenState extends State<StoreModeScreen> {
  late final Future<(List<StoreWarehouse>, List<StoreWarehouse>)> _whs = ApiService.instance.getStoreWarehouses();
  int _tab = 0;
  StoreWarehouse? _wh;

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<(List<StoreWarehouse>, List<StoreWarehouse>)>(
      future: _whs,
      builder: (context, s) {
        final title = L(context, 'وضع أمين المخزن', 'Storekeeper mode');
        if (s.hasError) return PageFrame(title: title, child: _note(context, Icons.cloud_off_outlined, L(context, 'تعذّر التحميل', 'Could not load'), L(context, 'تحقق من الاتصال ثم أعد المحاولة.', 'Check the connection and try again.')));
        if (!s.hasData) return PageFrame(title: title, child: const Center(child: CircularProgressIndicator()));
        final (mine, targets) = s.data!;
        if (mine.isEmpty) {
          return PageFrame(title: title, child: _note(context, Icons.lock_outline, L(context, 'لست أمين مخزن', 'Not a storekeeper'), L(context, 'الجرد والتحويل لمدير المخزون أو أمين المخزن في مستودعاته فقط.', 'Counts and transfers are for an inventory manager, or a warehouse keeper in their own warehouses.')));
        }
        _wh ??= mine.first;
        return Scaffold(
          appBar: AppBar(
            title: Text(title),
            shape: const Border(bottom: BorderSide(color: Brand.line)),
            bottom: PreferredSize(
              preferredSize: const Size.fromHeight(56),
              child: Padding(
                padding: const EdgeInsets.fromLTRB(16, 0, 16, 10),
                child: DropdownButtonFormField<int>(
                  initialValue: _wh!.id,
                  isExpanded: true,
                  decoration: InputDecoration(prefixIcon: const Icon(Icons.warehouse_outlined), isDense: true, labelText: L(context, 'المستودع', 'Warehouse')),
                  items: [for (final w in mine) DropdownMenuItem(value: w.id, child: Text(w.label, overflow: TextOverflow.ellipsis))],
                  onChanged: (v) => setState(() => _wh = mine.firstWhere((w) => w.id == v)),
                ),
              ),
            ),
          ),
          body: _tab == 0
              ? _CountView(key: ValueKey('count-${_wh!.id}'), warehouse: _wh!)
              : _TransferView(key: ValueKey('transfer-${_wh!.id}'), from: _wh!, targets: targets.where((t) => t.id != _wh!.id).toList()),
          bottomNavigationBar: NavigationBar(
            selectedIndex: _tab,
            onDestinationSelected: (i) => setState(() => _tab = i),
            destinations: [
              NavigationDestination(icon: const Icon(Icons.fact_check_outlined), label: L(context, 'جرد', 'Count')),
              NavigationDestination(icon: const Icon(Icons.swap_horiz), label: L(context, 'تحويل', 'Transfer')),
            ],
          ),
        );
      },
    );
  }
}

Widget _note(BuildContext context, IconData icon, String title, String text) =>
    ListView(padding: const EdgeInsets.all(16), children: [SectionCard(child: EmptyNote(icon: icon, title: title, text: text))]);

String _qty(double v) => v == v.roundToDouble() ? v.toInt().toString() : v.toStringAsFixed(3);

// ------------------------------------------------------------------------------------------------ blind count
class _CountView extends StatefulWidget {
  final StoreWarehouse warehouse;
  const _CountView({super.key, required this.warehouse});
  @override
  State<_CountView> createState() => _CountViewState();
}

class _CountViewState extends State<_CountView> {
  late Future<List<CountSheetItem>> _sheet = ApiService.instance.getCountSheet(widget.warehouse.id);
  final Map<int, double> _counted = {};
  final _scan = TextEditingController();
  final _scanFocus = FocusNode();
  String _filter = '';
  bool _busy = false;

  void _onScan(List<CountSheetItem> items, String code) {
    final hit = items.where((i) => i.matches(code)).toList();
    _scan.clear();
    _scanFocus.requestFocus();
    if (hit.isEmpty) {
      setState(() => _filter = code.trim());   // not a code: treat it as a name search
      return;
    }
    final it = hit.first;
    if (it.batchTracked) return _toast(L(context, 'صنف بدفعات: يُجرد على الويب لكل دفعة.', 'Batch item: counted per batch on the web.'));
    setState(() { _counted[it.id] = (_counted[it.id] ?? 0) + 1; _filter = ''; });
    _toast('${it.name} · ${_qty(_counted[it.id]!)} ${it.unit}');
  }

  void _toast(String m) => ScaffoldMessenger.of(context)..hideCurrentSnackBar()..showSnackBar(SnackBar(content: Text(m), duration: const Duration(milliseconds: 1400)));

  Future<void> _edit(CountSheetItem it) async {
    final c = TextEditingController(text: _counted[it.id] == null ? '' : _qty(_counted[it.id]!));
    final v = await showDialog<double?>(
      context: context,
      builder: (d) => AlertDialog(
        title: Text(it.name),
        content: TextField(controller: c, autofocus: true, keyboardType: const TextInputType.numberWithOptions(decimal: true), decoration: InputDecoration(labelText: L(context, 'الكمية المعدودة', 'Counted quantity'), suffixText: it.unit)),
        actions: [
          TextButton(onPressed: () => Navigator.pop(d, -1.0), child: Text(L(context, 'مسح', 'Clear'))),
          FilledButton(onPressed: () => Navigator.pop(d, double.tryParse(c.text.trim())), child: Text(L(context, 'حفظ', 'Save'))),
        ],
      ),
    );
    if (v == null) return;
    setState(() => v < 0 ? _counted.remove(it.id) : _counted[it.id] = v);
  }

  Future<void> _submit() async {
    setState(() => _busy = true);
    final r = await ApiService.instance.postCount(widget.warehouse.id, _counted, notes: 'mobile');
    if (!mounted) return;
    setState(() => _busy = false);
    if (!r.ok) return _toast(r.message ?? L(context, 'تعذّر إرسال الجرد', 'The count could not be sent'));
    await Navigator.of(context).push(MaterialPageRoute(builder: (_) => _CountResult(result: r, warehouse: widget.warehouse)));
    if (mounted) setState(() { _counted.clear(); _sheet = ApiService.instance.getCountSheet(widget.warehouse.id); });
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<List<CountSheetItem>>(
      future: _sheet,
      builder: (context, s) {
        if (s.hasError) return _note(context, Icons.lock_outline, L(context, 'غير مسموح', 'Not allowed'), L(context, 'لا تملك الجرد في هذا المستودع.', 'You may not count this warehouse.'));
        if (!s.hasData) return const Center(child: CircularProgressIndicator());
        final items = s.data!;
        if (items.isEmpty) return _note(context, Icons.inventory_2_outlined, L(context, 'لا أصناف', 'No items'), L(context, 'لا يوجد رصيد مسجّل في هذا المستودع.', 'No stock is recorded in this warehouse.'));
        // counted items rise to the top, so the line a scan just changed is always in view
        final shown = (_filter.isEmpty ? items : items.where((i) => i.name.contains(_filter) || i.code.toLowerCase().contains(_filter.toLowerCase())).toList()).toList()
          ..sort((a, b) => (_counted.containsKey(b.id) ? 1 : 0) - (_counted.containsKey(a.id) ? 1 : 0));
        return Column(children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
            child: TextField(
              controller: _scan, focusNode: _scanFocus, autofocus: true, textInputAction: TextInputAction.done,
              onSubmitted: (v) => _onScan(items, v),
              onChanged: (v) { if (v.isEmpty && _filter.isNotEmpty) setState(() => _filter = ''); },
              decoration: InputDecoration(prefixIcon: const Icon(Icons.qr_code_scanner), hintText: L(context, 'امسح الباركود أو اكتب الكود / الاسم', 'Scan a barcode, or type a code / name')),
            ),
          ),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
            child: Container(
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(color: Brand.tint2, borderRadius: BorderRadius.circular(12), border: Border.all(color: const Color(0xFFA8C5FF))),
              child: Text(L(context, 'جرد أعمى: الرصيد الدفتري لا يظهر هنا، ويُعرض الفرق بعد الإرسال. تُعدَّل الأصناف المعدودة فقط.', 'Blind count: the book quantity is hidden here and the variance shows after sending. Only counted items are adjusted.'), style: const TextStyle(fontSize: 12, color: Brand.deep)),
            ),
          ),
          Expanded(
            child: ListView(padding: const EdgeInsets.fromLTRB(16, 4, 16, 16), children: [
              SectionCard(
                padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
                child: Column(children: [
                  for (final it in shown)
                    RowItem(
                      leading: IconTile(Icons.inventory_2_outlined, fg: _counted.containsKey(it.id) ? Brand.ok : Brand.brand, bg: _counted.containsKey(it.id) ? Brand.okTint : Brand.tint2),
                      title: it.name,
                      subtitle: '${it.code}${it.batchTracked ? L(context, ' · بدفعات (على الويب)', ' · batches (on the web)') : ''}',
                      trailing: it.batchTracked
                          ? const Icon(Icons.block, color: Brand.muted, size: 18)
                          : Text(_counted[it.id] == null ? '—' : '${_qty(_counted[it.id]!)} ${it.unit}', style: TextStyle(fontWeight: FontWeight.w700, color: _counted[it.id] == null ? Brand.muted : Brand.ink)),
                      onTap: it.batchTracked ? null : () => _edit(it),
                    ),
                ]),
              ),
            ]),
          ),
          SafeArea(
            top: false,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 10),
              child: SizedBox(
                width: double.infinity,
                child: FilledButton.icon(
                  onPressed: _busy || _counted.isEmpty ? null : _submit,
                  icon: const Icon(Icons.send),
                  label: Text('${L(context, 'إرسال الجرد', 'Submit count')} (${_counted.length})'),
                ),
              ),
            ),
          ),
        ]);
      },
    );
  }
}

class _CountResult extends StatelessWidget {
  final StoreResult result;
  final StoreWarehouse warehouse;
  const _CountResult({required this.result, required this.warehouse});

  @override
  Widget build(BuildContext context) {
    if (result.pendingApproval) {
      return PageFrame(title: L(context, 'نتيجة الجرد', 'Count result'), child: _note(context, Icons.hourglass_top, L(context, 'أُرسل للاعتماد', 'Sent for approval'), L(context, 'قيمة الفروقات تتجاوز حدّ الاعتماد في الشركة؛ سيُرحَّل الجرد بعد موافقة مدير المخزون.', "The variance value exceeds the company's approval limit; the count posts once an inventory manager approves it.")));
    }
    return PageFrame(
      title: '${L(context, 'جرد ', 'Count ')}${result.docNo ?? ''}',
      child: ListView(padding: const EdgeInsets.all(16), children: [
        SectionCard(
          child: Column(children: [
            Text(L(context, 'قيمة التسوية', 'Adjustment value'), style: const TextStyle(color: Brand.muted)),
            Text(result.totalAdjValue.toStringAsFixed(3), style: TextStyle(fontSize: 26, fontWeight: FontWeight.w800, color: result.totalAdjValue < 0 ? Brand.bad : Brand.deep)),
            Text(warehouse.label, style: const TextStyle(fontSize: 12, color: Brand.muted)),
          ]),
        ),
        SectionCard(
          title: L(context, 'الفروقات', 'Variances'),
          child: Column(children: [
            Row(children: [
              Expanded(flex: 4, child: Text(L(context, 'الصنف', 'Item'), style: const TextStyle(fontSize: 11.5, color: Brand.muted))),
              for (final h in [L(context, 'الدفتري', 'Book'), L(context, 'المعدود', 'Counted'), L(context, 'الفرق', 'Diff')])
                Expanded(flex: 2, child: Text(h, textAlign: TextAlign.end, style: const TextStyle(fontSize: 11.5, color: Brand.muted))),
            ]),
            const Divider(),
            for (final l in result.lines)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 6),
                child: Row(children: [
                  Expanded(flex: 4, child: Text(l.name, maxLines: 2, style: const TextStyle(fontSize: 13))),
                  Expanded(flex: 2, child: Text(_qty(l.book), textAlign: TextAlign.end)),
                  Expanded(flex: 2, child: Text(_qty(l.counted), textAlign: TextAlign.end)),
                  Expanded(flex: 2, child: Text(l.diff > 0 ? '+${_qty(l.diff)}' : _qty(l.diff), textAlign: TextAlign.end, style: TextStyle(fontWeight: FontWeight.w700, color: l.diff == 0 ? Brand.ok : Brand.bad))),
                ]),
              ),
          ]),
        ),
      ]),
    );
  }
}

// ------------------------------------------------------------------------------------------------ transfer
class _TransferView extends StatefulWidget {
  final StoreWarehouse from;
  final List<StoreWarehouse> targets;
  const _TransferView({super.key, required this.from, required this.targets});
  @override
  State<_TransferView> createState() => _TransferViewState();
}

class _TransferViewState extends State<_TransferView> {
  StoreWarehouse? _to;
  final Map<int, (InventoryItem, double)> _lines = {};
  final _q = TextEditingController();
  List<InventoryItem> _found = [];
  bool _busy = false;

  Future<void> _search(String q) async {
    if (q.trim().isEmpty) return setState(() => _found = []);
    final r = await ApiService.instance.getInventoryItems(q: q.trim()).catchError((_) => <InventoryItem>[]);
    if (!mounted) return;
    // an exact code/barcode hit (a scan) goes straight onto the transfer
    final exact = r.where((i) => i.code == q.trim() || i.barcode == q.trim()).toList();
    if (exact.length == 1) {
      _add(exact.first);
      _q.clear();
      return setState(() => _found = []);
    }
    setState(() => _found = r.take(8).toList());
  }

  void _add(InventoryItem it) => setState(() => _lines[it.id] = (it, (_lines[it.id]?.$2 ?? 0) + 1));

  Future<void> _submit() async {
    if (_to == null) return;
    setState(() => _busy = true);
    final r = await ApiService.instance.postTransfer(widget.from.id, _to!.id, {for (final e in _lines.entries) e.key: e.value.$2}, notes: 'mobile');
    if (!mounted) return;
    setState(() => _busy = false);
    final msg = !r.ok
        ? (r.message ?? L(context, 'تعذّر التحويل', 'The transfer failed'))
        : r.pendingApproval
            ? L(context, 'قيمة التحويل تتجاوز حدّ الاعتماد: أُرسل للاعتماد', 'Above the approval limit: sent for approval')
            : '${L(context, 'رُحّل التحويل ', 'Transfer posted ')}${r.docNo ?? ''}';
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(msg)));
    if (r.ok) setState(() => _lines.clear());
  }

  @override
  Widget build(BuildContext context) {
    if (widget.targets.isEmpty) return _note(context, Icons.warehouse_outlined, L(context, 'لا مستودع آخر', 'No other warehouse'), L(context, 'لا يوجد مستودع آخر نشط للتحويل إليه.', 'There is no other active warehouse to transfer to.'));
    return Column(children: [
      Expanded(
        child: ListView(padding: const EdgeInsets.all(16), children: [
          SectionCard(
            child: Column(children: [
              Row(children: [
                const Icon(Icons.logout, color: Brand.muted, size: 18), const SizedBox(width: 8),
                Expanded(child: Text('${L(context, 'من: ', 'From: ')}${widget.from.label}', style: const TextStyle(fontWeight: FontWeight.w600))),
              ]),
              const SizedBox(height: 12),
              DropdownButtonFormField<int>(
                initialValue: _to?.id,
                isExpanded: true,
                decoration: InputDecoration(labelText: L(context, 'إلى', 'To'), prefixIcon: const Icon(Icons.login)),
                items: [for (final w in widget.targets) DropdownMenuItem(value: w.id, child: Text(w.label, overflow: TextOverflow.ellipsis))],
                onChanged: (v) => setState(() => _to = widget.targets.firstWhere((w) => w.id == v)),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _q, textInputAction: TextInputAction.search, onSubmitted: _search,
                decoration: InputDecoration(prefixIcon: const Icon(Icons.qr_code_scanner), hintText: L(context, 'امسح أو ابحث عن صنف ثم Enter', 'Scan or search an item, then Enter')),
              ),
              for (final it in _found)
                RowItem(
                  leading: const IconTile(Icons.add, size: 36),
                  title: it.name,
                  subtitle: '${it.code} · ${L(context, 'الرصيد الكلي ', 'total on hand ')}${_qty(it.onHand)}',
                  onTap: () { _add(it); setState(() { _found = []; _q.clear(); }); },
                ),
            ]),
          ),
          if (_lines.isNotEmpty)
            SectionCard(
              title: L(context, 'الأصناف المحوّلة', 'Items to transfer'),
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
              child: Column(children: [
                for (final e in _lines.entries)
                  RowItem(
                    leading: const IconTile(Icons.inventory_2_outlined, size: 36),
                    title: e.value.$1.name,
                    subtitle: e.value.$1.code,
                    trailing: Row(mainAxisSize: MainAxisSize.min, children: [
                      IconButton(visualDensity: VisualDensity.compact, onPressed: () => setState(() { final q = e.value.$2 - 1; q <= 0 ? _lines.remove(e.key) : _lines[e.key] = (e.value.$1, q); }), icon: const Icon(Icons.remove_circle_outline)),
                      Text(_qty(e.value.$2), style: const TextStyle(fontWeight: FontWeight.w700)),
                      IconButton(visualDensity: VisualDensity.compact, onPressed: () => _add(e.value.$1), icon: const Icon(Icons.add_circle_outline)),
                    ]),
                  ),
              ]),
            ),
        ]),
      ),
      SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 0, 16, 10),
          child: SizedBox(
            width: double.infinity,
            child: FilledButton.icon(
              onPressed: _busy || _lines.isEmpty || _to == null ? null : _submit,
              icon: const Icon(Icons.swap_horiz),
              label: Text('${L(context, 'ترحيل التحويل', 'Post transfer')} (${_lines.length})'),
            ),
          ),
        ),
      ),
    ]);
  }
}
