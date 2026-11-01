/* Finance and configuration: billing & cashier, insurance approvals and claims, payers & contracts,
   reports, hospital settings (facility type, modules, country), patient portal. */
(function () {
	'use strict';
	var H = HMS, L = H.L, T = H.T, D = H.data;

	/* ---------------- Billing ---------------- */
	H.route('billing', { module: 'billing', render: function () {
		var b = D.bill, p = D.patient(b.p), y = D.payer(p.payer), c = H.country();
		var insured = y.kind !== 'cash';
		// A non-medical line shows the tax rule at work: medical lines follow the country's medical
		// treatment, the companion's meals are taxed at the standard rate.
		var lines = b.lines.concat([{ code: 'NM-MEAL', d: L('وجبات مرافق', 'Companion meals'), dept: 'F&B', qty: 3, price: 45, cov: 0, taxable: true }]);
		var tot = { gross: 0, ins: 0, pat: 0, vat: 0 };
		var copayLeft = y.cap || Infinity;
		var rows = lines.map(function (l) {
			var gross = l.qty * l.price, covered = insured ? gross * l.cov / 100 : 0;
			var copay = Math.min(covered * (y.copay || 0) / 100, copayLeft); copayLeft -= copay;
			var ins = covered - copay, pat = gross - ins;
			var vat = l.taxable ? pat * c.vat / 100 : 0;
			tot.gross += gross; tot.ins += ins; tot.pat += pat; tot.vat += vat;
			return [H.code(l.code), '<div class="fw-semibold text-gray-800">' + T(l.d) + '</div><div class="text-muted fs-8">' + (D.departments[l.dept] ? T(D.departments[l.dept]) : l.dept) + '</div>', '<span class="num">' + l.qty + '</span>', H.money(l.price, { bare: true }), H.money(gross, { bare: true }), H.money(ins, { bare: true, dash: true }), H.money(pat, { bare: true, dash: true }), H.money(vat, { bare: true, dash: true })];
		});
		var due = tot.pat + tot.vat - b.paid;
		var foot = '<tr class="fw-bolder fs-5"><td class="ps-4" colspan="4" style="color:#145DBF">' + L('الإجمالي', 'Total') + ' (' + H.cur() + ')</td><td class="text-end">' + H.money(tot.gross, { bare: true }) + '</td><td class="text-end">' + H.money(tot.ins, { bare: true }) + '</td><td class="text-end">' + H.money(tot.pat, { bare: true }) + '</td><td class="text-end pe-4">' + H.money(tot.vat, { bare: true }) + '</td></tr>';
		var summary = '<div class="card mb-5 mb-xl-10"><div class="card-body py-10"><h2 class="mb-2">' + L('فاتورة ', 'Bill ') + '<span class="num">' + b.no + '</span></h2>' +
			'<p class="fs-6 fw-semibold text-gray-600 py-1 m-0">' + T(p.name) + ' · ' + L('زيارة ', 'Encounter ') + '<span class="num">' + b.enc + '</span> · ' + L('من ', 'From ') + H.dateStr(b.from) + ' — ' + L('إلى ', 'to ') + H.dateStr(b.to) + '</p>' +
			'<div class="row mt-6">' + [[L('الإجمالي', 'Gross'), tot.gross, 'text-info'], [L('حصة التأمين', 'Insurer share'), tot.ins, 'text-primary'], [L('حصة المريض + الضريبة', 'Patient share + tax'), tot.pat + tot.vat, 'text-primary'], [L('المتبقي على المريض', 'Patient balance due'), due, due > 0 ? 'text-danger' : 'text-success']].map(function (k) {
				return '<div class="col-sm-6 col-xl-3"><div class="card card-dashed flex-center min-w-175px my-3 p-6"><span class="fs-4 fw-semibold ' + k[2] + ' pb-1 px-2">' + k[0] + '</span><span class="fs-lg-2tx fw-bold d-flex justify-content-center">' + H.money(k[1], { bare: true }) + '</span></div></div>';
			}).join('') + '</div>' +
			H.notice({ icon: 'ki-shield-tick', title: insured ? T(y.name) + ' · ' + p.cls : L('مريض نقدي', 'Cash patient'), text: insured ? L('التحمل ', 'Co-pay ') + '<span class="num">' + y.copay + '%</span>' + (y.cap ? L(' بحد أقصى ', ' capped at ') + H.money(y.cap) : '') + L('، والضريبة: ', '; tax: ') + T(c.vatRule) + '.' : T(c.vatRule), badge: L('ضريبة ', 'VAT ') + '<span class="num">' + c.vat + '%</span>', cls: 'mt-4 mb-0' }) + '</div></div>';
		var lineCard = H.card({ title: L('بنود الفاتورة', 'Bill lines'), toolbar: H.btn('light-primary', 'ki-plus', L('إضافة خدمة', 'Add service')), body: H.table(
			[{ t: L('الرمز', 'Code'), w: '120px' }, { t: L('الخدمة', 'Service') }, { t: L('الكمية', 'Qty'), cls: 'text-end', w: '70px' }, { t: L('السعر', 'Price'), cls: 'text-end' }, { t: L('الإجمالي', 'Gross'), cls: 'text-end' }, { t: L('التأمين', 'Insurer'), cls: 'text-end' }, { t: L('المريض', 'Patient'), cls: 'text-end' }, { t: L('الضريبة', 'VAT'), cls: 'text-end' }], rows, { foot: foot }) });
		// Posting preview: the journal the bill produces through the existing accounting engine.
		var rev = {}; lines.forEach(function (l) { var k = D.departments[l.dept] ? T(D.departments[l.dept]) : l.dept; rev[k] = (rev[k] || 0) + l.qty * l.price; });
		var jr = [[L('ذمم شركة التأمين — ', 'Receivable: ') + T(y.name), tot.ins, 0], [L('الصندوق — دفعة مقدمة', 'Cash — deposit'), b.paid, 0], [L('ذمم المرضى', 'Patient receivable'), due, 0]]
			.concat(Object.keys(rev).map(function (k) { return [L('إيراد — ', 'Revenue: ') + k, 0, rev[k]]; })).concat([[L('ضريبة القيمة المضافة — مخرجات', 'Output VAT'), 0, tot.vat]]).filter(function (r) { return r[1] || r[2]; });
		var dr = jr.reduce(function (a, r) { return a + r[1]; }, 0), cr = jr.reduce(function (a, r) { return a + r[2]; }, 0);
		var journal = H.card({ title: L('معاينة القيد المحاسبي', 'Journal preview'), sub: L('يُرحَّل عند إغلاق الزيارة', 'Posted when the encounter closes'), toolbar: H.reuse('محرك القيود في المحاسبة', 'Accounting journal engine'), body: H.table([{ t: L('الحساب', 'Account') }, { t: L('مدين', 'Debit'), cls: 'text-end', w: '160px' }, { t: L('دائن', 'Credit'), cls: 'text-end', w: '160px' }],
			jr.map(function (r) { return [r[0], H.money(r[1], { bare: true, dash: true }), H.money(r[2], { bare: true, dash: true })]; }),
			{ foot: '<tr class="fw-bolder fs-5"><td class="ps-4" style="color:#145DBF">' + L('الإجمالي', 'Total') + '</td><td class="text-end">' + H.money(dr, { bare: true }) + '</td><td class="text-end pe-4">' + H.money(cr, { bare: true }) + '</td></tr>' }) });
		var pay = H.card({ title: L('التحصيل', 'Collect payment'), body: '<div class="row g-5">' + H.field(L('المبلغ', 'Amount'), '<div class="input-group input-group-solid"><input class="form-control form-control-solid num" value="' + due.toFixed(c.currency.dec) + '" /><span class="input-group-text">' + H.cur() + '</span></div>', 'col-12') +
			'<div class="col-12"><label class="fw-semibold fs-7 text-gray-700 mb-2">' + L('طريقة الدفع', 'Method') + '</label><div class="d-flex gap-2 flex-wrap">' + [L('نقدي', 'Cash'), L('بطاقة', 'Card'), L('تحويل', 'Transfer'), L('محفظة', 'Wallet')].map(function (m, i) { return '<label><input type="radio" class="btn-check" name="pm" ' + (i === 1 ? 'checked' : '') + ' /><span class="btn btn-sm btn-outline btn-outline-dashed btn-active-light-primary">' + m + '</span></label>'; }).join('') + '</div></div></div>' +
			'<button class="btn btn-primary w-100 mt-6" onclick="HMS.toast(HMS.L(\'تم التحصيل وإصدار الإيصال\',\'Payment collected and receipt issued\'))"><i class="ki-outline ki-wallet fs-4"></i>' + L('تحصيل وإصدار إيصال', 'Collect & issue receipt') + '</button>' +
			'<div class="text-muted fs-8 mt-4">' + L('الفاتورة الإلكترونية: ', 'e-Invoicing: ') + T(c.einvoice) + '</div>' });
		var html = summary + lineCard + '<div class="row g-5 g-xl-8"><div class="col-xl-7">' + journal + '</div><div class="col-xl-5">' + pay +
			H.card({ title: L('المريض', 'Patient'), body: H.personCell(p, p.mrn + ' · ' + H.payerLabel(p), '#/patient/' + p.mrn, 50) }) + '</div></div>';
		return { title: L('الفوترة والخزينة', 'Billing & cashier'), crumbs: [{ t: L('المالية', 'Finance') }, { t: L('الفوترة', 'Billing') }],
			actions: H.btn('light-primary', 'ki-printer', L('طباعة', 'Print'), 'onclick="HMS.toast(HMS.L(\'تُطبع من Report Studio\',\'Prints through Report Studio\'),\'info\')"') + H.btn('primary', 'ki-lock-2', L('إغلاق الزيارة وترحيل', 'Close encounter & post'), 'onclick="HMS.toast(HMS.L(\'رُحّل القيد وأُرسلت المطالبة\',\'Journal posted and claim queued\'))"'), html: html };
	} });

	/* ---------------- Insurance: approvals & claims ---------------- */
	var paSt = function (s) { return { pending: H.badge('primary', L('بانتظار الرد', 'Pending')), approved: H.badge('success', L('موافَق', 'Approved')), partial: H.badge('info', L('موافقة جزئية', 'Partly approved')), rejected: H.badge('danger', L('مرفوض', 'Rejected')) }[s]; };
	var clSt = function (s) { return { draft: H.badge('secondary', L('مسودة', 'Draft')), submitted: H.badge('primary', L('مرفوعة', 'Submitted')), paid: H.badge('success', L('مسددة', 'Paid')), partial: H.badge('info', L('مسددة جزئياً', 'Part paid')), 'rejected-lines': H.badge('danger', L('بنود مرفوضة', 'Lines rejected')) }[s]; };
	H.route('insurance', { module: 'insurance', render: function () {
		var c = H.country(), id = 'ins';
		var pa = H.card({ body: H.table([{ t: L('الطلب', 'Request'), w: '110px' }, { t: L('المريض', 'Patient') }, { t: L('الجهة', 'Payer') }, { t: L('الخدمة', 'Service') }, { t: L('المبلغ', 'Amount'), cls: 'text-end' }, { t: L('الحالة', 'Status') }],
			D.preauths.map(function (a) { var p = D.patient(a.p); return [H.code(a.id), H.personCell(p, p.mrn, '#/patient/' + p.mrn, 35), T(D.payer(a.payer).name), T(a.svc) + (a.reason ? '<div class="text-danger fs-8">' + T(a.reason) + '</div>' : ''), H.money(a.amt), paSt(a.st)]; })), bodyCls: 'pt-6' });
		var cl = H.card({ body: H.table([{ t: L('الدفعة', 'Batch'), w: '140px' }, { t: L('الجهة', 'Payer') }, { t: L('الفترة', 'Period') }, { t: L('المطالبات', 'Claims'), cls: 'text-end' }, { t: L('المبلغ', 'Amount'), cls: 'text-end' }, { t: L('المقبول', 'Accepted'), cls: 'text-end' }, { t: L('المرفوض', 'Rejected'), cls: 'text-end' }, { t: L('الحالة', 'Status') }],
			D.claims.map(function (b) { return [H.code(b.id), T(D.payer(b.payer).name), H.dateStr(b.period), '<span class="num">' + b.count + '</span>', H.money(b.amount, { bare: true }), H.money(b.accepted, { bare: true, dash: true }), b.rejected ? '<span class="text-danger">' + H.money(b.rejected, { bare: true }) + '</span>' : '<span class="hms-zero">—</span>', clSt(b.st)]; })), bodyCls: 'pt-6' });
		var rj = H.card({ body: H.table([{ t: L('الدفعة', 'Batch') }, { t: L('المريض', 'Patient') }, { t: L('البند', 'Line') }, { t: L('سبب الرفض', 'Reason') }, { t: L('المبلغ', 'Amount'), cls: 'text-end' }, { t: '', cls: 'text-end' }],
			D.rejections.map(function (r) { var p = D.patient(r.p); return [H.code(r.claim), H.personCell(p, null, null, 30), T(r.line), H.code(r.code) + ' <span class="text-gray-700 fs-7">' + T(r.reason) + '</span>', H.money(r.amt), H.btn('light-primary', 'ki-arrow-circle-right', L('إعادة رفع', 'Resubmit'), 'onclick="HMS.toast(HMS.L(\'أُعيد رفع البند مع المبررات\',\'Line resubmitted with justification\'))"')]; })), bodyCls: 'pt-6' });
		var html = H.stats([
			{ label: L('موافقات بانتظار الرد', 'Approvals pending'), value: '<span class="num">' + D.preauths.filter(function (a) { return a.st === 'pending'; }).length + '</span>', sub: L('أقدمها منذ ', 'oldest ') + '<span class="num">42</span> ' + L('د', 'm') },
			{ label: L('مطالبات الشهر', 'Claims this month'), value: H.money(367200, { bare: true }), sub: H.cur() },
			{ label: L('نسبة الرفض', 'Rejection rate'), value: '<span class="num">6.1%</span>', cls: 'text-danger', sub: L('الهدف أقل من ٥٪', 'Target below 5%') },
			{ label: L('متوسط أيام التحصيل', 'Days to collect'), value: '<span class="num">54</span>', sub: L('من تاريخ الرفع', 'from submission') }]);
		html += H.notice({ icon: 'ki-arrows-circle', title: L('الربط الإلكتروني: ', 'E-claims channel: ') + T(c.eclaims), text: L('القناة تُختار من إعدادات الدولة؛ نفس الشاشة ترفع لأي جهة حسب المنشأة.', 'The channel comes from the country settings; the same screen submits to whichever exchange the facility uses.'), badge: T(c.name) });
		html += H.tabs(id, [{ k: 'pa', t: L('الموافقات المسبقة', 'Pre-authorisations'), n: D.preauths.length }, { k: 'cl', t: L('دفعات المطالبات', 'Claim batches'), n: D.claims.length }, { k: 'rj', t: L('المرفوضات', 'Rejections'), n: D.rejections.length }], 'pa') +
			'<div class="tab-content">' + H.pane(id, 'pa', true, pa) + H.pane(id, 'cl', false, cl) + H.pane(id, 'rj', false, rj) + '</div>';
		return { title: L('الموافقات والمطالبات', 'Approvals & claims'), crumbs: [{ t: L('التأمين', 'Insurance') }], actions: H.btn('primary', 'ki-send', L('رفع دفعة الشهر', 'Submit month batch'), 'onclick="HMS.toast(HMS.L(\'رُفعت الدفعة\',\'Batch submitted\'))"'), html: html };
	} });

	H.route('payers', { module: 'insurance', render: function () {
		var rows = D.payers.filter(function (y) { return y.kind !== 'cash'; }).map(function (y) {
			var kind = { ins: L('تأمين', 'Insurer'), corp: L('شركات', 'Corporate'), gov: L('حكومي', 'Government') }[y.kind];
			return ['<div class="fw-bold text-gray-900">' + T(y.name) + '</div><div class="text-muted fs-8">' + kind + '</div>', '<span class="text-gray-700">' + y.classes + '</span>', '<span class="num">' + y.discount + '%</span>', y.copay ? '<span class="num">' + y.copay + '%</span>' : '<span class="hms-zero">—</span>', T(y.priceList), H.dateStr(y.from) + ' → ' + H.dateStr(y.to), '<span class="num ' + (y.rejectRate > 8 ? 'text-danger fw-bold' : '') + '">' + y.rejectRate + '%</span>', y.to < '2026-11-01' ? H.badge('danger', L('منتهي', 'Expired')) : H.badge('success', L('ساري', 'Active'))];
		});
		var html = H.card({ title: L('شركات التأمين والعقود', 'Payers & contracts'), sub: L('كل عقد يحدد الخصم والتحمل وقائمة الأسعار والخدمات التي تحتاج موافقة', 'Each contract sets the discount, co-pay, price list and services needing approval'), toolbar: H.searchBox(), body: H.table(
			[{ t: L('الجهة', 'Payer') }, { t: L('الفئات', 'Classes') }, { t: L('الخصم', 'Discount'), cls: 'text-end' }, { t: L('التحمل', 'Co-pay'), cls: 'text-end' }, { t: L('قائمة الأسعار', 'Price list') }, { t: L('الصلاحية', 'Validity') }, { t: L('نسبة الرفض', 'Rejection'), cls: 'text-end' }, { t: L('الحالة', 'State') }], rows) });
		html += H.card({ title: L('قائمة الأسعار — مقتطف', 'Price list — excerpt'), sub: L('قائمة A ٢٠٢٦', 'List A 2026'), body: H.table([{ t: L('الرمز', 'Code') }, { t: L('الخدمة', 'Service') }, { t: L('السعر العام', 'Base price'), cls: 'text-end' }, { t: L('سعر العقد', 'Contract price'), cls: 'text-end' }, { t: L('موافقة مسبقة', 'Pre-auth'), cls: 'text-center' }],
			[['CONS-IM', L('كشف استشاري باطنة', 'Internal medicine consultation'), 300, 255, false], ['LAB-CBC', L('صورة دم كاملة', 'Complete blood count'), 85, 72, false], ['RAD-MRI-K', L('رنين مغناطيسي للركبة', 'MRI knee'), 1900, 1615, true], ['ROOM-W3', L('إقامة جناح — ليلة', 'Ward stay — night'), 650, 553, true]].map(function (r) {
				return [H.code(r[0]), r[1], H.money(r[2], { bare: true }), H.money(r[3], { bare: true }), r[4] ? '<i class="ki-outline ki-check-circle fs-3 text-primary"></i>' : '<span class="hms-zero">—</span>'];
			})) });
		return { title: L('شركات التأمين والعقود', 'Payers & contracts'), crumbs: [{ t: L('التأمين', 'Insurance'), href: '#/insurance' }, { t: L('العقود', 'Contracts') }], actions: H.btn('primary', 'ki-plus', L('عقد جديد', 'New contract')), html: html };
	} });

	/* ---------------- Reports ---------------- */
	H.route('reports', { render: function () {
		var groups = [
			[L('سريرية', 'Clinical'), 'ki-heart', [L('أكثر التشخيصات شيوعاً', 'Top diagnoses'), L('معدل إعادة الدخول خلال ٣٠ يوماً', '30-day readmission rate'), L('زمن إنجاز المختبر', 'Lab turnaround time'), L('القيم الحرجة وزمن الإبلاغ', 'Critical values and call time')]],
			[L('تشغيلية', 'Operational'), 'ki-graph-3', [L('إشغال الأسرة ومتوسط الإقامة', 'Bed occupancy and length of stay'), L('أوقات الانتظار في الطوارئ', 'ER waiting times'), L('استغلال غرف العمليات', 'Theatre utilisation'), L('إنتاجية الأطباء', 'Doctor productivity')]],
			[L('مالية', 'Financial'), 'ki-dollar', [L('الإيراد حسب القسم والطبيب', 'Revenue by department and doctor'), L('أعمار ذمم التأمين', 'Insurance receivables ageing'), L('تحصيل الخزينة اليومي', 'Daily cashier collection'), L('ربحية الخدمة', 'Service profitability')]],
			[L('التأمين', 'Insurance'), 'ki-shield-tick', [L('أسباب الرفض', 'Rejection reasons'), L('الموافقات المعلقة', 'Pending approvals'), L('أداء الجهات الدافعة', 'Payer performance')]]
		];
		var html = H.notice({ icon: 'ki-chart-pie-simple', title: L('كل التقارير والمطبوعات عبر Report Studio', 'Every report and printout runs through Report Studio'), text: L('كل تقرير مجموعة بيانات مسجلة بصلاحياتها، والمطبوعات (روشتة، تقرير خروج، فاتورة) تُصمم من نفس المكان.', 'Each report is a registered dataset with its own permission; printouts are designed in the same place.'), badge: '<span class="num">15</span> ' + L('تقرير', 'reports') });
		html += '<div class="row g-5 g-xl-8">' + groups.map(function (g) {
			return '<div class="col-xl-6">' + H.card({ title: g[0], body: g[2].map(function (r) {
				return '<div class="d-flex align-items-center cb-row py-3"><span class="symbol symbol-40px me-4"><span class="symbol-label" style="background:#E7F0FF"><i class="ki-outline ' + g[1] + ' fs-3" style="color:#0E4A9E"></i></span></span><span class="fw-semibold text-gray-800 flex-grow-1">' + r + '</span><button class="btn btn-sm btn-light-primary" onclick="HMS.toast(HMS.L(\'يفتح في /Reports/Viewer\',\'Opens in /Reports/Viewer\'),\'info\')">' + L('فتح', 'Open') + '</button></div>';
			}).join('') }) + '</div>';
		}).join('') + '</div>';
		return { title: L('تقارير المستشفى', 'Hospital reports'), crumbs: [{ t: L('التقارير', 'Reports') }], html: html };
	} });

	/* ---------------- Settings ---------------- */
	function rerender() { H.saveSettings(); H.renderMenu(); H.renderHeader(); H.render(); }
	H.applyPreset = function (k) { H.settings.preset = k; H.settings.modules = H.presetModules(k); rerender(); H.toast(L('طُبّق نوع المنشأة', 'Facility type applied')); };
	H.toggleModule = function (k, on) { H.setModule(k, on); H.settings.preset = 'custom'; rerender(); };
	H.setCountry = function (k) { H.settings.country = k; rerender(); H.toast(L('تم تطبيق إعدادات الدولة', 'Country settings applied')); };
	H.resetSettings = function () { try { localStorage.removeItem('hms.settings'); } catch (e) { } location.reload(); };

	H.route('settings', { render: function () {
		var s = H.settings, c = H.country(), id = 'st';
		var presets = '<div class="row g-5 mb-8">' + H.PRESETS.map(function (p) {
			return '<div class="col-md-6 col-xl-3"><div class="hms-preset ' + (s.preset === p.key ? 'active' : '') + '" onclick="HMS.applyPreset(\'' + p.key + '\')"><i class="ki-outline ' + p.icon + ' fs-2x mb-3" style="color:#0E4A9E"></i><div class="fw-bold fs-5 text-gray-900">' + T(p) + '</div><div class="text-gray-600 fs-7 mt-1">' + L(p.dar, p.den) + '</div><div class="mt-3"><span class="badge badge-light-primary num">' + H.MODULES.filter(function (m) { return H.presetModules(p.key)[m.key]; }).length + '</span> <span class="text-muted fs-8">' + L('وحدة', 'modules') + '</span></div></div></div>';
		}).join('') + '</div>';
		var on = H.MODULES.filter(function (m) { return H.isOn(m.key); }).length;
		var mods = '<div class="row g-4">' + H.MODULES.map(function (m) {
			var req = (m.requires || []).map(function (r) { return T(H.MOD[r]); }).join(L('، ', ', '));
			var active = H.isOn(m.key);
			return '<div class="col-md-6 col-xl-4"><div class="hms-module ' + (active ? '' : 'off') + '"><div class="d-flex align-items-start"><span class="symbol symbol-40px me-4 flex-shrink-0"><span class="symbol-label" style="background:' + (active ? '#E7F0FF' : 'var(--bs-gray-200)') + '"><i class="ki-outline ' + m.icon + ' fs-3" style="color:' + (active ? '#0E4A9E' : 'var(--bs-gray-500)') + '"></i></span></span>' +
				'<div class="flex-grow-1 min-w-0"><div class="fw-bold text-gray-900 hms-module-title">' + T(m) + '</div><div class="text-gray-600 fs-8 mt-1">' + L(m.dar, m.den) + '</div>' + (req ? '<div class="text-muted fs-8 mt-2"><i class="ki-outline ki-abstract-26 fs-8"></i> ' + L('يتطلب: ', 'Requires: ') + req + '</div>' : '') + '</div>' +
				'<div class="form-check form-switch form-check-custom form-check-solid ms-3">' + (m.core ? '<span class="badge badge-light fs-8">' + L('أساسي', 'Core') + '</span>' : '<input class="form-check-input" type="checkbox" ' + (active ? 'checked' : '') + ' onchange="HMS.toggleModule(\'' + m.key + '\', this.checked)" />') + '</div></div></div></div>';
		}).join('') + '</div>';
		var facility = H.card({ title: L('نوع المنشأة', 'Facility type'), sub: L('اختيار النوع يضبط الوحدات دفعة واحدة، ثم يمكن تعديل كل وحدة بمفردها', 'A type switches modules in one go; each module can then be changed on its own'), body: presets }) +
			H.card({ title: L('الوحدات', 'Modules'), sub: '<span class="num">' + on + ' / ' + H.MODULES.length + '</span> ' + L('مفعّلة — الإيقاف يخفي الوحدة من القائمة ولا يحذف بياناتها', 'enabled — switching off hides a module and keeps its data'), body: mods });

		var countryCards = '<div class="row g-5 mb-8">' + Object.keys(D.countries).map(function (k) {
			var x = D.countries[k];
			return '<div class="col-md-4 col-xl"><div class="hms-preset text-center ' + (s.country === k ? 'active' : '') + '" onclick="HMS.setCountry(\'' + k + '\')"><img src="assets/media/flags/' + x.flag + '.svg" class="w-40px h-40px rounded-circle mb-3" alt="" /><div class="fw-bold text-gray-900">' + T(x.name) + '</div><div class="text-muted fs-8 mt-1 num">' + x.currency.code + '</div></div></div>';
		}).join('') + '</div>';
		var prof = [[L('العملة', 'Currency'), T(c.currency.name) + ' · <span class="num">' + c.currency.code + '</span> · ' + L('خانات عشرية ', 'decimals ') + '<span class="num">' + c.currency.dec + '</span>'], [L('ضريبة القيمة المضافة', 'VAT'), '<span class="num">' + c.vat + '%</span>'], [L('معاملة الخدمات الطبية', 'Medical services treatment'), T(c.vatRule)], [L('الفاتورة الإلكترونية', 'e-Invoicing'), T(c.einvoice)], [L('المطالبات الإلكترونية', 'e-Claims'), T(c.eclaims)], [L('الوصفة الإلكترونية', 'e-Prescription'), T(c.erx)], [L('الهوية', 'Identity document'), T(c.idLabel) + ' · <span class="num">' + c.idFormat + '</span> ' + L('رقم', 'digits')], [L('نظام الترميز', 'Coding system'), '<span class="num">' + c.coding + '</span>'], [L('الجهات الرقابية', 'Regulators'), T(c.regulator)], [L('عطلة نهاية الأسبوع', 'Weekend'), T(c.weekend)], [L('التقويم الهجري', 'Hijri calendar'), c.hijri ? L('يظهر بجانب الميلادي', 'Shown beside Gregorian') : L('غير مفعّل', 'Off')]];
		var country = H.card({ title: L('الدولة', 'Country'), sub: L('الدول والعملات من الجداول العامة للنظام (CountriesLookup و Currency)', 'Countries and currencies come from the system tables (CountriesLookup and Currency)'), body: countryCards +
			H.table([{ t: L('الإعداد', 'Setting'), w: '30%' }, { t: L('القيمة الافتراضية', 'Default value') }, { t: '', cls: 'text-end', w: '90px' }], prof.map(function (r) { return ['<span class="fw-semibold text-gray-700">' + r[0] + '</span>', '<span class="fw-bold text-gray-900">' + r[1] + '</span>', '<button class="btn btn-sm btn-light">' + L('تعديل', 'Edit') + '</button>']; })) }) +
			H.notice({ icon: 'ki-information-5', title: L('قيم افتراضية قابلة للتعديل', 'Editable defaults'), text: L('حزمة كل دولة نقطة بداية؛ نسب الضريبة والإعفاءات تُراجع مع المستشار الضريبي للمنشأة قبل التشغيل.', 'Each country pack is a starting point; tax rates and exemptions are confirmed with the facility\'s tax adviser before go-live.') });

		var numbering = H.card({ title: L('الترقيم', 'Numbering'), body: H.table([{ t: L('المستند', 'Document') }, { t: L('النمط', 'Pattern') }, { t: L('التالي', 'Next') }], [
			[L('رقم الملف الطبي', 'Medical record number'), H.code('MRN-{000000}'), '<span class="num">MRN-100438</span>'], [L('الزيارة', 'Encounter'), H.code('ENC-{YYYY}-{00000}'), '<span class="num">ENC-2026-04412</span>'], [L('الدخول', 'Admission'), H.code('ADM-{YYYY}-{0000}'), '<span class="num">ADM-2026-0913</span>'], [L('فاتورة المستشفى', 'Hospital invoice'), H.code('INV-H-{YYYY}-{00000}'), '<span class="num">INV-H-2026-01843</span>'], [L('طلب مختبر', 'Lab order'), H.code('LAB-{00000}'), '<span class="num">LAB-24132</span>']]) }) +
			H.card({ title: L('الأقسام والعيادات', 'Departments & clinics'), toolbar: H.btn('light-primary', 'ki-plus', L('إضافة', 'Add')), body: '<div class="d-flex flex-wrap gap-2">' + Object.keys(D.departments).map(function (k) { return '<span class="hms-chip">' + T(D.departments[k]) + '</span>'; }).join('') + '</div><div class="mt-4">' + H.reuse('الهيكل التنظيمي', 'Organisation structure') + '</div>' });

		var html = H.tabs(id, [{ k: 'f', t: L('المنشأة والوحدات', 'Facility & modules') }, { k: 'c', t: L('الدولة والضرائب', 'Country & tax') }, { k: 'n', t: L('الترقيم والأقسام', 'Numbering & departments') }], 'f') +
			'<div class="tab-content">' + H.pane(id, 'f', true, facility) + H.pane(id, 'c', false, country) + H.pane(id, 'n', false, numbering) + '</div>';
		return { title: L('إعدادات المستشفى', 'Hospital settings'), crumbs: [{ t: L('الإعدادات', 'Settings') }], actions: H.btn('light', 'ki-arrows-circle', L('استعادة الافتراضي', 'Reset to default'), 'onclick="HMS.resetSettings()"'), html: html,
			after: function () {
				// Keep the tab the user was on across the re-render a toggle causes.
				var key = 'hms.settingsTab', last = H.store.get(key, null);
				if (last) { var a = document.querySelector('a[href="#' + last + '"]'); if (a) bootstrap.Tab.getOrCreateInstance(a).show(); }
				[].slice.call(document.querySelectorAll('a[data-bs-toggle="tab"]')).forEach(function (a) { a.addEventListener('shown.bs.tab', function () { H.store.set(key, a.getAttribute('href').slice(1)); }); });
			} };
	} });

	/* ---------------- Patient portal ---------------- */
	H.route('portal', { module: 'portal', render: function () {
		var p = D.patient('MRN-100245');
		var phone = '<div class="mx-auto rounded-4 border border-4 border-gray-800 overflow-hidden" style="width:320px;height:620px;background:var(--bs-gray-100)"><div class="p-6" style="background:#0E4A9E;color:#fff"><div class="d-flex align-items-center gap-3">' + H.avatar(p, 45) + '<div><div class="fw-bold">' + L('مرحباً ', 'Hello ') + T(p.name).split(' ')[0] + '</div><div class="fs-8 opacity-75 num">' + p.mrn + '</div></div></div></div><div class="p-4">' +
			[['ki-calendar', L('موعدك القادم', 'Next appointment'), L('الأحد ١٠:٣٠ — د. منى السيد', 'Sun 10:30 — Dr. Mona Elsayed')], ['ki-flask', L('نتائج جديدة', 'New results'), L('تحليل الحمل الكمي — جاهز', 'Quantitative β-hCG — ready')], ['ki-capsule', L('أدويتي', 'My medications'), L('حمض الفوليك · حديد', 'Folic acid · Iron')], ['ki-bill', L('فواتيري', 'My bills'), L('لا توجد مستحقات', 'Nothing due')], ['ki-screen', L('زيارة مرئية', 'Video visit'), L('احجز كشفاً عن بعد', 'Book a remote consultation')]].map(function (r) {
				return '<div class="card mb-3"><div class="card-body p-4 d-flex align-items-center"><i class="ki-outline ' + r[0] + ' fs-2x me-3" style="color:#0E4A9E"></i><div><div class="fw-bold text-gray-900 fs-7">' + r[1] + '</div><div class="text-muted fs-8">' + r[2] + '</div></div></div></div>';
			}).join('') + '</div></div>';
		var html = '<div class="row g-5 align-items-center"><div class="col-xl-5">' + phone + '</div><div class="col-xl-7">' +
			H.notice({ icon: 'ki-phone', title: L('بوابة المريض', 'Patient portal'), text: L('تُبنى على بوابة العملاء الحالية بنطاق مثبّت على المريض، فلا يرى إلا ملفه.', 'Built on the existing Client Portal with a scope pinned to the patient, so they only ever see their own file.'), badge: 'demo30' }) +
			H.card({ title: L('ما يستطيع المريض فعله', 'What the patient can do'), body: [L('حجز وإلغاء المواعيد', 'Book and cancel appointments'), L('رؤية النتائج بعد اعتمادها فقط', 'See results only after verification'), L('دفع الفواتير إلكترونياً', 'Pay bills online'), L('تحميل التقارير الطبية', 'Download medical reports'), L('تحديث بيانات التأمين', 'Update insurance details')].map(function (t) { return '<div class="d-flex align-items-center cb-row py-3"><i class="ki-outline ki-check-circle fs-3 text-success me-3"></i><span class="fw-semibold text-gray-800">' + t + '</span></div>'; }).join('') + '<div class="mt-4">' + H.reuse('بوابة العملاء', 'Client Portal') + '</div>' }) + '</div></div>';
		return { title: L('بوابة المريض', 'Patient portal'), crumbs: [{ t: L('الإعدادات', 'Settings') }, { t: L('بوابة المريض', 'Patient portal') }], html: html };
	} });
})();
