/* Floor & room designer: the admin draws the building the way the rest of the system is configured —
   no developer needed. Floor mode moves and sizes spaces on a 12-column floor; room mode draws the
   interior (beds, bathroom, door, window, companion bed...). Every other screen reads the drawing. */
(function () {
	'use strict';
	var H = HMS, L = H.L, T = H.T, D = H.data;
	var COLOR = { bed: '#0E4A9E', er: '#F1416C', rad: '#7239EA', lab: '#17C653', pharm: '#1B84FF', clinic: '#0E4A9E', or: '#0E4A9E', icu: '#D9214E', admin: '#99A1B7' };
	var st = { mode: 'floor', floor: '2', room: '302', sel: null, dirty: false };
	var items = null; // room interior being edited
	// Beds added or removed in the drawing; they reach the room only when the drawing is saved.
	var pending = { added: [], removed: [] };

	function save(key, mutate) { var v = H.store.get(key, {}); mutate(v); H.store.set(key, v); }
	function floorList() { return H.floors(); }
	function curFloor() { return floorList().filter(function (f) { return f.k === st.floor; })[0] || floorList()[0]; }

	/* ---------------- Floor mode ---------------- */
	function floorCanvas() {
		var f = curFloor();
		var tiles = f.rooms.map(function (r) {
			var room = r.roomNo ? H.roomByNo(r.roomNo) : null;
			var sub = room ? T(D.roomTypes[room.type].name) + ' · <span class="num">' + room.beds.length + '</span> ' + L('سرير', 'bed(s)') : '';
			return '<div class="fd-tile' + (st.sel === r.id ? ' sel' : '') + '" data-id="' + r.id + '" style="grid-column:' + r.c + ' / span ' + r.cs + ';grid-row:' + r.r + ' / span ' + r.rs + ';border-color:' + COLOR[r.kind] + '">' +
				'<div class="fw-bold fs-7 text-gray-900 text-truncate">' + T(r.label) + '</div><div class="fs-9 text-muted text-truncate">' + sub + '</div>' +
				(room ? '<button class="btn btn-icon btn-sm btn-light-primary fd-open" title="' + L('رسم الغرفة', 'Draw the room') + '" onclick="event.stopPropagation();HMS.dsOpenRoom(\'' + room.no + '\')"><i class="ki-outline ki-pencil fs-6"></i></button>' : '') +
				'<span class="fd-handle"></span></div>';
		}).join('');
		return '<div class="d-flex flex-wrap gap-2 mb-4">' + floorList().map(function (x) { return '<button class="btn btn-sm ' + (x.k === f.k ? 'btn-primary' : 'btn-light') + '" onclick="HMS.dsFloor(\'' + x.k + '\')">' + T(x.t) + '</button>'; }).join('') + '</div>' +
			'<div class="text-muted fs-7 mb-3"><i class="ki-outline ki-information-5 fs-6 me-1"></i>' + L('اسحب أي مساحة لتحريكها، واسحب الركن لتغيير حجمها، واضغط القلم لرسم الغرفة من الداخل.', 'Drag a space to move it, drag its corner to resize it, and press the pencil to draw the room inside.') + '</div>' +
			'<div class="overflow-auto"><div class="fd-grid" id="fdGrid">' + tiles + '</div></div>';
	}
	function wireFloor() {
		var grid = document.getElementById('fdGrid'); if (!grid) return;
		grid.addEventListener('pointerdown', function (e) {
			var tile = e.target.closest('.fd-tile'); if (!tile || e.target.closest('.fd-open')) return;
			e.preventDefault();
			var id = tile.getAttribute('data-id'), f = curFloor(), r = f.rooms.filter(function (x) { return x.id === id; })[0];
			var resize = e.target.classList.contains('fd-handle');
			var colW = grid.clientWidth / 12, rowH = 92 + 9.6, sx = e.clientX, sy = e.clientY, o = { c: r.c, r: r.r, cs: r.cs, rs: r.rs };
			st.sel = id; [].slice.call(grid.querySelectorAll('.fd-tile')).forEach(function (t) { t.classList.toggle('sel', t === tile); });
			function move(ev) {
				var dx = (ev.clientX - sx) * (H.isAr ? -1 : 1), dc = Math.round(dx / colW), dr = Math.round((ev.clientY - sy) / rowH);
				if (resize) { r.cs = Math.max(1, Math.min(13 - o.c, o.cs + dc)); r.rs = Math.max(1, o.rs + dr); }
				else { r.c = Math.max(1, Math.min(13 - o.cs, o.c + dc)); r.r = Math.max(1, o.r + dr); }
				tile.style.gridColumn = r.c + ' / span ' + r.cs; tile.style.gridRow = r.r + ' / span ' + r.rs;
			}
			function up() {
				document.removeEventListener('pointermove', move); document.removeEventListener('pointerup', up);
				if (r.c !== o.c || r.r !== o.r || r.cs !== o.cs || r.rs !== o.rs) { save('hms.floorPos', function (v) { v[id] = { c: r.c, r: r.r, cs: r.cs, rs: r.rs }; }); H.toast(L('حُفظ موضع المساحة', 'Space position saved'), 'info'); }
			}
			document.addEventListener('pointermove', move); document.addEventListener('pointerup', up);
		});
	}

	/* ---------------- Room mode ---------------- */
	function room() { return H.roomByNo(st.room) || D.rooms[0]; }
	function roomCanvas() {
		var r = room(), t = D.roomTypes[r.type];
		if (!items) items = JSON.parse(JSON.stringify(H.roomItems(r)));
		var sel = st.sel != null ? items[st.sel] : null;
		var tools = [['bed', 'ki-home-3', L('سرير', 'Bed')], ['bath', 'ki-drop', L('حمام', 'Bathroom')], ['door', 'ki-entrance-left', L('باب', 'Door')], ['window', 'ki-sun', L('شباك', 'Window')], ['companion', 'ki-people', L('سرير مرافق', 'Companion bed')], ['lounge', 'ki-coffee', L('صالة', 'Lounge')], ['curtain', 'ki-abstract-14', L('ستارة فصل', 'Curtain')], ['ante', 'ki-shield-tick', L('غرفة انتقالية', 'Anteroom')], ['sink', 'ki-drop', L('مغسلة', 'Sink')]];
		var roomsOpts = D.rooms.map(function (x) { return { v: x.no, t: L('غرفة ', 'Room ') + x.no + ' — ' + T(x.ward.name) }; });
		var left = '<div class="mb-4">' + H.select(roomsOpts, r.no, 'onchange="HMS.dsOpenRoom(this.value)"') + '</div>' +
			'<div class="row g-3 mb-5">' + H.field(L('نوع الغرفة', 'Room type'), H.select(Object.keys(D.roomTypes).map(function (k) { return { v: k, t: T(D.roomTypes[k].name) }; }), r.type, 'onchange="HMS.dsType(this.value)"'), 'col-12') + '</div>' +
			'<div class="fw-bold text-gray-800 mb-3">' + L('أضف للغرفة', 'Add to the room') + '</div><div class="d-flex flex-wrap gap-2 mb-5">' + tools.map(function (x) { return '<button class="btn btn-sm btn-light-primary" onclick="HMS.dsAdd(\'' + x[0] + '\')"><i class="ki-outline ' + x[1] + ' fs-5"></i>' + x[2] + '</button>'; }).join('') + '</div>' +
			(sel ? '<div class="rounded p-4 mb-4" style="background:#F7FAFF;border:1px dashed #0E4A9E"><div class="fw-bold mb-3">' + L('العنصر المحدد: ', 'Selected: ') + (sel.k === 'bed' ? L('سرير ', 'Bed ') + '<span class="num">' + sel.no + '</span>' : (H.ITEMTXT(sel.k) || sel.k)) + '</div><div class="row g-2 mb-3">' +
				[['x', L('أفقي %', 'X %')], ['y', L('رأسي %', 'Y %')], ['w', L('عرض %', 'W %')], ['h', L('ارتفاع %', 'H %')]].map(function (p) { return '<div class="col-3"><label class="fs-9 text-muted d-block">' + p[1] + '</label><input class="form-control form-control-sm form-control-solid num" value="' + Math.round(sel[p[0]] * 10) / 10 + '" onchange="HMS.dsProp(\'' + p[0] + '\', this.value)" /></div>'; }).join('') + '</div>' +
				'<button class="btn btn-sm btn-light-danger w-100" onclick="HMS.dsDelete()"' + (sel.k === 'bed' && r.beds.some(function (b) { return b.no === sel.no && b.p; }) ? ' disabled title="' + L('السرير مشغول بمريض', 'The bed holds a patient') + '"' : '') + '><i class="ki-outline ki-trash fs-5"></i>' + L('حذف العنصر', 'Delete item') + '</button></div>' : '<div class="text-muted fs-7 mb-4">' + L('اضغط أي عنصر في المخطط لتحديده.', 'Click any item on the plan to select it.') + '</div>') +
			'<div class="d-flex gap-2"><button class="btn btn-sm btn-light flex-fill" onclick="HMS.dsReset()">' + L('الرسم الافتراضي', 'Default drawing') + '</button><button class="btn btn-sm btn-primary flex-fill" onclick="HMS.dsSave()"><i class="ki-outline ki-check fs-5"></i>' + L('حفظ الرسم', 'Save drawing') + '</button></div>';
		var right = '<div class="d-flex flex-stack mb-3"><span class="fw-bold fs-5">' + L('غرفة ', 'Room ') + '<span class="num">' + r.no + '</span> — ' + T(t.name) + '</span>' + (st.dirty ? H.badge('primary', L('تعديلات غير محفوظة', 'Unsaved changes')) : H.badge('success', L('محفوظ', 'Saved'))) + '</div>' +
			H.roomPlan(r, { items: items, edit: true, big: true, id: 'rdPlan', selectedItem: st.sel }) +
			'<div class="text-muted fs-7 mt-3">' + L('اسحب العنصر لتحريكه، واسحب الركن الصغير لتغيير حجمه.', 'Drag an item to move it; drag its small corner to resize it.') + '</div>';
		return '<div class="row g-5"><div class="col-xl-4">' + left + '</div><div class="col-xl-8">' + right + '</div></div>';
	}
	function wireRoom() {
		var plan = document.getElementById('rdPlan'); if (!plan) return;
		plan.addEventListener('pointerdown', function (e) {
			var el = e.target.closest('[data-i]'); if (!el) { st.sel = null; redraw(); return; }
			e.preventDefault();
			var i = +el.getAttribute('data-i'), it = items[i], resize = e.target.classList.contains('rp-handle');
			var W = plan.clientWidth, Hh = plan.clientHeight, sx = e.clientX, sy = e.clientY, o = { x: it.x, y: it.y, w: it.w, h: it.h };
			if (st.sel !== i) { st.sel = i; [].slice.call(plan.querySelectorAll('[data-i]')).forEach(function (x) { x.removeAttribute('data-sel'); }); el.setAttribute('data-sel', '1'); }
			var moved = false;
			function move(ev) {
				var dx = (ev.clientX - sx) * (H.isAr ? -1 : 1) * 100 / W, dy = (ev.clientY - sy) * 100 / Hh;
				if (Math.abs(dx) + Math.abs(dy) > 0.3) moved = true;
				var snap = function (v) { return Math.round(v * 2) / 2; };
				if (resize) { it.w = Math.max(0.6, Math.min(100 - it.x, snap(o.w + dx))); it.h = Math.max(1, Math.min(103 - it.y, snap(o.h + dy))); }
				else { it.x = Math.max(-2, Math.min(100 - it.w + 2, snap(o.x + dx))); it.y = Math.max(-2, Math.min(100 - it.h + 3, snap(o.y + dy))); }
				el.style.insetInlineStart = it.x + '%'; el.style.top = it.y + '%'; el.style.width = it.w + '%'; el.style.height = it.h + '%';
			}
			function up() { document.removeEventListener('pointermove', move); document.removeEventListener('pointerup', up); if (moved) st.dirty = true; redraw(); }
			document.addEventListener('pointermove', move); document.addEventListener('pointerup', up);
		});
	}
	function redraw() { var host = document.getElementById('dsBody'); if (!host) return; host.innerHTML = st.mode === 'floor' ? floorCanvas() : roomCanvas(); if (st.mode === 'floor') wireFloor(); else wireRoom(); syncTabs(); }
	function syncTabs() { [].slice.call(document.querySelectorAll('[data-ds-mode]')).forEach(function (b) { var on = b.getAttribute('data-ds-mode') === st.mode; b.classList.toggle('btn-primary', on); b.classList.toggle('btn-light', !on); }); }

	H.dsMode = function (m) { st.mode = m; st.sel = null; redraw(); };
	H.dsFloor = function (k) { st.floor = k; st.sel = null; redraw(); };
	H.dsOpenRoom = function (no) { if (st.dirty && !confirm(L('توجد تعديلات غير محفوظة، هل تريد تركها؟', 'Discard unsaved changes?'))) return; discardPending(); st.room = no; st.mode = 'room'; st.sel = null; st.dirty = false; items = null; redraw(); };
	// Leaving a drawing unsaved puts its beds back the way they were.
	function discardPending() { var r = room(); if (!r) return; r.beds = r.beds.filter(function (b) { return pending.added.indexOf(b.no) === -1; }); r.ward.beds = r.ward.beds.filter(function (b) { return pending.added.indexOf(b.no) === -1; }); pending = { added: [], removed: [] }; }
	H.dsAdd = function (k) {
		var r = room(), sizes = { bed: [26, 46], bath: [22, 34], door: [16, 3], window: [40, 3], companion: [28, 16], lounge: [24, 34], curtain: [0.6, 60], ante: [22, 34], sink: [10, 10] }[k];
		var it = { k: k, x: 37, y: k === 'window' ? -1.5 : k === 'door' ? 98 : 30, w: sizes[0], h: sizes[1] };
		if (k === 'bed') {
			var n = r.beds.length + 1, no = r.no + '-' + n; while (r.beds.some(function (b) { return b.no === no; })) { n++; no = r.no + '-' + n; }
			var b = { no: no, st: 'vacant' }; r.beds.push(b); r.ward.beds.push(b); it.no = no; pending.added.push(no);
		}
		items.push(it); st.sel = items.length - 1; st.dirty = true; redraw();
	};
	H.dsDelete = function () {
		var it = items[st.sel]; if (!it) return; var r = room();
		if (it.k === 'bed') {
			if (r.beds.some(function (b) { return b.no === it.no && b.p; })) return;
			r.beds = r.beds.filter(function (b) { return b.no !== it.no; }); r.ward.beds = r.ward.beds.filter(function (b) { return b.no !== it.no; });
			pending.removed.push(it.no);
		}
		items.splice(st.sel, 1); st.sel = null; st.dirty = true; redraw();
	};
	H.dsProp = function (p, v) { var it = items[st.sel]; if (!it || isNaN(+v)) return; it[p] = +v; st.dirty = true; redraw(); };
	H.dsType = function (k) { var r = room(); r.type = k; save('hms.roomTypes', function (v) { v[r.no] = k; }); H.toast(L('تغيّر نوع الغرفة وسعرها اليومي', 'Room type and daily rate changed'), 'info'); redraw(); };
	H.dsReset = function () { var r = room(); items = JSON.parse(JSON.stringify(H.defaultRoomItems(r))); st.sel = null; st.dirty = true; redraw(); };
	// Two beds on top of each other, or a bed over the bathroom, is a drawing mistake, not a layout.
	function clash() {
		var solid = items.filter(function (i) { return ['bed', 'bath', 'ante', 'lounge', 'companion'].indexOf(i.k) !== -1; });
		for (var a = 0; a < solid.length; a++) for (var b = a + 1; b < solid.length; b++) {
			var p = solid[a], q = solid[b];
			if ((p.k === 'bed' || q.k === 'bed') && p.x < q.x + q.w && q.x < p.x + p.w && p.y < q.y + q.h && q.y < p.y + p.h) return [p, q];
		}
		return null;
	}
	H.dsSave = function () {
		var c = clash();
		if (c) { H.toast(L('لا يمكن الحفظ: عنصران متداخلان (', 'Cannot save: two items overlap (') + [c[0], c[1]].map(function (i) { return i.k === 'bed' ? L('سرير ', 'bed ') + i.no : H.ITEMTXT(i.k); }).join(L(' و ', ' and ')) + ')', 'error'); return; }
		var r = room(); save('hms.roomItems', function (v) { v[r.no] = items; });
		save('hms.extraBeds', function (v) { v[r.no] = (v[r.no] || []).filter(function (n) { return pending.removed.indexOf(n) === -1; }).concat(pending.added); });
		save('hms.removedBeds', function (v) { pending.removed.forEach(function (n) { v[n] = 1; }); });
		pending = { added: [], removed: [] }; st.dirty = false; H.toast(L('حُفظ رسم الغرفة — يظهر الآن في الأسرة والخريطة والإسكان', 'Room drawing saved — now shown on beds, the map and accommodation')); redraw(); };
	H.dsNewRoom = function () {
		var wards = D.wards.map(function (w) { return { v: w.id, t: T(w.name) }; });
		H.modal(L('إضافة غرفة', 'Add a room'), '<div class="row g-5">' + H.field(L('رقم الغرفة', 'Room number'), H.input('', 'id="nrNo" class="num" placeholder="311"'), 'col-md-4') + H.field(L('الجناح', 'Ward'), H.select(wards, 'W3', 'id="nrWard"'), 'col-md-8') +
			H.field(L('نوع الغرفة', 'Room type'), H.select(Object.keys(D.roomTypes).map(function (k) { return { v: k, t: T(D.roomTypes[k].name) + ' — ' + D.roomTypes[k].beds + ' ' + L('سرير', 'bed(s)') }; }), 'semi', 'id="nrType"'), 'col-12') + '</div>' +
			'<div class="text-muted fs-7 mt-4">' + L('تُنشأ الأسرّة حسب النوع، وتظهر الغرفة على الدور لتضعها في مكانها ثم ترسمها من الداخل.', 'Beds are created from the type; the room appears on its floor for you to place, then draw inside.') + '</div>',
			'<button class="btn btn-light" data-bs-dismiss="modal">' + L('إلغاء', 'Cancel') + '</button><button class="btn btn-primary" onclick="HMS.dsCreateRoom()">' + L('إنشاء', 'Create') + '</button>');
	};
	H.dsCreateRoom = function () {
		var no = (document.getElementById('nrNo').value || '').trim(), ward = document.getElementById('nrWard').value, type = document.getElementById('nrType').value;
		if (!no) { H.toast(L('أدخل رقم الغرفة', 'Enter a room number'), 'error'); return; }
		if (D.rooms.some(function (r) { return r.no === no; })) { H.toast(L('الرقم مستخدم لغرفة أخرى', 'That number belongs to another room'), 'error'); return; }
		var list = H.store.get('hms.customRooms', []); list.push({ ward: ward, no: no, type: type, beds: D.roomTypes[type].beds }); H.store.set('hms.customRooms', list);
		var w = D.wards.filter(function (x) { return x.id === ward; })[0], beds = [];
		for (var i = 1; i <= D.roomTypes[type].beds; i++) { var b = { no: no + '-' + i, st: 'vacant' }; w.beds.push(b); beds.push(b); }
		D.rooms.push({ no: no, ward: w, type: type, beds: beds, custom: true });
		bootstrap.Modal.getInstance(document.getElementById('hms_modal')).hide();
		st.floor = { W3: '2', W2: '2', ICU: '3', W5: '4' }[ward] || st.floor; st.mode = 'floor'; st.sel = 'R' + no; redraw();
		H.toast(L('أُضيفت الغرفة ', 'Room added: ') + no);
	};
	H.dsResetAll = function () { ['hms.floorPos', 'hms.roomItems', 'hms.customRooms', 'hms.roomTypes', 'hms.extraBeds', 'hms.removedBeds'].forEach(function (k) { try { localStorage.removeItem(k); } catch (e) { } }); location.reload(); };

	H.route('designer', { module: 'inpatient', render: function () {
		var html = H.notice({ icon: 'ki-design-frame', title: L('الأدمن يرسم المبنى بنفسه', 'The admin draws the building'), text: L('زي باقي إعدادات النظام: الأدوار والغرف ومكانها وحجمها ومحتواها تُرسم هنا، وكل الشاشات (الأسرة، الخريطة، الإسكان) تقرأ نفس الرسم.', 'Like every other setting in the system: floors, rooms, their position, size and contents are drawn here, and every screen (beds, map, accommodation) reads the same drawing.'), badge: L('بدون مبرمج', 'No developer') });
		html += H.card({ toolbar: '', body: '<div class="d-flex flex-wrap flex-stack gap-3 mb-6"><div class="d-flex gap-2"><button class="btn btn-sm" data-ds-mode="floor" onclick="HMS.dsMode(\'floor\')"><i class="ki-outline ki-element-11 fs-5"></i>' + L('مخطط الدور', 'Floor plan') + '</button><button class="btn btn-sm" data-ds-mode="room" onclick="HMS.dsMode(\'room\')"><i class="ki-outline ki-home-3 fs-5"></i>' + L('رسم الغرفة', 'Room drawing') + '</button></div>' +
			'<div class="d-flex gap-2"><button class="btn btn-sm btn-light-primary" onclick="HMS.dsNewRoom()"><i class="ki-outline ki-plus fs-5"></i>' + L('إضافة غرفة', 'Add room') + '</button><button class="btn btn-sm btn-light" onclick="HMS.dsResetAll()">' + L('استعادة المبنى الافتراضي', 'Restore default building') + '</button></div></div><div id="dsBody"></div>' });
		return { title: L('مصمم الأدوار والغرف', 'Floor & room designer'), crumbs: [{ t: L('الإعدادات', 'Settings') }, { t: L('مصمم المبنى', 'Building designer') }], actions: H.link('light-primary', 'ki-map', L('معاينة على الخريطة', 'Preview on the map'), '#/map'), html: html, after: function () { items = null; st.dirty = false; redraw(); } };
	} });
})();
