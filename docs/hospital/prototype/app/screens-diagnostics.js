/* Diagnostics and pharmacy: laboratory worklist and result entry, radiology, blood bank, dispensing. */
(function () {
	'use strict';
	var H = HMS, L = H.L, T = H.T, D = H.data;

	H.labStatus = function (s) {
		return { ordered: H.badge('secondary', L('مطلوب', 'Ordered')), collected: H.badge('info', L('سُحبت العينة', 'Collected')), processing: H.badge('primary', L('قيد التحليل', 'Processing')), verified: H.badge('success', L('معتمد', 'Verified')) }[s];
	};
	H.radStatus = function (s) {
		return { scheduled: H.badge('secondary', L('مجدول', 'Scheduled')), arrived: H.badge('info', L('حضر', 'Arrived')), scanning: H.badge('primary', L('قيد التصوير', 'Scanning')), reporting: H.badge('primary', L('قيد كتابة التقرير', 'Reporting')), signed: H.badge('success', L('معتمد', 'Signed')) }[s];
	};
	H.rxStatus = function (s) {
		return { new: H.badge('primary', L('جديدة', 'New')), verified: H.badge('info', L('مراجعة', 'Verified')), dispensed: H.badge('success', L('صُرفت', 'Dispensed')) }[s];
	};
	function prio(p) { return p === 'stat' ? H.badge('danger', 'STAT') : p === 'urgent' ? H.badge('primary', L('عاجل', 'Urgent')) : H.badge('secondary', L('روتيني', 'Routine')); }

	/* ---------------- Laboratory ---------------- */
	H.route('lab', { module: 'lab', render: function () {
		var rows = D.labOrders.map(function (o) {
			var p = D.patient(o.p);
			var act = o.st === 'ordered' ? H.btn('light-primary', 'ki-barcode', L('سحب عينة', 'Collect'), 'onclick="HMS.toast(HMS.L(\'طُبع ملصق العينة\',\'Specimen label printed\'))"') :
				o.st === 'verified' ? '<a href="#/lab-result/' + o.id + '" class="btn btn-sm btn-light">' + L('عرض', 'View') + '</a>' : '<a href="#/lab-result/' + o.id + '" class="btn btn-sm btn-primary">' + L('إدخال النتيجة', 'Enter result') + '</a>';
			return [H.code(o.id), H.personCell(p, p.mrn, '#/patient/' + p.mrn, 35), '<div class="fw-semibold text-gray-800">' + T(o.test) + '</div><div class="text-muted fs-8 num">' + o.code + '</div>', prio(o.prio), '<span class="text-gray-700">' + T(o.from) + '</span>', '<span class="num">' + o.time + '</span>', H.labStatus(o.st) + (o.critical ? ' ' + H.badge('danger', L('قيمة حرجة', 'Critical')) : ''), act];
		});
		var html = H.stats([
			{ label: L('طلبات اليوم', 'Orders today'), value: '<span class="num">214</span>', sub: '<span class="num">' + D.labOrders.length + '</span> ' + L('معروضة', 'shown') },
			{ label: L('متوسط زمن الإنجاز', 'Average turnaround'), value: '<span class="num">47</span> ' + L('د', 'm'), sub: L('للطلبات العاجلة STAT: ', 'STAT: ') + '<span class="num">28</span> ' + L('د', 'm') },
			{ label: L('بانتظار الاعتماد', 'Awaiting verification'), value: '<span class="num">9</span>' },
			{ label: L('قيم حرجة غير مبلّغة', 'Unacknowledged critical values'), value: '<span class="num">1</span>', cls: 'text-danger', sub: L('يجب الإبلاغ خلال ٣٠ دقيقة', 'Must be called within 30 min') }]);
		html += H.notice({ kind: 'danger', icon: 'ki-notification-status', title: L('قيمة حرجة: تروبونين مرتفع', 'Critical value: troponin raised'), text: L('أحمد محمود عبدالله (الطوارئ) — أُرسل تنبيه للطبيب المعالج ولم يُؤكَّد الاستلام بعد.', 'Ahmed Mahmoud Abdullah (ER) — the attending was alerted and has not acknowledged yet.'), badge: '<span class="num">0.84</span> ng/mL' });
		html += (H.labSections ? H.labSections() : '');
		html += H.card({ title: L('قائمة عمل المختبر', 'Laboratory worklist'), toolbar: '<div class="d-flex gap-3">' + H.searchBox() + H.select([{ v: '', t: L('كل الحالات', 'All statuses') }, { v: 'o', t: L('مطلوب', 'Ordered') }, { v: 'p', t: L('قيد التحليل', 'Processing') }], '', 'style="width:170px"') + '</div>',
			body: H.table([{ t: L('الطلب', 'Order'), w: '120px' }, { t: L('المريض', 'Patient') }, { t: L('الفحص', 'Test') }, { t: L('الأولوية', 'Priority') }, { t: L('المصدر', 'From') }, { t: L('الوقت', 'Time') }, { t: L('الحالة', 'Status') }, { t: '', cls: 'text-end', w: '140px' }], rows) });
		return { title: L('المختبر', 'Laboratory'), crumbs: [{ t: L('الخدمات التشخيصية', 'Diagnostics') }, { t: L('المختبر', 'Laboratory') }], actions: H.btn('light-primary', 'ki-setting-3', L('ربط الأجهزة', 'Analyser interfaces')), html: html };
	} });

	H.route('lab-result', { module: 'lab', render: function (id) {
		var o = D.labOrders.filter(function (x) { return x.id === id; })[0] || D.labOrders[0];
		var p = D.patient(o.p);
		var rows = D.cbc.map(function (r) {
			var f = r.v > r.hi ? 'H' : r.v < r.lo ? 'L' : '';
			return ['<span class="fw-bold text-gray-800">' + T(r.t) + '</span>', '<input class="form-control form-control-sm form-control-solid num w-100px" value="' + r.v + '" />', '<span class="text-muted fs-7">' + r.u + '</span>', '<span class="num text-gray-700">' + r.lo + ' – ' + r.hi + '</span>', f ? '<span class="flag-' + f.toLowerCase() + ' num">' + f + '</span>' : '<span class="hms-zero">—</span>', '<span class="num text-muted">' + (r.v * (f === 'H' ? 0.82 : 1.06)).toFixed(1) + '</span>'];
		});
		var html = H.patientBanner(p) + '<div class="row g-5 g-xl-8"><div class="col-xl-8">' + H.card({ title: T(o.test) + ' ' + H.code(o.id), sub: L('العينة: دم كامل EDTA · سُحبت ٠٩:١٨ · الجهاز: محلل الدم ١', 'Specimen: whole blood EDTA · collected 09:18 · analyser: Haematology 1'), body:
			H.table([{ t: L('المؤشر', 'Analyte') }, { t: L('النتيجة', 'Result'), w: '130px' }, { t: L('الوحدة', 'Unit') }, { t: L('المعدل الطبيعي', 'Reference range') }, { t: L('علامة', 'Flag'), cls: 'text-center', w: '70px' }, { t: L('السابقة', 'Previous'), w: '90px' }], rows) +
			'<div class="mt-6">' + H.field(L('تعليق أخصائي المختبر', 'Laboratory comment'), '<textarea class="form-control form-control-solid" rows="2">' + L('ارتفاع كريات الدم البيضاء مع زيادة العدلات — يتوافق مع عدوى بكتيرية.', 'Leucocytosis with neutrophilia — consistent with bacterial infection.') + '</textarea>', 'col-12') + '</div>' }) + '</div><div class="col-xl-4">' +
			H.notice({ icon: 'ki-shield-tick', title: L('اعتماد على مرحلتين', 'Two-step verification'), text: L('الفني يُدخل النتيجة ثم يعتمدها الأخصائي، وبعدها فقط تظهر للطبيب وفي بوابة المريض.', 'A technician enters the result and a specialist verifies it; only then is it visible to the doctor and the patient portal.'), cls: 'mb-5' }) +
			H.card({ title: L('سجل العينة', 'Specimen trail'), body: [[L('طُلب', 'Ordered'), '09:12', D.doc('D2')], [L('سُحبت', 'Collected'), '09:18', D.staff.nurses[1]], [L('استُلمت في المختبر', 'Received in lab'), '09:26', D.staff.nurses[2]]].map(function (e) {
				return '<div class="d-flex align-items-center cb-row py-3">' + H.avatar(e[2], 30) + '<div class="ms-3 flex-grow-1"><div class="fw-semibold text-gray-800">' + e[0] + '</div><div class="text-muted fs-8">' + T(e[2].name) + '</div></div><span class="num text-muted fs-7">' + e[1] + '</span></div>';
			}).join('') }) + '</div></div>';
		return { title: L('إدخال النتيجة', 'Result entry'), crumbs: [{ t: L('المختبر', 'Laboratory'), href: '#/lab' }, { t: o.id }],
			actions: H.btn('light', 'ki-save-2', L('حفظ', 'Save')) + H.btn('primary', 'ki-check-circle', L('اعتماد النتيجة', 'Verify result'), 'onclick="HMS.toast(HMS.L(\'اعتُمدت النتيجة وأُبلغ الطبيب\',\'Result verified and doctor notified\'));location.hash=\'#/lab\'"'), html: html };
	} });

	/* ---------------- Radiology ---------------- */
	H.route('radiology', { module: 'radiology', render: function () {
		var mods = { XR: L('أشعة سينية', 'X-ray'), CT: L('مقطعية', 'CT'), MR: L('رنين', 'MRI'), US: L('موجات صوتية', 'Ultrasound') };
		var rows = D.radOrders.map(function (o) {
			var p = D.patient(o.p);
			return [H.code(o.id), H.personCell(p, p.mrn, '#/patient/' + p.mrn, 35), '<div class="fw-semibold text-gray-800">' + T(o.exam) + '</div><div class="text-muted fs-8">' + mods[o.mod] + ' · <span class="num">' + o.room + '</span></div>', prio(o.prio), H.radStatus(o.st) + (o.contrastAlert ? ' ' + H.badge('danger', L('حساسية صبغة', 'Contrast allergy')) : ''), H.personCell(D.doc(o.doc), null, null, 30)];
		});
		var report = '<div class="row g-5 mb-5">' + H.field(L('القالب', 'Template'), H.select([L('أشعة مقطعية للكلى', 'CT KUB'), L('أشعة صدر', 'Chest X-ray')], ''), 'col-md-6') + H.field(L('النتيجة المختصرة', 'Impression category'), H.select([L('طبيعي', 'Normal'), L('غير طبيعي', 'Abnormal'), L('حرج', 'Critical')], ''), 'col-md-6') + '</div>' +
			'<textarea class="form-control form-control-solid mb-4" rows="7">' + L('الفحص: أشعة مقطعية للكلى والحالبين بدون صبغة.\nالنتائج: حصوة ٦ ملم في الحالب الأيسر البعيد مع توسع خفيف في حوض الكلية.\nالخلاصة: حصوة حالب أيسر ٦ ملم مع انسداد جزئي.', 'Exam: CT KUB, non-contrast.\nFindings: 6 mm calculus in the distal left ureter with mild hydronephrosis.\nImpression: 6 mm left ureteric stone with partial obstruction.') + '</textarea>' +
			'<div class="d-flex gap-2">' + H.btn('light-primary', 'ki-picture', L('فتح الصور في PACS', 'Open images in PACS')) + H.btn('primary', 'ki-check-circle', L('اعتماد التقرير', 'Sign report'), 'onclick="HMS.toast(HMS.L(\'اعتُمد التقرير\',\'Report signed\'))"') + '</div>';
		var html = H.stats(Object.keys(mods).map(function (k) { var n = D.radOrders.filter(function (o) { return o.mod === k; }).length; return { label: mods[k], value: '<span class="num">' + n + '</span>', sub: L('في قائمة اليوم', 'on today\'s list') }; }));
		html += (H.radRooms ? H.radRooms() : '');
		html += H.card({ title: L('قائمة عمل الأشعة', 'Radiology worklist'), toolbar: H.searchBox(), body: H.table([{ t: L('الطلب', 'Order'), w: '120px' }, { t: L('المريض', 'Patient') }, { t: L('الفحص', 'Exam') }, { t: L('الأولوية', 'Priority') }, { t: L('الحالة', 'Status') }, { t: L('الطبيب الطالب', 'Ordering doctor') }], rows) });
		html += H.card({ title: L('كتابة التقرير', 'Reporting'), sub: 'RAD-8815 · ' + T(D.patient('MRN-100329').name), body: report });
		return { title: L('الأشعة', 'Radiology'), crumbs: [{ t: L('الخدمات التشخيصية', 'Diagnostics') }, { t: L('الأشعة', 'Radiology') }], html: html };
	} });

	/* ---------------- Blood bank ---------------- */
	H.route('bloodbank', { module: 'bloodbank', render: function () {
		var groups = [['O+', 24], ['O-', 4], ['A+', 18], ['A-', 3], ['B+', 11], ['B-', 2], ['AB+', 6], ['AB-', 1]];
		var html = '<div class="row g-5 mb-5">' + groups.map(function (g) {
			var low = g[1] < 5;
			return '<div class="col-6 col-md-3 col-xl-3"><div class="card card-flush h-100"><div class="card-body py-5 d-flex align-items-center"><span class="symbol symbol-50px me-4"><span class="symbol-label fw-bold fs-4" style="background:' + (low ? 'var(--bs-light-danger);color:var(--bs-danger)' : '#E7F0FF;color:#0E4A9E') + '">' + g[0] + '</span></span><div><div class="fs-2hx fw-bold text-gray-900 num">' + g[1] + '</div><div class="text-muted fs-8">' + L('وحدة دم كامل/مكثف', 'whole blood / PRBC units') + '</div></div></div></div></div>';
		}).join('') + '</div>';
		html += H.notice({ kind: 'danger', icon: 'ki-drop', title: L('مخزون منخفض', 'Low stock'), text: L('الفصائل السالبة أقل من حد الأمان — يُقترح إطلاق حملة تبرع.', 'Negative groups are below the safety level — a donor drive is suggested.'), badge: '<span class="num">4</span> ' + L('فصائل', 'groups') });
		html += H.card({ title: L('طلبات النقل', 'Transfusion requests'), body: H.table([{ t: L('المريض', 'Patient') }, { t: L('المنتج', 'Product') }, { t: L('الوحدات', 'Units') }, { t: L('التوافق', 'Cross-match') }], [
			[H.personCell(D.patient('MRN-100288'), 'ICU', null, 35), L('كريات حمراء مكثفة', 'Packed red cells'), '<span class="num">2</span>', H.badge('success', L('متوافق', 'Compatible'))],
			[H.personCell(D.patient('MRN-100317'), L('الولادة', 'Maternity'), null, 35), L('بلازما طازجة مجمدة', 'Fresh frozen plasma'), '<span class="num">1</span>', H.badge('primary', L('قيد الفحص', 'In progress'))]]) });
		return { title: L('بنك الدم', 'Blood bank'), crumbs: [{ t: L('الخدمات التشخيصية', 'Diagnostics') }, { t: L('بنك الدم', 'Blood bank') }], html: html };
	} });

	/* ---------------- Pharmacy ---------------- */
	H.route('pharmacy', { module: 'pharmacy', render: function () {
		var src = { op: L('عيادات', 'Outpatient'), ip: L('تنويم', 'Inpatient'), er: L('طوارئ', 'Emergency') };
		var sel = D.rx[1], sp = D.patient(sel.p);
		var queue = D.rx.map(function (r) {
			var p = D.patient(r.p);
			return { cls: r.id === sel.id ? 'bg-light-primary' : '', cells: [H.code(r.id), H.personCell(p, p.mrn, null, 35), H.badge('secondary', src[r.src]), '<span class="num">' + r.items.length + '</span>', H.personCell(D.doc(r.doc), null, null, 30), H.rxStatus(r.st) + (r.interaction ? ' ' + H.badge('danger', L('تداخل', 'Interaction')) : '')] };
		});
		var total = 0;
		var lines = sel.items.map(function (i) {
			var amt = i.qty * i.price; total += amt;
			return ['<div class="fw-semibold text-gray-800">' + T(i.drug) + '</div><div class="text-muted fs-8">' + T(i.sig) + '</div>', H.code(i.batch), H.dateStr(i.exp), '<span class="num">' + i.qty + '</span>', '<span class="num text-gray-600">' + H.fmtNum(i.stock) + '</span>', H.money(amt)];
		});
		var y = D.payer(sp.payer), share = total * y.copay / 100;
		var html = H.stats([
			{ label: L('روشتات بالانتظار', 'Prescriptions waiting'), value: '<span class="num">' + D.rx.filter(function (r) { return r.st !== 'dispensed'; }).length + '</span>' },
			{ label: L('متوسط زمن الصرف', 'Average dispense time'), value: '<span class="num">6</span> ' + L('د', 'm') },
			{ label: L('أصناف قريبة الانتهاء', 'Near-expiry items'), value: '<span class="num">12</span>', sub: L('خلال ٦٠ يوماً', 'within 60 days'), cls: 'text-danger' },
			{ label: L('أصناف تحت حد الطلب', 'Below reorder point'), value: '<span class="num">8</span>', sub: L('أُنشئ طلب شراء تلقائي', 'Auto purchase request raised') }]);
		html += H.card({ title: L('قائمة الصرف', 'Dispensing queue'), toolbar: H.searchBox(), body: H.table([{ t: L('الروشتة', 'Prescription'), w: '120px' }, { t: L('المريض', 'Patient') }, { t: L('المصدر', 'Source') }, { t: L('الأصناف', 'Items') }, { t: L('الطبيب', 'Doctor') }, { t: L('الحالة', 'Status') }], queue) });
		html += '<div class="row g-5 g-xl-8"><div class="col-xl-8">' + H.card({ title: L('صرف ', 'Dispense ') + sel.id, sub: T(sp.name) + ' · ' + H.payerLabel(sp), body:
			H.notice({ kind: 'danger', icon: 'ki-shield-cross', title: L('تنبيه تداخل دوائي', 'Drug interaction alert'), text: L('ديكلوفيناك مع تاريخ قرحة معدية — تم إضافة أوميبرازول كحماية. راجع الطبيب إذا لزم.', 'Diclofenac with a history of peptic ulcer — omeprazole was added for protection. Contact the prescriber if needed.'), badge: L('متوسط', 'Moderate') }) +
			H.table([{ t: L('الصنف', 'Item') }, { t: L('الدفعة', 'Batch') }, { t: L('الصلاحية', 'Expiry') }, { t: L('الكمية', 'Qty'), cls: 'text-end' }, { t: L('الرصيد', 'On hand'), cls: 'text-end' }, { t: L('القيمة', 'Amount'), cls: 'text-end' }], lines,
				{ foot: '<tr class="fw-bolder fs-6"><td class="ps-4" colspan="5" style="color:#145DBF">' + L('حصة المريض (', 'Patient share (') + '<span class="num">' + y.copay + '%</span>)</td><td class="text-end pe-4">' + H.money(share) + '</td></tr>' }) +
			'<div class="d-flex flex-wrap gap-3 align-items-center mt-5">' + H.reuse('المخزون — صرف بالدفعة الأقرب انتهاءً (FEFO)', 'Inventory — first-expiry-first-out issue') + '</div>' }) + '</div><div class="col-xl-4">' +
			H.card({ title: L('عند الصرف', 'On dispensing'), body: ['<i class="ki-outline ki-minus-square fs-3 text-primary me-3"></i>' + L('حركة صرف من مستودع الصيدلية', 'Stock issue from the pharmacy store'), '<i class="ki-outline ki-bill fs-3 text-primary me-3"></i>' + L('سطر على فاتورة الزيارة', 'A line on the encounter bill'), '<i class="ki-outline ki-printer fs-3 text-primary me-3"></i>' + L('ملصق الجرعة من Report Studio', 'Dose label from Report Studio'), '<i class="ki-outline ki-message-text fs-3 text-primary me-3"></i>' + L('رسالة للمريض بطريقة الاستخدام', 'SMS to the patient with directions')].map(function (t) { return '<div class="d-flex align-items-center cb-row py-3 fw-semibold text-gray-800">' + t + '</div>'; }).join('') }) +
			'<button class="btn btn-primary w-100" onclick="HMS.toast(HMS.L(\'تم الصرف وخصم المخزون\',\'Dispensed and stock issued\'))"><i class="ki-outline ki-check fs-4"></i>' + L('تأكيد الصرف', 'Confirm dispensing') + '</button></div></div>';
		return { title: L('الصيدلية', 'Pharmacy'), crumbs: [{ t: L('الصيدلية', 'Pharmacy') }], actions: H.link('light-primary', 'ki-package', L('مخزون الصيدلية', 'Pharmacy stock'), '#/pharmacy'), html: html };
	} });
})();
