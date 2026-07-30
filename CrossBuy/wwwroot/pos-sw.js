// CrossBuy Cashier — POS-9a service worker (offline shell). Runtime caching only (no fragile precache list).
// Scope: /pos/ (served from root with Service-Worker-Allowed: /pos/). Intercepts ONLY the cashier page + its
// static assets + the offline bundle; everything else passes straight through to the network.
const CACHE = 'cb-pos-v1';

self.addEventListener('install', function (e) { self.skipWaiting(); });

self.addEventListener('activate', function (e) {
    e.waitUntil((async function () {
        var keys = await caches.keys();
        await Promise.all(keys.filter(function (k) { return k.indexOf('cb-pos-') === 0 && k !== CACHE; }).map(function (k) { return caches.delete(k); }));
        await self.clients.claim();
    })());
});

// which requests this SW handles (cashier scope). Others -> untouched (default network).
function isAsset(url) {
    return url.pathname.indexOf('/Backend-assets/') === 0
        || url.pathname.indexOf('/uploads/') === 0
        || url.pathname === '/pos.webmanifest'
        || url.pathname === '/favicon.ico'
        || /\.(css|js|png|jpg|jpeg|svg|webp|avif|woff2?|ttf|eot)$/i.test(url.pathname);
}
function isCashierPage(url) { return url.pathname === '/pos/terminal' || url.pathname === '/pos/start'; }
function isBundle(url) { return url.pathname === '/pos/offline/bundle'; }

self.addEventListener('fetch', function (e) {
    var req = e.request;
    if (req.method !== 'GET') return;                 // never cache writes
    var url = new URL(req.url);
    if (url.origin !== self.location.origin) return;  // same-origin only

    // 1) cashier page + offline bundle: NETWORK-FIRST, fall back to cache when offline
    if (isCashierPage(url) || isBundle(url)) {
        e.respondWith((async function () {
            try {
                var fresh = await fetch(req);
                if (fresh && fresh.ok) { var c = await caches.open(CACHE); c.put(req, fresh.clone()); }
                return fresh;
            } catch (err) {
                var cached = await caches.match(req);
                if (cached) return cached;
                // last resort for a navigation: the cached terminal page
                if (req.mode === 'navigate') { var t = await caches.match('/pos/terminal'); if (t) return t; }
                throw err;
            }
        })());
        return;
    }

    // 2) static assets: CACHE-FIRST + refresh in background (stale-while-revalidate)
    if (isAsset(url)) {
        e.respondWith((async function () {
            var cached = await caches.match(req);
            var network = fetch(req).then(function (res) {
                if (res && res.ok) { caches.open(CACHE).then(function (c) { c.put(req, res.clone()); }); }
                return res;
            }).catch(function () { return null; });
            return cached || (await network) || Response.error();
        })());
        return;
    }
    // 3) anything else (admin, /pos writes, APIs): passthrough — do not intercept
});
