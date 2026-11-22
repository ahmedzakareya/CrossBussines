/* =====================================================================================================================
   Communication — chat and calls in one page for the whole system (owner, 2026-11-18; the approved prototype
   design-output/comm-prototype). It keeps every behaviour of the old chat page (conversations, search, older messages
   on scroll, replies, @mentions in groups, emoji, reactions, attachments with a preview, edit, delete, members, new chat,
   new group, read ticks, typing, live updates) and adds the calls: voice/video from a conversation, calls written into
   the conversation, the calls tab with the recording player, and the people tab with live presence.
   Settings come from window.CB_COMM (the view). No browser confirm(): a Swal dialog.
   ===================================================================================================================== */
(function () {
	'use strict';
	var CFG = window.CB_COMM || {}, S = CFG.s || CFG.S || {},   // the view's serializer camel-cases S to s
	 meId = CFG.meId, blank = CFG.blank || '';
	function t(k) { return S[k] || k; }
	// Communication v2: comm-plus.js adds threads, polls, forwarding, status… through these hooks (one page, two files)
	var hooks = {};
	function on(name, fn) { (hooks[name] = hooks[name] || []).push(fn); }
	function fire(name) { var a = [].slice.call(arguments, 1); (hooks[name] || []).forEach(function (f) { try { f.apply(null, a); } catch (e) { if (window.console) console.error(e); } }); }
	var sendFilters = [];   // each (body) -> Promise of the body to send, or null when it was handled (a slash command) or refused
	var $ = function (id) { return document.getElementById(id); };
	function esc(s) { var d = document.createElement('div'); d.textContent = s == null ? '' : String(s); return d.innerHTML; }
	var LOCALE = document.documentElement.lang || 'en';
	var isRtl = document.documentElement.dir === 'rtl';

	/* ---------------- icons: Lucide, drawn whenever an <i data-lucide> appears ---------------- */
	function ico(name, s) { return '<i data-lucide="' + name + '" class="cm-i" style="width:' + (s || 18) + 'px;height:' + (s || 18) + 'px" aria-hidden="true"></i>'; }
	// Lucide keeps data-lucide on the <svg> it draws, so only act while an <i> is left, with the observer paused
	// while it draws (otherwise its own changes wake it again, for ever)
	var mo = new MutationObserver(paint);
	function paint() {
		if (!window.lucide || !document.querySelector('.cm-root i[data-lucide], .cm-pop i[data-lucide], .modal i[data-lucide]')) return;
		mo.disconnect();
		try { lucide.createIcons({ attrs: { 'stroke-width': 1.75 }, nameAttr: 'data-lucide' }); } catch (e) { }
		mo.observe(document.body, { childList: true, subtree: true });
	}
	function ib(name, title, cls, attrs) { return '<button type="button" class="cm-ib ' + (cls || '') + '" title="' + esc(title) + '" aria-label="' + esc(title) + '" ' + (attrs || '') + '>' + ico(name, 18) + '</button>'; }

	/* ---------------- people: a face (photo or initials), with presence ---------------- */
	function initials(n) { var w = String(n || '').replace(/^(د\.|Dr\.?)\s*/i, '').trim().split(/\s+/).filter(Boolean); return ((w[0] || '?').charAt(0) + (w.length > 1 ? w[w.length - 1].charAt(0) : '')).toUpperCase(); }
	function hasPhoto(p) { return p && p !== blank && !/blank\.svg/i.test(p); }
	function av(name, photo, size) {
		size = size || 44;
		var st = 'width:' + size + 'px;height:' + size + 'px;font-size:' + Math.round(size * .36) + 'px';
		return hasPhoto(photo) ? '<img class="cm-av" src="' + esc(photo) + '" alt="" style="' + st + '" />' : '<span class="cm-av" style="' + st + '">' + esc(initials(name)) + '</span>';
	}
	var pres = {};   // employee → on | busy | off (the calls hub; the chat's own flag until it answers)
	function presOf(id, fallbackOnline) { return pres[id] || (fallbackOnline ? 'on' : 'off'); }
	function face(name, photo, size, state) { return '<div class="cm-face">' + av(name, photo, size) + (state ? '<span class="pres ' + state + '"></span>' : '') + '</div>'; }
	function grpFace(size) { var s = size || 44; return '<div class="cm-grp" style="width:' + s + 'px;height:' + s + 'px">' + ico('users-round', Math.round(s * .42)) + '</div>'; }
	function presText(state) {
		return state === 'on' ? '<span class="cm-st on">' + t('Available now') + '</span>' : state === 'busy' ? '<span class="cm-st busy">' + t('In a call') + '</span>' :
			state === 'phone' ? '<span class="cm-st phone">' + t('On their phone') + '</span>' : '<span class="cm-st">' + t('Offline') + '</span>';
	}

	/* ---------------- time ---------------- */
	function sameDay(a, b) { return a.toDateString() === b.toDateString(); }
	function hm(d) { try { return d.toLocaleTimeString(LOCALE, { hour: '2-digit', minute: '2-digit' }); } catch (e) { return ''; } }
	// a server time without a zone is UTC (the database stores UtcNow); a browser would read it as local time
	function utc(v) { if (typeof v === 'string' && !/(Z|[+-]\d\d:?\d\d)$/i.test(v)) v += 'Z'; return new Date(v); }
	function when(iso) {
		var d = utc(iso), now = new Date(), y = new Date(now); y.setDate(now.getDate() - 1);
		if (sameDay(d, now)) return hm(d);
		if (sameDay(d, y)) return t('Yesterday');
		// built by hand: the locale's own short date carries a right-to-left mark that reorders inside an isolated number
		return (d.getDate() < 10 ? '0' : '') + d.getDate() + '/' + (d.getMonth() < 9 ? '0' : '') + (d.getMonth() + 1);
	}
	function dayLabel(d) {
		var now = new Date(), y = new Date(now); y.setDate(now.getDate() - 1);
		if (sameDay(d, now)) return t('Today');
		if (sameDay(d, y)) return t('Yesterday');
		try { return d.toLocaleDateString(LOCALE, { weekday: 'long', day: 'numeric', month: 'long' }); } catch (e) { return d.toDateString(); }
	}
	function dur(sec) { sec = sec || 0; var h = Math.floor(sec / 3600), m = Math.floor(sec % 3600 / 60), s = sec % 60; return (h ? h + ':' + (m < 10 ? '0' : '') : (m < 10 ? '0' : '')) + m + ':' + (s < 10 ? '0' : '') + s; }
	function num(s) { return '<span class="cm-num">' + esc(s) + '</span>'; }

	/* ---------------- server ---------------- */
	var tokenEl = document.querySelector('input[name="__RequestVerificationToken"]');
	function token() { return tokenEl ? tokenEl.value : ''; }
	function getJson(url) { return fetch(url, { credentials: 'same-origin' }).then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; }); }
	function post(url, data) {
		var fd; if (data instanceof FormData) fd = data; else { fd = new FormData(); for (var k in data) if (data[k] != null) fd.append(k, data[k]); }
		return fetch(url, { method: 'POST', credentials: 'same-origin', headers: { 'RequestVerificationToken': token() }, body: fd }).then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; });
	}
	function toast(kind, msg) { if (window.toastr) toastr[kind](msg); }
	function ask(title, yes) {
		if (!window.Swal) return Promise.resolve(false);
		return Swal.fire({ text: title, icon: 'warning', showCancelButton: true, buttonsStyling: false, confirmButtonText: yes, cancelButtonText: t('Cancel'),
			customClass: { confirmButton: 'btn btn-danger', cancelButton: 'btn btn-light' } }).then(function (r) { return !!r.isConfirmed; });
	}

	/* ================================================================================================================
	   STATE
	   ================================================================================================================ */
	var tab = CFG.tab || 'chats', chip = 'all';
	var convs = [], dir = [], members = [], callPeople = [];
	var cur = null, curHeader = null, conn = null, replyTo = null, oldestId = null, loading = false, seen = {}, otherReadUpTo = 0, memberReads = {}, pendingFile = null, noOlder = false;
	var calls = [], callsShown = {}, msgCache = {};
	var callPage = 1, callSel = null;

	var shell = $('cmShell'), items = $('cmItems'), chips = $('cmChips'), search = $('cmSearch');
	var conv = $('cmConv'), head = $('cmHead'), thread = $('cmThread'), blankBox = $('cmBlank'), info = $('cmInfo');
	var input = $('cmInput'), sendBtn = $('cmSend'), micBtn = $('cmMic'), typingEl = $('cmTyping'), compose = $('cmComposeWrap');

	/* ================================================================================================================
	   THE LIST: three tabs
	   ================================================================================================================ */
	function setTab(next) {
		tab = next; chip = 'all';
		document.querySelectorAll('#cmTabs [data-tab]').forEach(function (b) { b.classList.toggle('on', b.getAttribute('data-tab') === tab); });
		try { history.replaceState(null, '', location.pathname + '?tab=' + tab + (cur && tab === 'chats' ? '&c=' + cur : '')); } catch (e) { }
		renderChips(); renderList();
		if (tab !== 'calls' && callSel) { callSel = null; if (!cur) showBlank(); }   // a call's detail belongs to the calls tab
		if (tab === 'calls') { callPage = 1; loadCalls(); }
		if (tab === 'people') loadPeople();
		if (tab === 'chats' && !cur) showBlank();
	}
	document.querySelectorAll('#cmTabs [data-tab]').forEach(function (b) { b.addEventListener('click', function () { setTab(b.getAttribute('data-tab')); }); });

	var missedCount = 0;
	function renderChips() {
		// the prototype's chips: chats — all, unread, groups, pinned; calls — all, missed · n, recorded
		var list = tab === 'chats' ? [['all', t('All')], ['unread', t('Unread')], ['groups', t('Groups')], ['pinned', t('Pinned')]]
			: tab === 'calls' ? [['all', t('All')], ['missed', t('Missed') + (missedCount ? ' · ' + missedCount : '')], ['recorded', t('Recorded')]] : [];
		chips.innerHTML = list.map(function (c) { return '<span role="button" tabindex="0" data-chip="' + c[0] + '" class="' + (chip === c[0] ? 'on' : '') + '">' + esc(c[1]) + '</span>'; }).join('');
		chips.classList.toggle('d-none', !list.length);
		chips.querySelectorAll('[data-chip]').forEach(function (b) {
			b.addEventListener('click', function () { chip = b.getAttribute('data-chip'); renderChips(); if (tab === 'calls') { callPage = 1; loadCalls(); } else renderList(); });
		});
	}
	search.addEventListener('input', function () {
		if (tab === 'calls') { clearTimeout(search._t); search._t = setTimeout(function () { callPage = 1; loadCalls(); }, 300); }
		else renderList();
	});
	function q() { return (search.value || '').trim().toLowerCase(); }
	function renderList() { if (tab === 'chats') renderConvs(); else if (tab === 'people') renderPeople(); else renderCalls(); }

	/* ---------------- chats ---------------- */
	var liveConvs = {};   // conversations with a group call going on now (from the calls hub)
	function previewHtml(c) {
		var p = c.preview || '';
		if (/^🎤/.test(p)) return ico('mic', 14) + '<span class="tx">' + esc(t('Voice message')) + '</span>';
		if (/^📷/.test(p)) return ico('image', 14) + '<span class="tx">' + esc(t('Picture')) + '</span>';
		if (/^📎/.test(p)) return ico('paperclip', 14) + '<span class="tx">' + esc(p.replace(/^📎\s*/, '')) + '</span>';
		return '<span class="tx" dir="auto">' + esc(p) + '</span>';
	}
	// the prototype's list item: the face, the name and when; the last line led by its kind (my tick, a voice note, a
	// picture, a file); the pin and mute marks; the unread count; the quick call buttons on hover
	function convItem(c) {
		var direct = c.kind === 'Direct', st = direct ? presOf(c.otherEmployeeId, c.online) : null;
		var tick = c.lastMine ? '<span class="cm-tick' + (c.lastSeen ? ' read' : '') + '">' + ico('check-check', 15) + '</span>' : '';
		return '<div class="cm-item' + (cur === c.id ? ' on' : '') + '" role="button" tabindex="0" data-conv="' + c.id + '">' +
			(direct ? face(c.title, c.avatar, 44, st) : grpFace(44)) +
			'<div class="cm-item-body">' +
				'<div class="cm-line1"><span class="nm">' + esc(c.title) + '</span>' + itemMarks(c) + (liveConvs[c.id] ? '<span class="live">' + ico('video', 11) + esc(t('Live')) + '</span>' : '') +
					'<span class="when num' + (c.unread ? ' hot' : '') + '">' + esc(c.lastAt ? when(c.lastAt) : '') + '</span></div>' +
				'<div class="cm-line2"><span class="last">' + tick + previewHtml(c) + '</span>' +
					(c.pinned ? '<span class="cm-mini">' + ico('pin', 13) + '</span>' : '') + (c.muted ? '<span class="cm-mini">' + ico('bell-off', 13) + '</span>' : '') +
					(c.unread ? '<span class="unread num">' + c.unread + '</span>' : '') + '</div>' +
			'</div>' +
			// a11y: the row is the button; these are mouse shortcuts only (the conversation's header has the same calls for
			// the keyboard and screen readers), so they stay out of the Tab order and the accessibility tree
			(direct ? '<span class="cm-quick" aria-hidden="true">' + qb('phone', t('Voice call'), 'data-call="0" data-emp="' + c.otherEmployeeId + '"') + qb('video', t('Video call'), 'data-call="1" data-emp="' + c.otherEmployeeId + '"') + '</span>' : '') +
			'</div>';
	}
	function qb(name, title, attrs) { return '<span class="cm-ib sm" title="' + esc(title) + '" ' + attrs + '>' + ico(name, 18) + '</span>'; }   // not a control: see above
	var itemDecor = [];
	function itemMarks(c) { return itemDecor.map(function (f) { try { return f(c) || ''; } catch (e) { return ''; } }).join(''); }
	function renderConvs() {
		var s = q();
		var list = convs.filter(function (c) {
			if (s && (c.title || '').toLowerCase().indexOf(s) === -1) return false;
			if (chip === 'unread') return c.unread > 0;
			if (chip === 'groups') return c.kind === 'Group';
			if (chip === 'pinned') return !!c.pinned;
			return true;
		});
		var html = list.map(convItem).join('');
		// people who match the search but have no conversation yet: start one from here
		if (s) {
			var have = {}; convs.forEach(function (c) { if (c.otherEmployeeId) have[c.otherEmployeeId] = 1; });
			var more = dir.filter(function (d) { return !have[d.id] && d.name.toLowerCase().indexOf(s) !== -1; }).slice(0, 8);
			if (more.length) html += '<div class="cm-sec">' + esc(t('People')) + '</div>' + more.map(function (d) {
				return '<div class="cm-item" role="button" tabindex="0" data-start="' + d.id + '">' + face(d.name, d.avatar, 42, presOf(d.id, d.online)) +
					'<div class="cm-item-body"><div class="cm-line1"><span class="nm">' + esc(d.name) + '</span></div><div class="cm-line2"><span class="last"><span class="tx">' + esc(t('New chat')) + '</span></span></div></div></div>';
			}).join('');
		}
		items.innerHTML = html || (s ? '' : '<div class="cm-empty">' + esc(t('No conversations yet')) + '</div>');
		fire('list', items, s, list.length);
	}
	items.addEventListener('click', function (e) {
		var callBtn = e.target.closest('[data-call]');
		if (callBtn) { e.stopPropagation(); startCall(+callBtn.getAttribute('data-emp'), callBtn.getAttribute('data-call') === '1'); return; }
		var chatBtn = e.target.closest('[data-chatwith]');
		if (chatBtn) { e.stopPropagation(); chatWith(+chatBtn.getAttribute('data-chatwith')); return; }
		var it = e.target.closest('.cm-item'); if (!it) return;
		if (it.hasAttribute('data-conv')) { var c = convs.find(function (x) { return x.id === +it.getAttribute('data-conv'); }); if (c) openConversation(c.id, c); }
		else if (it.hasAttribute('data-start')) chatWith(+it.getAttribute('data-start'));
		else if (it.hasAttribute('data-callrow')) selectCall(+it.getAttribute('data-callrow'));
		else if (it.hasAttribute('data-person')) chatWith(+it.getAttribute('data-person'));
	});
	items.addEventListener('keydown', function (e) { if (e.key === 'Enter' && e.target.classList.contains('cm-item')) e.target.click(); });

	function loadConvs(then) {
		return getJson('/Chat/Conversations').then(function (list) {
			convs = list || [];
			var unread = convs.reduce(function (n, c) { return n + (c.unread || 0); }, 0);
			$('cmCountChats').textContent = unread ? unread : '';
			askPresence(convs.filter(function (c) { return c.otherEmployeeId; }).map(function (c) { return c.otherEmployeeId; }));
			if (tab === 'chats') renderConvs();
			if (then) then(convs);
		});
	}
	function loadDir(cb) { return getJson('/Chat/Directory').then(function (list) { dir = list || []; repaintBodies(); if (cb) cb(dir); }); }
	function chatWith(empId) {
		post('/Chat/StartDirect', { otherId: empId }).then(function (res) {
			if (!res) return;
			if (tab !== 'chats') setTab('chats');
			loadConvs(function (l) { var m = l.find(function (x) { return x.id === res.id; }); var d = dir.find(function (x) { return x.id === empId; }); openConversation(res.id, m || { kind: 'Direct', title: d ? d.name : '', avatar: d && d.avatar, otherEmployeeId: empId }); });
		});
	}

	/* ---------------- people ---------------- */
	function loadPeople() {
		getJson('/Chat/CallPeople').then(function (r) { callPeople = r && r.people ? r.people : []; onCall = r && r.onCall ? r.onCall : []; callPeople.forEach(function (p) { pres[p.id] = p.state; }); if (tab === 'people') renderPeople(); });
	}
	function renderPeople() {
		var s = q(), rank = { on: 0, busy: 1, phone: 2, off: 3 };
		var list = callPeople.filter(function (p) { return !s || (p.name || '').toLowerCase().indexOf(s) !== -1 || (p.department || '').toLowerCase().indexOf(s) !== -1 || (p.title || '').toLowerCase().indexOf(s) !== -1; })
			.sort(function (a, b) { return (rank[presOf(a.id)] - rank[presOf(b.id)]) || String(a.name).localeCompare(String(b.name)); });
		var shown = list.slice(0, peopleLimit);
		// the hospital's on-call list first (hospital module, switched on in the settings), then everyone, available first
		var oc = onCall.length ? list.filter(function (p) { return onCall.indexOf(p.id) !== -1; }) : [];
		items.innerHTML = list.length ? (oc.length ? '<div class="cm-sec">' + esc(t('On call now')) + '</div>' + oc.map(personItem).join('') : '') + '<div class="cm-sec">' + esc(t('Available first')) + '</div>' + shown.map(personItem).join('') + (list.length > shown.length ? '<div class="cm-pager"><button type="button" class="cm-pill light" id="cmMorePeople">' + esc(t('Show more')) + ' ' + num('(' + (list.length - shown.length) + ')') + '</button></div>' : '')
			: '<div class="cm-empty">' + esc(t('No results')) + '</div>';
		var more = $('cmMorePeople'); if (more) more.addEventListener('click', function () { peopleLimit += 40; renderPeople(); });
	}
	var onCall = [];
	function personItem(p) {
			var st = presOf(p.id), off = st === 'off' ? 'disabled' : '';
			var sub = [p.title, p.department].filter(Boolean).join(' · ');
			return '<div class="cm-item" data-person="' + p.id + '">' + face(p.name, p.photo, 40, st) +
				'<div class="cm-item-body"><div class="cm-line1"><span class="nm">' + esc(p.name) + '</span></div><div class="cm-line2"><span class="last"><span class="tx">' + esc(sub || presLabel(st)) + '</span></span></div></div>' +
				'<span class="d-flex gap-1">' + ib('message-circle', t('Chat'), 'sm', 'data-chatwith="' + p.id + '"') + ib('phone', t('Voice call'), 'sm', off + ' data-call="0" data-emp="' + p.id + '"') + ib('video', t('Video call'), 'sm fill', off + ' data-call="1" data-emp="' + p.id + '"') + '</span></div>';
	}
	var peopleLimit = 40;
	// "phone": the app is closed but push rings it (owner 2026-11-22) — callable, after the people who are here
	function presLabel(st) { return st === 'on' ? t('Available now') : st === 'busy' ? t('In a call') : st === 'phone' ? t('On their phone') : t('Offline'); }

	/* ---------------- calls ---------------- */
	var callLog = null;
	function loadCalls() {
		var url = '/Chat/CallLog?page=' + callPage + '&filter=' + (chip === 'all' ? '' : chip) + '&q=' + encodeURIComponent(search.value || '');
		getJson(url).then(function (r) {
			callLog = r || { rows: [], total: 0, page: 1, pageSize: 10 };
			if (r && r.stats) setMissed(r.stats.missed);
			(callLog.rows || []).forEach(function (k) { if (k.other) pres[k.other.id] = k.other.state; });
			if (tab === 'calls') { renderCalls(); if (!callSel && callLog.rows && callLog.rows.length && !isPhone()) selectCall(callLog.rows[0].id); else if (callSel) paintCallDetail(); }
		});
	}
	function setMissed(n) { missedCount = n || 0; $('cmCountCalls').textContent = missedCount ? missedCount : ''; if (tab === 'calls') renderChips(); }
	function callTitle(k) {
		var missed = k.status === 'Missed' && !k.outgoing;
		return missed ? t('Missed') : k.status === 'Declined' ? (k.outgoing ? t('Declined') : t('You declined')) : k.status === 'Cancelled' ? (k.outgoing ? t('Cancelled') : t('Missed')) : k.status === 'Missed' ? t('Not answered') : k.outgoing ? t('Outgoing') : t('Incoming');
	}
	function renderCalls() {
		if (!callLog) { items.innerHTML = '<div class="cm-empty">…</div>'; return; }
		if (callLog.installed === false || !callLog.rows || !callLog.rows.length) { items.innerHTML = '<div class="cm-empty">' + esc(t('No calls yet')) + '</div>'; return; }
		var pages = Math.max(1, Math.ceil((callLog.total || 0) / (callLog.pageSize || 10)));
		items.innerHTML = callLog.rows.map(function (k) {
			var o = k.other, missed = (k.status === 'Missed' || k.status === 'Cancelled') && !k.outgoing;
			var dirIco = missed ? 'phone-missed' : k.outgoing ? 'phone-outgoing' : 'phone-incoming';
			var name = k.group ? t('Group call') : o ? o.name : '—';
			return '<div class="cm-item' + (callSel === k.id ? ' on' : '') + '" role="button" tabindex="0" data-callrow="' + k.id + '">' + (k.group || !o ? grpFace(42) : face(o.name, o.photo, 42, presOf(o.id, false))) +
				'<div class="cm-item-body"><div class="cm-line1"><span class="nm' + (missed ? ' cm-red' : '') + '">' + esc(name) + '</span><span class="when num">' + esc(when(k.at)) + '</span></div>' +
				'<div class="cm-line2"><span class="last' + (missed ? ' missed' : '') + '">' + ico(dirIco, 14) + '<span class="tx">' + esc(callTitle(k)) + ' · ' + esc(k.video ? t('Video') : t('Voice')) + (k.seconds ? ' · ' + num(dur(k.seconds)) : '') + '</span></span>' +
				(k.recordingId ? '<span class="cm-rec sm">' + ico('circle-dot', 11) + esc(t('Recorded')) + '</span>' : '') + '</div></div>' +
				(o && !k.group ? '<span class="cm-quick show">' + ib(k.video ? 'video' : 'phone', t('Call back'), 'sm' + (missed ? ' fill' : ''), 'data-call="' + (k.video ? 1 : 0) + '" data-emp="' + o.id + '"') + '</span>' : '') + '</div>';
		}).join('') + (pages > 1 ? '<div class="cm-pager">' + ib(isRtl ? 'chevron-right' : 'chevron-left', t('Previous'), 'xs', callPage <= 1 ? 'disabled id="cmPrev"' : 'id="cmPrev"') +
			'<span>' + esc(t('Page')) + ' ' + num(callPage + ' / ' + pages) + '</span>' + ib(isRtl ? 'chevron-left' : 'chevron-right', t('Next'), 'xs', callPage >= pages ? 'disabled id="cmNext"' : 'id="cmNext"') + '</div>' : '');
		var pv = $('cmPrev'), nx = $('cmNext');
		if (pv) pv.addEventListener('click', function () { callPage--; loadCalls(); });
		if (nx) nx.addEventListener('click', function () { callPage++; loadCalls(); });
	}
	function selectCall(id) {
		callSel = id; cur = null;
		items.querySelectorAll('[data-callrow]').forEach(function (el) { el.classList.toggle('on', +el.getAttribute('data-callrow') === id); });
		paintCallDetail();
		if (isPhone()) setPane('thread');
	}
	function paintCallDetail() {
		var k = callLog && (callLog.rows || []).find(function (x) { return x.id === callSel; });
		if (!k) return;
		shell.classList.add('no-info'); $('cmBanner').classList.add('d-none');
		blankBox.classList.add('d-none'); thread.classList.remove('d-none'); head.classList.remove('d-none'); compose.classList.add('d-none'); typingEl.innerHTML = '';
		var o = k.other, st = o ? presOf(o.id, false) : 'off', off = st === 'off' ? 'disabled' : '';
		head.innerHTML = backBtn() + (o && !k.group ? face(o.name, o.photo, 42, st) : grpFace(42)) +
			'<div style="min-width:0"><div class="nm">' + esc(o && !k.group ? o.name : t('Group call')) + '</div><div class="st">' + (o && !k.group ? presText(st) + (o.title ? '<span class="text-muted"> · ' + esc(o.title) + '</span>' : '') : '') + '</div></div>' +
			(o && !k.group ? '<div class="acts">' + ib('message-circle', t('Open the chat'), '', 'data-chatwith="' + o.id + '"') + ib('phone', t('Voice call'), '', off + ' data-call="0" data-emp="' + o.id + '"') + ib('video', t('Video call'), 'fill', off + ' data-call="1" data-emp="' + o.id + '"') + '</div>' : '');
		var html = callCard(k, o && !k.group ? o.name : '', true);
		if (k.patient) html += '<div class="cm-call"><span class="ic">' + ico('user-round', 16) + '</span><div class="cm-call-tx"><div class="t">' + esc(k.patient.name) + '</div><div class="s">' + esc(t('Patient')) + (k.patient.mrn ? ' · ' + num(k.patient.mrn) : '') + '</div></div></div>';
		if (k.note) html += '<div class="cm-call"><span class="ic">' + ico('notebook-pen', 16) + '</span><div class="cm-call-tx"><div class="t">' + esc(t('Call note')) + '</div><div class="s" style="white-space:pre-wrap">' + esc(k.note) + '</div></div></div>';
		html += k.recordingId ? '<div id="cmPlayer" class="cm-player"><div class="text-muted fs-7">…</div></div>' : '';
		thread.innerHTML = html;
		if (k.recordingId) loadPlayer(k.recordingId);
	}
	/* ---- the call's summary (owner 2026-11-19): a recorded call → its words → a short summary, made in the background
	   on the company's own machine. Started on demand; while it works, asked again every few seconds while on screen ---- */
	var sumTimer = null, sumWorking = {};
	function loadSummary(recId) {
		clearTimeout(sumTimer);
		getJson('/Chat/CallSummary/' + recId).then(function (r) {
			// a failed ask while it is being made (the server restarting, the network) is not an answer: keep showing the
			// progress and ask again, rather than falling back to "not started" and stopping
			if (!r && sumWorking[recId]) {
				var box = document.querySelector('[data-csum="' + recId + '"]');
				if (box) sumTimer = setTimeout(function () { if (document.body.contains(box)) loadSummary(recId); }, 10000);
				return;
			}
			sumWorking[recId] = !!(r && r.summary && r.summary.status === 'working');
			paintSummary(recId, r && r.summary);
		});
	}
	function paintSummary(recId, v) {
		var box = document.querySelector('[data-csum="' + recId + '"]'); if (!box) return;
		var hd = '<div class="hd">' + ico('sparkles', 15) + '<b>' + esc(t('Call summary')) + '</b></div>';
		if (!v || v.status === 'none') {
			box.innerHTML = hd + '<div class="nt">' + esc(t('The recording is turned into text and summarised on the company’s own servers; it takes a few minutes.')) + '</div>' +
				'<button type="button" class="go" data-csum-start="' + recId + '">' + ico('sparkles', 14) + '<span>' + esc(t('Summarise the call')) + '</span></button>';
			return;
		}
		if (v.status === 'working') {
			var pc = Math.round((v.progress || 0) * 100);
			box.innerHTML = hd + '<div class="nt">' + esc(pc < 75 ? t('Turning the call into text…') : t('Writing the summary…')) + ' <span class="cm-num">' + num(pc + '%') + '</span></div>' +
				'<div class="bar"><i style="width:' + Math.max(4, pc) + '%"></i></div><div class="nt sm">' + esc(t('You can leave this page; the summary will be here when it is ready.')) + '</div>';
			sumTimer = setTimeout(function () { if (document.body.contains(box)) loadSummary(recId); }, 6000);
			return;
		}
		if (v.status === 'failed') {
			box.innerHTML = hd + '<div class="nt">' + esc(t('This call could not be summarised.')) + '</div>' +
				'<button type="button" class="go" data-csum-start="' + recId + '">' + ico('rotate-ccw', 14) + '<span>' + esc(t('Try again')) + '</span></button>';
			return;
		}
		var s = v.summary || {}, list = function (title, items, icon) {
			return items && items.length ? '<div class="sec"><div class="st">' + ico(icon, 13) + esc(title) + '</div><ul>' + items.map(function (x) { return '<li dir="auto">' + x + '</li>'; }).join('') + '</ul></div>' : '';
		};
		box.innerHTML = hd +
			(s.overview ? '<div class="ov" dir="auto">' + esc(s.overview) + '</div>' : '<div class="nt">' + esc(s.tooShort || v.transcript ? t('Too little was said in this call to summarise it.') : t('No words were heard in this recording')) + '</div>') +
			list(t('Key points'), (s.points || []).map(esc), 'list') +
			list(t('Decisions'), (s.decisions || []).map(esc), 'circle-check') +
			list(t('Tasks'), (s.actions || []).map(function (a) { return '<b>' + esc(a.owner || '') + '</b> — ' + esc(a.task || ''); }), 'list-todo') +
			(v.transcript ? '<details class="tr"><summary>' + esc(t('The full text of the call')) + '</summary><div dir="auto">' + esc(v.transcript) + '</div></details>' : '') +
			'<div class="nt sm">' + esc(t('Made automatically from the recording — check before relying on it.')) + (v.requestedBy ? ' · ' + esc(t('requested by')) + ' ' + esc(v.requestedBy) : '') + '</div>';
	}
	document.addEventListener('click', function (e) {
		var b = e.target.closest && e.target.closest('[data-csum-start]'); if (!b) return;
		var recId = +b.getAttribute('data-csum-start');
		b.disabled = true; b.innerHTML = '<span class="spinner-border spinner-border-sm"></span><span>' + esc(t('Sending the recording…')) + '</span>';
		post('/Chat/CallSummaryStart/' + recId, {}).then(function (r) {
			if (!r || !r.ok) { toast('error', t('The summary is not available right now — try again later')); }
			paintSummary(recId, r && r.summary && r.summary.status !== 'none' ? r.summary : null);
			if (r && r.summary && r.summary.status === 'working') loadSummary(recId);
		});
	});
	function loadPlayer(recId) {
		getJson('/Chat/CallRecording/' + recId).then(function (r) {
			var box = $('cmPlayer'); if (!box) return;
			if (!r || !r.recording) { box.innerHTML = '<div class="text-muted fs-7">' + esc(t('This recording is not open to you')) + '</div>'; return; }
			var d = r.recording, video = d.kind !== 'audio';
			var src = '/Chat/CallRecordingMedia/' + recId;
			function who(p) { return p ? esc(p.name) : '—'; }
			var facts = [
				[t('Call'), (d.outgoing ? t('Outgoing to') + ' ' + who(d.callee) : t('Incoming from') + ' ' + who(d.caller)) + ' · ' + num(hm(utc(d.startedAt)))],
				[t('Recording'), esc(video ? t('Voice and video') : t('Voice')) + ' · ' + num(dur(d.seconds) + (d.size ? ' · ' + (d.size / 1048576).toFixed(1) + ' MB' : ''))],
				[t('Consent'), t('Asked by') + ' ' + who(d.requestedBy) + (d.agreedBy ? ' · ' + t('agreed by') + ' ' + who(d.agreedBy) : '')],
				[t('File'), esc(t('File Manager')) + ' / ' + num(d.folder)],
			];
			if (d.deleteAfter) facts.push([t('Deleted automatically on'), num(String(d.deleteAfter).substring(0, 10))]);
			var by = d.requestedBy ? '<span class="text-muted">· ' + esc(d.requestedBy.name) + '</span>' : '';
			box.innerHTML = '<div class="hd">' + ico('circle-dot', 16) + '<b>' + esc(t('Call recording')) + '</b>' + by + '</div>' +
				'<div class="vid' + (video ? '' : ' audio') + '">' + (video ? '<video preload="metadata" playsinline></video>' : ico('audio-lines', 40) + '<audio preload="metadata"></audio>') +
					'<div class="ctl"><button type="button" data-pp title="' + esc(t('Play')) + '">' + ico('play', 16) + '</button><span class="prog" data-prog><i style="width:0%"></i></span>' +
					'<span class="num" data-time>00:00 / ' + esc(dur(d.seconds)) + '</span><button type="button" data-vol title="' + esc(t('Sound')) + '">' + ico('volume-2', 16) + '</button>' +
					(video ? '<button type="button" data-full title="' + esc(t('Full screen')) + '">' + ico('maximize', 16) + '</button>' : '') + '</div></div>' +
				'<div class="facts">' + facts.map(function (f) { return '<div><span>' + esc(f[0]) + '</span><b>' + f[1] + '</b></div>'; }).join('') + '</div>' +
				'<div class="cm-csum" data-csum="' + recId + '"><div class="text-muted fs-8">…</div></div>';
			loadSummary(recId);
			var media = box.querySelector('video,audio'), logged = false;
			media.src = src;
			wirePlayer(box, media, d.seconds);
			media.addEventListener('play', function () { if (!logged) { logged = true; fetch(src + '?log=1', { credentials: 'same-origin', headers: { Range: 'bytes=0-0' } }).catch(function () { }); } });
		});
	}
	// the player's own controls (the prototype's bar over the picture): play, the progress, the time, sound, full screen
	function wirePlayer(box, media, seconds) {
		var pp = box.querySelector('[data-pp]'), prog = box.querySelector('[data-prog]'), bar = prog.querySelector('i'), time = box.querySelector('[data-time]');
		function total() { return isFinite(media.duration) && media.duration > 0 ? media.duration : (seconds || 0); }
		function paintTime() { bar.style.width = (total() ? Math.min(100, media.currentTime / total() * 100) : 0) + '%'; time.textContent = dur(Math.round(media.currentTime)) + ' / ' + dur(Math.round(total())); }
		pp.addEventListener('click', function () { if (media.paused) media.play().catch(function () { }); else media.pause(); });
		media.addEventListener('play', function () { pp.innerHTML = ico('pause', 16); });
		media.addEventListener('pause', function () { pp.innerHTML = ico('play', 16); });
		media.addEventListener('timeupdate', paintTime);
		media.addEventListener('loadedmetadata', paintTime);
		prog.addEventListener('click', function (e) {
			var r = prog.getBoundingClientRect(), x = (e.clientX - r.left) / r.width;   // the bar runs left to right (ltr)
			if (total()) media.currentTime = Math.max(0, Math.min(1, x)) * total();
		});
		var vol = box.querySelector('[data-vol]');
		vol.addEventListener('click', function () { media.muted = !media.muted; vol.innerHTML = ico(media.muted ? 'volume-x' : 'volume-2', 16); });
		var full = box.querySelector('[data-full]');
		if (full) full.addEventListener('click', function () { var v = box.querySelector('.vid'); if (v.requestFullscreen) v.requestFullscreen(); });
	}
	function callCard(k, otherName, detail) {
		var missed = (k.status === 'Missed' || k.status === 'Cancelled') && !k.outgoing;
		var icn = missed ? 'phone-missed' : k.video ? 'video' : k.outgoing ? 'phone-outgoing' : 'phone-incoming';
		var title = missed ? (k.video ? t('Missed video call') : t('Missed voice call')) + (otherName ? ' · ' + otherName : '')
			: k.status === 'Declined' ? (k.outgoing ? t('Declined') : t('You declined')) + ' · ' + (k.video ? t('Video') : t('Voice'))
			: k.status === 'Missed' || k.status === 'Cancelled' ? t('Not answered') + ' · ' + (k.video ? t('Video') : t('Voice'))
			: (k.outgoing ? (k.video ? t('Outgoing video call') : t('Outgoing voice call')) : (k.video ? t('Incoming video call') : t('Incoming voice call')));
		var sub = num(hm(utc(k.at))) + (k.seconds ? ' · ' + num(dur(k.seconds)) : '') + (k.reply ? ' · «' + esc(k.reply) + '»' : '');
		var go = '';
		if (k.recordingId) go += '<button type="button" class="cm-rec" data-rec="' + k.id + '">' + ico('circle-dot', 12) + esc(t('Recording')) + ' ' + ico('play', 11) + '</button>';
		if (missed && k.other !== null) go += '<button type="button" class="cm-pill" data-callback="' + (k.video ? 1 : 0) + '"' + (k.other ? ' data-emp="' + k.other.id + '"' : '') + '>' + ico('phone', 14) + esc(t('Call back')) + '</button>';
		if (missed && detail && k.other) go += '<button type="button" class="cm-pill light" data-chatwith="' + k.other.id + '">' + ico('message-circle', 14) + esc(t('Message')) + '</button>';
		return '<div class="cm-call' + (missed ? ' missed' : '') + '" data-at="' + utc(k.at).getTime() + '" data-callid="' + k.id + '"><span class="ic">' + ico(icn, 16) + '</span><div class="cm-call-tx"><div class="t">' + esc(title) + '</div><div class="s">' + sub + '</div></div><div class="go">' + go + '</div></div>';
	}

	/* ================================================================================================================
	   A CONVERSATION
	   ================================================================================================================ */
	function backBtn() { return '<button type="button" class="cm-ib ghost sm d-lg-none" data-back="1" title="' + esc(t('Back')) + '">' + ico(isRtl ? 'arrow-right' : 'arrow-left', 18) + '</button>'; }
	function showBlank() {
		blankBox.classList.remove('d-none'); head.classList.add('d-none'); thread.classList.add('d-none'); compose.classList.add('d-none'); info.innerHTML = ''; typingEl.innerHTML = '';
		shell.classList.add('no-info'); $('cmBanner').classList.add('d-none');
	}
	function paintHead() {
		if (!curHeader) return;
		var direct = curHeader.kind === 'Direct';
		var acts;
		if (direct) {
			var st = presOf(curHeader.other, curHeader.online), off = st === 'off' ? 'disabled' : '';
			var hp = callPeople.find(function (p) { return p.id === curHeader.other; });
			head.innerHTML = backBtn() + face(curHeader.title, curHeader.avatar, 42, st) +
				'<div style="min-width:0"><div class="nm">' + esc(curHeader.title) + '</div><div class="st">' + presText(st) + (hp && hp.department ? '<span class="text-muted"> · ' + esc(hp.department) + '</span>' : '') + '</div></div>';
			acts = ib('phone', t('Voice call'), '', off + ' data-hcall="0"') + ib('video', t('Video call'), 'fill', off + ' data-hcall="1"');
		} else {
			var avail = members.filter(function (m) { return presOf(m.id, m.online) === 'on'; }).length, count = curHeader.memberCount || (members.length + 1);
			head.innerHTML = backBtn() + grpFace(42) +
				'<div style="min-width:0"><div class="nm">' + esc(curHeader.title) + '</div><div class="st">' + (avail ? '<span class="cm-st on">' + num(avail) + ' ' + esc(t('available')) + '</span><span class="text-muted"> · </span>' : '') + '<span class="text-muted">' + num(count) + ' ' + esc(t('members (count)')) + '</span></div></div>';
			acts = ib('phone', t('Group voice call'), '', (members.length ? '' : 'disabled') + ' data-gcall="0"') + ib('video', t('Group video call'), 'fill', (members.length ? '' : 'disabled') + ' data-gcall="1"');
		}
		head.innerHTML += '<div class="acts">' + acts + '<span class="cm-sep"></span>' + ib('search', t('Search the chat'), 'ghost', 'data-find="1"') + ib('panel-right', t('Details'), 'ghost', 'data-info="1"') + '</div>';
		fire('head', head, curHeader);
	}
	head.addEventListener('click', function (e) {
		var b = e.target.closest('button'); if (!b || b.disabled) return;
		if (b.hasAttribute('data-back')) { setPane('list'); return; }
		if (b.hasAttribute('data-hcall')) { startCall(curHeader.other, b.getAttribute('data-hcall') === '1'); return; }
		if (b.hasAttribute('data-gcall')) { startGroup(b.getAttribute('data-gcall') === '1'); return; }
		if (b.hasAttribute('data-call')) { startCall(+b.getAttribute('data-emp'), b.getAttribute('data-call') === '1'); return; }
		if (b.hasAttribute('data-chatwith')) { chatWith(+b.getAttribute('data-chatwith')); return; }
		if (b.hasAttribute('data-find')) { toggleFind(); return; }
		if (b.hasAttribute('data-info')) { if (window.matchMedia('(max-width: 1399.98px)').matches) shell.classList.toggle('info-open'); else shell.classList.toggle('no-info'); return; }
	});

	/* the details pane */
	function paintInfo() {
		if (!curHeader) { info.innerHTML = ''; return; }
		var html;
		if (curHeader.kind === 'Direct') {
			var st = presOf(curHeader.other, curHeader.online), off = st === 'off' ? 'disabled' : '';
			var dp = callPeople.find(function (p) { return p.id === curHeader.other; });
			var sub = dp ? [dp.title, dp.department].filter(Boolean).join(' · ') : '';
			html = '<div class="sect text-center"><div class="d-flex justify-content-center mb-3">' + face(curHeader.title, curHeader.avatar, 76, st) + '</div>' +
				'<div class="fs-5 fw-bold text-gray-900">' + esc(curHeader.title) + '</div>' + (sub ? '<div class="text-muted fs-7">' + esc(sub) + '</div>' : '') +
				'<div class="fs-7 mt-1">' + presText(st) + '</div>' +
				'<div class="cm-actions"><button type="button" data-icall="0" ' + off + '><span>' + ico('phone', 18) + '</span>' + esc(t('Voice')) + '</button><button type="button" data-icall="1" ' + off + '><span>' + ico('video', 18) + '</span>' + esc(t('Video')) + '</button><button type="button" data-find="1"><span>' + ico('search', 18) + '</span>' + esc(t('Search')) + '</button>' + muteBtn() + '</div>' + pinLink() + '</div>';
			var mine = calls.slice(0, 4);
			html += '<div class="sect"><h4>' + ico('phone', 15) + esc(t('Calls between you')) + '</h4>' + (mine.length ? mine.map(function (k) {
				var missed = (k.status === 'Missed' || k.status === 'Cancelled') && !k.outgoing;
				return '<div class="cm-row"><span class="cm-chip' + (missed ? ' red' : '') + '">' + ico(missed ? 'phone-missed' : k.video ? 'video' : k.outgoing ? 'phone-outgoing' : 'phone-incoming', 14) + '</span><div style="min-width:0"><div class="t' + (missed ? ' cm-red' : '') + '">' + esc(callTitle(k)) + ' · ' + esc(k.video ? t('Video') : t('Voice')) + (k.seconds ? ' · ' + num(dur(k.seconds)) : '') + '</div><div class="s">' + num(when(k.at)) + '</div></div>' +
					(k.recordingId ? '<button type="button" class="cm-rec ms-auto" data-rec="' + k.id + '">' + ico('circle-dot', 11) + esc(t('Recorded')) + '</button>' : '') + '</div>';
			}).join('') : '<div class="text-muted fs-7">' + esc(t('No calls yet')) + '</div>') + '</div>';
		} else {
			var owner = curHeader.isOwner, count = curHeader.memberCount || (members.length + 1);
			html = '<div class="sect"><h4>' + ico('users-round', 15) + esc(t('Members')) + ' · ' + num(count) + '</h4>' +
				'<div class="cm-row">' + av(CFG.meName, CFG.meAvatar, 36) + '<div style="min-width:0"><div class="t text-truncate">' + esc(CFG.meName || t('You')) + ' · ' + esc(t('you (me)')) + '</div><div class="s">' + esc(owner ? t('Admin') : t('Member')) + '</div></div></div>' +
				members.map(function (m) {
					var st = presOf(m.id, m.online);
					return '<div class="cm-row">' + face(m.name, m.avatar, 36, st) + '<div style="min-width:0"><div class="t">' + esc(m.name) + '</div><div class="s">' + presText(st) + '</div></div>' +
						'<span class="ms-auto">' + ib('phone', t('Voice call'), 'xs', (st === 'off' ? 'disabled ' : '') + 'data-call="0" data-emp="' + m.id + '"') + '</span></div>';
				}).join('') +
				'<button type="button" class="cm-pill light w-100 mt-3 justify-content-center" data-members="1">' + ico('user-plus', 15) + esc(t('Add a member')) + '</button>' +
				'<div class="d-flex gap-2 mt-3">' + muteBtn(true) + '</div>' + pinLink() + '</div>' +
				'<div class="sect" data-gcalls><h4>' + ico('phone', 15) + esc(t('Group calls')) + '</h4><div class="text-muted fs-7">' + esc(t('No calls yet')) + '</div>' +
				'<div class="fs-8 text-muted mt-2">' + esc(t('Up to 4, up to 12 with the internal media server')) + '</div></div>';
			loadGroupCalls();
		}
		var files = [].slice.call(thread.querySelectorAll('.cm-msg[data-file]')).reverse().slice(0, 5);
		html += '<div class="sect"><h4>' + ico('folder', 15) + esc(t('Files & media')) + '</h4>' + (files.length ? files.map(function (el) {
			var name = el.getAttribute('data-file'), kind = el.getAttribute('data-kind');
			return '<a class="cm-row text-decoration-none" href="' + esc(el.getAttribute('data-href')) + '" target="_blank" rel="noopener"><span class="cm-fic ' + (/\.pdf$/i.test(name) ? 'pdf' : '') + '">' + ico(kind === 'image' ? 'image' : kind === 'audio' ? 'mic' : fileIcon(name), 16) + '</span><div style="min-width:0"><div class="t">' + esc(name) + '</div><div class="s">' + num(el.getAttribute('data-when')) + '</div></div></a>';
		}).join('') : '<div class="text-muted fs-7">' + esc(t('No files yet')) + '</div>') +
			'<a class="cm-link" href="' + esc(CFG.fileManager || '/FileManager') + '">' + esc(t('Every file and recording in the File Manager')) + ' ' + ico(isRtl ? 'arrow-left' : 'arrow-right', 14) + '</a></div>';
		info.innerHTML = html;
		fire('info', info, curHeader);
	}
	function loadGroupCalls() {
		var opened = cur;
		getJson('/Chat/GroupCallsIn?c=' + cur).then(function (l) {
			var box = info.querySelector('[data-gcalls]'); if (!box || cur !== opened || !l || !l.length) return;
			box.innerHTML = '<h4>' + ico('phone', 15) + esc(t('Group calls')) + '</h4>' + l.map(function (k) {
				return '<div class="cm-row"><span class="cm-chip">' + ico(k.video ? 'video' : 'phone', 14) + '</span><div><div class="t">' + num(k.people) + ' ' + esc(t('people (count)')) + (k.seconds ? ' · ' + num(dur(k.seconds)) : '') + '</div><div class="s">' + num(when(k.at)) + '</div></div></div>';
			}).join('') + '<div class="fs-8 text-muted mt-2">' + esc(t('Up to 4, up to 12 with the internal media server')) + '</div>';
		});
	}
	function curConv() { return convs.find(function (c) { return c.id === cur; }) || {}; }
	// mute: the prototype's fourth action (in a group, a light pill under the members)
	function muteBtn(pill) {
		var on = !!curConv().muted;
		if (pill) return '<button type="button" class="cm-pill light flex-grow-1 justify-content-center" data-mute="1">' + ico(on ? 'bell' : 'bell-off', 15) + esc(on ? t('Unmute') : t('Mute')) + '</button>';
		return '<button type="button" data-mute="1"><span>' + ico(on ? 'bell' : 'bell-off', 18) + '</span>' + esc(on ? t('Unmute') : t('Mute')) + '</button>';
	}
	function pinLink() {
		var on = !!curConv().pinned;
		return '<button type="button" class="cm-link border-0 bg-transparent p-0" data-pin="1">' + ico(on ? 'pin-off' : 'pin', 14) + esc(on ? t('Unpin the chat') : t('Pin the chat to the top')) + '</button>';
	}
	function toggle(kind) {
		var c = curConv(); if (!c.id) return;
		var on = kind === 'mute' ? !c.muted : !c.pinned;
		post('/Chat/' + (kind === 'mute' ? 'Mute' : 'Pin'), { c: c.id, on: on }).then(function (r) {
			if (!r || !r.ok) return;
			if (kind === 'mute') c.muted = on; else c.pinned = on;
			paintInfo(); loadConvs();
		});
	}
	info.addEventListener('click', function (e) {
		var b = e.target.closest('button'); if (!b || b.disabled) return;
		if (b.hasAttribute('data-icall')) startCall(curHeader.other, b.getAttribute('data-icall') === '1');
		else if (b.hasAttribute('data-call')) startCall(+b.getAttribute('data-emp'), b.getAttribute('data-call') === '1');
		else if (b.hasAttribute('data-find')) toggleFind();
		else if (b.hasAttribute('data-members')) { renderMembers(); membersModal.show(); }
		else if (b.hasAttribute('data-rec')) openRecording(+b.getAttribute('data-rec'));
		else if (b.hasAttribute('data-mute')) toggle('mute');
		else if (b.hasAttribute('data-pin')) toggle('pin');
	});
	function fileIcon(n) { return /\.(xlsx?|csv|ods)$/i.test(n) ? 'file-spreadsheet' : /\.(pdf|docx?|txt|rtf|odt)$/i.test(n) ? 'file-text' : /\.zip$/i.test(n) ? 'file-archive' : 'file'; }

	function openConversation(id, meta) {
		if (conn && cur) { try { conn.invoke('LeaveConversation', cur); } catch (e) { } }
		callSel = null;
		cur = id; replyTo = null; hideReply(); oldestId = null; noOlder = false; members = []; calls = []; callsShown = {}; closeFind();
		curHeader = { kind: meta.kind, title: meta.title, avatar: meta.avatar, online: !!meta.online, other: meta.otherEmployeeId || meta.other || null };
		if (tab !== 'chats') { tab = 'chats'; document.querySelectorAll('#cmTabs [data-tab]').forEach(function (b) { b.classList.toggle('on', b.getAttribute('data-tab') === 'chats'); }); renderChips(); }
		try { history.replaceState(null, '', location.pathname + '?tab=chats&c=' + id); } catch (e) { }
		shell.classList.remove('no-info');
		blankBox.classList.add('d-none'); head.classList.remove('d-none'); thread.classList.remove('d-none'); compose.classList.remove('d-none'); setComposerEnabled(true);
		input.placeholder = meta.kind === 'Group' ? t('Message — @ to mention') : t('Write a message');
		paintHead(); renderConvs(); paintBanner();
		if (isPhone()) setPane('thread');
		seen = {}; otherReadUpTo = 0; memberReads = {}; thread.innerHTML = ''; typingEl.innerHTML = '';
		thread.setAttribute('aria-busy', 'true');   // a11y: the history is not read aloud as it loads; only what arrives after
		var opened = id;
		var msgsP = getJson('/Chat/Messages?c=' + id).then(function (msgs) {
			if (cur !== opened) return;
			msgs = msgs || [];
			msgs.forEach(function (m) { appendMsg(m, false); });
			if (msgs.length) oldestId = msgs[0].id;
			if (msgs.length < 30) noOlder = true;
			fire('loaded', msgs.map(function (m) { return m.id; }), 'open');
			setTimeout(function () { if (cur === opened) thread.setAttribute('aria-busy', 'false'); }, 300);
		});
		var callsP = curHeader.other ? getJson('/Chat/CallsWith?other=' + curHeader.other).then(function (r) { if (cur === opened) calls = r || []; }) : Promise.resolve();
		Promise.all([msgsP, callsP]).then(function () { if (cur !== opened) return; placeCalls(); regroup(); paintInfo(); scrollBottom(); setTimeout(scrollBottom, 150); fire('ready', id, meta); });
		getJson('/Chat/Header?c=' + id).then(function (h) {
			if (!h || cur !== opened) return;
			otherReadUpTo = h.otherReadMessageId || 0;
			curHeader.memberCount = h.memberCount; curHeader.isOwner = !!h.isOwner;
			members = h.members || [];
			memberReads = {}; (h.memberReads || []).forEach(function (r) { memberReads[r.employeeId] = r.lastReadMessageId; });
			askPresence(members.map(function (m) { return m.id; }));
			repaintBodies(); paintTicks(); paintHead(); paintInfo(); paintBanner();
			if (membersModalOpen()) renderMembers();
		});
		if (conn) { try { conn.invoke('JoinConversation', id); } catch (e) { } }
		post('/Chat/MarkRead', { c: id }).then(function () { loadConvs(); if (window.__cbnbReload) window.__cbnbReload(); document.dispatchEvent(new CustomEvent('cbcomm:read')); });
		fire('open', id, meta);
	}

	/* ---------------- messages ---------------- */
	function isImg(m) { return m.attachmentType === 'image' || /\.(png|jpe?g|gif|bmp|webp|avif|jfif|tiff?)(\?|#|$)/i.test(m.attachmentName || ''); }
	function isAudio(m) { return m.attachmentType === 'audio'; }
	// an attachment served by the chat's own action takes a flag (?…&preview=true); a stored file path is used as it is
	function attUrl(path, flag) { return path.indexOf('?') >= 0 ? path + '&' + flag : path; }
	function fileHtml(m) {
		if (!m.attachmentPath) return '';
		if (isImg(m)) return '<img class="cm-img" src="' + esc(m.attachmentPath) + '" alt="" data-zoom="1" />';
		// a video message: plays in place (owner 2026-11-19)
		if (m.attachmentType === 'video') return '<video class="cm-vid" controls preload="metadata" playsinline src="' + esc(m.attachmentPath) + '#t=0.1"></video>'   // #t: a first frame, not black;
		if (isAudio(m)) {
			var seed = m.id || 1, bars = '';
			for (var i = 0; i < 20; i++) { seed = (seed * 9301 + 49297) % 233280; bars += '<i style="height:' + (5 + Math.round(seed / 233280 * 15)) + 'px"></i>'; }
			return '<div class="cm-voice" data-voice="' + esc(attUrl(m.attachmentPath, 'preview=true')) + '"><button type="button" class="pl" data-vplay title="' + esc(t('Play')) + '">' + ico('play', 14) + '</button>' +
				'<span class="wave">' + bars + '</span><span class="num fs-8" data-vlen>0:00</span></div>';
		}
		var name = m.attachmentName || 'file';
		var ext = (name.split('.').pop() || '').toUpperCase();
		return '<a class="cm-file" href="' + esc(attUrl(m.attachmentPath, 'download=true')) + '" target="_blank" rel="noopener"><span class="fi ' + (/\.pdf$/i.test(name) ? 'pdf' : '') + '">' + ico(fileIcon(name), 20) + '</span>' +
			'<div class="fm"><div class="fn">' + esc(name) + '</div><div class="fs num">' + esc(ext) + '</div></div>' + ico('download', 16) + '</a>';
	}
	function reactsHtml(m) {
		if (!m.reactions || !m.reactions.length) return '';
		return '<div class="cm-reacts">' + m.reactions.map(function (r) { return '<span class="' + (r.mine ? 'mine' : '') + '" data-emoji="' + esc(r.emoji) + '">' + r.emoji + ' <b class="cm-num">' + r.count + '</b></span>'; }).join('') + '</div>';
	}
	function bubbleInner(m) {
		var mine = m.senderId === meId;
		if (m.deleted) return '<div class="tx">' + esc(t('Message deleted')) + '</div>';
		var body = '';
		if (!mine && curHeader && curHeader.kind === 'Group') body += '<div class="who">' + esc(m.senderName) + '</div>';
		if (m.replyToId) body += '<div class="cm-quote" data-goto="' + m.replyToId + '"><b>' + esc(m.replyToSender || t('Message')) + '</b><span>' + esc(m.replyToBody != null ? m.replyToBody : t('Message deleted')) + '</span></div>';
		body += fileHtml(m);
		if (m.body) body += '<div class="tx" dir="auto">' + richText(m.body) + '</div>';
		body += '<div class="cm-meta">' + (m.editedAt ? '<span>' + esc(t('edited')) + ' ·</span>' : '') + num(hm(utc(m.createdAt))) + (mine ? '<span class="cm-tick">' + ico('check-check', 14) + '</span>' : '') + '</div>';
		return body;
	}
	// @mentions stand apart from the text (owner, 2026-11-18): each known person's full name after @ becomes its own
	// chip, isolated so an Arabic name inside an Arabic sentence (or the reverse) cannot run into the words around it.
	// Names carry spaces and brackets, so they are matched against the people we know, longest first.
	function mentionNames() {
		var seen = {}, list = [];
		[].concat(members, dir, [{ name: CFG.meName, altName: CFG.meAlt }]).forEach(function (d) {
			if (!d) return;
			[d.name, d.altName].forEach(function (x) { var n = x && String(x).trim(); if (n && !seen[n]) { seen[n] = 1; list.push(n); } });
		});
		return list.sort(function (a, b) { return b.length - a.length; });
	}
	function reEsc(x) { return x.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); }
	function richText(raw) {
		var names = mentionNames();
		raw = String(raw).replace(/\s*#rec:[A-Za-z]+:\d+\s*$/, '');
		if (!names.length || raw.indexOf('@') === -1) return fmtBlocks(fmtInline(linkify(esc(raw))));
		var re = new RegExp('@(' + names.map(reEsc).join('|') + ')', 'g'), out = '', last = 0, m;
		while ((m = re.exec(raw))) {
			out += fmtInline(linkify(esc(raw.slice(last, m.index))));
			var me = m[1] === CFG.meName || m[1] === CFG.meAlt;
			out += '<bdi class="cm-at' + (me ? ' me' : '') + '">@' + esc(m[1]) + '</bdi>';
			last = m.index + m[0].length;
		}
		return fmtBlocks(out + fmtInline(linkify(esc(raw.slice(last)))));
	}
	// the people arrive after the first messages: paint the mentions again once they are known
	function repaintBodies() {
		thread.querySelectorAll('.cm-msg').forEach(function (el) {
			var m = msgCache[+el.getAttribute('data-id')]; if (!m || m.deleted || !m.body || m.body.indexOf('@') === -1) return;
			var tx = el.querySelector('.cm-bubble > .tx'); if (tx) tx.innerHTML = richText(m.body);
		});
	}
	function fmtInline(h) {
		return h.replace(/`([^`\n]+)`/g, '<code class="cm-code">$1</code>')
			.replace(/(^|[\s(])\*([^*\n]+)\*(?=$|[\s.,!?؟،)])/g, '$1<b>$2</b>')
			.replace(/(^|[\s(])_([^_\n]+)_(?=$|[\s.,!?؟،)])/g, '$1<i>$2</i>')
			.replace(/(^|[\s(])~([^~\n]+)~(?=$|[\s.,!?؟،)])/g, '$1<s>$2</s>');
	}
	function fmtBlocks(h) {
		var out = [], list = false;
		h.replace(/```([\s\S]*?)```/g, function (m, code) { return '\u0000' + code.replace(/\n/g, '\u0001') + '\u0000'; }).split('\n').forEach(function (line) {
			var li = /^\s*[-•]\s+(.*)$/.exec(line), qt = /^&gt;\s?(.*)$/.exec(line);
			if (li) { if (!list) { out.push('<ul class="cm-ul">'); list = true; } out.push('<li>' + li[1] + '</li>'); return; }
			if (list) { out.push('</ul>'); list = false; }
			out.push(qt ? '<span class="cm-bq">' + qt[1] + '</span>' : line);
		});
		if (list) out.push('</ul>');
		return out.join('\n').replace(/\n?(<\/?ul[^>]*>)\n?/g, '$1').replace(/\n?(<li>)/g, '$1').replace(/\u0000([\s\S]*?)\u0000/g, function (m, code) { return '<pre class="cm-pre">' + code.replace(/^\u0001+|\u0001+$/g, '').replace(/\u0001/g, '\n') + '</pre>'; });
	}
	function linkify(h) { return h.replace(/(https?:\/\/[^\s<]+)/g, '<a href="$1" target="_blank" rel="noopener noreferrer" style="color:inherit;text-decoration:underline">$1</a>'); }
	function msgEl(m) {
		var mine = m.senderId === meId, el = document.createElement('div');
		el.className = 'cm-msg' + (mine ? ' mine' : '') + (m.deleted ? ' deleted' : '');
		el.setAttribute('data-id', m.id); el.setAttribute('data-sender', m.senderId); el.setAttribute('data-at', utc(m.createdAt).getTime());
		el.setAttribute('data-body', m.body || ''); el.setAttribute('data-text', m.body || m.attachmentName || '');
		el.setAttribute('data-name', mine ? t('You') : (m.senderName || ''));
		// a11y: each message is one stop for the arrow keys (the list itself is the Tab stop), named by who and when
		el.tabIndex = -1; el.setAttribute('role', 'article'); el.setAttribute('aria-label', (mine ? t('You') : (m.senderName || '')) + ' · ' + when(m.createdAt));
		if (m.attachmentPath && !m.deleted) { el.setAttribute('data-file', m.attachmentName || (isImg(m) ? t('Picture') : 'file')); el.setAttribute('data-kind', isImg(m) ? 'image' : isAudio(m) ? 'audio' : 'file'); el.setAttribute('data-href', m.attachmentPath); el.setAttribute('data-when', when(m.createdAt)); }
		var faceHtml = mine ? '' : '<span class="cm-msg-face">' + av(m.senderName, m.senderAvatar, 30) + '</span>';
		var bar = m.deleted ? '' : '<div class="cm-hover">' + ib('smile-plus', t('React'), 'xs', 'data-act="react"') + ib('reply', t('Reply'), 'xs', 'data-act="reply"') + ib('ellipsis', t('More'), 'xs', 'data-act="more"') + '</div>';
		el.innerHTML = faceHtml + '<div class="cm-bwrap"><div class="cm-bubble">' + bubbleInner(m) + '</div>' + bar + reactsHtml(m) + '</div>';
		msgCache[m.id] = m;
		fire('message', el, m);
		return el;
	}
	function appendMsg(m, prepend) {
		if (m.id) { if (seen[m.id]) return null; seen[m.id] = 1; }
		var el = msgEl(m);
		if (prepend) thread.insertBefore(el, thread.firstChild); else thread.appendChild(el);
		if (!prepend) { var im = el.querySelector('img.cm-img'); if (im) im.addEventListener('load', function () { if (nearBottom()) scrollBottom(); }); }
		var vb = el.querySelector('.cm-voice');
		if (vb) { var pa = new Audio(); pa.preload = 'metadata'; pa.src = vb.getAttribute('data-voice'); pa.addEventListener('loadedmetadata', function () { if (isFinite(pa.duration)) vb.querySelector('[data-vlen]').textContent = dur(Math.round(pa.duration)); }); }
		return el;
	}
	// a call goes where its time falls among the messages loaded so far; older ones wait for older messages
	function placeCalls() {
		var floor = noOlder ? 0 : (function () { var f = thread.querySelector('.cm-msg'); return f ? +f.getAttribute('data-at') : Infinity; })();
		calls.forEach(function (k) {
			var at = utc(k.at).getTime();
			if (callsShown[k.id] || at < floor) return;
			callsShown[k.id] = 1;
			var w = document.createElement('div'); w.innerHTML = callCard(k, curHeader ? curHeader.title : ''); var el = w.firstChild;
			var after = [].slice.call(thread.querySelectorAll('.cm-msg, .cm-call')).find(function (x) { return +x.getAttribute('data-at') > at; });
			if (after) thread.insertBefore(el, after); else thread.appendChild(el);
		});
	}
	// runs: the face on the last message of a run, the name on the first, rounded corners between; a day line on a new day
	function regroup() {
		thread.querySelectorAll('.cm-day').forEach(function (d) { d.remove(); });
		var rows = [].slice.call(thread.querySelectorAll('.cm-msg, .cm-call')), lastDay = null;
		rows.forEach(function (el, i) {
			var at = new Date(+el.getAttribute('data-at'));
			if (!lastDay || !sameDay(lastDay, at)) { var d = document.createElement('div'); d.className = 'cm-day'; d.innerHTML = '<span>' + esc(dayLabel(at)) + '</span>'; thread.insertBefore(d, el); }
			lastDay = at;
			if (!el.classList.contains('cm-msg')) return;
			var prev = rows[i - 1], next = rows[i + 1];
			function same(o) { return o && o.classList.contains('cm-msg') && o.getAttribute('data-sender') === el.getAttribute('data-sender') && Math.abs(+o.getAttribute('data-at') - +el.getAttribute('data-at')) < 5 * 60000 && sameDay(new Date(+o.getAttribute('data-at')), at); }
			var cont = same(prev), more = same(next);
			el.classList.toggle('cont', cont); el.classList.toggle('more', more);
			var f = el.querySelector('.cm-msg-face > *'); if (f) f.style.visibility = more ? 'hidden' : '';
			var w = el.querySelector('.who'); if (w) w.style.display = cont ? 'none' : '';
		});
		paintTicks();
		fire('regroup');
	}
	function paintTicks() {
		if (!curHeader) return;
		var others = (curHeader.memberCount || 2) - 1;
		thread.querySelectorAll('.cm-msg.mine').forEach(function (el) {
			var id = +el.getAttribute('data-id'), read;
			if (curHeader.kind === 'Direct') read = otherReadUpTo >= id;
			else { var n = 0; for (var k in memberReads) if (memberReads[k] >= id) n++; read = others > 0 && n >= others; }
			var tk = el.querySelector('.cm-tick'); if (tk) { tk.classList.toggle('read', !!read); tk.title = read ? (curHeader.kind === 'Direct' ? t('Seen') : t('Read by all')) : t('Sent'); }
		});
	}
	function nearBottom() { return thread.scrollHeight - thread.scrollTop - thread.clientHeight < 120; }
	function scrollBottom() { thread.scrollTop = thread.scrollHeight; }

	// older messages when the top is reached
	thread.addEventListener('scroll', function () {
		if (!cur || thread.scrollTop > 40 || !oldestId || loading || noOlder) return;
		loading = true; var keep = thread.scrollHeight, opened = cur; thread.setAttribute('aria-busy', 'true');
		getJson('/Chat/Messages?c=' + cur + '&before=' + oldestId).then(function (msgs) {
			loading = false; if (cur !== opened) return;
			msgs = msgs || [];
			if (!msgs.length) noOlder = true;
			msgs.slice().reverse().forEach(function (m) { appendMsg(m, true); });
			if (msgs.length) oldestId = msgs[0].id;
			fire('loaded', msgs.map(function (m) { return m.id; }), 'older');
			placeCalls(); regroup(); thread.scrollTop = thread.scrollHeight - keep;
			thread.setAttribute('aria-busy', 'false');
		});
	});

	// the hover bar, reactions, quotes, call cards in the thread
	thread.addEventListener('click', function (e) {
		var el = e.target.closest('.cm-msg'), b = e.target.closest('button,[data-emoji],.cm-quote,[data-zoom]');
		if (b && b.hasAttribute('data-rec')) { openRecording(+b.getAttribute('data-rec')); return; }
		if (b && b.hasAttribute('data-vplay')) { playVoice(b.closest('.cm-voice')); return; }
		if (b && b.hasAttribute('data-callback')) { startCall(+(b.getAttribute('data-emp') || 0) || (curHeader && curHeader.other), b.getAttribute('data-callback') === '1'); return; }
		if (b && b.hasAttribute('data-chatwith')) { chatWith(+b.getAttribute('data-chatwith')); return; }
		if (!el) return;
		var id = +el.getAttribute('data-id');
		if (b && b.hasAttribute('data-zoom')) { openImage(b.getAttribute('src')); return; }
		if (b && b.classList.contains('cm-quote')) { gotoMsg(b.getAttribute('data-goto')); return; }
		if (b && b.hasAttribute('data-emoji')) { react(id, b.getAttribute('data-emoji')); return; }
		if (b && b.getAttribute('data-act') === 'react') { e.stopPropagation(); openEmoji(b, function (em) { react(id, em); }); return; }
		if (b && b.getAttribute('data-act') === 'reply') { setReply(id, el.getAttribute('data-name'), el.getAttribute('data-text')); return; }
		if (b && b.getAttribute('data-act') === 'more') { e.stopPropagation(); openMore(b, el); return; }
		// a phone has no hover: a tap shows the bar
		if (isPhone() && !b) { thread.querySelectorAll('.cm-msg.tools-open').forEach(function (x) { if (x !== el) x.classList.remove('tools-open'); }); el.classList.toggle('tools-open'); }
	});
	/* ---------------- the keyboard in the message list (a11y): Tab lands on the newest message; ↑/↓ move between messages,
	   Home/End jump, Enter reaches the message's own buttons (react, reply, more), Esc goes back to writing ---------------- */
	function msgs() { return Array.prototype.slice.call(thread.querySelectorAll('.cm-msg[data-id]')); }
	thread.addEventListener('focus', function (e) { if (e.target !== thread) return; var l = msgs(); if (l.length) l[l.length - 1].focus(); });
	thread.addEventListener('keydown', function (e) {
		var el = e.target.closest && e.target.closest('.cm-msg[data-id]'); if (!el) return;
		var onMsg = e.target === el, l = msgs(), i = l.indexOf(el), to = null;
		if (e.key === 'Escape') { e.preventDefault(); if (onMsg) input.focus(); else el.focus(); return; }
		if (!onMsg) return;
		if (e.key === 'ArrowUp') to = l[Math.max(0, i - 1)];
		else if (e.key === 'ArrowDown') to = l[Math.min(l.length - 1, i + 1)];
		else if (e.key === 'Home') to = l[0];
		else if (e.key === 'End') to = l[l.length - 1];
		else if (e.key === 'Enter') { var b = el.querySelector('.cm-hover .cm-ib'); if (b) { e.preventDefault(); el.classList.add('tools-open'); b.focus(); } return; }
		if (to) { e.preventDefault(); to.focus(); to.scrollIntoView({ block: 'nearest' }); }
	});
	thread.addEventListener('focusout', function (e) { var el = e.target.closest && e.target.closest('.cm-msg'); if (el && !el.contains(e.relatedTarget)) el.classList.remove('tools-open'); });
	var myTaps = {};   // the reaction broadcast does not say who: my own tap flips my mark when it comes back
	function react(id, emoji) { myTaps[id] = emoji; post('/Chat/React', { id: id, emoji: emoji }); }
	var voicePlaying = null;
	function playVoice(box) {
		if (voicePlaying && voicePlaying.box !== box) { voicePlaying.audio.pause(); }
		if (voicePlaying && voicePlaying.box === box) { if (box._a.paused) box._a.play().catch(function () { }); else box._a.pause(); return; }
		var a = box._a || (box._a = new Audio(box.getAttribute('data-voice')));
		var bars = box.querySelectorAll('.wave i'), btn = box.querySelector('[data-vplay]'), len = box.querySelector('[data-vlen]');
		if (!box._wired) {
			box._wired = true;
			a.addEventListener('loadedmetadata', function () { if (isFinite(a.duration)) len.textContent = dur(Math.round(a.duration)); });
			a.addEventListener('timeupdate', function () {
				var f = a.duration && isFinite(a.duration) ? a.currentTime / a.duration : 0, n = Math.round(f * bars.length);
				for (var i = 0; i < bars.length; i++) bars[i].classList.toggle('done', i < n);
				len.textContent = dur(Math.round(a.currentTime));
			});
			a.addEventListener('play', function () { btn.innerHTML = ico('pause', 14); });
			a.addEventListener('pause', function () { btn.innerHTML = ico('play', 14); });
			a.addEventListener('ended', function () { for (var i = 0; i < bars.length; i++) bars[i].classList.remove('done'); if (isFinite(a.duration)) len.textContent = dur(Math.round(a.duration)); voicePlaying = null; });
		}
		voicePlaying = { box: box, audio: a };
		a.play().catch(function () { });
	}
	function gotoMsg(id) {
		var tgt = thread.querySelector('.cm-msg[data-id="' + id + '"]'); if (!tgt) return;
		tgt.scrollIntoView({ behavior: 'smooth', block: 'center' });
		var bb = tgt.querySelector('.cm-bubble'); bb.classList.add('cm-flash'); setTimeout(function () { bb.classList.remove('cm-flash'); }, 1200);
	}
	function openImage(src) {
		$('cmImgView').innerHTML = '<img src="' + esc(src) + '" alt="" style="max-width:100%;max-height:78vh;border-radius:12px;display:block;margin:0 auto" />';
		imgViewModal.show();
	}

	/* ---------------- pop-ups: emoji and the "more" menu ---------------- */
	var EMO = ['👍', '❤️', '😂', '🙏', '👏', '🎉', '✅', '🔥', '😀', '😃', '😄', '😁', '😆', '🤣', '🥹', '😊', '😍', '🥰', '😘', '😎', '🤩', '🥳', '🫠', '🥲', '🤔', '🫡', '🤯', '🫣', '🤗', '🫶', '🙂', '😉', '👎', '🙌', '🤝', '💪', '👌', '🫰', '🩷', '✨', '🚀', '🌟', '💯', '❌', '⚡', '🎯', '🧠', '👀', '☕'];
	var pop = document.createElement('div'); pop.className = 'cm-pop d-none'; document.body.appendChild(pop);
	var popFor = null;
	function placePop(anchor) {
		pop.classList.remove('d-none');
		var r = anchor.getBoundingClientRect(), w = pop.offsetWidth, h = pop.offsetHeight;
		var left = Math.min(Math.max(8, isRtl ? r.right - w : r.left), window.innerWidth - w - 8);
		var top = r.top - h - 8; if (top < 8) top = r.bottom + 8;
		pop.style.position = 'fixed'; pop.style.left = left + 'px'; pop.style.top = top + 'px';
	}
	function openEmoji(anchor, pick) {
		popFor = pick;
		pop.innerHTML = '<div class="cm-emoji-grid">' + EMO.map(function (e) { return '<button type="button" data-em="' + e + '" title="' + e + '">' + e + '</button>'; }).join('') + '</div>';
		placePop(anchor);
	}
	function openMore(anchor, el) {
		var mine = el.classList.contains('mine'), id = +el.getAttribute('data-id');
		var rows = [['copy', t('Copy'), 'copy']];
		if (mine && el.getAttribute('data-body')) rows.push(['pencil', t('Edit'), 'edit']);
		if (mine) rows.push(['trash-2', t('Delete'), 'delete']);
		var extra = {}; fire('more', rows, el, id, extra);
		rows.sort(function (a, b) { return (a[2] === 'delete') - (b[2] === 'delete'); });
		pop.innerHTML = rows.map(function (r) { return '<button type="button" class="cm-item w-100 border-0 bg-transparent' + (r[2] === 'delete' ? ' cm-red' : '') + '" data-more="' + r[2] + '" style="padding:8px 12px">' + ico(r[0], 16) + '<span class="fs-7 fw-semibold">' + esc(r[1]) + '</span></button>'; }).join('');
		popFor = function (what) {
			if (what === 'copy') { try { navigator.clipboard.writeText(el.getAttribute('data-text') || ''); toast('success', t('Copied')); } catch (x) { } }
			if (what === 'edit') openEdit(id, el.getAttribute('data-body'));
			if (what === 'delete') ask(t('Delete message?'), t('Yes, delete')).then(function (ok) { if (ok) post('/Chat/Delete', { id: id }); });
			rows.forEach(function (r) { if (r[2] === what && typeof r[3] === 'function') r[3](id, el); });
		};
		placePop(anchor);
	}
	pop.addEventListener('click', function (e) {
		var b = e.target.closest('button'); if (!b || !popFor) return;
		var fn = popFor; closePop();
		fn(b.getAttribute('data-em') || b.getAttribute('data-more'));
	});
	function closePop() { pop.classList.add('d-none'); popFor = null; }
	document.addEventListener('click', function (e) { if (!pop.classList.contains('d-none') && !pop.contains(e.target)) closePop(); });
	thread.addEventListener('scroll', function () { if (!pop.classList.contains('d-none')) closePop(); });

	/* ---------------- search inside the conversation ---------------- */
	var findBox = $('cmFind'), findInput = $('cmFindInput');
	function toggleFind() { if (findBox.classList.contains('d-none')) { findBox.classList.remove('d-none'); findInput.focus(); } else closeFind(); }
	function closeFind() { findBox.classList.add('d-none'); findInput.value = ''; applyFind(); }
	function applyFind() {
		var s = findInput.value.trim().toLowerCase(), hits = 0;
		thread.querySelectorAll('.cm-msg').forEach(function (el) { var on = !s || (el.getAttribute('data-text') || '').toLowerCase().indexOf(s) !== -1; el.style.display = on ? '' : 'none'; if (s && on) hits++; });
		thread.querySelectorAll('.cm-call, .cm-day').forEach(function (el) { el.style.display = s ? 'none' : ''; });
		$('cmFindCount').innerHTML = s ? num(hits) : '';
	}
	findInput.addEventListener('input', applyFind);
	findInput.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeFind(); });
	$('cmFindClose').addEventListener('click', closeFind);

	/* ================================================================================================================
	   THE COMPOSER
	   ================================================================================================================ */
	function setReply(id, who, text) {
		replyTo = id;
		$('cmReplyWho').textContent = t('Replying to') + ' ' + (who || '');
		$('cmReplyText').textContent = text || '';
		$('cmReplyBar').classList.remove('d-none'); input.focus();
	}
	function hideReply() { replyTo = null; $('cmReplyBar').classList.add('d-none'); }
	$('cmReplyCancel').addEventListener('click', hideReply);

	function autoGrow() { input.style.height = 'auto'; input.style.height = Math.min(input.scrollHeight, 130) + 'px'; syncSend(); }
	function syncSend() { if (micBtn) micBtn.disabled = !canRecord(); }
	function canRecord() { return !!(navigator.mediaDevices && window.MediaRecorder); }

	// @mentions — groups only, members only
	var mentionBox = $('cmMention'), mentionStart = -1;
	function mentionOpen() { return !mentionBox.classList.contains('d-none'); }
	function hideMention() { mentionBox.classList.add('d-none'); mentionStart = -1; }
	function showMention() {
		var val = input.value, pos = input.selectionStart == null ? val.length : input.selectionStart;
		var m = val.substring(0, pos).match(/@(\S{0,20})$/);
		var src = curHeader && curHeader.kind === 'Group' ? members : [];
		if (!m || !src.length) { hideMention(); return; }
		mentionStart = pos - m[0].length;
		var s = m[1].toLowerCase(), list = src.filter(function (d) { return d.name.toLowerCase().indexOf(s) !== -1; }).slice(0, 8);
		if (!list.length) { hideMention(); return; }
		mentionBox.innerHTML = list.map(function (d, i) { return '<div class="cm-item' + (i === 0 ? ' active' : '') + '" data-name="' + esc(d.name) + '">' + av(d.name, d.avatar, 28) + '<span class="fs-7 fw-semibold">' + esc(d.name) + '</span></div>'; }).join('');
		mentionBox.querySelectorAll('.cm-item').forEach(function (el) { el.addEventListener('mousedown', function (ev) { ev.preventDefault(); pickMention(el.getAttribute('data-name')); }); });
		mentionBox.classList.remove('d-none');
	}
	function pickMention(name) {
		var val = input.value, pos = input.selectionStart == null ? val.length : input.selectionStart;
		input.value = val.substring(0, mentionStart) + '@' + name + ' ' + val.substring(pos);
		var np = mentionStart + name.length + 2; try { input.setSelectionRange(np, np); } catch (e) { }
		hideMention(); input.focus();
	}
	function detectMentions(body) {
		var src = members.length ? members : dir, ids = [];
		src.forEach(function (d) { if (body.indexOf('@' + d.name) !== -1) ids.push(d.id); });
		return ids.join(',');
	}
	input.addEventListener('input', function () { showMention(); autoGrow(); });
	input.addEventListener('blur', function () { setTimeout(hideMention, 200); });
	var typingSent = 0;
	input.addEventListener('keydown', function (e) {
		if (mentionOpen()) {
			var list = [].slice.call(mentionBox.querySelectorAll('.cm-item'));
			if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); var a = mentionBox.querySelector('.active') || list[0]; if (a) pickMention(a.getAttribute('data-name')); return; }
			if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
				e.preventDefault(); var i = list.findIndex(function (x) { return x.classList.contains('active'); }); if (i < 0) i = 0;
				list[i].classList.remove('active'); i = e.key === 'ArrowDown' ? (i + 1) % list.length : (i - 1 + list.length) % list.length; list[i].classList.add('active'); return;
			}
			if (e.key === 'Escape') { hideMention(); return; }
		}
		if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); doSend(); return; }
		if (conn && cur && Date.now() - typingSent > 1500) { typingSent = Date.now(); try { conn.invoke('Typing', cur); } catch (x) { } }
	});
	$('cmEmojiBtn').addEventListener('click', function (e) {
		e.stopPropagation();
		openEmoji(this, function (em) {
			var s = input.selectionStart == null ? input.value.length : input.selectionStart, en = input.selectionEnd == null ? s : input.selectionEnd;
			input.value = input.value.slice(0, s) + em + input.value.slice(en);
			var np = s + em.length; input.focus(); try { input.setSelectionRange(np, np); } catch (x) { }
			autoGrow();
		});
	});

	function sendCore(body, file) {
		if (!cur) return;
		body = (body || '').trim();
		if (!body && !file) return;
		if (file && file.size > 50 * 1024 * 1024) { toast('error', t('File too large')); return; }
		var fd = new FormData(); fd.append('c', cur); if (body) fd.append('body', body);
		if (replyTo) fd.append('replyToId', replyTo);
		var mids = body ? detectMentions(body) : ''; if (mids) fd.append('mentionedIds', mids);
		if (file) fd.append('file', file, file.name);
		hideReply();
		sendBtn.disabled = true;
		fetch('/Chat/Send', { method: 'POST', credentials: 'same-origin', headers: { 'RequestVerificationToken': token() }, body: fd })
			.then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
			.then(function (dto) { if (!dto) throw new Error('empty'); appendMsg(dto, false); regroup(); scrollBottom(); paintInfo(); loadConvs(); })
			.catch(function (err) {
				// the text comes back into the box, ready to send again (a failed message is never lost)
				if (body && !file && !input.value.trim()) { input.value = body; autoGrow(); }
				toast('error', t('Send failed') + ' — ' + t('the message is back in the box, send it again') + (err && err.message ? ' (' + err.message + ')' : ''));
			})
			.then(function () { sendBtn.disabled = false; });
	}
	function doSend() {
		var b = input.value.trim(); if (!b) return;
		var chain = Promise.resolve(b);
		sendFilters.forEach(function (f) { chain = chain.then(function (x) { return x == null ? null : f(x); }); });
		chain.then(function (x) { if (x == null) return; input.value = ''; autoGrow(); sendCore(x, null); });
	}
	sendBtn.addEventListener('click', doSend);

	// a file or a picture opens a preview (with an optional caption) before it is sent
	function pickFile(accept) { var f = $('cmFile'); f.accept = accept || ''; f.value = ''; f.click(); }
	$('cmAttachBtn').addEventListener('click', function (e) { if (hooks.plus && hooks.plus.length) { e.stopPropagation(); fire('plus', this); } else pickFile(''); });
	$('cmImageBtn').addEventListener('click', function () { pickFile('image/*'); });
	$('cmPaperBtn').addEventListener('click', function () { pickFile('.pdf,.txt,.csv,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.zip,.rtf,.odt,.ods'); });
	$('cmFile').addEventListener('change', function () {
		var f = this.files && this.files[0]; if (!f) return;
		pendingFile = f;
		var wrap = $('cmPreviewWrap');
		wrap.innerHTML = /^image\//.test(f.type) ? '<img src="' + URL.createObjectURL(f) + '" style="max-width:100%;max-height:52vh;border-radius:12px;display:block;margin:0 auto" />'
			: '<div class="d-flex flex-column align-items-center py-6"><span class="cm-fic" style="width:64px;height:64px;border-radius:18px">' + ico(fileIcon(f.name), 30) + '</span><div class="fw-semibold mt-3">' + esc(f.name) + '</div><div class="text-muted fs-8">' + num((f.size / 1024).toFixed(0) + ' KB') + '</div></div>';
		$('cmCaption').value = input.value.trim();
		previewModal.show();
	});
	$('cmPreviewSend').addEventListener('click', function () {
		var cap = $('cmCaption').value.trim(), f = pendingFile;
		pendingFile = null; input.value = ''; autoGrow(); previewModal.hide();
		sendCore(cap, f);
	});
	$('cmPreviewModal').addEventListener('hidden.bs.modal', function () { pendingFile = null; $('cmPreviewWrap').innerHTML = ''; $('cmCaption').value = ''; });

	// a voice message: press the microphone, speak, press again to STOP — then listen to it, and send it or delete it
	// (owner 2026-11-19: a voice message is previewed before it goes)
	var recording = null, recChunks = [], recStart = 0, recTimer = null;
	if (micBtn) micBtn.addEventListener('click', function () {
		if (recording) { stopVoice(true); return; }
		if (voiceDraft) return;   // one at a time: the one waiting for review is sent or deleted first
		navigator.mediaDevices.getUserMedia({ audio: true }).then(function (stream) {
			var type = ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus', 'audio/mp4'].find(function (x) { return MediaRecorder.isTypeSupported && MediaRecorder.isTypeSupported(x); }) || '';
			recording = new MediaRecorder(stream, type ? { mimeType: type } : undefined); recChunks = [];
			recording.ondataavailable = function (ev) { if (ev.data && ev.data.size) recChunks.push(ev.data); };
			recording.start(); recStart = Date.now();
			$('cmVoiceBar').classList.remove('d-none'); compose.classList.add('recording');
			micBtn.classList.add('recording'); micBtn.innerHTML = ico('square', 16); micBtn.title = t('Stop and listen');
			recTimer = setInterval(function () { $('cmVoiceTime').textContent = dur(Math.round((Date.now() - recStart) / 1000)); }, 500);
		}).catch(function () { toast('error', t('Microphone unavailable')); });
	});
	$('cmVoiceCancel').addEventListener('click', function () { stopVoice(false); });
	function stopVoice(send) {
		var r = recording; if (!r) return;
		recording = null; clearInterval(recTimer);
		$('cmVoiceBar').classList.add('d-none'); $('cmVoiceTime').textContent = '0:00'; compose.classList.remove('recording');
		micBtn.classList.remove('recording'); micBtn.innerHTML = ico('mic', 18); micBtn.title = t('Voice message');
		r.onstop = function () {
			r.stream.getTracks().forEach(function (x) { x.stop(); });
			if (!send || !recChunks.length) return;
			var mime = (r.mimeType || 'audio/webm').split(';')[0], ext = mime.indexOf('ogg') >= 0 ? 'ogg' : mime.indexOf('mp4') >= 0 ? 'm4a' : 'webm';
			if (ext === 'm4a') mime = 'audio/mp4';
			var blob = new Blob(recChunks, { type: mime });
			var file = new File([blob], 'voice-' + new Date().toISOString().replace(/[:.]/g, '-') + '.' + ext, { type: mime });
			showVoiceDraft(file, Math.round((Date.now() - recStart) / 1000));
		};
		try { r.stop(); } catch (e) { }
	}

	// the recording waiting for review: play it, then send it or delete it
	var voiceDraft = null, vpAudio = $('cmVpAudio');
	function showVoiceDraft(file, seconds) {
		voiceDraft = { file: file, url: URL.createObjectURL(file), seconds: seconds };
		vpAudio.src = voiceDraft.url;
		$('cmVpTime').textContent = dur(seconds); $('cmVpProg').firstElementChild.style.width = '0%';
		$('cmVpPlay').innerHTML = ico('play', 14);
		$('cmVoicePreview').classList.remove('d-none'); compose.classList.add('recording');
		micBtn.disabled = true;
	}
	function dropVoiceDraft() {
		if (!voiceDraft) return;
		try { vpAudio.pause(); } catch (e) { }
		URL.revokeObjectURL(voiceDraft.url); voiceDraft = null; vpAudio.removeAttribute('src');
		$('cmVoicePreview').classList.add('d-none'); compose.classList.remove('recording');
		micBtn.disabled = false;
	}
	$('cmVpPlay').addEventListener('click', function () {
		if (!voiceDraft) return;
		if (vpAudio.paused) vpAudio.play().catch(function () { }); else vpAudio.pause();
	});
	vpAudio.addEventListener('play', function () { $('cmVpPlay').innerHTML = ico('pause', 14); });
	vpAudio.addEventListener('pause', function () { $('cmVpPlay').innerHTML = ico('play', 14); });
	vpAudio.addEventListener('ended', function () { $('cmVpPlay').innerHTML = ico('play', 14); $('cmVpProg').firstElementChild.style.width = '0%'; });
	vpAudio.addEventListener('timeupdate', function () {
		var total = isFinite(vpAudio.duration) && vpAudio.duration > 0 ? vpAudio.duration : (voiceDraft ? voiceDraft.seconds : 0);
		if (!total) return;
		$('cmVpProg').firstElementChild.style.width = Math.min(100, vpAudio.currentTime / total * 100) + '%';
		$('cmVpTime').textContent = dur(Math.round(vpAudio.currentTime)) + ' / ' + dur(Math.round(total));
	});
	$('cmVpProg').addEventListener('click', function (e) {
		var total = isFinite(vpAudio.duration) && vpAudio.duration > 0 ? vpAudio.duration : 0; if (!total) return;
		var r = this.getBoundingClientRect(), x = (isRtl ? r.right - e.clientX : e.clientX - r.left) / r.width;
		vpAudio.currentTime = Math.max(0, Math.min(1, x)) * total;
	});
	$('cmVpDelete').addEventListener('click', function () { dropVoiceDraft(); toast('info', t('The recording was deleted')); });
	$('cmVpSend').addEventListener('click', function () { var f = voiceDraft && voiceDraft.file; dropVoiceDraft(); if (f) sendCore('', f); });

	/* ---------------- edit ---------------- */
	function openEdit(id, body) {
		$('cmEditId').value = id; var ta = $('cmEditText'); ta.value = body || ''; editModal.show();
		setTimeout(function () { ta.focus(); try { ta.setSelectionRange(ta.value.length, ta.value.length); } catch (x) { } }, 250);
	}
	function saveEdit() { var id = $('cmEditId').value, nb = $('cmEditText').value.trim(); if (!id || !nb) return; post('/Chat/Edit', { id: id, body: nb }); editModal.hide(); }
	$('cmEditSave').addEventListener('click', saveEdit);
	$('cmEditText').addEventListener('keydown', function (e) { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); saveEdit(); } });

	function setComposerEnabled(on) {
		$('cmCompose').classList.toggle('locked', !on);
		input.disabled = !on;
		if (!on) input.placeholder = t('You are no longer a member of this group');
		sendBtn.disabled = !on;
	}

	/* ================================================================================================================
	   MEMBERS, NEW CHAT, NEW GROUP
	   ================================================================================================================ */
	function modal(id) { return window.bootstrap ? new bootstrap.Modal($(id)) : { show: function () { }, hide: function () { } }; }
	var membersModal = modal('cmMembersModal'), newModal = modal('cmNewModal'), groupModal = modal('cmGroupModal'), previewModal = modal('cmPreviewModal'), editModal = modal('cmEditModal'), imgViewModal = modal('cmImgViewModal'), recModal = modal('cmRecModal');
	function membersModalOpen() { return $('cmMembersModal').classList.contains('show'); }
	function personRow(d, tail) { return '<div class="cm-row" data-id="' + d.id + '">' + face(d.name, d.avatar, 36, presOf(d.id, d.online)) + '<div class="flex-grow-1" style="min-width:0"><div class="t">' + esc(d.name) + '</div></div>' + (tail || '') + '</div>'; }
	function renderMembers() {
		var owner = curHeader && curHeader.isOwner;
		$('cmMemList').innerHTML = '<div class="cm-row">' + av(CFG.meName, CFG.meAvatar, 36) + '<div class="flex-grow-1"><div class="t">' + esc(t('You')) + (owner ? ' · ' + esc(t('Owner')) : '') + '</div></div><button type="button" class="btn btn-sm btn-light-danger" data-leave="1">' + esc(t('Leave group')) + '</button></div>' +
			members.map(function (d) { return personRow(d, owner ? ib('user-minus', t('Remove'), 'xs', 'data-remove="' + d.id + '"') : ''); }).join('');
		loadDir(function () { renderMemAdd(''); });
	}
	function renderMemAdd(s) {
		var have = {}; members.forEach(function (d) { have[d.id] = 1; }); have[meId] = 1;
		var list = dir.filter(function (d) { return !have[d.id] && d.name.toLowerCase().indexOf(s.toLowerCase()) !== -1; }).slice(0, 30);
		$('cmMemAdd').innerHTML = list.length ? list.map(function (d) { return personRow(d, ib('user-plus', t('Add a member'), 'xs', 'data-add="' + d.id + '"')); }).join('') : '<div class="cm-empty py-4">' + esc(t('No results')) + '</div>';
	}
	$('cmMemSearch').addEventListener('input', function () { renderMemAdd(this.value); });
	$('cmMembersModal').addEventListener('click', function (e) {
		var b = e.target.closest('button'); if (!b) return;
		if (b.hasAttribute('data-add')) post('/Chat/AddMember', { c: cur, employeeId: b.getAttribute('data-add') }).then(refreshHeaderMembers);
		if (b.hasAttribute('data-remove')) post('/Chat/RemoveMember', { c: cur, employeeId: b.getAttribute('data-remove') }).then(refreshHeaderMembers);
		if (b.hasAttribute('data-leave')) ask(t('Leave this group?'), t('Yes, leave')).then(function (ok) {
			if (!ok) return;
			post('/Chat/RemoveMember', { c: cur, employeeId: meId }).then(function () { membersModal.hide(); setComposerEnabled(false); toast('info', t('You left the group')); cur = null; curHeader = null; showBlank(); loadConvs(); });
		});
	});
	function refreshHeaderMembers() {
		if (!cur) return;
		getJson('/Chat/Header?c=' + cur).then(function (h) {
			if (!h || !curHeader) return;
			curHeader.memberCount = h.memberCount; curHeader.isOwner = !!h.isOwner; members = h.members || [];
			paintHead(); paintInfo(); if (membersModalOpen()) renderMembers();
		});
	}
	function dirRows(list, check) {
		return list.map(function (d) { return '<label class="cm-row" style="cursor:pointer" data-id="' + d.id + '" data-name="' + esc(d.name) + '">' + face(d.name, d.avatar, 36, presOf(d.id, d.online)) + '<div class="flex-grow-1" style="min-width:0"><div class="t">' + esc(d.name) + '</div></div>' + (check ? '<input type="checkbox" class="form-check-input cm-grp-chk" value="' + d.id + '" />' : '') + '</label>'; }).join('') || '<div class="cm-empty py-4">' + esc(t('No results')) + '</div>';
	}
	$('cmNewChat').addEventListener('click', function () { loadDir(function () { $('cmDirSearch').value = ''; $('cmDirList').innerHTML = dirRows(dir.slice(0, 60), false); }); newModal.show(); });
	$('cmDirSearch').addEventListener('input', function () { var s = this.value.toLowerCase(); $('cmDirList').innerHTML = dirRows(dir.filter(function (d) { return d.name.toLowerCase().indexOf(s) !== -1; }).slice(0, 60), false); });
	$('cmDirList').addEventListener('click', function (e) { var r = e.target.closest('[data-id]'); if (!r) return; newModal.hide(); chatWith(+r.getAttribute('data-id')); });
	var grpPicked = {};
	$('cmNewGroup').addEventListener('click', function () { grpPicked = {}; $('cmGrpTitle').value = ''; $('cmGrpSearch').value = ''; loadDir(function () { $('cmGrpList').innerHTML = dirRows(dir.slice(0, 60), true); }); groupModal.show(); });
	$('cmGrpSearch').addEventListener('input', function () {
		var s = this.value.toLowerCase(); $('cmGrpList').innerHTML = dirRows(dir.filter(function (d) { return d.name.toLowerCase().indexOf(s) !== -1; }).slice(0, 60), true);
		$('cmGrpList').querySelectorAll('.cm-grp-chk').forEach(function (c) { c.checked = !!grpPicked[c.value]; });
	});
	$('cmGrpList').addEventListener('change', function (e) { if (e.target.classList.contains('cm-grp-chk')) { if (e.target.checked) grpPicked[e.target.value] = 1; else delete grpPicked[e.target.value]; } });
	$('cmGrpCreate').addEventListener('click', function () {
		var title = $('cmGrpTitle').value.trim(); if (!title) { $('cmGrpTitle').focus(); return; }
		post('/Chat/CreateGroup', { title: title, memberIds: Object.keys(grpPicked).join(',') }).then(function (res) {
			if (!res) return; groupModal.hide();
			loadConvs(function (l) { var m = l.find(function (x) { return x.id === res.id; }); openConversation(res.id, m || { kind: 'Group', title: title }); });
		});
	});

	/* ================================================================================================================
	   CALLS
	   ================================================================================================================ */
	function startCall(empId, video) {
		if (!empId) return;
		if (!window.cbCalls) { toast('error', t('Calls are not available here')); return; }
		window.cbCalls.start(empId, !!video);
	}
	function startGroup(video) {
		if (!window.cbCallsGroup) { toast('error', t('Calls are not available here')); return; }
		var ids = members.map(function (m) { return m.id; }).filter(function (id) { return presOf(id) !== 'off'; });
		if (!ids.length) { toast('info', t('Nobody in this group is available now')); return; }
		// the call belongs to this conversation: its members see it live here and can join it
		if (window.cbCallsGroup.startIn && cur) window.cbCallsGroup.startIn(cur, ids, !!video); else window.cbCallsGroup.start(ids, !!video);
	}

	/* ---------------- a group call going on in a conversation: the list's "live", the conversation's banner ---------------- */
	var liveList = [];
	function loadLive() {
		getJson('/Chat/LiveCalls').then(function (l) {
			liveList = l || []; var before = JSON.stringify(liveConvs);
			liveConvs = {}; liveList.forEach(function (x) { liveConvs[x.conversationId] = x; });
			if (before !== JSON.stringify(liveConvs)) { if (tab === 'chats') renderConvs(); }
			paintBanner();
		});
	}
	var bannerTimer = null;
	function paintBanner() {
		var b = $('cmBanner'); if (!b) return;
		var x = cur && liveConvs[cur];
		var key = x ? cur + ':' + x.callId + ':' + (x.joined || []).map(function (p) { return p.id; }).join(',') + ':' + x.inIt : '';
		if (key && key === b.getAttribute('data-key') && !b.classList.contains('d-none')) return;
		b.setAttribute('data-key', key);
		clearInterval(bannerTimer);
		if (!x || !curHeader || curHeader.kind !== 'Group') { b.classList.add('d-none'); b.innerHTML = ''; return; }
		var names = (x.joined || []).map(function (p) { return p.name; });
		var who = names.length > 2 ? names.slice(0, 2).join(t(' and ')) + ' +' + (names.length - 2) : names.join(t(' and '));
		b.innerHTML = '<span class="cm-live-dot"></span>' + ico(x.video ? 'video' : 'phone', 16) +
			'<div class="flex-grow-1" style="min-width:0"><div class="fw-bold">' + esc(t('A group call is on now')) + '</div><div class="fs-8 opacity-75">' + esc(who) + ' · ' + esc(t('for')) + ' <span class="num" data-since></span></div></div>' +
			'<span class="cm-stack">' + (x.joined || []).slice(0, 3).map(function (p) { return '<span class="symbol">' + av(p.name, p.photo, 28) + '</span>'; }).join('') + '</span>' +
			(x.inIt ? '' : '<button type="button" class="cm-pill light" data-join="' + x.callId + '" data-video="' + (x.video ? 1 : 0) + '">' + esc(t('Join')) + '</button>');
		b.classList.remove('d-none');
		var since = utc(x.since).getTime(), el = b.querySelector('[data-since]');
		function tick() { el.textContent = dur(Math.max(0, Math.round((Date.now() - since) / 1000))); }
		tick(); bannerTimer = setInterval(tick, 1000);
	}
	$('cmBanner').addEventListener('click', function (e) {
		var j = e.target.closest('[data-join]'); if (!j) return;
		if (window.cbCallsGroup && window.cbCallsGroup.joinIn) window.cbCallsGroup.joinIn(+j.getAttribute('data-join'), j.getAttribute('data-video') === '1');
	});
	setInterval(loadLive, 10000);
	document.addEventListener('cbcalls:changed', function () { setTimeout(loadLive, 800); });
	function openRecording(callId) {
		var k = calls.find(function (x) { return x.id === callId; }) || (callLog && (callLog.rows || []).find(function (x) { return x.id === callId; }));
		if (!k || !k.recordingId) return;
		$('cmRecBody').innerHTML = '<div id="cmPlayer" class="cm-player mt-0"><div class="text-muted fs-7">…</div></div>';
		recModal.show();
		loadPlayer(k.recordingId);
	}
	$('cmRecModal').addEventListener('hidden.bs.modal', function () { $('cmRecBody').innerHTML = ''; });
	function askPresence(ids) {
		if (!window.cbCalls || !ids || !ids.length) return;
		window.cbCalls.presenceOf(ids).then(function (rows) {
			var changed = false;
			(rows || []).forEach(function (r) { if (pres[r.employeeId] !== r.state) { pres[r.employeeId] = r.state; changed = true; } });
			if (changed) repaintPresence();
		}).catch(function () { });
	}
	var presT = null;
	function repaintPresence() { clearTimeout(presT); presT = setTimeout(function () { renderList(); if (curHeader) { paintHead(); paintInfo(); } }, 120); }
	function hookCalls() {
		if (!window.cbCalls || hookCalls.done) return; hookCalls.done = true;
		window.cbCalls.onPresence(function (m) {
			if (m && m.refresh) { askPresence(Object.keys(pres).map(Number)); return; }
			if (m && m.employeeId) { pres[m.employeeId] = m.state; repaintPresence(); }
		});
		askPresence(convs.filter(function (c) { return c.otherEmployeeId; }).map(function (c) { return c.otherEmployeeId; }));
	}
	document.addEventListener('cbcalls:ready', hookCalls);
	// a call ended or changed: write it into the conversation, refresh the log
	document.addEventListener('cbcalls:changed', function () {
		if (curHeader && curHeader.other) {
			var opened = cur;
			getJson('/Chat/CallsWith?other=' + curHeader.other).then(function (r) { if (cur !== opened || !r) return; var stick = nearBottom(); calls = r; placeCalls(); regroup(); paintInfo(); if (stick) scrollBottom(); });
		}
		if (tab === 'calls') loadCalls(); else getJson('/Chat/Pulse').then(function (r) { if (r && r.ok) setMissed(r.missed); });
	});

	/* ================================================================================================================
	   LIVE: the chat hub
	   ================================================================================================================ */
	function startHub() {
		if (!window.signalR) return;
		conn = new signalR.HubConnectionBuilder().withUrl('/hubs/chat').withAutomaticReconnect().build();
		fire('hub', conn);
		conn.on('message', function (m) {
			// mine too: a message sent in my name from elsewhere (another device, or a webhook I made) belongs here as well;
			// what this page sent itself is already shown, and appendMsg skips an id it has seen
			if (m.conversationId === cur) {
				var stick = nearBottom(), el = appendMsg(m, false);
				if (el) { regroup(); paintInfo(); if (stick || m.senderId === meId) scrollBottom(); fire('loaded', [m.id], 'live'); }
				if (m.senderId !== meId) { typingEl.innerHTML = ''; post('/Chat/MarkRead', { c: cur }); }
			}
			loadConvs();
		});
		conn.on('edited', function (e) {
			if (e.conversationId !== cur) return;
			var m = msgCache[e.id]; if (!m) return; m.body = e.body; m.editedAt = new Date().toISOString();
			var el = thread.querySelector('.cm-msg[data-id="' + e.id + '"]'); if (!el) return;
			el.querySelector('.cm-bubble').innerHTML = bubbleInner(m); el.setAttribute('data-body', e.body || ''); el.setAttribute('data-text', e.body || ''); regroup();
		});
		conn.on('deleted', function (e) {
			if (e.conversationId !== cur) return;
			var m = msgCache[e.id], el = thread.querySelector('.cm-msg[data-id="' + e.id + '"]'); if (!el || !m) return;
			m.deleted = true; m.reactions = [];
			var fresh = msgEl(m); el.replaceWith(fresh); regroup(); paintInfo();
		});
		conn.on('reaction', function (e) {
			if (e.conversationId !== cur) return;
			var m = msgCache[e.id], el = thread.querySelector('.cm-msg[data-id="' + e.id + '"]'); if (!el) return;
			// the broadcast carries the counts; whether each is mine is kept from what I knew, or flipped by my own tap
			var before = {}; (m && m.reactions || []).forEach(function (r) { before[r.emoji] = !!r.mine; });
			var tapped = myTaps[e.id]; if (tapped) { before[tapped] = !before[tapped]; delete myTaps[e.id]; }
			var list = (e.reactions || []).map(function (r) { return { emoji: r.emoji, count: r.count, mine: !!before[r.emoji] }; });
			if (m) m.reactions = list;
			var old = el.querySelector('.cm-reacts'); var html = reactsHtml({ reactions: list });
			if (old) old.outerHTML = html || ''; else if (html) el.querySelector('.cm-bwrap').insertAdjacentHTML('beforeend', html);
		});
		conn.on('typing', function (e) {
			if (e.conversationId !== cur || e.employeeId === meId) return;
			var nm = curHeader && curHeader.kind === 'Direct' ? curHeader.title : '';
			if (curHeader && curHeader.kind === 'Group') { var d = members.find(function (x) { return x.id === e.employeeId; }) || dir.find(function (x) { return x.id === e.employeeId; }); if (d) nm = d.name; }
			typingEl.innerHTML = '<span class="dots"><i></i><i></i><i></i></span>' + esc((nm ? nm + ' ' : '') + t('is typing'));
			clearTimeout(typingEl._t); typingEl._t = setTimeout(function () { typingEl.innerHTML = ''; }, 2500);
		});
		conn.on('presence', function () { loadConvs(); });
		conn.on('conversation', function () { loadConvs(); });
		conn.on('members', function (e) { if (e && e.conversationId === cur) refreshHeaderMembers(); loadConvs(); });
		conn.on('removed', function (e) {
			if (e && e.conversationId === cur) { setComposerEnabled(false); if (membersModalOpen()) membersModal.hide(); toast('warning', t('You were removed from this group')); }
			loadConvs();
		});
		conn.on('read', function (e) {
			if (e.conversationId !== cur || e.employeeId === meId) return;
			otherReadUpTo = Math.max(otherReadUpTo, e.lastReadId || 0);
			memberReads[e.employeeId] = Math.max(memberReads[e.employeeId] || 0, e.lastReadId || 0);
			paintTicks();
		});
		conn.onreconnected(function () {
			if (cur) {
				try { conn.invoke('JoinConversation', cur); } catch (e) { }
				getJson('/Chat/Messages?c=' + cur).then(function (msgs) { var added = false; (msgs || []).forEach(function (m) { if (appendMsg(m, false)) added = true; }); if (added) { regroup(); scrollBottom(); } });
			}
			loadConvs();
		});
		conn.start().then(function () { if (cur) conn.invoke('JoinConversation', cur); }).catch(function () { });
	}

	/* ---------------- the API comm-plus.js builds on ---------------- */
	window.cbComm = {
		on: on, fire: fire, sendFilters: sendFilters, itemDecor: itemDecor,
		t: t, esc: esc, ico: ico, ib: ib, av: av, face: face, num: num, when: when, utc: utc, hm: hm, dur: dur, dayLabel: dayLabel,
		post: post, getJson: getJson, toast: toast, ask: ask, richText: richText,
		cur: function () { return cur; }, header: function () { return curHeader; }, members: function () { return members; }, dir: function () { return dir; },
		convs: function () { return convs; }, msg: function (id) { return msgCache[id]; }, meId: meId, cfg: CFG, presOf: presOf,
		els: { thread: thread, info: info, head: head, input: input, items: items, shell: shell, compose: compose, blank: blankBox },
		open: function (id, then) {
			var c = convs.find(function (x) { return x.id === id; });
			if (c) { openConversation(c.id, c); if (then) then(); return; }
			loadConvs(function (l) { var m = l.find(function (x) { return x.id === id; }); if (m) { openConversation(m.id, m); if (then) then(); } });
		},
		appendMsg: function (m) { var el = appendMsg(m, false); regroup(); scrollBottom(); return el; },
		regroup: regroup, scrollBottom: scrollBottom, gotoMsg: gotoMsg, loadConvs: loadConvs, paintInfo: paintInfo, paintHead: paintHead, renderConvs: renderConvs,
		setReply: setReply, pickFile: pickFile, sendCore: sendCore, closePop: closePop, chatWith: chatWith, setTab: setTab, tab: function () { return tab; },
		pop: function (anchor, html, onPick) { popFor = onPick; pop.innerHTML = html; placePop(anchor); return pop; },
		showBlank: showBlank, isPhone: isPhone, setPane: setPane, startCall: startCall, conn: function () { return conn; },
		detectMentions: detectMentions, autoGrow: autoGrow,
		reads: function () { return { direct: otherReadUpTo, members: memberReads }; },
	};

	/* ================================================================================================================
	   PHONE: one pane at a time; DESKTOP: the shell fills the screen
	   ================================================================================================================ */
	var PHONE = '(max-width: 991.98px)';
	function isPhone() { return window.matchMedia(PHONE).matches; }
	function setPane(which) { shell.setAttribute('data-pane', which); fit(); if (which === 'thread') setTimeout(scrollBottom, 60); }
	function fit() {
		var top = shell.getBoundingClientRect().top + window.scrollY;
		shell.style.height = Math.max(520, window.innerHeight - Math.max(0, top - window.scrollY) - 24) + 'px';
	}
	window.addEventListener('resize', fit);

	/* ================================================================================================================
	   BOOT
	   ================================================================================================================ */
	mo.observe(document.body, { childList: true, subtree: true });
	if (window.signalR) startHub(); else if (CFG.signalr) { var sc = document.createElement('script'); sc.src = CFG.signalr; sc.onload = startHub; document.head.appendChild(sc); }
	if (window.cbCalls && window.cbCalls.connected && window.cbCalls.connected()) hookCalls();
	setTimeout(hookCalls, 2500);
	renderChips();
	document.querySelectorAll('#cmTabs [data-tab]').forEach(function (b) { b.classList.toggle('on', b.getAttribute('data-tab') === tab); });
	showBlank();
	loadDir();
	loadPeople();
	loadLive();
	getJson('/Chat/Pulse').then(function (r) { if (r && r.ok) setMissed(r.missed); });
	loadConvs(function (list) {
		if (CFG.openConv) { var m = list.find(function (x) { return x.id === CFG.openConv; }); if (m) { openConversation(m.id, m); return; } }
		if (tab !== 'chats') setTab(tab);
		else if (!isPhone() && list.length) openConversation(list[0].id, list[0]);
	});
	syncSend(); fit(); setTimeout(fit, 300); paint();
})();
