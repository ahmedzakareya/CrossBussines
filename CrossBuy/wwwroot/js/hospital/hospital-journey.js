/* HOSPITAL H272 — the patient's journey after a step.
 * The owner (2026-11-26): when a step is done, tell the user what comes next; if he may do it, let him go there or stay.
 * HospitalJourney.after(patientId, doneKey, { root, onStay }) asks the server where the patient stands and shows it:
 * the step just done, the next step (go now / stay here, or which department does it), and the steps after it. */
(function () {
	'use strict';
	var ICONS = {
		'check-in': ['user-tick', 3], 'consult': ['pulse', 2], 'lab-sample': ['test-tubes', 2], 'imaging': ['picture', 2],
		'dispense': ['capsule', 2], 'ward': ['home-2', 2], 'collect': ['wallet', 4], 'close-bill': ['bill', 6]
	};
	function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
	function duo(name, n, css) { var h = '<i class="ki-duotone ki-' + name + ' ' + css + '">'; for (var k = 1; k <= n; k++) h += '<span class="path' + k + '"></span>'; return h + '</i>'; }
	function stepIcon(key, css) { var i = ICONS[key] || ['route', 4]; return duo(i[0], i[1], css); }
	function initials(name) { return String(name || '').trim().split(/\s+/).slice(0, 2).map(function (w) { return w.charAt(0); }).join('').toUpperCase(); }

	function render(d) {
		var t = d.text, steps = d.steps || [], next = steps[0], rtl = document.documentElement.dir === 'rtl';
		var p = d.patient;
		var head = '<div class="modal-header py-5"><div class="d-flex align-items-center gap-4">' +
			'<div class="symbol symbol-45px symbol-circle">' + (p.photo ? '<img src="' + esc(p.photo) + '" alt="" style="object-fit:cover" />' : '<span class="symbol-label fw-bold bg-light-primary text-primary">' + esc(initials(p.name)) + '</span>') + '</div>' +
			'<div class="d-flex flex-column"><span class="fs-8 fw-semibold text-gray-500">' + esc(t.title) + '</span>' +
			'<span class="fw-bold fs-4 text-gray-900" style="unicode-bidi:plaintext">' + esc(p.name) + '</span><span class="text-gray-500 fs-8">' + esc(p.mrn) + '</span></div></div>' +
			'<button type="button" class="btn btn-sm btn-icon btn-active-light-primary" data-bs-dismiss="modal"><i class="ki-outline ki-cross fs-1"></i></button></div>';

		var body = '<div class="modal-body py-6">';
		if (d.done) body += '<div class="d-flex align-items-center gap-3 p-4 mb-6 rounded bg-light-primary">' + duo('check-circle', 2, 'fs-2x text-primary') +
			'<div class="d-flex flex-column"><span class="fs-8 fw-semibold text-gray-600">' + esc(t.stepDone) + '</span><span class="fw-bold fs-6 text-gray-900">' + esc(d.done) + '</span></div></div>';
		if (!next) {
			body += '<div class="d-flex flex-column align-items-center text-center py-6">' + duo('like-tag', 2, 'fs-5x text-primary mb-4') +
				'<div class="fw-bold fs-4 text-gray-800">' + esc(t.complete) + '</div></div>';
		} else {
			body += '<div class="fs-7 fw-bold text-gray-600 mb-3">' + esc(t.next) + '</div>' +
				'<div class="d-flex align-items-center gap-4 p-5 rounded border border-2 border-dashed ' + (next.allowed ? 'border-primary' : 'border-gray-300') + '" data-journey-next>' +
				'<div class="symbol symbol-55px flex-shrink-0"><span class="symbol-label ' + (next.allowed ? 'bg-primary' : 'bg-light') + '">' + stepIcon(next.key, 'fs-2x ' + (next.allowed ? 'text-white' : 'text-gray-500')) + '</span></div>' +
				'<div class="d-flex flex-column flex-grow-1"><span class="fw-bold fs-3 text-gray-900">' + esc(next.title) + '</span>' +
				'<span class="text-gray-600 fs-7 mt-1"><i class="ki-outline ki-geolocation fs-6 me-1"></i>' + esc(next.where) + '</span></div></div>';
			if (!next.allowed) body += '<div class="d-flex align-items-start gap-3 mt-4 p-4 rounded bg-light">' + duo('lock-2', 5, 'fs-2 text-gray-600') +
				'<span class="text-gray-700 fs-7">' + esc(t.notYours.replace('{0}', next.where)) + '</span></div>';
			if (steps.length > 1) {
				body += '<div class="fs-7 fw-bold text-gray-600 mt-7 mb-3">' + esc(t.then) + '</div><div class="d-flex flex-column gap-3">';
				steps.slice(1).forEach(function (s, i) {
					body += '<div class="d-flex align-items-center gap-3"><span class="badge badge-circle badge-light fs-8 fw-bold">' + (i + 2) + '</span>' +
						'<div class="symbol symbol-35px"><span class="symbol-label bg-light">' + stepIcon(s.key, 'fs-3 text-gray-600') + '</span></div>' +
						'<span class="fw-semibold fs-6 text-gray-800 flex-grow-1">' + esc(s.title) + '</span><span class="badge badge-light fs-8">' + esc(s.where) + '</span></div>';
				});
				body += '</div>';
			}
		}
		body += '</div>';

		var foot = '<div class="modal-footer flex-nowrap">';
		if (next && next.allowed) foot += '<button type="button" class="btn btn-light" data-journey-stay>' + esc(t.stay) + '</button>' +
			'<a class="btn btn-primary d-inline-flex align-items-center gap-2" data-journey-go href="' + esc(next.url) + '">' + esc(t.go) + '<i class="ki-outline ki-arrow-' + (rtl ? 'left' : 'right') + ' fs-3"></i></a>';
		else foot += '<button type="button" class="btn btn-primary" data-journey-stay>' + esc(t.stay) + '</button>';
		foot += '</div>';
		return '<div class="modal fade" tabindex="-1" data-journey-modal><div class="modal-dialog modal-dialog-centered mw-600px"><div class="modal-content">' + head + body + foot + '</div></div></div>';
	}

	function after(patientId, doneKey, opts) {
		opts = opts || {};
		var root = opts.root || '/Hospital';
		var stay = typeof opts.onStay === 'function' ? opts.onStay : function () { };
		var url = root + '/Journey/' + encodeURIComponent(patientId) + (doneKey ? '?done=' + encodeURIComponent(doneKey) : '');
		return fetch(url, { headers: { 'X-Requested-With': 'XMLHttpRequest' }, credentials: 'same-origin' })
			.then(function (r) { return r.ok ? r.json() : null; })
			.then(function (d) {
				if (!d || !d.ok || !window.bootstrap) { stay(); return null; }
				var old = document.querySelector('[data-journey-modal]'); if (old) old.remove();
				var wrap = document.createElement('div'); wrap.innerHTML = render(d);
				var el = wrap.firstChild; document.body.appendChild(el);
				var going = false;
				var go = el.querySelector('[data-journey-go]');
				if (go) go.addEventListener('click', function () { going = true; });
				el.querySelector('[data-journey-stay]').addEventListener('click', function () { bootstrap.Modal.getInstance(el).hide(); });
				el.addEventListener('hidden.bs.modal', function () { el.remove(); if (!going) stay(); });
				new bootstrap.Modal(el, { backdrop: 'static' }).show();
				return d;
			})
			.catch(function () { stay(); return null; });
	}

	window.HospitalJourney = { after: after };
})();
