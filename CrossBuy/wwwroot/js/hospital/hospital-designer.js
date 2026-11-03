/* Hospital floor & room designer. Floor mode moves and sizes spaces on a 12-column floor (saved on drop);
   room mode draws the interior and saves it with the room's beds. The server re-checks every rule — no
   overlap, nothing off the grid, no occupied bed removed — so the checks here are for a fast answer only. */
(function () {
	'use strict';
	var cfg = window.HospitalDesigner, W = cfg.words, esc = HospitalPlan.esc;
	var token = document.querySelector('input[name="__RequestVerificationToken"]').value;
	var body = document.getElementById('designerBody');
	var data = { types: [], floors: [] };
	var st = { mode: 'floor', floorId: null, roomId: null, sel: null, dirty: false };
	var items = null;
	function w(k) { return W[k] || k; }
	function name(o) { return cfg.isAr ? o.name : (o.nameEn || o.name); }
	function toast(kind, msg) { if (window.toastr) toastr[kind](msg); }

	function request(url, payload, form) {
		var opts = { method: 'POST', headers: { 'X-Requested-With': 'XMLHttpRequest', 'RequestVerificationToken': token } };
		if (form) { payload.append('__RequestVerificationToken', token); opts.body = payload; }
		else { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(payload); }
		return fetch(url, opts).then(function (r) { return r.json().catch(function () { return { ok: false }; }); });
	}
	function load() {
		return fetch(cfg.urls.building, { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
			.then(function (r) { return r.json(); })
			.then(function (res) {
				if (!res.ok) return;
				data = res;
				if (!st.floorId || !floor()) st.floorId = data.floors.length ? data.floors[0].id : null;
				fillSelects(); draw();
			});
	}
	function floor() { return data.floors.filter(function (f) { return f.id === st.floorId; })[0]; }
	function allRooms() { var a = []; data.floors.forEach(function (f) { a = a.concat(f.rooms.map(function (r) { r._floor = f; return r; })); }); return a; }
	function room() { return allRooms().filter(function (r) { return r.id === st.roomId; })[0]; }
	function typeOf(r) { return data.types.filter(function (t) { return t.id === r.typeId; })[0]; }

	// ---------------- floor mode ----------------
	function floorView() {
		if (!data.floors.length) return '<div class="text-center text-muted py-15">' + esc(w('No floors yet. Add the first floor to start drawing.')) + '</div>';
		var f = floor();
		var nav = '<div class="d-flex flex-wrap gap-2 mb-4">' + data.floors.map(function (x) { return '<button class="btn btn-sm ' + (x.id === f.id ? 'btn-primary' : 'btn-light') + '" data-floor="' + x.id + '">' + esc(name(x)) + '</button>'; }).join('') + '</div>';
		var tiles = f.rooms.map(function (r) {
			var t = typeOf(r);
			var sub = t ? esc(name(t)) + ' · <span class="hp-num">' + r.beds.length + '</span> ' + esc(w('bed(s)')) : esc(w('kind:' + r.kind));
			return '<div class="hp-tile hp-k-' + r.kind + (st.sel === r.id ? ' sel' : '') + '" data-room="' + r.id + '" style="grid-column:' + r.col + ' / span ' + r.colSpan + ';grid-row:' + r.row + ' / span ' + r.rowSpan + '">' +
				'<div class="fw-bold fs-7 text-gray-900 text-truncate">' + esc(w('Room')) + ' <span class="hp-num">' + esc(r.number) + '</span>' + (r.name ? ' · ' + esc(cfg.isAr ? r.name : (r.nameEn || r.name)) : '') + '</div><div class="fs-9 text-muted text-truncate">' + sub + '</div>' +
				'<button class="btn btn-icon btn-sm btn-light-primary hp-open w-25px h-25px" title="' + esc(w('Draw the room')) + '" data-draw="' + r.id + '"><i class="ki-outline ki-pencil fs-6"></i></button>' +
				'<span class="hp-handle"></span></div>';
		}).join('');
		return nav + '<div class="text-muted fs-7 mb-3"><i class="ki-outline ki-information-5 fs-6 me-1"></i>' + esc(w('Drag a space to move it, drag its corner to resize it, and press the pencil to draw the room inside.')) + '</div>' +
			(f.rooms.length ? '' : '<div class="text-muted fs-7 mb-3">' + esc(w('This floor has no rooms yet.')) + '</div>') +
			'<div class="overflow-auto"><div class="hp-floor" id="floorGrid">' + tiles + '</div></div>';
	}
	function wireFloor() {
		body.querySelectorAll('[data-floor]').forEach(function (b) { b.addEventListener('click', function () { st.floorId = +b.getAttribute('data-floor'); st.sel = null; draw(); }); });
		body.querySelectorAll('[data-draw]').forEach(function (b) { b.addEventListener('click', function (e) { e.stopPropagation(); openRoom(+b.getAttribute('data-draw')); }); });
		var grid = document.getElementById('floorGrid'); if (!grid) return;
		grid.addEventListener('pointerdown', function (e) {
			var tile = e.target.closest('.hp-tile'); if (!tile || e.target.closest('[data-draw]')) return;
			e.preventDefault();
			var r = floor().rooms.filter(function (x) { return x.id === +tile.getAttribute('data-room'); })[0];
			var resize = e.target.classList.contains('hp-handle');
			var colW = grid.clientWidth / 12, rowH = 92 + 9.6, sx = e.clientX, sy = e.clientY, o = { col: r.col, row: r.row, colSpan: r.colSpan, rowSpan: r.rowSpan };
			st.sel = r.id; grid.querySelectorAll('.hp-tile').forEach(function (t) { t.classList.toggle('sel', t === tile); });
			function move(ev) {
				var dx = (ev.clientX - sx) * (cfg.isAr ? -1 : 1), dc = Math.round(dx / colW), dr = Math.round((ev.clientY - sy) / rowH);
				if (resize) { r.colSpan = Math.max(1, Math.min(13 - o.col, o.colSpan + dc)); r.rowSpan = Math.max(1, o.rowSpan + dr); }
				else { r.col = Math.max(1, Math.min(13 - o.colSpan, o.col + dc)); r.row = Math.max(1, o.row + dr); }
				tile.style.gridColumn = r.col + ' / span ' + r.colSpan; tile.style.gridRow = r.row + ' / span ' + r.rowSpan;
			}
			function up() {
				document.removeEventListener('pointermove', move); document.removeEventListener('pointerup', up);
				if (r.col === o.col && r.row === o.row && r.colSpan === o.colSpan && r.rowSpan === o.rowSpan) return;
				request(cfg.urls.plan.replace(/0\/Plan$/, st.floorId + '/Plan'), [{ roomId: r.id, column: r.col, row: r.row, columnSpan: r.colSpan, rowSpan: r.rowSpan }])
					.then(function (res) {
						if (res.ok) { toast('success', w('Saved')); return; }
						// Refused (an overlap, usually): put the room back where the server says it is.
						r.col = o.col; r.row = o.row; r.colSpan = o.colSpan; r.rowSpan = o.rowSpan; draw();
						toast('error', res.error || w('The change could not be saved'));
					});
			}
			document.addEventListener('pointermove', move); document.addEventListener('pointerup', up);
		});
	}

	// ---------------- room mode ----------------
	var TOOLS = [['bed', 'ki-home-3', 'Bed'], ['bath', 'ki-drop', 'Bathroom'], ['door', 'ki-entrance-left', 'Door'], ['window', 'ki-sun', 'Window'], ['companion', 'ki-people', 'Companion bed'], ['lounge', 'ki-coffee', 'Lounge'], ['curtain', 'ki-abstract-14', 'Curtain'], ['anteroom', 'ki-shield-tick', 'Anteroom'], ['sink', 'ki-drop', 'Sink']];
	var SIZES = { bed: [26, 46], bath: [22, 34], door: [16, 3], window: [40, 3], companion: [28, 16], lounge: [24, 34], curtain: [0.6, 60], anteroom: [22, 34], sink: [10, 10] };

	function roomView() {
		var r = room(); if (!r) { st.mode = 'floor'; return floorView(); }
		var t = typeOf(r), carries = cfg.bedKinds.indexOf(r.kind) !== -1;
		if (!items) items = JSON.parse(JSON.stringify(HospitalPlan.items(r, t)));
		var sel = st.sel != null ? items[st.sel] : null;
		var beds = {}; r.beds.forEach(function (b) { beds[b.code] = b; });
		var held = sel && sel.k === 'bed' && beds[sel.bed] && (beds[sel.bed].status === 'Occupied' || beds[sel.bed].status === 'Reserved');
		var left = '<button class="btn btn-sm btn-light mb-5" data-back><i class="ki-outline ki-arrow-' + (cfg.isAr ? 'right' : 'left') + ' fs-5"></i>' + esc(w('Back to the floor')) + '</button>' +
			'<div class="fw-bold text-gray-800 mb-3">' + esc(w('Add to the room')) + '</div><div class="d-flex flex-wrap gap-2 mb-5">' +
			TOOLS.filter(function (x) { return x[0] !== 'bed' || carries; }).map(function (x) { return '<button class="btn btn-sm btn-light-primary" data-add="' + x[0] + '"><i class="ki-outline ' + x[1] + ' fs-5"></i>' + esc(w(x[2])) + '</button>'; }).join('') + '</div>' +
			(sel ? '<div class="rounded p-4 mb-4" style="background:#F7FAFF;border:1px dashed var(--cb-brand, #3A5795)"><div class="fw-bold mb-3">' + esc(w('Selected')) + ': ' + (sel.k === 'bed' ? esc(w('Bed')) + ' <span class="hp-num">' + esc(sel.bed) + '</span>' : esc(w(TOOLS.filter(function (x) { return x[0] === sel.k; })[0][2]))) + '</div>' +
				(sel.k === 'bed' ? '<div class="mb-3"><label class="fs-9 text-muted d-block">' + esc(w('Bed code')) + '</label><input class="form-control form-control-sm form-control-solid hp-num" data-prop="bed" value="' + esc(sel.bed) + '"' + (beds[sel.bed] ? ' readonly' : '') + ' /></div>' : '') +
				'<div class="row g-2 mb-3">' + [['x', 'X %'], ['y', 'Y %'], ['w', 'W %'], ['h', 'H %']].map(function (p) { return '<div class="col-3"><label class="fs-9 text-muted d-block">' + esc(w(p[1])) + '</label><input class="form-control form-control-sm form-control-solid hp-num" data-prop="' + p[0] + '" value="' + Math.round(sel[p[0]] * 10) / 10 + '" /></div>'; }).join('') + '</div>' +
				'<button class="btn btn-sm btn-light-danger w-100" data-delete' + (held ? ' disabled title="' + esc(w('This bed holds a patient and cannot be removed')) + '"' : '') + '><i class="ki-outline ki-trash fs-5"></i>' + esc(w('Delete item')) + '</button></div>'
				: '<div class="text-muted fs-7 mb-4">' + esc(w('Click any item on the plan to select it.')) + '</div>') +
			'<div class="d-flex gap-2"><button class="btn btn-sm btn-light flex-fill" data-reset>' + esc(w('Default drawing')) + '</button><button class="btn btn-sm btn-primary flex-fill" data-save><i class="ki-outline ki-check fs-5"></i>' + esc(w('Save drawing')) + '</button></div>';
		var head = '<div class="d-flex flex-stack mb-3"><a class="fw-bold fs-5 text-gray-900 text-hover-primary" href="' + cfg.urls.roomPage.replace(/0$/, r.id) + '">' + esc(w('Room')) + ' <span class="hp-num">' + esc(r.number) + '</span>' + (t ? ' — ' + esc(name(t)) : '') + '</a>' +
			(st.dirty ? '<span class="badge badge-light-primary">' + esc(w('Unsaved changes')) + '</span>' : '<span class="badge badge-light-success">' + esc(w('Saved')) + '</span>') + '</div>';
		var draft = { id: r.id, kind: r.kind, beds: r.beds.concat(items.filter(function (i) { return i.k === 'bed' && !beds[i.bed]; }).map(function (i) { return { code: i.bed, status: 'Vacant' }; })) };
		return '<div class="row g-5"><div class="col-xl-4">' + left + '</div><div class="col-xl-8">' + head +
			HospitalPlan.render(draft, t, { items: items, edit: true, big: true, id: 'roomCanvas', selectedItem: st.sel }) +
			'<div class="text-muted fs-7 mt-3">' + esc(w('Drag an item to move it; drag its small corner to resize it.')) + '</div></div></div>';
	}
	function nextBedCode(r) {
		var used = items.filter(function (i) { return i.k === 'bed'; }).map(function (i) { return i.bed; });
		for (var i = 0; i < 26; i++) { var c = r.number + '-' + String.fromCharCode(65 + i); if (used.indexOf(c) === -1) return c; }
		return r.number + '-' + (used.length + 1);
	}
	function clash() {
		var solid = items.filter(function (i) { return ['bed', 'bath', 'anteroom', 'lounge', 'companion'].indexOf(i.k) !== -1; });
		for (var a = 0; a < solid.length; a++) for (var b = a + 1; b < solid.length; b++) {
			var p = solid[a], q = solid[b];
			if ((p.k === 'bed' || q.k === 'bed') && p.x < q.x + q.w && q.x < p.x + p.w && p.y < q.y + q.h && q.y < p.y + p.h) return true;
		}
		return false;
	}
	function wireRoom() {
		var r = room(); if (!r) return;
		body.querySelector('[data-back]').addEventListener('click', function () {
			var back = function () { st.mode = 'floor'; st.sel = null; st.dirty = false; items = null; history.replaceState(null, '', location.pathname); draw(); };
			if (!st.dirty) { back(); return; }
			window.cbConfirm(w('Discard unsaved changes?'), { kind: 'danger' }).then(function (ok) { if (ok) back(); });
		});
		body.querySelectorAll('[data-add]').forEach(function (b) {
			b.addEventListener('click', function () {
				var k = b.getAttribute('data-add'), s = SIZES[k];
				var it = { k: k, x: 37, y: k === 'window' ? -1.5 : k === 'door' ? 98 : 30, w: s[0], h: s[1] };
				if (k === 'bed') it.bed = nextBedCode(r);
				items.push(it); st.sel = items.length - 1; st.dirty = true; draw();
			});
		});
		body.querySelectorAll('[data-prop]').forEach(function (inp) {
			inp.addEventListener('change', function () {
				var it = items[st.sel], p = inp.getAttribute('data-prop');
				if (p === 'bed') { var v = inp.value.trim(); if (v) it.bed = v; } else if (!isNaN(+inp.value)) it[p] = +inp.value;
				st.dirty = true; draw();
			});
		});
		var del = body.querySelector('[data-delete]'); if (del) del.addEventListener('click', function () { items.splice(st.sel, 1); st.sel = null; st.dirty = true; draw(); });
		body.querySelector('[data-reset]').addEventListener('click', function () { items = JSON.parse(JSON.stringify(HospitalPlan.defaultItems(r, typeOf(r)))); st.sel = null; st.dirty = true; draw(); });
		body.querySelector('[data-save]').addEventListener('click', function () {
			if (clash()) { toast('error', w('Two items overlap')); return; }
			request(cfg.urls.layout.replace(/0\/Layout$/, r.id + '/Layout'), items).then(function (res) {
				if (!res.ok) { toast('error', res.error || w('The change could not be saved')); return; }
				st.dirty = false; items = null; toast('success', w('Saved')); load();
			});
		});
		var canvas = document.getElementById('roomCanvas');
		canvas.addEventListener('pointerdown', function (e) {
			var el = e.target.closest('[data-i]'); if (!el) { if (st.sel != null) { st.sel = null; draw(); } return; }
			e.preventDefault();
			var i = +el.getAttribute('data-i'), it = items[i], resize = e.target.classList.contains('hp-handle');
			var Wd = canvas.clientWidth, Ht = canvas.clientHeight, sx = e.clientX, sy = e.clientY, o = { x: it.x, y: it.y, w: it.w, h: it.h }, moved = false;
			if (st.sel !== i) { st.sel = i; canvas.querySelectorAll('[data-i]').forEach(function (x) { x.removeAttribute('data-sel'); }); el.setAttribute('data-sel', '1'); }
			function snap(v) { return Math.round(v * 2) / 2; }
			function move(ev) {
				var dx = (ev.clientX - sx) * (cfg.isAr ? -1 : 1) * 100 / Wd, dy = (ev.clientY - sy) * 100 / Ht;
				if (Math.abs(dx) + Math.abs(dy) > 0.3) moved = true;
				if (resize) { it.w = Math.max(0.6, Math.min(100 - it.x, snap(o.w + dx))); it.h = Math.max(1, Math.min(103 - it.y, snap(o.h + dy))); }
				else { it.x = Math.max(-2, Math.min(100 - it.w + 2, snap(o.x + dx))); it.y = Math.max(-2, Math.min(100 - it.h + 3, snap(o.y + dy))); }
				el.style.insetInlineStart = it.x + '%'; el.style.top = it.y + '%'; el.style.width = it.w + '%'; el.style.height = it.h + '%';
			}
			function up() { document.removeEventListener('pointermove', move); document.removeEventListener('pointerup', up); if (moved) st.dirty = true; draw(); }
			document.addEventListener('pointermove', move); document.addEventListener('pointerup', up);
		});
	}
	function openRoom(id) {
		if (st.dirty && st.mode === 'room') { window.cbConfirm(w('Discard unsaved changes?'), { kind: 'danger' }).then(function (ok) { if (ok) { st.dirty = false; openRoom(id); } }); return; }
		var r = allRooms().filter(function (x) { return x.id === id; })[0]; if (!r) return;
		st.mode = 'room'; st.roomId = id; st.floorId = r._floor.id; st.sel = null; st.dirty = false; items = null;
		history.replaceState(null, '', '#room-' + id); draw();
	}

	function draw() { body.innerHTML = st.mode === 'room' ? roomView() : floorView(); if (st.mode === 'room') wireRoom(); else wireFloor(); }

	// ---------------- add floor / ward / room ----------------
	// Every list in the modals is a Metronic select2 tied to its modal (the backend layout has no enhancer,
	// so the page owns the init). Options are rebuilt from the building on every open, then select2 is told.
	var $ = window.jQuery;
	function sel2(s) {
		if (!$ || !$.fn.select2) return;
		var $s = $(s);
		if (!$s.hasClass('select2-hidden-accessible')) {
			$s.select2({
				width: '100%', dir: cfg.isAr ? 'rtl' : 'ltr', dropdownParent: $s.closest('.modal'),
				placeholder: s.getAttribute('data-placeholder') || null, allowClear: s.getAttribute('data-allow-clear') === 'true',
				minimumResultsForSearch: 8
			});
		}
		$s.trigger('change.select2');
	}
	function options(list, label, selected) {
		return '<option value=""></option>' + list.map(function (o) { return '<option value="' + o.id + '"' + (o.id === selected ? ' selected' : '') + '>' + esc(label(o)) + '</option>'; }).join('');
	}
	function fillSelects() {
		var types = data.types.filter(function (t) { return t.active; });
		document.querySelectorAll('[data-floors]').forEach(function (s) { s.innerHTML = options(data.floors, name, st.floorId); });
		document.querySelectorAll('[data-types]').forEach(function (s) { s.innerHTML = options(types, function (t) { return name(t) + ' — ' + t.beds + ' ' + w('bed(s)'); }, types.length === 1 ? types[0].id : null); });
		document.querySelectorAll('[data-empty="floors"]').forEach(function (n) { n.classList.toggle('d-none', data.floors.length > 0); });
		syncWards();
		document.querySelectorAll('[data-hp-select2]').forEach(sel2);
	}
	function syncWards() {
		var form = document.querySelector('[data-form="room"]'), fid = +form.floorId.value, f = data.floors.filter(function (x) { return x.id === fid; })[0];
		form.wardId.innerHTML = options(f ? f.wards : [], name, null);
		sel2(form.wardId);
		var carries = cfg.bedKinds.indexOf(form.kind.value) !== -1, noTypes = !data.types.some(function (t) { return t.active; });
		form.querySelector('[data-type-row]').classList.toggle('d-none', !carries);
		form.querySelector('[data-empty="types"]').classList.toggle('d-none', !(carries && noTypes));
		// Nothing to save against: no floor at all, or a bed room with no type to make its beds from.
		document.querySelector('[data-submit="room"]').disabled = !data.floors.length || (carries && noTypes);
		document.querySelector('[data-submit="ward"]').disabled = !data.floors.length;
	}
	CB.onSelectChange(document.querySelector('[data-form="room"] [name="floorId"]'), syncWards);
	CB.onSelectChange(document.querySelector('[data-form="room"] [name="kind"]'), syncWards);
	function resetForm(m) { m.querySelector('form').reset(); }
	document.querySelectorAll('[data-open]').forEach(function (b) {
		b.addEventListener('click', function () {
			var m = document.getElementById(b.getAttribute('data-open')); resetForm(m); fillSelects();
			bootstrap.Modal.getOrCreateInstance(m).show();
		});
	});
	// "Add a floor first" inside the ward / room modal: close it and open the floor modal.
	document.querySelectorAll('[data-switch]').forEach(function (b) {
		b.addEventListener('click', function () {
			var from = b.closest('.modal'), to = document.getElementById(b.getAttribute('data-switch'));
			from.addEventListener('hidden.bs.modal', function once() { from.removeEventListener('hidden.bs.modal', once); resetForm(to); bootstrap.Modal.getOrCreateInstance(to).show(); });
			bootstrap.Modal.getInstance(from).hide();
		});
	});
	document.querySelectorAll('[data-seed-types]').forEach(function (b) {
		b.addEventListener('click', function () {
			b.disabled = true;
			request(cfg.urls.seedTypes, new FormData(), true).then(function (res) {
				b.disabled = false;
				if (!res.ok) { toast('error', res.error || w('The change could not be saved')); return; }
				toast('success', w('Saved'));
				return fetch(cfg.urls.building, { headers: { 'X-Requested-With': 'XMLHttpRequest' } }).then(function (r) { return r.json(); })
					.then(function (d) { if (d.ok) { data = d; fillSelects(); } });
			});
		});
	});
	document.querySelectorAll('[data-submit]').forEach(function (b) {
		b.addEventListener('click', function () {
			var kind = b.getAttribute('data-submit'), f = document.querySelector('[data-form="' + kind + '"]');
			var payload = {}; new FormData(f).forEach(function (v, k) { payload[k] = v; });
			['floorId', 'wardId', 'roomTypeId', 'sortOrder'].forEach(function (k) { if (k in payload) payload[k] = payload[k] === '' ? null : +payload[k]; });
			if (kind === 'room' && cfg.bedKinds.indexOf(payload.kind) === -1) payload.roomTypeId = null;
			request(cfg.urls[kind], payload).then(function (res) {
				if (!res.ok) { toast('error', res.error || w('The change could not be saved')); return; }
				bootstrap.Modal.getInstance(b.closest('.modal')).hide(); toast('success', w('Saved'));
				if (kind === 'floor') st.floorId = res.id;
				if (kind === 'room') { st.floorId = payload.floorId; st.sel = res.id; st.mode = 'floor'; }
				load();
			});
		});
	});

	var m = /^#room-(\d+)$/.exec(location.hash);
	load().then(function () { if (m) openRoom(+m[1]); });
})();
