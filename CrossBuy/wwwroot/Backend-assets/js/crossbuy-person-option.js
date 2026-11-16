/*
 * People in a select2 list are shown by face (owner rule: show the person, not a glyph). An option whose data carries
 * `photo` or `initials` (an ajax result such as /Hospital/SearchPatients) is drawn as a theme symbol, the photo or the
 * initials, beside the name and the file number; every other option keeps select2's plain text. Set as select2's
 * DEFAULT templates, so a picker that passes its own templateResult still wins, and no screen has to opt in.
 */
(function () {
    'use strict';
    var $ = window.jQuery;
    if (!$ || !$.fn || !$.fn.select2 || !$.fn.select2.defaults || window.CBPersonOption) return;

    function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
    function isPerson(d) { return d && !d.loading && (d.photo !== undefined || d.initials !== undefined); }

    function face(d, size) {
        var img = d.photo
            ? '<img src="' + esc(d.photo) + '" alt="" style="object-fit:cover" />'
            : '<span class="symbol-label fw-bold fs-9 bg-light-primary text-primary">' + esc(d.initials || '?') + '</span>';
        return '<span class="symbol symbol-' + size + 'px symbol-circle flex-shrink-0 me-3">' + img + '</span>';
    }

    function result(d) {
        if (!isPerson(d)) return d.text;
        return $('<span class="d-flex align-items-center">' + face(d, 30) +
            '<span class="d-flex flex-column lh-sm"><span class="fw-semibold">' + esc(d.name || d.text) + '</span>' +
            (d.mrn ? '<span class="text-muted fs-8"><bdi>' + esc(d.mrn) + '</bdi></span>' : '') + '</span></span>');
    }

    function selection(d) {
        if (!isPerson(d)) return d.text;
        return $('<span class="d-inline-flex align-items-center">' + face(d, 20) + '<span>' + esc(d.name || d.text) +
            (d.mrn ? ' <span class="text-muted fs-8">· <bdi>' + esc(d.mrn) + '</bdi></span>' : '') + '</span></span>');
    }

    $.fn.select2.defaults.set('templateResult', result);
    $.fn.select2.defaults.set('templateSelection', selection);
    window.CBPersonOption = { result: result, selection: selection };
})();
