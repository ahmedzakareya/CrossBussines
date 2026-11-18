import 'package:flutter/material.dart';

import '../app_config.dart';
import '../models/inventory_item.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';

String _qty(double v) => v == v.roundToDouble() ? v.toInt().toString() : v.toStringAsFixed(2);
String _img(String? img) => (img == null || img.isEmpty) ? '' : (img.startsWith('http') ? img : '${AppConfig.mediaBaseUrl}$img');

/// The item's photo in the reference's soft square, or the package glyph when it has none.
class _Thumb extends StatelessWidget {
  final String url;
  final double size;
  const _Thumb(this.url, {this.size = 38});
  @override
  Widget build(BuildContext context) {
    final fallback = Container(width: size, height: size, alignment: Alignment.center, color: Brand.soft, child: Icon(Lu.package, size: size * .45, color: Brand.support));
    return ClipRRect(
      borderRadius: BorderRadius.circular(size * .28),
      child: url.isEmpty ? fallback : Image.network(url, width: size, height: size, fit: BoxFit.cover, errorBuilder: (_, __, ___) => fallback),
    );
  }
}

/// INVENTORY (the owner's reference): search or scan an item, see it on hand, open it for the stock in each warehouse.
/// Money is shown in the company's currency when the user may see the finance summary; otherwise without a unit.
class InventoryScreen extends StatefulWidget {
  const InventoryScreen({super.key});
  @override
  State<InventoryScreen> createState() => _InventoryScreenState();
}

class _InventoryScreenState extends State<InventoryScreen> {
  final _search = TextEditingController();
  late Future<List<InventoryItem>> _future = ApiService.instance.getInventoryItems();
  String _cur = '';

  @override
  void initState() {
    super.initState();
    AppNav.dataChanged.addListener(_reload);
    ApiService.instance.tryFinanceSummary().then((f) { if (mounted && f != null) setState(() => _cur = f.currency); });
  }

  @override
  void dispose() {
    AppNav.dataChanged.removeListener(_reload);
    _search.dispose();
    super.dispose();
  }

  void _reload() {
    if (mounted) setState(() => _future = ApiService.instance.getInventoryItems(q: _search.text.trim()));
  }

  @override
  Widget build(BuildContext context) {
    return ExecFrame(
      child: FutureBuilder<List<InventoryItem>>(
        future: _future,
        builder: (context, s) {
          final head = [
            if (Navigator.of(context).canPop()) BackLink(L(context, 'مساحات العمل', 'Workspaces')),
            PageHead(L(context, 'المخزون', 'Inventory'), eyebrow: L(context, 'الأصناف والمستودعات', 'Items and warehouses')),
            TextField(
              controller: _search, textInputAction: TextInputAction.search, onSubmitted: (_) => _reload(),
              decoration: InputDecoration(prefixIcon: const Icon(Lu.scanBarcode, size: 17), hintText: L(context, 'ابحث أو امسح الباركود', 'Search or scan a barcode')),
            ),
            const SizedBox(height: 14),
          ];
          if (s.hasError) return ExecBody(children: [...head, EmptyNote(icon: Lu.lock, title: L(context, 'تعذّر التحميل', 'Could not load'), text: L(context, 'لا تملك عرض المخزون، أو أن الاتصال انقطع.', 'You may not view inventory, or the connection dropped.'))]);
          if (!s.hasData) return ExecBody(children: [...head, const Padding(padding: EdgeInsets.all(24), child: Center(child: CircularProgressIndicator()))]);
          final items = s.data!;
          return ExecBody(
            onRefresh: () async => _reload(),
            children: [
              ...head,
              if (items.isEmpty)
                EmptyNote(icon: Lu.package, title: L(context, 'لا أصناف', 'No items'), text: L(context, 'غيّر كلمة البحث.', 'Try another search.'))
              else
                DataList(children: [
                  for (final it in items)
                    ItemRow(
                      leading: _Thumb(_img(it.image)),
                      title: it.name,
                      subtitle: [it.code, if (it.salesPrice > 0) '${money(it.salesPrice, _cur)} ${curLabel(_cur, isAr(context))}'.trim()].join(' · '),
                      end: it.onHand <= 0 ? Tag.bad(L(context, 'نفد', 'Out')) : Text(_qty(it.onHand), textDirection: TextDirection.ltr, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: Brand.inkC)),
                      onTap: (it.barcode ?? '').isEmpty && it.code.isEmpty ? null : () => Navigator.of(context).push(MaterialPageRoute(builder: (_) => ItemDetailScreen(code: (it.barcode ?? '').isNotEmpty ? it.barcode! : it.code, currency: _cur))),
                    ),
                ]),
            ],
          );
        },
      ),
    );
  }
}

/// One item: on hand and its value on the financial card, then the stock in each warehouse at its average cost.
class ItemDetailScreen extends StatelessWidget {
  final String code, currency;
  const ItemDetailScreen({super.key, required this.code, this.currency = ''});

  @override
  Widget build(BuildContext context) {
    final unit = curLabel(currency, isAr(context));
    return ExecFrame(
      child: FutureBuilder<ScanResult>(
        future: ApiService.instance.scanInventory(code),
        builder: (context, s) {
          final back = BackLink(L(context, 'المخزون', 'Inventory'));
          if (s.hasError || (s.hasData && !s.data!.ok)) return ExecBody(children: [back, EmptyNote(icon: Lu.packageX, title: L(context, 'الصنف غير موجود', 'Item not found'), text: L(context, 'لم يُعثر على صنف بهذا الكود.', 'No item has this code.'))]);
          if (!s.hasData) return const Center(child: CircularProgressIndicator());
          final d = s.data!;
          return ExecBody(children: [
            back,
            Row(children: [
              _Thumb(_img(d.image), size: 56),
              const SizedBox(width: 12),
              Expanded(child: PageHead(d.name, eyebrow: d.code)),
            ]),
            if (d.isComposite) ...[
              Align(alignment: AlignmentDirectional.centerStart, child: Tag.info(d.compositeType == 'Bundle' ? L(context, 'حزمة بيع', 'Bundle') : L(context, 'تجميع', 'Assembly'))),
              const SizedBox(height: 12),
            ],
            FinancialCard(
              label: L(context, 'الرصيد الكلي', 'On hand'),
              icon: Lu.package,
              amount: Amount(_qty(d.totalOnHand), d.units.isEmpty ? '' : d.units.first),
              footer: [
                (L(context, 'القيمة', 'Value'), whole(d.totalValue), unit),
                (L(context, 'سعر البيع', 'Sale price'), money(d.salesPrice, currency), unit),
              ],
            ),
            if (d.components.isNotEmpty) ...[
              SectionHeading(L(context, 'المكوّنات', 'Components')),
              DataList(children: [for (final c in d.components) ItemRow(leading: const IconBox(icon: Lu.puzzle), title: c)]),
            ],
            SectionHeading(L(context, 'حسب المستودع', 'By warehouse'), count: d.byWarehouse.length),
            if (d.byWarehouse.isEmpty)
              EmptyNote(icon: Lu.warehouse, title: L(context, 'لا رصيد', 'No stock'), text: L(context, 'لا يوجد رصيد لهذا الصنف في أي مستودع.', 'This item has no stock in any warehouse.'))
            else
              DataList(children: [
                for (final w in d.byWarehouse)
                  ItemRow(
                    leading: const IconBox(icon: Lu.warehouse),
                    title: w.warehouse,
                    subtitle: '${L(context, 'متوسط التكلفة', 'Average cost')} ${money(w.avgCost, currency)} $unit'.trim(),
                    end: Text(_qty(w.qty), textDirection: TextDirection.ltr, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: Brand.inkC)),
                  ),
              ]),
          ]);
        },
      ),
    );
  }
}
