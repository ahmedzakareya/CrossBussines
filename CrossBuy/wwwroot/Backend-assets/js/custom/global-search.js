// Global search wiring for the header box (TAB-0). Enter → the full /Search/Results page; typing (>=2 chars) shows a
// live dropdown of grouped hits from /Search/Suggest. Self-contained: it reads the Metronic header-search markup that
// already ships in the layout and never depends on the demo's hardcoded items.
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

    // Neutralise Metronic's demo KTSearch dropdown (hardcoded "Recently Searched" items). We own the dropdown.
    if (content) { content.style.setProperty('display', 'none', 'important'); content.setAttribute('data-cb-hidden', '1'); }
    root.removeAttribute('data-kt-menu-trigger');
    root.removeAttribute('data-kt-search');
    // keep it hidden even if KTMenu tries to reopen it on focus
    if (content) new MutationObserver(function () {
      if (content.getAttribute('data-cb-hidden') && content.style.display !== 'none') content.style.setProperty('display', 'none', 'important');
    }).observe(content, { attributes: true, attributeFilter: ['style', 'class'] });

    var isAr = (document.documentElement.getAttribute('dir') === 'rtl') ||
               (document.documentElement.lang || '').slice(0, 2) === 'ar';
    var box = null, timer = null, lastTerm = '';

    function go(term) { if (term && term.trim().length >= 2) window.location.href = '/Search/Results?q=' + encodeURIComponent(term.trim()); }

    // Delegate from the container (capture) so we keep working even if KTSearch replaces the input node.
    function liveInput() { return root.querySelector('.search-input') || input; }
    root.addEventListener('keydown', function (e) {
      if (!e.target.classList || !e.target.classList.contains('search-input')) return;
      if (e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); go(e.target.value); }
    }, true);
    if (form) form.addEventListener('submit', function (e) { e.preventDefault(); go(liveInput().value); });

    function panel() {
      if (box) return box;
      root.style.position = 'relative';
      box = document.createElement('div');
      box.className = 'cb-gsearch py-4 px-4';
      box.style.cssText = 'position:absolute;z-index:1060;inset-inline-start:0;top:100%;min-width:320px;margin-top:6px;background:var(--bs-body-bg);border:1px solid var(--bs-border-color);border-radius:.65rem;box-shadow:0 .5rem 1.5rem rgba(0,0,0,.15);max-height:70vh;overflow:auto;display:none;';
      root.appendChild(box);
      return box;
    }
    function hide() { if (box) box.style.display = 'none'; }
    function esc(s) { var d = document.createElement('div'); d.textContent = s == null ? '' : s; return d.innerHTML; }

    function render(data, term) {
      var p = panel();
      if (!data || !data.groups || !data.groups.length) {
        p.innerHTML = '<div class="text-muted fs-7 px-2 py-3">' + (isAr ? 'لا نتائج لـ «' : 'Nothing for “') + esc(term) + '»</div>';
      } else {
        var html = '';
        data.groups.forEach(function (g) {
          html += '<div class="d-flex align-items-center gap-2 text-muted fs-8 fw-bold text-uppercase pt-3 pb-1">' +
                  '<i class="ki-outline ' + esc(g.icon) + ' fs-6"></i>' + esc(g.label) + '</div>';
          g.hits.forEach(function (h) {
            var label = esc(h.Label || h.label);
            if (h.url) html += '<a href="' + h.url + '" class="d-flex align-items-center text-gray-800 text-hover-primary py-2 px-2 rounded"><span class="fw-semibold fs-7">' + label + '</span></a>';
            else html += '<div class="py-2 px-2 fs-7 text-gray-800">' + label + '</div>';
          });
        });
        html += '<a href="/Search/Results?q=' + encodeURIComponent(term) + '" class="d-flex align-items-center justify-content-center text-primary fw-semibold fs-7 border-top border-gray-200 mt-2 pt-3">' +
                (isAr ? 'كل النتائج (' : 'All results (') + data.total + ')</a>';
        p.innerHTML = html;
      }
      p.style.display = 'block';
    }

    function onType(e) {
      if (!e.target.classList || !e.target.classList.contains('search-input')) return;
      var term = (e.target.value || '').trim();
      if (timer) clearTimeout(timer);
      if (term.length < 2) { hide(); return; }
      if (term === lastTerm && box && box.style.display === 'block') return;
      timer = setTimeout(function () {
        lastTerm = term;
        fetch('/Search/Suggest?q=' + encodeURIComponent(term), { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
          .then(function (r) { return r.ok ? r.json() : null; })
          .then(function (d) { if (liveInput().value.trim() === term) render(d, term); })
          .catch(function () { /* a failed suggest never breaks the box; Enter still works */ });
      }, 250);
    }
    root.addEventListener('input', onType, true);
    root.addEventListener('keyup', onType, true);

    document.addEventListener('click', function (e) { if (box && !root.contains(e.target) && !box.contains(e.target)) hide(); });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init); else init();
})();
