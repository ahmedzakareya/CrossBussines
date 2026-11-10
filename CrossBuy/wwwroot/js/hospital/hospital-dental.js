/* The dental chart renderer (H15), drawn exactly as the approved prototype draws it.
   FDI (ISO 3950): two digits, quadrant then position from the midline; drawn from the dentist's view (the
   patient's right on the chart's left). Each tooth is an ANATOMICAL side view — crown and its roots by tooth
   kind (upper molars 3, lower molars 2, the upper first premolar 2) — over the five-surface top view, which is
   also where surfaces are picked. The charting convention of the commercial systems: red = a condition or
   treatment still to do (planned = red hatch), blue = completed here, green = existing work done elsewhere.
   No amber/yellow (house rule).
   A tooth's data: { s: { M|O|I|D|B|L: cond|plan|done|exist }, tx: [{ k: crown|rct|implant|extract, s }], missing, impacted }. */
(function () {
	'use strict';
	var UPPER = [18, 17, 16, 15, 14, 13, 12, 11, 21, 22, 23, 24, 25, 26, 27, 28], LOWER = [48, 47, 46, 45, 44, 43, 42, 41, 31, 32, 33, 34, 35, 36, 37, 38];
	var PRI_U = [55, 54, 53, 52, 51, 61, 62, 63, 64, 65], PRI_L = [85, 84, 83, 82, 81, 71, 72, 73, 74, 75];
	var MIX_U = [16, 55, 54, 53, 12, 11, 21, 22, 63, 64, 65, 26], MIX_L = [46, 85, 84, 83, 42, 41, 31, 32, 73, 74, 75, 36];
	var SETS = { Permanent: [UPPER, LOWER], Primary: [PRI_U, PRI_L], Mixed: [MIX_U, MIX_L] };
	var MARK = { cond: '#DC2626', plan: 'url(#dnHatch)', done: '#1D4ED8', exist: '#15803D' };
	var MARK_SOLID = { cond: '#DC2626', plan: '#DC2626', done: '#1D4ED8', exist: '#15803D' };
	function quadrant(n) { return Math.floor(n / 10); }
	function isUpper(n) { return [1, 2, 5, 6].indexOf(quadrant(n)) !== -1; }
	function isPrimary(n) { return quadrant(n) >= 5; }
	function patientRight(n) { return [1, 4, 5, 8].indexOf(quadrant(n)) !== -1; }
	function kind(n) { var u = n % 10; if (isPrimary(n)) return u <= 2 ? 'inc' : u === 3 ? 'can' : 'mol'; return u <= 2 ? 'inc' : u === 3 ? 'can' : u <= 5 ? 'pre' : 'mol'; }
	function front(n) { var k = kind(n); return k === 'inc' || k === 'can'; }
	function roots(n) { var k = kind(n), u = n % 10; if (k === 'mol') return isUpper(n) ? 3 : 2; if (k === 'pre' && isUpper(n) && u === 4) return 2; return 1; }
	function tx(t, k) { return (t.tx || []).filter(function (x) { return x.k === k; })[0]; }

	// The side (buccal) view of one tooth, drawn crown-up as a lower tooth and flipped for the upper arch, so the
	// roots point away from the occlusal plane as on every anatomical chart. Restorations sit on the crown faces,
	// a root canal is drawn inside the roots, an implant replaces them, a crown covers the crown.
	function sideView(n, t) {
		var k = kind(n), sc = isPrimary(n) ? 0.82 : 1, W = { inc: 24, can: 26, pre: 27, mol: 36 }[k] * sc, cx = 22, x1 = cx - W / 2, x2 = cx + W / 2;
		var cH = 30 * sc, top = 6, base = top + cH, rootL = { inc: 52, can: 64, pre: 52, mol: 46 }[k] * sc;
		var crownTop = k === 'can' ? 'L' + (cx - 4) + ',' + (top + 4) + ' L' + cx + ',' + top + ' L' + (cx + 4) + ',' + (top + 4)
			: k === 'mol' ? 'Q' + (x1 + W * 0.25) + ',' + (top - 3) + ' ' + (cx) + ',' + (top + 3) + ' Q' + (x2 - W * 0.25) + ',' + (top - 3) + ' ' + (x2 - 3) + ',' + (top + 2)
			: k === 'pre' ? 'Q' + cx + ',' + (top - 5) + ' ' + (x2 - 3) + ',' + (top + 2) : 'L' + (x2 - 3) + ',' + top;
		var crown = 'M' + (x1 + 3) + ',' + (k === 'inc' ? top : top + 2) + ' ' + crownTop + ' Q' + x2 + ',' + (top + 4) + ' ' + x2 + ',' + (top + 10) + ' L' + (x2 - 3) + ',' + base + ' L' + (x1 + 3) + ',' + base + ' L' + x1 + ',' + (top + 10) + ' Q' + x1 + ',' + (top + 4) + ' ' + (x1 + 3) + ',' + (k === 'inc' ? top : top + 2) + ' Z';
		var nR = roots(n), rW = nR === 1 ? W * 0.56 : nR === 2 ? W * 0.36 : W * 0.3, gap = nR === 1 ? 0 : W / (nR + 0.2);
		var rootPaths = [], canals = [];
		for (var i = 0; i < nR; i++) {
			var rc = nR === 1 ? cx : cx + (i - (nR - 1) / 2) * gap, len = nR === 3 && i === 1 ? rootL * 0.92 : rootL;
			rootPaths.push('M' + (rc - rW / 2) + ',' + (base - 1) + ' Q' + (rc - rW / 2.6) + ',' + (base + len * 0.72) + ' ' + rc + ',' + (base + len) + ' Q' + (rc + rW / 2.6) + ',' + (base + len * 0.72) + ' ' + (rc + rW / 2) + ',' + (base - 1) + ' Z');
			canals.push('M' + rc + ',' + (base - 4) + ' L' + rc + ',' + (base + len - 5));
		}
		var mesialRight = patientRight(n), s = t.s || {}, g = '';
		var imp = tx(t, 'implant'), rct = tx(t, 'rct'), crn = tx(t, 'crown'), ext = tx(t, 'extract'), ghost = t.missing && !imp;
		// roots, or the implant fixture that replaces them
		if (imp) {
			var iw = W * 0.42, ix = cx - iw / 2, c = MARK_SOLID[imp.s];
			g += '<rect x="' + ix + '" y="' + base + '" width="' + iw + '" height="' + (rootL * 0.85) + '" rx="3" fill="#E2E8F0" stroke="' + c + '" stroke-width="1.6"' + (imp.s === 'plan' ? ' stroke-dasharray="3 2"' : '') + '/>';
			for (var y = base + 6; y < base + rootL * 0.8; y += 6) g += '<line x1="' + ix + '" y1="' + y + '" x2="' + (ix + iw) + '" y2="' + (y + 3) + '" stroke="' + c + '" stroke-width="1.2"/>';
		} else rootPaths.forEach(function (d) { g += '<path d="' + d + '" fill="' + (ghost ? 'none' : '#FBFCFE') + '" stroke="' + (ghost ? '#CBD5E1' : '#94A3B8') + '" stroke-width="1.2"' + (ghost ? ' stroke-dasharray="3 3"' : '') + '/>'; });
		if (rct && !imp) canals.forEach(function (d) { g += '<path d="' + d + '" stroke="' + MARK_SOLID[rct.s] + '" stroke-width="3" stroke-linecap="round"' + (rct.s === 'plan' ? ' stroke-dasharray="4 3"' : '') + '/>'; });
		// the crown (natural, or the restoration that covers it)
		var crownFill = crn ? (crn.s === 'plan' ? 'url(#dnHatch)' : MARK_SOLID[crn.s]) : ghost ? 'none' : '#FFFFFF';
		g += '<path d="' + crown + '" fill="' + crownFill + '" fill-opacity="' + (crn && crn.s !== 'plan' ? 0.8 : 1) + '" stroke="' + (crn ? MARK_SOLID[crn.s] : ghost ? '#CBD5E1' : '#64748B') + '" stroke-width="' + (crn ? 2 : 1.2) + '"' + (ghost ? ' stroke-dasharray="3 3"' : '') + '/>';
		// surface work seen from the side: the buccal face, the mesial and distal walls, the occlusal / incisal edge
		if (!crn && !ghost) {
			var wM = W * 0.2, mx = mesialRight ? x2 - wM - 1 : x1 + 1, dx = mesialRight ? x1 + 1 : x2 - wM - 1;
			if (s.B) g += '<ellipse cx="' + cx + '" cy="' + (top + cH * 0.58) + '" rx="' + (W * 0.22) + '" ry="' + (cH * 0.22) + '" fill="' + MARK[s.B] + '" stroke="' + MARK_SOLID[s.B] + '"/>';
			if (s.M) g += '<rect x="' + mx + '" y="' + (top + 6) + '" width="' + wM + '" height="' + (cH - 10) + '" rx="2" fill="' + MARK[s.M] + '" stroke="' + MARK_SOLID[s.M] + '"/>';
			if (s.D) g += '<rect x="' + dx + '" y="' + (top + 6) + '" width="' + wM + '" height="' + (cH - 10) + '" rx="2" fill="' + MARK[s.D] + '" stroke="' + MARK_SOLID[s.D] + '"/>';
			var occ = s.O || s.I;
			if (occ) g += '<rect x="' + (x1 + W * 0.22) + '" y="' + (top + 1) + '" width="' + (W * 0.56) + '" height="' + (cH * 0.26) + '" rx="2" fill="' + MARK[occ] + '" stroke="' + MARK_SOLID[occ] + '"/>';
		}
		if (ext) g += '<line x1="' + (x1 - 2) + '" y1="' + (top - 2) + '" x2="' + (x2 + 2) + '" y2="' + (base + rootL) + '" stroke="#DC2626" stroke-width="2.4"/><line x1="' + (x2 + 2) + '" y1="' + (top - 2) + '" x2="' + (x1 - 2) + '" y2="' + (base + rootL) + '" stroke="#DC2626" stroke-width="2.4"/>';
		var tilt = t.impacted ? ' rotate(' + (patientRight(n) ? 24 : -24) + ' 22 ' + (base + 10) + ')' : '';
		var flip = isUpper(n) ? 'translate(0,112) scale(1,-1)' : '';
		return '<svg width="44" height="112" viewBox="0 0 44 112" class="dn-side' + (t.impacted ? ' dn-impacted' : '') + '"><g transform="' + flip + tilt + '">' + g + '</g></svg>';
	}

	// The occlusal (top) view: the five surfaces of the 5-surface diagram, used to read and to pick surfaces.
	// `pick` (optional) is the surfaces being chosen: drawn in the brand tint over the marks.
	function topView(n, t, size, pick) {
		var s = t.s || {}, up = isUpper(n), right = patientRight(n) ? 'M' : 'D', left = patientRight(n) ? 'D' : 'M';
		var topS = up ? 'B' : 'L', botS = up ? 'L' : 'B', mid = front(n) ? (s.I || s.O) : s.O, midK = front(n) ? 'I' : 'O';
		var picked = function (k) { return pick && pick.indexOf(k) !== -1; };
		var f = function (k) { return picked(k) ? '#C9DAF8' : s[k] ? MARK[s[k]] : '#FFFFFF'; }, st = function (k) { return picked(k) ? '#3A5795' : s[k] ? MARK_SOLID[s[k]] : '#94A3B8'; };
		var crn = tx(t, 'crown'), ghost = t.missing && !tx(t, 'implant'), sz = size || 34;
		var svg = '<svg width="' + sz + '" height="' + sz + '" viewBox="0 0 40 40"' + (ghost && !pick ? ' opacity="0.35"' : '') + '>' +
			'<polygon data-surf="' + topS + '" points="2,2 38,2 28,12 12,12" fill="' + f(topS) + '" stroke="' + st(topS) + '"/>' +
			'<polygon data-surf="' + right + '" points="38,2 38,38 28,28 28,12" fill="' + f(right) + '" stroke="' + st(right) + '"/>' +
			'<polygon data-surf="' + botS + '" points="2,38 38,38 28,28 12,28" fill="' + f(botS) + '" stroke="' + st(botS) + '"/>' +
			'<polygon data-surf="' + left + '" points="2,2 12,12 12,28 2,38" fill="' + f(left) + '" stroke="' + st(left) + '"/>' +
			'<rect data-surf="' + midK + '" x="12" y="12" width="16" height="16" fill="' + (picked(midK) ? '#C9DAF8' : mid ? MARK[mid] : '#FFFFFF') + '" stroke="' + (picked(midK) ? '#3A5795' : mid ? MARK_SOLID[mid] : '#94A3B8') + '"/>';
		if (crn && !pick) svg += '<rect x="2" y="2" width="36" height="36" rx="4" fill="none" stroke="' + MARK_SOLID[crn.s] + '" stroke-width="3"' + (crn.s === 'plan' ? ' stroke-dasharray="4 3"' : '') + '/>';
		if (ghost && !pick) svg += '<line x1="4" y1="4" x2="36" y2="36" stroke="#475569" stroke-width="2.5"/><line x1="36" y1="4" x2="4" y2="36" stroke="#475569" stroke-width="2.5"/>';
		return svg + '</svg>';
	}

	function toothCell(n, t) {
		t = t || {};
		var num = '<div class="dn-num hp-num">' + n + '</div>';
		var body = isUpper(n) ? sideView(n, t) + topView(n, t) + num : num + topView(n, t) + sideView(n, t);
		return '<div class="dn-tooth' + (t.missing ? ' dn-missing' : '') + '" data-tooth="' + n + '" title="' + n + '">' + body + '</div>';
	}
	function arch(list, chart, lower) {
		var half = list.length / 2;
		return '<div class="dn-arch' + (lower ? ' dn-lower' : '') + '">' + list.map(function (n, i) { return (i === half ? '<div class="dn-mid"></div>' : '') + toothCell(n, chart[n]); }).join('') + '</div>';
	}
	// One hatch pattern for "planned", shared by every tooth on the page.
	var DEFS = '<svg width="0" height="0" style="position:absolute"><defs><pattern id="dnHatch" width="5" height="5" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="5" height="5" fill="#FFFFFF"/><line x1="0" y1="0" x2="0" y2="5" stroke="#DC2626" stroke-width="2.4"/></pattern></defs></svg>';

	function chart(teeth, dentition, text) {
		var set = SETS[dentition] || SETS.Permanent;
		return '<div class="dn-chart"><div class="d-flex flex-stack text-muted fs-8 mb-2"><span>' + text.right + '</span><span>' + text.upper + '</span><span>' + text.left + '</span></div>' +
			arch(set[0], teeth, false) + '<div class="my-2 border-top border-2 border-gray-300"></div>' + arch(set[1], teeth, true) +
			'<div class="text-center text-muted fs-8 mt-2">' + text.lower + '</div></div>';
	}

	// The periodontal chart of one arch: six sites per tooth, drawn in the chart's own left-to-right order, so on the
	// patient's right the distal site comes first and on the patient's left the mesial one.
	// perio[n] = [[pd ×6], [rec ×6], [bleed ×6]], stored buccal DB·B·MB = 0,1,2 then lingual DL·L·ML = 3,4,5.
	function perioArch(perio, mob, furc, list, upper, text) {
		var teeth = list.filter(function (n) { return perio[n]; });
		if (!teeth.length) return '<div class="text-muted fs-7">' + text.noArch + '</div>';
		function order(n, side) { var b = side === 'b' ? [0, 1, 2] : [3, 4, 5]; return patientRight(n) ? b : b.slice().reverse(); }
		// The teeth stay in the dentist's order (18 on the left) in both languages; in Arabic the labels stand at the
		// right-hand edge, where the reading starts.
		var rtl = document.documentElement.dir === 'rtl';
		function lbl(label) { return '<td class="fw-semibold text-gray-700 fs-8 text-nowrap" dir="' + (rtl ? 'rtl' : 'ltr') + '" style="text-align:' + (rtl ? 'right;padding-left:12px' : 'left;padding-right:12px') + '">' + label + '</td>'; }
		function tr(label, cells) { return '<tr>' + (rtl ? cells + lbl(label) : lbl(label) + cells) + '</tr>'; }
		function row(label, side, k, cal) {
			return tr(label, teeth.map(function (n) {
				var x = perio[n];
				return '<td class="text-center" style="border-inline-start:1px solid #E2E8F0">' + order(n, side).map(function (i) {
					if (k === 2) return '<span class="pd-cell d-inline-block">' + (x[2][i] ? '<span class="pd-bop"></span>' : '·') + '</span>';
					var v = cal ? x[0][i] + x[1][i] : x[k][i];
					return '<span class="pd-cell d-inline-block hp-num ' + ((k === 0 || cal) && v >= 5 ? 'pd-hi' : '') + '">' + v + '</span>';
				}).join('') + '</td>';
			}).join(''));
		}
		function perTooth(label, map, hi, show) {
			return tr(label, teeth.map(function (n) {
				var v = map[n]; return '<td class="text-center hp-num fw-bold fs-8 ' + (v != null && hi(v) ? 'text-danger' : 'text-gray-600') + '" style="border-inline-start:1px solid #E2E8F0">' + (v == null ? '·' : show(v)) + '</td>';
			}).join(''));
		}
		var sideA = upper ? 'b' : 'l', sideB = upper ? 'l' : 'b';
		var nameA = upper ? text.buccal : text.lingual, nameB = upper ? text.palatal : text.buccal;
		var roman = function (v) { return ['', 'I', 'II', 'III'][v] || v; };
		return '<div class="table-responsive" dir="ltr"><table class="table table-sm mb-0 align-middle"><thead><tr>' + (rtl ? '' : '<th></th>') + teeth.map(function (n) { return '<th class="text-center hp-num fs-8">' + n + '</th>'; }).join('') + (rtl ? '<th></th>' : '') + '</tr></thead><tbody>' +
			perTooth(text.mobility, mob, function (v) { return v >= 2; }, function (v) { return v; }) + perTooth(text.furcation, furc, function (v) { return v > 1; }, roman) +
			row(nameA + ' — ' + text.pd, sideA, 0) + row(text.recession, sideA, 1) + row(text.cal, sideA, 0, true) + row(text.bleeding, sideA, 2) +
			'<tr><td colspan="' + (teeth.length + 1) + '" class="border-0 py-1"></td></tr>' +
			row(nameB + ' — ' + text.pd, sideB, 0) + row(text.recession, sideB, 1) + row(text.cal, sideB, 0, true) + row(text.bleeding, sideB, 2) + '</tbody></table></div>';
	}

	// A tooth's anatomical side view at a given height: the picture a tooth picker shows beside the number.
	function toothIcon(n, t, h) { return sideView(n, t || {}).replace('width="44" height="112"', 'width="' + Math.round(h * 44 / 112) + '" height="' + h + '"'); }

	// The "selected tooth" panel: its state, surfaces, whole-tooth work and what the plan holds for it. ONE place for
	// the dentist's chart and the patient's dental report; `opts.edit` adds the dentist's take-off and add buttons.
	function esc(v) { return String(v == null ? '' : v).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
	function toothPanel(n, teeth, marks, procs, W, opts) {
		opts = opts || {};
		var badge = function (st) { return '<span class="badge fs-8" style="background:' + MARK_SOLID[st] + ';color:#fff">' + W.st[st] + '</span>'; };
		var t = teeth[n] || {}, s = t.s || {}, mine = (marks || []).filter(function (x) { return x.tooth === n; });
		var state = t.missing ? W.missing : t.impacted ? W.impacted : (t.tx || []).length || Object.keys(s).length ? W.hasWork : W.sound;
		// each mark recorded by hand can be taken off (made in error); a plan's mark changes on its plan
		var off = function (x) { return opts.edit && x && !x.fromPlan ? ' <button type="button" class="btn btn-icon btn-sm btn-active-light-danger w-20px h-20px" data-void="' + x.id + '"><i class="ki-outline ki-cross fs-6"></i></button>' : ''; };
		var surfMark = function (k) { return mine.filter(function (x) { return x.surfaces && x.surfaces.indexOf(k) !== -1; }).pop(); };
		var workMark = function (k) { var w = { crown: 'Crown', rct: 'RootCanal', implant: 'Implant', extract: 'Extraction' }[k]; return mine.filter(function (x) { return x.work === w; }).pop(); };
		var surf = Object.keys(s).map(function (k) { return '<div class="d-flex flex-stack py-1"><span class="fs-7 text-gray-800">' + W.surf[k] + ' <span class="text-muted hp-num">' + k + '</span></span><span>' + badge(s[k]) + off(surfMark(k)) + '</span></div>'; }).join('') || '<span class="text-muted fs-7">—</span>';
		var work = (t.tx || []).map(function (x) { return '<div class="d-flex flex-stack py-1"><span class="fs-7 text-gray-800">' + W.tx[x.k] + '</span><span>' + badge(x.s) + off(workMark(x.k)) + '</span></div>'; }).join('');
		var states = mine.filter(function (x) { return x.work === 'Missing' || x.work === 'Impacted'; }).map(function (x) { return '<div class="d-flex flex-stack py-1"><span class="fs-7 text-gray-800">' + (x.work === 'Missing' ? W.missing : W.impacted) + '</span><span>' + off(x) + '</span></div>'; }).join('');
		var notes = mine.filter(function (x) { return x.note; }).map(function (x) { return '<div class="text-gray-700 fs-8 py-1">' + esc(x.note) + '</div>'; }).join('');
		var plan = (t.plan || []).map(function (c) {
			var bits = c.split('|'), pr = Object.keys(procs || {}).map(function (k) { return procs[k]; }).filter(function (p) { return p.code === bits[0]; })[0];
			return '<div class="d-flex flex-stack py-2 border-bottom border-gray-200"><span class="fs-7 text-gray-800">' + esc(pr ? pr.name : bits[0]) + '</span><span class="fs-8 hp-num">' + esc(bits[0]) + '</span></div>';
		}).join('') || '<div class="text-muted fs-7">' + W.nothing + '</div>';
		var kindT = W.kind[kind(n)] + (isPrimary(n) ? W.primary : '') + ' · ' + W.roots + ' <span class="hp-num">' + roots(n) + '</span>';
		return '<div class="d-flex align-items-center gap-3 mb-4"><span class="fs-2hx fw-bold hp-num text-gray-900">' + n + '</span><div><div class="fw-bold text-gray-900">' + state + '</div><div class="text-muted fs-8">' + kindT + '</div></div></div>' +
			'<div class="fw-semibold fs-7 text-gray-700 mb-1">' + W.surfaces + '</div><div class="mb-3">' + surf + '</div>' +
			(work || states ? '<div class="fw-semibold fs-7 text-gray-700 mb-1">' + W.whole + '</div><div class="mb-3">' + work + states + '</div>' : '') +
			(notes ? '<div class="mb-3">' + notes + '</div>' : '') +
			'<div class="fw-semibold fs-7 text-gray-700 mb-1">' + W.inPlan + '</div>' + plan +
			(opts.edit ? '<button type="button" class="btn btn-sm btn-light-primary w-100 mt-4" data-mark-open="' + n + '">' + W.add + '</button>' : '');
	}

	window.HospitalDental = {
		UPPER: UPPER, LOWER: LOWER, SETS: SETS, MARK_SOLID: MARK_SOLID, DEFS: DEFS,
		isUpper: isUpper, isPrimary: isPrimary, patientRight: patientRight, kind: kind, front: front, roots: roots,
		sideView: sideView, topView: topView, toothIcon: toothIcon, chart: chart, perioArch: perioArch, toothPanel: toothPanel
	};
})();
