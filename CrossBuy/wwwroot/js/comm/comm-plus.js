/* =====================================================================================================================
   Communication v2 (owner, 2026-11-18: "build every gap, completely") — what the leading team-chat systems have and ours
   did not, added to the Communication page through comm.js's hooks (window.cbComm):
   threads · forwarding · pinned messages · saved messages and reminders · polls · scheduled messages · a record of the
   system as a card · a task from a message · an unread divider and a catch-up of what was missed · who read it ·
   status and do-not-disturb · channels · a group's description and picture · search across every conversation ·
   approvals waiting for me · a meeting in the calendar · slash commands · the company's sensitive-data rule.
   In the prototype's own language (comm.css), Lucide icons, no browser confirm() (Swal).
   ===================================================================================================================== */
(function () {
	'use strict';
	var C = window.cbComm; if (!C) return;
	var t = C.t, esc = C.esc, ico = C.ico, ib = C.ib, av = C.av, num = C.num, post = C.post, getJson = C.getJson, toast = C.toast;
	var E = C.els, meId = C.meId;
	var isRtl = document.documentElement.dir === 'rtl';
	function $(id) { return document.getElementById(id); }
	function swal(o) { return window.Swal ? Swal.fire(Object.assign({ buttonsStyling: false, showCancelButton: true, cancelButtonText: t('Cancel'), customClass: { confirmButton: 'btn btn-primary', cancelButton: 'btn btn-light', popup: 'cm-root' } }, o)) : Promise.resolve({}); }
	function iso(d) { return d.toISOString(); }
	function localInput(d) { var p = function (n) { return (n < 10 ? '0' : '') + n; }; return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()) + 'T' + p(d.getHours()) + ':' + p(d.getMinutes()); }
	var Q = new URLSearchParams(location.search);

	/* ================================================================================================================
	   META — what each loaded message carries beyond itself (thread, forwarded, pinned, saved, poll)
	   ================================================================================================================ */
	var meta = {};
	C.on('loaded', function (ids) { loadMeta(ids); });
	C.on('open', function () { meta = {}; closeThread(); hideBars(); });
	function loadMeta(ids) {
		var c = C.cur(); if (!c || !ids || !ids.length) return;
		getJson('/Chat/Meta?c=' + c + '&ids=' + ids.join(',')).then(function (rows) {
			if (C.cur() !== c || !rows) return;
			rows.forEach(function (r) { meta[r.id] = r; decorate(r.id); });
		});
	}
	function msgEl(id) { return E.thread.querySelector('.cm-msg[data-id="' + id + '"]'); }
	function decorate(id) {
		var el = msgEl(id), r = meta[id]; if (!el || !r) return;
		var bub = el.querySelector('.cm-bubble'), wrap = el.querySelector('.cm-bwrap');
		// forwarded
		var fw = bub.querySelector('.cm-fwd'); if (fw) fw.remove();
		if (r.forwarded) bub.insertAdjacentHTML('afterbegin', '<div class="cm-fwd">' + ico('forward', 12) + esc(t('Forwarded')) + (r.forwardedFrom ? ' · ' + esc(r.forwardedFrom) : '') + '</div>');
		// pinned
		var pn = bub.querySelector('.cm-pinmark'); if (pn) pn.remove();
		if (r.pinned) { var mt = bub.querySelector('.cm-meta'); if (mt) mt.insertAdjacentHTML('afterbegin', '<span class="cm-pinmark" title="' + esc(t('Pinned by') + ' ' + (r.pinnedBy || '')) + '">' + ico('pin', 11) + '</span>'); }
		// thread
		var th = wrap.querySelector('.cm-thread-link'); if (th) th.remove();
		if (r.thread && r.thread.count) {
			wrap.insertAdjacentHTML('beforeend', '<button type="button" class="cm-thread-link" data-thread="' + id + '"><span class="cm-stackf">' + r.thread.people.map(function (p) { return av(p.name, p.avatar, 20); }).join('') + '</span>' +
				'<b>' + num(r.thread.count) + ' ' + esc(r.thread.count === 1 ? t('reply (count)') : t('replies (count)')) + '</b><span>' + esc(t('last')) + ' ' + num(C.when(r.thread.last)) + '</span>' + ico(isRtl ? 'chevron-left' : 'chevron-right', 14) + '</button>');
		}
		// came in through a webhook
		var vi = bub.querySelector('.cm-via'); if (vi) vi.remove();
		if (r.via) bub.insertAdjacentHTML('afterbegin', '<div class="cm-via">' + ico('webhook', 12) + esc(t('via')) + ' <b dir="auto">' + esc(r.via) + '</b></div>');
		// poll
		if (r.poll) paintPoll(el, r.poll);
		// a voice message's words
		paintTranscript(el, id, r.transcript);
		// its translation, once someone here asked for it
		if (trShown[id] && r.translations) { var tr = r.translations.find(function (x) { return x.lang === trShown[id]; }); if (tr) paintTranslation(el, tr); }
	}

	/* ================================================================================================================
	   TRANSLATION — «ترجمة» in a message's menu: into the other language (ar ↔ en), on this machine, kept for everyone
	   ================================================================================================================ */
	var trShown = {};
	function paintTranslation(el, tr) {
		var bub = el.querySelector('.cm-bubble'); if (!bub) return;
		var old = bub.querySelector('.cm-translation'); if (old) old.remove();
		var meta = bub.querySelector('.cm-meta');
		var html = '<div class="cm-translation"><div class="lb">' + ico('languages', 12) + esc(t('Translation')) + '</div><div class="tx" dir="auto">' + esc(tr.text) + '</div></div>';
		if (meta) meta.insertAdjacentHTML('beforebegin', html); else bub.insertAdjacentHTML('beforeend', html);
	}
	function translateMsg(id) {
		var el = msgEl(id); if (!el) return;
		var bub = el.querySelector('.cm-bubble');
		var wait = document.createElement('div'); wait.className = 'cm-translation'; wait.innerHTML = '<div class="lb"><span class="spinner-border spinner-border-sm"></span> ' + esc(t('Translating…')) + '</div>';
		var old = bub.querySelector('.cm-translation'); if (old) old.remove();
		var meta = bub.querySelector('.cm-meta'); if (meta) bub.insertBefore(wait, meta); else bub.appendChild(wait);
		post('/Chat/Translate', { id: id }).then(function (x) {
			if (!x || !x.ok) { wait.remove(); toast('error', t('The assistant is not available right now — try again later')); return; }
			trShown[id] = x.lang;
			paintTranslation(msgEl(id) || el, { lang: x.lang, text: x.text });
		});
	}

	/* ================================================================================================================
	   THE ASSISTANT — «المساعد» in the conversation's head: a summary of it, or a question about it, answered from it
	   alone by the company's own model. Every answer names the messages it rests on; nothing is made up.
	   ================================================================================================================ */
	C.on('head', function () {
		var acts = E.head.querySelector('.acts'); if (!acts || acts.querySelector('[data-assist]')) return;
		var find = acts.querySelector('[data-find]');
		var html = ib('sparkles', t('Assistant'), 'ghost', 'data-assist="1"');
		if (find) find.insertAdjacentHTML('beforebegin', html); else acts.insertAdjacentHTML('beforeend', html);
	});
	E.head.addEventListener('click', function (e) { if (e.target.closest && e.target.closest('[data-assist]')) openAssistant(); });
	function newestId() { var all = E.thread.querySelectorAll('.cm-msg[data-id]'); return all.length ? +all[all.length - 1].getAttribute('data-id') : 0; }
	function openAssistant() {
		var c = C.cur(); if (!c) return;
		var html = '<div class="cm-assist text-start">' +
			'<div class="d-grid gap-2"><button type="button" class="btn btn-primary" data-as="summary">' + ico('sparkles', 15) + ' ' + esc(t('Summarise the conversation')) + '</button></div>' +
			'<div class="or">' + esc(t('or')) + '</div>' +
			'<div class="d-flex gap-2"><input id="asQ" class="form-control form-control-solid" maxlength="300" placeholder="' + esc(t('Ask about this conversation…')) + '" />' +
			'<button type="button" class="btn btn-light-primary" data-as="ask">' + esc(t('Ask')) + '</button></div>' +
			'<div id="asOut" class="out"></div>' +
			'<div class="nt">' + esc(t('Answered from this conversation only, on the company’s own servers.')) + '</div></div>';
		swal({ title: t('Assistant'), html: html, showConfirmButton: false, cancelButtonText: t('Close'), width: 620 });
		var pop = document.querySelector('.swal2-popup');
		pop.addEventListener('click', function (e) {
			var b = e.target.closest('[data-as]'); if (b) { run(b.getAttribute('data-as')); return; }
			var g = e.target.closest('[data-goto-msg]'); if (g) { Swal.close(); C.gotoMsg(+g.getAttribute('data-goto-msg')); return; }
			var k = e.target.closest('[data-as-task]'); if (k) { var title = k.getAttribute('data-as-task'); Swal.close(); makeTask(newestId(), title); }
		});
		pop.querySelector('#asQ').addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); run('ask'); } });
		function out(h) { var o = pop.querySelector('#asOut'); if (o) o.innerHTML = h; }
		function run(mode) {
			var q = mode === 'ask' ? (pop.querySelector('#asQ').value || '').trim() : null;
			if (mode === 'ask' && !q) { pop.querySelector('#asQ').focus(); return; }
			pop.querySelectorAll('[data-as]').forEach(function (x) { x.disabled = true; });
			out('<div class="wait"><span class="spinner-border spinner-border-sm"></span> ' + esc(mode === 'ask' ? t('Reading the conversation…') : t('Summarising the conversation… this can take a few minutes')) + '</div>');
			post('/Chat/Assist', { c: c, mode: mode, question: q }).then(function (x) {
				if (!x || !x.ok) { done(); out('<div class="nt">' + esc(x && x.code === 'nothing_to_summarise' ? t('There is nothing written here to read yet.') : t('The assistant is not available right now — try again later')) + '</div>'); return; }
				poll(x.job, mode);
			});
		}
		function done() { pop.querySelectorAll('[data-as]').forEach(function (x) { x.disabled = false; }); }
		function poll(job, mode) {
			setTimeout(function () {
				if (!document.body.contains(pop)) return;
				getJson('/Chat/AssistStatus?job=' + encodeURIComponent(job)).then(function (s) {
					if (!s || s.status === 'working') { poll(job, mode); return; }
					done();
					if (s.status !== 'done' || !s.result) { out('<div class="nt">' + esc(t('The assistant is not available right now — try again later')) + '</div>'); return; }
					out(mode === 'ask' ? askHtml(s.result) : summaryHtml(s.result));
				});
			}, 3000);
		}
		function list(title, icon, items) { return items && items.length ? '<div class="sec"><div class="st">' + ico(icon, 13) + esc(title) + '</div><ul>' + items.join('') + '</ul></div>' : ''; }
		function cutNote(r) { return r.cut ? '<div class="nt">' + esc(t('Only the newest part of a long conversation was read.')) + '</div>' : ''; }
		function summaryHtml(r) {
			if (r.tooShort || !(r.overview || (r.points || []).length)) return '<div class="nt">' + esc(t('Too little was written here to summarise.')) + '</div>';
			return (r.overview ? '<div class="ov" dir="auto">' + esc(r.overview) + '</div>' : '') +
				list(t('Key points'), 'list', (r.points || []).map(function (p) { return '<li dir="auto">' + esc(p) + '</li>'; })) +
				list(t('Decisions'), 'circle-check', (r.decisions || []).map(function (p) { return '<li dir="auto">' + esc(p) + '</li>'; })) +
				list(t('Tasks'), 'list-todo', (r.actions || []).map(function (a) {
					return '<li dir="auto"><b>' + esc(a.owner || '') + '</b> — ' + esc(a.task || '') + ' <button type="button" class="mk" data-as-task="' + esc(a.task || '') + '">' + ico('square-check', 12) + esc(t('Make it a task')) + '</button></li>';
				})) + cutNote(r);
		}
		function askHtml(r) {
			if (!r.found) return '<div class="nt">' + esc(t('There is no answer to this in the conversation.')) + '</div>' + cutNote(r);
			return '<div class="ov" dir="auto">' + esc(r.answer) + '</div><div class="src">' + esc(t('From')) + ': ' +
				(r.messageIds || []).map(function (id) { return '<button type="button" data-goto-msg="' + id + '">' + ico('corner-down-left', 12) + num('#' + id) + '</button>'; }).join(' ') + '</div>' + cutNote(r);
		}
	}

	/* ================================================================================================================
	   SPEECH TO TEXT — «النص» under a voice message: transcribed once on this machine, then shown to every reader
	   ================================================================================================================ */
	function paintTranscript(el, id, tr) {
		var vb = el.querySelector('.cm-voice'); if (!vb) return;
		var box = el.querySelector('.cm-transcript'); if (box) box.remove();
		var html;
		if (tr && tr.status === 'done') html = tr.text ? '<div class="tx" dir="auto">' + esc(tr.text) + '</div>' : '<div class="nt">' + esc(t('No words were heard in this recording')) + '</div>';
		else if (tr && tr.status === 'too_long') html = '<div class="nt">' + esc(t('Too long to turn into text (about a minute and a half at most)')) + '</div>';
		else html = '<button type="button" class="go" data-transcribe="' + id + '">' + ico('captions', 14) + '<span>' + esc(t('Show the text')) + '</span></button>';
		vb.insertAdjacentHTML('afterend', '<div class="cm-transcript">' + html + '</div>');
	}
	document.addEventListener('click', function (e) {
		var b = e.target.closest && e.target.closest('[data-transcribe]'); if (!b) return;
		var id = +b.getAttribute('data-transcribe');
		b.disabled = true; b.innerHTML = '<span class="spinner-border spinner-border-sm"></span><span>' + esc(t('Turning it into text…')) + '</span>';
		post('/Chat/Transcribe', { id: id }).then(function (x) {
			var el = msgEl(id); if (!el) return;
			if (x && (x.ok || x.status === 'too_long')) { meta[id] = meta[id] || { id: id }; meta[id].transcript = { status: x.status, text: x.text, language: x.language }; paintTranscript(el, id, meta[id].transcript); return; }
			toast('error', t('The text is not available right now — try again later'));
			paintTranscript(el, id, null);
		});
	});

	/* ================================================================================================================
	   THE HOVER BAR and the "more" menu — reply in a thread, forward, pin, save, remind, make a task
	   ================================================================================================================ */
	C.on('message', function (el, m) {
		if (m.deleted) return;
		var bar = el.querySelector('.cm-hover'), more = bar && bar.querySelector('[data-act="more"]');
		if (more) more.insertAdjacentHTML('beforebegin', ib('messages-square', t('Reply in a thread'), 'xs', 'data-act="thread"'));
		recordCard(el, m);
	});
	C.on('more', function (rows, el, id) {
		var r = meta[id] || {}, m = C.msg(id) || {};
		rows.unshift(['messages-square', t('Reply in a thread'), 'thread', function () { openThread(id); }]);
		if (m.attachmentType !== 'poll') rows.push(['forward', t('Forward'), 'forward', function () { forwardMsg(id); }]);
		rows.push([r.pinned ? 'pin-off' : 'pin', r.pinned ? t('Unpin from the conversation') : t('Pin in the conversation'), 'pinmsg', function () { post('/Chat/PinMessage', { id: id, on: !r.pinned }).then(function (x) { if (x && x.ok) { loadMeta([id]); loadPinned(); } else toast('error', t('Could not pin it (at most 50)')); }); }]);
		rows.push([r.saved ? 'bookmark-x' : 'bookmark', r.saved ? t('Remove from saved') : t('Save'), 'save', function () { post('/Chat/SaveMessage', { id: id, on: !r.saved }).then(function () { loadMeta([id]); toast('success', r.saved ? t('Removed from saved') : t('Saved')); }); }]);
		rows.push(['alarm-clock', t('Remind me'), 'remind', function () { remindMenu(id); }]);
		rows.push(['square-check', t('Make a task'), 'task', function () { makeTask(id); }]);
		if (m.body && m.attachmentType !== 'poll') rows.push(['languages', t('Translate'), 'translate', function () { translateMsg(id); }]);
		if (m.attachmentType === 'poll' && el.classList.contains('mine') && r.poll && !r.poll.closed) rows.push(['lock', t('Close the poll'), 'closepoll', function () { post('/Chat/ClosePoll', { id: id }); }]);
	});
	E.thread.addEventListener('click', function (e) {
		var b = e.target.closest('[data-act="thread"], [data-thread], [data-vote], [data-catch], [data-tick]');
		if (!b) return;
		if (b.getAttribute('data-act') === 'thread') { e.stopPropagation(); openThread(+b.closest('.cm-msg').getAttribute('data-id')); }
		else if (b.hasAttribute('data-thread')) openThread(+b.getAttribute('data-thread'));
		else if (b.hasAttribute('data-vote')) vote(b);
		else if (b.hasAttribute('data-catch')) { var id = +b.getAttribute('data-catch'), tr = +(b.getAttribute('data-root') || 0); if (tr) openThread(tr); else C.gotoMsg(id); }
	}, true);

	/* ================================================================================================================
	   THREADS — the replies to a message, in the details pane
	   ================================================================================================================ */
	var threadRoot = null;
	function openThread(root) {
		var c = C.cur(); if (!c) return;
		threadRoot = root;
		E.shell.classList.remove('no-info'); E.shell.classList.add('info-open', 'thread-open');
		E.info.innerHTML = '<div class="cm-tpane"><div class="cm-head"><div class="min-w-0"><div class="nm">' + esc(t('Thread')) + '</div><div class="st text-muted fs-8">' + esc(C.header() ? C.header().title : '') + '</div></div>' +
			'<div class="acts">' + ib('x', t('Close'), 'ghost sm', 'data-tclose="1"') + '</div></div><div class="cm-thread cm-tlist" data-tlist><div class="cm-empty">…</div></div>' +
			'<div class="cm-compose"><div class="box"><textarea data-tinput rows="1" placeholder="' + esc(t('Reply in the thread…')) + '"></textarea></div><button type="button" class="cm-send" data-tsend title="' + esc(t('Send')) + '">' + ico('send-horizontal', 18) + '</button></div></div>';
		var list = E.info.querySelector('[data-tlist]'), inp = E.info.querySelector('[data-tinput]');
		getJson('/Chat/Thread?c=' + c + '&root=' + root).then(function (msgs) {
			if (threadRoot !== root) return;
			list.innerHTML = '';
			(msgs || []).forEach(function (m, i) { list.insertAdjacentHTML('beforeend', tmsg(m, i === 0)); });
			if ((msgs || []).length <= 1) list.insertAdjacentHTML('beforeend', '<div class="cm-empty py-4">' + esc(t('No replies yet — start the thread')) + '</div>');
			list.scrollTop = list.scrollHeight;
		});
		function send() {
			var body = inp.value.trim(); if (!body) return;
			inp.value = '';
			post('/Chat/ThreadReply', { c: c, root: root, body: body, mentionedIds: C.detectMentions(body) }).then(function (m) {
				if (!m || !m.id) { inp.value = body; toast('error', t('Send failed')); return; }
				addToThread(root, m);
			});
		}
		E.info.querySelector('[data-tsend]').onclick = send;
		inp.addEventListener('keydown', function (e) { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); } });
		E.info.querySelector('[data-tclose]').onclick = closeThread;
		inp.focus();
	}
	function tmsg(m, root) {
		var mine = m.senderId === meId;
		return '<div class="cm-msg' + (mine ? ' mine' : '') + (root ? ' cm-troot' : '') + '" data-tid="' + m.id + '">' + (mine ? '' : '<span class="cm-msg-face">' + av(m.senderName, m.senderAvatar, 28) + '</span>') +
			'<div class="cm-bwrap"><div class="cm-bubble">' + (mine ? '' : '<div class="who">' + esc(m.senderName) + '</div>') +
			(m.deleted ? '<div class="tx">' + esc(t('Message deleted')) + '</div>' : (m.attachmentName && m.attachmentType !== 'poll' ? '<div class="cm-fwd">' + ico('paperclip', 12) + esc(m.attachmentName) + '</div>' : '') + (m.body ? '<div class="tx" dir="auto">' + C.richText(m.body) + '</div>' : '')) +
			'<div class="cm-meta">' + num(C.hm(C.utc(m.createdAt))) + '</div></div></div></div>' + (root ? '<div class="cm-tsep"><span>' + esc(t('Replies')) + '</span></div>' : '');
	}
	function addToThread(root, m) {
		if (threadRoot === root) {
			var list = E.info.querySelector('[data-tlist]');
			if (list && !list.querySelector('[data-tid="' + m.id + '"]')) {
				var em = list.querySelector('.cm-empty'); if (em) em.remove();
				list.insertAdjacentHTML('beforeend', tmsg(m, false)); list.scrollTop = list.scrollHeight;
			}
		}
		loadMeta([root]);
	}
	function closeThread() {
		if (threadRoot == null) return;
		threadRoot = null; E.shell.classList.remove('thread-open', 'info-open');
		C.paintInfo();
	}

	/* ================================================================================================================
	   FORWARD
	   ================================================================================================================ */
	function forwardMsg(id) {
		var convs = C.convs();
		var html = '<div class="text-start"><div class="cm-search mb-3">' + ico('search', 16) + '<input id="fwQ" placeholder="' + esc(t('Search')) + '" /></div>' +
			'<div id="fwList" class="scroll-y" style="max-height:320px">' + convs.map(function (c) {
				return '<label class="cm-row" style="cursor:pointer" data-name="' + esc((c.title || '').toLowerCase()) + '">' + (c.kind === 'Direct' ? av(c.title, c.avatar, 32) : '<span class="cm-grp" style="width:32px;height:32px">' + ico('users-round', 14) + '</span>') +
					'<span class="t flex-grow-1">' + esc(c.title) + '</span><input type="checkbox" class="form-check-input" value="' + c.id + '" /></label>';
			}).join('') + '</div><input id="fwNote" class="form-control form-control-solid mt-3" placeholder="' + esc(t('Add a note (optional)')) + '" /></div>';
		swal({ title: t('Forward'), html: html, confirmButtonText: t('Forward'), didOpen: function () {
			$('fwQ').addEventListener('input', function () { var q = this.value.trim().toLowerCase(); document.querySelectorAll('#fwList [data-name]').forEach(function (r) { r.style.display = !q || r.getAttribute('data-name').indexOf(q) !== -1 ? '' : 'none'; }); });
			paintIcons();
		}, preConfirm: function () {
			var to = [].slice.call(document.querySelectorAll('#fwList input:checked')).map(function (x) { return x.value; });
			if (!to.length) { Swal.showValidationMessage(t('Choose at least one conversation')); return false; }
			return { to: to.join(','), note: $('fwNote').value };
		} }).then(function (r) {
			if (!r.isConfirmed) return;
			post('/Chat/Forward', { id: id, to: r.value.to, note: r.value.note }).then(function (x) { if (x && x.ok) { toast('success', t('Forwarded') + ' · ' + x.sent); C.loadConvs(); } else toast('error', t('Could not forward')); });
		});
	}
	function paintIcons() { if (window.lucide) try { lucide.createIcons({ attrs: { 'stroke-width': 1.75 } }); } catch (e) { } }

	/* ================================================================================================================
	   PINNED MESSAGES — a bar under the head; the list in the details pane
	   ================================================================================================================ */
	var pinnedRows = [];
	function hideBars() { var b = $('cmPinBar'); if (b) b.remove(); var s = $('cmSchedBar'); if (s) s.remove(); }
	C.on('ready', function () { loadPinned(); loadScheduled(); dividerAndCatchUp(); deepLink(); });
	function loadPinned() {
		var c = C.cur(); if (!c) return;
		getJson('/Chat/Pinned?c=' + c).then(function (rows) {
			if (C.cur() !== c) return;
			pinnedRows = rows || [];
			var bar = $('cmPinBar');
			if (!pinnedRows.length) { if (bar) bar.remove(); return; }
			if (!bar) { bar = document.createElement('button'); bar.type = 'button'; bar.id = 'cmPinBar'; bar.className = 'cm-pinbar'; E.head.insertAdjacentElement('afterend', bar); bar.onclick = showPinned; }
			var p = pinnedRows[0];
			bar.innerHTML = ico('pin', 14) + '<span class="min-w-0 text-truncate"><b>' + esc(p.sender || '') + ':</b> ' + esc(p.body || '') + '</span>' + (pinnedRows.length > 1 ? '<span class="cm-count">' + pinnedRows.length + '</span>' : '');
		});
	}
	function showPinned() {
		E.shell.classList.remove('no-info'); E.shell.classList.add('info-open');
		E.info.innerHTML = '<div class="sect"><h4>' + ico('pin', 15) + esc(t('Pinned messages')) + ' · ' + num(pinnedRows.length) + '<button type="button" class="cm-ib ghost xs ms-auto" data-pclose>' + ico('x', 16) + '</button></h4>' +
			pinnedRows.map(function (p) { return '<button type="button" class="cm-row w-100 border-0 bg-transparent text-start" data-goto="' + p.id + '"><span class="cm-chip">' + ico('pin', 14) + '</span><div class="min-w-0"><div class="t text-truncate">' + esc(p.body || '') + '</div><div class="s">' + esc(p.sender || '') + ' · ' + esc(t('pinned by (person)')) + ' ' + esc(p.by || '') + '</div></div></button>'; }).join('') + '</div>';
		E.info.querySelectorAll('[data-goto]').forEach(function (b) { b.onclick = function () { C.gotoMsg(b.getAttribute('data-goto')); }; });
		E.info.querySelector('[data-pclose]').onclick = function () { C.paintInfo(); E.shell.classList.remove('info-open'); };
	}

	/* ================================================================================================================
	   REMINDERS, a TASK from a message
	   ================================================================================================================ */
	function remindMenu(id) {
		var now = new Date(), tom = new Date(now); tom.setDate(now.getDate() + 1); tom.setHours(9, 0, 0, 0);
		var opts = [[t('In 20 minutes'), new Date(now.getTime() + 20 * 60000)], [t('In an hour'), new Date(now.getTime() + 3600000)], [t('In 3 hours'), new Date(now.getTime() + 3 * 3600000)], [t('Tomorrow at 9'), tom]];
		swal({ title: t('Remind me'), html: '<div class="d-grid gap-2">' + opts.map(function (o, i) { return '<button type="button" class="cm-pill light justify-content-center" data-ri="' + i + '">' + esc(o[0]) + '</button>'; }).join('') +
			'<div class="text-muted fs-8 mt-2">' + esc(t('or a time of your choosing')) + '</div><input id="remAt" type="datetime-local" class="form-control form-control-solid" value="' + localInput(new Date(now.getTime() + 3600000)) + '" /></div>',
			confirmButtonText: t('Set the reminder'), didOpen: function () {
				document.querySelectorAll('[data-ri]').forEach(function (b) { b.onclick = function () { $('remAt').value = localInput(opts[+b.getAttribute('data-ri')][1]); Swal.clickConfirm(); }; });
			}, preConfirm: function () { var v = $('remAt').value; if (!v) return false; return v; } }).then(function (r) {
			if (!r.isConfirmed) return;
			post('/Chat/Remind', { id: id, at: iso(new Date(r.value)) }).then(function (x) { if (x && x.ok) { toast('success', t('I will remind you')); loadMeta([id]); } else toast('error', t('Choose a time in the future')); });
		});
	}
	function makeTask(id, presetTitle) {
		var m = C.msg(id) || {}, dir = C.dir();
		var title = presetTitle || (m.body || m.attachmentName || '').split('\n')[0].slice(0, 150);
		var html = '<div class="text-start d-grid gap-3"><div><label class="fs-7 fw-semibold mb-1">' + esc(t('Title')) + '</label><input id="tkT" class="form-control form-control-solid" value="' + esc(title) + '" /></div>' +
			'<div><label class="fs-7 fw-semibold mb-1">' + esc(t('Assign to')) + '</label><select id="tkA" class="form-select form-select-solid"><option value="' + meId + '">' + esc(t('Me')) + '</option>' + dir.map(function (d) { return '<option value="' + d.id + '">' + esc(d.name) + '</option>'; }).join('') + '</select></div>' +
			'<div class="d-flex gap-3"><div class="flex-grow-1"><label class="fs-7 fw-semibold mb-1">' + esc(t('Due')) + '</label><input id="tkD" type="date" class="form-control form-control-solid" /></div>' +
			'<div class="flex-grow-1"><label class="fs-7 fw-semibold mb-1">' + esc(t('Priority')) + '</label><select id="tkP" class="form-select form-select-solid"><option value="Normal">' + esc(t('Normal')) + '</option><option value="High">' + esc(t('High')) + '</option><option value="Urgent">' + esc(t('Urgent')) + '</option><option value="Low">' + esc(t('Low')) + '</option></select></div></div></div>';
		swal({ title: t('Make a task'), html: html, confirmButtonText: t('Create the task'), preConfirm: function () { if (!$('tkT').value.trim()) return false; return { title: $('tkT').value, assignee: $('tkA').value, due: $('tkD').value, priority: $('tkP').value }; } }).then(function (r) {
			if (!r.isConfirmed) return;
			post('/Chat/MakeTask', { id: id, title: r.value.title, assignee: r.value.assignee, due: r.value.due ? iso(new Date(r.value.due + 'T12:00:00')) : '', priority: r.value.priority }).then(function (x) {
				if (x && x.ok) toast('success', t('The task is created and linked to the message'));
				else toast('error', x && x.error === 'forbidden' ? t('You may not create tasks') : x && x.error === 'assign' ? t('You may not assign tasks to others') : t('The task could not be created'));
			});
		});
	}

	/* ================================================================================================================
	   THE PLUS — a file, a picture, a poll, a record of the system, a scheduled message, a meeting
	   ================================================================================================================ */
	C.on('plus', function (anchor) {
		var rows = [['paperclip', t('File'), 'file'], ['image', t('Picture'), 'image'], ['video', t('Video message'), 'video'], ['chart-bar-big', t('Poll'), 'poll'], ['link-2', t('A record of the system'), 'record'], ['clock', t('Schedule a message'), 'schedule']];
		if (C.header() && C.header().kind === 'Group') rows.push(['calendar-plus', t('Schedule a meeting'), 'meeting']);
		C.pop(anchor, rows.map(function (r) { return '<button type="button" class="cm-item w-100 border-0 bg-transparent" data-more="' + r[2] + '" style="padding:8px 12px">' + ico(r[0], 16) + '<span class="fs-7 fw-semibold">' + esc(r[1]) + '</span></button>'; }).join(''), function (k) {
			if (k === 'file') C.pickFile(''); if (k === 'image') C.pickFile('image/*');
			if (k === 'video') videoDialog();
			if (k === 'poll') pollDialog(); if (k === 'record') recordDialog(); if (k === 'schedule') scheduleDialog(); if (k === 'meeting') meetingDialog();
		});
	});

	/* ---------------- a video message: record, watch it, then send it (or record again) ---------------- */
	function videoDialog() {
		var c = C.cur(); if (!c) return;
		if (!navigator.mediaDevices || !window.MediaRecorder) { toast('error', t('Video messages are not supported in this browser')); return; }
		var stream = null, rec = null, chunks = [], blob = null, url = null, started = 0, timer = null, MAX = 180;
		var html = '<div class="cm-vrec text-start"><video class="lv" autoplay muted playsinline></video><video class="pb d-none" controls playsinline></video>' +
			'<div class="bar"><span class="dot"></span><span class="num" data-vt>0:00</span><span class="text-muted fs-8">/ 3:00</span>' +
			'<span class="ms-auto d-flex gap-2"><button type="button" class="btn btn-sm btn-danger" data-vr="rec">' + ico('circle', 13) + ' ' + esc(t('Record')) + '</button>' +
			'<button type="button" class="btn btn-sm btn-light d-none" data-vr="stop">' + ico('square', 13) + ' ' + esc(t('Stop')) + '</button>' +
			'<button type="button" class="btn btn-sm btn-light d-none" data-vr="again">' + ico('rotate-ccw', 13) + ' ' + esc(t('Record again')) + '</button></span></div></div>';
		swal({ title: t('Video message'), html: html, confirmButtonText: t('Send'), width: 560,
			didOpen: function (pop) {
				Swal.getConfirmButton().disabled = true;
				var A = window.cbDevices ? window.cbDevices.audio() : true, V = window.cbDevices ? window.cbDevices.video({ height: { ideal: 480 } }) : { height: { ideal: 480 } };
				navigator.mediaDevices.getUserMedia({ audio: A, video: V }).then(function (s) { stream = s; pop.querySelector('.lv').srcObject = s; })
					.catch(function () { toast('error', t('The camera could not be opened')); Swal.close(); });
				pop.addEventListener('click', function (e) {
					var b = e.target.closest('[data-vr]'); if (!b) return;
					var k = b.getAttribute('data-vr');
					if (k === 'rec' || k === 'again') begin(pop); else if (k === 'stop') end(pop);
				});
			},
			willClose: function () { clearInterval(timer); if (rec && rec.state !== 'inactive') try { rec.stop(); } catch (e) { } if (stream) stream.getTracks().forEach(function (x) { x.stop(); }); if (url) URL.revokeObjectURL(url); },
			preConfirm: function () { return !!blob; }
		}).then(function (r) {
			if (!r.isConfirmed || !blob) return;
			var type = (blob.type || 'video/webm').split(';')[0], ext = type.indexOf('mp4') >= 0 ? 'mp4' : 'webm';
			var fd = new FormData(); fd.append('c', c); fd.append('file', new File([blob], 'video-' + Date.now() + '.' + ext, { type: type }));
			toast('info', t('Sending the video…'));
			fetch('/Chat/SendVideo', { method: 'POST', credentials: 'same-origin', headers: { RequestVerificationToken: document.querySelector('input[name="__RequestVerificationToken"]').value }, body: fd })
				.then(function (x) { if (!x.ok) toast('error', t('The video was not sent')); }).catch(function () { toast('error', t('The video was not sent')); });
		});
		function begin(pop) {
			if (!stream) return;
			blob = null; chunks = []; if (url) { URL.revokeObjectURL(url); url = null; }
			pop.querySelector('.pb').classList.add('d-none'); pop.querySelector('.lv').classList.remove('d-none');
			var mime = ['video/webm;codecs=vp9,opus', 'video/webm;codecs=vp8,opus', 'video/webm', 'video/mp4'].find(function (x) { return MediaRecorder.isTypeSupported && MediaRecorder.isTypeSupported(x); }) || '';
			rec = new MediaRecorder(stream, mime ? { mimeType: mime, videoBitsPerSecond: 900000 } : undefined);
			rec.ondataavailable = function (ev) { if (ev.data && ev.data.size) chunks.push(ev.data); };
			rec.onstop = function () {
				blob = new Blob(chunks, { type: (rec.mimeType || 'video/webm').split(';')[0] }); url = URL.createObjectURL(blob);
				var pb = pop.querySelector('.pb'); pb.src = url; pb.classList.remove('d-none'); pop.querySelector('.lv').classList.add('d-none');
				Swal.getConfirmButton().disabled = false;
			};
			rec.start(1000); started = Date.now();
			pop.querySelector('.cm-vrec').classList.add('on');
			['rec', 'again'].forEach(function (k) { pop.querySelector('[data-vr="' + k + '"]').classList.add('d-none'); });
			pop.querySelector('[data-vr="stop"]').classList.remove('d-none');
			Swal.getConfirmButton().disabled = true;
			timer = setInterval(function () {
				var s = Math.round((Date.now() - started) / 1000); pop.querySelector('[data-vt]').textContent = C.dur(s);
				if (s >= MAX) end(pop);
			}, 500);
		}
		function end(pop) {
			clearInterval(timer);
			if (rec && rec.state !== 'inactive') rec.stop();
			pop.querySelector('.cm-vrec').classList.remove('on');
			pop.querySelector('[data-vr="stop"]').classList.add('d-none'); pop.querySelector('[data-vr="again"]').classList.remove('d-none');
		}
	}

	/* ---------------- polls ---------------- */
	function pollDialog(preset) {
		preset = preset || {};
		var html = '<div class="text-start d-grid gap-3"><div><label class="fs-7 fw-semibold mb-1">' + esc(t('Question')) + '</label><input id="poQ" class="form-control form-control-solid" maxlength="300" value="' + esc(preset.q || '') + '" /></div>' +
			'<div><label class="fs-7 fw-semibold mb-1">' + esc(t('Options — one per line (2 to 12)')) + '</label><textarea id="poO" class="form-control form-control-solid" rows="4">' + esc((preset.o || []).join('\n')) + '</textarea></div>' +
			'<label class="form-check form-check-custom form-check-solid"><input id="poM" class="form-check-input" type="checkbox" /><span class="form-check-label fs-7">' + esc(t('More than one answer')) + '</span></label>' +
			'<label class="form-check form-check-custom form-check-solid"><input id="poA" class="form-check-input" type="checkbox" /><span class="form-check-label fs-7">' + esc(t('Anonymous — nobody sees who voted for what')) + '</span></label>' +
			'<div><label class="fs-7 fw-semibold mb-1">' + esc(t('Closes (optional)')) + '</label><input id="poC" type="datetime-local" class="form-control form-control-solid" /></div></div>';
		swal({ title: t('Poll'), html: html, confirmButtonText: t('Send the poll'), preConfirm: function () {
			var opts = $('poO').value.split('\n').map(function (x) { return x.trim(); }).filter(Boolean);
			if (!$('poQ').value.trim() || opts.length < 2) { Swal.showValidationMessage(t('A question and at least two options')); return false; }
			return { question: $('poQ').value.trim(), options: opts.slice(0, 12).join('\n'), multi: $('poM').checked, anonymous: $('poA').checked, closesAt: $('poC').value ? iso(new Date($('poC').value)) : '' };
		} }).then(function (r) {
			if (!r.isConfirmed) return;
			var v = r.value; v.c = C.cur();
			post('/Chat/CreatePoll', v).then(function (m) { if (m && m.id) { C.appendMsg(m); loadMeta([m.id]); C.loadConvs(); } else toast('error', t('The poll could not be sent')); });
		});
	}
	function paintPoll(el, p) {
		var bub = el.querySelector('.cm-bubble'); if (!bub) return;
		var box = bub.querySelector('.cm-poll'); if (box) box.remove();
		var tx = bub.querySelector('.tx'); if (tx) tx.style.display = 'none';
		var max = Math.max.apply(null, p.options.map(function (o) { return o.votes; }).concat([0]));
		var total = p.options.reduce(function (n, o) { return n + o.votes; }, 0);
		var html = '<div class="cm-poll" data-poll="' + p.id + '"><div class="q">' + ico('chart-bar-big', 16) + '<b dir="auto">' + esc(p.question) + '</b></div>' +
			'<div class="sub">' + esc(p.multi ? t('Choose one or more') : t('Choose one')) + (p.anonymous ? ' · ' + esc(t('anonymous')) : '') + (p.closed ? ' · ' + esc(t('closed')) : p.closesAt ? ' · ' + esc(t('closes')) + ' ' + num(C.when(p.closesAt)) : '') + '</div>' +
			p.options.map(function (o) {
				var pct = total ? Math.round(o.votes * 100 / total) : 0;
				return '<button type="button" class="opt' + (o.mine ? ' mine' : '') + (o.votes === max && max > 0 ? ' top' : '') + '" data-vote="' + o.id + '"' + (p.closed ? ' disabled' : '') + ' title="' + esc((o.who || []).join('، ')) + '">' +
					'<span class="box">' + (o.mine ? ico(p.multi ? 'square-check' : 'circle-check', 16) : ico(p.multi ? 'square' : 'circle', 16)) + '</span><span class="lb" dir="auto">' + esc(o.text) + '</span><span class="nv">' + num(o.votes) + '</span>' +
					'<span class="bar" style="width:' + pct + '%"></span></button>';
			}).join('') + '<div class="sub">' + num(p.voters) + ' ' + esc(t('voted')) + '</div></div>';
		var mt = bub.querySelector('.cm-meta'); mt.insertAdjacentHTML('beforebegin', html);
	}
	function vote(b) {
		var el = b.closest('.cm-msg'), id = +el.getAttribute('data-id'), p = meta[id] && meta[id].poll; if (!p || p.closed) return;
		var oid = +b.getAttribute('data-vote'), chosen = p.options.filter(function (o) { return o.mine; }).map(function (o) { return o.id; });
		if (p.multi) { var i = chosen.indexOf(oid); if (i >= 0) chosen.splice(i, 1); else chosen.push(oid); } else chosen = chosen[0] === oid ? [] : [oid];
		post('/Chat/Vote', { id: id, options: chosen.join(',') }).then(function () { refreshPoll(id); });
	}
	function refreshPoll(id) { getJson('/Chat/Poll?id=' + id).then(function (p) { if (!p) return; meta[id] = meta[id] || { id: id }; meta[id].poll = p; var el = msgEl(id); if (el) paintPoll(el, p); }); }

	/* ---------------- a record of the system, as a card ---------------- */
	function recordDialog() {
		getJson('/Chat/RecordTypes').then(function (types) {
			types = types || [];
			var html = '<div class="text-start d-grid gap-3"><select id="rcT" class="form-select form-select-solid">' + types.map(function (x) { return '<option value="' + esc(x.code) + '">' + esc(x.name) + '</option>'; }).join('') + '</select>' +
				'<div class="cm-search">' + ico('search', 16) + '<input id="rcQ" placeholder="' + esc(t('Search by number or name')) + '" /></div><div id="rcR" class="scroll-y" style="max-height:300px"></div></div>';
			swal({ title: t('A record of the system'), html: html, showConfirmButton: false, didOpen: function () {
				var tm; function run() { clearTimeout(tm); tm = setTimeout(function () {
					getJson('/Chat/RecordSearch?code=' + encodeURIComponent($('rcT').value) + '&q=' + encodeURIComponent($('rcQ').value)).then(function (rows) {
						$('rcR').innerHTML = (rows || []).map(function (r) { return '<button type="button" class="cm-row w-100 border-0 bg-transparent text-start" data-rc="' + esc(r.code) + ':' + r.id + '" data-lb="' + esc(r.label) + '" data-url="' + esc(r.url || '') + '"><span class="cm-chip">' + ico('file-text', 14) + '</span><span class="t">' + esc(r.label) + '</span></button>'; }).join('') || '<div class="cm-empty py-4">' + esc(t('No results')) + '</div>';
						$('rcR').querySelectorAll('[data-rc]').forEach(function (b) { b.onclick = function () {
							Swal.close();
							C.sendCore('🔗 ' + b.getAttribute('data-lb') + (b.getAttribute('data-url') ? '\n' + location.origin + b.getAttribute('data-url') : '') + ' #rec:' + b.getAttribute('data-rc'), null);
						}; });
						paintIcons();
					});
				}, 250); }
				$('rcT').addEventListener('change', run); $('rcQ').addEventListener('input', run); run(); paintIcons();
			} });
		});
	}
	var cardCache = {};
	function recordCard(el, m) {
		var mt = /#rec:([A-Za-z]+):(\d+)\s*$/.exec(m.body || ''); if (!mt) return;
		var key = mt[1] + ':' + mt[2], tx = el.querySelector('.cm-bubble > .tx');
		var p = cardCache[key] || (cardCache[key] = getJson('/Chat/RecordCard?code=' + mt[1] + '&id=' + mt[2]));
		p.then(function (r) {
			if (!r) return;
			if (tx) tx.style.display = 'none';
			var html = '<a class="cm-rec-card" ' + (r.url ? 'href="' + esc(r.url) + '" target="_blank" rel="noopener"' : '') + '><span class="ic">' + ico('file-text', 18) + '</span><div class="min-w-0"><div class="ty">' + esc(r.type || '') + '</div><div class="lb">' + esc(r.found ? r.label : t('Not available')) + '</div></div>' + (r.url ? ico('external-link', 14) : '') + '</a>';
			var mtEl = el.querySelector('.cm-bubble .cm-meta'); if (mtEl && !el.querySelector('.cm-rec-card')) mtEl.insertAdjacentHTML('beforebegin', html);
		});
	}

	/* ---------------- scheduled messages ---------------- */
	function scheduleDialog(preset) {
		var d = new Date(Date.now() + 3600000);
		var html = '<div class="text-start d-grid gap-3"><textarea id="scB" class="form-control form-control-solid" rows="3" placeholder="' + esc(t('Write a message')) + '">' + esc(preset || E.input.value || '') + '</textarea>' +
			'<input id="scA" type="datetime-local" class="form-control form-control-solid" value="' + localInput(d) + '" /></div>';
		swal({ title: t('Schedule a message'), html: html, confirmButtonText: t('Schedule'), preConfirm: function () { if (!$('scB').value.trim() || !$('scA').value) return false; return { body: $('scB').value.trim(), sendAt: iso(new Date($('scA').value)) }; } }).then(function (r) {
			if (!r.isConfirmed) return;
			post('/Chat/Schedule', { c: C.cur(), body: r.value.body, sendAt: r.value.sendAt }).then(function (x) {
				if (x && x.ok) { toast('success', t('Scheduled')); if (E.input.value.trim() === r.value.body) { E.input.value = ''; C.autoGrow(); } loadScheduled(); }
				else toast('error', t('Choose a time at least a minute from now'));
			});
		});
	}
	function loadScheduled() {
		var c = C.cur(); if (!c) return;
		getJson('/Chat/Scheduled?c=' + c).then(function (rows) {
			if (C.cur() !== c) return;
			var bar = $('cmSchedBar'); rows = rows || [];
			if (!rows.length) { if (bar) bar.remove(); return; }
			if (!bar) { bar = document.createElement('div'); bar.id = 'cmSchedBar'; bar.className = 'cm-schedbar'; E.compose.insertAdjacentElement('afterbegin', bar); }
			bar.innerHTML = ico('clock', 14) + '<span>' + num(rows.length) + ' ' + esc(t('scheduled (count)')) + ' · ' + esc(t('next (scheduled)')) + ' ' + num(C.when(rows[0].sendAt) + ' ' + C.hm(C.utc(rows[0].sendAt))) + '</span><button type="button" class="cm-link border-0 bg-transparent p-0 ms-auto" data-sched>' + esc(t('Show')) + '</button>';
			bar.querySelector('[data-sched]').onclick = function () {
				swal({ title: t('Scheduled messages'), showConfirmButton: false, html: '<div class="text-start">' + rows.map(function (s) { return '<div class="cm-row"><span class="cm-chip">' + ico('clock', 14) + '</span><div class="min-w-0 flex-grow-1"><div class="t text-truncate" dir="auto">' + esc(s.body) + '</div><div class="s">' + num(C.when(s.sendAt) + ' ' + C.hm(C.utc(s.sendAt))) + '</div></div><button type="button" class="cm-ib xs ghost cm-red" data-cancel="' + s.id + '" title="' + esc(t('Cancel')) + '">' + ico('x', 14) + '</button></div>'; }).join('') + '</div>',
					didOpen: function () { paintIcons(); document.querySelectorAll('[data-cancel]').forEach(function (b) { b.onclick = function () { post('/Chat/CancelScheduled', { id: b.getAttribute('data-cancel') }).then(function () { Swal.close(); loadScheduled(); }); }; }); } });
			};
		});
	}

	/* ---------------- a meeting in the calendar ---------------- */
	function meetingDialog() {
		var d = new Date(Date.now() + 86400000); d.setMinutes(0, 0, 0);
		var html = '<div class="text-start d-grid gap-3"><input id="mtT" class="form-control form-control-solid" placeholder="' + esc(t('Title')) + '" value="' + esc(C.header() ? C.header().title : '') + '" />' +
			'<div class="d-flex gap-3"><input id="mtS" type="datetime-local" class="form-control form-control-solid" value="' + localInput(d) + '" /><select id="mtM" class="form-select form-select-solid" style="max-width:130px"><option value="15">15′</option><option value="30" selected>30′</option><option value="45">45′</option><option value="60">60′</option><option value="90">90′</option></select></div>' +
			'<textarea id="mtN" class="form-control form-control-solid" rows="2" placeholder="' + esc(t('Agenda (optional)')) + '"></textarea><div class="text-muted fs-8">' + esc(t('Every member of the group gets it in their calendar, with the way back to this conversation; at the time, start the group call from here')) + '</div></div>';
		swal({ title: t('Schedule a meeting'), html: html, confirmButtonText: t('Schedule'), preConfirm: function () { if (!$('mtT').value.trim() || !$('mtS').value) return false; return { title: $('mtT').value, start: iso(new Date($('mtS').value)), minutes: $('mtM').value, note: $('mtN').value }; } }).then(function (r) {
			if (!r.isConfirmed) return;
			var v = r.value; v.c = C.cur();
			post('/Chat/ScheduleMeeting', v).then(function (x) { if (x && x.ok) toast('success', t('The meeting is in everyone’s calendar')); else toast('error', t('The meeting could not be scheduled')); });
		});
	}

	/* ================================================================================================================
	   THE UNREAD DIVIDER, and a CATCH-UP of what was missed
	   ================================================================================================================ */
	var openedUnread = 0;
	C.on('open', function (id, m) { openedUnread = (m && m.unread) || 0; });
	function dividerAndCatchUp() {
		var old = E.thread.querySelector('.cm-unread'); if (old) old.remove();
		if (!openedUnread) return;
		var others = [].slice.call(E.thread.querySelectorAll('.cm-msg:not(.mine)'));
		var first = others[Math.max(0, others.length - openedUnread)]; if (!first) return;
		var d = document.createElement('div'); d.className = 'cm-unread'; d.innerHTML = '<span>' + num(openedUnread) + ' ' + esc(t('unread messages')) + '</span>';
		first.parentNode.insertBefore(d, first);
		d.scrollIntoView({ block: 'center' });
		if (openedUnread >= 6) {
			var prev = first.previousElementSibling; while (prev && !prev.classList.contains('cm-msg')) prev = prev.previousElementSibling;
			var since = prev ? +prev.getAttribute('data-id') : 0, c = C.cur();
			getJson('/Chat/CatchUp?c=' + c + '&since=' + since).then(function (r) {
				if (!r || C.cur() !== c || !r.total) return;
				function sec(icon, title, list) {
					return list && list.length ? '<div class="cs"><div class="ch">' + ico(icon, 14) + esc(title) + ' · ' + num(list.length) + '</div>' + list.map(function (x) { return '<button type="button" class="ci" data-catch="' + x.id + '" data-root="' + (x.threadRootId || '') + '"><b>' + esc(x.sender || '') + ':</b> <span dir="auto">' + esc(x.body || '') + '</span></button>'; }).join('') + '</div>' : '';
				}
				var html = '<div class="cm-catchup"><div class="hd">' + ico('sparkles', 16) + '<b>' + esc(t('What you missed')) + '</b><span>' + num(r.total) + ' ' + esc(t('messages from')) + ' ' + esc(r.people.map(function (p) { return p.name; }).slice(0, 3).join('، ')) + '</span></div>' +
					sec('at-sign', t('Mentions of you'), r.mentions) + sec('circle-help', t('Questions'), r.questions) + sec('circle-check', t('Decisions'), r.decisions) +
					sec('paperclip', t('Files'), r.files) + sec('chart-bar-big', t('Polls'), r.polls) + sec('link', t('Links'), r.links) + '</div>';
				d.insertAdjacentHTML('afterend', html);
			});
		}
		openedUnread = 0;
	}

	/* ================================================================================================================
	   WHO READ IT (a group): a tap on the tick of my message
	   ================================================================================================================ */
	E.thread.addEventListener('click', function (e) {
		var tk = e.target.closest('.cm-msg.mine .cm-tick'); if (!tk) return;
		var h = C.header(); if (!h || h.kind !== 'Group') return;
		e.stopPropagation();
		var id = +tk.closest('.cm-msg').getAttribute('data-id'), reads = C.reads().members || {};
		var read = [], not = [];
		C.members().forEach(function (m) { ((reads[m.id] || 0) >= id ? read : not).push(m); });
		C.pop(tk, '<div class="cm-readby"><div class="h">' + ico('check-check', 14) + esc(t('Read by')) + ' · ' + num(read.length) + '</div>' + read.map(function (m) { return '<div class="cm-row">' + av(m.name, m.avatar, 24) + '<span class="t">' + esc(m.name) + '</span></div>'; }).join('') +
			(not.length ? '<div class="h mt-2">' + ico('check', 14) + esc(t('Not yet')) + ' · ' + num(not.length) + '</div>' + not.map(function (m) { return '<div class="cm-row">' + av(m.name, m.avatar, 24) + '<span class="t text-muted">' + esc(m.name) + '</span></div>'; }).join('') : '') + '</div>', function () { });
	}, true);

	/* ================================================================================================================
	   STATUS and DO-NOT-DISTURB
	   ================================================================================================================ */
	var statuses = {};
	function stHtml(id) {
		var s = statuses[id]; if (!s) return '';
		return (s.dnd ? '<span class="cm-dnd" title="' + esc(t('Do not disturb')) + '">' + ico('bell-off', 12) + '</span>' : '') + (s.emoji || s.text ? '<span class="cm-stmark" title="' + esc(s.text || '') + '">' + esc(s.emoji || '💬') + '</span>' : '');
	}
	C.itemDecor.push(function (c) { return (c.kind === 'Direct' && c.otherEmployeeId ? stHtml(c.otherEmployeeId) : '') + (extras[c.id] && extras[c.id].isPublic ? '<span class="cm-hash" title="' + esc(t('Public channel')) + '">#</span>' : ''); });
	function loadStatuses() {
		var ids = C.convs().filter(function (c) { return c.otherEmployeeId; }).map(function (c) { return c.otherEmployeeId; }).concat(C.members().map(function (m) { return m.id; }));
		getJson('/Chat/Statuses?ids=' + ids.join(',')).then(function (map) { statuses = {}; for (var k in (map || {})) statuses[+k] = map[k]; paintMe(); repaintList(); paintHeadStatus(); });
	}
	function repaintList() { if (C.tab() === 'chats' && !document.activeElement.isSameNode($('cmSearch'))) C.renderConvs(); }
	C.on('head', function () { paintHeadStatus(); });
	function paintHeadStatus() {
		var h = C.header(); if (!h) return;
		var st = E.head.querySelector('.st'); if (!st) return;
		var old = st.querySelector('.cm-hst'); if (old) old.remove();
		var nm = E.head.querySelector('.nm'); var oh = nm && nm.querySelector('.cm-hash'); if (oh) oh.remove();
		if (h.kind === 'Direct' && statuses[h.other]) { var s = statuses[h.other]; st.insertAdjacentHTML('beforeend', '<span class="cm-hst"> · ' + (s.dnd ? esc(t('Do not disturb')) : '') + (s.emoji || s.text ? ' ' + esc((s.emoji || '') + ' ' + (s.text || '')) : '') + '</span>'); }
		if (h.kind === 'Group' && extras[C.cur()] && extras[C.cur()].isPublic && nm) nm.insertAdjacentHTML('afterbegin', '<span class="cm-hash">#</span>');
	}
	function paintMe() {
		var btn = $('cmMyStatus'); if (!btn) return;
		var s = statuses[meId];
		btn.innerHTML = s ? '<span class="fs-6">' + esc(s.emoji || (s.dnd ? '⛔' : '💬')) + '</span>' : ico('smile', 18);
		btn.title = s ? ((s.dnd ? t('Do not disturb') + ' · ' : '') + (s.text || '')) : t('Set a status');
	}
	function statusDialog() {
		var s = statuses[meId] || {};
		var presets = [['📅', t('In a meeting')], ['🚗', t('On the way')], ['🤒', t('Off sick')], ['🌴', t('On leave')], ['🏠', t('Working from home')], ['🎯', t('Focusing')]];
		var html = '<div class="text-start d-grid gap-3"><div class="d-flex gap-2"><input id="stE" class="form-control form-control-solid text-center" style="max-width:64px" maxlength="4" value="' + esc(s.emoji || '') + '" placeholder="🙂" /><input id="stT" class="form-control form-control-solid" maxlength="100" value="' + esc(s.text || '') + '" placeholder="' + esc(t('What is your status?')) + '" /></div>' +
			'<div class="d-flex flex-wrap gap-2">' + presets.map(function (p) { return '<button type="button" class="cm-pill light sm" data-pre="' + esc(p[0]) + '|' + esc(p[1]) + '">' + esc(p[0] + ' ' + p[1]) + '</button>'; }).join('') + '</div>' +
			'<div><label class="fs-7 fw-semibold mb-1">' + esc(t('Clear after')) + '</label><select id="stU" class="form-select form-select-solid"><option value="">' + esc(t('Never')) + '</option><option value="60">' + esc(t('An hour')) + '</option><option value="240">' + esc(t('4 hours')) + '</option><option value="today">' + esc(t('Today')) + '</option><option value="week">' + esc(t('This week')) + '</option></select></div>' +
			'<label class="form-check form-switch form-check-custom form-check-solid"><input id="stD" class="form-check-input" type="checkbox" ' + (s.dnd ? 'checked' : '') + ' /><span class="form-check-label fw-semibold">' + esc(t('Do not disturb — no ringing, no desktop notices')) + '</span></label>' +
			'<select id="stDU" class="form-select form-select-solid"><option value="">' + esc(t('Until I turn it off')) + '</option><option value="60">' + esc(t('An hour')) + '</option><option value="120">' + esc(t('2 hours')) + '</option><option value="today">' + esc(t('Until tomorrow')) + '</option></select></div>';
		function until(v) { if (!v) return ''; var d = new Date(); if (v === 'today') { d.setHours(23, 59, 0, 0); } else if (v === 'week') { d.setDate(d.getDate() + (7 - d.getDay())); d.setHours(23, 59, 0, 0); } else d = new Date(Date.now() + (+v) * 60000); return iso(d); }
		swal({ title: t('Your status'), html: html, confirmButtonText: t('Save'), showDenyButton: !!(s.text || s.dnd), denyButtonText: t('Clear'), customClass: { confirmButton: 'btn btn-primary', denyButton: 'btn btn-light-danger', cancelButton: 'btn btn-light', popup: 'cm-root' },
			didOpen: function () { document.querySelectorAll('[data-pre]').forEach(function (b) { b.onclick = function () { var p = b.getAttribute('data-pre').split('|'); $('stE').value = p[0]; $('stT').value = p[1]; }; }); },
			preConfirm: function () { return { emoji: $('stE').value, text: $('stT').value, until: until($('stU').value), dnd: $('stD').checked, dndUntil: until($('stDU').value) }; } }).then(function (r) {
			if (r.isDenied) return post('/Chat/SetStatus', { dnd: false }).then(loadStatuses);
			if (!r.isConfirmed) return;
			post('/Chat/SetStatus', r.value).then(loadStatuses);
		});
	}
	// the page may connect to the hub before this file loads: attach to the connection that is there, and to a later one
	function onHub(fn) { if (C.conn()) fn(C.conn()); C.on('hub', fn); }
	onHub(function (conn) {
		conn.on('status', function (e) { if (!e) return; if (e.status) statuses[e.employeeId] = e.status; else delete statuses[e.employeeId]; paintMe(); repaintList(); paintHeadStatus(); });
		conn.on('thread', function (e) { if (e && e.conversationId === C.cur()) addToThread(e.rootId, e.message); });
		conn.on('pinned', function (e) { if (e && e.conversationId === C.cur()) { loadPinned(); loadMeta([e.id]); } });
		conn.on('poll', function (e) { if (e && e.conversationId === C.cur()) refreshPoll(e.id); });
	});
	C.on('message', function (el, m) { if (m.attachmentType === 'poll') setTimeout(function () { if (!meta[m.id]) loadMeta([m.id]); }, 50); });

	/* ================================================================================================================
	   CHANNELS, a group's description and picture
	   ================================================================================================================ */
	var extras = {};
	function loadExtras() { getJson('/Chat/ConvExtras').then(function (l) { extras = {}; (l || []).forEach(function (x) { extras[x.id] = x; }); repaintList(); paintHeadStatus(); }); }
	function browseChannels() {
		swal({ title: t('Channels'), showConfirmButton: false, width: 560, html: '<div class="text-start"><div class="d-flex gap-2 mb-3"><div class="cm-search flex-grow-1">' + ico('search', 16) + '<input id="chQ" placeholder="' + esc(t('Find a channel')) + '" /></div><button type="button" class="cm-pill" id="chNew">' + ico('plus', 14) + esc(t('New channel')) + '</button></div><div id="chL" class="scroll-y" style="max-height:380px"></div></div>',
			didOpen: function () {
				function load() { getJson('/Chat/Channels?q=' + encodeURIComponent($('chQ').value || '')).then(function (rows) {
					$('chL').innerHTML = (rows || []).map(function (c) { return '<div class="cm-row"><span class="cm-grp" style="width:36px;height:36px;font-weight:700">#</span><div class="min-w-0 flex-grow-1"><div class="t">' + esc(c.title) + '</div><div class="s text-truncate">' + esc(c.description || '') + ' · ' + num(c.members) + ' ' + esc(t('members (count)')) + '</div></div>' +
						(c.joined ? '<button type="button" class="cm-pill light sm" data-open="' + c.id + '">' + esc(t('Open')) + '</button>' : '<button type="button" class="cm-pill sm" data-join="' + c.id + '">' + esc(t('Join')) + '</button>') + '</div>'; }).join('') || '<div class="cm-empty py-4">' + esc(t('No channels yet — create the first one')) + '</div>';
					$('chL').querySelectorAll('[data-join]').forEach(function (b) { b.onclick = function () { post('/Chat/JoinChannel', { c: b.getAttribute('data-join') }).then(function (x) { if (x && x.ok) { Swal.close(); C.loadConvs(function () { loadExtras(); C.open(+b.getAttribute('data-join')); }); } }); }; });
					$('chL').querySelectorAll('[data-open]').forEach(function (b) { b.onclick = function () { Swal.close(); C.open(+b.getAttribute('data-open')); }; });
				}); }
				$('chQ').addEventListener('input', load); load(); paintIcons();
				$('chNew').onclick = function () { Swal.close(); newChannel(); };
			} });
	}
	function newChannel() {
		swal({ title: t('New channel'), html: '<div class="text-start d-grid gap-3"><div class="input-group"><span class="input-group-text">#</span><input id="ncT" class="form-control form-control-solid" maxlength="100" placeholder="' + esc(t('e.g. sales-team')) + '" /></div><textarea id="ncD" class="form-control form-control-solid" rows="2" maxlength="400" placeholder="' + esc(t('What is it for? (optional)')) + '"></textarea><div class="text-muted fs-8">' + esc(t('A channel is public: anyone in the company can find it and join')) + '</div></div>',
			confirmButtonText: t('Create'), preConfirm: function () { if (!$('ncT').value.trim()) return false; return { title: $('ncT').value, description: $('ncD').value }; } }).then(function (r) {
			if (!r.isConfirmed) return;
			post('/Chat/CreateChannel', r.value).then(function (x) { if (x && x.ok) C.loadConvs(function () { loadExtras(); C.open(x.id); }); else toast('error', t('The channel could not be created')); });
		});
	}
	C.on('info', function (box, h) {
		if (!h || threadRoot != null) return;
		if (h.kind !== 'Group') return;
		var c = C.cur();
		getJson('/Chat/GroupInfo?c=' + c).then(function (g) {
			if (!g || C.cur() !== c || threadRoot != null) return;
			var dup = box.querySelector('.cm-ginfo'); if (dup) dup.remove();   // two paints in a row answer twice: one card
			var first = box.querySelector('.sect'); if (!first) return;
			var top = '<div class="sect text-center cm-ginfo">' + (g.avatar ? '<img class="cm-av" src="' + esc(g.avatar) + '" style="width:76px;height:76px" alt="" />' : '<div class="cm-grp mx-auto" style="width:76px;height:76px">' + (g.isPublic ? '<b class="fs-1">#</b>' : ico('users-round', 32)) + '</div>') +
				'<div class="fs-5 fw-bold text-gray-900 mt-3">' + (g.isPublic ? '#' : '') + esc(h.title) + '</div>' + (g.description ? '<div class="text-muted fs-7 mt-1" dir="auto">' + esc(g.description) + '</div>' : '') +
				'<div class="fs-8 text-muted mt-1">' + esc(g.isPublic ? t('Public channel') : t('Private group')) + '</div>' +
				'<div class="d-flex gap-2 justify-content-center flex-wrap mt-3">' + (h.isOwner ? '<button type="button" class="cm-pill light sm" data-gedit>' + ico('pencil', 13) + esc(t('Edit')) + '</button>' +
					'<button type="button" class="cm-pill light sm" data-ghooks>' + ico('webhook', 13) + esc(t('Webhooks')) + '</button>' : '') +
				'<button type="button" class="cm-pill light sm" data-gmeet>' + ico('calendar-plus', 13) + esc(t('Meeting')) + '</button></div></div>';
			first.insertAdjacentHTML('beforebegin', top);
			var ed = box.querySelector('[data-gedit]'); if (ed) ed.onclick = function () { editGroup(g, h); };
			var hk = box.querySelector('[data-ghooks]'); if (hk) hk.onclick = function () { hooksDialog(h); };
			box.querySelector('[data-gmeet]').onclick = meetingDialog;
		});
	});
	/* ---------------- incoming webhooks (the group admin): another system posts into this conversation ---------------- */
	function hooksDialog(h) {
		var c = C.cur(); if (!c) return;
		var html = '<div class="cm-hooks text-start">' +
			'<div class="nt">' + esc(t('Another system (monitoring, a form, a script) can post messages here through a secret address. Messages go in your name, marked with the webhook\'s name.')) + '</div>' +
			'<div class="d-flex gap-2 mt-4"><input id="hkN" class="form-control form-control-solid" maxlength="60" placeholder="' + esc(t('Webhook name, e.g. Server alerts')) + '" />' +
			'<button type="button" class="btn btn-primary text-nowrap" data-hk="add">' + ico('plus', 14) + ' ' + esc(t('Add')) + '</button></div>' +
			'<div id="hkNew"></div><div id="hkL" class="mt-4"></div></div>';
		swal({ title: t('Webhooks') + ' · ' + h.title, html: html, showConfirmButton: false, cancelButtonText: t('Close'), width: 640 });
		var pop = document.querySelector('.swal2-popup');
		function load() {
			getJson('/Chat/Webhooks?c=' + c).then(function (rows) {
				var l = pop.querySelector('#hkL'); if (!l) return;
				if (!rows || !rows.length) { l.innerHTML = '<div class="cm-empty-sm">' + esc(t('No webhooks yet')) + '</div>'; return; }
				l.innerHTML = rows.map(function (r) {
					return '<div class="cm-hook' + (r.revoked ? ' off' : '') + '"><span class="ic">' + ico('webhook', 16) + '</span><div class="flex-grow-1 min-w-0">' +
						'<div class="nm" dir="auto">' + esc(r.name) + (r.revoked ? ' <span class="tag">' + esc(t('Revoked')) + '</span>' : '') + '</div>' +
						'<div class="sub">' + esc(t('Made by')) + ' ' + esc(r.by) + ' · ' + num(C.when(r.createdAt)) + ' · ' + esc(t('Messages')) + ': ' + num(r.uses) +
						(r.lastUsedAt ? ' · ' + esc(t('last')) + ' ' + num(C.when(r.lastUsedAt)) : '') + '</div></div>' +
						(r.revoked ? '' : '<button type="button" class="btn btn-sm btn-light-danger text-nowrap" data-hk-revoke="' + r.id + '">' + esc(t('Revoke')) + '</button>') + '</div>';
				}).join('');
			});
		}
		load();
		pop.addEventListener('click', function (e) {
			var a = e.target.closest('[data-hk="add"]');
			if (a) {
				var n = (pop.querySelector('#hkN').value || '').trim(); if (!n) { pop.querySelector('#hkN').focus(); return; }
				a.disabled = true;
				post('/Chat/CreateWebhook', { c: c, name: n }).then(function (x) {
					a.disabled = false;
					if (!x || !x.ok) { toast('error', t('The webhook could not be made — only the group admin can, and at most 10 at a time')); return; }
					pop.querySelector('#hkN').value = '';
					var ex = 'curl -X POST "' + x.url + '" -H "Content-Type: application/json" -d "{\\"text\\": \\"Hello\\"}"';
					pop.querySelector('#hkNew').innerHTML = '<div class="cm-hooknew"><div class="lb">' + ico('key-round', 13) + esc(t('Copy the address now — it will not be shown again')) + '</div>' +
						'<div class="d-flex gap-2 align-items-center"><code dir="ltr" class="flex-grow-1">' + esc(x.url) + '</code><button type="button" class="btn btn-sm btn-light-primary text-nowrap" data-hk-copy="' + esc(x.url) + '">' + ico('copy', 13) + ' ' + esc(t('Copy')) + '</button></div>' +
						'<div class="lb mt-3">' + esc(t('Example')) + '</div><code dir="ltr" class="d-block">' + esc(ex) + '</code></div>';
					load();
				});
				return;
			}
			var cp = e.target.closest('[data-hk-copy]');
			if (cp) { try { navigator.clipboard.writeText(cp.getAttribute('data-hk-copy')); toast('success', t('Copied')); } catch (x) { } return; }
			var rv = e.target.closest('[data-hk-revoke]');
			if (rv) {
				if (rv.getAttribute('data-sure') !== '1') { rv.setAttribute('data-sure', '1'); rv.textContent = t('Revoke — sure?'); return; }   // a second click confirms (no browser confirm())
				post('/Chat/RevokeWebhook', { c: c, id: +rv.getAttribute('data-hk-revoke') }).then(function (x) { if (x && x.ok) { toast('success', t('Revoked')); load(); } });
			}
		});
	}
	function editGroup(g, h) {
		var html = '<div class="text-start d-grid gap-3"><input id="geT" class="form-control form-control-solid" maxlength="100" value="' + esc(h.title) + '" /><textarea id="geD" class="form-control form-control-solid" rows="3" maxlength="400" placeholder="' + esc(t('Description')) + '">' + esc(g.description || '') + '</textarea>' +
			'<div><label class="fs-7 fw-semibold mb-1">' + esc(t('Picture')) + '</label><input id="geP" type="file" accept="image/*" class="form-control form-control-solid" />' + (g.avatar ? '<label class="form-check form-check-custom form-check-solid mt-2"><input id="geR" class="form-check-input" type="checkbox" /><span class="form-check-label fs-7">' + esc(t('Remove the picture')) + '</span></label>' : '') + '</div>' +
			'<label class="form-check form-switch form-check-custom form-check-solid"><input id="geO" class="form-check-input" type="checkbox" ' + (g.isPublic ? 'checked' : '') + ' /><span class="form-check-label fw-semibold">' + esc(t('Public channel — anyone in the company can find and join it')) + '</span></label></div>';
		swal({ title: t('Edit the group'), html: html, confirmButtonText: t('Save'), preConfirm: function () {
			var fd = new FormData(); fd.append('c', C.cur()); fd.append('title', $('geT').value); fd.append('description', $('geD').value); fd.append('isPublic', $('geO').checked);
			var f = $('geP').files[0]; if (f) fd.append('picture', f, f.name); if ($('geR') && $('geR').checked) fd.append('removePicture', true);
			return fd;
		} }).then(function (r) {
			if (!r.isConfirmed) return;
			post('/Chat/UpdateGroup', r.value).then(function (x) { if (x && x.ok) { toast('success', t('Saved')); C.loadConvs(function () { loadExtras(); C.open(C.cur()); }); } else toast('error', t('Only the group admin can change it, and a picture must be an image under 5 MB')); });
		});
	}

	/* ================================================================================================================
	   SEARCH across every conversation (in the list), with has:file / has:image / has:voice / has:link
	   ================================================================================================================ */
	var findT = null, lastFind = '';
	C.on('list', function (box, s) {
		if (C.tab() !== 'chats') return;
		if (!s || s.length < 2) { approvalsItem(box); return; }
		clearTimeout(findT);
		var q = s, has = null, m = /\bhas:(file|image|voice|link)\b/.exec(q); if (m) { has = m[1]; q = q.replace(m[0], '').trim(); }
		findT = setTimeout(function () {
			lastFind = s;
			getJson('/Chat/Find?q=' + encodeURIComponent(q) + (has ? '&has=' + has : '')).then(function (rows) {
				if (lastFind !== s || C.tab() !== 'chats') return;
				var old = box.querySelector('.cm-findsec'); if (old) old.remove();
				var hl = function (txt) { var e = esc(txt || ''); if (!q) return e; var i = (txt || '').toLowerCase().indexOf(q.toLowerCase()); return i < 0 ? e : esc(txt.slice(0, i)) + '<mark>' + esc(txt.slice(i, i + q.length)) + '</mark>' + esc(txt.slice(i + q.length)); };
				var html = '<div class="cm-findsec"><div class="cm-sec">' + esc(t('Messages')) + ' · ' + num((rows || []).length) + '</div>' + (rows || []).map(function (r) {
					return '<div class="cm-item" role="button" tabindex="0" data-hit="' + r.id + '" data-c="' + r.conversationId + '" data-root="' + (r.threadRootId || '') + '">' + av(r.sender, r.avatar, 36) +
						'<div class="cm-item-body"><div class="cm-line1"><span class="nm">' + esc(r.conversation || '') + '</span><span class="when num">' + esc(C.when(r.at)) + '</span></div><div class="cm-line2"><span class="last"><span class="tx"><b>' + esc(r.sender || '') + ':</b> ' + hl(r.body) + '</span></span></div></div></div>';
				}).join('') + ((rows || []).length ? '' : '<div class="cm-empty py-3">' + esc(t('No message matches')) + '</div>') + '</div>';
				box.insertAdjacentHTML('beforeend', html);
				box.querySelectorAll('[data-hit]').forEach(function (it) { it.onclick = function (e) { e.stopPropagation(); goHit(+it.getAttribute('data-c'), +it.getAttribute('data-hit'), +(it.getAttribute('data-root') || 0)); }; });
			});
		}, 300);
	});
	var pendingGoto = null;
	function goHit(c, id, root) {
		pendingGoto = { c: c, id: id, root: root };
		if (C.cur() === c) { applyGoto(); return; }
		C.open(c);
	}
	C.on('ready', function () { applyGoto(); });
	function applyGoto() {
		var g = pendingGoto; if (!g || g.c !== C.cur()) return;
		pendingGoto = null;
		if (g.root) { openThread(g.root); return; }
		var el = msgEl(g.id);
		if (el) C.gotoMsg(g.id); else toast('info', t('That message is further up — scroll up to load it'));
	}
	function deepLink() {
		var c = +(Q.get('c') || 0); if (!c || c !== C.cur()) return;
		var th = +(Q.get('thread') || 0), m = +(Q.get('m') || 0);
		if (th) openThread(th); else if (m) { pendingGoto = { c: c, id: m, root: 0 }; applyGoto(); }
		Q.delete('thread'); Q.delete('m');
	}

	/* ================================================================================================================
	   APPROVALS waiting for me — the top of the chats list, opening in the conversation pane
	   ================================================================================================================ */
	var approvals = null;
	function loadApprovals() { getJson('/Chat/Approvals').then(function (r) { approvals = r || { total: 0, rows: [] }; repaintList(); }); }
	function approvalsItem(box) {
		if (!approvals || !approvals.total || C.tab() !== 'chats') return;
		if (box.querySelector('[data-approvals]')) return;
		box.insertAdjacentHTML('afterbegin', '<div class="cm-item cm-appr" role="button" tabindex="0" data-approvals="1"><div class="cm-grp" style="width:44px;height:44px">' + ico('stamp', 18) + '</div><div class="cm-item-body"><div class="cm-line1"><span class="nm">' + esc(t('Approvals waiting for you')) + '</span></div><div class="cm-line2"><span class="last"><span class="tx">' + esc(approvals.rows[0] ? approvals.rows[0].title || '' : '') + '</span></span><span class="unread num">' + approvals.total + '</span></div></div></div>');
		box.querySelector('[data-approvals]').onclick = function (e) { e.stopPropagation(); showApprovals(); };
	}
	function showApprovals() {
		C.showBlank(); E.blank.classList.add('d-none'); E.head.classList.remove('d-none'); E.thread.classList.remove('d-none');
		E.head.innerHTML = '<div class="cm-grp" style="width:42px;height:42px">' + ico('stamp', 18) + '</div><div class="min-w-0"><div class="nm">' + esc(t('Approvals waiting for you')) + '</div><div class="st text-muted fs-8">' + esc(t('Each opens on its own screen, where it is decided with that module’s rules')) + '</div></div>';
		E.thread.innerHTML = (approvals.rows || []).map(function (r) {
			var facts = [r.requester, r.days ? r.days + ' ' + t('days') : null, r.amount != null ? Number(r.amount).toLocaleString() : null, r.at ? C.when(r.at) : null].filter(Boolean).join(' · ');
			return '<div class="cm-call cm-apcard"><span class="ic">' + ico('stamp', 16) + '</span><div class="cm-call-tx min-w-0"><div class="t" dir="auto">' + esc(r.title || '') + '</div><div class="s">' + esc(facts) + '</div></div><div class="go"><a class="cm-pill" href="' + esc(r.url || '#') + '">' + esc(t('Open and decide')) + '</a></div></div>';
		}).join('') || '<div class="cm-empty">' + esc(t('Nothing waits for you')) + '</div>';
		if (C.isPhone()) C.setPane('thread');
	}

	/* ================================================================================================================
	   SAVED MESSAGES and REMINDERS — a view of their own
	   ================================================================================================================ */
	function showSaved() {
		getJson('/Chat/Saved').then(function (r) {
			r = r || { saved: [], reminders: [] };
			C.showBlank(); E.blank.classList.add('d-none'); E.head.classList.remove('d-none'); E.thread.classList.remove('d-none');
			E.head.innerHTML = '<div class="cm-grp" style="width:42px;height:42px">' + ico('bookmark', 18) + '</div><div class="min-w-0"><div class="nm">' + esc(t('Saved and reminders')) + '</div><div class="st text-muted fs-8">' + esc(t('Only you see this')) + '</div></div>';
			function row(x, rem) {
				return '<div class="cm-call cm-saved" data-go="' + x.id + '" data-c="' + x.conversationId + '" data-root="' + (x.threadRootId || '') + '"><span class="ic">' + ico(rem ? 'alarm-clock' : 'bookmark', 16) + '</span><div class="cm-call-tx min-w-0"><div class="t text-truncate" dir="auto">' + esc(x.body || t('Message deleted')) + '</div>' +
					'<div class="s">' + esc((x.sender || '') + ' · ' + (x.conversation || '')) + ' · ' + num(rem ? C.when(x.remindAt) + ' ' + C.hm(C.utc(x.remindAt)) : C.when(x.at)) + '</div></div>' +
					'<div class="go">' + (rem ? '<button type="button" class="cm-pill light sm" data-done="' + x.reminderId + '">' + ico('check', 13) + esc(t('Done')) + '</button>' : '<button type="button" class="cm-ib xs ghost" data-unsave="' + x.id + '" title="' + esc(t('Remove from saved')) + '">' + ico('bookmark-x', 14) + '</button>') + '</div></div>';
			}
			E.thread.innerHTML = '<div class="cm-sec px-0">' + esc(t('Reminders')) + ' · ' + num(r.reminders.length) + '</div>' + (r.reminders.map(function (x) { return row(x, true); }).join('') || '<div class="cm-empty py-3">' + esc(t('No reminders')) + '</div>') +
				'<div class="cm-sec px-0 mt-4">' + esc(t('Saved messages')) + ' · ' + num(r.saved.length) + '</div>' + (r.saved.map(function (x) { return row(x, false); }).join('') || '<div class="cm-empty py-3">' + esc(t('Nothing saved yet — “Save” in a message’s menu')) + '</div>');
			E.thread.querySelectorAll('[data-go]').forEach(function (el) { el.onclick = function (e) { if (e.target.closest('button')) return; goHit(+el.getAttribute('data-c'), +el.getAttribute('data-go'), +(el.getAttribute('data-root') || 0)); }; });
			E.thread.querySelectorAll('[data-done]').forEach(function (b) { b.onclick = function () { post('/Chat/ReminderDone', { id: b.getAttribute('data-done') }).then(showSaved); }; });
			E.thread.querySelectorAll('[data-unsave]').forEach(function (b) { b.onclick = function () { post('/Chat/SaveMessage', { id: b.getAttribute('data-unsave'), on: false }).then(showSaved); }; });
			if (C.isPhone()) C.setPane('thread');
		});
	}

	/* ================================================================================================================
	   THE LIST'S HEAD: status, saved, channels
	   ================================================================================================================ */
	(function () {
		var bar = document.querySelector('.cm-title .ms-auto'); if (!bar) return;
		bar.insertAdjacentHTML('afterbegin', '<button type="button" class="cm-ib ghost" id="cmMyStatus" title="' + esc(t('Set a status')) + '">' + ico('smile', 18) + '</button>' +
			'<button type="button" class="cm-ib ghost" id="cmSavedBtn" title="' + esc(t('Saved and reminders')) + '">' + ico('bookmark', 18) + '</button>' +
			'<button type="button" class="cm-ib ghost" id="cmChannelsBtn" title="' + esc(t('Channels')) + '"><b style="font-size:17px;line-height:1">#</b></button>');
		$('cmMyStatus').onclick = statusDialog; $('cmSavedBtn').onclick = showSaved; $('cmChannelsBtn').onclick = browseChannels;
	})();

	/* ================================================================================================================
	   SLASH COMMANDS and THE SENSITIVE-DATA RULE — both run before a message is sent
	   ================================================================================================================ */
	var CMDS = [
		['/poll', t('/poll Question | option | option — a poll')],
		['/task', t('/task title — a task from the last message')],
		['/schedule', t('/schedule 14:30 text — send it later today (or tomorrow)')],
		['/status', t('/status text — set your status')],
		['/dnd', t('/dnd 60 — do not disturb for 60 minutes (/dnd off)')],
		['/find', t('/find words — search every conversation')],
		['/channel', t('/channel name — a new public channel')],
		['/meeting', t('/meeting — schedule a meeting with this group')],
	];
	C.sendFilters.push(function (body) {
		if (body.charAt(0) !== '/') return body;
		var m = /^\/(\w+)\s*([\s\S]*)$/.exec(body); if (!m) return body;
		var cmd = m[1].toLowerCase(), arg = m[2].trim();
		function clear() { E.input.value = ''; C.autoGrow(); }
		if (cmd === 'poll') { var parts = arg.split('|').map(function (x) { return x.trim(); }).filter(Boolean); clear(); pollDialog({ q: parts[0] || '', o: parts.slice(1) }); return null; }
		if (cmd === 'task') { var last = [].slice.call(E.thread.querySelectorAll('.cm-msg')).pop(); if (!last) { toast('info', t('No message to make a task from')); return null; } clear(); makeTask(+last.getAttribute('data-id'), arg); return null; }
		if (cmd === 'schedule') {
			var sm = /^(\d{1,2}):(\d{2})\s+([\s\S]+)$/.exec(arg); if (!sm) { toast('info', t('/schedule 14:30 text')); return null; }
			var d = new Date(); d.setHours(+sm[1], +sm[2], 0, 0); if (d < new Date()) d.setDate(d.getDate() + 1);
			post('/Chat/Schedule', { c: C.cur(), body: sm[3], sendAt: iso(d) }).then(function (x) { if (x && x.ok) { toast('success', t('Scheduled') + ' · ' + C.when(iso(d)) + ' ' + C.hm(d)); loadScheduled(); } });
			clear(); return null;
		}
		if (cmd === 'status') { post('/Chat/SetStatus', { text: arg, emoji: '💬', dnd: !!(statuses[meId] && statuses[meId].dnd) }).then(loadStatuses); clear(); toast('success', t('Status set')); return null; }
		if (cmd === 'dnd') {
			var off = /^off|إيقاف|لا$/i.test(arg), mins = +arg || 0, s = statuses[meId] || {};
			post('/Chat/SetStatus', { text: s.text || '', emoji: s.emoji || '', dnd: !off, dndUntil: mins ? iso(new Date(Date.now() + mins * 60000)) : '' }).then(loadStatuses);
			clear(); toast('success', off ? t('Do not disturb is off') : t('Do not disturb is on')); return null;
		}
		if (cmd === 'find') { clear(); var sb = $('cmSearch'); sb.value = arg; C.setTab('chats'); sb.dispatchEvent(new Event('input')); sb.focus(); return null; }
		if (cmd === 'channel') { clear(); post('/Chat/CreateChannel', { title: arg }).then(function (x) { if (x && x.ok) C.loadConvs(function () { loadExtras(); C.open(x.id); }); }); return null; }
		if (cmd === 'meeting') { clear(); if (C.header() && C.header().kind === 'Group') meetingDialog(); else toast('info', t('A meeting is scheduled from a group')); return null; }
		if (cmd === 'help') { clear(); swal({ title: t('Commands'), showConfirmButton: false, html: '<div class="text-start d-grid gap-2">' + CMDS.map(function (c) { return '<div class="fs-7"><code class="cm-code">' + esc(c[0]) + '</code> ' + esc(c[1].replace(/^\/\w+\s*/, '')) + '</div>'; }).join('') + '</div>' }); return null; }
		return body;
	});
	// the commands as you type "/"
	var slashBox = document.createElement('div'); slashBox.className = 'cm-pop cm-mention d-none'; E.compose.appendChild(slashBox);
	E.input.addEventListener('input', function () {
		var v = E.input.value;
		if (!/^\/\w*$/.test(v)) { slashBox.classList.add('d-none'); return; }
		var list = CMDS.filter(function (c) { return c[0].indexOf(v.toLowerCase()) === 0; });
		if (!list.length) { slashBox.classList.add('d-none'); return; }
		slashBox.innerHTML = list.map(function (c) { return '<div class="cm-item" data-cmd="' + c[0] + '"><code class="cm-code">' + esc(c[0]) + '</code><span class="fs-8 text-muted">' + esc(c[1].replace(/^\/\w+\s*/, '')) + '</span></div>'; }).join('');
		slashBox.querySelectorAll('[data-cmd]').forEach(function (it) { it.onmousedown = function (e) { e.preventDefault(); E.input.value = it.getAttribute('data-cmd') + ' '; slashBox.classList.add('d-none'); E.input.focus(); }; });
		slashBox.classList.remove('d-none');
	});
	E.input.addEventListener('blur', function () { setTimeout(function () { slashBox.classList.add('d-none'); }, 200); });

	C.sendFilters.push(function (body) {
		// asked every time: the administrator may change the rule while the page is open
		return post('/Chat/DlpCheck', { text: body }).then(function (r) {
			if (!r || !r.hits || !r.hits.length || r.mode === 'off') return body;
			var what = r.hits.map(function (h) { return h === 'card' ? t('a payment card number') : h === 'iban' ? t('an IBAN') : h === 'national-id' ? t('a national ID number') : t('a word the company listed'); }).join('، ');
			if (r.mode === 'block') { swal({ icon: 'error', title: t('Not sent'), text: t('This message contains') + ' ' + what + '. ' + t('The company does not allow it in chat.'), showCancelButton: false, confirmButtonText: t('OK') }); return null; }
			return swal({ icon: 'warning', title: t('Sensitive data'), text: t('This message contains') + ' ' + what + '. ' + t('Send it anyway?'), confirmButtonText: t('Send anyway'), customClass: { confirmButton: 'btn btn-danger', cancelButton: 'btn btn-light' } }).then(function (x) { return x.isConfirmed ? body : null; });
		});
	});

	/* ---------------- boot ---------------- */
	function boot() { loadExtras(); loadApprovals(); setTimeout(loadStatuses, 800); setInterval(loadApprovals, 120000); }
	C.on('open', function () { setTimeout(loadStatuses, 600); });
	if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot); else boot();
})();
