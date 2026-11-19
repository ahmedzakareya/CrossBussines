/* Communication — background blur in a call (owner 2026-11-19).
   MediaPipe's selfie segmenter (tasks-vision 1.2.2, Apache-2.0, vendored under /lib/mediapipe — no CDN, it works on
   the internal network) finds the person in each camera frame; the frame is drawn sharp where the person is and
   blurred everywhere else, on a canvas whose stream replaces the camera track. Everything happens on this device:
   no picture leaves it except the call itself.
   The library (~15 MB with its wasm) loads only the first time someone turns the blur on.

   window.cbBlur.supported()  -> can this browser do it at all
   window.cbBlur.create(track) -> Promise<{ track, stop() }>: the blurred track; stop() ends the drawing (the camera
                                  track it was given is the caller's to stop) */
(function () {
	'use strict';
	var src = (document.currentScript && document.currentScript.src) || '';
	var ROOT = src ? src.replace(/\/js\/comm\/comm-blur\.js.*$/, '') : '';
	var LIB = ROOT + '/lib/mediapipe/tasks-vision-1.2.2';
	var segP = null;

	function supported() {
		return !!(window.WebAssembly && window.HTMLCanvasElement && HTMLCanvasElement.prototype.captureStream);
	}

	function loadScript(url) {
		return new Promise(function (ok, fail) {
			if (window.Vision) { ok(); return; }
			var s = document.createElement('script'); s.src = url; s.async = true;
			s.onload = function () { ok(); }; s.onerror = function () { fail(new Error('blur library')); };
			document.head.appendChild(s);
		});
	}

	// one segmenter for the page: a call at a time, and its timestamps only ever increase
	function segmenter() {
		if (segP) return segP;
		segP = loadScript(LIB + '/vision_bundle.js').then(function () {
			var V = window.Vision;
			return V.FilesetResolver.forVisionTasks(LIB + '/wasm').then(function (files) {
				function opts(delegate) {
					return { baseOptions: { modelAssetPath: LIB + '/selfie_segmenter.bin', delegate: delegate }, runningMode: 'VIDEO', outputConfidenceMasks: true, outputCategoryMask: false };
				}
				// the graphics card when there is one; the processor otherwise
				return V.ImageSegmenter.createFromOptions(files, opts('GPU')).catch(function () { return V.ImageSegmenter.createFromOptions(files, opts('CPU')); });
			});
		});
		segP.catch(function () { segP = null; });
		return segP;
	}

	function create(track) {
		if (!supported()) return Promise.reject(new Error('unsupported'));
		return segmenter().then(function (seg) {
			var video = document.createElement('video');
			video.muted = true; video.playsInline = true; video.srcObject = new MediaStream([track]);
			var out = document.createElement('canvas'), ctx = out.getContext('2d');
			var st = track.getSettings ? track.getSettings() : {};
			out.width = st.width || 640; out.height = st.height || 480;
			var mask = document.createElement('canvas'), mctx = mask.getContext('2d'), img = null;
			var running = true, last = -1, raf = 0, timer = 0, stamp = 0;

			function draw(m, vw, vh) {
				var mw = m.width, mh = m.height, f = m.getAsFloat32Array();
				if (mask.width !== mw || mask.height !== mh) { mask.width = mw; mask.height = mh; img = null; }
				if (!img) img = mctx.createImageData(mw, mh);
				var d = img.data;
				for (var i = 0, j = 3; i < f.length; i++, j += 4) d[j] = f[i] * 255;   // the person's confidence as alpha
				mctx.putImageData(img, 0, 0);
				ctx.save();
				ctx.globalCompositeOperation = 'copy'; ctx.drawImage(mask, 0, 0, vw, vh);                 // where the person is
				ctx.globalCompositeOperation = 'source-in'; ctx.drawImage(video, 0, 0, vw, vh);          // the person, sharp
				ctx.globalCompositeOperation = 'destination-over'; ctx.filter = 'blur(14px)';            // the rest, blurred
				ctx.drawImage(video, -24, -24, vw + 48, vh + 48);                                         // (oversized: no dark rim)
				ctx.restore();
			}
			function frame() {
				if (!running) return;
				try {
					if (video.readyState >= 2 && video.currentTime !== last) {
						last = video.currentTime;
						var vw = video.videoWidth, vh = video.videoHeight;
						if (vw && vh) {
							if (out.width !== vw || out.height !== vh) { out.width = vw; out.height = vh; }
							stamp = Math.max(stamp + 1, performance.now());
							seg.segmentForVideo(video, stamp, function (r) { var m = r && r.confidenceMasks && r.confidenceMasks[0]; if (m) draw(m, vw, vh); });
						}
					}
				} catch (e) { /* a dropped frame, not a dropped call */ }
				// a hidden tab gets no animation frames, but the other side must keep seeing me
				if (document.hidden) timer = setTimeout(frame, 40); else raf = requestAnimationFrame(frame);
			}
			return video.play().catch(function () { }).then(function () {
				var outTrack = out.captureStream(25).getVideoTracks()[0];
				frame();
				return {
					track: outTrack,
					stop: function () {
						running = false; cancelAnimationFrame(raf); clearTimeout(timer);
						try { outTrack.stop(); } catch (e) { }
						video.srcObject = null;
					}
				};
			});
		});
	}

	window.cbBlur = { supported: supported, create: create };
})();
