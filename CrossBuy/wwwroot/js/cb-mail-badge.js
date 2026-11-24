// THE UNREAD MAIL ON THE ENVELOPE (email programme, owner 2026-11-24 «كمل»).
// The header's mail button carries the reader's own unread count, from /Comm/Counts (my Inbox only - a colleague's
// mail is never counted). It looks again every minute, when the tab comes back into view, and at once when the
// notifications hub announces new mail. Self-contained: each layout includes this one file beside its mail button.
(function () {
    var a = document.querySelector('.app-navbar-item a[href$="/Comm"]');
    if (!a || a.getAttribute('data-cb-mail-badge')) return;
    a.setAttribute('data-cb-mail-badge', '1');
    a.classList.add('position-relative');
    var b = document.createElement('span');
    b.className = 'position-absolute top-0 start-100 translate-middle badge badge-circle badge-primary d-none';
    b.style.cssText = 'font-size:9px;min-width:18px;height:18px;padding:0 4px';
    a.appendChild(b);
    var title = a.getAttribute('title') || '';

    function paint(n) {
        if (n > 0) {
            b.textContent = n > 99 ? '99+' : String(n);
            b.classList.remove('d-none');
            a.setAttribute('aria-label', title + ' (' + n + ')');
        } else {
            b.classList.add('d-none');
            a.setAttribute('aria-label', title);
        }
    }
    function look() {
        fetch('/Comm/Counts', { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (c) { if (c) paint(c.inbox || 0); })
            .catch(function () { });
    }
    function hub() {
        try {
            var conn = new signalR.HubConnectionBuilder().withUrl('/hubs/notifications').withAutomaticReconnect().build();
            conn.on('notification', function (n) {
                if (n && ((n.type || n.Type) === 'mail_received' || String(n.url || n.Url || '').indexOf('/Comm/View/') >= 0)) look();
            });
            conn.start().catch(function () { });
        } catch (e) { }
    }
    look();
    setInterval(look, 60000);
    document.addEventListener('visibilitychange', function () { if (!document.hidden) look(); });
    if (window.signalR) hub(); else window.addEventListener('load', function () { if (window.signalR) hub(); });
})();
