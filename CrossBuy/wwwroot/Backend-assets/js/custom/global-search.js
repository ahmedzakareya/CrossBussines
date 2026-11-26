// The header box. ONE field, two jobs.
//
// Typing (>= 2 chars) shows a live dropdown of grouped record hits from /Search/Suggest; above them
// sits a row that hands the same text to the assistant. Enter does whichever one the text asks for:
// a question goes to the assistant, a plain term opens /Search/Results.
//
// WHY ONE FIELD AND NOT TWO. A second box beside this one would make the person choose, before
// typing, between "find a record" and "ask a question" - a distinction they do not have in mind and
// cannot always make correctly ("أحمد" is a lookup, "كم عميل اسمه أحمد" is a question, and both
// start the same way). So the box takes either and decides afterwards, and the decision is always
// visible and always reversible: the ask row is on screen even when records matched, and the record
// hits stay on screen even when the text reads like a question.
//
// THE ASSISTANT ITSELF IS NOT REIMPLEMENTED HERE. window.cbAi.ask() is a door into the panel in
// _AiAssistant.cshtml - its conversation, its history, its refusal wording, its "how this was worked
// out" box. Answering inline would have meant a second renderer that drifts from that one.
//
// IT IS DRAWN IN THE THEME'S OWN VOCABULARY, NOT IN HAND-WRITTEN CSS.
//
// The first version of this file built its panel with an inline style string — its own border,
// radius, shadow and padding — so the dropdown was a white box that did not match anything else in
// the product, sat narrower than the input it belonged to, and listed results as bare lines. Every
// one of those decisions already exists in Metronic as `menu menu-sub menu-sub-dropdown` with
// `menu-item` / `menu-link` / `separator` inside it. Using them means the dropdown inherits the
// shell's radius, shadow, hover and dark-mode tokens for free, and keeps inheriting them when the
// theme changes. There is no inline styling below except the position, which depends on the input.
//
// The theme's own KTSearch dropdown ships full of demo content (a "Karina Clark" avatar and friends)
// and is wired to nothing, so it is taken out of the way once rather than fought with. The panel
// below is aligned to the INPUT, not to the component root, because the root includes the mobile
// toggle and aligning to it is what made the box look detached.
(function () {
  'use strict';

  function init() {
    var root = document.getElementById('kt_header_search');
    if (!root || root.dataset.cbWired) return;
    root.dataset.cbWired = '1';

    var input = root.querySelector('.search-input');
    var form = root.querySelector('form[data-kt-search-element="form"]') || root.querySelector('form');
    var content = root.querySelector('[data-kt-search-element="content"]');
    if (!input) return;

    // Retire the demo dropdown once. Removing the trigger attributes stops KTMenu reopening it, so
    // no observer has to keep pushing it back down.
    if (content) { content.remove(); }
    root.removeAttribute('data-kt-menu-trigger');
    root.removeAttribute('data-kt-menu-permanent');
    root.removeAttribute('data-kt-search');

    var isAr = (document.documentElement.getAttribute('dir') === 'rtl') ||
               (document.documentElement.lang || '').slice(0, 2) === 'ar';
    var T = {
      all:   isAr ? 'كل النتائج'      : 'All results',
      none:  isAr ? 'لا نتائج لـ'      : 'Nothing matched',
      scope: isAr ? 'ضمن صلاحيتك'      : 'within what you may view',
      more:  isAr ? 'أول النتائج فقط'  : 'first matches only',
      listOnly: isAr ? 'في القائمة' : 'in the list',
      ask:   isAr ? 'اسأل المساعد'     : 'Ask the assistant',
      records: isAr ? 'سجلات'          : 'Records',
    };

    // WHAT COUNTS AS A QUESTION — deliberately cautious, because being wrong in one direction costs
    // far more than the other. Sending a lookup to the assistant wastes a few seconds and shows the
    // wrong kind of answer; sending a question to /Search/Results shows "nothing matched" for a
    // question that has a perfectly good answer, which reads as the product not knowing.
    //
    // So: a single word is NEVER a question, however it is spelled — "أحمد", "INV-4821", "Baghdad"
    // are what people actually type. Two or more words qualify only on an explicit mark (؟ / ?) or
    // an opening interrogative. Everything else keeps today's behaviour exactly.
    var QW = ['كم','كام','ما','ماهي','ماهو','إيه','ايه','مين','من','فين','أين','اين','امتى','إمتى','متى',
              'ليه','لماذا','هل','اعرض','أعرض','عرض','اعطني','أعطني','وريني','قارن','لخص','احسب','كيف','ازاي','إزاي',
              'how','what','whats','which','who','where','when','why','is','are','do','does','did','can',
              'show','list','count','total','compare','summarise','summarize','give','top','any'];

    function isQuestion(t) {
      t = (t || '').trim();
      if (!t) return false;
      if (/[؟?]\s*$/.test(t)) return true;
      var words = t.split(/\s+/);
      if (words.length < 2) return false;
      var first = words[0].toLowerCase().replace(/[^ء-يa-z]/g, '');
      return QW.indexOf(first) >= 0;
    }

    function hasAssistant() { return !!(window.cbAi && typeof window.cbAi.ask === 'function'); }

    // THE SPINNER IS OURS NOW, because the theme's never stopped.
    //
    // KTSearch is still attached to this box (its attributes are what make the mobile toggle work),
    // and on the first keystroke it strips `d-none` off .search-spinner to say a search has started.
    // Nothing ever tells it one finished — the ajax it expects does not exist here — so the spinner
    // span kept turning for the rest of the page's life, on every screen, claiming the box was busy
    // long after results had been drawn. Driving it from OUR fetch makes it mean what it looks like.
    function spin(on) {
      var s = root.querySelector('.search-spinner');
      if (s) s.classList.toggle('d-none', !on);
    }

    function askAssistant(term) {
      term = (term || '').trim();
      if (!term || !hasAssistant()) return false;
      hide();
      var el = liveInput();
      if (el) el.blur();
      // Hand over the field itself, so the answer opens under the box that was typed in rather than
      // at the floating button in the far corner.
      window.cbAi.ask(term, el || null);
      return true;
    }

    var box = null, timer = null, lastTerm = '', activeIndex = -1;

    function liveInput() { return root.querySelector('.search-input') || input; }
    function esc(s) { var d = document.createElement('div'); d.textContent = s == null ? '' : s; return d.innerHTML; }
    function go(term) {
      term = (term || '').trim();
      if (term.length >= 2) window.location.href = '/Search/Results?q=' + encodeURIComponent(term);
    }

    function panel() {
      if (box) return box;
      box = document.createElement('div');
      // The theme's dropdown, verbatim. w-350px matches the input's own w-lg-350px so the panel is
      // the same width as the thing it belongs to.
      box.className = 'menu menu-sub menu-sub-dropdown menu-column menu-rounded ' +
                      'menu-state-bg-light-primary fw-semibold py-3 w-350px mw-100 show';
      box.style.position = 'absolute';
      box.style.zIndex = '1060';
      box.style.display = 'none';
      box.setAttribute('data-cb-global-search', '1');
      box.addEventListener('click', function (e) {
        var a = e.target && e.target.closest ? e.target.closest('a[data-cb-ask]') : null;
        if (!a) return;
        e.preventDefault();
        askAssistant(liveInput().value);
      });
      document.body.appendChild(box);
      return box;
    }

    // Anchored to the input in page coordinates, so it stays put inside the shell's sticky header
    // and does not inherit a stacking context that would clip it.
    function place() {
      var el = liveInput(), r = el.getBoundingClientRect(), p = panel();
      p.style.top = (window.scrollY + r.bottom + 6) + 'px';
      if (isAr) {
        p.style.right = (document.documentElement.clientWidth - (window.scrollX + r.right)) + 'px';
        p.style.left = 'auto';
      } else {
        p.style.left = (window.scrollX + r.left) + 'px';
        p.style.right = 'auto';
      }
      p.style.width = Math.max(260, r.width) + 'px';
    }

    function hide() { if (box) { box.style.display = 'none'; activeIndex = -1; } spin(false); }
    function visible() { return box && box.style.display === 'block'; }
    // The ask row is part of the keyboard path, not a mouse-only extra — it is the first thing
    // ArrowDown lands on, which is also the fastest way to reach it.
    function items() { return box ? Array.prototype.slice.call(box.querySelectorAll('a.menu-link[data-cb-ask], a.menu-link[data-cb-hit]')) : []; }

    function highlight(delta) {
      var list = items();
      if (!list.length) return;
      if (activeIndex >= 0 && list[activeIndex]) list[activeIndex].classList.remove('active');
      activeIndex = (activeIndex + delta + list.length) % list.length;
      list[activeIndex].classList.add('active');
      list[activeIndex].scrollIntoView({ block: 'nearest' });
    }

    function render(data, term) {
      var p = panel(), html = '';
      var groups = (data && data.groups) || [];

      // THE ASK ROW, FIRST AND ALWAYS (when an assistant is on the page). It is not conditional on
      // the text looking like a question: a person who typed a bare noun may still want the count
      // rather than the list, and the row is how they find out the assistant is there at all. It is
      // conditional on cbAi existing, so a layout without the assistant degrades to plain search.
      if (hasAssistant()) {
        html += '<div class="menu-item px-3">' +
                  '<a href="#" data-cb-ask="1" class="menu-link px-3 d-flex align-items-center gap-3">' +
                    '<i class="ki-outline ki-abstract-26 fs-5 text-primary flex-shrink-0"></i>' +
                    '<span class="text-truncate">' + esc(T.ask) +
                      ' <span class="text-muted">«<bdi>' + esc(term) + '</bdi>»</span>' +
                    '</span>' +
                  '</a>' +
                '</div>' +
                '<div class="separator my-2"></div>';
      }

      if (!groups.length) {
        // Says what was searched AND the limit on it: "nothing matched" alone would read as a claim
        // that the record does not exist, when it may only be outside this person's permission.
        html += '<div class="menu-item px-3 py-2">' +
               '<div class="menu-content text-gray-700 fs-7">' + esc(T.none) + ' «<bdi>' + esc(term) + '</bdi>» — ' +
               '<span class="text-muted">' + esc(T.scope) + '</span></div></div>';
      } else {
        groups.forEach(function (g, gi) {
          if (gi) html += '<div class="separator my-2"></div>';
          html += '<div class="menu-item px-3">' +
                    '<div class="menu-content d-flex align-items-center gap-2 text-muted text-uppercase fs-8 fw-bold px-3 pb-2">' +
                      '<i class="' + esc(g.icon) + ' fs-7"></i>' + esc(g.label) +
                    '</div>' +
                  '</div>';
          (g.hits || []).forEach(function (h) {
            var label = esc(h.Label || h.label || '');
            if (h.url) {
              // SAYS WHAT IT OPENS. A type with no detail screen can only reach a list — filtered to
              // this record where the list reads the term, the whole list where it does not. Offering
              // that as the record is how one employee's hit opened every employee.
              var note = h.opensList
                ? '<span class="text-muted fs-8 ms-2 flex-shrink-0">' + esc(T.listOnly) + '</span>'
                : '';
              html += '<div class="menu-item px-3">' +
                        '<a href="' + esc(h.url) + '" data-cb-hit="1" class="menu-link px-3 d-flex align-items-center justify-content-between">' +
                          '<bdi class="text-truncate">' + label + '</bdi>' + note +
                        '</a>' +
                      '</div>';
            } else {
              // No screen to open is a fact about the product, not a fault — shown, but plainly not
              // a link, so nobody clicks something that cannot answer.
              html += '<div class="menu-item px-3"><div class="menu-content px-3 py-2 text-muted fs-7">' +
                      '<bdi>' + label + '</bdi></div></div>';
            }
          });
        });
        html += '<div class="separator my-2"></div>' +
                '<div class="menu-item px-3">' +
                  '<a href="/Search/Results?q=' + encodeURIComponent(term) + '" class="menu-link px-3 text-primary">' +
                    esc(T.all) + ' (' + (data.total || 0) + ')' +
                  '</a>' +
                '</div>';
      }

      p.innerHTML = html;
      activeIndex = -1;
      place();
      p.style.display = 'block';
    }

    function onType(e) {
      if (!e.target.classList || !e.target.classList.contains('search-input')) return;
      var term = (e.target.value || '').trim();
      if (timer) clearTimeout(timer);
      if (term.length < 2) { hide(); spin(false); return; }
      if (term === lastTerm && visible()) return;
      timer = setTimeout(function () {
        lastTerm = term;
        spin(true);
        fetch('/Search/Suggest?q=' + encodeURIComponent(term), { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
          .then(function (r) { return r.ok ? r.json() : null; })
          // The guard matters: answers can arrive out of order, and the one that matches what is in
          // the box now is the only one worth drawing.
          .then(function (d) { if (liveInput().value.trim() === term) render(d, term); })
          .catch(function () { /* a failed suggest never breaks the box; Enter still works */ })
          // Whatever happened, the box is no longer waiting — and saying so is the whole point.
          .then(function () { if (liveInput().value.trim() === term) spin(false); });
      }, 250);
    }

    spin(false);   // in case KTSearch unhid it before we got here
    root.addEventListener('input', onType, true);
    root.addEventListener('keyup', onType, true);

    // A dropdown you can only reach with the mouse is half a control.
    root.addEventListener('keydown', function (e) {
      if (!e.target.classList || !e.target.classList.contains('search-input')) return;
      if (e.key === 'ArrowDown' && visible()) { e.preventDefault(); highlight(1); return; }
      if (e.key === 'ArrowUp' && visible()) { e.preventDefault(); highlight(-1); return; }
      if (e.key === 'Escape') { hide(); return; }
      if (e.key === 'Enter') {
        e.preventDefault(); e.stopPropagation();
        var term = (e.target.value || '').trim();
        var list = items();
        if (visible() && activeIndex >= 0 && list[activeIndex]) {
          var sel = list[activeIndex];
          if (sel.hasAttribute('data-cb-ask')) { askAssistant(term); return; }
          window.location.href = sel.href;
          return;
        }
        // Nothing picked from the list, so the TEXT decides. A question reaches the assistant; a
        // term opens the results page. On a page with no assistant, isQuestion's answer is never
        // acted on and Enter behaves exactly as it did before this box learned to ask.
        if (isQuestion(term) && askAssistant(term)) return;
        go(term);
      }
    }, true);

    if (form) form.addEventListener('submit', function (e) { e.preventDefault(); go(liveInput().value); });
    document.addEventListener('click', function (e) {
      if (box && !root.contains(e.target) && !box.contains(e.target)) hide();
    });
    window.addEventListener('resize', function () { if (visible()) place(); });
    window.addEventListener('scroll', function () { if (visible()) place(); }, true);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init); else init();
})();
