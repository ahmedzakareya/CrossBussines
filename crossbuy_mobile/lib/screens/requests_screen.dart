import 'package:flutter/material.dart';

import '../models/ess.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';
import 'inbox_tab.dart' show requestTitle, requestPill;

/// MY REQUESTS — letters, permissions and advances with their status; and the form to raise one. The server routes it
/// through the company's approval path and checks the advance against the legal cap; its refusal is shown as is.
class RequestsScreen extends StatefulWidget {
  const RequestsScreen({super.key});
  @override
  State<RequestsScreen> createState() => _RequestsScreenState();
}

class _RequestsScreenState extends State<RequestsScreen> {
  late Future<List<MyRequest>> _future = ApiService.instance.getMyRequests();

  @override
  Widget build(BuildContext context) => PageFrame(
        title: L(context, 'طلباتي', 'My requests'),
        actions: [
          IconButton(
            onPressed: () async {
              await Navigator.of(context).push(MaterialPageRoute(builder: (_) => const NewRequestScreen()));
              if (mounted) setState(() => _future = ApiService.instance.getMyRequests());
            },
            icon: const Icon(Icons.add),
          ),
        ],
        child: FutureBuilder<List<MyRequest>>(
          future: _future,
          builder: (context, s) {
            if (!s.hasData) return const Center(child: CircularProgressIndicator());
            if (s.data!.isEmpty) return Padding(padding: const EdgeInsets.all(16), child: SectionCard(child: EmptyNote(icon: Icons.description_outlined, title: L(context, 'لا طلبات', 'No requests'), text: L(context, 'اضغط + لطلب خطاب أو إذن أو سلفة.', 'Tap + for a letter, a permission or an advance.'))));
            return ListView(padding: const EdgeInsets.all(16), children: [
              SectionCard(
                padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
                child: Column(children: [
                  for (final r in s.data!)
                    RowItem(
                      leading: IconTile(Icons.description_outlined, fg: r.status == 1 ? Brand.ok : Brand.brand, bg: r.status == 1 ? Brand.okTint : Brand.tint2),
                      title: requestTitle(context, r),
                      subtitle: r.createdAt != null ? ymd(r.createdAt!) : null,
                      trailing: requestPill(context, r.status),
                    ),
                ]),
              ),
            ]);
          },
        ),
      );
}

class NewRequestScreen extends StatefulWidget {
  const NewRequestScreen({super.key});
  @override
  State<NewRequestScreen> createState() => _NewRequestScreenState();
}

class _NewRequestScreenState extends State<NewRequestScreen> {
  String _type = 'Letter';
  String _letter = 'Salary';
  final _addressee = TextEditingController();
  final _amount = TextEditingController();
  final _instalment = TextEditingController();
  final _reason = TextEditingController();
  DateTime _date = DateTime.now();
  TimeOfDay _from = const TimeOfDay(hour: 10, minute: 0), _to = const TimeOfDay(hour: 12, minute: 0);
  bool _busy = false;

  String _t(TimeOfDay t) => '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';

  Future<void> _send() async {
    setState(() => _busy = true);
    final body = <String, dynamic>{'requestType': _type, 'reason': _reason.text.trim()};
    if (_type == 'Letter') body.addAll({'letterType': _letter, 'addressee': _addressee.text.trim()});
    if (_type == 'Permission') body.addAll({'permissionDate': ymd(_date), 'fromTime': _t(_from), 'toTime': _t(_to)});
    if (_type == 'Advance') body.addAll({'advanceAmount': num.tryParse(_amount.text), 'advanceInstallment': num.tryParse(_instalment.text)});
    final (ok, err) = await ApiService.instance.createRequest(body);
    if (!mounted) return;
    setState(() => _busy = false);
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(ok ? L(context, 'أُرسل الطلب', 'Request sent') : (err ?? L(context, 'تعذّر إرسال الطلب', 'The request could not be sent')))));
    if (ok) {
      AppNav.notifyDataChanged();
      Navigator.of(context).pop();
    }
  }

  @override
  Widget build(BuildContext context) {
    final kinds = [('Letter', Icons.description_outlined, 'خطاب', 'Letter'), ('Permission', Icons.schedule_outlined, 'إذن', 'Permission'), ('Advance', Icons.payments_outlined, 'سلفة', 'Advance')];
    return PageFrame(
      title: L(context, 'طلب جديد', 'New request'),
      child: ListView(padding: const EdgeInsets.all(16), children: [
        SectionCard(
          child: Row(children: [
            for (final k in kinds)
              Expanded(
                child: InkWell(
                  borderRadius: BorderRadius.circular(14),
                  onTap: () => setState(() => _type = k.$1),
                  child: Column(children: [
                    IconTile(k.$2, size: 52, fg: _type == k.$1 ? Colors.white : Brand.brand, bg: _type == k.$1 ? Brand.brand : Brand.tint2),
                    const SizedBox(height: 6),
                    Text(L(context, k.$3, k.$4), style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w600)),
                  ]),
                ),
              ),
          ]),
        ),
        SectionCard(
          child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            if (_type == 'Letter') ...[
              DropdownButtonFormField<String>(
                initialValue: _letter,
                decoration: InputDecoration(labelText: L(context, 'نوع الخطاب', 'Letter type')),
                items: [
                  DropdownMenuItem(value: 'Salary', child: Text(L(context, 'شهادة راتب', 'Salary certificate'))),
                  DropdownMenuItem(value: 'Experience', child: Text(L(context, 'شهادة خبرة', 'Experience certificate'))),
                  DropdownMenuItem(value: 'Employment', child: Text(L(context, 'تعريف بالعمل', 'Employment letter'))),
                ],
                onChanged: (v) => setState(() => _letter = v ?? 'Salary'),
              ),
              const SizedBox(height: 12),
              TextField(controller: _addressee, decoration: InputDecoration(labelText: L(context, 'موجّه إلى', 'Addressed to'), hintText: L(context, 'مثال: بنك الكويت الوطني', 'e.g. National Bank of Kuwait'))),
            ],
            if (_type == 'Permission') ...[
              OutlinedButton.icon(
                onPressed: () async {
                  final d = await showDatePicker(context: context, initialDate: _date, firstDate: DateTime.now().subtract(const Duration(days: 30)), lastDate: DateTime.now().add(const Duration(days: 90)));
                  if (d != null) setState(() => _date = d);
                },
                icon: const Icon(Icons.calendar_today_outlined),
                label: Text(ymd(_date)),
              ),
              const SizedBox(height: 8),
              Row(children: [
                Expanded(child: OutlinedButton(onPressed: () async { final t = await showTimePicker(context: context, initialTime: _from); if (t != null) setState(() => _from = t); }, child: Text('${L(context, 'من ', 'From ')}${_t(_from)}'))),
                const SizedBox(width: 8),
                Expanded(child: OutlinedButton(onPressed: () async { final t = await showTimePicker(context: context, initialTime: _to); if (t != null) setState(() => _to = t); }, child: Text('${L(context, 'إلى ', 'To ')}${_t(_to)}'))),
              ]),
            ],
            if (_type == 'Advance') ...[
              TextField(controller: _amount, keyboardType: const TextInputType.numberWithOptions(decimal: true), decoration: InputDecoration(labelText: L(context, 'المبلغ', 'Amount'))),
              const SizedBox(height: 12),
              TextField(controller: _instalment, keyboardType: const TextInputType.numberWithOptions(decimal: true), decoration: InputDecoration(labelText: L(context, 'القسط الشهري', 'Monthly instalment'))),
              const SizedBox(height: 8),
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(color: Brand.tint2, borderRadius: BorderRadius.circular(12), border: Border.all(color: const Color(0xFFA8C5FF))),
                child: Text(L(context, 'يُفحص القسط مقابل السقف القانوني من الأجر؛ إن تجاوزه يقترح النظام القسط المسموح.', 'The instalment is checked against the legal cap on pay; above it, the system suggests the allowed one.'), style: const TextStyle(fontSize: 12, color: Brand.deep)),
              ),
            ],
            const SizedBox(height: 12),
            TextField(controller: _reason, maxLines: 3, decoration: InputDecoration(labelText: L(context, 'السبب / ملاحظة', 'Reason / note'))),
            const SizedBox(height: 14),
            FilledButton(onPressed: _busy ? null : _send, child: Text(L(context, 'إرسال', 'Send'))),
          ]),
        ),
      ]),
    );
  }
}
