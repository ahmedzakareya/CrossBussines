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
	function send(callId, blob) {
		if (!U.caption) return;
		fetch(U.caption.replace('__id__', callId), { method: 'POST', credentials: 'same-origin', headers: { 'RequestVerificationToken': token(), 'Content-Type': 'application/octet-stream' }, body: blob }).catch(function () { });
	}
	function loop() {
		if (!cur || !cur.on) return;
		var a = cur.mic && cur.mic();
		if (!a || !a.enabled || a.readyState !== 'live' || !window.MediaRecorder) { cur.timer = setTimeout(loop, 1500); return; }   // muted: nothing to say
		var m = mime(), chunks = [], r, callId = cur.callId;
		try { r = new MediaRecorder(new MediaStream([a]), m ? { mimeType: m, audioBitsPerSecond: 24000 } : undefined); } catch (e) { cur.timer = setTimeout(loop, 3000); return; }
		r.ondataavailable = function (e) { if (e.data && e.data.size) chunks.push(e.data); };
		r.onstop = function () {
			var blob = new Blob(chunks, { type: (r.mimeType || 'audio/webm').split(';')[0] });
			if (blob.size > 1500 && cur && cur.on && cur.callId === callId) send(callId, blob);
		};
		r.start(); cur.rec = r;
		cur.timer = setTimeout(function () { try { if (r.state !== 'inactive') r.stop(); } catch (e) { } loop(); }, PIECE_MS);
	}
	function start(o) {
		if (cur && cur.on && cur.callId === o.callId) return;
		stop();
		cur = { callId: o.callId, mic: o.mic, box: o.box, on: true };
		loop();
	}
	function stop() {
		if (!cur) return;
		cur.on = false; clearTimeout(cur.timer);
		try { if (cur.rec && cur.rec.state !== 'inactive') cur.rec.stop(); } catch (e) { }
		var o = cur.box && cur.box.querySelector('.cbc-captions'); if (o && o.parentNode) o.parentNode.removeChild(o);
		cur = null;
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
		start: start, stop: stop,
		active: function (callId) { return !!(cur && cur.on && (callId == null || cur.callId === callId)); },
		supported: function () { return !!(window.MediaRecorder && U.caption); }
	};
})();
