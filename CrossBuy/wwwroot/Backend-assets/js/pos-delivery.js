// POS-C4 — Delivery board. Lists open Delivery orders, assign driver, advance delivery status (out → delivered).
// Operational only (no GL). Real-time via the shared PosHub; every status change is CONFIRMED then toasts.
(function () {
    var D = window.DELIV || {}; var U = D.urls || {}, L = D.L || {}, RTL = !!D.rtl;
    var DRIVERS = D.drivers || [];
    var token = (document.querySelector('#af input[name="__RequestVerificationToken"]') || {}).value || '';
    var grid = document.getElementById('delivGrid'), emptyEl = document.getElementById('delivEmpty'), countEl = document.getElementById('delivCount');
    var orders = [];

    function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
    function TL(ar, en) { return RTL ? ar : en; }
    function post(u, d) {
        var fd = new FormData(); fd.append('__RequestVerificationToken', token);
        Object.keys(d).forEach(function (k) { if (d[k] !== null && d[k] !== undefined) fd.append(k, d[k]); });
        return fetch(u, { method: 'POST', body: fd, headers: { 'X-Requested-With': 'fetch' } }).then(function (r) { return r.json(); });
    }
    function elapsedText(iso) {
        if (!iso) return RTL ? 'الآن' : 'now';
        var ms = Date.now() - Date.parse(iso); if (!(ms > 0)) ms = 0;
        var m = Math.floor(ms / 60000); if (m < 1) return RTL ? 'الآن' : 'now';
        var h = Math.floor(m / 60), mm = m % 60;
        return h > 0 ? (RTL ? (h + ' س ' + mm + ' د') : (h + 'h ' + mm + 'm')) : (RTL ? (m + ' د') : (m + 'm'));
    }
    function kColor(s) { return s === 'Ready' ? 'success' : (s === 'Preparing' ? 'warning' : 'primary'); }
    function kLabel(s) { return s === 'Ready' ? L.kReady : (s === 'Preparing' ? L.kPrep : L.kNew); }

    function cbConfirm(text, danger) {
        if (window.CB && window.CB.confirm) return window.CB.confirm({ type: 'generic', icon: 'question', title: TL('تأكيد', 'Confirm'), text: text, confirmText: TL('تأكيد', 'Confirm'), confirmClass: danger ? 'btn btn-success' : 'btn btn-primary' });
        return Promise.resolve(window.confirm(text));
    }
    function toastOk(m) { if (window.CB && window.CB.toast) window.CB.toast.success(m); }
    function toastErr(m) { if (window.CB && window.CB.toast) window.CB.toast.error(m); }

    function render() {
        if (countEl) countEl.textContent = orders.length;
        if (!orders.length) { grid.innerHTML = ''; if (emptyEl) emptyEl.classList.remove('d-none'); return; }
        if (emptyEl) emptyEl.classList.add('d-none');
        grid.innerHTML = orders.map(function (o) {
            var ds = o.deliveryStatus;   // null | OutForDelivery | Delivered
            var isOut = ds === 'OutForDelivery', isDelivered = ds === 'Delivered';
            var kReady = o.kitchenStatus === 'Ready';
            // top-right badge: delivery status if set, else kitchen status
            var badgeColor = isDelivered ? 'success' : (isOut ? 'info' : kColor(o.kitchenStatus));
            var badgeLabel = isDelivered ? L.deliveredDone : (isOut ? L.out : kLabel(o.kitchenStatus));

            var addr = '<div class="d-flex flex-column gap-1 mb-4">'
                + (o.area ? '<span class="fs-7 text-gray-700"><i class="ki-outline ki-geolocation fs-6 text-primary me-2"></i>' + esc(o.area) + '</span>' : '')
                + (o.address ? '<span class="fs-7 text-gray-600"><i class="ki-outline ki-home-2 fs-6 text-muted me-2"></i>' + esc(o.address) + '</span>' : '')
                + (o.phone ? '<span class="fs-7 text-gray-600"><i class="ki-outline ki-phone fs-6 text-muted me-2"></i>' + esc(o.phone) + '</span>' : '')
                + '</div>';

            var stats = '<div class="d-flex flex-wrap mb-4">'
                + '<div class="border border-gray-300 border-dashed rounded min-w-90px py-2 px-4 me-3 mb-2"><div class="fs-6 text-gray-800 fw-bold kt-time" data-since="' + esc(o.sinceUtcIso) + '">' + elapsedText(o.sinceUtcIso) + '</div><div class="fw-semibold text-gray-500 fs-8">' + esc(L.since) + '</div></div>'
                + '<div class="border border-gray-300 border-dashed rounded min-w-90px py-2 px-4 mb-2"><div class="fs-6 text-gray-800 fw-bold">' + o.itemCount + ' <span class="fs-8 text-muted">' + esc(L.items) + '</span></div><div class="fw-semibold text-gray-500 fs-8">' + (o.grandTotal != null ? o.grandTotal : '') + '</div></div>'
                + '</div>';

            // driver: current name + a dropdown of active drivers (assign)
            var drvItems = DRIVERS.map(function (d) { return '<div class="menu-item px-2"><a href="#" class="menu-link px-3 py-2 dv-driver" data-order="' + o.orderId + '" data-driver="' + d.id + '"><span class="bullet bullet-dot bg-primary me-3"></span>' + esc(d.name) + '</a></div>'; }).join('');
            if (o.driverId) drvItems += '<div class="separator my-1"></div><div class="menu-item px-2"><a href="#" class="menu-link px-3 py-2 dv-driver text-muted" data-order="' + o.orderId + '" data-driver=""><span class="bullet bullet-dot bg-gray-400 me-3"></span>' + TL('بدون سائق', 'No driver') + '</a></div>';
            var driverRow = '<div class="d-flex align-items-center justify-content-between mb-4 pt-2 border-top border-gray-200 border-dashed">'
                + '<span class="fs-7 text-muted"><i class="ki-outline ki-scooter fs-5 me-2"></i>' + esc(L.driver) + '</span>'
                + '<div><button type="button" class="btn btn-sm ' + (o.driverId ? 'btn-light-primary' : 'btn-light') + ' py-1 px-3 d-flex align-items-center" data-kt-menu-trigger="click" data-kt-menu-placement="bottom-end">'
                + (o.driverName ? esc(o.driverName) : esc(L.pickDriver)) + '<i class="ki-outline ki-down fs-8 ms-2"></i></button>'
                + '<div class="menu menu-sub menu-sub-dropdown menu-column menu-rounded menu-gray-800 menu-state-bg-light-primary fw-semibold w-175px py-2" data-kt-menu="true">'
                + '<div class="menu-item px-3"><div class="menu-content text-muted fs-8 text-uppercase px-3 pb-1">' + esc(L.pickDriver) + '</div></div>'
                + (drvItems || ('<div class="menu-item px-3"><span class="menu-link text-muted">—</span></div>')) + '</div></div></div>';

            // footer action by delivery status
            var foot;
            if (isDelivered) {
                foot = '<div class="card-footer py-3 d-flex align-items-center justify-content-center gap-2"><span class="badge badge-light-success py-2 px-3"><i class="ki-outline ki-check-circle fs-6 me-1"></i>' + esc(L.deliveredDone) + '</span><span class="badge badge-light-warning py-2 px-3">' + esc(L.awaitingPay) + '</span></div>';
            } else if (isOut) {
                foot = '<div class="card-footer py-3"><button type="button" class="btn btn-sm btn-success w-100 dv-status" data-order="' + o.orderId + '" data-status="Delivered"><i class="ki-outline ki-check-circle fs-5 me-1"></i>' + esc(L.delivered) + '</button></div>';
            } else {
                foot = '<div class="card-footer py-3"><button type="button" class="btn btn-sm btn-primary w-100 dv-status' + (kReady ? '' : ' disabled') + '" data-order="' + o.orderId + '" data-status="OutForDelivery"' + (kReady ? '' : ' disabled title="' + esc(L.notReady) + '"') + '><i class="ki-outline ki-delivery fs-5 me-1"></i>' + esc(kReady ? L.out : L.notReady) + '</button></div>';
            }

            return '<div class="col-md-6 col-xl-4"><div class="card hover-elevate-up shadow-xs h-100" data-order="' + o.orderId + '">'
                + '<div class="card-header border-0 pt-7">'
                + '<div class="card-title m-0"><div class="symbol symbol-45px w-45px bg-light-' + badgeColor + ' me-3"><span class="symbol-label"><i class="ki-outline ki-scooter fs-2 text-' + badgeColor + '"></i></span></div>'
                + '<div class="d-flex flex-column"><span class="fs-4 fw-bold text-gray-900">' + (o.customerName ? esc(o.customerName) : TL('عميل', 'Customer')) + '</span><span class="fs-7 fw-semibold text-gray-500">' + esc(L.order) + ' #' + o.orderId + '</span></div></div>'
                + '<div class="card-toolbar"><span class="badge badge-light-' + badgeColor + ' fw-bold px-4 py-3">' + esc(badgeLabel) + '</span></div></div>'
                + '<div class="card-body pt-3 pb-4 px-8">' + stats + addr + driverRow + '</div>'
                + foot + '</div></div>';
        }).join('');
        if (window.KTMenu && typeof KTMenu.createInstances === 'function') { try { KTMenu.createInstances(); } catch (e) { } }
    }

    var busy = false;
    function assignDriver(orderId, driverId) {
        if (busy) return; busy = true;
        cbConfirm(L.confirmDriver, false).then(function (ok) {
            if (!ok) { busy = false; return; }
            post(U.driver, { orderId: orderId, driverId: driverId }).then(function (j) {
                busy = false;
                if (j.ok) { toastOk(TL('تم تعيين السائق', 'Driver assigned')); load(); } else toastErr(j.error || '');
            }).catch(function () { busy = false; toastErr(TL('تعذّر الاتصال', 'Connection failed')); });
        });
    }
    function advance(orderId, status) {
        if (busy) return; busy = true;
        var toDelivered = status === 'Delivered';
        cbConfirm(toDelivered ? L.confirmDelivered : L.confirmOut, toDelivered).then(function (ok) {
            if (!ok) { busy = false; return; }
            post(U.status, { orderId: orderId, status: status }).then(function (j) {
                busy = false;
                if (j.ok) { toastOk(toDelivered ? TL('تم تسليم الطلب', 'Order delivered') : TL('خرج الطلب للتوصيل', 'Order out for delivery')); load(); }
                else toastErr(j.error || '');
            }).catch(function () { busy = false; toastErr(TL('تعذّر الاتصال', 'Connection failed')); });
        });
    }

    grid.addEventListener('click', function (e) {
        var dv = e.target.closest('.dv-driver');
        if (dv) { e.preventDefault(); var did = dv.getAttribute('data-driver'); assignDriver(parseInt(dv.getAttribute('data-order')), did === '' ? '' : parseInt(did)); return; }
        var st = e.target.closest('.dv-status');
        if (st && !st.classList.contains('disabled')) { advance(parseInt(st.getAttribute('data-order')), st.getAttribute('data-status')); return; }
    });

    function load() {
        return fetch(U.orders, { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' }).then(function (r) { return r.json(); })
            .then(function (j) { if (j && j.ok) { orders = j.orders || []; render(); } }).catch(function () { });
    }
    var rb = document.getElementById('delivRefresh'); if (rb) rb.addEventListener('click', load);
    setInterval(function () { grid.querySelectorAll('.kt-time').forEach(function (el) { var s = el.getAttribute('data-since'); if (s) el.textContent = elapsedText(s); }); }, 1000);
    load();

    // real-time: any relevant PosHub event on this branch → re-fetch (authoritative). Poll fallback.
    var live = false;
    if (window.signalR) {
        try {
            var conn = new signalR.HubConnectionBuilder().withUrl('/hubs/pos').withAutomaticReconnect().build();
            ['OrderSentToKitchen', 'LineKdsStatusChanged', 'OrderReady', 'OrderOutForDelivery', 'OrderDelivered', 'OrderPaid'].forEach(function (ev) { conn.on(ev, load); });
            conn.onreconnecting(function () { live = false; });
            conn.onreconnected(function () { live = true; load(); });
            conn.onclose(function () { live = false; });
            conn.start().then(function () { live = true; }).catch(function () { live = false; });
        } catch (e) { live = false; }
    }
    (function tick() { setTimeout(function () { load(); tick(); }, live ? 15000 : 4000); })();
})();
