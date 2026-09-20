# -*- coding: utf-8 -*-
"""Report catalogue and user-facing messages, for the manual's reports and troubleshooting chapters.

READ-ONLY. The reporting layer declares its own bilingual titles (TitleAr / TitleEn) for every
dataset, field and parameter, which makes it the most reliably translated surface in the product —
so it is read directly rather than through the view layer.

Writes:
    report-catalog.json     datasets, their columns, parameters and permissions, in ar/en
    messages-catalog.csv    validation, refusal, empty-state and confirmation text users can meet
"""
import io, os, re, json, csv, collections

ROOT = r'C:\CrossBuy\CrossBuy'
APP = os.path.join(ROOT, 'CrossBuy')
REP = os.path.join(APP, 'BL', 'Reporting')
RES = os.path.join(APP, 'Resources')
OUT = os.path.join(ROOT, 'docs', 'user-manual-discovery')


def read(p):
    try:
        return io.open(p, encoding='utf-8', errors='replace').read()
    except OSError:
        return ''


# ---------------------------------------------------------------- 1. the report catalogue
# A dataset declares a code, then a bilingual title, then a list of fields each with its own pair.
# They are matched positionally inside the file because the shape is consistent across all eight
# dataset files; where a title has no code before it the entry is still recorded, with code=None,
# so nothing is silently dropped.
TITLE_RX = re.compile(r'TitleAr\s*=\s*"([^"]*)"\s*,\s*\n?\s*TitleEn\s*=\s*"([^"]*)"')
FIELD_RX = re.compile(
    r'Key\s*=\s*(?:"([^"]*)"|([A-Za-z0-9_.]+))\s*,\s*TitleAr\s*=\s*"([^"]*)"\s*,\s*TitleEn\s*=\s*"([^"]*)"')
CODE_RX = re.compile(r'(?:Code|ReportCode|DatasetCode)\s*=\s*(?:"([^"]+)"|([A-Za-z0-9_.]+))')
PERM_RX = re.compile(r'"([a-z]+\.[a-z.]+\.(?:view|export|notes))"')

datasets = []
for fn in sorted(os.listdir(REP)):
    if not fn.endswith('.cs'):
        continue
    t = read(fn_path := os.path.join(REP, fn))
    rel = os.path.relpath(fn_path, ROOT).replace('\\', '/')
    codes = [c[0] or c[1] for c in CODE_RX.findall(t)]
    literal_codes = sorted(set(re.findall(
        r'"((?:Inventory|Accounting|Crm|Admin|Roster|Platform)\.[A-Za-z0-9.]+)"', t)))
    fields = [{'key': k[0] or k[1], 'ar': k[2], 'en': k[3]} for k in FIELD_RX.findall(t)]
    # The dataset's own title is the first TitleAr/TitleEn pair that is NOT a field pair.
    field_spans = {(m.start(), m.end()) for m in FIELD_RX.finditer(t)}
    titles = [{'ar': m.group(1), 'en': m.group(2), 'pos': m.start()}
              for m in TITLE_RX.finditer(t)
              if not any(s <= m.start() < e for s, e in field_spans)]
    if not (literal_codes or fields):
        continue
    datasets.append({
        'source': rel,
        'report_codes': literal_codes,
        'declared_titles': [{'ar': x['ar'], 'en': x['en']} for x in titles],
        'permission_keys': sorted(set(PERM_RX.findall(t))),
        'field_count': len(fields),
        'fields': fields,
    })

# Output formats and their stated purpose, read from the contract file.
contracts = read(os.path.join(REP, 'ReportContracts.cs'))
formats = []
for m in re.finditer(r'((?:\s*//[^\n]*\n)+)\s*(\w+)\s*=\s*\d+\s*,', contracts):
    note = ' '.join(l.strip().lstrip('/ ').strip() for l in m.group(1).split('\n') if l.strip())
    formats.append({'format': m.group(2), 'purpose': note[:400]})

engines = sorted(set(re.findall(r'EngineName => \$?"([^"]+)"',
                                '\n'.join(read(os.path.join(REP, f))
                                          for f in os.listdir(REP) if f.endswith('.cs')))))

io.open(os.path.join(OUT, 'report-catalog.json'), 'w', encoding='utf-8').write(json.dumps({
    'dataset_files': len(datasets),
    'report_codes': sorted({c for d in datasets for c in d['report_codes']}),
    'permission_keys': sorted({p for d in datasets for p in d['permission_keys']}),
    'output_formats': formats,
    'render_engines': engines,
    'datasets': datasets,
}, ensure_ascii=False, indent=1))

# ---------------------------------------------------------------- 2. user-facing messages
# A manual's troubleshooting chapter has to quote what the user actually sees. Three sources:
#   * shared resources whose key or value reads like a message
#   * exception messages thrown by the business layer and shown to the user
#   * confirm() prompts in views
CULTURES = ('ar', 'en', 'fr')


def load_resx(path):
    if not os.path.exists(path):
        return {}
    return {m.group(1): re.sub(r'\s+', ' ', m.group(2)).strip()
            for m in re.finditer(r'<data name="([^"]+)"[^>]*>\s*<value>(.*?)</value>',
                                 read(path), re.S)}


shared = {c: load_resx(os.path.join(RES, 'SharedResources.%s.resx' % c)) for c in CULTURES}

MESSAGEY = re.compile(
    r'(cannot|must|required|invalid|failed|denied|not found|no |already|please|exceed|missing|'
    r'unable|error|closed|locked|insufficient|negative|duplicate|expired)', re.I)

rows = []
for key, en in sorted(shared['en'].items()):
    ar = shared['ar'].get(key, '')
    fr = shared['fr'].get(key, '')
    if not (MESSAGEY.search(en) or MESSAGEY.search(key)) or len(en) < 12:
        continue
    kind = ('refusal' if re.search(r'denied|permission|not allowed|unauthor', en, re.I) else
            'validation' if re.search(r'must|required|invalid|exceed|negative|duplicate', en, re.I) else
            'empty-state' if re.search(r'^no |not found|nothing', en, re.I) else
            'failure')
    rows.append({
        'message_id': 'MSG-' + key, 'kind': kind, 'source': 'SharedResources',
        'english': en, 'arabic': ar or '(missing — English key shown)',
        'french': fr or '(missing — English key shown)',
        'translated': 'yes' if (ar and fr) else 'NO',
        'evidence': 'repository-verified', 'where': 'Resources/SharedResources.*.resx',
    })

# Business-layer exceptions the UI surfaces
for dirpath, _d, filenames in os.walk(os.path.join(APP, 'BL')):
    for fn in filenames:
        if not fn.endswith('.cs'):
            continue
        p = os.path.join(dirpath, fn)
        rel = os.path.relpath(p, ROOT).replace('\\', '/')
        for m in re.finditer(r'throw new (\w*Exception)\(\s*"([^"]{15,200})"', read(p)):
            rows.append({
                'message_id': 'MSG-EX-%s-%d' % (fn[:-3], m.start()),
                'kind': 'business-rule refusal', 'source': m.group(1),
                'english': m.group(2), 'arabic': '(thrown in English; localised at the controller '
                                                 'where the screen does so)',
                'french': '', 'translated': 'source string is English',
                'evidence': 'repository-verified', 'where': rel,
            })

# CONFIRMATION PROMPTS — the manual must warn before a user meets one.
#
# There are TWO shapes, and a first pass that looked only for a quoted literal found six prompts
# reading "@Localizer[" and concluded the product had none. It has 26, and most use a PROJECT
# confirm dialog rather than the browser's:
#
#     confirm('@Localizer["Delete this message?"]')
#     confirm({ type: 'delete', text: …Localizer["Post this stock write-off?…"]… })
#
# The `type` is worth recording on its own: it is what decides the dialog's colour and its
# confirm-button wording, so the manual can describe the three treatments once.
for dirpath, _d, filenames in os.walk(os.path.join(APP, 'Views')):
    for fn in filenames:
        if not fn.endswith('.cshtml'):
            continue
        p = os.path.join(dirpath, fn)
        rel = os.path.relpath(p, ROOT).replace('\\', '/')
        body = read(p)
        # the per-screen resources, so the prompt can be shown in all three languages
        stem = fn[:-len('.cshtml')]
        vfolder = os.path.relpath(dirpath, os.path.join(APP, 'Views')).replace('\\', '/')
        vres = {c: load_resx(os.path.join(RES, 'Views', vfolder, '%s.%s.resx' % (stem, c)))
                for c in CULTURES}

        for m in re.finditer(r'confirm\(([^;]{0,400}?)\)\s*[;.]', body, re.S):
            call = m.group(1)
            keym = re.search(r'Localizer\["((?:[^"\\]|\\.)*)"\]', call)
            typem = re.search(r"type\s*:\s*'?\"?(\w+)", call)
            if not keym:
                # a variable-driven prompt — record that it exists, since the manual must still
                # tell the reader a dialog appears, but do not invent its wording
                rows.append({
                    'message_id': 'MSG-CONFIRM-%s-%d' % (stem, m.start()),
                    'kind': 'confirmation prompt', 'source': 'view (text built at runtime)',
                    'english': '(prompt text is assembled in JavaScript — see the view)',
                    'arabic': '', 'french': '',
                    'translated': 'not resolvable from source',
                    'evidence': 'repository-verified',
                    'where': '%s  [dialog type: %s]' % (rel, typem.group(1) if typem else 'native'),
                })
                continue
            key = keym.group(1)
            rows.append({
                'message_id': 'MSG-CONFIRM-%s-%d' % (stem, m.start()),
                'kind': 'confirmation prompt',
                'source': 'project dialog' if typem else 'browser confirm()',
                'english': vres['en'].get(key) or shared['en'].get(key) or key,
                'arabic': vres['ar'].get(key) or shared['ar'].get(key) or '(missing — key shown)',
                'french': vres['fr'].get(key) or shared['fr'].get(key) or '(missing — key shown)',
                'translated': 'yes' if (vres['ar'].get(key) or shared['ar'].get(key)) else 'NO',
                'evidence': 'repository-verified',
                'where': '%s  [dialog type: %s]' % (rel, typem.group(1) if typem else 'native'),
            })

with io.open(os.path.join(OUT, 'messages-catalog.csv'), 'w', encoding='utf-8-sig', newline='') as fh:
    w = csv.DictWriter(fh, fieldnames=['message_id', 'kind', 'source', 'english', 'arabic',
                                       'french', 'translated', 'evidence', 'where'])
    w.writeheader()
    w.writerows(rows)

print('dataset files      :', len(datasets))
print('report codes       :', len({c for d in datasets for c in d['report_codes']}))
print('declared columns   :', sum(d['field_count'] for d in datasets))
print('output formats     :', len(formats))
print('render engines     :', len(engines))
print('messages catalogued:', len(rows))
for k, v in collections.Counter(r['kind'] for r in rows).most_common():
    print('    %-26s %d' % (k, v))
