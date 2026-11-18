/* Hospital H176 — group calls, on every screen (loaded by _HospitalCalls after hospital-calls.js).
   Design: the approved prototype (#/calls): the invitation (host in the ring, the others' faces, the patient, the
   recording notice), the grid with the speaker framed, the participants panel (host adds / removes / ends for
   everyone), recording only when EVERY participant agreed.

   With the internal media server configured (the hub hands each person a room token), the group's media goes through
   it instead (LiveKit): each device sends once, the server passes it on; up to twelve people.
   Otherwise each device connects directly to each other one (a mesh, at most four people): the person who JOINS opens a
   connection to each person already in the call; those answer. One audio and one video transceiver per connection
   from the start, so the camera, mute and screen sharing are replaceTrack on every connection, never renegotiation. */
(function () {
	'use strict';
	if (window.cbCallsGroup) return;
	var K = window.cbCallsKit; if (!K) return;
	var t = K.t, esc = K.esc, ico = K.ico, face = K.face, toast = K.toast, num = K.num, mmss = K.mmss;
	var W = K.W, U = K.U;
	var me = (W.me || {}).id;
	var g = null, inv = null;   // the group call on this tab; the invitation ringing on it

	function conn() { return K.conn(); }
	function call(method) { var a = [].slice.call(arguments, 1); var c = conn(); return c ? c.invoke.apply(c, [method].concat(a)) : Promise.reject(); }

	/* ---------------- the invitation ---------------- */
	function onIncoming(m) {
		if (g || inv || K.current() || K.ringing()) return;
		var left = m.ringSeconds || 30, video = !!m.video, joined = m.members.filter(function (x) { return x.state === 'joined'; });
		var others = m.members.filter(function (x) { return x.card && x.card.id !== me && !x.host; });
		var el = document.createElement('div'); el.className = 'cbc-overlay'; el.setAttribute('role', 'alertdialog');
		el.innerHTML = '<div class="cbc-card">' +
			'<div class="cbc-band">' +
				'<div class="cbc-kind"><i class="ki-outline ki-people fs-5 text-white"></i>' + esc(t('grpInvite')) + '</div>' +
				'<span class="cbc-ring">' + face(m.host, 90) + '</span>' +
				'<div class="fs-3 fw-bolder mt-6">' + esc(t('grpInvites', m.host.name)) + '</div>' +
				'<div class="d-flex align-items-center justify-content-center gap-2 mt-3"><span class="cbc-stack">' + others.slice(0, 3).map(function (x) { return face(x.card, 30); }).join('') + '</span>' +
					'<span class="fs-7 opacity-75">' + esc(t('grpWithJoined').replace('{0}', m.members.length).replace('{1}', joined.length)) + '</span></div>' +
			'</div><div class="p-6">' +
				(m.patient || m.reason ? '<div class="cbc-about">' + (m.patient ? face(m.patient, 40) : '<i class="ki-outline ki-message-text fs-2x text-primary"></i>') +
					'<div class="min-w-0 flex-grow-1"><div class="text-muted fs-8">' + esc(m.patient ? t('aboutPatient') : t('reason')) + '</div>' +
					(m.patient ? '<div class="fw-bold text-gray-900 text-truncate">' + esc(m.patient.name) + ' · ' + num(esc(m.patient.mrn)) + '</div>' : '') +
					(m.reason ? '<div class="text-gray-700 fs-7">' + esc(m.reason) + '</div>' : '') + '</div></div>' : '') +
				(m.recording ? '<div class="cbc-recnote"><span class="text-danger">' + ico('rec', 22) + '</span><div class="fs-7 text-gray-800 text-start">' + esc(t('grpRecNotice')) + '</div></div>' : '') +
				'<div class="d-flex justify-content-center align-items-start gap-8 mb-3">' +
					'<div><button type="button" class="cbc-round decline" data-no><i class="ki-outline ki-cross fs-1 text-white"></i></button><div class="fs-8 text-gray-700 mt-2">' + esc(t('grpDecline')) + '</div></div>' +
					(video ? '<div><button type="button" class="cbc-round ghost" data-voice>' + ico('phone', 24) + '</button><div class="fs-8 text-gray-700 mt-2">' + esc(t('grpJoinVoice')) + '</div></div>' : '') +
					'<div><button type="button" class="cbc-round accept" data-yes>' + (video ? ico('cam', 24) : ico('phone', 24)) + '</button><div class="fs-8 text-gray-700 mt-2">' + esc(t('grpJoin')) + '</div></div>' +
				'</div>' +
				'<div class="text-muted fs-8 mt-4" data-left>' + t('ringLeft', num(left)) + '</div>' +
			'</div></div>';
		document.body.appendChild(el);
		inv = { callId: m.callId, el: el, stop: K.tone(K.RING) };
		inv.timer = setInterval(function () {
			left--; var l = el.querySelector('[data-left]'); if (l) l.innerHTML = t('ringLeft', num(Math.max(0, left)));
			if (left <= 0) { var id = inv.callId; closeInv(); call('GroupDecline', id, true).catch(function () { }); toast(t('missedFrom', m.host.name)); }
		}, 1000);
		el.querySelector('[data-no]').onclick = function () { var id = inv.callId; closeInv(); call('GroupDecline', id, false).catch(function () { }); };
		function join(withVideo) {
			var id = inv.callId; closeInv();
			call('GroupJoin', id, withVideo).then(function (r) {
				if (!r || !r.ok) { toast(t(r && r.error ? r.error : 'failed'), 'error'); return; }
				open(r, withVideo);
			}).catch(function () { toast(t('failed'), 'error'); });
		}
		el.querySelector('[data-yes]').onclick = function () { join(video); };
		var v = el.querySelector('[data-voice]'); if (v) v.onclick = function () { join(false); };
	}
	function closeInv() { if (!inv) return; clearInterval(inv.timer); inv.stop(); inv.el.remove(); inv = null; }

	/* ---------------- starting one ---------------- */
	function start(ids, video, patientId, reason) {
		if (g || K.current()) { toast(t('alreadyInCall')); return; }
		if (!window.isSecureContext) { toast(t('insecure'), 'error'); return; }
		call('GroupInvite', ids, !!video, patientId || null, reason || null, null).then(function (r) {
			if (!r || !r.ok) { toast(t(r && r.error ? r.error : 'failed', r && r.name), 'error'); return; }
			if (r.busy && r.busy.length) toast(t('grpBusy', r.busy.join('، ')));
			open(r, !!r.video, true);
		}).catch(function () { toast(t('failed'), 'error'); });
	}
	// a two-person call becomes a group call: pick who to add, the other person comes along without ringing again
	function fromCall(c) {
		pick([c.peer.id], function (ids) {
			call('GroupInvite', ids, !!c.video, c.patient ? c.patient.id : null, c.reason || null, c.id).then(function (r) {
				if (!r || !r.ok) { toast(t(r && r.error ? r.error : 'failed', r && r.name), 'error'); return; }
				K.closeQuietly(c);
				toast(t('grpBecame'));
				open(r, !!r.video, true);
			}).catch(function () { toast(t('failed'), 'error'); });
		});
	}
	function onConvert(m) {
		var c = K.current(); if (!c || c.id !== m.fromCallId) return;
		var video = !!c.video;
		K.closeQuietly(c);
		call('GroupInfo', m.callId).then(function (r) { if (r && r.ok) { toast(t('grpBecame')); open(r, video); } });
	}

	/* ---------------- the stage ---------------- */
	function open(r, video, iAmHost) {
		g = { id: r.callId, video: video, hostId: r.host.id, members: {}, order: [], patient: r.patient, reason: r.reason, cap: r.cap || 4,
			canRecord: r.canRecord, share: r.share !== false, maxHeight: r.maxHeight || 720, mic: true, cam: !!video, sharing: false, secs: 0, rec: null, recTrail: [] };
		(r.members || []).forEach(function (x) { if (x.card && x.card.id !== me) member(x.card, x.state); });
		g.el = document.createElement('div'); g.el.className = 'cbc-stage';
		g.el.innerHTML =
			'<div class="cbc-main">' +
				'<div class="cbc-top"><span class="cbc-step" style="width:40px;height:40px;background:rgba(255,255,255,.15);color:#fff"><i class="ki-outline ki-people fs-2"></i></span>' +
					'<div class="min-w-0"><div class="fw-bold fs-5 text-white">' + esc(t('grpCall')) + ' · <bdi class="cbc-num" dir="ltr" data-count>1</bdi></div><div class="fs-8 text-white opacity-75 text-truncate">' +
						(g.patient ? esc(g.patient.name) + ' · ' + num(esc(g.patient.mrn)) : esc(t('grpHostIs', r.host.name))) + '</div></div>' +
					'<span class="cbc-chip ms-3 d-none d-md-inline-flex"><i class="ki-outline ki-shield-tick fs-6 text-white"></i>' + esc(t('hospitalOnly')) + '</span>' +
					'<span class="cbc-chip d-none" data-recchip></span>' +
					'<span class="ms-auto fs-4 fw-bold cbc-num text-white" dir="ltr" data-clock>00:00</span>' +
				'</div>' +
				'<div class="cbc-grid" data-grid></div>' +
				'<div class="cbc-bar" data-bar></div>' +
			'</div>' +
			'<div class="cbc-side" data-side></div>';
		document.body.appendChild(g.el);
		g.clock = setInterval(function () { g.secs++; g.el.querySelector('[data-clock]').textContent = mmss(g.secs); if (g.rec && g.rec.state === 'on') { g.rec.secs++; recChip(); } }, 1000);
		g.speak = setInterval(speaker, 1000);
		g.ping = setInterval(function () { if (g) call('GroupPing', g.id).catch(function () { }); }, 30000);
		window.addEventListener('beforeunload', g.unload = function () { if (g) call('GroupLeave', g.id).catch(function () { }); });
		paint();
		var mine = g;
		media().then(function () {
			if (g !== mine) return;
			if (r.sfu) return sfuJoin(r.sfu);
			(r.connectTo || []).forEach(function (id) { if (g.members[id]) peer(id, true); });
		}).then(function () {
			if (g !== mine) return;
			if (r.recording) { g.rec = { id: r.recording.recId, state: 'on', mine: false, secs: 0 }; trail('ki-shield-tick', t('grpJoinedConsent')); recChip(); }
			paint();
		});
	}

	function member(card, state) {
		var x = g.members[card.id];
		if (!x) { x = g.members[card.id] = { card: card, state: state, cands: [], remote: { cam: g.video, mic: true, share: false }, level: 0 }; g.order.push(card.id); }
		else { x.card = card; x.state = state; }
		return x;
	}

	function media() {
		var v = g.video ? { height: { ideal: g.maxHeight }, facingMode: 'user' } : false;
		var gum = navigator.mediaDevices && navigator.mediaDevices.getUserMedia ? navigator.mediaDevices.getUserMedia({ audio: true, video: v }) : Promise.reject(new Error('none'));
		return gum.catch(function (e) { if (!v) throw e; g.cam = false; return navigator.mediaDevices.getUserMedia({ audio: true, video: false }); })
			.then(function (s) { g.local = s; }).catch(function (e) { g.local = new MediaStream(); g.mic = false; g.cam = false; toast(t('noMedia', e && e.name ? e.name : ''), 'error'); });
	}

	/* ---------------- through the internal media server (LiveKit) ---------------- */
	function loadLk() {
		if (window.LivekitClient) return Promise.resolve();
		return new Promise(function (ok, no) { var sc = document.createElement('script'); sc.src = U.livekit; sc.onload = ok; sc.onerror = no; document.head.appendChild(sc); });
	}
	function sfuJoin(sfu) {
		return loadLk().then(function () {
			var LK = window.LivekitClient, E = LK.RoomEvent;
			var room = g.room = new LK.Room({ adaptiveStream: true, dynacast: true });
			room.on(E.TrackSubscribed, function (track, pub, part) {
				if (!g) return;
				// their picture may arrive before the hub says they joined: they are added now, and named properly after
				var x = g.members[+part.identity] || member({ id: +part.identity, name: part.name || '', initials: (part.name || '?').slice(0, 2) }, 'joined');
				x.stream = x.stream || new MediaStream();
				if (x.stream.getTracks().indexOf(track.mediaStreamTrack) < 0) x.stream.addTrack(track.mediaStreamTrack);
				track.mediaStreamTrack.onmute = paint; track.mediaStreamTrack.onunmute = paint;
				if (track.kind === 'audio') { var a = audioFor(+part.identity); a.srcObject = x.stream; a.play().catch(function () { }); }
				x.net = 'connected'; paint();
			});
			room.on(E.ParticipantConnected, function () { sendState(); });   // whoever enters learns my camera and screen
			room.on(E.TrackUnsubscribed, function (track, pub, part) {
				var x = g && g.members[+part.identity]; if (!x || !x.stream) return;
				try { x.stream.removeTrack(track.mediaStreamTrack); } catch (e) { }
				paint();
			});
			room.on(E.ActiveSpeakersChanged, function (speakers) {
				if (!g) return;
				var other = (speakers || []).filter(function (p) { return +p.identity !== me; })[0];
				var id = other ? String(+other.identity) : null;
				if (id !== g.speaking) { g.speaking = id; paintTiles(); }
			});
			room.on(E.Reconnecting, function () { Object.keys(g.members).forEach(function (id) { g.members[id].net = 'connecting'; }); paint(); });
			room.on(E.Reconnected, function () { Object.keys(g.members).forEach(function (id) { g.members[id].net = 'connected'; }); paint(); });
			return room.connect(sfu.url, sfu.token).then(function () {
				var a = g.local.getAudioTracks()[0], v = g.local.getVideoTracks()[0], S = LK.Track.Source;
				var jobs = [];
				if (a) jobs.push(room.localParticipant.publishTrack(a, { source: S.Microphone }).then(function (p) { g.pubA = p; }));
				if (v) jobs.push(room.localParticipant.publishTrack(v, { source: S.Camera }).then(function (p) { g.pubV = p; }));
				return Promise.all(jobs);
			}).then(function () { sendState(); }).catch(function () { toast(t('lost'), 'error'); });
		});
	}
	// the picture this device sends: the camera, or the screen while sharing — on every connection, or on the media server
	function sendVideo(track) {
		if (g.room) {
			if (g.pubV && g.pubV.track) return g.pubV.track.replaceTrack(track || g.local.getVideoTracks()[0]).catch(function () { });
			if (track) return g.room.localParticipant.publishTrack(track, { source: window.LivekitClient.Track.Source.Camera }).then(function (p) { g.pubV = p; });
			return;
		}
		eachTx(function (tx) { tx.sender.replaceTrack(track || null); });
	}

	// One connection per pair. The joiner offers; the others answer.
	function peer(id, offer) {
		var x = g.members[id]; if (!x || x.pc) return x && x.ready;
		x.ready = K.iceServers().then(function (servers) {
			var pc = x.pc = new RTCPeerConnection({ iceServers: servers });
			x.stream = new MediaStream();
			pc.onicecandidate = function (e) { if (e.candidate) send(id, { t: 'cand', c: e.candidate }); };
			pc.ontrack = function (e) {
				if (x.stream.getTracks().indexOf(e.track) < 0) x.stream.addTrack(e.track);
				e.track.onunmute = paint; e.track.onmute = paint;
				if (e.track.kind === 'audio') { var a = audioFor(id); a.srcObject = x.stream; a.play().catch(function () { }); }
				paint();
			};
			pc.onconnectionstatechange = function () { x.net = pc.connectionState; paint(); };
			if (offer) {
				var a = g.local.getAudioTracks()[0], v = currentVideo();
				x.audioTx = pc.addTransceiver(a || 'audio', { direction: 'sendrecv', streams: [g.local] });
				x.videoTx = pc.addTransceiver(v || 'video', { direction: 'sendrecv', streams: [g.local] });
				return pc.createOffer().then(function (o) { return pc.setLocalDescription(o); }).then(function () { send(id, { t: 'offer', sdp: pc.localDescription }); });
			}
		});
		return x.ready;
	}
	function audioFor(id) {
		var a = g.el.querySelector('audio[data-a="' + id + '"]');
		if (!a) { a = document.createElement('audio'); a.autoplay = true; a.setAttribute('data-a', id); g.el.appendChild(a); }
		return a;
	}
	function currentVideo() { return g.sharing && g.screen ? g.screen.getVideoTracks()[0] : (g.local && g.local.getVideoTracks()[0]) || null; }
	function send(id, obj) { call('GroupSignal', g.id, id, JSON.stringify(obj)).catch(function () { }); }
	// mic, camera and screen state goes through the hub: to each connected device in a mesh, to everyone joined on the media server
	function sendState() { Object.keys(g.members).forEach(function (id) { var x = g.members[id]; if (x.pc || (g.room && x.state === 'joined')) send(+id, { t: 'state', mic: g.mic, cam: g.cam, share: g.sharing }); }); }

	function onSignal(m) {
		if (!g || g.id !== m.callId) return;
		var d; try { d = JSON.parse(m.payload); } catch (e) { return; }
		var x = g.members[m.fromId]; if (!x) return;
		if (d.t === 'state' && g.room) { x.remote = { mic: d.mic, cam: d.cam, share: d.share }; paint(); return; }
		if (d.t === 'offer') {
			peer(m.fromId, false).then(function () {
				var pc = x.pc;
				return pc.setRemoteDescription(d.sdp).then(function () {
					pc.getTransceivers().forEach(function (tx) {
						var k = tx.receiver.track && tx.receiver.track.kind;
						tx.direction = 'sendrecv';
						if (k === 'audio') { x.audioTx = tx; var a = g.local.getAudioTracks()[0]; if (a) tx.sender.replaceTrack(a); }
						if (k === 'video') { x.videoTx = tx; var v = currentVideo(); if (v) tx.sender.replaceTrack(v); }
					});
					return pc.createAnswer();
				}).then(function (ans) { return pc.setLocalDescription(ans); }).then(function () { send(m.fromId, { t: 'answer', sdp: pc.localDescription }); flush(x); sendState(); });
			}).catch(function () { });
			return;
		}
		(x.ready || Promise.resolve()).then(function () {
			if (!x.pc) return;
			if (d.t === 'answer') return x.pc.setRemoteDescription(d.sdp).then(function () { flush(x); sendState(); });
			if (d.t === 'cand') { if (x.pc.remoteDescription) return x.pc.addIceCandidate(d.c).catch(function () { }); x.cands.push(d.c); return; }
			if (d.t === 'state') { x.remote = { mic: d.mic, cam: d.cam, share: d.share }; paint(); }
		}).catch(function () { });
	}
	function flush(x) { var l = x.cands; x.cands = []; l.forEach(function (c) { x.pc.addIceCandidate(c).catch(function () { }); }); }

	// who is speaking: the loudest incoming audio above a floor, from each connection's own statistics
	function speaker() {
		if (!g || g.room) return;   // the media server reports who speaks
		var ids = Object.keys(g.members).filter(function (id) { return g.members[id].pc; });
		Promise.all(ids.map(function (id) {
			return g.members[id].pc.getStats().then(function (st) { var lv = 0; st.forEach(function (r) { if (r.type === 'inbound-rtp' && r.kind === 'audio' && r.audioLevel != null) lv = r.audioLevel; }); g.members[id].level = lv; });
		})).then(function () {
			var best = null, top = 0.04;
			ids.forEach(function (id) { if (g.members[id].level > top) { top = g.members[id].level; best = id; } });
			if (best !== g.speaking) { g.speaking = best; paintTiles(); }
		}).catch(function () { });
	}

	/* ---------------- painting ---------------- */
	function inCall() { return g.order.filter(function (id) { var x = g.members[id]; return x && (x.state === 'joined' || x.state === 'ringing'); }); }
	function paint() { if (!g || !g.el) return; paintTiles(); paintBar(); paintSide(); }
	function paintTiles() {
		var grid = g.el.querySelector('[data-grid]'), ids = inCall(), n = ids.length + 1;
		grid.style.setProperty('--cols', n <= 4 ? 2 : 3);
		g.el.querySelector('[data-count]').textContent = 1 + ids.filter(function (id) { return g.members[id].state === 'joined'; }).length;
		var wanted = ['me'].concat(ids);
		[].slice.call(grid.children).forEach(function (el) { if (wanted.indexOf(el.getAttribute('data-tile')) < 0) el.remove(); });
		wanted.forEach(function (key) {
			var el = grid.querySelector('[data-tile="' + key + '"]');
			if (!el) { el = document.createElement('div'); el.className = 'cbc-tile'; el.setAttribute('data-tile', key); el.innerHTML = '<video autoplay playsinline muted></video><div data-f></div><span class="cbc-tname"></span><span class="cbc-tstate"></span>'; grid.appendChild(el); }
			var v = el.querySelector('video'), f = el.querySelector('[data-f]'), nm = el.querySelector('.cbc-tname'), stt = el.querySelector('.cbc-tstate');
			if (key === 'me') {
				var lv = g.local && g.local.getVideoTracks()[0];
				var showMe = (lv && g.cam) || g.sharing;
				if (showMe) { var src = g.sharing && g.screen ? g.screen : new MediaStream([lv]); if (v.srcObject !== src && !(v.srcObject && v.srcObject.getVideoTracks()[0] === src.getVideoTracks()[0])) v.srcObject = src; v.classList.remove('d-none'); v.classList.toggle('cbc-mirror', !g.sharing); v.classList.toggle('cbc-contain', !!g.sharing); f.innerHTML = ''; }
				else { v.classList.add('d-none'); f.innerHTML = face(W.me || {}, 84); }
				nm.innerHTML = (g.mic ? '' : '<span class="cbc-tmute">' + ico('micOff', 14) + '</span>') + esc(t('you'));
				stt.className = 'cbc-tstate d-none';
				el.classList.remove('speaking', 'ringing');
				return;
			}
			var x = g.members[key], vt = x.stream && x.stream.getVideoTracks()[0];
			var show = x.state === 'joined' && vt && !vt.muted && (x.remote.cam || x.remote.share);
			if (show) { if (v.srcObject !== x.stream) v.srcObject = x.stream; v.classList.remove('d-none'); v.classList.toggle('cbc-contain', !!x.remote.share); v.classList.remove('cbc-mirror'); f.innerHTML = ''; v.play().catch(function () { }); }
			else { v.classList.add('d-none'); f.innerHTML = face(x.card, 84); }
			nm.innerHTML = (x.state === 'joined' && x.remote.mic === false ? '<span class="cbc-tmute">' + ico('micOff', 14) + '</span>' : '') + esc(x.card.name);
			var label = x.state === 'ringing' ? t('grpRinging') : (x.away || x.net === 'connecting' || x.net === 'disconnected' ? t('reconnecting') : '');
			stt.textContent = label; stt.className = 'cbc-tstate' + (label ? '' : ' d-none');
			el.classList.toggle('ringing', x.state === 'ringing');
			el.classList.toggle('speaking', String(g.speaking) === String(key));
		});
	}
	function paintBar() {
		var bar = g.el.querySelector('[data-bar]'), r = g.rec;
		bar.innerHTML =
			'<button type="button" class="cbc-ctl' + (g.mic ? '' : ' off') + '" data-k="mic" title="' + esc(g.mic ? t('mute') : t('unmute')) + '">' + ico(g.mic ? 'mic' : 'micOff', 22) + '</button>' +
			'<button type="button" class="cbc-ctl' + (g.cam ? '' : ' off') + '" data-k="cam" title="' + esc(t('camera')) + '">' + ico(g.cam ? 'cam' : 'camOff', 22) + '</button>' +
			(g.share && navigator.mediaDevices && navigator.mediaDevices.getDisplayMedia ? '<button type="button" class="cbc-ctl' + (g.sharing ? ' off' : '') + '" data-k="share" title="' + esc(t('share')) + '"><i class="ki-outline ki-screen fs-2 ' + (g.sharing ? '' : 'text-white') + '"></i></button>' : '') +
			(g.canRecord && window.MediaRecorder ? '<button type="button" class="cbc-ctl' + (r && r.state === 'on' ? ' rec-on' : '') + '" data-k="rec" title="' + esc(r && r.state === 'on' ? t('recStop') : t('grpRecAsk')) + '">' + ico('rec', 22) + '</button>' : '') +
			(g.hostId === me ? '<button type="button" class="cbc-ctl" data-k="add" title="' + esc(t('addPeople')) + '"><i class="ki-outline ki-user-tick fs-2 text-white"></i></button>' : '') +
			'<button type="button" class="cbc-ctl end" data-k="end" title="' + esc(t('grpLeave')) + '"><span style="display:inline-flex;transform:rotate(135deg)">' + ico('phone', 24) + '</span></button>';
		[].slice.call(bar.querySelectorAll('[data-k]')).forEach(function (b) { b.onclick = function () { control(b.getAttribute('data-k')); }; });
	}
	function stateBadge(s) {
		var map = { joined: ['badge-light-primary', 'grpStJoined'], ringing: ['badge-light', 'grpRinging'], declined: ['badge-light-danger', 'grpStDeclined'], missed: ['badge-light', 'grpStMissed'],
			busy: ['badge-light-danger', 'grpStBusy'], left: ['badge-light', 'grpStLeft'], removed: ['badge-light', 'grpStRemoved'] }[s] || ['badge-light', 'grpStLeft'];
		return '<span class="badge ' + map[0] + ' fw-bold fs-8">' + esc(t(map[1])) + '</span>';
	}
	function paintSide() {
		var side = g.el.querySelector('[data-side]'), host = g.hostId === me;
		var rows = '<div class="d-flex align-items-center gap-3 mb-4">' + face(W.me || {}, 35) + '<div class="flex-grow-1 min-w-0"><div class="fw-bold text-gray-900 fs-7">' + esc((W.me || {}).name || '') + '</div><div class="text-muted fs-8">' + esc(t('you')) + '</div></div>' +
			(host ? '<span class="badge badge-light-primary fw-bold fs-8">' + esc(t('grpHost')) + '</span>' : '') + '</div>' +
			g.order.map(function (id) {
				var x = g.members[id];
				return '<div class="d-flex align-items-center gap-3 mb-4">' + face(x.card, 35) + '<div class="flex-grow-1 min-w-0"><div class="fw-bold text-gray-900 fs-7 text-truncate">' + esc(x.card.name) + '</div><div class="text-muted fs-8 text-truncate">' + esc(x.card.title || '') + '</div></div>' +
					(+id === g.hostId ? '<span class="badge badge-light-primary fw-bold fs-8">' + esc(t('grpHost')) + '</span>' : stateBadge(x.state)) +
					(host && (x.state === 'joined' || x.state === 'ringing') ? '<button type="button" class="btn btn-icon btn-sm btn-light" title="' + esc(t('grpRemove')) + '" data-rm="' + id + '"><i class="ki-outline ki-cross fs-5"></i></button>' : '') + '</div>';
			}).join('');
		side.innerHTML =
			'<div class="p-6 border-bottom"><div class="d-flex flex-stack mb-1"><div class="fw-bold fs-5 text-gray-900">' + esc(t('grpPeople')) + '</div><span class="badge badge-light-primary fs-8">' + esc(t('grpUpTo', g.cap)) + '</span></div>' +
				'<div class="text-muted fs-7">' + esc(t('grpPeopleSub')) + '</div></div>' +
			'<div class="p-6 border-bottom">' + rows + '</div>' +
			(host ? '<div class="p-6 border-bottom"><button type="button" class="btn btn-sm btn-light-primary w-100" data-addside><i class="ki-outline ki-user-tick fs-4"></i>' + esc(t('addPeople')) + '</button></div>' : '') +
			'<div class="p-6 border-bottom' + (g.recTrail.length ? '' : ' d-none') + '" data-rectrail>' + trailHtml() + '</div>' +
			(g.patient ? '<div class="p-6 border-bottom"><div class="d-flex align-items-center gap-3">' + face(g.patient, 40) + '<div class="min-w-0"><div class="fw-bold text-gray-900 text-truncate">' + esc(g.patient.name) + '</div><div class="text-muted fs-8">' + num(esc(g.patient.mrn)) + '</div></div></div>' +
				'<a class="btn btn-sm btn-light-primary w-100 mt-3" target="_blank" rel="noopener" href="' + esc(U.patient.replace('__id__', g.patient.id)) + '"><i class="ki-outline ki-folder fs-4"></i>' + esc(t('openFile')) + '</a></div>' : '') +
			(host ? '<div class="p-6"><button type="button" class="btn btn-sm btn-light-danger w-100" data-endall>' + esc(t('grpEndAll')) + '</button></div>' : '');
		[].slice.call(side.querySelectorAll('[data-rm]')).forEach(function (b) { b.onclick = function () { call('GroupRemove', g.id, +b.getAttribute('data-rm')).catch(function () { }); }; });
		var a = side.querySelector('[data-addside]'); if (a) a.onclick = add;
		var e = side.querySelector('[data-endall]'); if (e) e.onclick = function () { call('GroupEnd', g.id).catch(function () { }); };
	}

	function control(k) {
		if (k === 'end') { leave(); return; }
		if (k === 'add') { add(); return; }
		if (k === 'rec') { recControl(); return; }
		if (k === 'mic') { var a = g.local && g.local.getAudioTracks()[0]; if (!a) { toast(t('noMic'), 'error'); return; } g.mic = !g.mic; a.enabled = g.mic; if (g.pubA) (g.mic ? g.pubA.unmute() : g.pubA.mute()); }
		if (k === 'cam') {
			var v = g.local && g.local.getVideoTracks()[0];
			if (v) { g.cam = !g.cam; v.enabled = g.cam; if (g.pubV && !g.sharing) (g.cam ? g.pubV.unmute() : g.pubV.mute()); }
			else {
				navigator.mediaDevices.getUserMedia({ video: { height: { ideal: g.maxHeight } } }).then(function (s) {
					var nv = s.getVideoTracks()[0]; g.local.addTrack(nv); g.cam = true;
					if (!g.sharing) sendVideo(nv);
					paint(); sendState();
				}).catch(function (e) { toast(t('noMedia', e.name || ''), 'error'); });
				return;
			}
		}
		if (k === 'share') {
			if (g.sharing) { stopShare(); return; }
			navigator.mediaDevices.getDisplayMedia({ video: true }).then(function (s) {
				g.screen = s; g.sharing = true; var st = s.getVideoTracks()[0];
				if (g.pubV && !g.cam) g.pubV.unmute();
				sendVideo(st);
				st.onended = stopShare; paint(); sendState();
			}).catch(function () { });
			return;
		}
		paint(); sendState();
	}
	function eachTx(f) { Object.keys(g.members).forEach(function (id) { var x = g.members[id]; if (x.videoTx) f(x.videoTx); }); }
	function stopShare() {
		if (!g || !g.sharing) return;
		g.sharing = false; if (g.screen) g.screen.getTracks().forEach(function (x) { x.stop(); }); g.screen = null;
		var v = g.local && g.local.getVideoTracks()[0];
		sendVideo(v || null);
		if (g.pubV && !g.cam) g.pubV.mute();
		paint(); sendState();
	}

	function add() {
		var have = [me].concat(g.order.filter(function (id) { var s = g.members[id].state; return s === 'joined' || s === 'ringing'; }).map(Number));
		var room = g.cap - have.length;
		if (room <= 0) { toast(t('grpFull', g.cap)); return; }
		pick(have, function (ids) {
			call('GroupAdd', g.id, ids).then(function (r) {
				if (!r || !r.ok) { toast(t(r && r.error ? r.error : 'failed', r && r.name), 'error'); return; }
				if (r.busy && r.busy.length) toast(t('grpBusy', r.busy.join('، ')));
			});
		}, room);
	}

	function leave() { if (!g) return; var id = g.id; call('GroupLeave', id).catch(function () { }); close(); toast(t('grpYouLeft')); K.refreshLog(); }
	function close() {
		if (!g) return;
		var x = g; g = null;
		clearInterval(x.clock); clearInterval(x.speak); clearInterval(x.ping);
		if (x.rec && x.rec.recorder) recFinish(x);
		if (x.screen) x.screen.getTracks().forEach(function (s) { s.stop(); });
		if (x.local) x.local.getTracks().forEach(function (s) { s.stop(); });
		Object.keys(x.members).forEach(function (id) { if (x.members[id].pc) try { x.members[id].pc.close(); } catch (e) { } });
		if (x.room) try { x.room.disconnect(); } catch (e) { }
		if (x.el) x.el.remove();
		if (x.unload) window.removeEventListener('beforeunload', x.unload);
	}

	/* ---------------- what the hub tells us ---------------- */
	function onJoined(m) { if (!g || g.id !== m.callId || !m.card) return; member(m.card, 'joined'); trailIfRecording(m.card.name); paint(); }   // they will offer
	function onLeft(m) {
		if (!g || g.id !== m.callId) return;
		var x = g.members[m.employeeId]; if (!x) return;
		x.state = m.removed ? 'removed' : 'left';
		if (x.pc) try { x.pc.close(); } catch (e) { } x.pc = null; x.stream = null; x.videoTx = null; x.audioTx = null; x.ready = null;
		var a = g.el.querySelector('audio[data-a="' + m.employeeId + '"]'); if (a) a.remove();
		toast(t(m.removed ? 'grpWasRemoved' : 'grpHasLeft', x.card.name));
		paint();
	}
	function onState(m) { if (!g || g.id !== m.callId || !g.members[m.employeeId]) return; g.members[m.employeeId].state = m.state; paint(); }
	function onMembers(m) { if (!g || g.id !== m.callId) return; (m.members || []).forEach(function (x) { if (x.card && x.card.id !== me && (!g.members[x.card.id] || g.members[x.card.id].state !== 'joined')) member(x.card, x.state); }); paint(); }
	function onHost(m) { if (!g || g.id !== m.callId) return; g.hostId = m.hostId; if (m.hostId === me) toast(t('grpYouHost')); paint(); }
	function onEnded(m) {
		if (inv && inv.callId === m.callId) { closeInv(); return; }
		if (!g || g.id !== m.callId) return;
		close();
		toast(t(m.status === 'Removed' ? 'grpYouRemoved' : 'grpEnded'), m.status === 'Removed' ? 'info' : 'success');
		K.refreshLog();
	}

	/* ---------------- a group recording: everyone agrees ---------------- */
	function trailHtml() {
		return '<div class="fw-bold text-gray-900 mb-4">' + esc(t('recTitle')) + '</div>' + g.recTrail.map(function (x) {
			return '<div class="d-flex align-items-center gap-3 mb-3"><span class="cbc-step' + (x.red ? ' red' : '') + '"><i class="ki-outline ' + x.icon + ' fs-5"></i></span><div class="flex-grow-1 fs-7 text-gray-800">' + esc(x.text) + '</div><span class="cbc-num fs-8 text-muted">' + x.at + '</span></div>';
		}).join('');
	}
	function hm() { var d = new Date(); return String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0'); }
	function trail(icon, text, red) { g.recTrail.push({ icon: icon, text: text, at: hm(), red: !!red }); var b = g.el && g.el.querySelector('[data-rectrail]'); if (b) { b.classList.remove('d-none'); b.innerHTML = trailHtml(); } }
	function trailIfRecording(name) { if (g.rec && g.rec.state === 'on') trail('ki-shield-tick', t('grpJoinedAgreeing', name)); }
	function recChip() {
		var chip = g.el.querySelector('[data-recchip]'), r = g.rec;
		if (!r) { chip.className = 'cbc-chip d-none'; return; }
		if (r.state === 'asking') { chip.className = 'cbc-chip'; chip.innerHTML = '<span class="spinner-border spinner-border-sm"></span>' + esc(t('grpRecWaiting', r.waiting)); return; }
		chip.className = 'cbc-chip cbc-rec'; chip.innerHTML = '<span class="cbc-recdot"></span>' + esc(t('recLabel')) + ' <bdi class="cbc-num" dir="ltr">' + mmss(r.secs || 0) + '</bdi>';
	}
	function recControl() {
		var r = g.rec;
		if (r) { call('GroupRecordStop', r.id).catch(function () { }); return; }
		var picture = !!(g.cam || g.sharing || Object.keys(g.members).some(function (id) { var r = g.members[id].remote; return r && (r.cam || r.share); }));
		call('GroupRecordRequest', g.id, picture).then(function (res) {
			if (!res || !res.ok) { toast(t(res && res.error ? res.error : 'failed'), 'error'); return; }
			g.rec = { id: res.recId, state: 'asking', kind: res.kind, mine: true, waiting: res.waiting, secs: 0 };
			trail('ki-send', t('grpRecAsked'));
			recChip(); paintBar();
		});
	}
	function onRecAsk(m) {
		if (!g || g.id !== m.callId || !m.group) return;
		g.rec = { id: m.recId, state: 'asking', kind: m.kind, mine: false, waiting: m.waiting, secs: 0 };
		var by = m.by || {}, p = m.patient;
		var el = document.createElement('div'); el.className = 'cbc-overlay'; el.style.zIndex = 2060;
		var rows = [
			['ki-screen', t('recWhat'), t(m.kind === 'video' ? 'grpRecWhatVideo' : 'grpRecWhatVoice')],
			['ki-folder', t('recWhere'), t('recWhereText') + (p ? ' · ' + t('recPatientFile', p.name) : '')],
			['ki-lock-2', t('recWho'), t('grpRecWho')],
			['ki-time', t('recKeep'), m.days > 0 ? t('recKeepDays', m.days) : t('recKeepForever')]
		].map(function (r) { return '<div class="d-flex gap-3 mb-4"><span class="cbc-step"><i class="ki-outline ' + r[0] + ' fs-4"></i></span><div><div class="fw-bold text-gray-900 fs-7">' + esc(r[1]) + '</div><div class="text-muted fs-7">' + esc(r[2]) + '</div></div></div>'; }).join('');
		el.innerHTML = '<div class="cbc-card"><div class="cbc-band cbc-band-rec"><div class="d-flex align-items-center justify-content-center gap-3 mb-4">' + face(by, 60) + '<span class="text-white">' + ico('rec', 34) + '</span></div>' +
			'<div class="fs-3 fw-bolder">' + esc(t('recAsks', by.name || '')) + '</div><div class="fs-7 opacity-75 mt-1">' + esc(t('grpRecOnlyIfAll')) + '</div></div>' +
			'<div class="p-6 text-start">' + rows + '<div class="d-flex gap-3 mt-6"><button type="button" class="btn btn-light flex-grow-1" data-no>' + esc(t('recRefuse')) + '</button>' +
			'<button type="button" class="btn btn-danger flex-grow-1" data-yes>' + ico('rec', 16) + ' ' + esc(t('recAgree')) + '</button></div>' +
			'<div class="text-muted fs-8 text-center mt-4">' + esc(t('recConsentNote')) + '</div></div></div>';
		document.body.appendChild(el);
		g.rec.overlay = el;
		el.querySelector('[data-no]').onclick = function () { el.remove(); call('GroupRecordAnswer', m.recId, false).catch(function () { }); };
		el.querySelector('[data-yes]').onclick = function () { el.remove(); call('GroupRecordAnswer', m.recId, true).catch(function () { }); };
		recChip();
	}
	function onRecAgreed(m) {
		if (!g || !g.rec || g.rec.id !== m.recId) return;
		var x = g.members[m.employeeId];
		trail('ki-shield-tick', m.employeeId === me ? t('recYouAgreed') : t('recAgreedBy', x ? x.card.name : ''));
		if (g.rec.waiting > 0) g.rec.waiting--; recChip();
	}
	function onRecClosed(m) {
		if (!g || !g.rec || g.rec.id !== m.recId) return;
		if (g.rec.overlay) g.rec.overlay.remove();
		if (m.refused) { trail('ki-cross-circle', t('recRefusedBy', m.byName || ''), true); toast(t('recRefusedToast', m.byName || '')); }
		g.rec = null; recChip(); paintBar();
	}
	function onRecStart(m) {
		if (!g || g.id !== m.callId || !m.group) return;
		if (g.rec && g.rec.overlay) g.rec.overlay.remove();
		g.rec = { id: m.recId, state: 'on', kind: m.kind, mine: m.requesterId === me, secs: 0 };
		trail('ki-notification-status', t('grpRecStarted'), true);
		recChip(); paintBar();
		if (g.rec.mine) recStart();
	}
	function onRecStop(m) {
		if (!g || !g.rec || g.rec.id !== m.recId) return;
		trail('ki-check-circle', m.byId === me ? t('recYouStopped') : t('recStoppedBy', (g.members[m.byId] || { card: {} }).card.name || ''));
		if (g.rec.recorder) recFinish(g); else g.rec = null;
		recChip(); paintBar();
	}
	// The requester's browser records everyone: all voices mixed, and (video) the grid as it is drawn on its own stage.
	function recStart() {
		var ac = new (window.AudioContext || window.webkitAudioContext)(), dest = ac.createMediaStreamDestination();
		var mixed = [];
		function addAudio(st) { if (st && st.getAudioTracks().length && mixed.indexOf(st) < 0) { mixed.push(st); ac.createMediaStreamSource(new MediaStream(st.getAudioTracks())).connect(dest); } }
		addAudio(g.local);
		var feed = setInterval(function () { Object.keys(g.members).forEach(function (id) { addAudio(g.members[id].stream); }); }, 1000);   // people who join later are heard too
		Object.keys(g.members).forEach(function (id) { addAudio(g.members[id].stream); });
		var tracks = dest.stream.getAudioTracks(), stops = [function () { clearInterval(feed); ac.close(); }];
		if (g.rec.kind === 'video') {
			var cv = document.createElement('canvas'); cv.width = 1280; cv.height = 720; var c2 = cv.getContext('2d');
			var draw = setInterval(function () {
				if (!g) return;
				c2.fillStyle = '#0F172A'; c2.fillRect(0, 0, 1280, 720);
				var tiles = [].slice.call(g.el.querySelectorAll('[data-grid] .cbc-tile'));
				function live(tile) { var v = tile.querySelector('video'); return v && !v.classList.contains('d-none') && v.videoWidth ? v : null; }
				function box(tile, x, y, w, h, whole, label) {
					var v = live(tile);
					c2.save(); c2.beginPath(); c2.rect(x + 4, y + 4, w - 8, h - 8); c2.clip();
					c2.fillStyle = '#1F2D4D'; c2.fillRect(x, y, w, h);
					if (v) { var s = whole ? Math.min(w / v.videoWidth, h / v.videoHeight) : Math.max(w / v.videoWidth, h / v.videoHeight); c2.drawImage(v, x + (w - v.videoWidth * s) / 2, y + (h - v.videoHeight * s) / 2, v.videoWidth * s, v.videoHeight * s); }
					c2.restore();
					c2.fillStyle = '#fff'; c2.font = 'bold ' + label + 'px sans-serif'; c2.textAlign = 'start'; c2.fillText((tile.querySelector('.cbc-tname') || {}).textContent || '', x + 16, y + h - 18);
				}
				// a shared screen is the main picture, whole; the people go in a column beside it
				var screen = tiles.filter(function (tile) { var v = live(tile); return v && v.classList.contains('cbc-contain'); })[0];
				if (screen) {
					box(screen, 0, 0, 1000, 720, true, 22);
					var rest = tiles.filter(function (tile) { return tile !== screen; }).slice(0, 4), rh = 720 / Math.max(rest.length, 1);
					rest.forEach(function (tile, i) { box(tile, 1000, i * rh, 280, Math.min(rh, 158), false, 16); });
					return;
				}
				var n = tiles.length, cols = n <= 4 ? 2 : 3, rows = Math.ceil(n / cols), w = 1280 / cols, h = 720 / rows;
				tiles.forEach(function (tile, i) { box(tile, (i % cols) * w, Math.floor(i / cols) * h, w, h, false, 22); });
			}, 1000 / 20);
			stops.push(function () { clearInterval(draw); });
			tracks = tracks.concat(cv.captureStream(20).getVideoTracks());
		}
		var kinds = g.rec.kind === 'video' ? ['video/webm;codecs=vp8,opus', 'video/webm'] : ['audio/webm;codecs=opus', 'audio/webm'];
		var type = kinds.filter(function (x) { return MediaRecorder.isTypeSupported(x); })[0];
		if (!type) { toast(t('recUnsupported'), 'error'); call('GroupRecordStop', g.rec.id).catch(function () { }); stops.forEach(function (f) { f(); }); return; }
		var rec = new MediaRecorder(new MediaStream(tracks), { mimeType: type, videoBitsPerSecond: 1500000, audioBitsPerSecond: 64000 });
		var r = g.rec, seq = 0, queue = Promise.resolve(), started = Date.now();
		r.recorder = rec;
		rec.ondataavailable = function (e) {
			if (!e.data || !e.data.size) return;
			var n = seq++, blob = e.data;
			queue = queue.then(function () {
				return fetch(U.recChunk.replace('__id__', r.id) + '?seq=' + n, { method: 'POST', headers: { 'RequestVerificationToken': K.token(), 'Content-Type': 'application/octet-stream' }, body: blob })
					.then(function (x) { return x.json(); }).then(function (res) { if (!res.ok) r.failed = res.error || t('failed'); }).catch(function () { r.failed = t('lost'); });
			});
		};
		rec.onstop = function () {
			stops.forEach(function (f) { try { f(); } catch (e) { } });
			queue.then(function () { return K.post(U.recFinish.replace('__id__', r.id), { seconds: Math.round((Date.now() - started) / 1000) }); })
				.then(function (res) { if (res && res.ok) toast(t('recSaved'), 'success'); else toast((res && res.error) || r.failed || t('failed'), 'error'); K.refreshLog(); });
		};
		rec.start(5000);
	}
	function recFinish(x) { var r = x.rec; if (!r) return; if (r.recorder && r.recorder.state !== 'inactive') { try { r.recorder.stop(); } catch (e) { } } x.rec = null; }

	/* ---------------- picking people ---------------- */
	function pick(exclude, done, room) {
		room = room || 3;
		K.get(U.people).then(function (res) {
			var list = ((res && res.people) || []).filter(function (p) { return exclude.indexOf(p.id) < 0; });
			var rank = { on: 0, busy: 1, off: 2 };
			list.sort(function (a, b) { return (rank[a.state] - rank[b.state]) || a.name.localeCompare(b.name); });
			var el = document.createElement('div'); el.className = 'cbc-overlay'; el.style.zIndex = 2070;
			el.innerHTML = '<div class="cbc-card text-start" style="max-width:520px"><div class="p-6 border-bottom d-flex flex-stack"><div><div class="fw-bold fs-4 text-gray-900">' + esc(t('addPeople')) + '</div>' +
				'<div class="text-muted fs-7">' + esc(t('grpPickSub', room)) + '</div></div><button type="button" class="btn btn-icon btn-sm btn-light" data-x><i class="ki-outline ki-cross fs-2"></i></button></div>' +
				'<div class="p-4"><input class="form-control form-control-solid mb-3" data-q placeholder="' + esc(t('grpSearch')) + '" />' +
				'<div style="max-height:380px;overflow-y:auto" data-list>' + list.map(function (p) {
					var dis = p.state !== 'on';
					return '<label class="d-flex align-items-center gap-3 p-2 rounded cbc-hit' + (dis ? ' opacity-50' : '') + '" data-row="' + esc((p.name + ' ' + (p.title || '')).toLowerCase()) + '">' +
						'<input class="form-check-input flex-shrink-0" type="checkbox" value="' + p.id + '"' + (dis ? ' disabled' : '') + ' />' + face(p, 36) +
						'<span class="flex-grow-1 min-w-0"><span class="d-block fw-bold text-gray-900 fs-7 text-truncate">' + esc(p.name) + '</span><span class="d-block text-muted fs-8 text-truncate">' + esc(p.title || '') + '</span></span>' +
						'<span class="fs-8 ' + (p.state === 'on' ? 'text-primary' : p.state === 'busy' ? 'text-danger' : 'text-muted') + '">' + esc(t(p.state === 'on' ? 'stOn' : p.state === 'busy' ? 'stBusy' : 'stOff')) + '</span></label>';
				}).join('') + '</div></div>' +
				'<div class="p-4 border-top d-flex justify-content-end gap-2"><button type="button" class="btn btn-light" data-x>' + esc(t('cancel')) + '</button><button type="button" class="btn btn-primary" data-ok disabled><i class="ki-outline ki-people fs-4"></i>' + esc(t('grpAddTo')) + '</button></div></div>';
			document.body.appendChild(el);
			var ok = el.querySelector('[data-ok]');
			function chosen() { return [].slice.call(el.querySelectorAll('input[type=checkbox]:checked')).map(function (c) { return +c.value; }); }
			el.addEventListener('change', function (e) {
				if (e.target.type !== 'checkbox') return;
				if (chosen().length > room) { e.target.checked = false; toast(t('grpFull', room + exclude.length)); }
				ok.disabled = chosen().length === 0;
			});
			el.querySelector('[data-q]').addEventListener('input', function () { var q = this.value.trim().toLowerCase(); [].slice.call(el.querySelectorAll('[data-row]')).forEach(function (r) { r.classList.toggle('d-none', !!q && r.getAttribute('data-row').indexOf(q) < 0); r.classList.toggle('d-flex', !q || r.getAttribute('data-row').indexOf(q) >= 0); }); });
			[].slice.call(el.querySelectorAll('[data-x]')).forEach(function (b) { b.onclick = function () { el.remove(); }; });
			ok.onclick = function () { var ids = chosen(); el.remove(); if (ids.length) done(ids); };
		});
	}

	/* ---------------- wiring ---------------- */
	// back after a drop or a server restart: rejoin the call's record (the connections between devices did not stop)
	document.addEventListener('cbcalls:resumed', function () { if (g) call('GroupResume', g.id).then(function (r) { if (r && r.ok) { g.hostId = r.hostId; paint(); } }).catch(function () { }); });
	function onAway(m) { if (!g || g.id !== m.callId || !g.members[m.employeeId]) return; g.members[m.employeeId].away = true; paint(); }
	function onBack(m) { if (!g || g.id !== m.callId || !g.members[m.employeeId]) return; g.members[m.employeeId].away = false; paint(); }

	function hook(c) {
		c.on('groupaway', onAway);
		c.on('groupback', onBack);
		c.on('groupincoming', onIncoming);
		c.on('groupjoined', onJoined);
		c.on('groupleft', onLeft);
		c.on('groupstate', onState);
		c.on('groupmembers', onMembers);
		c.on('grouphost', onHost);
		c.on('groupconvert', onConvert);
		c.on('gsignal', onSignal);
		c.on('ended', onEnded);
		c.on('recask', onRecAsk);
		c.on('recagreed', onRecAgreed);
		c.on('recclosed', onRecClosed);
		c.on('recstart', onRecStart);
		c.on('recstop', onRecStop);
	}
	(window.cbCallsHooks = window.cbCallsHooks || []).push(hook);
	if (conn()) hook(conn());

	window.cbCallsGroup = { start: start, fromCall: fromCall, busy: function () { return !!g || !!inv; } };
})();
