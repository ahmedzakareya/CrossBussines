/* Hospital H176 — internal calls, in every screen of the system (rendered by _HospitalCalls through _LabCriticalAlerts).
   Design: the approved prototype (#/calls): the ringing overlay, the dark call stage with the self view, the control bar
   and the call-context panel.

   The server only rings and relays (HospitalCallHub). Audio and video go browser to browser over WebRTC, through the
   hospital's own relay (TURN) when the two sit on different networks. Every call keeps ONE audio and ONE video
   transceiver from the start, so turning the camera on in a voice call or sharing the screen is a replaceTrack, never
   a renegotiation. */
(function () {
	'use strict';
	if (window.cbCalls) return;
	var W = window.cbCallsT || {}, U = window.cbCallsUrl || {};
	var rtl = document.documentElement.dir === 'rtl';
	var conn = null, current = null, ring = null, presenceFns = [], iceCache = null;

	function t(k, a) { var s = W[k] || k; return a == null ? s : s.replace('{0}', a); }
	function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
	function toast(msg, kind) { if (window.toastr) toastr[kind || 'info'](esc(msg)); }
	function token() { var i = document.querySelector('input[name="__RequestVerificationToken"]'); return i ? i.value : ''; }
	function post(url, body) {
		return fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token(), 'X-Requested-With': 'XMLHttpRequest' }, body: JSON.stringify(body || {}) })
			.then(function (r) { return r.json(); }).catch(function () { return { ok: false }; });
	}
	function get(url) { return fetch(url, { headers: { 'X-Requested-With': 'XMLHttpRequest' } }).then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; }); }
	function num(n) { return '<bdi class="cbc-num" dir="ltr">' + n + '</bdi>'; }
	function mmss(s) { return String(Math.floor(s / 60)).padStart(2, '0') + ':' + String(s % 60).padStart(2, '0'); }

	// keenicons has no camera, microphone or handset glyph — the prototype's own drawings.
	var SVG = {
		mic: '<path d="M12 3a3 3 0 0 0-3 3v6a3 3 0 0 0 6 0V6a3 3 0 0 0-3-3z"/><path d="M19 11a7 7 0 0 1-14 0M12 18v3"/>',
		micOff: '<path d="M3 3l18 18"/><path d="M9 9v3a3 3 0 0 0 5.1 2.1M15 9.3V6a3 3 0 0 0-5.7-1.3"/><path d="M19 11a7 7 0 0 1-1.1 3.8M5 11a7 7 0 0 0 10.6 6M12 18v3"/>',
		cam: '<rect x="3" y="6" width="13" height="12" rx="2"/><path d="M16 10l5-3v10l-5-3z"/>',
		camOff: '<path d="M3 3l18 18"/><path d="M16 16H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h1M10 6h4a2 2 0 0 1 2 2v3l5-3v10"/>',
		rec: '<circle cx="12" cy="12" r="6" fill="currentColor"/><circle cx="12" cy="12" r="10"/>',
		phone: '<path d="M22 16.9v3a2 2 0 0 1-2.2 2 19.8 19.8 0 0 1-8.6-3.1 19.5 19.5 0 0 1-6-6A19.8 19.8 0 0 1 2.1 4.2 2 2 0 0 1 4.1 2h3a2 2 0 0 1 2 1.7c.1.9.4 1.8.7 2.7a2 2 0 0 1-.5 2.1L8 9.8a16 16 0 0 0 6 6l1.3-1.3a2 2 0 0 1 2.1-.4c.9.3 1.8.6 2.7.7a2 2 0 0 1 1.7 2z"/>'
	};
	// Lucide (the Communication prototype's icons), drawn as an inline svg from its own shapes; the old names map onto it
	var LMAP = { mic: 'mic', micOff: 'mic-off', cam: 'video', camOff: 'video-off', rec: 'circle-dot', phone: 'phone' };
	function ico(k, size) {
		var s = size || 20, name = LMAP[k] || k, L = window.lucide && window.lucide.icons;
		var node = L && L[name.replace(/(^|-)([a-z0-9])/g, function (m, a, b) { return b.toUpperCase(); })];
		var open = '<svg class="cm-i" width="' + s + '" height="' + s + '" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">';
		if (node && node[2]) return open + node[2].map(function (c) { return '<' + c[0] + Object.keys(c[1]).map(function (a) { return ' ' + a + '="' + c[1][a] + '"'; }).join('') + '/>'; }).join('') + '</svg>';
		return SVG[k] ? open + SVG[k] + '</svg>' : '';
	}
	function face(p, size) {
		p = p || {};
		return '<span class="cbc-face" style="width:' + size + 'px;height:' + size + 'px;font-size:' + Math.round(size * 0.36) + 'px">' +
			(p.photo ? '<img src="' + esc(p.photo) + '" alt="" onerror="this.remove()" />' : '') + '<span>' + esc(p.initials || '?') + '</span></span>';
	}

	/* ---------------- sound: a soft ring for the callee, a ring-back for the caller (WebAudio, no file) ---------------- */
	var audioCtx = null;
	function tone(pattern) {
		var stop = false, timer;
		try { audioCtx = audioCtx || new (window.AudioContext || window.webkitAudioContext)(); } catch (e) { return function () { }; }
		function beep(f, at, dur) {
			var o = audioCtx.createOscillator(), g = audioCtx.createGain();
			o.frequency.value = f; o.type = 'sine'; g.gain.value = 0.0001;
			o.connect(g); g.connect(audioCtx.destination);
			var s = audioCtx.currentTime + at;
			g.gain.exponentialRampToValueAtTime(0.12, s + 0.02); g.gain.exponentialRampToValueAtTime(0.0001, s + dur);
			o.start(s); o.stop(s + dur + 0.05);
		}
		(function loop() { if (stop) return; pattern.forEach(function (b) { beep(b[0], b[1], b[2]); }); timer = setTimeout(loop, pattern.period); })();
		return function () { stop = true; clearTimeout(timer); };
	}
	var RING = [[880, 0, 0.35], [660, 0.4, 0.35], [880, 1.2, 0.35], [660, 1.6, 0.35]]; RING.period = 3200;
	var RINGBACK = [[425, 0, 1]]; RINGBACK.period = 4000;
	var WAIT = [[440, 0, 0.15], [440, 0.3, 0.15]]; WAIT.period = 4000;   // a call waiting: two soft beeps, not a ring over the call you are in

	/* ---------------- hub ---------------- */
	function connect() {
		if (!window.signalR || conn) return;
		// A restart of the server takes minutes: keep trying (1, 2, 3, 4 s, then every 5 s) for a quarter of an hour, and
		// start over after that. A call in progress does not wait for this — its media goes device to device.
		conn = new signalR.HubConnectionBuilder().withUrl(U.hub).withAutomaticReconnect({
			nextRetryDelayInMilliseconds: function (r) { return r.elapsedMilliseconds > 15 * 60 * 1000 ? null : (r.previousRetryCount < 4 ? (r.previousRetryCount + 1) * 1000 : 5000); }
		}).build();
		conn.onclose(function () { setTimeout(function () { conn.start().then(resumeAll).catch(function () { }); }, 5000); });
		conn.on('incoming', onIncoming);
		conn.on('answered', onAnswered);
		conn.on('ended', onEnded);
		conn.on('signal', onSignal);
		conn.on('taken', function (m) { if (ring && ring.callId === m.callId) closeRing(); });
		conn.on('missed', function () { toast(t('missedGeneric')); });
		conn.on('recask', onRecAsk);
		conn.on('recstart', onRecStart);
		conn.on('recclosed', onRecClosed);
		conn.on('recstop', onRecStop);
		conn.on('presence', function (m) { presenceFns.forEach(function (f) { try { f(m); } catch (e) { } }); });
		conn.onreconnected(function () { resumeAll(); presenceFns.forEach(function (f) { try { f({ refresh: true }); } catch (e) { } }); });
		conn.on('peeraway', function (m) { var c = current; if (c && c.id === m.callId) { c.peerAway = true; netChip(c); } });
		conn.on('peerback', function (m) { var c = current; if (c && c.id === m.callId) { c.peerAway = false; netChip(c); } });
		(window.cbCallsHooks || []).forEach(function (f) { try { f(conn); } catch (e) { } });   // H176 group calls
		conn.start().then(function () { document.dispatchEvent(new CustomEvent('cbcalls:ready')); }).catch(function () { });
	}
	function connected() { return conn && conn.state === 'Connected'; }
	// back after a drop or a server restart: the server is told which call this tab is in, and rebuilds it
	function resumeAll() {
		var c = current;
		if (c && c.answered) conn.invoke('Resume', c.id).then(function (r) { if (r && r.ok) { c.peerAway = !r.peerHere; netChip(c); } }).catch(function () { });
		document.dispatchEvent(new CustomEvent('cbcalls:resumed'));
	}
	function netChip(c) {
		if (!c || !c.el) return;
		var chip = c.el.querySelector('[data-net]'); if (!chip) return;
		var s = c.pc && c.pc.connectionState;
		chip.classList.toggle('d-none', !(c.peerAway || s === 'disconnected' || s === 'connecting'));
	}
	function boot() {
		if (window.signalR) connect();
		else { var s = document.createElement('script'); s.src = U.signalr; s.onload = connect; document.head.appendChild(s); }
	}

	function iceServers() {
		if (iceCache && iceCache.at > Date.now() - 6 * 3600 * 1000) return Promise.resolve(iceCache.servers);
		return get(U.ice).then(function (r) { var s = (r && r.iceServers) || []; iceCache = { at: Date.now(), servers: s }; return s; });
	}

	/* ---------------- incoming: the prototype's card (#/comm-ring), in the corner of whatever screen is open ---------------- */
	function ringLeft(n) { return '<span class="num">' + Math.max(0, n) + '</span> ' + esc(t('cmRingLeft')); }
	function onIncoming(m) {
		var inGroup = window.cbCallsGroup && window.cbCallsGroup.current && window.cbCallsGroup.current();
		if (ring || ((current || (window.cbCallsGroup && window.cbCallsGroup.busy())) && !m.waiting)) { return; }   // a second tab still answers only once; a call while in one rings only as "waiting" (Communication v2)
		var waiting = !!m.waiting && !!(current || inGroup);
		var left = m.ringSeconds || 30, video = !!m.video, p = m.patient, who = m.caller || {};
		var el = document.createElement('div'); el.className = 'cm-ring'; el.setAttribute('role', 'alertdialog');
		var about = p ? '<div class="cm-lastmsg">' + ico('user-round', 16) + '<div class="fs-7 min-w-0"><div class="text-muted fs-8">' + esc(t('aboutPatient')) + '</div><span class="fw-bold">' + esc(p.name) + '</span> · ' + num(esc(p.mrn)) + (m.reason ? '<div>' + esc(m.reason) + '</div>' : '') + '</div></div>'
			: m.reason ? '<div class="cm-lastmsg">' + ico('message-square-text', 16) + '<div class="fs-7 min-w-0"><div class="text-muted fs-8">' + esc(t('reason')) + '</div>' + esc(m.reason) + '</div></div>' : '';
		el.innerHTML = '<div class="band"><span class="ring">' + face(who, 56) + '</span><div class="min-w-0">' +
				'<div class="fs-8 opacity-75 d-flex align-items-center gap-1">' + ico(video ? 'video' : 'phone', 13) + esc(waiting ? t('cmWaiting') : video ? t('cmIncomingVideo') : t('cmIncomingVoice')) + '</div>' +
				'<div class="fs-4 fw-bold text-truncate">' + esc(who.name) + '</div><div class="fs-8 opacity-75 text-truncate">' + esc([who.title, who.department].filter(Boolean).join(' · ')) + '</div></div></div>' +
			'<div class="p-5">' + about +
				'<div class="cm-lastmsg d-none" data-last>' + ico('message-circle', 16) + '<div class="fs-7 min-w-0"><div class="text-muted fs-8">' + esc(t('cmLastMsg')) + '</div><div class="text-truncate" dir="auto" data-lasttx></div></div></div>' +
				'<div class="d-flex flex-wrap gap-2 mb-4">' + [t('cmQuick1'), t('cmQuick2'), t('cmQuick3')].map(function (q) { return '<button type="button" class="cm-pill light sm" data-reply>' + esc(q) + '</button>'; }).join('') + '</div>' +
				'<div class="cm-ringbtns"' + (video ? '' : ' style="grid-template-columns:repeat(2,1fr)"') + '>' +
					'<button type="button" class="no" data-decline>' + ico('phone-off', 20) + '<span>' + esc(t('decline')) + '</span></button>' +
					(video ? '<button type="button" class="vo" data-voice>' + ico('phone', 20) + '<span>' + esc(t('cmByVoice')) + '</span></button>' : '') +
					'<button type="button" class="yes" data-accept>' + ico(waiting ? 'phone-forwarded' : video ? 'video' : 'phone', 20) + '<span>' + esc(waiting ? t('cmEndAnswer') : t('cmAnswer')) + '</span></button></div>' +
				'<div class="text-muted fs-8 text-center mt-3" data-left>' + ringLeft(left) + '</div>' +
			'</div>';
		document.body.appendChild(el);
		// the last message between the two of you (the prototype's "your last message")
		if (U.lastWith && who.id) get(U.lastWith + '?other=' + who.id).then(function (r) {
			if (!r || !r.body || !ring || ring.el !== el) return;
			el.querySelector('[data-lasttx]').textContent = r.body; el.querySelector('[data-last]').classList.remove('d-none');
		});
		var stopTone = waiting ? tone(WAIT) : tone(RING);
		ring = { callId: m.callId, el: el, stopTone: stopTone, msg: m };
		ring.timer = setInterval(function () {
			left--; var l = el.querySelector('[data-left]'); if (l) l.innerHTML = ringLeft(left);
			if (left <= -5) closeRing();   // the caller's side ends it; this only tidies up if its message was lost
		}, 1000);
		el.querySelector('[data-decline]').onclick = function () { conn.invoke('Decline', m.callId, null); closeRing(); };
		[].slice.call(el.querySelectorAll('[data-reply]')).forEach(function (b) { b.onclick = function () { conn.invoke('Decline', m.callId, b.textContent); closeRing(); toast(t('replySent', who.name), 'success'); }; });
		function accept(withVideo) {
			closeRing();
			// waiting: the call I am in ends first (a group call: I leave it), then this one is answered
			if (current) { var old = current; hangup(old); teardown(old); }
			if (window.cbCallsGroup && window.cbCallsGroup.current && window.cbCallsGroup.current() && window.cbCallsGroup.leave) window.cbCallsGroup.leave();
			conn.invoke('Answer', m.callId, withVideo).then(function (r) {
				if (!r || !r.ok) { toast(t('failed'), 'error'); return; }
				current = newCall({ id: m.callId, role: 'callee', video: withVideo, peer: who, patient: m.patient, reason: m.reason, share: m.share, maxHeight: m.maxHeight, canRecord: m.canRecord });
				current.answered = true;
				openStage(current); startClock(current);
				current.pcReady = prepare(current);
			});
		}
		el.querySelector('[data-accept]').onclick = function () { accept(video); };
		var v = el.querySelector('[data-voice]'); if (v) v.onclick = function () { accept(false); };
	}
	function closeRing() { if (!ring) return; clearInterval(ring.timer); ring.stopTone(); ring.el.remove(); ring = null; }

	/* ---------------- outgoing ---------------- */
	function start(employeeId, video, patientId, reason) {
		if (current) { toast(t('alreadyInCall')); return; }
		if (!window.isSecureContext) { toast(t('insecure'), 'error'); return; }
		if (!connected()) { toast(t('notConnected'), 'error'); return; }
		conn.invoke('Invite', employeeId, !!video, patientId || null, reason || null).then(function (r) {
			if (!r || !r.ok) { toast(t(r && r.error ? r.error : 'failed', r && r.name), r && r.name ? 'info' : 'error'); if (r && r.name) refreshLog(); return; }
			current = newCall({ id: r.callId, role: 'caller', video: r.video, peer: r.callee, patient: r.patient, reason: reason, share: r.share, maxHeight: r.maxHeight, canRecord: r.canRecord });
			openStage(current);
			current.stopTone = tone(RINGBACK);
			current.media = media(current);   // ask for the camera while it rings, so answering connects at once
			var left = r.ringSeconds || 30;
			current.ringTimer = setInterval(function () {
				if (--left > 0) return;
				clearInterval(current.ringTimer);
				conn.invoke('Hangup', current.id, true);
			}, 1000);
		}).catch(function () { toast(t('failed'), 'error'); });
	}

	function newCall(o) { o.mic = true; o.cam = !!o.video; o.sharing = false; o.secs = 0; o.cands = []; o.remote = { cam: !!o.video, share: false, mic: true }; return o; }

	function onAnswered(m) {
		var c = current; if (!c || c.id !== m.callId || c.role !== 'caller') return;
		clearInterval(c.ringTimer); if (c.stopTone) c.stopTone();
		c.answered = true; c.remote.cam = !!m.video;
		paintRemote(c); startClock(c); paintBar(c);
		c.pcReady = prepare(c).then(function () {
			return c.pc.createOffer().then(function (o) { return c.pc.setLocalDescription(o); }).then(function () { send(c, { t: 'offer', sdp: c.pc.localDescription }); });
		});
	}

	function onEnded(m) {
		if (ring && ring.callId === m.callId) {
			var from = ring.msg.caller.name;
			closeRing();
			if (m.status === 'Missed' || m.status === 'Cancelled') { toast(t('missedFrom', from)); }
			refreshLog(); return;
		}
		var c = current; if (!c || c.id !== m.callId) return;
		if (c.role === 'caller' && !c.answered) {
			if (m.status === 'Declined') toast(t('declined', c.peer.name) + (m.reply ? ' — «' + m.reply + '»' : ''));
			else if (m.status === 'Missed') toast(t('noAnswer'));
		} else toast(t('ended'), 'success');
		teardown(c); refreshLog();
	}

	/* ---------------- WebRTC ---------------- */
	function media(c) {
		if (c.local) return Promise.resolve(c.local);
		var v = c.video ? { height: { ideal: c.maxHeight || 720 }, facingMode: 'user' } : false;
		var tryGet = navigator.mediaDevices && navigator.mediaDevices.getUserMedia ? navigator.mediaDevices.getUserMedia({ audio: true, video: v }) : Promise.reject(new Error('no media'));
		return tryGet.catch(function (e) {
			if (!v) throw e;
			c.cam = false; return navigator.mediaDevices.getUserMedia({ audio: true, video: false });
		}).then(function (s) { c.local = s; paintSelf(c); return s; })
			.catch(function (e) { c.local = new MediaStream(); c.mic = false; c.cam = false; toast(t('noMedia', e && e.name ? e.name : ''), 'error'); paintBar(c); return c.local; });
	}

	function prepare(c) {
		return Promise.all([iceServers(), c.media || media(c)]).then(function (res) {
			if (!current || current !== c) return;
			var pc = c.pc = new RTCPeerConnection({ iceServers: res[0] });
			c.remoteStream = new MediaStream();
			pc.onicecandidate = function (e) { if (e.candidate) send(c, { t: 'cand', c: e.candidate }); };
			pc.ontrack = function (e) {
				if (c.remoteStream.getTracks().indexOf(e.track) < 0) c.remoteStream.addTrack(e.track);
				if (e.track.kind === 'audio') { var a = c.el.querySelector('[data-raudio]'); a.srcObject = c.remoteStream; a.play().catch(function () { }); }
				e.track.onunmute = function () { paintRemote(c); }; e.track.onmute = function () { paintRemote(c); };
				paintRemote(c);
			};
			pc.onconnectionstatechange = function () {
				var s = pc.connectionState;
				netChip(c);
				if (s === 'failed') { toast(t('lost'), 'error'); hangup(c); }
			};
			if (c.role === 'caller') {
				var a = c.local.getAudioTracks()[0], v = c.local.getVideoTracks()[0];
				c.audioTx = pc.addTransceiver(a || 'audio', { direction: 'sendrecv', streams: [c.local] });
				c.videoTx = pc.addTransceiver(v || 'video', { direction: 'sendrecv', streams: [c.local] });
			}
			c.quality = setInterval(function () { quality(c); }, 3000);
		});
	}

	function send(c, obj) { if (connected()) conn.invoke('Signal', c.id, JSON.stringify(obj)).catch(function () { }); }
	function sendState(c) { send(c, { t: 'state', mic: c.mic, cam: c.cam, share: c.sharing }); }

	function onSignal(m) {
		var c = current; if (!c || c.id !== m.callId) return;
		var d; try { d = JSON.parse(m.payload); } catch (e) { return; }
		(c.pcReady || Promise.resolve()).then(function () {
			var pc = c.pc; if (!pc) return;
			if (d.t === 'offer') {
				return pc.setRemoteDescription(d.sdp).then(function () {
					var tx = pc.getTransceivers();
					c.audioTx = tx.filter(function (x) { return x.receiver.track && x.receiver.track.kind === 'audio'; })[0];
					c.videoTx = tx.filter(function (x) { return x.receiver.track && x.receiver.track.kind === 'video'; })[0];
					var a = c.local.getAudioTracks()[0], v = c.local.getVideoTracks()[0];
					if (c.audioTx) { c.audioTx.direction = 'sendrecv'; if (a) c.audioTx.sender.replaceTrack(a); }
					if (c.videoTx) { c.videoTx.direction = 'sendrecv'; if (v) c.videoTx.sender.replaceTrack(v); }
					return pc.createAnswer();
				}).then(function (ans) { return pc.setLocalDescription(ans); })
					.then(function () { send(c, { t: 'answer', sdp: pc.localDescription }); flush(c); sendState(c); });
			}
			if (d.t === 'answer') return pc.setRemoteDescription(d.sdp).then(function () { flush(c); sendState(c); });
			if (d.t === 'cand') { if (pc.remoteDescription) return pc.addIceCandidate(d.c).catch(function () { }); c.cands.push(d.c); return; }
			if (d.t === 'state') { c.remote = { mic: d.mic, cam: d.cam, share: d.share }; paintRemote(c); return; }
			if (d.t === 'hand') { c.peerHand = !!d.on; paintFlags(c); if (d.on) toast('✋ ' + c.peer.name, 'info'); return; }
			if (d.t === 'react') { floatReact(c, String(d.e || '').slice(0, 4), false); return; }
			if (d.t === 'hold') { hold(c, !!d.on, false); return; }
			if (d.t === 'patient' && d.patient && !c.patient) { c.patient = d.patient; paintContext(c); return; }
		}).catch(function () { });
	}
	function flush(c) { var l = c.cands; c.cands = []; l.forEach(function (x) { c.pc.addIceCandidate(x).catch(function () { }); }); }

	function quality(c) {
		if (!c.pc || !c.el) return;
		c.pc.getStats().then(function (st) {
			var rtt = null, lost = 0, got = 0;
			st.forEach(function (r) {
				if (r.type === 'candidate-pair' && r.nominated && r.currentRoundTripTime != null) rtt = r.currentRoundTripTime;
				if (r.type === 'inbound-rtp') { lost += r.packetsLost || 0; got += r.packetsReceived || 0; }
			});
			var lossPct = got ? lost / (lost + got) : 0;
			var q = rtt == null ? 3 : (rtt < 0.15 && lossPct < 0.02) ? 3 : (rtt < 0.4 && lossPct < 0.08) ? 2 : 1;
			var chip = c.el.querySelector('[data-q]'); if (!chip) return;
			var sig = chip.querySelector('svg'); if (sig) sig.style.opacity = q === 3 ? 1 : q === 2 ? 0.7 : 0.4;
			chip.title = q === 3 ? t('qExcellent') : q === 2 ? t('qGood') : t('qWeak');
		}).catch(function () { });
	}

	/* ---------------- the stage: the prototype's #/comm-incall — the picture, and the same conversation beside it ---------------- */
	function openStage(c) {
		var el = c.el = document.createElement('div'); el.className = 'cm-stage'; c.sideOpen = true;
		el.innerHTML =
			'<div class="main">' +
				'<div class="vid" data-remote><video autoplay playsinline data-rvideo class="d-none"></video><div class="cbc-rface" data-rface></div></div>' +
				'<audio autoplay data-raudio></audio>' +
				'<div class="top">' + face(c.peer, 40) +
					'<div class="min-w-0"><div class="fw-bold text-truncate">' + esc(c.peer.name) + '</div><div class="fs-8 opacity-75 d-flex align-items-center gap-1">' + ico('shield-check', 12) + ' ' + esc(t('cmSecure')) + '</div></div>' +
					'<span class="ms-auto d-flex align-items-center gap-2">' +
						'<span class="chip d-none" data-net><span class="spinner-border spinner-border-sm"></span>' + esc(t('reconnecting')) + '</span>' +
						'<span class="chip rec d-none" data-recchip></span>' +
						'<span class="chip" data-q>' + ico('signal', 13) + ' <span class="num" data-clock>' + esc(t('calling')) + '</span></span>' +
					'</span>' +
				'</div>' +
				'<div class="pip" data-self></div>' +
				'<div class="bar" data-bar></div>' +
			'</div>' +
			'<div class="side" data-side></div>';
		document.body.appendChild(el);
		paintRemote(c); paintSelf(c); paintBar(c); paintSide(c);
		window.addEventListener('beforeunload', c.unload = function () { if (current) conn.invoke('Hangup', current.id, false); });
	}

	function startClock(c) {
		clearInterval(c.clock); c.secs = 0;
		var el = c.el.querySelector('[data-clock]'); el.textContent = '00:00';
		c.clock = setInterval(function () { c.secs++; el.textContent = mmss(c.secs); }, 1000);
	}

	function paintRemote(c) {
		var box = c.el.querySelector('[data-rface]'), v = c.el.querySelector('[data-rvideo]');
		if (!c.answered) {
			box.innerHTML = '<div class="text-center text-white"><span class="cbc-ring">' + face(c.peer, 125) + '</span><div class="fs-3 fw-bold mt-6">' + esc(c.peer.name) + '</div><div class="opacity-75 mt-1">' + esc(t('calling')) + '</div></div>';
			v.classList.add('d-none'); return;
		}
		var vt = c.remoteStream && c.remoteStream.getVideoTracks()[0];
		var showVideo = vt && !vt.muted && (c.remote.cam || c.remote.share);
		if (showVideo) { if (v.srcObject !== c.remoteStream) v.srcObject = c.remoteStream; v.classList.remove('d-none'); v.classList.toggle('cbc-contain', !!c.remote.share); box.innerHTML = ''; v.play().catch(function () { }); }
		else {
			v.classList.add('d-none');
			box.innerHTML = '<div class="text-center text-white">' + face(c.peer, 125) + '<div class="opacity-75 mt-5">' + esc(c.video || c.remote.cam ? t('theirCamOff') : t('voiceCall')) + (c.remote.mic === false ? ' · ' + esc(t('theyMuted')) : '') + '</div></div>';
		}
	}

	function paintSelf(c) {
		if (!c.el) return;
		var box = c.el.querySelector('[data-self]');
		var vt = c.local && c.local.getVideoTracks()[0];
		if (c.sharing) { box.innerHTML = '<div class="text-center text-white">' + ico('monitor-up', 28) + '<div class="fs-8 mt-2">' + esc(t('sharing')) + '</div></div>'; return; }
		if (vt && c.cam) { box.innerHTML = '<video autoplay muted playsinline></video>'; box.querySelector('video').srcObject = new MediaStream([vt]); }
		else box.innerHTML = face(W.me || {}, 60);
	}

	// the prototype's bar: microphone, camera, share the screen, the chat, record, add someone, end
	function paintBar(c) {
		var bar = c.el.querySelector('[data-bar]'), recOn = c.rec && c.rec.state === 'on';
		function cb(k, icon, label, cls, off) { return '<button type="button" data-k="' + k + '" class="' + (cls || '') + '" title="' + esc(label) + '" aria-label="' + esc(label) + '"' + (off ? ' disabled' : '') + '>' + ico(icon, 21) + '</button>'; }
		bar.innerHTML =
			cb('mic', c.mic ? 'mic' : 'mic-off', c.mic ? t('mute') : t('unmute'), c.mic ? '' : 'on') +
			cb('cam', c.cam ? 'video' : 'video-off', t('camera'), c.cam ? '' : 'on') +
			(c.share !== false && navigator.mediaDevices && navigator.mediaDevices.getDisplayMedia ? cb('share', 'monitor-up', t('cmShare'), c.sharing ? 'on' : '') : '') +
			cb('chat', 'message-circle', t('cmTheChat'), c.sideOpen ? 'on' : '') +
			(c.canRecord && window.MediaRecorder ? cb('rec', recOn ? 'circle-stop' : 'circle-dot', recOn ? t('recStop') : t('recAsk'), recOn ? 'rec' : '', !c.answered) : '') +
			cb('add', 'user-plus', t('addPeople'), '', !(c.answered && window.cbCallsGroup)) +
			cb('more', 'ellipsis', t('cmMore'), c.hand || c.held ? 'on' : '', !c.answered) +
			cb('end', 'phone-off', t('end'), 'red');
		[].slice.call(bar.querySelectorAll('[data-k]')).forEach(function (b) { b.onclick = function () { control(c, b.getAttribute('data-k')); }; });
	}

	function control(c, k) {
		if (k === 'end') { hangup(c); return; }
		if (k === 'rec') { recControl(c); return; }
		if (k === 'add') { if (window.cbCallsGroup) window.cbCallsGroup.fromCall(c); return; }
		if (k === 'more') { moreMenu(c); return; }
		if (k === 'chat') { c.sideOpen = !c.sideOpen; c.el.classList.toggle('no-side', !c.sideOpen); c.el.classList.toggle('side-open', c.sideOpen); paintBar(c); return; }
		if (k === 'mic') { var a = c.local && c.local.getAudioTracks()[0]; if (!a) { toast(t('noMic'), 'error'); return; } c.mic = !c.mic; a.enabled = c.mic; }
		if (k === 'cam') {
			var v = c.local && c.local.getVideoTracks()[0];
			if (v) { c.cam = !c.cam; v.enabled = c.cam; }
			else {
				// a voice call turning its camera on: the video transceiver is already there
				navigator.mediaDevices.getUserMedia({ video: { height: { ideal: c.maxHeight || 720 } } }).then(function (s) {
					var nv = s.getVideoTracks()[0]; c.local.addTrack(nv); c.cam = true;
					if (c.videoTx && !c.sharing) c.videoTx.sender.replaceTrack(nv);
					paintSelf(c); paintBar(c); sendState(c);
				}).catch(function (e) { toast(t('noMedia', e.name || ''), 'error'); });
				return;
			}
		}
		if (k === 'share') {
			if (c.sharing) { stopShare(c); return; }
			navigator.mediaDevices.getDisplayMedia({ video: true }).then(function (s) {
				var st = s.getVideoTracks()[0]; c.screen = s; c.sharing = true;
				if (c.videoTx) c.videoTx.sender.replaceTrack(st);
				st.onended = function () { stopShare(c); };
				paintSelf(c); paintBar(c); sendState(c);
			}).catch(function () { });
			return;
		}
		paintSelf(c); paintBar(c); sendState(c);
	}
	// ---- Communication v2: what the leading call systems have in a call — raise a hand, react, hold, transfer, noise ----
	var REACTS = ['👍', '👏', '❤️', '😂', '🎉', '🙏'];
	function moreMenu(c) {
		var old = c.el.querySelector('.cbc-more'); if (old) { old.remove(); return; }
		var m = document.createElement('div'); m.className = 'cbc-more';
		m.innerHTML = '<div class="rx">' + REACTS.map(function (e) { return '<button type="button" data-rx="' + e + '">' + e + '</button>'; }).join('') + '</div>' +
			'<button type="button" class="it" data-m="hand">' + ico('hand', 18) + '<span>' + esc(c.hand ? t('cmLowerHand') : t('cmRaiseHand')) + '</span></button>' +
			'<button type="button" class="it" data-m="hold">' + ico(c.held ? 'play' : 'pause', 18) + '<span>' + esc(c.held ? t('cmResume') : t('cmHold')) + '</span></button>' +
			(window.cbCallsGroup && window.cbCallsGroup.transfer ? '<button type="button" class="it" data-m="transfer">' + ico('phone-forwarded', 18) + '<span>' + esc(t('cmTransfer')) + '</span></button>' : '') +
			'<button type="button" class="it" data-m="noise">' + ico('audio-lines', 18) + '<span>' + esc(t('cmNoise')) + '</span><b>' + esc(c.noiseOff ? t('cmOff') : t('cmOn')) + '</b></button>';
		c.el.querySelector('.main').appendChild(m);
		m.onclick = function (e) {
			var b = e.target.closest('button'); if (!b) return;
			var rx = b.getAttribute('data-rx'), k = b.getAttribute('data-m');
			m.remove();
			if (rx) { send(c, { t: 'react', e: rx }); floatReact(c, rx, true); return; }
			if (k === 'hand') { c.hand = !c.hand; send(c, { t: 'hand', on: c.hand }); paintFlags(c); paintBar(c); }
			if (k === 'hold') hold(c, !c.held, true);
			if (k === 'transfer') window.cbCallsGroup.transfer(c);
			if (k === 'noise') {
				c.noiseOff = !c.noiseOff;
				var a = c.local && c.local.getAudioTracks()[0];
				if (a && a.applyConstraints) a.applyConstraints({ noiseSuppression: !c.noiseOff, echoCancellation: true, autoGainControl: !c.noiseOff }).catch(function () { });
				toast(c.noiseOff ? t('cmNoiseOff') : t('cmNoiseOn'), 'info');
			}
		};
	}
	function hold(c, on, mine) {
		if (mine) {
			c.held = on;
			var a = c.local && c.local.getAudioTracks()[0], v = c.local && c.local.getVideoTracks()[0];
			if (on) { c.preHold = { mic: c.mic, cam: c.cam }; if (a) a.enabled = false; if (v) v.enabled = false; }
			else if (c.preHold) { if (a) a.enabled = c.preHold.mic; if (v) v.enabled = c.preHold.cam; }
			send(c, { t: 'hold', on: on }); paintBar(c);
		} else c.heldByPeer = on;
		var ra = c.el.querySelector('[data-raudio]'); if (ra) ra.muted = !!(c.held || c.heldByPeer);
		paintFlags(c);
	}
	function paintFlags(c) {
		var top = c.el.querySelector('.top .ms-auto'); if (!top) return;
		var f = top.querySelector('[data-flags]'); if (!f) { f = document.createElement('span'); f.setAttribute('data-flags', ''); f.className = 'd-flex gap-2'; top.insertBefore(f, top.firstChild); }
		f.innerHTML = (c.peerHand ? '<span class="chip">✋ ' + esc(c.peer.name) + '</span>' : '') + (c.hand ? '<span class="chip">✋ ' + esc(t('you')) + '</span>' : '') +
			(c.held || c.heldByPeer ? '<span class="chip">' + ico('pause', 13) + ' ' + esc(c.held ? t('cmOnHoldMine') : t('cmOnHoldTheirs')) + '</span>' : '');
		var vid = c.el.querySelector('.vid'); if (vid) vid.classList.toggle('cbc-held', !!(c.held || c.heldByPeer));
	}
	function floatReact(c, e, mine) {
		var main = c.el.querySelector('.main'); if (!main) return;
		var f = document.createElement('div'); f.className = 'cbc-float'; f.textContent = e;
		f.style.insetInlineStart = (mine ? 60 : 30) + Math.round(Math.random() * 25) + '%';
		main.appendChild(f); setTimeout(function () { f.remove(); }, 2600);
	}
	function stopShare(c) {
		if (!c.sharing) return;
		c.sharing = false; if (c.screen) c.screen.getTracks().forEach(function (x) { x.stop(); }); c.screen = null;
		var v = c.local && c.local.getVideoTracks()[0];
		if (c.videoTx) c.videoTx.sender.replaceTrack(v || null);
		if (c.el) { paintSelf(c); paintBar(c); } sendState(c);
	}

	/* ---- the side: the conversation between the two (what is written stays in the chat after the call), and the call's
	   own context (the note, the participants, the recording's trail; in the hospital, the patient) one tap away ---- */
	function paintSide(c) {
		var side = c.el.querySelector('[data-side]');
		side.innerHTML =
			'<div class="cm-head"><div class="min-w-0"><div class="nm" data-stitle>' + esc(t('cmTheChat')) + '</div><div class="st text-muted fs-8" data-ssub>' + esc(t('cmChatStays')) + '</div></div>' +
				'<div class="acts"><button type="button" class="cm-ib ghost sm" data-ctx title="' + esc(t('context')) + '" aria-label="' + esc(t('context')) + '">' + ico('clipboard-list', 18) + '</button>' +
				'<button type="button" class="cm-ib ghost sm" data-hide title="' + esc(t('cmHide')) + '" aria-label="' + esc(t('cmHide')) + '">' + ico('x', 18) + '</button></div></div>' +
			'<div class="cm-thread" data-sthread><div class="cm-day"><span>' + esc(t('cmToday')) + '</span></div></div>' +
			'<div class="cm-compose" data-scompose><div class="box">' +
				'<button type="button" class="cm-ib ghost xs" data-sattach title="' + esc(t('cmAttach')) + '">' + ico('plus', 18) + '</button>' +
				'<input data-sinput placeholder="' + esc(t('cmWrite')) + '" />' +
				'<button type="button" class="cm-ib ghost xs" data-semoji title="' + esc(t('cmEmoji')) + '">' + ico('smile', 18) + '</button>' +
				'<button type="button" class="cm-ib ghost xs" data-sfile title="' + esc(t('cmFile')) + '">' + ico('paperclip', 18) + '</button></div>' +
				'<button type="button" class="cm-send" data-ssend title="' + esc(t('cmSend')) + '">' + ico('send-horizontal', 18) + '</button>' +
				'<input type="file" class="d-none" data-sfileinput /></div>' +
			'<div class="cbc-ctx d-none" data-ctxpane></div>';
		side.querySelector('[data-hide]').onclick = function () { control(c, 'chat'); };
		side.querySelector('[data-ctx]').onclick = function () {
			var pane = side.querySelector('[data-ctxpane]'), on = pane.classList.contains('d-none');
			pane.classList.toggle('d-none', !on);
			['[data-sthread]', '[data-scompose]'].forEach(function (q) { side.querySelector(q).classList.toggle('d-none', on); });
			side.querySelector('[data-stitle]').textContent = on ? t('context') : t('cmTheChat');
			side.querySelector('[data-ssub]').textContent = on ? t(U.hospital ? 'contextSub' : 'cmContextSub') : t('cmChatStays');
			this.innerHTML = ico(on ? 'message-circle' : 'clipboard-list', 18);
		};
		paintContext(c);
		sideChat(c);
	}
	function paintContext(c) {
		var pane = c.el.querySelector('[data-ctxpane]'); if (!pane) return;
		var p = c.patient;
		// a patient belongs to the hospital module: someone without it sees no patient search and no file link
		var pat = !U.hospital ? (c.reason ? '<div class="p-6 border-bottom text-gray-700 fs-7">' + esc(c.reason) + '</div>' : '') : p
			? '<div class="p-6 border-bottom"><div class="d-flex align-items-center gap-3">' + face(p, 45) + '<div class="min-w-0"><div class="fw-bold text-gray-900 text-truncate">' + esc(p.name) + '</div><div class="text-muted fs-7">' + num(esc(p.mrn)) + '</div></div></div>' +
				(c.reason ? '<div class="text-gray-700 fs-7 mt-3">' + esc(c.reason) + '</div>' : '') +
				'<a class="cm-pill light w-100 justify-content-center mt-4" target="_blank" rel="noopener" href="' + esc(U.patient.replace('__id__', p.id)) + '">' + ico('folder-open', 15) + esc(t('openFile')) + '</a></div>'
			: '<div class="p-6 border-bottom">' + (c.reason ? '<div class="text-gray-700 fs-7 mb-3">' + esc(c.reason) + '</div>' : '') + '<div class="text-muted fs-7 mb-3">' + esc(t('notLinked')) + '</div>' +
				'<div class="cm-search">' + ico('search', 16) + '<input data-psearch placeholder="' + esc(t('searchPatient')) + '" autocomplete="off" /></div>' +
				'<div data-presults class="mt-2"></div></div>';
		var note = '<div class="p-6 border-bottom"><label class="fw-semibold fs-7 text-gray-700 mb-2">' + esc(t('quickNote')) + '</label>' +
			'<textarea class="form-control form-control-solid" rows="4" maxlength="1000" data-note placeholder="' + esc(U.hospital ? t('notePh') : t('cmNotePh')) + '">' + esc(c.note || '') + '</textarea>' +
			'<div class="text-muted fs-8 mt-2">' + esc(t('noteHelp')) + '</div></div>';
		var who = '<div class="p-6"><div class="fw-bold text-gray-900 mb-3">' + esc(t('inCall')) + '</div>' +
			'<div class="cm-row">' + face(W.me || {}, 35) + '<div><div class="t">' + esc((W.me || {}).name || '') + '</div><div class="s">' + esc(t('you')) + '</div></div></div>' +
			'<div class="cm-row">' + face(c.peer, 35) + '<div><div class="t">' + esc(c.peer.name) + '</div><div class="s">' + esc(c.peer.title || '') + '</div></div></div></div>';
		pane.innerHTML = pat + note + '<div class="p-6 border-bottom' + (c.recTrail && c.recTrail.length ? '' : ' d-none') + '" data-rectrail>' + recTrailHtml(c) + '</div>' + who;
		var ta = pane.querySelector('[data-note]');
		ta.addEventListener('input', function () { c.note = ta.value; c.noteDirty = true; });
		ta.addEventListener('blur', function () { saveNote(c); });
		var ps = pane.querySelector('[data-psearch]');
		if (ps) {
			var tm;
			ps.addEventListener('input', function () {
				clearTimeout(tm); var q = ps.value.trim(), out = pane.querySelector('[data-presults]');
				if (q.length < 2) { out.innerHTML = ''; return; }
				tm = setTimeout(function () {
					get(U.patientSearch + '?q=' + encodeURIComponent(q)).then(function (r) {
						var rows = (r && r.results) || [];
						out.innerHTML = rows.slice(0, 6).map(function (x) {
							return '<a href="#" class="cm-row text-decoration-none cbc-hit" data-pid="' + x.id + '">' + face(x, 30) + '<span class="min-w-0"><span class="d-block t text-truncate">' + esc(x.name) + '</span><span class="d-block s">' + num(esc(x.mrn)) + '</span></span></a>';
						}).join('') || '<div class="text-muted fs-8 p-2">' + esc(t('noMatch')) + '</div>';
						[].slice.call(out.querySelectorAll('[data-pid]')).forEach(function (a) {
							a.onclick = function (e) {
								e.preventDefault();
								post(U.note.replace('__id__', c.id), { patientId: +a.getAttribute('data-pid') }).then(function (res) {
									if (!res.ok) { toast(res.error || t('failed'), 'error'); return; }
									get(U.patientCard.replace('__id__', a.getAttribute('data-pid'))).then(function (pc) {
										if (pc && pc.patient) { c.patient = pc.patient; paintContext(c); send(c, { t: 'patient', patient: pc.patient }); }
									});
								});
							};
						});
					});
				}, 250);
			});
		}
	}

	// the conversation beside the call: the same direct chat as the Communication page (read, write, attach)
	function postForm(url, data) {
		var fd = data instanceof FormData ? data : new FormData(); if (!(data instanceof FormData)) for (var k in data) if (data[k] != null) fd.append(k, data[k]);
		return fetch(url, { method: 'POST', credentials: 'same-origin', headers: { 'RequestVerificationToken': token() }, body: fd }).then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; });
	}
	function chatMsg(m) {
		var mine = m.senderId === (W.me || {}).id, d = new Date(m.createdAt);
		var at = String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
		var body = m.deleted ? '<div class="tx">' + esc(t('cmDeleted')) + '</div>'
			: (m.attachmentPath ? '<div class="cm-file"><span class="fi">' + ico(m.attachmentType === 'image' ? 'image' : m.attachmentType === 'audio' ? 'mic' : 'file', 18) + '</span><div class="fm"><div class="fn">' + esc(m.attachmentName || '') + '</div></div></div>' : '') +
				(m.body ? '<div class="tx" dir="auto">' + esc(m.body) + '</div>' : '');
		return '<div class="cm-msg' + (mine ? ' mine' : '') + '" data-id="' + m.id + '">' + (mine ? '' : '<span class="cm-msg-face">' + face({ name: m.senderName, photo: m.senderAvatar, initials: (m.senderName || '?').charAt(0) }, 30) + '</span>') +
			'<div class="cm-bwrap"><div class="cm-bubble">' + body + '<div class="cm-meta"><span class="num">' + at + '</span>' + (mine ? '<span class="cm-tick">' + ico('check-check', 14) + '</span>' : '') + '</div></div></div></div>';
	}
	function sideChat(c) {
		var side = c.el.querySelector('[data-side]'), box = side.querySelector('[data-sthread]'), input = side.querySelector('[data-sinput]'), seen = {};
		function add(list) {
			var stick = box.scrollHeight - box.scrollTop - box.clientHeight < 80, added = false;
			(list || []).forEach(function (m) { if (seen[m.id]) return; seen[m.id] = 1; box.insertAdjacentHTML('beforeend', chatMsg(m)); added = true; });
			if (added && stick) box.scrollTop = box.scrollHeight;
		}
		function load() {
			if (!c.convId || c.closed) return;
			get(U.chatMessages + '?c=' + c.convId).then(function (list) { if (list && list.length) { add(list); postForm(U.chatRead, { c: c.convId }); } });
		}
		postForm(U.chatStart, { otherId: c.peer.id }).then(function (r) {
			if (!r || !r.id || c.closed) return;
			c.convId = r.id; load(); box.scrollTop = box.scrollHeight;
			c.chatPoll = setInterval(load, 3000);
		});
		function sendNow(file) {
			var body = input.value.trim(); if (!c.convId || (!body && !file)) return;
			var fd = new FormData(); fd.append('c', c.convId); if (body) fd.append('body', body); if (file) fd.append('file', file, file.name);
			input.value = '';
			postForm(U.chatSend, fd).then(function (dto) { if (dto && dto.id) { add([dto]); box.scrollTop = box.scrollHeight; } else toast(t('cmSendFailed'), 'error'); });
		}
		side.querySelector('[data-ssend]').onclick = function () { sendNow(null); };
		input.addEventListener('keydown', function (e) { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendNow(null); } });
		var fi = side.querySelector('[data-sfileinput]');
		side.querySelector('[data-sattach]').onclick = function () { fi.accept = ''; fi.click(); };
		side.querySelector('[data-sfile]').onclick = function () { fi.accept = '.pdf,.txt,.csv,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.zip'; fi.click(); };
		fi.onchange = function () { var f = fi.files && fi.files[0]; if (f) sendNow(f); fi.value = ''; };
		var emo = side.querySelector('[data-semoji]');
		emo.onclick = function (e) {
			e.stopPropagation();
			var old = side.querySelector('.cbc-emo'); if (old) { old.remove(); return; }
			var pop = document.createElement('div'); pop.className = 'cm-pop cbc-emo';
			pop.innerHTML = '<div class="cm-emoji-grid">' + ['👍', '❤️', '😂', '🙏', '👏', '✅', '🔥', '😊', '🤝', '👌', '🎉', '😀', '🙂', '👀'].map(function (x) { return '<button type="button">' + x + '</button>'; }).join('') + '</div>';
			pop.style.cssText = 'bottom:70px;inset-inline-end:16px;';
			side.appendChild(pop);
			pop.onclick = function (ev) { var b = ev.target.closest('button'); if (!b) return; input.value += b.textContent; input.focus(); pop.remove(); };
		};
	}

	function saveNote(c) {
		if (!c.noteDirty) return Promise.resolve();
		c.noteDirty = false;
		return post(U.note.replace('__id__', c.id), { note: c.note || '' }).then(function (r) { if (!r.ok) { c.noteDirty = true; toast(r.error || t('failed'), 'error'); } });
	}

	function hangup(c) { if (connected()) conn.invoke('Hangup', c.id, false).catch(function () { teardown(c); }); else teardown(c); }

	function teardown(c) {
		if (!c || c.closed) return; c.closed = true;
		clearInterval(c.ringTimer); clearInterval(c.clock); clearInterval(c.quality); clearInterval(c.chatPoll); if (c.stopTone) c.stopTone();
		saveNote(c);
		if (c.rec && c.rec.recorder) recFinish(c);
		stopShare(c);
		if (c.local) c.local.getTracks().forEach(function (x) { x.stop(); });
		if (c.pc) try { c.pc.close(); } catch (e) { }
		if (c.el) c.el.remove();
		if (c.unload) window.removeEventListener('beforeunload', c.unload);
		if (current === c) current = null;
	}

	/* ---------------- recording, with the other side's consent ---------------- */
	function hm(d) { d = d ? new Date(d) : new Date(); return String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0'); }
	function recTrailHtml(c) {
		var steps = (c.recTrail || []).map(function (x) {
			return '<div class="d-flex align-items-center gap-3 mb-3"><span class="cbc-step' + (x.red ? ' red' : '') + '"><i class="ki-outline ' + x.icon + ' fs-5"></i></span><div class="flex-grow-1 fs-7 text-gray-800">' + esc(x.text) + '</div><span class="cbc-num fs-8 text-muted">' + x.at + '</span></div>';
		}).join('');
		return '<div class="fw-bold text-gray-900 mb-4">' + esc(t('recTitle')) + '</div>' + steps;
	}
	function trail(c, icon, text, red) {
		c.recTrail = c.recTrail || []; c.recTrail.push({ icon: icon, text: text, at: hm(), red: !!red });
		var box = c.el && c.el.querySelector('[data-rectrail]'); if (box) { box.classList.remove('d-none'); box.innerHTML = recTrailHtml(c); }
		if (c.el && !red) toast(text, 'info');
	}
	function recChip(c) {
		var chip = c.el.querySelector('[data-recchip]'); if (!chip) return;
		var r = c.rec;
		if (!r || r.state === 'off') { chip.className = 'chip rec d-none'; chip.innerHTML = ''; return; }
		if (r.state === 'asking') { chip.className = 'chip'; chip.innerHTML = '<span class="spinner-border spinner-border-sm"></span>' + esc(t('recWaiting', c.peer.name)); return; }
		chip.className = 'chip rec'; chip.innerHTML = '<span class="dot"></span>' + esc(t('recLabel')) + ' <span class="num">' + mmss(r.secs || 0) + '</span>';
	}
	function recControl(c) {
		var r = c.rec;
		if (r && r.state === 'on') { conn.invoke('RecordStop', r.id).catch(function () { }); return; }
		if (r && r.state === 'asking') { conn.invoke('RecordWithdraw', r.id).catch(function () { }); return; }
		var picture = !!(c.cam || c.sharing || (c.remote && (c.remote.cam || c.remote.share)));
		conn.invoke('RecordRequest', c.id, picture).then(function (res) {
			if (!res || !res.ok) { toast(t(res && res.error ? res.error : 'failed'), 'error'); return; }
			c.rec = { id: res.recId, state: 'asking', kind: res.kind, mine: true };
			trail(c, 'ki-send', t(res.kind === 'video' ? 'recAskedVideo' : 'recAskedVoice'));
			recChip(c);
		}).catch(function () { toast(t('failed'), 'error'); });
	}
	function onRecAsk(m) {
		var c = current; if (!c || c.id !== m.callId) return;
		c.rec = { id: m.recId, state: 'asking', kind: m.kind, mine: false };
		var el = document.createElement('div'); el.className = 'cbc-overlay'; el.setAttribute('role', 'alertdialog'); el.style.zIndex = 2060;
		var p = m.patient, by = m.by || c.peer;
		var rows = [
			['ki-screen', t('recWhat'), t(m.kind === 'video' ? 'recWhatVideo' : 'recWhatVoice')],
			['ki-folder', t('recWhere'), t('recWhereText') + (p ? ' · ' + t('recPatientFile', p.name) : '')],
			['ki-lock-2', t('recWho'), t('recWhoText')],
			['ki-time', t('recKeep'), m.days > 0 ? t('recKeepDays', m.days) : t('recKeepForever')]
		].map(function (r) { return '<div class="d-flex gap-3 mb-4"><span class="cbc-step"><i class="ki-outline ' + r[0] + ' fs-4"></i></span><div><div class="fw-bold text-gray-900 fs-7">' + esc(r[1]) + '</div><div class="text-muted fs-7">' + esc(r[2]) + '</div></div></div>'; }).join('');
		el.innerHTML = '<div class="cbc-card">' +
			'<div class="cbc-band cbc-band-rec"><div class="d-flex align-items-center justify-content-center gap-3 mb-4">' + face(by, 60) + '<span class="text-white">' + ico('rec', 34) + '</span></div>' +
				'<div class="fs-3 fw-bolder">' + esc(t('recAsks', by.name)) + '</div><div class="fs-7 opacity-75 mt-1">' + esc(t('recOnlyIf')) + '</div></div>' +
			'<div class="p-6 text-start">' + rows +
				'<div class="d-flex gap-3 mt-6"><button type="button" class="btn btn-light flex-grow-1" data-no>' + esc(t('recRefuse')) + '</button>' +
				'<button type="button" class="btn btn-danger flex-grow-1" data-yes>' + ico('rec', 16) + ' ' + esc(t('recAgree')) + '</button></div>' +
				'<div class="text-muted fs-8 text-center mt-4">' + esc(t('recConsentNote')) + '</div></div></div>';
		document.body.appendChild(el);
		c.rec.overlay = el;
		el.querySelector('[data-no]').onclick = function () { el.remove(); conn.invoke('RecordAnswer', m.recId, false).catch(function () { }); };
		el.querySelector('[data-yes]').onclick = function () { el.remove(); conn.invoke('RecordAnswer', m.recId, true).catch(function () { }); };
	}
	function onRecClosed(m) {
		var c = current; if (!c || !c.rec || c.rec.id !== m.recId) return;
		if (c.rec.overlay) c.rec.overlay.remove();
		if (c.rec.mine && m.refused) { trail(c, 'ki-cross-circle', t('recRefusedBy', c.peer.name), true); toast(t('recRefusedToast', c.peer.name)); }
		c.rec = null; recChip(c);
	}
	function onRecStart(m) {
		var c = current; if (!c || c.id !== m.callId) return;
		if (c.rec && c.rec.overlay) c.rec.overlay.remove();
		var mine = m.requesterId === (W.me || {}).id, records = (m.recorderId || m.requesterId) === (W.me || {}).id;
		c.rec = { id: m.recId, state: 'on', kind: m.kind, mine: mine, secs: 0 };
		if (mine) trail(c, 'ki-shield-tick', t('recAgreedBy', c.peer.name));
		else trail(c, 'ki-shield-tick', t('recYouAgreed'));
		trail(c, 'ki-notification-status', t('recStarted'), true);
		c.rec.timer = setInterval(function () { c.rec.secs++; recChip(c); }, 1000);
		recChip(c); paintBar(c);
		if (records) recStart(c);   // a phone's request is recorded by this browser
	}
	function onRecStop(m) {
		var c = current; if (!c || !c.rec || c.rec.id !== m.recId) return;
		var byMe = m.byId === (W.me || {}).id;
		trail(c, 'ki-check-circle', byMe ? t('recYouStopped') : t('recStoppedBy', c.peer.name));
		if (c.rec.recorder) recFinish(c);
		else { clearInterval(c.rec.timer); c.rec = null; }
		recChip(c); paintBar(c);
	}

	// The requester's browser records BOTH sides: the voices mixed into one track, and (for a video recording) the two
	// pictures drawn together — the other side full, mine in the corner, as the stage shows them. A shared screen (mine
	// or theirs) takes the whole frame, fitted not cropped so nothing on it is lost, with the faces in the corner.
	function recStream(c) {
		var ac = new (window.AudioContext || window.webkitAudioContext)(), dest = ac.createMediaStreamDestination();
		[c.local, c.remoteStream].forEach(function (st) { if (st && st.getAudioTracks().length) ac.createMediaStreamSource(new MediaStream(st.getAudioTracks())).connect(dest); });
		var tracks = dest.stream.getAudioTracks(), stop = [function () { ac.close(); }];
		if (c.rec.kind === 'video') {
			var cv = document.createElement('canvas'); cv.width = 1280; cv.height = 720;
			var g = cv.getContext('2d'), rv = c.el.querySelector('[data-rvideo]'), sv = document.createElement('video');
			sv.muted = true; sv.playsInline = true; var lv = c.local && c.local.getVideoTracks()[0]; if (lv) { sv.srcObject = new MediaStream([lv]); sv.play().catch(function () { }); }
			var scr = document.createElement('video'), scrOf = null; scr.muted = true; scr.playsInline = true;   // my own shared screen
			function fit(v, x, y, w, h, whole) {
				var vw = v.videoWidth, vh = v.videoHeight; if (!vw || !vh) return false;
				var s = whole ? Math.min(w / vw, h / vh) : Math.max(w / vw, h / vh), dw = vw * s, dh = vh * s; g.drawImage(v, x + (w - dw) / 2, y + (h - dh) / 2, dw, dh); return true;
			}
			function corner(v, i) { var x = 1280 - (320 + 24) * (i + 1), y = 720 - 180 - 24; g.save(); g.beginPath(); g.rect(x, y, 320, 180); g.clip(); g.fillStyle = '#1F2D4D'; g.fillRect(x, y, 320, 180); fit(v, x, y, 320, 180); g.restore(); }
			var theirs = function () { return rv && !rv.classList.contains('d-none') && rv.videoWidth; };
			// a timer, not requestAnimationFrame: it keeps drawing when the tab is in the background
			var draw = setInterval(function () {
				g.fillStyle = '#0F172A'; g.fillRect(0, 0, 1280, 720);
				if (c.sharing && c.screen) {
					if (scrOf !== c.screen) { scrOf = c.screen; scr.srcObject = c.screen; scr.play().catch(function () { }); }
					fit(scr, 0, 0, 1280, 720, true);
					var k = 0; if (theirs() && c.remote && c.remote.cam) corner(rv, k++);
					if (c.cam && lv) corner(sv, k);
					return;
				}
				if (scrOf) { scrOf = null; scr.srcObject = null; }
				var shared = !!(c.remote && c.remote.share);
				if (!(theirs() && fit(rv, 0, 0, 1280, 720, shared))) { g.fillStyle = '#E7F0FF'; g.font = 'bold 40px sans-serif'; g.textAlign = 'center'; g.fillText(c.peer.name || '', 640, 370); }
				if (c.cam && lv) corner(sv, 0);
			}, 1000 / 20);
			stop.push(function () { clearInterval(draw); sv.srcObject = null; scr.srcObject = null; });
			tracks = tracks.concat(cv.captureStream(20).getVideoTracks());
		}
		return { stream: new MediaStream(tracks), stop: function () { stop.forEach(function (f) { try { f(); } catch (e) { } }); } };
	}
	function recStart(c) {
		var mix = recStream(c), kinds = c.rec.kind === 'video' ? ['video/webm;codecs=vp8,opus', 'video/webm'] : ['audio/webm;codecs=opus', 'audio/webm'];
		var type = kinds.filter(function (x) { return MediaRecorder.isTypeSupported(x); })[0];
		if (!type) { toast(t('recUnsupported'), 'error'); conn.invoke('RecordStop', c.rec.id).catch(function () { }); mix.stop(); return; }
		var rec = new MediaRecorder(mix.stream, { mimeType: type, videoBitsPerSecond: 1200000, audioBitsPerSecond: 64000 });
		var r = c.rec, seq = 0, queue = Promise.resolve(), started = Date.now();
		r.recorder = rec; r.mix = mix; r.started = started;
		rec.ondataavailable = function (e) {
			if (!e.data || !e.data.size) return;
			var n = seq++, blob = e.data;
			queue = queue.then(function () {
				return fetch(U.recChunk.replace('__id__', r.id) + '?seq=' + n, { method: 'POST', headers: { 'RequestVerificationToken': token(), 'Content-Type': 'application/octet-stream' }, body: blob })
					.then(function (x) { return x.json(); }).then(function (res) { if (!res.ok) { r.failed = res.error || t('failed'); } }).catch(function () { r.failed = t('lost'); });
			});
		};
		rec.onstop = function () {
			mix.stop();
			queue.then(function () {
				return post(U.recFinish.replace('__id__', r.id), { seconds: Math.round((Date.now() - started) / 1000) });
			}).then(function (res) {
				if (res && res.ok) toast(t('recSaved'), 'success'); else toast((res && res.error) || r.failed || t('failed'), 'error');
				refreshLog();
			});
		};
		rec.start(5000);   // a piece every five seconds: a closed tab loses at most the last few seconds
	}
	function recFinish(c) {
		var r = c.rec; if (!r) return;
		clearInterval(r.timer);
		if (r.recorder && r.recorder.state !== 'inactive') { try { r.recorder.stop(); } catch (e) { } }
		c.rec = null;
	}

	function refreshLog() { document.dispatchEvent(new CustomEvent('cbcalls:changed')); }

	// for hospital-calls-group.js: the same helpers, the same connection, and a quiet close of a two-person call that
	// became a group call
	window.cbCallsKit = {
		t: t, esc: esc, ico: ico, face: face, toast: toast, post: post, get: get, token: token, num: num, mmss: mmss, tone: tone, RING: RING,
		iceServers: iceServers, conn: function () { return conn; }, connected: connected, refreshLog: refreshLog, U: U, W: W,
		current: function () { return current; }, ringing: function () { return !!ring; },
		closeQuietly: function (c) { if (c) { c.quiet = true; teardown(c); } }
	};

	window.cbCalls = {
		start: start,
		connected: connected,
		presenceOf: function (ids) { return connected() ? conn.invoke('PresenceOf', ids) : Promise.resolve([]); },
		onPresence: function (fn) { presenceFns.push(fn); },
		iceServers: iceServers,
		/// The relay test of the settings screen: does this browser get a relay candidate within the time?
		testRelay: function (ms) {
			return iceServers().then(function (servers) {
				return new Promise(function (resolve) {
					if (!servers.length) { resolve(false); return; }
					var pc = new RTCPeerConnection({ iceServers: servers, iceTransportPolicy: 'relay' }), done = false;
					function end(v) { if (done) return; done = true; try { pc.close(); } catch (e) { } resolve(v); }
					pc.onicecandidate = function (e) { if (e.candidate && / typ relay /.test(e.candidate.candidate)) end(true); if (!e.candidate) end(false); };
					pc.createDataChannel('t'); pc.createOffer().then(function (o) { return pc.setLocalDescription(o); });
					setTimeout(function () { end(false); }, ms || 6000);
				});
			});
		}
	};
	if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot); else boot();
})();
