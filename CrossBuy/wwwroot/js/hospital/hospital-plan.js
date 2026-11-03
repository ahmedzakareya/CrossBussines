/* Hospital room plan: draws a room's interior from its saved drawing (HospitalRooms.LayoutJson), or from
   its type when nothing has been drawn yet. Shared by the bed board, the room page and the designer, so
   every screen shows the same room. Items are percent of the room: { k, x, y, w, h, bed? }. */
(function () {
	'use strict';

	var text = { door: 'Door', bath: 'Bath', anteroom: 'Ante', companion: 'Companion', lounge: 'Lounge', sink: 'Sink', status: {} };
	function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }

	function avatar(p, size) {
		var s = 'width:' + size + 'px;height:' + size + 'px';
		return p.photo ? '<img class="av" style="' + s + '" src="' + esc(p.photo) + '" alt="" />'
			: '<span class="av ini" style="' + s + ';font-size:' + Math.round(size * 0.4) + 'px">' + esc(p.initials || '?') + '</span>';
	}

	function hasFeature(type, f) { return !!type && (type.features || []).indexOf(f) !== -1; }

	// The type's default drawing. Beds sit against the window wall so they never cover the bathroom in the
	// door corner; a four-bed bay is a 2×2; an open ICU bay is one row.
	function defaultItems(room, type) {
		var beds = room.beds || [], n = beds.length, it = [];
		it.push({ k: 'window', x: 25, y: -1.5, w: 50, h: 3 }, { k: 'door', x: 76, y: 98, w: 16, h: 3 });
		if (hasFeature(type, 'bath')) it.push({ k: 'bath', x: 0, y: 66, w: 22, h: 34 });
		if (hasFeature(type, 'anteroom')) it.push({ k: 'anteroom', x: 78, y: 66, w: 22, h: 34 });
		if (hasFeature(type, 'companion')) it.push({ k: 'companion', x: 28, y: 76, w: 30, h: 16 });
		if (hasFeature(type, 'lounge')) it.push({ k: 'lounge', x: 72, y: 8, w: 24, h: 36 });
		if (n === 2) it.push({ k: 'curtain', x: 50, y: 8, w: 0.6, h: 62 });
		var icu = room.kind === 'Icu';
		var pos = n === 1 ? [[38, 10]] : n === 2 ? [[14, 10], [62, 10]] : icu ? [[3, 34], [27, 34], [51, 34], [75, 34]] : [[8, 14], [56, 14], [8, 58], [56, 58]];
		var bw = n <= 2 ? 26 : icu ? 21 : 30, bh = n === 4 && !icu ? 30 : n <= 2 ? 50 : 46;
		beds.forEach(function (b, i) {
			var p = pos[i] || [3 + (i % 4) * 24, 8 + Math.floor(i / 4) * 34];
			it.push({ k: 'bed', bed: b.code, x: p[0], y: p[1], w: n > 4 ? 21 : bw, h: n > 4 ? 28 : bh });
		});
		return it;
	}

	// The saved drawing, plus any active bed it does not show yet (added since, e.g. by a type change),
	// at a free default spot — a bed must never be invisible on the board.
	function items(room, type) {
		var saved = null;
		if (room.layout) { try { saved = JSON.parse(room.layout); } catch (e) { saved = null; } }
		if (!Array.isArray(saved)) return defaultItems(room, type);
		var defs = defaultItems(room, type);
		(room.beds || []).forEach(function (b) {
			if (!saved.some(function (i) { return i.k === 'bed' && i.bed === b.code; })) {
				var d = defs.filter(function (i) { return i.bed === b.code; })[0];
				if (d) saved.push(d);
			}
		});
		return saved;
	}

	function render(room, type, opts) {
		opts = opts || {};
		var list = opts.items || items(room, type);
		var byCode = {}; (room.beds || []).forEach(function (b) { byCode[b.code] = b; });
		var h = '<div class="hp-plan' + (opts.small ? ' small' : '') + (opts.big ? ' big' : '') + '"' + (opts.id ? ' id="' + opts.id + '"' : '') + '>';
		list.forEach(function (it, idx) {
			var style = 'inset-inline-start:' + it.x + '%;top:' + it.y + '%;width:' + it.w + '%;height:' + it.h + '%';
			var edit = opts.edit ? ' data-i="' + idx + '"' + (opts.selectedItem === idx ? ' data-sel="1"' : '') : '';
			var handle = opts.edit ? '<span class="hp-handle"></span>' : '';
			var cls = (opts.edit ? ' hp-edit' : '');
			if (it.k !== 'bed') {
				var label = text[it.k] ? '<span class="lbl">' + esc(text[it.k]) + '</span>' : '';
				h += '<div class="hp-' + it.k + cls + '" style="' + style + '"' + edit + '>' + (it.k === 'bath' ? '<i class="ki-outline ki-drop fs-7"></i>' : '') + label + handle + '</div>';
				return;
			}
			var b = byCode[it.bed] || { code: it.bed, status: 'Vacant' };
			var short = it.h < 40 && it.w > it.h * 0.6 ? ' short' : '';
			var state = text.status[b.status] || b.status;
			// The person in the bed, as the prototype draws them: their photo (initials when there is
			// none) and their first name; the full name and MRN in the tooltip.
			var p = b.patient;
			var who = p ? '<div class="who">' + avatar(p, opts.small ? 20 : 26) + (opts.small ? '' : '<span class="nm">' + esc(p.first) + '</span>') + '</div>'
				: '<div class="state">' + esc(state) + '</div>';
			var inner = '<div class="pillow"></div><div class="code hp-num">' + esc(b.code) + '</div>' + who + handle;
			var title = ' title="' + esc(b.code + ' — ' + (p ? p.name + ' · ' + p.mrn : state)) + '"';
			var pick = opts.pickable ? opts.pickable(b) : null;
			var extra = (opts.selectedBed && opts.selectedBed === b.id ? ' sel' : '') + (pick === false ? ' na' : '') + (opts.onBed && b.id ? ' clickable' : '');
			var data = b.id ? ' data-bed="' + b.id + '"' : '';
			if (!opts.edit && opts.bedHref) h += '<a class="hp-bed ' + esc(b.status) + short + extra + '" style="' + style + '" href="' + opts.bedHref(b) + '"' + data + title + '>' + inner + '</a>';
			else h += '<div class="hp-bed ' + esc(b.status) + short + cls + extra + '" style="' + style + '"' + edit + data + title + '>' + inner + '</div>';
		});
		return h + '</div>';
	}

	window.HospitalPlan = { text: text, defaultItems: defaultItems, items: items, render: render, esc: esc, avatar: avatar };
})();
