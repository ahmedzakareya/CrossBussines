import 'package:flutter/material.dart';

import '../models/inventory_item.dart';
import '../models/store.dart';
import '../services/api_service.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';

/// STOREKEEPER MODE (the owner's reference, "mode-store"): the navy band turns into the mode bar — the mode, the
/// warehouse, and "exit mode" — and the bottom bar holds the mode's own tabs: a blind count and a warehouse transfer.
/// Both post through /api/store under the same permission and approval threshold as the web screens.
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
        (IconData, String, String) mode(String sub) => (Lu.scanBarcode, title, sub);
        if (s.hasError) {
          return ExecFrame(mode: mode(''), child: ExecBody(children: [EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر التحميل', 'Could not load'), text: L(context, 'تحقق من الاتصال ثم أعد المحاولة.', 'Check the connection and try again.'))]));
        }
        if (!s.hasData) return ExecFrame(mode: mode(''), child: const Center(child: CircularProgressIndicator()));
        final (mine, targets) = s.data!;
        if (mine.isEmpty) {
          return ExecFrame(
            mode: mode(''),
            child: ExecBody(children: [EmptyNote(icon: Lu.lock, title: L(context, 'لست أمين مخزن', 'Not a storekeeper'), text: L(context, 'الجرد والتحويل لمدير المخزون أو أمين المخزن في مستودعاته فقط.', 'Counts and transfers are for an inventory manager, or a warehouse keeper in their own warehouses.'))]),
          );
        }
        _wh ??= mine.first;
        Future<void> pickWarehouse() async {
          if (mine.length < 2) return;
          final id = await pickFromSheet<int>(context, title: L(context, 'المستودع', 'Warehouse'), selected: _wh!.id, options: [for (final w in mine) (w.id, w.name, w.code)]);
          if (id != null) setState(() => _wh = mine.firstWhere((w) => w.id == id));
        }
        final whField = FieldBox(
          label: L(context, 'المستودع', 'Warehouse'),
          onTap: mine.length < 2 ? null : pickWarehouse,
          child: Row(children: [Expanded(child: Text(_wh!.label, overflow: TextOverflow.ellipsis)), if (mine.length > 1) const Icon(Lu.chevronDown, size: 15, color: Brand.support)]),
        );
        return ExecFrame(
          mode: mode(_wh!.label),
          bottomBar: ExecNav(
            items: [(Lu.scanBarcode, L(context, 'جرد', 'Count')), (Lu.arrowLeftRight, L(context, 'تحويل', 'Transfer'))],
            index: _tab,
            onTap: (i) => setState(() => _tab = i),
          ),
          child: _tab == 0
              ? _CountView(key: ValueKey('count-${_wh!.id}'), warehouse: _wh!, whField: whField)
              : _TransferView(key: ValueKey('transfer-${_wh!.id}'), from: _wh!, whField: whField, targets: targets.where((t) => t.id != _wh!.id).toList()),
        );
      },
    );
  }
}

String _qty(double v) => v == v.roundToDouble() ? v.toInt().toString() : v.toStringAsFixed(3);

void _toast(BuildContext context, String m) =>
    ScaffoldMessenger.of(context)..hideCurrentSnackBar()..showSnackBar(SnackBar(content: Text(m), duration: const Duration(milliseconds: 1400)));

// ------------------------------------------------------------------------------------------------ blind count
class _CountView extends StatefulWidget {
  final StoreWarehouse warehouse;
  final Widget whField;
  const _CountView({super.key, required this.warehouse, required this.whField});
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

  @override
  void dispose() {
    _scan.dispose();
    _scanFocus.dispose();
    super.dispose();
  }

  void _onScan(List<CountSheetItem> items, String code) {
    final hit = items.where((i) => i.matches(code)).toList();
    _scan.clear();
    _scanFocus.requestFocus();
    if (hit.isEmpty) {
      setState(() => _filter = code.trim());   // not a code: treat it as a name search
      return;
    }
    final it = hit.first;
    if (it.batchTracked) return _toast(context, L(context, 'صنف بدفعات: يُجرد على الويب لكل دفعة.', 'Batch item: counted per batch on the web.'));
    setState(() { _counted[it.id] = (_counted[it.id] ?? 0) + 1; _filter = ''; });
    _toast(context, '${it.name} · ${_qty(_counted[it.id]!)} ${it.unit}');
  }

  void _step(CountSheetItem it, double by) => setState(() {
        final q = (_counted[it.id] ?? 0) + by;
        q <= 0 ? _counted.remove(it.id) : _counted[it.id] = q;
      });

  Future<void> _edit(CountSheetItem it) async {
    final c = TextEditingController(text: _counted[it.id] == null ? '' : _qty(_counted[it.id]!));
    final v = await showDialog<double?>(
      context: context,
      builder: (d) => AlertDialog(
        title: Text(it.name, style: T.h2),
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
    if (!r.ok) return _toast(context, r.message ?? L(context, 'تعذّر إرسال الجرد', 'The count could not be sent'));
    await Navigator.of(context).push(MaterialPageRoute(builder: (_) => _CountResult(result: r, warehouse: widget.warehouse)));
    if (mounted) setState(() { _counted.clear(); _sheet = ApiService.instance.getCountSheet(widget.warehouse.id); });
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<List<CountSheetItem>>(
      future: _sheet,
      builder: (context, s) {
        final head = [PageHead(L(context, 'جرد المخزون', 'Stock count'), eyebrow: L(context, 'جرد أعمى', 'Blind count')), widget.whField];
        if (s.hasError) return ExecBody(children: [...head, EmptyNote(icon: Lu.lock, title: L(context, 'غير مسموح', 'Not allowed'), text: L(context, 'لا تملك الجرد في هذا المستودع.', 'You may not count this warehouse.'))]);
        if (!s.hasData) return const Center(child: CircularProgressIndicator());
        final items = s.data!;
        if (items.isEmpty) return ExecBody(children: [...head, EmptyNote(icon: Lu.package, title: L(context, 'لا أصناف', 'No items'), text: L(context, 'لا يوجد رصيد مسجّل في هذا المستودع.', 'No stock is recorded in this warehouse.'))]);
        // counted items rise to the top, so the line a scan just changed is always in view
        final shown = (_filter.isEmpty ? items : items.where((i) => i.name.contains(_filter) || i.code.toLowerCase().contains(_filter.toLowerCase())).toList()).toList()
          ..sort((a, b) => (_counted.containsKey(b.id) ? 1 : 0) - (_counted.containsKey(a.id) ? 1 : 0));
        return Column(children: [
          Expanded(
            child: ExecBody(children: [
              ...head,
              ScanBox(
                text: L(context, 'امسح الباركود — كل مسح يضيف قطعة', 'Scan the barcode — each scan adds one'),
                hint: L(context, 'أو اكتب كود الصنف أو اسمه', 'or type the item code or name'),
                controller: _scan, focusNode: _scanFocus, autofocus: true,
                onSubmitted: (v) => _onScan(items, v),
                onChanged: (v) { if (v.isEmpty && _filter.isNotEmpty) setState(() => _filter = ''); },
              ),
              SectionHeading(L(context, 'الأصناف', 'Items'), count: _counted.length),
              DataList(children: [
                for (final it in shown)
                  ItemRow(
                    leading: IconBox(icon: _counted.containsKey(it.id) ? Lu.packageCheck : Lu.package),
                    title: it.name,
                    subtitle: '${it.code}${it.batchTracked ? L(context, ' · بدفعات (على الويب)', ' · batches (on the web)') : ''}',
                    end: it.batchTracked
                        ? const Icon(Lu.ban, size: 16, color: Brand.mutedC)
                        : QtyStepper(
                            value: _counted[it.id] == null ? '—' : _qty(_counted[it.id]!),
                            onMinus: _counted[it.id] == null ? null : () => _step(it, -1),
                            onPlus: () => _step(it, 1),
                            onTapValue: () => _edit(it),
                          ),
                  ),
              ]),
              const SizedBox(height: 12),
              Text(L(context, 'الرصيد الدفتري لا يظهر أثناء العدّ؛ الفروقات تُعرض بعد الإرسال، وتُسوّى الأصناف المعدودة فقط.', 'The book quantity is hidden while counting; variances show after sending, and only counted items are adjusted.'), textAlign: TextAlign.center, style: T.small),
            ]),
          ),
          _BottomAction(
            label: '${L(context, 'إرسال الجرد', 'Submit count')} (${_counted.length})',
            icon: Lu.send,
            onPressed: _busy || _counted.isEmpty ? null : _submit,
          ),
        ]);
      },
    );
  }
}

/// The page's one primary action, pinned above the mode's tab bar.
class _BottomAction extends StatelessWidget {
  final String label;
  final IconData icon;
  final VoidCallback? onPressed;
  const _BottomAction({required this.label, required this.icon, this.onPressed});
  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.fromLTRB(22, 10, 22, 10),
        decoration: const BoxDecoration(color: Brand.page, border: Border(top: BorderSide(color: Brand.lineC))),
        child: SizedBox(width: double.infinity, child: FilledButton.icon(onPressed: onPressed, icon: Icon(icon, size: 16), label: Text(label))),
      );
}

class _CountResult extends StatelessWidget {
  final StoreResult result;
  final StoreWarehouse warehouse;
  const _CountResult({required this.result, required this.warehouse});

  @override
  Widget build(BuildContext context) {
    final mode = (Lu.scanBarcode, L(context, 'وضع أمين المخزن', 'Storekeeper mode'), warehouse.label);
    if (result.pendingApproval) {
      return ExecFrame(
        mode: mode,
        child: ExecBody(children: [
          BackLink(L(context, 'الجرد', 'Count')),
          EmptyNote(icon: Lu.hourglass, title: L(context, 'أُرسل للاعتماد', 'Sent for approval'), text: L(context, 'قيمة الفروقات تتجاوز حدّ الاعتماد في الشركة؛ سيُرحَّل الجرد بعد موافقة مدير المخزون.', "The variance value exceeds the company's approval limit; the count posts once an inventory manager approves it.")),
        ]),
      );
    }
    final v = result.totalAdjValue;
    return ExecFrame(
      mode: mode,
      child: ExecBody(children: [
        BackLink(L(context, 'الجرد', 'Count')),
        PageHead(L(context, 'نتيجة الجرد', 'Count result'), eyebrow: result.docNo ?? ''),
        FinancialCard(
          label: L(context, 'قيمة التسوية', 'Adjustment value'),
          icon: Lu.scale,
          amount: Amount(v.toStringAsFixed(3), ''),
          note: v == 0 ? L(context, 'لا فروقات — المعدود يطابق الدفتري', 'No variance — the count matches the book') : warehouse.label,
          footer: [
            (L(context, 'أصناف معدودة', 'Items counted'), '${result.lines.length}', ''),
            (L(context, 'بفروقات', 'With variance'), '${result.lines.where((l) => l.diff != 0).length}', ''),
          ],
        ),
        SectionHeading(L(context, 'الفروقات', 'Variances')),
        DataList(children: [
          for (final l in result.lines)
            ItemRow(
              leading: IconBox(icon: l.diff == 0 ? Lu.circleCheck : Lu.circleAlert, fg: l.diff == 0 ? Brand.support : Brand.badC),
              title: l.name,
              subtitle: '${L(context, 'الدفتري', 'Book')} ${_qty(l.book)} · ${L(context, 'المعدود', 'Counted')} ${_qty(l.counted)}',
              end: l.diff == 0 ? Tag.ok(L(context, 'مطابق', 'Matches')) : Tag.bad(l.diff > 0 ? '+${_qty(l.diff)}' : _qty(l.diff)),
            ),
        ]),
      ]),
    );
  }
}

// ------------------------------------------------------------------------------------------------ transfer
class _TransferView extends StatefulWidget {
  final StoreWarehouse from;
  final Widget whField;
  final List<StoreWarehouse> targets;
  const _TransferView({super.key, required this.from, required this.whField, required this.targets});
  @override
  State<_TransferView> createState() => _TransferViewState();
}

class _TransferViewState extends State<_TransferView> {
  StoreWarehouse? _to;
  final Map<int, (InventoryItem, double)> _lines = {};
  final _q = TextEditingController();
  List<InventoryItem> _found = [];
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

  void _minus(int id) => setState(() {
        final e = _lines[id]!;
        final q = e.$2 - 1;
        q <= 0 ? _lines.remove(id) : _lines[id] = (e.$1, q);
      });

  Future<void> _pickTo() async {
    final id = await pickFromSheet<int>(context, title: L(context, 'إلى المستودع', 'To warehouse'), selected: _to?.id, options: [for (final w in widget.targets) (w.id, w.name, w.code)]);
    if (id != null) setState(() => _to = widget.targets.firstWhere((w) => w.id == id));
  }

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
    _toast(context, msg);
    if (r.ok) setState(() => _lines.clear());
  }

  @override
  Widget build(BuildContext context) {
    final head = [PageHead(L(context, 'تحويل مخزني', 'Stock transfer'), eyebrow: '${L(context, 'من', 'From')} ${widget.from.label}'), widget.whField];
    if (widget.targets.isEmpty) {
      return ExecBody(children: [...head, EmptyNote(icon: Lu.warehouse, title: L(context, 'لا مستودع آخر', 'No other warehouse'), text: L(context, 'لا يوجد مستودع آخر نشط للتحويل إليه.', 'There is no other active warehouse to transfer to.'))]);
    }
    return Column(children: [
      Expanded(
        child: ExecBody(children: [
          ...head,
          FieldBox(
            label: L(context, 'إلى', 'To'),
            onTap: _pickTo,
            child: Row(children: [
              Expanded(child: Text(_to?.label ?? L(context, 'اختر المستودع', 'Choose the warehouse'), overflow: TextOverflow.ellipsis, style: _to == null ? T.small : null)),
              const Icon(Lu.chevronDown, size: 15, color: Brand.support),
            ]),
          ),
          ScanBox(
            text: L(context, 'امسح الأصناف المحوّلة', 'Scan the items to transfer'),
            hint: L(context, 'أو ابحث بالكود أو الاسم ثم Enter', 'or search by code or name, then Enter'),
            controller: _q,
            onSubmitted: _search,
          ),
          if (_found.isNotEmpty) ...[
            const SizedBox(height: 8),
            DataList(children: [
              for (final it in _found)
                ItemRow(
                  leading: const IconBox(icon: Lu.plus),
                  title: it.name,
                  subtitle: '${it.code} · ${L(context, 'الرصيد الكلي ', 'total on hand ')}${_qty(it.onHand)}',
                  onTap: () { _add(it); setState(() { _found = []; _q.clear(); }); },
                ),
            ]),
          ],
          SectionHeading(L(context, 'الأصناف', 'Items'), count: _lines.length),
          if (_lines.isEmpty)
            EmptyNote(icon: Lu.package, title: L(context, 'لا أصناف بعد', 'No items yet'), text: L(context, 'امسح صنفاً أو ابحث عنه ليُضاف للتحويل.', 'Scan or search an item to add it to the transfer.'))
          else
            DataList(children: [
              for (final e in _lines.entries)
                ItemRow(
                  leading: const IconBox(icon: Lu.package),
                  title: e.value.$1.name,
                  subtitle: e.value.$1.code,
                  end: QtyStepper(value: _qty(e.value.$2), onMinus: () => _minus(e.key), onPlus: () => _add(e.value.$1)),
                ),
            ]),
        ]),
      ),
      _BottomAction(
        label: '${L(context, 'ترحيل التحويل', 'Post transfer')} (${_lines.length})',
        icon: Lu.arrowLeftRight,
        onPressed: _busy || _lines.isEmpty || _to == null ? null : _submit,
      ),
    ]);
  }
}
