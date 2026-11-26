// CRM C8 — the ticket screen's customer panel: loads the thread + suggested articles, sends a reply or an article.
// The panel's markup arrives by fetch (scripts inside it would not run), so every handler is delegated from here.
(function () {
  var host = document.getElementById('ticketDesk');
  if (!host) return;
  var src = host.getAttribute('data-src'), replyUrl = host.getAttribute('data-reply'), failed = host.getAttribute('data-failed') || 'Error';
  function token() { var t = document.querySelector('input[name="__RequestVerificationToken"]'); return t ? t.value : ''; }
  function load() {
    return fetch(src, { credentials: 'same-origin' }).then(function (r) { return r.ok ? r.text() : ''; }).then(function (h) {
      host.innerHTML = h;
      var box = host.querySelector('.crm-ib-msgs'); if (box) box.scrollTop = box.scrollHeight;
    });
  }
  async function send(data) {
    var fd = new FormData(); Object.keys(data).forEach(function (k) { if (data[k] != null) fd.append(k, data[k]); });
    fd.append('__RequestVerificationToken', token());
    var r = await fetch(replyUrl, { method: 'POST', body: fd, credentials: 'same-origin' }).then(function (x) { return x.json(); }).catch(function () { return { ok: false, error: failed }; });
    if (r.ok) load(); else if (window.CB && CB.toast) CB.toast(r.error || failed, 'error');
  }
  host.addEventListener('click', function (e) {
    var id = host.querySelector('#deskCard') && host.querySelector('#deskCard').getAttribute('data-ticket');
    if (e.target.closest('#deskSend')) { var ta = host.querySelector('#deskBody'); if (ta.value.trim()) send({ id: id, body: ta.value }); return; }
    var a = e.target.closest('[data-article]');
    if (a) send({ id: id, articleId: a.getAttribute('data-article'), body: (host.querySelector('#deskBody') || {}).value || '' });
  });
  load();
})();
