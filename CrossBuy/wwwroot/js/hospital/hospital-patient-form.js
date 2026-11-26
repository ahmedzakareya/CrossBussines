/* HOSPITAL H272 — what the patient's two forms share (the desk's registration window and the file's edit page).
 * 1. A refusal from the server shows in red under the field it is about, never as a toast (owner rule): the service's
 *    untranslated key names the field.
 * 2. The edit page shows, before it saves, exactly what changes - each field's old value and its new one (the owner:
 *    "before an edit, a confirmation; make sure the edit's data is the data that was saved"). */
(function () {
	'use strict';
	var FIELD = {
		'Enter the Arabic name': 'name', 'Enter the English name': 'nameEn',
		'The ID number must be 5 to 20 digits': 'nationalId', 'This ID number already has a file: {0}': 'nationalId',
		'Enter a valid mobile number': 'phone', 'Enter a real date of birth': 'birthDate', 'Choose the sex': 'sex',
		'Unknown nationality': 'nationalityId', 'Unknown blood group': 'bloodGroup',
		'Unknown allergen': 'allergy', 'Choose the allergy in every row, or remove the row': 'allergy',
		'Choose an approved price list': 'priceListId'
	};
	function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
	function field(root, name) {
		if (name === 'allergy') return root.querySelector('[data-allergy-picker]');
		return root.querySelector('[name="' + name + '"], [data-reg-f="' + name + '"]');
	}
	function holder(el) {
		if (el.matches('[data-allergy-picker]')) return el;
		var c = el.parentElement;
		while (c && !/(^|\s)col(-|\s|$)/.test(c.className || '')) c = c.parentElement;
		return c || el.parentElement;
	}
	function clear(root) {
		root.querySelectorAll('[data-fe]').forEach(function (m) { m.remove(); });
		root.querySelectorAll('.is-invalid').forEach(function (e) { e.classList.remove('is-invalid'); });
		root.querySelectorAll('.select2-selection.border-danger').forEach(function (e) { e.classList.remove('border', 'border-danger'); });
	}
	/// Writes the message under the field; true when the field was found (the caller toasts only what has no field).
	function error(root, name, message) {
		var el = field(root, name); if (!el) return false;
		var h = holder(el);
		var m = document.createElement('div');
		m.className = 'invalid-feedback d-block fs-7 fw-semibold'; m.setAttribute('data-fe', ''); m.textContent = message;
		h.appendChild(m);
		if (!el.matches('[data-allergy-picker]')) {
			el.classList.add('is-invalid');
			var s2 = el.nextElementSibling && el.nextElementSibling.classList.contains('select2') ? el.nextElementSibling.querySelector('.select2-selection') : null;
			if (s2) s2.classList.add('border', 'border-danger');
		}
		var clean = function () { m.remove(); el.classList.remove('is-invalid'); };
		el.addEventListener('input', clean, { once: true }); el.addEventListener('change', clean, { once: true });
		if (window.jQuery) jQuery(el).one('change', clean);
		return true;
	}
	/// A refusal of the save: under its field when the key names one; false when it does not.
	function refusal(root, res) {
		var name = FIELD[res && res.key];
		if (!name) return false;
		var shown = error(root, name, res.error || res.key);
		var el = shown && field(root, name);
		if (el) (holder(el) || el).scrollIntoView({ block: 'center', behavior: 'smooth' });
		return shown;
	}

	// ---- the edit page: what the file holds now, and what will change --------------------------------------------------
	function labelOf(el) {
		if (el.matches('[data-allergy-picker]')) return null;
		var h = holder(el), l = h && h.querySelector('label');
		return l ? l.textContent.replace(/\*/g, '').trim() : el.name;
	}
	function shownValue(el, words) {
		if (el.type === 'checkbox') return el.checked ? words.yes : words.no;
		if (el.tagName === 'SELECT') return el.value ? (el.options[el.selectedIndex] || {}).text || '' : '—';
		return (el.value || '').trim() || '—';
	}
	function allergies(root, words) {
		var box = root.querySelector('[data-allergy-picker]'); if (!box) return null;
		var nka = box.querySelector('[data-ap-nka]');
		if (nka && nka.checked) return words.nka;
		var rows = [].map.call(box.querySelectorAll('[data-ap-row]'), function (r) {
			return [].map.call(r.querySelectorAll('select'), function (s) { return s.value ? (s.options[s.selectedIndex] || {}).text : ''; }).filter(Boolean).join(' · ');
		}).filter(Boolean);
		return rows.length ? rows.join('، ') : '—';
	}
	function snapshot(form, words) {
		var out = [];
		form.querySelectorAll('input[name], select[name], textarea[name]').forEach(function (el) {
			if (el.type === 'hidden' || el.type === 'file' || el.closest('[data-snapshot-skip]')) return;
			out.push({ key: el.name, label: labelOf(el), value: shownValue(el, words) });
		});
		var a = allergies(form, words);
		if (a != null) out.push({ key: '#allergies', label: words.allergies, value: a });
		return out;
	}
	function changes(before, after) {
		var was = {}; before.forEach(function (f) { was[f.key] = f; });
		return after.filter(function (f) { return was[f.key] && was[f.key].value !== f.value; })
			.map(function (f) { return { label: f.label, from: was[f.key].value, to: f.value }; });
	}
	/// The confirmation: each changed field with its old and new value. Resolves true to save.
	function confirmChanges(list, words) {
		var rtl = document.documentElement.dir === 'rtl', arrow = rtl ? '←' : '→';
		var html = list.length
			? '<div class="text-gray-700 fs-6 mb-4">' + esc(words.intro) + '</div><div class="d-flex flex-column gap-3 text-start">' + list.map(function (c) {
				return '<div class="border border-dashed border-gray-300 rounded px-4 py-3"><div class="fw-bold fs-7 text-gray-600 mb-1">' + esc(c.label) + '</div>' +
					'<div class="fs-6"><span class="text-gray-500 text-decoration-line-through" style="unicode-bidi:plaintext">' + esc(c.from) + '</span> <span class="text-gray-400 mx-1">' + arrow + '</span> ' +
					'<span class="fw-bold text-primary" style="unicode-bidi:plaintext">' + esc(c.to) + '</span></div></div>';
			}).join('') + '</div>'
			: '<div class="text-gray-700 fs-6">' + esc(words.nothing) + '</div>';
		if (!window.CB || !CB.confirm) return Promise.resolve(true);
		return CB.confirm({ icon: list.length ? 'question' : 'info', title: words.title, html: html, confirmText: words.save, confirmClass: 'btn btn-primary' });
	}

	// ---- the photo (Views/Hospital/_PatientPhotoHead.cshtml): from a file or the camera; the image is sent after the save -
	/// Works the photo head inside root. Returns { blob() - the new image or null, stop() - closes the camera }.
	function photo(root) {
		var head = root.querySelector('[data-ph]'); if (!head) return { blob: function () { return null; }, stop: function () { } };
		var q = function (a) { return head.querySelector('[data-ph-' + a + ']'); };
		var w = {}; try { w = JSON.parse(head.getAttribute('data-ph-words') || '{}'); } catch (e) { }
		var stream = null, blob = null, msg = q('msg');
		function say(t) { msg.textContent = t || ''; msg.classList.toggle('d-none', !t); }
		function stop() { if (stream) { stream.getTracks().forEach(function (x) { x.stop(); }); stream = null; } q('video').classList.add('d-none'); q('snap').classList.add('d-none'); }
		function show(b) { blob = b; var img = q('shot'); img.src = URL.createObjectURL(b); img.classList.remove('d-none'); q('video').classList.add('d-none'); q('empty').classList.add('d-none'); say(''); }
		q('file').addEventListener('change', function (ev) {
			var f = ev.target.files[0]; if (!f) return;
			if (f.size > 5 * 1024 * 1024 || !/^image\//.test(f.type)) { say(w.big); ev.target.value = ''; return; }
			stop(); show(f);
		});
		q('cam').addEventListener('click', function () {
			// a browser offers the camera only on https (or localhost); over plain http mediaDevices is simply absent
			if (!window.isSecureContext) { say(w.cameraHttps); return; }
			if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) { say(w.camera); return; }
			navigator.mediaDevices.getUserMedia({ video: { facingMode: 'user', width: { ideal: 640 }, height: { ideal: 640 } }, audio: false }).then(function (s) {
				stream = s; var v = q('video'); v.srcObject = s; v.classList.remove('d-none'); q('shot').classList.add('d-none'); q('empty').classList.add('d-none');
				q('snap').classList.remove('d-none'); say('');
			}).catch(function () { say(w.camera); });
		});
		q('snap').addEventListener('click', function () {
			var vid = q('video'), vw = vid.videoWidth, vh = vid.videoHeight, side = Math.min(vw, vh); if (!side) return;
			var c = document.createElement('canvas'); c.width = c.height = Math.min(side, 640);
			c.getContext('2d').drawImage(vid, (vw - side) / 2, (vh - side) / 2, side, side, 0, 0, c.width, c.height);
			c.toBlob(function (b) { stop(); show(b); }, 'image/jpeg', 0.9);
		});
		return { blob: function () { return blob; }, stop: stop };
	}

	window.HospPatientForm = { clear: clear, error: error, refusal: refusal, snapshot: snapshot, changes: changes, confirmChanges: confirmChanges, photo: photo };
})();
