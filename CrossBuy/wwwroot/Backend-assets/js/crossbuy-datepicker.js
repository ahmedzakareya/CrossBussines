/* ============================================================
   crossbuy-datepicker.js — what every flatpickr in the product shares,
   whichever layout's enhancer created it (owner 2026-11-25: the
   patient-registration birth-date calendar was "not on the identity").

   1. Arabic screens get Arabic month and day names. They come from the
      browser's own Arabic calendar data (Intl), not from strings typed here,
      so they are standard Arabic and nothing needs translating.
   2. Every picker field carries the calendar icon at its end edge (the
      back-office enhancer already drew one; the rest did not).
   3. A birth-date field (name/id containing "birth" or "dob") cannot pick a
      day after today.
   4. The month is the theme's static name between the arrows, not the
      browser's drop-down list; the year is typed or stepped.

   Loaded right after plugins.bundle.js and before any layout enhancer runs
   (they run on DOMContentLoaded), so flatpickr.localize() is in place before
   the first instance is built. Colours live in crossbuy-brand.css.
   ============================================================ */
(function (w, d) {
    var fp = w.flatpickr;
    if (!fp) return;

    var html = d.documentElement;
    var isAr = (html.getAttribute('lang') || '').toLowerCase().indexOf('ar') === 0 || html.getAttribute('dir') === 'rtl';

    if (isAr && w.Intl && Intl.DateTimeFormat) {
        try {
            var months = [], monthsShort = [], days = [], daysShort = [];
            var mFmt = new Intl.DateTimeFormat('ar', { month: 'long', timeZone: 'UTC' });
            for (var m = 0; m < 12; m++) { var mn = mFmt.format(new Date(Date.UTC(2024, m, 15))); months.push(mn); monthsShort.push(mn); }
            var dLong = new Intl.DateTimeFormat('ar', { weekday: 'long', timeZone: 'UTC' });
            // 2024-01-07 is a Sunday: flatpickr's weekday arrays start on Sunday. The head row uses the one-letter
            // names (ح ن ث ر خ ج س) - full names ran into each other in the 36px cells.
            // H272: the browser's "narrow" Arabic names are short WORDS (أحد، أربعاء...) that still ran together; the head
            // row takes the standard one-letter names, Sunday first.
            var letters = ['ح', 'ن', 'ث', 'ر', 'خ', 'ج', 'س'];
            for (var k = 0; k < 7; k++) {
                var day = new Date(Date.UTC(2024, 0, 7 + k));
                days.push(dLong.format(day));
                daysShort.push(letters[k]);
            }
            fp.localize({
                weekdays: { shorthand: daysShort, longhand: days },
                months: { shorthand: monthsShort, longhand: months },
                firstDayOfWeek: 6,
                rangeSeparator: ' — ',
                time_24hr: true
            });
            // a screen that asks for flatpickr's own Arabic locale by name gets the same head row
            if (fp.l10ns && fp.l10ns.ar && fp.l10ns.ar.weekdays) fp.l10ns.ar.weekdays.shorthand = daysShort.slice();
        } catch (e) { /* the English names stay; the picker still works */ }
    }

    function isBirth(el) { return /birth|dob/i.test((el.getAttribute('name') || '') + ' ' + (el.id || '')); }

    function addIcon(inst) {
        var el = inst.altInput || inst.input;
        if (!el || !el.parentNode || el.type === 'hidden') return;
        if (el.closest('.cb-date-wrap') || el.closest('.input-group') || el.hasAttribute('data-no-date-icon')) return;   // one icon, never inside an input-group
        if (inst.config.noCalendar) return;                                                    // a time-only field is not a calendar
        var wrap = d.createElement('div');
        wrap.className = 'cb-date-wrap position-relative';
        el.parentNode.insertBefore(wrap, el);
        wrap.appendChild(el);
        var ic = d.createElement('i');
        ic.className = 'ki-outline ki-calendar-8 fs-3 text-gray-500';
        // H272: the icon and the room kept for it sit on the SAME physical side. A date typed left-to-right (dir=ltr) on
        // an Arabic page had its room on the right while the icon sat on the left, over the digits.
        var side = w.getComputedStyle(wrap).direction === 'rtl' ? 'left' : 'right';
        ic.style.cssText = 'position:absolute;top:50%;' + side + ':1rem;transform:translateY(-50%);cursor:pointer;z-index:1;';
        ic.addEventListener('click', function () { inst.open(); });
        wrap.appendChild(ic);
        el.style[side === 'left' ? 'paddingLeft' : 'paddingRight'] = '2.75rem';
    }

    // 4. H272: the month is shown as the theme draws it (its name between the arrows), never the browser's own
    //    drop-down list - the owner: «الشكل دا مروع في الكالندر».
    fp.defaultConfig.monthSelectorType = 'static';

    var prev = fp.defaultConfig.onReady;
    var hooks = Array.isArray(prev) ? prev.slice() : (prev ? [prev] : []);
    hooks.push(function (_sel, _str, inst) {
        try {
            if (isBirth(inst.input) && !inst.config.maxDate) inst.set('maxDate', 'today');
            addIcon(inst);
        } catch (e) { }
    });
    fp.defaultConfig.onReady = hooks;
})(window, document);
