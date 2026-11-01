/* Hospital prototype core: language, settings, module registry, router and the house UI helpers.
   Every screen builds its markup from these helpers so the prototype speaks one visual language —
   the TrialBalance notice, the ItemLedger statistic card and the #0E4A9E house table. */
(function () {
	'use strict';

	var lang = window.HMS_LANG === 'en' ? 'en' : 'ar';
	var isAr = lang === 'ar';

	function L(ar, en) { return isAr ? ar : en; }
	// Bilingual display field: {ar, en} twin, falling back to whichever side exists.
	function T(o) { if (o == null) return ''; if (typeof o === 'string') return o; return (isAr ? (o.ar || o.en) : (o.en || o.ar)) || ''; }
	function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }

	// Browser storage is a per-viewer convenience here; the page must render without it.
	var store = {
		get: function (k, d) { try { var v = localStorage.getItem(k); return v == null ? d : JSON.parse(v); } catch (e) { return d; } },
		set: function (k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch (e) { } }
	};

	/* ---------------- Module registry ----------------
	   A module is the unit a facility switches on or off. `requires` is enforced both ways: turning a
	   module on turns on what it needs, turning one off turns off what depends on it. */
	var MODULES = [
		{ key: 'mpi', core: true, icon: 'ki-profile-user', ar: 'سجل المرضى الموحد', en: 'Master patient index', dar: 'ملف طبي واحد لكل مريض، مطابقة الهوية ومنع التكرار', den: 'One medical record per patient, identity matching, duplicate prevention' },
		{ key: 'appointments', core: true, icon: 'ki-calendar', ar: 'المواعيد والاستقبال', en: 'Appointments & front desk', dar: 'جداول الأطباء والحجز والتسجيل وقوائم الانتظار', den: 'Doctor rosters, booking, check-in and queues' },
		{ key: 'opd', core: true, icon: 'ki-briefcase', ar: 'العيادات الخارجية', en: 'Outpatient clinics', dar: 'قائمة الطبيب والكشف والتشخيص والطلبات والروشتة', den: 'Doctor worklist, consultation, diagnosis, orders, prescription' },
		{ key: 'billing', core: true, icon: 'ki-bill', ar: 'الفوترة والخزينة', en: 'Billing & cashier', dar: 'فاتورة الزيارة وحصة المريض والتحصيل والترحيل للمحاسبة', den: 'Encounter bill, patient share, collection, posting to accounting' },
		{ key: 'er', icon: 'ki-pulse', ar: 'الطوارئ', en: 'Emergency', dar: 'الفرز (ESI) ولوحة الطوارئ والملاحظة والقرار', den: 'Triage (ESI), ER board, observation and disposition', requires: ['opd'] },
		{ key: 'inpatient', icon: 'ki-home-3', ar: 'التنويم والأسرة', en: 'Inpatient & beds', dar: 'الدخول والتحويل والخروج والأجنحة والأسرة', den: 'Admission, transfer, discharge, wards and beds' },
		{ key: 'nursing', icon: 'ki-heart', ar: 'التمريض', en: 'Nursing', dar: 'محطة التمريض وسجل إعطاء الأدوية والعلامات الحيوية', den: 'Nursing station, medication administration record, vitals', requires: ['inpatient'] },
		{ key: 'or', icon: 'ki-syringe', ar: 'العمليات', en: 'Operating theatre', dar: 'جدولة الغرف وقائمة السلامة الجراحية والتخدير', den: 'Theatre scheduling, surgical safety checklist, anaesthesia', requires: ['inpatient'] },
		{ key: 'icu', icon: 'ki-graph-up', ar: 'العناية المركزة', en: 'Intensive care', dar: 'مخطط العناية المركزة والمراقبة الساعية', den: 'ICU flowsheet and hourly monitoring', requires: ['nursing'] },
		{ key: 'lab', icon: 'ki-flask', ar: 'المختبر', en: 'Laboratory', dar: 'الطلبات والعينات والنتائج والقيم الحرجة', den: 'Orders, specimens, results and critical values' },
		{ key: 'radiology', icon: 'ki-scan-barcode', ar: 'الأشعة', en: 'Radiology', dar: 'قوائم الأجهزة والتقارير والربط مع PACS', den: 'Modality worklists, reports, PACS link' },
		{ key: 'pharmacy', icon: 'ki-capsule', ar: 'الصيدلية', en: 'Pharmacy', dar: 'صرف الروشتات من المخزون بالدفعة والصلاحية', den: 'Dispensing from inventory by batch and expiry' },
		{ key: 'bloodbank', icon: 'ki-drop', ar: 'بنك الدم', en: 'Blood bank', dar: 'المتبرعون والوحدات وتوافق الدم والنقل', den: 'Donors, units, cross-match and transfusion', requires: ['lab'] },
		{ key: 'insurance', icon: 'ki-shield-tick', ar: 'التأمين والمطالبات', en: 'Insurance & claims', dar: 'العقود والموافقات المسبقة والمطالبات والمرفوضات', den: 'Contracts, pre-authorisation, claims and rejections', requires: ['billing'] },
		{ key: 'kitchen', icon: 'ki-shop', ar: 'المطبخ المركزي والكافيتيريا', en: 'Central kitchen & cafeteria', dar: 'إنتاج الوجبات بالوصفات، الكافيتيريا، وتوزيع التكلفة على مراكز التكلفة', den: 'Recipe-based meal production, cafeteria, and cost allocation to cost centres' },
		{ key: 'dietary', icon: 'ki-coffee', ar: 'التغذية العلاجية', en: 'Clinical dietary', dar: 'الوجبات العلاجية لمرضى التنويم حسب أمر الطبيب', den: 'Therapeutic meals for inpatients per the doctor\'s order', requires: ['inpatient', 'kitchen'] },
		{ key: 'cssd', icon: 'ki-shield-search', ar: 'التعقيم المركزي', en: 'Central sterile supply', dar: 'دورات التعقيم وتتبع الأطقم الجراحية', den: 'Sterilisation cycles and instrument-set tracking', requires: ['or'] },
		{ key: 'portal', icon: 'ki-phone', ar: 'بوابة المريض', en: 'Patient portal', dar: 'الحجز الذاتي والنتائج والفواتير للمريض', den: 'Self-booking, results and bills for the patient', requires: ['appointments'] },
		{ key: 'telemed', icon: 'ki-screen', ar: 'الطب الاتصالي', en: 'Telemedicine', dar: 'كشف مرئي عن بعد مرتبط بالموعد', den: 'Video consultation tied to the appointment', requires: ['opd'] },
		{ key: 'doctorfees', icon: 'ki-wallet', ar: 'أتعاب الأطباء', en: 'Doctor fees', dar: 'عقود الأطباء الدائمين والجزئيين والزائرين وكشوف الأتعاب', den: 'Permanent, part-time and visiting contracts and fee statements', requires: ['billing'] },
		{ key: 'supply', icon: 'ki-package', ar: 'إمداد الأقسام', en: 'Ward supply', dar: 'طلبات الأقسام من المستودعات ومخزون الأمانة والمخدرات', den: 'Ward requisitions, consignment and controlled drugs' },
		{ key: 'quality', icon: 'ki-shield-search', ar: 'الجودة وسلامة المرضى', en: 'Quality & patient safety', dar: 'بلاغات الحوادث والمؤشرات ومتطلبات الاعتماد', den: 'Incident reports, indicators and accreditation' }
	];
	var MOD = {}; MODULES.forEach(function (m) { MOD[m.key] = m; });

	var PRESETS = [
		{ key: 'clinic', icon: 'ki-briefcase', ar: 'عيادة', en: 'Clinic', dar: 'عيادة واحدة أو عدة أطباء، بدون تنويم', den: 'One or several doctors, no beds', mods: ['pharmacy', 'insurance', 'doctorfees'] },
		{ key: 'polyclinic', icon: 'ki-office-bag', ar: 'مجمع طبي', en: 'Polyclinic', dar: 'عيادات متعددة مع مختبر وأشعة وصيدلية', den: 'Many clinics with lab, imaging and pharmacy', mods: ['lab', 'radiology', 'pharmacy', 'insurance', 'portal', 'telemed', 'doctorfees', 'supply', 'quality', 'kitchen'] },
		{ key: 'daysurgery', icon: 'ki-syringe', ar: 'مركز جراحة اليوم الواحد', en: 'Day-surgery centre', dar: 'عمليات وإفاقة بدون تنويم طويل', den: 'Theatre and recovery, short stay', mods: ['lab', 'radiology', 'pharmacy', 'insurance', 'inpatient', 'nursing', 'or', 'cssd', 'doctorfees', 'supply', 'quality', 'kitchen'] },
		{ key: 'hospital', icon: 'ki-bank', ar: 'مستشفى عام', en: 'General hospital', dar: 'كل الوحدات: طوارئ وتنويم وعمليات وعناية مركزة', den: 'Every module: ER, wards, theatre and ICU', mods: MODULES.map(function (m) { return m.key; }) }
	];

	var DEFAULTS = { preset: 'hospital', country: 'SA', modules: null, facility: { ar: 'مستشفى كروس بزنس التخصصي', en: 'CrossBusiness Specialist Hospital' } };
	var settings = Object.assign({}, DEFAULTS, store.get('hms.settings', {}));
	if (!settings.modules) settings.modules = presetModules('hospital');
	// A module added after a viewer saved settings takes its value from their preset.
	(function () { var base = presetModules(settings.preset === 'custom' ? 'hospital' : settings.preset); MODULES.forEach(function (m) { if (!(m.key in settings.modules)) settings.modules[m.key] = base[m.key]; }); })();

	function presetModules(key) {
		var p = PRESETS.filter(function (x) { return x.key === key; })[0] || PRESETS[3];
		var on = {};
		MODULES.forEach(function (m) { on[m.key] = !!m.core || p.mods.indexOf(m.key) !== -1; });
		return on;
	}
	function isOn(key) { return !key || !!(MOD[key] && (MOD[key].core || settings.modules[key])); }
	function setModule(key, on) {
		var m = MOD[key]; if (!m || m.core) return;
		settings.modules[key] = on;
		if (on) (m.requires || []).forEach(function (r) { if (!isOn(r)) setModule(r, true); });
		else MODULES.forEach(function (d) { if ((d.requires || []).indexOf(key) !== -1 && settings.modules[d.key]) setModule(d.key, false); });
	}
	function saveSettings() { store.set('hms.settings', settings); }

	/* ---------------- Formatting ---------------- */
	function country() { return HMS.data.countries[settings.country] || HMS.data.countries.SA; }
	function fmtNum(v, dec) { return Number(v || 0).toLocaleString('en-US', { minimumFractionDigits: dec || 0, maximumFractionDigits: dec || 0 }); }
	function money(v, opts) {
		var c = country().currency; opts = opts || {};
		if (!v && opts.dash) return '<span class="hms-zero">—</span>';
		return '<span class="num">' + fmtNum(v, c.dec) + '</span>' + (opts.bare ? '' : ' <span class="text-muted fs-8 fw-semibold">' + (isAr ? c.symAr : c.code) + '</span>');
	}
	function cur() { var c = country().currency; return isAr ? c.symAr : c.code; }
	var TODAY = new Date(2026, 10, 1);
	function age(dob) { var d = new Date(dob); var a = TODAY.getFullYear() - d.getFullYear(); var m = TODAY.getMonth() - d.getMonth(); if (m < 0 || (m === 0 && TODAY.getDate() < d.getDate())) a--; return a; }
	function ageSex(p) { return '<span class="num">' + age(p.dob) + '</span> ' + L('سنة', 'y') + ' · ' + (p.sex === 'M' ? L('ذكر', 'Male') : L('أنثى', 'Female')); }
	function dateStr(s) { return '<span class="num">' + s + '</span>'; }

	/* ---------------- People ---------------- */
	function initials(name) { var p = String(name || '').trim().split(/\s+/); return ((p[0] || '').charAt(0) + (p[1] || '').charAt(0)).toUpperCase(); }
	// Show the person, not a glyph: photo when there is one, initials on the brand tint otherwise.
	function avatar(p, size, round) {
		size = size || 40;
		var cls = 'symbol symbol-' + size + 'px' + (round === false ? '' : ' symbol-circle') + ' flex-shrink-0';
		if (p && p.photo) return '<div class="' + cls + '"><img src="assets/media/avatars/' + p.photo + '" alt="' + esc(T(p.name)) + '" /></div>';
		return '<div class="' + cls + '"><span class="symbol-label fw-bold" style="background:#E7F0FF;color:#0E4A9E;">' + esc(initials(p ? (p.name.en || p.name.ar) : '?')) + '</span></div>';
	}
	function personCell(p, sub, href, size) {
		var nm = esc(T(p.name));
		return '<div class="d-flex align-items-center min-w-0">' + avatar(p, size || 40) +
			'<div class="d-flex flex-column ms-3 min-w-0">' +
			(href ? '<a href="' + href + '" class="text-gray-900 text-hover-primary fs-6 fw-bold text-truncate">' + nm + '</a>' : '<span class="text-gray-900 fs-6 fw-bold text-truncate">' + nm + '</span>') +
			(sub ? '<span class="text-muted fw-semibold fs-7 text-truncate">' + sub + '</span>' : '') + '</div></div>';
	}

	/* ---------------- House components ---------------- */
	function badge(kind, text, extra) { return '<span class="badge badge-light-' + kind + ' fw-bold fs-8 ' + (extra || '') + '">' + text + '</span>'; }
	function code(text) { return '<span class="badge hms-code fw-bold fs-8 num">' + esc(text) + '</span>'; }
	function reuse(ar, en) { return '<span class="hms-reuse"><i class="ki-outline ki-abstract-26 fs-8" style="color:#7239EA"></i>' + L('يعيد استخدام: ', 'Reuses: ') + L(ar, en) + '</span>'; }

	function toolbar(title, crumbs, actions) {
		var bc = '<li class="breadcrumb-item text-muted"><a href="#/dashboard" class="text-muted text-hover-primary">' + L('المستشفى', 'Hospital') + '</a></li>';
		(crumbs || []).forEach(function (c) {
			bc += '<li class="breadcrumb-item"><span class="bullet bg-gray-500 w-5px h-2px"></span></li>';
			bc += '<li class="breadcrumb-item text-muted">' + (c.href ? '<a href="' + c.href + '" class="text-muted text-hover-primary">' + c.t + '</a>' : c.t) + '</li>';
		});
		return '<div class="app-container container-fluid d-flex flex-stack flex-wrap gap-3">' +
			'<div class="page-title d-flex flex-column justify-content-center gap-1"><h1 class="page-heading text-gray-900 fw-bold fs-3 m-0">' + title + '</h1>' +
			'<ul class="breadcrumb breadcrumb-separatorless fw-semibold fs-7 my-0">' + bc + '</ul></div>' +
			'<div class="d-flex align-items-center gap-2 gap-lg-3 d-print-none">' + (actions || '') + '</div></div>';
	}
	function btn(kind, icon, text, attrs) { return '<button type="button" class="btn btn-sm btn-' + kind + '" ' + (attrs || '') + '>' + (icon ? '<i class="ki-outline ' + icon + ' fs-4"></i>' : '') + text + '</button>'; }
	// "Go to" arrows point the reading direction: left on an Arabic page, right on an English one.
	function link(kind, icon, text, href) { if (icon === 'ki-arrow-left' && !isAr) icon = 'ki-arrow-right'; return '<a href="' + href + '" class="btn btn-sm btn-' + kind + '">' + (icon ? '<i class="ki-outline ' + icon + ' fs-4"></i>' : '') + text + '</a>'; }

	// The TrialBalance banner: icon tile, title, ONE sentence, and a solid badge carrying the figure.
	function notice(o) {
		var kind = o.kind || 'info';
		var badgeHtml = o.badge ? '<span class="badge fs-7 fw-bold px-4 py-2 flex-shrink-0 ' + (kind === 'danger' ? 'badge-danger' : kind === 'success' ? 'badge-success' : 'hms-badge-solid') + '">' + o.badge + '</span>' : '';
		var wrap = kind === 'danger' ? 'bg-light-danger border-danger' : kind === 'success' ? 'bg-light-success border-success' : 'hms-notice';
		var tile = kind === 'danger' ? 'bg-danger' : kind === 'success' ? 'bg-success' : '';
		return '<div class="notice d-flex rounded border border-dashed p-6 ' + wrap + ' ' + (o.cls || 'mb-5') + '">' +
			'<span class="symbol symbol-45px me-5 flex-shrink-0"><span class="symbol-label ' + tile + '"><i class="ki-outline ' + (o.icon || 'ki-information-5') + ' fs-2x text-white"></i></span></span>' +
			'<div class="d-flex flex-stack flex-grow-1 flex-wrap flex-md-nowrap"><div class="mb-3 mb-md-0 fw-semibold pe-5">' +
			'<h4 class="text-gray-900 fw-bold mb-1">' + o.title + '</h4><div class="fs-6 text-gray-700">' + o.text + '</div></div>' + badgeHtml + '</div></div>';
	}
	// The ItemLedger statistic card.
	function stats(items, col) {
		col = col || 'col-sm-6 col-xl-3';
		return '<div class="row g-5 g-xl-8 mb-5">' + items.map(function (s) {
			return '<div class="' + col + '"><div class="card card-flush h-100"><div class="card-body py-5">' +
				'<div class="d-flex flex-stack"><div class="text-muted fw-semibold fs-7 mb-1">' + s.label + '</div>' + (s.icon ? '<i class="ki-outline ' + s.icon + ' fs-2 text-gray-400"></i>' : '') + '</div>' +
				'<div class="fs-2hx fw-bold ' + (s.cls || 'text-gray-900') + '">' + s.value + '</div>' +
				'<div class="text-muted fs-8 mt-1">' + (s.sub || '&nbsp;') + '</div></div></div></div>';
		}).join('') + '</div>';
	}
	function card(o) {
		var head = '';
		if (o.title || o.toolbar) {
			head = '<div class="card-header border-0 pt-6"><div class="card-title align-items-start flex-column m-0">' +
				'<span class="card-label fw-bold text-gray-900 fs-4">' + (o.title || '') + '</span>' +
				(o.sub ? '<span class="text-muted mt-1 fw-semibold fs-7">' + o.sub + '</span>' : '') + '</div>' +
				(o.toolbar ? '<div class="card-toolbar">' + o.toolbar + '</div>' : '') + '</div>';
		}
		return '<div class="card ' + (o.cls || 'mb-5 mb-xl-8') + '">' + head + '<div class="card-body ' + (o.bodyCls || 'pt-4') + '">' + o.body + '</div></div>';
	}
	// House table. cols: [{t, cls, w}], rows: array of arrays of cell html (or {cls, cells}).
	function table(cols, rows, o) {
		o = o || {};
		var th = cols.map(function (c, i) {
			return '<th class="' + (i === 0 ? 'ps-4 ' : '') + (i === cols.length - 1 ? 'pe-4 ' : '') + (c.cls || '') + '"' + (c.w ? ' style="width:' + c.w + '"' : '') + '>' + c.t + '</th>';
		}).join('');
		var tb = rows.map(function (r) {
			var cells = Array.isArray(r) ? r : r.cells;
			return '<tr class="' + (r.cls || '') + '"' + (r.attrs || '') + '>' + cells.map(function (c, i) {
				var cc = cols[i] || {};
				return '<td class="' + (i === 0 ? 'ps-4 ' : '') + (i === cells.length - 1 ? 'pe-4 ' : '') + (cc.cls || '') + '">' + c + '</td>';
			}).join('') + '</tr>';
		}).join('');
		if (!rows.length) tb = '<tr><td colspan="' + cols.length + '" class="text-center text-muted py-10">' + L('لا توجد سجلات', 'No records') + '</td></tr>';
		return '<div class="table-responsive"><table class="table align-middle gy-4 mb-0 hms-table" style="font-variant-numeric:tabular-nums;">' +
			'<thead><tr class="fw-bold fs-8 text-uppercase text-muted">' + th + '</tr></thead><tbody class="fs-6">' + tb + '</tbody>' + (o.foot ? '<tfoot>' + o.foot + '</tfoot>' : '') + '</table></div>';
	}
	function searchBox(id, ph) {
		return '<div class="d-flex align-items-center position-relative my-1"><i class="ki-outline ki-magnifier fs-3 position-absolute ms-4"></i>' +
			'<input type="text" ' + (id ? 'id="' + id + '"' : '') + ' data-hms-filter class="form-control form-control-solid w-250px ps-12" placeholder="' + (ph || L('بحث...', 'Search...')) + '" autocomplete="off" /></div>';
	}
	function field(label, control, col) { return '<div class="' + (col || 'col-md-6') + ' fv-row"><label class="fw-semibold fs-7 text-gray-700 mb-2">' + label + '</label>' + control + '</div>'; }
	function input(val, attrs) { return '<input class="form-control form-control-solid" value="' + esc(val || '') + '" ' + (attrs || '') + ' />'; }
	function select(opts, val, attrs) {
		return '<select class="form-select form-select-solid" data-no-select2 ' + (attrs || '') + '>' + opts.map(function (o) {
			var v = typeof o === 'object' ? o.v : o, t = typeof o === 'object' ? o.t : o;
			return '<option value="' + esc(v) + '"' + (String(v) === String(val) ? ' selected' : '') + '>' + t + '</option>';
		}).join('') + '</select>';
	}
	function tabs(id, items, active) {
		return '<ul class="nav nav-stretch nav-line-tabs nav-line-tabs-2x border-transparent fs-6 fw-bold mb-6" role="tablist">' + items.map(function (it) {
			return '<li class="nav-item"><a class="nav-link text-active-primary py-4 me-6 ' + (it.k === active ? 'active' : '') + '" data-bs-toggle="tab" href="#' + id + '_' + it.k + '">' + it.t + (it.n != null ? ' <span class="badge badge-light-primary ms-1 num">' + it.n + '</span>' : '') + '</a></li>';
		}).join('') + '</ul>';
	}
	function pane(id, k, active, html) { return '<div class="tab-pane fade ' + (active ? 'show active' : '') + '" id="' + id + '_' + k + '" role="tabpanel">' + html + '</div>'; }
	function progress(pct, kind) { return '<div class="d-flex align-items-center w-100"><div class="progress h-6px w-100 bg-light-primary me-3"><div class="progress-bar bg-' + (kind || 'primary') + '" style="width:' + pct + '%"></div></div><span class="fw-bold fs-7 num">' + pct + '%</span></div>'; }

	function modal(title, body, footer, size) {
		document.getElementById('hms_modal_title').innerHTML = title;
		document.getElementById('hms_modal_body').innerHTML = body;
		document.getElementById('hms_modal_footer').innerHTML = footer || '<button class="btn btn-light" data-bs-dismiss="modal">' + L('إغلاق', 'Close') + '</button>';
		var dlg = document.querySelector('#hms_modal .modal-dialog');
		dlg.className = 'modal-dialog modal-dialog-centered ' + (size || 'mw-750px');
		bootstrap.Modal.getOrCreateInstance(document.getElementById('hms_modal')).show();
	}
	function toast(msg, kind) {
		if (window.toastr) { toastr.options = { positionClass: isAr ? 'toastr-top-left' : 'toastr-top-right', timeOut: 2600, rtl: isAr }; toastr[kind || 'success'](msg); }
	}

	/* ---------------- Router ---------------- */
	var routes = {};
	function route(name, def) { routes[name] = def; }
	function current() { var h = (location.hash || '#/dashboard').replace(/^#\/?/, ''); var p = h.split('/'); return { name: p[0] || 'dashboard', arg: p.slice(1).join('/') }; }

	function refusal(m) {
		return {
			title: T(m), crumbs: [{ t: T(m) }],
			html: '<div class="card"><div class="card-body p-10">' + notice({
				icon: 'ki-lock-2', title: L('هذه الوحدة غير مفعّلة لهذه المنشأة', 'This module is switched off for this facility'),
				text: L('تفعيلها من إعدادات المستشفى يضيفها للقائمة فوراً دون أي تغيير في البيانات.', 'Turning it on in Hospital settings adds it to the menu at once, with no data change.'),
				badge: T(m)
			}) + '<a href="#/settings" class="btn btn-sm btn-primary"><i class="ki-outline ki-setting-2 fs-4"></i>' + L('إعدادات المستشفى', 'Hospital settings') + '</a></div></div>'
		};
	}

	function render() {
		var r = current(), def = routes[r.name] || routes.dashboard;
		var out = def.module && !isOn(def.module) ? refusal(MOD[def.module]) : def.render(r.arg);
		document.getElementById('kt_app_toolbar').innerHTML = toolbar(out.title, out.crumbs, out.actions);
		document.getElementById('hms_content').innerHTML = out.html;
		document.title = out.title + ' — CrossBusiness';
		markMenu(r.name);
		if (window.KTMenu) KTMenu.createInstances();
		[].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]')).forEach(function (el) { bootstrap.Tooltip.getOrCreateInstance(el); });
		wireFilters();
		if (out.after) out.after();
		window.scrollTo(0, 0);
	}
	// Client-side row filter: any [data-hms-filter] input inside a card filters that card's tbody rows.
	function wireFilters() {
		[].slice.call(document.querySelectorAll('[data-hms-filter]')).forEach(function (inp) {
			var cardEl = inp.closest('.card'); if (!cardEl) return;
			inp.addEventListener('input', function () {
				var q = inp.value.trim().toLowerCase();
				[].slice.call(cardEl.querySelectorAll('tbody tr')).forEach(function (tr) { tr.style.display = !q || tr.textContent.toLowerCase().indexOf(q) !== -1 ? '' : 'none'; });
			});
		});
	}

	/* ---------------- Menu ---------------- */
	var MENU = [
		{ icon: 'ki-element-11', ar: 'لوحة المستشفى', en: 'Hospital overview', items: [
			{ r: 'dashboard', ar: 'لوحة التحكم', en: 'Dashboard' },
			{ r: 'map', ar: 'خريطة المستشفى', en: 'Hospital map' },
			{ r: 'blueprint', ar: 'خريطة الوحدة', en: 'Module blueprint' }] },
		{ icon: 'ki-profile-user', ar: 'المرضى والاستقبال', en: 'Patients & front desk', items: [
			{ r: 'patients', ar: 'سجل المرضى', en: 'Patient registry', m: 'mpi' },
			{ r: 'register', ar: 'تسجيل مريض', en: 'Register patient', m: 'mpi' },
			{ r: 'appointments', ar: 'المواعيد', en: 'Appointments', m: 'appointments' }] },
		{ icon: 'ki-briefcase', ar: 'العيادات', en: 'Clinics', items: [
			{ r: 'clinics', ar: 'لوحة العيادات', en: 'Clinics board', m: 'opd' },
			{ r: 'opd', ar: 'قائمة الطبيب', en: 'Doctor worklist', m: 'opd' },
			{ r: 'consult', ar: 'شاشة الكشف', en: 'Consultation', m: 'opd' },
			{ r: 'telemed', ar: 'الطب الاتصالي', en: 'Telemedicine', m: 'telemed' }] },
		{ icon: 'ki-pulse', ar: 'الطوارئ', en: 'Emergency', items: [
			{ r: 'er', ar: 'لوحة الطوارئ', en: 'ER board', m: 'er' }] },
		{ icon: 'ki-home-3', ar: 'التنويم', en: 'Inpatient care', items: [
			{ r: 'beds', ar: 'الأسرة والغرف', en: 'Beds & rooms', m: 'inpatient' },
			{ r: 'rooms', ar: 'أنواع الغرف', en: 'Room types', m: 'inpatient' },
			{ r: 'nursing', ar: 'محطة التمريض', en: 'Nursing station', m: 'nursing' },
			{ r: 'icu', ar: 'العناية المركزة', en: 'Intensive care', m: 'icu' },
			{ r: 'or', ar: 'جدول العمليات', en: 'Theatre schedule', m: 'or' }] },
		{ icon: 'ki-flask', ar: 'الخدمات التشخيصية', en: 'Diagnostics', items: [
			{ r: 'lab', ar: 'المختبر', en: 'Laboratory', m: 'lab' },
			{ r: 'radiology', ar: 'الأشعة', en: 'Radiology', m: 'radiology' },
			{ r: 'bloodbank', ar: 'بنك الدم', en: 'Blood bank', m: 'bloodbank' }] },
		{ icon: 'ki-capsule', ar: 'الصيدلية', en: 'Pharmacy', items: [
			{ r: 'pharmacy', ar: 'صرف الروشتات', en: 'Dispensing', m: 'pharmacy' },
			{ r: 'medroute', ar: 'توجيه الأدوية', en: 'Medication routing', m: 'pharmacy' }] },
		{ icon: 'ki-people', ar: 'الأطباء والعاملون', en: 'Doctors & staff', items: [
			{ r: 'practitioners', ar: 'الممارسون الصحيون', en: 'Practitioners' },
			{ r: 'fees', ar: 'كشوف الأتعاب', en: 'Fee statements', m: 'doctorfees' }] },
		{ icon: 'ki-bill', ar: 'المالية والتأمين', en: 'Finance & insurance', items: [
			{ r: 'billing', ar: 'الفوترة والخزينة', en: 'Billing & cashier', m: 'billing' },
			{ r: 'estimates', ar: 'التقديرات والتأمين المقدم', en: 'Estimates & deposits', m: 'billing' },
			{ r: 'cashier', ar: 'إغلاق الخزينة', en: 'Cashier closing', m: 'billing' },
			{ r: 'insurance', ar: 'الموافقات والمطالبات', en: 'Approvals & claims', m: 'insurance' },
			{ r: 'payers', ar: 'شركات التأمين والعقود', en: 'Payers & contracts', m: 'insurance' }] },
		{ icon: 'ki-shop', ar: 'الخدمات المساندة', en: 'Support services', items: [
			{ r: 'kitchen', ar: 'المطبخ والكافيتيريا', en: 'Kitchen & cafeteria', m: 'kitchen' },
			{ r: 'dietary', ar: 'التغذية العلاجية', en: 'Clinical dietary', m: 'dietary' },
			{ r: 'cssd', ar: 'التعقيم المركزي', en: 'Sterile supply', m: 'cssd' }] },
		{ icon: 'ki-shield-search', ar: 'الإمداد والجودة', en: 'Supply & quality', items: [
			{ r: 'requisitions', ar: 'طلبات الأقسام', en: 'Ward requisitions', m: 'supply' },
			{ r: 'incidents', ar: 'بلاغات الحوادث', en: 'Incident reports', m: 'quality' }] },
		{ icon: 'ki-chart-pie-simple', ar: 'التقارير', en: 'Reports', items: [
			{ r: 'reports', ar: 'تقارير المستشفى', en: 'Hospital reports' }] },
		{ icon: 'ki-setting-2', ar: 'الإعدادات', en: 'Settings', items: [
			{ r: 'settings', ar: 'إعدادات المستشفى', en: 'Hospital settings' },
			{ r: 'designer', ar: 'مصمم الأدوار والغرف', en: 'Floor & room designer', m: 'inpatient' },
			{ r: 'portal', ar: 'بوابة المريض', en: 'Patient portal', m: 'portal' }] }
	];

	function renderMenu() {
		var html = '<div class="menu-item px-3 mb-2 pt-2"><div class="menu-content"><span class="menu-heading fw-bold text-uppercase fs-7 text-gray-700">' + L('نظام المستشفى', 'Hospital system') + '</span></div></div>';
		MENU.forEach(function (cat) {
			var items = cat.items.filter(function (i) { return isOn(i.m); });
			if (!items.length) return;
			html += '<div class="menu-item menu-accordion cb-cat" data-kt-menu-trigger="click" data-hms-cat>' +
				'<span class="menu-link"><span class="menu-icon"><i class="ki-outline ' + cat.icon + ' fs-2"></i></span><span class="menu-title">' + L(cat.ar, cat.en) + '</span><span class="menu-arrow"></span></span>' +
				'<div class="menu-sub menu-sub-accordion">' + items.map(function (i) {
					return '<div class="menu-item"><a class="menu-link" data-hms-route="' + i.r + '" href="#/' + i.r + '"><span class="menu-bullet"><span class="bullet bullet-dot"></span></span><span class="menu-title">' + L(i.ar, i.en) + '</span></a></div>';
				}).join('') + '</div></div>';
		});
		document.getElementById('hms_menu').innerHTML = html;
	}
	function markMenu(name) {
		var alias = { patient: 'patients', 'lab-result': 'lab', practitioner: 'practitioners' };
		name = alias[name] || name;
		[].slice.call(document.querySelectorAll('#hms_menu [data-hms-route]')).forEach(function (a) {
			var on = a.getAttribute('data-hms-route') === name;
			a.classList.toggle('active', on);
			if (on) { var cat = a.closest('[data-hms-cat]'); if (cat) cat.classList.add('here', 'show'); }
		});
	}

	function renderHeader() {
		var c = country();
		document.getElementById('hms_lang_toggle').innerHTML = (isAr ? 'English' : 'العربية');
		document.getElementById('hms_search').setAttribute('placeholder', L('ابحث برقم الملف أو الاسم أو الهوية...', 'Search by MRN, name or ID...'));
		document.getElementById('hms_facility_chip').innerHTML =
			'<a href="#/settings" class="d-flex align-items-center gap-2 px-4 py-2 rounded bg-light-primary text-hover-primary">' +
			'<img src="assets/media/flags/' + c.flag + '.svg" class="w-20px h-20px rounded-circle" alt="" />' +
			'<span class="fw-bold fs-7 text-gray-800">' + T(settings.facility) + '</span>' +
			'<span class="badge hms-badge-solid fs-8">' + T(PRESETS.filter(function (p) { return p.key === settings.preset; })[0] || { ar: 'مخصص', en: 'Custom' }) + '</span></a>';
		var me = HMS.data.staff.me;
		document.getElementById('hms_user').innerHTML = '<div class="d-flex align-items-center gap-3">' + avatar(me, 40) +
			'<div class="d-none d-xl-flex flex-column"><span class="fw-bold fs-7 text-gray-900">' + T(me.name) + '</span><span class="text-muted fs-8">' + T(me.role) + '</span></div></div>';
		document.getElementById('hms_proto_note').textContent = L('نموذج أولي تفاعلي — بيانات تجريبية بالكامل', 'Interactive prototype — all data is sample data');
	}

	// Stored raw, not JSON: the head script reads it before any of this code exists.
	function toggleLang() { try { localStorage.setItem('hms.lang', isAr ? 'en' : 'ar'); } catch (e) { } location.reload(); }
	function globalSearch(e) {
		e.preventDefault();
		var q = document.getElementById('hms_search').value.trim().toLowerCase(); if (!q) return false;
		var hit = HMS.data.patients.filter(function (p) { return (p.mrn + ' ' + p.name.ar + ' ' + p.name.en + ' ' + p.nid).toLowerCase().indexOf(q) !== -1; })[0];
		if (hit) location.hash = '#/patient/' + hit.mrn; else toast(L('لا يوجد مريض مطابق', 'No matching patient'), 'info');
		return false;
	}

	window.HMS = {
		lang: lang, isAr: isAr, L: L, T: T, esc: esc, store: store,
		MODULES: MODULES, MOD: MOD, PRESETS: PRESETS, settings: settings, presetModules: presetModules, isOn: isOn, setModule: setModule, saveSettings: saveSettings,
		country: country, money: money, cur: cur, fmtNum: fmtNum, age: age, ageSex: ageSex, dateStr: dateStr, TODAY: TODAY,
		avatar: avatar, personCell: personCell, badge: badge, code: code, reuse: reuse,
		toolbar: toolbar, btn: btn, link: link, notice: notice, stats: stats, card: card, table: table, searchBox: searchBox,
		field: field, input: input, select: select, tabs: tabs, pane: pane, progress: progress, modal: modal, toast: toast,
		route: route, render: render, renderMenu: renderMenu, renderHeader: renderHeader, toggleLang: toggleLang, globalSearch: globalSearch,
		data: {}
	};
})();
