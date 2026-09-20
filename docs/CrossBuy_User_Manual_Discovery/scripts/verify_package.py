# -*- coding: utf-8 -*-
"""Check that every file the documents reference actually exists, and print the handoff numbers.

The brief requires the package be checked before it ships. This resolves every relative link in
every .md, confirms the machine-readable files are present and parseable, and prints the counts
that the final report quotes — so the report is generated from the package rather than from
memory.
"""
import io, os, re, csv, json, collections

OUT = r'C:\CrossBuy\CrossBuy\docs\user-manual-discovery'

problems = []

# ---------------------------------------------------------------- 1. links inside the documents
LINK = re.compile(r'\[([^\]]+)\]\(([^)]+)\)')
BACKTICK = re.compile(r'`([A-Za-z0-9_\-/.]+\.(?:json|csv|md|py|mjs|png|ttf|txt))`')

md_files = sorted(f for f in os.listdir(OUT) if f.endswith('.md'))
checked = 0
for fn in md_files:
    t = io.open(os.path.join(OUT, fn), encoding='utf-8').read()
    targets = set()
    for _label, href in LINK.findall(t):
        if href.startswith(('http:', 'https:', '#', 'mailto:')):
            continue
        targets.add(href.split('#')[0])
    for name in BACKTICK.findall(t):
        # only treat it as a package file if it has no directory part or names one of ours
        if '/' in name and not name.startswith(('assets/', 'screenshots/', 'scripts/')):
            continue
        targets.add(name)
    for target in sorted(targets):
        if not target or ':' in target:
            continue
        # A bare file name in backticks is a package file wherever it lives — the brand marks are
        # named as `crossbuy-logo-full.png` in prose but stored under assets/brand/. Resolving
        # only against the package root reported fifteen real files as missing.
        checked += 1
        if os.path.exists(os.path.join(OUT, target)):
            continue
        if '/' not in target and any(
                target in files for _dp, _dn, files in os.walk(OUT)):
            continue
        # Names that belong to the APPLICATION, not to this package, are out of scope here.
        if target in ('appsettings.json',):
            continue
        problems.append('%s references a missing file: %s' % (fn, target))

# ---------------------------------------------------------------- 2. the machine-readable set
REQUIRED = [
    'README.md', 'screen-catalog.json', 'field-action-catalog.json', 'workflow-catalog.json',
    'role-permission-matrix.csv', 'screenshot-manifest.csv', 'bilingual-glossary.csv',
    'evidence-register.csv', 'coverage-and-gaps.csv', 'report-catalog.json',
    'messages-catalog.csv', 'route-protection.csv', 'partials-catalog.json',
]
for r in REQUIRED:
    p = os.path.join(OUT, r)
    if not os.path.exists(p):
        problems.append('REQUIRED FILE MISSING: ' + r)
        continue
    if r.endswith('.json'):
        try:
            json.load(io.open(p, encoding='utf-8'))
        except ValueError as e:
            problems.append('%s is not valid JSON: %s' % (r, e))
    if r.endswith('.csv'):
        try:
            rows = list(csv.DictReader(io.open(p, encoding='utf-8-sig')))
            if not rows:
                problems.append('%s has no data rows' % r)
        except (OSError, csv.Error) as e:
            problems.append('%s could not be read: %s' % (r, e))

for d in ('assets/owner', 'assets/brand', 'assets/fonts', 'screenshots', 'scripts'):
    if not os.path.isdir(os.path.join(OUT, d)):
        problems.append('MISSING FOLDER: ' + d)

# ---------------------------------------------------------------- 3. workflow cross-references
wf = json.load(io.open(os.path.join(OUT, 'workflow-catalog.json'), encoding='utf-8'))
screens = json.load(io.open(os.path.join(OUT, 'screen-catalog.json'), encoding='utf-8'))
ids = {s['screen_id'] for s in screens['screens']}
bad = [(w['id'], st['route']) for w in wf['workflows'] for st in w['steps']
       if st['screen_id'] not in ids]
for wid, route in bad:
    problems.append('workflow %s step route %s has no screen' % (wid, route))

# ---------------------------------------------------------------- 4. the numbers
man = list(csv.DictReader(io.open(os.path.join(OUT, 'screenshot-manifest.csv'), encoding='utf-8-sig')))
cov = list(csv.DictReader(io.open(os.path.join(OUT, 'coverage-and-gaps.csv'), encoding='utf-8-sig')))
fa = json.load(io.open(os.path.join(OUT, 'field-action-catalog.json'), encoding='utf-8'))['entries']
roles = list(csv.DictReader(io.open(os.path.join(OUT, 'role-permission-matrix.csv'), encoding='utf-8-sig')))
gloss = list(csv.DictReader(io.open(os.path.join(OUT, 'bilingual-glossary.csv'), encoding='utf-8-sig')))
msgs = list(csv.DictReader(io.open(os.path.join(OUT, 'messages-catalog.csv'), encoding='utf-8-sig')))
rep = json.load(io.open(os.path.join(OUT, 'report-catalog.json'), encoding='utf-8'))

png = [f for f in os.listdir(os.path.join(OUT, 'screenshots')) if f.endswith('.png')]
en = sum(1 for f in png if f.endswith('.en.png'))
ar = sum(1 for f in png if f.endswith('.ar.png'))

def size_of(d):
    tot = 0
    for dp, _dn, fns in os.walk(os.path.join(OUT, d)):
        for f in fns:
            tot += os.path.getsize(os.path.join(dp, f))
    return tot

print('=' * 66)
print('PACKAGE VERIFICATION')
print('=' * 66)
print('markdown documents        :', len(md_files))
print('file references checked   :', checked)
print('machine-readable files    :', len(REQUIRED), 'required, all parse' if not any(
    p.startswith(('REQUIRED', 'workflow')) or 'JSON' in p for p in problems) else 'SEE PROBLEMS')
print()
print('modules documented        :', len({c["module"] for c in cov}))
print('screens catalogued        :', len(cov))
print('  with an English capture :', sum(1 for c in cov if c['screenshot_en'] == 'yes'))
print('  with an Arabic capture  :', sum(1 for c in cov if c['screenshot_ar'] == 'yes'))
print('  with BOTH               :', sum(1 for c in cov if c['screenshot_en'] == 'yes' and c['screenshot_ar'] == 'yes'))
print('  with NEITHER            :', sum(1 for c in cov if c['screenshot_en'] == 'no' and c['screenshot_ar'] == 'no'))
print('screenshots on disk       :', len(png), '(%d en, %d ar)' % (en, ar))
print('manifest rows             :', len(man))
print('  flagged for review      :', sum(1 for r in man if 'REVIEW BEFORE' in r['privacy_treatment']))
print('  dev overlay visible     :', sum(1 for r in man if r['dev_overlay_visible'] == 'yes'))
print()
print('workflows                 :', wf['workflow_count'], '/', wf['step_count'], 'steps')
print('  steps resolved to screen:', wf['validation']['steps_with_a_known_screen'])
ev = collections.Counter(st['evidence'].split('(')[0].strip() for w in wf['workflows'] for st in w['steps'])
for k, v in ev.most_common():
    print('    %-52s %d' % (k[:52], v))
print()
print('fields + actions          :', len(fa),
      '(%d fields, %d actions)' % (sum(1 for x in fa if x['kind'] == 'field'),
                                   sum(1 for x in fa if x['kind'] == 'action')))
print('role/permission rows      :', len(roles),
      '(%d roles, %d actions)' % (sum(1 for r in roles if r['kind'] == 'role'),
                                  sum(1 for r in roles if r['kind'] == 'action')))
print('glossary keys             :', len(gloss))
print('  translated in all three :', sum(1 for g in gloss if g['translation_status'].startswith('translated')))
print('  missing a culture       :', sum(1 for g in gloss if 'MISSING' in g['translation_status']))
print('messages                  :', len(msgs))
print('report codes / columns    :', len(rep['report_codes']), '/',
      sum(d['field_count'] for d in rep['datasets']))
print()
print('assets/owner              :', '%.1f MB' % (size_of('assets/owner') / 1e6))
print('assets/brand              :', '%.1f MB' % (size_of('assets/brand') / 1e6))
print('assets/fonts              :', '%.1f MB' % (size_of('assets/fonts') / 1e6))
print('screenshots               :', '%.1f MB' % (size_of('screenshots') / 1e6))
print('scripts                   :', len(os.listdir(os.path.join(OUT, 'scripts'))), 'files')
print()
if problems:
    print('PROBLEMS (%d):' % len(problems))
    for p in problems:
        print('  !!', p)
else:
    print('No problems found: every referenced file exists and every catalogue parses.')
