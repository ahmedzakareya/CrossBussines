// RC-3b — Kitchen Display Screen (KDS). Reads /pos/kds/tickets (poll) and advances line status via /pos/kds/line-status.
// Operational only — never touches accounting. Real-time (SignalR) comes in RC-3c; for now we poll.
(function () {
    var K = window.KDS || {}; var U = K.urls || {}, L = K.L || {}, RTL = !!K.rtl;
    var STATIONS = K.stations || [];
    var token = (document.querySelector('#af input[name="__RequestVerificationToken"]') || {}).value || '';
    var grid = document.getElementById('kdsGrid'), emptyEl = document.getElementById('kdsEmpty'), countEl = document.getElementById('kdsCount'), tabsEl = document.getElementById('kdsTabs');
    var tickets = [];
    // RC-3e: station filter (client-side). Persisted so a station screen remembers its tab.
    var curStation = (function () { try { return localStorage.getItem('cbKdsStation') || 'all'; } catch (e) { return 'all'; } })();

    function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
    // normalise a stored item image path (backslashes → slashes, ensure leading slash); null if none
    function imgSrc(p) { if (!p) return null; p = String(p).replace(/\\/g, '/'); if (!/^https?:|^\//.test(p)) p = '/' + p; return p; }
    function post(u, d) {
        var fd = new FormData(); fd.append('__RequestVerificationToken', token);
        Object.keys(d).forEach(function (k) { fd.append(k, d[k]); });
        return fetch(u, { method: 'POST', body: fd, headers: { 'X-Requested-With': 'fetch' } }).then(function (r) { return r.json(); });
    }
    // Friendly elapsed duration ("منذ 3 د" / "1 س 20 د"), NOT a clock — a wall-clock time reads wrong for "how long ago".
    function elapsedText(iso) {
        if (!iso) return RTL ? 'الآن' : 'now';
        var ms = Date.now() - Date.parse(iso); if (!(ms > 0)) ms = 0;
        var m = Math.floor(ms / 60000);
        if (m < 1) return RTL ? 'الآن' : 'now';
        var h = Math.floor(m / 60), mm = m % 60;
        if (h > 0) return RTL ? (h + ' س ' + mm + ' د') : (h + 'h ' + mm + 'm');
        return RTL ? (m + ' د') : (m + 'm');
    }
    // Urgency tier by age → colour that escalates (fresh→success, ageing→warning, late→danger). Conveys that time is passing.
    function elapsedTier(iso) {
        if (!iso) return 'success';
        var m = Math.floor(Math.max(0, Date.now() - Date.parse(iso)) / 60000);
        return m < 8 ? 'success' : (m < 18 ? 'warning' : 'danger');
    }
    function statusLabel(s) { return s === 'Ready' ? L.ready : (s === 'Preparing' ? L.preparing : L['new']); }
    function nextStatus(s) { return s === 'New' ? 'Preparing' : (s === 'Preparing' ? 'Ready' : null); }
    // KDS status colours — brand-aligned (crossbuy-brand: --bs-primary #0E4A9E). New=primary(brand), Preparing=warning, Ready=success. No off-brand blue/purple.
    function statusColor(s) { return s === 'Ready' ? 'success' : (s === 'Preparing' ? 'warning' : 'primary'); }
    function TL(ar, en) { return RTL ? ar : en; }
    // Every status change is CONFIRMED first (no "click and get surprised"), then a toast reports the result.
    function cbConfirm(text, toReady) {
        if (window.CB && window.CB.confirm) {
            return window.CB.confirm({
                type: 'generic', icon: 'question',
                title: TL('تأكيد تغيير الحالة', 'Confirm status change'), text: text,
                confirmText: TL('Confirm', 'Confirm'),
                confirmClass: toReady ? 'btn btn-success' : 'btn btn-warning'
            });
        }
        return Promise.resolve(window.confirm(text));
    }
    function toastOk(m) { if (window.CB && window.CB.toast) window.CB.toast.success(m); }
    function toastErr(m) { if (window.CB && window.CB.toast) window.CB.toast.error(m); }

    // station tabs: «الكل» + one per branch station — Metronic nav-line-tabs (brand green underline via crossbuy-brand.css)
    function renderTabs() {
        if (!tabsEl) return;
        if (!STATIONS.length) { tabsEl.style.display = 'none'; return; }
        // Reference tab shape (Metronic nav-line-tabs): a continuous bottom line under ALL tabs + a coloured segment
        // under the active one. Spaced with me-8; active underline/text = brand green (crossbuy-brand.css).
        var tab = function (id, label) {
            return '<li class="nav-item"><button type="button" class="nav-link text-active-primary pb-4 me-8' + (curStation === id ? ' active' : '') + '" data-st="' + id + '">' + esc(label) + '</button></li>';
        };
        var html = tab('all', L.all || 'All');
        html += STATIONS.map(function (s) { return tab(String(s.id), s.name); }).join('');
        tabsEl.innerHTML = html;
    }
    function lineInStation(l) { return curStation === 'all' || String(l.stationId) === curStation; }

    function render() {
        // filter each ticket to the selected station's lines; drop tickets with none
        var view = tickets.map(function (t) {
            var ls = (t.lines || []).filter(lineInStation);
            return ls.length ? { t: t, lines: ls } : null;
        }).filter(Boolean);
        if (countEl) countEl.textContent = view.length;
        if (!view.length) { grid.innerHTML = ''; if (emptyEl) emptyEl.classList.remove('d-none'); return; }
        if (emptyEl) emptyEl.classList.add('d-none');
        // Card design adapted from Metronic demo39 pages/user-profile/projects.html
        // (symbol + status badge in the header, dashed stat boxes, progress bar) — line list lives in the body.
        grid.innerHTML = view.map(function (v) {
            var t = v.t;
            var oc = statusColor(t.orderKds);   // order-level colour → symbol, status badge, progress bar
            var isTable = !!t.tableCode;
            var whereIcon = isTable ? 'ki-geolocation' : 'ki-handcart';
            var whereTitle = isTable ? (esc(L.table) + ' ' + esc(t.tableCode)) : esc(L.takeaway);
            var total = v.lines.length;
            var readyCount = v.lines.filter(function (l) { return l.kdsStatus === 'Ready'; }).length;
            var pct = total ? Math.round(readyCount / total * 100) : 0;

            var head = '<div class="card-header border-0 pt-7">'
                + '<div class="card-title m-0">'
                + '<div class="symbol symbol-45px w-45px bg-light-' + oc + ' me-3"><span class="symbol-label"><i class="ki-outline ' + whereIcon + ' fs-2 text-' + oc + '"></i></span></div>'
                + '<div class="d-flex flex-column"><span class="fs-3 fw-bold text-gray-900">' + whereTitle + '</span><span class="fs-6 fw-semibold text-gray-500">' + esc(L.order) + ' #' + t.orderId + '</span></div>'
                + '</div>'
                + '<div class="card-toolbar"><span class="badge badge-light-' + oc + ' fw-bold px-4 py-3">' + esc(statusLabel(t.orderKds)) + '</span></div></div>';

            var tier = elapsedTier(t.sinceUtcIso);   // escalating urgency colour (fresh→ageing→late)
            var stats = '<div class="d-flex flex-wrap mb-4">'
                + '<div class="border border-' + tier + ' border-dashed rounded min-w-110px py-2 px-4 me-4 mb-3 kt-timebox" data-since="' + esc(t.sinceUtcIso) + '" data-tier="' + tier + '">'
                + '<div class="d-flex align-items-center">'
                + '<span class="pulse pulse-' + tier + ' me-3 kt-pulse"><span class="pulse-ring"></span><i class="ki-outline ki-time fs-4 text-' + tier + ' kt-ticon"></i></span>'
                + '<span class="fs-5 fw-bold text-' + tier + ' kt-time">' + elapsedText(t.sinceUtcIso) + '</span></div>'
                + '<div class="fw-semibold text-gray-500 fs-8 mt-1">' + TL('منذ الإرسال', 'Since sent') + '</div></div>'
                + '<div class="border border-gray-300 border-dashed rounded min-w-100px py-2 px-4 mb-3"><div class="fs-5 text-gray-800 fw-bold">' + readyCount + '/' + total + '</div><div class="fw-semibold text-gray-500 fs-8 mt-1">' + esc(L.ready) + '</div></div>'
                + '</div>';
            var progress = '<div class="h-6px w-100 bg-light rounded mb-6"><div class="bg-' + oc + ' rounded h-6px" role="progressbar" style="width:' + pct + '%"></div></div>';

            var flow = ['New', 'Preparing', 'Ready'];
            var lines = v.lines.map(function (l) {
                var st = l.kdsStatus || 'New';
                var sc = statusColor(st);
                var stTag = (curStation === 'all' && l.stationName) ? '<span class="badge badge-light fs-8 ms-2">' + esc(l.stationName) + '</span>' : '';
                // status change = pick from a Metronic dropdown LIST (forward statuses only); confirm happens on pick.
                var control;
                if (st === 'Ready') {
                    control = '<span class="badge badge-light-success py-2 px-3"><i class="ki-outline ki-check fs-7 me-1"></i>' + esc(L.ready) + '</span>';
                } else {
                    var ci = flow.indexOf(st), items = '';
                    for (var i = ci + 1; i < flow.length; i++) {
                        items += '<div class="menu-item px-2"><a href="#" class="menu-link px-3 py-2 kl-set" data-order="' + t.orderId + '" data-line="' + l.lineId + '" data-status="' + flow[i] + '" data-name="' + esc(l.name) + '"><span class="bullet bullet-dot bg-' + statusColor(flow[i]) + ' me-3"></span>' + esc(statusLabel(flow[i])) + '</a></div>';
                    }
                    control = '<div>'
                        + '<button type="button" class="btn btn-sm btn-light-' + sc + ' py-1 px-3 d-flex align-items-center" data-kt-menu-trigger="click" data-kt-menu-placement="bottom-end">' + esc(statusLabel(st)) + '<i class="ki-outline ki-down fs-8 ms-2"></i></button>'
                        + '<div class="menu menu-sub menu-sub-dropdown menu-column menu-rounded menu-gray-800 menu-state-bg-light-primary fw-semibold w-150px py-2" data-kt-menu="true">'
                        + '<div class="menu-item px-3"><div class="menu-content text-muted fs-8 text-uppercase px-3 pb-1">' + TL('تغيير الحالة', 'Change status') + '</div></div>'
                        + items + '</div></div>';
                }
                var img = imgSrc(l.image);   // the DISH'S real photo (Item.ImagePath) — a food thumbnail, not an avatar
                var thumb = img
                    ? '<div class="symbol symbol-50px me-3"><img src="' + esc(img) + '" alt="" class="rounded"/></div>'
                    : '<div class="symbol symbol-50px me-3"><span class="symbol-label bg-light-' + sc + ' rounded"><i class="ki-outline ki-cup fs-2 text-' + sc + '"></i></span></div>';
                var strike = st === 'Ready' ? ' text-decoration-line-through opacity-50' : '';
                var mods = (l.modifiers && l.modifiers.length)
                    ? '<div class="fs-8 text-gray-600' + strike + '">' + l.modifiers.map(function (m) { return '+ ' + esc(m); }).join(', ') + '</div>' : '';
                return '<div class="d-flex align-items-center py-3 px-3 border-bottom border-gray-200 border-dashed">'
                    + thumb
                    + '<div class="flex-grow-1 me-2">'
                    + '<span class="fs-6 fw-bold text-gray-800' + strike + '"><span class="text-' + statusColor('New') + '">' + l.qty + '× </span>' + esc(l.name) + '</span>' + stTag + mods + '</div>'
                    + control + '</div>';
            }).join('');

            var allReady = v.lines.every(function (l) { return l.kdsStatus === 'Ready'; });   // for the VISIBLE (station's) lines
            var foot = allReady ? '' : '<div class="card-footer py-4"><button type="button" class="btn btn-sm btn-light-success w-100 kt-allready" data-order="' + t.orderId + '"><i class="ki-outline ki-check-circle fs-5 me-1"></i>' + esc(L.allReady) + '</button></div>';
            return '<div class="col-md-6 col-xl-4"><div class="card hover-elevate-up shadow-xs h-100" data-order="' + t.orderId + '">'
                + head
                + '<div class="card-body pt-3 pb-6 px-8">' + stats + progress + '<div class="d-flex flex-column">' + lines + '</div></div>'
                + foot + '</div></div>';
        }).join('');
        // dynamically-rendered dropdowns need KTMenu wired up (Metronic only auto-inits menus present at page load)
        if (window.KTMenu && typeof KTMenu.createInstances === 'function') { try { KTMenu.createInstances(); } catch (e) { } }
    }

    var busy = false;
    // Advance ONE line — confirm first, then apply, then toast.
    function advance(orderId, lineId, status, name) {
        if (busy) return;
        var toReady = status === 'Ready';
        var q = toReady
            ? TL('Mark «' + name + '» as ready?', 'Mark "' + name + '" as ready?')
            : TL('بدء تحضير «' + name + '»؟', 'Start preparing "' + name + '"?');
        cbConfirm(q, toReady).then(function (ok) {
            if (!ok) return;
            busy = true;
            post(U.lineStatus, { orderId: orderId, lineId: lineId, status: status }).then(function (j) {
                busy = false;
                if (j.ok) { toastOk(toReady ? TL('تم تعليم الصنف كجاهز', 'Item marked ready') : TL('بدأ تحضير الصنف', 'Preparation started')); load(); }
                else toastErr(j.error || TL('تعذّر تحديث الحالة', 'Could not update status'));
            }).catch(function () { busy = false; toastErr(TL('تعذّر الاتصال', 'Connection failed')); });
        });
    }
    // Mark all VISIBLE (current station) lines Ready — confirm first, then apply sequentially, then toast.
    function allReady(orderId) {
        var t = tickets.filter(function (x) { return x.orderId === orderId; })[0]; if (!t) return;
        // only ready the lines VISIBLE in the current station tab (don't touch other stations' lines)
        var pending = (t.lines || []).filter(function (l) { return l.kdsStatus !== 'Ready' && lineInStation(l); });
        if (!pending.length) return;
        cbConfirm(TL('تعليم كل أصناف الطلب #' + orderId + ' كجاهزة؟ (' + pending.length + ' صنف)', 'Mark all items of order #' + orderId + ' as ready? (' + pending.length + ' items)'), true).then(function (ok) {
            if (!ok) return;
            busy = true;
            (function step(i) {
                if (i >= pending.length) { busy = false; toastOk(TL('تم تعليم الطلب كجاهز', 'Order marked ready')); load(); return; }
                post(U.lineStatus, { orderId: orderId, lineId: pending[i].lineId, status: 'Ready' }).then(function () { step(i + 1); }).catch(function () { step(i + 1); });
            })(0);
        });
    }

    grid.addEventListener('click', function (e) {
        var set = e.target.closest('.kl-set');
        if (set) { e.preventDefault(); advance(parseInt(set.getAttribute('data-order')), parseInt(set.getAttribute('data-line')), set.getAttribute('data-status'), set.getAttribute('data-name') || ''); return; }
        var ar = e.target.closest('.kt-allready');
        if (ar) { allReady(parseInt(ar.getAttribute('data-order'))); return; }
    });

    // RC-3e: station tabs — filter to a station (persisted); no re-fetch, just re-filter the loaded tickets
    if (tabsEl) tabsEl.addEventListener('click', function (e) {
        var b = e.target.closest('.nav-link'); if (!b) return;
        curStation = b.getAttribute('data-st');
        try { localStorage.setItem('cbKdsStation', curStation); } catch (x) { }
        renderTabs(); render();
    });

    function load() {
        return fetch(U.tickets, { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' }).then(function (r) { return r.json(); })
            .then(function (j) { if (j && j.ok) { tickets = j.tickets || []; render(); } }).catch(function () { });
    }

    var refreshBtn = document.getElementById('kdsRefresh'); if (refreshBtn) refreshBtn.addEventListener('click', load);
    // live clock + per-card timers tick (no re-fetch)
    var clockEl = document.getElementById('kdsClock');
    setInterval(function () {
        if (clockEl) { var d = new Date(); clockEl.textContent = ('0' + d.getHours()).slice(-2) + ':' + ('0' + d.getMinutes()).slice(-2) + ':' + ('0' + d.getSeconds()).slice(-2); }
        grid.querySelectorAll('.kt-timebox').forEach(function (box) {
            var s = box.getAttribute('data-since'); if (!s) return;
            var t = box.querySelector('.kt-time'); if (t) t.textContent = elapsedText(s);   // ticks every second (feels live)
            var tier = elapsedTier(s);
            if (box.getAttribute('data-tier') !== tier) {   // only touch colour classes on a real tier change → pulse keeps animating
                box.setAttribute('data-tier', tier);
                box.className = 'border border-' + tier + ' border-dashed rounded min-w-110px py-2 px-4 me-4 mb-3 kt-timebox';
                var p = box.querySelector('.kt-pulse'); if (p) p.className = 'pulse pulse-' + tier + ' me-3 kt-pulse';
                var ic = box.querySelector('.kt-ticon'); if (ic) ic.className = 'ki-outline ki-time fs-4 text-' + tier + ' kt-ticon';
                if (t) t.className = 'fs-5 fw-bold text-' + tier + ' kt-time';
            }
        });
    }, 1000);

    renderTabs();
    load();

    // RC-3c: real-time. Any POS event on this branch → re-fetch tickets instantly (authoritative).
    // The 5s poll stays as a FALLBACK: fast (2s) while the socket is down, slow (15s) while it's live.
    var live = false;
    function refetch() { load(); }
    if (window.signalR) {
        try {
            var conn = new signalR.HubConnectionBuilder().withUrl('/hubs/pos').withAutomaticReconnect().build();
            ['OrderSentToKitchen', 'LineKdsStatusChanged', 'OrderReady', 'OrderPaid'].forEach(function (ev) { conn.on(ev, refetch); });
            conn.onreconnecting(function () { live = false; });
            conn.onreconnected(function () { live = true; load(); });
            conn.onclose(function () { live = false; });
            conn.start().then(function () { live = true; }).catch(function () { live = false; });
        } catch (e) { live = false; }
    }
    // adaptive fallback poll: 15s while the socket is live, 3s while it's down
    (function tick() { setTimeout(function () { load(); tick(); }, live ? 15000 : 3000); })();
})();
