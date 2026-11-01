/* Clinical screens: doctor worklist, consultation, emergency, beds, nursing, ICU, theatre,
   telemedicine, dietary and sterile supply. */
(function () {
	'use strict';
	var H = HMS, L = H.L, T = H.T, D = H.data;

	/* ---------------- Doctor worklist ---------------- */
	H.route('opd', { module: 'opd', render: function () {
		var d = D.doc('D1');
		var q = D.appointments.filter(function (a) { return a.d === 'D1'; });
		var st = { booked: H.badge('secondary', L('لم يحضر بعد', 'Not arrived')), arrived: H.badge('info', L('في الانتظار', 'Waiting')), inroom: '<span class="badge hms-badge-solid fs-8">' + L('في الكشف', 'In consultation') + '</span>', done: H.badge('success', L('انتهى', 'Done')), noshow: H.badge('secondary', L('لم يحضر', 'No-show')) };
		var rows = q.map(function (a, i) {
			var p = D.patient(a.p);
			var act = a.st === 'arrived' || a.st === 'inroom' ? '<a href="#/consult/' + p.mrn + '" class="btn btn-sm btn-primary">' + (a.st === 'inroom' ? L('متابعة الكشف', 'Resume') : L('ابدأ الكشف', 'Start')) + '</a>' : '<a href="#/patient/' + p.mrn + '" class="btn btn-sm btn-light-primary">' + L('الملف', 'File') + '</a>';
			return ['<span class="fs-3 fw-bold text-gray-400 num">' + String(i + 1).padStart(2, '0') + '</span>', '<span class="num fw-bold">' + a.t + '</span>', H.personCell(p, H.ageSex(p) + ' · ' + p.mrn, '#/patient/' + p.mrn), H.payerLabel(p),
				a.st === 'arrived' || a.st === 'inroom' ? '<i class="ki-outline ki-check-circle fs-2 text-success"></i>' : '<span class="hms-zero">—</span>',
				a.st === 'arrived' ? '<span class="num fw-bold text-danger">22</span> ' + L('د', 'm') : '<span class="hms-zero">—</span>', st[a.st], act];
		});
		var html = '<div class="card mb-5"><div class="card-body d-flex flex-wrap align-items-center gap-5 py-6">' + H.avatar(d, 60) +
			'<div class="flex-grow-1"><div class="fs-3 fw-bold text-gray-900">' + T(d.name) + '</div><div class="text-muted fw-semibold">' + T(d.title) + ' · ' + L('غرفة ', 'Room ') + '<span class="num">' + d.room + '</span></div></div>' +
			H.stats([{ label: L('في الانتظار', 'Waiting'), value: '<span class="num">1</span>' }, { label: L('تم كشفهم', 'Seen'), value: '<span class="num">2</span>' }, { label: L('متبقٍ', 'Remaining'), value: '<span class="num">3</span>' }], 'col-4').replace('mb-5', 'mb-0 flex-grow-1') + '</div></div>';
		html += H.card({ title: L('قائمة اليوم', 'Today\'s list'), sub: L('العلامات الحيوية يسجلها التمريض قبل الدخول', 'Vitals are taken by nursing before the patient goes in'), toolbar: H.searchBox(), body: H.table(
			[{ t: '#', w: '50px' }, { t: L('الموعد', 'Time'), w: '80px' }, { t: L('المريض', 'Patient') }, { t: L('الجهة الدافعة', 'Payer') }, { t: L('العلامات الحيوية', 'Vitals'), cls: 'text-center', w: '110px' }, { t: L('الانتظار', 'Wait'), w: '90px' }, { t: L('الحالة', 'Status') }, { t: '', cls: 'text-end', w: '140px' }], rows) });
		return { title: L('قائمة الطبيب', 'Doctor worklist'), crumbs: [{ t: L('العيادات', 'Clinics') }, { t: L('قائمة الطبيب', 'Worklist') }], actions: H.select(D.staff.doctors.map(function (x) { return { v: x.id, t: T(x.name) }; }), 'D1', 'style="width:220px"'), html: html };
	} });

	/* ---------------- Consultation ---------------- */
	H.route('consult', { module: 'opd', render: function (mrn) {
		var p = D.patient(mrn || 'MRN-100245');
		var y = D.payer(p.payer);
		var orders = [['LAB-CBC', L('صورة دم كاملة', 'Complete blood count'), 85, true], ['LAB-HBA1C', L('سكر تراكمي', 'HbA1c'), 120, true], ['LAB-TSH', L('هرمون الغدة الدرقية', 'TSH'), 140, false], ['RAD-CXR', L('أشعة صدر', 'Chest X-ray'), 180, false], ['RAD-USAB', L('موجات صوتية للبطن', 'Abdominal ultrasound'), 450, false], ['PROC-ECG', L('رسم قلب', 'ECG'), 120, false]];
		var soap = '<div class="row g-6">' +
			H.field(L('الشكوى الرئيسية', 'Chief complaint'), H.input(L('إرهاق وعطش متكرر منذ ٣ أسابيع', 'Fatigue and frequent thirst for 3 weeks')), 'col-12') +
			H.field(L('التاريخ المرضي (S)', 'History (S)'), '<textarea class="form-control form-control-solid" rows="3">' + L('تعب عام، كثرة التبول ليلاً، لا يوجد ألم صدري.', 'General fatigue, nocturia, no chest pain.') + '</textarea>') +
			H.field(L('الفحص السريري (O)', 'Examination (O)'), '<textarea class="form-control form-control-solid" rows="3">' + L('واعية، العلامات الحيوية مستقرة، لا وذمة.', 'Alert, vitals stable, no oedema.') + '</textarea>') +
			'</div><div class="row g-4 mt-2">' + [['BP', '128/82'], ['HR', '84'], ['T °C', '36.9'], ['SpO₂', '98%'], [L('الوزن', 'Wt') + ' kg', '71'], ['BMI', '26.1']].map(function (v) {
				return '<div class="col-4 col-md-2"><label class="fw-semibold fs-8 text-muted mb-1 d-block">' + v[0] + '</label>' + H.input(v[1], 'class="num form-control-sm"') + '</div>';
			}).join('') + '</div>';
		var dx = '<div class="position-relative mb-4"><i class="ki-outline ki-magnifier fs-3 position-absolute top-50 translate-middle-y ms-4"></i><input class="form-control form-control-solid ps-12" placeholder="' + L('ابحث بالرمز أو الوصف...', 'Search code or description...') + '" /></div>' +
			D.icd.slice(1, 3).map(function (c, i) { return '<div class="d-flex align-items-center cb-row py-3">' + H.code(c.c) + '<span class="ms-3 fw-semibold text-gray-800 flex-grow-1">' + T(c.t) + '</span>' + (i === 0 ? H.badge('primary', L('رئيسي', 'Primary')) : H.badge('secondary', L('ثانوي', 'Secondary'))) + '</div>'; }).join('') +
			'<div class="text-muted fs-8 mt-3">' + L('نظام الترميز: ', 'Coding system: ') + '<span class="num">' + H.country().coding + '</span></div>';
		var ord = H.table([{ t: '', w: '40px' }, { t: L('الخدمة', 'Service') }, { t: L('السعر', 'Price'), cls: 'text-end' }, { t: L('التغطية', 'Coverage') }], orders.map(function (o) {
			return ['<input type="checkbox" class="form-check-input" ' + (o[3] ? 'checked' : '') + ' />', '<div class="fw-semibold text-gray-800">' + o[1] + '</div><div class="text-muted fs-8 num">' + o[0] + '</div>', H.money(o[2]), y.kind === 'cash' ? H.badge('secondary', L('نقدي', 'Cash')) : o[0] === 'LAB-TSH' ? H.badge('danger', L('يحتاج موافقة', 'Needs approval')) : H.badge('success', L('مغطى ', 'Covered ') + (100 - y.copay) + '%')];
		}));
		var rx = H.table([{ t: L('الدواء', 'Drug') }, { t: L('الجرعة والمدة', 'Dose & duration') }, { t: L('الكمية', 'Qty'), cls: 'text-end', w: '70px' }, { t: L('المخزون', 'Stock'), w: '130px' }, { t: '', w: '40px' }], [
			['<div class="fw-semibold text-gray-800">' + L('ميتفورمين ٥٠٠ ملجم', 'Metformin 500 mg') + '</div>', L('قرص مرتين يومياً بعد الأكل · ٣٠ يوم', '1 tab bid after meals · 30 days'), '<span class="num">60</span>', H.badge('success', '<span class="num">1,240</span> ' + L('متاح', 'in stock')), '<i class="ki-outline ki-trash fs-4 text-gray-400"></i>'],
			['<div class="fw-semibold text-gray-800">' + L('فيتامين د ٥٠٠٠٠ وحدة', 'Vitamin D 50,000 IU') + '</div>', L('كبسولة أسبوعياً · ٨ أسابيع', '1 cap weekly · 8 weeks'), '<span class="num">8</span>', H.badge('danger', '<span class="num">6</span> ' + L('فقط', 'only')), '<i class="ki-outline ki-trash fs-4 text-gray-400"></i>']
		]) + '<div class="d-flex flex-wrap gap-2 mt-4 align-items-center">' + H.btn('light-primary', 'ki-plus', L('إضافة دواء', 'Add drug')) + '<span class="ms-auto">' + H.reuse('رصيد المخزون لحظياً', 'Live inventory balance') + '</span></div>' +
			'<div class="text-muted fs-8 mt-3">' + L('الوصفة الإلكترونية: ', 'e-Prescription: ') + T(H.country().erx) + '</div>';
		var side = H.card({ title: L('ملخص سريع', 'At a glance'), body:
				'<div class="mb-4"><div class="text-muted fs-7 fw-semibold mb-1">' + L('آخر زيارة', 'Last visit') + '</div><div class="fw-bold text-gray-800">' + H.dateStr('2026-08-02') + ' · ' + T(D.departments.ER) + '</div></div>' +
				'<div class="mb-4"><div class="text-muted fs-7 fw-semibold mb-1">' + L('آخر نتيجة سكر تراكمي', 'Last HbA1c') + '</div><div class="fw-bold text-danger"><span class="num">7.9%</span> <span class="text-muted fs-8 fw-semibold">' + H.dateStr('2026-05-19') + '</span></div></div>' +
				'<div><div class="text-muted fs-7 fw-semibold mb-1">' + L('الأمراض المزمنة', 'Chronic conditions') + '</div><div class="fw-bold text-gray-800">' + (p.chronic.length ? p.chronic.map(T).join(L('، ', ', ')) : L('لا توجد', 'None')) + '</div></div>' }) +
			H.card({ title: L('التحويل والمتابعة', 'Referral & follow-up'), body: '<div class="row g-4">' + H.field(L('موعد متابعة بعد', 'Follow-up in'), H.select([L('أسبوعين', '2 weeks'), L('شهر', '1 month'), L('٣ أشهر', '3 months')], ''), 'col-12') + H.field(L('تحويل إلى', 'Refer to'), H.select([{ v: '', t: '—' }].concat(Object.keys(D.departments).map(function (k) { return { v: k, t: T(D.departments[k]) }; })), ''), 'col-12') + '</div>' +
				'<label class="form-check form-check-custom form-check-solid mt-5"><input class="form-check-input" type="checkbox" checked /><span class="form-check-label text-gray-700">' + L('إنشاء مهمة متابعة للتمريض', 'Create a nursing follow-up task') + '</span></label><div class="mt-3">' + H.reuse('المهام', 'Tasks') + '</div>' }) +
			H.notice({ icon: 'ki-bill', title: L('عند التوقيع', 'On signing'), text: L('تُنشأ رسوم الكشف والطلبات تلقائياً في فاتورة الزيارة وتُرسل الطلبات للمختبر والأشعة والصيدلية.', 'Consultation and order charges post to the encounter bill and orders go to lab, radiology and pharmacy.'), cls: 'mb-5' });
		var html = H.patientBanner(p) + '<div class="row g-5 g-xl-8"><div class="col-xl-8">' +
			H.card({ title: L('التوثيق السريري', 'Clinical note'), sub: 'SOAP', body: soap }) +
			H.card({ title: L('التشخيص (A)', 'Assessment (A)'), body: dx }) +
			H.card({ title: L('الطلبات (P)', 'Orders (P)'), sub: L('التغطية تُحسب من عقد الجهة الدافعة', 'Coverage comes from the payer contract'), body: ord }) +
			H.card({ title: L('الروشتة', 'Prescription'), body: rx }) +
			'</div><div class="col-xl-4">' + side + '</div></div>';
		return { title: L('الكشف', 'Consultation'), crumbs: [{ t: L('العيادات', 'Clinics'), href: '#/opd' }, { t: T(p.name) }],
			actions: H.btn('light', 'ki-save-2', L('حفظ كمسودة', 'Save draft'), 'onclick="HMS.toast(HMS.L(\'تم الحفظ\',\'Draft saved\'),\'info\')"') + H.btn('primary', 'ki-check-square', L('توقيع وإغلاق الزيارة', 'Sign & close visit'), 'onclick="HMS.toast(HMS.L(\'تم التوقيع — أُرسلت ٢ طلبات وروشتة\',\'Signed — 2 orders and 1 prescription sent\'));location.hash=\'#/opd\'"'), html: html };
	} });

	/* ---------------- Emergency ---------------- */
	H.route('er', { module: 'er', render: function () {
		var lanes = [['triage', L('الفرز', 'Triage')], ['waiting', L('انتظار الطبيب', 'Waiting for doctor')], ['treatment', L('تحت العلاج', 'In treatment')], ['observation', L('ملاحظة', 'Observation')], ['disposition', L('القرار', 'Disposition')]];
		var board = '<div class="row g-4 flex-nowrap overflow-auto pb-3">' + lanes.map(function (ln) {
			var items = D.er.filter(function (e) { return e.stage === ln[0]; }).sort(function (a, b) { return a.esi - b.esi; });
			return '<div class="col" style="min-width:200px"><div class="hms-lane"><div class="d-flex flex-stack mb-4"><span class="fw-bold text-gray-800">' + ln[1] + '</span><span class="badge badge-light-primary num">' + items.length + '</span></div>' +
				items.map(function (e) {
					var p = D.patient(e.p);
					return '<div class="hms-lane-card esi-' + e.esi + ' cursor-pointer" onclick="location.hash=\'#/patient/' + p.mrn + '\'"><div class="d-flex flex-stack mb-3"><span class="badge badge-esi-' + e.esi + ' fw-bold">ESI ' + e.esi + '</span><span class="text-muted fs-8"><i class="ki-outline ki-time fs-8"></i> <span class="num fw-bold' + (e.mins > 45 && e.stage === 'waiting' ? ' text-danger' : '') + '">' + e.mins + '</span> ' + L('د', 'm') + '</span></div>' +
						H.personCell(p, H.ageSex(p), null, 35) + '<div class="fw-semibold text-gray-800 mt-3 fs-7">' + T(e.cc) + '</div><div class="text-muted fs-8 num mt-1">' + e.vitals + '</div>' +
						'<div class="d-flex flex-stack mt-3 pt-3 border-top border-gray-200"><span class="fs-8 text-muted">' + L('سرير ', 'Bay ') + '<span class="num fw-bold text-gray-700">' + e.bay + '</span></span>' + (e.doc ? H.avatar(D.doc(e.doc), 25) : '<span class="fs-8 text-muted">' + L('بدون طبيب', 'Unassigned') + '</span>') + '</div></div>';
				}).join('') + '</div></div>';
		}).join('') + '</div>';
		var html = H.stats([
			{ label: L('في القسم الآن', 'In department now'), value: '<span class="num">' + D.er.length + '</span>', sub: L('سعة ', 'Capacity ') + '<span class="num">14</span>' },
			{ label: L('متوسط الوصول للطبيب', 'Door-to-doctor'), value: '<span class="num">18</span> ' + L('د', 'm'), sub: L('الهدف ', 'Target ') + '<span class="num">30</span> ' + L('د', 'm') },
			{ label: L('أطول انتظار', 'Longest wait'), value: '<span class="num">52</span> ' + L('د', 'm'), sub: 'ESI 3', cls: 'text-danger' },
			{ label: L('غادر دون رؤية الطبيب', 'Left without being seen'), value: '<span class="num">1.4%</span>', sub: L('آخر ٣٠ يوماً', 'Last 30 days') }]);
		html += H.notice({ kind: 'danger', icon: 'ki-notification-status', title: L('إنعاش — سرير R1', 'Resuscitation — bay R1'), text: L('دانة الصالح: تشبع الأكسجين ٨٦٪، تم استدعاء فريق القلب والعناية المركزة.', 'Dana Alsaleh: SpO₂ 86%, cardiology and ICU teams paged.'), badge: 'ESI 1' });
		html += H.card({ title: L('لوحة الطوارئ', 'Emergency board'), sub: L('مقياس الفرز ESI من ١ (الأخطر) إلى ٥', 'ESI triage scale from 1 (most urgent) to 5'), body: board });
		return { title: L('الطوارئ', 'Emergency'), crumbs: [{ t: L('الطوارئ', 'Emergency') }], actions: H.btn('primary', 'ki-plus', L('وصول حالة جديدة', 'New arrival'), 'onclick="HMS.erArrival()"'), html: html };
	} });
	H.erArrival = function () {
		H.modal(L('تسجيل وصول للطوارئ', 'Emergency arrival'), '<div class="row g-5">' +
			H.field(L('المريض', 'Patient'), H.select([{ v: '', t: L('مريض غير معروف (تسجيل سريع)', 'Unknown patient (quick register)') }].concat(D.patients.slice(0, 12).map(function (p) { return { v: p.mrn, t: T(p.name) }; })), '')) +
			H.field(L('طريقة الوصول', 'Arrival mode'), H.select([L('مشياً', 'Walk-in'), L('إسعاف', 'Ambulance'), L('تحويل', 'Referral')], '')) +
			H.field(L('الشكوى', 'Complaint'), H.input(''), 'col-12') +
			'<div class="col-12"><label class="fw-semibold fs-7 text-gray-700 mb-2">' + L('درجة الفرز', 'Triage level') + '</label><div class="d-flex gap-2">' + [1, 2, 3, 4, 5].map(function (n) { return '<label class="flex-fill"><input type="radio" class="btn-check" name="esi" ' + (n === 3 ? 'checked' : '') + ' /><span class="btn btn-sm w-100 badge-esi-' + n + ' fw-bold">ESI ' + n + '</span></label>'; }).join('') + '</div></div></div>',
			'<button class="btn btn-light" data-bs-dismiss="modal">' + L('إلغاء', 'Cancel') + '</button><button class="btn btn-primary" data-bs-dismiss="modal" onclick="HMS.toast(HMS.L(\'أُضيفت الحالة للوحة\',\'Added to the board\'))">' + L('تسجيل', 'Register') + '</button>');
	};

	/* ---------------- Beds ---------------- */
	var bedTxt = function (s) { return { occupied: L('مشغول', 'Occupied'), vacant: L('متاح', 'Vacant'), cleaning: L('تنظيف', 'Cleaning'), reserved: L('محجوز', 'Reserved'), blocked: L('مغلق', 'Blocked') }[s]; };
	H.route('beds', { module: 'inpatient', render: function () {
		var all = D.wards.reduce(function (a, w) { return a.concat(w.beds); }, []);
		var count = function (s) { return all.filter(function (b) { return b.st === s; }).length; };
		var html = H.stats([
			{ label: L('إجمالي الأسرة', 'Total beds'), value: '<span class="num">' + all.length + '</span>', sub: '<span class="num">' + D.wards.length + '</span> ' + L('أجنحة', 'wards') },
			{ label: L('مشغول', 'Occupied'), value: '<span class="num">' + count('occupied') + '</span>', sub: '<span class="num">' + Math.round(count('occupied') * 100 / all.length) + '%</span> ' + L('إشغال', 'occupancy') },
			{ label: L('متاح الآن', 'Available now'), value: '<span class="num">' + count('vacant') + '</span>', sub: '<span class="num">' + count('cleaning') + '</span> ' + L('قيد التنظيف', 'being cleaned'), cls: 'text-success' },
			{ label: L('خروج متوقع اليوم', 'Expected discharges today'), value: '<span class="num">4</span>', sub: L('قبل الساعة ١٢ ظهراً: ', 'Before noon: ') + '<span class="num">1</span>' }]);
		var legend = ['occupied', 'vacant', 'cleaning', 'reserved', 'blocked'].map(function (s) { return '<span class="d-inline-flex align-items-center gap-2 me-5 fs-7 fw-semibold text-gray-700"><span class="hms-bed ' + s + '" style="min-height:0;width:16px;height:16px;padding:0;cursor:default"></span>' + bedTxt(s) + '</span>'; }).join('');
		html += '<div class="mb-5">' + legend + '</div>';
		D.wards.forEach(function (w) {
			if (w.id === 'ICU' && !H.isOn('icu')) return;
			html += H.card({ title: T(w.name), sub: '<span class="num">' + w.beds.filter(function (b) { return b.st === 'occupied'; }).length + ' / ' + w.beds.length + '</span> ' + L('مشغول', 'occupied'), body: '<div class="row g-4">' + w.beds.map(function (b) {
				var inner = '<div class="d-flex flex-stack mb-2"><span class="fw-bold fs-5 num">' + b.no + '</span><span class="fs-8 fw-bold text-gray-600">' + bedTxt(b.st) + '</span></div>';
				if (b.p) {
					var p = D.patient(b.p);
					inner += '<div class="d-flex align-items-center">' + H.avatar(p, 30) + '<div class="ms-2 min-w-0"><div class="fw-bold fs-7 text-gray-900 text-truncate">' + T(p.name) + '</div><div class="text-muted fs-8 text-truncate">' + (b.dx ? T(b.dx) : L('بانتظار الوصول', 'Awaiting arrival')) + '</div></div></div>' + (b.los ? '<div class="text-muted fs-8 mt-2">' + L('يوم الإقامة ', 'Day ') + '<span class="num fw-bold">' + b.los + '</span></div>' : '');
				} else inner += '<div class="text-muted fs-8 mt-4">' + (b.st === 'vacant' ? L('جاهز للاستقبال', 'Ready to receive') : b.st === 'cleaning' ? L('بانتظار خدمات النظافة', 'Waiting for housekeeping') : L('صيانة', 'Maintenance')) + '</div>';
				return '<div class="col-6 col-md-4 col-xl-2"><div class="hms-bed ' + b.st + '" onclick="HMS.bedAction(\'' + w.id + '\',\'' + b.no + '\')">' + inner + '</div></div>';
			}).join('') + '</div>' });
		});
		return { title: L('إدارة الأسرة', 'Bed management'), crumbs: [{ t: L('التنويم', 'Inpatient') }, { t: L('الأسرة', 'Beds') }], actions: H.btn('primary', 'ki-entrance-left', L('طلب دخول', 'Admission request'), 'onclick="HMS.admit()"'), html: html };
	} });
	H.bedAction = function (wid, no) {
		var w = D.wards.filter(function (x) { return x.id === wid; })[0], b = w.beds.filter(function (x) { return x.no === no; })[0];
		if (!b.p) {
			H.modal(L('سرير ', 'Bed ') + no + ' — ' + bedTxt(b.st), H.notice({ icon: 'ki-home-3', title: T(w.name), text: b.st === 'vacant' ? L('السرير جاهز. اختر مريضاً من طلبات الدخول المعلقة لإسكانه.', 'The bed is ready. Pick a patient from pending admission requests.') : L('السرير غير متاح للإسكان حالياً.', 'The bed cannot take a patient right now.'), badge: bedTxt(b.st), cls: 'mb-0' }),
				b.st === 'vacant' ? '<button class="btn btn-light" data-bs-dismiss="modal">' + L('إغلاق', 'Close') + '</button><button class="btn btn-primary" data-bs-dismiss="modal" onclick="HMS.admit()">' + L('إسكان مريض', 'Assign a patient') + '</button>' : null);
			return;
		}
		var p = D.patient(b.p), d = b.doc ? D.doc(b.doc) : null;
		H.modal(L('سرير ', 'Bed ') + no, '<div class="d-flex align-items-center mb-6">' + H.personCell(p, p.mrn + ' · ' + H.ageSex(p), '#/patient/' + p.mrn, 60) + '</div><div class="row g-5 mb-6">' +
			[[L('التشخيص', 'Diagnosis'), b.dx ? T(b.dx) : '—'], [L('يوم الإقامة', 'Length of stay'), b.los ? '<span class="num">' + b.los + '</span>' : '—'], [L('الجهة الدافعة', 'Payer'), H.payerLabel(p)]].map(function (f) { return '<div class="col-4"><div class="text-muted fs-7 fw-semibold">' + f[0] + '</div><div class="fw-bold text-gray-900">' + f[1] + '</div></div>'; }).join('') + '</div>' +
			(d ? '<div class="mb-2 text-muted fs-7 fw-semibold">' + L('الطبيب المعالج', 'Attending') + '</div>' + H.personCell(d, T(d.title)) : ''),
			'<button class="btn btn-light" data-bs-dismiss="modal">' + L('إغلاق', 'Close') + '</button><button class="btn btn-light-primary" data-bs-dismiss="modal" onclick="HMS.toast(HMS.L(\'أُرسل طلب التحويل\',\'Transfer request sent\'),\'info\')"><i class="ki-outline ki-arrows-loop fs-4"></i>' + L('تحويل', 'Transfer') + '</button><button class="btn btn-primary" data-bs-dismiss="modal" onclick="HMS.toast(HMS.L(\'بدأ إجراء الخروج — أُبلغت الفوترة والصيدلية\',\'Discharge started — billing and pharmacy notified\'))"><i class="ki-outline ki-exit-right fs-4"></i>' + L('خروج', 'Discharge') + '</button>');
	};
	H.admit = function () {
		H.modal(L('طلب دخول', 'Admission request'), '<div class="row g-5">' +
			H.field(L('المريض', 'Patient'), H.select(D.patients.slice(0, 12).map(function (p) { return { v: p.mrn, t: T(p.name) + ' — ' + p.mrn }; }), 'MRN-100371')) +
			H.field(L('الطبيب المعالج', 'Attending doctor'), H.select(D.staff.doctors.map(function (d) { return { v: d.id, t: T(d.name) }; }), 'D1')) +
			H.field(L('الجناح', 'Ward'), H.select(D.wards.map(function (w) { return { v: w.id, t: T(w.name) }; }), 'W3')) +
			H.field(L('السرير', 'Bed'), H.select(['303', '308'], '303')) +
			H.field(L('سبب الدخول', 'Reason for admission'), H.input(L('التهاب بنكرياس حاد للملاحظة', 'Acute pancreatitis for observation')), 'col-12') + '</div>' +
			H.notice({ icon: 'ki-shield-tick', title: L('موافقة التأمين', 'Insurance approval'), text: L('سيُرسل طلب موافقة مسبقة للدخول تلقائياً إلى الجهة الدافعة.', 'A pre-authorisation for the admission is sent to the payer automatically.'), cls: 'mt-6 mb-0' }),
			'<button class="btn btn-light" data-bs-dismiss="modal">' + L('إلغاء', 'Cancel') + '</button><button class="btn btn-primary" data-bs-dismiss="modal" onclick="HMS.toast(HMS.L(\'تم الدخول — سرير ٣٠٣\',\'Admitted — bed 303\'))">' + L('تأكيد الدخول', 'Confirm admission') + '</button>');
	};

	/* ---------------- Nursing station ---------------- */
	H.route('nursing', { module: 'nursing', render: function () {
		var hours = ['06', '10', '14', '18', '22'];
		var mark = { g: ['mar-given', '✓'], d: ['mar-due', '•'], m: ['mar-missed', '!'], h: ['mar-held', '—'] };
		var rows = D.mar.map(function (m) {
			var p = D.patient(m.p);
			return [H.personCell(p, L('سرير ', 'Bed ') + '<span class="num">' + m.bed + '</span>', null, 35), '<div class="fw-semibold text-gray-800">' + T(m.drug) + '</div><div class="text-muted fs-8">' + T(m.freq) + '</div>'].concat(hours.map(function (h) {
				var c = m.cells[h]; return c ? '<span class="mar-dot ' + mark[c][0] + ' cursor-pointer" onclick="HMS.toast(HMS.L(\'تم تسجيل الإعطاء\',\'Administration recorded\'))">' + mark[c][1] + '</span>' : '';
			}));
		});
		var nurse = D.staff.nurses[0];
		var html = '<div class="card mb-5"><div class="card-body d-flex flex-wrap align-items-center gap-5 py-6">' + H.avatar(nurse, 60) + '<div class="flex-grow-1"><div class="fs-3 fw-bold text-gray-900">' + T(nurse.name) + '</div><div class="text-muted fw-semibold">' + T(nurse.title) + ' · ' + L('المناوبة الصباحية ٠٧:٠٠–١٥:٠٠', 'Day shift 07:00–15:00') + '</div></div><div class="d-flex">' + D.staff.nurses.slice(1).map(function (n) { return '<div class="ms-n3" data-bs-toggle="tooltip" title="' + T(n.name) + '">' + H.avatar(n, 40) + '</div>'; }).join('') + '</div></div></div>';
		html += H.stats([
			{ label: L('أدوية مستحقة الآن', 'Doses due now'), value: '<span class="num">5</span>', sub: L('خلال الساعة القادمة', 'within the next hour') },
			{ label: L('جرعات فائتة', 'Missed doses'), value: '<span class="num">1</span>', sub: L('تحتاج توثيق السبب', 'Need a documented reason'), cls: 'text-danger' },
			{ label: L('علامات حيوية مستحقة', 'Vitals due'), value: '<span class="num">3</span>', sub: L('كل ٤ ساعات', 'every 4 hours') },
			{ label: L('تقييم خطر السقوط', 'Fall-risk reviews'), value: '<span class="num">2</span>', sub: L('مرضى بخطر مرتفع', 'high-risk patients') }]);
		html += H.card({ title: L('سجل إعطاء الأدوية — جناح ٣', 'Medication administration record — Ward 3'), sub: L('اضغط الجرعة المستحقة لتسجيل الإعطاء بمسح سوار المريض', 'Click a due dose to record it after scanning the patient wristband'),
			toolbar: '<div class="d-flex gap-4 fs-8 fw-semibold text-gray-700">' + [['mar-given', '✓', L('أُعطيت', 'Given')], ['mar-due', '•', L('مستحقة', 'Due')], ['mar-missed', '!', L('فائتة', 'Missed')], ['mar-held', '—', L('موقوفة', 'Held')]].map(function (x) { return '<span class="d-inline-flex align-items-center gap-1"><span class="mar-dot ' + x[0] + '" style="width:20px;height:20px">' + x[1] + '</span>' + x[2] + '</span>'; }).join('') + '</div>',
			body: H.table([{ t: L('المريض', 'Patient') }, { t: L('الدواء', 'Medication') }].concat(hours.map(function (h) { return { t: '<span class="num">' + h + ':00</span>', cls: 'mar-cell' }; })), rows) });
		html += '<div class="row g-5"><div class="col-xl-6">' + H.card({ title: L('مهام التمريض', 'Nursing tasks'), body: [[L('تغيير ضماد — سرير ٣٠٢', 'Dressing change — bed 302'), '11:00'], [L('سحب عينة دم — سرير ٣٠٥', 'Blood draw — bed 305'), '11:30'], [L('تعليم المريض عن الإنسولين — سرير ٣٠١', 'Insulin teaching — bed 301'), '13:00']].map(function (t) {
			return '<label class="d-flex align-items-center cb-row py-3"><input type="checkbox" class="form-check-input me-3" /><span class="fw-semibold text-gray-800 flex-grow-1">' + t[0] + '</span><span class="num text-muted fs-7">' + t[1] + '</span></label>';
		}).join('') + '<div class="mt-4">' + H.reuse('المهام', 'Tasks') + '</div>' }) + '</div><div class="col-xl-6">' +
			H.card({ title: L('تسليم المناوبة', 'Shift handover'), body: '<textarea class="form-control form-control-solid" rows="5">' + L('سرير ٣٠٥: جرعة الفوروسيميد ٦ صباحاً فاتت بسبب انخفاض الضغط — أُبلغ الطبيب.', 'Bed 305: 06:00 furosemide missed due to low BP — doctor informed.') + '</textarea>' }) + '</div></div>';
		return { title: L('محطة التمريض', 'Nursing station'), crumbs: [{ t: L('التنويم', 'Inpatient') }, { t: L('التمريض', 'Nursing') }], actions: H.select(D.wards.map(function (w) { return { v: w.id, t: T(w.name) }; }), 'W3', 'style="width:240px"'), html: html };
	} });

	/* ---------------- ICU ---------------- */
	H.route('icu', { module: 'icu', render: function () {
		var p = D.patient('MRN-100288');
		var hrs = ['08', '09', '10', '11', '12'];
		var rows = [['HR', [128, 122, 115, 108, 102], 'bpm'], ['MAP', [62, 64, 68, 71, 73], 'mmHg'], ['SpO₂', [88, 91, 93, 94, 95], '%'], ['RR', [28, 26, 24, 22, 20], '/min'], [L('حرارة', 'Temp'), [38.2, 38.0, 37.8, 37.6, 37.5], '°C'], [L('الإخراج البولي', 'Urine output'), [20, 35, 40, 45, 50], 'ml']];
		var html = H.patientBanner(p) + H.stats([
			{ label: L('أسرة العناية', 'ICU beds'), value: '<span class="num">3 / 4</span>', sub: L('مشغول', 'occupied') },
			{ label: L('على جهاز تنفس', 'Ventilated'), value: '<span class="num">1</span>' },
			{ label: L('على أدوية رافعة للضغط', 'On vasopressors'), value: '<span class="num">1</span>' },
			{ label: 'SOFA', value: '<span class="num">7</span>', sub: L('تحسن من ٩ أمس', 'Improved from 9 yesterday') }]);
		html += H.card({ title: L('المخطط الساعي', 'Hourly flowsheet'), sub: L('القيم تُسحب من أجهزة المراقبة عند ربطها', 'Values stream from bedside monitors once connected'), body: H.table([{ t: L('المؤشر', 'Parameter') }].concat(hrs.map(function (h) { return { t: '<span class="num">' + h + ':00</span>', cls: 'text-center' }; })).concat([{ t: L('الوحدة', 'Unit') }]),
			rows.map(function (r) { return ['<span class="fw-bold text-gray-800">' + r[0] + '</span>'].concat(r[1].map(function (v) { return '<span class="num">' + v + '</span>'; })).concat(['<span class="text-muted fs-8">' + r[2] + '</span>']); })) });
		return { title: L('العناية المركزة', 'Intensive care'), crumbs: [{ t: L('التنويم', 'Inpatient') }, { t: L('العناية المركزة', 'ICU') }], html: html };
	} });

	/* ---------------- Theatre ---------------- */
	H.route('or', { module: 'or', render: function () {
		var from = 8, to = 16, span = to - from;
		var hours = []; for (var h = from; h < to; h++) hours.push(h);
		var grid = '<div class="hms-sched" style="grid-template-columns:140px 1fr;">';
		grid += '<div class="hd"></div><div class="hd d-flex">' + hours.map(function (h) { return '<div class="flex-fill num text-center fs-7 text-gray-700">' + String(h).padStart(2, '0') + ':00</div>'; }).join('') + '</div>';
		D.theatres.forEach(function (room, i) {
			grid += '<div class="fw-bold text-gray-800 d-flex align-items-center">' + T(room) + '</div><div style="min-height:74px">';
			D.surgeries.filter(function (s) { return s.room === i; }).forEach(function (s) {
				var startPct = (s.start - from) * 100 / span, w = s.dur * 100 / span;
				grid += '<div class="hms-or-block ' + s.st + '" style="inset-inline-start:' + startPct + '%;width:' + w + '%" title="' + H.esc(T(s.proc)) + '"><div class="fw-bold text-truncate">' + T(s.proc) + '</div><div class="fs-8 text-truncate opacity-75">' + T(D.patient(s.p).name) + '</div></div>';
			});
			grid += '</div>';
		});
		grid += '</div>';
		var live = D.surgeries.filter(function (s) { return s.st === 'live'; })[0];
		var lp = D.patient(live.p);
		var check = [[L('تأكيد هوية المريض والإجراء والموضع', 'Patient, procedure and site confirmed'), true], [L('الموافقة موقعة', 'Consent signed'), true], [L('فحص جهاز التخدير', 'Anaesthesia machine check'), true], [L('تأكيد الحساسية', 'Allergies confirmed'), true], [L('عدّ الأدوات قبل الإغلاق', 'Instrument count before closure'), false], [L('توثيق العينات', 'Specimens labelled'), false]];
		var html = H.stats([
			{ label: L('عمليات اليوم', 'Cases today'), value: '<span class="num">' + D.surgeries.length + '</span>' },
			{ label: L('منتهية', 'Completed'), value: '<span class="num">' + D.surgeries.filter(function (s) { return s.st === 'done'; }).length + '</span>', cls: 'text-success' },
			{ label: L('استغلال الغرف', 'Theatre utilisation'), value: '<span class="num">68%</span>', sub: L('الهدف ٧٥٪', 'Target 75%') },
			{ label: L('بدء أول عملية في موعدها', 'First-case on-time start'), value: '<span class="num">3 / 4</span>' }]);
		html += H.card({ title: L('جدول غرف العمليات', 'Theatre schedule'), sub: L('الأحد ١ نوفمبر', 'Sunday 1 November'), body: '<div class="overflow-auto"><div style="min-width:820px">' + grid + '</div></div>' });
		html += '<div class="row g-5"><div class="col-xl-7">' + H.card({ title: L('العملية الجارية', 'Case in progress'), sub: T(D.theatres[live.room]), body:
			'<div class="d-flex flex-wrap align-items-center gap-6 mb-6">' + H.personCell(lp, lp.mrn, '#/patient/' + lp.mrn, 50) + '<div><div class="text-muted fs-7 fw-semibold">' + L('الإجراء', 'Procedure') + '</div><div class="fw-bold text-gray-900">' + T(live.proc) + ' ' + H.code('CPT ' + live.cpt) + '</div></div></div>' +
			'<div class="row g-5">' + [[L('الجراح', 'Surgeon'), D.doc(live.surgeon)], [L('طبيب التخدير', 'Anaesthetist'), D.doc(live.anes)]].map(function (x) { return '<div class="col-md-6"><div class="text-muted fs-7 fw-semibold mb-2">' + x[0] + '</div>' + H.personCell(x[1], T(x[1].title)) + '</div>'; }).join('') + '</div>' +
			'<div class="mt-6">' + H.reuse('تفكيك المكونات (BOM) لخصم طقم العملية من المخزون', 'BOM explosion to consume the procedure kit from inventory') + '</div>' }) + '</div><div class="col-xl-5">' +
			H.card({ title: L('قائمة السلامة الجراحية', 'Surgical safety checklist'), sub: L('وفق منظمة الصحة العالمية', 'WHO checklist'), body: check.map(function (c) {
				return '<label class="d-flex align-items-center cb-row py-3"><input type="checkbox" class="form-check-input me-3" ' + (c[1] ? 'checked' : '') + ' /><span class="fw-semibold ' + (c[1] ? 'text-gray-800' : 'text-gray-600') + '">' + c[0] + '</span></label>';
			}).join('') }) + '</div></div>';
		return { title: L('جدول العمليات', 'Theatre schedule'), crumbs: [{ t: L('التنويم', 'Inpatient') }, { t: L('العمليات', 'Theatre') }], actions: H.btn('primary', 'ki-plus', L('حجز عملية', 'Book a case'), 'onclick="HMS.toast(HMS.L(\'نموذج الحجز في المرحلة التالية\',\'Booking form comes in the next pass\'),\'info\')"'), html: html };
	} });

	/* ---------------- Telemedicine, dietary, sterile supply ---------------- */
	H.route('telemed', { module: 'telemed', render: function () {
		var d = D.doc('D3'), p = D.patient('MRN-100274');
		var html = '<div class="row g-5"><div class="col-xl-8"><div class="card mb-5"><div class="card-body p-0"><div class="d-flex flex-center flex-column rounded" style="height:420px;background:#0B1B33;color:#fff;">' + H.avatar(p, 100) + '<div class="fs-3 fw-bold mt-4">' + T(p.name) + '</div><div class="opacity-75 fs-7 mt-1"><span class="num">12:48</span> · ' + L('اتصال مشفّر', 'Encrypted call') + '</div><div class="d-flex gap-3 mt-8">' +
			['ki-speaker', 'ki-faceid', 'ki-screen'].map(function (i) { return '<span class="btn btn-icon btn-light w-50px h-50px rounded-circle"><i class="ki-outline ' + i + ' fs-2"></i></span>'; }).join('') + '<span class="btn btn-icon btn-danger w-50px h-50px rounded-circle"><i class="ki-outline ki-phone fs-2"></i></span></div></div></div></div></div>' +
			'<div class="col-xl-4">' + H.card({ title: L('الطبيب', 'Doctor'), body: H.personCell(d, T(d.title)) }) + H.card({ title: L('ملاحظات الزيارة', 'Visit notes'), body: '<textarea class="form-control form-control-solid" rows="6"></textarea><div class="mt-4">' + H.btn('primary', 'ki-check', L('حفظ ومتابعة للكشف', 'Save and continue to consultation')) + '</div>' }) + '</div></div>';
		return { title: L('الطب الاتصالي', 'Telemedicine'), crumbs: [{ t: L('العيادات', 'Clinics') }, { t: L('الطب الاتصالي', 'Telemedicine') }], html: html };
	} });
	H.route('dietary', { module: 'dietary', render: function () {
		var diets = [L('عادي', 'Regular'), L('سكري', 'Diabetic'), L('قليل الملح', 'Low salt'), L('سوائل', 'Clear fluids'), L('صائم', 'NPO')];
		var rows = D.wards[0].beds.filter(function (b) { return b.p && b.st === 'occupied'; }).map(function (b, i) { var p = D.patient(b.p); return [H.personCell(p, L('سرير ', 'Bed ') + '<span class="num">' + b.no + '</span>', null, 35), H.select(diets, diets[i % diets.length]), p.allergies.length ? H.badge('danger', p.allergies.map(T).join(', ')) : '<span class="hms-zero">—</span>', H.badge('success', L('أُرسلت', 'Sent'))]; });
		return { title: L('التغذية', 'Dietary'), crumbs: [{ t: L('التنويم', 'Inpatient') }, { t: L('التغذية', 'Dietary') }], html: H.card({ title: L('طلبات وجبة الغداء — جناح ٣', 'Lunch orders — Ward 3'), body: H.table([{ t: L('المريض', 'Patient') }, { t: L('النظام الغذائي', 'Diet'), w: '200px' }, { t: L('الحساسية', 'Allergy') }, { t: L('المطبخ', 'Kitchen') }], rows) + '<div class="mt-4">' + H.reuse('شاشة المطبخ في نقاط البيع', 'POS kitchen display') + '</div>' }) };
	} });
	H.route('cssd', { module: 'cssd', render: function () {
		var rows = [['SET-ORTHO-04', L('طقم عظام كبير', 'Major ortho set'), L('معقم', 'Sterile'), 'success', '2026-11-08'], ['SET-LAP-02', L('طقم منظار', 'Laparoscopy set'), L('في دورة التعقيم', 'In autoclave'), 'primary', '—'], ['SET-CS-01', L('طقم قيصرية', 'C-section set'), L('بانتظار الغسيل', 'Awaiting wash'), 'secondary', '—'], ['SET-ENT-03', L('طقم أنف وأذن', 'ENT set'), L('معقم', 'Sterile'), 'success', '2026-11-05']];
		return { title: L('التعقيم المركزي', 'Central sterile supply'), crumbs: [{ t: L('التنويم', 'Inpatient') }, { t: L('التعقيم', 'CSSD') }], html: H.card({ title: L('تتبع الأطقم الجراحية', 'Instrument-set tracking'), body: H.table([{ t: L('الطقم', 'Set') }, { t: L('الوصف', 'Description') }, { t: L('الحالة', 'Status') }, { t: L('صالح حتى', 'Sterile until') }], rows.map(function (r) { return [H.code(r[0]), r[1], H.badge(r[3], r[2]), r[4] === '—' ? '<span class="hms-zero">—</span>' : H.dateStr(r[4])]; })) }) };
	} });
})();
