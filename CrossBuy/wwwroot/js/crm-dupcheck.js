/* CRM C1 — "a similar record already exists", live, under every CRM entry form.
   Attaches itself to any form that posts to /Crm/SaveAccount, /Crm/SaveLead or /Crm/SaveContact: reads the
   name / phone / email inputs as the person types (debounced) and asks /Crm/DuplicateCheck. It never blocks a
   save — a person decides — it only puts the existing record, with a link and the reasons, in front of them. */
(function () {
	'use strict';
	if (window.__cbCrmDupBound) return;
	window.__cbCrmDupBound = true;

	var KINDS = { '/crm/saveaccount': 'account', '/crm/savelead': 'lead', '/crm/savecontact': 'contact' };
	// the warning's look lives in the C1 stylesheet; bring it along on screens that do not load it
	if (!document.querySelector('link[href*="crm-identity.css"]')) {
		var css = document.createElement('link'); css.rel = 'stylesheet'; css.href = '/css/crm-identity.css'; document.head.appendChild(css);
	}
	var isAr = (document.documentElement.getAttribute('lang') || '').indexOf('ar') === 0 || document.documentElement.dir === 'rtl';
	function t(ar, en) { return isAr ? ar : en; }
	function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }

	function kindOf(form) {
		var a = (form.getAttribute('action') || '').toLowerCase().split('?')[0];
		for (var k in KINDS) if (a.slice(-k.length) === k) return KINDS[k];
		return null;
	}
	function field(form, names) {
		for (var i = 0; i < names.length; i++) { var el = form.querySelector('[name="' + names[i] + '"]'); if (el) return el; }
		return null;
	}

	function attach(form) {
		var kind = kindOf(form);
		if (!kind || form.__cbDup) return;
		form.__cbDup = true;
		var name = field(form, ['name', 'Name']), phone = field(form, ['phone', 'Phone']), email = field(form, ['email', 'Email']);
		var idEl = field(form, ['id', 'Id', 'ID']);
		if (!name && !phone && !email) return;
		var box = document.createElement('div');
		box.className = 'crm-dup-box d-none';
		box.setAttribute('role', 'status');
		box.setAttribute('aria-live', 'polite');
		var anchor = (email || phone || name).closest('.row, .fv-row, .mb-5, div');
		(anchor && anchor.parentNode ? anchor.parentNode : form).appendChild(box);

		var timer = null, seq = 0;
		function run() {
			var n = name ? name.value.trim() : '', p = phone ? phone.value.trim() : '', e = email ? email.value.trim() : '';
			if (n.length < 3 && p.replace(/\D/g, '').length < 7 && e.indexOf('@') < 1) { box.classList.add('d-none'); return; }
			var my = ++seq;
			var qs = 'kind=' + kind + '&name=' + encodeURIComponent(n) + '&phone=' + encodeURIComponent(p) + '&email=' + encodeURIComponent(e) +
				(idEl && +idEl.value > 0 ? '&selfId=' + (+idEl.value) : '') + '&culture=' + (isAr ? 'ar' : 'en') + '&ui-culture=' + (isAr ? 'ar' : 'en');
			fetch('/Crm/DuplicateCheck?' + qs, { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
				.then(function (r) { return r.ok ? r.json() : []; })
				.then(function (hits) {
					if (my !== seq) return;
					if (!hits || !hits.length) { box.classList.add('d-none'); box.innerHTML = ''; return; }
					box.innerHTML = '<div class="fw-bold text-danger mb-2"><i class="ki-outline ki-copy fs-5 text-danger me-1"></i>' +
						t('يوجد سجل مشابه — تأكد قبل الحفظ', 'A similar record exists — check before saving') + '</div>' +
						hits.map(function (h) {
							return '<div class="d-flex flex-wrap align-items-center gap-2 py-1"><span class="badge badge-light-primary fs-9">' + esc(h.kindLabel) + '</span>' +
								'<a href="' + esc(h.url) + '" target="_blank" rel="noopener" class="text-gray-900 text-hover-primary">' + esc(h.name) + '</a>' +
								(h.sub ? '<span class="text-muted fs-8">' + esc(h.sub) + '</span>' : '') +
								'<span class="ms-auto">' + (h.reasons || []).map(function (r) { return '<span class="crm-reason">' + esc(r) + '</span>'; }).join('') + '</span></div>';
						}).join('');
					box.classList.remove('d-none');
				})
				.catch(function () { /* a failed check never blocks typing or saving */ });
		}
		function later() { clearTimeout(timer); timer = setTimeout(run, 450); }
		[name, phone, email].forEach(function (el) { if (el) { el.addEventListener('input', later); el.addEventListener('change', later); } });
	}

	function scan(root) { [].slice.call((root || document).querySelectorAll('form')).forEach(attach); }
	if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', function () { scan(); }); else scan();
	// forms that arrive later (a modal filled by script) are picked up too
	new MutationObserver(function (ms) { ms.forEach(function (m) { [].slice.call(m.addedNodes).forEach(function (n) { if (n.nodeType === 1) scan(n); }); }); })
		.observe(document.body, { childList: true, subtree: true });
})();
