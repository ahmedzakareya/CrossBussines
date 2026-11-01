/* Rooms as rooms: a room has a type, a number of beds, features and a daily rate, and a patient is
   accommodated into a BED inside a room. Replaces the bed-per-tile board and the admission dialog. */
(function () {
	'use strict';
	var H = HMS, L = H.L, T = H.T, D = H.data;
	function P(ar, en) { return { ar: ar, en: en }; }

	/* ---------------- Room types (a settings catalogue) ---------------- */
	// feat keys: bath, companion, tv, fridge, lounge, oxygen, negp (negative pressure), ante (anteroom), monitor
	D.roomTypes = {
		vip: { name: P('جناح VIP', 'VIP suite'), beds: 1, rate: 2500, feat: ['bath', 'companion', 'lounge', 'tv', 'fridge', 'oxygen'], gender: 'any' },
		private: { name: P('غرفة خاصة', 'Private room'), beds: 1, rate: 1200, feat: ['bath', 'companion', 'tv', 'oxygen'], gender: 'any' },
		semi: { name: P('غرفة مزدوجة', 'Semi-private room'), beds: 2, rate: 750, feat: ['bath', 'tv', 'oxygen'], gender: 'same' },
		ward4: { name: P('عنبر ٤ أسرّة', 'Four-bed bay'), beds: 4, rate: 450, feat: ['oxygen'], gender: 'same' },
		isolation: { name: P('غرفة عزل (ضغط سالب)', 'Isolation room (negative pressure)'), beds: 1, rate: 1500, feat: ['bath', 'negp', 'ante', 'oxygen'], gender: 'any' },
		icu: { name: P('عناية مركزة — صالة مفتوحة', 'ICU — open bay'), beds: 4, rate: 2400, feat: ['monitor', 'oxygen'], gender: 'any' }
	};
	var FEAT = function (k) { return { bath: L('حمام خاص', 'Private bathroom'), companion: L('سرير مرافق', 'Companion bed'), tv: L('تلفاز', 'TV'), fridge: L('ثلاجة', 'Fridge'), lounge: L('صالة استقبال', 'Sitting area'), oxygen: L('أكسجين مركزي', 'Piped oxygen'), negp: L('ضغط سالب', 'Negative pressure'), ante: L('غرفة انتقالية', 'Anteroom'), monitor: L('مراقبة قلبية', 'Cardiac monitoring') }[k]; };
	var RANK = ['ward4', 'semi', 'private', 'vip'];
	// What a payer class is entitled to; an upgrade above it is paid by the patient.
	H.entitlement = function (p) {
		var y = D.payer(p.payer);
		if (y.kind === 'cash') return null;
		if (y.kind === 'corp') return 'private';
		if (y.kind === 'gov') return 'semi';
		return { VIP: 'vip', Gold: 'private', A: 'private', Silver: 'semi', B: 'semi', C: 'ward4' }[p.cls] || 'semi';
	};

	/* ---------------- Rooms over the existing beds ---------------- */
	// [ward, room number, type, bed numbers, gender policy of the ward]
	var LAYOUT = [
		['W3', '301', 'private', ['301']], ['W3', '302', 'semi', ['302', '303']], ['W3', '304', 'ward4', ['304', '305', '306', '307']], ['W3', '308', 'isolation', ['308']], ['W3', '309', 'vip', ['309']], ['W3', '310', 'private', ['310']],
		['W2', '201', 'semi', ['201', '202']], ['W2', '203', 'ward4', ['203', '204', '205', '206']], ['W2', '207', 'private', ['207']], ['W2', '208', 'private', ['208']],
		['W5', '501', 'vip', ['501']], ['W5', '502', 'private', ['502']], ['W5', '503', 'semi', ['503', '504']], ['W5', '505', 'semi', ['505', '506']],
		['ICU', 'ICU-A', 'icu', ['I01', 'I02', 'I03', 'I04']]
	];
	var WARDSEX = { W3: 'M', W5: 'F' };
	D.rooms = LAYOUT.map(function (r) {
		var w = D.wards.filter(function (x) { return x.id === r[0]; })[0];
		return { no: r[1], ward: w, type: r[2], beds: r[3].map(function (n) { return w.beds.filter(function (b) { return b.no === n; })[0]; }).filter(Boolean) };
	});
	// What the admin changed in the designer: rooms added on a floor, and a room's type.
	H.store.get('hms.customRooms', []).forEach(function (c) {
		var w = D.wards.filter(function (x) { return x.id === c.ward; })[0]; if (!w || D.rooms.some(function (r) { return r.no === c.no; })) return;
		var beds = [];
		for (var i = 1; i <= c.beds; i++) { var b = { no: c.no + '-' + i, st: 'vacant' }; w.beds.push(b); beds.push(b); }
		D.rooms.push({ no: c.no, ward: w, type: c.type, beds: beds, custom: true });
	});
	var extraBeds = H.store.get('hms.extraBeds', {}), removedBeds = H.store.get('hms.removedBeds', {});
	D.rooms.forEach(function (r) {
		(extraBeds[r.no] || []).forEach(function (no) { if (!r.beds.some(function (b) { return b.no === no; })) { var b = { no: no, st: 'vacant' }; r.beds.push(b); r.ward.beds.push(b); } });
		// A drawn-out bed leaves the room only if nobody lies in it.
		r.beds = r.beds.filter(function (b) { return !removedBeds[b.no] || b.p; });
		r.ward.beds = r.ward.beds.filter(function (b) { return !removedBeds[b.no] || b.p; });
	});
	var typeOverride = H.store.get('hms.roomTypes', {});
	D.rooms.forEach(function (r) { if (typeOverride[r.no] && D.roomTypes[typeOverride[r.no]]) r.type = typeOverride[r.no]; });
	H.roomsOfWard = function (wid) { return D.rooms.filter(function (r) { return r.ward.id === wid; }); };
	H.roomOfBed = function (no) { return D.rooms.filter(function (r) { return r.beds.some(function (b) { return b.no === no; }); })[0]; };
	H.roomByNo = function (no) { return D.rooms.filter(function (r) { return r.no === no; })[0]; };
	function roomSex(r) { var occ = r.beds.filter(function (b) { return b.p && b.st !== 'reserved'; }).map(function (b) { return D.patient(b.p).sex; }); return occ[0] || WARDSEX[r.ward.id] || null; }
	function occupied(r) { return r.beds.filter(function (b) { return b.st === 'occupied'; }).length; }

	/* ---------------- The drawn room plan ---------------- */
	// Beds are drawn where they physically stand; a bed is clickable. mode 'board' opens the bed,
	// mode 'pick' selects it for accommodation.
	// A room's interior is a list of items in percent of the room: {k, x, y, w, h, no?}. The admin's
	// drawing (room designer) is stored per room; without one, a default is derived from the type.
	H.defaultRoomItems = function (r) {
		var t = D.roomTypes[r.type], n = r.beds.length, f = t.feat, it = [];
		it.push({ k: 'window', x: 25, y: -1.5, w: 50, h: 3 }, { k: 'door', x: 76, y: 98, w: 16, h: 3 });
		if (f.indexOf('bath') !== -1) it.push({ k: 'bath', x: 0, y: 66, w: 22, h: 34 });
		if (f.indexOf('ante') !== -1) it.push({ k: 'ante', x: 78, y: 66, w: 22, h: 34 });
		if (f.indexOf('companion') !== -1) it.push({ k: 'companion', x: 28, y: 76, w: 30, h: 16 });
		if (f.indexOf('lounge') !== -1) it.push({ k: 'lounge', x: 72, y: 8, w: 24, h: 36 });
		if (n === 2) it.push({ k: 'curtain', x: 50, y: 8, w: 0.6, h: 62 });
		// Beds sit against the window wall so they never cover the bathroom in the door corner.
		var pos = n === 1 ? [[38, 10]] : n === 2 ? [[14, 10], [62, 10]] : r.type === 'icu' ? [[3, 34], [27, 34], [51, 34], [75, 34]] : [[8, 14], [56, 14], [8, 58], [56, 58]];
		var bw = n <= 2 ? 26 : r.type === 'icu' ? 21 : 30, bh = n === 4 && r.type !== 'icu' ? 30 : n <= 2 ? 50 : 46;
		r.beds.forEach(function (b, i) { var p = pos[i] || [5 + (i % 4) * 24, 10 + Math.floor(i / 4) * 40]; it.push({ k: 'bed', no: b.no, x: p[0], y: p[1], w: bw, h: bh }); });
		return it;
	};
	H.roomItems = function (r) {
		var saved = H.store.get('hms.roomItems', {})[r.no];
		if (!saved) return H.defaultRoomItems(r);
		// A bed added after the drawing was saved still appears, at its default spot.
		var defs = H.defaultRoomItems(r);
		r.beds.forEach(function (b) { if (!saved.some(function (i) { return i.k === 'bed' && i.no === b.no; })) saved.push(defs.filter(function (i) { return i.no === b.no; })[0]); });
		return saved;
	};
	var ITEMTXT = function (k) { return { window: '', door: L('باب', 'Door'), bath: L('حمام', 'Bath'), ante: L('انتقالية', 'Ante'), companion: L('مرافق', 'Companion'), lounge: L('صالة', 'Lounge'), curtain: '', sink: L('مغسلة', 'Sink'), nurse: L('تمريض', 'Nurse') }[k]; };
	H.ITEMTXT = ITEMTXT;
	H.roomPlan = function (r, opts) {
		opts = opts || {};
		var items = opts.items || H.roomItems(r);
		var h = '<div class="hms-roomplan' + (opts.small ? ' small' : '') + (opts.big ? ' big' : '') + '"' + (opts.id ? ' id="' + opts.id + '"' : '') + '>';
		items.forEach(function (it, idx) {
			var st = 'inset-inline-start:' + it.x + '%;top:' + it.y + '%;width:' + it.w + '%;height:' + it.h + '%';
			var edit = opts.edit ? ' data-i="' + idx + '"' + (opts.selectedItem === idx ? ' data-sel="1"' : '') : '';
			var handle = opts.edit ? '<span class="rp-handle"></span>' : '';
			if (it.k !== 'bed') { h += '<div class="rp-' + it.k + (opts.edit ? ' rp-edit' : '') + '" style="' + st + '"' + edit + '>' + (it.k === 'bath' ? '<i class="ki-outline ki-drop fs-7"></i>' : '') + (ITEMTXT(it.k) ? '<span>' + ITEMTXT(it.k) + '</span>' : '') + handle + '</div>'; return; }
			var b = r.beds.filter(function (x) { return x.no === it.no; })[0] || { no: it.no, st: 'vacant' };
			var p = b.p ? D.patient(b.p) : null, sel = opts.selected === b.no, can = opts.pickable ? opts.pickable(b) : true;
			var click = opts.edit ? '' : opts.mode === 'pick' ? (can ? 'HMS.admitPick(\'' + r.no + '\',\'' + b.no + '\')' : '') : 'HMS.bedAction(\'' + r.ward.id + '\',\'' + b.no + '\')';
			h += '<div class="rp-bed ' + b.st + (it.h < 40 && it.w > it.h * 0.6 ? ' short' : '') + (sel ? ' sel' : '') + (can ? '' : ' na') + (opts.edit ? ' rp-edit' : '') + '" style="' + st + '"' + edit + (click ? ' onclick="event.stopPropagation();' + click + '"' : '') + ' title="' + H.esc(L('سرير ', 'Bed ') + b.no + (p ? ' — ' + T(p.name) : '')) + '">' +
				'<div class="rp-pillow"></div><div class="rp-bedno num">' + b.no + '</div>' +
				(p ? '<div class="rp-who">' + H.avatar(p, opts.small ? 20 : 26) + '</div>' : '<div class="rp-free">' + ({ vacant: L('متاح', 'Free'), cleaning: L('تنظيف', 'Cleaning'), blocked: L('مغلق', 'Blocked') }[b.st] || '') + '</div>') + handle + '</div>';
		});
		return h + '</div>';
	};
	function featChips(t) { return t.feat.map(function (k) { return '<span class="hms-chip me-1 mb-1">' + FEAT(k) + '</span>'; }).join(''); }
	function roomCard(r, opts) {
		var t = D.roomTypes[r.type], sex = roomSex(r);
		return '<div class="card h-100"><div class="card-body p-5"><div class="d-flex flex-stack mb-1"><span class="fw-bold fs-4 text-gray-900">' + L('غرفة ', 'Room ') + '<span class="num">' + r.no + '</span></span><span class="badge hms-badge-solid fs-8">' + T(t.name) + '</span></div>' +
			'<div class="d-flex flex-wrap gap-3 text-muted fs-8 fw-semibold mb-4"><span><span class="num text-gray-800 fw-bold">' + occupied(r) + ' / ' + r.beds.length + '</span> ' + L('مشغول', 'occupied') + '</span><span>' + H.money(t.rate) + ' ' + L('/ يوم', '/ day') + '</span>' + (t.gender === 'same' && sex ? '<span>' + (sex === 'M' ? L('رجال', 'Male') : L('نساء', 'Female')) + '</span>' : '') + '</div>' +
			H.roomPlan(r, opts) + '</div></div>';
	}
	H.roomCard = roomCard;

	/* ---------------- Bed board, by room ---------------- */
	var bedTxt = function (s) { return { occupied: L('مشغول', 'Occupied'), vacant: L('متاح', 'Vacant'), cleaning: L('تنظيف', 'Cleaning'), reserved: L('محجوز', 'Reserved'), blocked: L('مغلق', 'Blocked') }[s]; };
	H.route('beds', { module: 'inpatient', render: function () {
		var all = D.wards.reduce(function (a, w) { return a.concat(w.beds); }, []);
		var count = function (s) { return all.filter(function (b) { return b.st === s; }).length; };
		var html = H.stats([
			{ label: L('الغرف / الأسرّة', 'Rooms / beds'), value: '<span class="num">' + D.rooms.length + ' / ' + all.length + '</span>', sub: '<span class="num">' + D.wards.length + '</span> ' + L('أجنحة', 'wards') },
			{ label: L('مشغول', 'Occupied'), value: '<span class="num">' + count('occupied') + '</span>', sub: '<span class="num">' + Math.round(count('occupied') * 100 / all.length) + '%</span> ' + L('إشغال', 'occupancy') },
			{ label: L('متاح الآن', 'Available now'), value: '<span class="num">' + count('vacant') + '</span>', sub: '<span class="num">' + count('cleaning') + '</span> ' + L('قيد التنظيف', 'being cleaned'), cls: 'text-success' },
			{ label: L('خروج متوقع اليوم', 'Expected discharges today'), value: '<span class="num">4</span>', sub: L('قبل الساعة ١٢ ظهراً: ', 'Before noon: ') + '<span class="num">1</span>' }]);
		var legend = ['occupied', 'vacant', 'cleaning', 'reserved', 'blocked'].map(function (s) { return '<span class="d-inline-flex align-items-center gap-2 me-5 fs-7 fw-semibold text-gray-700"><span class="rp-bed ' + s + '" style="position:static;display:inline-block;width:18px;height:12px"></span>' + bedTxt(s) + '</span>'; }).join('');
		html += '<div class="d-flex flex-wrap flex-stack mb-5 gap-3"><div>' + legend + '</div>' + H.link('light-primary', 'ki-element-11', L('أنواع الغرف', 'Room types'), '#/rooms') + '</div>';
		D.wards.forEach(function (w) {
			if (w.id === 'ICU' && !H.isOn('icu')) return;
			var rooms = H.roomsOfWard(w.id);
			html += H.card({ title: T(w.name), sub: '<span class="num">' + rooms.length + '</span> ' + L('غرف', 'rooms') + ' · <span class="num">' + w.beds.filter(function (b) { return b.st === 'occupied'; }).length + ' / ' + w.beds.length + '</span> ' + L('سرير مشغول', 'beds occupied') + (WARDSEX[w.id] ? ' · ' + (WARDSEX[w.id] === 'M' ? L('رجال', 'Male') : L('نساء', 'Female')) : ''),
				body: '<div class="row g-5">' + rooms.map(function (r) { return '<div class="' + (r.beds.length >= 4 ? 'col-xl-6' : 'col-md-6 col-xl-3') + '">' + roomCard(r) + '</div>'; }).join('') + '</div>' });
		});
		return { title: L('إدارة الأسرة والغرف', 'Beds & rooms'), crumbs: [{ t: L('التنويم', 'Inpatient') }, { t: L('الأسرة', 'Beds') }], actions: H.btn('primary', 'ki-entrance-left', L('إسكان مريض', 'Accommodate a patient'), 'onclick="HMS.admit()"'), html: html };
	} });

	/* ---------------- Room types catalogue ---------------- */
	H.route('rooms', { module: 'inpatient', render: function () {
		var y = D.payers.filter(function (x) { return x.kind === 'ins'; });
		var cards = Object.keys(D.roomTypes).map(function (k) {
			var t = D.roomTypes[k], rooms = D.rooms.filter(function (r) { return r.type === k; }), beds = rooms.reduce(function (a, r) { return a + r.beds.length; }, 0), free = rooms.reduce(function (a, r) { return a + r.beds.filter(function (b) { return b.st === 'vacant'; }).length; }, 0);
			var sample = rooms[0];
			return '<div class="col-md-6 col-xl-4"><div class="card h-100"><div class="card-body p-6"><div class="d-flex flex-stack mb-2"><span class="fw-bold fs-4 text-gray-900">' + T(t.name) + '</span><span class="fs-4 fw-bold" style="color:#0E4A9E">' + H.money(t.rate) + '</span></div>' +
				'<div class="text-muted fs-7 mb-4">' + L('أسرّة في الغرفة: ', 'Beds per room: ') + '<span class="num fw-bold text-gray-800">' + t.beds + '</span> · ' + (t.gender === 'same' ? L('نفس الجنس فقط', 'Same sex only') : L('بدون قيد جنس', 'No sex restriction')) + '</div>' +
				(sample ? H.roomPlan(sample, { small: true }) : '') + '<div class="mt-4">' + featChips(t) + '</div><div class="separator my-4"></div>' +
				'<div class="d-flex flex-stack fs-7"><span class="text-muted">' + L('الغرف', 'Rooms') + ' <span class="num fw-bold text-gray-800">' + rooms.length + '</span> · ' + L('أسرّة', 'beds') + ' <span class="num fw-bold text-gray-800">' + beds + '</span></span>' + (free ? H.badge('success', '<span class="num">' + free + '</span> ' + L('متاح', 'free')) : H.badge('secondary', L('ممتلئة', 'Full'))) + '</div></div></div></div>';
		}).join('');
		var ent = H.table([{ t: L('الجهة / الفئة', 'Payer / class') }, { t: L('الغرفة المستحقة', 'Entitled room') }, { t: L('الترقية', 'Upgrade') }],
			[[L('تأمين — VIP', 'Insurance — VIP'), 'vip'], [L('تأمين — A / Gold', 'Insurance — A / Gold'), 'private'], [L('تأمين — B / Silver', 'Insurance — B / Silver'), 'semi'], [L('تأمين — C', 'Insurance — C'), 'ward4'], [L('عقد شركات', 'Corporate contract'), 'private'], [L('برنامج حكومي', 'Government programme'), 'semi'], [L('نقدي', 'Cash'), null]].map(function (r) {
				return ['<span class="fw-semibold text-gray-800">' + r[0] + '</span>', r[1] ? '<span class="badge hms-badge-solid fs-8">' + T(D.roomTypes[r[1]].name) + '</span>' : '<span class="text-gray-600 fs-7">' + L('أي غرفة بسعرها', 'Any room at its price') + '</span>', r[1] && r[1] !== 'vip' ? '<span class="text-gray-600 fs-7">' + L('يدفع المريض فرق السعر اليومي', 'The patient pays the daily difference') + '</span>' : '<span class="hms-zero">—</span>'];
			}));
		var html = H.notice({ icon: 'ki-element-11', title: L('النوع يحدد السعر والاستحقاق والمسموح', 'The type decides price, entitlement and who may share'), text: L('كل غرفة لها نوع من هذه القائمة، ومنه يأتي سعر الإقامة اليومي في الفاتورة، والغرفة التي يغطيها تأمين المريض، ومنع خلط الجنسين في الغرف المشتركة.', 'Every room has a type from this list; it drives the daily stay price on the bill, the room the patient\'s insurance covers, and the same-sex rule for shared rooms.'), badge: '<span class="num">' + Object.keys(D.roomTypes).length + '</span> ' + L('أنواع', 'types') });
		html += '<div class="row g-5 g-xl-8 mb-5">' + cards + '</div>';
		html += H.card({ title: L('استحقاق الغرفة حسب الجهة الدافعة', 'Room entitlement by payer'), sub: L('يُضبط في عقد كل جهة', 'Set in each payer contract'), body: ent });
		return { title: L('أنواع الغرف', 'Room types'), crumbs: [{ t: L('التنويم', 'Inpatient'), href: '#/beds' }, { t: L('أنواع الغرف', 'Room types') }], actions: H.btn('primary', 'ki-plus', L('نوع جديد', 'New type')), html: html };
	} });

	/* ---------------- Accommodation ---------------- */
	var adm = { p: 'MRN-100371', room: null, bed: null };
	function canTake(p, r, b) {
		if (b.st !== 'vacant') return false;
		var t = D.roomTypes[r.type], sex = roomSex(r);
		if (t.gender === 'same' && sex && sex !== p.sex) return false;
		if (WARDSEX[r.ward.id] && WARDSEX[r.ward.id] !== p.sex) return false;
		return true;
	}
	function admitBody() {
		var p = D.patient(adm.p), ent = H.entitlement(p), entRate = ent ? D.roomTypes[ent].rate : 0;
		var options = D.rooms.filter(function (r) { return r.type !== 'icu'; }).map(function (r) { return { r: r, free: r.beds.filter(function (b) { return canTake(p, r, b); }) }; }).filter(function (o) { return o.free.length; });
		if (!adm.room || !options.some(function (o) { return o.r.no === adm.room; })) { var first = options.filter(function (o) { return o.r.type === ent; })[0] || options[0]; adm.room = first ? first.r.no : null; adm.bed = first ? first.free[0].no : null; }
		var r = H.roomByNo(adm.room), t = r ? D.roomTypes[r.type] : null, diff = t && ent ? Math.max(0, t.rate - entRate) : 0;
		var list = options.map(function (o) {
			var ot = D.roomTypes[o.r.type], d = ent ? ot.rate - entRate : null, active = o.r.no === adm.room;
			return '<div class="d-flex align-items-center cb-row py-3 px-3 cursor-pointer rounded ' + (active ? 'bg-light-primary' : '') + '" onclick="HMS.admitPick(\'' + o.r.no + '\',\'' + o.free[0].no + '\')">' +
				'<div class="flex-grow-1"><div class="fw-bold text-gray-900">' + L('غرفة ', 'Room ') + '<span class="num">' + o.r.no + '</span> · ' + T(ot.name) + '</div><div class="text-muted fs-8">' + T(o.r.ward.name) + ' · <span class="num">' + o.free.length + '</span> ' + L('سرير متاح', 'bed(s) free') + '</div></div>' +
				'<div class="text-end"><div class="fw-bold fs-7">' + H.money(ot.rate) + '</div>' + (d === null ? '' : d === 0 ? H.badge('success', L('مغطاة', 'Covered')) : d > 0 ? H.badge('primary', '<span class="num">+' + H.fmtNum(d) + '</span>') : H.badge('secondary', L('أقل من الاستحقاق', 'Below entitlement'))) + '</div></div>';
		}).join('');
		return '<div class="d-flex flex-wrap align-items-center gap-4 mb-5"><div class="flex-grow-1">' + H.select(D.patients.slice(0, 12).map(function (x) { return { v: x.mrn, t: T(x.name) + ' — ' + x.mrn }; }), adm.p, 'onchange="HMS.admitPatient(this.value)"') + '</div></div>' +
			'<div class="d-flex align-items-center mb-5">' + H.personCell(p, H.ageSex(p) + ' · ' + H.payerLabel(p), null, 45) + '</div>' +
			H.notice({ icon: 'ki-shield-tick', title: ent ? L('الغرفة المستحقة: ', 'Entitled room: ') + T(D.roomTypes[ent].name) : L('مريض نقدي', 'Cash patient'), text: ent ? L('الأقل من الاستحقاق مقبول، والأعلى يدفع المريض فرقه يومياً بعد توقيعه على الإقرار.', 'Anything at or below entitlement is covered; above it the patient pays the daily difference after signing the acknowledgement.') : L('يدفع سعر الغرفة التي يختارها.', 'Pays the price of the room chosen.'), badge: ent ? H.money(entRate) + ' ' + L('/ يوم', '/ day') : null, cls: 'mb-5' }) +
			'<div class="row g-5"><div class="col-lg-5"><div class="fw-bold text-gray-800 mb-2">' + L('الغرف المتاحة المناسبة', 'Suitable rooms with a free bed') + '</div><div class="text-muted fs-8 mb-3">' + L('مستبعد تلقائياً: الغرف المشتركة بجنس مختلف والأجنحة المخصصة للجنس الآخر', 'Excluded automatically: shared rooms of the other sex and wards for the other sex') + '</div><div style="max-height:360px;overflow:auto">' + list + '</div></div>' +
			'<div class="col-lg-7">' + (r ? '<div class="d-flex flex-stack mb-3"><span class="fw-bold fs-5">' + L('غرفة ', 'Room ') + '<span class="num">' + r.no + '</span> — ' + T(t.name) + '</span><span class="text-muted fs-7">' + L('اختر السرير من المخطط', 'Pick the bed on the plan') + '</span></div>' +
				H.roomPlan(r, { mode: 'pick', selected: adm.bed, pickable: function (b) { return canTake(p, r, b); } }) +
				'<div class="mt-4">' + featChips(t) + '</div>' +
				'<div class="row g-4 mt-2">' + [[L('السرير', 'Bed'), '<span class="num">' + (adm.bed || '—') + '</span>'], [L('السعر اليومي', 'Daily rate'), H.money(t.rate)], [L('فرق على المريض', 'Patient pays extra'), diff ? '<span class="text-danger">' + H.money(diff) + ' ' + L('/ يوم', '/ day') + '</span>' : '<span class="text-success">' + L('لا يوجد', 'None') + '</span>']].map(function (k) { return '<div class="col-4"><div class="text-muted fs-8 fw-semibold">' + k[0] + '</div><div class="fw-bold fs-6">' + k[1] + '</div></div>'; }).join('') + '</div>' +
				(diff ? '<label class="form-check form-check-custom form-check-solid mt-5"><input class="form-check-input" type="checkbox" /><span class="form-check-label text-gray-700">' + L('وقّع المريض على إقرار دفع فرق الترقية', 'The patient signed the upgrade-difference acknowledgement') + '</span></label>' : '') : H.notice({ kind: 'danger', icon: 'ki-information-5', title: L('لا يوجد سرير مناسب', 'No suitable bed'), text: L('كل الغرف المناسبة ممتلئة؛ يُضاف المريض لقائمة انتظار الإسكان.', 'Every suitable room is full; the patient joins the accommodation waiting list.') })) + '</div></div>';
	}
	function admitFooter() { return '<button class="btn btn-light" data-bs-dismiss="modal">' + L('إلغاء', 'Cancel') + '</button><button class="btn btn-primary" data-bs-dismiss="modal" onclick="HMS.toast(HMS.L(\'تم إسكان المريض — سرير \',\'Patient accommodated — bed \')+\'' + (adm.bed || '') + '\')">' + L('تأكيد الإسكان', 'Confirm accommodation') + '</button>'; }
	H.admit = function () { H.modal(L('إسكان مريض', 'Accommodate a patient'), admitBody(), admitFooter(), 'mw-1000px'); };
	H.admitPick = function (room, bed) { adm.room = room; adm.bed = bed; document.getElementById('hms_modal_body').innerHTML = admitBody(); document.getElementById('hms_modal_footer').innerHTML = admitFooter(); };
	H.admitPatient = function (mrn) { adm.p = mrn; adm.room = null; adm.bed = null; document.getElementById('hms_modal_body').innerHTML = admitBody(); document.getElementById('hms_modal_footer').innerHTML = admitFooter(); };

	/* ---------------- Bed details now name the room ---------------- */
	var oldBedAction = H.bedAction;
	H.bedAction = function (wid, no) {
		var r = H.roomOfBed(no);
		if (!r) return oldBedAction(wid, no);
		var b = r.beds.filter(function (x) { return x.no === no; })[0], t = D.roomTypes[r.type];
		var info = '<div class="d-flex flex-wrap gap-2 mb-5"><span class="hms-chip">' + T(r.ward.name) + '</span><span class="hms-chip">' + L('غرفة ', 'Room ') + '<span class="num">' + r.no + '</span> · ' + T(t.name) + '</span><span class="hms-chip">' + H.money(t.rate) + ' ' + L('/ يوم', '/ day') + '</span></div>' + H.roomPlan(r, { selected: no }) + '<div class="mt-4 mb-5">' + featChips(t) + '</div>';
		if (!b.p) {
			H.modal(L('سرير ', 'Bed ') + no + ' — ' + bedTxt(b.st), info, b.st === 'vacant' ? '<button class="btn btn-light" data-bs-dismiss="modal">' + L('إغلاق', 'Close') + '</button><button class="btn btn-primary" onclick="HMS.admitPick && (HMS.admit(), HMS.admitPick(\'' + r.no + '\',\'' + no + '\'))">' + L('إسكان مريض هنا', 'Accommodate a patient here') + '</button>' : null, 'mw-750px');
			return;
		}
		var p = D.patient(b.p), d = b.doc ? D.doc(b.doc) : null, ent = H.entitlement(p), diff = ent ? Math.max(0, t.rate - D.roomTypes[ent].rate) : 0;
		info += '<div class="d-flex align-items-center mb-5">' + H.personCell(p, p.mrn + ' · ' + H.ageSex(p), '#/patient/' + p.mrn, 55) + '</div><div class="row g-5 mb-5">' +
			[[L('التشخيص', 'Diagnosis'), b.dx ? T(b.dx) : '—'], [L('يوم الإقامة', 'Length of stay'), b.los ? '<span class="num">' + b.los + '</span>' : '—'], [L('الجهة الدافعة', 'Payer'), H.payerLabel(p)], [L('الغرفة المستحقة', 'Entitled room'), ent ? T(D.roomTypes[ent].name) : L('نقدي', 'Cash')], [L('فرق الترقية اليومي', 'Daily upgrade difference'), diff ? '<span class="text-danger">' + H.money(diff) + '</span>' : L('لا يوجد', 'None')], [L('الحساسية', 'Allergies'), p.allergies.length ? '<span class="text-danger">' + p.allergies.map(T).join(', ') + '</span>' : L('لا توجد', 'None')]].map(function (f) { return '<div class="col-md-4"><div class="text-muted fs-8 fw-semibold">' + f[0] + '</div><div class="fw-bold text-gray-900 fs-7">' + f[1] + '</div></div>'; }).join('') + '</div>' +
			(d ? '<div class="text-muted fs-8 fw-semibold mb-2">' + L('الطبيب المعالج', 'Attending') + '</div>' + H.personCell(d, T(d.title)) : '');
		H.modal(L('سرير ', 'Bed ') + no, info, '<button class="btn btn-light" data-bs-dismiss="modal">' + L('إغلاق', 'Close') + '</button><button class="btn btn-light-primary" data-bs-dismiss="modal" onclick="HMS.toast(HMS.L(\'أُرسل طلب التحويل\',\'Transfer request sent\'),\'info\')"><i class="ki-outline ki-arrows-loop fs-4"></i>' + L('تحويل', 'Transfer') + '</button><button class="btn btn-primary" data-bs-dismiss="modal" onclick="HMS.toast(HMS.L(\'بدأ إجراء الخروج — أُبلغت الفوترة والصيدلية\',\'Discharge started — billing and pharmacy notified\'))"><i class="ki-outline ki-exit-right fs-4"></i>' + L('خروج', 'Discharge') + '</button>', 'mw-750px');
	};
})();
