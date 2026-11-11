import 'package:flutter/material.dart';

import '../models/ess.dart';
import '../services/api_service.dart';
import '../services/app_nav.dart';
import '../theme/brand.dart';
import '../widgets/ui.dart';
import 'inbox_tab.dart' show requestTitle, requestPill;

/// MY REQUESTS (the owner's reference): letters, permissions and advances as request cards with their status; and
/// the form to raise one. The server routes it through the company's approval path and checks an advance against the
/// legal cap; its refusal is shown as it is.
class RequestsScreen extends StatefulWidget {
  const RequestsScreen({super.key});
  @override
  State<RequestsScreen> createState() => _RequestsScreenState();
}

class _RequestsScreenState extends State<RequestsScreen> {
  late Future<(String, List<MyRequest>)> _future = ApiService.instance.getMyRequests();

  IconData _icon(String? t) => switch (t) { 'Advance' => Lu.handCoins, 'Permission' => Lu.clock3, 'ProfileChange' => Lu.userPen, _ => Lu.fileText };

  @override
  Widget build(BuildContext context) {
    return ExecFrame(
      child: FutureBuilder<(String, List<MyRequest>)>(
        future: _future,
        builder: (context, s) {
          final head = [
            BackLink(L(context, 'رجوع', 'Back')),
            PageHead(L(context, 'طلباتي', 'My requests'), eyebrow: L(context, 'خطابات · سلف · أذونات', 'Letters · advances · permissions')),
            FilledButton.icon(
              onPressed: () async {
                await Navigator.of(context).push(MaterialPageRoute(builder: (_) => const NewRequestScreen()));
                if (mounted) setState(() => _future = ApiService.instance.getMyRequests());
              },
              icon: const Icon(Lu.plus, size: 16),
              label: Text(L(context, 'طلب جديد', 'New request')),
            ),
            const SizedBox(height: 18),
          ];
          if (s.hasError) {
            return ExecBody(children: [...head, EmptyNote(icon: Lu.wifiOff, title: L(context, 'تعذّر تحميل الطلبات', 'Could not load requests'), text: L(context, 'تحقق من الاتصال ثم أعد المحاولة.', 'Check the connection and try again.'))]);
          }
          if (!s.hasData) return const Center(child: CircularProgressIndicator());
          final (cur, list) = s.data!;
          return ExecBody(children: [
            ...head,
            if (list.isEmpty) EmptyNote(icon: Lu.fileText, title: L(context, 'لا طلبات', 'No requests'), text: L(context, 'اطلب خطاباً أو إذناً أو سلفة من الزر أعلاه.', 'Ask for a letter, a permission or an advance with the button above.')),
            for (final r in list)
              RequestCard(
                leading: IconBox(icon: _icon(r.type)),
                title: requestTitle(context, r),
                subtitle: r.createdAt != null ? '${L(context, 'قُدّم في ', 'Submitted ')}${ymd(r.createdAt!)}' : '',
                tag: requestPill(context, r.status),
                amount: r.amount == null ? '' : money(r.amount!, cur),
                unit: r.amount == null ? '' : curLabel(cur, isAr(context)),
              ),
          ]);
        },
      ),
    );
  }
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
    const types = ['Letter', 'Permission', 'Advance'];
    const letters = ['Salary', 'Experience', 'Employment'];
    final letterNames = [L(context, 'شهادة راتب', 'Salary certificate'), L(context, 'شهادة خبرة', 'Experience certificate'), L(context, 'تعريف بالعمل', 'Employment letter')];
    InputDecoration dec(String label, {String? hint}) => InputDecoration(labelText: label, hintText: hint);
    return ExecFrame(
      child: ExecBody(children: [
        BackLink(L(context, 'طلباتي', 'My requests')),
        PageHead(L(context, 'طلب جديد', 'New request'), eyebrow: L(context, 'يُرسل عبر مسار الاعتماد في شركتك', "Sent through your company's approval path")),
        ChipTabs(
          items: [L(context, 'خطاب', 'Letter'), L(context, 'إذن', 'Permission'), L(context, 'سلفة', 'Advance')],
          index: types.indexOf(_type),
          onTap: (i) => setState(() => _type = types[i]),
        ),
        if (_type == 'Letter') ...[
          ChipTabs(items: letterNames, index: letters.indexOf(_letter), onTap: (i) => setState(() => _letter = letters[i])),
          TextField(controller: _addressee, decoration: dec(L(context, 'موجّه إلى', 'Addressed to'), hint: L(context, 'مثال: بنك الكويت الوطني', 'e.g. National Bank of Kuwait'))),
        ],
        if (_type == 'Permission') ...[
          FieldBox(
            label: L(context, 'التاريخ', 'Date'),
            onTap: () async {
              final d = await showDatePicker(context: context, initialDate: _date, firstDate: DateTime.now().subtract(const Duration(days: 30)), lastDate: DateTime.now().add(const Duration(days: 90)));
              if (d != null) setState(() => _date = d);
            },
            child: Row(children: [Expanded(child: Text(dayMonth(context, _date))), const Icon(Lu.calendar, size: 15, color: Brand.support)]),
          ),
          Row(children: [
            Expanded(
              child: FieldBox(
                label: L(context, 'من', 'From'),
                onTap: () async { final t = await showTimePicker(context: context, initialTime: _from); if (t != null) setState(() => _from = t); },
                child: Text(_t(_from), textDirection: TextDirection.ltr),
              ),
            ),
            const SizedBox(width: 9),
            Expanded(
              child: FieldBox(
                label: L(context, 'إلى', 'To'),
                onTap: () async { final t = await showTimePicker(context: context, initialTime: _to); if (t != null) setState(() => _to = t); },
                child: Text(_t(_to), textDirection: TextDirection.ltr),
              ),
            ),
          ]),
        ],
        if (_type == 'Advance') ...[
          TextField(controller: _amount, keyboardType: const TextInputType.numberWithOptions(decimal: true), decoration: dec(L(context, 'المبلغ', 'Amount'))),
          const SizedBox(height: 9),
          TextField(controller: _instalment, keyboardType: const TextInputType.numberWithOptions(decimal: true), decoration: dec(L(context, 'القسط الشهري', 'Monthly instalment'))),
          const SizedBox(height: 9),
          SoftNote(L(context, 'يُفحص القسط مقابل السقف القانوني من الأجر؛ إن تجاوزه يقترح النظام القسط المسموح.', 'The instalment is checked against the legal cap on pay; above it, the system suggests the allowed one.')),
        ],
        const SizedBox(height: 9),
        TextField(controller: _reason, maxLines: 3, decoration: dec(L(context, 'السبب / ملاحظة', 'Reason / note'))),
        const SizedBox(height: 16),
        FilledButton(onPressed: _busy ? null : _send, child: Text(L(context, 'إرسال الطلب', 'Send request'))),
      ]),
    );
  }
}
