/* Second pass, after the gap analysis: hospital map, clinics board, lab sections and imaging rooms,
   medication routing, practitioners and fee statements, estimates and deposits, cashier closing,
   ward requisitions, incident reports, and the gap map on the blueprint. */
(function () {
	'use strict';
	var H = HMS, L = H.L, T = H.T, D = H.data;
	function P(ar, en) { return { ar: ar, en: en }; }

	/* ================= Data ================= */
	// Clinic rooms live on the first floor; theatre/ICU on the third.
	[['D1', '101'], ['D2', '104'], ['D3', '110'], ['D4', '106'], ['D5', '120'], ['D6', '108']].forEach(function (r) { D.doc(r[0]).room = r[1]; });

	// How each doctor is engaged and paid. channel: payroll (employee) | vendor (not an employee).
	D.contracts = {
		D1: { type: 'permanent', channel: 'payroll', license: 'SCFHS-11-R-28831', exp: '2027-05-14', priv: ['opd', 'ip'], sessions: P('يومياً ٨–٤', 'Daily 8–4'), rules: [[P('راتب شهري', 'Monthly salary'), P('حسب سلم الرواتب', 'Per salary policy')], [P('حافز', 'Incentive'), P('١٠٪ من إيراد الكشف فوق ٦٠٬٠٠٠ شهرياً', '10% of consultation revenue above 60,000 / month')]] },
		D2: { type: 'visiting', channel: 'vendor', license: 'SCFHS-09-C-11902', exp: '2026-11-20', priv: ['opd', 'ip', 'proc'], sessions: P('الأحد والثلاثاء', 'Sun & Tue'), rules: [[P('الكشف', 'Consultations'), P('٤٠٪ من المحصّل', '40% of collected')], [P('القسطرة والإجراءات', 'Cath & procedures'), P('٢٥٪ من المحصّل', '25% of collected')], [P('زيارة مريض منوّم', 'Inpatient visit'), P('٢٠٠ لكل زيارة', '200 per visit')]] },
		D3: { type: 'parttime', channel: 'payroll', license: 'SCFHS-14-S-40310', exp: '2028-02-01', priv: ['opd'], sessions: P('٣ جلسات أسبوعياً', '3 sessions / week'), rules: [[P('الجلسة', 'Session'), P('١٬٥٠٠ لكل جلسة ٤ ساعات', '1,500 per 4-hour session')]] },
		D4: { type: 'visiting', channel: 'vendor', license: 'SCFHS-07-C-09114', exp: '2027-09-30', priv: ['opd', 'ip', 'surg'], sessions: P('الاثنين والأربعاء', 'Mon & Wed'), rules: [[P('الكشف', 'Consultations'), P('٣٥٪ من المحصّل', '35% of collected')], [P('العمليات — جراح', 'Surgery — surgeon'), P('٦٠٪ من أتعاب العملية', '60% of the surgical fee')], [P('حد أدنى مضمون', 'Guaranteed minimum'), P('١٢٬٠٠٠ شهرياً', '12,000 / month')]] },
		D5: { type: 'permanent', channel: 'payroll', license: 'SCFHS-12-C-30021', exp: '2027-01-09', priv: ['opd', 'ip', 'surg'], sessions: P('يومياً', 'Daily'), rules: [[P('راتب شهري', 'Monthly salary'), P('حسب سلم الرواتب', 'Per salary policy')]] },
		D6: { type: 'visiting', channel: 'vendor', license: 'SCFHS-05-C-07720', exp: '2026-12-28', priv: ['opd', 'ip', 'surg'], sessions: P('حسب جدول العمليات', 'Per theatre list'), rules: [[P('العمليات — جراح', 'Surgery — surgeon'), P('٦٠٪ من أتعاب العملية', '60% of the surgical fee')], [P('الكشف', 'Consultations'), P('٤٠٪ من المحصّل', '40% of collected')]] },
		D7: { type: 'permanent', channel: 'payroll', license: 'SCFHS-16-R-51200', exp: '2028-06-30', priv: ['er'], sessions: P('ورديات ١٢ ساعة', '12-hour shifts'), rules: [[P('راتب شهري', 'Monthly salary'), P('حسب سلم الرواتب', 'Per salary policy')], [P('بدل مناوبة', 'On-call allowance'), P('٣٠٠ لكل مناوبة', '300 per on-call')]] },
		D8: { type: 'parttime', channel: 'payroll', license: 'SCFHS-10-C-22118', exp: '2027-03-15', priv: ['anes'], sessions: P('حسب جدول العمليات', 'Per theatre list'), rules: [[P('التخدير', 'Anaesthesia'), P('٢٥٪ من أتعاب العملية', '25% of the surgical fee')]] }
	};
	var TYPE = function (t) { return { permanent: ['primary', L('دائم', 'Permanent')], parttime: ['info', L('دوام جزئي', 'Part-time')], visiting: ['secondary', L('زائر', 'Visiting')] }[t]; };
	var PRIV = function (k) { return { opd: L('كشف عيادات', 'Clinic'), ip: L('تنويم', 'Admitting'), proc: L('إجراءات', 'Procedures'), surg: L('عمليات', 'Surgery'), er: L('طوارئ', 'Emergency'), anes: L('تخدير', 'Anaesthesia') }[k]; };
	function daysTo(d) { return Math.round((new Date(d) - H.TODAY) / 86400000); }

	/* ================= Hospital map ================= */
	// Each floor is a 12-column plan; a room is [id, col, row, colSpan, rowSpan, kind, label, occupants].
	function floors() {
		// Ward rooms as real rooms: width follows the bed count, and a row wraps at 12 columns.
		var roomTiles = function (wid, r0, kind) {
			var c = 1, r = r0;
			return H.roomsOfWard(wid).map(function (rm) {
				var span = rm.beds.length >= 4 ? 4 : rm.beds.length === 2 ? 3 : 2;
				if (c + span > 13) { c = 1; r++; }
				var occ = rm.beds.filter(function (b) { return b.p; });
				var t = { id: 'R' + rm.no, roomNo: rm.no, c: c, r: r, cs: span, rs: 1, kind: kind || 'bed', st: occ.length ? 'occupied' : rm.beds[0].st, label: P('غرفة ' + rm.no, 'Room ' + rm.no), patients: occ.map(function (b) { return b.p; }), patient: occ.length ? occ[0].p : null, ward: rm.ward };
				c += span; return t;
			});
		};
		var bedRoom = function (w, b, i, c0, r0) { return { id: b.no, c: c0 + (i % 6) * 2, r: r0 + Math.floor(i / 6), cs: 2, rs: 1, kind: 'bed', st: b.st, label: P('غرفة ' + b.no, 'Room ' + b.no), patient: b.p, dx: b.dx, doc: b.doc, ward: w }; };
		var w = function (id) { return D.wards.filter(function (x) { return x.id === id; })[0]; };
		var erRooms = D.er.filter(function (e) { return e.bay !== '—'; }).map(function (e, i) { return { id: 'ER-' + e.bay, c: 1 + i * 2, r: 1, cs: 2, rs: 1, kind: 'er', label: P('طوارئ ' + e.bay, 'ER ' + e.bay), patient: e.p, dx: e.cc, doc: e.doc, esi: e.esi }; });
		var radRooms = ['XR-1', 'CT-1', 'MR-1', 'US-2', 'ECHO'].map(function (rm, i) { var o = D.radOrders.filter(function (x) { return x.room === rm && (x.st === 'scanning' || x.st === 'arrived'); })[0]; return { id: rm, c: 1 + i * 2, r: 3, cs: 2, rs: 1, kind: 'rad', label: P('أشعة ' + rm, 'Imaging ' + rm), patient: o ? o.p : null, dx: o ? o.exam : null, doc: o ? o.doc : null }; });
		return [
			{ k: 'G', t: P('الدور الأرضي', 'Ground floor'), rooms: [{ id: 'REC', c: 11, r: 1, cs: 2, rs: 2, kind: 'admin', label: P('الاستقبال', 'Reception') }].concat(erRooms).concat([
				{ id: 'WAIT', c: 9, r: 2, cs: 2, rs: 1, kind: 'admin', label: P('انتظار الطوارئ', 'ER waiting'), count: D.er.filter(function (e) { return e.stage === 'waiting' || e.stage === 'triage'; }).length },
				{ id: 'CASH', c: 11, r: 3, cs: 2, rs: 1, kind: 'admin', label: P('الخزينة', 'Cashier') }]).concat(radRooms).concat([
				{ id: 'LAB-H', c: 1, r: 4, cs: 3, rs: 1, kind: 'lab', label: P('معمل الدم', 'Haematology lab') }, { id: 'LAB-C', c: 4, r: 4, cs: 3, rs: 1, kind: 'lab', label: P('معمل الكيمياء', 'Chemistry lab') },
				{ id: 'LAB-M', c: 7, r: 4, cs: 2, rs: 1, kind: 'lab', label: P('الميكروبيولوجي', 'Microbiology') }, { id: 'PH', c: 9, r: 4, cs: 4, rs: 1, kind: 'pharm', label: P('الصيدلية', 'Pharmacy') }]) },
			{ k: '1', t: P('الدور الأول — العيادات', 'First floor — clinics'), rooms: D.staff.doctors.slice(0, 6).map(function (d, i) {
				var cur = D.appointments.filter(function (a) { return a.d === d.id && a.st === 'inroom'; })[0];
				return { id: d.room, c: 1 + (i % 3) * 4, r: 1 + Math.floor(i / 3) * 2, cs: 4, rs: 2, kind: 'clinic', label: P('عيادة ' + d.room, 'Clinic ' + d.room), staff: [d], patient: cur ? cur.p : null, waiting: D.appointments.filter(function (a) { return a.d === d.id && a.st === 'arrived'; }).length };
			}).concat([{ id: 'PHL', c: 1, r: 5, cs: 4, rs: 1, kind: 'lab', label: P('سحب العينات', 'Phlebotomy') }, { id: 'NS1', c: 5, r: 5, cs: 4, rs: 1, kind: 'admin', label: P('محطة تمريض العيادات', 'Clinic nursing station') }, { id: 'W1', c: 9, r: 5, cs: 4, rs: 1, kind: 'admin', label: P('صالة الانتظار', 'Waiting hall'), count: 9 }]) },
			{ k: '2', t: P('الدور الثاني — الأجنحة', 'Second floor — wards'), rooms: roomTiles('W3', 1).concat([{ id: 'NS3', c: 1, r: 3, cs: 12, rs: 1, kind: 'admin', label: P('محطة تمريض — جناح ٣ وجناح ٢', 'Nursing station — wards 3 & 2'), staff: D.staff.nurses }]).concat(roomTiles('W2', 4)) },
			{ k: '3', t: P('الدور الثالث — العمليات والعناية', 'Third floor — theatre & ICU'), rooms: roomTiles('ICU', 1, 'icu').map(function (t) { t.cs = 12; return t; }).concat(D.theatres.map(function (t, i) {
				var live = D.surgeries.filter(function (s) { return s.room === i && s.st === 'live'; })[0];
				return { id: 'OR' + (i + 1), c: 1 + i * 3, r: 2, cs: 3, rs: 2, kind: 'or', label: t, patient: live ? live.p : null, dx: live ? live.proc : null, doc: live ? live.surgeon : null, staff: live ? [D.doc(live.surgeon), D.doc(live.anes)] : [] };
			})).concat([{ id: 'REC3', c: 1, r: 4, cs: 6, rs: 1, kind: 'admin', label: P('الإفاقة', 'Recovery') }, { id: 'CSSD', c: 7, r: 4, cs: 6, rs: 1, kind: 'admin', label: P('التعقيم المركزي', 'Sterile supply') }]) },
			{ k: '4', t: P('الدور الرابع — الولادة', 'Fourth floor — maternity'), rooms: roomTiles('W5', 1).concat([{ id: 'DEL', c: 1, r: 2, cs: 6, rs: 2, kind: 'or', label: P('غرفة الولادة', 'Delivery suite') }, { id: 'NICU', c: 7, r: 2, cs: 6, rs: 2, kind: 'icu', label: P('حضانة الأطفال', 'Neonatal unit'), count: 3 }]) }
		];
	}
	// The admin's floor drawing (floor designer) overrides where a space sits and how big it is.
	var floorsRaw = floors;
	floors = function () {
		var pos = H.store.get('hms.floorPos', {});
		return floorsRaw().map(function (f) { f.rooms.forEach(function (r) { var o = pos[r.id]; if (o) { r.c = o.c; r.r = o.r; r.cs = o.cs; r.rs = o.rs; } }); return f; });
	};
	H.floors = function () { return floors(); };
	var KIND = { bed: ['#0E4A9E', '#F7FAFF'], er: ['#F1416C', '#FFF5F8'], rad: ['#7239EA', '#F8F5FF'], lab: ['#17C653', '#F1FFF6'], pharm: ['#1B84FF', '#EEF6FF'], clinic: ['#0E4A9E', '#F7FAFF'], or: ['#0E4A9E', '#E7F0FF'], icu: ['#D9214E', '#FFF5F8'], admin: ['#99A1B7', '#F9F9F9'] };
	var KINDTXT = function (k) { return { bed: L('غرفة تنويم', 'Ward room'), er: L('طوارئ', 'Emergency'), rad: L('أشعة', 'Imaging'), lab: L('معمل', 'Laboratory'), pharm: L('صيدلية', 'Pharmacy'), clinic: L('عيادة', 'Clinic'), or: L('عمليات', 'Theatre'), icu: L('عناية مركزة', 'Intensive care'), admin: L('خدمات', 'Services') }[k]; };
	H.mapFloor = 'G';
	function roomTile(rm) {
		var col = KIND[rm.kind], p = rm.patient ? D.patient(rm.patient) : null, empty = rm.kind === 'bed' && !p;
		var who = p ? '<div class="d-flex align-items-center mt-2">' + H.avatar(p, 25) + '<span class="ms-2 fs-8 fw-bold text-gray-800 text-truncate">' + T(p.name) + '</span></div>' : '';
		if (rm.roomNo) {
			// A ward room: its type, how full it is, and a face per occupied bed.
			var room = H.roomByNo(rm.roomNo), rt = D.roomTypes[room.type];
			empty = !rm.patients.length;
			who = '<div class="fs-8 text-muted mt-1 text-truncate">' + T(rt.name) + ' · <span class="num fw-bold text-gray-800">' + rm.patients.length + ' / ' + room.beds.length + '</span></div>' +
				(rm.patients.length ? '<div class="d-flex mt-2 ps-2">' + rm.patients.map(function (m) { return '<span class="ms-n2">' + H.avatar(D.patient(m), 25) + '</span>'; }).join('') + '</div>' : '');
		}
		var staff = (rm.staff || []).slice(0, 3).map(function (s) { return '<span class="ms-n2">' + H.avatar(s, 25) + '</span>'; }).join('');
		var extra = rm.count != null ? '<div class="fs-8 text-muted mt-2"><span class="num fw-bold">' + rm.count + '</span> ' + L('شخص', 'people') + '</div>' : rm.waiting ? '<div class="fs-8 text-muted mt-1">' + L('ينتظر ', 'Waiting ') + '<span class="num fw-bold">' + rm.waiting + '</span></div>' : empty ? '<div class="fs-8 text-muted mt-2">' + ({ vacant: L('متاحة', 'Vacant'), cleaning: L('تنظيف', 'Cleaning'), reserved: L('محجوزة', 'Reserved'), blocked: L('مغلقة', 'Blocked') }[rm.st] || '') + '</div>' : '';
		var search = [rm.id, T(rm.label)].concat((rm.patients || (p ? [p.mrn] : [])).map(function (m) { var x = D.patient(m); return x.name.ar + ' ' + x.name.en + ' ' + x.mrn; })).concat((rm.staff || []).map(function (s) { return s.name.ar + ' ' + s.name.en; })).join(' ').toLowerCase();
		return '<div class="hms-room" data-room="' + rm.id + '" data-search="' + H.esc(search) + '" onclick="HMS.roomInfo(\'' + rm.id + '\')" style="grid-column:' + rm.c + ' / span ' + rm.cs + ';grid-row:' + rm.r + ' / span ' + rm.rs + ';border-color:' + col[0] + ';background:' + (empty ? 'var(--bs-body-bg)' : col[1]) + (empty ? ';border-style:dashed' : '') + '">' +
			'<div class="d-flex flex-stack"><span class="fw-bold fs-7 text-gray-900 text-truncate">' + T(rm.label) + '</span>' + (rm.esi ? '<span class="badge badge-esi-' + rm.esi + ' fs-9">ESI ' + rm.esi + '</span>' : '') + '</div>' + who +
			(staff ? '<div class="d-flex mt-2 ps-2">' + staff + '</div>' : '') + extra + '</div>';
	}
	H.findRoom = function (rid) { var hit = null; floors().forEach(function (f) { f.rooms.forEach(function (r) { if (r.id === rid) hit = { f: f, r: r }; }); }); return hit; };
	H.roomInfo = function (rid) {
		var h = H.findRoom(rid); if (!h) return; var rm = h.r, p = rm.patient ? D.patient(rm.patient) : null;
		if (rm.roomNo) {
			// A ward room opens on its drawn plan; each occupant is listed with what the desk needs.
			var room = H.roomByNo(rm.roomNo);
			var occ = room.beds.filter(function (b) { return b.p; }).map(function (b) {
				var x = D.patient(b.p);
				return '<div class="cb-row py-3"><div class="d-flex align-items-center flex-wrap gap-3">' + H.personCell(x, L('سرير ', 'Bed ') + '<span class="num">' + b.no + '</span> · ' + x.mrn + ' · ' + H.ageSex(x), '#/patient/' + x.mrn, 40) +
					'<div class="ms-auto text-end fs-8"><div class="fw-bold text-gray-800">' + (b.dx ? T(b.dx) : L('بانتظار الوصول', 'Awaiting arrival')) + '</div><div class="text-muted">' + H.payerLabel(x) + (x.allergies.length ? ' · <span class="text-danger">' + x.allergies.map(T).join(', ') + '</span>' : '') + '</div></div></div></div>';
			}).join('');
			H.modal(L('غرفة ', 'Room ') + room.no + ' — ' + T(D.roomTypes[room.type].name), '<div class="d-flex flex-wrap gap-2 mb-4"><span class="hms-chip">' + T(h.f.t) + '</span><span class="hms-chip">' + T(room.ward.name) + '</span><span class="hms-chip">' + H.money(D.roomTypes[room.type].rate) + ' ' + L('/ يوم', '/ day') + '</span></div>' +
				H.roomPlan(room, {}) + '<div class="mt-5">' + (occ || H.notice({ icon: 'ki-home-3', title: L('الغرفة فارغة', 'The room is empty'), text: L('كل أسرّتها متاحة أو قيد التجهيز.', 'All its beds are free or being prepared.'), cls: 'mb-0' })) + '</div>', null, 'mw-750px');
			return;
		}
		var body = '<div class="d-flex flex-wrap gap-2 mb-6"><span class="hms-chip">' + T(h.f.t) + '</span><span class="hms-chip">' + KINDTXT(rm.kind) + '</span>' + (rm.ward ? '<span class="hms-chip">' + T(rm.ward.name) + '</span>' : '') + '</div>';
		if (p) {
			body += '<div class="text-muted fs-7 fw-semibold mb-2">' + L('المريض', 'Patient') + '</div><div class="d-flex align-items-center mb-5">' + H.personCell(p, p.mrn + ' · ' + H.ageSex(p), '#/patient/' + p.mrn, 55) + '</div>' +
				'<div class="row g-5 mb-6">' + [[L('السبب / التشخيص', 'Reason / diagnosis'), rm.dx ? T(rm.dx) : '—'], [L('الجهة الدافعة', 'Payer'), H.payerLabel(p)], [L('فصيلة الدم', 'Blood group'), '<span class="num">' + p.blood + '</span>'], [L('الحساسية', 'Allergies'), p.allergies.length ? '<span class="text-danger fw-bold">' + p.allergies.map(T).join(', ') + '</span>' : L('لا توجد', 'None')], [L('الجوال', 'Mobile'), '<span class="num">' + p.phone + '</span>'], [T(H.country().idLabel), '<span class="num">' + p.nid + '</span>']].map(function (f) { return '<div class="col-md-4"><div class="text-muted fs-8 fw-semibold">' + f[0] + '</div><div class="fw-bold text-gray-900 fs-7">' + f[1] + '</div></div>'; }).join('') + '</div>';
		} else body += H.notice({ icon: 'ki-home-3', title: L('لا يوجد مريض في هذا المكان الآن', 'No patient here right now'), text: rm.count != null ? L('عدد الموجودين حالياً: ', 'People here now: ') + rm.count : L('المكان متاح أو مخصص للخدمات.', 'The space is free or a service area.'), cls: 'mb-5' });
		var staff = (rm.staff || []).concat(rm.doc && !(rm.staff || []).some(function (s) { return s.id === rm.doc; }) ? [D.doc(rm.doc)] : []);
		if (staff.length) body += '<div class="text-muted fs-7 fw-semibold mb-2">' + L('الطاقم الموجود', 'Staff present') + '</div>' + staff.map(function (s) { return '<div class="cb-row py-2">' + H.personCell(s, T(s.title)) + '</div>'; }).join('');
		H.modal(T(rm.label), body, '<button class="btn btn-light" data-bs-dismiss="modal">' + L('إغلاق', 'Close') + '</button>' + (p ? '<a href="#/patient/' + p.mrn + '" class="btn btn-primary" onclick="bootstrap.Modal.getInstance(document.getElementById(\'hms_modal\')).hide()">' + L('فتح ملف المريض', 'Open patient file') + '</a>' : ''));
	};
	H.showFloor = function (k) { H.mapFloor = k; H.render(); };
	H.route('map', { render: function () {
		var fl = floors(), cur = fl.filter(function (f) { return f.k === H.mapFloor; })[0] || fl[0];
		var allRooms = [].concat.apply([], fl.map(function (f) { return f.rooms; }));
		var patients = allRooms.filter(function (r) { return r.patient; }).length;
		var html = H.stats([
			{ label: L('مرضى داخل المبنى الآن', 'Patients in the building'), value: '<span class="num">' + (patients + 12) + '</span>', sub: L('منهم في الغرف ', 'in rooms ') + '<span class="num">' + patients + '</span>' },
			{ label: L('أطباء متواجدون', 'Doctors on site'), value: '<span class="num">' + D.staff.doctors.length + '</span>' },
			{ label: L('غرف متاحة', 'Rooms free'), value: '<span class="num">' + allRooms.filter(function (r) { return r.kind === 'bed' && r.st === 'vacant'; }).length + '</span>', cls: 'text-success' },
			{ label: L('أجهزة أشعة تعمل', 'Imaging rooms busy'), value: '<span class="num">' + allRooms.filter(function (r) { return r.kind === 'rad' && r.patient; }).length + ' / 5</span>' }]);
		var legend = Object.keys(KIND).map(function (k) { return '<span class="d-inline-flex align-items-center gap-2 me-4 fs-8 fw-semibold text-gray-700"><span style="width:12px;height:12px;border-radius:3px;border:2px solid ' + KIND[k][0] + ';background:' + KIND[k][1] + '"></span>' + KINDTXT(k) + '</span>'; }).join('');
		var floorsNav = '<div class="d-flex flex-wrap gap-2">' + fl.map(function (f) { return '<button class="btn btn-sm ' + (f.k === cur.k ? 'btn-primary' : 'btn-light') + '" onclick="HMS.showFloor(\'' + f.k + '\')">' + T(f.t) + '</button>'; }).join('') + '</div>';
		html += H.card({ title: T(cur.t), sub: L('اضغط أي غرفة لعرض من بداخلها وبياناته', 'Click any room to see who is inside and their details'), toolbar: '<div class="d-flex flex-wrap gap-3 align-items-center"><div class="position-relative"><i class="ki-outline ki-magnifier fs-3 position-absolute top-50 translate-middle-y ms-4"></i><input id="mapSearch" class="form-control form-control-solid w-250px ps-12" placeholder="' + L('ابحث عن مريض أو طبيب...', 'Find a patient or doctor...') + '" /></div></div>',
			body: '<div class="mb-5">' + floorsNav + '</div><div class="mb-4">' + legend + '</div><div class="overflow-auto"><div class="hms-plan">' + cur.rooms.map(roomTile).join('') + '</div></div><div id="mapHit" class="mt-4"></div>' });
		return { title: L('خريطة المستشفى', 'Hospital map'), crumbs: [{ t: L('خريطة المستشفى', 'Hospital map') }], html: html, after: function () {
			var inp = document.getElementById('mapSearch');
			inp.addEventListener('input', function () {
				var q = inp.value.trim().toLowerCase(), out = document.getElementById('mapHit');
				[].slice.call(document.querySelectorAll('.hms-room')).forEach(function (el) { el.classList.toggle('hms-room-dim', !!q && el.getAttribute('data-search').indexOf(q) === -1); });
				if (q.length < 2) { out.innerHTML = ''; return; }
				var hits = []; fl.forEach(function (f) { f.rooms.forEach(function (r) { var s = [r.id, T(r.label), (r.patients || (r.patient ? [r.patient] : [])).map(function (m) { return D.patient(m).name.ar + ' ' + D.patient(m).name.en; }).join(' ')].concat((r.staff || []).map(function (x) { return x.name.ar + ' ' + x.name.en; })).join(' ').toLowerCase(); if (s.indexOf(q) !== -1) hits.push({ f: f, r: r }); }); });
				out.innerHTML = hits.length ? hits.slice(0, 6).map(function (h) { return '<button class="btn btn-sm btn-light-primary me-2 mb-2" onclick="HMS.showFloor(\'' + h.f.k + '\');setTimeout(function(){HMS.roomInfo(\'' + h.r.id + '\')},50)">' + T(h.f.t) + ' · ' + T(h.r.label) + '</button>'; }).join('') : '<span class="text-muted fs-7">' + L('لا يوجد في أي غرفة', 'Not found in any room') + '</span>';
			});
		} };
	} });

	/* ================= Clinics board ================= */
	H.route('clinics', { module: 'opd', render: function () {
		var cards = D.staff.doctors.slice(0, 6).map(function (d, i) {
			var mine = D.appointments.filter(function (a) { return a.d === d.id; });
			var cur = mine.filter(function (a) { return a.st === 'inroom'; })[0], wait = mine.filter(function (a) { return a.st === 'arrived'; }), next = mine.filter(function (a) { return a.st === 'booked'; })[0];
			var c = D.contracts[d.id];
			var state = cur ? '<span class="badge hms-badge-solid fs-8">' + L('في الكشف', 'In session') + '</span>' : i === 5 ? H.badge('secondary', L('لم تبدأ', 'Not started')) : H.badge('info', L('متاح', 'Available'));
			var delay = [0, 12, 0, 25, 5, 0][i];
			return '<div class="col-md-6 col-xl-4"><div class="card h-100"><div class="card-body p-6">' +
				'<div class="d-flex flex-stack mb-5"><span class="fs-4 fw-bold text-gray-900">' + L('عيادة ', 'Clinic ') + '<span class="num">' + d.room + '</span></span>' + state + '</div>' +
				'<div class="d-flex align-items-center mb-5">' + H.personCell(d, T(D.departments[d.dept]) + ' · ' + H.badge(TYPE(c.type)[0], TYPE(c.type)[1]), '#/practitioner/' + d.id, 50) + '</div>' +
				'<div class="rounded p-4 mb-4" style="background:#F7FAFF"><div class="text-muted fs-8 fw-semibold mb-2">' + L('عند الطبيب الآن', 'With the doctor now') + '</div>' + (cur ? H.personCell(D.patient(cur.p), '<span class="num">' + cur.t + '</span> · ' + L('منذ ', 'for ') + '<span class="num">9</span> ' + L('د', 'm'), '#/patient/' + cur.p, 35) : '<span class="text-muted fs-7">—</span>') + '</div>' +
				'<div class="row g-3 text-center"><div class="col-4"><div class="fs-2 fw-bold num">' + wait.length + '</div><div class="text-muted fs-8">' + L('ينتظرون', 'Waiting') + '</div></div><div class="col-4"><div class="fs-2 fw-bold num">' + mine.filter(function (a) { return a.st === 'done'; }).length + '</div><div class="text-muted fs-8">' + L('تم', 'Seen') + '</div></div><div class="col-4"><div class="fs-2 fw-bold num ' + (delay > 15 ? 'text-danger' : '') + '">' + delay + '</div><div class="text-muted fs-8">' + L('تأخير د', 'Delay m') + '</div></div></div>' +
				'<div class="separator my-4"></div><div class="d-flex flex-stack fs-7"><span class="text-muted">' + L('التالي', 'Next') + '</span><span class="fw-bold text-gray-800">' + (next ? T(D.patient(next.p).name) + ' · <span class="num">' + next.t + '</span>' : '—') + '</span></div></div></div></div>';
		}).join('');
		var html = H.stats([
			{ label: L('عيادات تعمل الآن', 'Clinics running'), value: '<span class="num">5 / 6</span>' },
			{ label: L('في الانتظار', 'Waiting'), value: '<span class="num">' + D.appointments.filter(function (a) { return a.st === 'arrived'; }).length + '</span>', sub: L('متوسط الانتظار ', 'Avg wait ') + '<span class="num">17</span> ' + L('د', 'm') },
			{ label: L('متوسط مدة الكشف', 'Avg consultation'), value: '<span class="num">14</span> ' + L('د', 'm') },
			{ label: L('عيادات متأخرة', 'Clinics running late'), value: '<span class="num">1</span>', cls: 'text-danger', sub: L('أكثر من ١٥ دقيقة', 'over 15 minutes') }]);
		html += H.notice({ icon: 'ki-screen', title: L('شاشة النداء في صالة الانتظار', 'Calling screen in the waiting hall'), text: L('كل عيادة تنادي المريض التالي برقم الدور، ويظهر على شاشة الصالة ويُرسل له إشعار.', 'Each clinic calls the next patient by queue number, shown on the hall screen and sent as a notification.'), badge: L('رقم الدور ', 'Now serving ') + '<span class="num">A-017</span>' });
		html += '<div class="row g-5 g-xl-8">' + cards + '</div>';
		return { title: L('لوحة العيادات الخارجية', 'Outpatient clinics board'), crumbs: [{ t: L('العيادات', 'Clinics') }, { t: L('اللوحة', 'Board') }], actions: H.link('light-primary', 'ki-map', L('على الخريطة', 'On the map'), '#/map'), html: html, after: function () { H.mapFloor = '1'; } };
	} });

	/* ================= Lab sections & imaging rooms (embedded in their screens) ================= */
	H.labSections = function () {
		var secs = [[L('أمراض الدم', 'Haematology'), L('محلل الدم ١', 'Haem analyser 1'), 'online', 38, 6], [L('الكيمياء الحيوية', 'Clinical chemistry'), L('محلل الكيمياء ٢', 'Chem analyser 2'), 'online', 61, 11], [L('المناعة والهرمونات', 'Immunology & hormones'), L('محلل المناعة', 'Immunoassay'), 'qc', 22, 9], [L('الميكروبيولوجي', 'Microbiology'), L('حاضنة المزارع', 'Culture incubator'), 'online', 14, 14], [L('بنك الدم', 'Blood bank'), L('جهاز توافق الدم', 'Cross-match analyser'), 'down', 4, 4]];
		var st = function (s) { return { online: H.badge('success', L('متصل', 'Online')), qc: H.badge('primary', L('معايرة الجودة', 'Running QC')), down: H.badge('danger', L('متوقف', 'Down')) }[s]; };
		var pipe = [[L('مطلوب', 'Ordered'), 24], [L('سُحبت', 'Collected'), 17], [L('استُلمت', 'Received'), 12], [L('قيد التحليل', 'Processing'), 31], [L('بانتظار الاعتماد', 'To verify'), 9], [L('معتمدة اليوم', 'Verified today'), 121]];
		return '<div class="row g-5 g-xl-8"><div class="col-xl-7">' + H.card({ title: L('أقسام المعمل والأجهزة', 'Lab sections & analysers'), toolbar: H.reuse('الأصول والصيانة الوقائية والمعايرة', 'Fixed assets, preventive maintenance & calibration'), body: H.table([{ t: L('القسم', 'Section') }, { t: L('الجهاز', 'Analyser') }, { t: L('الحالة', 'State') }, { t: L('عينات اليوم', 'Today'), cls: 'text-end' }, { t: L('في الطابور', 'Queue'), cls: 'text-end' }],
			secs.map(function (s) { return ['<span class="fw-bold text-gray-800">' + s[0] + '</span>', '<span class="text-gray-700">' + s[1] + '</span>', st(s[2]), '<span class="num">' + s[3] + '</span>', '<span class="num fw-bold">' + s[4] + '</span>']; })) }) + '</div><div class="col-xl-5">' +
			H.card({ title: L('مسار العينات', 'Specimen pipeline'), sub: L('من الطلب حتى الاعتماد', 'From order to verification'), body: pipe.map(function (p, i) { return '<div class="d-flex align-items-center cb-row py-3"><span class="symbol symbol-30px me-3"><span class="symbol-label fw-bold fs-8" style="background:' + (i === pipe.length - 1 ? 'var(--bs-light-success);color:#0e7a39' : '#E7F0FF;color:#0E4A9E') + '">' + (i + 1) + '</span></span><span class="fw-semibold text-gray-800 flex-grow-1">' + p[0] + '</span><span class="num fw-bold fs-5">' + p[1] + '</span></div>'; }).join('') }) + '</div></div>';
	};
	H.radRooms = function () {
		var rooms = [['XR-1', L('أشعة سينية', 'X-ray'), 'free', null, 3], ['CT-1', L('مقطعية ١٢٨ شريحة', 'CT 128-slice'), 'busy', 'MRN-100421', 2], ['MR-1', L('رنين ١٫٥ تسلا', 'MRI 1.5T'), 'maint', null, 1], ['US-2', L('موجات صوتية', 'Ultrasound'), 'busy', 'MRN-100245', 4], ['ECHO', L('إيكو القلب', 'Echocardiography'), 'ready', 'MRN-100402', 1]];
		var st = function (s) { return { free: H.badge('success', L('متاحة', 'Free')), busy: '<span class="badge hms-badge-solid fs-8">' + L('قيد التصوير', 'Scanning') + '</span>', maint: H.badge('danger', L('صيانة', 'Maintenance')), ready: H.badge('info', L('المريض جاهز', 'Patient ready')) }[s]; };
		return '<div class="row g-5 mb-5">' + rooms.map(function (r) {
			var p = r[3] ? D.patient(r[3]) : null;
			return '<div class="col-md-6 col-xl"><div class="card h-100"><div class="card-body p-5"><div class="d-flex flex-stack mb-3"><span class="fw-bold fs-5 num">' + r[0] + '</span>' + st(r[2]) + '</div><div class="text-muted fs-7 mb-4">' + r[1] + '</div>' +
				(p ? H.personCell(p, p.mrn, '#/patient/' + p.mrn, 35) : '<div class="text-muted fs-8">' + (r[2] === 'maint' ? L('صيانة وقائية حتى ١٢:٠٠', 'Preventive maintenance until 12:00') : L('لا يوجد مريض', 'No patient')) + '</div>') +
				'<div class="separator my-3"></div><div class="fs-8 text-muted">' + L('في الانتظار ', 'Queue ') + '<span class="num fw-bold text-gray-800">' + r[4] + '</span></div></div></div></div>';
		}).join('') + '</div>';
	};

	/* ================= Medication routing ================= */
	var STAGES = function () { return [L('وُصف', 'Prescribed'), L('راجعه الصيدلي', 'Verified'), L('جُهّز', 'Prepared'), L('أُرسل', 'Dispatched'), L('استُلم بالجناح', 'Received'), L('أُعطي للمريض', 'Administered')]; };
	D.medRoutes = [
		{ id: 'MR-7701', p: 'MRN-100231', bed: '301', drug: P('سيفترياكسون ١ جم وريدي', 'Ceftriaxone 1 g IV'), prio: 'routine', via: 'tube', stage: 5, times: ['07:40', '07:52', '08:05', '08:09', '08:12', '08:30'], by: ['D1', 'N0', 'N0', 'TB', 'N2', 'N2'] },
		{ id: 'MR-7714', p: 'MRN-100288', bed: 'I02', drug: P('نورأدرينالين تسريب', 'Noradrenaline infusion'), prio: 'stat', via: 'porter', stage: 3, times: ['10:41', '10:44', '10:50', '10:53', '', ''], by: ['D2', 'N0', 'N0', 'PT', '', ''] },
		{ id: 'MR-7716', p: 'MRN-100434', bed: '305', drug: P('فوروسيميد ٤٠ ملجم وريدي', 'Furosemide 40 mg IV'), prio: 'urgent', via: 'cabinet', stage: 4, times: ['10:30', '10:36', '—', '—', '10:38', ''], by: ['D1', 'N0', '', '', 'N1', ''] },
		{ id: 'MR-7719', p: 'MRN-100317', bed: '504', drug: P('أوكسيتوسين ١٠ وحدات', 'Oxytocin 10 U'), prio: 'routine', via: 'tube', stage: 2, times: ['10:55', '11:01', '11:06', '', '', ''], by: ['D5', 'N0', 'N0', '', '', ''] },
		{ id: 'MR-7722', p: 'MRN-100301', bed: '302', drug: P('كلينداميسين ٦٠٠ ملجم وريدي', 'Clindamycin 600 mg IV'), prio: 'routine', via: 'tube', stage: 1, times: ['11:02', '11:09', '', '', '', ''], by: ['D1', 'N0', '', '', '', ''] },
		{ id: 'MR-7725', p: 'MRN-100329', bed: '307', drug: P('مورفين ٥ ملجم وريدي (مخدر)', 'Morphine 5 mg IV (controlled)'), prio: 'urgent', via: 'porter', stage: 0, times: ['11:10', '', '', '', '', ''], by: ['D6', '', '', '', '', ''], controlled: true }
	];
	var VIA = function (v) { return { tube: L('الأنبوب الهوائي', 'Pneumatic tube'), porter: L('ناقل', 'Porter'), cabinet: L('خزانة الجناح الآلية', 'Ward automated cabinet') }[v]; };
	var pharmacist = { name: P('ص. ليلى الحسن', 'Ph. Layla Alhassan'), title: P('صيدلانية سريرية', 'Clinical pharmacist'), photo: '300-12.jpg' };
	var porter = { name: P('سعيد عمر', 'Saeed Omar'), title: P('ناقل', 'Porter'), photo: null };
	function byPerson(k) { if (!k) return null; if (k === 'N0') return pharmacist; if (k === 'PT') return porter; if (k === 'TB') return null; if (k[0] === 'N') return D.staff.nurses[+k[1] - 1]; return D.doc(k); }
	H.route('medroute', { module: 'pharmacy', render: function () {
		var st = STAGES();
		var counts = st.map(function (_, i) { return D.medRoutes.filter(function (m) { return m.stage === i; }).length; });
		var pipe = '<div class="d-flex flex-wrap gap-3 mb-2">' + st.map(function (s, i) { return '<div class="flex-fill rounded p-4 text-center" style="min-width:130px;background:' + (i === st.length - 1 ? 'var(--bs-light-success)' : '#F7FAFF') + ';border:1px dashed ' + (i === st.length - 1 ? 'var(--bs-success)' : '#0E4A9E') + '"><div class="fs-2 fw-bold num">' + counts[i] + '</div><div class="fs-8 fw-semibold text-gray-700">' + s + '</div></div>'; }).join('') + '</div>';
		var rows = D.medRoutes.map(function (m) {
			var p = D.patient(m.p);
			var steps = '<div class="d-flex align-items-center">' + st.map(function (s, i) {
				var done = i <= m.stage && m.times[i] && m.times[i] !== '—', skip = m.times[i] === '—', curStep = i === m.stage + 1;
				return '<span class="d-inline-flex flex-center rounded-circle fs-9 fw-bold" data-bs-toggle="tooltip" title="' + H.esc(s + (m.times[i] && !skip ? ' · ' + m.times[i] : skip ? ' · ' + L('غير مطلوب', 'not needed') : '')) + '" style="width:22px;height:22px;' + (done ? 'background:#0E4A9E;color:#fff' : skip ? 'background:var(--bs-gray-100);color:var(--bs-gray-400)' : curStep ? 'box-shadow:inset 0 0 0 2px #0E4A9E;color:#0E4A9E' : 'background:var(--bs-gray-200);color:var(--bs-gray-500)') + '">' + (i + 1) + '</span>' + (i < st.length - 1 ? '<span style="width:14px;height:2px;background:' + (i < m.stage ? '#0E4A9E' : 'var(--bs-gray-200)') + '"></span>' : '');
			}).join('') + '</div>';
			var last = byPerson(m.by[m.stage]);
			return [H.code(m.id), H.personCell(p, L('سرير ', 'Bed ') + '<span class="num">' + m.bed + '</span>', '#/patient/' + p.mrn, 35), '<div class="fw-semibold text-gray-800">' + T(m.drug) + '</div>' + (m.controlled ? H.badge('danger', L('دواء مراقب — توقيع شاهدين', 'Controlled — two signatures')) : ''), (m.prio === 'stat' ? H.badge('danger', 'STAT') : m.prio === 'urgent' ? H.badge('primary', L('عاجل', 'Urgent')) : H.badge('secondary', L('روتيني', 'Routine'))), '<span class="text-gray-700 fs-7">' + VIA(m.via) + '</span>', steps, last ? '<div class="d-flex align-items-center gap-2" data-bs-toggle="tooltip" title="' + H.esc(T(last.name)) + '">' + H.avatar(last, 30) + '<span class="num fs-8 text-muted">' + m.times[m.stage] + '</span></div>' : '<span class="text-muted fs-8">' + L('الأنبوب', 'Tube') + ' · <span class="num">' + m.times[m.stage] + '</span></span>'];
		});
		var html = H.stats([
			{ label: L('أوامر دوائية نشطة', 'Active medication orders'), value: '<span class="num">' + D.medRoutes.filter(function (m) { return m.stage < 5; }).length + '</span>' },
			{ label: L('زمن الوصول STAT', 'STAT turnaround'), value: '<span class="num">12</span> ' + L('د', 'm'), sub: L('الهدف ١٥ دقيقة', 'Target 15 minutes') },
			{ label: L('زمن الوصول الروتيني', 'Routine turnaround'), value: '<span class="num">38</span> ' + L('د', 'm'), sub: L('الهدف ٦٠ دقيقة', 'Target 60 minutes') },
			{ label: L('جرعات متأخرة عن موعدها', 'Doses late against schedule'), value: '<span class="num">1</span>', cls: 'text-danger' }]);
		html += H.notice({ icon: 'ki-route', title: L('من الوصفة حتى سرير المريض', 'From prescription to the bedside'), text: L('كل أمر دوائي يُتتبع بست خطوات، وكل خطوة تُسجل باسم من نفذها ووقتها، والإعطاء يتم بمسح سوار المريض.', 'Every medication order is tracked through six steps, each stamped with who did it and when; administration needs a wristband scan.'), badge: L('٦ خطوات', '6 steps') });
		html += H.card({ title: L('مراحل التوجيه الآن', 'Routing stages now'), body: pipe });
		html += H.card({ title: L('الأوامر الدوائية', 'Medication orders'), toolbar: H.searchBox(), body: H.table([{ t: L('الأمر', 'Order'), w: '100px' }, { t: L('المريض', 'Patient') }, { t: L('الدواء', 'Medication') }, { t: L('الأولوية', 'Priority') }, { t: L('طريقة الإرسال', 'Route') }, { t: L('المسار', 'Progress'), w: '230px' }, { t: L('آخر خطوة', 'Last step') }], rows) });
		html += '<div class="row g-5"><div class="col-xl-6">' + H.card({ title: L('قواعد التوجيه', 'Routing rules'), body: [[L('STAT في العناية والطوارئ', 'STAT in ICU and ER'), L('ناقل مباشر خلال ١٥ دقيقة', 'Direct porter within 15 minutes')], [L('أدوية متوفرة في خزانة الجناح', 'Drugs stocked in the ward cabinet'), L('صرف من الخزانة بعد مراجعة الصيدلي', 'Cabinet issue after pharmacist review')], [L('الأدوية المخدرة', 'Controlled drugs'), L('ناقل + توقيع شاهدين عند الاستلام والإعطاء', 'Porter + two signatures on receipt and administration')], [L('باقي الأدوية', 'Everything else'), L('الأنبوب الهوائي حسب جولات الجرعات', 'Pneumatic tube on dose rounds')]].map(function (r) { return '<div class="d-flex flex-stack cb-row py-3"><span class="fw-semibold text-gray-800">' + r[0] + '</span><span class="text-gray-600 fs-7 text-end">' + r[1] + '</span></div>'; }).join('') }) + '</div><div class="col-xl-6">' +
			H.card({ title: L('ما يحدث في الخلفية', 'What happens behind it'), body: [L('التجهيز يخصم من مستودع الصيدلية بالدفعة (FEFO)', 'Preparation issues stock from the pharmacy store by batch (FEFO)'), L('الاستلام بالجناح ينقل العهدة للتمريض', 'Ward receipt moves custody to nursing'), L('الإعطاء يُسجل في سجل إعطاء الأدوية ويضيف السطر للفاتورة', 'Administration writes the MAR and adds the bill line'), L('الجرعة غير المعطاة ترجع للصيدلية كمرتجع', 'An unused dose returns to pharmacy as a return')].map(function (t) { return '<div class="d-flex align-items-center cb-row py-3"><i class="ki-outline ki-check-circle fs-3 text-primary me-3"></i><span class="fw-semibold text-gray-800">' + t + '</span></div>'; }).join('') + '<div class="mt-3">' + H.reuse('المخزون والتحويلات بين المستودعات', 'Inventory and store transfers') + '</div>' }) + '</div></div>';
		return { title: L('توجيه الأدوية', 'Medication routing'), crumbs: [{ t: L('الصيدلية', 'Pharmacy'), href: '#/pharmacy' }, { t: L('التوجيه', 'Routing') }], html: html };
	} });

	/* ================= Practitioners ================= */
	H.route('practitioners', { render: function () {
		var rows = D.staff.doctors.map(function (d) {
			var c = D.contracts[d.id], left = daysTo(c.exp), ty = TYPE(c.type);
			return [H.personCell(d, T(d.title), '#/practitioner/' + d.id), T(D.departments[d.dept]), H.badge(ty[0], ty[1]), '<span class="text-gray-700 fs-7">' + (c.channel === 'payroll' ? L('المرتبات', 'Payroll') : L('مورّد — الحسابات', 'Vendor — payables')) + '</span>',
				c.priv.map(function (k) { return '<span class="hms-chip me-1 mb-1">' + PRIV(k) + '</span>'; }).join(''), H.dateStr(c.exp) + (left < 60 ? '<div>' + H.badge('danger', L('ينتهي خلال ', 'Expires in ') + '<span class="num">' + left + '</span> ' + L('يوم', 'days')) + '</div>' : ''), '<a href="#/practitioner/' + d.id + '" class="btn btn-sm btn-light-primary">' + L('الملف', 'Profile') + '</a>'];
		});
		var html = H.stats([
			{ label: L('أطباء دائمون', 'Permanent doctors'), value: '<span class="num">3</span>', sub: L('عبر المرتبات', 'through payroll') },
			{ label: L('دوام جزئي', 'Part-time'), value: '<span class="num">2</span>', sub: L('عبر المرتبات ببند متغير', 'payroll, variable line') },
			{ label: L('زائرون', 'Visiting'), value: '<span class="num">3</span>', sub: L('عبر الموردين والمدفوعات', 'through vendors & payables') },
			{ label: L('تراخيص تنتهي خلال ٦٠ يوماً', 'Licences expiring in 60 days'), value: '<span class="num">2</span>', cls: 'text-danger', sub: L('يوقف الحجز عند الانتهاء', 'Booking stops on expiry') }]);
		html += H.notice({ icon: 'ki-people', title: L('ملف ممارس واحد، وطريقتان للدفع', 'One practitioner record, two ways to pay'), text: L('البيانات الطبية والصلاحيات في ملف واحد لكل طبيب؛ الدائم والجزئي يُدفع لهما من المرتبات، والزائر يُعامل كمورّد.', 'Clinical data and privileges live in one record per doctor; permanent and part-time are paid through payroll, visiting doctors as vendors.') });
		html += H.card({ title: L('الممارسون الصحيون', 'Practitioners'), toolbar: H.searchBox(), body: H.table([{ t: L('الطبيب', 'Doctor') }, { t: L('القسم', 'Department') }, { t: L('نوع التعاقد', 'Engagement') }, { t: L('قناة الدفع', 'Paid through') }, { t: L('الصلاحيات السريرية', 'Clinical privileges') }, { t: L('انتهاء الترخيص', 'Licence expiry') }, { t: '', cls: 'text-end' }], rows) });
		return { title: L('الممارسون الصحيون', 'Practitioners'), crumbs: [{ t: L('الأطباء والعاملون', 'Doctors & staff') }], actions: H.btn('primary', 'ki-plus', L('إضافة طبيب', 'Add doctor')), html: html };
	} });

	H.route('practitioner', { render: function (id) {
		var d = D.doc(id || 'D2'), c = D.contracts[d.id], ty = TYPE(c.type), left = daysTo(c.exp);
		var banner = '<div class="hms-pbanner p-6 mb-5"><div class="d-flex flex-wrap align-items-center gap-5">' + H.avatar(d, 75) + '<div class="flex-grow-1"><div class="d-flex flex-wrap align-items-center gap-2"><span class="fs-2 fw-bold text-gray-900">' + T(d.name) + '</span>' + H.badge(ty[0], ty[1]) + '</div><div class="text-muted fw-semibold mt-1">' + T(d.title) + ' · ' + T(D.departments[d.dept]) + ' · ' + L('عيادة ', 'Clinic ') + '<span class="num">' + d.room + '</span></div>' +
			'<div class="d-flex flex-wrap gap-2 mt-3"><span class="hms-chip"><i class="ki-outline ki-calendar fs-7"></i>' + T(c.sessions) + '</span><span class="hms-chip"><i class="ki-outline ki-wallet fs-7"></i>' + (c.channel === 'payroll' ? L('يُدفع عبر المرتبات', 'Paid through payroll') : L('يُدفع كمورّد', 'Paid as a vendor')) + '</span></div></div></div></div>' +
			(left < 60 ? H.notice({ kind: 'danger', icon: 'ki-shield-cross', title: L('الترخيص المهني قارب على الانتهاء', 'Professional licence about to expire'), text: L('عند الانتهاء يتوقف حجز مواعيد جديدة لهذا الطبيب تلقائياً حتى رفع الترخيص المجدد.', 'On expiry, new bookings for this doctor stop automatically until the renewed licence is uploaded.'), badge: '<span class="num">' + left + '</span> ' + L('يوم', 'days') }) : '');
		var fees = H.card({ title: L('قواعد الأتعاب', 'Fee rules'), sub: c.channel === 'vendor' ? L('أساس الاحتساب: المحصّل فعلاً (النقدي عند الدفع والتأمين عند قبول المطالبة)', 'Basis: actually collected (cash when paid, insurance when the claim is accepted)') : L('تُضاف كبند متغير في مسير الرواتب', 'Added as a variable line on the payroll run'),
			toolbar: H.btn('light-primary', 'ki-plus', L('قاعدة', 'Rule')), body: H.table([{ t: L('الخدمة', 'Service') }, { t: L('الاحتساب', 'Calculation') }], c.rules.map(function (r) { return ['<span class="fw-bold text-gray-800">' + T(r[0]) + '</span>', T(r[1])]; })) +
				'<div class="mt-4">' + (c.channel === 'vendor' ? H.reuse('الموردون وفواتير الشراء وضريبة الاستقطاع', 'Vendors, purchase invoices and withholding tax') : H.reuse('مسير الرواتب — يحتاج بنود مستحقات متغيرة', 'Payroll run — needs variable earning lines')) + '</div>' });
		var cred = H.card({ title: L('الاعتماد والتراخيص', 'Credentials & licences'), body: H.table([{ t: L('المستند', 'Document') }, { t: L('الرقم', 'Number') }, { t: L('الانتهاء', 'Expiry') }], [
			[L('الترخيص المهني', 'Professional licence'), '<span class="num">' + c.license + '</span>', H.dateStr(c.exp)], [L('شهادة الإنعاش القلبي المتقدم', 'ACLS certificate'), '<span class="num">ACLS-88213</span>', H.dateStr('2027-04-02')], [L('تأمين الأخطاء الطبية', 'Malpractice insurance'), '<span class="num">MP-2026-4471</span>', H.dateStr('2026-12-31')]]) + '<div class="mt-4">' + H.reuse('منصة المستندات مع تنبيهات الانتهاء', 'Document platform with expiry alerts') + '</div>' });
		var priv = H.card({ title: L('الصلاحيات السريرية', 'Clinical privileges'), sub: L('تحدد ما يظهر له في الكشف والتنويم والعمليات', 'Decides what he can do in clinic, wards and theatre'), body: ['opd', 'ip', 'proc', 'surg', 'er', 'anes'].map(function (k) { return '<label class="d-flex align-items-center cb-row py-3"><input type="checkbox" class="form-check-input me-3" ' + (c.priv.indexOf(k) !== -1 ? 'checked' : '') + ' /><span class="fw-semibold text-gray-800">' + PRIV(k) + '</span></label>'; }).join('') });
		var days = [L('السبت', 'Sat'), L('الأحد', 'Sun'), L('الاثنين', 'Mon'), L('الثلاثاء', 'Tue'), L('الأربعاء', 'Wed'), L('الخميس', 'Thu')];
		var on = { D1: [0, 1, 2, 3, 4, 5], D2: [1, 3], D3: [0, 2, 4], D4: [2, 4], D5: [0, 1, 2, 3, 4], D6: [1, 2], D7: [0, 1, 3, 5], D8: [1, 2, 4] }[d.id] || [];
		var sched = H.card({ title: L('أيام التواجد', 'Weekly presence'), body: '<div class="d-flex flex-wrap gap-2">' + days.map(function (x, i) { var a = on.indexOf(i) !== -1; return '<div class="rounded px-5 py-4 text-center fw-bold fs-7" style="min-width:80px;background:' + (a ? '#0E4A9E;color:#fff' : 'var(--bs-gray-100);color:var(--bs-gray-500)') + '">' + x + '</div>'; }).join('') + '</div>' });
		var html = banner + '<div class="row g-5 g-xl-8"><div class="col-xl-7">' + fees + cred + '</div><div class="col-xl-5">' + priv + sched + '</div></div>';
		return { title: T(d.name), crumbs: [{ t: L('الممارسون', 'Practitioners'), href: '#/practitioners' }, { t: T(d.name) }], actions: H.isOn('doctorfees') ? H.link('light-primary', 'ki-wallet', L('كشف الأتعاب', 'Fee statement'), '#/fees') : '', html: html };
	} });

	/* ================= Fee statement ================= */
	H.route('fees', { module: 'doctorfees', render: function () {
		var d = D.doc('D2');
		var lines = [
			['2026-10-04', 'MRN-100231', P('كشف قلب', 'Cardiology consultation'), 'NIC', 300, 300, 40],
			['2026-10-04', 'MRN-100288', P('كشف قلب', 'Cardiology consultation'), 'GOV', 300, 300, 40],
			['2026-10-11', 'MRN-100260', P('قسطرة قلبية تشخيصية', 'Diagnostic cardiac catheterisation'), 'CORP', 14500, 14500, 25],
			['2026-10-13', 'MRN-100342', P('كشف قلب', 'Cardiology consultation'), 'NIC', 300, 0, 40],
			['2026-10-18', 'MRN-100371', P('إيكو القلب', 'Echocardiogram'), 'TKF', 650, 650, 25],
			['2026-10-20', 'MRN-100231', P('زيارة مريض منوّم × ٣', 'Inpatient visit × 3'), 'NIC', 600, 600, null],
			['2026-10-25', 'MRN-100301', P('كشف قلب', 'Cardiology consultation'), 'GHC', 300, 300, 40]
		];
		var total = 0;
		var rows = lines.map(function (l) {
			var share = l[6] == null ? 600 : l[5] * l[6] / 100; // 3 inpatient visits at 200
			total += share; var p = D.patient(l[1]);
			return [H.dateStr(l[0]), H.personCell(p, null, null, 30), T(l[2]), T(D.payer(l[3]).name), H.money(l[4], { bare: true }), l[5] ? H.money(l[5], { bare: true }) : '<span class="text-muted fs-8">' + L('لم يُحصّل بعد', 'Not collected yet') + '</span>', l[6] == null ? '<span class="text-gray-600 fs-8">' + L('٢٠٠ لكل زيارة', '200 / visit') + '</span>' : '<span class="num">' + l[6] + '%</span>', H.money(share, { bare: true, dash: true })];
		});
		var reversal = -120, net = total + reversal;
		var foot = '<tr><td class="ps-4" colspan="7"><span class="fw-bold text-danger">' + L('عكس نصيب بند مرفوض من التأمين (CLB-2026-09G)', 'Reversal for a line rejected by the insurer (CLB-2026-09G)') + '</span></td><td class="text-end pe-4 text-danger num fw-bold">' + H.fmtNum(reversal, H.country().currency.dec) + '</td></tr>' +
			'<tr class="fw-bolder fs-5"><td class="ps-4" colspan="7" style="color:#145DBF">' + L('صافي المستحق', 'Net due') + ' (' + H.cur() + ')</td><td class="text-end pe-4">' + H.money(net, { bare: true }) + '</td></tr>';
		var steps = [[L('أُعد آلياً', 'Generated'), '2026-11-01', true], [L('اعتماد المدير الطبي', 'Medical director approval'), '', false], [L('اعتماد المالية', 'Finance approval'), '', false], [L('فاتورة مورّد وسداد', 'Vendor bill & payment'), '', false]];
		var html = '<div class="card mb-5"><div class="card-body d-flex flex-wrap align-items-center gap-5 py-6">' + H.personCell(d, T(d.title) + ' · ' + H.badge('secondary', L('زائر', 'Visiting')), '#/practitioner/' + d.id, 60) + '<div class="ms-auto d-flex gap-3">' + H.select([{ v: 'D2', t: T(d.name) }, { v: 'D4', t: T(D.doc('D4').name) }, { v: 'D6', t: T(D.doc('D6').name) }], 'D2', 'style="width:220px"') + H.select([L('أكتوبر ٢٠٢٦', 'October 2026'), L('سبتمبر ٢٠٢٦', 'September 2026')], '', 'style="width:170px"') + '</div></div></div>';
		html += H.stats([
			{ label: L('إجمالي المفوتر', 'Total billed'), value: H.money(lines.reduce(function (a, l) { return a + l[4]; }, 0), { bare: true }), sub: '<span class="num">' + lines.length + '</span> ' + L('خدمة', 'services') },
			{ label: L('المحصّل فعلاً', 'Actually collected'), value: H.money(lines.reduce(function (a, l) { return a + l[5]; }, 0), { bare: true }), sub: L('أساس الاحتساب', 'the calculation basis') },
			{ label: L('نصيب الطبيب', 'Doctor share'), value: H.money(total, { bare: true }), sub: L('قبل التعديلات', 'before adjustments') },
			{ label: L('صافي المستحق', 'Net due'), value: H.money(net, { bare: true }), cls: 'text-primary', sub: H.cur() }]);
		html += H.notice({ icon: 'ki-wallet', title: L('يُحتسب على المحصّل', 'Calculated on what was collected'), text: L('الخدمة غير المحصّلة تنتقل لكشف الشهر الذي تُحصّل فيه، والمرفوض من التأمين يُعكس نصيبه تلقائياً.', 'An uncollected service moves to the month it is collected; a line the insurer rejects reverses its share automatically.'), badge: L('أكتوبر ٢٠٢٦', 'October 2026') });
		html += H.card({ title: L('تفاصيل الكشف', 'Statement lines'), body: H.table([{ t: L('التاريخ', 'Date') }, { t: L('المريض', 'Patient') }, { t: L('الخدمة', 'Service') }, { t: L('الجهة', 'Payer') }, { t: L('المفوتر', 'Billed'), cls: 'text-end' }, { t: L('المحصّل', 'Collected'), cls: 'text-end' }, { t: L('النسبة', 'Rate'), cls: 'text-end' }, { t: L('النصيب', 'Share'), cls: 'text-end' }], rows, { foot: foot }) });
		html += '<div class="row g-5"><div class="col-xl-6">' + H.card({ title: L('مسار الاعتماد', 'Approval path'), body: steps.map(function (s, i) { return '<div class="d-flex align-items-center cb-row py-3"><span class="symbol symbol-35px me-4"><span class="symbol-label fw-bold" style="background:' + (s[2] ? '#0E4A9E;color:#fff' : '#E7F0FF;color:#0E4A9E') + '">' + (i + 1) + '</span></span><span class="fw-semibold text-gray-800 flex-grow-1">' + s[0] + '</span>' + (s[1] ? H.dateStr(s[1]) : H.badge('secondary', L('بانتظار', 'Pending'))) + '</div>'; }).join('') }) + '</div><div class="col-xl-6">' +
			H.card({ title: L('القيد عند الاعتماد', 'Journal on approval'), toolbar: H.reuse('الموردون والمدفوعات', 'Vendors & payables'), body: H.table([{ t: L('الحساب', 'Account') }, { t: L('مدين', 'Debit'), cls: 'text-end' }, { t: L('دائن', 'Credit'), cls: 'text-end' }], [
				[L('أتعاب أطباء مستحقة', 'Accrued doctor fees'), H.money(net, { bare: true }), '<span class="hms-zero">—</span>'],
				[L('دائنون — ', 'Payable: ') + T(d.name), '<span class="hms-zero">—</span>', H.money(net, { bare: true })]]) + '<div class="text-muted fs-8 mt-3">' + L('ضريبة الاستقطاع تُخصم عند السداد إن كانت مفعّلة على المورّد حسب إعدادات الدولة.', 'Withholding tax is deducted at payment if enabled on the vendor, per the country settings.') + '</div>' }) + '</div></div>';
		return { title: L('كشف أتعاب الطبيب', 'Doctor fee statement'), crumbs: [{ t: L('الأطباء والعاملون', 'Doctors & staff') }, { t: L('كشوف الأتعاب', 'Fee statements') }],
			actions: H.btn('light-primary', 'ki-printer', L('طباعة', 'Print'), 'onclick="HMS.toast(HMS.L(\'تُطبع من Report Studio\',\'Prints through Report Studio\'),\'info\')"') + H.btn('primary', 'ki-check-circle', L('اعتماد المدير الطبي', 'Medical director approval'), 'onclick="HMS.toast(HMS.L(\'اعتُمد الكشف وأُحيل للمالية\',\'Approved and sent to finance\'))"'), html: html };
	} });

	/* ================= Estimates & deposits ================= */
	H.route('estimates', { module: 'billing', render: function () {
		var p = D.patient('MRN-100260'), y = D.payer(p.payer);
		var items = [[L('أتعاب الجراح', 'Surgeon fee'), 4500], [L('أتعاب التخدير', 'Anaesthesia fee'), 1500], [L('غرفة العمليات — ساعتان', 'Theatre — 2 hours'), 3200], [L('إقامة — ٣ ليالٍ (غرفة خاصة)', 'Stay — 3 nights (private room)'), 2700], [L('دعامة قلبية (مخزون أمانة)', 'Coronary stent (consignment stock)'), 6800], [L('تحاليل وأشعة متوقعة', 'Expected lab & imaging'), 1100], [L('أدوية ومستهلكات', 'Drugs & consumables'), 1400]];
		var gross = items.reduce(function (a, i) { return a + i[1]; }, 0), cov = y.kind === 'ins' ? 0.8 : y.kind === 'corp' ? 1 : 0, ins = gross * cov, pat = gross - ins, dep = Math.round(pat * 1.2 + (cov === 1 ? 2000 : 0));
		var est = H.card({ title: L('تقدير تكلفة إجراء', 'Procedure estimate'), sub: T(p.name) + ' · ' + H.payerLabel(p), body: '<div class="row g-5 mb-6">' +
			H.field(L('الباقة / الإجراء', 'Package / procedure'), H.select([L('قسطرة علاجية + دعامة واحدة', 'Therapeutic cath + one stent'), L('ولادة طبيعية — باقة', 'Normal delivery — package'), L('استئصال المرارة بالمنظار — باقة', 'Laparoscopic cholecystectomy — package')], ''), 'col-md-6') +
			H.field(L('درجة الغرفة', 'Room class'), H.select([L('خاصة', 'Private'), L('مزدوجة', 'Semi-private'), L('جناح VIP', 'VIP suite')], ''), 'col-md-3') + H.field(L('مدة الإقامة المتوقعة', 'Expected stay'), H.input('3', 'class="num"'), 'col-md-3') + '</div>' +
			H.table([{ t: L('البند', 'Item') }, { t: L('التقدير', 'Estimate'), cls: 'text-end' }], items.map(function (i) { return [i[0], H.money(i[1])]; }), { foot: '<tr class="fw-bolder fs-5"><td class="ps-4" style="color:#145DBF">' + L('إجمالي التقدير', 'Estimated total') + '</td><td class="text-end pe-4">' + H.money(gross) + '</td></tr>' }) +
			'<div class="text-muted fs-8 mt-3">' + L('الباقة لها سعر ثابت؛ ما يتجاوز حدودها (أيام إضافية، مضاعفات، دعامة ثانية) يُفوتر خارجها.', 'A package has a fixed price; anything beyond its limits (extra days, complications, a second stent) is billed outside it.') + '</div>' });
		var side = H.card({ title: L('التأمين المقدم المطلوب', 'Required deposit'), body: '<div class="row g-4 mb-5">' + [[L('حصة الجهة الدافعة', 'Payer share'), ins], [L('حصة المريض المتوقعة', 'Expected patient share'), pat], [L('التأمين المطلوب', 'Deposit required'), dep]].map(function (k, i) { return '<div class="col-12 d-flex flex-stack cb-row py-2"><span class="fw-semibold text-gray-700">' + k[0] + '</span><span class="fw-bold fs-4 ' + (i === 2 ? 'text-primary' : 'text-gray-900') + '">' + H.money(k[1]) + '</span></div>'; }).join('') + '</div>' +
			H.notice({ icon: 'ki-shield-tick', title: L('موافقة مسبقة مطلوبة', 'Pre-authorisation needed'), text: L('يُرسل التقدير للجهة الدافعة تلقائياً كطلب موافقة.', 'The estimate goes to the payer automatically as an approval request.'), cls: 'mb-5' }) +
			'<button class="btn btn-primary w-100" onclick="HMS.toast(HMS.L(\'تم تحصيل التأمين المقدم وربطه بالزيارة\',\'Deposit collected and tied to the encounter\'))"><i class="ki-outline ki-wallet fs-4"></i>' + L('تحصيل التأمين المقدم', 'Collect deposit') + '</button>' });
		var open = [['MRN-100231', 5000, 4120], ['MRN-100317', 3000, 2890], ['MRN-100368', 2000, 640], ['MRN-100301', 4000, 4310]];
		var deps = H.card({ title: L('التأمينات المفتوحة للمرضى المنومين', 'Open deposits for inpatients'), sub: L('تنبيه عند استهلاك ٨٠٪ من التأمين', 'Alert when 80% of the deposit is used'), body: H.table([{ t: L('المريض', 'Patient') }, { t: L('التأمين', 'Deposit'), cls: 'text-end' }, { t: L('حصة المريض حتى الآن', 'Patient share so far'), cls: 'text-end' }, { t: L('الاستهلاك', 'Used'), w: '220px' }, { t: '', cls: 'text-end' }],
			open.map(function (o) { var pc = Math.round(o[2] * 100 / o[1]); return [H.personCell(D.patient(o[0]), D.patient(o[0]).mrn, '#/patient/' + o[0], 35), H.money(o[1], { bare: true }), H.money(o[2], { bare: true }), H.progress(Math.min(pc, 100), pc >= 80 ? 'danger' : 'primary'), pc >= 80 ? H.btn('light-primary', 'ki-message-text', L('طلب تأمين إضافي', 'Request top-up'), 'onclick="HMS.toast(HMS.L(\'أُرسل طلب لأهل المريض\',\'Request sent to the family\'))"') : '']; })) + '<div class="mt-4">' + H.reuse('الدفعات تحت الحساب في الذمم المدينة', 'On-account receipts in receivables') + '</div>' });
		var html = '<div class="row g-5 g-xl-8"><div class="col-xl-8">' + est + '</div><div class="col-xl-4">' + side + '</div></div>' + deps;
		return { title: L('التقديرات والتأمين المقدم', 'Estimates & deposits'), crumbs: [{ t: L('المالية', 'Finance') }, { t: L('التقديرات', 'Estimates') }], actions: H.btn('light-primary', 'ki-printer', L('طباعة التقدير للمريض', 'Print estimate for patient'), 'onclick="HMS.toast(HMS.L(\'تُطبع من Report Studio\',\'Prints through Report Studio\'),\'info\')"'), html: html };
	} });

	/* ================= Cashier closing ================= */
	H.route('cashier', { module: 'billing', render: function () {
		var dec = H.country().currency.dec;
		var by = [[L('نقدي', 'Cash'), 18420], [L('بطاقات', 'Cards'), 41250], [L('تحويل بنكي', 'Bank transfer'), 9800], [L('محفظة إلكترونية', 'E-wallet'), 2310]];
		var refunds = 650, float = 1000, expectedCash = float + 18420 - refunds, counted = 18740, variance = counted - expectedCash;
		var denoms = [500, 200, 100, 50, 10, 5, 1], qty = [20, 25, 23, 13, 21, 4, 5];
		var cashierP = { name: P('منى الشريف', 'Mona Alsharif'), title: P('أمينة خزينة', 'Cashier'), photo: '300-24.jpg' };
		var html = '<div class="card mb-5"><div class="card-body d-flex flex-wrap align-items-center gap-5 py-6">' + H.personCell(cashierP, T(cashierP.title) + ' · ' + L('خزينة العيادات · وردية صباحية ٠٧:٠٠–١٥:٠٠', 'Clinics cashier · day shift 07:00–15:00'), null, 55) + '<span class="ms-auto">' + H.reuse('إغلاق الوردية في نقاط البيع (PosShift)', 'POS shift closing (PosShift)') + '</span></div></div>';
		html += H.stats(by.map(function (b) { return { label: b[0], value: H.money(b[1], { bare: true }), sub: H.cur() }; }));
		html += '<div class="row g-5 g-xl-8"><div class="col-xl-6">' + H.card({ title: L('جرد النقدية', 'Cash count'), body: H.table([{ t: L('الفئة', 'Denomination') }, { t: L('العدد', 'Count'), w: '120px' }, { t: L('القيمة', 'Value'), cls: 'text-end' }], denoms.map(function (dn, i) { return ['<span class="num fw-bold">' + dn + '</span>', '<input class="form-control form-control-sm form-control-solid num w-90px" value="' + qty[i] + '" />', H.money(dn * qty[i], { bare: true })]; }), { foot: '<tr class="fw-bolder fs-5"><td class="ps-4" colspan="2" style="color:#145DBF">' + L('المعدود', 'Counted') + '</td><td class="text-end pe-4">' + H.money(counted, { bare: true }) + '</td></tr>' }) }) + '</div><div class="col-xl-6">' +
			H.card({ title: L('ملخص الإغلاق', 'Closing summary'), body: [[L('عهدة الافتتاح', 'Opening float'), float], [L('مقبوضات نقدية', 'Cash receipts'), 18420], [L('مردودات نقدية', 'Cash refunds'), -refunds], [L('النقدية المتوقعة', 'Expected cash'), expectedCash]].map(function (r, i) { return '<div class="d-flex flex-stack cb-row py-3"><span class="fw-semibold ' + (i === 3 ? 'text-gray-900 fw-bold' : 'text-gray-700') + '">' + r[0] + '</span><span class="fw-bold num">' + H.fmtNum(r[1], dec) + '</span></div>'; }).join('') +
				'<div class="mt-5">' + (variance === 0 ? H.notice({ kind: 'success', icon: 'ki-check-circle', title: L('النقدية مطابقة', 'Cash balances'), text: L('لا يوجد فرق.', 'No difference.'), cls: 'mb-0' }) : H.notice({ kind: variance < 0 ? 'danger' : 'info', icon: 'ki-information-5', title: variance < 0 ? L('عجز في الخزينة', 'Cash short') : L('زيادة في الخزينة', 'Cash over'), text: L('يُرحّل الفرق لحساب فروقات الخزينة ويحتاج اعتماد المشرف.', 'The difference posts to the cash over/short account and needs supervisor approval.'), badge: '<span class="num">' + H.fmtNum(variance, dec) + '</span>', cls: 'mb-0' })) + '</div>' }) +
			H.card({ title: L('التسليم', 'Handover'), body: '<div class="row g-4">' + H.field(L('يُسلّم إلى', 'Hand over to'), H.select([L('الخزينة الرئيسية', 'Main treasury'), L('الوردية المسائية', 'Evening shift')], ''), 'col-12') + '</div><button class="btn btn-primary w-100 mt-5" onclick="HMS.toast(HMS.L(\'أُغلقت الوردية ورُحّل القيد\',\'Shift closed and journal posted\'))"><i class="ki-outline ki-lock-2 fs-4"></i>' + L('إغلاق الوردية', 'Close the shift') + '</button>' }) + '</div></div>';
		html += H.card({ title: L('ورديات اليوم', 'Today\'s shifts'), body: H.table([{ t: L('الخزينة', 'Cashier desk') }, { t: L('الأمين', 'Cashier') }, { t: L('الوردية', 'Shift') }, { t: L('المقبوضات', 'Receipts'), cls: 'text-end' }, { t: L('الفرق', 'Variance'), cls: 'text-end' }, { t: L('الحالة', 'State') }], [
			[L('خزينة العيادات', 'Clinics desk'), H.personCell(cashierP, null, null, 30), '<span class="num">07:00–15:00</span>', H.money(71780, { bare: true }), '<span class="num ' + (variance < 0 ? 'text-danger' : '') + '">' + H.fmtNum(variance, dec) + '</span>', H.badge('primary', L('قيد الإغلاق', 'Closing'))],
			[L('خزينة الطوارئ', 'ER desk'), H.personCell({ name: P('خالد منصور', 'Khaled Mansour'), photo: '300-17.jpg' }, null, null, 30), '<span class="num">23:00–07:00</span>', H.money(12460, { bare: true }), '<span class="hms-zero">—</span>', H.badge('success', L('مغلقة', 'Closed'))],
			[L('خزينة التنويم', 'Inpatient desk'), H.personCell({ name: P('ريم عادل', 'Reem Adel'), photo: '300-6.jpg' }, null, null, 30), '<span class="num">07:00–15:00</span>', H.money(38900, { bare: true }), '<span class="hms-zero">—</span>', H.badge('info', L('مفتوحة', 'Open'))]]) });
		return { title: L('إغلاق الخزينة', 'Cashier closing'), crumbs: [{ t: L('المالية', 'Finance') }, { t: L('الخزينة', 'Cashier') }], html: html };
	} });

	/* ================= Ward requisitions ================= */
	H.route('requisitions', { module: 'supply', render: function () {
		var reqSt = function (s) { return { submitted: H.badge('primary', L('مرسل', 'Submitted')), approved: H.badge('info', L('معتمد', 'Approved')), picking: H.badge('primary', L('قيد التجهيز', 'Picking')), dispatched: H.badge('success', L('أُرسل', 'Dispatched')), draft: H.badge('secondary', L('مسودة', 'Draft')) }[s]; };
		var reqs = [['REQ-3310', L('جناح ٣', 'Ward 3'), D.staff.nurses[0], 14, 'stat', 'picking'], ['REQ-3308', L('العناية المركزة', 'ICU'), D.staff.nurses[1], 9, 'urgent', 'approved'], ['REQ-3305', L('العمليات', 'Theatre'), D.staff.nurses[2], 22, 'routine', 'dispatched'], ['REQ-3311', L('الطوارئ', 'Emergency'), D.doc('D7'), 6, 'routine', 'submitted'], ['REQ-3312', L('جناح الولادة', 'Maternity'), D.staff.nurses[0], 11, 'routine', 'draft']];
		var lines = [[L('محلول ملحي ٠٫٩٪ ٥٠٠ مل', 'Normal saline 0.9% 500 ml'), 40, 12, 28, 28, 1240], [L('قفازات معقمة مقاس ٧', 'Sterile gloves size 7'), 200, 60, 140, 140, 5200], [L('كانيولا وريدية ٢٠G', 'IV cannula 20G'), 100, 18, 82, 80, 960], [L('شاش معقم ١٠×١٠', 'Sterile gauze 10×10'), 300, 140, 160, 160, 7400], [L('حقن ٥ مل', 'Syringe 5 ml'), 250, 30, 220, 200, 3100]];
		var html = H.stats([
			{ label: L('طلبات اليوم', 'Requests today'), value: '<span class="num">' + reqs.length + '</span>' },
			{ label: L('متوسط زمن التلبية', 'Average fulfilment'), value: '<span class="num">2.4</span> ' + L('س', 'h') },
			{ label: L('أصناف تحت حد الجناح', 'Items below ward par'), value: '<span class="num">17</span>', cls: 'text-danger' },
			{ label: L('زرعات أمانة مستخدمة', 'Consignment implants used'), value: '<span class="num">3</span>', sub: L('بانتظار أمر شراء', 'awaiting a purchase order') }]);
		html += H.card({ title: L('طلبات الأقسام من المستودع', 'Ward requisitions to stores'), toolbar: H.searchBox(), body: H.table([{ t: L('الطلب', 'Request') }, { t: L('القسم', 'Ward') }, { t: L('مقدم الطلب', 'Requested by') }, { t: L('الأصناف', 'Items'), cls: 'text-end' }, { t: L('الأولوية', 'Priority') }, { t: L('الحالة', 'State') }],
			reqs.map(function (r) { return [H.code(r[0]), '<span class="fw-bold text-gray-800">' + r[1] + '</span>', H.personCell(r[2], null, null, 30), '<span class="num">' + r[3] + '</span>', r[4] === 'stat' ? H.badge('danger', 'STAT') : r[4] === 'urgent' ? H.badge('primary', L('عاجل', 'Urgent')) : H.badge('secondary', L('روتيني', 'Routine')), reqSt(r[5])]; })) });
		html += H.card({ title: 'REQ-3310 · ' + L('جناح ٣', 'Ward 3'), sub: L('الكمية المقترحة = حد الجناح − الرصيد في الجناح', 'Suggested quantity = ward par − ward balance'), toolbar: H.reuse('التحويلات بين المستودعات مع الاعتماد', 'Store transfers with approval'), body: H.table([{ t: L('الصنف', 'Item') }, { t: L('حد الجناح', 'Par'), cls: 'text-end' }, { t: L('الرصيد بالجناح', 'On ward'), cls: 'text-end' }, { t: L('المطلوب', 'Requested'), cls: 'text-end' }, { t: L('المعتمد', 'Approved'), cls: 'text-end' }, { t: L('رصيد المستودع', 'Store stock'), cls: 'text-end' }],
			lines.map(function (l) { return ['<span class="fw-semibold text-gray-800">' + l[0] + '</span>', '<span class="num">' + l[1] + '</span>', '<span class="num ' + (l[2] < l[1] * 0.3 ? 'text-danger fw-bold' : '') + '">' + l[2] + '</span>', '<span class="num">' + l[3] + '</span>', '<input class="form-control form-control-sm form-control-solid num w-80px ms-auto" value="' + l[4] + '" />', '<span class="num text-gray-600">' + H.fmtNum(l[5]) + '</span>']; })) });
		html += '<div class="row g-5"><div class="col-xl-6">' + H.card({ title: L('زرعات مخزون الأمانة', 'Consignment implants'), sub: L('ملك المورّد حتى تُستخدم، وبعدها يُنشأ أمر شراء آلياً', 'Owned by the supplier until used; a purchase order is then raised automatically'), body: H.table([{ t: L('السيريال', 'Serial') }, { t: L('الصنف', 'Item') }, { t: L('المريض', 'Patient') }, { t: L('الحالة', 'State') }], [
			[H.code('STN-88120'), L('دعامة قلبية ٣×١٨', 'Coronary stent 3×18'), H.personCell(D.patient('MRN-100260'), null, null, 30), H.badge('primary', L('أمر شراء مسودة', 'Draft PO'))],
			[H.code('HIP-22051'), L('مسمار عنق الفخذ', 'Hip fixation nail'), H.personCell(D.patient('MRN-100418'), null, null, 30), H.badge('success', L('مفوتر', 'Invoiced'))],
			[H.code('MSH-40017'), L('شبكة فتق', 'Hernia mesh'), '<span class="text-muted fs-8">' + L('في المستودع', 'In store') + '</span>', H.badge('secondary', L('أمانة', 'Consignment'))]]) + '<div class="mt-3">' + H.reuse('السيريال في المخزون — منطق الأمانة جديد', 'Inventory serials — consignment logic is new') + '</div>' }) + '</div><div class="col-xl-6">' +
			H.card({ title: L('سجل الأدوية المخدرة — جناح ٣', 'Controlled drugs register — Ward 3'), sub: L('كل حركة بتوقيع ممرضين', 'Every movement signed by two nurses'), body: H.table([{ t: L('الدواء', 'Drug') }, { t: L('افتتاحي', 'Open'), cls: 'text-end' }, { t: L('وارد', 'In'), cls: 'text-end' }, { t: L('أُعطي', 'Given'), cls: 'text-end' }, { t: L('إتلاف', 'Wasted'), cls: 'text-end' }, { t: L('الرصيد', 'Balance'), cls: 'text-end' }], [
				[L('مورفين ١٠ ملجم أمبول', 'Morphine 10 mg amp'), 10, 5, 4, 1, 10], [L('فنتانيل ١٠٠ مكجم', 'Fentanyl 100 mcg'), 6, 0, 2, 0, 4], [L('ميدازولام ٥ ملجم', 'Midazolam 5 mg'), 8, 4, 3, 0, 9]].map(function (r) { return ['<span class="fw-semibold text-gray-800">' + r[0] + '</span>'].concat(r.slice(1).map(function (v, i) { return '<span class="num ' + (i === 4 ? 'fw-bold' : '') + '">' + v + '</span>'; })); })) }) + '</div></div>';
		return { title: L('طلبات الأقسام', 'Ward requisitions'), crumbs: [{ t: L('الإمداد', 'Supply') }, { t: L('طلبات الأقسام', 'Requisitions') }], actions: H.btn('primary', 'ki-plus', L('طلب جديد', 'New requisition')), html: html };
	} });

	/* ================= Incident reports ================= */
	H.route('incidents', { module: 'quality', render: function () {
		var sev = function (s) { return { near: H.badge('secondary', L('كاد أن يحدث', 'Near miss')), none: H.badge('info', L('بدون ضرر', 'No harm')), mild: H.badge('primary', L('ضرر بسيط', 'Mild harm')), moderate: H.badge('danger', L('ضرر متوسط', 'Moderate harm')), severe: '<span class="badge badge-danger fs-8">' + L('ضرر جسيم', 'Severe harm') + '</span>' }[s]; };
		var st = function (s) { return { new: H.badge('primary', L('جديد', 'New')), review: H.badge('info', L('قيد المراجعة', 'Under review')), rca: H.badge('danger', L('تحليل السبب الجذري', 'Root-cause analysis')), closed: H.badge('success', L('مغلق', 'Closed')) }[s]; };
		var list = [['INC-0412', L('خطأ دوائي — جرعة مضاعفة', 'Medication error — double dose'), L('جناح ٣', 'Ward 3'), 'moderate', 'rca', D.staff.nurses[0], '2026-10-31'], ['INC-0411', L('سقوط مريض', 'Patient fall'), L('جناح ٢', 'Ward 2'), 'mild', 'review', D.staff.nurses[2], '2026-10-30'], ['INC-0410', L('وخز إبرة لموظف', 'Needle-stick injury'), L('الطوارئ', 'Emergency'), 'none', 'closed', null, '2026-10-28'], ['INC-0409', L('عينة بدون ملصق', 'Unlabelled specimen'), L('المختبر', 'Laboratory'), 'near', 'closed', D.staff.nurses[1], '2026-10-27'], ['INC-0408', L('عطل جهاز تسريب', 'Infusion pump failure'), L('العناية المركزة', 'ICU'), 'none', 'new', D.doc('D2'), '2026-11-01']];
		var form = '<div class="row g-5">' + H.field(L('نوع الحادث', 'Incident type'), H.select([L('خطأ دوائي', 'Medication error'), L('سقوط', 'Fall'), L('خطأ في هوية المريض', 'Wrong patient'), L('عطل جهاز', 'Equipment failure'), L('عدوى مكتسبة', 'Hospital-acquired infection'), L('أخرى', 'Other')], '')) + H.field(L('درجة الضرر', 'Harm level'), H.select([L('كاد أن يحدث', 'Near miss'), L('بدون ضرر', 'No harm'), L('ضرر بسيط', 'Mild'), L('ضرر متوسط', 'Moderate'), L('ضرر جسيم', 'Severe')], '')) +
			H.field(L('المكان', 'Location'), H.select(D.wards.map(function (w) { return T(w.name); }), ''), 'col-md-6') + H.field(L('المريض المتأثر (اختياري)', 'Affected patient (optional)'), H.select([{ v: '', t: '—' }].concat(D.patients.slice(0, 12).map(function (p) { return { v: p.mrn, t: T(p.name) }; })), ''), 'col-md-6') +
			H.field(L('ماذا حدث؟', 'What happened?'), '<textarea class="form-control form-control-solid" rows="3"></textarea>', 'col-12') + '</div>' +
			'<label class="form-check form-check-custom form-check-solid mt-5"><input class="form-check-input" type="checkbox" /><span class="form-check-label text-gray-700">' + L('إرسال البلاغ بدون ذكر اسمي', 'Submit without my name') + '</span></label>' +
			'<button class="btn btn-primary w-100 mt-5" onclick="HMS.toast(HMS.L(\'أُرسل البلاغ لإدارة الجودة\',\'Report sent to quality management\'))"><i class="ki-outline ki-send fs-4"></i>' + L('إرسال البلاغ', 'Submit report') + '</button>';
		var html = H.stats([
			{ label: L('بلاغات هذا الشهر', 'Reports this month'), value: '<span class="num">23</span>', sub: L('ثقافة الإبلاغ: ارتفاع جيد', 'Reporting culture: healthy rise') },
			{ label: L('أخطاء دوائية', 'Medication errors'), value: '<span class="num">6</span>' },
			{ label: L('حالات سقوط', 'Falls'), value: '<span class="num">2</span>', sub: '<span class="num">0.8</span> ' + L('لكل ١٠٠٠ يوم مريض', 'per 1,000 patient-days') },
			{ label: L('تحليل سبب جذري مفتوح', 'Open root-cause analyses'), value: '<span class="num">1</span>', cls: 'text-danger', sub: L('مهلة ٤٥ يوماً', '45-day deadline') }]);
		html += H.notice({ icon: 'ki-shield-search', title: L('الإبلاغ للتعلم لا للعقاب', 'Reporting is for learning, not blame'), text: L('أي موظف يبلغ خلال دقيقة، ويمكن الإبلاغ دون ذكر الاسم؛ الحوادث الجسيمة تفتح تحليل سبب جذري تلقائياً.', 'Any staff member can report in a minute, anonymously if they wish; serious events open a root-cause analysis automatically.') });
		html += '<div class="row g-5 g-xl-8"><div class="col-xl-8">' + H.card({ title: L('البلاغات', 'Reports'), toolbar: H.searchBox(), body: H.table([{ t: L('البلاغ', 'Report') }, { t: L('الوصف', 'Description') }, { t: L('المكان', 'Location') }, { t: L('الضرر', 'Harm') }, { t: L('الحالة', 'State') }, { t: L('المبلّغ', 'Reporter') }],
			list.map(function (r) { return [H.code(r[0]), '<div class="fw-semibold text-gray-800">' + r[1] + '</div><div class="text-muted fs-8">' + H.dateStr(r[6]) + '</div>', r[2], sev(r[3]), st(r[4]), r[5] ? H.avatar(r[5], 30) : '<span class="text-muted fs-8">' + L('بدون اسم', 'Anonymous') + '</span>']; })) +
			'<div class="mt-4">' + H.reuse('مهام المتابعة وشكاوى المرضى من تذاكر CRM', 'Follow-up tasks, and patient complaints from CRM tickets') + '</div>' }) + '</div><div class="col-xl-4">' + H.card({ title: L('بلاغ جديد', 'New report'), body: form }) + '</div></div>';
		return { title: L('بلاغات الحوادث وسلامة المرضى', 'Incident reports & patient safety'), crumbs: [{ t: L('الجودة', 'Quality') }, { t: L('البلاغات', 'Incidents') }], html: html };
	} });

	/* ================= Central kitchen & cafeteria ================= */
	// One kitchen feeds three streams — patients, on-duty staff and the paying cafeteria — and its whole
	// monthly cost is pooled, then allocated to the cost centres that consumed it.
	H.route('kitchen', { module: 'kitchen', render: function () {
		var dec = H.country().currency.dec, id = 'kt';
		var pool = [[L('الخامات المصروفة من مخزون المطبخ', 'Ingredients issued from kitchen stores'), 186400, L('المخزون', 'Inventory')], [L('رواتب طاقم المطبخ', 'Kitchen staff payroll'), 92000, L('المرتبات — مركز تكلفة المطبخ', 'Payroll — kitchen cost centre')], [L('غاز وكهرباء ومياه', 'Gas, power & water'), 14600, L('المصروفات', 'Expenses')], [L('مستهلكات وتغليف', 'Disposables & packaging'), 9800, L('المخزون', 'Inventory')], [L('إهلاك معدات المطبخ', 'Kitchen equipment depreciation'), 6200, L('الأصول الثابتة', 'Fixed assets')]];
		var poolTotal = pool.reduce(function (a, p) { return a + p[1]; }, 0);
		// Destinations: [cost centre, stream, meals, standard recipe cost]
		var dest = [
			[L('جناح ٣ — باطنة', 'Ward 3 — medical'), 'patient', 2480, 18.4], [L('جناح ٢ — جراحة', 'Ward 2 — surgical'), 'patient', 1980, 18.4], [L('جناح ٥ — الولادة', 'Ward 5 — maternity'), 'patient', 1560, 19.6], [L('العناية المركزة (تغذية أنبوبية)', 'ICU (tube feeds)'), 'patient', 620, 26.0], [L('الطوارئ والملاحظة', 'ER & observation'), 'patient', 310, 14.0],
			[L('التمريض — وجبة المناوبة', 'Nursing — duty meal'), 'staff', 2600, 12.6], [L('الأطباء — وجبة المناوبة', 'Doctors — duty meal'), 'staff', 900, 12.6], [L('الخدمات المساندة', 'Support services'), 'staff', 1300, 12.6], [L('الإدارة', 'Administration'), 'staff', 600, 12.6],
			[L('الكافيتيريا — تكلفة المبيعات', 'Cafeteria — cost of sales'), 'cafe', 6250, 11.9]];
		var weight = dest.reduce(function (a, d) { return a + d[2] * d[3]; }, 0);
		dest.forEach(function (d) { d.push(poolTotal * d[2] * d[3] / weight); });
		var meals = dest.reduce(function (a, d) { return a + d[2]; }, 0);
		var sum = function (k) { return dest.filter(function (d) { return d[1] === k; }).reduce(function (a, d) { return a + d[4]; }, 0); };
		var streamTxt = function (k) { return { patient: H.badge('primary', L('مرضى', 'Patients')), staff: H.badge('info', L('موظفون', 'Staff')), cafe: H.badge('secondary', L('كافيتيريا', 'Cafeteria')) }[k]; };

		var head = H.stats([
			{ label: L('وجبات أكتوبر', 'Meals in October'), value: '<span class="num">' + H.fmtNum(meals) + '</span>', sub: L('مرضى · موظفون · كافيتيريا', 'patients · staff · cafeteria') },
			{ label: L('تكلفة المطبخ الكلية', 'Total kitchen cost'), value: H.money(poolTotal, { bare: true }), sub: H.cur() },
			{ label: L('متوسط تكلفة الوجبة', 'Average cost per meal'), value: H.money(poolTotal / meals, { bare: true }), sub: L('شامل العمالة والتشغيل', 'including labour and running costs') },
			{ label: L('هامش الكافيتيريا', 'Cafeteria margin'), value: '<span class="num">' + Math.round((142300 - sum('cafe') - 18000) * 100 / 142300) + '%</span>', cls: 'text-success', sub: L('بعد تحميل نصيبها من المطبخ', 'after its share of kitchen cost') }]);
		head += H.notice({ icon: 'ki-shop', title: L('مطبخ واحد، ثلاث جهات مستفيدة', 'One kitchen, three consumers'), text: L('كل وجبة مبنية على وصفة، فتكلفتها معروفة؛ وفي آخر الشهر تُوزَّع تكلفة المطبخ كاملة على مراكز التكلفة حسب ما استهلكته فعلاً.', 'Every meal is built on a recipe, so its cost is known; at month end the whole kitchen cost is allocated to the cost centres by what they actually consumed.'), badge: L('أكتوبر ٢٠٢٦', 'October 2026') });

		/* --- Production today --- */
		var diets = [[L('عادي', 'Regular'), 96], [L('سكري', 'Diabetic'), 41], [L('قليل الملح', 'Low salt'), 28], [L('كلوي', 'Renal'), 12], [L('لين', 'Soft'), 17], [L('تغذية أنبوبية', 'Tube feed'), 6], [L('صائم', 'NPO'), 9]];
		var rounds = [[L('الإفطار', 'Breakfast'), '06:30', 'done', 196, 168, 410], [L('الغداء', 'Lunch'), '12:00', 'live', 201, 182, 640], [L('العشاء', 'Dinner'), '18:00', 'planned', 199, 150, 0], [L('وجبات خفيفة', 'Snacks'), '15:30', 'planned', 88, 0, 0]];
		var prod = '<div class="row g-5"><div class="col-xl-7">' + H.card({ title: L('جولات اليوم', 'Today\'s rounds'), body: H.table([{ t: L('الجولة', 'Round') }, { t: L('الوقت', 'Time') }, { t: L('مرضى', 'Patients'), cls: 'text-end' }, { t: L('موظفون', 'Staff'), cls: 'text-end' }, { t: L('كافيتيريا', 'Cafeteria'), cls: 'text-end' }, { t: L('الحالة', 'State') }],
			rounds.map(function (r) { return ['<span class="fw-bold text-gray-800">' + r[0] + '</span>', '<span class="num">' + r[1] + '</span>', '<span class="num">' + r[3] + '</span>', r[4] ? '<span class="num">' + r[4] + '</span>' : '<span class="hms-zero">—</span>', r[5] ? '<span class="num">' + r[5] + '</span>' : '<span class="hms-zero">—</span>', r[2] === 'done' ? H.badge('success', L('وُزّعت', 'Served')) : r[2] === 'live' ? '<span class="badge hms-badge-solid fs-8">' + L('قيد التوزيع', 'Serving') + '</span>' : H.badge('secondary', L('مجدولة', 'Planned'))]; })) }) +
			H.card({ title: L('بطاقة وصفة: غداء سكري', 'Recipe card: diabetic lunch'), sub: L('التكلفة المعيارية تُحسب من آخر تكلفة للخامات', 'Standard cost comes from the latest ingredient cost'), toolbar: H.reuse('الوصفات (BOM) ومطعم نقاط البيع', 'Recipes (BOM) and POS restaurant'), body: H.table([{ t: L('المكوّن', 'Ingredient') }, { t: L('الكمية', 'Qty'), cls: 'text-end' }, { t: L('التكلفة', 'Cost'), cls: 'text-end' }], [
				[L('صدر دجاج مشوي', 'Grilled chicken breast'), '<span class="num">150 g</span>', H.money(7.8, { bare: true })], [L('أرز بني', 'Brown rice'), '<span class="num">120 g</span>', H.money(1.9, { bare: true })], [L('خضار سوتيه', 'Sautéed vegetables'), '<span class="num">150 g</span>', H.money(3.1, { bare: true })], [L('سلطة + زبادي قليل الدسم', 'Salad + low-fat yoghurt'), '<span class="num">1</span>', H.money(3.6, { bare: true })], [L('فاكهة + تغليف', 'Fruit + packaging'), '<span class="num">1</span>', H.money(2.0, { bare: true })]],
				{ foot: '<tr class="fw-bolder fs-6"><td class="ps-4" colspan="2" style="color:#145DBF">' + L('تكلفة الخامات للوجبة', 'Ingredient cost per meal') + '</td><td class="text-end pe-4">' + H.money(18.4, { bare: true }) + '</td></tr>' }) }) + '</div><div class="col-xl-5">' +
			H.card({ title: L('وجبات المرضى حسب النظام الغذائي', 'Patient meals by diet'), sub: L('من أوامر الأطباء في التغذية العلاجية', 'From doctors\' orders in clinical dietary'), body: diets.map(function (dt) { return '<div class="d-flex align-items-center cb-row py-3"><span class="fw-semibold text-gray-800 flex-grow-1">' + dt[0] + '</span><span class="num fw-bold">' + dt[1] + '</span></div>'; }).join('') + (H.isOn('dietary') ? '<div class="mt-4">' + H.link('light-primary', 'ki-arrow-left', L('التغذية العلاجية', 'Clinical dietary'), '#/dietary') + '</div>' : '') }) + '</div></div>';

		/* --- Patient meals --- */
		var pm = dest.filter(function (d) { return d[1] === 'patient'; });
		var patients = H.card({ title: L('وجبات المرضى على مراكز التكلفة', 'Patient meals by cost centre'), sub: L('تكلفة وجبة المريض جزء من تكلفة الجناح، لا تُفوتر للمريض إلا الإضافات', 'A patient meal is part of the ward\'s cost; only extras are billed to the patient'),
			body: H.table([{ t: L('مركز التكلفة', 'Cost centre') }, { t: L('الوجبات', 'Meals'), cls: 'text-end' }, { t: L('التكلفة المعيارية', 'Standard cost'), cls: 'text-end' }, { t: L('التكلفة المحمّلة', 'Allocated cost'), cls: 'text-end' }],
				pm.map(function (d) { return ['<span class="fw-bold text-gray-800">' + d[0] + '</span>', '<span class="num">' + H.fmtNum(d[2]) + '</span>', H.money(d[3], { bare: true }), H.money(d[4], { bare: true })]; }),
				{ foot: '<tr class="fw-bolder fs-5"><td class="ps-4" colspan="3" style="color:#145DBF">' + L('إجمالي وجبات المرضى', 'Total patient meals') + '</td><td class="text-end pe-4">' + H.money(sum('patient'), { bare: true }) + '</td></tr>' }) });
		patients += '<div class="row g-5"><div class="col-xl-6">' + H.card({ title: L('ما يُفوتر للمريض', 'What is billed to the patient'), body: [[L('وجبات المرافق', 'Companion meals'), '<span class="num">380</span> ' + L('وجبة', 'meals'), L('سطر على فاتورة الزيارة بالضريبة العادية', 'A line on the encounter bill at the standard tax rate')], [L('قائمة VIP الإضافية', 'VIP à-la-carte menu'), '<span class="num">64</span> ' + L('طلب', 'orders'), L('بسعر قائمة الأسعار', 'At price-list price')], [L('وجبة المريض الأساسية', 'The patient\'s standard meal'), '—', L('ضمن سعر الإقامة — لا تُفوتر منفصلة', 'Inside the room rate — not billed separately')]].map(function (r) { return '<div class="cb-row py-3"><div class="d-flex flex-stack"><span class="fw-bold text-gray-800">' + r[0] + '</span><span class="fw-semibold">' + r[1] + '</span></div><div class="text-muted fs-8 mt-1">' + r[2] + '</div></div>'; }).join('') }) + '</div><div class="col-xl-6">' +
			H.card({ title: L('سلامة غذاء المريض', 'Patient food safety'), body: [L('حساسية المريض تمنع الصنف من وصفته تلقائياً', 'A patient\'s allergy blocks the item from his tray automatically'), L('أمر «صائم» قبل العملية يوقف الوجبات حتى يُلغى', 'A pre-operative NPO order stops meals until it is lifted'), L('ملصق الصينية باسم المريض والسرير والنظام الغذائي', 'Tray label with patient, bed and diet'), L('عينة محفوظة من كل وجبة ٧٢ ساعة', 'A retained sample of every meal for 72 hours')].map(function (t) { return '<div class="d-flex align-items-center cb-row py-3"><i class="ki-outline ki-check-circle fs-3 text-primary me-3"></i><span class="fw-semibold text-gray-800">' + t + '</span></div>'; }).join('') }) + '</div></div>';

		/* --- Cafeteria --- */
		var outlets = [[L('الكافيتيريا الرئيسية', 'Main cafeteria'), L('الدور الأرضي', 'Ground floor'), 98400, 'open'], [L('كشك القهوة', 'Coffee kiosk'), L('مدخل العيادات', 'Clinics entrance'), 31200, 'open'], [L('مطعم الموظفين', 'Staff dining hall'), L('الدور الثالث', 'Third floor'), 12700, 'open']];
		var cafe = '<div class="row g-5 mb-5">' + outlets.map(function (o) { return '<div class="col-md-4"><div class="card h-100"><div class="card-body p-6"><div class="d-flex flex-stack mb-2"><span class="fw-bold fs-5 text-gray-900">' + o[0] + '</span>' + H.badge('success', L('مفتوح', 'Open')) + '</div><div class="text-muted fs-7 mb-4">' + o[1] + '</div><div class="text-muted fs-8">' + L('مبيعات أكتوبر', 'October sales') + '</div><div class="fs-2 fw-bold">' + H.money(o[2]) + '</div></div></div></div>'; }).join('') + '</div>';
		var mix = [[L('نقدي', 'Cash'), 38], [L('بطاقات', 'Cards'), 41], [L('خصم من راتب الموظف', 'Staff payroll deduction'), 14], [L('بدل وجبة الموظف', 'Staff meal allowance'), 7]];
		cafe += '<div class="row g-5"><div class="col-xl-6">' + H.card({ title: L('طرق الدفع', 'Payment mix'), toolbar: H.reuse('نقاط البيع — المطعم والكاشير', 'POS — restaurant and cashier'), body: mix.map(function (m) { return '<div class="cb-row py-3"><div class="d-flex flex-stack mb-2"><span class="fw-semibold text-gray-800">' + m[0] + '</span><span class="num fw-bold">' + m[1] + '%</span></div><div class="progress h-6px bg-light-primary"><div class="progress-bar bg-primary" style="width:' + m[1] + '%"></div></div></div>'; }).join('') +
			'<div class="text-muted fs-8 mt-3">' + L('الخصم من الراتب يحتاج بنود مستحقات/استقطاعات متغيرة في المرتبات — نفس الفجوة المسجلة لأتعاب الأطباء.', 'Payroll deduction needs variable earning/deduction lines in payroll — the same gap recorded for doctor fees.') + '</div>' }) + '</div><div class="col-xl-6">' +
			H.card({ title: L('ربح وخسارة الكافيتيريا — أكتوبر', 'Cafeteria P&L — October'), body: [[L('المبيعات', 'Sales'), 142300, ''], [L('تكلفة الوجبات (نصيبها من المطبخ)', 'Meal cost (its share of the kitchen)'), -sum('cafe'), ''], [L('رواتب طاقم الكافيتيريا', 'Cafeteria staff payroll'), -18000, ''], [L('صافي ربح الكافيتيريا', 'Cafeteria net profit'), 142300 - sum('cafe') - 18000, 'total']].map(function (r) { return '<div class="d-flex flex-stack cb-row py-3"><span class="' + (r[2] ? 'fw-bold text-gray-900' : 'fw-semibold text-gray-700') + '">' + r[0] + '</span><span class="num fw-bold ' + (r[2] ? 'fs-4 text-success' : r[1] < 0 ? 'text-danger' : '') + '">' + H.fmtNum(r[1], dec) + '</span></div>'; }).join('') + '<div class="text-muted fs-8 mt-3">' + L('الكافيتيريا مركز ربح مستقل له قائمة دخل خاصة.', 'The cafeteria is its own profit centre with its own income statement.') + '</div>' }) + '</div></div>';

		/* --- Costing & allocation --- */
		var cost = H.card({ title: L('وعاء تكلفة المطبخ', 'Kitchen cost pool'), sub: L('كل ما صرفه المطبخ في الشهر، من مصدره', 'Everything the kitchen spent in the month, by source'), body: H.table([{ t: L('البند', 'Item') }, { t: L('المصدر', 'Source') }, { t: L('المبلغ', 'Amount'), cls: 'text-end' }], pool.map(function (p) { return ['<span class="fw-semibold text-gray-800">' + p[0] + '</span>', '<span class="text-muted fs-7">' + p[2] + '</span>', H.money(p[1], { bare: true })]; }),
			{ foot: '<tr class="fw-bolder fs-5"><td class="ps-4" colspan="2" style="color:#145DBF">' + L('الإجمالي', 'Total') + '</td><td class="text-end pe-4">' + H.money(poolTotal, { bare: true }) + '</td></tr>' }) });
		cost += H.card({ title: L('توزيع التكلفة على مراكز التكلفة', 'Allocation to cost centres'), sub: L('أساس التوزيع: عدد الوجبات × التكلفة المعيارية للوصفة', 'Basis: meals × the recipe\'s standard cost'), body: H.table([{ t: L('مركز التكلفة', 'Cost centre') }, { t: L('الجهة', 'Stream') }, { t: L('الوجبات', 'Meals'), cls: 'text-end' }, { t: L('النسبة', 'Share'), cls: 'text-end' }, { t: L('المبلغ المحمّل', 'Allocated'), cls: 'text-end' }],
			dest.map(function (d) { return ['<span class="fw-bold text-gray-800">' + d[0] + '</span>', streamTxt(d[1]), '<span class="num">' + H.fmtNum(d[2]) + '</span>', '<span class="num">' + (d[4] * 100 / poolTotal).toFixed(1) + '%</span>', H.money(d[4], { bare: true })]; }),
			{ foot: '<tr class="fw-bolder fs-5"><td class="ps-4" colspan="4" style="color:#145DBF">' + L('الإجمالي = وعاء التكلفة', 'Total = cost pool') + '</td><td class="text-end pe-4">' + H.money(poolTotal, { bare: true }) + '</td></tr>' }) });
		var theo = 179900, actual = 186400, waste = 2900;
		cost += '<div class="row g-5"><div class="col-xl-6">' + H.card({ title: L('الفعلي مقابل النظري للخامات', 'Actual vs theoretical ingredients'), toolbar: H.reuse('تحليل استهلاك المطعم', 'Restaurant consumption analysis'), body: [[L('النظري (الوصفات × الوجبات المقدَّمة)', 'Theoretical (recipes × meals served)'), theo], [L('الفعلي (المصروف من المخزون)', 'Actual (issued from stores)'), actual], [L('هالك مسجّل (بواقي وإنتاج زائد)', 'Recorded waste (leftovers, over-production)'), waste], [L('فرق غير مبرر', 'Unexplained variance'), actual - theo - waste]].map(function (r, i) { return '<div class="d-flex flex-stack cb-row py-3"><span class="fw-semibold ' + (i === 3 ? 'text-danger fw-bold' : 'text-gray-700') + '">' + r[0] + '</span><span class="num fw-bold ' + (i === 3 ? 'text-danger' : '') + '">' + H.fmtNum(r[1], dec) + '</span></div>'; }).join('') + '<div class="text-muted fs-8 mt-3">' + L('الفرق ', 'Variance ') + '<span class="num">' + ((actual - theo) * 100 / theo).toFixed(1) + '%</span>' + L(' — يُراجع شهرياً مع مشرف المطبخ.', ' — reviewed monthly with the kitchen supervisor.') + '</div>' }) + '</div><div class="col-xl-6">' +
			H.card({ title: L('قيد التوزيع الشهري', 'Monthly allocation journal'), toolbar: H.reuse('مراكز التكلفة على سطور القيد', 'Cost centres on journal lines'), body: H.table([{ t: L('الحساب / مركز التكلفة', 'Account / cost centre') }, { t: L('مدين', 'Debit'), cls: 'text-end' }, { t: L('دائن', 'Credit'), cls: 'text-end' }], [
				[L('مصروف وجبات المرضى — مراكز الأجنحة', 'Patient meals expense — ward centres'), H.money(sum('patient'), { bare: true }), '<span class="hms-zero">—</span>'],
				[L('مزايا الموظفين: وجبات — مراكز الأقسام', 'Staff benefits: meals — department centres'), H.money(sum('staff'), { bare: true }), '<span class="hms-zero">—</span>'],
				[L('تكلفة مبيعات الكافيتيريا', 'Cafeteria cost of sales'), H.money(sum('cafe'), { bare: true }), '<span class="hms-zero">—</span>'],
				[L('وعاء تكلفة المطبخ (حساب وسيط)', 'Kitchen cost pool (clearing)'), '<span class="hms-zero">—</span>', H.money(poolTotal, { bare: true })]],
				{ foot: '<tr class="fw-bolder fs-6"><td class="ps-4" style="color:#145DBF">' + L('الإجمالي', 'Total') + '</td><td class="text-end">' + H.money(poolTotal, { bare: true }) + '</td><td class="text-end pe-4">' + H.money(poolTotal, { bare: true }) + '</td></tr>' }) }) + '</div></div>';

		var html = head + H.tabs(id, [{ k: 'p', t: L('الإنتاج اليوم', 'Production today') }, { k: 'm', t: L('وجبات المرضى', 'Patient meals') }, { k: 'c', t: L('الكافيتيريا', 'Cafeteria') }, { k: 'k', t: L('التكاليف والتوزيع', 'Costing & allocation') }], 'p') +
			'<div class="tab-content">' + H.pane(id, 'p', true, prod) + H.pane(id, 'm', false, patients) + H.pane(id, 'c', false, cafe) + H.pane(id, 'k', false, cost) + '</div>';
		return { title: L('المطبخ المركزي والكافيتيريا', 'Central kitchen & cafeteria'), crumbs: [{ t: L('الخدمات المساندة', 'Support services') }, { t: L('المطبخ والكافيتيريا', 'Kitchen & cafeteria') }], actions: H.btn('primary', 'ki-calculator', L('تشغيل توزيع الشهر', 'Run month allocation'), 'onclick="HMS.toast(HMS.L(\'رُحّل قيد توزيع تكلفة المطبخ\',\'Kitchen cost allocation posted\'))"'), html: html };
	} });

	/* ================= Blueprint: the gap map ================= */
	H.blueprintGaps = function () {
		var s = function (k) { return { ok: H.badge('success', L('موجود', 'Exists')), part: H.badge('primary', L('يحتاج توسعة', 'Extend')), new: H.badge('secondary', L('جديد', 'New')) }[k]; };
		var groups = [
			[L('الأطباء والعاملون', 'Doctors & staff'), [[L('ملف الممارس والتراخيص والصلاحيات', 'Practitioner record, licences, privileges'), 'new'], [L('أتعاب الزائر كمورّد + ضريبة الاستقطاع', 'Visiting fees as vendor + withholding tax'), 'ok'], [L('أتعاب الجزئي في المرتبات', 'Part-time fees on payroll'), 'part'], [L('جداول الورديات', 'Shift rosters'), 'ok'], [L('المناوبات عند الطلب', 'On-call'), 'new']]],
			[L('مالية المريض', 'Patient finance'), [[L('التأمين المقدم وعرض السعر', 'Deposits and estimates'), 'part'], [L('الباقات واستثناءاتها', 'Packages and exclusions'), 'part'], [L('حد ائتمان الشركات', 'Corporate credit limit'), 'ok'], [L('اعتماد الخصم بسلسلة', 'Discount approval chain'), 'part'], [L('إغلاق خزينة طبية', 'Medical cashier closing'), 'part'], [L('الحالات الخيرية وكشف حساب المريض', 'Charity cases, patient statement'), 'new']]],
			[L('دورة الإيراد', 'Revenue cycle'), [[L('رسوم آلية (إقامة السرير ليلياً)', 'Automatic charges (nightly bed)'), 'new'], [L('فحص المطالبة قبل الرفع', 'Claim scrubbing'), 'new'], [L('مطابقة إشعارات السداد', 'Remittance matching'), 'new'], [L('عكس نصيب الطبيب عند الرفض', 'Doctor-share reversal on rejection'), 'new']]],
			[L('السجلات الطبية', 'Medical records'), [[L('ملخص الخروج والتقارير الطبية', 'Discharge summary, medical reports'), 'new'], [L('الإجازات المرضية والمواليد والوفيات', 'Sick leave, births, deaths'), 'new'], [L('ربط المولود بأمه ومريض مجهول', 'Newborn-to-mother, unknown patient'), 'new'], [L('دمج الملفات المكررة', 'Duplicate merge'), 'new'], [L('الموافقات والتوقيع الإلكتروني', 'Consents & e-signature'), 'new'], [L('مستندات المريض', 'Patient documents'), 'part']]],
			[L('الإمداد الطبي', 'Medical supply'), [[L('الدفعات والصلاحية والسيريال', 'Batches, expiry, serials'), 'ok'], [L('طلبات الأقسام', 'Ward requisitions'), 'new'], [L('مخزون الأمانة', 'Consignment stock'), 'part'], [L('سجل المخدرات', 'Controlled-drug register'), 'new'], [L('توجيه الأدوية حتى السرير', 'Medication routing to bedside'), 'new'], [L('طلب الشراء العام', 'General purchase request'), 'new']]],
			[L('الجودة والاعتماد', 'Quality & accreditation'), [[L('بلاغات الحوادث', 'Incident reports'), 'new'], [L('الشكاوى', 'Complaints'), 'ok'], [L('استبيانات الرضا', 'Satisfaction surveys'), 'new'], [L('سجل من فتح الملف', 'Record-access log'), 'new'], [L('الوصول الطارئ', 'Break-glass access'), 'new']]],
			[L('الخدمات المساندة', 'Support services'), [[L('الصيانة الوقائية والمعايرة', 'Preventive maintenance & calibration'), 'ok'], [L('أوامر عمل الأجهزة', 'Equipment work orders'), 'new'], [L('النظافة والنقل والمغسلة', 'Housekeeping, porters, laundry'), 'new'], [L('المطبخ المركزي والكافيتيريا وتوزيع التكلفة', 'Central kitchen, cafeteria, cost allocation'), 'part'], [L('خريطة المستشفى ومن في كل غرفة', 'Hospital map, who is in each room'), 'new']]],
			[L('التكامل والقنوات', 'Integration & channels'), [[L('رسائل SMS', 'SMS'), 'new'], [L('واتساب', 'WhatsApp'), 'part'], [L('نظام الدور والكشك', 'Queue & kiosk'), 'new'], [L('بوابة مريض بالحجز والدفع', 'Portal with booking & payment'), 'part'], [L('HL7 / FHIR / DICOM / أجهزة المعمل', 'HL7 / FHIR / DICOM / analysers'), 'new'], [L('المنصات الوطنية', 'National platforms'), 'new']]]
		];
		var decisions = [[L('أساس أتعاب الطبيب', 'Doctor-fee basis'), L('المحصّل (مُوصى به)', 'Collected (recommended)')], [L('الفروع', 'Branches'), L('شركة لكل منشأة', 'One company per facility')], [L('أول دولة', 'First country'), L('بانتظار القرار', 'Awaiting decision')], [L('عمولات الأطباء المحولين', 'Referral commissions'), L('لا تُبنى إلا بتأكيد قانوني', 'Only with legal confirmation')]];
		return H.card({ title: L('النواقص بعد التحليل المعمّق', 'Gaps after the deep analysis'), sub: L('حالة كل احتياج في النظام الحالي', 'The state of each need in the current system'), body: '<div class="row g-5">' + groups.map(function (g) {
			return '<div class="col-md-6 col-xl-3"><div class="border border-dashed rounded p-5 h-100" style="border-color:#C9D8F2 !important"><div class="fw-bold text-gray-900 fs-6 mb-3">' + g[0] + '</div>' + g[1].map(function (i) { return '<div class="d-flex flex-stack py-2 gap-3"><span class="fs-7 text-gray-700">' + i[0] + '</span>' + s(i[1]) + '</div>'; }).join('') + '</div></div>';
		}).join('') + '</div>' }) + H.card({ title: L('قرارات مطلوبة من الإدارة', 'Decisions needed'), body: H.table([{ t: L('القرار', 'Decision') }, { t: L('التوصية / الحالة', 'Recommendation / state') }], decisions) });
	};
})();
