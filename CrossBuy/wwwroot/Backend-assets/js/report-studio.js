// =============================================================================================
// REPORT STUDIO V2 - THE DESIGNER.
//
// Lifted out of Views/Reports/Studio.cshtml, where 2,478 lines of it lived inline. Only four lines
// depended on the server, and those now arrive in the CBD object the view emits immediately before
// this file loads - so this is a plain script with no Razor in it and nothing to re-parse.
//
// THE MOVE WAS WORTH MAKING FOR ONE REASON ABOVE THE OTHERS. An inline script in a .cshtml dies
// SILENTLY if anything disturbs the closing body tag - no console error, no failed request, just a
// designer that does nothing. That failure mode is recorded in this repository because it has
// happened. A file cannot be killed that way.
//
// CBD is read ONCE, here at the top. Nothing below reaches back into the page for configuration,
// which is what keeps this file testable and what stops the view growing a second contract.
// =============================================================================================
"use strict";
(function () {

    // =============================================================================================
    // THE MODEL. This object — not the DOM — is the report.
    // =============================================================================================
    var AR = !!CBD.arabic;

    var BAND = { ReportHeader: 0, PageHeader: 1, GroupHeader: 2, Detail: 3, GroupFooter: 4, PageFooter: 5, ReportFooter: 6 };
    var KIND = { Text: 0, Field: 1, Image: 2, Line: 3, Rectangle: 4, SystemField: 5, Summary: 6, Table: 7, QrCode: 8, Chart: 9, CrossTab: 10, SubReport: 11, Icon: 12 };

    // The server's own drawings, handed over in window.CBD so there is exactly one definition of each
    // mark. See the comment beside `icons:` in Studio.cshtml for why a second copy would be a bug.
    var ICONS = (window.CBD && window.CBD.icons) || [];
    function iconArt(v) {
        for (var i = 0; i < ICONS.length; i++) { if (ICONS[i].v === v) { return ICONS[i]; } }
        return ICONS[0] || { v: 0, t: "", d: "" };
    }
    function iconSvg(v, px, colour) {
        return "<svg viewBox='0 0 24 24' width='" + px + "' height='" + px + "' fill='none' stroke='" +
               (colour || "currentColor") + "' stroke-width='1.7' stroke-linecap='round' " +
               "stroke-linejoin='round' aria-hidden='true'>" + iconArt(v).d + "</svg>";
    }
    var CHART = { Column: 0, Bar: 1, Line: 2, Pie: 3 };

    // A sub-report links to a GROUP, so a group band is where a linked one belongs; a report band
    // embeds the child whole and unlinked. Detail is out for the same reason a chart is: it renders
    // once with the page's run, so there is no single parent value there to link on.
    var SUB_GROUP_BANDS = [2, 4];      // GroupHeader, GroupFooter
    var SUB_REPORT_BANDS = [0, 6];     // ReportHeader, ReportFooter

    // The four bands a chart or a cross-tab may sit in. Both read a SCOPE of rows, and only these four
    // carry one: page bands have no rows at all, and a Detail band holds the current page's run — a chart
    // there would draw one page while looking like a chart of the report.
    var SCOPED_BANDS = [0, 2, 4, 6];   // ReportHeader, GroupHeader, GroupFooter, ReportFooter
    var SYS  = { CurrentDate: 0, CurrentDateTime: 1, PageNumber: 2, TotalPages: 3, PageXOfY: 4, ReportName: 5 };
    var AGG  = { None: 0, Sum: 1, Average: 2, Min: 3, Max: 4, Count: 5 };

    var BAND_LABEL = [
        AR ? "رأس التقرير"  : "Report header",
        AR ? "رأس الصفحة"   : "Page header",
        AR ? "رأس المجموعة" : "Group header",
        AR ? "التفاصيل"     : "Detail",
        AR ? "ذيل المجموعة" : "Group footer",
        AR ? "ذيل الصفحة"   : "Page footer",
        AR ? "ذيل التقرير"  : "Report footer"
    ];

    // Paper, in mm. The same table ReportPaper holds server-side; duplicated here ONLY to draw the sheet,
    // never to decide anything — the server recomputes the printable box from its own copy.
    var PAPER = { 0: [210, 297], 1: [148, 210], 2: [215.9, 279.4], 3: [215.9, 355.6], 4: [80, 297] };

    // FROM THE SERVER, not a literal. The list used to name Amiri, which is not installed on this
    // host - a designer that offers a face the render machine lacks produces a layout that looks one
    // way in the browser and another on paper with nothing to explain it. ReportTypography.DesignerFaces
    // is the same source the renderers read, and it puts the application's own two faces first.
    var FONTS = CBD.fonts;

    // The canvas sheet takes the document font so the designer shows what will print. Called from
    // renderCanvas; a null family falls back to the stylesheet, which is the platform stack.
    function applyDocFont() {
        // #cbd-page IS the sheet - the element renderCanvas sizes to the paper. Setting the family here
        // means every band, box and table face inside it inherits the document font, which is what the
        // print renderer does with body.cbv.
        var host = document.getElementById("cbd-page");
        if (!host) { return; }
        var pg = (S.layout && S.layout.page) || {};
        host.style.fontFamily = pg.fontFamily ? ('"' + pg.fontFamily + '"') : "";
        host.style.fontSize = pg.fontSizePt ? (pg.fontSizePt + "pt") : "";
    }

    function blankLayout() {
        var bands = [];
        for (var k = 0; k <= 6; k++) {
            bands.push({
                kind: k,
                heightMm: (k === BAND.Detail ? 8 : (k === BAND.GroupHeader || k === BAND.GroupFooter ? 0 : 18)),
                groupFieldKey: null,
                elements: []
            });
        }
        return {
            schemaVersion: 1,
            // EVERY page key is present, including the two that used to be absent. syncPageInputs reads
            // pg.fontFamily, and a state object that simply lacks the key cannot represent "the font this
            // report has" — so an opened template showed "Default" in the picker and the next save wrote
            // that emptiness back over the stored face. A missing key and a chosen null are not the same
            // thing, and only one of them is safe to save.
            page: {
                pageSize: 0, orientation: 0,
                marginTopMm: 18, marginBottomMm: 16, marginLeftMm: 12, marginRightMm: 12,

                // direction 0 = follow the language, which is what a new design wants: the same layout
                // read correctly in Arabic and in English. `rtl` is the retired flag — carried so a
                // rollback still deserialises, read by nothing.
                direction: 0, rtl: AR,

                // The renderers default all four to true, so a new design starts where they do. Present
                // as KEYS, not absent: a missing key and a chosen false are not the same thing, and the
                // page inputs have to be able to represent both.
                showHeader: true, showFooter: true, showPageNumbers: true, repeatHeaderRow: true,

                fontFamily: null, fontSizePt: null
            },
            gridMm: 5,
            snapToGrid: true,
            bands: bands,
            parameters: {}
        };
    }

    var S = {
        templateId: 0,
        name: "",
        nameEn: "",
        datasetCode: "",
        columns: [],
        filters: [],
        sorts: [],
        parameters: {},
        layout: blankLayout(),

        fields: [],          // StudioFieldOption[]
        paramDefs: [],       // StudioParameterOption[]
        assets: [],          // ReportAssetSummary[]

        selection: [],       // element ids
        assetTarget: null,   // element id waiting for the image the upload is about to return, or null
        selectedBand: BAND.Detail,
        zoom: 1,
        showGrid: true,
        clipboard: null
    };

    var undoStack = [], redoStack = [];

    // =============================================================================================
    // small helpers
    // =============================================================================================
    function $(id) { return document.getElementById(id); }
    function esc(v) {
        return String(v == null ? "" : v)
            .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
    }
    function uid() { return "e" + Math.random().toString(36).slice(2, 10); }
    function clone(o) { return JSON.parse(JSON.stringify(o)); }
    function token() {
        var el = document.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : "";
    }
    // Set once the toolbar is wired; loadPage() calls it so the segmented buttons follow a
    // template that was just opened. Declared here rather than inside the wiring closure because
    // loadPage() is outside it.
    var CBD_PAINT_SEGMENTS = null;

    function fieldOf(key) {
        for (var i = 0; i < S.fields.length; i++) { if (S.fields[i].key === key) { return S.fields[i]; } }
        return null;
    }
    function band(kind) {
        for (var i = 0; i < S.layout.bands.length; i++) { if (S.layout.bands[i].kind === kind) { return S.layout.bands[i]; } }
        return null;
    }
    function allElements() {
        var out = [];
        S.layout.bands.forEach(function (b) { b.elements.forEach(function (e) { out.push({ b: b, e: e }); }); });
        return out;
    }
    function elementById(id) {
        var hit = null;
        allElements().forEach(function (p) { if (p.e.id === id) { hit = p; } });
        return hit;
    }

    function paper() {
        var p = PAPER[S.layout.page.pageSize] || PAPER[0];
        return S.layout.page.orientation === 1 ? [p[1], p[0]] : [p[0], p[1]];
    }
    function contentWidthMm() {
        var pg = S.layout.page;
        return Math.max(10, paper()[0] - pg.marginLeftMm - pg.marginRightMm);
    }
    function contentHeightMm() {
        var pg = S.layout.page;
        return Math.max(10, paper()[1] - pg.marginTopMm - pg.marginBottomMm);
    }
    // mm → px at the current zoom. 96 CSS px per inch is the browser's own definition, so a page at 100%
    // is close to physical size on a typical display and exactly right in proportion at any zoom.
    function scale() { return (96 / 25.4) * S.zoom; }

    function snap(mm) {
        if (!S.layout.snapToGrid) { return Math.round(mm * 10) / 10; }
        var g = S.layout.gridMm > 0 ? S.layout.gridMm : 1;
        return Math.round(mm / g) * g;
    }

    // =============================================================================================
    // UNDO / REDO
    //
    // Snapshots of the MODEL, not of the DOM. 60 deep: enough to undo a session's worth of nudges and
    // small enough that the layouts never become the reason the tab runs out of memory.
    // =============================================================================================
    function snapshot() {
        undoStack.push(JSON.stringify({ layout: S.layout, columns: S.columns, filters: S.filters, sorts: S.sorts, parameters: S.parameters }));
        if (undoStack.length > 60) { undoStack.shift(); }
        redoStack.length = 0;
        syncUndoButtons();
    }
    function restore(json) {
        var st = JSON.parse(json);
        S.layout = st.layout; S.columns = st.columns; S.filters = st.filters; S.sorts = st.sorts; S.parameters = st.parameters;
        S.selection = [];
        renderAll();
    }
    function undo() {
        if (!undoStack.length) { return; }
        redoStack.push(JSON.stringify({ layout: S.layout, columns: S.columns, filters: S.filters, sorts: S.sorts, parameters: S.parameters }));
        restore(undoStack.pop());
        syncUndoButtons();
    }
    function redo() {
        if (!redoStack.length) { return; }
        undoStack.push(JSON.stringify({ layout: S.layout, columns: S.columns, filters: S.filters, sorts: S.sorts, parameters: S.parameters }));
        restore(redoStack.pop());
        syncUndoButtons();
    }
    function syncUndoButtons() {
        $("cbd-undo").disabled = undoStack.length === 0;
        $("cbd-redo").disabled = redoStack.length === 0;
    }

    // =============================================================================================
    // THE TOOLBOX — twenty-two placeable things, each one a plain element record.
    //
    // Note there is no "logo goes top-left" tool and no "signature block". §5 is explicit that a logo,
    // a signature and a stamp must be placeable ANYWHERE; they are Image elements with a role, and the
    // role affects nothing but the default picture and the label.
    // =============================================================================================
    var TOOLS = [
        { id: "text",    icon: "ki-text-align-left",   label: AR ? "نص"              : "Text label",     make: function () { return el(KIND.Text, { text: AR ? "نص" : "Text", widthMm: 40, heightMm: 6 }); } },
        { id: "title",   icon: "ki-text-bold",         label: AR ? "عنوان"           : "Title",          make: function () { return el(KIND.Text, { text: AR ? "عنوان التقرير" : "Report title", widthMm: 90, heightMm: 10, style: { fontSizePt: 16, bold: true, align: 1 } }); } },
        { id: "field",   icon: "ki-abstract-25",       label: AR ? "حقل"             : "Data field",     make: function () { return el(KIND.Field, { widthMm: 35, heightMm: 6 }); } },
        { id: "table",   icon: "ki-row-horizontal",    label: AR ? "جدول"            : "Table",          make: function () { return el(KIND.Table, { widthMm: Math.min(contentWidthMm(), 160), heightMm: 30, columns: [] }); } },
        { id: "line-h",  icon: "ki-minus",             label: AR ? "خط أفقي"         : "Horizontal line", make: function () { return el(KIND.Line, { widthMm: 60, heightMm: 0.4, style: { borderStyle: 1, borderColor: "#000000" } }); } },
        { id: "line-v",  icon: "ki-text-italic",       label: AR ? "خط رأسي"         : "Vertical line",  make: function () { return el(KIND.Line, { widthMm: 0.4, heightMm: 20, style: { borderStyle: 1, borderColor: "#000000" } }); } },
        { id: "rect",    icon: "ki-abstract-41",       label: AR ? "مستطيل"          : "Rectangle",      make: function () { return el(KIND.Rectangle, { widthMm: 50, heightMm: 20, style: { borderStyle: 1, borderColor: "#000000" } }); } },
        { id: "logo",    icon: "ki-picture",           label: AR ? "شعار الشركة"     : "Company logo",   make: function () { return el(KIND.Image, { imageRole: 1, widthMm: 35, heightMm: 18 }); } },
        { id: "brlogo",  icon: "ki-shop",              label: AR ? "شعار الفرع"      : "Branch logo",    make: function () { return el(KIND.Image, { imageRole: 4, widthMm: 30, heightMm: 16 }); } },
        { id: "sign",    icon: "ki-pencil",            label: AR ? "توقيع"           : "Signature",      make: function () { return el(KIND.Image, { imageRole: 2, widthMm: 40, heightMm: 18 }); } },
        { id: "stamp",   icon: "ki-shield-tick",       label: AR ? "ختم"             : "Stamp",          make: function () { return el(KIND.Image, { imageRole: 3, widthMm: 30, heightMm: 30 }); } },
        { id: "image",   icon: "ki-picture",           label: AR ? "صورة"            : "Image",          make: function () { return el(KIND.Image, { imageRole: 0, widthMm: 35, heightMm: 25 }); } },
        { id: "sum",     icon: "ki-plus-square",       label: AR ? "مجموع"           : "Sum",            make: function () { return el(KIND.Summary, { aggregate: AGG.Sum, widthMm: 35, heightMm: 6, style: { align: 2, bold: true } }); } },
        { id: "avg",     icon: "ki-chart-simple",      label: AR ? "متوسط"           : "Average",        make: function () { return el(KIND.Summary, { aggregate: AGG.Average, widthMm: 35, heightMm: 6, style: { align: 2 } }); } },
        { id: "min",     icon: "ki-down",              label: AR ? "أصغر قيمة"       : "Minimum",        make: function () { return el(KIND.Summary, { aggregate: AGG.Min, widthMm: 35, heightMm: 6, style: { align: 2 } }); } },
        { id: "max",     icon: "ki-up",                label: AR ? "أكبر قيمة"       : "Maximum",        make: function () { return el(KIND.Summary, { aggregate: AGG.Max, widthMm: 35, heightMm: 6, style: { align: 2 } }); } },
        { id: "count",   icon: "ki-abstract-14",       label: AR ? "عدد"             : "Count",          make: function () { return el(KIND.Summary, { aggregate: AGG.Count, widthMm: 30, heightMm: 6, style: { align: 2 } }); } },
        { id: "pageno",  icon: "ki-abstract-24",       label: AR ? "رقم الصفحة"      : "Page number",    make: function () { return el(KIND.SystemField, { systemField: SYS.PageNumber, widthMm: 20, heightMm: 6, style: { align: 1 } }); } },
        { id: "pagexy",  icon: "ki-abstract-23",       label: AR ? "صفحة س من ص"     : "Page X of Y",    make: function () { return el(KIND.SystemField, { systemField: SYS.PageXOfY, widthMm: 32, heightMm: 6, style: { align: 1 } }); } },
        { id: "pages",   icon: "ki-abstract-22",       label: AR ? "عدد الصفحات"     : "Total pages",    make: function () { return el(KIND.SystemField, { systemField: SYS.TotalPages, widthMm: 20, heightMm: 6 }); } },
        { id: "date",    icon: "ki-calendar",          label: AR ? "التاريخ"         : "Print date",     make: function () { return el(KIND.SystemField, { systemField: SYS.CurrentDate, widthMm: 30, heightMm: 6 }); } },
        { id: "datetime",icon: "ki-time",              label: AR ? "التاريخ والوقت"  : "Date and time",  make: function () { return el(KIND.SystemField, { systemField: SYS.CurrentDateTime, widthMm: 42, heightMm: 6 }); } },
        { id: "repname", icon: "ki-document",          label: AR ? "اسم التقرير"     : "Report name",    make: function () { return el(KIND.SystemField, { systemField: SYS.ReportName, widthMm: 60, heightMm: 8, style: { bold: true } }); } },
        { id: "qr",      icon: "ki-scan-barcode",      label: AR ? "رمز QR"          : "QR code",        make: function () { return el(KIND.QrCode, { widthMm: 25, heightMm: 25 }); } },
        { id: "colchart",icon: "ki-chart-simple-2",    label: AR ? "أعمدة بيانية"    : "Column chart",   make: function () { return el(KIND.Chart, { chartKind: CHART.Column, widthMm: 90, heightMm: 55 }); } },
        { id: "barchart",icon: "ki-chart-simple-3",    label: AR ? "أشرطة بيانية"    : "Bar chart",      make: function () { return el(KIND.Chart, { chartKind: CHART.Bar, widthMm: 90, heightMm: 55 }); } },
        { id: "linechart",icon: "ki-chart-line-down",  label: AR ? "خط بياني"        : "Line chart",     make: function () { return el(KIND.Chart, { chartKind: CHART.Line, widthMm: 90, heightMm: 50 }); } },
        { id: "piechart",icon: "ki-chart-pie-simple",  label: AR ? "دائرة بيانية"    : "Pie chart",      make: function () { return el(KIND.Chart, { chartKind: CHART.Pie, widthMm: 85, heightMm: 50 }); } },
        { id: "icon",    icon: "ki-medal-star",        label: AR ? "أيقونة"          : "Icon",           make: function () { return el(KIND.Icon, { icon: 0, widthMm: 12, heightMm: 12, style: { color: "#166fe5" } }); } },
        { id: "crosstab",icon: "ki-abstract-26",       label: AR ? "جدول محوري"      : "Cross-tab",      make: function () { return el(KIND.CrossTab, { widthMm: 120, heightMm: 50 }); } },
        { id: "subrep",  icon: "ki-questionnaire-tablet", label: AR ? "تقرير فرعي"   : "Sub-report",     make: function () { return el(KIND.SubReport, { widthMm: 130, heightMm: 40 }); } }
    ];

    // The reports this reader may run, for the sub-report picker. Fetched once and cached: the catalogue
    // endpoint already filters by permission, so the list can only ever offer a child they could open
    // directly - the picker is not a place where access is decided.
    var SUBCAT = null;
    function loadSubCatalog(then) {
        if (SUBCAT) { then(SUBCAT); return; }
        fetch("/api/reports/center/catalog", { credentials: "same-origin" })
            .then(function (r) { return r.ok ? r.json() : []; })
            .then(function (list) { SUBCAT = list || []; then(SUBCAT); })
            .catch(function () { SUBCAT = []; then(SUBCAT); });
    }

    function el(kind, over) {
        var e = {
            id: uid(), kind: kind, xMm: 5, yMm: 3, widthMm: 40, heightMm: 6, z: 0,
            text: null, textEn: null, fieldKey: null, systemField: SYS.CurrentDate, aggregate: AGG.Sum,
            assetId: null, imageRole: 0, fit: 0, preserveAspect: true, columns: [],
            qrEcc: 2, qrModulePixels: 8,
            icon: 0,
            compare: 0, compareHigherIsBetter: true,
            categoryFieldKey: null, seriesFieldKey: null, chartKind: CHART.Column,
            maxCategories: 12, showValues: true, showGrandTotals: true,
            subReportCode: null, linkChildFieldKey: null,
            style: {
                fontFamily: null, fontSizePt: 9, bold: false, italic: false, underline: false,
                align: 0, verticalAlign: 0, color: null, background: null, borderColor: null,
                borderStyle: 0, borderWidthMm: 0.2, paddingMm: 0.5, numberFormat: null, dateFormat: null, visible: true
            }
        };
        over = over || {};
        Object.keys(over).forEach(function (k) {
            if (k === "style") { Object.keys(over.style).forEach(function (sk) { e.style[sk] = over.style[sk]; }); }
            else { e[k] = over[k]; }
        });
        return e;
    }

    function renderToolbox() {
        $("cbd-toolbox").innerHTML = TOOLS.map(function (t) {
            return "<button type='button' draggable='true' data-tool='" + t.id + "' " +
                   "class='btn btn-sm btn-light justify-content-start fw-semibold fs-8 py-2 cbd-tool'>" +
                   "<i class='ki-outline " + t.icon + " fs-5 me-2'></i>" + esc(t.label) + "</button>";
        }).join("");

        Array.prototype.forEach.call($("cbd-toolbox").querySelectorAll("[data-tool]"), function (b) {
            b.addEventListener("dragstart", function (ev) {
                ev.dataTransfer.setData("text/plain", "tool:" + b.getAttribute("data-tool"));
                ev.dataTransfer.effectAllowed = "copy";
            });
            b.addEventListener("click", function () { addTool(b.getAttribute("data-tool"), null); });
        });
    }

    function toolById(id) {
        for (var i = 0; i < TOOLS.length; i++) { if (TOOLS[i].id === id) { return TOOLS[i]; } }
        return null;
    }

    function addTool(toolId, at) {
        var t = toolById(toolId);
        if (!t) { return; }
        var e = t.make();
        var b = band(at ? at.kind : S.selectedBand) || band(BAND.Detail);
        if (at) { e.xMm = snap(at.xMm); e.yMm = snap(at.yMm); }
        e.z = nextZ(b);
        snapshot();
        b.elements.push(e);
        S.selectedBand = b.kind;
        S.selection = [e.id];
        renderAll();
    }

    function nextZ(b) {
        var max = 0;
        b.elements.forEach(function (e) { if (e.z > max) { max = e.z; } });
        return max + 1;
    }

    // =============================================================================================
    // WHICH BAND AN ELEMENT LIVES IN — and how it changes bands.
    //
    // An element's Y is LOCAL to its band, so "where is it on the page" needs the band's own top, and
    // "which band is the pointer over" needs the reverse. Both walk S.layout.bands in ARRAY order rather
    // than by kind, because that is the order renderCanvas draws them in and a loaded draft is not
    // guaranteed to list them 0..6.
    //
    // WHY THIS EXISTS AT ALL: an element's band used to be fixed at the moment it was created. Dragging
    // it never changed its band — beginMove only edited xMm/yMm, and yMm was clamped at 0, so an image
    // added to Detail could be dragged up to its own band's top and no further. The properties panel
    // showed the band as plain text. So a picture that landed in the wrong band could not be moved into
    // the right one at all; the only way out was to delete it and add it again with the target band
    // selected first. That is what "I cannot add the image to the band marked in red" was.
    // =============================================================================================
    function bandTopMm(kind) {
        var top = 0;
        for (var i = 0; i < S.layout.bands.length; i++) {
            if (S.layout.bands[i].kind === kind) { return top; }
            top += S.layout.bands[i].heightMm || 0;
        }
        return top;
    }

    // The band containing a Y measured from the top of the content box. Zero-height bands are skipped —
    // they occupy no pixels, so no pointer can be "over" one. Past the last band (the blank area below,
    // which exists whenever the page is taller than the bands) the last band with real height wins,
    // rather than the element ending up owned by nothing.
    function bandAtMm(absY) {
        var top = 0, last = null;
        for (var i = 0; i < S.layout.bands.length; i++) {
            var b = S.layout.bands[i], h = b.heightMm || 0;
            if (h > 0) {
                last = b;
                if (absY >= top && absY < top + h) { return b; }
            }
            top += h;
        }
        return last;
    }

    // Re-homes one element into another band, keeping the element itself untouched apart from its
    // stacking order, which has to be recomputed against its new neighbours. Returns the new band.
    function moveToBand(pair, targetKind) {
        var target = band(targetKind);
        if (!pair || !target || pair.b.kind === targetKind) { return null; }

        var at = pair.b.elements.indexOf(pair.e);
        if (at >= 0) { pair.b.elements.splice(at, 1); }
        pair.e.z = nextZ(target);
        target.elements.push(pair.e);
        S.selectedBand = target.kind;
        return target;
    }

    // =============================================================================================
    // THE CANVAS
    // =============================================================================================
    function renderCanvas() {
        var sc = scale();
        var pw = paper()[0], ph = paper()[1];
        var pg = S.layout.page;

        var pageEl = $("cbd-page");
        pageEl.style.width = (pw * sc) + "px";
        pageEl.style.direction = pg.rtl ? "rtl" : "ltr";
        applyDocFont();

        var bandsHeight = S.layout.bands.reduce(function (a, b) { return a + (b.heightMm || 0); }, 0);
        var boxHeight = Math.max(contentHeightMm(), bandsHeight);
        pageEl.style.height = ((boxHeight + pg.marginTopMm + pg.marginBottomMm) * sc) + "px";

        var content = $("cbd-content");
        content.style.top = (pg.marginTopMm * sc) + "px";
        content.style.insetInlineStart = (pg.marginLeftMm * sc) + "px";
        content.style.width = (contentWidthMm() * sc) + "px";
        content.style.height = (boxHeight * sc) + "px";
        content.style.backgroundImage = "";

        if (S.showGrid && S.layout.gridMm > 0) {
            var g = S.layout.gridMm * sc;
            // Two 1px lines per axis. Drawn as a background so the grid costs nothing per element and
            // never lands in the saved model.
            content.style.backgroundImage =
                "linear-gradient(to right, rgba(0,0,0,.06) 1px, transparent 1px)," +
                "linear-gradient(to bottom, rgba(0,0,0,.06) 1px, transparent 1px)";
            content.style.backgroundSize = g + "px " + g + "px";
        }

        content.innerHTML = S.layout.bands.map(function (b) { return bandHtml(b, sc); }).join("");

        wireBands(sc);
        wireElements(sc);
        markMissingImages(content);
        renderRulers(sc);
        renderStatus();
    }

    function bandHtml(b, sc) {
        var h = Math.max(0, b.heightMm || 0) * sc;
        var selected = S.selectedBand === b.kind;
        var tag = BAND_LABEL[b.kind] + " · " + (b.heightMm || 0) + "mm";

        var inner = b.elements
            .slice()
            .sort(function (x, y) { return (x.z || 0) - (y.z || 0); })
            .map(function (e) { return elementHtml(e, sc); })
            .join("");

        // THE RESIZE GRIP: the bottom few pixels of the band, where a report designer's band splitter has
        // always been. Only for bands that HAVE a height — a 0mm band's grip would sit on top of the
        // previous band's grip, and two overlapping splitters make the boundary ambiguous to grab. A 0mm
        // band is opened from the number box in the الأقسام / Bands tab, which is also what the grip
        // writes to, so the two agree.
        var grip = (b.heightMm || 0) > 0
            ? "<span class='cbd-band-resize' data-bandgrip='" + b.kind + "' title='" +
              esc(AR ? "اسحب لتغيير ارتفاع القسم" : "Drag to change the band's height") + "'></span>"
            : "";

        return "<div class='cbd-band" + (selected ? " sel" : "") + "' data-band='" + b.kind + "' " +
               "style='height:" + h + "px'>" +
               "<span class='cbd-band-tag'>" + esc(tag) + "</span>" + inner + grip + "</div>";
    }

    function elementHtml(e, sc) {
        var s = e.style || {};
        var st = [];
        st.push("inset-inline-start:" + (e.xMm * sc) + "px");
        st.push("top:" + (e.yMm * sc) + "px");
        st.push("width:" + (e.widthMm * sc) + "px");
        st.push("height:" + (e.heightMm * sc) + "px");
        st.push("z-index:" + (e.z || 0));
        st.push("font-size:" + (s.fontSizePt * (96 / 72) * S.zoom) + "px");
        if (s.fontFamily) { st.push("font-family:" + s.fontFamily); }
        if (s.bold) { st.push("font-weight:700"); }
        if (s.italic) { st.push("font-style:italic"); }
        if (s.underline) { st.push("text-decoration:underline"); }
        if (s.color) { st.push("color:" + s.color); }
        if (s.background) { st.push("background:" + s.background); }
        if (s.borderStyle) {
            st.push("border:" + Math.max(1, s.borderWidthMm * sc) + "px " +
                    ["none", "solid", "dashed", "dotted"][s.borderStyle] + " " + (s.borderColor || "#000"));
        }
        st.push("padding:" + (s.paddingMm * sc) + "px");
        st.push("justify-content:" + ["flex-start", "center", "flex-end", "space-between"][s.align || 0]);
        st.push("align-items:" + ["flex-start", "center", "flex-end"][s.verticalAlign || 0]);
        if (s.visible === false) { st.push("opacity:.35"); }

        var sel = S.selection.indexOf(e.id) >= 0;
        // EIGHT HANDLES, NOT FOUR. The corners change both dimensions at once, so with only corners
        // there was no way to set a height without also disturbing the width - and a chart or a table
        // that is the right width and the wrong height is the common case, not the rare one. The four
        // edge handles each move ONE axis and leave the other exactly where the author put it.
        var handles = sel
            ? "<span class='cbd-handle nw' data-h='nw'></span><span class='cbd-handle ne' data-h='ne'></span>" +
              "<span class='cbd-handle sw' data-h='sw'></span><span class='cbd-handle se' data-h='se'></span>" +
              "<span class='cbd-handle n'  data-h='n'></span><span class='cbd-handle s'  data-h='s'></span>" +
              "<span class='cbd-handle w'  data-h='w'></span><span class='cbd-handle e'  data-h='e'></span>"
            : "";

        // cbd-el-img is what lets an image FILL its frame instead of dictating its own height — see the
        // rule of that name in the stylesheet.
        return "<div class='cbd-el" + (sel ? " sel" : "") + (e.kind === KIND.Image ? " cbd-el-img" : "") +
               "' data-el='" + e.id + "' data-kind='" + e.kind + "' style='" + st.join(";") + "'>" +
               "<span class='cbd-el-body'>" + elementFace(e) + "</span>" + handles + "</div>";
    }

    // What the element LOOKS like on the canvas. A design-time face, deliberately not a data preview:
    // a Field shows its field name in braces so the designer can see the binding, and real values come
    // from Preview, which runs the actual report.
    function elementFace(e) {
        switch (e.kind) {
            case KIND.Text:
                return esc(e.text || "");
            case KIND.Field: {
                var f = fieldOf(e.fieldKey);
                return "<span class='text-primary'>{" + esc(f ? f.title : (e.fieldKey || (AR ? "غير مرتبط" : "unbound"))) + "}</span>";
            }
            case KIND.SystemField: {
                var names = [
                    AR ? "{التاريخ}" : "{Date}",
                    AR ? "{التاريخ والوقت}" : "{Date and time}",
                    AR ? "{رقم الصفحة}" : "{Page}",
                    AR ? "{عدد الصفحات}" : "{Pages}",
                    AR ? "{صفحة س من ص}" : "{Page X of Y}",
                    AR ? "{اسم التقرير}" : "{Report name}"
                ];
                return "<span class='text-info'>" + esc(names[e.systemField] || "") + "</span>";
            }
            case KIND.Summary: {
                var f2 = fieldOf(e.fieldKey);
                var agg = ["", "SUM", "AVG", "MIN", "MAX", "COUNT"][e.aggregate] || "";
                return "<span class='text-success'>" + esc(agg) + "(" + esc(f2 ? f2.title : (e.fieldKey || "?")) + ")</span>";
            }
            case KIND.QrCode: {
                // A DESIGN-TIME FACE, like every other element here: the canvas says what the code
                // WILL carry, not what it encodes. Generating a real QR per keystroke would be a
                // round trip per drag, and the Preview button runs the actual report.
                var qf = fieldOf(e.fieldKey);
                var what = qf ? ("{" + qf.title + "}")
                             : ((e.text || "").trim() ? e.text : (AR ? "غير مرتبط" : "unbound"));
                return "<span class='text-muted fs-8 d-inline-flex align-items-center gap-1'>" +
                       "<i class='ki-outline ki-scan-barcode fs-4'></i>" + esc(what) + "</span>";
            }
            case KIND.Icon:
                // THE ONLY ELEMENT WHOSE DESIGN-TIME FACE IS THE REAL OUTPUT. A chart has to be named
                // rather than drawn here because its bars need rows; an icon needs nothing, so the
                // author sees on the canvas exactly what will print. Sized to the element's own box, so
                // dragging a handle changes the mark and not a caption about it.
                return "<span class='cbd-icon-face'>" +
                       iconSvg(e.icon || 0, "100%", (e.style && e.style.color) || "#3F4254") + "</span>";

            case KIND.Chart: {
                // A DESIGN-TIME FACE, same rule as the QR: the canvas names what will be drawn, it does
                // not draw it. The real bars need the real rows, and Preview runs the real report.
                var cf = fieldOf(e.categoryFieldKey), mf = fieldOf(e.fieldKey);
                var icons = ["ki-chart-simple-2", "ki-chart-simple-3", "ki-chart-line-down", "ki-chart-pie-simple"];
                var kinds = AR ? ["أعمدة", "أشرطة", "خط", "دائرة"] : ["Column", "Bar", "Line", "Pie"];
                var agg3 = ["", "SUM", "AVG", "MIN", "MAX", "COUNT", "COUNTD"][e.aggregate] || "";
                return "<span class='text-primary fs-8 d-inline-flex align-items-center gap-1'>" +
                       "<i class='ki-outline " + (icons[e.chartKind] || icons[0]) + " fs-3'></i>" +
                       esc(kinds[e.chartKind] || "") + " · " + esc(agg3) + "(" + esc(mf ? mf.title : (e.fieldKey || "?")) + ")" +
                       " / " + esc(cf ? cf.title : (AR ? "بلا محور" : "no axis")) + "</span>";
            }
            case KIND.CrossTab: {
                var xr = fieldOf(e.categoryFieldKey), xc = fieldOf(e.seriesFieldKey), xm = fieldOf(e.fieldKey);
                var agg4 = ["", "SUM", "AVG", "MIN", "MAX", "COUNT", "COUNTD"][e.aggregate] || "";
                return "<span class='text-primary fs-8 d-inline-flex align-items-center gap-1'>" +
                       "<i class='ki-outline ki-abstract-26 fs-3'></i>" +
                       esc(agg4) + "(" + esc(xm ? xm.title : (e.fieldKey || "?")) + ")" +
                       " · " + esc(xr ? xr.title : "?") + " × " + esc(xc ? xc.title : "?") + "</span>";
            }
            case KIND.SubReport: {
                var rep = (SUBCAT || []).filter(function (r) { return r.code === e.subReportCode; })[0];
                var name = rep ? (AR ? rep.titleAr : rep.titleEn) : (e.subReportCode || (AR ? "غير محدد" : "not set"));
                return "<span class='text-primary fs-8 d-inline-flex align-items-center gap-1'>" +
                       "<i class='ki-outline ki-questionnaire-tablet fs-3'></i>" + esc(name) +
                       (e.linkChildFieldKey ? (" · " + esc(e.linkChildFieldKey)) : "") + "</span>";
            }
            case KIND.Image: {
                if (e.assetId) {
                    return "<img src='/api/reports/studio/assets/" + encodeURIComponent(e.assetId) + "' alt='' " +
                           "style='object-fit:" + ["contain", "cover", "fill"][e.fit || 0] + "'>";
                }
                var roles = [AR ? "صورة" : "Image", AR ? "شعار الشركة" : "Company logo",
                             AR ? "توقيع" : "Signature", AR ? "ختم" : "Stamp",
                             AR ? "شعار الفرع" : "Branch logo"];
                return "<span class='text-muted fs-8'><i class='ki-outline ki-picture fs-5 me-1'></i>" + esc(roles[e.imageRole] || "") + "</span>";
            }
            case KIND.Line:
            case KIND.Rectangle:
                return "";
            case KIND.Table:
                return tableFace(e);
        }
        return "";
    }

    function tableFace(e) {
        if (!e.columns || !e.columns.length) {
            return "<span class='text-muted fs-8'>" + (AR ? "جدول بلا أعمدة — أضف أعمدة من الخصائص" : "Empty table — add columns in Properties") + "</span>";
        }
        // padding on the CELLS, not from table-sm's own spacing, so the two rows fit a print-row box.
        var cell = "padding:.2mm .6mm;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;";

        // WIDTHS AS PERCENTAGES, NOT MILLIMETRES.
        //
        // The canvas zoom is applied by the designer itself: it converts every element's mm to px and
        // multiplies by the zoom, which is why the host box carries an explicit px width. A `width:46mm`
        // on a <th> is a REAL millimetre, which the browser renders at 3.78px/mm whatever the zoom — so
        // the table stayed its full printed width inside a shrunken box and the last columns fell off
        // the edge. Measured at 71%: a 501px box holding a 703px table, and 703/501 is exactly 1/0.71.
        // A percentage of the box is zoom-independent and prints the same.
        var totalMm = e.columns.reduce(function (s, c) { return s + (c.widthMm > 0 ? c.widthMm : 0); }, 0);
        function pct(c) { return totalMm > 0 ? ((c.widthMm > 0 ? c.widthMm : 0) / totalMm * 100) : (100 / e.columns.length); }

        var head = e.columns.map(function (c) {
            var f = fieldOf(c.fieldKey);
            return "<th style='" + cell + "width:" + pct(c).toFixed(3) + "%;text-align:" +
                   ["start", "center", "end", "justify"][c.align || 0] + "'>" +
                   esc(c.headerText || (f ? f.title : c.fieldKey)) + "</th>";
        }).join("");
        var row = e.columns.map(function (c) {
            return "<td class='text-primary' style='" + cell + "'>{" + esc(c.fieldKey) + "}</td>";
        }).join("");
        // THE FACE HAS TO FIT THE BOX IT IS DRAWN IN.
        //
        // It drew a Bootstrap table-sm — two rows at roughly 24px each, about 25mm — inside whatever
        // height the element has. A table in the Detail band cannot be 25mm tall, because the renderer
        // uses the BAND height as the print row height (see ReportVisualRenderer: the header costs one
        // detail height and so does every row). So an honestly-sized table, 8-10mm, had its face
        // clipped: measured 47px cells inside a 22px band, which is why only the last few column
        // headers were legible and the rest looked missing.
        //
        // line-height 1, no cell padding and a clipping wrapper make the two rows fit an 8mm box. The
        // face is a preview of a print row, so it should be the size of a print row.
        return "<div style='overflow:hidden;height:100%'>" +
               "<table class='table table-sm m-0 fs-8' " +
               "style='table-layout:fixed;width:100%;line-height:1.05;border-collapse:collapse'>" +
               "<thead><tr class='cb-thead-rule'>" + head + "</tr></thead>" +
               "<tbody><tr>" + row + "</tr></tbody></table></div>";
    }

    function renderRulers(sc) {
        var cw = contentWidthMm(), ch = contentHeightMm();
        var pg = S.layout.page;
        var step = 10;

        var h = [];
        for (var x = 0; x <= cw; x += step) {
            h.push("<span class='cbd-tick cbd-tick-h' style='inset-inline-start:" + ((pg.marginLeftMm + x) * sc + 14) + "px'>" + x + "</span>");
        }
        $("cbd-ruler-h").innerHTML = h.join("");

        var v = [];
        for (var y = 0; y <= ch; y += step) {
            v.push("<span class='cbd-tick cbd-tick-v' style='top:" + ((pg.marginTopMm + y) * sc + 14) + "px'>" + y + "</span>");
        }
        $("cbd-ruler-v").innerHTML = v.join("");
    }

    // Zoom so the whole sheet fits the width available. Was inline in the Fit button's handler; it is a
    // function now because BOOT calls it too — see fitIfSheetOverflows.
    function fitToWidth() {
        var avail = $("cbd-scroll").clientWidth - 40;
        if (avail <= 0) { return; }
        var z = avail / (paper()[0] * (96 / 25.4));
        S.zoom = Math.max(0.4, Math.min(2, z));
        $("cbd-zoom").value = Math.round(S.zoom * 100);
        $("cbd-zoom-label").textContent = Math.round(S.zoom * 100) + "%";
        renderCanvas();
    }

    // OPENING THE DESIGNER ON A NARROW SCREEN SHOWED A CORNER OF THE PAGE. At 390px the scroll box is
    // ~326px wide and an A4 sheet at 100% is 794px, so the first thing a phone user saw was a slice of
    // one edge with no way to tell what they were looking at. Fitted ONCE, at boot, and only when the
    // sheet does not fit: a size test rather than a device test, so a narrow desktop window behaves the
    // same, and a re-fit is never forced on top of a zoom the user chose themselves.
    function fitIfSheetOverflows() {
        var box = $("cbd-scroll");
        if (!box) { return; }
        if (paper()[0] * scale() > box.clientWidth - 8) { fitToWidth(); }
    }

    function renderStatus() {
        var parts = [];
        parts.push((AR ? "الورق: " : "Paper: ") + paper()[0].toFixed(0) + " × " + paper()[1].toFixed(0) + " mm");
        parts.push((AR ? "منطقة الطباعة: " : "Printable: ") + contentWidthMm().toFixed(0) + " × " + contentHeightMm().toFixed(0) + " mm");

        if (S.selection.length === 1) {
            var p = elementById(S.selection[0]);
            if (p) {
                parts.push(BAND_LABEL[p.b.kind] + " · " +
                    "X " + p.e.xMm.toFixed(1) + " · Y " + p.e.yMm.toFixed(1) +
                    " · " + p.e.widthMm.toFixed(1) + "×" + p.e.heightMm.toFixed(1) + " mm");
            }
        } else if (S.selection.length > 1) {
            parts.push(S.selection.length + (AR ? " عناصر محددة" : " selected"));
        }
        $("cbd-status").textContent = parts.join("   |   ");
    }

    // =============================================================================================
    // DRAG, DROP, MOVE, RESIZE
    //
    // RTL: the pointer moves in SCREEN space and the model lives in LOGICAL space, so the horizontal
    // delta is negated under rtl. Get this wrong and an Arabic user's elements walk the wrong way —
    // which is precisely the sort of thing a "mirror the CSS afterwards" approach never notices.
    // =============================================================================================
    function rtlSign() { return S.layout.page.rtl ? -1 : 1; }

    function wireBands(sc) {
        Array.prototype.forEach.call($("cbd-content").querySelectorAll(".cbd-band"), function (bandEl) {
            var kind = parseInt(bandEl.getAttribute("data-band"), 10);

            bandEl.addEventListener("dragover", function (ev) {
                ev.preventDefault();
                ev.dataTransfer.dropEffect = "copy";
                bandEl.classList.add("drop");
            });
            bandEl.addEventListener("dragleave", function () { bandEl.classList.remove("drop"); });

            bandEl.addEventListener("drop", function (ev) {
                ev.preventDefault();
                bandEl.classList.remove("drop");

                var payload = ev.dataTransfer.getData("text/plain") || "";
                var rect = bandEl.getBoundingClientRect();

                // Logical X: distance from the band's START edge, which under rtl is its right edge.
                var offset = S.layout.page.rtl ? (rect.right - ev.clientX) : (ev.clientX - rect.left);
                var at = { kind: kind, xMm: Math.max(0, offset / sc), yMm: Math.max(0, (ev.clientY - rect.top) / sc) };

                if (payload.indexOf("tool:") === 0) {
                    addTool(payload.slice(5), at);
                } else if (payload.indexOf("field:") === 0) {
                    dropField(payload.slice(6), at);
                } else if (payload.indexOf("asset:") === 0) {
                    dropAsset(parseInt(payload.slice(6), 10), at);
                }
            });

            var grip = bandEl.querySelector("[data-bandgrip]");
            if (grip) {
                grip.addEventListener("mousedown", function (ev) {
                    ev.preventDefault();
                    ev.stopPropagation();   // not a band selection click, and not a drag of any element
                    beginBandResize(ev, kind, sc);
                });
            }

            // CAPTURE PHASE, so a Ctrl-drag that starts on top of an element is a marquee before that
            // element's own mousedown can claim it as a move. Without capture the element wins - it is
            // the deeper node - and Ctrl over a full canvas would just drag whatever was under it.
            bandEl.addEventListener("mousedown", function (ev) {
                // TWO WAYS IN, and the second one is not a convenience.
                //
                // Empty space starts a marquee. But a finished report has NO empty space - every pixel
                // of a designed band is covered by an element, card background or rule - and that is
                // exactly when rubber-banding is worth having. Requiring bare canvas made the gesture
                // unavailable on the only layouts that need it.
                //
                // So Ctrl (or Alt) starts one anywhere, over elements included. It cannot be confused
                // with a move: a plain drag on an element still moves that element, untouched.
                if (ev.target === bandEl || ev.ctrlKey || ev.altKey) {
                    ev.preventDefault();
                    ev.stopPropagation();
                    beginMarquee(ev, kind, bandEl);
                } else if (ev.target.classList.contains("cbd-band-tag")) {
                    S.selectedBand = kind;
                    if (!ev.shiftKey) { S.selection = []; }
                    renderCanvas(); renderProps(); renderBandList();
                }
            }, true);
        });
    }

    // =============================================================================================
    // DRAG TO SELECT.
    //
    // The selection has always been an ARRAY - shift-click could build one, and every move, nudge and
    // delete already act on all of it. What was missing was the gesture people actually reach for, so
    // multi-select looked absent rather than awkward.
    //
    // THE BOX IS DRAWN IN PIXELS AND RESOLVED IN MILLIMETRES. Everything persisted is mm, so the rect
    // is converted once, at the end, and compared against element geometry in the model rather than
    // against the DOM. An element hidden behind another is still inside the box, and still selected -
    // which is the behaviour a reader of the band list expects.
    //
    // INTERSECTION, NOT CONTAINMENT. Requiring a box to swallow an element whole means a careful drag
    // is needed to catch a wide table; touching it is what an author means by "and that one".
    // =============================================================================================
    function beginMarquee(ev, kind, bandEl) {
        var sc = scale();
        var rect = bandEl.getBoundingClientRect();
        var startX = ev.clientX, startY = ev.clientY;
        var additive = ev.shiftKey;
        var before = additive ? S.selection.slice() : [];

        var box = document.createElement("div");
        box.className = "cbd-marquee";
        bandEl.appendChild(box);

        var moved = false;

        function draw(mx, my) {
            var x = Math.min(startX, mx), y = Math.min(startY, my);
            var w = Math.abs(mx - startX), h = Math.abs(my - startY);
            box.style.left = (x - rect.left) + "px";
            box.style.top = (y - rect.top) + "px";
            box.style.width = w + "px";
            box.style.height = h + "px";
            return { x: x, y: y, w: w, h: h };
        }

        function hits(px) {
            // pixels -> millimetres, against the band's own origin. RTL is handled the way the rest of
            // the canvas handles it: x is a distance from the CONTENT START edge, so the box is mirrored
            // here rather than every element being stored twice.
            var x1 = (px.x - rect.left) / sc, x2 = (px.x + px.w - rect.left) / sc;
            if (S.layout.page.rtl) {
                var wMm = rect.width / sc;
                var t = wMm - x2; x2 = wMm - x1; x1 = t;
            }
            var y1 = (px.y - rect.top) / sc, y2 = (px.y + px.h - rect.top) / sc;

            // NOT named 'band': a var of that name hoists over the band() function above and the
            // call becomes 'band is not a function' at the first drag.
            var bnd = band(kind);
            if (!bnd) { return []; }
            return bnd.elements.filter(function (e) {
                return e.xMm < x2 && (e.xMm + e.widthMm) > x1
                    && e.yMm < y2 && (e.yMm + e.heightMm) > y1;
            }).map(function (e) { return e.id; });
        }

        function onMove(mv) {
            if (!moved && Math.abs(mv.clientX - startX) < 3 && Math.abs(mv.clientY - startY) < 3) { return; }
            moved = true;
            var px = draw(mv.clientX, mv.clientY);
            var ids = hits(px);
            S.selectedBand = kind;
            S.selection = additive ? before.concat(ids.filter(function (id) { return before.indexOf(id) < 0; })) : ids;
            paintSelection();
        }

        function onUp(mv) {
            document.removeEventListener("mousemove", onMove);
            document.removeEventListener("mouseup", onUp);
            if (box.parentNode) { box.parentNode.removeChild(box); }

            // A CLICK IS NOT A DRAG. Without this an ordinary click on empty canvas would clear the
            // selection through the marquee path and re-render twice.
            if (!moved) {
                S.selectedBand = kind;
                if (!additive) { S.selection = []; }
            }
            renderCanvas(); renderProps(); renderBandList();
        }

        document.addEventListener("mousemove", onMove);
        document.addEventListener("mouseup", onUp);
    }

    // Repaints the selection outline WITHOUT rebuilding the canvas: a full renderCanvas on every
    // mousemove would drop the marquee element it is drawing and stutter on a large report.
    function paintSelection() {
        Array.prototype.forEach.call($("cbd-content").querySelectorAll("[data-el]"), function (node) {
            var on = S.selection.indexOf(node.getAttribute("data-el")) >= 0;
            node.classList.toggle("sel", on);
        });
        renderStatus();
    }

    function dropField(key, at) {
        var f = fieldOf(key);
        var e = el(KIND.Field, { fieldKey: key, widthMm: 35, heightMm: 6 });
        if (f && (f.type === 2 || f.type === 3)) { e.style.align = 2; e.style.numberFormat = "N2"; }
        if (f && f.type === 4) { e.style.dateFormat = "yyyy-MM-dd"; }
        var b = band(at ? at.kind : S.selectedBand) || band(BAND.Detail);
        if (at) { e.xMm = snap(at.xMm); e.yMm = snap(at.yMm); }
        e.z = nextZ(b);
        snapshot();
        b.elements.push(e);
        S.selectedBand = b.kind;
        S.selection = [e.id];
        renderAll();
    }

    function dropAsset(assetId, at) {
        var a = null;
        S.assets.forEach(function (x) { if (x.id === assetId) { a = x; } });
        var e = el(KIND.Image, { assetId: assetId, imageRole: a ? a.role : 0, widthMm: 35, heightMm: 20 });
        var b = band(at ? at.kind : S.selectedBand) || band(BAND.Detail);
        if (at) { e.xMm = snap(at.xMm); e.yMm = snap(at.yMm); }
        e.z = nextZ(b);
        snapshot();
        b.elements.push(e);
        S.selectedBand = b.kind;
        S.selection = [e.id];
        renderAll();
    }

    // The last press on an element, for double-click detection. MEASURED HERE rather than through the
    // browser's own dblclick event, and not by preference: the mousedown handler below re-renders the
    // canvas, so the node that took the first click is replaced before the second one lands and the
    // browser never raises dblclick at all — verified, a delegated dblclick listener on the content box
    // never fired once. Two presses on the same element inside DOUBLE_MS, with no drag in between, is
    // the gesture. A drag clears it (see beginMove), so click-then-drag cannot be mistaken for a
    // double-click and pop a file dialog mid-move; a press on a resize handle is excluded outright.
    var lastPress = { id: null, at: 0 };
    var DOUBLE_MS = 400;

    function wireElements(sc) {
        Array.prototype.forEach.call($("cbd-content").querySelectorAll("[data-el]"), function (node) {
            var id = node.getAttribute("data-el");

            node.addEventListener("mousedown", function (ev) {
                ev.stopPropagation();
                var handle = ev.target.getAttribute && ev.target.getAttribute("data-h");

                var now = (new Date()).getTime();
                var isDouble = !handle && lastPress.id === id && (now - lastPress.at) < DOUBLE_MS;
                lastPress = { id: id, at: now };

                if (ev.shiftKey) {
                    var at = S.selection.indexOf(id);
                    if (at >= 0) { S.selection.splice(at, 1); } else { S.selection.push(id); }
                } else if (S.selection.indexOf(id) < 0) {
                    S.selection = [id];
                }

                var pair = elementById(id);
                if (pair) { S.selectedBand = pair.b.kind; }
                renderCanvas(); renderProps(); renderBandList();

                // DOUBLE-CLICK AN IMAGE = "give this element a picture". The obvious gesture on an empty
                // image box, and the one that does not require finding a control in another rail first.
                if (isDouble && pair && pair.e.kind === KIND.Image) {
                    lastPress = { id: null, at: 0 };   // a third press must not re-open the dialog
                    requestImageFor(pair.e);
                    return;                            // and no move starts under the open dialog
                }

                if (handle) { beginResize(ev, id, handle, sc); }
                else { beginMove(ev, sc); }
            });
        });
    }

    // RESIZING A BAND BY DRAGGING ITS BOTTOM EDGE.
    //
    // The height was only ever a number box in the الأقسام / Bands tab. It still is — this writes to the
    // same heightMm, and renderBandList at the end makes the box show what was dragged — but "make the
    // header taller" is a direct-manipulation gesture and asking someone to leave the canvas, find the
    // right tab and type millimetres is not the same thing.
    //
    // Elements are NOT moved or clipped by this. Their Y is measured from the band's top, so shrinking a
    // band leaves anything taller than it overhanging — the same as it has always been for an element
    // that does not fit, and the same as the print renderer does.
    function beginBandResize(ev, kind, sc) {
        var b = band(kind);
        if (!b) { return; }

        var startY = ev.clientY;
        var origin = b.heightMm || 0;
        var taken = false;

        S.selectedBand = kind;
        // The pointer leaves the grip the moment the band is redrawn under it, so the cursor is pinned
        // for the duration rather than left to the hit test.
        var priorCursor = document.body.style.cursor;
        document.body.style.cursor = "row-resize";

        function paintGrip() {
            var g = $("cbd-content").querySelector("[data-bandgrip='" + kind + "']");
            if (g) { g.classList.add("on"); }
        }
        paintGrip();

        function onMove(e2) {
            var dyMm = (e2.clientY - startY) / sc;
            if (!taken && Math.abs(dyMm) < 0.3) { return; }
            if (!taken) { snapshot(); taken = true; }

            // A FLOOR OF 2mm, not 0, and only on the DRAG. A band dragged to 0 renders no pixels, so its
            // grip is not drawn either — the gesture would delete its own handle and there would be no
            // way to drag the band back open. 0 is still reachable by typing it in the الأقسام / Bands
            // tab, which is a deliberate act and reversible from the same box.
            b.heightMm = Math.max(2, snap(origin + dyMm));
            renderCanvas();     // the band's tag carries the live height, so the number is visible as it moves
            paintGrip();
        }
        function onUp() {
            document.removeEventListener("mousemove", onMove);
            document.removeEventListener("mouseup", onUp);
            document.body.style.cursor = priorCursor;
            renderBandList();   // the Bands tab's number box must agree with what was just dragged
            renderProps();
            renderStatus();
        }
        document.addEventListener("mousemove", onMove);
        document.addEventListener("mouseup", onUp);
    }

    function beginMove(ev, sc) {
        var startX = ev.clientX, startY = ev.clientY;

        // ORIGINS ARE KEPT IN CONTENT SPACE (absolute Y from the top of the printable box), not as the
        // band-local Y the model stores. The element may change band half-way through the drag, and a
        // local origin would then be measured against a different band's top — the element would jump.
        var origin = {};
        S.selection.forEach(function (id) {
            var p = elementById(id);
            if (p) { origin[id] = { x: p.e.xMm, absY: bandTopMm(p.b.kind) + p.e.yMm }; }
        });
        var moved = false;

        function onMove(e2) {
            var dxMm = ((e2.clientX - startX) * rtlSign()) / sc;
            var dyMm = (e2.clientY - startY) / sc;
            if (!moved && Math.abs(dxMm) < 0.3 && Math.abs(dyMm) < 0.3) { return; }
            if (!moved) { snapshot(); moved = true; lastPress = { id: null, at: 0 }; }

            S.selection.forEach(function (id) {
                var p = elementById(id);
                if (!p || !origin[id]) { return; }

                var absY = Math.max(0, snap(origin[id].absY + dyMm));

                // DRAGGING ACROSS A BAND BOUNDARY MOVES THE ELEMENT INTO THAT BAND. Dragging a picture
                // up into the page header is the obvious gesture for putting it there, and before this
                // it did nothing at all: the band never changed and the local Y stopped at 0.
                var target = bandAtMm(absY);
                if (target && target.kind !== p.b.kind) {
                    moveToBand(p, target.kind);
                    p = elementById(id);            // re-homed: the old pair is stale
                    if (!p) { return; }
                }

                // NOT clamped into the band's height, deliberately: the pointer decides where the
                // element goes, and an element taller than its band has always been allowed to overhang.
                p.e.xMm = Math.max(0, snap(origin[id].x + dxMm));
                p.e.yMm = Math.max(0, snap(absY - bandTopMm(p.b.kind)));
            });
            renderCanvas();
            drawGuides(sc);
        }
        function onUp() {
            document.removeEventListener("mousemove", onMove);
            document.removeEventListener("mouseup", onUp);
            clearGuides();
            // renderBandList as well: a drag can now change which band an element belongs to, and the
            // bands panel is where that is shown.
            renderProps();
            renderBandList();
        }
        document.addEventListener("mousemove", onMove);
        document.addEventListener("mouseup", onUp);
    }

    function beginResize(ev, id, handle, sc) {
        ev.stopPropagation();
        var p = elementById(id);
        if (!p) { return; }
        var startX = ev.clientX, startY = ev.clientY;
        var o = { x: p.e.xMm, y: p.e.yMm, w: p.e.widthMm, h: p.e.heightMm };
        var taken = false;

        function onMove(e2) {
            var dx = ((e2.clientX - startX) * rtlSign()) / sc;
            var dy = (e2.clientY - startY) / sc;
            if (!taken) { snapshot(); taken = true; }

            // WHICH AXES THIS HANDLE OWNS. An edge handle names one letter, so it moves one dimension
            // and the other is left untouched - not recomputed and rounded back to the same value,
            // because snap() would drift it a fraction of a millimetre on every mousemove.
            var west  = handle === "nw" || handle === "sw" || handle === "w";
            var north = handle === "nw" || handle === "ne" || handle === "n";
            var horiz = handle.indexOf("w") >= 0 || handle.indexOf("e") >= 0;
            var vert  = handle.indexOf("n") >= 0 || handle.indexOf("s") >= 0;

            if (horiz) {
                p.e.widthMm = Math.max(1, snap(west ? o.w - dx : o.w + dx));
                if (west) { p.e.xMm = Math.max(0, snap(o.x + (o.w - p.e.widthMm))); }
            }
            if (vert) {
                p.e.heightMm = Math.max(1, snap(north ? o.h - dy : o.h + dy));
                if (north) { p.e.yMm = Math.max(0, snap(o.y + (o.h - p.e.heightMm))); }
            }

            renderCanvas();
            drawGuides(sc);
        }
        function onUp() {
            document.removeEventListener("mousemove", onMove);
            document.removeEventListener("mouseup", onUp);
            clearGuides();
            renderProps();
        }
        document.addEventListener("mousemove", onMove);
        document.addEventListener("mouseup", onUp);
    }

    // Alignment guides: a red line whenever a dragged edge or centre lines up with another element's.
    // Purely visual — nothing here changes the model, which is why it can be as approximate as it likes.
    function clearGuides() {
        Array.prototype.forEach.call(document.querySelectorAll(".cbd-guide"), function (g) { g.remove(); });
    }
    function drawGuides(sc) {
        clearGuides();
        if (S.selection.length !== 1) { return; }
        var me = elementById(S.selection[0]);
        if (!me) { return; }

        var host = $("cbd-content");
        var tol = 0.6;
        var mine = [me.e.xMm, me.e.xMm + me.e.widthMm / 2, me.e.xMm + me.e.widthMm];

        allElements().forEach(function (p) {
            if (p.e.id === me.e.id) { return; }
            var theirs = [p.e.xMm, p.e.xMm + p.e.widthMm / 2, p.e.xMm + p.e.widthMm];
            theirs.forEach(function (t) {
                mine.forEach(function (m) {
                    if (Math.abs(m - t) <= tol) {
                        var g = document.createElement("div");
                        g.className = "cbd-guide";
                        g.style.insetInlineStart = (t * sc) + "px";
                        g.style.top = "0";
                        g.style.width = "1px";
                        g.style.height = "100%";
                        host.appendChild(g);
                    }
                });
            });
        });
    }

    // =============================================================================================
    // SELECTION COMMANDS
    // =============================================================================================
    function deleteSelection() {
        if (!S.selection.length) { return; }
        snapshot();
        S.layout.bands.forEach(function (b) {
            b.elements = b.elements.filter(function (e) { return S.selection.indexOf(e.id) < 0; });
        });
        S.selection = [];
        renderAll();
    }

    function duplicateSelection() {
        if (!S.selection.length) { return; }
        snapshot();
        var fresh = [];
        S.selection.forEach(function (id) {
            var p = elementById(id);
            if (!p) { return; }
            var copy = clone(p.e);
            copy.id = uid();
            copy.xMm = snap(copy.xMm + (S.layout.gridMm || 2));
            copy.yMm = snap(copy.yMm + (S.layout.gridMm || 2));
            copy.z = nextZ(p.b);
            p.b.elements.push(copy);
            fresh.push(copy.id);
        });
        S.selection = fresh;
        renderAll();
    }

    function copySelection() {
        S.clipboard = S.selection.map(function (id) {
            var p = elementById(id);
            return p ? { kind: p.b.kind, e: clone(p.e) } : null;
        }).filter(Boolean);
    }

    function pasteClipboard() {
        if (!S.clipboard || !S.clipboard.length) { return; }
        snapshot();
        var fresh = [];
        S.clipboard.forEach(function (item) {
            var b = band(S.selectedBand) || band(item.kind) || band(BAND.Detail);
            var copy = clone(item.e);
            copy.id = uid();
            copy.xMm = snap(copy.xMm + (S.layout.gridMm || 2));
            copy.yMm = snap(copy.yMm + (S.layout.gridMm || 2));
            copy.z = nextZ(b);
            b.elements.push(copy);
            fresh.push(copy.id);
        });
        S.selection = fresh;
        renderAll();
    }

    function zOrder(toFront) {
        if (!S.selection.length) { return; }
        snapshot();
        S.selection.forEach(function (id) {
            var p = elementById(id);
            if (!p) { return; }
            if (toFront) { p.e.z = nextZ(p.b); }
            else {
                var min = 0;
                p.b.elements.forEach(function (x) { if (x.z < min) { min = x.z; } });
                p.e.z = min - 1;
            }
        });
        renderAll();
    }

    function nudge(dx, dy) {
        if (!S.selection.length) { return; }
        snapshot();
        S.selection.forEach(function (id) {
            var p = elementById(id);
            if (!p) { return; }
            p.e.xMm = Math.max(0, Math.round((p.e.xMm + dx) * 10) / 10);
            p.e.yMm = Math.max(0, Math.round((p.e.yMm + dy) * 10) / 10);
        });
        renderCanvas(); renderProps();
    }

    // =============================================================================================
    // PROPERTIES PANEL — one form per element kind.
    // =============================================================================================
    function renderProps() {
        var host = $("cbd-props"), empty = $("cbd-props-empty");

        if (S.selection.length !== 1) {
            host.innerHTML = "";
            empty.style.display = "";
            empty.textContent = S.selection.length > 1
                ? (AR ? S.selection.length + " عناصر محددة — استخدم الأسهم أو الحذف." : S.selection.length + " elements selected — use the arrow keys or Delete.")
                : (AR ? "اختر عنصرًا لعرض خصائصه." : "Select an element to edit it.");
            renderStatus();
            return;
        }

        empty.style.display = "none";
        var p = elementById(S.selection[0]);
        if (!p) { host.innerHTML = ""; return; }
        var e = p.e, s = e.style;

        var h = [];
        // THE BAND IS A CHOICE, not a label. It used to be printed as plain text, which made it the one
        // property of an element that could not be changed after it was created — and dragging could not
        // change it either, so an element in the wrong band had to be deleted and re-added. Each option
        // carries the band's height because a 0mm band is a real and confusing destination: the element
        // goes there and has no room to be seen in.
        h.push(row(AR ? "القسم" : "Band", select("p-band", S.layout.bands.map(function (bb) {
            return { v: bb.kind, t: BAND_LABEL[bb.kind] + " · " + (bb.heightMm || 0) + "mm" };
        }), p.b.kind)));

        // ---- geometry, always ----
        h.push("<div class='separator my-3'></div>");
        h.push("<div class='row g-2'>");
        h.push(numCol("X (mm)", "px", e.xMm));
        h.push(numCol("Y (mm)", "py", e.yMm));
        h.push(numCol(AR ? "العرض" : "Width", "pw", e.widthMm));
        h.push(numCol(AR ? "الارتفاع" : "Height", "ph", e.heightMm));
        h.push("</div>");

        // ---- per-kind ----
        h.push("<div class='separator my-3'></div>");

        if (e.kind === KIND.Text) {
            h.push(row(AR ? "النص (عربي)" : "Text (Arabic)",
                "<textarea id='p-text' rows='2' class='form-control form-control-sm'>" + esc(e.text || "") + "</textarea>"));

             // THE ENGLISH TWIN, captured beside the Arabic rather than translated afterwards. Left
               // empty the document prints the Arabic in both languages, which is what it did before
               // this existed — so an old template loses nothing by not having one.
            h.push(row(AR ? "النص (إنجليزي)" : "Text (English)",
                "<textarea id='p-text-en' rows='2' class='form-control form-control-sm' " +
                "placeholder='" + esc(AR ? "يُطبع عند اختيار الإنجليزية" : "printed when the report is in English") + "'>" +
                esc(e.textEn || "") + "</textarea>"));
        }

        if (e.kind === KIND.QrCode) {
             // WHAT THE CODE CARRIES. A bound field wins over the text — the renderer says so — so the
               // picker offers an explicit "no field" entry rather than leaving the author to guess why
               // their typed payload is being ignored.
            h.push(row(AR ? "الحقل" : "Field", select("p-field",
                [{ v: "", t: AR ? "— نص ثابت —" : "— fixed text —" }].concat(S.fields.map(function (f) {
                    return { v: f.key, t: f.title };
                })), e.fieldKey || "")));
            h.push(row(AR ? "النص الثابت" : "Fixed text",
                "<textarea id='p-text' rows='2' class='form-control form-control-sm' " +
                "placeholder='" + esc(AR ? "يُستخدم عندما لا يوجد حقل" : "used when no field is bound") + "'>" +
                esc(e.text || "") + "</textarea>"));

             // THE TRADE THE AUTHOR OWNS. Higher correction survives a stamp on the corner and spends
               // modules doing it; lower fits a longer payload and fails the first fold.
            h.push(row(AR ? "تصحيح الخطأ" : "Error correction", select("p-qrecc", [
                { v: 0, t: "L — 7%" }, { v: 1, t: "M — 15%" },
                { v: 2, t: "Q — 25%" }, { v: 3, t: "H — 30%" }
            ], e.qrEcc == null ? 2 : e.qrEcc)));
            h.push(row(AR ? "دقة الطباعة (بكسل/وحدة)" : "Print resolution (px/module)",
                "<input id='p-qrpx' type='number' min='2' max='20' step='1' class='form-control form-control-sm' value='" +
                (e.qrModulePixels == null ? 8 : e.qrModulePixels) + "'>"));
        }

        if (e.kind === KIND.SubReport) {
            var inGroup = SUB_GROUP_BANDS.indexOf(p.b.kind) >= 0;
            var inReport = SUB_REPORT_BANDS.indexOf(p.b.kind) >= 0;

            if (!inGroup && !inReport) {
                h.push("<div class='alert alert-warning py-2 px-3 fs-8 mb-3'>" +
                    esc(AR ? "التقرير الفرعي يرتبط بمجموعة، أو يُدرَج كاملًا في حِزمة تقرير — لن يُقبل هنا."
                           : "A sub-report is linked to a group, or embedded whole in a report band.") +
                    "</div>");
            }

            h.push(row(AR ? "التقرير" : "Report", select("p-subcode",
                [{ v: "", t: AR ? "— اختر —" : "— choose —" }].concat((SUBCAT || []).map(function (r) {
                    return { v: r.code, t: (AR ? r.titleAr : r.titleEn) || r.code };
                })), e.subReportCode || "")));

            if (inGroup) {
                 // THE LINK IS A CHILD COLUMN, so it is typed rather than picked: the designer holds this
                   // report's fields, not the child's, and offering the parent's list here would invite a
                   // key that cannot match anything.
                h.push(row(AR ? "حقل الربط في التقرير الفرعي" : "Link field (child)",
                    "<input id='p-sublink' type='text' class='form-control form-control-sm' value='" +
                    esc(e.linkChildFieldKey || "") + "' placeholder='" +
                    esc(AR ? "يطابَق بقيمة المجموعة" : "matched against the group value") + "'>"));
                h.push("<div class='text-muted fs-8 mb-3'>" +
                    esc(AR ? "يُطابَق هذا العمود بقيمة الحقل الذي تجمّع عليه هذه الحِزمة."
                           : "This child column is matched against the field this band groups on.") + "</div>");
            }

            h.push("<div class='text-muted fs-8'>" +
                esc(AR ? "يعرض التقرير الفرعي أعمدة التقرير الأصلية — لا يمكن إظهار عمود يخفيه هو."
                       : "A sub-report prints the child report's own columns; it cannot reveal one the child hides.") +
                "</div>");

            if (!SUBCAT) loadSubCatalog(function () { renderProps(); });
        }

        if (e.kind === KIND.Summary) {
            // OFFERED ONLY WHERE IT WORKS. The server refuses a comparison outside a report header or
            // footer - a group's scope is "this customer's rows" and the previous period's equivalent may
            // not exist at all - so showing the control in a group band would be offering a choice whose
            // every value is rejected on save.
            var reportBand = (p.b.kind === 0 || p.b.kind === 6);
            if (reportBand) {
                h.push(row(AR ? "المقارنة" : "Compare with", select("p-compare", [
                    { v: 0, t: AR ? "— بلا —" : "— none —" },
                    { v: 1, t: AR ? "الفترة السابقة" : "The previous period" }
                ], e.compare || 0)));

                if (e.compare) {
                    // THE COLOUR IS A CLAIM AND THE AUTHOR MAKES IT. Revenue up is good; overdue
                    // receivables up is not. Without this the report would tell a reader that a rise in
                    // their ageing is an improvement, in the most confident way it has of saying anything.
                    h.push(row(AR ? "الأفضل هو" : "Better is", select("p-cmpdir", [
                        { v: "1", t: AR ? "الأعلى" : "Higher" },
                        { v: "0", t: AR ? "الأقل" : "Lower" }
                    ], e.compareHigherIsBetter === false ? "0" : "1")));
                    h.push("<div class='text-muted fs-8 mb-3'>" +
                        esc(AR ? "الفترة السابقة هي نفس عدد الأيام قبل بداية المدى مباشرةً."
                               : "The previous period is the same number of days immediately before the range starts.") +
                        "</div>");
                }
            } else if (e.compare) {
                h.push("<div class='alert alert-warning py-2 px-3 fs-8 mb-3'>" +
                    esc(AR ? "المقارنة تعمل في رأس أو تذييل التقرير فقط — نطاق المجموعة قد لا يكون له مقابل في الفترة السابقة."
                           : "A comparison works only in a report header or footer - a group's scope may have no equivalent in the other period.") +
                    "</div>");
            }
        }

        if (e.kind === KIND.Icon) {
            // A GRID, NOT A DROPDOWN. Sixteen marks are chosen by recognising one, and a <select> of
            // sixteen words makes the author translate each name back into a picture before they can
            // pick. The name is still there as the tooltip, for the one that is ambiguous.
            var cells = ICONS.map(function (ic) {
                return "<button type='button' class='cbd-icon-cell" + (e.icon === ic.v ? " on" : "") +
                       "' data-icon='" + ic.v + "' title='" + esc(ic.t) + "' aria-label='" + esc(ic.t) + "'>" +
                       iconSvg(ic.v, 20) + "</button>";
            }).join("");
            h.push(row(AR ? "الأيقونة" : "Icon", "<div class='cbd-icon-grid'>" + cells + "</div>"));
            h.push("<div class='text-muted fs-8 mb-3'>" +
                esc(AR ? "الحجم من مربّع العنصر، واللون من لون النص أسفل."
                       : "The size comes from the element's own box and the colour from its text colour below.") +
                "</div>");
        }

        if (e.kind === KIND.Chart || e.kind === KIND.CrossTab) {
            var isCt = e.kind === KIND.CrossTab;
            var fieldOpts = S.fields.map(function (f) { return { v: f.key, t: f.title }; });

             // THE BAND WARNING IS SHOWN, NOT ENFORCED HERE. The server refuses the placement either way;
               // saying so at the point of confusion beats a rejected save the author has to decode.
            if (SCOPED_BANDS.indexOf(p.b.kind) < 0) {
                h.push("<div class='alert alert-warning py-2 px-3 fs-8 mb-3'>" +
                    esc(AR ? "هذا العنصر يلخّص نطاقًا كاملًا من الصفوف، ومكانه حِزمة تقرير أو مجموعة — لن يُقبل هنا."
                           : "This element summarises a whole scope of rows and belongs in a report or group band.") +
                    "</div>");
            }

            if (!isCt) {
                h.push(row(AR ? "نوع الرسم" : "Chart type", select("p-chartkind", [
                    { v: CHART.Column, t: AR ? "أعمدة" : "Column" },
                    { v: CHART.Bar, t: AR ? "أشرطة" : "Bar" },
                    { v: CHART.Line, t: AR ? "خط" : "Line" },
                    { v: CHART.Pie, t: AR ? "دائرة" : "Pie" }
                ], e.chartKind == null ? CHART.Column : e.chartKind)));
            }

            h.push(row(isCt ? (AR ? "حقل الصفوف" : "Row field") : (AR ? "المحور" : "Category axis"),
                select("p-cat", fieldOpts, e.categoryFieldKey || "")));

            // THE SERIES FIELD.
            //
            // Required on a cross-tab - without a column axis it is a Summary with extra steps - and
            // OPTIONAL on a chart, where it splits each category: columns into a group, a line into
            // several lines. The chart list therefore carries an explicit "none", because "I do not want
            // a breakdown" has to be a choice the author can make and come back from, not the absence of
            // one that the first field in the list silently fills.
            //
            // A PIE IS OFFERED NOTHING. Its slices already ARE the categories and their whole claim is
            // that they sum to the circle, so the server refuses a series field on one. A control whose
            // every value is rejected on save is worse than no control.
            var isPie = !isCt && (e.chartKind === CHART.Pie);
            if (isCt) {
                h.push(row(AR ? "حقل الأعمدة" : "Column field",
                    select("p-series", fieldOpts, e.seriesFieldKey || "")));
            } else if (!isPie) {
                h.push(row(AR ? "تقسيم حسب (سلسلة)" : "Break down by (series)",
                    select("p-series", [{ v: "", t: AR ? "— بلا —" : "— none —" }].concat(fieldOpts),
                           e.seriesFieldKey || "")));
            } else if (e.seriesFieldKey) {
                // Carried over from another chart kind. Say so rather than drop it silently: switching
                // back to a column restores it, and saving while it is a pie is refused for this reason.
                h.push("<div class='alert alert-warning py-2 px-3 fs-8 mb-3'>" +
                    esc(AR ? "الدائرة لا تقبل تقسيمًا بسلسلة — شرائحها هي الفئات نفسها. غيّر النوع إلى أعمدة أو أشرطة أو خط لاستخدامه."
                           : "A pie takes no series field - its slices already are the categories. Switch to column, bar or line to use it.") +
                    "</div>");
            }

            // THE TIME BUCKET, offered only when an axis actually is a date.
            //
            // The renderer groups by the FORMATTED category, so this is not a display choice that merely
            // looks different - it decides HOW MANY buckets there are. "yyyy-MM" is grouping by month.
            // Without a control the capability existed and no author could reach it.
            var catField = fieldOf(e.categoryFieldKey);
            var serField = fieldOf(e.seriesFieldKey);
            if ((catField && catField.type === 4) || (serField && serField.type === 4)) {
                var buckets = [
                    { v: "yyyy-MM-dd", t: AR ? "يومي" : "By day" },
                    { v: "yyyy-MM", t: AR ? "شهري" : "By month" },
                    { v: "yyyy", t: AR ? "سنوي" : "By year" }
                ];
                var cur = e.style.dateFormat || "yyyy-MM-dd";
                // An author who typed their own format keeps it, listed as itself: the control shows the
                // truth rather than snapping their choice to the nearest preset.
                if (cur !== "yyyy-MM-dd" && cur !== "yyyy-MM" && cur !== "yyyy") {
                    buckets.push({ v: cur, t: cur });
                }
                h.push(row(AR ? "تجميع التواريخ" : "Group dates by", select("p-datebucket", buckets, cur)));
                h.push("<div class='text-muted fs-8 mb-3'>" +
                    esc(AR ? "يحدّد عدد الفئات، لا شكل التسمية فقط — وهو تنسيق واحد للمحور والسلسلة معًا."
                           : "Sets how many buckets there are, not just how the label looks - and one format serves both the axis and the series.") +
                    "</div>");
            }

            h.push(row(AR ? "القيمة" : "Measure", select("p-field", fieldOpts, e.fieldKey || "")));
            h.push(row(AR ? "الدالة" : "Function", select("p-agg", [
                { v: AGG.Sum, t: "SUM" }, { v: AGG.Average, t: "AVG" },
                { v: AGG.Min, t: "MIN" }, { v: AGG.Max, t: "MAX" }, { v: AGG.Count, t: "COUNT" }
            ], e.aggregate)));

             // THE CEILING IS THE AUTHOR'S, because only they know their data's shape. Past it the tail
               // folds into one labelled bucket — it is never dropped, so totals still reconcile.
            h.push(row(isCt ? (AR ? "أقصى عدد أعمدة" : "Max columns") : (AR ? "أقصى عدد فئات" : "Max categories"),
                "<input id='p-maxcat' type='number' min='2' max='40' step='1' class='form-control form-control-sm' value='" +
                (e.maxCategories == null ? 12 : e.maxCategories) + "'>"));

            h.push(row(AR ? "إظهار القيم" : "Show values",
                "<input id='p-showvals' type='checkbox' class='form-check-input' " + (e.showValues === false ? "" : "checked") + ">"));

            if (isCt) {
                h.push(row(AR ? "صف وعمود الإجمالي" : "Grand totals",
                    "<input id='p-showtotals' type='checkbox' class='form-check-input' " + (e.showGrandTotals === false ? "" : "checked") + ">"));
            }
        }

        if (e.kind === KIND.Field || e.kind === KIND.Summary) {
            h.push(row(AR ? "الحقل" : "Field", select("p-field", S.fields.map(function (f) {
                return { v: f.key, t: f.title };
            }), e.fieldKey)));
        }

        if (e.kind === KIND.Summary) {
            h.push(row(AR ? "الدالة" : "Function", select("p-agg", [
                { v: AGG.Sum, t: "SUM" }, { v: AGG.Average, t: "AVG" },
                { v: AGG.Min, t: "MIN" }, { v: AGG.Max, t: "MAX" }, { v: AGG.Count, t: "COUNT" }
            ], e.aggregate)));
        }

        if (e.kind === KIND.SystemField) {
            h.push(row(AR ? "النوع" : "Kind", select("p-sys", [
                { v: SYS.CurrentDate, t: AR ? "التاريخ" : "Date" },
                { v: SYS.CurrentDateTime, t: AR ? "التاريخ والوقت" : "Date and time" },
                { v: SYS.PageNumber, t: AR ? "رقم الصفحة" : "Page number" },
                { v: SYS.TotalPages, t: AR ? "عدد الصفحات" : "Total pages" },
                { v: SYS.PageXOfY, t: AR ? "صفحة س من ص" : "Page X of Y" },
                { v: SYS.ReportName, t: AR ? "اسم التقرير" : "Report name" }
            ], e.systemField)));
        }

        if (e.kind === KIND.Image) {
             // THE ROLE IS NOT DECORATION. "شعار الشركة" and "شعار الفرع" are RESOLVED at print time from
               // the tenant's own records, so a box with one of those roles and no uploaded picture is not
               // empty — it is the company's mark, and it stays correct when the company changes its logo or
               // the document is printed by another branch. Every other role is just a label on a picture.
            h.push(row(AR ? "الدور" : "Role", select("p-role", [
                { v: 1, t: AR ? "شعار الشركة" : "Company logo" }, { v: 4, t: AR ? "شعار الفرع" : "Branch logo" },
                { v: 2, t: AR ? "توقيع" : "Signature" },
                { v: 3, t: AR ? "ختم" : "Stamp" }, { v: 0, t: AR ? "أخرى" : "Other" }
            ], e.imageRole)));
            // THE UPLOAD BUTTON BELONGS HERE TOO, beside the picker that needs it.
            //
            // The only other one is at the bottom of the toolbox rail, ~235px below the fold behind
            // twenty-two tools. Someone who places a signature and comes here to give it a picture finds a
            // list with nothing in it and no way to add anything — which is indistinguishable from "importing
            // an image does not work". Same hidden file input, same endpoint; the element's own role is
            // pushed into the role selector first, so an image uploaded from a signature is filed as one.
            h.push(row(AR ? "الصورة" : "Picture",
                "<div class='d-flex gap-1 align-items-center'>" +
                "<div class='flex-grow-1 min-w-0'>" +
                select("p-asset",
                    [{ v: "", t: AR ? "— بلا —" : "— none —" }].concat(S.assets.map(function (a) {
                        return { v: a.id, t: a.title || a.fileName };
                    })), e.assetId == null ? "" : e.assetId) +
                "</div>" +
                "<button type='button' id='p-asset-upload' class='btn btn-sm btn-light-primary px-2 flex-shrink-0' " +
                "title='" + esc(AR ? "رفع صورة" : "Upload an image") + "'>" +
                "<i class='ki-outline ki-cloud-add fs-5'></i></button>" +
                "</div>"));
            h.push(row(AR ? "الملاءمة" : "Fit", select("p-fit", [
                { v: 0, t: AR ? "احتواء" : "Contain" }, { v: 1, t: AR ? "تغطية" : "Cover" }, { v: 2, t: AR ? "تمديد" : "Stretch" }
            ], e.fit)));
        }

        if (e.kind === KIND.Table) {
            h.push(tableEditor(e));
        }

        // ---- text style, for everything that draws text ----
        if (e.kind !== KIND.Line && e.kind !== KIND.Rectangle && e.kind !== KIND.Image) {
            h.push("<div class='separator my-3'></div>");
            h.push(row(AR ? "الخط" : "Font", select("p-font",
                [{ v: "", t: AR ? "افتراضي" : "Default" }].concat(FONTS.map(function (f) { return { v: f, t: f }; })),
                s.fontFamily || "")));
            h.push("<div class='row g-2'>");
            h.push(numCol(AR ? "الحجم (pt)" : "Size (pt)", "p-size", s.fontSizePt));
            h.push("<div class='col-6 d-flex align-items-end gap-1 pb-1'>" +
                   toggle("p-bold", "B", s.bold) + toggle("p-italic", "I", s.italic) + toggle("p-underline", "U", s.underline) +
                   "</div>");
            h.push("</div>");
            h.push(row(AR ? "المحاذاة" : "Align", select("p-align", [
                { v: 0, t: AR ? "بداية" : "Start" }, { v: 1, t: AR ? "وسط" : "Center" },
                { v: 2, t: AR ? "نهاية" : "End" }, { v: 3, t: AR ? "ضبط" : "Justify" }
            ], s.align)));
            h.push(row(AR ? "رأسيًا" : "Vertical", select("p-valign", [
                { v: 0, t: AR ? "أعلى" : "Top" }, { v: 1, t: AR ? "وسط" : "Middle" }, { v: 2, t: AR ? "أسفل" : "Bottom" }
            ], s.verticalAlign)));

            if (e.kind === KIND.Field || e.kind === KIND.Summary) {
                h.push(row(AR ? "تنسيق رقمي" : "Number format",
                    "<input id='p-numfmt' type='text' class='form-control form-control-sm' placeholder='N2' value='" + esc(s.numberFormat || "") + "'>"));
                h.push(row(AR ? "تنسيق التاريخ" : "Date format",
                    "<input id='p-datefmt' type='text' class='form-control form-control-sm' placeholder='yyyy-MM-dd' value='" + esc(s.dateFormat || "") + "'>"));
            }
        }

        // ---- box style, for everything ----
        h.push("<div class='separator my-3'></div>");
        h.push("<div class='row g-2'>");
        h.push(colorCol(AR ? "اللون" : "Colour", "p-color", s.color));
        h.push(colorCol(AR ? "الخلفية" : "Background", "p-bg", s.background));
        h.push("</div>");
        h.push(row(AR ? "الإطار" : "Border", select("p-border", [
            { v: 0, t: AR ? "بلا" : "None" }, { v: 1, t: AR ? "متصل" : "Solid" },
            { v: 2, t: AR ? "متقطع" : "Dashed" }, { v: 3, t: AR ? "منقَّط" : "Dotted" }
        ], s.borderStyle)));
        h.push("<div class='row g-2'>");
        h.push(colorCol(AR ? "لون الإطار" : "Border colour", "p-bcolor", s.borderColor));
        h.push(numCol(AR ? "حشو (مم)" : "Padding (mm)", "p-pad", s.paddingMm));
        h.push("</div>");

        host.innerHTML = h.join("");
        wireProps(e, p.b);
        renderStatus();
    }

    function row(label, control) {
        return "<div class='mb-2'><label class='fs-8 fw-semibold text-muted d-block mb-1'>" + esc(label) + "</label>" + control + "</div>";
    }
    function numCol(label, id, value) {
        return "<div class='col-6 mb-2'><label class='fs-8 fw-semibold text-muted d-block mb-1' for='" + id + "'>" + esc(label) + "</label>" +
               "<input id='" + id + "' type='number' step='0.5' class='form-control form-control-sm' value='" + (value == null ? "" : value) + "'></div>";
    }
    function colorCol(label, id, value) {
        return "<div class='col-6 mb-2'><label class='fs-8 fw-semibold text-muted d-block mb-1' for='" + id + "'>" + esc(label) + "</label>" +
               "<input id='" + id + "' type='color' class='form-control form-control-sm form-control-color w-100' value='" + esc(value || "#000000") + "'>" +
               "<button type='button' class='btn btn-sm btn-light w-100 mt-1 fs-8 py-1' data-clear='" + id + "'>" + (AR ? "بلا" : "None") + "</button></div>";
    }
    function select(id, options, value) {
        return "<select id='" + id + "' class='form-select form-select-sm'>" +
               options.map(function (o) {
                   return "<option value='" + esc(o.v) + "'" + (String(o.v) === String(value == null ? "" : value) ? " selected" : "") + ">" + esc(o.t) + "</option>";
               }).join("") + "</select>";
    }
    function toggle(id, label, on) {
        return "<button type='button' id='" + id + "' class='btn btn-sm " + (on ? "btn-primary" : "btn-light") + " px-3 py-1 fw-bold fs-8'>" + label + "</button>";
    }

    // ---- THE TABLE DESIGNER ----------------------------------------------------------------------
    // A real column list: field, header, width, alignment, format and an optional total. §7 asks for a
    // table that is designed rather than a fixed grid, and this is what "designed" means in practice —
    // every column is a decision the user made and the renderer reproduces exactly.
    function tableEditor(e) {
        var rows = (e.columns || []).map(function (c, i) {
            return "<div class='border border-dashed rounded p-2 mb-2'>" +
                   "<div class='d-flex flex-stack mb-1'>" +
                   "<span class='fs-8 fw-bold text-gray-700'>#" + (i + 1) + "</span>" +
                   "<span class='d-flex gap-1'>" +
                   "<button type='button' class='btn btn-icon btn-sm btn-light py-0 px-1' data-cup='" + i + "' aria-label='" + (AR ? "أعلى" : "Up") + "'><i class='ki-outline ki-up fs-6'></i></button>" +
                   "<button type='button' class='btn btn-icon btn-sm btn-light py-0 px-1' data-cdown='" + i + "' aria-label='" + (AR ? "أسفل" : "Down") + "'><i class='ki-outline ki-down fs-6'></i></button>" +
                   "<button type='button' class='btn btn-icon btn-sm btn-light-danger py-0 px-1' data-cdel='" + i + "' aria-label='" + (AR ? "حذف" : "Remove") + "'><i class='ki-outline ki-cross fs-6'></i></button>" +
                   "</span></div>" +
                   select("c-field-" + i, S.fields.map(function (f) { return { v: f.key, t: f.title }; }), c.fieldKey) +
                   "<input id='c-head-" + i + "' type='text' class='form-control form-control-sm mt-1' placeholder='" + (AR ? "العنوان (عربي)" : "Header (Arabic)") + "' value='" + esc(c.headerText || "") + "'>" +
                   "<input id='c-head-en-" + i + "' type='text' class='form-control form-control-sm mt-1' dir='ltr' placeholder='" + (AR ? "العنوان (إنجليزي)" : "Header (English)") + "' value='" + esc(c.headerTextEn || "") + "'>" +
                   "<div class='row g-1 mt-1'>" +
                   "<div class='col-6'><input id='c-width-" + i + "' type='number' step='1' min='5' class='form-control form-control-sm' value='" + c.widthMm + "' aria-label='" + (AR ? "العرض" : "Width") + "'></div>" +
                   "<div class='col-6'>" + select("c-align-" + i, [
                       { v: 0, t: AR ? "بداية" : "Start" }, { v: 1, t: AR ? "وسط" : "Center" }, { v: 2, t: AR ? "نهاية" : "End" }
                   ], c.align) + "</div>" +
                   "</div>" +
                   "<div class='row g-1 mt-1'>" +
                   "<div class='col-6'><input id='c-fmt-" + i + "' type='text' class='form-control form-control-sm' placeholder='N2' value='" + esc(c.format || "") + "' aria-label='" + (AR ? "التنسيق" : "Format") + "'></div>" +
                   "<div class='col-6'>" + select("c-total-" + i, [
                       { v: 0, t: AR ? "بلا إجمالي" : "No total" }, { v: 1, t: "SUM" }, { v: 2, t: "AVG" },
                       { v: 3, t: "MIN" }, { v: 4, t: "MAX" }, { v: 5, t: "COUNT" }
                   ], c.total) + "</div>" +
                   "</div></div>";
        }).join("");

        return "<div class='fs-8 fw-bold text-gray-700 mb-2'>" + (AR ? "أعمدة الجدول" : "Table columns") + "</div>" +
               rows +
               "<button type='button' id='c-add' class='btn btn-sm btn-light-primary w-100 fs-8'>" +
               "<i class='ki-outline ki-plus fs-5 me-1'></i>" + (AR ? "إضافة عمود" : "Add column") + "</button>";
    }

    function wireProps(e, b) {
        function on(id, ev, fn) {
            var node = $(id);
            if (node) { node.addEventListener(ev, fn); }
        }
        function edit(fn) { snapshot(); fn(); renderCanvas(); renderStatus(); }
        function num(id, dflt) {
            var v = parseFloat($(id).value);
            return isNaN(v) ? dflt : v;
        }

        on("px", "change", function () { edit(function () { e.xMm = Math.max(0, num("px", e.xMm)); }); });
        on("py", "change", function () { edit(function () { e.yMm = Math.max(0, num("py", e.yMm)); }); });
        on("pw", "change", function () { edit(function () { e.widthMm = Math.max(1, num("pw", e.widthMm)); }); });
        on("ph", "change", function () { edit(function () { e.heightMm = Math.max(1, num("ph", e.heightMm)); }); });

        on("p-band", "change", function () {
            var to = parseInt($("p-band").value, 10);
            var pair = elementById(e.id);
            if (!pair || pair.b.kind === to) { return; }

            snapshot();
            moveToBand(pair, to);

            // CLAMPED into the target band, unlike a drag: here the user named a BAND rather than a
            // position, so the element belongs inside it. A Y left over from a taller band would put it
            // out of sight below its new one, which looks exactly like the move having failed.
            var target = band(to);
            var room = Math.max(0, (target.heightMm || 0) - e.heightMm);
            if (e.yMm > room) { e.yMm = room; }

            renderAll();
        });

        on("p-text", "input", function () { e.text = $("p-text").value; renderCanvas(); });
        on("p-text-en", "input", function () { e.textEn = $("p-text-en").value; renderCanvas(); });
        on("p-field", "change", function () { edit(function () { e.fieldKey = $("p-field").value || null; }); });
        on("p-qrecc", "change", function () { edit(function () { e.qrEcc = parseInt($("p-qrecc").value, 10); }); });
        on("p-qrpx", "change", function () {
            edit(function () {
                var v = parseInt($("p-qrpx").value, 10);
                // The server clamps 2-20; mirroring it here means the author sees the value that will
                // be stored rather than one quietly changed under them.
                e.qrModulePixels = isNaN(v) ? 8 : Math.min(20, Math.max(2, v));
                $("p-qrpx").value = e.qrModulePixels;
            });
        });
        on("p-agg", "change", function () { edit(function () { e.aggregate = parseInt($("p-agg").value, 10); }); });

         // CHART AND CROSS-TAB. Each writes ONE property and re-renders — the canvas face is derived from
           // the element, so there is nothing here that reads the DOM back into the model.
        on("p-subcode", "change", function () { edit(function () { e.subReportCode = $("p-subcode").value || null; }); });
        on("p-sublink", "change", function () { edit(function () { e.linkChildFieldKey = ($("p-sublink").value || "").trim() || null; }); });

        on("p-chartkind", "change", function () {
            edit(function () { e.chartKind = parseInt($("p-chartkind").value, 10); });
            renderProps();   // a pie hides the series row; the other three show it
        });
        on("p-compare", "change", function () {
            edit(function () { e.compare = parseInt($("p-compare").value, 10); });
            renderProps();   // choosing a comparison brings the direction question with it
        });
        on("p-cmpdir", "change", function () {
            edit(function () { e.compareHigherIsBetter = $("p-cmpdir").value === "1"; });
        });

        Array.prototype.forEach.call(document.querySelectorAll("[data-icon]"), function (b) {
            b.addEventListener("click", function () {
                edit(function () { e.icon = parseInt(b.getAttribute("data-icon"), 10); });
                renderProps();
            });
        });

        on("p-cat", "change", function () {
            edit(function () { e.categoryFieldKey = $("p-cat").value || null; });
            renderProps();   // a date axis brings the time bucket with it
        });
        on("p-series", "change", function () {
            edit(function () { e.seriesFieldKey = $("p-series").value || null; });
            renderProps();
        });
        on("p-datebucket", "change", function () {
            edit(function () { e.style.dateFormat = $("p-datebucket").value || null; });
        });
        on("p-showvals", "change", function () { edit(function () { e.showValues = $("p-showvals").checked; }); });
        on("p-showtotals", "change", function () { edit(function () { e.showGrandTotals = $("p-showtotals").checked; }); });
        on("p-maxcat", "change", function () {
            edit(function () {
                var v = parseInt($("p-maxcat").value, 10);
                e.maxCategories = isNaN(v) ? 12 : Math.max(2, Math.min(40, v));
            });
            $("p-maxcat").value = e.maxCategories;
        });
        on("p-sys", "change", function () { edit(function () { e.systemField = parseInt($("p-sys").value, 10); }); });
        on("p-role", "change", function () { edit(function () { e.imageRole = parseInt($("p-role").value, 10); }); });
        on("p-asset", "change", function () {
            edit(function () {
                var v = $("p-asset").value;
                e.assetId = v === "" ? null : parseInt(v, 10);
            });
        });
        on("p-asset-upload", "click", function () { requestImageFor(e); });
        on("p-fit", "change", function () { edit(function () { e.fit = parseInt($("p-fit").value, 10); }); });

        on("p-font", "change", function () { edit(function () { e.style.fontFamily = $("p-font").value || null; }); });
        on("p-size", "change", function () { edit(function () { e.style.fontSizePt = Math.max(4, num("p-size", 9)); }); });
        on("p-bold", "click", function () { edit(function () { e.style.bold = !e.style.bold; }); renderProps(); });
        on("p-italic", "click", function () { edit(function () { e.style.italic = !e.style.italic; }); renderProps(); });
        on("p-underline", "click", function () { edit(function () { e.style.underline = !e.style.underline; }); renderProps(); });
        on("p-align", "change", function () { edit(function () { e.style.align = parseInt($("p-align").value, 10); }); });
        on("p-valign", "change", function () { edit(function () { e.style.verticalAlign = parseInt($("p-valign").value, 10); }); });
        on("p-numfmt", "change", function () { e.style.numberFormat = $("p-numfmt").value || null; });
        on("p-datefmt", "change", function () { e.style.dateFormat = $("p-datefmt").value || null; });

        on("p-color", "change", function () { edit(function () { e.style.color = $("p-color").value; }); });
        on("p-bg", "change", function () { edit(function () { e.style.background = $("p-bg").value; }); });
        on("p-bcolor", "change", function () { edit(function () { e.style.borderColor = $("p-bcolor").value; }); });
        on("p-visible", "change", function () { edit(function () { e.style.visible = $("p-visible").checked; }); });
        on("p-border", "change", function () { edit(function () { e.style.borderStyle = parseInt($("p-border").value, 10); }); });
        on("p-pad", "change", function () { edit(function () { e.style.paddingMm = Math.max(0, num("p-pad", 0.5)); }); });

        Array.prototype.forEach.call(document.querySelectorAll("[data-clear]"), function (btn) {
            btn.addEventListener("click", function () {
                var id = btn.getAttribute("data-clear");
                edit(function () {
                    if (id === "p-color") { e.style.color = null; }
                    if (id === "p-bg") { e.style.background = null; }
                    if (id === "p-bcolor") { e.style.borderColor = null; }
                });
                renderProps();
            });
        });

        // ---- table columns ----
        on("c-add", "click", function () {
            edit(function () {
                e.columns = e.columns || [];
                var f = S.fields[0];
                e.columns.push({ fieldKey: f ? f.key : "", headerText: null, headerTextEn: null, widthMm: 25, align: 0, format: null, total: 0 });
            });
            renderProps();
        });

        (e.columns || []).forEach(function (c, i) {
            on("c-field-" + i, "change", function () { edit(function () { c.fieldKey = $("c-field-" + i).value; }); });
            on("c-head-" + i, "change", function () { edit(function () { c.headerText = $("c-head-" + i).value || null; }); });
            on("c-head-en-" + i, "change", function () { edit(function () { c.headerTextEn = $("c-head-en-" + i).value || null; }); });
            on("c-width-" + i, "change", function () { edit(function () { c.widthMm = Math.max(5, num("c-width-" + i, 25)); }); });
            on("c-align-" + i, "change", function () { edit(function () { c.align = parseInt($("c-align-" + i).value, 10); }); });
            on("c-fmt-" + i, "change", function () { edit(function () { c.format = $("c-fmt-" + i).value || null; }); });
            on("c-total-" + i, "change", function () { edit(function () { c.total = parseInt($("c-total-" + i).value, 10); }); });

            on("c-cdel-" + i, "click", function () { });
        });

        Array.prototype.forEach.call(document.querySelectorAll("[data-cdel]"), function (btn) {
            btn.addEventListener("click", function () {
                edit(function () { e.columns.splice(parseInt(btn.getAttribute("data-cdel"), 10), 1); });
                renderProps();
            });
        });
        Array.prototype.forEach.call(document.querySelectorAll("[data-cup]"), function (btn) {
            btn.addEventListener("click", function () {
                var i = parseInt(btn.getAttribute("data-cup"), 10);
                if (i <= 0) { return; }
                edit(function () { var t = e.columns[i - 1]; e.columns[i - 1] = e.columns[i]; e.columns[i] = t; });
                renderProps();
            });
        });
        Array.prototype.forEach.call(document.querySelectorAll("[data-cdown]"), function (btn) {
            btn.addEventListener("click", function () {
                var i = parseInt(btn.getAttribute("data-cdown"), 10);
                if (i >= e.columns.length - 1) { return; }
                edit(function () { var t = e.columns[i + 1]; e.columns[i + 1] = e.columns[i]; e.columns[i] = t; });
                renderProps();
            });
        });
    }

    // =============================================================================================
    // BAND LIST — heights and the grouping field.
    // =============================================================================================
    function renderBandList() {
        var groupable = S.fields.filter(function (f) { return f.groupable; });

        $("cbd-bandlist").innerHTML = S.layout.bands.map(function (b) {
            var isGroup = b.kind === BAND.GroupHeader || b.kind === BAND.GroupFooter;
            var g = isGroup
                ? "<div class='mt-1'>" + select("bg-" + b.kind,
                    [{ v: "", t: AR ? "— بلا تجميع —" : "— no grouping —" }].concat(groupable.map(function (f) {
                        return { v: f.key, t: f.title };
                    })), b.groupFieldKey || "") + "</div>"
                : "";

            return "<div class='border border-dashed rounded p-2" + (S.selectedBand === b.kind ? " border-primary" : "") + "' data-bandrow='" + b.kind + "'>" +
                   "<div class='d-flex flex-stack'>" +
                   "<span class='fs-8 fw-bold text-gray-800'>" + esc(BAND_LABEL[b.kind]) + "</span>" +
                   "<input id='bh-" + b.kind + "' type='number' step='1' min='0' class='form-control form-control-sm w-70px' value='" + (b.heightMm || 0) + "' aria-label='" + esc(BAND_LABEL[b.kind]) + "'>" +
                   "</div>" + g + "</div>";
        }).join("");

        S.layout.bands.forEach(function (b) {
            var input = $("bh-" + b.kind);
            if (input) {
                input.addEventListener("change", function () {
                    var v = parseFloat(input.value);
                    snapshot();
                    b.heightMm = isNaN(v) ? 0 : Math.max(0, v);
                    renderCanvas();
                });
            }
            var sel = $("bg-" + b.kind);
            if (sel) {
                sel.addEventListener("change", function () {
                    snapshot();
                    b.groupFieldKey = sel.value || null;
                    renderCanvas();
                });
            }
        });

        Array.prototype.forEach.call($("cbd-bandlist").querySelectorAll("[data-bandrow]"), function (rowEl) {
            rowEl.addEventListener("click", function (ev) {
                if (ev.target.tagName === "INPUT" || ev.target.tagName === "SELECT") { return; }
                S.selectedBand = parseInt(rowEl.getAttribute("data-bandrow"), 10);
                renderCanvas(); renderBandList();
            });
        });
    }

    // =============================================================================================
    // DATA TAB — fields, parameters, filters, sorts
    // =============================================================================================
    function renderFields() {
        $("cbd-fields").innerHTML = S.fields.map(function (f) {
            return "<button type='button' draggable='true' data-field='" + esc(f.key) + "' " +
                   "class='btn btn-sm btn-light justify-content-start fw-semibold fs-8 py-2 cbd-tool'>" +
                   "<i class='ki-outline ki-abstract-25 fs-5 me-2 text-primary'></i>" + esc(f.title) + "</button>";
        }).join("");

        Array.prototype.forEach.call($("cbd-fields").querySelectorAll("[data-field]"), function (b) {
            b.addEventListener("dragstart", function (ev) {
                ev.dataTransfer.setData("text/plain", "field:" + b.getAttribute("data-field"));
                ev.dataTransfer.effectAllowed = "copy";
            });
            b.addEventListener("click", function () { dropField(b.getAttribute("data-field"), null); });
        });
    }

    // §9. Rendered from the DESCRIPTORS the dataset declares — type, required, options, bounds — so a
    // dataset that gains a parameter gains an input here with no change to this file.
    function renderParams() {
        var host = $("cbd-params");
        $("cbd-params-empty").style.display = S.paramDefs.length ? "none" : "";

        host.innerHTML = S.paramDefs.map(function (d) {
            var v = S.parameters[d.key] == null ? "" : S.parameters[d.key];
            var input;

            if (d.options && d.options.length) {
                input = "<select id='pa-" + esc(d.key) + "' class='form-select form-select-sm'>" +
                        "<option value=''>" + (AR ? "— افتراضي —" : "— default —") + "</option>" +
                        d.options.map(function (o) {
                            return "<option value='" + esc(o.value) + "'" + (o.value === v ? " selected" : "") + ">" + esc(o.label) + "</option>";
                        }).join("") + "</select>";
            } else {
                // 4 = Date in ReportFieldType; anything else takes text, and the SERVER's binder decides
                // whether the text is acceptable. A client-side type guess here would be a second rule.
                var type = d.type === 4 ? "date" : (d.type === 2 || d.type === 3 || d.type === 1 ? "number" : "text");
                input = "<input id='pa-" + esc(d.key) + "' type='" + type + "' class='form-control form-control-sm' " +
                        (d.minValue ? "min='" + esc(d.minValue) + "' " : "") +
                        (d.maxValue ? "max='" + esc(d.maxValue) + "' " : "") +
                        "placeholder='" + esc(d.defaultValue || "") + "' value='" + esc(v) + "'>";
            }

            return "<div><label class='fs-8 fw-semibold text-muted d-block mb-1' for='pa-" + esc(d.key) + "'>" +
                   esc(d.title) + (d.required ? " <span class='text-danger'>*</span>" : "") + "</label>" +
                   input +
                   (d.help ? "<div class='form-text fs-9 mt-1'>" + esc(d.help) + "</div>" : "") +
                   "</div>";
        }).join("");

        S.paramDefs.forEach(function (d) {
            var node = $("pa-" + d.key);
            if (node) {
                node.addEventListener("change", function () {
                    S.parameters[d.key] = node.value === "" ? null : node.value;
                });
            }
        });
    }

    // ReportFilterOperator: 8 = Between, 9 = In, 10 = IsNull, 11 = IsNotNull.
    function isBetween(op) { return op === 8; }
    function isIn(op) { return op === 9; }
    function takesNoValue(op) { return op === 10 || op === 11; }

    // WHAT THE OPERATOR NEEDS, shown. One box for a comparison, two for a range, a list for IN, and
    // none at all for "is null" — a value box beside "is empty" invites a value nothing will read.
    function valueInputs(f, i) {
        if (takesNoValue(f.operator)) {
            return "<div class='col-7 d-flex align-items-center'><span class='fs-9 text-muted'>" +
                   esc(AR ? "بدون قيمة" : "no value") + "</span></div>";
        }
        if (isBetween(f.operator)) {
            return "<div class='col-7'><div class='row g-1'>" +
                   "<div class='col-6'><input id='f-val-" + i + "' type='text' class='form-control form-control-sm' value='" +
                   esc((f.values && f.values[0]) || "") + "' placeholder='" + esc(AR ? "من" : "from") + "'></div>" +
                   "<div class='col-6'><input id='f-val2-" + i + "' type='text' class='form-control form-control-sm' value='" +
                   esc((f.values && f.values[1]) || "") + "' placeholder='" + esc(AR ? "إلى" : "to") + "'></div>" +
                   "</div></div>";
        }
        if (isIn(f.operator)) {
            return "<div class='col-7'><input id='f-val-" + i + "' type='text' class='form-control form-control-sm' value='" +
                   esc((f.values || []).join(", ")) + "' placeholder='" + esc(AR ? "قيم مفصولة بفاصلة" : "comma-separated") + "'></div>";
        }
        return "<div class='col-7'><input id='f-val-" + i + "' type='text' class='form-control form-control-sm' value='" +
               esc((f.values && f.values[0]) || "") + "' aria-label='" + (AR ? "القيمة" : "Value") + "'></div>";
    }

    function renderFilters() {
        $("cbd-filters").innerHTML = S.filters.map(function (f, i) {
            var field = fieldOf(f.field);
            var ops = field ? field.operators : [];
            return "<div class='border border-dashed rounded p-2'>" +
                   "<div class='d-flex flex-stack mb-1'>" +
                   "<span class='fs-8 fw-bold text-gray-700'>#" + (i + 1) + "</span>" +
                   "<button type='button' class='btn btn-icon btn-sm btn-light-danger py-0 px-1' data-fdel='" + i + "' aria-label='" + (AR ? "حذف" : "Remove") + "'><i class='ki-outline ki-cross fs-6'></i></button>" +
                   "</div>" +
                   select("f-field-" + i, S.fields.filter(function (x) { return x.filterable; }).map(function (x) {
                       return { v: x.key, t: x.title };
                   }), f.field) +
                   "<div class='row g-1 mt-1'>" +
                   "<div class='col-5'>" + select("f-op-" + i, ops.map(function (o) { return { v: o.operator, t: o.label }; }), f.operator) + "</div>" +
                   valueInputs(f, i) +
                   "</div></div>";
        }).join("");

        S.filters.forEach(function (f, i) {
            var fe = $("f-field-" + i), oe = $("f-op-" + i);
            if (fe) { fe.addEventListener("change", function () { f.field = fe.value; renderFilters(); }); }
            // THE OPERATOR REPAINTS THE ROW: how many value boxes there are depends on it, and a
            // range that kept a single box is how "between" came to mean "greater than" in silence.
            if (oe) {
                oe.addEventListener("change", function () {
                    f.operator = parseInt(oe.value, 10);
                    if (takesNoValue(f.operator)) { f.values = []; }
                    else if (isBetween(f.operator) && (f.values || []).length < 2) {
                        f.values = [(f.values || [])[0] || "", ""];
                    }
                    renderFilters();
                });
            }
            // BETWEEN keeps two, IN keeps the list, everything else keeps one — and what the editor
            // shows is what gets stored, so a second endpoint typed into a range is not discarded on
            // the next repaint.
            var v1 = $("f-val-" + i), v2 = $("f-val2-" + i);
            function commit() {
                if (isBetween(f.operator)) {
                    f.values = [v1 ? v1.value : "", v2 ? v2.value : ""];
                } else if (isIn(f.operator)) {
                    f.values = (v1 ? v1.value : "").split(",")
                        .map(function (s) { return s.trim(); })
                        .filter(function (s) { return s.length > 0; });
                } else if (takesNoValue(f.operator)) {
                    f.values = [];
                } else {
                    f.values = [v1 ? v1.value : ""];
                }
            }
            if (v1) { v1.addEventListener("change", commit); }
            if (v2) { v2.addEventListener("change", commit); }
        });
        Array.prototype.forEach.call($("cbd-filters").querySelectorAll("[data-fdel]"), function (b) {
            b.addEventListener("click", function () {
                S.filters.splice(parseInt(b.getAttribute("data-fdel"), 10), 1);
                renderFilters();
            });
        });
    }

    function renderSorts() {
        $("cbd-sorts").innerHTML = S.sorts.map(function (s, i) {
            return "<div class='d-flex align-items-center gap-1'>" +
                   "<div class='flex-grow-1'>" + select("s-field-" + i, S.fields.filter(function (x) { return x.sortable; }).map(function (x) {
                       return { v: x.key, t: x.title };
                   }), s.field) + "</div>" +
                   "<div class='w-100px'>" + select("s-dir-" + i, [
                       { v: "false", t: AR ? "تصاعدي" : "Asc" }, { v: "true", t: AR ? "تنازلي" : "Desc" }
                   ], String(!!s.descending)) + "</div>" +
                   "<button type='button' class='btn btn-icon btn-sm btn-light-danger' data-sdel='" + i + "' aria-label='" + (AR ? "حذف" : "Remove") + "'><i class='ki-outline ki-cross fs-6'></i></button>" +
                   "</div>";
        }).join("");

        S.sorts.forEach(function (s, i) {
            var fe = $("s-field-" + i), de = $("s-dir-" + i);
            if (fe) { fe.addEventListener("change", function () { s.field = fe.value; }); }
            if (de) { de.addEventListener("change", function () { s.descending = de.value === "true"; }); }
        });
        Array.prototype.forEach.call($("cbd-sorts").querySelectorAll("[data-sdel]"), function (b) {
            b.addEventListener("click", function () {
                S.sorts.splice(parseInt(b.getAttribute("data-sdel"), 10), 1);
                renderSorts();
            });
        });
    }

    // The bytes behind an asset row are fetched by ID, and that fetch can 404 while the row is perfectly
    // present — the row lives in the database, the file lives on the application's disk. Wherever such an
    // image is drawn, this says so rather than leaving an empty box: in the picker as a red dashed
    // thumbnail, on the canvas as a short label in place of the picture.
    function markMissingImages(root) {
        Array.prototype.forEach.call(root.querySelectorAll("img[src*='/studio/assets/']"), function (img) {
            var flag = function () {
                var thumb = img.closest(".cbd-asset");
                if (thumb) {
                    // The <img> is hidden rather than left to render its alt text: a broken-image glyph plus a
                    // filename spills out of a 46px box and reads as damage to the screen, not to the file.
                    img.style.display = "none";
                    thumb.classList.add("missing");
                    thumb.title = (AR ? "ملف الصورة غير موجود على هذا الخادم: " : "Image file missing on this server: ")
                                + (img.getAttribute("alt") || "");
                    return;
                }
                var host = img.parentNode;
                if (!host || host.querySelector(".cbd-img-missing")) { return; }
                img.style.display = "none";
                var span = document.createElement("span");
                span.className = "cbd-img-missing";
                span.textContent = AR ? "ملف الصورة غير موجود" : "image file missing";
                host.appendChild(span);
            };
            if (img.complete && img.naturalWidth === 0) { flag(); }
            img.addEventListener("error", flag);
        });
    }

    // "GIVE THIS ELEMENT A PICTURE." One place, two ways in: the button in the element's own Picture row,
    // and a double-click on the element itself.
    //
    // The element's role is pushed into the role selector first, so an image imported for a signature is
    // FILED as a signature — the user should not have to find a selector in the other rail to get that
    // right. assetTarget is what makes the returned image land on this element instead of merely joining
    // the list.
    function requestImageFor(e) {
        if (!e || e.kind !== KIND.Image) { return; }
        var roleSelect = $("cbd-asset-role");
        if (roleSelect) { roleSelect.value = String(e.imageRole); }
        S.assetTarget = e.id;
        $("cbd-asset-file").click();
    }

    function renderAssets() {
        $("cbd-assets").innerHTML = S.assets.map(function (a) {
            return "<div class='cbd-asset' draggable='true' data-asset='" + a.id + "' title='" + esc(a.title || a.fileName) + "'>" +
                   "<img src='/api/reports/studio/assets/" + a.id + "' alt='" + esc(a.title || a.fileName) + "'></div>";
        }).join("");

        Array.prototype.forEach.call($("cbd-assets").querySelectorAll("[data-asset]"), function (node) {
            node.addEventListener("dragstart", function (ev) {
                ev.dataTransfer.setData("text/plain", "asset:" + node.getAttribute("data-asset"));
                ev.dataTransfer.effectAllowed = "copy";
            });
            node.addEventListener("click", function () { dropAsset(parseInt(node.getAttribute("data-asset"), 10), null); });
        });

        markMissingImages($("cbd-assets"));
    }

    function renderAll() {
        renderCanvas();
        renderProps();
        renderBandList();
        renderFilters();
        renderSorts();
        syncUndoButtons();
    }

    // =============================================================================================
    // SERVER CALLS
    // =============================================================================================
    function draft() {
        return {
            templateId: S.templateId,
            datasetCode: S.datasetCode,
            name: $("cbd-name").value,
            nameEn: $("cbd-name-en").value,
            columns: S.columns,
            filters: S.filters,
            sorts: S.sorts,
            parameters: S.parameters,
            pageSize: 200,

            // WHO IT IS FOR. A draft that does not say is Personal, which is what every layout designed
            // here silently was before this control existed.
            scope: parseInt(($("cbd-scope") || {}).value || "3", 10),
            isDefault: !!(($("cbd-default") || {}).checked),

            visual: S.layout
        };
    }

    function post(url, body) {
        return fetch(url, {
            method: "POST",
            headers: { "Content-Type": "application/json", "RequestVerificationToken": token() },
            body: JSON.stringify(body)
        });
    }

    // A VISIBLE OUTCOME. The status bar is below the fold on a 1000px window, so anything that
    // only lands there has not been reported. Swal is already on this screen; a toast is used
    // rather than CB.toast because that degrades to a modal alert() when toastr is missing, and
    // toastr is not in this application's plugins bundle.
    function notify(kind, text) {
        if (typeof Swal === "undefined" || !Swal || !Swal.fire) { return; }
        Swal.fire({
            toast: true,
            // Follows the page direction, so it never sits on top of the toolbar it reports about.
            position: (document.documentElement.getAttribute("dir") === "rtl") ? "top-start" : "top-end",
            icon: kind,
            title: text,
            showConfirmButton: false,
            timer: kind === "error" ? 6000 : 2600,
            timerProgressBar: true
        });
    }

    function showErrors(list) {
        var box = $("cbd-errors");
        if (!list || !list.length) { box.classList.add("d-none"); box.innerHTML = ""; return; }
        box.classList.remove("d-none");
        box.innerHTML = "<ul class='mb-0 ps-4'>" + list.map(function (e) { return "<li>" + esc(e) + "</li>"; }).join("") + "</ul>";
        // AND a toast, for the same reason the save confirmation needed one: this box is inside the
        // designer shell and can be scrolled out of sight, and a refusal nobody sees is a refusal
        // that reads as "the button is broken".
        notify("error", list[0]);
    }

    function loadDataset(code) {
        S.datasetCode = code;
        S.fields = []; S.paramDefs = [];
        if (!code) { renderFields(); renderParams(); renderAll(); return; }

        fetch("/api/reports/studio/fields?datasetCode=" + encodeURIComponent(code))
            .then(function (r) { return r.json(); })
            .then(function (d) { S.fields = d.fields || []; renderFields(); renderAll(); });

        fetch("/api/reports/studio/parameters?datasetCode=" + encodeURIComponent(code))
            .then(function (r) { return r.json(); })
            .then(function (d) { S.paramDefs = d.parameters || []; renderParams(); });
    }

    function loadAssets() {
        fetch("/api/reports/studio/assets")
            .then(function (r) { return r.json(); })
            .then(function (d) { S.assets = d.assets || []; renderAssets(); renderProps(); });
    }

    function doPreview() {
        showErrors(null);
        $("cbd-preview-body").innerHTML = "<div class='text-center py-10'><span class='spinner-border text-primary'></span></div>";
        var modal = new bootstrap.Modal($("cbd-preview-modal"));
        modal.show();

        post("/api/reports/studio/preview", draft())
            .then(function (r) { return r.json().then(function (d) { return { ok: r.ok, d: d }; }); })
            .then(function (res) {
                if (!res.ok) {
                    $("cbd-preview-body").innerHTML = "";
                    $("cbd-preview-meta").textContent = "";
                    showErrors(res.d.errors || [AR ? "تعذر إنشاء المعاينة." : "The preview could not be produced."]);
                    modal.hide();
                    return;
                }
                // The RENDERER's markup, verbatim. Not restyled here: a preview that was prettied up locally
                // would stop being a preview of the printed document.
                $("cbd-preview-body").innerHTML = res.d.html;
                $("cbd-preview-meta").textContent =
                    (AR ? "عدد الصفوف: " : "Rows: ") + res.d.rowCount +
                    (res.d.truncated ? (AR ? " (مقتطع)" : " (truncated)") : "");
            });
    }

    function doPrint() {
        showErrors(null);
        post("/api/reports/studio/print", draft())
            .then(function (r) {
                if (!r.ok) { return r.json().then(function (d) { showErrors(d.errors || []); return null; }); }
                return r.text();
            })
            .then(function (html) {
                if (!html) { return; }
                // A blank window carrying the SAME document the PDF converts. No second print template.
                var w = window.open("", "_blank");
                if (!w) { showErrors([AR ? "منع المتصفح فتح نافذة الطباعة." : "The browser blocked the print window."]); return; }
                w.document.open(); w.document.write(html); w.document.close();
                w.onload = function () { w.focus(); w.print(); };
            });
    }

    function doDownload(url, body) {
        showErrors(null);
        post(url, body)
            .then(function (r) {
                if (!r.ok) { return r.json().then(function (d) { showErrors(d.errors || []); return null; }); }
                var name = "report";
                var cd = r.headers.get("Content-Disposition") || "";
                var m = /filename\*?=(?:UTF-8''|")?([^";]+)/i.exec(cd);
                if (m) { name = decodeURIComponent(m[1].replace(/"/g, "")); }
                return r.blob().then(function (b) { return { blob: b, name: name }; });
            })
            .then(function (file) {
                if (!file) { return; }
                var a = document.createElement("a");
                a.href = URL.createObjectURL(file.blob);
                a.download = file.name;
                document.body.appendChild(a); a.click();
                setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
            });
    }

    function doSave() {
        showErrors(null);
        post("/api/reports/studio/save", draft())
            .then(function (r) { return r.json().then(function (d) { return { ok: r.ok, d: d }; }); })
            .then(function (res) {
                if (!res.ok) { showErrors(res.d.errors || []); return; }
                S.templateId = res.d.templateId;
                var msg = (AR ? "تم الحفظ — نسخة " : "Saved — version ") + res.d.versionNo;
                // The status bar keeps its record; the toast is what the person actually sees.
                $("cbd-status").textContent = msg;
                notify("success", msg);
            });
    }

    function doOpen(templateId) {
        fetch("/api/reports/studio/open/" + templateId)
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (d) {
                if (!d) { showErrors([AR ? "تعذر فتح التقرير." : "That report could not be opened."]); return; }
                applyDraft(d.draft);
            });
    }

    // =============================================================================================
    // NEW AND CLEAR
    //
    // TWO DIFFERENT ERASERS, and the difference between them is the whole point:
    //
    //   CLEAR  empties the PAGE — every element in every band — and keeps everything around it: the
    //          report name, the data set, the filters, the sorts, the paper setup. It is ONE undo step,
    //          so Ctrl+Z brings the design straight back.
    //   NEW    throws the DRAFT away and starts an untitled one: no template id, no data set, no
    //          filters, and an empty undo history — which is precisely why it cannot be undone, and why
    //          it asks the harder question. Nothing already SAVED is touched either way; neither of
    //          these calls the server at all.
    // =============================================================================================
    function confirmThen(title, text, confirmLabel, fn) {
        if (typeof Swal !== "undefined" && Swal && Swal.fire) {
            Swal.fire({
                title: title,
                text: text,
                icon: "warning",
                showCancelButton: true,
                confirmButtonText: confirmLabel,
                cancelButtonText: AR ? "إلغاء" : "Cancel",
                buttonsStyling: false,
                customClass: { confirmButton: "btn btn-danger", cancelButton: "btn btn-light" }
            }).then(function (r) { if (r && r.isConfirmed) { fn(); } });
            return;
        }
        // Swal arrives with the shared plugin bundle. If that ever stops being true, a destructive action
        // must still ASK rather than quietly become a one-click wipe.
        if (window.confirm(title + "\n\n" + text)) { fn(); }
    }

    function clearPage() {
        var count = S.layout.bands.reduce(function (a, b) { return a + b.elements.length; }, 0);
        if (!count) { return; }   // nothing to clear, so nothing to ask about

        confirmThen(
            AR ? "مسح الصفحة؟" : "Clear the page?",
            AR ? "سيتم حذف " + count + " عنصرًا من التصميم. الاسم ومجموعة البيانات والفلاتر وإعداد الورق تبقى كما هي، ويمكن التراجع بـ Ctrl+Z."
               : count + " element(s) will be removed from the design. The name, data set, filters and paper setup stay as they are, and Ctrl+Z brings the design back.",
            AR ? "مسح" : "Clear",
            function () {
                snapshot();
                S.layout.bands.forEach(function (b) { b.elements = []; });
                S.selection = [];
                S.assetTarget = null;
                showErrors(null);
                renderAll();
            });
    }

    function newReport() {
        confirmThen(
            AR ? "تقرير جديد؟" : "Start a new report?",
            AR ? "سيتم البدء من صفحة فارغة: التصميم والاسم ومجموعة البيانات والفلاتر والترتيب كلها تُلغى، ولا يمكن التراجع عن ذلك. أي تقرير محفوظ لا يتأثر."
               : "You will start from an empty page: the design, name, data set, filters and sorts are all discarded, and that cannot be undone. Anything already saved is untouched.",
            AR ? "تقرير جديد" : "New report",
            function () {
                S.assetTarget = null;
                showErrors(null);
                applyDraft({
                    templateId: 0, datasetCode: "", name: "", nameEn: "",
                    columns: [], filters: [], sorts: [], parameters: {},
                    visual: blankLayout()
                });
            });
    }

    function applyDraft(dr) {
        if (!dr) { return; }
        S.templateId = dr.templateId || 0;
        S.datasetCode = dr.datasetCode || "";
        S.columns = dr.columns || [];
        S.filters = (dr.filters || []).map(function (f) { return { field: f.field, operator: f.operator, values: f.values || [] }; });
        S.sorts = (dr.sorts || []).map(function (s) { return { field: s.field, descending: !!s.descending }; });
        S.parameters = dr.parameters || {};
        // THE TEMPLATE'S OWN PAGE, whether or not it has a visual design.
        //
        // This was `dr.visual || blankLayout()`. For a template with no Visual — one designed before the
        // visual designer existed, or whose visual did not survive validation — the designer opened on
        // blankLayout()'s hardcoded A4/portrait/18-16-12-12/no-font and the next save persisted exactly
        // that, so opening a report and pressing save reset its paper, its margins and its font. The draft
        // now carries the stored page setup separately, and it wins over any default.
        S.layout = dr.visual || blankLayout();
        if (dr.pageSetup) {
            S.layout.page = Object.assign({}, S.layout.page, dr.pageSetup);
        }
        S.selection = [];
        undoStack.length = 0; redoStack.length = 0;

        $("cbd-name").value = dr.name || "";
        $("cbd-name-en").value = dr.nameEn || "";

        // WHO IT IS FOR, from the template rather than from a default. A control that opened on a
        // guess would re-scope the layout on the next save — a personal draft silently published, or
        // worse, a company standard quietly made private.
        //
        // A scope this designer does not offer (Team, Platform) is SHOWN AS IT IS by adding it to the
        // list rather than falling back to "only me": the author must not be able to change what they
        // cannot see, and pressing save must not move a team layout out of its team.
        var scopeSel = $("cbd-scope");
        if (scopeSel) {
            var sc = (dr.scope == null) ? 3 : dr.scope;
            if (!Array.prototype.some.call(scopeSel.options, function (o) { return +o.value === +sc; })) {
                var extra = document.createElement("option");
                extra.value = sc;
                extra.textContent = (sc === 2) ? (AR ? "فريق" : "Team")
                                  : (sc === 0) ? (AR ? "المنصة" : "Platform")
                                  : String(sc);
                extra.disabled = true;
                scopeSel.appendChild(extra);
            }
            scopeSel.value = String(sc);
        }
        if ($("cbd-default")) { $("cbd-default").checked = !!dr.isDefault; }

        $("cbd-dataset").value = S.datasetCode;
        syncPageInputs();
        loadDataset(S.datasetCode);
        renderAll();
    }

    // =============================================================================================
    // PAGE SETUP INPUTS
    // =============================================================================================
    function syncPageInputs() {
        var pg = S.layout.page;
        $("cbd-paper").value = pg.pageSize;
        $("cbd-orient").value = pg.orientation;

        // A LAYOUT SAVED BEFORE THESE EXISTED has no value for them, and the renderers' own defaults
        // are true — so an absent flag reads as ON rather than silently turning a header off.
        if ($("cbd-dir")) { $("cbd-dir").value = String(pg.direction == null ? 0 : pg.direction); }
        // Opening a saved template sets the selects; the segmented buttons read from them, so they
        // are repainted here. Without it a report saved as Landscape opened with Portrait lit.
        if (typeof CBD_PAINT_SEGMENTS === "function") { CBD_PAINT_SEGMENTS(); }
        if ($("cbd-show-header")) { $("cbd-show-header").checked = pg.showHeader !== false; }
        if ($("cbd-show-footer")) { $("cbd-show-footer").checked = pg.showFooter !== false; }
        if ($("cbd-show-pageno")) { $("cbd-show-pageno").checked = pg.showPageNumbers !== false; }
        if ($("cbd-repeat-head")) { $("cbd-repeat-head").checked = pg.repeatHeaderRow !== false; }
        $("cbd-mt").value = pg.marginTopMm;
        $("cbd-mb").value = pg.marginBottomMm;
        $("cbd-ml").value = pg.marginLeftMm;
        $("cbd-mr").value = pg.marginRightMm;
        $("cbd-font").value = pg.fontFamily || "";
        $("cbd-fontsize").value = pg.fontSizePt ? pg.fontSizePt : "";
        $("cbd-grid").value = S.layout.gridMm;
        $("cbd-snap").checked = !!S.layout.snapToGrid;
    }

    function wirePageInputs() {
        function bindNum(id, apply) {
            $(id).addEventListener("change", function () {
                var v = parseFloat($(id).value);
                if (isNaN(v)) { return; }
                snapshot(); apply(Math.max(0, v)); renderCanvas();
            });
        }
        $("cbd-paper").addEventListener("change", function () {
            snapshot(); S.layout.page.pageSize = parseInt($("cbd-paper").value, 10); renderCanvas();
        });
        $("cbd-orient").addEventListener("change", function () {
            snapshot(); S.layout.page.orientation = parseInt($("cbd-orient").value, 10); renderCanvas();
        });
        bindNum("cbd-mt", function (v) { S.layout.page.marginTopMm = v; });
        bindNum("cbd-mb", function (v) { S.layout.page.marginBottomMm = v; });
        bindNum("cbd-ml", function (v) { S.layout.page.marginLeftMm = v; });
        bindNum("cbd-mr", function (v) { S.layout.page.marginRightMm = v; });
        bindNum("cbd-grid", function (v) { S.layout.gridMm = Math.max(1, v); });

        // A font change repaints the canvas, because the whole point is seeing it. snapshot() first so
        // it lands on the undo stack like every other page edit.
        $("cbd-font").addEventListener("change", function () {
            snapshot();
            var v = $("cbd-font").value;
            S.layout.page.fontFamily = v ? v : null;
            renderCanvas();
        });
        $("cbd-fontsize").addEventListener("change", function () {
            snapshot();
            var v = parseFloat($("cbd-fontsize").value);
            // Out of range clears it rather than clamping silently: the server validates 5-30 and a
            // value quietly changed under the author is worse than one that visibly did not take.
            S.layout.page.fontSizePt = (isNaN(v) || v < 5 || v > 30) ? null : v;
            if (S.layout.page.fontSizePt === null) { $("cbd-fontsize").value = ""; }
            renderCanvas();
        });

        // DIRECTION repaints, because the canvas mirrors with it — an author changing it has to see
        // the design flip, not discover it at print time.
        if ($("cbd-dir")) {
            $("cbd-dir").addEventListener("change", function () {
                snapshot();
                S.layout.page.direction = parseInt($("cbd-dir").value, 10);
                renderCanvas();
            });
        }

        // The four page flags. No repaint: none of them changes the canvas, they change what the
        // RENDERER emits — the running header, the page footer, the repeated table header.
        function bindFlag(id, apply) {
            var node = $(id);
            if (!node) { return; }
            node.addEventListener("change", function () { snapshot(); apply(node.checked); renderStatus(); });
        }
        bindFlag("cbd-show-header", function (v) { S.layout.page.showHeader = v; });
        bindFlag("cbd-show-footer", function (v) { S.layout.page.showFooter = v; });
        bindFlag("cbd-show-pageno", function (v) { S.layout.page.showPageNumbers = v; });
        bindFlag("cbd-repeat-head", function (v) { S.layout.page.repeatHeaderRow = v; });

        $("cbd-snap").addEventListener("change", function () { S.layout.snapToGrid = $("cbd-snap").checked; });
        $("cbd-showgrid").addEventListener("change", function () { S.showGrid = $("cbd-showgrid").checked; renderCanvas(); });

        $("cbd-zoom").addEventListener("input", function () {
            S.zoom = parseInt($("cbd-zoom").value, 10) / 100;
            $("cbd-zoom-label").textContent = Math.round(S.zoom * 100) + "%";
            renderCanvas();
        });
        $("cbd-fit").addEventListener("click", fitToWidth);

        // =========================================================================================
        // THE ICON TOOLBAR'S OWN THREE BEHAVIOURS.
        //
        // The toggles need none: they are still checkboxes behind a styled label, so the click, the
        // `change` event and .checked all work exactly as they did, and every bindFlag() above is
        // untouched. These three are what an icon toolbar adds on top.
        // =========================================================================================

        // 1. SEGMENTED CHOICE -> THE SELECT IT SPEAKS FOR.
        //
        // Orientation and text direction are still <select>s, hidden. The buttons set the value and
        // dispatch `change`, so the handlers registered above hear exactly what they heard when a
        // person used the dropdown - there is no second path into the model, and loadPage() still
        // writes to one element rather than to a set of buttons.
        function paintSegments() {
            Array.prototype.forEach.call(document.querySelectorAll("[data-seg]"), function (b) {
                var sel = $(b.getAttribute("data-seg"));
                var on = sel && String(sel.value) === b.getAttribute("data-val");
                b.classList.toggle("on", !!on);
                b.setAttribute("aria-pressed", on ? "true" : "false");
            });
        }

        Array.prototype.forEach.call(document.querySelectorAll("[data-seg]"), function (b) {
            b.addEventListener("click", function () {
                var sel = $(b.getAttribute("data-seg"));
                if (!sel) { return; }
                sel.value = b.getAttribute("data-val");
                sel.dispatchEvent(new Event("change"));   // the real handler, not a copy of it
                paintSegments();
            });
        });

        // A template load writes to the selects directly, so the buttons follow the selects rather
        // than the other way round.
        ["cbd-orient", "cbd-dir"].forEach(function (id) {
            var sel = $(id);
            if (sel) { sel.addEventListener("change", paintSegments); }
        });
        paintSegments();
        CBD_PAINT_SEGMENTS = paintSegments;

        // 2. THE MARGINS POPOVER. Four numbers that belong together and do not belong in the row.
        (function () {
            var btn = $("cbd-margins-btn"), pop = $("cbd-margins-pop");
            if (!btn || !pop) { return; }

            function open(on) {
                pop.hidden = !on;
                btn.setAttribute("aria-expanded", on ? "true" : "false");
                btn.classList.toggle("on", on);
            }
            btn.addEventListener("click", function (ev) { ev.stopPropagation(); open(pop.hidden); });

            // Closing on an outside click is listened for on the DOCUMENT, so a click anywhere -
            // including on the canvas, which stops its own propagation - still closes it.
            document.addEventListener("click", function (ev) {
                if (!pop.hidden && !pop.contains(ev.target) && ev.target !== btn) { open(false); }
            });
            pop.addEventListener("click", function (ev) { ev.stopPropagation(); });
            document.addEventListener("keydown", function (ev) {
                if (ev.key === "Escape" && !pop.hidden) { open(false); btn.focus(); }
            });
        })();

        // 3. ZOOM STEPPERS. The slider stays and stays authoritative; these nudge it and let it
        // announce the change, for the same reason the segments do not touch the model directly.
        Array.prototype.forEach.call(document.querySelectorAll("[data-zoom]"), function (b) {
            b.addEventListener("click", function () {
                var r = $("cbd-zoom");
                if (!r) { return; }
                var step = parseInt(b.getAttribute("data-zoom"), 10);
                var next = Math.max(40, Math.min(200, (parseInt(r.value, 10) || 100) + step));
                if (next === parseInt(r.value, 10)) { return; }
                r.value = next;
                r.dispatchEvent(new Event("input"));
            });
        });
    }

    // =============================================================================================
    // KEYBOARD
    // =============================================================================================
    function wireKeyboard() {
        document.addEventListener("keydown", function (ev) {
            var tag = (ev.target.tagName || "").toLowerCase();
            if (tag === "input" || tag === "textarea" || tag === "select") { return; }

            var step = ev.shiftKey ? (S.layout.gridMm || 1) : 1;

            if (ev.key === "Delete" || ev.key === "Backspace") { ev.preventDefault(); deleteSelection(); return; }
            if (ev.key === "ArrowLeft") { ev.preventDefault(); nudge(-step * rtlSign(), 0); return; }
            if (ev.key === "ArrowRight") { ev.preventDefault(); nudge(step * rtlSign(), 0); return; }
            if (ev.key === "ArrowUp") { ev.preventDefault(); nudge(0, -step); return; }
            if (ev.key === "ArrowDown") { ev.preventDefault(); nudge(0, step); return; }
            if (ev.key === "Escape") { S.selection = []; renderCanvas(); renderProps(); return; }

            if (ev.ctrlKey || ev.metaKey) {
                var k = ev.key.toLowerCase();
                if (k === "z") { ev.preventDefault(); undo(); }
                else if (k === "y") { ev.preventDefault(); redo(); }
                else if (k === "c") { ev.preventDefault(); copySelection(); }
                else if (k === "v") { ev.preventDefault(); pasteClipboard(); }
                else if (k === "d") { ev.preventDefault(); duplicateSelection(); }
                else if (k === "a") {
                    ev.preventDefault();
                    var b = band(S.selectedBand);
                    S.selection = b ? b.elements.map(function (e) { return e.id; }) : [];
                    renderCanvas(); renderProps();
                }
            }
        });
    }

    // =============================================================================================
    // BOOT
    // =============================================================================================
    function boot() {
        if (!$("cbd-toolbox")) { return; }   // the no-datasets state renders no designer

        renderToolbox();
        syncPageInputs();
        wirePageInputs();
        wireKeyboard();
        renderAll();
        loadAssets();

        $("cbd-dataset").addEventListener("change", function () { loadDataset($("cbd-dataset").value); });

        $("cbd-new").addEventListener("click", newReport);
        $("cbd-clear").addEventListener("click", clearPage);

        $("cbd-undo").addEventListener("click", undo);
        $("cbd-redo").addEventListener("click", redo);
        $("cbd-del").addEventListener("click", deleteSelection);
        $("cbd-dup").addEventListener("click", duplicateSelection);
        $("cbd-front").addEventListener("click", function () { zOrder(true); });
        $("cbd-back").addEventListener("click", function () { zOrder(false); });

        $("cbd-preview").addEventListener("click", doPreview);
        $("cbd-print").addEventListener("click", doPrint);
        $("cbd-pdf").addEventListener("click", function () { doDownload("/api/reports/studio/pdf", draft()); });
        $("cbd-save").addEventListener("click", doSave);

        Array.prototype.forEach.call(document.querySelectorAll("[data-export]"), function (b) {
            b.addEventListener("click", function () {
                doDownload("/api/reports/studio/export?format=" + b.getAttribute("data-export"), draft());
            });
        });

        // ---- THE DOCK BAR --------------------------------------------------------------------
        //
        // ONE panel open at a time — a tab strip whose tabs all stay open at once is just two columns
        // again. Each panel's own state is remembered per browser, because a toolbox that closes itself
        // on every visit is a toolbox nobody uses twice; the TOOLS panel therefore defaults to OPEN, or
        // a first-time designer opens a canvas with no visible way to add anything to it.
        //
        // localStorage can throw outright (a private window, site data blocked), so every touch of it is
        // wrapped: the designer must not fail to load over a remembered tab state.
        var DOCKS = [
            { tab: "cbd-tools-tab", panel: "cbd-tools-panel", close: "cbd-tools-close",
              key: "cbd-tools-open", autohide: false, dflt: "1" },
            { tab: "cbd-saved-tab", panel: "cbd-saved-panel", close: "cbd-saved-close",
              key: "cbd-saved-open", autohide: true, dflt: "0" }
        ];

        function dockSet(d, open) {
            var panel = $(d.panel), tab = $(d.tab);
            if (!panel || !tab) { return; }
            panel.hidden = !open;
            tab.classList.toggle("on", open);
            tab.setAttribute("aria-expanded", open ? "true" : "false");
            try { localStorage.setItem(d.key, open ? "1" : "0"); } catch (ignored) { }
            if (open) {
                DOCKS.forEach(function (other) { if (other !== d) { dockSet(other, false); } });
            }
        }
        function dockByPanel(id) {
            for (var i = 0; i < DOCKS.length; i++) { if (DOCKS[i].panel === id) { return DOCKS[i]; } }
            return null;
        }

        DOCKS.forEach(function (d) {
            var tab = $(d.tab), panel = $(d.panel);
            if (!tab || !panel) { return; }
            tab.addEventListener("click", function () { dockSet(d, panel.hidden); });
            if ($(d.close)) { $(d.close).addEventListener("click", function () { dockSet(d, false); }); }

            // The default is per SCREEN, not absolute: on a phone every panel is full width, so a
            // toolbox that opens by default pushes the canvas a third of a page down before anything
            // can be seen. A remembered choice always wins over the default, either way.
            var dflt = (window.innerWidth < 768) ? "0" : d.dflt;
            var remembered = null;
            try { remembered = localStorage.getItem(d.key); } catch (ignored) { }
            dockSet(d, (remembered === null ? dflt : remembered) === "1");
        });

        // CLICK AWAY dismisses the panels that ask for it — the reports list, not the toolbox. mousedown
        // rather than click, so it is gone before the canvas acts on the same press, and nothing is
        // prevented, so that press still lands where it was aimed.
        document.addEventListener("mousedown", function (ev) {
            if (ev.target.closest && ev.target.closest(".cbd-dockbar")) { return; }
            DOCKS.forEach(function (d) {
                var panel = $(d.panel);
                if (d.autohide && panel && !panel.hidden) { dockSet(d, false); }
            });
        });

        Array.prototype.forEach.call(document.querySelectorAll("[data-open]"), function (b) {
            b.addEventListener("click", function () {
                doOpen(parseInt(b.getAttribute("data-open"), 10));
                var d = dockByPanel("cbd-saved-panel");
                if (d) { dockSet(d, false); }   // the picker's job is done the moment a report is chosen
            });
        });

        $("cbd-add-filter").addEventListener("click", function () {
            var f = S.fields.filter(function (x) { return x.filterable; })[0];
            if (!f) { return; }
            S.filters.push({ field: f.key, operator: f.operators.length ? f.operators[0].operator : 0, values: [""] });
            renderFilters();
        });
        $("cbd-add-sort").addEventListener("click", function () {
            var f = S.fields.filter(function (x) { return x.sortable; })[0];
            if (!f) { return; }
            S.sorts.push({ field: f.key, descending: false });
            renderSorts();
        });

        // Upload: BYTES go up, an ID comes back. No path is sent and none could be.
        $("cbd-asset-file").addEventListener("change", function () {
            var file = $("cbd-asset-file").files[0];
            if (!file) { return; }
            var fd = new FormData();
            fd.append("file", file);
            fd.append("role", $("cbd-asset-role").value);
            fd.append("title", file.name);

            // r.text() THEN parse, never r.json() straight — and a .catch at the end.
            //
            // The first version read r.json() directly with no catch, and that is how "I pick a file and
            // nothing happens" is built: a refusal (404 from the author gate, 400 from anti-forgery, 500 from
            // anything) answers with a body that is not JSON, r.json() rejects, the rejection is unhandled,
            // and the screen shows no message of any kind. A failed upload must SAY it failed.
            fetch("/api/reports/studio/assets", {
                method: "POST",
                headers: { "RequestVerificationToken": token() },
                body: fd
            })
                .then(function (r) {
                    return r.text().then(function (t) {
                        var d = null;
                        try { d = t ? JSON.parse(t) : null; } catch (ignored) { d = null; }
                        return { ok: r.ok, status: r.status, d: d };
                    });
                })
                .then(function (res) {
                    $("cbd-asset-file").value = "";
                    if (!res.ok || !res.d || !res.d.asset) {
                        S.assetTarget = null;
                        showErrors((res.d && res.d.errors && res.d.errors.length ? res.d.errors : null)
                            || [(AR ? "تعذّر رفع الصورة (HTTP " : "The image could not be uploaded (HTTP ") + res.status + ")"]);
                        return;
                    }
                    showErrors(null);
                    S.assets.unshift(res.d.asset);

                    // An upload started from an element's own Picture row places the image on that element.
                    // Uploading and then having to pick the same file out of a list is a step nobody wants.
                    var target = S.assetTarget ? elementById(S.assetTarget) : null;
                    S.assetTarget = null;
                    if (target) {
                        snapshot();
                        target.e.assetId = res.d.asset.id;
                        renderAll();
                        renderAssets();
                        return;
                    }

                    renderAssets(); renderProps();
                })
                .catch(function (err) {
                    $("cbd-asset-file").value = "";
                    S.assetTarget = null;
                    showErrors([(AR ? "تعذّر رفع الصورة: " : "The image could not be uploaded: ") +
                                ((err && err.message) ? err.message : String(err))]);
                });
        });

    // A report opened from a link. The view resolved and re-validated it SERVER-SIDE; null means
    // there was nothing to open, or nothing this caller may open - the two are one answer.
    if (CBD.openDraft) applyDraft(CBD.openDraft);

        // LAST, deliberately: the draft above can change the paper size, and fitting the previous one
        // would be fitting the wrong sheet.
        fitIfSheetOverflows();
    }

    if (document.readyState === "loading") { document.addEventListener("DOMContentLoaded", boot); }
    else { boot(); }
})();
