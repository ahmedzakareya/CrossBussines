// HOSPITAL H270 — the coded allergy list (Views/Hospital/_AllergyPicker.cshtml). Each row: the allergen (from the
// catalogue, or typed — "not in the list"), the reaction, the severity. "No known allergy" stands alone. The list is
// kept as JSON in the hidden field the form sends; the server checks it again (HospitalAllergenCatalog.Refusal).
// select2's change only reaches jQuery, so the allergen select is listened to with jQuery's .on.
(function () {
    'use strict';
    function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }

    function init(root) {
        (root || document).querySelectorAll('[data-allergy-picker]').forEach(function (box) {
            if (box.__ap) return; box.__ap = true;
            var catalog = JSON.parse(box.getAttribute('data-catalog') || '[]'), W = JSON.parse(box.getAttribute('data-words') || '{}');
            var hidden = box.querySelector('[data-ap-value]'), rowsEl = box.querySelector('[data-ap-rows]'), nka = box.querySelector('[data-ap-nka]'), add = box.querySelector('[data-ap-add]');
            var items = []; try { items = JSON.parse(hidden.value || '[]') || []; } catch (e) { items = []; }
            var byCode = {}; catalog.forEach(function (c) { byCode[c.code] = c; });
            if (items.length === 1 && items[0].code === 'NKA') { nka.checked = true; items = []; }

            function sync() {
                var out = nka.checked ? [{ code: 'NKA' }] : items.filter(function (i) { return i.code || (i.text || '').trim(); })
                    .map(function (i) { return { code: i.code || null, text: i.code ? null : (i.text || '').trim(), reaction: i.reaction || null, severity: i.severity || null }; });
                hidden.value = JSON.stringify(out);
            }
            function options(list, chosen) {
                return '<option value=""></option>' + list.map(function (o) { return '<option value="' + esc(o.code) + '"' + (o.code === chosen ? ' selected' : '') + '>' + esc(o.label) + '</option>'; }).join('');
            }
            function allergenOptions(i) {
                var groups = {}; catalog.forEach(function (c) { (groups[c.kind] = groups[c.kind] || []).push(c); });
                var h = '<option value=""></option>';
                Object.keys(groups).forEach(function (k) {
                    h += '<optgroup label="' + esc((W.groups || {})[k] || k) + '">' + groups[k].map(function (c) { return '<option value="c:' + esc(c.code) + '"' + (i.code === c.code ? ' selected' : '') + '>' + esc(c.label) + '</option>'; }).join('') + '</optgroup>';
                });
                if (!i.code && i.text) h += '<option value="t:' + esc(i.text) + '" selected>' + esc(i.text) + '</option>';
                return h;
            }
            function draw() {
                add.classList.toggle('d-none', nka.checked);
                if (nka.checked) { rowsEl.innerHTML = '<div class="notice d-flex bg-light-primary rounded border-primary border border-dashed p-3 mb-2 fs-7 fw-semibold text-gray-700"><i class="ki-outline ki-shield-tick fs-4 text-primary me-2"></i>' + esc(W.nkaOn) + '</div>'; sync(); return; }
                if (!items.length) { rowsEl.innerHTML = '<div class="text-muted fs-7 mb-2">' + esc(W.none) + '</div>'; sync(); return; }
                rowsEl.innerHTML = items.map(function (i, n) {
                    return '<div class="row g-2 align-items-start mb-3 mx-0 p-2 rounded border border-dashed border-gray-300" data-ap-row="' + n + '">' +
                        '<div class="col-12"><select class="form-select form-select-sm form-select-solid" data-ap-what data-placeholder="' + esc(W.choose) + '">' + allergenOptions(i) + '</select>' +
                        (!i.code && i.text ? '<span class="badge badge-light-info fs-9 fw-bold mt-1">' + esc(W.notCoded) + '</span>' : '') + '</div>' +
                        '<div class="col"><select class="form-select form-select-sm form-select-solid" data-ap-reaction title="' + esc(W.reaction) + '" data-placeholder="' + esc(W.reaction) + '">' + options(W.reactions || [], i.reaction).replace('<option value=""></option>', '<option value="">' + esc(W.reaction) + '</option>') + '</select></div>' +
                        '<div class="col"><select class="form-select form-select-sm form-select-solid" data-ap-severity title="' + esc(W.severity) + '">' + options(W.severities || [], i.severity).replace('<option value=""></option>', '<option value="">' + esc(W.severity) + '</option>') + '</select></div>' +
                        '<div class="col-auto"><button type="button" class="btn btn-icon btn-sm btn-bg-light btn-active-color-danger" data-ap-remove="' + n + '" title="' + esc(W.remove) + '"><i class="ki-outline ki-trash fs-4"></i></button></div></div>';
                }).join('');
                if (window.jQuery && jQuery.fn.select2) jQuery(rowsEl).find('[data-ap-what]').each(function () {
                    var $s = jQuery(this), modal = box.closest('.modal');
                    $s.select2({ tags: true, width: '100%', placeholder: $s.data('placeholder'), allowClear: true, dir: document.documentElement.dir || 'ltr', dropdownParent: modal ? jQuery(modal) : jQuery(document.body),
                        createTag: function (p) { var t = (p.term || '').trim(); return t.length < 2 ? null : { id: 't:' + t, text: t, newTag: true }; },
                        // the typed text goes LAST: Enter picks the list's own entry ("بنس" → بنسلين), a new word only when nothing matches
                        insertTag: function (data, tag) { data.push(tag); } })
                      .on('change', function () {
                        var i = items[+$s.closest('[data-ap-row]').attr('data-ap-row')], v = $s.val() || '';
                        if (/^c:/.test(v)) { i.code = v.slice(2); i.text = null; } else { i.code = null; i.text = v.replace(/^t:/, ''); }
                        draw();
                      });
                });
                sync();
            }
            rowsEl.addEventListener('change', function (e) {
                var row = e.target.closest('[data-ap-row]'); if (!row) return; var i = items[+row.getAttribute('data-ap-row')];
                if (e.target.matches('[data-ap-reaction]')) i.reaction = e.target.value || null;
                if (e.target.matches('[data-ap-severity]')) i.severity = e.target.value || null;
                sync();
            });
            rowsEl.addEventListener('click', function (e) { var b = e.target.closest('[data-ap-remove]'); if (!b) return; items.splice(+b.getAttribute('data-ap-remove'), 1); draw(); });
            add.addEventListener('click', function () {
                items.push({ code: null, text: null, reaction: null, severity: null }); draw();
                var last = rowsEl.querySelectorAll('[data-ap-what]'); if (last.length && window.jQuery && jQuery.fn.select2) jQuery(last[last.length - 1]).select2('open');
            });
            nka.addEventListener('change', function () { if (nka.checked) items = []; draw(); });
            draw();
        });
    }
    window.hospAllergyPicker = { init: init };
    document.addEventListener('DOMContentLoaded', function () { init(document); });
})();
