// CrossBuy Cashier — POS-9b local order engine. Builds the OPEN order entirely offline from the cached bundle
// (IndexedDB 'order' store), mirroring the server's open-order pricing (RC-4b fold + Recompute). ZERO accounting —
// the open order has no GL/stock (as since RC-2). Pay + sync-queue land in 9c/9d. Loaded AFTER pos-offline.js.
(function () {
    'use strict';
    if (!window.CBOffline) return;
    var db = function () { return window.CBOffline.db(); };
    function tx(store, mode, fn) { return db().then(function (d) { return new Promise(function (res, rej) { var t = d.transaction(store, mode); var r = fn(t.objectStore(store)); t.oncomplete = function () { res(r && r.result !== undefined ? r.result : undefined); }; t.onerror = function () { rej(t.error); }; }); }); }
    function put(o) { return tx('order', 'readwrite', function (s) { s.put(o); }); }
    function get1() { return tx('order', 'readonly', function (s) { return s.get('current'); }); }
    function del1() { return tx('order', 'readwrite', function (s) { s.delete('current'); }); }
    // meta (receipt counter) + queue (settled orders, FIFO)
    function metaGet(key) { return tx('meta', 'readonly', function (s) { return s.get(key); }); }
    function metaPut(o) { return tx('meta', 'readwrite', function (s) { s.put(o); }); }
    function queuePush(entry) { return tx('queue', 'readwrite', function (s) { s.add(entry); }); }
    function queueAll() { return db().then(function (d) { return new Promise(function (res, rej) { var out = []; var t = d.transaction('queue', 'readonly'); var cur = t.objectStore('queue').openCursor(); cur.onsuccess = function (e) { var c = e.target.result; if (c) { var v = c.value; v.seq = c.key; out.push(v); c.continue(); } else res(out); }; cur.onerror = function () { rej(cur.error); }; }); }); }
    function guid() { try { return crypto.randomUUID(); } catch (e) { return 'g-' + Date.now() + '-' + Math.floor(Math.random() * 1e9); } }
    function pad6(n) { n = String(n); while (n.length < 6) n = '0' + n; return n; }

    var R = function (n) { return Math.round((n || 0) * 100) / 100; };
    var bundle = null, order = null, seq = 0;

    function ensureBundle() { if (bundle) return Promise.resolve(bundle); return window.CBOffline.getBundle().then(function (b) { bundle = b || {}; return bundle; }); }
    function itemMap() { var m = {}; if (bundle && bundle.menu) bundle.menu.forEach(function (g) { (g.items || []).forEach(function (it) { m[it.itemId] = it; }); }); return m; }
    function taxRate(itemId) { var t = (bundle && bundle.taxByItem) ? bundle.taxByItem[itemId] : null; return (t != null) ? t : ((bundle && bundle.defaultVatRate) || 0); }
    function hasMods(itemId) { return !!(bundle && bundle.modItemIds && bundle.modItemIds.indexOf(itemId) >= 0); }
    function modGroups(itemId) { return (bundle && bundle.modifiers && bundle.modifiers[itemId]) || []; }

    function recompute() {
        if (!order) return;
        var sub = 0, tax = 0;
        var svcPct = (bundle && bundle.setting && bundle.setting.serviceChargePct) || 0;
        var dv = (bundle && bundle.defaultVatRate) || 0;
        order.lines.forEach(function (l) { l.lineTotal = R(l.qty * l.unitPrice - (l.discountAmount || 0)); sub += l.lineTotal; tax += R(l.lineTotal * l.taxRate / 100); });
        order.subTotal = R(sub);
        order.serviceAmount = svcPct > 0 ? R(sub * svcPct / 100) : 0;
        if (order.serviceAmount > 0) tax += R(order.serviceAmount * dv / 100);
        order.taxTotal = R(tax);
        order.grandTotal = R(order.subTotal + order.serviceAmount + order.taxTotal);
    }
    function persist() { if (!order) return Promise.resolve(); order.id = 'current'; return put(order); }

    var CBLO = {
        ready: null,
        getOrder: function () { return order; },
        create: function (orderType, tableId) {
            return ensureBundle().then(function () {
                order = { id: 'current', status: 'Open', orderType: orderType || 'Takeaway', tableId: tableId || null, guests: 1, lines: [], subTotal: 0, serviceAmount: 0, taxTotal: 0, grandTotal: 0, offline: true };
                seq = 0; return persist().then(function () { return order; });
            });
        },
        ensure: function (orderType, tableId) { return order ? Promise.resolve(order) : CBLO.create(orderType, tableId); },
        itemModifiers: function (itemId) { return ensureBundle().then(function () { return modGroups(itemId); }); },
        addLine: function (itemId, qty, optionIds) {
            qty = qty > 0 ? qty : 1;
            return ensureBundle().then(function () { return CBLO.ensure(); }).then(function () {
                var it = itemMap()[itemId]; if (!it) return order;
                var extras = 0, mods = [];
                if (optionIds && optionIds.length) {
                    var byOpt = {}; modGroups(itemId).forEach(function (g) { (g.options || []).forEach(function (o) { byOpt[o.optionId] = o; }); });
                    optionIds.forEach(function (oid) { var o = byOpt[oid]; if (o) { extras += (o.extraPrice || 0); mods.push({ optionId: o.optionId, name: o.name, extraPrice: o.extraPrice || 0 }); } });
                }
                var unit = R((it.price || 0) + extras), tr = taxRate(itemId);
                // an item WITH modifier groups never merges (RC-4b); plain items merge into a same-item no-modifier line
                var existing = (!hasMods(itemId) && (!optionIds || !optionIds.length)) ? order.lines.filter(function (l) { return l.itemId === itemId && (!l.modifiers || !l.modifiers.length); })[0] : null;
                if (existing) existing.qty += qty;
                else order.lines.push({ id: (--seq), itemId: itemId, name: it.name, image: (bundle.images && bundle.images[itemId]) || null, qty: qty, unitPrice: unit, discountAmount: 0, taxRate: tr, lineTotal: R(unit * qty), sentQty: 0, kdsStatus: null, modifiers: mods, optionIds: (optionIds || []) });
                recompute(); return persist().then(function () { return order; });
            });
        },
        setQty: function (lineId, q) {
            if (!order) return Promise.resolve(order);
            if (q <= 0) order.lines = order.lines.filter(function (x) { return x.id !== lineId; });
            else { var l = order.lines.filter(function (x) { return x.id === lineId; })[0]; if (l) l.qty = q; }
            recompute(); return persist().then(function () { return order; });
        },
        removeLine: function (lineId) { if (!order) return Promise.resolve(order); order.lines = order.lines.filter(function (x) { return x.id !== lineId; }); recompute(); return persist().then(function () { return order; }); },
        discard: function () { order = null; return del1(); },

        // POS-9c: next LOCAL receipt number for this terminal (prefix isolates it globally; seeded from the bundle's
        // server counter on first offline use, then incremented locally in IDB). Distinct from the accounting invoice no.
        nextReceipt: function () {
            return ensureBundle().then(function () {
                var t = bundle.terminal || {}, prefix = t.receiptPrefix || 'T-';
                return metaGet('receiptCounter').then(function (m) {
                    var next = (m && m.value != null) ? m.value : (t.nextReceiptNo || 1);   // seed from server counter once
                    return metaPut({ id: 'receiptCounter', value: next + 1 }).then(function () { return prefix + pad6(next); });
                });
            });
        },
        // POS-9c: finalize the current order OFFLINE — assign a local receipt no + enqueue the settled order (paid) for
        // replay on reconnect (9d). NO GL/stock here. tip = {amount, method} or null. Returns {snapshot, receiptNo}.
        pay: function (method, tip) {
            if (!order || !order.lines.length) return Promise.reject(new Error('empty'));
            return CBLO.nextReceipt().then(function (rno) {
                var snap = JSON.parse(JSON.stringify(order));
                snap.receiptNo = rno; snap.status = 'Paid';
                var entry = {
                    kind: 'paid-order',
                    localGuid: guid(), terminalId: bundle.terminalId, shiftId: bundle.shiftId,
                    orderType: order.orderType, tableId: order.tableId || null, customerId: order.customerId || null,
                    method: method || 'Cash',
                    tip: (tip && tip.amount > 0) ? { amount: tip.amount, method: tip.method || 'Cash' } : null,
                    receiptNo: rno,
                    lines: order.lines.map(function (l) { return { itemId: l.itemId, qty: l.qty, unitPrice: l.unitPrice, discountAmount: l.discountAmount || 0, taxRate: l.taxRate, optionIds: l.optionIds || [] }; }),
                    subTotal: order.subTotal, serviceAmount: order.serviceAmount, taxTotal: order.taxTotal, grandTotal: order.grandTotal,
                    paidAtMs: Date.now(), offline: true
                };
                return queuePush(entry).then(function () { return del1(); }).then(function () { order = null; return { snapshot: snap, receiptNo: rno, entry: entry }; });
            });
        },
        queueList: function () { return queueAll(); },
        dequeue: function (seq) { return tx('queue', 'readwrite', function (s) { s.delete(seq); }); },   // POS-9d: remove a replayed entry
        // POS-9e: queue an OFFLINE shift close — the server posts the variance JE at sync (no accounting on the device).
        queueShiftClose: function (closingFloat) {
            return ensureBundle().then(function () {
                var entry = { kind: 'shift-close', terminalId: bundle.terminalId, shiftId: bundle.shiftId, closingFloat: closingFloat || 0, queuedAtMs: Date.now() };
                return queuePush(entry).then(function () { return entry; });
            });
        }
    };
    // load any persisted open order (survives reload)
    CBLO.ready = ensureBundle().then(get1).then(function (o) {
        if (o) { order = o; var mn = 0; (o.lines || []).forEach(function (l) { if (l.id < mn) mn = l.id; }); seq = mn; }
    }).catch(function () { });
    window.CBLO = CBLO;
})();
