// CRM C9 — the assistant on three existing screens, added without touching how they work:
//   the deal page      #dealAssist[data-src]       → the assistant panel (summary, do next, draft reply)
//   the leads list     #rows + #crmAssistCfg[data-lead-insights] → a behavioural score with its reasons beside the rule score
//   the pipeline board .kanban-card[data-id] + #crmAssistCfg[data-pipeline-hints] → the card's next action and risk
(function () {
  var cfg = document.getElementById('crmAssistCfg') || document.createElement('div');
  function esc(s) { return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;'); }
  // ---- the deal page
  var panel = document.getElementById('dealAssist');
  if (panel) {
    fetch(panel.getAttribute('data-src'), { credentials: 'same-origin' }).then(function (r) { return r.ok ? r.text() : ''; }).then(function (h) { panel.innerHTML = h; });
    panel.addEventListener('click', function (e) {
      if (!e.target.closest('[data-copy-draft]')) return;
      var t = panel.querySelector('[data-draft]'); if (!t) return;
      t.select(); if (navigator.clipboard) navigator.clipboard.writeText(t.value);
      if (window.CB && CB.toast) CB.toast(panel.getAttribute('data-copied') || 'Copied', 'success');
    });
  }
  // ---- the leads list (rows arrive by fetch, so watch for them)
  var leadsUrl = cfg.getAttribute('data-lead-insights'), rows = document.getElementById('rows');
  if (leadsUrl && rows) {
    var busy = false;
    function decorate() {
      if (busy) return;
      var trs = Array.prototype.filter.call(rows.querySelectorAll('tr[data-id]'), function (tr) { return !tr.hasAttribute('data-ins'); });
      if (!trs.length) return;
      busy = true;
      fetch(leadsUrl + '?ids=' + trs.map(function (tr) { return tr.getAttribute('data-id'); }).join(','), { credentials: 'same-origin' }).then(function (r) { return r.ok ? r.json() : {}; }).then(function (map) {
        trs.forEach(function (tr) {
          tr.setAttribute('data-ins', '1');
          var x = map[tr.getAttribute('data-id')]; if (!x) return;
          var cell = tr.querySelectorAll('td')[5]; if (!cell) return;   // the rule-score column
          var col = x.band === 'Hot' ? 'danger' : x.band === 'Warm' ? 'primary' : 'secondary';
          var div = document.createElement('div'); div.className = 'mt-1';
          div.innerHTML = '<span class="badge badge-' + col + ' fs-9" title="' + esc(x.why.join('\n')) + '">' + esc(x.bandText) + ' · ' + x.score + '</span>'
            + '<div class="fs-9 text-muted mt-1 text-truncate" style="max-width:180px;margin-inline:auto">' + esc(x.why[0] || '') + '</div>';
          cell.appendChild(div);
        });
      }).finally(function () { busy = false; });
    }
    new MutationObserver(decorate).observe(rows, { childList: true });
    decorate();
  }
  // ---- the pipeline board
  var hintsUrl = cfg.getAttribute('data-pipeline-hints');
  if (hintsUrl) {
    var cards = document.querySelectorAll('.kanban-card[data-id]');
    if (cards.length) fetch(hintsUrl + '?ids=' + Array.prototype.map.call(cards, function (c) { return c.getAttribute('data-id'); }).join(','), { credentials: 'same-origin' })
      .then(function (r) { return r.ok ? r.json() : {}; }).then(function (map) {
        cards.forEach(function (c) {
          var x = map[c.getAttribute('data-id')]; if (!x || !x.action) return;
          var body = c.querySelector('.card-body') || c;
          var d = document.createElement('div');
          d.className = 'crm-card-hint ' + (x.urgent ? 'urgent' : '') + (x.risk === 'High' ? ' risk' : '');
          d.innerHTML = '<i class="ki-outline ki-abstract-26 fs-7"></i><span>' + esc(x.action) + '</span>';
          body.appendChild(d);
        });
      });
  }
})();
