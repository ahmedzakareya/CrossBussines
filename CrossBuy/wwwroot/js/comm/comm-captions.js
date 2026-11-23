/* =====================================================================================================================
   LIVE CAPTIONS IN A CALL (owner, 2026-11-22). Turned on by anyone in the call, for everyone in it (a 1:1 call or a
   group call): each person's page records THEIR OWN microphone in short pieces and sends them to the server
   (/Chat/CallCaption), which has the company's own speech model write them out and passes the words to everyone in
   the call under that person's name. So every line says who spoke, and nothing is stored anywhere.

   Each piece is a fresh recorder (three seconds): a piece cut from a running recorder has no header and cannot be
   read on its own. A muted microphone sends nothing. The words appear at the bottom of the call and fade after a few
   seconds; screen readers hear them (aria-live).
   ===================================================================================================================== */
(function () {
	'use strict';
	if (window.cbCaptions) return;
	var U = window.cbCallsUrl || {};
	var PIECE_MS = 3000;
	var cur = null;   // { callId, mic(), box, on, rec, timer }

	function token() { var i = document.querySelector('input[name="__RequestVerificationToken"]'); return i ? i.value : ''; }
	function mime() {
		var list = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4'];
		for (var i = 0; i < list.length; i++) if (window.MediaRecorder && MediaRecorder.isTypeSupported && MediaRecorder.isTypeSupported(list[i])) return list[i];
		return '';
	}
	function overlay() {
		if (!cur || !cur.box) return null;
		var o = cur.box.querySelector('.cbc-captions');
		if (!o) { o = document.createElement('div'); o.className = 'cbc-captions'; o.setAttribute('aria-live', 'polite'); cur.box.appendChild(o); }
		return o;
	}
	function show(name, text) {
		var o = overlay(); if (!o) return;
		var line = document.createElement('div'); line.className = 'ln';
		var b = document.createElement('b'); b.textContent = name ? name + ':' : '';
		var s = document.createElement('span'); s.setAttribute('dir', 'auto'); s.textContent = ' ' + text;
		line.appendChild(b); line.appendChild(s); o.appendChild(line);
		while (o.children.length > 3) o.removeChild(o.firstChild);
		setTimeout(function () { line.classList.add('out'); setTimeout(function () { if (line.parentNode) line.parentNode.removeChild(line); }, 600); }, 7000);
	}
	function send(callId, blob, speaker) {
		if (!U.caption) return Promise.resolve(null);
		var url = U.caption.replace('__id__', callId) + (speaker ? (U.caption.indexOf('?') >= 0 ? '&' : '?') + 'speaker=' + speaker : '');
		return fetch(url, { method: 'POST', credentials: 'same-origin', headers: { 'RequestVerificationToken': token(), 'Content-Type': 'application/octet-stream' }, body: blob })
			.then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; });
	}
	// One loop per voice: 'me' (my microphone), and each person in the call who asked to be captioned by a browser
	// because their device cannot record its own voice in a call (the app) — what this page HEARS from them, sent
	// under their id. The server lets one relayer per person through; a refused relayer rests for a while.
	function loop(key) {
		if (!cur || !cur.on) return;
		var v = cur.voices[key]; if (!v) return;
		var a = key === 'me' ? (cur.mic && cur.mic()) : (cur.relay && cur.relay(+key));
		var rest = v.restUntil && Date.now() < v.restUntil;
		if (!a || !a.enabled || a.readyState !== 'live' || !window.MediaRecorder || rest) { v.timer = setTimeout(function () { loop(key); }, rest ? 5000 : 1500); return; }   // muted / gone / another relays: nothing to send
		var m = mime(), chunks = [], r, callId = cur.callId;
		try { r = new MediaRecorder(new MediaStream([a]), m ? { mimeType: m, audioBitsPerSecond: 24000 } : undefined); } catch (e) { v.timer = setTimeout(function () { loop(key); }, 3000); return; }
		r.ondataavailable = function (e) { if (e.data && e.data.size) chunks.push(e.data); };
		r.onstop = function () {
			var blob = new Blob(chunks, { type: (r.mimeType || 'audio/webm').split(';')[0] });
			if (blob.size > 1500 && cur && cur.on && cur.callId === callId)
				send(callId, blob, key === 'me' ? null : +key).then(function (x) { if (x && x.taken) v.restUntil = Date.now() + 15000; });
		};
		r.start(); v.rec = r;
		v.timer = setTimeout(function () { try { if (r.state !== 'inactive') r.stop(); } catch (e) { } loop(key); }, PIECE_MS);
	}
	function voice(key) { if (!cur || cur.voices[key]) return; cur.voices[key] = {}; loop(key); }
	function quiet(key) {
		var v = cur && cur.voices[key]; if (!v) return;
		clearTimeout(v.timer); try { if (v.rec && v.rec.state !== 'inactive') v.rec.stop(); } catch (e) { }
		delete cur.voices[key];
	}
	function start(o) {
		if (cur && cur.on && cur.callId === o.callId) return;
		stop();
		cur = { callId: o.callId, mic: o.mic, relay: o.relay, box: o.box, on: true, voices: {} };
		voice('me');
		(o.relayIds ? o.relayIds() : []).forEach(function (id) { voice(String(id)); });
	}
	function stop() {
		if (!cur) return;
		cur.on = false;
		Object.keys(cur.voices).forEach(quiet);
		var o = cur.box && cur.box.querySelector('.cbc-captions'); if (o && o.parentNode) o.parentNode.removeChild(o);
		cur = null;
	}
	/// someone in the call asked (or stopped asking) to be captioned by this page
	function relay(callId, id, on) {
		if (!cur || !cur.on || cur.callId !== callId) return;
		if (on) voice(String(id)); else quiet(String(id));
	}
	function onCaption(m) {
		if (!cur || !m || m.callId !== cur.callId || !m.text) return;
		var ar = document.documentElement.dir === 'rtl';
		show((ar ? m.nameAr : m.nameEn) || m.nameAr || m.nameEn || '', m.text);
	}
	function hook(conn) { conn.on('caption', onCaption); }
	(window.cbCallsHooks = window.cbCallsHooks || []).push(hook);
	var K = window.cbCallsKit; if (K && K.conn && K.conn()) hook(K.conn());

	window.cbCaptions = {
		start: start, stop: stop, relay: relay,
		active: function (callId) { return !!(cur && cur.on && (callId == null || cur.callId === callId)); },
		supported: function () { return !!(window.MediaRecorder && U.caption); }
	};
})();
