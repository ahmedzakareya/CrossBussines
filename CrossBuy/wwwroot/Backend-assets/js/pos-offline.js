// CrossBuy Cashier — POS-9a offline layer (shell + bundle cache). NO accounting/stock — data caching only.
// Registers the service worker, pulls the "terminal bundle" into IndexedDB while online (auto on load + periodic
// + manual button), and shows an online/offline indicator. The local order engine (9b) reads window.CBOffline.getBundle().
(function () {
    'use strict';
    var DB = 'cbpos', DBV = 3, STORE = 'bundle', REFRESH_MS = 4 * 60 * 1000;

    // ---- IndexedDB (shared cbpos db: 'bundle' [9a] + 'order' [9b] + 'queue'/'meta' [9c]) ----
    function idb() {
        return new Promise(function (res, rej) {
            var r = indexedDB.open(DB, DBV);
            r.onupgradeneeded = function () {
                var db = r.result;
                if (!db.objectStoreNames.contains('bundle')) db.createObjectStore('bundle', { keyPath: 'id' });
                if (!db.objectStoreNames.contains('order')) db.createObjectStore('order', { keyPath: 'id' });
                if (!db.objectStoreNames.contains('queue')) db.createObjectStore('queue', { keyPath: 'seq', autoIncrement: true });   // FIFO settled orders
                if (!db.objectStoreNames.contains('meta')) db.createObjectStore('meta', { keyPath: 'id' });                          // local receipt counter etc.
            };
            r.onsuccess = function () { res(r.result); };
            r.onerror = function () { rej(r.error); };
        });
    }
    function idbPut(obj) { return idb().then(function (db) { return new Promise(function (res, rej) { var tx = db.transaction(STORE, 'readwrite'); tx.objectStore(STORE).put(obj); tx.oncomplete = function () { res(); }; tx.onerror = function () { rej(tx.error); }; }); }); }
    function idbGet(id) { return idb().then(function (db) { return new Promise(function (res, rej) { var tx = db.transaction(STORE, 'readonly'); var g = tx.objectStore(STORE).get(id); g.onsuccess = function () { res(g.result); }; g.onerror = function () { rej(g.error); }; }); }); }

    // ---- online/offline indicator ----
    function setStatus(online, note) {
        var txt = document.getElementById('cbOffTxt');
        var pill = document.getElementById('cbOffRefresh');
        if (pill) { pill.classList.toggle('is-online', !!online); pill.classList.toggle('is-offline', !online); }
        if (txt) txt.textContent = note || (online ? (txt.getAttribute('data-on') || 'Online') : (txt.getAttribute('data-off') || 'Offline'));
    }

    // ---- pull the bundle (online only) ----
    function refreshBundle() {
        if (!navigator.onLine) { setStatus(false); return Promise.resolve(null); }
        return fetch('/pos/offline/bundle', { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (j) {
                if (j && j.ok) { j.id = 'current'; return idbPut(j).then(function () { setStatus(true); return j; }); }
                setStatus(navigator.onLine); return null;
            })
            .catch(function () { setStatus(navigator.onLine); return null; });
    }

    // ---- POS-9d: flush the offline queue on reconnect (FIFO; each replayed order removed; stop on first failure) ----
    var flushing = false;
    function flushQueue() {
        if (flushing || !navigator.onLine || !window.CBLO) return Promise.resolve();
        flushing = true;
        return window.CBLO.queueList().then(function (list) {
            return (function next(i) {
                if (i >= list.length) return;
                var e = list[i];
                var url = e.kind === 'shift-close' ? '/pos/sync/shift-close' : '/pos/sync/paid-order';   // POS-9e: route by kind
                return fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'fetch' }, body: JSON.stringify(e) })
                    .then(function (r) { return r.ok ? r.json() : null; })
                    .then(function (j) {
                        if (j && j.ok) { return window.CBLO.dequeue(e.seq).then(function () { return next(i + 1); }); }
                        // server rejected → stop, keep FIFO order for a later retry
                    });
            })(0);
        }).catch(function () { }).finally(function () { flushing = false; });
    }

    // public API for the local order engine (9b/9c/9d)
    window.CBOffline = {
        getBundle: function () { return idbGet('current'); },
        refresh: refreshBundle,
        isOnline: function () { return navigator.onLine; },
        db: idb,                                   // shared cbpos handle (9b uses the 'order' store)
        flush: flushQueue                          // POS-9d: replay queued offline orders
    };

    // ---- service worker ----
    function registerSW() {
        if (!('serviceWorker' in navigator)) return;
        navigator.serviceWorker.register('/pos-sw.js', { scope: '/pos/' }).catch(function (e) { /* SW optional; app still works online */ });
    }

    function boot() {
        registerSW();
        setStatus(navigator.onLine);
        refreshBundle();                                  // cache latest while online
        setTimeout(flushQueue, 1500);                     // POS-9d: replay any queued offline orders on load (if online)
        setInterval(function () { refreshBundle(); flushQueue(); }, REFRESH_MS);   // periodic refresh + flush
        window.addEventListener('online', function () { setStatus(true); refreshBundle(); flushQueue(); });   // POS-9d: sync on reconnect
        window.addEventListener('offline', function () { setStatus(false); });
        var btn = document.getElementById('cbOffRefresh');
        if (btn) btn.addEventListener('click', function (e) { e.preventDefault(); setStatus(navigator.onLine, btn.getAttribute('data-busy') || '...'); refreshBundle().then(function () { setStatus(navigator.onLine); }); });
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot); else boot();
})();
