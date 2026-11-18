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
	function ico(k, size) { return '<svg width="' + (size || 20) + '" height="' + (size || 20) + '" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + SVG[k] + '</svg>'; }
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

	/* ---------------- incoming ---------------- */
	function onIncoming(m) {
		if (current || ring || (window.cbCallsGroup && window.cbCallsGroup.busy())) { return; }   // the server already refuses a second call to someone busy; a second tab still answers only once
		var left = m.ringSeconds || 30, video = !!m.video;
		var el = document.createElement('div'); el.className = 'cbc-overlay'; el.setAttribute('role', 'alertdialog');
		var p = m.patient;
		el.innerHTML = '<div class="cbc-card">' +
			'<div class="cbc-band">' +
				'<div class="cbc-kind">' + (video ? ico('cam', 16) : ico('phone', 15)) + esc(video ? t('incomingVideo') : t('incomingVoice')) + '</div>' +
				'<span class="cbc-ring">' + face(m.caller, 100) + '</span>' +
				'<div class="fs-2 fw-bolder mt-6">' + esc(m.caller.name) + '</div>' +
				'<div class="fs-7 opacity-75">' + esc([m.caller.title, m.caller.department].filter(Boolean).join(' · ')) + '</div>' +
			'</div><div class="p-6">' +
				(p || m.reason ? '<div class="cbc-about">' + (p ? face(p, 40) : '<i class="ki-outline ki-message-text fs-2x text-primary"></i>') +
					'<div class="min-w-0 flex-grow-1"><div class="text-muted fs-8">' + esc(p ? t('aboutPatient') : t('reason')) + '</div>' +
					(p ? '<div class="fw-bold text-gray-900 text-truncate">' + esc(p.name) + ' · ' + num(esc(p.mrn)) + '</div>' : '') +
					(m.reason ? '<div class="text-gray-700 fs-7">' + esc(m.reason) + '</div>' : '') + '</div></div>' : '') +
				'<div class="d-flex justify-content-center align-items-start gap-8 mb-5">' +
					'<div><button type="button" class="cbc-round decline" data-decline title="' + esc(t('decline')) + '"><i class="ki-outline ki-cross fs-1 text-white"></i></button><div class="fs-8 text-gray-700 mt-2">' + esc(t('decline')) + '</div></div>' +
					(video ? '<div><button type="button" class="cbc-round ghost" data-voice>' + ico('phone', 24) + '</button><div class="fs-8 text-gray-700 mt-2">' + esc(t('voiceOnly')) + '</div></div>' : '') +
					'<div><button type="button" class="cbc-round accept" data-accept>' + (video ? ico('cam', 24) : ico('phone', 24)) + '</button><div class="fs-8 text-gray-700 mt-2">' + esc(t('answer')) + '</div></div>' +
				'</div>' +
				'<div class="d-flex flex-wrap justify-content-center gap-2">' + [t('quick1'), t('quick2')].map(function (q) { return '<button type="button" class="btn btn-sm btn-light fs-8" data-reply>' + esc(q) + '</button>'; }).join('') + '</div>' +
				'<div class="text-muted fs-8 mt-5" data-left>' + t('ringLeft', num(left)) + '</div>' +
			'</div></div>';
		document.body.appendChild(el);
		var stopTone = tone(RING);
		ring = { callId: m.callId, el: el, stopTone: stopTone, msg: m };
		ring.timer = setInterval(function () {
			left--; var l = el.querySelector('[data-left]'); if (l) l.innerHTML = t('ringLeft', num(Math.max(0, left)));
			if (left <= -5) closeRing();   // the caller's side ends it; this only tidies up if its message was lost
		}, 1000);
		el.querySelector('[data-decline]').onclick = function () { conn.invoke('Decline', m.callId, null); closeRing(); };
		[].slice.call(el.querySelectorAll('[data-reply]')).forEach(function (b) { b.onclick = function () { conn.invoke('Decline', m.callId, b.textContent); closeRing(); toast(t('replySent', m.caller.name), 'success'); }; });
		function accept(withVideo) {
			closeRing();
			conn.invoke('Answer', m.callId, withVideo).then(function (r) {
				if (!r || !r.ok) { toast(t('failed'), 'error'); return; }
				current = newCall({ id: m.callId, role: 'callee', video: withVideo, peer: m.caller, patient: m.patient, reason: m.reason, share: m.share, maxHeight: m.maxHeight, canRecord: m.canRecord });
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
			if (d.t === 'patient' && d.patient && !c.patient) { c.patient = d.patient; paintSide(c); return; }
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
			chip.querySelectorAll('i').forEach(function (b, i) { b.style.opacity = i < q ? 1 : 0.3; });
			chip.querySelector('[data-qt]').textContent = q === 3 ? t('qExcellent') : q === 2 ? t('qGood') : t('qWeak');
		}).catch(function () { });
	}

	/* ---------------- the stage ---------------- */
	function openStage(c) {
		var el = c.el = document.createElement('div'); el.className = 'cbc-stage';
		el.innerHTML =
			'<div class="cbc-main">' +
				'<div class="cbc-top">' + face(c.peer, 40) +
					'<div class="min-w-0"><div class="fw-bold fs-5 text-white text-truncate">' + esc(c.peer.name) + '</div><div class="fs-8 text-white opacity-75 text-truncate">' + esc([c.peer.title, c.peer.department].filter(Boolean).join(' · ')) + '</div></div>' +
					'<span class="cbc-chip ms-3" data-q><span class="cbc-bars"><i style="height:5px"></i><i style="height:9px"></i><i style="height:14px"></i></span><span data-qt>' + esc(t('qExcellent')) + '</span></span>' +
					'<span class="cbc-chip d-none d-md-inline-flex"><i class="ki-outline ki-shield-tick fs-6 text-white"></i>' + esc(t('hospitalOnly')) + '</span>' +
					'<span class="cbc-chip d-none" data-recchip></span>' +
					'<span class="cbc-chip d-none" data-net><span class="spinner-border spinner-border-sm"></span>' + esc(t('reconnecting')) + '</span>' +
					'<span class="ms-auto fs-4 fw-bold cbc-num text-white" dir="ltr" data-clock></span>' +
				'</div>' +
				'<div class="cbc-remote" data-remote><video autoplay playsinline data-rvideo class="d-none"></video><div data-rface></div></div>' +
				'<audio autoplay data-raudio></audio>' +
				'<div class="cbc-self" data-self></div>' +
				'<div class="cbc-bar" data-bar></div>' +
			'</div>' +
			'<div class="cbc-side" data-side></div>';
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
			box.innerHTML = '<div class="text-center"><span class="cbc-ring">' + face(c.peer, 125) + '</span><div class="fs-3 fw-bold mt-6 text-white">' + esc(c.peer.name) + '</div><div class="text-white opacity-75 mt-1">' + esc(t('calling')) + '</div></div>';
			v.classList.add('d-none'); return;
		}
		var vt = c.remoteStream && c.remoteStream.getVideoTracks()[0];
		var showVideo = vt && !vt.muted && (c.remote.cam || c.remote.share);
		if (showVideo) { if (v.srcObject !== c.remoteStream) v.srcObject = c.remoteStream; v.classList.remove('d-none'); v.classList.toggle('cbc-contain', !!c.remote.share); box.innerHTML = ''; v.play().catch(function () { }); }
		else {
			v.classList.add('d-none');
			box.innerHTML = '<div class="text-center">' + face(c.peer, 125) + '<div class="text-white opacity-75 mt-5">' + esc(c.video || c.remote.cam ? t('theirCamOff') : t('voiceCall')) + (c.remote.mic === false ? ' · ' + esc(t('theyMuted')) : '') + '</div></div>';
		}
	}

	function paintSelf(c) {
		if (!c.el) return;
		var box = c.el.querySelector('[data-self]');
		var vt = c.local && c.local.getVideoTracks()[0];
		if (c.sharing) { box.innerHTML = '<div class="text-center text-white"><i class="ki-outline ki-screen fs-2x text-white"></i><div class="fs-8 mt-2">' + esc(t('sharing')) + '</div></div><span class="tag">' + esc(t('you')) + '</span>'; return; }
		if (vt && c.cam) { box.innerHTML = '<video autoplay muted playsinline></video><span class="tag">' + esc(t('you')) + '</span>'; box.querySelector('video').srcObject = new MediaStream([vt]); }
		else box.innerHTML = face(W.me || {}, 60) + '<span class="tag">' + esc(t('you')) + '</span>';
	}

	function paintBar(c) {
		var bar = c.el.querySelector('[data-bar]');
		bar.innerHTML =
			'<button type="button" class="cbc-ctl' + (c.mic ? '' : ' off') + '" data-k="mic" title="' + esc(c.mic ? t('mute') : t('unmute')) + '">' + ico(c.mic ? 'mic' : 'micOff', 22) + '</button>' +
			'<button type="button" class="cbc-ctl' + (c.cam ? '' : ' off') + '" data-k="cam" title="' + esc(t('camera')) + '">' + ico(c.cam ? 'cam' : 'camOff', 22) + '</button>' +
			(c.share !== false && navigator.mediaDevices && navigator.mediaDevices.getDisplayMedia ? '<button type="button" class="cbc-ctl' + (c.sharing ? ' off' : '') + '" data-k="share" title="' + esc(t('share')) + '"><i class="ki-outline ki-screen fs-2 ' + (c.sharing ? '' : 'text-white') + '"></i></button>' : '') +
			(c.canRecord && window.MediaRecorder ? '<button type="button" class="cbc-ctl' + (c.rec && c.rec.state === 'on' ? ' rec-on' : '') + '" data-k="rec" title="' + esc(c.rec && c.rec.state === 'on' ? t('recStop') : t('recAsk')) + '"' + (c.answered ? '' : ' disabled') + '>' + ico('rec', 22) + '</button>' : '') +
			'<button type="button" class="cbc-ctl" data-k="add" title="' + esc(t('addPeople')) + '"' + (c.answered && window.cbCallsGroup ? '' : ' disabled') + '><i class="ki-outline ki-user-tick fs-2 text-white"></i></button>' +
			'<button type="button" class="cbc-ctl end" data-k="end" title="' + esc(t('end')) + '"><span style="display:inline-flex;transform:rotate(135deg)">' + ico('phone', 24) + '</span></button>';
		[].slice.call(bar.querySelectorAll('[data-k]')).forEach(function (b) { b.onclick = function () { control(c, b.getAttribute('data-k')); }; });
	}

	function control(c, k) {
		if (k === 'end') { hangup(c); return; }
		if (k === 'rec') { recControl(c); return; }
		if (k === 'add') { if (window.cbCallsGroup) window.cbCallsGroup.fromCall(c); return; }
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
	function stopShare(c) {
		if (!c.sharing) return;
		c.sharing = false; if (c.screen) c.screen.getTracks().forEach(function (x) { x.stop(); }); c.screen = null;
		var v = c.local && c.local.getVideoTracks()[0];
		if (c.videoTx) c.videoTx.sender.replaceTrack(v || null);
		if (c.el) { paintSelf(c); paintBar(c); } sendState(c);
	}

	function paintSide(c) {
		var side = c.el.querySelector('[data-side]'), p = c.patient;
		var head = '<div class="p-6 border-bottom"><div class="fw-bold fs-5 text-gray-900 mb-1">' + esc(t('context')) + '</div><div class="text-muted fs-7">' + esc(t('contextSub')) + '</div></div>';
		var pat = p
			? '<div class="p-6 border-bottom"><div class="d-flex align-items-center gap-3">' + face(p, 45) + '<div class="min-w-0"><div class="fw-bold text-gray-900 text-truncate">' + esc(p.name) + '</div><div class="text-muted fs-7">' + num(esc(p.mrn)) + '</div></div></div>' +
				(c.reason ? '<div class="text-gray-700 fs-7 mt-3">' + esc(c.reason) + '</div>' : '') +
				'<a class="btn btn-sm btn-light-primary w-100 mt-4" target="_blank" rel="noopener" href="' + esc(U.patient.replace('__id__', p.id)) + '"><i class="ki-outline ki-folder fs-4"></i>' + esc(t('openFile')) + '</a></div>'
			: '<div class="p-6 border-bottom">' + (c.reason ? '<div class="text-gray-700 fs-7 mb-3">' + esc(c.reason) + '</div>' : '') + '<div class="text-muted fs-7 mb-3">' + esc(t('notLinked')) + '</div>' +
				'<div class="position-relative"><i class="ki-outline ki-magnifier fs-3 position-absolute top-50 translate-middle-y ms-4"></i><input class="form-control form-control-solid ps-12" data-psearch placeholder="' + esc(t('searchPatient')) + '" autocomplete="off" /></div>' +
				'<div data-presults class="mt-2"></div></div>';
		var note = '<div class="p-6 border-bottom"><label class="fw-semibold fs-7 text-gray-700 mb-2">' + esc(t('quickNote')) + '</label>' +
			'<textarea class="form-control form-control-solid" rows="4" maxlength="1000" data-note placeholder="' + esc(t('notePh')) + '">' + esc(c.note || '') + '</textarea>' +
			'<div class="text-muted fs-8 mt-2">' + esc(t('noteHelp')) + '</div></div>';
		var who = '<div class="p-6"><div class="fw-bold text-gray-900 mb-3">' + esc(t('inCall')) + '</div>' +
			'<div class="d-flex align-items-center gap-3 mb-3">' + face(W.me || {}, 35) + '<div><div class="fw-bold text-gray-900 fs-7">' + esc((W.me || {}).name || '') + '</div><div class="text-muted fs-8">' + esc(t('you')) + '</div></div></div>' +
			'<div class="d-flex align-items-center gap-3">' + face(c.peer, 35) + '<div><div class="fw-bold text-gray-900 fs-7">' + esc(c.peer.name) + '</div><div class="text-muted fs-8">' + esc(c.peer.title || '') + '</div></div></div></div>';
		side.innerHTML = head + pat + note + '<div class="p-6 border-bottom' + (c.recTrail && c.recTrail.length ? '' : ' d-none') + '" data-rectrail>' + recTrailHtml(c) + '</div>' + who;
		var ta = side.querySelector('[data-note]');
		ta.addEventListener('input', function () { c.note = ta.value; c.noteDirty = true; });
		ta.addEventListener('blur', function () { saveNote(c); });
		var ps = side.querySelector('[data-psearch]');
		if (ps) {
			var tm;
			ps.addEventListener('input', function () {
				clearTimeout(tm); var q = ps.value.trim(), out = side.querySelector('[data-presults]');
				if (q.length < 2) { out.innerHTML = ''; return; }
				tm = setTimeout(function () {
					get(U.patientSearch + '?q=' + encodeURIComponent(q)).then(function (r) {
						var rows = (r && r.results) || [];
						out.innerHTML = rows.slice(0, 6).map(function (x) {
							return '<a href="#" class="d-flex align-items-center gap-3 p-2 rounded cbc-hit" data-pid="' + x.id + '">' + face(x, 30) + '<span class="min-w-0"><span class="d-block fw-semibold text-gray-900 fs-7 text-truncate">' + esc(x.name) + '</span><span class="d-block text-muted fs-8">' + num(esc(x.mrn)) + '</span></span></a>';
						}).join('') || '<div class="text-muted fs-8 p-2">' + esc(t('noMatch')) + '</div>';
						[].slice.call(out.querySelectorAll('[data-pid]')).forEach(function (a) {
							a.onclick = function (e) {
								e.preventDefault();
								post(U.note.replace('__id__', c.id), { patientId: +a.getAttribute('data-pid') }).then(function (res) {
									if (!res.ok) { toast(res.error || t('failed'), 'error'); return; }
									get(U.patientCard.replace('__id__', a.getAttribute('data-pid'))).then(function (pc) {
										if (pc && pc.patient) { c.patient = pc.patient; paintSide(c); send(c, { t: 'patient', patient: pc.patient }); }
									});
								});
							};
						});
					});
				}, 250);
			});
		}
	}

	function saveNote(c) {
		if (!c.noteDirty) return Promise.resolve();
		c.noteDirty = false;
		return post(U.note.replace('__id__', c.id), { note: c.note || '' }).then(function (r) { if (!r.ok) { c.noteDirty = true; toast(r.error || t('failed'), 'error'); } });
	}

	function hangup(c) { if (connected()) conn.invoke('Hangup', c.id, false).catch(function () { teardown(c); }); else teardown(c); }

	function teardown(c) {
		if (!c || c.closed) return; c.closed = true;
		clearInterval(c.ringTimer); clearInterval(c.clock); clearInterval(c.quality); if (c.stopTone) c.stopTone();
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
	}
	function recChip(c) {
		var chip = c.el.querySelector('[data-recchip]'); if (!chip) return;
		var r = c.rec;
		if (!r || r.state === 'off') { chip.className = 'cbc-chip d-none'; chip.innerHTML = ''; return; }
		if (r.state === 'asking') { chip.className = 'cbc-chip'; chip.innerHTML = '<span class="spinner-border spinner-border-sm"></span>' + esc(t('recWaiting', c.peer.name)); return; }
		chip.className = 'cbc-chip cbc-rec'; chip.innerHTML = '<span class="cbc-recdot"></span>' + esc(t('recLabel')) + ' <bdi class="cbc-num" dir="ltr">' + mmss(r.secs || 0) + '</bdi>';
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
		var mine = m.requesterId === (W.me || {}).id;
		c.rec = { id: m.recId, state: 'on', kind: m.kind, mine: mine, secs: 0 };
		if (mine) trail(c, 'ki-shield-tick', t('recAgreedBy', c.peer.name));
		else trail(c, 'ki-shield-tick', t('recYouAgreed'));
		trail(c, 'ki-notification-status', t('recStarted'), true);
		c.rec.timer = setInterval(function () { c.rec.secs++; recChip(c); }, 1000);
		recChip(c); paintBar(c);
		if (mine) recStart(c);
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
