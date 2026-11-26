/* Hospital accommodation: the "accommodate a patient" window (admit and transfer) and the bed window.
   The server decides which beds a patient may take (HospitalAdmissionService) and checks every rule
   again on confirm; this script only shows the answer and draws the plans.
   After a change nothing reloads: the page's onChange callback redraws what it shows. */
(function () {
	'use strict';
	var cfg = window.HospitalAdmitConfig, W = cfg.words, esc = HospitalPlan.esc, $ = window.jQuery;
	var token = (document.querySelector('input[name="__RequestVerificationToken"]') || {}).value || '';
	var admitEl = document.getElementById('admitModal'), bedEl = document.getElementById('bedModal');
	var body = admitEl.querySelector('[data-admit-body]'), confirmBtn = admitEl.querySelector('[data-admit-confirm]');
	var picker = admitEl.querySelector('[data-admit-patient]');
	// H270: the id goes where the route keeps its 0 ("Admissions/0/Transfer" has it in the middle). The old /0$/ replace
	// left transfer and discharge posting for admission 0 - refused, behind a red toast read as "saved".
	function withId(url, id) { return url.replace(/\/0(?=\/|\?|$)/, '/' + id); }
	var state = { building: null, patient: null, beds: [], roomId: null, bedId: null, transfer: null, currency: null };
	var onChange = function () { };

	function w(k) { return W[k] || k; }
	function fmt(s) { var a = [].slice.call(arguments, 1); return w(s).replace(/\{(\d)\}/g, function (_, i) { return a[i]; }); }
	function money(v) { var d = state.currency ? state.currency.decimals : 2; return '<span class="hp-num">' + Number(v || 0).toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d }) + '</span>' + (state.currency ? ' <span class="text-muted fs-8">' + esc(state.currency.label) + '</span>' : ''); }
	function toast(kind, msg) { if (window.toastr) toastr[kind](msg); }
	function getJson(url) { return fetch(url, { headers: { 'X-Requested-With': 'XMLHttpRequest' } }).then(function (r) { return r.json(); }); }
	function post(url, payload) {
		return fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest', 'RequestVerificationToken': token }, body: JSON.stringify(payload || {}) })
			.then(function (r) { return r.json().catch(function () { return { ok: false }; }); });
	}
	function building() {
		return getJson(cfg.urls.building).then(function (b) { if (b.ok) { state.building = b; state.currency = b.currency; } return b; });
	}
	function rooms() { var a = []; (state.building ? state.building.floors : []).forEach(function (f) { f.rooms.forEach(function (r) { r._floor = f; a.push(r); }); }); return a; }
	function roomOfBed(bedId) { return rooms().filter(function (r) { return r.beds.some(function (b) { return b.id === bedId; }); })[0]; }
	function typeOf(r) { return (state.building.types || []).filter(function (t) { return t.id === r.typeId; })[0]; }
	function person(p, size) {
		return '<div class="d-flex align-items-center min-w-0"><div class="symbol symbol-' + size + 'px symbol-circle flex-shrink-0">' +
			(p.photo ? '<img src="' + esc(p.photo) + '" alt="" style="object-fit:cover" />' : '<span class="symbol-label fw-bold hp-initials">' + esc(p.initials || (p.nameEn || '?').split(' ').map(function (x) { return x.charAt(0); }).slice(0, 2).join('').toUpperCase()) + '</span>') +
			'</div><div class="d-flex flex-column ms-3 min-w-0"><a href="' + withId(cfg.urls.patient, p.id) + '" class="text-gray-900 text-hover-primary fs-6 fw-bold text-truncate">' + esc(p.name) + '</a>' +
			'<span class="text-muted fw-semibold fs-7"><span class="hp-num">' + esc(p.mrn) + '</span>' + (p.age != null ? ' · <span class="hp-num">' + p.age + '</span> ' + esc(w('y')) : '') + ' · ' + esc(p.sex === 'M' ? w('Male') : w('Female')) + '</span></div></div>';
	}

	// ---------------- accommodate / transfer ----------------
	function initPicker() {
		if (!$ || !$.fn.select2 || $(picker).hasClass('select2-hidden-accessible')) return;
		$(picker).select2({
			width: '100%', dir: cfg.isAr ? 'rtl' : 'ltr', dropdownParent: $(admitEl), placeholder: picker.getAttribute('data-placeholder'), minimumInputLength: 0,
			ajax: { url: cfg.urls.search, dataType: 'json', delay: 250, data: function (p) { return { q: p.term || '' }; }, processResults: function (d) { return { results: d.results || [] }; } }
		});
		$(picker).on('select2:select', function (e) { loadPatient(+e.params.data.id); });
	}
	function loadPatient(id) {
		state.patient = null; state.beds = []; draw();
		getJson(cfg.urls.suitable + '?patientId=' + id).then(function (res) {
			if (!res.ok) return;
			state.patient = res.patient; state.beds = res.beds; state.currency = res.currency || state.currency;
			var keep = state.beds.some(function (b) { return b.bedId === state.bedId; });
			if (!keep) { state.bedId = state.beds.length ? state.beds[0].bedId : null; }
			state.roomId = state.bedId ? state.beds.filter(function (b) { return b.bedId === state.bedId; })[0].roomId : null;
			draw();
		});
	}
	function draw() {
		var p = state.patient;
		if (!p) { body.innerHTML = ''; confirmBtn.disabled = true; return; }
		var already = p.admitted && !state.transfer;
		var head = '<div class="d-flex flex-wrap align-items-center gap-4 mb-5">' + person(p, 45) +
			(p.allergies ? '<span class="badge badge-light-danger fs-8 ms-auto"><i class="ki-outline ki-shield-cross fs-7 text-danger me-1"></i>' + esc(w('Allergy')) + ': ' + esc(p.allergies.split('\n').join('، ')) + '</span>' : '') + '</div>';
		if (already) {
			body.innerHTML = head + notice('danger', w('Already admitted'), fmt('This patient is already in room {0}, bed {1}.', p.admitted.room, p.admitted.bed));
			confirmBtn.disabled = true; return;
		}
		// rooms with a free, suitable bed — grouped from the bed list
		var byRoom = {}; state.beds.forEach(function (b) { (byRoom[b.roomId] = byRoom[b.roomId] || { r: b, free: [] }).free.push(b); });
		var keys = Object.keys(byRoom);
		if (!keys.length) {
			body.innerHTML = head + notice('danger', w('No suitable bed'), w('Every suitable room is full; choose another patient or free a bed first.'));
			confirmBtn.disabled = true; return;
		}
		var list = keys.map(function (k) {
			var o = byRoom[k], active = +k === state.roomId;
			return '<div class="d-flex align-items-center py-3 px-3 mb-1 rounded cursor-pointer ' + (active ? 'bg-light-primary' : '') + '" data-room-pick="' + k + '">' +
				'<div class="flex-grow-1 min-w-0"><div class="fw-bold text-gray-900">' + esc(w('Room')) + ' <span class="hp-num">' + esc(o.r.room) + '</span>' + (o.r.type ? ' · ' + esc(o.r.type) : '') + '</div>' +
				'<div class="text-muted fs-8">' + (o.r.ward ? esc(o.r.ward) + ' · ' : '') + '<span class="hp-num">' + o.free.length + '</span> ' + esc(w('bed(s) free')) + '</div></div>' +
				'<div class="text-end fw-bold fs-7 text-nowrap">' + money(o.r.rate) + '</div></div>';
		}).join('');
		var room = rooms().filter(function (r) { return r.id === state.roomId; })[0];
		var free = {}; state.beds.forEach(function (b) { free[b.bedId] = true; });
		var chosen = state.beds.filter(function (b) { return b.bedId === state.bedId; })[0];
		var right = room ? '<div class="d-flex flex-stack mb-3"><span class="fw-bold fs-5">' + esc(w('Room')) + ' <span class="hp-num">' + esc(room.number) + '</span>' + (chosen && chosen.type ? ' — ' + esc(chosen.type) : '') + '</span><span class="text-muted fs-7">' + esc(w('Pick the bed on the plan')) + '</span></div>' +
			HospitalPlan.render(room, typeOf(room), { selectedBed: state.bedId, pickable: function (b) { return !!free[b.id]; }, onBed: true }) +
			'<div class="row g-4 mt-2">' +
			'<div class="col-4"><div class="text-muted fs-8 fw-semibold">' + esc(w('Bed')) + '</div><div class="fw-bold fs-6 hp-num">' + esc(chosen ? chosen.bed : '—') + '</div></div>' +
			'<div class="col-4"><div class="text-muted fs-8 fw-semibold">' + esc(w('Daily rate')) + '</div><div class="fw-bold fs-6">' + (chosen ? money(chosen.rate) : '—') + '</div></div>' +
			'<div class="col-4"><div class="text-muted fs-8 fw-semibold">' + esc(w('Ward')) + '</div><div class="fw-bold fs-6">' + esc(chosen && chosen.ward ? chosen.ward : '—') + '</div></div></div>' +
			(state.transfer ? '' : '<div class="mt-5"><label class="fw-semibold fs-7 text-gray-700 mb-2">' + esc(w('Diagnosis')) + '</label><input class="form-control form-control-solid" maxlength="400" data-admit-dx /></div>') : '';
		body.innerHTML = head + '<div class="row g-5"><div class="col-lg-5"><div class="fw-bold text-gray-800 mb-2">' + esc(w('Suitable rooms with a free bed')) + '</div>' +
			'<div class="text-muted fs-8 mb-3">' + esc(w('Excluded automatically: shared rooms of the other sex and wards for the other sex')) + '</div>' +
			'<div style="max-height:380px;overflow:auto">' + list + '</div></div><div class="col-lg-7">' + right + '</div></div>';
		body.querySelectorAll('[data-room-pick]').forEach(function (el) {
			el.addEventListener('click', function () {
				state.roomId = +el.getAttribute('data-room-pick');
				state.bedId = state.beds.filter(function (b) { return b.roomId === state.roomId; })[0].bedId; draw();
			});
		});
		body.querySelectorAll('.hp-plan [data-bed]').forEach(function (el) {
			el.addEventListener('click', function (e) { e.preventDefault(); var id = +el.getAttribute('data-bed'); if (free[id]) { state.bedId = id; draw(); } });
		});
		confirmBtn.disabled = !state.bedId;
	}
	function notice(kind, title, text) {
		return '<div class="notice d-flex rounded border border-' + kind + ' border-dashed bg-light-' + kind + ' p-5"><i class="ki-outline ki-information-5 fs-2tx text-' + kind + ' me-4"></i>' +
			'<div class="fw-semibold"><h4 class="text-gray-900 fw-bold mb-1">' + esc(title) + '</h4><div class="fs-6 text-gray-700">' + esc(text) + '</div></div></div>';
	}
	confirmBtn.addEventListener('click', function () {
		if (!state.patient) { toast('error', w('Choose a patient first')); return; }
		if (!state.bedId) { toast('error', w('Choose a bed on the plan')); return; }
		confirmBtn.disabled = true;
		var dx = body.querySelector('[data-admit-dx]');
		var call = state.transfer
			? post(withId(cfg.urls.transfer, state.transfer), { bedId: state.bedId })
			: post(cfg.urls.admit, { patientId: state.patient.id, bedId: state.bedId, diagnosis: dx ? dx.value : null });
		call.then(function (res) {
			if (!res.ok) { confirmBtn.disabled = false; toast('error', res.error || w('The change could not be saved')); return; }
			bootstrap.Modal.getInstance(admitEl).hide(); toast('success', w('Saved'));
			building().then(function () { onChange(); });
		});
	});

	/// opts: { patientId?, bedId?, transferAdmissionId? }
	function open(opts) {
		opts = opts || {};
		state.patient = null; state.beds = []; state.bedId = opts.bedId || null; state.roomId = null; state.transfer = opts.transferAdmissionId || null;
		admitEl.querySelector('[data-admit-title]').textContent = w(state.transfer ? 'Transfer the patient' : 'Accommodate a patient');
		confirmBtn.querySelector('span').textContent = w(state.transfer ? 'Confirm transfer' : 'Confirm accommodation');
		admitEl.querySelector('[data-admit-picker]').classList.toggle('d-none', !!opts.patientId);
		initPicker();
		if ($) $(picker).val(null).trigger('change');
		draw();
		(state.building ? Promise.resolve() : building()).then(function () { if (opts.patientId) loadPatient(opts.patientId); });
		bootstrap.Modal.getOrCreateInstance(admitEl).show();
	}

	// ---------------- the bed window ----------------
	function bed(bedId, canAdmit) {
		(state.building ? Promise.resolve() : building()).then(function () {
			var r = roomOfBed(bedId); if (!r) return;
			var b = r.beds.filter(function (x) { return x.id === bedId; })[0], t = typeOf(r), p = b.patient;
			bedEl.querySelector('[data-bed-title]').innerHTML = esc(w('Bed')) + ' <span class="hp-num">' + esc(b.code) + '</span> — ' + esc(w(b.status));
			var info = '<div class="d-flex flex-wrap gap-2 mb-5">' + (r._floor ? '<span class="hp-chip">' + esc(cfg.isAr ? r._floor.name : (r._floor.nameEn || r._floor.name)) + '</span>' : '') +
				'<span class="hp-chip">' + esc(w('Room')) + ' <span class="hp-num">' + esc(r.number) + '</span>' + (t ? ' · ' + esc(cfg.isAr ? t.name : (t.nameEn || t.name)) : '') + '</span>' +
				(t ? '<span class="hp-chip">' + money(t.rate) + ' ' + esc(w('per day')) + '</span>' : '') + '</div>' +
				HospitalPlan.render(r, t, { selectedBed: bedId });
			var foot = '<button type="button" class="btn btn-light" data-bs-dismiss="modal">' + esc(w('Close')) + '</button>';
			if (p) {
				var since = new Date(p.since.replace(/Z?$/, 'Z'));
				info += '<div class="separator my-5"></div><div class="d-flex flex-wrap align-items-center gap-4 mb-4">' + person(p, 55) + '</div><div class="row g-5">' +
					'<div class="col-md-4"><div class="text-muted fs-8 fw-semibold">' + esc(w('Since')) + '</div><div class="fw-bold text-gray-900 fs-7 hp-num">' + since.toISOString().slice(0, 10) + '</div></div>' +
					'<div class="col-md-4"><div class="text-muted fs-8 fw-semibold">' + esc(w('Diagnosis')) + '</div><div class="fw-bold text-gray-900 fs-7">' + esc(p.diagnosis || '—') + '</div></div>' +
					'<div class="col-md-4"><div class="text-muted fs-8 fw-semibold">' + esc(w('Allergy')) + '</div><div class="fw-bold fs-7 ' + (p.allergy ? 'text-danger' : 'text-gray-900') + '">' + (p.allergy ? '!' : '—') + '</div></div></div>';
				foot += '<a class="btn btn-light-primary" href="' + withId(cfg.urls.patient, p.id) + '">' + esc(w('Open file')) + '</a>';
				if (canAdmit) foot += '<button type="button" class="btn btn-light-primary" data-bed-transfer><i class="ki-outline ki-arrows-loop fs-4"></i>' + esc(w('Transfer')) + '</button>' +
					'<button type="button" class="btn btn-primary" data-bed-discharge><i class="ki-outline ki-exit-right fs-4"></i>' + esc(w('Discharge')) + '</button>';
			} else if (b.status === 'Vacant' && canAdmit) {
				foot += '<button type="button" class="btn btn-primary" data-bed-admit><i class="ki-outline ki-entrance-left fs-4"></i>' + esc(w('Accommodate a patient here')) + '</button>';
			} else if (b.status !== 'Vacant') {
				info += '<div class="text-muted fs-7 mt-4">' + esc(w('This bed is not available to accommodate.')) + '</div>';
			}
			bedEl.querySelector('[data-bed-body]').innerHTML = info;
			var f = bedEl.querySelector('[data-bed-footer]'); f.innerHTML = foot;
			var modal = bootstrap.Modal.getOrCreateInstance(bedEl);
			var then = function (fn) { bedEl.addEventListener('hidden.bs.modal', function once() { bedEl.removeEventListener('hidden.bs.modal', once); fn(); }); modal.hide(); };
			var a = f.querySelector('[data-bed-admit]'); if (a) a.addEventListener('click', function () { then(function () { open({ bedId: bedId }); }); });
			var tr = f.querySelector('[data-bed-transfer]'); if (tr) tr.addEventListener('click', function () { then(function () { open({ patientId: p.id, transferAdmissionId: p.admissionId }); }); });
			var d = f.querySelector('[data-bed-discharge]');
			if (d) d.addEventListener('click', function () {
				window.cbConfirm(w('Discharge this patient? The bed goes to cleaning.'), { kind: 'danger' }).then(function (ok) {
					if (!ok) return;
					d.disabled = true;
					post(withId(cfg.urls.discharge, p.admissionId)).then(function (res) {
						if (!res.ok) { d.disabled = false; toast('error', res.error || w('The change could not be saved')); return; }
						modal.hide(); toast('success', w('Saved')); building().then(function () { onChange(); });
						if (window.HospitalJourney) window.HospitalJourney.after(p.id, 'discharge', { root: cfg.root });   // H272: what the patient still has (the bill)
					});
				});
			});
			modal.show();
		});
	}

	function discharge(admissionId) {
		return window.cbConfirm(w('Discharge this patient? The bed goes to cleaning.'), { kind: 'danger' }).then(function (ok) {
			if (!ok) return false;
			return post(withId(cfg.urls.discharge, admissionId)).then(function (res) {
				if (!res.ok) { toast('error', res.error || w('The change could not be saved')); return false; }
				toast('success', w('Saved')); return building().then(function () { onChange(); return true; });
			});
		});
	}

	window.HospitalAdmit = {
		open: open, bed: bed, discharge: discharge, building: function () { return state.building; }, refresh: building,
		onChange: function (fn) { onChange = fn || function () { }; }
	};
})();
