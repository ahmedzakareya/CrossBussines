/* Communication — the microphone, camera and speaker a call uses (owner 2026-11-19; Slack added a pre-join check this
   year, Teams and Zoom have long had one).

   window.cbDevices
     .audio()            -> the getUserMedia audio constraint: the chosen microphone, else the default
     .video(base)        -> base video constraints with the chosen camera added (ideal, so an unplugged one falls back)
     .speaker()          -> the chosen output device id ('' = default)
     .applySpeaker(root) -> route every <audio>/<video> under root to the chosen speaker (where the browser can)
     .open({ replace })  -> the check: choose each device, see yourself, watch the microphone's level, play a test tone.
                            In a call, replace(kind, track) puts the newly chosen microphone or camera on the call at once.
   The choice is kept on this device (localStorage) and used by every call after it. */
(function () {
	'use strict';
	if (window.cbDevices) return;
	var K = window.cbCallsKit || {};
	var t = K.t || function (k) { return k; }, esc = K.esc || function (s) { return String(s == null ? '' : s); }, ico = K.ico || function () { return ''; };
	var KEY = { audioinput: 'cb.dev.mic', videoinput: 'cb.dev.cam', audiooutput: 'cb.dev.spk' };

	function get(k) { try { return localStorage.getItem(k) || ''; } catch (e) { return ''; } }
	function set(k, v) { try { if (v) localStorage.setItem(k, v); else localStorage.removeItem(k); } catch (e) { } }

	function audio() {
		var id = get(KEY.audioinput);
		return id ? { deviceId: { ideal: id }, echoCancellation: true, noiseSuppression: true, autoGainControl: true } : true;
	}
	function video(base) {
		if (!base) return base;
		var id = get(KEY.videoinput), v = Object.assign({}, base === true ? {} : base);
		if (id) { v.deviceId = { ideal: id }; delete v.facingMode; }
		return v;
	}
	function speaker() { return get(KEY.audiooutput); }
	function applySpeaker(root) {
		var id = speaker(); if (!id) return;
		[].slice.call((root || document).querySelectorAll('audio,video')).forEach(function (m) { if (m.setSinkId) m.setSinkId(id).catch(function () { }); });
	}

	function open(opts) {
		opts = opts || {};
		if (!navigator.mediaDevices || !navigator.mediaDevices.enumerateDevices) return;
		var preview = null, ctx = null, raf = 0;
		var html = '<div class="cm-dev text-start">' +
			'<div class="pv"><video autoplay muted playsinline></video><div class="nocam">' + ico('video-off', 28) + '</div></div>' +
			'<label>' + esc(t('cmDevMic')) + '</label><select class="form-select form-select-solid" data-k="audioinput"></select>' +
			'<div class="lvl" aria-hidden="true"><i></i></div>' +
			'<label>' + esc(t('cmDevCam')) + '</label><select class="form-select form-select-solid" data-k="videoinput"></select>' +
			'<label>' + esc(t('cmDevSpk')) + '</label><div class="d-flex gap-2"><select class="form-select form-select-solid" data-k="audiooutput"></select>' +
			'<button type="button" class="btn btn-light-primary btn-sm text-nowrap" data-tone>' + ico('volume-2', 14) + ' ' + esc(t('cmDevTest')) + '</button></div>' +
			'<div class="nt">' + esc(t('cmDevNote')) + '</div></div>';
		var swal = window.Swal ? Swal.fire({
			title: t('cmDevTitle'), html: html, showCancelButton: true, confirmButtonText: t('cmDevSave'), cancelButtonText: t('cmDevCancel'),
			buttonsStyling: false, customClass: { confirmButton: 'btn btn-primary', cancelButton: 'btn btn-light', popup: 'cm-root' }, width: 520,
			didOpen: function (pop) { start(pop); }, willClose: stop
		}) : null;
		if (!swal) return;

		function start(pop) {
			var sel = {};
			['audioinput', 'videoinput', 'audiooutput'].forEach(function (k) { sel[k] = pop.querySelector('select[data-k="' + k + '"]'); });
			if (!('setSinkId' in HTMLMediaElement.prototype)) { sel.audiooutput.disabled = true; }
			// permission first: device names are hidden until the page may use them
			navigator.mediaDevices.getUserMedia({ audio: true, video: true }).catch(function () { return navigator.mediaDevices.getUserMedia({ audio: true }); })
				.then(function (s) { s.getTracks().forEach(function (x) { x.stop(); }); }).catch(function () { })
				.then(function () { return navigator.mediaDevices.enumerateDevices(); })
				.then(function (list) {
					['audioinput', 'videoinput', 'audiooutput'].forEach(function (k) {
						var items = list.filter(function (d) { return d.kind === k; }), cur = get(KEY[k]);
						sel[k].innerHTML = '<option value="">' + esc(t('cmDevDefault')) + '</option>' + items.map(function (d, i) {
							return '<option value="' + esc(d.deviceId) + '"' + (d.deviceId === cur ? ' selected' : '') + '>' + esc(d.label || (k + ' ' + (i + 1))) + '</option>';
						}).join('');
					});
					restart(pop, sel);
					sel.audioinput.onchange = sel.videoinput.onchange = function () { restart(pop, sel); };
				});
			pop.querySelector('[data-tone]').onclick = function () { tone(sel.audiooutput.value); };
			// save: remember, and put the new devices on the call now
			Swal.getConfirmButton().addEventListener('click', function () {
				var before = { mic: get(KEY.audioinput), cam: get(KEY.videoinput) };
				set(KEY.audioinput, sel.audioinput.value); set(KEY.videoinput, sel.videoinput.value); set(KEY.audiooutput, sel.audiooutput.value);
				applySpeaker(document);
				if (typeof opts.replace === 'function') {
					if (before.mic !== sel.audioinput.value) navigator.mediaDevices.getUserMedia({ audio: audio() }).then(function (s) { opts.replace('audio', s.getAudioTracks()[0]); }).catch(function () { });
					if (before.cam !== sel.videoinput.value && opts.hasVideo && opts.hasVideo())
						navigator.mediaDevices.getUserMedia({ video: video(opts.videoBase || { height: { ideal: 720 } }) }).then(function (s) { opts.replace('video', s.getVideoTracks()[0]); }).catch(function () { });
				}
				if (K.toast) K.toast(t('cmDevSaved'), 'success');
			}, { once: true });
		}

		function restart(pop, sel) {
			stopPreview();
			var a = sel.audioinput.value ? { deviceId: { exact: sel.audioinput.value } } : true;
			var v = sel.videoinput.value ? { deviceId: { exact: sel.videoinput.value } } : true;
			navigator.mediaDevices.getUserMedia({ audio: a, video: v }).catch(function () { return navigator.mediaDevices.getUserMedia({ audio: a }); }).then(function (s) {
				preview = s;
				var vid = pop.querySelector('.pv video'), has = s.getVideoTracks().length > 0;
				vid.srcObject = has ? s : null; pop.querySelector('.pv').classList.toggle('off', !has);
				// the microphone's level, so a dead or wrong microphone is seen before the call
				try {
					ctx = new (window.AudioContext || window.webkitAudioContext)();
					var an = ctx.createAnalyser(); an.fftSize = 512; ctx.createMediaStreamSource(s).connect(an);
					var buf = new Uint8Array(an.fftSize), bar = pop.querySelector('.lvl i');
					(function loop() {
						an.getByteTimeDomainData(buf); var peak = 0;
						for (var i = 0; i < buf.length; i++) peak = Math.max(peak, Math.abs(buf[i] - 128));
						bar.style.width = Math.min(100, peak / 64 * 100) + '%';
						raf = requestAnimationFrame(loop);
					})();
				} catch (e) { }
			}).catch(function () { pop.querySelector('.pv').classList.add('off'); });
		}
		function stopPreview() {
			cancelAnimationFrame(raf);
			if (ctx) { try { ctx.close(); } catch (e) { } ctx = null; }
			if (preview) { preview.getTracks().forEach(function (x) { x.stop(); }); preview = null; }
		}
		function stop() { stopPreview(); }
		function tone(sinkId) {
			try {
				var c = new (window.AudioContext || window.webkitAudioContext)(), o = c.createOscillator(), g = c.createGain(), d = c.createMediaStreamDestination();
				o.frequency.value = 660; g.gain.value = .15; o.connect(g); g.connect(d);
				var el = new Audio(); el.srcObject = d.stream;
				(sinkId && el.setSinkId ? el.setSinkId(sinkId) : Promise.resolve()).catch(function () { }).then(function () { el.play().catch(function () { }); o.start(); setTimeout(function () { o.stop(); c.close(); }, 700); });
			} catch (e) { }
		}
	}

	window.cbDevices = { audio: audio, video: video, speaker: speaker, applySpeaker: applySpeaker, open: open };
})();
