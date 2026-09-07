// POS cashier terminal — pure JS (NO Razor). Reads config from window.POS.
// Metronic POS theme: category pills + item grid + order table w/ dialer + green summary.
// Payment happens in a MODAL with a numeric keypad + live change. Dine-in tables picked from a VISUAL modal grid.
(function () {
    var P = window.POS; if (!P) return;
    var tokenEl = document.querySelector('#af input[name="__RequestVerificationToken"]'); if (!tokenEl) return;
    var token = tokenEl.value;
    var U = P.urls, L = P.L || {}, CUR = P.cur || '', RTL = !!P.rtl, RECEIPT = P.receipt || {}, TERM = P.term || {}, HALLS = P.halls || [];
    var order = P.order || null;
    var orderType = 'Takeaway', tableId = null, tableCode = null;
    var payPaidStr = '', payMethod = 'Cash';
    var linesEl = document.getElementById('ordLines'); if (!linesEl) return;
    var emptyEl = document.getElementById('ordEmpty');
    var elSub = document.getElementById('ordSub'), elSvc = document.getElementById('ordSvc'),
        elTax = document.getElementById('ordTax'), elGrand = document.getElementById('ordGrand'),
        btnPay = document.getElementById('btnPay');
    var busy = false;
    var tapLock = false;   // guards against a double-tap on a table creating two orders (the "counted twice" bug)

    function fmt(n) { return (Math.round((n || 0) * 100) / 100).toFixed(2); }
    function esc(s) { return (s || '').toString().replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
    // hall name: prefer English NameEn when the UI is English (falls back to the Arabic name)
    function hallName(h) { return (!RTL && h && h.nameEn) ? h.nameEn : (h && h.name || ''); }
    // walk-in is a system customer named "عميل نقدي (كاشير)" — show a localized label, never Arabic in an English UI
    function custName(n) { return (n === 'عميل نقدي (كاشير)') ? (L.walkIn || 'Walk-in') : (n || ''); }
    // "منذ 5 د" / "5m ago" from an ISO UTC time (RTL-aware, compact)
    function timeAgo(iso) {
        if (!iso) return '';
        var t = Date.parse(iso); if (isNaN(t)) return '';
        var mins = Math.max(0, Math.floor((Date.now() - t) / 60000));
        var ar = (window.POS && window.POS.rtl);
        if (mins < 1) return ar ? 'الآن' : 'now';
        if (mins < 60) return ar ? ('منذ ' + mins + ' د') : (mins + 'm');
        var h = Math.floor(mins / 60), m = mins % 60;
        return ar ? ('منذ ' + h + ' س' + (m ? ' ' + m + ' د' : '')) : (h + 'h' + (m ? ' ' + m + 'm' : ''));
    }
    function post(u, d) { var fd = new FormData(); fd.append('__RequestVerificationToken', token); Object.keys(d).forEach(function (k) { fd.append(k, d[k]); }); return fetch(u, { method: 'POST', body: fd, headers: { 'X-Requested-With': 'fetch' } }).then(function (r) { return r.json(); }); }
    function toast(k, m) { if (window.toastr) { toastr[k](m); } }
    function grand() { return order && order.grandTotal ? order.grandTotal : 0; }
    function modal(id) { var el = document.getElementById(id); return el ? (bootstrap.Modal.getInstance(el) || new bootstrap.Modal(el)) : null; }

    // ===== live SERVER clock (ticks locally off a one-time server/client offset, so it shows server time) =====
    (function serverClock() {
        var cl = document.getElementById('posClock'), dt = document.getElementById('posDate');
        if (!cl) return;
        var offset = (typeof P.serverNowMs === 'number') ? (P.serverNowMs - Date.now()) : 0;
        var loc = RTL ? 'ar-EG' : 'en-GB';
        function tick() {
            var d = new Date(Date.now() + offset);
            try { cl.textContent = d.toLocaleTimeString(loc, { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: RTL }); }
            catch (e) { cl.textContent = ('0' + d.getHours()).slice(-2) + ':' + ('0' + d.getMinutes()).slice(-2) + ':' + ('0' + d.getSeconds()).slice(-2); }
            if (dt) { try { dt.textContent = d.toLocaleDateString(loc, { weekday: 'long', day: 'numeric', month: 'long' }); } catch (e) { dt.textContent = d.toDateString(); } }
        }
        tick(); setInterval(tick, 1000);
    })();

    // ================= order type + tables =================
    var btnPickTable = document.getElementById('btnPickTable'), tableBadge = document.getElementById('tableBadge'), btnMoveTable = document.getElementById('btnMoveTable'), btnMergeTable = document.getElementById('btnMergeTable');
    var moveMode = false, mergeMode = false, moveOrderId = null, moveSrcCode = '', mergeSrcTable = null, mergeSrcCode = '', menuTable = null;
    function tr(a, b) { return RTL ? a : b; }
    function paintTable() {
        var dineWithTable = (orderType === 'Dine-in' && tableId && order && order.id);
        if (tableBadge) {
            if (orderType === 'Dine-in' && tableId) { tableBadge.textContent = (L.table || 'Table') + ' ' + (tableCode || ''); tableBadge.classList.remove('d-none'); }
            else tableBadge.classList.add('d-none');
        }
        if (btnMoveTable) btnMoveTable.classList.toggle('d-none', !dineWithTable);
        if (btnMergeTable) btnMergeTable.classList.toggle('d-none', !dineWithTable);
    }
    document.querySelectorAll('.otype').forEach(function (b) {
        b.addEventListener('click', function () {
            document.querySelectorAll('.otype').forEach(function (x) { x.classList.remove('active'); });
            b.classList.add('active'); orderType = b.getAttribute('data-type');
            var dine = orderType === 'Dine-in';
            if (btnPickTable) btnPickTable.classList.toggle('d-none', !dine);
            if (!dine) { tableId = null; tableCode = null; }
            paintTable();
        });
    });
    // ===================== FLOOR BOARD =====================
    // A restaurant floor board: hall selector, action rail, tables laid out by their real X/Y/W/H,
    // colour-coded by status (Occupied/AwaitingBill/Reserved/Cleaning/Closed/Available) with a live
    // sit-down timer + seat count, a status legend, and a live stats bar. Robust: never magnifies a
    // table, and falls back to a tidy grid if coordinates are missing.
    var floor = document.getElementById('floorPlan'), hallSel = document.getElementById('hallSel');
    var curHall = HALLS.length ? HALLS[0].id : null, pickedTable = null, boardTimer = null, boardView = 'map';
    // Reference palette: the STATUS is carried by the table's coloured BORDER + a status pill under the number.
    // Chairs are neutral (taupe) regardless of status — exactly like the design.
    var STAT = {
        Available: { c: '#16a34a' },               // green  — فارغة
        Occupied: { c: '#2f6fed' },                // blue   — مشغولة
        Held: { c: '#f59e0b' },                    // orange — معلقة
        Awaiting: { c: '#8b5cf6' },                // purple — بانتظار دفع
        Reserved: { c: '#e5484d' },                // red    — محجوزة
        Cleaning: { c: '#9aa4b2' },                // gray   — قيد التنظيف
        Closed: { c: '#7b8794', dim: true }        // muted + dimmed
    };
    function hexA(hex, a) { var m = (hex || '#000').replace('#', ''); return 'rgba(' + parseInt(m.substr(0, 2), 16) + ',' + parseInt(m.substr(2, 2), 16) + ',' + parseInt(m.substr(4, 2), 16) + ',' + a + ')'; }
    var num = function (v) { var n = Number(v); return isFinite(n) ? n : 0; };
    function curTables() { var h = HALLS.filter(function (x) { return x.id === curHall; })[0]; return h ? (h.tables || []) : []; }
    // effective status for a table (Occupied splits into Occupied / Awaiting-bill after 60 min)
    function effStatus(t) {
        if (t.status === 'Occupied') { return elapsedMin(t) >= 60 ? 'Awaiting' : 'Occupied'; }
        return STAT[t.status] ? t.status : 'Available';
    }
    function elapsedMin(t) { if (!t.sinceUtc) return 0; var ms = Date.now() - Date.parse(t.sinceUtc); return ms > 0 ? ms / 60000 : 0; }
    function timerStr(t) { var m = Math.floor(elapsedMin(t)); return ('0' + Math.floor(m / 60)).slice(-2) + ':' + ('0' + (m % 60)).slice(-2); }
    function statusLabel(s) { return { Occupied: L.occupied, Held: L.heldTable, Awaiting: L.awaiting, Reserved: L.reserved, Cleaning: L.cleaning, Closed: L.closed, Available: L.available }[s] || ''; }

    var CM = 34;   // chair margin around each table surface (px)
    // One cushioned chair (seat cushion + a slim backrest behind it), rotated so the backrest faces OUTWARD.
    // col = null → neutral gray (free seat); col = status colour → an OCCUPIED (seated) chair.
    function oneChair(cx, cy, sw, rotDeg, col) {
        var sh = Math.round(sw * 1.16);
        // realistic top-down dining chair drawn in SVG: a curved backrest arc + a rounded seat cushion.
        var seatC = col || '#d2c4a4', backC = col ? hexA(col, .82) : '#b1a181', hi = col ? 'rgba(255,255,255,.28)' : 'rgba(255,255,255,.5)';
        var svg = '<svg width="' + sw + '" height="' + sh + '" viewBox="0 0 26 30" style="display:block;overflow:visible">'
            + '<path d="M3.5 3 Q13 -1.5 22.5 3 L22 10 Q13 6.5 4 10 Z" fill="' + backC + '"/>'
            + '<rect x="1.5" y="8" width="23" height="20" rx="7.5" fill="' + seatC + '"/>'
            + '<rect x="4.5" y="11" width="17" height="9" rx="5" fill="' + hi + '"/>'
            + '</svg>';
        return '<span style="position:absolute;left:' + (cx - sw / 2) + 'px;top:' + (cy - sh / 2) + 'px;width:' + sw + 'px;height:' + sh + 'px;transform:rotate(' + rotDeg + 'deg);transform-origin:center;filter:drop-shadow(0 2px 3px rgba(0,0,0,.28))">' + svg + '</span>';
    }
    // How many chairs sit on each side [top, right, bottom, left]. Longer sides get more; a 2-seater
    // faces each other on the two opposite long sides; larger counts spread proportionally.
    function sideCounts(seats, w, h) {
        if (seats <= 0) return [0, 0, 0, 0];
        var tall = h > w * 1.1;
        if (seats === 1) return tall ? [0, 1, 0, 0] : [1, 0, 0, 0];
        if (seats === 2) return tall ? [0, 1, 0, 1] : [1, 0, 1, 0];   // opposite sides → facing each other
        var weights = [w, h, w, h], total = 2 * (w + h);
        var raw = weights.map(function (x) { return seats * x / total; });
        var base = raw.map(function (x) { return Math.floor(x); });
        var rem = seats - (base[0] + base[1] + base[2] + base[3]);
        var order = [0, 1, 2, 3].sort(function (a, b) { return (raw[b] - base[b]) - (raw[a] - base[a]); });
        for (var k = 0; k < rem; k++) base[order[k % 4]]++;
        return base;
    }
    // Draw `seats` chairs hugging the table. The first `occN` chairs get the OCCUPIED colour (a seated guest),
    // the rest stay neutral. Chairs are CENTRED on each side (no chairs stuck on corners).
    function chairsHtml(seats, w, h, round, col, occN) {
        seats = Math.max(0, seats | 0); if (!seats) return '';
        occN = Math.max(0, Math.min(occN == null ? 0 : occN, seats));
        var out = '', i, sw = 26, gap = 9;
        function cc(i) { return i < occN ? col : null; }
        if (round) {
            var R = Math.min(w, h) / 2 + 16, cx = w / 2, cy = h / 2;
            for (i = 0; i < seats; i++) {
                var a = -Math.PI / 2 + i * 2 * Math.PI / seats;
                out += oneChair(cx + R * Math.cos(a), cy + R * Math.sin(a), sw, a * 180 / Math.PI + 90, cc(i));
            }
            return out;
        }
        var cnt = sideCounts(seats, w, h), ci = 0;
        function place(k, side) {
            for (var j = 0; j < k; j++) {
                var x, y, rot;
                if (side === 0) { x = (j + 1) * w / (k + 1); y = -gap; rot = 0; }           // top
                else if (side === 1) { x = w + gap; y = (j + 1) * h / (k + 1); rot = 90; }  // right
                else if (side === 2) { x = (j + 1) * w / (k + 1); y = h + gap; rot = 180; } // bottom
                else { x = -gap; y = (j + 1) * h / (k + 1); rot = 270; }                    // left
                out += oneChair(x, y, sw, rot, ci++ < occN ? col : null);
            }
        }
        place(cnt[0], 0); place(cnt[1], 1); place(cnt[2], 2); place(cnt[3], 3);
        return out;
    }
    // A table matching the reference: WOOD-GRAIN top + thin status-coloured border, big number,
    // a SOLID status pill (white text) under it, and a faint seat-count label beneath.
    function tableCard(left, top, w, h, t, round) {
        var s = effStatus(t), c = STAT[s] || STAT.Available, occ = (s === 'Occupied' || s === 'Awaiting'), col = c.c;
        // occupied tables get a bright WHITE top (like tables 3 & 4 in the reference); free/held/reserved keep a warm oak WOOD top.
        var topBg = occ
            ? 'background:linear-gradient(158deg,#ffffff,#eef1f6)'
            : 'background:'
                + 'repeating-linear-gradient(90deg,rgba(120,78,32,.15) 0 1px,rgba(120,78,32,.04) 1px 3px,transparent 3px 7px),'  // grain
                + 'repeating-linear-gradient(90deg,rgba(95,60,18,.10) 0 2px,transparent 2px 38px),'                              // plank seams
                + 'linear-gradient(155deg,#ead2a0,#d4b57e)';
        var surface = topBg + ';border:2.5px solid ' + col + ';box-shadow:0 10px 24px rgba(70,50,20,.24),inset 0 1px 0 rgba(255,255,255,.5)' + (occ ? ',0 0 0 4px ' + hexA(col, .15) : '') + (c.dim ? ';opacity:.6;filter:grayscale(.3)' : '');
        var occAttrs = occ ? (' draggable="true" data-order="' + (t.orderId || '') + '" data-cust="' + esc(custName(t.customerName)) + '" data-count="' + (t.orderCount || 1) + '"') : '';
        var menuIcon = '<span class="tbl-menu" data-id="' + t.id + '" style="position:absolute;top:6px;' + (RTL ? 'left' : 'right') + ':6px;width:24px;height:24px;border-radius:50%;background:rgba(255,255,255,.7);display:flex;align-items:center;justify-content:center;cursor:pointer;z-index:3"><i class="ki-outline ki-dots-vertical fs-6" style="color:' + col + '"></i></span>';
        var cntBadge = (occ && (t.orderCount || 1) > 1) ? ('<span style="position:absolute;top:6px;' + (RTL ? 'right' : 'left') + ':6px;min-width:20px;height:20px;padding:0 5px;border-radius:10px;background:' + col + ';color:#fff;font-size:11px;font-weight:800;display:flex;align-items:center;justify-content:center;z-index:3">' + t.orderCount + '</span>') : '';
        var numSize = Math.round(Math.min(w, h) * 0.42);
        var pill = '<span style="margin-top:7px;padding:3px 15px;border-radius:999px;background:' + col + ';color:#fff;font-size:11.5px;font-weight:800;line-height:1.5;box-shadow:0 2px 7px ' + hexA(col, .4) + '">' + statusLabel(s) + '</span>';
        // POS-B2: reserved tables carry the reservation for the arrive/no-show action + show guest·time
        var resvAttrs = (t.status === 'Reserved') ? (' data-resv="' + (t.reservationId || '') + '" data-resvfor="' + esc(t.reservedFor || '') + '" data-resvat="' + esc(t.reservedAt || '') + '"') : '';
        var resvCap = (t.status === 'Reserved' && (t.reservedFor || t.reservedAt)) ? ('<span style="margin-top:3px;font-size:9.5px;color:#fff;background:' + hexA(col, .85) + ';padding:1px 8px;border-radius:8px;font-weight:700;max-width:96%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">' + (t.reservedAt ? (esc(t.reservedAt) + ' · ') : '') + esc(t.reservedFor || '') + '</span>') : '';
        var label = '<span style="position:absolute;bottom:-34px;left:-8px;right:-8px;text-align:center;font-size:10px;color:#9a8e74;font-weight:700;text-shadow:0 1px 2px rgba(244,236,221,.9)">' + tr('طاولة ', 'Table ') + esc(t.code) + ' · ' + t.seats + ' ' + (L.person || '') + '</span>';
        return '<div class="tseat" style="position:absolute;left:' + left + 'px;top:' + top + 'px;width:' + (w + CM * 2) + 'px;height:' + (h + CM * 2) + 'px">'
            + '<div class="tcell d-flex flex-column flex-center text-center" data-id="' + t.id + '" data-code="' + esc(t.code) + '" data-status="' + t.status + '" data-seats="' + t.seats + '"' + occAttrs + resvAttrs + ' '
            + 'style="position:absolute;left:' + CM + 'px;top:' + CM + 'px;width:' + w + 'px;height:' + h + 'px;cursor:pointer;' + (round ? 'border-radius:50%;' : 'border-radius:18px;') + surface + ';overflow:visible">'
            + chairsHtml(t.seats, w, h, round, col, occ ? Math.max(1, Math.min(t.guests || t.orderCount || 1, t.seats)) : 0) + menuIcon + cntBadge
            + '<span style="font-size:' + numSize + 'px;font-weight:800;color:#33302b;line-height:1;text-shadow:0 1px 0 rgba(255,255,255,.4)">' + esc(t.code) + '</span>'
            + pill + resvCap + label
            + '</div></div>';
    }

    function renderBoard() {
        if (!floor) return;
        var tables = curTables(), emptyT = document.getElementById('tablesEmpty');
        if (!tables.length) { floor.innerHTML = ''; floor.style.display = 'none'; if (emptyT) emptyT.classList.remove('d-none'); }
        else { floor.style.display = ''; if (emptyT) emptyT.classList.add('d-none'); }
        // layout: real coords (natural size up to a cap → popup grows with the hall; shrink only if huge)
        // or a tidy grid fallback when coordinates are missing.
        // on phones the saved X/Y coords make tables tiny/overflow → use the tidy wrap-grid instead
        var hasLayout = (boardView === 'map') && (window.innerWidth >= 768) && tables.some(function (t) { return num(t.x) > 0 || num(t.y) > 0; });
        if (tables.length) {
            if (hasLayout) {
                var minX = 1e9, minY = 1e9, maxX = 0, maxY = 0;
                tables.forEach(function (t) {
                    minX = Math.min(minX, num(t.x)); minY = Math.min(minY, num(t.y));
                    maxX = Math.max(maxX, num(t.x) + (num(t.w) || 104)); maxY = Math.max(maxY, num(t.y) + (num(t.h) || 104));
                });
                var rawW = (maxX - minX), rawH = (maxY - minY);
                // fit to the board width and allow a GENTLE grow (up to 1.3×) so tables aren't tiny; never overflow.
                var bw = document.getElementById('boardWrap');
                var availW = ((bw && bw.clientWidth) ? bw.clientWidth : (window.innerWidth || 1200) * 0.6) - 60 - CM * 2;
                var scale = Math.max(0.7, Math.min(availW / rawW, 1.3));
                floor.className = 'position-relative';
                floor.style.margin = '0 auto';
                floor.style.width = Math.round(rawW * scale + CM * 2) + 'px';
                floor.style.height = Math.round(rawH * scale + CM * 2) + 'px';
                floor.innerHTML = tables.map(function (t) {
                    var w = Math.max(Math.round((num(t.w) || 104) * scale), 74), h = Math.max(Math.round((num(t.h) || 104) * scale), 74);
                    var left = Math.round((num(t.x) - minX) * scale), top = Math.round((num(t.y) - minY) * scale);
                    return tableCard(left, top, w, h, t, t.shape === 'Round');
                }).join('');
            } else {
                // grid fallback — cells flow in a wrap grid (each cell = seat-area with chairs)
                floor.className = 'd-flex flex-wrap gap-6 justify-content-center';
                floor.style.margin = '0 auto'; floor.style.width = 'auto'; floor.style.height = 'auto'; floor.style.maxWidth = '900px';
                floor.innerHTML = tables.map(function (t, i) {
                    // lay out in a relative flow: wrap each absolute seat-area in a sized relative box
                    var w = t.shape === 'Round' ? 116 : 108, h = 108;
                    return '<div style="position:relative;width:' + (w + CM * 2) + 'px;height:' + (h + CM * 2) + 'px">'
                        + tableCard(0, 0, w, h, t, t.shape === 'Round').replace('position:absolute;left:0px;top:0px;', 'position:absolute;left:0;top:0;')
                        + '</div>';
                }).join('');
            }
        }
        // stats
        var occ = 0, res = 0, cln = 0, total = tables.length;
        tables.forEach(function (t) { var s = effStatus(t); if (s === 'Occupied' || s === 'Awaiting') occ++; else if (s === 'Reserved') res++; else if (s === 'Cleaning') cln++; });
        var g = function (id) { return document.getElementById(id); };
        if (g('stTotal')) { g('stTotal').textContent = total; g('stOcc').textContent = occ; g('stRes').textContent = res; g('stClean').textContent = cln; g('stPct').textContent = (total ? Math.round(occ * 100 / total) : 0) + '%'; }
        var hn = g('tbvHallName'), curH = HALLS.filter(function (x) { return x.id === curHall; })[0];
        if (hn && curH) hn.textContent = hallName(curH);
        applyZoom();
        renderOrderList();
    }

    // ===== left panel: order list (open tables + held) — reads the same HALLS data as the map =====
    var orderTab = 'open';
    function renderOrderList() {
        var box = document.getElementById('tbvOrders'); if (!box) return;
        var tables = curTables();
        var occT = tables.filter(function (t) { var s = effStatus(t); return s === 'Occupied' || s === 'Awaiting'; });
        var openCnt = document.getElementById('tbvOpenCnt'); if (openCnt) openCnt.textContent = occT.length;
        if (orderTab === 'held') {
            box.innerHTML = '<div class="tbv-empty"><i class="ki-outline ki-time fs-3x d-block mb-2"></i>' + (L.heldEmpty || '') + '</div>';
            fetch(U.held, { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (j) {
                var rows = (j && j.ok) ? (j.orders || j.held || []) : [];
                if (!rows.length) return;
                box.innerHTML = rows.map(function (o) {
                    var hasTbl = o.tableCode && o.tableCode !== '';
                    return orderCardHtml({
                        code: o.tableCode || '', status: 'Held', amount: o.grandTotal || 0,
                        title: hasTbl ? (tr('طاولة ', 'Table ') + esc(o.tableCode)) : (tr('طلب معلّق ', 'Held order ') + '#' + o.id),
                        badge: hasTbl ? o.tableCode : '⏸',
                        sinceUtc: o.heldAtUtcIso, customerName: o.customerName || '',
                        orderId: o.id, tableId: o.tableId || 0, items: o.items || []
                    });
                }).join('');
            }).catch(function () { });
            return;
        }
        if (!occT.length) { box.innerHTML = '<div class="tbv-empty"><i class="ki-outline ki-basket fs-3x d-block mb-2"></i>' + (L.noOpen || L.heldEmpty || '') + '</div>'; return; }
        box.innerHTML = occT.map(function (t) { return orderCardHtml(t); }).join('');
    }
    // map a dish name to a food emoji for the order-card preview
    function foodEmoji(n) {
        n = (n || '').toLowerCase();
        function has() { for (var i = 0; i < arguments.length; i++) if (n.indexOf(arguments[i]) >= 0) return true; return false; }
        if (has('برجر', 'برغر', 'burger', 'همبرجر')) return '🍔';
        if (has('بيتزا', 'pizza')) return '🍕';
        if (has('دجاج', 'فراخ', 'chicken', 'wing')) return '🍗';
        if (has('بطاطس', 'بطاطا', 'fries', 'potato')) return '🍟';
        if (has('بيبسي', 'كولا', 'عصير', 'مشروب', 'مياه', 'ماء', 'pepsi', 'cola', 'soda', 'juice', 'water', 'coke', 'sprite', 'drink')) return '🥤';
        if (has('قهوة', 'كابتشينو', 'لاتيه', 'coffee', 'latte', 'cappu', 'espresso', 'موكا', 'mocha')) return '☕';
        if (has('شاي', 'tea')) return '🍵';
        if (has('آيس كريم', 'ايس كريم', 'ice cream', 'مثلجات')) return '🍨';
        if (has('كيك', 'تشيز', 'حلوى', 'حلى', 'cake', 'cheesecake', 'dessert', 'sweet', 'حلا')) return '🍰';
        if (has('سلطة', 'salad')) return '🥗';
        if (has('ستيك', 'لحم', 'steak', 'beef', 'meat', 'مشوي', 'grill')) return '🥩';
        if (has('رامن', 'شوربة', 'حساء', 'soup', 'ramen', 'noodle', 'مكرونة', 'باستا', 'pasta', 'معكرونة')) return '🍜';
        if (has('سمك', 'سلمون', 'fish', 'salmon', 'tuna', 'جمبري', 'shrimp', 'seafood', 'بحري', 'روبيان')) return '🐟';
        if (has('فطور', 'بيض', 'egg', 'breakfast', 'omelet', 'أومليت')) return '🍳';
        if (has('بان كيك', 'بانكيك', 'pancake', 'وافل', 'waffle')) return '🥞';
        if (has('ساندويتش', 'sandwich', 'رول', 'wrap', 'شاورما', 'shawarma', 'راب')) return '🌯';
        if (has('أرز', 'رز', 'rice', 'برياني', 'كبسة', 'biryani')) return '🍚';
        return '🍽️';
    }
    // a mini top-down "table + 4 chairs" glyph in the status colour, with the table-number badge
    function tableGlyph(col, code) {
        return '<span class="tbl-ic" style="background:' + hexA(col, .12) + '">'
            + '<svg viewBox="0 0 24 24" width="26" height="26"><rect x="6" y="6" width="12" height="12" rx="3.5" fill="' + col + '"/>'
            + '<rect x="9" y="1.5" width="6" height="3" rx="1.5" fill="' + col + '" opacity=".5"/><rect x="9" y="19.5" width="6" height="3" rx="1.5" fill="' + col + '" opacity=".5"/>'
            + '<rect x="1.5" y="9" width="3" height="6" rx="1.5" fill="' + col + '" opacity=".5"/><rect x="19.5" y="9" width="3" height="6" rx="1.5" fill="' + col + '" opacity=".5"/></svg>'
            + '<span class="tbl-ic-no" style="background:' + col + '">' + esc(code) + '</span></span>';
    }
    function orderCardHtml(t) {
        var s = effStatus(t), c = STAT[s] || STAT.Available, col = c.c;
        var title = t.title || (tr('طاولة ', 'Table ') + esc(t.code));
        var badge = (t.badge != null && t.badge !== '') ? t.badge : t.code;
        // people count when we have it; otherwise the customer name (held walk-in orders)
        var ppl = t.guests ? ('<span><i class="ki-outline ki-people"></i> ' + t.guests + ' ' + (L.person || '') + '</span>')
            : (t.customerName ? ('<span><i class="ki-outline ki-user"></i> ' + esc(custName(t.customerName)) + '</span>') : '');
        var food = (t.items && t.items.length) ? ('<div class="tfood">' + t.items.slice(0, 6).map(function (it) {
            var name = (it && it.n != null) ? it.n : it, img = (it && it.img) ? it.img : '';
            // real thumbnail when the item has a photo; otherwise a food-emoji fallback
            return img
                ? '<img class="fimg" src="' + esc(img) + '" alt="" title="' + esc(name) + '" loading="lazy" onerror="this.outerHTML=\'<span class=femoji>' + foodEmoji(name) + '</span>\'">'
                : '<span class="femoji" title="' + esc(name) + '">' + foodEmoji(name) + '</span>';
        }).join('') + '</div>') : '';
        var when = t.sinceUtc ? ('<div class="tmid"><i class="ki-outline ki-time fs-6"></i> ' + timerStr(t) + '</div>') : '<div class="tmid"></div>';
        var amt = t.amount ? ('<div class="amt">' + fmt(t.amount) + ' <small>' + CUR + '</small></div>') : '';
        return '<div class="tbv-card" data-tid="' + (t.tableId || t.id || '') + '" data-code="' + esc(t.code) + '" data-order="' + (t.orderId || '') + '">'
            + '<span class="edge" style="background:' + col + '"></span>'
            + tableGlyph(col, badge)
            + '<div class="info"><div class="tname">' + title + '</div>'
            + '<div class="tppl">' + ppl + '</div>' + food + '</div>'
            + when
            + '<div class="tright"><span class="stpill" style="background:' + hexA(col, .13) + ';color:' + col + '">' + statusLabel(s) + '</span>' + amt + '</div>'
            + '</div>';
    }

    // ===== map zoom =====
    var zoom = 1;
    function applyZoom() { var z = document.getElementById('floorZoom'); if (z) z.style.transform = 'scale(' + zoom + ')'; }
    function setZoom(v) { zoom = Math.max(0.5, Math.min(1.8, v)); applyZoom(); }

    function fillHalls() {
        if (!hallSel) return;
        hallSel.innerHTML = HALLS.map(function (h) { return '<option value="' + h.id + '">' + esc(hallName(h)) + '</option>'; }).join('');
        if (curHall != null) hallSel.value = curHall;
    }
    if (hallSel) hallSel.addEventListener('change', function () { curHall = parseInt(hallSel.value); renderBoard(); });
    // re-render the board on resize/orientation change so it stays responsive (grid on phones, floor-plan on wider)
    var rzT = null;
    window.addEventListener('resize', function () { if (rzT) clearTimeout(rzT); rzT = setTimeout(function () { if (floor) renderBoard(); }, 200); });

    // live timers tick every second (only clocks re-render, not the whole board)
    function startTimers() {
        if (boardTimer) clearInterval(boardTimer);
        boardTimer = setInterval(function () {
            if (!floor) return;
            floor.querySelectorAll('.tclock').forEach(function (el) {
                var since = el.getAttribute('data-since'); if (!since) return;
                var m = Math.floor(Math.max(0, (Date.now() - Date.parse(since)) / 60000));
                el.textContent = ('0' + Math.floor(m / 60)).slice(-2) + ':' + ('0' + (m % 60)).slice(-2);
            });
        }, 1000);
    }

    function refreshBoard() {
        return fetch(U.board, { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (j) {
            if (j && j.ok) { HALLS = j.halls || []; if (!HALLS.some(function (h) { return h.id === curHall; })) curHall = HALLS.length ? HALLS[0].id : null; fillHalls(); renderBoard(); }
        });
    }

    function resetTblModes() { moveMode = false; mergeMode = false; moveOrderId = null; moveSrcCode = ''; mergeSrcTable = null; mergeSrcCode = ''; }
    if (btnPickTable) btnPickTable.addEventListener('click', function () { resetTblModes(); refreshBoard(); var m = modal('tablesModal'); if (m) m.show(); });
    var tablesModalEl = document.getElementById('tablesModal');
    if (tablesModalEl) tablesModalEl.addEventListener('hidden.bs.modal', function () { resetTblModes(); hideTblMenu(); });

    // (Move/merge are done via a table-PICKER dialog now — see openTablePicker — not floor drag/tap-modes.)
    // ===== Per-table FLOATING menu (no nested modal — nesting broke merge). Several actions per table. =====
    var tblMenuEl = document.getElementById('tblMenu');
    function hideTblMenu() { if (tblMenuEl) tblMenuEl.style.display = 'none'; }
    document.addEventListener('click', function (e) {
        if (!tblMenuEl || tblMenuEl.style.display === 'none') return;
        if (tblMenuEl.contains(e.target) || e.target.closest('.tcell')) return;
        hideTblMenu();
    });
    function closeBoard() { hideTblMenu(); var m = modal('tablesModal'); if (m) m.hide(); }

    // load a table order into the sidebar (orderId>0 → a specific party sitting on the table)
    function loadTable(tid, code, orderId) {
        return post(U.openTable, { tableId: tid, orderId: orderId || 0 }).then(function (j) {
            if (!j.ok) { toast('error', j.error || ''); return null; }
            order = j.order; orderType = 'Dine-in'; tableId = tid; tableCode = code;
            document.querySelectorAll('.otype').forEach(function (x) { x.classList.toggle('active', x.getAttribute('data-type') === 'Dine-in'); });
            if (btnPickTable) btnPickTable.classList.remove('d-none');
            paintTable(); render();
            toast('success', j.recalled ? (L.recalled || '') : (L.opened || ''));
            return j;
        });
    }
    // MERGE tables: the source table's parties move onto the target table as SEPARATE bills (source frees).
    function doMerge(srcTable, tgtTable) {
        if (busy) return; busy = true;
        post(U.merge, { sourceTableId: srcTable, targetTableId: tgtTable }).then(function (j) {
            if (j.ok) {
                toast('success', L.merged || '');
                // if the active order was on the source table, it's now on the target table
                if (order && order.tableId === srcTable) { order.tableId = tgtTable; tableId = tgtTable; tableCode = ''; }
                render(); paintTable(); refreshBoard();
            } else toast('error', j.error || '');
        }).finally(function () { resetTblModes(); busy = false; });
    }
    // MOVE an order (with its customer) to a free table.
    function doMove(orderId, toTable, code) {
        if (busy) return; busy = true;
        post(U.move, { orderId: orderId, toTableId: toTable }).then(function (j) {
            if (j.ok) {
                toast('success', L.moved || '');
                if (order && order.id === orderId && j.order) { order = j.order; tableId = order.tableId; tableCode = code; }
                render(); paintTable(); refreshBoard();
            } else toast('error', j.error || '');
        }).finally(function () { resetTblModes(); busy = false; });
    }
    // explicit-direction confirms — always spell out FROM which table → TO which (fixes the "backwards" confusion)
    function askMerge(srcId, srcCode, tgtId, tgtCode) {
        var html = '<div style="font-size:17px;font-weight:800;margin:8px 0">' + tr('طاولة ', 'Table ') + esc(srcCode) + ' <span style="color:#009ef7;font-size:22px">&#10142;</span> ' + tr('طاولة ', 'Table ') + esc(tgtCode) + '</div>'
            + '<div style="font-size:13px;color:#7e8299">' + (RTL ? ('طلب طاولة ' + esc(srcCode) + ' سينضمّ إلى طاولة ' + esc(tgtCode) + '، وتصبح طاولة ' + esc(srcCode) + ' فارغة.') : ('Table ' + esc(srcCode) + '’s order joins table ' + esc(tgtCode) + '; table ' + esc(srcCode) + ' becomes free.')) + '</div>';
        if (window.Swal) Swal.fire({ title: tr('تأكيد الدمج', 'Confirm merge'), html: html, icon: 'question', showCancelButton: true, confirmButtonText: tr('دمج', 'Merge'), cancelButtonText: tr('رجوع', 'Back'), confirmButtonColor: '#0E4A9E', reverseButtons: true }).then(function (r) { if (r.isConfirmed) doMerge(srcId, tgtId); });
        else if (confirm(tr('دمج طاولة ', 'Merge table ') + srcCode + ' → ' + tgtCode + '؟')) doMerge(srcId, tgtId);
    }
    function askMove(orderId, srcCode, tgtId, tgtCode) {
        var html = '<div style="font-size:17px;font-weight:800;margin:8px 0">' + tr('طاولة ', 'Table ') + esc(srcCode || '?') + ' <span style="color:#1f9d5c;font-size:22px">&#10142;</span> ' + tr('طاولة ', 'Table ') + esc(tgtCode) + '</div>'
            + '<div style="font-size:13px;color:#7e8299">' + (RTL ? ('سينتقل الطلب (بعميله) إلى طاولة ' + esc(tgtCode) + '.') : ('The order (with its customer) moves to table ' + esc(tgtCode) + '.')) + '</div>';
        if (window.Swal) Swal.fire({ title: tr('تأكيد النقل', 'Confirm move'), html: html, icon: 'question', showCancelButton: true, confirmButtonText: tr('نقل', 'Move'), cancelButtonText: tr('رجوع', 'Back'), confirmButtonColor: '#0E4A9E', reverseButtons: true }).then(function (r) { if (r.isConfirmed) doMove(orderId, tgtId, tgtCode); });
        else if (confirm(tr('نقل إلى طاولة ', 'Move to table ') + tgtCode + '؟')) doMove(orderId, tgtId, tgtCode);
    }

    // Move/Merge PICKER: repaint the floating panel with a list of the VALID target tables and let the cashier
    // pick one (no floor tap-modes, no drag). mode='move' → free tables; mode='merge' → other occupied tables.
    function allBoardTables() { var a = []; (HALLS || []).forEach(function (h) { (h.tables || []).forEach(function (t) { a.push(t); }); }); return a; }
    function openTablePicker(mode, src, x, y) {
        if (!tblMenuEl || !src) return;
        var isMove = (mode === 'move'), col = isMove ? '#f1416c' : '#009ef7';
        var list = allBoardTables().filter(function (t) {
            if (t.id === src.id) return false;
            return isMove ? (['Available', 'Reserved', 'Cleaning'].indexOf(t.status) >= 0)
                          : (t.status === 'Occupied' || t.status === 'Awaiting');
        });
        var title = isMove ? (tr('نقل طلب طاولة ', 'Move table ') + src.code + tr(' إلى طاولة فارغة:', ' to a free table:'))
                           : (tr('ضمّ طلب طاولة ', 'Merge table ') + src.code + tr(' إلى:', ' into:'));
        var html = '<div style="padding:11px 14px;background:' + col + ';color:#fff;font-weight:800;font-size:13.5px">' + esc(title) + '</div>';
        if (!list.length) html += '<div style="padding:18px 14px;color:#a1a5b7;font-size:13px;text-align:center">' + (isMove ? tr('لا توجد طاولات فارغة', 'No free tables') : tr('لا توجد طاولات مشغولة أخرى', 'No other busy tables')) + '</div>';
        list.forEach(function (t) {
            var right = (!isMove && t.amount) ? ('<span style="font-weight:800;color:#1f9d5c;font-size:13px">' + fmt(t.amount) + '</span>')
                                              : ('<span style="font-size:11px;color:#a1a5b7"><i class="ki-outline ki-people fs-7"></i> ' + (t.seats || '') + '</span>');
            html += '<button type="button" data-pick="' + t.id + '" data-pcode="' + esc(t.code) + '" class="tbl-pick" style="display:flex;align-items:center;justify-content:space-between;gap:8px;width:100%;border:0;border-top:1px solid #f4f4f6;background:#fff;padding:12px 14px;cursor:pointer;text-align:' + (RTL ? 'right' : 'left') + '"><span style="font-weight:700;color:#181c32;font-size:13.5px">' + tr('طاولة ', 'Table ') + esc(t.code) + '</span>' + right + '</button>';
        });
        html += '<button type="button" id="pickBack" style="width:100%;border:0;border-top:1px solid #eef0f2;background:#f9f9fb;padding:10px;font-size:12px;color:#7e8299;cursor:pointer">' + tr('رجوع', 'Back') + '</button>';
        tblMenuEl.innerHTML = html;
        tblMenuEl.setAttribute('data-pmode', mode);
        pickerSrc = src;
        tblMenuEl.style.display = 'block';
        var mw = 250, mh = Math.min(tblMenuEl.offsetHeight || 340, window.innerHeight * 0.82), vw = window.innerWidth, vh = window.innerHeight;
        var px = (typeof x === 'number') ? x : (vw / 2 - mw / 2), py = (typeof y === 'number') ? y : (vh / 2 - mh / 2);
        tblMenuEl.style.left = Math.max(10, Math.min(px, vw - mw - 10)) + 'px';
        tblMenuEl.style.top = Math.max(10, Math.min(py, vh - mh - 10)) + 'px';
    }
    var pickerSrc = null;

    // build + show the floating menu for a table (fetches its open parties first)
    function openTableMenu(t, x, y) {
        if (!tblMenuEl) return;
        menuTable = t;
        var occ = (t.status === 'Occupied' || t.status === 'Awaiting'), closed = (t.status === 'Closed');
        var col = (STAT[t.status] || STAT.Available).c;
        function mi(icon, label, color, act) {
            return '<button type="button" data-act="' + act + '" class="tbl-mi" style="display:flex;align-items:center;gap:10px;width:100%;border:0;border-top:1px solid #f4f4f6;background:#fff;padding:11px 14px;font-size:13.5px;font-weight:600;color:#3f4254;cursor:pointer;text-align:' + (RTL ? 'right' : 'left') + '"><i class="ki-outline ' + icon + ' fs-3" style="color:' + color + '"></i><span>' + esc(label) + '</span></button>';
        }
        function chip(st, label, color) {
            var on = (t.status === st);
            return '<button type="button" data-stat="' + st + '" class="tbl-stat" style="border:1px solid ' + color + ';background:' + (on ? color : '#fff') + ';color:' + (on ? '#fff' : color) + ';border-radius:8px;padding:5px 9px;font-size:11.5px;font-weight:700;cursor:pointer">' + esc(label) + '</button>';
        }
        function paint(orders, seats) {
            seats = seats || t.seats || 1;
            var head = '<div style="padding:11px 14px;background:' + col + ';color:#fff"><div style="font-weight:800;font-size:14px">' + tr('طاولة ', 'Table ') + esc(t.code) + '</div>'
                + '<div style="font-size:11.5px;opacity:.9">' + esc(statusLabel(t.status)) + ' · ' + orders.length + '/' + seats + ' ' + tr('طلب', 'orders') + '</div></div>';
            var body = '';
            (orders || []).forEach(function (o) {
                var empty = (o.itemCount || 0) === 0;
                var right = empty
                    ? '<span style="font-weight:600;color:#a1a5b7;font-size:11.5px">' + tr('لم يُطلب بعد', 'not ordered yet') + '</span>'
                    : '<span style="font-weight:800;color:#1f9d5c;font-size:13px">' + fmt(o.grandTotal) + '</span>';
                var sub = empty ? esc(custName(o.customerName)) : (esc(custName(o.customerName)) + ' · ' + o.itemCount + ' ' + (L.items || ''));
                body += '<button type="button" data-open="' + o.id + '" class="tbl-mi" style="display:flex;align-items:center;justify-content:space-between;gap:8px;width:100%;border:0;border-top:1px solid #f4f4f6;background:#fff;padding:10px 14px;cursor:pointer;text-align:' + (RTL ? 'right' : 'left') + '">'
                    + '<span style="display:flex;flex-direction:column"><span style="font-weight:700;color:#181c32;font-size:13px">#' + o.id + (o.hasUnsent ? ' <span style="color:#f6a609">&#9679;</span>' : '') + '</span><span style="font-size:11px;color:#a1a5b7">' + sub + '</span></span>'
                    + right + '</button>';
            });
            // free chairs = seats − guests already seated (across all parties). Free chair ⇒ can add another customer/party.
            var usedGuests = (orders || []).reduce(function (s, o) { return s + (o.guests || 0); }, 0);
            var freeSeats = Math.max(0, seats - usedGuests);
            var canNew = !closed && freeSeats > 0;
            if (canNew) body += mi('ki-plus-square', occ ? (tr('إضافة عميل على نفس الطاولة', 'Add a customer to this table') + ' · ' + freeSeats + ' ' + tr('مقعد فارغ', 'free')) : tr('فتح طلب جديد', 'Open a new order'), '#1f9d5c', 'new');
            if (occ) {
                body += mi('ki-wallet', tr('تحصيل ودفع (آخر طلب)', 'Pay (latest order)'), '#0E4A9E', 'pay');
                body += mi('ki-tablet-text', tr('إرسال للمطبخ', 'Send to kitchen'), '#f6a609', 'kitchen');
                body += mi('ki-arrows-loop', tr('دمج: ضمّ طلب هذه الطاولة لأخرى', 'Merge this table into another'), '#009ef7', 'merge');
                body += mi('ki-arrow-two-diagonals', tr('نقل الطلب لطاولة فارغة', 'Move order to a free table'), '#f1416c', 'move');
            }
            var stat = '<div style="padding:8px 14px 2px;font-size:10.5px;color:#a1a5b7;font-weight:700;border-top:1px solid #f4f4f6">' + tr('حالة الطاولة', 'Table status') + '</div>'
                + '<div style="display:flex;flex-wrap:wrap;gap:6px;padding:8px 12px 12px">' + chip('Available', tr('فارغة', 'Available'), '#1f9d5c') + chip('Reserved', tr('محجوزة', 'Reserved'), '#f6a609') + chip('Cleaning', tr('تنظيف', 'Cleaning'), '#a16207') + chip('Closed', tr('مغلقة', 'Closed'), '#7e8299') + '</div>';
            tblMenuEl.innerHTML = head + body + stat;
            tblMenuEl.style.display = 'block';
            var mw = 250, mh = Math.min(tblMenuEl.offsetHeight || 340, window.innerHeight * 0.82), vw = window.innerWidth, vh = window.innerHeight;
            tblMenuEl.style.left = Math.max(10, Math.min(x, vw - mw - 10)) + 'px';
            tblMenuEl.style.top = Math.max(10, Math.min(y, vh - mh - 10)) + 'px';
        }
        if (occ) {
            fetch(U.tableOrders + '?tableId=' + t.id + '&_=' + (new Date()).getTime(), { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' }).then(function (r) { return r.json(); })
                .then(function (j) { paint((j && j.ok) ? (j.orders || []) : [], j && j.seats); })
                .catch(function () { paint([], t.seats); });
        } else { paint([], t.seats); }
    }

    // menu action clicks
    if (tblMenuEl) tblMenuEl.addEventListener('click', function (e) {
        // PICKER: chose a target table for move/merge (dialog method)
        var pk = e.target.closest('.tbl-pick');
        if (pk) {
            var mode = tblMenuEl.getAttribute('data-pmode'), src = pickerSrc;
            var tid = parseInt(pk.getAttribute('data-pick')), tcode = pk.getAttribute('data-pcode') || '';
            hideTblMenu();
            if (src) { if (mode === 'move') askMove(src.orderId, src.code, tid, tcode); else askMerge(src.id, src.code, tid, tcode); }
            return;
        }
        if (e.target.closest('#pickBack')) { hideTblMenu(); return; }
        var t = menuTable; if (!t) return;
        var stb = e.target.closest('.tbl-stat');
        if (stb) {
            post(U.tstatus, { tableId: t.id, status: stb.getAttribute('data-stat') }).then(function (j) {
                if (j.ok) { HALLS = j.halls || HALLS; fillHalls(); renderBoard(); hideTblMenu(); }
                else toast('error', j.error || '');
            });
            return;
        }
        var openBtn = e.target.closest('[data-open]');
        if (openBtn) { loadTable(t.id, t.code, parseInt(openBtn.getAttribute('data-open'))).then(function (j) { if (j) closeBoard(); }); return; }
        var act = e.target.closest('.tbl-mi[data-act]'); if (!act) return;
        var a = act.getAttribute('data-act');
        if (a === 'new') {
            if (t.status === 'Occupied' || t.status === 'Awaiting') {
                post(U.newOnTable, { tableId: t.id }).then(function (j) {
                    if (!j.ok) { toast('error', j.error || ''); return; }
                    order = j.order; orderType = 'Dine-in'; tableId = t.id; tableCode = t.code;
                    document.querySelectorAll('.otype').forEach(function (x) { x.classList.toggle('active', x.getAttribute('data-type') === 'Dine-in'); });
                    if (btnPickTable) btnPickTable.classList.remove('d-none');
                    paintTable(); render(); toast('success', L.opened || ''); closeBoard();
                });
            } else { loadTable(t.id, t.code, 0).then(function (j) { if (j) closeBoard(); }); }
        }
        else if (a === 'pay') { loadTable(t.id, t.code, 0).then(function (j) { if (j) { closeBoard(); if (btnPay) btnPay.click(); } }); }
        else if (a === 'kitchen') { loadTable(t.id, t.code, 0).then(function (j) { if (j && order) post(U.sendKitchen, { orderId: order.id }).then(function (k) { if (k.ok) { order = k.order; render(); toast('success', L.sentKitchen || ''); } else toast('error', k.error || ''); }); hideTblMenu(); }); }
        else if (a === 'merge') { var sm = { id: t.id, code: t.code, orderId: t.orderId }; refreshBoard().then(function () { openTablePicker('merge', sm); }); }
        else if (a === 'move') { var sv = { id: t.id, code: t.code, orderId: t.orderId }; refreshBoard().then(function () { openTablePicker('move', sv); }); }
    });

    // Sidebar Move/Merge buttons act on the ACTIVE order as the SOURCE → open the target-table PICKER (centered)
    if (btnMoveTable) btnMoveTable.addEventListener('click', function () {
        if (!(order && order.id && orderType === 'Dine-in' && tableId)) return;
        var src = { id: tableId, code: tableCode || '', orderId: order.id };
        refreshBoard().then(function () { openTablePicker('move', src); });
    });
    if (btnMergeTable) btnMergeTable.addEventListener('click', function () {
        if (!(order && order.id && orderType === 'Dine-in' && tableId)) return;
        var src = { id: tableId, code: tableCode || '', orderId: order.id };
        refreshBoard().then(function () { openTablePicker('merge', src); });
    });

    // Floor interactions: ⋮ → full menu (incl. move/merge via picker). BODY tap: current unseated invoice →
    // SEAT it here (keep items, "order first then sit"); else recall the table's order ("sit first then order").
    // The current order is NEVER emptied, and tapping NEVER opens a new invoice.
    // POS-B2: reservation arrival → open a Dine-in order on the table (linked); no-show → drop it
    function arriveReservation(rid, code) {
        post(U.reservationArrive, { reservationId: rid }).then(function (j) {
            if (j.ok && j.order) {
                order = j.order; orderType = 'Dine-in'; tableId = j.order.tableId || null; tableCode = code;
                document.querySelectorAll('.otype').forEach(function (x) { x.classList.toggle('active', x.getAttribute('data-type') === 'Dine-in'); });
                if (btnPickTable) btnPickTable.classList.remove('d-none');
                render(); paintTable(); refreshBoard(); if (typeof renderInvoices === 'function') renderInvoices(); toast('success', L.opened || ''); closeBoard();
            } else toast('error', j.error || '');
        });
    }
    function noShowReservation(rid) {
        post(U.reservationNoShow, { reservationId: rid }).then(function (j) {
            if (j.ok) { refreshBoard(); toast('success', tr('تم — لم يحضر', 'Marked no-show')); } else toast('error', j.error || '');
        });
    }
    if (floor) floor.addEventListener('click', function (e) {
        var cell = e.target.closest('.tcell'); if (!cell) return;
        var id = parseInt(cell.getAttribute('data-id')), st = cell.getAttribute('data-status'), code = cell.getAttribute('data-code') || '';
        var occ = (st === 'Occupied' || st === 'Awaiting');
        var onIcon = !!e.target.closest('.tbl-menu');
        var t = { id: id, code: code, status: st, seats: parseInt(cell.getAttribute('data-seats')) || 0, orderId: parseInt(cell.getAttribute('data-order')) || null, cust: cell.getAttribute('data-cust') || '' };
        // ⋮ always opens the menu; a CLOSED table also opens it on any tap so its «فارغة» chip can re-open it.
        if (onIcon || st === 'Closed') { openTableMenu(t, e.clientX, e.clientY); return; }
        // POS-B2: a RESERVED table → offer "arrived" (open a Dine-in order) or "no-show"
        if (st === 'Reserved') {
            var rid = parseInt(cell.getAttribute('data-resv')) || 0; if (!rid) return;
            var rfor = cell.getAttribute('data-resvfor') || '', rat = cell.getAttribute('data-resvat') || '';
            var body = tr('طاولة ', 'Table ') + code + (rat ? (' — ' + rat) : '') + (rfor ? (' — ' + rfor) : '');
            if (window.Swal) {
                Swal.fire({
                    title: tr('حجز', 'Reservation'), text: body, icon: 'question', showCancelButton: true, showDenyButton: true,
                    confirmButtonText: tr('وصل — افتح طلب', 'Arrived — open order'), denyButtonText: tr('لم يحضر', 'No-show'), cancelButtonText: tr('رجوع', 'Back'),
                    confirmButtonColor: '#16a34a', denyButtonColor: '#e5484d', reverseButtons: true
                }).then(function (r) { if (r.isConfirmed) arriveReservation(rid, code); else if (r.isDenied) noShowReservation(rid); });
            } else { if (confirm(tr('وصل العميل؟ (إلغاء = لم يحضر)', 'Arrived? (cancel = no-show)'))) arriveReservation(rid, code); else noShowReservation(rid); }
            return;
        }
        // CURRENT invoice not yet seated → SEAT it here as a party (even if the table already has parties, up to
        // its seats). This is how you seat a 2nd/3rd customer on the SAME table.
        if (order && order.id && !tableId) {
            if (tapLock) return; tapLock = true;
            var clear = function () { setTimeout(function () { tapLock = false; }, 700); };
            post(U.seat, { orderId: order.id, toTableId: id }).then(function (j) {
                if (j.ok) {
                    if (j.order) { order = j.order; } tableId = id; tableCode = code; orderType = 'Dine-in';
                    document.querySelectorAll('.otype').forEach(function (x) { x.classList.toggle('active', x.getAttribute('data-type') === 'Dine-in'); });
                    if (btnPickTable) btnPickTable.classList.remove('d-none');
                    render(); paintTable(); refreshBoard(); toast('success', L.moved || L.opened || ''); closeBoard();
                } else toast('error', j.error || '');
            }).finally(clear);
            return;
        }
        // no current invoice: OCCUPIED table → open its ACTIONS MENU (parties/pay/kitchen/merge/move/status —
        // no auto-recall); FREE table → tell them to press «جديد».
        if (occ) { openTableMenu(t, e.clientX, e.clientY); return; }
        toast('info', tr('اضغط «جديد» لبدء فاتورة، ثم اختر الطاولة', 'Press «New» to start an invoice, then pick the table'));
    });
    // board refresh (adding tables is admin-only — no add-table control here)
    var btnRefreshBoard = document.getElementById('btnRefreshBoard');
    if (btnRefreshBoard) btnRefreshBoard.addEventListener('click', function () { refreshBoard(); });

    // ===== two-panel wiring: order-list tabs, cards, view toggle, zoom, new-order =====
    document.querySelectorAll('.tbv-tab').forEach(function (b) {
        b.addEventListener('click', function () {
            document.querySelectorAll('.tbv-tab').forEach(function (x) { x.classList.remove('active'); });
            b.classList.add('active'); orderTab = b.getAttribute('data-otab'); renderOrderList();
        });
    });
    var ordersBox = document.getElementById('tbvOrders');
    if (ordersBox) ordersBox.addEventListener('click', function (e) {
        var card = e.target.closest('.tbv-card'); if (!card) return;
        var tid = parseInt(card.getAttribute('data-tid')) || 0, code = card.getAttribute('data-code') || '', oid = parseInt(card.getAttribute('data-order')) || 0;
        if (!tid && !oid) return;
        loadTable(tid, code, oid); closeBoard();
    });
    var vList = document.getElementById('tbvViewList'), vMap = document.getElementById('tbvViewMap');
    function setView(v) { boardView = v; if (vList) vList.classList.toggle('active', v === 'list'); if (vMap) vMap.classList.toggle('active', v === 'map'); renderBoard(); }
    if (vList) vList.addEventListener('click', function () { setView('list'); });
    if (vMap) vMap.addEventListener('click', function () { setView('map'); });
    var zin = document.getElementById('tbvZin'), zout = document.getElementById('tbvZout'), zfit = document.getElementById('tbvZfit');
    if (zin) zin.addEventListener('click', function () { setZoom(zoom + 0.15); });
    if (zout) zout.addEventListener('click', function () { setZoom(zoom - 0.15); });
    if (zfit) zfit.addEventListener('click', function () { setZoom(1); });
    var tbvNew = document.getElementById('tbvNew');
    if (tbvNew) tbvNew.addEventListener('click', function () { closeBoard(); var rn = document.getElementById('railNew'); if (rn) rn.click(); });

    fillHalls(); renderBoard(); startTimers();

    // ================= receipt (thermal, offline) =================
    function receiptHtml(o, rno, paid, chg) {
        var W = (RECEIPT.paperWidth == 58 ? 58 : 80), pad = (W == 58 ? 2 : 4);
        var rows = (o.lines || []).map(function (l) {
            var mrows = (l.modifiers && l.modifiers.length)
                ? l.modifiers.map(function (m) { return '<tr><td class="q"></td><td class="n mut">+ ' + esc(m.name) + '</td><td class="t"></td></tr>'; }).join('') : '';
            return '<tr><td class="q">' + l.qty + '×</td><td class="n">' + esc(l.name) + '</td><td class="t">' + fmt(l.lineTotal) + '</td></tr>' + mrows;
        }).join('');
        var svc = o.serviceAmount > 0 ? '<div class="row"><span>' + esc(L.service) + '</span><span>' + fmt(o.serviceAmount) + '</span></div>' : '';
        var tbl = (o.orderType === 'Dine-in' && o.tableId) ? ('<div class="row mut"><span>' + esc(L.table) + '</span><span>' + esc(tableCode || o.tableId) + '</span></div>') : '';
        var head = (RECEIPT.logo ? ('<img src="' + RECEIPT.logo + '" style="max-height:60px;max-width:100%;margin:0 auto 4px;display:block"/>') : '')
            + '<div class="name">' + esc(RECEIPT.name) + '</div>'
            + (RECEIPT.address ? '<div class="mut">' + esc(RECEIPT.address) + '</div>' : '')
            + (RECEIPT.phone ? '<div class="mut">' + esc(RECEIPT.phone) + '</div>' : '')
            + (RECEIPT.taxNo ? '<div class="mut">' + esc(L.taxNo) + ': ' + esc(RECEIPT.taxNo) + '</div>' : '');
        var cashLines = (paid != null) ? ('<div class="row"><span>' + esc(L.paid) + '</span><span>' + fmt(paid) + '</span></div><div class="row"><span>' + esc(L.change) + '</span><span>' + fmt(chg) + '</span></div>') : '';
        var css = '@page{size:' + W + 'mm auto;margin:0}body{width:' + W + 'mm;margin:0;padding:' + pad + 'mm;font-family:Cairo,\'Courier New\',monospace;color:#000;font-size:' + (W == 58 ? 11 : 12) + 'px;line-height:1.5}.c{text-align:center}.name{font-weight:700;font-size:' + (W == 58 ? 13 : 15) + 'px}.mut{font-size:' + (W == 58 ? 9 : 10) + 'px}hr{border:0;border-top:1px dashed #000;margin:5px 0}table{width:100%;border-collapse:collapse}td{padding:1px 0;vertical-align:top}td.q{width:16%}td.t{width:24%;text-align:' + (RTL ? 'left' : 'right') + ';white-space:nowrap}td.n{width:60%}.row{display:flex;justify-content:space-between}.tot{font-weight:700;font-size:' + (W == 58 ? 13 : 15) + 'px}';
        return '<!doctype html><html dir="' + (RTL ? 'rtl' : 'ltr') + '"><head><meta charset="utf-8"><link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Cairo&display=swap"><style>' + css + '</style></head><body>'
            + '<div class="c">' + head + '</div><hr>'
            + '<div class="row mut"><span>' + esc(L.receipt) + '</span><span>' + esc(rno || '-') + '</span></div>'
            + '<div class="row mut"><span>' + esc(L.terminal) + '</span><span>' + esc(TERM.code) + ' · ' + esc(L.shift) + ' ' + esc(TERM.shift) + '</span></div>'
            + '<div class="row mut"><span>' + esc(L.cashier) + '</span><span>' + esc(TERM.cashier) + '</span></div>' + tbl
            + '<div class="row mut"><span>' + esc(L.date) + '</span><span>' + new Date().toLocaleString() + '</span></div><hr>'
            + '<table>' + rows + '</table><hr>'
            + '<div class="row"><span>' + esc(L.subtotal) + '</span><span>' + fmt(o.subTotal) + '</span></div>' + svc
            + '<div class="row"><span>' + esc(L.vat) + '</span><span>' + fmt(o.taxTotal) + '</span></div>'
            + '<div class="row tot"><span>' + esc(L.total) + '</span><span>' + fmt(o.grandTotal) + ' ' + CUR + '</span></div><hr>'
            + cashLines
            + (RECEIPT.footer ? '<hr><div class="c mut">' + esc(RECEIPT.footer) + '</div>' : '')
            + '<div class="c mut" style="margin-top:6px">' + esc(L.thankYou) + '</div></body></html>';
    }
    function printReceipt(o, rno, paid, chg) {
        var f = document.getElementById('posPrintFrame');
        if (!f) { f = document.createElement('iframe'); f.id = 'posPrintFrame'; f.style.cssText = 'position:fixed;right:-9999px;bottom:0;width:0;height:0;border:0'; document.body.appendChild(f); }
        var d = f.contentWindow.document; d.open(); d.write(receiptHtml(o, rno, paid, chg)); d.close();
        setTimeout(function () { try { f.contentWindow.focus(); var n = RECEIPT.copies || 1; for (var i = 0; i < n; i++) { f.contentWindow.print(); } } catch (e) { } }, 350);
    }

    // ================= order render =================
    var btnKitchen = document.getElementById('btnKitchen');
    function hasUnsent() { return !!(order && order.lines && order.lines.some(function (l) { return (l.qty - (l.sentQty || 0)) > 0.0001; })); }
    function render() {
        var has = order && order.lines && order.lines.length;
        paintCustomer();
        emptyEl.classList.toggle('d-none', !!has);
        if (btnKitchen) btnKitchen.classList.toggle('d-none', !hasUnsent());
        var statT = document.getElementById('statTotal'), statC = document.getElementById('statCount');
        var statG = document.getElementById('statGuests'); if (statG) statG.textContent = (order && order.guests) ? order.guests : 1;
        if (!has) {
            linesEl.innerHTML = ''; elSub.textContent = elGrand.textContent = elTax.textContent = '0.00'; if (elSvc) elSvc.textContent = '0.00';
            if (statT) statT.textContent = '0.00'; if (statC) statC.textContent = '0';
            if (btnPay) btnPay.disabled = true; return;
        }
        linesEl.innerHTML = order.lines.map(function (l) {
            var sent = (l.sentQty || 0), fullySent = sent >= l.qty - 0.0001, minusOff = l.qty <= sent + 0.0001;
            var img = l.image ? ('<img src="' + l.image + '" alt="">') : ('<span class="ph d-flex align-items-center justify-content-center"><i class="ki-outline ki-cup"></i></span>');
            var badge = sent > 0 ? (' <span class="posx-chip green" style="font-size:9px;padding:1px 6px">' + esc(L.kitchenBadge || '') + '</span>') : '';
            var mods = (l.modifiers && l.modifiers.length)
                ? '<div class="text-muted fs-8">' + l.modifiers.map(function (m) { return esc(m.name) + (m.extraPrice > 0 ? ' +' + fmt(m.extraPrice) : ''); }).join(', ') + '</div>' : '';
            return '<div class="posx-line">' + img
                + '<div class="nm"><b>' + esc(l.name) + badge + '</b>' + mods + '</div>'
                + '<div class="posx-qty">'
                + '<button type="button" class="qminus" data-line="' + l.id + '"' + (minusOff ? ' disabled' : '') + '>−</button>'
                + '<b>' + l.qty + '</b>'
                + '<button type="button" class="qplus" data-line="' + l.id + '">+</button>'
                + '<button type="button" class="qdel" data-line="' + l.id + '"' + (fullySent ? ' disabled' : '') + ' style="color:#fca5a5">×</button>'
                + '</div><div class="price">' + fmt(l.lineTotal) + '</div></div>';
        }).join('');
        elSub.textContent = fmt(order.subTotal); if (elSvc) elSvc.textContent = fmt(order.serviceAmount); elTax.textContent = fmt(order.taxTotal); elGrand.textContent = fmt(order.grandTotal);
        if (statT) statT.textContent = fmt(order.grandTotal);
        if (statC) statC.textContent = order.lines.reduce(function (a, l) { return a + (l.qty || 0); }, 0);
        if (btnPay) btnPay.disabled = false;
    }
    if (btnKitchen) btnKitchen.addEventListener('click', function () {
        if (!order || !order.lines.length || busy) return; busy = true;
        post(U.sendKitchen, { orderId: order.id }).then(function (j) {
            if (j.ok) { order = j.order; render(); toast('success', L.sentKitchen || ''); }
            else toast('info', j.error || L.noNewKitchen || '');
        }).finally(function () { busy = false; });
    });

    // guests (عدد الأشخاص) — how many people share THIS one invoice (not separate bills)
    function setGuests(n) {
        n = Math.max(1, Math.min(n | 0, 50));
        var apply = function (oid) {
            post(U.guests, { orderId: oid, guests: n }).then(function (j) {
                if (j.ok) { if (j.order) order = j.order; else if (order) order.guests = n; render(); }
                else toast('error', j.error || '');
            });
        };
        if (order && order.id) apply(order.id);
        else ensure().then(function (oid) { if (oid) apply(oid); });
    }
    var gMinus = document.getElementById('guestMinus'), gPlus = document.getElementById('guestPlus');
    if (gMinus) gMinus.addEventListener('click', function () { setGuests(((order && order.guests) || 1) - 1); });
    if (gPlus) gPlus.addEventListener('click', function () { setGuests(((order && order.guests) || 1) + 1); });

    function ensure() {
        if (order && order.id) return Promise.resolve(order.id);
        // no need to pick a table first — you can add items now and seat the invoice on a table later
        return post(U.create, { orderType: orderType, tableId: tableId || '' }).then(function (j) { if (j.ok) { order = j.order; render(); return order.id; } toast('error', j.error || ''); return 0; });
    }
    // POS-9b: offline? → the local order engine (CBLO) handles the open-order lifecycle from the cached bundle
    function off() { return !navigator.onLine && !!window.CBLO; }
    // RC-4b: core add — optionIds (array) are the chosen modifier options folded into the line price server-side
    function postAddItem(id, opts) {
        if (busy) return;
        if (off()) { busy = true; window.CBLO.addLine(id, 1, opts).then(function (o) { order = o; render(); }).finally(function () { busy = false; }); return; }
        busy = true;
        ensure().then(function (oid) {
            if (!oid) { busy = false; return; }
            var d = { orderId: oid, itemId: id, qty: 1 };
            if (opts && opts.length) d.optionIds = opts.join(',');
            return post(U.add, d).then(function (j) { if (j.ok) { order = j.order; render(); } else toast('error', j.error || ''); });
        }).finally(function () { busy = false; });
    }
    // RC-4b: dispatcher — an item WITH modifier groups opens the chooser first; a plain item adds instantly
    function addItem(id) {
        if (P.modItemIds && P.modItemIds.indexOf(id) >= 0) openModifierChooser(id);
        else postAddItem(id, null);
    }
    // ── RC-4b modifier chooser ──────────────────────────────────────────
    var modCurItem = 0;
    function openModifierChooser(id) {
        modCurItem = id;
        var card = document.querySelector('.pos-item[data-id="' + id + '"]');
        var nm = card ? (card.getAttribute('data-name') || '') : '';
        var showGroups = function (groups) {
            if (!groups || !groups.length) { postAddItem(id, null); return; }   // no active groups → fast path
            renderModifierGroups(nm, groups);
            var m = modal('modModal'); if (m) m.show();
        };
        if (off()) { window.CBLO.itemModifiers(id).then(showGroups); return; }   // POS-9b: modifiers from the cached bundle
        fetch(U.itemMods + '?itemId=' + id, { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' })
            .then(function (r) { return r.json(); })
            .then(function (j) { if (!j.ok) { toast('error', j.error || ''); return; } showGroups(j.groups || []); });
    }
    function renderModifierGroups(itemName, groups) {
        var nmEl = document.getElementById('modItemName'); if (nmEl) nmEl.textContent = itemName;
        var box = document.getElementById('modGroups'); if (!box) return;
        var html = '';
        groups.forEach(function (g) {
            var isChoice = g.type === 'Choice';
            var reqBadge = isChoice ? ' <span class="badge badge-light-danger fs-8">' + esc(L.modRequired || 'Required') + '</span>' : '';
            var maxNote = (!isChoice && g.maxSelect > 0) ? ' <span class="text-muted fs-8">(≤ ' + g.maxSelect + ')</span>' : '';
            html += '<div class="mb-5 mod-group" data-gid="' + g.groupId + '" data-type="' + g.type + '" data-min="' + (g.minSelect || 0) + '" data-max="' + (g.maxSelect || 0) + '">';
            html += '<div class="fw-bold fs-5 mb-3">' + esc(g.name) + reqBadge + maxNote + '</div>';
            (g.options || []).forEach(function (op) {
                var extra = op.extraPrice > 0 ? ' <span class="text-success fw-bold">+' + fmt(op.extraPrice) + ' ' + esc(CUR) + '</span>' : '';
                var inType = isChoice ? 'radio' : 'checkbox';
                var checked = (isChoice && op.isDefault) ? ' checked' : '';
                html += '<label class="d-flex align-items-center gap-3 border border-gray-200 rounded-3 p-3 mb-2 cursor-pointer">'
                    + '<input class="form-check-input mod-opt" type="' + inType + '" name="modg_' + g.groupId + '" value="' + op.optionId + '"' + checked + '>'
                    + '<span class="fs-5 flex-grow-1">' + esc(op.name) + extra + '</span></label>';
            });
            html += '</div>';
        });
        box.innerHTML = html;
        box.querySelectorAll('.mod-opt').forEach(function (inp) { inp.addEventListener('change', modValidate); });
        modValidate();
    }
    function modCollect() {
        var opts = [];
        document.querySelectorAll('#modGroups .mod-opt:checked').forEach(function (i) { opts.push(parseInt(i.value)); });
        return opts;
    }
    function modValidate() {
        var ok = true;
        document.querySelectorAll('#modGroups .mod-group').forEach(function (g) {
            var type = g.getAttribute('data-type'), min = parseInt(g.getAttribute('data-min')) || 0, max = parseInt(g.getAttribute('data-max')) || 0;
            var n = g.querySelectorAll('.mod-opt:checked').length;
            if (type === 'Choice') { if (n !== 1) ok = false; }
            else { if (n < min) ok = false; if (max > 0 && n > max) ok = false; }
            // enforce AddOn max at the UI: once max reached, disable the unchecked ones
            if (type !== 'Choice' && max > 0) {
                var atMax = n >= max;
                g.querySelectorAll('.mod-opt').forEach(function (i) { if (!i.checked) i.disabled = atMax; });
            }
        });
        var btn = document.getElementById('modConfirm');
        if (btn) { btn.disabled = !ok; btn.textContent = L.modAdd || 'Add to order'; }
    }
    var modConfirmBtn = document.getElementById('modConfirm');
    if (modConfirmBtn) modConfirmBtn.addEventListener('click', function () {
        if (modConfirmBtn.disabled) { toast('warning', L.modPickOne || ''); return; }
        var opts = modCollect(); var m = modal('modModal'); if (m) m.hide();
        postAddItem(modCurItem, opts);
    });
    // ────────────────────────────────────────────────────────────────────

    // ── RC-6b: Z report / shift close (read-only report; close calls RC-6a) ──
    var zCur = null;
    function money(n) { return fmt(n) + ' ' + CUR; }
    function varianceText(v) {
        if (Math.abs(v) < 0.005) return '<span class="text-muted">' + money(0) + '</span>';
        return v > 0 ? '<span class="text-success fw-bold">' + (L.zOver || 'Over') + ' ' + money(v) + '</span>'
                     : '<span class="text-danger fw-bold">' + (L.zShort || 'Short') + ' ' + money(-v) + '</span>';
    }
    function renderZ(z) {
        var pays = (z.payments || []).map(function (p) {
            return '<div class="d-flex justify-content-between py-1"><span>' + esc(p.method) + ' <span class="text-muted fs-8">(' + p.count + ')</span></span><span class="fw-bold">' + money(p.amount) + '</span></div>';
        }).join('') || '<div class="text-muted">—</div>';
        var box = document.getElementById('zBody');
        box.innerHTML =
            '<div class="d-flex justify-content-between py-1"><span class="text-muted">' + esc(z.terminalCode) + ' · ' + esc(z.shiftType) + '</span><span class="badge badge-light-' + (z.status === 'Closed' ? 'success' : 'primary') + '">#' + z.shiftId + '</span></div>'
            + '<div class="separator my-3"></div>'
            + '<div class="d-flex justify-content-between py-1"><span>' + (L.zOrders || 'Orders') + '</span><span class="fw-bold">' + z.orderCount + '</span></div>'
            + '<div class="d-flex justify-content-between py-1"><span>' + (L.subtotal || 'Subtotal') + '</span><span>' + money(z.subTotal) + '</span></div>'
            + (z.serviceAmount > 0 ? '<div class="d-flex justify-content-between py-1"><span>' + (L.service || 'Service') + '</span><span>' + money(z.serviceAmount) + '</span></div>' : '')
            + '<div class="d-flex justify-content-between py-1"><span>' + (L.vat || 'VAT') + '</span><span>' + money(z.taxTotal) + '</span></div>'
            + '<div class="d-flex justify-content-between py-1 fs-5 fw-bold"><span>' + (L.zSales || 'Total sales') + '</span><span>' + money(z.grandTotal) + '</span></div>'
            + (z.returnsTotal > 0 ? '<div class="d-flex justify-content-between py-1 text-danger"><span>' + (L.zShort || '') + '</span><span>-' + money(z.returnsTotal) + '</span></div>' : '')
            + '<div class="separator my-3"></div>'
            + '<div class="fw-bold mb-2">' + (L.paid || 'Payments') + '</div>' + pays
            + (z.tipsTotal > 0 ? '<div class="d-flex justify-content-between py-1 text-success"><span>' + (L.zTips || 'Tips') + '</span><span>' + money(z.tipsTotal) + '</span></div>' : '')
            + '<div class="separator my-3"></div>'
            + '<div class="d-flex justify-content-between py-1"><span class="text-muted">' + (L.zExpected || 'Expected cash') + '</span><span>' + money(z.expectedCash) + '</span></div>';
        // prefill counted with expected + live variance preview
        var ci = document.getElementById('zCounted');
        if (z.status === 'Closed') { ci.value = (z.closingFloat != null ? z.closingFloat : ''); ci.disabled = true; }
        else { ci.value = z.expectedCash.toFixed(2); ci.disabled = false; }
        previewVariance();
        // hide close button for an already-closed shift
        var cb = document.getElementById('zConfirmClose'); if (cb) cb.style.display = (z.status === 'Closed') ? 'none' : '';
    }
    function previewVariance() {
        if (!zCur) return;
        var counted = parseFloat((document.getElementById('zCounted') || {}).value) || 0;
        var v = Math.round((counted - zCur.expectedCash) * 100) / 100;
        var el = document.getElementById('zVariancePreview');
        if (el) el.innerHTML = (L.zVariance || 'Variance') + ': ' + varianceText(v);
    }
    function openZReport() {
        fetch(U.shiftZ, { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (j) {
            if (!j.ok) { toast('error', j.error || ''); return; }
            zCur = j.report; renderZ(zCur); var m = modal('zModal'); if (m) m.show();
        });
    }
    function zReceiptHtml(z, counted, variance) {
        var W = (RECEIPT.paperWidth == 58 ? 58 : 80), pad = (W == 58 ? 2 : 4);
        var rows = (z.payments || []).map(function (p) { return '<div class="row"><span>' + esc(p.method) + ' (' + p.count + ')</span><span>' + fmt(p.amount) + '</span></div>'; }).join('');
        var css = '@page{size:' + W + 'mm auto;margin:0}body{width:' + W + 'mm;margin:0;padding:' + pad + 'mm;font-family:Cairo,monospace;color:#000;font-size:' + (W == 58 ? 11 : 12) + 'px;line-height:1.5}.c{text-align:center}.b{font-weight:700}.row{display:flex;justify-content:space-between}hr{border:0;border-top:1px dashed #000;margin:5px 0}';
        return '<!doctype html><html dir="' + (RTL ? 'rtl' : 'ltr') + '"><head><meta charset="utf-8"><style>' + css + '</style></head><body>'
            + '<div class="c b">' + esc(RECEIPT.name || '') + '</div><div class="c">' + (L.zTitle || 'Z') + ' · ' + esc(z.terminalCode) + ' #' + z.shiftId + '</div><hr>'
            + '<div class="row"><span>' + (L.zOrders || 'Orders') + '</span><span>' + z.orderCount + '</span></div>'
            + '<div class="row"><span>' + (L.subtotal || '') + '</span><span>' + fmt(z.subTotal) + '</span></div>'
            + '<div class="row"><span>' + (L.vat || '') + '</span><span>' + fmt(z.taxTotal) + '</span></div>'
            + '<div class="row b"><span>' + (L.zSales || '') + '</span><span>' + fmt(z.grandTotal) + '</span></div><hr>' + rows
            + (z.tipsTotal > 0 ? '<div class="row"><span>' + (L.zTips || 'Tips') + '</span><span>' + fmt(z.tipsTotal) + '</span></div>' : '') + '<hr>'
            + '<div class="row"><span>' + (L.zExpected || '') + '</span><span>' + fmt(z.expectedCash) + '</span></div>'
            + '<div class="row"><span>' + (L.zCounted || '') + '</span><span>' + fmt(counted) + '</span></div>'
            + '<div class="row b"><span>' + (L.zVariance || '') + '</span><span>' + fmt(variance) + '</span></div>'
            + '<hr><div class="c">' + new Date().toLocaleString() + '</div></body></html>';
    }
    function printZ(z, counted, variance) {
        var f = document.createElement('iframe'); f.style.position = 'fixed'; f.style.right = '-9999px'; document.body.appendChild(f);
        f.contentDocument.open(); f.contentDocument.write(zReceiptHtml(z, counted, variance)); f.contentDocument.close();
        setTimeout(function () { try { f.contentWindow.focus(); f.contentWindow.print(); } catch (e) { } }, 350);
    }
    var btnCloseShift = document.getElementById('btnCloseShift');
    if (btnCloseShift) btnCloseShift.addEventListener('click', function (e) { e.preventDefault(); openZReport(); });
    var zCountedEl = document.getElementById('zCounted'); if (zCountedEl) zCountedEl.addEventListener('input', previewVariance);
    var zPrintOnly = document.getElementById('zPrintOnly');
    if (zPrintOnly) zPrintOnly.addEventListener('click', function () { if (!zCur) return; var c = parseFloat((document.getElementById('zCounted') || {}).value) || zCur.expectedCash; printZ(zCur, c, Math.round((c - zCur.expectedCash) * 100) / 100); });
    var zConfirmClose = document.getElementById('zConfirmClose');
    if (zConfirmClose) zConfirmClose.addEventListener('click', function () {
        if (!zCur || busy) return;
        var counted = parseFloat((document.getElementById('zCounted') || {}).value) || 0;
        var go = function () {
            busy = true;
            // POS-9e: offline → queue the close (server posts the variance JE at sync). Print a local Z from the offline figures.
            if (off()) {
                window.CBLO.queueShiftClose(counted).then(function () {
                    toast('success', L.zClosed || '');
                    printZ(zCur, counted, Math.round((counted - zCur.expectedCash) * 100) / 100);
                    setTimeout(function () { location.href = '/pos/start'; }, 700);
                }).finally(function () { busy = false; });
                return;
            }
            post(U.shiftClose, { closingFloat: counted }).then(function (j) {
                if (!j.ok) { toast('error', j.error || ''); return; }
                var z = j.report; toast('success', L.zClosed || '');
                printZ(z, z.closingFloat != null ? z.closingFloat : counted, z.cashVariance != null ? z.cashVariance : 0);
                setTimeout(function () { location.href = '/pos/start'; }, 700);
            }).finally(function () { busy = false; });
        };
        if (window.Swal) { Swal.fire({ text: L.zConfirmClose || 'Close?', icon: 'warning', showCancelButton: true, confirmButtonText: L.zTitle || 'Close', cancelButtonText: L.cancel || 'Cancel' }).then(function (r) { if (r.isConfirmed) go(); }); }
        else if (confirm(L.zConfirmClose || 'Close?')) go();
    });
    // ────────────────────────────────────────────────────────────────────

    // ── RC-6c: recent sales → void (c-1) / partial return (c-2), manager only ──
    function ask(msg, cb) {
        if (window.Swal) Swal.fire({ text: msg, icon: 'warning', showCancelButton: true, confirmButtonText: L.yes || 'OK', cancelButtonText: L.cancel || 'Cancel' }).then(function (r) { if (r.isConfirmed) cb(); });
        else if (confirm(msg)) cb();
    }
    function openRecent() {
        var rp = document.getElementById('returnPicker'); if (rp) rp.classList.add('d-none');
        var rl = document.getElementById('recentList'); if (rl) rl.classList.remove('d-none');
        fetch(U.recent, { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (j) {
            if (!j.ok) { toast('error', j.error || ''); return; }
            renderRecent(j.orders || []); var m = modal('recentModal'); if (m) m.show();
        });
    }
    function renderRecent(orders) {
        var box = document.getElementById('recentList'); if (!box) return;
        if (!orders.length) { box.innerHTML = '<div class="text-muted text-center py-6">' + esc(L.noRecent || '') + '</div>'; return; }
        box.innerHTML = orders.map(function (o) {
            var voided = o.status === 'Voided';
            var badge = voided ? '<span class="badge badge-light-danger">' + esc(L.voidedBadge || 'Voided') + '</span>' : '<span class="badge badge-light-success">' + money(o.grandTotal) + '</span>';
            var acts = voided ? '' :
                '<div class="d-flex gap-2"><button type="button" class="btn btn-sm btn-light-danger rc-void" data-id="' + o.id + '">' + esc(L.voidFull || '') + '</button>'
                + '<button type="button" class="btn btn-sm btn-light-warning rc-return" data-id="' + o.id + '">' + esc(L.partialReturn || '') + '</button></div>';
            return '<div class="d-flex align-items-center justify-content-between border border-gray-200 rounded-3 p-3 mb-2">'
                + '<div><div class="fw-bold">' + esc(o.receiptNo || ('#' + o.id)) + ' ' + badge + '</div>'
                + '<div class="text-muted fs-8">' + esc(o.customerName || '') + ' · ' + timeAgo(o.closedAt) + '</div></div>' + acts + '</div>';
        }).join('');
    }
    var retOrderId = 0;
    function openReturnPicker(orderId) {
        retOrderId = orderId;
        fetch(U.orderDetail + '?orderId=' + orderId, { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' })
            .then(function (r) { return r.json(); }).then(function (j) {
                if (!j.ok || !j.order) { toast('error', (j && j.error) || ''); return; }
                var lines = j.order.lines || [];
                document.getElementById('recentList').classList.add('d-none');
                var rp = document.getElementById('returnPicker'); rp.classList.remove('d-none');
                document.getElementById('returnLines').innerHTML = lines.map(function (l) {
                    return '<div class="d-flex align-items-center justify-content-between py-2 border-bottom border-gray-200 border-dashed ret-row" data-line="' + l.id + '" data-max="' + l.qty + '">'
                        + '<div class="flex-grow-1"><b>' + esc(l.name) + '</b> <span class="text-muted fs-8">× ' + l.qty + ' · ' + money(l.unitPrice) + '</span></div>'
                        + '<div class="d-flex align-items-center gap-2"><button type="button" class="btn btn-icon btn-sm btn-light rq-minus">−</button>'
                        + '<b class="rq-val" style="min-width:24px;text-align:center">0</b>'
                        + '<button type="button" class="btn btn-icon btn-sm btn-light rq-plus">+</button></div></div>';
                }).join('');
                retValidate();
            });
    }
    function retValidate() {
        var any = false;
        document.querySelectorAll('#returnLines .ret-row').forEach(function (r) { if ((parseFloat(r.querySelector('.rq-val').textContent) || 0) > 0) any = true; });
        var b = document.getElementById('returnConfirm'); if (b) b.disabled = !any;
    }
    function collectReturn() {
        var out = [];
        document.querySelectorAll('#returnLines .ret-row').forEach(function (r) {
            var q = parseFloat(r.querySelector('.rq-val').textContent) || 0;
            if (q > 0) out.push({ lineId: parseInt(r.getAttribute('data-line')), qty: q });
        });
        return out;
    }
    var recentListEl = document.getElementById('recentList');
    if (recentListEl) recentListEl.addEventListener('click', function (e) {
        var v = e.target.closest('.rc-void'), r = e.target.closest('.rc-return');
        if (v) { var id = parseInt(v.getAttribute('data-id')); ask(L.voidConfirm || 'Void?', function () { post(U.voidPaid, { orderId: id }).then(function (j) { if (j.ok) { toast('success', L.voided || ''); openRecent(); } else toast('error', j.error || ''); }); }); }
        else if (r) { openReturnPicker(parseInt(r.getAttribute('data-id'))); }
    });
    var returnLinesEl = document.getElementById('returnLines');
    if (returnLinesEl) returnLinesEl.addEventListener('click', function (e) {
        var row = e.target.closest('.ret-row'); if (!row) return;
        var val = row.querySelector('.rq-val'), cur = parseFloat(val.textContent) || 0, max = parseFloat(row.getAttribute('data-max')) || 0;
        if (e.target.closest('.rq-plus') && cur < max) val.textContent = cur + 1;
        else if (e.target.closest('.rq-minus') && cur > 0) val.textContent = cur - 1;
        retValidate();
    });
    var returnBack = document.getElementById('returnBack');
    if (returnBack) returnBack.addEventListener('click', function () { document.getElementById('returnPicker').classList.add('d-none'); document.getElementById('recentList').classList.remove('d-none'); });
    var returnConfirm = document.getElementById('returnConfirm');
    if (returnConfirm) returnConfirm.addEventListener('click', function () {
        var allocs = collectReturn(); if (!allocs.length) return;
        ask(L.returnConfirmMsg || 'Return?', function () {
            post(U.returnLines, { orderId: retOrderId, allocations: JSON.stringify(allocs) }).then(function (j) {
                if (j.ok) { toast('success', L.returned || ''); var m = modal('recentModal'); if (m) m.hide(); }
                else toast('error', j.error || '');
            });
        });
    });
    var btnRecent = document.getElementById('btnRecent');
    if (btnRecent) btnRecent.addEventListener('click', function (e) { e.preventDefault(); openRecent(); });
    // ────────────────────────────────────────────────────────────────────
    function setQty(id, q) { if (busy) return; if (off()) { window.CBLO.setQty(id, q).then(function (o) { order = o; render(); }); return; } busy = true; post(U.setq, { orderId: order.id, lineId: id, qty: q }).then(function (j) { if (j.ok) { order = j.order; render(); } else toast('error', j.error || ''); }).finally(function () { busy = false; }); }
    function delLine(id) { if (busy) return; if (off()) { window.CBLO.removeLine(id).then(function (o) { order = o; render(); }); return; } busy = true; post(U.del, { orderId: order.id, lineId: id }).then(function (j) { if (j.ok) { order = j.order; render(); } else toast('error', j.error || ''); }).finally(function () { busy = false; }); }

    document.querySelectorAll('.pos-item').forEach(function (b) { b.addEventListener('click', function () { addItem(parseInt(b.getAttribute('data-id'))); }); });
    linesEl.addEventListener('click', function (e) {
        if (!order) return;
        var m = e.target.closest('.qminus'), p = e.target.closest('.qplus'), d = e.target.closest('.qdel');
        if (m) { var l = order.lines.find(function (x) { return x.id == m.getAttribute('data-line'); }); if (l) setQty(l.id, l.qty - 1); }
        else if (p) { var l2 = order.lines.find(function (x) { return x.id == p.getAttribute('data-line'); }); if (l2) setQty(l2.id, l2.qty + 1); }
        else if (d) { delLine(parseInt(d.getAttribute('data-line'))); }
    });
    // «جديد» is the ONLY way to open a new invoice — and it CONFIRMS first if one is already open with items.
    var btnNew = document.getElementById('btnNew');
    if (btnNew) btnNew.addEventListener('click', function () {
        var startFresh = function () {
            post(U.create, { orderType: 'Takeaway', tableId: '' }).then(function (j) {
                if (!j.ok) { toast('error', j.error || ''); return; }
                order = j.order; tableId = null; tableCode = null; orderType = 'Takeaway';
                document.querySelectorAll('.otype').forEach(function (x) { x.classList.toggle('active', x.getAttribute('data-type') === 'Takeaway'); });
                if (btnPickTable) btnPickTable.classList.add('d-none');
                render(); paintTable(); toast('success', L.newTitle || '');
            });
        };
        var hasOrder = order && order.id;
        var hasItems = hasOrder && order.lines && order.lines.length;
        // already have an OPEN EMPTY order → don't pile up another empty one; keep this one.
        if (hasOrder && !hasItems) { toast('info', tr('عندك طلب فاضي مفتوح بالفعل — ضيف أصناف أو ألغيه الأول', 'You already have an empty open order — add items or cancel it first')); return; }
        if (!hasOrder) { startFresh(); return; }   // nothing open → just start
        var proceed = function () {
            // dine-in table order stays open on its table (guest still there); takeaway → park it (recallable), never discard
            if (orderType === 'Dine-in' && tableId) startFresh();
            else post(U.hold, { orderId: order.id }).then(function (j) { if (j.ok) toast('info', L.holdOk || ''); refreshHeldCount(); startFresh(); }).catch(startFresh);
        };
        var msg = tr('في فاتورة مفتوحة بأصناف — تبدأ فاتورة جديدة؟', 'An invoice with items is open — start a new one?');
        if (window.Swal) Swal.fire({ title: tr('فاتورة مفتوحة', 'Open invoice'), text: msg, icon: 'warning', showCancelButton: true, confirmButtonText: tr('نعم، جديدة', 'Yes, new'), cancelButtonText: tr('رجوع', 'Back'), confirmButtonColor: '#0E4A9E', reverseButtons: true }).then(function (r) { if (r.isConfirmed) proceed(); });
        else if (confirm(msg)) proceed();
    });

    // ================= POS-4c: hold / recall (takeaway parking) =================
    var btnHeld = document.getElementById('btnHeld'), heldCount = document.getElementById('heldCount');
    function refreshHeldCount() {
        fetch(U.openInv, { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (j) {
            if (!j || !j.ok || !heldCount) return;
            var n = (j.held || []).length;
            heldCount.textContent = n; heldCount.classList.toggle('d-none', n === 0);
        }).catch(function () { });
    }
    // location chip: table code / takeaway / held
    function invLoc(h) {
        if (h.tableId) return '<span class="badge badge-light-danger"><i class="ki-outline ki-geolocation fs-8"></i> ' + tr('طاولة ', 'Table ') + esc(h.tableCode || h.tableId) + '</span>';
        if (h.isHeld) return '<span class="badge badge-light-warning">' + tr('معلّقة', 'Held') + '</span>';
        return '<span class="badge badge-light-info">' + tr('سفري', 'Takeaway') + '</span>';
    }
    if (btnHeld) btnHeld.addEventListener('click', function () {
        var list = document.getElementById('heldList'), empty = document.getElementById('heldEmpty');
        if (list) list.innerHTML = '';
        fetch(U.openInv, { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (j) {
            var held = (j && j.held) || [];
            if (empty) empty.classList.toggle('d-none', held.length > 0);
            if (list) list.innerHTML = held.map(function (h) {
                var when = timeAgo(h.heldAtUtcIso);
                var empty0 = (h.itemCount || 0) === 0;
                return '<div class="d-flex align-items-center gap-3 border border-gray-200 rounded-3 p-3 bg-white shadow-sm">'
                    + '<span class="symbol symbol-45px"><span class="symbol-label bg-light-primary"><i class="ki-outline ki-receipt-square fs-2 text-primary"></i></span></span>'
                    + '<div class="d-flex flex-column flex-grow-1 min-w-0">'
                    + '<div class="d-flex align-items-center gap-2 flex-wrap"><span class="fw-bolder text-gray-900 fs-5">#' + h.id + '</span>'
                    + invLoc(h)
                    + '<span class="badge badge-light-primary">' + h.itemCount + ' ' + (L.items || '') + '</span>'
                    + (when ? '<span class="fs-8 text-muted ms-auto"><i class="ki-outline ki-time fs-8"></i> ' + when + '</span>' : '') + '</div>'
                    + '<span class="fs-7 text-gray-600 text-truncate"><i class="ki-outline ki-user fs-8"></i> ' + esc(custName(h.customerName)) + '</span>'
                    + '<span class="fs-8 text-muted text-truncate">' + (empty0 ? tr('لم يُطلب بعد', 'not ordered yet') : (esc(h.firstItem || '') + (h.itemCount > 1 ? (' +' + (h.itemCount - 1)) : ''))) + '</span></div>'
                    + '<div class="d-flex flex-column align-items-end gap-2">'
                    + '<span class="fw-bolder text-success fs-4">' + fmt(h.grandTotal) + ' <span class="fs-8">' + CUR + '</span></span>'
                    + '<button type="button" class="btn btn-sm btn-primary held-item px-4" data-id="' + h.id + '" data-tcode="' + esc(h.tableCode || '') + '"><i class="ki-outline ki-entrance-left fs-5"></i>' + tr('فتح', 'Open') + '</button></div>'
                    + '</div>';
            }).join('');
            var m = modal('heldModal'); if (m) m.show();
        });
    });
    var heldListEl = document.getElementById('heldList');
    if (heldListEl) heldListEl.addEventListener('click', function (e) {
        var b = e.target.closest('.held-item'); if (!b || busy) return; busy = true;
        var id = parseInt(b.getAttribute('data-id')), tcode = b.getAttribute('data-tcode') || '';
        post(U.load, { orderId: id }).then(function (j) {   // LOAD to view/continue — keep its held status
            if (j.ok && j.order) {
                order = j.order; orderType = order.orderType || 'Takeaway'; tableId = order.tableId || null; tableCode = tableId ? tcode : null;
                document.querySelectorAll('.otype').forEach(function (x) { x.classList.toggle('active', x.getAttribute('data-type') === orderType); });
                render(); paintTable(); refreshHeldCount(); toast('success', L.opened || L.recallOk || '');
                var m = modal('heldModal'); if (m) m.hide();
            } else toast('error', j.error || '');
        }).finally(function () { busy = false; });
    });

    // ================= payment modal (keypad + change) =================
    var elPayTotal = document.getElementById('payTotal'), elPayPaid = document.getElementById('payPaid'),
        elPayChange = document.getElementById('payChange'), payConfirm = document.getElementById('payConfirm'),
        payQuick = document.getElementById('payQuick'), cashPanel = document.getElementById('cashPanel'),
        splitRowEl = document.getElementById('splitRow'), methodsEl = document.getElementById('payMethods'),
        tendersPanel = document.getElementById('tendersPanel'), tenderRowsEl = document.getElementById('tenderRows'), tenderRemainEl = document.getElementById('tenderRemain');
    var PM = (P.payMethods && P.payMethods.length) ? P.payMethods : [{ method: 'Cash', name: 'Cash' }];
    var tenderMode = false;
    var splitParts = 1, splitPartsEl = document.getElementById('splitParts'), splitPerEl = document.getElementById('splitPer');
    function paintSplit() {
        if (splitPartsEl) splitPartsEl.textContent = splitParts;
        if (splitPerEl) splitPerEl.textContent = splitParts > 1 ? ((L.perPerson || 'Each') + ': ' + fmt(grand() / splitParts) + ' ' + CUR) : '';
    }
    var splitMinus = document.getElementById('splitMinus'), splitPlus = document.getElementById('splitPlus');
    if (splitMinus) splitMinus.addEventListener('click', function () { if (splitParts > 1) { splitParts--; paintSplit(); } });
    if (splitPlus) splitPlus.addEventListener('click', function () { if (splitParts < 20) { splitParts++; paintSplit(); } });
    function paidNum() { return parseFloat(payPaidStr || '0') || 0; }
    function refreshPay() {
        if (tenderMode) { refreshTenders(); return; }
        var g = grand(), cash = payMethod === 'Cash', paid = cash ? paidNum() : g, chg = paid - g;
        paintSplit();
        if (elPayTotal) elPayTotal.textContent = fmt(g);
        if (elPayPaid) elPayPaid.textContent = fmt(paid);
        if (elPayChange) elPayChange.textContent = fmt(chg > 0 ? chg : 0);
        if (cashPanel) cashPanel.classList.toggle('d-none', !cash);
        if (splitRowEl) splitRowEl.classList.toggle('d-none', !cash);   // equal-cash-split is a CASH concept
        if (payConfirm) payConfirm.disabled = !(order && order.lines && order.lines.length) || (cash && paid + 0.0001 < g);
    }
    // ---- payment methods rendered from the branch config (Cash + configured) ----
    function renderMethods() {
        if (!methodsEl) return;
        methodsEl.innerHTML = PM.map(function (m, i) {
            var ic = m.method === 'Cash' ? 'ki-dollar' : (m.method === 'Card' ? 'ki-credit-cart' : 'ki-wallet');
            return '<label class="btn bg-light btn-color-gray-600 btn-active-text-gray-800 border border-3 border-gray-100 border-active-primary btn-active-light-primary flex-grow-1 px-3 py-3' + (i === 0 ? ' active' : '') + '" style="min-width:92px">'
                + '<input class="btn-check paymethod" type="radio" name="method" value="' + esc(m.method) + '"' + (i === 0 ? ' checked' : '') + ' />'
                + '<i class="ki-outline ' + ic + ' fs-2hx mb-2 pe-0"></i><span class="fs-7 fw-bold d-block">' + esc(m.name) + '</span></label>';
        }).join('');
    }
    if (methodsEl) methodsEl.addEventListener('change', function () {
        var r = methodsEl.querySelector('.paymethod:checked'); if (!r) return;
        payMethod = r.value; payPaidStr = '';
        methodsEl.querySelectorAll('label').forEach(function (l) { l.classList.remove('active'); });
        r.closest('label').classList.add('active'); refreshPay();
    });
    // ---- multi-tender (split across methods) ----
    function tenderRowHtml(sel, amt) {
        var opts = PM.map(function (m) { return '<option value="' + esc(m.method) + '"' + (m.method === sel ? ' selected' : '') + '>' + esc(m.name) + '</option>'; }).join('');
        return '<div class="d-flex gap-2 tender-row"><select class="form-select form-select-sm form-select-solid tmethod" style="max-width:45%">' + opts + '</select>'
            + '<input type="number" step="0.01" min="0" class="form-control form-control-sm form-control-solid tamount text-end" value="' + (amt != null ? amt : '') + '" placeholder="0.00">'
            + '<button type="button" class="btn btn-sm btn-icon btn-light-danger trem"><i class="ki-outline ki-cross fs-4"></i></button></div>';
    }
    function collectTenders() {
        var rows = tenderRowsEl ? [].slice.call(tenderRowsEl.querySelectorAll('.tender-row')) : [];
        return rows.map(function (r) { return { method: r.querySelector('.tmethod').value, amount: parseFloat(r.querySelector('.tamount').value) || 0 }; }).filter(function (t) { return t.amount > 0; });
    }
    function refreshTenders() {
        var g = grand(), sum = collectTenders().reduce(function (a, t) { return a + t.amount; }, 0), rem = g - sum;
        if (tenderRemainEl) { tenderRemainEl.textContent = fmt(rem > 0 ? rem : 0); tenderRemainEl.className = 'fw-bold ' + (rem > 0.0001 ? 'text-danger' : 'text-success'); }
        if (elPayTotal) elPayTotal.textContent = fmt(g);
        if (elPayPaid) elPayPaid.textContent = fmt(sum);
        if (elPayChange) elPayChange.textContent = fmt(sum - g > 0 ? sum - g : 0);
        if (payConfirm) payConfirm.disabled = !(order && order.lines && order.lines.length) || sum + 0.0001 < g;
    }
    function addTenderRow(method, amt) { if (!tenderRowsEl) return; var d = document.createElement('div'); d.innerHTML = tenderRowHtml(method || 'Cash', amt); tenderRowsEl.appendChild(d.firstChild); refreshTenders(); }
    if (tenderRowsEl) {
        tenderRowsEl.addEventListener('input', refreshTenders);
        tenderRowsEl.addEventListener('change', refreshTenders);
        tenderRowsEl.addEventListener('click', function (e) { var b = e.target.closest('.trem'); if (b) { b.closest('.tender-row').remove(); refreshTenders(); } });
    }
    var btnAddTender = document.getElementById('btnAddTender'); if (btnAddTender) btnAddTender.addEventListener('click', function () { addTenderRow('Cash', ''); });
    var btnTenders = document.getElementById('btnTenders'); if (btnTenders) btnTenders.addEventListener('click', function () {
        if (off()) { toast('info', L.offlineSingleOnly || (RTL ? 'الدفع المتعدد يتطلب اتصالًا' : 'Split payment needs a connection')); return; }   // POS-9c
        tenderMode = !tenderMode;
        if (methodsEl) methodsEl.classList.toggle('d-none', tenderMode);
        if (cashPanel) cashPanel.classList.toggle('d-none', tenderMode);
        if (splitRowEl) splitRowEl.classList.toggle('d-none', tenderMode);
        if (tendersPanel) tendersPanel.classList.toggle('d-none', !tenderMode);
        if (tenderMode && tenderRowsEl) { tenderRowsEl.innerHTML = ''; addTenderRow('Cash', grand().toFixed(2)); }
        refreshPay();
    });
    // RC-5 (TIP-2): read the optional tip field + method → merged into the pay POST (server posts the tip JE to 210207)
    function withTip(d) {
        var a = parseFloat((document.getElementById('tipAmount') || {}).value) || 0;
        if (a > 0) { d.tipAmount = a; d.tipMethod = (document.querySelector('input[name=tipMethod]:checked') || {}).value || 'Cash'; }
        return d;
    }
    function doPayTenders(snapshot, ts, paid, chg) {
        if (busy) return; busy = true; payConfirm.disabled = true;
        post(U.payTenders, withTip({ orderId: snapshot.id, tenders: JSON.stringify(ts) })).then(function (j) {
            if (j.ok) {
                var m = modal('payModal'); if (m) m.hide();
                try { printReceipt(snapshot, j.receiptNo, paid, chg); } catch (e) { }
                if (window.Swal) Swal.fire({ title: L.paid, html: esc(L.receipt) + ': <b>' + esc(j.receiptNo || '-') + '</b>', icon: 'success', timer: 2000, showConfirmButton: false });
                setTimeout(function () { location.href = '/pos/terminal'; }, 1600);
            } else { toast('error', j.error || L.payFailed); busy = false; payConfirm.disabled = false; }
        }).catch(function () { busy = false; payConfirm.disabled = false; });
    }
    function buildQuick() {
        if (!payQuick) return; var g = grand(); if (g <= 0) { payQuick.innerHTML = ''; return; }
        var vals = [g, Math.ceil(g / 10) * 10, Math.ceil(g / 50) * 50, g + 50, g + 100];
        var uniq = []; vals.forEach(function (v) { v = Math.round(v * 100) / 100; if (v >= g && uniq.indexOf(v) < 0) uniq.push(v); });
        payQuick.innerHTML = uniq.slice(0, 4).map(function (v) { return '<button type="button" class="btn btn-sm btn-light-primary flex-grow-1 qcash" data-v="' + v + '">' + fmt(v) + '</button>'; }).join('');
    }
    if (payQuick) payQuick.addEventListener('click', function (e) { var b = e.target.closest('.qcash'); if (b) { payPaidStr = b.getAttribute('data-v'); refreshPay(); } });
    // one place handles a "key" whether it came from the on-screen pad or the physical keyboard
    function padPress(k) {
        var dec = payPaidStr.indexOf('.'), decLen = dec >= 0 ? payPaidStr.length - dec - 1 : 0;
        if (k === 'clear') payPaidStr = '';
        else if (k === 'back') payPaidStr = payPaidStr.slice(0, -1);
        else if (k === '.') { if (dec < 0) payPaidStr = (payPaidStr === '' ? '0' : payPaidStr) + '.'; }
        else if (k === '00') { if (payPaidStr !== '' && decLen === 0) payPaidStr += '00'; }
        else { if (decLen < 2) payPaidStr = (payPaidStr + k).replace(/^0+(?=\d)/, ''); }   // digit, max 2 decimals
        refreshPay();
    }
    var payPad = document.getElementById('payPad');
    if (payPad) payPad.addEventListener('click', function (e) { var b = e.target.closest('.padkey'); if (b) padPress(b.getAttribute('data-k')); });
    // PHYSICAL KEYBOARD: works only while the payment modal is open + Cash is selected
    document.addEventListener('keydown', function (e) {
        var pm = document.getElementById('payModal'); if (!pm || !pm.classList.contains('show')) return;
        if (e.ctrlKey || e.altKey || e.metaKey) return;
        if (e.key >= '0' && e.key <= '9') { padPress(e.key); e.preventDefault(); }
        else if (e.key === '.' || e.key === ',') { padPress('.'); e.preventDefault(); }
        else if (e.key === 'Backspace') { padPress('back'); e.preventDefault(); }
        else if (e.key === 'Delete') { padPress('clear'); e.preventDefault(); }
        else if (e.key === 'Enter') { if (payConfirm && !payConfirm.disabled) payConfirm.click(); e.preventDefault(); }
    });
    if (btnPay) btnPay.addEventListener('click', function () {
        if (!order || !order.lines.length) return;
        payPaidStr = ''; payMethod = 'Cash'; splitParts = 1; tenderMode = false;
        var tipEl = document.getElementById('tipAmount'); if (tipEl) tipEl.value = '';   // RC-5: reset tip each open
        var tipCash = document.getElementById('tipCash'); if (tipCash) tipCash.checked = true;
        renderMethods();
        if (methodsEl) methodsEl.classList.remove('d-none');
        if (tendersPanel) tendersPanel.classList.add('d-none');
        buildQuick(); refreshPay();
        var m = modal('payModal'); if (m) m.show();
    });
    // does the actual settlement + receipt print (called only AFTER the cashier confirms the change)
    function doPay(snapshot, paid, chg) {
        if (busy) return; busy = true; payConfirm.disabled = true;
        post(U.pay, withTip({ orderId: snapshot.id, method: 'Cash', parts: splitParts })).then(function (j) {
            if (j.ok) {
                var m = modal('payModal'); if (m) m.hide();
                try { printReceipt(snapshot, j.receiptNo, paid, chg); } catch (e) { }
                if (window.Swal) { Swal.fire({ title: L.paid, html: esc(L.change) + ': <b>' + fmt(chg) + ' ' + CUR + '</b><br>' + esc(L.receipt) + ': <b>' + esc(j.receiptNo || '-') + '</b>', icon: 'success', timer: 2200, showConfirmButton: false }); }
                setTimeout(function () { location.href = '/pos/terminal'; }, 1800);
            } else { toast('error', j.error || L.payFailed); busy = false; payConfirm.disabled = false; }
        }).catch(function () { busy = false; payConfirm.disabled = false; });
    }
    // POS-9c: read the optional tip as an object (for the offline settle payload)
    function readTipObj() { var a = parseFloat((document.getElementById('tipAmount') || {}).value) || 0; return a > 0 ? { amount: a, method: (document.querySelector('input[name=tipMethod]:checked') || {}).value || 'Cash' } : null; }
    // POS-9c: finalize offline — CBLO assigns a local receipt no + enqueues the settled order (paid) for sync (9d). No server post.
    function offlinePay(method, paid) {
        if (busy) return; busy = true;
        window.CBLO.pay(method, readTipObj()).then(function (r) {
            var m = modal('payModal'); if (m) m.hide();
            var g = r.snapshot.grandTotal, isCash = method === 'Cash';
            printReceipt(r.snapshot, r.receiptNo, isCash ? paid : g, isCash ? (paid - g) : 0);
            order = null; render();
            toast('success', L.offlineQueued || (RTL ? 'تم الدفع بلا نت — سيُزامن عند العودة' : 'Paid offline — will sync'));
        }).catch(function () { toast('error', ''); }).finally(function () { busy = false; });
    }
    if (payConfirm) payConfirm.addEventListener('click', function () {
        if (!order || !order.lines.length) return;
        var g0 = grand();
        if (off()) {   // POS-9c: offline settle — single method only (Cash/Card); multi-tender/split need the network
            if (tenderMode) { toast('info', L.offlineSingleOnly || (RTL ? 'الدفع المتعدد يتطلب اتصالًا' : 'Split payment needs a connection')); return; }
            if (payMethod === 'Cash') { var pd = paidNum(); if (pd + 0.0001 < g0) { toast('warning', L.needPaid); return; } offlinePay('Cash', pd); }
            else offlinePay(payMethod, g0);
            return;
        }
        if (tenderMode) {   // split across multiple methods
            var ts = collectTenders(), sum = ts.reduce(function (a, t) { return a + t.amount; }, 0);
            if (sum + 0.0001 < g0) { toast('warning', L.needPaid); return; }
            doPayTenders(order, ts, sum, sum - g0); return;
        }
        if (payMethod !== 'Cash') {   // single non-cash → exact, no change
            doPayTenders(order, [{ method: payMethod, amount: g0 }], g0, 0); return;
        }
        var g = grand(), paid = paidNum(); if (paid + 0.0001 < g) { toast('warning', L.needPaid); return; }
        var chg = paid - g, snapshot = order;
        // FIRST show the cashier the change to hand back; only after they confirm → pay + print
        var giveTxt = RTL ? 'سلّم الباقي للزبون' : 'Give change to customer';
        var okTxt = RTL ? 'تم التسليم — ادفع واطبع' : 'Given — pay & print';
        var backTxt = RTL ? 'رجوع' : 'Back';
        if (window.Swal) {
            Swal.fire({
                title: giveTxt,
                html: '<div style="font-size:54px;font-weight:800;color:#1f9d5c;line-height:1.05;margin:8px 0">' + fmt(chg) + ' ' + esc(CUR) + '</div>'
                    + '<div style="font-size:14px;color:#7e8299">' + esc(L.total) + ': <b>' + fmt(g) + '</b> &nbsp;·&nbsp; ' + esc(L.paid) + ': <b>' + fmt(paid) + '</b></div>',
                icon: chg > 0.0001 ? 'info' : 'success',
                showCancelButton: true, confirmButtonText: okTxt, cancelButtonText: backTxt,
                confirmButtonColor: '#0E4A9E', reverseButtons: true
            }).then(function (r) { if (r.isConfirmed) doPay(snapshot, paid, chg); });
        } else {
            if (confirm(giveTxt + ': ' + fmt(chg) + ' ' + CUR)) doPay(snapshot, paid, chg);
        }
    });

    // ===== POS-4d-3b: split BY ITEM — assign each line's units to N bills → N cash invoices =====
    var siBills = 2, siAlloc = {}, siBillsEl = document.getElementById('siBills');
    function siUnit(l) { return (l.qty > 0) ? (l.lineTotal / l.qty) : 0; }
    function siInit() { siAlloc = {}; (order.lines || []).forEach(function (l) { var a = []; for (var b = 0; b < siBills; b++) a.push(b === 0 ? l.qty : 0); siAlloc[l.id] = a; }); }
    function siLineSum(l) { var s = 0, a = siAlloc[l.id] || []; for (var b = 0; b < siBills; b++) s += (a[b] || 0); return s; }
    function siBillSub(b) { var s = 0; (order.lines || []).forEach(function (l) { s += (siAlloc[l.id][b] || 0) * siUnit(l); }); return s; }
    function renderSplitItems() {
        var head = document.getElementById('siHead'), body = document.getElementById('siBody'), foot = document.getElementById('siFoot');
        if (!head) return;
        var bl = RTL ? 'فاتورة' : 'Bill';
        var h = '<tr><th class="ps-0">' + (RTL ? 'الصنف' : 'Item') + '</th>';
        for (var b = 0; b < siBills; b++) h += '<th class="text-center">' + bl + ' ' + (b + 1) + '</th>';
        head.innerHTML = h + '</tr>';
        body.innerHTML = (order.lines || []).map(function (l) {
            var rem = l.qty - siLineSum(l);
            var nm = '<td class="ps-0"><span class="fw-bold text-gray-900">' + esc(l.name) + '</span><span class="text-muted fs-8 d-block">×' + l.qty + (Math.abs(rem) > 0.0001 ? ' · <span class="text-danger">' + (RTL ? 'متبقّي ' : 'left ') + rem + '</span>' : '') + '</span></td>';
            var cells = '';
            for (var b = 0; b < siBills; b++) {
                var v = siAlloc[l.id][b] || 0;
                cells += '<td class="text-center"><span class="d-inline-flex align-items-center gap-1">'
                    + '<button type="button" class="btn btn-light-danger sidec" style="width:26px;height:26px;padding:0" data-line="' + l.id + '" data-b="' + b + '">−</button>'
                    + '<b style="min-width:18px;display:inline-block">' + v + '</b>'
                    + '<button type="button" class="btn btn-light-primary siinc" style="width:26px;height:26px;padding:0" data-line="' + l.id + '" data-b="' + b + '">+</button>'
                    + '</span></td>';
            }
            return '<tr>' + nm + cells + '</tr>';
        }).join('');
        var f = '<tr><td class="ps-0 text-muted">' + (RTL ? 'إجمالي جزئي' : 'Subtotal') + '</td>';
        for (var b2 = 0; b2 < siBills; b2++) f += '<td class="text-center text-primary">' + fmt(siBillSub(b2)) + '</td>';
        foot.innerHTML = f + '</tr>';
        var allFull = (order.lines || []).every(function (l) { return Math.abs(siLineSum(l) - l.qty) < 0.0001; });
        var emptyBill = false;
        for (var b3 = 0; b3 < siBills; b3++) { var any = false; (order.lines || []).forEach(function (l) { if ((siAlloc[l.id][b3] || 0) > 0) any = true; }); if (!any) emptyBill = true; }
        var msg = !allFull ? (RTL ? 'في أصناف لسه متوزّعتش بالكامل' : 'Some items not fully allocated') : (emptyBill ? (RTL ? 'في فاتورة فاضية — وزّع عليها صنف' : 'A bill is empty') : '');
        var warn = document.getElementById('siWarn'), conf = document.getElementById('siConfirm');
        if (warn) { warn.textContent = msg; warn.classList.toggle('d-none', !msg); }
        if (conf) conf.disabled = !!msg;
        if (siBillsEl) siBillsEl.textContent = siBills;
    }
    function openSplitItems() {
        if (!order || !order.lines || !order.lines.length) return;
        if (off()) { toast('info', L.offlineSingleOnly || (RTL ? 'التقسيم بالأصناف يتطلب اتصالًا' : 'Split-by-item needs a connection')); return; }   // POS-9c
        siBills = 2; siInit();
        var pm = modal('payModal'); if (pm) pm.hide();
        setTimeout(function () { renderSplitItems(); var m = modal('splitItemsModal'); if (m) m.show(); }, 260);
    }
    var btnSplitItems = document.getElementById('btnSplitItems');
    if (btnSplitItems) btnSplitItems.addEventListener('click', openSplitItems);
    var siMinusB = document.getElementById('siMinus'), siPlusB = document.getElementById('siPlus');
    if (siMinusB) siMinusB.addEventListener('click', function () { if (siBills > 2) { siBills--; siInit(); renderSplitItems(); } });
    if (siPlusB) siPlusB.addEventListener('click', function () { if (siBills < 10) { siBills++; siInit(); renderSplitItems(); } });
    var siBodyEl = document.getElementById('siBody');
    if (siBodyEl) siBodyEl.addEventListener('click', function (e) {
        var inc = e.target.closest('.siinc'), dec = e.target.closest('.sidec'), btn = inc || dec; if (!btn) return;
        var lid = parseInt(btn.getAttribute('data-line')), b = parseInt(btn.getAttribute('data-b'));
        var line = (order.lines || []).find(function (x) { return x.id === lid; }); if (!line) return;
        var a = siAlloc[lid], cur = a[b] || 0;
        if (inc) {
            if (siLineSum(line) < line.qty) a[b] = cur + 1;                 // assign a still-unallocated unit
            else { for (var j = 0; j < siBills; j++) { if (j !== b && (a[j] || 0) > 0) { a[j]--; a[b] = cur + 1; break; } } }  // fully allocated → MOVE a unit from another bill
        } else { if (cur > 0) a[b] = cur - 1; }
        renderSplitItems();
    });
    var siConfirmB = document.getElementById('siConfirm');
    if (siConfirmB) siConfirmB.addEventListener('click', function () {
        if (busy || !order) return;
        var bills = [];
        for (var b = 0; b < siBills; b++) {
            var arr = [];
            (order.lines || []).forEach(function (l) { var q = siAlloc[l.id][b] || 0; if (q > 0) arr.push({ lineId: l.id, qty: q }); });
            if (arr.length) bills.push(arr);
        }
        if (!bills.length) return;
        busy = true; siConfirmB.disabled = true;
        post(U.paySplitItems, withTip({ orderId: order.id, bills: JSON.stringify(bills) })).then(function (j) {
            if (j.ok) {
                var m = modal('splitItemsModal'); if (m) m.hide();
                var n = (j.invoiceIds || []).length;
                if (window.Swal) Swal.fire({ title: L.paid || 'Paid', html: (RTL ? ('تم إنشاء <b>' + n + '</b> فواتير') : ('<b>' + n + '</b> bills created')) + '<br>' + esc(L.receipt || 'Receipt') + ': <b>' + esc(j.receiptNo || '-') + '</b>', icon: 'success', timer: 2400, showConfirmButton: false });
                setTimeout(function () { location.href = '/pos/terminal'; }, 1900);
            } else { toast('error', j.error || L.payFailed || ''); busy = false; siConfirmB.disabled = false; }
        }).catch(function () { busy = false; siConfirmB.disabled = false; });
    });

    // ================= Customer (link order to a customer; defaults to Walk-in) =================
    var custNameEl = document.getElementById('custName'), btnCustomer = document.getElementById('btnCustomer'), custT;
    function paintCustomer() {
        if (!custNameEl) return;
        custNameEl.textContent = (order && order.customerName) ? order.customerName : (L.walkIn || 'Walk-in');
    }
    function custDoSearch(q) {
        var box = document.getElementById('custResults'); if (!box) return;
        fetch(U.custSearch + '?q=' + encodeURIComponent(q || ''), { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (j) {
            var cs = (j && j.customers) || [];
            box.innerHTML = cs.length ? cs.map(function (c) {
                return '<button type="button" class="btn btn-flex btn-active-light-primary border border-gray-200 rounded-3 p-3 text-start cust-pick" data-id="' + c.id + '">'
                    + '<span class="symbol symbol-35px me-3"><span class="symbol-label bg-light-primary"><i class="ki-outline ki-user fs-5 text-primary"></i></span></span>'
                    + '<span class="d-flex flex-column"><span class="fw-bold text-gray-800 fs-7">' + esc(c.name) + '</span><span class="fs-8 text-muted">' + esc(c.phone || '') + '</span></span></button>';
            }).join('') : '<div class="text-center text-muted py-4">' + esc(L.noCust || '') + '</div>';
        }).catch(function () { });
    }
    function pickCustomer(custId) {
        if (!order || !order.id) return;
        post(U.setCust, { orderId: order.id, customerId: custId }).then(function (j) {
            if (j.ok && j.order) { order = j.order; paintCustomer(); toast('success', L.custSet || ''); var m = modal('customerModal'); if (m) m.hide(); }
            else toast('error', j.error || '');
        });
    }
    if (btnCustomer) btnCustomer.addEventListener('click', function () {
        // need an order to attach to; takeaway → create one, dine-in → needs a table (ensure handles it)
        ensure().then(function (oid) { if (!oid) return; custDoSearch(''); var m = modal('customerModal'); if (m) m.show(); });
    });
    var custSearchEl = document.getElementById('custSearch');
    if (custSearchEl) custSearchEl.addEventListener('input', function () { clearTimeout(custT); var q = this.value; custT = setTimeout(function () { custDoSearch(q); }, 250); });
    var custResultsEl = document.getElementById('custResults');
    if (custResultsEl) custResultsEl.addEventListener('click', function (e) { var b = e.target.closest('.cust-pick'); if (b) pickCustomer(parseInt(b.getAttribute('data-id'))); });
    var ncSave = document.getElementById('ncSave');
    if (ncSave) ncSave.addEventListener('click', function () {
        var name = (document.getElementById('ncName').value || '').trim(); if (!name) return;
        var phone = (document.getElementById('ncPhone').value || '').trim();
        post(U.custAdd, { name: name, phone: phone }).then(function (j) {
            if (j.ok && j.customer) { toast('success', L.custAdded || ''); document.getElementById('ncName').value = ''; document.getElementById('ncPhone').value = ''; pickCustomer(j.customer.id); }
            else toast('error', j.error || '');
        });
    });

    // ============ 3-PANEL SHELL: category filter, item search, current-invoices column, rails/footer ============
    function setTxt(id, v) { var e = document.getElementById(id); if (e) e.textContent = v; }

    // ===== menu favourites & tools — DESIGN ONLY, client-side (localStorage); no order/pricing logic touched =====
    var posItemsEl = document.getElementById('posItems'), pcFav = document.getElementById('pcFav'), pcTop = document.getElementById('pcTop'), pcFilter = document.getElementById('pcFilter');
    var FAVKEY = 'cbFavItems', POPKEY = 'cbItemPops';
    function jparse(k) { try { return JSON.parse(localStorage.getItem(k) || (k === POPKEY ? '{}' : '[]')); } catch (e) { return k === POPKEY ? {} : []; } }
    function jsave(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch (e) { } }
    function paintFavs() { var f = jparse(FAVKEY); document.querySelectorAll('#posItems .pi-fav').forEach(function (b) { var on = f.indexOf(b.getAttribute('data-fav')) >= 0; b.classList.toggle('active', on); var i = b.querySelector('i'); if (i) i.className = (on ? 'ki-solid' : 'ki-outline') + ' ki-heart'; }); }
    // heart toggles a favourite — capture phase so it never triggers the card's add-to-order click
    if (posItemsEl) posItemsEl.addEventListener('click', function (e) {
        var h = e.target.closest('.pi-fav'); if (!h) return;
        e.stopPropagation(); e.preventDefault();
        var id = h.getAttribute('data-fav'), f = jparse(FAVKEY), i = f.indexOf(id);
        if (i >= 0) f.splice(i, 1); else f.push(id); jsave(FAVKEY, f); paintFavs();
    }, true);
    // track how often each item is ordered on THIS device (client-only) → powers «الأكثر طلباً»
    if (posItemsEl) posItemsEl.addEventListener('click', function (e) {
        var card = e.target.closest('.pos-item'); if (!card || e.target.closest('.pi-fav')) return;
        var id = card.getAttribute('data-id'), p = jparse(POPKEY); p[id] = (p[id] || 0) + 1; jsave(POPKEY, p);
    });
    function clearToolActive() { if (pcFav) pcFav.classList.remove('active'); if (pcTop) pcTop.classList.remove('active'); }
    function showAllItems() { document.querySelectorAll('#posItems .posx-item').forEach(function (it) { it.style.display = ''; }); }
    // category tabs — filter the flat grid by data-cat
    var posCats = document.getElementById('posCats');
    if (posCats) posCats.addEventListener('click', function (e) {
        var b = e.target.closest('.posx-cat'); if (!b) return;
        clearToolActive();
        posCats.querySelectorAll('.posx-cat').forEach(function (x) { x.classList.remove('active'); }); b.classList.add('active');
        var cat = b.getAttribute('data-cat');
        document.querySelectorAll('#posItems .posx-item').forEach(function (it) { it.style.display = (cat === 'all' || it.getAttribute('data-cat') === cat) ? '' : 'none'; });
    });
    // «المفضلة» — show only favourited items
    if (pcFav) pcFav.addEventListener('click', function () {
        if (pcTop) pcTop.classList.remove('active');
        if (posCats) posCats.querySelectorAll('.posx-cat').forEach(function (x) { x.classList.remove('active'); });
        var on = pcFav.classList.toggle('active');
        if (!on) { showAllItems(); return; }
        var f = jparse(FAVKEY);
        document.querySelectorAll('#posItems .posx-item').forEach(function (it) { it.style.display = f.indexOf(it.getAttribute('data-id')) >= 0 ? '' : 'none'; });
    });
    // «الأكثر طلباً» — reorder visible items by this device's order frequency (client-only)
    if (pcTop) pcTop.addEventListener('click', function () {
        if (pcFav) pcFav.classList.remove('active');
        if (posCats) posCats.querySelectorAll('.posx-cat').forEach(function (x) { x.classList.remove('active'); });
        var on = pcTop.classList.toggle('active');
        var grid = posItemsEl; if (!grid) return;
        var cards = [].slice.call(grid.querySelectorAll('.posx-item'));
        if (on) {
            var p = jparse(POPKEY);
            cards.sort(function (a, b) { return (p[b.getAttribute('data-id')] || 0) - (p[a.getAttribute('data-id')] || 0); });
        } else {
            cards.sort(function (a, b) { return (parseInt(a.getAttribute('data-id')) || 0) - (parseInt(b.getAttribute('data-id')) || 0); });
        }
        cards.forEach(function (c) { c.style.display = ''; grid.appendChild(c); });
    });
    if (pcFilter) pcFilter.addEventListener('click', function () { var s = document.getElementById('itemSearch') || document.getElementById('hdrSearch'); if (s) s.focus(); });
    paintFavs();
    // item search (menu search + header search both filter the grid)
    function filterItems(q) { q = (q || '').trim().toLowerCase(); if (typeof clearToolActive === 'function') clearToolActive(); document.querySelectorAll('#posItems .posx-item').forEach(function (it) { var n = (it.getAttribute('data-name') || '').toLowerCase(); it.style.display = (!q || n.indexOf(q) >= 0) ? '' : 'none'; }); }
    ['itemSearch', 'hdrSearch'].forEach(function (id) { var el = document.getElementById(id); if (el) el.addEventListener('input', function () { filterItems(el.value); }); });

    // current-invoices column (left): all OPEN invoices with a location chip; tap to open
    var invFilter = 'all';
    function invLoc(h) {
        if (h.tableId) return { txt: tr('طاولة ', 'Table ') + (h.tableCode || h.tableId), cls: 's-blue', chip: 'green' };
        if (h.isHeld) return { txt: tr('معلّقة', 'Held'), cls: 's-amber', chip: 'amber' };
        return { txt: tr('سفري', 'Takeaway'), cls: '', chip: 'green' };
    }
    function renderInvoices() {
        fetch(U.openInv, { headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (j) {
            var list = (j && j.held) || [];
            var dine = list.filter(function (x) { return x.tableId; }), held = list.filter(function (x) { return !x.tableId && x.isHeld; }), take = list.filter(function (x) { return !x.tableId && !x.isHeld; });
            setTxt('cntAll', list.length); setTxt('cntDine', dine.length); setTxt('cntHeld2', held.length); setTxt('cntTake', take.length);
            var show = invFilter === 'dinein' ? dine : invFilter === 'held' ? held : invFilter === 'takeaway' ? take : list;
            var box = document.getElementById('invList'), empty = document.getElementById('invEmpty');
            if (empty) empty.classList.toggle('d-none', show.length > 0);
            if (!box) return;
            box.innerHTML = show.map(function (h) {
                var loc = invLoc(h), when = timeAgo(h.heldAtUtcIso), big = h.tableId ? (h.tableCode || h.tableId) : ('#' + h.id);
                return '<div class="posx-inv ' + loc.cls + '" data-open="' + h.id + '" data-tcode="' + esc(h.tableCode || '') + '">'
                    + '<div class="big">' + esc(big) + '</div>'
                    + '<div class="mid"><div class="r1"><i class="ki-outline ki-user fs-8"></i> ' + esc(custName(h.customerName)) + '</div>'
                    + '<div class="r1"><span class="posx-chip ' + loc.chip + '">' + esc(loc.txt) + '</span> · ' + h.itemCount + ' ' + (L.items || '') + (when ? (' · ' + when) : '') + '</div></div>'
                    + '<div class="amt">' + fmt(h.grandTotal) + ' <small>' + CUR + '</small></div></div>';
            }).join('');
        }).catch(function () { });
    }
    document.querySelectorAll('#invTabs .posx-tab').forEach(function (t) {
        t.addEventListener('click', function () { document.querySelectorAll('#invTabs .posx-tab').forEach(function (x) { x.classList.remove('active'); }); t.classList.add('active'); invFilter = t.getAttribute('data-f'); renderInvoices(); });
    });
    var invListEl2 = document.getElementById('invList');
    if (invListEl2) invListEl2.addEventListener('click', function (e) {
        var c = e.target.closest('[data-open]'); if (!c || busy) return; busy = true;
        var id = parseInt(c.getAttribute('data-open')), tcode = c.getAttribute('data-tcode') || '';
        post(U.load, { orderId: id }).then(function (j) {   // LOAD (view/continue) — does NOT change the invoice's status
            if (j.ok && j.order) {
                order = j.order; orderType = order.orderType || 'Takeaway'; tableId = order.tableId || null; tableCode = tableId ? tcode : null;
                document.querySelectorAll('.otype').forEach(function (x) { x.classList.toggle('active', x.getAttribute('data-type') === orderType); });
                if (btnPickTable) btnPickTable.classList.toggle('d-none', orderType !== 'Dine-in');
                render(); paintTable(); renderInvoices(); toast('success', L.opened || L.recallOk || '');
            } else toast('error', j.error || '');
        }).finally(function () { busy = false; });
    });

    // «تعليق» (hold current takeaway → parks it, sidebar clears, shows in the invoices column)
    var btnHold = document.getElementById('btnHold');
    if (btnHold) btnHold.addEventListener('click', function () {
        if (!(order && order.id && order.lines && order.lines.length)) { toast('info', L.pickTable ? '' : ''); return; }
        if (orderType === 'Dine-in' && tableId) { toast('info', tr('طلبات الصالة على طاولاتها', 'Table orders stay on their tables')); return; }
        post(U.hold, { orderId: order.id }).then(function (j) {
            if (j.ok) {
                order = null; tableId = null; tableCode = null; orderType = 'Takeaway';
                document.querySelectorAll('.otype').forEach(function (x) { x.classList.toggle('active', x.getAttribute('data-type') === 'Takeaway'); });
                if (btnPickTable) btnPickTable.classList.add('d-none');
                render(); paintTable(); renderInvoices(); toast('info', L.holdOk || '');
            } else toast('error', j.error || '');
        });
    });

    // rails + footer → delegate to the existing buttons / actions
    function relay(fromId, toId) { var a = document.getElementById(fromId), b = document.getElementById(toId); if (a && b) a.addEventListener('click', function () { b.click(); }); }
    relay('footPay', 'btnPay'); relay('railNew', 'btnNew'); relay('railHeld', 'btnHeld'); relay('railTables', 'btnPickTable');
    relay('railCustomers', 'btnCustomer'); relay('railCust2', 'btnCustomer');
    var footCancel = document.getElementById('footCancel');
    if (footCancel) footCancel.addEventListener('click', function () {
        if (!(order && order.id)) { toast('info', tr('لا توجد فاتورة', 'No open invoice')); return; }
        var go = function () { post(U.discard, { orderId: order.id }).then(function () { location.href = '/pos/terminal'; }); };
        if (window.Swal) Swal.fire({ title: tr('إلغاء الفاتورة؟', 'Cancel invoice?'), icon: 'warning', showCancelButton: true, confirmButtonText: tr('نعم', 'Yes'), cancelButtonText: tr('رجوع', 'Back'), confirmButtonColor: '#e5484d', reverseButtons: true }).then(function (r) { if (r.isConfirmed) go(); });
        else if (confirm(tr('إلغاء الفاتورة؟', 'Cancel invoice?'))) go();
    });
    ['footDrawer', 'railNote'].forEach(function (id) { var e = document.getElementById(id); if (e) e.addEventListener('click', function () { toast('info', L.soon || ''); }); });

    // mobile: bottom tab bar switches which column is visible (menu / order / invoices)
    document.querySelectorAll('#posxMtabs .mtab').forEach(function (t) {
        t.addEventListener('click', function () {
            document.querySelectorAll('#posxMtabs .mtab').forEach(function (x) { x.classList.remove('active'); }); t.classList.add('active');
            var cls = t.getAttribute('data-col');
            document.querySelectorAll('.posx-body .posx-col').forEach(function (c) { c.classList.toggle('m-active', c.classList.contains(cls)); });
        });
    });
    // when a menu item is tapped on mobile, jump to the order tab so the cashier sees it added
    document.querySelectorAll('.pos-item').forEach(function (b) {
        b.addEventListener('click', function () {
            if (window.innerWidth <= 820) { var ot = document.querySelector('#posxMtabs .mtab[data-col="posx-order-col"]'); if (ot) ot.click(); }
        });
    });

    // avatar dropdown (custom — reliable positioning next to the avatar, RTL-aware)
    var puEl = document.getElementById('posxUser'), pumEl = document.getElementById('posxUserMenu');
    if (puEl && pumEl) {
        puEl.addEventListener('click', function (e) { if (e.target.closest('.posx-usermenu')) return; pumEl.classList.toggle('open'); });
        document.addEventListener('click', function (e) { if (!puEl.contains(e.target)) pumEl.classList.remove('open'); });
    }

    render(); paintTable(); refreshHeldCount(); renderInvoices();
    setInterval(renderInvoices, 8000);

    // POS-9b: offline restore — if the network is down and a local open order was persisted, load it into the UI
    // (deferred so CBLO [loaded after this script] is ready). Survives page reload while offline.
    setTimeout(function () {
        if (!navigator.onLine && window.CBLO && window.CBLO.ready) {
            window.CBLO.ready.then(function () { var o = window.CBLO.getOrder(); if (o) { order = o; render(); } });
        }
    }, 300);

    // RC-3c: real-time — the terminal reacts to kitchen/branch events instantly (falls back to the 8s poll above if the socket is down).
    if (window.signalR) {
        try {
            var posConn = new signalR.HubConnectionBuilder().withUrl('/hubs/pos').withAutomaticReconnect().build();
            posConn.on('OrderReady', function (p) { toast('success', (L.orderReady || (RTL ? 'الطلب جاهز' : 'Order ready')) + ' #' + (p && p.orderId ? p.orderId : '')); renderInvoices(); });
            posConn.on('LineKdsStatusChanged', function () { renderInvoices(); });
            posConn.on('OrderSentToKitchen', function () { renderInvoices(); });
            posConn.on('OrderPaid', function () { renderInvoices(); refreshHeldCount(); });
            posConn.start().catch(function () { });
        } catch (e) { }
    }
})();
