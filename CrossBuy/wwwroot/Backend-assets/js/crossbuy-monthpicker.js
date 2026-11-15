/*
 * The theme's month picker. Every <input type="month"> becomes a field that opens a calendar in flatpickr's own markup
 * and classes (the theme calendar), showing a year with twelve months — never the browser's native month popup
 * (owner rule 2026-11-12: theme controls only). The bundled flatpickr 4.6.13 has no monthSelect plugin, hence this file.
 *
 * Contract kept for the screens: the ORIGINAL input stays in the DOM as type=hidden, so its name posts with the form,
 * .value still reads/writes "YYYY-MM", min/max are honoured, and a pick dispatches 'input' + 'change' on it (bubbling) —
 * existing listeners keep working untouched. A visible read-only field beside it shows the month by name ("نوفمبر 2026").
 * Opt out with data-no-flatpickr. Fields added later (fetch + innerHTML, modals) are picked up by an observer.
 */
(function () {
    'use strict';
    if (window.CBMonthPicker) return;

    var lang = (document.documentElement.getAttribute('lang') || 'en').slice(0, 2);
    var rtl = document.documentElement.getAttribute('dir') === 'rtl';
    var loc = lang + '-u-nu-latn';
    function monthNames(style) {
        var out = [];
        for (var i = 0; i < 12; i++) {
            try { out.push(new Intl.DateTimeFormat(loc, { month: style }).format(new Date(2000, i, 1))); }
            catch (e) { out.push(String(i + 1)); }
        }
        return out;
    }
    var LONG = monthNames('long'), SHORT = monthNames('short');
    var PREV = '<svg version="1.1" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 17 17"><g></g><path d="M5.207 8.471l7.146 7.147-0.707 0.707-7.853-7.854 7.854-7.853 0.707 0.707-7.147 7.146z"></path></svg>';
    var NEXT = '<svg version="1.1" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 17 17"><g></g><path d="M13.207 8.472l-7.854 7.854-0.707-0.707 7.146-7.146-7.146-7.148 0.707-0.707 7.854 7.854z"></path></svg>';
    var nativeValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');

    function parse(v) { var m = /^(\d{4})-(\d{1,2})$/.exec(v || ''); return m ? { y: +m[1], m: +m[2] } : null; }
    function label(p) { return p ? LONG[p.m - 1] + ' ' + p.y : ''; }
    function pad(n) { return (n < 10 ? '0' : '') + n; }
    function key(y, m) { return y * 12 + m; }

    (function css() {
        var s = document.createElement('style');
        s.textContent =
            '.cb-month-cal{z-index:1100;width:290px;padding-bottom:.25rem}' +
            '.cb-month-cal .cb-months{display:flex;align-items:center;justify-content:space-between;direction:ltr;height:44px;padding:0 .35rem}' +
            '.cb-month-cal .cb-year{font-size:1.05rem;font-weight:600;color:var(--bs-gray-800);direction:ltr;unicode-bidi:isolate}' +
            '.cb-month-cal .cb-nav{display:inline-flex;align-items:center;justify-content:center;width:34px;height:34px;border-radius:.475rem;cursor:pointer}' +
            '.cb-month-cal .cb-nav svg{width:14px;height:14px;fill:var(--bs-gray-500)}' +
            '.cb-month-cal .cb-nav:hover{background:var(--bs-gray-100)}.cb-month-cal .cb-nav:hover svg{fill:var(--bs-primary)}' +
            '.cb-month-cal .cb-nav.flatpickr-disabled{opacity:.3;pointer-events:none}' +
            '.cb-month-cal .cb-month-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:.4rem;padding:.75rem .85rem .6rem}' +
            '.cb-month-cal .cb-month{max-width:none;width:auto;height:40px;line-height:40px;margin:0;flex:none;border-radius:.475rem;cursor:pointer}' +
            '.cb-month-field{cursor:pointer;background-image:none}';
        document.head.appendChild(s);
    })();

    var open = null;
    function close() { if (open) { open.cal.remove(); open = null; } }

    function show(el, shown) {
        close();
        var now = new Date();
        var start = parse(el.value) || { y: now.getFullYear(), m: now.getMonth() + 1 };
        var min = parse(el.getAttribute('min')), max = parse(el.getAttribute('max'));
        var year = start.y;
        var cal = document.createElement('div');
        cal.className = 'flatpickr-calendar animate open arrowTop cb-month-cal';
        var modal = shown.closest('.modal-content');
        (modal || document.body).appendChild(cal);

        function draw() {
            var sel = parse(el.value);
            // The header is laid out explicitly: the arrow on the reading-start side goes back a year (right in Arabic),
            // each arrow points outward, and the grid flows in the page's direction (January first on the right in Arabic).
            var backOff = min && key(year - 1, 12) < key(min.y, min.m), fwdOff = max && key(year + 1, 1) > key(max.y, max.m);
            function nav(d, svg) { var off = d < 0 ? backOff : fwdOff; return '<span class="cb-nav' + (off ? ' flatpickr-disabled' : '') + '" data-d="' + d + '">' + svg + '</span>'; }
            var h = '<div class="flatpickr-months cb-months">' + nav(rtl ? 1 : -1, PREV) +
                '<span class="cb-year">' + year + '</span>' + nav(rtl ? -1 : 1, NEXT) + '</div>' +
                '<div class="cb-month-grid" dir="' + (rtl ? 'rtl' : 'ltr') + '">';
            for (var i = 1; i <= 12; i++) {
                var off = (min && key(year, i) < key(min.y, min.m)) || (max && key(year, i) > key(max.y, max.m));
                var on = sel && sel.y === year && sel.m === i;
                var today = year === now.getFullYear() && i === now.getMonth() + 1;
                h += '<span class="flatpickr-day cb-month' + (on ? ' selected' : '') + (today && !on ? ' today' : '') + (off ? ' flatpickr-disabled' : '') + '" data-m="' + i + '">' + SHORT[i - 1] + '</span>';
            }
            cal.innerHTML = h + '</div>';
        }
        function place() {
            var r = shown.getBoundingClientRect(), base = modal ? modal.getBoundingClientRect() : { top: -window.scrollY, left: -window.scrollX };
            var w = cal.offsetWidth, vw = document.documentElement.clientWidth;
            var left = rtl ? r.right - w : r.left;
            left = Math.max(8, Math.min(left, vw - w - 8));
            cal.style.top = (r.bottom - base.top + 6) + 'px';
            cal.style.left = (left - base.left) + 'px';
        }
        cal.addEventListener('mousedown', function (e) { e.preventDefault(); });
        cal.addEventListener('click', function (e) {
            e.stopPropagation();
            var t = e.target.closest('[data-d],[data-m]');
            if (!t || t.classList.contains('flatpickr-disabled')) return;
            if (t.hasAttribute('data-d')) { year += +t.getAttribute('data-d'); draw(); return; }
            el.value = year + '-' + pad(+t.getAttribute('data-m'));
            close();
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
            shown.focus();
        });
        draw(); place();
        open = { el: el, shown: shown, cal: cal };
    }

    function init(el) {
        if (el._cbMonth || el.hasAttribute('data-no-flatpickr')) return;
        el._cbMonth = true;
        var shown = document.createElement('input');
        shown.type = 'text';
        shown.readOnly = true;
        shown.className = el.className;
        shown.classList.add('form-control-solid', 'flatpickr-input', 'cb-month-field');
        shown.classList.remove('hp-num');
        if (el.style.cssText) shown.style.cssText = el.style.cssText;
        if (el.title) shown.title = el.title;
        if (el.disabled) shown.disabled = true;
        shown.setAttribute('aria-haspopup', 'dialog');
        el.setAttribute('type', 'hidden');
        el.parentNode.insertBefore(shown, el.nextSibling);
        // Keep the visible label in step with every write to the real value, scripted ones included.
        Object.defineProperty(el, 'value', {
            configurable: true,
            get: function () { return nativeValue.get.call(el); },
            set: function (v) { nativeValue.set.call(el, v); shown.value = label(parse(v)); }
        });
        shown.value = label(parse(el.value));
        shown.addEventListener('click', function (e) { e.stopPropagation(); if (open && open.el === el) close(); else show(el, shown); });
        shown.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' || e.key === ' ' || e.key === 'ArrowDown') { e.preventDefault(); show(el, shown); }
            else if (e.key === 'Escape' || e.key === 'Tab') close();
        });
    }

    function enhance(root) { (root || document).querySelectorAll('input[type="month"]').forEach(init); }

    document.addEventListener('mousedown', function (e) { if (open && !open.cal.contains(e.target) && e.target !== open.shown) close(); });
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') close(); });
    window.addEventListener('resize', close);
    window.addEventListener('scroll', function (e) { if (open && !open.cal.contains(e.target)) close(); }, true);
    document.addEventListener('shown.bs.modal', function (e) { enhance(e.target); });
    document.addEventListener('hide.bs.modal', close);

    function boot() {
        enhance(document);
        var pending = false;
        new MutationObserver(function (list) {
            if (pending) return;
            for (var i = 0; i < list.length; i++) {
                if (list[i].addedNodes.length) { pending = true; requestAnimationFrame(function () { pending = false; enhance(document); }); return; }
            }
        }).observe(document.body, { childList: true, subtree: true });
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot); else boot();

    window.CBMonthPicker = { enhance: enhance };
})();
