# -*- coding: utf-8 -*-
"""Build screenshot-manifest.csv, coverage-and-gaps.csv and evidence-register.csv.

The manifest is built from the capture LOGS, not from the directory listing, so a PNG with no log
entry is reported as unexplained rather than silently counted — and a route that was attempted and
failed is present with its reason instead of being invisible.
"""
import io, os, re, csv, json, glob, collections

ROOT = r'C:\CrossBuy\CrossBuy'
OUT = os.path.join(ROOT, 'docs', 'user-manual-discovery')
SHOTS = os.path.join(OUT, 'screenshots')
SCRATCH_ROUTES = os.path.join(
    r'C:\Users\Lenovo\AppData\Local\Temp\claude\c--CrossBuy',
    '7d5e59b2-fa47-4fc5-945e-07614ef3105c', 'scratchpad', 'routes.json')

screens = json.load(io.open(os.path.join(OUT, 'screen-catalog.json'), encoding='utf-8'))['screens']
by_route = {s['route'].lower(): s for s in screens}
wf = json.load(io.open(os.path.join(OUT, 'workflow-catalog.json'), encoding='utf-8'))

# which workflow (if any) each screen belongs to — gives the manifest its "intended manual section"
screen_to_wf = collections.defaultdict(set)
for w in wf['workflows']:
    for st in w['steps']:
        if st['screen_id']:
            screen_to_wf[st['screen_id']].add(w['id'])

MODULE_SECTION = {
    'Account': 'Part 1 — Getting started', 'Portal': 'Part 1 — Getting started',
    'Home': 'Part 1 — Getting started', 'Workspace': 'Part 12 — Working together',
    'Service': 'Part 4 — Setting up', 'Admin': 'Part 4/11 — Setting up & People',
    'Brand': 'Part 4 — Setting up',
    'Accounting': 'Part 5 — Accounting', 'Currency': 'Part 5 — Accounting',
    'Inventory': 'Part 6 — Inventory and supply',
    'Pos': 'Part 8 — Restaurant and retail', 'PosApp': 'Part 8 — Restaurant and retail',
    'Hyper': 'Part 8 — Restaurant and retail',
    'RestaurantIntelligence': 'Part 8 — Restaurant and retail',
    'Project': 'Part 9 — Projects and contracting',
    'ProjectCloseout': 'Part 9 — Projects and contracting',
    'Crm': 'Part 10 — Customers and relationships',
    'People': 'Part 11 — People', 'EmployeeOnboarding': 'Part 11 — People',
    'Roster': 'Part 11 — People',
    'Tasks': 'Part 12 — Working together', 'Calendar': 'Part 12 — Working together',
    'Chat': 'Part 12 — Working together', 'Comm': 'Part 12 — Working together',
    'Announcements': 'Part 12 — Working together',
    'FileManager': 'Part 12 — Working together', 'Documents': 'Part 12 — Working together',
    'Notifications': 'Part 12 — Working together', 'Approvals': 'Part 12 — Working together',
    'Reports': 'Part 13 — Reports and analytics',
    'BusinessEventMonitor': 'Part 13 — Reports and analytics',
    'InsightActions': 'Part 14 — Intelligent features',
    'ClientPortal': 'Part 15 — Outside the company', 'Store': 'Part 15 — Outside the company',
    'Shared': '(framework — not a manual section)',
}


def slug(r):
    return re.sub(r'[^A-Za-z0-9]+', '-', r.lstrip('/')).lower()


# ---------------------------------------------------------------- 1. merge the capture logs
entries = {}          # (route, lang) -> record ; later logs win, which is what a retry means
for p in sorted(glob.glob(os.path.join(SHOTS, 'capture-log.*.json')),
                key=os.path.getmtime):
    try:
        rows = json.load(io.open(p, encoding='utf-8'))
    except (ValueError, OSError):
        continue
    for r in rows:
        if not isinstance(r, dict) or 'route' not in r:
            continue
        key = (r['route'], r.get('lang', 'en'))
        prev = entries.get(key)
        # never let a failed retry overwrite an earlier success
        if prev and prev.get('captured') and not r.get('captured'):
            continue
        entries[key] = r

on_disk = {f for f in os.listdir(SHOTS) if f.endswith('.png')}

rows = []
for (route, lang), r in sorted(entries.items(), key=lambda kv: (kv[0][0] or '', kv[0][1] or '')):
    s = by_route.get(route.lower())
    fn = r.get('file') or '%s.%s.png' % (slug(route), lang)
    exists = fn in on_disk
    sid = s['screen_id'] if s else None
    flags = r.get('privacyFlags') or []
    if not exists:
        treatment = 'not captured'
    elif flags:
        treatment = 'development data; automatic scan flagged: %s — REVIEW BEFORE PUBLICATION' % ','.join(flags)
    else:
        treatment = ('development data; no e-mail, phone, IBAN or 14-digit id detected. '
                     'The signed-in user name and avatar are visible in the header.')
    # THE FILE NAME IS AUTHORITATIVE for language. A handful of log rows carried a `lang` from an
    # earlier run whose log was reused, which made the manifest disagree with the directory by
    # five files. The suffix is what the capture actually wrote.
    if exists:
        lang = 'ar' if fn.endswith('.ar.png') else 'en'
    rows.append({
        'screen_id': sid or '(no catalogue entry)',
        'language': lang,
        'filename': fn if exists else '',
        'route': route,
        'final_url': r.get('finalUrl', ''),
        'role': 'development administrator (single session)',
        'state': r.get('state', 'unknown'),
        'http_status': r.get('status', ''),
        'page_title': r.get('title', ''),
        'heading': r.get('heading', '') or '',
        'html_lang': r.get('lang', ''),
        'direction': r.get('dir', ''),
        'inputs': r.get('inputs', ''), 'buttons': r.get('buttons', ''),
        'table_rows': r.get('rows', ''), 'modals': r.get('modals', ''),
        'reauthenticated': 'yes' if r.get('reauthenticated') else 'no',
        'dev_overlay_visible': 'yes' if r.get('devOverlay') else 'no',
        'capture_date': '2026-09-20',
        'viewport': '1440x900',
        'privacy_treatment': treatment,
        'verification_level': ('runtime viewed (screen renders); no action performed'
                               if exists else 'not verified — capture failed'),
        'intended_manual_section': MODULE_SECTION.get(s['module_folder'] if s else '', '(unassigned)'),
        'workflows': '; '.join(sorted(screen_to_wf.get(sid, ()))),
        'capture_error': (r.get('error') or '')[:160],
    })

# PNGs with no log row. A retry that reused a log tag overwrote the earlier log, so a handful of
# genuine captures can lose their entry. The file name is deterministic, so the route is recovered
# from the route list by slug before anything is called unexplained.
slug_to_route = {slug(r): r for r in
                 json.load(io.open(os.path.join(SCRATCH_ROUTES), encoding='utf-8'))} \
    if os.path.exists(SCRATCH_ROUTES) else {}

logged = {r['filename'] for r in rows if r['filename']}
for f in sorted(on_disk - logged):
    lang = 'ar' if f.endswith('.ar.png') else 'en'
    base = f[:-len('.%s.png' % lang)]
    route = slug_to_route.get(base)
    if route:
        s = by_route.get(route.lower())
        sid = s['screen_id'] if s else '(no catalogue entry)'
        rows.append({
            'screen_id': sid, 'language': lang, 'filename': f, 'route': route,
            'final_url': '', 'role': 'development administrator (single session)',
            'state': 'captured; its capture log was overwritten by a later retry',
            'http_status': '', 'page_title': '', 'heading': '', 'html_lang': '', 'direction': '',
            'inputs': '', 'buttons': '', 'table_rows': '', 'modals': '',
            'reauthenticated': '', 'dev_overlay_visible': 'no', 'capture_date': '2026-09-20',
            'viewport': '1440x900',
            'privacy_treatment': 'development data; NOT scanned (log lost) — REVIEW BEFORE PUBLICATION',
            'verification_level': 'runtime viewed (screen renders); no action performed',
            'intended_manual_section': MODULE_SECTION.get(s['module_folder'] if s else '', '(unassigned)'),
            'workflows': '; '.join(sorted(screen_to_wf.get(sid, ()))),
            'capture_error': '',
        })
        continue
    rows.append({
        'screen_id': '(unmatched)', 'language': lang,
        'filename': f, 'route': '', 'final_url': '', 'role': '',
        'state': 'PNG PRESENT WITH NO LOG ENTRY — provenance unknown, do not publish',
        'http_status': '', 'page_title': '', 'heading': '', 'html_lang': '', 'direction': '',
        'inputs': '', 'buttons': '', 'table_rows': '', 'modals': '',
        'reauthenticated': '', 'dev_overlay_visible': '', 'capture_date': '2026-09-20',
        'viewport': '1440x900', 'privacy_treatment': 'unknown',
        'verification_level': 'not verified', 'intended_manual_section': '', 'workflows': '',
        'capture_error': '',
    })

FIELDS = ['screen_id', 'language', 'filename', 'route', 'final_url', 'role', 'state',
          'http_status', 'page_title', 'heading', 'html_lang', 'direction', 'inputs', 'buttons',
          'table_rows', 'modals', 'reauthenticated', 'dev_overlay_visible', 'capture_date',
          'viewport', 'privacy_treatment', 'verification_level', 'intended_manual_section',
          'workflows', 'capture_error']
with io.open(os.path.join(OUT, 'screenshot-manifest.csv'), 'w', encoding='utf-8-sig', newline='') as fh:
    w = csv.DictWriter(fh, fieldnames=FIELDS)
    w.writeheader()
    w.writerows(rows)

# ---------------------------------------------------------------- 2. coverage and gaps
cap = collections.defaultdict(set)
for r in rows:
    if r['filename']:
        cap[r['screen_id']].add(r['language'])

cov = []
for s in screens:
    sid = s['screen_id']
    langs = cap.get(sid, set())
    unlabelled = sum(1 for f in s['fields'] if not f['label'])
    icon_only = sum(1 for a in s['actions']
                    if (a['label'] or {}).get('en_source') == 'icon-only')
    gaps = []
    if 'en' not in langs:
        gaps.append('no English screenshot')
    if 'ar' not in langs:
        gaps.append('no Arabic screenshot')
    if not s['heading']:
        gaps.append('no heading element — name it from the navigation')
    elif s['heading'].get('ar_source') == 'fallback-to-key':
        gaps.append('heading has NO ARABIC — the key is shown to the user')
    if unlabelled:
        gaps.append('%d field(s) with no caption' % unlabelled)
    if icon_only:
        gaps.append('%d icon-only action(s) — must be illustrated' % icon_only)
    if not s['has_matching_action'] and not s['rendered_by_actions']:
        gaps.append('no route found — framework view or orphan')
    cov.append({
        'screen_id': sid, 'module': s['module_folder'], 'route': s['route'],
        'title_en': (s['heading'] or {}).get('en', ''), 'title_ar': (s['heading'] or {}).get('ar', ''),
        'fields': len(s['fields']), 'actions': len(s['actions']), 'columns': len(s['columns']),
        'notices': len(s['notices']), 'modals': len(s['modals']),
        'code_evidence': 'repository-verified',
        'runtime_evidence': ('runtime viewed' if langs else 'NOT viewed'),
        'screenshot_en': 'yes' if 'en' in langs else 'no',
        'screenshot_ar': 'yes' if 'ar' in langs else 'no',
        'actions_tested': 'none — no action was performed on any screen',
        'roles_tested': 'administrator only',
        'gaps': '; '.join(gaps) or 'none identified',
    })

with io.open(os.path.join(OUT, 'coverage-and-gaps.csv'), 'w', encoding='utf-8-sig', newline='') as fh:
    w = csv.DictWriter(fh, fieldnames=list(cov[0].keys()))
    w.writeheader()
    w.writerows(sorted(cov, key=lambda r: (r['module'], r['route'])))

# ---------------------------------------------------------------- 3. evidence register
EV = [
    ('baseline', 'branch / HEAD / date', 'git log -1 --format="%H|%ad|%s"; git rev-parse --abbrev-ref HEAD',
     'branch reporting/studio-gap-closure; HEAD moved 3x during discovery (2020ee5a -> ccc6a349 -> 13c8d984)',
     'repository-verified'),
    ('baseline', 'working tree state', 'git status --porcelain | wc -l', '73-78 uncommitted entries throughout',
     'repository-verified'),
    ('baseline', 'application version', 'grep Version/Company/Authors/Copyright CrossBuy.csproj',
     'NONE DECLARED — no version, product, company, author or copyright anywhere', 'repository-verified'),
    ('baseline', 'runtime used', 'netstat; curl https://localhost:44368/Account/Login',
     "the owner's IIS Express instance, started 10:32, Development, database CrossBuyDev", 'runtime viewed'),
    ('baseline', 'database identity', 'sqlcmd -Q "SELECT @@SERVERNAME, DB_NAME()"',
     'DESKTOP-K8SA5SV | CrossBuyDev — production never contacted', 'runtime viewed'),
    ('owner', 'git author', 'git log --format="%an <%ae>" | sort | uniq -c',
     'Ahmed Zakarya — 235 of 247 commits', 'repository-verified'),
    ('owner', 'name in docs', 'grep -rlni ahmed docs/',
     'CrossBuy_System_Context_for_AI.md:194 "= employee \\"Ahmed\\", company head"; ADR-032:91 employee:12',
     'repository-verified'),
    ('owner', 'employee 12 does not exist', 'SELECT ... FROM Employee WHERE ID IN (5,12)',
     "no row for 12 — the ADR's employee:12 is an illustrative example", 'runtime viewed'),
    ('owner', 'owner employee record', 'SELECT ID, FullName, FullNameEn, ProfileImage FROM Employee WHERE ID=5',
     'FullNameEn "ahmed zakareya"; ProfileImage /uploads/employees/d5af6445-….png', 'runtime viewed'),
    ('owner', 'Arabic name, decoded', 'SELECT UNICODE(SUBSTRING(FullName,n,1)) …',
     '1575,1581,1605,1583,32,1586,1603,1585,1610,1575 -> احمد زكريا (clean, no mojibake)', 'runtime viewed'),
    ('owner', 'portrait', 'PNG header parse of d5af6445-….png',
     '1254x1254, 1,638,991 bytes, 8-bit truecolour; the only unique image among 17', 'repository-verified'),
    ('owner', 'photo hashing', 'sha256 over uploads/employees',
     'REFUSED by the environment data-handling policy; duplicate grouping rests on file sizes',
     'blocked'),
    ('screens', 'inventory', 'scripts/extract_screens.py',
     '309 screens, 63 partials, 1305 fields, 1475 actions, 1370 columns, 94 modals, 4333 keys',
     'repository-verified'),
    ('screens', 'orphan resolution', 'RENDERED_BY over View("…") and View("~/Views/…")',
     '15 views opened by a differently-named action; DocumentDetails serves 8 document types',
     'repository-verified'),
    ('access', 'roles and routes', 'scripts/extract_access.py',
     '11 platform scopes, 14 vocabularies, 1220 routable actions, 17 anonymous', 'repository-verified'),
    ('access', 'roles in the database',
     'SELECT Name FROM AspNetRoles; role counts from the four module role tables',
     'SuperAdmin/Administrator/Admin/Auditor/PlatformOps; Inventory 1, Accounting 1, CRM 5; '
     'PlatformRoleAssignments = 0 ROWS', 'runtime viewed'),
    ('reports', 'catalogue', 'scripts/extract_reports_and_messages.py',
     '41 report codes, 305 bilingual columns, 5 output formats, 7 permission keys', 'repository-verified'),
    ('messages', 'catalogue', 'same script',
     '223 messages: 80 business-rule refusals, 50 validation, 35 failure, 32 empty state, '
     '23 confirmation prompts, 3 permission refusals', 'repository-verified'),
    ('workflows', 'catalogue', 'scripts/build_workflow_catalog.py',
     '18 workflows, 93 steps, every route validated against the screen catalogue (4 invented '
     'routes were caught and corrected)', 'repository-verified'),
    ('workflows', 'command vs form', 'GET/POST pairing per controller',
     '15 form-then-save pairs against 390 command-only POST actions — the product is AJAX-driven',
     'repository-verified'),
    ('workflows', 'status vocabulary', 'status strings assigned/compared in BL and Controllers',
     '52 distinct values; "Void" AND "Voided" both assigned', 'repository-verified'),
    ('runtime', 'sign-in and landing', 'capture script',
     'sign-in lands on /Portal/Choose, not a dashboard', 'behaviour tested safely'),
    ('runtime', 'culture switch', 'capture script, Arabic pass',
     'html lang=ar dir=rtl, style.bundle.rtl.css served, Cairo confirmed loaded', 'behaviour tested safely'),
    ('runtime', 'refusal', 'capture of /Account/AccessDenied', 'HTTP 403, screen captured',
     'behaviour tested safely'),
    ('runtime', 'routing', 'capture of /Inventory, /Accounting, /Crm, /Admin',
     '404 — the default route has no action=Index; only 7 controllers have a bare-name route',
     'behaviour tested safely'),
    ('runtime', 'slow screens', 'capture timeouts',
     '/Accounting/Payroll and /Accounting/PayrollDisbursement exceeded 45s', 'runtime measured'),
    ('runtime', 'session eviction', 'capture logs across slices',
     'the long-running instance evicts its in-memory session under memory pressure (>1.1GB); '
     'SessionValidation then redirects to sign-in mid-navigation', 'runtime measured'),
    ('runtime', 'second instance', 'dotnet run --launch-profile http',
     'BUILD FAILED MSB3027 — the running instance holds bin/Debug/net8.0/CrossBuy.exe', 'blocked'),
    ('not-verified', 'actions', '—', 'ZERO of 1475 actions were performed', 'not verified'),
    ('not-verified', 'roles', '—', 'only the administrator session was used', 'not verified'),
    ('not-verified', 'mobile client', '—', 'crossbuy_mobile not examined', 'not verified'),
    ('not-verified', 'AI service', '—', 'crossbuy_ai read, not started; no AI call made', 'not verified'),
    ('not-verified', 'reports run', '—', 'no report executed, exported or printed', 'not verified'),
    ('not-verified', 'production', '—', 'never contacted', 'not verified'),
]
with io.open(os.path.join(OUT, 'evidence-register.csv'), 'w', encoding='utf-8-sig', newline='') as fh:
    w = csv.writer(fh)
    w.writerow(['area', 'claim', 'command_or_method', 'result', 'evidence_level'])
    w.writerows(EV)

en = sum(1 for r in rows if r['language'] == 'en' and r['filename'])
ar = sum(1 for r in rows if r['language'] == 'ar' and r['filename'])
print('manifest rows        :', len(rows))
print('english screenshots  :', en)
print('arabic screenshots   :', ar)
print('privacy-flagged      :', sum(1 for r in rows if 'REVIEW BEFORE' in r['privacy_treatment']))
print('dev overlay visible  :', sum(1 for r in rows if r['dev_overlay_visible'] == 'yes'))
print('unmatched PNGs       :', sum(1 for r in rows if r['screen_id'] == '(unmatched)'))
print('coverage rows        :', len(cov))
print('screens with BOTH    :', sum(1 for c in cov if c['screenshot_en'] == 'yes' and c['screenshot_ar'] == 'yes'))
print('screens with NEITHER :', sum(1 for c in cov if c['screenshot_en'] == 'no' and c['screenshot_ar'] == 'no'))
print('evidence rows        :', len(EV))
