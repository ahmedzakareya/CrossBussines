# -*- coding: utf-8 -*-
"""Screen, field and action inventory for the CrossBuy user manual.

READ-ONLY. Walks the Razor views, resolves every localizer key against the real .resx files in all
three cultures, and records what a manual writer needs per screen: exact bilingual titles, the
route, filters, table columns, form fields, actions, empty states and messages.

Writes into docs/user-manual-discovery/:
    screen-catalog.json         one entry per user-facing screen
    field-action-catalog.json   fields and actions, per screen
    bilingual-glossary.csv      every localizer key with ar / en / fr and where it is used
    partials-catalog.json       shared partials (dialogs, panels) that screens include

Nothing is executed and nothing outside the output folder is written.
"""
import io, os, re, json, csv, collections

ROOT = r'C:\CrossBuy\CrossBuy'
APP = os.path.join(ROOT, 'CrossBuy')
VIEWS = os.path.join(APP, 'Views')
RES = os.path.join(APP, 'Resources')
CTRL = os.path.join(APP, 'Controllers')
OUT = os.path.join(ROOT, 'docs', 'user-manual-discovery')

CULTURES = ('ar', 'en', 'fr')


def read(p):
    try:
        return io.open(p, encoding='utf-8', errors='replace').read()
    except OSError:
        return ''


# ---------------------------------------------------------------------------- resource resolution
def load_resx(path):
    """name -> value, for one .resx file."""
    if not os.path.exists(path):
        return {}
    t = read(path)
    out = {}
    for m in re.finditer(r'<data name="([^"]+)"[^>]*>\s*<value>(.*?)</value>', t, re.S):
        out[m.group(1)] = re.sub(r'\s+', ' ', m.group(2)).strip()
    return out


SHARED = {c: load_resx(os.path.join(RES, 'SharedResources.%s.resx' % c)) for c in CULTURES}
SHARED['neutral'] = load_resx(os.path.join(RES, 'SharedResources.resx'))

_view_res_cache = {}


def view_resx(folder, view):
    """The per-screen resource set: Resources/Views/<Folder>/<View>.<culture>.resx"""
    key = (folder, view)
    if key in _view_res_cache:
        return _view_res_cache[key]
    base = os.path.join(RES, 'Views', folder)
    got = {c: load_resx(os.path.join(base, '%s.%s.resx' % (view, c))) for c in CULTURES}
    _view_res_cache[key] = got
    return got


def resolve(key, folder, view, shared_only=False):
    """Resolve a localizer key to ar/en/fr.

    IViewLocalizer falls back to the KEY when a culture has no entry — which is why a missing
    French string surfaces as English rather than as an error. That fallback is reproduced here so
    the catalogue shows what a user would actually read, and `source` records which it was.
    """
    out = {}
    vr = {} if shared_only else view_resx(folder, view)
    for c in CULTURES:
        v = None
        src = None
        if not shared_only and vr.get(c, {}).get(key):
            v, src = vr[c][key], 'view'
        elif SHARED.get(c, {}).get(key):
            v, src = SHARED[c][key], 'shared'
        if v is None:
            v, src = key, 'fallback-to-key'
        out[c] = v
        out[c + '_source'] = src
    return out


# ---------------------------------------------------------------------------- controller actions
def controller_index():
    """controller -> { action -> {verbs, attributes, line} }, read from Controllers/*.cs."""
    idx = collections.defaultdict(dict)
    files = []
    for base in (CTRL, os.path.join(CTRL, 'Api')):
        if not os.path.isdir(base):
            continue
        for fn in sorted(os.listdir(base)):
            if fn.endswith('.cs') and os.path.isfile(os.path.join(base, fn)):
                files.append(os.path.join(base, fn))

    for p in files:
        t = read(p)
        fn = os.path.basename(p)
        # AdminController.Recruitment.cs and friends are partial classes of one controller.
        name = fn[:-3].split('.')[0]
        if name.endswith('Controller'):
            name = name[:-len('Controller')]
        lines = t.split('\n')
        pending = []
        for i, line in enumerate(lines):
            s = line.strip()
            # ATTRIBUTES AND THE METHOD SHARE A LINE in most of this codebase:
            #     [HttpGet] public async Task<IActionResult> Index()
            # Treating a line that merely STARTS with '[' as attribute-only skipped the method and
            # lost 98 of 309 screens on the first run. So the leading attributes are peeled off and
            # whatever follows is still examined as a declaration.
            inline = re.match(r'((?:\[[^\]]*\]\s*)+)(.*)$', s)
            if inline:
                pending.extend(a.strip() for a in re.findall(r'\[([^\]]+)\]', inline.group(1)))
                s = inline.group(2).strip()
                if not s:
                    continue
            m = re.search(r'public\s+(?:static\s+)?(?:async\s+)?(?:Task<)?[A-Za-z<>\[\]?]*\s+(\w+)\s*\(', s)
            if m and ('ActionResult' in s or 'IActionResult' in s or 'Task<' in s):
                act = m.group(1)
                verbs = [a for a in pending if a.startswith('Http')]
                attrs = [a for a in pending if not a.startswith('Http')]
                prev = idx[name].get(act)
                if prev:
                    prev['verbs'] = sorted(set(prev['verbs'] + verbs))
                    prev['attributes'] = sorted(set(prev['attributes'] + attrs))
                else:
                    idx[name][act] = {
                        'verbs': verbs or ['HttpGet (implicit)'],
                        'attributes': attrs,
                        'file': os.path.relpath(p, ROOT).replace('\\', '/'),
                        'line': i + 1,
                    }
                pending = []
            elif s and not s.startswith('//') and not s.startswith('['):
                pending = []
    return idx


CONTROLLERS = controller_index()

# Class-level attributes (e.g. [Authorize] on the controller) apply to every action in it.
CLASS_ATTRS = {}
for base in (CTRL, os.path.join(CTRL, 'Api')):
    if not os.path.isdir(base):
        continue
    for fn in sorted(os.listdir(base)):
        p = os.path.join(base, fn)
        if not (fn.endswith('.cs') and os.path.isfile(p)):
            continue
        t = read(p)
        nm = fn[:-3].split('.')[0]
        if nm.endswith('Controller'):
            nm = nm[:-len('Controller')]
        m = re.search(r'((?:\[[^\]]+\]\s*)+)public\s+(?:sealed\s+|partial\s+|abstract\s+)*class\s+\w*Controller', t)
        if m:
            CLASS_ATTRS.setdefault(nm, set()).update(
                a.strip('[] ') for a in re.findall(r'\[([^\]]+)\]', m.group(1)))


# Which action returns View("SomeOtherName") — so a view whose file name differs from its action
# is still traceable to the route that opens it.
VIEW_CALL_RX = re.compile(r'(?:Partial)?View\(\s*"([^"]+)"')
DECL_RX = re.compile(r'public\s+(?:static\s+)?(?:async\s+)?(?:Task<)?[A-Za-z<>\[\]?]*\s+(\w+)\s*\(')
SIMPLE_NAME_RX = re.compile(r'[A-Za-z0-9_]+')

RENDERED_BY = collections.defaultdict(set)
for base in (CTRL, os.path.join(CTRL, 'Api')):
    if not os.path.isdir(base):
        continue
    for fn in sorted(os.listdir(base)):
        p_ = os.path.join(base, fn)
        if not (fn.endswith('.cs') and os.path.isfile(p_)):
            continue
        nm = fn[:-3].split('.')[0]
        if nm.endswith('Controller'):
            nm = nm[:-len('Controller')]
        body = read(p_)
        # TWO FORMS, and missing the second one stranded every Hyper screen:
        #     View("CategoryForm", model)                   - a view in this controller's folder
        #     View("~/Views/Hyper/PosLogin.cshtml", model)  - an ABSOLUTE view path, which is how
        #                                                      HyperPosController renders screens
        #                                                      that live under Views/Hyper
        for mm in VIEW_CALL_RX.finditer(body):
            target = mm.group(1)
            head = body[:mm.start()]
            dm = None
            for d in DECL_RX.finditer(head):
                dm = d
            if not dm:
                continue
            action = '%s.%s' % (nm, dm.group(1))
            if target.startswith('~/Views/'):
                rel = target[len('~/Views/'):]
                if rel.lower().endswith('.cshtml'):
                    rel = rel[:-len('.cshtml')]
                parts = rel.split('/')
                RENDERED_BY[('/'.join(parts[:-1]), parts[-1])].add(action)
            elif SIMPLE_NAME_RX.fullmatch(target):
                RENDERED_BY[(nm, target)].add(action)


# ---------------------------------------------------------------------------- view parsing
KEY_RX = re.compile(r'(?:Localizer|SR|SharedLocalizer|L)\["((?:[^"\\]|\\.)*)"\]')
SHARED_KEY_RX = re.compile(r'\bSR\["((?:[^"\\]|\\.)*)"\]')
RES_PROP_RX = re.compile(r'SharedResources\.(\w+)')


def strip_razor_comments(t):
    return re.sub(r'@\*.*?\*@', ' ', t, flags=re.S)


def text_of(frag):
    """Human text from a Razor fragment: keep localizer keys, drop markup and code."""
    frag = re.sub(r'<[^>]*>', ' ', frag)
    # Razor expressions in all the shapes that appear here. The parenthesised form has to go
    # first, or `@(` leaves a bare bracket behind — which is how 115 actions ended up labelled "[".
    frag = re.sub(r'@\([^)]*\)?', ' ', frag)
    frag = re.sub(r'@\w+(?:\.\w+)*(?:\([^)]*\))?(?:\[[^\]]*\])?', ' ', frag)
    # A blanket `{…}` strip was tried here and removed: it ate the body of 132 in-page notices
    # whose text legitimately contains braces, and a notice the user reads is exactly what the
    # troubleshooting chapter is made of.
    frag = re.sub(r'\s+', ' ', frag).strip()
    # A label made only of punctuation or a stray bracket is not a label.
    if frag and not re.search(r'[0-9A-Za-z؀-ۿ]', frag):
        return ''
    return frag


def keys_in(frag):
    return [m.group(1) for m in KEY_RX.finditer(frag)] + \
           [m.group(1) for m in RES_PROP_RX.finditer(frag)]


# The culture test is spelled four ways across the view tree — `isAr` (the Inventory convention),
# `arabic` (175 uses, mostly Accounting and Admin), `isArabic`, and a bare `ar`. Matching only the
# first left 175 labels reading "@(arabic ?" in the catalogue.
TERNARY_RX = re.compile(r'\b(?:isAr|isArabic|arabic|ar)\s*\?\s*"([^"]*)"\s*:\s*"([^"]*)"')


def label_for(frag, folder, view):
    """Best bilingual label for a fragment: the first localizer key, else the literal text."""
    # Several screens carry their heading as an inline culture test rather than a resource:
    #     @(isAr ? "الهايبر ماركت" : "Hypermarket")
    # That is already bilingual, so read both sides instead of reporting the Razor expression.
    tm = TERNARY_RX.search(frag)
    if tm:
        return {'key': None, 'ar': tm.group(1), 'en': tm.group(2), 'fr': tm.group(2),
                'ar_source': 'inline-ternary-in-view', 'en_source': 'inline-ternary-in-view'}
    ks = keys_in(frag)
    if ks:
        r = resolve(ks[0], folder, view, shared_only=bool(SHARED_KEY_RX.search(frag)))
        return {'key': ks[0], 'ar': r['ar'], 'en': r['en'], 'fr': r['fr'],
                'ar_source': r['ar_source'], 'en_source': r['en_source']}
    lit = text_of(frag)
    if lit:
        return {'key': None, 'ar': lit, 'en': lit, 'fr': lit,
                'ar_source': 'literal-in-view', 'en_source': 'literal-in-view'}
    return None


BTN_RX = re.compile(r'<(button|a)\b([^>]*\bclass="[^"]*\bbtn\b[^"]*")([^>]*)>(.*?)</\1>', re.S | re.I)
TH_RX = re.compile(r'<th\b([^>]*)>(.*?)</th>', re.S | re.I)
LABEL_RX = re.compile(r'<label\b([^>]*)>(.*?)</label>', re.S | re.I)
INPUT_RX = re.compile(r'<(input|select|textarea)\b([^>]*?)/?>', re.S | re.I)
MODAL_RX = re.compile(r'id="([A-Za-z0-9_\-]*(?:modal|Modal)[A-Za-z0-9_\-]*)"')
ALERT_RX = re.compile(r'<div\b[^>]*class="[^"]*\balert\b([^"]*)"[^>]*>(.*?)</div>', re.S | re.I)


def attr(s, name):
    m = re.search(r'\b%s\s*=\s*"([^"]*)"' % name, s, re.I)
    return m.group(1) if m else None


def parse_screen(path, folder, view):
    raw = read(path)
    t = strip_razor_comments(raw)

    layout = attr(t, 'Layout') or (re.search(r'Layout\s*=\s*"([^"]+)"', t).group(1)
                                   if re.search(r'Layout\s*=\s*"([^"]+)"', t) else None)

    # --- heading: the <h1>/page-heading the user reads at the top of the screen
    heading = None
    hm = re.search(r'<h1\b[^>]*>(.*?)</h1>', t, re.S | re.I)
    if not hm:
        hm = re.search(r'class="[^"]*page-heading[^"]*"[^>]*>(.*?)<', t, re.S | re.I)
    if hm:
        heading = label_for(hm.group(1), folder, view)

    # --- breadcrumb trail
    crumbs = []
    bm = re.search(r'<ul\b[^>]*class="[^"]*breadcrumb[^"]*"[^>]*>(.*?)</ul>', t, re.S | re.I)
    if bm:
        for li in re.findall(r'<li\b[^>]*>(.*?)</li>', bm.group(1), re.S | re.I):
            lab = label_for(li, folder, view)
            if lab and lab['en'].strip():
                crumbs.append(lab)

    # --- table columns
    columns = []
    for m in TH_RX.finditer(t):
        lab = label_for(m.group(2), folder, view)
        if lab and lab['en'].strip() and len(lab['en']) < 60:
            columns.append({'label': lab, 'align': 'end' if 'text-end' in (m.group(1) or '') else 'start'})

    # --- filters and form fields
    fields = []
    seen_ids = set()
    for m in INPUT_RX.finditer(t):
        tag, a = m.group(1).lower(), m.group(2)
        fid = attr(a, 'id') or attr(a, 'name') or attr(a, 'asp-for')
        if not fid or fid in seen_ids:
            continue
        # A hidden anti-forgery field is not a user-facing field.
        itype = (attr(a, 'type') or ('select' if tag == 'select' else 'textarea' if tag == 'textarea' else 'text')).lower()
        if itype == 'hidden':
            continue
        seen_ids.add(fid)
        # the nearest preceding <label> is this field's caption
        # FINDING A FIELD'S CAPTION, in the order that is actually correct:
        #
        #   1. <label for="<id>"> wherever it is — an explicit binding beats any proximity guess.
        #   2. For a checkbox or radio, the label that FOLLOWS it. This is the standard Bootstrap
        #      form-check order (<input><label>), and taking the preceding label instead shifted
        #      every caption by one: TrackExpiry was captioned "Track batch" and TrackSerial
        #      "Track expiry" — wrong labels on the two fields that decide whether stock is batch-
        #      or serial-tracked, which is exactly the kind of error a manual must not carry.
        #   3. Otherwise the nearest PRECEDING label, and only if it is close. Without the
        #      distance cap the last label on the page was attached to every unlabelled control
        #      after it.
        caption = None
        explicit = re.search(r'<label\b[^>]*\bfor\s*=\s*"%s"[^>]*>(.*?)</label>'
                             % re.escape(fid), t, re.S | re.I)
        if explicit:
            caption = label_for(explicit.group(1), folder, view)
        elif itype in ('checkbox', 'radio'):
            # The Metronic form-check puts the caption in a SPAN after the input, inside a <label>
            # that opened before it:
            #   <label class="form-check…"><input type="checkbox" …/><span
            #      class="form-check-label">@Localizer["Track batch"]</span></label>
            # So `before` is truncated mid-open-tag and the last COMPLETE <label> in it belongs to
            # the previous field — which is what shifted every caption by one. Read the span.
            after = t[m.end():m.end() + 400]
            am = (re.search(r'<span\b[^>]*form-check-label[^>]*>(.*?)</span>', after, re.S | re.I)
                  or re.search(r'<span\b[^>]*>(.*?)</span>', after, re.S | re.I)
                  or LABEL_RX.search(after))
            if am:
                caption = label_for(am.group(1) if am.re.groups == 1 else am.group(2),
                                    folder, view)
        if caption is None:
            before = t[:m.start()]
            lm = None
            for lm2 in LABEL_RX.finditer(before):
                lm = lm2
            if lm and (m.start() - lm.end()) <= 400:
                caption = label_for(lm.group(2), folder, view)
        ph = attr(a, 'placeholder')
        fields.append({
            'id': fid,
            'control': tag,
            'type': itype,
            'label': caption,
            'placeholder': label_for(ph, folder, view) if ph and '@' in ph else (
                {'key': None, 'ar': ph, 'en': ph, 'fr': ph, 'ar_source': 'literal-in-view',
                 'en_source': 'literal-in-view'} if ph else None),
            'required': bool(re.search(r'\brequired\b', a, re.I)),
            'readonly': bool(re.search(r'\breadonly\b', a, re.I)),
            'disabled': bool(re.search(r'\bdisabled\b', a, re.I)),
            'min': attr(a, 'min'), 'max': attr(a, 'max'), 'step': attr(a, 'step'),
            'maxlength': attr(a, 'maxlength'),
            'lookup': attr(a, 'data-control'),
            'model_binding': attr(a, 'asp-for'),
        })

    # --- actions (buttons and button-styled links)
    actions = []
    for m in BTN_RX.finditer(t):
        inner = m.group(4)
        allattr = (m.group(2) or '') + (m.group(3) or '')
        lab = label_for(inner, folder, view)
        title = attr(allattr, 'title')
        if (not lab or not lab['en'].strip()) and title:
            lab = label_for(title, folder, view)
        if not lab or not lab['en'].strip():
            # icon-only button: record it, because a manual must still name it
            icon = re.search(r'ki-(?:outline|solid)\s+ki-([a-z0-9\-]+)', inner)
            lab = {'key': None, 'ar': '(أيقونة: %s)' % (icon.group(1) if icon else '؟'),
                   'en': '(icon only: %s)' % (icon.group(1) if icon else '?'),
                   'fr': '(icone)', 'ar_source': 'icon-only', 'en_source': 'icon-only'}
        cls = attr(allattr, 'class') or ''
        variant = 'primary' if 'btn-primary' in cls else \
                  'danger' if 'btn-danger' in cls or 'btn-light-danger' in cls else \
                  'success' if 'btn-success' in cls or 'btn-light-success' in cls else \
                  'neutral'
        actions.append({
            'label': lab,
            'element': m.group(1).lower(),
            'variant': variant,
            'href': attr(allattr, 'href'),
            'id': attr(allattr, 'id'),
            'opens_modal': attr(allattr, 'data-bs-target'),
            'submits': 'type="submit"' in allattr.lower(),
            'confirm': bool(re.search(r'confirm\s*\(', allattr, re.I)),
        })

    # --- notices the user may see
    notices = []
    for m in ALERT_RX.finditer(t):
        kind = 'warning' if 'warning' in m.group(1) else 'danger' if 'danger' in m.group(1) else \
               'success' if 'success' in m.group(1) else 'info'
        lab = label_for(m.group(2), folder, view)
        if lab and lab['en'].strip():
            notices.append({'kind': kind, 'message': lab})

    # empty / loading states, which a manual should show
    empties = []
    for m in re.finditer(r'id="(empty\w*|loading\w*|noData\w*|emptyRow|loadingRow)"[^>]*>(.*?)</', t, re.S | re.I):
        lab = label_for(m.group(2), folder, view)
        if lab and lab['en'].strip():
            empties.append({'id': m.group(1), 'message': lab})

    modals = sorted(set(MODAL_RX.findall(t)))
    partials = sorted(set(re.findall(r'Partial(?:Async)?\("([^"]+)"', t) +
                          re.findall(r'<partial\s+name="([^"]+)"', t)))
    components = sorted(set(re.findall(r'Component\.InvokeAsync\("([^"]+)"', t)))

    # permission hooks a view itself evaluates
    perm_hooks = sorted(set(re.findall(r'CanAsync\("([^"]+)"\)', t)))

    return {
        'layout': layout, 'heading': heading, 'breadcrumb': crumbs,
        'columns': columns, 'fields': fields, 'actions': actions,
        'notices': notices, 'empty_states': empties,
        'modals': modals, 'partials': partials, 'view_components': components,
        'view_permission_hooks': perm_hooks,
        'lines': raw.count('\n') + 1,
        'has_form': '<form' in t.lower(),
        'is_ajax_driven': 'fetch(' in t or '$.ajax' in t or '$.get' in t or '$.post' in t,
    }


# ---------------------------------------------------------------------------- walk
screens, partials_out = [], []
glossary = collections.defaultdict(lambda: {'uses': set()})

for dirpath, _d, filenames in os.walk(VIEWS):
    rel_folder = os.path.relpath(dirpath, VIEWS).replace('\\', '/')
    if rel_folder == '.':
        rel_folder = ''
    for fn in sorted(filenames):
        if not fn.endswith('.cshtml'):
            continue
        view = fn[:-len('.cshtml')]
        path = os.path.join(dirpath, fn)
        folder = rel_folder.split('/')[0] if rel_folder else ''
        parsed = parse_screen(path, rel_folder, view)
        src = os.path.relpath(path, ROOT).replace('\\', '/')

        # glossary harvest
        raw = strip_razor_comments(read(path))
        shared_keys = set(SHARED_KEY_RX.findall(raw))
        for k in set(keys_in(raw)):
            r = resolve(k, rel_folder, view, shared_only=k in shared_keys)
            g = glossary[k]
            g.update({'ar': r['ar'], 'en': r['en'], 'fr': r['fr'],
                      'ar_source': r['ar_source'], 'en_source': r['en_source'],
                      'fr_source': r['fr_source']})
            g['uses'].add(src)

        if view.startswith('_') or view.startswith('Components'):
            partials_out.append({'id': 'PARTIAL-%s-%s' % (folder or 'Shared', view.lstrip('_')),
                                 'folder': folder or 'Shared', 'name': view, 'source': src,
                                 **{k: parsed[k] for k in
                                    ('heading', 'fields', 'actions', 'columns', 'notices', 'lines')}})
            continue

        ctrl = folder
        act = CONTROLLERS.get(ctrl, {}).get(view)
        rendered_by = None
        if not act:
            # A view whose name differs from its action — HyperController.Sales() returns
            # View("HyperSales"). Without this the screen looks orphaned when it is perfectly live.
            rendered_by = sorted(set(RENDERED_BY.get((ctrl, view), ())))
        route = '/%s/%s' % (ctrl, view) if ctrl else '/%s' % view
        # The id must use the FULL folder path: Views/Shared/Default.cshtml and
        # Views/Shared/Components/.../Default.cshtml both exist, and keying on the first path
        # segment alone produced two screens with the same id.
        id_folder = (rel_folder or 'ROOT').replace('/', '-').upper()
        screens.append({
            'screen_id': 'SCR-%s-%s' % (id_folder, view),
            'module_folder': folder or '(root)',
            'view_name': view,
            'route': route,
            'source_view': src,
            'controller': ctrl or None,
            'controller_action': view if act else None,
            'action_verbs': act['verbs'] if act else None,
            'action_attributes': (act['attributes'] if act else None),
            'class_attributes': sorted(CLASS_ATTRS.get(ctrl, ())) or None,
            'controller_source': ('%s:%d' % (act['file'], act['line'])) if act else None,
            'has_matching_action': bool(act),
            'rendered_by_actions': rendered_by or None,
            **parsed,
        })

screens.sort(key=lambda s: (s['module_folder'], s['view_name']))
partials_out.sort(key=lambda s: (s['folder'], s['name']))

# ---------------------------------------------------------------------------- outputs
io.open(os.path.join(OUT, 'screen-catalog.json'), 'w', encoding='utf-8').write(
    json.dumps({'generated': '2026-09-20', 'screen_count': len(screens), 'screens': screens},
               ensure_ascii=False, indent=1))

io.open(os.path.join(OUT, 'partials-catalog.json'), 'w', encoding='utf-8').write(
    json.dumps({'partial_count': len(partials_out), 'partials': partials_out},
               ensure_ascii=False, indent=1))

fa = []
for s in screens:
    for f in s['fields']:
        fa.append({'screen_id': s['screen_id'], 'route': s['route'], 'kind': 'field', **f})
    for a in s['actions']:
        fa.append({'screen_id': s['screen_id'], 'route': s['route'], 'kind': 'action', **a})
io.open(os.path.join(OUT, 'field-action-catalog.json'), 'w', encoding='utf-8').write(
    json.dumps({'entry_count': len(fa), 'entries': fa}, ensure_ascii=False, indent=1))

with io.open(os.path.join(OUT, 'bilingual-glossary.csv'), 'w', encoding='utf-8-sig', newline='') as fh:
    w = csv.writer(fh)
    w.writerow(['key', 'arabic', 'english', 'french', 'ar_source', 'en_source', 'fr_source',
                'translation_status', 'use_count', 'first_used_in'])
    for k in sorted(glossary):
        g = glossary[k]
        if g.get('ar_source') == 'fallback-to-key':
            status = 'MISSING arabic — key shown to user'
        elif g.get('en_source') == 'fallback-to-key' and g.get('ar_source') != 'fallback-to-key':
            status = 'english falls back to key text'
        elif g.get('fr_source') == 'fallback-to-key':
            status = 'MISSING french — English key shown'
        else:
            status = 'translated in all three'
        uses = sorted(g['uses'])
        w.writerow([k, g.get('ar', ''), g.get('en', ''), g.get('fr', ''),
                    g.get('ar_source', ''), g.get('en_source', ''), g.get('fr_source', ''),
                    status, len(uses), uses[0] if uses else ''])

print('screens          :', len(screens))
print('partials         :', len(partials_out))
print('fields           :', sum(len(s['fields']) for s in screens))
print('actions          :', sum(len(s['actions']) for s in screens))
print('table columns    :', sum(len(s['columns']) for s in screens))
print('notices          :', sum(len(s['notices']) for s in screens))
print('empty states     :', sum(len(s['empty_states']) for s in screens))
print('modals           :', sum(len(s['modals']) for s in screens))
print('glossary keys    :', len(glossary))
print('screens with no matching controller action :',
      sum(1 for s in screens if not s['has_matching_action']))
