/* =====================================================================================================================
   Communication on every screen (owner, 2026-11-18; the approved prototype #/comm-float and its top bar):
   - the top bar: the chat icon carries the unread count, a phone icon beside it carries the missed calls not returned;
   - the floating button (unread count) opens a panel: search, the recent conversations, new chat, open all.
   Loaded by _HospitalCalls (every layout renders it, for every employee). Lucide icons, drawn by window.cbCallsKit.ico.
   ===================================================================================================================== */
(function () {
	'use strict';
	if (window.cbCommEverywhere) return; window.cbCommEverywhere = true;
	// INSIDE THE CHAT DRAWER (crossbuy-chat-drawer.js loads /Chat?embed=1 in a frame) nothing of this belongs: the page
	// around the drawer already has the floating button, the counts and the notices. A second button inside the drawer
	// was the owner's "the slide opens the whole system".
	if (window.self !== window.top) return;
	var K = window.cbCallsKit; if (!K) return;
	var t = K.t, esc = K.esc, ico = K.ico, face = K.face, get = K.get;
	var base = (K.U && K.U.comm) || '/Chat';
	var onChat = /^\/chat(\/|$|\?)/i.test(location.pathname) && !/^\/chat\/settings/i.test(location.pathname);

	/* ---------------- the top bar ---------------- */
	function topbar() {
		var a = document.querySelector('#kt_app_header a[href$="/Chat"], #kt_app_header a[href$="/Chat/Index"], .app-navbar a[href$="/Chat"]');
		if (!a || a.getAttribute('data-cm')) return;
		a.setAttribute('data-cm', '1');
		a.className = 'cm-topicon'; a.title = t('cmChats');
		a.innerHTML = ico('message-circle', 20) + '<span class="n num" data-cm-unread></span>';
		var ph = document.createElement('a');
		ph.className = 'cm-topicon ms-1'; ph.href = base + '?tab=calls'; ph.title = t('cmCalls'); ph.setAttribute('data-cm', '1');
		ph.innerHTML = ico('phone', 19) + '<span class="n num" data-cm-missed></span>';
		a.parentNode.insertBefore(ph, a.nextSibling);
	}

	/* ---------------- the floating button and its panel ---------------- */
	var fab, panel, convs = null;
	function build() {
		fab = document.createElement('button'); fab.type = 'button'; fab.className = 'cm-fab border-0'; fab.title = t('cmCommunication'); fab.setAttribute('aria-label', t('cmCommunication'));
		fab.innerHTML = ico('message-circle', 26) + '<span class="n num" data-cm-unread></span>';
		panel = document.createElement('div'); panel.className = 'cm-fpanel d-none'; panel.setAttribute('role', 'dialog');
		panel.innerHTML = '<div class="cm-fhead"><span class="fw-bold fs-5">' + esc(t('cmCommunication')) + '</span><span class="ms-auto d-flex gap-1">' +
				'<a class="cm-ib xs ghost" href="' + esc(base) + '?tab=people" title="' + esc(t('cmNewChat')) + '">' + ico('square-pen', 16) + '</a>' +
				'<a class="cm-ib xs ghost" href="' + esc(base) + '" title="' + esc(t('cmOpenAll')) + '">' + ico('maximize-2', 16) + '</a></span></div>' +
			'<div class="cm-search mx-4 mb-2">' + ico('search', 16) + '<input data-fq placeholder="' + esc(t('cmSearch')) + '" /></div>' +
			'<div class="cm-items" style="max-height:52vh" data-flist><div class="cm-empty">…</div></div>';
		document.body.appendChild(panel); document.body.appendChild(fab);
		document.documentElement.classList.add('cm-has-fab');
		fab.addEventListener('click', function () {
			var open = panel.classList.contains('d-none');
			panel.classList.toggle('d-none', !open);
			fab.innerHTML = open ? ico('x', 26) : ico('message-circle', 26) + '<span class="n num" data-cm-unread></span>';
			if (open) { loadConvs(); panel.querySelector('[data-fq]').focus(); } else paintCounts();
		});
		panel.querySelector('[data-fq]').addEventListener('input', renderList);
		document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && !panel.classList.contains('d-none')) fab.click(); });
	}
	function when(iso) {
		var d = new Date(iso), now = new Date(), y = new Date(now); y.setDate(now.getDate() - 1);
		if (d.toDateString() === now.toDateString()) return String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
		if (d.toDateString() === y.toDateString()) return t('cmYesterday');
		return String(d.getDate()).padStart(2, '0') + '/' + String(d.getMonth() + 1).padStart(2, '0');
	}
	function preview(c) {
		var p = c.preview || '';
		if (/^🎤/.test(p)) return ico('mic', 14) + '<span class="tx">' + esc(t('cmVoiceMsg')) + '</span>';
		if (/^📷/.test(p)) return ico('image', 14) + '<span class="tx">' + esc(t('cmPicture')) + '</span>';
		if (/^📎/.test(p)) return ico('paperclip', 14) + '<span class="tx">' + esc(p.replace(/^📎\s*/, '')) + '</span>';
		return '<span class="tx" dir="auto">' + esc(p) + '</span>';
	}
	// the prototype's list item, the same as the Communication page draws it
	function item(c) {
		var direct = c.kind === 'Direct';
		var who = direct ? face({ name: c.title, photo: c.avatar, initials: initials(c.title) }, 44) : '<div class="cm-grp" style="width:44px;height:44px">' + ico('users-round', 18) + '</div>';
		var tick = c.lastMine ? '<span class="cm-tick' + (c.lastSeen ? ' read' : '') + '">' + ico('check-check', 15) + '</span>' : '';
		return '<a class="cm-item" href="' + esc(base) + '?c=' + c.id + '">' + '<span class="cm-face">' + who + (direct ? '<span class="pres' + (c.online ? ' on' : '') + '"></span>' : '') + '</span>' +
			'<div class="cm-item-body"><div class="cm-line1"><span class="nm">' + esc(c.title) + '</span><span class="when num' + (c.unread ? ' hot' : '') + '">' + esc(c.lastAt ? when(c.lastAt) : '') + '</span></div>' +
			'<div class="cm-line2"><span class="last">' + tick + preview(c) + '</span>' + (c.pinned ? '<span class="cm-mini">' + ico('pin', 13) + '</span>' : '') + (c.muted ? '<span class="cm-mini">' + ico('bell-off', 13) + '</span>' : '') +
			(c.unread ? '<span class="unread num">' + c.unread + '</span>' : '') + '</div></div></a>';
	}
	function initials(n) { var w = String(n || '').trim().split(/\s+/).filter(Boolean); return ((w[0] || '?').charAt(0) + (w.length > 1 ? w[w.length - 1].charAt(0) : '')).toUpperCase(); }
	function loadConvs() { get(base + '/Conversations').then(function (l) { convs = l || []; renderList(); }); }
	function renderList() {
		var box = panel.querySelector('[data-flist]'); if (!convs) return;
		var q = (panel.querySelector('[data-fq]').value || '').trim().toLowerCase();
		var list = convs.filter(function (c) { return !q || (c.title || '').toLowerCase().indexOf(q) !== -1; }).slice(0, q ? 20 : 6);
		box.innerHTML = list.length ? list.map(item).join('') : '<div class="cm-empty">' + esc(q ? t('cmNoResults') : t('cmNoChats')) + '</div>';
	}

	/* ---------------- the counts ---------------- */
	var counts = { unread: 0, missed: 0 };
	function paintCounts() {
		document.querySelectorAll('[data-cm-unread]').forEach(function (n) { n.textContent = counts.unread > 0 ? (counts.unread > 99 ? '99+' : counts.unread) : ''; });
		document.querySelectorAll('[data-cm-missed]').forEach(function (n) { n.textContent = counts.missed > 0 ? counts.missed : ''; });
	}
	function pulse() { get(base + '/Pulse').then(function (r) { if (r && r.ok) { counts.unread = r.unread || 0; counts.missed = r.missed || 0; paintCounts(); paintTitle(); } }); }

	/* ---------------- Communication v2: the tab's title, and a notice on the desktop ---------------- */
	// the browser tab carries the count, like every chat: "(3) Trial balance"
	var baseTitle = document.title.replace(/^\(\d+\+?\)\s*/, '');
	function paintTitle() { var n = counts.unread + counts.missed; document.title = (n > 0 ? '(' + (n > 99 ? '99+' : n) + ') ' : '') + baseTitle; }
	// a desktop notice for a message while this tab is in the background — never in do-not-disturb, never for a muted
	// conversation. (A notice with every tab closed needs a push service outside the company network; the system keeps
	// its traffic inside, so it stays a notice of an open tab.)
	var myDnd = false;
	function refreshDnd() { get(base + '/Statuses').then(function (m) { var s = m && m[String((W.me || {}).id)]; myDnd = !!(s && s.dnd); }); }
	var W = K.W || {};
	function canNotify() { return 'Notification' in window && Notification.permission === 'granted'; }
	function askPermission() { if ('Notification' in window && Notification.permission === 'default') Notification.requestPermission().then(function () { paintAsk(); }); }
	function paintAsk() {
		document.querySelectorAll('[data-cm-ask]').forEach(function (b) { b.classList.toggle('d-none', !('Notification' in window) || Notification.permission !== 'default'); });
	}
	function onConversation(e) {
		pulse();
		if (!e || !document.hidden || !canNotify() || myDnd || e.from === (W.me || {}).id) return;
		get(base + '/Conversations').then(function (list) {
			var c = (list || []).filter(function (x) { return x.id === e.conversationId; })[0];
			if (!c || c.muted) return;
			try {
				var n = new Notification(c.title || t('cmCommunication'), { body: e.preview || '', tag: 'cb-chat-' + e.conversationId, icon: c.avatar || undefined, silent: false });
				n.onclick = function () { window.focus(); location.href = base + '?c=' + e.conversationId; n.close(); };
			} catch (x) { }
		});
	}
	function chatHub() {
		if (!window.signalR) { setTimeout(chatHub, 1500); return; }
		var hub = new signalR.HubConnectionBuilder().withUrl(base.replace(/\/Chat$/, '') + '/hubs/chat').withAutomaticReconnect().build();
		hub.on('conversation', onConversation);
		hub.on('status', function (e) { if (e && e.employeeId === (W.me || {}).id) myDnd = !!(e.status && e.status.dnd); });
		hub.start().catch(function () { });
	}

	function boot() {
		topbar();
		if (!onChat) build();
		pulse();
		setInterval(pulse, 30000);
		document.addEventListener('cbcalls:changed', pulse);
		document.addEventListener('cbcomm:read', pulse);
		refreshDnd(); chatHub();
		// the way to turn the notices on: in the floating panel's head, or on the Communication page's
		var askHtml = '<button type="button" class="cm-ib ghost' + (onChat ? '' : ' xs') + '" data-cm-ask title="' + esc(t('cmNotifyOn')) + '">' + ico('bell-ring', onChat ? 18 : 16) + '</button>';
		var spot = onChat ? document.querySelector('.cm-title .ms-auto') : (panel && panel.querySelector('.cm-fhead .ms-auto'));
		if (spot) { spot.insertAdjacentHTML('afterbegin', askHtml); spot.querySelector('[data-cm-ask]').onclick = askPermission; }
		paintAsk();
	}
	if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot); else boot();
})();
