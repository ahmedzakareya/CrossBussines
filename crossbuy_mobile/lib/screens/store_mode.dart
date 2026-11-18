import 'package:flutter/material.dart';

import '../models/inventory_item.dart';
import '../models/store.dart';
import '../services/api_service.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';

/// STOREKEEPER MODE (the owner's reference, "mode-store"): the navy band turns into the mode bar — the mode, the
/// warehouse, and "exit mode" — and the bottom bar holds the mode's own tabs: a blind stock count and a warehouse
/// transfer. Both post through /api/store under the same permission and approval threshold as the web screens.
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
        // a keeper of several warehouses switches by tapping the page's warehouse line (the reference keeps no field)
        final VoidCallback? switchWarehouse = mine.length < 2
            ? null
            : () async {
                final id = await pickFromSheet<int>(context, title: L(context, 'المستودع', 'Warehouse'), selected: _wh!.id, options: [for (final w in mine) (w.id, w.name, w.code)]);
                if (id != null) setState(() => _wh = mine.firstWhere((w) => w.id == id));
              };
        return ExecFrame(
          mode: mode(_wh!.label),
          bottomBar: ExecNav(
            items: [(Lu.scanBarcode, L(context, 'جرد', 'Count')), (Lu.arrowLeftRight, L(context, 'تحويل', 'Transfer')), (Lu.packageCheck, L(context, 'استلام', 'Receive'))],
            index: _tab,
            onTap: (i) => setState(() => _tab = i),
          ),
          child: switch (_tab) {
            0 => _CountView(key: ValueKey('count-${_wh!.id}'), warehouse: _wh!, onSwitch: switchWarehouse),
            1 => _TransferView(key: ValueKey('transfer-${_wh!.id}'), from: _wh!, onSwitch: switchWarehouse, targets: targets.where((t) => t.id != _wh!.id).toList()),
            _ => _ReceiveView(key: ValueKey('receive-${_wh!.id}'), warehouse: _wh!, onSwitch: switchWarehouse),
          },
        );
      },
    );
  }
}

String _qty(double v) => v == v.roundToDouble() ? v.toInt().toString() : v.toStringAsFixed(3);

void _toast(BuildContext context, String m) =>
    ScaffoldMessenger.of(context)..hideCurrentSnackBar()..showSnackBar(SnackBar(content: Text(m), duration: const Duration(milliseconds: 1400)));

/// The page heading; its warehouse line opens the warehouse switch when there is more than one.
Widget _head(BuildContext context, String title, String eyebrow, VoidCallback? onSwitch) => GestureDetector(
      onTap: onSwitch,
      child: PageHead(title, eyebrow: onSwitch == null ? eyebrow : '$eyebrow  ▾'),
    );

// ------------------------------------------------------------------------------------------------ blind count
class _CountView extends StatefulWidget {
  final StoreWarehouse warehouse;
  final VoidCallback? onSwitch;
  const _CountView({super.key, required this.warehouse, this.onSwitch});
  @override
  State<_CountView> createState() => _CountViewState();
}

class _CountViewState extends State<_CountView> {
  late Future<List<CountSheetItem>> _sheet = ApiService.instance.getCountSheet(widget.warehouse.id);
  final Map<int, double> _counted = {};
  final List<int> _order = [];   // counted items, the latest scan on top
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

  void _bump(CountSheetItem it, double by) => setState(() {
        final q = (_counted[it.id] ?? 0) + by;
        if (q <= 0) {
          _counted.remove(it.id);
          _order.remove(it.id);
        } else {
          _counted[it.id] = q;
          _order..remove(it.id)..insert(0, it.id);
        }
      });

  void _onScan(List<CountSheetItem> items, String code) {
    final hit = items.where((i) => i.matches(code)).toList();
    _scan.clear();
    _scanFocus.requestFocus();
    if (hit.isEmpty) {
      setState(() => _filter = code.trim());   // not a code: search by name below
      return;
    }
    final it = hit.first;
    if (it.batchTracked) return _toast(context, L(context, 'صنف بدفعات: يُجرد على الويب لكل دفعة.', 'Batch item: counted per batch on the web.'));
    _filter = '';
    _bump(it, 1);
  }

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
    setState(() {
      if (v <= 0) {
        _counted.remove(it.id);
        _order.remove(it.id);
      } else {
        _counted[it.id] = v;
        if (!_order.contains(it.id)) _order.insert(0, it.id);
      }
    });
  }

  Future<void> _submit() async {
    if (_counted.isEmpty || _busy) return;
    setState(() => _busy = true);
    final r = await ApiService.instance.postCount(widget.warehouse.id, _counted, notes: 'mobile');
    if (!mounted) return;
    setState(() => _busy = false);
    if (!r.ok) return _toast(context, r.message ?? L(context, 'تعذّر إرسال الجرد', 'The count could not be sent'));
    await Navigator.of(context).push(MaterialPageRoute(builder: (_) => _CountResult(result: r, warehouse: widget.warehouse)));
    if (mounted) setState(() { _counted.clear(); _order.clear(); _sheet = ApiService.instance.getCountSheet(widget.warehouse.id); });
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<List<CountSheetItem>>(
      future: _sheet,
      builder: (context, s) {
        final head = _head(context, L(context, 'جرد المخزون', 'Stock count'), '${widget.warehouse.name} · ${L(context, 'جرد أعمى', 'blind count')}', widget.onSwitch);
        if (s.hasError) return ExecBody(children: [head, EmptyNote(icon: Lu.lock, title: L(context, 'غير مسموح', 'Not allowed'), text: L(context, 'لا تملك الجرد في هذا المستودع.', 'You may not count this warehouse.'))]);
        if (!s.hasData) return const Center(child: CircularProgressIndicator());
        final items = s.data!;
        final byId = {for (final i in items) i.id: i};
        final matches = _filter.isEmpty ? <CountSheetItem>[] : items.where((i) => i.name.contains(_filter) || i.code.toLowerCase().contains(_filter.toLowerCase())).take(8).toList();
        Widget row(CountSheetItem it) => ItemRow(
              leading: const IconBox(icon: Lu.package),
              title: it.name,
              subtitle: '${it.code}${it.batchTracked ? L(context, ' · بدفعات (على الويب)', ' · batches (on the web)') : ''}',
              end: it.batchTracked
                  ? const Icon(Lu.ban, size: 16, color: Brand.mutedC)
                  : QtyStepper(
                      value: _counted[it.id] == null ? '0' : _qty(_counted[it.id]!),
                      onMinus: _counted[it.id] == null ? null : () => _bump(it, -1),
                      onPlus: () => _bump(it, 1),
                      onTapValue: () => _edit(it),
                    ),
            );
        return ExecBody(children: [
          head,
          ScanBox(
            text: L(context, 'امسح الباركود — كل مسح يضيف قطعة', 'Scan the barcode — each scan adds one'),
            hint: L(context, 'أو اكتب كود الصنف', 'or type the item code'),
            controller: _scan, focusNode: _scanFocus, autofocus: true,
            onSubmitted: (v) => _onScan(items, v),
            onChanged: (v) { if (v.isEmpty && _filter.isNotEmpty) setState(() => _filter = ''); },
          ),
          if (_filter.isNotEmpty) ...[
            SectionHeading(L(context, 'نتائج البحث', 'Search results'), count: matches.length),
            if (matches.isEmpty)
              EmptyNote(icon: Lu.packageX, title: L(context, 'لا صنف بهذا الاسم', 'No item by that name'), text: L(context, 'جرّب الكود أو جزءاً من الاسم.', 'Try the code or part of the name.'))
            else
              DataList(children: [for (final it in matches) row(it)]),
          ],
          SectionHeading(L(context, 'تم عدّه', 'Counted'), count: _counted.length, action: L(context, 'إرسال الجرد', 'Submit count'), actionIcon: Lu.send, onAction: _counted.isEmpty || _busy ? null : _submit),
          if (_order.isEmpty)
            EmptyNote(icon: Lu.scanBarcode, title: L(context, 'لم يُعدّ شيء بعد', 'Nothing counted yet'), text: L(context, 'امسح أول صنف في الرف ليظهر هنا.', 'Scan the first item on the shelf and it shows here.'))
          else
            DataList(children: [for (final id in _order) if (byId[id] != null) row(byId[id]!)]),
          const SizedBox(height: 14),
          Text(L(context, 'الرصيد الدفتري لا يظهر أثناء العدّ؛ الفروقات تُعرض بعد الإرسال', 'The book quantity is hidden while counting; variances show after sending'), textAlign: TextAlign.center, style: T.small),
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
  final VoidCallback? onSwitch;
  final List<StoreWarehouse> targets;
  const _TransferView({super.key, required this.from, this.onSwitch, required this.targets});
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
    if (_to == null) {
      _toast(context, L(context, 'اختر المستودع المحوَّل إليه', 'Choose the warehouse to transfer to'));
      return;
    }
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
    final head = _head(context, L(context, 'تحويل مخزني', 'Stock transfer'), '${L(context, 'من', 'From')} ${widget.from.name}', widget.onSwitch);
    if (widget.targets.isEmpty) {
      return ExecBody(children: [head, EmptyNote(icon: Lu.warehouse, title: L(context, 'لا مستودع آخر', 'No other warehouse'), text: L(context, 'لا يوجد مستودع آخر نشط للتحويل إليه.', 'There is no other active warehouse to transfer to.'))]);
    }
    return ExecBody(children: [
      head,
      FieldBox(
        label: L(context, 'إلى', 'To'),
        onTap: _pickTo,
        child: Row(children: [
          Expanded(child: Text(_to?.label ?? L(context, 'اختر المستودع', 'Choose the warehouse'), overflow: TextOverflow.ellipsis, style: _to == null ? T.small : null)),
          const Icon(Lu.chevronDown, size: 15, color: Brand.support),
        ]),
      ),
      ScanBox(
        compact: true,
        text: L(context, 'امسح الأصناف المحوّلة', 'Scan the items to transfer'),
        hint: L(context, 'أو ابحث بالكود أو الاسم', 'or search by code or name'),
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
      const SizedBox(height: 14),
      FilledButton(onPressed: _busy || _lines.isEmpty ? null : _submit, child: Text(L(context, 'ترحيل التحويل', 'Post transfer'))),
    ]);
  }
}

// ------------------------------------------------------------------------------------------------ receive
/// RECEIVE (the reference's "store-receive"): the approved purchase orders awaiting goods for this warehouse; open one
/// and each line shows what is being received against what is due ("90 / 150"). A scan adds one to its line; a tap
/// sets the quantity. "Confirm receipt" posts when every line is complete, "partial receipt" posts what is there. Prices,
/// units and currency come from the order on the server. Batch / expiry / serial items are received on the web.
class _ReceiveView extends StatefulWidget {
  final StoreWarehouse warehouse;
  final VoidCallback? onSwitch;
  const _ReceiveView({super.key, required this.warehouse, this.onSwitch});
  @override
  State<_ReceiveView> createState() => _ReceiveViewState();
}

class _ReceiveViewState extends State<_ReceiveView> {
  late Future<List<OpenPurchaseOrder>?> _orders = ApiService.instance.getOpenOrders(widget.warehouse.id);
  OpenPurchaseOrder? _po;
  Future<(String, String, List<ReceiptLine>)>? _sheet;
  final Map<int, double> _got = {};
  final _scan = TextEditingController();
  bool _busy = false;

  @override
  void dispose() {
    _scan.dispose();
    super.dispose();
  }

  void _open(OpenPurchaseOrder po) => setState(() {
        _po = po;
        _got.clear();
        _sheet = ApiService.instance.getReceiptSheet(po.id, widget.warehouse.id);
      });

  void _back() => setState(() {
        _po = null;
        _sheet = null;
        _got.clear();
        _orders = ApiService.instance.getOpenOrders(widget.warehouse.id);
      });

  void _onScan(List<ReceiptLine> lines, String code) {
    _scan.clear();
    final hit = lines.where((l) => l.matches(code)).toList();
    if (hit.isEmpty) return _toast(context, L(context, 'الصنف ليس في هذا الأمر', 'That item is not on this order'));
    final l = hit.firstWhere((x) => (_got[x.lineId] ?? 0) < x.remaining, orElse: () => hit.first);
    if (l.needsDetails) return _toast(context, L(context, 'صنف بدفعات أو صلاحية: يُستلم على الويب.', 'Batch / expiry item: received on the web.'));
    setState(() => _got[l.lineId] = ((_got[l.lineId] ?? 0) + 1).clamp(0, l.remaining).toDouble());
  }

  Future<void> _edit(ReceiptLine l) async {
    if (l.needsDetails) return _toast(context, L(context, 'صنف بدفعات أو صلاحية: يُستلم على الويب.', 'Batch / expiry item: received on the web.'));
    final c = TextEditingController(text: _got[l.lineId] == null ? '' : _qty(_got[l.lineId]!));
    final v = await showDialog<double?>(
      context: context,
      builder: (d) => AlertDialog(
        title: Text(l.name, style: T.h2),
        content: TextField(controller: c, autofocus: true, keyboardType: const TextInputType.numberWithOptions(decimal: true), decoration: InputDecoration(labelText: L(context, 'الكمية المستلمة', 'Quantity received'), helperText: L(context, 'المطلوب ${_qty(l.remaining)}', '${_qty(l.remaining)} due'))),
        actions: [
          TextButton(onPressed: () => Navigator.pop(d, l.remaining), child: Text(L(context, 'كامل المطلوب', 'All due'))),
          FilledButton(onPressed: () => Navigator.pop(d, double.tryParse(c.text.trim())), child: Text(L(context, 'حفظ', 'Save'))),
        ],
      ),
    );
    if (v == null) return;
    setState(() => v <= 0 ? _got.remove(l.lineId) : _got[l.lineId] = v.clamp(0, l.remaining).toDouble());
  }

  Future<void> _post() async {
    if (_po == null || _got.isEmpty || _busy) return;
    setState(() => _busy = true);
    final (ok, msg) = await ApiService.instance.postReceipt(_po!.id, widget.warehouse.id, Map.of(_got));
    if (!mounted) return;
    setState(() => _busy = false);
    _toast(context, ok ? '${L(context, 'رُحّل الاستلام ', 'Receipt posted ')}${msg ?? ''}' : (msg ?? L(context, 'تعذّر الاستلام', 'The receipt could not be posted')));
    if (ok) _back();
  }

  @override
  Widget build(BuildContext context) {
    if (_po == null) {
      return FutureBuilder<List<OpenPurchaseOrder>?>(
        future: _orders,
        builder: (context, s) {
          final head = _head(context, L(context, 'استلام بضاعة', 'Receive goods'), '${L(context, 'إلى', 'Into')} ${widget.warehouse.name}', widget.onSwitch);
          if (s.hasError) return ExecBody(children: [head, EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر التحميل', 'Could not load'), text: L(context, 'تحقق من الاتصال ثم أعد المحاولة.', 'Check the connection and try again.'))]);
          if (s.connectionState != ConnectionState.done) return const Center(child: CircularProgressIndicator());
          final list = s.data;
          if (list == null) return ExecBody(children: [head, EmptyNote(icon: Lu.lock, title: L(context, 'الاستلام غير مسموح', 'Receiving is not allowed'), text: L(context, 'استلام البضاعة لمن يملك صلاحية المشتريات في المخزون.', 'Receiving goods needs the inventory purchasing permission.'))]);
          return ExecBody(onRefresh: () async => _back(), children: [
            head,
            if (list.isEmpty)
              EmptyNote(icon: Lu.packageCheck, title: L(context, 'لا أوامر بانتظار الاستلام', 'No orders awaiting goods'), text: L(context, 'تظهر هنا أوامر الشراء المعتمدة لهذا المستودع.', 'Approved purchase orders for this warehouse show here.'))
            else
              DataList(children: [
                for (final po in list)
                  ItemRow(
                    leading: const IconBox(icon: Lu.clipboardList),
                    title: '${po.orderNo} · ${po.vendor}',
                    subtitle: [
                      L(context, po.lines == 1 ? 'بند واحد' : po.lines == 2 ? 'بندان' : po.lines <= 10 ? '${po.lines} بنود' : '${po.lines} بنداً', po.lines == 1 ? '1 line' : '${po.lines} lines'),
                      if (po.expectedDate != null) '${L(context, 'متوقع', 'Due')} ${dayMonth(context, po.expectedDate!)}',
                    ].join(' · '),
                    end: po.partly ? Tag(L(context, 'استلام جزئي', 'Partly received')) : null,
                    onTap: () => _open(po),
                  ),
              ]),
          ]);
        },
      );
    }
    return FutureBuilder<(String, String, List<ReceiptLine>)>(
      future: _sheet,
      builder: (context, s) {
        final head = [
          BackLink(L(context, 'أوامر الشراء', 'Purchase orders'), onTap: _back),
          PageHead(L(context, 'استلام بضاعة', 'Receive goods'), eyebrow: '${L(context, 'أمر شراء', 'Purchase order')} ${_po!.orderNo}'),
        ];
        if (s.hasError) return ExecBody(children: [...head, EmptyNote(icon: Lu.lock, title: L(context, 'الأمر غير متاح', 'Order not available'), text: L(context, 'لم يعد هذا الأمر مفتوحاً لهذا المستودع.', 'This order is no longer open for this warehouse.'))]);
        if (!s.hasData) return const Center(child: CircularProgressIndicator());
        final (_, vendor, lines) = s.data!;
        final open = lines.where((l) => !l.needsDetails).toList();
        final complete = open.isNotEmpty && open.every((l) => (_got[l.lineId] ?? 0) >= l.remaining);
        return ExecBody(children: [
          ...head,
          TextField(
            controller: _scan, autofocus: true, textInputAction: TextInputAction.done,
            onSubmitted: (v) => _onScan(lines, v),
            decoration: InputDecoration(prefixIcon: const Icon(Lu.scanBarcode, size: 17), hintText: L(context, 'امسح الصنف المستلم — كل مسح قطعة', 'Scan each item received — one per scan')),
          ),
          const SizedBox(height: 12),
          DataList(children: [
            for (final l in lines)
              ItemRow(
                leading: IconBox(icon: (_got[l.lineId] ?? 0) >= l.remaining ? Lu.packageCheck : Lu.package),
                title: l.name,
                subtitle: l.needsDetails ? L(context, 'بدفعات أو صلاحية — يُستلم على الويب', 'Batch / expiry — received on the web') : '${L(context, 'المطلوب', 'Due')} ${_qty(l.remaining)}',
                end: l.needsDetails
                    ? const Icon(Lu.ban, size: 16, color: Brand.mutedC)
                    : ((_got[l.lineId] ?? 0) >= l.remaining ? Tag.ok : Tag.new)('${_qty(_got[l.lineId] ?? 0)} / ${_qty(l.remaining)}'),
                onTap: () => _edit(l),
              ),
          ]),
          if (vendor.isNotEmpty) ...[const SizedBox(height: 10), Text('${L(context, 'المورد', 'Vendor')}: $vendor', textAlign: TextAlign.center, style: T.small)],
          const SizedBox(height: 14),
          Row(children: [
            Expanded(child: FilledButton(onPressed: _busy || !complete ? null : _post, child: Text(L(context, 'تأكيد الاستلام', 'Confirm receipt')))),
            const SizedBox(width: 9),
            Expanded(child: OutlinedButton(onPressed: _busy || _got.isEmpty || complete ? null : _post, child: Text(L(context, 'استلام جزئي', 'Partial receipt')))),
          ]),
        ]);
      },
    );
  }
}
