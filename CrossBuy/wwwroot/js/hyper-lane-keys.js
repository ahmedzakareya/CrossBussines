/* ============================================================================
   CASHIER LANE — FULL KEYBOARD OPERATION

   Requirement: a cashier must be able to run the whole lane without ever touching
   a mouse — by keyboard alone, or by touch alone. This file is the keyboard half.

   HOW IT BINDS, AND WHY THAT WAY. The lane is server-rendered: every action is its
   own <form method="post"> posting to its own URL. So the shortcuts resolve their
   target by that URL — form[action="/hyper/pos/hold"] — never by a generated id or
   a DOM position. A redesign that moves a button keeps working; only renaming the
   endpoint would break it, and that is a change nobody makes by accident.

   WHAT "COVER EVERY CASE" MEANS HERE. Every action the lane offers has a key, and
   every key is shown on screen: the footer strip carries the common ones and F1
   opens the full list. A shortcut nobody can discover is a shortcut nobody uses,
   so discoverability is part of the feature, not decoration.

   THE SCAN FIELD IS HOME. A lane is driven by a scanner, which is a keyboard that
   types very fast and presses Enter. So focus returns to the scan field after every
   action, and any bare digit typed anywhere lands in it — otherwise the first
   barcode of the next sale disappears into whatever happened to be focused.

   Bilingual: every string here has both halves, chosen from <html lang>.
   ============================================================================ */
(function () {
	'use strict';

	var isAr = (document.documentElement.getAttribute('lang') || 'ar').toLowerCase().indexOf('ar') === 0;
	function L(ar, en) { return isAr ? ar : en; }

	function $(sel, root) { return (root || document).querySelector(sel); }
	function $$(sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); }

	/* The lane's actions, each identified by the endpoint its form posts to. */
	function formFor(path) { return $('form[action="/hyper/pos/' + path + '"]'); }
	function submitForm(path) {
		var f = formFor(path);
		if (!f) { flash(L('هذا الإجراء غير متاح الآن', 'That action is not available right now')); return false; }
		var btn = $('button[type="submit"]', f);
		if (btn && btn.disabled) { flash(L('هذا الإجراء معطَّل الآن', 'That action is disabled right now')); return false; }
		f.submit();
		return true;
	}

	var scan = function () { return $('input[name="barcode"]'); };

	function focusScan() {
		var s = scan();
		if (s) { s.focus(); try { s.select(); } catch (e) {} }
	}

	/* ---- a small, non-blocking message; never an alert() ---- */
	var flashEl = null;
	function flash(msg) {
		if (!flashEl) {
			flashEl = document.createElement('div');
			flashEl.className = 'lane-flash';
			document.body.appendChild(flashEl);
		}
		flashEl.textContent = msg;
		flashEl.classList.add('is-on');
		clearTimeout(flashEl._t);
		flashEl._t = setTimeout(function () { flashEl.classList.remove('is-on'); }, 1800);
	}

	/* ---- basket row selection, so lines can be edited without pointing ---- */
	function rows() { return $$('[data-lane-row]'); }
	var sel = -1;
	function paint() {
		rows().forEach(function (r, i) { r.classList.toggle('is-sel', i === sel); });
		var r = rows()[sel];
		if (r && r.scrollIntoView) r.scrollIntoView({ block: 'nearest' });
	}
	function move(d) {
		var n = rows().length;
		if (!n) { sel = -1; return; }
		sel = sel < 0 ? (d > 0 ? 0 : n - 1) : Math.min(n - 1, Math.max(0, sel + d));
		paint();
	}
	function rowAction(path, extra) {
		var r = rows()[sel];
		if (!r) { flash(L('اختر سطراً أولاً بالسهمين', 'Select a line first with the arrow keys')); return; }
		var f = $('form[action="/hyper/pos/' + path + '"]', r);
		if (!f) { flash(L('هذا الإجراء غير متاح على هذا السطر', 'That action is not available on this line')); return; }
		if (extra) { var q = $('input[name="qty"]', f); if (q) q.value = extra(parseFloat(q.value) || 0); }
		f.submit();
	}

	/* ---- tenders: the lane posts a method with the payment, so set and show ---- */
	function pickTender(kind) {
		var sel2 = $('#cbPayMethod');
		if (!sel2) { flash(L('الدفع غير متاح الآن', 'Payment is not available right now')); return; }
		sel2.value = kind;
		if (typeof sel2.onchange === 'function') sel2.onchange();
		sel2.dispatchEvent(new Event('change', { bubbles: true }));
		$$('[data-tender]').forEach(function (b) { b.classList.toggle('is-on', b.getAttribute('data-tender') === kind); });
		var amt = kind === 'Card' ? null : $('#cbCashIn');
		if (amt) { amt.focus(); try { amt.select(); } catch (e) {} } else focusScan();
		flash(L('طريقة الدفع: ', 'Payment method: ') + (kind === 'Cash' ? L('نقدي', 'Cash') : kind === 'Card' ? L('شبكة', 'Card') : L('مُقسَّم', 'Split')));
	}

	/* ---- the shortcut table: one source for behaviour AND for the help sheet ---- */
	/* ---- HM-AGE: the gate in front of payment --------------------------------------------------
	   A restricted basket cannot be paid for until the cashier states they looked at an ID. This
	   disables the pay button and lights the gate; the REFUSAL itself is on the server, because a
	   button disabled in a browser is advice, not a control. Once cleared it stays cleared for this
	   basket - re-asking after every scan would train the cashier to click it without reading. */
	function gate() { return document.querySelector('[data-age-gate]'); }
	function payBtn() { return document.querySelector('.lane-pay'); }

	function confirmAge() {
		var g = gate(); if (!g || g.classList.contains('is-ok')) return;
		var f = document.getElementById('cbAgeConfirmed'); if (f) f.value = 'true';
		g.classList.add('is-ok');
		var ok = g.querySelector('[data-age-ok]');
		if (ok) ok.innerHTML = '<i class="ki-outline ki-check-circle fs-2 me-1"></i>' +
			L('تم التحقّق من السن', 'Age verified');
		var b = payBtn(); if (b) { b.disabled = false; b.focus(); }
	}

	function wireGate() {
		var g = gate(); if (!g) return;
		var b = payBtn(); if (b) b.disabled = true;
		var ok = g.querySelector('[data-age-ok]');
		if (ok) ok.addEventListener('click', confirmAge);
	}

	var MAP = [
		{ k: 'F1', ar: 'قائمة الاختصارات', en: 'Shortcut list', run: toggleHelp },
		{ k: 'F2', ar: 'العودة إلى حقل المسح', en: 'Back to the scan field', run: focusScan },
		{ k: 'F3', ar: 'فحص السعر', en: 'Price check', run: function () { go('/hyper/pos/pricecheck'); } },
		{ k: 'F4', ar: 'العميل', en: 'Customer', run: function () { go('/hyper/pos/customer'); } },
		{ k: 'F6', ar: 'تعليق البيعة', en: 'Park the sale', run: function () { submitForm('hold'); } },
		{ k: 'F7', ar: 'استئناف أول بيعة معلّقة', en: 'Resume the first parked sale', run: function () { submitForm('resume'); } },
		{ k: 'F8', ar: 'إلغاء البيعة', en: 'Void the sale', run: function () { submitForm('void'); } },
		{ k: 'Shift+F7', ar: 'تأكيد السن بعد رؤية الهوية', en: 'Confirm the age after seeing the ID', run: confirmAge },
		{ k: 'F9', ar: 'دفع نقدي', en: 'Tender cash', run: function () { pickTender('Cash'); } },
		{ k: 'F10', ar: 'دفع بالشبكة', en: 'Tender card', run: function () { pickTender('Card'); } },
		{ k: 'Shift+F9', ar: 'دفع مُقسَّم', en: 'Split tender', run: function () { pickTender('Split'); } },
		{ k: 'Ctrl+Enter', ar: 'تحصيل', en: 'Take payment', run: function () { submitForm('pay'); } },
		{ k: '↑ / ↓', ar: 'اختيار سطر من السلة', en: 'Select a basket line', run: null },
		{ k: '+', ar: 'زيادة كمية السطر', en: 'Increase the line quantity', run: null },
		{ k: '−', ar: 'إنقاص كمية السطر', en: 'Decrease the line quantity', run: null },
		{ k: 'Delete', ar: 'حذف السطر المختار', en: 'Remove the selected line', run: null },
		{ k: 'Alt+1…8', ar: 'المفاتيح السريعة للأصناف', en: 'Item quick keys', run: null },
		{ k: 'Esc', ar: 'إفراغ الحقل / إغلاق النافذة', en: 'Clear the field / close the sheet', run: null }
	];

	function go(url) { window.location.href = url; }

	/* ---- the help sheet, built from the same table the keys run from ---- */
	var help = null;
	function toggleHelp() {
		if (help && help.parentNode) { help.parentNode.removeChild(help); help = null; focusScan(); return; }
		help = document.createElement('div');
		help.className = 'lane-help';
		help.innerHTML =
			'<div class="lane-help-card" role="dialog" aria-modal="true">' +
				'<div class="lane-help-head">' +
					'<span>' + L('اختصارات لوحة المفاتيح', 'Keyboard shortcuts') + '</span>' +
					'<button type="button" class="lane-help-x" aria-label="' + L('إغلاق', 'Close') + '">&times;</button>' +
				'</div>' +
				'<div class="lane-help-body">' +
					MAP.map(function (m) {
						return '<div class="lane-help-row"><kbd>' + m.k + '</kbd><span>' + L(m.ar, m.en) + '</span></div>';
					}).join('') +
				'</div>' +
				'<div class="lane-help-foot">' + L('يمكن تشغيل الممرّ كاملاً بلوحة المفاتيح وحدها، أو باللمس وحده.',
					'The lane can be run entirely by keyboard, or entirely by touch.') + '</div>' +
			'</div>';
		document.body.appendChild(help);
		var x = $('.lane-help-x', help);
		if (x) x.onclick = toggleHelp;
		help.addEventListener('click', function (e) { if (e.target === help) toggleHelp(); });
		x && x.focus();
	}

	/* ---- the always-visible strip, so the keys are discoverable ---- */
	function buildStrip() {
		if ($('.lane-keysbar')) return;
		var bar = document.createElement('div');
		bar.className = 'lane-keysbar';
		var show = ['F2', 'F3', 'F6', 'F8', 'F9', 'F10', 'Ctrl+Enter', 'F1'];
		// The age key only earns a place on the strip when a restricted basket is actually open -
		// a bar crowded with keys that do nothing is a bar the cashier stops reading.
		if (gate()) show.splice(6, 0, 'Shift+F7');
		bar.innerHTML = MAP.filter(function (m) { return show.indexOf(m.k) >= 0; }).map(function (m) {
			return '<button type="button" class="lane-keysbar-item" data-run="' + m.k + '"><kbd>' + m.k + '</kbd><span>' + L(m.ar, m.en) + '</span></button>';
		}).join('');
		document.body.appendChild(bar);
		// the strip is also TOUCH: every hint is a real button, so a tablet user needs no keyboard
		$$('.lane-keysbar-item', bar).forEach(function (b) {
			b.onclick = function () {
				var m = MAP.filter(function (x) { return x.k === b.getAttribute('data-run'); })[0];
				if (m && m.run) m.run();
			};
		});
	}

	/* ---- the handler ---- */
	function onKey(e) {
		var k = e.key, tag = (e.target.tagName || '').toLowerCase();
		var typing = (tag === 'input' || tag === 'textarea' || tag === 'select');
		var inScan = typing && e.target.name === 'barcode';

		if (k === 'Escape') {
			if (help) { toggleHelp(); e.preventDefault(); return; }
			if (typing) { e.target.value = ''; }
			focusScan(); e.preventDefault(); return;
		}
		if (k === 'F1' || (k === '?' && !typing)) { e.preventDefault(); toggleHelp(); return; }
		if (help) { if (k !== 'Tab') e.preventDefault(); return; }

		var combo = (e.ctrlKey ? 'Ctrl+' : '') + (e.shiftKey && k.indexOf('F') === 0 ? 'Shift+' : '') + k;
		var hit = MAP.filter(function (m) { return m.k === combo && m.run; })[0];
		if (hit) { e.preventDefault(); hit.run(); return; }

		if (e.altKey && /^[1-8]$/.test(k)) {
			e.preventDefault();
			var keys = $$('[data-quickkey]');
			var b = keys[parseInt(k, 10) - 1];
			if (b) b.click(); else flash(L('لا يوجد مفتاح سريع بهذا الرقم', 'There is no quick key with that number'));
			return;
		}

		/* line editing: only when the scan field is not mid-barcode */
		if (!typing || (inScan && !e.target.value)) {
			if (k === 'ArrowDown') { e.preventDefault(); move(1); return; }
			if (k === 'ArrowUp') { e.preventDefault(); move(-1); return; }
			if (k === '+' || k === '=') { e.preventDefault(); rowAction('line/qty', function (q) { return q + 1; }); return; }
			if (k === '-' || k === '_') { e.preventDefault(); rowAction('line/qty', function (q) { return Math.max(0, q - 1); }); return; }
			if (k === 'Delete') { e.preventDefault(); rowAction('line/remove'); return; }
		}

		/* a bare digit anywhere belongs to the scanner */
		if (!typing && /^[0-9]$/.test(k)) { focusScan(); }
	}

	/* ---- the quantity steppers: the TOUCH half of the same action the +/- keys run.
	   They adjust the qty field the existing form already posts, so the server contract
	   is untouched and the two input methods cannot drift apart. ---- */
	function wireSteppers() {
		$$('[data-step]').forEach(function (b) {
			b.addEventListener('click', function () {
				var f = b.closest('form');
				var q = f && $('input[name="qty"]', f);
				if (!q) return;
				var next = (parseFloat(q.value) || 0) + parseFloat(b.getAttribute('data-step'));
				q.value = Math.max(0, next);
				f.submit();
			});
		});
	}

	function init() {
		// THIS LAYER BELONGS TO THE LANE AND NOWHERE ELSE. The script is loaded by the shared till
		// layout, so without this it also mounted on the return and receipt screens - where F8 read
		// "Void the sale" over a page that has no sale, and every key on the strip did nothing. A
		// shortcut that lies is worse than no shortcut, so the whole layer stands down unless the
		// lane's own scan field is on the page.
		if (!document.querySelector('input[name="barcode"]')) return;
		// the strip is pinned to the bottom, so the page needs room under it or the last
		// basket row sits behind it
		document.body.classList.add('has-lane-keysbar');
		wireSteppers();
		wireGate();
		buildStrip();
		focusScan();
		document.addEventListener('keydown', onKey, true);
		// focus comes home after any click too, so a touch user's next scan still lands
		document.addEventListener('click', function (e) {
			var t = e.target;
			if (t.closest && (t.closest('input') || t.closest('select') || t.closest('.lane-help'))) return;
			setTimeout(focusScan, 0);
		});
	}

	if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
	else init();
})();
