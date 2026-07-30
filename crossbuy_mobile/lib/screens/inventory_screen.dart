import 'package:flutter/material.dart';

import '../app_config.dart';
import '../l10n/app_localizations.dart';
import '../models/inventory_item.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../widgets/app_ui.dart';

class InventoryScreen extends StatefulWidget {
  const InventoryScreen({super.key});
  @override
  State<InventoryScreen> createState() => _InventoryScreenState();
}

class _InventoryScreenState extends State<InventoryScreen> {
  final _search = TextEditingController();
  late Future<List<InventoryItem>> _future;

  @override
  void initState() {
    super.initState();
    _future = ApiService.instance.getInventoryItems();
    AppNav.dataChanged.addListener(_reload);
  }

  void _reload() {
    if (mounted) setState(() => _future = ApiService.instance.getInventoryItems(q: _search.text.trim()));
  }

  @override
  void dispose() {
    AppNav.dataChanged.removeListener(_reload);
    _search.dispose();
    super.dispose();
  }

  String _img(String? img) =>
      (img == null || img.isEmpty) ? '' : (img.startsWith('http') ? img : '${AppConfig.mediaBaseUrl}$img');

  String _qty(double v) => v.toStringAsFixed(v == v.roundToDouble() ? 0 : 2);
  String _money(double v) => v.toStringAsFixed(2).replaceAllMapped(RegExp(r'\B(?=(\d{3})+(?!\d))'), (m) => ',');

  void _runSearch() => setState(() => _future = ApiService.instance.getInventoryItems(q: _search.text.trim()));

  Future<void> _showDetail(String barcode) async {
    final ar = AppLocalizations.of(context).isAr;
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.white,
      shape: const RoundedRectangleBorder(borderRadius: BorderRadius.vertical(top: Radius.circular(20))),
      builder: (ctx) => FutureBuilder<ScanResult>(
        future: ApiService.instance.scanInventory(barcode),
        builder: (ctx, snap) {
          if (!snap.hasData) return const SizedBox(height: 240, child: Center(child: CircularProgressIndicator()));
          final d = snap.data!;
          if (!d.ok) return SizedBox(height: 160, child: Center(child: Text(ar ? 'الصنف غير موجود' : 'Item not found')));
          return SingleChildScrollView(
            padding: const EdgeInsets.fromLTRB(18, 18, 18, 28),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisSize: MainAxisSize.min, children: [
              Row(children: [
                _thumb(_img(d.image), 64),
                const SizedBox(width: 14),
                Expanded(
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text(d.name, style: const TextStyle(fontSize: 17, fontWeight: FontWeight.bold, color: kInk)),
                    const SizedBox(height: 3),
                    Text(d.code, style: const TextStyle(color: kMuted, fontSize: 12.5)),
                    if (d.isComposite)
                      Padding(padding: const EdgeInsets.only(top: 5), child: _chip(
                        d.compositeType == 'Bundle' ? (ar ? 'حزمة بيع' : 'Bundle') : (ar ? 'تجميع' : 'Assembly'),
                        const Color(0xFFFFF4D6), const Color(0xFF9A6B00))),
                  ]),
                ),
              ]),
              const SizedBox(height: 16),
              Row(children: [
                _stat(ar ? 'الرصيد الكلي' : 'On hand', _qty(d.totalOnHand), kPrimary),
                _stat(ar ? 'القيمة' : 'Value', _money(d.totalValue), kGreen),
                _stat(ar ? 'سعر البيع' : 'Sale price', _money(d.salesPrice), kInk),
              ]),
              if (d.components.isNotEmpty) ...[
                const SizedBox(height: 16),
                Text(ar ? 'المكوّنات' : 'Components', style: const TextStyle(fontWeight: FontWeight.bold, color: kInk)),
                const SizedBox(height: 6),
                ...d.components.map((c) => Padding(padding: const EdgeInsets.symmetric(vertical: 2),
                    child: Row(children: [const Icon(Icons.circle, size: 6, color: kMuted), const SizedBox(width: 8), Expanded(child: Text(c, style: const TextStyle(color: kInk)))]))),
              ],
              const SizedBox(height: 16),
              Text(ar ? 'الرصيد حسب المخزن' : 'On hand by warehouse', style: const TextStyle(fontWeight: FontWeight.bold, color: kInk)),
              const SizedBox(height: 6),
              if (d.byWarehouse.isEmpty)
                Text(ar ? 'لا يوجد رصيد' : 'No stock', style: const TextStyle(color: kMuted))
              else
                ...d.byWarehouse.map((w) => Container(
                      margin: const EdgeInsets.symmetric(vertical: 4),
                      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
                      decoration: BoxDecoration(color: kChipBg, borderRadius: BorderRadius.circular(12)),
                      child: Row(children: [
                        const Icon(Icons.warehouse_outlined, size: 18, color: kPrimary),
                        const SizedBox(width: 10),
                        Expanded(child: Text(w.warehouse, style: const TextStyle(fontWeight: FontWeight.w600, color: kInk))),
                        Text(_qty(w.qty), style: const TextStyle(fontWeight: FontWeight.bold, color: kInk)),
                        const SizedBox(width: 10),
                        Text('@ ${_money(w.avgCost)}', style: const TextStyle(color: kMuted, fontSize: 12)),
                      ]),
                    )),
            ]),
          );
        },
      ),
    );
  }

  Widget _stat(String label, String value, Color color) => Expanded(
        child: Container(
          margin: const EdgeInsets.symmetric(horizontal: 3),
          padding: const EdgeInsets.symmetric(vertical: 12),
          decoration: BoxDecoration(color: kChipBg, borderRadius: BorderRadius.circular(12)),
          child: Column(children: [
            Text(value, style: TextStyle(fontWeight: FontWeight.bold, fontSize: 15, color: color)),
            const SizedBox(height: 3),
            Text(label, style: const TextStyle(color: kMuted, fontSize: 11)),
          ]),
        ),
      );

  Widget _chip(String text, Color bg, Color fg) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 3),
        decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(20)),
        child: Text(text, style: TextStyle(color: fg, fontSize: 11, fontWeight: FontWeight.bold)),
      );

  Widget _thumb(String url, double size) => ClipRRect(
        borderRadius: BorderRadius.circular(12),
        child: url.isEmpty
            ? Container(width: size, height: size, color: kChipBg, child: const Icon(Icons.inventory_2_outlined, color: kMuted))
            : Image.network(url, width: size, height: size, fit: BoxFit.cover,
                errorBuilder: (_, __, ___) => Container(width: size, height: size, color: kChipBg, child: const Icon(Icons.inventory_2_outlined, color: kMuted))),
      );

  @override
  Widget build(BuildContext context) {
    final t = AppLocalizations.of(context);
    final ar = t.isAr;
    return Container(
      color: kBg,
      child: Column(children: [
        AppHeader(title: t.t('inventory')),
        Padding(
          padding: const EdgeInsets.fromLTRB(14, 14, 14, 6),
          child: TextField(
            controller: _search,
            textInputAction: TextInputAction.search,
            onSubmitted: (_) => _runSearch(),
            decoration: InputDecoration(
              hintText: ar ? 'بحث / امسح الباركود' : 'Search / scan barcode',
              prefixIcon: const Icon(Icons.qr_code_scanner, color: kPrimary),
              suffixIcon: IconButton(icon: const Icon(Icons.search), onPressed: _runSearch),
              filled: true, fillColor: Colors.white,
              border: OutlineInputBorder(borderRadius: BorderRadius.circular(14), borderSide: BorderSide.none),
              contentPadding: const EdgeInsets.symmetric(horizontal: 14),
            ),
          ),
        ),
        Expanded(
          child: FutureBuilder<List<InventoryItem>>(
            future: _future,
            builder: (context, snap) {
              if (snap.connectionState == ConnectionState.waiting) return const Center(child: CircularProgressIndicator());
              if (snap.hasError) return Center(child: Text(ar ? 'تعذّر التحميل' : 'Failed to load', style: const TextStyle(color: kMuted)));
              final items = snap.data ?? [];
              if (items.isEmpty) return Center(child: Text(ar ? 'لا توجد أصناف' : 'No items', style: const TextStyle(color: kMuted)));
              return ListView.separated(
                padding: const EdgeInsets.fromLTRB(14, 6, 14, 20),
                itemCount: items.length,
                separatorBuilder: (_, __) => const SizedBox(height: 8),
                itemBuilder: (context, i) {
                  final it = items[i];
                  final low = it.onHand <= 0;
                  return InkWell(
                    onTap: () => it.barcode != null ? _showDetail(it.barcode!) : null,
                    borderRadius: BorderRadius.circular(14),
                    child: Container(
                      padding: const EdgeInsets.all(10),
                      decoration: BoxDecoration(color: Colors.white, borderRadius: BorderRadius.circular(14),
                          boxShadow: [BoxShadow(color: Colors.black.withValues(alpha: 0.04), blurRadius: 10, offset: const Offset(0, 3))]),
                      child: Row(children: [
                        _thumb(_img(it.image), 52),
                        const SizedBox(width: 12),
                        Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                          Text(it.name, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontWeight: FontWeight.bold, color: kInk)),
                          const SizedBox(height: 3),
                          Text('${it.code}${it.salesPrice > 0 ? '  ·  ${_money(it.salesPrice)}' : ''}', style: const TextStyle(color: kMuted, fontSize: 12)),
                        ])),
                        Column(crossAxisAlignment: CrossAxisAlignment.end, children: [
                          Container(
                            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
                            decoration: BoxDecoration(color: low ? const Color(0xFFFDE7EA) : const Color(0xFFE8F8EF), borderRadius: BorderRadius.circular(20)),
                            child: Text(_qty(it.onHand), style: TextStyle(fontWeight: FontWeight.bold, color: low ? const Color(0xFFD9214E) : const Color(0xFF0FA958)))),
                          const SizedBox(height: 4),
                          Text(ar ? 'الرصيد' : 'on hand', style: const TextStyle(color: kMuted, fontSize: 10)),
                        ]),
                      ]),
                    ),
                  );
                },
              );
            },
          ),
        ),
      ]),
    );
  }
}
