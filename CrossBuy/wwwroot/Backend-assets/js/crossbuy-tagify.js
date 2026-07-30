/* ============================================================
   CrossBuy shared Tagify search field.
   Turns a plain <input> into a multi-tag search box (Metronic's
   bundled Tagify). Each tag = one search term; OR semantics on
   the server. Optional DB-backed autocomplete via suggestUrl.

   Usage:
     var s = cbTagify(inputEl, {
        suggestUrl: '/Inventory/ItemsSuggest', // optional
        placeholder: 'ابحث…',
        onChange: function(terms){ ... }        // terms = "a|b|c"
     });
     s.terms()  -> current value as "a|b|c"
     s.clear()  -> remove all tags (no onChange fired)
   ============================================================ */
(function (w) {
    function cbTagify(input, opts) {
        opts = opts || {};
        if (!input || typeof Tagify === 'undefined') {
            // graceful fallback: behave like a normal input
            return {
                terms: function () { return (input && input.value || '').trim(); },
                clear: function () { if (input) input.value = ''; }
            };
        }
        // placeholder MUST come from the input's HTML attribute (correctly encoded by Razor),
        // never from a JS string literal (Arabic in inline <script> gets HTML-entity-encoded).
        var placeholder = opts.placeholder || input.getAttribute('placeholder') || '';

        var tagify = new Tagify(input, {
            placeholder: placeholder,
            delimiters: null,                 // we control add via Enter / suggestion click
            enforceWhitelist: false,          // free typing allowed too
            dropdown: { enabled: 1, maxItems: 10, closeOnSelect: true, highlightFirst: true, searchKeys: ['value', 'name'] },
            templates: {
                dropdownItem: function (item) {
                    return '<div ' + this.getAttributes(item) + ' class="tagify__dropdown__item" tabindex="0" role="option">' +
                        '<strong>' + (item.value || '') + '</strong>' +
                        (item.name ? '<span class="text-muted ms-2">' + item.name + '</span>' : '') +
                        '</div>';
                }
            }
        });

        function fire() { if (typeof opts.onChange === 'function') opts.onChange(terms()); }
        function terms() { return tagify.value.map(function (t) { return t.value; }).join('|'); }

        tagify.on('add', fire);
        tagify.on('remove', fire);

        // async autocomplete
        if (opts.suggestUrl) {
            var timer = null;
            tagify.on('input', function (e) {
                var val = e.detail.value;
                tagify.whitelist = null;
                clearTimeout(timer);
                timer = setTimeout(function () {
                    tagify.loading(true);
                    fetch(opts.suggestUrl + '?term=' + encodeURIComponent(val), { headers: { 'X-Requested-With': 'fetch' } })
                        .then(function (r) { return r.json(); })
                        .then(function (data) {
                            tagify.whitelist = Array.isArray(data) ? data : [];
                            tagify.loading(false).dropdown.show(val);
                        })
                        .catch(function () { tagify.loading(false); });
                }, 250);
            });
        }

        return {
            tagify: tagify,
            terms: terms,
            clear: function () { tagify.removeAllTags(); }
        };
    }
    w.cbTagify = cbTagify;

    /* ------------------------------------------------------------
       cbServerList — wires a standard server-side list page.
       Conventions (element IDs the shell must use):
         #f_q (Tagify search) · #f_size (page size) · #f_reset (reset)
         #rows (tbody) · #pager · #pageInfo · #resultCount
         #loadingRow · #emptyRow · #grandValue (optional)
         any filter <select> tagged class="cb-filter" auto-reloads
       opts: { url, suggestUrl?, isAr, addParams?(URLSearchParams), onReset?() }
       returns { reload, search }
    ------------------------------------------------------------ */
    function cbServerList(opts) {
        opts = opts || {};
        var isAr = !!opts.isAr;
        var body = document.getElementById('rows');
        var loading = document.getElementById('loadingRow'), empty = document.getElementById('emptyRow');
        var pager = document.getElementById('pager'), pageInfo = document.getElementById('pageInfo');
        var resultCount = document.getElementById('resultCount'), grand = document.getElementById('grandValue');
        var size = document.getElementById('f_size');
        var page = 1, pages = 1, total = 0;
        var qel = document.getElementById('f_q');
        var search = qel ? cbTagify(qel, { suggestUrl: opts.suggestUrl, onChange: function () { reload(); } })
                         : { terms: function () { return ''; }, clear: function () {} };

        function params() {
            var p = new URLSearchParams();
            var t = search.terms(); if (t) p.set('q', t);
            if (typeof opts.addParams === 'function') opts.addParams(p);
            p.set('page', page); p.set('pageSize', (size && size.value) || '25');
            return p.toString();
        }
        function load() {
            if (loading) loading.classList.remove('d-none');
            if (empty) empty.classList.add('d-none');
            if (body) body.innerHTML = '';
            fetch(opts.url + '?' + params(), { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' })
                .then(function (r) {
                    total = parseInt(r.headers.get('X-Total') || '0');
                    page = parseInt(r.headers.get('X-Page') || '1');
                    pages = parseInt(r.headers.get('X-Pages') || '1');
                    if (grand) { var gv = parseFloat(r.headers.get('X-GrandValue') || '0'); grand.textContent = gv.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }); }
                    return r.text();
                })
                .then(function (html) {
                    if (loading) loading.classList.add('d-none');
                    if (body) body.innerHTML = html;
                    if (empty) empty.classList.toggle('d-none', total !== 0);
                    if (resultCount) resultCount.textContent = isAr ? ('النتائج: ' + total.toLocaleString()) : (total.toLocaleString() + ' results');
                    renderPager();
                })
                .catch(function () { if (loading) loading.classList.add('d-none'); if (empty) empty.classList.remove('d-none'); });
        }
        function pageInfoText() {
            if (!pageInfo) return;
            if (total === 0) { pageInfo.textContent = ''; return; }
            var ps = parseInt((size && size.value) || '25'); var from = (page - 1) * ps + 1; var to = Math.min(page * ps, total);
            pageInfo.textContent = isAr ? (from + '–' + to + ' من ' + total.toLocaleString()) : (from + '–' + to + ' of ' + total.toLocaleString());
        }
        function btn(label, p, disabled, active) {
            var li = document.createElement('li'); li.className = 'page-item' + (disabled ? ' disabled' : '') + (active ? ' active' : '');
            var a = document.createElement('a'); a.className = 'page-link'; a.href = '#'; a.innerHTML = label;
            if (!disabled && !active) a.addEventListener('click', function (e) { e.preventDefault(); page = p; load(); window.scrollTo({ top: 0, behavior: 'smooth' }); });
            li.appendChild(a); return li;
        }
        function renderPager() {
            pageInfoText(); if (!pager) return; pager.innerHTML = '';
            if (pages <= 1) return;
            pager.appendChild(btn('<i class="ki-outline ki-' + (isAr ? 'right' : 'left') + '"></i>', page - 1, page <= 1, false));
            var start = Math.max(1, page - 2), end = Math.min(pages, start + 4); start = Math.max(1, end - 4);
            if (start > 1) { pager.appendChild(btn('1', 1, false, page === 1)); if (start > 2) pager.appendChild(btn('…', 0, true, false)); }
            for (var i = start; i <= end; i++) pager.appendChild(btn(String(i), i, false, i === page));
            if (end < pages) { if (end < pages - 1) pager.appendChild(btn('…', 0, true, false)); pager.appendChild(btn(String(pages), pages, false, page === pages)); }
            pager.appendChild(btn('<i class="ki-outline ki-' + (isAr ? 'left' : 'right') + '"></i>', page + 1, page >= pages, false));
        }
        function reload() { page = 1; load(); }
        function wireChange(el) { if (!el) return; if (window.jQuery && jQuery(el).data('select2')) jQuery(el).on('change', reload); else el.addEventListener('change', reload); }
        wireChange(size);
        Array.prototype.forEach.call(document.querySelectorAll('.cb-filter'), wireChange);
        // auto-inject an "Export Excel" button (uses the current filters/search) when an exportUrl is given
        if (opts.exportUrl) {
            var xbtn = document.createElement('button');
            xbtn.type = 'button'; xbtn.className = 'btn btn-sm btn-light-success text-nowrap flex-shrink-0';
            xbtn.innerHTML = '<i class="ki-outline ki-file-down fs-4"></i>' + (isAr ? 'تصدير Excel' : 'Export Excel');
            xbtn.addEventListener('click', function () { window.location = opts.exportUrl + '?' + params(); });
            if (size && size.parentNode) {
                // wrap the export button + page-size select in a FRESH flex row so they always sit side by side
                // (a brand-new <div> can't be overridden by any .card-toolbar rule that breaks the row)
                var grp = document.createElement('div');
                grp.className = 'd-flex align-items-center gap-2';
                size.parentNode.insertBefore(grp, size);
                grp.appendChild(xbtn);
                grp.appendChild(size);
            } else if (pager && pager.parentNode) {
                pager.parentNode.insertBefore(xbtn, pager);
            }
        }
        var reset = document.getElementById('f_reset');
        if (reset) reset.addEventListener('click', function () {
            search.clear();
            Array.prototype.forEach.call(document.querySelectorAll('.cb-filter'), function (el) { el.value = ''; if (window.jQuery && jQuery(el).data('select2')) jQuery(el).val('').trigger('change.select2'); });
            if (typeof opts.onReset === 'function') opts.onReset();
            reload();
        });
        load();
        return { reload: reload, search: search };
    }
    w.cbServerList = cbServerList;

    /* ------------------------------------------------------------
       cbItemSelect — turn a line-item <select> into a server-side
       (select2-ajax) item picker so forms never preload the whole
       catalog. On pick it stamps data-price/data-base/data-name onto
       the chosen <option>, so each form's EXISTING change handler
       (which reads those attributes) keeps working unchanged.
    ------------------------------------------------------------ */
    function cbItemSelect(sel, url, opts) {
        if (!sel || !window.jQuery || !jQuery.fn || typeof jQuery.fn.select2 === 'undefined') return;
        opts = opts || {};
        var rtl = document.documentElement.getAttribute('dir') === 'rtl';
        var $sel = jQuery(sel);
        $sel.select2({
            placeholder: opts.placeholder || (rtl ? 'ابحث عن صنف…' : 'Search item…'),
            allowClear: true,
            minimumInputLength: 1,
            dropdownParent: opts.dropdownParent || undefined,
            ajax: {
                url: url, dataType: 'json', delay: 250,
                data: function (p) { return { term: p.term }; },
                processResults: function (d) { return { results: d.results }; }
            }
        });
        $sel.on('select2:select', function (e) {
            var d = e.params.data, opt = sel.querySelector('option[value="' + d.id + '"]');
            if (opt) {
                opt.setAttribute('data-price', d.price != null ? d.price : 0);
                opt.setAttribute('data-cost', d.cost != null ? d.cost : 0);
                opt.setAttribute('data-base', d.baseUom != null ? d.baseUom : '');
                opt.setAttribute('data-name', d.name || '');
                opt.setAttribute('data-nameen', d.nameEn || '');
            }
        });
        return $sel;
    }
    // programmatically pick an item (barcode scan): inject a stamped option + fire change
    function cbItemSelectSet(sel, item) {
        if (!sel || !window.jQuery) return;
        var label = (item.code ? item.code + ' — ' : '') + (item.name || ('#' + item.id));
        var opt = new Option(label, item.id, true, true);
        opt.setAttribute('data-price', item.price != null ? item.price : 0);
        opt.setAttribute('data-cost', item.cost != null ? item.cost : 0);
        opt.setAttribute('data-base', item.baseUom != null ? item.baseUom : '');
        opt.setAttribute('data-name', item.name || '');
        opt.setAttribute('data-nameen', item.nameEn || '');
        jQuery(sel).append(opt).trigger('change');
    }
    w.cbItemSelect = cbItemSelect;
    w.cbItemSelectSet = cbItemSelectSet;
})(window);
