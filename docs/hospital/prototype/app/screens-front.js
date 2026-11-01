/* Front screens: dashboard, module blueprint, patient registry, patient file, registration, appointments. */
(function () {
	'use strict';
	var H = HMS, L = H.L, T = H.T, D = H.data;

	function pStatus(p) {
		var m = { ip: ['primary', L('منوّم', 'Inpatient')], op: ['info', L('عيادات', 'Outpatient')], er: ['danger', L('طوارئ', 'Emergency')], or: ['primary', L('في العمليات', 'In theatre')], none: ['secondary', L('لا توجد زيارة مفتوحة', 'No open visit')] }[p.status] || ['secondary', '—'];
		return H.badge(m[0], m[1]);
	}
	H.pStatus = pStatus;
	function payerLabel(p) { var y = D.payer(p.payer); return T(y.name) + (y.kind === 'ins' ? ' · ' + p.cls : ''); }
	H.payerLabel = payerLabel;

	/* ---------------- Dashboard ---------------- */
	H.route('dashboard', { render: function () {
		var allBeds = D.wards.reduce(function (a, w) { return a.concat(w.beds); }, []);
		var occ = allBeds.filter(function (b) { return b.st === 'occupied'; }).length;
		var occPct = Math.round(occ * 100 / allBeds.length);
		var waiting = D.er.filter(function (e) { return e.stage === 'waiting' || e.stage === 'triage'; }).length;
		var k = [];
		k.push({ label: L('زيارات العيادات اليوم', 'Outpatient visits today'), value: '<span class="num">' + D.appointments.length * 6 + '</span>', sub: L('منها ', 'of which ') + '<span class="num">' + D.appointments.filter(function (a) { return a.st === 'done'; }).length * 6 + '</span> ' + L('مكتملة', 'completed'), icon: 'ki-briefcase' });
		if (H.isOn('er')) k.push({ label: L('الطوارئ — في الانتظار', 'ER — waiting'), value: '<span class="num">' + waiting + '</span>', sub: L('متوسط الوصول للطبيب ', 'Door-to-doctor avg ') + '<span class="num">18</span> ' + L('دقيقة', 'min'), icon: 'ki-pulse' });
		if (H.isOn('inpatient')) k.push({ label: L('إشغال الأسرة', 'Bed occupancy'), value: '<span class="num">' + occPct + '%</span>', sub: '<span class="num">' + occ + ' / ' + allBeds.length + '</span> ' + L('سرير مشغول', 'beds occupied'), icon: 'ki-home-3' });
		k.push({ label: L('إيراد اليوم', 'Revenue today'), value: H.money(186420, { bare: true }), sub: H.cur() + ' · ' + L('نقدي ', 'cash ') + '<span class="num">31%</span> · ' + L('تأمين ', 'insured ') + '<span class="num">69%</span>', icon: 'ki-dollar' });

		var html = H.stats(k);
		html += H.notice({ icon: 'ki-calendar-tick', title: L('ملخص المناوبة الصباحية', 'Morning shift summary'), text: L('٣ حالات حرجة تحت المتابعة، ونتيجة حرجة واحدة لم يُبلَّغ بها الطبيب بعد.', '3 critical patients under watch, and one critical result not yet acknowledged by the doctor.'), badge: L('الأحد ١ نوفمبر ٢٠٢٦', 'Sun 1 Nov 2026') });

		var left = '', right = '';
		if (H.isOn('inpatient')) {
			left += H.card({ title: L('الإشغال حسب الجناح', 'Occupancy by ward'), sub: L('لحظي من لوحة الأسرة', 'Live from the bed board'), toolbar: H.link('light-primary', 'ki-arrow-left', L('لوحة الأسرة', 'Bed board'), '#/beds'), body: H.table(
				[{ t: L('الجناح', 'Ward') }, { t: L('مشغول', 'Occupied'), cls: 'text-end', w: '90px' }, { t: L('متاح', 'Vacant'), cls: 'text-end', w: '90px' }, { t: L('الإشغال', 'Occupancy'), w: '220px' }],
				D.wards.map(function (w) {
					var o = w.beds.filter(function (b) { return b.st === 'occupied'; }).length, v = w.beds.filter(function (b) { return b.st === 'vacant'; }).length;
					var pct = Math.round(o * 100 / w.beds.length);
					return ['<span class="fw-bold text-gray-800">' + T(w.name) + '</span>', '<span class="num fw-bold">' + o + '</span>', v ? '<span class="num">' + v + '</span>' : '<span class="hms-zero">—</span>', H.progress(pct, pct > 85 ? 'danger' : 'primary')];
				})) });
		}
		if (H.isOn('er')) {
			left += H.card({ title: L('قائمة الطوارئ', 'Emergency queue'), sub: L('مرتبة حسب درجة الفرز', 'Ordered by triage level'), toolbar: H.link('light-primary', 'ki-arrow-left', L('لوحة الطوارئ', 'ER board'), '#/er'), body: H.table(
				[{ t: L('المريض', 'Patient') }, { t: 'ESI', w: '70px' }, { t: L('الشكوى', 'Complaint') }, { t: L('الانتظار', 'Wait'), cls: 'text-end', w: '90px' }],
				D.er.slice().sort(function (a, b) { return a.esi - b.esi; }).slice(0, 5).map(function (e) {
					var p = D.patient(e.p);
					return [H.personCell(p, p.mrn, '#/patient/' + p.mrn, 35), '<span class="badge badge-esi-' + e.esi + ' fw-bold">ESI ' + e.esi + '</span>', '<span class="text-gray-700">' + T(e.cc) + '</span>', '<span class="num fw-bold">' + e.mins + '</span> ' + L('د', 'm')];
				})) });
		}
		right += H.card({ title: L('الأطباء المناوبون', 'Doctors on duty'), sub: L('اليوم', 'Today'), body: D.staff.doctors.slice(0, 6).map(function (d) {
			var n = D.appointments.filter(function (a) { return a.d === d.id; }).length;
			return '<div class="d-flex align-items-center cb-row py-3">' + H.personCell(d, T(d.title) + ' · ' + T(D.departments[d.dept])) + '<span class="ms-auto badge badge-light-primary num">' + n + ' ' + L('موعد', 'appts') + '</span></div>';
		}).join('') });
		if (H.isOn('or')) {
			right += H.card({ title: L('العمليات اليوم', 'Theatre today'), toolbar: H.link('light-primary', 'ki-arrow-left', L('الجدول', 'Schedule'), '#/or'), body: D.surgeries.slice(0, 4).map(function (s) {
				var st = { done: H.badge('success', L('انتهت', 'Done')), live: '<span class="badge hms-badge-solid fs-8">' + L('جارية', 'In progress') + '</span>', planned: H.badge('secondary', L('مجدولة', 'Planned')) }[s.st];
				return '<div class="d-flex align-items-center cb-row py-3"><div class="flex-grow-1 min-w-0"><div class="fw-bold text-gray-800 text-truncate">' + T(s.proc) + '</div><div class="text-muted fs-7">' + T(D.patient(s.p).name) + ' · ' + T(D.theatres[s.room]) + '</div></div>' + st + '</div>';
			}).join('') });
		}
		if (H.isOn('insurance')) {
			right += H.card({ title: L('المطالبات هذا الشهر', 'Claims this month'), body: H.stats([
				{ label: L('مرفوعة', 'Submitted'), value: H.money(248900, { bare: true }), sub: H.cur() },
				{ label: L('نسبة الرفض', 'Rejection rate'), value: '<span class="num">6.1%</span>', sub: L('الهدف أقل من ٥٪', 'Target below 5%'), cls: 'text-danger' }], 'col-6').replace('mb-5', 'mb-0') });
		}
		html += '<div class="row g-5 g-xl-8"><div class="col-xl-7">' + left + '</div><div class="col-xl-5">' + right + '</div></div>';
		return { title: L('لوحة المستشفى', 'Hospital dashboard'), crumbs: [{ t: L('لوحة التحكم', 'Dashboard') }], actions: H.link('primary', 'ki-plus', L('تسجيل مريض', 'Register patient'), '#/register'), html: html };
	} });

	/* ---------------- Blueprint ---------------- */
	H.route('blueprint', { render: function () {
		var reuseRows = [
			[L('الصيدلية والمستلزمات الطبية', 'Pharmacy & medical supplies'), L('المخزون: الأصناف، الدفعات والصلاحية، المستودعات والرفوف، حد إعادة الطلب', 'Inventory: items, batches & expiry, warehouses & racks, reorder point'), H.badge('success', L('جاهز', 'Ready'))],
			[L('خصم المستهلكات عند تقديم الخدمة', 'Consumption when a service is delivered'), L('نمط خصم المطبخ في نقاط البيع + تفكيك المكونات (BOM) لحزم الإجراءات', 'POS kitchen-consumption pattern + BOM explosion for procedure kits'), H.badge('success', L('جاهز', 'Ready'))],
			[L('الفوترة والإيرادات والذمم', 'Billing, revenue, receivables'), L('المحاسبة: القيود، الذمم، عكس القيد، أحداث الفواتير', 'Accounting: journals, receivables, reversal, invoice events'), H.badge('success', L('جاهز', 'Ready'))],
			[L('الأطباء والتمريض والورديات', 'Doctors, nurses, shifts'), L('الموارد البشرية والهيكل التنظيمي', 'HR and organisation structure'), H.badge('success', L('جاهز', 'Ready'))],
			[L('المهام والمتابعات والتنبيهات', 'Tasks, follow-ups, alerts'), L('المهام والإشارات والخط الزمني ومركز الإشعارات', 'Tasks, mentions, timeline and notification centre'), H.badge('success', L('جاهز', 'Ready'))],
			[L('المطبوعات (روشتة، ملخص خروج، فاتورة)', 'Printouts (prescription, discharge, invoice)'), L('Report Studio فقط — تسجيل مجموعة بيانات لكل مطبوعة', 'Report Studio only — one dataset per printout'), H.badge('success', L('جاهز', 'Ready'))],
			[L('ملفات المريض (أشعة، تقارير)', 'Patient files (images, reports)'), L('منصة المستندات — فيها جهتا موظف وعميل، ونضيف «مريض» كجهة ثالثة', 'Document platform — has Employee and Customer owners; Patient becomes a third'), H.badge('primary', L('يحتاج توسعة', 'Needs extension'))],
			[L('بوابة المريض', 'Patient portal'), L('بوابة العملاء (Metronic demo30) مع نطاق مثبّت', 'Client Portal (Metronic demo30) with a pinned scope'), H.badge('primary', L('يحتاج توسعة', 'Needs extension'))],
			[L('الصلاحيات وفصل الشركات', 'Permissions & company isolation'), L('محولات الصلاحيات + BusinessContext + فلاتر الشركة', 'Permission adapters + BusinessContext + company filters'), H.badge('primary', L('نطاق جديد', 'New scope'))],
			[L('أتعاب الطبيب الزائر', 'Visiting doctor fees'), L('الموردون وفواتير الشراء والمدفوعات وضريبة الاستقطاع (TaxCode WHT)', 'Vendors, purchase invoices, payments and withholding tax (TaxCode WHT)'), H.badge('success', L('جاهز', 'Ready'))],
			[L('أتعاب الجزئي وخصم مشتريات الموظف', 'Part-time fees and staff purchase deductions'), L('مسير الرواتب — يحتاج بنود مستحقات واستقطاعات متغيرة على القسيمة', 'Payroll run — needs variable earning and deduction lines on the payslip'), H.badge('primary', L('يحتاج توسعة', 'Needs extension'))],
			[L('التأمين المقدم والباقات', 'Deposits and packages'), L('الدفعات تحت الحساب وحد الائتمان + الأصناف المركبة (Bundle)', 'On-account receipts and credit limit + bundle items'), H.badge('primary', L('يحتاج توسعة', 'Needs extension'))],
			[L('إغلاق خزينة المستشفى', 'Hospital cashier closing'), L('إغلاق وردية نقاط البيع (PosShift) بالعهدة والجرد والفرق', 'POS shift closing (PosShift) with float, count and variance'), H.badge('primary', L('يحتاج توسعة', 'Needs extension'))],
			[L('طلبات الأقسام وتوجيه الأدوية', 'Ward requisitions and medication routing'), L('التحويلات بين المستودعات مع الاعتماد + الصرف بالدفعة FEFO', 'Store transfers with approval + FEFO batch issue'), H.badge('primary', L('يحتاج توسعة', 'Needs extension'))],
			[L('زرعات الأمانة', 'Consignment implants'), L('السيريال في المخزون — منطق ملكية المورّد جديد', 'Inventory serials — supplier-ownership logic is new'), H.badge('primary', L('يحتاج توسعة', 'Needs extension'))],
			[L('المطبخ المركزي والكافيتيريا', 'Central kitchen & cafeteria'), L('مطعم نقاط البيع والوصفات (BOM) وتحليل الفعلي مقابل النظري', 'POS restaurant, recipes (BOM) and actual-vs-theoretical analysis'), H.badge('success', L('جاهز', 'Ready'))],
			[L('توزيع التكاليف على الأقسام', 'Cost allocation to departments'), L('مراكز التكلفة على سطور القيد وعلى قسيمة المرتب', 'Cost centres on journal lines and on the payslip'), H.badge('success', L('جاهز', 'Ready'))],
			[L('أجهزة المعامل والأشعة', 'Lab and imaging equipment'), L('الأصول الثابتة والصيانة الوقائية والمعايرة — أوامر العمل جديدة', 'Fixed assets, preventive maintenance & calibration — work orders are new'), H.badge('primary', L('يحتاج توسعة', 'Needs extension'))],
			[L('الشكاوى', 'Complaints'), L('تذاكر CRM بالـ SLA', 'CRM tickets with SLA'), H.badge('success', L('جاهز', 'Ready'))]
		];
		var newRows = [
			['Patient · PatientIdentifier · PatientAllergy', L('الملف الطبي الموحد ومطابقة الهوية', 'Master record and identity matching')],
			['Encounter · EncounterType', L('العمود الفقري: عيادة / طوارئ / تنويم / عملية', 'The spine: clinic / ER / inpatient / surgery')],
			['Appointment · Schedule · Slot', L('جداول الأطباء والحجز', 'Rosters and booking')],
			['ClinicalNote · Diagnosis(ICD) · VitalSign', L('التوثيق السريري', 'Clinical documentation')],
			['Order · OrderItem (Lab/Rad/Med/Procedure)', L('طلب موحد تتفرع منه كل الخدمات', 'One order model for every service')],
			['LabSpecimen · LabResult · RadiologyReport', L('النتائج والتقارير', 'Results and reports')],
			['Ward · Room · Bed · Admission · Transfer', L('التنويم وحركة المريض', 'Inpatient flow')],
			['MedicationAdministration', L('سجل إعطاء الأدوية', 'Medication administration record')],
			['TheatreBooking · SurgicalChecklist', L('العمليات والسلامة الجراحية', 'Theatre and surgical safety')],
			['Payer · Contract · PriceList · PreAuth · Claim · ClaimLine', L('التأمين والمطالبات', 'Insurance and claims')],
			['PatientAccessLog', L('سجل من فتح ملف أي مريض', 'Who opened which patient record')],
			['Floor · Room · RoomOccupancy', L('خريطة المبنى ومن في كل غرفة', 'Building map and who is in each room')],
			['ClinicSession · QueueTicket', L('جلسات العيادات وأرقام الدور', 'Clinic sessions and queue numbers')],
			['Practitioner · Privilege · FeeRule', L('الممارس وصلاحياته وقواعد أتعابه (مرتبط بموظف أو مورّد)', 'Practitioner, privileges and fee rules (linked to an employee or a vendor)')],
			['FeeStatement · FeeStatementLine', L('كشف الأتعاب الشهري ومسار اعتماده', 'Monthly fee statement and its approval')],
			['Estimate · Package · PatientDeposit', L('التقدير والباقات والتأمين المقدم', 'Estimates, packages and deposits')],
			['CashierSession', L('وردية خزينة المستشفى', 'Hospital cashier shift')],
			['MedicationDispatch · DispatchStep', L('توجيه الدواء بست خطوات حتى السرير', 'Six-step routing of a dose to the bedside')],
			['WardRequisition · ControlledDrugEntry', L('طلبات الأقسام وسجل المخدرات', 'Ward requisitions and the controlled-drug register')],
			['IncidentReport · RootCauseAnalysis', L('بلاغات الحوادث وتحليل السبب', 'Incident reports and root-cause analysis')],
			['MealOrder · CostAllocationRun', L('طلبات الوجبات وتوزيع تكلفة المطبخ شهرياً', 'Meal orders and the monthly kitchen cost allocation')]
		];
		var waves = [
			[L('الموجة ٠', 'Wave 0'), L('الحوكمة: ملكية الوحدة، رفع تجميد عدد الـ Controllers، نطاق صلاحيات طبي، شرائح SQL، سجل الوصول للملف، وبنود المرتبات المتغيرة', 'Governance: module ownership, controller-count freeze, clinical permission scope, SQL slices, record-access log, and variable payroll lines')],
			[L('الموجة ١', 'Wave 1'), L('سجل المرضى + المواعيد ولوحة العيادات + الكشف + الممارسون وأتعابهم + الفوترة وإغلاق الخزينة — يكفي لتشغيل عيادة', 'Patients + appointments and clinics board + consultation + practitioners and fees + billing and cashier closing — enough to run a clinic')],
			[L('الموجة ٢', 'Wave 2'), L('المختبر + الأشعة + الصيدلية من المخزون + التأمين والمطالبات + التقديرات والباقات — مجمع طبي', 'Lab + radiology + pharmacy from inventory + insurance & claims + estimates and packages — polyclinic')],
			[L('الموجة ٣', 'Wave 3'), L('الطوارئ + التنويم والأسرة وخريطة المستشفى + التمريض وتوجيه الأدوية + طلبات الأقسام + المطبخ والكافيتيريا وتوزيع التكلفة — مستشفى', 'ER + wards, beds and the hospital map + nursing and medication routing + ward requisitions + kitchen, cafeteria and cost allocation — hospital')],
			[L('الموجة ٤', 'Wave 4'), L('العمليات وزرعات الأمانة + العناية المركزة + بنك الدم + التعقيم + الجودة وبلاغات الحوادث', 'Theatre and consignment implants + ICU + blood bank + CSSD + quality and incident reports')],
			[L('الموجة ٥', 'Wave 5'), L('حزم الدول: الربط الإلكتروني للمطالبات والفوترة والوصفات حسب الدولة', 'Country packs: e-claims, e-invoicing and e-prescription integrations')]
		];
		var html = H.notice({ icon: 'ki-abstract-26', title: L('مستشفى فوق المنصة الحالية، لا نظام منفصل', 'A hospital on the existing platform, not a separate system'), text: L('كل وحدة تُفعَّل من الإعدادات، فنفس النسخة تشغّل عيادة أو مجمعاً أو مستشفى كاملاً حسب الدولة.', 'Every module is switched on in settings, so one build runs a clinic, a polyclinic or a full hospital, per country.'), badge: '<span class="num">' + H.MODULES.length + '</span> ' + L('وحدة', 'modules') });
		html += H.card({ title: L('ما يُعاد استخدامه من CrossBusiness', 'What CrossBusiness already provides'), body: H.table([{ t: L('احتياج المستشفى', 'Hospital need'), w: '28%' }, { t: L('الموجود في المنصة', 'Existing platform capability') }, { t: L('الحالة', 'State'), w: '140px' }], reuseRows) });
		html += '<div class="row g-5 g-xl-8"><div class="col-xl-6">' + H.card({ title: L('الكيانات الجديدة', 'New entities'), body: H.table([{ t: L('الكيان', 'Entity') }, { t: L('الغرض', 'Purpose') }], newRows.map(function (r) { return ['<span class="badge hms-code fw-bold fs-8">' + r[0] + '</span>', r[1]]; })) }) + '</div>';
		html += '<div class="col-xl-6">' + H.card({ title: L('خطة المراحل', 'Phased roadmap'), body: waves.map(function (w, i) {
			return '<div class="d-flex align-items-start cb-row py-4"><span class="symbol symbol-40px me-4"><span class="symbol-label fw-bold" style="background:' + (i === 0 ? '#0E4A9E;color:#fff' : '#E7F0FF;color:#0E4A9E') + '">' + i + '</span></span><div><div class="fw-bold text-gray-900">' + w[0] + '</div><div class="text-gray-700 fs-6">' + w[1] + '</div></div></div>';
		}).join('') }) + '</div></div>';
		if (H.blueprintGaps) html += H.blueprintGaps();
		return { title: L('خريطة الوحدة', 'Module blueprint'), crumbs: [{ t: L('خريطة الوحدة', 'Module blueprint') }], html: html };
	} });

	/* ---------------- Patient registry ---------------- */
	H.route('patients', { module: 'mpi', render: function () {
		var rows = D.patients.map(function (p) {
			return [H.personCell(p, H.ageSex(p), '#/patient/' + p.mrn), H.code(p.mrn), '<span class="num text-gray-700">' + p.nid + '</span>', '<span class="num text-gray-700">' + p.phone + '</span>', '<span class="text-gray-700">' + payerLabel(p) + '</span>', pStatus(p) + (p.loc ? '<div class="text-muted fs-8 mt-1">' + T(p.loc) + '</div>' : ''),
				'<a href="#/patient/' + p.mrn + '" class="btn btn-sm btn-light-primary">' + L('فتح الملف', 'Open file') + '</a>'];
		});
		var html = H.stats([
			{ label: L('إجمالي المرضى', 'Total patients'), value: '<span class="num">18,426</span>', sub: L('منذ بداية التشغيل', 'Since go-live') },
			{ label: L('مسجلون هذا الشهر', 'Registered this month'), value: '<span class="num">612</span>', sub: '<span class="num">+8%</span> ' + L('عن الشهر السابق', 'vs last month') },
			{ label: L('ملفات مكررة محتملة', 'Possible duplicates'), value: '<span class="num">7</span>', sub: L('بانتظار الدمج', 'Awaiting merge'), cls: 'text-danger' },
			{ label: L('مؤمَّن عليهم', 'Insured'), value: '<span class="num">71%</span>', sub: L('من زيارات الشهر', 'of this month\'s visits') }]);
		html += H.card({ title: L('سجل المرضى', 'Patient registry'), toolbar: H.searchBox(null, L('اسم، رقم ملف، هوية...', 'Name, MRN, ID...')), body: H.table(
			[{ t: L('المريض', 'Patient') }, { t: L('رقم الملف', 'MRN'), w: '130px' }, { t: T(H.country().idLabel) }, { t: L('الجوال', 'Mobile') }, { t: L('الجهة الدافعة', 'Payer') }, { t: L('الحالة', 'Status') }, { t: '', cls: 'text-end', w: '110px' }], rows) });
		return { title: L('سجل المرضى', 'Patient registry'), crumbs: [{ t: L('المرضى', 'Patients') }], actions: H.link('primary', 'ki-plus', L('تسجيل مريض', 'Register patient'), '#/register'), html: html };
	} });

	/* ---------------- Patient file ---------------- */
	function patientBanner(p) {
		var y = D.payer(p.payer);
		return '<div class="hms-pbanner p-6 mb-5"><div class="d-flex flex-wrap align-items-center gap-5">' + H.avatar(p, 75) +
			'<div class="flex-grow-1 min-w-0"><div class="d-flex flex-wrap align-items-center gap-2 mb-1"><span class="fs-2 fw-bold text-gray-900">' + T(p.name) + '</span>' + H.code(p.mrn) + pStatus(p) + '</div>' +
			'<div class="d-flex flex-wrap gap-2 mt-2"><span class="hms-chip"><i class="ki-outline ki-user fs-7"></i>' + H.ageSex(p) + '</span>' +
			'<span class="hms-chip"><i class="ki-outline ki-drop fs-7"></i>' + L('فصيلة ', 'Blood ') + '<span class="num">' + p.blood + '</span></span>' +
			'<span class="hms-chip"><i class="ki-outline ki-shield-tick fs-7"></i>' + T(y.name) + (y.kind === 'ins' ? ' · ' + p.cls : '') + '</span>' +
			'<span class="hms-chip"><i class="ki-outline ki-flag fs-7"></i>' + T(p.nat) + '</span>' +
			(p.loc ? '<span class="hms-chip"><i class="ki-outline ki-geolocation fs-7"></i>' + T(p.loc) + '</span>' : '') + '</div></div>' +
			'<div class="d-flex gap-2 flex-wrap">' + H.link('light-primary', 'ki-calendar-add', L('موعد', 'Book'), '#/appointments') + H.link('light-primary', 'ki-briefcase', L('كشف', 'Consult'), '#/consult/' + p.mrn) + H.link('light', 'ki-bill', L('الفاتورة', 'Bill'), '#/billing') + '</div></div></div>' +
			(p.allergies.length ? H.notice({ kind: 'danger', icon: 'ki-shield-cross', title: L('حساسية مسجلة', 'Recorded allergy'), text: p.allergies.map(T).join(L('، ', ', ')) + L(' — تظهر هذه التنبيه في الكشف والصرف والتمريض.', ' — this alert follows the patient into consultation, dispensing and nursing.'), badge: L('حساسية', 'Allergy') }) : '');
	}
	H.patientBanner = patientBanner;

	H.route('patient', { module: 'mpi', render: function (mrn) {
		var p = D.patient(mrn);
		var labs = D.labOrders.filter(function (o) { return o.p === p.mrn; });
		var rads = D.radOrders.filter(function (o) { return o.p === p.mrn; });
		var rx = D.rx.filter(function (o) { return o.p === p.mrn; });
		var appts = D.appointments.filter(function (a) { return a.p === p.mrn; });
		var vit = H.stats([
			{ label: L('ضغط الدم', 'Blood pressure'), value: '<span class="num">138/86</span>', sub: 'mmHg · 09:10' },
			{ label: L('النبض', 'Heart rate'), value: '<span class="num">88</span>', sub: 'bpm · 09:10' },
			{ label: L('الحرارة', 'Temperature'), value: '<span class="num">37.4</span>', sub: '°C · 09:10' },
			{ label: L('تشبع الأكسجين', 'SpO₂'), value: '<span class="num">96%</span>', sub: L('هواء الغرفة', 'Room air') }]);
		var visits = [
			['2026-11-01', L('تنويم', 'Inpatient'), T(D.departments.IM), D.doc('D1'), L('مفتوحة', 'Open')],
			['2026-10-14', L('عيادة', 'Clinic'), T(D.departments.CARD), D.doc('D2'), L('مغلقة', 'Closed')],
			['2026-08-02', L('طوارئ', 'Emergency'), T(D.departments.ER), D.doc('D7'), L('مغلقة', 'Closed')],
			['2026-05-19', L('عيادة', 'Clinic'), T(D.departments.IM), D.doc('D1'), L('مغلقة', 'Closed')]
		];
		var overview = vit + '<div class="row g-5"><div class="col-xl-6">' +
			H.card({ title: L('المشاكل الصحية النشطة', 'Active problems'), body: (p.chronic.length ? p.chronic : [{ ar: 'لا توجد', en: 'None recorded' }]).map(function (c, i) {
				return '<div class="d-flex align-items-center cb-row py-3"><span class="bullet bullet-dot bg-primary me-3"></span><span class="fw-semibold text-gray-800 flex-grow-1">' + T(c) + '</span>' + (p.chronic.length ? H.code(['E11.9', 'I10', 'I50.9', 'J45.9'][i] || 'Z00') : '') + '</div>';
			}).join('') }) +
			H.card({ title: L('الأدوية الحالية', 'Current medications'), body: H.table([{ t: L('الدواء', 'Drug') }, { t: L('الجرعة', 'Dose') }], [
				[L('ميتفورمين ٨٥٠ ملجم', 'Metformin 850 mg'), L('مرتين يومياً', 'twice daily')], [L('أملوديبين ٥ ملجم', 'Amlodipine 5 mg'), L('يومياً', 'daily')], [L('سيفترياكسون ١ جم وريدي', 'Ceftriaxone 1 g IV'), L('كل ٢٤ ساعة', 'q24h')]]) }) +
			'</div><div class="col-xl-6">' + H.card({ title: L('الخط الزمني للمريض', 'Patient timeline'), body:
				[[L('٠٩:٤٠ اليوم', '09:40 today'), L('نتيجة صورة دم كاملة جاهزة — كريات بيضاء مرتفعة', 'CBC result ready — WBC high'), 'ki-flask', D.staff.nurses[0]],
				[L('٠٩:١٠ اليوم', '09:10 today'), L('قياس العلامات الحيوية', 'Vital signs recorded'), 'ki-heart', D.staff.nurses[1]],
				[L('أمس ٢١:٠٠', 'Yesterday 21:00'), L('نقل من العناية المركزة إلى جناح ٣', 'Transferred from ICU to Ward 3'), 'ki-arrows-loop', D.doc('D1')],
				[L('٢٩ أكتوبر', '29 Oct'), L('دخول عبر الطوارئ — التهاب رئوي', 'Admitted via ER — pneumonia'), 'ki-home-3', D.doc('D7')]].map(function (e) {
					return '<div class="d-flex align-items-start cb-row py-4">' + H.avatar(e[3], 35) + '<div class="ms-3 flex-grow-1"><div class="fw-semibold text-gray-800">' + e[1] + '</div><div class="text-muted fs-7">' + T(e[3].name) + ' · ' + e[0] + '</div></div><i class="ki-outline ' + e[2] + ' fs-2 text-gray-400"></i></div>';
				}).join('') }) + '</div></div>';
		var visitsHtml = H.card({ body: H.table([{ t: L('التاريخ', 'Date') }, { t: L('النوع', 'Type') }, { t: L('القسم', 'Department') }, { t: L('الطبيب', 'Doctor') }, { t: L('الحالة', 'Status') }],
			visits.map(function (v) { return [H.dateStr(v[0]), v[1], v[2], H.personCell(v[3], null, null, 30), v[4] === L('مفتوحة', 'Open') ? H.badge('primary', v[4]) : H.badge('secondary', v[4])]; })), bodyCls: 'pt-6' });
		var resultsHtml = H.card({ body: H.table([{ t: L('الطلب', 'Order') }, { t: L('الفحص', 'Test') }, { t: L('الحالة', 'Status') }, { t: '', cls: 'text-end' }],
			labs.map(function (o) { return [H.code(o.id), T(o.test), H.labStatus(o.st), o.code === 'CBC' ? '<a href="#/lab-result/' + o.id + '" class="btn btn-sm btn-light-primary">' + L('عرض', 'View') + '</a>' : '']; })
				.concat(rads.map(function (o) { return [H.code(o.id), T(o.exam), H.radStatus(o.st), '']; }))), bodyCls: 'pt-6' });
		var docsHtml = '<div class="row g-5">' + [[L('تقرير أشعة الصدر', 'Chest X-ray report'), 'PDF', '2026-10-29'], [L('موافقة على الإجراء', 'Procedure consent'), 'PDF', '2026-10-29'], [L('صورة بطاقة التأمين', 'Insurance card'), 'JPG', '2026-05-19'], [L('تقرير خروج سابق', 'Previous discharge summary'), 'PDF', '2026-08-02']].map(function (d) {
			return '<div class="col-md-6 col-xl-3"><div class="card h-100"><div class="card-body d-flex flex-column flex-center text-center p-8"><i class="ki-outline ki-document fs-3x text-primary mb-4"></i><div class="fw-bold text-gray-800 mb-1">' + d[0] + '</div><div class="text-muted fs-7">' + d[1] + ' · ' + H.dateStr(d[2]) + '</div></div></div></div>';
		}).join('') + '</div><div class="mt-4">' + H.reuse('منصة المستندات (جهة مالكة «مريض» جديدة)', 'Document platform (a new Patient owner)') + '</div>';
		var bill = D.bill, gross = bill.lines.reduce(function (a, l) { return a + l.qty * l.price; }, 0);
		var billingHtml = H.stats([{ label: L('إجمالي الزيارة المفتوحة', 'Open encounter total'), value: H.money(gross, { bare: true }), sub: H.cur() }, { label: L('حصة المريض المستحقة', 'Patient share due'), value: H.money(1124, { bare: true }), sub: H.cur(), cls: 'text-danger' }, { label: L('مدفوع مقدماً', 'Deposit paid'), value: H.money(bill.paid, { bare: true }), sub: H.cur() }], 'col-md-4') + '<a href="#/billing" class="btn btn-sm btn-primary"><i class="ki-outline ki-bill fs-4"></i>' + L('فتح الفاتورة', 'Open the bill') + '</a>';
		var y = D.payer(p.payer);
		var insHtml = y.kind === 'cash' ? H.notice({ icon: 'ki-wallet', title: L('مريض نقدي', 'Cash patient'), text: L('لا توجد وثيقة تأمين مسجلة لهذا المريض.', 'No insurance policy is recorded for this patient.') }) :
			H.card({ title: T(y.name), sub: L('وثيقة ', 'Policy ') + '<span class="num">' + p.policy + '</span>', body: '<div class="row g-5">' + [[L('الفئة', 'Class'), p.cls], [L('نسبة التحمل', 'Co-pay'), '<span class="num">' + y.copay + '%</span>'], [L('حد التحمل للزيارة', 'Co-pay cap per visit'), y.cap ? H.money(y.cap) : '—'], [L('صلاحية العقد', 'Contract valid'), H.dateStr(y.from) + ' → ' + H.dateStr(y.to)], [L('قائمة الأسعار', 'Price list'), T(y.priceList)], [L('الربط الإلكتروني', 'E-claims'), T(H.country().eclaims)]].map(function (f) {
				return '<div class="col-md-4"><div class="text-muted fw-semibold fs-7 mb-1">' + f[0] + '</div><div class="fw-bold fs-5 text-gray-900">' + f[1] + '</div></div>';
			}).join('') + '</div>' });
		var id = 'pf';
		var html = patientBanner(p) + H.tabs(id, [{ k: 'o', t: L('نظرة عامة', 'Overview') }, { k: 'v', t: L('الزيارات', 'Visits'), n: visits.length }, { k: 'r', t: L('النتائج', 'Results'), n: labs.length + rads.length }, { k: 'm', t: L('الروشتات', 'Prescriptions'), n: rx.length }, { k: 'd', t: L('المستندات', 'Documents'), n: 4 }, { k: 'b', t: L('الحساب', 'Account') }, { k: 'i', t: L('التأمين', 'Insurance') }], 'o') +
			'<div class="tab-content">' + H.pane(id, 'o', true, overview) + H.pane(id, 'v', false, visitsHtml) + H.pane(id, 'r', false, resultsHtml) +
			H.pane(id, 'm', false, H.card({ body: H.table([{ t: L('الروشتة', 'Prescription') }, { t: L('الأصناف', 'Items') }, { t: L('الطبيب', 'Doctor') }, { t: L('الحالة', 'Status') }], rx.map(function (r) { return [H.code(r.id), r.items.map(function (i) { return T(i.drug); }).join('<br>'), H.personCell(D.doc(r.doc), null, null, 30), H.rxStatus(r.st)]; })), bodyCls: 'pt-6' })) +
			H.pane(id, 'd', false, docsHtml) + H.pane(id, 'b', false, billingHtml) + H.pane(id, 'i', false, insHtml) + '</div>' +
			'<div class="text-muted fs-8 mt-4"><i class="ki-outline ki-eye fs-7 me-1"></i>' + L('فتح هذا الملف مُسجَّل في سجل الوصول الطبي باسمك ووقتك.', 'Opening this file is recorded in the clinical access log with your name and time.') + '</div>';
		return { title: T(p.name), crumbs: [{ t: L('المرضى', 'Patients'), href: '#/patients' }, { t: p.mrn }], actions: H.btn('light-primary', 'ki-printer', L('طباعة الملف', 'Print file'), 'onclick="HMS.toast(HMS.L(\'يُفتح من Report Studio\',\'Opens in Report Studio\'),\'info\')"'), html: html };
	} });

	/* ---------------- Registration ---------------- */
	H.route('register', { module: 'mpi', render: function () {
		var c = H.country();
		var form = '<div class="row g-6">' +
			H.field(L('الاسم بالعربية', 'Name in Arabic') + ' <span class="text-danger">*</span>', H.input('', 'dir="rtl" placeholder="الاسم الرباعي"')) +
			H.field(L('الاسم بالإنجليزية', 'Name in English') + ' <span class="text-danger">*</span>', H.input('', 'dir="ltr" placeholder="Full name"')) +
			H.field(T(c.idLabel) + ' <span class="text-danger">*</span>', H.input('', 'class="num" placeholder="' + Array(+c.idFormat + 1).join('0') + '"') + '<div class="text-muted fs-8 mt-1">' + L('يُتحقق من الطول: ', 'Length check: ') + '<span class="num">' + c.idFormat + '</span> ' + L('رقم — حسب إعدادات الدولة', 'digits — from the country settings') + '</div>', 'col-md-4') +
			H.field(L('تاريخ الميلاد', 'Date of birth'), '<div class="input-group input-group-solid">' + H.input('', 'type="date"') + (c.hijri ? '<span class="input-group-text fs-8">' + L('هجري', 'Hijri') + '</span>' : '') + '</div>', 'col-md-4') +
			H.field(L('النوع', 'Sex'), H.select([{ v: 'M', t: L('ذكر', 'Male') }, { v: 'F', t: L('أنثى', 'Female') }], 'M'), 'col-md-4') +
			H.field(L('الجنسية', 'Nationality'), H.select(Object.keys(D.countries).map(function (k) { return { v: k, t: T(D.countries[k].name) }; }), H.settings.country) + '<div class="text-muted fs-8 mt-1">' + L('من جدول الدول في الإعدادات العامة', 'From the system country table') + '</div>', 'col-md-4') +
			H.field(L('الجوال', 'Mobile'), H.input('', 'class="num" dir="ltr" placeholder="+966 5x xxx xxxx"'), 'col-md-4') +
			H.field(L('فصيلة الدم', 'Blood group'), H.select(['—', 'O+', 'O-', 'A+', 'A-', 'B+', 'B-', 'AB+', 'AB-'], '—'), 'col-md-4') +
			'</div>';
		var ins = '<div class="row g-6">' +
			H.field(L('الجهة الدافعة', 'Payer'), H.select(D.payers.map(function (y) { return { v: y.id, t: T(y.name) }; }), 'NIC')) +
			H.field(L('رقم الوثيقة', 'Policy number'), H.input('', 'class="num"')) +
			H.field(L('الفئة', 'Class'), H.select(['VIP', 'A', 'B', 'C'], 'A'), 'col-md-4') +
			H.field(L('تاريخ الانتهاء', 'Expiry'), H.input('', 'type="date"'), 'col-md-4') +
			H.field(L('صورة البطاقة', 'Card image'), '<div class="border border-dashed rounded p-3 text-center text-muted fs-7"><i class="ki-outline ki-picture fs-2 d-block mb-1"></i>' + L('اسحب الصورة هنا', 'Drop the image here') + '</div>', 'col-md-4') +
			'</div><div class="mt-5">' + H.btn('light-primary', 'ki-check-circle', L('التحقق من الأهلية إلكترونياً', 'Check eligibility online'), 'onclick="HMS.toast(HMS.L(\'مؤهل — الفئة A، التحمل ٢٠٪\',\'Eligible — class A, co-pay 20%\'))"') + ' <span class="text-muted fs-8 ms-2">' + T(c.eclaims) + '</span></div>';
		var html = '<div class="row g-5 g-xl-8"><div class="col-xl-8">' +
			H.card({ title: L('بيانات المريض', 'Patient details'), sub: L('كل اسم يُدخل بالعربية والإنجليزية', 'Every name is captured in Arabic and English'), body: form }) +
			H.card({ title: L('التأمين', 'Insurance'), body: ins }) +
			H.card({ title: L('جهة الاتصال للطوارئ', 'Emergency contact'), body: '<div class="row g-6">' + H.field(L('الاسم', 'Name'), H.input(''), 'col-md-4') + H.field(L('صلة القرابة', 'Relationship'), H.input(''), 'col-md-4') + H.field(L('الجوال', 'Mobile'), H.input('', 'class="num" dir="ltr"'), 'col-md-4') + '</div>' +
				'<label class="form-check form-check-custom form-check-solid mt-6"><input class="form-check-input" type="checkbox" checked /><span class="form-check-label fw-semibold text-gray-700">' + L('وقّع المريض على الموافقة العامة على العلاج وسياسة الخصوصية', 'The patient signed the general consent to treatment and the privacy notice') + '</span></label>' }) +
			'</div><div class="col-xl-4">' +
			H.notice({ icon: 'ki-people', title: L('فحص التكرار', 'Duplicate check'), text: L('عند إدخال الهوية أو الجوال يبحث النظام عن ملف قائم قبل إنشاء ملف جديد.', 'As the ID or mobile is entered, the system looks for an existing file before creating a new one.'), cls: 'mb-5' }) +
			H.card({ title: L('ملفات مشابهة', 'Similar files'), body: D.patients.slice(8, 10).map(function (p) {
				return '<div class="d-flex align-items-center cb-row py-3">' + H.personCell(p, p.mrn + ' · <span class="num">' + p.nid + '</span>') + '<button class="btn btn-sm btn-light ms-auto">' + L('استخدام', 'Use') + '</button></div>';
			}).join('') }) +
			H.card({ title: L('رقم الملف', 'Medical record number'), body: '<div class="fs-2 fw-bold text-gray-900 num mb-2">MRN-100438</div><div class="text-muted fs-7">' + L('يُولَّد تلقائياً حسب نمط الترقيم في الإعدادات', 'Generated from the numbering pattern in settings') + '</div>' }) +
			'</div></div>';
		return { title: L('تسجيل مريض جديد', 'Register a new patient'), crumbs: [{ t: L('المرضى', 'Patients'), href: '#/patients' }, { t: L('تسجيل', 'Register') }],
			actions: H.link('light', null, L('إلغاء', 'Cancel'), '#/patients') + H.btn('primary', 'ki-check', L('حفظ وفتح الملف', 'Save and open file'), 'onclick="HMS.toast(HMS.L(\'تم إنشاء الملف MRN-100438\',\'File MRN-100438 created\'));location.hash=\'#/patient/MRN-100231\'"'), html: html };
	} });

	/* ---------------- Appointments ---------------- */
	H.route('appointments', { module: 'appointments', render: function () {
		var docs = D.staff.doctors.slice(0, 5);
		var stTxt = { booked: L('محجوز', 'Booked'), arrived: L('حضر', 'Arrived'), inroom: L('عند الطبيب', 'With doctor'), done: L('انتهى', 'Done'), noshow: L('لم يحضر', 'No-show') };
		var typeTxt = { new: L('جديد', 'New'), fu: L('متابعة', 'Follow-up'), proc: L('إجراء', 'Procedure') };
		var g = '<div class="hms-sched" style="grid-template-columns:70px repeat(' + docs.length + ', minmax(150px,1fr));">';
		g += '<div class="hd"></div>' + docs.map(function (d) { return '<div class="hd">' + H.personCell(d, T(D.departments[d.dept]) + ' · ' + L('غرفة ', 'Room ') + '<span class="num">' + d.room + '</span>', null, 35) + '</div>'; }).join('');
		D.slots.forEach(function (t) {
			g += '<div class="tm num">' + t + '</div>';
			docs.forEach(function (d) {
				var a = D.appointments.filter(function (x) { return x.d === d.id && x.t === t; })[0];
				if (!a) { g += '<div class="cursor-pointer" title="' + L('حجز', 'Book') + '" onclick="HMS.bookSlot(\'' + d.id + '\',\'' + t + '\')"></div>'; return; }
				var p = D.patient(a.p);
				g += '<div><div class="hms-slot ' + a.st + '" onclick="location.hash=\'#/patient/' + p.mrn + '\'"><div class="fw-bold text-truncate">' + T(p.name) + '</div><div class="fs-8 opacity-75">' + typeTxt[a.type] + ' · ' + stTxt[a.st] + '</div></div></div>';
			});
		});
		g += '</div>';
		var legend = Object.keys(stTxt).map(function (k) { return '<span class="d-inline-flex align-items-center gap-2 me-5 fs-7 fw-semibold text-gray-700"><span class="hms-slot ' + k + '" style="width:14px;height:14px;padding:0;display:inline-block"></span>' + stTxt[k] + '</span>'; }).join('');
		var html = H.stats([
			{ label: L('مواعيد اليوم', 'Appointments today'), value: '<span class="num">' + D.appointments.length + '</span>', sub: L('عبر ', 'across ') + '<span class="num">' + docs.length + '</span> ' + L('أطباء', 'doctors') },
			{ label: L('حضروا', 'Checked in'), value: '<span class="num">' + D.appointments.filter(function (a) { return a.st !== 'booked' && a.st !== 'noshow'; }).length + '</span>', sub: L('منهم عند الطبيب الآن ', 'with a doctor now ') + '<span class="num">' + D.appointments.filter(function (a) { return a.st === 'inroom'; }).length + '</span>' },
			{ label: L('لم يحضروا', 'No-shows'), value: '<span class="num">' + D.appointments.filter(function (a) { return a.st === 'noshow'; }).length + '</span>', sub: L('تُرسل رسالة إعادة جدولة تلقائياً', 'A reschedule SMS is sent automatically') },
			{ label: L('حجوزات البوابة', 'Portal bookings'), value: '<span class="num">34%</span>', sub: L('من حجوزات الأسبوع', 'of this week\'s bookings') }]);
		html += H.card({ title: L('جدول الأطباء — الأحد ١ نوفمبر', 'Doctor schedule — Sun 1 Nov'), sub: L('اضغط خانة فارغة للحجز، أو موعداً لفتح ملف المريض', 'Click an empty slot to book, or an appointment to open the file'),
			toolbar: '<div class="d-flex gap-2">' + H.select([{ v: '', t: L('كل الأقسام', 'All departments') }].concat(Object.keys(D.departments).map(function (k) { return { v: k, t: T(D.departments[k]) }; })), '', 'style="width:190px"') + H.btn('light', 'ki-left', '', '') + H.btn('light', 'ki-right', '', '') + '</div>',
			body: '<div class="mb-5">' + legend + '</div><div class="overflow-auto">' + g + '</div>' });
		return { title: L('المواعيد', 'Appointments'), crumbs: [{ t: L('المواعيد', 'Appointments') }], actions: H.btn('primary', 'ki-calendar-add', L('حجز موعد', 'Book appointment'), 'onclick="HMS.bookSlot(\'D1\',\'13:30\')"'), html: html };
	} });

	H.bookSlot = function (docId, t) {
		var d = D.doc(docId);
		H.modal(L('حجز موعد', 'Book an appointment'),
			'<div class="d-flex align-items-center mb-6">' + H.personCell(d, T(d.title) + ' · <span class="num">' + t + '</span>') + '</div><div class="row g-5">' +
			H.field(L('المريض', 'Patient'), H.select(D.patients.slice(0, 12).map(function (p) { return { v: p.mrn, t: T(p.name) + ' — ' + p.mrn }; }), '')) +
			H.field(L('نوع الزيارة', 'Visit type'), H.select([{ v: 'new', t: L('جديد', 'New') }, { v: 'fu', t: L('متابعة', 'Follow-up') }, { v: 'proc', t: L('إجراء', 'Procedure') }, { v: 'tele', t: L('مرئي عن بعد', 'Video visit') }], 'new')) +
			H.field(L('ملاحظة', 'Note'), '<textarea class="form-control form-control-solid" rows="2"></textarea>', 'col-12') + '</div>' +
			'<label class="form-check form-check-custom form-check-solid mt-5"><input class="form-check-input" type="checkbox" checked /><span class="form-check-label text-gray-700">' + L('إرسال تأكيد وتذكير بالرسائل', 'Send SMS confirmation and reminder') + '</span></label>',
			'<button class="btn btn-light" data-bs-dismiss="modal">' + L('إلغاء', 'Cancel') + '</button><button class="btn btn-primary" data-bs-dismiss="modal" onclick="HMS.toast(HMS.L(\'تم الحجز\',\'Appointment booked\'))">' + L('تأكيد الحجز', 'Confirm booking') + '</button>');
	};
})();
