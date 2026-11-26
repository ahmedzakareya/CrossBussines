// HOSPITAL H270 — the coded allergy list (Views/Hospital/_AllergyPicker.cshtml). Each row: the allergen (from the
// catalogue, or typed — "not in the list"), the reaction, the severity. "No known allergy" stands alone. The list is
// kept as JSON in the hidden field the form sends; the server checks it again (HospitalAllergenCatalog.Refusal).
// select2's change only reaches jQuery, so the allergen select is listened to with jQuery's .on.
(function () {
    'use strict';
    // the way a desk types a name: case, Arabic letter forms and the article do not matter
    function fold(s) {
        return String(s || '').toLowerCase().replace(/[ً-ٟـ]/g, '').replace(/[أإآ]/g, 'ا').replace(/ة/g, 'ه').replace(/ى/g, 'ي')
            .split(/\s+/).filter(Boolean).map(function (w) { return w.length > 3 && w.indexOf('ال') === 0 ? w.slice(2) : w; }).join(' ');
    }
    function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }

    function init(root) {
        (root || document).querySelectorAll('[data-allergy-picker]').forEach(function (box) {
            if (box.__ap) return; box.__ap = true;
            var catalog = JSON.parse(box.getAttribute('data-catalog') || '[]'), W = JSON.parse(box.getAttribute('data-words') || '{}');
            var hidden = box.querySelector('[data-ap-value]'), rowsEl = box.querySelector('[data-ap-rows]'), nka = box.querySelector('[data-ap-nka]'), add = box.querySelector('[data-ap-add]');
            var items = []; try { items = JSON.parse(hidden.value || '[]') || []; } catch (e) { items = []; }
            var byCode = {}; catalog.forEach(function (c) { byCode[c.code] = c; c.keys = (c.names || [c.label]).concat([c.label]).map(fold); });
            function entryFor(term) { var f = fold(term); return catalog.filter(function (c) { return c.keys.indexOf(f) >= 0; })[0]; }
            // select2's search, by any of an entry's names (optgroups searched through their children)
            function matcher(params, data) {
                var t = fold(params.term); if (!t) return data;
                if (data.children) { var kids = data.children.filter(function (k) { return matcher(params, k) !== null; }); if (!kids.length) return null; var g = jQuery.extend({}, data, true); g.children = kids; return g; }
                var c = /^c:/.test(data.id || '') ? byCode[data.id.slice(2)] : null;
                var keys = c ? c.keys : [fold(data.text)];
                return keys.some(function (k) { return k.indexOf(t) >= 0; }) ? data : null;
            }
            if (items.length === 1 && items[0].code === 'NKA') { nka.checked = true; items = []; }

            function sync() {
                // a row with a reaction or a severity but no allergen is SENT, so the server refuses it instead of it vanishing
                var out = nka.checked ? [{ code: 'NKA' }] : items.filter(function (i) { return i.code || (i.text || '').trim() || i.reaction || i.severity; })
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
                // H272: the states inside the section's dashed frame - asked and none, nothing yet, or the rows under their heads
                if (nka.checked) { rowsEl.innerHTML = '<div class="d-flex align-items-center gap-3 fs-6 fw-semibold text-gray-700"><i class="ki-duotone ki-shield-tick fs-2x text-primary"><span class="path1"></span><span class="path2"></span></i>' + esc(W.nkaOn) + '</div>'; sync(); return; }
                if (!items.length) { rowsEl.innerHTML = '<div class="d-flex align-items-center gap-3 mb-4 fs-7 text-gray-600"><i class="ki-duotone ki-information-5 fs-2 text-gray-500"><span class="path1"></span><span class="path2"></span><span class="path3"></span></i>' + esc(W.none) + '</div>'; sync(); return; }
                var label = function (t) { return '<label class="fw-semibold fs-7 text-gray-600 mb-2 d-md-none">' + esc(t) + '</label>'; };
                rowsEl.innerHTML = '<div class="row g-4 mb-2 d-none d-md-flex fw-semibold fs-7 text-gray-600"><div class="col-md-6">' + esc(W.allergen || '') + '</div><div class="col-md-3">' + esc(W.reaction) + '</div><div class="col">' + esc(W.severity) + '</div><div class="col-auto"><span class="d-inline-block w-40px"></span></div></div>' +
                  items.map(function (i, n) {
                    return '<div class="row g-4 align-items-start mb-4" data-ap-row="' + n + '">' +
                        '<div class="col-md-6">' + label(W.allergen || '') + '<select class="form-select form-select-solid" data-ap-what data-placeholder="' + esc(W.choose) + '">' + allergenOptions(i) + '</select>' +
                        (!i.code && i.text ? '<span class="badge badge-light-info fs-8 fw-bold mt-2">' + esc(W.notCoded) + '</span>' : '') + '</div>' +
                        '<div class="col-md-3">' + label(W.reaction) + '<select class="form-select form-select-solid" data-ap-reaction title="' + esc(W.reaction) + '" data-placeholder="' + esc(W.reaction) + '">' + options(W.reactions || [], i.reaction).replace('<option value=""></option>', '<option value="">' + esc(W.reaction) + '</option>') + '</select></div>' +
                        '<div class="col">' + label(W.severity) + '<select class="form-select form-select-solid" data-ap-severity title="' + esc(W.severity) + '">' + options(W.severities || [], i.severity).replace('<option value=""></option>', '<option value="">' + esc(W.severity) + '</option>') + '</select></div>' +
                        '<div class="col-auto"><button type="button" class="btn btn-icon btn-light btn-active-light-danger w-40px h-40px" data-ap-remove="' + n + '" title="' + esc(W.remove) + '"><i class="ki-outline ki-trash fs-3"></i></button></div></div>';
                }).join('');
                if (window.jQuery && jQuery.fn.select2) jQuery(rowsEl).find('[data-ap-what]').each(function () {
                    var $s = jQuery(this), modal = box.closest('.modal');
                    $s.select2({ tags: true, width: '100%', placeholder: $s.data('placeholder'), allowClear: true, dir: document.documentElement.dir || 'ltr', dropdownParent: modal ? jQuery(modal) : jQuery(document.body),
                        matcher: matcher,
                        createTag: function (p) { var t = (p.term || '').trim(); if (t.length < 2 || entryFor(t)) return null; return { id: 't:' + t, text: t, newTag: true }; },
                        // the typed text goes LAST: Enter picks the list's own entry ("بنس" → بنسلين), a new word only when nothing matches
                        insertTag: function (data, tag) { data.push(tag); } })
                      // typed and left without Enter (a click elsewhere): the words are kept — the list's own entry when it is
                      // exactly that name, otherwise as an allergy not in the list
                      .on('select2:closing', function () {
                        var term = (jQuery('.select2-container--open .select2-search__field').val() || '').trim();
                        if (term.length < 2 || $s.val()) return;
                        var same = entryFor(term);
                        var v = same ? 'c:' + same.code : 't:' + term;
                        if (!same && !$s.find('option').filter(function () { return this.value === v; }).length) $s.append(new Option(term, v, false, false));
                        setTimeout(function () { $s.val(v).trigger('change'); }, 0);
                      })
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
